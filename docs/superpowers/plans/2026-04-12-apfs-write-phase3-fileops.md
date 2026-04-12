# APFS Write Phase 3 + 4 — Catalog B-tree Mutations & Test Harness

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the five stub write methods in `ApfsWriter` (CreateFile, CreateDirectory, WriteFileData, DeleteEntry, SetFileSize) so they perform real in-place APFS catalog B-tree mutations, and verify them with a synthetic file-backed test harness.

**Architecture:** In-place writes only — read the existing fs-tree leaf block, deserialize its records into `ApfsBTreeNode`, insert/delete/modify, re-serialize with incremented XID + new Fletcher-64 checksum, write back to the same physical block. Single-leaf fs-tree assumption (correct for small external Mac drives). fs-tree block is resolved lazily from the volume superblock → volume omap on first write, or injected directly in tests.

**Tech Stack:** C# 12 / .NET 9, `MacMount.RawDiskEngine`, `MacMount.ApfsWriteTest`. All tests are in-memory (no real disk). Run with `npm run apfs:test`.

---

## File Structure

| Action | Path | Responsibility |
|--------|------|----------------|
| Modify | `native/MacMount.RawDiskEngine/ApfsBTreeNode.cs` | Add `Deserialize` static method |
| Modify | `native/MacMount.RawDiskEngine/ApfsWriter.cs` | Add fs-tree infrastructure + implement 5 stub methods |
| Create | `native/MacMount.ApfsWriteTest/ApfsFileOpsTests.cs` | Phase 4 test suite (10 tests) |
| Modify | `native/MacMount.ApfsWriteTest/Program.cs` | Wire Phase 4 suite into runner |

---

## APFS Key/Value Reference (used in every task below)

```
APFS B-tree key encoding:  obj_id_and_type = (type << 60) | cnid
  Type 3 = Inode       key: 8 bytes
  Type 8 = FileExtent  key: 16 bytes (8-byte base + 8-byte logical_offset)
  Type 9 = DirRecord   key: 10+name_len bytes (8-byte base + u16 name_len + name+NUL)

Inode key  (8 bytes):  (3UL << 60) | cnid
DirRec key (variable): (9UL << 60) | parentCnid | name_len(u16) | name(UTF-8+NUL)
Extent key (16 bytes): (8UL << 60) | fileCnid  | logicalOffset(u64)

Inode value (j_inode_val_t, 92 bytes, no xfields):
  +0x00  8  parent_id
  +0x08  8  private_id (= cnid)
  +0x10  8  create_time (ns since epoch)
  +0x18  8  mod_time
  +0x20  8  change_time
  +0x28  8  access_time
  +0x30  8  internal_flags (0)
  +0x38  4  nchildren (dir) or nlink=1 (file), i32
  +0x3C  4  default_protection_class (0)
  +0x40  4  write_generation_counter (0)
  +0x44  4  bsd_flags (0)
  +0x48  4  owner uid (0)
  +0x4C  4  group gid (0)
  +0x50  2  mode: 0x81A4=regular, 0x41ED=directory
  +0x52  2  pad1 (0)
  +0x54  8  uncompressed_size  ← used as file size (simplification)
  Total = 92 bytes

DirRec value (j_drec_val_t, 18 bytes, no xfields):
  +0x00  8  file_id (cnid of entry)
  +0x08  8  date_added (ns)
  +0x10  2  flags: DT_REG=0x0008, DT_DIR=0x0004

Extent value (j_file_extent_val_t, 24 bytes):
  +0x00  8  len_and_flags: byte length in bits 55:0, flags 63:56 (0)
  +0x08  8  phys_block_num
  +0x10  8  crypto_id (0 = unencrypted)

Volume superblock offsets (APFS spec):
  0x80  apfs_omap_oid      (physical OID → direct block number of volume omap root)
  0x88  apfs_root_tree_oid (virtual OID of fs-tree root, resolve via omap)

Omap B-tree record:
  key (16 bytes): oid(u64) + xid(u64)
  val (16 bytes): flags(u32) + size(u32) + paddr(u64)   ← paddr at val+0x08
```

---

