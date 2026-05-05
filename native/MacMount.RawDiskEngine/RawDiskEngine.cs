using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.HfsPlus;

namespace MacMount.RawDiskEngine;

public sealed class RawDiskEngine : IRawDiskEngine
{
    private const int ProbeBytes = 4096;
    private const int LbaSize = 512;
    private static readonly Guid GptTypeApfs = new("7C3457EF-0000-11AA-AA11-00306543ECAC");
    private static readonly Guid GptTypeHfs = new("48465300-0000-11AA-AA11-00306543ECAC");
    private static readonly Guid GptTypeCoreStorage = new("53746F72-6167-11AA-AA11-00306543ECAC");
    private const ushort ApmDriverDescriptorSignature = 0x4552; // "ER"
    private const ushort ApmPartitionSignature = 0x504D; // "PM"

    private readonly IReadOnlyList<IFileSystemParser> _parsers;
    private readonly IRawBlockDeviceFactory _deviceFactory;

    public RawDiskEngine(IReadOnlyList<IFileSystemParser>? parsers = null, IRawBlockDeviceFactory? deviceFactory = null)
    {
        _parsers = parsers ?? new IFileSystemParser[] { new ApfsParser(), new HfsPlusParser() };
        _deviceFactory = deviceFactory ?? new WindowsRawBlockDeviceFactory();
    }

