using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ModemAPI
{
    /// <summary>
    /// Represents a Large Message Direct Tunnel Protocol (LMDTP) client.
    /// One client makes one request at a time: <see cref="Request(Stream, string, long, int, LMDTPRequestType)"/> must not be called
    /// from several threads at once. Requests one after another are fine, each of them gets its own SRTP session.
    /// </summary>
    public class LMDTPClient
    {
        public const ushort LMDTP_REQUEST_PREFIX = 0xFF40;
        public const ushort LMDTP_RESPONSE_PREFIX = 0x72D3;
        /// <summary>
        /// Length of the response header: prefix (2), flags (2), hash field (64), resource length (8).
        /// </summary>
        private const int ResponseHeaderLength = 76;
        // the SRTP session of the last request: the server starts numbering from 0 for every request, so every request needs a new one
        private SRTPClient? UnderlyingClient;
        private readonly int SrtpTimeoutTicks;
        // held while a packet of the response is processed, so nothing is written to the destination after Request returned
        private readonly Lock ResponseLock = new();
        private IModem UnderlyingModem;
        private Address Destination;
        private uint ConnectionID;
        public event Action<Exception> ErrorOccured = delegate { };
        private volatile bool IsReceivingData = false;
        private bool IsFirstPacket = true;
        private long TimestampLastPacket = 0;
        private byte[] ExpectedHash = new byte[64];
        private LMDTPHashAlgorithms ResponseHashAlgorithm = LMDTPHashAlgorithms.Sha512;
        private long CurrentDownloadBytesInternal = 0;
        private long TotalDownloadBytesInternal = 0;
        private long MaxDownloadBytesInternal = 0;
        private long StartPosition = 0;
        private volatile Exception? RequestFailure = null;
        private bool IsClosed = false;
        private Stream? CurrentDestinationStream = null;
        private BackgroundHasher? Hasher = null;
        private readonly ManualResetEventSlim ReceivingFinished = new(false);
        private LMDTPHashAlgorithms SupportedHashAlgorithmsInternal = LMDTPHashAlgorithms.Sha512 | LMDTPHashAlgorithms.XxHash3;
        public long CurrentDownloadBytes { get { return CurrentDownloadBytesInternal; } }
        public long TotalDownloadBytes { get { return TotalDownloadBytesInternal; } }
        /// <summary>
        /// Hash algorithms this client offers to the server, the server picks the fastest one it can provide.
        /// SHA-512 is always accepted, because servers that don't know about this choice always send it.
        /// Both algorithms only detect accidental corruption, see <see cref="LMDTPHashAlgorithms"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value contains bits, that are not a known algorithm</exception>
        public LMDTPHashAlgorithms SupportedHashAlgorithms
        {
            get { return SupportedHashAlgorithmsInternal; }
            set
            {
                if ((value & ~(LMDTPHashAlgorithms.Sha512 | LMDTPHashAlgorithms.XxHash3)) != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "only LMDTPHashAlgorithms.Sha512 and LMDTPHashAlgorithms.XxHash3 are supported");
                }
                SupportedHashAlgorithmsInternal = value;
            }
        }
        public LMDTPClient(IModem baseModem, Address dest, uint cid, int srtpTimeoutTicks)
        {
            UnderlyingModem = baseModem;
            Destination = dest;
            ConnectionID = cid;
            SrtpTimeoutTicks = srtpTimeoutTicks;
        }
        private SRTPClient OpenSession()
        {
            SRTPClient session = new(UnderlyingModem, Destination, "lm_tunnel", ConnectionID, SrtpTimeoutTicks);
            session.OnError += delegate (int code, string desc)
            {
                ErrorOccured(new Exception($"SRTP ERRNO {code}: {desc}"));
            };
            session.OnClose += delegate (SRTPClient sender)
            {
                // nothing more can arrive, so a request in progress can't complete anymore
                if (IsReceivingData && ReferenceEquals(sender, UnderlyingClient))
                {
                    Fail(new OperationCanceledException("The SRTP session of this LMDTP client was closed while receiving the response."));
                }
            };
            session.OnMessageReceived = SRTPDataEvent;
            return session;
        }
        /// <summary>
        /// Sends a request to obtain remote symbols.
        /// </summary>
        /// <param name="remoteResourceName">Remote resource name to obtain</param>
        /// <param name="maxDownloadBytes">How much data is OK to be downloaded</param>
        /// <param name="ticksTimeout">How much ticks this request can wait without packets before throwing <see cref="TimeoutException"/></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public byte[] Request(string remoteResourceName, long maxDownloadBytes, int ticksTimeout = 50000000)
        {
            // using the stream variant based on the MemoryStream
            MemoryStream ms = new();
            Request(ms, remoteResourceName, maxDownloadBytes, ticksTimeout);
            byte[] temp = ms.ToArray();
            ms.Dispose();
            return temp;
        }
        /// <summary>
        /// Sends a request to obtain remote symbols and writes all obtained symbols to a presented stream.
        /// Returns only after the whole resource arrived and its hash matched; any error ends the request with an exception
        /// (the same exception is also passed to <see cref="ErrorOccured"/>).
        /// </summary>
        /// <param name="destination">Writable stream. It doesn't have to support seeking: the data is written in order, starting at its
        /// current position. If it supports seeking, its position is moved back to where the data starts after a successful request.
        /// After a failed request it may contain a part of the resource.</param>
        /// <param name="remoteResourceName">Remote resource name to obtain, not longer than 65535 bytes in UTF-8</param>
        /// <param name="maxDownloadBytes">Limit how much you want to server to send you (0 or more), if the resourse is longer, the server will return an error and the client will throw an exception</param>
        /// <param name="ticksTimeout">How much ticks this request can wait without packets before throwing <see cref="TimeoutException"/> (more than 0)</param>
        /// <param name="rqType">Request type</param>
        /// <exception cref="ArgumentNullException">destination or remoteResourceName is null</exception>
        /// <exception cref="ArgumentException">destination isn't writable</exception>
        /// <exception cref="ArgumentOutOfRangeException">An argument is out of its range</exception>
        /// <exception cref="TimeoutException">Nothing arrived for ticksTimeout</exception>
        /// <exception cref="OperationCanceledException">This client or its SRTP session was closed</exception>
        /// <exception cref="InvalidDataException">The response doesn't follow the protocol, or the hash didn't match</exception>
        /// <exception cref="FileNotFoundException">The server doesn't have this resource</exception>
        /// <exception cref="LargeMessageTooBigException">The resource is longer than maxDownloadBytes</exception>
        /// <exception cref="ConnectionFailedException">The server failed to process the request</exception>
        /// <exception cref="UnauthorizedAccessException">The resource is not for this client</exception>
        /// <exception cref="IOException">The server's drive failed, or writing to destination failed</exception>
        public void Request(Stream destination, string remoteResourceName, long maxDownloadBytes, int ticksTimeout = 50000000, LMDTPRequestType rqType = LMDTPRequestType.Obtain)
        {
            // format:
            // 2 bytes prefix
            // 2 bytes length of the remote resource name
            // 0-65535 bytes remote resource name
            // 8 bytes maximum amount of bytes, that we could download
            // 1 byte request type
            // 1 byte supported hash algorithms (older servers ignore it and answer with SHA-512)
            // 4 bytes request ID, new for every request, so the server can tell a repeated copy of a request from a new one
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(remoteResourceName);
            if (!destination.CanWrite)
            {
                throw new ArgumentException("the destination stream must be writable", nameof(destination));
            }
            ArgumentOutOfRangeException.ThrowIfNegative(maxDownloadBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksTimeout);
            if (!Enum.IsDefined(rqType))
            {
                throw new ArgumentOutOfRangeException(nameof(rqType), "unknown request type");
            }
            try
            {
                if (IsClosed)
                {
                    throw new OperationCanceledException("Large Message Direct Tunnel Protocol client was closed by the User.");
                }
                CurrentDestinationStream = destination;
                int resourceNameByteLength = Encoding.UTF8.GetByteCount(remoteResourceName);
                if (resourceNameByteLength > 65535)
                {
                    throw new ArgumentOutOfRangeException(nameof(remoteResourceName), "the length of resource name after converting it into the UTF-8 must not exceed 65535 bytes");
                }
                Span<byte> request = new byte[2 + 2 + resourceNameByteLength + 8 + 1 + 1 + 4];
                BinaryPrimitives.WriteUInt16BigEndian(request, LMDTP_REQUEST_PREFIX); // protocol prefix
                BinaryPrimitives.WriteUInt16BigEndian(request[2..], (ushort)resourceNameByteLength); // length of the resource name
                Encoding.UTF8.GetBytes(remoteResourceName, request[4..]);
                int afterName = 4 + resourceNameByteLength;
                BinaryPrimitives.WriteInt64BigEndian(request[afterName..], maxDownloadBytes);
                request[afterName + 8] = (byte)rqType;
                request[afterName + 9] = (byte)(SupportedHashAlgorithms | LMDTPHashAlgorithms.Sha512);
                BinaryPrimitives.WriteUInt32BigEndian(request[(afterName + 10)..], (uint)Random.Shared.NextInt64(1, uint.MaxValue));
                RequestFailure = null;
                MaxDownloadBytesInternal = maxDownloadBytes;
                StartPosition = destination.CanSeek ? destination.Position : 0;
                CurrentDownloadBytesInternal = 0;
                TotalDownloadBytesInternal = 0;
                IsFirstPacket = true;
                Hasher?.Dispose();
                Hasher = null; // created once the response tells which hash the server used
                ReceivingFinished.Reset();
                // the session of the previous request stays open after a success, so it can acknowledge late retransmissions;
                // it must be gone before the server starts the new response, or it would acknowledge it as already received
                UnderlyingClient?.Close();
                UnderlyingClient = OpenSession();
                IsReceivingData = true;
                UnderlyingModem.LowLevelTransmit(request.ToArray(), Destination, "lm_tunnel", ConnectionID);
                TimestampLastPacket = Stopwatch.GetTimestamp();
                while (IsReceivingData)
                {
                    ThrowIfFailed();
                    if (Stopwatch.GetElapsedTime(Volatile.Read(ref TimestampLastPacket)).Ticks > ticksTimeout)
                    {
                        throw new TimeoutException("LMDTP Obtain Request timed out...");
                    }
                    // returns as soon as the download completes or fails, the timeout is only for checking ticksTimeout
                    ReceivingFinished.Wait(100);
                }
                ThrowIfFailed();
            }
            catch
            {
                // a failed response can't be continued, and its session must not take the packets of the next one
                // (the request is marked as over first, so closing the session doesn't report a second error)
                IsReceivingData = false;
                UnderlyingClient?.Close();
                throw;
            }
            finally
            {
                IsReceivingData = false;
                // waits for a packet, that is being processed right now, so the destination isn't touched after returning
                lock (ResponseLock)
                {
                    IsFirstPacket = true;
                    Hasher?.Dispose();
                    Hasher = null;
                }
            }
        }
        /// <summary>
        /// Closes this instance of LMDTP Client and removes it's resources.
        /// </summary>
        public void Close()
        {
            IsClosed = true;
            UnderlyingClient?.Close();
        }
        private void ThrowIfFailed()
        {
            Exception? failure = RequestFailure;
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        /// <summary>
        /// Ends the request in progress with an error: Request throws it, and <see cref="ErrorOccured"/> gets it too.
        /// </summary>
        private void Fail(Exception error)
        {
            if (RequestFailure == null)
            {
                RequestFailure = error;
                ErrorOccured(error);
            }
            ReceivingFinished.Set();
        }
        void SRTPDataEvent(SRTPClient sender, byte[] data)
        {
            lock (ResponseLock)
            {
                if (!IsReceivingData || RequestFailure != null || !ReferenceEquals(sender, UnderlyingClient))
                {
                    // late data of a request, that is already over
                    return;
                }
                try
                {
                    if (CurrentDestinationStream == null)
                    {
                        throw new InvalidOperationException("Stream for the destination was null");
                    }
                    if (IsFirstPacket)
                    {
                        ReadResponseHeader(data);
                    }
                    else
                    {
                        ReceiveData(data);
                    }
                    Volatile.Write(ref TimestampLastPacket, Stopwatch.GetTimestamp());
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
        }
        private void ReadResponseHeader(byte[] data)
        {
            ReadOnlySpan<byte> bytes = data;
            if (bytes.Length < ResponseHeaderLength)
            {
                Fail(new IndexOutOfRangeException($"The response contains less than {ResponseHeaderLength} bytes"));
                return;
            }
            // check the prefix
            if (BinaryPrimitives.ReadUInt16BigEndian(bytes) != LMDTP_RESPONSE_PREFIX)
            {
                Fail(new InvalidDataException("Remote server sent an invalid response: response prefix doesn't macth with the spec"));
                return;
            }
            // read all flags
            ushort sFlags = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
            if ((sFlags & (ushort)LMDTPResponseFlags.LargeMessageNotFound) != 0) // 0x0001
            {
                Fail(new FileNotFoundException("remote large message file was not found"));
                return;
            }
            if ((sFlags & (ushort)LMDTPResponseFlags.LargeMessageExceededMTU) != 0) // 0x0002
            {
                Fail(new LargeMessageTooBigException("requested large message exceeded length, specified in the response"));
                return;
            }
            if ((sFlags & (ushort)LMDTPResponseFlags.ServerSideError) != 0) // 0x0004
            {
                Fail(new ConnectionFailedException("lmdtp server failed for some reason"));
                return;
            }
            if ((sFlags & (ushort)LMDTPResponseFlags.NotYourLargeMessage) != 0) // 0x0008
            {
                Fail(new UnauthorizedAccessException("this large message is not for you to download"));
                return;
            }
            if ((sFlags & (ushort)LMDTPResponseFlags.ServerHardDriveFailed) != 0) // 0x0010
            {
                Fail(new IOException("server's hard drive is physically or logically failed"));
                return;
            }
            long dataLength = BinaryPrimitives.ReadInt64BigEndian(bytes[68..]);
            if (dataLength < 0)
            {
                Fail(new InvalidDataException($"Remote server sent an invalid response: the resource length is negative ({dataLength})"));
                return;
            }
            if (dataLength > MaxDownloadBytesInternal)
            {
                Fail(new LargeMessageTooBigException($"the server announced {dataLength} bytes, which is more than {MaxDownloadBytesInternal} bytes this request agreed to download"));
                return;
            }
            // read the hash: XxHash3 takes the first 8 bytes of the field, SHA-512 all 64
            if ((sFlags & (ushort)LMDTPResponseFlags.HashIsXxHash3) != 0)
            {
                ResponseHashAlgorithm = LMDTPHashAlgorithms.XxHash3;
                ExpectedHash = bytes.Slice(4, 8).ToArray();
            }
            else
            {
                ResponseHashAlgorithm = LMDTPHashAlgorithms.Sha512;
                ExpectedHash = bytes.Slice(4, 64).ToArray();
            }
            TotalDownloadBytesInternal = dataLength;
            Hasher?.Dispose();
            Hasher = new BackgroundHasher(ResponseHashAlgorithm);
            IsFirstPacket = false;
            // data right after the header belongs to the resource; for an empty resource this also completes the request
            ReceiveData(bytes[ResponseHeaderLength..].ToArray());
        }
        private void ReceiveData(byte[] data)
        {
            Stream destination = CurrentDestinationStream ?? throw new InvalidOperationException("Stream for the destination was null");
            BackgroundHasher hasher = Hasher ?? throw new InvalidOperationException("data arrived while no request is in progress");
            // the received length is counted here instead of reading destination.Position, which not every stream supports
            if (data.Length > TotalDownloadBytesInternal - CurrentDownloadBytesInternal)
            {
                Fail(new InvalidDataException($"Remote server sent more than the {TotalDownloadBytesInternal} bytes it announced"));
                return;
            }
            if (data.Length > 0)
            {
                destination.Write(data);
                // SRTP gives every packet its own array, so it can be handed over without copying
                hasher.Append(data);
                CurrentDownloadBytesInternal += data.Length;
            }
            if (CurrentDownloadBytesInternal == TotalDownloadBytesInternal)
            {
                if (!hasher.Finish().SequenceEqual(ExpectedHash))
                {
                    Fail(new InvalidDataException($"{(ResponseHashAlgorithm == LMDTPHashAlgorithms.XxHash3 ? "XxHash3" : "SHA-512")} is mismatched. Received data is corrupted and cannot be corrected."));
                    return;
                }
                if (destination.CanSeek)
                {
                    destination.Position = StartPosition;
                }
                IsFirstPacket = true;
                IsReceivingData = false;
                ReceivingFinished.Set();
            }
        }
        /// <summary>
        /// Computes the hash on its own thread, so hashing (SHA-512 does about 6 Gbit/s on one core) runs in parallel with receiving
        /// instead of holding up the SRTP delivery thread. The queue is bounded: if hashing falls behind, <see cref="Append"/>
        /// blocks, and the sender gets slowed down by SRTP flow control instead of the memory filling up.
        /// </summary>
        private sealed class BackgroundHasher : IDisposable
        {
            private readonly Channel<byte[]> Queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            private readonly IncrementalHash? Sha512;
            private readonly XxHash3? XxHash3;
            private readonly Task Worker;

            public BackgroundHasher(LMDTPHashAlgorithms algorithm)
            {
                if (algorithm == LMDTPHashAlgorithms.XxHash3)
                {
                    XxHash3 = new XxHash3();
                }
                else
                {
                    Sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                }
                Worker = Task.Run(Run);
            }

            private async Task Run()
            {
                await foreach (byte[] chunk in Queue.Reader.ReadAllAsync())
                {
                    if (XxHash3 != null)
                    {
                        XxHash3.Append(chunk);
                    }
                    else
                    {
                        Sha512!.AppendData(chunk);
                    }
                }
            }

            /// <summary>
            /// Queues data for hashing. The array must not be changed afterwards.
            /// </summary>
            public void Append(byte[] chunk)
            {
                if (!Queue.Writer.TryWrite(chunk))
                {
                    Queue.Writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult();
                }
            }

            /// <summary>
            /// Waits for everything queued to be hashed and returns the hash.
            /// </summary>
            public byte[] Finish()
            {
                Queue.Writer.TryComplete();
                Worker.GetAwaiter().GetResult();
                return XxHash3 != null ? XxHash3.GetHashAndReset() : Sha512!.GetHashAndReset();
            }

            public void Dispose()
            {
                Queue.Writer.TryComplete();
                try
                {
                    Worker.Wait();
                }
                catch (AggregateException)
                {
                    // the result isn't needed anymore
                }
                Sha512?.Dispose();
            }
        }
    }
}
