using System.Text;
using System.Threading.Channels;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// The queued packets go over an MSSP line together: the same lines in the same order, whole lines in every write,
    /// fewer writes than packets, and a broken packet or a failed write doesn't stop the others.
    /// </summary>
    public class PacketBatchWriterTests
    {
        private static Packet PacketOf(int n, int dataLength, string? metadata = null) =>
            new(new Address("7-1-1"), new Address("7-1-2"), Enumerable.Range(0, dataLength).Select(i => (byte)(i + n)).ToArray(), true, metadata, "q", (uint)n, false, (ulong)n, 1, 9);

        private static Channel<Packet> Queue(IEnumerable<Packet> packets)
        {
            Channel<Packet> queue = Channel.CreateUnbounded<Packet>();
            foreach (Packet packet in packets)
            {
                queue.Writer.TryWrite(packet);
            }
            return queue;
        }

        private static List<Packet> ReadLines(byte[] written)
        {
            return Encoding.ASCII.GetString(written).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => PacketConverter.BytesToPacket(Convert.FromBase64String(line))).ToList();
        }

        [Fact]
        public void QueuedPacketsGoTogetherInOrder()
        {
            Random random = new(3);
            List<Packet> packets = [];
            for (int n = 0; n < 300; n++)
            {
                packets.Add(PacketOf(n, random.Next(4) switch { 0 => 0, 1 => random.Next(1, 200), 2 => random.Next(200, 9000), _ => random.Next(9000, 60000) }));
            }
            packets.Insert(150, PacketOf(1000, 65535, new string('m', 65535))); // bigger than the buffers at the start
            List<byte[]> writes = [];
            List<Exception> errors = [];
            PacketBatchWriter.WriteQueued(Queue(packets).Reader, lines => writes.Add(lines.ToArray()), errors.Add);

            Assert.Empty(errors);
            Assert.All(writes, w => Assert.Equal((byte)'\n', w[^1])); // only whole lines
            Assert.All(writes, w => Assert.True(w.Length <= PacketBatchWriter.BatchBytes || w.Count(b => b == '\n') == 1, "a write longer than the batch, that isn't one line"));
            Assert.True(writes.Count < packets.Count / 2, $"{writes.Count} writes for {packets.Count} packets");
            List<Packet> read = ReadLines([.. writes.SelectMany(w => w)]);
            Assert.Equal(packets.Count, read.Count);
            for (int i = 0; i < packets.Count; i++)
            {
                Assert.False(read[i].IsErrorWhileReading);
                Assert.Equal(packets[i].ConnectionID, read[i].ConnectionID);
                Assert.Equal(packets[i].DataBytes, read[i].DataBytes);
                Assert.Equal(packets[i].Metadata, read[i].Metadata);
            }
        }

        [Fact]
        public void BrokenPacketAndFailedWriteDontStopTheOthers()
        {
            Packet tooLongAddress = new(new Address(new string('1', 300)), new Address("7-1-2"), [1], true, null, "q", 99, false, 0, 1, 9);
            // big packets: every one is a write of its own, and the second write fails
            List<Packet> packets = [PacketOf(0, 50000), tooLongAddress, PacketOf(1, 50000), PacketOf(2, 50000), PacketOf(3, 50000)];
            List<byte[]> writes = [];
            List<Exception> errors = [];
            int calls = 0;
            PacketBatchWriter.WriteQueued(Queue(packets).Reader, lines =>
            {
                if (++calls == 2)
                {
                    throw new IOException("the line broke for a moment");
                }
                writes.Add(lines.ToArray());
            }, errors.Add);

            Assert.Equal(2, errors.Count);
            Assert.IsType<ArgumentOutOfRangeException>(errors[0]);
            Assert.IsType<IOException>(errors[1]);
            Assert.Equal([0u, 2u, 3u], ReadLines([.. writes.SelectMany(w => w)]).Select(p => p.ConnectionID));
        }

        [Fact]
        public void EmptyQueueWritesNothing()
        {
            int calls = 0;
            PacketBatchWriter.WriteQueued(Queue([]).Reader, lines => calls++, e => throw e);
            Assert.Equal(0, calls);
        }
    }
}
