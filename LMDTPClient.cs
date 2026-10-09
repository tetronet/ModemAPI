using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        private long TimestampRequestSent = 0;
        private byte[] SHA512Received = new byte[64];
        private long CurrentDownloadBytesInternal = 0;
        private bool IsClosed = false;
        private Stream? CurrentDestinationStream = null;
        public long CurrentDownloadBytes { get { return CurrentDownloadBytesInternal; } }
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
        public void Request(Stream destination, string remoteResourceName, long maxDownloadBytes, int ticksTimeout = 50000000)
        {
            // format:
            // 2 bytes prefix
            // 2 bytes length of the remote resource name
            // 0-65535 bytes remote resource name
            // 8 bytes maximum amount of bytes, that we could download
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
                Span<byte> request = new byte[2 + 2 + resourceNameByteLength + 8];
                BinaryPrimitives.WriteUInt16BigEndian(request, LMDTP_REQUEST_PREFIX); // protocol prefix
                BinaryPrimitives.WriteUInt16BigEndian(request[2..], (ushort)resourceNameByteLength); // length of the resource name
                Encoding.UTF8.GetBytes(remoteResourceName, request[4..]);
                BinaryPrimitives.WriteInt64BigEndian(request[^8..], maxDownloadBytes);
                IsReceivingData = true;
                IsFirstPacket = true;
                UnderlyingClient.OnMessageReceived += SRTPDataEvent;
                UnderlyingModem.LowLevelTransmit(request.ToArray(), Destination, "lm_tunnel", ConnectionID);
                TimestampRequestSent = DateTime.Now.Ticks;
                while (IsReceivingData)
                {
                    if (IsFirstPacket && DateTime.Now.Ticks - TimestampRequestSent > ticksTimeout)
                    {
                        throw new TimeoutException("LMDTP Obtain Request timed out...");
                    }
                    Thread.Sleep(100);
                }
            }
            finally
            {
                UnderlyingClient.OnMessageReceived -= SRTPDataEvent;
                IsReceivingData = false;
                IsFirstPacket = true;
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
                        // read checksum SHA-512
                        SHA512Received = bytes.Slice(4, 64).ToArray();
                        // allocate data and read it
                        long dataLength = BinaryPrimitives.ReadInt64BigEndian(bytes[68..]);
                        CurrentDestinationStream.SetLength(dataLength);
                        CurrentDestinationStream.Write(bytes[76..]);

                        IsFirstPacket = false;
                    }
                    else
                    {
                        CurrentDestinationStream.Write(bytes);
                        if (CurrentDestinationStream.Position == CurrentDestinationStream.Length)
                        {
                            CurrentDestinationStream.Position = 0;
                            if (SHA512.HashData(CurrentDestinationStream).SequenceEqual(SHA512Received))
                            {
                                IsFirstPacket = true;
                                IsReceivingData = false;
                            }
                            else
                            {
                                ErrorOccured(new InvalidDataException("SHA-512 is mismatched. Received data is corrupted and cannot be corrected."));
                                return;
                            }
                        }
                    }
                    CurrentDownloadBytesInternal = CurrentDestinationStream.Position;
                }
            }
            catch (Exception e)
            {
                ErrorOccured(e);
            }
        }
    }
}
