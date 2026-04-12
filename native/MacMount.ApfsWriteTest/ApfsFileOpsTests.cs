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
    private const uint BlockSize   = 4096;
    private const int  TotalBlocks = 128; // 512 KB — LoadBitmapAsync reserves blocks 0-63; data blocks are 64-127

    // Well-known blocks in the synthetic image
    private const ulong FsBTreeBlock = 2;
    private const ulong OmapBlock    = 3;

    // APFS reserves: 1=invalid, 2=root-dir, 3=private-dir
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

        // ── Test 1: ApfsBTreeNode.Deserialize round-trip ──────────────────────────

        await Run("1. Deserialize round-trips a 2-record node (keys and values preserved)", async () =>
        {
            await Task.CompletedTask;
            var node = new ApfsBTreeNode(BlockSize, oid: 42, xid: 7,
                objectType: 0x00000002u, objectSubtype: 0x0Du);
            var k1 = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(k1, (3UL << 60) | 100u);
            var v1 = new byte[92];
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
            Assert(firstKey == ((3UL << 60) | 100u), $"first key mismatch: {firstKey}");
            Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid");
        });

        // ── Test 2: CreateFileAsync — inode + drec inserted ──────────────────────

        await Run("2. CreateFileAsync inserts inode (type 3) and drec (type 9) records", async () =>
        {
            var (image, writer) = BuildWriterAndImage();
            await writer.CreateFileAsync(RootCnid, "hello.txt");

            var fsBuf = GetFsBlock(image);
            var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
            Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after CreateFileAsync");
            Assert(node.RecordCount == 2, $"expected 2 records (inode+drec), got {node.RecordCount}");

            var inodeKeyType = BinaryPrimitives.ReadUInt64LittleEndian(node.Records[0].Key.AsSpan(0, 8)) >> 60;
            Assert(inodeKeyType == 3, $"first record should be inode (type 3), got type {inodeKeyType}");
        });

        // ── Test 3: CreateFileAsync with data — extent record present ─────────────

        await Run("3. CreateFileAsync with initialData inserts extent (type 8) record and writes data", async () =>
        {
            var (image, writer) = BuildWriterAndImage();
            var data = Encoding.UTF8.GetBytes("hello from APFS!");
            await writer.CreateFileAsync(RootCnid, "data.txt", data);

            var fsBuf = GetFsBlock(image);
            var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
            Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid");
            Assert(node.RecordCount == 3, $"expected 3 records (inode+drec+extent), got {node.RecordCount}");

            var extentRec = node.Records.FirstOrDefault(r =>
                r.Key.Length >= 8 && (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) >> 60) == 8);
            Assert(extentRec != default, "no extent record found");

            var physBlock = BinaryPrimitives.ReadUInt64LittleEndian(extentRec.Value.AsSpan(0x08, 8));
            Assert(physBlock >= 64 && physBlock < TotalBlocks, $"physBlock {physBlock} out of free range (expected 64-127)");
            var writtenSlice = image.AsSpan((int)(physBlock * BlockSize), data.Length);
            Assert(writtenSlice.SequenceEqual(data.AsSpan()), "data bytes not written to device");
        });

        // ── Test 4: CreateDirectoryAsync — dir inode + drec ──────────────────────

        await Run("4. CreateDirectoryAsync inserts dir inode (mode 0x41ED) and drec with DT_DIR flag", async () =>
        {
            var (image, writer) = BuildWriterAndImage();
            await writer.CreateDirectoryAsync(RootCnid, "subdir");

            var fsBuf = GetFsBlock(image);
            var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
            Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after CreateDirectoryAsync");
            Assert(node.RecordCount == 2, $"expected 2 records, got {node.RecordCount}");

            var inodeRec = node.Records.First(r =>
                (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) >> 60) == 3);
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeRec.Value.AsSpan(0x50, 2));
            Assert(mode == 0x41ED, $"expected directory mode 0x41ED, got 0x{mode:X4}");

            var drecRec = node.Records.First(r =>
                (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) >> 60) == 9);
            var dtFlags = BinaryPrimitives.ReadUInt16LittleEndian(drecRec.Value.AsSpan(0x10, 2));
            Assert(dtFlags == 0x0004, $"expected DT_DIR flags 0x0004, got 0x{dtFlags:X4}");
        });

        // ── Test 5: WriteFileDataAsync — extent added, inode size updated ─────────

        await Run("5. WriteFileDataAsync adds extent record and updates inode size", async () =>
        {
            var (image, writer) = BuildWriterAndImage();
            var cnid    = await writer.CreateFileAsync(RootCnid, "write_me.txt"); // size=0
            var payload = Encoding.UTF8.GetBytes("APFS Phase 3 write data");
            await writer.WriteFileDataAsync(cnid, 0, payload, payload.Length);

            var fsBuf = GetFsBlock(image);
            var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
            Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after WriteFileDataAsync");
            Assert(node.RecordCount == 3, $"expected 3 records (inode+drec+extent), got {node.RecordCount}");

            var inodeKey = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(inodeKey, (3UL << 60) | cnid);
            var inodeRec = node.Records.First(r => r.Key.SequenceEqual(inodeKey));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(inodeRec.Value.AsSpan(0x54, 8));
            Assert(size == (ulong)payload.Length, $"expected inode size={payload.Length}, got {size}");
        });

        // ── Test 6: DeleteEntryAsync — all records removed, blocks freed ──────────

        await Run("6. DeleteEntryAsync removes inode, drec, extent records and frees blocks", async () =>
        {
            var (image, writer) = BuildWriterAndImage();
            var freeBlocksBefore = writer.AllocatorFreeBlocks;
            var data = new byte[4096]; // exactly 1 block
            await writer.CreateFileAsync(RootCnid, "to_delete.txt", data);
            var freeAfterCreate = writer.AllocatorFreeBlocks;
            Assert(freeAfterCreate < freeBlocksBefore, "no block was allocated for initial data");

            await writer.DeleteEntryAsync(RootCnid, "to_delete.txt");

            var fsBuf = GetFsBlock(image);
            var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
            Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after DeleteEntryAsync");
            Assert(node.RecordCount == 0, $"expected 0 records after delete, got {node.RecordCount}");
            Assert(writer.AllocatorFreeBlocks == freeBlocksBefore, "blocks not freed after delete");
        });

        // ── Test 7: SetFileSizeAsync — shrink frees blocks ───────────────────────

        await Run("7. SetFileSizeAsync shrinks file: blocks freed, inode size updated", async () =>
        {
            var (image, writer) = BuildWriterAndImage();
            var data = new byte[8192]; // 2 blocks
            var cnid = await writer.CreateFileAsync(RootCnid, "shrink_me.txt", data);
            var freeAfterCreate = writer.AllocatorFreeBlocks;

            await writer.SetFileSizeAsync(cnid, 0); // shrink to zero

            var fsBuf = GetFsBlock(image);
            var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
            Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after SetFileSizeAsync");

            var inodeRec = node.Records.First(r =>
                (BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(0, 8)) >> 60) == 3);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(inodeRec.Value.AsSpan(0x54, 8));
            Assert(size == 0, $"expected inode size=0 after shrink, got {size}");

            // Extents removed — only inode + drec remain
            Assert(node.RecordCount == 2, $"expected 2 records after shrink, got {node.RecordCount}");
            Assert(writer.AllocatorFreeBlocks > freeAfterCreate, "blocks not freed after shrink");
        });

        // ── Test 8: Create + Write + Delete roundtrip ─────────────────────────────

        await Run("8. Create → Write → Delete roundtrip: fs-tree empty, no block leak", async () =>
        {
            var (image, writer) = BuildWriterAndImage();
            var freeStart = writer.AllocatorFreeBlocks;

            var cnid = await writer.CreateFileAsync(RootCnid, "roundtrip.txt");
            await writer.WriteFileDataAsync(cnid, 0, new byte[4096], 4096);
            await writer.DeleteEntryAsync(RootCnid, "roundtrip.txt");

            var fsBuf = GetFsBlock(image);
            var node  = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)!;
            Assert(node.RecordCount == 0, $"expected empty fs-tree, got {node.RecordCount} records");
            Assert(writer.AllocatorFreeBlocks == freeStart, $"block leak: before={freeStart} after={writer.AllocatorFreeBlocks}");
            Assert(ApfsChecksum.Verify(fsBuf.AsSpan()), "checksum invalid after roundtrip");
        });

        // ── Test 9: fs-tree XID increments on each write ──────────────────────────

        await Run("9. Each write operation increments the fs-tree node XID", async () =>
        {
            var (image, writer) = BuildWriterAndImage();
            var xidBefore = BinaryPrimitives.ReadUInt64LittleEndian(
                image.AsSpan((int)(FsBTreeBlock * BlockSize) + 0x10, 8));

            await writer.CreateFileAsync(RootCnid, "xid_check.txt");

            var xidAfter = BinaryPrimitives.ReadUInt64LittleEndian(
                image.AsSpan((int)(FsBTreeBlock * BlockSize) + 0x10, 8));
            Assert(xidAfter > xidBefore, $"XID did not increment: before={xidBefore} after={xidAfter}");
        });

        // ── Test 10: FlushAsync updates VSB file/dir counts at spec offsets ───────

        await Run("10. FlushAsync updates apfs_num_files (0xA8) and apfs_num_directories (0xB0) in VSB", async () =>
        {
            var (image, writer) = BuildWriterAndImage();
            await writer.CreateFileAsync(RootCnid, "flush_a.txt");
            await writer.CreateFileAsync(RootCnid, "flush_b.txt");
            await writer.CreateDirectoryAsync(RootCnid, "flush_dir");
            await writer.FlushAsync();

            var vsb      = image.AsSpan((int)(1 * BlockSize), (int)BlockSize);
            var numFiles = BinaryPrimitives.ReadUInt64LittleEndian(vsb.Slice(0xA8, 8));
            var numDirs  = BinaryPrimitives.ReadUInt64LittleEndian(vsb.Slice(0xB0, 8));
            Assert(numFiles == 2, $"expected 2 files, got {numFiles}");
            Assert(numDirs  == 1, $"expected 1 dir, got {numDirs}");
            Assert(ApfsChecksum.Verify(vsb), "VSB checksum invalid after FlushAsync");
        });

        Console.WriteLine($"\nResults: {passed} passed, {failed} failed out of {passed + failed} tests.");
        return failed == 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Returns a fresh copy of the fs-tree block from the image.</summary>
    private static byte[] GetFsBlock(byte[] image) =>
        image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize).ToArray();

    /// <summary>
    /// Builds a 12-block in-memory APFS image and a matching writable ApfsWriter.
    /// fs-tree block is injected directly (bypasses omap resolution).
    /// Blocks 0-3 are pre-allocated (metadata area); data blocks start at 4.
    /// </summary>
    private static (byte[] image, ApfsWriter writer) BuildWriterAndImage()
    {
        var image  = BuildSyntheticImage();
        var device = new WritableMemoryRawBlockDevice(image);

        var allocator = new ApfsBlockAllocator(device, BlockSize, TotalBlocks, partitionOffset: 0);
        allocator.LoadBitmapAsync().GetAwaiter().GetResult();
        // LoadBitmapAsync reserves blocks 0-63 as metadata (min(64, 128) = 64).
        // Blocks 64-127 are free for data writes — no manual pre-allocation needed.

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
    /// Constructs the synthetic image: VSB at block 1, empty fs-tree leaf at block 2,
    /// omap B-tree root at block 3 (maps oid=2 → block 2).
    /// </summary>
    private static byte[] BuildSyntheticImage()
    {
        var image = new byte[TotalBlocks * BlockSize];

        // ── Block 1: Volume Superblock ────────────────────────────────────────────
        var vsb = image.AsSpan((int)(1 * BlockSize), (int)BlockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x08, 8), 500);          // o_oid
        BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x10, 8), 1);            // o_xid = 1
        BinaryPrimitives.WriteUInt32LittleEndian(vsb.Slice(0x20, 4), 0x42535041u);  // APSB magic
        BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x80, 8), OmapBlock);    // apfs_omap_oid = 3
        BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x88, 8), FsBTreeBlock); // apfs_root_tree_oid = 2
        // apfs_num_files at 0xA8, apfs_num_directories at 0xB0 — start at 0
        ApfsChecksum.WriteChecksum(vsb);

        // ── Block 2: Empty fs-tree leaf ───────────────────────────────────────────
        var fsNode = new ApfsBTreeNode(BlockSize, oid: FsBTreeBlock, xid: 1,
            objectType: 0x00000002u,    // OBJECT_TYPE_BTREE_NODE | OBJ_PHYSICAL
            objectSubtype: 0x0000000Eu  // OBJECT_TYPE_FSTREE
        );
        var fsBuf = fsNode.Serialize()!;
        fsBuf.CopyTo(image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize));

        // ── Block 3: Volume omap B-tree root (entry: oid=2, xid=1, paddr=2) ──────
        var omapNode = new ApfsBTreeNode(BlockSize, oid: OmapBlock, xid: 1,
            objectType: 0x40000002u,    // OBJECT_TYPE_BTREE_NODE | OBJ_EPHEMERAL
            objectSubtype: 0x0000000Bu, // OBJECT_TYPE_OMAP
            isRoot: true);
        var omapKey = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(omapKey.AsSpan(0, 8), FsBTreeBlock); // oid = 2
        BinaryPrimitives.WriteUInt64LittleEndian(omapKey.AsSpan(8, 8), 1);             // xid = 1
        var omapVal = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(omapVal.AsSpan(0, 4), 0);             // flags
        BinaryPrimitives.WriteUInt32LittleEndian(omapVal.AsSpan(4, 4), BlockSize);     // size
        BinaryPrimitives.WriteUInt64LittleEndian(omapVal.AsSpan(8, 8), FsBTreeBlock);  // paddr = 2
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
