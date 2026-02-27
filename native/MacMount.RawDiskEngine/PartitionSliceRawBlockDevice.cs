using System;
using System.Threading;
using System.Threading.Tasks;

namespace MacMount.RawDiskEngine;

internal sealed class PartitionSliceRawBlockDevice : IRawBlockDevice
{
    private readonly IRawBlockDevice _parent;
    private readonly long _baseOffset;

    public PartitionSliceRawBlockDevice(IRawBlockDevice parent, string devicePath, long baseOffset, long length)
    {
        _parent = parent;
        DevicePath = devicePath;
        _baseOffset = baseOffset;
        Length = Math.Max(0, length);
    }

    public string DevicePath { get; }
    public long Length { get; }

    public ValueTask<int> ReadAsync(long offset, byte[] buffer, int count, CancellationToken cancellationToken = default)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (offset >= Length || count == 0) return new ValueTask<int>(0);

        var capped = (int)Math.Min(count, Length - offset);
        return _parent.ReadAsync(_baseOffset + offset, buffer, capped, cancellationToken);
    }

    public void Dispose()
    {
        // Parent owns underlying handle lifecycle.
    }
}