## Task 1: Add `ApfsBTreeNode.Deserialize`

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsBTreeNode.cs`

- [ ] **Step 1: Add the Deserialize static method after the Serialize method (before CompareKeys)**

  In `ApfsBTreeNode.cs`, after the closing `}` of `Serialize()` and before `private static int CompareKeys`:

  ```csharp
  /// <summary>
  /// Reconstructs an <see cref="ApfsBTreeNode"/> from a raw block buffer.
  /// Returns null if the buffer is too small or has no valid btn_nkeys field.
  /// </summary>
  public static ApfsBTreeNode? Deserialize(byte[] block, uint blockSize, bool isRoot = false)
  {
      if (block.Length < HeaderSize) return null;

      var oid        = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(0x08, 8));
      var xid        = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(0x10, 8));
      var objType    = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x18, 4));
      var objSubtype = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x1C, 4));
      var nkeys      = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x24, 4));

      var node = new ApfsBTreeNode(blockSize, oid, xid, objType, objSubtype, isRoot);

      int tocBase = HeaderSize; // 0x38
      for (uint i = 0; i < nkeys; i++)
      {
          int tocOff = tocBase + (int)(i * TocEntrySize);
          if (tocOff + TocEntrySize > block.Length) break;

          var kOff = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(tocOff + 0, 2));
          var kLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(tocOff + 2, 2));
          var vOff = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(tocOff + 4, 2));
          var vLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(tocOff + 6, 2));

          if (kOff + kLen > block.Length || vOff + vLen > block.Length) break;

          var key = block.AsSpan(kOff, kLen).ToArray();
          var val = block.AsSpan(vOff, vLen).ToArray();
          node._records.Add((key, val));  // bypass Insert sorting — preserve on-disk order
      }

      return node;
  }
  ```

  Note: `_records` is private. Change `private readonly List<(byte[] Key, byte[] Value)> _records = new();` to `internal readonly List<(byte[] Key, byte[] Value)> _records = new();` so Deserialize (in the same namespace) can access it directly, or make `_records` accessible via a property. Simplest: keep it private and use a second constructor overload that accepts an initial list. Actually, since `Deserialize` is a `static` method on the same class, it **can** access private members. No change needed.

- [ ] **Step 2: Verify the field is accessible from the static method**

  `Deserialize` is a static member of `ApfsBTreeNode` — it can access `node._records` directly. No visibility change needed. Double-check by reviewing `_records` is `private readonly List<...>` and the static method is inside the same `sealed class ApfsBTreeNode`. ✓

- [ ] **Step 3: Build to confirm no compile errors**

  ```bash
  cd native/MacMount.RawDiskEngine && dotnet build -c Debug 2>&1 | tail -5
  ```
  Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

  ```bash
  git add native/MacMount.RawDiskEngine/ApfsBTreeNode.cs
  git commit -m "feat: add ApfsBTreeNode.Deserialize — reconstruct B-tree node from raw block bytes"
  ```

---

## Task 2: Add fs-tree infrastructure to `ApfsWriter`

Adds `_fsBTreeBlock` state, lazy omap-based resolution from the volume superblock, and `ReadFsBTreeAsync`/`WriteFsBTreeAsync` helpers. Also fixes the FlushAsync field offsets to match the APFS spec (0xA8 for num_files, 0xB0 for num_directories — the existing 0x90/0x98 offsets were wrong and happened to pass tests because the test image was built to match the wrong offsets).

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsWriter.cs`
- Modify: `native/MacMount.ApfsWriteTest/ApfsCowTests.cs` (fix test 9 offsets)

- [ ] **Step 1: Add `_fsBTreeBlock` field and `SetFsBTreeBlock` method to `ApfsWriter`**

  After the existing field declarations in `ApfsWriter` (after `private long _pendingDirCountDelta;`), add:

  ```csharp
  private ulong? _fsBTreeBlock;
  ```

  After the existing `AllocateCnid()` method, add:

  ```csharp
  /// <summary>
  /// Directly sets the physical block number of the fs-tree root/leaf.
  /// Call this in tests instead of relying on omap resolution.
  /// </summary>
  public void SetFsBTreeBlock(ulong block) => _fsBTreeBlock = block;
  ```

- [ ] **Step 2: Add `GetFsBTreeBlockAsync` with lazy omap resolution**

  After `SetFsBTreeBlock`, add the following three methods:

  ```csharp
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
  /// Reads the volume superblock, follows the volume omap, and returns
  /// the physical block number of the fs-tree root node.
  /// </summary>
  private async Task<ulong?> ResolveRootTreeBlockAsync(ulong vsbBlock, CancellationToken ct)
  {
      // --- 1. Read volume superblock ---
      var vsb = new byte[_blockSize];
      var read = await _device.ReadAsync(
          _partitionOffset + (long)(vsbBlock * _blockSize), vsb, (int)_blockSize, ct)
          .ConfigureAwait(false);
      if (read < (int)_blockSize) return null;

      // apfs_omap_oid at 0x80 — physical OID (= direct block number, partition-relative)
      var omapOid      = BinaryPrimitives.ReadUInt64LittleEndian(vsb.AsSpan(0x80, 8));
      // apfs_root_tree_oid at 0x88 — virtual OID, must be resolved through omap
      var rootTreeOid  = BinaryPrimitives.ReadUInt64LittleEndian(vsb.AsSpan(0x88, 8));
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

      var buf = new byte[_blockSize];
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
  ```

- [ ] **Step 3: Fix FlushAsync field offsets to match APFS spec**

  In `FlushAsync`, change the two offset comments and field reads:

  Old (wrong — these are extentref/snap-meta OIDs per the spec):
  ```csharp
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
  ```

  New (correct per APFS spec — apfs_num_files at 0xA8, apfs_num_directories at 0xB0):
  ```csharp
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
  ```

- [ ] **Step 4: Fix ApfsCowTests test 9 to use correct offsets**

  In `ApfsCowTests.cs`, test 9 ("FlushAsync rewrites volume superblock..."), find the setup lines:
  ```csharp
  BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x90, 8), 3);    // apfs_num_files = 3
  BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x98, 8), 1);    // apfs_num_directories = 1
  ```

  Change to:
  ```csharp
  BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0xA8, 8), 3);    // apfs_num_files = 3
  BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0xB0, 8), 1);    // apfs_num_directories = 1
  ```

  And the assertion reads:
  ```csharp
  var newFileCount = BinaryPrimitives.ReadUInt64LittleEndian(after.Slice(0x90, 8));
  var newDirCount  = BinaryPrimitives.ReadUInt64LittleEndian(after.Slice(0x98, 8));
  ```

  Change to:
  ```csharp
  var newFileCount = BinaryPrimitives.ReadUInt64LittleEndian(after.Slice(0xA8, 8));
  var newDirCount  = BinaryPrimitives.ReadUInt64LittleEndian(after.Slice(0xB0, 8));
  ```

- [ ] **Step 5: Build and run existing tests to confirm they still pass**

  ```bash
  npm run apfs:test
  ```

  Expected: `ALL SUITES PASSED` (all Phase 1 + Phase 2 tests green). If any fail, the offset change above may not have been applied consistently — check both the write (test setup) and the read (assertions) are both updated.

- [ ] **Step 6: Commit**

  ```bash
  git add native/MacMount.RawDiskEngine/ApfsWriter.cs native/MacMount.ApfsWriteTest/ApfsCowTests.cs
  git commit -m "feat: add ApfsWriter fs-tree read/write helpers; fix FlushAsync offsets to APFS spec (0xA8/0xB0)"
  ```

