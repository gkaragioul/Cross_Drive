using System.Buffers.Binary;
using MacMount.RawDiskEngine;

namespace MacMount.ApfsWriteTest;

/// <summary>
/// Tests for ApfsSpacemanReader using a synthetic in-memory APFS spaceman structure.
///
/// Synthetic image layout (32 blocks × 4096 bytes = 128 KB):
///   Block 0-3: metadata (zeros, marked used in bitmap)
///   Block 4: Spaceman block
///     sm_blocks_per_chunk=32 (all 32 blocks fit in 1 chunk)
///     sm_dev[0]: block_count=32, chunk_count=1, cib_count=1, free_count=22
///     sm_dev[0].sm_addr_offset=256 (CIB address array at byte 256 of this block)
///     CIB address [0] = 5 (block 5 is the CIB)
///   Block 5: CIB block
///     cib_index=0, cib_chunk_info_count=1
///     chunk_info[0]: ci_addr=6, ci_block_count=32, ci_free_count=22
///   Block 6: Bitmap block (32 bits meaningful)
///     Blocks 0-9 used (bit=0), blocks 10-31 free (bit=1)
///     Byte 0: blocks 0-7 all used  → 0x00
///     Byte 1: blocks 8-9 used (bits 0-1=0), blocks 10-15 free (bits 2-7=1) → 0xFC
///     Byte 2: blocks 16-23 all free → 0xFF
///     Byte 3: blocks 24-31 all free → 0xFF
///
/// NOTE: These offsets match the Apple APFS Reference (spaceman_phys_t / spaceman_device_t).
/// If tests fail against a real APFS image, the offsets in ApfsSpacemanReader.cs may need
/// adjustment for the specific macOS version generating the image.
/// </summary>
internal static class ApfsSpacemanTests
{
    private const uint BlockSize = 4096;
    private const int TotalBlocks = 32;
    private const ulong SpacemanBlock = 4;
    private const ulong CibBlock = 5;
    private const ulong BitmapBlock = 6;
    private const int ExpectedFreeBlocks = 22; // blocks 10-31

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

        // Test 3: FreeBlockCount is > 0 and < TotalBlockCount
        await Run("3. FreeBlockCount > 0 and < TotalBlockCount", async () =>
        {
            var device = new MemoryRawBlockDevice(imageBytes);
            var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
            Assert(sm.FreeBlockCount > 0, "FreeBlockCount is 0");
            Assert(sm.FreeBlockCount < sm.TotalBlockCount, $"FreeBlockCount {sm.FreeBlockCount} >= TotalBlockCount {sm.TotalBlockCount}");
        });

        // Test 4: FreeBlockCount matches expected value for synthetic image
        await Run("4. FreeBlockCount == 22 (blocks 10-31 are free)", async () =>
        {
            var device = new MemoryRawBlockDevice(imageBytes);
            var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
            Assert(sm.FreeBlockCount == ExpectedFreeBlocks, $"expected {ExpectedFreeBlocks}, got {sm.FreeBlockCount}");
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

        // Test 7: Round-trip MarkBlockUsed / IsBlockFree / MarkBlockFree
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

        // Test 8: ApfsBlockAllocator backed by spaceman — AllocateBlocks(1) marks a block used
        await Run("8. ApfsBlockAllocator(spaceman).AllocateBlocks(1) returns a block and marks it used", async () =>
        {
            var device = new MemoryRawBlockDevice(imageBytes);
            var sm = await ApfsSpacemanReader.LoadAsync(device, SpacemanBlock, BlockSize);
            var allocator = new ApfsBlockAllocator(sm);
            await allocator.LoadBitmapAsync(); // no-op for spaceman-backed allocator
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
        WriteSpacemanBlock(image, (int)(SpacemanBlock * BlockSize));
        WriteCibBlock(image, (int)(CibBlock * BlockSize));
        WriteBitmapBlock(image, (int)(BitmapBlock * BlockSize));
        return image;
    }

    private static void WriteSpacemanBlock(byte[] image, int offset)
    {
        var block = image.AsSpan(offset, (int)BlockSize);

        // Object header (not validated by reader, but set for completeness)
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(8, 8), 42);         // oid
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(16, 8), 1);         // xid
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(24, 4), 0x40000005u); // type: ephemeral | OBJECT_TYPE_SPACEMAN

        // spaceman_phys_t fields
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x20, 4), BlockSize);     // sm_block_size
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x24, 4), TotalBlocks);  // sm_blocks_per_chunk (32 → 1 chunk)
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x28, 4), 100);           // sm_chunks_per_cib
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x2C, 4), 100);           // sm_cibs_per_cab

        // sm_dev[0] at 0x30
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x30, 8), TotalBlocks);         // sm_block_count
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x38, 8), 1);                   // sm_chunk_count
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x40, 4), 1);                   // sm_cib_count
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x44, 4), 0);                   // sm_cab_count
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x48, 8), ExpectedFreeBlocks);  // sm_free_count
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x50, 4), 256);                 // sm_addr_offset → byte 256

        // CIB address array at byte 256: one entry → CIB is at block 5
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(256, 8), CibBlock);
    }

    private static void WriteCibBlock(byte[] image, int offset)
    {
        var block = image.AsSpan(offset, (int)BlockSize);

        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x20, 4), 0); // cib_index = 0
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x24, 4), 1); // cib_chunk_info_count = 1

        // spaceman_chunk_info_t[0] at 0x28 (24 bytes):
        //   ci_xid        [+0x00] = 1
        //   ci_addr       [+0x08] = BitmapBlock (6)
        //   ci_block_count[+0x10] = TotalBlocks (32)
        //   ci_free_count [+0x14] = ExpectedFreeBlocks (22)
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x28 + 0x00, 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(0x28 + 0x08, 8), BitmapBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x28 + 0x10, 4), TotalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0x28 + 0x14, 4), (uint)ExpectedFreeBlocks);
    }

    private static void WriteBitmapBlock(byte[] image, int offset)
    {
        // Blocks 0-9: used (bit=0), blocks 10-31: free (bit=1)
        // Byte 0 (bits 0-7  = blocks 0-7):  all used  → 0x00
        // Byte 1 (bits 8-15 = blocks 8-15): blocks 8,9 used (bits 0-1=0), blocks 10-15 free (bits 2-7=1) → 0xFC
        // Byte 2 (bits 16-23 = blocks 16-23): all free → 0xFF
        // Byte 3 (bits 24-31 = blocks 24-31): all free → 0xFF
        image[offset + 0] = 0x00;
        image[offset + 1] = 0xFC;
        image[offset + 2] = 0xFF;
        image[offset + 3] = 0xFF;
    }
}

/// <summary>In-memory IRawBlockDevice backed by a byte array — used for testing.</summary>
internal sealed class MemoryRawBlockDevice : IRawBlockDevice
{
    private readonly byte[] _data;

    public MemoryRawBlockDevice(byte[] data) => _data = data;

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
