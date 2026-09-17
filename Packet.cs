namespace ModemAPI
{
    /// <summary>
    /// Represents a packet, that may be transmitted by a modem, and also recevied.
    /// </summary>
    public class Packet
    {
        public Packet()
        {
            
        }
        public Packet(Address tx,
            Address rx,
            byte[] bytes,
            bool last,
            string? md,
            string qt,
            uint cid,
            bool errored,
            ulong pid,
            ulong mid)
        {
            Receiver = rx;
            Transmitter = tx;
            IsLastInSequence = last;
            Metadata = md;
            QueryType = qt;
            ConnectionID = cid;
            IsErrorWhileReading = errored;
            PacketNo = pid;
            MessageId = mid;
        }
        /// <summary>
        /// TetroNet subscriber address, that transmitted this instance of packet.
        /// </summary>
        public Address Transmitter = new Address();
        /// <summary>
        /// TetroNet subscriber address, that is going to receive this instance of packet.
        /// </summary>
        public Address Receiver = new Address();
        /// <summary>
        /// Data of the packet, that was transferred in it.
        /// </summary>
        public byte[] DataBytes = [];
        /// <summary>
        /// Is packet last in packet sequence.
        /// </summary>
        public bool IsLastInSequence = false;
        /// <summary>
        /// Metadata string that was received, metadata may not exist, then value will be set to null.
        /// </summary>
        public string? Metadata = null;
        /// <summary>
        /// Query type of that package.
        /// </summary>
        public string QueryType = "";
        /// <summary>
        /// ConnectionID for that package.
        /// </summary>
        public uint ConnectionID = 0;
        /// <summary>
        /// True, if packet is wrongly received or there's some other reasons to make packet wrong.
        /// </summary>
        public bool IsErrorWhileReading = false;
        /// <summary>
        /// Packet transmission No. to get when packet order gets messed up.
        /// </summary>
        public ulong PacketNo = 0;
        /// <summary>
        /// ID of message, that this packet belongs to.
        /// </summary>
        public ulong MessageId = 0;

        public class Static
        {
            /// <summary>
            /// Represents an empty Tetronet packet.
            /// </summary>
            public static readonly Packet EmptyPacket = new();
            /// <summary>
            /// Represents a packet, that had one or more errors to occur during receiving process.
            /// </summary>
            public static readonly Packet ErroredPacket = new(new(), new(), [], false, null, "", 0, true, 0, 0);
        }
    }
}
