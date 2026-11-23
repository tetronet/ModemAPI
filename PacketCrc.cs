using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.Intrinsics.X86;

namespace ModemAPI
{
    /// <summary>
    /// CRC-32 of the packets: the same CRC as <see cref="Crc32"/> (IEEE 802.3, the same value for the same bytes), only faster on
    /// processors without carry-less multiplication. <see cref="Crc32"/> uses PCLMULQDQ on x86 (about 10 GB/s and more), and
    /// without it goes byte by byte: 0.4 GB/s on a Xeon X3440, so the two passes of a forwarded packet of 59 KB took 290 us and an
    /// address machine choked at about 3000 such packets per second. Slicing by 8 takes 8 bytes per step with 8 tables.
    /// </summary>
    internal static class PacketCrc
    {
        // x86 without PCLMULQDQ (before Westmere): the only processors where the library goes byte by byte. Elsewhere (ARM has its
        // own instructions for this) the library is used as it is
        private static readonly bool UseSlicing = X86Base.IsSupported && !Pclmulqdq.IsSupported;

        private static readonly uint[] T0 = new uint[256];
        private static readonly uint[] T1 = new uint[256];
        private static readonly uint[] T2 = new uint[256];
        private static readonly uint[] T3 = new uint[256];
        private static readonly uint[] T4 = new uint[256];
        private static readonly uint[] T5 = new uint[256];
        private static readonly uint[] T6 = new uint[256];
        private static readonly uint[] T7 = new uint[256];

        static PacketCrc()
        {
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    // the reflected polynomial of CRC-32
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
                T0[i] = crc;
            }
            // Tk[i]: the CRC of byte i followed by k zero bytes
            uint[][] tables = [T0, T1, T2, T3, T4, T5, T6, T7];
            for (int k = 1; k < 8; k++)
            {
                for (int i = 0; i < 256; i++)
                {
                    uint previous = tables[k - 1][i];
                    tables[k][i] = (previous >> 8) ^ T0[previous & 0xFF];
                }
            }
        }

        /// <summary>
        /// The same as <see cref="Crc32.HashToUInt32(ReadOnlySpan{byte})"/>.
        /// </summary>
        public static uint HashToUInt32(ReadOnlySpan<byte> data)
        {
            return UseSlicing ? SliceBy8(data) : Crc32.HashToUInt32(data);
        }

        /// <summary>
        /// Slicing by 8, whatever the processor (internal for the tests).
        /// </summary>
        internal static uint SliceBy8(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            while (data.Length >= 8)
            {
                uint low = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
                uint high = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                crc = T7[(byte)low] ^ T6[(byte)(low >> 8)] ^ T5[(byte)(low >> 16)] ^ T4[low >> 24]
                    ^ T3[(byte)high] ^ T2[(byte)(high >> 8)] ^ T1[(byte)(high >> 16)] ^ T0[high >> 24];
                data = data[8..];
            }
            foreach (byte b in data)
            {
                crc = T0[(byte)(crc ^ b)] ^ (crc >> 8);
            }
            return ~crc;
        }
    }
}
