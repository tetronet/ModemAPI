using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// Regression tests for the third review (R1-R4 in CODE_REVIEW.md).
    /// </summary>
    public class ReviewRound3Tests
    {
        private const int SrtpTimeoutTicks = 2_000_000; // 200 ms

        // ---------- R1: a client starts over with the same ConnectionID ----------

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
                foreach (SRTPClient session in Sessions) session.Close();
            }
        }

        private static void Send(FakeLink link, string[] messages)
        {
            SRTPClient client = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(20), () =>
            {
                foreach (string m in messages) client.Transmit(Encoding.UTF8.GetBytes(m));
                client.WaitForAcknowledgements();
            }));
            client.Close();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)] // without metadata: no stream ID, the new stream is told apart by the contents of message 0
        public void ClientStartingOverWithTheSameConnectionIdGetsANewSession(bool v1)
        {
            using FakeLink link = new() { StripMetadata = v1 };
            using Server server = new(link);
            Send(link, ["old0", "old1", "old2", "old3", "old4"]);
            Send(link, ["new0", "new1", "new2"]);
            Assert.True(SpinWait.SpinUntil(() => server.Received.Count == 8, TimeSpan.FromSeconds(10)), $"received: {string.Join(", ", server.Received)}");
            Assert.Equal(2, server.Sessions.Count);
            Assert.Equal(["old0", "old1", "old2", "old3", "old4"], server.Received.Where(r => r.Session == 1).Select(r => r.Message));
            Assert.Equal(["new0", "new1", "new2"], server.Received.Where(r => r.Session == 2).Select(r => r.Message));
        }

        [Fact]
        public void RepeatedFirstMessageDoesntStartANewSession()
        {
            using FakeLink link = new();
            // every copy of message 0 arrives twice, and its acknowledgements are lost a few times, so it gets retransmitted too
            link.AToB = p => p.DataBytes.Length > 8 && BinaryPrimitives.ReadInt64BigEndian(p.DataBytes) == 0 ? Fault.Duplicate : Fault.Deliver;
            int acks = 0;
            link.BToA = p => FakeLink.IsAck(p) && Interlocked.Increment(ref acks) <= 3 ? Fault.Drop : Fault.Deliver;
            using Server server = new(link);
            Send(link, ["a", "b", "c"]);
            Assert.True(SpinWait.SpinUntil(() => server.Received.Count == 3, TimeSpan.FromSeconds(10)));
            Thread.Sleep(300);
            Assert.Single(server.Sessions);
            Assert.Equal(["a", "b", "c"], server.Received.Select(r => r.Message));
        }

        [Fact]
        public void IdleSessionsAreClosedWhenAsked()
        {
            using FakeLink link = new();
            using Server server = new(link);
            server.Instance.IdleSessionTimeout = TimeSpan.FromMilliseconds(500);
            int subscribersBefore = link.B.SubscriberCount;
            SRTPClient client = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            client.Transmit([1]);
            Assert.True(SpinWait.SpinUntil(() => server.Sessions.Count == 1, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => link.B.SubscriberCount == subscribersBefore, TimeSpan.FromSeconds(5)), "the idle session wasn't closed");
            Assert.False(server.Instance.RemoveClient(link.A.LocalModemAddress!, 7));
            client.Close();
        }

        // ---------- R2: the answering modem over UDP ----------

        private static int FreeUdpPort()
        {
            using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        [Fact]
        public void AnsweringModemWorksOverUdp()
        {
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", true);
            answering.AttachIncomingEvent(answering.Answer);
            ConcurrentQueue<Packet> atAnswering = new();
            answering.InternalAttachReceiveEvent((p, k) => atAnswering.Enqueue(p));

            LowLatencyPhysicalModem calling = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1-1") };
            ConcurrentQueue<Packet> atCalling = new();
            calling.AttachReceiveEventNoUnfragment((p, k) => atCalling.Enqueue(p));
            calling.Dial();
            Assert.True(SpinWait.SpinUntil(() => calling.IsModemConnected, TimeSpan.FromSeconds(5)), "the connection wasn't established");
            Assert.Equal("7-1-1", answering.GetSubscriberAddress().AddressValue);

            calling.LowLevelTransmit(Encoding.UTF8.GetBytes("up"), new Address("7-1-9"), "udp-test", 3, null);
            Assert.True(SpinWait.SpinUntil(() => atAnswering.Count == 1, TimeSpan.FromSeconds(5)));
            Assert.Equal("up", Encoding.UTF8.GetString(atAnswering.Single().DataBytes));

            byte[] big = RandomNumberGenerator.GetBytes(40000);
            answering.TransmitToSubscriber(new Packet { Transmitter = new("7-1-9"), Receiver = new("7-1-1"), QueryType = "udp-test", DataBytes = big, IsLastInSequence = true });
            Assert.True(SpinWait.SpinUntil(() => atCalling.Count == 1, TimeSpan.FromSeconds(5)));
            Assert.Equal(big, atCalling.Single().DataBytes);
        }

        // ---------- R3: request IDs of LMDTP ----------

        private sealed class OneResource(byte[] data) : IResourceProvider
        {
            public Stream? GetResource(string name) => new MemoryStream(data, false);
            public long GetResourceSize(string name) => data.Length;
            public byte[] GetResourceSha512Hashsum(string name) => SHA512.HashData(data);
            public LMDTPResponseFlags GetResourceResponseFlags(string name) => LMDTPResponseFlags.Success;
        }

        private static byte[] RequestWithId(uint id)
        {
            byte[] request = new byte[2 + 2 + 1 + 8 + 1 + 1 + 4];
            BinaryPrimitives.WriteUInt16BigEndian(request, LMDTPClient.LMDTP_REQUEST_PREFIX);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 1);
            request[4] = (byte)'r';
            BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(5), 1_000_000);
            request[13] = (byte)LMDTPRequestType.Obtain;
            request[14] = (byte)LMDTPHashAlgorithms.Sha512;
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(15), id);
            return request;
        }

        [Fact]
        public void RequestIdZeroMeansNoId()
        {
            using FakeLink link = new();
            LMDTPServer server = new(link.B, 1000, 2000, [], false, SrtpTimeoutTicks, 0) { ResourceProvider = new OneResource([1, 2, 3]) };
            int sessions = 0;
            server.SRTPClientReceived += (a, c) => Interlocked.Increment(ref sessions);
            server.Start();
            link.A.LowLevelTransmit(RequestWithId(0), link.B.LocalModemAddress!, "lm_tunnel", 1000);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref sessions) == 1, TimeSpan.FromSeconds(5)));
            link.A.LowLevelTransmit(RequestWithId(0), link.B.LocalModemAddress!, "lm_tunnel", 1000);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref sessions) == 2, TimeSpan.FromSeconds(5)), "a second request with ID 0 was taken for a duplicate");
            server.Stop();
        }

        [Fact]
        public void RequestRefusedByTheLimitIsntRememberedAsSeen()
        {
            using FakeLink link = new();
            LMDTPServer server = new(link.B, 1000, 2000, [], false, SrtpTimeoutTicks, 0) { ResourceProvider = new OneResource([1, 2, 3]), MaxSessionsPerAddress = 1 };
            int sessions = 0;
            server.SRTPClientReceived += (a, c) => Interlocked.Increment(ref sessions);
            server.Start();
            // nobody acknowledges, so the first session stays (until it gives up after 10 retransmissions of 200 ms)
            link.A.LowLevelTransmit(RequestWithId(1), link.B.LocalModemAddress!, "lm_tunnel", 1000);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref sessions) == 1, TimeSpan.FromSeconds(5)));
            link.A.LowLevelTransmit(RequestWithId(2), link.B.LocalModemAddress!, "lm_tunnel", 1001); // over the limit
            Thread.Sleep(300);
            Assert.Equal(1, Volatile.Read(ref sessions));
            server.Stop(); // frees the limit
            server.Start();
            link.A.LowLevelTransmit(RequestWithId(2), link.B.LocalModemAddress!, "lm_tunnel", 1001); // the same request again
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref sessions) == 2, TimeSpan.FromSeconds(5)), "the refused request was remembered as answered");
            server.Stop();
        }

        // ---------- R4: the message handler ----------

        [Fact]
        public void ThrowingHandlerDoesntStopTheFollowingMessages()
        {
            using FakeLink link = new();
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            SRTPClient receiver = new(link.B, link.A.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            ConcurrentQueue<byte> received = new();
            int handlerErrors = 0;
            receiver.OnError += (code, text) => { if (code == -2) Interlocked.Increment(ref handlerErrors); };
            receiver.OnMessageReceived = (c, d) =>
            {
                received.Enqueue(d[0]);
                if (d[0] == 2) throw new InvalidOperationException("handler bug");
            };
            for (byte i = 0; i < 5; i++) sender.Transmit([i]);
            Assert.True(SpinWait.SpinUntil(() => received.Count == 5, TimeSpan.FromSeconds(5)), $"received {received.Count}");
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, received.ToArray());
            Assert.Equal(1, handlerErrors);
            receiver.OnMessageReceived = null!; // means "nobody listens"
            sender.Transmit([5]);
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(10), sender.WaitForAcknowledgements));
            sender.Close();
            receiver.Close();
        }
    }
}
