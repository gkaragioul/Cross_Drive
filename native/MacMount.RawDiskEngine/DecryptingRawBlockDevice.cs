using System.Buffers.Binary;

namespace MacMount.RawDiskEngine;

internal sealed class DecryptingRawBlockDevice : IRawBlockDevice
{
    private readonly IRawBlockDevice _inner;
    private readonly AesXts _xts;
    private readonly uint _sectorSize = 512;

    public string DevicePath => _inner.DevicePath;
    public long Length => _inner.Length;

    public DecryptingRawBlockDevice(IRawBlockDevice inner, byte[] vek, uint containerBlockSize)
    {
        _inner = inner;

        var key1 = new byte[16];
        var key2 = new byte[16];
        Array.Copy(vek, 0, key1, 0, 16);
        Array.Copy(vek, 16, key2, 0, 16);
        _xts = new AesXts(key1, key2);
    }

    public async ValueTask<int> ReadAsync(long offset, byte[] buffer, int count, CancellationToken cancellationToken = default)
    {
        var alignedOffset = offset - (offset % _sectorSize);
        var alignedEnd = offset + count;
        if (alignedEnd % _sectorSize != 0)
            alignedEnd += _sectorSize - (alignedEnd % _sectorSize);

        var alignedCount = (int)(alignedEnd - alignedOffset);
        var tempBuf = new byte[alignedCount];

        var read = await _inner.ReadAsync(alignedOffset, tempBuf, alignedCount, cancellationToken).ConfigureAwait(false);
        if (read <= 0) return 0;

        var srcOffset = (int)(offset - alignedOffset);
        var bytesToCopy = Math.Min(count, read - srcOffset);

        // Decrypt each sector. The XTS tweak (unit number) is the sector index
        // from the start of the device, incrementing by 1 for each 512-byte sector.
        var sectorCount = read / (int)_sectorSize;
        var baseSector = (ulong)(alignedOffset / _sectorSize);

        for (var i = 0; i < sectorCount; i++)
        {
            var unitNo = baseSector + (ulong)i;
            var sectorOffset = (int)(i * _sectorSize);
            _xts.Decrypt(tempBuf, sectorOffset, tempBuf, sectorOffset, unitNo);
        }

        Array.Copy(tempBuf, srcOffset, buffer, 0, bytesToCopy);
        return bytesToCopy;
    }

    public void Dispose()
    {
        _xts.Dispose();
        _inner.Dispose();
    }
}
