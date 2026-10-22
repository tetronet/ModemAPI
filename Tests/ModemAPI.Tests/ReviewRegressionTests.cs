using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// Regression tests for the bugs found in CODE_REVIEW.md, one region per item.
    /// </summary>
    public class ReviewRegressionTests
    {
        private const int SrtpTimeoutTicks = 2_000_000; // 200 ms
        private const int ClientTimeoutTicks = 100_000_000; // 10 s

        private sealed class Resources : IResourceProvider
        {
            public readonly ConcurrentDictionary<string, byte[]> Data = new();
            public Func<string, Stream?>? StreamOverride;
            public Stream? GetResource(string name) => StreamOverride != null ? StreamOverride(name) : Data.TryGetValue(name, out byte[]? d) ? new MemoryStream(d, false) : null;
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

        private static (FakeLink link, Resources resources, LMDTPServer server, List<Exception> errors) LmdtpSetup()
        {
            FakeLink link = new();
            Resources resources = new();
            LMDTPServer server = new(link.B, 1000, 100000, [], false, SrtpTimeoutTicks, 0) { PacketMaxLength = 1000, ResourceProvider = resources };
            List<Exception> errors = [];
            server.ErrorOccured += e => { lock (errors) errors.Add(e); };
            server.Start();
            return (link, resources, server, errors);
        }

        // ---------- #1: one LMDTPClient, several requests ----------

        [Fact]
        public void SeveralRequestsOnTheSameClient()
        {
            var (link, resources, server, _) = LmdtpSetup();
            using (link)
            {
                resources.Data["a"] = RandomBytes(5000, 1);
                resources.Data["b"] = RandomBytes(12345, 2);
                LMDTPClient client = new(link.A, link.B.LocalModemAddress!, 1000, SrtpTimeoutTicks);
                foreach (string name in new[] { "a", "b", "a", "b", "b" })
                {
                    byte[] received = [];
                    Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(20), () => received = client.Request(name, 1_000_000, ClientTimeoutTicks)));
                    Assert.Equal(resources.Data[name], received);
                }
                client.Close();
                server.Stop();
            }
        }

        [Fact]
        public void RequestAfterAFailedRequestOnTheSameClient()
        {
            var (link, resources, server, _) = LmdtpSetup();
            using (link)
            {
                resources.Data["r"] = RandomBytes(20000, 3);
                LMDTPClient client = new(link.A, link.B.LocalModemAddress!, 1000, SrtpTimeoutTicks);
                // fails at once
                Assert.IsType<FileNotFoundException>(Guard.RunWithin(TimeSpan.FromSeconds(20), () => client.Request("missing", 1_000_000, ClientTimeoutTicks)));
                // times out in the middle: the server still has a session for this address and ConnectionID when the next request comes
                int delivered = 0;
                link.BToA = p => FakeLink.IsData(p) && Interlocked.Increment(ref delivered) > 5 ? Fault.Drop : Fault.Deliver;
                Assert.IsType<TimeoutException>(Guard.RunWithin(TimeSpan.FromSeconds(20), () => client.Request("r", 1_000_000, 10_000_000)));
                link.BToA = _ => Fault.Deliver;
                byte[] received = [];
                Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(20), () => received = client.Request("r", 1_000_000, ClientTimeoutTicks)));
                Assert.Equal(resources.Data["r"], received);
                client.Close();
                server.Stop();
            }
        }

        // ---------- #2: L1 frames of the TCP and UDP wires ----------

        [Fact]
        public void L1FramesSurviveAnyCutting()
        {
            byte[][] payloads = [RandomBytes(10, 4), [], RandomBytes(70000, 5), RandomBytes(1, 6), RandomBytes(300, 7)];
            byte[] stream = payloads.SelectMany(L1Framing.Frame).ToArray();
            Assert.Equal([0, 0, 10], stream[..3]);
            foreach (int cut in new[] { 1, 2, 3, 7, 1024, 4096, stream.Length })
            {
                List<byte> buffer = [];
                List<byte[]> frames = [];
                for (int i = 0; i < stream.Length; i += cut)
                {
                    buffer.AddRange(stream.AsSpan(i, Math.Min(cut, stream.Length - i)));
                    L1Framing.ExtractFrames(buffer, frames.Add, e => throw e);
                }
                Assert.Equal(payloads.Length, frames.Count);
                for (int i = 0; i < payloads.Length; i++) Assert.Equal(payloads[i], frames[i]);
                Assert.Empty(buffer);
            }
        }

        [Fact]
        public void L1FrameHandlerErrorsDontStopTheOtherFrames()
        {
            List<byte> buffer = [.. L1Framing.Frame([1]), .. L1Framing.Frame([2]), .. L1Framing.Frame([3])];
            List<byte> handled = [];
            List<Exception> errors = [];
            L1Framing.ExtractFrames(buffer, f => { if (f[0] == 2) throw new InvalidOperationException(); handled.Add(f[0]); }, errors.Add);
            Assert.Equal([1, 3], handled);
            Assert.Single(errors);
        }

        [Fact]
        public void LowLatencyModemTalksUdpOneDatagramPerPacket()
        {
            using UdpClient peer = new(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;
            LowLatencyPhysicalModem modem = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP) { LocalModemAddress = new Address("7-1-1") };
            ConcurrentQueue<Packet> received = new();
            modem.AttachReceiveEventNoUnfragment((p, k) => received.Enqueue(p));

            // sending: a 30000-byte packet is one datagram, without a length header
            byte[] payload = RandomBytes(30000, 8);
            modem.LowLevelTransmit(payload, new Address("7-1-2"), "udp-test", 5, null);
            IPEndPoint? modemEndPoint = null;
            peer.Client.ReceiveTimeout = 5000;
            Packet sent = PacketConverter.BytesToPacket(peer.Receive(ref modemEndPoint));
            Assert.False(sent.IsErrorWhileReading);
            Assert.Equal(payload, sent.DataBytes);

            // receiving: every datagram is one packet, and garbage or a lost datagram doesn't affect the ones after it
            byte[] Encode(int n) => PacketConverter.PacketToBytes(new Packet { Transmitter = new("7-1-2"), Receiver = new("7-1-1"), QueryType = "udp-test", DataBytes = [(byte)n, 42], IsLastInSequence = true });
            peer.Send(Encode(1), modemEndPoint!);
            peer.Send(Encode(2)[..20], modemEndPoint!); // a cut packet
            peer.Send([0, 0, 1, 2, 3], modemEndPoint!); // garbage, which the old length header would have read as a huge frame
            // packet 3 is "lost": it's never sent
            peer.Send(Encode(4), modemEndPoint!);
            peer.Send(Encode(5), modemEndPoint!);
            Assert.True(SpinWait.SpinUntil(() => received.Count >= 3, TimeSpan.FromSeconds(5)), $"received {received.Count} packets");
            Assert.Equal([1, 4, 5], received.Select(p => (int)p.DataBytes[0]));
        }

        // ---------- #3, #4, #12: queues of packets ----------

        private static Packet Part(string from, ulong messageId, ulong no, bool last, byte value) =>
            new() { Transmitter = new(from), Receiver = new("7-1-9"), MessageId = messageId, PacketNo = no, IsLastInSequence = last, DataBytes = [value], QueryType = "q" };

        [Fact]
        public void IncompleteQueuesTimeOut()
        {
            PacketQueueAssembler assembler = new(0, 2_000_000); // 200 ms
            assembler.AddPacket(Part("7-1-1", 1, 0, false, 1));
            Assert.Equal(1, assembler.Count);
            Assert.True(SpinWait.SpinUntil(() => assembler.Count == 0, TimeSpan.FromSeconds(5)), "the incomplete queue was never removed");
        }

        [Fact]
        public void QueuesOfDifferentTransmittersDontMix()
        {
            PacketQueueAssembler assembler = new();
            assembler.AddPacket(Part("7-1-1", 5, 0, false, 1));
            assembler.AddPacket(Part("7-1-2", 5, 0, false, 10));
            assembler.AddPacket(Part("7-1-2", 5, 1, true, 11));
            assembler.AddPacket(Part("7-1-1", 5, 1, true, 2));
            DataBlock? first = assembler.GetPacketQueue(new Address("7-1-1"), 5)?.Assemble();
            DataBlock? second = assembler.GetPacketQueue(new Address("7-1-2"), 5)?.Assemble();
            Assert.Equal([1, 2], first!.DataBytes);
            Assert.Equal([10, 11], second!.DataBytes);
        }

        [Fact]
        public void QueueAssemblesOnlyWhenCompleteAndFillsDataString()
        {
            PacketQueue queue = new(PacketQueueAssembler.DefaultTimeout);
            byte[] text = Encoding.UTF8.GetBytes("ready");
            queue.AddPacket(Part("7-1-1", 1, 2, true, text[2]));
            Assert.Null(queue.Assemble());
            queue.AddPacket(Part("7-1-1", 1, 0, false, text[0]));
            Assert.Null(queue.Assemble());
            queue.AddPacket(Part("7-1-1", 1, 1, false, text[1]));
            Assert.Equal(Encoding.UTF8.GetString(text[..3]), queue.Assemble()!.DataString);
        }

        [Fact]
        public void AssemblerKeepsItsLimit()
        {
            PacketQueueAssembler assembler = new(2);
            for (ulong id = 0; id < 5; id++) assembler.AddPacket(Part("7-1-1", id, 0, false, 0));
            Assert.Equal(2, assembler.Count);
        }

        [Fact]
        public void RetransmissionTimeoutUsesAMonotonicClock()
        {
            ReliablePacketInformation info = new(0, Stopwatch.GetTimestamp(), [1]);
            Assert.False(info.CheckTimeout(10_000_000));
            Thread.Sleep(30);
            Assert.True(info.CheckTimeout(100_000));
        }

        // ---------- #5: SRTPServer ----------

        [Fact]
        public void SrtpServerCreatesOneSessionAndKeepsTheFirstMessage()
        {
            using FakeLink link = new();
            SRTPServer server = new(link.B, "srtp-test", 1, 100);
            ConcurrentBag<SRTPClient> sessions = [];
            ConcurrentQueue<byte[]> received = new();
            server.OnClientConnected += (s, c) => { sessions.Add(c); c.OnMessageReceived = (cc, d) => received.Enqueue(d); };
            server.Start();
            // the client retransmits only after 30 s: the first message must get through without a retransmission
            SRTPClient client = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, 300_000_000);
            for (int i = 0; i < 50; i++) client.Transmit([(byte)i]);
            Assert.True(SpinWait.SpinUntil(() => received.Count == 50, TimeSpan.FromSeconds(10)), $"received {received.Count} of 50 within 10 s; sessions {sessions.Count}; " + string.Join("; ", sessions.Select(c => $"missing {c.MissingPacket}, buffered {c.CountReorderingPackets()}")) + "; got " + string.Join(",", received.Select(d => d[0])));
            Assert.Equal(Enumerable.Range(0, 50).Select(i => (byte)i), received.Select(d => d[0]));
            Assert.Single(sessions);
            client.Close();
        }

        [Fact]
        public void SrtpServerStopsTakingNewClientsAndRemoveClientClosesIt()
        {
            using FakeLink link = new();
            SRTPServer server = new(link.B, "srtp-test", 1, 100);
            ConcurrentBag<SRTPClient> sessions = [];
            server.OnClientConnected += (s, c) => sessions.Add(c);
            server.Start();
            SRTPClient first = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            first.Transmit([1]);
            Assert.True(SpinWait.SpinUntil(() => sessions.Count == 1, TimeSpan.FromSeconds(5)));
            int subscribers = link.B.SubscriberCount;
            Assert.True(server.RemoveClient(link.A.LocalModemAddress!, 7));
            Assert.Equal(subscribers - 1, link.B.SubscriberCount);
            server.Stop();
            SRTPClient second = new(link.A, link.B.LocalModemAddress!, "srtp-test", 8, SrtpTimeoutTicks);
            second.Transmit([2]);
            Thread.Sleep(500);
            Assert.Single(sessions);
            first.Close();
            second.Close();
        }

        // ---------- #6: Transmit from several threads ----------

        [Fact]
        public void TransmitFromSeveralThreads()
        {
            using FakeLink link = new();
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            SRTPClient receiver = new(link.B, link.A.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks);
            ConcurrentQueue<byte[]> received = new();
            receiver.OnMessageReceived = (c, d) => received.Enqueue(d);
            const int threads = 8, perThread = 200;
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(30), () =>
            {
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                {
                    for (int i = 0; i < perThread; i++) sender.Transmit([(byte)t, (byte)(i >> 8), (byte)i]);
                });
                sender.WaitForAcknowledgements();
            }));
            Assert.True(SpinWait.SpinUntil(() => received.Count == threads * perThread, TimeSpan.FromSeconds(5)), $"received {received.Count}");
            // every message exactly once, and the messages of every thread in the order it sent them
            for (int t = 0; t < threads; t++)
            {
                Assert.Equal(Enumerable.Range(0, perThread), received.Where(d => d[0] == t).Select(d => d[1] << 8 | d[2]));
            }
            sender.Close();
            receiver.Close();
        }

        [Fact]
        public void TransmitReportsTheRealReasonAfterTheRemoteSideDied()
        {
            using FakeLink link = new() { Down = true };
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, SrtpTimeoutTicks) { RetransmissionsBeforeConsideringRemoteSideDead = 2 };
            sender.Transmit([1]);
            Assert.True(SpinWait.SpinUntil(() => { try { sender.Transmit([2]); return false; } catch (OperationCanceledException) { return true; } catch (InvalidOperationException) { return false; } }, TimeSpan.FromSeconds(10)));
        }

        // ---------- #7, #8: LMDTPServer ----------

        [Fact]
        public void ServerKeepsTheSessionLimitPerAddress()
        {
            var (link, resources, server, errors) = LmdtpSetup();
            using (link)
            {
                server.Stop();
                server.MaxSessionsPerAddress = 1;
                server.Start();
                resources.Data["r"] = RandomBytes(100, 9);
                // nobody acknowledges these requests, so their sessions stay
                byte[] request = Request("r");
                link.A.LowLevelTransmit(request, link.B.LocalModemAddress!, "lm_tunnel", 1000);
                link.A.LowLevelTransmit(request, link.B.LocalModemAddress!, "lm_tunnel", 1001);
                Assert.True(SpinWait.SpinUntil(() => { lock (errors) return errors.Any(e => e.Message.Contains("MaxSessionsPerAddress")); }, TimeSpan.FromSeconds(5)));
                server.Stop();
            }
        }

        [Fact]
        public void ServerCanBeStartedOnceAndStopped()
        {
            var (link, _, server, _) = LmdtpSetup();
            using (link)
            {
                Assert.Throws<InvalidOperationException>(server.Start);
                server.Stop();
                Assert.Equal(0, link.B.SubscriberCount);
                server.Start();
                Assert.Equal(1, link.B.SubscriberCount);
                server.Stop();
            }
        }

        [Fact]
        public void ProviderObjectDisposedExceptionIsAnErrorNotATimeout()
        {
            var (link, resources, server, errors) = LmdtpSetup();
            using (link)
            {
                resources.Data["r"] = RandomBytes(100, 10);
                resources.StreamOverride = _ => throw new ObjectDisposedException("resource");
                int timedOut = 0;
                server.SRTPClientTransmitTimedOut += (a, c) => Interlocked.Increment(ref timedOut);
                LMDTPClient client = new(link.A, link.B.LocalModemAddress!, 1000, SrtpTimeoutTicks);
                Stopwatch sw = Stopwatch.StartNew();
                Assert.IsType<ConnectionFailedException>(Guard.RunWithin(TimeSpan.FromSeconds(20), () => client.Request("r", 1_000_000, ClientTimeoutTicks)));
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
                lock (errors) Assert.Contains(errors, e => e is ObjectDisposedException);
                Assert.Equal(0, timedOut);
                client.Close();
                server.Stop();
            }
        }

        private static byte[] Request(string name)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] request = new byte[2 + 2 + nameBytes.Length + 8 + 1];
            BinaryPrimitives.WriteUInt16BigEndian(request, LMDTPClient.LMDTP_REQUEST_PREFIX);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)nameBytes.Length);
            nameBytes.CopyTo(request, 4);
            BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(4 + nameBytes.Length), 1_000_000);
            request[^1] = (byte)LMDTPRequestType.Obtain;
            return request;
        }

        // ---------- #9: parsing packets ----------

        private static byte[] WithCrc(byte[] packet)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(packet.Length - 4), Crc32.HashToUInt32(packet.AsSpan(0, packet.Length - 4)));
            return packet;
        }

        [Fact]
        public void BrokenLengthsGiveAnErroredPacketInsteadOfAnException()
        {
            Packet packet = new() { Transmitter = new("7-1-1"), Receiver = new("7-1-2"), QueryType = "q", DataBytes = RandomBytes(100, 11), Metadata = "m" };
            byte[] good = PacketConverter.PacketToBytes(packet);
            Assert.False(PacketConverter.BytesToPacket(good).IsErrorWhileReading);
            // receiver address length (offset 4) far beyond the packet, data length too big, and a cut packet; the CRC is fixed every time
            byte[] longAddress = (byte[])good.Clone(); longAddress[4] = 250;
            byte[] longData = (byte[])good.Clone();
            int dataLengthOffset = 4 + 1 + 5 + 1 + 5 + 1 + 1 + 4 + 1 + 8 + 8 + 2;
            BinaryPrimitives.WriteUInt16BigEndian(longData.AsSpan(dataLengthOffset), 60000);
            byte[] cut = good[..40];
            foreach (byte[] broken in new[] { WithCrc(longAddress), WithCrc(longData), WithCrc(cut), new byte[20] })
            {
                Packet parsed = PacketConverter.BytesToPacket(broken);
                Assert.True(parsed.IsErrorWhileReading);
            }
        }

        [Fact]
        public void MetadataRules()
        {
            Packet Parse(string? metadata) => PacketConverter.BytesToPacket(PacketConverter.PacketToBytes(new Packet { Transmitter = new("7-1-1"), Receiver = new("7-1-2"), QueryType = "q", DataBytes = [1], Metadata = metadata }));
            Assert.Equal("Wow WWW", Parse("Wow WWW".Replace(" ", "")) is { IsErrorWhileReading: false } p ? "Wow WWW" : null);
            Assert.Equal("WoW", Parse("WoW").Metadata);
            Assert.Null(Parse(null).Metadata);
            Assert.Null(Parse("").Metadata);
            Assert.True(Parse("bad\u0001char").IsErrorWhileReading);
        }

        // ---------- #13: More Stable Serial Packet ----------

        [Fact]
        public void MsspWorksOnStreamsWithoutTimeoutsAndSkipsTheRestOfATooLongLine()
        {
            byte[] good = [1, 2, 3];
            MemoryStream input = new();
            input.Write(Enumerable.Repeat((byte)'A', 17 * 1024 * 1024).ToArray());
            input.Write("\n"u8);
            input.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(good) + "\n"));
            input.Position = 0;
            List<byte[]> received = [];
            int malformed = 0, dropped = 0;
            ManualResetEventSlim done = new();
            MoreStableSerialPacket mssp = new(input); // a MemoryStream can't time out: the constructor used to throw
            mssp.OnBufferReceived((d, k) => { lock (received) received.Add(d); });
            mssp.MalformedPacketReceived += () => Interlocked.Increment(ref malformed);
            mssp.OnDataDropped += (reason, count) => { if (reason == L1DropBytesReasons.LineTooLong) Interlocked.Increment(ref dropped); };
            mssp.TextReaderReturnedNull += done.Set;
            Assert.True(done.Wait(TimeSpan.FromSeconds(30)));
            Thread.Sleep(100);
            lock (received) Assert.Equal(good, Assert.Single(received));
            Assert.Equal(0, malformed);
            Assert.True(dropped >= 1);
        }

        private sealed class DisposedStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new ObjectDisposedException("port");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }

        [Fact]
        public void MsspStopsOnAClosedStream()
        {
            int errors = 0;
            ManualResetEventSlim stopped = new();
            MoreStableSerialPacket mssp = new(new DisposedStream());
            mssp.InternalErrorHappened += (e, emitter) => Interlocked.Increment(ref errors);
            mssp.TextReaderReturnedNull += stopped.Set;
            Assert.True(stopped.Wait(TimeSpan.FromSeconds(5)) || Volatile.Read(ref errors) <= 1);
            Thread.Sleep(300);
            Assert.True(Volatile.Read(ref errors) <= 1, $"{errors} errors: the reader keeps spinning");
        }

        // ---------- #14: NLMNP ----------

        [Fact]
        public void NotificationIsRepeatedUntilAcknowledgedAndReportedOnce()
        {
            using FakeLink link = new();
            int notifications = 0;
            link.AToB = p => p.QueryType == "lm_notify" && Interlocked.Increment(ref notifications) <= 2 ? Fault.Drop : Fault.Deliver;
            int acks = 0;
            link.BToA = p => p.QueryType == "lm_ack_notify" && Interlocked.Increment(ref acks) == 1 ? Fault.Drop : Fault.Deliver;
            NLMNPClient sender = new(link.A) { ResendIntervalMs = 100, TimeoutMs = 10000 };
            NLMNPClient receiver = new(link.B);
            int reported = 0;
            receiver.OnNotificationReceived += (from, server, name) => Interlocked.Increment(ref reported);
            receiver.Listen();
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(15), () => sender.Notify(link.B.LocalModemAddress!, new Address("7-1-5"), "file.bin")));
            Thread.Sleep(300);
            Assert.Equal(1, reported);
            Assert.Throws<WrongCharacherException>(() => sender.Notify(link.B.LocalModemAddress!, new Address("7-1-5"), "two\nlines"));
            sender.Close();
            sender.Close(); // closing twice is allowed
            receiver.Close();
        }

        [Fact]
        public void BrokenNotificationIsNotAcknowledged()
        {
            using FakeLink link = new();
            int acks = 0;
            link.BToA = p => { if (p.QueryType == "lm_ack_notify") Interlocked.Increment(ref acks); return Fault.Deliver; };
            NLMNPClient receiver = new(link.B);
            int errors = 0;
            receiver.OnErrorReceived += e => Interlocked.Increment(ref errors);
            receiver.Listen();
            link.A.LowLevelTransmit(Encoding.UTF8.GetBytes("no address here"), link.B.LocalModemAddress!, "lm_notify", 4000000015);
            link.A.LowLevelTransmit(Encoding.UTF8.GetBytes("name@not an address"), link.B.LocalModemAddress!, "lm_notify", 4000000015);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref errors) == 2, TimeSpan.FromSeconds(5)));
            Assert.Equal(0, acks);
            receiver.Close();
        }

        // ---------- small things ----------

        [Fact]
        public void AddressIsNeverEqualToNull()
        {
            Assert.False(new Address().Equals((Address?)null));
            Assert.True(new Address("7-1").Equals((object)new Address("7-1")));
        }
    }
}
