using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MacMount.RawDiskEngine;

/// <summary>
/// Native HFS+ on-disk structure reader and writer. Reads/writes the volume header, catalog B-tree,
/// allocation bitmap, and file fork extents directly from/to raw block devices without DiscUtils.
/// All HFS+ on-disk values are BIG-ENDIAN.
/// </summary>
public sealed class HfsPlusNativeReader : IDisposable
{
    private readonly IRawBlockDevice _device;
    private readonly long _partitionOffset;
    private HfsPlusVolumeHeader _header;
    private readonly uint _nodeSize;
    private uint _rootNodeIndex;
    private uint _firstLeafNodeIndex;
    private readonly long _catalogFileStart; // byte offset of first catalog extent on disk

    // Catalog file extent list (block runs on disk)
    private readonly List<(long ByteOffset, long ByteLength)> _catalogExtents = new();

    // Allocation file extent list (block runs on disk)
    private readonly List<(long ByteOffset, long ByteLength)> _allocationExtents = new();

    // Extents-overflow file extent list and B-tree header info
    // (used for files that need more than 8 inline extents)
    private readonly List<(long ByteOffset, long ByteLength)> _extentsExtents = new();
    private uint _extentsNodeSize;
    private uint _extentsRootNode;

    // Volume header raw bytes (512 bytes), kept in sync for flush
    private byte[] _vhRawBuf = new byte[512];

    // Mutable volume header state tracked for writes
    private uint _nextCatalogId;
    private uint _freeBlocks;
    private uint _volumeAttributes;

    // B-tree header state
    private uint _totalNodes;
    private uint _freeNodes;
    private uint _leafRecords;
    private uint _lastLeafNode;
    private ushort _treeDepth;

    // Thread safety: serialise all write operations (WinFsp callbacks are concurrent)
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public HfsPlusVolumeHeader VolumeHeader => _header;
    public bool IsWritable => _device.CanWrite;
    public uint NextCatalogId => _nextCatalogId;

    private HfsPlusNativeReader(IRawBlockDevice device, long partitionOffset, HfsPlusVolumeHeader header,
        uint nodeSize, uint rootNodeIndex, uint firstLeafNodeIndex, List<(long, long)> catalogExtents,
        long catalogFileStart, List<(long, long)> allocationExtents, byte[] vhRawBuf,
        uint nextCatalogId, uint freeBlocks, uint volumeAttributes,
        uint totalNodes, uint freeNodes, uint leafRecords, uint lastLeafNode, ushort treeDepth,
        List<(long, long)> extentsExtents, uint extentsNodeSize, uint extentsRootNode)
    {
        _device = device;
        _partitionOffset = partitionOffset;
        _header = header;
        _nodeSize = nodeSize;
        _rootNodeIndex = rootNodeIndex;
        _firstLeafNodeIndex = firstLeafNodeIndex;
        _catalogExtents = catalogExtents;
        _catalogFileStart = catalogFileStart;
        _allocationExtents = allocationExtents;
        _vhRawBuf = vhRawBuf;
        _nextCatalogId = nextCatalogId;
        _freeBlocks = freeBlocks;
        _volumeAttributes = volumeAttributes;
        _totalNodes = totalNodes;
        _freeNodes = freeNodes;
        _leafRecords = leafRecords;
        _lastLeafNode = lastLeafNode;
        _treeDepth = treeDepth;
        _extentsExtents = extentsExtents;
        _extentsNodeSize = extentsNodeSize;
        _extentsRootNode = extentsRootNode;
    }

