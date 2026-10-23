using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// Modem-KeepAlive of LL-CIoCIL-mini (over UDP, with a connection timeout of 300 ms: a Modem-KeepAlive every 100 ms).
    /// </summary>
    public class KeepAliveTests
    {
        private const ulong TimeoutNs = 300_000_000;
        private static readonly byte[] KeepAlive = [0x77, 0x99, 0x77, 0x99];
        private static readonly byte[] KeepAliveAnswer = [0x22, 0xFF, 0x22, 0xFF];

        private static int FreeUdpPort()
        {
            using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        private static (LowLatencyAnsweringPhysicalModem modem, IPEndPoint endPoint) Answering(ulong timeoutNs = TimeoutNs, int maxMissed = 3)
        {
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", true)
            {
                KeepAliveTimeoutNanoseconds = timeoutNs,
                MaxMissedKeepAlives = maxMissed,
            };
            answering.AttachIncomingEvent(answering.Answer);
            return (answering, new IPEndPoint(IPAddress.Loopback, port));
        }

        private static byte[] ConnectionRequest(string address)
        {
            List<byte> request = [.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixRequest];
            byte[] addressBytes = Encoding.UTF8.GetBytes(address);
            request.Add((byte)addressBytes.Length);
            request.AddRange(addressBytes);
            request.Add(255);
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(request.ToArray()));
            request.AddRange(crc);
            return request.ToArray();
        }

        /// <summary>
        /// A subscriber made of a bare socket, that connects and then answers Modem-KeepAlives the way the test wants.
        /// </summary>
        private static UdpClient ConnectBareSubscriber(IPEndPoint answering)
        {
            UdpClient subscriber = new(new IPEndPoint(IPAddress.Loopback, 0));
            subscriber.Client.ReceiveTimeout = 5000;
            subscriber.Send(ConnectionRequest("7-1-1"), answering);
            IPEndPoint? from = null;
            byte[] response = subscriber.Receive(ref from);
            Assert.True(response.AsSpan().StartsWith(CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse));
            return subscriber;
        }

        [Fact]
        public void AnsweredKeepAlivesKeepTheConnectionOnBothSides()
        {
            var (answering, endPoint) = Answering();
            LowLatencyPhysicalModem calling = new($"127.0.0.1:{endPoint.Port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1-1") };
            calling.Dial();
            Assert.True(SpinWait.SpinUntil(() => calling.IsModemConnected, TimeSpan.FromSeconds(5)));
            // no data at all for five timeouts: only Modem-KeepAlives go back and forth
            Thread.Sleep(1500);
            Assert.True(answering.HasActiveConnection);
            Assert.True(calling.IsModemConnected);
        }

        [Fact]
        public void OnlyLossesInARowEndTheConnection()
        {
            var (answering, endPoint) = Answering();
            using UdpClient subscriber = ConnectBareSubscriber(endPoint);
            // the example from the spec (alive), one answered, then three lost in a row (dead)
            bool[] answers = [true, false, true, true, false, true, true, false, true, true, true, true, false, true, false, false, false, false, false];
            int received = 0;
            bool aliveAfterTheExample = false, aliveAfterTwoLosses = false;
            IPEndPoint? from = null;
            subscriber.Client.ReceiveTimeout = 1000;
            try
            {
                while (true)
                {
                    byte[] datagram = subscriber.Receive(ref from);
                    if (!datagram.AsSpan().SequenceEqual(KeepAlive)) continue;
                    received++;
                    if (received == 15) aliveAfterTheExample = answering.HasActiveConnection; // keep-alives 1..14 are judged
                    if (received == 17) aliveAfterTwoLosses = answering.HasActiveConnection;  // 15 and 16 are lost
                    if (answers[received - 1]) subscriber.Send(KeepAliveAnswer, endPoint);
                }
            }
            catch (SocketException)
            {
                // no Modem-KeepAlive for a second: the answering modem stopped sending them
            }
            Assert.True(aliveAfterTheExample, "the connection died during the example, that must keep it alive");
            Assert.True(aliveAfterTwoLosses, "two losses in a row ended the connection");
            Assert.False(answering.HasActiveConnection);
            Assert.Equal(17, received); // 15, 16 and 17 lost in a row: no Modem-KeepAlive after that
        }

        [Fact]
        public void AnyTrafficCountsAsAnAnswer()
        {
            var (answering, endPoint) = Answering();
            using UdpClient subscriber = ConnectBareSubscriber(endPoint);
            using CancellationTokenSource stop = new();
            // never answers 0x22FF22FF, but sends some other data after every Modem-KeepAlive
            Task.Run(() =>
            {
                IPEndPoint? from = null;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        if (subscriber.Receive(ref from).AsSpan().SequenceEqual(KeepAlive)) subscriber.Send([1, 2, 3, 4, 5], endPoint);
                    }
                    catch (SocketException) { }
                }
            });
            Thread.Sleep(1500);
            Assert.True(answering.HasActiveConnection);
            stop.Cancel();
        }

        [Fact]
        public void MissedKeepAlivesLimitIsConfigurable()
        {
            var (answering, endPoint) = Answering(maxMissed: 5);
            using UdpClient subscriber = ConnectBareSubscriber(endPoint);
            // nothing is answered: with a limit of 5 the connection lives through 4 losses (~500 ms) and dies at the 5th
            Thread.Sleep(450);
            Assert.True(answering.HasActiveConnection, "the connection died before 5 losses in a row");
            Assert.True(SpinWait.SpinUntil(() => !answering.HasActiveConnection, TimeSpan.FromSeconds(3)));
        }

        [Fact]
        public void AnsweringModemTakesANewConnectionAfterTheOldOneDied()
        {
            var (answering, endPoint) = Answering();
            using UdpClient lost = ConnectBareSubscriber(endPoint); // never answers, so the connection dies
            Assert.True(SpinWait.SpinUntil(() => !answering.HasActiveConnection, TimeSpan.FromSeconds(3)));
            // a subscriber from another host can now connect, because the line is free
            using UdpClient other = new(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
            other.Client.ReceiveTimeout = 5000;
            other.Send(ConnectionRequest("7-1-2"), endPoint);
            IPEndPoint? from = null;
            Assert.True(other.Receive(ref from).AsSpan().StartsWith(CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse));
            Assert.Equal("7-1-2", answering.GetSubscriberAddress().AddressValue);
        }

        // ---------- the calling side ----------

        private static byte[] ConnectionResponse(ulong timeoutNs)
        {
            List<byte> response = [.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse];
            response.Add(0b00111000); // packet MTU, large message MTU and the timeout follow
            byte[] fields = new byte[2 + 8 + 8];
            BinaryPrimitives.WriteUInt16BigEndian(fields, 60000);
            BinaryPrimitives.WriteUInt64BigEndian(fields.AsSpan(2), ulong.MaxValue);
            BinaryPrimitives.WriteUInt64BigEndian(fields.AsSpan(10), timeoutNs);
            response.AddRange(fields);
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(response.ToArray()));
            response.AddRange(crc);
            return response.ToArray();
        }

        [Fact]
        public void CallingModemAnswersKeepAlivesAndNoticesASilentLine()
        {
            using UdpClient bareAnswering = new(new IPEndPoint(IPAddress.Loopback, 0));
            bareAnswering.Client.ReceiveTimeout = 5000;
            int port = ((IPEndPoint)bareAnswering.Client.LocalEndPoint!).Port;
            LowLatencyPhysicalModem calling = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1-1") };
            ConcurrentQueue<Exception> errors = new();
            calling.InternalErrorHappened += (e, emitter) => errors.Enqueue(e);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                calling.Dial();
                IPEndPoint? callingEndPoint = null;
                Assert.True(bareAnswering.Receive(ref callingEndPoint).AsSpan().StartsWith(CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixRequest));
                bareAnswering.Send(ConnectionResponse(TimeoutNs), callingEndPoint!);
                Assert.True(SpinWait.SpinUntil(() => calling.IsModemConnected, TimeSpan.FromSeconds(5)), $"attempt {attempt} didn't connect");

                bareAnswering.Send(KeepAlive, callingEndPoint!);
                IPEndPoint? from = null;
                Assert.Equal(KeepAliveAnswer, bareAnswering.Receive(ref from));

                // silence for longer than the timeout: the connection is lost, and the modem can dial again
                Assert.True(SpinWait.SpinUntil(() => !calling.IsModemConnected, TimeSpan.FromSeconds(3)), "the silent line wasn't noticed");
            }
            Assert.Equal(2, errors.Count(e => e is TimeoutException));
        }

        [Fact]
        public void TimeoutZeroTurnsModemKeepAliveOff()
        {
            var (answering, endPoint) = Answering(timeoutNs: 0);
            using UdpClient subscriber = ConnectBareSubscriber(endPoint);
            subscriber.Client.ReceiveTimeout = 700;
            IPEndPoint? from = null;
            Assert.Throws<SocketException>(() => subscriber.Receive(ref from)); // no Modem-KeepAlive comes
            Assert.True(answering.HasActiveConnection);

            using UdpClient bareAnswering = new(new IPEndPoint(IPAddress.Loopback, 0));
            bareAnswering.Client.ReceiveTimeout = 5000;
            LowLatencyPhysicalModem calling = new($"127.0.0.1:{((IPEndPoint)bareAnswering.Client.LocalEndPoint!).Port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1-1") };
            calling.Dial();
            IPEndPoint? callingEndPoint = null;
            bareAnswering.Receive(ref callingEndPoint);
            bareAnswering.Send(ConnectionResponse(0), callingEndPoint!);
            Assert.True(SpinWait.SpinUntil(() => calling.IsModemConnected, TimeSpan.FromSeconds(5)));
            Thread.Sleep(700);
            Assert.True(calling.IsModemConnected); // a silent line is fine without a timeout
        }
    }
}
