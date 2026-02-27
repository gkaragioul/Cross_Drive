using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MacMount.RawDiskEngine;

internal sealed class ApfsRawFileSystemProvider : IRawFileSystemProvider
{
    private const int MaxVolumeHintsForView = 64;
    private const int MaxPreviewEntriesPerVolume = 48;
    private readonly IRawBlockDevice _device;
    private readonly uint _blockSize;
    private readonly long _partitionOffsetBytes;
    private readonly Dictionary<string, RawFsEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<RawFsEntry>> _dirChildren = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApfsFileReadPlan> _fileReadPlans = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Offset, byte[] Data)> _readCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _infoBytes;

    private ApfsRawFileSystemProvider(IRawBlockDevice device, ApfsContainerSummary summary, MountPlan plan)
    {
        _device = device;
        _blockSize = summary.BlockSize;
        _partitionOffsetBytes = Math.Max(0, plan.PartitionOffsetBytes);
        FileSystemType = "APFS";
        TotalBytes = summary.EstimatedTotalBytes > 0 ? summary.EstimatedTotalBytes : Math.Max(1, plan.TotalBytes);
        FreeBytes = 0;

        var now = DateTimeOffset.UtcNow;
        var infoText = BuildInfoText(summary, plan);
        _infoBytes = Encoding.UTF8.GetBytes(infoText);

        var root = new RawFsEntry("\\", "ROOT", true, 0, now, FileAttributes.Directory);
        var volumesDir = new RawFsEntry("\\Volumes", "Volumes", true, 0, now, FileAttributes.Directory);
        var infoFile = new RawFsEntry("\\APFS_CONTAINER_INFO.txt", "APFS_CONTAINER_INFO.txt", false, _infoBytes.Length, now, FileAttributes.ReadOnly);

        _entries[root.Path] = root;
        _entries[volumesDir.Path] = volumesDir;
        _entries[infoFile.Path] = infoFile;

        var rootChildren = new List<RawFsEntry> { volumesDir, infoFile };
        var volumeChildren = new List<RawFsEntry>();
        foreach (var oid in summary.VolumeObjectIds)
        {
            var name = $"Volume_{oid:X}";
            var path = $"\\Volumes\\{name}";
            var entry = new RawFsEntry(path, name, true, 0, now, FileAttributes.Directory);
            _entries[path] = entry;
            volumeChildren.Add(entry);

            if (summary.VolumePreviewsByOid.TryGetValue(oid, out var preview))
            {
                var previewChildren = new List<RawFsEntry>();
                foreach (var item in preview.RootEntries.Take(MaxPreviewEntriesPerVolume))
                {
                    var childPath = $"{path}\\{item.Name}";
                    var attrs = item.IsDirectory ? FileAttributes.Directory : FileAttributes.ReadOnly;
                    long size = 0;
                    if (!item.IsDirectory &&
                        preview.RootFilePlansByName.TryGetValue(item.Name, out var planForFile))
                    {
                        _fileReadPlans[childPath] = planForFile;
                        size = Math.Max(0, planForFile.TotalSize);
                    }
                    var child = new RawFsEntry(childPath, item.Name, item.IsDirectory, size, now, attrs);
                    _entries[childPath] = child;
                    previewChildren.Add(child);
                }
                _dirChildren[path] = previewChildren;
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

    public static async Task<ApfsRawFileSystemProvider> CreateAsync(MountPlan plan, CancellationToken cancellationToken = default)
    {
        var basePath = plan.PhysicalDrivePath;
        var hashIdx = basePath.IndexOf("#part", StringComparison.OrdinalIgnoreCase);
        if (hashIdx > 0)
        {
            basePath = basePath[..hashIdx];
        }

        var device = await new WindowsRawBlockDeviceFactory().OpenReadOnlyAsync(basePath, cancellationToken).ConfigureAwait(false);
        try
        {
            var reader = new ApfsMetadataReader(device, plan.PartitionOffsetBytes, plan.PartitionLengthBytes);
            var summary = await reader.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
            return new ApfsRawFileSystemProvider(device, summary, plan);
        }
        catch
        {
            device.Dispose();
            throw;
        }
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

        if (destination.Length <= 131072 &&
            _readCache.TryGetValue(n, out var cached) &&
            offset >= cached.Offset &&
            offset + destination.Length <= cached.Offset + cached.Data.Length)
        {
            var src = (int)(offset - cached.Offset);
            cached.Data.AsSpan(src, destination.Length).CopyTo(destination);
            return destination.Length;
        }

        var written = 0;
        var targetStart = offset;
        var targetEnd = offset + destination.Length;

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
            var deviceOffset = checked(_partitionOffsetBytes + (long)(extent.PhysicalBlockNumber * _blockSize) + srcOffInExtent);

            var temp = new byte[bytesToRead];
            var read = _device.ReadAsync(deviceOffset, temp, bytesToRead).AsTask().GetAwaiter().GetResult();
            if (read <= 0)
            {
                continue;
            }

            temp.AsSpan(0, read).CopyTo(destination.Slice(dstOff, read));
            written += read;
            if (read < bytesToRead)
            {
                break;
            }
        }

        if (written > 0 && written <= 131072)
        {
            var snap = destination.Slice(0, written).ToArray();
            _readCache[n] = (offset, snap);
        }

        return written;
    }

    public void Dispose()
    {
        try { _device.Dispose(); } catch { }
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
        sb.AppendLine("MacMount APFS Native Metadata");
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
        sb.AppendLine($"ObjectMapOid: {summary.ObjectMapOid}");
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
            sb.AppendLine($"VolumePreview: oid=0x{kv.Key:X} rootTreeOid={(v.RootTreeOid?.ToString() ?? "n/a")} rootTreeBlock={(v.RootTreeBlock?.ToString() ?? "n/a")} entries={v.RootEntries.Count}");
        }
        sb.AppendLine($"VolumeSuperblockHintsCount: {summary.VolumeSuperblockHints.Count}");
        foreach (var hint in summary.VolumeSuperblockHints.Take(MaxVolumeHintsForView))
        {
            sb.AppendLine($"VolumeHint: block={hint.BlockNumber} oid=0x{hint.ObjectId:X} xid={hint.TransactionId}");
        }
        sb.AppendLine("Status: APFS object map/tree traversal is in progress.");
        return sb.ToString();
    }
}

