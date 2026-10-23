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
    /// Regression tests for the fifth review (T1, T2 in CODE_REVIEW.md).
    /// </summary>
    public class ReviewRound5Tests
    {
        private const int SrtpTimeoutTicks = 2_000_000; // 200 ms

        private static int FreeUdpPort()
        {
            using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        private static (LowLatencyAnsweringPhysicalModem answering, int port) Answering()
        {
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", true);
            answering.AttachIncomingEvent(answering.Answer);
            return (answering, port);
        }

        private static (LowLatencyPhysicalModem modem, ConcurrentQueue<Packet> received) Connect(int port, string address)
        {
            LowLatencyPhysicalModem modem = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address(address) };
            ConcurrentQueue<Packet> received = new();
            modem.AttachReceiveEventNoUnfragment((p, k) => received.Enqueue(p));
            modem.Dial();
            Assert.True(SpinWait.SpinUntil(() => modem.IsModemConnected, TimeSpan.FromSeconds(5)), $"{address} couldn't connect");
            return (modem, received);
        }

        private static Packet ToSubscriber(byte value) => new() { Transmitter = new("7-1-9"), Receiver = new("7-1-1"), QueryType = "udp-test", DataBytes = [value], IsLastInSequence = true };

        // ---------- T1: reconnecting to the answering modem after the connection got lost without a word ----------

        [Fact]
        public void SubscriberReconnectsFromANewPortAfterLosingTheConnection()
        {
            // Modem-KeepAlive every 100 ms: the lost connection is found dead after ~300 ms
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", true) { KeepAliveTimeoutNanoseconds = 300_000_000 };
            answering.AttachIncomingEvent(answering.Answer);
            IPEndPoint endPoint = new(IPAddress.Loopback, port);
            // the first subscriber "crashes" right after connecting: it answers nothing anymore
            using UdpClient first = new(new IPEndPoint(IPAddress.Loopback, 0));
            first.Client.ReceiveTimeout = 5000;
            first.Send(ConnectionRequest("7-1-1"), endPoint);
            IPEndPoint? from = null;
            Assert.True(first.Receive(ref from).AsSpan().StartsWith(CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse));
            Assert.True(SpinWait.SpinUntil(() => !answering.HasActiveConnection, TimeSpan.FromSeconds(3)), "the lost connection wasn't found dead");

            // the subscriber comes back with a new socket (a new port)
            var (second, atSecond) = Connect(port, "7-1-1");
            answering.TransmitToSubscriber(ToSubscriber(2));
            Assert.True(SpinWait.SpinUntil(() => atSecond.Count == 1, TimeSpan.FromSeconds(5)), "the answers didn't go to the reconnected subscriber");
            Assert.Equal(2, atSecond.Single().DataBytes[0]);
        }

        internal static byte[] ConnectionRequest(string address)
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

        [Fact]
        public void ConnectionRequestFromAnotherHostDoesntTakeAnActiveLine()
        {
            var (answering, port) = Answering();
            var (subscriber, atSubscriber) = Connect(port, "7-1-1");

            // another host (127.0.0.2) sends a perfectly valid connection request while the line is in use
            List<byte> request = [.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixRequest];
            byte[] address = Encoding.UTF8.GetBytes("6-6-6");
            request.Add((byte)address.Length);
            request.AddRange(address);
            request.Add(255);
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(request.ToArray()));
            request.AddRange(crc);
            using UdpClient stranger = new(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
            stranger.Send(request.ToArray(), new IPEndPoint(IPAddress.Loopback, port));
            Thread.Sleep(300);

            Assert.Equal("7-1-1", answering.GetSubscriberAddress().AddressValue);
            answering.TransmitToSubscriber(ToSubscriber(3));
            Assert.True(SpinWait.SpinUntil(() => atSubscriber.Count == 1, TimeSpan.FromSeconds(5)), "the answer went to the stranger");
        }

        // ---------- T2: a client learns, that the remote side started a new stream ----------

        [Fact]
        public void ClientReportsAStreamItIgnores()
        {
            using FakeLink link = new();
            SRTPClient receiver = new(link.B, link.A.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            ConcurrentQueue<int> errors = new();
            receiver.OnError += (code, text) => errors.Enqueue(code);
            receiver.OnMessageReceived = (c, d) => { };

            SRTPClient first = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            first.Transmit([1]);
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(10), first.WaitForAcknowledgements));
            first.Close();

            // the remote side restarts: a new client with the same ConnectionID sends a few messages, which are retransmitted too
            SRTPClient second = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks) { RetransmissionsBeforeConsideringRemoteSideDead = 3 };
            for (byte i = 0; i < 3; i++) second.Transmit([i]);
            Assert.NotNull(Guard.RunWithin(TimeSpan.FromSeconds(10), second.WaitForAcknowledgements)); // nobody acknowledges it
            Assert.Equal(1, errors.Count(code => code == 18)); // reported once, not for every message and retransmission
            second.Close();
            receiver.Close();
        }
    }
}