    public static async Task<HfsPlusNativeReader?> OpenAsync(IRawBlockDevice device, long partitionOffset, CancellationToken ct = default)
    {
        // Read volume header at partition + 1024
        var vhBuf = new byte[512];
        var read = await device.ReadAsync(partitionOffset + 1024, vhBuf, vhBuf.Length, ct).ConfigureAwait(false);
        if (read < 512) return null;

        var sig = BinaryPrimitives.ReadUInt16BigEndian(vhBuf.AsSpan(0, 2));
        if (sig != 0x482B && sig != 0x4858) return null; // H+ or HX

        var header = ParseVolumeHeader(vhBuf);
        if (header.BlockSize == 0 || header.TotalBlocks == 0) return null;

        // Read mutable VH fields
        var volumeAttributes = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(4, 4));
        var nextCatalogId = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(64, 4));

        // Build catalog file extent map (up to 8 extents in the volume header)
        var catalogExtents = new List<(long ByteOffset, long ByteLength)>();
        for (int i = 0; i < 8; i++)
        {
            var startBlock = header.CatalogExtents[i].StartBlock;
            var blockCount = header.CatalogExtents[i].BlockCount;
            if (blockCount == 0) break;
            var byteOff = partitionOffset + (long)startBlock * header.BlockSize;
            var byteLen = (long)blockCount * header.BlockSize;
            catalogExtents.Add((byteOff, byteLen));
        }

        if (catalogExtents.Count == 0) return null;

        // Build allocation file extent map (offset 112 = 0x70 in VH)
        var allocationExtents = new List<(long ByteOffset, long ByteLength)>();
        const int allocationForkOffset = 112; // 0x70
        for (int i = 0; i < 8; i++)
        {
            var extOff = allocationForkOffset + 16 + i * 8;
            var startBlock = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(extOff, 4));
            var blockCnt = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(extOff + 4, 4));
            if (blockCnt == 0) break;
            var byteOff = partitionOffset + (long)startBlock * header.BlockSize;
            var byteLen = (long)blockCnt * header.BlockSize;
            allocationExtents.Add((byteOff, byteLen));
        }

        // Build extents-overflow file extent map (offset 80 = 0x50 in VH).
        // Layout matches allocation/catalog forks: logicalSize(8) clumpSize(4) totalBlocks(4) extents[8].
        var extentsExtents = new List<(long ByteOffset, long ByteLength)>();
        const int extentsForkOffset = 80; // 0x50
        for (int i = 0; i < 8; i++)
        {
            var extOff = extentsForkOffset + 16 + i * 8;
            var startBlock = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(extOff, 4));
            var blockCnt = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(extOff + 4, 4));
            if (blockCnt == 0) break;
            var byteOff = partitionOffset + (long)startBlock * header.BlockSize;
            var byteLen = (long)blockCnt * header.BlockSize;
            extentsExtents.Add((byteOff, byteLen));
        }

        // Read the extents-overflow B-tree header node (node 0) if the file exists.
        // Note: many newly-formatted volumes have an empty extents-overflow B-tree
        // (header node only); this is normal.
        uint extentsNodeSize = 0;
        uint extentsRootNode = 0;
        if (extentsExtents.Count > 0)
        {
            var extentsHeaderBuf = new byte[Math.Max(header.BlockSize, 4096u)];
            var extentsHeaderRead = await device.ReadAsync(extentsExtents[0].ByteOffset, extentsHeaderBuf, extentsHeaderBuf.Length, ct).ConfigureAwait(false);
            if (extentsHeaderRead >= 512)
            {
                var extentsKind = (sbyte)extentsHeaderBuf[8];
                if (extentsKind == 1)
                {
                    extentsRootNode  = BinaryPrimitives.ReadUInt32BigEndian(extentsHeaderBuf.AsSpan(16, 4));
                    extentsNodeSize  = BinaryPrimitives.ReadUInt16BigEndian(extentsHeaderBuf.AsSpan(32, 2));
                }
            }
        }

        // Read the catalog B-tree header node (node 0)
        var headerNodeBuf = new byte[Math.Max(header.BlockSize, 4096u)];
        var headerNodeRead = await device.ReadAsync(catalogExtents[0].ByteOffset, headerNodeBuf, headerNodeBuf.Length, ct).ConfigureAwait(false);
        if (headerNodeRead < 512) return null;

        // BTNodeDescriptor: fLink(4) bLink(4) kind(1) height(1) numRecords(2) reserved(2) = 14 bytes
        var nodeKind = (sbyte)headerNodeBuf[8];
        if (nodeKind != 1) return null; // must be header node (kind=1)

        // BTHeaderRec starts at offset 14 in the header node
        // +0: treeDepth(2), +2: rootNode(4), +6: leafRecords(4), +10: firstLeafNode(4),
        // +14: lastLeafNode(4), +18: nodeSize(2), +20: maxKeyLength(2), +22: totalNodes(4), +26: freeNodes(4)
        var treeDepth = BinaryPrimitives.ReadUInt16BigEndian(headerNodeBuf.AsSpan(14, 2));
        var rootNode = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(16, 4));
        var leafRecords = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(20, 4));
        var firstLeafNode = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(24, 4));
        var lastLeafNode = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(28, 4));
        var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(headerNodeBuf.AsSpan(32, 2));
        var totalNodes = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(14 + 22, 4));
        var freeNodes = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(14 + 26, 4));

        if (nodeSize < 512) return null;

        return new HfsPlusNativeReader(device, partitionOffset, header, nodeSize, rootNode, firstLeafNode,
            catalogExtents, catalogExtents[0].ByteOffset, allocationExtents, vhBuf,
            nextCatalogId, header.FreeBlocks, volumeAttributes,
            totalNodes, freeNodes, leafRecords, lastLeafNode, treeDepth,
            extentsExtents, extentsNodeSize, extentsRootNode);
    }

    /// <summary>
    /// List all catalog entries (files and folders) with the given parent CNID.
    /// Root folder CNID = 2.
    /// </summary>
    public async Task<List<HfsPlusCatalogItem>> ListDirectoryAsync(uint parentCnid, CancellationToken ct = default)
    {
        var results = new List<HfsPlusCatalogItem>();
        var visited = new HashSet<uint>();

        // Find the first leaf node by traversing B-tree index from root.
        // Fall back to scanning from first leaf if index traversal fails.
        var leafNodeIndex = await FindFirstLeafForParentAsync(parentCnid, ct).ConfigureAwait(false);
        if (leafNodeIndex == 0) leafNodeIndex = _firstLeafNodeIndex;
        if (leafNodeIndex == 0) return results;

        // Walk the leaf node chain
        var currentNode = leafNodeIndex;
        var safetyLimit = 50000;
        while (currentNode != 0 && safetyLimit-- > 0)
        {
            if (!visited.Add(currentNode)) break; // cycle detection

            var nodeBuf = await ReadCatalogNodeAsync(currentNode, ct).ConfigureAwait(false);
            if (nodeBuf is null) break;

            var kind = (sbyte)nodeBuf[8];
            if (kind != -1) break; // not a leaf node

            var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
            var pastTarget = false;

            for (int i = 0; i < numRecords; i++)
            {
                var (recOffset, recLen) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
                if (recOffset < 14 || recOffset + 6 > nodeBuf.Length) continue;

                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOffset, 2));
                if (keyLen < 6 || recOffset + 2 + keyLen > nodeBuf.Length) continue;

                var recParentCnid = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOffset + 2, 4));

                if (recParentCnid < parentCnid) continue;
                if (recParentCnid > parentCnid) { pastTarget = true; break; }

                // Parse the catalog record after the key
                var dataOffset = recOffset + 2 + keyLen;
                // Align to 2-byte boundary
                if (dataOffset % 2 != 0) dataOffset++;

                if (dataOffset + 2 > nodeBuf.Length) continue;

                var recordType = BinaryPrimitives.ReadInt16BigEndian(nodeBuf.AsSpan(dataOffset, 2));

                // Extract name from key: after parentCnid(4), nameLength(2), then UTF-16BE chars
                var nameOffset = recOffset + 6;
                var nameLen = recOffset + 2 + 4 < nodeBuf.Length
                    ? BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOffset + 6, 2))
                    : 0;
                var name = "";
                if (nameLen > 0 && nameOffset + 2 + nameLen * 2 <= nodeBuf.Length)
                {
                    var chars = new char[nameLen];
                    for (int c = 0; c < nameLen; c++)
                    {
                        chars[c] = (char)BinaryPrimitives.ReadUInt16BigEndian(
                            nodeBuf.AsSpan(nameOffset + 2 + c * 2, 2));
                    }
                    name = new string(chars);
                }

                if (string.IsNullOrEmpty(name)) continue;

                if (recordType == 1) // Folder
                {
                    if (dataOffset + 70 > nodeBuf.Length) continue;
                    var folderCnid = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(dataOffset + 8, 4));
                    var modTime = ReadHfsTimestamp(nodeBuf, dataOffset + 16);
                    results.Add(new HfsPlusCatalogItem(name, true, 0, modTime, folderCnid, null));
                }
                else if (recordType == 2) // File
                {
                    if (dataOffset + 248 > nodeBuf.Length) continue;
                    var fileCnid = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(dataOffset + 8, 4));
                    var modTime = ReadHfsTimestamp(nodeBuf, dataOffset + 16);
                    // Data fork: starts at dataOffset + 88
                    var dataFork = ParseForkData(nodeBuf, dataOffset + 88);
                    results.Add(new HfsPlusCatalogItem(name, false, dataFork.LogicalSize, modTime, fileCnid, dataFork));
                }
                // Types 3/4 are thread records — skip
            }

            if (pastTarget) break;

            // Follow fLink to next leaf node
            var fLink = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(0, 4));
            currentNode = fLink;
        }

        return results;
    }

    /// <summary>
    /// Returns the full extent list for a fork: the up-to-8 inline extents, plus
    /// any additional extents resolved from the extents-overflow B-tree if the
    /// fork's totalBlocks exceeds what the inline extents cover.
    /// </summary>
    /// <param name="fork">The fork descriptor read from the catalog (has 8 inline extents).</param>
    /// <param name="fileId">The CNID of the file (used as the extents-overflow B-tree key).</param>
    /// <param name="forkType">0x00 for data fork, 0xFF for resource fork.</param>
    public async Task<HfsPlusExtent[]> ResolveAllExtentsAsync(
        HfsPlusForkInfo fork, uint fileId, byte forkType, CancellationToken ct = default)
    {
        var inline = new List<HfsPlusExtent>();
        uint blocksCovered = 0;
        foreach (var ext in fork.Extents)
        {
            if (ext.BlockCount == 0) break;
            inline.Add(ext);
            blocksCovered += ext.BlockCount;
        }

        // If logicalSize fits inside the blocks covered by inline extents, no overflow lookup needed.
        var blocksRequired = (uint)((fork.LogicalSize + _header.BlockSize - 1) / _header.BlockSize);
        if (blocksCovered >= blocksRequired || _extentsRootNode == 0 || _extentsNodeSize == 0)
        {
            return inline.ToArray();
        }

        // Walk the extents-overflow B-tree starting from blocksCovered.
        var combined = new List<HfsPlusExtent>(inline);
        var startBlock = blocksCovered;
        var safetyLimit = 256; // bound the walk against pathological B-trees
        while (blocksCovered < blocksRequired && safetyLimit-- > 0)
        {
            var record = await LookupExtentsOverflowAsync(fileId, forkType, startBlock, ct).ConfigureAwait(false);
            if (record is null || record.Length == 0) break;
            foreach (var ext in record)
            {
                if (ext.BlockCount == 0) break;
                combined.Add(ext);
                blocksCovered += ext.BlockCount;
            }
            // Next overflow record starts where the last one ended.
            startBlock = blocksCovered;
        }

        return combined.ToArray();
    }

    /// <summary>
    /// Read file data from a fork, resolving extents-overflow records when the
    /// 8 inline extents are insufficient. Use this overload for any file whose
    /// fileId+forkType is known and whose fork.LogicalSize may extend past the
    /// inline extents.
    /// </summary>
    public async Task<int> ReadFileAsync(HfsPlusForkInfo fork, uint fileId, byte forkType,
        long offset, byte[] buffer, int count, CancellationToken ct = default)
    {
        var allExtents = await ResolveAllExtentsAsync(fork, fileId, forkType, ct).ConfigureAwait(false);
        return await ReadExtentsAsync(allExtents, fork.LogicalSize, offset, buffer, count, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read file data from a data fork's extent records (legacy 8-extent path).
    /// Prefer the (fileId, forkType) overload to support files larger than 8 extents.
    /// </summary>
    public Task<int> ReadFileAsync(HfsPlusForkInfo fork, long offset, byte[] buffer, int count, CancellationToken ct = default)
    {
        return ReadExtentsAsync(fork.Extents, fork.LogicalSize, offset, buffer, count, ct);
    }

    private async Task<int> ReadExtentsAsync(IReadOnlyList<HfsPlusExtent> extents, long logicalSize,
        long offset, byte[] buffer, int count, CancellationToken ct)
    {
        if (offset < 0 || offset >= logicalSize) return 0;
        var toRead = (int)Math.Min(count, logicalSize - offset);
        var totalRead = 0;

        foreach (var ext in extents)
        {
            if (ext.BlockCount == 0) break;

            var extByteStart = (long)ext.StartBlock * _header.BlockSize;
            var extByteLen = (long)ext.BlockCount * _header.BlockSize;

            if (offset >= extByteLen)
            {
                offset -= extByteLen;
                continue;
            }

            var readStart = _partitionOffset + extByteStart + offset;
            var readLen = (int)Math.Min(toRead - totalRead, extByteLen - offset);
            if (readLen <= 0) break;

            var tempBuf = new byte[readLen];
            var read = await _device.ReadAsync(readStart, tempBuf, readLen, ct).ConfigureAwait(false);
            if (read > 0)
            {
                Array.Copy(tempBuf, 0, buffer, totalRead, read);
                totalRead += read;
            }

            offset = 0; // subsequent extents start at 0
            if (totalRead >= toRead) break;
        }

        return totalRead;
    }

    /// <summary>
    /// Look up a single extents-overflow record for (fileId, forkType, startBlock).
    /// Returns the 8-extent array if found, null otherwise. Walks the B-tree from
    /// the root index node down to a leaf using HFSPlusExtentKey ordering:
    /// fileID, then forkType (data 0x00 &lt; resource 0xFF), then startBlock.
    /// </summary>
    private async Task<HfsPlusExtent[]?> LookupExtentsOverflowAsync(uint fileId, byte forkType, uint startBlock, CancellationToken ct)
    {
        if (_extentsRootNode == 0 || _extentsNodeSize == 0) return null;

        // Walk down from the root, picking the index entry whose key is <= target.
        var currentNode = _extentsRootNode;
        var safetyLimit = 32; // tree depth bound
        while (safetyLimit-- > 0)
        {
            var nodeBuf = await ReadExtentsNodeAsync(currentNode, ct).ConfigureAwait(false);
            if (nodeBuf is null) return null;

            var kind = (sbyte)nodeBuf[8];          // 0x00 = leaf, 0xFE = index, 0x01 = header
            var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));

            // Index node: pick child whose key is <= target, descend.
            if (kind == unchecked((sbyte)0xFE))
            {
                uint chosenChild = 0;
                for (int i = 0; i < numRecords; i++)
                {
                    var (recOff, recLen) = GetExtentsRecordOffsetAndLength(nodeBuf, i, numRecords);
                    if (recOff < 14 || recLen <= 0) continue;
                    if (recLen < 12 + 4) continue; // key(12) + child pointer(4)
                    var keyCmp = CompareExtentsKey(nodeBuf, recOff, fileId, forkType, startBlock);
                    if (keyCmp <= 0)
                    {
                        chosenChild = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 12, 4));
                    }
                    else
                    {
                        break;
                    }
                }
                if (chosenChild == 0) return null;
                currentNode = chosenChild;
                continue;
            }

            // Leaf node: find a record whose key matches OR is the largest key <= target
            // (because overflow records cover a range of startBlocks).
            if (kind == 0)
            {
                int bestIdx = -1;
                for (int i = 0; i < numRecords; i++)
                {
                    var (recOff, recLen) = GetExtentsRecordOffsetAndLength(nodeBuf, i, numRecords);
                    if (recOff < 14 || recLen < 12 + 64) continue;
                    if (CompareExtentsFileFork(nodeBuf, recOff, fileId, forkType) != 0) continue;
                    var recStartBlock = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 8, 4));
                    if (recStartBlock <= startBlock) bestIdx = i;
                    else break;
                }
                if (bestIdx < 0) return null;
                var (off, len) = GetExtentsRecordOffsetAndLength(nodeBuf, bestIdx, numRecords);
                if (off < 14 || len < 12 + 64) return null;
                // Confirm the chosen record actually starts at the requested startBlock — the
                // walk above can pick the record covering an earlier range if no exact match
                // exists, but for our caller (sequential traversal) that means there's no
                // record for this startBlock yet.
                var foundStart = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(off + 8, 4));
                if (foundStart != startBlock) return null;

                return ParseExtentsRecord(nodeBuf, off + 12);
            }

            return null; // unexpected node kind
        }
        return null;
    }

    private async Task<byte[]?> ReadExtentsNodeAsync(uint nodeIndex, CancellationToken ct)
    {
        if (_extentsNodeSize == 0) return null;
        var buf = new byte[_extentsNodeSize];
        var nodeOffset = (long)nodeIndex * _extentsNodeSize;
        var remaining = nodeOffset;
        foreach (var (byteOff, byteLen) in _extentsExtents)
        {
            if (remaining < byteLen)
            {
                var physicalOffset = byteOff + remaining;
                var read = await _device.ReadAsync(physicalOffset, buf, buf.Length, ct).ConfigureAwait(false);
                return read >= _extentsNodeSize ? buf : null;
            }
            remaining -= byteLen;
        }
        return null;
    }

    private (int Offset, int Length) GetExtentsRecordOffsetAndLength(byte[] nodeBuf, int recordIndex, int numRecords)
    {
        // Same offset-table layout as the catalog B-tree, parameterized on the
        // extents-overflow B-tree's own node size.
        var freeSpacePos = (int)_extentsNodeSize - 2;
        var entryPos = freeSpacePos - ((numRecords - recordIndex) * 2);
        var nextEntryPos = entryPos + 2;
        if (entryPos < 14 || entryPos + 2 > nodeBuf.Length) return (0, 0);
        if (nextEntryPos < 0 || nextEntryPos + 2 > nodeBuf.Length) return (0, 0);
        var offset = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(entryPos, 2));
        var nextOffset = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(nextEntryPos, 2));
        if (offset < 14 || nextOffset <= offset) return (offset, 0);
        return (offset, nextOffset - offset);
    }

    /// <summary>
    /// Compare a record's HFSPlusExtentKey at <paramref name="recOff"/> against
    /// (fileId, forkType, startBlock). Returns -1 / 0 / +1 by HFS+ ordering:
    /// fileID, then forkType (data 0x00 &lt; resource 0xFF), then startBlock.
    /// </summary>
    private static int CompareExtentsKey(byte[] nodeBuf, int recOff, uint fileId, byte forkType, uint startBlock)
    {
        // HFSPlusExtentKey: keyLength(2) forkType(1) pad(1) fileID(4) startBlock(4)
        var recForkType = nodeBuf[recOff + 2];
        var recFileId = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 4, 4));
        var recStartBlock = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 8, 4));
        if (recFileId != fileId) return recFileId < fileId ? -1 : 1;
        if (recForkType != forkType) return recForkType < forkType ? -1 : 1;
        if (recStartBlock != startBlock) return recStartBlock < startBlock ? -1 : 1;
        return 0;
    }

    private static int CompareExtentsFileFork(byte[] nodeBuf, int recOff, uint fileId, byte forkType)
    {
        var recForkType = nodeBuf[recOff + 2];
        var recFileId = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 4, 4));
        if (recFileId != fileId) return recFileId < fileId ? -1 : 1;
        if (recForkType != forkType) return recForkType < forkType ? -1 : 1;
        return 0;
    }

    private static HfsPlusExtent[] ParseExtentsRecord(byte[] nodeBuf, int valOff)
    {
        // HFSPlusExtentRecord = 8 × HFSPlusExtentDescriptor (8 bytes each)
        var extents = new HfsPlusExtent[8];
        for (int i = 0; i < 8; i++)
        {
            var off = valOff + i * 8;
            var startBlock = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(off, 4));
            var blockCount = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(off + 4, 4));
            extents[i] = new HfsPlusExtent(startBlock, blockCount);
        }
        return extents;
    }

    // ─── WRITE SUPPORT ────────────────────────────────────────────────────────

    #region Allocation Bitmap

    /// <summary>
    /// Allocate <paramref name="count"/> contiguous free blocks from the allocation bitmap.
    /// Returns the starting block number. Throws if not enough contiguous blocks.
    /// </summary>
    public async Task<uint> AllocateBlocksAsync(uint count, CancellationToken ct = default)
    {
        if (count == 0) throw new ArgumentException("Cannot allocate 0 blocks.", nameof(count));
        if (count > _freeBlocks) throw new InvalidOperationException($"Not enough free blocks: requested {count}, available {_freeBlocks}.");

        var totalBlocks = _header.TotalBlocks;
        // Read the entire allocation bitmap
        var bitmapBytes = await ReadAllocationBitmapAsync(ct).ConfigureAwait(false);

        // Search for 'count' contiguous free bits (0-bits)
        uint runStart = 0;
        uint runLength = 0;
        for (uint block = 0; block < totalBlocks; block++)
        {
            var byteIndex = block / 8;
            var bitIndex = 7 - (int)(block % 8); // MSB first
            if (byteIndex >= bitmapBytes.Length) break;

            var inUse = (bitmapBytes[byteIndex] & (1 << bitIndex)) != 0;
            if (inUse)
            {
                runStart = block + 1;
                runLength = 0;
            }
            else
            {
                runLength++;
                if (runLength >= count)
                {
                    // Found a run — mark all bits as used
                    for (uint b = runStart; b < runStart + count; b++)
                    {
                        var bi = b / 8;
                        var bit = 7 - (int)(b % 8);
                        bitmapBytes[bi] |= (byte)(1 << bit);
                    }

                    await WriteAllocationBitmapAsync(bitmapBytes, ct).ConfigureAwait(false);
                    _freeBlocks -= count;
                    return runStart;
                }
            }
        }

        throw new InvalidOperationException($"Cannot find {count} contiguous free blocks.");
    }

    /// <summary>
    /// Free <paramref name="count"/> blocks starting at <paramref name="startBlock"/>.
    /// </summary>
    public async Task FreeBlocksAsync(uint startBlock, uint count, CancellationToken ct = default)
    {
        if (count == 0) return;
        var bitmapBytes = await ReadAllocationBitmapAsync(ct).ConfigureAwait(false);

        for (uint b = startBlock; b < startBlock + count && b < _header.TotalBlocks; b++)
        {
            var bi = b / 8;
            var bit = 7 - (int)(b % 8);
            if (bi < bitmapBytes.Length)
            {
                bitmapBytes[bi] &= (byte)~(1 << bit);
            }
        }

        await WriteAllocationBitmapAsync(bitmapBytes, ct).ConfigureAwait(false);
        _freeBlocks += count;
    }

    private async Task<byte[]> ReadAllocationBitmapAsync(CancellationToken ct)
    {
        // Total bytes needed: ceil(totalBlocks / 8)
        var totalBytes = (int)((_header.TotalBlocks + 7) / 8);
        var bitmap = new byte[totalBytes];
        var read = 0;

        foreach (var (byteOff, byteLen) in _allocationExtents)
        {
            var remaining = totalBytes - read;
            if (remaining <= 0) break;
            var toRead = (int)Math.Min(remaining, byteLen);
            var buf = new byte[toRead];
            await _device.ReadAsync(byteOff, buf, toRead, ct).ConfigureAwait(false);
            Buffer.BlockCopy(buf, 0, bitmap, read, toRead);
            read += toRead;
        }

        return bitmap;
    }

    private async Task WriteAllocationBitmapAsync(byte[] bitmap, CancellationToken ct)
    {
        var totalBytes = bitmap.Length;
        var written = 0;

        foreach (var (byteOff, byteLen) in _allocationExtents)
        {
            var remaining = totalBytes - written;
            if (remaining <= 0) break;
            var toWrite = (int)Math.Min(remaining, byteLen);
            var buf = new byte[toWrite];
            Buffer.BlockCopy(bitmap, written, buf, 0, toWrite);
            await _device.WriteAsync(byteOff, buf, toWrite, ct).ConfigureAwait(false);
            written += toWrite;
        }
    }

    #endregion

    #region Volume Header + Journal

    /// <summary>
    /// Disable journaling by clearing the journal flag (bit 13) in the volume header attributes.
    /// Safe for external drives. Must be called before making any structural writes.
    /// </summary>
    public async Task DisableJournalAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            const uint kHFSVolumeJournaledBit = 1u << 13;
            if ((_volumeAttributes & kHFSVolumeJournaledBit) == 0) return; // already off

            _volumeAttributes &= ~kHFSVolumeJournaledBit;
            // Zero out journalInfoBlock (VH offset 36, uint32 BE)
            BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(4, 4), _volumeAttributes);
            BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(36, 4), 0);

            await FlushVolumeHeaderAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Write the volume header to both primary (partition+1024) and alternate (end of volume - 1024) locations
    /// with updated freeBlocks and nextCatalogID.
    /// </summary>
    public async Task FlushVolumeHeaderAsync(CancellationToken ct = default)
    {
        // Update mutable fields in _vhRawBuf
        BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(4, 4), _volumeAttributes);
        BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(48, 4), _freeBlocks);
        BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(64, 4), _nextCatalogId);

        // Update catalog file fork extents in the volume header (offset 272 = 0x110).
        // This MUST be done after GrowCatalogFileAsync adds new extents, otherwise
        // a reopen would lose access to catalog nodes in the grown extents.
        const int catalogForkOffset = 272;
        // Compute total catalog logical size and total blocks from _catalogExtents
        long catalogLogicalSize = 0;
        uint catalogTotalBlocks = 0;
        foreach (var (_, byteLen) in _catalogExtents)
        {
            catalogLogicalSize += byteLen;
            catalogTotalBlocks += (uint)(byteLen / _header.BlockSize);
        }
        BinaryPrimitives.WriteUInt64BigEndian(_vhRawBuf.AsSpan(catalogForkOffset, 8), (ulong)catalogLogicalSize);
        BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(catalogForkOffset + 12, 4), catalogTotalBlocks);
        // Write up to 8 inline extents
        for (int i = 0; i < 8; i++)
        {
            var extOff = catalogForkOffset + 16 + i * 8;
            if (i < _catalogExtents.Count)
            {
                var (byteOff, byteLen) = _catalogExtents[i];
                var startBlock = (uint)((byteOff - _partitionOffset) / _header.BlockSize);
                var blockCount = (uint)(byteLen / _header.BlockSize);
                BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(extOff, 4), startBlock);
                BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(extOff + 4, 4), blockCount);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(extOff, 4), 0);
                BinaryPrimitives.WriteUInt32BigEndian(_vhRawBuf.AsSpan(extOff + 4, 4), 0);
            }
        }

        // Write primary volume header at partition + 1024
        await _device.WriteAsync(_partitionOffset + 1024, _vhRawBuf, 512, ct).ConfigureAwait(false);

        // Write alternate volume header at end of volume - 1024
        var altOffset = _partitionOffset + (long)_header.TotalBlocks * _header.BlockSize - 1024;
        if (altOffset > _partitionOffset + 1024) // sanity check
        {
            await _device.WriteAsync(altOffset, _vhRawBuf, 512, ct).ConfigureAwait(false);
        }

        // Update in-memory header to reflect new freeBlocks
        _header = _header with { FreeBlocks = _freeBlocks };
    }

    #endregion

    #region B-tree Write Helpers

    private async Task WriteCatalogNodeAsync(uint nodeIndex, byte[] nodeBuf, CancellationToken ct)
    {
        var nodeOffset = (long)nodeIndex * _nodeSize;
        var remaining = nodeOffset;
        foreach (var (byteOff, byteLen) in _catalogExtents)
        {
            if (remaining < byteLen)
            {
                var physicalOffset = byteOff + remaining;
                await _device.WriteAsync(physicalOffset, nodeBuf, (int)_nodeSize, ct).ConfigureAwait(false);
                return;
            }
            remaining -= byteLen;
        }

        throw new InvalidOperationException($"Catalog node {nodeIndex} is beyond catalog extent range.");
    }

    private async Task UpdateBTreeHeaderAsync(CancellationToken ct)
    {
        // Read header node (node 0)
        var headerBuf = await ReadCatalogNodeAsync(0, ct).ConfigureAwait(false);
        if (headerBuf is null) throw new InvalidOperationException("Cannot read B-tree header node.");

        // BTHeaderRec at offset 14:
        // +0: treeDepth(2), +2: rootNode(4), +6: leafRecords(4), +10: firstLeafNode(4),
        // +14: lastLeafNode(4), +22: totalNodes(4), +26: freeNodes(4)
        BinaryPrimitives.WriteUInt16BigEndian(headerBuf.AsSpan(14, 2), _treeDepth);
        BinaryPrimitives.WriteUInt32BigEndian(headerBuf.AsSpan(16, 4), _rootNodeIndex);
        BinaryPrimitives.WriteUInt32BigEndian(headerBuf.AsSpan(20, 4), _leafRecords);
        BinaryPrimitives.WriteUInt32BigEndian(headerBuf.AsSpan(24, 4), _firstLeafNodeIndex);
        BinaryPrimitives.WriteUInt32BigEndian(headerBuf.AsSpan(28, 4), _lastLeafNode);
        BinaryPrimitives.WriteUInt32BigEndian(headerBuf.AsSpan(14 + 22, 4), _totalNodes);
        BinaryPrimitives.WriteUInt32BigEndian(headerBuf.AsSpan(14 + 26, 4), _freeNodes);

        await WriteCatalogNodeAsync(0, headerBuf, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Allocate a free node in the B-tree by finding a free bit in the B-tree node bitmap
    /// (stored in the header node's map record).
    /// </summary>
    private async Task<uint> AllocateBTreeNodeAsync(CancellationToken ct)
    {
        if (_freeNodes == 0)
        {
            // Grow the catalog file to make room for more nodes
            await GrowCatalogFileAsync(ct).ConfigureAwait(false);
        }

        // The bitmap is stored in the header node, starting after the 3 standard records
        // (header record, user data record, map record). The map record typically starts at
        // offset 256 in the header node and runs to the end of the node.
        var headerBuf = await ReadCatalogNodeAsync(0, ct).ConfigureAwait(false);
        if (headerBuf is null) throw new InvalidOperationException("Cannot read B-tree header node.");

        // Find the map record offset from the record offset table
        var numRecords = BinaryPrimitives.ReadUInt16BigEndian(headerBuf.AsSpan(10, 2));
        if (numRecords < 3) throw new InvalidOperationException("B-tree header node has fewer than 3 records.");

        var (mapOffset, mapLen) = GetRecordOffsetAndLength(headerBuf, 2, numRecords);
        if (mapOffset < 14 || mapLen <= 0)
        {
            var hex = Convert.ToHexString(headerBuf.AsSpan(0, Math.Min(64, headerBuf.Length)));
            throw new InvalidOperationException(
                $"Cannot locate B-tree node bitmap. numRecords={numRecords}, mapOffset={mapOffset}, mapLen={mapLen}, " +
                $"nodeSize={_nodeSize}, totalNodes={_totalNodes}, freeNodes={_freeNodes}, headerHex={hex}");
        }

        // Search for a free bit (0-bit) in the bitmap. Bit N=1 means node N is allocated.
        for (int byteIdx = 0; byteIdx < mapLen; byteIdx++)
        {
            var b = headerBuf[mapOffset + byteIdx];
            if (b == 0xFF) continue; // all 8 nodes allocated

            for (int bitIdx = 7; bitIdx >= 0; bitIdx--)
            {
                if ((b & (1 << bitIdx)) == 0)
                {
                    var nodeId = (uint)(byteIdx * 8 + (7 - bitIdx));
                    if (nodeId == 0) continue; // node 0 is the header node
                    if (nodeId >= _totalNodes) continue;

                    // Mark as allocated
                    headerBuf[mapOffset + byteIdx] |= (byte)(1 << bitIdx);
                    await WriteCatalogNodeAsync(0, headerBuf, ct).ConfigureAwait(false);
                    _freeNodes--;
                    return nodeId;
                }
            }
        }

        throw new InvalidOperationException("No free B-tree nodes found in bitmap.");
    }

    /// <summary>
    /// Grow the catalog file by allocating more disk blocks and adding new empty B-tree nodes.
    /// Also extends the header node's map record (bitmap) if it's too small to track the new nodes.
    /// </summary>
    private async Task GrowCatalogFileAsync(CancellationToken ct)
    {
        // Allocate enough blocks for 16 new nodes
        var nodesPerBlock = _header.BlockSize / _nodeSize;
        if (nodesPerBlock == 0) nodesPerBlock = 1;
        var newNodeCount = (uint)Math.Max(16, nodesPerBlock);
        var blocksNeeded = (uint)((newNodeCount * _nodeSize + _header.BlockSize - 1) / _header.BlockSize);

        var startBlock = await AllocateBlocksAsync(blocksNeeded, ct).ConfigureAwait(false);
        var byteOff = _partitionOffset + (long)startBlock * _header.BlockSize;
        var byteLen = (long)blocksNeeded * _header.BlockSize;

        // Zero-fill the new blocks
        var zeroBuf = new byte[Math.Min(byteLen, 65536)];
        for (long written = 0; written < byteLen; written += zeroBuf.Length)
        {
            var chunk = (int)Math.Min(zeroBuf.Length, byteLen - written);
            await _device.WriteAsync(byteOff + written, zeroBuf, chunk, ct).ConfigureAwait(false);
        }

        // Add to catalog extent list
        _catalogExtents.Add((byteOff, byteLen));

        // Update counts
        var addedNodes = (uint)(byteLen / _nodeSize);
        _totalNodes += addedNodes;
        _freeNodes += addedNodes;

        // Extend the header node's map record (bitmap) if it can't cover totalNodes.
        // The map record stores one bit per node; if totalNodes exceeds the bitmap capacity,
        // we must rebuild the header node with a larger map record.
        await ExtendMapRecordIfNeededAsync(ct).ConfigureAwait(false);

        // Flush the B-tree header with new totalNodes/freeNodes
        await UpdateBTreeHeaderAsync(ct).ConfigureAwait(false);

        // Update the volume header catalog fork to include the new extent
        await FlushVolumeHeaderAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensure the header node's map record (record index 2) is large enough to cover _totalNodes bits.
    /// If the existing map record is too small, rebuild the header node with a larger bitmap.
    /// </summary>
    private async Task ExtendMapRecordIfNeededAsync(CancellationToken ct)
    {
        var headerBuf = await ReadCatalogNodeAsync(0, ct).ConfigureAwait(false);
        if (headerBuf is null) return;

        var numRecords = BinaryPrimitives.ReadUInt16BigEndian(headerBuf.AsSpan(10, 2));
        if (numRecords < 3) return;

        var (mapOffset, mapLen) = GetRecordOffsetAndLength(headerBuf, 2, numRecords);
        if (mapOffset < 14 || mapLen <= 0) return;

        // Current bitmap can track mapLen * 8 nodes
        var currentCapacity = (uint)(mapLen * 8);
        if (currentCapacity >= _totalNodes) return; // bitmap is already large enough

        // Calculate the new map record size needed (round up to cover _totalNodes, plus 25% headroom)
        var requiredBits = _totalNodes + (_totalNodes / 4); // 25% headroom for future growth
        var newMapLen = (int)((requiredBits + 7) / 8);

        // Ensure the new map record fits in the header node alongside records 0 and 1.
        // Record 0 = BTHeaderRec (starts at offset 14), Record 1 = user data (128 bytes typically).
        // We need: 14 (node descriptor) + record0Len + record1Len + newMapLen + offsetTable(4 entries * 2 bytes)
        var (rec0Off, rec0Len) = GetRecordOffsetAndLength(headerBuf, 0, numRecords);
        var (rec1Off, rec1Len) = GetRecordOffsetAndLength(headerBuf, 1, numRecords);

        // Clamp newMapLen so the 3 records + offset table + alignment padding fit in the node.
        // RebuildNodeRecords adds up to 1 byte padding after each record except the last,
        // so we need 2 extra bytes (padding after rec0 and rec1) in the worst case.
        var fixedOverhead = 14 + rec0Len + rec1Len + 2 * (3 + 1) + 2; // 3 records + free-space entry + alignment
        var maxMapLen = (int)_nodeSize - fixedOverhead;
        if (newMapLen > maxMapLen) newMapLen = maxMapLen;

        if (newMapLen <= mapLen) return; // can't grow further within this node

        // Build new map record: copy existing bitmap data, zero-fill the extension
        var newMapRecord = new byte[newMapLen];
        Buffer.BlockCopy(headerBuf, mapOffset, newMapRecord, 0, Math.Min(mapLen, newMapLen));
        // Extension bytes are already zero (= free nodes), which is correct

        // Rebuild the header node with the 3 records: headerRec, userData, expanded mapRecord
        var rec0Data = new byte[rec0Len];
        Buffer.BlockCopy(headerBuf, rec0Off, rec0Data, 0, rec0Len);
        var rec1Data = new byte[rec1Len];
        Buffer.BlockCopy(headerBuf, rec1Off, rec1Data, 0, rec1Len);

        var records = new List<byte[]> { rec0Data, rec1Data, newMapRecord };

        // Preserve node descriptor fields (fLink, bLink, kind=1 header, height=0)
        // but numRecords will be updated by RebuildNodeRecords
        var savedDescriptor = new byte[14];
        Buffer.BlockCopy(headerBuf, 0, savedDescriptor, 0, 14);

        // Clear and rebuild
        Array.Clear(headerBuf, 0, headerBuf.Length);
        Buffer.BlockCopy(savedDescriptor, 0, headerBuf, 0, 14);
        RebuildNodeRecords(headerBuf, records);

        await WriteCatalogNodeAsync(0, headerBuf, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build a catalog key from parentCNID and name.
    /// Key layout: keyLength(2) + parentID(4) + nameLength(2) + name(UTF-16BE)
    /// </summary>
    private static byte[] BuildCatalogKey(uint parentCnid, string name)
    {
        var nameChars = name.ToCharArray();
        var keyDataLen = 4 + 2 + nameChars.Length * 2; // parentID + nameLength + name bytes
        var keyLen = 2 + keyDataLen; // keyLength field + data
        var buf = new byte[keyLen];

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)keyDataLen);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(2, 4), parentCnid);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), (ushort)nameChars.Length);
        for (int i = 0; i < nameChars.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8 + i * 2, 2), (ushort)nameChars[i]);
        }

        return buf;
    }

    /// <summary>
    /// Build a catalog file record (248 bytes).
    /// </summary>
    private static byte[] BuildFileRecord(uint cnid, uint createDate, long dataForkSize, HfsPlusExtent[] dataExtents)
    {
        var rec = new byte[248];
        BinaryPrimitives.WriteInt16BigEndian(rec.AsSpan(0, 2), 2); // recordType = file
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(8, 4), cnid);
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(12, 4), createDate);
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(16, 4), createDate); // contentModDate
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(20, 4), createDate); // attributeModDate
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(24, 4), createDate); // accessDate

        // HFSPlusBSDInfo at +32: ownerID(4)=99, groupID(4)=99, adminFlags(1)=0, ownerFlags(1)=0, fileMode(2)=0x81A4, special(4)=0
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(32, 4), 99);
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(36, 4), 99);
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(42, 2), 0x81A4); // -rw-r--r--

        // Data fork at +88: logicalSize(8) + clumpSize(4) + totalBlocks(4) + extents[8]
        BinaryPrimitives.WriteUInt64BigEndian(rec.AsSpan(88, 8), (ulong)dataForkSize);
        uint totalBlocks = 0;
        for (int i = 0; i < 8 && i < dataExtents.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(104 + i * 8, 4), dataExtents[i].StartBlock);
            BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(108 + i * 8, 4), dataExtents[i].BlockCount);
            totalBlocks += dataExtents[i].BlockCount;
        }
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(100, 4), totalBlocks);

        return rec;
    }

    /// <summary>
    /// Build a catalog folder record (88 bytes).
    /// </summary>
    private static byte[] BuildFolderRecord(uint cnid, uint createDate)
    {
        var rec = new byte[88];
        BinaryPrimitives.WriteInt16BigEndian(rec.AsSpan(0, 2), 1); // recordType = folder
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(8, 4), cnid);
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(12, 4), createDate);
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(16, 4), createDate);
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(20, 4), createDate);
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(24, 4), createDate);

        // HFSPlusBSDInfo: ownerID=99, groupID=99, fileMode=0x41ED (drwxr-xr-x)
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(32, 4), 99);
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(36, 4), 99);
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(42, 2), 0x41ED);

        return rec;
    }

    /// <summary>
    /// Build a thread record. Type 3=folder thread, 4=file thread.
    /// </summary>
    private static byte[] BuildThreadRecord(int recordType, uint parentCnid, string nodeName)
    {
        var nameChars = nodeName.ToCharArray();
        var recLen = 2 + 2 + 4 + 2 + nameChars.Length * 2; // type + reserved + parentID + nameLen + name
        var rec = new byte[recLen];

        BinaryPrimitives.WriteInt16BigEndian(rec.AsSpan(0, 2), (short)recordType);
        // reserved(2) = 0 at offset 2
        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(4, 4), parentCnid);
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(8, 2), (ushort)nameChars.Length);
        for (int i = 0; i < nameChars.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(10 + i * 2, 2), (ushort)nameChars[i]);
        }

        return rec;
    }

    private static uint GetCurrentHfsTimestamp()
    {
        var epoch = new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var seconds = (uint)(DateTimeOffset.UtcNow - epoch).TotalSeconds;
        return seconds;
    }

    /// <summary>
    /// Compare two catalog keys: (parentCnid, name) ordering.
    /// Returns negative if a &lt; b, 0 if equal, positive if a &gt; b.
    /// </summary>
    private static int CompareCatalogKeys(byte[] nodeBuf, int recOffset, uint targetParent, string targetName)
    {
        var recParent = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOffset + 2, 4));
        if (recParent != targetParent) return recParent.CompareTo(targetParent);

        var nameLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOffset + 6, 2));
        var nameChars = new char[nameLen];
        for (int i = 0; i < nameLen; i++)
        {
            nameChars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOffset + 8 + i * 2, 2));
        }
        var recName = new string(nameChars);
        return string.Compare(recName, targetName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the free space in a node (space between last record and offset table).
    /// </summary>
    private int GetNodeFreeSpace(byte[] nodeBuf, int numRecords)
    {
        if (numRecords == 0)
        {
            // Free space = nodeSize - 14 (header) - 2 (free space offset entry)
            return (int)_nodeSize - 14 - 2;
        }

        // The last record ends at the offset pointed to by the free-space entry
        var freeSpaceEntryPos = (int)_nodeSize - 2;
        var freeSpaceOffset = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(freeSpaceEntryPos, 2));

        // Offset table starts at nodeSize - 2*(numRecords+1) growing backwards
        var offsetTableStart = (int)_nodeSize - 2 * (numRecords + 1);

        return offsetTableStart - freeSpaceOffset;
    }

    /// <summary>
    /// Insert a record (key + data) into a leaf node. If the node is full, split it.
    /// Returns true if the insertion caused a split that needs to be propagated upward.
    /// </summary>
    private async Task<(bool Split, uint NewNodeIndex, byte[] NewNodeFirstKey)?> InsertIntoLeafAsync(
        uint nodeIndex, byte[] key, byte[] data, CancellationToken ct)
    {
        var nodeBuf = await ReadCatalogNodeAsync(nodeIndex, ct).ConfigureAwait(false);
        if (nodeBuf is null) throw new InvalidOperationException($"Cannot read catalog node {nodeIndex}.");

        var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
        var recordLen = key.Length + data.Length;
        // Each record needs 2 bytes in the offset table + up to 1 byte alignment padding
        var totalNeeded = recordLen + 2 + 1;

        var freeSpace = GetNodeFreeSpace(nodeBuf, numRecords);

        if (freeSpace >= totalNeeded)
        {
            // Enough room — insert the record
            InsertRecordIntoNode(nodeBuf, numRecords, key, data);
            await WriteCatalogNodeAsync(nodeIndex, nodeBuf, ct).ConfigureAwait(false);
            return null; // no split needed
        }

        // Node is full — split
        return await SplitLeafAndInsertAsync(nodeIndex, nodeBuf, numRecords, key, data, ct).ConfigureAwait(false);
    }

    private void InsertRecordIntoNode(byte[] nodeBuf, int numRecords, byte[] key, byte[] data)
    {
        // Find the insertion point by comparing keys
        var targetParent = BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(2, 4));
        var targetNameLen = BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(6, 2));
        var targetChars = new char[targetNameLen];
        for (int i = 0; i < targetNameLen; i++)
            targetChars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(8 + i * 2, 2));
        var targetName = new string(targetChars);

        int insertAt = numRecords; // default: append at end

        for (int i = 0; i < numRecords; i++)
        {
            var (recOff, _) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
            if (recOff < 14) continue;
            var cmp = CompareCatalogKeys(nodeBuf, recOff, targetParent, targetName);
            if (cmp > 0)
            {
                insertAt = i;
                break;
            }
        }

        // Get current free space offset (where new record data goes)
        var freeSpaceEntryPos = (int)_nodeSize - 2;
        var freeSpaceOffset = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(freeSpaceEntryPos, 2));

        // We need to shift records after insertAt to make room in the offset table
        // But for simplicity, we append the record data at freeSpaceOffset and
        // rebuild the offset table

        // Write the record data (key + data) at freeSpaceOffset
        // Actually, records in a B-tree node must be in key order by their offsets.
        // The simplest correct approach: collect all existing records, insert new one, rewrite node.

        var allRecords = new List<byte[]>();
        for (int i = 0; i < numRecords; i++)
        {
            var (recOff, recLen) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
            if (recOff < 14 || recLen <= 0) continue;
            var rec = new byte[recLen];
            Buffer.BlockCopy(nodeBuf, recOff, rec, 0, recLen);
            allRecords.Add(rec);
        }

        // Build the new record
        var newRecord = new byte[key.Length + data.Length];
        Buffer.BlockCopy(key, 0, newRecord, 0, key.Length);
        Buffer.BlockCopy(data, 0, newRecord, key.Length, data.Length);

        // Insert at proper position
        if (insertAt > allRecords.Count) insertAt = allRecords.Count;
        allRecords.Insert(insertAt, newRecord);

        // Rebuild the node
        RebuildNodeRecords(nodeBuf, allRecords);
    }

    private void RebuildNodeRecords(byte[] nodeBuf, List<byte[]> records)
    {
        var newNumRecords = (ushort)records.Count;
        // Keep the node descriptor (first 14 bytes) intact except numRecords
        BinaryPrimitives.WriteUInt16BigEndian(nodeBuf.AsSpan(10, 2), newNumRecords);

        // Compute available space: between the 14-byte node descriptor and the offset table.
        // The offset table has (numRecords + 1) entries of 2 bytes each, growing backwards from the end.
        var dataStart = 14;
        var offsetTableSize = 2 * (newNumRecords + 1);
        var offsetTableStart = (int)_nodeSize - offsetTableSize;
        var availableSpace = offsetTableStart - dataStart;

        // Pre-check: verify all records + alignment fit within the available space.
        // If they don't, the caller should have split before reaching this point.
        int totalRecordBytes = 0;
        for (int i = 0; i < records.Count; i++)
        {
            totalRecordBytes += records[i].Length;
            // Account for 2-byte alignment padding (except after the last record)
            if (i < records.Count - 1 && totalRecordBytes % 2 != 0) totalRecordBytes++;
        }
        if (totalRecordBytes > availableSpace)
        {
            throw new InvalidOperationException(
                $"RebuildNodeRecords: records ({totalRecordBytes} bytes) exceed available node space ({availableSpace} bytes). " +
                $"Node should have been split first. Records={records.Count}, nodeSize={_nodeSize}.");
        }

        // Clear data area (from offset 14 to start of offset table)
        Array.Clear(nodeBuf, dataStart, offsetTableStart - dataStart);

        // Write records sequentially starting at offset 14
        var currentOffset = dataStart;
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];

            // Bounds check: ensure the record fits before the offset table
            if (currentOffset + rec.Length > offsetTableStart)
            {
                throw new InvalidOperationException(
                    $"RebuildNodeRecords: record {i} (length {rec.Length}) at offset {currentOffset} would overlap offset table at {offsetTableStart}.");
            }

            Buffer.BlockCopy(rec, 0, nodeBuf, currentOffset, rec.Length);

            // Write offset entry for this record
            // Offset table: entry for record i is at nodeSize - 2*(numRecords+1 - i)
            var entryPos = (int)_nodeSize - 2 * (newNumRecords + 1 - i);
            if (entryPos >= dataStart && entryPos + 2 <= nodeBuf.Length)
            {
                BinaryPrimitives.WriteUInt16BigEndian(nodeBuf.AsSpan(entryPos, 2), (ushort)currentOffset);
            }

            currentOffset += rec.Length;
            // Align to 2-byte boundary
            if (currentOffset % 2 != 0) currentOffset++;
        }

        // Write free-space offset (last entry in offset table, at nodeSize-2)
        var freeSpaceEntryPos = (int)_nodeSize - 2;
        if (freeSpaceEntryPos >= dataStart && freeSpaceEntryPos + 2 <= nodeBuf.Length)
        {
            BinaryPrimitives.WriteUInt16BigEndian(nodeBuf.AsSpan(freeSpaceEntryPos, 2), (ushort)currentOffset);
        }
    }

    private async Task<(bool Split, uint NewNodeIndex, byte[] NewNodeFirstKey)?> SplitLeafAndInsertAsync(
        uint nodeIndex, byte[] nodeBuf, int numRecords, byte[] key, byte[] data, CancellationToken ct)
    {
        // Collect all existing records + the new one
        var allRecords = new List<byte[]>();
        var targetParent = BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(2, 4));
        var targetNameLen = BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(6, 2));
        var targetChars = new char[targetNameLen];
        for (int i = 0; i < targetNameLen; i++)
            targetChars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(8 + i * 2, 2));
        var targetName = new string(targetChars);

        bool inserted = false;
        for (int i = 0; i < numRecords; i++)
        {
            var (recOff, recLen) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
            if (recOff < 14 || recLen <= 0) continue;

            if (!inserted)
            {
                var cmp = CompareCatalogKeys(nodeBuf, recOff, targetParent, targetName);
                if (cmp > 0)
                {
                    var newRec = new byte[key.Length + data.Length];
                    Buffer.BlockCopy(key, 0, newRec, 0, key.Length);
                    Buffer.BlockCopy(data, 0, newRec, key.Length, data.Length);
                    allRecords.Add(newRec);
                    inserted = true;
                }
            }

            var existing = new byte[recLen];
            Buffer.BlockCopy(nodeBuf, recOff, existing, 0, recLen);
            allRecords.Add(existing);
        }

        if (!inserted)
        {
            var newRec = new byte[key.Length + data.Length];
            Buffer.BlockCopy(key, 0, newRec, 0, key.Length);
            Buffer.BlockCopy(data, 0, newRec, key.Length, data.Length);
            allRecords.Add(newRec);
        }

        // Split by size: find the point where the left half fills roughly half the node.
        // This handles variable-size records (long filenames) correctly.
        var halfCapacity = ((int)_nodeSize - 14 - 4) / 2; // rough half of usable space
        var accumulated = 0;
        var splitPoint = allRecords.Count / 2; // default midpoint
        for (int i = 0; i < allRecords.Count; i++)
        {
            accumulated += allRecords[i].Length + 2; // record + offset table entry
            if (accumulated % 2 != 0) accumulated++; // alignment
            if (accumulated >= halfCapacity && i > 0)
            {
                splitPoint = i;
                break;
            }
        }
        // Ensure at least 1 record on each side
        if (splitPoint < 1) splitPoint = 1;
        if (splitPoint >= allRecords.Count) splitPoint = allRecords.Count - 1;

        var leftRecords = allRecords.GetRange(0, splitPoint);
        var rightRecords = allRecords.GetRange(splitPoint, allRecords.Count - splitPoint);

        // Allocate a new node
        var newNodeIndex = await AllocateBTreeNodeAsync(ct).ConfigureAwait(false);

        // Prepare new node buffer
        var newNodeBuf = new byte[_nodeSize];
        // BTNodeDescriptor for new leaf: kind=-1, height=1
        var oldFLink = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(newNodeBuf.AsSpan(0, 4), oldFLink); // fLink = old node's fLink
        BinaryPrimitives.WriteUInt32BigEndian(newNodeBuf.AsSpan(4, 4), nodeIndex); // bLink = old node
        newNodeBuf[8] = unchecked((byte)(-1)); // kind = leaf (-1)
        newNodeBuf[9] = 1; // height = 1

        // Update old node's fLink to point to new node
        BinaryPrimitives.WriteUInt32BigEndian(nodeBuf.AsSpan(0, 4), newNodeIndex);

        // If old fLink node exists, update its bLink to point to new node
        if (oldFLink != 0)
        {
            var fLinkBuf = await ReadCatalogNodeAsync(oldFLink, ct).ConfigureAwait(false);
            if (fLinkBuf != null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(fLinkBuf.AsSpan(4, 4), newNodeIndex);
                await WriteCatalogNodeAsync(oldFLink, fLinkBuf, ct).ConfigureAwait(false);
            }
        }

        // Rebuild records in both nodes
        RebuildNodeRecords(nodeBuf, leftRecords);
        RebuildNodeRecords(newNodeBuf, rightRecords);

        await WriteCatalogNodeAsync(nodeIndex, nodeBuf, ct).ConfigureAwait(false);
        await WriteCatalogNodeAsync(newNodeIndex, newNodeBuf, ct).ConfigureAwait(false);

        // Update lastLeafNode if needed
        if (oldFLink == 0)
        {
            _lastLeafNode = newNodeIndex;
        }

        // The first key of the new (right) node needs to be inserted into the parent index
        // Extract first key from right node
        var (firstRecOff, firstRecLen) = GetRecordOffsetAndLength(newNodeBuf, 0, rightRecords.Count);
        var keyLenField = BinaryPrimitives.ReadUInt16BigEndian(newNodeBuf.AsSpan(firstRecOff, 2));
        if (firstRecOff + 2 + keyLenField > newNodeBuf.Length)
            throw new InvalidOperationException($"Malformed HFS+ key at node record offset {firstRecOff}: keyLen={keyLenField} exceeds node buffer ({newNodeBuf.Length} bytes).");
        var firstKey = new byte[2 + keyLenField];
        Buffer.BlockCopy(newNodeBuf, firstRecOff, firstKey, 0, firstKey.Length);

        return (true, newNodeIndex, firstKey);
    }

    /// <summary>
    /// Insert an index record pointing to childNodeIndex into the specified parent index node.
    /// This is needed after a leaf (or index) split. The parentNodeIndex is obtained from
    /// FindLeafForKeyAsync so we insert at the correct level without re-traversing.
    /// If the parent itself is full, we split it and propagate upward.
    /// </summary>
    private async Task InsertIntoParentIndexAsync(uint parentNodeIndex, uint childNodeIndex,
        byte[] childFirstKey, CancellationToken ct)
    {
        var parentBuf = await ReadCatalogNodeAsync(parentNodeIndex, ct).ConfigureAwait(false);
        if (parentBuf is null) return;

        var parentNumRecords = BinaryPrimitives.ReadUInt16BigEndian(parentBuf.AsSpan(10, 2));

        // Build index record: key + child pointer (4 bytes BE)
        var ptrData = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ptrData.AsSpan(0, 4), childNodeIndex);

        var freeSpace = GetNodeFreeSpace(parentBuf, parentNumRecords);
        var totalNeeded = childFirstKey.Length + ptrData.Length + 2 + 1; // +1 for alignment padding

        if (freeSpace >= totalNeeded)
        {
            InsertRecordIntoNode(parentBuf, parentNumRecords, childFirstKey, ptrData);
            await WriteCatalogNodeAsync(parentNodeIndex, parentBuf, ct).ConfigureAwait(false);
        }
        else
        {
            // Parent index node is full — split it and propagate upward.
            // Find the grandparent by doing a full-key descent for the child's first key.
            var (_, grandparentIndex) = await FindLeafForKeyAsync(childFirstKey, ct).ConfigureAwait(false);
            // Actually we need the parent of parentNodeIndex. Walk from root to find it.
            var grandparent = await FindParentOfNodeAsync(parentNodeIndex, ct).ConfigureAwait(false);

            var splitResult = await SplitIndexAndInsertAsync(parentNodeIndex, parentBuf, parentNumRecords,
                childFirstKey, ptrData, ct).ConfigureAwait(false);

            if (splitResult is not null && grandparent != parentNodeIndex)
            {
                // Recurse: insert the new index node's first key into the grandparent
                await InsertIntoParentIndexAsync(grandparent, splitResult.Value.NewNodeIndex,
                    splitResult.Value.NewNodeFirstKey, ct).ConfigureAwait(false);
            }
            else if (splitResult is not null)
            {
                // We split the root — need to create a new root
                await CreateNewRootAsync(parentNodeIndex, splitResult.Value.NewNodeIndex,
                    splitResult.Value.NewNodeFirstKey, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Find the parent index node of the given target node by traversing from root.
    /// Returns the root node index if target is the root or not found.
    /// </summary>
    private async Task<uint> FindParentOfNodeAsync(uint targetNodeIndex, CancellationToken ct)
    {
        uint currentNode = _rootNodeIndex;
        uint parentNode = _rootNodeIndex;
        int depth = 0;

        while (depth < 20)
        {
            if (currentNode == targetNodeIndex) return parentNode;

            var nodeBuf = await ReadCatalogNodeAsync(currentNode, ct).ConfigureAwait(false);
            if (nodeBuf is null) return _rootNodeIndex;

            var kind = (sbyte)nodeBuf[8];
            if (kind == -1) return _rootNodeIndex; // leaf — target not found as index node
            if (kind != 0) return _rootNodeIndex;

            var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));

            // Check all children — if any is the target, current is the parent
            for (int i = 0; i < numRecords; i++)
            {
                var (recOff, _) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
                if (recOff < 14 || recOff + 6 > nodeBuf.Length) continue;
                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff, 2));
                if (keyLen < 6) continue;
                var ptrOffset = recOff + 2 + keyLen;
                if (ptrOffset % 2 != 0) ptrOffset++;
                if (ptrOffset + 4 > nodeBuf.Length) continue;
                var childNode = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(ptrOffset, 4));

                if (childNode == targetNodeIndex) return currentNode;
            }

            // Follow the last child pointer to continue descending (heuristic)
            uint lastChild = 0;
            for (int i = 0; i < numRecords; i++)
            {
                var (recOff, _) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
                if (recOff < 14 || recOff + 6 > nodeBuf.Length) continue;
                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff, 2));
                if (keyLen < 6) continue;
                var ptrOffset = recOff + 2 + keyLen;
                if (ptrOffset % 2 != 0) ptrOffset++;
                if (ptrOffset + 4 > nodeBuf.Length) continue;
                lastChild = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(ptrOffset, 4));
            }

            if (lastChild == 0) return _rootNodeIndex;
            parentNode = currentNode;
            currentNode = lastChild;
            depth++;
        }

        return _rootNodeIndex;
    }

    /// <summary>
    /// Split an index node and insert a new record. Returns the split info for propagation.
    /// </summary>
    private async Task<(bool Split, uint NewNodeIndex, byte[] NewNodeFirstKey)?> SplitIndexAndInsertAsync(
        uint nodeIndex, byte[] nodeBuf, int numRecords, byte[] key, byte[] data, CancellationToken ct)
    {
        // Collect all existing records + the new one, in key order
        var targetParent = BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(2, 4));
        var targetNameLen = BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(6, 2));
        var targetChars = new char[targetNameLen];
        for (int i = 0; i < targetNameLen; i++)
            targetChars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(8 + i * 2, 2));
        var targetName = new string(targetChars);

        var allRecords = new List<byte[]>();
        bool inserted = false;
        for (int i = 0; i < numRecords; i++)
        {
            var (recOff, recLen) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
            if (recOff < 14 || recLen <= 0) continue;

            if (!inserted)
            {
                var cmp = CompareCatalogKeys(nodeBuf, recOff, targetParent, targetName);
                if (cmp > 0)
                {
                    var newRec = new byte[key.Length + data.Length];
                    Buffer.BlockCopy(key, 0, newRec, 0, key.Length);
                    Buffer.BlockCopy(data, 0, newRec, key.Length, data.Length);
                    allRecords.Add(newRec);
                    inserted = true;
                }
            }

            var existing = new byte[recLen];
            Buffer.BlockCopy(nodeBuf, recOff, existing, 0, recLen);
            allRecords.Add(existing);
        }

        if (!inserted)
        {
            var newRec = new byte[key.Length + data.Length];
            Buffer.BlockCopy(key, 0, newRec, 0, key.Length);
            Buffer.BlockCopy(data, 0, newRec, key.Length, data.Length);
            allRecords.Add(newRec);
        }

        // Split by size: find the point where the left half fills roughly half the node.
        var halfCapacity = ((int)_nodeSize - 14 - 4) / 2;
        var accumulated = 0;
        var splitPoint = allRecords.Count / 2;
        for (int i = 0; i < allRecords.Count; i++)
        {
            accumulated += allRecords[i].Length + 2;
            if (accumulated % 2 != 0) accumulated++;
            if (accumulated >= halfCapacity && i > 0)
            {
                splitPoint = i;
                break;
            }
        }
        if (splitPoint < 1) splitPoint = 1;
        if (splitPoint >= allRecords.Count) splitPoint = allRecords.Count - 1;

        var leftRecords = allRecords.GetRange(0, splitPoint);
        var rightRecords = allRecords.GetRange(splitPoint, allRecords.Count - splitPoint);

        // Allocate a new node
        var newNodeIndex = await AllocateBTreeNodeAsync(ct).ConfigureAwait(false);

        // Prepare new index node buffer
        var newNodeBuf = new byte[_nodeSize];
        var height = nodeBuf[9]; // preserve height from original index node
        var oldFLink = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(newNodeBuf.AsSpan(0, 4), oldFLink);
        BinaryPrimitives.WriteUInt32BigEndian(newNodeBuf.AsSpan(4, 4), nodeIndex);
        newNodeBuf[8] = 0; // kind = index
        newNodeBuf[9] = height;

        // Update old node's fLink
        BinaryPrimitives.WriteUInt32BigEndian(nodeBuf.AsSpan(0, 4), newNodeIndex);

        // If old fLink node exists, update its bLink
        if (oldFLink != 0)
        {
            var fLinkBuf = await ReadCatalogNodeAsync(oldFLink, ct).ConfigureAwait(false);
            if (fLinkBuf != null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(fLinkBuf.AsSpan(4, 4), newNodeIndex);
                await WriteCatalogNodeAsync(oldFLink, fLinkBuf, ct).ConfigureAwait(false);
            }
        }

        RebuildNodeRecords(nodeBuf, leftRecords);
        RebuildNodeRecords(newNodeBuf, rightRecords);

        await WriteCatalogNodeAsync(nodeIndex, nodeBuf, ct).ConfigureAwait(false);
        await WriteCatalogNodeAsync(newNodeIndex, newNodeBuf, ct).ConfigureAwait(false);

        // Extract first key of new (right) node for parent insertion
        var (firstRecOff, _) = GetRecordOffsetAndLength(newNodeBuf, 0, rightRecords.Count);
        var keyLenField = BinaryPrimitives.ReadUInt16BigEndian(newNodeBuf.AsSpan(firstRecOff, 2));
        if (firstRecOff + 2 + keyLenField > newNodeBuf.Length)
            throw new InvalidOperationException($"Malformed HFS+ key at index node record offset {firstRecOff}: keyLen={keyLenField} exceeds node buffer ({newNodeBuf.Length} bytes).");
        var firstKey = new byte[2 + keyLenField];
        Buffer.BlockCopy(newNodeBuf, firstRecOff, firstKey, 0, firstKey.Length);

        return (true, newNodeIndex, firstKey);
    }

    /// <summary>
    /// Create a new root index node when the current root is split.
    /// </summary>
    private async Task CreateNewRootAsync(uint leftChild, uint rightChild, byte[] rightFirstKey, CancellationToken ct)
    {
        var newRootIndex = await AllocateBTreeNodeAsync(ct).ConfigureAwait(false);
        var newRootBuf = new byte[_nodeSize];

        // Read old root to get its height
        var oldRootBuf = await ReadCatalogNodeAsync(leftChild, ct).ConfigureAwait(false);
        var childHeight = oldRootBuf != null ? oldRootBuf[9] : (byte)1;

        // New root: kind=0 (index), height = childHeight + 1
        newRootBuf[8] = 0;
        newRootBuf[9] = (byte)(childHeight + 1);

        // Build two index records: one for left child (using its first key) and one for right child
        // Left child: extract first key from left child node
        byte[] leftFirstKey;
        if (oldRootBuf != null)
        {
            var leftNumRecs = BinaryPrimitives.ReadUInt16BigEndian(oldRootBuf.AsSpan(10, 2));
            var (leftRecOff, _) = GetRecordOffsetAndLength(oldRootBuf, 0, leftNumRecs);
            var leftKeyLen = BinaryPrimitives.ReadUInt16BigEndian(oldRootBuf.AsSpan(leftRecOff, 2));
            if (leftRecOff + 2 + leftKeyLen > oldRootBuf.Length)
                throw new InvalidOperationException($"Malformed HFS+ key at root node record offset {leftRecOff}: keyLen={leftKeyLen} exceeds node buffer ({oldRootBuf.Length} bytes).");
            leftFirstKey = new byte[2 + leftKeyLen];
            Buffer.BlockCopy(oldRootBuf, leftRecOff, leftFirstKey, 0, leftFirstKey.Length);
        }
        else
        {
            leftFirstKey = rightFirstKey; // fallback
        }

        var records = new List<byte[]>();

        // Left record: leftFirstKey + leftChild pointer
        var leftRec = new byte[leftFirstKey.Length + 4];
        Buffer.BlockCopy(leftFirstKey, 0, leftRec, 0, leftFirstKey.Length);
        BinaryPrimitives.WriteUInt32BigEndian(leftRec.AsSpan(leftFirstKey.Length, 4), leftChild);
        records.Add(leftRec);

        // Right record: rightFirstKey + rightChild pointer
        var rightRec = new byte[rightFirstKey.Length + 4];
        Buffer.BlockCopy(rightFirstKey, 0, rightRec, 0, rightFirstKey.Length);
        BinaryPrimitives.WriteUInt32BigEndian(rightRec.AsSpan(rightFirstKey.Length, 4), rightChild);
        records.Add(rightRec);

        RebuildNodeRecords(newRootBuf, records);
        await WriteCatalogNodeAsync(newRootIndex, newRootBuf, ct).ConfigureAwait(false);

        // Update tree state
        _rootNodeIndex = newRootIndex;
        _treeDepth++;
        await UpdateBTreeHeaderAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove a record from a leaf node by parentCnid and name.
    /// </summary>
    private async Task<bool> RemoveFromLeafAsync(uint parentCnid, string name, CancellationToken ct)
    {
        var removeKey = BuildCatalogKey(parentCnid, name);
        var (leafNode, _) = await FindLeafForKeyAsync(removeKey, ct).ConfigureAwait(false);

        var currentNode = leafNode;
        var visited = new HashSet<uint>();
        while (currentNode != 0)
        {
            if (!visited.Add(currentNode)) break;
            var nodeBuf = await ReadCatalogNodeAsync(currentNode, ct).ConfigureAwait(false);
            if (nodeBuf is null) break;

            var kind = (sbyte)nodeBuf[8];
            if (kind != -1) break;

            var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
            var allRecords = new List<byte[]>();
            int removeAt = -1;

            for (int i = 0; i < numRecords; i++)
            {
                var (recOff, recLen) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
                if (recOff < 14 || recLen <= 0) continue;

                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff, 2));
                if (keyLen >= 6)
                {
                    var recParent = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 2, 4));
                    if (recParent == parentCnid)
                    {
                        var recNameLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff + 6, 2));
                        var recChars = new char[recNameLen];
                        for (int c = 0; c < recNameLen; c++)
                            recChars[c] = (char)BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff + 8 + c * 2, 2));
                        var recName = new string(recChars);

                        if (string.Equals(recName, name, StringComparison.OrdinalIgnoreCase))
                        {
                            removeAt = allRecords.Count;
                        }
                    }
                    else if (recParent > parentCnid && removeAt < 0)
                    {
                        break; // past target
                    }
                }

                var rec = new byte[recLen];
                Buffer.BlockCopy(nodeBuf, recOff, rec, 0, recLen);
                allRecords.Add(rec);
            }

            if (removeAt >= 0)
            {
                allRecords.RemoveAt(removeAt);
                RebuildNodeRecords(nodeBuf, allRecords);
                await WriteCatalogNodeAsync(currentNode, nodeBuf, ct).ConfigureAwait(false);
                _leafRecords--;
                return true;
            }

            var fLink = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(0, 4));
            currentNode = fLink;
        }

        return false;
    }

    #endregion

    #region Catalog Write Operations

    /// <summary>
    /// Create a new file in the catalog and optionally write initial data.
    /// Returns the assigned CNID.
    /// </summary>
    public async Task<uint> CreateFileAsync(uint parentCnid, string name, byte[]? initialData = null, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cnid = _nextCatalogId++;
            var now = GetCurrentHfsTimestamp();

            // Allocate blocks for initial data if any
            var dataExtents = Array.Empty<HfsPlusExtent>();
            long dataSize = 0;
            if (initialData is not null && initialData.Length > 0)
            {
                dataSize = initialData.Length;
                var blocksNeeded = (uint)((dataSize + _header.BlockSize - 1) / _header.BlockSize);
                var startBlock = await AllocateBlocksAsync(blocksNeeded, ct).ConfigureAwait(false);
                dataExtents = new[] { new HfsPlusExtent(startBlock, blocksNeeded) };

                // Write data to allocated blocks
                var writeOffset = _partitionOffset + (long)startBlock * _header.BlockSize;
                // Pad to block boundary for sector-aligned writes
                var paddedSize = (int)(blocksNeeded * _header.BlockSize);
                var paddedData = new byte[paddedSize];
                Buffer.BlockCopy(initialData, 0, paddedData, 0, initialData.Length);
                await _device.WriteAsync(writeOffset, paddedData, paddedSize, ct).ConfigureAwait(false);
            }

            // Build and insert file record: key=(parentCnid, name), data=file record
            var fileKey = BuildCatalogKey(parentCnid, name);
            var fileRecord = BuildFileRecord(cnid, now, dataSize, dataExtents);

            // Find the correct leaf using FULL key descent (parentCnid + name)
            var (leafNode, parentIndex) = await FindLeafForKeyAsync(fileKey, ct).ConfigureAwait(false);
            var split = await InsertIntoLeafAsync(leafNode, fileKey, fileRecord, ct).ConfigureAwait(false);
            _leafRecords++;

            await HandleSplitAsync(split, leafNode, parentIndex, ct).ConfigureAwait(false);

            // Build and insert thread record: key=(cnid, ""), data=thread pointing back to parent
            var threadKey = BuildCatalogKey(cnid, "");
            var threadRecord = BuildThreadRecord(4, parentCnid, name); // 4 = file thread

            // Use full-key descent for thread record too (high CNID goes to correct leaf, not first)
            var (threadLeaf, threadParentIndex) = await FindLeafForKeyAsync(threadKey, ct).ConfigureAwait(false);
            var threadSplit = await InsertIntoLeafAsync(threadLeaf, threadKey, threadRecord, ct).ConfigureAwait(false);
            _leafRecords++;

            await HandleSplitAsync(threadSplit, threadLeaf, threadParentIndex, ct).ConfigureAwait(false);

            // Update B-tree header
            await UpdateBTreeHeaderAsync(ct).ConfigureAwait(false);

            // Flush volume header
            await FlushVolumeHeaderAsync(ct).ConfigureAwait(false);

            return cnid;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Create a new folder in the catalog. Returns the assigned CNID.
    /// </summary>
    public async Task<uint> CreateFolderAsync(uint parentCnid, string name, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cnid = _nextCatalogId++;
            var now = GetCurrentHfsTimestamp();

            var folderKey = BuildCatalogKey(parentCnid, name);
            var folderRecord = BuildFolderRecord(cnid, now);

            // Find the correct leaf using FULL key descent (parentCnid + name)
            var (leafNode, parentIndex) = await FindLeafForKeyAsync(folderKey, ct).ConfigureAwait(false);
            var split = await InsertIntoLeafAsync(leafNode, folderKey, folderRecord, ct).ConfigureAwait(false);
            _leafRecords++;

            await HandleSplitAsync(split, leafNode, parentIndex, ct).ConfigureAwait(false);

            // Thread record: type 3 = folder thread
            var threadKey = BuildCatalogKey(cnid, "");
            var threadRecord = BuildThreadRecord(3, parentCnid, name);

            // Use full-key descent for thread record too
            var (threadLeaf, threadParentIndex) = await FindLeafForKeyAsync(threadKey, ct).ConfigureAwait(false);
            var threadSplit = await InsertIntoLeafAsync(threadLeaf, threadKey, threadRecord, ct).ConfigureAwait(false);
            _leafRecords++;

            await HandleSplitAsync(threadSplit, threadLeaf, threadParentIndex, ct).ConfigureAwait(false);

            await UpdateBTreeHeaderAsync(ct).ConfigureAwait(false);
            await FlushVolumeHeaderAsync(ct).ConfigureAwait(false);

            return cnid;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Handle a leaf or index node split result. If the split leaf was the root (parentIndex == splitNode),
    /// create a new root index node. Otherwise insert into the parent index.
    /// </summary>
    private async Task HandleSplitAsync((bool Split, uint NewNodeIndex, byte[] NewNodeFirstKey)? split,
        uint splitNode, uint parentIndex, CancellationToken ct)
    {
        if (split is null) return;

        if (parentIndex == splitNode)
        {
            // The node that split WAS the root — create a new root above both halves
            await CreateNewRootAsync(splitNode, split.Value.NewNodeIndex, split.Value.NewNodeFirstKey, ct).ConfigureAwait(false);
        }
        else
        {
            await InsertIntoParentIndexAsync(parentIndex, split.Value.NewNodeIndex, split.Value.NewNodeFirstKey, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Delete a catalog entry (file or folder) by parentCnid and name.
    /// Frees allocated data blocks for files.
    /// </summary>
    public async Task DeleteEntryAsync(uint parentCnid, string name, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // First, find the entry to get its CNID and data fork (for freeing blocks)
            var items = await ListDirectoryAsync(parentCnid, ct).ConfigureAwait(false);
            var target = items.Find(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target is null) throw new FileNotFoundException($"Entry '{name}' not found under CNID {parentCnid}.");

            // Free data blocks if it's a file
            if (!target.IsDirectory && target.DataFork is not null)
            {
                foreach (var ext in target.DataFork.Extents)
                {
                    if (ext.BlockCount > 0)
                    {
                        await FreeBlocksAsync(ext.StartBlock, ext.BlockCount, ct).ConfigureAwait(false);
                    }
                }
            }

            // Remove the file/folder record: key=(parentCnid, name)
            await RemoveFromLeafAsync(parentCnid, name, ct).ConfigureAwait(false);

            // Remove the thread record: key=(cnid, "")
            await RemoveFromLeafAsync(target.Cnid, "", ct).ConfigureAwait(false);

            await UpdateBTreeHeaderAsync(ct).ConfigureAwait(false);
            await FlushVolumeHeaderAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Write data to a file's data fork at a given offset. Allocates new blocks if needed.
    /// </summary>
    public async Task WriteFileDataAsync(uint fileCnid, long offset, byte[] data, int count, CancellationToken ct = default)
    {
        if (count <= 0) return;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFileDataCoreAsync(fileCnid, offset, data, count, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteFileDataCoreAsync(uint fileCnid, long offset, byte[] data, int count, CancellationToken ct)
    {

        // Find the file's catalog record to get current fork data
        // We need to find it by searching for the thread record first, then the file record
        var threadKey = BuildCatalogKey(fileCnid, "");
        var (threadLeaf, _) = await FindLeafForKeyAsync(threadKey, ct).ConfigureAwait(false);

        // Find the thread record to get parentCnid and name
        uint parentCnid = 0;
        string fileName = "";
        HfsPlusForkInfo? currentFork = null;

        // Walk leaf chain to find thread record for this CNID
        var currentNode = threadLeaf;
        var visited = new HashSet<uint>();
        while (currentNode != 0 && parentCnid == 0)
        {
            if (!visited.Add(currentNode)) break;
            var nodeBuf = await ReadCatalogNodeAsync(currentNode, ct).ConfigureAwait(false);
            if (nodeBuf is null) break;
            if ((sbyte)nodeBuf[8] != -1) break;

            var numRecs = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
            for (int i = 0; i < numRecs; i++)
            {
                var (recOff, recLen) = GetRecordOffsetAndLength(nodeBuf, i, numRecs);
                if (recOff < 14 || recLen <= 0) continue;
                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff, 2));
                if (keyLen < 6) continue;

                var recParent = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 2, 4));
                if (recParent != fileCnid) continue;

                var dataOff = recOff + 2 + keyLen;
                if (dataOff % 2 != 0) dataOff++;
                if (dataOff + 2 > nodeBuf.Length) continue;
                var recType = BinaryPrimitives.ReadInt16BigEndian(nodeBuf.AsSpan(dataOff, 2));
                if (recType == 4) // file thread
                {
                    parentCnid = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(dataOff + 4, 4));
                    var nameLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(dataOff + 8, 2));
                    var chars = new char[nameLen];
                    for (int c = 0; c < nameLen; c++)
                        chars[c] = (char)BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(dataOff + 10 + c * 2, 2));
                    fileName = new string(chars);
                    break;
                }
            }

            var fLink = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(0, 4));
            currentNode = fLink;
        }

        if (parentCnid == 0)
            throw new InvalidOperationException($"Cannot find thread record for CNID {fileCnid}.");

        // Now find the file record to get current fork data
        var fileKey = BuildCatalogKey(parentCnid, fileName);
        var (fileLeaf, _fileParent) = await FindLeafForKeyAsync(fileKey, ct).ConfigureAwait(false);

        currentNode = fileLeaf;
        visited.Clear();
        uint fileNodeIndex = 0;
        int fileRecordIndex = -1;

        while (currentNode != 0)
        {
            if (!visited.Add(currentNode)) break;
            var nodeBuf = await ReadCatalogNodeAsync(currentNode, ct).ConfigureAwait(false);
            if (nodeBuf is null) break;
            if ((sbyte)nodeBuf[8] != -1) break;

            var numRecs = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
            for (int i = 0; i < numRecs; i++)
            {
                var (recOff, recLen) = GetRecordOffsetAndLength(nodeBuf, i, numRecs);
                if (recOff < 14 || recLen <= 0) continue;
                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff, 2));
                if (keyLen < 6) continue;

                var recParent = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 2, 4));
                if (recParent > parentCnid) goto doneSearch;
                if (recParent < parentCnid) continue;

                var recNameLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff + 6, 2));
                var recChars = new char[recNameLen];
                for (int c = 0; c < recNameLen; c++)
                    recChars[c] = (char)BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff + 8 + c * 2, 2));
                var recName = new string(recChars);

                if (!string.Equals(recName, fileName, StringComparison.OrdinalIgnoreCase)) continue;

                var dataOff = recOff + 2 + keyLen;
                if (dataOff % 2 != 0) dataOff++;
                if (dataOff + 248 > nodeBuf.Length) continue;
                var recType = BinaryPrimitives.ReadInt16BigEndian(nodeBuf.AsSpan(dataOff, 2));
                if (recType == 2) // file record
                {
                    currentFork = ParseForkData(nodeBuf, dataOff + 88);
                    fileNodeIndex = currentNode;
                    fileRecordIndex = i;
                    goto doneSearch;
                }
            }

            var fl = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(0, 4));
            currentNode = fl;
        }

        doneSearch:
        if (currentFork is null)
            throw new InvalidOperationException($"Cannot find file record for CNID {fileCnid}.");

        // Determine if we need more blocks
        var requiredSize = offset + count;
        var currentAllocatedBytes = 0L;
        foreach (var ext in currentFork.Extents)
            currentAllocatedBytes += (long)ext.BlockCount * _header.BlockSize;

        if (requiredSize > currentAllocatedBytes)
        {
            // Need to allocate more blocks
            var additionalBytes = requiredSize - currentAllocatedBytes;
            var additionalBlocks = (uint)((additionalBytes + _header.BlockSize - 1) / _header.BlockSize);

            // Find first empty extent slot BEFORE allocating, so we can refuse
            // cleanly instead of leaking allocated blocks if the fork is full.
            var extents = new List<HfsPlusExtent>(currentFork.Extents);
            int emptySlot = -1;
            for (int i = 0; i < extents.Count; i++)
            {
                if (extents[i].BlockCount == 0) { emptySlot = i; break; }
            }
            if (emptySlot < 0)
            {
                throw new InvalidOperationException(
                    $"WriteFileData: file CNID {fileCnid} already uses all {extents.Count} inline extents; " +
                    "extents-overflow file is not supported on write.");
            }

            var newStart = await AllocateBlocksAsync(additionalBlocks, ct).ConfigureAwait(false);
            extents[emptySlot] = new HfsPlusExtent(newStart, additionalBlocks);

            currentFork = new HfsPlusForkInfo(Math.Max(currentFork.LogicalSize, requiredSize), extents.ToArray());

            // Update the file record on disk with new fork data
            await UpdateFileRecordForkAsync(fileNodeIndex, fileRecordIndex, currentFork, ct).ConfigureAwait(false);
        }

        // Write data through the fork extents
        var writeOffset = offset;
        var dataWritten = 0;
        foreach (var ext in currentFork.Extents)
        {
            if (ext.BlockCount == 0) break;
            var extByteLen = (long)ext.BlockCount * _header.BlockSize;
            if (writeOffset >= extByteLen)
            {
                writeOffset -= extByteLen;
                continue;
            }

            var diskOffset = _partitionOffset + (long)ext.StartBlock * _header.BlockSize + writeOffset;
            var toWrite = (int)Math.Min(count - dataWritten, extByteLen - writeOffset);

            // Pad to sector alignment
            var alignedLen = ((toWrite + 511) / 512) * 512;
            var writeBuf = new byte[alignedLen];
            Buffer.BlockCopy(data, dataWritten, writeBuf, 0, toWrite);
            await _device.WriteAsync(diskOffset, writeBuf, alignedLen, ct).ConfigureAwait(false);

            dataWritten += toWrite;
            writeOffset = 0;
            if (dataWritten >= count) break;
        }

        // Update logical size if needed
        if (requiredSize > currentFork.LogicalSize)
        {
            currentFork = currentFork with { LogicalSize = requiredSize };
            await UpdateFileRecordForkAsync(fileNodeIndex, fileRecordIndex, currentFork, ct).ConfigureAwait(false);
        }

        await FlushVolumeHeaderAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Set the size of a file, truncating or extending as needed.
    /// </summary>
    public async Task SetFileSizeAsync(uint fileCnid, long newSize, CancellationToken ct = default)
    {
        if (newSize < 0) throw new ArgumentOutOfRangeException(nameof(newSize));
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SetFileSizeCoreAsync(fileCnid, newSize, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SetFileSizeCoreAsync(uint fileCnid, long newSize, CancellationToken ct)
    {
        // Find file record via thread record (same logic as WriteFileDataAsync)
        // For brevity, we use the simplified approach: find the record and update it

        // Locate file record
        // Search for this CNID's thread to find parent+name
        var threadKey = BuildCatalogKey(fileCnid, "");
        var (tLeaf, _) = await FindLeafForKeyAsync(threadKey, ct).ConfigureAwait(false);

        uint parentCnid = 0;
        string fileName = "";
        var cn = tLeaf;
        var vis = new HashSet<uint>();
        while (cn != 0 && parentCnid == 0)
        {
            if (!vis.Add(cn)) break;
            var nb = await ReadCatalogNodeAsync(cn, ct).ConfigureAwait(false);
            if (nb is null || (sbyte)nb[8] != -1) break;

            var nr = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(10, 2));
            for (int i = 0; i < nr; i++)
            {
                var (ro, rl) = GetRecordOffsetAndLength(nb, i, nr);
                if (ro < 14 || rl <= 0) continue;
                var kl = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(ro, 2));
                if (kl < 6) continue;
                var rp = BinaryPrimitives.ReadUInt32BigEndian(nb.AsSpan(ro + 2, 4));
                if (rp != fileCnid) continue;
                var dOff = ro + 2 + kl;
                if (dOff % 2 != 0) dOff++;
                if (dOff + 10 > nb.Length) continue;
                var rt = BinaryPrimitives.ReadInt16BigEndian(nb.AsSpan(dOff, 2));
                if (rt == 4)
                {
                    parentCnid = BinaryPrimitives.ReadUInt32BigEndian(nb.AsSpan(dOff + 4, 4));
                    var nl = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(dOff + 8, 2));
                    var ch = new char[nl];
                    for (int c = 0; c < nl; c++)
                        ch[c] = (char)BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(dOff + 10 + c * 2, 2));
                    fileName = new string(ch);
                }
            }
            cn = BinaryPrimitives.ReadUInt32BigEndian(nb.AsSpan(0, 4));
        }

        if (parentCnid == 0)
            throw new InvalidOperationException($"Cannot find thread record for CNID {fileCnid}.");

        // Find the file record
        var fileKey = BuildCatalogKey(parentCnid, fileName);
        var (fileLeaf, _fileParent) = await FindLeafForKeyAsync(fileKey, ct).ConfigureAwait(false);

        cn = fileLeaf;
        vis.Clear();
        HfsPlusForkInfo? fork = null;
        uint fNodeIdx = 0;
        int fRecIdx = -1;

        while (cn != 0)
        {
            if (!vis.Add(cn)) break;
            var nb = await ReadCatalogNodeAsync(cn, ct).ConfigureAwait(false);
            if (nb is null || (sbyte)nb[8] != -1) break;

            var nr = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(10, 2));
            for (int i = 0; i < nr; i++)
            {
                var (ro, rl) = GetRecordOffsetAndLength(nb, i, nr);
                if (ro < 14 || rl <= 0) continue;
                var kl = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(ro, 2));
                if (kl < 6) continue;
                var rp = BinaryPrimitives.ReadUInt32BigEndian(nb.AsSpan(ro + 2, 4));
                if (rp > parentCnid) goto done2;
                if (rp < parentCnid) continue;

                var rnl = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(ro + 6, 2));
                var rch = new char[rnl];
                for (int c = 0; c < rnl; c++)
                    rch[c] = (char)BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(ro + 8 + c * 2, 2));
                var rn = new string(rch);
                if (!string.Equals(rn, fileName, StringComparison.OrdinalIgnoreCase)) continue;

                var dOff = ro + 2 + kl;
                if (dOff % 2 != 0) dOff++;
                if (dOff + 248 > nb.Length) continue;
                var rt = BinaryPrimitives.ReadInt16BigEndian(nb.AsSpan(dOff, 2));
                if (rt == 2)
                {
                    fork = ParseForkData(nb, dOff + 88);
                    fNodeIdx = cn;
                    fRecIdx = i;
                    goto done2;
                }
            }
            cn = BinaryPrimitives.ReadUInt32BigEndian(nb.AsSpan(0, 4));
        }

        done2:
        if (fork is null)
            throw new InvalidOperationException($"Cannot find file record for CNID {fileCnid}.");

        var currentAllocated = 0L;
        foreach (var ext in fork.Extents)
            currentAllocated += (long)ext.BlockCount * _header.BlockSize;

        if (newSize > currentAllocated)
        {
            // Extend: allocate more blocks
            var need = (uint)((newSize - currentAllocated + _header.BlockSize - 1) / _header.BlockSize);
            var start = await AllocateBlocksAsync(need, ct).ConfigureAwait(false);
            var extents = new List<HfsPlusExtent>(fork.Extents);
            for (int i = 0; i < extents.Count; i++)
            {
                if (extents[i].BlockCount == 0)
                {
                    extents[i] = new HfsPlusExtent(start, need);
                    break;
                }
            }
            fork = new HfsPlusForkInfo(newSize, extents.ToArray());
        }
        else if (newSize < fork.LogicalSize)
        {
            // Truncate: free excess blocks
            var keepBlocks = (uint)((newSize + _header.BlockSize - 1) / _header.BlockSize);
            uint accumulated = 0;
            var extents = new List<HfsPlusExtent>(fork.Extents);
            for (int i = 0; i < extents.Count; i++)
            {
                if (extents[i].BlockCount == 0) break;
                accumulated += extents[i].BlockCount;
                if (accumulated > keepBlocks)
                {
                    // This extent has blocks to free
                    var excess = accumulated - keepBlocks;
                    var keep = extents[i].BlockCount - excess;
                    if (keep > 0)
                    {
                        // Free the tail of this extent
                        await FreeBlocksAsync(extents[i].StartBlock + keep, excess, ct).ConfigureAwait(false);
                        extents[i] = new HfsPlusExtent(extents[i].StartBlock, keep);
                    }
                    else
                    {
                        // Free the entire extent
                        await FreeBlocksAsync(extents[i].StartBlock, extents[i].BlockCount, ct).ConfigureAwait(false);
                        extents[i] = new HfsPlusExtent(0, 0);
                    }

                    // Free all subsequent extents
                    for (int j = i + 1; j < extents.Count; j++)
                    {
                        if (extents[j].BlockCount > 0)
                        {
                            await FreeBlocksAsync(extents[j].StartBlock, extents[j].BlockCount, ct).ConfigureAwait(false);
                            extents[j] = new HfsPlusExtent(0, 0);
                        }
                    }
                    break;
                }
            }
            fork = new HfsPlusForkInfo(newSize, extents.ToArray());
        }
        else
        {
            // Same allocation, just update logical size
            fork = fork with { LogicalSize = newSize };
        }

        await UpdateFileRecordForkAsync(fNodeIdx, fRecIdx, fork, ct).ConfigureAwait(false);
        await FlushVolumeHeaderAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Update a file record's data fork on disk at the given node and record index.
    /// </summary>
    private async Task UpdateFileRecordForkAsync(uint nodeIndex, int recordIndex, HfsPlusForkInfo fork, CancellationToken ct)
    {
        var nodeBuf = await ReadCatalogNodeAsync(nodeIndex, ct).ConfigureAwait(false);
        if (nodeBuf is null) return;

        var numRecs = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
        var (recOff, recLen) = GetRecordOffsetAndLength(nodeBuf, recordIndex, numRecs);
        if (recOff < 14 || recLen <= 0) return;

        var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff, 2));
        var dataOff = recOff + 2 + keyLen;
        if (dataOff % 2 != 0) dataOff++;

        // Data fork starts at dataOff + 88
        var forkOff = dataOff + 88;
        if (forkOff + 80 > nodeBuf.Length) return;

        // Write fork data: logicalSize(8) + clumpSize(4) + totalBlocks(4) + extents[8]
        BinaryPrimitives.WriteUInt64BigEndian(nodeBuf.AsSpan(forkOff, 8), (ulong)fork.LogicalSize);
        uint totalBlocks = 0;
        for (int i = 0; i < 8; i++)
        {
            if (i < fork.Extents.Length)
            {
                BinaryPrimitives.WriteUInt32BigEndian(nodeBuf.AsSpan(forkOff + 16 + i * 8, 4), fork.Extents[i].StartBlock);
                BinaryPrimitives.WriteUInt32BigEndian(nodeBuf.AsSpan(forkOff + 20 + i * 8, 4), fork.Extents[i].BlockCount);
                totalBlocks += fork.Extents[i].BlockCount;
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(nodeBuf.AsSpan(forkOff + 16 + i * 8, 4), 0);
                BinaryPrimitives.WriteUInt32BigEndian(nodeBuf.AsSpan(forkOff + 20 + i * 8, 4), 0);
            }
        }
        BinaryPrimitives.WriteUInt32BigEndian(nodeBuf.AsSpan(forkOff + 12, 4), totalBlocks);

        // Update contentModDate
        var now = GetCurrentHfsTimestamp();
        BinaryPrimitives.WriteUInt32BigEndian(nodeBuf.AsSpan(dataOff + 16, 4), now);

        await WriteCatalogNodeAsync(nodeIndex, nodeBuf, ct).ConfigureAwait(false);
    }

    #endregion

    // ─── END WRITE SUPPORT ────────────────────────────────────────────────────

    private async Task<uint> FindFirstLeafForParentAsync(uint parentCnid, CancellationToken ct)
    {
        // Use the full key comparison with (parentCnid, "") to find the FIRST leaf
        // that could contain records for this parent. The empty name sorts before all
        // real names, so this finds the correct starting position.
        var searchKey = BuildCatalogKey(parentCnid, "");
        var (leaf, _) = await FindLeafForKeyAsync(searchKey, ct).ConfigureAwait(false);
        return leaf;
    }

    /// <summary>
    /// Proper B-tree descent using the FULL catalog key (parentCnid + name) to find the
    /// correct leaf node for insertion. Also returns the parent index node so that split
    /// propagation can insert directly into the right place.
    /// </summary>
    private async Task<(uint LeafNode, uint ParentNode)> FindLeafForKeyAsync(byte[] catalogKey, CancellationToken ct)
    {
        var targetParent = BinaryPrimitives.ReadUInt32BigEndian(catalogKey.AsSpan(2, 4));
        var targetNameLen = BinaryPrimitives.ReadUInt16BigEndian(catalogKey.AsSpan(6, 2));
        var targetChars = new char[targetNameLen];
        for (int i = 0; i < targetNameLen; i++)
            targetChars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(catalogKey.AsSpan(8 + i * 2, 2));
        var targetName = new string(targetChars);

        uint currentNode = _rootNodeIndex;
        uint parentNode = _rootNodeIndex;
        int depth = 0;

        while (depth < 20)
        {
            var nodeBuf = await ReadCatalogNodeAsync(currentNode, ct).ConfigureAwait(false);
            if (nodeBuf is null) return (_firstLeafNodeIndex, _rootNodeIndex);

            var kind = (sbyte)nodeBuf[8];
            if (kind == -1) return (currentNode, parentNode); // found leaf

            if (kind != 0) return (_firstLeafNodeIndex, _rootNodeIndex); // unexpected node kind

            parentNode = currentNode;
            var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
            uint bestChild = 0;

            for (int i = 0; i < numRecords; i++)
            {
                var (recOff, _) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
                if (recOff < 14 || recOff + 6 > nodeBuf.Length) continue;
                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff, 2));
                if (keyLen < 6) continue;

                var ptrOffset = recOff + 2 + keyLen;
                if (ptrOffset % 2 != 0) ptrOffset++;
                if (ptrOffset + 4 > nodeBuf.Length) continue;

                var childNode = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(ptrOffset, 4));
                var cmp = CompareCatalogKeys(nodeBuf, recOff, targetParent, targetName);
                if (cmp <= 0)
                    bestChild = childNode;
                else
                    break;
            }

            if (bestChild == 0) return (_firstLeafNodeIndex, _rootNodeIndex);
            currentNode = bestChild;
            depth++;
        }

        return (_firstLeafNodeIndex, _rootNodeIndex);
    }

    private async Task<byte[]?> ReadCatalogNodeAsync(uint nodeIndex, CancellationToken ct)
    {
        var nodeOffset = (long)nodeIndex * _nodeSize;
        var buf = new byte[_nodeSize];

        // Map node offset to physical disk offset through the catalog extent list
        var remaining = nodeOffset;
        foreach (var (byteOff, byteLen) in _catalogExtents)
        {
            if (remaining < byteLen)
            {
                var physicalOffset = byteOff + remaining;
                var read = await _device.ReadAsync(physicalOffset, buf, buf.Length, ct).ConfigureAwait(false);
                return read >= _nodeSize ? buf : null;
            }
            remaining -= byteLen;
        }

        return null; // node beyond extent range
    }

    private (int Offset, int Length) GetRecordOffsetAndLength(byte[] nodeBuf, int recordIndex, int numRecords)
    {
        // Record offset table is at the END of the node, growing backwards.
        // Each entry is a UInt16 BE offset from the start of the node.
        // The final entry stores the start of free space; the preceding entries
        // store record offsets in reverse order, with the first record furthest away.
        var freeSpacePos = (int)_nodeSize - 2;
        var entryPos = freeSpacePos - ((numRecords - recordIndex) * 2);
        var nextEntryPos = entryPos + 2;

        if (entryPos < 14 || entryPos + 2 > nodeBuf.Length) return (0, 0);
        if (nextEntryPos < 0 || nextEntryPos + 2 > nodeBuf.Length) return (0, 0);

        var offset = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(entryPos, 2));
        var nextOffset = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(nextEntryPos, 2));

        if (offset < 14 || nextOffset <= offset) return (offset, 0);
        return (offset, nextOffset - offset);
    }

    private static HfsPlusForkInfo ParseForkData(byte[] buf, int offset)
    {
        // HFSPlusForkData: logicalSize(8) clumpSize(4) totalBlocks(4) extents[8](startBlock(4)+blockCount(4))
        if (offset + 80 > buf.Length) return new HfsPlusForkInfo(0, Array.Empty<HfsPlusExtent>());

        var logicalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(offset, 8));
        var extents = new HfsPlusExtent[8];
        for (int i = 0; i < 8; i++)
        {
            var extOff = offset + 16 + i * 8;
            var startBlock = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(extOff, 4));
            var blockCount = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(extOff + 4, 4));
            extents[i] = new HfsPlusExtent(startBlock, blockCount);
        }

        return new HfsPlusForkInfo(logicalSize, extents);
    }

    private static DateTimeOffset ReadHfsTimestamp(byte[] buf, int offset)
    {
        // HFS+ timestamps are seconds since 1904-01-01 00:00:00 UTC
        if (offset + 4 > buf.Length) return DateTimeOffset.MinValue;
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(offset, 4));
        if (seconds == 0) return DateTimeOffset.MinValue;
        try
        {
            return new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static HfsPlusVolumeHeader ParseVolumeHeader(byte[] buf)
    {
        var signature = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0, 2));
        var blockSize = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(40, 4));
        var totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(44, 4));
        var freeBlocks = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(48, 4));

        // HFS+ volume header fork data offsets:
        //   allocationFile at 112 (0x70), extentsFile at 192 (0xC0),
        //   catalogFile at 272 (0x110), attributesFile at 352 (0x160), startupFile at 432 (0x1B0)
        // Each ForkData is 80 bytes: logicalSize(8) + clumpSize(4) + totalBlocks(4) + extents[8](startBlock(4)+blockCount(4))
        const int catalogForkOffset = 272; // 0x110
        var catalogExtents = new HfsPlusExtent[8];
        for (int i = 0; i < 8; i++)
        {
            var extOff = catalogForkOffset + 16 + i * 8; // skip logicalSize(8) + clumpSize(4) + totalBlocks(4)
            var startBlock = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(extOff, 4));
            var blockCnt = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(extOff + 4, 4));
            catalogExtents[i] = new HfsPlusExtent(startBlock, blockCnt);
        }

        var catalogLogicalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(catalogForkOffset, 8));

        return new HfsPlusVolumeHeader(signature, blockSize, totalBlocks, freeBlocks, catalogLogicalSize, catalogExtents);
    }

    // ─── DIAGNOSTICS ──────────────────────────────────────────────────────────

    /// <summary>
    /// Dump B-tree structure for diagnostics. Returns info about nodes and records.
    /// </summary>
    public async Task<BTreeDiagnostics> GetBTreeDiagnosticsAsync(CancellationToken ct = default)
    {
        var diag = new BTreeDiagnostics
        {
            RootNodeIndex = _rootNodeIndex,
            FirstLeafNodeIndex = _firstLeafNodeIndex,
            LastLeafNode = _lastLeafNode,
            TotalNodes = _totalNodes,
            FreeNodes = _freeNodes,
            LeafRecords = _leafRecords,
            CatalogExtentCount = _catalogExtents.Count,
        };

        // Walk leaf chain from firstLeaf via fLink
        var currentNode = _firstLeafNodeIndex;
        var visited = new HashSet<uint>();
        while (currentNode != 0)
        {
            if (!visited.Add(currentNode)) { diag.HasCycle = true; break; }
            var nodeBuf = await ReadCatalogNodeAsync(currentNode, ct).ConfigureAwait(false);
            if (nodeBuf is null) { diag.UnreadableNodes.Add(currentNode); break; }

            var kind = (sbyte)nodeBuf[8];
            var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
            var fLink = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(0, 4));

            var nodeInfo = new BTreeNodeInfo
            {
                NodeIndex = currentNode,
                Kind = kind,
                NumRecords = numRecords,
                FLink = fLink,
            };

            for (int i = 0; i < numRecords; i++)
            {
                var (recOff, recLen) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
                if (recOff < 14 || recLen <= 0) continue;
                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff, 2));
                if (keyLen >= 6)
                {
                    var parentCnid = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 2, 4));
                    nodeInfo.RecordParentCnids.Add(parentCnid);
                }
            }

            diag.LeafNodes.Add(nodeInfo);
            currentNode = fLink;
        }

        // Also walk index nodes from root
        var indexQ = new Queue<uint>();
        if (_rootNodeIndex != 0) indexQ.Enqueue(_rootNodeIndex);
        var visitedIndex = new HashSet<uint>();
        while (indexQ.Count > 0)
        {
            var nodeIdx = indexQ.Dequeue();
            if (!visitedIndex.Add(nodeIdx)) continue;
            var nodeBuf = await ReadCatalogNodeAsync(nodeIdx, ct).ConfigureAwait(false);
            if (nodeBuf is null) continue;
            var kind = (sbyte)nodeBuf[8];
            if (kind != 0) continue; // only index nodes

            var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
            var indexInfo = new BTreeIndexNodeInfo { NodeIndex = nodeIdx, NumRecords = numRecords };

            for (int i = 0; i < numRecords; i++)
            {
                var (recOff, _) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
                if (recOff < 14) continue;
                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff, 2));
                if (keyLen < 6) continue;
                var parentCnid = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOff + 2, 4));
                var ptrOff = recOff + 2 + keyLen;
                if (ptrOff % 2 != 0) ptrOff++;
                if (ptrOff + 4 > nodeBuf.Length) continue;
                var childNode = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(ptrOff, 4));

                // Extract name from key
                var nameLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff + 6, 2));
                var nameChars = new char[nameLen];
                for (int c = 0; c < nameLen; c++)
                    nameChars[c] = (char)BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOff + 8 + c * 2, 2));
                var name = new string(nameChars);

                indexInfo.Children.Add((parentCnid, name, childNode));
                indexQ.Enqueue(childNode);
            }

            diag.IndexNodes.Add(indexInfo);
        }

        return diag;
    }

    // ─── FORMAT SUPPORT ────────────────────────────────────────────────────────

    /// <summary>
    /// Write a fresh HFS+ filesystem to the device at the given partition offset.
    /// Creates volume header, allocation bitmap, extents overflow B-tree,
    /// and catalog B-tree with root folder (CNID 2).
    /// </summary>
    public static async Task FormatAsync(IRawBlockDevice device, long partitionOffset, long partitionSize,
        string volumeName, CancellationToken ct = default)
    {
        if (!device.CanWrite) throw new InvalidOperationException("Device must be writable for format.");
        if (partitionSize < 1024 * 1024)
            throw new ArgumentException("Partition too small for HFS+; need at least 1 MB.", nameof(partitionSize));

        const uint blockSize = 4096;
        const uint nodeSize = 8192;
        var totalBlocks = (uint)(partitionSize / blockSize);

        // ── Layout plan ──
        // Block 0: reserved (contains boot sectors at partition+0 and partition+1024 for VH)
        // Block 1: allocation bitmap file (we allocate enough blocks)
        // Then: extents overflow B-tree (1 block for header node only since nodeSize <= 2*blockSize)
        // Then: catalog B-tree (128 nodes = 128*8192/4096 = 256 blocks)
        // Rest: free space

        // Allocation bitmap: needs ceil(totalBlocks / 8) bytes, stored in whole blocks
        var bitmapBytes = (totalBlocks + 7) / 8;
        var bitmapBlocks = (uint)((bitmapBytes + blockSize - 1) / blockSize);

        // Extents overflow: 1 node = 8192 bytes = 2 blocks
        var extentsBlocks = (uint)(nodeSize / blockSize);
        if (extentsBlocks == 0) extentsBlocks = 1;
        var extentsTotalNodes = 1u; // just header node

        // Catalog: 128 nodes minimum = 128 * 8192 / 4096 = 256 blocks
        var catalogNodeCount = 128u;
        var catalogBlocks = (uint)(catalogNodeCount * nodeSize / blockSize);

        // Block assignments:
        // Block 0: reserved (VH lives at partitionOffset+1024, inside block 0 assuming blockSize>=2048)
        // Block 1 .. 1+bitmapBlocks-1: allocation bitmap
        var bitmapStartBlock = 1u;
        var extentsStartBlock = bitmapStartBlock + bitmapBlocks;
        var catalogStartBlock = extentsStartBlock + extentsBlocks;
        var firstFreeBlock = catalogStartBlock + catalogBlocks;

        // How many blocks are used by metadata
        var usedBlocks = firstFreeBlock; // blocks 0..firstFreeBlock-1
        var freeBlocks = totalBlocks - usedBlocks;

        // ── Zero-fill metadata area ──
        var zeroBuf = new byte[blockSize];
        for (uint b = 0; b < firstFreeBlock && b < totalBlocks; b++)
        {
            var off = partitionOffset + (long)b * blockSize;
            await device.WriteAsync(off, zeroBuf, (int)blockSize, ct).ConfigureAwait(false);
        }

        // ── 1. Write allocation bitmap ──
        // Mark blocks 0..firstFreeBlock-1 as used (bit=1, MSB first)
        var bitmap = new byte[bitmapBlocks * blockSize];
        for (uint b = 0; b < usedBlocks; b++)
        {
            var byteIdx = b / 8;
            var bitIdx = 7 - (int)(b % 8);
            bitmap[byteIdx] |= (byte)(1 << bitIdx);
        }

        // Write bitmap to disk
        for (uint i = 0; i < bitmapBlocks; i++)
        {
            var off = partitionOffset + (long)(bitmapStartBlock + i) * blockSize;
            var chunk = new byte[blockSize];
            Buffer.BlockCopy(bitmap, (int)(i * blockSize), chunk, 0, (int)blockSize);
            await device.WriteAsync(off, chunk, (int)blockSize, ct).ConfigureAwait(false);
        }

        // ── 2. Write extents overflow B-tree (header node only) ──
        var extentsNode = new byte[nodeSize];
        // Node descriptor: fLink(4)=0, bLink(4)=0, kind(1)=1(header), height(1)=0, numRecords(2)=3, reserved(2)=0
        extentsNode[8] = 1; // kind = header node
        BinaryPrimitives.WriteUInt16BigEndian(extentsNode.AsSpan(10, 2), 3); // numRecords

        // BTHeaderRec at offset 14 (record 0):
        // treeDepth(2)=0, rootNode(4)=0, leafRecords(4)=0, firstLeafNode(4)=0, lastLeafNode(4)=0
        // nodeSize(2), maxKeyLength(2)=10, totalNodes(4), freeNodes(4), reserved(2), clumpSize(4), btreeType(1), keyCompareType(1), attributes(4)
        BinaryPrimitives.WriteUInt16BigEndian(extentsNode.AsSpan(14 + 18, 2), (ushort)nodeSize); // nodeSize
        BinaryPrimitives.WriteUInt16BigEndian(extentsNode.AsSpan(14 + 20, 2), 10); // maxKeyLength for extents
        BinaryPrimitives.WriteUInt32BigEndian(extentsNode.AsSpan(14 + 22, 4), extentsTotalNodes); // totalNodes
        BinaryPrimitives.WriteUInt32BigEndian(extentsNode.AsSpan(14 + 26, 4), extentsTotalNodes - 1); // freeNodes (all but header)

        // Record 0 = BTHeaderRec: 110 bytes (offsets 14..123)
        // Record 1 = reserved user data record: 128 bytes (offsets 124..251)
        // Record 2 = map record: rest of node up to offset table

        // Offset table (at end of node, growing backwards):
        // 4 entries: rec0, rec1, rec2, free-space
        var extNumRec = 3;
        var extRec0Off = 14;
        var extRec1Off = 14 + 110; // 124
        var extRec2Off = extRec1Off + 128; // 252

        // Map record for extents: mark node 0 as used (bit 7 of byte 0)
        extentsNode[extRec2Off] = 0x80; // node 0 allocated

        // Free space offset: end of map record
        var extMapLen = (int)nodeSize - (extNumRec + 1) * 2 - extRec2Off;
        var extFreeOff = extRec2Off + extMapLen;

        // Write offset table at end of node
        // Entry positions: nodeSize - 2*(numRecords+1-i) for record i
        // Record 0: nodeSize - 2*(3+1-0) = nodeSize - 8
        // Record 1: nodeSize - 2*(3+1-1) = nodeSize - 6
        // Record 2: nodeSize - 2*(3+1-2) = nodeSize - 4
        // Free space: nodeSize - 2 = nodeSize - 2
        BinaryPrimitives.WriteUInt16BigEndian(extentsNode.AsSpan((int)nodeSize - 8, 2), (ushort)extRec0Off);
        BinaryPrimitives.WriteUInt16BigEndian(extentsNode.AsSpan((int)nodeSize - 6, 2), (ushort)extRec1Off);
        BinaryPrimitives.WriteUInt16BigEndian(extentsNode.AsSpan((int)nodeSize - 4, 2), (ushort)extRec2Off);
        BinaryPrimitives.WriteUInt16BigEndian(extentsNode.AsSpan((int)nodeSize - 2, 2), (ushort)extFreeOff);

        // Write extents B-tree node to disk
        var extDiskOff = partitionOffset + (long)extentsStartBlock * blockSize;
        await device.WriteAsync(extDiskOff, extentsNode, (int)nodeSize, ct).ConfigureAwait(false);

        // ── 3. Write catalog B-tree ──
        // Node 0: header node
        // Node 1: root leaf node with root folder records
        var catHeaderNode = new byte[nodeSize];
        catHeaderNode[8] = 1; // kind = header node
        BinaryPrimitives.WriteUInt16BigEndian(catHeaderNode.AsSpan(10, 2), 3); // numRecords

        // BTHeaderRec at offset 14:
        var catTreeDepth = (ushort)1;
        var catRootNode = 1u;
        var catLeafRecords = 2u; // root folder record + thread record
        var catFirstLeaf = 1u;
        var catLastLeaf = 1u;

        BinaryPrimitives.WriteUInt16BigEndian(catHeaderNode.AsSpan(14, 2), catTreeDepth);
        BinaryPrimitives.WriteUInt32BigEndian(catHeaderNode.AsSpan(16, 4), catRootNode);
        BinaryPrimitives.WriteUInt32BigEndian(catHeaderNode.AsSpan(20, 4), catLeafRecords);
        BinaryPrimitives.WriteUInt32BigEndian(catHeaderNode.AsSpan(24, 4), catFirstLeaf);
        BinaryPrimitives.WriteUInt32BigEndian(catHeaderNode.AsSpan(28, 4), catLastLeaf);
        BinaryPrimitives.WriteUInt16BigEndian(catHeaderNode.AsSpan(14 + 18, 2), (ushort)nodeSize);
        BinaryPrimitives.WriteUInt16BigEndian(catHeaderNode.AsSpan(14 + 20, 2), 516); // maxKeyLength for catalog
        BinaryPrimitives.WriteUInt32BigEndian(catHeaderNode.AsSpan(14 + 22, 4), catalogNodeCount);
        BinaryPrimitives.WriteUInt32BigEndian(catHeaderNode.AsSpan(14 + 26, 4), catalogNodeCount - 2); // header + root leaf used

        // Record 0 = BTHeaderRec: 110 bytes (14..123)
        // Record 1 = reserved: 128 bytes (124..251)
        // Record 2 = map record: 252 to (nodeSize - 8)
        var catRec0Off = 14;
        var catRec1Off = 14 + 110; // 124
        var catRec2Off = catRec1Off + 128; // 252
        var catMapLen = (int)nodeSize - 4 * 2 - catRec2Off;
        var catFreeOff = catRec2Off + catMapLen;

        // Map record: mark nodes 0 and 1 as used
        catHeaderNode[catRec2Off] = 0xC0; // bits 7 and 6 set = nodes 0 and 1

        // Offset table
        BinaryPrimitives.WriteUInt16BigEndian(catHeaderNode.AsSpan((int)nodeSize - 8, 2), (ushort)catRec0Off);
        BinaryPrimitives.WriteUInt16BigEndian(catHeaderNode.AsSpan((int)nodeSize - 6, 2), (ushort)catRec1Off);
        BinaryPrimitives.WriteUInt16BigEndian(catHeaderNode.AsSpan((int)nodeSize - 4, 2), (ushort)catRec2Off);
        BinaryPrimitives.WriteUInt16BigEndian(catHeaderNode.AsSpan((int)nodeSize - 2, 2), (ushort)catFreeOff);

        // Write catalog header node at node 0
        var catDiskOff = partitionOffset + (long)catalogStartBlock * blockSize;
        await device.WriteAsync(catDiskOff, catHeaderNode, (int)nodeSize, ct).ConfigureAwait(false);

        // ── Catalog Root Leaf Node (node 1) ──
        // Contains:
        //   Record 0: thread record for root folder — key=(CNID=1, name=""), data=(type=3, parentID=1, name=volumeName)
        //   Record 1: folder record for root folder — key=(CNID=1, name=volumeName), data=(type=1, folderID=2, timestamps)
        var rootLeafNode = new byte[nodeSize];
        // Node descriptor: fLink=0, bLink=0, kind=-1(leaf), height=1, numRecords=2
        rootLeafNode[8] = unchecked((byte)(-1)); // kind = leaf
        rootLeafNode[9] = 1; // height

        var now = GetCurrentHfsTimestamp();

        // Build Record 0: root parent thread
        // Key: parentCNID=1 (root parent), name="" (thread records have empty name)
        var threadKey = BuildCatalogKey(1, "");
        // Thread record data: type=3 (folder thread), reserved=0, parentID=1, name=volumeName
        var threadData = BuildThreadRecord(3, 1, volumeName);

        // Build Record 1: root folder
        // Key: parentCNID=1, name=volumeName
        var folderKey = BuildCatalogKey(1, volumeName);
        // Folder record data: type=1, folderID=2
        var folderData = BuildFolderRecord(2, now);

        // The records must be in key order. Thread key (CNID=1, name="") sorts before
        // folder key (CNID=1, name=volumeName) because empty string < any string.
        var records = new List<byte[]>();

        // Record 0: thread (key + data)
        var rec0 = new byte[threadKey.Length + threadData.Length];
        Buffer.BlockCopy(threadKey, 0, rec0, 0, threadKey.Length);
        Buffer.BlockCopy(threadData, 0, rec0, threadKey.Length, threadData.Length);
        records.Add(rec0);

        // Record 1: folder (key + data)
        var rec1 = new byte[folderKey.Length + folderData.Length];
        Buffer.BlockCopy(folderKey, 0, rec1, 0, folderKey.Length);
        Buffer.BlockCopy(folderData, 0, rec1, folderKey.Length, folderData.Length);
        records.Add(rec1);

        // Write records into leaf node
        BinaryPrimitives.WriteUInt16BigEndian(rootLeafNode.AsSpan(10, 2), (ushort)records.Count);
        var dataStart = 14;
        var leafOffsetTableSize = 2 * (records.Count + 1); // +1 for free-space entry
        var currentOffset = dataStart;

        for (int i = 0; i < records.Count; i++)
        {
            Buffer.BlockCopy(records[i], 0, rootLeafNode, currentOffset, records[i].Length);
            // Write offset table entry
            var entryPos = (int)nodeSize - 2 * (records.Count + 1 - i);
            BinaryPrimitives.WriteUInt16BigEndian(rootLeafNode.AsSpan(entryPos, 2), (ushort)currentOffset);
            currentOffset += records[i].Length;
            // Align to 2-byte boundary
            if (currentOffset % 2 != 0) currentOffset++;
        }

        // Write free-space offset
        BinaryPrimitives.WriteUInt16BigEndian(rootLeafNode.AsSpan((int)nodeSize - 2, 2), (ushort)currentOffset);

        // Write catalog root leaf node at node 1
        var leafDiskOff = catDiskOff + nodeSize;
        await device.WriteAsync(leafDiskOff, rootLeafNode, (int)nodeSize, ct).ConfigureAwait(false);

        // ── 4. Write volume header ──
        var vh = new byte[512];

        // Signature: 0x482B ('H+')
        BinaryPrimitives.WriteUInt16BigEndian(vh.AsSpan(0, 2), 0x482B);
        // Version: 4
        BinaryPrimitives.WriteUInt16BigEndian(vh.AsSpan(2, 2), 4);
        // Attributes: kHFSVolumeUnmountedBit (bit 8)
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(4, 4), 1u << 8);

        // createDate, modifyDate, backupDate, checkedDate at offsets 8,12,16,20 (each uint32 BE)
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(8, 4), now);
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(12, 4), now);

        // fileCount at offset 24 = 0
        // folderCount at offset 28 = 0 (we don't count root itself)
        // blockSize at offset 40
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(40, 4), blockSize);
        // totalBlocks at offset 44
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(44, 4), totalBlocks);
        // freeBlocks at offset 48
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(48, 4), freeBlocks);

        // nextAllocation at offset 52 = firstFreeBlock
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(52, 4), firstFreeBlock);
        // rsrcClumpSize at offset 56 = blockSize
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(56, 4), blockSize);
        // dataClumpSize at offset 60 = blockSize
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(60, 4), blockSize);
        // nextCatalogID at offset 64 = 16 (CNIDs 1-15 reserved, but 2 is root folder)
        BinaryPrimitives.WriteUInt32BigEndian(vh.AsSpan(64, 4), 16);

        // ── Fork data for special files ──

        // Allocation file fork at offset 112 (0x70): 80 bytes
        WriteForkData(vh, 112, bitmapBlocks * blockSize, blockSize, bitmapBlocks,
            new[] { (bitmapStartBlock, bitmapBlocks) });

        // Extents file fork at offset 192 (0xC0): 80 bytes
        WriteForkData(vh, 192, extentsBlocks * blockSize, blockSize, extentsBlocks,
            new[] { (extentsStartBlock, extentsBlocks) });

        // Catalog file fork at offset 272 (0x110): 80 bytes
        WriteForkData(vh, 272, catalogBlocks * blockSize, blockSize, catalogBlocks,
            new[] { (catalogStartBlock, catalogBlocks) });

        // Attributes file fork at offset 352 (0x160): empty (all zeros)
        // Startup file fork at offset 432 (0x1B0): empty (all zeros)

        // Write primary volume header at partition + 1024
        await device.WriteAsync(partitionOffset + 1024, vh, 512, ct).ConfigureAwait(false);

        // Write alternate volume header at end of partition - 1024
        var altOffset = partitionOffset + partitionSize - 1024;
        if (altOffset > partitionOffset + 1024)
        {
            await device.WriteAsync(altOffset, vh, 512, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Write a HFS+ fork data structure (80 bytes) at the given offset in a buffer.
    /// </summary>
    private static void WriteForkData(byte[] buf, int offset, long logicalSize, uint clumpSize,
        uint totalBlocks, (uint StartBlock, uint BlockCount)[] extents)
    {
        // logicalSize(8) + clumpSize(4) + totalBlocks(4) + extents[8](startBlock(4)+blockCount(4))
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(offset, 8), (ulong)logicalSize);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset + 8, 4), clumpSize);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset + 12, 4), totalBlocks);

        for (int i = 0; i < 8; i++)
        {
            var extOff = offset + 16 + i * 8;
            if (i < extents.Length)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(extOff, 4), extents[i].StartBlock);
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(extOff + 4, 4), extents[i].BlockCount);
            }
            // else: already zero
        }
    }

    public void Dispose()
    {
        // Device is owned by the caller (HfsPlusRawFileSystemProvider)
    }
}

