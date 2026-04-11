using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MacMount.RawDiskEngine;

public sealed class ApfsParser : IFileSystemParser
{
    public string Name => "APFS";
    private const uint NxsbMagic = 0x4253584E; // "NXSB" little-endian

    public async Task<bool> CanHandleAsync(IRawBlockDevice device, CancellationToken cancellationToken = default)
    {
        var container = await ReadContainerAsync(device, cancellationToken).ConfigureAwait(false);
        return container is not null;
    }

    public async Task<MountPlan> BuildMountPlanAsync(IRawBlockDevice device, CancellationToken cancellationToken = default)
    {
        // SAFETY: APFS write support is experimental. Disabled unless explicitly opted in.
        var experimentalWritable = string.Equals(
            Environment.GetEnvironmentVariable("MACMOUNT_EXPERIMENTAL_APFS_WRITES"), "1", StringComparison.Ordinal);
        try
        {
            var reader = new ApfsMetadataReader(device, 0, device.Length);
            var summary = await reader.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
            var total = summary.EstimatedTotalBytes > 0 ? summary.EstimatedTotalBytes : device.Length;
            var previews = summary.VolumePreviewsByOid.Values.ToList();
            var encryptedCount = previews.Count(v => v.IsEncrypted);
            var hasBrowsableUnencryptedVolume = previews.Any(v =>
                !v.IsEncrypted &&
                (v.RootEntries.Count > 0 || v.DirectoryEntriesByParentId.Count > 0));
            var needsPassword = encryptedCount > 0 && !hasBrowsableUnencryptedVolume;
            var volumeSummary = string.Join(
                ", ",
                previews.Take(6).Select(v =>
                {
                    var roleSuffix = string.Equals(v.RoleName, "None", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : $"[{v.RoleName}]";
                    var encryptedSuffix = v.IsEncrypted ? "[Encrypted]" : string.Empty;
                    return $"{v.DisplayName}{roleSuffix}{encryptedSuffix}";
                }));
            if (previews.Count > 6)
            {
                volumeSummary += ", ...";
            }

            var firstEncryptedUuid = previews
                .Where(v => v.IsEncrypted && v.VolumeUuid != Guid.Empty)
                .Select(v => v.VolumeUuid)
                .FirstOrDefault();

            var notes =
                $"APFS container parsed. " +
                $"BlockSize={summary.BlockSize}, BlockCount={summary.BlockCount}, " +
                $"CheckpointXid={summary.TransactionId}, VolumeCount={summary.VolumeObjectIds.Count}, " +
                $"ResolvedVolumePointers={summary.ResolvedVolumePointers.Count}, IndexedObjects={summary.IndexedObjectCount}, " +
                $"EncryptedVolumes={encryptedCount}" +
                (firstEncryptedUuid != Guid.Empty ? $" VolumeUuid={firstEncryptedUuid}" : string.Empty) +
                "." +
                (string.IsNullOrWhiteSpace(volumeSummary) ? string.Empty : $" Volumes={volumeSummary}.");

            return new MountPlan(
                device.DevicePath,
                "APFS",
                total,
                Writable: experimentalWritable, // SAFETY: disabled unless MACMOUNT_EXPERIMENTAL_APFS_WRITES=1
                Notes: notes,
                IsEncrypted: encryptedCount > 0,
                NeedsPassword: needsPassword
            );
        }
        catch
        {
            var container = await ReadContainerAsync(device, cancellationToken).ConfigureAwait(false);
            if (container is null)
            {
                return new MountPlan(
                    device.DevicePath,
                    "APFS",
                    device.Length,
                    Writable: experimentalWritable, // SAFETY: disabled unless MACMOUNT_EXPERIMENTAL_APFS_WRITES=1
                    Notes: "APFS detection uncertain (NXSB parse failed).",
                    IsEncrypted: false,
                    NeedsPassword: false
                );
            }

            var computedTotal = checked((long)Math.Min((decimal)long.MaxValue, (decimal)container.BlockSize * container.BlockCount));
            var total = computedTotal > 0 ? computedTotal : device.Length;

            var notes =
                $"APFS container parsed. " +
                $"NXBlockSize={container.BlockSize}, NXBlockCount={container.BlockCount}, " +
                $"CheckpointDescBase={container.CheckpointDescriptorBase}, CheckpointDescBlocks={container.CheckpointDescriptorBlocks}, " +
                $"CheckpointDataBase={container.CheckpointDataBase}, CheckpointDataBlocks={container.CheckpointDataBlocks}, " +
                $"FSOid={container.MainVolumeOid}.";

            return new MountPlan(
                device.DevicePath,
                "APFS",
                total,
                Writable: experimentalWritable, // SAFETY: disabled unless MACMOUNT_EXPERIMENTAL_APFS_WRITES=1
                Notes: notes,
                IsEncrypted: false,
                NeedsPassword: false
            );
        }
    }

    public async IAsyncEnumerable<FileEntry> EnumerateRootAsync(IRawBlockDevice device, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task<ApfsContainerHeader?> ReadContainerAsync(IRawBlockDevice device, CancellationToken cancellationToken)
    {
        // Probe a few common placements for NXSB on raw disks/partitions.
        foreach (var blockStart in new long[] { 0, 4096, 8192, 65536 })
        {
            var header = await TryReadContainerHeaderAtOffsetAsync(device, blockStart, cancellationToken).ConfigureAwait(false);
            if (header is not null)
            {
                return header;
            }
        }

        return null;
    }

    private static async Task<ApfsContainerHeader?> TryReadContainerHeaderAtOffsetAsync(IRawBlockDevice device, long offset, CancellationToken cancellationToken)
    {
        var block = new byte[4096];
        var read = await RawReadUtil.ReadExactlyAtAsync(device, offset, block, block.Length, cancellationToken).ConfigureAwait(false);
        if (read < 256)
        {
            return null;
        }

        // nx_magic at +32 from block start.
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(32, 4));
        if (magic != NxsbMagic)
        {
            return null;
        }

        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(36, 4));
        var blockCount = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(40, 8));
        if (blockSize < 4096 || blockSize > (1u << 20) || blockCount == 0)
        {
            return null;
        }

        // APFS nx_superblock fields (selected):
        // nx_xp_desc_base @ 112, nx_xp_desc_blocks @ 120
        // nx_xp_data_base @ 128, nx_xp_data_blocks @ 136
        // nx_fs_oid[0] @ 184
        var xpDescBase = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(112, 8));
        var xpDescBlocks = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(120, 4));
        var xpDataBase = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(128, 8));
        var xpDataBlocks = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(136, 4));
        var fsOid0 = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(184, 8));

        return new ApfsContainerHeader(
            BlockSize: blockSize,
            BlockCount: blockCount,
            CheckpointDescriptorBase: xpDescBase,
            CheckpointDescriptorBlocks: xpDescBlocks,
            CheckpointDataBase: xpDataBase,
            CheckpointDataBlocks: xpDataBlocks,
            MainVolumeOid: fsOid0
        );
    }

    private sealed record ApfsContainerHeader(
        uint BlockSize,
        ulong BlockCount,
        ulong CheckpointDescriptorBase,
        uint CheckpointDescriptorBlocks,
        ulong CheckpointDataBase,
        uint CheckpointDataBlocks,
        ulong MainVolumeOid
    );
}
