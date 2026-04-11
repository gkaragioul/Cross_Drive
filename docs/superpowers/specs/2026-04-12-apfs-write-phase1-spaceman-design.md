# APFS Write Support — Phase 1: Spaceman Parser Design

## Context

MacMount currently mounts APFS drives read-only. Full read-write support (equivalent to macOS) is the goal, decomposed into four phases:

- **Phase 1 (this spec):** Real free-block map from the APFS space manager
- Phase 2: Copy-on-Write B-tree mutation engine + object map updates
- Phase 3: File operations (create, delete, rename, write data, resize)
- Phase 4: Stress testing and edge cases

Each phase builds on the previous. Phase 1 is purely read infrastructure — no writes to disk occur. It is the foundation every subsequent phase depends on.

---

## Goal

Replace the fake `ApfsBlockAllocator` bitmap (which assumes all blocks are free) with a real bitmap sourced from the APFS space manager structure on disk. After Phase 1, the app will:

- Report accurate free space on APFS drives (currently always shows 0)
- Know exactly which blocks are free before attempting any allocation
- Have a correct in-memory bitmap that Phases 2–3 will update as blocks are allocated/freed

---

## APFS Spaceman Background

The APFS space manager (spaceman) is a container-level structure that tracks free and used blocks across the entire physical device. Key concepts:

- **Spaceman OID**: stored in the NX superblock at offset `0x98` (`nx_spaceman_oid`). Resolved to a physical block address via the container omap (same mechanism already used for volume enumeration).
- **Chunks**: blocks are grouped into chunks of `sm_blocks_per_chunk` each (typically 8192). Each chunk has one bitmap block on disk.
- **Chunk-Info Blocks (CIBs)**: each CIB describes multiple chunks, storing the physical block address of each chunk's bitmap and the count of free blocks in that chunk.
- **Bit encoding**: in APFS chunk bitmaps, `1` = free, `0` = used (opposite of HFS+).
- **Device index**: the spaceman tracks one or two devices (`sm_dev[0]` = main device). We only need `sm_dev[0]` for external drives.

The walk to build the free-block map:
1. Resolve spaceman OID → physical block via omap
2. Read spaceman block → extract `sm_blocks_per_chunk`, `sm_dev[0].sm_chunk_count`, `sm_dev[0].sm_addr_offset`
3. At `sm_addr_offset` within the spaceman block, read the array of CIB physical block addresses
4. For each CIB: read the CIB block, extract per-chunk bitmap addresses
5. For each chunk bitmap block: read it, decode bits into the in-memory free-block array

---

## New Component: `ApfsSpacemanReader`

**File:** `native/MacMount.RawDiskEngine/ApfsSpacemanReader.cs`

**Responsibility:** Read the APFS spaceman from disk and expose a queryable free-block bitmap. Also supports in-memory mutation (mark block used/free) for use by the allocator during Phases 2–3 writes.

```
public interface:
    static Task<ApfsSpacemanReader> LoadAsync(
        IRawBlockDevice device,
        ulong spacamanPhysBlock,
        uint blockSize,
        CancellationToken ct)

    ulong FreeBlockCount { get; }
    ulong TotalBlockCount { get; }
    bool IsBlockFree(ulong block)
    void MarkBlockUsed(ulong block)     // in-memory only — Phase 2 will flush to disk
    void MarkBlockFree(ulong block)     // in-memory only
```

**Internal state:** a `System.Collections.BitArray` sized to `TotalBlockCount` bits, populated from the chunk bitmaps. Thread-safe via a single `lock` (same pattern as existing `ApfsBlockAllocator`).

**Error handling:** if the spaceman block fails to parse (wrong magic, truncated read, unrecognised version), throw `InvalidOperationException` with a descriptive message. The caller (`ApfsRawFileSystemProvider.CreateAsync`) catches this and falls back to read-only mode.

---

## Changes to Existing Files

### `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs`

**`ReadNxSuperblockAtContainerBlockAsync`** — add one field:
```csharp
// After reading omapOid at offset 0xA0:
var spacemanOid = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(0x98));
```

**`ApfsContainerSummary` record** — add one field:
```csharp
ulong SpacemanOid   // new, 0 if not found
```

