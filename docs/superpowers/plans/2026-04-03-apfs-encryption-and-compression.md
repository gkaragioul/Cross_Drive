# APFS Encryption Unlock & Compressed File Reads — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire up the existing APFS encryption code so that password-based unlock works end-to-end, and enable reading zlib-compressed APFS files that currently return 0 bytes.

**Architecture:** The broker already has the full encrypted-mount flow (detect encrypted -> prompt password -> call TryUnlockEncryptedApfsAsync -> inject VEK into MountPlan -> DecryptingRawBlockDevice wraps the raw device). Two bugs block it: (1) the keybag is read from the wrong superblock offset, (2) the volume UUID is never extracted from the volume superblock so the keybag lookup always fails. For compression, inline zlib-compressed files (APFS decmpfs type 3) need a decompression path in the file read pipeline.

**Tech Stack:** .NET 9 / C#, APFS on-disk structures, AES-XTS, PBKDF2, zlib (System.IO.Compression)

---

## File Map

| Action | File | Responsibility |
|--------|------|---------------|
| Modify | `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs` | Add `VolumeUuid` to `ApfsVolumePreview` record; read UUID at offset 0xF0 in `TryBuildVolumePreviewAsync`; add zlib decompression in `ReadFile` |
| Modify | `native/MacMount.RawDiskEngine/ApfsParser.cs` | Include first encrypted volume's UUID in MountPlan notes |
| Modify | `native/MacMount.RawDiskEngine/ApfsKeyManager.cs` | Fix keybag offset (0x4F8 not 0x108); fix byte-vs-block arithmetic; read block size from superblock |
| Modify | `native/MacMount.RawDiskEngine/DecryptingRawBlockDevice.cs` | Fix XTS unit number calculation (use block-relative addressing) |
| Modify | `docs/CURRENT_STATUS.md` | Update status to reflect encryption and compression progress |
| Modify | `CLAUDE.md` | Update if any structural changes are made |

---

### Task 1: Add Volume UUID to the APFS metadata pipeline

The `ApfsVolumePreview` record has no UUID field. The broker's `TryUnlockEncryptedApfsAsync` tries to regex-match `VolumeUuid=...` from the MountPlan notes, but no code ever writes it there. Without the UUID, the keybag entry lookup always returns null.

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs:543-557` (ApfsVolumePreview record)
- Modify: `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs:1192-1262` (TryBuildVolumePreviewAsync)
- Modify: `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs:583-592` (constants section)

- [ ] **Step 1: Add UUID constant and field to ApfsVolumePreview**

In `ApfsRawFileSystemProvider.cs`, add the UUID offset constant near the other volume superblock offsets (around line 589):

```csharp
private const int ApfsFsFlagsOffset = 264;
private const int ApfsVolumeUuidOffset = 240; // apfs_vol_uuid at 0xF0 in volume superblock
private const int ApfsVolumeNameOffset = 704;
private const int ApfsVolumeNameLength = 256;
private const int ApfsRoleOffset = 964;
```

Then add `VolumeUuid` as a field in the `ApfsVolumePreview` record (around line 543). Insert it after `VolumeObjectId`:

```csharp
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
    IReadOnlyDictionary<ulong, ApfsFileReadPlan> FilePlansByObjectId
);
```

- [ ] **Step 2: Read UUID from volume superblock in TryBuildVolumePreviewAsync**

In the `TryBuildVolumePreviewAsync` method (around line 1211), after reading `volumeName`, add UUID extraction:

```csharp
var volumeName = TryReadUtf8NullTerminatedString(volumeBuffer, ApfsVolumeNameOffset, ApfsVolumeNameLength);

