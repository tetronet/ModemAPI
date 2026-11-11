using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// UNNRP over loopback: a master with three children, two of them run servers (for the redirects), the third one the client.
    /// </summary>
    public class UnnrpTests
    {
        private const int Long = 5000;
        private const int Short = 800;
        private const string QT = "usernetwork";

        private sealed class Network : IDisposable
        {
            public readonly LowLatencyPhysicalModem Master;
            public readonly LowLatencyPhysicalModem ServerModem;
            public readonly LowLatencyPhysicalModem Server2Modem;
            public readonly LowLatencyPhysicalModem ClientModem;
            public readonly UNNRPServer Server;
            public readonly UNNRPServer Server2;
            public readonly UNNRPClient Client;
            /// <summary>UNNRP packets, that reached the first server modem.</summary>
            public readonly ConcurrentQueue<Packet> Requests = new();
            /// <summary>UNNRP packets, that reached the client modem.</summary>
            public readonly ConcurrentQueue<Packet> Responses = new();
            private readonly List<TcpListener> Listeners = [];

            public Network()
            {
                TcpListener line = Listen();
                Task<TcpClient> accepted = line.AcceptTcpClientAsync();
                Master = new("127.0.0.1", Port(line), L1Types.L1_INET_TCP, true) { LocalModemAddress = new Address("7-1"), ConnectAsAnAddressMachine = true, SMLALength = 2 };
                LowLatencyAnsweringPhysicalModem upstream = new(accepted.Result.GetStream());
                upstream.AttachIncomingEvent(upstream.Answer);
                Master.AttachPacketRouter(new PacketRouter(new Address("7-1")));
                Master.Dial();
                Assert.True(SpinWait.SpinUntil(() => Master.IsModemConnected, TimeSpan.FromSeconds(5)), "the master didn't connect");
                ServerModem = Child("server");
                Server2Modem = Child("server2");
                ClientModem = Child("client");
                ServerModem.AttachReceiveEventNoUnfragment((p, k) => { if (p.QueryType == QT) Requests.Enqueue(p); });
                ClientModem.AttachReceiveEventNoUnfragment((p, k) => { if (p.QueryType == QT) Responses.Enqueue(p); });
                Server = new(ServerModem);
                Server2 = new(Server2Modem);
                Client = new(ClientModem);
            }

            public Address ServerAddress => ServerModem.LocalModemAddress!;
            public Address Server2Address => Server2Modem.LocalModemAddress!;
            public Address ClientAddress => ClientModem.LocalModemAddress!;

            private TcpListener Listen()
            {
                TcpListener listener = new(IPAddress.Loopback, 0);
                listener.Start();
                Listeners.Add(listener);
                return listener;
            }

            private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

            private LowLatencyPhysicalModem Child(string name)
            {
                TcpListener listener = Listen();
                Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
                LowLatencyPhysicalModem child = new("127.0.0.1", Port(listener), L1Types.L1_INET_TCP, true);
                Master.CreateSlave(accepted.Result.GetStream(), name);
                child.Dial();
                Assert.True(SpinWait.SpinUntil(() => child.IsModemConnected, TimeSpan.FromSeconds(5)), $"{name} didn't connect");
                return child;
            }

            /// <summary>
            /// Sends raw request data to the first server and returns the data of its response, or null, when it didn't answer.
            /// </summary>
            public byte[]? Raw(byte[] request, uint connectionId = 123456, int timeout = Long) => RawTo(ServerAddress, request, connectionId, timeout);

            public byte[]? RawTo(Address server, byte[] request, uint connectionId = 123456, int timeout = Long)
            {
                while (Responses.TryDequeue(out _)) { }
                ClientModem.LowLevelTransmit(request, server, QT, connectionId, null);
                Packet? response = null;
                SpinWait.SpinUntil(() => Responses.TryDequeue(out response), timeout);
                if (response == null)
                {
                    return null;
                }
                Assert.Equal(connectionId, response.ConnectionID);
                Assert.Equal(server, response.Transmitter);
                return response.DataBytes;
            }

            public void Dispose()
            {
                Client.Dispose();
                Server.Stop();
                Server2.Stop();
                foreach (TcpListener listener in Listeners)
                {
                    listener.Stop();
                }
            }
        }

        private static Network Started(Action<UNNRPServer>? configure = null)
        {
            Network network = new();
            configure?.Invoke(network.Server);
            network.Server.Start();
            network.Server2.Start();
            return network;
        }

        private static byte[] Req(byte type, string phrase) => [0x10, type, .. Encoding.ASCII.GetBytes(phrase)];
        private static byte[] Body(string body) => Encoding.ASCII.GetBytes(body);
        private static readonly byte[] Ok = [0x50, 0x02];
        private static readonly byte[] Error = [0x50, 0x04];
        private static byte[] Result(string body) => [0x50, 0x01, .. Body(body)];

        // ---------- UNAME ----------

        [Theory]
        [InlineData("homeserver")]
        [InlineData("Home-Server_2")]
        [InlineData("shop.retalog.com")]
        [InlineData(".a.")]
        [InlineData("a")]
        [InlineData("_")]
        public void ValidUnames(string uname)
        {
            Assert.True(UNNRPProtocol.IsValidUname(uname));
            Assert.True(UNNRPProtocol.IsAllowedUname(uname));
            _ = new RecordRegister(uname);
        }

        [Theory]
        [InlineData("")]
        [InlineData("a..b")]
        [InlineData("..")]
        [InlineData("home server")]
        [InlineData("home*")]
        [InlineData("*.retalog.com")]
        [InlineData("a/b")]
        [InlineData("a:b")]
        [InlineData("a,b")]
        [InlineData("café")]
        [InlineData("дом")]
        public void InvalidUnames(string uname)
        {
            Assert.False(UNNRPProtocol.IsValidUname(uname));
            Assert.Throws<ArgumentException>(() => new RecordRegister(uname));
            Assert.Throws<ArgumentException>(() => new RecordUnameToAfTnet(uname, new Address("7-1-2")));
            Assert.Throws<ArgumentException>(() => new RecordAfTnetToUname(new Address("7-1-2"), uname));
            Assert.Throws<ArgumentException>(() => new RecordUnameToArrayTnet(uname, [new Address("7-1-2")]));
        }

        [Theory]
        [InlineData("7-1-23")]
        [InlineData("123")]
        [InlineData("vnet8596226")]
        [InlineData("VNet1")]
        public void UnamesLikeAddressesAreRefused(string uname)
        {
            // valid characters, but a request couldn't tell them from an address
            Assert.True(UNNRPProtocol.IsValidUname(uname));
            Assert.False(UNNRPProtocol.IsAllowedUname(uname));
            Assert.Throws<ArgumentException>(() => new RecordRegister(uname));
        }

        [Fact]
        public void UnamesNearAddressesAreAllowed()
        {
            _ = new RecordRegister("7-1-23a");
            _ = new RecordRegister("vnet");
            _ = new RecordRegister("vnet12x");
            _ = new RecordRegister("-7-1");
            _ = new RecordRegister("7.1");
        }

        [Theory]
        [InlineData("home server")]
        [InlineData("a..b")]
        [InlineData("a*b")]
        [InlineData("")]
        [InlineData("7--1")]
        public void ServerAnswersErrorToAnInvalidPhrase(string phrase)
        {
            using Network network = Started(s => s.AddRecord(new RecordRegister("a.b")));
            Assert.Equal(Error, network.Raw(Req(0x01, phrase)));
            Assert.Equal(Error, network.Raw(Req(0x3F, phrase)));
        }

        [Fact]
        public void ServerAnswersErrorToNonAsciiPhrase()
        {
            using Network network = Started(s => s.AddRecord(new RecordRegister("home")));
            Assert.Equal(Error, network.Raw([0x10, 0x01, (byte)'h', 0xEF, (byte)'m', (byte)'e']));
            Assert.Equal(Error, network.Raw([0x10, 0x3F, 0xC3, 0xA9]));
        }

        [Fact]
        public void ClientRefusesAnInvalidUnameWithoutAsking()
        {
            using Network network = Started();
            Assert.Throws<ArgumentException>(() => network.Client.ResolveName(network.ServerAddress, "a..b", Long));
            Assert.Throws<ArgumentException>(() => network.Client.ResolveName(network.ServerAddress, "7-1", Long));
            Assert.Throws<ArgumentException>(() => network.Client.ResolveVideonet(network.ServerAddress, "vnetx", Long));
            Assert.Throws<ArgumentException>(() => network.Client.ResolveAddress(network.ServerAddress, new Address("a"), Long));
            Assert.Throws<ArgumentException>(() => network.Client.GetRecordType(network.ServerAddress, "déjà", Long));
            Assert.True(network.Requests.IsEmpty);
        }

        // ---------- request types ----------

        [Fact]
        public void PingIsAlwaysOk()
        {
            using Network network = Started();
            Assert.Equal(Ok, network.Raw([0x10, 0x60]));
            Assert.Equal(Ok, network.Raw(Req(0x60, "a..b ÿ")));
            Assert.Equal(Ok, network.Raw([0x10, 0x60, 0xFF, 0x00]));
            Assert.True(network.Client.Ping(network.ServerAddress, Long));
        }

        [Fact]
        public void PingOfAStoppedServerIsFalse()
        {
            using Network network = Started();
            network.Server.Stop();
            Assert.False(network.Client.Ping(network.ServerAddress, Short));
        }

        [Fact]
        public void UnknownRequestTypeIsError()
        {
            using Network network = Started(s => s.AddRecord(new RecordRegister("home")));
            foreach (byte type in new byte[] { 0x00, 0x02, 0x3E, 0x40, 0x5F, 0x61, 0xFF })
            {
                Assert.Equal(Error, network.Raw(Req(type, "home")));
            }
        }

        [Fact]
        public void NotARequestIsNotAnswered()
        {
            using Network network = Started(s => s.AddRecord(new RecordRegister("home")));
            Assert.Null(network.Raw([0x50, 0x01], timeout: Short));
            Assert.Null(network.Raw([0x50, 0x60], timeout: Short));
            Assert.Null(network.Raw([0x11, 0x60], timeout: Short));
            Assert.Null(network.Raw([0x10], timeout: Short));
            Assert.Null(network.Raw([], timeout: Short));
            // other query types aren't UNNRP
            network.ClientModem.LowLevelTransmit([0x10, 0x60], network.ServerAddress, "command", 123456, null);
            Assert.False(SpinWait.SpinUntil(() => !network.Responses.IsEmpty, Short));
        }

        [Fact]
        public void RequestTypeAnswersTheTypeOfEveryRecord()
        {
            using Network network = Started(s =>
            {
                s.AddRecord(new RecordUnameToAfTnet("a", new Address("7-9-1")));
                s.AddRecord(new RecordAfTnetToUname(new Address("7-9-1"), "a"));
                s.AddRecord(new RecordUnameToArrayTnet("arr", [new Address("7-9-1"), new Address("7-9-2")]));
                s.AddRecord(new RecordRegister("reg"));
                s.AddRecord(new RecordVideoNetwork("vnet8596226", new Address("7-9-3")));
                s.AddRecord(new RecordNameSpaceServer("*.retalog.com", new Address("7-9-4")));
            });
            Address server = network.ServerAddress;
            Assert.Equal(RecordUnameToAfTnet.RecordType, network.Client.GetRecordType(server, "a", Long));
            Assert.Equal(RecordAfTnetToUname.RecordType, network.Client.GetRecordType(server, "7-9-1", Long));
            Assert.Equal(RecordUnameToArrayTnet.RecordType, network.Client.GetRecordType(server, "arr", Long));
            Assert.Equal(RecordRegister.RecordType, network.Client.GetRecordType(server, "reg", Long));
            Assert.Equal(RecordVideoNetwork.RecordType, network.Client.GetRecordType(server, "vnet8596226", Long));
            // the server tells about its NSS record instead of redirecting (7-9-4 doesn't exist)
            Assert.Equal(RecordNameSpaceServer.RecordType, network.Client.GetRecordType(server, "shop.retalog.com", Long));
            Assert.Equal([0x50, 0x05, 0x40], network.Raw(Req(0x3F, "shop.retalog.com")));
            Assert.Equal([0x50, 0x05, 0x01], network.Raw(Req(0x3F, "A")));
            Assert.Throws<UNNRPErrorException>(() => network.Client.GetRecordType(server, "unknown", Long));
            Assert.Throws<UNNRPErrorException>(() => network.Client.GetRecordType(server, "a..b", Long));
        }

        [Fact]
        public void RecordTypeConstants()
        {
            Assert.Equal(0x01, RecordUnameToAfTnet.RecordType);
            Assert.Equal(0x7F, RecordAfTnetToUname.RecordType);
            Assert.Equal(0x20, RecordUnameToArrayTnet.RecordType);
            Assert.Equal(0xFF, RecordRegister.RecordType);
            Assert.Equal(0x88, RecordVideoNetwork.RecordType);
            Assert.Equal(0x40, RecordNameSpaceServer.RecordType);
            Assert.Equal(0x01, new RecordUnameToAfTnet("a", new Address("7-1")).Type);
            Assert.Equal(0x7F, new RecordAfTnetToUname(new Address("7-1"), "a").Type);
            Assert.Equal(0x20, new RecordUnameToArrayTnet("a", [new Address("7-1")]).Type);
            Assert.Equal(0xFF, new RecordRegister("a").Type);
            Assert.Equal(0x88, new RecordVideoNetwork(1, new Address("7-1")).Type);
            Assert.Equal(0x40, new RecordNameSpaceServer("*.a", new Address("7-1")).Type);
        }

        // ---------- records ----------

        [Fact]
        public void UnameToAddress()
        {
            using Network network = Started(s => s.AddRecord(new RecordUnameToAfTnet("homeserver", new Address("7-415-261-11516-116-11-11591-190-1"))));
            Assert.Equal([new Address("7-415-261-11516-116-11-11591-190-1")], network.Client.ResolveName(network.ServerAddress, "homeserver", Long));
            // names are compared without regard to case
            Assert.Equal([new Address("7-415-261-11516-116-11-11591-190-1")], network.Client.ResolveName(network.ServerAddress, "HomeServer", Long));
            Assert.Equal(Result("7-415-261-11516-116-11-11591-190-1"), network.Raw(Req(0x01, "homeserver")));
        }

        [Fact]
        public void UnameToArray()
        {
            using Network network = Started(s => s.AddRecord(new RecordUnameToArrayTnet("cluster", [new Address("7-1-5"), new Address("7-2-6"), new Address("8-3")])));
            Assert.Equal([new Address("7-1-5"), new Address("7-2-6"), new Address("8-3")], network.Client.ResolveName(network.ServerAddress, "cluster", Long));
            Assert.Equal(Result("7-1-5,7-2-6,8-3"), network.Raw(Req(0x01, "cluster")));
        }

        [Fact]
        public void RegisterIsAnEmptyResult()
        {
            using Network network = Started(s => s.AddRecord(new RecordRegister("taken")));
            Assert.Empty(network.Client.ResolveName(network.ServerAddress, "taken", Long));
            Assert.Equal([0x50, 0x01], network.Raw(Req(0x01, "taken")));
        }

        [Fact]
        public void AddressToUname()
        {
            using Network network = Started(s => s.AddRecord(new RecordAfTnetToUname(new Address("7-1-23"), "homeserver")));
            Assert.Equal("homeserver", network.Client.ResolveAddress(network.ServerAddress, new Address("7-1-23"), Long));
            Assert.Equal(Result("homeserver"), network.Raw(Req(0x01, "7-1-23")));
            Assert.Throws<UNNRPErrorException>(() => network.Client.ResolveAddress(network.ServerAddress, new Address("7-1-24"), Long));
        }

        [Fact]
        public void VideonetToAddress()
        {
            UInt128 max = UInt128.MaxValue;
            using Network network = Started(s =>
            {
                s.AddRecord(new RecordVideoNetwork("vnet8596226", new Address("7-1-23")));
                s.AddRecord(new RecordVideoNetwork(max, new Address("7-1-24")));
            });
            Assert.Equal(new Address("7-1-23"), network.Client.ResolveVideonet(network.ServerAddress, "vnet8596226", Long));
            Assert.Equal(new Address("7-1-23"), network.Client.ResolveVideonet(network.ServerAddress, (UInt128)8596226, Long));
            Assert.Equal(new Address("7-1-24"), network.Client.ResolveVideonet(network.ServerAddress, max, Long));
            Assert.Equal(Result("7-1-23"), network.Raw(Req(0x01, "vnet8596226")));
            // the same number written otherwise
            Assert.Equal(Result("7-1-23"), network.Raw(Req(0x01, "VNET008596226")));
            // 2^128 doesn't fit
            Assert.Equal(Error, network.Raw(Req(0x01, "vnet340282366920938463463374607431768211456")));
            Assert.Throws<ArgumentException>(() => new RecordVideoNetwork("vnet340282366920938463463374607431768211456", new Address("7-1")));
            Assert.Throws<UNNRPErrorException>(() => network.Client.ResolveVideonet(network.ServerAddress, "vnet1", Long));
        }

        [Fact]
        public void UnknownNameIsError()
        {
            using Network network = Started(s => s.AddRecord(new RecordRegister("taken")));
            UNNRPErrorException error = Assert.Throws<UNNRPErrorException>(() => network.Client.ResolveName(network.ServerAddress, "free", Long));
            Assert.Equal(network.ServerAddress, error.Server);
            Assert.Equal("free", error.Phrase);
            Assert.Equal(Error, network.Raw(Req(0x01, "free")));
        }

        [Fact]
        public void RecordsAddedWhileRunningAreAnswered()
        {
            using Network network = Started();
            Assert.Throws<UNNRPErrorException>(() => network.Client.ResolveName(network.ServerAddress, "late", Long));
            network.Server.AddRecord(new RecordUnameToAfTnet("late", new Address("7-7")));
            Assert.Equal([new Address("7-7")], network.Client.ResolveName(network.ServerAddress, "late", Long));
        }

        [Fact]
        public void RecordsRefuseInvalidAddresses()
        {
            Assert.Throws<ArgumentException>(() => new RecordUnameToAfTnet("a", new Address("7--1")));
            Assert.Throws<ArgumentException>(() => new RecordUnameToAfTnet("a", new Address("")));
            Assert.Throws<ArgumentException>(() => new RecordUnameToAfTnet("a", new Address("0")));
            Assert.Throws<ArgumentNullException>(() => new RecordUnameToAfTnet("a", null!));
            Assert.Throws<ArgumentException>(() => new RecordAfTnetToUname(new Address("7-a"), "a"));
            Assert.Throws<ArgumentException>(() => new RecordUnameToArrayTnet("a", []));
            Assert.Throws<ArgumentException>(() => new RecordUnameToArrayTnet("a", [new Address("7-1"), new Address("x")]));
            Assert.Throws<ArgumentException>(() => new RecordVideoNetwork(1, new Address("-1")));
            Assert.Throws<ArgumentException>(() => new RecordNameSpaceServer("*.a", new Address("1-")));
        }

        [Theory]
        [InlineData("retalog.com")]
        [InlineData("*retalog.com")]
        [InlineData("*.")]
        [InlineData("*..com")]
        [InlineData("*.a..b")]
        [InlineData("*.*.com")]
        [InlineData("**.com")]
        [InlineData("a.*.com")]
        [InlineData("*.re talog")]
        public void InvalidWildcards(string wildcard)
        {
            Assert.Throws<ArgumentException>(() => new RecordNameSpaceServer(wildcard, new Address("7-1")));
        }

        [Fact]
        public void RecordsCopyTheirAddresses()
        {
            Address address = new("7-1-2");
            Address[] array = [new Address("7-1-3")];
            RecordUnameToAfTnet a = new("a", address);
            RecordUnameToArrayTnet arr = new("b", array);
            address.AddressValue = "8-8";
            array[0] = new Address("9-9");
            Assert.Equal(new Address("7-1-2"), a.Address);
            Assert.Equal([new Address("7-1-3")], arr.Addresses);
        }

        // ---------- AddRecord ----------

        [Fact]
        public void NameRecordsShareOneNamespace()
        {
            UNNRPServer server = new(new FakeModem());
            server.AddRecord(new RecordUnameToAfTnet("home", new Address("7-1-2")));
            Assert.Throws<InvalidOperationException>(() => server.AddRecord(new RecordUnameToAfTnet("home", new Address("7-1-3"))));
            Assert.Throws<InvalidOperationException>(() => server.AddRecord(new RecordUnameToAfTnet("HOME", new Address("7-1-3"))));
            Assert.Throws<InvalidOperationException>(() => server.AddRecord(new RecordUnameToArrayTnet("Home", [new Address("7-1-3")])));
            Assert.Throws<InvalidOperationException>(() => server.AddRecord(new RecordRegister("home")));
            server.AddRecord(new RecordRegister("reg"));
            Assert.Throws<InvalidOperationException>(() => server.AddRecord(new RecordUnameToAfTnet("reg", new Address("7-1-3"))));
            server.AddRecord(new RecordNameSpaceServer("*.retalog.com", new Address("7-5")));
            Assert.Throws<InvalidOperationException>(() => server.AddRecord(new RecordNameSpaceServer("*.RETALOG.com", new Address("7-6"))));
            // the root of a wildcard and the wildcard are different names
            server.AddRecord(new RecordUnameToAfTnet("retalog.com", new Address("7-5-1")));
        }

        [Fact]
        public void ReverseAndVideonetRecordsHaveTheirOwnKeys()
        {
            UNNRPServer server = new(new FakeModem());
            server.AddRecord(new RecordUnameToAfTnet("home", new Address("7-1-2")));
            // a name and the reverse record of its address live together
            server.AddRecord(new RecordAfTnetToUname(new Address("7-1-2"), "home"));
            Assert.Throws<InvalidOperationException>(() => server.AddRecord(new RecordAfTnetToUname(new Address("7-1-2"), "other")));
            // several addresses may have one name
            server.AddRecord(new RecordAfTnetToUname(new Address("7-1-3"), "home"));
            server.AddRecord(new RecordVideoNetwork("vnet7", new Address("7-1-2")));
            Assert.Throws<InvalidOperationException>(() => server.AddRecord(new RecordVideoNetwork("vnet007", new Address("7-1-3"))));
            Assert.Throws<InvalidOperationException>(() => server.AddRecord(new RecordVideoNetwork(7, new Address("7-1-3"))));
            server.AddRecord(new RecordVideoNetwork(8, new Address("7-1-2")));
        }

        private sealed class ForeignRecord : IResolveRecord
        {
            public byte Type => 0x01;
            public string Key => "foreign";
        }

        [Fact]
        public void AddRecordRefusesForeignRecordsAndNull()
        {
            UNNRPServer server = new(new FakeModem());
            Assert.Throws<ArgumentException>(() => server.AddRecord(new ForeignRecord()));
            Assert.Throws<ArgumentNullException>(() => server.AddRecord(null!));
            // a refused record takes nothing
            server.AddRecord(new RecordRegister("foreign"));
        }

        [Fact]
        public void TakenNameKeepsTheFirstRecord()
        {
            using Network network = Started(s => s.AddRecord(new RecordUnameToAfTnet("home", new Address("7-1-2"))));
            Assert.Throws<InvalidOperationException>(() => network.Server.AddRecord(new RecordUnameToAfTnet("home", new Address("7-1-3"))));
            Assert.Equal([new Address("7-1-2")], network.Client.ResolveName(network.ServerAddress, "home", Long));
        }

        // ---------- NSS and REDIRECT ----------

        [Fact]
        public void NssRedirectsToAnotherServer()
        {
            using Network network = Started();
            network.Server.AddRecord(new RecordNameSpaceServer("*.retalog.com", network.Server2Address));
            network.Server2.AddRecord(new RecordUnameToAfTnet("shop.retalog.com", new Address("7-42")));
            network.Server2.AddRecord(new RecordUnameToArrayTnet("a.b.retalog.com", [new Address("7-43"), new Address("7-44")]));
            Assert.Equal([0x50, 0x03, .. Body(network.Server2Address.ToString())], network.Raw(Req(0x01, "shop.retalog.com")));
            Assert.Equal([0x50, 0x03, .. Body(network.Server2Address.ToString())], network.Raw(Req(0x01, "a.b.retalog.com")));
            Assert.Equal([0x50, 0x03, .. Body(network.Server2Address.ToString())], network.Raw(Req(0x01, "X.Retalog.COM")));
            Assert.Equal([new Address("7-42")], network.Client.ResolveName(network.ServerAddress, "shop.retalog.com", Long));
            Assert.Equal([new Address("7-43"), new Address("7-44")], network.Client.ResolveName(network.ServerAddress, "a.b.retalog.com", Long));
            // the second server has no such name
            UNNRPErrorException error = Assert.Throws<UNNRPErrorException>(() => network.Client.ResolveName(network.ServerAddress, "none.retalog.com", Long));
            Assert.Equal(network.Server2Address, error.Server);
        }

        [Fact]
        public void WildcardDoesNotCoverItsRoot()
        {
            using Network network = Started(s => s.AddRecord(new RecordNameSpaceServer("*.retalog.com", new Address("7-9"))));
            Assert.Equal(Error, network.Raw(Req(0x01, "retalog.com")));
            Assert.Equal(Error, network.Raw(Req(0x01, "xretalog.com")));
            Assert.Equal(Error, network.Raw(Req(0x01, "a.retalog.co")));
            Assert.Equal(Error, network.Raw(Req(0x01, "retalog.com.")));
            Assert.Equal([0x50, 0x03, .. Body("7-9")], network.Raw(Req(0x01, ".retalog.com")));
        }

        [Fact]
        public void ExactRecordAndLongestWildcardWin()
        {
            using Network network = Started(s =>
            {
                s.AddRecord(new RecordNameSpaceServer("*.com", new Address("7-10")));
                s.AddRecord(new RecordNameSpaceServer("*.retalog.com", new Address("7-11")));
                s.AddRecord(new RecordNameSpaceServer("*.b.retalog.com", new Address("7-12")));
                s.AddRecord(new RecordUnameToAfTnet("shop.retalog.com", new Address("7-13")));
            });
            Assert.Equal(Result("7-13"), network.Raw(Req(0x01, "shop.retalog.com")));
            Assert.Equal([0x50, 0x03, .. Body("7-12")], network.Raw(Req(0x01, "a.b.retalog.com")));
            Assert.Equal([0x50, 0x03, .. Body("7-11")], network.Raw(Req(0x01, "a.c.retalog.com")));
            Assert.Equal([0x50, 0x03, .. Body("7-11")], network.Raw(Req(0x01, "retalog.retalog.com")));
            Assert.Equal([0x50, 0x03, .. Body("7-10")], network.Raw(Req(0x01, "retalog.com")));
            Assert.Equal([0x50, 0x03, .. Body("7-10")], network.Raw(Req(0x01, "a.b.other.com")));
        }

        [Fact]
        public void RedirectLoopIsDetected()
        {
            using Network network = Started();
            network.Server.AddRecord(new RecordNameSpaceServer("*.loop", network.Server2Address));
            network.Server2.AddRecord(new RecordNameSpaceServer("*.loop", network.ServerAddress));
            UNNRPException error = Assert.Throws<UNNRPException>(() => network.Client.ResolveName(network.ServerAddress, "a.loop", Long));
            Assert.IsNotType<UNNRPErrorException>(error);
            // found by the loop check, not by MaxRedirects
            Assert.Contains("redirect loop", error.Message);
            Assert.DoesNotContain("MaxRedirects", error.Message);
        }

        [Fact]
        public void MaxRedirectsLimitsTheRedirects()
        {
            using Network network = Started();
            network.Server.AddRecord(new RecordNameSpaceServer("*.far", network.Server2Address));
            network.Server2.AddRecord(new RecordNameSpaceServer("*.far", new Address("7-99")));
            network.Client.MaxRedirects = 1;
            UNNRPException error = Assert.Throws<UNNRPException>(() => network.Client.ResolveName(network.ServerAddress, "a.far", Long));
            Assert.Contains("MaxRedirects", error.Message);
            network.Client.MaxRedirects = 0;
            Assert.Throws<UNNRPException>(() => network.Client.ResolveName(network.ServerAddress, "a.far", Long));
            Assert.Throws<ArgumentOutOfRangeException>(() => network.Client.MaxRedirects = -1);
            Assert.Equal(UNNRPClient.DefaultMaxRedirects, new UNNRPClient(network.ClientModem).MaxRedirects);
            // with 1 redirect allowed and a real record on the second server it works
            network.Client.MaxRedirects = 1;
            network.Server2.AddRecord(new RecordUnameToAfTnet("b.far", new Address("7-98")));
            Assert.Equal([new Address("7-98")], network.Client.ResolveName(network.ServerAddress, "b.far", Long));
        }

        [Fact]
        public void RedirectToAMissingServerTimesOut()
        {
            using Network network = Started(s => s.AddRecord(new RecordNameSpaceServer("*.gone", new Address("7-99"))));
            Assert.Throws<TimeoutException>(() => network.Client.ResolveName(network.ServerAddress, "a.gone", Short));
        }

        // ---------- transport ----------

        [Fact]
        public void ConnectionIDIsRandomAndEchoed()
        {
            using Network network = Started(s => s.AddRecord(new RecordRegister("taken")));
            HashSet<uint> ids = [];
            for (int i = 0; i < 5; i++)
            {
                while (network.Requests.TryDequeue(out _)) { }
                while (network.Responses.TryDequeue(out _)) { }
                network.Client.ResolveName(network.ServerAddress, "taken", Long);
                Assert.True(network.Requests.TryDequeue(out Packet? request));
                Assert.True(SpinWait.SpinUntil(() => !network.Responses.IsEmpty, Long));
                Assert.True(network.Responses.TryDequeue(out Packet? response));
                Assert.InRange(request.ConnectionID, 4000u, 800000000u);
                Assert.Equal(request.ConnectionID, response.ConnectionID);
                Assert.Equal(QT, request.QueryType);
                Assert.Equal(QT, response.QueryType);
                Assert.True(string.IsNullOrEmpty(request.Metadata));
                Assert.True(string.IsNullOrEmpty(response.Metadata));
                Assert.Equal(Req(0x01, "taken"), request.DataBytes);
                ids.Add(request.ConnectionID);
            }
            Assert.True(ids.Count >= 4, "the ConnectionIDs aren't random");
        }

        [Fact]
        public void ClientTakesOnlyTheResponseOfTheAskedServer()
        {
            // the first server is stopped; a response with the right ConnectionID from the second server must be ignored
            using Network network = Started(s => s.AddRecord(new RecordRegister("taken")));
            network.Server.Stop();
            network.Server2.Stop();
            Task<bool> ping = Task.Run(() => network.Client.Ping(network.ServerAddress, 3000));
            Assert.True(SpinWait.SpinUntil(() => !network.Requests.IsEmpty, Long));
            Assert.True(network.Requests.TryDequeue(out Packet? request));
            network.Server2Modem.LowLevelTransmit([0x50, 0x02], network.ClientAddress, QT, request.ConnectionID, null);
            Assert.False(ping.Result);
            // the same response from the asked server is taken
            ping = Task.Run(() => network.Client.Ping(network.ServerAddress, 3000));
            Assert.True(SpinWait.SpinUntil(() => !network.Requests.IsEmpty, Long));
            Assert.True(network.Requests.TryDequeue(out request));
            network.ServerModem.LowLevelTransmit([0x50, 0x02], network.ClientAddress, QT, request.ConnectionID, null);
            Assert.True(ping.Result);
            // an answer other than OK isn't a successful ping
            foreach (byte[] other in new byte[][] { [0x50, 0x04], [0x50, 0x01], [0x50, 0x02, 0x00] })
            {
                ping = Task.Run(() => network.Client.Ping(network.ServerAddress, 3000));
                Assert.True(SpinWait.SpinUntil(() => !network.Requests.IsEmpty, Long));
                Assert.True(network.Requests.TryDequeue(out request));
                network.ServerModem.LowLevelTransmit(other, network.ClientAddress, QT, request.ConnectionID, null);
                Assert.False(ping.Result);
            }
        }

        [Fact]
        public void ClientRefusesBrokenResponses()
        {
            using Network network = Started();
            network.Server.Stop();
            UNNRPException Answer(Func<Packet, byte[]> response, Action call)
            {
                Task<Exception?> task = Task.Run(() => { try { call(); return null; } catch (Exception ex) { return ex; } });
                Assert.True(SpinWait.SpinUntil(() => !network.Requests.IsEmpty, Long));
                Assert.True(network.Requests.TryDequeue(out Packet? request));
                network.ServerModem.LowLevelTransmit(response(request), network.ClientAddress, QT, request.ConnectionID, null);
                return Assert.IsAssignableFrom<UNNRPException>(task.Result);
            }
            Action resolve = () => network.Client.ResolveName(network.ServerAddress, "home", 3000);
            Answer(r => [0x50, 0x01, .. Body("7-1,x")], resolve);
            Answer(r => [0x50, 0x01, .. Body("7-1,")], resolve);
            Answer(r => [0x50, 0x02], resolve);
            Answer(r => [0x50, 0x09], resolve);
            Answer(r => [0x50, 0x05, 0x01], resolve);
            Answer(r => [0x50, 0x03, .. Body("not an address")], resolve);
            Answer(r => [0x50, 0x05], () => network.Client.GetRecordType(network.ServerAddress, "home", 3000));
            Answer(r => [0x50, 0x05, 0x01, 0x02], () => network.Client.GetRecordType(network.ServerAddress, "home", 3000));
            Answer(r => [0x50, 0x01, .. Body("a..b")], () => network.Client.ResolveAddress(network.ServerAddress, new Address("7-1"), 3000));
            Answer(r => [0x50, 0x01], () => network.Client.ResolveVideonet(network.ServerAddress, 5, 3000));
            // "0" is an empty address by the tetronet standards
            Answer(r => [0x50, 0x01, .. Body("0")], () => network.Client.ResolveVideonet(network.ServerAddress, 5, 3000));
            Answer(r => [0x50, 0x01, .. Body("7-1,0")], resolve);
        }

        [Fact]
        public void ServerAndClientOnOneModem()
        {
            // the client ignores requests, the server ignores responses
            using Network network = Started(s => s.AddRecord(new RecordUnameToAfTnet("self", new Address("7-3"))));
            using UNNRPClient client = new(network.ServerModem);
            Assert.Equal([new Address("7-3")], client.ResolveName(network.ServerAddress, "self", Long));
            Assert.True(client.Ping(network.Server2Address, Long));
        }

        [Fact]
        public void StartAfterStopWorksAgain()
        {
            using Network network = Started();
            Assert.Throws<InvalidOperationException>(network.Server.Start);
            network.Server.Stop();
            network.Server.Stop();
            Assert.False(network.Client.Ping(network.ServerAddress, Short));
            network.Server.Start();
            Assert.True(network.Client.Ping(network.ServerAddress, Long));
        }

        [Fact]
        public void TimeoutWithoutServer()
        {
            using Network network = Started();
            Assert.Throws<TimeoutException>(() => network.Client.ResolveName(new Address("7-99"), "home", Short));
            Assert.Throws<ArgumentOutOfRangeException>(() => network.Client.ResolveName(network.ServerAddress, "home", 0));
            Assert.Throws<ArgumentException>(() => network.Client.ResolveName(new Address("7--1"), "home", Long));
        }

        [Fact]
        public void NotConnectedModem()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            LowLatencyPhysicalModem notDialed = new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, L1Types.L1_INET_TCP, true) { LocalModemAddress = new Address("7-5") };
            listener.Stop();
            UNNRPServer server = new(notDialed);
            Assert.Throws<ModemNotConnectedException>(server.Start);
            using UNNRPClient client = new(notDialed);
            Assert.Throws<ModemNotConnectedException>(() => client.ResolveName(new Address("7-1"), "home", 100));
            Assert.Throws<ModemNotConnectedException>(() => client.Ping(new Address("7-1"), 100));
        }

        [Fact]
        public void DisposedClient()
        {
            using Network network = Started();
            UNNRPClient client = new(network.ClientModem);
            client.Dispose();
            client.Dispose();
            Assert.Throws<ObjectDisposedException>(() => client.ResolveName(network.ServerAddress, "home", Long));
            Assert.Throws<ObjectDisposedException>(() => client.Ping(network.ServerAddress, Long));
        }

        /// <summary>
        /// A modem, that is never used: AddRecord doesn't need the network.
        /// </summary>
        private sealed class FakeModem : IModem
        {
            public int MaxReinitializeAttempts { get; set; }
            public Address? LocalModemAddress { get; set; }
            public bool IsModemConnected => false;
            public LargeMessage? LastDownloadedLargeMessage { get; set; }
            public void Dial() => throw new NotSupportedException();
            public void Drop(bool carefulMode) => throw new NotSupportedException();
            public void Transmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0, ushort ttl = 65535) => throw new NotSupportedException();
            public void Transmit(string data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0, ushort ttl = 65535) => throw new NotSupportedException();
            public void LowLevelTransmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null, ushort ttl = 65535) => throw new NotSupportedException();
            public void AttachReceiveEvent(Action<DataBlock, Action> onReceive) => throw new NotSupportedException();
            public void AttachReceiveEventNoUnfragment(Action<Packet, Action> onReceive) => throw new NotSupportedException();
            public bool IsAddressSMLA() => false;
            public Task GetLargeMessage(Address transmitter) => throw new NotSupportedException();
            public Task GetLargeMessage(Address tx, Address rx) => throw new NotSupportedException();
            public Task GetLargeMessage(TransmitterReceiverPair txrxpair) => throw new NotSupportedException();
            public Task DeleteLargeMessage(Address transmitter) => throw new NotSupportedException();
            public Task<int> PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string? metadata = null) => throw new NotSupportedException();
            public Task DeleteLargeMessage(Address tx, Address rx) => throw new NotSupportedException();
            public Task DeleteLargeMessage(TransmitterReceiverPair txrxpair) => throw new NotSupportedException();
        }
    }
}
