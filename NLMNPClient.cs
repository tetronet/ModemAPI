using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Timers;

namespace ModemAPI
{
    /// <summary>
    /// New Large Message Notification Protocol (NLMNP) Client.
    /// </summary>
    public class NLMNPClient
    {
        /// <summary>
        /// ConnectionID of every NLMNP packet.
        /// </summary>
        private const uint NotificationConnectionID = 4000000015;
        /// <summary>
        /// A repeated notification (its acknowledgement got lost) is acknowledged again, but not reported again, within this time.
        /// </summary>
        private static readonly TimeSpan RepeatedNotificationWindow = TimeSpan.FromMinutes(1);
        /// <summary>
        /// The most notifications remembered for telling repeats apart; the oldest ones are forgotten first.
        /// </summary>
        private const int MaxRememberedNotifications = 4096;
        private IModem Modem;
        private bool IsListening = false;
        private volatile bool IsClosed = false;
        // notifications reported lately: sender address and contents -> Stopwatch timestamp
        private readonly ConcurrentDictionary<(string, string), long> RecentNotifications = new();
        private event Action<Address, string> ReceivedLMAck = delegate { };
        private Action<Packet, Action>? NotificationHandler = null;
        /// <summary>
        /// Gets invoked once for every notification. A sender repeats a notification until it sees the acknowledgement, so the same
        /// notification (same sender, large message and server) coming again within a minute is acknowledged, but not reported again:
        /// a sender, that wants to notify about the same large message again (for example it changed), should wait a minute or use another name.
        /// The handler gets the: notification sender's <see cref="Address"/>, the server's address <see cref="Address"/> which has the large message available to obtain and remote resource name, that you will want to obtain using LMDTP or other protocols.
        /// </summary>
        public event Action<Address, Address, string> OnNotificationReceived = delegate { };
        /// <summary>
        /// Gets invoked each time an error happens in this instance of <see cref="NLMNPClient"/>
        /// </summary>
        public event Action<Exception> OnErrorReceived = delegate { };
        /// <summary>
        /// How long to wait until an acknowlegment for the notification comes, in milliseconds.
        /// </summary>
        public int TimeoutMs = 30000;
        /// <summary>
        /// How often the notification is sent again while no acknowledgement comes, in milliseconds.
        /// </summary>
        public int ResendIntervalMs = 1000;
        /// <summary>
        /// Create a new instance of NLMNP Client.
        /// </summary>
        /// <param name="modem">Modem, that will be used for notifications</param>
        public NLMNPClient(IModem modem)
        {
            Modem = modem;
            Modem.AttachReceiveEventNoUnfragment(OnAckPacket);
        }
        private void OnAckPacket(Packet packet, Action obsolete)
        {
            if (packet.ConnectionID == NotificationConnectionID && packet.QueryType == "lm_ack_notify")
            {
                ReceivedLMAck(packet.Transmitter, Encoding.UTF8.GetString(packet.DataBytes));
            }
        }
        /// <summary>
        /// Makes this <see cref="NLMNPClient"/> to listen to incoming large message notifications.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public void Listen()
        {
            if (IsClosed)
            {
                throw new OperationCanceledException("This client is closed.");
            }
            if (!IsListening)
            {
                NotificationHandler = delegate (Packet packet, Action obsolete)
                {
                    try
                    {
                        if (IsClosed)
                        {
                            return;
                        }
                        if (packet.ConnectionID == NotificationConnectionID && packet.QueryType == "lm_notify")
                        {
                            // the notification is checked before it gets acknowledged: a broken one is not accepted
                            string contents = Encoding.UTF8.GetString(packet.DataBytes);
                            string[] data = contents.Split('@');
                            if (data.Length != 2 || data[0].Contains('\n') || !Address.Validate(new Address(data[1])))
                            {
                                OnErrorReceived(new InvalidDataException($"Notification sent by {packet.Transmitter} contains invalid data."));
                                return;
                            }
                            Address serverWithLargeMessage = new(data[1]);
                            string largeMessageName = data[0];
                            Modem.LowLevelTransmit(Encoding.UTF8.GetBytes($"notification_received\n{largeMessageName}"), packet.Transmitter, "lm_ack_notify", NotificationConnectionID);
                            // the sender repeats a notification until it sees the acknowledgement, so the same one may come again
                            if (IsRepeated(packet.Transmitter, contents))
                            {
                                return;
                            }
                            OnNotificationReceived(packet.Transmitter, serverWithLargeMessage, largeMessageName);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnErrorReceived(ex);
                    }
                };
                Modem.AttachReceiveEventNoUnfragment(NotificationHandler);
            }
            else
            {
                throw new InvalidOperationException("This NLMNP client is already listening for incoming notifications");
            }
            IsListening = true;
        }
        /// <summary>
        /// Sends a notification to a remote computer that there's a large message available to download from a specific LMXS (Large Message eXchange Server).
        /// </summary>
        /// <param name="notificationDest">The address of the notification's receiver</param>
        /// <param name="at">The address of the LMXS where the large message is located and ready for obtain</param>
        /// <param name="lmName">The name of the large message which can be obtained from the specified LMXS</param>
        /// <exception cref="WrongCharacherException">lmName contains '@' or a line break</exception>
        /// <exception cref="TimeoutException">No acknowledgement came in <see cref="TimeoutMs"/>; the notification was sent every <see cref="ResendIntervalMs"/></exception>
        public void Notify(Address notificationDest, Address at, string lmName)
        {
            if (IsClosed)
            {
                throw new OperationCanceledException("This client is closed.");
            }
            if (lmName.Contains('@'))
            {
                throw new WrongCharacherException("lmName must not contain @, since this character is used by the system");
            }
            if (lmName.Contains('\n'))
            {
                throw new WrongCharacherException("lmName must not contain line breaks, since the acknowledgement uses them");
            }
            if (!Address.Validate(at))
            {
                throw new ArgumentException("Invalid address", nameof(at));
            }
            // not disposed: an acknowledgement may still be handled on another thread after Notify returned
            ManualResetEventSlim acknowledged = new(false);
            void nevent(Address transmitter, string datautf8)
            {
                try
                {
                    if (!datautf8.Contains('\n'))
                    {
                        OnErrorReceived(new InvalidDataException($"Notification sent by {transmitter} contains invalid data."));
                        return;
                    }
                    if (datautf8.StartsWith("notification_received") &&
                        transmitter.Equals(notificationDest) &&
                        datautf8.Split('\n')[1] == lmName)
                    {
                        acknowledged.Set();
                    }
                }
                catch (Exception ex)
                {
                    OnErrorReceived(ex);
                }
            }
            ReceivedLMAck += nevent;
            try
            {
                byte[] notification = Encoding.UTF8.GetBytes($"{lmName}@{at}");
                Stopwatch timeoutSw = Stopwatch.StartNew();
                // sent again until acknowledged: a single lost packet must not make the whole notification fail
                do
                {
                    Modem.LowLevelTransmit(notification, notificationDest, "lm_notify", NotificationConnectionID);
                    long left = TimeoutMs - timeoutSw.ElapsedMilliseconds;
                    if (left <= 0)
                    {
                        break;
                    }
                    if (acknowledged.Wait((int)Math.Min(left, Math.Max(1, ResendIntervalMs))))
                    {
                        return;
                    }
                }
                while (!IsClosed && timeoutSw.ElapsedMilliseconds < TimeoutMs);
                if (acknowledged.IsSet)
                {
                    return;
                }
                if (IsClosed)
                {
                    throw new OperationCanceledException("This client was closed while notifying.");
                }
                throw new TimeoutException("Notification of the remote side has timed out.");
            }
            finally
            {
                ReceivedLMAck -= nevent;
            }
        }
        private bool IsRepeated(Address transmitter, string contents)
        {
            long now = Stopwatch.GetTimestamp();
            (string, string) key = (transmitter.AddressValue ?? "", contents);
            if (RecentNotifications.TryGetValue(key, out long seen) && Stopwatch.GetElapsedTime(seen, now) <= RepeatedNotificationWindow)
            {
                return true;
            }
            RecentNotifications[key] = now;
            // cleaned only when it gets big, not on every notification
            if (RecentNotifications.Count > MaxRememberedNotifications)
            {
                foreach (KeyValuePair<(string, string), long> old in RecentNotifications)
                {
                    if (Stopwatch.GetElapsedTime(old.Value, now) > RepeatedNotificationWindow)
                    {
                        RecentNotifications.TryRemove(old);
                    }
                }
                // still too many fresh ones: the oldest are forgotten
                foreach (KeyValuePair<(string, string), long> old in RecentNotifications.OrderBy(kv => kv.Value).Take(RecentNotifications.Count - MaxRememberedNotifications))
                {
                    RecentNotifications.TryRemove(old);
                }
            }
            return false;
        }
        /// <summary>
        /// Closes this client. Closing it again does nothing.
        /// </summary>
        public void Close()
        {
            if (IsClosed)
            {
                // closing again does nothing
                return;
            }
            IsClosed = true;
            Modem.DetachReceiveEventNoUnfragment(OnAckPacket);
            if (NotificationHandler != null)
            {
                Modem.DetachReceiveEventNoUnfragment(NotificationHandler);
            }
            ReceivedLMAck = delegate { };
            OnNotificationReceived = delegate { };
            OnErrorReceived = delegate { };
        }
    }
}
