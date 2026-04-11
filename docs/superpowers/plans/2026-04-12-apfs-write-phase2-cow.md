# APFS Write Phase 2 — COW Block Writer + Omap Updates

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a correct, testable APFS block-writing foundation: Fletcher-64 checksum, an in-memory B-tree leaf node builder, and a volume-superblock flush that actually writes to disk with a valid checksum.

**Architecture:** In-place writes (not full COW) — read the existing block, modify fields, recompute checksum, write back to the same physical block. Cheaper than full COW and sufficient for external drive editing. Phase 3 will add catalog B-tree insertion; Phase 2 delivers the infrastructure all writes depend on.

**APFS B-tree offset convention discovered from read code:**
All offsets stored in B-tree node fields (btn_table_space.off, k.off, v.off) are **absolute from the block start**. TOC therefore stores `btn_table_space.off = 0x38 = 56` on a fresh node. The `EnumerateAbsoluteOffsets` heuristic in the read code works because raw offsets are already absolute.

**APFS fs_tree key format (layout B — Apple spec):**
- First 8 bytes: `(type << 60) | obj_id`
  - Type 3 = Inode, Type 8 = FileExtent, Type 9 = DirRecord

**Fletcher-64 algorithm (Apple APFS Reference, Appendix B):**
```
c0 = c1 = 0
for each 32-bit LE word w in block (treating bytes 0–7 as zero):
    c0 = (c0 + w) % 0xFFFFFFFF
    c1 = (c1 + c0) % 0xFFFFFFFF
f0 = 0xFFFFFFFF − ((c0 + c1) % 0xFFFFFFFF)
f1 = 0xFFFFFFFF − ((c0 + f0) % 0xFFFFFFFF)
stored_checksum = (f1 << 32) | f0
```

**Tech Stack:** .NET 9, C#, existing `IRawBlockDevice`, `ApfsBlockAllocator`, `ApfsContainerSummary`, `ApfsVolumePreview`

---

## File Map

| Action  | File |
|---------|------|
| **Create** | `native/MacMount.RawDiskEngine/ApfsChecksum.cs` |
| **Create** | `native/MacMount.RawDiskEngine/ApfsBTreeNode.cs` |
| **Modify** | `native/MacMount.RawDiskEngine/ApfsWriter.cs` — FlushAsync + constructor |
| **Modify** | `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs` — pass extra context to ApfsWriter |
| **Create** | `native/MacMount.ApfsWriteTest/ApfsCowTests.cs` |
| **Modify** | `native/MacMount.ApfsWriteTest/Program.cs` — add `ApfsCowTests.RunAllAsync()` |

---

## Task 1: Fletcher-64 Checksum

**Files:**
- Create: `native/MacMount.RawDiskEngine/ApfsChecksum.cs`

- [ ] **Step 1: Write the failing test**

Add `native/MacMount.ApfsWriteTest/ApfsCowTests.cs`:

```csharp
using System.Buffers.Binary;
using MacMount.RawDiskEngine;

namespace MacMount.ApfsWriteTest;

internal static class ApfsCowTests
{
    public static async Task<bool> RunAllAsync()
    {
        var passed = 0;
        var failed = 0;

        async Task Run(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine($"  PASS  {name}"); passed++; }
            catch (Exception ex) { Console.WriteLine($"  FAIL  {name}: {ex.Message}"); failed++; }
        }

        // Test 1: checksum of known-zero block is not zero (algorithm produces non-trivial output)
        await Run("1. Checksum of 4096-zero block is deterministic and non-zero", async () =>
        {
            await Task.CompletedTask;
            var block = new byte[4096];
            var c1 = ApfsChecksum.Compute(block.AsSpan());
            var c2 = ApfsChecksum.Compute(block.AsSpan());
            Assert(c1 == c2, "not deterministic");
            Assert(c1 != 0, "should not be zero");
        });

        // Test 2: WriteChecksum + Verify round-trip
        await Run("2. WriteChecksum + Verify round-trip on 4096-byte block", async () =>
        {
            await Task.CompletedTask;
            var block = new byte[4096];
            // Write some data at bytes 32+
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8, 8), 42);   // o_oid
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(16, 8), 7);   // o_xid
            ApfsChecksum.WriteChecksum(block.AsSpan());
            Assert(block[0] != 0 || block[1] != 0 || block[2] != 0 || block[3] != 0,
                "checksum bytes should not all be zero");
            Assert(ApfsChecksum.Verify(block.AsSpan()), "verify should pass after WriteChecksum");
        });

        // Test 3: modifying a byte invalidates checksum
        await Run("3. Modifying a data byte invalidates the checksum", async () =>
        {
            await Task.CompletedTask;
            var block = new byte[4096];
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8, 8), 100);
            ApfsChecksum.WriteChecksum(block.AsSpan());
            block[100] ^= 0xFF; // flip a byte
            Assert(!ApfsChecksum.Verify(block.AsSpan()), "checksum should fail after data modification");
        });

        Console.WriteLine($"\nResults: {passed} passed, {failed} failed out of {passed + failed} tests.");
        return failed == 0;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
cd H:\DevWork\Win_Apps\GK_Mac_Opener
dotnet build native/MacMount.ApfsWriteTest/MacMount.ApfsWriteTest.csproj -c Release
```
Expected: build error — `ApfsChecksum` does not exist.

