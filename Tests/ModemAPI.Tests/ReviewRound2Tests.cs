using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// Regression tests for the second review (N1-N9 in CODE_REVIEW.md).
    /// </summary>
    public class ReviewRound2Tests
    {
        private const int SrtpTimeoutTicks = 2_000_000; // 200 ms
        private const int ClientTimeoutTicks = 100_000_000; // 10 s

        private sealed class Resources : IResourceProvider
        {
            public readonly ConcurrentDictionary<string, byte[]> Data = new();
            public Stream? GetResource(string name) => Data.TryGetValue(name, out byte[]? d) ? new MemoryStream(d, false) : null;
            public long GetResourceSize(string name) => Data.TryGetValue(name, out byte[]? d) ? d.Length : 0;
            public byte[] GetResourceSha512Hashsum(string name) => SHA512.HashData(Data[name]);
            public LMDTPResponseFlags GetResourceResponseFlags(string name) => Data.ContainsKey(name) ? LMDTPResponseFlags.Success : LMDTPResponseFlags.LargeMessageNotFound;
        }

        private static byte[] RandomBytes(int length, int seed)
        {
            byte[] data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        // ---------- N1: repeated copies of an LMDTP request ----------

        [Fact]
        public void DuplicatedRequestIsAnsweredOnce()
        {
            using FakeLink link = new();
            link.AToB = p => FakeLink.IsLmdtpRequest(p) ? Fault.Duplicate : Fault.Deliver;
            Resources resources = new();
            resources.Data["r"] = RandomBytes(100_000, 1);
            LMDTPServer server = new(link.B, 1000, 2000, [], false, SrtpTimeoutTicks, 0) { PacketMaxLength = 1000, ResourceProvider = resources };
            int sessions = 0;
            server.SRTPClientReceived += (a, c) => Interlocked.Increment(ref sessions);
            server.Start();
            LMDTPClient client = new(link.A, link.B.LocalModemAddress!, 1000, SrtpTimeoutTicks);
            byte[] received = [];
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(30), () => received = client.Request("r", 1_000_000, ClientTimeoutTicks)));
            Assert.Equal(resources.Data["r"], received);
            Assert.Equal(1, sessions);
            client.Close();
            server.Stop();
        }

        [Fact]
        public void LateCopyOfAnOldRequestDoesntReplaceTheNewOne()
        {
            using FakeLink link = new();
            Resources resources = new();
            resources.Data["a"] = RandomBytes(5000, 2);
            resources.Data["b"] = RandomBytes(200_000, 3);
            LMDTPServer server = new(link.B, 1000, 2000, [], false, SrtpTimeoutTicks, 0) { PacketMaxLength = 1000, ResourceProvider = resources };
            int sessions = 0;
            server.SRTPClientReceived += (a, c) => Interlocked.Increment(ref sessions);
            server.Start();
            byte[]? requestA = null;
            int requests = 0;
            link.AToB = p =>
            {
                if (FakeLink.IsLmdtpRequest(p) && Interlocked.Increment(ref requests) == 1)
                {
                    requestA = (byte[])p.DataBytes.Clone();
                }
                else if (FakeLink.IsLmdtpRequest(p))
                {
                    // right behind the request for "b" comes a late copy of the request for "a"
                    byte[] lateCopy = requestA!;
                    Task.Run(() => link.A.LowLevelTransmit(lateCopy, link.B.LocalModemAddress!, "lm_tunnel", 1000));
                }
                return Fault.Deliver;
            };
            LMDTPClient client = new(link.A, link.B.LocalModemAddress!, 1000, SrtpTimeoutTicks);
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(30), () => client.Request("a", 1_000_000, ClientTimeoutTicks)));
            byte[] received = [];
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(30), () => received = client.Request("b", 1_000_000, ClientTimeoutTicks)));
            Assert.Equal(resources.Data["b"], received);
            Assert.Equal(2, sessions);
            client.Close();
            server.Stop();
        }

        // ---------- N3: the low latency modem doesn't block its reader by default ----------

        [Fact]
        public void LowLatencyModemDropsInsteadOfBlockingByDefault()
        {
            using System.Net.Sockets.UdpClient peer = new(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            int port = ((System.Net.IPEndPoint)peer.Client.LocalEndPoint!).Port;
            LowLatencyPhysicalModem modem = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP);
            Assert.True(modem.DropPacketsWhenOverloaded);
        }

        // ---------- N4, N5: SRTPServer ----------

        [Fact]
        public void ClientConnectedHandlerCanWaitForTheFirstMessage()
        {
            using FakeLink link = new();
            SRTPServer server = new(link.B, "srtp-test", 1, 100);
            bool gotItInsideTheHandler = false;
            server.OnClientConnected += (s, c) =>
            {
                // a "serve the client" handler: subscribe, then wait for the request before returning
                using ManualResetEventSlim first = new();
                c.OnMessageReceived = (cc, d) => first.Set();
                gotItInsideTheHandler = first.Wait(TimeSpan.FromSeconds(5));
            };
            server.Start();
            SRTPClient client = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, 300_000_000);
            client.Transmit([1]);
            Assert.True(SpinWait.SpinUntil(() => gotItInsideTheHandler, TimeSpan.FromSeconds(10)));
            client.Close();
        }

        [Fact]
        public void OnlyTheFirstMessageOpensASession()
        {
            using FakeLink link = new();
            SRTPServer server = new(link.B, "srtp-test", 1, 100);
            int connected = 0;
            server.OnClientConnected += (s, c) => Interlocked.Increment(ref connected);
            server.Start();
            Address server_ = link.B.LocalModemAddress!;
            byte[] Message(long id) { byte[] m = new byte[9]; BinaryPrimitives.WriteInt64BigEndian(m, id); return m; }
            link.A.LowLevelTransmit(new byte[8], server_, "srtp-test", 7);                  // a v1 acknowledgement
            link.A.LowLevelTransmit(new byte[8], server_, "srtp-test", 8, "srtp2a");        // a v2 acknowledgement
            link.A.LowLevelTransmit(Message(5), server_, "srtp-test", 9, "srtp2");          // a late message from the middle of a stream
            Thread.Sleep(500);
            Assert.Equal(0, connected);
            link.A.LowLevelTransmit(Message(0), server_, "srtp-test", 10, "srtp2");         // the first message of a stream
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref connected) == 1, TimeSpan.FromSeconds(5)));
        }

        // ---------- N6: why a packet couldn't be read ----------

        [Fact]
        public void ReadErrorTellsWhatWentWrong()
        {
            Packet packet = new() { Transmitter = new("7-1-1"), Receiver = new("7-1-2"), QueryType = "q", DataBytes = RandomBytes(50, 4) };
            byte[] good = PacketConverter.PacketToBytes(packet);
            Assert.Equal(PacketReadError.None, PacketConverter.BytesToPacket(good).ReadError);

            byte[] damaged = (byte[])good.Clone();
            damaged[40] ^= 1;
            Assert.Equal(PacketReadError.CrcMismatch, PacketConverter.BytesToPacket(damaged).ReadError);

            byte[] notAPacket = (byte[])good.Clone();
            notAPacket[0] ^= 1;
            Assert.Equal(PacketReadError.NotAPacket, PacketConverter.BytesToPacket(notAPacket).ReadError);

            byte[] wrongFormat = (byte[])good.Clone();
            wrongFormat[4] = 250; // receiver address longer than the packet
            BinaryPrimitives.WriteUInt32LittleEndian(wrongFormat.AsSpan(wrongFormat.Length - 4), Crc32.HashToUInt32(wrongFormat.AsSpan(0, wrongFormat.Length - 4)));
            Assert.Equal(PacketReadError.WrongFormat, PacketConverter.BytesToPacket(wrongFormat).ReadError);

            byte[] badMetadata = PacketConverter.PacketToBytes(new Packet { Transmitter = new("7-1-1"), Receiver = new("7-1-2"), QueryType = "q", DataBytes = [1], Metadata = "bad\u0001" });
            Assert.Equal(PacketReadError.WrongMetadata, PacketConverter.BytesToPacket(badMetadata).ReadError);
        }

        // ---------- N8: NLMNP ----------

        [Fact]
        public void ClosingDuringNotifyCancelsIt()
        {
            using FakeLink link = new() { Down = true };
            NLMNPClient sender = new(link.A) { ResendIntervalMs = 100, TimeoutMs = 20000 };
            Task.Delay(300).ContinueWith(_ => sender.Close());
            Exception? error = Guard.RunWithin(TimeSpan.FromSeconds(10), () => sender.Notify(link.B.LocalModemAddress!, new Address("7-1-5"), "file.bin"));
            Assert.IsType<OperationCanceledException>(error);
        }
    }
}
