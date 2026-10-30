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
    /// RAWWS of <see cref="VirtualModem"/>: the fast writing and reading of "data-transmission" give exactly what the old
    /// JsonSerializer code gave, and the modem works with a WebSocket server, that splits messages and gets many sends at once.
    /// </summary>
    public class RawWsTests
    {
        // ---------- the old code, as it was ----------

        private static byte[] OldWrite(Packet packet) =>
            JsonSerializer.SerializeToUtf8Bytes(new object[] { "data-transmission", PacketConverter.PacketToWebSocketMessage(packet) });

        private static Packet? OldRead(byte[] message)
        {
            string textMessage = Encoding.ASCII.GetString(message);
            List<JsonElement>? temp = JsonSerializer.Deserialize<List<JsonElement>>(textMessage);
            WebSocketMessage? received = temp![1].Deserialize<WebSocketMessage>();
            return received == null ? null : PacketConverter.WebSocketMessageToPacket(received);
        }

        private static void AssertSamePacket(Packet expected, Packet actual)
        {
            Assert.Equal(expected.Transmitter.AddressValue, actual.Transmitter.AddressValue);
            Assert.Equal(expected.Receiver.AddressValue, actual.Receiver.AddressValue);
            Assert.Equal(expected.DataBytes, actual.DataBytes);
            Assert.Equal(expected.IsLastInSequence, actual.IsLastInSequence);
            Assert.Equal(expected.Metadata, actual.Metadata);
            Assert.Equal(expected.QueryType, actual.QueryType);
            Assert.Equal(expected.ConnectionID, actual.ConnectionID);
            Assert.Equal(expected.IsErrorWhileReading, actual.IsErrorWhileReading);
            Assert.Equal(expected.PacketNo, actual.PacketNo);
            Assert.Equal(expected.MessageId, actual.MessageId);
            Assert.Equal(expected.RoutersToPass, actual.RoutersToPass);
        }

        private static readonly string[] Texts = ["", "q", "srtp2:0000abcd", "<a&b>'\"+/\\", "привет", "tab\there", "\u0001\u001f", "emoji 😀", "a-b--c-"];

        private static Packet RandomPacket(Random random)
        {
            string Address() => random.Next(4) switch
            {
                0 => "7-1-1",
                1 => string.Join('-', Enumerable.Range(0, random.Next(1, 6)).Select(_ => random.Next(0, 100000).ToString())),
                2 => Texts[random.Next(Texts.Length)],
                _ => "0",
            };
            byte[] data = new byte[random.Next(4) == 0 ? random.Next(0, 70000) : random.Next(0, 300)];
            random.NextBytes(data);
            return new Packet(new Address(Address()), new Address(Address()), data, random.Next(2) == 0,
                random.Next(3) == 0 ? null : Texts[random.Next(Texts.Length)], Texts[random.Next(Texts.Length)],
                (uint)random.NextInt64(0, uint.MaxValue + 1L), false, (ulong)random.NextInt64(), (ulong)random.NextInt64() * 2, (ushort)random.Next(0, 65536));
        }

        [Fact]
        public void WritingGivesTheBytesOfTheOldCode()
        {
            Random random = new(1);
            RawWsCodec codec = new();
            for (int i = 0; i < 3000; i++)
            {
                Packet packet = RandomPacket(random);
                Assert.Equal(OldWrite(packet), codec.WriteDataTransmission(packet).ToArray());
            }
        }

        [Fact]
        public void WritingThrowsLikeTheOldCode()
        {
            RawWsCodec codec = new();
            Packet noReceiver = new() { Transmitter = new("7-1-1"), QueryType = "q", DataBytes = [1] };
            Assert.Throws<NullAddressException>(() => codec.WriteDataTransmission(noReceiver));
            Packet noTransmitter = new() { Receiver = new("7-1-1"), QueryType = "q", DataBytes = [1] };
            Assert.Throws<NullAddressException>(() => codec.WriteDataTransmission(noTransmitter));
            // and works on after that
            Packet good = new() { Transmitter = new("7-1-1"), Receiver = new("7-1-2"), QueryType = "q", DataBytes = [1] };
            Assert.Equal(OldWrite(good), codec.WriteDataTransmission(good).ToArray());
        }

        [Fact]
        public void ReadingGivesThePacketOfTheOldCode()
        {
            Random random = new(2);
            RawWsCodec codec = new();
            int fast = 0;
            for (int i = 0; i < 3000; i++)
            {
                byte[] message = OldWrite(RandomPacket(random));
                if (codec.TryReadDataTransmission(message, out Packet? packet))
                {
                    fast++;
                    AssertSamePacket(OldRead(message)!, packet!);
                }
            }
            // everything the old code writes without escapes is read the fast way
            Assert.True(fast > 1000, $"only {fast} of 3000 were read the fast way");
        }

        private static readonly string[] MessageTexts =
        [
            // the usual one, numbers in the addresses, whitespace, another order, unknown fields, the rest of the array
            """["data-transmission",{"package_data":"AQID","metadata":null,"package_info":{"connectionid":7,"from":["7","1","1"],"to":["7","1","2"],"package_type":"q","is_last_in_package_queue":true,"package_no":0,"message_id":5,"max_hops":3}}]""",
            """[ "data-transmission" , { "package_info" : { "max_hops":1, "to":[7,1,2], "from":[7,"1",1], "message_id":9, "package_no":2, "is_last_in_package_queue":false, "package_type":"q", "connectionid":4, "extra":{"a":[1,2]} }, "metadata":"m", "package_data":"", "x":1 }, 1, {"y":[]} ] """,
            // no max_hops: 65535
            """["data-transmission",{"package_data":"AQ==","metadata":"m","package_info":{"connectionid":1,"from":["7"],"to":[],"package_type":"q","is_last_in_package_queue":false,"package_no":1,"message_id":1}}]""",
            // repeated fields: the last one wins, and a repeated package_info replaces the whole earlier one
            """["data-transmission",{"package_data":"AQ==","package_data":"AgI=","metadata":"a","metadata":"b","package_info":{"connectionid":1,"from":["7"],"to":["8"],"package_type":"q","is_last_in_package_queue":false,"package_no":1,"message_id":1,"max_hops":7},"package_info":{"connectionid":2,"from":["9"],"to":["8"],"package_type":"r","is_last_in_package_queue":true,"package_no":3,"message_id":4}}]""",
            // escapes, non-ASCII, base64 with whitespace, nulls, wrong types, out of range, trailing data: whatever happens, the same as before
            """["data-transmission",{"package_data":"AQ==","metadata":"т","package_info":{"connectionid":1,"from":["7"],"to":["8"],"package_type":"q","is_last_in_package_queue":false,"package_no":1,"message_id":1}}]""",
            """["data-transmission",{"package_data":"AQID","metadata":"тест","package_info":{"connectionid":1,"from":["7"],"to":["8"],"package_type":"q","is_last_in_package_queue":false,"package_no":1,"message_id":1}}]""",
            """["data-transmission",{"package_data":"AQ ID","metadata":null,"package_info":{"connectionid":1,"from":["7"],"to":["8"],"package_type":"q","is_last_in_package_queue":false,"package_no":1,"message_id":1}}]""",
            """["data-transmission",{"package_data":"AQID","metadata":null,"package_info":{"connectionid":1,"from":["7"],"to":["8"],"package_type":null,"is_last_in_package_queue":false,"package_no":1,"message_id":1}}]""",
            """["data-transmission",{"package_data":"AQID","metadata":null,"package_info":{"connectionid":-1,"from":["7"],"to":["8"],"package_type":"q","is_last_in_package_queue":false,"package_no":1,"message_id":1}}]""",
            """["data-transmission",{"package_data":"AQID","metadata":null,"package_info":{"connectionid":1.5,"from":["7"],"to":["8"],"package_type":"q","is_last_in_package_queue":"true","package_no":1,"message_id":1}}]""",
            """["data-transmission",{"package_data":"AQID","metadata":null,"package_info":{"connectionid":1,"from":["7",null],"to":["8",true],"package_type":"q","is_last_in_package_queue":false,"package_no":1,"message_id":1}}]""",
            """["data-transmission",{"package_data":"AQID","metadata":null,"package_info":{"connectionid":1,"from":["7"],"to":["8"],"package_type":"q","is_last_in_package_queue":false,"package_no":1,"message_id":1,"max_hops":70000}}]""",
            """["data-transmission",{"package_data":"AQID","metadata":null,"package_info":null}]""",
            """["data-transmission",null]""",
            """["data-transmission"]""",
            """["data-transmission",{"package_data":"AQID"}] x""",
            """["data-transmission",{"package_data":"AQID",}]""",
        ];

        public static TheoryData<string> Messages => new(MessageTexts);

        [Theory]
        [MemberData(nameof(Messages))]
        public void ReadingAnyMessageGivesWhatTheOldCodeGave(string text)
        {
            byte[] message = Encoding.UTF8.GetBytes(text);
            Assert.True(RawWsCodec.IsDataTransmission(message));
            Packet? old;
            try
            {
                old = OldRead(message);
            }
            catch (Exception)
            {
                old = null; // the old code failed on it: the fast way must leave it to the old one
            }
            RawWsCodec codec = new();
            if (codec.TryReadDataTransmission(message, out Packet? packet))
            {
                Assert.NotNull(old);
                Assert.False(old!.IsErrorWhileReading, "the fast way read a message, that the old one considered broken");
                AssertSamePacket(old, packet!);
            }
        }

        [Fact]
        public void UsualMessagesAreReadTheFastWay()
        {
            RawWsCodec codec = new();
            foreach (int i in new[] { 0, 1, 2, 3 })
            {
                Assert.True(codec.TryReadDataTransmission(Encoding.UTF8.GetBytes(MessageTexts[i]), out _), $"message {i}");
            }
        }

        [Theory]
        [InlineData("""["get-addr"]""", false)]
        [InlineData("""["data-transmissionX",{}]""", false)]
        [InlineData("""[ "data-transmission" ,{}]""", true)]
        [InlineData("""no quotes""", false)]
        public void EventNameIsTheTextBetweenTheFirstTwoQuotes(string text, bool expected)
        {
            Assert.Equal(expected, RawWsCodec.IsDataTransmission(Encoding.UTF8.GetBytes(text)));
        }

        // ---------- the modem with a WebSocket server ----------

        /// <summary>
        /// A WebSocket server on a bare socket: the handshake by hand, then <see cref="WebSocket.CreateFromStream(Stream, bool, string?, TimeSpan)"/>.
        /// </summary>
        private sealed class Server : IDisposable
        {
            private readonly TcpListener Listener = new(IPAddress.Loopback, 0);
            public string Uri => $"ws://127.0.0.1:{((IPEndPoint)Listener.LocalEndpoint).Port}/";
            public readonly ConcurrentQueue<byte[]> Received = new();
            public readonly TaskCompletionSource<WebSocket> Connected = new();

            public Server()
            {
                Listener.Start();
                Task.Run(async () =>
                {
                    TcpClient client = await Listener.AcceptTcpClientAsync();
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
                        Received.Enqueue(buffer[..count]);
                    }
                });
            }

            public void Dispose() => Listener.Stop();
        }

        private static (VirtualModem modem, Server server, WebSocket socket) Connect()
        {
            Server server = new();
            VirtualModem modem = new(server.Uri, new LineParams(new Address("7-1-1"), false, null), rawWs: true);
            modem.Dial();
            Assert.True(server.Connected.Task.Wait(TimeSpan.FromSeconds(5)), "the modem didn't connect");
            return (modem, server, server.Connected.Task.Result);
        }

        [Fact]
        public void MessagesInPartsAreReceivedWhole()
        {
            var (modem, server, socket) = Connect();
            using (server)
            {
                ConcurrentQueue<Packet> received = new();
                modem.AttachReceiveEventNoUnfragment((p, k) => received.Enqueue(p));
                Random random = new(3);
                List<Packet> sent = [];
                for (int i = 0; i < 20; i++)
                {
                    Packet packet = new(new Address("7-2-" + i), new Address("7-1-1"), RandomBytes(random, random.Next(1, 40000)), true, i % 2 == 0 ? null : "md", "q", (uint)i, false, (ulong)i, (ulong)i, 9);
                    sent.Add(packet);
                    byte[] message = OldWrite(packet);
                    // in several frames of a message, as a server or a proxy may send it
                    for (int offset = 0; offset < message.Length;)
                    {
                        int part = Math.Min(message.Length - offset, random.Next(1, 9000));
                        socket.SendAsync(message.AsMemory(offset, part), WebSocketMessageType.Text, offset + part == message.Length, default).AsTask().Wait();
                        offset += part;
                    }
                }
                Assert.True(SpinWait.SpinUntil(() => received.Count == sent.Count, TimeSpan.FromSeconds(10)), $"{received.Count} of {sent.Count} received");
                foreach ((Packet expected, Packet actual) in sent.Zip(received))
                {
                    AssertSamePacket(expected, actual);
                }
            }
        }

        private static byte[] RandomBytes(Random random, int length)
        {
            byte[] bytes = new byte[length];
            random.NextBytes(bytes);
            return bytes;
        }

        [Fact]
        public void PacketsForOtherModemsOrFromThisOneAreNotDelivered()
        {
            var (modem, server, socket) = Connect();
            using (server)
            {
                // what passes the address check of RAWWS (subscribers get only their exact address, the rest is routed to slaves)
                ConcurrentQueue<Packet> received = new();
                modem.InternalAttachReceiveEvent((p, k) => received.Enqueue(p));
                // X1: 7-1-12 and 7-1-10 share the beginning of the text with 7-1-1, but they are other modems
                (string from, string to)[] packets = [("7-2", "7-3"), ("7-1-1-5", "7-1-1"), ("7-2", "7-1-1-4"), ("7-2", "7-1-12"), ("7-1-12", "7-1-1"), ("7-1-10", "7-1-1-3"), ("7-1-1", "7-1-1"), ("7-2", "7-1-1")];
                foreach ((string from, string to) in packets)
                {
                    byte[] message = OldWrite(new Packet(new Address(from), new Address(to), [1], true, null, "q", 1, false, 0, 1, 9));
                    socket.SendAsync(message, WebSocketMessageType.Text, true, default).Wait();
                }
                Assert.True(SpinWait.SpinUntil(() => received.Count == 4, TimeSpan.FromSeconds(5)), $"{received.Count} delivered");
                Thread.Sleep(200);
                Assert.Equal(["7-2>7-1-1-4", "7-1-12>7-1-1", "7-1-10>7-1-1-3", "7-2>7-1-1"], received.Select(p => $"{p.Transmitter.AddressValue}>{p.Receiver.AddressValue}"));
            }
        }

        [Fact]
        public void ManySendersAtOnceSendWholeMessages()
        {
            var (modem, server, socket) = Connect();
            using (server)
            {
                ConcurrentDictionary<string, byte[]> expected = new();
                Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                {
                    Packet packet = new(new Address("7-1-1"), new Address("7-2-" + i), RandomBytes(new Random(i), 1000 + i * 37), true, "m" + i, "q", (uint)i, false, 0, (ulong)i, 9);
                    expected[i.ToString()] = OldWrite(packet);
                    modem.Transmit(packet);
                });
                Assert.True(SpinWait.SpinUntil(() => server.Received.Count == 400, TimeSpan.FromSeconds(10)), $"{server.Received.Count} of 400 arrived");
                HashSet<string> wanted = expected.Values.Select(Convert.ToBase64String).ToHashSet();
                Assert.All(server.Received, message => Assert.Contains(Convert.ToBase64String(message), wanted));
            }
        }
    }
}
