using System.Buffers.Binary;
using System.Text;

namespace CrossDrive.RawDiskEngine;

internal sealed class HfsClassicRawFileSystemProvider : IRawFileSystemProvider
{
    private const int SectorSize = 512;
    private const int MdbOffset = 1024;
    private const ushort HfsSignature = 0x4244; // "BD"
    private const uint RootFolderId = 2;
    private const int AppleDoubleHeaderBaseLength = 26;
    private const int AppleDoubleEntryLength = 12;
    private const int AppleDoubleFinderInfoLength = 32;
    private const uint AppleDoubleResourceForkEntryId = 2;
    private const uint AppleDoubleFinderInfoEntryId = 9;

    private readonly IRawBlockDevice _device;
    private readonly bool _ownsDevice;
    private readonly HfsClassicVolumeInfo _volume;
    private readonly List<CatalogExtentRun> _catalogRuns;
    private readonly List<CatalogExtentRun> _extentsOverflowRuns;
    private readonly Dictionary<uint, List<OverflowExtentRecord>> _dataOverflowExtentsByFileId = new();
    private readonly Dictionary<uint, List<OverflowExtentRecord>> _resourceOverflowExtentsByFileId = new();
    private readonly Dictionary<string, RawFsEntry> _entryByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HfsClassicCatalogItem> _itemByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HfsClassicCatalogItem> _appleDoubleByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _folderIdByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, List<RawFsEntry>> _childrenByFolderId = new();

    private HfsClassicRawFileSystemProvider(
        IRawBlockDevice device,
        bool ownsDevice,
        HfsClassicVolumeInfo volume,
        List<CatalogExtentRun> catalogRuns,
        List<CatalogExtentRun> extentsOverflowRuns)
    {
        _device = device;
        _ownsDevice = ownsDevice;
        _volume = volume;
        _catalogRuns = catalogRuns;
        _extentsOverflowRuns = extentsOverflowRuns;
        FileSystemType = "HFS";
        TotalBytes = (long)volume.AllocationBlockCount * volume.AllocationBlockSize;
        FreeBytes = (long)volume.FreeAllocationBlocks * volume.AllocationBlockSize;
    }

    public string FileSystemType { get; }
    public long TotalBytes { get; }
    public long FreeBytes { get; }