---

## Task 3: Add key/value builder helpers to `ApfsWriter`

Private static helpers to encode APFS key/value byte arrays. These are used by all five write methods.

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsWriter.cs`

- [ ] **Step 1: Add the six builder methods and two reader helpers at the bottom of ApfsWriter, before `Dispose()`**

  ```csharp
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
      var nameLen   = (ushort)(nameBytes.Length + 1); // +1 for NUL
      var k = new byte[10 + nameLen];
      BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(0, 8), (9UL << 60) | parentCnid);
      BinaryPrimitives.WriteUInt16LittleEndian(k.AsSpan(8, 2), nameLen);
      nameBytes.CopyTo(k.AsSpan(10));
      // k[10 + nameBytes.Length] = 0x00 (NUL) — already zero from new byte[]
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
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x00, 8), parentCnid);  // parent_id
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x08, 8), cnid);        // private_id
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x10, 8), now);         // create_time
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x18, 8), now);         // mod_time
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x20, 8), now);         // change_time
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x28, 8), now);         // access_time
      // internal_flags = 0 (0x30)
      BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(0x38, 4), isDir ? 0 : 1); // nchildren/nlink
      // protection_class, write_gen_counter, bsd_flags = 0 (0x3C, 0x40, 0x44)
      // owner, group = 0 (0x48, 0x4C)
      ushort mode = isDir ? (ushort)0x41ED : (ushort)0x81A4;
      BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(0x50, 2), mode);        // mode
      // pad1 = 0 (0x52)
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x54, 8), (ulong)Math.Max(0, size)); // uncompressed_size
      return v;
  }

  private static byte[] BuildDrRecVal(uint cnid, bool isDir)
  {
      var v   = new byte[18]; // j_drec_val_t without xfields
      var now = GetApfsTimeNs();
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x00, 8), cnid);        // file_id
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x08, 8), now);         // date_added
      ushort dtFlags = isDir ? (ushort)0x0004 : (ushort)0x0008;                 // DT_DIR=4, DT_REG=8
      BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(0x10, 2), dtFlags);
      return v;
  }

  private static byte[] BuildExtentVal(long byteLength, ulong physBlock)
  {
      var v = new byte[24]; // j_file_extent_val_t
      // len_and_flags: length in bits 55:0, flags in bits 63:56 (0)
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x00, 8), (ulong)byteLength & 0x00FFFFFFFFFFFFFF_UL);
      BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0x08, 8), physBlock);   // phys_block_num
      // crypto_id = 0 (0x10) — already zero
      return v;
  }

  // ── Value readers ─────────────────────────────────────────────────────────

  /// <summary>Reads the file size from an inode value (uncompressed_size at +0x54).</summary>
  private static long ReadInodeSize(byte[] val) =>
      val.Length >= 92
          ? (long)BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(0x54, 8))
          : 0;

  /// <summary>
  /// Updates the size field in an existing inode value buffer in-place.
  /// Returns the same array (mutated).
  /// </summary>
  private static byte[] UpdateInodeSize(byte[] val, long newSize)
  {
      if (val.Length >= 92)
          BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(0x54, 8), (ulong)Math.Max(0, newSize));
      return val;
  }

  /// <summary>Returns the physical block and byte length from an extent value.</summary>
  private static (ulong PhysBlock, long ByteLength) ReadExtentVal(byte[] val)
  {
      if (val.Length < 24) return (0, 0);
      var lenAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(0x00, 8));
      var phys        = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(0x08, 8));
      return (phys, (long)(lenAndFlags & 0x00FFFFFFFFFFFFFF_UL));
  }
  ```

- [ ] **Step 2: Build to verify no compile errors**

  ```bash
  cd native/MacMount.RawDiskEngine && dotnet build -c Debug 2>&1 | tail -5
  ```

  Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

  ```bash
  git add native/MacMount.RawDiskEngine/ApfsWriter.cs
  git commit -m "feat: add APFS key/value builder + reader helpers to ApfsWriter"
  ```

---

## Task 4: Implement `CreateFileAsync`

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsWriter.cs`

- [ ] **Step 1: Replace the stub body of `CreateFileAsync`**

  Find the existing method:
  ```csharp
  public async Task<uint> CreateFileAsync(uint parentCnid, string name, byte[]? initialData = null, CancellationToken ct = default)
  {
      if (!IsWritable) throw new InvalidOperationException("Device is read-only");

      var cnid = AllocateCnid();

      // Allocate blocks for data if provided
      if (initialData is not null && initialData.Length > 0)
      {
  ```

  Replace the **entire body** of `CreateFileAsync` with:

  ```csharp
  public async Task<uint> CreateFileAsync(uint parentCnid, string name, byte[]? initialData = null, CancellationToken ct = default)
  {
      if (!IsWritable) throw new InvalidOperationException("Device is read-only");

      var cnid  = AllocateCnid();
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
          node.Insert(BuildInodeKey(cnid),           BuildInodeVal(cnid, parentCnid, false, size));
          node.Insert(BuildDrRecKey(parentCnid, name), BuildDrRecVal(cnid, false));

          // Extent record only if there is initial data
          if (initialData is not null && initialData.Length > 0)
              node.Insert(BuildExtentKey(cnid, 0), BuildExtentVal(initialData.Length, startBlock));

          await WriteFsBTreeAsync(node, block, ct).ConfigureAwait(false);
      }

      TrackFileCreated();
      return cnid;
  }
  ```

- [ ] **Step 2: Build**

  ```bash
  cd native/MacMount.RawDiskEngine && dotnet build -c Debug 2>&1 | tail -5
  ```

  Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

  ```bash
  git add native/MacMount.RawDiskEngine/ApfsWriter.cs
  git commit -m "feat: implement ApfsWriter.CreateFileAsync — inode + drec + extent records in fs-tree"
  ```

