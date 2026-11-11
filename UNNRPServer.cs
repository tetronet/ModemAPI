namespace ModemAPI
{
    /// <summary>
    /// Answers the requests of UNNRP (User Name Network Resolve Protocol) clients: maps short names of nodes (UNAMEs) to tetronet addresses
    /// and back, like DNS. With <see cref="RecordNameSpaceServer"/> the servers form a network and redirect the clients to each other.
    /// Like every server class, it doesn't dial or drop the modem: the modem has to be connected to the tetronet before <see cref="Start"/>.
    /// </summary>
    /// <remarks>
    /// Every request and every response is one tetronet packet without encryption; there are no sessions. Records can be added
    /// while the server is running.
    /// </remarks>
    public class UNNRPServer
    {
        private readonly IModem Modem;
        private readonly Lock StartLock = new();
        private readonly Lock RecordsLock = new();
        private Action<Packet, Action>? Listener = null;
        // UNAMEs and wildcards ("*.retalog.com") share one namespace, '*' is never in a UNAME
        private readonly Dictionary<string, IResolveRecord> Names = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RecordAfTnetToUname> Pointers = new(StringComparer.Ordinal);
        private readonly Dictionary<UInt128, RecordVideoNetwork> Videonet = [];

        /// <summary>
        /// Gets invoked for errors of sending the responses.
        /// </summary>
        public event Action<Exception> ErrorOccured = delegate { };

        /// <summary>
        /// Creates a new UNNRP server without records.
        /// </summary>
        /// <param name="modem">Modem, through which the server takes the requests; this server doesn't dial or drop it</param>
        public UNNRPServer(IModem modem)
        {
            ArgumentNullException.ThrowIfNull(modem);
            Modem = modem;
        }

        /// <summary>
        /// Adds a record. What a record takes depends on its type: <see cref="RecordUnameToAfTnet"/>, <see cref="RecordUnameToArrayTnet"/>,
        /// <see cref="RecordRegister"/> take their UNAME and <see cref="RecordNameSpaceServer"/> its wildcard, all of them in one namespace,
        /// where names are compared without regard to case; <see cref="RecordAfTnetToUname"/> takes its tetronet address,
        /// <see cref="RecordVideoNetwork"/> its videonet address. So a name and the reverse record of its address can be on one server.
        /// </summary>
        /// <exception cref="ArgumentNullException">record is null</exception>
        /// <exception cref="ArgumentException">record isn't one of the record classes of this library</exception>
        /// <exception cref="InvalidOperationException">What the record takes is taken already by another record</exception>
        public void AddRecord(IResolveRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            lock (RecordsLock)
            {
                switch (record)
                {
                    case RecordUnameToAfTnet or RecordUnameToArrayTnet or RecordRegister or RecordNameSpaceServer:
                        if (!Names.TryAdd(record.Key, record))
                        {
                            throw new InvalidOperationException($"the name '{record.Key}' is taken already");
                        }
                        break;
                    case RecordAfTnetToUname pointer:
                        if (!Pointers.TryAdd(pointer.Key, pointer))
                        {
                            throw new InvalidOperationException($"the address {pointer.Key} has a name already");
                        }
                        break;
                    case RecordVideoNetwork video:
                        if (!Videonet.TryAdd(video.VideonetAddress, video))
                        {
                            throw new InvalidOperationException($"the videonet address {video.Key} is taken already");
                        }
                        break;
                    default:
                        throw new ArgumentException($"{record.GetType()} isn't a record of UNNRP, only the record classes of ModemAPI can be added", nameof(record));
                }
            }
        }

        /// <summary>
        /// Starts taking requests. After <see cref="Stop"/> the server can be started again.
        /// </summary>
        /// <exception cref="ModemNotConnectedException">The modem isn't connected to the tetronet</exception>
        /// <exception cref="InvalidOperationException">The server is started already</exception>
        public void Start()
        {
            if (!Modem.IsModemConnected)
            {
                throw new ModemNotConnectedException("the modem of the UNNRP server isn't connected to the tetronet");
            }
            lock (StartLock)
            {
                if (Listener != null)
                {
                    throw new InvalidOperationException("this UNNRP server is already started");
                }
                Listener = OnPacket;
                Modem.AttachReceiveEventNoUnfragment(Listener);
            }
        }

        /// <summary>
        /// Stops taking requests. The server can be started again.
        /// </summary>
        public void Stop()
        {
            lock (StartLock)
            {
                if (Listener == null)
                {
                    return;
                }
                Modem.DetachReceiveEventNoUnfragment(Listener);
                Listener = null;
            }
        }

        private void OnPacket(Packet p, Action k)
        {
            // responses (0x50) are never answered, so two servers can't talk to each other forever
            if (p.QueryType != UNNRPProtocol.QueryType || p.DataBytes.Length < 2 || p.DataBytes[0] != UNNRPProtocol.RequestMarker)
            {
                return;
            }
            byte[] response = Answer(p.DataBytes);
            Address client = p.Transmitter;
            uint connectionId = p.ConnectionID;
            // not sent on the thread of the modem
            Task.Run(() =>
            {
                try
                {
                    Modem.LowLevelTransmit(response, client, UNNRPProtocol.QueryType, connectionId, null);
                }
                catch (Exception ex)
                {
                    ErrorOccured(ex);
                }
            });
        }

        /// <summary>
        /// The response to a request: data begins with 0x10, at least 2 bytes long.
        /// </summary>
        internal byte[] Answer(byte[] request)
        {
            byte requestType = request[1];
            if (requestType == UNNRPProtocol.RequestPing)
            {
                return UNNRPProtocol.Response(UNNRPProtocol.ResponseOk);
            }
            if (requestType != UNNRPProtocol.RequestMapping && requestType != UNNRPProtocol.RequestRecordType)
            {
                return UNNRPProtocol.Response(UNNRPProtocol.ResponseError);
            }
            for (int i = 2; i < request.Length; i++)
            {
                if (request[i] > 0x7F)
                {
                    return UNNRPProtocol.Response(UNNRPProtocol.ResponseError);
                }
            }
            string phrase = System.Text.Encoding.ASCII.GetString(request, 2, request.Length - 2);
            IResolveRecord? record = Decide(phrase);
            if (record == null)
            {
                return UNNRPProtocol.Response(UNNRPProtocol.ResponseError);
            }
            if (requestType == UNNRPProtocol.RequestRecordType)
            {
                return [UNNRPProtocol.ResponseMarker, UNNRPProtocol.ResponseType, record.Type];
            }
            return record switch
            {
                RecordUnameToAfTnet a => UNNRPProtocol.Response(UNNRPProtocol.ResponseResult, a.Address.ToString()),
                RecordUnameToArrayTnet array => UNNRPProtocol.Response(UNNRPProtocol.ResponseResult, string.Join(',', array.Addresses)),
                RecordRegister => UNNRPProtocol.Response(UNNRPProtocol.ResponseResult),
                RecordAfTnetToUname pointer => UNNRPProtocol.Response(UNNRPProtocol.ResponseResult, pointer.Uname),
                RecordVideoNetwork video => UNNRPProtocol.Response(UNNRPProtocol.ResponseResult, video.Address.ToString()),
                RecordNameSpaceServer nss => UNNRPProtocol.Response(UNNRPProtocol.ResponseRedirect, nss.Server.ToString()),
                _ => UNNRPProtocol.Response(UNNRPProtocol.ResponseError),
            };
        }

        /// <summary>
        /// The record, by which the server answers the phrase, or null (ERROR).
        /// </summary>
        private IResolveRecord? Decide(string phrase)
        {
            lock (RecordsLock)
            {
                if (UNNRPProtocol.IsTetronetAddress(phrase))
                {
                    return Pointers.GetValueOrDefault(phrase);
                }
                if (UNNRPProtocol.LooksLikeVideonet(phrase))
                {
                    return UNNRPProtocol.TryParseVideonet(phrase, out UInt128 value) ? Videonet.GetValueOrDefault(value) : null;
                }
                if (!UNNRPProtocol.IsValidUname(phrase))
                {
                    return null;
                }
                if (Names.TryGetValue(phrase, out IResolveRecord? exact))
                {
                    return exact;
                }
                // the longest wildcard first: for a.b.retalog.com "*.b.retalog.com", then "*.retalog.com", then "*.com"
                for (int dot = phrase.IndexOf('.'); dot >= 0; dot = phrase.IndexOf('.', dot + 1))
                {
                    if (Names.TryGetValue("*." + phrase[(dot + 1)..], out IResolveRecord? wildcard) && wildcard is RecordNameSpaceServer)
                    {
                        return wildcard;
                    }
                }
                return null;
            }
        }
    }
}