- [ ] **Step 3: Create `ApfsChecksum.cs`**

```csharp
using System.Buffers.Binary;

namespace MacMount.RawDiskEngine;

/// <summary>
/// Fletcher-64 checksum for APFS on-disk objects.
/// Every APFS block has an 8-byte o_cksum field at offset 0.
/// The checksum is computed over the entire block with bytes 0-7 treated as zero.
/// </summary>
internal static class ApfsChecksum
{
    private const uint M = 0xFFFFFFFF;

    /// <summary>
    /// Computes the APFS checksum to store in bytes 0-7.
    /// Caller must zero (or ignore) bytes 0-7 when passing the block in — this method
    /// internally treats them as zero regardless of their actual values.
    /// </summary>
    public static ulong Compute(ReadOnlySpan<byte> block)
    {
        ulong c0 = 0, c1 = 0;
        for (int i = 0; i < block.Length; i += 4)
        {
            // Treat checksum field (bytes 0-7 = first two 32-bit words) as zero
            uint word = i < 8 ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i, 4));
            c0 = (c0 + word) % M;
            c1 = (c1 + c0) % M;
        }
        ulong f0 = M - ((c0 + c1) % M);
        ulong f1 = M - ((c0 + f0) % M);
        return (f1 << 32) | f0;
    }

    /// <summary>Writes the computed checksum into bytes 0-7 of <paramref name="block"/>.</summary>
    public static void WriteChecksum(Span<byte> block)
    {
        var checksum = Compute(block);
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0, 8), checksum);
    }

    /// <summary>Returns true if the checksum stored in bytes 0-7 matches the computed checksum.</summary>
    public static bool Verify(ReadOnlySpan<byte> block) =>
        Compute(block) == BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(0, 8));
}
```

- [ ] **Step 4: Wire the test into Program.cs**

In `native/MacMount.ApfsWriteTest/Program.cs`, add `ApfsCowTests.RunAllAsync()` call alongside `ApfsSpacemanTests.RunAllAsync()`.

- [ ] **Step 5: Run and verify tests pass**

```
npm run apfs:test
```
Expected: 3 new PASS lines for `ApfsCowTests`.

- [ ] **Step 6: Commit**

```
git add native/MacMount.RawDiskEngine/ApfsChecksum.cs native/MacMount.ApfsWriteTest/ApfsCowTests.cs native/MacMount.ApfsWriteTest/Program.cs
git commit -m "feat: APFS Fletcher-64 checksum — compute, write, verify"
```

---

## Task 2: APFS B-tree Leaf Node Builder

**Files:**
- Create: `native/MacMount.RawDiskEngine/ApfsBTreeNode.cs`
- Modify: `native/MacMount.ApfsWriteTest/ApfsCowTests.cs`

**On-disk layout produced by `ApfsBTreeNode.Serialize()`:**

