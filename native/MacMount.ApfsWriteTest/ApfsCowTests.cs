using System.Buffers.Binary;
using MacMount.RawDiskEngine;

namespace MacMount.ApfsWriteTest;

/// <summary>
/// Phase 2 tests: Fletcher-64 checksum + APFS B-tree leaf node builder + FlushAsync.
/// </summary>
internal static class ApfsCowTests
{
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

        // ---- Checksum tests ----

        await Run("1. Checksum of 4096-zero block is deterministic and non-zero", async () =>
        {
            await Task.CompletedTask;
            var block = new byte[4096];
            var c1 = ApfsChecksum.Compute(block.AsSpan());
            var c2 = ApfsChecksum.Compute(block.AsSpan());
            Assert(c1 == c2, "not deterministic");
            Assert(c1 != 0, "checksum of zero block should not be zero");
        });

        await Run("2. WriteChecksum + Verify round-trip on 4096-byte block", async () =>
        {
            await Task.CompletedTask;
            var block = new byte[4096];
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(0x08, 8), 42);  // o_oid
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(0x10, 8), 7);   // o_xid
            ApfsChecksum.WriteChecksum(block.AsSpan());
            // checksum field must be non-zero (extremely unlikely to be zero)
            var stored = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(0, 8));
            Assert(stored != 0, "stored checksum should not be zero");
            Assert(ApfsChecksum.Verify(block.AsSpan()), "verify should pass after WriteChecksum");
        });

        await Run("3. Modifying a data byte invalidates the checksum", async () =>
        {
            await Task.CompletedTask;
            var block = new byte[4096];
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(0x08, 8), 100);
            ApfsChecksum.WriteChecksum(block.AsSpan());
            block[100] ^= 0xFF; // flip bits in a data byte
            Assert(!ApfsChecksum.Verify(block.AsSpan()), "checksum should fail after data modification");
        });

        // ---- B-tree node builder tests ----

        await Run("4. Empty ApfsBTreeNode serializes to a 4096-byte block with valid checksum", async () =>
        {
            await Task.CompletedTask;
            var node = new ApfsBTreeNode(4096, oid: 10, xid: 1,
                objectType: 0x00000002u,    // OBJECT_TYPE_BTREE_NODE | OBJ_PHYSICAL
                objectSubtype: 0x0000000Bu  // OBJECT_TYPE_OMAP
            );
            var buf = node.Serialize();
            Assert(buf is not null, "Serialize returned null");
            Assert(buf!.Length == 4096, $"expected 4096 bytes, got {buf.Length}");
            Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid on empty node");
            var nkeys = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0x24, 4));
            Assert(nkeys == 0, $"expected nkeys=0, got {nkeys}");
        });

        await Run("5. Single-record node: nkeys=1, checksum valid, OID and subtype in header", async () =>
        {
            await Task.CompletedTask;
            var node = new ApfsBTreeNode(4096, oid: 11, xid: 2,
                objectType: 0x00000002u, objectSubtype: 0x0Bu);
            // omap key: OID=42, XID=1
            var key = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(0, 8), 42);
            BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), 1);
            // omap val: flags=0, size=4096, paddr=100
            var val = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(0, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(4, 4), 4096);
            BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(8, 8), 100);
            node.Insert(key, val);
            var buf = node.Serialize()!;
            var nkeys = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0x24, 4));
            Assert(nkeys == 1, $"expected nkeys=1, got {nkeys}");
            var storedOid = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0x08, 8));
            Assert(storedOid == 11, $"expected oid=11, got {storedOid}");
            Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid");
        });

        await Run("6. Insert two records out of order: keys sorted ascending in TOC", async () =>
        {
            await Task.CompletedTask;
            var node = new ApfsBTreeNode(4096, oid: 12, xid: 3,
                objectType: 0x00000002u, objectSubtype: 0x0Bu);
            // Insert high OID first, then low OID — expect sorted output
            var keyHigh = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(keyHigh.AsSpan(0, 8), 200);
            BinaryPrimitives.WriteUInt64LittleEndian(keyHigh.AsSpan(8, 8), 1);
            var keyLow = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(keyLow.AsSpan(0, 8), 50);
            BinaryPrimitives.WriteUInt64LittleEndian(keyLow.AsSpan(8, 8), 1);
            var dummyVal = new byte[16];
            node.Insert(keyHigh, dummyVal);
            node.Insert(keyLow, dummyVal);
            var recs = node.Records;
            Assert(recs.Count == 2, $"expected 2 records, got {recs.Count}");
            var oid0 = BinaryPrimitives.ReadUInt64LittleEndian(recs[0].Key.AsSpan(0, 8));
            var oid1 = BinaryPrimitives.ReadUInt64LittleEndian(recs[1].Key.AsSpan(0, 8));
            Assert(oid0 == 50,  $"expected oid0=50, got {oid0}");
            Assert(oid1 == 200, $"expected oid1=200, got {oid1}");
            var buf = node.Serialize()!;
            Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid after two inserts");
        });

        await Run("7. Delete removes the matching record; remaining record intact", async () =>
        {
            await Task.CompletedTask;
            var node = new ApfsBTreeNode(4096, oid: 13, xid: 4,
                objectType: 0x00000002u, objectSubtype: 0x0Bu);
            var key1 = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(key1.AsSpan(0, 8), 10);
            var key2 = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(key2.AsSpan(0, 8), 20);
            var v = new byte[16];
            node.Insert(key1, v);
            node.Insert(key2, v);
            var deleted = node.Delete(key1);
            Assert(deleted, "Delete returned false");
            Assert(node.RecordCount == 1, $"expected 1 record, got {node.RecordCount}");
            var remainOid = BinaryPrimitives.ReadUInt64LittleEndian(node.Records[0].Key.AsSpan(0, 8));
            Assert(remainOid == 20, $"expected remaining OID=20, got {remainOid}");
            var buf = node.Serialize()!;
            Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid after delete");
        });

        await Run("8. Serialized node: TOC k.off / v.off are absolute and point to correct data", async () =>
        {
            await Task.CompletedTask;
            const ulong oid = 777, xid = 5, paddr = 333;
            var node = new ApfsBTreeNode(4096, oid: 99, xid: xid,
                objectType: 0x00000002u, objectSubtype: 0x0Bu);
            var key = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(0, 8), oid);
            BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), xid);
            var val = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(0, 4), 0);      // flags
            BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(4, 4), 4096);   // size
            BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(8, 8), paddr);  // paddr
            node.Insert(key, val);
            var buf = node.Serialize()!;
            Assert(ApfsChecksum.Verify(buf.AsSpan()), "checksum invalid");
            // TOC is at 0x38 (HeaderSize). First entry = first 8 bytes of TOC.
            var kOff = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x38 + 0, 2));
            var kLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x38 + 2, 2));
            var vOff = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x38 + 4, 2));
            var vLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x38 + 6, 2));
            Assert(kLen == 16, $"expected kLen=16, got {kLen}");
            Assert(vLen == 16, $"expected vLen=16, got {vLen}");
            // k.off and v.off should be valid absolute positions within the block
            Assert(kOff >= 0x38 && kOff + 16 <= 4096, $"kOff={kOff} out of range");
            Assert(vOff >= 0x38 && vOff + 16 <= 4096, $"vOff={vOff} out of range");
            var readOid   = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(kOff, 8));
            var readPaddr = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(vOff + 8, 8));
            Assert(readOid   == oid,   $"expected OID={oid}, got {readOid}");
            Assert(readPaddr == paddr, $"expected paddr={paddr}, got {readPaddr}");
        });

        // ---- FlushAsync test ----

        await Run("9. FlushAsync rewrites volume superblock: increments XID, updates file count, valid checksum", async () =>
        {
            // Build a synthetic volume superblock at block 1 (block 0 = zeros).
            var deviceBytes = new byte[4096 * 2];
            var vsb = deviceBytes.AsSpan(4096, 4096);
            // APSB magic at 0x20 = 0x42535041
            BinaryPrimitives.WriteUInt32LittleEndian(vsb.Slice(0x20, 4), 0x42535041u);
            BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x08, 8), 500);  // o_oid
            BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x10, 8), 10);   // o_xid = 10
            BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x90, 8), 3);    // apfs_num_files = 3
            BinaryPrimitives.WriteUInt64LittleEndian(vsb.Slice(0x98, 8), 1);    // apfs_num_directories = 1
            ApfsChecksum.WriteChecksum(vsb);

            var device = new WritableMemoryRawBlockDevice(deviceBytes);
            var allocator = new ApfsBlockAllocator(device, 4096, 100, 0);
            await allocator.LoadBitmapAsync();

            var writer = new ApfsWriter(
                device, allocator, 4096,
                partitionOffset: 0,
                volumeOid: 500,
                volumeSuperblockBlock: 1,
                currentXid: 10);

            // Simulate 2 file creates
            writer.TrackFileCreated();
            writer.TrackFileCreated();

            await writer.FlushAsync();

            // Verify the written block
            var after = deviceBytes.AsSpan(4096, 4096);
            var newXid       = BinaryPrimitives.ReadUInt64LittleEndian(after.Slice(0x10, 8));
            var newFileCount = BinaryPrimitives.ReadUInt64LittleEndian(after.Slice(0x90, 8));
            var newDirCount  = BinaryPrimitives.ReadUInt64LittleEndian(after.Slice(0x98, 8));
            Assert(newXid == 11,       $"expected xid=11, got {newXid}");
            Assert(newFileCount == 5,  $"expected num_files=5, got {newFileCount}");
            Assert(newDirCount == 1,   $"dir count should be unchanged, got {newDirCount}");
            Assert(ApfsChecksum.Verify(after), "checksum invalid after FlushAsync");
        });

        Console.WriteLine($"\nResults: {passed} passed, {failed} failed out of {passed + failed} tests.");
        return failed == 0;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

/// <summary>In-memory IRawBlockDevice backed by a mutable byte array — used for write tests.</summary>
internal sealed class WritableMemoryRawBlockDevice : IRawBlockDevice
{
    private readonly byte[] _data;

    public WritableMemoryRawBlockDevice(byte[] data) => _data = data;

    public string DevicePath => "memory://writable-test";
    public long Length => _data.Length;
    public bool CanWrite => true;

    public ValueTask<int> ReadAsync(long offset, byte[] buffer, int count, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset >= _data.Length) return ValueTask.FromResult(0);
        var available = (int)Math.Min(count, _data.Length - offset);
        Buffer.BlockCopy(_data, (int)offset, buffer, 0, available);
        return ValueTask.FromResult(available);
    }

    public ValueTask WriteAsync(long offset, byte[] buffer, int count, CancellationToken cancellationToken = default)
    {
        if (offset >= 0 && offset + count <= _data.Length)
            Buffer.BlockCopy(buffer, 0, _data, (int)offset, count);
        return ValueTask.CompletedTask;
    }

    public void Dispose() { }
}
