using System.Globalization;

namespace ModemAPI
{
    /// <summary>
    /// UNAME → tetronet address (like A/AAAA of DNS).
    /// </summary>
    public sealed class RecordUnameToAfTnet : IResolveRecord
    {
        /// <summary>The type of this record.</summary>
        public const byte RecordType = 0x01;
        /// <inheritdoc/>
        public byte Type => RecordType;
        /// <inheritdoc/>
        public string Key => Uname;
        /// <summary>The name.</summary>
        public string Uname { get; }
        /// <summary>The tetronet address of the name.</summary>
        public Address Address { get; }

        /// <summary>
        /// Creates the record.
        /// </summary>
        /// <exception cref="ArgumentNullException">uname or address is null</exception>
        /// <exception cref="ArgumentException">uname isn't a valid UNAME, or address isn't a valid tetronet address</exception>
        public RecordUnameToAfTnet(string uname, Address address)
        {
            Uname = UNNRPProtocol.CheckUname(uname, nameof(uname));
            Address = UNNRPProtocol.CheckAddress(address, nameof(address));
        }
    }

    /// <summary>
    /// Tetronet address → UNAME (like PTR of DNS).
    /// </summary>
    public sealed class RecordAfTnetToUname : IResolveRecord
    {
        /// <summary>The type of this record.</summary>
        public const byte RecordType = 0x7F;
        /// <inheritdoc/>
        public byte Type => RecordType;
        /// <inheritdoc/>
        public string Key => Address.ToString();
        /// <summary>The tetronet address.</summary>
        public Address Address { get; }
        /// <summary>The name of the address.</summary>
        public string Uname { get; }

        /// <summary>
        /// Creates the record.
        /// </summary>
        /// <exception cref="ArgumentNullException">address or uname is null</exception>
        /// <exception cref="ArgumentException">address isn't a valid tetronet address, or uname isn't a valid UNAME</exception>
        public RecordAfTnetToUname(Address address, string uname)
        {
            Address = UNNRPProtocol.CheckAddress(address, nameof(address));
            Uname = UNNRPProtocol.CheckUname(uname, nameof(uname));
        }
    }

    /// <summary>
    /// UNAME → several tetronet addresses.
    /// </summary>
    public sealed class RecordUnameToArrayTnet : IResolveRecord
    {
        /// <summary>The type of this record.</summary>
        public const byte RecordType = 0x20;
        /// <inheritdoc/>
        public byte Type => RecordType;
        /// <inheritdoc/>
        public string Key => Uname;
        /// <summary>The name.</summary>
        public string Uname { get; }
        /// <summary>The tetronet addresses of the name, in the order they are answered.</summary>
        public IReadOnlyList<Address> Addresses { get; }

        /// <summary>
        /// Creates the record.
        /// </summary>
        /// <exception cref="ArgumentNullException">uname, addresses or one of the addresses is null</exception>
        /// <exception cref="ArgumentException">uname isn't a valid UNAME, there are no addresses, one of them isn't a valid tetronet address,
        /// or all of them don't fit into one response</exception>
        public RecordUnameToArrayTnet(string uname, IEnumerable<Address> addresses)
        {
            Uname = UNNRPProtocol.CheckUname(uname, nameof(uname));
            ArgumentNullException.ThrowIfNull(addresses);
            Address[] copy = [.. addresses.Select(a => UNNRPProtocol.CheckAddress(a, nameof(addresses)))];
            if (copy.Length == 0)
            {
                throw new ArgumentException("there must be at least one address", nameof(addresses));
            }
            if (copy.Sum(a => a.Length + 1) - 1 > UNNRPProtocol.MaxBodyLength)
            {
                throw new ArgumentException($"the addresses separated by commas must fit into {UNNRPProtocol.MaxBodyLength} bytes", nameof(addresses));
            }
            Addresses = copy;
        }
    }

    /// <summary>
    /// Only takes the UNAME, without mapping it to anything. The server answers it with an empty result.
    /// </summary>
    public sealed class RecordRegister : IResolveRecord
    {
        /// <summary>The type of this record.</summary>
        public const byte RecordType = 0xFF;
        /// <inheritdoc/>
        public byte Type => RecordType;
        /// <inheritdoc/>
        public string Key => Uname;
        /// <summary>The name.</summary>
        public string Uname { get; }