internal sealed record ApfsContainerSummary(
    uint BlockSize,
    ulong BlockCount,
    ulong TransactionId,
    ulong CheckpointDescriptorBase,
    uint CheckpointDescriptorBlocks,
    ulong CheckpointDataBase,
    uint CheckpointDataBlocks,
    ulong ObjectMapOid,
    ulong? ObjectMapBlockNumber,
    ulong? ObjectMapTreeOid,
    ulong? ObjectMapTreeBlockNumber,
    int IndexedObjectCount,
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
    ulong TransactionId
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
    ulong? RootTreeOid,
    ulong? RootTreeBlock,
    IReadOnlyList<ApfsPreviewEntry> RootEntries,
    IReadOnlyDictionary<string, ApfsFileReadPlan> RootFilePlansByName
);

internal sealed record ApfsPreviewEntry(
    string Name,
    bool IsDirectory
);

internal sealed record ApfsFileReadPlan(
    long TotalSize,
    IReadOnlyList<ApfsFileExtent> Extents
);

internal sealed record ApfsFileExtent(
    long LogicalOffset,
    long Length,
    ulong PhysicalBlockNumber
);

internal sealed class ApfsMetadataReader
{
    private const uint NxsbMagic = 0x4253584E; // "NXSB" little-endian
    private const uint ApsbMagic = 0x42535041; // "APSB" little-endian
    private const int MaxHintScanBlocks = 8192;
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
        objectIndex.TryGetValue(best.ObjectMapOid, out var omapPtr);
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
            ObjectMapOid: best.ObjectMapOid,
            ObjectMapBlockNumber: omapPtr?.BlockNumber,
            ObjectMapTreeOid: omapTreeOid,
            ObjectMapTreeBlockNumber: omapTreePtr?.BlockNumber,
            IndexedObjectCount: objectIndex.Count,
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

