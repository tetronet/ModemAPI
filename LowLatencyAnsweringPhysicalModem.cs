using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class LowLatencyAnsweringPhysicalModem : IAnsweringModem, ICorruptionAwareModem
    {
        private L1Types LevelOneWireType;
        private PNetworkCommunicator Communicator;
        private string LargeMessagesDirectory = "";
        internal string ModemName = "";
        private bool AllowLargeMessageOperations = true;
        private int PacketMaximumTransmissionUnit = 60000;
        private ulong LargeMessageMaximumTransmissionUnit = ulong.MaxValue;
        public bool HasPendingConnection = false;
        public bool HasActiveConnection = false;
        private CIoCILLowLatShortConnectionRequestBody PendingConnectionInformation;
        private List<Action> EventsForIncomingConnection = [];
        private List<Action> EventsForAnsweredConnection = [];
        private Func<string, Address>? SMLARequest;
        private Address ClientAddress = new();
        private ulong TimeoutNs = 0;
        private List<Action> CRCMismatchEvents = [];
        private readonly Channel<Packet> TransmissionQueue =
            Channel.CreateBounded<Packet>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        private volatile bool TxLoopWorking = false;
        private long LocallyDroppedPackets = 0;
        /// <summary>
        /// Allow this modem to drop packets for its subscriber once the line is overloaded (the transmission queue holds 1024 packets):
        /// a dropped packet is counted in <see cref="LocallyDroppedPacketsCounter"/>. True by default, because the address machine
        /// forwards packets from one thread for all of its lines, and one slow line must not hold up the others; when false, forwarding waits for space.
        /// </summary>
        public bool DropPacketsWhenOverloaded { get; set; } = true;
        /// <summary>
        /// Counts packets for the subscriber dropped because the transmission queue was full.
        /// </summary>
        public long LocallyDroppedPacketsCounter { get { return Interlocked.Read(ref LocallyDroppedPackets); } }

        private readonly Lock WriterLock = new();
        /// <summary>
        /// Gets invoked when an error happens in the modem.
        /// </summary>
        public event Action<Exception, ErrorEmitter> InternalErrorHappened = delegate { };

        /// <summary>
        /// Gets invoked if any data gets dropped by the underlying Level One client, and argument will contain exact count of dropped bytes
        /// </summary>
        public event Action<L1DropBytesReasons, int> L1DroppedData = delegate { };
        private class PNetworkCommunicator
        {
            private TcpListener? tcp;
            private TcpClient? session;
            private UdpClient? udp;
            public ISerialPacket? sp;
            private bool subscribedForReadingFromTcp = false;
            private bool subscribedForReadingFromUdp = false;
            List<Action<byte[], Action>> readEvents = [];
            private bool isInitialized = false;
            private L1Types t;
            private List<byte> L1RxBuffer = [];
            public Action<Exception> ErrorReported = delegate { };

            public PNetworkCommunicator(TcpListener? tcp, UdpClient? udp, ISerialPacket? sp)
            {
                this.tcp = tcp;
                this.udp = udp;
                this.sp = sp;
                if (tcp != null)
                {
                    tcp.Start(1);
                    Task.Factory.StartNew(delegate ()
                    {
                        while (true)
                        {
                            try
                            {
                                // blocks until a client connects instead of polling Pending() in a loop
                                TcpClient accepted = tcp.AcceptTcpClient();
                                // the new connection replaces the previous one, which must not stay open
                                Interlocked.Exchange(ref session, accepted)?.Close();
                            }
                            catch (ObjectDisposedException)
                            {
                                break;
                            }
                            catch
                            {
                                Thread.Sleep(50);
                            }
                        }
                    }, TaskCreationOptions.LongRunning);
                }
            }

            private void TcpReader()
            {
                byte[] buffer = new byte[65536];
                TcpClient? closedSession = null;
                while (true)
                {
                    TcpClient? current = session;
                    if (current == null || !current.Connected || current == closedSession)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    try
                    {
                        // blocks until data arrives, so the thread doesn't spin while the line is idle
                        int read = current.GetStream().Read(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            // the remote side closed the connection: report it once and wait for the next one
                            closedSession = current;
                            L1RxBuffer.Clear();
                            ErrorReported(new EndOfStreamException("the remote side closed the TCP connection"));
                            continue;
                        }
                        ModemAPIDebugger.OutputDebugMessage($"low latency ans physical modem received data from tcp");
                        L1RxBuffer.AddRange(buffer.AsSpan(0, read));
                        // one read may carry several frames, all of them must be handled before blocking again
                        L1Framing.ExtractFrames(L1RxBuffer, data => CallAllEvents(data, delegate () { throw new NotImplementedException(); }), ErrorReported);
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                    {
                        // the connection broke or was replaced by a new one; the data of the old one is useless
                        closedSession = current;
                        L1RxBuffer.Clear();
                        ErrorReported(e);
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
                    TcpClient current = session ?? throw new NullTransmitterException("tcp");
                    current.GetStream().Write(L1Framing.Frame(data));
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

        internal LowLatencyAnsweringPhysicalModem(string wire, int speed, L1Types wt, string lmdir, bool useMoreStableSerialPacket)
        {
            LevelOneWireType = wt;
            LargeMessagesDirectory = lmdir;
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
                    p_.InternalErrorHappened += InternalErrorHappened;
                    p = p_;
                }
                Communicator = new(null, null, p);
            }
            else if (wt == L1Types.L1_INET_TCP)
            {
                Communicator = new(new(IPAddress.Parse(wire.Split(':')[0]), int.Parse(wire.Split(':')[1])), null, null);
            }
            else if (wt == L1Types.L1_INET_UDP)
            {
                UdpClient client = new();
                client.Connect(IPAddress.Parse(wire.Split(':')[0]), int.Parse(wire.Split(':')[1]));
                client.Client.Bind(new IPEndPoint(IPAddress.Parse(wire.Split(':')[0]), int.Parse(wire.Split(':')[1])));
                Communicator = new(null, client, null);
            }
            else
            {
                throw new NotSupportedException("L1 type not supported");
            }
            Communicator.Init(wt);
            Communicator.ErrorReported = delegate (Exception e) { InternalErrorHappened(e, ErrorEmitter.LowLatencyAnsweringPhysicalModem); };
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                if (!HasActiveConnection)
                {
                    CIoCILLowLatShortConnectionRequestBody temp = PacketConverter.ParseLowLatencyConnectionRequest(d);
                    if (!temp.IsRequestDamaged)
                    {
                        HasPendingConnection = true;
                        PendingConnectionInformation = temp;
                        foreach (Action action in EventsForIncomingConnection)
                        {
                            action();
                        }
                    }
                }
            });
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                if (HasActiveConnection && d.SequenceEqual(CIoCILLowLatShortDefaultPhrases.DisconnectionRequest))
                {
                    Communicator.Write(CIoCILLowLatShortDefaultPhrases.DisconnectionResponse);
                    HasActiveConnection = false;
                    HasPendingConnection = false;
                    ClientAddress = new();
                }
            });
        }
        internal LowLatencyAnsweringPhysicalModem(Stream stream)
        {
            LevelOneWireType = L1Types.L1_SERIAL;
            MoreStableSerialPacket mssp = new(stream);
            mssp.OnDataDropped += delegate (L1DropBytesReasons reason, int count)
            {
                L1DroppedData(reason, count);
            };
            Communicator = new(null, null, mssp);
            Communicator.Init(L1Types.L1_SERIAL);
            Communicator.ErrorReported = delegate (Exception e) { InternalErrorHappened(e, ErrorEmitter.LowLatencyAnsweringPhysicalModem); };
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                if (!HasActiveConnection)
                {
                    CIoCILLowLatShortConnectionRequestBody temp = PacketConverter.ParseLowLatencyConnectionRequest(d);
                    if (!temp.IsRequestDamaged)
                    {
                        HasPendingConnection = true;
                        PendingConnectionInformation = temp;
                        foreach (Action action in EventsForIncomingConnection)
                        {
                            action();
                        }
                    }
                }
            });
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                if (HasActiveConnection && d.SequenceEqual(CIoCILLowLatShortDefaultPhrases.DisconnectionRequest))
                {
                    Communicator.Write(CIoCILLowLatShortDefaultPhrases.DisconnectionResponse);
                    HasActiveConnection = false;
                    HasPendingConnection = false;
                    ClientAddress = new();
                }
            });
        }
        public void ModifyAnsweringModemSettings(int packetmtu, ulong lmmtu, bool allowlm, string modemName)
        {
            PacketMaximumTransmissionUnit = packetmtu;
            LargeMessageMaximumTransmissionUnit = lmmtu;
            AllowLargeMessageOperations = allowlm;
            ModemName = modemName;
        }
        public void Answer()
        {
            if (HasPendingConnection)
            {
                List<byte> response = [.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse];
                byte flags = (byte)(
                    0b00111000 | // willing to transmit MTUs and timeout
                    (PendingConnectionInformation.CurrentClientAddress.IsEmpty ? 0b00000010 : 0) | // SMLA or no
                    (AllowLargeMessageOperations ? 0b00000001 : 0) // does this answering modem operate with LMs
                    );
                response.Add(flags);
                // SMLA
                if (PendingConnectionInformation.CurrentClientAddress.IsEmpty)
                {
                    if (SMLARequest == null)
                    {
                        throw new NullReferenceException("smla request");
                    }
                    Address tempAddr = SMLARequest(ModemName);
                    byte[] smlaAddrBytes = Encoding.UTF8.GetBytes(tempAddr.AddressValue ?? "");
                    ClientAddress = Address.Copy(tempAddr);
                    response.Add((byte)smlaAddrBytes.Length);
                    response.AddRange(smlaAddrBytes);
                }
                else
                {
                    ClientAddress = Address.Copy(PendingConnectionInformation.CurrentClientAddress);
                }
                // MTUs and timeout
                Span<byte> mtuForPacketsBytes = stackalloc byte[2];
                Span<byte> mtuForLargeMessagesBytes = stackalloc byte[8];
                Span<byte> keepaliveTimeoutInNanosecondsBytes = stackalloc byte[8];
                Span<byte> crc32 = stackalloc byte[4];
                BinaryPrimitives.WriteUInt16BigEndian(mtuForPacketsBytes, (ushort)PacketMaximumTransmissionUnit);
                BinaryPrimitives.WriteUInt64BigEndian(mtuForLargeMessagesBytes, LargeMessageMaximumTransmissionUnit);
                BinaryPrimitives.WriteUInt64BigEndian(keepaliveTimeoutInNanosecondsBytes, TimeoutNs);
                response.AddRange(mtuForPacketsBytes);
                response.AddRange(mtuForLargeMessagesBytes);
                response.AddRange(keepaliveTimeoutInNanosecondsBytes);
                BinaryPrimitives.WriteUInt32BigEndian(crc32, Crc32.HashToUInt32([.. response]));
                response.AddRange(crc32);
                Communicator.Write([.. response]);
                HasActiveConnection = true;
            }
            else
            {
                throw new InvalidOperationException("no pending connections for answering");
            }
        }

        public void AttachConnectEvent(Action onConnection)
        {
            EventsForAnsweredConnection.Add(onConnection);
        }

        public void AttachIncomingEvent(Action onIncoming)
        {
            EventsForIncomingConnection.Add(onIncoming);
        }

        public void Drop()
        {
            throw new NotImplementedException();
        }

        public Address GetSubscriberAddress()
        {
            return ClientAddress;
        }

        public void Intercept(Action<DataBlock, Action, PacketTransmissionDirection> onPacketIntercepted)
        {
            throw new NotImplementedException();
        }

        public void LockDataTransfer()
        {
            throw new NotImplementedException();
        }

        public bool SetSubscriberAddress(Address subscriberAddress)
        {
            throw new NotImplementedException();
        }

        public void TransmitToSubscriber(byte[] data, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100)
        {
            throw new NotImplementedException();
        }

        public void TransmitToSubscriber(string data, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100)
        {
            throw new NotImplementedException();
        }

        public void UnlockDataTransfer()
        {
            throw new NotImplementedException();
        }
        internal void AddSMLARequest(Func<string, Address> rq)
        {
            SMLARequest = rq;
        }
        internal void TransmitToSubscriber(Packet packet)
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
                                InternalErrorHappened(ex, ErrorEmitter.LowLatencyAnsweringPhysicalModem);
                            }
                        }
                    }
                })
                { IsBackground = true, Name = "LowLatencyAnsweringPhysicalModem sender" }.Start();
            }
        }
        internal void InternalAttachReceiveEvent(Action<Packet, Action> evt)
        {
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                if (PacketConverter.CheckIsPacket(d))
                {
                    Packet packet = PacketConverter.BytesToPacket(d);
                    if (!packet.IsErrorWhileReading)
                    {
                        evt(packet, k);
                    }
                    else
                    {
                        ModemAPIDebugger.OutputDebugMessage("low latency answering modem received a corrupted packet");
                        if (packet.ReadError == PacketReadError.CrcMismatch)
                        {
                            CallAllEventsOfCrc();
                        }
                        else
                        {
                            // a packet with a correct CRC, but a wrong structure: not damage on the line, so it isn't reported as a CRC mismatch
                            InternalErrorHappened(new InvalidDataException($"received a packet, that can't be read: {packet.ReadError}"), ErrorEmitter.LowLatencyAnsweringPhysicalModem);
                        }
                    }
                }
            });
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
