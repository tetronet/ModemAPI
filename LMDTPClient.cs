using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ModemAPI
{
    /// <summary>
    /// Represents a Large Message Direct Tunnel Protocol (LMDTP) client.
    /// </summary>
    public class LMDTPClient
    {
        public const ushort LMDTP_REQUEST_PREFIX = 0xFF40;
        public const ushort LMDTP_RESPONSE_PREFIX = 0x72D3;
        private SRTPClient UnderlyingClient;
        private IModem UnderlyingModem;
        private Address Destination;
        private uint ConnectionID;
        public event Action<Exception> ErrorOccured = delegate { };
        private bool IsReceivingData = false;
        private bool IsFirstPacket = true;
        private long TimestampLastPacket = 0;
        private byte[] ExpectedHash = new byte[64];
        private LMDTPHashAlgorithms ResponseHashAlgorithm = LMDTPHashAlgorithms.Sha512;
        private long CurrentDownloadBytesInternal = 0;
        private long TotalDownloadBytesInternal = 0;
        private bool IsClosed = false;
        private Stream? CurrentDestinationStream = null;
        private BackgroundHasher? Hasher = null;
        private readonly ManualResetEventSlim ReceivingFinished = new(false);
        public long CurrentDownloadBytes { get { return CurrentDownloadBytesInternal; } }
        public long TotalDownloadBytes { get { return TotalDownloadBytesInternal; } }
        /// <summary>
        /// Hash algorithms this client offers to the server, the server picks the fastest one it can provide.
        /// SHA-512 is always accepted, because servers that don't know about this choice always send it.
        /// </summary>
        public LMDTPHashAlgorithms SupportedHashAlgorithms { get; set; } = LMDTPHashAlgorithms.Sha512 | LMDTPHashAlgorithms.XxHash3;
        public LMDTPClient(IModem baseModem, Address dest, uint cid, int srtpTimeoutTicks)
        {
            UnderlyingClient = new(baseModem, dest, "lm_tunnel", cid, srtpTimeoutTicks);
            UnderlyingClient.OnError += delegate (int code, string desc)
            {
                ErrorOccured(new Exception($"SRTP ERRNO {code}: {desc}"));
            };
            UnderlyingModem = baseModem;
            Destination = dest;
            ConnectionID = cid;
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
        /// </summary>
        /// <param name="destination">Stream</param>
        /// <param name="remoteResourceName">Remote resource name to obtain</param>
        /// <param name="maxDownloadBytes">Limit how much you want to server to send you, if the resourse is longer, the server will return an error and the client will throw an exception</param>
        /// <param name="ticksTimeout">How much ticks this request can wait without packets before throwing <see cref="TimeoutException"/></param>
        public void Request(Stream destination, string remoteResourceName, long maxDownloadBytes, int ticksTimeout = 50000000, LMDTPRequestType rqType = LMDTPRequestType.Obtain)
        {
            // format:
            // 2 bytes prefix
            // 2 bytes length of the remote resource name
            // 0-65535 bytes remote resource name
            // 8 bytes maximum amount of bytes, that we could download
            // 1 byte request type
            // 1 byte supported hash algorithms (older servers ignore it and answer with SHA-512)
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
                Span<byte> request = new byte[2 + 2 + resourceNameByteLength + 8 + 1 + 1];
                BinaryPrimitives.WriteUInt16BigEndian(request, LMDTP_REQUEST_PREFIX); // protocol prefix
                BinaryPrimitives.WriteUInt16BigEndian(request[2..], (ushort)resourceNameByteLength); // length of the resource name
                Encoding.UTF8.GetBytes(remoteResourceName, request[4..]);
                BinaryPrimitives.WriteInt64BigEndian(request[^10..], maxDownloadBytes);
                request[^2] = (byte)rqType;
                request[^1] = (byte)(SupportedHashAlgorithms | LMDTPHashAlgorithms.Sha512);
                IsReceivingData = true;
                IsFirstPacket = true;
                Hasher?.Dispose();
                Hasher = null; // created once the response tells which hash the server used
                ReceivingFinished.Reset();
                UnderlyingClient.OnMessageReceived += SRTPDataEvent;
                UnderlyingModem.LowLevelTransmit(request.ToArray(), Destination, "lm_tunnel", ConnectionID);
                TimestampLastPacket = DateTime.Now.Ticks;
                while (IsReceivingData)
                {
                    if (DateTime.Now.Ticks - TimestampLastPacket > ticksTimeout)
                    {
                        throw new TimeoutException("LMDTP Obtain Request timed out...");
                    }
                    // returns as soon as the download is complete, the timeout is only for checking ticksTimeout
                    ReceivingFinished.Wait(100);
                }
            }
            finally
            {
                UnderlyingClient.OnMessageReceived -= SRTPDataEvent;
                IsReceivingData = false;
                IsFirstPacket = true;
                Hasher?.Dispose();
                Hasher = null;
            }
        }
        /// <summary>
        /// Closes this instance of LMDTP Client and removes it's resources.
        /// </summary>
        public void Close()
        {
            UnderlyingClient.Close();
            IsClosed = true;
        }
        void SRTPDataEvent(SRTPClient sender, byte[] data)
        {
            try
            {
                if (CurrentDestinationStream == null)
                {
                    throw new InvalidOperationException("Stream for the destination was null");
                }
                Span<byte> bytes = new(data);
                // check the prefix
                if (bytes.Length < 2 || BinaryPrimitives.ReadUInt16BigEndian(bytes) != LMDTP_RESPONSE_PREFIX && IsFirstPacket)
                {
                    ErrorOccured(new InvalidDataException("Remote server sent an invalid response: response prefix doesn't macth with the spec"));
                    return;
                }
                else
                {
                    // read all flags
                    if (IsFirstPacket)
                    {
                        if (bytes.Length < 76)
                        {
                            ErrorOccured(new IndexOutOfRangeException("The response contains less than 76 bytes"));
                            return;
                        }
                        ushort sFlags = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
                        if ((sFlags & (ushort)LMDTPResponseFlags.LargeMessageNotFound) != 0) // 0x0001
                        {
                            ErrorOccured(new FileNotFoundException("remote large message file was not found"));
                            return;
                        }
                        if ((sFlags & (ushort)LMDTPResponseFlags.LargeMessageExceededMTU) != 0) // 0x0002
                        {
                            ErrorOccured(new LargeMessageTooBigException("requested large message exceeded length, specified in the response"));
                            return;
                        }
                        if ((sFlags & (ushort)LMDTPResponseFlags.ServerSideError) != 0) // 0x0004
                        {
                            ErrorOccured(new ConnectionFailedException("lmdtp server failed for some reason"));
                            return;
                        }
                        if ((sFlags & (ushort)LMDTPResponseFlags.NotYourLargeMessage) != 0) // 0x0008
                        {
                            ErrorOccured(new UnauthorizedAccessException("this large message is not for you to download"));
                            return;
                        }
                        if ((sFlags & (ushort)LMDTPResponseFlags.ServerHardDriveFailed) != 0) // 0x0010
                        {
                            ErrorOccured(new IOException("server's hard drive is physically or logically failed"));
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
                        Hasher?.Dispose();
                        Hasher = new BackgroundHasher(ResponseHashAlgorithm);
                        // allocate data and read it
                        long dataLength = BinaryPrimitives.ReadInt64BigEndian(bytes[68..]);
                        TotalDownloadBytesInternal = dataLength;
                        CurrentDestinationStream.Write(bytes[76..]);
                        Hasher.Append(bytes[76..].ToArray());
                        IsFirstPacket = false;
                    }
                    else
                    {
                        BackgroundHasher hasher = Hasher ?? throw new InvalidOperationException("data arrived while no request is in progress");
                        CurrentDestinationStream.Write(bytes);
                        // SRTP gives every packet its own array, so it can be handed over without copying
                        hasher.Append(data);
                        if (CurrentDestinationStream.Position == TotalDownloadBytes)
                        {
                            CurrentDestinationStream.Position = 0;
                            if (hasher.Finish().SequenceEqual(ExpectedHash))
                            {
                                IsFirstPacket = true;
                                IsReceivingData = false;
                                ReceivingFinished.Set();
                            }
                            else
                            {
                                ErrorOccured(new InvalidDataException($"{(ResponseHashAlgorithm == LMDTPHashAlgorithms.XxHash3 ? "XxHash3" : "SHA-512")} is mismatched. Received data is corrupted and cannot be corrected."));
                                return;
                            }
                        }
                    }
                    TimestampLastPacket = DateTime.Now.Ticks;
                    CurrentDownloadBytesInternal = CurrentDestinationStream.Position;
                }
            }
            catch (Exception e)
            {
                ErrorOccured(e);
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
