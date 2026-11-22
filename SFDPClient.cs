using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ModemAPI
{
    /// <summary>
    /// Sends data to an SFDP (System For Data Protocol) server and waits for its response.
    /// Thread-safe: several requests can be sent at the same time, also to different servers.
    /// </summary>
    /// <remarks>
    /// The delivery isn't guaranteed: a request or its response can be lost, and then the request times out, though the server
    /// could have got the data. Nothing is retransmitted; LMDTP is for data, that has to be delivered.
    /// </remarks>
    public class SFDPClient : IDisposable
    {
        private readonly IModem Modem;
        private readonly ConcurrentDictionary<string, PendingRequest> Pending = new();
        private readonly Action<DataBlock, Action> ResponseListener;
        private readonly Lock AttachLock = new();
        private bool Attached = false;
        private bool Disposed = false;

        private sealed class PendingRequest
        {
            public required byte[]? Key;
            public required string ClientAddress;
            public required string ServerAddress;
            public required uint ConnectionID;
            public required bool IsPing;
            public readonly TaskCompletionSource<(string Header, string Body)> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// ConnectionID of the requests: one of the range, that the server listens to. The server answers with the same ConnectionID.
        /// </summary>
        public uint PacketConnectionID { get; set; }
        /// <summary>
        /// Most bytes of one packet, when a request is fragmented. 1 to 65535, default 1024.
        /// </summary>
        public ushort PacketSize { get; set; } = 1024;
        /// <summary>
        /// Gets invoked for responses, that are dropped: their tag didn't match (a wrong key, or they were damaged or forged),
        /// or they break the format. The request waiting for them keeps waiting for a valid response till its timeout.
        /// </summary>
        public event Action<Exception> ErrorOccured = delegate { };

        /// <summary>
        /// Creates a new SFDP client.
        /// </summary>
        /// <param name="modem">Modem, through which the requests are sent; this client doesn't dial or drop it</param>
        public SFDPClient(IModem modem)
        {
            ArgumentNullException.ThrowIfNull(modem);
            Modem = modem;
            ResponseListener = OnDataBlock;
        }

        /// <summary>
        /// Sends <c>SFD_PING</c> (without encryption) and waits for <c>SFD_PONG</c>.
        /// </summary>
        /// <param name="systemAddress">Address of the SFDP server</param>
        /// <param name="timeoutMs">How long to wait for the response, in milliseconds (more than 0)</param>
        /// <returns><see langword="true"/> - the server answered, <see langword="false"/> - it didn't in timeoutMs</returns>
        /// <exception cref="ArgumentNullException">systemAddress is null</exception>
        /// <exception cref="ArgumentException">systemAddress isn't a valid tetronet address</exception>
        /// <exception cref="ArgumentOutOfRangeException">timeoutMs is not more than 0</exception>
        /// <exception cref="ModemNotConnectedException">The modem isn't connected to the tetronet</exception>
        /// <exception cref="ObjectDisposedException">This client is disposed</exception>
        public bool CheckSystem(Address systemAddress, int timeoutMs)
        {
            return Request(systemAddress, SFDPProtocol.Ping, null, timeoutMs, null) != null;
        }

        /// <summary>
        /// Sends the data as is with the header <c>SFD_DATA_TRANSFER</c> and returns the data of the response.
        /// </summary>
        /// <param name="systemAddress">Address of the SFDP server</param>
        /// <param name="rawData">The data without the header; it isn't checked</param>
        /// <param name="timeoutMs">How long to wait for the response, in milliseconds (more than 0)</param>
        /// <param name="encryptionKey"><see langword="null"/> or empty - the request and its response are sent without encryption;
        /// 32 bytes - AES-256-GCM key, the same as the server has for the address of this modem</param>
        /// <returns>The data of the response without the header, or <see langword="null"/>, when the server answered <c>SFD_NO_DATA</c></returns>
        /// <exception cref="ArgumentNullException">systemAddress or rawData is null</exception>
        /// <exception cref="ArgumentException">systemAddress isn't a valid tetronet address</exception>
        /// <exception cref="ArgumentOutOfRangeException">timeoutMs is not more than 0</exception>
        /// <exception cref="InvalidDataException">The key is not empty and not 32 bytes long</exception>
        /// <exception cref="ModemNotConnectedException">The modem isn't connected to the tetronet</exception>
        /// <exception cref="TimeoutException">No valid response came in timeoutMs</exception>
        /// <exception cref="ObjectDisposedException">This client is disposed</exception>
        public string? SendDataRaw(Address systemAddress, string rawData, int timeoutMs, byte[]? encryptionKey)
        {
            ArgumentNullException.ThrowIfNull(rawData);
            (string Header, string Body)? response = Request(systemAddress, SFDPProtocol.DataTransfer, rawData, timeoutMs, encryptionKey)
                ?? throw new TimeoutException($"SFDP server {systemAddress} didn't answer in {timeoutMs} ms");
            return response.Value.Header == SFDPProtocol.NoData ? null : response.Value.Body;
        }

        /// <summary>
        /// Sends an object (see <see cref="SFDPObject.FromObject"/>) with the header <c>SFD_DATA_TRANSFER</c> and returns the response
        /// as an object of type <typeparamref name="TResult"/> (see <see cref="SFDPObject.ToObject{T}"/>).
        /// </summary>
        /// <returns>The response, or <see langword="null"/> (default), when the server answered <c>SFD_NO_DATA</c></returns>
        /// <exception cref="NotSupportedException">The data can't be written as an SFDP object, or the type of the result isn't supported</exception>
        /// <exception cref="FormatException">The response breaks the format, or can't be converted to <typeparamref name="TResult"/></exception>
        /// <exception cref="TimeoutException">No valid response came in timeoutMs</exception>
        /// <inheritdoc cref="SendDataRaw" path="/exception"/>
        public TResult? SendData<T, TResult>(Address systemAddress, T data, int timeoutMs, byte[]? encryptionKey)
        {
            ArgumentNullException.ThrowIfNull(data);
            string? response = SendDataRaw(systemAddress, SFDPObject.FromObject(data).ToString(), timeoutMs, encryptionKey);
            if (response == null)
            {
                return default;
            }
            return SFDPObject.Parse(response).ToObject<TResult>();
        }

        /// <summary>
        /// Sends a request and waits for its response; null on a timeout.
        /// </summary>
        private (string Header, string Body)? Request(Address server, string header, string? body, int timeoutMs, byte[]? encryptionKey)
        {
            ArgumentNullException.ThrowIfNull(server);
            if (!Address.Validate(server))
            {
                throw new ArgumentException($"'{server}' isn't a valid tetronet address", nameof(server));
            }
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
            byte[]? key = null;
            if (encryptionKey != null && encryptionKey.Length != 0)
            {
                if (encryptionKey.Length != SFDPProtocol.KeyLength)
                {
                    throw new InvalidDataException($"AES-256-GCM key has to be exactly {SFDPProtocol.KeyLength} bytes long");
                }
                // copied, so changing the caller's array while waiting doesn't change the key used for the response
                key = (byte[])encryptionKey.Clone();
            }
            ObjectDisposedException.ThrowIf(Disposed, this);
            Address? local = Modem.LocalModemAddress;
            if (!Modem.IsModemConnected || local == null || local.IsEmpty)
            {
                throw new ModemNotConnectedException("the modem of the SFDP client isn't connected to the tetronet");
            }
            EnsureAttached();
            byte[] plaintext = SFDPProtocol.Utf8.GetBytes(SFDPProtocol.Message(header, body));
            byte[] nonce = RandomNumberGenerator.GetBytes(SFDPProtocol.NonceLength);
            string id = Convert.ToHexStringLower(nonce);
            uint connectionId = PacketConnectionID;
            PendingRequest request = new()
            {
                Key = key,
                ClientAddress = local.ToString(),
                ServerAddress = server.ToString(),
                ConnectionID = connectionId,
                IsPing = header == SFDPProtocol.Ping,
            };
            byte[] data;
            string metadata;
            if (key == null)
            {
                data = plaintext;
                metadata = SFDPProtocol.PlainRequestPrefix + id;
            }
            else
            {
                string time = SFDPProtocol.FormatTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                byte[] aad = SFDPProtocol.RequestAad(request.ClientAddress, request.ServerAddress, time);
                data = SFDPProtocol.Encrypt(key, nonce, plaintext, aad, out byte[] tag);
                metadata = SFDPProtocol.EncryptedRequestPrefix + id + ":" + time + ":" + Convert.ToHexStringLower(tag);
            }
            // the nonce is 12 random bytes, so two requests never share it
            Pending[id] = request;
            try
            {
                SFDPProtocol.Send(Modem, data, server, connectionId, metadata, PacketSize);
                if (!request.Result.Task.Wait(timeoutMs))
                {
                    return null;
                }
                return request.Result.Task.Result;
            }
            finally
            {
                Pending.TryRemove(id, out _);
            }
        }

        private void EnsureAttached()
        {
            lock (AttachLock)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                if (!Attached)
                {
                    Modem.AttachReceiveEvent(ResponseListener);
                    Attached = true;
                }
            }
        }

        private void OnDataBlock(DataBlock block, Action k)
        {
            try
            {
                if (block.QueryType != SFDPProtocol.QueryType || block.Metadata == null)
                {
                    return;
                }
                string metadata = block.Metadata;
                bool encrypted;
                string id;
                byte[]? nonce = null;
                byte[]? tag = null;
                if (metadata.StartsWith(SFDPProtocol.PlainResponsePrefix, StringComparison.Ordinal))
                {
                    encrypted = false;
                    id = metadata[SFDPProtocol.PlainResponsePrefix.Length..];
                }
                else if (metadata.StartsWith(SFDPProtocol.EncryptedResponsePrefix, StringComparison.Ordinal))
                {
                    encrypted = true;
                    string[] fields = metadata[SFDPProtocol.EncryptedResponsePrefix.Length..].Split(':');
                    if (fields.Length != 3)
                    {
                        throw new InvalidDataException("an encrypted SFDP response must have 3 fields in its metadata");
                    }
                    id = fields[0];
                    nonce = SFDPProtocol.ParseHex(fields[1], SFDPProtocol.NonceLength, "nonce");
                    tag = SFDPProtocol.ParseHex(fields[2], SFDPProtocol.TagLength, "tag");
                }
                else
                {
                    // a request to a server on this modem, or something else
                    return;
                }
                if (!Pending.TryGetValue(id, out PendingRequest? request) || block.ConnectionID != request.ConnectionID)
                {
                    // a response to a request, that timed out already, a repeated one, or something else
                    return;
                }
                byte[] data = [.. block.DataBytes];
                byte[] plaintext;
                if (request.Key == null)
                {
                    if (encrypted)
                    {
                        throw new InvalidDataException($"SFDP server {block.Transmitter} sent an encrypted response to a request without encryption");
                    }
                    plaintext = data;
                }
                else
                {
                    if (!encrypted)
                    {
                        // never accepted: anyone could send it
                        throw new InvalidDataException($"SFDP response from {block.Transmitter} to an encrypted request isn't encrypted");
                    }
                    byte[] aad = SFDPProtocol.ResponseAad(request.ClientAddress, request.ServerAddress, id);
                    // throws on a wrong tag, and then nothing of the data is used
                    plaintext = SFDPProtocol.Decrypt(request.Key, nonce!, data, tag!, aad);
                }
                (string header, string body) = SFDPProtocol.ParseMessage(SFDPProtocol.StrictUtf8.GetString(plaintext));
                bool expected = request.IsPing ? header == SFDPProtocol.Pong : header is SFDPProtocol.DataResponse or SFDPProtocol.NoData;
                if (!expected)
                {
                    throw new InvalidDataException($"SFDP server {block.Transmitter} answered '{header.Trim()}' to {(request.IsPing ? "SFD_PING" : "SFD_DATA_TRANSFER")}");
                }
                request.Result.TrySetResult((header, body));
            }
            catch (Exception ex)
            {
                ErrorOccured(ex);
            }
        }

        /// <summary>
        /// Stops listening to responses; requests in progress time out.
        /// </summary>
        public void Dispose()
        {
            lock (AttachLock)
            {
                if (Disposed)
                {
                    return;
                }
                Disposed = true;
                if (Attached)
                {
                    Modem.DetachReceiveEvent(ResponseListener);
                    Attached = false;
                }
            }
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// The parts of SFDP, that the client and the server share (see PROTOCOLS.md).
    /// </summary>
    internal static class SFDPProtocol
    {
        public const string QueryType = "sfd_object";
        public const string PlainRequestPrefix = "q:";
        public const string EncryptedRequestPrefix = "qe:";
        public const string PlainResponsePrefix = "r:";
        public const string EncryptedResponsePrefix = "re:";
        public const string DataTransfer = "      SFD_DATA_TRANSFER";
        public const string DataResponse = "      SFD_DATA_RESPONSE";
        public const string Pong = "      SFD_PONG";
        public const string Ping = "      SFD_PING";
        public const string NoData = "      SFD_NO_DATA";
        public const int KeyLength = 32;
        public const int NonceLength = 12;
        public const int TagLength = 16;
        public static readonly UTF8Encoding Utf8 = new(false, false);
        public static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>
        /// The header line, and, when there is a body, LF and the body.
        /// </summary>
        public static string Message(string header, string? body) => body == null ? header : header + "\n" + body;

        /// <summary>
        /// Splits a message into its header and its body (empty, when there's none). The header ends at the first line break:
        /// CRLF, LFCR, CR or LF; the body is everything after it, as it is.
        /// </summary>
        /// <exception cref="InvalidDataException">The header isn't one of the SFDP headers</exception>
        public static (string Header, string Body) ParseMessage(string message)
        {
            int end = message.IndexOfAny(['\r', '\n']);
            string header = end < 0 ? message : message[..end];
            string body = end < 0 ? "" : message[(end + SFDPFormat.LineBreakLength(message, end))..];
            if (header is not (DataTransfer or DataResponse or Pong or Ping or NoData))
            {
                string shown = header.Length > 64 ? header[..64] + "..." : header;
                throw new InvalidDataException($"'{shown}' isn't an SFDP header (6 spaces and the name of the message)");
            }
            return (header, body);
        }

        public static string FormatTime(long unixSeconds) => unixSeconds.ToString("x16", CultureInfo.InvariantCulture);

        public static byte[] RequestAad(string client, string server, string timeHex) => Encoding.ASCII.GetBytes($"sfdp-q|{client}|{server}|{timeHex}");

        public static byte[] ResponseAad(string client, string server, string requestNonceHex) => Encoding.ASCII.GetBytes($"sfdp-r|{client}|{server}|{requestNonceHex}");

        public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad, out byte[] tag) => NCCPProtocol.Encrypt(key, nonce, plaintext, aad, out tag);

        /// <exception cref="AuthenticationTagMismatchException">The tag doesn't match: the key, the data, the AAD or the tag is wrong</exception>
        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] aad) => NCCPProtocol.Decrypt(key, nonce, ciphertext, tag, aad);

        /// <summary>
        /// Reads exactly <paramref name="length"/> bytes written as hex digits (any case).
        /// </summary>
        public static byte[] ParseHex(string hex, int length, string what)
        {
            if (hex.Length != length * 2 || !hex.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"SFDP {what} must be {length * 2} hex digits");
            }
            return Convert.FromHexString(hex);
        }

        /// <summary>
        /// Sends a request or a response with Transmit (fragmented). A message is never empty: it has a header at least.
        /// </summary>
        public static void Send(IModem modem, byte[] data, Address to, uint connectionId, string metadata, ushort packetSize)
        {
            modem.Transmit(data, to, QueryType, connectionId, metadata, packetSize == 0 ? (ushort)1024 : packetSize);
        }
    }
}
