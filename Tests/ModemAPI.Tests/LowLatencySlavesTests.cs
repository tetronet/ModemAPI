using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// Slaves of <see cref="LowLatencyPhysicalModem"/>: the same API and routing as the slaves of <see cref="VirtualModem"/>.
    /// The line of the master goes to an answering modem ("upstream", the tetronet side); the slaves are answering modems
    /// of the master, every one with a calling modem ("child") connected to it.
    /// </summary>
    public class LowLatencySlavesTests
    {
        private sealed class Network : IDisposable
        {
            public readonly LowLatencyPhysicalModem Master;
            public readonly LowLatencyAnsweringPhysicalModem Upstream;
            public readonly ConcurrentQueue<Packet> AtUpstream = new();
            public readonly ConcurrentQueue<Packet> AtMaster = new();
            private readonly List<TcpListener> Listeners = [];

            public Network()
            {
                TcpListener line = Listen();
                Task<TcpClient> accepted = line.AcceptTcpClientAsync();
                Master = new("127.0.0.1", Port(line), L1Types.L1_INET_TCP, true) { LocalModemAddress = new Address("7-1"), ConnectAsAnAddressMachine = true, SMLALength = 2 };
                Upstream = new(accepted.Result.GetStream());
                Upstream.AttachIncomingEvent(Upstream.Answer);
                Upstream.InternalAttachReceiveEvent((p, k) => AtUpstream.Enqueue(p));
                Master.AttachReceiveEventNoUnfragment((p, k) => AtMaster.Enqueue(p));
                Master.AttachPacketRouter(new PacketRouter(new Address("7-1")));
                Master.Dial();
                Assert.True(SpinWait.SpinUntil(() => Master.IsModemConnected, TimeSpan.FromSeconds(5)), "the master didn't connect");
            }

            private TcpListener Listen()
            {
                TcpListener listener = new(IPAddress.Loopback, 0);
                listener.Start();
                Listeners.Add(listener);
                return listener;
            }

            private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

            /// <summary>
            /// A slave of the master and a child, that got its address from it by SMLA.
            /// </summary>
            public (LowLatencyPhysicalModem child, ConcurrentQueue<Packet> received) Child(string name)
            {
                TcpListener listener = Listen();
                Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
                LowLatencyPhysicalModem child = new("127.0.0.1", Port(listener), L1Types.L1_INET_TCP, true);
                LowLatencyAnsweringPhysicalModem slave = Master.CreateSlave(accepted.Result.GetStream(), name);
                Assert.Same(slave, Master.GetAnsweringModem(name));
                ConcurrentQueue<Packet> received = new();
                child.AttachReceiveEventNoUnfragment((p, k) => received.Enqueue(p));
                child.Dial();
                Assert.True(SpinWait.SpinUntil(() => child.IsModemConnected, TimeSpan.FromSeconds(5)), $"{name} didn't connect");
                return (child, received);
            }

            /// <summary>
            /// A UDP slave of the master and a child of it.
            /// </summary>
            public (LowLatencyPhysicalModem child, ConcurrentQueue<Packet> received) UdpChild(string name)
            {
                int port;
                using (UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0)))
                {
                    port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
                }
                Master.CreateSlave($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP);
                LowLatencyPhysicalModem child = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP);
                ConcurrentQueue<Packet> received = new();
                child.AttachReceiveEventNoUnfragment((p, k) => received.Enqueue(p));
                child.Dial();
                Assert.True(SpinWait.SpinUntil(() => child.IsModemConnected, TimeSpan.FromSeconds(5)), $"{name} didn't connect");
                return (child, received);
            }

            public void Dispose()
            {
                foreach (TcpListener listener in Listeners)
                {
                    listener.Stop();
                }
            }
        }

        private static Packet PacketOf(string from, string to, byte data, ushort ttl = 9) => new(new Address(from), new Address(to), [data], true, null, "q", 1, false, 0, data, ttl);

        [Fact]
        public void ChildGetsAnAddressUnderTheMaster()
        {
            using Network network = new();
            var (child, _) = network.Child("s1");
            Assert.Matches(@"^7-1-\d\d$", child.LocalModemAddress!.AddressValue);
            IAnswerHost host = network.Master;
            Assert.Single(host.GetAnsweringAllModems());
        }

        [Fact]
        public void PacketsOfAChildGoDownTheLine()
        {
            using Network network = new();
            var (child, _) = network.Child("s1");
            child.LowLevelTransmit([42], new Address("7-9"), "q", 7, null, 20);
            Assert.True(SpinWait.SpinUntil(() => !network.AtUpstream.IsEmpty, TimeSpan.FromSeconds(5)));
            Packet packet = Assert.Single(network.AtUpstream);
            Assert.Equal(child.LocalModemAddress!.AddressValue, packet.Transmitter.AddressValue);
            Assert.Equal("7-9", packet.Receiver.AddressValue);
            Assert.Equal([42], packet.DataBytes);
            Assert.Equal(19, packet.RoutersToPass); // the master is one router
            Assert.Empty(network.AtMaster);
        }

        [Fact]
        public void PacketsFromTheLineGoToTheirChildOnly()
        {
            using Network network = new();
            var (child1, received1) = network.Child("s1");
            var (child2, received2) = network.Child("s2");
            string address1 = child1.LocalModemAddress!.AddressValue!;
            network.Upstream.TransmitToSubscriber(PacketOf("7-9", address1, 1));
            network.Upstream.TransmitToSubscriber(PacketOf("7-9", "7-1", 2)); // for the master itself
            Assert.True(SpinWait.SpinUntil(() => received1.Count == 1 && network.AtMaster.Count == 1, TimeSpan.FromSeconds(5)));
            Thread.Sleep(200);
            Packet packet = Assert.Single(received1);
            Assert.Equal([1], packet.DataBytes);
            Assert.Equal(8, packet.RoutersToPass);
            Assert.Equal([2], Assert.Single(network.AtMaster).DataBytes);
            Assert.Empty(received2);
        }

        [Fact]
        public void PacketsFromTheLineForAChildOfAChildGoToThatChild()
        {
            using Network network = new();
            var (child1, received1) = network.Child("s1");
            var (_, received2) = network.Child("s2");
            string address1 = child1.LocalModemAddress!.AddressValue!; // 7-1-NN, an address machine itself
            network.Upstream.TransmitToSubscriber(PacketOf("7-9", address1 + "-5", 1)); // under it
            network.Upstream.TransmitToSubscriber(PacketOf("7-9", address1 + "5", 2)); // not under it
            Assert.True(SpinWait.SpinUntil(() => !received1.IsEmpty, TimeSpan.FromSeconds(5)));
            Thread.Sleep(300);
            Packet packet = Assert.Single(received1);
            Assert.Equal([1], packet.DataBytes);
            Assert.Equal(8, packet.RoutersToPass);
            Assert.Empty(received2);
            // the packet of a child isn't the master's
            Assert.DoesNotContain(network.AtMaster, p => p.DataBytes[0] == 1);
        }

        [Fact]
        public void ChildrenReachEachOtherInsideTheMaster()
        {
            using Network network = new();
            var (child1, _) = network.Child("s1");
            var (child2, received2) = network.Child("s2");
            child1.LowLevelTransmit([5], child2.LocalModemAddress!, "q", 7, null, 20);
            Assert.True(SpinWait.SpinUntil(() => received2.Count == 1, TimeSpan.FromSeconds(5)));
            Assert.Equal(child1.LocalModemAddress!.AddressValue, Assert.Single(received2).Transmitter.AddressValue);
            Thread.Sleep(200);
            Assert.Empty(network.AtUpstream);
            Assert.Empty(network.AtMaster);
        }

        [Fact]
        public void UdpSlavesRouteToAddressesUnderAChild()
        {
            using Network network = new();
            var (child1, _) = network.UdpChild("u1");
            var (child2, received2) = network.UdpChild("u2");
            // the address of a modem behind child2: a serial or UDP slave takes the packets for the addresses under its child
            child1.LowLevelTransmit([6], new Address(child2.LocalModemAddress!.AddressValue + "-5"), "q", 7, null, 20);
            Assert.True(SpinWait.SpinUntil(() => received2.Count == 1, TimeSpan.FromSeconds(5)));
            Assert.Equal(child2.LocalModemAddress!.AddressValue + "-5", Assert.Single(received2).Receiver.AddressValue);
            Assert.Empty(network.AtUpstream);
        }

        [Fact]
        public void PacketWithNoRoutersLeftIsNotForwarded()
        {
            using Network network = new();
            var (child, received) = network.Child("s1");
            network.Upstream.TransmitToSubscriber(PacketOf("7-9", child.LocalModemAddress!.AddressValue!, 1, ttl: 0));
            child.LowLevelTransmit([2], new Address("7-9"), "q", 7, null, 0);
            Thread.Sleep(500);
            Assert.Empty(received);
            Assert.Empty(network.AtUpstream);
        }

        [Fact]
        public void ErrorOfASlaveReachesTheMaster()
        {
            using Network network = new();
            network.Master.SMLALength = 0; // forgotten: a child without an address can't get one
            ConcurrentQueue<(Exception, ErrorEmitter)> errors = new();
            network.Master.InternalErrorHappened += (e, emitter) => errors.Enqueue((e, emitter));
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
                LowLatencyPhysicalModem child = new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, L1Types.L1_INET_TCP, true);
                network.Master.CreateSlave(accepted.Result.GetStream(), "s1");
                child.Dial();
                Assert.True(SpinWait.SpinUntil(() => !errors.IsEmpty, TimeSpan.FromSeconds(5)), "the error of the slave didn't reach the master");
                // reported by the MSSP reader of the slave, that the failed SMLA was thrown on
                var (error, emitter) = Assert.Single(errors);
                Assert.Contains("smla", Assert.IsType<InvalidDataException>(error).Message);
                Assert.Equal(ErrorEmitter.MoreStableSerialPacket, emitter);
                Assert.False(child.IsModemConnected);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public void ErrorOfTheLineReachesHandlersAttachedAfterTheConstructor()
        {
            using Network network = new();
            // the MSSP reader of the master reports, what a receive handler throws
            network.Master.AttachReceiveEventNoUnfragment((p, k) => throw new ApplicationException("handler failed"));
            ConcurrentQueue<Exception> errors = new();
            network.Master.InternalErrorHappened += (e, emitter) => errors.Enqueue(e);
            network.Upstream.TransmitToSubscriber(PacketOf("7-9", "7-1", 1));
            Assert.True(SpinWait.SpinUntil(() => !errors.IsEmpty, TimeSpan.FromSeconds(5)), "the error didn't reach the handler");
            Assert.Contains(errors, e => e is ApplicationException);
        }

        [Fact]
        public void DamagedMsspLineOfASlaveIsReportedAsACrcMismatch()
        {
            using Network network = new();
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using TcpClient line = new();
                Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
                line.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                LowLatencyAnsweringPhysicalModem slave = network.Master.CreateSlave(accepted.Result.GetStream(), "s1");
                int mismatches = 0;
                slave.AttachCRCMismatchEvent(() => Interlocked.Increment(ref mismatches));
                line.GetStream().Write("@@@@\n"u8); // not base64: a line damaged on the way
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref mismatches) == 1, TimeSpan.FromSeconds(5)), "the damaged line wasn't reported");
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public void SlavesNeedAnAddressMachineWithAnAddress()
        {
            LowLatencyPhysicalModem notAnAddressMachine = new("127.0.0.1:1", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1") };
            Assert.Throws<InvalidOperationException>(() => notAnAddressMachine.CreateSlave(new MemoryStream(), "s"));
            LowLatencyPhysicalModem noAddress = new("127.0.0.1:1", 0, L1Types.L1_INET_UDP) { ConnectAsAnAddressMachine = true };
            Assert.Throws<NullAddressException>(() => noAddress.CreateSlave(new MemoryStream(), "s"));
            LowLatencyPhysicalModem master = new("127.0.0.1:1", 0, L1Types.L1_INET_UDP) { ConnectAsAnAddressMachine = true, LocalModemAddress = new Address("7-1") };
            Assert.Throws<InvalidOperationException>(() => master.CreateSlave("127.0.0.1:0", 0, L1Types.L1_INET_TCP));
            master.CreateSlave(new MemoryStream(), "s");
            Assert.Throws<ArgumentException>(() => master.CreateSlave(new MemoryStream(), "s"));
        }
    }
}
