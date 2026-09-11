using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;

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
        /// Gets or sets maximum length of the packet used in response transmission (default is 8000).
        /// </summary>
        public int PacketMaxLength = 8000;
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
                if (CheckGrayList(p.Transmitter))
                {
                    if (Clients.ContainsKey((p.Transmitter, p.ConnectionID)))
                    {
                        return;
                    }
                    SRTPClient temp = new(BaseModem, p.Transmitter, "lm_tunnel", p.ConnectionID, SrtpTimeout);
                    temp.TicksPacketDelay = SrtpRateLimiting;
                    if (Clients.TryAdd((p.Transmitter, p.ConnectionID), temp))
                    {
                        HandleClient(p.Transmitter, p.ConnectionID);
                    }
                    else
                    {
                        //Console.WriteLine($"Failed to add client from adddress {p.Transmitter} and Cid {p.ConnectionID}");
                    }
                }
                else
                {
                    ErrorOccured(new UnauthorizedAccessException($"User from remote address {p.Transmitter} was trying to access this LMDTP while he is {(IsListBlackOrWhite ? "not included in White" : "is included in Black")} List"));
                }
                //Console.WriteLine("Processed all net data");
            });
        }
        private bool CheckGrayList(Address address)
        {
            return GrayList.Contains(address) == IsListBlackOrWhite;
        }
        private byte[] ConstructResponseHeader(LMDTPResponseFlags? flags, long? reslen, byte[]? sha512)
        {
            Span<byte> bytes = stackalloc byte[76];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, LMDTPClient.LMDTP_RESPONSE_PREFIX);
            BinaryPrimitives.WriteUInt16BigEndian(bytes[2..], (ushort)(flags ?? LMDTPResponseFlags.ServerSideError));
            sha512?.AsSpan().CopyTo(bytes[4..]);
            if (sha512 == null)
            {
                bytes.Slice(4, 64).Clear();
            }
            BinaryPrimitives.WriteInt64BigEndian(bytes[68..], reslen ?? 0);
            return bytes.ToArray();
        }
        private void HandleClient(Address transmitter, uint connectid)
        {
            try
            {
                if (Clients.TryGetValue((transmitter, connectid), out SRTPClient? tempClient))
                {
                    tempClient.OnMessageReceived += delegate (SRTPClient sender, byte[] data)
                    {
                        try
                        {
                            Span<byte> bytes = new(data);
                            if (BinaryPrimitives.ReadUInt16BigEndian(bytes) != LMDTPClient.LMDTP_REQUEST_PREFIX)
                            {
                                sender.Close();
                                Clients.TryRemove((transmitter, connectid), out _);
                                ErrorOccured(new InvalidDataException($"LMDTP Request Prefix does not match (expected: 0x{LMDTPClient.LMDTP_REQUEST_PREFIX:X4}, received: 0x{bytes[0]:X2}{bytes[1]:X2})"));
                                return;
                            }
                            ushort resourceNameLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
                            string resourceName = Encoding.UTF8.GetString(bytes.Slice(4, resourceNameLength));
                            if (ResourceProvider?.GetResourceSize(resourceName) > BinaryPrimitives.ReadInt64BigEndian(bytes[(2 + 2 + resourceNameLength)..]))
                            {
                                sender.Transmit(ConstructResponseHeader(LMDTPResponseFlags.LargeMessageExceededMTU, 0, new byte[64]));
                                sender.Close();
                                Clients.TryRemove((transmitter, connectid), out _);
                                ErrorOccured(new LargeMessageTooBigException("Resource Provider said there is more bytes, then client is proceeding to download"));
                                return;
                            }
                            // send the response
                            Stream? stream = ResourceProvider?.GetResource(resourceName);
                            if (stream == null)
                            {
                                sender.Transmit(ConstructResponseHeader(LMDTPResponseFlags.ServerSideError, 0, new byte[64]));
                                sender.Close();
                                Clients.TryRemove((transmitter, connectid), out _);
                                ErrorOccured(new ArgumentNullException(nameof(stream), "Resource Provider must return a valid non-null stream object"));
                                return;
                            }
                            sender.Transmit(ConstructResponseHeader(LMDTPResponseFlags.Success, ResourceProvider?.GetResourceSize(resourceName), ResourceProvider?.GetResourceSha512Hashsum(resourceName)));
                            Span<byte> packetBuffer = new byte[PacketMaxLength];
                            byte[] buffer = new byte[PacketMaxLength];
                            int bytesRead;
                            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (bytesRead < buffer.Length)
                                {
                                    // last chunk can be not full
                                    byte[] lastChunk = new byte[bytesRead];
                                    Array.Copy(buffer, lastChunk, bytesRead);
                                    sender.Transmit(lastChunk);
                                }
                                else
                                {
                                    sender.Transmit(buffer);
                                }
                            }
                            // close this client
                            //Console.WriteLine("Currently there are clients: " + Clients.Count);
                            //Console.WriteLine("LMDTP server closed");
                            stream.Dispose();
                            sender.Close();
                            Clients.TryRemove((transmitter, connectid), out _);
                            //Console.WriteLine("And now there's clients: " + Clients.Count);
                        }
                        catch (Exception ex)
                        {
                            ErrorOccured(ex);
                        }
                    };
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
