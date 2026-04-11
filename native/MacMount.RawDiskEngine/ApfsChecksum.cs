using System.Buffers.Binary;

namespace MacMount.RawDiskEngine;

/// <summary>
/// Fletcher-64 checksum for APFS on-disk objects.
///
/// Every APFS object block stores a 64-bit checksum at bytes 0-7 (the o_cksum field).
/// The checksum is computed over the entire block with those first 8 bytes treated as zero.
///
/// Algorithm (Apple APFS Reference, Appendix B):
///   c0 = c1 = 0
///   for each 32-bit LE word w (bytes 0–7 treated as zero):
///     c0 = (c0 + w) % 0xFFFFFFFF
///     c1 = (c1 + c0) % 0xFFFFFFFF
///   f0 = 0xFFFFFFFF − ((c0 + c1) % 0xFFFFFFFF)
///   f1 = 0xFFFFFFFF − ((c0 + f0) % 0xFFFFFFFF)
///   stored_checksum = (f1 &lt;&lt; 32) | f0
/// </summary>
internal static class ApfsChecksum
{
    private const uint M = 0xFFFFFFFF;

    /// <summary>
    /// Computes the APFS checksum value to store in bytes 0-7.
    /// Treats the first 8 bytes of <paramref name="block"/> as zero regardless of their contents.
    /// </summary>
    public static ulong Compute(ReadOnlySpan<byte> block)
    {
        ulong c0 = 0, c1 = 0;
        for (int i = 0; i < block.Length; i += 4)
        {
            // First two 32-bit words (bytes 0-7) are the checksum field — treat as zero.
            uint word = i < 8 ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i, 4));
            c0 = (c0 + word) % M;
            c1 = (c1 + c0) % M;
        }
        ulong f0 = M - ((c0 + c1) % M);
        ulong f1 = M - ((c0 + f0) % M);
        return (f1 << 32) | f0;
    }

    /// <summary>
    /// Computes the checksum and writes it into bytes 0-7 of <paramref name="block"/>.
    /// </summary>
    public static void WriteChecksum(Span<byte> block)
    {
        var checksum = Compute(block);
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0, 8), checksum);
    }

    /// <summary>
    /// Returns true if the checksum stored in bytes 0-7 matches the computed checksum.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> block) =>
        Compute(block) == BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(0, 8));
}
