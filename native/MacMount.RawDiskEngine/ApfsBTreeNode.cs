using System.Buffers.Binary;

namespace MacMount.RawDiskEngine;

/// <summary>
/// In-memory APFS B-tree leaf node.
/// Supports inserting and deleting key-value records, then serializing to a block buffer
/// with the correct APFS on-disk layout and a valid Fletcher-64 checksum.
///
/// On-disk layout produced by <see cref="Serialize"/>:
/// <code>
///   0x00–0x07  o_cksum       (Fletcher-64, written last)
///   0x08–0x0F  o_oid
///   0x10–0x17  o_xid
///   0x18–0x1B  o_type
///   0x1C–0x1F  o_subtype
///   0x20–0x21  btn_flags     (0x0002 = BTN_LEAF)
///   0x22–0x23  btn_level     (0 = leaf)
///   0x24–0x27  btn_nkeys
///   0x28–0x29  btn_table_space.off  = 0x38  (absolute TOC position)
///   0x2A–0x2B  btn_table_space.len  = nkeys × 8
///   0x2C–0x37  (other nloc fields, zeroed)
///   0x38+      TOC: nkeys × kvloc_t (8 bytes each)
///              Keys: packed after TOC
///              Values: packed from block end backward
///   block_end−40  btree_info (root nodes only)
/// </code>
///
/// All offsets stored in the block (btn_table_space.off, k.off, v.off) are <b>absolute from
/// the block start</b>, matching the convention used by <see cref="ApfsRawFileSystemProvider"/>.
/// </summary>
internal sealed class ApfsBTreeNode
{
    private const int HeaderSize = 0x38;  // obj_hdr (32) + btn header fields (24) = 56
    private const int TocEntrySize = 8;   // kvloc_t: k.off(u16) + k.len(u16) + v.off(u16) + v.len(u16)
    private const int BTreeInfoSize = 40; // btree_info appended at end of root nodes

    private readonly uint _blockSize;
    private readonly bool _isRoot;
    private readonly List<(byte[] Key, byte[] Value)> _records = new();

    /// <summary>OID stored in the object header (o_oid).</summary>
    public ulong ObjectId { get; set; }

    /// <summary>Transaction ID stored in the object header (o_xid).</summary>
    public ulong TransactionId { get; set; }

    /// <summary>Object type stored in the object header (o_type). Combine OBJECT_TYPE_* with storage flags.</summary>
    public uint ObjectType { get; set; }

    /// <summary>Object subtype stored in the object header (o_subtype). Identifies the B-tree's key family.</summary>
    public uint ObjectSubtype { get; set; }

    public ApfsBTreeNode(uint blockSize, ulong oid, ulong xid,
        uint objectType, uint objectSubtype, bool isRoot = false)
    {
        _blockSize = blockSize;
        _isRoot = isRoot;
        ObjectId = oid;
        TransactionId = xid;
        ObjectType = objectType;
        ObjectSubtype = objectSubtype;
    }

    /// <summary>All current records in their sorted (insertion) order.</summary>
    public IReadOnlyList<(byte[] Key, byte[] Value)> Records => _records;

    /// <summary>Number of records currently in this node.</summary>
    public int RecordCount => _records.Count;

    /// <summary>
    /// Inserts a record in ascending sorted key order (lexicographic comparison of raw key bytes).
    /// If an identical key already exists, the new record is inserted after it (duplicates allowed).
    /// </summary>
    public void Insert(byte[] key, byte[] value)
    {
        int i = 0;
        while (i < _records.Count && CompareKeys(_records[i].Key, key) < 0) i++;
        _records.Insert(i, (key, value));
    }

