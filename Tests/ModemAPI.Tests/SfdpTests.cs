using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// SFDP: the text format of the data, and the exchange over loopback (a master with two children, one of them runs the server,
    /// the other one the client).
    /// </summary>
    public class SfdpTests
    {
        private const int Long = 5000;
        private const int Short = 800;
        private const uint RangeStart = 1000;
        private const uint RangeEnd = 1010;
        private const uint InRange = 1005;

        private sealed class Network : IDisposable
        {
            public readonly LowLatencyPhysicalModem Master;
            public readonly LowLatencyPhysicalModem ServerModem;
            public readonly LowLatencyPhysicalModem ClientModem;
            public readonly SFDPServer Server;
            public readonly SFDPClient Client;
            /// <summary>Packets of SFDP requests, that reached the server modem.</summary>
            public readonly ConcurrentQueue<Packet> Requests = new();
            /// <summary>Packets of SFDP responses, that reached the client modem.</summary>
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
                ServerModem.AttachReceiveEventNoUnfragment((p, k) => { if (p.QueryType == "sfd_object") Requests.Enqueue(p); });
                ClientModem.AttachReceiveEventNoUnfragment((p, k) => { if (p.QueryType == "sfd_object") Responses.Enqueue(p); });
                Server = new(ServerModem, RangeStart, RangeEnd);
                Server.ErrorOccured += ServerErrors.Enqueue;
                Client = new(ClientModem) { PacketConnectionID = InRange };
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

            public string? Raw(string data, byte[]? key = null, int timeout = Long) => Client.SendDataRaw(ServerAddress, data, timeout, key);

            /// <summary>Sends a request bypassing the client, from the client modem.</summary>
            public void Inject(byte[] data, string metadata, uint connectionId = InRange) =>
                ClientModem.LowLevelTransmit(data, ServerAddress, "sfd_object", connectionId, metadata);

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

        /// <summary>The server echoes the parsed data back.</summary>
        private static Network Started(Action<SFDPServer>? configure = null)
        {
            Network network = new();
            network.Server.OnData = (a, data) => data;
            configure?.Invoke(network.Server);
            network.Server.Start();
            return network;
        }

        private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

        private static Network StartedWithKey(out byte[] key, Action<SFDPServer>? configure = null)
        {
            byte[] k = NewKey();
            Network network = Started(s =>
            {
                s.SFDPServerUsesEncryption = true;
                configure?.Invoke(s);
            });
            network.Server.SFDPEncryptionKeys[network.ClientAddress] = k;
            key = k;
            return network;
        }

        public enum Color { Red, Green }

        public class Inner
        {
            public string? Name { get; set; }
            public int Number { get; set; }
        }

        public class Sample
        {
            public string? Text { get; set; }
            public int Int { get; set; }
            public long Long { get; set; }
            public double Double { get; set; }
            public decimal Decimal { get; set; }
            public bool Bool { get; set; }
            public char Char { get; set; }
            public Color Color { get; set; }
            public DateTime Time { get; set; }
            public int? Nullable { get; set; }
            public Inner? Inner { get; set; }
            public Dictionary<string, string>? Map { get; set; }
            public string? Missing { get; set; }
            public string ReadOnly => "computed";
        }

        private static Sample NewSample() => new()
        {
            Text = "with spaces \"quotes\" \\ and\nline feeds",
            Int = -47,
            Long = long.MaxValue,
            Double = 0.1 + 0.2,
            Decimal = 12345.678m,
            Bool = true,
            Char = 'ж',
            Color = Color.Green,
            Time = new DateTime(2026, 11, 22, 10, 20, 30, 123, DateTimeKind.Utc),
            Nullable = 5,
            Inner = new() { Name = "kek", Number = 85 },
            Map = new() { ["a"] = "1", ["b"] = "" },
        };

        private static void AssertSame(Sample expected, Sample actual)
        {
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Int, actual.Int);
            Assert.Equal(expected.Long, actual.Long);
            Assert.Equal(expected.Double, actual.Double);
            Assert.Equal(expected.Decimal, actual.Decimal);
            Assert.Equal(expected.Bool, actual.Bool);
            Assert.Equal(expected.Char, actual.Char);
            Assert.Equal(expected.Color, actual.Color);
            Assert.Equal(expected.Time, actual.Time);
            Assert.Equal(expected.Nullable, actual.Nullable);
            Assert.Equal(expected.Inner?.Name, actual.Inner?.Name);
            Assert.Equal(expected.Inner?.Number, actual.Inner?.Number);
            Assert.Equal(expected.Map, actual.Map);
            Assert.Equal(expected.Missing, actual.Missing);
        }

        // ---------- format ----------

        private const string Example =
            "ObjectKey: objectValue\n" +
            "DictionaryThing:\n" +
            "--ObjectKey: 47\n" +
            "--AnotherObjectKey: \"this string contains spaces so it must be in quotes\"\n" +
            "--DictionaryInside:\n" +
            "----Lol: kek\n" +
            "----Number: 85\n" +
            "----SomethingElse: FUUUUUUCK\n" +
            "--ThisBelongsToDictionaryThing: sure_it_does_tbh\n" +
            "ThisIsOnTheRootLevel: yup";

        [Fact]
        public void TheExampleOfTheSpecificationIsParsed()
        {
            SFDPObject root = SFDPObject.Parse(Example);
            Assert.Equal(["ObjectKey", "DictionaryThing", "ThisIsOnTheRootLevel"], root.Keys);
            Assert.Equal("objectValue", root.GetString("ObjectKey"));
            Assert.Equal("yup", root.GetString("ThisIsOnTheRootLevel"));
            SFDPObject thing = root.GetObject("DictionaryThing")!;
            Assert.Equal(["ObjectKey", "AnotherObjectKey", "DictionaryInside", "ThisBelongsToDictionaryThing"], thing.Keys);
            Assert.Equal("47", thing.GetString("ObjectKey"));
            Assert.Equal("this string contains spaces so it must be in quotes", thing.GetString("AnotherObjectKey"));
            Assert.Equal("sure_it_does_tbh", thing.GetString("ThisBelongsToDictionaryThing"));
            SFDPObject inside = thing.GetObject("DictionaryInside")!;
            Assert.Equal("kek", inside["Lol"]);
            Assert.Equal("85", inside["Number"]);
            Assert.Equal("FUUUUUUCK", inside["SomethingElse"]);
            Assert.Null(root["Nope"]);
            Assert.Null(root.GetObject("ObjectKey"));
            Assert.Null(root.GetString("DictionaryThing"));
            // written back exactly as it was
            Assert.Equal(Example, root.ToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("two words")]
        [InlineData("\"")]
        [InlineData("\\")]
        [InlineData("a\\nb")]
        [InlineData("line\nfeed")]
        [InlineData("\r\t\x01")]
        [InlineData("ends with \\")]
        [InlineData("key: value")]
        [InlineData("\"quoted\"")]
        [InlineData("--dashes")]
        [InlineData("юникод 😀")]
        public void AnyValueIsWrittenAndReadBack(string value)
        {
            SFDPObject root = new() { ["Key"] = value };
            string text = root.ToString();
            Assert.Single(text.Split('\n'));
            Assert.Equal(value, SFDPObject.Parse(text).GetString("Key"));
        }

        [Theory]
        [InlineData("plain", "K: plain")]
        [InlineData("a:b\\c", "K: a:b\\c")]
        [InlineData("", "K: \"\"")]
        [InlineData("a b", "K: \"a b\"")]
        [InlineData("say \"hi\"\n\\", "K: \"say \\\"hi\\\"\\n\\\\\"")]
        public void ValuesAreQuotedOnlyWhenTheyHaveTo(string value, string expected)
        {
            Assert.Equal(expected, new SFDPObject { ["K"] = value }.ToString());
            Assert.Equal(value, SFDPObject.Parse(expected).GetString("K"));
        }

        [Theory]
        [InlineData("\n")]
        [InlineData("\r\n")]
        [InlineData("\r")]
        [InlineData("\n\r")]
        public void AnyLineBreakIsTaken(string lineBreak)
        {
            SFDPObject root = SFDPObject.Parse(Example.Replace("\n", lineBreak) + lineBreak);
            Assert.Equal(Example, root.ToString());
            Assert.Equal("85", root.GetObject("DictionaryThing")!.GetObject("DictionaryInside")!["Number"]);
        }

        [Fact]
        public void MixedLineBreaksAreTaken()
        {
            SFDPObject root = SFDPObject.Parse("A: 1\r\nB:\r--C: 2\n\r--D: 3\nE: 4\r\n\r\n\n\rF: 5");
            Assert.Equal("A: 1\nB:\n--C: 2\n--D: 3\nE: 4\nF: 5", root.ToString());
        }

        [Fact]
        public void CarriageReturnInAValueIsEscaped()
        {
            Assert.Equal("K: \"a\\rb\\r\\n\"", new SFDPObject { ["K"] = "a\rb\r\n" }.ToString());
            Assert.Equal("a\rb\r\n", SFDPObject.Parse("K: \"a\\rb\\r\\n\"").GetString("K"));
        }

        [Fact]
        public void EmptyObjectsAndEmptyLinesAreFine()
        {
            SFDPObject root = SFDPObject.Parse("\nA:\nB:\n--C:\n\nD: x\n");
            Assert.Equal(0, root.GetObject("A")!.Count);
            Assert.Equal(0, root.GetObject("B")!.GetObject("C")!.Count);
            Assert.Equal("x", root.GetString("D"));
            Assert.Equal("A:\nB:\n--C:\nD: x", root.ToString());
            Assert.Equal(0, SFDPObject.Parse("").Count);
            Assert.Equal("", new SFDPObject().ToString());
        }

        [Theory]
        [InlineData("Key:value")]
        [InlineData("Key:  two")]
        [InlineData("Key: a b")]
        [InlineData("Key: a\"b")]
        [InlineData("Key: \"unclosed")]
        [InlineData("Key: \"a\" b")]
        [InlineData("Key: \"bad \\q escape\"")]
        [InlineData("Key: \"ends with \\")]
        [InlineData("Key: ")]
        [InlineData("--Key: x")]
        [InlineData("A:\n----Key: x")]
        [InlineData("A: x\n--Key: y")]
        [InlineData("-Key: x")]
        [InlineData("---Key: x")]
        [InlineData(": x")]
        [InlineData("Key x")]
        [InlineData("Ke y: x")]
        [InlineData("Ke\"y: x")]
        [InlineData("Key: x\nKey: y")]
        [InlineData("Key: x\rKey: y")]
        [InlineData("Key: \"a\rb\"")]
        public void BrokenDataIsRefused(string data)
        {
            Assert.Throws<FormatException>(() => SFDPObject.Parse(data));
        }

        [Fact]
        public void NestingIsLimited()
        {
            StringBuilder ok = new();
            for (int i = 0; i < SFDPObject.MaxDepth; i++)
            {
                ok.Append('-', i * 2).Append("L:\n");
            }
            Assert.NotNull(SFDPObject.Parse(ok.ToString()));
            ok.Append('-', SFDPObject.MaxDepth * 2).Append("L:\n");
            Assert.Throws<FormatException>(() => SFDPObject.Parse(ok.ToString()));
            // an object nested into itself can't be written
            SFDPObject loop = new();
            loop["Self"] = loop;
            Assert.Throws<InvalidOperationException>(loop.ToString);
        }

        [Fact]
        public void FieldsCanBeChanged()
        {
            SFDPObject root = new() { ["A"] = "1", ["B"] = new SFDPObject { ["C"] = "2" }, ["D"] = "3" };
            root["A"] = "changed";
            root["B"] = null;
            root["E"] = "4";
            Assert.Equal("A: changed\nD: 3\nE: 4", root.ToString());
            Assert.Throws<ArgumentException>(() => root["bad key"] = "x");
            Assert.Throws<ArgumentException>(() => root["-bad"] = "x");
            Assert.Throws<ArgumentException>(() => root[""] = "x");
            Assert.Throws<ArgumentException>(() => root["K"] = 5);
        }

        [Fact]
        public void ObjectsAreConvertedByTheirProperties()
        {
            Sample sample = NewSample();
            SFDPObject converted = SFDPObject.FromObject(sample);
            Assert.Equal("-47", converted["Int"]);
            Assert.Equal("True", converted["Bool"]);
            Assert.Equal("Green", converted["Color"]);
            Assert.Equal("computed", converted["ReadOnly"]);
            Assert.Null(converted["Missing"]); // null properties are left out
            Assert.Equal("kek", converted.GetObject("Inner")!["Name"]);
            Assert.Equal("", converted.GetObject("Map")!["b"]);
            Sample back = SFDPObject.Parse(converted.ToString()).ToObject<Sample>();
            AssertSame(sample, back);
        }

        [Fact]
        public void ConversionDoesNotDependOnTheCulture()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
                SFDPObject converted = SFDPObject.FromObject(new Sample { Double = 1.5, Decimal = 2.25m });
                Assert.Equal("1.5", converted["Double"]);
                Assert.Equal("2.25", converted["Decimal"]);
                Assert.Equal(1.5, converted.ToObject<Sample>().Double);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Fact]
        public void WrongValuesAndUnsupportedTypesAreRefused()
        {
            Assert.Throws<FormatException>(() => SFDPObject.Parse("Int: abc").ToObject<Sample>());
            Assert.Throws<FormatException>(() => SFDPObject.Parse("Int: 99999999999").ToObject<Sample>());
            Assert.Throws<FormatException>(() => SFDPObject.Parse("Inner: x").ToObject<Sample>());
            Assert.Throws<FormatException>(() => SFDPObject.Parse("Int:").ToObject<Sample>());
            Assert.Throws<NotSupportedException>(() => SFDPObject.FromObject(new { List = new[] { 1, 2 } }));
            Assert.Throws<NotSupportedException>(() => SFDPObject.FromObject(new List<string>()));
            Assert.Throws<NotSupportedException>(() => SFDPObject.FromObject("just a string"));
            Assert.Throws<NotSupportedException>(() => SFDPObject.FromObject(5));
            Assert.Throws<ArgumentException>(() => SFDPObject.FromObject(new Dictionary<string, string> { ["bad key"] = "x" }));
            // unknown fields are ignored
            Assert.Equal(7, SFDPObject.Parse("Int: 7\nUnknown: x").ToObject<Sample>().Int);
            // dictionaries and SFDPObject itself work as the result type
            Dictionary<string, string> map = SFDPObject.Parse("a: 1\nb: 2").ToObject<Dictionary<string, string>>();
            Assert.Equal("2", map["b"]);
            Assert.Equal("1", SFDPObject.Parse("a: 1").ToObject<SFDPObject>()["a"]);
        }

        // ---------- exchange ----------

        [Fact]
        public void PingIsAnsweredAndReported()
        {
            using Network network = Started();
            ConcurrentQueue<(Address, uint)> pings = new();
            network.Server.OnPing += (a, id) => pings.Enqueue((a, id));
            Assert.True(network.Client.CheckSystem(network.ServerAddress, Long));
            (Address address, uint connectionId) = Assert.Single(pings);
            Assert.Equal(network.ClientAddress, address);
            Assert.Equal(InRange, connectionId);
            Packet request = Assert.Single(network.Requests);
            Assert.Equal("      SFD_PING", Encoding.UTF8.GetString(request.DataBytes));
            Assert.Matches("^q:[0-9a-f]{24}$", request.Metadata);
            Assert.True(SpinWait.SpinUntil(() => !network.Responses.IsEmpty, Long));
            Packet response = Assert.Single(network.Responses);
            Assert.Equal("      SFD_PONG", Encoding.UTF8.GetString(response.DataBytes));
            Assert.Equal("r:" + request.Metadata![2..], response.Metadata);
            Assert.Equal(InRange, response.ConnectionID);
        }

        [Fact]
        public void PingWithoutAnAnswerIsFalse()
        {
            using Network network = new(); // the server isn't started
            DateTime start = DateTime.UtcNow;
            Assert.False(network.Client.CheckSystem(network.ServerAddress, 500));
            Assert.InRange((DateTime.UtcNow - start).TotalMilliseconds, 450, 4000);
        }

        [Fact]
        public void ConnectionIdOutsideOfTheRangeIsIgnored()
        {
            using Network network = Started();
            network.Client.PacketConnectionID = RangeEnd + 1;
            Assert.False(network.Client.CheckSystem(network.ServerAddress, Short));
            network.Client.PacketConnectionID = RangeStart - 1;
            Assert.False(network.Client.CheckSystem(network.ServerAddress, Short));
            network.Client.PacketConnectionID = RangeStart;
            Assert.True(network.Client.CheckSystem(network.ServerAddress, Long));
            network.Client.PacketConnectionID = RangeEnd;
            Assert.True(network.Client.CheckSystem(network.ServerAddress, Long));
            Assert.Empty(network.ServerErrors);
        }

        [Fact]
        public void RawDataGoesAsItIsWithTheHeader()
        {
            using Network network = Started();
            ConcurrentQueue<(Address, string)> raw = new();
            network.Server.OnDataRaw += (a, data) => raw.Enqueue((a, data));
            Assert.Equal("Key: value\nOther: \"a b\"", network.Raw("Key: value\nOther: \"a b\""));
            (Address address, string data) = Assert.Single(raw);
            Assert.Equal(network.ClientAddress, address);
            Assert.Equal("Key: value\nOther: \"a b\"", data);
            Assert.Equal("      SFD_DATA_TRANSFER\nKey: value\nOther: \"a b\"", Encoding.UTF8.GetString(Assert.Single(network.Requests).DataBytes));
            Assert.True(SpinWait.SpinUntil(() => !network.Responses.IsEmpty, Long));
            Assert.Equal("      SFD_DATA_RESPONSE\nKey: value\nOther: \"a b\"", Encoding.UTF8.GetString(Assert.Single(network.Responses).DataBytes));
        }

        [Fact]
        public void OnDataGetsTheParsedDataAndTheClient()
        {
            using Network network = Started();
            Address? client = null;
            network.Server.OnData = (a, data) =>
            {
                client = a;
                return new SFDPObject { ["Sum"] = (int.Parse(data.GetString("A")!) + int.Parse(data.GetObject("Nested")!.GetString("B")!)).ToString() };
            };
            Assert.Equal("Sum: 5", network.Raw("A: 2\nNested:\n--B: 3"));
            Assert.Equal(network.ClientAddress, client);
        }

        [Fact]
        public void NullFromOnDataIsNoData()
        {
            using Network network = Started(s => s.OnData = (a, data) => null);
            Assert.Null(network.Raw("A: 1"));
            Assert.Null(network.Client.SendData<Inner, Inner>(network.ServerAddress, new Inner { Name = "x" }, Long, null));
            Assert.True(SpinWait.SpinUntil(() => network.Responses.Count == 2, Long));
            Assert.All(network.Responses, p => Assert.Equal("      SFD_NO_DATA", Encoding.UTF8.GetString(p.DataBytes)));
        }

        [Fact]
        public void WithoutOnDataTheDataIsNotParsedAndNoDataIsSent()
        {
            using Network network = Started(s => s.OnData = null);
            ConcurrentQueue<string> raw = new();
            network.Server.OnDataRaw += (a, data) => raw.Enqueue(data);
            Assert.Null(network.Raw("anything at all, not SFDP: \"\n"));
            Assert.Equal("anything at all, not SFDP: \"\n", Assert.Single(raw));
            Assert.Empty(network.ServerErrors);
        }

        [Fact]
        public void StringFromOnDataIsSentAsItIs()
        {
            using Network network = Started(s => s.OnData = (a, data) => "not parsed: \"");
            Assert.Equal("not parsed: \"", network.Raw("A: 1"));
        }

        [Fact]
        public void EmptyDataIsSentToo()
        {
            using Network network = Started(s => s.OnData = (a, data) => data.Count == 0 ? "" : "not empty");
            ConcurrentQueue<string> raw = new();
            network.Server.OnDataRaw += (a, data) => raw.Enqueue(data);
            Assert.Equal("", network.Raw(""));
            Assert.Equal("", Assert.Single(raw));
        }

        [Fact]
        public void TypedDataGoesThereAndBack()
        {
            using Network network = Started();
            Sample sample = NewSample();
            Sample? back = network.Client.SendData<Sample, Sample>(network.ServerAddress, sample, Long, null);
            Assert.NotNull(back);
            AssertSame(sample, back);
            // a server can answer with any object
            network.Server.OnData = (a, data) => new Inner { Name = data.GetString("Text"), Number = 1 };
            Inner? inner = network.Client.SendData<Sample, Inner>(network.ServerAddress, sample, Long, null);
            Assert.Equal(sample.Text, inner!.Name);
            Assert.Equal(1, inner.Number);
        }

        [Fact]
        public void FailuresOfTheServerHandlersAreNoData()
        {
            using Network network = Started(s => s.OnData = (a, data) => throw new InvalidOperationException("boom"));
            network.Server.OnDataRaw += (a, data) => throw new ApplicationException("raw boom");
            Assert.Null(network.Raw("A: 1"));
            Assert.Contains(network.ServerErrors, e => e.Message == "boom");
            Assert.Contains(network.ServerErrors, e => e.Message == "raw boom");
            // data, that isn't SFDP, with OnData set
            network.Server.OnData = (a, data) => data;
            network.ServerErrors.Clear();
            Assert.Null(network.Raw("not sfdp"));
            Assert.Contains(network.ServerErrors, e => e is FormatException);
            // a result, that can't be written
            network.Server.OnData = (a, data) => new { List = new[] { 1 } };
            network.ServerErrors.Clear();
            Assert.Null(network.Raw("A: 1"));
            Assert.Contains(network.ServerErrors, e => e is NotSupportedException);
            // a failing OnPing doesn't stop the pong
            network.Server.OnPing += (a, id) => throw new ApplicationException("ping boom");
            Assert.True(network.Client.CheckSystem(network.ServerAddress, Long));
            Assert.Contains(network.ServerErrors, e => e.Message == "ping boom");
        }

        [Fact]
        public void ResponseThatIsNotSfdpFailsTheTypedCall()
        {
            using Network network = Started(s => s.OnData = (a, data) => "not sfdp");
            Assert.Throws<FormatException>(() => network.Client.SendData<Inner, Inner>(network.ServerAddress, new Inner(), Long, null));
        }

        [Theory]
        [InlineData("SFD_PING")]
        [InlineData("     SFD_PING")]
        [InlineData("       SFD_PING")]
        [InlineData("      SFD_PING ")]
        [InlineData("      sfd_ping")]
        [InlineData("      SFD_PONG")]
        [InlineData("      SFD_DATA_RESPONSE\nA: 1")]
        [InlineData("      SFD_NO_DATA")]
        [InlineData(" \r     SFD_PING")]
        public void InvalidHeadersAreDropped(string message)
        {
            using Network network = Started();
            int pings = 0;
            network.Server.OnPing += (a, id) => Interlocked.Increment(ref pings);
            network.Inject(Encoding.UTF8.GetBytes(message), "q:" + new string('a', 24));
            Assert.True(SpinWait.SpinUntil(() => !network.ServerErrors.IsEmpty, Long));
            Thread.Sleep(200);
            Assert.Equal(0, pings);
            Assert.Empty(network.Responses);
            Assert.All(network.ServerErrors, e => Assert.IsType<InvalidDataException>(e));
        }

        [Theory]
        [InlineData("\n")]
        [InlineData("\r\n")]
        [InlineData("\r")]
        [InlineData("\n\r")]
        public void HeaderEndsAtAnyLineBreak(string lineBreak)
        {
            using Network network = Started();
            ConcurrentQueue<string> raw = new();
            network.Server.OnDataRaw += (a, data) => raw.Enqueue(data);
            int pings = 0;
            network.Server.OnPing += (a, id) => Interlocked.Increment(ref pings);
            network.Inject(Encoding.UTF8.GetBytes("      SFD_PING" + lineBreak), "q:" + new string('a', 24));
            network.Inject(Encoding.UTF8.GetBytes("      SFD_DATA_TRANSFER" + lineBreak + "A: 1" + lineBreak + "B:" + lineBreak + "--C: 2"), "q:" + new string('b', 24));
            Assert.True(SpinWait.SpinUntil(() => network.Responses.Count == 2, Long));
            Assert.Equal(1, pings);
            // the body is given to OnDataRaw as it came, only the line break after the header is taken away
            Assert.Equal("A: 1" + lineBreak + "B:" + lineBreak + "--C: 2", Assert.Single(raw));
            Assert.Contains(network.Responses, p => Encoding.UTF8.GetString(p.DataBytes) == "      SFD_DATA_RESPONSE\nA: 1\nB:\n--C: 2");
            Assert.Empty(network.ServerErrors);
        }

        [Fact]
        public void ResponseWithCarriageReturnsIsTaken()
        {
            using Network network = new(); // the server isn't started, the answer is made by hand
            Task<Inner?> pending = Task.Run(() => network.Client.SendData<Inner, Inner>(network.ServerAddress, new Inner(), 3000, null));
            Assert.True(SpinWait.SpinUntil(() => !network.Requests.IsEmpty, Long));
            string id = network.Requests.First().Metadata![2..];
            network.ServerModem.LowLevelTransmit(Encoding.UTF8.GetBytes("      SFD_DATA_RESPONSE\r\nName: kek\r\nNumber: 85\r\n"), network.ClientAddress, "sfd_object", InRange, $"r:{id}");
            Inner? inner = pending.GetAwaiter().GetResult();
            Assert.Equal("kek", inner!.Name);
            Assert.Equal(85, inner.Number);
        }

        [Fact]
        public void NoAnswerIsATimeout()
        {
            using Network network = new(); // the server isn't started
            Assert.Throws<TimeoutException>(() => network.Raw("A: 1", null, 500));
            Assert.Throws<TimeoutException>(() => network.Client.SendData<Inner, Inner>(network.ServerAddress, new Inner(), 500, null));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(31)]
        [InlineData(33)]
        public void KeyOfAWrongLengthIsRefused(int length)
        {
            using Network network = Started();
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => network.Raw("A: 1", new byte[length]));
            Assert.Equal("AES-256-GCM key has to be exactly 32 bytes long", ex.Message);
            Assert.Empty(network.Requests);
        }

        [Fact]
        public void EmptyKeyMeansNoEncryption()
        {
            using Network network = Started();
            Assert.Equal("A: 1", network.Raw("A: 1", []));
            Assert.StartsWith("q:", Assert.Single(network.Requests).Metadata);
        }

        [Fact]
        public void ClientChecksItsArguments()
        {
            using Network network = Started();
            Assert.Throws<ArgumentOutOfRangeException>(() => network.Raw("A: 1", null, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => network.Client.CheckSystem(network.ServerAddress, -1));
            Assert.Throws<ArgumentException>(() => network.Client.CheckSystem(new Address("7--1"), 100));
            Assert.Throws<ArgumentNullException>(() => network.Client.SendDataRaw(network.ServerAddress, null!, 100, null));
            Assert.Throws<ArgumentNullException>(() => network.Client.SendData<Inner, Inner>(network.ServerAddress, null!, 100, null));
            Assert.Throws<NotSupportedException>(() => network.Client.SendData<int[], Inner>(network.ServerAddress, [1], 100, null));
        }

        [Fact]
        public void LocalModemFaultIsAnException()
        {
            // a line, that nobody answers on: the modem is there, but not dialed
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            LowLatencyPhysicalModem notDialed = new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, L1Types.L1_INET_TCP, true) { LocalModemAddress = new Address("7-5") };
            listener.Stop();
            SFDPServer server = new(notDialed, 1, 2);
            Assert.Throws<ModemNotConnectedException>(server.Start);
            using SFDPClient client = new(notDialed);
            Assert.Throws<ModemNotConnectedException>(() => client.CheckSystem(new Address("7-1"), 100));
            Assert.Throws<ModemNotConnectedException>(() => client.SendDataRaw(new Address("7-1"), "A: 1", 100, null));
        }

        [Fact]
        public void LongDataIsFragmented()
        {
            using Network network = StartedWithKey(out byte[] key);
            string big = new('ж', 5000);
            SFDPObject data = new() { ["Big"] = big };
            Assert.Equal(data.ToString(), network.Raw(data.ToString(), key));
            Assert.True(network.Requests.Count > 1);
        }

        [Fact]
        public void RequestsCanRunAtTheSameTime()
        {
            using Network network = StartedWithKey(out byte[] key, s => s.OnData = (a, data) => { Thread.Sleep(300); return data; });
            string?[] results = new string?[8];
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => results[i] = network.Raw("N: " + i, key));
            Assert.Equal(Enumerable.Range(0, 8).Select(i => "N: " + i), results);
        }

        [Fact]
        public void StartAfterStopWorksAgain()
        {
            using Network network = Started();
            Assert.True(network.Client.CheckSystem(network.ServerAddress, Long));
            network.Server.Stop();
            network.Server.Stop(); // twice is fine
            Assert.False(network.Client.CheckSystem(network.ServerAddress, Short));
            network.Server.Start();
            Assert.True(network.Client.CheckSystem(network.ServerAddress, Long));
            Assert.Throws<InvalidOperationException>(network.Server.Start);
        }

        [Fact]
        public void EmptyRangeIsRefusedOnStart()
        {
            using Network network = new();
            SFDPServer server = new(network.ServerModem, 10, 9);
            Assert.Throws<InvalidOperationException>(server.Start);
        }

        // ---------- encryption ----------

        [Fact]
        public void EncryptedExchangeWorksAndHidesTheData()
        {
            using Network network = StartedWithKey(out byte[] key, s => s.OnData = (a, data) => new SFDPObject { ["Answer"] = "PASSWORD" });
            string secret = "Secret: topsecret";
            Assert.Equal("Answer: PASSWORD", network.Raw(secret, key));
            Packet request = Assert.Single(network.Requests);
            Assert.Matches("^qe:[0-9a-f]{24}:[0-9a-f]{16}:[0-9a-f]{32}$", request.Metadata);
            Assert.DoesNotContain("topsecret", Encoding.UTF8.GetString(request.DataBytes));
            Assert.DoesNotContain("SFD_", Encoding.UTF8.GetString(request.DataBytes));
            Assert.Equal(Encoding.UTF8.GetByteCount("      SFD_DATA_TRANSFER\n" + secret), request.DataBytes.Length);
            Assert.True(SpinWait.SpinUntil(() => !network.Responses.IsEmpty, Long));
            Packet response = Assert.Single(network.Responses);
            string requestNonce = request.Metadata!.Split(':')[1];
            Assert.Matches("^re:" + requestNonce + ":[0-9a-f]{24}:[0-9a-f]{32}$", response.Metadata);
            Assert.NotEqual(requestNonce, response.Metadata!.Split(':')[2]);
            Assert.DoesNotContain("PASSWORD", Encoding.UTF8.GetString(response.DataBytes));
            // typed calls are encrypted the same way
            Assert.Equal("PASSWORD", network.Client.SendData<Inner, Dictionary<string, string>>(network.ServerAddress, new Inner(), Long, key)!["Answer"]);
        }

        [Fact]
        public void PingIsAnsweredByAnEncryptingServer()
        {
            using Network network = StartedWithKey(out _);
            Assert.True(network.Client.CheckSystem(network.ServerAddress, Long));
            Assert.StartsWith("q:", Assert.Single(network.Requests).Metadata);
        }

        [Fact]
        public void DataWithoutEncryptionIsDroppedByAnEncryptingServer()
        {
            using Network network = StartedWithKey(out _);
            int handled = 0;
            network.Server.OnDataRaw += (a, data) => Interlocked.Increment(ref handled);
            Assert.Throws<TimeoutException>(() => network.Raw("A: 1", null, Short));
            Assert.Equal(0, handled);
            Assert.Empty(network.Responses);
            Assert.Contains(network.ServerErrors, e => e is UnauthorizedAccessException);
        }

        [Fact]
        public void EncryptedDataIsDroppedByAServerWithoutEncryption()
        {
            using Network network = Started();
            int handled = 0;
            network.Server.OnDataRaw += (a, data) => Interlocked.Increment(ref handled);
            Assert.Throws<TimeoutException>(() => network.Raw("A: 1", NewKey(), Short));
            Assert.Equal(0, handled);
        }

        [Fact]
        public void WrongKeyOrNoKeyIsNotHandled()
        {
            using Network network = StartedWithKey(out byte[] key);
            int handled = 0;
            network.Server.OnDataRaw += (a, data) => Interlocked.Increment(ref handled);
            Assert.Throws<TimeoutException>(() => network.Raw("A: 1", NewKey(), Short));
            network.Server.SFDPEncryptionKeys.Clear();
            network.Server.SFDPEncryptionKeys[new Address("7-1-99")] = key; // the key of another address
            Assert.Throws<TimeoutException>(() => network.Raw("A: 1", key, Short));
            Assert.Equal(0, handled);
            Assert.Contains(network.ServerErrors, e => e is UnauthorizedAccessException && e.Message.Contains("tag"));
            Assert.Contains(network.ServerErrors, e => e is UnauthorizedAccessException && e.Message.Contains("no key"));
        }

        [Theory]
        [InlineData("tag")]
        [InlineData("data")]
        [InlineData("time")]
        [InlineData("nonce")]
        [InlineData("repeat")]
        public void TamperedOrRepeatedRequestIsNotHandled(string what)
        {
            using Network network = StartedWithKey(out byte[] key);
            int handled = 0;
            network.Server.OnDataRaw += (a, d) => Interlocked.Increment(ref handled);
            Assert.Equal("A: 1", network.Raw("A: 1", key));
            Packet captured = Assert.Single(network.Requests);
            byte[] data = captured.DataBytes;
            string[] f = captured.Metadata!.Split(':'); // qe, nonce, time, tag
            static string Flip(string hex, int index) => hex[..index] + (hex[index] == '0' ? '1' : '0') + hex[(index + 1)..];
            switch (what)
            {
                case "tag": f[3] = Flip(f[3], 31); break;
                case "data": data = (byte[])data.Clone(); data[^1] ^= 1; break;
                case "time": f[2] = Flip(f[2], 15); break;
                case "nonce": f[1] = Flip(f[1], 0); break;
                case "repeat": f[1] = f[1].ToUpperInvariant(); break;
            }
            network.ServerErrors.Clear();
            network.Inject(data, string.Join(':', f));
            Assert.True(SpinWait.SpinUntil(() => !network.ServerErrors.IsEmpty, Long));
            Thread.Sleep(200);
            Assert.Equal(1, handled);
            Assert.Contains(network.ServerErrors, e => e is UnauthorizedAccessException && e.Message.Contains(what == "repeat" ? "copy" : "tag"));
        }

        [Theory]
        [InlineData(1000)]
        [InlineData(-1000)]
        public void RequestOutsideOfTheWindowIsNotHandled(int serverClockShift)
        {
            using Network network = StartedWithKey(out byte[] key, s => s.ReplayWindowSeconds = 60);
            network.Server.UnixTimeSeconds = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + serverClockShift;
            Assert.Throws<TimeoutException>(() => network.Raw("A: 1", key, Short));
            Assert.Contains(network.ServerErrors, e => e.Message.Contains("ReplayWindowSeconds"));
            network.Server.UnixTimeSeconds = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Sign(serverClockShift) * 50;
            Assert.Equal("A: 1", network.Raw("A: 1", key));
        }

        [Fact]
        public void ForgedResponsesAreNotAccepted()
        {
            using Network network = StartedWithKey(out byte[] key);
            Assert.Equal("A: 1", network.Raw("A: 1", key));
            Assert.True(SpinWait.SpinUntil(() => !network.Responses.IsEmpty, Long));
            Packet oldResponse = Assert.Single(network.Responses);
            while (network.Requests.TryDequeue(out _)) { }
            network.Server.Stop();
            Task<string?> pending = Task.Run(() => network.Raw("A: 2", key, 3000));
            Assert.True(SpinWait.SpinUntil(() => !network.Requests.IsEmpty, Long));
            string newId = network.Requests.First().Metadata!.Split(':')[1];
            string[] f = oldResponse.Metadata!.Split(':');
            // the old response with the ID of the new request: the AAD doesn't match
            network.ServerModem.LowLevelTransmit(oldResponse.DataBytes, network.ClientAddress, "sfd_object", InRange, $"re:{newId}:{f[2]}:{f[3]}");
            Assert.True(SpinWait.SpinUntil(() => !network.ClientErrors.IsEmpty, Long));
            Assert.Contains(network.ClientErrors, e => e is AuthenticationTagMismatchException);
            // a response without encryption to an encrypted request
            network.ServerModem.LowLevelTransmit(Encoding.UTF8.GetBytes("      SFD_NO_DATA"), network.ClientAddress, "sfd_object", InRange, $"r:{newId}");
            Assert.True(SpinWait.SpinUntil(() => network.ClientErrors.Count >= 2, Long));
            Assert.Contains(network.ClientErrors, e => e is InvalidDataException && e.Message.Contains("isn't encrypted"));
            Assert.Throws<AggregateException>(() => pending.Wait());
            Assert.IsType<TimeoutException>(pending.Exception!.InnerException);
        }

        [Fact]
        public void PongIsNotAnAnswerToData()
        {
            using Network network = new(); // the server isn't started, the answers are forged
            Task<string?> pending = Task.Run(() => network.Raw("A: 1", null, 3000));
            Assert.True(SpinWait.SpinUntil(() => !network.Requests.IsEmpty, Long));
            string id = network.Requests.First().Metadata![2..];
            network.ServerModem.LowLevelTransmit(Encoding.UTF8.GetBytes("      SFD_PONG"), network.ClientAddress, "sfd_object", InRange, $"r:{id}");
            Assert.True(SpinWait.SpinUntil(() => !network.ClientErrors.IsEmpty, Long));
            Assert.IsType<InvalidDataException>(Assert.Single(network.ClientErrors));
            // another ConnectionID is ignored
            network.ServerModem.LowLevelTransmit(Encoding.UTF8.GetBytes("      SFD_DATA_RESPONSE\nX: 1"), network.ClientAddress, "sfd_object", InRange + 1, $"r:{id}");
            Thread.Sleep(200);
            network.ServerModem.LowLevelTransmit(Encoding.UTF8.GetBytes("      SFD_DATA_RESPONSE\nX: 2"), network.ClientAddress, "sfd_object", InRange, $"r:{id}");
            Assert.Equal("X: 2", pending.Result);
        }
    }
}