```
0x00–0x07  o_cksum        (written by ApfsChecksum.WriteChecksum)
0x08–0x0F  o_oid
0x10–0x17  o_xid
0x18–0x1B  o_type
0x1C–0x1F  o_subtype
0x20–0x21  btn_flags      (0x0002 = BTN_LEAF)
0x22–0x23  btn_level      (0 = leaf)
0x24–0x27  btn_nkeys
0x28–0x29  btn_table_space.off  = 0x38 (absolute TOC start)
0x2A–0x2B  btn_table_space.len = nkeys * 8
0x2C–0x37  (other nloc fields, zeroed)
0x38+      data area:
  [TOC]    nkeys × 8-byte kvloc_t entries:
             k.off (u16 absolute), k.len (u16), v.off (u16 absolute), v.len (u16)
  [Keys]   records' keys (in insertion order = sorted by key bytes)
  [Values] records' values (packed from block end backward)
[block_end − 40]  btree_info (root nodes only, zeroed except bt_node_size)
```

- [ ] **Step 1: Add B-tree node tests to `ApfsCowTests.cs`**

Append to `ApfsCowTests.RunAllAsync()` (before the Results line):

```csharp
// --- B-tree node builder tests ---

// Test 4: empty node serializes to a valid block with correct checksum
await Run("4. Empty ApfsBTreeNode serializes with valid checksum", async () =>
{
    await Task.CompletedTask;
    var node = new ApfsBTreeNode(4096, oid: 10, xid: 1,
        objectType: 0x00000002u,   // OBJECT_TYPE_BTREE_NODE | OBJ_PHYSICAL
        objectSubtype: 0x0000000Bu  // OBJECT_TYPE_OMAP
    );
    var buf = node.Serialize();
    Assert(buf is not null, "Serialize returned null");
    Assert(buf!.Length == 4096, $"expected 4096 bytes, got {buf.Length}");
    Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid on empty node");
});

// Test 5: single-record node has correct nkeys and valid checksum
await Run("5. Single-record node: nkeys=1, checksum valid", async () =>
{
    await Task.CompletedTask;
    var node = new ApfsBTreeNode(4096, oid: 11, xid: 2, objectType: 0x00000002u, objectSubtype: 0x0Bu);
    // omap key: OID=42, XID=1 (16 bytes)
    var key = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(0, 8), 42);
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), 1);
    // omap val: flags=0, size=4096, paddr=100 (16 bytes)
    var val = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(0, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(4, 4), 4096);
    BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(8, 8), 100);
    node.Insert(key, val);
    var buf = node.Serialize()!;
    // nkeys at 0x24
    var nkeys = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0x24, 4));
    Assert(nkeys == 1, $"expected nkeys=1, got {nkeys}");
    Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid");
});

// Test 6: insertion maintains sorted key order (two records)
await Run("6. Insert two records: keys are sorted ascending in TOC", async () =>
{
    await Task.CompletedTask;
    var node = new ApfsBTreeNode(4096, oid: 12, xid: 3, objectType: 0x00000002u, objectSubtype: 0x0Bu);
    // Insert high OID first, then low OID — expect sorted output
    var keyHigh = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(keyHigh.AsSpan(0, 8), 200); // OID 200
    BinaryPrimitives.WriteUInt64LittleEndian(keyHigh.AsSpan(8, 8), 1);
    var keyLow = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(keyLow.AsSpan(0, 8), 50);  // OID 50
    BinaryPrimitives.WriteUInt64LittleEndian(keyLow.AsSpan(8, 8), 1);
    var dummyVal = new byte[16];
    node.Insert(keyHigh, dummyVal);
    node.Insert(keyLow, dummyVal);
    // Records should now be sorted: OID 50 first, OID 200 second
    var recs = node.Records;
    Assert(recs.Count == 2, $"expected 2 records, got {recs.Count}");
    var oid0 = BinaryPrimitives.ReadUInt64LittleEndian(recs[0].Key.AsSpan(0, 8));
    var oid1 = BinaryPrimitives.ReadUInt64LittleEndian(recs[1].Key.AsSpan(0, 8));
    Assert(oid0 == 50, $"expected oid0=50, got {oid0}");
    Assert(oid1 == 200, $"expected oid1=200, got {oid1}");
    var buf = node.Serialize()!;
    Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid after two inserts");
});

// Test 7: delete removes the correct record
await Run("7. Delete removes matching record; remaining record intact", async () =>
{
    await Task.CompletedTask;
    var node = new ApfsBTreeNode(4096, oid: 13, xid: 4, objectType: 0x00000002u, objectSubtype: 0x0Bu);
    var key1 = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(key1.AsSpan(0, 8), 10);
    var key2 = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(key2.AsSpan(0, 8), 20);
    var v = new byte[16];
    node.Insert(key1, v);
    node.Insert(key2, v);
    var deleted = node.Delete(key1);
    Assert(deleted, "Delete returned false");
    Assert(node.RecordCount == 1, $"expected 1 record, got {node.RecordCount}");
    var remaining = BinaryPrimitives.ReadUInt64LittleEndian(node.Records[0].Key.AsSpan(0, 8));
    Assert(remaining == 20, $"expected remaining OID=20, got {remaining}");
    var buf = node.Serialize()!;
    Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid after delete");
});

// Test 8: TOC and key/value positions are readable by the heuristic decoder
await Run("8. Serialized omap leaf: heuristic reader extracts correct OID and paddr", async () =>
{
    await Task.CompletedTask;
    const ulong oid = 777, xid = 5, paddr = 333;
    var node = new ApfsBTreeNode(4096, oid: 99, xid: xid, objectType: 0x00000002u, objectSubtype: 0x0Bu);
    var key = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(0, 8), oid);
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), xid);
    var val = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(0, 4), 0);        // flags
    BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(4, 4), 4096);     // size
    BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(8, 8), paddr);    // paddr
    node.Insert(key, val);
    var buf = node.Serialize()!;
    Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid");
    // Read back: find k.off for first TOC entry at 0x38
    var kOff = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x38, 2)); // k.off (absolute)
    var kLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x3A, 2));
    var vOff = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x3C, 2)); // v.off (absolute)
    var vLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x3E, 2));
    Assert(kLen == 16, $"expected kLen=16, got {kLen}");
    Assert(vLen == 16, $"expected vLen=16, got {vLen}");
    var readOid = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(kOff, 8));
    var readPaddr = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(vOff + 8, 8));
    Assert(readOid == oid, $"expected OID={oid}, got {readOid}");
    Assert(readPaddr == paddr, $"expected paddr={paddr}, got {readPaddr}");
});
```

