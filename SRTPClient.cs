using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ModemAPI
{
    public class SRTPClient
    {
        /// <summary>
        /// The most data one message can carry: 8 bytes of the sequential number and the data have to fit into one tetronet packet.
        /// </summary>
        public const int MaxMessageLength = 59992;
        /// <summary>
        /// Metadata of data packets sent by clients that understand batched acknowledgements. Older clients ignore metadata,
        /// so they keep receiving data as before and keep acknowledging every packet.
        /// </summary>
        private const string BatchedAckDataTag = "srtp2";
        /// <summary>
        /// Metadata of a batched acknowledgement: 8 bytes "every packet up to this number is delivered", followed by an
        /// optional bitmap of packets received after it (bit i = packet number + 1 + i). It is only sent to peers, whose data carried <see cref="BatchedAckDataTag"/>.
        /// </summary>
        private const string BatchedAckTag = "srtp2a";
        private const int SelectiveAckBits = 256;
        private Address? CommunicatingWith = null;
        private uint ConnectionID = 0;
        private string QueryType = "";
        private IModem Modem;
        private long LastReceived = -1; // every packet up to this one is delivered to OnMessageReceived
        private long HighestContiguousReceived = -1; // every packet up to this one is received (delivered or waiting in ReorderingBuffer)
        private long SequentialPacketNumber = 0;
        private long LowestUnacknowledged = 0; // sender side: packets below this one are acknowledged
        private volatile bool IsClosed = false;
        private int CloseStarted = 0;
        private int TicksTimeout;
        private ConcurrentDictionary<long, byte[]> ReorderingBuffer = new ConcurrentDictionary<long, byte[]>();
        private ConcurrentDictionary<long, ReliablePacketInformation> InternalState = new ConcurrentDictionary<long, ReliablePacketInformation>();
        private readonly Lock ReceiveLock = new();
        private readonly Lock AcknowledgementLock = new();
        private readonly object WindowLock = new();
        private bool PeerUsesBatchedAcks = false;
        private int UnacknowledgedPackets = 0; // delivered from a batched-ack peer, but not acknowledged yet
        private int OutOfOrderSinceAcknowledgement = 0; // received after a gap since the last acknowledgement
        private long FirstUnacknowledgedTimestamp = 0;
        private long NextSendTimestamp = 0;
        private readonly Lock PacingLock = new();
        private readonly SemaphoreSlim ReorderSignal = new(0, 1);
        private Stopwatch LastWindowHalvingTimer = Stopwatch.StartNew();
        private bool FailedDueToTooManyRetxAttepms = false;
        private double ThrottlingPower = 0.01;
        // the window size is changed by the receiving thread and the retransmission thread
        private readonly Lock CongestionLock = new();
        // Transmit is called by any number of threads: the sequential number is taken and the packet sent under this lock
        private readonly Lock TransmitLock = new();
        private Action<SRTPClient, byte[]> MessageReceivedHandler = delegate { };
        /// <summary>
        /// Gets called for each received message by this client, and data of this message is sent to the event handler.
        /// A session created by <see cref="SRTPServer"/> starts delivering messages when this is set.
        /// </summary>
        public Action<SRTPClient, byte[]> OnMessageReceived
        {
            get { return MessageReceivedHandler; }
            set
            {
                MessageReceivedHandler = value;
                // the application subscribed, so the messages held for it can go
                if (DeliveryHeld)
                {
                    ReleaseDelivery();
                }
            }
        }
        /// <summary>
        /// Gets called for each error in this client, where the errcode and text description are sent to the event handler.
        /// </summary>
        public Action<int, string> OnError = delegate { };
        /// <summary>
        /// Gets called for each retransmit, and Sequential ID is sent to the event handler.
        /// </summary>
        public Action<long> OnRetransmit = delegate { };
        /// <summary>
        /// Gets called for each acknowledgement packet, and Sequential ID is sent to the event handler.
        /// </summary>
        public Action<long> OnAckReceived = delegate { };
        /// <summary>
        /// Gets called for each acknowledgement packet sending.
        /// </summary>
        public Action<long> OnAckTransmitted = delegate { };
        /// <summary>
        /// Gets called for each packet that is out of order, and Sequential ID is sent to the event handler.
        /// </summary>
        public Action<long> OnOutOfOrderPacketReceived = delegate { };
        /// <summary>
        /// Window size: how far past the first unacknowledged packet the sender may go. Without <see cref="PacketLossBasedCongestionControl"/>
        /// the whole window is used. Keep it not bigger than the remote side's <see cref="MaxPacketNoFromFuture"/>.
        /// </summary>
        public int MaxUnackedPackets = 1024;
        /// <summary>
        /// How deep into the future packets will be received.
        /// </summary>
        public int MaxPacketNoFromFuture = 1024;
        /// <summary>
        /// When the remote side supports batched acknowledgements, one acknowledgement is sent per this many delivered packets.
        /// </summary>
        public int AckEveryPackets = 16;
        /// <summary>
        /// When the remote side supports batched acknowledgements, a received packet is acknowledged not later than this (plus the timer resolution).
        /// </summary>
        public int AckDelayMilliseconds = 2;
        /// <summary>
        /// A packet is considered lost and retransmitted right away (without waiting for the timeout), once the remote side
        /// reports this many packets after it as received. Smaller values react faster, bigger ones tolerate more reordering.
        /// </summary>
        public int FastRetransmitAfterPackets = 3;
        /// <summary>
        /// Delay in Ticks (each tick is 100 ns) between Universal Packet Sends.
        /// </summary>
        public int TicksPacketDelay = 0;
        /// <summary>
        /// If set to true, this client controls the load using packet loss: the window shrinks on losses and grows on acknowledgements, so the tetronet doesn't get overloaded
        /// </summary>
        public bool PacketLossBasedCongestionControl = false;
        /// <summary>
        /// Gets the remote address, which this SRTP client is currently communicating with.
        /// </summary>
        public Address RemoteServer { get { return Address.Copy(CommunicatingWith); } }
        /// <summary>
        /// Gets the Connection ID, which this SRTP client is currently using for tetronet communication.
        /// </summary>
        public uint EndPointConnectionID { get { return ConnectionID; } }
        /// <summary>
        /// Time out in Ticks, for how long this instance of SRTPClient will wait for a free slot in InternalState.
        /// </summary>
        public int TransmitTimeOutInTicks = 600000000;
        /// <summary>
        /// How many retransmissions will any of the packets need to cause the remote side being considered dead and when Transmit(byte[]) method will throw an exception.
        /// </summary>
        public int RetransmissionsBeforeConsideringRemoteSideDead = 100;
        /// <summary>
        /// Milliseconds to pause on packet loss if <see cref="PacketLossBasedCongestionControl"/> is set to <see langword="true"/>
        /// </summary>
        public int ThrottleTime = 2000;
        public event Action<SRTPClient> OnClose = delegate { };
        public long MissingPacket
        {
            get
            {
                return LastReceived + 1;
            }
        }
        public bool IsMissingPacketAvailable
        {
            get
            {
                return ReorderingBuffer.ContainsKey(MissingPacket);
            }
        }
        private int CurrentWindow
        {
            get
            {
                return PacketLossBasedCongestionControl ? Math.Max(1, (int)(MaxUnackedPackets * ThrottlingPower)) : MaxUnackedPackets;
            }
        }

        public SRTPClient(IModem modem, Address dst, string qt, uint cid, int tickTimeout, bool autoclear = true)
        {
            ConnectionID = cid;
            QueryType = qt;
            Modem = modem;
            CommunicatingWith = dst;
            TicksTimeout = tickTimeout;
            // receiver task
            Modem.AttachReceiveEventNoUnfragment(OnPacketReceived);
            // error correction task, also cleans the reordering buffer once a second
            _ = Task.Run(async delegate ()
            {
                int round = 0;
                while (!IsClosed)
                {
                    try
                    {
                        foreach (ReliablePacketInformation info in InternalState.Values)
                        {
                            if (info.CheckTimeout(TicksTimeout))
                            {
                                // a packet gets exactly RetransmissionsBeforeConsideringRemoteSideDead retransmissions
                                if (info.RetransmissionCounter >= RetransmissionsBeforeConsideringRemoteSideDead)
                                {
                                    FailedDueToTooManyRetxAttepms = true;
                                    Close();
                                    break;
                                }
                                info.RetransmissionCounter++;
                                // retransmit packets
                                UnivSend(info.Data, BatchedAckDataTag);
                                // update timeout
                                info.UpdateTimeStamp();
                                // if enabled, pause the transmission
                                HalveWindowOnLoss();
                                // call the user event
                                OnRetransmit(info.Id);
                            }
                        }
                        if (autoclear && ++round % 10 == 0)
                        {
                            ClearReorderingBuffer();
                        }
                        await Task.Delay(100);
                    }
                    catch (Exception ex)
                    {
                        OnError(1870, "Retransmit Exception: " + ex);
                    }
                }
            });
            // reorder task, also sends delayed batched acknowledgements
            _ = Task.Run(async delegate ()
            {
                while (!IsClosed)
                {
                    try
                    {
                        ForceReadAllAvailablePackets();
                        SendDelayedAcknowledgement();
                    }
                    catch (Exception e)
                    {
                        OnError(-1, "Exception: " + e.ToString());
                    }
                    // wake up on a new packet instead of polling, the timeout is just a safety net or the acknowledgement delay
                    await ReorderSignal.WaitAsync(Volatile.Read(ref UnacknowledgedPackets) > 0 ? AckDelayMilliseconds : 100);
                }
            });
        }
        // while set, received messages wait in the reordering buffer instead of going to OnMessageReceived
        private volatile bool DeliveryHeld = false;
        /// <summary>
        /// Keeps received messages from being delivered until <see cref="ReleaseDelivery"/> or until <see cref="OnMessageReceived"/> is set,
        /// so none of them goes to nobody before the application subscribed.
        /// </summary>
        internal void HoldDelivery()
        {
            DeliveryHeld = true;
        }
        internal void ReleaseDelivery()
        {
            DeliveryHeld = false;
            WakeUpReorderTask();
        }
        /// <summary>
        /// Handles a packet, that this client didn't get from the modem itself.
        /// </summary>
        internal void Receive(Packet packet)
        {
            OnPacketReceived(packet, delegate { });
        }
        private void OnPacketReceived(Packet packet, Action k)
        {
            if (IsClosed)
            {
                return;
            }
            if (packet.ConnectionID != ConnectionID || packet.QueryType != QueryType || !packet.Transmitter.Equals(CommunicatingWith))
            {
                ModemAPIDebugger.OutputDebugMessage("SRTPClient MISMATCH CID OR QT!!!!!!!!!");
                return;
            }
            try
            {
                byte[] dataReceived = packet.DataBytes;
                if (packet.Metadata == BatchedAckTag)
                {
                    HandleBatchedAcknowledgement(dataReceived);
                    return;
                }
                // parse received packet
                if (dataReceived.Length < 8)
                {
                    OnError(16, "Packet too short.");
                    return;
                }
                long id = BinaryPrimitives.ReadInt64BigEndian(dataReceived);
                if (id < 0)
                {
                    OnError(17, $"Packet with a negative sequential number {id}.");
                    return;
                }

                if (dataReceived.Length == 8)
                {
                    // == ack receiver ==
                    if (InternalState.TryRemove(id, out _))
                    {
                        OnAckReceived(id);
                        lock (AcknowledgementLock)
                        {
                            AdvanceLowestUnacknowledged();
                        }
                        AcknowledgementsReceived(1);
                    }
                    return;
                }
                // == enqueue packets ==
                bool batched = packet.Metadata == BatchedAckDataTag;
                bool tooDeep = false;
                bool acknowledgeNow = !batched; // older peers expect an acknowledgement for every packet
                lock (ReceiveLock)
                {
                    if (batched)
                    {
                        PeerUsesBatchedAcks = true;
                    }
                    if (id <= HighestContiguousReceived || ReorderingBuffer.ContainsKey(id))
                    {
                        // a retransmission: our acknowledgement got lost or late, so repeat it now
                        acknowledgeNow = true;
                    }
                    else if (LastReceived + MaxPacketNoFromFuture < id)
                    {
                        tooDeep = true;
                    }
                    else
                    {
                        bool inOrder = id == HighestContiguousReceived + 1;
                        ReorderingBuffer.TryAdd(id, dataReceived);
                        while (ReorderingBuffer.ContainsKey(HighestContiguousReceived + 1))
                        {
                            HighestContiguousReceived++;
                        }
                        if (batched && !inOrder)
                        {
                            // in-order packets get acknowledged once they are delivered, but a gap means a loss: the sender
                            // retransmits a hole once FastRetransmitAfterPackets packets after it are reported, so report them
                            // as soon as there are enough, and then every few packets until the hole is filled
                            OutOfOrderSinceAcknowledgement++;
                            acknowledgeNow = OutOfOrderSinceAcknowledgement % (FastRetransmitAfterPackets + 1) == FastRetransmitAfterPackets;
                        }
                    }
                }
                if (tooDeep)
                {
                    OnError(-996, $"Packet {id} is too deep into the future.");
                    return;
                }
                WakeUpReorderTask();
                if (!acknowledgeNow)
                {
                    return;
                }
                if (batched)
                {
                    SendBatchedAcknowledgement();
                }
                else
                {
                    UnivSend(dataReceived.AsSpan(0, 8).ToArray());
                    ModemAPIDebugger.OutputDebugMessage($"UnivSend acknowledgement for packet {id}");
                }
            }
            catch (Exception e)
            {
                OnError(-1, "Exception: " + e.ToString());
            }
        }
        private void HandleBatchedAcknowledgement(byte[] acknowledgement)
        {
            if (acknowledgement.Length < 8)
            {
                OnError(16, "Packet too short.");
                return;
            }
            long cumulative = BinaryPrimitives.ReadInt64BigEndian(acknowledgement);
            int acknowledged = 0;
            List<ReliablePacketInformation>? lost = null;
            lock (AcknowledgementLock)
            {
                long upTo = Math.Min(cumulative, Interlocked.Read(ref SequentialPacketNumber) - 1);
                for (long id = LowestUnacknowledged; id <= upTo; id++)
                {
                    if (InternalState.TryRemove(id, out _))
                    {
                        acknowledged++;
                        OnAckReceived(id);
                    }
                }
                if (upTo >= LowestUnacknowledged)
                {
                    LowestUnacknowledged = upTo + 1;
                }
                for (int bit = 0; bit < Math.Min((acknowledgement.Length - 8) * 8, SelectiveAckBits); bit++)
                {
                    if ((acknowledgement[8 + bit / 8] & (1 << (bit % 8))) != 0 && InternalState.TryRemove(cumulative + 1 + bit, out _))
                    {
                        acknowledged++;
                        OnAckReceived(cumulative + 1 + bit);
                    }
                }
                // the window start doesn't move past selectively acknowledged packets: they are received, but not delivered
                // yet, and the receiver only takes packets up to its delivered position + MaxPacketNoFromFuture
                lost = FindLostPackets(acknowledgement, cumulative);
            }
            if (acknowledged > 0)
            {
                AcknowledgementsReceived(acknowledged);
            }
            if (lost != null)
            {
                // fast retransmit: the holes are resent now instead of after TicksTimeout
                foreach (ReliablePacketInformation info in lost)
                {
                    UnivSend(info.Data, BatchedAckDataTag);
                    info.UpdateTimeStamp();
                    OnRetransmit(info.Id);
                }
                HalveWindowOnLoss();
            }
        }
        /// <summary>
        /// Finds the holes in a selective acknowledgement: packets not reported as received, while at least
        /// <see cref="FastRetransmitAfterPackets"/> packets after them are. Must be called under <see cref="AcknowledgementLock"/>.
        /// </summary>
        /// <returns>Packets to retransmit right away or null</returns>
        private List<ReliablePacketInformation>? FindLostPackets(byte[] acknowledgement, long cumulative)
        {
            int bits = Math.Min((acknowledgement.Length - 8) * 8, SelectiveAckBits);
            long highestReported = -1;
            for (int bit = bits - 1; bit >= 0; bit--)
            {
                if ((acknowledgement[8 + bit / 8] & (1 << (bit % 8))) != 0)
                {
                    highestReported = cumulative + 1 + bit;
                    break;
                }
            }
            if (highestReported < 0)
            {
                return null;
            }
            List<ReliablePacketInformation>? lost = null;
            long sent = Interlocked.Read(ref SequentialPacketNumber);
            for (long id = cumulative + 1; id <= highestReported - FastRetransmitAfterPackets; id++)
            {
                int bit = (int)(id - cumulative - 1);
                if ((acknowledgement[8 + bit / 8] & (1 << (bit % 8))) != 0 || !InternalState.TryGetValue(id, out ReliablePacketInformation? info))
                {
                    continue;
                }
                // a hole is resent once; it is resent again only when packets sent after that retransmission arrive
                // without it, which means the retransmission got lost too
                if (info.FastRetransmittedBefore < 0 || highestReported >= info.FastRetransmittedBefore + FastRetransmitAfterPackets)
                {
                    info.FastRetransmittedBefore = sent;
                    (lost ??= []).Add(info);
                }
            }
            return lost;
        }
        /// <summary>
        /// Moves the window start past every acknowledged packet. Must be called under <see cref="AcknowledgementLock"/>.
        /// </summary>
        private void AdvanceLowestUnacknowledged()
        {
            // Transmit adds a packet before counting it, so every number below SequentialPacketNumber is either waiting or acknowledged
            while (LowestUnacknowledged < Interlocked.Read(ref SequentialPacketNumber) && !InternalState.ContainsKey(LowestUnacknowledged))
            {
                LowestUnacknowledged++;
            }
        }
        /// <summary>
        /// With <see cref="PacketLossBasedCongestionControl"/>, halves the window on a loss, but not more often than once per timeout.
        /// </summary>
        private void HalveWindowOnLoss()
        {
            if (!PacketLossBasedCongestionControl)
            {
                return;
            }
            lock (CongestionLock)
            {
                if (LastWindowHalvingTimer.Elapsed.Ticks > TicksTimeout)
                {
                    ThrottlingPower = Math.Clamp(ThrottlingPower / 2, 0.0001, 1);
                    LastWindowHalvingTimer.Restart();
                }
            }
        }
        private void AcknowledgementsReceived(int count)
        {
            if (PacketLossBasedCongestionControl)
            {
                lock (CongestionLock)
                {
                    ThrottlingPower = Math.Clamp(ThrottlingPower + 0.1 * count / MaxUnackedPackets, 0.0001, 1);
                }
            }
            // free slots in the window: wake up Transmit and WaitForAcknowledgements
            lock (WindowLock)
            {
                Monitor.PulseAll(WindowLock);
            }
        }
        private void SendBatchedAcknowledgement()
        {
            byte[] acknowledgement;
            long cumulative;
            lock (ReceiveLock)
            {
                if (!PeerUsesBatchedAcks)
                {
                    return;
                }
                UnacknowledgedPackets = 0;
                OutOfOrderSinceAcknowledgement = 0;
                // only delivered packets are acknowledged as a whole, so the sender can't get further ahead of the
                // application than the window, and the receiving buffer stays bounded
                cumulative = LastReceived;
                Span<byte> bitmap = stackalloc byte[SelectiveAckBits / 8];
                bitmap.Clear();
                int bitmapLength = 0;
                // received, but not delivered yet: reported selectively, so they don't get retransmitted
                if (!ReorderingBuffer.IsEmpty)
                {
                    for (int bit = 0; bit < SelectiveAckBits; bit++)
                    {
                        if (ReorderingBuffer.ContainsKey(cumulative + 1 + bit))
                        {
                            bitmap[bit / 8] |= (byte)(1 << (bit % 8));
                            bitmapLength = bit / 8 + 1;
                        }
                    }
                }
                acknowledgement = new byte[8 + bitmapLength];
                BinaryPrimitives.WriteInt64BigEndian(acknowledgement, cumulative);
                bitmap[..bitmapLength].CopyTo(acknowledgement.AsSpan(8));
            }
            UnivSend(acknowledgement, BatchedAckTag);
            OnAckTransmitted(cumulative);
        }
        private void SendDelayedAcknowledgement()
        {
            bool due;
            lock (ReceiveLock)
            {
                due = UnacknowledgedPackets > 0 &&
                    Stopwatch.GetElapsedTime(FirstUnacknowledgedTimestamp).TotalMilliseconds >= AckDelayMilliseconds;
            }
            if (due)
            {
                SendBatchedAcknowledgement();
            }
        }
        private void WakeUpReorderTask()
        {
            if (ReorderSignal.CurrentCount != 0)
            {
                return;
            }
            try
            {
                ReorderSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // another thread has already woken it up
            }
        }
        public bool AreAllPacketsFromReorderingBufferIncludedInAlreadyReceived()
        {
            foreach (long pno in ReorderingBuffer.Keys)
            {
                if (pno > LastReceived)
                {
                    return false;
                }
            }
            return true;
        }
        /// <summary>
        /// Counts packets for reordering.
        /// </summary>
        /// <returns>How many packets are in the reordering buffer of this instance of SRTPClient</returns>
        public int CountReorderingPackets()
        {
            return ReorderingBuffer.Count;
        }
        public void ClearReorderingBuffer()
        {
            foreach (long pno in ReorderingBuffer.Keys)
            {
                if (pno <= LastReceived)
                {
                    ReorderingBuffer.TryRemove(pno, out _);
                }
            }
        }
        /// <summary>
        /// Sends a message. Can be called from several threads at once: every message gets its own sequential number, and the
        /// messages are delivered in the order their calls took the numbers.
        /// </summary>
        /// <exception cref="OperationCanceledException">The remote side is considered dead (too many retransmissions)</exception>
        /// <exception cref="InvalidOperationException">The client was closed</exception>
        /// <exception cref="TimeoutException">The window stayed full for <see cref="TransmitTimeOutInTicks"/></exception>
        /// <exception cref="ArgumentOutOfRangeException">data is empty or longer than <see cref="MaxMessageLength"/></exception>
        public void Transmit(byte[] data)
        {
            // the cause of the failure is reported before the fact that the client got closed because of it
            if (FailedDueToTooManyRetxAttepms)
            {
                Close();
                throw new OperationCanceledException("Client was cancelled by the retransmission thread: too many retransmission attempts.");
            }
            if (IsClosed)
            {
                throw new InvalidOperationException("This client is in an invalid state \"Closed\". To transmit data, it must be in state \"Opened\"");
            }
            if (data.Length > MaxMessageLength || data.Length == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(data), $"data must contain not more than {MaxMessageLength} bytes and not less than 1 byte");
            }
            byte[] data_ = new byte[data.Length + 8];
            data.CopyTo(data_, 8);
            lock (TransmitLock)
            {
                // wait for a free slot in the window, acknowledgements wake this thread up
                WaitForWindow(delegate () { return SequentialPacketNumber - Volatile.Read(ref LowestUnacknowledged) < CurrentWindow; });
                long id = SequentialPacketNumber;
                BinaryPrimitives.WriteInt64BigEndian(data_.AsSpan(), id);
                // added before the number is counted: everything below SequentialPacketNumber is waiting or acknowledged
                InternalState.TryAdd(id, new(id, Stopwatch.GetTimestamp(), data_));
                Interlocked.Increment(ref SequentialPacketNumber);
                UnivSend(data_, BatchedAckDataTag);
            }
        }
        /// <summary>
        /// Blocks until the remote side acknowledges every transmitted packet. Call it before <see cref="Close"/>, otherwise
        /// packets lost on the way would never be retransmitted.
        /// </summary>
        /// <exception cref="TimeoutException">Not everything was acknowledged in <see cref="TransmitTimeOutInTicks"/></exception>
        /// <exception cref="InvalidOperationException">The client was closed</exception>
        /// <exception cref="OperationCanceledException">The remote side is considered dead</exception>
        public void WaitForAcknowledgements()
        {
            WaitForWindow(delegate () { return InternalState.IsEmpty; });
        }
        private void WaitForWindow(Func<bool> isReady)
        {
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                lock (WindowLock)
                {
                    // checked before isReady: Close() empties the unacknowledged packets, which must not look like "everything acknowledged"
                    if (!IsClosed && !FailedDueToTooManyRetxAttepms && isReady())
                    {
                        return;
                    }
                    if (!IsClosed && !FailedDueToTooManyRetxAttepms && Stopwatch.GetElapsedTime(start) <= TimeSpan.FromTicks(TransmitTimeOutInTicks))
                    {
                        // the timeout only matters if a wake up got lost or the window size changed
                        Monitor.Wait(WindowLock, 100);
                        continue;
                    }
                }
                if (FailedDueToTooManyRetxAttepms)
                {
                    Close();
                    throw new OperationCanceledException("Client was cancelled by the retransmission thread: too many retransmission attempts.");
                }
                if (IsClosed)
                {
                    throw new InvalidOperationException("This client is in an invalid state \"Closed\". To transmit data, it must be in state \"Opened\"");
                }
                throw new TimeoutException("Transmit timed out. Normally this will mean that the remote side is dead and you want to Close() this SRTPClient.");
            }
        }
        public void Close()
        {
            // the retransmission thread, Transmit and the owner can all close the client, but it closes only once
            if (Interlocked.Exchange(ref CloseStarted, 1) == 1)
            {
                return;
            }
            if (!IsClosed && Volatile.Read(ref UnacknowledgedPackets) > 0)
            {
                // don't leave the sender waiting for a delayed acknowledgement, that will never come
                SendBatchedAcknowledgement();
            }
            IsClosed = true;
            Modem.DetachReceiveEventNoUnfragment(OnPacketReceived);
            WakeUpReorderTask();
            ReorderingBuffer.Clear();
            InternalState.Clear();
            lock (WindowLock)
            {
                Monitor.PulseAll(WindowLock);
            }
            MessageReceivedHandler = delegate { };
            OnClose(this);
        }
        public bool WasPacketReceived(long packno)
        {
            return packno <= HighestContiguousReceived || ReorderingBuffer.ContainsKey(packno);
        }
        private void UnivSend(byte[]? data, string? metadata = null)
        {
            try
            {
                WaitForSendSlot();
                if (data == null)
                {
                    return;
                }
                if (CommunicatingWith == null)
                {
                    OnError(1048576, "This client does not communicate with any remote system.");
                    return;
                }
                Modem.LowLevelTransmit(data, CommunicatingWith, QueryType, ConnectionID, metadata);
            }
            catch (Exception ex)
            {
                OnError(1800, "UNIVSEND failed: " + ex);
            }
        }
        /// <summary>
        /// Paces sends to one per <see cref="TicksPacketDelay"/> on average. Instead of spinning between every two packets,
        /// the thread sleeps only once it gets ahead of the schedule, and catches up after an oversleep by sending a short burst.
        /// </summary>
        private void WaitForSendSlot()
        {
            if (TicksPacketDelay <= 0)
            {
                return;
            }
            long delay = TicksPacketDelay * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
            long maxLag = Stopwatch.Frequency / 50; // 20 ms, longer than the Windows timer resolution
            long minSleep = Stopwatch.Frequency / 500; // 2 ms
            long wait;
            lock (PacingLock)
            {
                long now = Stopwatch.GetTimestamp();
                if (NextSendTimestamp < now - maxLag)
                {
                    // the client was idle, not oversleeping, so there is nothing to catch up
                    NextSendTimestamp = now;
                }
                wait = NextSendTimestamp - now;
                NextSendTimestamp += delay;
            }
            if (wait >= minSleep)
            {
                Thread.Sleep(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
            }
        }
        private void ForceReadAllAvailablePackets()
        {
            while (!DeliveryHeld)
            {
                byte[]? dataFromBuffer;
                bool acknowledge = false;
                lock (ReceiveLock)
                {
                    // removing and counting the packet together keeps it "received" for duplicate checks at every moment
                    if (!ReorderingBuffer.TryRemove(LastReceived + 1, out dataFromBuffer))
                    {
                        return;
                    }
                    LastReceived++;
                    if (PeerUsesBatchedAcks)
                    {
                        if (UnacknowledgedPackets++ == 0)
                        {
                            FirstUnacknowledgedTimestamp = Stopwatch.GetTimestamp();
                        }
                        acknowledge = UnacknowledgedPackets >= AckEveryPackets;
                    }
                }
                MessageReceivedHandler(this, dataFromBuffer[8..]); // call user events
                if (acknowledge)
                {
                    SendBatchedAcknowledgement();
                }
                ModemAPIDebugger.OutputDebugMessage("delivered: " + LastReceived);
            }
        }
    }
}