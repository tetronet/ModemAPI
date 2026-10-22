using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    /// <summary>
    /// Represents a packet queue assembler for defragmenting a DataBlock from multiple packets. Queues are told apart by the
    /// transmitter address and the message ID, and a queue, that gets no new packets for the timeout, is removed. Thread-safe.
    /// </summary>
    public class PacketQueueAssembler
    {
        /// <summary>
        /// Timeout of a queue when none is given: 60 seconds, in ticks (100 ns).
        /// </summary>
        public const int DefaultTimeout = 600000000;
        private int MaximumHandledQueues;
        private int Timeout;
        private ConcurrentDictionary<(string, ulong), PacketQueue> PacketQueues = [];

        private sealed class CleanupState
        {
            // the timer only holds the assembler weakly, so an assembler nobody uses anymore is collected and its timer stops
            public required WeakReference<PacketQueueAssembler> Assembler;
            public Timer? Timer;
        }

        /// <summary>
        /// Create an instance of PacketQueueAssembler: any number of queues, <see cref="DefaultTimeout"/>.
        /// </summary>
        public PacketQueueAssembler() : this(0, DefaultTimeout)
        {
        }
        /// <summary>
        /// Create an instance of PacketQueueAssembler with <see cref="DefaultTimeout"/>.
        /// </summary>
        /// <param name="size">How many queues can be handled at the same time (0 or less - any number).</param>
        public PacketQueueAssembler(int size) : this(size, DefaultTimeout)
        {
        }
        /// <summary>
        /// Create an instance of PacketQueueAssembler.
        /// </summary>
        /// <param name="size">How many queues can be handled at the same time (0 or less - any number); packets of new messages over this number are dropped.</param>
        /// <param name="timeout">How much time queue will live without receiving new packets, in ticks (100 ns); 0 or less - <see cref="DefaultTimeout"/></param>
        public PacketQueueAssembler(int size, int timeout)
        {
            MaximumHandledQueues = size;
            Timeout = timeout > 0 ? timeout : DefaultTimeout;
            // checked a few times per timeout, but not more often than every 50 ms or less often than every second
            TimeSpan period = TimeSpan.FromMilliseconds(Math.Clamp(TimeSpan.FromTicks(Timeout).TotalMilliseconds / 4, 50, 1000));
            CleanupState state = new() { Assembler = new(this) };
            state.Timer = new Timer(Cleanup, state, period, period);
        }
        private static void Cleanup(object? stateObject)
        {
            CleanupState state = (CleanupState)stateObject!;
            if (state.Assembler.TryGetTarget(out PacketQueueAssembler? assembler))
            {
                assembler.RemoveTimedOutQueues();
            }
            else
            {
                state.Timer?.Dispose();
            }
        }
        private void RemoveTimedOutQueues()
        {
            foreach (KeyValuePair<(string, ulong), PacketQueue> kv in PacketQueues)
            {
                if (kv.Value.IsTimedOut())
                {
                    PacketQueues.TryRemove(kv);
                }
            }
        }
        /// <summary>
        /// How many queues are being assembled right now.
        /// </summary>
        public int Count { get { return PacketQueues.Count; } }
        /// <summary>
        /// Adds a new packet to the queue by its transmitter, Message ID and Packet No.
        /// </summary>
        /// <param name="packet">Packet to add.</param>
        public void AddPacket(Packet packet)
        {
            (string, ulong) key = (packet.Transmitter.AddressValue ?? "", packet.MessageId);
            if (!PacketQueues.TryGetValue(key, out PacketQueue? queue))
            {
                if (MaximumHandledQueues > 0 && PacketQueues.Count >= MaximumHandledQueues)
                {
                    // too many messages are being assembled already
                    return;
                }
                queue = PacketQueues.GetOrAdd(key, _ => new PacketQueue(Timeout));
            }
            queue.AddPacket(packet);
        }
        /// <summary>
        /// Gets the queue of a message.
        /// </summary>
        /// <param name="transmitter">Who sent the message</param>
        /// <param name="msgid">Message ID</param>
        public PacketQueue? GetPacketQueue(Address transmitter, ulong msgid)
        {
            PacketQueues.TryGetValue((transmitter.AddressValue ?? "", msgid), out PacketQueue? value);
            return value;
        }
        /// <summary>
        /// Gets a queue with this Message ID from any transmitter.
        /// </summary>
        [Obsolete("Message IDs of different transmitters can be the same, use GetPacketQueue(Address, ulong)")]
        public PacketQueue? GetPacketQueue(ulong msgid)
        {
            foreach (KeyValuePair<(string, ulong), PacketQueue> kv in PacketQueues)
            {
                if (kv.Key.Item2 == msgid)
                {
                    return kv.Value;
                }
            }
            return null;
        }
        /// <summary>
        /// Removes the queue of a message.
        /// </summary>
        /// <returns>true, if the queue was removed by this call, otherwise - false.</returns>
        public bool RemovePacketQueue(Address transmitter, ulong msgid)
        {
            return PacketQueues.TryRemove((transmitter.AddressValue ?? "", msgid), out _);
        }
        /// <summary>
        /// Removes the queues with this Message ID from every transmitter.
        /// </summary>
        /// <returns>true, if a queue was removed by this call, otherwise - false.</returns>
        [Obsolete("Message IDs of different transmitters can be the same, use RemovePacketQueue(Address, ulong)")]
        public bool RemovePacketQueue(ulong msgid)
        {
            bool removed = false;
            foreach (KeyValuePair<(string, ulong), PacketQueue> kv in PacketQueues)
            {
                if (kv.Key.Item2 == msgid && PacketQueues.TryRemove(kv))
                {
                    removed = true;
                }
            }
            return removed;
        }
    }
}