    public static async Task<HfsClassicRawFileSystemProvider> CreateAsync(MountPlan plan, CancellationToken ct = default)
    {
        var basePath = plan.PhysicalDrivePath;
        var hashIdx = basePath.IndexOf('#');
        if (hashIdx > 0)
        {
            basePath = basePath[..hashIdx];
        }

        var factory = new WindowsRawBlockDeviceFactory();
        var device = await factory.OpenReadOnlyAsync(basePath, ct).ConfigureAwait(false);
        try
        {
            return await CreateFromDeviceAsync(plan, device, ownsDevice: true, ct).ConfigureAwait(false);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    internal static async Task<HfsClassicRawFileSystemProvider> CreateFromDeviceAsync(
        MountPlan plan,
        IRawBlockDevice device,
        bool ownsDevice = false,
        CancellationToken ct = default)
    {
        var volume = await ReadVolumeInfoAsync(device, Math.Max(0, plan.PartitionOffsetBytes), ct).ConfigureAwait(false);
        var catalogRuns = BuildCatalogRuns(volume);
        if (catalogRuns.Count == 0)
        {
            throw new InvalidDataException("Classic HFS catalog file has no inline extents in the master directory block.");
        }

        var extentsOverflowRuns = BuildExtentRuns(volume, volume.ExtentsOverflowExtents);
        var provider = new HfsClassicRawFileSystemProvider(device, ownsDevice, volume, catalogRuns, extentsOverflowRuns);
        await provider.LoadExtentsOverflowAsync(ct).ConfigureAwait(false);
        await provider.LoadCatalogAsync(ct).ConfigureAwait(false);
        return provider;
    }

    public RawFsEntry? GetEntry(string path)
    {
        var n = NormalizePath(path);
        return _entryByPath.TryGetValue(n, out var entry) ? entry : null;
    }

    public IReadOnlyList<RawFsEntry> ListDirectory(string path)
    {
        var n = NormalizePath(path);
        if (!_folderIdByPath.TryGetValue(n, out var folderId))
        {
            return Array.Empty<RawFsEntry>();
        }

        return _childrenByFolderId.TryGetValue(folderId, out var entries)
            ? entries
            : Array.Empty<RawFsEntry>();
    }

    public int ReadFile(string path, long offset, Span<byte> destination)
    {
        if (offset < 0 || destination.Length == 0) return 0;

        var n = NormalizePath(path);
        if (_appleDoubleByPath.TryGetValue(n, out var appleDoubleItem))
        {
            return ReadAppleDoubleResourceFork(appleDoubleItem, offset, destination);
        }

        if (!_itemByPath.TryGetValue(n, out var item) || item.IsDirectory || item.Size <= offset)
        {
            return 0;
        }

        return ReadFork(item.DataExtents, item.Size, offset, destination);
    }

    private int ReadFork(IReadOnlyList<HfsClassicExtent> extents, long forkSize, long offset, Span<byte> destination)
    {
        if (offset < 0 || destination.Length == 0 || forkSize <= offset) return 0;

        var requested = (int)Math.Min(destination.Length, forkSize - offset);
        var totalRead = 0;
        long logicalCursor = 0;

        foreach (var extent in extents)
        {
            if (extent.BlockCount == 0) continue;

            var extentLength = (long)extent.BlockCount * _volume.AllocationBlockSize;
            var extentEnd = logicalCursor + extentLength;
            if (offset >= extentEnd)
            {
                logicalCursor = extentEnd;
                continue;
            }

            var inExtentOffset = Math.Max(0, offset - logicalCursor);
            var available = extentLength - inExtentOffset;
            var toRead = (int)Math.Min(requested - totalRead, available);
            if (toRead <= 0) break;

            var diskOffset = AllocationBlockToDiskOffset(extent.StartBlock) + inExtentOffset;
            var temp = new byte[toRead];
            var read = _device.ReadAsync(diskOffset, temp, temp.Length).GetAwaiter().GetResult();
            if (read <= 0) break;

            temp.AsSpan(0, read).CopyTo(destination.Slice(totalRead, read));
            totalRead += read;
            if (totalRead >= requested) break;

            logicalCursor = extentEnd;
        }

        return totalRead;
    }

    private int ReadAppleDoubleResourceFork(HfsClassicCatalogItem item, long offset, Span<byte> destination)
    {
        var totalSize = GetAppleDoubleSidecarSize(item);
        if (offset < 0 || offset >= totalSize || destination.Length == 0) return 0;

        var requested = (int)Math.Min(destination.Length, totalSize - offset);
        var writtenEnd = 0;
        var header = BuildAppleDoubleHeader(item);
        CopyAppleDoubleSegment(header, 0, offset, requested, destination, ref writtenEnd);

        if (item.FinderInfo is { Length: AppleDoubleFinderInfoLength } finderInfo)
        {
            CopyAppleDoubleSegment(finderInfo, GetAppleDoubleFinderInfoOffset(item), offset, requested, destination, ref writtenEnd);
        }

        if (item.ResourceSize > 0 && item.ResourceExtents.Length > 0)
        {
            var targetStart = offset;
            var targetEnd = offset + requested;
            var resourceStart = GetAppleDoubleResourceForkOffset(item);
            var resourceEnd = resourceStart + item.ResourceSize;
            var copyStart = Math.Max(targetStart, resourceStart);
            var copyEnd = Math.Min(targetEnd, resourceEnd);
            var count = (int)Math.Max(0, copyEnd - copyStart);
            if (count > 0)
            {
                var destinationOffset = (int)(copyStart - targetStart);
                var read = ReadFork(item.ResourceExtents, item.ResourceSize, copyStart - resourceStart, destination.Slice(destinationOffset, count));
                writtenEnd = Math.Max(writtenEnd, destinationOffset + read);
            }
        }

        return writtenEnd;
    }

    public void Dispose()
    {
        if (_ownsDevice)
        {
            _device.Dispose();
        }
    }

    private async Task LoadCatalogAsync(CancellationToken ct)
    {
        var headerNode = await ReadCatalogNodeAsync(0, 512, ct).ConfigureAwait(false);
        var kind = (sbyte)headerNode[8];
        if (kind != 1)
        {
            throw new InvalidDataException($"Classic HFS catalog B-tree header node has invalid kind {kind}.");
        }

        var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(headerNode.AsSpan(32, 2));
        if (nodeSize < 512 || nodeSize > 32768 || (nodeSize & (nodeSize - 1)) != 0)
        {
            nodeSize = 512;
        }

        if (nodeSize != 512)
        {
            headerNode = await ReadCatalogNodeAsync(0, nodeSize, ct).ConfigureAwait(false);
        }

        var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(headerNode.AsSpan(24, 4));
        if (firstLeaf == 0)
        {
            throw new InvalidDataException("Classic HFS catalog B-tree has no leaf nodes.");
        }

        var items = new List<HfsClassicCatalogItem>();
        var current = firstLeaf;
        var visited = new HashSet<uint>();
        var safety = 50000;

        while (current != 0 && safety-- > 0 && visited.Add(current))
        {
            var node = await ReadCatalogNodeAsync(current, nodeSize, ct).ConfigureAwait(false);
            if ((sbyte)node[8] != -1) break;

            var next = BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(0, 4));
            var recordCount = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(10, 2));
            for (var i = 0; i < recordCount; i++)
            {
                var (recordOffset, recordLength) = GetRecordOffsetAndLength(node, nodeSize, i, recordCount);
                if (recordLength <= 0) continue;

                var item = TryParseCatalogRecord(node.AsSpan(recordOffset, recordLength));
                if (item is not null)
                {
                    items.Add(WithOverflowExtents(item));
                }
            }

            current = next;
        }

        BuildPathIndexes(items);
    }

