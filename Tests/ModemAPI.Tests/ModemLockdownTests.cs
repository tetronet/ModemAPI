using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// MODEM LOCKDOWN: one RAWWS operation without a limit (a send to a server, that doesn't read, or a connection to a server,
    /// that never answers the upgrade) must not stop the VirtualModem forever.
    /// </summary>
    public class ModemLockdownTests
    {
        /// <summary>
        /// A RAWWS server: the first connection is served by <see cref="FirstConnection"/>, the next ones read everything.
        /// </summary>
        private sealed class Server : IDisposable
        {
            private readonly TcpListener Listener = new(IPAddress.Loopback, 0);
            public string Uri => $"ws://127.0.0.1:{((IPEndPoint)Listener.LocalEndpoint).Port}/";
            public int Accepted;
            public int Upgraded;
            public Func<TcpClient, Task>? FirstConnection;
            private readonly ConcurrentBag<TcpClient> Clients = [];

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
                        Clients.Add(client);
                        bool first = Interlocked.Increment(ref Accepted) == 1;
                        _ = Task.Run(() => first && FirstConnection != null ? FirstConnection(client) : ServeReading(client));
                    }
                });
            }

            public async Task<WebSocket> Upgrade(TcpClient client)
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
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
                Interlocked.Increment(ref Upgraded);
                return WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromMinutes(1));
            }

            private async Task ServeReading(TcpClient client)
            {
                try
                {
                    WebSocket socket = await Upgrade(client);
                    byte[] buffer = new byte[1 << 20];
                    while (true)
                    {
                        ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), default);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                    }
                }
                catch
                {
                    // the connection is gone
                }
            }

            public void Dispose()
            {
                Listener.Stop();
                foreach (TcpClient client in Clients)
                {
                    client.Dispose();
                }
            }
        }

        [Fact]
        public void ServerThatDoesntReadDoesntHoldTransmitForever()
        {
            using Server server = new();
            // the first connection is upgraded and then never read: the sends fill the TCP buffers and stop
            server.FirstConnection = async client =>
            {
                await server.Upgrade(client);
                await Task.Delay(Timeout.Infinite);
            };
            VirtualModem modem = new(server.Uri, new LineParams(new Address("7-1-1"), false, null), rawWs: true)
            {
                RawSendTimeout = TimeSpan.FromMilliseconds(500)
            };
            modem.Dial();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref server.Upgraded) == 1, TimeSpan.FromSeconds(5)), "the modem didn't connect");

            byte[] data = new byte[60000];
            long longest = 0;
            Task sender = Task.Run(() =>
            {
                // until the modem gives the stuck connection up and is on the next one
                while (Volatile.Read(ref server.Upgraded) < 2)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    modem.LowLevelTransmit(data, new Address("7-2"), "q", 1, null);
                    longest = Math.Max(longest, watch.ElapsedMilliseconds);
                }
            });
            Assert.True(sender.Wait(TimeSpan.FromSeconds(30)), "Transmit hangs on the server, that doesn't read");
            Assert.True(longest < 5000, $"one Transmit took {longest} ms");
        }

        [Fact]
        public void ServerThatNeverAnswersTheUpgradeIsGivenUp()
        {
            using Server server = new();
            // the first connection is accepted, but the upgrade is never answered
            server.FirstConnection = _ => Task.Delay(Timeout.Infinite);
            VirtualModem modem = new(server.Uri, new LineParams(new Address("7-1-1"), false, null), rawWs: true)
            {
                RawConnectTimeout = TimeSpan.FromMilliseconds(500)
            };
            int reconnected = 0;
            modem.OnSuccessReconnectWebsocket = () => Interlocked.Increment(ref reconnected);
            modem.Dial();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reconnected) == 1, TimeSpan.FromSeconds(10)), "the modem waits for the upgrade forever");
            Assert.Equal(2, Volatile.Read(ref server.Accepted));
            Assert.Equal(1, Volatile.Read(ref server.Upgraded));
        }

        [Fact]
        public void ReceiveLoopIsntStoppedByAStuckSend()
        {
            using Server server = new();
            WebSocket? first = null;
            using SemaphoreSlim upgraded = new(0);
            // the first connection sends to the modem, but never reads what the modem sends
            server.FirstConnection = async client =>
            {
                first = await server.Upgrade(client);
                upgraded.Release();
                await Task.Delay(Timeout.Infinite);
            };
            VirtualModem modem = new(server.Uri, new LineParams(new Address("7-1-1"), false, null), rawWs: true)
            {
                RawSendTimeout = TimeSpan.FromMilliseconds(500)
            };
            int received = 0;
            byte[] data = new byte[60000];
            // every packet is answered from the receive loop, as SRTP answers with acknowledgements
            modem.AttachReceiveEventNoUnfragment((p, k) =>
            {
                Interlocked.Increment(ref received);
                modem.LowLevelTransmit(data, new Address("7-2"), "q", 1, null);
            });
            modem.Dial();
            Assert.True(upgraded.Wait(TimeSpan.FromSeconds(5)), "the modem didn't connect");
            Task feeder = Task.Run(async () =>
            {
                RawWsCodec codec = new();
                for (int i = 0; i < 400; i++)
                {
                    Packet p = new(new Address("7-2"), new Address("7-1-1"), [1, 2, 3], true, null, "q", 1, false, 0, (ulong)i, 100);
                    byte[] message = codec.WriteDataTransmission(p).ToArray();
                    try
                    {
                        await first!.SendAsync(message, WebSocketMessageType.Text, true, default);
                    }
                    catch
                    {
                        return; // the modem gave the connection up
                    }
                }
            });
            // the answers fill the buffers, a send stops, and the modem replaces the connection instead of hanging
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref server.Upgraded) == 2, TimeSpan.FromSeconds(30)), "the modem hangs, the receive loop is stopped behind the send");
            feeder.Wait(TimeSpan.FromSeconds(10));
            Assert.True(received > 0);
        }
    }
}
