using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ModemAPI
{
    /// <summary>
    /// Resolves names through UNNRP (User Name Network Resolve Protocol) servers: UNAME → tetronet addresses, tetronet address → UNAME,
    /// videonet address → tetronet address. Follows the redirects of the servers by itself, see <see cref="MaxRedirects"/>.
    /// Thread-safe: several requests can be made at the same time, also to different servers.
    /// </summary>
    /// <remarks>
    /// Every request is one tetronet packet and nothing is retransmitted: a lost request or response ends in <see cref="TimeoutException"/>.
    /// There's no encryption: anyone on the route can read or change the answers.
    /// </remarks>
    public class UNNRPClient : IDisposable
    {
        /// <summary>
        /// The most redirects one request can follow, by default 8. 0 - a redirect isn't followed, it ends in <see cref="UNNRPException"/>.
        /// </summary>
        public const int DefaultMaxRedirects = 8;
        private readonly IModem Modem;
        private readonly ConcurrentDictionary<uint, PendingRequest> Pending = new();
        private readonly Action<Packet, Action> ResponseListener;
        private readonly Lock AttachLock = new();
        private bool Attached = false;
        private bool Disposed = false;
        private int maxRedirects = DefaultMaxRedirects;

        private sealed class PendingRequest(Address server)
        {
            public readonly Address Server = server;
            public readonly TaskCompletionSource<byte[]> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// The most redirects one request can follow, 0 or more, by default <see cref="DefaultMaxRedirects"/>. 0 - a redirect isn't followed.
        /// More redirects, or a redirect to a server, that this request visited already, end in <see cref="UNNRPException"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative</exception>
        public int MaxRedirects
        {
            get => maxRedirects;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                maxRedirects = value;
            }
        }

        /// <summary>
        /// Creates a new UNNRP client.
        /// </summary>
        /// <param name="modem">Modem, through which the requests are sent; this client doesn't dial or drop it</param>
        public UNNRPClient(IModem modem)
        {
            ArgumentNullException.ThrowIfNull(modem);
            Modem = modem;
            ResponseListener = OnPacket;
        }

        /// <summary>
        /// Resolves a UNAME into its tetronet addresses.
        /// </summary>
        /// <param name="server">Address of the UNNRP server, where the search begins</param>
        /// <param name="uname">The name, for example <c>homeserver</c></param>
        /// <param name="timeoutMs">How long to wait for every response (every redirect waits again), in milliseconds (more than 0)</param>
        /// <returns>The addresses of the name: one for <see cref="RecordUnameToAfTnet"/>, all of them for <see cref="RecordUnameToArrayTnet"/>,
        /// none for <see cref="RecordRegister"/> (the name is taken, but doesn't lead anywhere)</returns>
        /// <exception cref="ArgumentNullException">server or uname is null</exception>
        /// <exception cref="ArgumentException">server isn't a valid tetronet address, or uname isn't a valid UNAME</exception>
        /// <exception cref="ArgumentOutOfRangeException">timeoutMs is not more than 0</exception>
        /// <exception cref="ModemNotConnectedException">The modem isn't connected to the tetronet</exception>
        /// <exception cref="TimeoutException">A server didn't answer in timeoutMs</exception>
        /// <exception cref="UNNRPErrorException">A server answered ERROR: there's no such name</exception>
        /// <exception cref="UNNRPException">Too many redirects, a redirect loop, or a response, that breaks the protocol</exception>
        /// <exception cref="ObjectDisposedException">This client is disposed</exception>
        public Address[] ResolveName(Address server, string uname, int timeoutMs)
        {
            ArgumentNullException.ThrowIfNull(uname);
            if (!UNNRPProtocol.IsAllowedUname(uname))
            {
                throw new ArgumentException($"'{uname}' isn't a valid UNAME", nameof(uname));
            }
            (Address answeredBy, byte[] body) = Resolve(server, UNNRPProtocol.RequestMapping, uname, timeoutMs, UNNRPProtocol.ResponseResult);
            if (body.Length == 0)
            {
                return [];
            }
            string[] addresses = Encoding.ASCII.GetString(body).Split(',');
            return [.. addresses.Select(a => ParseAddress(a, answeredBy))];
        }

        /// <summary>
        /// Resolves a tetronet address into its UNAME (<see cref="RecordAfTnetToUname"/>).
        /// </summary>
        /// <returns>The name of the address</returns>
        /// <exception cref="ArgumentException">server or address isn't a valid tetronet address</exception>
        /// <exception cref="UNNRPErrorException">The server answered ERROR: the address has no name</exception>
        /// <remarks>The other exceptions are the same as of <see cref="ResolveName"/>.</remarks>
        public string ResolveAddress(Address server, Address address, int timeoutMs)
        {
            ArgumentNullException.ThrowIfNull(address);
            if (!Address.Validate(address))
            {
                throw new ArgumentException($"'{address}' isn't a valid tetronet address", nameof(address));
            }
            (Address answeredBy, byte[] body) = Resolve(server, UNNRPProtocol.RequestMapping, address.ToString(), timeoutMs, UNNRPProtocol.ResponseResult);
            string uname = Encoding.ASCII.GetString(body);
            if (!UNNRPProtocol.IsValidUname(uname))
            {
                throw new UNNRPException($"UNNRP server {answeredBy} answered '{uname}', that isn't a valid UNAME");
            }
            return uname;
        }

        /// <summary>
        /// Resolves a videonet address into a tetronet address (<see cref="RecordVideoNetwork"/>).
        /// </summary>
        /// <param name="server">Address of the UNNRP server</param>
        /// <param name="videonetAddress">The videonet address, for example <c>vnet8596226</c></param>
        /// <param name="timeoutMs">How long to wait for every response, in milliseconds (more than 0)</param>
        /// <exception cref="ArgumentException">server isn't a valid tetronet address, or videonetAddress isn't a videonet address</exception>
        /// <exception cref="UNNRPErrorException">The server answered ERROR: there's no such videonet address</exception>
        /// <remarks>The other exceptions are the same as of <see cref="ResolveName"/>.</remarks>
        public Address ResolveVideonet(Address server, string videonetAddress, int timeoutMs)
        {
            return ResolveVideonet(server, UNNRPProtocol.ParseVideonet(videonetAddress, nameof(videonetAddress)), timeoutMs);
        }

        /// <summary>
        /// Resolves a videonet address into a tetronet address (<see cref="RecordVideoNetwork"/>).
        /// </summary>
        /// <remarks>The exceptions are the same as of <see cref="ResolveVideonet(Address, string, int)"/>.</remarks>
        public Address ResolveVideonet(Address server, UInt128 videonetAddress, int timeoutMs)
        {
            (Address answeredBy, byte[] body) = Resolve(server, UNNRPProtocol.RequestMapping, UNNRPProtocol.FormatVideonet(videonetAddress), timeoutMs, UNNRPProtocol.ResponseResult);
            return ParseAddress(Encoding.ASCII.GetString(body), answeredBy);
        }

        /// <summary>
        /// Asks the server, by which record it answers the phrase. A redirecting server answers the type of its
        /// <see cref="RecordNameSpaceServer"/> instead of redirecting.
        /// </summary>
        /// <param name="server">Address of the UNNRP server</param>
        /// <param name="phrase">A UNAME, a tetronet address or a videonet address, ASCII</param>
        /// <param name="timeoutMs">How long to wait for the response, in milliseconds (more than 0)</param>
        /// <returns>The <c>RecordType</c> of the record, for example <see cref="RecordUnameToAfTnet.RecordType"/></returns>
        /// <exception cref="ArgumentException">server isn't a valid tetronet address, or phrase isn't ASCII or too long</exception>
        /// <exception cref="UNNRPErrorException">The server answered ERROR: it has no record for the phrase, or the phrase isn't valid</exception>
        /// <remarks>The other exceptions are the same as of <see cref="ResolveName"/>.</remarks>
        public byte GetRecordType(Address server, string phrase, int timeoutMs)
        {
            ArgumentNullException.ThrowIfNull(phrase);
            if (!Ascii.IsValid(phrase) || phrase.Length > UNNRPProtocol.MaxBodyLength)
            {
                throw new ArgumentException($"the phrase must be ASCII and not longer than {UNNRPProtocol.MaxBodyLength} characters", nameof(phrase));
            }
            (Address answeredBy, byte[] body) = Resolve(server, UNNRPProtocol.RequestRecordType, phrase, timeoutMs, UNNRPProtocol.ResponseType);
            if (body.Length != 1)
            {
                throw new UNNRPException($"UNNRP server {answeredBy} answered TYPE with {body.Length} bytes instead of 1");
            }
            return body[0];
        }

        /// <summary>
        /// Checks, that a UNNRP server answers.
        /// </summary>
        /// <param name="server">Address of the UNNRP server</param>
        /// <param name="timeoutMs">How long to wait for the response, in milliseconds (more than 0)</param>
        /// <returns><see langword="true"/> - the server answered OK; <see langword="false"/> - no answer in timeoutMs, or another answer</returns>
        /// <exception cref="ArgumentException">server isn't a valid tetronet address</exception>
        /// <exception cref="ModemNotConnectedException">The modem isn't connected to the tetronet</exception>
        /// <exception cref="ObjectDisposedException">This client is disposed</exception>
        public bool Ping(Address server, int timeoutMs)
        {
            CheckServer(server, timeoutMs);
            try
            {
                byte[] response = Exchange(server, Request(UNNRPProtocol.RequestPing, ""), timeoutMs);
                return response.Length == 2 && response[1] == UNNRPProtocol.ResponseOk;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        /// <summary>
        /// Sends the request and follows the redirects; returns the server, that answered, and the body of its response.
        /// </summary>
        private (Address, byte[]) Resolve(Address server, byte requestType, string phrase, int timeoutMs, byte expected)
        {
            CheckServer(server, timeoutMs);
            byte[] request = Request(requestType, phrase);
            HashSet<string> visited = [server.ToString()];
            int redirects = 0;
            Address current = Address.Copy(server);
            while (true)
            {
                byte[] response = Exchange(current, request, timeoutMs);
                byte type = response[1];
                byte[] body = response[2..];
                if (type == expected)
                {
                    return (current, body);
                }
                switch (type)
                {
                    case UNNRPProtocol.ResponseError:
                        throw new UNNRPErrorException(current, phrase);
                    case UNNRPProtocol.ResponseRedirect:
                        Address next = ParseAddress(Encoding.ASCII.GetString(body), current);
                        if (redirects >= MaxRedirects)
                        {
                            throw new UNNRPException($"UNNRP server {current} redirected '{phrase}' to {next}, but MaxRedirects ({MaxRedirects}) redirects were followed already");
                        }
                        if (!visited.Add(next.ToString()))
                        {
                            throw new UNNRPException($"UNNRP server {current} redirected '{phrase}' to {next}, that was asked already: a redirect loop");
                        }
                        redirects++;
                        current = next;
                        break;
                    default:
                        throw new UNNRPException($"UNNRP server {current} answered with the type 0x{type:X2}, that isn't expected here");
                }
            }
        }

        private static Address ParseAddress(string text, Address answeredBy)
        {
            Address address = new(text);
            if (!Address.Validate(address) || address.IsEmpty)
            {
                throw new UNNRPException($"UNNRP server {answeredBy} answered '{text}', that isn't a valid tetronet address");
            }
            return address;
        }

        private static byte[] Request(byte requestType, string phrase)
        {
            byte[] data = new byte[2 + phrase.Length];
            data[0] = UNNRPProtocol.RequestMarker;
            data[1] = requestType;
            Encoding.ASCII.GetBytes(phrase, 0, phrase.Length, data, 2);
            return data;
        }

        private static void CheckServer(Address server, int timeoutMs)
        {
            ArgumentNullException.ThrowIfNull(server);
            if (!Address.Validate(server))
            {
                throw new ArgumentException($"'{server}' isn't a valid tetronet address", nameof(server));
            }
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        }

        /// <summary>
        /// Sends one request with a new random ConnectionID and waits for the response to it: at least 2 bytes, beginning with 0x50.
        /// </summary>
        private byte[] Exchange(Address server, byte[] request, int timeoutMs)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Address? local = Modem.LocalModemAddress;
            if (!Modem.IsModemConnected || local == null || local.IsEmpty)
            {
                throw new ModemNotConnectedException("the modem of the UNNRP client isn't connected to the tetronet");
            }
            EnsureAttached();
            PendingRequest pending = new(Address.Copy(server));
            uint connectionId;
            do
            {
                connectionId = (uint)RandomNumberGenerator.GetInt32((int)UNNRPProtocol.MinConnectionID, (int)UNNRPProtocol.MaxConnectionID + 1);
            }
            while (!Pending.TryAdd(connectionId, pending));
            try
            {
                Modem.LowLevelTransmit(request, server, UNNRPProtocol.QueryType, connectionId, null);
                if (!pending.Response.Task.Wait(timeoutMs))
                {
                    throw new TimeoutException($"UNNRP server {server} didn't answer in {timeoutMs} ms");
                }
                return pending.Response.Task.Result;
            }
            finally
            {
                Pending.TryRemove(connectionId, out _);
            }
        }

        private void EnsureAttached()
        {
            lock (AttachLock)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                if (!Attached)
                {
                    Modem.AttachReceiveEventNoUnfragment(ResponseListener);
                    Attached = true;
                }
            }
        }

        private void OnPacket(Packet p, Action k)
        {
            // requests (0x10) are for a server on this modem; a response is taken only from the server, that was asked
            if (p.QueryType != UNNRPProtocol.QueryType || p.DataBytes.Length < 2 || p.DataBytes[0] != UNNRPProtocol.ResponseMarker ||
                !Pending.TryGetValue(p.ConnectionID, out PendingRequest? pending) || !pending.Server.Equals(p.Transmitter))
            {
                return;
            }
            pending.Response.TrySetResult([.. p.DataBytes]);
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
                    Modem.DetachReceiveEventNoUnfragment(ResponseListener);
                    Attached = false;
                }
            }
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// A UNNRP request failed: too many redirects, a redirect loop, or a server answered something, that breaks the protocol.
    /// </summary>
    public class UNNRPException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public UNNRPException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// A UNNRP server answered ERROR: it has no record for the phrase, or the phrase isn't valid.
    /// </summary>
    public class UNNRPErrorException : UNNRPException
    {
        /// <summary>The server, that answered ERROR.</summary>
        public Address Server { get; }
        /// <summary>The phrase of the request.</summary>
        public string Phrase { get; }

        /// <summary>Creates the exception.</summary>
        public UNNRPErrorException(Address server, string phrase) : base($"UNNRP server {server} answered ERROR to '{phrase}'")
        {
            Server = Address.Copy(server);
            Phrase = phrase;
        }
    }
}