---

## Task 5: Implement `CreateDirectoryAsync`

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsWriter.cs`

- [ ] **Step 1: Replace the stub body of `CreateDirectoryAsync`**

  Find:
  ```csharp
  public async Task<uint> CreateDirectoryAsync(uint parentCnid, string name, CancellationToken ct = default)
  {
      if (!IsWritable) throw new InvalidOperationException("Device is read-only");

      var cnid = AllocateCnid();
      TrackDirCreated();
      return await Task.FromResult(cnid);
  }
  ```

  Replace with:
  ```csharp
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
  ```

- [ ] **Step 2: Build**

  ```bash
  cd native/MacMount.RawDiskEngine && dotnet build -c Debug 2>&1 | tail -5
  ```

  Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

  ```bash
  git add native/MacMount.RawDiskEngine/ApfsWriter.cs
  git commit -m "feat: implement ApfsWriter.CreateDirectoryAsync — inode + drec for directories"
  ```

---

## Task 6: Implement `WriteFileDataAsync`

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsWriter.cs`

- [ ] **Step 1: Replace the stub body of `WriteFileDataAsync`**

  Find:
  ```csharp
  public async Task WriteFileDataAsync(uint fileCnid, long offset, byte[] data, int count, CancellationToken ct = default)
  {
      if (!IsWritable) throw new InvalidOperationException("Device is read-only");
      if (count == 0) return;

      var blocksNeeded = (uint)((count + _blockSize - 1) / _blockSize);
      var startBlock = _allocator.AllocateBlocks(blocksNeeded);
  ```

  Replace the **entire method body** with:

  ```csharp
  public async Task WriteFileDataAsync(uint fileCnid, long offset, byte[] data, int count, CancellationToken ct = default)
  {
      if (!IsWritable) throw new InvalidOperationException("Device is read-only");
      if (count == 0) return;

      var blocksNeeded = (uint)((count + _blockSize - 1) / _blockSize);
      var startBlock   = _allocator.AllocateBlocks(blocksNeeded);

      if (!startBlock.HasValue)
          throw new IOException("WriteFileData: failed to allocate blocks.");

      // Write raw data blocks
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

      // Update fs-tree: add extent record + update inode size
      var fsBtree = await ReadFsBTreeAsync(ct).ConfigureAwait(false);
      if (fsBtree.HasValue)
      {
          var (node, block) = fsBtree.Value;

          // Add extent record for this logical offset → physical block mapping
          node.Insert(BuildExtentKey(fileCnid, offset), BuildExtentVal(count, startBlock.Value));

          // Update inode size if this write extends the file
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
  ```

  Note: `node.Records.FirstOrDefault(r => r.Key.SequenceEqual(inodeKey))` requires `using System.Linq;` — already present at the top of the file (`ApfsWriter.cs` uses the same namespace as `ApfsBTreeNode.cs`). If not, add it.

- [ ] **Step 2: Build**

  ```bash
  cd native/MacMount.RawDiskEngine && dotnet build -c Debug 2>&1 | tail -5
  ```

  Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

  ```bash
  git add native/MacMount.RawDiskEngine/ApfsWriter.cs
  git commit -m "feat: implement ApfsWriter.WriteFileDataAsync — extent record + inode size update"
  ```

---

## Task 7: Implement `DeleteEntryAsync`

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsWriter.cs`

- [ ] **Step 1: Replace the stub body of `DeleteEntryAsync`**

  Find:
  ```csharp
  public async Task DeleteEntryAsync(uint parentCnid, string name, CancellationToken ct = default)
  {
      if (!IsWritable) throw new InvalidOperationException("Device is read-only");
      TrackFileDeleted();
      await Task.CompletedTask;
  }
  ```

  Replace with:
  ```csharp
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
  ```

- [ ] **Step 2: Build**

  ```bash
  cd native/MacMount.RawDiskEngine && dotnet build -c Debug 2>&1 | tail -5
  ```

  Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

  ```bash
  git add native/MacMount.RawDiskEngine/ApfsWriter.cs
  git commit -m "feat: implement ApfsWriter.DeleteEntryAsync — remove inode + drec + free extent blocks"
  ```

---

## Task 8: Implement `SetFileSizeAsync`

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsWriter.cs`

- [ ] **Step 1: Replace the stub body of `SetFileSizeAsync`**

  Find:
  ```csharp
  public async Task SetFileSizeAsync(uint fileCnid, long newSize, CancellationToken ct = default)
  {
      if (!IsWritable) throw new InvalidOperationException("Device is read-only");
      await Task.CompletedTask;
  }
  ```

  Replace with:
  ```csharp
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

      // Step 2: On shrink — free blocks and remove extents that lie fully beyond newSize
      if (newSize >= 0)
      {
          var extentPrefix = (8UL << 60) | fileCnid;
          var extentsToTrim = node.Records
              .Where(r =>
              {
                  if (r.Key.Length < 16) return false;
                  if (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) != extentPrefix) return false;
                  var logicalStart = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8, 8));
                  return logicalStart >= newSize; // extent starts at or past new EOF → fully removed
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
      }

      await WriteFsBTreeAsync(node, block, ct).ConfigureAwait(false);
  }
  ```

- [ ] **Step 2: Build the full solution**

  ```bash
  cd native/MacMount.RawDiskEngine && dotnet build -c Debug 2>&1 | tail -5
  ```

  Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

  ```bash
  git add native/MacMount.RawDiskEngine/ApfsWriter.cs
  git commit -m "feat: implement ApfsWriter.SetFileSizeAsync — inode size update + trim extents on shrink"
  ```

---