    private void BuildPathIndexes(List<HfsClassicCatalogItem> items)
    {
        var modified = _volume.ModifiedTime;
        var root = new RawFsEntry("\\", string.IsNullOrWhiteSpace(_volume.VolumeName) ? "ROOT" : _volume.VolumeName, true, 0, modified, FileAttributes.Directory);
        _entryByPath["\\"] = root;
        _folderIdByPath["\\"] = RootFolderId;

        var byId = items
            .Where(i => i.Cnid != 0)
            .GroupBy(i => i.Cnid)
            .ToDictionary(g => g.Key, g => g.First());

        string ResolvePath(HfsClassicCatalogItem item, HashSet<uint> stack)
        {
            if (item.ParentId == RootFolderId || !byId.TryGetValue(item.ParentId, out var parent))
            {
                return "\\" + SanitizePathSegment(item.Name);
            }

            if (!stack.Add(item.Cnid))
            {
                return "\\" + SanitizePathSegment(item.Name);
            }

            var parentPath = ResolvePath(parent, stack);
            return parentPath == "\\"
                ? "\\" + SanitizePathSegment(item.Name)
                : parentPath + "\\" + SanitizePathSegment(item.Name);
        }

        foreach (var item in items.Where(i => i.RecordKind is 0x0100 or 0x0200))
        {
            var path = NormalizePath(ResolvePath(item, new HashSet<uint>()));
            var attributes = item.IsDirectory ? FileAttributes.Directory : FileAttributes.ReadOnly;
            var entry = new RawFsEntry(path, SanitizePathSegment(item.Name), item.IsDirectory, item.IsDirectory ? 0 : item.Size, item.ModifiedTime, attributes);
            _entryByPath[path] = entry;
            if (item.IsDirectory)
            {
                _folderIdByPath[path] = item.Cnid;
            }
            else
            {
                _itemByPath[path] = item;
            }

            if (!_childrenByFolderId.TryGetValue(item.ParentId, out var siblings))
            {
                siblings = new List<RawFsEntry>();
                _childrenByFolderId[item.ParentId] = siblings;
            }
            siblings.Add(entry);
        }

        foreach (var item in items.Where(HasAppleDoubleSidecar))
        {
            var dataPath = NormalizePath(ResolvePath(item, new HashSet<uint>()));
            var parentPath = GetParentPath(dataPath);
            var sidecarName = "._" + SanitizePathSegment(item.Name);
            var sidecarPath = parentPath == "\\" ? "\\" + sidecarName : parentPath + "\\" + sidecarName;
            if (_entryByPath.ContainsKey(sidecarPath))
            {
                continue;
            }

            var sidecarEntry = new RawFsEntry(
                sidecarPath,
                sidecarName,
                false,
                GetAppleDoubleSidecarSize(item),
                item.ModifiedTime,
                FileAttributes.ReadOnly | FileAttributes.Hidden);
            _entryByPath[sidecarPath] = sidecarEntry;
            _appleDoubleByPath[sidecarPath] = item;

            if (!_childrenByFolderId.TryGetValue(item.ParentId, out var siblings))
            {
                siblings = new List<RawFsEntry>();
                _childrenByFolderId[item.ParentId] = siblings;
            }
            siblings.Add(sidecarEntry);
        }

        foreach (var key in _childrenByFolderId.Keys.ToArray())
        {
            _childrenByFolderId[key] = _childrenByFolderId[key]
                .OrderBy(e => e.IsDirectory ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static string GetParentPath(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath) || normalizedPath == "\\") return "\\";
        var idx = normalizedPath.LastIndexOf('\\');
        return idx <= 0 ? "\\" : normalizedPath[..idx];
    }

    private HfsClassicCatalogItem? TryParseCatalogRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < 8) return null;

