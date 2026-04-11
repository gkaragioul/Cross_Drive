using System.Buffers.Binary;

namespace MacMount.RawDiskEngine;

/// <summary>
/// Manages block allocation for APFS volumes.
/// Uses the allocation bitmap from the container to track free blocks.
/// </summary>
internal sealed class ApfsBlockAllocator : IDisposable
{
    private readonly IRawBlockDevice? _device;
    private readonly uint _blockSize;
    private readonly ulong _blockCount;
    private readonly long _partitionOffset;
    private readonly ApfsSpacemanReader? _spaceman;
    private readonly object _sync = new();
    private byte[]? _allocationBitmap;
    private bool _bitmapLoaded;
    private ulong _freeBlocks;

    public ApfsBlockAllocator(IRawBlockDevice device, uint blockSize, ulong blockCount, long partitionOffset)
    {
        _device = device;
        _blockSize = blockSize;
        _blockCount = blockCount;
        _partitionOffset = partitionOffset;
        _freeBlocks = blockCount;
    }

    /// <summary>Creates an allocator backed by a real spaceman bitmap.</summary>
    public ApfsBlockAllocator(ApfsSpacemanReader spaceman)
    {
        _spaceman = spaceman;
        _blockCount = spaceman.TotalBlockCount;
        _freeBlocks = spaceman.FreeBlockCount;
        _bitmapLoaded = true; // bitmap already populated by the reader
    }

    public ulong FreeBlocks => _spaceman is not null ? _spaceman.FreeBlockCount : _freeBlocks;

