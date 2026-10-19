using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace ModemAPI
{
    /// <summary>
    /// Sends every buffer as one line of base64 terminated by '\n'. Works on raw bytes only: base64 is encoded and decoded
    /// straight to/from UTF-8, every line is written with a single call, and the stream is read in large blocks.
    /// </summary>
    public class MoreStableSerialPacket : ISerialPacket
    {
        private const int ReadBlockSize = 65536;
        /// <summary>
        /// A line longer than this can't be a packet (base64 of the biggest one is ~90 KB), so it gets dropped instead of growing the buffer forever.
        /// </summary>
        private const int MaxLineLength = 16 * 1024 * 1024;
        private Stream io;
        private readonly Lock _writeLock = new();
        private Action<byte[], Action> OnReceive = delegate { };
        public event Action<L1DropBytesReasons, int> OnDataDropped = delegate { };
        public event Action MalformedPacketReceived = delegate { };
        public event Action<Exception, ErrorEmitter> InternalErrorHappened = delegate { };
        public event Action TextReaderReturnedNull = delegate { };
        public Stream UnderlyingStream { get { return io; } }

        public MoreStableSerialPacket(Stream io)
        {
            this.io = io;
            io.ReadTimeout = Timeout.Infinite;
            Task.Factory.StartNew(ReaderLoop, TaskCreationOptions.LongRunning);
        }

        public void TransmitBuffer(byte[] buffer)
        {
            byte[] line = ArrayPool<byte>.Shared.Rent(Base64.GetMaxEncodedToUtf8Length(buffer.Length) + 1);
            try
            {
                // encoded outside of the lock, so several senders only wait for each other while writing
                Base64.EncodeToUtf8(buffer, line, out _, out int written);
                line[written] = 0x0A;
                lock (_writeLock)
                {
                    // one write per packet: the newline used to be a second write, which meant a second system call
                    io.Write(line, 0, written + 1);
                }
                ModemAPIDebugger.OutputDebugMessage($"sent {buffer.Length} bytes of data");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(line);
            }
        }

        public void OnBufferReceived(Action<byte[], Action> onReceive)
        {
            ModemAPIDebugger.OutputDebugMessage("Subscribed to the serial buffer");
            OnReceive += onReceive;
        }

        private void ReaderLoop()
        {
            byte[] buffer = new byte[ReadBlockSize];
            int start = 0; // unprocessed bytes are buffer[start..end)
            int end = 0;
            bool firstRead = true;
            bool skipLineFeed = false; // the last line ended with '\r', so a '\n' right after it belongs to the same terminator
            while (true)
            {
                if (end == buffer.Length)
                {
                    if (start > 0)
                    {
                        // move the unfinished line to the front to make room for the next block
                        Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                        end -= start;
                        start = 0;
                    }
                    else if (buffer.Length >= MaxLineLength)
                    {
                        OnDataDropped(L1DropBytesReasons.LineTooLong, end);
                        end = 0;
                    }
                    else
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }
                }
                int read;
                try
                {
                    read = io.Read(buffer, end, buffer.Length - end);
                }
                catch (TimeoutException te)
                {
                    InternalErrorHappened(te, ErrorEmitter.HardwareSerialPort);
                    continue;
                }
                catch (Exception e)
                {
                    InternalErrorHappened(e, ErrorEmitter.MoreStableSerialPacket);
                    // a broken stream keeps throwing, so don't spin on it
                    Thread.Sleep(10);
                    continue;
                }
                if (read == 0)
                {
                    // end of the stream: like StreamReader.ReadLine, an unterminated last line is still a line
                    if (end > start)
                    {
                        ProcessLine(buffer.AsSpan(start, end - start));
                    }
                    TextReaderReturnedNull();
                    break;
                }
                end += read;
                if (firstRead)
                {
                    firstRead = false;
                    // StreamReader used to skip a UTF-8 byte order mark at the start of the stream
                    if (buffer.AsSpan(start, end - start).StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                    {
                        start += 3;
                    }
                }
                while (start < end)
                {
                    Span<byte> pending = buffer.AsSpan(start, end - start);
                    if (skipLineFeed)
                    {
                        skipLineFeed = false;
                        if (pending[0] == (byte)'\n')
                        {
                            start++;
                            continue;
                        }
                    }
                    // '\n', '\r' and "\r\n" all end a line, the same as StreamReader.ReadLine
                    int lineEnd = pending.IndexOfAny((byte)'\n', (byte)'\r');
                    if (lineEnd < 0)
                    {
                        break;
                    }
                    ProcessLine(pending[..lineEnd]);
                    skipLineFeed = pending[lineEnd] == (byte)'\r';
                    start += lineEnd + 1;
                }
                if (start == end)
                {
                    start = 0;
                    end = 0;
                }
            }
        }

        private void ProcessLine(ReadOnlySpan<byte> line)
        {
            try
            {
                byte[] packet = DecodeLine(line);
                ModemAPIDebugger.OutputDebugMessage($"Received data MSSP: {line.Length} base64 bytes");
                OnReceive(packet, delegate () { throw new NotImplementedException(); });
            }
            catch (FormatException fe)
            {
                Task.Run(() => MalformedPacketReceived());
                InternalErrorHappened(fe, ErrorEmitter.Base64Converter);
            }
            catch (TimeoutException te)
            {
                InternalErrorHappened(te, ErrorEmitter.HardwareSerialPort);
            }
            catch (Exception e)
            {
                InternalErrorHappened(e, ErrorEmitter.MoreStableSerialPacket);
            }
        }

        private static byte[] DecodeLine(ReadOnlySpan<byte> line)
        {
            if (line.Length % 4 == 0)
            {
                int padding = line.Length == 0 ? 0 : line[^1] == (byte)'=' ? (line[^2] == (byte)'=' ? 2 : 1) : 0;
                byte[] result = new byte[line.Length / 4 * 3 - padding];
                if (Base64.DecodeFromUtf8(line, result, out _, out int written) == OperationStatus.Done && written == result.Length)
                {
                    return result;
                }
            }
            // whitespace, invalid characters and other unusual input give the same result (or FormatException) as before
            return Convert.FromBase64String(Encoding.UTF8.GetString(line));
        }
    }
}