        var keyLength = record[0];
        if (keyLength < 6 || keyLength + 1 > record.Length) return null;

        var parentId = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(2, 4));
        var nameLength = record[6];
        if (7 + nameLength > record.Length) return null;

        var name = DecodeClassicName(record.Slice(7, nameLength));
        if (string.IsNullOrWhiteSpace(name)) return null;

        var dataOffset = 1 + keyLength;
        if ((dataOffset & 1) != 0) dataOffset++;
        if (dataOffset + 2 > record.Length) return null;

        var recordType = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(dataOffset, 2));
        if (recordType == 0x0100)
        {
            if (dataOffset + 70 > record.Length) return null;
            var cnid = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(dataOffset + 6, 4));
            var modified = ReadHfsTimestamp(record.Slice(dataOffset + 14, 4));
            return new HfsClassicCatalogItem(
                parentId,
                cnid,
                name,
                true,
                0,
                modified,
                Array.Empty<HfsClassicExtent>(),
                0,
                Array.Empty<HfsClassicExtent>(),
                null,
                recordType);
        }

        if (recordType == 0x0200)
        {
            if (dataOffset + 102 > record.Length) return null;
            var cnid = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(dataOffset + 20, 4));
            var size = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(dataOffset + 26, 4));
            var resourceSize = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(dataOffset + 36, 4));
            var modified = ReadHfsTimestamp(record.Slice(dataOffset + 48, 4));
            var finderInfo = ReadClassicFinderInfo(record.Slice(dataOffset + 4, 16), record.Slice(dataOffset + 56, 16));
            var extents = ReadExtents(record.Slice(dataOffset + 74, 12));
            var resourceExtents = ReadExtents(record.Slice(dataOffset + 86, 12));
            return new HfsClassicCatalogItem(parentId, cnid, name, false, size, modified, extents, resourceSize, resourceExtents, finderInfo, recordType);
        }

        return null;
    }

    private HfsClassicCatalogItem WithOverflowExtents(HfsClassicCatalogItem item)
    {
        if (item.IsDirectory)
        {
            return item;
        }

        var dataExtents = item.DataExtents;
        if (_dataOverflowExtentsByFileId.TryGetValue(item.Cnid, out var dataOverflow) && dataOverflow.Count > 0)
        {
            dataExtents = dataExtents
                .Concat(dataOverflow
                    .OrderBy(r => r.ForkBlockIndex)
                    .SelectMany(r => r.Extents))
                .Where(e => e.BlockCount > 0)
                .ToArray();
        }

        var resourceExtents = item.ResourceExtents;
        if (_resourceOverflowExtentsByFileId.TryGetValue(item.Cnid, out var resourceOverflow) && resourceOverflow.Count > 0)
        {
            resourceExtents = resourceExtents
                .Concat(resourceOverflow
                    .OrderBy(r => r.ForkBlockIndex)
                    .SelectMany(r => r.Extents))
                .Where(e => e.BlockCount > 0)
                .ToArray();
        }

        return item with { DataExtents = dataExtents, ResourceExtents = resourceExtents };
    }

    private async Task LoadExtentsOverflowAsync(CancellationToken ct)
    {
        if (_extentsOverflowRuns.Count == 0 || _volume.ExtentsOverflowFileSize == 0)
        {
            return;
        }

        byte[] headerNode;
        try
        {
            headerNode = await ReadBTreeNodeAsync(0, 512, MapExtentsOverflowLogicalOffsetToPhysical, "classic HFS extents-overflow", ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if ((sbyte)headerNode[8] != 1)
        {
            return;
        }

        var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(headerNode.AsSpan(32, 2));
        if (nodeSize < 512 || nodeSize > 32768 || (nodeSize & (nodeSize - 1)) != 0)
        {
            nodeSize = 512;
        }

        if (nodeSize != 512)
        {
            headerNode = await ReadBTreeNodeAsync(0, nodeSize, MapExtentsOverflowLogicalOffsetToPhysical, "classic HFS extents-overflow", ct).ConfigureAwait(false);
        }

        var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(headerNode.AsSpan(24, 4));
        if (firstLeaf == 0)
        {
            return;
        }

        var current = firstLeaf;
        var visited = new HashSet<uint>();
        var safety = 50000;
        while (current != 0 && safety-- > 0 && visited.Add(current))
        {
            var node = await ReadBTreeNodeAsync(current, nodeSize, MapExtentsOverflowLogicalOffsetToPhysical, "classic HFS extents-overflow", ct).ConfigureAwait(false);
            if ((sbyte)node[8] != -1) break;

            var next = BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(0, 4));
            var recordCount = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(10, 2));
            for (var i = 0; i < recordCount; i++)
            {
                var (recordOffset, recordLength) = GetRecordOffsetAndLength(node, nodeSize, i, recordCount);
                if (recordLength < 20) continue;

                var overflow = TryParseOverflowExtentRecord(node.AsSpan(recordOffset, recordLength));
                if (overflow is null || overflow.FileId == 5)
                {
                    continue;
                }

                var target = overflow.ForkType == -1
                    ? _resourceOverflowExtentsByFileId
                    : overflow.ForkType == 0
                        ? _dataOverflowExtentsByFileId
                        : null;
                if (target is null)
                {
                    continue;
                }

                if (!target.TryGetValue(overflow.FileId, out var records))
                {
                    records = new List<OverflowExtentRecord>();
                    target[overflow.FileId] = records;
                }
                records.Add(overflow);
            }

            current = next;
        }
    }

    private static OverflowExtentRecord? TryParseOverflowExtentRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < 20) return null;
        var keyLength = record[0];
        if (keyLength < 7 || 1 + keyLength > record.Length) return null;

        var forkType = unchecked((sbyte)record[1]);
        var fileId = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(2, 4));
        var forkBlockIndex = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(6, 2));
        var dataOffset = 1 + keyLength;
        if ((dataOffset & 1) != 0) dataOffset++;
        if (dataOffset + 12 > record.Length) return null;

        var extents = ReadExtents(record.Slice(dataOffset, 12));
        if (extents.Length == 0) return null;
        return new OverflowExtentRecord(forkType, fileId, forkBlockIndex, extents);
    }

    private async Task<byte[]> ReadCatalogNodeAsync(uint nodeIndex, int nodeSize, CancellationToken ct)
        => await ReadBTreeNodeAsync(nodeIndex, nodeSize, MapCatalogLogicalOffsetToPhysical, "classic HFS catalog", ct).ConfigureAwait(false);

    private async Task<byte[]> ReadBTreeNodeAsync(uint nodeIndex, int nodeSize, Func<long, long> mapLogicalToPhysical, string label, CancellationToken ct)
    {
        var logicalOffset = (long)nodeIndex * nodeSize;
        var physicalOffset = mapLogicalToPhysical(logicalOffset);
        if (physicalOffset < 0)
        {
            throw new InvalidDataException($"{label} node {nodeIndex} is outside inline extents.");
        }

        var buffer = new byte[nodeSize];
        var read = await _device.ReadAsync(physicalOffset, buffer, buffer.Length, ct).ConfigureAwait(false);
        if (read < buffer.Length)
        {
            throw new EndOfStreamException($"Short read for {label} node {nodeIndex}: {read}/{buffer.Length} bytes.");
        }

        return buffer;
    }

    private long MapCatalogLogicalOffsetToPhysical(long logicalOffset)
    {
        foreach (var run in _catalogRuns)
        {
            if (logicalOffset >= run.LogicalStart && logicalOffset < run.LogicalStart + run.Length)
            {
                return run.PhysicalStart + (logicalOffset - run.LogicalStart);
            }
        }

        return -1;
    }

    private long MapExtentsOverflowLogicalOffsetToPhysical(long logicalOffset)
    {
        foreach (var run in _extentsOverflowRuns)
        {
            if (logicalOffset >= run.LogicalStart && logicalOffset < run.LogicalStart + run.Length)
            {
                return run.PhysicalStart + (logicalOffset - run.LogicalStart);
            }
        }

        return -1;
    }

    private long AllocationBlockToDiskOffset(uint allocationBlock)
        => _volume.AllocationStartOffset + (long)allocationBlock * _volume.AllocationBlockSize;

    private static async Task<HfsClassicVolumeInfo> ReadVolumeInfoAsync(IRawBlockDevice device, long partitionOffset, CancellationToken ct)
    {
        var mdb = new byte[512];
        var read = await device.ReadAsync(partitionOffset + MdbOffset, mdb, mdb.Length, ct).ConfigureAwait(false);
        if (read < 162)
        {
            throw new EndOfStreamException($"Classic HFS master directory block short read: {read}/162 bytes.");
        }

        var signature = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(0, 2));
        if (signature != HfsSignature)
        {
            throw new InvalidDataException($"Classic HFS signature mismatch at MDB: 0x{signature:X4}.");
        }

        var allocationBlockCount = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(18, 2));
        var allocationBlockSize = BinaryPrimitives.ReadUInt32BigEndian(mdb.AsSpan(20, 4));
        var extentsStartBlock = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(28, 2));
        var freeAllocationBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(34, 2));
        var volumeNameLength = Math.Min(mdb[36], (byte)27);
        var volumeName = DecodeClassicName(mdb.AsSpan(37, volumeNameLength));
        var modifiedTime = ReadHfsTimestamp(mdb.AsSpan(6, 4));
        var extentsOverflowFileSize = BinaryPrimitives.ReadUInt32BigEndian(mdb.AsSpan(130, 4));
        var extentsOverflowExtents = ReadExtents(mdb.AsSpan(134, 12));
        var catalogFileSize = BinaryPrimitives.ReadUInt32BigEndian(mdb.AsSpan(146, 4));
        var catalogExtents = ReadExtents(mdb.AsSpan(150, 12));

        if (allocationBlockCount == 0 || allocationBlockSize < SectorSize || (allocationBlockSize % SectorSize) != 0)
        {
            throw new InvalidDataException($"Invalid classic HFS allocation fields: blocks={allocationBlockCount}, blockSize={allocationBlockSize}.");
        }

        return new HfsClassicVolumeInfo(
            partitionOffset,
            partitionOffset + (long)extentsStartBlock * SectorSize,
            allocationBlockSize,
            allocationBlockCount,
            freeAllocationBlocks,
            extentsStartBlock,
            volumeName,
            modifiedTime,
            extentsOverflowFileSize,
            extentsOverflowExtents,
            catalogFileSize,
            catalogExtents);
    }

    private static List<CatalogExtentRun> BuildCatalogRuns(HfsClassicVolumeInfo volume)
        => BuildExtentRuns(volume, volume.CatalogExtents);

    private static List<CatalogExtentRun> BuildExtentRuns(HfsClassicVolumeInfo volume, IReadOnlyList<HfsClassicExtent> extents)
    {
        var runs = new List<CatalogExtentRun>();
        long logical = 0;
        foreach (var extent in extents)
        {
            if (extent.BlockCount == 0) continue;
            var length = (long)extent.BlockCount * volume.AllocationBlockSize;
            runs.Add(new CatalogExtentRun(
                logical,
                volume.AllocationStartOffset + (long)extent.StartBlock * volume.AllocationBlockSize,
                length));
            logical += length;
        }
        return runs;
    }

    private static HfsClassicExtent[] ReadExtents(ReadOnlySpan<byte> bytes)
    {
        var extents = new List<HfsClassicExtent>(3);
        for (var i = 0; i < 3; i++)
        {
            var offset = i * 4;
            if (offset + 4 > bytes.Length) break;
            var start = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2));
            if (count > 0)
            {
                extents.Add(new HfsClassicExtent(start, count));
            }
        }
        return extents.ToArray();
    }

    private static bool HasAppleDoubleSidecar(HfsClassicCatalogItem item)
        => !item.IsDirectory &&
           ((item.ResourceSize > 0 && item.ResourceExtents.Length > 0) ||
            item.FinderInfo is { Length: AppleDoubleFinderInfoLength });

    private static byte[]? ReadClassicFinderInfo(ReadOnlySpan<byte> fileInfo, ReadOnlySpan<byte> extendedInfo)
    {
        if (fileInfo.Length < 16 || extendedInfo.Length < 16) return null;

        var finderInfo = new byte[AppleDoubleFinderInfoLength];
        fileInfo[..16].CopyTo(finderInfo);
        extendedInfo[..16].CopyTo(finderInfo.AsSpan(16));

        for (var i = 0; i < finderInfo.Length; i++)
        {
            if (finderInfo[i] != 0)
            {
                return finderInfo;
            }
        }

        return null;
    }

    private static long GetAppleDoubleSidecarSize(HfsClassicCatalogItem item)
        => GetAppleDoubleResourceForkOffset(item) + (HasResourceForkEntry(item) ? item.ResourceSize : 0);

    private static int GetAppleDoubleEntryCount(HfsClassicCatalogItem item)
        => (item.FinderInfo is { Length: AppleDoubleFinderInfoLength } ? 1 : 0) + (HasResourceForkEntry(item) ? 1 : 0);

    private static int GetAppleDoubleHeaderLength(HfsClassicCatalogItem item)
        => AppleDoubleHeaderBaseLength + GetAppleDoubleEntryCount(item) * AppleDoubleEntryLength;

    private static int GetAppleDoubleFinderInfoOffset(HfsClassicCatalogItem item)
        => GetAppleDoubleHeaderLength(item);

    private static long GetAppleDoubleResourceForkOffset(HfsClassicCatalogItem item)
        => GetAppleDoubleHeaderLength(item) + (item.FinderInfo is { Length: AppleDoubleFinderInfoLength } ? AppleDoubleFinderInfoLength : 0);

    private static bool HasResourceForkEntry(HfsClassicCatalogItem item)
        => item.ResourceSize > 0 && item.ResourceExtents.Length > 0;

    private static byte[] BuildAppleDoubleHeader(HfsClassicCatalogItem item)
    {
        var header = new byte[GetAppleDoubleHeaderLength(item)];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0x00051607); // AppleDouble
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 0x00020000); // version 2
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24, 2), (ushort)GetAppleDoubleEntryCount(item));

        var entryOffset = AppleDoubleHeaderBaseLength;
        if (item.FinderInfo is { Length: AppleDoubleFinderInfoLength })
        {
            WriteAppleDoubleEntry(header.AsSpan(entryOffset, AppleDoubleEntryLength), AppleDoubleFinderInfoEntryId, GetAppleDoubleFinderInfoOffset(item), AppleDoubleFinderInfoLength);
            entryOffset += AppleDoubleEntryLength;
        }

        if (HasResourceForkEntry(item))
        {
            WriteAppleDoubleEntry(header.AsSpan(entryOffset, AppleDoubleEntryLength), AppleDoubleResourceForkEntryId, GetAppleDoubleResourceForkOffset(item), item.ResourceSize);
        }

        return header;
    }

    private static void WriteAppleDoubleEntry(Span<byte> target, uint id, long offset, long length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target[..4], id);
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(4, 4), (uint)Math.Min(offset, uint.MaxValue));
        BinaryPrimitives.WriteUInt32BigEndian(target.Slice(8, 4), (uint)Math.Min(length, uint.MaxValue));
    }

    private static void CopyAppleDoubleSegment(ReadOnlySpan<byte> source, long sourceOffset, long requestOffset, int requested, Span<byte> destination, ref int writtenEnd)
    {
        var targetStart = requestOffset;
        var targetEnd = requestOffset + requested;
        var sourceEnd = sourceOffset + source.Length;
        var copyStart = Math.Max(targetStart, sourceOffset);
        var copyEnd = Math.Min(targetEnd, sourceEnd);
        var count = (int)Math.Max(0, copyEnd - copyStart);
        if (count <= 0)
        {
            return;
        }

        var sourceIndex = (int)(copyStart - sourceOffset);
        var destinationIndex = (int)(copyStart - targetStart);
        source.Slice(sourceIndex, count).CopyTo(destination.Slice(destinationIndex, count));
        writtenEnd = Math.Max(writtenEnd, destinationIndex + count);
    }

    private static (int Offset, int Length) GetRecordOffsetAndLength(byte[] node, int nodeSize, int recordIndex, int recordCount)
    {
        var freeSpacePos = nodeSize - 2;
        var entryPos = freeSpacePos - ((recordCount - recordIndex) * 2);
        var nextEntryPos = entryPos + 2;
        if (entryPos < 14 || nextEntryPos + 2 > node.Length) return (0, 0);

        var offset = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(entryPos, 2));
        var nextOffset = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(nextEntryPos, 2));
        if (offset < 14 || nextOffset <= offset || nextOffset > node.Length)
        {
            return (offset, 0);
        }

        return (offset, nextOffset - offset);
    }

    private static DateTimeOffset ReadHfsTimestamp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return DateTimeOffset.UnixEpoch;
        var hfsSeconds = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (hfsSeconds == 0) return DateTimeOffset.UnixEpoch;
        const long hfsToUnixSeconds = 2082844800L;
        var unixSeconds = (long)hfsSeconds - hfsToUnixSeconds;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static string DecodeClassicName(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i] = b switch
            {
                (byte)':' => '_',
                >= 32 and < 127 => (char)b,
                >= 128 => MacRomanHighChars[b - 128],
                _ => '_'
            };
        }

        return new string(chars).Trim();
    }

    private static readonly char[] MacRomanHighChars =
    {
        '\u00C4', '\u00C5', '\u00C7', '\u00C9', '\u00D1', '\u00D6', '\u00DC', '\u00E1',
        '\u00E0', '\u00E2', '\u00E4', '\u00E3', '\u00E5', '\u00E7', '\u00E9', '\u00E8',
        '\u00EA', '\u00EB', '\u00ED', '\u00EC', '\u00EE', '\u00EF', '\u00F1', '\u00F3',
        '\u00F2', '\u00F4', '\u00F6', '\u00F5', '\u00FA', '\u00F9', '\u00FB', '\u00FC',
        '\u2020', '\u00B0', '\u00A2', '\u00A3', '\u00A7', '\u2022', '\u00B6', '\u00DF',
        '\u00AE', '\u00A9', '\u2122', '\u00B4', '\u00A8', '\u2260', '\u00C6', '\u00D8',
        '\u221E', '\u00B1', '\u2264', '\u2265', '\u00A5', '\u00B5', '\u2202', '\u2211',
        '\u220F', '\u03C0', '\u222B', '\u00AA', '\u00BA', '\u03A9', '\u00E6', '\u00F8',
        '\u00BF', '\u00A1', '\u00AC', '\u221A', '\u0192', '\u2248', '\u2206', '\u00AB',
        '\u00BB', '\u2026', '\u00A0', '\u00C0', '\u00C3', '\u00D5', '\u0152', '\u0153',
        '\u2013', '\u2014', '\u201C', '\u201D', '\u2018', '\u2019', '\u00F7', '\u25CA',
        '\u00FF', '\u0178', '\u2044', '\u20AC', '\u2039', '\u203A', '\uFB01', '\uFB02',
        '\u2021', '\u00B7', '\u201A', '\u201E', '\u2030', '\u00C2', '\u00CA', '\u00C1',
        '\u00CB', '\u00C8', '\u00CD', '\u00CE', '\u00CF', '\u00CC', '\u00D3', '\u00D4',
        '\uF8FF', '\u00D2', '\u00DA', '\u00DB', '\u00D9', '\u0131', '\u02C6', '\u02DC',
        '\u00AF', '\u02D8', '\u02D9', '\u02DA', '\u00B8', '\u02DD', '\u02DB', '\u02C7'
    };

    private static string SanitizePathSegment(string value)
    {
        var decoded = string.IsNullOrWhiteSpace(value) ? "_" : value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            decoded = decoded.Replace(c, '_');
        }
        return decoded.Replace(':', '_');
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/" || path == "\\") return "\\";
        var p = path.Replace('/', '\\');
        if (!p.StartsWith('\\')) p = "\\" + p.TrimStart('\\');
        return p.TrimEnd('\\') == string.Empty ? "\\" : p.TrimEnd('\\');
    }

    private readonly record struct HfsClassicVolumeInfo(
        long PartitionOffset,
        long AllocationStartOffset,
        uint AllocationBlockSize,
        uint AllocationBlockCount,
        uint FreeAllocationBlocks,
        uint ExtentsStartBlock,
        string VolumeName,
        DateTimeOffset ModifiedTime,
        uint ExtentsOverflowFileSize,
        HfsClassicExtent[] ExtentsOverflowExtents,
        uint CatalogFileSize,
        HfsClassicExtent[] CatalogExtents);

    private readonly record struct HfsClassicExtent(uint StartBlock, uint BlockCount);

    private readonly record struct CatalogExtentRun(long LogicalStart, long PhysicalStart, long Length);

    private sealed record OverflowExtentRecord(
        sbyte ForkType,
        uint FileId,
        uint ForkBlockIndex,
        HfsClassicExtent[] Extents);

    private sealed record HfsClassicCatalogItem(
        uint ParentId,
        uint Cnid,
        string Name,
        bool IsDirectory,
        long Size,
        DateTimeOffset ModifiedTime,
        HfsClassicExtent[] DataExtents,
        long ResourceSize,
        HfsClassicExtent[] ResourceExtents,
        byte[]? FinderInfo,
        ushort RecordKind);
}
