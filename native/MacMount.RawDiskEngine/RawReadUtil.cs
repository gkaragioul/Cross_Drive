using System;
using System.Threading;
using System.Threading.Tasks;

namespace MacMount.RawDiskEngine;

internal static class RawReadUtil
{
    private const int Alignment = 4096;

    public static async Task<int> ReadExactlyAtAsync(IRawBlockDevice device, long offset, byte[] buffer, int count, CancellationToken cancellationToken = default)
    {
        if (count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (count <= 0) return 0;
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

        var alignedStart = (offset / Alignment) * Alignment;
        var alignedEnd = ((offset + count + Alignment - 1) / Alignment) * Alignment;
        var alignedLength = (int)Math.Max(Alignment, alignedEnd - alignedStart);
        var scratch = new byte[alignedLength];

        var totalAligned = 0;
        while (totalAligned < alignedLength)
        {
            var chunk = new byte[alignedLength - totalAligned];
            var n = await device.ReadAsync(alignedStart + totalAligned, chunk, chunk.Length, cancellationToken).ConfigureAwait(false);
            if (n <= 0) break;
            Buffer.BlockCopy(chunk, 0, scratch, totalAligned, n);
            totalAligned += n;
        }

        var sliceOffset = (int)(offset - alignedStart);
        var available = Math.Max(0, totalAligned - sliceOffset);
        var toCopy = Math.Min(count, available);
        if (toCopy > 0)
        {
            Buffer.BlockCopy(scratch, sliceOffset, buffer, 0, toCopy);
        }

        return toCopy;
    }
}
