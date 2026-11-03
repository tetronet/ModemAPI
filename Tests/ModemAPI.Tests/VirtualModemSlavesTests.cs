using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// Slaves of <see cref="VirtualModem"/> (RAWWS to a local server, the tetronet side): every packet reaches its slave once,
    /// SMLA gives addresses of the set length, and a refused slave doesn't keep its port.
    /// </summary>
    public class VirtualModemSlavesTests
    {
        private sealed class Network : IDisposable
        {
            private readonly TcpListener Server = new(IPAddress.Loopback, 0);
            private readonly TaskCompletionSource<WebSocket> Connected = new();
            private readonly List<TcpListener> Listeners = [];
            public readonly VirtualModem Master;
            public readonly ConcurrentQueue<Packet> AtTetronet = new();

            public Network(int smlaLength = 2, bool attachRouter = true)
            {
                Server.Start();
                Task.Run(Serve);
                Master = new($"ws://127.0.0.1:{((IPEndPoint)Server.LocalEndpoint).Port}/", new LineParams(new Address("7-1"), false, null), rawWs: true)
                {
                    ConnectAsAnAddressMachine = true,
                    SMLALength = smlaLength,
                };
                if (attachRouter)
                {
                    Master.AttachPacketRouter(new PacketRouter(new Address("7-1")));
                }
                Master.Dial();
                Assert.True(Connected.Task.Wait(TimeSpan.FromSeconds(5)), "the modem didn't connect");
            }

            private async Task Serve()
            {
                TcpClient client = await Server.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                StringBuilder request = new();
                byte[] one = new byte[1];
                while (!request.ToString().EndsWith("\r\n\r\n"))
                {
                    await stream.ReadExactlyAsync(one);
                    request.Append((char)one[0]);
                }
                string key = request.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
                WebSocket socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromMinutes(1));
                Connected.SetResult(socket);
                RawWsCodec codec = new();
                byte[] buffer = new byte[1 << 20];
                while (true)
                {
                    int count = 0;
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(count), default);
                        count += result.Count;
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    if (codec.TryReadDataTransmission(buffer.AsSpan(0, count), out Packet? packet))
                    {
                        AtTetronet.Enqueue(packet!);
                    }
                }
            }

            /// <summary>
            /// The tetronet sends a packet to the master.
            /// </summary>
            public void FromTetronet(Packet packet)
            {
                byte[] message = JsonSerializer.SerializeToUtf8Bytes(new object[] { "data-transmission", PacketConverter.PacketToWebSocketMessage(packet) });
                Connected.Task.Result.SendAsync(message, WebSocketMessageType.Text, true, default).Wait();
            }

            /// <summary>
            /// A slave of the master and a child, that got its address from it by SMLA.
            /// </summary>
            public (LowLatencyPhysicalModem child, ConcurrentQueue<Packet> received) Child(string name)
            {
                TcpListener listener = new(IPAddress.Loopback, 0);
                listener.Start();
                Listeners.Add(listener);
                Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
                LowLatencyPhysicalModem child = new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, L1Types.L1_INET_TCP, true);
                Master.CreateSlave(accepted.Result.GetStream(), name);
                return Connect(child, name);
            }

            /// <summary>
            /// A UDP slave of the master and a child of it.
            /// </summary>
            public (LowLatencyPhysicalModem child, ConcurrentQueue<Packet> received) UdpChild(string name)
            {
                int port = FreeUdpPort();
                Master.CreateSlave($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP);
                return Connect(new LowLatencyPhysicalModem($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP), name);
            }

            private static (LowLatencyPhysicalModem, ConcurrentQueue<Packet>) Connect(LowLatencyPhysicalModem child, string name)
            {
                ConcurrentQueue<Packet> received = new();
                child.AttachReceiveEventNoUnfragment((p, k) => received.Enqueue(p));
                child.Dial();
                Assert.True(SpinWait.SpinUntil(() => child.IsModemConnected, TimeSpan.FromSeconds(5)), $"{name} didn't connect");
                return (child, received);
            }

            public void Dispose()
            {
                Server.Stop();
                foreach (TcpListener listener in Listeners)
                {
                    listener.Stop();
                }
            }
        }

        private static int FreeUdpPort()
        {
            using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        private static Packet PacketOf(string from, string to, byte data) => new(new Address(from), new Address(to), [data], true, null, "q", 1, false, 0, data, 9);

        [Fact]
        public void PacketFromTheTetronetReachesItsSlaveOnce()
        {
            using Network network = new();
            var (child1, received1) = network.Child("s1");
            var (_, received2) = network.Child("s2");
            var (_, received3) = network.Child("s3");
            for (byte i = 0; i < 5; i++)
            {
                network.FromTetronet(PacketOf("7-9", child1.LocalModemAddress!.AddressValue!, i));
            }
            Assert.True(SpinWait.SpinUntil(() => received1.Count >= 5, TimeSpan.FromSeconds(5)));
            Thread.Sleep(300);
            // with three slaves every packet came three times
            Assert.Equal([0, 1, 2, 3, 4], received1.Select(p => p.DataBytes[0]));
            Assert.All(received1, p => Assert.Equal(8, p.RoutersToPass));
            Assert.Empty(received2);
            Assert.Empty(received3);
        }

        [Fact]
        public void PacketOfAChildGoesToTheTetronet()
        {
            using Network network = new();
            var (child, _) = network.Child("s1");
            child.LowLevelTransmit([42], new Address("7-9"), "q", 7, null, 20);
            Assert.True(SpinWait.SpinUntil(() => !network.AtTetronet.IsEmpty, TimeSpan.FromSeconds(5)));
            Packet packet = Assert.Single(network.AtTetronet);
            Assert.Equal(child.LocalModemAddress!.AddressValue, packet.Transmitter.AddressValue);
            Assert.Equal(19, packet.RoutersToPass);
        }

        [Fact]
        public void SmlaRetryGivesANumberOfTheSetLength()
        {
            using Network network = new(smlaLength: 1);
            // only 9 is free: the numbers drawn before it are taken, and a retry must not make the number longer
            lock (network.Master.TakenNumbers)
            {
                network.Master.TakenNumbers.AddRange(["0", "1", "2", "3", "4", "5", "6", "7", "8"]);
            }
            var (child, _) = network.Child("s1");
            Assert.Equal("7-1-9", child.LocalModemAddress!.AddressValue);
        }

        [Fact]
        public void ShortAddressInTheInnerNetworkDoesntBreakTheSlave()
        {
            using Network network = new();
            var (child, _) = network.UdpChild("u1");
            ConcurrentQueue<Exception> errors = new();
            network.Master.GetAnsweringAllModems().Values.Single().InternalErrorHappened += (e, emitter) => errors.Enqueue(e);
            // "7-1" is in the inner network, but shorter than the address of a slave
            child.LowLevelTransmit([1], new Address("7-1"), "q", 7, null, 20);
            child.LowLevelTransmit([2], new Address("7-9"), "q", 7, null, 20);
            Assert.True(SpinWait.SpinUntil(() => !network.AtTetronet.IsEmpty, TimeSpan.FromSeconds(5)), "the slave stopped forwarding");
            Assert.Equal([2], Assert.Single(network.AtTetronet).DataBytes);
            Assert.Empty(errors);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public void ChildOfALowLatencyMasterTalksToADeviceOfTheOuterNetwork(bool boardUnderTheSameMaster, bool attachRouters)
        {
            // CIAddressMachine (VirtualModem 7-1) -> LowLatencyPhysicalModem master (7-1-NN) -> LPH (7-1-NN-N); the board is
            // another device of 7-1 or another child of the same master. Without an attached router the default one routes
            using Network network = new(attachRouter: attachRouters);
            int masterPort = FreeUdpPort();
            network.Master.CreateSlave($"127.0.0.1:{masterPort}", 0, L1Types.L1_INET_UDP);
            LowLatencyPhysicalModem master = new($"127.0.0.1:{masterPort}", 0, L1Types.L1_INET_UDP) { ConnectAsAnAddressMachine = true, SMLALength = 1 };
            master.Dial();
            Assert.True(SpinWait.SpinUntil(() => master.IsModemConnected, TimeSpan.FromSeconds(5)), "the master didn't connect");
            if (attachRouters)
            {
                master.AttachPacketRouter(new PacketRouter(master.LocalModemAddress!));
            }

            (LowLatencyPhysicalModem child, ConcurrentQueue<Packet> received) UnderMaster(string name)
            {
                int port = FreeUdpPort();
                master.CreateSlave($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP);
                LowLatencyPhysicalModem child = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP);
                ConcurrentQueue<Packet> received = new();
                child.AttachReceiveEventNoUnfragment((p, k) => received.Enqueue(p));
                child.Dial();
                Assert.True(SpinWait.SpinUntil(() => child.IsModemConnected, TimeSpan.FromSeconds(5)), $"{name} didn't connect");
                return (child, received);
            }

            var (lph, atLph) = UnderMaster("lph");
            var (board, atBoard) = boardUnderTheSameMaster ? UnderMaster("board") : network.UdpChild("board");
            Assert.StartsWith(master.LocalModemAddress!.AddressValue + "-", lph.LocalModemAddress!.AddressValue);

            lph.LowLevelTransmit([1], board.LocalModemAddress!, "q", 7, null, 20);
            Assert.True(SpinWait.SpinUntil(() => atBoard.Any(p => p.DataBytes[0] == 1), TimeSpan.FromSeconds(5)), $"{lph.LocalModemAddress} -> {board.LocalModemAddress} didn't arrive");
            board.LowLevelTransmit([2], lph.LocalModemAddress!, "q", 7, null, 20);
            Assert.True(SpinWait.SpinUntil(() => atLph.Any(p => p.DataBytes[0] == 2), TimeSpan.FromSeconds(5)), $"{board.LocalModemAddress} -> {lph.LocalModemAddress} didn't arrive");

            // from the outer network (behind the tetronet) to the child of the master, and back
            network.FromTetronet(PacketOf("7-9", lph.LocalModemAddress!.AddressValue!, 3));
            Assert.True(SpinWait.SpinUntil(() => atLph.Any(p => p.DataBytes[0] == 3), TimeSpan.FromSeconds(5)), $"7-9 -> {lph.LocalModemAddress} didn't arrive");
            Packet atLphFromOuter = atLph.Single(p => p.DataBytes[0] == 3);
            Assert.Equal(7, atLphFromOuter.RoutersToPass); // two routers passed
            lph.LowLevelTransmit([4], new Address("7-9"), "q", 7, null, 20);
            Assert.True(SpinWait.SpinUntil(() => network.AtTetronet.Any(p => p.DataBytes[0] == 4), TimeSpan.FromSeconds(5)), $"{lph.LocalModemAddress} -> 7-9 didn't arrive");
            // the master and its slave don't take a packet for another child, that looks like it is under the slave
            Assert.DoesNotContain(atBoard, p => p.DataBytes[0] == 3);
        }

        [Fact]
        public void PacketFromTheTetronetForANeighbourOfASlaveIsNotForwarded()
        {
            using Network network = new(attachRouter: false);
            var (child, received) = network.UdpChild("u1");
            string address = child.LocalModemAddress!.AddressValue!; // 7-1-NN
            // 7-1-NNd isn't under 7-1-NN; 7-1-NN-d is
            network.FromTetronet(PacketOf("7-9", address + "5", 1));
            network.FromTetronet(PacketOf("7-9", address + "-5", 2));
            Assert.True(SpinWait.SpinUntil(() => received.Any(p => p.DataBytes[0] == 2), TimeSpan.FromSeconds(5)));
            Thread.Sleep(300);
            Assert.DoesNotContain(received, p => p.DataBytes[0] == 1);
        }

        private sealed class RefusingRouter(Address localAddress) : IPacketRouter
        {
            public Address LocalAddress { get; } = localAddress;
            public PacketRouterDecidion Decide(TransmitterReceiverPair txrx, PacketTransmissionDirection dir) => PacketRouterDecidion.AddressSpoofingDetectedRefusedToForward;
            public void WorkOnReceiveByRouterPacket(Packet packetWithReceiveByRoutersFlag) { }
            public Packet? ModifyModifiablePacket(Packet packetWithModifyByRoutersFlag) => null;
        }

        [Fact]
        public void AttachedRouterIsUsedInsteadOfTheDefaultOne()
        {
            using Network network = new(attachRouter: false);
            network.Master.AttachPacketRouter(new RefusingRouter(new Address("7-1")));
            var (child1, _) = network.UdpChild("u1");
            var (child2, received2) = network.UdpChild("u2");
            child1.LowLevelTransmit([1], child2.LocalModemAddress!, "q", 7, null, 20);
            child1.LowLevelTransmit([2], new Address("7-9"), "q", 7, null, 20);
            network.FromTetronet(PacketOf("7-9", child2.LocalModemAddress!.AddressValue!, 3));
            Thread.Sleep(500);
            Assert.Empty(received2);
            Assert.Empty(network.AtTetronet);
        }

        [Fact]
        public void RefusedSlaveDoesntKeepItsPort()
        {
            VirtualModem noAddress = new("ws://127.0.0.1:1/", new LineParams(), rawWs: true) { ConnectAsAnAddressMachine = true };
            noAddress.LocalModemAddress = null;
            int port = FreeUdpPort();
            Assert.Throws<NullAddressException>(() => noAddress.CreateSlave($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP));
            using UdpClient again = new(new IPEndPoint(IPAddress.Loopback, port)); // the port is free

            using Network network = new();
            network.Master.CreateSlave(new MemoryStream(), "s");
            Assert.Throws<ArgumentException>(() => network.Master.CreateSlave(new MemoryStream(), "s"));
        }
    }
}
