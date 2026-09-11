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

        private bool CurrentlyWritingPacket = false;
        private readonly Lock WriterLock = new();

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

            public PNetworkCommunicator(TcpListener? tcp, UdpClient? udp, ISerialPacket? sp)
            {
                this.tcp = tcp;
                this.udp = udp;
                this.sp = sp;
                if (tcp != null)
                {
                    tcp.Start(1);
                    Task.Run(delegate ()
                    {
                        while (true)
                        {
                            if (tcp.Pending())
                            {
                                session = tcp.AcceptTcpClient();
                            }
                        }
                    });
                }
            }

            private void TcpReader()
            {
                while (true)
                {
                    if (session == null)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    if (!session.Connected)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    byte[] buffer = new byte[session.Available];
                    int read = session.GetStream().Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        ModemAPIDebugger.OutputDebugMessage($"low latency ans physical modem received data from tcp");
                        ModemAPIDebugger.PrintByteArray(buffer);
                        L1RxBuffer.AddRange(buffer.Take(read));
                    }
                    else
                    {
                        ModemAPIDebugger.OutputDebugMessage($"low latency ans physical modem was not able to read data from tcp");
                    }
                    if (L1RxBuffer.Count < 3)
                    {
                        // not enough bytes
                        ModemAPIDebugger.OutputDebugMessage("not enough bytes");
                        continue;
                    }
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
                    if (session == null)
                    {
                        throw new NullTransmitterException("tcp");
                    }
                    // convert length of the buffer to bytes
                    byte hi = (byte)(data.Length & 0xFF0000 >> 16);
                    byte mid = (byte)(data.Length & 0xFF00 >> 8);
                    byte lo = (byte)(data.Length & 0xFF);
                    // send these bytes
                    session.GetStream().WriteByte(hi);
                    session.GetStream().WriteByte(mid);
                    session.GetStream().WriteByte(lo);
                    session.GetStream().Write(data, 0, data.Length);
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
                    byte hi = (byte)(data.Length & 0xFF0000 >> 16);
                    byte mid = (byte)(data.Length & 0xFF00 >> 8);
                    byte lo = (byte)(data.Length & 0xFF);
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
                client.DontFragment = true;
                Communicator = new(null, client, null);
            }
            else
            {
                throw new NotSupportedException("L1 type not supported");
            }
            Communicator.Init(wt);
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
            lock (WriterLock)
            {
                if (!CurrentlyWritingPacket)
                {
                    CurrentlyWritingPacket = true;
                    Task.Run(() => { Communicator.Write(PacketConverter.PacketToBytes(packet)); CurrentlyWritingPacket = false; });
                }
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
                        CallAllEventsOfCrc();
                        ModemAPIDebugger.OutputDebugMessage("low latency answering modem received a corrupted packet");
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
