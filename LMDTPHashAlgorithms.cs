namespace ModemAPI
{
    /// <summary>
    /// Hash algorithms, which LMDTP can use to check the integrity of a downloaded resource.
    /// A client lists the ones it supports as a bit mask in the last byte of its request; clients that don't send it support only SHA-512.
    /// </summary>
    [Flags]
    public enum LMDTPHashAlgorithms : byte
    {
        /// <summary>
        /// SHA-512, supported by every LMDTP client and server (about 6 Gbit/s on one core).
        /// </summary>
        Sha512 = 0x01,
        /// <summary>
        /// 64-bit XxHash3: not cryptographic, but detects corruption and is about 25 times faster than SHA-512.
        /// The hash is sent in the first 8 bytes of the 64-byte hash field, big-endian.
        /// </summary>
        XxHash3 = 0x02,
    }
}