        var descBase = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(112, 8));
        var descBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(120, 4));
        var dataBase = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(128, 8));
        var dataBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(136, 4));
        var omapOid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(152, 8));

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
            ObjectMapOid: omapOid,
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
        yield return (0, (ulong)Math.Min((ulong)MaxHintScanBlocks, nx.BlockCount));
        if (nx.CheckpointDescriptorBlocks > 0)
        {
            yield return (nx.CheckpointDescriptorBase, Math.Min((ulong)nx.CheckpointDescriptorBlocks, (ulong)MaxHintScanBlocks));
        }
        if (nx.CheckpointDataBlocks > 0)
        {
            yield return (nx.CheckpointDataBase, Math.Min((ulong)nx.CheckpointDataBlocks, (ulong)MaxHintScanBlocks));
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

        var rootTreeOid = TryExtractVolumeRootTreeOid(volumeBuffer, objectIndex);
        ulong? rootTreeBlock = null;
        if (rootTreeOid.HasValue && objectIndex.TryGetValue(rootTreeOid.Value, out var rootTreePtr))
        {
            rootTreeBlock = rootTreePtr.BlockNumber;
        }

        var rootEntries = new List<ApfsPreviewEntry>();
        var rootFilePlansByName = new Dictionary<string, ApfsFileReadPlan>(StringComparer.OrdinalIgnoreCase);
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
        }

        return new ApfsVolumePreview(
            VolumeObjectId: resolvedVolumePointer.ObjectId,
            RootTreeOid: rootTreeOid,
            RootTreeBlock: rootTreeBlock,
            RootEntries: rootEntries,
            RootFilePlansByName: rootFilePlansByName
        );
    }

    private async Task<ApfsVolumeTraversalResult> TraverseFsTreePreviewEntriesAsync(
        ulong rootTreeBlock,
        uint blockSize,
        ulong blockCount,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        var results = new List<ApfsPreviewEntry>(Math.Max(8, maxEntries));
        var rootFilePlansByName = new Dictionary<string, ApfsFileReadPlan>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var childIdToName = new Dictionary<ulong, string>();
        var childIdToIsDir = new Dictionary<ulong, bool>();
        var extentsByChildId = new Dictionary<ulong, List<ApfsFileExtent>>();
        var visitedBlocks = new HashSet<ulong>();
        var queue = new Queue<(ulong Block, int Depth)>();
        queue.Enqueue((rootTreeBlock, 0));

        const int maxDepth = 2;
        const int maxVisitedBlocks = 192;

        while (queue.Count > 0 && results.Count < maxEntries && visitedBlocks.Count < maxVisitedBlocks)
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

            if (TryDecodeStructuredNodeSlots(node, out _, out _, out var isLeaf) && isLeaf)
            {
                var entries = TryExtractFsTreePreviewEntries(node);
                if (entries.Count == 0)
                {
                    var names = ExtractLikelyApfsNames(node);
                    foreach (var name in names)
                    {
                        if (results.Count >= maxEntries) break;
                        if (string.IsNullOrWhiteSpace(name) || !seenNames.Add(name)) continue;
                        results.Add(new ApfsPreviewEntry(name, IsDirectory: false));
                    }
                }
                else
                {
                    foreach (var e in entries)
                    {
                        if (results.Count >= maxEntries) break;
                        if (!seenNames.Add(e.Name)) continue;
                        results.Add(new ApfsPreviewEntry(e.Name, e.IsDirectory));
                        if (e.ChildId.HasValue)
                        {
                            childIdToName[e.ChildId.Value] = e.Name;
                            childIdToIsDir[e.ChildId.Value] = e.IsDirectory;
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

        foreach (var kv in extentsByChildId)
        {
            if (!childIdToName.TryGetValue(kv.Key, out var name))
            {
                continue;
            }
            if (childIdToIsDir.TryGetValue(kv.Key, out var isDir) && isDir)
            {
                continue;
            }

            var ordered = kv.Value.OrderBy(x => x.LogicalOffset).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }
            var totalSize = ordered.Max(x => x.LogicalOffset + x.Length);
            if (totalSize <= 0)
            {
                continue;
            }
            rootFilePlansByName[name] = new ApfsFileReadPlan(totalSize, ordered);
        }

        return new ApfsVolumeTraversalResult(results, rootFilePlansByName);
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
            if (!seen.Add(record.Name))
            {
                continue;
            }

            output.Add(new FsTreePreviewItem(record.Name, record.IsDirectory, record.ChildId));
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
                }

                if (!isDir)
                {
                    for (var i = 0; i + 4 <= limit; i += 2)
                    {
                        var v = BinaryPrimitives.ReadUInt32LittleEndian(nodeBuffer.AsSpan(absValOff + i, 4));
                        // Common "directory" discriminator values seen in drec-like payloads.
                        if (v is 2 or 4 or 8)
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
                }
                else if ((mode & 0xF000) == 0x8000)
                {
                    isDir = false;
                }
            }

            record = new FsTreeRecord(name, isDir, childId);
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

        return new ApfsVolumeSuperblockHint(blockNumber, oid, xid);
    }

    private sealed record NxSuperblock(
        uint BlockSize,
        ulong BlockCount,
        ulong TransactionId,
        ulong CheckpointDescriptorBase,
        uint CheckpointDescriptorBlocks,
        ulong CheckpointDataBase,
        uint CheckpointDataBlocks,
        ulong ObjectMapOid,
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

    private readonly record struct FsTreeRecord(string Name, bool IsDirectory, ulong? ChildId);

    private readonly record struct FsTreePreviewItem(string Name, bool IsDirectory, ulong? ChildId);

    private sealed record ApfsVolumeTraversalResult(
        IReadOnlyList<ApfsPreviewEntry> Entries,
        IReadOnlyDictionary<string, ApfsFileReadPlan> RootFilePlansByName
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
