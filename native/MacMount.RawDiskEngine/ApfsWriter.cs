using System.Buffers.Binary;
using System.Linq;

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
    private ulong? _fsBTreeBlock;

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

    /// <summary>Returns the allocator's current free block count (for testing).</summary>
    public ulong AllocatorFreeBlocks => _allocator.FreeBlocks;

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

    /// <summary>
    /// Directly sets the physical block number of the fs-tree root/leaf.
    /// Call this in tests instead of relying on omap resolution.
    /// </summary>
    public void SetFsBTreeBlock(ulong block) => _fsBTreeBlock = block;

    /// <summary>
    /// Returns the cached fs-tree block, resolving it from the volume superblock if needed.
    /// Returns null if the block cannot be determined (read-only device, missing VSB, etc.).
    /// </summary>
    private async ValueTask<ulong?> GetFsBTreeBlockAsync(CancellationToken ct)
    {
        if (_fsBTreeBlock.HasValue) return _fsBTreeBlock;
        if (_volumeSuperblockBlock is null) return null;

        try
        {
            _fsBTreeBlock = await ResolveRootTreeBlockAsync(_volumeSuperblockBlock.Value, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Resolution failure is non-fatal — write ops will be skipped
        }
        return _fsBTreeBlock;
    }

    /// <summary>
    /// Reads the volume superblock, follows the volume omap B-tree, and returns
    /// the physical block number of the fs-tree root node.
    /// </summary>
    private async Task<ulong?> ResolveRootTreeBlockAsync(ulong vsbBlock, CancellationToken ct)
    {
        // --- 1. Read volume superblock ---
        var vsb  = new byte[_blockSize];
        var read = await _device.ReadAsync(
            _partitionOffset + (long)(vsbBlock * _blockSize), vsb, (int)_blockSize, ct)
            .ConfigureAwait(false);
        if (read < (int)_blockSize) return null;

        // apfs_omap_oid at 0x80 — physical OID (= direct block number, partition-relative)
        var omapOid     = BinaryPrimitives.ReadUInt64LittleEndian(vsb.AsSpan(0x80, 8));
        // apfs_root_tree_oid at 0x88 — virtual OID, resolve through omap
        var rootTreeOid = BinaryPrimitives.ReadUInt64LittleEndian(vsb.AsSpan(0x88, 8));
        if (omapOid == 0 || rootTreeOid == 0) return null;

        // --- 2. Read omap B-tree root block ---
        var omapBuf = new byte[_blockSize];
        read = await _device.ReadAsync(
            _partitionOffset + (long)(omapOid * _blockSize), omapBuf, (int)_blockSize, ct)
            .ConfigureAwait(false);
        if (read < (int)_blockSize) return null;

        var omapNode = ApfsBTreeNode.Deserialize(omapBuf, _blockSize, isRoot: true);
        if (omapNode is null) return null;

        // --- 3. Find omap record: oid == rootTreeOid, highest xid <= _currentXid ---
        ulong bestXid  = 0;
        ulong bestAddr = 0;
        bool  found    = false;

        foreach (var (key, val) in omapNode.Records)
        {
            if (key.Length < 16 || val.Length < 16) continue;
            var kOid = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(0, 8));
            var kXid = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8, 8));
            if (kOid != rootTreeOid) continue;
            if (kXid > _currentXid) continue;
            if (found && kXid < bestXid) continue;
            bestXid  = kXid;
            bestAddr = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(8, 8)); // paddr at val+0x08
            found    = true;
        }

        return found ? bestAddr : null;
    }

    /// <summary>Reads and deserializes the fs-tree leaf block.</summary>
    private async Task<(ApfsBTreeNode node, ulong block)?> ReadFsBTreeAsync(CancellationToken ct)
    {
        var block = await GetFsBTreeBlockAsync(ct).ConfigureAwait(false);
        if (block is null) return null;

        var buf  = new byte[_blockSize];
        var read = await _device.ReadAsync(
            _partitionOffset + (long)(block.Value * _blockSize), buf, (int)_blockSize, ct)
            .ConfigureAwait(false);
        if (read < (int)_blockSize) return null;

        var node = ApfsBTreeNode.Deserialize(buf, _blockSize);
        return node is null ? null : (node, block.Value);
    }

    /// <summary>Serializes and writes a B-tree node back to a specific block.</summary>
    private async Task WriteFsBTreeAsync(ApfsBTreeNode node, ulong block, CancellationToken ct)
    {
        node.TransactionId = ++_currentXid;
        var buf = node.Serialize();
        if (buf is null) throw new IOException("B-tree node exceeds block size — node splitting not yet supported.");
        await _device.WriteAsync(
            _partitionOffset + (long)(block * _blockSize), buf, buf.Length, ct)
            .ConfigureAwait(false);
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
    /// Creates a new file, optionally with initial data.
    /// Writes inode + drec (+ extent if data provided) records to the fs-tree.
    /// </summary>
    public async Task<uint> CreateFileAsync(uint parentCnid, string name, byte[]? initialData = null, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");

        var cnid = AllocateCnid();
        long size = initialData?.Length ?? 0;

        // --- Write initial data blocks ---
        ulong startBlock = 0;
        if (initialData is not null && initialData.Length > 0)
        {
            var blocksNeeded = (uint)((initialData.Length + _blockSize - 1) / _blockSize);
            var allocated    = _allocator.AllocateBlocks(blocksNeeded);
            if (!allocated.HasValue)
                throw new IOException($"CreateFile: no free blocks for initial data ({blocksNeeded} needed).");

            startBlock = allocated.Value;
            var paddedData = new byte[blocksNeeded * _blockSize];
            initialData.CopyTo(paddedData, 0);
            await WriteBlocksAsync(startBlock, paddedData, ct).ConfigureAwait(false);
        }

        // --- Read-modify-write the fs-tree ---
        var fsBtree = await ReadFsBTreeAsync(ct).ConfigureAwait(false);
        if (fsBtree.HasValue)
        {
            var (node, block) = fsBtree.Value;
            node.Insert(BuildInodeKey(cnid),              BuildInodeVal(cnid, parentCnid, false, size));
            node.Insert(BuildDrRecKey(parentCnid, name),  BuildDrRecVal(cnid, false));

            if (initialData is not null && initialData.Length > 0)
                node.Insert(BuildExtentKey(cnid, 0), BuildExtentVal(initialData.Length, startBlock));

            await WriteFsBTreeAsync(node, block, ct).ConfigureAwait(false);
        }

        TrackFileCreated();
        return cnid;
    }

    /// <summary>
    /// Creates a new directory. Writes inode + drec records to the fs-tree.
    /// </summary>
    public async Task<uint> CreateDirectoryAsync(uint parentCnid, string name, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");

        var cnid = AllocateCnid();

        var fsBtree = await ReadFsBTreeAsync(ct).ConfigureAwait(false);
        if (fsBtree.HasValue)
        {
            var (node, block) = fsBtree.Value;
            node.Insert(BuildInodeKey(cnid),             BuildInodeVal(cnid, parentCnid, true, 0));
            node.Insert(BuildDrRecKey(parentCnid, name), BuildDrRecVal(cnid, true));
            await WriteFsBTreeAsync(node, block, ct).ConfigureAwait(false);
        }

        TrackDirCreated();
        return cnid;
    }

    /// <summary>
    /// Writes data to a file at the specified offset.
    /// Allocates blocks, writes raw data, adds an extent record, and updates inode size.
    /// </summary>
    public async Task WriteFileDataAsync(uint fileCnid, long offset, byte[] data, int count, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");
        if (count == 0) return;

        var blocksNeeded = (uint)((count + _blockSize - 1) / _blockSize);
        var startBlock   = _allocator.AllocateBlocks(blocksNeeded);

        if (!startBlock.HasValue)
            throw new IOException("WriteFileData: failed to allocate blocks.");

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

        // Update fs-tree: add extent record + update inode size if write extends EOF
        var fsBtree = await ReadFsBTreeAsync(ct).ConfigureAwait(false);
        if (fsBtree.HasValue)
        {
            var (node, block) = fsBtree.Value;

            node.Insert(BuildExtentKey(fileCnid, offset), BuildExtentVal(count, startBlock.Value));

            var inodeKey = BuildInodeKey(fileCnid);
            var existing = node.Records.FirstOrDefault(r => r.Key.SequenceEqual(inodeKey));
            if (existing != default)
            {
                var currentSize = ReadInodeSize(existing.Value);
                var newEnd      = offset + count;
                if (newEnd > currentSize)
                {
                    node.Delete(inodeKey);
                    node.Insert(inodeKey, UpdateInodeSize(existing.Value, newEnd));
                }
            }

            await WriteFsBTreeAsync(node, block, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes a catalog entry (file or directory).
    /// Removes inode + drec + all extent records from the fs-tree, and frees allocated blocks.
    /// </summary>
    public async Task DeleteEntryAsync(uint parentCnid, string name, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");

        var fsBtree = await ReadFsBTreeAsync(ct).ConfigureAwait(false);
        if (fsBtree.HasValue)
        {
            var (node, block) = fsBtree.Value;

            // Step 1: Find the drec to get the target CNID
            var drecKey = BuildDrRecKey(parentCnid, name);
            var drecRec = node.Records.FirstOrDefault(r => r.Key.SequenceEqual(drecKey));
            uint targetCnid = 0;
            if (drecRec != default && drecRec.Value.Length >= 8)
                targetCnid = (uint)BinaryPrimitives.ReadUInt64LittleEndian(drecRec.Value.AsSpan(0, 8));

            // Step 2: Collect and free all extent blocks for this file
            if (targetCnid != 0)
            {
                var extentPrefix = (8UL << 60) | targetCnid;
                var extentsToRemove = node.Records
                    .Where(r => r.Key.Length >= 8 &&
                                BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) == extentPrefix)
                    .ToList();

                foreach (var (extKey, extVal) in extentsToRemove)
                {
                    var (physBlock, byteLen) = ReadExtentVal(extVal);
                    if (physBlock > 0 && byteLen > 0)
                    {
                        var blockCount = (uint)((byteLen + _blockSize - 1) / _blockSize);
                        _allocator.FreeBlockRange(physBlock, blockCount);
                    }
                    node.Delete(extKey);
                }

                // Step 3: Remove inode record
                node.Delete(BuildInodeKey(targetCnid));
            }

            // Step 4: Remove drec record
            node.Delete(drecKey);

            await WriteFsBTreeAsync(node, block, ct).ConfigureAwait(false);
        }

        TrackFileDeleted();
    }

    /// <summary>
    /// Sets the size of a file, truncating or extending as needed.
    /// On shrink: updates inode size and frees extents that lie fully at or beyond newSize.
    /// </summary>
    public async Task SetFileSizeAsync(uint fileCnid, long newSize, CancellationToken ct = default)
    {
        if (!IsWritable) throw new InvalidOperationException("Device is read-only");
        if (newSize < 0) throw new ArgumentOutOfRangeException(nameof(newSize));

        var fsBtree = await ReadFsBTreeAsync(ct).ConfigureAwait(false);
        if (!fsBtree.HasValue) return;

        var (node, block) = fsBtree.Value;

        // Step 1: Update inode size
        var inodeKey = BuildInodeKey(fileCnid);
        var inodeRec = node.Records.FirstOrDefault(r => r.Key.SequenceEqual(inodeKey));
        if (inodeRec != default)
        {
            node.Delete(inodeKey);
            node.Insert(inodeKey, UpdateInodeSize(inodeRec.Value, newSize));
        }

        // Step 2: On shrink — free and remove extents fully beyond newSize
        var extentPrefix = (8UL << 60) | fileCnid;
        var extentsToTrim = node.Records
            .Where(r =>
            {
                if (r.Key.Length < 16) return false;
                if (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) != extentPrefix) return false;
                var logicalStart = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8, 8));
                return logicalStart >= newSize; // fully past new EOF
            })
            .ToList();

        foreach (var (extKey, extVal) in extentsToTrim)
        {
            var (physBlock, byteLen) = ReadExtentVal(extVal);
            if (physBlock > 0 && byteLen > 0)
            {
                var blockCount = (uint)((byteLen + _blockSize - 1) / _blockSize);
                _allocator.FreeBlockRange(physBlock, blockCount);
            }
            node.Delete(extKey);
        }

        await WriteFsBTreeAsync(node, block, ct).ConfigureAwait(false);
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

            // apfs_num_files at 0xA8 (u64 LE) — APFS Reference §Table 13-4
            if (_pendingFileCountDelta != 0)
            {
                var cur = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0xA8, 8));
                var next = (ulong)Math.Max(0L, cur + _pendingFileCountDelta);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0xA8, 8), next);
                _pendingFileCountDelta = 0;
            }

            // apfs_num_directories at 0xB0 (u64 LE)
            if (_pendingDirCountDelta != 0)
            {
                var cur = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0xB0, 8));
                var next = (ulong)Math.Max(0L, cur + _pendingDirCountDelta);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0xB0, 8), next);
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

    // ── Key builders ──────────────────────────────────────────────────────────

    private static byte[] BuildInodeKey(uint cnid)
    {
        var k = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(k, (3UL << 60) | cnid);
        return k;
    }

    private static byte[] BuildDrRecKey(uint parentCnid, string name)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var nameLen   = (ushort)(nameBytes.Length + 1); // +1 for NUL terminator
        var k = new byte[10 + nameLen];
        BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(0, 8), (9UL << 60) | parentCnid);
        BinaryPrimitives.WriteUInt16LittleEndian(k.AsSpan(8, 2), nameLen);
        nameBytes.CopyTo(k.AsSpan(10));
        // k[10 + nameBytes.Length] = 0x00 (NUL) — already zero from array initializer
        return k;
    }

    private static byte[] BuildExtentKey(uint fileCnid, long logicalOffset)
    {
        var k = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(0, 8), (8UL << 60) | fileCnid);
        BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(8, 8), (ulong)logicalOffset);
        return k;
    }

    // ── Value builders ────────────────────────────────────────────────────────

    private static byte[] BuildInodeVal(uint cnid, uint parentCnid, bool isDir, long size)
    {
        var v   = new byte[92]; // j_inode_val_t without xfields
        var now = GetApfsTimeNs();
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x00, 8), parentCnid);            // parent_id
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x08, 8), cnid);                   // private_id
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x10, 8), now);                    // create_time
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x18, 8), now);                    // mod_time
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x20, 8), now);                    // change_time
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x28, 8), now);                    // access_time
        // internal_flags = 0 at 0x30
        BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(0x38, 4), isDir ? 0 : 1);           // nchildren/nlink
        // protection_class, write_gen_counter, bsd_flags = 0 at 0x3C, 0x40, 0x44
        // owner, group = 0 at 0x48, 0x4C
        ushort mode = isDir ? (ushort)0x41ED : (ushort)0x81A4;
        BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(0x50, 2), mode);                   // mode
        // pad1 = 0 at 0x52
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x54, 8), (ulong)Math.Max(0, size)); // uncompressed_size
        return v;
    }

    private static byte[] BuildDrRecVal(uint cnid, bool isDir)
    {
        var v   = new byte[18]; // j_drec_val_t without xfields
        var now = GetApfsTimeNs();
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x00, 8), cnid);                   // file_id
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x08, 8), now);                    // date_added
        ushort dtFlags = isDir ? (ushort)0x0004 : (ushort)0x0008;                            // DT_DIR=4, DT_REG=8
        BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(0x10, 2), dtFlags);
        return v;
    }

    private static byte[] BuildExtentVal(long byteLength, ulong physBlock)
    {
        var v = new byte[24]; // j_file_extent_val_t
        // len_and_flags: length in bits 55:0, flags in bits 63:56 (0 = no flags)
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x00, 8), (ulong)byteLength & 0x00FFFFFFFFFFFFFFUL);
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x08, 8), physBlock);              // phys_block_num
        // crypto_id = 0 at 0x10 — already zero
        return v;
    }

    // ── Value readers ─────────────────────────────────────────────────────────

    /// <summary>Reads the file size from an inode value (uncompressed_size at +0x54).</summary>
    private static long ReadInodeSize(byte[] val) =>
        val.Length >= 92
            ? (long)BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(0x54, 8))
            : 0;

    /// <summary>Updates the size field in an inode value buffer in-place and returns it.</summary>
    private static byte[] UpdateInodeSize(byte[] val, long newSize)
    {
        if (val.Length >= 92)
            BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(0x54, 8), (ulong)Math.Max(0, newSize));
        return val;
    }

    /// <summary>Returns the physical block and byte length encoded in an extent value.</summary>
    private static (ulong PhysBlock, long ByteLength) ReadExtentVal(byte[] val)
    {
        if (val.Length < 24) return (0, 0);
        var lenAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(0x00, 8));
        var phys        = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(0x08, 8));
        return (phys, (long)(lenAndFlags & 0x00FFFFFFFFFFFFFFUL));
    }

    public void Dispose()
    {
        // Cleanup
    }
}

internal sealed record ApfsExtent(ulong StartBlock, uint BlockCount, ulong LogicalOffset);