var volumeUuid = Guid.Empty;
if (volumeBuffer.Length >= ApfsVolumeUuidOffset + 16)
{
    var uuidBytes = new byte[16];
    Array.Copy(volumeBuffer, ApfsVolumeUuidOffset, uuidBytes, 0, 16);
    volumeUuid = new Guid(uuidBytes);
}
```

Then pass it into the `ApfsVolumePreview` constructor (around line 1248):

```csharp
return new ApfsVolumePreview(
    VolumeObjectId: resolvedVolumePointer.ObjectId,
    VolumeUuid: volumeUuid,
    DisplayName: string.IsNullOrWhiteSpace(volumeName) ? $"Volume_{resolvedVolumePointer.ObjectId:X}" : volumeName,
    // ... rest unchanged
);
```

- [ ] **Step 3: Build the .NET projects to verify compilation**

Run: `dotnet build native/MacMount.RawDiskEngine/MacMount.RawDiskEngine.csproj -c Release`
Then: `dotnet build native/MacMount.NativeBroker/MacMount.NativeBroker.csproj -c Release`

Expected: Both succeed. The `VolumeUuid` field is new so there may be compilation errors in the info text builder or hint builder that reference the old constructor signature. Fix any such errors by passing `Guid.Empty` for the UUID in fallback/hint code paths.

- [ ] **Step 4: Commit**

```bash
git add native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs
git commit -m "feat: read APFS volume UUID from superblock at offset 0xF0"
```

---

### Task 2: Include Volume UUID in MountPlan notes

The broker's `TryUnlockEncryptedApfsAsync` (NativeBroker Program.cs:537-543) regex-matches `VolumeUuid=<guid>` from the plan's Notes string. The `ApfsParser.BuildMountPlanAsync` builds the Notes string but never includes the UUID.

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsParser.cs:20-65` (BuildMountPlanAsync)

- [ ] **Step 1: Pass the first encrypted volume's UUID into the notes string**

In `ApfsParser.BuildMountPlanAsync` (around line 33-54), after building `volumeSummary`, extract the first encrypted volume's UUID and append it to `notes`. The `previews` list comes from `summary.VolumePreviewsByOid.Values` which now includes `VolumeUuid`:

```csharp
var firstEncryptedUuid = previews
    .Where(v => v.IsEncrypted && v.VolumeUuid != Guid.Empty)
    .Select(v => v.VolumeUuid)
    .FirstOrDefault();

var notes =
    $"APFS container parsed. " +
    $"BlockSize={summary.BlockSize}, BlockCount={summary.BlockCount}, " +
    $"CheckpointXid={summary.TransactionId}, VolumeCount={summary.VolumeObjectIds.Count}, " +
    $"ResolvedVolumePointers={summary.ResolvedVolumePointers.Count}, IndexedObjects={summary.IndexedObjectCount}, " +
    $"EncryptedVolumes={encryptedCount}." +
    (firstEncryptedUuid != Guid.Empty ? $" VolumeUuid={firstEncryptedUuid}" : string.Empty) +
    (string.IsNullOrWhiteSpace(volumeSummary) ? string.Empty : $" Volumes={volumeSummary}.");
```

This ensures the broker's regex `VolumeUuid=([0-9a-fA-F-]{36})` will find it.

- [ ] **Step 2: Build and verify**

Run: `dotnet build native/MacMount.RawDiskEngine/MacMount.RawDiskEngine.csproj -c Release`
Then: `dotnet build native/MacMount.NativeBroker/MacMount.NativeBroker.csproj -c Release`

Expected: Both succeed.

- [ ] **Step 3: Commit**

```bash
git add native/MacMount.RawDiskEngine/ApfsParser.cs
git commit -m "feat: include encrypted volume UUID in APFS MountPlan notes"
```

---

### Task 3: Fix ApfsKeyManager keybag offset and block arithmetic

The `LoadContainerKeybagAsync` method reads the APFS container keybag (`nx_keylocker` field) from offset 0x108 in the NX superblock. The correct offset is **0x4F8**. Additionally, `nx_keylocker` is a `prange_t` whose `pr_start_paddr` is a physical **block address**, not a byte offset — it must be multiplied by the container block size. And `pr_block_count` gives the keybag size in blocks, not bytes.

