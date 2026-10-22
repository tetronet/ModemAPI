using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// Regression tests for the fourth review (S1-S4 in CODE_REVIEW.md).
    /// </summary>
    public class ReviewRound4Tests
    {
        private const int SrtpTimeoutTicks = 2_000_000; // 200 ms

        private sealed class Server : IDisposable
        {
            public readonly SRTPServer Instance;
            public readonly ConcurrentQueue<SRTPClient> Sessions = new();
            public readonly ConcurrentQueue<(int Session, string Message)> Received = new();

            public Server(FakeLink link)
            {
                Instance = new(link.B, "srtp-test", 1, 100);
                Instance.OnClientConnected += (s, c) =>
                {
                    Sessions.Enqueue(c);
                    int number = Sessions.Count;
                    c.OnMessageReceived = (cc, d) => Received.Enqueue((number, Encoding.UTF8.GetString(d)));
                };
                Instance.Start();
            }

            public void Dispose()
            {
                Instance.Stop();
                foreach (SRTPClient session in Sessions) session.Close();
            }
        }

        private static Exception? Send(FakeLink link, string[] messages)
        {
            SRTPClient client = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            try
            {
                return Guard.RunWithin(TimeSpan.FromSeconds(20), () =>
                {
                    foreach (string m in messages) client.Transmit(Encoding.UTF8.GetBytes(m));
                    client.WaitForAcknowledgements();
                });
            }
            finally
            {
                client.Close();
            }
        }

        private static bool IsMessage0(Packet p) => FakeLink.IsData(p) && FakeLink.SequenceOf(p) == 0;

        // ---------- S1: the first message of the new stream doesn't come first ----------

        [Fact]
        public void NewStreamSurvivesTheLossOfItsFirstMessage()
        {
            using FakeLink link = new();
            using Server server = new(link);
            Assert.Null(Send(link, ["old0", "old1", "old2", "old3", "old4"]));
            // the first copy of message 0 of the new stream is lost, its retransmission gets through
            int zeros = 0;
            link.AToB = p => IsMessage0(p) && Interlocked.Increment(ref zeros) == 1 ? Fault.Drop : Fault.Deliver;
            Assert.Null(Send(link, ["new0", "new1", "new2"]));
            Assert.True(SpinWait.SpinUntil(() => server.Received.Count(r => r.Message.StartsWith("new")) == 3, TimeSpan.FromSeconds(10)),
                $"received: {string.Join(", ", server.Received)}");
            Assert.Equal(["new0", "new1", "new2"], server.Received.Where(r => r.Message.StartsWith("new")).Select(r => r.Message));
            Assert.Equal(["old0", "old1", "old2", "old3", "old4"], server.Received.Where(r => r.Message.StartsWith("old")).Select(r => r.Message));
        }

        [Fact]
        public void NewStreamSurvivesItsFirstMessageComingSecond()
        {
            using FakeLink link = new();
            using Server server = new(link);
            Assert.Null(Send(link, ["old0", "old1", "old2", "old3", "old4"]));
            // message 0 of the new stream is held back and arrives after message 1
            int zeros = 0;
            link.AToB = p => IsMessage0(p) && Interlocked.Increment(ref zeros) == 1 ? Fault.Swap : Fault.Deliver;
            Assert.Null(Send(link, ["new0", "new1", "new2"]));
            Assert.True(SpinWait.SpinUntil(() => server.Received.Count(r => r.Message.StartsWith("new")) == 3, TimeSpan.FromSeconds(10)),
                $"received: {string.Join(", ", server.Received)}");
            Assert.Equal(["new0", "new1", "new2"], server.Received.Where(r => r.Message.StartsWith("new")).Select(r => r.Message));
        }

        [Fact]
        public void AcknowledgementOfAnotherStreamIsIgnored()
        {
            // a v2 acknowledgement for another stream says everything up to 100 is delivered: the sender must not believe it
            using FakeLink link = new();
            link.BToA = _ => Fault.Drop; // the real receiver never answers
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks) { RetransmissionsBeforeConsideringRemoteSideDead = 3 };
            sender.Transmit([1]);
            byte[] ack = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(ack, 100);
            link.BToA = _ => Fault.Deliver;
            link.B.LowLevelTransmit(ack, link.A.LocalModemAddress!, "srtp-test", 7, "srtp2a:00000001");
            Exception? error = Guard.RunWithin(TimeSpan.FromSeconds(10), sender.WaitForAcknowledgements);
            Assert.NotNull(error); // still unacknowledged: the remote side is considered dead, not "everything delivered"
            sender.Close();
        }

        // ---------- S2: the answering modem over UDP answers only its subscriber ----------

        private static int FreeUdpPort()
        {
            using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        [Fact]
        public void StrangerDatagramDoesntRedirectTheAnswers()
        {
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", true);
            answering.AttachIncomingEvent(answering.Answer);
            LowLatencyPhysicalModem calling = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1-1") };
            ConcurrentQueue<Packet> atCalling = new();
            calling.AttachReceiveEventNoUnfragment((p, k) => atCalling.Enqueue(p));
            calling.Dial();
            Assert.True(SpinWait.SpinUntil(() => calling.IsModemConnected, TimeSpan.FromSeconds(5)));

            // somebody else sends garbage and even a packet to the answering modem's port
            using UdpClient stranger = new(new IPEndPoint(IPAddress.Loopback, 0));
            stranger.Send([1, 2, 3], new IPEndPoint(IPAddress.Loopback, port));
            stranger.Send(PacketConverter.PacketToBytes(new Packet { Transmitter = new("6-6-6"), Receiver = new("7-1-9"), QueryType = "q", DataBytes = [6], IsLastInSequence = true }), new IPEndPoint(IPAddress.Loopback, port));
            Thread.Sleep(200);

            answering.TransmitToSubscriber(new Packet { Transmitter = new("7-1-9"), Receiver = new("7-1-1"), QueryType = "udp-test", DataBytes = [42], IsLastInSequence = true });
            Assert.True(SpinWait.SpinUntil(() => atCalling.Count == 1, TimeSpan.FromSeconds(5)), "the answer went to the stranger");
            stranger.Client.ReceiveTimeout = 300;
            IPEndPoint? from = null;
            Assert.Throws<SocketException>(() => stranger.Receive(ref from));
        }

        // ---------- S3: the idle timer of SRTPServer ----------

        [Fact]
        public void IdleTimeoutCanBeSetAfterStartAndStopEndsIt()
        {
            using FakeLink link = new();
            using Server server = new(link);
            server.Instance.IdleSessionTimeout = TimeSpan.FromMilliseconds(500); // set after Start
            int subscribersBefore = link.B.SubscriberCount;
            SRTPClient client = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            client.Transmit([1]);
            Assert.True(SpinWait.SpinUntil(() => server.Sessions.Count == 1, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => link.B.SubscriberCount == subscribersBefore, TimeSpan.FromSeconds(5)), "the idle session wasn't closed");
            client.Close();
        }

        [Fact]
        public void AbandonedServerCanBeCollected()
        {
            using FakeLink link = new();
            WeakReference weak = CreateAbandonedServer(link);
            for (int i = 0; i < 5 && weak.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
            Assert.False(weak.IsAlive, "the stopped server is still kept alive");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference CreateAbandonedServer(FakeLink link)
        {
            SRTPServer server = new(link.B, "srtp-test", 1, 100) { IdleSessionTimeout = TimeSpan.FromSeconds(30) };
            server.Start();
            server.Stop();
            return new WeakReference(server);
        }
    }
}
