using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.IO.Hashing;
using System.Text;

namespace ModemAPI
{
    public class PacketConverter
    {
        /// <summary>
        /// Bytes of a binary packet that don't depend on its contents: header (4), lengths of receiver, transmitter and
        /// query type (1 + 1 + 1), connection ID (4), last-in-sequence flag (1), packet No. (8), message ID (8), TTL (2),
        /// data length (2), metadata length (2) and CRC-32 (4).
        /// </summary>
        private const int FixedPacketSize = 4 + 1 + 1 + 1 + 4 + 1 + 8 + 8 + 2 + 2 + 2 + 4;
        /// <summary>
        /// Lookup table for <see cref="PrintableCharacterList.PrintableCharacters"/>, indexed by byte value.
        /// </summary>
        private static readonly bool[] MetadataByteAllowed = BuildMetadataTable();
        // The same addresses and query types arrive in packet after packet, so the last decoded strings are reused
        // instead of allocating new ones. Strings are immutable, so sharing them between packets is safe.
        [ThreadStatic] private static string? LastReceiver;
        [ThreadStatic] private static string? LastTransmitter;
        [ThreadStatic] private static string? LastQueryType;

        public static Packet WebSocketMessageToPacket(WebSocketMessage message)
        {
            if (message.package_info == null
                || message.package_data == null
                || message.package_info.is_last_in_package_queue == null
                || message.package_info.package_type == null
                || message.package_info.to == null
                || message.package_info.from == null
                || message.package_info.connectionid == null
                || message.package_info.package_no == null
                || message.package_info.message_id == null)
            {
                return Errored(PacketReadError.WrongFormat);
                //throw new ArgumentNullException(nameof(message), "websocket message must nothave null in all fields except metadata");
            }
            return new Packet(
                new Address(JoinAddressParts(message.package_info.from)),
                new Address(JoinAddressParts(message.package_info.to)),
                DecodeBase64(message.package_data),
                (bool)message.package_info.is_last_in_package_queue,
                message.metadata,
                message.package_info.package_type,
                (uint)message.package_info.connectionid,
                false,
                (ulong)message.package_info.package_no,
                (ulong)message.package_info.message_id,
                message.package_info.max_hops ?? 65535);
        }

        public static WebSocketMessage PacketToWebSocketMessage(Packet packet)
        {
            if (packet.Receiver.AddressValue == null)
            {
                throw new NullAddressException("receiver address null");
            }
            if (packet.Transmitter.AddressValue == null)
            {
                throw new NullAddressException("transmitter address null");
            }
            WebSocketMessage message = new();
            message.package_data = Convert.ToBase64String(packet.DataBytes);
            message.metadata = packet.Metadata;
            message.package_info = new();
            message.package_info.to = new List<object>(packet.Receiver.AddressValue.Split('-'));
            message.package_info.from = new List<object>(packet.Transmitter.AddressValue.Split('-'));
            message.package_info.is_last_in_package_queue = packet.IsLastInSequence;
            message.package_info.connectionid = packet.ConnectionID;
            message.package_info.package_type = packet.QueryType;
            message.package_info.package_no = packet.PacketNo;
            message.package_info.message_id = packet.MessageId;
            message.package_info.max_hops = packet.RoutersToPass;
            return message;
        }

