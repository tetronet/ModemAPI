using System.Diagnostics;
using System.Text;

namespace ModemAPI
{
    /// <summary>
    /// Represens a Packet Queue, that is being built using packets, received from the Tetronet. Thread-safe.
    /// </summary>
    public class PacketQueue
    {
        private SortedDictionary<ulong, Packet> Packets = new SortedDictionary<ulong, Packet>();
        private readonly Lock PacketsLock = new();
        // PacketNo of the packet marked as the last one, or -1 while it hasn't arrived
        private long LastPacketNo = -1;
        private ulong HighestPacketNo = 0;
        /// <summary>
        /// <see cref="Stopwatch.GetTimestamp"/> of the last time when a new packet was introduced.
        /// </summary>
        public long Timestamp;
        /// <summary>
        /// Timeout for this Packet Queue in ticks (100 ns): how long it lives without new packets.
        /// </summary>
        public readonly int Timeout;
        /// <summary>
        /// Create a new instance of PacketQueue.
        /// </summary>
        /// <param name="timeout">How long the queue lives without new packets, in ticks (100 ns)</param>
        public PacketQueue(int timeout)
        {
            Timeout = timeout;
            Timestamp = Stopwatch.GetTimestamp();
        }
        /// <summary>
        /// Introduces a new packet to this instance of Packet Queue.
        /// </summary>
        /// <param name="packet">Packet to introduce.</param>
        public void AddPacket(Packet packet)
        {
            lock (PacketsLock)
            {
                if (!Packets.TryAdd(packet.PacketNo, packet))
                {
                    return;
                }
                if (packet.PacketNo > HighestPacketNo)
                {
                    HighestPacketNo = packet.PacketNo;
                }
                if (packet.IsLastInSequence && LastPacketNo < 0)
                {
                    LastPacketNo = (long)packet.PacketNo;
                }
                Timestamp = Stopwatch.GetTimestamp();
            }
        }
        /// <summary>
        /// Checks for a timeout.
        /// </summary>
        /// <returns>true, if this Packet Queue is timed out, otherwise - false.</returns>
        public bool IsTimedOut()
        {
            return Stopwatch.GetElapsedTime(Volatile.Read(ref Timestamp)).Ticks > Timeout;
        }
        /// <summary>
        /// Assembles all Packets into a single DataBlock object. Cheap while the queue isn't complete, so it can be called for every packet.
        /// </summary>
        /// <returns>DataBlock object, if this Packet Queue is full and is terminated, otherwise - null.</returns>
        public DataBlock? Assemble()
        {
            lock (PacketsLock)
            {
                // complete: the last packet arrived, and so did every packet before it (numbers are unique, so count and
                // the highest number tell that without walking the queue)
                if (LastPacketNo < 0 || HighestPacketNo != (ulong)LastPacketNo || Packets.Count != LastPacketNo + 1)
                {
                    return null;
                }
                DataBlock prepare = new();
                Packet lastPacket = Packets[(ulong)LastPacketNo];
                prepare.Transmitter = lastPacket.Transmitter;
                prepare.Receiver = lastPacket.Receiver;
                prepare.QueryType = lastPacket.QueryType;
                prepare.Metadata = lastPacket.Metadata;
                prepare.ConnectionID = lastPacket.ConnectionID;
                int length = 0;
                foreach (Packet currentPacket in Packets.Values)
                {
                    length += currentPacket.DataBytes.Length;
                }
                prepare.DataBytes.Capacity = length;
                foreach (Packet currentPacket in Packets.Values)
                {
                    prepare.DataBytes.AddRange(currentPacket.DataBytes);
                }
                prepare.DataString = Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(prepare.DataBytes));
                return prepare;
            }
        }
    }
}
