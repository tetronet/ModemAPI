using System.Buffers.Binary;
using System.Collections.Concurrent;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// What happens to a packet on the fake link.
    /// </summary>
    public enum Fault
    {
        Deliver,
        Drop,
        Duplicate,
        /// <summary>Delivered right after the next packet going the same way, so the two arrive swapped.</summary>
        Swap,
    }

    /// <summary>
    /// Two fake modems connected by a link, that can lose, duplicate and reorder packets, strip their metadata or go down.
    /// Every modem delivers packets on its own thread, one by one, like a real modem's reader thread.
    /// </summary>
    public sealed class FakeLink : IDisposable
    {
        public readonly FakeModem A;
        public readonly FakeModem B;
        /// <summary>Decides the fate of every packet sent from A to B.</summary>
        public Func<Packet, Fault> AToB = _ => Fault.Deliver;
        /// <summary>Decides the fate of every packet sent from B to A.</summary>
        public Func<Packet, Fault> BToA = _ => Fault.Deliver;
        /// <summary>Removes the metadata of every packet, which makes SRTP fall back to v1.</summary>
        public bool StripMetadata;
        /// <summary>Drops everything in both directions.</summary>
        public volatile bool Down;
        private readonly Lock SwapLock = new();
        private Packet? HeldAToB;
        private Packet? HeldBToA;

        public FakeLink()
        {
            A = new FakeModem(this, new Address("7-1-1"));
            B = new FakeModem(this, new Address("7-1-2"));
        }

        internal void Send(FakeModem from, Packet packet)
        {
            if (Down)
            {
                return;
            }
            if (StripMetadata)
            {
                packet.Metadata = null;
            }
            bool fromA = from == A;
            FakeModem to = fromA ? B : A;
            Fault fault = (fromA ? AToB : BToA)(packet);
            lock (SwapLock)
            {
                switch (fault)
                {
                    case Fault.Drop:
                        return;
                    case Fault.Swap:
                        if ((fromA ? HeldAToB : HeldBToA) == null)
                        {
                            if (fromA) HeldAToB = packet; else HeldBToA = packet;
                            // if nothing else comes after it, it is delivered a bit later anyway instead of never
                            Task.Delay(50).ContinueWith(_ => ReleaseHeld(fromA, packet));
                            return;
                        }
                        break;
                    case Fault.Duplicate:
                        to.Enqueue(Copy(packet));
                        break;
                }
                to.Enqueue(packet);
                Packet? held = fromA ? HeldAToB : HeldBToA;
                if (held != null)
                {
                    if (fromA) HeldAToB = null; else HeldBToA = null;
                    to.Enqueue(held);
                }
            }
        }

        private void ReleaseHeld(bool fromA, Packet packet)
        {
            lock (SwapLock)
            {
                if (fromA && HeldAToB == packet) { HeldAToB = null; B.Enqueue(packet); }
                if (!fromA && HeldBToA == packet) { HeldBToA = null; A.Enqueue(packet); }
            }
        }

        private static Packet Copy(Packet p)
        {
            return new Packet(p.Transmitter, p.Receiver, (byte[])p.DataBytes.Clone(), p.IsLastInSequence, p.Metadata, p.QueryType, p.ConnectionID, false, p.PacketNo, p.MessageId, p.RoutersToPass);
        }

        public void Dispose()
        {
            A.Dispose();
            B.Dispose();
        }

        // helpers for fault policies

        /// <summary>An SRTP acknowledgement of either version.</summary>
        public static bool IsAck(Packet p) => p.Metadata == "srtp2a" || (p.Metadata != "srtp2" && p.DataBytes.Length == 8);

        /// <summary>An SRTP message (data), not an acknowledgement and not an LMDTP request.</summary>
        public static bool IsData(Packet p) => !IsAck(p) && !IsLmdtpRequest(p) && p.DataBytes.Length > 8;

        /// <summary>The LMDTP request is sent once without retransmission, so tests never lose it.</summary>
        public static bool IsLmdtpRequest(Packet p) => p.Metadata == null && p.DataBytes.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(p.DataBytes) == LMDTPClient.LMDTP_REQUEST_PREFIX;

        /// <summary>Sequential number of an SRTP message.</summary>
        public static long SequenceOf(Packet p) => BinaryPrimitives.ReadInt64BigEndian(p.DataBytes);
    }

    public sealed class FakeModem : IModem, IDisposable
    {
        private readonly FakeLink Link;
        private readonly BlockingCollection<Packet> Inbox = new();
        private readonly Thread Reader;
        private Action<Packet, Action>[] Subscribers = [];
        private readonly Lock SubscribersLock = new();

        internal FakeModem(FakeLink link, Address address)
        {
            Link = link;
            LocalModemAddress = address;
            Reader = new Thread(ReadLoop) { IsBackground = true, Name = "fake modem " + address };
            Reader.Start();
        }

        internal void Enqueue(Packet packet)
        {
            if (!Inbox.IsAddingCompleted)
            {
                try { Inbox.Add(packet); } catch (InvalidOperationException) { }
            }
        }

        private void ReadLoop()
        {
            foreach (Packet packet in Inbox.GetConsumingEnumerable())
            {
                foreach (Action<Packet, Action> subscriber in Volatile.Read(ref Subscribers))
                {
                    subscriber(packet, delegate { });
                }
            }
        }

        public int MaxReinitializeAttempts { get; set; }
        public Address? LocalModemAddress { get; set; }
        public bool IsModemConnected => true;
        public LargeMessage? LastDownloadedLargeMessage { get; set; }
        public void Dial() { }
        public void Drop(bool carefulMode) { }

        public void LowLevelTransmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null, ushort ttl = 65535)
        {
            // a copy, like a real modem serializing the packet: the sender may reuse its buffer
            Packet packet = new(LocalModemAddress!, address, (byte[])data.Clone(), true, metadata, queryType, connectionId, false, 0, 0, ttl);
            Link.Send(this, packet);
        }

        public void AttachReceiveEventNoUnfragment(Action<Packet, Action> onReceive)
        {
            lock (SubscribersLock) Subscribers = [.. Subscribers, onReceive];
        }

        public void DetachReceiveEventNoUnfragment(Action<Packet, Action> onReceive)
        {
            lock (SubscribersLock)
            {
                int index = Array.IndexOf(Subscribers, onReceive);
                if (index >= 0) Subscribers = [.. Subscribers[..index], .. Subscribers[(index + 1)..]];
            }
        }

        public int SubscriberCount => Volatile.Read(ref Subscribers).Length;

        public void Dispose() => Inbox.CompleteAdding();

        public void Transmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0, ushort ttl = 65535) => throw new NotSupportedException();
        public void Transmit(string data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0, ushort ttl = 65535) => throw new NotSupportedException();
        public void AttachReceiveEvent(Action<DataBlock, Action> onReceive) => throw new NotSupportedException();
        public bool IsAddressSMLA() => false;
        public Task GetLargeMessage(Address transmitter) => throw new NotSupportedException();
        public Task GetLargeMessage(Address tx, Address rx) => throw new NotSupportedException();
        public Task GetLargeMessage(TransmitterReceiverPair txrxpair) => throw new NotSupportedException();
        public Task DeleteLargeMessage(Address transmitter) => throw new NotSupportedException();
        public Task<int> PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string? metadata = null) => throw new NotSupportedException();
        public Task DeleteLargeMessage(Address tx, Address rx) => throw new NotSupportedException();
        public Task DeleteLargeMessage(TransmitterReceiverPair txrxpair) => throw new NotSupportedException();
    }

    /// <summary>
    /// A random source shared by the fault policy of both directions (they run on different threads).
    /// </summary>
    public sealed class SharedRandom(int seed)
    {
        private readonly Random Random = new(seed);
        public double NextDouble() { lock (Random) return Random.NextDouble(); }
    }

    public static class Guard
    {
        /// <summary>
        /// Runs a blocking action and fails the test if it doesn't finish in time, instead of hanging the test run.
        /// </summary>
        /// <returns>The exception thrown by the action, or null if it completed</returns>
        public static Exception? RunWithin(TimeSpan limit, Action action)
        {
            Task task = Task.Factory.StartNew(action, TaskCreationOptions.LongRunning);
            bool finished;
            try
            {
                finished = task.Wait(limit);
            }
            catch (AggregateException error)
            {
                return error.InnerException;
            }
            Assert.True(finished, $"the operation didn't finish in {limit.TotalSeconds} s");
            return null;
        }
    }
}
