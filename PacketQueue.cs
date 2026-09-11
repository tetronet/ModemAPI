using System.Text;

namespace ModemAPI
{
    /// <summary>
    /// Represens a Packet Queue, that is being built using packets, received from the Tetronet.
    /// </summary>
    public class PacketQueue
    {
        private SortedDictionary<ulong, Packet> Packets = new SortedDictionary<ulong, Packet>();
        /// <summary>
        /// Timestamp of last time when a new packet was introduced in Ticks.
        /// </summary>
        public long Timestamp;
        /// <summary>
        /// Timeout for this Packet Queue
        /// </summary>
        public readonly int Timeout;
        /// <summary>
        /// Create a new instance of PacketQueue.
        /// </summary>
        public PacketQueue(int timeout)
        {
            Timestamp = DateTime.Now.Ticks;
        }
        /// <summary>
        /// Introduces a new packet to this instance of Packet Queue.
        /// </summary>
        /// <param name="packet">Packet to introduce.</param>
        public void AddPacket(Packet packet)
        {
            if (Packets.TryAdd(packet.PacketNo, packet))
            {
                Timestamp = DateTime.Now.Ticks;
            }
        }
        /// <summary>
        /// Checks for a timeout.
        /// </summary>
        /// <returns>true, if this Packet Queue is timed out, otherwise - false.</returns>
        public bool IsTimedOut()
        {
            return DateTime.Now.Ticks - Timeout > Timestamp;
        }
        /// <summary>
        /// Assembles all Packets into a single DataBlock object.
        /// </summary>
        /// <returns>DataBlock object, if this Packet Queue is full and is terminated, otherwise - null.</returns>
        public DataBlock? Assemble()
        {
            bool canAssemble1 = true;
            bool canAssemble2 = false;
            for (int i = 0; i < Packets.Count; i++)
            {
                if (!Packets.ContainsKey((ulong)i))
                {
                    canAssemble1 = false;
                    break;
                }
            }
            foreach (Packet packet in Packets.Values)
            {
                canAssemble2 = packet.IsLastInSequence;
                if (canAssemble2 == true)
                {
                    break;
                }
            }
            if (canAssemble1 && canAssemble2)
            {
                DataBlock prepare = new();
                StringBuilder sb = new StringBuilder();
                Packet lastPacket = Packets.Last().Value;
                prepare.Transmitter = lastPacket.Transmitter;
                prepare.Receiver = lastPacket.Receiver;
                prepare.QueryType = lastPacket.QueryType;
                prepare.Metadata = lastPacket.Metadata;
                prepare.ConnectionID = lastPacket.ConnectionID;
                for (int i = 0; i < Packets.Count; i++)
                {
                    Packet currentPacket = Packets[(ulong)i];
                    sb.Append(Encoding.UTF8.GetString([.. currentPacket.DataBytes]));
                    prepare.DataBytes.AddRange(currentPacket.DataBytes);
                }
                return prepare;
            }
            return null;
        }
    }
}
