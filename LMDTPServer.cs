using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace ModemAPI
{
    /// <summary>
    /// Responds for any LMDTP incoming requests.
    /// </summary>
    public class LMDTPServer
    {
        private IModem BaseModem;
        private HashSet<Address> GrayList;
        private ConcurrentDictionary<(Address, uint), SRTPClient> Clients = [];
        // sessions ended because a newer request took their place, or the server stopped: their end is not a timeout
        private readonly ConcurrentDictionary<SRTPClient, byte> ReplacedSessions = new();
        private Action<Packet, Action>? RequestListener = null;
        // the last request IDs seen from every client address and ConnectionID (v2 clients send one with every request)
        private readonly Dictionary<(Address, uint), (Queue<uint> Ids, long LastSeen)> RecentRequestIds = [];
        private readonly Lock RecentRequestIdsLock = new();
        private const int RequestIdsPerClient = 16;
        // the most clients, whose request IDs are remembered; over it the quarter, that asked longest ago, is forgotten at once
        private const int MaxClientsWithRequestIds = 4096;
        private readonly Lock StartLock = new();
        private bool IsListBlackOrWhite;
        private uint ConnectionIDRangeStart;
        private uint ConnectionIDRangeEnd;
        private int SrtpTimeout = 0;
        private int SrtpRateLimiting = 0;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        /// <summary>
        /// Resource provider, that must be set before starting the server, and when a request comes, this Resource Provider implementation is supposed to give the data of the response.
        /// </summary>
        public IResourceProvider? ResourceProvider { get; set; }
        /// <summary>
        /// Gets invoked once an Exception occurs in the depths of LMDTP Server.
        /// </summary>
        public event Action<Exception> ErrorOccured = delegate { };
        /// <summary>
        /// Gets or sets maximum length of the packet used in response transmission: 1 to <see cref="SRTPClient.MaxMessageLength"/>
        /// (default is 59992, the most an SRTP packet can carry). Bigger packets mean fewer packets and acknowledgements per byte, which is what limits the speed.
        /// </summary>
        public int PacketMaxLength = 59992;
        /// <summary>
        /// When a new reliable client appears.
        /// </summary>
        public event Action<Address, uint> SRTPClientReceived = delegate { };
        public bool SrtpEnablePacketLossBasedRateLimiter = false;
        public event Action<Address, uint> SRTPClientTransmitTimedOut = delegate { };
        /// <summary>
        /// Gets invoked on each successfully asnwered request, and you will get the address and the connection ID of the client completed.
        /// </summary>
        public event Action<Address, uint, string> RequestSuccessfullyProcessed = delegate { };
        /// <summary>
        /// The most sessions (requests in progress) this server keeps at the same time; requests over the limit are ignored and
        /// reported through <see cref="ErrorOccured"/>. Every session can hold up to about 60 MB of unacknowledged data. At least 1, default 256.
        /// </summary>
        public int MaxSessions = 256;
        /// <summary>
        /// The most sessions one remote address can have at the same time (every ConnectionID is a separate session). At least 1, default 16.
        /// </summary>
        public int MaxSessionsPerAddress = 16;
        /// <summary>
        /// Creates a new instance of LMDTPServer.
        /// </summary>
        /// <param name="baseModem">Modem, that will be used by the server to communicate with the Tetronet</param>
        /// <param name="connectionIdRangeStart">Start of the Connection ID range, in which this server will listen to the requests</param>
        /// <param name="connectionIdRangeEnd">End of the Connection ID range, in which this server will listen to the requests</param>
        /// <param name="grayList">List, that can be Black or White list to prevent some people from accessing this server</param>
        /// <param name="blackOrWhiteList">Set it to true if you want to make white list, and false to make black list</param>
        public LMDTPServer(IModem baseModem, uint connectionIdRangeStart, uint connectionIdRangeEnd, List<Address> grayList, bool blackOrWhiteList, int srtpTimeoutInTicks, int srtpRateLimiting)
        {
            BaseModem = baseModem;
            ConnectionIDRangeStart = connectionIdRangeStart;
            ConnectionIDRangeEnd = connectionIdRangeEnd;
            GrayList = [..grayList];
            IsListBlackOrWhite = blackOrWhiteList;
            SrtpTimeout = srtpTimeoutInTicks;
            SrtpRateLimiting = srtpRateLimiting;
        }
        /// <summary>
        /// Starts this server for listening to any incoming requests.
        /// </summary>
        /// <exception cref="InvalidOperationException">The configuration is invalid: <see cref="ResourceProvider"/> isn't set,
        /// <see cref="PacketMaxLength"/> is not 1 to <see cref="SRTPClient.MaxMessageLength"/>, the ConnectionID range is empty,
        /// the SRTP timeout is not positive or the SRTP rate limiting is negative</exception>
        public void Start()
        {
            // the configuration is checked once here, instead of failing in the middle of the first transfer
            if (ResourceProvider == null)
            {
                throw new InvalidOperationException("ResourceProvider must be set before starting the LMDTP server");
            }
            if (PacketMaxLength < 1 || PacketMaxLength > SRTPClient.MaxMessageLength)
            {
                throw new InvalidOperationException($"PacketMaxLength must be 1 to {SRTPClient.MaxMessageLength}, but it is {PacketMaxLength}");
            }
            if (ConnectionIDRangeStart > ConnectionIDRangeEnd)
            {
                throw new InvalidOperationException($"the ConnectionID range {ConnectionIDRangeStart}-{ConnectionIDRangeEnd} is empty");
            }
            if (SrtpTimeout <= 0)
            {
                throw new InvalidOperationException($"the SRTP timeout must be more than 0 ticks, but it is {SrtpTimeout}");
            }
            if (SrtpRateLimiting < 0)
            {
                throw new InvalidOperationException($"the SRTP rate limiting must not be negative, but it is {SrtpRateLimiting}");
            }
            if (MaxSessions < 1 || MaxSessionsPerAddress < 1)
            {
                throw new InvalidOperationException("MaxSessions and MaxSessionsPerAddress must be at least 1");
            }
            lock (StartLock)
            {
                if (RequestListener != null)
                {
                    throw new InvalidOperationException("this LMDTP server is already started");
                }
                RequestListener = OnRequestPacket;
                BaseModem.AttachReceiveEventNoUnfragment(RequestListener);
            }
        }
        /// <summary>
        /// Stops taking requests and ends the sessions in progress. The server can be started again.
        /// </summary>
        public void Stop()
        {
            lock (StartLock)
            {
                if (RequestListener == null)
                {
                    return;
                }
                BaseModem.DetachReceiveEventNoUnfragment(RequestListener);
                RequestListener = null;
            }
            foreach (KeyValuePair<(Address, uint), SRTPClient> session in Clients)
            {
                EndSession(session.Key, session.Value);
            }
        }
        private void OnRequestPacket(Packet p, Action k)
        {
            try
            {
                //Console.WriteLine($"Received net data: address={p.Transmitter} qt={p.QueryType} cid={p.ConnectionID}");
                // this runs for every packet of every session, so only a new LMDTP request may get past these cheap checks
                // (clients send nothing but acknowledgements in a session, and those never start with the request prefix)
                if (ConnectionIDRangeStart > p.ConnectionID || p.ConnectionID > ConnectionIDRangeEnd ||
                    p.QueryType != "lm_tunnel" ||
                    !string.IsNullOrEmpty(p.Metadata) ||
                    p.DataBytes.Length < 2 ||
                    BinaryPrimitives.ReadUInt16BigEndian(p.DataBytes) != LMDTPClient.LMDTP_REQUEST_PREFIX)
                {
                    return;
                }
                if (!CheckGrayList(p.Transmitter))
                {
                    ErrorOccured(new UnauthorizedAccessException($"User from remote address {p.Transmitter} was trying to access this LMDTP while he is {(IsListBlackOrWhite ? "not included in White" : "is included in Black")} List"));
                    return;
                }
                (Address, uint) key = (p.Transmitter, p.ConnectionID);
                bool hasRequestId = TryReadRequestId(p.DataBytes, out uint requestId);
                if (hasRequestId && WasRequestSeen(key, requestId))
                {
                    // the same request again: a duplicate on the way or a late copy; it is answered already (or being answered)
                    return;
                }
                if (!Clients.ContainsKey(key))
                {
                    if (Clients.Count >= MaxSessions)
                    {
                        ErrorOccured(new InvalidOperationException($"LMDTP request from {p.Transmitter} is ignored: the server already has {MaxSessions} sessions (MaxSessions)"));
                        return;
                    }
                    int sessionsOfAddress = 0;
                    foreach ((Address, uint) existing in Clients.Keys)
                    {
                        if (existing.Item1.Equals(p.Transmitter))
                        {
                            sessionsOfAddress++;
                        }
                    }
                    if (sessionsOfAddress >= MaxSessionsPerAddress)
                    {
                        ErrorOccured(new InvalidOperationException($"LMDTP request from {p.Transmitter} is ignored: this address already has {MaxSessionsPerAddress} sessions (MaxSessionsPerAddress)"));
                        return;
                    }
                }
                SRTPClient session = new(BaseModem, p.Transmitter, "lm_tunnel", p.ConnectionID, SrtpTimeout);
                session.PacketLossBasedCongestionControl = SrtpEnablePacketLossBasedRateLimiter;
                session.TicksPacketDelay = SrtpRateLimiting;
                session.RetransmissionsBeforeConsideringRemoteSideDead = 10;
                session.OnError += delegate (int code, string desc)
                {
                    ErrorOccured(new Exception($"SRTP ERRNO {code}: {desc}"));
                };
                // A new request from the same address and ConnectionID means the client is done with the previous one: it gave up,
                // or it finished and asked again before the server saw its last acknowledgement. The old session ends and the new
                // one takes its place. Sessions are created here on the modem's thread, so requests are taken in the order they came.
                // remembered only now: a request refused by the limits above hasn't been answered
                if (hasRequestId)
                {
                    RememberRequestId(key, requestId);
                }
                if (Clients.TryGetValue(key, out SRTPClient? previous))
                {
                    EndSession(key, previous);
                }
                Clients[key] = session;
                Task.Run(delegate ()
                {
                    try
                    {
                        SRTPClientReceived(p.Transmitter, p.ConnectionID);
                    }
                    catch (Exception ex)
                    {
                        ErrorOccured(ex);
                    }
                    HandleClient(p.Transmitter, p.ConnectionID, session, p.DataBytes);
                });
                //Console.WriteLine("Processed all net data");
            }
            catch (Exception ex)
            {
                ErrorOccured(ex);
            }
        }
        /// <summary>
        /// Ends a session, that didn't finish by itself (a newer request took its place, or the server stops): its end is not reported as a timeout.
        /// </summary>
        private void EndSession((Address, uint) key, SRTPClient session)
        {
            // marked before it leaves Clients, so the cleanup of HandleClient can't run in between and leave the mark behind
            ReplacedSessions.TryAdd(session, 0);
            if (Clients.TryRemove(new KeyValuePair<(Address, uint), SRTPClient>(key, session)))
            {
                session.Close();
            }
            else
            {
                // the session ended by itself in the meantime
                ReplacedSessions.TryRemove(session, out _);
            }
        }
        /// <summary>
        /// Reads the request ID, that v2 clients put after the hash algorithms byte. Older clients don't send it, and 0 means "no ID" too.
        /// </summary>
        private static bool TryReadRequestId(byte[] data, out uint requestId)
        {
            requestId = 0;
            if (data.Length < 4)
            {
                return false;
            }
            int offset = 4 + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)) + 8 + 1 + 1;
            if (data.Length < offset + 4)
            {
                return false;
            }
            requestId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            return requestId != 0;
        }
        /// <summary>
        /// Tells if a request of a client was seen (and answered or refused) already.
        /// </summary>
        private bool WasRequestSeen((Address, uint) key, uint requestId)
        {
            lock (RecentRequestIdsLock)
            {
                return RecentRequestIds.TryGetValue(key, out (Queue<uint> Ids, long LastSeen) recent) && recent.Ids.Contains(requestId);
            }
        }
        /// <summary>
        /// Remembers a request ID of a client.
        /// </summary>
        private void RememberRequestId((Address, uint) key, uint requestId)
        {
            long now = Stopwatch.GetTimestamp();
            lock (RecentRequestIdsLock)
            {
                if (!RecentRequestIds.TryGetValue(key, out (Queue<uint> Ids, long LastSeen) recent))
                {
                    if (RecentRequestIds.Count >= MaxClientsWithRequestIds)
                    {
                        // a hard limit: the quarter, that asked longest ago, is forgotten in one go, so this happens rarely
                        foreach (KeyValuePair<(Address, uint), (Queue<uint> Ids, long LastSeen)> old in RecentRequestIds.OrderBy(kv => kv.Value.LastSeen).Take(MaxClientsWithRequestIds / 4).ToArray())
                        {
                            RecentRequestIds.Remove(old.Key);
                        }
                    }
                    recent = (new Queue<uint>(), now);
                }
                recent.Ids.Enqueue(requestId);
                if (recent.Ids.Count > RequestIdsPerClient)
                {
                    recent.Ids.Dequeue();
                }
                RecentRequestIds[key] = (recent.Ids, now);
            }
        }
        private bool CheckGrayList(Address address)
        {
            return GrayList.Contains(address) == IsListBlackOrWhite;
        }
        private byte[] ConstructResponseHeader(LMDTPResponseFlags? flags, long? reslen, byte[]? hash)
        {
            Span<byte> bytes = stackalloc byte[76];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, LMDTPClient.LMDTP_RESPONSE_PREFIX);
            BinaryPrimitives.WriteUInt16BigEndian(bytes[2..], (ushort)(flags ?? LMDTPResponseFlags.ServerSideError));
            // the hash field is 64 bytes: SHA-512 fills it, shorter hashes take its beginning and the rest stays zero
            bytes.Slice(4, 64).Clear();
            hash?.AsSpan(0, Math.Min(hash.Length, 64)).CopyTo(bytes[4..]);
            BinaryPrimitives.WriteInt64BigEndian(bytes[68..], reslen ?? 0);
            return bytes.ToArray();
        }
        /// <summary>
        /// Computes the hash of a resource the way LMDTP sends it, for <see cref="IResourceProvider.GetResourceHash"/> implementations.
        /// Reads the stream to its end.
        /// </summary>
        /// <param name="resource">Resource data</param>
        /// <param name="algorithm">One of the algorithms</param>
        /// <returns>The hash: 64 bytes for SHA-512, 8 bytes (big-endian) for XxHash3</returns>
        public static byte[] ComputeResourceHash(Stream resource, LMDTPHashAlgorithms algorithm)
        {
            if (algorithm == LMDTPHashAlgorithms.Sha512)
            {
                return SHA512.HashData(resource);
            }
            if (algorithm == LMDTPHashAlgorithms.XxHash3)
            {
                XxHash3 hash = new();
                hash.Append(resource);
                return hash.GetCurrentHash();
            }
            throw new ArgumentOutOfRangeException(nameof(algorithm), "exactly one supported algorithm must be given");
        }
        /// <summary>
        /// Parses and validates a request. Every field is checked against the length of the request before it is read.
        /// </summary>
        /// <returns>true if the request is well-formed, otherwise false and the reason in error</returns>
        private static bool TryParseRequest(byte[] data, out string resourceName, out long maxDownloadBytes, out LMDTPRequestType requestType, out LMDTPHashAlgorithms clientHashes, out string? error)
        {
            resourceName = "";
            maxDownloadBytes = 0;
            requestType = 0;
            clientHashes = LMDTPHashAlgorithms.Sha512;
            // prefix (2), resource name length (2), resource name, maximum download length (8), request type (1), [v2: hash algorithms (1)]
            const int fixedLength = 2 + 2 + 8 + 1;
            if (data.Length < fixedLength)
            {
                error = $"the request is {data.Length} bytes long, but even with an empty resource name it takes {fixedLength} bytes";
                return false;
            }
            if (BinaryPrimitives.ReadUInt16BigEndian(data) != LMDTPClient.LMDTP_REQUEST_PREFIX)
            {
                error = "the request prefix doesn't match the spec";
                return false;
            }
            int resourceNameLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
            if (data.Length < fixedLength + resourceNameLength)
            {
                error = $"the resource name is {resourceNameLength} bytes long, but the request is only {data.Length} bytes long";
                return false;
            }
            try
            {
                resourceName = StrictUtf8.GetString(data, 4, resourceNameLength);
            }
            catch (DecoderFallbackException)
            {
                error = "the resource name is not valid UTF-8";
                return false;
            }
            maxDownloadBytes = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(4 + resourceNameLength));
            requestType = (LMDTPRequestType)data[4 + resourceNameLength + 8];
            // newer clients append a byte with the hash algorithms they support, older ones support only SHA-512;
            // bits of algorithms unknown to this server are ignored
            if (data.Length > fixedLength + resourceNameLength)
            {
                clientHashes = (LMDTPHashAlgorithms)data[fixedLength + resourceNameLength] & (LMDTPHashAlgorithms.Sha512 | LMDTPHashAlgorithms.XxHash3);
            }
            error = null;
            return true;
        }
        /// <summary>
        /// Sends a response, that has only the header, and waits until the client receives it.
        /// </summary>
        private void SendHeaderOnly(SRTPClient client, LMDTPResponseFlags flags)
        {
            Send(client, ConstructResponseHeader(flags, 0, null));
            WaitAcknowledged(client);
        }
        /// <summary>
        /// The SRTP session of a request ended: the remote side stopped answering, or the session was closed.
        /// </summary>
        private sealed class SessionEndedException(Exception inner) : Exception("the SRTP session of the request ended", inner);
        private static void Send(SRTPClient client, byte[] message)
        {
            try
            {
                client.Transmit(message);
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or InvalidOperationException)
            {
                throw new SessionEndedException(e);
            }
        }
        private static void WaitAcknowledged(SRTPClient client)
        {
            try
            {
                client.WaitForAcknowledgements();
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or InvalidOperationException)
            {
                throw new SessionEndedException(e);
            }
        }
        private void HandleClient(Address transmitter, uint connectid, SRTPClient tempClient, byte[] data)
        {
            Stream? stream = null;
            bool headerSent = false;
            bool processed = false;
            string resourceName = "";
            try
            {
                //tempClient.OnAckReceived += delegate (long p) { Console.WriteLine("ack lmdtp server"); };
                //tempClient.OnRetransmit += delegate (long p) { Console.WriteLine("retx lmdtp server"); };
                if (!TryParseRequest(data, out resourceName, out long maxDownloadBytes, out LMDTPRequestType requestType, out LMDTPHashAlgorithms clientHashes, out string? parseError))
                {
                    // not a request this server can understand, so there's nothing to answer
                    ErrorOccured(new InvalidDataException($"Malformed LMDTP request from {transmitter} with ConnectionID {connectid}: {parseError}"));
                    return;
                }
                IResourceProvider? provider = ResourceProvider;
                int packetMaxLength = PacketMaxLength;
                if (provider == null || packetMaxLength < 1 || packetMaxLength > SRTPClient.MaxMessageLength)
                {
                    headerSent = true;
                    SendHeaderOnly(tempClient, LMDTPResponseFlags.ServerSideError);
                    ErrorOccured(new InvalidOperationException($"LMDTP server is misconfigured: ResourceProvider must be set and PacketMaxLength must be 1 to {SRTPClient.MaxMessageLength}"));
                    return;
                }
                if (requestType != LMDTPRequestType.Obtain && requestType != LMDTPRequestType.Hash)
                {
                    headerSent = true;
                    SendHeaderOnly(tempClient, LMDTPResponseFlags.ServerSideError);
                    ErrorOccured(new InvalidDataException($"LMDTP request from {transmitter} has an unknown request type 0x{(byte)requestType:x2}"));
                    return;
                }
                LMDTPResponseFlags responseFlags = provider.GetResourceResponseFlags(resourceName);
                const LMDTPResponseFlags errorFlags = LMDTPResponseFlags.LargeMessageNotFound | LMDTPResponseFlags.LargeMessageExceededMTU |
                    LMDTPResponseFlags.ServerSideError | LMDTPResponseFlags.NotYourLargeMessage | LMDTPResponseFlags.ServerHardDriveFailed;
                if ((responseFlags & errorFlags) != 0)
                {
                    // the provider refuses this resource: tell the client why, without any data
                    headerSent = true;
                    SendHeaderOnly(tempClient, responseFlags & errorFlags);
                    return;
                }
                long resourceSize = provider.GetResourceSize(resourceName);
                if (resourceSize < 0)
                {
                    headerSent = true;
                    SendHeaderOnly(tempClient, LMDTPResponseFlags.ServerSideError);
                    ErrorOccured(new InvalidDataException($"Resource Provider returned a negative size ({resourceSize}) for \"{resourceName}\""));
                    return;
                }
                if (resourceSize > maxDownloadBytes)
                {
                    headerSent = true;
                    SendHeaderOnly(tempClient, LMDTPResponseFlags.LargeMessageExceededMTU);
                    ErrorOccured(new LargeMessageTooBigException("Resource Provider said there is more bytes, then client is proceeding to download"));
                    return;
                }
                // send the response
                switch (requestType)
                {
                    case LMDTPRequestType.Obtain:
                        stream = provider.GetResource(resourceName);
                        if (stream == null || !stream.CanRead)
                        {
                            headerSent = true;
                            SendHeaderOnly(tempClient, LMDTPResponseFlags.ServerSideError);
                            ErrorOccured(new ArgumentNullException(nameof(stream), "Resource Provider must return a valid non-null readable stream object"));
                            return;
                        }
                        byte[]? resourceHash = null;
                        if ((clientHashes & LMDTPHashAlgorithms.XxHash3) != 0)
                        {
                            resourceHash = provider.GetResourceHash(resourceName, LMDTPHashAlgorithms.XxHash3);
                            if (resourceHash != null && resourceHash.Length == 8)
                            {
                                responseFlags |= LMDTPResponseFlags.HashIsXxHash3;
                            }
                            else
                            {
                                resourceHash = null;
                            }
                        }
                        resourceHash ??= provider.GetResourceSha512Hashsum(resourceName);
                        if (resourceHash == null || ((responseFlags & LMDTPResponseFlags.HashIsXxHash3) == 0 && resourceHash.Length != 64))
                        {
                            headerSent = true;
                            SendHeaderOnly(tempClient, LMDTPResponseFlags.ServerSideError);
                            ErrorOccured(new InvalidDataException($"Resource Provider must return a 64-byte SHA-512 of \"{resourceName}\""));
                            return;
                        }
                        headerSent = true;
                        Send(tempClient, ConstructResponseHeader(responseFlags, resourceSize, resourceHash));
                        // exactly the announced length is sent, the client fails on anything else
                        byte[] buffer = new byte[packetMaxLength];
                        long remaining = resourceSize;
                        while (remaining > 0)
                        {
                            int bytesRead = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (bytesRead == 0)
                            {
                                throw new EndOfStreamException($"Resource Provider said \"{resourceName}\" is {resourceSize} bytes long, but its stream ended after {resourceSize - remaining} bytes");
                            }
                            if (bytesRead < buffer.Length)
                            {
                                // last chunk can be not full
                                byte[] lastChunk = new byte[bytesRead];
                                Array.Copy(buffer, lastChunk, bytesRead);
                                Send(tempClient, lastChunk);
                            }
                            else
                            {
                                Send(tempClient, buffer);
                            }
                            remaining -= bytesRead;
                        }
                        break;
                    case LMDTPRequestType.Hash:
                        byte[]? hash = provider.GetResourceSha512Hashsum(resourceName);
                        if (hash == null || hash.Length != 64)
                        {
                            headerSent = true;
                            SendHeaderOnly(tempClient, LMDTPResponseFlags.ServerSideError);
                            ErrorOccured(new InvalidDataException($"Resource Provider must return a 64-byte SHA-512 of \"{resourceName}\""));
                            return;
                        }
                        byte[] hashhash = SHA512.HashData(hash);
                        headerSent = true;
                        Send(tempClient, ConstructResponseHeader(responseFlags, 64, hashhash));
                        Send(tempClient, hash);
                        break;
                }
                // with a big window a lot of data may still be unacknowledged, and closing now would stop its retransmission
                WaitAcknowledged(tempClient);
                processed = true;
            }
            catch (SessionEndedException)
            {
                // a session replaced by a newer request (or ended by Stop) didn't time out
                if (!ReplacedSessions.ContainsKey(tempClient))
                {
                    try
                    {
                        SRTPClientTransmitTimedOut(transmitter, connectid);
                    }
                    catch (Exception ex)
                    {
                        ErrorOccured(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorOccured(ex);
                if (!headerSent)
                {
                    // the client is still waiting for the header: let it fail right away instead of after its timeout
                    try
                    {
                        SendHeaderOnly(tempClient, LMDTPResponseFlags.ServerSideError);
                    }
                    catch (SessionEndedException)
                    {
                        // the client is gone anyway
                    }
                    catch (Exception sendError)
                    {
                        ErrorOccured(sendError);
                    }
                }
            }
            finally
            {
                // whatever happened, the session is over: a client with the same address and ConnectionID can make a new request
                stream?.Dispose();
                tempClient.Close();
                // only this session is removed: a newer request may have taken the key already
                Clients.TryRemove(new KeyValuePair<(Address, uint), SRTPClient>((transmitter, connectid), tempClient));
                ReplacedSessions.TryRemove(tempClient, out _);
            }
            if (processed)
            {
                try
                {
                    RequestSuccessfullyProcessed(transmitter, connectid, resourceName);
                }
                catch (Exception ex)
                {
                    ErrorOccured(ex);
                }
            }
        }
    }
}