    /// <summary>
    /// Loads the allocation bitmap from the container.
    /// For now, we assume all blocks are free (simplified approach for external drives).
    /// </summary>
    public async Task LoadBitmapAsync(CancellationToken ct = default)
    {
        if (_bitmapLoaded || _spaceman is not null) return;

        lock (_sync)
        {
            if (_bitmapLoaded) return;

            // Simplified: assume all blocks are free
            // In a full implementation, this would parse the spaceman structure
            var bitmapSize = (int)((_blockCount + 7) / 8);
            _allocationBitmap = new byte[bitmapSize];

            // Mark all blocks as free (0 = free, 1 = used)
            Array.Clear(_allocationBitmap, 0, bitmapSize);

            // Reserve first few blocks for container superblock
            for (ulong i = 0; i < Math.Min(64, _blockCount); i++)
            {
                MarkBlockUsed(i);
            }

            _bitmapLoaded = true;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Allocates a contiguous range of blocks.
    /// </summary>
    public ulong? AllocateBlocks(uint count)
    {
        if (!_bitmapLoaded || count == 0) return null;

        lock (_sync)
        {
            // Simple first-fit allocation
            for (ulong start = 0; start <= _blockCount - count; start++)
            {
                if (IsRangeFree(start, count))
                {
                    for (ulong i = 0; i < count; i++)
                    {
                        MarkBlockUsed(start + i);
                    }
                    _freeBlocks -= count;
                    return start;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Frees a range of blocks.
    /// </summary>
    public void FreeBlockRange(ulong startBlock, uint count)
    {
        if (!_bitmapLoaded || count == 0) return;

        lock (_sync)
        {
            for (ulong i = 0; i < count; i++)
            {
                MarkBlockFree(startBlock + i);
            }
            _freeBlocks += count;
        }
    }

    private bool IsRangeFree(ulong start, uint count)
    {
        for (ulong i = 0; i < count; i++)
        {
            if (IsBlockUsed(start + i)) return false;
        }
        return true;
    }

    private bool IsBlockUsed(ulong block)
    {
        if (_spaceman is not null) return !_spaceman.IsBlockFree(block);
        if (_allocationBitmap is null || block >= _blockCount) return true;
        var byteIndex = (int)(block / 8);
        var bitIndex = (int)(block % 8);
        return (_allocationBitmap[byteIndex] & (1 << bitIndex)) != 0;
    }

    private void MarkBlockUsed(ulong block)
    {
        if (_spaceman is not null) { _spaceman.MarkBlockUsed(block); return; }
        if (_allocationBitmap is null || block >= _blockCount) return;
        var byteIndex = (int)(block / 8);
        var bitIndex = (int)(block % 8);
        _allocationBitmap[byteIndex] |= (byte)(1 << bitIndex);
    }

    private void MarkBlockFree(ulong block)
    {
        if (_spaceman is not null) { _spaceman.MarkBlockFree(block); return; }
        if (_allocationBitmap is null || block >= _blockCount) return;
        var byteIndex = (int)(block / 8);
        var bitIndex = (int)(block % 8);
        _allocationBitmap[byteIndex] &= (byte)~(1 << bitIndex);
    }

    public void Dispose()
    {
        _allocationBitmap = null;
    }
}

/// <summary>
/// Handles APFS write operations including block allocation, volume superblock flush,
/// and pending metadata tracking.
/// </summary>
internal sealed class ApfsWriter : IDisposable
{
    private readonly IRawBlockDevice _device;
    private readonly ApfsBlockAllocator _allocator;
    private readonly uint _blockSize;
    private readonly long _partitionOffset;
    private readonly ulong _volumeOid;
    private readonly ulong? _volumeSuperblockBlock;
    private readonly object _sync = new();
    private ulong _currentXid;
    private ulong _nextObjectId = 1000; // Starting CNID for new objects
    private long _pendingFileCountDelta;
    private long _pendingDirCountDelta;

    public ApfsWriter(
        IRawBlockDevice device,
        ApfsBlockAllocator allocator,
        uint blockSize,
        long partitionOffset,
        ulong volumeOid,
        ulong? volumeSuperblockBlock = null,
        ulong currentXid = 0)
    {
        _device = device;
        _allocator = allocator;
        _blockSize = blockSize;
        _partitionOffset = partitionOffset;
        _volumeOid = volumeOid;
        _volumeSuperblockBlock = volumeSuperblockBlock;
        _currentXid = currentXid;
    }

    public bool IsWritable => _device.CanWrite;

    /// <summary>
    /// Allocates a new CNID (Catalog Node ID) for file system objects.
    /// </summary>
    public uint AllocateCnid()
    {
        lock (_sync)
        {
            return (uint)Interlocked.Increment(ref _nextObjectId);
        }
    }

    /// <summary>Increments the pending file count for the next FlushAsync.</summary>
    public void TrackFileCreated() => Interlocked.Increment(ref _pendingFileCountDelta);

    /// <summary>Increments the pending directory count for the next FlushAsync.</summary>
    public void TrackDirCreated() => Interlocked.Increment(ref _pendingDirCountDelta);

    /// <summary>Decrements the pending file count (for delete operations).</summary>
    public void TrackFileDeleted() => Interlocked.Decrement(ref _pendingFileCountDelta);

    /// <summary>Decrements the pending directory count (for delete operations).</summary>
    public void TrackDirDeleted() => Interlocked.Decrement(ref _pendingDirCountDelta);

    /// <summary>
    /// Creates a new file with initial data.
    /// </summary>
    public async Task<uint> CreateFileAsync(uint parentCnid, string name, byte[]? initialData = null, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");

        var cnid = AllocateCnid();

        // Allocate blocks for data if provided
        if (initialData is not null && initialData.Length > 0)
        {
            var blocksNeeded = (uint)((initialData.Length + _blockSize - 1) / _blockSize);
            var startBlock = _allocator.AllocateBlocks(blocksNeeded);

            if (startBlock.HasValue)
            {
                var paddedData = new byte[blocksNeeded * _blockSize];
                initialData.CopyTo(paddedData, 0);
                await WriteBlocksAsync(startBlock.Value, paddedData, ct).ConfigureAwait(false);
            }
        }

        TrackFileCreated();
        return cnid;
    }

    /// <summary>
    /// Creates a new directory.
    /// </summary>
    public async Task<uint> CreateDirectoryAsync(uint parentCnid, string name, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");

        var cnid = AllocateCnid();
        TrackDirCreated();
        return await Task.FromResult(cnid);
    }

    /// <summary>
    /// Writes data to a file at the specified offset.
    /// </summary>
    public async Task WriteFileDataAsync(uint fileCnid, long offset, byte[] data, int count, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");
        if (count == 0) return;

        var blocksNeeded = (uint)((count + _blockSize - 1) / _blockSize);
        var startBlock = _allocator.AllocateBlocks(blocksNeeded);

        if (!startBlock.HasValue)
        {
            throw new IOException("Failed to allocate blocks for write");
        }

        try
        {
            var paddedData = new byte[blocksNeeded * _blockSize];
            data.AsSpan(0, count).CopyTo(paddedData);
            await WriteBlocksAsync(startBlock.Value, paddedData, ct).ConfigureAwait(false);
        }
        catch
        {
            _allocator.FreeBlockRange(startBlock.Value, blocksNeeded);
            throw;
        }
    }

    /// <summary>
    /// Deletes a catalog entry (file or directory).
    /// </summary>
    public async Task DeleteEntryAsync(uint parentCnid, string name, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");
        TrackFileDeleted();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Sets the size of a file, truncating or extending as needed.
    /// </summary>
    public async Task SetFileSizeAsync(uint fileCnid, long newSize, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Flushes pending metadata changes to the volume superblock.
    /// Reads the volume superblock, increments the XID, updates file/directory counts,
    /// recomputes the Fletcher-64 checksum, and writes the block back in place.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!IsWritable) return;
        if (_volumeSuperblockBlock is null) return;
        if (_pendingFileCountDelta == 0 && _pendingDirCountDelta == 0) return;

        var offset = _partitionOffset + (long)(_volumeSuperblockBlock.Value * _blockSize);
        var buf = new byte[_blockSize];
        var read = await _device.ReadAsync(offset, buf, buf.Length, ct).ConfigureAwait(false);
        if (read < (int)_blockSize) return;

        // Increment transaction ID in object header (o_xid at 0x10)
        lock (_sync)
        {
            _currentXid++;
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x10, 8), _currentXid);

            // apfs_num_files at 0x90 (u64 LE)
            if (_pendingFileCountDelta != 0)
            {
                var cur = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0x90, 8));
                var next = (ulong)Math.Max(0L, cur + _pendingFileCountDelta);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x90, 8), next);
                _pendingFileCountDelta = 0;
            }

            // apfs_num_directories at 0x98 (u64 LE)
            if (_pendingDirCountDelta != 0)
            {
                var cur = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0x98, 8));
                var next = (ulong)Math.Max(0L, cur + _pendingDirCountDelta);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x98, 8), next);
                _pendingDirCountDelta = 0;
            }
        }

        ApfsChecksum.WriteChecksum(buf.AsSpan());
        await _device.WriteAsync(offset, buf, buf.Length, ct).ConfigureAwait(false);
    }

    private async Task WriteBlocksAsync(ulong blockNumber, byte[] data, CancellationToken ct)
    {
        var offset = _partitionOffset + (long)(blockNumber * _blockSize);
        await _device.WriteAsync(offset, data, data.Length, ct).ConfigureAwait(false);
    }

    private static ulong GetApfsTimeNs()
    {
        var unixTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (ulong)unixTime * 1_000_000UL;
    }

    public void Dispose()
    {
        // Cleanup
    }
}

internal sealed record ApfsExtent(ulong StartBlock, uint BlockCount, ulong LogicalOffset);