        /// <summary>
        /// Creates the record.
        /// </summary>
        /// <exception cref="ArgumentNullException">uname is null</exception>
        /// <exception cref="ArgumentException">uname isn't a valid UNAME</exception>
        public RecordRegister(string uname)
        {
            Uname = UNNRPProtocol.CheckUname(uname, nameof(uname));
        }
    }

    /// <summary>
    /// Videonet address (a 128-bit number written as <c>vnet8596226</c>) → tetronet address.
    /// </summary>
    public sealed class RecordVideoNetwork : IResolveRecord
    {
        /// <summary>The type of this record.</summary>
        public const byte RecordType = 0x88;
        /// <inheritdoc/>
        public byte Type => RecordType;
        /// <inheritdoc/>
        public string Key => UNNRPProtocol.FormatVideonet(VideonetAddress);
        /// <summary>The videonet address.</summary>
        public UInt128 VideonetAddress { get; }
        /// <summary>The tetronet address of the videonet address.</summary>
        public Address Address { get; }

        /// <summary>
        /// Creates the record.
        /// </summary>
        /// <exception cref="ArgumentNullException">address is null</exception>
        /// <exception cref="ArgumentException">address isn't a valid tetronet address</exception>
        public RecordVideoNetwork(UInt128 videonetAddress, Address address)
        {
            VideonetAddress = videonetAddress;
            Address = UNNRPProtocol.CheckAddress(address, nameof(address));
        }

        /// <summary>
        /// Creates the record.
        /// </summary>
        /// <param name="videonetAddress">The videonet address, for example <c>vnet8596226</c></param>
        /// <param name="address">The tetronet address</param>
        /// <exception cref="ArgumentNullException">videonetAddress or address is null</exception>
        /// <exception cref="ArgumentException">videonetAddress isn't a videonet address, or address isn't a valid tetronet address</exception>
        public RecordVideoNetwork(string videonetAddress, Address address)
            : this(UNNRPProtocol.ParseVideonet(videonetAddress, nameof(videonetAddress)), address)
        {
        }
    }

    /// <summary>
    /// Wildcard UNAME (for example <c>*.retalog.com</c>) → another UNNRP server, to which the requests for the names under it are redirected.
    /// The wildcard covers the names of any depth under it (<c>a.retalog.com</c>, <c>a.b.retalog.com</c>), but not <c>retalog.com</c> itself.
    /// </summary>
    public sealed class RecordNameSpaceServer : IResolveRecord
    {
        /// <summary>The type of this record.</summary>
        public const byte RecordType = 0x40;
        /// <inheritdoc/>
        public byte Type => RecordType;
        /// <inheritdoc/>
        public string Key => Wildcard;
        /// <summary>The wildcard, for example <c>*.retalog.com</c>.</summary>
        public string Wildcard { get; }
        /// <summary>The address of the UNNRP server, to which the names under the wildcard are redirected.</summary>
        public Address Server { get; }

        /// <summary>
        /// Creates the record.
        /// </summary>
        /// <param name="wildcard"><c>*.</c> followed by a valid UNAME, that doesn't begin with a dot, for example <c>*.retalog.com</c></param>
        /// <param name="server">The address of the other UNNRP server</param>
        /// <exception cref="ArgumentNullException">wildcard or server is null</exception>
        /// <exception cref="ArgumentException">wildcard or server isn't valid</exception>
        public RecordNameSpaceServer(string wildcard, Address server)
        {
            ArgumentNullException.ThrowIfNull(wildcard);
            string suffix = wildcard.StartsWith("*.", StringComparison.Ordinal) ? wildcard[2..] : "";
            if (!UNNRPProtocol.IsValidUname(suffix) || suffix.StartsWith('.') || wildcard.Length > UNNRPProtocol.MaxBodyLength)
            {
                throw new ArgumentException($"'{wildcard}' isn't a wildcard: it must be '*.' followed by a valid UNAME, that doesn't begin with a dot", nameof(wildcard));
            }
            Wildcard = wildcard;
            Server = UNNRPProtocol.CheckAddress(server, nameof(server));
        }
    }

