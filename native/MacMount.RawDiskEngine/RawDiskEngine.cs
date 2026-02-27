using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils;
using DiscUtils.HfsPlus;

namespace MacMount.RawDiskEngine;

public sealed class RawDiskEngine : IRawDiskEngine
{
    private const int ProbeBytes = 4096;
    private const int LbaSize = 512;
    private static readonly Guid GptTypeApfs = new("7C3457EF-0000-11AA-AA11-00306543ECAC");
    private static readonly Guid GptTypeHfs = new("48465300-0000-11AA-AA11-00306543ECAC");

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

            // Attempt direct parser detection on whole device first.
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

            // Then inspect GPT partitions and probe parser on each Apple partition slice.
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
            }

            return new MountPlan(
                rawDevice.DevicePath,
                "unknown",
                TotalBytes: rawDevice.Length,
                Writable: false,
                Notes: $"Raw-disk reader active. ProbeBytes={read}, Header32={headerHex}. No APFS/HFS+ GPT partition detected.",
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
            provider = HfsPlusRawFileSystemProvider.Create(plan);
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

            if (typeGuid != GptTypeApfs && typeGuid != GptTypeHfs)
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
    private readonly RawBlockDeviceStream _stream;
    private readonly HfsPlusFileSystem _fs;
    private readonly object _sync = new();

    private HfsPlusRawFileSystemProvider(IRawBlockDevice device, RawBlockDeviceStream stream, HfsPlusFileSystem fs)
    {
        _device = device;
        _stream = stream;
        _fs = fs;
        FileSystemType = "HFS+";
    }

    public static HfsPlusRawFileSystemProvider Create(MountPlan plan)
    {
        var basePath = plan.PhysicalDrivePath;
        var hashIdx = basePath.IndexOf("#part", StringComparison.OrdinalIgnoreCase);
        if (hashIdx > 0)
        {
            basePath = basePath[..hashIdx];
        }

        var device = WindowsRawBlockDevice.OpenReadOnly(basePath);
        var start = Math.Max(0, plan.PartitionOffsetBytes);
        var length = plan.PartitionLengthBytes > 0 ? plan.PartitionLengthBytes : Math.Max(0, device.Length - start);
        var stream = new RawBlockDeviceStream(device, start, length, ownsDevice: false);
        var fs = new HfsPlusFileSystem(stream);
        return new HfsPlusRawFileSystemProvider(device, stream, fs);
    }

    public string FileSystemType { get; }
    public long TotalBytes => _fs.Size;
    public long FreeBytes => _fs.AvailableSpace;

    public RawFsEntry? GetEntry(string path)
    {
        var p = Normalize(path);
        lock (_sync)
        {
            try
            {
                if (p == "/")
                {
                    return new RawFsEntry("\\", "ROOT", true, 0, DateTimeOffset.UtcNow, FileAttributes.Directory);
                }

                if (_fs.DirectoryExists(p))
                {
                    var di = _fs.GetDirectoryInfo(p);
                    return new RawFsEntry(ToWinPath(p), di.Name, true, 0, di.LastWriteTimeUtc, FileAttributes.Directory);
                }

                if (_fs.FileExists(p))
                {
                    var fi = _fs.GetFileInfo(p);
                    return new RawFsEntry(ToWinPath(p), fi.Name, false, fi.Length, fi.LastWriteTimeUtc, FileAttributes.ReadOnly);
                }
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public IReadOnlyList<RawFsEntry> ListDirectory(string path)
    {
        var p = Normalize(path);
        var list = new List<RawFsEntry>();
        lock (_sync)
        {
            try
            {
                // Use GetFileSystemInfos() to batch-fetch all child metadata in one directory
                // traversal instead of calling GetEntry() (2-4 disk reads) per entry.
                var dir = _fs.GetDirectoryInfo(p);
                foreach (var fsi in dir.GetFileSystemInfos())
                {
                    if (fsi is DiscDirectoryInfo)
                    {
                        list.Add(new RawFsEntry(ToWinPath(fsi.FullName), fsi.Name, true, 0, fsi.LastWriteTimeUtc, FileAttributes.Directory));
                    }
                    else if (fsi is DiscFileInfo dfi)
                    {
                        list.Add(new RawFsEntry(ToWinPath(fsi.FullName), fsi.Name, false, dfi.Length, fsi.LastWriteTimeUtc, FileAttributes.ReadOnly));
                    }
                }
            }
            catch
            {
                return Array.Empty<RawFsEntry>();
            }
        }
        return list;
    }

    public int ReadFile(string path, long offset, Span<byte> destination)
    {
        if (destination.Length == 0) return 0;
        var p = Normalize(path);
        lock (_sync)
        {
            try
            {
                using var s = _fs.OpenFile(p, FileMode.Open, FileAccess.Read);
                if (offset < 0 || offset >= s.Length) return 0;
                s.Position = offset;
                var rented = ArrayPool<byte>.Shared.Rent(destination.Length);
                try
                {
                    var read = s.Read(rented, 0, destination.Length);
                    if (read > 0)
                    {
                        rented.AsSpan(0, read).CopyTo(destination);
                    }
                    return read;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            catch
            {
                return 0;
            }
        }
    }

    public void Dispose()
    {
        try { _fs.Dispose(); } catch {}
        try { _stream.Dispose(); } catch {}
        try { _device.Dispose(); } catch {}
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "\\") return "/";
        var p = path.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p.TrimStart('/');
        return p;
    }

    private static string ToWinPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return "\\";
        return "\\" + path.Trim('/').Replace('/', '\\');
    }
}
