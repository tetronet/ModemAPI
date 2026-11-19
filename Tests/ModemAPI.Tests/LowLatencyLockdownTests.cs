using System.IO.Pipelines;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// MODEM LOCKDOWN for the LowLatency modems: when the other side stops reading the line, the sender of the modem stops on a
    /// write, but the reader thread must go on receiving.
    /// </summary>
    public class LowLatencyLockdownTests
    {
        /// <summary>
        /// A line: what the test writes with <see cref="Feeder"/> is read by the modem, and what the modem writes is thrown away,
        /// or waits while <see cref="Gate"/> is closed, as on a line, that the other side doesn't read.
        /// </summary>
        private sealed class Line : Stream
        {
            private readonly Pipe Inbound = new();
            private readonly Stream InboundReader;
            public readonly ManualResetEventSlim Gate = new(true);
            public readonly MoreStableSerialPacket Feeder;
            public int BlockedWrites;

            public Line()
            {
                InboundReader = Inbound.Reader.AsStream();
                Feeder = new MoreStableSerialPacket(Inbound.Writer.AsStream());
            }

            public override int Read(byte[] buffer, int offset, int count) => InboundReader.Read(buffer, offset, count);
            public override int Read(Span<byte> buffer) => InboundReader.Read(buffer);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => InboundReader.ReadAsync(buffer, cancellationToken);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => InboundReader.ReadAsync(buffer, offset, count, cancellationToken);

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (!Gate.IsSet)
                {
                    Interlocked.Increment(ref BlockedWrites);
                    Gate.Wait();
                }
            }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.Span);
                return ValueTask.CompletedTask;
            }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer.AsSpan(offset, count));
                return Task.CompletedTask;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private static byte[] PacketFor(Address receiver, ulong id)
        {
            return PacketConverter.PacketToBytes(new Packet(new Address("7-2"), receiver, [1, 2, 3], true, null, "q", 1, false, 0, id, 100));
        }

        /// <summary>
        /// Holds the sender of the modem on a write, that doesn't end: the line, that the other side doesn't read.
        /// </summary>
        private static void StopTheSender(Line line, LowLatencyPhysicalModem modem)
        {
            line.Gate.Reset();
            modem.LowLevelTransmit([9], new Address("7-2"), "q", 1, null);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref line.BlockedWrites) > 0, TimeSpan.FromSeconds(5)), "the sender didn't write");
        }

        [Fact]
        public void KeepAliveDoesntStopTheReaderBehindAStuckSender()
        {
            using Line line = new();
            LowLatencyPhysicalModem modem = new(line) { LocalModemAddress = new Address("7-1-1") };
            int received = 0;
            modem.AttachReceiveEventNoUnfragment((p, k) => Interlocked.Increment(ref received));
            try
            {
                StopTheSender(line, modem);
                // Modem-KeepAlive of the answering modem, then a packet: the answer to the first must not hold the second
                line.Feeder.TransmitBuffer(CIoCILLowLatShortDefaultPhrases.KeepAliveRequest);
                line.Feeder.TransmitBuffer(PacketFor(new Address("7-1-1"), 1));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref received) == 1, TimeSpan.FromSeconds(5)), "the reader stopped behind the answer to Modem-KeepAlive");
            }
            finally
            {
                line.Gate.Set();
            }
        }

        [Fact]
        public void ReaderDoesntWaitForAFullQueue()
        {
            using Line line = new();
            // the caller waits for space in the queue, but the reader thread must not
            LowLatencyPhysicalModem modem = new(line) { LocalModemAddress = new Address("7-1-1"), DropPacketsWhenOverloaded = false };
            int received = 0;
            // every packet is answered from the receive event, as SRTP answers with acknowledgements
            modem.AttachReceiveEventNoUnfragment((p, k) =>
            {
                Interlocked.Increment(ref received);
                modem.LowLevelTransmit([1], new Address("7-2"), "q", 1, null);
            });
            try
            {
                StopTheSender(line, modem);
                const int count = 1500; // more than the queue of 1024 packets takes
                for (int i = 0; i < count; i++)
                {
                    line.Feeder.TransmitBuffer(PacketFor(new Address("7-1-1"), (ulong)i));
                }
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref received) == count, TimeSpan.FromSeconds(10)), $"the reader stopped on the full queue after {received} packets");
                Assert.True(modem.LocallyDroppedPacketsCounter > 0);
            }
            finally
            {
                line.Gate.Set();
            }
        }
    }
}
