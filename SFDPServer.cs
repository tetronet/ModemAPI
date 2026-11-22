using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace ModemAPI
{
    /// <summary>
    /// Takes data, that SFDP (System For Data Protocol) clients send, and answers them.
    /// Like every server class, it doesn't dial or drop the modem: the modem has to be connected to the tetronet before <see cref="Start"/>.
    /// </summary>
    /// <remarks>
    /// The delivery isn't guaranteed: data can be taken while its response gets lost, and the client then may send it again.
    /// Every request is independent, there are no sessions. The server answers every <c>SFD_PING</c> with <c>SFD_PONG</c> and every
    /// accepted <c>SFD_DATA_TRANSFER</c> with <c>SFD_DATA_RESPONSE</c> or <c>SFD_NO_DATA</c>.
    /// </remarks>
    public class SFDPServer
    {
        private readonly IModem Modem;
        private readonly uint ConnectionIDRangeStart;
        private readonly uint ConnectionIDRangeEnd;
        private readonly Lock StartLock = new();
        private Action<DataBlock, Action>? Listener = null;
        // nonces of the accepted encrypted requests: (client address, nonce in lower case hex) -> when it can be forgotten
        private readonly Dictionary<(string, string), long> SeenNonces = [];
        private readonly Queue<((string, string) Key, long Expires)> SeenNoncesOrder = new();
        private readonly Lock SeenNoncesLock = new();

        /// <summary>
        /// Gets invoked for every <c>SFD_PING</c>, that is answered, with the address of the client and the ConnectionID of the request.
        /// </summary>
        public event Action<Address, uint> OnPing = delegate { };
        /// <summary>
        /// Gets invoked for every accepted <c>SFD_DATA_TRANSFER</c> with the address of the client and the data without the header,
        /// not parsed. It is invoked before <see cref="OnData"/>.
        /// </summary>
        public event Action<Address, string> OnDataRaw = delegate { };
        /// <summary>
        /// Gets the address of the client and the parsed data of every accepted <c>SFD_DATA_TRANSFER</c>, and returns the response:
        /// <see langword="null"/> - <c>SFD_NO_DATA</c> is sent; a <see cref="string"/> - sent as the data of the response as is;
        /// an <see cref="SFDPObject"/> or any other object - written as an SFDP object (see <see cref="SFDPObject.FromObject"/>).
        /// When it isn't set, the data isn't parsed and <c>SFD_NO_DATA</c> is sent. When the data can't be parsed, or it throws,
        /// or its result can't be written, <see cref="ErrorOccured"/> gets invoked and <c>SFD_NO_DATA</c> is sent.
        /// </summary>
        public Func<Address, SFDPObject, object?>? OnData { get; set; }
        /// <summary>
        /// <see langword="true"/> - only <c>SFD_DATA_TRANSFER</c> encrypted with the key of its address in <see cref="SFDPEncryptionKeys"/>
        /// is taken; requests without encryption (except <c>SFD_PING</c>), with a wrong tag, from an address without a key, too old or repeated
        /// are dropped without an answer.
        /// <see langword="false"/> - only requests without encryption are taken, without any checks.
        /// </summary>
        public bool SFDPServerUsesEncryption = false;
        /// <summary>
        /// AES-256-GCM keys (32 bytes) of the clients by their address. Every address has a key of its own, so a key taken out of one
        /// device doesn't open the others.
        /// </summary>
        public ConcurrentDictionary<Address, byte[]> SFDPEncryptionKeys { get; } = new();
        /// <summary>
        /// More than 0: an encrypted request is taken only, when the time it was sent differs from the clock of this server by not more
        /// than this number of seconds, and its nonce is remembered for that time, so a copy of it is never taken again.
        /// 0: the time isn't checked (for devices without a clock), only the last <see cref="MaxRememberedNonces"/> nonces are remembered,
        /// so a copy can be taken again after it's forgotten or after a restart of the server. Default 120.
        /// </summary>
        public int ReplayWindowSeconds = 120;
        /// <summary>
        /// Most nonces of encrypted requests remembered at the same time, at least 1, default 100000. With <see cref="ReplayWindowSeconds"/>
        /// more than 0, requests over it are dropped till the old nonces expire; with 0, the oldest nonce is forgotten.
        /// </summary>
        public int MaxRememberedNonces = 100000;
        /// <summary>
        /// Most bytes of one packet, when a response is fragmented. 1 to 65535, default 1024.
        /// </summary>
        public ushort PacketSize = 1024;
        /// <summary>
        /// Gets invoked for requests, that are dropped, for exceptions of <see cref="OnPing"/>, <see cref="OnDataRaw"/> and
        /// <see cref="OnData"/>, for data, that can't be parsed, and for errors of sending the responses.
        /// </summary>
        public event Action<Exception> ErrorOccured = delegate { };
        /// <summary>
        /// The clock used for <see cref="ReplayWindowSeconds"/>, Unix time in seconds.
        /// </summary>
        internal Func<long> UnixTimeSeconds = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// Creates a new SFDP server.
        /// </summary>
        /// <param name="modem">Modem, through which the server takes the requests; this server doesn't dial or drop it</param>
        /// <param name="connectionIdRangeStart">Start of the ConnectionID range, in which this server takes the requests</param>
        /// <param name="connectionIdRangeEnd">End of the ConnectionID range (included)</param>
        public SFDPServer(IModem modem, uint connectionIdRangeStart, uint connectionIdRangeEnd)
        {
            ArgumentNullException.ThrowIfNull(modem);
            Modem = modem;
            ConnectionIDRangeStart = connectionIdRangeStart;
            ConnectionIDRangeEnd = connectionIdRangeEnd;
        }

        /// <summary>
        /// Starts taking requests. After <see cref="Stop"/> the server can be started again.
        /// </summary>
        /// <exception cref="ModemNotConnectedException">The modem isn't connected to the tetronet</exception>
        /// <exception cref="InvalidOperationException">The server is started already, or its settings are invalid</exception>
        public void Start()
        {
            if (ConnectionIDRangeStart > ConnectionIDRangeEnd)
            {
                throw new InvalidOperationException($"the ConnectionID range {ConnectionIDRangeStart}-{ConnectionIDRangeEnd} is empty");
            }
            if (ReplayWindowSeconds < 0)
            {
                throw new InvalidOperationException($"ReplayWindowSeconds must not be negative, but it is {ReplayWindowSeconds}");
            }
            if (MaxRememberedNonces < 1)
            {
                throw new InvalidOperationException($"MaxRememberedNonces must be at least 1, but it is {MaxRememberedNonces}");
            }
            if (PacketSize == 0)
            {
                throw new InvalidOperationException("PacketSize must be at least 1");
            }
            if (!Modem.IsModemConnected)
            {
                throw new ModemNotConnectedException("the modem of the SFDP server isn't connected to the tetronet");
            }
            lock (StartLock)
            {
                if (Listener != null)
                {
                    throw new InvalidOperationException("this SFDP server is already started");
                }
                Listener = OnDataBlock;
                Modem.AttachReceiveEvent(Listener);
            }
        }

        /// <summary>
        /// Stops taking requests; requests, that are being handled, still get answered. The server can be started again.
        /// </summary>
        public void Stop()
        {
            lock (StartLock)
            {
                if (Listener == null)
                {
                    return;
                }
                Modem.DetachReceiveEvent(Listener);
                Listener = null;
            }
        }

        private void OnDataBlock(DataBlock block, Action k)
        {
            // only the cheap checks on the thread of the modem, OnData may take long
            if (block.ConnectionID < ConnectionIDRangeStart || block.ConnectionID > ConnectionIDRangeEnd ||
                block.QueryType != SFDPProtocol.QueryType || block.Metadata == null ||
                !(block.Metadata.StartsWith(SFDPProtocol.PlainRequestPrefix, StringComparison.Ordinal) ||
                  block.Metadata.StartsWith(SFDPProtocol.EncryptedRequestPrefix, StringComparison.Ordinal)))
            {
                return;
            }
            Task.Run(() => HandleRequest(block));
        }

        private void HandleRequest(DataBlock block)
        {
            try
            {
                string metadata = block.Metadata!;
                Address client = block.Transmitter;
                byte[] data = [.. block.DataBytes];
                if (metadata.StartsWith(SFDPProtocol.PlainRequestPrefix, StringComparison.Ordinal))
                {
                    string id = metadata[SFDPProtocol.PlainRequestPrefix.Length..];
                    SFDPProtocol.ParseHex(id, SFDPProtocol.NonceLength, "request ID");
                    (string header, string body) = SFDPProtocol.ParseMessage(SFDPProtocol.StrictUtf8.GetString(data));
                    if (SFDPServerUsesEncryption && header != SFDPProtocol.Ping)
                    {
                        throw new UnauthorizedAccessException($"SFDP request without encryption from {client} is dropped: this server uses encryption");
                    }
                    string answer = Answer(client, block.ConnectionID, header, body);
                    SFDPProtocol.Send(Modem, SFDPProtocol.Utf8.GetBytes(answer), client, block.ConnectionID, SFDPProtocol.PlainResponsePrefix + id, PacketSize);
                    return;
                }
                if (!SFDPServerUsesEncryption)
                {
                    throw new InvalidDataException($"encrypted SFDP request from {client} is dropped: this server doesn't use encryption");
                }
                string[] fields = metadata[SFDPProtocol.EncryptedRequestPrefix.Length..].Split(':');
                if (fields.Length != 3)
                {
                    throw new InvalidDataException($"encrypted SFDP request from {client} must have 3 fields in its metadata");
                }
                string nonceHex = fields[0];
                string timeHex = fields[1];
                byte[] nonce = SFDPProtocol.ParseHex(nonceHex, SFDPProtocol.NonceLength, "nonce");
                byte[] timeBytes = SFDPProtocol.ParseHex(timeHex, 8, "time");
                byte[] tag = SFDPProtocol.ParseHex(fields[2], SFDPProtocol.TagLength, "tag");
                if (!SFDPEncryptionKeys.TryGetValue(client, out byte[]? storedKey) || storedKey == null)
                {
                    throw new UnauthorizedAccessException($"SFDP request from {client} is dropped: there's no key for this address");
                }
                byte[] key = (byte[])storedKey.Clone();
                if (key.Length != SFDPProtocol.KeyLength)
                {
                    throw new InvalidDataException($"the key of {client} in SFDPEncryptionKeys is {key.Length} bytes long, AES-256-GCM key has to be exactly {SFDPProtocol.KeyLength} bytes long");
                }
                string clientAddress = client.ToString();
                string serverAddress = block.Receiver.ToString();
                byte[] plaintext;
                try
                {
                    // the tag is checked before anything else is done with the request
                    plaintext = SFDPProtocol.Decrypt(key, nonce, data, tag, SFDPProtocol.RequestAad(clientAddress, serverAddress, timeHex));
                }
                catch (Exception ex)
                {
                    throw new UnauthorizedAccessException($"SFDP request from {client} is dropped: its tag doesn't match (a wrong key, or it was damaged or forged)", ex);
                }
                long sentAt = BinaryPrimitives.ReadInt64BigEndian(timeBytes);
                string? refusal = AcceptNonce(clientAddress, nonceHex.ToLowerInvariant(), sentAt);
                if (refusal != null)
                {
                    throw new UnauthorizedAccessException(refusal);
                }
                (string requestHeader, string requestBody) = SFDPProtocol.ParseMessage(SFDPProtocol.StrictUtf8.GetString(plaintext));
                string response = Answer(client, block.ConnectionID, requestHeader, requestBody);
                byte[] responseNonce = RandomNumberGenerator.GetBytes(SFDPProtocol.NonceLength);
                // bound to the request: the nonce of the request goes into the AAD exactly as it came
                byte[] ciphertext = SFDPProtocol.Encrypt(key, responseNonce, SFDPProtocol.Utf8.GetBytes(response), SFDPProtocol.ResponseAad(clientAddress, serverAddress, nonceHex), out byte[] responseTag);
                string responseMetadata = SFDPProtocol.EncryptedResponsePrefix + nonceHex + ":" + Convert.ToHexStringLower(responseNonce) + ":" + Convert.ToHexStringLower(responseTag);
                SFDPProtocol.Send(Modem, ciphertext, client, block.ConnectionID, responseMetadata, PacketSize);
            }
            catch (Exception ex)
            {
                ErrorOccured(ex);
            }
        }

        /// <summary>
        /// The response to an accepted request. Never throws for the errors of the user's handlers, only for a request, that isn't
        /// SFD_PING or SFD_DATA_TRANSFER (then it's dropped).
        /// </summary>
        private string Answer(Address client, uint connectionId, string header, string body)
        {
            if (header == SFDPProtocol.Ping)
            {
                try
                {
                    OnPing(Address.Copy(client), connectionId);
                }
                catch (Exception ex)
                {
                    ErrorOccured(ex);
                }
                return SFDPProtocol.Pong;
            }
            if (header != SFDPProtocol.DataTransfer)
            {
                throw new InvalidDataException($"SFDP request from {client} is dropped: '{header.Trim()}' isn't a request");
            }
            try
            {
                OnDataRaw(Address.Copy(client), body);
            }
            catch (Exception ex)
            {
                ErrorOccured(ex);
            }
            Func<Address, SFDPObject, object?>? handler = OnData;
            if (handler == null)
            {
                return SFDPProtocol.NoData;
            }
            try
            {
                object? result = handler(Address.Copy(client), SFDPObject.Parse(body));
                string? data = result switch
                {
                    null => null,
                    string s => s,
                    _ => SFDPObject.FromObject(result).ToString(),
                };
                return data == null ? SFDPProtocol.NoData : SFDPProtocol.Message(SFDPProtocol.DataResponse, data);
            }
            catch (Exception ex)
            {
                ErrorOccured(ex);
                return SFDPProtocol.NoData;
            }
        }

        /// <summary>
        /// Checks the time of an authentic request and remembers its nonce. Returns null, when the request is taken, otherwise why
        /// it's dropped: it's too old, from the future, repeated, or there's no room to remember its nonce.
        /// </summary>
        private string? AcceptNonce(string client, string nonceHex, long sentAt)
        {
            int window = ReplayWindowSeconds;
            long now = Stopwatch.GetTimestamp();
            long expires = long.MaxValue;
            if (window > 0)
            {
                long serverTime = UnixTimeSeconds();
                // checked without overflow: sentAt can be anything the client wrote
                if (sentAt < serverTime - window || sentAt > serverTime + window)
                {
                    return $"SFDP request from {client} is dropped: it was sent at {sentAt}, and the time of this server is {serverTime} (ReplayWindowSeconds is {window})";
                }
                // remembered till the request gets out of the window for sure, whichever way the clocks go
                expires = now + Stopwatch.Frequency * 2 * (long)window + Stopwatch.Frequency;
            }
            (string, string) key = (client, nonceHex);
            lock (SeenNoncesLock)
            {
                while (SeenNoncesOrder.Count > 0 && SeenNoncesOrder.Peek().Expires <= now)
                {
                    ((string, string) old, long oldExpires) = SeenNoncesOrder.Dequeue();
                    if (SeenNonces.TryGetValue(old, out long current) && current == oldExpires)
                    {
                        SeenNonces.Remove(old);
                    }
                }
                if (SeenNonces.ContainsKey(key))
                {
                    return $"SFDP request from {client} is dropped: it's a copy of a request, that was taken already (nonce {nonceHex})";
                }
                if (SeenNonces.Count >= MaxRememberedNonces)
                {
                    if (window > 0)
                    {
                        // forgetting a nonce, that is still in the window, would let its copy through
                        return $"SFDP request from {client} is dropped: {MaxRememberedNonces} nonces are remembered already (MaxRememberedNonces)";
                    }
                    while (SeenNonces.Count >= MaxRememberedNonces && SeenNoncesOrder.Count > 0)
                    {
                        SeenNonces.Remove(SeenNoncesOrder.Dequeue().Key);
                    }
                }
                SeenNonces[key] = expires;
                SeenNoncesOrder.Enqueue((key, expires));
            }
            return null;
        }
    }
}
