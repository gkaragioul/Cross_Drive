using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using CrossDrive.RawDiskEngine;

namespace CrossDrive.ApfsWriteTest;

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

        await Run("11. APFS decmpfs zlib inline reports uncompressed size and decompresses bytes", async () =>
        {
            await Task.CompletedTask;

            var plain = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("CrossDrive compressed APFS payload. ", 128)));
            var decmpfs = BuildInlineDecmpfs(compressionType: 3, plain, CompressDeflate(plain));

            var providerType = Type.GetType("CrossDrive.RawDiskEngine.ApfsRawFileSystemProvider, CrossDrive.RawDiskEngine", throwOnError: true)!;
            var sizeMethod = providerType.GetMethod("TryReadInlineDecmpfsUncompressedSize", BindingFlags.NonPublic | BindingFlags.Static)!;
            var decompressMethod = providerType.GetMethod("TryDecompressInlineDecmpfs", BindingFlags.NonPublic | BindingFlags.Static)!;

            var logicalSize = (long?)sizeMethod.Invoke(null, new object[] { decmpfs });
            var decompressed = (byte[]?)decompressMethod.Invoke(null, new object[] { decmpfs });

            Assert(decmpfs.Length < plain.Length, $"test payload did not compress: compressed={decmpfs.Length} plain={plain.Length}");
            Assert(logicalSize == plain.Length, $"expected logical size {plain.Length}, got {logicalSize}");
            if (decompressed is null) throw new Exception("decompression returned null");
            Assert(decompressed.SequenceEqual(plain), "decompressed bytes do not match original payload");
        });

        await Run("12. APFS decmpfs uncompressed inline size is header logical size", async () =>
        {
            await Task.CompletedTask;

            var plain = Encoding.UTF8.GetBytes("resident APFS data");
            var decmpfs = BuildInlineDecmpfs(compressionType: 1, plain, plain);

            var providerType = Type.GetType("CrossDrive.RawDiskEngine.ApfsRawFileSystemProvider, CrossDrive.RawDiskEngine", throwOnError: true)!;
            var logicalSizeMethod = providerType.GetMethod("GetInlineDataLogicalSize", BindingFlags.NonPublic | BindingFlags.Static)!;
            var logicalSize = (long)logicalSizeMethod.Invoke(null, new object[] { decmpfs, true })!;

            Assert(logicalSize == plain.Length, $"expected logical size {plain.Length}, got {logicalSize}");
        });

        await Run("13. APFS decmpfs uncompressed inline pads short resident payload to logical size", async () =>
        {
            await Task.CompletedTask;

            var logical = new byte[32];
            var resident = Encoding.ASCII.GetBytes("SHORT");
            var decmpfs = BuildInlineDecmpfs(compressionType: 1, logical, resident);

            var providerType = Type.GetType("CrossDrive.RawDiskEngine.ApfsRawFileSystemProvider, CrossDrive.RawDiskEngine", throwOnError: true)!;
            var decompressMethod = providerType.GetMethod("TryDecompressInlineDecmpfs", BindingFlags.NonPublic | BindingFlags.Static)!;
            var decompressed = (byte[]?)decompressMethod.Invoke(null, new object[] { decmpfs });

            if (decompressed is null) throw new Exception("decompression returned null");
            Assert(decompressed.Length == logical.Length, $"expected logical length {logical.Length}, got {decompressed.Length}");
            Assert(decompressed.AsSpan(0, resident.Length).SequenceEqual(resident), "resident payload mismatch");
            Assert(decompressed.AsSpan(resident.Length).SequenceEqual(new byte[logical.Length - resident.Length]), "resident payload tail was not zero-filled");
        });

        await Run("14. APFS decmpfs zlib resource-fork cmpf chunks decompress bytes", async () =>
        {
            await Task.CompletedTask;

            var plain = Encoding.UTF8.GetBytes(string.Concat(
                Enumerable.Repeat("CrossDrive APFS resource-fork compressed data block. ", 1800)));
            var decmpfs = BuildInlineDecmpfs(compressionType: 4, plain, Array.Empty<byte>());
            var chunks = plain
                .Chunk(64 * 1024)
                .Select(chunk => CompressDeflate(chunk))
                .ToArray();
            var cmpfData = BuildChunkedCmpfData(chunks);
            var resourceFork = BuildCmpfResourceFork(cmpfData);

            var providerType = Type.GetType("CrossDrive.RawDiskEngine.ApfsRawFileSystemProvider, CrossDrive.RawDiskEngine", throwOnError: true)!;
            var decompressMethod = providerType.GetMethod("TryDecompressResourceForkDecmpfs", BindingFlags.NonPublic | BindingFlags.Static)!;
            var decompressed = (byte[]?)decompressMethod.Invoke(null, new object[] { decmpfs, resourceFork });

            if (decompressed is null) throw new Exception("resource-fork decompression returned null");
            Assert(decompressed.SequenceEqual(plain), "resource-fork decompressed bytes do not match original payload");
        });

        await Run("15. APFS decmpfs zlib resource-fork cmpf range reads cross chunk boundaries", async () =>
        {
            await Task.CompletedTask;

            var plain = Enumerable.Range(0, 150 * 1024)
                .Select(i => (byte)((i * 31) & 0xFF))
                .ToArray();
            var decmpfs = BuildInlineDecmpfs(compressionType: 4, plain, Array.Empty<byte>());
            var chunks = plain
                .Chunk(64 * 1024)
                .Select(chunk => CompressDeflate(chunk))
                .ToArray();
            var resourceFork = BuildCmpfResourceFork(BuildChunkedCmpfData(chunks));

            var providerType = Type.GetType("CrossDrive.RawDiskEngine.ApfsRawFileSystemProvider, CrossDrive.RawDiskEngine", throwOnError: true)!;
            var rangeMethod = providerType.GetMethod(
                "TryDecompressResourceForkDecmpfsRange",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(byte[]), typeof(byte[]), typeof(long), typeof(int) },
                modifiers: null)!;
            var offset = 64 * 1024 - 37;
            var count = 512;
            var range = (byte[]?)rangeMethod.Invoke(null, new object[] { decmpfs, resourceFork, (long)offset, count });

            if (range is null) throw new Exception("resource-fork range decompression returned null");
            Assert(range.Length == count, $"expected range length {count}, got {range.Length}");
            Assert(range.SequenceEqual(plain.AsSpan(offset, count).ToArray()), "range bytes crossing cmpf chunk boundary do not match original payload");
        });

        await Run("16. APFS decmpfs zlib resource-fork cmpf streamed range reads only intersecting chunks", async () =>
        {
            await Task.CompletedTask;

            var plain = new byte[256 * 1024];
            new Random(12345).NextBytes(plain);
            var decmpfs = BuildInlineDecmpfs(compressionType: 4, plain, Array.Empty<byte>());
            var chunks = plain
                .Chunk(64 * 1024)
                .Select(chunk => CompressDeflate(chunk))
                .ToArray();
            var resourceFork = BuildCmpfResourceFork(BuildChunkedCmpfData(chunks));
            var reads = new List<(long Offset, int Count)>();

            byte[]? ReadRange(long offset, int count)
            {
                if (offset < 0 || count < 0 || offset > resourceFork.Length || count > resourceFork.Length - offset)
                {
                    return null;
                }

                reads.Add((offset, count));
                return resourceFork.AsSpan((int)offset, count).ToArray();
            }

            var providerType = Type.GetType("CrossDrive.RawDiskEngine.ApfsRawFileSystemProvider, CrossDrive.RawDiskEngine", throwOnError: true)!;
            var rangeMethod = providerType.GetMethod(
                "TryDecompressResourceForkDecmpfsRange",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(byte[]), typeof(Func<long, int, byte[]?>), typeof(long), typeof(long), typeof(int) },
                modifiers: null)!;
            var offset = 64 * 1024 + 1234;
            var count = 2048;
            var range = (byte[]?)rangeMethod.Invoke(null, new object[] { decmpfs, (Func<long, int, byte[]?>)ReadRange, (long)resourceFork.Length, (long)offset, count });

            if (range is null) throw new Exception("streamed resource-fork range decompression returned null");
            Assert(range.Length == count, $"expected streamed range length {count}, got {range.Length}");
            Assert(range.SequenceEqual(plain.AsSpan(offset, count).ToArray()), "streamed range bytes do not match original payload");
            Assert(!reads.Any(read => read.Offset == 0 && read.Count >= resourceFork.Length), "streamed range path read the full resource fork");
            Assert(reads.Count(read => read.Count > 4096) == 1, $"expected exactly one compressed chunk payload read, got reads: {string.Join(", ", reads.Select(r => $"{r.Offset}+{r.Count}"))}");
            Assert(reads.Sum(read => read.Count) < resourceFork.Length / 2, $"streamed range read too many bytes: read {reads.Sum(read => read.Count)} of {resourceFork.Length}");
        });

        await Run("14. APFS ResourceFork xattr payload is converted to AppleDouble sidecar bytes", async () =>
        {
            await Task.CompletedTask;

            var resource = Encoding.UTF8.GetBytes("APFS resource fork payload");
            var helperType = Type.GetType("CrossDrive.RawDiskEngine.ApfsAppleDouble, CrossDrive.RawDiskEngine", throwOnError: true)!;
            var buildMethod = helperType.GetMethod("BuildResourceForkSidecar", BindingFlags.Public | BindingFlags.Static)!;
            var sidecar = (byte[])buildMethod.Invoke(null, new object[] { resource })!;

            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(0, 4)) == 0x00051607, "AppleDouble magic mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(4, 4)) == 0x00020000, "AppleDouble version mismatch");
            Assert(BinaryPrimitives.ReadUInt16BigEndian(sidecar.AsSpan(24, 2)) == 1, "AppleDouble entry count mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(26, 4)) == 2, "AppleDouble resource fork entry id mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(30, 4)) == 38, "AppleDouble resource fork offset mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(34, 4)) == resource.Length, "AppleDouble resource fork length mismatch");
            Assert(sidecar.AsSpan(38).SequenceEqual(resource), "AppleDouble resource fork payload mismatch");
        });

        await Run("15. APFS inline ResourceFork xattr parser honors xdata_len header", async () =>
        {
            await Task.CompletedTask;

            var resource = Encoding.UTF8.GetBytes("inline APFS resource data");
            var xattrValue = new byte[4 + resource.Length + 5];
            BinaryPrimitives.WriteUInt16LittleEndian(xattrValue.AsSpan(0, 2), 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(xattrValue.AsSpan(2, 2), (ushort)resource.Length);
            resource.CopyTo(xattrValue.AsSpan(4));

            var helperType = Type.GetType("CrossDrive.RawDiskEngine.ApfsAppleDouble, CrossDrive.RawDiskEngine", throwOnError: true)!;
            var extractMethod = helperType.GetMethod(
                "TryExtractInlineXattrData",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(byte[]) },
                modifiers: null)!;
            var extracted = (byte[]?)extractMethod.Invoke(null, new object[] { xattrValue });

            if (extracted is null) throw new Exception("xattr extraction returned null");
            Assert(extracted.SequenceEqual(resource), "xattr extraction did not honor xdata_len");

            var nonEmbedded = new byte[4 + resource.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(nonEmbedded.AsSpan(0, 2), 0x0000);
            BinaryPrimitives.WriteUInt16LittleEndian(nonEmbedded.AsSpan(2, 2), (ushort)resource.Length);
            resource.CopyTo(nonEmbedded.AsSpan(4));
            var nonEmbeddedExtracted = (byte[]?)extractMethod.Invoke(null, new object[] { nonEmbedded });
            Assert(nonEmbeddedExtracted is null, "non-embedded xattr descriptor was mistaken for inline payload");
        });

        await Run("16. APFS extent-backed ResourceFork sidecars stream AppleDouble header and payload", async () =>
        {
            await Task.CompletedTask;

            var image = new byte[18 * BlockSize];
            var first = Encoding.ASCII.GetBytes("RSRC-EXTENT-A");
            var second = Encoding.ASCII.GetBytes("RSRC-EXTENT-B");
            first.CopyTo(image.AsSpan((int)(14 * BlockSize)));
            second.CopyTo(image.AsSpan((int)(15 * BlockSize)));

            using var device = new WritableMemoryRawBlockDevice(image);
            var resourcePlan = new ApfsFileReadPlan(
                TotalSize: 33,
                Extents: new[]
                {
                    new ApfsFileExtent(LogicalOffset: 0, Length: first.Length, PhysicalBlockNumber: 14),
                    new ApfsFileExtent(LogicalOffset: 20, Length: second.Length, PhysicalBlockNumber: 15)
                });
            var sidecarPlan = ApfsAppleDouble.BuildResourceForkSidecarReadPlan(resourcePlan);

            Assert(sidecarPlan.InlineData is { Length: 38 }, "AppleDouble sidecar header was not stored inline");
            Assert(sidecarPlan.TotalSize == 71, $"expected sidecar logical size 71, got {sidecarPlan.TotalSize}");
            Assert(sidecarPlan.Extents.Count == 2, "resource fork extents were not preserved");
            Assert(sidecarPlan.Extents[0].LogicalOffset == 38, "first resource extent was not shifted after AppleDouble header");
            Assert(sidecarPlan.Extents[1].LogicalOffset == 58, "second resource extent was not shifted after AppleDouble header");

            var whole = Enumerable.Repeat((byte)0xCC, (int)sidecarPlan.TotalSize).ToArray();
            var read = ApfsRawFileSystemProvider.ReadInlinePrefixedExtentBackedFile(device, 0, BlockSize, sidecarPlan, 0, whole);
            Assert(read == whole.Length, $"expected full sidecar read count {whole.Length}, got {read}");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(whole.AsSpan(0, 4)) == 0x00051607, "AppleDouble magic mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(whole.AsSpan(34, 4)) == 33, "AppleDouble resource fork length mismatch");
            Assert(whole.AsSpan(38, first.Length).SequenceEqual(first), "first resource fork extent mismatch");
            Assert(whole.AsSpan(38 + first.Length, 20 - first.Length).SequenceEqual(new byte[20 - first.Length]), "resource fork sparse gap was not zero-filled");
            Assert(whole.AsSpan(58, second.Length).SequenceEqual(second), "second resource fork extent mismatch");

            var partial = Enumerable.Repeat((byte)0xCC, 24).ToArray();
            var partialRead = ApfsRawFileSystemProvider.ReadInlinePrefixedExtentBackedFile(device, 0, BlockSize, sidecarPlan, 30, partial);
            Assert(partialRead == partial.Length, $"expected partial sidecar read count {partial.Length}, got {partialRead}");
            Assert(partial.AsSpan(0, 4).SequenceEqual(sidecarPlan.InlineData.AsSpan(30, 4)), "partial AppleDouble header mismatch");
            Assert(partial.AsSpan(8, first.Length).SequenceEqual(first), "partial resource extent mismatch");
        });

        await Run("17. APFS FinderInfo xattr is preserved as AppleDouble entry 9", async () =>
        {
            await Task.CompletedTask;

            var resource = Encoding.UTF8.GetBytes("APFS resource fork with FinderInfo");
            var finderInfo = new byte[32];
            Encoding.ASCII.GetBytes("TEXTttxt").CopyTo(finderInfo, 0);
            for (var i = 8; i < finderInfo.Length; i++)
            {
                finderInfo[i] = (byte)(0x30 + i);
            }

            var resourcePlan = new ApfsFileReadPlan(resource.Length, Array.Empty<ApfsFileExtent>(), resource);
            var sidecarPlan = ApfsAppleDouble.BuildAppleDoubleSidecarReadPlan(resourcePlan, finderInfo);

            Assert(sidecarPlan.TotalSize == 82 + resource.Length, $"expected FinderInfo sidecar logical size {82 + resource.Length}, got {sidecarPlan.TotalSize}");
            Assert(sidecarPlan.InlineData is { Length: > 82 }, "FinderInfo AppleDouble sidecar was not stored inline");
            var sidecar = sidecarPlan.InlineData!;
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(0, 4)) == 0x00051607, "AppleDouble magic mismatch");
            Assert(BinaryPrimitives.ReadUInt16BigEndian(sidecar.AsSpan(24, 2)) == 2, "AppleDouble entry count mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(26, 4)) == 9, "FinderInfo entry id mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(30, 4)) == 50, "FinderInfo entry offset mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(34, 4)) == 32, "FinderInfo entry length mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(38, 4)) == 2, "resource fork entry id mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(42, 4)) == 82, "resource fork entry offset mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(46, 4)) == resource.Length, "resource fork entry length mismatch");
            Assert(sidecar.AsSpan(50, 32).SequenceEqual(finderInfo), "FinderInfo payload mismatch");
            Assert(sidecar.AsSpan(82, resource.Length).SequenceEqual(resource), "resource fork payload mismatch");
        });

        await Run("18. APFS extent-backed ResourceFork sidecars shift after FinderInfo entry", async () =>
        {
            await Task.CompletedTask;

            var image = new byte[18 * BlockSize];
            var first = Encoding.ASCII.GetBytes("RSRC-FI-A");
            var second = Encoding.ASCII.GetBytes("RSRC-FI-B");
            first.CopyTo(image.AsSpan((int)(14 * BlockSize)));
            second.CopyTo(image.AsSpan((int)(15 * BlockSize)));

            var finderInfo = Enumerable.Range(0, 32).Select(i => (byte)(0x80 + i)).ToArray();
            using var device = new WritableMemoryRawBlockDevice(image);
            var resourcePlan = new ApfsFileReadPlan(
                TotalSize: 33,
                Extents: new[]
                {
                    new ApfsFileExtent(LogicalOffset: 0, Length: first.Length, PhysicalBlockNumber: 14),
                    new ApfsFileExtent(LogicalOffset: 20, Length: second.Length, PhysicalBlockNumber: 15)
                });
            var sidecarPlan = ApfsAppleDouble.BuildAppleDoubleSidecarReadPlan(resourcePlan, finderInfo);

            Assert(sidecarPlan.InlineData is { Length: 82 }, "AppleDouble FinderInfo prefix was not stored inline");
            Assert(sidecarPlan.TotalSize == 115, $"expected sidecar logical size 115, got {sidecarPlan.TotalSize}");
            Assert(sidecarPlan.Extents.Count == 2, "resource fork extents were not preserved");
            Assert(sidecarPlan.Extents[0].LogicalOffset == 82, "first resource extent was not shifted after FinderInfo prefix");
            Assert(sidecarPlan.Extents[1].LogicalOffset == 102, "second resource extent was not shifted after FinderInfo prefix");

            var whole = Enumerable.Repeat((byte)0xCC, (int)sidecarPlan.TotalSize).ToArray();
            var read = ApfsRawFileSystemProvider.ReadInlinePrefixedExtentBackedFile(device, 0, BlockSize, sidecarPlan, 0, whole);
            Assert(read == whole.Length, $"expected full sidecar read count {whole.Length}, got {read}");
            Assert(whole.AsSpan(50, 32).SequenceEqual(finderInfo), "FinderInfo payload mismatch");
            Assert(whole.AsSpan(82, first.Length).SequenceEqual(first), "first resource fork extent mismatch");
            Assert(whole.AsSpan(82 + first.Length, 20 - first.Length).SequenceEqual(new byte[20 - first.Length]), "resource fork sparse gap was not zero-filled");
            Assert(whole.AsSpan(102, second.Length).SequenceEqual(second), "second resource fork extent mismatch");
        });

        await Run("19. APFS inline xattrs are packed into FinderInfo AppleDouble ATTR data", async () =>
        {
            await Task.CompletedTask;

            var resource = Encoding.ASCII.GetBytes("RSRC-WITH-XATTRS");
            var finderInfo = Enumerable.Range(0, 32).Select(i => (byte)(0x40 + i)).ToArray();
            var whereFroms = Encoding.UTF8.GetBytes("https://example.invalid/source");
            var userTest = new byte[] { 1, 2, 3, 4, 5 };
            var attrs = new[]
            {
                new ApfsExtendedAttribute("user.test", userTest),
                new ApfsExtendedAttribute("com.apple.metadata:kMDItemWhereFroms", whereFroms),
                new ApfsExtendedAttribute("com.apple.decmpfs", Encoding.ASCII.GetBytes("skip-me"))
            };

            var resourcePlan = new ApfsFileReadPlan(resource.Length, Array.Empty<ApfsFileExtent>(), resource);
            var sidecarPlan = ApfsAppleDouble.BuildAppleDoubleSidecarReadPlan(resourcePlan, finderInfo, attrs);

            Assert(sidecarPlan.InlineData is { Length: > 120 }, "xattr AppleDouble sidecar was not stored inline");
            var sidecar = sidecarPlan.InlineData!;
            Assert(BinaryPrimitives.ReadUInt16BigEndian(sidecar.AsSpan(24, 2)) == 2, "AppleDouble entry count mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(26, 4)) == 9, "FinderInfo entry id mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(38, 4)) == 2, "resource fork entry id mismatch");

            var finderOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(30, 4));
            var finderLength = (int)BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(34, 4));
            var resourceOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(42, 4));
            var resourceLength = (int)BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(46, 4));
            Assert(finderOffset == 50, $"expected FinderInfo offset 50, got {finderOffset}");
            Assert(finderLength > 32, "FinderInfo ATTR entry length did not include xattrs");
            Assert(resourceOffset == finderOffset + finderLength, "resource fork offset did not follow ATTR FinderInfo payload");
            Assert(resourceLength == resource.Length, "resource fork length mismatch");
            Assert(sidecar.AsSpan(finderOffset, 32).SequenceEqual(finderInfo), "FinderInfo bytes were not preserved");
            Assert(sidecar.AsSpan(resourceOffset, resource.Length).SequenceEqual(resource), "resource fork payload mismatch");

            var attrHeaderOffset = finderOffset + 34;
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(attrHeaderOffset, 4)) == 0x41545452, "ATTR magic mismatch");
            Assert(BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(attrHeaderOffset + 8, 4)) == resourceOffset, "ATTR total_size mismatch");
            var attrDataStart = (int)BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(attrHeaderOffset + 12, 4));
            var attrDataLength = (int)BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(attrHeaderOffset + 16, 4));
            Assert(attrDataLength == whereFroms.Length + userTest.Length, "ATTR data length mismatch");
            Assert(BinaryPrimitives.ReadUInt16BigEndian(sidecar.AsSpan(attrHeaderOffset + 34, 2)) == 2, "ATTR count mismatch");

            var parsed = new Dictionary<string, (int Offset, byte[] Data)>(StringComparer.Ordinal);
            var entryOffset = attrHeaderOffset + 36;
            for (var i = 0; i < 2; i++)
            {
                var dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(entryOffset, 4));
                var dataLength = (int)BinaryPrimitives.ReadUInt32BigEndian(sidecar.AsSpan(entryOffset + 4, 4));
                var nameLength = sidecar[entryOffset + 10];
                var name = Encoding.UTF8.GetString(sidecar.AsSpan(entryOffset + 11, nameLength - 1));
                parsed[name] = (dataOffset, sidecar.AsSpan(dataOffset, dataLength).ToArray());
                entryOffset += Align4(11 + nameLength);
            }

            Assert(attrDataStart == parsed.Values.Min(value => value.Offset), "ATTR data_start did not point at first xattr payload");
            Assert(parsed["com.apple.metadata:kMDItemWhereFroms"].Data.SequenceEqual(whereFroms), "WhereFroms xattr payload mismatch");
            Assert(parsed["user.test"].Data.SequenceEqual(userTest), "user.test xattr payload mismatch");
            Assert(!parsed.ContainsKey("com.apple.decmpfs"), "decmpfs metadata should not be re-packed with decompressed APFS data");
        });

        await Run("20. APFS sparse extent reads zero-fill holes and return logical byte count", async () =>
        {
            await Task.CompletedTask;

            var image = new byte[16 * BlockSize];
            var first = Encoding.ASCII.GetBytes("FIRST123");
            var second = Encoding.ASCII.GetBytes("SECOND45");
            first.CopyTo(image.AsSpan((int)(10 * BlockSize)));
            second.CopyTo(image.AsSpan((int)(11 * BlockSize)));

            using var device = new WritableMemoryRawBlockDevice(image);
            var plan = new ApfsFileReadPlan(
                TotalSize: 24,
                Extents: new[]
                {
                    new ApfsFileExtent(LogicalOffset: 0, Length: first.Length, PhysicalBlockNumber: 10),
                    new ApfsFileExtent(LogicalOffset: 16, Length: second.Length, PhysicalBlockNumber: 11)
                });

            var whole = new byte[24];
            var read = ApfsRawFileSystemProvider.ReadExtentBackedFile(device, 0, BlockSize, plan, 0, whole);
            Assert(read == whole.Length, $"expected logical read count {whole.Length}, got {read}");
            Assert(whole.AsSpan(0, first.Length).SequenceEqual(first), "first extent bytes mismatch");
            Assert(whole.AsSpan(8, 8).SequenceEqual(new byte[8]), "sparse hole was not zero-filled");
            Assert(whole.AsSpan(16, second.Length).SequenceEqual(second), "second extent bytes mismatch");

            var partial = Enumerable.Repeat((byte)0xCC, 16).ToArray();
            var partialRead = ApfsRawFileSystemProvider.ReadExtentBackedFile(device, 0, BlockSize, plan, 4, partial);
            Assert(partialRead == partial.Length, $"expected partial logical read count {partial.Length}, got {partialRead}");
            Assert(partial.AsSpan(0, 4).SequenceEqual(first.AsSpan(4, 4)), "partial first extent tail mismatch");
            Assert(partial.AsSpan(4, 8).SequenceEqual(new byte[8]), "partial sparse hole was not zero-filled");
            Assert(partial.AsSpan(12, 4).SequenceEqual(second.AsSpan(0, 4)), "partial second extent head mismatch");
        });

        await Run("21. APFS extent read plans preserve inode logical size beyond final extent", async () =>
        {
            await Task.CompletedTask;

            var image = new byte[16 * BlockSize];
            var payload = Encoding.ASCII.GetBytes("TAILDATA");
            payload.CopyTo(image.AsSpan((int)(12 * BlockSize)));

            using var device = new WritableMemoryRawBlockDevice(image);
            var extents = new[]
            {
                new ApfsFileExtent(LogicalOffset: 0, Length: payload.Length, PhysicalBlockNumber: 12)
            };
            var logicalSize = ApfsRawFileSystemProvider.GetExtentBackedLogicalSize(
                extents,
                inodeLogicalSize: 32,
                inlineData: null,
                isCompressed: false);
            Assert(logicalSize == 32, $"expected inode logical size 32, got {logicalSize}");

            var plan = new ApfsFileReadPlan(logicalSize, extents);
            var actual = Enumerable.Repeat((byte)0xCC, 32).ToArray();
            var read = ApfsRawFileSystemProvider.ReadExtentBackedFile(device, 0, BlockSize, plan, 0, actual);
            Assert(read == 32, $"expected logical read count 32, got {read}");
            Assert(actual.AsSpan(0, payload.Length).SequenceEqual(payload), "extent payload mismatch");
            Assert(actual.AsSpan(payload.Length).SequenceEqual(new byte[32 - payload.Length]), "sparse tail was not zero-filled");
        });

        await Run("22. Raw APFS symlink entries preserve target and reparse attributes", async () =>
        {
            await Task.CompletedTask;

            var entry = new RawFsEntry(
                "\\link",
                "link",
                IsDirectory: false,
                Size: 0,
                DateTimeOffset.UtcNow,
                FileAttributes.ReadOnly | FileAttributes.ReparsePoint,
                "../target.txt");

            Assert(entry.IsSymbolicLink, "RawFsEntry did not classify non-empty SymlinkTarget as a symbolic link");
            Assert(entry.SymlinkTarget == "../target.txt", "RawFsEntry symlink target was not preserved");
            Assert((entry.Attributes & FileAttributes.ReparsePoint) != 0, "RawFsEntry symlink missing reparse attribute");
        });

        Console.WriteLine($"\nResults: {passed} passed, {failed} failed out of {passed + failed} tests.");
        return failed == 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Returns a fresh copy of the fs-tree block from the image.</summary>
    private static byte[] GetFsBlock(byte[] image) =>
        image.AsSpan((int)(FsBTreeBlock * BlockSize), (int)BlockSize).ToArray();

    private static byte[] BuildInlineDecmpfs(uint compressionType, byte[] uncompressed, byte[] payload)
    {
        const int headerLength = 16;
        var decmpfs = new byte[headerLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(decmpfs.AsSpan(0, 4), 0x636D7066u);
        BinaryPrimitives.WriteUInt32LittleEndian(decmpfs.AsSpan(4, 4), compressionType);
        BinaryPrimitives.WriteUInt64LittleEndian(decmpfs.AsSpan(8, 8), (ulong)uncompressed.Length);
        payload.CopyTo(decmpfs.AsSpan(headerLength));
        return decmpfs;
    }

    private static byte[] CompressDeflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] BuildChunkedCmpfData(IReadOnlyList<byte[]> compressedChunks)
    {
        var chunkTableLength = 8 + compressedChunks.Count * 8;
        var dataStart = 16 + chunkTableLength;
        var compressedDataSize = compressedChunks.Sum(chunk => chunk.Length);
        var footerOffset = dataStart + compressedDataSize;
        var footerLength = 50;
        var output = new byte[footerOffset + footerLength];

        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), 16);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4, 4), (uint)footerOffset);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8, 4), (uint)(chunkTableLength + compressedDataSize));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12, 4), (uint)footerLength);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16, 4), (uint)compressedDataSize);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(20, 4), (uint)compressedChunks.Count);

        var chunkOutputOffset = dataStart;
        for (var i = 0; i < compressedChunks.Count; i++)
        {
            var chunk = compressedChunks[i];
            var tableOffset = 24 + i * 8;
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(tableOffset, 4), (uint)(chunkOutputOffset - 20));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(tableOffset + 4, 4), (uint)chunk.Length);
            chunk.CopyTo(output.AsSpan(chunkOutputOffset));
            chunkOutputOffset += chunk.Length;
        }

        Encoding.ASCII.GetBytes("cmpf").CopyTo(output.AsSpan(footerOffset + 32));
        return output;
    }

    private static byte[] BuildCmpfResourceFork(byte[] cmpfData)
    {
        var dataOffset = 16;
        var dataLength = 4 + cmpfData.Length;
        var mapOffset = dataOffset + dataLength;
        var mapLength = 50;
        var output = new byte[mapOffset + mapLength];

        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)dataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4, 4), (uint)mapOffset);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8, 4), (uint)dataLength);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12, 4), (uint)mapLength);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(dataOffset, 4), (uint)cmpfData.Length);
        cmpfData.CopyTo(output.AsSpan(dataOffset + 4));

        output.AsSpan(0, 16).CopyTo(output.AsSpan(mapOffset, 16));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(mapOffset + 24, 2), 28);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(mapOffset + 26, 2), 50);

        var typeListStart = mapOffset + 28;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(typeListStart, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(typeListStart + 2, 4), 0x636D7066u);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(typeListStart + 6, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(typeListStart + 8, 2), 10);

        var refStart = typeListStart + 10;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(refStart, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(refStart + 2, 2), 0xFFFF);
        output[refStart + 4] = 0;
        output[refStart + 5] = 0;
        output[refStart + 6] = 0;
        output[refStart + 7] = 0;

        return output;
    }

    private static int Align4(int value) => (value + 3) & ~3;

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
        // Phase 3/4 tests verify in-place writes against a fixed FsBTreeBlock.
        // Disable COW for them; the round-trip behaviour is validated separately
        // in ApfsRemountTests where the test reads through the omap chain.
        writer.UseCowOnFsTreeWrite = false;
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
