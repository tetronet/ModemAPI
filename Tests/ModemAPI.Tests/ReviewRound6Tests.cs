using System.Buffers.Binary;
using System.Collections;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// Regression tests for the sixth review (U2, U3, U4 in CODE_REVIEW.md).
    /// </summary>
    public class ReviewRound6Tests
    {
        private static int FreeUdpPort()
        {
            using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        private static UdpClient Socket()
        {
            UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
            socket.Client.ReceiveTimeout = 5000;
            return socket;
        }

        private static bool IsConnectionResponse(byte[] datagram) => datagram.AsSpan().StartsWith(CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse);

        // ---------- U2: over UDP the same IP address with another port doesn't take an active line ----------

        [Fact]
        public void ConnectionRequestFromAnotherPortOfTheSameIpDoesntTakeAnActiveLine()
        {
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", true) { KeepAliveTimeoutNanoseconds = 0 };
            answering.AttachIncomingEvent(answering.Answer);
            IPEndPoint endPoint = new(IPAddress.Loopback, port);
            using UdpClient subscriber = Socket();
            subscriber.Send(ReviewRound5Tests.ConnectionRequest("7-1-1"), endPoint);
            IPEndPoint? from = null;
            Assert.True(IsConnectionResponse(subscriber.Receive(ref from)));

            // the same IP address (forged, or another host behind the same NAT), another port
            using UdpClient attacker = Socket();
            attacker.Client.ReceiveTimeout = 500;
            attacker.Send(ReviewRound5Tests.ConnectionRequest("6-6-6"), endPoint);
            Assert.Throws<SocketException>(() => attacker.Receive(ref from));
            Assert.True(answering.HasActiveConnection);
            Assert.Equal("7-1-1", answering.GetSubscriberAddress().AddressValue);
            answering.TransmitToSubscriber(new Packet { Transmitter = new("7-1-9"), Receiver = new("7-1-1"), QueryType = "udp-test", DataBytes = [1], IsLastInSequence = true });
            Assert.True(PacketConverter.CheckIsPacket(subscriber.Receive(ref from)), "the answer didn't go to the subscriber");
        }

        [Fact]
        public void SubscriberReconnectsFromItsOwnAddressAndPortRightAway()
        {
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", true) { KeepAliveTimeoutNanoseconds = 0 };
            answering.AttachIncomingEvent(answering.Answer);
            IPEndPoint endPoint = new(IPAddress.Loopback, port);
            using UdpClient subscriber = Socket();
            IPEndPoint? from = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                subscriber.Send(ReviewRound5Tests.ConnectionRequest("7-1-1"), endPoint);
                Assert.True(IsConnectionResponse(subscriber.Receive(ref from)), $"attempt {attempt} wasn't answered");
            }
            Assert.True(answering.HasActiveConnection);
        }

        // ---------- U3: the timeout in the connection response is optional ----------

        private static byte[] ConnectionResponseWithoutOptionalFields()
        {
            List<byte> response = [.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse];
            response.Add(0); // no flags: no SMLA, MTU or timeout
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(response.ToArray()));
            response.AddRange(crc);
            return response.ToArray();
        }

        [Fact]
        public void ConnectionResponseWithoutTimeoutConnects()
        {
            using UdpClient bareAnswering = Socket();
            LowLatencyPhysicalModem calling = new($"127.0.0.1:{((IPEndPoint)bareAnswering.Client.LocalEndPoint!).Port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1-1") };
            calling.Dial();
            IPEndPoint? callingEndPoint = null;
            bareAnswering.Receive(ref callingEndPoint);
            bareAnswering.Send(ConnectionResponseWithoutOptionalFields(), callingEndPoint!);
            Assert.True(SpinWait.SpinUntil(() => calling.IsModemConnected, TimeSpan.FromSeconds(5)), "a response without the timeout didn't connect");
            Thread.Sleep(500);
            Assert.True(calling.IsModemConnected); // no timeout: no Modem-KeepAlive, a silent line is fine
        }

        // ---------- U4: Dial doesn't add a handler every time ----------

        private static int ReadHandlerCount(LowLatencyPhysicalModem modem)
        {
            object communicator = typeof(LowLatencyPhysicalModem).GetField("Communicator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(modem)!;
            return ((ICollection)communicator.GetType().GetField("readEvents", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(communicator)!).Count;
        }

        [Fact]
        public void RepeatedDialAndDropDontPileUpHandlers()
        {
            using UdpClient bareAnswering = Socket();
            LowLatencyPhysicalModem calling = new($"127.0.0.1:{((IPEndPoint)bareAnswering.Client.LocalEndPoint!).Port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1-1") };
            calling.Dial();
            calling.Drop(false);
            int handlers = ReadHandlerCount(calling);
            for (int i = 0; i < 20; i++)
            {
                calling.Dial();
                calling.Drop(false);
            }
            Assert.Equal(handlers, ReadHandlerCount(calling));
        }
    }
}
