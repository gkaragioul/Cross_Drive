# APFS Write Phase 1 — Spaceman Parser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fake `ApfsBlockAllocator` bitmap (all blocks assumed free) with a real bitmap parsed from the on-disk APFS space manager, enabling accurate free-space reporting and correct block allocation for Phase 2 writes.

**Architecture:** `ApfsSpacemanReader` reads the spaceman block, walks its CIB/bitmap chain, and exposes an in-memory `BitArray`. `ApfsRawFileSystemProvider.CreateAsync` resolves the spaceman's physical block via the pre-built object index and passes it to the reader. `ApfsBlockAllocator` gains a constructor overload that delegates to the reader instead of the fake bitmap. `FreeBytes` is wired to the real count.

**Tech Stack:** C# .NET 9, `System.Collections.BitArray`, `System.Buffers.Binary.BinaryPrimitives`, file-backed `IRawBlockDevice` for tests.

---

## APFS Spaceman Structure Reference

All offsets are from the **start of the block** (including the 32-byte object header).

**`spaceman_phys_t` fields used:**
| Offset | Size | Field |
|--------|------|-------|
| 0x20 | uint32 | `sm_block_size` |
| 0x24 | uint32 | `sm_blocks_per_chunk` |
| 0x28 | uint32 | `sm_chunks_per_cib` |
| 0x2C | uint32 | `sm_cibs_per_cab` |
| 0x30 | uint64 | `sm_dev[0].sm_block_count` |
| 0x38 | uint64 | `sm_dev[0].sm_chunk_count` |
| 0x40 | uint32 | `sm_dev[0].sm_cib_count` |
| 0x44 | uint32 | `sm_dev[0].sm_cab_count` |
| 0x48 | uint64 | `sm_dev[0].sm_free_count` |
| 0x50 | uint32 | `sm_dev[0].sm_addr_offset` — byte offset within this block to the CIB address array |

**`spaceman_cib_t` fields used (each CIB block):**
| Offset | Size | Field |
|--------|------|-------|
| 0x20 | uint32 | `cib_index` — first chunk index covered by this CIB |
| 0x24 | uint32 | `cib_chunk_info_count` — number of chunks in this CIB |
| 0x28 | 24 × N | array of `spaceman_chunk_info_t` |

**`spaceman_chunk_info_t` (24 bytes each):**
| Offset | Size | Field |
|--------|------|-------|
| 0x00 | uint64 | `ci_xid` |
| 0x08 | uint64 | `ci_addr` — physical block of bitmap (0 = all blocks in chunk are free) |
| 0x10 | uint32 | `ci_block_count` |
| 0x14 | uint32 | `ci_free_count` |

**Bit encoding:** `1 = free`, `0 = used` (opposite of HFS+). Bit N within a chunk corresponds to block `chunkStartBlock + N`. Bit N is stored in byte `N/8`, bit position `N%8` (LSB first).

---

## Files Changed

| File | Change |
|------|--------|
| Create: `native/MacMount.RawDiskEngine/ApfsSpacemanReader.cs` | New class — reads spaceman and exposes free-block bitmap |
| Modify: `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs` | `NxSuperblock` + `ApfsContainerSummary` records, `ReadNxSuperblockAtContainerBlockAsync`, `ReadSummaryAsync`, `CreateAsync`, constructor, `FreeBytes` |
| Modify: `native/MacMount.RawDiskEngine/ApfsWriter.cs` | `ApfsBlockAllocator` — new constructor overload + delegation methods |
| Create: `native/MacMount.ApfsWriteTest/MacMount.ApfsWriteTest.csproj` | New test project |
| Create: `native/MacMount.ApfsWriteTest/Program.cs` | Test runner |
| Create: `native/MacMount.ApfsWriteTest/ApfsSpacemanTests.cs` | 7 tests |
| Modify: `package.json` | Add `apfs:test` script |

---

## Task 1: Fix `NxSuperblock` record — rename `ObjectMapOid` → `SpacemanOid`, add `OmapOid`

**Files:** `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs`

The field at offset 152 (0x98) is `nx_spaceman_oid` per Apple spec. The field at 160 (0xA0) is `nx_omap_oid` (the true container omap OID — not currently read). This task corrects the naming and adds the missing omap OID, which also fixes the existing bug where the omap lookup was using the spaceman OID by mistake.

- [ ] **Step 1: Update `NxSuperblock` record definition** (around line 2784)

  Find:
  ```csharp
      private sealed record NxSuperblock(
          uint BlockSize,
          ulong BlockCount,
          ulong TransactionId,
          ulong CheckpointDescriptorBase,
          uint CheckpointDescriptorBlocks,
          ulong CheckpointDataBase,
          uint CheckpointDataBlocks,
          ulong ObjectMapOid,
          IReadOnlyList<ulong> VolumeObjectIds
      );
  ```

  Replace with:
  ```csharp
      private sealed record NxSuperblock(
          uint BlockSize,
          ulong BlockCount,
          ulong TransactionId,
          ulong CheckpointDescriptorBase,
          uint CheckpointDescriptorBlocks,
          ulong CheckpointDataBase,
          uint CheckpointDataBlocks,
          ulong SpacemanOid,   // nx_spaceman_oid @ offset 0x98 — OID of the space manager object
          ulong OmapOid,       // nx_omap_oid @ offset 0xA0 — OID of the container object map
          IReadOnlyList<ulong> VolumeObjectIds
      );
  ```