**APFS NX superblock layout reference for nx_keylocker:**
- `nx_fs_oid[100]` starts at 0xB8, spans 800 bytes, ends at 0x3D8
- `nx_counters[32]` starts at 0x3D8, spans 256 bytes, ends at 0x4D8
- `nx_blocked_out_prange` (16 bytes) at 0x4D8
- `nx_evict_mapping_tree_oid` (8 bytes) at 0x4E8
- `nx_flags` (8 bytes) at 0x4F0
- `nx_keylocker` (prange_t: 8-byte block addr + 8-byte block count) at **0x4F8**

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsKeyManager.cs:62-77` (LoadContainerKeybagAsync)

- [ ] **Step 1: Fix the keybag location and size calculation**

Replace the `LoadContainerKeybagAsync` method body:

```csharp
private async Task<byte[]?> LoadContainerKeybagAsync(CancellationToken ct)
{
    // Read the NX superblock (must be at least large enough for nx_keylocker at 0x4F8 + 16)
    var superblockSize = Math.Max(_blockSize, 1296u); // 0x4F8 + 16 = 0x508 = 1288, round up
    var buf = new byte[superblockSize];
    var read = await _device.ReadAsync((long)_nxBaseOffset, buf, buf.Length, ct).ConfigureAwait(false);
    if (read < 0x508) return null; // not enough data for nx_keylocker field

    // nx_keylocker is a prange_t at offset 0x4F8 in the NX superblock.
    // prange_t { paddr_t pr_start_paddr; uint64_t pr_block_count; }
    // pr_start_paddr is a physical BLOCK address (not byte offset).
    var keybagBlockAddr = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0x4F8));
    var keybagBlockCount = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0x500));

    if (keybagBlockAddr == 0 || keybagBlockCount == 0 || keybagBlockCount > 256)
        return null;

    var keybagByteOffset = (long)(keybagBlockAddr * _blockSize);
    var keybagByteSize = (long)(keybagBlockCount * _blockSize);

    if (keybagByteSize > 1024 * 1024)
        return null;

    var keybagBuf = new byte[keybagByteSize];
    await _device.ReadAsync(keybagByteOffset, keybagBuf, keybagBuf.Length, ct).ConfigureAwait(false);

    return keybagBuf;
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build native/MacMount.RawDiskEngine/MacMount.RawDiskEngine.csproj -c Release`
Then: `dotnet build native/MacMount.NativeBroker/MacMount.NativeBroker.csproj -c Release`

Expected: Both succeed.

- [ ] **Step 3: Commit**

```bash
git add native/MacMount.RawDiskEngine/ApfsKeyManager.cs
git commit -m "fix: read APFS keybag from correct NX superblock offset 0x4F8"
```

---

### Task 4: Fix DecryptingRawBlockDevice XTS unit number calculation

The current code computes the XTS tweak unit number as `baseUnit + i * _csFactor` where `_csFactor = containerBlockSize / sectorSize`. For APFS, the XTS tweak should be the **sector number relative to the start of the volume** (i.e., `alignedOffset / sectorSize`), incrementing by 1 per sector. The `_csFactor` multiplication skips sector numbers and produces wrong tweaks.

**Files:**
- Modify: `native/MacMount.RawDiskEngine/DecryptingRawBlockDevice.cs:27-55` (ReadAsync)

- [ ] **Step 1: Fix the unit number to use sequential sector addresses**

Replace the decryption loop in `ReadAsync`:

```csharp
public async ValueTask<int> ReadAsync(long offset, byte[] buffer, int count, CancellationToken cancellationToken = default)
{
    var alignedOffset = offset - (offset % _sectorSize);
    var alignedEnd = offset + count;
    if (alignedEnd % _sectorSize != 0)
        alignedEnd += _sectorSize - (alignedEnd % _sectorSize);

    var alignedCount = (int)(alignedEnd - alignedOffset);
    var tempBuf = new byte[alignedCount];

    var read = await _inner.ReadAsync(alignedOffset, tempBuf, alignedCount, cancellationToken).ConfigureAwait(false);
    if (read <= 0) return 0;

    var srcOffset = (int)(offset - alignedOffset);
    var bytesToCopy = Math.Min(count, read - srcOffset);

    // Decrypt each sector. The XTS tweak (unit number) is the sector index
    // from the start of the device (alignedOffset / sectorSize), incrementing
    // by 1 for each 512-byte sector.
    var sectorCount = read / (int)_sectorSize;
    var baseSector = (ulong)(alignedOffset / _sectorSize);

    for (var i = 0; i < sectorCount; i++)
    {
        var unitNo = baseSector + (ulong)i;
        var sectorOffset = (int)(i * _sectorSize);
        _xts.Decrypt(tempBuf, sectorOffset, tempBuf, sectorOffset, unitNo);
    }

    Array.Copy(tempBuf, srcOffset, buffer, 0, bytesToCopy);
    return bytesToCopy;
}
```

Also remove the unused `_csFactor` field since it is no longer needed:

```csharp
internal sealed class DecryptingRawBlockDevice : IRawBlockDevice
{
    private readonly IRawBlockDevice _inner;
    private readonly AesXts _xts;
    private readonly uint _sectorSize = 512;

