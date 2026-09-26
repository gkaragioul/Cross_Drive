using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CrossDrive.RawDiskEngine;

internal sealed class ApfsRawFileSystemProvider : IRawFileSystemProvider
{
    private const int MaxVolumeHintsForView = 64;
    private const int MaxPreviewEntriesPerVolume = 48;
    private const ulong DefaultApfsRootDirectoryId = 2;
    private readonly IRawBlockDevice _device;
    private readonly uint _blockSize;
    private readonly long _partitionOffsetBytes;
    private readonly Dictionary<string, RawFsEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<RawFsEntry>> _dirChildren = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApfsFileReadPlan> _fileReadPlans = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Offset, byte[] Data)> _readCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _infoBytes;
    private readonly bool _writable;
    private readonly ApfsWriter? _writer;
    private readonly ApfsBlockAllocator? _allocator;
    private readonly Dictionary<string, uint> _cnidByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private ApfsRawFileSystemProvider(IRawBlockDevice device, ApfsContainerSummary summary, MountPlan plan, bool writable, ApfsSpacemanReader? spaceman = null)
    {
        _device = device;
        _blockSize = summary.BlockSize;
        _partitionOffsetBytes = Math.Max(0, plan.PartitionOffsetBytes);
        _writable = writable && device.CanWrite;
        FileSystemType = "APFS";
        TotalBytes = summary.EstimatedTotalBytes > 0 ? summary.EstimatedTotalBytes : Math.Max(1, plan.TotalBytes);
        FreeBytes = spaceman is not null
            ? (long)Math.Min((decimal)long.MaxValue, (decimal)spaceman.FreeBlockCount * _blockSize)
            : TotalBytes;

        // Initialize write support if writable
        if (_writable)
        {
            _allocator = spaceman is not null
                ? new ApfsBlockAllocator(spaceman)
                : new ApfsBlockAllocator(device, _blockSize, summary.BlockCount, _partitionOffsetBytes);
            // Get the first volume OID and its physical superblock block for the writer.
            var volumeOid = summary.VolumeObjectIds.FirstOrDefault();
            var volumePtr = summary.ResolvedVolumePointers.FirstOrDefault();
            _writer = new ApfsWriter(
                device, _allocator, _blockSize, _partitionOffsetBytes,
                volumeOid,
                volumeSuperblockBlock: volumePtr?.PhysicalBlockNumber,
                currentXid: summary.TransactionId);
        }

        var now = DateTimeOffset.UtcNow;
        var infoText = BuildInfoText(summary, plan);
        _infoBytes = Encoding.UTF8.GetBytes(infoText);

        var root = new RawFsEntry("\\", "ROOT", true, 0, now, FileAttributes.Directory);
        _entries[root.Path] = root;

        var primaryVolume = SelectPrimaryVolumePreview(summary);
        if (primaryVolume is not null)
        {
            if (!PopulateVolumeCatalog("\\", primaryVolume, now))
            {
                AddPreviewEntries("\\", primaryVolume, now);
            }
            return;
        }

        var volumesDir = new RawFsEntry("\\Volumes", "Volumes", true, 0, now, FileAttributes.Directory);
        var infoFile = new RawFsEntry("\\APFS_CONTAINER_INFO.txt", "APFS_CONTAINER_INFO.txt", false, _infoBytes.Length, now, FileAttributes.ReadOnly);

        _entries[volumesDir.Path] = volumesDir;
        _entries[infoFile.Path] = infoFile;

        var rootChildren = new List<RawFsEntry> { volumesDir, infoFile };
        var volumeChildren = new List<RawFsEntry>();
        var usedVolumeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oid in summary.VolumeObjectIds)
        {
            summary.VolumePreviewsByOid.TryGetValue(oid, out var preview);
            var name = BuildVolumeFolderName(preview, oid, usedVolumeNames);
            var path = $"\\Volumes\\{name}";
            var entry = new RawFsEntry(path, name, true, 0, now, FileAttributes.Directory);
            _entries[path] = entry;
            volumeChildren.Add(entry);

            if (preview is not null)
            {
                if (!PopulateVolumeCatalog(path, preview, now))
                {
                    AddPreviewEntries(path, preview, now);
                }
            }
            else
            {
                _dirChildren[path] = Array.Empty<RawFsEntry>();
            }
        }
        foreach (var hint in summary.VolumeSuperblockHints.Take(MaxVolumeHintsForView))
        {
            var name = $"Checkpoint_{hint.BlockNumber:X}_OID_{hint.ObjectId:X}";
            var path = $"\\Volumes\\{name}";
            if (_entries.ContainsKey(path))
            {
                continue;
            }
            var entry = new RawFsEntry(path, name, true, 0, now, FileAttributes.Directory);
            _entries[path] = entry;
            volumeChildren.Add(entry);
        }

        _dirChildren["\\"] = rootChildren;
        _dirChildren["\\Volumes"] = volumeChildren;
    }

    public string FileSystemType { get; }
    public long TotalBytes { get; }
    public long FreeBytes { get; }
    public bool IsWritable => _writable;

    public static async Task<ApfsRawFileSystemProvider> CreateAsync(MountPlan plan, CancellationToken cancellationToken = default)
    {
        var basePath = plan.PhysicalDrivePath;
        var hashIdx = basePath.IndexOf("#part", StringComparison.OrdinalIgnoreCase);
        if (hashIdx > 0)
        {
            basePath = basePath[..hashIdx];
        }

        // Open device in read-write mode if plan is writable
        IRawBlockDevice device;
        var factory = new WindowsRawBlockDeviceFactory();
        if (plan.Writable)
        {
            device = await factory.OpenReadWriteAsync(basePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            device = await factory.OpenReadOnlyAsync(basePath, cancellationToken).ConfigureAwait(false);
        }

        if (plan.EncryptionKey is not null && plan.EncryptionKey.Length >= 32)
        {
            var containerBlockSize = 4096u;
            device = new DecryptingRawBlockDevice(device, plan.EncryptionKey, containerBlockSize);
        }

        try
        {
            var reader = new ApfsMetadataReader(device, plan.PartitionOffsetBytes, plan.PartitionLengthBytes);
            var summary = await reader.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);

            ApfsSpacemanReader? spaceman = null;
            if (summary.SpacemanPhysicalBlock.HasValue)
            {
                try
                {
                    spaceman = await ApfsSpacemanReader.LoadAsync(
                        device,
                        summary.SpacemanPhysicalBlock.Value,
                        summary.BlockSize,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Non-fatal: fall back to an optimistic read-only capacity hint
                    // so Explorer does not present APFS as a full drive.
                    System.Diagnostics.Debug.WriteLine($"[ApfsRawFileSystemProvider] Spaceman load failed: {ex.Message}");
                }
            }

            return new ApfsRawFileSystemProvider(device, summary, plan, plan.Writable, spaceman);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static readonly HashSet<string> MacMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fseventsd", ".Spotlight-V100", ".Trashes", ".TemporaryItems",
        ".DocumentRevisions-V100", ".vol", ".com.apple.timemachine.donotpresent",
        ".MobileBackups", ".PKInstallSandboxManager", ".com.apple.bootpicker.history",
        ".com.apple.recovery.boot", ".dbfseventsd", ".hotfiles.btree"
    };

    private static bool IsMacMetadata(string name)
    {
        return MacMetadataNames.Contains(name) || name.StartsWith("._", StringComparison.Ordinal);
    }

    public RawFsEntry? GetEntry(string path)
    {
        var n = Normalize(path);
        return _entries.TryGetValue(n, out var entry) ? entry : null;
    }

    public IReadOnlyList<RawFsEntry> ListDirectory(string path)
    {
        var n = Normalize(path);
        return _dirChildren.TryGetValue(n, out var list) ? list : Array.Empty<RawFsEntry>();
    }

    public int ReadFile(string path, long offset, Span<byte> destination)
    {
        var n = Normalize(path);
        if (string.Equals(n, "\\APFS_CONTAINER_INFO.txt", StringComparison.OrdinalIgnoreCase))
        {
            if (offset < 0 || offset >= _infoBytes.Length || destination.Length <= 0)
            {
                return 0;
            }

            var available = _infoBytes.Length - (int)offset;
            var count = Math.Min(destination.Length, available);
            _infoBytes.AsSpan((int)offset, count).CopyTo(destination);
            return count;
        }

        if (!_fileReadPlans.TryGetValue(n, out var readPlan))
        {
            return 0;
        }
        if (offset < 0 || destination.Length <= 0)
        {
            return 0;
        }
        if (offset >= readPlan.TotalSize)
        {
            return 0;
        }

        if (readPlan.IsCompressed)
        {
            byte[]? decompressed = null;
            if (readPlan.InlineData is not null)
            {
                decompressed = TryDecompressInlineDecmpfs(readPlan.InlineData);
                if (decompressed is null && readPlan.CompressionResourceFork is not null)
                {
                    var requestedCount = Math.Min(destination.Length, (int)Math.Min(int.MaxValue, readPlan.TotalSize - offset));
                    var streamedRange = TryDecompressResourceForkDecmpfsRange(
                        readPlan.InlineData,
                        (forkOffset, forkCount) => TryReadPlanRange(readPlan.CompressionResourceFork, forkOffset, forkCount),
                        readPlan.CompressionResourceFork.TotalSize,
                        offset,
                        requestedCount);
                    if (streamedRange is not null)
                    {
                        streamedRange.AsSpan().CopyTo(destination);
                        return streamedRange.Length;
                    }

                    var resourceForkBytes = TryReadPlanBytes(
                        readPlan.CompressionResourceFork,
                        ApfsDecmpfs.MaxCompressedResourceForkSize);
                    if (resourceForkBytes is not null)
                    {
                        var range = TryDecompressResourceForkDecmpfsRange(
                            readPlan.InlineData,
                            resourceForkBytes,
                            offset,
                            requestedCount);
                        if (range is not null)
                        {
                            range.AsSpan().CopyTo(destination);
                            return range.Length;
                        }

                        decompressed = TryDecompressResourceForkDecmpfs(readPlan.InlineData, resourceForkBytes);
                    }
                }
            }
            if (decompressed is not null)
            {
                if (offset >= decompressed.Length) return 0;
                var available = decompressed.Length - (int)offset;
                var count = Math.Min(destination.Length, available);
                if (count > 0)
                {
                    decompressed.AsSpan((int)offset, count).CopyTo(destination);
                }
                return count;
            }
            return 0;
        }

        if (readPlan.InlineData is not null && readPlan.Extents.Count == 0)
        {
            var available = readPlan.InlineData.Length - (int)offset;
            var count = Math.Min(destination.Length, Math.Max(0, available));
            if (count > 0)
            {
                readPlan.InlineData.AsSpan((int)offset, count).CopyTo(destination);
            }
            return count;
        }

        if (readPlan.InlineData is not null && readPlan.Extents.Count > 0)
        {
            var compositeWritten = ReadInlinePrefixedExtentBackedFile(_device, _partitionOffsetBytes, _blockSize, readPlan, offset, destination);
            if (compositeWritten > 0 && compositeWritten <= 131072)
            {
                var snap = destination.Slice(0, compositeWritten).ToArray();
                _readCache[n] = (offset, snap);
            }

            return compositeWritten;
        }

        if (destination.Length <= 131072 &&
            _readCache.TryGetValue(n, out var cached) &&
            offset >= cached.Offset &&
            offset + destination.Length <= cached.Offset + cached.Data.Length)
        {
            var src = (int)(offset - cached.Offset);
            cached.Data.AsSpan(src, destination.Length).CopyTo(destination);
            return destination.Length;
        }

        var written = ReadExtentBackedFile(_device, _partitionOffsetBytes, _blockSize, readPlan, offset, destination);
        if (written > 0 && written <= 131072)
        {
            var snap = destination.Slice(0, written).ToArray();
            _readCache[n] = (offset, snap);
        }

        return written;
    }

    private byte[]? TryReadPlanBytes(ApfsFileReadPlan readPlan, long maxBytes)
    {
        if (readPlan.TotalSize <= 0 || readPlan.TotalSize > maxBytes || readPlan.TotalSize > int.MaxValue)
        {
            return null;
        }

        var output = new byte[(int)readPlan.TotalSize];
        var read = readPlan.InlineData is not null && readPlan.Extents.Count == 0
            ? CopyInlinePlanBytes(readPlan, output)
            : readPlan.InlineData is not null
                ? ReadInlinePrefixedExtentBackedFile(_device, _partitionOffsetBytes, _blockSize, readPlan, 0, output)
                : ReadExtentBackedFile(_device, _partitionOffsetBytes, _blockSize, readPlan, 0, output);
        return read == output.Length ? output : null;
    }

    private byte[]? TryReadPlanRange(ApfsFileReadPlan readPlan, long offset, int count)
    {
        if (offset < 0 || count <= 0 || offset >= readPlan.TotalSize)
        {
            return null;
        }

        var logicalBytes = (int)Math.Min(count, readPlan.TotalSize - offset);
        if (logicalBytes <= 0)
        {
            return null;
        }

        var output = new byte[logicalBytes];
        int read;
        if (readPlan.InlineData is not null && readPlan.Extents.Count == 0)
        {
            output.AsSpan().Clear();
            var inlineStart = 0L;
            var inlineEnd = readPlan.InlineData.Length;
            var targetEnd = offset + logicalBytes;
            if (inlineEnd > offset && inlineStart < targetEnd)
            {
                var copyStart = Math.Max(inlineStart, offset);
                var copyEnd = Math.Min(inlineEnd, targetEnd);
                var sourceOffset = (int)(copyStart - inlineStart);
                var targetOffset = (int)(copyStart - offset);
                var copyLength = (int)(copyEnd - copyStart);
                readPlan.InlineData.AsSpan(sourceOffset, copyLength).CopyTo(output.AsSpan(targetOffset, copyLength));
            }

            read = logicalBytes;
        }
        else
        {
            read = readPlan.InlineData is not null
                ? ReadInlinePrefixedExtentBackedFile(_device, _partitionOffsetBytes, _blockSize, readPlan, offset, output)
                : ReadExtentBackedFile(_device, _partitionOffsetBytes, _blockSize, readPlan, offset, output);
        }

        return read == logicalBytes ? output : null;
    }

    private static int CopyInlinePlanBytes(ApfsFileReadPlan readPlan, Span<byte> destination)
    {
        if (readPlan.InlineData is null || destination.Length == 0)
        {
            return 0;
        }

        var count = Math.Min(destination.Length, readPlan.InlineData.Length);
        readPlan.InlineData.AsSpan(0, count).CopyTo(destination);
        return count;
    }

    internal static int ReadExtentBackedFile(
        IRawBlockDevice device,
        long partitionOffsetBytes,
        uint blockSize,
        ApfsFileReadPlan readPlan,
        long offset,
        Span<byte> destination)
    {
        if (offset < 0 || destination.Length <= 0 || offset >= readPlan.TotalSize)
        {
            return 0;
        }

        var logicalBytes = (int)Math.Min(destination.Length, readPlan.TotalSize - offset);
        if (logicalBytes <= 0)
        {
            return 0;
        }

        var target = destination[..logicalBytes];
        target.Clear();
        var targetStart = offset;
        var targetEnd = offset + logicalBytes;

        foreach (var extent in readPlan.Extents)
        {
            var extentStart = extent.LogicalOffset;
            var extentEnd = extent.LogicalOffset + extent.Length;
            if (extentEnd <= targetStart || extentStart >= targetEnd)
            {
                continue;
            }

            var copyStart = Math.Max(extentStart, targetStart);
            var copyEnd = Math.Min(extentEnd, targetEnd);
            var bytesToRead = (int)Math.Max(0, copyEnd - copyStart);
            if (bytesToRead <= 0)
            {
                continue;
            }

            var srcOffInExtent = copyStart - extentStart;
            var dstOff = (int)(copyStart - targetStart);
            var deviceOffset = checked(partitionOffsetBytes + (long)(extent.PhysicalBlockNumber * blockSize) + srcOffInExtent);

            var temp = new byte[bytesToRead];
            var read = device.ReadAsync(deviceOffset, temp, bytesToRead).AsTask().GetAwaiter().GetResult();
            if (read <= 0)
            {
                continue;
            }

            temp.AsSpan(0, read).CopyTo(target.Slice(dstOff, read));
        }

        return logicalBytes;
    }

    internal static int ReadInlinePrefixedExtentBackedFile(
        IRawBlockDevice device,
        long partitionOffsetBytes,
        uint blockSize,
        ApfsFileReadPlan readPlan,
        long offset,
        Span<byte> destination)
    {
        if (readPlan.InlineData is null)
        {
            return ReadExtentBackedFile(device, partitionOffsetBytes, blockSize, readPlan, offset, destination);
        }
        if (offset < 0 || destination.Length <= 0 || offset >= readPlan.TotalSize)
        {
            return 0;
        }

        var logicalBytes = (int)Math.Min(destination.Length, readPlan.TotalSize - offset);
        if (logicalBytes <= 0)
        {
            return 0;
        }

        var target = destination[..logicalBytes];
        target.Clear();

        var inlineStart = 0L;
        var inlineEnd = readPlan.InlineData.Length;
        var targetStart = offset;
        var targetEnd = offset + logicalBytes;
        if (inlineEnd > targetStart && inlineStart < targetEnd)
        {
            var copyStart = Math.Max(inlineStart, targetStart);
            var copyEnd = Math.Min(inlineEnd, targetEnd);
            var srcOff = (int)(copyStart - inlineStart);
            var dstOff = (int)(copyStart - targetStart);
            var count = (int)(copyEnd - copyStart);
            readPlan.InlineData.AsSpan(srcOff, count).CopyTo(target.Slice(dstOff, count));
        }

        foreach (var extent in readPlan.Extents)
        {
            var extentStart = extent.LogicalOffset;
            var extentEnd = extent.LogicalOffset + extent.Length;
            if (extentEnd <= targetStart || extentStart >= targetEnd)
            {
                continue;
            }

            var copyStart = Math.Max(extentStart, targetStart);
            var copyEnd = Math.Min(extentEnd, targetEnd);
            var bytesToRead = (int)Math.Max(0, copyEnd - copyStart);
            if (bytesToRead <= 0)
            {
                continue;
            }

            var srcOffInExtent = copyStart - extentStart;
            var dstOff = (int)(copyStart - targetStart);
            var deviceOffset = checked(partitionOffsetBytes + (long)(extent.PhysicalBlockNumber * blockSize) + srcOffInExtent);

            var temp = new byte[bytesToRead];
            var read = device.ReadAsync(deviceOffset, temp, bytesToRead).AsTask().GetAwaiter().GetResult();
            if (read <= 0)
            {
                continue;
            }

            temp.AsSpan(0, read).CopyTo(target.Slice(dstOff, read));
        }

        return logicalBytes;
    }

    internal static long GetExtentBackedLogicalSize(
        IReadOnlyList<ApfsFileExtent> orderedExtents,
        long? inodeLogicalSize,
        byte[]? inlineData,
        bool isCompressed)
    {
        var totalSize = orderedExtents.Count == 0
            ? 0
            : orderedExtents.Max(x => x.LogicalOffset + x.Length);
        if (inodeLogicalSize.HasValue && inodeLogicalSize.Value > totalSize)
        {
            totalSize = inodeLogicalSize.Value;
        }
        if (isCompressed && inlineData is not null)
        {
            totalSize = ApfsDecmpfs.TryReadInlineUncompressedSize(inlineData) ?? totalSize;
        }
        return totalSize;
    }

    private static byte[]? TryDecompressInlineDecmpfs(byte[] inlineData)
    {
        // decmpfs header: 4-byte magic + 4-byte type + 8-byte uncompressed_size.
        if (inlineData.Length < ApfsDecmpfs.HeaderLength) return null;

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(inlineData.AsSpan(0, 4));
        if (magic != 0x636D7066) return null; // "fpmc" LE

        var compressionType = BinaryPrimitives.ReadUInt32LittleEndian(inlineData.AsSpan(4, 4));
        var uncompressedSize = TryReadInlineDecmpfsUncompressedSize(inlineData);
        if (!uncompressedSize.HasValue) return null;
        if (uncompressedSize.Value > ApfsDecmpfs.MaxFullDecompressionSize)
        {
            return null;
        }

        // Type 1: uncompressed data stored inline after the header (rare, small files)
        if (compressionType == 1 && inlineData.Length > ApfsDecmpfs.HeaderLength)
        {
            var data = new byte[(int)uncompressedSize.Value];
            var residentBytes = Math.Min(data.Length, inlineData.Length - ApfsDecmpfs.HeaderLength);
            Array.Copy(inlineData, ApfsDecmpfs.HeaderLength, data, 0, residentBytes);
            return data;
        }

        // Type 3: zlib-compressed data stored inline after the decmpfs header.
        if (compressionType == 3 && inlineData.Length > ApfsDecmpfs.HeaderLength)
        {
            try
            {
                var compressedPayload = inlineData.AsSpan(ApfsDecmpfs.HeaderLength);
                using var inputStream = new MemoryStream(compressedPayload.ToArray());

                // APFS zlib inline data uses raw deflate (no zlib header) when the first byte
                // is NOT 0x78. If it IS 0x78 it's a standard zlib stream (skip 2-byte header).
                if (compressedPayload.Length > 0 && compressedPayload[0] == 0x78)
                {
                    inputStream.Position = 2; // skip zlib header (CMF + FLG)
                }

                using var deflate = new System.IO.Compression.DeflateStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream((int)uncompressedSize.Value);
                deflate.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                return null; // decompression failed, fall back to returning 0 bytes
            }
        }

        // Types 4, 8, 12: resource-fork variants — compressed payload lives in the
        //   resource fork rather than inline. Let the caller route those through
        //   TryDecompressResourceForkDecmpfs once it has the matching resource fork.
        if (compressionType is 4 or 8 or 12)
        {
            return null;
        }

        // Type 11: LZFSE-compressed inline. Decoder is significantly larger than
        //   LZVN (FSE entropy coding); not yet ported.
        // For all of these we log the type so the caller can produce a specific
        // diagnostic instead of a silent zero-byte read.
        Console.Error.WriteLine($"[APFS] decmpfs: unsupported compression type {compressionType} ({DecmpfsTypeName(compressionType)}) — file will read as 0 bytes.");
        return null;
    }

    private static byte[]? TryDecompressResourceForkDecmpfs(byte[] inlineData, byte[] resourceForkBytes)
    {
        if (inlineData.Length < ApfsDecmpfs.HeaderLength)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(inlineData.AsSpan(0, 4));
        if (magic != 0x636D7066)
        {
            return null;
        }

        var compressionType = BinaryPrimitives.ReadUInt32LittleEndian(inlineData.AsSpan(4, 4));
        var uncompressedSize = TryReadInlineDecmpfsUncompressedSize(inlineData);
        if (!uncompressedSize.HasValue)
        {
            return null;
        }

        if (compressionType != 4)
        {
            Console.Error.WriteLine($"[APFS] decmpfs: unsupported resource-fork compression type {compressionType} ({DecmpfsTypeName(compressionType)}).");
            return null;
        }

        var cmpfData = TryExtractCmpfResourceData(resourceForkBytes) ?? resourceForkBytes;
        return TryDecompressZlibResourceData(cmpfData, uncompressedSize.Value);
    }

    private static byte[]? TryDecompressResourceForkDecmpfsRange(
        byte[] inlineData,
        byte[] resourceForkBytes,
        long offset,
        int count)
    {
        return TryDecompressResourceForkDecmpfsRange(
            inlineData,
            (forkOffset, forkCount) =>
            {
                if (forkOffset > int.MaxValue ||
                    !IsRangeInside(resourceForkBytes.Length, (int)forkOffset, forkCount))
                {
                    return null;
                }

                return resourceForkBytes.AsSpan((int)forkOffset, forkCount).ToArray();
            },
            resourceForkBytes.Length,
            offset,
            count);
    }

    private static byte[]? TryDecompressResourceForkDecmpfsRange(
        byte[] inlineData,
        Func<long, int, byte[]?> readResourceForkRange,
        long resourceForkLength,
        long offset,
        int count)
    {
        if (offset < 0 || count <= 0 || inlineData.Length < ApfsDecmpfs.HeaderLength)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(inlineData.AsSpan(0, 4));
        if (magic != 0x636D7066)
        {
            return null;
        }

        var compressionType = BinaryPrimitives.ReadUInt32LittleEndian(inlineData.AsSpan(4, 4));
        var uncompressedSize = TryReadInlineDecmpfsUncompressedSize(inlineData);
        if (compressionType != 4 || !uncompressedSize.HasValue || offset >= uncompressedSize.Value)
        {
            return null;
        }

        var rangeLength = (int)Math.Min(count, uncompressedSize.Value - offset);
        if (rangeLength <= 0)
        {
            return null;
        }

        if (TryLocateCmpfResourceData(
            resourceForkLength,
            readResourceForkRange,
            out var cmpfPayloadOffset,
            out var cmpfPayloadLength))
        {
            return TryDecompressZlibResourceDataRange(
                cmpfPayloadLength,
                (cmpfOffset, cmpfCount) => readResourceForkRange(cmpfPayloadOffset + cmpfOffset, cmpfCount),
                uncompressedSize.Value,
                offset,
                rangeLength);
        }

        return TryDecompressZlibResourceDataRange(
            resourceForkLength,
            readResourceForkRange,
            uncompressedSize.Value,
            offset,
            rangeLength);
    }

    private static bool TryLocateCmpfResourceData(
        long resourceForkLength,
        Func<long, int, byte[]?> readResourceForkRange,
        out long cmpfPayloadOffset,
        out int cmpfPayloadLength)
    {
        cmpfPayloadOffset = 0;
        cmpfPayloadLength = 0;
        if (resourceForkLength < 16 || resourceForkLength > ApfsDecmpfs.MaxCompressedResourceForkSize)
        {
            return false;
        }

        var header = readResourceForkRange(0, 16);
        if (header is null || header.Length != 16)
        {
            return false;
        }

        var dataOffset = ReadUInt32BigEndianAsInt(header.AsSpan(0, 4));
        var mapOffset = ReadUInt32BigEndianAsInt(header.AsSpan(4, 4));
        var dataLength = ReadUInt32BigEndianAsInt(header.AsSpan(8, 4));
        var mapLength = ReadUInt32BigEndianAsInt(header.AsSpan(12, 4));
        if (dataOffset < 0 ||
            mapOffset < 0 ||
            dataLength < 0 ||
            mapLength < 30 ||
            !IsRangeInside(resourceForkLength, dataOffset, dataLength) ||
            !IsRangeInside(resourceForkLength, mapOffset, mapLength))
        {
            return false;
        }

        var map = readResourceForkRange(mapOffset, mapLength);
        if (map is null || map.Length != mapLength)
        {
            return false;
        }

        var typeListOffset = BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(24, 2));
        if (typeListOffset + 2 > map.Length)
        {
            return false;
        }

        var typeCount = BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(typeListOffset, 2)) + 1;
        var typeEntryStart = typeListOffset + 2;
        for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
        {
            var entryOffset = typeEntryStart + typeIndex * 8;
            if (entryOffset + 8 > map.Length)
            {
                return false;
            }

            var typeCode = BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(entryOffset, 4));
            var resourceCount = BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(entryOffset + 4, 2)) + 1;
            var refListOffset = BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(entryOffset + 6, 2));
            if (typeCode != 0x636D7066u) // "cmpf"
            {
                continue;
            }

            var refListStart = typeListOffset + refListOffset;
            for (var refIndex = 0; refIndex < resourceCount; refIndex++)
            {
                var refOffset = refListStart + refIndex * 12;
                if (refOffset + 12 > map.Length)
                {
                    return false;
                }

                var resourceDataOffset = ReadUInt24BigEndian(map.AsSpan(refOffset + 5, 3));
                var dataRecordOffset = (long)dataOffset + resourceDataOffset;
                if (!IsRangeInside(resourceForkLength, dataRecordOffset, 4) ||
                    dataRecordOffset + 4 > (long)dataOffset + dataLength)
                {
                    continue;
                }

                var lengthBytes = readResourceForkRange(dataRecordOffset, 4);
                if (lengthBytes is null || lengthBytes.Length != 4)
                {
                    continue;
                }

                var resourceLength = ReadUInt32BigEndianAsInt(lengthBytes.AsSpan(0, 4));
                var payloadOffset = dataRecordOffset + 4;
                if (resourceLength >= 0 &&
                    IsRangeInside(resourceForkLength, payloadOffset, resourceLength) &&
                    payloadOffset + resourceLength <= (long)dataOffset + dataLength)
                {
                    cmpfPayloadOffset = payloadOffset;
                    cmpfPayloadLength = resourceLength;
                    return true;
                }
            }
        }

        return false;
    }

    private static byte[]? TryExtractCmpfResourceData(byte[] resourceForkBytes)
    {
        if (resourceForkBytes.Length < 16)
        {
            return null;
        }

        var dataOffset = ReadUInt32BigEndianAsInt(resourceForkBytes.AsSpan(0, 4));
        var mapOffset = ReadUInt32BigEndianAsInt(resourceForkBytes.AsSpan(4, 4));
        var dataLength = ReadUInt32BigEndianAsInt(resourceForkBytes.AsSpan(8, 4));
        var mapLength = ReadUInt32BigEndianAsInt(resourceForkBytes.AsSpan(12, 4));
        if (!IsRangeInside(resourceForkBytes.Length, dataOffset, dataLength) ||
            !IsRangeInside(resourceForkBytes.Length, mapOffset, mapLength) ||
            mapLength < 30)
        {
            return null;
        }

        var map = resourceForkBytes.AsSpan(mapOffset, mapLength);
        var typeListOffset = BinaryPrimitives.ReadUInt16BigEndian(map.Slice(24, 2));
        if (typeListOffset + 2 > map.Length)
        {
            return null;
        }

        var typeListStart = mapOffset + typeListOffset;
        var typeCount = BinaryPrimitives.ReadUInt16BigEndian(resourceForkBytes.AsSpan(typeListStart, 2)) + 1;
        var typeEntryStart = typeListStart + 2;
        for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
        {
            var entryOffset = typeEntryStart + typeIndex * 8;
            if (entryOffset + 8 > mapOffset + mapLength)
            {
                return null;
            }

            var typeCode = BinaryPrimitives.ReadUInt32BigEndian(resourceForkBytes.AsSpan(entryOffset, 4));
            var resourceCount = BinaryPrimitives.ReadUInt16BigEndian(resourceForkBytes.AsSpan(entryOffset + 4, 2)) + 1;
            var refListOffset = BinaryPrimitives.ReadUInt16BigEndian(resourceForkBytes.AsSpan(entryOffset + 6, 2));
            if (typeCode != 0x636D7066u) // "cmpf"
            {
                continue;
            }

            var refListStart = typeListStart + refListOffset;
            for (var refIndex = 0; refIndex < resourceCount; refIndex++)
            {
                var refOffset = refListStart + refIndex * 12;
                if (refOffset + 12 > mapOffset + mapLength)
                {
                    return null;
                }

                var resourceDataOffset = ReadUInt24BigEndian(resourceForkBytes.AsSpan(refOffset + 5, 3));
                var dataRecordOffset = dataOffset + resourceDataOffset;
                if (dataRecordOffset + 4 > dataOffset + dataLength || dataRecordOffset + 4 > resourceForkBytes.Length)
                {
                    continue;
                }

                var resourceLength = ReadUInt32BigEndianAsInt(resourceForkBytes.AsSpan(dataRecordOffset, 4));
                var payloadOffset = dataRecordOffset + 4;
                if (IsRangeInside(resourceForkBytes.Length, payloadOffset, resourceLength) &&
                    payloadOffset + resourceLength <= dataOffset + dataLength)
                {
                    return resourceForkBytes.AsSpan(payloadOffset, resourceLength).ToArray();
                }
            }
        }

        return null;
    }

    private static byte[]? TryDecompressZlibResourceData(byte[] cmpfData, long uncompressedSize)
    {
        if (uncompressedSize <= 0 || uncompressedSize > ApfsDecmpfs.MaxFullDecompressionSize)
        {
            return null;
        }

        var expectedLength = (int)uncompressedSize;
        var chunked = TryDecompressChunkedZlibResourceData(cmpfData, expectedLength);
        if (chunked is not null)
        {
            return chunked;
        }

        return TryInflateDecmpfsZlibPayload(cmpfData, expectedLength);
    }

    private static byte[]? TryDecompressZlibResourceDataRange(
        byte[] cmpfData,
        long uncompressedSize,
        long offset,
        int count)
    {
        if (uncompressedSize <= 0 ||
            uncompressedSize > ApfsDecmpfs.MaxLogicalSize ||
            offset < 0 ||
            count <= 0 ||
            offset >= uncompressedSize)
        {
            return null;
        }

        var requestedLength = (int)Math.Min(count, uncompressedSize - offset);
        var chunked = TryDecompressChunkedZlibResourceDataRange(cmpfData, uncompressedSize, offset, requestedLength);
        if (chunked is not null)
        {
            return chunked;
        }

        if (uncompressedSize > ApfsDecmpfs.MaxFullDecompressionSize)
        {
            return null;
        }

        var whole = TryInflateDecmpfsZlibPayload(cmpfData, (int)uncompressedSize);
        if (whole is null)
        {
            return null;
        }

        return whole.AsSpan((int)offset, requestedLength).ToArray();
    }

    private static byte[]? TryDecompressZlibResourceDataRange(
        long cmpfLength,
        Func<long, int, byte[]?> readCmpfRange,
        long uncompressedSize,
        long offset,
        int count)
    {
        if (cmpfLength <= 0 ||
            cmpfLength > ApfsDecmpfs.MaxCompressedResourceForkSize ||
            uncompressedSize <= 0 ||
            uncompressedSize > ApfsDecmpfs.MaxLogicalSize ||
            offset < 0 ||
            count <= 0 ||
            offset >= uncompressedSize)
        {
            return null;
        }

        var requestedLength = (int)Math.Min(count, uncompressedSize - offset);
        var chunked = TryDecompressChunkedZlibResourceDataRange(cmpfLength, readCmpfRange, uncompressedSize, offset, requestedLength);
        if (chunked is not null)
        {
            return chunked;
        }

        if (uncompressedSize > ApfsDecmpfs.MaxFullDecompressionSize || cmpfLength > int.MaxValue)
        {
            return null;
        }

        var cmpfData = readCmpfRange(0, (int)cmpfLength);
        if (cmpfData is null || cmpfData.Length != cmpfLength)
        {
            return null;
        }

        var whole = TryInflateDecmpfsZlibPayload(cmpfData, (int)uncompressedSize);
        if (whole is null)
        {
            return null;
        }

        return whole.AsSpan((int)offset, requestedLength).ToArray();
    }

    private static byte[]? TryDecompressChunkedZlibResourceData(byte[] cmpfData, int expectedLength)
    {
        if (cmpfData.Length < 24)
        {
            return null;
        }

        var descriptorsOffset = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(0, 4));
        var footerOffset = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(4, 4));
        var descriptorsAndDataSize = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(8, 4));
        if (descriptorsOffset < 16 ||
            descriptorsOffset + 8 > cmpfData.Length ||
            footerOffset < descriptorsOffset ||
            footerOffset > cmpfData.Length ||
            descriptorsAndDataSize <= 0)
        {
            return null;
        }

        var compressedDataSize = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(descriptorsOffset, 4));
        var chunkCount = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(descriptorsOffset + 4, 4));
        var maxChunks = (expectedLength + ApfsDecmpfs.ChunkSize - 1) / ApfsDecmpfs.ChunkSize;
        if (chunkCount <= 0 || chunkCount > Math.Max(1, maxChunks) || compressedDataSize < 0)
        {
            return null;
        }

        var tupleStart = descriptorsOffset + 8;
        if (tupleStart + chunkCount * 8 > cmpfData.Length)
        {
            return null;
        }

        var output = new byte[expectedLength];
        var outputOffset = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            var tupleOffset = tupleStart + i * 8;
            var relativeDataOffset = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(tupleOffset, 4));
            var chunkCompressedSize = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(tupleOffset + 4, 4));
            var chunkDataOffset = 20 + relativeDataOffset;
            if (chunkCompressedSize <= 0 || !IsRangeInside(cmpfData.Length, chunkDataOffset, chunkCompressedSize))
            {
                return null;
            }

            var remaining = expectedLength - outputOffset;
            if (remaining <= 0)
            {
                return null;
            }

            var expectedChunkLength = Math.Min(ApfsDecmpfs.ChunkSize, remaining);
            var chunk = TryInflateDecmpfsZlibPayload(
                cmpfData.AsSpan(chunkDataOffset, chunkCompressedSize),
                expectedChunkLength);
            if (chunk is null || chunk.Length > remaining)
            {
                return null;
            }

            chunk.CopyTo(output.AsSpan(outputOffset));
            outputOffset += chunk.Length;
        }

        return outputOffset == expectedLength ? output : null;
    }

    private static byte[]? TryDecompressChunkedZlibResourceDataRange(
        byte[] cmpfData,
        long uncompressedSize,
        long offset,
        int count)
    {
        if (cmpfData.Length < 24 || count <= 0)
        {
            return null;
        }

        if (!TryReadCmpfChunkTable(cmpfData, uncompressedSize, out var chunkDescriptors))
        {
            return null;
        }

        var output = new byte[count];
        var requestedStart = offset;
        var requestedEnd = offset + count;
        foreach (var descriptor in chunkDescriptors)
        {
            var chunkStart = descriptor.Index * (long)ApfsDecmpfs.ChunkSize;
            var chunkEnd = chunkStart + descriptor.UncompressedLength;
            if (chunkEnd <= requestedStart || chunkStart >= requestedEnd)
            {
                continue;
            }

            var chunk = TryInflateDecmpfsZlibPayload(
                cmpfData.AsSpan(descriptor.DataOffset, descriptor.CompressedLength),
                descriptor.UncompressedLength);
            if (chunk is null)
            {
                return null;
            }

            var copyStart = Math.Max(chunkStart, requestedStart);
            var copyEnd = Math.Min(chunkEnd, requestedEnd);
            var sourceOffset = (int)(copyStart - chunkStart);
            var targetOffset = (int)(copyStart - requestedStart);
            var copyLength = (int)(copyEnd - copyStart);
            chunk.AsSpan(sourceOffset, copyLength).CopyTo(output.AsSpan(targetOffset, copyLength));
        }

        return output;
    }

    private static byte[]? TryDecompressChunkedZlibResourceDataRange(
        long cmpfLength,
        Func<long, int, byte[]?> readCmpfRange,
        long uncompressedSize,
        long offset,
        int count)
    {
        if (cmpfLength < 24 || count <= 0)
        {
            return null;
        }

        if (!TryReadCmpfChunkTable(cmpfLength, readCmpfRange, uncompressedSize, out var chunkDescriptors))
        {
            return null;
        }

        var output = new byte[count];
        var requestedStart = offset;
        var requestedEnd = offset + count;
        foreach (var descriptor in chunkDescriptors)
        {
            var chunkStart = descriptor.Index * (long)ApfsDecmpfs.ChunkSize;
            var chunkEnd = chunkStart + descriptor.UncompressedLength;
            if (chunkEnd <= requestedStart || chunkStart >= requestedEnd)
            {
                continue;
            }

            var compressedChunk = readCmpfRange(descriptor.DataOffset, descriptor.CompressedLength);
            if (compressedChunk is null || compressedChunk.Length != descriptor.CompressedLength)
            {
                return null;
            }

            var chunk = TryInflateDecmpfsZlibPayload(compressedChunk, descriptor.UncompressedLength);
            if (chunk is null)
            {
                return null;
            }

            var copyStart = Math.Max(chunkStart, requestedStart);
            var copyEnd = Math.Min(chunkEnd, requestedEnd);
            var sourceOffset = (int)(copyStart - chunkStart);
            var targetOffset = (int)(copyStart - requestedStart);
            var copyLength = (int)(copyEnd - copyStart);
            chunk.AsSpan(sourceOffset, copyLength).CopyTo(output.AsSpan(targetOffset, copyLength));
        }

        return output;
    }

    private static bool TryReadCmpfChunkTable(
        byte[] cmpfData,
        long uncompressedSize,
        out IReadOnlyList<ApfsCmpfChunkDescriptor> chunkDescriptors)
    {
        chunkDescriptors = Array.Empty<ApfsCmpfChunkDescriptor>();
        if (cmpfData.Length < 24 || uncompressedSize <= 0 || uncompressedSize > ApfsDecmpfs.MaxLogicalSize)
        {
            return false;
        }

        var descriptorsOffset = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(0, 4));
        var footerOffset = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(4, 4));
        var descriptorsAndDataSize = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(8, 4));
        if (descriptorsOffset < 16 ||
            descriptorsOffset + 8 > cmpfData.Length ||
            footerOffset < descriptorsOffset ||
            footerOffset > cmpfData.Length ||
            descriptorsAndDataSize <= 0)
        {
            return false;
        }

        var compressedDataSize = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(descriptorsOffset, 4));
        var chunkCount = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(descriptorsOffset + 4, 4));
        var maxChunks = (int)Math.Min(int.MaxValue, (uncompressedSize + ApfsDecmpfs.ChunkSize - 1) / ApfsDecmpfs.ChunkSize);
        if (chunkCount <= 0 || chunkCount > Math.Max(1, maxChunks) || compressedDataSize < 0)
        {
            return false;
        }

        var tupleStart = descriptorsOffset + 8;
        if (tupleStart + chunkCount * 8 > cmpfData.Length)
        {
            return false;
        }

        var descriptors = new List<ApfsCmpfChunkDescriptor>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            var tupleOffset = tupleStart + i * 8;
            var relativeDataOffset = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(tupleOffset, 4));
            var chunkCompressedSize = ReadUInt32BigEndianAsInt(cmpfData.AsSpan(tupleOffset + 4, 4));
            var chunkDataOffset = 20 + relativeDataOffset;
            if (chunkCompressedSize <= 0 || !IsRangeInside(cmpfData.Length, chunkDataOffset, chunkCompressedSize))
            {
                return false;
            }

            var remaining = uncompressedSize - i * (long)ApfsDecmpfs.ChunkSize;
            if (remaining <= 0)
            {
                return false;
            }

            descriptors.Add(new ApfsCmpfChunkDescriptor(
                i,
                chunkDataOffset,
                chunkCompressedSize,
                (int)Math.Min(ApfsDecmpfs.ChunkSize, remaining)));
        }

        chunkDescriptors = descriptors;
        return true;
    }

    private static bool TryReadCmpfChunkTable(
        long cmpfLength,
        Func<long, int, byte[]?> readCmpfRange,
        long uncompressedSize,
        out IReadOnlyList<ApfsCmpfChunkDescriptor> chunkDescriptors)
    {
        chunkDescriptors = Array.Empty<ApfsCmpfChunkDescriptor>();
        if (cmpfLength < 24 ||
            cmpfLength > ApfsDecmpfs.MaxCompressedResourceForkSize ||
            uncompressedSize <= 0 ||
            uncompressedSize > ApfsDecmpfs.MaxLogicalSize)
        {
            return false;
        }

        var header = readCmpfRange(0, 24);
        if (header is null || header.Length != 24)
        {
            return false;
        }

        var descriptorsOffset = ReadUInt32BigEndianAsInt(header.AsSpan(0, 4));
        var footerOffset = ReadUInt32BigEndianAsInt(header.AsSpan(4, 4));
        var descriptorsAndDataSize = ReadUInt32BigEndianAsInt(header.AsSpan(8, 4));
        if (descriptorsOffset < 16 ||
            footerOffset < descriptorsOffset ||
            footerOffset > cmpfLength ||
            descriptorsAndDataSize <= 0 ||
            !IsRangeInside(cmpfLength, descriptorsOffset, 8))
        {
            return false;
        }

        var tableHeader = readCmpfRange(descriptorsOffset, 8);
        if (tableHeader is null || tableHeader.Length != 8)
        {
            return false;
        }

        var compressedDataSize = ReadUInt32BigEndianAsInt(tableHeader.AsSpan(0, 4));
        var chunkCount = ReadUInt32BigEndianAsInt(tableHeader.AsSpan(4, 4));
        var maxChunks = (int)Math.Min(int.MaxValue, (uncompressedSize + ApfsDecmpfs.ChunkSize - 1) / ApfsDecmpfs.ChunkSize);
        if (chunkCount <= 0 || chunkCount > Math.Max(1, maxChunks) || compressedDataSize < 0)
        {
            return false;
        }

        var tupleBytes = checked(chunkCount * 8);
        var tableLength = 8 + tupleBytes;
        if (tableLength < 8 ||
            tableLength > int.MaxValue ||
            !IsRangeInside(cmpfLength, descriptorsOffset, tableLength))
        {
            return false;
        }

        var table = readCmpfRange(descriptorsOffset, tableLength);
        if (table is null || table.Length != tableLength)
        {
            return false;
        }

        var descriptors = new List<ApfsCmpfChunkDescriptor>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            var tupleOffset = 8 + i * 8;
            var relativeDataOffset = ReadUInt32BigEndianAsInt(table.AsSpan(tupleOffset, 4));
            var chunkCompressedSize = ReadUInt32BigEndianAsInt(table.AsSpan(tupleOffset + 4, 4));
            var chunkDataOffset = 20L + relativeDataOffset;
            if (relativeDataOffset < 0 ||
                chunkCompressedSize <= 0 ||
                chunkDataOffset > int.MaxValue ||
                !IsRangeInside(cmpfLength, chunkDataOffset, chunkCompressedSize))
            {
                return false;
            }

            var remaining = uncompressedSize - i * (long)ApfsDecmpfs.ChunkSize;
            if (remaining <= 0)
            {
                return false;
            }

            descriptors.Add(new ApfsCmpfChunkDescriptor(
                i,
                (int)chunkDataOffset,
                chunkCompressedSize,
                (int)Math.Min(ApfsDecmpfs.ChunkSize, remaining)));
        }

        chunkDescriptors = descriptors;
        return true;
    }

    private static byte[]? TryInflateDecmpfsZlibPayload(ReadOnlySpan<byte> compressedPayload, int expectedLength)
    {
        if (expectedLength < 0 || compressedPayload.IsEmpty)
        {
            return null;
        }

        if (compressedPayload[0] == 0xFF)
        {
            var raw = compressedPayload[1..];
            if (raw.Length != expectedLength)
            {
                return null;
            }
            return raw.ToArray();
        }

        byte[]? Inflate(ReadOnlySpan<byte> payload)
        {
            try
            {
                using var inputStream = new MemoryStream(payload.ToArray());
                using var deflate = new System.IO.Compression.DeflateStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream(expectedLength);
                deflate.CopyTo(output);
                var inflated = output.ToArray();
                return inflated.Length == expectedLength ? inflated : null;
            }
            catch
            {
                return null;
            }
        }

        if (compressedPayload.Length > 2 && compressedPayload[0] == 0x78)
        {
            var zlibInflated = Inflate(compressedPayload[2..]);
            if (zlibInflated is not null)
            {
                return zlibInflated;
            }
        }

        return Inflate(compressedPayload);
    }

    private static int ReadUInt32BigEndianAsInt(ReadOnlySpan<byte> value)
    {
        var raw = BinaryPrimitives.ReadUInt32BigEndian(value);
        return raw <= int.MaxValue ? (int)raw : -1;
    }

    private static int ReadUInt24BigEndian(ReadOnlySpan<byte> value)
    {
        return (value[0] << 16) | (value[1] << 8) | value[2];
    }

    private static bool IsRangeInside(int containerLength, int offset, int length)
    {
        return offset >= 0 && length >= 0 && offset <= containerLength && length <= containerLength - offset;
    }

    private static bool IsRangeInside(long containerLength, long offset, long length)
    {
        return offset >= 0 && length >= 0 && offset <= containerLength && length <= containerLength - offset;
    }

    private static long? TryReadInlineDecmpfsUncompressedSize(byte[] inlineData)
    {
        return ApfsDecmpfs.TryReadInlineUncompressedSize(inlineData);
    }

    private static long GetInlineDataLogicalSize(byte[] inlineData, bool isCompressed)
    {
        return ApfsDecmpfs.GetInlineDataLogicalSize(inlineData, isCompressed);
    }

    private static string DecmpfsTypeName(uint type) => type switch
    {
        1  => "uncompressed inline",
        3  => "zlib inline",
        4  => "zlib in resource fork",
        7  => "LZVN inline",
        8  => "LZVN in resource fork",
        11 => "LZFSE inline",
        12 => "LZFSE in resource fork",
        _  => $"unknown ({type})"
    };

    private readonly record struct ApfsCmpfChunkDescriptor(
        int Index,
        int DataOffset,
        int CompressedLength,
        int UncompressedLength
    );

    // Write operations
    public int WriteFile(string path, long offset, ReadOnlySpan<byte> source)
    {
        if (!_writable || _writer is null) throw new InvalidOperationException("Provider is read-only.");
        if (source.Length == 0) return 0;

        var n = Normalize(path);
        uint cnid;
        lock (_sync)
        {
            if (!_cnidByPath.TryGetValue(n, out cnid))
            {
                // Try to resolve via parent listing
                var parent = GetParentPath(n);
                ListDirectory(parent);
                if (!_cnidByPath.TryGetValue(n, out cnid)) return 0;
            }
        }

        try
        {
            var buf = source.ToArray();
            _writer.WriteFileDataAsync(cnid, offset, buf, buf.Length).GetAwaiter().GetResult();
            return buf.Length;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[APFS] WriteFile({path}) error: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    public void CreateFile(string path)
    {
        if (!_writable || _writer is null) throw new InvalidOperationException("Provider is read-only.");

        var n = Normalize(path);
        var parentPath = GetParentPath(n);
        var fileName = n[(n.LastIndexOf('\\') + 1)..];

        uint parentCnid;
        lock (_sync)
        {
            if (!_cnidByPath.TryGetValue(parentPath, out parentCnid))
            {
                throw new DirectoryNotFoundException($"Parent directory not found: {parentPath}");
            }
        }

        try
        {
            var cnid = _writer.CreateFileAsync(parentCnid, fileName).GetAwaiter().GetResult();
            lock (_sync)
            {
                _cnidByPath[n] = cnid;
                var entry = new RawFsEntry(n, fileName, false, 0, DateTimeOffset.UtcNow, FileAttributes.Normal);
                _entries[n] = entry;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[APFS] CreateFile({path}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void CreateDirectory(string path)
    {
        if (!_writable || _writer is null) throw new InvalidOperationException("Provider is read-only.");

        var n = Normalize(path);
        var parentPath = GetParentPath(n);
        var dirName = n[(n.LastIndexOf('\\') + 1)..];

        uint parentCnid;
        lock (_sync)
        {
            if (!_cnidByPath.TryGetValue(parentPath, out parentCnid))
            {
                throw new DirectoryNotFoundException($"Parent directory not found: {parentPath}");
            }
        }

        try
        {
            var cnid = _writer.CreateDirectoryAsync(parentCnid, dirName).GetAwaiter().GetResult();
            lock (_sync)
            {
                _cnidByPath[n] = cnid;
                var entry = new RawFsEntry(n, dirName, true, 0, DateTimeOffset.UtcNow, FileAttributes.Directory);
                _entries[n] = entry;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[APFS] CreateDirectory({path}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void Delete(string path)
    {
        if (!_writable || _writer is null) throw new InvalidOperationException("Provider is read-only.");

        var n = Normalize(path);
        var parentPath = GetParentPath(n);
        var entryName = n[(n.LastIndexOf('\\') + 1)..];

        uint parentCnid;
        lock (_sync)
        {
            if (!_cnidByPath.TryGetValue(parentPath, out parentCnid))
            {
                throw new DirectoryNotFoundException($"Parent directory not found: {parentPath}");
            }
        }

        try
        {
            _writer.DeleteEntryAsync(parentCnid, entryName).GetAwaiter().GetResult();
            lock (_sync)
            {
                _cnidByPath.Remove(n);
                _entries.Remove(n);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[APFS] Delete({path}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void Rename(string oldPath, string newPath)
    {
        if (!_writable || _writer is null) throw new InvalidOperationException("Provider is read-only.");

        var oldN = Normalize(oldPath);
        var newN = Normalize(newPath);
        var oldParent = GetParentPath(oldN);
        var newParent = GetParentPath(newN);
        var oldName = oldN[(oldN.LastIndexOf('\\') + 1)..];
        var newName = newN[(newN.LastIndexOf('\\') + 1)..];

        uint oldParentCnid, newParentCnid;
        lock (_sync)
        {
            if (!_cnidByPath.TryGetValue(oldParent, out oldParentCnid))
                throw new DirectoryNotFoundException($"Old parent not found: {oldParent}");
            if (!_cnidByPath.TryGetValue(newParent, out newParentCnid))
                throw new DirectoryNotFoundException($"New parent not found: {newParent}");
        }

        try
        {
            // APFS doesn't have native rename - delete old + create new
            var existing = GetEntry(oldPath);
            if (existing is null) throw new FileNotFoundException($"Entry not found: {oldPath}");

            if (existing.IsDirectory)
            {
                _writer.DeleteEntryAsync(oldParentCnid, oldName).GetAwaiter().GetResult();
                _writer.CreateDirectoryAsync(newParentCnid, newName).GetAwaiter().GetResult();
            }
            else
            {
                // Read existing file data
                byte[]? fileData = null;
                if (_fileReadPlans.TryGetValue(oldN, out var plan) && plan.TotalSize > 0)
                {
                    fileData = new byte[plan.TotalSize];
                    ReadFile(oldPath, 0, fileData);
                }

                _writer.DeleteEntryAsync(oldParentCnid, oldName).GetAwaiter().GetResult();
                _writer.CreateFileAsync(newParentCnid, newName, fileData).GetAwaiter().GetResult();
            }

            lock (_sync)
            {
                _cnidByPath.Remove(oldN);
                if (_cnidByPath.TryGetValue(oldParent, out var cnid))
                {
                    _cnidByPath[newN] = cnid;
                }
                _entries.Remove(oldN);
                var newEntry = new RawFsEntry(newN, newName, existing.IsDirectory, existing.Size, DateTimeOffset.UtcNow, existing.Attributes);
                _entries[newN] = newEntry;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[APFS] Rename({oldPath} -> {newPath}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void SetFileSize(string path, long newSize)
    {
        if (!_writable || _writer is null) throw new InvalidOperationException("Provider is read-only.");

        var n = Normalize(path);
        uint cnid;
        lock (_sync)
        {
            if (!_cnidByPath.TryGetValue(n, out cnid))
            {
                var parent = GetParentPath(n);
                ListDirectory(parent);
                if (!_cnidByPath.TryGetValue(n, out cnid))
                    throw new FileNotFoundException($"File not found: {path}");
            }
        }

        try
        {
            _writer.SetFileSizeAsync(cnid, newSize).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[APFS] SetFileSize({path}, {newSize}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void Flush()
    {
        if (!_writable || _writer is null) return;
        try
        {
            _writer.FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[APFS] Flush error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetParentPath(string path)
    {
        if (path == "\\") return "\\";
        var lastSlash = path.LastIndexOf('\\');
        if (lastSlash <= 0) return "\\";
        return path[..lastSlash];
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch { }
        try { _allocator?.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }

    private bool PopulateVolumeCatalog(string volumePath, ApfsVolumePreview preview, DateTimeOffset now)
    {
        if (preview.DirectoryEntriesByParentId.Count == 0)
        {
            return false;
        }

        var queue = new Queue<(ulong DirectoryId, string Path)>();
        var visitedDirectoryIds = new HashSet<ulong>();
        queue.Enqueue((preview.RootDirectoryId, volumePath));

        var populated = false;
        while (queue.Count > 0)
        {
            var (directoryId, currentPath) = queue.Dequeue();
            if (!visitedDirectoryIds.Add(directoryId))
            {
                continue;
            }

            if (!preview.DirectoryEntriesByParentId.TryGetValue(directoryId, out var catalogChildren) || catalogChildren.Count == 0)
            {
                _dirChildren[currentPath] = Array.Empty<RawFsEntry>();
                continue;
            }

            var currentChildren = new List<RawFsEntry>(catalogChildren.Count);
            foreach (var item in catalogChildren)
            {
                if (IsMacMetadata(item.Name)) continue;

                var childPath = JoinPath(currentPath, item.Name);
                long size = 0;
                string? symlinkTarget = null;
                if (!item.IsDirectory &&
                    item.ChildId.HasValue &&
                    preview.FilePlansByObjectId.TryGetValue(item.ChildId.Value, out var planForFile))
                {
                    if (item.IsSymbolicLink)
                    {
                        symlinkTarget = TryReadApfsSymlinkTarget(planForFile);
                    }
                    else
                    {
                        _fileReadPlans[childPath] = planForFile;
                        size = Math.Max(0, planForFile.TotalSize);
                    }
                }

                var attrs = item.IsDirectory
                    ? FileAttributes.Directory
                    : symlinkTarget is not null
                        ? FileAttributes.ReadOnly | FileAttributes.ReparsePoint
                        : FileAttributes.ReadOnly;
                var child = new RawFsEntry(childPath, item.Name, item.IsDirectory, size, now, attrs, symlinkTarget);
                _entries[childPath] = child;
                currentChildren.Add(child);

                if (!item.IsDirectory &&
                    item.ChildId.HasValue &&
                    preview.ResourceForkAppleDoubleByObjectId.TryGetValue(item.ChildId.Value, out var appleDoublePlan))
                {
                    var sidecarName = "._" + item.Name;
                    var sidecarPath = JoinPath(currentPath, sidecarName);
                    if (!_entries.ContainsKey(sidecarPath))
                    {
                        var sidecar = new RawFsEntry(
                            sidecarPath,
                            sidecarName,
                            false,
                            Math.Max(0, appleDoublePlan.TotalSize),
                            now,
                            FileAttributes.ReadOnly | FileAttributes.Hidden);
                        _entries[sidecarPath] = sidecar;
                        _fileReadPlans[sidecarPath] = appleDoublePlan;
                        currentChildren.Add(sidecar);
                    }
                }

                if (item.IsDirectory && item.ChildId.HasValue)
                {
                    queue.Enqueue((item.ChildId.Value, childPath));
                }
            }

            _dirChildren[currentPath] = currentChildren;
            populated = populated || currentChildren.Count > 0;
        }

        return populated;
    }

    private void AddPreviewEntries(string volumePath, ApfsVolumePreview preview, DateTimeOffset now)
    {
        var previewChildren = new List<RawFsEntry>();
        foreach (var item in preview.RootEntries.Take(MaxPreviewEntriesPerVolume))
        {
            if (IsMacMetadata(item.Name)) continue;

            var childPath = JoinPath(volumePath, item.Name);
            var attrs = item.IsDirectory ? FileAttributes.Directory : FileAttributes.ReadOnly;
            long size = 0;
            string? symlinkTarget = null;
            if (!item.IsDirectory &&
                preview.RootFilePlansByName.TryGetValue(item.Name, out var planForFile))
            {
                if (item.IsSymbolicLink)
                {
                    symlinkTarget = TryReadApfsSymlinkTarget(planForFile);
                    if (symlinkTarget is not null)
                    {
                        attrs = FileAttributes.ReadOnly | FileAttributes.ReparsePoint;
                    }
                }
                else
                {
                    _fileReadPlans[childPath] = planForFile;
                    size = Math.Max(0, planForFile.TotalSize);
                }
            }
            var child = new RawFsEntry(childPath, item.Name, item.IsDirectory, size, now, attrs, symlinkTarget);
            _entries[childPath] = child;
            previewChildren.Add(child);
        }
        _dirChildren[volumePath] = previewChildren;
    }

    private string? TryReadApfsSymlinkTarget(ApfsFileReadPlan plan)
    {
        if (plan.TotalSize <= 0 || plan.TotalSize > 4096 || plan.IsCompressed)
        {
            return null;
        }

        var buffer = new byte[(int)plan.TotalSize];
        var read = plan.InlineData is not null && plan.Extents.Count == 0
            ? CopyInlinePlanData(plan, buffer)
            : ReadExtentBackedFile(_device, _partitionOffsetBytes, _blockSize, plan, 0, buffer);
        if (read <= 0)
        {
            return null;
        }

        var target = Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(target) ? null : target;
    }

    private static int CopyInlinePlanData(ApfsFileReadPlan plan, Span<byte> destination)
    {
        if (plan.InlineData is null || destination.Length == 0)
        {
            return 0;
        }

        var count = Math.Min(destination.Length, plan.InlineData.Length);
        plan.InlineData.AsSpan(0, count).CopyTo(destination);
        return count;
    }

    private static ApfsVolumePreview? SelectPrimaryVolumePreview(ApfsContainerSummary summary)
    {
        var previews = summary.VolumeObjectIds
            .Select(oid => summary.VolumePreviewsByOid.TryGetValue(oid, out var preview) ? preview : null)
            .Where(preview => preview is not null && HasUserVisibleEntries(preview))
            .Cast<ApfsVolumePreview>()
            .ToList();

        if (previews.Count == 0)
        {
            return null;
        }

        if (previews.Count == 1)
        {
            return previews[0];
        }

        return previews
            .OrderByDescending(ScorePrimaryVolume)
            .FirstOrDefault();
    }

    private static bool HasUserVisibleEntries(ApfsVolumePreview preview)
    {
        return preview.RootEntries.Any(entry => !IsMacMetadata(entry.Name)) ||
               preview.DirectoryEntriesByParentId.Values.Any(entries => entries.Any(entry => !IsMacMetadata(entry.Name)));
    }

    private static int ScorePrimaryVolume(ApfsVolumePreview preview)
    {
        var score = 0;
        if (preview.RoleName.Contains("Data", StringComparison.OrdinalIgnoreCase)) score += 100;
        if (preview.RoleName.Contains("User", StringComparison.OrdinalIgnoreCase)) score += 80;
        if (preview.RoleName.Equals("None", StringComparison.OrdinalIgnoreCase)) score += 50;
        if (preview.RoleName.Contains("System", StringComparison.OrdinalIgnoreCase)) score -= 20;
        if (preview.RoleName.Contains("Recovery", StringComparison.OrdinalIgnoreCase)) score -= 100;
        if (preview.RoleName.Contains("Preboot", StringComparison.OrdinalIgnoreCase)) score -= 100;
        if (preview.RoleName.Contains("VM", StringComparison.OrdinalIgnoreCase)) score -= 100;
        score += Math.Min(30, preview.RootEntries.Count(entry => !IsMacMetadata(entry.Name)));
        return score;
    }

    private static string JoinPath(string parent, string name)
    {
        var safeName = name.Replace('\\', '_').Replace('/', '_');
        if (string.IsNullOrWhiteSpace(parent) || parent == "\\")
        {
            return "\\" + safeName;
        }
        return parent.TrimEnd('\\') + "\\" + safeName;
    }

    private static string BuildVolumeFolderName(ApfsVolumePreview? preview, ulong oid, ISet<string> usedVolumeNames)
    {
        var preferredName = preview is not null
            ? SanitizeVolumeName(preview.DisplayName)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(preferredName))
        {
            preferredName = $"Volume_{oid:X}";
        }

        var candidate = preferredName;
        if (usedVolumeNames.Add(candidate))
        {
            return candidate;
        }

        candidate = $"{preferredName}_OID_{oid:X}";
        if (usedVolumeNames.Add(candidate))
        {
            return candidate;
        }

        var suffix = 2;
        while (!usedVolumeNames.Add($"{candidate}_{suffix}"))
        {
            suffix++;
        }

        return $"{candidate}_{suffix}";
    }

    private static string SanitizeVolumeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        }

        return builder
            .ToString()
            .Trim()
            .TrimEnd('.', ' ');
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "\\";
        var p = path.Replace('/', '\\');
        if (!p.StartsWith("\\")) p = "\\" + p.TrimStart('\\');
        return p;
    }

    private static string BuildInfoText(ApfsContainerSummary summary, MountPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CrossDrive APFS Native Metadata");
        sb.AppendLine($"PhysicalDrivePath: {plan.PhysicalDrivePath}");
        sb.AppendLine($"PartitionOffsetBytes: {plan.PartitionOffsetBytes}");
        sb.AppendLine($"PartitionLengthBytes: {plan.PartitionLengthBytes}");
        sb.AppendLine($"ContainerBlockSize: {summary.BlockSize}");
        sb.AppendLine($"ContainerBlockCount: {summary.BlockCount}");
        sb.AppendLine($"CheckpointXid: {summary.TransactionId}");
        sb.AppendLine($"CheckpointDescBase: {summary.CheckpointDescriptorBase}");
        sb.AppendLine($"CheckpointDescBlocks: {summary.CheckpointDescriptorBlocks}");
        sb.AppendLine($"CheckpointDataBase: {summary.CheckpointDataBase}");
        sb.AppendLine($"CheckpointDataBlocks: {summary.CheckpointDataBlocks}");
        sb.AppendLine($"SpacemanOid: {summary.SpacemanOid}");
        sb.AppendLine($"OmapOid: {summary.OmapOid}");
        sb.AppendLine($"SpacemanPhysicalBlock: {summary.SpacemanPhysicalBlock?.ToString() ?? "not found"}");
        sb.AppendLine($"ObjectMapBlockNumber: {summary.ObjectMapBlockNumber}");
        sb.AppendLine($"ObjectMapTreeOid: {summary.ObjectMapTreeOid}");
        sb.AppendLine($"ObjectMapTreeBlockNumber: {summary.ObjectMapTreeBlockNumber}");
        sb.AppendLine($"IndexedObjectCount: {summary.IndexedObjectCount}");
        sb.AppendLine($"ResolvedVolumePointersCount: {summary.ResolvedVolumePointers.Count}");
        foreach (var resolved in summary.ResolvedVolumePointers.Take(MaxVolumeHintsForView))
        {
            sb.AppendLine(
                $"ResolvedVolumePointer: oid=0x{resolved.ObjectId:X} xid={resolved.TransactionId} paddr={resolved.PhysicalBlockNumber} size={resolved.LogicalSize} flags=0x{resolved.Flags:X}");
        }
        sb.AppendLine($"VolumeObjectIds: {string.Join(",", summary.VolumeObjectIds.Select(x => x.ToString("X")))}");
        sb.AppendLine($"VolumePreviewsCount: {summary.VolumePreviewsByOid.Count}");
        foreach (var kv in summary.VolumePreviewsByOid.Take(MaxVolumeHintsForView))
        {
            var v = kv.Value;
            sb.AppendLine(
                $"VolumePreview: oid=0x{kv.Key:X} name=\"{v.DisplayName}\" role={v.RoleName} encrypted={(v.IsEncrypted ? "yes" : "no")} fsFlags=0x{v.FsFlags:X} rootTreeOid={(v.RootTreeOid?.ToString() ?? "n/a")} rootTreeBlock={(v.RootTreeBlock?.ToString() ?? "n/a")} entries={v.RootEntries.Count}");
        }
        sb.AppendLine($"VolumeSuperblockHintsCount: {summary.VolumeSuperblockHints.Count}");
        foreach (var hint in summary.VolumeSuperblockHints.Take(MaxVolumeHintsForView))
        {
            sb.AppendLine(
                $"VolumeHint: block={hint.BlockNumber} oid=0x{hint.ObjectId:X} xid={hint.TransactionId} name=\"{hint.VolumeName}\" role={DescribeVolumeRole(hint.Role)} encrypted={(((hint.FsFlags & ApfsMetadataReader.ApfsFsOneKeyFlag) != 0) ? "yes" : "no")} fsFlags=0x{hint.FsFlags:X}");
        }
        sb.AppendLine("Status: APFS object map/tree traversal is in progress.");
        return sb.ToString();
    }

    internal static string DescribeVolumeRole(ushort role)
    {
        if (role == 0)
        {
            return "None";
        }

        var names = new List<string>(8);
        var legacyFlags = role & 0x003F;
        if ((legacyFlags & 0x0001) != 0) names.Add("System");
        if ((legacyFlags & 0x0002) != 0) names.Add("User");
        if ((legacyFlags & 0x0004) != 0) names.Add("Recovery");
        if ((legacyFlags & 0x0008) != 0) names.Add("VM");
        if ((legacyFlags & 0x0010) != 0) names.Add("Preboot");
        if ((legacyFlags & 0x0020) != 0) names.Add("Installer");

        var enumeratedRole = role >> 6;
        switch (enumeratedRole)
        {
            case 1:
                names.Add("Data");
                break;
            case 2:
                names.Add("Baseband");
                break;
            case 3:
                names.Add("Update");
                break;
            case 4:
                names.Add("XART");
                break;
            case 5:
                names.Add("Hardware");
                break;
            case 6:
                names.Add("Backup");
                break;
            case 7:
                names.Add("Sidecar");
                break;
            case 8:
                names.Add("Reserved8");
                break;
            case 9:
                names.Add("Enterprise");
                break;
            case 10:
                names.Add("Reserved10");
                break;
            case 11:
                names.Add("Prelogin");
                break;
        }

        return names.Count == 0 ? $"0x{role:X4}" : string.Join("+", names);
    }
}

internal static class ApfsDecmpfs
{
    public const int HeaderLength = 16;
    public const int ChunkSize = 64 * 1024;
    public const long MaxLogicalSize = 16L * 1024 * 1024 * 1024;
    public const long MaxFullDecompressionSize = 128L * 1024 * 1024;
    public const long MaxCompressedResourceForkSize = 512L * 1024 * 1024;

    public static long? TryReadInlineUncompressedSize(byte[] inlineData)
    {
        if (inlineData.Length < HeaderLength) return null;

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(inlineData.AsSpan(0, 4));
        if (magic != 0x636D7066) return null; // "fpmc" LE

        var uncompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(inlineData.AsSpan(8, 8));
        if (uncompressedSize <= 0 || uncompressedSize > MaxLogicalSize) return null;

        return uncompressedSize;
    }

    public static long GetInlineDataLogicalSize(byte[] inlineData, bool isCompressed)
    {
        if (!isCompressed) return inlineData.Length;
        return TryReadInlineUncompressedSize(inlineData) ?? inlineData.Length;
    }
}

internal static class ApfsAppleDouble
{
    private const int AppleDoubleHeaderBaseLength = 26;
    private const int AppleDoubleEntryLength = 12;
    private const int AppleDoubleFinderInfoLength = 32;
    private const int AppleDoubleAttrPadLength = 2;
    private const int AppleDoubleAttrHeaderLength = 36;
    private const uint AppleDoubleResourceForkEntryId = 2;
    private const uint AppleDoubleFinderInfoEntryId = 9;
    private const uint AppleDoubleAttrMagic = 0x41545452;
    private const int MaxInlineResourceForkBytes = 16 * 1024 * 1024;

    public static byte[]? TryExtractInlineXattrData(byte[] value)
    {
        return TryExtractInlineXattrData(value.AsSpan());
    }

    public static byte[]? TryExtractInlineXattrData(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0 || value.Length > MaxInlineResourceForkBytes + 16)
        {
            return null;
        }

        if (value.Length >= 4)
        {
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(value[0..2]);
            var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(value[2..4]);
            if (declaredLength > 0 &&
                4 + declaredLength <= value.Length)
            {
                const ushort XATTR_DATA_EMBEDDED = 0x0001;
                return (flags & XATTR_DATA_EMBEDDED) != 0
                    ? value.Slice(4, declaredLength).ToArray()
                    : null;
            }
        }

        if (value.Length <= MaxInlineResourceForkBytes)
        {
            return value.ToArray();
        }

        return null;
    }

    public static byte[] BuildResourceForkSidecar(byte[] resourceFork)
    {
        return BuildInlineAppleDoubleSidecar(resourceFork, null, null);
    }

    internal static ApfsFileReadPlan BuildResourceForkSidecarReadPlan(ApfsFileReadPlan resourceForkPlan)
        => BuildAppleDoubleSidecarReadPlan(resourceForkPlan, null, null);

    internal static ApfsFileReadPlan BuildAppleDoubleSidecarReadPlan(ApfsFileReadPlan? resourceForkPlan, byte[]? finderInfo)
        => BuildAppleDoubleSidecarReadPlan(resourceForkPlan, finderInfo, null);

    internal static ApfsFileReadPlan BuildAppleDoubleSidecarReadPlan(
        ApfsFileReadPlan? resourceForkPlan,
        byte[]? finderInfo,
        IReadOnlyList<ApfsExtendedAttribute>? extendedAttributes)
    {
        var hasResourceFork = resourceForkPlan is { TotalSize: > 0 };
        var cleanFinderInfo = NormalizeFinderInfo(finderInfo);
        var cleanExtendedAttributes = NormalizeExtendedAttributes(extendedAttributes);
        var hasExtendedAttributes = cleanExtendedAttributes.Count > 0;
        var hasFinderInfo = cleanFinderInfo is not null;
        if (!hasResourceFork && !hasFinderInfo && !hasExtendedAttributes)
        {
            return new ApfsFileReadPlan(0, Array.Empty<ApfsFileExtent>(), Array.Empty<byte>());
        }
        if (resourceForkPlan is { TotalSize: > uint.MaxValue })
        {
            return new ApfsFileReadPlan(0, Array.Empty<ApfsFileExtent>(), Array.Empty<byte>());
        }

        if (resourceForkPlan?.InlineData is { Length: > 0 } inlineResourceFork && resourceForkPlan.Extents.Count == 0)
        {
            var inlineSidecar = BuildInlineAppleDoubleSidecar(inlineResourceFork, cleanFinderInfo, cleanExtendedAttributes);
            return new ApfsFileReadPlan(
                inlineSidecar.Length,
                Array.Empty<ApfsFileExtent>(),
                inlineSidecar);
        }

        var prefix = BuildAppleDoubleInlinePrefix(resourceForkPlan?.TotalSize ?? 0, hasResourceFork, cleanFinderInfo, cleanExtendedAttributes);
        var shiftedExtents = resourceForkPlan?.Extents
            .Select(extent => new ApfsFileExtent(
                LogicalOffset: prefix.Length + extent.LogicalOffset,
                Length: extent.Length,
                PhysicalBlockNumber: extent.PhysicalBlockNumber))
            .ToArray() ?? Array.Empty<ApfsFileExtent>();
        return new ApfsFileReadPlan(
            prefix.Length + (resourceForkPlan?.TotalSize ?? 0),
            shiftedExtents,
            prefix,
            IsCompressed: false);
    }

    internal static bool IsPreservableExtendedAttributeName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               !string.Equals(name, "com.apple.ResourceFork", StringComparison.Ordinal) &&
               !string.Equals(name, "com.apple.FinderInfo", StringComparison.Ordinal) &&
               !string.Equals(name, "com.apple.decmpfs", StringComparison.Ordinal);
    }

    private static byte[] BuildInlineAppleDoubleSidecar(
        byte[]? resourceFork,
        byte[]? finderInfo,
        IReadOnlyList<ApfsExtendedAttribute>? extendedAttributes)
    {
        var cleanFinderInfo = NormalizeFinderInfo(finderInfo);
        var cleanExtendedAttributes = NormalizeExtendedAttributes(extendedAttributes);
        var output = BuildAppleDoubleInlinePrefix(
            resourceFork?.LongLength ?? 0,
            resourceFork is { Length: > 0 },
            cleanFinderInfo,
            cleanExtendedAttributes);
        if (resourceFork is { Length: > 0 })
        {
            Array.Resize(ref output, output.Length + resourceFork.Length);
            resourceFork.CopyTo(output.AsSpan(output.Length - resourceFork.Length));
        }
        return output;
    }

    private static byte[] BuildAppleDoubleInlinePrefix(
        long resourceForkLength,
        bool hasResourceFork,
        byte[]? finderInfo,
        IReadOnlyList<ApfsExtendedAttribute>? extendedAttributes)
    {
        var cleanFinderInfo = NormalizeFinderInfo(finderInfo);
        var cleanExtendedAttributes = NormalizeExtendedAttributes(extendedAttributes);
        var includeResourceForkEntry = hasResourceFork || cleanExtendedAttributes.Count > 0;
        var includeFinderInfoEntry = cleanFinderInfo is not null || cleanExtendedAttributes.Count > 0;
        var headerLength = AppleDoubleHeaderBaseLength +
            ((includeFinderInfoEntry ? 1 : 0) + (includeResourceForkEntry ? 1 : 0)) * AppleDoubleEntryLength;
        var finderInfoPayload = cleanExtendedAttributes.Count > 0
            ? BuildFinderInfoAttributePayload(cleanFinderInfo, cleanExtendedAttributes, headerLength)
            : cleanFinderInfo;
        var output = BuildAppleDoubleSidecarHeader(
            resourceForkLength,
            includeResourceForkEntry,
            finderInfoPayload?.Length ?? 0);
        if (finderInfoPayload is not null)
        {
            Array.Resize(ref output, output.Length + finderInfoPayload.Length);
            finderInfoPayload.CopyTo(output.AsSpan(output.Length - finderInfoPayload.Length));
        }
        return output;
    }

    private static byte[] BuildAppleDoubleSidecarHeader(long resourceForkLength, bool includeResourceForkEntry, int finderInfoEntryLength)
    {
        if (includeResourceForkEntry && (resourceForkLength < 0 || resourceForkLength > uint.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(resourceForkLength), "AppleDouble resource fork length must fit in 32 bits.");
        }

        var hasFinderInfoEntry = finderInfoEntryLength > 0;
        var entryCount = (hasFinderInfoEntry ? 1 : 0) + (includeResourceForkEntry ? 1 : 0);
        var headerLength = AppleDoubleHeaderBaseLength + entryCount * AppleDoubleEntryLength;
        var output = new byte[headerLength];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), 0x00051607);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4, 4), 0x00020000);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(24, 2), (ushort)entryCount);

        var entryOffset = AppleDoubleHeaderBaseLength;
        var dataOffset = headerLength;
        if (hasFinderInfoEntry)
        {
            WriteAppleDoubleEntry(output.AsSpan(entryOffset, AppleDoubleEntryLength), AppleDoubleFinderInfoEntryId, dataOffset, finderInfoEntryLength);
            entryOffset += AppleDoubleEntryLength;
            dataOffset += finderInfoEntryLength;
        }

        if (includeResourceForkEntry)
        {
            WriteAppleDoubleEntry(output.AsSpan(entryOffset, AppleDoubleEntryLength), AppleDoubleResourceForkEntryId, dataOffset, resourceForkLength);
        }

        return output;
    }

    private static void WriteAppleDoubleEntry(Span<byte> target, uint id, int offset, long length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target[..4], id);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(4, 4), (uint)offset);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(8, 4), (uint)Math.Min(length, uint.MaxValue));
    }

    private static byte[] BuildFinderInfoAttributePayload(
        byte[]? finderInfo,
        IReadOnlyList<ApfsExtendedAttribute> extendedAttributes,
        int finderInfoEntryOffset)
    {
        var cleanFinderInfo = NormalizeFinderInfo(finderInfo);
        var attrs = NormalizeExtendedAttributes(extendedAttributes);
        var entryPayloads = attrs.Select(attr =>
        {
            var nameBytes = Encoding.UTF8.GetBytes(attr.Name);
            var nameLengthWithNull = nameBytes.Length + 1;
            var entryLength = Align4(11 + nameLengthWithNull);
            return (Attribute: attr, NameBytes: nameBytes, NameLengthWithNull: nameLengthWithNull, EntryLength: entryLength);
        }).ToArray();

        var attrEntriesLength = entryPayloads.Sum(entry => entry.EntryLength);
        var attrDataLength = attrs.Sum(attr => attr.Data.Length);
        var attrHeaderOffset = AppleDoubleFinderInfoLength + AppleDoubleAttrPadLength;
        var attrEntriesOffset = attrHeaderOffset + AppleDoubleAttrHeaderLength;
        var attrDataOffset = attrEntriesOffset + attrEntriesLength;
        var output = new byte[attrDataOffset + attrDataLength];
        var absoluteDataStart = finderInfoEntryOffset + attrDataOffset;
        var absoluteTotalSize = finderInfoEntryOffset + output.Length;

        cleanFinderInfo?.CopyTo(output.AsSpan(0, AppleDoubleFinderInfoLength));

        var attrHeader = output.AsSpan(attrHeaderOffset, AppleDoubleAttrHeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(attrHeader.Slice(0, 4), AppleDoubleAttrMagic);
        BinaryPrimitives.WriteUInt32BigEndian(attrHeader.Slice(4, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(attrHeader.Slice(8, 4), (uint)absoluteTotalSize);
        BinaryPrimitives.WriteUInt32BigEndian(attrHeader.Slice(12, 4), (uint)absoluteDataStart);
        BinaryPrimitives.WriteUInt32BigEndian(attrHeader.Slice(16, 4), (uint)attrDataLength);
        BinaryPrimitives.WriteUInt16BigEndian(attrHeader.Slice(32, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(attrHeader.Slice(34, 2), (ushort)attrs.Count);

        var entryOffset = attrEntriesOffset;
        var dataOffset = attrDataOffset;
        foreach (var entry in entryPayloads)
        {
            var entrySpan = output.AsSpan(entryOffset, entry.EntryLength);
            BinaryPrimitives.WriteUInt32BigEndian(entrySpan.Slice(0, 4), (uint)(finderInfoEntryOffset + dataOffset));
            BinaryPrimitives.WriteUInt32BigEndian(entrySpan.Slice(4, 4), (uint)entry.Attribute.Data.Length);
            BinaryPrimitives.WriteUInt16BigEndian(entrySpan.Slice(8, 2), 0);
            entrySpan[10] = (byte)entry.NameLengthWithNull;
            entry.NameBytes.CopyTo(entrySpan.Slice(11, entry.NameBytes.Length));
            entry.Attribute.Data.CopyTo(output.AsSpan(dataOffset, entry.Attribute.Data.Length));
            entryOffset += entry.EntryLength;
            dataOffset += entry.Attribute.Data.Length;
        }

        return output;
    }

    private static byte[]? NormalizeFinderInfo(byte[]? finderInfo)
    {
        if (finderInfo is not { Length: >= AppleDoubleFinderInfoLength })
        {
            return null;
        }

        for (var i = 0; i < AppleDoubleFinderInfoLength; i++)
        {
            if (finderInfo[i] != 0)
            {
                return finderInfo.Take(AppleDoubleFinderInfoLength).ToArray();
            }
        }

        return null;
    }

    private static IReadOnlyList<ApfsExtendedAttribute> NormalizeExtendedAttributes(IReadOnlyList<ApfsExtendedAttribute>? extendedAttributes)
    {
        if (extendedAttributes is null || extendedAttributes.Count == 0)
        {
            return Array.Empty<ApfsExtendedAttribute>();
        }

        var byName = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var attribute in extendedAttributes)
        {
            if (!IsPreservableExtendedAttributeName(attribute.Name) ||
                attribute.Data is not { Length: > 0 } data ||
                data.Length > MaxInlineResourceForkBytes)
            {
                continue;
            }

            var nameLengthWithNull = Encoding.UTF8.GetByteCount(attribute.Name) + 1;
            if (nameLengthWithNull > byte.MaxValue)
            {
                continue;
            }

            byName[attribute.Name] = data.ToArray();
        }

        return byName
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ApfsExtendedAttribute(pair.Key, pair.Value))
            .ToArray();
    }

    private static int Align4(int value) => (value + 3) & ~3;
}

internal sealed record ApfsExtendedAttribute(string Name, byte[] Data);

internal sealed record ApfsContainerSummary(
    uint BlockSize,
    ulong BlockCount,
    ulong TransactionId,
    ulong CheckpointDescriptorBase,
    uint CheckpointDescriptorBlocks,
    ulong CheckpointDataBase,
    uint CheckpointDataBlocks,
    ulong SpacemanOid,
    ulong OmapOid,
    ulong? ObjectMapBlockNumber,
    ulong? ObjectMapTreeOid,
    ulong? ObjectMapTreeBlockNumber,
    int IndexedObjectCount,
    ulong? SpacemanPhysicalBlock,
    IReadOnlyList<ApfsResolvedObjectPointer> ResolvedVolumePointers,
    IReadOnlyDictionary<ulong, ApfsVolumePreview> VolumePreviewsByOid,
    IReadOnlyList<ulong> VolumeObjectIds,
    IReadOnlyList<ApfsVolumeSuperblockHint> VolumeSuperblockHints
)
{
    public long EstimatedTotalBytes
    {
        get
        {
            var total = (decimal)BlockSize * BlockCount;
            if (total <= 0) return 0;
            return (long)Math.Min((decimal)long.MaxValue, total);
        }
    }
}

internal sealed record ApfsVolumeSuperblockHint(
    ulong BlockNumber,
    ulong ObjectId,
    ulong TransactionId,
    string VolumeName,
    ushort Role,
    ulong FsFlags
);

internal sealed record ApfsResolvedObjectPointer(
    ulong ObjectId,
    ulong TransactionId,
    ulong PhysicalBlockNumber,
    uint LogicalSize,
    uint Flags
);

internal sealed record ApfsVolumePreview(
    ulong VolumeObjectId,
    Guid VolumeUuid,
    string DisplayName,
    string RoleName,
    ushort Role,
    ulong FsFlags,
    bool IsEncrypted,
    ulong? RootTreeOid,
    ulong? RootTreeBlock,
    IReadOnlyList<ApfsPreviewEntry> RootEntries,
    IReadOnlyDictionary<string, ApfsFileReadPlan> RootFilePlansByName,
    ulong RootDirectoryId,
    IReadOnlyDictionary<ulong, IReadOnlyList<ApfsCatalogEntry>> DirectoryEntriesByParentId,
    IReadOnlyDictionary<ulong, ApfsFileReadPlan> FilePlansByObjectId,
    IReadOnlyDictionary<ulong, ApfsFileReadPlan> ResourceForkAppleDoubleByObjectId
);

internal sealed record ApfsPreviewEntry(
    string Name,
    bool IsDirectory,
    bool IsSymbolicLink = false
);

internal sealed record ApfsCatalogEntry(
    string Name,
    bool IsDirectory,
    ulong? ChildId,
    bool IsSymbolicLink = false
);

internal sealed record ApfsFileReadPlan(
    long TotalSize,
    IReadOnlyList<ApfsFileExtent> Extents,
    byte[]? InlineData = null,
    bool IsCompressed = false,
    ApfsFileReadPlan? CompressionResourceFork = null
);

internal sealed record ApfsFileExtent(
    long LogicalOffset,
    long Length,
    ulong PhysicalBlockNumber
);

internal sealed class ApfsMetadataReader
{
    internal const ulong ApfsFsOneKeyFlag = 0x00000008;
    private const uint NxsbMagic = 0x4253584E; // "NXSB" little-endian
    private const uint ApsbMagic = 0x42535041; // "APSB" little-endian
    private const ulong DefaultApfsRootDirectoryId = 2;
    private const int ApfsFsFlagsOffset = 264;
    private const int ApfsVolumeUuidOffset = 240; // apfs_vol_uuid at 0xF0 in volume superblock
    private const int ApfsVolumeNameOffset = 704;
    private const int ApfsVolumeNameLength = 256;
    private const int ApfsRoleOffset = 964;
    private const int MaxContainerFrontScanBlocks = 8192;
    private const int MaxCheckpointScanBlocks = 65536;
    private const int MaxHintResults = 128;
    private const int MaxOmapCandidateBlocks = 96;
    private const int MaxOmapTraversalDepth = 2;
    private const int MaxStructuredNodeKeys = 4096;
    private const int MaxVolumePreviewEntries = 48;
    private readonly IRawBlockDevice _device;
    private readonly long _partitionOffsetBytes;
    private readonly long _partitionLengthBytes;

    public ApfsMetadataReader(IRawBlockDevice device, long partitionOffsetBytes, long partitionLengthBytes)
    {
        _device = device;
        _partitionOffsetBytes = Math.Max(0, partitionOffsetBytes);
        _partitionLengthBytes = partitionLengthBytes > 0 ? partitionLengthBytes : Math.Max(0, device.Length - _partitionOffsetBytes);
    }

    public async Task<ApfsContainerSummary> ReadSummaryAsync(CancellationToken cancellationToken = default)
    {
        var baseNx = await ReadNxSuperblockAtContainerBlockAsync(0, assumedBlockSize: 4096, cancellationToken).ConfigureAwait(false);
        if (baseNx is null)
        {
            throw new InvalidOperationException("APFS NXSB not found at container start.");
        }

        var best = baseNx;
        if (best.CheckpointDescriptorBlocks > 0 && best.CheckpointDescriptorBase > 0)
        {
            var scanCount = (int)Math.Min(best.CheckpointDescriptorBlocks, 256u);
            for (var i = 0; i < scanCount; i++)
            {
                var blockNumber = checked(best.CheckpointDescriptorBase + (ulong)i);
                var candidate = await ReadNxSuperblockAtContainerBlockAsync(blockNumber, best.BlockSize, cancellationToken).ConfigureAwait(false);
                if (candidate is null)
                {
                    continue;
                }

                if (candidate.TransactionId > best.TransactionId)
                {
                    best = candidate;
                }
            }
        }

        var objectIndex = await BuildLatestObjectIndexAsync(best, cancellationToken).ConfigureAwait(false);
        objectIndex.TryGetValue(best.OmapOid, out var omapPtr);
        objectIndex.TryGetValue(best.SpacemanOid, out var spacemanPtr);
        var omapTreeOid = omapPtr is not null
            ? await TryReadOmapTreeOidAsync(omapPtr, best.BlockSize, cancellationToken).ConfigureAwait(false)
            : null;
        ApfsObjectPointer? omapTreePtr = null;
        if (omapTreeOid.HasValue && objectIndex.TryGetValue(omapTreeOid.Value, out var treePtr))
        {
            omapTreePtr = treePtr;
        }

        var resolvedVolumePointers = new List<ApfsResolvedObjectPointer>(best.VolumeObjectIds.Count);
        if (omapTreePtr is not null)
        {
            foreach (var volOid in best.VolumeObjectIds)
            {
                var resolved = await TryResolveOidViaOmapHeuristicAsync(
                    omapTreePtr.BlockNumber,
                    best.BlockSize,
                    best.BlockCount,
                    volOid,
                    best.TransactionId,
                    cancellationToken
                ).ConfigureAwait(false);
                if (resolved is not null)
                {
                    resolvedVolumePointers.Add(resolved);
                }
            }
        }

        var volumePreviews = new Dictionary<ulong, ApfsVolumePreview>();
        foreach (var resolved in resolvedVolumePointers)
        {
            var preview = await TryBuildVolumePreviewAsync(
                resolved,
                best.BlockSize,
                best.BlockCount,
                objectIndex,
                cancellationToken).ConfigureAwait(false);
            if (preview is not null)
            {
                volumePreviews[resolved.ObjectId] = preview;
            }
        }

        var volumeHints = await ScanVolumeSuperblockHintsAsync(best, objectIndex, cancellationToken).ConfigureAwait(false);

        return new ApfsContainerSummary(
            BlockSize: best.BlockSize,
            BlockCount: best.BlockCount,
            TransactionId: best.TransactionId,
            CheckpointDescriptorBase: best.CheckpointDescriptorBase,
            CheckpointDescriptorBlocks: best.CheckpointDescriptorBlocks,
            CheckpointDataBase: best.CheckpointDataBase,
            CheckpointDataBlocks: best.CheckpointDataBlocks,
            SpacemanOid: best.SpacemanOid,
            OmapOid: best.OmapOid,
            ObjectMapBlockNumber: omapPtr?.BlockNumber,
            ObjectMapTreeOid: omapTreeOid,
            ObjectMapTreeBlockNumber: omapTreePtr?.BlockNumber,
            IndexedObjectCount: objectIndex.Count,
            SpacemanPhysicalBlock: spacemanPtr?.BlockNumber,
            ResolvedVolumePointers: resolvedVolumePointers,
            VolumePreviewsByOid: volumePreviews,
            VolumeObjectIds: best.VolumeObjectIds,
            VolumeSuperblockHints: volumeHints
        );
    }

    private async Task<NxSuperblock?> ReadNxSuperblockAtContainerBlockAsync(ulong blockNumber, uint assumedBlockSize, CancellationToken cancellationToken)
    {
        if (assumedBlockSize < 4096 || assumedBlockSize > (1u << 20))
        {
            return null;
        }

        var offset = checked(_partitionOffsetBytes + (long)(blockNumber * assumedBlockSize));
        if (offset < _partitionOffsetBytes || offset >= checked(_partitionOffsetBytes + _partitionLengthBytes))
        {
            return null;
        }

        var buffer = new byte[assumedBlockSize];
        var read = await RawReadUtil.ReadExactlyAtAsync(_device, offset, buffer, buffer.Length, cancellationToken).ConfigureAwait(false);
        if (read < 256)
        {
            return null;
        }

        // APFS object header:
        // +16 xid (u64); nx_superblock starts at +32.
        var xid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(16, 8));
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(32, 4));
        if (magic != NxsbMagic)
        {
            return null;
        }

        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(36, 4));
        var blockCount = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(40, 8));
        if (blockSize < 4096 || blockSize > (1u << 20) || blockCount == 0)
        {
            return null;
        }

        var descBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(104, 4)); // nx_xp_desc_blocks
        var dataBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(108, 4)); // nx_xp_data_blocks
        var descBase = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(112, 8));   // nx_xp_desc_base
        var dataBase = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(120, 8));   // nx_xp_data_base
        var spacemanOid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(152, 8)); // nx_spaceman_oid
        var omapOid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(160, 8));     // nx_omap_oid

        // nx_fs_oid[100] begins at offset 184 in nx_superblock (after +32 object header).
        // We only read until available bytes in current buffer.
        var volumeIds = new List<ulong>(8);
        const int fsOidArrayOffset = 184;
        var maxCount = Math.Min(100, Math.Max(0, (buffer.Length - fsOidArrayOffset) / 8));
        for (var i = 0; i < maxCount; i++)
        {
            var oid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(fsOidArrayOffset + i * 8, 8));
            if (oid != 0)
            {
                volumeIds.Add(oid);
            }
        }

        return new NxSuperblock(
            BlockSize: blockSize,
            BlockCount: blockCount,
            TransactionId: xid,
            CheckpointDescriptorBase: descBase,
            CheckpointDescriptorBlocks: descBlocks,
            CheckpointDataBase: dataBase,
            CheckpointDataBlocks: dataBlocks,
            SpacemanOid: spacemanOid,
            OmapOid: omapOid,
            VolumeObjectIds: volumeIds
        );
    }

    private async Task<Dictionary<ulong, ApfsObjectPointer>> BuildLatestObjectIndexAsync(NxSuperblock nx, CancellationToken cancellationToken)
    {
        var index = new Dictionary<ulong, ApfsObjectPointer>();
        var seenBlocks = new HashSet<ulong>();
        foreach (var (start, count) in GetScanRanges(nx))
        {
            for (ulong i = 0; i < count; i++)
            {
                var blockNumber = start + i;
                if (blockNumber >= nx.BlockCount || !seenBlocks.Add(blockNumber))
                {
                    continue;
                }

                var pointer = await TryReadObjectPointerAtBlockAsync(blockNumber, nx.BlockSize, cancellationToken).ConfigureAwait(false);
                if (pointer is null || pointer.ObjectId == 0)
                {
                    continue;
                }

                if (!index.TryGetValue(pointer.ObjectId, out var existing) || pointer.TransactionId > existing.TransactionId)
                {
                    index[pointer.ObjectId] = pointer;
                }
            }
        }

        return index;
    }

    private async Task<IReadOnlyList<ApfsVolumeSuperblockHint>> ScanVolumeSuperblockHintsAsync(
        NxSuperblock nx,
        Dictionary<ulong, ApfsObjectPointer> objectIndex,
        CancellationToken cancellationToken)
    {
        var blockSize = nx.BlockSize;
        var hints = new List<ApfsVolumeSuperblockHint>(16);
        var seenBlocks = new HashSet<ulong>();
        var dedupeByOid = new Dictionary<ulong, ApfsVolumeSuperblockHint>();

        // Fast path: use indexed objects for known nx_fs_oid values.
        foreach (var volOid in nx.VolumeObjectIds)
        {
            if (!objectIndex.TryGetValue(volOid, out var ptr))
            {
                continue;
            }

            var hint = await TryReadVolumeSuperblockHintAsync(ptr.BlockNumber, blockSize, cancellationToken).ConfigureAwait(false);
            if (hint is not null)
            {
                dedupeByOid[hint.ObjectId] = hint;
                seenBlocks.Add(ptr.BlockNumber);
            }
        }

        // Fallback scan for additional checkpoints/rotations.
        foreach (var (start, count) in GetScanRanges(nx))
        {
            for (ulong i = 0; i < count; i++)
            {
                if (hints.Count >= MaxHintResults)
                {
                    break;
                }

                var blockNumber = start + i;
                if (blockNumber >= nx.BlockCount || !seenBlocks.Add(blockNumber))
                {
                    continue;
                }

                var hint = await TryReadVolumeSuperblockHintAsync(blockNumber, blockSize, cancellationToken).ConfigureAwait(false);
                if (hint is null)
                {
                    continue;
                }

                if (!dedupeByOid.TryGetValue(hint.ObjectId, out var existing) || hint.TransactionId > existing.TransactionId)
                {
                    dedupeByOid[hint.ObjectId] = hint;
                }
            }
        }

        hints.AddRange(dedupeByOid.Values.OrderByDescending(x => x.TransactionId));
        return hints;
    }

    private IEnumerable<(ulong Start, ulong Count)> GetScanRanges(NxSuperblock nx)
    {
        yield return (0, (ulong)Math.Min((ulong)MaxContainerFrontScanBlocks, nx.BlockCount));
        if (nx.CheckpointDescriptorBlocks > 0)
        {
            yield return (nx.CheckpointDescriptorBase, Math.Min((ulong)nx.CheckpointDescriptorBlocks, (ulong)MaxCheckpointScanBlocks));
        }
        if (nx.CheckpointDataBlocks > 0)
        {
            yield return (nx.CheckpointDataBase, Math.Min((ulong)nx.CheckpointDataBlocks, (ulong)MaxCheckpointScanBlocks));
        }
    }

    private async Task<ApfsObjectPointer?> TryReadObjectPointerAtBlockAsync(ulong blockNumber, uint blockSize, CancellationToken cancellationToken)
    {
        var offset = checked(_partitionOffsetBytes + (long)(blockNumber * blockSize));
        if (offset < _partitionOffsetBytes || offset >= checked(_partitionOffsetBytes + _partitionLengthBytes))
        {
            return null;
        }

        var buffer = new byte[Math.Max(64u, blockSize)];
        var read = await RawReadUtil.ReadExactlyAtAsync(_device, offset, buffer, buffer.Length, cancellationToken).ConfigureAwait(false);
        if (read < 32)
        {
            return null;
        }

        // APFS object header:
        // +0 checksum(u64), +8 oid(u64), +16 xid(u64), +24 type(u32), +28 subtype(u32)
        var oid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
        var xid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(16, 8));
        var type = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24, 4));
        var subtype = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(28, 4));
        if (oid == 0)
        {
            return null;
        }

        return new ApfsObjectPointer(blockNumber, oid, xid, type, subtype);
    }

    private async Task<ulong?> TryReadOmapTreeOidAsync(ApfsObjectPointer omapPointer, uint blockSize, CancellationToken cancellationToken)
    {
        var offset = checked(_partitionOffsetBytes + (long)(omapPointer.BlockNumber * blockSize));
        if (offset < _partitionOffsetBytes || offset >= checked(_partitionOffsetBytes + _partitionLengthBytes))
        {
            return null;
        }

        var buffer = new byte[Math.Max(128u, blockSize)];
        var read = await RawReadUtil.ReadExactlyAtAsync(_device, offset, buffer, buffer.Length, cancellationToken).ConfigureAwait(false);
        if (read < 64)
        {
            return null;
        }

        var oid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
        if (oid != omapPointer.ObjectId)
        {
            return null;
        }

        // omap_phys starts after object header (32 bytes). om_tree_oid is at +16 in omap_phys body.
        // absolute offset: 32 + 16 = 48.
        var treeOid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(48, 8));
        return treeOid == 0 ? null : treeOid;
    }

    private async Task<ApfsResolvedObjectPointer?> TryResolveOidViaOmapHeuristicAsync(
        ulong omapTreeBlockNumber,
        uint blockSize,
        ulong blockCount,
        ulong targetOid,
        ulong maxXid,
        CancellationToken cancellationToken)
    {
        var ordered = await TryResolveOidViaOrderedDescentAsync(
            omapTreeBlockNumber,
            blockSize,
            blockCount,
            targetOid,
            maxXid,
            cancellationToken).ConfigureAwait(false);
        if (ordered is not null)
        {
            return ordered;
        }

        var visited = new HashSet<ulong>();
        var queue = new Queue<(ulong Block, int Depth)>();
        queue.Enqueue((omapTreeBlockNumber, 0));
        ApfsResolvedObjectPointer? best = null;

        while (queue.Count > 0 && visited.Count < MaxOmapCandidateBlocks)
        {
            var (candidateBlock, depth) = queue.Dequeue();
            if (!visited.Add(candidateBlock))
            {
                continue;
            }

            var buffer = await ReadBlockBufferAsync(candidateBlock, blockSize, cancellationToken).ConfigureAwait(false);
            if (buffer is null)
            {
                continue;
            }

            var resolved = TryResolveInSingleNode(buffer, targetOid, maxXid, blockCount, blockSize);
            if (resolved is not null && (best is null || resolved.TransactionId > best.TransactionId))
            {
                best = resolved;
            }

            if (depth >= MaxOmapTraversalDepth)
            {
                continue;
            }

            foreach (var child in ExtractLikelyChildBlockPointers(buffer, blockCount))
            {
                if (!visited.Contains(child))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return best;
    }

    private async Task<ApfsResolvedObjectPointer?> TryResolveOidViaOrderedDescentAsync(
        ulong rootBlock,
        uint blockSize,
        ulong blockCount,
        ulong targetOid,
        ulong maxXid,
        CancellationToken cancellationToken)
    {
        var currentBlock = rootBlock;
        var depth = 0;
        var seen = new HashSet<ulong>();

        while (depth <= MaxOmapTraversalDepth && seen.Add(currentBlock))
        {
            var buffer = await ReadBlockBufferAsync(currentBlock, blockSize, cancellationToken).ConfigureAwait(false);
            if (buffer is null)
            {
                return null;
            }

            if (!TryDecodeStructuredNodeSlots(buffer, out var slots, out _, out var isLeaf) || slots.Count == 0)
            {
                return null;
            }

            var keyed = new List<(NodeSlot Slot, OmapKey Key)>(slots.Count);
            foreach (var slot in slots)
            {
                if (TryExtractOmapKey(buffer, slot, out var key))
                {
                    keyed.Add((slot, key));
                }
            }
            if (keyed.Count == 0)
            {
                return null;
            }

            keyed.Sort((a, b) => CompareOmapKey(a.Key, b.Key));
            var target = new OmapKey(targetOid, maxXid);

            if (isLeaf)
            {
                ApfsResolvedObjectPointer? best = null;
                foreach (var item in keyed)
                {
                    if (item.Key.Oid != targetOid || item.Key.Xid == 0 || item.Key.Xid > maxXid)
                    {
                        continue;
                    }

                    var resolved = TryParseResolvedValue(buffer, item.Slot, item.Key, blockCount);
                    if (resolved is not null && (best is null || resolved.TransactionId > best.TransactionId))
                    {
                        best = resolved;
                    }
                }
                return best;
            }

            NodeSlot? selected = null;
            OmapKey? selectedKey = null;
            foreach (var item in keyed)
            {
                if (CompareOmapKey(item.Key, target) <= 0)
                {
                    selected = item.Slot;
                    selectedKey = item.Key;
                }
                else
                {
                    break;
                }
            }

            if (selected is null)
            {
                selected = keyed[0].Slot;
                selectedKey = keyed[0].Key;
            }

            var child = TryReadChildPointerFromSlot(buffer, selected!, blockCount);
            if (!child.HasValue || child.Value == currentBlock)
            {
                return null;
            }

            currentBlock = child.Value;
            depth++;
        }

        return null;
    }

    private static ApfsResolvedObjectPointer? TryResolveInSingleNode(
        byte[] buffer,
        ulong targetOid,
        ulong maxXid,
        ulong blockCount,
        uint blockSize)
    {
        var candidates = DecodeOmapNodeTableCandidates(buffer, targetOid, maxXid, blockCount);
        if (candidates.Count > 0)
        {
            return candidates.OrderByDescending(x => x.TransactionId).FirstOrDefault();
        }

        ApfsResolvedObjectPointer? best = null;
        for (var i = 32; i + 32 <= buffer.Length; i += 8)
        {
            var oid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, 8));
            if (oid != targetOid) continue;

            var xid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i + 8, 8));
            if (xid == 0 || xid > maxXid) continue;

            var flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 16, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 20, 4));
            var paddr = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i + 24, 8));
            if (paddr == 0 || paddr >= blockCount) continue;
            if (size == 0 || size > blockSize * 4) continue;

            if (best is null || xid > best.TransactionId)
            {
                best = new ApfsResolvedObjectPointer(oid, xid, paddr, size, flags);
            }
        }

        return best;
    }

    private static bool TryExtractOmapKey(byte[] nodeBuffer, NodeSlot slot, out OmapKey key)
    {
        foreach (var absKeyOff in EnumerateAbsoluteOffsets(slot.KeyOffset))
        {
            if (absKeyOff < 0 || absKeyOff + 16 > nodeBuffer.Length)
            {
                continue;
            }
            var oid = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absKeyOff, 8));
            var xid = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absKeyOff + 8, 8));
            if (oid == 0)
            {
                continue;
            }

            key = new OmapKey(oid, xid);
            return true;
        }

        key = default;
        return false;
    }

    private static int CompareOmapKey(OmapKey a, OmapKey b)
    {
        var oc = a.Oid.CompareTo(b.Oid);
        if (oc != 0) return oc;
        return a.Xid.CompareTo(b.Xid);
    }

    private static ApfsResolvedObjectPointer? TryParseResolvedValue(
        byte[] nodeBuffer,
        NodeSlot slot,
        OmapKey key,
        ulong blockCount)
    {
        foreach (var absValOff in EnumerateAbsoluteOffsets(slot.ValueOffset))
        {
            if (absValOff < 0 || absValOff + 16 > nodeBuffer.Length)
            {
                continue;
            }

            var flags = BinaryPrimitives.ReadUInt32LittleEndian(nodeBuffer.AsSpan(absValOff, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(nodeBuffer.AsSpan(absValOff + 4, 4));
            var paddr = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absValOff + 8, 8));
            if (paddr == 0 || paddr >= blockCount)
            {
                continue;
            }
            if (size == 0 || size > nodeBuffer.Length * 4)
            {
                continue;
            }

            return new ApfsResolvedObjectPointer(
                ObjectId: key.Oid,
                TransactionId: key.Xid,
                PhysicalBlockNumber: paddr,
                LogicalSize: size,
                Flags: flags);
        }

        return null;
    }

    private async Task<ApfsVolumePreview?> TryBuildVolumePreviewAsync(
        ApfsResolvedObjectPointer resolvedVolumePointer,
        uint blockSize,
        ulong blockCount,
        Dictionary<ulong, ApfsObjectPointer> objectIndex,
        CancellationToken cancellationToken)
    {
        var volumeBuffer = await ReadBlockBufferAsync(resolvedVolumePointer.PhysicalBlockNumber, blockSize, cancellationToken).ConfigureAwait(false);
        if (volumeBuffer is null)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(volumeBuffer.AsSpan(32, 4));
        if (magic != ApsbMagic)
        {
            return null;
        }

        var volumeName = TryReadUtf8NullTerminatedString(volumeBuffer, ApfsVolumeNameOffset, ApfsVolumeNameLength);
        var volumeUuid = Guid.Empty;
        if (volumeBuffer.Length >= ApfsVolumeUuidOffset + 16)
        {
            var uuidBytes = new byte[16];
            Array.Copy(volumeBuffer, ApfsVolumeUuidOffset, uuidBytes, 0, 16);
            volumeUuid = new Guid(uuidBytes);
        }
        var role = volumeBuffer.Length >= ApfsRoleOffset + 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(volumeBuffer.AsSpan(ApfsRoleOffset, 2))
            : (ushort)0;
        var fsFlags = volumeBuffer.Length >= ApfsFsFlagsOffset + 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(volumeBuffer.AsSpan(ApfsFsFlagsOffset, 8))
            : 0UL;
        var rootTreeOid = TryExtractVolumeRootTreeOid(volumeBuffer, objectIndex);
        ulong? rootTreeBlock = null;
        if (rootTreeOid.HasValue && objectIndex.TryGetValue(rootTreeOid.Value, out var rootTreePtr))
        {
            rootTreeBlock = rootTreePtr.BlockNumber;
        }

        var rootEntries = new List<ApfsPreviewEntry>();
        var rootFilePlansByName = new Dictionary<string, ApfsFileReadPlan>(StringComparer.OrdinalIgnoreCase);
        ulong rootDirectoryId = DefaultApfsRootDirectoryId;
        IReadOnlyDictionary<ulong, IReadOnlyList<ApfsCatalogEntry>> directoryEntriesByParentId = new Dictionary<ulong, IReadOnlyList<ApfsCatalogEntry>>();
        IReadOnlyDictionary<ulong, ApfsFileReadPlan> filePlansByObjectId = new Dictionary<ulong, ApfsFileReadPlan>();
        IReadOnlyDictionary<ulong, ApfsFileReadPlan> resourceForkAppleDoubleByObjectId = new Dictionary<ulong, ApfsFileReadPlan>();
        if (rootTreeBlock.HasValue)
        {
            var traversal = await TraverseFsTreePreviewEntriesAsync(
                    rootTreeBlock.Value,
                    blockSize,
                    blockCount,
                    MaxVolumePreviewEntries,
                    cancellationToken).ConfigureAwait(false);
            rootEntries.AddRange(traversal.Entries);
            foreach (var kv in traversal.RootFilePlansByName)
            {
                rootFilePlansByName[kv.Key] = kv.Value;
            }
            rootDirectoryId = traversal.RootDirectoryId;
            directoryEntriesByParentId = traversal.DirectoryEntriesByParentId;
            filePlansByObjectId = traversal.FilePlansByObjectId;
            resourceForkAppleDoubleByObjectId = traversal.ResourceForkAppleDoubleByObjectId;
        }

        return new ApfsVolumePreview(
            VolumeObjectId: resolvedVolumePointer.ObjectId,
            VolumeUuid: volumeUuid,
            DisplayName: string.IsNullOrWhiteSpace(volumeName) ? $"Volume_{resolvedVolumePointer.ObjectId:X}" : volumeName,
            RoleName: ApfsRawFileSystemProvider.DescribeVolumeRole(role),
            Role: role,
            FsFlags: fsFlags,
            IsEncrypted: (fsFlags & ApfsFsOneKeyFlag) != 0,
            RootTreeOid: rootTreeOid,
            RootTreeBlock: rootTreeBlock,
            RootEntries: rootEntries,
            RootFilePlansByName: rootFilePlansByName,
            RootDirectoryId: rootDirectoryId,
            DirectoryEntriesByParentId: directoryEntriesByParentId,
            FilePlansByObjectId: filePlansByObjectId,
            ResourceForkAppleDoubleByObjectId: resourceForkAppleDoubleByObjectId
        );
    }

    private async Task<ApfsVolumeTraversalResult> TraverseFsTreePreviewEntriesAsync(
        ulong rootTreeBlock,
        uint blockSize,
        ulong blockCount,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        var fallbackNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extentsByChildId = new Dictionary<ulong, List<ApfsFileExtent>>();
        var inlineDataByObjectId = new Dictionary<ulong, (byte[]? InlineData, bool IsCompressed, long? LogicalSize)>();
        var resourceForkByObjectId = new Dictionary<ulong, ApfsFileReadPlan>();
        var resourceForkAppleDoubleByObjectId = new Dictionary<ulong, ApfsFileReadPlan>();
        var dirEntriesByParentId = new Dictionary<ulong, Dictionary<string, ApfsCatalogEntry>>();
        var visitedBlocks = new HashSet<ulong>();
        var queue = new Queue<(ulong Block, int Depth)>();
        queue.Enqueue((rootTreeBlock, 0));

        const int maxDepth = 2;
        const int maxVisitedBlocks = 192;

        while (queue.Count > 0 && visitedBlocks.Count < maxVisitedBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (block, depth) = queue.Dequeue();
            if (!visitedBlocks.Add(block))
            {
                continue;
            }

            var node = await ReadBlockBufferAsync(block, blockSize, cancellationToken).ConfigureAwait(false);
            if (node is null)
            {
                continue;
            }

            if (TryDecodeStructuredNodeSlots(node, out var slots, out _, out var isLeaf) && isLeaf)
            {
                var entries = TryExtractFsTreePreviewEntries(node);
                if (entries.Count == 0)
                {
                    var names = ExtractLikelyApfsNames(node);
                    foreach (var name in names)
                    {
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            fallbackNames.Add(name);
                        }
                    }
                }
                else
                {
                    foreach (var e in entries)
                    {
                        if (e.KeyType != FsKeyType.DirRecord)
                        {
                            continue;
                        }

                        if (!dirEntriesByParentId.TryGetValue(e.ParentId, out var byName))
                        {
                            byName = new Dictionary<string, ApfsCatalogEntry>(StringComparer.OrdinalIgnoreCase);
                            dirEntriesByParentId[e.ParentId] = byName;
                        }

                        if (!byName.TryGetValue(e.Name, out var existing) ||
                            (!existing.ChildId.HasValue && e.ChildId.HasValue))
                        {
                            byName[e.Name] = new ApfsCatalogEntry(e.Name, e.IsDirectory, e.ChildId, e.IsSymbolicLink);
                        }
                    }
                }

                var nodeExtents = TryExtractFsTreeFileExtents(node, blockSize, blockCount);
                foreach (var kv in nodeExtents)
                {
                    if (!extentsByChildId.TryGetValue(kv.Key, out var list))
                    {
                        list = new List<ApfsFileExtent>();
                        extentsByChildId[kv.Key] = list;
                    }
                    list.AddRange(kv.Value);
                }

                var nodeInodeData = BuildInodeDataMap(node, slots, extentsByChildId);
                foreach (var kv in nodeInodeData)
                {
                    if (!inlineDataByObjectId.ContainsKey(kv.Key))
                    {
                        inlineDataByObjectId[kv.Key] = kv.Value;
                    }
                }

                var nodeXattrs = BuildResourceForkMaps(node, slots, extentsByChildId);
                foreach (var kv in nodeXattrs.ResourceForkByObjectId)
                {
                    if (!resourceForkByObjectId.ContainsKey(kv.Key))
                    {
                        resourceForkByObjectId[kv.Key] = kv.Value;
                    }
                }
                foreach (var kv in nodeXattrs.AppleDoubleByObjectId)
                {
                    if (!resourceForkAppleDoubleByObjectId.ContainsKey(kv.Key))
                    {
                        resourceForkAppleDoubleByObjectId[kv.Key] = kv.Value;
                    }
                }
                foreach (var kv in nodeXattrs.DecmpfsByObjectId)
                {
                    inlineDataByObjectId[kv.Key] = (
                        kv.Value,
                        IsCompressed: true,
                        LogicalSize: ApfsDecmpfs.TryReadInlineUncompressedSize(kv.Value));
                }
                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var child in ExtractLikelyChildBlockPointers(node, blockCount))
            {
                if (!visitedBlocks.Contains(child))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        var rootDirectoryId = dirEntriesByParentId.ContainsKey(DefaultApfsRootDirectoryId)
            ? DefaultApfsRootDirectoryId
            : dirEntriesByParentId
                .OrderByDescending(x => x.Value.Count)
                .Select(x => x.Key)
                .FirstOrDefault();

        var results = new List<ApfsPreviewEntry>(Math.Max(8, maxEntries));
        var rootFilePlansByName = new Dictionary<string, ApfsFileReadPlan>(StringComparer.OrdinalIgnoreCase);
        var rootChildIdToName = new Dictionary<ulong, string>();
        var rootChildIdToIsDir = new Dictionary<ulong, bool>();

        if (rootDirectoryId != 0 &&
            dirEntriesByParentId.TryGetValue(rootDirectoryId, out var rootCatalogEntriesByName))
        {
            foreach (var item in rootCatalogEntriesByName.Values
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxEntries))
            {
                results.Add(new ApfsPreviewEntry(item.Name, item.IsDirectory, item.IsSymbolicLink));
                if (item.ChildId.HasValue)
                {
                    rootChildIdToName[item.ChildId.Value] = item.Name;
                    rootChildIdToIsDir[item.ChildId.Value] = item.IsDirectory;
                }
            }
        }
        else
        {
            foreach (var name in fallbackNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(maxEntries))
            {
                results.Add(new ApfsPreviewEntry(name, IsDirectory: false));
            }
        }

        var filePlansByObjectId = new Dictionary<ulong, ApfsFileReadPlan>();
        foreach (var kv in extentsByChildId)
        {
            if (rootChildIdToIsDir.TryGetValue(kv.Key, out var isDir) && isDir)
            {
                continue;
            }

            var ordered = kv.Value.OrderBy(x => x.LogicalOffset).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }
            byte[]? inlineData = null;
            var isCompressed = false;
            long? inodeLogicalSize = null;
            if (inlineDataByObjectId.TryGetValue(kv.Key, out var inodeInfo))
            {
                inlineData = inodeInfo.InlineData;
                isCompressed = inodeInfo.IsCompressed;
                inodeLogicalSize = inodeInfo.LogicalSize;
            }

            resourceForkByObjectId.TryGetValue(kv.Key, out var compressionResourceFork);
            var totalSize = ApfsRawFileSystemProvider.GetExtentBackedLogicalSize(ordered, inodeLogicalSize, inlineData, isCompressed);
            if (totalSize <= 0)
            {
                continue;
            }

            var plan = new ApfsFileReadPlan(
                totalSize,
                ordered,
                inlineData,
                isCompressed,
                isCompressed ? compressionResourceFork : null);
            filePlansByObjectId[kv.Key] = plan;
            if (rootChildIdToName.TryGetValue(kv.Key, out var name))
            {
                rootFilePlansByName[name] = plan;
            }
        }

        foreach (var kv in inlineDataByObjectId)
        {
            if (filePlansByObjectId.ContainsKey(kv.Key)) continue;
            if (rootChildIdToIsDir.TryGetValue(kv.Key, out var isDir) && isDir) continue;
            if (kv.Value.InlineData is null)
            {
                if (kv.Value.IsCompressed && rootChildIdToName.TryGetValue(kv.Key, out var cname))
                {
                    resourceForkByObjectId.TryGetValue(kv.Key, out var compressionResourceFork);
                    filePlansByObjectId[kv.Key] = new ApfsFileReadPlan(
                        Math.Max(0, kv.Value.LogicalSize ?? 0),
                        Array.Empty<ApfsFileExtent>(),
                        null,
                        true,
                        compressionResourceFork);
                    rootFilePlansByName[cname] = filePlansByObjectId[kv.Key];
                }
                continue;
            }

            var logicalSize = ApfsDecmpfs.GetInlineDataLogicalSize(kv.Value.InlineData, kv.Value.IsCompressed);
            resourceForkByObjectId.TryGetValue(kv.Key, out var inlineCompressionResourceFork);
            var iPlan = new ApfsFileReadPlan(
                logicalSize,
                Array.Empty<ApfsFileExtent>(),
                kv.Value.InlineData,
                kv.Value.IsCompressed,
                kv.Value.IsCompressed ? inlineCompressionResourceFork : null);
            filePlansByObjectId[kv.Key] = iPlan;
            if (rootChildIdToName.TryGetValue(kv.Key, out var iname))
            {
                rootFilePlansByName[iname] = iPlan;
            }
        }

        var readonlyCatalog = dirEntriesByParentId.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ApfsCatalogEntry>)kv.Value.Values
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray());

        return new ApfsVolumeTraversalResult(
            results,
            rootFilePlansByName,
            rootDirectoryId == 0 ? DefaultApfsRootDirectoryId : rootDirectoryId,
            readonlyCatalog,
            filePlansByObjectId,
            resourceForkAppleDoubleByObjectId);
    }

    private static ulong? TryExtractVolumeRootTreeOid(byte[] volumeBuffer, Dictionary<ulong, ApfsObjectPointer> objectIndex)
    {
        // APFS volume superblock contains multiple object OIDs; root tree OID is one of them.
        // Try a prioritized offset list (based on common apfs_superblock layouts), then fallback scan.
        var candidates = new List<ulong>();
        foreach (var off in new[] { 152, 160, 168, 176, 184, 192, 200, 208 })
        {
            if (off + 8 <= volumeBuffer.Length)
            {
                candidates.Add(BinaryPrimitives.ReadUInt64LittleEndian(volumeBuffer.AsSpan(off, 8)));
            }
        }

        for (var off = 128; off + 8 <= Math.Min(volumeBuffer.Length, 320); off += 8)
        {
            candidates.Add(BinaryPrimitives.ReadUInt64LittleEndian(volumeBuffer.AsSpan(off, 8)));
        }

        // prefer OIDs that exist in current object index and are not the volume's own oid/header fields.
        foreach (var oid in candidates.Distinct())
        {
            if (oid == 0)
            {
                continue;
            }
            if (objectIndex.ContainsKey(oid))
            {
                return oid;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractLikelyApfsNames(byte[] buffer)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        void flush()
        {
            if (sb.Length < 3 || sb.Length > 120)
            {
                sb.Clear();
                return;
            }
            var name = sb.ToString();
            sb.Clear();

            if (name.StartsWith(".") || name.Contains("\\") || name.Contains("/") || name.Contains(":"))
            {
                return;
            }
            if (name.All(char.IsDigit))
            {
                return;
            }

            results.Add(name);
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            var b = buffer[i];
            var isAlphaNum = (b >= (byte)'0' && b <= (byte)'9')
                || (b >= (byte)'A' && b <= (byte)'Z')
                || (b >= (byte)'a' && b <= (byte)'z');
            var isCommon = b is (byte)' ' or (byte)'_' or (byte)'-' or (byte)'.' or (byte)'(' or (byte)')';
            if (isAlphaNum || isCommon)
            {
                sb.Append((char)b);
                continue;
            }

            flush();
        }
        flush();

        return results.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<FsTreePreviewItem> TryExtractFsTreePreviewEntries(byte[] fsTreeNodeBuffer)
    {
        var output = new List<FsTreePreviewItem>();
        if (!TryDecodeStructuredNodeSlots(fsTreeNodeBuffer, out var slots, out _, out _))
        {
            return output;
        }

        var inodeModesById = BuildInodeModeMap(fsTreeNodeBuffer, slots);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots)
        {
            if (!TryReadFsTreeRecord(fsTreeNodeBuffer, slot, inodeModesById, out var record))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(record.Name) || record.Name is "." or "..")
            {
                continue;
            }
            if (record.Name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }
            if (!seen.Add($"{record.ParentId:X}:{record.Name}"))
            {
                continue;
            }

            output.Add(new FsTreePreviewItem(record.KeyType, record.ParentId, record.Name, record.IsDirectory, record.ChildId, record.IsSymbolicLink));
            if (output.Count >= MaxVolumePreviewEntries)
            {
                break;
            }
        }

        return output;
    }

    private static Dictionary<ulong, List<ApfsFileExtent>> TryExtractFsTreeFileExtents(
        byte[] nodeBuffer,
        uint blockSize,
        ulong blockCount)
    {
        var result = new Dictionary<ulong, List<ApfsFileExtent>>();
        if (!TryDecodeStructuredNodeSlots(nodeBuffer, out var slots, out _, out _))
        {
            return result;
        }

        foreach (var slot in slots)
        {
            if (!TryDecodeFsTreeKeyFromNode(nodeBuffer, slot, out var key) || key.Type != FsKeyType.FileExtent)
            {
                continue;
            }

            long logicalOffset = 0;
            if (key.NamePayload.Length >= 8)
            {
                var raw = BinaryPrimitives.ReadUInt64LittleEndian(key.NamePayload.AsSpan(0, 8));
                logicalOffset = raw <= blockCount ? checked((long)(raw * blockSize)) : (long)raw;
            }

            foreach (var absValOff in EnumerateAbsoluteOffsets(slot.ValueOffset))
            {
                if (absValOff < 0 || absValOff + 16 > nodeBuffer.Length)
                {
                    continue;
                }
                var v0 = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absValOff + 0, 8));
                var v1 = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absValOff + 8, 8));

                ulong paddr;
                long length;
                if (v0 > 0 && v0 < blockCount && v1 > 0)
                {
                    paddr = v0;
                    length = v1 <= blockCount ? checked((long)(v1 * blockSize)) : (long)v1;
                }
                else if (v1 > 0 && v1 < blockCount && v0 > 0)
                {
                    paddr = v1;
                    length = v0 <= blockCount ? checked((long)(v0 * blockSize)) : (long)v0;
                }
                else
                {
                    continue;
                }

                if (length <= 0 || paddr == 0 || paddr >= blockCount)
                {
                    continue;
                }

                if (!result.TryGetValue(key.ObjectId, out var list))
                {
                    list = new List<ApfsFileExtent>();
                    result[key.ObjectId] = list;
                }
                list.Add(new ApfsFileExtent(logicalOffset, length, paddr));
                break;
            }
        }

        return result;
    }

    private static bool TryReadFsTreeRecord(
        byte[] nodeBuffer,
        NodeSlot slot,
        IReadOnlyDictionary<ulong, ushort> inodeModesById,
        out FsTreeRecord record)
    {
        record = default;

        foreach (var absKeyOff in EnumerateAbsoluteOffsets(slot.KeyOffset))
        {
            if (absKeyOff < 0 || absKeyOff + 8 > nodeBuffer.Length)
            {
                continue;
            }

            var keyLen = slot.KeyLength;
            if (keyLen < 9 || absKeyOff + keyLen > nodeBuffer.Length)
            {
                continue;
            }

            if (!TryDecodeFsTreeKey(nodeBuffer.AsSpan(absKeyOff, keyLen), out var key))
            {
                continue;
            }
            if (key.Type is not FsKeyType.DirRecord and not FsKeyType.Xattr and not FsKeyType.FileExtent)
            {
                continue;
            }

            var nameBytes = key.NamePayload.AsSpan();
            var name = DecodeName(nameBytes);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var isDir = false;
            var isSymlink = false;
            ulong? childId = null;
            foreach (var absValOff in EnumerateAbsoluteOffsets(slot.ValueOffset))
            {
                if (absValOff < 0 || absValOff + 16 > nodeBuffer.Length)
                {
                    continue;
                }
                if (slot.ValueLength > 0 && absValOff + slot.ValueLength > nodeBuffer.Length)
                {
                    continue;
                }

                var limit = slot.ValueLength > 0 ? Math.Min((int)slot.ValueLength, 64) : 64;
                if (TryDecodeDrecValue(nodeBuffer.AsSpan(absValOff, limit), out var drec))
                {
                    childId = drec.ChildId;
                    if (drec.FileType == DrecFileType.Directory)
                    {
                        isDir = true;
                    }
                    else if (drec.FileType == DrecFileType.Symlink)
                    {
                        isDir = false;
                        isSymlink = true;
                    }
                }

                if (!isDir && !isSymlink)
                {
                    for (var i = 0; i + 4 <= limit; i += 2)
                    {
                        var v = BinaryPrimitives.ReadUInt32LittleEndian(nodeBuffer.AsSpan(absValOff + i, 4));
                        // Common "directory" discriminator values seen in drec-like payloads.
                        if (v is 2 or 8)
                        {
                            isDir = true;
                            break;
                        }
                    }
                }

                break;
            }

            // Inode join: if drec child id maps to inode mode, use authoritative mode bits.
            if (childId.HasValue && inodeModesById.TryGetValue(childId.Value, out var mode))
            {
                if ((mode & 0xF000) == 0x4000)
                {
                    isDir = true;
                    isSymlink = false;
                }
                else if ((mode & 0xF000) == 0x8000)
                {
                    isDir = false;
                    isSymlink = false;
                }
                else if ((mode & 0xF000) == 0xA000)
                {
                    isDir = false;
                    isSymlink = true;
                }
            }

            record = new FsTreeRecord(key.Type, key.ObjectId, name, isDir, childId, isSymlink);
            return true;
        }

        return false;
    }

    private static Dictionary<ulong, ushort> BuildInodeModeMap(byte[] nodeBuffer, IReadOnlyList<NodeSlot> slots)
    {
        var map = new Dictionary<ulong, ushort>();
        foreach (var slot in slots)
        {
            if (!TryDecodeFsTreeKeyFromNode(nodeBuffer, slot, out var key) || key.Type != FsKeyType.Inode)
            {
                continue;
            }

            foreach (var absValOff in EnumerateAbsoluteOffsets(slot.ValueOffset))
            {
                if (absValOff < 0 || absValOff + 2 > nodeBuffer.Length)
                {
                    continue;
                }

                var limit = slot.ValueLength > 0 ? Math.Min((int)slot.ValueLength, 80) : 80;
                var mode = TryExtractModeFromValue(nodeBuffer.AsSpan(absValOff, Math.Min(limit, nodeBuffer.Length - absValOff)));
                if (mode.HasValue)
                {
                    map[key.ObjectId] = mode.Value;
                    break;
                }
            }
        }
        return map;
    }

    private static Dictionary<ulong, (byte[]? InlineData, bool IsCompressed, long? LogicalSize)> BuildInodeDataMap(
        byte[] nodeBuffer,
        IReadOnlyList<NodeSlot> slots,
        IReadOnlyDictionary<ulong, List<ApfsFileExtent>> extentsByChildId)
    {
        var map = new Dictionary<ulong, (byte[]?, bool, long?)>();
        foreach (var slot in slots)
        {
            if (!TryDecodeFsTreeKeyFromNode(nodeBuffer, slot, out var key) || key.Type != FsKeyType.Inode)
            {
                continue;
            }

            foreach (var absValOff in EnumerateAbsoluteOffsets(slot.ValueOffset))
            {
                if (absValOff < 0 || absValOff + 4 > nodeBuffer.Length)
                {
                    continue;
                }

                var valLen = slot.ValueLength > 0 ? Math.Min((int)slot.ValueLength, 256) : 256;
                if (absValOff + valLen > nodeBuffer.Length) valLen = nodeBuffer.Length - absValOff;
                if (valLen < 32) continue;

                var inodeData = nodeBuffer.AsSpan(absValOff, valLen);

                // APFS inode layout (common):
                // offset 0-7: parent_id
                // offset 8-9: private_id
                // offset 10-11: create_time (nanoseconds)
                // offset 12-13: mod_time
                // offset 14-15: change_time
                // offset 16-17: access_time
                // offset 18-19: nchildren (dirs) / nlink
                // offset 20-21: mode (unix permissions + file type)
                // offset 22-23: owner (uid)
                // offset 24-25: group (gid)
                // offset 26-27: flags (bsd_flags, includes UF_COMPRESSED=0x20)
                // offset 28-31: internal_flags
                // offset 32-39: union_size (for regular files: logical size)
                // offset 40-47: gen_count
                // offset 48-55: rec_ext_size
                // offset 56-63: alloc_size
                // offset 64+: inline_data (if internal_flags has INLINE_DATA_FLAG=0x20000000)

                var mode = TryExtractModeFromValue(inodeData.Slice(20, Math.Min(4, inodeData.Length - 20)));
                if (!mode.HasValue) continue;
                var isRegular = (mode.Value & 0xF000) == 0x8000;
                if (!isRegular) continue;

                var internalFlags = valLen >= 32 ? BinaryPrimitives.ReadUInt32LittleEndian(inodeData.Slice(28, 4)) : 0u;
                var bsdFlags = valLen >= 28 ? BinaryPrimitives.ReadUInt16LittleEndian(inodeData.Slice(26, 2)) : (ushort)0;
                var logicalSize = TryReadApfsInodeLogicalSize(inodeData);
                const uint INLINE_DATA_FLAG = 0x20000000;
                const ushort UF_COMPRESSED = 0x0020;

                var isCompressed = (bsdFlags & UF_COMPRESSED) != 0;
                var hasExtents = extentsByChildId.ContainsKey(key.ObjectId);

                if (!hasExtents && (internalFlags & INLINE_DATA_FLAG) != 0 && valLen >= 80)
                {
                    // Inline data starts around offset 64 in the inode value
                    // The size is stored in union_size at offset 32
                    var inlineSize = (int)BinaryPrimitives.ReadUInt64LittleEndian(inodeData.Slice(32, 8));
                    if (inlineSize > 0 && inlineSize <= 3800 && valLen >= 64 + inlineSize)
                    {
                        var inlineBytes = inodeData.Slice(64, inlineSize).ToArray();
                        map[key.ObjectId] = (inlineBytes, isCompressed, logicalSize);
                    }
                }
                else if (isCompressed || logicalSize.HasValue)
                {
                    // File has extents or unsupported compressed data. Keep inode
                    // metadata so read plans preserve APFS logical size.
                    map[key.ObjectId] = (null, isCompressed, logicalSize);
                }

                break;
            }
        }
        return map;
    }

    private static long? TryReadApfsInodeLogicalSize(ReadOnlySpan<byte> inodeData)
    {
        if (inodeData.Length < 40)
        {
            return null;
        }

        var raw = BinaryPrimitives.ReadUInt64LittleEndian(inodeData.Slice(32, 8));
        return raw <= long.MaxValue ? (long)raw : long.MaxValue;
    }

    private static ApfsResourceForkMaps BuildResourceForkMaps(
        byte[] nodeBuffer,
        IReadOnlyList<NodeSlot> slots,
        IReadOnlyDictionary<ulong, List<ApfsFileExtent>> extentsByChildId)
    {
        var resourceForks = new Dictionary<ulong, ApfsFileReadPlan>();
        var finderInfoByObjectId = new Dictionary<ulong, byte[]>();
        var extendedAttributesByObjectId = new Dictionary<ulong, List<ApfsExtendedAttribute>>();
        var decmpfsByObjectId = new Dictionary<ulong, byte[]>();
        foreach (var slot in slots)
        {
            if (!TryDecodeFsTreeKeyFromNode(nodeBuffer, slot, out var key) || key.Type != FsKeyType.Xattr)
            {
                continue;
            }

            var name = DecodeName(key.NamePayload);
            var isResourceFork = string.Equals(name, "com.apple.ResourceFork", StringComparison.Ordinal);
            var isFinderInfo = string.Equals(name, "com.apple.FinderInfo", StringComparison.Ordinal);
            var isDecmpfs = string.Equals(name, "com.apple.decmpfs", StringComparison.Ordinal);
            var isPreservableXattr = ApfsAppleDouble.IsPreservableExtendedAttributeName(name);
            if (!isResourceFork && !isFinderInfo && !isDecmpfs && !isPreservableXattr)
            {
                continue;
            }

            foreach (var absValOff in EnumerateAbsoluteOffsets(slot.ValueOffset))
            {
                if (absValOff < 0 || absValOff >= nodeBuffer.Length)
                {
                    continue;
                }

                var valLen = slot.ValueLength > 0
                    ? Math.Min((int)slot.ValueLength, nodeBuffer.Length - absValOff)
                    : nodeBuffer.Length - absValOff;
                if (valLen <= 0)
                {
                    continue;
                }

                var inlineXattr = ApfsAppleDouble.TryExtractInlineXattrData(nodeBuffer.AsSpan(absValOff, valLen));
                if (isDecmpfs)
                {
                    if (inlineXattr is { Length: >= ApfsDecmpfs.HeaderLength } decmpfs)
                    {
                        decmpfsByObjectId[key.ObjectId] = decmpfs;
                        break;
                    }
                    continue;
                }

                if (isFinderInfo)
                {
                    if (inlineXattr is { Length: >= 32 } finderInfo)
                    {
                        finderInfoByObjectId[key.ObjectId] = finderInfo.Take(32).ToArray();
                        break;
                    }
                    continue;
                }

                if (!isResourceFork)
                {
                    if (inlineXattr is { Length: > 0 } xattrData)
                    {
                        if (!extendedAttributesByObjectId.TryGetValue(key.ObjectId, out var list))
                        {
                            list = new List<ApfsExtendedAttribute>();
                            extendedAttributesByObjectId[key.ObjectId] = list;
                        }

                        list.Add(new ApfsExtendedAttribute(name, xattrData));
                        break;
                    }

                    continue;
                }

                if (inlineXattr is not { Length: > 0 } resourceFork)
                {
                    if (TryBuildExtentBackedResourceForkPlan(
                        key.ObjectId,
                        nodeBuffer.AsSpan(absValOff, valLen),
                        extentsByChildId) is { TotalSize: > 0 } extentBackedPlan)
                    {
                        resourceForks[key.ObjectId] = extentBackedPlan;
                        break;
                    }
                    continue;
                }

                resourceForks[key.ObjectId] = new ApfsFileReadPlan(
                    resourceFork.Length,
                    Array.Empty<ApfsFileExtent>(),
                    resourceFork,
                    IsCompressed: false);
                break;
            }
        }

        var map = new Dictionary<ulong, ApfsFileReadPlan>();
        foreach (var objectId in resourceForks.Keys.Concat(finderInfoByObjectId.Keys).Concat(extendedAttributesByObjectId.Keys).Distinct())
        {
            resourceForks.TryGetValue(objectId, out var resourceForkPlan);
            finderInfoByObjectId.TryGetValue(objectId, out var finderInfo);
            IReadOnlyList<ApfsExtendedAttribute> extendedAttributes = extendedAttributesByObjectId.TryGetValue(objectId, out var attrs)
                ? attrs
                : Array.Empty<ApfsExtendedAttribute>();
            var sidecar = ApfsAppleDouble.BuildAppleDoubleSidecarReadPlan(resourceForkPlan, finderInfo, extendedAttributes);
            if (sidecar.TotalSize > 0)
            {
                map[objectId] = sidecar;
            }
        }

        return new ApfsResourceForkMaps(resourceForks, map, decmpfsByObjectId);
    }

    private static ApfsFileReadPlan? TryBuildExtentBackedResourceForkPlan(
        ulong ownerObjectId,
        ReadOnlySpan<byte> xattrValue,
        IReadOnlyDictionary<ulong, List<ApfsFileExtent>> extentsByChildId)
    {
        if (xattrValue.Length < 16)
        {
            return null;
        }

        var candidateObjectIds = new List<ulong>();
        for (var i = 0; i + 8 <= xattrValue.Length; i += 8)
        {
            var candidate = BinaryPrimitives.ReadUInt64LittleEndian(xattrValue.Slice(i, 8));
            if (candidate != 0 && candidate != ownerObjectId && extentsByChildId.ContainsKey(candidate))
            {
                candidateObjectIds.Add(candidate);
            }
        }

        foreach (var candidateObjectId in candidateObjectIds.Distinct())
        {
            if (!extentsByChildId.TryGetValue(candidateObjectId, out var extents) || extents.Count == 0)
            {
                continue;
            }

            var ordered = extents.OrderBy(x => x.LogicalOffset).ToArray();
            var logicalSize = TryExtractResourceForkDstreamSize(xattrValue)
                ?? ApfsRawFileSystemProvider.GetExtentBackedLogicalSize(ordered, null, null, isCompressed: false);
            if (logicalSize <= 0)
            {
                continue;
            }

            return new ApfsFileReadPlan(logicalSize, ordered);
        }

        return null;
    }

    private static long? TryExtractResourceForkDstreamSize(ReadOnlySpan<byte> xattrValue)
    {
        if (xattrValue.Length < 8)
        {
            return null;
        }

        var start = 0;
        if (xattrValue.Length >= 4)
        {
            var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(xattrValue[2..4]);
            if (declaredLength > 0 && 4 + declaredLength <= xattrValue.Length)
            {
                start = 4;
            }
        }

        long? best = null;
        for (var i = start; i + 8 <= xattrValue.Length; i += 8)
        {
            var raw = BinaryPrimitives.ReadUInt64LittleEndian(xattrValue.Slice(i, 8));
            if (raw == 0 || raw > int.MaxValue)
            {
                continue;
            }

            var value = (long)raw;
            if (!best.HasValue || value > best.Value)
            {
                best = value;
            }
        }

        return best;
    }

    private static ushort? TryExtractModeFromValue(ReadOnlySpan<byte> value)
    {
        for (var i = 0; i + 2 <= value.Length; i += 2)
        {
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(value[i..(i + 2)]);
            var kind = mode & 0xF000;
            if (kind is 0x4000 or 0x8000 or 0xA000)
            {
                return mode;
            }
        }
        return null;
    }

    private static bool TryDecodeDrecValue(ReadOnlySpan<byte> value, out DrecValue decoded)
    {
        // Common drec layouts store child id and a small type discriminator near the start.
        // We try several candidate offsets and validate with sane ranges.
        decoded = default;
        var childIdOffsets = new[] { 0, 8, 16, 24 };
        var typeOffsets = new[] { 8, 12, 16, 20, 24, 28 };

        foreach (var childOff in childIdOffsets)
        {
            if (childOff + 8 > value.Length) continue;
            var childId = BinaryPrimitives.ReadUInt64LittleEndian(value[childOff..(childOff + 8)]);
            if (childId == 0) continue;

            foreach (var typeOff in typeOffsets)
            {
                if (typeOff + 4 > value.Length) continue;
                var t = BinaryPrimitives.ReadUInt32LittleEndian(value[typeOff..(typeOff + 4)]);
                var ft = t switch
                {
                    1 => DrecFileType.Regular,
                    2 => DrecFileType.Directory,
                    4 => DrecFileType.Symlink,
                    8 => DrecFileType.Other,
                    _ => DrecFileType.Unknown
                };
                if (ft == DrecFileType.Unknown) continue;

                decoded = new DrecValue(childId, ft);
                return true;
            }
        }

        return false;
    }

    private static bool TryDecodeFsTreeKeyFromNode(byte[] nodeBuffer, NodeSlot slot, out DecodedFsKey decoded)
    {
        foreach (var absKeyOff in EnumerateAbsoluteOffsets(slot.KeyOffset))
        {
            if (absKeyOff < 0 || absKeyOff + 8 > nodeBuffer.Length)
            {
                continue;
            }
            var keyLen = slot.KeyLength;
            if (keyLen < 9 || absKeyOff + keyLen > nodeBuffer.Length)
            {
                continue;
            }
            if (TryDecodeFsTreeKey(nodeBuffer.AsSpan(absKeyOff, keyLen), out decoded))
            {
                return true;
            }
        }
        decoded = default;
        return false;
    }

    private static bool TryDecodeFsTreeKey(ReadOnlySpan<byte> key, out DecodedFsKey decoded)
    {
        decoded = default;
        if (key.Length < 9)
        {
            return false;
        }

        var objTypeWord = BinaryPrimitives.ReadUInt64LittleEndian(key[..8]);
        // APFS object-id/type packing differs by key family. Support two common layouts:
        // layout A: low nibble = type, upper bits = object id
        // layout B: high nibble = type, lower bits = object id
        var typeA = (byte)(objTypeWord & 0xF);
        var objA = objTypeWord >> 4;
        var typeB = (byte)((objTypeWord >> 60) & 0xF);
        var objB = objTypeWord & 0x0FFF_FFFF_FFFF_FFFFUL;

        var fsTypeA = MapFsKeyType(typeA);
        var fsTypeB = MapFsKeyType(typeB);

        // Prefer drec-capable mapping when available.
        if (fsTypeA != FsKeyType.Unknown)
        {
            decoded = new DecodedFsKey(fsTypeA, objA, key[8..].ToArray());
            return true;
        }
        if (fsTypeB != FsKeyType.Unknown)
        {
            decoded = new DecodedFsKey(fsTypeB, objB, key[8..].ToArray());
            return true;
        }

        return false;
    }

    private static FsKeyType MapFsKeyType(byte t)
    {
        // Conservative mapping to keep false positives low.
        return t switch
        {
            3 => FsKeyType.Inode,
            4 => FsKeyType.Xattr,
            8 => FsKeyType.FileExtent,
            9 => FsKeyType.DirRecord,
            _ => FsKeyType.Unknown
        };
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        var trimmed = bytes;
        while (trimmed.Length > 0 && (trimmed[^1] == 0 || trimmed[^1] == (byte)'\n' || trimmed[^1] == (byte)'\r'))
        {
            trimmed = trimmed[..^1];
        }
        if (trimmed.Length < 1 || trimmed.Length > 255)
        {
            return string.Empty;
        }

        try
        {
            var name = Encoding.UTF8.GetString(trimmed);
            name = name.Trim();
            if (name.Length is < 1 or > 255)
            {
                return string.Empty;
            }
            foreach (var c in name)
            {
                if (char.IsControl(c))
                {
                    return string.Empty;
                }
            }
            if (name.Contains('\\') || name.Contains('/') || name.Contains(':'))
            {
                return string.Empty;
            }
            return name;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ulong? TryReadChildPointerFromSlot(byte[] nodeBuffer, NodeSlot slot, ulong blockCount)
    {
        foreach (var absValOff in EnumerateAbsoluteOffsets(slot.ValueOffset))
        {
            if (absValOff < 0 || absValOff + 8 > nodeBuffer.Length)
            {
                continue;
            }
            var paddr = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absValOff, 8));
            if (paddr > 0 && paddr < blockCount)
            {
                return paddr;
            }
        }
        return null;
    }

    private static HashSet<ulong> ExtractLikelyChildBlockPointers(byte[] nodeBuffer, ulong blockCount)
    {
        var result = new HashSet<ulong>();

        if (TryDecodeStructuredNodeSlots(nodeBuffer, out var structuredSlots, out _, out var isLeaf) && !isLeaf)
        {
            foreach (var slot in structuredSlots)
            {
                foreach (var absValOff in EnumerateAbsoluteOffsets(slot.ValueOffset))
                {
                    if (absValOff < 0 || absValOff + 8 > nodeBuffer.Length)
                    {
                        continue;
                    }
                    var paddr = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absValOff, 8));
                    if (paddr == 0 || paddr >= blockCount)
                    {
                        continue;
                    }
                    result.Add(paddr);
                    if (result.Count >= MaxOmapCandidateBlocks)
                    {
                        return result;
                    }
                }
            }
        }

        // Fast raw scan for values that look like block pointers.
        for (var i = 64; i + 8 <= nodeBuffer.Length; i += 8)
        {
            var candidate = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(i, 8));
            if (candidate == 0 || candidate >= blockCount)
            {
                continue;
            }

            result.Add(candidate);
            if (result.Count >= MaxOmapCandidateBlocks)
            {
                break;
            }
        }

        // Structured slot decode: treat small vlen entries as possible child paddr values.
        var tableStarts = new[] { 48, 56, 64, 72, 80, 96, 112, 128 };
        var counts = new[] { 8, 16, 24, 32, 48, 64 };
        foreach (var tableStart in tableStarts)
        {
            foreach (var count in counts)
            {
                if (tableStart + count * 8 > nodeBuffer.Length)
                {
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    var slot = tableStart + i * 8;
                    var vOff = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slot + 4, 2));
                    var vLen = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slot + 6, 2));
                    if (!(vLen == 8 || vLen == 16))
                    {
                        continue;
                    }
                    if (vOff + 8 > nodeBuffer.Length)
                    {
                        continue;
                    }

                    var paddr = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(vOff, 8));
                    if (paddr == 0 || paddr >= blockCount)
                    {
                        continue;
                    }

                    result.Add(paddr);
                    if (result.Count >= MaxOmapCandidateBlocks)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }

    private async Task<byte[]?> ReadBlockBufferAsync(ulong blockNumber, uint blockSize, CancellationToken cancellationToken)
    {
        var offset = checked(_partitionOffsetBytes + (long)(blockNumber * blockSize));
        if (offset < _partitionOffsetBytes || offset >= checked(_partitionOffsetBytes + _partitionLengthBytes))
        {
            return null;
        }

        var buffer = new byte[blockSize];
        var read = await RawReadUtil.ReadExactlyAtAsync(_device, offset, buffer, buffer.Length, cancellationToken).ConfigureAwait(false);
        return read < 128 ? null : buffer;
    }

    private static List<ApfsResolvedObjectPointer> DecodeOmapNodeTableCandidates(
        byte[] nodeBuffer,
        ulong targetOid,
        ulong maxXid,
        ulong blockCount)
    {
        var results = new List<ApfsResolvedObjectPointer>();
        if (nodeBuffer.Length < 256)
        {
            return results;
        }

        if (TryDecodeStructuredNodeSlots(nodeBuffer, out var structuredSlots, out _, out var isLeaf) && isLeaf)
        {
            foreach (var slot in structuredSlots)
            {
                foreach (var absKeyOff in EnumerateAbsoluteOffsets(slot.KeyOffset))
                {
                    if (absKeyOff < 0 || absKeyOff + 16 > nodeBuffer.Length)
                    {
                        continue;
                    }
                    var oid = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absKeyOff, 8));
                    if (oid != targetOid)
                    {
                        continue;
                    }
                    var xid = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absKeyOff + 8, 8));
                    if (xid == 0 || xid > maxXid)
                    {
                        continue;
                    }

                    foreach (var absValOff in EnumerateAbsoluteOffsets(slot.ValueOffset))
                    {
                        if (absValOff < 0 || absValOff + 16 > nodeBuffer.Length)
                        {
                            continue;
                        }
                        var flags = BinaryPrimitives.ReadUInt32LittleEndian(nodeBuffer.AsSpan(absValOff, 4));
                        var size = BinaryPrimitives.ReadUInt32LittleEndian(nodeBuffer.AsSpan(absValOff + 4, 4));
                        var paddr = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(absValOff + 8, 8));
                        if (paddr == 0 || paddr >= blockCount)
                        {
                            continue;
                        }
                        if (size == 0 || size > nodeBuffer.Length * 4)
                        {
                            continue;
                        }

                        results.Add(new ApfsResolvedObjectPointer(
                            ObjectId: oid,
                            TransactionId: xid,
                            PhysicalBlockNumber: paddr,
                            LogicalSize: size,
                            Flags: flags));
                    }
                }
            }
            if (results.Count > 0)
            {
                return results;
            }
        }

        // Attempt a few plausible table start/count windows used by APFS node layouts.
        // Each slot is interpreted as: [koff:u16][klen:u16][voff:u16][vlen:u16].
        var tableStarts = new[] { 48, 56, 64, 72, 80, 96, 112, 128 };
        var counts = new[] { 8, 16, 24, 32, 48, 64, 96, 128 };

        foreach (var tableStart in tableStarts)
        {
            foreach (var count in counts)
            {
                var tableBytes = count * 8;
                if (tableStart + tableBytes > nodeBuffer.Length)
                {
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    var slot = tableStart + i * 8;
                    var kOff = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slot + 0, 2));
                    var kLen = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slot + 2, 2));
                    var vOff = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slot + 4, 2));
                    var vLen = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slot + 6, 2));

                    if (kLen < 16 || kLen > 64 || vLen < 16 || vLen > 64)
                    {
                        continue;
                    }
                    if (kOff + kLen > nodeBuffer.Length || vOff + vLen > nodeBuffer.Length)
                    {
                        continue;
                    }

                    var oid = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(kOff, 8));
                    if (oid != targetOid)
                    {
                        continue;
                    }

                    var xid = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(kOff + 8, 8));
                    if (xid == 0 || xid > maxXid)
                    {
                        continue;
                    }

                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(nodeBuffer.AsSpan(vOff, 4));
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(nodeBuffer.AsSpan(vOff + 4, 4));
                    var paddr = BinaryPrimitives.ReadUInt64LittleEndian(nodeBuffer.AsSpan(vOff + 8, 8));
                    if (paddr == 0 || paddr >= blockCount)
                    {
                        continue;
                    }
                    if (size == 0 || size > nodeBuffer.Length * 4)
                    {
                        continue;
                    }

                    results.Add(new ApfsResolvedObjectPointer(
                        ObjectId: oid,
                        TransactionId: xid,
                        PhysicalBlockNumber: paddr,
                        LogicalSize: size,
                        Flags: flags));
                }
            }
        }

        return results;
    }

    private static IEnumerable<int> EnumerateAbsoluteOffsets(ushort rawOffset)
    {
        // APFS slot offsets can be absolute in-node or relative to region bases depending on node format.
        // Try multiple common interpretations.
        yield return rawOffset;
        yield return 32 + rawOffset;
        yield return 64 + rawOffset;
    }

    private static bool TryDecodeStructuredNodeSlots(
        byte[] nodeBuffer,
        out List<NodeSlot> slots,
        out ushort level,
        out bool isLeaf)
    {
        slots = new List<NodeSlot>();
        level = 0;
        isLeaf = false;

        // Candidate btree node header anchored at object-body start (+32).
        // Layout guess:
        // +0 flags(u16), +2 level(u16), +4 nkeys(u32), +8 tableOff(u16), +10 tableLen(u16)
        const int bodyBase = 32;
        if (nodeBuffer.Length < bodyBase + 16)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(bodyBase + 0, 2));
        level = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(bodyBase + 2, 2));
        var nkeys = BinaryPrimitives.ReadUInt32LittleEndian(nodeBuffer.AsSpan(bodyBase + 4, 4));
        var tableOff = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(bodyBase + 8, 2));
        var tableLen = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(bodyBase + 10, 2));

        if (nkeys == 0 || nkeys > MaxStructuredNodeKeys)
        {
            return false;
        }
        if (tableLen < nkeys * 8 || tableLen > nodeBuffer.Length)
        {
            return false;
        }

        // Table can be absolute or relative to body base.
        var tableAbsCandidates = new[] { (int)tableOff, bodyBase + tableOff };
        foreach (var tableAbs in tableAbsCandidates)
        {
            if (tableAbs < 0 || tableAbs + (int)(nkeys * 8) > nodeBuffer.Length)
            {
                continue;
            }

            var parsed = new List<NodeSlot>((int)nkeys);
            var valid = true;
            for (var i = 0; i < nkeys; i++)
            {
                var slotPos = tableAbs + i * 8;
                var keyOff = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slotPos + 0, 2));
                var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slotPos + 2, 2));
                var valOff = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slotPos + 4, 2));
                var valLen = BinaryPrimitives.ReadUInt16LittleEndian(nodeBuffer.AsSpan(slotPos + 6, 2));
                if (keyLen == 0 || keyLen > 256 || valLen > 256)
                {
                    valid = false;
                    break;
                }
                parsed.Add(new NodeSlot(keyOff, keyLen, valOff, valLen));
            }

            if (!valid || parsed.Count == 0)
            {
                continue;
            }

            slots = parsed;
            isLeaf = level == 0 || (flags & 0x0001) != 0;
            return true;
        }

        return false;
    }

    private async Task<ApfsVolumeSuperblockHint?> TryReadVolumeSuperblockHintAsync(ulong blockNumber, uint blockSize, CancellationToken cancellationToken)
    {
        var offset = checked(_partitionOffsetBytes + (long)(blockNumber * blockSize));
        if (offset < _partitionOffsetBytes || offset >= checked(_partitionOffsetBytes + _partitionLengthBytes))
        {
            return null;
        }

        var buffer = new byte[blockSize];
        var read = await RawReadUtil.ReadExactlyAtAsync(_device, offset, buffer, buffer.Length, cancellationToken).ConfigureAwait(false);
        if (read < 64)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(32, 4));
        if (magic != ApsbMagic)
        {
            return null;
        }

        var oid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
        var xid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(16, 8));
        if (oid == 0)
        {
            return null;
        }

        var volumeName = TryReadUtf8NullTerminatedString(buffer, ApfsVolumeNameOffset, ApfsVolumeNameLength) ?? $"Volume_{oid:X}";
        var role = buffer.Length >= ApfsRoleOffset + 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(ApfsRoleOffset, 2))
            : (ushort)0;
        var fsFlags = buffer.Length >= ApfsFsFlagsOffset + 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(ApfsFsFlagsOffset, 8))
            : 0UL;

        return new ApfsVolumeSuperblockHint(blockNumber, oid, xid, volumeName, role, fsFlags);
    }

    private static string? TryReadUtf8NullTerminatedString(byte[] buffer, int offset, int maxLength)
    {
        if (offset < 0 || maxLength <= 0 || buffer.Length < offset + 1)
        {
            return null;
        }

        var available = Math.Min(maxLength, buffer.Length - offset);
        var slice = buffer.AsSpan(offset, available);
        var zeroIndex = slice.IndexOf((byte)0);
        if (zeroIndex >= 0)
        {
            slice = slice[..zeroIndex];
        }

        if (slice.IsEmpty)
        {
            return null;
        }

        var value = Encoding.UTF8.GetString(slice).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record NxSuperblock(
        uint BlockSize,
        ulong BlockCount,
        ulong TransactionId,
        ulong CheckpointDescriptorBase,
        uint CheckpointDescriptorBlocks,
        ulong CheckpointDataBase,
        uint CheckpointDataBlocks,
        ulong SpacemanOid,   // nx_spaceman_oid @ offset 0x98 — OID of the space manager object
        ulong OmapOid,       // nx_omap_oid @ offset 0xA0 — OID of the container object map
        IReadOnlyList<ulong> VolumeObjectIds
    );

    private sealed record ApfsObjectPointer(
        ulong BlockNumber,
        ulong ObjectId,
        ulong TransactionId,
        uint Type,
        uint Subtype
    );

    private sealed record NodeSlot(
        ushort KeyOffset,
        ushort KeyLength,
        ushort ValueOffset,
        ushort ValueLength
    );

    private readonly record struct OmapKey(ulong Oid, ulong Xid);

    private readonly record struct FsTreeRecord(FsKeyType KeyType, ulong ParentId, string Name, bool IsDirectory, ulong? ChildId, bool IsSymbolicLink);

    private readonly record struct FsTreePreviewItem(FsKeyType KeyType, ulong ParentId, string Name, bool IsDirectory, ulong? ChildId, bool IsSymbolicLink);

    private sealed record ApfsVolumeTraversalResult(
        IReadOnlyList<ApfsPreviewEntry> Entries,
        IReadOnlyDictionary<string, ApfsFileReadPlan> RootFilePlansByName,
        ulong RootDirectoryId,
        IReadOnlyDictionary<ulong, IReadOnlyList<ApfsCatalogEntry>> DirectoryEntriesByParentId,
        IReadOnlyDictionary<ulong, ApfsFileReadPlan> FilePlansByObjectId,
        IReadOnlyDictionary<ulong, ApfsFileReadPlan> ResourceForkAppleDoubleByObjectId
    );

    private sealed record ApfsResourceForkMaps(
        IReadOnlyDictionary<ulong, ApfsFileReadPlan> ResourceForkByObjectId,
        IReadOnlyDictionary<ulong, ApfsFileReadPlan> AppleDoubleByObjectId,
        IReadOnlyDictionary<ulong, byte[]> DecmpfsByObjectId
    );

    private readonly record struct DecodedFsKey(FsKeyType Type, ulong ObjectId, byte[] NamePayload);

    private enum FsKeyType : byte
    {
        Unknown = 0,
        Inode = 1,
        Xattr = 2,
        FileExtent = 3,
        DirRecord = 4
    }

    private readonly record struct DrecValue(ulong ChildId, DrecFileType FileType);


    private enum DrecFileType : byte
    {
        Unknown = 0,
        Regular = 1,
        Directory = 2,
        Symlink = 3,
        Other = 4
    }
}
