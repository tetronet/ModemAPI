namespace ModemAPI
{
    /// <summary>
    /// Represents types of L1 (physical) wires for the Tetronet Communication.
    /// </summary>
    public enum L1Types
    {
        /// <summary>
        /// Use Serial port as a L1 for the Tetronet.
        /// </summary>
        L1_SERIAL = 0,
        /// <summary>
        /// Use Transmission Control Protocol / Internet Protocol (TCP/IP) as a L1 for the Tetronet.
        /// </summary>
        L1_INET_TCP = 1,
        /// <summary>
        /// Use User Datagram Protocol / Internet Protocol (UDP/IP) as a L1 for the Tetronet.
        /// </summary>
        L1_INET_UDP = 2,
    }
}
