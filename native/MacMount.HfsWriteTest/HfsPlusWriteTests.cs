using MacMount.RawDiskEngine;

namespace MacMount.HfsWriteTest;

public static class HfsPlusWriteTests
{
    private const long ImageSize = 100 * 1024 * 1024; // 100 MB

    public static async Task<bool> RunAllAsync(string imageFilePath)
    {
        var results = new List<(string Name, bool Passed)>();

        results.Add(("TestReadEmptyRoot", await RunTest("TestReadEmptyRoot", () => TestReadEmptyRoot(imageFilePath))));
        results.Add(("TestCreateSingleFile", await RunTest("TestCreateSingleFile", () => TestCreateSingleFile(imageFilePath))));
        results.Add(("TestCreateMultipleFiles", await RunTest("TestCreateMultipleFiles", () => TestCreateMultipleFiles(imageFilePath))));
        results.Add(("TestCreateDirectory", await RunTest("TestCreateDirectory", () => TestCreateDirectory(imageFilePath))));
        results.Add(("TestCreateFileInSubdirectory", await RunTest("TestCreateFileInSubdirectory", () => TestCreateFileInSubdirectory(imageFilePath))));
        results.Add(("TestCreateManyFiles", await RunTest("TestCreateManyFiles", () => TestCreateManyFiles(imageFilePath))));
        results.Add(("TestDeleteFile", await RunTest("TestDeleteFile", () => TestDeleteFile(imageFilePath))));
        results.Add(("TestLargeFile", await RunTest("TestLargeFile", () => TestLargeFile(imageFilePath))));
        results.Add(("TestOverwriteFile", await RunTest("TestOverwriteFile", () => TestOverwriteFile(imageFilePath))));
        results.Add(("TestFileSurvivesReopen", await RunTest("TestFileSurvivesReopen", () => TestFileSurvivesReopen(imageFilePath))));
        results.Add(("TestDeepNestedPaths", await RunTest("TestDeepNestedPaths", () => TestDeepNestedPaths(imageFilePath))));
        results.Add(("TestManyFilesInSubdirectory", await RunTest("TestManyFilesInSubdirectory", () => TestManyFilesInSubdirectory(imageFilePath))));
        results.Add(("TestMixedCreatePattern", await RunTest("TestMixedCreatePattern", () => TestMixedCreatePattern(imageFilePath))));
        results.Add(("TestCatalogGrowth", await RunTest("TestCatalogGrowth", () => TestCatalogGrowth(imageFilePath))));
        results.Add(("TestExplorerCopyPattern", await RunTest("TestExplorerCopyPattern", () => TestExplorerCopyPattern(imageFilePath))));
        results.Add(("TestUserExactSequence", await RunTest("TestUserExactSequence", () => TestUserExactSequence(imageFilePath))));
        results.Add(("TestLongFilenames", await RunTest("TestLongFilenames", () => TestLongFilenames(imageFilePath))));

        Console.WriteLine();
        Console.WriteLine("Summary:");
        foreach (var (name, passed) in results)
        {
            Console.WriteLine($"  {(passed ? "PASS" : "FAIL")} {name}");
        }

        return results.All(r => r.Passed);
    }

