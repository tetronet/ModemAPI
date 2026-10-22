using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class LowLatencyPhysicalModem : IModem, ICorruptionAwareModem
    {
        private PNetworkCommunicator Communicator;
        private L1Types LevelOneWireType;
        private long PingInterval;
        private List<Action> CRCMismatchEvents = [];
        private PacketQueueAssembler Assembler = new(1000000, 600000000); // 1 million packet queues at the same time, 60 seconds timeout
        private readonly Lock WriterLock = new();
        private readonly Channel<Packet> TransmissionQueue =
            Channel.CreateBounded<Packet>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        private volatile bool TxLoopWorking = false;
        private long LocallyDroppedPackets = 0;
        private readonly SubscriberList<Packet> PacketSubscribers = new();
        private readonly SubscriberList<DataBlock> DataBlockSubscribers = new();
        private readonly Lock PacketDispatcherLock = new();
        private bool PacketDispatcherAttached = false;
        private bool AssemblerAttached = false;

        public int MaxReinitializeAttempts { get; set; }
        /// <summary>
        /// Local tetronet address for this modem.
        /// </summary>
        public Address? LocalModemAddress { get; set; }
        /// <summary>
        /// Last downloaded <see cref="LargeMessage"/> by this <see cref="LowLatencyPhysicalModem"/> instance or null if there's no one.
        /// </summary>
        [Obsolete("Large messages in the tetronet must get replaced by LMDTP transfers")]
        public LargeMessage? LastDownloadedLargeMessage { get; set; }
        /// <summary>
        /// Connect this modem as an address machine.
        /// </summary>
        public bool ConnectAsAnAddressMachine { get; set; }
        /// <summary>
        /// MTU for the Packets
        /// </summary>
        public int PacketMTU { get; private set; }
        /// <summary>
        /// MTU for the Large Messages
        /// </summary>
        public ulong LargeMessageMTU { get; private set; }
        public bool CanExchangeLargeMessages { get; private set; }
        /// <summary>
        /// Counts packets dropped because the transmission queue was full and <see cref="DropPacketsWhenOverloaded"/> allowed it.
        /// </summary>
        public long LocallyDroppedPacketsCounter { get { return Interlocked.Read(ref LocallyDroppedPackets); } }
        /// <summary>
        /// True if this <see cref="LowLatencyPhysicalModem"/> is connected, and false if not.
        /// </summary>
        public bool IsModemConnected { get; set; }
        /// <summary>
        /// Allow this modem to drop packets once the line is overloaded (the transmission queue holds 1024 packets): a dropped packet
        /// is counted in <see cref="LocallyDroppedPacketsCounter"/>. True by default: SRTP retransmits what got lost, and LMDTP and NLMNP
        /// time out or repeat. When false, transmitting waits for space instead; then nothing may transmit from a receive event of this
        /// modem (SRTP acknowledgements, LMDTP and NLMNP do), because the waiting reader thread can deadlock two busy modems.
        /// </summary>
        public bool DropPacketsWhenOverloaded { get; set; } = true;
        /// <summary>
        /// Gets invoked if any data gets dropped by the underlying Level One client, and argument will contain exact count of dropped bytes
        /// </summary>
        public event Action<L1DropBytesReasons, int> L1DroppedData = delegate { };
        /// <summary>
        /// Gets invoked when an error happens in the modem.
        /// </summary>
        public event Action<Exception, ErrorEmitter> InternalErrorHappened = delegate { };

        private class PNetworkCommunicator(TcpClient? tcp, UdpClient? udp, ISerialPacket? sp)
        {
            private TcpClient? tcp = tcp;
            private UdpClient? udp = udp;
            public ISerialPacket? sp = sp;
            private bool subscribedForReadingFromTcp = false;
            private bool subscribedForReadingFromUdp = false;
            List<Action<byte[], Action>> readEvents = [];
            private bool isInitialized = false;
            private L1Types t;
            private List<byte> L1RxBuffer = [];
            private object _writeLock = new();
            public Action<Exception> ErrorReported = delegate { };

            private void TcpReader()
            {
                byte[] buffer = new byte[65536];
                while (true)
                {
                    if (tcp == null)
                    {
                        ErrorReported(new NullTransmitterException("tcp"));
                        return;
                    }
                    if (!tcp.Connected)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    try
                    {
                        // blocks until data arrives, so the thread doesn't spin while the line is idle
                        int read = tcp.GetStream().Read(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            // the remote side closed the connection; this modem doesn't reconnect, so the reader is done
                            ErrorReported(new EndOfStreamException("the remote side closed the TCP connection"));
                            return;
                        }
                        ModemAPIDebugger.OutputDebugMessage($"low latency physical modem received data from tcp");
                        L1RxBuffer.AddRange(buffer.AsSpan(0, read));
                        // one read may carry several frames, all of them must be handled before blocking again
                        L1Framing.ExtractFrames(L1RxBuffer, data => CallAllEvents(data, delegate () { throw new NotImplementedException(); }), ErrorReported);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        ErrorReported(e);
                        Thread.Sleep(50);
                    }
                }
            }
            private void UdpReader()
            {
                if (udp == null)
                {
                    ErrorReported(new NullTransmitterException("udp"));
                    return;
                }
                while (true)
                {
                    byte[] datagram;
                    try
                    {
                        IPEndPoint? ep = null;
                        // one datagram is one whole packet: a lost, repeated or reordered datagram affects only itself
                        datagram = udp.Receive(ref ep);
                        ModemAPIDebugger.OutputDebugMessage($"low latency physical modem received a datagram from {ep}");
                    }
                    catch (ObjectDisposedException)
                    {
                        // the socket was closed, there is nothing to read anymore
                        return;
                    }
                    catch (Exception e)
                    {
                        // for example ICMP "port unreachable" of an earlier datagram; the socket still works
                        ErrorReported(e);
                        continue;
                    }
                    try
                    {
                        CallAllEvents(datagram, delegate () { throw new NotImplementedException(); });
                    }
                    catch (Exception e)
                    {
                        ErrorReported(e);
                    }
                }
            }

            private void CallAllEvents(byte[] a, Action b)
            {
                foreach (Action<byte[], Action> action in readEvents.ToArray()) // handlers may be added while the reader thread is running
                {
                    action(a, b);
                }
            }

            public void Init(L1Types t)
            {
                if (isInitialized)
                {
                    throw new InvalidOperationException("already initialized");
                }
                this.t = t;
                isInitialized = true;
            }

            /// <summary>
            /// Internal function for sending bytes.
            /// </summary>
            /// <param name="data">Data for sending.</param>
            public void Write(byte[] data)
            {
                lock (_writeLock)
                {
                    if (t == L1Types.L1_SERIAL)
                    {
                        if (sp == null)
                        {
                            throw new NullTransmitterException("sp");
                        }
                        sp.TransmitBuffer(data);
                    }
                    if (t == L1Types.L1_INET_TCP)
                    {
                        if (tcp == null)
                        {
                            throw new NullTransmitterException("tcp");
                        }
                        tcp.GetStream().Write(L1Framing.Frame(data));
                    }
                    if (t == L1Types.L1_INET_UDP)
                    {
                        if (udp == null)
                        {
                            throw new NullTransmitterException("udp");
                        }
                        if (data.Length > L1Framing.MaxDatagramLength)
                        {
                            throw new ArgumentOutOfRangeException(nameof(data), $"a packet sent over UDP must not be longer than {L1Framing.MaxDatagramLength} bytes");
                        }
                        // the whole packet is one datagram, without a length header: UDP keeps datagrams whole, and losing one loses only that packet
                        udp.Send(data);
                    }
                }
            }
            /// <summary>
            /// Internal function for adding a on data read event.
            /// </summary>
            /// <param name="onSuccessfulRead">Callback when a successfull read occured.</param>
            public void OnDataRead(Action<byte[], Action> onSuccessfulRead)
            {
                if (t == L1Types.L1_SERIAL)
                {
                    if (sp == null)
                    {
                        throw new NullTransmitterException("sp");
                    }
                    sp.OnBufferReceived(onSuccessfulRead);
                }
                if (t == L1Types.L1_INET_TCP)
                {
                    readEvents.Add(onSuccessfulRead); // add event to the list of events
                    if (!subscribedForReadingFromTcp)
                    {
                        subscribedForReadingFromTcp = true;
                        Task.Factory.StartNew(TcpReader, TaskCreationOptions.LongRunning);
                    }
                }
                if (t == L1Types.L1_INET_UDP)
                {
                    readEvents.Add(onSuccessfulRead); // add event to the list of events
                    if (!subscribedForReadingFromUdp)
                    {
                        subscribedForReadingFromUdp = true;
                        Task.Factory.StartNew(UdpReader, TaskCreationOptions.LongRunning);
                    }
                }
            }
        }
        /// <summary>
        /// Don't use this constructor.
        /// </summary>
        /// <param name="wire"></param>
        /// <param name="speed"></param>
        /// <param name="wt"></param>
        /// <param name="atCommands"></param>
        /// <param name="afterAtCommandDelay"></param>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Dial-up modems are very obsolete, and this constructor doesn't support a lot of stuff, use the other one")]
        public LowLatencyPhysicalModem(string wire, int speed, L1Types wt, string[] atCommands, int afterAtCommandDelay)
        {
            LevelOneWireType = wt;
            if (wt == L1Types.L1_SERIAL)
            {
                SerialPort port = new(wire, speed);
                port.Open();
                if (atCommands.Length != 0)
                {
                    foreach (string atCommand in atCommands)
                    {
                        port.WriteLine(atCommand);
                        Thread.Sleep(250);
                    }
                    Thread.Sleep(afterAtCommandDelay);
                }
                SerialPacket p = new(port);
                p.OnDataDropped += delegate (L1DropBytesReasons reason, int count)
                {
                    L1DroppedData(reason, count);
                };
                Communicator = new(null, null, p);
            }
            else if (wt == L1Types.L1_INET_TCP)
            {
                TcpClient client = new();
                client.Connect(IPAddress.Parse(wire.Split(':')[0]), int.Parse(wire.Split(':')[1]));
                Communicator = new(client, null, null);
                UsesRawTcp = true;
            }
            else if (wt == L1Types.L1_INET_UDP)
            {
                UdpClient client = new();
                client.Connect(IPAddress.Parse(wire.Split(':')[0]), int.Parse(wire.Split(':')[1]));
                Communicator = new(null, client, null);
            }
            else
            {
                throw new NotSupportedException("L1 type not supported");
            }
            InitCommunicator(wt == L1Types.L1_INET_TCP && !UsesRawTcp ? L1Types.L1_SERIAL : wt);
        }
        /// <summary>
        /// Initializes a LowLatencyPhysicalModem instance.
        /// </summary>
        /// <param name="wire">Wire name</param>
        /// <param name="speed">Bitrate</param>
        /// <param name="wt">Wire Type, e.g. serial, TCP or UDP (last one currenly isn't supported)</param>
        /// <param name="useMoreStableSerialPacket">Use MSSP instead of classic SerialPacket</param>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="NotSupportedException"></exception>
        public LowLatencyPhysicalModem(string wire, int speed, L1Types wt, bool useMoreStableSerialPacket = false)
        {
            LevelOneWireType = wt;
            if (wt == L1Types.L1_SERIAL)
            {
                SerialPort port = new(wire, speed);
                port.ReadBufferSize = 2097152;
                port.Open();
                ISerialPacket p;
                if (!useMoreStableSerialPacket)
                {
                    SerialPacket p_ = new(port);
                    p_.OnDataDropped += delegate (L1DropBytesReasons reason, int count)
                    {
                        L1DroppedData(reason, count);
                    };
                    p = p_;
                }
                else
                {
                    MoreStableSerialPacket p_ = new(port.BaseStream);
                    p_.OnDataDropped += delegate (L1DropBytesReasons reason, int count)
                    {
                        L1DroppedData(reason, count);
                    };
                    p_.MalformedPacketReceived += CallAllEventsOfCrc;
                    p_.InternalErrorHappened += InternalErrorHappened;
                    p = p_;
                }
                Communicator = new(null, null, p);
            }
            else if (wt == L1Types.L1_INET_TCP)
            {
                TcpClient client = new();
                client.Connect(IPAddress.Parse(wire), speed);
                if (!useMoreStableSerialPacket)
                {
                    throw new InvalidOperationException("you must use more stable serial packet to use transmission control protocol over internet protocol as the physical layer for the tetronet");
                }
                MoreStableSerialPacket mssp = new(client.GetStream());
                mssp.OnDataDropped += delegate (L1DropBytesReasons reason, int count)
                {
                    L1DroppedData(reason, count);
                };
                mssp.MalformedPacketReceived += CallAllEventsOfCrc;
                mssp.InternalErrorHappened += InternalErrorHappened;
                Communicator = new(null, null, mssp);
            }
            else if (wt == L1Types.L1_INET_UDP)
            {
                UdpClient client = new();
                client.Connect(IPAddress.Parse(wire.Split(':')[0]), int.Parse(wire.Split(':')[1]));
                Communicator = new(null, client, null);
            }
            else
            {
                throw new NotSupportedException("L1 type not supported");
            }
            InitCommunicator(wt == L1Types.L1_INET_TCP && !UsesRawTcp ? L1Types.L1_SERIAL : wt);
        }

        private bool UsesRawTcp = false;
        private void InitCommunicator(L1Types wireType)
        {
            Communicator.Init(wireType);
            Communicator.ErrorReported = delegate (Exception e)
            {
                if (e is EndOfStreamException)
                {
                    // the remote side closed the connection, and this modem doesn't reconnect
                    IsModemConnected = false;
                }
                InternalErrorHappened(e, ErrorEmitter.LowLatencyPhysicalModem);
            };
        }
        public void Dial()
        {
            // attach response handler
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                if (IsModemConnected)
                {
                    return;
                }
                CIoCILLowLatShortResponseBody response = PacketConverter.ParseLowLatencyConnectionResponse(d);
                if (response.IsResponseDamaged)
                {
                    throw new ConnectionFailedException("damaged response");
                }
                if (response.ContainsSMLA)
                {
                    LocalModemAddress = Address.Copy(response.PossibleSMLA ?? throw new NullAddressException("no SMLA address found"));
                }
                if (response.SpecifiesPacketMTU)
                {
                    PacketMTU = response.PossiblePacketMTU ?? throw new NullReferenceException("mtu null");
                }
                if (response.SpecifiesLargeMessageMTU)
                {
                    LargeMessageMTU = response.PossibleLargeMessageMTU ?? throw new NullReferenceException("mtu null");
                }
                PingInterval = (long)(response.PossibleConnectionTimeoutInNanoseconds ?? throw new NullReferenceException("timeout")) / 3;
                CanExchangeLargeMessages = response.RemoteAddressMachineSupportsLargeMessageOperation;
                IsModemConnected = true;
            });
            // send connection request
            LocalModemAddress ??= new Address();
            LocalModemAddress.AddressValue ??= "0";
            List<byte> conrequest = [.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixRequest];
            conrequest.Add((byte)LocalModemAddress.AddressValue.Length);
            conrequest.AddRange(Encoding.UTF8.GetBytes(LocalModemAddress.AddressValue));
            if (ConnectAsAnAddressMachine)
            {
                conrequest.Add(0);
            }
            else
            {
                conrequest.Add(255);
            }
            Span<byte> crc32bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc32bytes, Crc32.HashToUInt32(conrequest.ToArray()));
            conrequest.AddRange(crc32bytes);
            Communicator.Write([.. conrequest]);
        }

        public void Drop(bool carefulMode)
        {
            // attach response handler
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                if (d.SequenceEqual(CIoCILLowLatShortDefaultPhrases.DisconnectionResponse))
                {
                    IsModemConnected = false;
                }
            });
            // send the request
            Communicator.Write(CIoCILLowLatShortDefaultPhrases.DisconnectionRequest);
        }

        /// <summary>
        /// Transmits a packet or a packet queue depending on Packet Size and length of data.
        /// </summary>
        /// <param name="data">Data to transmit</param>
        /// <param name="address">Destination address</param>
        /// <param name="queryType">Query type</param>
        /// <param name="connectionId">Tetronet port</param>
        /// <param name="metadata">Metadata (doesn't work for some reason)</param>
        /// <param name="packetSize">Max amount of bytes in a single packet in a packet queue</param>
        /// <param name="delay">Delay between transmitting each packet in the queue, useful for overloaded/slow network</param>
        /// <exception cref="NullAddressException">Happens when transmitting modem does not have any tetronet address</exception>
        public void Transmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0, ushort ttl = 65535)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("to transmit, receive or exchange data, modem must have a local address");
            }
            ulong mid = (ulong)Random.Shared.NextInt64();
            for (int i = 0; i < data.Length; i += packetSize)
            {
                //Console.WriteLine("at line 223 iterator is : " + i);
                Packet packet = new()
                {
                    DataBytes = [.. data.Skip(i).Take(packetSize)],
                    QueryType = queryType,
                    ConnectionID = connectionId,
                    Metadata = metadata,
                    IsLastInSequence = (i / packetSize) == (Math.Ceiling((double)data.Length / packetSize) - 1),
                    Receiver = address,
                    Transmitter = LocalModemAddress,
                    PacketNo = (ulong)(i / packetSize),
                    MessageId = mid,
                    RoutersToPass = ttl
                };
                Transmit(packet);
                Thread.Sleep(delay);
            }
        }

        public void LowLevelTransmit(byte[] data, Address address, string qt, uint cid, string? metadata, ushort ttl = 65535)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException();
            }
            Packet packet = new();
            packet.PacketNo = 0;
            packet.MessageId = (ulong)Random.Shared.NextInt64();
            packet.Transmitter = LocalModemAddress;
            packet.QueryType = qt;
            packet.Receiver = address;
            packet.ConnectionID = cid;
            packet.Metadata = metadata;
            packet.DataBytes = data;
            packet.IsLastInSequence = true;
            packet.RoutersToPass = ttl;
            Transmit(packet);
        }

        /// <summary>
        /// Transmits a packet or a packet queue depending on Packet Size and length of data.
        /// </summary>
        /// <param name="data">Data to transmit</param>
        /// <param name="address">Destination address</param>
        /// <param name="queryType">Query type</param>
        /// <param name="connectionId">Tetronet port</param>
        /// <param name="metadata">Metadata (doesn't work for some reason)</param>
        /// <param name="packetSize">Max amount of bytes in a single packet in a packet queue</param>
        /// <param name="delay">Delay between transmitting each packet in the queue, useful for overloaded/slow network</param>
        /// <exception cref="NullAddressException">Happens when transmitting modem does not have any tetronet address</exception>
        public void Transmit(string data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0, ushort ttl = 65535)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("to transmit, receive or exchange data, modem must have a local address");
            }
            ulong mid = (ulong)Random.Shared.NextInt64();
            for (int i = 0; i < data.Length; i += packetSize)
            {
                //Console.WriteLine("at line 247 iterator is : " + i);
                Packet? packet = new()
                {
                    DataBytes = [.. Encoding.UTF8.GetBytes(new string([.. data.ToCharArray().Skip(i).Take(packetSize)]))],
                    QueryType = queryType,
                    ConnectionID = connectionId,
                    Metadata = metadata,
                    IsLastInSequence = (i / packetSize) == (Math.Ceiling((double)data.Length / packetSize) - 1),
                    Receiver = address,
                    Transmitter = LocalModemAddress,
                    PacketNo = (ulong)(i / packetSize),
                    MessageId = mid,
                    RoutersToPass = ttl
                };
                //Console.WriteLine("the value at line 258 is : " + (i + packetSize) + " and packet estimated packsize will be : " + packetSize);
                //Console.WriteLine("the value at line 259 is : " + new string(data.ToCharArray().Skip(i).Take(packetSize).ToArray()));
                Transmit(packet);
                Thread.Sleep(delay);
            }
        }

        internal void Transmit(Packet packet)
        {
            // the queue is bounded: when the line can't keep up, either the caller waits for space (the default), or the packet
            // is dropped and counted, if DropPacketsWhenOverloaded allows that
            if (!TransmissionQueue.Writer.TryWrite(packet))
            {
                if (DropPacketsWhenOverloaded)
                {
                    Interlocked.Increment(ref LocallyDroppedPackets);
                    return;
                }
                TransmissionQueue.Writer.WriteAsync(packet).AsTask().GetAwaiter().GetResult();
            }
            if (TxLoopWorking)
            {
                return;
            }
            lock (WriterLock)
            {
                if (TxLoopWorking)
                {
                    return;
                }
                TxLoopWorking = true;
                // one sender thread for the whole life of the modem (writing to the line blocks, so it gets a thread of its own
                // instead of a thread pool one): a write error is reported and the next packet is sent anyway
                new Thread(delegate ()
                {
                    while (TransmissionQueue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    {
                        while (TransmissionQueue.Reader.TryRead(out Packet? thePacket))
                        {
                            try
                            {
                                Communicator.Write(PacketConverter.PacketToBytes(thePacket));
                            }
                            catch (Exception ex)
                            {
                                InternalErrorHappened(ex, ErrorEmitter.LowLatencyPhysicalModem);
                            }
                        }
                    }
                })
                { IsBackground = true, Name = "LowLatencyPhysicalModem sender" }.Start();
            }
        }

        public void AttachReceiveEvent(Action<DataBlock, Action> onReceive)
        {
            DataBlockSubscribers.Add(onReceive);
            lock (PacketDispatcherLock)
            {
                if (AssemblerAttached)
                {
                    return;
                }
                AssemblerAttached = true;
            }
            // one assembler for the whole modem, so every subscriber gets each assembled data block
            AttachReceiveEventNoUnfragment(AssemblePacket);
        }

        public void DetachReceiveEvent(Action<DataBlock, Action> onReceive)
        {
            DataBlockSubscribers.Remove(onReceive);
        }

        private void AssemblePacket(Packet p, Action k)
        {
            if (DataBlockSubscribers.Count == 0)
            {
                return;
            }
            Assembler.AddPacket(p);
            DataBlock? possibleAssembledPacketQueue = Assembler.GetPacketQueue(p.Transmitter, p.MessageId)?.Assemble();
            // only the call that removed the queue delivers it, so a data block is never delivered twice
            if (possibleAssembledPacketQueue != null && Assembler.RemovePacketQueue(p.Transmitter, p.MessageId))
            {
                DataBlockSubscribers.Invoke(possibleAssembledPacketQueue, k);
            }
        }

        public void AttachReceiveEventNoUnfragment(Action<Packet, Action> onReceive)
        {
            PacketSubscribers.Add(onReceive);
            lock (PacketDispatcherLock)
            {
                if (!PacketDispatcherAttached)
                {
                    // one handler parses each received packet once and hands it to every subscriber
                    Communicator.OnDataRead(DispatchPacket);
                    PacketDispatcherAttached = true;
                }
            }
        }

        public void DetachReceiveEventNoUnfragment(Action<Packet, Action> onReceive)
        {
            PacketSubscribers.Remove(onReceive);
        }

        private void DispatchPacket(byte[] b, Action k)
        {
            if (PacketSubscribers.Count == 0 || !PacketConverter.CheckIsPacket(b))
            {
                return;
            }
            Packet p = PacketConverter.BytesToPacket(b);
            if (p.IsErrorWhileReading)
            {
                ModemAPIDebugger.OutputDebugMessage("low latency modem received a corrupted packet");
                if (p.ReadError == PacketReadError.CrcMismatch)
                {
                    CallAllEventsOfCrc();
                }
                else
                {
                    // a packet with a correct CRC, but a wrong structure: not damage on the line, so it isn't reported as a CRC mismatch
                    InternalErrorHappened(new InvalidDataException($"received a packet, that can't be read: {p.ReadError}"), ErrorEmitter.LowLatencyPhysicalModem);
                }
                return;
            }
            PacketSubscribers.Invoke(p, k);
        }

        public bool IsAddressSMLA()
        {
            throw new NotImplementedException();
        }

        public Task GetLargeMessage(Address transmitter)
        {
            throw new NotImplementedException();
        }

        public Task GetLargeMessage(Address tx, Address rx)
        {
            throw new NotImplementedException();
        }

        public Task GetLargeMessage(TransmitterReceiverPair txrxpair)
        {
            throw new NotImplementedException();
        }

        public Task DeleteLargeMessage(Address transmitter)
        {
            throw new NotImplementedException();
        }

        public Task<int> PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string? metadata = null)
        {
            throw new NotImplementedException();
        }

        public Task DeleteLargeMessage(Address tx, Address rx)
        {
            throw new NotImplementedException();
        }

        public Task DeleteLargeMessage(TransmitterReceiverPair txrxpair)
        {
            throw new NotImplementedException();
        }

        public void AttachCRCMismatchEvent(Action evt)
        {
            CRCMismatchEvents.Add(evt);
        }
        public Stream? RetrieveUnderlyingPhy()
        {
            return Communicator.sp?.UnderlyingStream;
        }
        public void SwapSerialPacketizer(ISerialPacket newSerialPacket)
        {
            Communicator.sp = newSerialPacket;
        }
        private void CallAllEventsOfCrc()
        {
            Task.Run(delegate ()
            {
                foreach (Action action in CRCMismatchEvents)
                {
                    action();
                }
            });
        }
    }
}
