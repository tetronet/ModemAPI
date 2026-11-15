using System.Buffers;
using System.Threading.Channels;

namespace ModemAPI
{
    /// <summary>
    /// Sends the packets, that are queued already, over an MSSP line with as few writes as possible: their lines are encoded
    /// one after another into one buffer and written together. A write to a socket or a port is a system call, which costs more
    /// than encoding a small packet, so one write per packet was the most expensive part of the path. The lines on the wire are
    /// the same; nothing waits for more packets, only the ones queued already go together.
    /// </summary>
    internal static class PacketBatchWriter
    {
        /// <summary>
        /// Lines are written once this many bytes are collected (and when the queue is empty). Not more than the line of one
        /// biggest packet (~80 KB): on a slow serial line a write holds the line, and a Modem-KeepAlive answer waits for it.
        /// </summary>
        internal const int BatchBytes = 64 * 1024;

        /// <param name="write">Writes whole lines with one write</param>
        /// <param name="onError">Gets the error of a packet, that can't be converted, or of a write (then every packet of that write is lost)</param>
        internal static void WriteQueued(ChannelReader<Packet> queue, LineDataHandler write, Action<Exception> onError)
        {
            byte[] batch = ArrayPool<byte>.Shared.Rent(BatchBytes);
            byte[] packetBytes = ArrayPool<byte>.Shared.Rent(64 * 1024);
            int used = 0;
            try
            {
                while (queue.TryRead(out Packet? packet))
                {
                    int size;
                    try
                    {
                        size = PacketConverter.EstimatePacketSize(packet);
                    }
                    catch (Exception e)
                    {
                        // a packet, that doesn't fit into the format, is reported and skipped, as before
                        onError(e);
                        continue;
                    }
                    int lineLength = MoreStableSerialPacket.LineLengthFor(size);
                    // a write is at most BatchBytes, or one line, that is longer by itself (the buffer may have grown for one)
                    if (used > 0 && used + lineLength > BatchBytes)
                    {
                        Flush(write, onError, batch, ref used);
                    }
                    EnsureLength(ref batch, lineLength);
                    EnsureLength(ref packetBytes, size);
                    PacketConverter.PacketToBytes(packet, packetBytes.AsSpan(0, size));
                    used += MoreStableSerialPacket.EncodeLine(packetBytes.AsSpan(0, size), batch.AsSpan(used));
                }
                Flush(write, onError, batch, ref used);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(batch);
                ArrayPool<byte>.Shared.Return(packetBytes);
            }
        }

        private static void Flush(LineDataHandler write, Action<Exception> onError, byte[] batch, ref int used)
        {
            if (used == 0)
            {
                return;
            }
            try
            {
                write(batch.AsSpan(0, used));
            }
            catch (Exception e)
            {
                onError(e);
            }
            used = 0;
        }

        /// <summary>
        /// Replaces an empty pooled buffer with a bigger one, if it is shorter than needed (a packet with near 64 KB of data and of metadata).
        /// </summary>
        private static void EnsureLength(ref byte[] buffer, int length)
        {
            if (buffer.Length < length)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = ArrayPool<byte>.Shared.Rent(length);
            }
        }
    }
}