public sealed record HfsPlusVolumeHeader(
    ushort Signature,
    uint BlockSize,
    uint TotalBlocks,
    uint FreeBlocks,
    long CatalogLogicalSize,
    HfsPlusExtent[] CatalogExtents
)
{
    public long TotalBytes => (long)BlockSize * TotalBlocks;
    public long FreeBytes => (long)BlockSize * FreeBlocks;
    public bool IsHfsx => Signature == 0x4858;
}

public sealed record HfsPlusExtent(uint StartBlock, uint BlockCount);

public sealed record HfsPlusForkInfo(long LogicalSize, HfsPlusExtent[] Extents);

public sealed record HfsPlusCatalogItem(
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset ModifiedTime,
    uint Cnid,
    HfsPlusForkInfo? DataFork
);

// ─── Diagnostic types ────────────────────────────────────────────────────

public class BTreeDiagnostics
{
    public uint RootNodeIndex;
    public uint FirstLeafNodeIndex;
    public uint LastLeafNode;
    public uint TotalNodes;
    public uint FreeNodes;
    public uint LeafRecords;
    public int CatalogExtentCount;
    public bool HasCycle;
    public List<uint> UnreadableNodes = new();
    public List<BTreeNodeInfo> LeafNodes = new();
    public List<BTreeIndexNodeInfo> IndexNodes = new();
}

public class BTreeNodeInfo
{
    public uint NodeIndex;
    public sbyte Kind;
    public int NumRecords;
    public uint FLink;
    public List<uint> RecordParentCnids = new();
}

public class BTreeIndexNodeInfo
{
    public uint NodeIndex;
    public int NumRecords;
    public List<(uint ParentCnid, string Name, uint ChildNode)> Children = new();
}
