using System.Collections.Concurrent;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;

namespace ModemAPI
{
    /// <summary>
    /// Executes commands, that NCCP (Network Computers Control Protocol) clients send, and answers with their results.
    /// Like every server class, it doesn't dial or drop the modem: the modem has to be connected to the tetronet before <see cref="Start"/>.
    /// </summary>
    /// <remarks>
    /// The delivery isn't guaranteed: a command can be executed while its response gets lost, and the client then repeats it.
    /// Commands, that can be repeated, have to tolerate being executed more than once: that's up to the user.
    /// Every command is independent, there are no sessions. The server always answers a request it accepted, even when the
    /// command failed: with the result of <see cref="HandleCommandExecutionError"/>, or <see cref="EverythingFailed"/>.
    /// </remarks>
    public class NCCPServer
    {
        /// <summary>
        /// The deepest nesting <see cref="MaxNetworkCommandRecursion"/> can be set to.
        /// </summary>
        public const int MaxAllowedRecursion = 1000;
        private readonly IModem Modem;
        private readonly Lock StartLock = new();
        private Action<DataBlock, Action>? Listener = null;
        // nonces of the accepted encrypted requests: (client address, nonce in lower case hex) -> when it can be forgotten
        private readonly Dictionary<(string, string), long> SeenNonces = [];
        private readonly Queue<((string, string) Key, long Expires)> SeenNoncesOrder = new();
        private readonly Lock SeenNoncesLock = new();

        /// <summary>
        /// Commands of this server by name (case-sensitive). A command gets the address of the client and its arguments
        /// (without the name of the command), and must return a result (not <see langword="null"/>).
        /// </summary>
        public ConcurrentDictionary<string, Func<Address, string[], string>> Commands { get; } = new(StringComparer.Ordinal);
        /// <summary>
        /// Called instead of a command, that isn't in <see cref="Commands"/>. It gets the address of the client and the name of the
        /// command followed by its arguments (element 0 is the name).
        /// </summary>
        public Func<Address, string[], string> UnknownCommand { get; set; } = delegate { return "Unknown command"; };
        /// <summary>
        /// Called, when the command (or a command nested in it) threw an exception, returned <see langword="null"/>, or its syntax is wrong
        /// (<see cref="FormatException"/>). Its result is sent to the client instead of the result of the command.
        /// </summary>
        public Func<Exception, string> HandleCommandExecutionError { get; set; } = delegate { return "Command execution failed"; };
        /// <summary>
        /// Sent to the client, when even <see cref="HandleCommandExecutionError"/> threw an exception or returned <see langword="null"/>.
        /// </summary>
        public string EverythingFailed = "NCCP: everything failed";
        /// <summary>
        /// How deep commands can be nested in parentheses: 0 - no nesting, 1 - <c>A;(B)</c>, 2 - <c>A;(B;(C))</c> and so on.
        /// 0 to <see cref="MaxAllowedRecursion"/>, default 16.
        /// </summary>
        public int MaxNetworkCommandRecursion = 16;
        /// <summary>
        /// Sent to the client instead of executing anything, when its command is nested deeper than <see cref="MaxNetworkCommandRecursion"/>.
        /// </summary>
        public string MaxRecursionDepthPassedErrorMessage = "NCCP: max recursion depth passed";
        /// <summary>
        /// <see langword="true"/> - only requests encrypted with the key of their address in <see cref="NCCPEncryptionKeys"/> are executed;
        /// requests without encryption, with a wrong tag, from an address without a key, too old or repeated are dropped without an answer.
        /// <see langword="false"/> - only requests without encryption are taken, and passed to the commands without any checks.
        /// </summary>
        public bool NCCPServerUsesEncryption = false;
        /// <summary>
        /// AES-256-GCM keys (32 bytes) of the clients by their address. Every address has a key of its own, so a key taken out of one
        /// device doesn't open the others.
        /// </summary>
        public ConcurrentDictionary<Address, byte[]> NCCPEncryptionKeys { get; } = new();
        /// <summary>
        /// More than 0: an encrypted request is taken only, when the time it was sent differs from the clock of this server by not more
        /// than this number of seconds, and its nonce is remembered for that time, so a copy of it is never executed again.
        /// 0: the time isn't checked (for devices without a clock), only the last <see cref="MaxRememberedNonces"/> nonces are remembered,
        /// so a copy can be executed again after it's forgotten or after a restart of the server. Default 120.
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
        /// Gets invoked for requests, that are dropped, and for errors of sending the responses.
        /// </summary>
        public event Action<Exception> ErrorOccured = delegate { };
        /// <summary>
        /// The clock used for <see cref="ReplayWindowSeconds"/>, Unix time in seconds.
        /// </summary>
        internal Func<long> UnixTimeSeconds = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// Creates a new NCCP server.
        /// </summary>
        /// <param name="modem">Modem, through which the server takes the requests; this server doesn't dial or drop it</param>
        public NCCPServer(IModem modem)
        {
            ArgumentNullException.ThrowIfNull(modem);
            Modem = modem;
        }

