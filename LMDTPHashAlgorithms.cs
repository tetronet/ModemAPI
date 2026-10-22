namespace ModemAPI
{
    /// <summary>
    /// Hash algorithms, which LMDTP can use to check the integrity of a downloaded resource.
    /// A client lists the ones it supports as a bit mask in the last byte of its request; clients that don't send it support only SHA-512.
    /// <para>
    /// What the hash guarantees: the client got exactly the bytes the server's provider described, so data damaged or lost on the
    /// way is detected (SRTP itself has no checksum of the data, it relies on the CRC-32 of every tetronet packet).
    /// What it doesn't guarantee: that the data comes from who you think. The hash travels in the same unauthenticated SRTP session
    /// as the data, so anyone, who can change the data on the way, can change the hash too. This is true for SHA-512 as well:
    /// a cryptographic hash only helps against tampering when it reaches the client through a trusted way, which LMDTP doesn't have.
    /// </para>
    /// </summary>
    [Flags]
    public enum LMDTPHashAlgorithms : byte
    {
        /// <summary>
        /// SHA-512, supported by every LMDTP client and server (about 6 Gbit/s on one core). Collisions can't be made on purpose,
        /// so it is the one to use when the hash is compared with a value from a trusted source (for example the result of a HASH request
        /// made some other way); within one LMDTP download it protects exactly as much as XxHash3.
        /// </summary>
        Sha512 = 0x01,
        /// <summary>
        /// 64-bit XxHash3 with seed 0: not cryptographic, but detects accidental corruption (a random change goes unnoticed with
        /// a chance of about 1 in 2^64) and is about 25 times faster than SHA-512. Collisions can be made on purpose, so it is
        /// no protection against deliberate changes. The hash is sent in the first 8 bytes of the 64-byte hash field, big-endian.
        /// </summary>
        XxHash3 = 0x02,
    }
}
