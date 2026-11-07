using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ModemAPI
{
    /// <summary>
    /// Sends commands to an NCCP (Network Computers Control Protocol) server and waits for their results.
    /// Thread-safe: several commands can be executed at the same time, also to different servers.
    /// </summary>
    /// <remarks>
    /// The delivery isn't guaranteed: a request or its response can be lost, and then <see cref="NetworkExecuteCommand"/> throws
    /// <see cref="TimeoutException"/>, though the command could have been executed. Repeating a command after a timeout can
    /// execute it twice, so commands, that are repeated, have to tolerate that (be idempotent): that's up to the user.
    /// </remarks>
    public class NCCPClient : IDisposable
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
            public readonly TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Most bytes of one packet, when a request is fragmented. 1 to 65535, default 1024.
        /// </summary>
        public ushort PacketSize { get; set; } = 1024;
        /// <summary>
        /// Gets invoked for responses, that are dropped: their tag didn't match (a wrong key, or they were damaged or forged),
        /// or they break the format. The command waiting for them keeps waiting for a valid response till its timeout.
        /// </summary>
        public event Action<Exception> ErrorOccured = delegate { };

        /// <summary>
        /// Creates a new NCCP client.
        /// </summary>
        /// <param name="modem">Modem, through which the commands are sent; this client doesn't dial or drop it</param>
        public NCCPClient(IModem modem)
        {
            ArgumentNullException.ThrowIfNull(modem);
            Modem = modem;
            ResponseListener = OnDataBlock;
        }

        /// <summary>
        /// Executes a command on an NCCP server and returns its result.
        /// </summary>
        /// <param name="server">Address of the NCCP server</param>
        /// <param name="syntax">The command, for example <c>Command;Arg1;(OtherCommand;Arg2)</c>. Special characters of the arguments
        /// have to be escaped, see <see cref="Escape"/></param>
        /// <param name="timeoutMs">How long to wait for the response, in milliseconds (more than 0)</param>
        /// <param name="aes256gcmEncryptionKey"><see langword="null"/> or empty - the command and its result are sent without encryption;
        /// 32 bytes - AES-256-GCM key, the same as the server has for the address of this modem</param>
        /// <returns>The result of the command</returns>
        /// <exception cref="ArgumentNullException">server or syntax is null</exception>
        /// <exception cref="ArgumentException">server isn't a valid tetronet address</exception>
        /// <exception cref="ArgumentOutOfRangeException">timeoutMs is not more than 0</exception>
        /// <exception cref="InvalidDataException">The key is not empty and not 32 bytes long</exception>
        /// <exception cref="ModemNotConnectedException">The modem isn't connected to the tetronet</exception>
        /// <exception cref="TimeoutException">No valid response came in timeoutMs</exception>
        /// <exception cref="ObjectDisposedException">This client is disposed</exception>
        public string NetworkExecuteCommand(Address server, string syntax, int timeoutMs, byte[]? aes256gcmEncryptionKey)
        {
            ArgumentNullException.ThrowIfNull(server);
            ArgumentNullException.ThrowIfNull(syntax);
            if (!Address.Validate(server))
            {
                throw new ArgumentException($"'{server}' isn't a valid tetronet address", nameof(server));
            }
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
            byte[]? key = null;
            if (aes256gcmEncryptionKey != null && aes256gcmEncryptionKey.Length != 0)
            {
                if (aes256gcmEncryptionKey.Length != NCCPProtocol.KeyLength)
                {
                    throw new InvalidDataException($"AES-256-GCM key has to be exactly {NCCPProtocol.KeyLength} bytes long");
                }
                // copied, so changing the caller's array while waiting doesn't change the key used for the response
                key = (byte[])aes256gcmEncryptionKey.Clone();
            }
            ObjectDisposedException.ThrowIf(Disposed, this);
            Address? local = Modem.LocalModemAddress;
            if (!Modem.IsModemConnected || local == null || local.IsEmpty)
            {
                throw new ModemNotConnectedException("the modem of the NCCP client isn't connected to the tetronet");
            }
            EnsureAttached();
            byte[] plaintext = NCCPProtocol.Utf8.GetBytes(syntax);
            byte[] nonce = RandomNumberGenerator.GetBytes(NCCPProtocol.NonceLength);
            string id = Convert.ToHexStringLower(nonce);
            PendingRequest request = new() { Key = key, ClientAddress = local.ToString(), ServerAddress = server.ToString() };
            byte[] data;
            string metadata;
            if (key == null)
            {
                data = plaintext;
                metadata = NCCPProtocol.PlainRequestPrefix + id;
            }
            else
            {
                string time = NCCPProtocol.FormatTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                byte[] aad = NCCPProtocol.RequestAad(request.ClientAddress, request.ServerAddress, time);
                data = NCCPProtocol.Encrypt(key, nonce, plaintext, aad, out byte[] tag);
                metadata = NCCPProtocol.EncryptedRequestPrefix + id + ":" + time + ":" + Convert.ToHexStringLower(tag);
            }
            // the nonce is 12 random bytes, so two requests never share it
            Pending[id] = request;
            try
            {
                NCCPProtocol.Send(Modem, data, server, metadata, PacketSize);
                if (!request.Result.Task.Wait(timeoutMs))
                {
                    throw new TimeoutException($"NCCP server {server} didn't answer in {timeoutMs} ms");
                }
                return request.Result.Task.Result;
            }
            finally
            {
                Pending.TryRemove(id, out _);
            }
        }

        /// <summary>
        /// Escapes the special characters of an argument (<c>\</c>, <c>;</c>, <c>(</c>, <c>)</c>), so it's passed to the command as is.
        /// </summary>
        public static string Escape(string argument)
        {
            ArgumentNullException.ThrowIfNull(argument);
            StringBuilder sb = new(argument.Length);
            foreach (char c in argument)
            {
                if (c is '\\' or ';' or '(' or ')')
                {
                    sb.Append('\\');
                }
                sb.Append(c);
            }
            return sb.ToString();
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
                if (block.ConnectionID != NCCPProtocol.ConnectionID || block.QueryType != NCCPProtocol.QueryType || block.Metadata == null)
                {
                    return;
                }
                string metadata = block.Metadata;
                bool encrypted;
                string id;
                byte[]? nonce = null;
                byte[]? tag = null;
                if (metadata.StartsWith(NCCPProtocol.PlainResponsePrefix, StringComparison.Ordinal))
                {
                    encrypted = false;
                    id = metadata[NCCPProtocol.PlainResponsePrefix.Length..];
                }
                else if (metadata.StartsWith(NCCPProtocol.EncryptedResponsePrefix, StringComparison.Ordinal))
                {
                    encrypted = true;
                    string[] fields = metadata[NCCPProtocol.EncryptedResponsePrefix.Length..].Split(':');
                    if (fields.Length != 3)
                    {
                        throw new InvalidDataException("an encrypted NCCP response must have 3 fields in its metadata");
                    }
                    id = fields[0];
                    nonce = NCCPProtocol.ParseHex(fields[1], NCCPProtocol.NonceLength, "nonce");
                    tag = NCCPProtocol.ParseHex(fields[2], NCCPProtocol.TagLength, "tag");
                }
                else
                {
                    // a request to a server on this modem, or something else
                    return;
                }
                if (!Pending.TryGetValue(id, out PendingRequest? request))
                {
                    // a response to a request, that timed out already, or a repeated one
                    return;
                }
                byte[] data = [.. block.DataBytes];
                string result;
                if (request.Key == null)
                {
                    if (encrypted)
                    {
                        throw new InvalidDataException($"NCCP server {block.Transmitter} sent an encrypted response to a request without encryption");
                    }
                    result = NCCPProtocol.Utf8.GetString(data);
                }
                else
                {
                    if (!encrypted)
                    {
                        // never accepted: anyone could send it
                        throw new InvalidDataException($"NCCP response from {block.Transmitter} to an encrypted request isn't encrypted");
                    }
                    byte[] aad = NCCPProtocol.ResponseAad(request.ClientAddress, request.ServerAddress, id);
                    // throws on a wrong tag, and then nothing of the data is used
                    byte[] plaintext = NCCPProtocol.Decrypt(request.Key, nonce!, data, tag!, aad);
                    result = NCCPProtocol.Utf8.GetString(plaintext);
                }
                request.Result.TrySetResult(result);
            }
            catch (Exception ex)
            {
                ErrorOccured(ex);
            }
        }

        /// <summary>
        /// Stops listening to responses; commands in progress time out.
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
    /// The parts of NCCP, that the client and the server share (see PROTOCOLS.md).
    /// </summary>
    internal static class NCCPProtocol
    {
        public const uint ConnectionID = ConnectionIDDefaults.COMMAND; // 4998512
        public const string QueryType = "command";
        public const string PlainRequestPrefix = "q:";
        public const string EncryptedRequestPrefix = "qe:";
        public const string PlainResponsePrefix = "r:";
        public const string EncryptedResponsePrefix = "re:";
        public const int KeyLength = 32;
        public const int NonceLength = 12;
        public const int TagLength = 16;
        public static readonly UTF8Encoding Utf8 = new(false, false);
        public static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public static string FormatTime(long unixSeconds) => unixSeconds.ToString("x16", CultureInfo.InvariantCulture);

        public static byte[] RequestAad(string client, string server, string timeHex) => Encoding.ASCII.GetBytes($"nccp-q|{client}|{server}|{timeHex}");

        public static byte[] ResponseAad(string client, string server, string requestNonceHex) => Encoding.ASCII.GetBytes($"nccp-r|{client}|{server}|{requestNonceHex}");

        public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad, out byte[] tag)
        {
            byte[] ciphertext = new byte[plaintext.Length];
            tag = new byte[TagLength];
            using AesGcm aes = new(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return ciphertext;
        }

        /// <exception cref="AuthenticationTagMismatchException">The tag doesn't match: the key, the data, the AAD or the tag is wrong</exception>
        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] aad)
        {
            byte[] plaintext = new byte[ciphertext.Length];
            using AesGcm aes = new(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }

        /// <summary>
        /// Reads exactly <paramref name="length"/> bytes written as hex digits (any case).
        /// </summary>
        public static byte[] ParseHex(string hex, int length, string what)
        {
            if (hex.Length != length * 2 || !hex.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"NCCP {what} must be {length * 2} hex digits");
            }
            return Convert.FromHexString(hex);
        }

        /// <summary>
        /// Sends the data of a request or a response with Transmit (fragmented). The only exception is empty data (an empty command
        /// or result): Transmit sends no packets at all for it, so the other side would wait till its timeout; it is sent as
        /// the one packet Transmit would have made, the last one with PacketNo 0 and no data.
        /// </summary>
        public static void Send(IModem modem, byte[] data, Address to, string metadata, ushort packetSize)
        {
            if (data.Length == 0)
            {
                modem.LowLevelTransmit(data, to, QueryType, ConnectionID, metadata);
            }
            else
            {
                modem.Transmit(data, to, QueryType, ConnectionID, metadata, packetSize == 0 ? (ushort)1024 : packetSize);
            }
        }
    }
}