## Task 9: Phase 4 — `ApfsFileOpsTests.cs` (test harness)

Builds a 12-block synthetic image with a volume superblock (block 1), an empty fs-tree leaf (block 2), and an omap to link them (block 3). Uses `SetFsBTreeBlock(2)` to skip the omap resolution path and test write operations directly.

**Files:**
- Create: `native/MacMount.ApfsWriteTest/ApfsFileOpsTests.cs`

- [ ] **Step 1: Create `ApfsFileOpsTests.cs`**

  ```csharp
  using System.Buffers.Binary;
  using System.Text;
  using MacMount.RawDiskEngine;

  namespace MacMount.ApfsWriteTest;

  /// <summary>
  /// Phase 3 / Phase 4 tests: APFS catalog B-tree mutations via ApfsWriter.
  ///
  /// Synthetic image layout (12 blocks × 4096 bytes = 48 KB):
  ///   Block 0 : container superblock (zeros — not used by writer)
  ///   Block 1 : volume superblock (APSB magic, omap_oid=3, root_tree_oid=2)
  ///   Block 2 : fs-tree leaf (empty ApfsBTreeNode, oid=2, xid=1)
  ///   Block 3 : volume omap B-tree root (one record: oid=2, xid=1, paddr=2)
  ///   Blocks 4–11 : free space (data blocks)
  /// </summary>
  internal static class ApfsFileOpsTests
  {
      private const uint BlockSize  = 4096;
      private const int  TotalBlocks = 12;

      // Well-known blocks in the synthetic image
      private const ulong FsBTreeBlock = 2;
      private const ulong OmapBlock    = 3;

      // Root CNID constant (APFS reserves 1=invalid, 2=root dir, 3=private dir)
      private const uint RootCnid = 2;

      public static async Task<bool> RunAllAsync()
      {
          var passed = 0;
          var failed = 0;

          async Task Run(string name, Func<Task> test)
          {
              try   { await test(); Console.WriteLine($"  PASS  {name}"); passed++; }
              catch (Exception ex) { Console.WriteLine($"  FAIL  {name}: {ex.Message}"); failed++; }
          }

          // ── Test 1: ApfsBTreeNode.Deserialize round-trip ────────────────────

          await Run("1. Deserialize round-trips a 2-record node (keys and values preserved)", async () =>
          {
              await Task.CompletedTask;
              var node = new ApfsBTreeNode(BlockSize, oid: 42, xid: 7,
                  objectType: 0x00000002u, objectSubtype: 0x0Du);
              var k1 = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(k1, (3UL << 60) | 100u);
              var v1 = new byte[92]; // inode-sized value
              var k2 = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(k2, (3UL << 60) | 200u);
              var v2 = new byte[92];
              node.Insert(k1, v1);
              node.Insert(k2, v2);
              var buf = node.Serialize()!;

              var restored = ApfsBTreeNode.Deserialize(buf, BlockSize)!;
              Assert(restored.RecordCount == 2, $"expected 2 records, got {restored.RecordCount}");
              Assert(restored.ObjectId == 42, $"oid mismatch: {restored.ObjectId}");
              Assert(restored.TransactionId == 7, $"xid mismatch: {restored.TransactionId}");
              var firstKey = BinaryPrimitives.ReadUInt64LittleEndian(restored.Records[0].Key.AsSpan(0, 8));
              Assert(firstKey == (3UL << 60) | 100u, $"first key mismatch: {firstKey}");
              Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid");
          });

          // ── Test 2: CreateFileAsync — inode + drec inserted ────────────────

          await Run("2. CreateFileAsync inserts inode (type 3) and drec (type 9) records", async () =>
          {
              var (image, writer) = BuildWriterAndImage();
              var cnid = await writer.CreateFileAsync(RootCnid, "hello.txt");

              var fsBuf = image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize).ToArray();
              var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
              Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after CreateFileAsync");
              Assert(node.RecordCount == 2, $"expected 2 records (inode+drec), got {node.RecordCount}");

              // Verify inode key type = 3
              var inodeKeyType = BinaryPrimitives.ReadUInt64LittleEndian(node.Records[0].Key.AsSpan(0, 8)) >> 60;
              Assert(inodeKeyType == 3, $"first record should be inode (type 3), got type {inodeKeyType}");
          });

          // ── Test 3: CreateFileAsync with data — extent record present ───────

          await Run("3. CreateFileAsync with initialData inserts extent (type 8) record", async () =>
          {
              var (image, writer) = BuildWriterAndImage();
              var data = Encoding.UTF8.GetBytes("hello from APFS!");
              var cnid = await writer.CreateFileAsync(RootCnid, "data.txt", data);

              var fsBuf = image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize).ToArray();
              var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
              Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid");
              Assert(node.RecordCount == 3, $"expected 3 records (inode+drec+extent), got {node.RecordCount}");

              // Find the extent record (key type = 8)
              var extentRec = node.Records.FirstOrDefault(r =>
                  r.Key.Length >= 8 && (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) >> 60) == 8);
              Assert(extentRec != default, "no extent record found");

              // Verify data was written to the device at the indicated physical block
              var physBlock = BinaryPrimitives.ReadUInt64LittleEndian(extentRec.Value.AsSpan(0x08, 8));
              Assert(physBlock >= 4 && physBlock < TotalBlocks, $"physBlock {physBlock} out of free range");
              var writtenSlice = image.AsSpan((int)(physBlock * BlockSize), data.Length);
              Assert(writtenSlice.SequenceEqual(data.AsSpan()), "data not written to device");
          });

          // ── Test 4: CreateDirectoryAsync — dir inode + drec ────────────────

          await Run("4. CreateDirectoryAsync inserts dir inode (mode 0x41ED) and drec with DT_DIR flag", async () =>
          {
              var (image, writer) = BuildWriterAndImage();
              var cnid = await writer.CreateDirectoryAsync(RootCnid, "subdir");

              var fsBuf = image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize).ToArray();
              var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
              Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after CreateDirectoryAsync");
              Assert(node.RecordCount == 2, $"expected 2 records, got {node.RecordCount}");

              // Inode value: mode at val+0x50 = 0x41ED
              var inodeRec = node.Records.First(r =>
                  (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) >> 60) == 3);
              var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeRec.Value.AsSpan(0x50, 2));
              Assert(mode == 0x41ED, $"expected directory mode 0x41ED, got 0x{mode:X4}");

              // DirRec value: flags at val+0x10 = 0x0004 (DT_DIR)
              var drecRec = node.Records.First(r =>
                  (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) >> 60) == 9);
              var dtFlags = BinaryPrimitives.ReadUInt16LittleEndian(drecRec.Value.AsSpan(0x10, 2));
              Assert(dtFlags == 0x0004, $"expected DT_DIR flags 0x0004, got 0x{dtFlags:X4}");
          });

          // ── Test 5: WriteFileDataAsync — extent added, inode size updated ──

          await Run("5. WriteFileDataAsync adds extent record and updates inode size", async () =>
          {
              var (image, writer) = BuildWriterAndImage();
              var cnid = await writer.CreateFileAsync(RootCnid, "write_me.txt"); // size=0
              var payload = Encoding.UTF8.GetBytes("APFS Phase 3 write data");
              await writer.WriteFileDataAsync(cnid, 0, payload, payload.Length);

              var fsBuf = image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize).ToArray();
              var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
              Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after WriteFileDataAsync");

              // Should now have inode + drec + extent = 3 records
              Assert(node.RecordCount == 3, $"expected 3 records, got {node.RecordCount}");

              // Inode size should equal payload length
              var inodeRec = node.Records.First(r =>
                  (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) >> 60) == 3 &&
                  (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) & 0x0FFFFFFFFFFFFFFF_UL) == cnid);
              var size = BinaryPrimitives.ReadUInt64LittleEndian(inodeRec.Value.AsSpan(0x54, 8));
              Assert(size == (ulong)payload.Length, $"expected size={payload.Length}, got {size}");
          });

          // ── Test 6: DeleteEntryAsync — all records removed, blocks freed ───

          await Run("6. DeleteEntryAsync removes inode, drec, extent records and frees blocks", async () =>
          {
              var (image, writer) = BuildWriterAndImage();
              var freeBlocksBefore = writer.AllocatorFreeBlocks;
              var data = new byte[4096]; // exactly 1 block
              var cnid = await writer.CreateFileAsync(RootCnid, "to_delete.txt", data);
              var freeAfterCreate = writer.AllocatorFreeBlocks;
              Assert(freeAfterCreate < freeBlocksBefore, "no block was allocated for initial data");

              await writer.DeleteEntryAsync(RootCnid, "to_delete.txt");

              var fsBuf = image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize).ToArray();
              var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
              Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after DeleteEntryAsync");
              Assert(node.RecordCount == 0, $"expected 0 records after delete, got {node.RecordCount}");
              Assert(writer.AllocatorFreeBlocks == freeBlocksBefore, "blocks not freed after delete");
          });

          // ── Test 7: SetFileSizeAsync — shrink frees blocks ─────────────────

          await Run("7. SetFileSizeAsync shrinks file: blocks freed, inode size updated", async () =>
          {
              var (image, writer) = BuildWriterAndImage();
              var data = new byte[8192]; // 2 blocks
              var cnid = await writer.CreateFileAsync(RootCnid, "shrink_me.txt", data);
              var freeAfterCreate = writer.AllocatorFreeBlocks;

              await writer.SetFileSizeAsync(cnid, 0); // shrink to zero

              var fsBuf = image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize).ToArray();
              var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
              Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after SetFileSizeAsync");

              // Inode size should now be 0
              var inodeRec = node.Records.First(r =>
                  (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) >> 60) == 3);
              var size = BinaryPrimitives.ReadUInt64LittleEndian(inodeRec.Value.AsSpan(0x54, 8));
              Assert(size == 0, $"expected inode size=0, got {size}");

              // Extent records should be removed (only inode + drec remain)
              Assert(node.RecordCount == 2, $"expected 2 records after shrink to 0, got {node.RecordCount}");
              Assert(writer.AllocatorFreeBlocks > freeAfterCreate, "blocks not freed after shrink");
          });

          // ── Test 8: Create + Write + Delete roundtrip ──────────────────────

          await Run("8. Create → Write → Delete roundtrip: fs-tree returns to empty, all blocks freed", async () =>
          {
              var (image, writer) = BuildWriterAndImage();
              var freeStart = writer.AllocatorFreeBlocks;

              var cnid = await writer.CreateFileAsync(RootCnid, "roundtrip.txt");
              await writer.WriteFileDataAsync(cnid, 0, new byte[4096], 4096);
              await writer.DeleteEntryAsync(RootCnid, "roundtrip.txt");

              var fsBuf = image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize).ToArray();
              var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
              Assert(node.RecordCount == 0, $"expected empty fs-tree, got {node.RecordCount} records");
              Assert(writer.AllocatorFreeBlocks == freeStart, "block leak: free count changed after create+delete");
              Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid");
          });

          // ── Test 9: fs-tree XID increments on each write ──────────────────

          await Run("9. Each write operation increments the fs-tree block XID", async () =>
          {
              var (image, writer) = BuildWriterAndImage();
              var xidBefore = BinaryPrimitives.ReadUInt64LittleEndian(
                  image.AsSpan((int)(FsBTreeBlock * BlockSize) + 0x10, 8));

              await writer.CreateFileAsync(RootCnid, "xid_check.txt");

              var xidAfter = BinaryPrimitives.ReadUInt64LittleEndian(
                  image.AsSpan((int)(FsBTreeBlock * BlockSize) + 0x10, 8));
              Assert(xidAfter > xidBefore, $"XID did not increment: before={xidBefore} after={xidAfter}");
          });

          // ── Test 10: FlushAsync updates VSB file/dir counts ────────────────

          await Run("10. FlushAsync updates VSB apfs_num_files and apfs_num_directories", async () =>
          {
              var (image, writer) = BuildWriterAndImage();

              await writer.CreateFileAsync(RootCnid, "flush_a.txt");
              await writer.CreateFileAsync(RootCnid, "flush_b.txt");
              await writer.CreateDirectoryAsync(RootCnid, "flush_dir");
              await writer.FlushAsync();

              // VSB is at block 1
              var vsb = image.AsSpan((int)(1 * BlockSize), (int)BlockSize);
              var numFiles = BinaryPrimitives.ReadUInt64LittleEndian(vsb.Slice(0xA8, 8));
              var numDirs  = BinaryPrimitives.ReadUInt64LittleEndian(vsb.Slice(0xB0, 8));
              Assert(numFiles == 2, $"expected 2 files, got {numFiles}");
              Assert(numDirs  == 1, $"expected 1 dir, got {numDirs}");
              Assert(ApfsChecksum.Verify(vsb), "VSB checksum invalid after flush");
          });

          Console.WriteLine($"\nResults: {passed} passed, {failed} failed out of {passed + failed} tests.");
          return failed == 0;
      }

      // ── Helpers ─────────────────────────────────────────────────────────────

      /// <summary>
      /// Builds a 12-block in-memory APFS image and a matching writable ApfsWriter.
      /// The writer's fs-tree block is injected directly (bypasses omap resolution).
      /// </summary>
      private static (byte[] image, ApfsWriter writer) BuildWriterAndImage()
      {
          var image = BuildSyntheticImage();
          var device = new WritableMemoryRawBlockDevice(image);

          // Allocator over 8 free blocks (4–11); blocks 0-3 are metadata
          var allocator = new ApfsBlockAllocator(device, BlockSize, TotalBlocks, partitionOffset: 0);
          allocator.LoadBitmapAsync().GetAwaiter().GetResult();
          // Mark blocks 0-3 as used (metadata area)
          for (ulong b = 0; b < 4; b++) _ = allocator.AllocateBlocks(1); // consume blocks 0-3 in order

          var writer = new ApfsWriter(
              device, allocator, BlockSize,
              partitionOffset: 0,
              volumeOid: 500,
              volumeSuperblockBlock: 1,
              currentXid: 1);
          writer.SetFsBTreeBlock(FsBTreeBlock);
          return (image, writer);
      }

      /// <summary>
      /// Constructs the synthetic image bytes with a valid VSB at block 1,
      /// an empty fs-tree node at block 2, and a minimal omap at block 3.
      /// </summary>
      private static byte[] BuildSyntheticImage()
      {
          var image = new byte[TotalBlocks * BlockSize];

          // ── Block 1: Volume Superblock ────────────────────────────────────
          var vsb = image.AsSpan((int)(1 * BlockSize), (int)BlockSize);
          BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x08, 8), 500);         // o_oid
          BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x10, 8), 1);           // o_xid = 1
          BinaryPrimitives.WriteUInt32LittleEndian(vsb.Slice(0x20, 4), 0x42535041u); // APSB magic
          BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x80, 8), OmapBlock);   // apfs_omap_oid = 3
          BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x88, 8), FsBTreeBlock); // apfs_root_tree_oid = 2
          // apfs_num_files at 0xA8, apfs_num_directories at 0xB0 — start at zero
          ApfsChecksum.WriteChecksum(vsb);

          // ── Block 2: Empty fs-tree leaf ───────────────────────────────────
          var fsNode = new ApfsBTreeNode(BlockSize, oid: FsBTreeBlock, xid: 1,
              objectType: 0x00000002u,    // OBJECT_TYPE_BTREE_NODE | OBJ_PHYSICAL
              objectSubtype: 0x0000000Eu  // OBJECT_TYPE_FSTREE
          );
          var fsBuf = fsNode.Serialize()!;
          fsBuf.CopyTo(image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize));

          // ── Block 3: Volume omap B-tree root (one entry: oid=2, xid=1, paddr=2) ──
          var omapNode = new ApfsBTreeNode(BlockSize, oid: OmapBlock, xid: 1,
              objectType: 0x40000002u,    // OBJECT_TYPE_BTREE_NODE | OBJ_EPHEMERAL (omap flag)
              objectSubtype: 0x0000000Bu, // OBJECT_TYPE_OMAP
              isRoot: true);
          var omapKey = new byte[16];
          BinaryPrimitives.WriteUInt64LittleEndian(omapKey.AsSpan(0, 8), FsBTreeBlock); // oid = 2
          BinaryPrimitives.WriteUInt64LittleEndian(omapKey.AsSpan(8, 8), 1);             // xid = 1
          var omapVal = new byte[16];
          // omap_val: flags(u32)=0, size(u32)=BlockSize, paddr(u64)=FsBTreeBlock
          BinaryPrimitives.WriteUInt32LittleEndian(omapVal.AsSpan(0, 4), 0);
          BinaryPrimitives.WriteUInt32LittleEndian(omapVal.AsSpan(4, 4), BlockSize);
          BinaryPrimitives.WriteUInt64LittleEndian(omapVal.AsSpan(8, 8), FsBTreeBlock);
          omapNode.Insert(omapKey, omapVal);
          var omapBuf = omapNode.Serialize()!;
          omapBuf.CopyTo(image.AsSpan((int)(OmapBlock * BlockSize), (int)BlockSize));

          return image;
      }

      private static void Assert(bool condition, string message)
      {
          if (!condition) throw new Exception(message);
      }
  }
  ```