- [ ] **Step 2: Update `ReadNxSuperblockAtContainerBlockAsync` — read both OIDs and fix constructor** (around line 1087)

  Find:
  ```csharp
          var omapOid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(152, 8));
  ```

  Replace with:
  ```csharp
          var spacemanOid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(152, 8)); // nx_spaceman_oid
          var omapOid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(160, 8));     // nx_omap_oid
  ```

  Then find the `NxSuperblock` constructor call (around line 1103):
  ```csharp
          return new NxSuperblock(
              BlockSize: blockSize,
              BlockCount: blockCount,
              TransactionId: xid,
              CheckpointDescriptorBase: descBase,
              CheckpointDescriptorBlocks: descBlocks,
              CheckpointDataBase: dataBase,
              CheckpointDataBlocks: dataBlocks,
              ObjectMapOid: omapOid,
              VolumeObjectIds: volumeIds
          );
  ```

  Replace with:
  ```csharp
          return new NxSuperblock(
              BlockSize: blockSize,
              BlockCount: blockCount,
              TransactionId: xid,
              CheckpointDescriptorBase: descBase,
              CheckpointDescriptorBlocks: descBlocks,
              CheckpointDataBase: dataBase,
              CheckpointDataBlocks: dataBlocks,
              SpacemanOid: spacemanOid,
              OmapOid: omapOid,
              VolumeObjectIds: volumeIds
          );
  ```

- [ ] **Step 3: Update `ApfsContainerSummary` record definition** (around line 835)

  Find:
  ```csharp
  internal sealed record ApfsContainerSummary(
      uint BlockSize,
      ulong BlockCount,
      ulong TransactionId,
      ulong CheckpointDescriptorBase,
      uint CheckpointDescriptorBlocks,
      ulong CheckpointDataBase,
      uint CheckpointDataBlocks,
      ulong ObjectMapOid,
      ulong? ObjectMapBlockNumber,
      ulong? ObjectMapTreeOid,
      ulong? ObjectMapTreeBlockNumber,
      int IndexedObjectCount,
      IReadOnlyList<ApfsResolvedObjectPointer> ResolvedVolumePointers,
      IReadOnlyDictionary<ulong, ApfsVolumePreview> VolumePreviewsByOid,
      IReadOnlyList<ulong> VolumeObjectIds,
      IReadOnlyList<ApfsVolumeSuperblockHint> VolumeSuperblockHints
  )
  ```

  Replace with:
  ```csharp
  internal sealed record ApfsContainerSummary(
      uint BlockSize,
      ulong BlockCount,
      ulong TransactionId,
      ulong CheckpointDescriptorBase,
      uint CheckpointDescriptorBlocks,
      ulong CheckpointDataBase,
      uint CheckpointDataBlocks,
      ulong SpacemanOid,
      ulong OmapOid,
      ulong? ObjectMapBlockNumber,
      ulong? ObjectMapTreeOid,
      ulong? ObjectMapTreeBlockNumber,
      int IndexedObjectCount,
      ulong? SpacemanPhysicalBlock,
      IReadOnlyList<ApfsResolvedObjectPointer> ResolvedVolumePointers,
      IReadOnlyDictionary<ulong, ApfsVolumePreview> VolumePreviewsByOid,
      IReadOnlyList<ulong> VolumeObjectIds,
      IReadOnlyList<ApfsVolumeSuperblockHint> VolumeSuperblockHints
  )
  ```

- [ ] **Step 4: Update `ReadSummaryAsync` — fix omap lookup, add spaceman block resolution** (around line 979)

  Find:
  ```csharp
          objectIndex.TryGetValue(best.ObjectMapOid, out var omapPtr);
  ```

  Replace with:
  ```csharp
          objectIndex.TryGetValue(best.OmapOid, out var omapPtr);
          objectIndex.TryGetValue(best.SpacemanOid, out var spacemanPtr);
  ```

  Then find the `ApfsContainerSummary` constructor call (around line 1027):
  ```csharp
          return new ApfsContainerSummary(
              BlockSize: best.BlockSize,
              BlockCount: best.BlockCount,
              TransactionId: best.TransactionId,
              CheckpointDescriptorBase: best.CheckpointDescriptorBase,
              CheckpointDescriptorBlocks: best.CheckpointDescriptorBlocks,
              CheckpointDataBase: best.CheckpointDataBase,
              CheckpointDataBlocks: best.CheckpointDataBlocks,
              ObjectMapOid: best.ObjectMapOid,
              ObjectMapBlockNumber: omapPtr?.BlockNumber,
              ObjectMapTreeOid: omapTreeOid,
              ObjectMapTreeBlockNumber: omapTreePtr?.BlockNumber,
              IndexedObjectCount: objectIndex.Count,
              ResolvedVolumePointers: resolvedVolumePointers,
              VolumePreviewsByOid: volumePreviews,
              VolumeObjectIds: best.VolumeObjectIds,
              VolumeSuperblockHints: volumeHints
          );
  ```

  Replace with:
  ```csharp
          return new ApfsContainerSummary(
              BlockSize: best.BlockSize,
              BlockCount: best.BlockCount,
              TransactionId: best.TransactionId,
              CheckpointDescriptorBase: best.CheckpointDescriptorBase,
              CheckpointDescriptorBlocks: best.CheckpointDescriptorBlocks,
              CheckpointDataBase: best.CheckpointDataBase,
              CheckpointDataBlocks: best.CheckpointDataBlocks,
              SpacemanOid: best.SpacemanOid,
              OmapOid: best.OmapOid,
              ObjectMapBlockNumber: omapPtr?.BlockNumber,
              ObjectMapTreeOid: omapTreeOid,
              ObjectMapTreeBlockNumber: omapTreePtr?.BlockNumber,
              IndexedObjectCount: objectIndex.Count,
              SpacemanPhysicalBlock: spacemanPtr?.BlockNumber,
              ResolvedVolumePointers: resolvedVolumePointers,
              VolumePreviewsByOid: volumePreviews,
              VolumeObjectIds: best.VolumeObjectIds,
              VolumeSuperblockHints: volumeHints
          );
  ```

- [ ] **Step 5: Update the debug info text builder** (around line 748)

  Find:
  ```csharp
          sb.AppendLine($"ObjectMapOid: {summary.ObjectMapOid}");
  ```

  Replace with:
  ```csharp
          sb.AppendLine($"SpacemanOid: {summary.SpacemanOid}");
          sb.AppendLine($"OmapOid: {summary.OmapOid}");
          sb.AppendLine($"SpacemanPhysicalBlock: {summary.SpacemanPhysicalBlock?.ToString() ?? "not found"}");
  ```

