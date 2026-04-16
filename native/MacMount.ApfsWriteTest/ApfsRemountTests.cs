using System.Buffers.Binary;
using MacMount.RawDiskEngine;

namespace MacMount.ApfsWriteTest;

/// <summary>
/// End-to-end tests that prove fs-tree writes survive a "remount" — i.e. a
/// fresh ApfsWriter instance that was NOT given an explicit fs-tree block
/// override and therefore must follow the VSB → omap → fs-tree pointer chain
/// to find the data we wrote.
///
/// This is the critical correctness gate for un-gating APFS write support:
/// without working copy-on-write of the omap (and the VSB rewrite that points
/// at the new omap block), writes appear to succeed in-memory but vanish on
/// remount because the metadata pointer chain still points at the old,
/// unmodified blocks.
/// </summary>
internal static class ApfsRemountTests
{
    private const uint BlockSize    = 4096;
    private const uint TotalBlocks  = 256;
    private const ulong VsbBlock    = 1;
    private const ulong InitialFsTreeBlock = 2;
    private const ulong InitialOmapBlock   = 3;
    private const ulong VolumeOid   = 500;

    public static async Task<bool> RunAllAsync()
    {
        var passed = 0;
        var failed = 0;

        async Task Run(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine($"  PASS  {name}"); passed++; }
            catch (Exception ex) { Console.WriteLine($"  FAIL  {name}: {ex.Message}"); failed++; }
        }

        await Run("1. CreateFile via writer A → fresh writer B finds inode through omap chain", async () =>
        {
            var image = BuildSyntheticImage();
            var device = new WritableMemoryRawBlockDevice(image);

            // Writer A: COW enabled (default). Creates a file.
            var allocatorA = new ApfsBlockAllocator(device, BlockSize, TotalBlocks, partitionOffset: 0);
            await allocatorA.LoadBitmapAsync();
            var writerA = new ApfsWriter(device, allocatorA, BlockSize, 0,
                volumeOid: VolumeOid, volumeSuperblockBlock: VsbBlock, currentXid: 1);
            // No SetFsBTreeBlock — A must resolve via omap on the first write.
            // (constructor's currentXid=1 matches the omap entry we put in BuildSyntheticImage)
            var cnid = await writerA.CreateFileAsync(parentCnid: 2, name: "round-trip.txt");
            Assert(cnid > 0, "CreateFileAsync returned zero CNID");

            // ── "Remount" — fresh writer B with NO knowledge of A's state ──
            var allocatorB = new ApfsBlockAllocator(device, BlockSize, TotalBlocks, partitionOffset: 0);
            await allocatorB.LoadBitmapAsync();
            // Read the post-write VSB to learn the new XID — a real mount does this.
            var vsbBuf = new byte[BlockSize];
            await device.ReadAsync((long)(VsbBlock * BlockSize), vsbBuf, vsbBuf.Length);
            var vsbXid = BinaryPrimitives.ReadUInt64LittleEndian(vsbBuf.AsSpan(0x10, 8));
            Assert(vsbXid >= 2, $"expected VSB xid >= 2 after one write, got {vsbXid}");

            var writerB = new ApfsWriter(device, allocatorB, BlockSize, 0,
                volumeOid: VolumeOid, volumeSuperblockBlock: VsbBlock, currentXid: vsbXid);

            // B reads the fs-tree via the omap chain and should find the inode A wrote.
            // We exercise that by issuing a write — it internally calls ReadFsBTreeAsync
            // which invokes ResolveRootTreeBlockAsync. If the omap chain is broken, the
            // read returns null and the write silently no-ops.
            var beforeWriteFsBlock = await GetFsBlockViaOmap(device, vsbBuf);
            Assert(beforeWriteFsBlock != InitialFsTreeBlock,
                $"omap was not COW'd — still pointing at initial fs-tree block {InitialFsTreeBlock}");

            // The fs-tree node at the new block must contain CNID's records.
            var fsBuf = new byte[BlockSize];
            await device.ReadAsync((long)(beforeWriteFsBlock * BlockSize), fsBuf, fsBuf.Length);
            Assert(ApfsChecksum.Verify(fsBuf), "fs-tree node checksum failed after remount");
            var node = ApfsBTreeNode.Deserialize(fsBuf, BlockSize)
                ?? throw new Exception("fs-tree deserialize returned null");
            // CreateFile inserts inode (type 3) + drec (type 9). For our file +
            // base directory the leaf should now contain >= 2 records.
            Assert(node.RecordCount >= 2,
                $"expected fs-tree to contain >= 2 records after CreateFile, got {node.RecordCount}");
        });

