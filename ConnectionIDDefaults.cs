namespace ModemAPI
{
    /// <summary>
    /// Default ConnectionIDs for the tetronet traffic.
    /// </summary>
    public class ConnectionIDDefaults
    {
        /// <summary>
        /// Default ConnectionID for transmitting user messages using tetronet.
        /// </summary>
        public const uint MESSAGE = 22384112;
        /// <summary>
        /// Default ConnectionID for transmitting commands to the coreserver.
        /// </summary>
        public const uint COMMAND = 4998512;
        /// <summary>
        /// Default ConnectionID for transmitting User Files using tetronet.
        /// </summary>
        public const uint USER_FTCI = 99451;
        /// <summary>
        /// Default ConnectionID for transmitting LMSCP system signals.
        /// </summary>
        public const uint LMSCP = 13011453;
        /// <summary>
        /// Default ConnectionID for transmitting Tetronet Acked Data.
        /// </summary>
        public const uint TNET_ACK = 985113258;
        /// <summary>
        /// Default ConnectionID for LMTP Additional packets.
        /// </summary>
        public const uint LARGE_MESSAGE_TRIGGER = 4000000012;
    }
}