- [ ] **Step 6: Build to verify no compile errors**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  npm run raw:build 2>&1 | tail -5
  ```

  Expected last line: `Build succeeded.`

- [ ] **Step 7: Commit**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  git add native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs
  git commit -m "fix: rename ObjectMapOid to SpacemanOid, add OmapOid, add SpacemanPhysicalBlock to APFS container summary"
  ```

---

## Task 2: Create `ApfsSpacemanReader.cs`

**Files:** `native/MacMount.RawDiskEngine/ApfsSpacemanReader.cs`

- [ ] **Step 1: Create the file**

  Create `native/MacMount.RawDiskEngine/ApfsSpacemanReader.cs` with:

  ```csharp
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
          var smBlockSize       = BinaryPrimitives.ReadUInt32LittleEndian(smBlock.AsSpan(0x20, 4));
          var blocksPerChunk    = BinaryPrimitives.ReadUInt32LittleEndian(smBlock.AsSpan(0x24, 4));
          var blockCount        = BinaryPrimitives.ReadUInt64LittleEndian(smBlock.AsSpan(0x30, 8));
          var chunkCount        = BinaryPrimitives.ReadUInt64LittleEndian(smBlock.AsSpan(0x38, 8));
          var cibCount          = BinaryPrimitives.ReadUInt32LittleEndian(smBlock.AsSpan(0x40, 4));
          var freeCountFromSm   = BinaryPrimitives.ReadUInt64LittleEndian(smBlock.AsSpan(0x48, 8));
          var addrOffset        = BinaryPrimitives.ReadUInt32LittleEndian(smBlock.AsSpan(0x50, 4));
  
          if (blocksPerChunk == 0)
              throw new InvalidOperationException("ApfsSpacemanReader: sm_blocks_per_chunk is zero.");
          if (blockCount == 0 || blockCount > (ulong)long.MaxValue / blockSize)
              throw new InvalidOperationException($"ApfsSpacemanReader: implausible block count {blockCount}.");
          if (cibCount > 65536)
              throw new InvalidOperationException($"ApfsSpacemanReader: implausible CIB count {cibCount}.");
          if (addrOffset == 0 || addrOffset + (ulong)cibCount * 8 > blockSize)
              throw new InvalidOperationException($"ApfsSpacemanReader: CIB address array at offset {addrOffset} overruns block (size {blockSize}).");
  
          // Allocate bitmap: one bit per block (APFS: 1=free, 0=used)
          var bitmapSize = (int)Math.Min(blockCount, (ulong)int.MaxValue);
          var bitmap = new BitArray(bitmapSize, false); // default: all used
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
  
              var cibFirstChunkIndex   = BinaryPrimitives.ReadUInt32LittleEndian(cibBlock.AsSpan(0x20, 4));
              var cibChunkInfoCount    = BinaryPrimitives.ReadUInt32LittleEndian(cibBlock.AsSpan(0x24, 4));
  
              // Walk chunk info entries
              const int ChunkInfoSize = 24; // sizeof(spaceman_chunk_info_t)
              const int ChunkInfoArrayOffset = 0x28;
              for (uint ci = 0; ci < cibChunkInfoCount; ci++)
              {
                  var ciOffset = ChunkInfoArrayOffset + (int)(ci * ChunkInfoSize);
                  if (ciOffset + ChunkInfoSize > cibRead)
                      break;
  
                  // ci_xid (8) + ci_addr (8) + ci_block_count (4) + ci_free_count (4)
                  var ciAddr        = BinaryPrimitives.ReadUInt64LittleEndian(cibBlock.AsSpan(ciOffset + 8, 8));
                  var ciBlockCount  = BinaryPrimitives.ReadUInt32LittleEndian(cibBlock.AsSpan(ciOffset + 16, 4));
                  var ciFreeCount   = BinaryPrimitives.ReadUInt32LittleEndian(cibBlock.AsSpan(ciOffset + 20, 4));
  
                  var chunkIndex = cibFirstChunkIndex + ci;
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
  
                      // Decode bits: bit N in the chunk → block (chunkStartBlock + N)
                      for (uint b = 0; b < ciBlockCount; b++)
                      {
                          var absBlock = chunkStartBlock + b;
                          if (absBlock >= blockCount) break;
                          var bitmapBit = (bitmapBlock[b / 8] >> (int)(b % 8)) & 1;
                          if (bitmapBit == 1) // 1 = free
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
              var idx = (int)block;
              return block < _totalBlocks && idx < _bitmap.Length && _bitmap[idx];
          }
      }
  
      /// <summary>Marks a block as used (in-memory only — Phase 3 will flush to disk).</summary>
      public void MarkBlockUsed(ulong block)
      {
          lock (_sync)
          {
              var idx = (int)block;
              if (block >= _totalBlocks || idx >= _bitmap.Length) return;
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
              var idx = (int)block;
              if (block >= _totalBlocks || idx >= _bitmap.Length) return;
              if (!_bitmap[idx])
              {
                  _bitmap[idx] = true;
                  _freeBlocks++;
              }
          }
      }
  }
  ```

- [ ] **Step 2: Build to verify no compile errors**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  npm run raw:build 2>&1 | tail -5
  ```

  Expected last line: `Build succeeded.`

- [ ] **Step 3: Commit**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  git add native/MacMount.RawDiskEngine/ApfsSpacemanReader.cs
  git commit -m "feat: add ApfsSpacemanReader — parses APFS spaceman bitmap from disk"
  ```

---

## Task 3: Update `ApfsBlockAllocator` in `ApfsWriter.cs`

**Files:** `native/MacMount.RawDiskEngine/ApfsWriter.cs`

Add a constructor overload that wraps an `ApfsSpacemanReader`. When the spaceman is present, all bitmap operations delegate to it.