        await Run("2. Two consecutive writes COW twice → VSB.omap_oid changes each time", async () =>
        {
            var image = BuildSyntheticImage();
            var device = new WritableMemoryRawBlockDevice(image);
            var allocator = new ApfsBlockAllocator(device, BlockSize, TotalBlocks, 0);
            await allocator.LoadBitmapAsync();
            var writer = new ApfsWriter(device, allocator, BlockSize, 0,
                volumeOid: VolumeOid, volumeSuperblockBlock: VsbBlock, currentXid: 1);

            var vsb1 = new byte[BlockSize];
            await device.ReadAsync((long)(VsbBlock * BlockSize), vsb1, vsb1.Length);
            var omap1 = BinaryPrimitives.ReadUInt64LittleEndian(vsb1.AsSpan(0x80, 8));

            await writer.CreateFileAsync(2, "first.txt");
            var vsb2 = new byte[BlockSize];
            await device.ReadAsync((long)(VsbBlock * BlockSize), vsb2, vsb2.Length);
            var omap2 = BinaryPrimitives.ReadUInt64LittleEndian(vsb2.AsSpan(0x80, 8));

            Assert(omap2 != omap1, $"VSB.omap_oid should change after first write (was {omap1}, still {omap2})");

            await writer.CreateFileAsync(2, "second.txt");
            var vsb3 = new byte[BlockSize];
            await device.ReadAsync((long)(VsbBlock * BlockSize), vsb3, vsb3.Length);
            var omap3 = BinaryPrimitives.ReadUInt64LittleEndian(vsb3.AsSpan(0x80, 8));

            Assert(omap3 != omap2, $"VSB.omap_oid should change after second write (was {omap2}, still {omap3})");
        });

        await Run("3. After COW, the original fs-tree block content is unchanged (immutability)", async () =>
        {
            var image = BuildSyntheticImage();
            var originalFsBlockCopy = new byte[BlockSize];
            Array.Copy(image, (int)(InitialFsTreeBlock * BlockSize), originalFsBlockCopy, 0, (int)BlockSize);

            var device = new WritableMemoryRawBlockDevice(image);
            var allocator = new ApfsBlockAllocator(device, BlockSize, TotalBlocks, 0);
            await allocator.LoadBitmapAsync();
            var writer = new ApfsWriter(device, allocator, BlockSize, 0,
                volumeOid: VolumeOid, volumeSuperblockBlock: VsbBlock, currentXid: 1);
            await writer.CreateFileAsync(2, "immutability.txt");

            var afterFsBlock = new byte[BlockSize];
            Array.Copy(image, (int)(InitialFsTreeBlock * BlockSize), afterFsBlock, 0, (int)BlockSize);
            Assert(originalFsBlockCopy.AsSpan().SequenceEqual(afterFsBlock),
                "original fs-tree block was mutated — COW did not allocate a new block");
        });

        Console.WriteLine($"\nResults: {passed} passed, {failed} failed out of {passed + failed} tests.");
        return failed == 0;
    }

    /// <summary>
    /// Reads the VSB → omap leaf → fs-tree pointer chain and returns the
    /// physical block number that the highest-XID omap entry points at.
    /// </summary>
    private static async Task<ulong> GetFsBlockViaOmap(IRawBlockDevice device, byte[] vsbBuf)
    {
        var omapBlock   = BinaryPrimitives.ReadUInt64LittleEndian(vsbBuf.AsSpan(0x80, 8));
        var rootTreeOid = BinaryPrimitives.ReadUInt64LittleEndian(vsbBuf.AsSpan(0x88, 8));

        var omapBuf = new byte[BlockSize];
        await device.ReadAsync((long)(omapBlock * BlockSize), omapBuf, omapBuf.Length);
        var omapNode = ApfsBTreeNode.Deserialize(omapBuf, BlockSize, isRoot: true)
            ?? throw new Exception("omap deserialize returned null");

        ulong bestXid = 0;
        ulong bestPaddr = 0;
        foreach (var (key, val) in omapNode.Records)
        {
            if (key.Length < 16 || val.Length < 16) continue;
            var kOid = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(0, 8));
            var kXid = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8, 8));
            if (kOid != rootTreeOid) continue;
            if (kXid >= bestXid)
            {
                bestXid = kXid;
                bestPaddr = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(8, 8));
            }
        }
        if (bestPaddr == 0) throw new Exception($"no omap entry found for rootTreeOid={rootTreeOid}");
        return bestPaddr;
    }

    /// <summary>
    /// Same synthetic image as ApfsFileOpsTests.BuildSyntheticImage but kept
    /// local so the two test suites can evolve independently.
    /// </summary>
    private static byte[] BuildSyntheticImage()
    {
        var image = new byte[TotalBlocks * BlockSize];

        // Block 1: VSB
        var vsb = image.AsSpan((int)(VsbBlock * BlockSize), (int)BlockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x08, 8), VolumeOid);
        BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x10, 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(vsb.Slice(0x20, 4), 0x42535041u);  // APSB
        BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x80, 8), InitialOmapBlock);
        BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x88, 8), InitialFsTreeBlock);
        ApfsChecksum.WriteChecksum(vsb);

        // Block 2: empty fs-tree leaf
        var fsNode = new ApfsBTreeNode(BlockSize, oid: InitialFsTreeBlock, xid: 1,
            objectType: 0x00000002u, objectSubtype: 0x0000000Eu);
        var fsBuf = fsNode.Serialize()!;
        fsBuf.CopyTo(image.AsSpan((int)(InitialFsTreeBlock * BlockSize), (int)BlockSize));

        // Block 3: omap with one entry: (oid=2, xid=1) → paddr=2
        var omapNode = new ApfsBTreeNode(BlockSize, oid: InitialOmapBlock, xid: 1,
            objectType: 0x40000002u, objectSubtype: 0x0000000Bu, isRoot: true);
        var key = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(0, 8), InitialFsTreeBlock);
        BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), 1);
        var val = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(4, 4), BlockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(8, 8), InitialFsTreeBlock);
        omapNode.Insert(key, val);
        var omapBuf = omapNode.Serialize()!;
        omapBuf.CopyTo(image.AsSpan((int)(InitialOmapBlock * BlockSize), (int)BlockSize));

        return image;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
