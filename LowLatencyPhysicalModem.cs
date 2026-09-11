using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
        private bool CurrentlyWritingPacket = false;
        private readonly Lock WriterLock = new();
        
        public int MaxReinitializeAttempts { get; set; }
        public Address? LocalModemAddress { get; set; }
        public LargeMessage? LastDownloadedLargeMessage { get; set; }
        public bool ConnectAsAnAddressMachine { get; set; }
        public int PacketMTU { get; private set; }
        public ulong LargeMessageMTU { get; private set; }
        public bool CanExchangeLargeMessages { get; private set; }
        public bool IsModemConnected { get; set; }
        /// <summary>
        /// Allow this modem to drop packets once the serial port is overloaded.
        /// </summary>
        public bool DropPacketsWhenOverloaded { get; set; }
        /// <summary>
        /// Gets invoked if any data gets dropped by the underlying Level One client, and argument will contain exact count of dropped bytes
        /// </summary>
        public event Action<L1DropBytesReasons, int> L1DroppedData = delegate { };
        /// <summary>
        /// Gets invoked if some shit happens in the modem.
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

            private void TcpReader()
            {
                while (true)
                {
                    if (tcp == null)
                    {
                        throw new NullTransmitterException("tcp");
                    }
                    if (!tcp.Connected)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    try
                    {
                        byte[] buffer = new byte[tcp.Available];
                        int read = tcp.GetStream().Read(buffer, 0, buffer.Length);
                        if (read > 0)
                        {
                            ModemAPIDebugger.OutputDebugMessage($"low latency physical modem received data from tcp");
                            L1RxBuffer.AddRange(buffer.Take(read));
                        }
                        else
                        {
                            ModemAPIDebugger.OutputDebugMessage($"low latency physical modem was not able to read data from tcp");
                        }
                        
                        if (L1RxBuffer.Count < 3)
                        {
                            // not enough bytes
                            ModemAPIDebugger.OutputDebugMessage("not enough bytes");
                            continue;
                        }
                        int length =
                            L1RxBuffer[0] << 16 |
                            L1RxBuffer[1] << 8 |
                            L1RxBuffer[2];
                        if (L1RxBuffer.Count >= length + 3)
                        {
                            byte[] data = [.. L1RxBuffer.Skip(3).Take(length)];
                            L1RxBuffer.RemoveRange(0, length + 3);
                            CallAllEvents(data, delegate () { throw new NotImplementedException(); });
                        }
                    }
                    catch
                    {
                        ModemAPIDebugger.OutputDebugMessage("error during tcp read");
                    }
                }
            }
            private void UdpReader()
            {
                while (true)
                {
                    if (udp == null)
                    {
                        throw new NullTransmitterException("udp");
                    }
                    IPEndPoint? ep = null;
                    L1RxBuffer.AddRange(udp.Receive(ref ep));
                    ModemAPIDebugger.OutputDebugMessage($"low latency physical modem received a datagram from {ep}");
                    int length = 
                        L1RxBuffer[0] << 16 &
                        L1RxBuffer[1] << 8 &
                        L1RxBuffer[2];
                    if (L1RxBuffer.Count > length + 3)
                    {
                        byte[] data = [.. L1RxBuffer.Skip(3).Take(length)];
                        L1RxBuffer.RemoveRange(0, length + 3);
                        CallAllEvents(data, delegate () { throw new NotImplementedException(); });
                    }
                }
            }

            private void CallAllEvents(byte[] a, Action b)
            {
                foreach (Action<byte[], Action> action in readEvents)
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
                        if (data.Length > 16777215)
                        {
                            throw new ArgumentOutOfRangeException(nameof(data), "buffer bigger than 16777215 bytes");
                        }
                        if (tcp == null)
                        {
                            throw new NullTransmitterException("tcp");
                        }
                        // convert length of the buffer to bytes
                        byte hi = (byte)(data.Length & 0xFF0000 >> 16);
                        byte mid = (byte)(data.Length & 0xFF00 >> 8);
                        byte lo = (byte)(data.Length & 0xFF);
                        // send these bytes
                        tcp.GetStream().WriteByte(hi);
                        tcp.GetStream().WriteByte(mid);
                        tcp.GetStream().WriteByte(lo);
                        tcp.GetStream().Write(data, 0, data.Length);
                    }
                    if (t == L1Types.L1_INET_UDP)
                    {
                        if (data.Length > 16777215)
                        {
                            throw new ArgumentOutOfRangeException(nameof(data), "buffer bigger than 16777215 bytes");
                        }
                        if (udp == null)
                        {
                            throw new NullTransmitterException("udp");
                        }
                        // convert length of the buffer to bytes
                        byte hi = (byte)((data.Length & 0xFF0000) >> 16);
                        byte mid = (byte)((data.Length & 0x00FF00) >> 8);
                        byte lo = (byte)(data.Length & 0x0000FF);
                        // create buffer for sending to the UDP
                        byte[] bytes = new byte[3 + data.Length];
                        // write length of this buffer
                        bytes[0] = hi;
                        bytes[1] = mid;
                        bytes[2] = lo;
                        // copy data to bytes
                        data.CopyTo(bytes, 3);
                        // send buffer as multiple datagrams
                        for (int i = 0; i < bytes.Length; i += 1024)
                        {
                            udp.Send(bytes.Skip(i).Take(1024).ToArray());
                        }
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
                        Task.Run(TcpReader);
                    }
                }
                if (t == L1Types.L1_INET_UDP)
                {
                    readEvents.Add(onSuccessfulRead); // add event to the list of events
                    if (!subscribedForReadingFromUdp)
                    {
                        Task.Run(UdpReader);
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
            }
            else if (wt == L1Types.L1_INET_UDP)
            {
                UdpClient client = new();
                client.Connect(IPAddress.Parse(wire.Split(':')[0]), int.Parse(wire.Split(':')[1]));
                client.DontFragment = true;
                Communicator = new(null, client, null);
            }
            else
            {
                throw new NotSupportedException("L1 type not supported");
            }
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
                client.DontFragment = true;
                Communicator = new(null, client, null);
            }
            else
            {
                throw new NotSupportedException("L1 type not supported");
            }
        }

        public void Dial()
        {
            // init
            Communicator.Init(L1Types.L1_SERIAL);
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
        public void Transmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0)
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
                    PacketNo = (byte)(i / packetSize),
                    MessageId = mid
                };
                Transmit(packet);
                Thread.Sleep(delay);
            }
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
        public void Transmit(string data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0)
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
                    PacketNo = (byte)(i / packetSize),
                    MessageId = mid
                };
                //Console.WriteLine("the value at line 258 is : " + (i + packetSize) + " and packet estimated packsize will be : " + packetSize);
                //Console.WriteLine("the value at line 259 is : " + new string(data.ToCharArray().Skip(i).Take(packetSize).ToArray()));
                Transmit(packet);
                Thread.Sleep(delay);
            }
        }

        internal void Transmit(Packet packet)
        {
            lock (WriterLock)
            {
                if (!CurrentlyWritingPacket)
                {
                    CurrentlyWritingPacket = true;
                    Task.Run(() => { Communicator.Write(PacketConverter.PacketToBytes(packet)); CurrentlyWritingPacket = false; });
                }
            }
        }

        public void AttachReceiveEvent(Action<DataBlock, Action> onReceive)
        {
            AttachReceiveEventNoUnfragment(delegate (Packet p, Action k)
            {
                Assembler.AddPacket(p);
                DataBlock? possibleAssembledPacketQueue = Assembler.GetPacketQueue(p.MessageId).Assemble();
                if (possibleAssembledPacketQueue != null)
                {
                    onReceive(possibleAssembledPacketQueue, k);
                }
            });
        }

        public void AttachReceiveEventNoUnfragment(Action<Packet, Action> onReceive)
        {
            Communicator.OnDataRead(delegate (byte[] b, Action k)
            {
                if (PacketConverter.CheckIsPacket(b))
                {
                    Packet p = PacketConverter.BytesToPacket(b);
                    if (p.IsErrorWhileReading)
                    {
                        ModemAPIDebugger.OutputDebugMessage("low latency modem received a corrupted packet");
                        CallAllEventsOfCrc();
                        return;
                    }
                    onReceive(p, k);
                }
            });
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
