namespace ModemAPI
{
    /// <summary>
    /// Why a received packet couldn't be read (<see cref="Packet.IsErrorWhileReading"/>).
    /// </summary>
    public enum PacketReadError
    {
        /// <summary>
        /// The packet was read fine.
        /// </summary>
        None = 0,
        /// <summary>
        /// It isn't a packet: it's too short, or it doesn't start with the packet header.
        /// </summary>
        NotAPacket = 1,
        /// <summary>
        /// The CRC-32 doesn't match: the packet got damaged on the way.
        /// </summary>
        CrcMismatch = 2,
        /// <summary>
        /// The CRC-32 matches, but the fields don't fit the format (lengths, flags): a sender with a different version of the protocol, or a broken one.
        /// </summary>
        WrongFormat = 3,
        /// <summary>
        /// The metadata contains characters, that aren't allowed in it.
        /// </summary>
        WrongMetadata = 4,
    }
}
