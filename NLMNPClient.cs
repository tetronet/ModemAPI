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
        private IModem Modem;
        private bool IsListening = false;
        private bool IsClosed = false;
        private event Action<Address, string> ReceivedLMAck = delegate { };
        /// <summary>
        /// Gets invoked each time a notification for downloading a remote large message comes, and you will get the: notification sender's <see cref="Address"/>, the server's address <see cref="Address"/> which has the large message available to obtain and remote resource name, that you will want to obtain using LMDTP or other protocols.
        /// </summary>
        public event Action<Address, Address, string> OnNotificationReceived = delegate { };
        /// <summary>
        /// Gets invoked each time some shit happens in this instance of <see cref="NLMNPClient"/>
        /// </summary>
        public event Action<Exception> OnErrorReceived = delegate { };
        /// <summary>
        /// How long to wait until an acknowlegment for the notification comes, in milliseconds.
        /// </summary>
        public int TimeoutMs = 30000;
        /// <summary>
        /// Create a new instance of NLMNP Client.
        /// </summary>
        /// <param name="modem">Modem, that will be used for notifications</param>
        public NLMNPClient(IModem modem)
        {
            Modem = modem;
            Modem.AttachReceiveEventNoUnfragment(delegate (Packet packet, Action obsolete)
            {
                if (packet.ConnectionID == 4000000015 && packet.QueryType == "lm_ack_notify")
                {
                    ReceivedLMAck(packet.Transmitter, Encoding.UTF8.GetString(packet.DataBytes));
                }
            });
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
                Modem.AttachReceiveEventNoUnfragment(delegate (Packet packet, Action obsolete)
                {
                    try
                    {
                        if (IsClosed)
                        {
                            return;
                        }
                        if (packet.ConnectionID == 4000000015 && packet.QueryType == "lm_notify")
                        {
                            string[] data = Encoding.UTF8.GetString(packet.DataBytes).Split('@');
                            Address serverWithLargeMessage = new(data[1]);
                            string largeMessageName = data[0];
                            Modem.LowLevelTransmit(Encoding.UTF8.GetBytes($"notification_received\n{largeMessageName}"), packet.Transmitter, "lm_ack_notify", 4000000015);
                            OnNotificationReceived(packet.Transmitter, serverWithLargeMessage, largeMessageName);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnErrorReceived(ex);
                    }
                });
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
        /// <exception cref="WrongCharacherException"></exception>
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
            if (!Address.Validate(at))
            {
                throw new ArgumentException("Invalid address", nameof(at));
            }
            bool spinWaitsForAck = true;
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
                        spinWaitsForAck = false;
                    }
                }
                catch (Exception ex)
                {
                    OnErrorReceived(ex);
                }
            }
            ReceivedLMAck += nevent;
            Modem.LowLevelTransmit(Encoding.UTF8.GetBytes($"{lmName}@{at}"), notificationDest, "lm_notify", 4000000015);
            Stopwatch timeoutSw = Stopwatch.StartNew();
            while (spinWaitsForAck)
            {
                if (timeoutSw.ElapsedMilliseconds > TimeoutMs)
                {
                    ReceivedLMAck -= nevent;
                    throw new TimeoutException("Notification of the remote side has timed out.");
                }
                Thread.Sleep(10);
            }
            ReceivedLMAck -= nevent;
        }
        /// <summary>
        /// Closes this client.
        /// </summary>
        public void Close()
        {
            if (IsClosed)
            {
                throw new OperationCanceledException("This client is closed.");
            }
            IsClosed = true;
            ReceivedLMAck = delegate { };
            OnNotificationReceived = delegate { };
            OnErrorReceived = delegate { };
        }
    }
}
