using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MacMount.RawDiskEngine;

public sealed class HfsPlusParser : IFileSystemParser
{
    public string Name => "HFS+/HFSX";

    public async Task<bool> CanHandleAsync(IRawBlockDevice device, CancellationToken cancellationToken = default)
    {
        // HFS+/HFSX volume header signature at byte offset 1024: 'H+' or 'HX'.
        // Read aligned 4KB block and inspect bytes 1024..1025.
        var block = new byte[4096];
        var read = await RawReadUtil.ReadExactlyAtAsync(device, 0, block, block.Length, cancellationToken).ConfigureAwait(false);
        if (read < 1026) return false;

        return (block[1024] == (byte)'H' && block[1025] == (byte)'+')
            || (block[1024] == (byte)'H' && block[1025] == (byte)'X');
    }

    public async Task<MountPlan> BuildMountPlanAsync(IRawBlockDevice device, CancellationToken cancellationToken = default)
    {
        var format = DetectFormat(device, cancellationToken);
        // HFS+ mounts read-write by default (matches the v1.1.0 baseline behavior). The
        // CROSSDRIVE_EXPERIMENTAL_HFS_WRITES env var override is left in place for callers
        // that need to force read-only via env vars.
        var disableWrite =
            string.Equals(Environment.GetEnvironmentVariable("CROSSDRIVE_EXPERIMENTAL_HFS_WRITES"), "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("MACMOUNT_EXPERIMENTAL_HFS_WRITES"), "0", StringComparison.OrdinalIgnoreCase);
        var experimentalWritable = !disableWrite;
        var encryptedProbe = await ProbeEncryptedOrUnreadableCatalogAsync(device, cancellationToken).ConfigureAwait(false);
        // The entropy probe is informational only — it produces false positives on
        // legitimate HFS+ volumes (e.g. drives whose first catalog block sits in a
        // journal-pending region or otherwise contains compressed/tagged metadata).
        // We keep the diagnostic in Notes so it shows up in logs, but never gate the
        // mount on it. HfsPlusNativeReader is the authoritative arbiter — if the
        // catalog really is encrypted/unreadable, parsing will fail and that error
        // is what the user sees.
        var plan = new MountPlan(
            device.DevicePath,
            format,
            device.Length,
            Writable: experimentalWritable,
            Notes: BuildNotes(format, experimentalWritable, encryptedProbe),
            IsEncrypted: false,
            NeedsPassword: false
        );
        return plan;
    }

    public async IAsyncEnumerable<FileEntry> EnumerateRootAsync(IRawBlockDevice device, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static string DetectFormat(IRawBlockDevice device, CancellationToken cancellationToken)
    {
        var block = new byte[4096];
        var read = RawReadUtil.ReadExactlyAtAsync(device, 0, block, block.Length, cancellationToken).GetAwaiter().GetResult();
        if (read >= 1026)
        {
            if (block[1024] == (byte)'H' && block[1025] == (byte)'X')
            {
                return "HFSX";
            }
        }
        return "HFS+";
    }

    private static string BuildNotes(string format, bool experimentalWritable, HfsCatalogProbeResult probe)
    {
        var baseNote = experimentalWritable
            ? $"{format} signature detected. Experimental write support enabled."
            : $"{format} signature detected. Mounted read-only by default.";

        if (!probe.IsLikelyEncrypted)
        {
            return baseNote;
        }

        return $"{baseNote} Catalog fork is unreadable/high-entropy, which usually means an encrypted or locked HFS/CoreStorage-style volume. {probe.Details}";
    }

    private static async Task<HfsCatalogProbeResult> ProbeEncryptedOrUnreadableCatalogAsync(IRawBlockDevice device, CancellationToken cancellationToken)
    {
        var vhBuf = new byte[512];
        var read = await RawReadUtil.ReadExactlyAtAsync(device, 1024, vhBuf, vhBuf.Length, cancellationToken).ConfigureAwait(false);
        if (read < 512)
        {
            return new(false, "volume header short read");
        }

        var blockSize = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(40, 4));
        if (blockSize is < 512 or > 1048576 || (blockSize & (blockSize - 1)) != 0)
        {
            return new(false, $"invalid blockSize={blockSize}");
        }

        // Extra diagnostics so we can tell apart "encrypted" from "dirty / journal needs replay".
        var attrs              = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(4, 4));
        var lastMountedVersion = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(8, 4));
        var journalInfoBlock   = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(12, 4));
        var writeCount         = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(68, 4));
        var totalBlocks        = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(44, 4));
        // Decode lastMountedVersion as 4 ASCII bytes (e.g. '10.0', 'HFSJ', 'fsck')
        var lmvAscii = new string(new[] {
            (char)((lastMountedVersion >> 24) & 0xFF),
            (char)((lastMountedVersion >> 16) & 0xFF),
            (char)((lastMountedVersion >>  8) & 0xFF),
            (char)((lastMountedVersion      ) & 0xFF) });
        // HFS+ attribute bit flags
        var unmountedClean = (attrs & 0x00000100) != 0;
        var inconsistent   = (attrs & 0x00000800) != 0;
        var journaled      = (attrs & 0x00002000) != 0;

        const int catalogForkOffset = 272;
        var startBlock = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(catalogForkOffset + 16, 4));
        var blockCount = BinaryPrimitives.ReadUInt32BigEndian(vhBuf.AsSpan(catalogForkOffset + 20, 4));
        if (startBlock == 0 || blockCount == 0)
        {
            return new(false, $"VH attrs=0x{attrs:X8} unmounted={unmountedClean} inconsistent={inconsistent} journaled={journaled} lmv='{lmvAscii}' writeCount={writeCount} totalBlocks={totalBlocks} blockSize={blockSize} jib={journalInfoBlock} catalog fork has no first extent");
        }

        var catalogOffset = (long)startBlock * blockSize;
        var sample = new byte[(int)Math.Min(4096u, blockSize)];
        var sampleRead = await RawReadUtil.ReadExactlyAtAsync(device, catalogOffset, sample, sample.Length, cancellationToken).ConfigureAwait(false);
        if (sampleRead < 512)
        {
            return new(false, $"catalog sample short read at relative offset {catalogOffset}, bytesRead={sampleRead}");
        }

        var kind = (sbyte)sample[8];
        var entropy = CalculateEntropy(sample.AsSpan(0, sampleRead));
        var head32 = Convert.ToHexString(sample.AsSpan(0, Math.Min(sampleRead, 32)));
        var looksLikeHeaderNode = kind == 1 &&
            BinaryPrimitives.ReadUInt16BigEndian(sample.AsSpan(32, 2)) >= 512;

        // Read the alternate volume header (HFS+ keeps a backup at byte (deviceLength - 1024)).
        // If the primary catalog descriptor is corrupt but the alternate has a different (valid)
        // descriptor, that's a recovery path.
        var altDiag = "altVH=skip";
        if (device.Length >= 2048)
        {
            var altBuf = new byte[512];
            var altRead = await RawReadUtil.ReadExactlyAtAsync(device, device.Length - 1024, altBuf, altBuf.Length, cancellationToken).ConfigureAwait(false);
            if (altRead >= 512)
            {
                var altSig            = BinaryPrimitives.ReadUInt16BigEndian(altBuf.AsSpan(0, 2));
                var altBlockSize      = BinaryPrimitives.ReadUInt32BigEndian(altBuf.AsSpan(40, 4));
                var altCatStart       = BinaryPrimitives.ReadUInt32BigEndian(altBuf.AsSpan(catalogForkOffset + 16, 4));
                var altCatCount       = BinaryPrimitives.ReadUInt32BigEndian(altBuf.AsSpan(catalogForkOffset + 20, 4));
                var altLmv            = BinaryPrimitives.ReadUInt32BigEndian(altBuf.AsSpan(8, 4));
                var altSigChars       = $"{(char)((altSig >> 8) & 0xFF)}{(char)(altSig & 0xFF)}";
                altDiag = $"altVH sig='{altSigChars}'(0x{altSig:X4}) blockSize={altBlockSize} catStart={altCatStart} catCount={altCatCount} altLmv=0x{altLmv:X8}";

                // If alt VH has a valid HFS+ signature and a DIFFERENT catalog start, also probe that location.
                if ((altSig == 0x482B || altSig == 0x4858) && altBlockSize == blockSize && altCatStart != startBlock && altCatStart != 0)
                {
                    var altCatOffset = (long)altCatStart * blockSize;
                    var altSample = new byte[(int)Math.Min(4096u, blockSize)];
                    var altSampleRead = await RawReadUtil.ReadExactlyAtAsync(device, altCatOffset, altSample, altSample.Length, cancellationToken).ConfigureAwait(false);
                    if (altSampleRead >= 512)
                    {
                        var altKind = (sbyte)altSample[8];
                        var altEnt  = CalculateEntropy(altSample.AsSpan(0, altSampleRead));
                        var altHead = Convert.ToHexString(altSample.AsSpan(0, Math.Min(altSampleRead, 32)));
                        altDiag += $" altCatProbe@{altCatOffset}: kind={altKind} entropy={altEnt:F2} head32={altHead}";
                    }
                }
            }
            else
            {
                altDiag = $"altVH=shortRead({altRead})";
            }
        }

        var diag = $"VH attrs=0x{attrs:X8} unmounted={unmountedClean} inconsistent={inconsistent} journaled={journaled} lmv='{lmvAscii}' writeCount={writeCount} totalBlocks={totalBlocks} blockSize={blockSize} jib={journalInfoBlock} catalogOffset={catalogOffset}, catalogBlocks={blockCount}, kind8={kind}, entropy={entropy:F2}, head32={head32} {altDiag}";

        if (!looksLikeHeaderNode && entropy >= 7.25)
        {
            return new(true, diag);
        }

        return new(false, diag);
    }

    private static double CalculateEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;

        Span<int> counts = stackalloc int[256];
        foreach (var b in data)
        {
            counts[b]++;
        }

        double entropy = 0;
        foreach (var count in counts)
        {
            if (count == 0) continue;
            var p = (double)count / data.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    private readonly record struct HfsCatalogProbeResult(bool IsLikelyEncrypted, string Details);
}