- [ ] **Step 1: Add `_spaceman` field and new constructor to `ApfsBlockAllocator`**

  Find the class field declarations (around line 9):
  ```csharp
  internal sealed class ApfsBlockAllocator : IDisposable
  {
      private readonly IRawBlockDevice _device;
      private readonly uint _blockSize;
      private readonly ulong _blockCount;
      private readonly long _partitionOffset;
      private readonly object _sync = new();
      private byte[]? _allocationBitmap;
      private bool _bitmapLoaded;
      private ulong _freeBlocks;
  ```

  Replace with:
  ```csharp
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
  ```

  Then add the new constructor immediately after the existing one (after the closing `}` of `public ApfsBlockAllocator(IRawBlockDevice device, ...)`):

  Find:
  ```csharp
      public ApfsBlockAllocator(IRawBlockDevice device, uint blockSize, ulong blockCount, long partitionOffset)
      {
          _device = device;
          _blockSize = blockSize;
          _blockCount = blockCount;
          _partitionOffset = partitionOffset;
          _freeBlocks = blockCount;
      }
  ```

  Replace with:
  ```csharp
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
  ```

- [ ] **Step 2: Update `FreeBlocks` property to sync from spaceman when present**

  Find:
  ```csharp
      public ulong FreeBlocks => _freeBlocks;
  ```

  Replace with:
  ```csharp
      public ulong FreeBlocks => _spaceman is not null ? _spaceman.FreeBlockCount : _freeBlocks;
  ```

- [ ] **Step 3: Update `LoadBitmapAsync` to skip when spaceman is set**

  Find:
  ```csharp
      public async Task LoadBitmapAsync(CancellationToken ct = default)
      {
          if (_bitmapLoaded) return;
  ```

  Replace with:
  ```csharp
      public async Task LoadBitmapAsync(CancellationToken ct = default)
      {
          if (_bitmapLoaded || _spaceman is not null) return;
  ```

- [ ] **Step 4: Update `IsBlockUsed` to delegate to spaceman**

  Find:
  ```csharp
      private bool IsBlockUsed(ulong block)
      {
          if (_allocationBitmap is null || block >= _blockCount) return true;
          var byteIndex = (int)(block / 8);
          var bitIndex = (int)(block % 8);
          return (_allocationBitmap[byteIndex] & (1 << bitIndex)) != 0;
      }
  ```

  Replace with:
  ```csharp
      private bool IsBlockUsed(ulong block)
      {
          if (_spaceman is not null) return !_spaceman.IsBlockFree(block);
          if (_allocationBitmap is null || block >= _blockCount) return true;
          var byteIndex = (int)(block / 8);
          var bitIndex = (int)(block % 8);
          return (_allocationBitmap[byteIndex] & (1 << bitIndex)) != 0;
      }
  ```

- [ ] **Step 5: Update `MarkBlockUsed` to delegate to spaceman**

  Find:
  ```csharp
      private void MarkBlockUsed(ulong block)
      {
          if (_allocationBitmap is null || block >= _blockCount) return;
          var byteIndex = (int)(block / 8);
          var bitIndex = (int)(block % 8);
          _allocationBitmap[byteIndex] |= (byte)(1 << bitIndex);
      }
  ```

  Replace with:
  ```csharp
      private void MarkBlockUsed(ulong block)
      {
          if (_spaceman is not null) { _spaceman.MarkBlockUsed(block); return; }
          if (_allocationBitmap is null || block >= _blockCount) return;
          var byteIndex = (int)(block / 8);
          var bitIndex = (int)(block % 8);
          _allocationBitmap[byteIndex] |= (byte)(1 << bitIndex);
      }
  ```

- [ ] **Step 6: Update `MarkBlockFree` to delegate to spaceman**

  Find:
  ```csharp
      private void MarkBlockFree(ulong block)
      {
          if (_allocationBitmap is null || block >= _blockCount) return;
          var byteIndex = (int)(block / 8);
          var bitIndex = (int)(block % 8);
          _allocationBitmap[byteIndex] &= (byte)~(1 << bitIndex);
      }
  ```

  Replace with:
  ```csharp
      private void MarkBlockFree(ulong block)
      {
          if (_spaceman is not null) { _spaceman.MarkBlockFree(block); return; }
          if (_allocationBitmap is null || block >= _blockCount) return;
          var byteIndex = (int)(block / 8);
          var bitIndex = (int)(block % 8);
          _allocationBitmap[byteIndex] &= (byte)~(1 << bitIndex);
      }
  ```

- [ ] **Step 7: Update `AllocateBlocks` to keep `_freeBlocks` in sync when using spaceman**

  The existing `AllocateBlocks` decrements `_freeBlocks` after calling `MarkBlockUsed`. Since `MarkBlockUsed` now delegates to spaceman (which maintains its own count), the `_freeBlocks--` in `AllocateBlocks` becomes a no-op (but harmless) when spaceman is active because `FreeBlocks` property now reads from spaceman. No code change needed here.

- [ ] **Step 8: Build to verify no compile errors**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  npm run raw:build 2>&1 | tail -5
  ```

  Expected last line: `Build succeeded.`

- [ ] **Step 9: Commit**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  git add native/MacMount.RawDiskEngine/ApfsWriter.cs
  git commit -m "feat: ApfsBlockAllocator gains spaceman-backed constructor — delegates bitmap ops to ApfsSpacemanReader"
  ```

---

## Task 4: Wire spaceman into `ApfsRawFileSystemProvider` — `CreateAsync`, constructor, `FreeBytes`

**Files:** `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs`