- [ ] **Step 2: Run test to verify failures**

```
npm run apfs:test
```
Expected: FAIL on tests 4–8 — `ApfsBTreeNode` does not exist.

- [ ] **Step 3: Create `ApfsBTreeNode.cs`**

```csharp
using System.Buffers.Binary;

namespace MacMount.RawDiskEngine;

/// <summary>
/// Builds and serializes an APFS B-tree leaf node with the correct on-disk layout.
/// All offsets stored in the block (btn_table_space.off, k.off, v.off) are absolute from block start.
///
/// Layout:
///   0x00–0x1F  obj_hdr (32 bytes): cksum, oid, xid, type, subtype
///   0x20–0x37  btn header (24 bytes): flags, level, nkeys, table_space, ...
///   0x38+      data area:
///     TOC at 0x38 (btn_table_space.off = 0x38): nkeys × kvloc_t (8 bytes each)
///     Keys: after TOC, packed forward
///     Values: from block end backward (before btree_info on root nodes)
///   [block_end−40]  btree_info (root nodes only)
/// </summary>
internal sealed class ApfsBTreeNode
{
    private const int HeaderSize = 0x38; // 56 bytes: obj_hdr(32) + btn_fields(24)
    private const int TocEntrySize = 8;  // kvloc_t: k.off(u16)+k.len(u16)+v.off(u16)+v.len(u16)
    private const int BTreeInfoSize = 40;

    private readonly uint _blockSize;
    private readonly bool _isRoot;
    private readonly List<(byte[] Key, byte[] Value)> _records = new();

    public ulong ObjectId { get; set; }
    public ulong TransactionId { get; set; }
    public uint ObjectType { get; set; }
    public uint ObjectSubtype { get; set; }

    public ApfsBTreeNode(uint blockSize, ulong objectId, ulong transactionId,
        uint objectType, uint objectSubtype, bool isRoot = false)
    {
        _blockSize = blockSize;
        _isRoot = isRoot;
        ObjectId = objectId;
        TransactionId = transactionId;
        ObjectType = objectType;
        ObjectSubtype = objectSubtype;
    }

    public IReadOnlyList<(byte[] Key, byte[] Value)> Records => _records;
    public int RecordCount => _records.Count;

    /// <summary>Inserts a record in sorted key order (ascending, lexicographic).</summary>
    public void Insert(byte[] key, byte[] value)
    {
        int i = 0;
        while (i < _records.Count && CompareKeys(_records[i].Key, key) < 0) i++;
        _records.Insert(i, (key, value));
    }

    /// <summary>Removes the record whose key matches exactly. Returns true if found.</summary>
    public bool Delete(byte[] key)
    {
        for (int i = 0; i < _records.Count; i++)
        {
            if (CompareKeys(_records[i].Key, key) == 0)
            {
                _records.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Returns true if the given key+value can be inserted without overflowing the block.</summary>
    public bool WouldFit(byte[] key, byte[] value)
    {
        int btreeInfoSize = _isRoot ? BTreeInfoSize : 0;
        int dataAreaSize = (int)_blockSize - HeaderSize - btreeInfoSize;
        int currentUsed = _records.Count * TocEntrySize
            + _records.Sum(r => r.Key.Length + r.Value.Length);
        return currentUsed + TocEntrySize + key.Length + value.Length <= dataAreaSize;
    }

    /// <summary>
    /// Serializes the node to a new block-sized buffer with Fletcher-64 checksum.
    /// Returns null if records do not fit in one block.
    /// </summary>
    public byte[]? Serialize()
    {
        int btreeInfoSize = _isRoot ? BTreeInfoSize : 0;
        int dataAreaSize = (int)_blockSize - HeaderSize - btreeInfoSize;
        int tocSize = _records.Count * TocEntrySize;
        int keysSize = _records.Sum(r => r.Key.Length);
        int valsSize = _records.Sum(r => r.Value.Length);
        if (tocSize + keysSize + valsSize > dataAreaSize) return null;

        var buf = new byte[_blockSize];

        // --- Object header ---
        // o_cksum at 0x00 — written last
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x08, 8), ObjectId);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x10, 8), TransactionId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x18, 4), ObjectType);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x1C, 4), ObjectSubtype);

        // --- btn header ---
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x20, 2), 0x0002); // BTN_LEAF
        // btn_level = 0 (already zero)
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x24, 4), (uint)_records.Count);
        // btn_table_space.off = absolute position of TOC = HeaderSize = 0x38
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x28, 2), (ushort)HeaderSize);
        // btn_table_space.len = allocated TOC size (may be larger than used, but keep it exact)
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x2A, 2), (ushort)tocSize);

        // --- Data area ---
        int tocBase = HeaderSize;           // 0x38
        int keyBase = tocBase + tocSize;    // keys start after TOC
        int valEnd = (int)_blockSize - btreeInfoSize; // values packed from here backward

        int keyPos = 0; // bytes written into keys area
        int valPos = 0; // bytes written from valEnd backward

        for (int i = 0; i < _records.Count; i++)
        {
            var (key, value) = _records[i];
            int absKeyOff = keyBase + keyPos;
            valPos += value.Length;
            int absValOff = valEnd - valPos;

            // kvloc_t at tocBase + i*8
            int tocOff = tocBase + i * TocEntrySize;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tocOff + 0, 2), (ushort)absKeyOff);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tocOff + 2, 2), (ushort)key.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tocOff + 4, 2), (ushort)absValOff);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tocOff + 6, 2), (ushort)value.Length);

            key.CopyTo(buf.AsSpan(absKeyOff, key.Length));
            value.CopyTo(buf.AsSpan(absValOff, value.Length));
            keyPos += key.Length;
        }

        // --- btree_info (root only) ---
        if (_isRoot)
        {
            int infoBase = (int)_blockSize - BTreeInfoSize;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(infoBase + 4, 4), _blockSize);
        }

        ApfsChecksum.WriteChecksum(buf);
        return buf;
    }

    private static int CompareKeys(byte[] a, byte[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }
}
```

