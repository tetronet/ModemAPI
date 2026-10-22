using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// LMDTP client and server: a request must end with the resource or with an exception, and quickly when the server
    /// reports a problem; a malformed or failed request must not break the server or leave its session behind.
    /// </summary>
    public class LmdtpTests
    {
        private const int SrtpTimeoutTicks = 2_000_000; // 200 ms
        private const int ClientTimeoutTicks = 100_000_000; // 10 s: errors must arrive much earlier than this
        private static readonly TimeSpan FastFailure = TimeSpan.FromSeconds(5);
        private static uint NextConnectionId = 1000;

        private sealed class Resources : IResourceProvider
        {
            public readonly Dictionary<string, byte[]> Data = [];
            public bool OfferXxHash3 = true;
            public LMDTPResponseFlags Flags = LMDTPResponseFlags.Success;
            public Func<string, byte[]?>? Sha512Override;
            public Func<string, Stream?>? StreamOverride;
            public Func<string, long>? SizeOverride;

            public Stream? GetResource(string resourceName) => StreamOverride != null ? StreamOverride(resourceName) : Data.TryGetValue(resourceName, out byte[]? d) ? new MemoryStream(d, false) : null;
            public long GetResourceSize(string resourceName) => SizeOverride != null ? SizeOverride(resourceName) : Data.TryGetValue(resourceName, out byte[]? d) ? d.Length : 0;
            public byte[] GetResourceSha512Hashsum(string resourceName) => Sha512Override != null ? Sha512Override(resourceName)! : SHA512.HashData(Data[resourceName]);
            public LMDTPResponseFlags GetResourceResponseFlags(string resourceName) => Data.ContainsKey(resourceName) ? Flags : LMDTPResponseFlags.LargeMessageNotFound;
            public byte[]? GetResourceHash(string resourceName, LMDTPHashAlgorithms algorithm) =>
                algorithm == LMDTPHashAlgorithms.XxHash3 && OfferXxHash3 ? LMDTPServer.ComputeResourceHash(new MemoryStream(Data[resourceName]), algorithm) : null;
        }

        private sealed class Setup : IDisposable
        {
            public readonly FakeLink Link = new();
            public readonly Resources Resources = new();
            public readonly LMDTPServer Server;
            public readonly List<Exception> ServerErrors = [];
            public int Processed;
            private readonly List<LMDTPClient> Clients = [];

            public Setup(int packetMaxLength = 1000)
            {
                Server = new LMDTPServer(Link.B, 1000, 100000, [], false, SrtpTimeoutTicks, 0) { PacketMaxLength = packetMaxLength, ResourceProvider = Resources };
                Server.ErrorOccured += e => { lock (ServerErrors) ServerErrors.Add(e); };
                Server.RequestSuccessfullyProcessed += (a, c, n) => Interlocked.Increment(ref Processed);
                Server.Start();
            }

            public LMDTPClient Client(uint? cid = null)
            {
                LMDTPClient client = new(Link.A, Link.B.LocalModemAddress!, cid ?? Interlocked.Increment(ref NextConnectionId), SrtpTimeoutTicks);
                lock (Clients) Clients.Add(client);
                return client;
            }

            public void Dispose()
            {
                lock (Clients) foreach (LMDTPClient client in Clients) client.Close();
                Server.Stop();
                Link.Dispose();
            }
        }

        private static byte[] RandomBytes(int length, int seed)
        {
            byte[] data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        /// <summary>A destination stream without seeking: Position and Length throw, like a network stream.</summary>
        private sealed class ForwardOnlyStream : Stream
        {
            public readonly MemoryStream Inner = new();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(999, true)]
        [InlineData(1000, true)]
        [InlineData(2001, true)] // the last packet carries 1 byte
        [InlineData(2001, false)]
        [InlineData(50_000, false)]
        public void DownloadsResourcesOfAnySize(int size, bool xxHash3)
        {
            using Setup setup = new();
            byte[] data = RandomBytes(size, size);
            setup.Resources.Data["r"] = data;
            setup.Resources.OfferXxHash3 = xxHash3;
            LMDTPClient client = setup.Client();
            byte[] received = [];
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(20), () => received = client.Request("r", 1_000_000, ClientTimeoutTicks)));
            Assert.Equal(data, received);
            Assert.Equal(size, client.TotalDownloadBytes);
            Assert.Equal(size, client.CurrentDownloadBytes);
        }

        [Fact]
        public void WritesToStreamsWithoutPosition()
        {
            using Setup setup = new();
            byte[] data = RandomBytes(5000, 1);
            setup.Resources.Data["r"] = data;
            ForwardOnlyStream destination = new();
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(20), () => setup.Client().Request(destination, "r", 1_000_000, ClientTimeoutTicks)));
            Assert.Equal(data, destination.Inner.ToArray());
        }

        [Fact]
        public void AppendsToANonEmptyStreamAndRewindsToTheDownloadedData()
        {
            using Setup setup = new();
            byte[] data = RandomBytes(3000, 2);
            setup.Resources.Data["r"] = data;
            MemoryStream destination = new();
            destination.Write([1, 2, 3, 4, 5]);
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(20), () => setup.Client().Request(destination, "r", 1_000_000, ClientTimeoutTicks)));
            Assert.Equal(5, destination.Position);
            Assert.Equal(data, destination.ToArray()[5..]);
        }

        [Fact]
        public void DownloadSurvivesALossyLink()
        {
            using Setup setup = new();
            SharedRandom random = new(3);
            Fault Chaos(Packet p)
            {
                if (FakeLink.IsLmdtpRequest(p)) return Fault.Deliver;
                double roll = random.NextDouble();
                return roll < 0.1 ? Fault.Drop : roll < 0.15 ? Fault.Duplicate : roll < 0.2 ? Fault.Swap : Fault.Deliver;
            }
            setup.Link.AToB = Chaos;
            setup.Link.BToA = Chaos;
            byte[] data = RandomBytes(200_000, 3);
            setup.Resources.Data["r"] = data;
            byte[] received = [];
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(60), () => received = setup.Client().Request("r", 1_000_000, ClientTimeoutTicks)));
            Assert.Equal(data, received);
        }

        private static void AssertFailsFast<T>(Action request) where T : Exception
        {
            Stopwatch sw = Stopwatch.StartNew();
            Exception? error = Guard.RunWithin(TimeSpan.FromSeconds(20), request);
            Assert.IsType<T>(error);
            Assert.True(sw.Elapsed < FastFailure, $"the request failed only after {sw.Elapsed.TotalSeconds:F1} s");
        }

        [Fact]
        public void MissingResourceFailsFast()
        {
            using Setup setup = new();
            AssertFailsFast<FileNotFoundException>(() => setup.Client().Request("missing", 1_000_000, ClientTimeoutTicks));
        }

        [Theory]
        [InlineData(LMDTPResponseFlags.NotYourLargeMessage, typeof(UnauthorizedAccessException))]
        [InlineData(LMDTPResponseFlags.ServerHardDriveFailed, typeof(IOException))]
        [InlineData(LMDTPResponseFlags.ServerSideError, typeof(ConnectionFailedException))]
        public void ErrorFlagsOfTheProviderFailFast(LMDTPResponseFlags flags, Type expected)
        {
            using Setup setup = new();
            setup.Resources.Data["r"] = RandomBytes(5000, 4);
            setup.Resources.Flags = flags;
            Stopwatch sw = Stopwatch.StartNew();
            Exception? error = Guard.RunWithin(TimeSpan.FromSeconds(20), () => setup.Client().Request("r", 1_000_000, ClientTimeoutTicks));
            Assert.IsType(expected, error);
            Assert.True(sw.Elapsed < FastFailure);
        }

        [Fact]
        public void TooBigResourceFailsFast()
        {
            using Setup setup = new();
            setup.Resources.Data["r"] = RandomBytes(5000, 5);
            AssertFailsFast<LargeMessageTooBigException>(() => setup.Client().Request("r", 4999, ClientTimeoutTicks));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HashMismatchFailsFast(bool xxHash3)
        {
            using Setup setup = new();
            setup.Resources.Data["r"] = RandomBytes(5000, 6);
            setup.Resources.OfferXxHash3 = false;
            if (xxHash3)
            {
                // a provider, that gives a wrong XxHash3
                setup.Resources.Data["r"] = RandomBytes(5000, 6);
                setup.Server.ResourceProvider = new WrongXxHash3(setup.Resources);
            }
            else
            {
                setup.Resources.Sha512Override = name => new byte[64];
            }
            AssertFailsFast<InvalidDataException>(() => setup.Client().Request("r", 1_000_000, ClientTimeoutTicks));
        }

        private sealed class WrongXxHash3(IResourceProvider inner) : IResourceProvider
        {
            public Stream? GetResource(string resourceName) => inner.GetResource(resourceName);
            public long GetResourceSize(string resourceName) => inner.GetResourceSize(resourceName);
            public byte[] GetResourceSha512Hashsum(string resourceName) => inner.GetResourceSha512Hashsum(resourceName);
            public LMDTPResponseFlags GetResourceResponseFlags(string resourceName) => inner.GetResourceResponseFlags(resourceName);
            public byte[]? GetResourceHash(string resourceName, LMDTPHashAlgorithms algorithm) => algorithm == LMDTPHashAlgorithms.XxHash3 ? new byte[8] : null;
        }

        [Fact]
        public void ProviderExceptionFailsFastAndTheServerKeepsWorking()
        {
            using Setup setup = new();
            byte[] data = RandomBytes(3000, 7);
            setup.Resources.Data["r"] = data;
            setup.Resources.StreamOverride = name => throw new IOException("disk on fire");
            uint cid = Interlocked.Increment(ref NextConnectionId);
            AssertFailsFast<ConnectionFailedException>(() => setup.Client(cid).Request("r", 1_000_000, ClientTimeoutTicks));
            // the session got cleaned up: the same client address and ConnectionID can ask again
            setup.Resources.StreamOverride = null;
            byte[] received = [];
            SpinWait.SpinUntil(() => setup.Link.B.SubscriberCount == 1, TimeSpan.FromSeconds(5));
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(20), () => received = setup.Client(cid).Request("r", 1_000_000, ClientTimeoutTicks)));
            Assert.Equal(data, received);
        }

        [Fact]
        public void ResourceStreamShorterThanItsSizeIsReportedAndCleanedUp()
        {
            using Setup setup = new();
            setup.Resources.Data["r"] = RandomBytes(3000, 8);
            setup.Resources.SizeOverride = name => 5000;
            uint cid = Interlocked.Increment(ref NextConnectionId);
            Exception? error = Guard.RunWithin(TimeSpan.FromSeconds(20), () => setup.Client(cid).Request("r", 1_000_000, 20_000_000));
            Assert.IsType<TimeoutException>(error); // the header was sent already, the client can only stop waiting
            lock (setup.ServerErrors) Assert.Contains(setup.ServerErrors, e => e is EndOfStreamException);
            Assert.True(SpinWait.SpinUntil(() => setup.Link.B.SubscriberCount == 1, TimeSpan.FromSeconds(10)), "the session wasn't cleaned up");
        }

        // a server that breaks the protocol: answers any request with whatever the test wants

        private static void RawServer(FakeLink link, Action<SRTPClient> respond)
        {
            link.B.AttachReceiveEventNoUnfragment((p, k) =>
            {
                if (!FakeLink.IsLmdtpRequest(p)) return;
                Task.Run(() =>
                {
                    SRTPClient session = new(link.B, p.Transmitter, "lm_tunnel", p.ConnectionID, SrtpTimeoutTicks);
                    respond(session);
                });
            });
        }

        private static byte[] Header(ushort prefix, LMDTPResponseFlags flags, long length, byte[]? hash = null)
        {
            byte[] header = new byte[76];
            BinaryPrimitives.WriteUInt16BigEndian(header, prefix);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)flags);
            hash?.CopyTo(header, 4);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(68), length);
            return header;
        }

        public static IEnumerable<object[]> BrokenResponses()
        {
            byte[] data = RandomBytes(100, 9);
            byte[] sha = SHA512.HashData(data);
            yield return new object[] { "short header", new[] { new byte[40] } };
            yield return new object[] { "wrong prefix", new[] { Header(0x1234, 0, 100, sha), data } };
            yield return new object[] { "negative length", new[] { Header(LMDTPClient.LMDTP_RESPONSE_PREFIX, 0, -5, sha) } };
            yield return new object[] { "longer than allowed", new[] { Header(LMDTPClient.LMDTP_RESPONSE_PREFIX, 0, 2_000_000, sha) } };
            yield return new object[] { "more data than announced", new[] { Header(LMDTPClient.LMDTP_RESPONSE_PREFIX, 0, 50, sha), data } };
        }

        [Theory]
        [MemberData(nameof(BrokenResponses))]
        public void ProtocolViolationsFailFast(string name, byte[][] messages)
        {
            using FakeLink link = new();
            RawServer(link, session => { foreach (byte[] m in messages) session.Transmit(m); });
            LMDTPClient client = new(link.A, link.B.LocalModemAddress!, 1000, SrtpTimeoutTicks);
            Stopwatch sw = Stopwatch.StartNew();
            Exception? error = Guard.RunWithin(TimeSpan.FromSeconds(20), () => client.Request("r", 1_000_000, ClientTimeoutTicks));
            Assert.True(error is InvalidDataException or IndexOutOfRangeException or LargeMessageTooBigException, $"{name}: {error}");
            Assert.True(sw.Elapsed < FastFailure, $"{name}: failed only after {sw.Elapsed.TotalSeconds:F1} s");
        }

        // malformed requests to the real server

        private static byte[] Request(string name, long max, byte type, int? nameLengthField = null)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] request = new byte[2 + 2 + nameBytes.Length + 8 + 1];
            BinaryPrimitives.WriteUInt16BigEndian(request, LMDTPClient.LMDTP_REQUEST_PREFIX);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)(nameLengthField ?? nameBytes.Length));
            nameBytes.CopyTo(request, 4);
            BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(4 + nameBytes.Length), max);
            request[^1] = type;
            return request;
        }

        public static IEnumerable<object[]> MalformedRequests()
        {
            yield return new object[] { "only the prefix", new byte[] { 0xFF, 0x40 } };
            yield return new object[] { "truncated", Request("r", 1000, 0x15)[..10] };
            yield return new object[] { "name longer than the request", Request("r", 1000, 0x15, nameLengthField: 5000) };
            byte[] badUtf8 = Request("rr", 1000, 0x15);
            badUtf8[4] = 0xFF;
            yield return new object[] { "name is not UTF-8", badUtf8 };
        }

        [Theory]
        [MemberData(nameof(MalformedRequests))]
        public void MalformedRequestsAreRejectedAndCleanedUp(string name, byte[] request)
        {
            using Setup setup = new();
            byte[] data = RandomBytes(3000, 10);
            setup.Resources.Data["r"] = data;
            uint cid = Interlocked.Increment(ref NextConnectionId);
            setup.Link.A.LowLevelTransmit(request, setup.Link.B.LocalModemAddress!, "lm_tunnel", cid);
            Assert.True(SpinWait.SpinUntil(() => { lock (setup.ServerErrors) return setup.ServerErrors.Any(e => e is InvalidDataException); }, TimeSpan.FromSeconds(5)), name);
            Assert.True(SpinWait.SpinUntil(() => setup.Link.B.SubscriberCount == 1, TimeSpan.FromSeconds(5)), $"{name}: the session wasn't cleaned up");
            // the same address and ConnectionID still work
            byte[] received = [];
            Assert.Null(Guard.RunWithin(TimeSpan.FromSeconds(20), () => received = setup.Client(cid).Request("r", 1_000_000, ClientTimeoutTicks)));
            Assert.Equal(data, received);
        }

        [Fact]
        public void UnknownRequestTypeGetsAServerSideError()
        {
            using Setup setup = new();
            setup.Resources.Data["r"] = RandomBytes(100, 11);
            uint cid = Interlocked.Increment(ref NextConnectionId);
            SRTPClient session = new(setup.Link.A, setup.Link.B.LocalModemAddress!, "lm_tunnel", cid, SrtpTimeoutTicks);
            byte[]? header = null;
            session.OnMessageReceived = (c, d) => header ??= d;
            setup.Link.A.LowLevelTransmit(Request("r", 1000, 0x77), setup.Link.B.LocalModemAddress!, "lm_tunnel", cid);
            Assert.True(SpinWait.SpinUntil(() => header != null, TimeSpan.FromSeconds(5)));
            Assert.Equal((ushort)LMDTPResponseFlags.ServerSideError, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref setup.Processed) == 0 && setup.Link.B.SubscriberCount == 1, TimeSpan.FromSeconds(5)));
            session.Close();
        }

        // configuration checks

        [Fact]
        public void ServerChecksItsConfigurationOnStart()
        {
            using FakeLink link = new();
            Assert.Throws<InvalidOperationException>(() => new LMDTPServer(link.B, 1, 10, [], false, SrtpTimeoutTicks, 0).Start());
            Resources resources = new();
            Assert.Throws<InvalidOperationException>(() => new LMDTPServer(link.B, 1, 10, [], false, SrtpTimeoutTicks, 0) { ResourceProvider = resources, PacketMaxLength = 0 }.Start());
            Assert.Throws<InvalidOperationException>(() => new LMDTPServer(link.B, 1, 10, [], false, SrtpTimeoutTicks, 0) { ResourceProvider = resources, PacketMaxLength = SRTPClient.MaxMessageLength + 1 }.Start());
            Assert.Throws<InvalidOperationException>(() => new LMDTPServer(link.B, 10, 1, [], false, SrtpTimeoutTicks, 0) { ResourceProvider = resources }.Start());
            Assert.Throws<InvalidOperationException>(() => new LMDTPServer(link.B, 1, 10, [], false, 0, 0) { ResourceProvider = resources }.Start());
            Assert.Throws<InvalidOperationException>(() => new LMDTPServer(link.B, 1, 10, [], false, SrtpTimeoutTicks, -1) { ResourceProvider = resources }.Start());
        }

        [Fact]
        public void ClientChecksItsArgumentsBeforeSending()
        {
            using FakeLink link = new();
            LMDTPClient client = new(link.A, link.B.LocalModemAddress!, 1000, SrtpTimeoutTicks);
            Assert.Throws<ArgumentException>(() => client.Request(new MemoryStream(new byte[10], false), "r", 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => client.Request(new MemoryStream(), "r", -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => client.Request(new MemoryStream(), "r", 100, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => client.Request(new MemoryStream(), "r", 100, 1000, (LMDTPRequestType)0x77));
            Assert.Throws<ArgumentOutOfRangeException>(() => client.Request(new MemoryStream(), new string('x', 70000), 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => client.SupportedHashAlgorithms = (LMDTPHashAlgorithms)0x80);
        }
    }
}