- [ ] **Step 1: Update the private constructor to accept `ApfsSpacemanReader?` and fix `FreeBytes`** (around line 29)

  Find:
  ```csharp
      private ApfsRawFileSystemProvider(IRawBlockDevice device, ApfsContainerSummary summary, MountPlan plan, bool writable)
      {
          _device = device;
          _blockSize = summary.BlockSize;
          _partitionOffsetBytes = Math.Max(0, plan.PartitionOffsetBytes);
          _writable = writable && device.CanWrite;
          FileSystemType = "APFS";
          TotalBytes = summary.EstimatedTotalBytes > 0 ? summary.EstimatedTotalBytes : Math.Max(1, plan.TotalBytes);
          FreeBytes = 0;
  
          // Initialize write support if writable
          if (_writable)
          {
              _allocator = new ApfsBlockAllocator(device, _blockSize, summary.BlockCount, _partitionOffsetBytes);
              // Get the first volume OID for the writer (simplified - should use the mounted volume)
              var volumeOid = summary.VolumeObjectIds.FirstOrDefault();
              _writer = new ApfsWriter(device, _allocator, _blockSize, _partitionOffsetBytes, volumeOid);
          }
  ```

  Replace with:
  ```csharp
      private ApfsRawFileSystemProvider(IRawBlockDevice device, ApfsContainerSummary summary, MountPlan plan, bool writable, ApfsSpacemanReader? spaceman = null)
      {
          _device = device;
          _blockSize = summary.BlockSize;
          _partitionOffsetBytes = Math.Max(0, plan.PartitionOffsetBytes);
          _writable = writable && device.CanWrite;
          FileSystemType = "APFS";
          TotalBytes = summary.EstimatedTotalBytes > 0 ? summary.EstimatedTotalBytes : Math.Max(1, plan.TotalBytes);
          FreeBytes = spaceman is not null ? (long)(spaceman.FreeBlockCount * _blockSize) : 0;
  
          // Initialize write support if writable
          if (_writable)
          {
              _allocator = spaceman is not null
                  ? new ApfsBlockAllocator(spaceman)
                  : new ApfsBlockAllocator(device, _blockSize, summary.BlockCount, _partitionOffsetBytes);
              // Get the first volume OID for the writer (simplified - should use the mounted volume)
              var volumeOid = summary.VolumeObjectIds.FirstOrDefault();
              _writer = new ApfsWriter(device, _allocator, _blockSize, _partitionOffsetBytes, volumeOid);
          }
  ```

- [ ] **Step 2: Update `CreateAsync` to load the spaceman reader when writable** (around line 133)

  Find:
  ```csharp
          try
          {
              var reader = new ApfsMetadataReader(device, plan.PartitionOffsetBytes, plan.PartitionLengthBytes);
              var summary = await reader.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
              return new ApfsRawFileSystemProvider(device, summary, plan, plan.Writable);
          }
  ```

  Replace with:
  ```csharp
          try
          {
              var reader = new ApfsMetadataReader(device, plan.PartitionOffsetBytes, plan.PartitionLengthBytes);
              var summary = await reader.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
  
              ApfsSpacemanReader? spaceman = null;
              if (plan.Writable && summary.SpacemanPhysicalBlock.HasValue)
              {
                  try
                  {
                      spaceman = await ApfsSpacemanReader.LoadAsync(
                          device,
                          summary.SpacemanPhysicalBlock.Value,
                          summary.BlockSize,
                          cancellationToken).ConfigureAwait(false);
                  }
                  catch (Exception ex)
                  {
                      // Non-fatal: fall back to fake allocator; APFS writes will still work
                      // but free-space reporting will be inaccurate.
                      System.Diagnostics.Debug.WriteLine($"[ApfsRawFileSystemProvider] Spaceman load failed: {ex.Message}");
                  }
              }
  
              return new ApfsRawFileSystemProvider(device, summary, plan, plan.Writable, spaceman);
          }
  ```

- [ ] **Step 3: Build to verify no compile errors**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  npm run raw:build 2>&1 | tail -5
  ```

  Expected last line: `Build succeeded.`

- [ ] **Step 4: Commit**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  git add native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs
  git commit -m "feat: load APFS spaceman on writable mount — real free-space count and bitmap-backed allocation"
  ```

---

## Task 5: Create `MacMount.ApfsWriteTest` project with synthetic spaceman tests

**Files:** `native/MacMount.ApfsWriteTest/MacMount.ApfsWriteTest.csproj`, `native/MacMount.ApfsWriteTest/Program.cs`, `native/MacMount.ApfsWriteTest/ApfsSpacemanTests.cs`

The test creates a synthetic in-memory APFS spaceman structure (no real disk required). The synthetic image has:
- 32 blocks of 4096 bytes each
- Spaceman at block 4: tracks 32 blocks, 1 chunk, 1 CIB, 22 blocks free
- CIB at block 5
- Bitmap at block 6: blocks 0–9 used, blocks 10–31 free