- [ ] **Step 4: Run tests and verify all pass**

```
npm run apfs:test
```
Expected: Tests 4–8 pass.

- [ ] **Step 5: Commit**

```
git add native/MacMount.RawDiskEngine/ApfsBTreeNode.cs native/MacMount.ApfsWriteTest/ApfsCowTests.cs
git commit -m "feat: APFS B-tree leaf node builder with sorted insertion and checksum"
```

---

## Task 3: Volume Superblock Flush

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsWriter.cs`
- Modify: `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs`

Wire `FlushAsync` to read the volume superblock, increment the XID, update `apfs_num_files` (if needed), recompute checksum, and write it back.

Volume superblock fields to update (all offsets relative to block start, APFS object header at 0):
- `apfs_num_files` at offset **0x90** (u64 LE): number of files
- `apfs_num_directories` at offset **0x98** (u64 LE): number of directories
- `apfs_num_symlinks` at offset **0xA0** (u64 LE)
- XID at offset **0x10** (u64 LE): transaction ID in the object header

- [ ] **Step 1: Update `ApfsWriter` constructor to accept volume superblock block**

In `ApfsWriter.cs`:
```csharp
// New fields:
private readonly ulong? _volumeSuperblockBlock;
private ulong _currentXid;
private long _pendingFileCountDelta;
private long _pendingDirCountDelta;

