using System.Buffers.Binary;
using System.Collections;

namespace MacMount.RawDiskEngine;

/// <summary>
/// Reads the APFS space manager from disk and maintains a queryable in-memory free-block bitmap.
/// Bit encoding: 1 = free, 0 = used (APFS convention, opposite of HFS+).
///
/// Spaceman structure offsets (from Apple APFS Reference, all relative to block start):
///   0x20: sm_block_size (uint32)
///   0x24: sm_blocks_per_chunk (uint32)
///   0x30: sm_dev[0].sm_block_count (uint64)
///   0x38: sm_dev[0].sm_chunk_count (uint64)
///   0x40: sm_dev[0].sm_cib_count (uint32)
///   0x48: sm_dev[0].sm_free_count (uint64)
///   0x50: sm_dev[0].sm_addr_offset (uint32) — byte offset in this block to CIB address array
///
/// CIB block offsets:
///   0x20: cib_index (uint32) — first chunk index
///   0x24: cib_chunk_info_count (uint32)
///   0x28: array of spaceman_chunk_info_t (24 bytes each):
///     +0x00: ci_xid (uint64)
///     +0x08: ci_addr (uint64) — physical block of bitmap (0 = all free)
///     +0x10: ci_block_count (uint32)
///     +0x14: ci_free_count (uint32)
/// </summary>
internal sealed class ApfsSpacemanReader
{
    private readonly BitArray _bitmap;
    private readonly ulong _totalBlocks;
    private readonly object _sync = new();
    private ulong _freeBlocks;

    private ApfsSpacemanReader(BitArray bitmap, ulong totalBlocks, ulong freeBlocks)
    {
        _bitmap = bitmap;
        _totalBlocks = totalBlocks;
        _freeBlocks = freeBlocks;
    }

    public ulong FreeBlockCount => _freeBlocks;
    public ulong TotalBlockCount => _totalBlocks;