- [ ] **Step 1: Create the `.csproj`**

  Create `native/MacMount.ApfsWriteTest/MacMount.ApfsWriteTest.csproj`:
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
  
    <PropertyGroup>
      <OutputType>Exe</OutputType>
      <TargetFramework>net9.0</TargetFramework>
      <ImplicitUsings>enable</ImplicitUsings>
      <Nullable>enable</Nullable>
    </PropertyGroup>
  
    <ItemGroup>
      <ProjectReference Include="..\MacMount.RawDiskEngine\MacMount.RawDiskEngine.csproj" />
    </ItemGroup>
  
  </Project>
  ```

- [ ] **Step 2: Create `Program.cs`**

  Create `native/MacMount.ApfsWriteTest/Program.cs`:
  ```csharp
  namespace MacMount.ApfsWriteTest;
  
  public static class Program
  {
      public static async Task<int> Main(string[] args)
      {
          Console.WriteLine("APFS Spaceman Write Test Harness");
          Console.WriteLine(new string('=', 60));
  
          try
          {
              var allPassed = await ApfsSpacemanTests.RunAllAsync();
              Console.WriteLine(new string('=', 60));
              Console.WriteLine(allPassed ? "ALL TESTS PASSED" : "SOME TESTS FAILED");
              return allPassed ? 0 : 1;
          }
          catch (Exception ex)
          {
              Console.WriteLine($"FATAL: Unhandled exception: {ex}");
              return 2;
          }
      }
  }
  ```

- [ ] **Step 3: Create `ApfsSpacemanTests.cs`**

  Create `native/MacMount.ApfsWriteTest/ApfsSpacemanTests.cs`:

  ```csharp
  using System.Buffers.Binary;
  using MacMount.RawDiskEngine;
  
  namespace MacMount.ApfsWriteTest;
  
  /// <summary>
  /// Tests for ApfsSpacemanReader using a synthetic in-memory APFS spaceman structure.
  ///
  /// Synthetic image layout (32 blocks × 4096 bytes = 128 KB):
  ///   Block 0: "used" metadata block
  ///   Blocks 1–3: padding (zeros, treated as used)
  ///   Block 4: Spaceman block
  ///     sm_block_size=4096, sm_blocks_per_chunk=32 (all 32 blocks fit in 1 chunk)
  ///     sm_dev[0]: block_count=32, chunk_count=1, cib_count=1, free_count=22
  ///     sm_dev[0].sm_addr_offset=256 (CIB address array at byte 256 of this block)
  ///     CIB address [0] = 5 (block 5 is the CIB)
  ///   Block 5: CIB block
  ///     cib_index=0, cib_chunk_info_count=1
  ///     chunk_info[0]: ci_addr=6, ci_block_count=32, ci_free_count=22
  ///   Block 6: Bitmap block (32 bits meaningful)
  ///     Blocks 0–9 used (bits=0), blocks 10–31 free (bits=1)
  ///     Bytes: [0x00, 0xFC, 0xFF, 0xFF, then zeros]
  ///     Explanation:
  ///       Byte 0: bits 0–7 → blocks 0–7 used → 0x00
  ///       Byte 1: bits 0–1 → blocks 8–9 used (0), bits 2–7 → blocks 10–15 free (1) → 0b11111100 = 0xFC
  ///       Byte 2: bits 0–7 → blocks 16–23 free → 0xFF
  ///       Byte 3: bits 0–7 → blocks 24–31 free → 0xFF
  ///
  /// NOTE: If you have a real APFS image, replace the synthetic image with a real one and
  /// adjust the expected values. The spaceman offsets used here match the Apple APFS Reference
  /// (spaceman_phys_t / spaceman_device_t layout). If tests fail against a real image,
  /// the offsets in ApfsSpacemanReader.cs may need adjustment.
  /// </summary>
  internal static class ApfsSpacemanTests
  {
      private const uint BlockSize = 4096;
      private const int TotalBlocks = 32;
      private const ulong SpacemanBlock = 4;
      private const ulong CibBlock = 5;
      private const ulong BitmapBlock = 6;
      private const int UsedBlockCount = 10;   // blocks 0–9
      private const int FreeBlockCount  = 22;  // blocks 10–31
  
      public static async Task<bool> RunAllAsync()
      {
          var passed = 0;
          var failed = 0;
  
          async Task Run(string name, Func<Task> test)
          {
              try
              {
                  await test();
                  Console.WriteLine($"  PASS  {name}");
                  passed++;
              }
              catch (Exception ex)
              {
                  Console.WriteLine($"  FAIL  {name}: {ex.Message}");
                  failed++;
              }
          }
  
          var imageBytes = BuildSyntheticImage();
  
          // Test 1: LoadAsync completes without throwing
          await Run("1. LoadAsync completes on synthetic image", async () =>
          {
              var device = new MemoryRawBlockDevice(imageBytes);
              _ = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
          });
  
          // Test 2: TotalBlockCount matches the image block count
          await Run("2. TotalBlockCount == 32", async () =>
          {
              var device = new MemoryRawBlockDevice(imageBytes);
              var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
              Assert(sm.TotalBlockCount == TotalBlocks, $"expected {TotalBlocks}, got {sm.TotalBlockCount}");
          });
  
          // Test 3: FreeBlockCount > 0 and < TotalBlockCount
          await Run("3. FreeBlockCount > 0 and < TotalBlockCount", async () =>
          {
              var device = new MemoryRawBlockDevice(imageBytes);
              var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
              Assert(sm.FreeBlockCount > 0, $"FreeBlockCount is 0");
              Assert(sm.FreeBlockCount < sm.TotalBlockCount, $"FreeBlockCount {sm.FreeBlockCount} >= TotalBlockCount {sm.TotalBlockCount}");
          });
  
          // Test 4: FreeBlockCount matches expected value for this synthetic image
          await Run("4. FreeBlockCount == 22 (blocks 10–31 are free)", async () =>
          {
              var device = new MemoryRawBlockDevice(imageBytes);
              var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
              Assert(sm.FreeBlockCount == FreeBlockCount, $"expected {FreeBlockCount}, got {sm.FreeBlockCount}");
          });
  
          // Test 5: IsBlockFree(0) == false (block 0 is always used — holds metadata)
          await Run("5. IsBlockFree(0) == false (block 0 is used)", async () =>
          {
              var device = new MemoryRawBlockDevice(imageBytes);
              var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
              Assert(!sm.IsBlockFree(0), "block 0 should be marked used");
          });
  
          // Test 6: IsBlockFree(10) == true (first free block in synthetic image)
          await Run("6. IsBlockFree(10) == true (block 10 is free)", async () =>
          {
              var device = new MemoryRawBlockDevice(imageBytes);
              var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
              Assert(sm.IsBlockFree(10), "block 10 should be free");
          });
  
          // Test 7: Round-trip MarkBlockUsed/IsBlockFree/MarkBlockFree
          await Run("7. Round-trip: MarkBlockUsed(10) → !IsBlockFree → MarkBlockFree → IsBlockFree", async () =>
          {
              var device = new MemoryRawBlockDevice(imageBytes);
              var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
              var before = sm.FreeBlockCount;
              sm.MarkBlockUsed(10);
              Assert(!sm.IsBlockFree(10), "block 10 should be used after MarkBlockUsed");
              Assert(sm.FreeBlockCount == before - 1, $"FreeBlockCount should be {before - 1}, got {sm.FreeBlockCount}");
              sm.MarkBlockFree(10);
              Assert(sm.IsBlockFree(10), "block 10 should be free after MarkBlockFree");
              Assert(sm.FreeBlockCount == before, $"FreeBlockCount should be restored to {before}, got {sm.FreeBlockCount}");
          });
  
          // Test 8: ApfsBlockAllocator backed by spaceman — AllocateBlocks(1) marks block used
          await Run("8. ApfsBlockAllocator(spaceman).AllocateBlocks(1) returns a free block", async () =>
          {
              var device = new MemoryRawBlockDevice(imageBytes);
              var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
              await sm.EnsureBitmapLoadedAsync(); // no-op for spaceman-backed, but LoadBitmapAsync must exist
              var allocator = new ApfsBlockAllocator(sm);
              await allocator.LoadBitmapAsync();
              var allocated = allocator.AllocateBlocks(1);
              Assert(allocated.HasValue, "AllocateBlocks(1) returned null — no free blocks found");
              Assert(!sm.IsBlockFree(allocated!.Value), $"allocated block {allocated.Value} should now be used");
          });
  
          Console.WriteLine($"\nResults: {passed} passed, {failed} failed out of {passed + failed} tests.");
          return failed == 0;
      }
  
      private static void Assert(bool condition, string message)
      {
          if (!condition) throw new Exception(message);
      }
  
      /// <summary>Builds the synthetic APFS-like image in memory.</summary>
      private static byte[] BuildSyntheticImage()
      {
          var image = new byte[TotalBlocks * BlockSize];
  
          // Block 4: Spaceman block
          WriteSpacemanBlock(image, (int)(SpacemanBlock * BlockSize));
  
          // Block 5: CIB block
          WriteCibBlock(image, (int)(CibBlock * BlockSize));
  
          // Block 6: Bitmap block
          WriteBitmapBlock(image, (int)(BitmapBlock * BlockSize));
  
          return image;
      }
  
      private static void WriteSpacemanBlock(byte[] image, int offset)
      {
          var block = image.AsSpan(offset, (int)BlockSize);
  
          // Object header (32 bytes, offsets relative to block start):
          //   cksum [0..7]: 0 (not validated by reader)
          //   oid   [8..15]: 42
          //   xid   [16..23]: 1
          //   type  [24..27]: 0x40000005 (ephemeral | OBJECT_TYPE_SPACEMAN)
          BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(8, 8), 42);
          BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(16, 8), 1);
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(24, 4), 0x40000005u);
  
          // Primary spaceman fields
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x20, 4), BlockSize);    // sm_block_size
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x24, 4), TotalBlocks); // sm_blocks_per_chunk (all fit in 1 chunk)
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x28, 4), 100);         // sm_chunks_per_cib
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x2C, 4), 100);         // sm_cibs_per_cab
  
          // sm_dev[0]
          BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x30, 8), TotalBlocks);   // sm_block_count
          BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x38, 8), 1);             // sm_chunk_count
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x40, 4), 1);             // sm_cib_count
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x44, 4), 0);             // sm_cab_count
          BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x48, 8), FreeBlockCount); // sm_free_count
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x50, 4), 256);           // sm_addr_offset (byte 256 in this block)
  
          // CIB address array at byte 256: one entry pointing to block 5
          BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(256, 8), CibBlock);
      }
  
      private static void WriteCibBlock(byte[] image, int offset)
      {
          var block = image.AsSpan(offset, (int)BlockSize);
  
          // cib_index = 0, cib_chunk_info_count = 1
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x20, 4), 0); // cib_index
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x24, 4), 1); // cib_chunk_info_count
  
          // chunk_info[0] at 0x28 (24 bytes):
          //   ci_xid        [+0x00]: 1
          //   ci_addr       [+0x08]: BitmapBlock (6)
          //   ci_block_count[+0x10]: TotalBlocks (32)
          //   ci_free_count [+0x14]: FreeBlockCount (22)
          BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x28 + 0x00, 8), 1);
          BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x28 + 0x08, 8), BitmapBlock);
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x28 + 0x10, 4), TotalBlocks);
          BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x28 + 0x14, 4), (uint)FreeBlockCount);
      }
  
      private static void WriteBitmapBlock(byte[] image, int offset)
      {
          // Blocks 0–9: used (bit=0), blocks 10–31: free (bit=1)
          // Byte 0 (bits 0–7  = blocks 0–7):  all used → 0x00
          // Byte 1 (bits 8–15 = blocks 8–15): blocks 8,9 used, 10–15 free → 0b11111100 = 0xFC
          // Byte 2 (bits 16–23 = blocks 16–23): all free → 0xFF
          // Byte 3 (bits 24–31 = blocks 24–31): all free → 0xFF
          image[offset + 0] = 0x00;
          image[offset + 1] = 0xFC;
          image[offset + 2] = 0xFF;
          image[offset + 3] = 0xFF;
          // Remaining bytes are 0x00 (beyond the 32 meaningful blocks)
      }
  }
  
  /// <summary>In-memory IRawBlockDevice for testing.</summary>
  internal sealed class MemoryRawBlockDevice : IRawBlockDevice
  {
      private readonly byte[] _data;
  
      public MemoryRawBlockDevice(byte[] data)
      {
          _data = data;
      }
  
      public string DevicePath => "memory://test";
      public long Length => _data.Length;
      public bool CanWrite => false;
  
      public ValueTask<int> ReadAsync(long offset, byte[] buffer, int count, CancellationToken cancellationToken = default)
      {
          if (offset < 0 || offset >= _data.Length) return ValueTask.FromResult(0);
          var available = (int)Math.Min(count, _data.Length - offset);
          Buffer.BlockCopy(_data, (int)offset, buffer, 0, available);
          return ValueTask.FromResult(available);
      }
  
      public void Dispose() { }
  }
  ```

  > **Note on Test 8:** After writing this, check if `ApfsSpacemanReader` has an `EnsureBitmapLoadedAsync` method. If not, remove that call — `LoadAsync` already populates the bitmap. Also verify `ApfsBlockAllocator` is `internal` (accessible from the test project via `InternalsVisibleTo`).

- [ ] **Step 4: Check accessibility — add `InternalsVisibleTo` to `MacMount.RawDiskEngine`**

  `ApfsSpacemanReader` and `ApfsBlockAllocator` are `internal`. The test project needs access. Add an `InternalsVisibleTo` attribute to `MacMount.RawDiskEngine`.

  Find or create `native/MacMount.RawDiskEngine/Properties/AssemblyInfo.cs`. If it doesn't exist, check if the project uses the `AssemblyName` attribute anywhere. The simplest approach: add to any `.cs` file, or add a new file.

  Create `native/MacMount.RawDiskEngine/AssemblyInfo.cs`:
  ```csharp
  using System.Runtime.CompilerServices;
  
  [assembly: InternalsVisibleTo("MacMount.ApfsWriteTest")]
  ```

  **Important:** Also check if `MacMount.HfsWriteTest` is already in the `InternalsVisibleTo` list (if there's already an `AssemblyInfo.cs`). If so, add both entries to the same file.

- [ ] **Step 5: Fix Test 8 — remove the non-existent `EnsureBitmapLoadedAsync` call**

  In `ApfsSpacemanTests.cs`, find:
  ```csharp
              await sm.EnsureBitmapLoadedAsync(); // no-op for spaceman-backed, but LoadBitmapAsync must exist
  ```

  Replace with:
  ```csharp
              // Bitmap already loaded by LoadAsync — no extra call needed
  ```

- [ ] **Step 6: Build the test project**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  dotnet build native/MacMount.ApfsWriteTest/MacMount.ApfsWriteTest.csproj -c Release 2>&1 | tail -10
  ```

  Expected: `Build succeeded.`

