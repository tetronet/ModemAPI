using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ModemAPI
{
    public class SRTPClient
    {
        private Address? CommunicatingWith = null;
        private uint ConnectionID = 0;
        private string QueryType = "";
        private IModem Modem;
        private long LastReceived = -1;
        private long SequentialPacketNumber = 0;
        private bool IsClosed = false;
        private int TicksTimeout;
        private HashSet<long> ReceivedNumbers = new HashSet<long>();
        private ConcurrentDictionary<long, byte[]> ReorderingBuffer = new ConcurrentDictionary<long, byte[]>();
        private ConcurrentDictionary<long, ReliablePacketInformation> InternalState = new ConcurrentDictionary<long, ReliablePacketInformation>();
        private long InternalPacketTimer = 0;
        private bool FailedDueToTooManyRetxAttepms = false;
        private volatile bool IsThrottled = false;
        /// <summary>
        /// Gets called for each received message by this client, and data of this message is sent to the event handler.
        /// </summary>
        public Action<SRTPClient, byte[]> OnMessageReceived = delegate { };
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
        /// How many packets can stand in the send, but unacked state.
        /// </summary>
        public int MaxUnackedPackets = 10000;
        /// <summary>
        /// How deep into the future packets will be received.
        /// </summary>
        public int MaxPacketNoFromFuture = 10000;
        /// <summary>
        /// Delay in Ticks (each tick is 100 ns) between Universal Packet Sends.
        /// </summary>
        public int TicksPacketDelay = 0;
        /// <summary>
        /// If set to true, this client will stop sending any data for 2s on each retransmitted packet to prevent tetronet from doing OHHH MYY PCCCCAEAEAE
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

        public SRTPClient(IModem modem, Address dst, string qt, uint cid, int tickTimeout, bool autoclear = true)
        {
            ConnectionID = cid;
            QueryType = qt;
            Modem = modem;
            CommunicatingWith = dst;
            TicksTimeout = tickTimeout;
            // receiver task
            Modem.AttachReceiveEventNoUnfragment(delegate (Packet packet, Action k)
            {
                if (IsClosed)
                {
                    return;
                }
                if (packet.ConnectionID != ConnectionID || packet.QueryType != QueryType)
                {
                    ModemAPIDebugger.OutputDebugMessage("SRTPClient MISMATCH CID OR QT!!!!!!!!!");
                    return;
                }
                try
                {
                    byte[] dataReceived = packet.DataBytes;
                    // parse received packet
                    if (dataReceived.Length < 8)
                    {
                        OnError(16, "Packet too short.");
                        return;
                    }
                    long id = BinaryPrimitives.ReadInt64BigEndian(dataReceived);
                    
                    if (dataReceived.Length == 8)
                    {
                        // == ack receiver ==
                        InternalState.TryRemove(id, out _);
                        OnAckReceived(id);
                    }
                    if (dataReceived.Length > 8)
                    {
                        // == enqueue packets ==
                        if (LastReceived + MaxPacketNoFromFuture < id)
                        {
                            OnError(-996, $"Packet {id} is too deep into the future.");
                            return;
                        }
                        ReorderingBuffer.TryAdd(id, dataReceived);
                    }
                }
                catch (Exception e)
                {
                    OnError(-1, "Exception: " + e.ToString());
                }
            });
            // error correction task
            _ = Task.Run(async delegate ()
            {
                while (!IsClosed)
                {
                    try
                    {
                        foreach (ReliablePacketInformation info in InternalState.Values)
                        {
                            if (info.CheckTimeout(TicksTimeout))
                            {
                                // retransmit packets
                                UnivSend(info.Data);
                                // update timeout
                                info.UpdateTimeStamp();
                                // check the counter
                                if (info.RetransmissionCounter > RetransmissionsBeforeConsideringRemoteSideDead)
                                {
                                    FailedDueToTooManyRetxAttepms = true;
                                    Close();
                                }
                                // update retx counter
                                info.RetransmissionCounter++;
                                // if enabled, pause the transmission
                                if (PacketLossBasedCongestionControl)
                                {
                                    _ = Task.Run(delegate ()
                                    {
                                        if (!IsThrottled)
                                        {
                                            IsThrottled = true;
                                            Thread.Sleep(ThrottleTime);
                                            IsThrottled = false;
                                        }
                                    });
                                }
                                // call the user event
                                OnRetransmit(info.Id);
                            }
                        }
                        await Task.Delay(100);
                    }
                    catch (Exception ex)
                    {
                        OnError(1870, "Retransmit Exception: " + ex);
                    }
                }
            });
            // reorder task
            _ = Task.Run(async delegate ()
            {
                while (!IsClosed)
                {
                    try
                    {
                        ForceReadAllAvailablePackets();
                    }
                    catch (Exception e)
                    {
                        OnError(-1, "Exception: " + e.ToString());
                    }
                    await Task.Delay(1);
                }
            });
            // autoclear task
            if (autoclear)
            {
                _ = Task.Run(async delegate ()
                {
                    while (true)
                    {
                        ClearReorderingBuffer();
                        await Task.Delay(1000);
                    }
                });
            }
        }
        public bool AreAllPacketsFromReorderingBufferIncludedInAlreadyReceived()
        {
            foreach (long pno in ReorderingBuffer.Keys)
            {
                if (!ReceivedNumbers.Contains(pno))
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
            List<long> toRemove = [];
            foreach (long pno in ReorderingBuffer.Keys)
            {
                if (ReceivedNumbers.Contains(pno))
                {
                    toRemove.Add(pno);
                }
            }
            foreach (long pno in toRemove)
            {
                ReorderingBuffer.TryRemove(pno, out _);
            }
        }
        public void Transmit(byte[] data)
        {
            if (IsClosed)
            {
                throw new InvalidOperationException("This client is in an invalid state \"Closed\". To transmit data, it must be in state \"Opened\"");
            }
            if (FailedDueToTooManyRetxAttepms)
            {
                Close();
                throw new OperationCanceledException("Client was cancelled by the retransmission thread: too many retransmission attempts.");
            }
            if (data.Length > 59992 || data.Length == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(data), "data must contain not more than 59992 bytes and not less than 1 byte");
            }
            long start = Stopwatch.GetTimestamp();
            while (InternalState.Count > MaxUnackedPackets)
            {
                OnError(-1885, "Pending for acknowledgement packets storage is full");
                if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromTicks(TransmitTimeOutInTicks))
                {
                    throw new TimeoutException("Transmit timed out. Normally this will mean that the remote side is dead and you want to Close() this SRTPClient.");
                }
                if (IsClosed)
                {
                    throw new InvalidOperationException("This client is in an invalid state \"Closed\". To transmit data, it must be in state \"Opened\"");
                }
                if (FailedDueToTooManyRetxAttepms)
                {
                    Close();
                    throw new OperationCanceledException("Client was cancelled by the retransmission thread: too many retransmission attempts.");
                }
                Thread.Sleep(100);
            }
            while (IsThrottled)
            {
                Thread.Sleep(50);
            }
            byte[] data_ = new byte[data.Length + 8];
            BinaryPrimitives.WriteInt64BigEndian(data_.AsSpan(), SequentialPacketNumber);
            data.CopyTo(data_, 8);
            InternalState.TryAdd(SequentialPacketNumber, new(SequentialPacketNumber, DateTime.Now.Ticks, data_));
            Interlocked.Increment(ref SequentialPacketNumber);
            UnivSend(data_);
        }
        public void Close()
        {
            IsClosed = true;
            ReorderingBuffer.Clear();
            ReceivedNumbers.Clear();
            InternalState.Clear();
            OnMessageReceived = delegate { };
        }
        public bool WasPacketReceived(long packno)
        {
            return ReceivedNumbers.Contains(packno);
        }
        private void UnivSend(byte[]? data)
        {
            try
            {
                while (TicksPacketDelay > Stopwatch.GetTimestamp() - InternalPacketTimer) { }
                //SpinWait.SpinUntil(delegate () { return TicksPacketDelay > Stopwatch.GetTimestamp() - InternalPacketTimer; });
                if (data == null)
                {
                    return;
                }
                if (CommunicatingWith == null)
                {
                    OnError(1048576, "This client does not communicate with any remote system.");
                    return;
                }
                Modem.LowLevelTransmit(data, CommunicatingWith, QueryType, ConnectionID);
                InternalPacketTimer = Stopwatch.GetTimestamp();
            }
            catch (Exception ex)
            {
                OnError(1800, "UNIVSEND failed: " + ex);
            }
        }
        private void ForceReadAllAvailablePackets()
        {
            while (ReorderingBuffer.TryRemove(LastReceived + 1, out byte[]? dataFromBuffer))
            {
                UnivSend(dataFromBuffer.AsSpan(0, 8).ToArray());
                ModemAPIDebugger.OutputDebugMessage($"UnivSend acknowledgement for packet {LastReceived + 1}");
                if (ReceivedNumbers.Contains(LastReceived + 1))
                {
                    ModemAPIDebugger.OutputDebugMessage("received duplicate packets");
                    return;
                }
                ReceivedNumbers.Add(LastReceived + 1);
                ModemAPIDebugger.OutputDebugMessage("added to received numbers");
                OnMessageReceived(this, dataFromBuffer[8..]); // call user events
                ModemAPIDebugger.OutputDebugMessage("executed user events");
                Interlocked.Increment(ref LastReceived); // increment LastReceived so everything will work
                ModemAPIDebugger.OutputDebugMessage("increment: " + LastReceived);
            }
        }
    }
}