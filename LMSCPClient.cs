namespace ModemAPI
{
    /// <summary>
    /// Large Message Sending Control Protocol (LMSCP) Client
    /// for transmitting extremely large data over the Tetronet.
    /// </summary>
    public class LMSCPClient
    {
        private IModem modem;
        /// <summary>
        /// Create a new instance of LMSCP Client.
        /// </summary>
        /// <param name="mdm">Modem, that this client will use for transmission</param>
        LMSCPClient(IModem mdm)
        {
            modem = mdm;
        }
        /// <summary>
        /// Uses LMSCP to transmit a file from the filesystem using tetronet.
        /// </summary>
        /// <param name="file">File Stream, that will be used to read the file and upload it's parts to the Tetronet</param>
        /// <param name="receiver">Tetronet Address, that is supposed to receive the file</param>
        /// <param name="connectid">Connection ID ()</param>
        /// <param name="qtype"></param>
        /// <param name="lmscpConnectid"></param>
        /// <param name="blockSize"></param>
        /// <exception cref="NullAddressException">Modem does not have Tetronet Address</exception>
        public void LMSCPTransmitLargeMessage(FileStream file, Address receiver, uint connectid, string qtype, uint lmscpConnectid = 13011453, int blockSize = 16777216)
        {
            if (modem.LocalModemAddress == null)
            {
                throw new NullAddressException("given modem doesn't have a local address");
            }
            modem.Transmit("LMSCP=true", receiver, "page", lmscpConnectid);
            LMSCPState currentState = LMSCPState.LMSCPTerminalReady;
            modem.AttachReceiveEvent(async delegate (DataBlock received, Action endLoop)
            {
                if (received.QueryType == "page" && received.ConnectionID == lmscpConnectid)
                {
                    if (received.DataString == "ok" && currentState == LMSCPState.LMSCPTerminalReady)
                    {
                        currentState = LMSCPState.LMSCPConnectOk;
                    }
                    if (received.DataString == "ready" && currentState == LMSCPState.LMSCPConnectOk)
                    {
                        currentState = LMSCPState.Initializing;
                        modem.Transmit("size=" + file.Length, receiver, "page", lmscpConnectid);
                    }
                    if (received.DataString == "ok" && currentState == LMSCPState.Initializing)
                    {
                        for (ulong i = 0; i < (ulong)file.Length / (ulong)blockSize; i++)
                        {
                            modem.Transmit($"block_id={i + 1}/{(ulong)file.Length / (ulong)blockSize}", receiver, "page", lmscpConnectid);
                            byte[] buffer = [];
                            file.ReadExactly(buffer, 0, blockSize);
                            await modem.PushLargeMessage(buffer, receiver.AddressValue ?? throw new NullAddressException(), connectid, qtype);
                            currentState = LMSCPState.UploadingLM;
                        }
                        currentState = LMSCPState.LMSCPLargeMessagesTransmitted;
                    }
                    if (received.DataString == "data-accept" && currentState == LMSCPState.LMSCPLargeMessagesTransmitted)
                    {
                        modem.Transmit("ok", receiver, "page", lmscpConnectid);
                        currentState = LMSCPState.LMSCPTransmissionCompleted;
                    }
                    if (received.DataString == "LMSCP=false")
                    {
                        currentState = LMSCPState.None;
                    }
                }
                
            });
        }
        public void OnLMSCPConnection(Func<FileStream> onDataReceived)
        {
            
        }
    }
}