// New constructor signature:
public ApfsWriter(
    IRawBlockDevice device,
    ApfsBlockAllocator allocator,
    uint blockSize,
    long partitionOffset,
    ulong volumeOid,
    ulong? volumeSuperblockBlock,
    ulong currentXid)
{
    _device = device;
    _allocator = allocator;
    _blockSize = blockSize;
    _partitionOffset = partitionOffset;
    _volumeOid = volumeOid;
    _volumeSuperblockBlock = volumeSuperblockBlock;
    _currentXid = currentXid;
}
```

Update `CreateFileAsync` to increment `_pendingFileCountDelta`.
Update `CreateDirectoryAsync` to increment `_pendingDirCountDelta`.
Update `DeleteEntryAsync` to decrement the appropriate counter.

Implement `FlushAsync`:
```csharp
public async Task FlushAsync(CancellationToken ct = default)
{
    if (!IsWritable || _volumeSuperblockBlock is null) return;
    if (_pendingFileCountDelta == 0 && _pendingDirCountDelta == 0) return;

    var offset = _partitionOffset + (long)(_volumeSuperblockBlock.Value * _blockSize);
    var buf = new byte[_blockSize];
    var read = await _device.ReadAsync(offset, buf, buf.Length, ct).ConfigureAwait(false);
    if (read < (int)_blockSize) return;

    // Increment XID
    _currentXid++;
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x10, 8), _currentXid);

    // Update file/dir counts
    if (_pendingFileCountDelta != 0)
    {
        var cur = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0x90, 8));
        var next = (ulong)Math.Max(0, cur + _pendingFileCountDelta);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x90, 8), next);
        _pendingFileCountDelta = 0;
    }
    if (_pendingDirCountDelta != 0)
    {
        var cur = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0x98, 8));
        var next = (ulong)Math.Max(0, cur + _pendingDirCountDelta);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x98, 8), next);
        _pendingDirCountDelta = 0;
    }

    ApfsChecksum.WriteChecksum(buf.AsSpan());
    await _device.WriteAsync(offset, buf, buf.Length, ct).ConfigureAwait(false);
}
```

- [ ] **Step 2: Update `ApfsRawFileSystemProvider` constructor to pass the volume superblock block**

In the `_writer = new ApfsWriter(...)` call, add:
- `volumeSuperblockBlock`: get from `summary.ResolvedVolumePointers.FirstOrDefault()?.PhysicalBlockNumber`
- `currentXid`: get from `summary.TransactionId`

```csharp
var volumePtr = summary.ResolvedVolumePointers.FirstOrDefault();
_writer = new ApfsWriter(
    device, _allocator, _blockSize, _partitionOffsetBytes,
    volumeOid,
    volumePtr?.PhysicalBlockNumber,
    summary.TransactionId);