**`CreateAsync` (static factory on `ApfsRawFileSystemProvider`)** — when `plan.Writable`:
```csharp
if (plan.Writable && summary.SpacemanOid != 0)
{
    // The container omap is already indexed in summary.ResolvedVolumePointers / the
    // internal object index built by ReadSummaryAsync. Resolve the spaceman OID by
    // reading the omap B-tree directly (same pattern used for volume OID resolution).
    // Implementation note: expose a helper ApfsMetadataReader.ResolvePhysicalBlock(oid)
    // or re-read and walk the omap inline here.
    var spacemanPhysBlock = /* resolve summary.SpacemanOid via omap */;
    if (spacemanPhysBlock.HasValue)
    {
        var spacemanReader = await ApfsSpacemanReader.LoadAsync(
            device, spacemanPhysBlock.Value, summary.BlockSize, ct);
        allocator = new ApfsBlockAllocator(spacemanReader);
    }
}
```

**`FreeBytes` property** — return real data:
```csharp
public long FreeBytes => _allocator is not null && _allocator.FreeBlocks > 0
    ? (long)(_allocator.FreeBlocks * _blockSize)
    : 0;
```

### `native/MacMount.RawDiskEngine/ApfsWriter.cs` (`ApfsBlockAllocator`)

**Constructor** — add overload accepting `ApfsSpacemanReader`:
```csharp
public ApfsBlockAllocator(ApfsSpacemanReader spaceman)
{
    _spaceman = spaceman;
    _blockCount = spaceman.TotalBlockCount;
    _freeBlocks = spaceman.FreeBlockCount;
    _bitmapLoaded = true;  // already loaded by the reader
}
```

**`LoadBitmapAsync`** — skip if `_spaceman` is set (bitmap already populated by the reader).

**`FreeBlocks` property** — already exists; when spaceman is present, keep it in sync with `_spaceman.FreeBlockCount` rather than maintaining a separate counter.

**`IsBlockFree`, `MarkBlockUsed`, `MarkBlockFree`** — delegate to `_spaceman` if set, otherwise use the existing `_allocationBitmap` byte array (backwards compatibility for when no spaceman is available).

---

## Test Project: `MacMount.ApfsWriteTest`

**File:** `native/MacMount.ApfsWriteTest/Program.cs`

Pattern mirrors `MacMount.HfsWriteTest` — a standalone console app that runs against a file-backed APFS image and exits 0 on success, non-zero on failure.

**Test image:** a 32 MB APFS disk image at `native/MacMount.ApfsWriteTest/test-apfs.img`. Small enough to commit to git. Created once on macOS with `hdiutil create -size 32m -fs APFS -volname TestVol -o test-apfs.img`, then a handful of small test files copied in so the spaceman has real allocations to report. The image is committed to the repo; if macOS is unavailable, the build script documents how to recreate it.

**Tests:**
1. `LoadAsync` completes without throwing
2. `TotalBlockCount` matches expected value for the test image (derived from image size ÷ block size)
3. `FreeBlockCount` is > 0 and < `TotalBlockCount`
4. `FreeBlockCount` is within 5% of what `hdiutil info` reports (documented in a comment — not auto-verified since hdiutil is macOS-only)
5. `IsBlockFree(0)` returns `false` (block 0 is always used — it holds the NX superblock)
6. Round-trip: `MarkBlockUsed(someKnownFreeBlock)` → `IsBlockFree` returns `false` → `MarkBlockFree` → `IsBlockFree` returns `true`
7. `ApfsBlockAllocator` constructed from spaceman: `AllocateBlocks(1)` returns a block that `IsBlockFree` now reports as used

**Build target:** `npm run apfs:test` (to be added to `package.json`, mirrors `npm run hfs:test`)

---

## Out of Scope for Phase 1

- Writing updated spaceman bitmaps back to disk (Phase 3)
- COW B-tree mutations (Phase 2)
- Any file create / delete / rename / write (Phase 3)
- Encrypted APFS (spaceman is unencrypted even on encrypted volumes — safe to read)
- Snapshot spaceman trees (only the main device spaceman is needed for external drives)

---

## Risk

**Low.** This phase is read-only. The worst outcome from a spaceman parse failure is a thrown exception that causes `CreateAsync` to fall back to read-only mode — the same behaviour as today. No data can be corrupted by a read-only parse of the spaceman.