        public static string Base64Encode(string plainText)
        {
            byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        public static string Base64Decode(string base64EncodedData)
        {
            byte[] base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
            return Encoding.UTF8.GetString(base64EncodedBytes);
        }

        public static Packet BytesToPacket(byte[] bytes)
        {
            return BytesToPacket(bytes.AsSpan());
        }

        public static Packet BytesToPacket(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 8)
            {
                return Packet.Static.ErroredPacket;
            }
            if (!bytes[..4].SequenceEqual(CIoCILDefaultPhrases.PacketTransferHeader))
            {
                return Errored(PacketReadError.NotAPacket);
            }
            if (Crc32.HashToUInt32(bytes[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]))
            {
                return Errored(PacketReadError.CrcMismatch);
            }
            if (bytes.Length < FixedPacketSize)
            {
                return Errored(PacketReadError.WrongFormat);
            }
            // the CRC-32 can match by chance (or on purpose), so every length is checked against what is really there:
            // a broken packet becomes an errored one instead of an exception in the reader thread of a modem
            ReadOnlySpan<byte> body = bytes[..^4];
            int offset = 4;
            ModemAPIDebugger.OutputDebugMessage("Converting the packet to byte[] at 94 line in PacketConverter.cs");
            // Get receiver address, transmitter address and query type for packet
            if (!TryTake(body, ref offset, 1, out ReadOnlySpan<byte> addressReceiver) ||
                !TryTake(body, ref offset, 1, out ReadOnlySpan<byte> addressTransmitter) ||
                !TryTake(body, ref offset, 1, out ReadOnlySpan<byte> queryType))
            {
                return Errored(PacketReadError.WrongFormat);
            }
            // connection ID (4), last in sequence (1), transmission No. (8), message ID (8), TTL (2)
            if (body.Length - offset < 4 + 1 + 8 + 8 + 2)
            {
                return Errored(PacketReadError.WrongFormat);
            }
            // Get connection ID for packet
            uint connectid = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4));
            offset += 4;
            // Get is last in packet sequence address for packet
            byte isLastInPacketSequence = body[offset];
            offset += 1;
            // Get packet transmission No.
            ulong transmissionNo = BinaryPrimitives.ReadUInt64BigEndian(body.Slice(offset));
            offset += 8;
            // Get packet message id
            ulong messageId = BinaryPrimitives.ReadUInt64BigEndian(body.Slice(offset));
            offset += 8;
            // Get packet TTL
            ushort ttl = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset));
            offset += 2;
            // Get data and metadata for packet
            if (!TryTake(body, ref offset, 2, out ReadOnlySpan<byte> packetData) ||
                !TryTake(body, ref offset, 2, out ReadOnlySpan<byte> packetMetadata))
            {
                return Errored(PacketReadError.WrongFormat);
            }
            bool isLastInPacketSequence_;
            if (isLastInPacketSequence == CIoCILDefaultPhrases.CIoCILTrue[0])
            {
                isLastInPacketSequence_ = true;
            }
            else if (isLastInPacketSequence == CIoCILDefaultPhrases.CIoCILFalse[0])
            {
                isLastInPacketSequence_ = false;
            }
            else
            {
                return Errored(PacketReadError.WrongFormat);
            }
            foreach (byte b in packetMetadata)
            {
                if (!MetadataByteAllowed[b])
                {
                    // metadata must follow the valid charlist
                    return Errored(PacketReadError.WrongMetadata);
                }
            }
            Packet prepare = new(
                new Address(GetAsciiString(addressTransmitter, ref LastTransmitter)),
                new Address(GetAsciiString(addressReceiver, ref LastReceiver)),
                packetData.ToArray(),
                isLastInPacketSequence_,
                // no metadata is null, as Packet.Metadata documents
                packetMetadata.IsEmpty ? null : Encoding.ASCII.GetString(packetMetadata),
                GetAsciiString(queryType, ref LastQueryType),
                connectid,
                false,
                transmissionNo,
                messageId,
                ttl);
            ModemAPIDebugger.OutputDebugMessage("==== [at PacketConverter.cs at line 170] ====");
            ModemAPIDebugger.PrintOutPacket(prepare);
            return prepare;
        }

        private static Packet Errored(PacketReadError reason)
        {
            return new Packet { IsErrorWhileReading = true, ReadError = reason };
        }
        /// <summary>
        /// Takes a field prefixed with its big-endian length of lengthSize bytes, if both fit into the packet.
        /// </summary>
        private static bool TryTake(ReadOnlySpan<byte> body, ref int offset, int lengthSize, out ReadOnlySpan<byte> field)
        {
            field = default;
            if (body.Length - offset < lengthSize)
            {
                return false;
            }
            int length = lengthSize == 1 ? body[offset] : BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset));
            offset += lengthSize;
            if (body.Length - offset < length)
            {
                return false;
            }
            field = body.Slice(offset, length);
            offset += length;
            return true;
        }
        /// <summary>
        /// Calculates the exact length of the packet after converting it with <see cref="PacketToBytes(Packet)"/>.
        /// </summary>
        /// <param name="packet">Packet to measure</param>
        /// <returns>Length of the binary packet in bytes</returns>
        /// <exception cref="NullAddressException">Receiver or transmitter address is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">Some field of the packet doesn't fit into the binary format</exception>
        public static int EstimatePacketSize(Packet packet)
        {
            string receiver = packet.Receiver?.AddressValue ?? throw new NullAddressException("packet address receiver null");
            string transmitter = packet.Transmitter?.AddressValue ?? throw new NullAddressException("packet address transmitter null");
            int receiverLength = AsciiByteCount(receiver);
            int transmitterLength = AsciiByteCount(transmitter);
            int queryTypeLength = AsciiByteCount(packet.QueryType);
            int metadataLength = packet.Metadata == null ? 0 : AsciiByteCount(packet.Metadata);
            if (receiverLength > byte.MaxValue || transmitterLength > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(packet), "packet address must not be longer than 255 characters");
            if (queryTypeLength > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(packet), "packet query type must not be longer than 255 characters");
            if (packet.DataBytes.Length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(packet), "packet data too large");
            if (metadataLength > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(packet), "packet metadata too large");
            return FixedPacketSize + receiverLength + transmitterLength + queryTypeLength + packet.DataBytes.Length + metadataLength;
        }

        public static byte[] PacketToBytes(Packet packet)
        {
            int size = EstimatePacketSize(packet);
            // every byte gets written below, so there's no need to zero the array first
            byte[] result = GC.AllocateUninitializedArray<byte>(size);
            WritePacket(packet, result);
            return result;
        }

        /// <summary>
        /// Converts the packet into the binary format, writing it into a buffer supplied by the caller (for example a pooled one).
        /// </summary>
        /// <param name="packet">Packet to convert</param>
        /// <param name="destination">Buffer, that must be at least <see cref="EstimatePacketSize(Packet)"/> bytes long</param>
        /// <returns>How many bytes were written</returns>
        /// <exception cref="ArgumentException">The buffer is too small</exception>
        public static int PacketToBytes(Packet packet, Span<byte> destination)
        {
            int size = EstimatePacketSize(packet);
            if (destination.Length < size)
            {
                throw new ArgumentException($"destination must be at least {size} bytes long", nameof(destination));
            }
            WritePacket(packet, destination[..size]);
            return size;
        }

        /// <summary>
        /// Writes the packet into a buffer of exactly <see cref="EstimatePacketSize(Packet)"/> bytes.
        /// </summary>
        private static void WritePacket(Packet packet, Span<byte> destination)
        {
            // Header
            CIoCILDefaultPhrases.PacketTransferHeader.CopyTo(destination);
            int offset = 4;
            // Receiver, transmitter and query type, each prefixed with its length
            offset += WriteShortAscii(packet.Receiver.AddressValue!, destination[offset..]);
            offset += WriteShortAscii(packet.Transmitter.AddressValue!, destination[offset..]);
            offset += WriteShortAscii(packet.QueryType, destination[offset..]);
            // Connection ID
            BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], packet.ConnectionID);
            offset += 4;
            // Flags
            destination[offset] = packet.IsLastInSequence
                ? CIoCILDefaultPhrases.CIoCILTrue[0]
                : CIoCILDefaultPhrases.CIoCILFalse[0];
            offset += 1;
            // Packet No
            BinaryPrimitives.WriteUInt64BigEndian(destination[offset..], packet.PacketNo);
            offset += 8;
            // Message ID
            BinaryPrimitives.WriteUInt64BigEndian(destination[offset..], packet.MessageId);
            offset += 8;
            // ttl
            BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], packet.RoutersToPass);
            offset += 2;
            // Data
            BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)packet.DataBytes.Length);
            offset += 2;
            packet.DataBytes.CopyTo(destination[offset..]);
            offset += packet.DataBytes.Length;
            // Metadata
            int metadataLength = packet.Metadata == null ? 0 : Encoding.ASCII.GetBytes(packet.Metadata, destination[(offset + 2)..]);
            BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)metadataLength);
            offset += 2 + metadataLength;
            // CRC, in the same byte order as Crc32.Hash() produces it
            BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], Crc32.HashToUInt32(destination[..offset]));
        }

        /// <summary>
        /// Writes a length byte followed by the ASCII bytes of the string.
        /// </summary>
        /// <returns>How many bytes were written</returns>
        private static int WriteShortAscii(string value, Span<byte> destination)
        {
            int length = Encoding.ASCII.GetBytes(value, destination[1..]);
            destination[0] = (byte)length;
            return length + 1;
        }

        private static int AsciiByteCount(string value)
        {
            // pure ASCII is one byte per char; anything else goes through the encoder, which may merge surrogate pairs into one '?'
            return Ascii.IsValid(value) ? value.Length : Encoding.ASCII.GetByteCount(value);
        }

        private static string GetAsciiString(ReadOnlySpan<byte> bytes, ref string? cache)
        {
            string? cached = cache;
            if (cached != null && cached.Length == bytes.Length && Ascii.Equals(bytes, cached))
            {
                return cached;
            }
            string value = Encoding.ASCII.GetString(bytes);
            cache = value;
            return value;
        }

        /// <summary>
        /// Same result as <see cref="Convert.FromBase64String(string)"/>, but uses the vectorized UTF-8 decoder for the usual
        /// canonical input and leaves everything else (whitespace, wrong length, invalid characters) to Convert.
        /// </summary>
        private static byte[] DecodeBase64(string base64)
        {
            int length = base64.Length;
            if (length == 0 || length % 4 != 0 || length > 1 << 20)
            {
                return Convert.FromBase64String(base64);
            }
            int padding = base64[^1] == '=' ? (base64[^2] == '=' ? 2 : 1) : 0;
            byte[] result = new byte[length / 4 * 3 - padding];
            byte[]? rented = null;
            Span<byte> utf8 = length <= 4096 ? stackalloc byte[length] : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
            try
            {
                // base64 is plain ASCII, so narrowing the chars gives its UTF-8 form; anything non-ASCII fails below anyway
                if (Ascii.FromUtf16(base64, utf8, out _) == OperationStatus.Done &&
                    Base64.DecodeFromUtf8(utf8, result, out _, out int written) == OperationStatus.Done &&
                    written == result.Length)
                {
                    return result;
                }
                return Convert.FromBase64String(base64);
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        private static string JoinAddressParts(List<object> parts)
        {
            if (parts.Count == 1)
            {
                return parts[0].ToString() ?? "";
            }
            string[] values = new string[parts.Count];
            int count = 0;
            foreach (object part in parts)
            {
                string? value = part.ToString();
                if (value != null)
                {
                    values[count++] = value;
                }
            }
            return string.Join('-', values, 0, count);
        }

        private static bool[] BuildMetadataTable()
        {
            bool[] table = new bool[256];
            foreach (char c in PrintableCharacterList.PrintableCharacters)
            {
                if (c < 128)
                {
                    table[c] = true;
                }
            }
            // Encoding.ASCII decodes every byte above 0x7F as '?'
            for (int b = 128; b < 256; b++)
            {
                table[b] = !PrintableCharacterList.IsCharacherWrong('?');
            }
            return table;
        }

        public static CIoCILLowLatShortResponseBody ParseLowLatencyConnectionResponse(byte[] response)
        {
            List<byte> responseCopy = [.. response];
            CIoCILLowLatShortResponseBody result = new CIoCILLowLatShortResponseBody();
            // check if valid response: the prefix, the flags and the CRC at least
            if (response.Length < 13 + 1 + 4 || !response.Take(13).SequenceEqual(CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse))
            {
                result.IsResponseDamaged = true;
                return result;
            }
            // check if no CRC errors were presented in this packet
            if (BinaryPrimitives.ReadUInt32BigEndian([.. response.TakeLast(4)]) != Crc32.HashToUInt32([.. response.SkipLast(4)]))
            {
                result.IsResponseDamaged = true;
                return result;
            }
            responseCopy.RemoveRange(0, 13);
            responseCopy.RemoveRange(responseCopy.Count - 4, 4); // the CRC
            // read flags
            result.RemoteAddressMachineSupportsLargeMessageOperation = (response[13] & 0b00000001) != 0;
            result.ContainsSMLA = (response[13] & 0b00000010) != 0;
            result.SpecifiesPacketMTU = (response[13] & 0b00001000) != 0;
            result.SpecifiesLargeMessageMTU = (response[13] & 0b00010000) != 0;
            result.SpecifiesConnectionTimeoutInNanoseconds = (response[13] & 0b00100000) != 0;
            responseCopy.RemoveAt(0);
            // every field the flags announce must fit into the response
            int needed = (result.ContainsSMLA ? 1 + (responseCopy.Count > 0 ? responseCopy[0] : 0) : 0)
                + (result.SpecifiesPacketMTU ? 2 : 0) + (result.SpecifiesLargeMessageMTU ? 8 : 0) + (result.SpecifiesConnectionTimeoutInNanoseconds ? 8 : 0);
            if (responseCopy.Count < needed)
            {
                result.IsResponseDamaged = true;
                return result;
            }
            // read all available response fields
            if (result.ContainsSMLA) // read SMLA information
            {
                result.PossibleSMLA = new Address(Encoding.ASCII.GetString([.. responseCopy.Skip(1).Take(responseCopy[0])]));
                responseCopy.RemoveRange(0, responseCopy[0] + 1);
            }
            if (result.SpecifiesPacketMTU) // read packet MTU information
            {
                result.PossiblePacketMTU = BinaryPrimitives.ReadUInt16BigEndian([.. responseCopy]);
                responseCopy.RemoveRange(0, 2);
            }
            if (result.SpecifiesLargeMessageMTU) // read large message MTU information
            {
                result.PossibleLargeMessageMTU = BinaryPrimitives.ReadUInt64BigEndian([.. responseCopy]);
                responseCopy.RemoveRange(0, 8);
            }
            if (result.SpecifiesConnectionTimeoutInNanoseconds) // read timeout ns information
            {
                result.PossibleConnectionTimeoutInNanoseconds = BinaryPrimitives.ReadUInt64BigEndian([.. responseCopy]);
                responseCopy.RemoveRange(0, 8);
            }
            // return the resulting structure
            return result;
        }
        public static bool CheckIsPacket(byte[] bytes)
        {
            return bytes.Length > 8 && bytes.AsSpan(0, 4).SequenceEqual(CIoCILDefaultPhrases.PacketTransferHeader);
        }
        public static CIoCILLowLatShortConnectionRequestBody ParseLowLatencyConnectionRequest(byte[] request)
        {
            List<byte> requestCopy = [.. request];
            CIoCILLowLatShortConnectionRequestBody result = new CIoCILLowLatShortConnectionRequestBody();
            if (requestCopy.Count < 17) // not enough bytes
            {
                result.IsRequestDamaged = true;
                return result;
            }
            if (!requestCopy.Take(13).SequenceEqual(CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixRequest)) // not correct header
            {
                result.IsRequestDamaged = true;
                return result;
            }
            if (Crc32.HashToUInt32([.. requestCopy.SkipLast(4)]) != BinaryPrimitives.ReadUInt32BigEndian([..requestCopy.TakeLast(4)])) // crc mismatch
            {
                result.IsRequestDamaged = true;
                return result;
            }
            requestCopy.RemoveRange(0, 13);
            // get and parse address
            byte currentClientAddressLength = requestCopy[0];
            if (requestCopy.Count < 1 + currentClientAddressLength + 1 + 4) // the address, the address machine flag and the CRC must fit
            {
                result.IsRequestDamaged = true;
                return result;
            }
            requestCopy.RemoveAt(0);
            result.CurrentClientAddress = new Address(Encoding.ASCII.GetString([.. requestCopy], 0, currentClientAddressLength));
            requestCopy.RemoveRange(0, currentClientAddressLength);
            // get and parse "Connect as an Address Machine" value
            result.IsAddressMachine = requestCopy[0] == 0;
            // return the result
            return result;
        }
    }
}