```

- [ ] **Step 3: Add flush test to `ApfsCowTests.cs`**

```csharp
// Test 9: ApfsWriter.FlushAsync updates volume superblock checksum
await Run("9. FlushAsync rewrites volume superblock with valid checksum", async () =>
{
    // Build a synthetic volume superblock block with APSB magic
    var vsb = new byte[4096];
    // magic at 0x20 = APSB = 0x42535041
    BinaryPrimitives.WriteUInt32LittleEndian(vsb.AsSpan(0x20, 4), 0x42535041u);
    BinaryPrimitives.WriteUInt64LittleEndian(vsb.AsSpan(0x08, 8), 500);  // oid
    BinaryPrimitives.WriteUInt64LittleEndian(vsb.AsSpan(0x10, 8), 10);   // xid
    BinaryPrimitives.WriteUInt64LittleEndian(vsb.AsSpan(0x90, 8), 3);    // num_files = 3
    ApfsChecksum.WriteChecksum(vsb.AsSpan());

    // Build a 2-block device: block 0 = zeros, block 1 = volume superblock
    var deviceBytes = new byte[4096 * 2];
    vsb.CopyTo(deviceBytes.AsSpan(4096, 4096));
    var device = new WritableMemoryRawBlockDevice(deviceBytes);

    var spaceman = await ApfsSpacemanReader.LoadAsync(
        new MemoryRawBlockDevice(new byte[32 * 4096]), 4, 4096);
    var allocator = new ApfsBlockAllocator(spaceman);
    await allocator.LoadBitmapAsync();

    var writer = new ApfsWriter(
        device, allocator, 4096, partitionOffset: 0,
        volumeOid: 500,
        volumeSuperblockBlock: 1,
        currentXid: 10);

    // Simulate creating 2 files
    writer.TrackFileCreated();
    writer.TrackFileCreated();

    await writer.FlushAsync();

    // Verify: block 1 should have incremented xid, updated num_files, valid checksum
    var after = deviceBytes.AsSpan(4096, 4096);
    var newXid = BinaryPrimitives.ReadUInt64LittleEndian(after.Slice(0x10, 8));
    var newFileCount = BinaryPrimitives.ReadUInt64LittleEndian(after.Slice(0x90, 8));
    Assert(newXid == 11, $"expected xid=11, got {newXid}");
    Assert(newFileCount == 5, $"expected num_files=5, got {newFileCount}");
    Assert(ApfsChecksum.Verify(after), "checksum invalid after FlushAsync");
});
```

Add `WritableMemoryRawBlockDevice` to the test file:
```csharp
internal sealed class WritableMemoryRawBlockDevice : IRawBlockDevice
{
    private readonly byte[] _data;
    public WritableMemoryRawBlockDevice(byte[] data) => _data = data;
    public string DevicePath => "memory://writable-test";
    public long Length => _data.Length;
    public bool CanWrite => true;
    public ValueTask<int> ReadAsync(long offset, byte[] buffer, int count, CancellationToken ct = default)
    {
        if (offset < 0 || offset >= _data.Length) return ValueTask.FromResult(0);
        var available = (int)Math.Min(count, _data.Length - offset);
        Buffer.BlockCopy(_data, (int)offset, buffer, 0, available);
        return ValueTask.FromResult(available);
    }
    public ValueTask WriteAsync(long offset, byte[] buffer, int count, CancellationToken ct = default)
    {
        Buffer.BlockCopy(buffer, 0, _data, (int)offset, count);
        return ValueTask.CompletedTask;
    }
    public void Dispose() { }
}
```

Also add `writer.TrackFileCreated()` as a public method on `ApfsWriter`:
```csharp
public void TrackFileCreated() => Interlocked.Increment(ref _pendingFileCountDelta);
public void TrackDirCreated() => Interlocked.Increment(ref _pendingDirCountDelta);
```

- [ ] **Step 4: Check IRawBlockDevice for WriteAsync signature**

Read `native/MacMount.RawDiskEngine/Interfaces.cs` to confirm `WriteAsync` signature before implementing.

- [ ] **Step 5: Build and run — all 9 tests pass**

```
npm run apfs:test
```
Expected: 9 PASS (3 checksum + 5 B-tree node + 1 flush).

- [ ] **Step 6: Commit**

```
git add native/MacMount.RawDiskEngine/ApfsWriter.cs native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs native/MacMount.ApfsWriteTest/ApfsCowTests.cs
git commit -m "feat: ApfsWriter.FlushAsync writes volume superblock with updated XID and checksum"
```

---

## Final

After all tasks, run the full suite:
```
npm run apfs:test
npm run hfs:test   # sanity-check no regressions
```

Expected: 9 Phase-2 tests + 8 Phase-1 tests = 17 tests passing, HFS+ tests unchanged.
