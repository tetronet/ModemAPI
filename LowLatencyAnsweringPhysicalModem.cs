using System;
using System.Buffers.Binary;
using System.Diagnostics;
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
        /// <summary>
        /// How long a connection request, that waits for <see cref="Answer"/>, keeps the line: until then a connection request from
        /// another address can't take it (over UDP). Default 10 seconds.
        /// </summary>
        public TimeSpan PendingConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);
        // when the pending connection request came (Stopwatch)
        private long PendingConnectionSince;
        private List<Action> EventsForIncomingConnection = [];
        private List<Action> EventsForAnsweredConnection = [];
        private Func<string, Address>? SMLARequest;
        private Address ClientAddress = new();
        /// <summary>
        /// Connection timeout in nanoseconds, that this modem tells its subscriber when it connects (LL-CIoCIL-mini). This modem
        /// sends Modem-KeepAlive every third of it; the subscriber considers the connection lost, when nothing comes for the whole
        /// timeout. Default is 100 seconds; 0 turns Modem-KeepAlive off on both sides. Used for the next connection.
        /// </summary>
        public ulong KeepAliveTimeoutNanoseconds { get; set; } = 100_000_000_000;
        /// <summary>
        /// How many Modem-KeepAlives in a row may go without any answer from the subscriber, before the connection is considered
        /// lost and this modem is ready for a new connection (the subscriber isn't told). Unanswered ones in between answered ones
        /// don't count. At least 1, default 3.
        /// </summary>
        public int MaxMissedKeepAlives { get; set; } = 3;
        private readonly Lock KeepAliveLock = new();
        private Timer? KeepAliveTimer;
        // the Modem-KeepAlive run of the current connection: a tick of an older one does nothing
        private object? KeepAliveRun;
        private volatile bool TrafficSinceKeepAlive = false;
        private bool KeepAliveOutstanding = false;
        private int MissedKeepAlives = 0;
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
            // LEGACY (TCP): see the L1_INET_TCP branch of the constructor
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
            // UDP: whether a new subscriber may take the line now (the modem says so while it has no active connection)
            public Func<bool> CanTakeNewSubscriber = () => true;
            // UDP: where the subscriber sends from; the answers go there
            private volatile IPEndPoint? UdpPeer;

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
                    IPEndPoint? ep = null;
                    try
                    {
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
                    // the line belongs to one subscriber: anybody can send a datagram to this port (with any source address, the same
                    // IP address as the subscriber's too: it can be forged, or be a NAT shared by many), so another address becomes
                    // the one answered only by a valid connection request while there is no connection. A subscriber, that comes
                    // back from another port (a restart, or NAT gave it a new one), connects as soon as Modem-KeepAlive finds the old
                    // connection dead; from its own address and port it reconnects right away
                    if (!ep!.Equals(UdpPeer))
                    {
                        if (!CanTakeNewSubscriber() || PacketConverter.ParseLowLatencyConnectionRequest(datagram).IsRequestDamaged)
                        {
                            continue;
                        }
                        UdpPeer = ep;
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
                lock (WriteLock)
                {
                    WriteUnlocked(data);
                }
            }
            private readonly Lock WriteLock = new();
            private void WriteUnlocked(byte[] data)
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
                    IPEndPoint peer = UdpPeer ?? throw new NullTransmitterException("udp: nothing has come from the subscriber yet, so there is nowhere to answer");
                    udp.Send(data, data.Length, peer);
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
                // LEGACY, not used: the answering side over TCP is VirtualModem.CreateSlave(Stream, string), one modem per accepted
                // connection (the application accepts them with its own TcpListener). This listener serves one subscriber, and
                // every new TCP connection replaces it; don't let user code reach this branch (the constructor is internal, and
                // CreateSlave(string, int, L1Types, bool) refuses TCP). Kept for now, not to be fixed or extended.
                Communicator = new(new(IPAddress.Parse(wire.Split(':')[0]), int.Parse(wire.Split(':')[1])), null, null);
            }
            else if (wt == L1Types.L1_INET_UDP)
            {
                // the answering side listens on its own address and port (like the TCP listener above), and answers whoever calls
                UdpClient client = new(new IPEndPoint(IPAddress.Parse(wire.Split(':')[0]), int.Parse(wire.Split(':')[1])));
                if (OperatingSystem.IsWindows())
                {
                    // without this an ICMP "port unreachable" for an earlier answer makes the next Receive throw
                    const int SIO_UDP_CONNRESET = -1744830452;
                    client.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
                }
                Communicator = new(null, client, null);
            }
            else
            {
                throw new NotSupportedException("L1 type not supported");
            }
            Communicator.Init(wt);
            Communicator.ErrorReported = delegate (Exception e) { InternalErrorHappened(e, ErrorEmitter.LowLatencyAnsweringPhysicalModem); };
            Communicator.CanTakeNewSubscriber = CanTakeNewSubscriber;
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                // anything from the subscriber shows it is alive, the answer to Modem-KeepAlive or any other data
                TrafficSinceKeepAlive = true;
            });
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                // a connection request is answered even during an active connection: the subscriber only asks again when it lost
                // the connection without saying so (it crashed, or its network changed), and otherwise it couldn't connect anymore.
                // Only the subscriber's own line can deliver it: the current TCP session, or UDP from its own address and port.
                CIoCILLowLatShortConnectionRequestBody temp = PacketConverter.ParseLowLatencyConnectionRequest(d);
                if (!temp.IsRequestDamaged)
                {
                    HasActiveConnection = false;
                    Volatile.Write(ref PendingConnectionSince, Stopwatch.GetTimestamp());
                    HasPendingConnection = true;
                    PendingConnectionInformation = temp;
                    foreach (Action action in EventsForIncomingConnection)
                    {
                        action();
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
            Communicator.CanTakeNewSubscriber = CanTakeNewSubscriber;
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                // anything from the subscriber shows it is alive, the answer to Modem-KeepAlive or any other data
                TrafficSinceKeepAlive = true;
            });
            Communicator.OnDataRead(delegate (byte[] d, Action k)
            {
                // a connection request is answered even during an active connection: the subscriber only asks again when it lost
                // the connection without saying so (it crashed, or its network changed), and otherwise it couldn't connect anymore.
                // Only the subscriber's own line can deliver it: the current TCP session, or UDP from its own address and port.
                CIoCILLowLatShortConnectionRequestBody temp = PacketConverter.ParseLowLatencyConnectionRequest(d);
                if (!temp.IsRequestDamaged)
                {
                    HasActiveConnection = false;
                    Volatile.Write(ref PendingConnectionSince, Stopwatch.GetTimestamp());
                    HasPendingConnection = true;
                    PendingConnectionInformation = temp;
                    foreach (Action action in EventsForIncomingConnection)
                    {
                        action();
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
        // another subscriber may take the line only when it is free: no active connection, and no connection request waiting for
        // Answer() (unless it waits for longer than PendingConnectionTimeout: the application doesn't answer it)
        private bool CanTakeNewSubscriber()
        {
            return !HasActiveConnection && (!HasPendingConnection || Stopwatch.GetElapsedTime(Volatile.Read(ref PendingConnectionSince)) > PendingConnectionTimeout);
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
                ulong keepAliveTimeout = KeepAliveTimeoutNanoseconds;
                BinaryPrimitives.WriteUInt64BigEndian(keepaliveTimeoutInNanosecondsBytes, keepAliveTimeout);
                response.AddRange(mtuForPacketsBytes);
                response.AddRange(mtuForLargeMessagesBytes);
                response.AddRange(keepaliveTimeoutInNanosecondsBytes);
                BinaryPrimitives.WriteUInt32BigEndian(crc32, Crc32.HashToUInt32([.. response]));
                response.AddRange(crc32);
                Communicator.Write([.. response]);
                HasActiveConnection = true;
                StartKeepAlive(keepAliveTimeout);
            }
            else
            {
                throw new InvalidOperationException("no pending connections for answering");
            }
        }

        private void StartKeepAlive(ulong timeoutNanoseconds)
        {
            lock (KeepAliveLock)
            {
                KeepAliveTimer?.Dispose();
                KeepAliveTimer = null;
                KeepAliveRun = null;
                if (timeoutNanoseconds == 0)
                {
                    return;
                }
                object run = new();
                KeepAliveRun = run;
                TrafficSinceKeepAlive = false;
                KeepAliveOutstanding = false;
                MissedKeepAlives = 0;
                // every third of the timeout (nanoseconds / 100 = ticks)
                TimeSpan interval = TimeSpan.FromTicks((long)Math.Max(1, timeoutNanoseconds / 3 / 100));
                KeepAliveTimer = new Timer(KeepAliveTick, run, interval, interval);
            }
        }
        private void KeepAliveTick(object? run)
        {
            lock (KeepAliveLock)
            {
                if (!ReferenceEquals(run, KeepAliveRun))
                {
                    return;
                }
                if (!HasActiveConnection)
                {
                    // disconnected or connecting again: this run is over
                    StopKeepAlive();
                    return;
                }
                if (KeepAliveOutstanding)
                {
                    // the previous Modem-KeepAlive counts as answered, if anything came from the subscriber since it was sent
                    if (TrafficSinceKeepAlive)
                    {
                        MissedKeepAlives = 0;
                    }
                    else if (++MissedKeepAlives >= Math.Max(1, MaxMissedKeepAlives))
                    {
                        // the connection is dead: ready for a new one; the subscriber isn't told, and no more Modem-KeepAlives are sent
                        HasActiveConnection = false;
                        HasPendingConnection = false;
                        StopKeepAlive();
                        return;
                    }
                }
                TrafficSinceKeepAlive = false;
                KeepAliveOutstanding = true;
            }
            try
            {
                Communicator.Write(CIoCILLowLatShortDefaultPhrases.KeepAliveRequest);
            }
            catch (Exception e)
            {
                InternalErrorHappened(e, ErrorEmitter.LowLatencyAnsweringPhysicalModem);
            }
        }
        // must be called under KeepAliveLock
        private void StopKeepAlive()
        {
            KeepAliveTimer?.Dispose();
            KeepAliveTimer = null;
            KeepAliveRun = null;
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
