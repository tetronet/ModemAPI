using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.IO.Pipes;
using System.Text;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// MSSP decodes lines for the modems' own handlers into a pooled buffer, but a subscriber of OnBufferReceived still gets an
    /// array of its own, that later lines don't overwrite.
    /// </summary>
    public class MsspPooledTests
    {
        private static (MoreStableSerialPacket reader, Stream writer) Pipe()
        {
            AnonymousPipeServerStream server = new(PipeDirection.Out);
            AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);
            return (new MoreStableSerialPacket(client), server);
        }

        private static byte[][] Messages(int count)
        {
            Random random = new(7);
            byte[][] messages = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                messages[i] = new byte[random.Next(1, 70_000)];
                random.NextBytes(messages[i]);
            }
            return messages;
        }

        private static void Write(Stream writer, IEnumerable<byte[]> messages)
        {
            foreach (byte[] message in messages)
            {
                writer.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(message) + "\n"));
            }
            writer.Flush();
        }

        [Fact]
        public void OwnHandlersGetEveryLineFromThePool()
        {
            var (reader, writer) = Pipe();
            byte[][] messages = Messages(40);
            ConcurrentQueue<byte[]> seen = new();
            reader.OnLineReceived(d => seen.Enqueue(d.ToArray()));
            Write(writer, messages);
            // unusual lines go the old way: whitespace inside base64 and a line of a wrong length (a FormatException, not a line)
            writer.Write(Encoding.ASCII.GetBytes("AQID BA==\n" + "AQI\n"));
            writer.Flush();
            Assert.True(SpinWait.SpinUntil(() => seen.Count >= messages.Length + 1, TimeSpan.FromSeconds(10)));
            Thread.Sleep(100);
            Assert.Equal([.. messages, [1, 2, 3, 4]], seen.ToArray());
            writer.Dispose();
        }

        [Fact]
        public void ArraySubscriberKeepsItsArrays()
        {
            var (reader, writer) = Pipe();
            byte[][] messages = Messages(40);
            ConcurrentQueue<byte[]> kept = new();
            ConcurrentQueue<byte[]> seen = new();
            reader.OnLineReceived(d => seen.Enqueue(d.ToArray()));
            reader.OnBufferReceived((d, k) => kept.Enqueue(d)); // keeps the array itself, not a copy
            Write(writer, messages);
            Assert.True(SpinWait.SpinUntil(() => kept.Count == messages.Length, TimeSpan.FromSeconds(10)));
            Thread.Sleep(100);
            // every kept array still holds its own line after all the later ones, and the own handlers got every line too
            Assert.Equal(messages, kept.ToArray());
            Assert.Equal(messages, seen.ToArray());
            writer.Dispose();
        }

        [Fact]
        public void SpanTransmitSendsTheSameLine()
        {
            var (reader, writer) = Pipe();
            MoreStableSerialPacket sender = new(writer);
            byte[] message = Messages(1)[0];
            ConcurrentQueue<byte[]> seen = new();
            reader.OnLineReceived(d => seen.Enqueue(d.ToArray()));
            byte[] buffer = new byte[message.Length + 10];
            message.CopyTo(buffer, 5);
            sender.TransmitBuffer(buffer.AsSpan(5, message.Length));
            buffer.AsSpan().Clear(); // the caller may reuse its buffer right after the call
            sender.TransmitBuffer(message);
            writer.Flush();
            Assert.True(SpinWait.SpinUntil(() => seen.Count == 2, TimeSpan.FromSeconds(10)));
            Assert.All(seen, line => Assert.Equal(message, line));
            writer.Dispose();
        }

        [Fact]
        public void ConnectionRequestIsParsedAsBefore()
        {
            List<byte> request = [.. CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixRequest, 3, .. "7-1"u8, 0];
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(request.ToArray()));
            byte[] valid = [.. request, .. crc];
            var parsed = PacketConverter.ParseLowLatencyConnectionRequest(valid);
            Assert.False(parsed.IsRequestDamaged);
            Assert.Equal("7-1", parsed.CurrentClientAddress.AddressValue);
            Assert.True(parsed.IsAddressMachine);

            Assert.True(PacketConverter.ParseLowLatencyConnectionRequest(valid[..16]).IsRequestDamaged); // too short
            byte[] otherPrefix = (byte[])valid.Clone();
            otherPrefix[0] ^= 1;
            Assert.True(PacketConverter.ParseLowLatencyConnectionRequest(otherPrefix).IsRequestDamaged);
            byte[] badCrc = (byte[])valid.Clone();
            badCrc[^1] ^= 1;
            Assert.True(PacketConverter.ParseLowLatencyConnectionRequest(badCrc).IsRequestDamaged);
            Assert.True(PacketConverter.ParseLowLatencyConnectionRequest(new byte[60_000]).IsRequestDamaged); // a packet
        }
    }
}
