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
    }
}
