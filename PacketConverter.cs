using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ModemAPI
{
    public class PacketConverter
    {
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
                return Packet.Static.ErroredPacket;
                //throw new ArgumentNullException(nameof(message), "websocket message must nothave null in all fields except metadata");
            }
            Packet packet = new();
            packet.ConnectionID = (uint)message.package_info.connectionid;
            packet.IsLastInSequence = (bool)message.package_info.is_last_in_package_queue;
            packet.QueryType = message.package_info.package_type;
            List<string?> receiverAddressList = new();
            List<string?> transmitterAddressList = new();
            foreach (object o in message.package_info.to)
            {
                if (o.ToString() != null)
                {
                    receiverAddressList.Add(o.ToString());
                }
            }
            foreach (object o in message.package_info.from)
            {
                if (o.ToString() != null)
                {
                    transmitterAddressList.Add(o.ToString());
                }
            }
            packet.Receiver = new(string.Join("-", receiverAddressList.ToArray()));
            packet.Transmitter = new(string.Join("-", transmitterAddressList.ToArray()));
            packet.DataBytes = [.. Convert.FromBase64String(message.package_data)];
            packet.Metadata = message.metadata;
            packet.PacketNo = (byte)message.package_info.package_no;
            packet.MessageId = (ulong)message.package_info.message_id;
            return packet;
        }

        public static WebSocketMessage PacketToWebSocketMessage(Packet packet)
        {
            WebSocketMessage message = new();
            message.package_data = Convert.ToBase64String(packet.DataBytes.ToArray());
            message.metadata = packet.Metadata;
            message.package_info = new();
            if (packet.Receiver.AddressValue == null)
            {
                throw new NullAddressException("receiver address null");
            }
            if (packet.Transmitter.AddressValue == null)
            {
                throw new NullAddressException("transmitter address null");
            }
            message.package_info.to = packet.Receiver.AddressValue.Split('-').Cast<object>().ToList();
            message.package_info.from = packet.Transmitter.AddressValue.Split('-').Cast<object>().ToList();
            message.package_info.is_last_in_package_queue = packet.IsLastInSequence;
            message.package_info.connectionid = packet.ConnectionID;
            message.package_info.package_type = packet.QueryType;
            message.package_info.package_no = packet.PacketNo;
            message.package_info.message_id = packet.MessageId;
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

        // todo: add support for new message id thing
        public static Packet BytesToPacket(byte[] bytes_)
        {
            ReadOnlySpan<byte> bytes = bytes_;
            int offset = 0;
            if (bytes.Length < 8)
            {
                return Packet.Static.ErroredPacket;
            }
            Packet prepare = new();
            bool success = false;
            ReadOnlySpan<byte> remoteHash = bytes.Slice(bytes.Length - 4, 4);
            ReadOnlySpan<byte> localHash = Crc32.Hash(bytes.Slice(0, bytes.Length - 4));
            if (bytes.Slice(offset, 4).SequenceEqual(CIoCILDefaultPhrases.PacketTransferHeader) && localHash.SequenceEqual(remoteHash))
            {
                offset += 4;
                ModemAPIDebugger.OutputDebugMessage("Converting the packet to byte[] at 94 line in PacketConverter.cs");
                // Get receiver address for packet
                byte addressReceiverLength = bytes[offset];
                offset += 1;
                ReadOnlySpan<byte> addressReceiver = bytes.Slice(offset, addressReceiverLength);
                offset += addressReceiverLength;
                // Get transmitter address for packet
                byte addressTransmitterLength = bytes[offset];
                offset += 1;
                ReadOnlySpan<byte> addressTransmitter = bytes.Slice(offset, addressTransmitterLength);
                offset += addressTransmitterLength;
                // Get query type for packet
                byte queryTypeLength = bytes[offset];
                offset += 1;
                ReadOnlySpan<byte> queryType = bytes.Slice(offset, queryTypeLength);
                offset += queryTypeLength;
                // Get connection ID for packet
                ReadOnlySpan<byte> connectid = bytes.Slice(offset, 4);
                offset += 4;
                // Get is last in packet sequence address for packet
                byte isLastInPacketSequence = bytes[offset];
                offset += 1;
                // Get packet transmission No.
                ulong transmissionNo_ = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(offset));
                offset += 8;
                // Get packet message id
                ulong messageId_ = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(offset));
                offset += 8;
                // Get data for packet
                ushort packetDataLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset));
                offset += 2;
                ReadOnlySpan<byte> packetData = bytes.Slice(offset, packetDataLength);
                offset += packetDataLength;
                // Get metadata for packet
                ushort packetMetadataLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset));
                offset += 2;
                ReadOnlySpan<byte> packetMetadata = bytes.Slice(offset, packetMetadataLength);
                offset += packetMetadataLength;
                // Preparing data for out brand new packet
                Address addressReceiver_ = new(Encoding.ASCII.GetString(addressReceiver));
                Address addressTransmitter_ = new(Encoding.ASCII.GetString(addressTransmitter));
                string queryType_ = Encoding.ASCII.GetString(queryType);
                uint connectid_ = BinaryPrimitives.ReadUInt32BigEndian(connectid);
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
                    return Packet.Static.ErroredPacket;
                }
                byte[] packetDataBytes_ = [.. packetData];
                string packetMetadata_ = Encoding.ASCII.GetString(packetMetadata);
                foreach (char c in packetMetadata_)
                {
                    if (PrintableCharacterList.IsCharacherWrong(c))
                    {
                        throw new WrongCharacherException("metadata must follow valid charlist");
                    }
                }
                // Move all of the readen data to the preparing packet
                prepare.Receiver = addressReceiver_;
                prepare.Transmitter = addressTransmitter_;
                prepare.QueryType = queryType_;
                prepare.ConnectionID = connectid_;
                prepare.IsLastInSequence = isLastInPacketSequence_;
                prepare.DataBytes = packetDataBytes_;
                prepare.Metadata = packetMetadata_;
                prepare.PacketNo = transmissionNo_;
                prepare.MessageId = messageId_;
                success = true;
                ModemAPIDebugger.OutputDebugMessage("==== [at PacketConverter.cs at line 170] ====");
                ModemAPIDebugger.PrintOutPacket(prepare);
            }
            prepare.IsErrorWhileReading = !success;
            return prepare;
        }

        public static byte[] PacketToBytes(Packet packet)
        {
            if (packet.Receiver?.AddressValue == null)
                throw new NullAddressException("packet address receiver null");

            if (packet.Transmitter?.AddressValue == null)
                throw new NullAddressException("packet address transmitter null");

            var buffer = new List<byte>(1600);

            // Header
            buffer.AddRange(CIoCILDefaultPhrases.PacketTransferHeader);

            // Receiver
            byte[] receiverBytes = Encoding.ASCII.GetBytes(packet.Receiver.AddressValue);
            buffer.Add((byte)receiverBytes.Length);
            buffer.AddRange(receiverBytes);

            // Transmitter
            byte[] transmitterBytes = Encoding.ASCII.GetBytes(packet.Transmitter.AddressValue);
            buffer.Add((byte)transmitterBytes.Length);
            buffer.AddRange(transmitterBytes);

            // Query type
            byte[] queryBytes = Encoding.ASCII.GetBytes(packet.QueryType);
            buffer.Add((byte)queryBytes.Length);
            buffer.AddRange(queryBytes);

            // Connection ID
            Span<byte> connectionBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(connectionBytes, packet.ConnectionID);
            buffer.AddRange(connectionBytes);

            // Flags
            buffer.Add(packet.IsLastInSequence
                ? CIoCILDefaultPhrases.CIoCILTrue[0]
                : CIoCILDefaultPhrases.CIoCILFalse[0]);

            // Packet No
            Span<byte> pn = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(pn, packet.PacketNo);
            buffer.AddRange(pn);

            // Message ID
            Span<byte> mid = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(mid, packet.MessageId);
            buffer.AddRange(mid);

            // Data
            if (packet.DataBytes.Length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(packet), "packet data too large");

            Span<byte> dataLengthBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(dataLengthBytes, (ushort)packet.DataBytes.Length);
            buffer.AddRange(dataLengthBytes);
            buffer.AddRange(packet.DataBytes);

            // Metadata
            byte[] metadataBytes = packet.Metadata != null
                ? Encoding.ASCII.GetBytes(packet.Metadata)
                : [];

            if (metadataBytes.Length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException("packet metadata too large");

            Span<byte> metadataLengthBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(metadataLengthBytes, (ushort)metadataBytes.Length);
            buffer.AddRange(metadataLengthBytes);
            buffer.AddRange(metadataBytes);

            // CRC
            byte[] crc = Crc32.Hash(buffer.ToArray());
            buffer.AddRange(crc);

            return buffer.ToArray();
        }

        public static CIoCILLowLatShortResponseBody ParseLowLatencyConnectionResponse(byte[] response)
        {
            List<byte> responseCopy = [.. response];
            CIoCILLowLatShortResponseBody result = new CIoCILLowLatShortResponseBody();
            // check if valid response
            if (!response.Take(13).SequenceEqual(CIoCILLowLatShortDefaultPhrases.InitConnectionPrefixResponse))
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
            // read flags
            result.RemoteAddressMachineSupportsLargeMessageOperation = (response[13] & 0b00000001) != 0;
            result.ContainsSMLA = (response[13] & 0b00000010) != 0;
            result.SpecifiesPacketMTU = (response[13] & 0b00001000) != 0;
            result.SpecifiesLargeMessageMTU = (response[13] & 0b00010000) != 0;
            result.SpecifiesConnectionTimeoutInNanoseconds = (response[13] & 0b00100000) != 0;
            responseCopy.RemoveAt(0);
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
            return bytes.Length > 8 && bytes.Take(4).SequenceEqual(CIoCILDefaultPhrases.PacketTransferHeader);
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
