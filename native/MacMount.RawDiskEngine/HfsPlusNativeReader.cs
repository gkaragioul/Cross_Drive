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

    // Thread safety: serialise all write operations (WinFsp callbacks are concurrent)
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public HfsPlusVolumeHeader VolumeHeader => _header;
    public bool IsWritable => _device.CanWrite;
    public uint NextCatalogId => _nextCatalogId;

    private HfsPlusNativeReader(IRawBlockDevice device, long partitionOffset, HfsPlusVolumeHeader header,
        uint nodeSize, uint rootNodeIndex, uint firstLeafNodeIndex, List<(long, long)> catalogExtents,
        long catalogFileStart, List<(long, long)> allocationExtents, byte[] vhRawBuf,
        uint nextCatalogId, uint freeBlocks, uint volumeAttributes,
        uint totalNodes, uint freeNodes, uint leafRecords, uint lastLeafNode)
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

        // Read the catalog B-tree header node (node 0)
        var headerNodeBuf = new byte[Math.Max(header.BlockSize, 4096u)];
        var headerNodeRead = await device.ReadAsync(catalogExtents[0].ByteOffset, headerNodeBuf, headerNodeBuf.Length, ct).ConfigureAwait(false);
        if (headerNodeRead < 512) return null;

        // BTNodeDescriptor: fLink(4) bLink(4) kind(1) height(1) numRecords(2) reserved(2) = 14 bytes
        var nodeKind = (sbyte)headerNodeBuf[8];
        if (nodeKind != 1) return null; // must be header node (kind=1)

        // BTHeaderRec starts at offset 14 in the header node
        // treeDepth(2) rootNode(4) leafRecords(4) firstLeafNode(4) lastLeafNode(4) nodeSize(2)
        var rootNode = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(16, 4));
        var leafRecords = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(20, 4));
        var firstLeafNode = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(24, 4));
        var lastLeafNode = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(28, 4));
        var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(headerNodeBuf.AsSpan(32, 2));
        // totalNodes at offset 14+18=32+2=34? Actually BTHeaderRec layout:
        // +0: treeDepth(2), +2: rootNode(4), +6: leafRecords(4), +10: firstLeafNode(4),
        // +14: lastLeafNode(4), +18: nodeSize(2), +20: maxKeyLength(2), +22: totalNodes(4), +26: freeNodes(4)
        var totalNodes = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(14 + 22, 4));
        var freeNodes = BinaryPrimitives.ReadUInt32BigEndian(headerNodeBuf.AsSpan(14 + 26, 4));

        if (nodeSize < 512) return null;

        return new HfsPlusNativeReader(device, partitionOffset, header, nodeSize, rootNode, firstLeafNode,
            catalogExtents, catalogExtents[0].ByteOffset, allocationExtents, vhBuf,
            nextCatalogId, header.FreeBlocks, volumeAttributes,
            totalNodes, freeNodes, leafRecords, lastLeafNode);
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
    /// Read file data from a data fork's extent records.
    /// </summary>
    public async Task<int> ReadFileAsync(HfsPlusForkInfo fork, long offset, byte[] buffer, int count, CancellationToken ct = default)
    {
        if (offset < 0 || offset >= fork.LogicalSize) return 0;
        var toRead = (int)Math.Min(count, fork.LogicalSize - offset);
        var totalRead = 0;

        foreach (var ext in fork.Extents)
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
        // +2: rootNode(4), +6: leafRecords(4), +10: firstLeafNode(4), +14: lastLeafNode(4),
        // +22: totalNodes(4), +26: freeNodes(4)
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
            throw new InvalidOperationException("No free B-tree nodes available.");

        // The bitmap is stored in the header node, starting after the 3 standard records
        // (header record, user data record, map record). The map record typically starts at
        // offset 256 in the header node and runs to the end of the node.
        var headerBuf = await ReadCatalogNodeAsync(0, ct).ConfigureAwait(false);
        if (headerBuf is null) throw new InvalidOperationException("Cannot read B-tree header node.");

        // Find the map record offset from the record offset table
        var numRecords = BinaryPrimitives.ReadUInt16BigEndian(headerBuf.AsSpan(10, 2));
        if (numRecords < 3) throw new InvalidOperationException("B-tree header node has fewer than 3 records.");

        var (mapOffset, mapLen) = GetRecordOffsetAndLength(headerBuf, 2, numRecords);
        if (mapOffset < 14 || mapLen <= 0) throw new InvalidOperationException("Cannot locate B-tree node bitmap.");

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
        // Each record also needs 2 bytes in the offset table
        var totalNeeded = recordLen + 2;

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

        // Clear data area (from offset 14 to start of offset table)
        var dataStart = 14;
        var offsetTableStart = (int)_nodeSize - 2 * (newNumRecords + 1);
        Array.Clear(nodeBuf, dataStart, offsetTableStart - dataStart);

        // Write records sequentially starting at offset 14
        var currentOffset = dataStart;
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            Buffer.BlockCopy(rec, 0, nodeBuf, currentOffset, rec.Length);

            // Write offset entry for this record
            // Offset table: entry for record i is at nodeSize - 2*(numRecords+1 - i)
            var entryPos = (int)_nodeSize - 2 * (newNumRecords + 1 - i);
            BinaryPrimitives.WriteUInt16BigEndian(nodeBuf.AsSpan(entryPos, 2), (ushort)currentOffset);

            currentOffset += rec.Length;
            // Align to 2-byte boundary
            if (currentOffset % 2 != 0) currentOffset++;
        }

        // Write free-space offset (last entry in offset table, at nodeSize-2)
        BinaryPrimitives.WriteUInt16BigEndian(nodeBuf.AsSpan((int)_nodeSize - 2, 2), (ushort)currentOffset);
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

        // Split: first half stays in old node, second half goes to new node
        var splitPoint = allRecords.Count / 2;
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
        var totalNeeded = childFirstKey.Length + ptrData.Length + 2;

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

        // Split at midpoint
        var splitPoint = allRecords.Count / 2;
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

        // Update tree depth in header
        _rootNodeIndex = newRootIndex;
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

            if (split is not null)
            {
                await InsertIntoParentIndexAsync(parentIndex, split.Value.NewNodeIndex, split.Value.NewNodeFirstKey, ct).ConfigureAwait(false);
            }

            // Build and insert thread record: key=(cnid, ""), data=thread pointing back to parent
            var threadKey = BuildCatalogKey(cnid, "");
            var threadRecord = BuildThreadRecord(4, parentCnid, name); // 4 = file thread

            // Use full-key descent for thread record too (high CNID goes to correct leaf, not first)
            var (threadLeaf, threadParentIndex) = await FindLeafForKeyAsync(threadKey, ct).ConfigureAwait(false);
            var threadSplit = await InsertIntoLeafAsync(threadLeaf, threadKey, threadRecord, ct).ConfigureAwait(false);
            _leafRecords++;

            if (threadSplit is not null)
            {
                await InsertIntoParentIndexAsync(threadParentIndex, threadSplit.Value.NewNodeIndex, threadSplit.Value.NewNodeFirstKey, ct).ConfigureAwait(false);
            }

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

            if (split is not null)
            {
                await InsertIntoParentIndexAsync(parentIndex, split.Value.NewNodeIndex, split.Value.NewNodeFirstKey, ct).ConfigureAwait(false);
            }

            // Thread record: type 3 = folder thread
            var threadKey = BuildCatalogKey(cnid, "");
            var threadRecord = BuildThreadRecord(3, parentCnid, name);

            // Use full-key descent for thread record too
            var (threadLeaf, threadParentIndex) = await FindLeafForKeyAsync(threadKey, ct).ConfigureAwait(false);
            var threadSplit = await InsertIntoLeafAsync(threadLeaf, threadKey, threadRecord, ct).ConfigureAwait(false);
            _leafRecords++;

            if (threadSplit is not null)
            {
                await InsertIntoParentIndexAsync(threadParentIndex, threadSplit.Value.NewNodeIndex, threadSplit.Value.NewNodeFirstKey, ct).ConfigureAwait(false);
            }

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
            var newStart = await AllocateBlocksAsync(additionalBlocks, ct).ConfigureAwait(false);

            // Add new extent to fork — find first empty extent slot
            var extents = new List<HfsPlusExtent>(currentFork.Extents);
            for (int i = 0; i < extents.Count; i++)
            {
                if (extents[i].BlockCount == 0)
                {
                    extents[i] = new HfsPlusExtent(newStart, additionalBlocks);
                    break;
                }
            }

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
        var currentNode = _rootNodeIndex;
        var depth = 0;

        while (depth < 20) // safety limit
        {
            var nodeBuf = await ReadCatalogNodeAsync(currentNode, ct).ConfigureAwait(false);
            if (nodeBuf is null) return 0;

            var kind = (sbyte)nodeBuf[8];
            if (kind == -1) return currentNode; // leaf node

            if (kind != 0) return 0; // must be index node

            var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(10, 2));
            uint bestChild = 0;

            for (int i = 0; i < numRecords; i++)
            {
                var (recOffset, recLen) = GetRecordOffsetAndLength(nodeBuf, i, numRecords);
                if (recOffset < 14 || recOffset + 6 > nodeBuf.Length) continue;

                var keyLen = BinaryPrimitives.ReadUInt16BigEndian(nodeBuf.AsSpan(recOffset, 2));
                if (keyLen < 6) continue;

                var recParentCnid = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(recOffset + 2, 4));

                // Data after key is the child node pointer (uint32 BE)
                var ptrOffset = recOffset + 2 + keyLen;
                if (ptrOffset % 2 != 0) ptrOffset++;
                if (ptrOffset + 4 > nodeBuf.Length) continue;

                var childNode = BinaryPrimitives.ReadUInt32BigEndian(nodeBuf.AsSpan(ptrOffset, 4));

                if (recParentCnid <= parentCnid)
                {
                    bestChild = childNode;
                }
                else
                {
                    break;
                }
            }

            if (bestChild == 0) return 0;
            currentNode = bestChild;
            depth++;
        }

        return 0;
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