- [ ] **Step 2: Add `AllocatorFreeBlocks` property to `ApfsWriter`** (used by tests 6, 7, 8)

  In `ApfsWriter.cs`, after `public bool IsWritable => _device.CanWrite;`, add:

  ```csharp
  /// <summary>Returns the allocator's current free block count (for testing).</summary>
  public ulong AllocatorFreeBlocks => _allocator.FreeBlocks;
  ```

- [ ] **Step 3: Build the test project**

  ```bash
  cd native/MacMount.ApfsWriteTest && dotnet build -c Debug 2>&1 | tail -10
  ```

  Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

  ```bash
  git add native/MacMount.ApfsWriteTest/ApfsFileOpsTests.cs native/MacMount.RawDiskEngine/ApfsWriter.cs
  git commit -m "feat: add Phase 4 ApfsFileOpsTests — 10 B-tree write operation tests"
  ```

---

## Task 10: Wire Phase 4 into Program.cs and run all suites

**Files:**
- Modify: `native/MacMount.ApfsWriteTest/Program.cs`

- [ ] **Step 1: Add Phase 3 suite to the runner**

  In `Program.cs`, find:
  ```csharp
  var spaceman = await RunSuite("Phase 1 — Spaceman Parser", ApfsSpacemanTests.RunAllAsync);
  var cow = await RunSuite("Phase 2 — COW Block Writer", ApfsCowTests.RunAllAsync);

  allPassed = spaceman && cow;
  ```

  Replace with:
  ```csharp
  var spaceman  = await RunSuite("Phase 1 — Spaceman Parser",        ApfsSpacemanTests.RunAllAsync);
  var cow       = await RunSuite("Phase 2 — COW Block Writer",       ApfsCowTests.RunAllAsync);
  var fileOps   = await RunSuite("Phase 3/4 — File Operation Writes", ApfsFileOpsTests.RunAllAsync);

  allPassed = spaceman && cow && fileOps;
  ```

