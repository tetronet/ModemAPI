using System.IO.Hashing;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// The CRC of processors without PCLMULQDQ must give exactly what <see cref="Crc32"/> gives: it is the CRC on the wire.
    /// </summary>
    public class PacketCrcTests
    {
        [Fact]
        public void SlicingGivesTheSameCrcAsTheLibraryForEveryLengthAndOffset()
        {
            byte[] data = new byte[70000];
            new Random(5).NextBytes(data);
            // every length up to 64 (all the tails after the 8-byte steps), then longer ones, from unaligned offsets too
            foreach (int length in Enumerable.Range(0, 65).Concat([100, 1399, 1400, 4097, 59000, 65507]))
            {
                foreach (int offset in new[] { 0, 1, 3, 7 })
                {
                    ReadOnlySpan<byte> part = data.AsSpan(offset, length);
                    Assert.Equal(Crc32.HashToUInt32(part), PacketCrc.SliceBy8(part));
                }
            }
        }

        [Fact]
        public void KnownValue()
        {
            // the check value of CRC-32: "123456789" gives 0xCBF43926
            Assert.Equal(0xCBF43926u, PacketCrc.SliceBy8("123456789"u8));
            Assert.Equal(0xCBF43926u, PacketCrc.HashToUInt32("123456789"u8));
        }
    }
}