    public async Task<MountPlan> AnalyzeAsync(MountRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PhysicalDrivePath))
        {
            throw new ArgumentException("PhysicalDrivePath is required.", nameof(request));
        }

        IRawBlockDevice rawDevice;
        try
        {
            rawDevice = await _deviceFactory.OpenReadOnlyAsync(request.PhysicalDrivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"OpenReadOnlyAsync failed for '{request.PhysicalDrivePath}': {ex.Message}", ex);
        }

        using (rawDevice)
        {
            var probeBuffer = new byte[ProbeBytes];
            int read;
            try
            {
                read = await RawReadUtil.ReadExactlyAtAsync(rawDevice, 0, probeBuffer, probeBuffer.Length, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Probe read failed at offset 0 on '{rawDevice.DevicePath}': {ex.Message}", ex);
            }
            var headerHex = Convert.ToHexString(probeBuffer.AsSpan(0, Math.Min(read, 32)));

            if (!string.IsNullOrWhiteSpace(request.FileSystemHint))
            {
                return new MountPlan(
                    rawDevice.DevicePath,
                    request.FileSystemHint.Trim(),
                    rawDevice.Length,
                    Writable: false,
                    Notes: $"Raw-disk reader active. ProbeBytes={read}, Header32={headerHex}",
                    PartitionOffsetBytes: 0,
                    PartitionLengthBytes: rawDevice.Length
                );
            }

            // Inspect partition tables first (GPT, MBR, APM) so that the correct
            // partition offset is used.  Fall back to whole-device probe only when
            // no partition table is found — some Mac disks write an HFS+ signature
            // at device byte 1024 even though the real partition starts at 1 MB+.

            // Inspect GPT partitions and probe parser on each Apple partition slice.
            var candidates = await ReadGptMacPartitionCandidatesAsync(rawDevice, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in candidates)
            {
                using var slice = new PartitionSliceRawBlockDevice(
                    rawDevice,
                    devicePath: $"{rawDevice.DevicePath}#part{candidate.Index}",
                    baseOffset: candidate.StartOffset,
                    length: candidate.Length
                );

                foreach (var parser in _parsers)
                {
                    if (await parser.CanHandleAsync(slice, cancellationToken).ConfigureAwait(false))
                    {
                        var parserPlan = await parser.BuildMountPlanAsync(slice, cancellationToken).ConfigureAwait(false);
                        return parserPlan with
                        {
                            Notes = $"{parserPlan.Notes} Source=GPT part {candidate.Index}, TypeGuid={candidate.TypeGuid}, StartOffset={candidate.StartOffset}",
                            PartitionOffsetBytes = candidate.StartOffset,
                            PartitionLengthBytes = candidate.Length
                        };
                    }
                }

                // Fallback to GUID hint if parser is not ready yet.
                if (candidate.TypeGuid == GptTypeApfs)
                {
                    return new MountPlan(
                        slice.DevicePath,
                        "APFS",
                        slice.Length,
                        Writable: false,
                        Notes: $"GPT APFS partition detected at part {candidate.Index}; parser signature probe pending.",
                        PartitionOffsetBytes: candidate.StartOffset,
                        PartitionLengthBytes: candidate.Length
                    );
                }

                if (candidate.TypeGuid == GptTypeHfs)
                {
                    return new MountPlan(
                        slice.DevicePath,
                        "HFS+",
                        slice.Length,
                        Writable: false,
                        Notes: $"GPT HFS+ partition detected at part {candidate.Index}; parser signature probe pending.",
                        PartitionOffsetBytes: candidate.StartOffset,
                        PartitionLengthBytes: candidate.Length
                    );
                }

                if (candidate.TypeGuid == GptTypeCoreStorage)
                {
                    return new MountPlan(
                        slice.DevicePath,
                        "CoreStorage",
                        slice.Length,
                        Writable: false,
                        Notes: $"GPT Apple CoreStorage partition detected at part {candidate.Index}; unlock/decryption is not implemented yet.",
                        PartitionOffsetBytes: candidate.StartOffset,
                        PartitionLengthBytes: candidate.Length
                    );
                }
            }

            // Inspect MBR partitions for Apple HFS+ (type 0xAF).
            var mbrCandidates = ReadMbrHfsCandidates(probeBuffer);
            foreach (var mbr in mbrCandidates)
            {
                using var slice = new PartitionSliceRawBlockDevice(
                    rawDevice,
                    devicePath: $"{rawDevice.DevicePath}#mbrpart{mbr.Index}",
                    baseOffset: mbr.StartOffset,
                    length: mbr.Length
                );

                foreach (var parser in _parsers)
                {
                    if (await parser.CanHandleAsync(slice, cancellationToken).ConfigureAwait(false))
                    {
                        var parserPlan = await parser.BuildMountPlanAsync(slice, cancellationToken).ConfigureAwait(false);
                        return parserPlan with
                        {
                            Notes = $"{parserPlan.Notes} Source=MBR part {mbr.Index}, Type=0xAF, StartOffset={mbr.StartOffset}",
                            PartitionOffsetBytes = mbr.StartOffset,
                            PartitionLengthBytes = mbr.Length
                        };
                    }
                }

                // Parser didn't match signature but MBR type is 0xAF — trust the partition type
                return new MountPlan(
                    slice.DevicePath,
                    "HFS+",
                    slice.Length,
                    Writable: false,
                    Notes: $"MBR HFS+ partition (type 0xAF) at part {mbr.Index}; StartOffset={mbr.StartOffset}",
                    PartitionOffsetBytes: mbr.StartOffset,
                    PartitionLengthBytes: mbr.Length
                );
            }

            // Inspect Apple Partition Map (APM) disks for HFS+/CoreStorage layouts.
            var apmCandidates = await ReadApmMacPartitionCandidatesAsync(rawDevice, cancellationToken).ConfigureAwait(false);
            foreach (var apm in apmCandidates)
            {
                using var slice = new PartitionSliceRawBlockDevice(
                    rawDevice,
                    devicePath: $"{rawDevice.DevicePath}#apmpart{apm.Index}",
                    baseOffset: apm.StartOffset,
                    length: apm.Length
                );

                foreach (var parser in _parsers)
                {
                    if (await parser.CanHandleAsync(slice, cancellationToken).ConfigureAwait(false))
                    {
                        var parserPlan = await parser.BuildMountPlanAsync(slice, cancellationToken).ConfigureAwait(false);
                        return parserPlan with
                        {
                            Notes = $"{parserPlan.Notes} Source=APM part {apm.Index}, Type={apm.TypeName}, Name={apm.Name}, StartOffset={apm.StartOffset}",
                            PartitionOffsetBytes = apm.StartOffset,
                            PartitionLengthBytes = apm.Length
                        };
                    }
                }

                if (string.Equals(apm.TypeName, "Apple_HFS", StringComparison.OrdinalIgnoreCase))
                {
                    return new MountPlan(
                        slice.DevicePath,
                        "HFS+",
                        slice.Length,
                        Writable: false,
                        Notes: $"APM HFS partition detected at part {apm.Index}; Name={apm.Name}, StartOffset={apm.StartOffset}",
                        PartitionOffsetBytes: apm.StartOffset,
                        PartitionLengthBytes: apm.Length
                    );
                }

                if (string.Equals(apm.TypeName, "Apple_CoreStorage", StringComparison.OrdinalIgnoreCase))
                {
                    return new MountPlan(
                        slice.DevicePath,
                        "CoreStorage",
                        slice.Length,
                        Writable: false,
                        Notes: $"APM Apple CoreStorage partition detected at part {apm.Index}; Name={apm.Name}. Unlock/decryption is not implemented yet.",
                        PartitionOffsetBytes: apm.StartOffset,
                        PartitionLengthBytes: apm.Length
                    );
                }
            }

            // No partition table found — try whole-device direct parser probe as fallback.
            foreach (var parser in _parsers)
            {
                if (await parser.CanHandleAsync(rawDevice, cancellationToken).ConfigureAwait(false))
                {
                    var parserPlan = await parser.BuildMountPlanAsync(rawDevice, cancellationToken).ConfigureAwait(false);
                    return parserPlan with
                    {
                        Notes = $"{parserPlan.Notes} ProbeBytes={read}, Header32={headerHex}, Source=whole-disk",
                        PartitionOffsetBytes = 0,
                        PartitionLengthBytes = rawDevice.Length
                    };
                }
            }

            return new MountPlan(
                rawDevice.DevicePath,
                "unknown",
                TotalBytes: rawDevice.Length,
                Writable: false,
                Notes: $"Raw-disk reader active. ProbeBytes={read}, Header32={headerHex}. No APFS/HFS+/CoreStorage/APM partition detected.",
                PartitionOffsetBytes: 0,
                PartitionLengthBytes: rawDevice.Length
            );
        }
    }

    public async Task<IRawFileSystemProvider> CreateFileSystemProviderAsync(MountPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IRawFileSystemProvider provider;
        if (string.Equals(plan.FileSystemType, "HFS+", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(plan.FileSystemType, "HFSX", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                provider = await HfsPlusRawFileSystemProvider.CreateAsync(plan, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception nativeEx)
            {
                Console.Error.WriteLine($"[HFS+ Native] Falling back to DiscUtils provider: {nativeEx.Message}");
                try
                {
                    provider = await DiscUtilsHfsPlusFileSystemProvider.CreateAsync(plan, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception fallbackEx)
                {
                    throw new InvalidOperationException(
                        $"HFS mount failed. Native provider error: {nativeEx.Message} Fallback provider error: {fallbackEx.Message}",
                        fallbackEx
                    );
                }
            }
        }
        else if (string.Equals(plan.FileSystemType, "APFS", StringComparison.OrdinalIgnoreCase))
        {
            provider = await ApfsRawFileSystemProvider.CreateAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            provider = new ProbeRawFileSystemProvider(plan);
        }

        // Wrap with aggressive caching: 2-min directory/entry TTL, 512KB blocks, 16 read-ahead blocks
        return new CachedRawFileSystemProvider(provider, CacheOptions.Aggressive);
    }

    private static async Task<IReadOnlyList<GptPartitionCandidate>> ReadGptMacPartitionCandidatesAsync(IRawBlockDevice device, CancellationToken cancellationToken)
    {
        var header = new byte[92];
        var headerRead = await RawReadUtil.ReadExactlyAtAsync(device, LbaSize, header, header.Length, cancellationToken).ConfigureAwait(false);
        if (headerRead < 92) return Array.Empty<GptPartitionCandidate>();

        var signature = Encoding.ASCII.GetString(header, 0, 8);
        if (!string.Equals(signature, "EFI PART", StringComparison.Ordinal))
        {
            return Array.Empty<GptPartitionCandidate>();
        }

        var partitionEntryLba = BitConverter.ToInt64(header, 72);
        var entryCount = BitConverter.ToInt32(header, 80);
        var entrySize = BitConverter.ToInt32(header, 84);

        if (partitionEntryLba <= 0 || entryCount <= 0 || entrySize < 128 || entrySize > 1024)
        {
            return Array.Empty<GptPartitionCandidate>();
        }

        var maxEntries = Math.Min(entryCount, 256);
        var result = new List<GptPartitionCandidate>(capacity: 8);
        var entryBuffer = new byte[entrySize];

        for (var i = 0; i < maxEntries; i++)
        {
            var entryOffset = checked(partitionEntryLba * LbaSize + (long)i * entrySize);
            var read = await RawReadUtil.ReadExactlyAtAsync(device, entryOffset, entryBuffer, entryBuffer.Length, cancellationToken).ConfigureAwait(false);
            if (read < entrySize) break;

            var isEmpty = true;
            for (var j = 0; j < 16; j++)
            {
                if (entryBuffer[j] != 0) { isEmpty = false; break; }
            }
            if (isEmpty) continue;

            var typeBytes = new byte[16];
            Buffer.BlockCopy(entryBuffer, 0, typeBytes, 0, 16);
            var typeGuid = new Guid(typeBytes);

            if (typeGuid != GptTypeApfs && typeGuid != GptTypeHfs && typeGuid != GptTypeCoreStorage)
            {
                continue;
            }

            var firstLba = BitConverter.ToInt64(entryBuffer, 32);
            var lastLba = BitConverter.ToInt64(entryBuffer, 40);
            if (firstLba <= 0 || lastLba < firstLba)
            {
                continue;
            }

            var startOffset = checked(firstLba * LbaSize);
            var length = checked((lastLba - firstLba + 1) * LbaSize);
            result.Add(new GptPartitionCandidate(i + 1, typeGuid, startOffset, length));
        }

        return result;
    }

    private readonly record struct GptPartitionCandidate(int Index, Guid TypeGuid, long StartOffset, long Length);

    private readonly record struct MbrPartitionCandidate(int Index, long StartOffset, long Length);

    private readonly record struct ApmPartitionCandidate(int Index, string Name, string TypeName, long StartOffset, long Length);

    private static IReadOnlyList<MbrPartitionCandidate> ReadMbrHfsCandidates(byte[] mbrBuffer)
    {
        // MBR signature check: bytes 510-511 must be 0x55AA
        if (mbrBuffer.Length < 512 || mbrBuffer[510] != 0x55 || mbrBuffer[511] != 0xAA)
            return Array.Empty<MbrPartitionCandidate>();

        var result = new List<MbrPartitionCandidate>(4);
        // 4 primary MBR partition entries start at offset 446, each 16 bytes
        for (int i = 0; i < 4; i++)
        {
            int offset = 446 + i * 16;
            byte partType = mbrBuffer[offset + 4];
            // 0xAF = Apple HFS/HFS+, 0xAF is the standard type for HFS+
            if (partType != 0xAF) continue;

            // LBA start (4 bytes little-endian at offset+8), sector count (4 bytes at offset+12)
            uint lbaStart = BitConverter.ToUInt32(mbrBuffer, offset + 8);
            uint sectorCount = BitConverter.ToUInt32(mbrBuffer, offset + 12);
            if (lbaStart == 0 || sectorCount == 0) continue;

            long startOffset = (long)lbaStart * LbaSize;
            long length = (long)sectorCount * LbaSize;
            result.Add(new MbrPartitionCandidate(i + 1, startOffset, length));
        }

        return result;
    }

    private static async Task<IReadOnlyList<ApmPartitionCandidate>> ReadApmMacPartitionCandidatesAsync(IRawBlockDevice device, CancellationToken cancellationToken)
    {
        var ddrBuffer = new byte[512];
        var ddrRead = await RawReadUtil.ReadExactlyAtAsync(device, 0, ddrBuffer, ddrBuffer.Length, cancellationToken).ConfigureAwait(false);
        if (ddrRead < ddrBuffer.Length)
        {
            return Array.Empty<ApmPartitionCandidate>();
        }

        var signature = BinaryPrimitives.ReadUInt16BigEndian(ddrBuffer.AsSpan(0, 2));
        if (signature != ApmDriverDescriptorSignature)
        {
            return Array.Empty<ApmPartitionCandidate>();
        }

        var blockSize = BinaryPrimitives.ReadUInt16BigEndian(ddrBuffer.AsSpan(2, 2));
        if (blockSize < 512 || blockSize > 4096 || (blockSize & (blockSize - 1)) != 0)
        {
            blockSize = 512;
        }

        var entryBuffer = new byte[blockSize];
        var firstEntryRead = await RawReadUtil.ReadExactlyAtAsync(device, blockSize, entryBuffer, entryBuffer.Length, cancellationToken).ConfigureAwait(false);
        if (firstEntryRead < entryBuffer.Length ||
            BinaryPrimitives.ReadUInt16BigEndian(entryBuffer.AsSpan(0, 2)) != ApmPartitionSignature)
        {
            return Array.Empty<ApmPartitionCandidate>();
        }

        var mapEntryCount = BinaryPrimitives.ReadUInt32BigEndian(entryBuffer.AsSpan(4, 4));
        if (mapEntryCount == 0)
        {
            return Array.Empty<ApmPartitionCandidate>();
        }

        var result = new List<ApmPartitionCandidate>(capacity: 8);
        var maxEntries = (int)Math.Min(mapEntryCount, 256u);
        for (var i = 0; i < maxEntries; i++)
        {
            if (i > 0)
            {
                var entryOffset = checked((long)(i + 1) * blockSize);
                var entryRead = await RawReadUtil.ReadExactlyAtAsync(device, entryOffset, entryBuffer, entryBuffer.Length, cancellationToken).ConfigureAwait(false);
                if (entryRead < entryBuffer.Length)
                {
                    break;
                }
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(entryBuffer.AsSpan(0, 2)) != ApmPartitionSignature)
            {
                continue;
            }

            var startBlock = BinaryPrimitives.ReadUInt32BigEndian(entryBuffer.AsSpan(8, 4));
            var blockCount = BinaryPrimitives.ReadUInt32BigEndian(entryBuffer.AsSpan(12, 4));
            if (startBlock == 0 || blockCount == 0)
            {
                continue;
            }

            var name = DecodeApmString(entryBuffer.AsSpan(16, 32));
            var typeName = DecodeApmString(entryBuffer.AsSpan(48, 32));
            if (!IsInterestingApmPartitionType(typeName))
            {
                continue;
            }

            var startOffset = checked((long)startBlock * blockSize);
            var length = checked((long)blockCount * blockSize);
            if (startOffset >= device.Length || length <= 0)
            {
                continue;
            }

            if (startOffset + length > device.Length)
            {
                length = device.Length - startOffset;
            }

            if (length <= 0)
            {
                continue;
            }

            result.Add(new ApmPartitionCandidate(i + 1, name, typeName, startOffset, length));
        }

        return result;
    }

    private static bool IsInterestingApmPartitionType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        return string.Equals(typeName, "Apple_HFS", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(typeName, "Apple_CoreStorage", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("APFS", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeApmString(ReadOnlySpan<byte> bytes)
    {
        var zeroIndex = bytes.IndexOf((byte)0);
        if (zeroIndex >= 0)
        {
            bytes = bytes[..zeroIndex];
        }

        return Encoding.ASCII.GetString(bytes).Trim();
    }
}

internal sealed class DiscUtilsHfsPlusFileSystemProvider : IRawFileSystemProvider
{
    private readonly IRawBlockDevice _device;
    private readonly RawBlockDeviceStream _stream;
    private readonly HfsPlusFileSystem _fs;

    private DiscUtilsHfsPlusFileSystemProvider(IRawBlockDevice device, RawBlockDeviceStream stream, HfsPlusFileSystem fs, string fsType)
    {
        _device = device;
        _stream = stream;
        _fs = fs;
        FileSystemType = fsType;
    }

    public static async Task<DiscUtilsHfsPlusFileSystemProvider> CreateAsync(MountPlan plan, CancellationToken ct = default)
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
            var start = Math.Max(0, plan.PartitionOffsetBytes);
            var length = plan.PartitionLengthBytes > 0
                ? plan.PartitionLengthBytes
                : Math.Max(0, device.Length - start);
            var stream = new RawBlockDeviceStream(device, start, length, ownsDevice: false);
            try
            {
                var fs = new HfsPlusFileSystem(stream);
                var fsType = string.IsNullOrWhiteSpace(plan.FileSystemType) ? "HFS+" : plan.FileSystemType;
                return new DiscUtilsHfsPlusFileSystemProvider(device, stream, fs, fsType);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public string FileSystemType { get; }
    public long TotalBytes => _fs.Size;
    public long FreeBytes => _fs.AvailableSpace;

    public RawFsEntry? GetEntry(string path)
    {
        var n = NormalizeDiscPath(path);
        try
        {
            if (string.IsNullOrEmpty(n))
            {
                return new RawFsEntry("\\", "ROOT", true, 0, DateTimeOffset.UtcNow, FileAttributes.Directory);
            }

            if (!_fs.Exists(n))
            {
                return null;
            }

            var info = _fs.GetFileSystemInfo(n);
            var name = Path.GetFileName(n.TrimEnd('/', '\\'));
            var isDirectory = _fs.DirectoryExists(n);
            var size = isDirectory ? 0 : _fs.GetFileLength(n);
            return new RawFsEntry(
                NormalizePath(path),
                string.IsNullOrWhiteSpace(name) ? n : name,
                isDirectory,
                size,
                new DateTimeOffset(info.LastWriteTimeUtc),
                info.Attributes
            );
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<RawFsEntry> ListDirectory(string path)
    {
        var n = NormalizeDiscPath(path);
        try
        {
            var entries = _fs.GetFileSystemEntries(n);
            var results = new List<RawFsEntry>(entries.Length);
            foreach (var entryPath in entries)
            {
                var info = _fs.GetFileSystemInfo(entryPath);
                var isDirectory = _fs.DirectoryExists(entryPath);
                var name = Path.GetFileName(entryPath.TrimEnd('/', '\\'));
                var normalized = NormalizePath(entryPath.Replace('/', '\\'));
                var size = isDirectory ? 0 : _fs.GetFileLength(entryPath);
                results.Add(new RawFsEntry(
                    normalized,
                    name,
                    isDirectory,
                    size,
                    new DateTimeOffset(info.LastWriteTimeUtc),
                    info.Attributes
                ));
            }
            return results;
        }
        catch
        {
            return Array.Empty<RawFsEntry>();
        }
    }

    public int ReadFile(string path, long offset, Span<byte> destination)
    {
        if (destination.Length == 0) return 0;
        var n = NormalizeDiscPath(path);
        try
        {
            using var stream = _fs.OpenFile(n, FileMode.Open, FileAccess.Read);
            if (offset < 0 || offset >= stream.Length) return 0;
            stream.Position = offset;
            var temp = new byte[destination.Length];
            var read = stream.Read(temp, 0, temp.Length);
            if (read > 0)
            {
                temp.AsSpan(0, read).CopyTo(destination);
            }
            return read;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        try { _fs.Dispose(); } catch {}
        try { _stream.Dispose(); } catch {}
        try { _device.Dispose(); } catch {}
    }

    private static string NormalizeDiscPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/" || path == "\\") return string.Empty;
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/" || path == "\\") return "\\";
        var p = path.Replace('/', '\\');
        if (!p.StartsWith('\\')) p = "\\" + p.TrimStart('\\');
        return p;
    }
}

internal sealed class ProbeRawFileSystemProvider : IRawFileSystemProvider
{
    private readonly Dictionary<string, RawFsEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _infoBytes;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public ProbeRawFileSystemProvider(MountPlan plan)
    {
        FileSystemType = plan.FileSystemType;
        TotalBytes = Math.Max(1, plan.TotalBytes);
        FreeBytes = 0;

        var infoText =
            $"MacMount Native Raw FS Provider{Environment.NewLine}" +
            $"PhysicalDrivePath: {plan.PhysicalDrivePath}{Environment.NewLine}" +
            $"FileSystemType: {plan.FileSystemType}{Environment.NewLine}" +
            $"TotalBytes: {plan.TotalBytes}{Environment.NewLine}" +
            $"Writable: {plan.Writable}{Environment.NewLine}" +
            $"IsEncrypted: {plan.IsEncrypted}{Environment.NewLine}" +
            $"NeedsPassword: {plan.NeedsPassword}{Environment.NewLine}" +
            $"Notes: {plan.Notes}{Environment.NewLine}";
        _infoBytes = Encoding.UTF8.GetBytes(infoText);

        _entries["\\"] = new RawFsEntry("\\", "ROOT", true, 0, _now, FileAttributes.Directory);
        _entries["\\INFO.txt"] = new RawFsEntry("\\INFO.txt", "INFO.txt", false, _infoBytes.Length, _now, FileAttributes.ReadOnly);
    }

    public string FileSystemType { get; }
    public long TotalBytes { get; }
    public long FreeBytes { get; }

    public RawFsEntry? GetEntry(string path)
    {
        var n = Normalize(path);
        return _entries.TryGetValue(n, out var entry) ? entry : null;
    }

    public IReadOnlyList<RawFsEntry> ListDirectory(string path)
    {
        var n = Normalize(path);
        if (!string.Equals(n, "\\", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<RawFsEntry>();
        }

        return new[] { _entries["\\INFO.txt"] };
    }

    public int ReadFile(string path, long offset, Span<byte> destination)
    {
        var n = Normalize(path);
        if (!string.Equals(n, "\\INFO.txt", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (offset < 0 || offset >= _infoBytes.Length || destination.Length <= 0)
        {
            return 0;
        }

        var available = _infoBytes.Length - (int)offset;
        var count = Math.Min(destination.Length, available);
        _infoBytes.AsSpan((int)offset, count).CopyTo(destination);
        return count;
    }

    public void Dispose()
    {
        // no-op for probe provider
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "\\";
        var p = path.Replace('/', '\\');
        if (!p.StartsWith("\\")) p = "\\" + p.TrimStart('\\');
        return p;
    }
}

internal sealed class HfsPlusRawFileSystemProvider : IRawFileSystemProvider
{
    private readonly IRawBlockDevice _device;
    private readonly HfsPlusNativeReader _reader;
    private readonly bool _writable;
    private readonly object _sync = new();

    // Path-to-entry and path-to-CNID caches, built lazily as directories are listed
    private readonly Dictionary<string, RawFsEntry> _entryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _cnidByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HfsPlusForkInfo> _forkByPath = new(StringComparer.OrdinalIgnoreCase);

    private HfsPlusRawFileSystemProvider(IRawBlockDevice device, HfsPlusNativeReader reader, string fsType, bool writable)
    {
        _device = device;
        _reader = reader;
        _writable = writable;
        FileSystemType = fsType;
        _cnidByPath["\\"] = 2; // root folder CNID
    }

    public static async Task<HfsPlusRawFileSystemProvider> CreateAsync(MountPlan plan, CancellationToken ct = default)
    {
        var basePath = plan.PhysicalDrivePath;
        var hashIdx = basePath.IndexOf('#');
        if (hashIdx > 0)
        {
            basePath = basePath[..hashIdx];
        }

        var factory = new WindowsRawBlockDeviceFactory();
        IRawBlockDevice device;
        if (plan.Writable)
        {
            device = await factory.OpenReadWriteAsync(basePath, ct).ConfigureAwait(false);
        }
        else
        {
            device = await factory.OpenReadOnlyAsync(basePath, ct).ConfigureAwait(false);
        }

        var start = Math.Max(0, plan.PartitionOffsetBytes);

        var reader = await HfsPlusNativeReader.OpenAsync(device, start, ct).ConfigureAwait(false);
        if (reader is null)
        {
            var diagnostic = await HfsPlusNativeReader.DiagnoseOpenFailureAsync(device, start, ct).ConfigureAwait(false);
            device.Dispose();
            throw new InvalidOperationException($"Failed to open HFS+ volume header or catalog B-tree. Diagnostic: {diagnostic}");
        }

        // If writable, disable journaling so writes are safe on external drives
        if (plan.Writable && reader.IsWritable)
        {
            await reader.DisableJournalAsync(ct).ConfigureAwait(false);
        }

        // Probe the root catalog up front so broken HFS+ parses fail before Explorer
        // gets an apparently mounted but empty shell drive.
        var rootItems = await reader.ListDirectoryAsync(2, ct).ConfigureAwait(false);
        if (rootItems.Count == 0)
        {
            Console.Error.WriteLine("[HFS+ Native] Root directory probe returned zero items.");
        }

        var fsType = string.IsNullOrWhiteSpace(plan.FileSystemType) ? "HFS+" : plan.FileSystemType;
        return new HfsPlusRawFileSystemProvider(device, reader, fsType, plan.Writable && reader.IsWritable);
    }

    public string FileSystemType { get; }
    public long TotalBytes => _reader.VolumeHeader.TotalBytes;
    public long FreeBytes => _reader.VolumeHeader.FreeBytes;
    public bool IsWritable => _writable;

    public RawFsEntry? GetEntry(string path)
    {
        var n = NormalizePath(path);
        lock (_sync)
        {
            if (_entryCache.TryGetValue(n, out var cached)) return cached;

            if (n == "\\")
            {
                var root = new RawFsEntry("\\", "ROOT", true, 0, DateTimeOffset.UtcNow, FileAttributes.Directory);
                _entryCache[n] = root;
                return root;
            }
        }

        // If not cached, try listing the parent to populate
        var parentPath = GetParentPath(n);
        ListDirectory(parentPath);

        lock (_sync)
        {
            return _entryCache.TryGetValue(n, out var entry) ? entry : null;
        }
    }

    public IReadOnlyList<RawFsEntry> ListDirectory(string path)
    {
        var n = NormalizePath(path);
        uint cnid;

        lock (_sync)
        {
            if (!_cnidByPath.TryGetValue(n, out cnid))
            {
                return Array.Empty<RawFsEntry>();
            }
        }

        try
        {
            var items = _reader.ListDirectoryAsync(cnid).GetAwaiter().GetResult();
            var results = new List<RawFsEntry>(items.Count);

            lock (_sync)
            {
                foreach (var item in items)
                {
                    var childPath = n == "\\" ? $"\\{item.Name}" : $"{n}\\{item.Name}";
                    var attrs = item.IsDirectory ? FileAttributes.Directory : (_writable ? FileAttributes.Normal : FileAttributes.ReadOnly);
                    var entry = new RawFsEntry(childPath, item.Name, item.IsDirectory, item.Size, item.ModifiedTime, attrs);

                    // Always cache for internal lookups (GetEntry, WriteFile, etc.)
                    _entryCache[childPath] = entry;
                    _cnidByPath[childPath] = item.Cnid;
                    if (item.DataFork is not null)
                    {
                        _forkByPath[childPath] = item.DataFork;
                    }

                    // Only show non-metadata entries to Explorer
                    if (!IsMacMetadata(item.Name))
                    {
                        results.Add(entry);
                    }
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HFS+ Native] ListDirectory({path}) error: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<RawFsEntry>();
        }
    }

    public int ReadFile(string path, long offset, Span<byte> destination)
    {
        if (destination.Length == 0) return 0;
        var n = NormalizePath(path);

        HfsPlusForkInfo? fork;
        uint fileId;
        lock (_sync)
        {
            if (!_forkByPath.TryGetValue(n, out fork)) return 0;
            if (!_cnidByPath.TryGetValue(n, out fileId)) return 0;
        }

        try
        {
            var buf = new byte[destination.Length];
            var read = _reader.ReadFileAsync(fork, fileId, 0x00, offset, buf, destination.Length).GetAwaiter().GetResult();
            if (read > 0)
            {
                buf.AsSpan(0, read).CopyTo(destination);
            }
            return read;
        }
        catch
        {
            return 0;
        }
    }

    public int WriteFile(string path, long offset, ReadOnlySpan<byte> source)
    {
        if (!_writable) throw new InvalidOperationException("Provider is read-only.");
        if (source.Length == 0) return 0;

        var n = NormalizePath(path);
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
            _reader.WriteFileDataAsync(cnid, offset, buf, buf.Length).GetAwaiter().GetResult();

            // Invalidate caches for this path
            InvalidatePath(n);

            return buf.Length;
        }
        catch (Exception ex)
        {
            // Re-throw so the broker's Write callback logs the actual error to its debug
            // log instead of silently returning 0. Without this, write failures cascade
            // (Windows Explorer keeps retrying, the catalog gets into a bad state, and
            // subsequent Creates fail with confusing bounds errors).
            Console.Error.WriteLine($"[HFS+ Native] WriteFile({path}) error: {ex.GetType().Name}: {ex.Message}");
            throw new IOException($"HFS+ WriteFile(path='{path}', cnid={cnid}, offset={offset}, len={source.Length}): {ex.Message}", ex);
        }
    }

    public void CreateFile(string path)
    {
        if (!_writable) throw new InvalidOperationException("Provider is read-only.");

        var n = NormalizePath(path);
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
            var cnid = _reader.CreateFileAsync(parentCnid, fileName).GetAwaiter().GetResult();

            // Invalidate stale parent directory listing BEFORE adding the new entry to cache.
            // If we add first then invalidate, InvalidateParent removes direct children of the
            // parent — including the entry we just added, causing GetEntry to return null.
            InvalidateParent(parentPath);

            lock (_sync)
            {
                _cnidByPath[n] = cnid;
                var entry = new RawFsEntry(n, fileName, false, 0, DateTimeOffset.UtcNow, FileAttributes.Normal);
                _entryCache[n] = entry;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HFS+ Native] CreateFile({path}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void CreateDirectory(string path)
    {
        if (!_writable) throw new InvalidOperationException("Provider is read-only.");

        var n = NormalizePath(path);
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
            var cnid = _reader.CreateFolderAsync(parentCnid, dirName).GetAwaiter().GetResult();

            // Invalidate stale parent directory listing BEFORE adding the new entry to cache.
            // If we add first then invalidate, InvalidateParent removes direct children of the
            // parent — including the entry we just added, causing GetEntry to return null.
            InvalidateParent(parentPath);

            lock (_sync)
            {
                _cnidByPath[n] = cnid;
                var entry = new RawFsEntry(n, dirName, true, 0, DateTimeOffset.UtcNow, FileAttributes.Directory);
                _entryCache[n] = entry;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HFS+ Native] CreateDirectory({path}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void Delete(string path)
    {
        if (!_writable) throw new InvalidOperationException("Provider is read-only.");

        var n = NormalizePath(path);
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
            _reader.DeleteEntryAsync(parentCnid, entryName).GetAwaiter().GetResult();
            InvalidatePath(n);
            InvalidateParent(parentPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HFS+ Native] Delete({path}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void Rename(string oldPath, string newPath)
    {
        if (!_writable) throw new InvalidOperationException("Provider is read-only.");

        var oldN = NormalizePath(oldPath);
        var newN = NormalizePath(newPath);
        var oldParent = GetParentPath(oldN);
        var newParent = GetParentPath(newN);
        var oldName = oldN[(oldN.LastIndexOf('\\') + 1)..];
        var newName = newN[(newN.LastIndexOf('\\') + 1)..];

        // Get existing entry info
        var existing = GetEntry(oldPath);
        if (existing is null) throw new FileNotFoundException($"Entry not found: {oldPath}");

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
            // HFS+ doesn't have a native rename — delete old + create new with same data
            // For files, read data first, then delete, then recreate
            if (existing.IsDirectory)
            {
                _reader.DeleteEntryAsync(oldParentCnid, oldName).GetAwaiter().GetResult();
                _reader.CreateFolderAsync(newParentCnid, newName).GetAwaiter().GetResult();
            }
            else
            {
                // Read existing file data
                byte[]? fileData = null;
                HfsPlusForkInfo? fork;
                lock (_sync) { _forkByPath.TryGetValue(oldN, out fork); }
                if (fork is not null && fork.LogicalSize > 0)
                {
                    fileData = new byte[fork.LogicalSize];
                    _reader.ReadFileAsync(fork, 0, fileData, fileData.Length).GetAwaiter().GetResult();
                }

                _reader.DeleteEntryAsync(oldParentCnid, oldName).GetAwaiter().GetResult();
                _reader.CreateFileAsync(newParentCnid, newName, fileData).GetAwaiter().GetResult();
            }

            InvalidatePath(oldN);
            InvalidatePath(newN);
            InvalidateParent(oldParent);
            if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateParent(newParent);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HFS+ Native] Rename({oldPath} -> {newPath}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void SetFileSize(string path, long newSize)
    {
        if (!_writable) throw new InvalidOperationException("Provider is read-only.");

        var n = NormalizePath(path);
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
            _reader.SetFileSizeAsync(cnid, newSize).GetAwaiter().GetResult();
            InvalidatePath(n);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HFS+ Native] SetFileSize({path}, {newSize}) error: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void Flush()
    {
        if (!_writable) return;
        try
        {
            _reader.FlushVolumeHeaderAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HFS+ Native] Flush error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void InvalidatePath(string normalizedPath)
    {
        lock (_sync)
        {
            _entryCache.Remove(normalizedPath);
            _forkByPath.Remove(normalizedPath);
            // Don't remove CNID mapping — it stays valid
        }
    }

    private void InvalidateParent(string normalizedParentPath)
    {
        // Remove all children from cache — force re-listing from disk
        lock (_sync)
        {
            var prefix = normalizedParentPath == "\\" ? "\\" : normalizedParentPath + "\\";
            var toRemove = new List<string>();
            foreach (var key in _entryCache.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    key.IndexOf('\\', prefix.Length) < 0) // direct children only
                {
                    toRemove.Add(key);
                }
            }
            foreach (var key in toRemove)
            {
                _entryCache.Remove(key);
                _forkByPath.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        try { _reader.Dispose(); } catch {}
        try { _device.Dispose(); } catch {}
    }

    private static readonly HashSet<string> MacMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fseventsd", ".Spotlight-V100", ".Trashes", ".TemporaryItems",
        ".DocumentRevisions-V100", ".vol", ".com.apple.timemachine.donotpresent",
        ".journal", ".journal_info_block",
        ".HFS+ Private Directory Data", "\x00\x00\x00\x00HFS+ Private Data",
        ".DS_Store"
    };

    private static bool IsMacMetadata(string name)
    {
        if (MacMetadataNames.Contains(name)) return true;
        if (name.StartsWith("._", StringComparison.Ordinal)) return true;
        if (name.Contains("HFS+ Private", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/" || path == "\\") return "\\";
        var p = path.Replace('/', '\\');
        if (!p.StartsWith('\\')) p = "\\" + p;
        return p;
    }

    private static string GetParentPath(string path)
    {
        var lastSep = path.LastIndexOf('\\');
        return lastSep <= 0 ? "\\" : path[..lastSep];
    }
}
