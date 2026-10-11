using System.Text;

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
        public event Action<Address, string> OnNotificationReceived = delegate { };
        /// <summary>
        /// Create a new instance of NLMNP Client.
        /// </summary>
        /// <param name="modem">Modem, that will be used for notifications</param>
        public NLMNPClient(IModem modem)
        {
            Modem = modem;
        }
        /// <summary>
        /// Makes this <see cref="NLMNPClient"/> to listen to incoming large message notifications.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public void Listen()
        {
            if (!IsListening)
            {
                Modem.AttachReceiveEventNoUnfragment(delegate (Packet packet, Action obsolete)
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
                        OnNotificationReceived(serverWithLargeMessage, largeMessageName);
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
            if (lmName.Contains('@'))
            {
                throw new WrongCharacherException("lmName must not contain @, since this character is used by the system");
            }
            if (!Address.Validate(at))
            {
                throw new ArgumentException("Invalid address", nameof(at));
            }
            Modem.LowLevelTransmit(Encoding.UTF8.GetBytes($"{lmName}@{at}"), notificationDest, "lm_notify", 4000000015);
        }
    }
}