        /// <summary>
        /// Starts taking requests. After <see cref="Stop"/> the server can be started again.
        /// </summary>
        /// <exception cref="ModemNotConnectedException">The modem isn't connected to the tetronet</exception>
        /// <exception cref="InvalidOperationException">The server is started already, or its settings are invalid</exception>
        public void Start()
        {
            if (MaxNetworkCommandRecursion < 0 || MaxNetworkCommandRecursion > MaxAllowedRecursion)
            {
                throw new InvalidOperationException($"MaxNetworkCommandRecursion must be 0 to {MaxAllowedRecursion}, but it is {MaxNetworkCommandRecursion}");
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
                throw new ModemNotConnectedException("the modem of the NCCP server isn't connected to the tetronet");
            }
            lock (StartLock)
            {
                if (Listener != null)
                {
                    throw new InvalidOperationException("this NCCP server is already started");
                }
                Listener = OnDataBlock;
                Modem.AttachReceiveEvent(Listener);
            }
        }

        /// <summary>
        /// Stops taking requests; commands, that are being executed, still get answered. The server can be started again.
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
            // only the cheap checks on the thread of the modem, the commands may take long
            if (block.ConnectionID != NCCPProtocol.ConnectionID || block.QueryType != NCCPProtocol.QueryType || block.Metadata == null ||
                !(block.Metadata.StartsWith(NCCPProtocol.PlainRequestPrefix, StringComparison.Ordinal) ||
                  block.Metadata.StartsWith(NCCPProtocol.EncryptedRequestPrefix, StringComparison.Ordinal)))
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
                if (!NCCPServerUsesEncryption)
                {
                    if (!metadata.StartsWith(NCCPProtocol.PlainRequestPrefix, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"encrypted NCCP request from {client} is dropped: this server doesn't use encryption");
                    }
                    string id = metadata[NCCPProtocol.PlainRequestPrefix.Length..];
                    NCCPProtocol.ParseHex(id, NCCPProtocol.NonceLength, "request ID");
                    string result = ExecuteSyntax(client, () => NCCPProtocol.StrictUtf8.GetString(data));
                    NCCPProtocol.Send(Modem, NCCPProtocol.Utf8.GetBytes(result), client, NCCPProtocol.PlainResponsePrefix + id, PacketSize);
                    return;
                }
                if (!metadata.StartsWith(NCCPProtocol.EncryptedRequestPrefix, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException($"NCCP request without encryption from {client} is dropped: this server uses encryption");
                }
                string[] fields = metadata[NCCPProtocol.EncryptedRequestPrefix.Length..].Split(':');
                if (fields.Length != 3)
                {
                    throw new InvalidDataException($"encrypted NCCP request from {client} must have 3 fields in its metadata");
                }
                string nonceHex = fields[0];
                string timeHex = fields[1];
                byte[] nonce = NCCPProtocol.ParseHex(nonceHex, NCCPProtocol.NonceLength, "nonce");
                byte[] timeBytes = NCCPProtocol.ParseHex(timeHex, 8, "time");
                byte[] tag = NCCPProtocol.ParseHex(fields[2], NCCPProtocol.TagLength, "tag");
                if (!NCCPEncryptionKeys.TryGetValue(client, out byte[]? storedKey) || storedKey == null)
                {
                    throw new UnauthorizedAccessException($"NCCP request from {client} is dropped: there's no key for this address");
                }
                byte[] key = (byte[])storedKey.Clone();
                if (key.Length != NCCPProtocol.KeyLength)
                {
                    throw new InvalidDataException($"the key of {client} in NCCPEncryptionKeys is {key.Length} bytes long, AES-256-GCM key has to be exactly {NCCPProtocol.KeyLength} bytes long");
                }
                string clientAddress = client.ToString();
                string serverAddress = block.Receiver.ToString();
                byte[] plaintext;
                try
                {
                    // the tag is checked before anything else is done with the request
                    plaintext = NCCPProtocol.Decrypt(key, nonce, data, tag, NCCPProtocol.RequestAad(clientAddress, serverAddress, timeHex));
                }
                catch (Exception ex)
                {
                    throw new UnauthorizedAccessException($"NCCP request from {client} is dropped: its tag doesn't match (a wrong key, or it was damaged or forged)", ex);
                }
                long sentAt = BinaryPrimitives.ReadInt64BigEndian(timeBytes);
                string? refusal = AcceptNonce(clientAddress, nonceHex.ToLowerInvariant(), sentAt);
                if (refusal != null)
                {
                    throw new UnauthorizedAccessException(refusal);
                }
                string answer = ExecuteSyntax(client, () => NCCPProtocol.StrictUtf8.GetString(plaintext));
                byte[] responseNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(NCCPProtocol.NonceLength);
                // bound to the request: the nonce of the request goes into the AAD exactly as it came
                byte[] ciphertext = NCCPProtocol.Encrypt(key, responseNonce, NCCPProtocol.Utf8.GetBytes(answer), NCCPProtocol.ResponseAad(clientAddress, serverAddress, nonceHex), out byte[] responseTag);
                string responseMetadata = NCCPProtocol.EncryptedResponsePrefix + nonceHex + ":" + Convert.ToHexStringLower(responseNonce) + ":" + Convert.ToHexStringLower(responseTag);
                NCCPProtocol.Send(Modem, ciphertext, client, responseMetadata, PacketSize);
            }
            catch (Exception ex)
            {
                ErrorOccured(ex);
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
                    return $"NCCP request from {client} is dropped: it was sent at {sentAt}, and the time of this server is {serverTime} (ReplayWindowSeconds is {window})";
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
                    return $"NCCP request from {client} is dropped: it's a copy of a request, that was taken already (nonce {nonceHex})";
                }
                if (SeenNonces.Count >= MaxRememberedNonces)
                {
                    if (window > 0)
                    {
                        // forgetting a nonce, that is still in the window, would let its copy through
                        return $"NCCP request from {client} is dropped: {MaxRememberedNonces} nonces are remembered already (MaxRememberedNonces)";
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

        /// <summary>
        /// Parses and executes a command; never throws, every failure becomes the answer, that the protocol requires.
        /// </summary>
        private string ExecuteSyntax(Address client, Func<string> getSyntax)
        {
            try
            {
                CommandNode command;
                try
                {
                    command = NCCPSyntax.Parse(getSyntax(), MaxNetworkCommandRecursion);
                }
                catch (NCCPRecursionException)
                {
                    return MaxRecursionDepthPassedErrorMessage ?? "";
                }
                return Execute(client, command);
            }
            catch (Exception ex)
            {
                try
                {
                    return HandleCommandExecutionError(ex) ?? EverythingFailed ?? "";
                }
                catch
                {
                    return EverythingFailed ?? "";
                }
            }
        }

        private string Execute(Address client, CommandNode command)
        {
            string[] values = new string[command.Elements.Count];
            for (int i = 0; i < values.Length; i++)
            {
                StringBuilder sb = new();
                foreach (object part in command.Elements[i])
                {
                    sb.Append(part is CommandNode nested ? Execute(client, nested) : (string)part);
                }
                values[i] = sb.ToString();
            }
            string? result;
            if (Commands.TryGetValue(values[0], out Func<Address, string[], string>? handler))
            {
                result = handler(Address.Copy(client), values[1..]);
            }
            else
            {
                result = UnknownCommand(Address.Copy(client), values);
            }
            return result ?? throw new InvalidOperationException($"NCCP command '{values[0]}' returned null, but it must return a result");
        }
    }

    /// <summary>
    /// A parsed command: its elements (element 0 is the name, the rest are the arguments). Every element is a sequence of parts:
    /// text (string) and nested commands (CommandNode), that are joined after the nested commands are executed.
    /// </summary>
    internal sealed class CommandNode
    {
        public readonly List<List<object>> Elements = [[]];
    }

    /// <summary>
    /// The command is nested deeper than allowed.
    /// </summary>
    internal sealed class NCCPRecursionException : Exception
    {
    }

    /// <summary>
    /// The parser of NCCP commands: <c>Name;Arg;(Nested;Arg)</c>, with <c>\\</c>, <c>\;</c>, <c>\(</c>, <c>\)</c> as escapes.
    /// </summary>
    internal static class NCCPSyntax
    {
        /// <exception cref="FormatException">The syntax is wrong</exception>
        /// <exception cref="NCCPRecursionException">Nesting is deeper than maxDepth</exception>
        public static CommandNode Parse(string syntax, int maxDepth)
        {
            int position = 0;
            CommandNode root = ParseCommand(syntax, ref position, 0, maxDepth);
            if (position != syntax.Length)
            {
                // ParseCommand only stops early at ')'
                throw new FormatException($"NCCP syntax error: ')' at {position} has no matching '('");
            }
            return root;
        }

        private static CommandNode ParseCommand(string s, ref int i, int depth, int maxDepth)
        {
            CommandNode node = new();
            List<object> element = node.Elements[0];
            StringBuilder text = new();
            void FlushText()
            {
                if (text.Length > 0)
                {
                    element.Add(text.ToString());
                    text.Clear();
                }
            }
            while (i < s.Length)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\':
                        if (i + 1 >= s.Length)
                        {
                            throw new FormatException("NCCP syntax error: '\\' at the end, escape it as '\\\\'");
                        }
                        char escaped = s[i + 1];
                        if (escaped is not ('\\' or ';' or '(' or ')'))
                        {
                            throw new FormatException($"NCCP syntax error: '\\{escaped}' at {i} isn't an escape, only \\\\, \\;, \\( and \\) are");
                        }
                        text.Append(escaped);
                        i += 2;
                        break;
                    case ';':
                        FlushText();
                        element = [];
                        node.Elements.Add(element);
                        i++;
                        break;
                    case '(':
                        if (depth + 1 > maxDepth)
                        {
                            throw new NCCPRecursionException();
                        }
                        FlushText();
                        int open = i;
                        i++;
                        element.Add(ParseCommand(s, ref i, depth + 1, maxDepth));
                        if (i >= s.Length || s[i] != ')')
                        {
                            throw new FormatException($"NCCP syntax error: '(' at {open} isn't closed");
                        }
                        i++;
                        break;
                    case ')':
                        FlushText();
                        return node;
                    default:
                        text.Append(c);
                        i++;
                        break;
                }
            }
            FlushText();
            return node;
        }
    }
}
