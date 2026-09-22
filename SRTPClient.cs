using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class SRTPClient
    {
        private Address? CommunicatingWith = null;
        private uint ConnectionID = 0;
        private string QueryType = "";
        private bool IsFirstReceivedPacket = false;
        private IModem Modem;
        private long LastReceived = -1;
        private long SequentialPacketNumber = 0;
        private bool IsClosed = false;
        private int TicksTimeout;
        private HashSet<long> ReceivedNumbers = new HashSet<long>();
        private ConcurrentDictionary<long, byte[]> ReorderingBuffer = new ConcurrentDictionary<long, byte[]>();
        private ConcurrentDictionary<long, ReliablePacketInformation> InternalState = new ConcurrentDictionary<long, ReliablePacketInformation>();
        private long InternalPacketTimer = 0;

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
        /// Gets called for each acknowledged packet, and Sequential ID is sent to the event handler.
        /// </summary>
        public Action<long> OnAckReceived = delegate { };
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
        /// Gets the remote address, which this SRTP client is currently communicating with.
        /// </summary>
        public Address RemoteServer { get { return Address.Copy(CommunicatingWith); } }
        public uint EndPointConnectionID { get { return ConnectionID; } }
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
                if (packet.ConnectionID != ConnectionID || packet.QueryType != QueryType)
                {
                    return;
                }
                try
                {
                    byte[] dataReceived = packet.DataBytes;
                    // check IP end point
                    if (!IsFirstReceivedPacket)
                    {
                        IsFirstReceivedPacket = true;
                    }
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
                    foreach (ReliablePacketInformation info in InternalState.Values)
                    {
                        if (info.CheckTimeout(TicksTimeout))
                        {
                            // retransmit packets
                            UnivSend(info.Data);
                            // update timeout
                            info.UpdateTimeStamp();
                            // call the user event
                            OnRetransmit(info.Id);
                        }
                    }
                    await Task.Delay(100);
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
            if (data.Length > 59992 || data.Length == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(data), "data must contain not more than 59992 bytes and not less than 1 byte");
            }
            while (InternalState.Count > MaxUnackedPackets)
            {
                Thread.Sleep(1);
            }
            byte[] data_ = new byte[data.Length + 8];
            BinaryPrimitives.WriteInt64BigEndian(data_.AsSpan(), SequentialPacketNumber);
            data.CopyTo(data_, 8);
            InternalState.TryAdd(SequentialPacketNumber, new ReliablePacketInformation(SequentialPacketNumber, DateTime.Now.Ticks, data_));
            Interlocked.Increment(ref SequentialPacketNumber);
            UnivSend(data_);
        }
        public void Close()
        {
            IsClosed = true;
            InternalState.Clear();
        }
        public bool WasPacketReceived(long packno)
        {
            return ReceivedNumbers.Contains(packno);
        }
        private void UnivSend(byte[]? data)
        {
            while (TicksPacketDelay > DateTime.Now.Ticks - InternalPacketTimer) { }
            if (data == null)
            {
                return;
            }
            if (CommunicatingWith == null)
            {
                OnError(1048576, "This client does not communicate with any remote system.");
                return;
            }
            Modem.Transmit(data, CommunicatingWith, QueryType, ConnectionID, packetSize: 60000);
            InternalPacketTimer = DateTime.Now.Ticks;
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
        private static bool CheckIPEndPointEquality(IPEndPoint a, IPEndPoint b)
        {
            return a.Address.Equals(b.Address) && a.Port.Equals(b.Port);
        }
    }
}