- [ ] **Step 2: Run the full test harness**

  ```bash
  npm run apfs:test
  ```

  Expected output (all 27 tests across three suites):
  ```
  APFS Write Test Harness
  ============================================================

  === Phase 1 — Spaceman Parser ===
  ------------------------------------------------------------
    PASS  1. LoadAsync completes on synthetic image
    ...
  Results: 8 passed, 0 failed out of 8 tests.

  === Phase 2 — COW Block Writer ===
  ------------------------------------------------------------
    PASS  1. Checksum of 4096-zero block is deterministic and non-zero
    ...
  Results: 9 passed, 0 failed out of 9 tests.

  === Phase 3/4 — File Operation Writes ===
  ------------------------------------------------------------
    PASS  1. Deserialize round-trips a 2-record node (keys and values preserved)
    PASS  2. CreateFileAsync inserts inode (type 3) and drec (type 9) records
    PASS  3. CreateFileAsync with initialData inserts extent (type 8) record
    PASS  4. CreateDirectoryAsync inserts dir inode (mode 0x41ED) and drec with DT_DIR flag
    PASS  5. WriteFileDataAsync adds extent record and updates inode size
    PASS  6. DeleteEntryAsync removes inode, drec, extent records and frees blocks
    PASS  7. SetFileSizeAsync shrinks file: blocks freed, inode size updated
    PASS  8. Create → Write → Delete roundtrip: fs-tree returns to empty, all blocks freed
    PASS  9. Each write operation increments the fs-tree block XID
    PASS  10. FlushAsync updates VSB apfs_num_files and apfs_num_directories
  Results: 10 passed, 0 failed out of 10 tests.

  ============================================================
  ALL SUITES PASSED
  ```

  If any test fails, read the error message: it will identify the specific assertion and field. Common causes:
  - **Offset mismatch in BuildInodeVal/BuildDrRecVal**: count the byte offsets from the constant table at the top of this plan
  - **Block allocator not starting at block 4**: verify the `BuildWriterAndImage()` helper pre-allocates blocks 0-3
  - **AllocatorFreeBlocks not exposed**: add the property to ApfsWriter (Step 2 of Task 9)
  - **Deserialize static method can't access `_records`**: static methods on the same class can access private instance members

