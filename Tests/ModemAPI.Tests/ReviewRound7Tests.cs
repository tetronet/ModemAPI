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
    /// Regression tests for the seventh review (V1, V2 in CODE_REVIEW.md).
    /// </summary>
    public class ReviewRound7Tests
    {
        private static int FreeUdpPort()
        {
            using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        private static UdpClient Socket(string ip = "127.0.0.1", int timeout = 5000)
        {
            UdpClient socket = new(new IPEndPoint(IPAddress.Parse(ip), 0));
            socket.Client.ReceiveTimeout = timeout;
            return socket;
        }

        private static bool IsConnectionResponse(byte[] datagram) => datagram.AsSpan().StartsWith(CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse);

        // ---------- V1: a connection request waiting for Answer() keeps the line ----------

        [Fact]
        public void PendingConnectionRequestCantBeTakenOver()
        {
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", true) { KeepAliveTimeoutNanoseconds = 0 };
            int incoming = 0;
            answering.AttachIncomingEvent(() => Interlocked.Increment(ref incoming)); // the application answers later
            IPEndPoint endPoint = new(IPAddress.Loopback, port);

            using UdpClient caller = Socket();
            caller.Send(ReviewRound5Tests.ConnectionRequest("7-1-1"), endPoint);
            Assert.True(SpinWait.SpinUntil(() => incoming == 1, TimeSpan.FromSeconds(5)));

            // another host asks while the first request waits for Answer()
            using UdpClient attacker = Socket("127.0.0.2", 500);
            attacker.Send(ReviewRound5Tests.ConnectionRequest("6-6-6"), endPoint);
            Thread.Sleep(200);
            Assert.Equal(1, incoming);

            answering.Answer();
            IPEndPoint? from = null;
            Assert.True(IsConnectionResponse(caller.Receive(ref from)), "the answer didn't go to the first caller");
            Assert.Equal("7-1-1", answering.GetSubscriberAddress().AddressValue);
            Assert.Throws<SocketException>(() => attacker.Receive(ref from));
        }

        [Fact]
        public void UnansweredConnectionRequestFreesTheLineAfterTheTimeout()
        {
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", true)
            {
                KeepAliveTimeoutNanoseconds = 0,
                PendingConnectionTimeout = TimeSpan.FromMilliseconds(200),
            };
            ConcurrentQueue<string?> incoming = new();
            answering.AttachIncomingEvent(() => incoming.Enqueue("call"));
            IPEndPoint endPoint = new(IPAddress.Loopback, port);

            using UdpClient silent = Socket();
            silent.Send(ReviewRound5Tests.ConnectionRequest("7-1-1"), endPoint);
            Assert.True(SpinWait.SpinUntil(() => incoming.Count == 1, TimeSpan.FromSeconds(5)));
            Thread.Sleep(400); // the application never answered it

            using UdpClient other = Socket("127.0.0.2");
            other.Send(ReviewRound5Tests.ConnectionRequest("7-1-2"), endPoint);
            Assert.True(SpinWait.SpinUntil(() => incoming.Count == 2, TimeSpan.FromSeconds(5)), "the line stayed taken by a request nobody answers");
            answering.Answer();
            IPEndPoint? from = null;
            Assert.True(IsConnectionResponse(other.Receive(ref from)));
        }

        // ---------- V2: a damaged connection response is reported, the parsers check the lengths ----------

        private static byte[] WithCrc(List<byte> bytes)
        {
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(bytes.ToArray()));
            return [.. bytes, .. crc];
        }

        [Fact]
        public void TruncatedConnectionResponseIsReportedAndDialCanGoOn()
        {
            using UdpClient bareAnswering = Socket();
            LowLatencyPhysicalModem calling = new($"127.0.0.1:{((IPEndPoint)bareAnswering.Client.LocalEndPoint!).Port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1-1") };
            ConcurrentQueue<Exception> errors = new();
            calling.InternalErrorHappened += (e, emitter) => errors.Enqueue(e);
            calling.Dial();
            IPEndPoint? callingEndPoint = null;
            bareAnswering.Receive(ref callingEndPoint);

            // a correct CRC, but the flags announce SMLA, both MTUs and the timeout, and only the SMLA length (too long) is there
            bareAnswering.Send(WithCrc([.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse, 0b00111010, 200]), callingEndPoint!);
            Assert.True(SpinWait.SpinUntil(() => !errors.IsEmpty, TimeSpan.FromSeconds(5)));
            Assert.IsType<ConnectionFailedException>(Assert.Single(errors));
            Assert.False(calling.IsModemConnected);

            // a good response afterwards connects
            bareAnswering.Send(WithCrc([.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse, 0]), callingEndPoint!);
            Assert.True(SpinWait.SpinUntil(() => calling.IsModemConnected, TimeSpan.FromSeconds(5)));
        }

        [Theory]
        [InlineData(new byte[] { 0b00001000 })]             // packet MTU announced, missing
        [InlineData(new byte[] { 0b00100000, 1, 2, 3 })]    // timeout announced, 3 of 8 bytes
        [InlineData(new byte[] { 0b00000010 })]             // SMLA announced, no length
        [InlineData(new byte[] { 0b00000010, 5, 0x37 })]    // SMLA of 5 characters, 1 there
        public void ConnectionResponseParserRejectsMissingFields(byte[] body)
        {
            CIoCILLowLatShortResponseBody response = PacketConverter.ParseLowLatencyConnectionResponse(WithCrc([.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse, .. body]));
            Assert.True(response.IsResponseDamaged);
        }

        [Fact]
        public void ConnectionRequestParserRejectsATooLongAddress()
        {
            List<byte> request = [.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixRequest, 200, .. Encoding.ASCII.GetBytes("7-1-1"), 255];
            Assert.True(PacketConverter.ParseLowLatencyConnectionRequest(WithCrc(request)).IsRequestDamaged);
            // the correct one still parses
            Assert.False(PacketConverter.ParseLowLatencyConnectionRequest(ReviewRound5Tests.ConnectionRequest("7-1-1")).IsRequestDamaged);
        }
    }
}