    /// <summary>
    /// The parts of UNNRP, that the client, the server and the records share (see PROTOCOLS.md).
    /// </summary>
    internal static class UNNRPProtocol
    {
        public const string QueryType = "usernetwork";
        public const uint MinConnectionID = 4000;
        public const uint MaxConnectionID = 800000000;
        public const byte RequestMarker = 0x10;
        public const byte ResponseMarker = 0x50;
        public const byte RequestMapping = 0x01;
        public const byte RequestRecordType = 0x3F;
        public const byte RequestPing = 0x60;
        public const byte ResponseResult = 0x01;
        public const byte ResponseOk = 0x02;
        public const byte ResponseRedirect = 0x03;
        public const byte ResponseError = 0x04;
        public const byte ResponseType = 0x05;
        /// <summary>The most data of one tetronet packet.</summary>
        public const int MaxPacketData = ushort.MaxValue;
        /// <summary>The longest phrase of a request and the longest body of a response: the packet minus the 2-byte header.</summary>
        public const int MaxBodyLength = MaxPacketData - 2;
        public const string VideonetPrefix = "vnet";

        /// <summary>
        /// Only English letters, digits, '-', '_' and '.', but not two dots in a row; at least one character.
        /// </summary>
        public static bool IsValidUname(string? uname)
        {
            if (string.IsNullOrEmpty(uname) || uname.Contains(".."))
            {
                return false;
            }
            foreach (char c in uname)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The phrase is a tetronet address: only digits and '-', not at the ends and not two in a row.
        /// </summary>
        public static bool IsTetronetAddress(string phrase) => Address.Validate(new Address(phrase));

        /// <summary>
        /// The phrase looks like a videonet address: "vnet" in any case followed by decimal digits only (it may still be too big).
        /// </summary>
        public static bool LooksLikeVideonet(string phrase) =>
            phrase.Length > VideonetPrefix.Length &&
            phrase.StartsWith(VideonetPrefix, StringComparison.OrdinalIgnoreCase) &&
            phrase.AsSpan(VideonetPrefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;

        /// <summary>
        /// A valid UNAME, that is neither a tetronet address nor a videonet address, so a request can't confuse them.
        /// </summary>
        public static bool IsAllowedUname(string? uname) =>
            IsValidUname(uname) && !IsTetronetAddress(uname!) && !LooksLikeVideonet(uname!) && uname!.Length <= MaxBodyLength;

        public static bool TryParseVideonet(string phrase, out UInt128 value)
        {
            value = 0;
            return LooksLikeVideonet(phrase) &&
                UInt128.TryParse(phrase.AsSpan(VideonetPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        public static string FormatVideonet(UInt128 value) => VideonetPrefix + value.ToString(CultureInfo.InvariantCulture);

        public static UInt128 ParseVideonet(string videonetAddress, string paramName)
        {
            ArgumentNullException.ThrowIfNull(videonetAddress, paramName);
            if (!TryParseVideonet(videonetAddress, out UInt128 value))
            {
                throw new ArgumentException($"'{videonetAddress}' isn't a videonet address: it must be 'vnet' followed by a decimal number from 0 to 2^128-1", paramName);
            }
            return value;
        }

        public static string CheckUname(string uname, string paramName)
        {
            ArgumentNullException.ThrowIfNull(uname, paramName);
            if (!IsAllowedUname(uname))
            {
                throw new ArgumentException($"'{uname}' isn't a valid UNAME: only English letters, digits, '-', '_' and '.' (not two dots in a row) are allowed, " +
                    "and it can't be a tetronet address or a videonet address", paramName);
            }
            return uname;
        }

        public static Address CheckAddress(Address address, string paramName)
        {
            ArgumentNullException.ThrowIfNull(address, paramName);
            if (!Address.Validate(address) || address.IsEmpty)
            {
                throw new ArgumentException($"'{address}' isn't a valid tetronet address", paramName);
            }
            return Address.Copy(address);
        }

        public static byte[] Response(byte type, string? body = null)
        {
            int length = body?.Length ?? 0;
            byte[] data = new byte[2 + length];
            data[0] = ResponseMarker;
            data[1] = type;
            if (body != null)
            {
                System.Text.Encoding.ASCII.GetBytes(body, 0, body.Length, data, 2);
            }
            return data;
        }
    }
}
