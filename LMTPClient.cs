namespace ModemAPI
{
    /// <summary>
    /// Large Message Transfer Protocol (LMTP) Client
    /// for transmitting Large Messages and triggering
    /// the receiver for it to download uploaded data.
    /// </summary>
    public class LMTPClient
    {
        private IModem Modem;
        /// <summary>
        /// Create a new instance of LMTP Client.
        /// </summary>
        /// <param name="modem">Modem, that will be used to transmit Large Messages</param>
        public LMTPClient(IModem modem)
        {
            Modem = modem;
        }
        /// <summary>
        /// Asynchronously sends a Large Message using the standart LMTP.
        /// </summary>
        /// <param name="message">Large Message for transmitting in to the network</param>
        /// <returns>true if upload was successfully completed, otherwise - false</returns>
        public async Task<bool> SendLargeMessage(LargeMessage message)
        {
            try
            {
                bool isActive = true;
                Modem.AttachReceiveEvent(delegate (DataBlock r, Action k)
                {
                    if (isActive && r.ConnectionID == ConnectionIDDefaults.LARGE_MESSAGE_TRIGGER && r.QueryType == "lm_ack")
                    {
                        Modem.DeleteLargeMessage(message.Transmitter).Wait();
                        Thread.Sleep(100);
                        isActive = false;
                    }
                });
                int len = await Modem.PushLargeMessage(message.Data, message.Receiver.AddressValue ?? throw new NullAddressException("receiver address"), message.ConnectionID, message.QueryType, message.Metadata);
                Thread.Sleep(100);
                Modem.Transmit("largeMessageDataTransfer", message.Receiver, "largemessage", ConnectionIDDefaults.LARGE_MESSAGE_TRIGGER, len.ToString());
            }
            catch
            {
                return false;
            }
            return true;
        }

        public void WaitForLargeMessages(Action<LargeMessage> onLargeMessageFullyDownloaded)
        {
            Modem.AttachReceiveEvent(delegate (DataBlock r, Action k)
            {
                if (r.ConnectionID == ConnectionIDDefaults.LARGE_MESSAGE_TRIGGER && r.QueryType == "largemessage")
                {
                    Modem.GetLargeMessage(r.Transmitter).Wait();
                    if (Modem.LastDownloadedLargeMessage == null)
                    {
                        throw new NullReferenceException("large message null");
                    }
                    Modem.Transmit("largeMessageDataTransferWasSuccessfullyCompleted", Modem.LastDownloadedLargeMessage.Transmitter, "lm_ack", ConnectionIDDefaults.LARGE_MESSAGE_TRIGGER, "");
                    onLargeMessageFullyDownloaded(Modem.LastDownloadedLargeMessage);
                }
            });
        }
    }
}
