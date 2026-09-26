using System.Buffers.Binary;
using CrossDrive.RawDiskEngine;

namespace CrossDrive.HfsWriteTest;

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
        results.Add(("TestMountHfsPlusResourceForkAppleDouble", await RunTest("TestMountHfsPlusResourceForkAppleDouble", () => TestMountHfsPlusResourceForkAppleDouble(imageFilePath))));
        results.Add(("TestMountHfsPlusFinderInfoAppleDouble", await RunTest("TestMountHfsPlusFinderInfoAppleDouble", () => TestMountHfsPlusFinderInfoAppleDouble(imageFilePath))));
        results.Add(("TestMountHfsPlusSymlinkReparsePoint", await RunTest("TestMountHfsPlusSymlinkReparsePoint", () => TestMountHfsPlusSymlinkReparsePoint(imageFilePath))));
        results.Add(("TestMountHfsxReadOnlyBrowsing", await RunTest("TestMountHfsxReadOnlyBrowsing", () => TestMountHfsxReadOnlyBrowsing(imageFilePath))));
        results.Add(("TestHfsxCatalogIndexCompareIsCaseSensitive", await RunTest("TestHfsxCatalogIndexCompareIsCaseSensitive", () => TestHfsxCatalogIndexCompareIsCaseSensitive(imageFilePath))));
        results.Add(("TestHfsxCaseSensitiveCacheKeepsDistinctNames", await RunTest("TestHfsxCaseSensitiveCacheKeepsDistinctNames", TestHfsxCaseSensitiveCacheKeepsDistinctNames)));
        results.Add(("TestMountHfsPlusResourceForkExtentsOverflow", await RunTest("TestMountHfsPlusResourceForkExtentsOverflow", () => TestMountHfsPlusResourceForkExtentsOverflow(imageFilePath))));
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
        results.Add(("TestAnalyzeApmClassicHfs", await RunTest("TestAnalyzeApmClassicHfs", () => TestAnalyzeApmClassicHfs(imageFilePath))));
        results.Add(("TestMountApmClassicHfsReadOnly", await RunTest("TestMountApmClassicHfsReadOnly", () => TestMountApmClassicHfsReadOnly(imageFilePath))));
        results.Add(("TestMountApmClassicHfsExtentsOverflow", await RunTest("TestMountApmClassicHfsExtentsOverflow", () => TestMountApmClassicHfsExtentsOverflow(imageFilePath))));
        results.Add(("TestMountApmClassicHfsResourceForkAppleDouble", await RunTest("TestMountApmClassicHfsResourceForkAppleDouble", () => TestMountApmClassicHfsResourceForkAppleDouble(imageFilePath))));
        results.Add(("TestMountApmClassicHfsFinderInfoAppleDouble", await RunTest("TestMountApmClassicHfsFinderInfoAppleDouble", () => TestMountApmClassicHfsFinderInfoAppleDouble(imageFilePath))));
        results.Add(("TestMountApmClassicHfsResourceForkExtentsOverflow", await RunTest("TestMountApmClassicHfsResourceForkExtentsOverflow", () => TestMountApmClassicHfsResourceForkExtentsOverflow(imageFilePath))));
        results.Add(("TestMountApmClassicHfsMacRomanFilename", await RunTest("TestMountApmClassicHfsMacRomanFilename", () => TestMountApmClassicHfsMacRomanFilename(imageFilePath))));
        results.Add(("TestMountApmClassicHfsCatalogLeafChain", await RunTest("TestMountApmClassicHfsCatalogLeafChain", () => TestMountApmClassicHfsCatalogLeafChain(imageFilePath))));

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

    private static async Task<bool> TestMountHfsPlusResourceForkAppleDouble(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        var data = System.Text.Encoding.UTF8.GetBytes("hello from hfs plus data fork\n");
        var resource = System.Text.Encoding.UTF8.GetBytes("hfs plus resource fork payload\n");

        using (var device = FileBackedBlockDevice.CreateNew(imageFilePath, ImageSize))
        {
            await HfsPlusNativeReader.FormatAsync(device, 0, ImageSize, "TestVolume");
            var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (reader is null)
            {
                Console.WriteLine("  FAIL: Could not open formatted image.");
                return false;
            }

            using (reader)
            {
                await reader.CreateFileAsync(2, "Forked.txt", data);
            }
        }

        await PatchHfsPlusResourceForkAsync(imageFilePath, "Forked.txt", resource);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        if (!string.Equals(plan.FileSystemType, "HFS+", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Expected HFS+, got {plan.FileSystemType}. Notes: {plan.Notes}");
            return false;
        }

        using var provider = await engine.CreateFileSystemProviderAsync(plan);
        var rootEntries = provider.ListDirectory("\\");

        var entry = provider.GetEntry("\\Forked.txt");
        if (entry is null || entry.Size != data.Length)
        {
            Console.WriteLine($"  Expected Forked.txt size {data.Length}, got {entry?.Size.ToString() ?? "missing"}");
            return false;
        }

        if (!rootEntries.Any(e => string.Equals(e.Name, "._Forked.txt", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"  Expected listed AppleDouble sidecar ._Forked.txt. Got: {string.Join(", ", rootEntries.Select(e => e.Name))}");
            return false;
        }

        var sidecar = provider.GetEntry("\\._Forked.txt");
        if (sidecar is null)
        {
            Console.WriteLine("  Expected AppleDouble sidecar ._Forked.txt for HFS+ resource fork.");
            return false;
        }
        if (sidecar.Size != 38 + resource.Length)
        {
            Console.WriteLine($"  Expected AppleDouble sidecar size {38 + resource.Length}, got {sidecar.Size}");
            return false;
        }
        if (!sidecar.Attributes.HasFlag(FileAttributes.ReadOnly) ||
            !sidecar.Attributes.HasFlag(FileAttributes.Hidden))
        {
            Console.WriteLine($"  Expected read-only hidden sidecar attributes, got {sidecar.Attributes}");
            return false;
        }

        var dataRead = new byte[data.Length];
        var dataReadCount = provider.ReadFile("\\Forked.txt", 0, dataRead);
        if (dataReadCount != data.Length || !dataRead.SequenceEqual(data))
        {
            Console.WriteLine($"  Expected to read {data.Length} data-fork bytes, got {dataReadCount}.");
            return false;
        }

        var appleDouble = new byte[(int)sidecar.Size];
        var read = provider.ReadFile("\\._Forked.txt", 0, appleDouble);
        if (read != appleDouble.Length)
        {
            Console.WriteLine($"  Expected to read {appleDouble.Length} AppleDouble bytes, got {read}");
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(4, 4));
        var entryCount = BinaryPrimitives.ReadUInt16BigEndian(appleDouble.AsSpan(24, 2));
        var entryId = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(26, 4));
        var entryOffset = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(30, 4));
        var entryLength = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(34, 4));
        if (magic != 0x00051607 || version != 0x00020000 || entryCount != 1 || entryId != 2 ||
            entryOffset != 38 || entryLength != resource.Length)
        {
            Console.WriteLine($"  Invalid AppleDouble header: magic=0x{magic:X8}, version=0x{version:X8}, entries={entryCount}, id={entryId}, offset={entryOffset}, len={entryLength}");
            return false;
        }

        if (!appleDouble.Skip((int)entryOffset).Take(resource.Length).SequenceEqual(resource))
        {
            Console.WriteLine("  HFS+ resource fork payload did not match AppleDouble entry data.");
            return false;
        }

        var partial = new byte[12];
        var partialRead = provider.ReadFile("\\._Forked.txt", 38 + 5, partial);
        if (partialRead != partial.Length || !partial.SequenceEqual(resource.Skip(5).Take(partial.Length)))
        {
            Console.WriteLine($"  Expected partial HFS+ resource-fork read, got {partialRead} bytes.");
            return false;
        }

        Console.WriteLine("  HFS+ resource fork is exposed as a valid read-only AppleDouble sidecar.");
        return true;
    }

    private static async Task<bool> TestMountHfsPlusFinderInfoAppleDouble(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        var data = System.Text.Encoding.UTF8.GetBytes("hfs plus finder info data\n");
        var resource = System.Text.Encoding.UTF8.GetBytes("finder info resource payload\n");
        var finderInfo = new byte[32];
        System.Text.Encoding.ASCII.GetBytes("TEXTttxt").CopyTo(finderInfo, 0);
        for (var i = 8; i < finderInfo.Length; i++)
        {
            finderInfo[i] = (byte)(0xA0 + i);
        }

        using (var device = FileBackedBlockDevice.CreateNew(imageFilePath, ImageSize))
        {
            await HfsPlusNativeReader.FormatAsync(device, 0, ImageSize, "TestVolume");
            var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (reader is null)
            {
                Console.WriteLine("  FAIL: Could not open formatted image.");
                return false;
            }

            using (reader)
            {
                await reader.CreateFileAsync(2, "FinderInfo.txt", data);
            }
        }

        await PatchHfsPlusResourceForkAsync(imageFilePath, "FinderInfo.txt", resource, finderInfo: finderInfo);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        using var provider = await engine.CreateFileSystemProviderAsync(plan);

        var sidecar = provider.GetEntry("\\._FinderInfo.txt");
        if (sidecar is null || sidecar.Size != 82 + resource.Length)
        {
            Console.WriteLine($"  Expected Finder Info AppleDouble sidecar size {82 + resource.Length}, got {sidecar?.Size.ToString() ?? "missing"}");
            return false;
        }

        var appleDouble = new byte[(int)sidecar.Size];
        var read = provider.ReadFile("\\._FinderInfo.txt", 0, appleDouble);
        if (read != appleDouble.Length)
        {
            Console.WriteLine($"  Expected to read {appleDouble.Length} Finder Info AppleDouble bytes, got {read}");
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(4, 4));
        var entryCount = BinaryPrimitives.ReadUInt16BigEndian(appleDouble.AsSpan(24, 2));
        var finderEntryId = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(26, 4));
        var finderOffset = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(30, 4));
        var finderLength = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(34, 4));
        var resourceEntryId = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(38, 4));
        var resourceOffset = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(42, 4));
        var resourceLength = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(46, 4));
        if (magic != 0x00051607 || version != 0x00020000 || entryCount != 2 ||
            finderEntryId != 9 || finderOffset != 50 || finderLength != 32 ||
            resourceEntryId != 2 || resourceOffset != 82 || resourceLength != resource.Length)
        {
            Console.WriteLine($"  Invalid Finder Info AppleDouble header: entries={entryCount}, finder=({finderEntryId},{finderOffset},{finderLength}), resource=({resourceEntryId},{resourceOffset},{resourceLength})");
            return false;
        }

        if (!appleDouble.Skip((int)finderOffset).Take(32).SequenceEqual(finderInfo))
        {
            Console.WriteLine("  HFS+ Finder Info payload did not match AppleDouble entry data.");
            return false;
        }
        if (!appleDouble.Skip((int)resourceOffset).Take(resource.Length).SequenceEqual(resource))
        {
            Console.WriteLine("  HFS+ resource fork payload did not match shifted AppleDouble entry data.");
            return false;
        }

        var partial = new byte[10];
        var partialRead = provider.ReadFile("\\._FinderInfo.txt", finderOffset + 7, partial);
        if (partialRead != partial.Length || !partial.SequenceEqual(finderInfo.Skip(7).Take(partial.Length)))
        {
            Console.WriteLine($"  Expected partial Finder Info read, got {partialRead} bytes.");
            return false;
        }

        Console.WriteLine("  HFS+ Finder Info is preserved in AppleDouble sidecars.");
        return true;
    }

    private static async Task<bool> TestMountHfsPlusSymlinkReparsePoint(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const string linkName = "LinkToTarget";
        const string target = "../Target.txt";
        var symlinkPayload = System.Text.Encoding.UTF8.GetBytes(target);

        using (var device = FileBackedBlockDevice.CreateNew(imageFilePath, ImageSize))
        {
            await HfsPlusNativeReader.FormatAsync(device, 0, ImageSize, "TestVolume");
            var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (reader is null)
            {
                Console.WriteLine("  FAIL: Could not open formatted image.");
                return false;
            }

            using (reader)
            {
                await reader.CreateFileAsync(2, "Target.txt", System.Text.Encoding.UTF8.GetBytes("target data\n"));
                await reader.CreateFileAsync(2, linkName, symlinkPayload);
            }
        }

        await PatchHfsPlusResourceForkAsync(
            imageFilePath,
            linkName,
            Array.Empty<byte>(),
            fileMode: 0xA1FF);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        using var provider = await engine.CreateFileSystemProviderAsync(plan);

        var entries = provider.ListDirectory("\\");
        var link = entries.FirstOrDefault(e => string.Equals(e.Name, linkName, StringComparison.Ordinal));
        if (link is null)
        {
            Console.WriteLine($"  Expected listed HFS+ symlink {linkName}. Got: {string.Join(", ", entries.Select(e => e.Name))}");
            return false;
        }

        if (!link.IsSymbolicLink ||
            !string.Equals(link.SymlinkTarget, target, StringComparison.Ordinal) ||
            !link.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            link.Size != 0)
        {
            Console.WriteLine($"  Invalid HFS+ symlink metadata: target='{link.SymlinkTarget}', attrs={link.Attributes}, size={link.Size}");
            return false;
        }

        var readBuffer = new byte[symlinkPayload.Length];
        var read = provider.ReadFile($"\\{linkName}", 0, readBuffer);
        if (read != 0)
        {
            Console.WriteLine($"  Expected HFS+ symlink to expose reparse metadata instead of target bytes, read {read}.");
            return false;
        }

        Console.WriteLine("  HFS+ symlinks preserve target text as WinFsp reparse-point metadata.");
        return true;
    }

    private static async Task<bool> TestMountHfsxReadOnlyBrowsing(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const string fileName = "MixedCase-HFSX.txt";
        var data = System.Text.Encoding.UTF8.GetBytes("hello from an HFSX volume\n");

        using (var device = FileBackedBlockDevice.CreateNew(imageFilePath, ImageSize))
        {
            await HfsPlusNativeReader.FormatAsync(device, 0, ImageSize, "HFSXVolume");
            var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (reader is null)
            {
                Console.WriteLine("  FAIL: Could not open formatted image.");
                return false;
            }

            using (reader)
            {
                await reader.CreateFileAsync(2, fileName, data);
            }
        }

        await PatchHfsxSignatureAsync(imageFilePath, ImageSize);

        using (var readDevice = FileBackedBlockDevice.Open(imageFilePath, writable: false))
        {
            var hfsxReader = await HfsPlusNativeReader.OpenAsync(readDevice, 0);
            if (hfsxReader is null || !hfsxReader.VolumeHeader.IsHfsx)
            {
                Console.WriteLine("  Expected native reader to recognize the HX HFSX signature.");
                return false;
            }
            hfsxReader.Dispose();
        }

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        if (!string.Equals(plan.FileSystemType, "HFSX", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Expected HFSX, got {plan.FileSystemType}. Notes: {plan.Notes}");
            return false;
        }

        using var provider = await engine.CreateFileSystemProviderAsync(plan);
        var root = provider.ListDirectory("\\");
        var entry = root.FirstOrDefault(e => string.Equals(e.Name, fileName, StringComparison.Ordinal));
        if (entry is null || entry.Size != data.Length)
        {
            Console.WriteLine($"  Expected {fileName} size {data.Length}, got {entry?.Size.ToString() ?? "missing"}");
            return false;
        }

        var readBuffer = new byte[data.Length];
        var read = provider.ReadFile($"\\{fileName}", 0, readBuffer);
        if (read != data.Length || !readBuffer.SequenceEqual(data))
        {
            Console.WriteLine($"  Expected to read {data.Length} HFSX bytes, got {read}.");
            return false;
        }

        Console.WriteLine("  HFSX volumes are analyzed, mounted through the native provider, and browsed read-only.");
        return true;
    }

    private static async Task<bool> TestHfsxCatalogIndexCompareIsCaseSensitive(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        using (var device = FileBackedBlockDevice.CreateNew(imageFilePath, ImageSize))
        {
            await HfsPlusNativeReader.FormatAsync(device, 0, ImageSize, "HFSXCompare");
            using var hfsPlusReader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (hfsPlusReader is null)
            {
                Console.WriteLine("  FAIL: Could not open formatted HFS+ image.");
                return false;
            }

            var hfsPlusCompare = InvokeCatalogKeyCompare(hfsPlusReader, "abc", "ABC");
            if (hfsPlusCompare != 0)
            {
                Console.WriteLine($"  Expected HFS+ catalog comparison to fold case, got {hfsPlusCompare}.");
                return false;
            }
        }

        await PatchHfsxSignatureAsync(imageFilePath, ImageSize);

        using (var readDevice = FileBackedBlockDevice.Open(imageFilePath, writable: false))
        {
            using var hfsxReader = await HfsPlusNativeReader.OpenAsync(readDevice, 0);
            if (hfsxReader is null || !hfsxReader.VolumeHeader.IsHfsx)
            {
                Console.WriteLine("  Expected native reader to recognize the HX HFSX signature.");
                return false;
            }

            var hfsxCompare = InvokeCatalogKeyCompare(hfsxReader, "abc", "ABC");
            if (hfsxCompare <= 0)
            {
                Console.WriteLine($"  Expected HFSX catalog comparison to be case-sensitive, got {hfsxCompare}.");
                return false;
            }
        }

        Console.WriteLine("  HFSX catalog index comparison is case-sensitive while HFS+ remains case-folded.");
        return true;
    }

    private static int InvokeCatalogKeyCompare(HfsPlusNativeReader reader, string recordName, string targetName)
    {
        const int recOffset = 14;
        var nodeBuf = new byte[128];
        BinaryPrimitives.WriteUInt32BigEndian(nodeBuf.AsSpan(recOffset + 2, 4), 2);
        BinaryPrimitives.WriteUInt16BigEndian(nodeBuf.AsSpan(recOffset + 6, 2), (ushort)recordName.Length);
        for (var i = 0; i < recordName.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(nodeBuf.AsSpan(recOffset + 8 + i * 2, 2), (ushort)recordName[i]);
        }

        var method = typeof(HfsPlusNativeReader).GetMethod(
            "CompareCatalogKeys",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method is null)
        {
            throw new MissingMethodException(nameof(HfsPlusNativeReader), "CompareCatalogKeys");
        }

        return (int)method.Invoke(reader, new object[] { nodeBuf, recOffset, 2u, targetName })!;
    }

    private static Task<bool> TestHfsxCaseSensitiveCacheKeepsDistinctNames()
    {
        using var provider = new CachedRawFileSystemProvider(
            new HfsxCaseSensitiveTestProvider(),
            new CacheOptions { EnableReadAhead = false, BlockSize = 16 });

        var root = provider.ListDirectory("\\");
        if (root.Count(e => string.Equals(e.Name, "Report.txt", StringComparison.Ordinal)) != 1 ||
            root.Count(e => string.Equals(e.Name, "report.txt", StringComparison.Ordinal)) != 1)
        {
            Console.WriteLine("  Expected both HFSX case-distinct names in the cached root listing.");
            return Task.FromResult(false);
        }

        var mixedCase = provider.GetEntry("\\Report.txt");
        var lowerCase = provider.GetEntry("\\report.txt");
        if (mixedCase is null || lowerCase is null ||
            !string.Equals(mixedCase.Path, "\\Report.txt", StringComparison.Ordinal) ||
            !string.Equals(lowerCase.Path, "\\report.txt", StringComparison.Ordinal))
        {
            Console.WriteLine($"  HFSX entry cache collapsed case-distinct names: {mixedCase?.Path ?? "missing"} / {lowerCase?.Path ?? "missing"}");
            return Task.FromResult(false);
        }

        var mixedBuffer = new byte[(int)mixedCase.Size];
        var lowerBuffer = new byte[(int)lowerCase.Size];
        var mixedRead = provider.ReadFile("\\Report.txt", 0, mixedBuffer);
        var lowerRead = provider.ReadFile("\\report.txt", 0, lowerBuffer);
        var mixedText = System.Text.Encoding.UTF8.GetString(mixedBuffer, 0, mixedRead);
        var lowerText = System.Text.Encoding.UTF8.GetString(lowerBuffer, 0, lowerRead);
        if (mixedText != "mixed-case payload" || lowerText != "lower-case payload")
        {
            Console.WriteLine($"  HFSX block cache returned wrong data: '{mixedText}' / '{lowerText}'.");
            return Task.FromResult(false);
        }

        var hitsBeforeWrite = provider.GetStatistics().CacheHits;
        var replacement = System.Text.Encoding.UTF8.GetBytes("MIXED-case payload");
        provider.WriteFile("\\Report.txt", 0, replacement);

        var lowerHitsBeforeReread = provider.GetStatistics().CacheHits;
        Array.Clear(lowerBuffer);
        lowerRead = provider.ReadFile("\\report.txt", 0, lowerBuffer);
        var lowerHitsAfterReread = provider.GetStatistics().CacheHits;
        lowerText = System.Text.Encoding.UTF8.GetString(lowerBuffer, 0, lowerRead);
        if (lowerText != "lower-case payload" || lowerHitsAfterReread <= lowerHitsBeforeReread)
        {
            Console.WriteLine("  HFSX invalidation for Report.txt removed or polluted report.txt.");
            return Task.FromResult(false);
        }

        Array.Clear(mixedBuffer);
        mixedRead = provider.ReadFile("\\Report.txt", 0, mixedBuffer);
        mixedText = System.Text.Encoding.UTF8.GetString(mixedBuffer, 0, mixedRead);
        if (mixedText != "MIXED-case payload" || provider.GetStatistics().CacheHits <= hitsBeforeWrite)
        {
            Console.WriteLine($"  HFSX invalidation did not refresh the modified case-distinct file: '{mixedText}'.");
            return Task.FromResult(false);
        }

        Console.WriteLine("  HFSX cache preserves case-distinct paths, file data, and invalidation.");
        return Task.FromResult(true);
    }

    private static async Task<bool> TestMountHfsPlusResourceForkExtentsOverflow(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const int blockSize = 4096;
        var data = System.Text.Encoding.UTF8.GetBytes("hfs plus fragmented resource fork data\n");
        var resource = new byte[blockSize * 10 + 123];
        for (var i = 0; i < resource.Length; i++)
        {
            resource[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        using (var device = FileBackedBlockDevice.CreateNew(imageFilePath, ImageSize))
        {
            await HfsPlusNativeReader.FormatAsync(device, 0, ImageSize, "TestVolume");
            var reader = await HfsPlusNativeReader.OpenAsync(device, 0);
            if (reader is null)
            {
                Console.WriteLine("  FAIL: Could not open formatted image.");
                return false;
            }

            using (reader)
            {
                await reader.CreateFileAsync(2, "FragmentedFork.bin", data);
            }
        }

        await PatchHfsPlusResourceForkAsync(imageFilePath, "FragmentedFork.bin", resource, useOverflowExtents: true);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        using var provider = await engine.CreateFileSystemProviderAsync(plan);
        var rootEntries = provider.ListDirectory("\\");

        if (!rootEntries.Any(e => string.Equals(e.Name, "._FragmentedFork.bin", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"  Expected listed fragmented AppleDouble sidecar ._FragmentedFork.bin. Got: {string.Join(", ", rootEntries.Select(e => e.Name))}");
            return false;
        }

        var sidecar = provider.GetEntry("\\._FragmentedFork.bin");
        if (sidecar is null || sidecar.Size != 38 + resource.Length)
        {
            Console.WriteLine($"  Expected fragmented AppleDouble sidecar size {38 + resource.Length}, got {sidecar?.Size.ToString() ?? "missing"}");
            return false;
        }

        var appleDouble = new byte[(int)sidecar.Size];
        var read = provider.ReadFile("\\._FragmentedFork.bin", 0, appleDouble);
        if (read != appleDouble.Length)
        {
            Console.WriteLine($"  Expected to read {appleDouble.Length} fragmented AppleDouble bytes, got {read}");
            return false;
        }

        var entryOffset = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(30, 4));
        var entryLength = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(34, 4));
        if (entryOffset != 38 || entryLength != resource.Length)
        {
            Console.WriteLine($"  Invalid fragmented AppleDouble resource entry: offset={entryOffset}, length={entryLength}");
            return false;
        }

        if (!appleDouble.Skip((int)entryOffset).Take(resource.Length).SequenceEqual(resource))
        {
            Console.WriteLine("  Fragmented HFS+ resource fork payload did not match AppleDouble entry data.");
            return false;
        }

        var crossing = new byte[96];
        var crossingOffset = 38 + blockSize * 8 - 37;
        var crossingRead = provider.ReadFile("\\._FragmentedFork.bin", crossingOffset, crossing);
        if (crossingRead != crossing.Length ||
            !crossing.SequenceEqual(resource.Skip(blockSize * 8 - 37).Take(crossing.Length)))
        {
            Console.WriteLine($"  Expected partial read across HFS+ resource-fork overflow boundary, got {crossingRead} bytes.");
            return false;
        }

        Console.WriteLine("  HFS+ resource fork data continues through the extents-overflow B-tree.");
        return true;
    }

    private static async Task PatchHfsPlusResourceForkAsync(string imageFilePath, string fileName, byte[] resourceFork, bool useOverflowExtents = false, byte[]? finderInfo = null, ushort? fileMode = null)
    {
        await using var stream = new FileStream(imageFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var vh = new byte[512];
        stream.Position = 1024;
        await stream.ReadExactlyAsync(vh);

        var blockSize = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(40, 4));
        var freeBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(48, 4));
        var nextAllocation = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(52, 4));
        var bitmapStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(112 + 16, 4));
        var bitmapBlockCount = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(112 + 20, 4));
        var extentsStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(192 + 16, 4));
        var extentsBlockCount = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(192 + 20, 4));
        var catalogStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(272 + 16, 4));

        var resourceBlocks = (uint)((resourceFork.Length + blockSize - 1) / blockSize);
        var resourceExtents = new List<(uint StartBlock, uint BlockCount)>();
        var allocatedBlocks = new SortedSet<uint>();
        uint extentsOverflowLeafStartBlock = 0;
        uint extentsOverflowLeafBlockCount = 0;

        if (useOverflowExtents)
        {
            extentsOverflowLeafBlockCount = (uint)((8192 + blockSize - 1) / blockSize);
            extentsOverflowLeafStartBlock = nextAllocation + 4;
            for (uint block = extentsOverflowLeafStartBlock; block < extentsOverflowLeafStartBlock + extentsOverflowLeafBlockCount; block++)
            {
                allocatedBlocks.Add(block);
            }

            var firstResourceBlock = nextAllocation + 16;
            for (uint i = 0; i < resourceBlocks; i++)
            {
                var startBlock = firstResourceBlock + i * 2;
                resourceExtents.Add((startBlock, 1));
                allocatedBlocks.Add(startBlock);

                var blockBytes = new byte[blockSize];
                var sourceOffset = (int)(i * blockSize);
                var sourceCount = Math.Min((int)blockSize, resourceFork.Length - sourceOffset);
                if (sourceCount > 0)
                {
                    Buffer.BlockCopy(resourceFork, sourceOffset, blockBytes, 0, sourceCount);
                }
                stream.Position = (long)startBlock * blockSize;
                await stream.WriteAsync(blockBytes);
            }
        }
        else
        {
            var resourceStartBlock = nextAllocation + 8;
            resourceExtents.Add((resourceStartBlock, resourceBlocks));
            for (uint block = resourceStartBlock; block < resourceStartBlock + resourceBlocks; block++)
            {
                allocatedBlocks.Add(block);
            }

            var paddedResource = new byte[resourceBlocks * blockSize];
            Buffer.BlockCopy(resourceFork, 0, paddedResource, 0, resourceFork.Length);
            stream.Position = (long)resourceStartBlock * blockSize;
            await stream.WriteAsync(paddedResource);
        }

        var bitmap = new byte[bitmapBlockCount * blockSize];
        stream.Position = (long)bitmapStartBlock * blockSize;
        await stream.ReadExactlyAsync(bitmap);
        foreach (var block in allocatedBlocks)
        {
            var byteIndex = block / 8;
            var bitIndex = 7 - (int)(block % 8);
            bitmap[byteIndex] |= (byte)(1 << bitIndex);
        }
        stream.Position = (long)bitmapStartBlock * blockSize;
        await stream.WriteAsync(bitmap);

        if (useOverflowExtents)
        {
            WriteHfsPlusForkData(
                vh.AsSpan(192, 80),
                checked((int)((extentsBlockCount + extentsOverflowLeafBlockCount) * blockSize)),
                blockSize,
                new[] { (extentsStartBlock, extentsBlockCount), (extentsOverflowLeafStartBlock, extentsOverflowLeafBlockCount) });
        }

        var allocatedBlockCount = (uint)allocatedBlocks.Count;
        var nextFreeBlock = allocatedBlocks.Count > 0 ? allocatedBlocks.Max + 1 : nextAllocation;
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(48, 4), freeBlocks > allocatedBlockCount ? freeBlocks - allocatedBlockCount : 0);
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(52, 4), Math.Max(nextAllocation, nextFreeBlock));
        stream.Position = 1024;
        await stream.WriteAsync(vh);

        var catalogOffset = (long)catalogStartBlock * blockSize;
        var catalogHeader = new byte[8192];
        stream.Position = catalogOffset;
        await stream.ReadExactlyAsync(catalogHeader);

        var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(catalogHeader.AsSpan(14 + 18, 2));
        var totalNodes = BinaryPrimitives.ReadUInt32BigEndian(catalogHeader.AsSpan(14 + 22, 4));
        if (nodeSize == 0)
        {
            throw new InvalidDataException($"Unexpected HFS+ catalog node size {nodeSize}.");
        }

        for (uint nodeIndex = 0; nodeIndex < totalNodes; nodeIndex++)
        {
            var node = new byte[nodeSize];
            stream.Position = catalogOffset + (long)nodeIndex * nodeSize;
            await stream.ReadExactlyAsync(node);

            if ((sbyte)node[8] != -1)
            {
                continue;
            }

            var numRecords = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(10, 2));
            for (var recordIndex = 0; recordIndex < numRecords; recordIndex++)
            {
                var (recordOffset, _) = GetBTreeRecordOffsetAndLength(node, recordIndex, numRecords);
                if (recordOffset < 14 || recordOffset + 8 > node.Length) continue;

                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(recordOffset, 2));
                if (keyLen < 6 || recordOffset + 2 + keyLen > node.Length) continue;

                var parentCnid = BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(recordOffset + 2, 4));
                var nameLength = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(recordOffset + 6, 2));
                if (parentCnid != 2 || nameLength != fileName.Length) continue;

                var chars = new char[nameLength];
                for (var i = 0; i < nameLength; i++)
                {
                    chars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(recordOffset + 8 + i * 2, 2));
                }
                if (!string.Equals(new string(chars), fileName, StringComparison.Ordinal)) continue;

                var dataOffset = recordOffset + 2 + keyLen;
                if (dataOffset % 2 != 0) dataOffset++;
                if (dataOffset + 248 > node.Length) continue;
                if (BinaryPrimitives.ReadInt16BigEndian(node.AsSpan(dataOffset, 2)) != 2) continue;
                if (fileMode.HasValue)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(dataOffset + 42, 2), fileMode.Value);
                }

                if (finderInfo is not null)
                {
                    if (finderInfo.Length != 32)
                    {
                        throw new ArgumentOutOfRangeException(nameof(finderInfo), "Finder Info must be exactly 32 bytes.");
                    }
                    finderInfo.CopyTo(node.AsSpan(dataOffset + 48, 32));
                }

                var fileCnid = BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(dataOffset + 8, 4));
                var inlineResourceExtents = useOverflowExtents
                    ? resourceExtents.Take(8).ToArray()
                    : resourceExtents.ToArray();

                WriteHfsPlusForkData(
                    node.AsSpan(dataOffset + 168, 80),
                    resourceFork.Length,
                    blockSize,
                    inlineResourceExtents);

                stream.Position = catalogOffset + (long)nodeIndex * nodeSize;
                await stream.WriteAsync(node);

                if (useOverflowExtents)
                {
                    await WriteHfsPlusExtentsOverflowTreeAsync(
                        stream,
                        blockSize,
                        extentsStartBlock,
                        extentsOverflowLeafStartBlock,
                        fileCnid,
                        startBlock: 8,
                        resourceExtents.Skip(8).ToArray());
                }
                return;
            }
        }

        throw new InvalidDataException($"Could not locate HFS+ catalog file record for '{fileName}'.");
    }

    private static async Task PatchHfsxSignatureAsync(string imageFilePath, long partitionSize)
    {
        await using var stream = new FileStream(imageFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var hfsxHeader = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(hfsxHeader.AsSpan(0, 2), 0x4858); // "HX"
        BinaryPrimitives.WriteUInt16BigEndian(hfsxHeader.AsSpan(2, 2), 5);      // HFSX volume version

        stream.Position = 1024;
        await stream.WriteAsync(hfsxHeader);

        var alternateHeaderOffset = partitionSize - 1024;
        if (alternateHeaderOffset > 1024 && alternateHeaderOffset + hfsxHeader.Length <= stream.Length)
        {
            stream.Position = alternateHeaderOffset;
            await stream.WriteAsync(hfsxHeader);
        }
    }

    private static async Task<bool> TestAnalyzeApmClassicHfs(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const int blockSize = 512;
        const uint partitionStartBlock = 64;
        const long imageSize = 8 * 1024 * 1024;
        var partitionOffset = partitionStartBlock * blockSize;
        var partitionBlockCount = (uint)((imageSize - partitionOffset) / blockSize);

        await using (var stream = new FileStream(imageFilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(imageSize);

            var ddr = new byte[blockSize];
            BinaryPrimitives.WriteUInt16BigEndian(ddr.AsSpan(0, 2), 0x4552); // "ER"
            BinaryPrimitives.WriteUInt16BigEndian(ddr.AsSpan(2, 2), blockSize);
            stream.Position = 0;
            await stream.WriteAsync(ddr);

            var apm = new byte[blockSize];
            BinaryPrimitives.WriteUInt16BigEndian(apm.AsSpan(0, 2), 0x504D); // "PM"
            BinaryPrimitives.WriteUInt32BigEndian(apm.AsSpan(4, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(apm.AsSpan(8, 4), partitionStartBlock);
            BinaryPrimitives.WriteUInt32BigEndian(apm.AsSpan(12, 4), partitionBlockCount);
            WriteApmString(apm.AsSpan(16, 32), "LegacyHFS");
            WriteApmString(apm.AsSpan(48, 32), "Apple_HFS");
            stream.Position = blockSize;
            await stream.WriteAsync(apm);

            var hfsSignature = new byte[] { 0x42, 0x44 }; // "BD", HFS Standard volume signature.
            stream.Position = partitionOffset + 1024;
            await stream.WriteAsync(hfsSignature);
        }

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        if (!string.Equals(plan.FileSystemType, "HFS", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Expected HFS, got {plan.FileSystemType}. Notes: {plan.Notes}");
            return false;
        }
        if (plan.PartitionOffsetBytes != partitionOffset)
        {
            Console.WriteLine($"  Expected partition offset {partitionOffset}, got {plan.PartitionOffsetBytes}");
            return false;
        }

        Console.WriteLine("  APM Apple_HFS with BD signature is classified as classic HFS.");
        return true;
    }

    private static async Task<bool> TestMountApmClassicHfsReadOnly(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const int blockSize = 512;
        const uint partitionStartBlock = 64;
        const long imageSize = 8 * 1024 * 1024;
        var partitionOffset = partitionStartBlock * blockSize;
        var content = System.Text.Encoding.ASCII.GetBytes("hello hfs!\n");

        await WriteClassicHfsImageAsync(imageFilePath, imageSize, partitionStartBlock, content);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        if (!string.Equals(plan.FileSystemType, "HFS", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Expected HFS, got {plan.FileSystemType}. Notes: {plan.Notes}");
            return false;
        }
        if (plan.PartitionOffsetBytes != partitionOffset)
        {
            Console.WriteLine($"  Expected partition offset {partitionOffset}, got {plan.PartitionOffsetBytes}");
            return false;
        }

        using var device = FileBackedBlockDevice.Open(imageFilePath, writable: false);
        using var provider = await HfsClassicRawFileSystemProvider.CreateFromDeviceAsync(plan, device);

        var root = provider.ListDirectory("\\");
        var hello = root.FirstOrDefault(e => string.Equals(e.Name, "HELLO.TXT", StringComparison.OrdinalIgnoreCase));
        var folder = root.FirstOrDefault(e => string.Equals(e.Name, "FOLDER", StringComparison.OrdinalIgnoreCase));
        if (hello is null || folder is null || !folder.IsDirectory)
        {
            Console.WriteLine($"  Expected HELLO.TXT and FOLDER in root. Got: {string.Join(", ", root.Select(e => e.Name))}");
            return false;
        }
        if (hello.Size != content.Length)
        {
            Console.WriteLine($"  Expected HELLO.TXT size {content.Length}, got {hello.Size}");
            return false;
        }

        var readBuffer = new byte[content.Length];
        var read = provider.ReadFile("\\HELLO.TXT", 0, readBuffer);
        if (read != content.Length || !readBuffer.SequenceEqual(content))
        {
            Console.WriteLine($"  Expected to read '{System.Text.Encoding.ASCII.GetString(content)}', got {read} bytes '{System.Text.Encoding.ASCII.GetString(readBuffer)}'");
            return false;
        }

        Console.WriteLine("  Classic HFS provider listed root and read HELLO.TXT without external runtime.");
        return true;
    }

    private static async Task<bool> TestMountApmClassicHfsExtentsOverflow(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const uint partitionStartBlock = 64;
        const long imageSize = 8 * 1024 * 1024;
        var blocks = new[]
        {
            System.Text.Encoding.ASCII.GetBytes("catalog extent block 1".PadRight(512, '1')),
            System.Text.Encoding.ASCII.GetBytes("catalog extent block 2".PadRight(512, '2')),
            System.Text.Encoding.ASCII.GetBytes("catalog extent block 3".PadRight(512, '3')),
            System.Text.Encoding.ASCII.GetBytes("overflow extent block 4".PadRight(512, '4'))
        };
        var expected = blocks.SelectMany(b => b).ToArray();

        await WriteClassicHfsImageAsync(imageFilePath, imageSize, partitionStartBlock, expected, useOverflowExtent: true);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        using var device = FileBackedBlockDevice.Open(imageFilePath, writable: false);
        using var provider = await HfsClassicRawFileSystemProvider.CreateFromDeviceAsync(plan, device);

        var entry = provider.GetEntry("\\HELLO.TXT");
        if (entry is null || entry.Size != expected.Length)
        {
            Console.WriteLine($"  Expected HELLO.TXT size {expected.Length}, got {entry?.Size.ToString() ?? "missing"}");
            return false;
        }

        var actual = new byte[expected.Length];
        var read = provider.ReadFile("\\HELLO.TXT", 0, actual);
        if (read != expected.Length || !actual.SequenceEqual(expected))
        {
            Console.WriteLine($"  Expected {expected.Length} bytes across catalog+overflow extents, got {read}.");
            return false;
        }

        var tail = new byte[64];
        var tailRead = provider.ReadFile("\\HELLO.TXT", 1536, tail);
        if (tailRead != tail.Length || !tail.SequenceEqual(expected.Skip(1536).Take(64)))
        {
            Console.WriteLine($"  Expected partial read from overflow extent, got {tailRead} bytes.");
            return false;
        }

        Console.WriteLine("  Classic HFS provider read file data that continues in the extents-overflow B-tree.");
        return true;
    }

    private static async Task<bool> TestMountApmClassicHfsResourceForkAppleDouble(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const uint partitionStartBlock = 64;
        const long imageSize = 8 * 1024 * 1024;
        var data = System.Text.Encoding.ASCII.GetBytes("hello hfs!\n");
        var resource = System.Text.Encoding.ASCII.GetBytes("classic resource fork payload\n");
        await WriteClassicHfsImageAsync(imageFilePath, imageSize, partitionStartBlock, data, resourceForkContent: resource);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        using var device = FileBackedBlockDevice.Open(imageFilePath, writable: false);
        using var provider = await HfsClassicRawFileSystemProvider.CreateFromDeviceAsync(plan, device);

        var sidecar = provider.GetEntry("\\._HELLO.TXT");
        if (sidecar is null)
        {
            Console.WriteLine("  Expected AppleDouble sidecar ._HELLO.TXT for resource fork.");
            return false;
        }
        if (sidecar.Size != 38 + resource.Length)
        {
            Console.WriteLine($"  Expected AppleDouble sidecar size {38 + resource.Length}, got {sidecar.Size}");
            return false;
        }

        var appleDouble = new byte[(int)sidecar.Size];
        var read = provider.ReadFile("\\._HELLO.TXT", 0, appleDouble);
        if (read != appleDouble.Length)
        {
            Console.WriteLine($"  Expected to read {appleDouble.Length} AppleDouble bytes, got {read}");
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(4, 4));
        var entryCount = BinaryPrimitives.ReadUInt16BigEndian(appleDouble.AsSpan(24, 2));
        var entryId = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(26, 4));
        var entryOffset = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(30, 4));
        var entryLength = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(34, 4));
        if (magic != 0x00051607 || version != 0x00020000 || entryCount != 1 || entryId != 2 ||
            entryOffset != 38 || entryLength != resource.Length)
        {
            Console.WriteLine($"  Invalid AppleDouble header: magic=0x{magic:X8}, version=0x{version:X8}, entries={entryCount}, id={entryId}, offset={entryOffset}, len={entryLength}");
            return false;
        }

        if (!appleDouble.Skip((int)entryOffset).Take(resource.Length).SequenceEqual(resource))
        {
            Console.WriteLine("  Resource fork payload did not match AppleDouble entry data.");
            return false;
        }

        Console.WriteLine("  Classic HFS resource fork is exposed as a valid AppleDouble sidecar.");
        return true;
    }

    private static async Task<bool> TestMountApmClassicHfsFinderInfoAppleDouble(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const uint partitionStartBlock = 64;
        const long imageSize = 8 * 1024 * 1024;
        var data = System.Text.Encoding.ASCII.GetBytes("hello hfs finder info!\n");
        var resource = System.Text.Encoding.ASCII.GetBytes("classic finder info resource\n");
        var finderInfo = new byte[32];
        System.Text.Encoding.ASCII.GetBytes("TEXTttxt").CopyTo(finderInfo, 0);
        for (var i = 8; i < finderInfo.Length; i++)
        {
            finderInfo[i] = (byte)(0x50 + i);
        }

        await WriteClassicHfsImageAsync(
            imageFilePath,
            imageSize,
            partitionStartBlock,
            data,
            resourceForkContent: resource,
            finderInfo: finderInfo);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        using var device = FileBackedBlockDevice.Open(imageFilePath, writable: false);
        using var provider = await HfsClassicRawFileSystemProvider.CreateFromDeviceAsync(plan, device);

        var sidecar = provider.GetEntry("\\._HELLO.TXT");
        if (sidecar is null || sidecar.Size != 82 + resource.Length)
        {
            Console.WriteLine($"  Expected classic Finder Info AppleDouble sidecar size {82 + resource.Length}, got {sidecar?.Size.ToString() ?? "missing"}");
            return false;
        }

        var appleDouble = new byte[(int)sidecar.Size];
        var read = provider.ReadFile("\\._HELLO.TXT", 0, appleDouble);
        if (read != appleDouble.Length)
        {
            Console.WriteLine($"  Expected to read {appleDouble.Length} classic Finder Info AppleDouble bytes, got {read}");
            return false;
        }

        var entryCount = BinaryPrimitives.ReadUInt16BigEndian(appleDouble.AsSpan(24, 2));
        var finderEntryId = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(26, 4));
        var finderOffset = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(30, 4));
        var finderLength = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(34, 4));
        var resourceEntryId = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(38, 4));
        var resourceOffset = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(42, 4));
        var resourceLength = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(46, 4));
        if (entryCount != 2 ||
            finderEntryId != 9 || finderOffset != 50 || finderLength != 32 ||
            resourceEntryId != 2 || resourceOffset != 82 || resourceLength != resource.Length)
        {
            Console.WriteLine($"  Invalid classic Finder Info AppleDouble header: entries={entryCount}, finder=({finderEntryId},{finderOffset},{finderLength}), resource=({resourceEntryId},{resourceOffset},{resourceLength})");
            return false;
        }

        if (!appleDouble.Skip((int)finderOffset).Take(32).SequenceEqual(finderInfo))
        {
            Console.WriteLine("  Classic HFS Finder Info payload did not match AppleDouble entry data.");
            return false;
        }
        if (!appleDouble.Skip((int)resourceOffset).Take(resource.Length).SequenceEqual(resource))
        {
            Console.WriteLine("  Classic HFS resource fork payload did not match shifted AppleDouble entry data.");
            return false;
        }

        Console.WriteLine("  Classic HFS Finder Info is preserved in AppleDouble sidecars.");
        return true;
    }

    private static async Task<bool> TestMountApmClassicHfsResourceForkExtentsOverflow(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const uint partitionStartBlock = 64;
        const long imageSize = 8 * 1024 * 1024;
        var data = System.Text.Encoding.ASCII.GetBytes("hello hfs!\n");
        var resourceBlocks = new[]
        {
            System.Text.Encoding.ASCII.GetBytes("resource catalog block 1".PadRight(512, 'R')),
            System.Text.Encoding.ASCII.GetBytes("resource catalog block 2".PadRight(512, 'S')),
            System.Text.Encoding.ASCII.GetBytes("resource catalog block 3".PadRight(512, 'T')),
            System.Text.Encoding.ASCII.GetBytes("resource overflow block 4".PadRight(512, 'U'))
        };
        var resource = resourceBlocks.SelectMany(b => b).ToArray();

        await WriteClassicHfsImageAsync(
            imageFilePath,
            imageSize,
            partitionStartBlock,
            data,
            resourceForkContent: resource,
            useResourceOverflowExtent: true);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        using var device = FileBackedBlockDevice.Open(imageFilePath, writable: false);
        using var provider = await HfsClassicRawFileSystemProvider.CreateFromDeviceAsync(plan, device);

        var sidecar = provider.GetEntry("\\._HELLO.TXT");
        if (sidecar is null || sidecar.Size != 38 + resource.Length)
        {
            Console.WriteLine($"  Expected AppleDouble sidecar size {38 + resource.Length}, got {sidecar?.Size.ToString() ?? "missing"}");
            return false;
        }

        var appleDouble = new byte[(int)sidecar.Size];
        var read = provider.ReadFile("\\._HELLO.TXT", 0, appleDouble);
        if (read != appleDouble.Length)
        {
            Console.WriteLine($"  Expected to read {appleDouble.Length} AppleDouble bytes, got {read}");
            return false;
        }

        var entryOffset = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(30, 4));
        var entryLength = BinaryPrimitives.ReadUInt32BigEndian(appleDouble.AsSpan(34, 4));
        if (entryOffset != 38 || entryLength != resource.Length)
        {
            Console.WriteLine($"  Invalid AppleDouble resource entry: offset={entryOffset}, length={entryLength}");
            return false;
        }
        if (!appleDouble.Skip((int)entryOffset).Take(resource.Length).SequenceEqual(resource))
        {
            Console.WriteLine("  Resource fork overflow payload did not match AppleDouble entry data.");
            return false;
        }

        var tail = new byte[64];
        var tailRead = provider.ReadFile("\\._HELLO.TXT", 38 + 1536, tail);
        if (tailRead != tail.Length || !tail.SequenceEqual(resource.Skip(1536).Take(64)))
        {
            Console.WriteLine($"  Expected partial read from resource overflow extent, got {tailRead} bytes.");
            return false;
        }

        Console.WriteLine("  Classic HFS resource fork data continues through the extents-overflow B-tree.");
        return true;
    }

    private static async Task<bool> TestMountApmClassicHfsMacRomanFilename(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const uint partitionStartBlock = 64;
        const long imageSize = 8 * 1024 * 1024;
        var data = System.Text.Encoding.ASCII.GetBytes("bonjour classic hfs\n");
        var macRomanName = new byte[] { (byte)'C', (byte)'A', (byte)'F', 0x83, (byte)'.', (byte)'T', (byte)'X', (byte)'T' }; // CAFÉ.TXT

        await WriteClassicHfsImageAsync(imageFilePath, imageSize, partitionStartBlock, data, fileNameBytes: macRomanName);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        using var device = FileBackedBlockDevice.Open(imageFilePath, writable: false);
        using var provider = await HfsClassicRawFileSystemProvider.CreateFromDeviceAsync(plan, device);

        var entry = provider.GetEntry("\\CAFÉ.TXT");
        if (entry is null)
        {
            var root = provider.ListDirectory("\\");
            Console.WriteLine($"  Expected CAFÉ.TXT decoded from MacRoman. Got: {string.Join(", ", root.Select(e => e.Name))}");
            return false;
        }
        if (entry.Size != data.Length)
        {
            Console.WriteLine($"  Expected CAFÉ.TXT size {data.Length}, got {entry.Size}");
            return false;
        }

        var actual = new byte[data.Length];
        var read = provider.ReadFile("\\CAFÉ.TXT", 0, actual);
        if (read != data.Length || !actual.SequenceEqual(data))
        {
            Console.WriteLine($"  Expected to read MacRoman-named file data, got {read} bytes.");
            return false;
        }

        Console.WriteLine("  Classic HFS MacRoman filename decoded and read through the native provider.");
        return true;
    }

    private static async Task<bool> TestMountApmClassicHfsCatalogLeafChain(string imageFilePath)
    {
        if (File.Exists(imageFilePath))
            File.Delete(imageFilePath);

        const uint partitionStartBlock = 64;
        const long imageSize = 8 * 1024 * 1024;
        var firstLeafContent = System.Text.Encoding.ASCII.GetBytes("first catalog leaf\n");
        var secondLeafContent = System.Text.Encoding.ASCII.GetBytes("second catalog leaf\n");

        await WriteClassicHfsImageAsync(
            imageFilePath,
            imageSize,
            partitionStartBlock,
            firstLeafContent,
            useSecondCatalogLeaf: true,
            secondLeafFileContent: secondLeafContent);

        var engine = new global::CrossDrive.RawDiskEngine.RawDiskEngine(deviceFactory: new FileImageDeviceFactory());
        var plan = await engine.AnalyzeAsync(new MountRequest(imageFilePath, string.Empty));
        using var device = FileBackedBlockDevice.Open(imageFilePath, writable: false);
        using var provider = await HfsClassicRawFileSystemProvider.CreateFromDeviceAsync(plan, device);

        var root = provider.ListDirectory("\\");
        var second = root.FirstOrDefault(e => string.Equals(e.Name, "SECOND.TXT", StringComparison.OrdinalIgnoreCase));
        if (second is null || second.Size != secondLeafContent.Length)
        {
            Console.WriteLine($"  Expected SECOND.TXT from second catalog leaf. Got: {string.Join(", ", root.Select(e => e.Name))}");
            return false;
        }

        var actual = new byte[secondLeafContent.Length];
        var read = provider.ReadFile("\\SECOND.TXT", 0, actual);
        if (read != secondLeafContent.Length || !actual.SequenceEqual(secondLeafContent))
        {
            Console.WriteLine($"  Expected to read SECOND.TXT from the linked catalog leaf, got {read} bytes.");
            return false;
        }

        Console.WriteLine("  Classic HFS catalog leaf fLink chain was followed and SECOND.TXT was read.");
        return true;
    }

    private static async Task WriteClassicHfsImageAsync(string imageFilePath, long imageSize, uint partitionStartBlock, byte[] fileContent, bool useOverflowExtent = false, byte[]? resourceForkContent = null, byte[]? fileNameBytes = null, bool useResourceOverflowExtent = false, bool useSecondCatalogLeaf = false, byte[]? secondLeafFileContent = null, byte[]? finderInfo = null)
    {
        const int blockSize = 512;
        const ushort extentsStartBlock = 4;
        const ushort catalogStartAllocationBlock = 16;
        const ushort fileStartAllocationBlock = 32;
        const ushort fileSecondAllocationBlock = 40;
        const ushort fileThirdAllocationBlock = 48;
        const ushort fileOverflowAllocationBlock = 56;
        const ushort resourceForkAllocationBlock = 72;
        const ushort resourceForkSecondAllocationBlock = 80;
        const ushort resourceForkThirdAllocationBlock = 88;
        const ushort resourceForkOverflowAllocationBlock = 96;
        const ushort secondLeafFileStartAllocationBlock = 104;
        const ushort extentsOverflowStartAllocationBlock = 24;
        const ushort extentsOverflowBlockCount = 2;
        const uint allocationBlockSize = 512;
        var catalogBlockCount = (ushort)(useSecondCatalogLeaf ? 3 : 2);

        var partitionOffset = partitionStartBlock * blockSize;
        var partitionBlockCount = (uint)((imageSize - partitionOffset) / blockSize);
        var allocationBlockCount = (ushort)(partitionBlockCount - extentsStartBlock);
        var allocationStart = partitionOffset + extentsStartBlock * blockSize;
        var catalogOffset = allocationStart + catalogStartAllocationBlock * allocationBlockSize;
        var extentsOverflowOffset = allocationStart + extentsOverflowStartAllocationBlock * allocationBlockSize;
        var fileOffset = allocationStart + fileStartAllocationBlock * allocationBlockSize;
        var now = ToHfsTimestamp(DateTimeOffset.UtcNow);

        await using var stream = new FileStream(imageFilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(imageSize);

        var ddr = new byte[blockSize];
        BinaryPrimitives.WriteUInt16BigEndian(ddr.AsSpan(0, 2), 0x4552); // "ER"
        BinaryPrimitives.WriteUInt16BigEndian(ddr.AsSpan(2, 2), blockSize);
        stream.Position = 0;
        await stream.WriteAsync(ddr);

        var apm = new byte[blockSize];
        BinaryPrimitives.WriteUInt16BigEndian(apm.AsSpan(0, 2), 0x504D); // "PM"
        BinaryPrimitives.WriteUInt32BigEndian(apm.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(apm.AsSpan(8, 4), partitionStartBlock);
        BinaryPrimitives.WriteUInt32BigEndian(apm.AsSpan(12, 4), partitionBlockCount);
        WriteApmString(apm.AsSpan(16, 32), "LegacyHFS");
        WriteApmString(apm.AsSpan(48, 32), "Apple_HFS");
        stream.Position = blockSize;
        await stream.WriteAsync(apm);

        var mdb = new byte[blockSize];
        BinaryPrimitives.WriteUInt16BigEndian(mdb.AsSpan(0, 2), 0x4244); // "BD"
        BinaryPrimitives.WriteUInt32BigEndian(mdb.AsSpan(6, 4), now);
        BinaryPrimitives.WriteUInt16BigEndian(mdb.AsSpan(18, 2), allocationBlockCount);
        BinaryPrimitives.WriteUInt32BigEndian(mdb.AsSpan(20, 4), allocationBlockSize);
        BinaryPrimitives.WriteUInt16BigEndian(mdb.AsSpan(28, 2), extentsStartBlock);
        BinaryPrimitives.WriteUInt16BigEndian(mdb.AsSpan(34, 2), (ushort)(allocationBlockCount - 40));
        WriteClassicPascalString(mdb.AsSpan(36, 28), "LegacyHFS");
        if (useOverflowExtent || useResourceOverflowExtent)
        {
            BinaryPrimitives.WriteUInt32BigEndian(mdb.AsSpan(130, 4), extentsOverflowBlockCount * allocationBlockSize);
            WriteClassicExtent(mdb.AsSpan(134, 4), extentsOverflowStartAllocationBlock, extentsOverflowBlockCount);
        }
        BinaryPrimitives.WriteUInt32BigEndian(mdb.AsSpan(146, 4), catalogBlockCount * allocationBlockSize);
        WriteClassicExtent(mdb.AsSpan(150, 4), catalogStartAllocationBlock, catalogBlockCount);
        stream.Position = partitionOffset + 1024;
        await stream.WriteAsync(mdb);

        if (useOverflowExtent || useResourceOverflowExtent)
        {
            var extentsHeaderNode = BuildClassicHfsCatalogHeaderNode();
            stream.Position = extentsOverflowOffset;
            await stream.WriteAsync(extentsHeaderNode);

            var overflowRecords = new List<byte[]>();
            if (useOverflowExtent)
            {
                overflowRecords.Add(BuildClassicHfsExtentsOverflowRecord(16, 0, 3, fileOverflowAllocationBlock));
            }
            if (useResourceOverflowExtent)
            {
                overflowRecords.Add(BuildClassicHfsExtentsOverflowRecord(16, -1, 3, resourceForkOverflowAllocationBlock));
            }
            var extentsLeafNode = BuildClassicHfsExtentsOverflowLeafNode(overflowRecords);
            stream.Position = extentsOverflowOffset + blockSize;
            await stream.WriteAsync(extentsLeafNode);
        }

        var headerNode = BuildClassicHfsCatalogHeaderNode(
            leafRecords: useSecondCatalogLeaf ? 3u : 2u,
            lastLeaf: useSecondCatalogLeaf ? 2u : 1u,
            totalNodes: useSecondCatalogLeaf ? 3u : 2u);
        stream.Position = catalogOffset;
        await stream.WriteAsync(headerNode);

        var leafNode = BuildClassicHfsCatalogLeafNode(now, (uint)fileContent.Length, fileStartAllocationBlock, useOverflowExtent, (uint)(resourceForkContent?.Length ?? 0), resourceForkAllocationBlock, fileNameBytes, useResourceOverflowExtent, nextNode: useSecondCatalogLeaf ? 2u : 0u, finderInfo: finderInfo);
        stream.Position = catalogOffset + blockSize;
        await stream.WriteAsync(leafNode);

        if (useSecondCatalogLeaf)
        {
            var secondContent = secondLeafFileContent ?? System.Text.Encoding.ASCII.GetBytes("second catalog leaf\n");
            var secondLeafNode = BuildClassicHfsCatalogLeafNode(
                now,
                (uint)secondContent.Length,
                secondLeafFileStartAllocationBlock,
                fileNameBytes: System.Text.Encoding.ASCII.GetBytes("SECOND.TXT"),
                previousNode: 1,
                fileCnid: 18,
                includeFolder: false);
            stream.Position = catalogOffset + (2 * blockSize);
            await stream.WriteAsync(secondLeafNode);

            stream.Position = allocationStart + secondLeafFileStartAllocationBlock * allocationBlockSize;
            await stream.WriteAsync(secondContent);
        }

        if (useOverflowExtent)
        {
            var fileBlocks = fileContent.Chunk(blockSize).Select(c => c.ToArray()).ToArray();
            var starts = new[] { fileStartAllocationBlock, fileSecondAllocationBlock, fileThirdAllocationBlock, fileOverflowAllocationBlock };
            for (var i = 0; i < starts.Length; i++)
            {
                stream.Position = allocationStart + starts[i] * allocationBlockSize;
                await stream.WriteAsync(fileBlocks[i]);
            }
        }
        else
        {
            stream.Position = fileOffset;
            await stream.WriteAsync(fileContent);
        }

        if (resourceForkContent is { Length: > 0 })
        {
            if (useResourceOverflowExtent)
            {
                var resourceBlocks = resourceForkContent.Chunk(blockSize).Select(c => c.ToArray()).ToArray();
                var starts = new[] { resourceForkAllocationBlock, resourceForkSecondAllocationBlock, resourceForkThirdAllocationBlock, resourceForkOverflowAllocationBlock };
                for (var i = 0; i < starts.Length; i++)
                {
                    stream.Position = allocationStart + starts[i] * allocationBlockSize;
                    await stream.WriteAsync(resourceBlocks[i]);
                }
            }
            else
            {
                stream.Position = allocationStart + resourceForkAllocationBlock * allocationBlockSize;
                await stream.WriteAsync(resourceForkContent);
            }
        }
    }

    private static byte[] BuildClassicHfsCatalogHeaderNode(uint leafRecords = 2, uint lastLeaf = 1, uint totalNodes = 2)
    {
        var node = new byte[512];
        node[8] = 1; // header node
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10, 2), 1);

        const int recordOffset = 14;
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(recordOffset + 0, 2), 1); // tree depth
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(recordOffset + 2, 4), 1); // root node
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(recordOffset + 6, 4), leafRecords);
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(recordOffset + 10, 4), 1); // first leaf
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(recordOffset + 14, 4), lastLeaf);
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(recordOffset + 18, 2), 512); // node size
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(recordOffset + 20, 2), 37); // max key length
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(recordOffset + 22, 4), totalNodes);
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(recordOffset + 26, 4), 0); // free nodes

        WriteRecordOffsetTable(node, new[] { recordOffset }, 64);
        return node;
    }

    private static byte[] BuildClassicHfsCatalogLeafNode(uint timestamp, uint fileSize, ushort fileStartAllocationBlock, bool useOverflowExtent = false, uint resourceForkSize = 0, ushort resourceForkStartBlock = 0, byte[]? fileNameBytes = null, bool useResourceOverflowExtent = false, uint nextNode = 0, uint previousNode = 0, uint fileCnid = 16, bool includeFolder = true, byte[]? finderInfo = null)
    {
        var node = new byte[512];
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(0, 4), nextNode);
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(4, 4), previousNode);
        node[8] = unchecked((byte)-1); // leaf node
        node[9] = 1;

        var records = new List<byte[]>
        {
            BuildClassicHfsFileRecord(2, fileNameBytes ?? System.Text.Encoding.ASCII.GetBytes("HELLO.TXT"), fileCnid, timestamp, fileSize, fileStartAllocationBlock, useOverflowExtent, resourceForkSize, resourceForkStartBlock, useResourceOverflowExtent, finderInfo)
        };
        if (includeFolder)
        {
            records.Add(BuildClassicHfsFolderRecord(2, "FOLDER", 17, timestamp));
        }

        var offsets = new int[records.Count];
        var cursor = 14;
        for (var i = 0; i < records.Count; i++)
        {
            offsets[i] = cursor;
            records[i].CopyTo(node.AsSpan(cursor));
            cursor += records[i].Length;
            if ((cursor & 1) != 0) cursor++;
        }

        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10, 2), (ushort)records.Count);
        WriteRecordOffsetTable(node, offsets, cursor);
        return node;
    }

    private static byte[] BuildClassicHfsExtentsOverflowLeafNode(uint fileId, ushort forkBlockIndex, ushort overflowStartBlock)
        => BuildClassicHfsExtentsOverflowLeafNode(new[] { BuildClassicHfsExtentsOverflowRecord(fileId, 0, forkBlockIndex, overflowStartBlock) });

    private static byte[] BuildClassicHfsExtentsOverflowLeafNode(IReadOnlyList<byte[]> records)
    {
        var node = new byte[512];
        node[8] = unchecked((byte)-1); // leaf node
        node[9] = 1;

        var offsets = new int[records.Count];
        var cursor = 14;
        for (var i = 0; i < records.Count; i++)
        {
            offsets[i] = cursor;
            records[i].CopyTo(node.AsSpan(cursor));
            cursor += records[i].Length;
            if ((cursor & 1) != 0) cursor++;
        }

        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10, 2), (ushort)records.Count);
        WriteRecordOffsetTable(node, offsets, cursor);
        return node;
    }

    private static byte[] BuildClassicHfsExtentsOverflowRecord(uint fileId, ushort forkBlockIndex, ushort overflowStartBlock)
        => BuildClassicHfsExtentsOverflowRecord(fileId, 0, forkBlockIndex, overflowStartBlock);

    private static byte[] BuildClassicHfsExtentsOverflowRecord(uint fileId, sbyte forkType, ushort forkBlockIndex, ushort overflowStartBlock)
    {
        var record = new byte[20];
        record[0] = 7; // key length
        record[1] = unchecked((byte)forkType);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(2, 4), fileId);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(6, 2), forkBlockIndex);
        WriteClassicExtent(record.AsSpan(8, 4), overflowStartBlock, 1);
        return record;
    }

    private static byte[] BuildClassicHfsFileRecord(uint parentId, string name, uint cnid, uint timestamp, uint fileSize, ushort dataStartBlock, bool useOverflowExtent = false, uint resourceForkSize = 0, ushort resourceForkStartBlock = 0, byte[]? finderInfo = null)
        => BuildClassicHfsFileRecord(parentId, System.Text.Encoding.ASCII.GetBytes(name), cnid, timestamp, fileSize, dataStartBlock, useOverflowExtent, resourceForkSize, resourceForkStartBlock, finderInfo: finderInfo);

    private static byte[] BuildClassicHfsFileRecord(uint parentId, byte[] nameBytes, uint cnid, uint timestamp, uint fileSize, ushort dataStartBlock, bool useOverflowExtent = false, uint resourceForkSize = 0, ushort resourceForkStartBlock = 0, bool useResourceOverflowExtent = false, byte[]? finderInfo = null)
    {
        var key = BuildClassicCatalogKey(parentId, nameBytes);
        var dataOffset = key.Length;
        if ((dataOffset & 1) != 0) dataOffset++;
        var record = new byte[dataOffset + 102];
        key.CopyTo(record.AsSpan(0));

        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(dataOffset + 0, 2), 0x0200);
        if (finderInfo is not null)
        {
            if (finderInfo.Length != 32)
            {
                throw new ArgumentOutOfRangeException(nameof(finderInfo), "Finder Info must be exactly 32 bytes.");
            }

            finderInfo.AsSpan(0, 16).CopyTo(record.AsSpan(dataOffset + 4, 16));
            finderInfo.AsSpan(16, 16).CopyTo(record.AsSpan(dataOffset + 56, 16));
        }
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 20, 4), cnid);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(dataOffset + 24, 2), dataStartBlock);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 26, 4), fileSize);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 30, 4), 512);
        if (resourceForkSize > 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(dataOffset + 34, 2), resourceForkStartBlock);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 36, 4), resourceForkSize);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 40, 4), 512);
        }
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 44, 4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 48, 4), timestamp);
        WriteClassicExtent(record.AsSpan(dataOffset + 74, 4), dataStartBlock, 1);
        if (useOverflowExtent)
        {
            WriteClassicExtent(record.AsSpan(dataOffset + 78, 4), 40, 1);
            WriteClassicExtent(record.AsSpan(dataOffset + 82, 4), 48, 1);
        }
        if (resourceForkSize > 0)
        {
            WriteClassicExtent(record.AsSpan(dataOffset + 86, 4), resourceForkStartBlock, 1);
            if (useResourceOverflowExtent)
            {
                WriteClassicExtent(record.AsSpan(dataOffset + 90, 4), 80, 1);
                WriteClassicExtent(record.AsSpan(dataOffset + 94, 4), 88, 1);
            }
        }
        return record;
    }

    private static byte[] BuildClassicHfsFolderRecord(uint parentId, string name, uint cnid, uint timestamp)
    {
        var key = BuildClassicCatalogKey(parentId, name);
        var dataOffset = key.Length;
        if ((dataOffset & 1) != 0) dataOffset++;
        var record = new byte[dataOffset + 70];
        key.CopyTo(record.AsSpan(0));

        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(dataOffset + 0, 2), 0x0100);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 6, 4), cnid);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 10, 4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(dataOffset + 14, 4), timestamp);
        return record;
    }

    private static byte[] BuildClassicCatalogKey(uint parentId, string name)
    {
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        return BuildClassicCatalogKey(parentId, nameBytes);
    }

    private static byte[] BuildClassicCatalogKey(uint parentId, byte[] nameBytes)
    {
        if (nameBytes.Length > 31) throw new ArgumentOutOfRangeException(nameof(nameBytes), "Classic HFS names are limited to 31 bytes.");
        var keyLength = 6 + nameBytes.Length;
        var key = new byte[1 + keyLength];
        key[0] = (byte)keyLength;
        key[1] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(2, 4), parentId);
        key[6] = (byte)nameBytes.Length;
        nameBytes.CopyTo(key.AsSpan(7));
        return key;
    }

    private static void WriteRecordOffsetTable(byte[] node, IReadOnlyList<int> recordOffsets, int freeOffset)
    {
        var count = recordOffsets.Count;
        var freeSpacePos = node.Length - 2;
        for (var i = 0; i < count; i++)
        {
            var entryPos = freeSpacePos - ((count - i) * 2);
            BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(entryPos, 2), (ushort)recordOffsets[i]);
        }
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(freeSpacePos, 2), (ushort)freeOffset);
    }

    private static void WriteClassicExtent(Span<byte> target, ushort startBlock, ushort blockCount)
    {
        BinaryPrimitives.WriteUInt16BigEndian(target[..2], startBlock);
        BinaryPrimitives.WriteUInt16BigEndian(target.Slice(2, 2), blockCount);
    }

    private static void WriteClassicPascalString(Span<byte> target, string value)
    {
        target.Clear();
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        var length = Math.Min(bytes.Length, target.Length - 1);
        target[0] = (byte)length;
        bytes.AsSpan(0, length).CopyTo(target[1..]);
    }

    private static void WriteHfsPlusForkData(Span<byte> target, int logicalSize, uint blockSize, uint startBlock, uint blockCount)
        => WriteHfsPlusForkData(target, logicalSize, blockSize, new[] { (startBlock, blockCount) });

    private static void WriteHfsPlusForkData(Span<byte> target, int logicalSize, uint blockSize, IReadOnlyList<(uint StartBlock, uint BlockCount)> extents)
    {
        target.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(target[..8], (ulong)logicalSize);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(8, 4), blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(12, 4), extents.Aggregate(0u, (sum, ext) => sum + ext.BlockCount));
        for (var i = 0; i < extents.Count && i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target.Slice(16 + i * 8, 4), extents[i].StartBlock);
            BinaryPrimitives.WriteUInt32BigEndian(target.Slice(20 + i * 8, 4), extents[i].BlockCount);
        }
    }

    private static async Task WriteHfsPlusExtentsOverflowTreeAsync(
        FileStream stream,
        uint blockSize,
        uint extentsStartBlock,
        uint leafStartBlock,
        uint fileCnid,
        uint startBlock,
        IReadOnlyList<(uint StartBlock, uint BlockCount)> overflowExtents)
    {
        const int nodeSize = 8192;

        var header = new byte[nodeSize];
        stream.Position = (long)extentsStartBlock * blockSize;
        await stream.ReadExactlyAsync(header);

        header[8] = 1; // header node
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14, 2), 1); // treeDepth
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), 1); // rootNode
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), 1); // leafRecords
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), 1); // firstLeafNode
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28, 4), 1); // lastLeafNode
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(32, 2), nodeSize);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(34, 2), 10);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36, 4), 2); // totalNodes
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(40, 4), 0); // freeNodes

        const int mapRecordOffset = 252;
        header[mapRecordOffset] = 0xC0; // nodes 0 and 1 allocated

        stream.Position = (long)extentsStartBlock * blockSize;
        await stream.WriteAsync(header);

        var leaf = BuildHfsPlusExtentsOverflowLeafNode(fileCnid, 0xFF, startBlock, overflowExtents);
        stream.Position = (long)leafStartBlock * blockSize;
        await stream.WriteAsync(leaf);
    }

    private static byte[] BuildHfsPlusExtentsOverflowLeafNode(
        uint fileCnid,
        byte forkType,
        uint startBlock,
        IReadOnlyList<(uint StartBlock, uint BlockCount)> overflowExtents)
    {
        const int nodeSize = 8192;
        var node = new byte[nodeSize];
        node[8] = 0; // leaf node for the current extents-overflow reader
        node[9] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10, 2), 1);

        var record = new byte[12 + 64];
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(0, 2), 10);
        record[2] = forkType;
        record[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4, 4), fileCnid);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(8, 4), startBlock);

        for (var i = 0; i < overflowExtents.Count && i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(12 + i * 8, 4), overflowExtents[i].StartBlock);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(16 + i * 8, 4), overflowExtents[i].BlockCount);
        }

        record.CopyTo(node.AsSpan(14));
        WriteRecordOffsetTable(node, new[] { 14 }, 14 + record.Length);
        return node;
    }

    private static (int Offset, int Length) GetBTreeRecordOffsetAndLength(byte[] node, int recordIndex, int recordCount)
    {
        if (recordIndex < 0 || recordIndex >= recordCount)
        {
            return (0, 0);
        }

        var nodeSize = node.Length;
        var offsetEntry = nodeSize - 2 * (recordCount + 1 - recordIndex);
        var nextEntry = nodeSize - 2 * (recordCount - recordIndex);
        if (offsetEntry < 0 || nextEntry < 0 || nextEntry + 2 > node.Length)
        {
            return (0, 0);
        }

        var offset = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(offsetEntry, 2));
        var nextOffset = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(nextEntry, 2));
        if (offset < 14 || nextOffset <= offset)
        {
            return (offset, 0);
        }

        return (offset, nextOffset - offset);
    }

    private static uint ToHfsTimestamp(DateTimeOffset value)
    {
        const long hfsToUnixSeconds = 2082844800L;
        return checked((uint)(value.ToUnixTimeSeconds() + hfsToUnixSeconds));
    }

    private static void WriteApmString(Span<byte> target, string value)
    {
        target.Clear();
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, target.Length)).CopyTo(target);
    }

    private sealed class FileImageDeviceFactory : IRawBlockDeviceFactory
    {
        public Task<IRawBlockDevice> OpenReadOnlyAsync(string physicalDrivePath, CancellationToken cancellationToken = default)
            => Task.FromResult<IRawBlockDevice>(FileBackedBlockDevice.Open(physicalDrivePath, writable: false));
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

    private sealed class HfsxCaseSensitiveTestProvider : IRawFileSystemProvider
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal)
        {
            ["\\Report.txt"] = System.Text.Encoding.UTF8.GetBytes("mixed-case payload"),
            ["\\report.txt"] = System.Text.Encoding.UTF8.GetBytes("lower-case payload")
        };

        public string FileSystemType => "HFSX";
        public long TotalBytes => 1024;
        public long FreeBytes => 512;
        public bool IsWritable => true;

        public RawFsEntry? GetEntry(string path)
        {
            var normalized = NormalizePath(path);
            if (normalized == "\\")
            {
                return new RawFsEntry("\\", "ROOT", true, 0, DateTimeOffset.UtcNow, FileAttributes.Directory);
            }

            return _files.TryGetValue(normalized, out var data)
                ? new RawFsEntry(normalized, normalized[(normalized.LastIndexOf('\\') + 1)..], false, data.Length, DateTimeOffset.UtcNow)
                : null;
        }

        public IReadOnlyList<RawFsEntry> ListDirectory(string path)
        {
            if (NormalizePath(path) != "\\")
            {
                return Array.Empty<RawFsEntry>();
            }

            return _files
                .Select(file => new RawFsEntry(file.Key, file.Key[(file.Key.LastIndexOf('\\') + 1)..], false, file.Value.Length, DateTimeOffset.UtcNow))
                .ToArray();
        }

        public int ReadFile(string path, long offset, Span<byte> destination)
        {
            var normalized = NormalizePath(path);
            if (!_files.TryGetValue(normalized, out var data) || offset < 0 || offset >= data.Length)
            {
                return 0;
            }

            var count = (int)Math.Min(destination.Length, data.Length - offset);
            data.AsSpan((int)offset, count).CopyTo(destination);
            return count;
        }

        public int WriteFile(string path, long offset, ReadOnlySpan<byte> source)
        {
            var normalized = NormalizePath(path);
            if (!_files.TryGetValue(normalized, out var data) || offset < 0 || offset > data.Length)
            {
                return 0;
            }

            var target = data.ToArray();
            var count = Math.Min(source.Length, target.Length - (int)offset);
            source[..count].CopyTo(target.AsSpan((int)offset, count));
            _files[normalized] = target;
            return count;
        }

        public void Dispose()
        {
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/" || path == "\\") return "\\";
            var p = path.Replace('/', '\\');
            return p.StartsWith('\\') ? p : "\\" + p.TrimStart('\\');
        }
    }

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
