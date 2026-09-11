using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    /// <summary>
    /// Represents a packet queue assembler for defragmenting a DataBlock from multiple packets.
    /// </summary>
    public class PacketQueueAssembler
    {
        private int MaximumHandledQueues;
        private int Timeout;
        private ConcurrentDictionary<ulong, PacketQueue> PacketQueues = [];
        /// <summary>
        /// Create an instance of PacketQueueAssembler.
        /// </summary>
        public PacketQueueAssembler()
        {
            Task.Run(TimeoutCheckerLoop);
        }
        /// <summary>
        /// Create an instance of PacketQueueAssembler.
        /// </summary>
        /// <param name="size">How many queues can be handled at the same time.</param>
        public PacketQueueAssembler(int size)
        {
            MaximumHandledQueues = size;
            Task.Run(TimeoutCheckerLoop);
        }
        /// <summary>
        /// Create an instance of PacketQueueAssembler.
        /// </summary>
        /// <param name="size">How many queues can be handled at the same time.</param>
        /// <param name="timeout">How much time queue will live without receiving new packets</param>
        public PacketQueueAssembler(int size, int timeout)
        {
            MaximumHandledQueues = size;
            Timeout = timeout;
            Task.Run(TimeoutCheckerLoop);
        }
        /// <summary>
        /// Adds a new packet to the queue by Message ID and Packet No.
        /// </summary>
        /// <param name="packet">Packet to add.</param>
        public void AddPacket(Packet packet)
        {
            if (PacketQueues.TryGetValue(packet.MessageId, out PacketQueue? value))
            {
                value.AddPacket(packet);
            }
            else
            {
                PacketQueues.TryAdd(packet.MessageId, new PacketQueue(Timeout));
                PacketQueues[packet.MessageId].AddPacket(packet);
            }
        }
        public PacketQueue GetPacketQueue(ulong msgid)
        {
            return PacketQueues[msgid];
        }
        private void TimeoutCheckerLoop()
        {
            foreach (KeyValuePair<ulong, PacketQueue> kv in PacketQueues)
            {
                if (kv.Value.IsTimedOut())
                {
                    PacketQueues.Remove(kv.Key, out _);
                }
            }
        }
    }
}
