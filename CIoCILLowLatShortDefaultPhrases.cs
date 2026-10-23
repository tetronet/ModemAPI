namespace ModemAPI
{
    public class CIoCILLowLatShortDefaultPhrases
    {
        // === CONNECTION PHRASES ===
        /// <summary>
        /// Connection prefix for requesting a Tetronet Connection using LL-CIoCIL-mini.
        /// </summary>
        public static readonly byte[] InitConnectionPrefixRequest = "LL-CIoCIL-min"u8.ToArray();
        /// <summary>
        /// Connection prefix for response of the Address Machine, that will connect Low Latency Modem to the Tetronet.
        /// </summary>
        public static readonly byte[] InitConnectionPrefixResponse = "LL-CIoCIL-min"u8.ToArray();
        // === DISCONNECTION PHRASES ===
        /// <summary>
        /// Disconnection request for the Low Latency Connection.
        /// </summary>
        public static readonly byte[] DisconnectionRequest = "LLcntbrk"u8.ToArray();
        /// <summary>
        /// Disconnection response when the Remote Address Machine accepts disconnection for the Client's Modem.
        /// </summary>
        public static readonly byte[] DisconnectionResponse = [0x4c, 0x4c, 0x53, 0x74, 0x65, 0x72, 0x6d, 0x6f, 0x6b];
        // === MODEM-KEEPALIVE ===
        /// <summary>
        /// Modem-KeepAlive, that the answering modem sends to its subscriber every third of the connection timeout.
        /// </summary>
        public static readonly byte[] KeepAliveRequest = [0x77, 0x99, 0x77, 0x99];
        /// <summary>
        /// The subscriber's answer to <see cref="KeepAliveRequest"/>.
        /// </summary>
        public static readonly byte[] KeepAliveResponse = [0x22, 0xFF, 0x22, 0xFF];
    }
}
