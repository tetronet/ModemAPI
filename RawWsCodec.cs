using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace ModemAPI
{
    /// <summary>
    /// The "data-transmission" message of RAWWS, written and read straight from UTF-8 bytes. Gives exactly the bytes of
    /// <c>JsonSerializer.SerializeToUtf8Bytes(new object[] { "data-transmission", PacketConverter.PacketToWebSocketMessage(packet) })</c>
    /// (the same <see cref="Utf8JsonWriter"/> with the same default encoder, the same order of the properties), and reads the
    /// messages, that the server sends, without strings, lists and reflection in between.
    /// </summary>
    internal sealed class RawWsCodec
    {
        private static ReadOnlySpan<byte> DataTransmission => "data-transmission"u8;

        // ---------- writing ----------

        private readonly ArrayBufferWriter<byte> Output = new(4096);
        private readonly Utf8JsonWriter Writer;

        public RawWsCodec()
        {
            Writer = new Utf8JsonWriter(Output);
        }

        /// <summary>
        /// Writes the message of a packet; the result is valid until the next call. Not thread safe.
        /// </summary>
        public ReadOnlyMemory<byte> WriteDataTransmission(Packet packet)
        {
            string receiver = packet.Receiver.AddressValue ?? throw new NullAddressException("receiver address null");
            string transmitter = packet.Transmitter.AddressValue ?? throw new NullAddressException("transmitter address null");
            Output.ResetWrittenCount();
            Writer.Reset(Output);
            Writer.WriteStartArray();
            Writer.WriteStringValue(DataTransmission);
            Writer.WriteStartObject();
            WriteBase64(Writer, packet.DataBytes);
            Writer.WriteString("metadata"u8, packet.Metadata);
            Writer.WriteStartObject("package_info"u8);
            Writer.WriteNumber("connectionid"u8, packet.ConnectionID);
            WriteAddressParts(Writer, "from"u8, transmitter);
            WriteAddressParts(Writer, "to"u8, receiver);
            Writer.WriteString("package_type"u8, packet.QueryType);
            Writer.WriteBoolean("is_last_in_package_queue"u8, packet.IsLastInSequence);
            Writer.WriteNumber("package_no"u8, packet.PacketNo);
            Writer.WriteNumber("message_id"u8, packet.MessageId);
            Writer.WriteNumber("max_hops"u8, packet.RoutersToPass);
            Writer.WriteEndObject();
            Writer.WriteEndObject();
            Writer.WriteEndArray();
            Writer.Flush();
            return Output.WrittenMemory;
        }

        private static void WriteBase64(Utf8JsonWriter writer, byte[] data)
        {
            // the string of Convert.ToBase64String, escaped by hand the way the default encoder of the writer does it: of the 65
            // characters of base64 it escapes only "+" (as +); escaping it character by character in the writer is slow
            int length = Base64.GetMaxEncodedToUtf8Length(data.Length);
            byte[] rented = ArrayPool<byte>.Shared.Rent(length * 2 + 2);
            try
            {
                Span<byte> base64 = rented.AsSpan(length + 2);
                Base64.EncodeToUtf8(data, base64, out _, out int written);
                base64 = base64[..written];
                int pluses = base64.Count((byte)'+');
                byte[] json = pluses == 0 ? rented : ArrayPool<byte>.Shared.Rent(written + pluses * 5 + 2);
                try
                {
                    Span<byte> output = json;
                    int position = 0;
                    output[position++] = (byte)'"';
                    while (true)
                    {
                        int plus = base64.IndexOf((byte)'+');
                        ReadOnlySpan<byte> run = plus < 0 ? base64 : base64[..plus];
                        // copied forward: with no "+" the output is the same buffer in front of the base64, so it never overtakes it
                        run.CopyTo(output[position..]);
                        position += run.Length;
                        if (plus < 0)
                        {
                            break;
                        }
                        @"\u002B"u8.CopyTo(output[position..]);
                        position += 6;
                        base64 = base64[(plus + 1)..];
                    }
                    output[position++] = (byte)'"';
                    writer.WritePropertyName("package_data"u8);
                    writer.WriteRawValue(output[..position], skipInputValidation: true);
                }
                finally
                {
                    if (json != rented)
                    {
                        ArrayPool<byte>.Shared.Return(json);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static void WriteAddressParts(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string address)
        {
            // the parts of address.Split('-'), empty ones too
            writer.WriteStartArray(name);
            ReadOnlySpan<char> rest = address;
            while (true)
            {
                int dash = rest.IndexOf('-');
                if (dash < 0)
                {
                    writer.WriteStringValue(rest);
                    break;
                }
                writer.WriteStringValue(rest[..dash]);
                rest = rest[(dash + 1)..];
            }
            writer.WriteEndArray();
        }

        // ---------- reading ----------

        // the same addresses and query types come in message after message: the last strings are reused
        private readonly CachedString LastReceiver = new();
        private readonly CachedString LastTransmitter = new();
        private readonly CachedString LastQueryType = new();
        private readonly CachedString LastMetadata = new();
        private byte[] AddressBuffer = new byte[256];
        // strings with escapes ("+" of base64 is "+" from JsonSerializer, for example) are unescaped here
        private byte[] UnescapeBuffer = new byte[4096];

        private sealed class CachedString
        {
            private byte[] Utf8 = [];
            private string Value = "";

            public string Get(ReadOnlySpan<byte> utf8)
            {
                if (!utf8.SequenceEqual(Utf8))
                {
                    Value = Encoding.UTF8.GetString(utf8);
                    Utf8 = utf8.ToArray();
                }
                return Value;
            }
        }

        /// <summary>
        /// Tells if a message is "data-transmission": exactly the name between its first two quotes.
        /// </summary>
        public static bool IsDataTransmission(ReadOnlySpan<byte> message)
        {
            int start = message.IndexOf((byte)'"');
            if (start < 0)
            {
                return false;
            }
            int end = message[(start + 1)..].IndexOf((byte)'"');
            return end >= 0 && message.Slice(start + 1, end).SequenceEqual(DataTransmission);
        }

        /// <summary>
        /// Reads a "data-transmission" message of the usual shape: <c>["data-transmission", {...}]</c> with all the fields of the packet,
        /// plain ASCII (escapes in the strings are fine). Returns false for anything else (nothing is changed then); such a message is read
        /// the old way, which handles every case as before. Not thread safe.
        /// </summary>
        public bool TryReadDataTransmission(ReadOnlySpan<byte> message, out Packet? packet)
        {
            packet = null;
            // the old way reads the message as ASCII: every other byte becomes '?'
            if (message.IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0)
            {
                return false;
            }
            try
            {
                return TryRead(message, out packet);
            }
            catch (Exception e) when (e is JsonException || e is InvalidOperationException)
            {
                // broken JSON, or an escape, that isn't valid UTF-16 (a lone surrogate)
                packet = null;
                return false;
            }
        }

        private ref struct Fields
        {
            public byte[]? Data;
            public bool HasData;
            public string? Metadata;
            public bool HasInfo;
            public uint? ConnectionId;
            public string? From;
            public string? To;
            public string? QueryType;
            public bool? IsLast;
            public ulong? PacketNo;
            public ulong? MessageId;
            public ushort? MaxHops;
        }

        private bool TryRead(ReadOnlySpan<byte> message, out Packet? packet)
        {
            packet = null;
            Utf8JsonReader reader = new(message);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return false;
            }
            // the event name
            if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
            {
                return false;
            }
            reader.Skip();
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }
            Fields fields = new();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("package_data"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String || !TryDecodeBase64(Unescaped(ref reader), out fields.Data))
                    {
                        return false;
                    }
                    fields.HasData = true;
                }
                else if (reader.ValueTextEquals("metadata"u8))
                {
                    if (!TryReadString(ref reader, LastMetadata, out fields.Metadata))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("package_info"u8))
                {
                    reader.Read();
                    // a repeated package_info replaces the earlier one completely, as when it is deserialized
                    fields.ConnectionId = null;
                    fields.From = fields.To = fields.QueryType = null;
                    fields.IsLast = null;
                    fields.PacketNo = fields.MessageId = null;
                    fields.MaxHops = null;
                    if (reader.TokenType != JsonTokenType.StartObject || !TryReadInfo(ref reader, ref fields))
                    {
                        return false;
                    }
                    fields.HasInfo = true;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject)
            {
                return false;
            }
            // the rest of the array, then nothing
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                reader.Skip();
            }
            if (reader.TokenType != JsonTokenType.EndArray || reader.Read())
            {
                return false;
            }
            if (!fields.HasData || !fields.HasInfo || fields.ConnectionId == null || fields.From == null || fields.To == null
                || fields.QueryType == null || fields.IsLast == null || fields.PacketNo == null || fields.MessageId == null)
            {
                return false;
            }
            packet = new Packet(new Address(fields.From), new Address(fields.To), fields.Data!, fields.IsLast.Value, fields.Metadata, fields.QueryType,
                fields.ConnectionId.Value, false, fields.PacketNo.Value, fields.MessageId.Value, fields.MaxHops ?? 65535);
            return true;
        }

        private bool TryReadInfo(ref Utf8JsonReader reader, ref Fields fields)
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("connectionid"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt32(out uint value))
                    {
                        return false;
                    }
                    fields.ConnectionId = value;
                }
                else if (reader.ValueTextEquals("from"u8))
                {
                    if (!TryReadAddress(ref reader, LastTransmitter, out fields.From))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("to"u8))
                {
                    if (!TryReadAddress(ref reader, LastReceiver, out fields.To))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("package_type"u8))
                {
                    if (!TryReadString(ref reader, LastQueryType, out fields.QueryType))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("is_last_in_package_queue"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False)
                    {
                        fields.IsLast = reader.TokenType == JsonTokenType.True;
                    }
                    else
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("package_no"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out ulong value))
                    {
                        return false;
                    }
                    fields.PacketNo = value;
                }
                else if (reader.ValueTextEquals("message_id"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out ulong value))
                    {
                        return false;
                    }
                    fields.MessageId = value;
                }
                else if (reader.ValueTextEquals("max_hops"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt16(out ushort value))
                    {
                        return false;
                    }
                    fields.MaxHops = value;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            return reader.TokenType == JsonTokenType.EndObject;
        }

        // the value of the current string token, unescaped; valid until the next call
        private ReadOnlySpan<byte> Unescaped(ref Utf8JsonReader reader)
        {
            if (!reader.ValueIsEscaped)
            {
                return reader.ValueSpan;
            }
            // unescaped is never longer than escaped
            if (UnescapeBuffer.Length < reader.ValueSpan.Length)
            {
                UnescapeBuffer = new byte[Math.Max(reader.ValueSpan.Length, UnescapeBuffer.Length * 2)];
            }
            int written = reader.CopyString(UnescapeBuffer);
            return UnescapeBuffer.AsSpan(0, written);
        }

        // a string or null
        private bool TryReadString(ref Utf8JsonReader reader, CachedString cache, out string? value)
        {
            reader.Read();
            value = null;
            if (reader.TokenType == JsonTokenType.Null)
            {
                return true;
            }
            if (reader.TokenType != JsonTokenType.String)
            {
                return false;
            }
            value = cache.Get(Unescaped(ref reader));
            return true;
        }

        // an array of strings or numbers (their text as it is), joined with '-'
        private bool TryReadAddress(ref Utf8JsonReader reader, CachedString cache, out string? value)
        {
            reader.Read();
            value = null;
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                return false;
            }
            int length = 0;
            bool first = true;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String && reader.TokenType != JsonTokenType.Number)
                {
                    return false;
                }
                ReadOnlySpan<byte> part = reader.TokenType == JsonTokenType.String ? Unescaped(ref reader) : reader.ValueSpan;
                int needed = length + (first ? 0 : 1) + part.Length;
                if (needed > AddressBuffer.Length)
                {
                    Array.Resize(ref AddressBuffer, Math.Max(needed, AddressBuffer.Length * 2));
                }
                if (!first)
                {
                    AddressBuffer[length++] = (byte)'-';
                }
                part.CopyTo(AddressBuffer.AsSpan(length));
                length += part.Length;
                first = false;
            }
            value = cache.Get(AddressBuffer.AsSpan(0, length));
            return true;
        }

        private static bool TryDecodeBase64(ReadOnlySpan<byte> base64, out byte[]? data)
        {
            data = null;
            // only the strict form: anything else (whitespace in it, for example) is read the old way
            if (base64.Length % 4 != 0)
            {
                return false;
            }
            int padding = base64.Length == 0 ? 0 : base64[^1] == '=' ? (base64[^2] == '=' ? 2 : 1) : 0;
            byte[] result = new byte[base64.Length / 4 * 3 - padding];
            if (Base64.DecodeFromUtf8(base64, result, out int consumed, out int written) != OperationStatus.Done
                || consumed != base64.Length || written != result.Length)
            {
                return false;
            }
            data = result;
            return true;
        }
    }
}