    public string DevicePath => _inner.DevicePath;
    public long Length => _inner.Length;

    public DecryptingRawBlockDevice(IRawBlockDevice inner, byte[] vek, uint containerBlockSize)
    {
        _inner = inner;

        var key1 = new byte[16];
        var key2 = new byte[16];
        Array.Copy(vek, 0, key1, 0, 16);
        Array.Copy(vek, 16, key2, 0, 16);
        _xts = new AesXts(key1, key2);
    }
    // ... rest unchanged
}
```

Note: `containerBlockSize` parameter is kept in the constructor signature to avoid breaking the caller, even though it's no longer used internally.

- [ ] **Step 2: Build and verify**

Run: `dotnet build native/MacMount.RawDiskEngine/MacMount.RawDiskEngine.csproj -c Release`
Then: `dotnet build native/MacMount.NativeBroker/MacMount.NativeBroker.csproj -c Release`

Expected: Both succeed.

- [ ] **Step 3: Commit**

```bash
git add native/MacMount.RawDiskEngine/DecryptingRawBlockDevice.cs
git commit -m "fix: use sequential sector-based XTS tweaks for APFS decryption"
```

---

### Task 5: Add zlib decompression for inline-compressed APFS files

Files with the `UF_COMPRESSED` BSD flag and inline data currently return 0 bytes from `ReadFile`. APFS uses a `decmpfs` attribute header to describe the compression. Type 3 (inline zlib) is the most common for small files. The inline data already captured by `BuildInodeDataMap` contains the raw `decmpfs` header + compressed payload.

**decmpfs header layout (12 bytes):**
```
uint32_t compression_magic;   // 0x636D7066 = "fpmc" LE
uint32_t compression_type;    // 3=zlib inline, 4=zlib resource fork, 7=LZVN inline, etc.
uint64_t uncompressed_size;
// followed by compressed data (for inline types)
```

**Files:**
- Modify: `native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs` — ReadFile method (around line 145-200) and add a static decompression helper

- [ ] **Step 1: Add the decompression helper method**

Add this static method to the `ApfsRawFileSystemProvider` class (after `ReadFile`, before `Dispose`):

```csharp
private static byte[]? TryDecompressInlineDecmpfs(byte[] inlineData)
{
    // decmpfs header: 4-byte magic + 4-byte type + 8-byte uncompressed_size = 12 bytes minimum
    if (inlineData.Length < 12) return null;

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(inlineData.AsSpan(0, 4));
    if (magic != 0x636D7066) return null; // "fpmc" LE

    var compressionType = BinaryPrimitives.ReadUInt32LittleEndian(inlineData.AsSpan(4, 4));
    var uncompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(inlineData.AsSpan(8, 8));

    if (uncompressedSize <= 0 || uncompressedSize > 64 * 1024 * 1024) return null; // sanity cap

    // Type 3: zlib-compressed data stored inline after the 12-byte header
    if (compressionType == 3 && inlineData.Length > 12)
    {
        try
        {
            var compressedPayload = inlineData.AsSpan(12);
            using var inputStream = new MemoryStream(compressedPayload.ToArray());

            // APFS zlib inline data uses raw deflate (no zlib header) when the first byte
            // is NOT 0x78. If it IS 0x78 it's a standard zlib stream (skip 2-byte header).
            if (compressedPayload.Length > 0 && compressedPayload[0] == 0x78)
            {
                inputStream.Position = 2; // skip zlib header (CMF + FLG)
            }

            using var deflate = new System.IO.Compression.DeflateStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream((int)uncompressedSize);
            deflate.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null; // decompression failed, fall back to returning 0 bytes
        }
    }

    // Type 1: uncompressed data stored inline after the header (rare, small files)
    if (compressionType == 1 && inlineData.Length > 12)
    {
        var data = new byte[Math.Min(uncompressedSize, inlineData.Length - 12)];
        Array.Copy(inlineData, 12, data, 0, data.Length);
        return data;
    }

    return null; // unsupported compression type (LZVN=7, LZFSE=11, resource-fork types 4/8/12)
}
```

- [ ] **Step 2: Wire decompression into ReadFile**

In the `ReadFile` method, replace the early return for compressed files (around line 185-188):

```csharp
if (readPlan.IsCompressed)
{
    if (readPlan.InlineData is not null)
    {
        var decompressed = TryDecompressInlineDecmpfs(readPlan.InlineData);
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
    }
    return 0;
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build native/MacMount.RawDiskEngine/MacMount.RawDiskEngine.csproj -c Release`
Then: `dotnet build native/MacMount.NativeBroker/MacMount.NativeBroker.csproj -c Release`

Expected: Both succeed.

- [ ] **Step 4: Commit**

```bash
git add native/MacMount.RawDiskEngine/ApfsRawFileSystemProvider.cs
git commit -m "feat: decompress inline zlib-compressed APFS files (decmpfs type 3)"
```

---

### Task 6: Verify full build and update documentation

**Files:**
- Modify: `docs/CURRENT_STATUS.md`
- Modify: `CLAUDE.md` (if any structural changes were made)

- [ ] **Step 1: Build all .NET projects**

Run:
```bash
dotnet build native/MacMount.RawDiskEngine/MacMount.RawDiskEngine.csproj -c Release
dotnet build native/MacMount.NativeBroker/MacMount.NativeBroker.csproj -c Release
dotnet build native/MacMount.NativeService/MacMount.NativeService.csproj -c Release
```

Expected: All three succeed.

- [ ] **Step 2: Run self-test**

Run: `npm run test`

Expected: All checks pass.

- [ ] **Step 3: Update CURRENT_STATUS.md**

Update the following sections in `docs/CURRENT_STATUS.md`:

Under "APFS Encrypted Unlock" (around line 68-71), change to:

```markdown
### APFS Encrypted Unlock
- The app can detect encrypted APFS and request a password.
- Native APFS decryption is now wired end-to-end:
  - Volume UUID is read from the APFS volume superblock.
  - Container keybag is read from the correct NX superblock offset (0x4F8).
  - PBKDF2 key derivation + RFC 3394 AES key unwrap produce the VEK.
  - DecryptingRawBlockDevice applies AES-XTS decryption transparently.
- **Status:** Implemented but needs validation against real encrypted APFS images.
- Known limitation: Only password-based unlock is supported. Hardware-bound keys (T2/Apple Silicon) cannot be unlocked.
```

Under "Current Partially Working Areas > APFS" (around line 56-59), add:

```markdown
- Inline zlib-compressed files (decmpfs type 3) can now be read.
- Other compression types (LZVN, LZFSE, resource-fork-based) still return 0 bytes.
```

- [ ] **Step 4: Commit**

```bash
git add docs/CURRENT_STATUS.md CLAUDE.md
git commit -m "docs: update status for APFS encryption and compression support"
```

---

## Out of Scope (documented for future work)

1. **CoreStorage / FileVault 1** — Completely different volume manager and encryption scheme. Requires its own plan.
2. **LZVN / LZFSE decompression** — These Apple-proprietary algorithms need either a .NET port or a native library. Not trivial.
3. **Resource-fork compressed files** (decmpfs types 4, 8, 12) — Requires parsing the resource fork structure in addition to the catalog tree.
4. **T2/Apple Silicon hardware-bound encryption** — Platform limitation, not implementable on Windows.
5. **APFS bridge binary bundling** — External build dependency, not a code change.