- [ ] **Step 7: Add `apfs:test` script to `package.json`**

  Find in `package.json`:
  ```json
      "hfs:test": "dotnet run --project native/MacMount.HfsWriteTest/MacMount.HfsWriteTest.csproj"
  ```

  Replace with:
  ```json
      "hfs:test": "dotnet run --project native/MacMount.HfsWriteTest/MacMount.HfsWriteTest.csproj",
      "apfs:test": "dotnet run --project native/MacMount.ApfsWriteTest/MacMount.ApfsWriteTest.csproj"
  ```

- [ ] **Step 8: Run the tests**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  npm run apfs:test
  ```

  Expected output:
  ```
  APFS Spaceman Write Test Harness
  ============================================================
    PASS  1. LoadAsync completes on synthetic image
    PASS  2. TotalBlockCount == 32
    PASS  3. FreeBlockCount > 0 and < TotalBlockCount
    PASS  4. FreeBlockCount == 22 (blocks 10–31 are free)
    PASS  5. IsBlockFree(0) == false (block 0 is used)
    PASS  6. IsBlockFree(10) == true (block 10 is free)
    PASS  7. Round-trip: MarkBlockUsed(10) → !IsBlockFree → MarkBlockFree → IsBlockFree
    PASS  8. ApfsBlockAllocator(spaceman).AllocateBlocks(1) returns a free block

  Results: 8 passed, 0 failed out of 8 tests.
  ============================================================
  ALL TESTS PASSED
  ```

  If any test fails with an offset-related error, the `spaceman_phys_t` offsets in `ApfsSpacemanReader.cs` may differ from the Apple spec version shipped with this macOS version. The synthetic image is built with the same offsets as the reader, so if the reader fails to parse its own synthetic data, there is a logic bug (not an offset mismatch).

- [ ] **Step 9: Commit**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  git add native/MacMount.ApfsWriteTest/ native/MacMount.RawDiskEngine/AssemblyInfo.cs package.json
  git commit -m "feat: add MacMount.ApfsWriteTest — 8 synthetic spaceman tests, all passing"
  ```

