using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// NCCP over loopback: a master with two children, one of them runs the server, the other one the client.
    /// </summary>
    public class NccpTests
    {
        private const int Long = 5000;
        private const int Short = 800;

        private sealed class Network : IDisposable
        {
            public readonly LowLatencyPhysicalModem Master;
            public readonly LowLatencyPhysicalModem ServerModem;
            public readonly LowLatencyPhysicalModem ClientModem;
            public readonly NCCPServer Server;
            public readonly NCCPClient Client;
            /// <summary>Packets of NCCP requests, that reached the server modem.</summary>
            public readonly ConcurrentQueue<Packet> Requests = new();
            /// <summary>Packets of NCCP responses, that reached the client modem.</summary>
            public readonly ConcurrentQueue<Packet> Responses = new();
            public readonly ConcurrentQueue<Exception> ServerErrors = new();
            public readonly ConcurrentQueue<Exception> ClientErrors = new();
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
                ClientModem = Child("client");
                ServerModem.AttachReceiveEventNoUnfragment((p, k) => { if (p.QueryType == "command") Requests.Enqueue(p); });
                ClientModem.AttachReceiveEventNoUnfragment((p, k) => { if (p.QueryType == "command") Responses.Enqueue(p); });
                Server = new(ServerModem);
                Server.ErrorOccured += ServerErrors.Enqueue;
                Client = new(ClientModem);
                Client.ErrorOccured += ClientErrors.Enqueue;
            }

            public Address ServerAddress => ServerModem.LocalModemAddress!;
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

            public string Run(string syntax, byte[]? key = null, int timeout = Long) => Client.NetworkExecuteCommand(ServerAddress, syntax, timeout, key);

            public void Dispose()
            {
                Client.Dispose();
                Server.Stop();
                foreach (TcpListener listener in Listeners)
                {
                    listener.Stop();
                }
            }
        }

        private static Network Started(Action<NCCPServer>? configure = null)
        {
            Network network = new();
            network.Server.Commands["Join"] = (a, args) => string.Join("|", args);
            network.Server.Commands["Upper"] = (a, args) => args[0].ToUpperInvariant();
            configure?.Invoke(network.Server);
            network.Server.Start();
            return network;
        }

        private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

        private static Network StartedWithKey(out byte[] key, Action<NCCPServer>? configure = null)
        {
            byte[] k = NewKey();
            Network network = Started(s =>
            {
                s.NCCPServerUsesEncryption = true;
                configure?.Invoke(s);
            });
            network.Server.NCCPEncryptionKeys[network.ClientAddress] = k;
            key = k;
            return network;
        }

        // ---------- syntax ----------

        [Fact]
        public void CommandGetsItsArgumentsAndTheAddressOfTheClient()
        {
            using Network network = Started();
            Address? seen = null;
            network.Server.Commands["Command"] = (a, args) => { seen = a; return string.Join("|", args); };
            Assert.Equal("Test1|Shit|Blablabla", network.Run("Command;Test1;Shit;Blablabla"));
            Assert.Equal(network.ClientAddress, seen);
        }

        [Fact]
        public void NestedCommandRunsFirstAndItsResultIsAnArgument()
        {
            using Network network = Started();
            List<string> order = [];
            network.Server.Commands["Command"] = (a, args) => { order.Add("Command"); return string.Join("|", args); };
            network.Server.Commands["OtherCommand"] = (a, args) => { order.Add("OtherCommand:" + args[0]); return "R(" + args[0] + ")"; };
            // the result of a nested command is passed as is, its parentheses aren't syntax anymore
            Assert.Equal("Arg1|R(Arg2)", network.Run("Command;Arg1;(OtherCommand;Arg2)"));
            Assert.Equal(["OtherCommand:Arg2", "Command"], order);
        }

        [Fact]
        public void NestedCommandsRunLeftToRightAndDepthFirst()
        {
            using Network network = Started();
            ConcurrentQueue<string> order = new();
            network.Server.Commands["L"] = (a, args) => { order.Enqueue(string.Join(",", args)); return string.Join(",", args); };
            Assert.Equal("x,a,b,c-d", network.Run("L;x;(L;a;(L;b));(L;c)-(L;d)"));
            Assert.Equal(["b", "a,b", "c", "d", "x,a,b,c-d"], order);
        }

        [Fact]
        public void TextAndNestedCommandsJoinInOneArgument()
        {
            using Network network = Started();
            Assert.Equal("preARGpost|x", network.Run("Join;pre(Upper;arg)post;x"));
            // a nested command can give the name of the command too
            network.Server.Commands["Name"] = (a, args) => "Join";
            Assert.Equal("1|2", network.Run("(Name);1;2"));
        }

        [Fact]
        public void EscapedCharactersReachTheCommandUnescaped()
        {
            using Network network = Started();
            string[]? got = null;
            network.Server.Commands["E"] = (a, args) => { got = args; return "ok"; };
            Assert.Equal("ok", network.Run(@"E;a\;b;c\(d\)e;f\\g;\\;\;"));
            Assert.Equal(["a;b", "c(d)e", @"f\g", @"\", ";"], got);
        }

        [Fact]
        public void EscapeHelperMakesAnyTextOneArgument()
        {
            using Network network = Started();
            string[]? got = null;
            network.Server.Commands["E"] = (a, args) => { got = args; return "ok"; };
            string nasty = @"a;(b);\c\\;)(";
            network.Run("E;" + NCCPClient.Escape(nasty) + ";" + NCCPClient.Escape("второй"));
            Assert.Equal([nasty, "второй"], got);
        }

        [Fact]
        public void EmptyArgumentsAndAnEmptyCommandAreKept()
        {
            using Network network = Started();
            string[]? got = null;
            network.Server.Commands["E"] = (a, args) => { got = args; return ""; };
            Assert.Equal("", network.Run("E;;x;"));
            Assert.Equal(["", "x", ""], got);
            network.Server.Commands[""] = (a, args) => "empty name, " + args.Length;
            Assert.Equal("empty name, 0", network.Run(""));
        }

        [Fact]
        public void LongCommandsAndResultsAreFragmented()
        {
            using Network network = StartedWithKey(out byte[] key);
            string big = new('ж', 5000);
            Assert.Equal(big.ToUpperInvariant() + "|" + big, network.Run("Join;(Upper;" + big + ");" + big, key));
            Assert.True(network.Requests.Count > 1);
        }

        [Theory]
        [InlineData("Join;(a")]
        [InlineData("Join;a)")]
        [InlineData("Join;a)b")]
        [InlineData(@"Join;\x")]
        [InlineData(@"Join;a\")]
        [InlineData("Join;((Upper;a)")]
        public void WrongSyntaxGoesToTheErrorHandler(string syntax)
        {
            using Network network = Started();
            int executed = 0;
            network.Server.Commands["Join"] = (a, args) => { Interlocked.Increment(ref executed); return "joined"; };
            network.Server.Commands["Upper"] = (a, args) => { Interlocked.Increment(ref executed); return "upper"; };
            Exception? error = null;
            network.Server.HandleCommandExecutionError = ex => { error = ex; return "bad syntax"; };
            Assert.Equal("bad syntax", network.Run(syntax));
            Assert.IsType<FormatException>(error);
            Assert.Equal(0, executed);
        }

        [Fact]
        public void NestingUpToTheLimitIsExecuted()
        {
            using Network network = Started(s => s.MaxNetworkCommandRecursion = 2);
            Assert.Equal("A|B|C", network.Run("Join;A;(Join;B;(Join;C))"));
        }

        [Fact]
        public void NestingOverTheLimitExecutesNothing()
        {
            using Network network = Started(s =>
            {
                s.MaxNetworkCommandRecursion = 2;
                s.MaxRecursionDepthPassedErrorMessage = "too deep";
            });
            int executed = 0;
            network.Server.Commands["Join"] = (a, args) => { Interlocked.Increment(ref executed); return "x"; };
            network.Server.HandleCommandExecutionError = ex => "error handler";
            Assert.Equal("too deep", network.Run("Join;(Join;A);(Join;B;(Join;C;(Join;D)))"));
            Assert.Equal(0, executed);
        }

        [Fact]
        public void NoNestingAtAllWithZeroLimit()
        {
            using Network network = Started(s =>
            {
                s.MaxNetworkCommandRecursion = 0;
                s.MaxRecursionDepthPassedErrorMessage = "too deep";
            });
            Assert.Equal("a|b", network.Run("Join;a;b"));
            Assert.Equal("too deep", network.Run("Join;(Upper;a)"));
        }

        [Fact]
        public void VeryDeepNestingIsRefusedWithoutStackOverflow()
        {
            using Network network = Started(s => s.MaxRecursionDepthPassedErrorMessage = "too deep");
            string deep = string.Concat(Enumerable.Repeat("Join;(", 20000)) + new string(')', 20000);
            Assert.Equal("too deep", network.Run(deep, null, 20000));
        }

        [Fact]
        public void TooBigRecursionLimitIsRefusedOnStart()
        {
            using Network network = new();
            network.Server.MaxNetworkCommandRecursion = NCCPServer.MaxAllowedRecursion + 1;
            Assert.Throws<InvalidOperationException>(network.Server.Start);
            network.Server.MaxNetworkCommandRecursion = -1;
            Assert.Throws<InvalidOperationException>(network.Server.Start);
        }

        // ---------- errors ----------

        [Fact]
        public void UnknownCommandGetsTheNameAndTheArguments()
        {
            using Network network = Started();
            string[]? got = null;
            Address? from = null;
            network.Server.UnknownCommand = (a, args) => { got = args; from = a; return "no such command"; };
            Assert.Equal("no such command", network.Run("Nope;1;2"));
            Assert.Equal(["Nope", "1", "2"], got);
            Assert.Equal(network.ClientAddress, from);
            // and its result can be nested
            Assert.Equal("no such command|x", network.Run("Join;(Nope);x"));
        }

        [Fact]
        public void ExceptionOfACommandGoesToTheErrorHandler()
        {
            using Network network = Started();
            network.Server.Commands["Fail"] = (a, args) => throw new ArithmeticException("boom");
            int outer = 0;
            network.Server.Commands["Outer"] = (a, args) => { outer++; return "outer"; };
            network.Server.HandleCommandExecutionError = ex => "handled: " + ex.GetType().Name + " " + ex.Message;
            Assert.Equal("handled: ArithmeticException boom", network.Run("Fail"));
            // a failing nested command fails the whole command
            Assert.Equal("handled: ArithmeticException boom", network.Run("Outer;(Fail)"));
            Assert.Equal(0, outer);
        }

        [Fact]
        public void ExceptionOfUnknownCommandGoesToTheErrorHandler()
        {
            using Network network = Started();
            network.Server.UnknownCommand = (a, args) => throw new KeyNotFoundException(args[0]);
            network.Server.HandleCommandExecutionError = ex => "handled " + ex.Message;
            Assert.Equal("handled Nope", network.Run("Nope"));
        }

        [Fact]
        public void NullResultIsAnError()
        {
            using Network network = Started();
            network.Server.Commands["Null"] = (a, args) => null!;
            Exception? error = null;
            network.Server.HandleCommandExecutionError = ex => { error = ex; return "null handled"; };
            Assert.Equal("null handled", network.Run("Null"));
            Assert.IsType<InvalidOperationException>(error);
        }

        [Fact]
        public void FailingErrorHandlerGivesEverythingFailed()
        {
            using Network network = Started(s => s.EverythingFailed = "all broken");
            network.Server.Commands["Fail"] = (a, args) => throw new Exception("1");
            network.Server.HandleCommandExecutionError = ex => throw new Exception("2");
            Assert.Equal("all broken", network.Run("Fail"));
            network.Server.HandleCommandExecutionError = ex => null!;
            Assert.Equal("all broken", network.Run("Fail"));
        }

        [Fact]
        public void TheWholeChainEndsInEverythingFailed()
        {
            using Network network = Started(s => s.EverythingFailed = "all broken");
            List<string> chain = [];
            network.Server.UnknownCommand = (a, args) => { chain.Add("unknown"); throw new Exception("u"); };
            network.Server.HandleCommandExecutionError = ex => { chain.Add("handler:" + ex.Message); throw new Exception("h"); };
            Assert.Equal("all broken", network.Run("Nope"));
            Assert.Equal(["unknown", "handler:u"], chain);
        }

        // ---------- client ----------

        [Fact]
        public void NoAnswerIsATimeout()
        {
            using Network network = new(); // the server isn't started
            DateTime start = DateTime.UtcNow;
            Assert.Throws<TimeoutException>(() => network.Run("Join;a", null, 500));
            Assert.InRange((DateTime.UtcNow - start).TotalMilliseconds, 450, 4000);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(16)]
        [InlineData(31)]
        [InlineData(33)]
        [InlineData(64)]
        public void KeyOfAWrongLengthIsRefused(int length)
        {
            using Network network = Started();
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => network.Run("Join;a", new byte[length]));
            Assert.Equal("AES-256-GCM key has to be exactly 32 bytes long", ex.Message);
            Assert.Empty(network.Requests);
        }

        [Fact]
        public void EmptyKeyMeansNoEncryption()
        {
            using Network network = Started();
            Assert.Equal("a|b", network.Run("Join;a;b", []));
            Assert.StartsWith("q:", Assert.Single(network.Requests).Metadata);
        }

        [Fact]
        public void ClientChecksItsArguments()
        {
            using Network network = Started();
            Assert.Throws<ArgumentOutOfRangeException>(() => network.Run("Join", null, 0));
            Assert.Throws<ArgumentException>(() => network.Client.NetworkExecuteCommand(new Address("7--1"), "Join", 100, null));
            Assert.Throws<ArgumentNullException>(() => network.Client.NetworkExecuteCommand(network.ServerAddress, null!, 100, null));
        }

        [Fact]
        public void CommandsCanRunAtTheSameTime()
        {
            using Network network = StartedWithKey(out byte[] key);
            network.Server.Commands["Slow"] = (a, args) => { Thread.Sleep(300); return args[0]; };
            string[] results = new string[8];
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => results[i] = network.Run("Slow;" + i, key));
            Assert.Equal(Enumerable.Range(0, 8).Select(i => i.ToString()), results);
        }

        // ---------- server lifecycle ----------

        [Fact]
        public void StartAfterStopWorksAgain()
        {
            using Network network = Started();
            Assert.Equal("a", network.Run("Join;a"));
            network.Server.Stop();
            network.Server.Stop(); // twice is fine
            Assert.Throws<TimeoutException>(() => network.Run("Join;a", null, Short));
            network.Server.Start();
            Assert.Equal("b", network.Run("Join;b"));
            Assert.Throws<InvalidOperationException>(network.Server.Start);
            Assert.Equal("c", network.Run("Join;c"));
        }

        [Fact]
        public void ServerNeedsAConnectedModem()
        {
            // a line, that nobody answers on: the modem is there, but not dialed
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            LowLatencyPhysicalModem notDialed = new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, L1Types.L1_INET_TCP, true) { LocalModemAddress = new Address("7-5") };
            listener.Stop();
            NCCPServer server = new(notDialed);
            Assert.Throws<ModemNotConnectedException>(server.Start);
            NCCPClient client = new(notDialed);
            Assert.Throws<ModemNotConnectedException>(() => client.NetworkExecuteCommand(new Address("7-1"), "x", 100, null));
        }

        // ---------- encryption ----------

        [Fact]
        public void EncryptedExchangeWorksAndHidesTheCommand()
        {
            using Network network = StartedWithKey(out byte[] key);
            string secret = "Join;topsecret;(Upper;password)";
            Assert.Equal("topsecret|PASSWORD", network.Run(secret, key));
            Packet request = Assert.Single(network.Requests);
            Assert.Matches("^qe:[0-9a-f]{24}:[0-9a-f]{16}:[0-9a-f]{32}$", request.Metadata);
            Assert.DoesNotContain("topsecret", Encoding.UTF8.GetString(request.DataBytes));
            Assert.Equal(Encoding.UTF8.GetByteCount(secret), request.DataBytes.Length);
            Assert.True(SpinWait.SpinUntil(() => !network.Responses.IsEmpty, Long));
            Packet response = Assert.Single(network.Responses);
            string requestNonce = request.Metadata!.Split(':')[1];
            Assert.Matches("^re:" + requestNonce + ":[0-9a-f]{24}:[0-9a-f]{32}$", response.Metadata);
            // a nonce of its own for the response
            Assert.NotEqual(requestNonce, response.Metadata!.Split(':')[2]);
            Assert.DoesNotContain("PASSWORD", Encoding.UTF8.GetString(response.DataBytes));
        }

        [Fact]
        public void EveryRequestHasANewNonce()
        {
            using Network network = StartedWithKey(out byte[] key);
            for (int i = 0; i < 5; i++)
            {
                network.Run("Join;same", key);
            }
            Assert.Equal(5, network.Requests.Select(p => p.Metadata!.Split(':')[1]).Distinct().Count());
            Assert.True(SpinWait.SpinUntil(() => network.Responses.Count == 5, Long));
            Assert.Equal(5, network.Responses.Select(p => p.Metadata!.Split(':')[2]).Distinct().Count());
        }

        [Fact]
        public void WrongKeyIsNotExecuted()
        {
            using Network network = StartedWithKey(out _);
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            Assert.Throws<TimeoutException>(() => network.Run("Do", NewKey(), Short));
            Assert.Equal(0, executed);
            Assert.Contains(network.ServerErrors, e => e is UnauthorizedAccessException);
        }

        [Fact]
        public void AddressWithoutAKeyIsNotExecuted()
        {
            using Network network = StartedWithKey(out byte[] key);
            network.Server.NCCPEncryptionKeys.Clear();
            network.Server.NCCPEncryptionKeys[new Address("7-1-99")] = key; // the key of another address
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            Assert.Throws<TimeoutException>(() => network.Run("Do", key, Short));
            Assert.Equal(0, executed);
        }

        [Fact]
        public void RequestWithoutEncryptionIsDroppedByAnEncryptingServer()
        {
            using Network network = StartedWithKey(out _);
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            Assert.Throws<TimeoutException>(() => network.Run("Do", null, Short));
            Assert.Equal(0, executed);
            Assert.Empty(network.Responses);
        }

        [Fact]
        public void EncryptedRequestIsDroppedByAServerWithoutEncryption()
        {
            using Network network = Started();
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            Assert.Throws<TimeoutException>(() => network.Run("Do", NewKey(), Short));
            Assert.Equal(0, executed);
        }

        private static (byte[] Data, string Metadata) CaptureRequest(Network network, byte[] key, string syntax)
        {
            Assert.Equal("done", network.Run(syntax, key));
            Packet request = Assert.Single(network.Requests);
            while (network.Requests.TryDequeue(out _)) { }
            return (request.DataBytes, request.Metadata!);
        }

        private static void Inject(Network network, byte[] data, string metadata) =>
            network.ClientModem.LowLevelTransmit(data, network.ServerAddress, "command", ConnectionIDDefaults.COMMAND, metadata);

        private static string Flip(string hex, int index) => hex[..index] + (hex[index] == '0' ? '1' : '0') + hex[(index + 1)..];

        [Theory]
        [InlineData("tag")]
        [InlineData("data")]
        [InlineData("time")]
        [InlineData("nonce")]
        public void TamperedRequestIsNotExecuted(string what)
        {
            using Network network = StartedWithKey(out byte[] key);
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            (byte[] data, string metadata) = CaptureRequest(network, key, "Do;x");
            Assert.Equal(1, executed);
            string[] f = metadata.Split(':'); // qe, nonce, time, tag
            switch (what)
            {
                case "tag": f[3] = Flip(f[3], 31); break;
                case "data": data = (byte[])data.Clone(); data[^1] ^= 1; break;
                case "time": f[2] = Flip(f[2], 15); break;
                case "nonce": f[1] = Flip(f[1], 0); break;
            }
            network.ServerErrors.Clear();
            Inject(network, data, string.Join(':', f));
            Assert.True(SpinWait.SpinUntil(() => !network.ServerErrors.IsEmpty, Long));
            Thread.Sleep(200);
            Assert.Equal(1, executed);
            Assert.Contains(network.ServerErrors, e => e is UnauthorizedAccessException && e.Message.Contains("tag"));
        }

        [Fact]
        public void RepeatedRequestIsNotExecutedAgain()
        {
            using Network network = StartedWithKey(out byte[] key);
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            (byte[] data, string metadata) = CaptureRequest(network, key, "Do");
            network.ServerErrors.Clear();
            Inject(network, data, metadata);
            // the same nonce in upper case is the same request
            string[] f = metadata.Split(':');
            f[1] = f[1].ToUpperInvariant();
            Inject(network, data, string.Join(':', f));
            Assert.True(SpinWait.SpinUntil(() => network.ServerErrors.Count >= 2, Long));
            Thread.Sleep(200);
            Assert.Equal(1, executed);
            Assert.All(network.ServerErrors, e => Assert.Contains("copy", e.Message));
        }

        [Fact]
        public void RepeatedRequestIsNotExecutedAgainWithoutTheClock()
        {
            using Network network = StartedWithKey(out byte[] key, s => s.ReplayWindowSeconds = 0);
            network.Server.UnixTimeSeconds = () => 0; // the clock is ignored with ReplayWindowSeconds = 0
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            (byte[] data, string metadata) = CaptureRequest(network, key, "Do");
            network.ServerErrors.Clear();
            Inject(network, data, metadata);
            Assert.True(SpinWait.SpinUntil(() => !network.ServerErrors.IsEmpty, Long));
            Assert.Equal(1, executed);
        }

        [Fact]
        public void WithoutTheClockTheOldestNonceIsForgotten()
        {
            using Network network = StartedWithKey(out byte[] key, s =>
            {
                s.ReplayWindowSeconds = 0;
                s.MaxRememberedNonces = 2;
            });
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            (byte[] data, string metadata) = CaptureRequest(network, key, "Do");
            CaptureRequest(network, key, "Do");
            CaptureRequest(network, key, "Do");
            Assert.Equal(3, executed);
            // the first nonce is forgotten: documented as the price of having no clock
            Inject(network, data, metadata);
            Assert.True(SpinWait.SpinUntil(() => executed == 4, Long));
        }

        [Fact]
        public void FullNonceMemoryDropsRequestsWithTheClock()
        {
            using Network network = StartedWithKey(out byte[] key, s => s.MaxRememberedNonces = 1);
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            CaptureRequest(network, key, "Do");
            Assert.Throws<TimeoutException>(() => network.Run("Do", key, Short));
            Assert.Equal(1, executed);
        }

        [Theory]
        [InlineData(1000)]
        [InlineData(-1000)]
        public void RequestOutsideOfTheWindowIsNotExecuted(int serverClockShift)
        {
            using Network network = StartedWithKey(out byte[] key, s => s.ReplayWindowSeconds = 60);
            network.Server.UnixTimeSeconds = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + serverClockShift;
            int executed = 0;
            network.Server.Commands["Do"] = (a, args) => { Interlocked.Increment(ref executed); return "done"; };
            Assert.Throws<TimeoutException>(() => network.Run("Do", key, Short));
            Assert.Equal(0, executed);
            Assert.Contains(network.ServerErrors, e => e.Message.Contains("ReplayWindowSeconds"));
            // inside the window it's fine
            network.Server.UnixTimeSeconds = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Sign(serverClockShift) * 50;
            Assert.Equal("done", network.Run("Do", key));
        }

        [Fact]
        public void ResponseOfAnotherRequestIsNotAccepted()
        {
            using Network network = StartedWithKey(out byte[] key);
            network.Server.Commands["Do"] = (a, args) => "result " + args[0];
            Assert.Equal("result 1", network.Run("Do;1", key));
            Assert.True(SpinWait.SpinUntil(() => !network.Responses.IsEmpty, Long));
            Packet oldResponse = Assert.Single(network.Responses);
            while (network.Requests.TryDequeue(out _)) { }
            network.Server.Stop();
            // a new request, that the server doesn't answer; the old response is sent to it with the ID of the new request
            Task<string> pending = Task.Run(() => network.Run("Do;2", key, 3000));
            Assert.True(SpinWait.SpinUntil(() => !network.Requests.IsEmpty, Long));
            string newId = network.Requests.First().Metadata!.Split(':')[1];
            string[] f = oldResponse.Metadata!.Split(':');
            network.ServerModem.LowLevelTransmit(oldResponse.DataBytes, network.ClientAddress, "command", ConnectionIDDefaults.COMMAND, $"re:{newId}:{f[2]}:{f[3]}");
            Assert.True(SpinWait.SpinUntil(() => !network.ClientErrors.IsEmpty, Long));
            Assert.Contains(network.ClientErrors, e => e is AuthenticationTagMismatchException);
            // a response without encryption to an encrypted request isn't taken either
            network.ServerModem.LowLevelTransmit(Encoding.UTF8.GetBytes("forged"), network.ClientAddress, "command", ConnectionIDDefaults.COMMAND, $"r:{newId}");
            Assert.True(SpinWait.SpinUntil(() => network.ClientErrors.Count >= 2, Long));
            Assert.Contains(network.ClientErrors, e => e is InvalidDataException && e.Message.Contains("isn't encrypted"));
            Assert.Throws<AggregateException>(() => pending.Wait());
            Assert.IsType<TimeoutException>(pending.Exception!.InnerException);
        }

        [Fact]
        public void ResponseIsBoundToItsRequestByTheAad()
        {
            // the same key, nonce, data and tag, but another request nonce in the AAD: the tag doesn't match
            byte[] key = NewKey();
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] data = NCCPProtocol.Encrypt(key, nonce, Encoding.UTF8.GetBytes("answer"), NCCPProtocol.ResponseAad("7-1-10", "7-1-11", "aa"), out byte[] tag);
            Assert.Equal("answer", Encoding.UTF8.GetString(NCCPProtocol.Decrypt(key, nonce, data, tag, NCCPProtocol.ResponseAad("7-1-10", "7-1-11", "aa"))));
            Assert.ThrowsAny<CryptographicException>(() => NCCPProtocol.Decrypt(key, nonce, data, tag, NCCPProtocol.ResponseAad("7-1-10", "7-1-11", "ab")));
            Assert.ThrowsAny<CryptographicException>(() => NCCPProtocol.Decrypt(key, nonce, data, tag, NCCPProtocol.ResponseAad("7-1-12", "7-1-11", "aa")));
            // and a request can't pass for a response or back
            Assert.ThrowsAny<CryptographicException>(() => NCCPProtocol.Decrypt(key, nonce, data, tag, NCCPProtocol.RequestAad("7-1-10", "7-1-11", "aa")));
        }

        [Fact]
        public void KeyOfAClientIsCopiedOnTheCall()
        {
            using Network network = StartedWithKey(out byte[] key);
            network.Server.Commands["Slow"] = (a, args) => { Thread.Sleep(300); return "slow"; };
            byte[] mine = (byte[])key.Clone();
            Task<string> call = Task.Run(() => network.Client.NetworkExecuteCommand(network.ServerAddress, "Slow", Long, mine));
            Thread.Sleep(100);
            Array.Clear(mine);
            Assert.Equal("slow", call.Result);
        }
    }
}