    /// <summary>
    /// Removes the first record whose key exactly matches <paramref name="key"/>.
    /// Returns true if a record was found and removed, false otherwise.
    /// </summary>
    public bool Delete(byte[] key)
    {
        for (int i = 0; i < _records.Count; i++)
        {
            if (CompareKeys(_records[i].Key, key) == 0)
            {
                _records.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if the given key+value can be inserted without overflowing the block.
    /// </summary>
    public bool WouldFit(byte[] key, byte[] value)
    {
        int btreeInfoSize = _isRoot ? BTreeInfoSize : 0;
        int dataAreaSize = (int)_blockSize - HeaderSize - btreeInfoSize;
        int currentUsed = _records.Count * TocEntrySize
                          + _records.Sum(r => r.Key.Length + r.Value.Length);
        return currentUsed + TocEntrySize + key.Length + value.Length <= dataAreaSize;
    }

    /// <summary>
    /// Serializes all records to a new block-sized byte array with the APFS B-tree node header
    /// and a valid Fletcher-64 checksum. Returns null if the records don't fit in one block.
    /// </summary>
    public byte[]? Serialize()
    {
        int btreeInfoSize = _isRoot ? BTreeInfoSize : 0;
        int dataAreaSize = (int)_blockSize - HeaderSize - btreeInfoSize;
        int tocSize = _records.Count * TocEntrySize;
        int keysSize = _records.Sum(r => r.Key.Length);
        int valsSize = _records.Sum(r => r.Value.Length);
        if (tocSize + keysSize + valsSize > dataAreaSize) return null;

        var buf = new byte[_blockSize];

        // --- Object header (0x00–0x1F) ---
        // o_cksum at 0x00 written last by ApfsChecksum.WriteChecksum
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x08, 8), ObjectId);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x10, 8), TransactionId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x18, 4), ObjectType);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x1C, 4), ObjectSubtype);

        // --- btn header (0x20–0x37) ---
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x20, 2), 0x0002); // BTN_LEAF
        // btn_level = 0 (leaf) — already zero
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x24, 4), (uint)_records.Count);
        // btn_table_space.off = absolute TOC position = HeaderSize = 0x38
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x28, 2), (ushort)HeaderSize);
        // btn_table_space.len = bytes allocated for TOC
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x2A, 2), (ushort)tocSize);
        // 0x2C–0x37: free_space and free_list nloc fields — zeroed (simplified)

        // --- Data area records ---
        // TOC at HeaderSize (0x38), keys packed forward after TOC, values packed backward from valEnd.
        int tocBase = HeaderSize;
        int keyBase = tocBase + tocSize;
        int valEnd = (int)_blockSize - btreeInfoSize;

        int keyPos = 0; // bytes written into keys area (offset from keyBase)
        int valPos = 0; // bytes written from valEnd backward

        for (int i = 0; i < _records.Count; i++)
        {
            var (key, value) = _records[i];
            int absKeyOff = keyBase + keyPos;
            valPos += value.Length;
            int absValOff = valEnd - valPos;

            int tocOff = tocBase + i * TocEntrySize;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tocOff + 0, 2), (ushort)absKeyOff);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tocOff + 2, 2), (ushort)key.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tocOff + 4, 2), (ushort)absValOff);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tocOff + 6, 2), (ushort)value.Length);

            key.CopyTo(buf.AsSpan(absKeyOff, key.Length));
            value.CopyTo(buf.AsSpan(absValOff, value.Length));
            keyPos += key.Length;
        }

        // --- btree_info (root nodes only, at block end) ---
        if (_isRoot)
        {
            int infoBase = (int)_blockSize - BTreeInfoSize;
            // bt_fixed.bt_node_size at infoBase+4
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(infoBase + 4, 4), _blockSize);
            // Remaining btree_info fields (key_size, val_size, counts) are zero — simplified.
        }

        // --- Compute and write checksum ---
        ApfsChecksum.WriteChecksum(buf);
        return buf;
    }

    /// <summary>
    /// Reconstructs an <see cref="ApfsBTreeNode"/> from a raw block buffer.
    /// Returns null if the buffer is too small or has no valid btn_nkeys field.
    /// Preserves on-disk record order (does not re-sort).
    /// </summary>
    public static ApfsBTreeNode? Deserialize(byte[] block, uint blockSize, bool isRoot = false)
    {
        if (block.Length < HeaderSize) return null;

        var oid        = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(0x08, 8));
        var xid        = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(0x10, 8));
        var objType    = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x18, 4));
        var objSubtype = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x1C, 4));
        var nkeys      = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x24, 4));

        var node = new ApfsBTreeNode(blockSize, oid, xid, objType, objSubtype, isRoot);

        int tocBase = HeaderSize; // 0x38
        for (uint i = 0; i < nkeys; i++)
        {
            int tocOff = tocBase + (int)(i * TocEntrySize);
            if (tocOff + TocEntrySize > block.Length) break;

            var kOff = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(tocOff + 0, 2));
            var kLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(tocOff + 2, 2));
            var vOff = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(tocOff + 4, 2));
            var vLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(tocOff + 6, 2));

            if (kOff + kLen > block.Length || vOff + vLen > block.Length) break;

            var key = block.AsSpan(kOff, kLen).ToArray();
            var val = block.AsSpan(vOff, vLen).ToArray();
            node._records.Add((key, val)); // preserve on-disk order (static member of same class)
        }

        return node;
    }

    private static int CompareKeys(byte[] a, byte[] b)
    {
        // APFS stores integer key fields as little-endian uint64. Compare in 8-byte chunks
        // as unsigned 64-bit integers so that type-encoded keys (type << 60 | id) sort
        // numerically by type first, then by id — matching APFS on-disk ordering.
        // Remaining bytes beyond the last complete 8-byte chunk compare lexicographically.
        int len = Math.Min(a.Length, b.Length);
        int aligned = (len / 8) * 8;
        for (int i = 0; i < aligned; i += 8)
        {
            var va = BinaryPrimitives.ReadUInt64LittleEndian(a.AsSpan(i, 8));
            var vb = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i, 8));
            if (va != vb) return va.CompareTo(vb);
        }
        for (int i = aligned; i < len; i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }
}