    /// <summary>Loads the spaceman from the given physical block and builds the in-memory bitmap.</summary>
    /// <param name="device">Raw block device to read from.</param>
    /// <param name="spacemanPhysBlock">Physical block number of the spaceman object.</param>
    /// <param name="blockSize">Container block size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ApfsSpacemanReader> LoadAsync(
        IRawBlockDevice device,
        ulong spacemanPhysBlock,
        uint blockSize,
        CancellationToken ct = default)
    {
        if (blockSize < 4096 || blockSize > (1u << 20))
            throw new InvalidOperationException($"ApfsSpacemanReader: invalid block size {blockSize}.");

        // Read spaceman block
        var smBlock = new byte[blockSize];
        var smOffset = checked((long)(spacemanPhysBlock * blockSize));
        var read = await RawReadUtil.ReadExactlyAtAsync(device, smOffset, smBlock, smBlock.Length, ct).ConfigureAwait(false);
        if (read < 0x60)
            throw new InvalidOperationException($"ApfsSpacemanReader: truncated read at block {spacemanPhysBlock} (got {read} bytes, need at least 0x60).");

        // Parse primary spaceman fields
        var blocksPerChunk = BinaryPrimitives.ReadUInt32LittleEndian(smBlock.AsSpan(0x24, 4));
        var blockCount     = BinaryPrimitives.ReadUInt64LittleEndian(smBlock.AsSpan(0x30, 8));
        var cibCount       = BinaryPrimitives.ReadUInt32LittleEndian(smBlock.AsSpan(0x40, 4));
        var addrOffset     = BinaryPrimitives.ReadUInt32LittleEndian(smBlock.AsSpan(0x50, 4));

        if (blocksPerChunk == 0)
            throw new InvalidOperationException("ApfsSpacemanReader: sm_blocks_per_chunk is zero.");
        if (blockCount == 0 || blockCount > (ulong)long.MaxValue / blockSize)
            throw new InvalidOperationException($"ApfsSpacemanReader: implausible block count {blockCount}.");
        if (cibCount > 65536)
            throw new InvalidOperationException($"ApfsSpacemanReader: implausible CIB count {cibCount}.");
        if (addrOffset == 0 || (ulong)addrOffset + (ulong)cibCount * 8 > blockSize)
            throw new InvalidOperationException($"ApfsSpacemanReader: CIB address array at offset {addrOffset} overruns block (size {blockSize}).");

        // Allocate bitmap: one bit per block (APFS: 1=free, 0=used). Default: all used.
        var bitmapSize = (int)Math.Min(blockCount, (ulong)int.MaxValue);
        var bitmap = new BitArray(bitmapSize, false);
        ulong freeCount = 0;

        // Walk CIBs
        for (uint cibIdx = 0; cibIdx < cibCount; cibIdx++)
        {
            // Read CIB physical block address from the CIB address array in the spaceman block
            var cibAddrFieldOffset = (int)(addrOffset + cibIdx * 8);
            if (cibAddrFieldOffset + 8 > smBlock.Length)
                break;
            var cibPhysBlock = BinaryPrimitives.ReadUInt64LittleEndian(smBlock.AsSpan(cibAddrFieldOffset, 8));
            if (cibPhysBlock == 0 || cibPhysBlock >= blockCount)
                continue;

            // Read CIB block
            var cibBlock = new byte[blockSize];
            var cibOffset = checked((long)(cibPhysBlock * blockSize));
            var cibRead = await RawReadUtil.ReadExactlyAtAsync(device, cibOffset, cibBlock, cibBlock.Length, ct).ConfigureAwait(false);
            if (cibRead < 0x28)
                continue;

            var cibFirstChunkIndex = BinaryPrimitives.ReadUInt32LittleEndian(cibBlock.AsSpan(0x20, 4));
            var cibChunkInfoCount  = BinaryPrimitives.ReadUInt32LittleEndian(cibBlock.AsSpan(0x24, 4));

            // Walk chunk info entries
            const int ChunkInfoSize = 24; // sizeof(spaceman_chunk_info_t)
            const int ChunkInfoArrayOffset = 0x28;
            for (uint ci = 0; ci < cibChunkInfoCount; ci++)
            {
                var ciOffset = ChunkInfoArrayOffset + (int)(ci * ChunkInfoSize);
                if (ciOffset + ChunkInfoSize > cibRead)
                    break;

                // ci_xid (+0x00, 8 bytes) skipped
                var ciAddr       = BinaryPrimitives.ReadUInt64LittleEndian(cibBlock.AsSpan(ciOffset + 0x08, 8));
                var ciBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(cibBlock.AsSpan(ciOffset + 0x10, 4));

                var chunkIndex      = cibFirstChunkIndex + ci;
                var chunkStartBlock = (ulong)chunkIndex * blocksPerChunk;

                if (ciAddr == 0)
                {
                    // All blocks in this chunk are free
                    for (ulong b = 0; b < ciBlockCount && chunkStartBlock + b < blockCount; b++)
                    {
                        var absBlock = (int)(chunkStartBlock + b);
                        if (absBlock < bitmap.Length)
                        {
                            bitmap[absBlock] = true;
                            freeCount++;
                        }
                    }
                }
                else
                {
                    // Read the chunk's bitmap block
                    var bitmapBlock = new byte[blockSize];
                    var bitmapBlockOffset = checked((long)(ciAddr * blockSize));
                    var bitmapRead = await RawReadUtil.ReadExactlyAtAsync(device, bitmapBlockOffset, bitmapBlock, bitmapBlock.Length, ct).ConfigureAwait(false);
                    if (bitmapRead < (int)((ciBlockCount + 7) / 8))
                        continue;

                    // Decode bits: bit N in the chunk → block (chunkStartBlock + N), 1 = free
                    for (uint b = 0; b < ciBlockCount; b++)
                    {
                        var absBlock = chunkStartBlock + b;
                        if (absBlock >= blockCount) break;
                        var bitmapBit = (bitmapBlock[b / 8] >> (int)(b % 8)) & 1;
                        if (bitmapBit == 1)
                        {
                            var idx = (int)absBlock;
                            if (idx < bitmap.Length)
                            {
                                bitmap[idx] = true;
                                freeCount++;
                            }
                        }
                    }
                }
            }
        }

        return new ApfsSpacemanReader(bitmap, blockCount, freeCount);
    }

    /// <summary>Returns true if the block is free according to the spaceman bitmap.</summary>
    public bool IsBlockFree(ulong block)
    {
        lock (_sync)
        {
            // Guard ulong→int cast: if block >= bitmap length (including the int.MaxValue cap
            // applied at load time for very large volumes), treat as used.
            if (block >= _totalBlocks || block >= (ulong)_bitmap.Length) return false;
            return _bitmap[(int)block];
        }
    }

    /// <summary>Marks a block as used (in-memory only — Phase 3 will flush to disk).</summary>
    public void MarkBlockUsed(ulong block)
    {
        lock (_sync)
        {
            if (block >= _totalBlocks || block >= (ulong)_bitmap.Length) return;
            var idx = (int)block;
            if (_bitmap[idx])
            {
                _bitmap[idx] = false;
                if (_freeBlocks > 0) _freeBlocks--;
            }
        }
    }

    /// <summary>Marks a block as free (in-memory only — Phase 3 will flush to disk).</summary>
    public void MarkBlockFree(ulong block)
    {
        lock (_sync)
        {
            if (block >= _totalBlocks || block >= (ulong)_bitmap.Length) return;
            var idx = (int)block;
            if (!_bitmap[idx])
            {
                _bitmap[idx] = true;
                _freeBlocks++;
            }
        }
    }
}
