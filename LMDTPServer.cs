using System.Buffers.Binary;
using System.Collections.Concurrent;
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
        private bool IsListBlackOrWhite;
        private uint ConnectionIDRangeStart;
        private uint ConnectionIDRangeEnd;
        private int SrtpTimeout = 0;
        private int SrtpRateLimiting = 0;
        /// <summary>
        /// Resource provider, that must be set before starting the server, and when a request comes, this Resource Provider implementation is supposed to give the data of the response.
        /// </summary>
        public IResourceProvider? ResourceProvider { get; set; }
        /// <summary>
        /// Gets invoked once an Exception occurs in the depths of LMDTP Server.
        /// </summary>
        public event Action<Exception> ErrorOccured = delegate { };
        /// <summary>
        /// Gets or sets maximum length of the packet used in response transmission (default is 59992, the most an SRTP packet can carry).
        /// Bigger packets mean fewer packets and acknowledgements per byte, which is what limits the speed.
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
        public void Start()
        {
            BaseModem.AttachReceiveEventNoUnfragment(delegate (Packet p, Action k)
            {
                //Console.WriteLine($"Received net data: address={p.Transmitter} qt={p.QueryType} cid={p.ConnectionID}");
                // this runs for every packet of every session, so only a new LMDTP request may get past these cheap checks
                if (ConnectionIDRangeStart > p.ConnectionID || p.ConnectionID > ConnectionIDRangeEnd ||
                    p.QueryType != "lm_tunnel" ||
                    p.DataBytes.Length < 2 ||
                    BinaryPrimitives.ReadUInt16BigEndian(p.DataBytes) != LMDTPClient.LMDTP_REQUEST_PREFIX ||
                    Clients.ContainsKey((p.Transmitter, p.ConnectionID)))
                {
                    return;
                }
                if (!CheckGrayList(p.Transmitter))
                {
                    ErrorOccured(new UnauthorizedAccessException($"User from remote address {p.Transmitter} was trying to access this LMDTP while he is {(IsListBlackOrWhite ? "not included in White" : "is included in Black")} List"));
                    return;
                }
                Task.Run(delegate ()
                {
                    SRTPClient temp = new(BaseModem, p.Transmitter, "lm_tunnel", p.ConnectionID, SrtpTimeout);
                    temp.PacketLossBasedCongestionControl = SrtpEnablePacketLossBasedRateLimiter;
                    temp.TicksPacketDelay = SrtpRateLimiting;
                    temp.RetransmissionsBeforeConsideringRemoteSideDead = 10;
                    temp.OnError += delegate (int code, string desc)
                    {
                        ErrorOccured(new Exception($"SRTP ERRNO {code}: {desc}"));
                    };
                    if (Clients.TryAdd((p.Transmitter, p.ConnectionID), temp))
                    {
                        SRTPClientReceived(p.Transmitter, p.ConnectionID);
                        HandleClient(p.Transmitter, p.ConnectionID, p.DataBytes);
                    }
                    else
                    {
                        // the same request was already taken by another task, this client must not stay subscribed to the modem
                        temp.Close();
                    }
                });
                //Console.WriteLine("Processed all net data");
            });
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
        private void HandleClient(Address transmitter, uint connectid, byte[] data)
        {
            try
            {
                if (Clients.TryGetValue((transmitter, connectid), out SRTPClient? tempClient))
                {
                    Stream? stream = null;
                    try
                    {
                        //tempClient.OnAckReceived += delegate (long p) { Console.WriteLine("ack lmdtp server"); };
                        //tempClient.OnRetransmit += delegate (long p) { Console.WriteLine("retx lmdtp server"); };
                        Span<byte> bytes = new(data);
                        if (BinaryPrimitives.ReadUInt16BigEndian(bytes) != LMDTPClient.LMDTP_REQUEST_PREFIX)
                        {
                            return;
                        }
                        ushort resourceNameLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
                        string resourceName = Encoding.UTF8.GetString(bytes.Slice(4, resourceNameLength));
                        LMDTPRequestType requestType = (LMDTPRequestType)bytes[2 + 2 + resourceNameLength + 8];
                        // newer clients append a byte with the hash algorithms they support, older ones support only SHA-512
                        LMDTPHashAlgorithms clientHashes = bytes.Length > 2 + 2 + resourceNameLength + 8 + 1
                            ? (LMDTPHashAlgorithms)bytes[2 + 2 + resourceNameLength + 8 + 1]
                            : LMDTPHashAlgorithms.Sha512;
                        if (ResourceProvider?.GetResourceSize(resourceName) > BinaryPrimitives.ReadInt64BigEndian(bytes[(2 + 2 + resourceNameLength)..]))
                        {
                            tempClient.Transmit(ConstructResponseHeader(LMDTPResponseFlags.LargeMessageExceededMTU, 0, new byte[64]));
                            tempClient.Close();
                            Clients.TryRemove((transmitter, connectid), out _);
                            ErrorOccured(new LargeMessageTooBigException("Resource Provider said there is more bytes, then client is proceeding to download"));
                            return;
                        }
                        // send the response
                        switch (requestType)
                        {
                            case LMDTPRequestType.Obtain:
                                stream = ResourceProvider?.GetResource(resourceName);
                                if (stream == null)
                                {
                                    tempClient.Transmit(ConstructResponseHeader(LMDTPResponseFlags.ServerSideError, 0, new byte[64]));
                                    tempClient.Close();
                                    Clients.TryRemove((transmitter, connectid), out _);
                                    ErrorOccured(new ArgumentNullException(nameof(stream), "Resource Provider must return a valid non-null stream object"));
                                    return;
                                }
                                LMDTPResponseFlags? responseFlags = ResourceProvider?.GetResourceResponseFlags(resourceName);
                                byte[]? resourceHash = null;
                                if ((clientHashes & LMDTPHashAlgorithms.XxHash3) != 0)
                                {
                                    resourceHash = ResourceProvider?.GetResourceHash(resourceName, LMDTPHashAlgorithms.XxHash3);
                                    if (resourceHash != null)
                                    {
                                        responseFlags |= LMDTPResponseFlags.HashIsXxHash3;
                                    }
                                }
                                resourceHash ??= ResourceProvider?.GetResourceSha512Hashsum(resourceName);
                                tempClient.Transmit(ConstructResponseHeader(responseFlags, ResourceProvider?.GetResourceSize(resourceName), resourceHash));
                                byte[] buffer = new byte[PacketMaxLength];
                                int bytesRead;
                                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    if (bytesRead < buffer.Length)
                                    {
                                        // last chunk can be not full
                                        byte[] lastChunk = new byte[bytesRead];
                                        Array.Copy(buffer, lastChunk, bytesRead);
                                        tempClient.Transmit(lastChunk);
                                    }
                                    else
                                    {
                                        tempClient.Transmit(buffer);
                                    }
                                }
                                break;
                            case LMDTPRequestType.Hash:
                                byte[]? hash = ResourceProvider?.GetResourceSha512Hashsum(resourceName);
                                if (hash == null)
                                {
                                    break;
                                }
                                byte[] hashhash = SHA512.HashData(hash);
                                tempClient.Transmit(ConstructResponseHeader(ResourceProvider?.GetResourceResponseFlags(resourceName), 64, hashhash));
                                tempClient.Transmit(hash);
                                break;
                        }
                        // close this client
                        //Console.WriteLine("Currently there are clients: " + Clients.Count);
                        //Console.WriteLine("LMDTP server closed");
                        stream?.Dispose();
                        // with a big window a lot of data may still be unacknowledged, and closing now would stop its retransmission
                        tempClient.WaitForAcknowledgements();
                        tempClient.Close();
                        Clients.TryRemove((transmitter, connectid), out _);
                        //Console.WriteLine("And now there's clients: " + Clients.Count);
                        RequestSuccessfullyProcessed(transmitter, connectid, resourceName);
                    }
                    catch (OperationCanceledException)
                    {
                        tempClient.Close();
                        Clients.TryRemove((transmitter, connectid), out _);
                        SRTPClientTransmitTimedOut(transmitter, connectid);
                    }
                    catch (InvalidOperationException)
                    {
                        tempClient.Close();
                        Clients.TryRemove((transmitter, connectid), out _);
                        SRTPClientTransmitTimedOut(transmitter, connectid);
                    }
                    catch (TimeoutException)
                    {
                        tempClient.Close();
                        Clients.TryRemove((transmitter, connectid), out _);
                        SRTPClientTransmitTimedOut(transmitter, connectid);
                    }
                    catch (Exception ex)
                    {
                        ErrorOccured(ex);
                    }
                    finally
                    {
                        stream?.Dispose();
                    }
                }
                else
                {
                    ErrorOccured(new KeyNotFoundException("Client was not found in the server's client list"));
                }
            }
            catch (Exception ex)
            {
                ErrorOccured(ex);
            }
        }
    }
}
