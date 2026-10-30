using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// Regression tests for the ninth review (X2, X3 in CODE_REVIEW.md; X1 is in <see cref="RawWsTests"/>).
    /// </summary>
    public class ReviewRound9Tests
    {
        // ---------- X2: one reconnection at a time, a lost connection doesn't throw from Transmit ----------

        /// <summary>
        /// A RAWWS server taking any number of connections.
        /// </summary>
        private sealed class Server : IDisposable
        {
            private readonly TcpListener Listener = new(IPAddress.Loopback, 0);
            public string Uri => $"ws://127.0.0.1:{((IPEndPoint)Listener.LocalEndpoint).Port}/";
            public readonly ConcurrentQueue<TcpClient> Connections = new();
            public readonly ConcurrentDictionary<TcpClient, int> MessagesPerConnection = new();

            public Server()
            {
                Listener.Start();
                Task.Run(async () =>
                {
                    while (true)
                    {
                        TcpClient client;
                        try
                        {
                            client = await Listener.AcceptTcpClientAsync();
                        }
                        catch
                        {
                            return;
                        }
                        _ = Task.Run(() => Serve(client));
                    }
                });
            }

            private async Task Serve(TcpClient client)
            {
                try
                {
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
                    if (!Connections.IsEmpty)
                    {
                        // a slow reconnection, so the receive loop and the senders notice the loss while it is in progress
                        await Task.Delay(500);
                    }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
                    WebSocket socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromMinutes(1));
                    MessagesPerConnection[client] = 0;
                    Connections.Enqueue(client);
                    byte[] buffer = new byte[1 << 20];
                    while (true)
                    {
                        ValueWebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(buffer.AsMemory(), default);
                        } while (!result.EndOfMessage);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                        MessagesPerConnection.AddOrUpdate(client, 1, (_, n) => n + 1);
                    }
                }
                catch
                {
                    // the connection is gone
                }
            }

            public void Dispose() => Listener.Stop();
        }

        [Fact]
        public void LostConnectionIsReplacedOnceAndTransmitDoesntThrow()
        {
            using Server server = new();
            VirtualModem modem = new(server.Uri, new LineParams(new Address("7-1-1"), false, null), rawWs: true);
            modem.Dial();
            Assert.True(SpinWait.SpinUntil(() => server.Connections.Count == 1, TimeSpan.FromSeconds(5)), "the modem didn't connect");
            server.Connections.TryPeek(out TcpClient? first);

            ConcurrentQueue<Exception> errors = new();
            using CancellationTokenSource stop = new();
            Task[] senders = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        modem.LowLevelTransmit([1, 2, 3], new Address("7-2"), "q", (uint)i, null);
                    }
                    catch (Exception e)
                    {
                        errors.Enqueue(e);
                        return;
                    }
                    Thread.Sleep(1);
                }
            })).ToArray();
            Thread.Sleep(200);

            // the server side breaks the connection while the senders and the receive loop use it
            first!.Client.LingerState = new LingerOption(true, 0);
            first.Close();

            Assert.True(SpinWait.SpinUntil(() => server.Connections.Count >= 2, TimeSpan.FromSeconds(5)), "the modem didn't reconnect");
            Thread.Sleep(1500); // a second reconnection would come by now
            stop.Cancel();
            Task.WaitAll(senders, TimeSpan.FromSeconds(10));

            Assert.Empty(errors);
            Assert.Equal(2, server.Connections.Count);
            TcpClient second = server.Connections.Last();
            Assert.True(server.MessagesPerConnection[second] > 0, "nothing was sent over the new connection");
        }

        // ---------- X3: the retransmission timeout follows the round trip and backs off ----------

        [Fact]
        public void RetransmissionTimeoutGrowsToTheRoundTrip()
        {
            using FakeLink link = new();
            // acknowledgements take 300 ms, the timeout is 50 ms: without measuring the round trip every packet is resent several times
            link.BToA = p =>
            {
                if (!FakeLink.IsAck(p))
                {
                    return Fault.Deliver;
                }
                Task.Delay(300).ContinueWith(_ => link.A.Enqueue(p));
                return Fault.Drop;
            };
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "q", 9, 500_000);
            SRTPClient receiver = new(link.B, link.A.LocalModemAddress!, "q", 9, 500_000);
            int received = 0;
            receiver.OnMessageReceived = (c, d) => Interlocked.Increment(ref received);
            int retransmissions = 0;
            sender.OnRetransmit = id => Interlocked.Increment(ref retransmissions);
            try
            {
                for (int i = 0; i < 60; i++)
                {
                    sender.Transmit([(byte)i]);
                    Thread.Sleep(50);
                }
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref received) == 60, TimeSpan.FromSeconds(10)));
                sender.WaitForAcknowledgements();
                // only the first packets, sent before the round trip was known, may be resent
                Assert.True(retransmissions <= 20, $"{retransmissions} retransmissions of 60 packets");
                long timeout = (long)typeof(SRTPClient).GetField("RetransmissionTimeoutTicks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sender)!;
                Assert.True(timeout >= 3_000_000, $"the retransmission timeout is {timeout / 10_000} ms with the round trip of 300 ms");
            }
            finally
            {
                sender.Close();
                receiver.Close();
            }
        }

        [Fact]
        public void FastLinkKeepsTheGivenTimeout()
        {
            using FakeLink link = new();
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "q", 10, 20_000_000);
            SRTPClient receiver = new(link.B, link.A.LocalModemAddress!, "q", 10, 20_000_000);
            int received = 0;
            receiver.OnMessageReceived = (c, d) => Interlocked.Increment(ref received);
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    sender.Transmit([(byte)i]);
                }
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref received) == 50, TimeSpan.FromSeconds(10)));
                sender.WaitForAcknowledgements();
                long timeout = (long)typeof(SRTPClient).GetField("RetransmissionTimeoutTicks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sender)!;
                Assert.Equal(20_000_000, timeout);
            }
            finally
            {
                sender.Close();
                receiver.Close();
            }
        }

        [Fact]
        public void RetransmissionsOfAPacketBackOffUpToEightTimes()
        {
            using FakeLink link = new();
            link.AToB = p => FakeLink.IsData(p) ? Fault.Drop : Fault.Deliver; // nothing gets through
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "q", 11, 1_000_000) { RetransmissionsBeforeConsideringRemoteSideDead = 6 };
            ConcurrentQueue<long> times = new();
            Stopwatch clock = Stopwatch.StartNew();
            sender.OnRetransmit = id => times.Enqueue(clock.ElapsedMilliseconds);
            bool closed = false;
            sender.OnClose += c => closed = true;
            try
            {
                sender.Transmit([1]);
                // waits of 100, 200, 400, 800, 800, 800 ms and the last one before it is dead
                Assert.True(SpinWait.SpinUntil(() => closed, TimeSpan.FromSeconds(15)), "the remote side wasn't considered dead");
                long[] t = [0, .. times];
                Assert.Equal(7, t.Length);
                long[] expected = [100, 200, 400, 800, 800, 800];
                for (int i = 0; i < expected.Length; i++)
                {
                    long gap = t[i + 1] - t[i];
                    Assert.True(gap >= expected[i] * 0.9 && gap <= expected[i] + 400, $"retransmission {i + 1} came {gap} ms after the previous one, {expected[i]} expected");
                }
            }
            finally
            {
                sender.Close();
            }
        }
    }
}