- [ ] **Step 3: Commit**

  ```bash
  git add native/MacMount.ApfsWriteTest/Program.cs
  git commit -m "feat: wire ApfsFileOpsTests into apfs:test harness — Phase 3/4 complete"
  ```

---

## Self-Review

**Spec coverage check:**
- CreateFileAsync ✓ (Task 4)
- CreateDirectoryAsync ✓ (Task 5)
- WriteFileDataAsync ✓ (Task 6)
- DeleteEntryAsync ✓ (Task 7)
- SetFileSizeAsync ✓ (Task 8)
- Deserialize ✓ (Task 1)
- Omap resolution ✓ (Task 2 — lazy, also injectable for tests)
- All 5 stub methods now write real B-tree records ✓
- Phase 4 tests validate every operation independently + roundtrip ✓
- FlushAsync offset bug fixed + test updated ✓

**Placeholder scan:** No "TBD", "TODO", or "add appropriate" patterns. All code blocks are complete and compilable.

**Type consistency:**
- `BuildFsBTreeAsync` → renamed consistently to `ReadFsBTreeAsync` everywhere
- `WriteFsBTreeAsync` takes `(ApfsBTreeNode node, ulong block, CancellationToken ct)` — consistent in Tasks 2, 4, 5, 6, 7, 8
- `AllocatorFreeBlocks` property added in Task 9, used in tests 6, 7, 8 ✓
- `SetFsBTreeBlock(ulong block)` added in Task 2, called in test harness `BuildWriterAndImage()` ✓