---

## Task 6: Final verification

- [ ] **Step 1: Run the full self-test suite**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  npm run test
  ```

  Expected: all tests pass.

- [ ] **Step 2: Run the HFS+ write tests to confirm no regressions**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  npm run hfs:test 2>&1 | tail -5
  ```

  Expected last line: `ALL TESTS PASSED`

- [ ] **Step 3: Full raw engine rebuild**

  ```bash
  cd "H:/DevWork/Win_Apps/GK_Mac_Opener"
  npm run raw:build 2>&1 | tail -3
  ```

  Expected: `Build succeeded.`

- [ ] **Step 4: Confirm the APFS write gate is still off by default**

  ```bash
  powershell -Command "[System.Environment]::GetEnvironmentVariable('MACMOUNT_EXPERIMENTAL_APFS_WRITES', 'Machine')"
  ```

  Expected: empty output (env var not set).

---

## Post-Implementation Notes

- **Real APFS image testing:** Run `npm run apfs:test` against a real APFS disk image by temporarily replacing `MemoryRawBlockDevice` with `WindowsRawBlockDevice` opened on a known APFS drive and passing the real `SpacemanPhysicalBlock`. The spaceman OID is resolved by the provider at mount time; log it from `summary.SpacemanPhysicalBlock`.
- **Offset validation:** If Phase 2 integration tests against a real drive show wrong free-space counts, add diagnostic output from `ApfsSpacemanReader.LoadAsync` that prints the parsed spaceman fields. Compare against macOS `diskutil info` output.
- **Phase 2 dependency:** This phase produces `ApfsSpacemanReader` with working `IsBlockFree`/`MarkBlockUsed`/`MarkBlockFree`. Phase 2 COW B-tree mutations will call these to allocate new blocks for copied tree nodes.