    private static async Task<bool> RunTest(string name, Func<Task<bool>> test)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {name} ---");
        try
        {
            var passed = await test();
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
            return passed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: Exception: {ex.Message}");
            Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"  Inner: {ex.InnerException.Message}");
            Console.WriteLine($"  Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            return false;
        }
    }

    /// <summary>
    /// Helper: creates a fresh image, formats it, opens reader, calls action, disposes.
    /// </summary>
    private static async Task<bool> WithFormattedImage(string imageFilePath, Func<HfsPlusNativeReader, FileBackedBlockDevice, Task<bool>> action)
    {
        // Delete any existing image
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        using var device = FileBackedBlockDevice.CreateNew(imageFilePath, ImageSize);
        await HfsPlusNativeReader.FormatAsync(device, 0, ImageSize, "TestVolume");

        var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
        if (reader is null)
        {
            Console.WriteLine("  FAIL: Could not open formatted image.");
            return false;
        }

        using (reader)
        {
            return await action(reader, device);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 1: Format, open, list root (CNID=2), verify 0 items
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestReadEmptyRoot(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var items = await reader.ListDirectoryAsync(2);
            if (items.Count != 0)
            {
                Console.WriteLine($"  Expected 0 items in root, found {items.Count}");
                foreach (var item in items)
                    Console.WriteLine($"    - {item.Name} (dir={item.IsDirectory}, size={item.Size})");
                return false;
            }
            Console.WriteLine("  Root directory is empty as expected.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 2: Create one file with 100 bytes, verify + read back
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestCreateSingleFile(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var data = new byte[100];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

            var cnid = await reader.CreateFileAsync(2, "hello.txt", data);
            Console.WriteLine($"  Created file with CNID {cnid}");

            // Verify it appears in root listing
            var items = await reader.ListDirectoryAsync(2);
            if (items.Count != 1)
            {
                Console.WriteLine($"  Expected 1 item in root, found {items.Count}");
                return false;
            }

            var file = items[0];
            if (file.Name != "hello.txt")
            {
                Console.WriteLine($"  Expected name 'hello.txt', got '{file.Name}'");
                return false;
            }
            if (file.IsDirectory)
            {
                Console.WriteLine("  Expected file, got directory");
                return false;
            }
            if (file.Size != 100)
            {
                Console.WriteLine($"  Expected size 100, got {file.Size}");
                return false;
            }

            // Read back and verify
            if (file.DataFork is null)
            {
                Console.WriteLine("  DataFork is null");
                return false;
            }

            var readBuf = new byte[100];
            var readCount = await reader.ReadFileAsync(file.DataFork, 0, readBuf, 100);
            if (readCount != 100)
            {
                Console.WriteLine($"  Expected to read 100 bytes, got {readCount}");
                return false;
            }

            for (int i = 0; i < 100; i++)
            {
                if (readBuf[i] != data[i])
                {
                    Console.WriteLine($"  Data mismatch at byte {i}: expected {data[i]}, got {readBuf[i]}");
                    return false;
                }
            }

            Console.WriteLine("  File created and data verified.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 3: Create 10 files with varying sizes
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestCreateMultipleFiles(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var sizes = new[] { 100, 500, 1024, 2048, 4096, 8192, 10240, 50000, 100000, 100000 };
            var fileData = new Dictionary<string, byte[]>();

            for (int f = 0; f < sizes.Length; f++)
            {
                var name = $"file_{f:D2}.bin";
                var data = new byte[sizes[f]];
                var rng = new Random(42 + f);
                rng.NextBytes(data);
                fileData[name] = data;

                await reader.CreateFileAsync(2, name, data);
                Console.WriteLine($"  Created {name} ({sizes[f]} bytes)");
            }

            // List root and verify all 10 files
            var items = await reader.ListDirectoryAsync(2);
            if (items.Count != sizes.Length)
            {
                Console.WriteLine($"  Expected {sizes.Length} items in root, found {items.Count}");
                foreach (var item in items)
                    Console.WriteLine($"    - {item.Name}");
                return false;
            }

            // Read back each file and verify data
            foreach (var item in items)
            {
                if (!fileData.TryGetValue(item.Name, out var expected))
                {
                    Console.WriteLine($"  Unexpected file: {item.Name}");
                    return false;
                }

                if (item.Size != expected.Length)
                {
                    Console.WriteLine($"  {item.Name}: expected size {expected.Length}, got {item.Size}");
                    return false;
                }

                if (item.DataFork is null)
                {
                    Console.WriteLine($"  {item.Name}: DataFork is null");
                    return false;
                }

                var readBuf = new byte[expected.Length];
                var readCount = await reader.ReadFileAsync(item.DataFork, 0, readBuf, expected.Length);
                if (readCount != expected.Length)
                {
                    Console.WriteLine($"  {item.Name}: expected to read {expected.Length}, got {readCount}");
                    return false;
                }

                for (int i = 0; i < expected.Length; i++)
                {
                    if (readBuf[i] != expected[i])
                    {
                        Console.WriteLine($"  {item.Name}: data mismatch at byte {i}");
                        return false;
                    }
                }
            }

            Console.WriteLine("  All 10 files created and verified.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 4: Create a directory, verify it appears
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestCreateDirectory(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var cnid = await reader.CreateFolderAsync(2, "MyFolder");
            Console.WriteLine($"  Created folder with CNID {cnid}");

            var items = await reader.ListDirectoryAsync(2);
            if (items.Count != 1)
            {
                Console.WriteLine($"  Expected 1 item in root, found {items.Count}");
                return false;
            }

            var folder = items[0];
            if (folder.Name != "MyFolder")
            {
                Console.WriteLine($"  Expected name 'MyFolder', got '{folder.Name}'");
                return false;
            }
            if (!folder.IsDirectory)
            {
                Console.WriteLine("  Expected directory, got file");
                return false;
            }

            // Verify empty listing of the new folder
            var subItems = await reader.ListDirectoryAsync(cnid);
            if (subItems.Count != 0)
            {
                Console.WriteLine($"  Expected 0 items in new folder, found {subItems.Count}");
                return false;
            }

            Console.WriteLine("  Directory created and verified.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 5: Create dir, create file inside it
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestCreateFileInSubdirectory(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var dirCnid = await reader.CreateFolderAsync(2, "SubDir");
            Console.WriteLine($"  Created folder SubDir with CNID {dirCnid}");

            var fileData = new byte[256];
            for (int i = 0; i < fileData.Length; i++) fileData[i] = (byte)(i ^ 0xAB);

            var fileCnid = await reader.CreateFileAsync(dirCnid, "nested.dat", fileData);
            Console.WriteLine($"  Created file nested.dat with CNID {fileCnid}");

            // Verify root has 1 item (the folder)
            var rootItems = await reader.ListDirectoryAsync(2);
            if (rootItems.Count != 1)
            {
                Console.WriteLine($"  Expected 1 item in root, found {rootItems.Count}");
                return false;
            }

            // Verify subdirectory has 1 item
            var subItems = await reader.ListDirectoryAsync(dirCnid);
            if (subItems.Count != 1)
            {
                Console.WriteLine($"  Expected 1 item in SubDir, found {subItems.Count}");
                return false;
            }

            var nested = subItems[0];
            if (nested.Name != "nested.dat" || nested.IsDirectory || nested.Size != 256)
            {
                Console.WriteLine($"  Unexpected: name={nested.Name}, isDir={nested.IsDirectory}, size={nested.Size}");
                return false;
            }

            // Read back data
            var readBuf = new byte[256];
            var readCount = await reader.ReadFileAsync(nested.DataFork!, 0, readBuf, 256);
            if (readCount != 256)
            {
                Console.WriteLine($"  Expected to read 256 bytes, got {readCount}");
                return false;
            }

            for (int i = 0; i < 256; i++)
            {
                if (readBuf[i] != fileData[i])
                {
                    Console.WriteLine($"  Data mismatch at byte {i}");
                    return false;
                }
            }

            Console.WriteLine("  File in subdirectory created and verified.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 6: Create 50 files to exercise B-tree node splitting
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestCreateManyFiles(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var expected = new HashSet<string>();
            for (int i = 0; i < 50; i++)
            {
                var name = $"item_{i:D3}.txt";
                var data = new byte[64];
                new Random(i).NextBytes(data);
                await reader.CreateFileAsync(2, name, data);
                expected.Add(name);

                if ((i + 1) % 10 == 0)
                    Console.WriteLine($"  Created {i + 1}/50 files...");
            }

            var items = await reader.ListDirectoryAsync(2);
            if (items.Count != 50)
            {
                Console.WriteLine($"  Expected 50 items, found {items.Count}");
                // Show which are missing
                var found = new HashSet<string>(items.Select(x => x.Name));
                var missing = expected.Except(found).OrderBy(x => x).ToList();
                var extra = found.Except(expected).OrderBy(x => x).ToList();
                if (missing.Count > 0)
                    Console.WriteLine($"  Missing ({missing.Count}): {string.Join(", ", missing)}");
                if (extra.Count > 0)
                    Console.WriteLine($"  Extra ({extra.Count}): {string.Join(", ", extra)}");
                Console.WriteLine($"  Found: {string.Join(", ", items.Select(x => x.Name).OrderBy(x => x))}");

                // Diagnostic: walk the raw leaf chain to count all records with parentCnid=2
                Console.WriteLine("  Diagnostic: walking raw B-tree leaf chain...");
                await DumpBTreeDiagnostics(reader, device);
                return false;
            }

            Console.WriteLine("  All 50 files created and listed successfully.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 7: Create file, verify, delete, verify gone
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestDeleteFile(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var data = new byte[128];
            new Random(77).NextBytes(data);
            await reader.CreateFileAsync(2, "deleteme.bin", data);

            var items = await reader.ListDirectoryAsync(2);
            if (items.Count != 1)
            {
                Console.WriteLine($"  Before delete: expected 1 item, found {items.Count}");
                return false;
            }

            await reader.DeleteEntryAsync(2, "deleteme.bin");

            items = await reader.ListDirectoryAsync(2);
            if (items.Count != 0)
            {
                Console.WriteLine($"  After delete: expected 0 items, found {items.Count}");
                foreach (var item in items)
                    Console.WriteLine($"    - {item.Name}");
                return false;
            }

            Console.WriteLine("  File created, verified, deleted, verified gone.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 8: Write a 5MB file, read back entire content byte-for-byte
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestLargeFile(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var size = 5 * 1024 * 1024; // 5 MB
            var data = new byte[size];
            new Random(999).NextBytes(data);

            var cnid = await reader.CreateFileAsync(2, "large.bin", data);
            Console.WriteLine($"  Created 5MB file with CNID {cnid}");

            var items = await reader.ListDirectoryAsync(2);
            if (items.Count != 1)
            {
                Console.WriteLine($"  Expected 1 item, found {items.Count}");
                return false;
            }

            var file = items[0];
            if (file.Size != size)
            {
                Console.WriteLine($"  Expected size {size}, got {file.Size}");
                return false;
            }

            // Read back in chunks
            var readBuf = new byte[size];
            var totalRead = 0;
            var chunkSize = 65536;
            while (totalRead < size)
            {
                var toRead = Math.Min(chunkSize, size - totalRead);
                var tempBuf = new byte[toRead];
                var read = await reader.ReadFileAsync(file.DataFork!, totalRead, tempBuf, toRead);
                if (read <= 0)
                {
                    Console.WriteLine($"  Read returned {read} at offset {totalRead}");
                    return false;
                }
                Buffer.BlockCopy(tempBuf, 0, readBuf, totalRead, read);
                totalRead += read;
            }

            // Compare byte-for-byte
            var mismatches = 0;
            var firstMismatch = -1;
            for (int i = 0; i < size; i++)
            {
                if (readBuf[i] != data[i])
                {
                    mismatches++;
                    if (firstMismatch < 0) firstMismatch = i;
                }
            }

            if (mismatches > 0)
            {
                Console.WriteLine($"  {mismatches} byte mismatches, first at offset {firstMismatch}");
                Console.WriteLine($"    Expected: {data[firstMismatch]:X2}, Got: {readBuf[firstMismatch]:X2}");
                return false;
            }

            Console.WriteLine("  5MB file written and verified byte-for-byte.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 9: Write data, overwrite at same offset, verify second write wins
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestOverwriteFile(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var initialData = new byte[4096];
            for (int i = 0; i < initialData.Length; i++) initialData[i] = 0xAA;

            var cnid = await reader.CreateFileAsync(2, "overwrite.bin", initialData);
            Console.WriteLine($"  Created file with CNID {cnid}");

            // Overwrite first 1024 bytes with different data
            var overwriteData = new byte[1024];
            for (int i = 0; i < overwriteData.Length; i++) overwriteData[i] = 0xBB;

            await reader.WriteFileDataAsync(cnid, 0, overwriteData, overwriteData.Length);
            Console.WriteLine("  Overwrote first 1024 bytes");

            // Read back and verify
            var items = await reader.ListDirectoryAsync(2);
            if (items.Count != 1)
            {
                Console.WriteLine($"  Expected 1 item, found {items.Count}");
                return false;
            }

            var file = items[0];
            var readBuf = new byte[4096];
            var readCount = await reader.ReadFileAsync(file.DataFork!, 0, readBuf, 4096);
            if (readCount != 4096)
            {
                Console.WriteLine($"  Expected to read 4096, got {readCount}");
                return false;
            }

            // First 1024 bytes should be 0xBB
            for (int i = 0; i < 1024; i++)
            {
                if (readBuf[i] != 0xBB)
                {
                    Console.WriteLine($"  Byte {i}: expected 0xBB, got 0x{readBuf[i]:X2}");
                    return false;
                }
            }

            // Remaining bytes should be 0xAA
            for (int i = 1024; i < 4096; i++)
            {
                if (readBuf[i] != 0xAA)
                {
                    Console.WriteLine($"  Byte {i}: expected 0xAA, got 0x{readBuf[i]:X2}");
                    return false;
                }
            }

            Console.WriteLine("  Overwrite verified: first 1024=0xBB, rest=0xAA.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 10: Create files, close reader, reopen, verify still there
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestFileSurvivesReopen(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        // Phase 1: create and write
        byte[] fileData;
        {
            using var device = FileBackedBlockDevice.CreateNew(imageFilePath, ImageSize);
            await HfsPlusNativeReader.FormatAsync(device, 0, ImageSize, "TestVolume");

            var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (reader is null)
            {
                Console.WriteLine("  FAIL: Could not open formatted image (phase 1).");
                return false;
            }

            fileData = new byte[512];
            new Random(123).NextBytes(fileData);

            using (reader)
            {
                await reader.CreateFileAsync(2, "persistent.dat", fileData);
                await reader.CreateFolderAsync(2, "persistent_dir");
                Console.WriteLine("  Phase 1: created file and folder.");
            }
        } // device disposed, file closed

        // Phase 2: reopen and verify
        {
            using var device = FileBackedBlockDevice.Open(imageFilePath, writable: false);
            var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (reader is null)
            {
                Console.WriteLine("  FAIL: Could not reopen image (phase 2).");
                return false;
            }

            using (reader)
            {
                var items = await reader.ListDirectoryAsync(2);
                if (items.Count != 2)
                {
                    Console.WriteLine($"  Expected 2 items after reopen, found {items.Count}");
                    foreach (var item in items)
                        Console.WriteLine($"    - {item.Name} (dir={item.IsDirectory})");
                    return false;
                }

                var file = items.Find(i => i.Name == "persistent.dat");
                var dir = items.Find(i => i.Name == "persistent_dir");

                if (file is null)
                {
                    Console.WriteLine("  File 'persistent.dat' not found after reopen.");
                    return false;
                }
                if (dir is null)
                {
                    Console.WriteLine("  Folder 'persistent_dir' not found after reopen.");
                    return false;
                }
                if (file.IsDirectory)
                {
                    Console.WriteLine("  'persistent.dat' should be a file, not a directory.");
                    return false;
                }
                if (!dir.IsDirectory)
                {
                    Console.WriteLine("  'persistent_dir' should be a directory, not a file.");
                    return false;
                }

                // Verify file data
                var readBuf = new byte[512];
                var readCount = await reader.ReadFileAsync(file.DataFork!, 0, readBuf, 512);
                if (readCount != 512)
                {
                    Console.WriteLine($"  Expected to read 512, got {readCount}");
                    return false;
                }

                for (int i = 0; i < 512; i++)
                {
                    if (readBuf[i] != fileData[i])
                    {
                        Console.WriteLine($"  Data mismatch at byte {i} after reopen");
                        return false;
                    }
                }

                Console.WriteLine("  Phase 2: files and data persist across close/reopen.");
                return true;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 11: Deep nested paths — Dir1/Dir2/Dir3/Dir4/file.txt
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestDeepNestedPaths(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var dir1 = await reader.CreateFolderAsync(2, "Dir1");
            Console.WriteLine($"  Created Dir1 with CNID {dir1}");

            var dir2 = await reader.CreateFolderAsync(dir1, "Dir2");
            Console.WriteLine($"  Created Dir2 with CNID {dir2}");

            var dir3 = await reader.CreateFolderAsync(dir2, "Dir3");
            Console.WriteLine($"  Created Dir3 with CNID {dir3}");

            var dir4 = await reader.CreateFolderAsync(dir3, "Dir4");
            Console.WriteLine($"  Created Dir4 with CNID {dir4}");

            var fileData = new byte[256];
            for (int i = 0; i < fileData.Length; i++) fileData[i] = (byte)(i ^ 0x55);
            var fileCnid = await reader.CreateFileAsync(dir4, "file.txt", fileData);
            Console.WriteLine($"  Created file.txt with CNID {fileCnid}");

            // Verify the chain: root has 1 item (Dir1)
            var rootItems = await reader.ListDirectoryAsync(2);
            if (rootItems.Count != 1 || rootItems[0].Name != "Dir1")
            {
                Console.WriteLine($"  Root: expected 1 item 'Dir1', found {rootItems.Count}: {string.Join(", ", rootItems.Select(x => x.Name))}");
                return false;
            }

            // Verify Dir4 has the file
            var dir4Items = await reader.ListDirectoryAsync(dir4);
            if (dir4Items.Count != 1 || dir4Items[0].Name != "file.txt")
            {
                Console.WriteLine($"  Dir4: expected 1 item 'file.txt', found {dir4Items.Count}: {string.Join(", ", dir4Items.Select(x => x.Name))}");
                return false;
            }

            // Verify file data
            var readBuf = new byte[256];
            var readCount = await reader.ReadFileAsync(dir4Items[0].DataFork!, 0, readBuf, 256);
            for (int i = 0; i < 256; i++)
            {
                if (readBuf[i] != fileData[i])
                {
                    Console.WriteLine($"  Data mismatch at byte {i}");
                    return false;
                }
            }

            Console.WriteLine("  Deep nested path created and verified (4 levels deep).");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 12: 100 files in a subdirectory — exercises B-tree splitting
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestManyFilesInSubdirectory(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var subdir = await reader.CreateFolderAsync(2, "BulkDir");
            Console.WriteLine($"  Created BulkDir with CNID {subdir}");

            var expectedFiles = new Dictionary<string, byte[]>();
            for (int i = 0; i < 100; i++)
            {
                var name = $"bulk_{i:D3}.dat";
                var size = 1024 + (i * 1000) % 100000; // 1KB to ~100KB varying
                var data = new byte[size];
                new Random(i + 1000).NextBytes(data);
                expectedFiles[name] = data;

                await reader.CreateFileAsync(subdir, name, data);

                if ((i + 1) % 25 == 0)
                    Console.WriteLine($"  Created {i + 1}/100 files in BulkDir...");
            }

            // Verify all 100 files are listable
            var items = await reader.ListDirectoryAsync(subdir);
            if (items.Count != 100)
            {
                Console.WriteLine($"  Expected 100 items in BulkDir, found {items.Count}");
                var found = new HashSet<string>(items.Select(x => x.Name));
                var missing = expectedFiles.Keys.Except(found).OrderBy(x => x).Take(10).ToList();
                if (missing.Count > 0)
                    Console.WriteLine($"  First missing: {string.Join(", ", missing)}");
                await DumpBTreeDiagnostics(reader, device);
                return false;
            }

            // Verify a sample of files are readable with correct data
            var sampled = 0;
            foreach (var item in items)
            {
                if (sampled >= 10) break; // spot-check 10 files for speed
                if (!expectedFiles.TryGetValue(item.Name, out var expected)) continue;

                if (item.Size != expected.Length)
                {
                    Console.WriteLine($"  {item.Name}: size mismatch, expected {expected.Length}, got {item.Size}");
                    return false;
                }

                if (item.DataFork is null)
                {
                    Console.WriteLine($"  {item.Name}: DataFork is null");
                    return false;
                }

                var readBuf = new byte[expected.Length];
                var readCount = await reader.ReadFileAsync(item.DataFork, 0, readBuf, expected.Length);
                if (readCount != expected.Length)
                {
                    Console.WriteLine($"  {item.Name}: read {readCount} bytes, expected {expected.Length}");
                    return false;
                }

                for (int i = 0; i < expected.Length; i++)
                {
                    if (readBuf[i] != expected[i])
                    {
                        Console.WriteLine($"  {item.Name}: data mismatch at byte {i}");
                        return false;
                    }
                }

                sampled++;
            }

            Console.WriteLine($"  All 100 files in subdirectory created and verified ({sampled} spot-checked).");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 13: Mixed create pattern — interleaved root and subdirectory
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestMixedCreatePattern(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var subdir = await reader.CreateFolderAsync(2, "MixedDir");
            Console.WriteLine($"  Created MixedDir with CNID {subdir}");

            var rootFiles = new HashSet<string>();
            var subdirFiles = new HashSet<string>();

            for (int i = 0; i < 20; i++)
            {
                // Create in root
                var rootName = $"root_{i:D2}.txt";
                var rootData = new byte[512];
                new Random(i).NextBytes(rootData);
                await reader.CreateFileAsync(2, rootName, rootData);
                rootFiles.Add(rootName);

                // Create in subdir
                var subName = $"sub_{i:D2}.txt";
                var subData = new byte[512];
                new Random(i + 100).NextBytes(subData);
                await reader.CreateFileAsync(subdir, subName, subData);
                subdirFiles.Add(subName);

                if ((i + 1) % 10 == 0)
                    Console.WriteLine($"  Created {i + 1}/20 pairs (root + subdir)...");
            }

            // Verify root: should have 20 files + 1 directory = 21 items
            var rootItems = await reader.ListDirectoryAsync(2);
            if (rootItems.Count != 21)
            {
                Console.WriteLine($"  Root: expected 21 items (20 files + 1 dir), found {rootItems.Count}");
                var found = new HashSet<string>(rootItems.Select(x => x.Name));
                var expectedAll = new HashSet<string>(rootFiles) { "MixedDir" };
                var missing = expectedAll.Except(found).OrderBy(x => x).ToList();
                if (missing.Count > 0)
                    Console.WriteLine($"  Missing from root: {string.Join(", ", missing)}");
                await DumpBTreeDiagnostics(reader, device);
                return false;
            }

            // Verify subdir: should have 20 files
            var subItems = await reader.ListDirectoryAsync(subdir);
            if (subItems.Count != 20)
            {
                Console.WriteLine($"  MixedDir: expected 20 items, found {subItems.Count}");
                var found = new HashSet<string>(subItems.Select(x => x.Name));
                var missing = subdirFiles.Except(found).OrderBy(x => x).ToList();
                if (missing.Count > 0)
                    Console.WriteLine($"  Missing from subdir: {string.Join(", ", missing)}");
                await DumpBTreeDiagnostics(reader, device);
                return false;
            }

            Console.WriteLine("  Mixed create pattern verified: 21 in root, 20 in subdir.");
            return true;
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 14: Catalog growth — 200 files with long names to exhaust
    //          initial catalog nodes and force GrowCatalogFileAsync,
    //          then reopen to verify extent persistence.
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestCatalogGrowth(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const int fileCount = 200;
        var expectedNames = new HashSet<string>();

        // Phase 1: create files with long names (100-char) to consume node space faster
        // and force the catalog B-tree to grow beyond 128 initial nodes.
        {
            using var device = FileBackedBlockDevice.CreateNew(imageFilePath, ImageSize);
            await HfsPlusNativeReader.FormatAsync(device, 0, ImageSize, "TestVolume");

            var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (reader is null)
            {
                Console.WriteLine("  FAIL: Could not open formatted image (phase 1).");
                return false;
            }

            using (reader)
            {
                for (int i = 0; i < fileCount; i++)
                {
                    // Use 100-char names: large keys force more splits and faster node exhaustion.
                    // Each file record + thread record with 100-char name uses ~1200 bytes,
                    // so ~6 files per leaf node => 128 nodes exhausted around 380 files with overhead.
                    var name = $"growth_{i:D4}_" + new string((char)('a' + (i % 26)), 85) + ".bin";
                    var data = new byte[64];
                    new Random(i + 5000).NextBytes(data);
                    await reader.CreateFileAsync(2, name, data);
                    expectedNames.Add(name);

                    if ((i + 1) % 50 == 0)
                        Console.WriteLine($"  Phase 1: Created {i + 1}/{fileCount} files...");
                }

                // Verify all files are listable before closing
                var items = await reader.ListDirectoryAsync(2);
                if (items.Count != fileCount)
                {
                    Console.WriteLine($"  Phase 1: Expected {fileCount} items, found {items.Count}");
                    var found = new HashSet<string>(items.Select(x => x.Name));
                    var missing = expectedNames.Except(found).OrderBy(x => x).Take(10).ToList();
                    if (missing.Count > 0)
                        Console.WriteLine($"  First missing: {string.Join(", ", missing)}");
                    await DumpBTreeDiagnostics(reader, device);
                    return false;
                }
                Console.WriteLine($"  Phase 1: All {fileCount} files created and listed.");
            }
        }

        // Phase 2: reopen and verify all files survived (tests FlushVolumeHeaderAsync
        // catalog extent persistence after growth)
        {
            using var device = FileBackedBlockDevice.Open(imageFilePath, writable: false);
            var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (reader is null)
            {
                Console.WriteLine("  FAIL: Could not reopen image (phase 2).");
                return false;
            }

            using (reader)
            {
                var items = await reader.ListDirectoryAsync(2);
                if (items.Count != fileCount)
                {
                    Console.WriteLine($"  Phase 2 (reopen): Expected {fileCount} items, found {items.Count}");
                    return false;
                }

                // Spot-check 5 files for correct data
                var rng = new Random(42);
                for (int check = 0; check < 5; check++)
                {
                    var idx = rng.Next(fileCount);
                    var name = $"growth_{idx:D4}_" + new string((char)('a' + (idx % 26)), 85) + ".bin";
                    var item = items.Find(x => x.Name == name);
                    if (item is null)
                    {
                        Console.WriteLine($"  Phase 2: File '{name}' not found.");
                        return false;
                    }
                    if (item.DataFork is null)
                    {
                        Console.WriteLine($"  Phase 2: File '{name}' has null DataFork.");
                        return false;
                    }
                    var readBuf = new byte[64];
                    var readCount = await reader.ReadFileAsync(item.DataFork, 0, readBuf, 64);
                    if (readCount != 64)
                    {
                        Console.WriteLine($"  Phase 2: File '{name}': expected 64 bytes, read {readCount}.");
                        return false;
                    }
                    var expectedData = new byte[64];
                    new Random(idx + 5000).NextBytes(expectedData);
                    for (int b = 0; b < 64; b++)
                    {
                        if (readBuf[b] != expectedData[b])
                        {
                            Console.WriteLine($"  Phase 2: File '{name}': data mismatch at byte {b}.");
                            return false;
                        }
                    }
                }

                Console.WriteLine($"  Phase 2: All {fileCount} files verified after reopen.");
            }
        }

        Console.WriteLine("  Catalog growth test passed: files survive close/reopen.");
        return true;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 15: Real-world Windows Explorer copy pattern
    // Mimics the exact failure mode the user hit: files in subfolders with
    // UUID-style randomly-ordered names, varying sizes, Create-then-Write-in-
    // chunks instead of Create-with-data-in-one-shot. This is what WinFsp does
    // when Explorer copies files in.
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestExplorerCopyPattern(string imageFilePath)
    {
        // Larger image to allow MB-to-tens-of-MB file copies, the size range
        // where the user's failure surfaced.
        const long imageSize = 2L * 1024 * 1024 * 1024; // 2 GB
        if (File.Exists(imageFilePath)) File.Delete(imageFilePath);

        using var device = FileBackedBlockDevice.CreateNew(imageFilePath, imageSize);
        await HfsPlusNativeReader.FormatAsync(device, 0, imageSize, "TestVolume");

        var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
        if (reader is null)
        {
            Console.WriteLine("  FAIL: open after format returned null.");
            return false;
        }

        using (reader)
        {
            // Two subfolders, like the user's "Mature" and "Celebs"
            var folderA = await reader.CreateFolderAsync(2, "Mature");
            var folderB = await reader.CreateFolderAsync(2, "Celebs");
            Console.WriteLine($"  Created folders: Mature(cnid={folderA}), Celebs(cnid={folderB})");

            // Sizes closer to the user's real workload: 250 KB to 200 MB.
            var fileSpecs = new (uint Folder, string Name, long Size)[]
            {
                (folderA, "6.mp4", 20_100_000L),                                          // 20.1 MB
                (folderA, "10041976-7529a11be7f18478cfabfcd8ecab1a25.mp4", 182_000_000L), // 182 MB
                (folderB, "6AE767AA-DF45-4508-ACBB-7F3EB88E162F.MP4", 875_000L),
                (folderB, "7A19A067-EB90-4BC1-A035-C9834B3CCF62.MP4", 1_800_000L),
                (folderB, "2025-01-04 12.37.22.jpg", 250_000L),
                (folderA, "Admiregirls.com__0hblegzifvhe0v4mdqawn_source__Admiregirls.com.mp4", 35_000_000L),
                (folderA, "vid_001.mp4", 600_000L),
                (folderA, "vid_002.mp4", 5_200_000L),
                (folderB, "img_a.jpg", 300_000L),
                (folderB, "img_b.jpg", 450_000L),
                (folderA, "deep_nested_name_with_many_chars_to_grow_keys.bin", 12_500_000L),
                (folderB, "B47C29D1-FAFE-4B0E-9E02-1A33EE56CC8A.mp4", 50_100_000L),
            };

            // CRITICAL: NO SetFileSize call here. This matches the user's flow exactly —
            // the broker debug log showed Windows Explorer creating files with alloc=0,
            // meaning no pre-allocation. Writes incrementally extend the file, each one
            // allocating a new extent slot. With only 8 inline extent slots in the HFS+
            // catalog file record, a file written in more than 8 chunks fails.

            // Phase 1: 50 small UUID-named files alternating between folders.
            var rng = new Random(12345);
            var phase1Names = new List<(uint Folder, string Name, uint Cnid)>();
            for (int i = 0; i < 50; i++)
            {
                var folder = (i % 2 == 0) ? folderA : folderB;
                var uuid = Guid.NewGuid().ToString().ToUpperInvariant();
                var name = $"{uuid}.mp4";
                var size = 250_000L + rng.Next(750_000); // 250 KB - 1 MB
                Console.WriteLine($"  [P1 {i+1}/50] {(folder == folderA ? "Mature" : "Celebs")}/{name.Substring(0, 16)}... ({size:N0} bytes)");

                var cnid = await reader.CreateFileAsync(folder, name, initialData: null);
                // NO SetFileSize — write extends incrementally
                const int chunkSize = 1 * 1024 * 1024;
                long offset = 0;
                int chunkIdx = 0;
                while (offset < size)
                {
                    var thisChunk = (int)Math.Min(chunkSize, size - offset);
                    var buf = new byte[thisChunk];
                    new Random((int)(cnid * 1000 + chunkIdx)).NextBytes(buf);
                    await reader.WriteFileDataAsync(cnid, offset, buf, thisChunk);
                    offset += thisChunk;
                    chunkIdx++;
                }
                phase1Names.Add((folder, name, cnid));
            }

            Console.WriteLine($"  Phase 1 complete: {phase1Names.Count} small files. Now interleaving large copies...");

            // Phase 2: large files written in many 1 MB chunks — this is what
            // exhausts the 8 inline extent slots.
            foreach (var (folder, name, totalSize) in fileSpecs)
            {
                Console.WriteLine($"  [P2] Creating {(folder == folderA ? "Mature" : "Celebs")}/{name} ({totalSize:N0} bytes)...");

                var cnid = await reader.CreateFileAsync(folder, name, initialData: null);
                // NO SetFileSize

                const int chunkSize = 1 * 1024 * 1024;
                long offset = 0;
                int chunkIdx = 0;
                while (offset < totalSize)
                {
                    var thisChunk = (int)Math.Min(chunkSize, totalSize - offset);
                    var buf = new byte[thisChunk];
                    new Random((int)(cnid * 1000 + chunkIdx)).NextBytes(buf);
                    await reader.WriteFileDataAsync(cnid, offset, buf, thisChunk);
                    offset += thisChunk;
                    chunkIdx++;
                }

                Console.WriteLine($"    OK ({chunkIdx} chunks, cnid={cnid})");
            }

            // Verify file counts match (phase1 + phase2)
            var matureItems = await reader.ListDirectoryAsync(folderA);
            var celebsItems = await reader.ListDirectoryAsync(folderB);
            var expectedMature = fileSpecs.Count(s => s.Folder == folderA) + phase1Names.Count(n => n.Folder == folderA);
            var expectedCelebs = fileSpecs.Count(s => s.Folder == folderB) + phase1Names.Count(n => n.Folder == folderB);
            Console.WriteLine($"  Mature: {matureItems.Count}/{expectedMature}, Celebs: {celebsItems.Count}/{expectedCelebs}");

            if (matureItems.Count != expectedMature)
            {
                Console.WriteLine($"  FAIL: Mature folder count mismatch. Got {matureItems.Count}, expected {expectedMature}.");
                var found = new HashSet<string>(matureItems.Select(x => x.Name));
                var expected = fileSpecs.Where(s => s.Folder == folderA).Select(s => s.Name).ToHashSet();
                foreach (var missing in expected.Except(found))
                    Console.WriteLine($"    MISSING: {missing}");
                await DumpBTreeDiagnostics(reader, device);
                return false;
            }
            if (celebsItems.Count != expectedCelebs)
            {
                Console.WriteLine($"  FAIL: Celebs folder count mismatch. Got {celebsItems.Count}, expected {expectedCelebs}.");
                var found = new HashSet<string>(celebsItems.Select(x => x.Name));
                var expected = fileSpecs.Where(s => s.Folder == folderB).Select(s => s.Name).ToHashSet();
                foreach (var missing in expected.Except(found))
                    Console.WriteLine($"    MISSING: {missing}");
                await DumpBTreeDiagnostics(reader, device);
                return false;
            }

            // Spot-check a few files: read first chunk back, verify it deserialises and
            // matches the Random stream we wrote.
            foreach (var (folder, name, totalSize) in fileSpecs.Take(3))
            {
                var items = folder == folderA ? matureItems : celebsItems;
                var entry = items.FirstOrDefault(i => i.Name == name);
                if (entry is null)
                {
                    Console.WriteLine($"  FAIL: Could not find entry for {name} during readback.");
                    return false;
                }
                if (entry.DataFork is null || entry.DataFork.LogicalSize != totalSize)
                {
                    Console.WriteLine($"  FAIL: {name} size mismatch. DataFork={entry.DataFork?.LogicalSize.ToString() ?? "null"}, expected {totalSize}.");
                    return false;
                }
            }

            Console.WriteLine($"  Explorer copy pattern verified: {fileSpecs.Length} files across 2 folders, varied sizes, all readable.");
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 16: Mirror the user's EXACT failure sequence from the broker debug log:
    //   Mature/, files in Mature with writes (some HUGE), then Celebs/ created LATER,
    //   then small files in Celebs with writes. Simulates the cnid 23 failure.
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestUserExactSequence(string imageFilePath)
    {
        const long imageSize = 4L * 1024 * 1024 * 1024; // 4 GB to fit big test files
        if (File.Exists(imageFilePath)) File.Delete(imageFilePath);

        using var device = FileBackedBlockDevice.CreateNew(imageFilePath, imageSize);
        await HfsPlusNativeReader.FormatAsync(device, 0, imageSize, "TestVolume");

        var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
        if (reader is null) return false;

        async Task WriteInChunksAsync(uint cnid, long totalSize)
        {
            const int chunkSize = 1 * 1024 * 1024;
            long offset = 0;
            while (offset < totalSize)
            {
                var thisChunk = (int)Math.Min(chunkSize, totalSize - offset);
                var buf = new byte[thisChunk];
                new Random((int)(cnid * 1000 + offset / chunkSize)).NextBytes(buf);
                await reader.WriteFileDataAsync(cnid, offset, buf, thisChunk);
                offset += thisChunk;
            }
        }

        using (reader)
        {
            // Step 1: Mature folder
            var matureCnid = await reader.CreateFolderAsync(2, "Mature");
            Console.WriteLine($"  Mature cnid={matureCnid}");

            // Step 2: 6.mp4 (HUGE — 1.5 GB to mimic user's video file)
            var sixCnid = await reader.CreateFileAsync(matureCnid, "6.mp4");
            Console.WriteLine($"  6.mp4 cnid={sixCnid}");
            await WriteInChunksAsync(sixCnid, 1_500_000_000);

            // Step 3: Admiregirls 1 (~400 MB)
            var ag1Cnid = await reader.CreateFileAsync(matureCnid, "Admiregirls.com__0hblegzifvhe0v4mdqawn_source__Admiregirls.com.mp4");
            Console.WriteLine($"  AG1 cnid={ag1Cnid}");
            await WriteInChunksAsync(ag1Cnid, 400_000_000);

            // Step 4: Admiregirls 2 (~300 MB)
            var ag2Cnid = await reader.CreateFileAsync(matureCnid, "Admiregirls.com__0hbmzresjfy1bovfnk2bx_source__Admiregirls.com.mp4");
            Console.WriteLine($"  AG2 cnid={ag2Cnid}");
            await WriteInChunksAsync(ag2Cnid, 300_000_000);

            // Step 5: Celebs folder — created LATE, after MUCH catalog activity in Mature
            var celebsCnid = await reader.CreateFolderAsync(2, "Celebs");
            Console.WriteLine($"  Celebs cnid={celebsCnid}");

            // Step 6: .DS_Store (small)
            var dsCnid = await reader.CreateFileAsync(celebsCnid, ".DS_Store");
            Console.WriteLine($"  .DS_Store cnid={dsCnid}");
            await WriteInChunksAsync(dsCnid, 6148);

            // Step 7: 1C4EA5F6 file (3.7 MB — 4 chunks)
            var firstUuidCnid = await reader.CreateFileAsync(celebsCnid, "1C4EA5F6-4FB1-4000-9D2A-A66173DEAD90.MP4");
            Console.WriteLine($"  1C4EA5F6 cnid={firstUuidCnid}");
            await WriteInChunksAsync(firstUuidCnid, 3_920_212);

            // Step 8: THE CRITICAL ONE — 2025-01-04 12.36.36.jpg (100 KB)
            // After Create succeeded for the user, Write failed with "Cannot find thread record"
            var jpgCnid = await reader.CreateFileAsync(celebsCnid, "2025-01-04 12.36.36.jpg");
            Console.WriteLine($"  2025-01-04 12.36.36.jpg cnid={jpgCnid}");

            // Try to find the thread record IMMEDIATELY before writing — this is the
            // sanity check that maps to the user's failure mode.
            try
            {
                await reader.WriteFileDataAsync(jpgCnid, 0, new byte[102476], 102476);
                Console.WriteLine($"  Write succeeded for jpg cnid={jpgCnid}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL: Write failed: {ex.Message}");
                Console.WriteLine($"  Dumping B-tree state to diagnose:");
                await DumpBTreeDiagnostics(reader, device);
                return false;
            }

            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 17: Long filenames — 50, 100, 200, and 255 characters
    // ════════════════════════════════════════════════════════════════════════
    private static async Task<bool> TestLongFilenames(string imageFilePath)
    {
        return await WithFormattedImage(imageFilePath, async (reader, device) =>
        {
            var lengths = new[] { 50, 100, 200, 255 };
            var createdNames = new List<string>();

            foreach (var len in lengths)
            {
                // Build a name of the specified length with a .txt suffix
                var prefix = new string('A', len - 4) + ".txt";
                if (prefix.Length > len) prefix = prefix.Substring(0, len);
                var name = prefix;
                createdNames.Add(name);

                var data = new byte[128];
                new Random(len).NextBytes(data);

                await reader.CreateFileAsync(2, name, data);
                Console.WriteLine($"  Created file with {name.Length}-char name");
            }

            // Verify all are listable
            var items = await reader.ListDirectoryAsync(2);
            if (items.Count != lengths.Length)
            {
                Console.WriteLine($"  Expected {lengths.Length} items, found {items.Count}");
                foreach (var item in items)
                    Console.WriteLine($"    - '{item.Name}' ({item.Name.Length} chars)");
                return false;
            }

            // Verify names match
            foreach (var expectedName in createdNames)
            {
                var found = items.Find(i => i.Name == expectedName);
                if (found is null)
                {
                    Console.WriteLine($"  File with {expectedName.Length}-char name not found in listing.");
                    Console.WriteLine($"  Listed names: {string.Join(", ", items.Select(x => $"'{x.Name}'({x.Name.Length})"))}");
                    return false;
                }
                if (found.Size != 128)
                {
                    Console.WriteLine($"  File with {expectedName.Length}-char name: expected size 128, got {found.Size}");
                    return false;
                }
            }

            Console.WriteLine($"  Long filename test passed: created files with names of {string.Join(", ", lengths)} chars.");
            return true;
        });
    }

    // ─── Diagnostic helper ──────────────────────────────────────────────────

    private static async Task DumpBTreeDiagnostics(HfsPlusNativeReader reader, FileBackedBlockDevice device)
    {
        var diag = await reader.GetBTreeDiagnosticsAsync();
        Console.WriteLine($"  B-tree state: root={diag.RootNodeIndex}, firstLeaf={diag.FirstLeafNodeIndex}, " +
                          $"lastLeaf={diag.LastLeafNode}, total={diag.TotalNodes}, free={diag.FreeNodes}, " +
                          $"leafRecords={diag.LeafRecords}, extents={diag.CatalogExtentCount}");
        if (diag.HasCycle) Console.WriteLine("  WARNING: cycle detected in leaf chain!");
        if (diag.UnreadableNodes.Count > 0) Console.WriteLine($"  WARNING: unreadable nodes: {string.Join(", ", diag.UnreadableNodes)}");

        Console.WriteLine($"  Index nodes ({diag.IndexNodes.Count}):");
        foreach (var idx in diag.IndexNodes)
        {
            Console.WriteLine($"    Node {idx.NodeIndex}: {idx.NumRecords} records");
            foreach (var (pid, name, child) in idx.Children)
            {
                Console.WriteLine($"      key=({pid},\"{name}\") -> child {child}");
            }
        }

        Console.WriteLine($"  Leaf chain ({diag.LeafNodes.Count} nodes):");
        int totalParent2Records = 0;
        foreach (var leaf in diag.LeafNodes)
        {
            var p2Count = leaf.RecordParentCnids.Count(c => c == 2);
            totalParent2Records += p2Count;
            Console.WriteLine($"    Node {leaf.NodeIndex}: {leaf.NumRecords} records, " +
                              $"fLink={leaf.FLink}, parentCnids={string.Join(",", leaf.RecordParentCnids.Distinct().OrderBy(x=>x))} " +
                              $"(parent=2 count: {p2Count})");
        }
        Console.WriteLine($"  Total records with parentCnid=2 across leaf chain: {totalParent2Records}");
    }
}
