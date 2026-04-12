using System.Collections.Concurrent;
using System.Buffers;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Fsp;
using MacMount.RawDiskEngine;

EnsureWinFspRuntimePath();

var service = new NativeService(new WinFspMountEngine(), new RawDiskEngine());
await service.RunAsync();

static void EnsureWinFspRuntimePath()
{
    try
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp", "bin")
        };

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var parts = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => string.Equals(p.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var updated = string.IsNullOrWhiteSpace(currentPath)
                ? candidate
                : $"{candidate};{currentPath}";
            Environment.SetEnvironmentVariable("PATH", updated);
            return;
        }
    }
    catch
    {
        // best-effort bootstrap only
    }
}

internal sealed class NativeService
{
    private readonly IMountEngine _engine;
    private readonly IRawDiskEngine _rawDiskEngine;

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public NativeService(IMountEngine engine, IRawDiskEngine rawDiskEngine)
    {
        _engine = engine;
        _rawDiskEngine = rawDiskEngine;
    }

    public async Task RunAsync()
    {
        Console.WriteLine($"MacMount.NativeService starting (pid={Environment.ProcessId})");

        while (true)
        {
            try
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(
                        "macmount.native",
                        PipeDirection.InOut,
                        16,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    );
                }
                catch (UnauthorizedAccessException)
                {
                    // Pipe may already exist with different ACL — wait and retry
                    await Task.Delay(1000);
                    try
                    {
                        server = new NamedPipeServerStream(
                            "macmount.native",
                            PipeDirection.InOut,
                            16,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous
                        );
                    }
                    catch (Exception retryEx)
                    {
                        Console.Error.WriteLine($"Pipe retry failed: {retryEx.Message}");
                        await Task.Delay(2000);
                        continue;
                    }
                }

                await server.WaitForConnectionAsync();

                _ = Task.Run(async () =>
                {
                    using (server)
                    {
                        await HandleClientAsync(server);
                    }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Pipe accept error: {ex.Message}");
                await Task.Delay(200);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        try
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line))
            {
                await writer.WriteLineAsync("{\"ok\":false,\"error\":\"empty request\"}");
                return;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : null;
            var requestId = root.TryGetProperty("requestId", out var reqEl) ? reqEl.GetString() : null;

            object response;
            switch (action)
            {
                case "ping":
                    response = new { ok = true, requestId, service = "MacMount.NativeService", version = "0.3.0-alpha", pid = Environment.ProcessId, elevated = IsElevated() };
                    break;
                case "status":
                    response = BuildStatusResponse(requestId);
                    break;
                case "cache_stats":
                    response = BuildCacheStatsResponse(requestId);
                    break;
                case "mount":
                    response = HandleMount(root, requestId);
                    break;
                case "mount_raw":
                    response = await HandleMountRawAsync(root, requestId);
                    break;
                case "unmount":
                    response = HandleUnmount(root, requestId);
                    break;
                case "analyze_raw":
                    response = await HandleAnalyzeRawAsync(root, requestId);
                    break;
                default:
                    response = new { ok = false, requestId, error = "unsupported action", suggestion = "Use ping|status|cache_stats|mount|mount_raw|unmount|analyze_raw" };
                    break;
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        }
        catch (Exception ex)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    private object BuildStatusResponse(string? requestId)
    {
        var mounted = _engine.GetMounted().Select(m => new
        {
            m.DriveId,
            m.Letter,
            m.Path,
            m.SourcePath,
            m.Engine,
            m.MountedAtUtc
        });

        return new
        {
            ok = true,
            requestId,
            elevated = IsElevated(),
            mounted,
            supportsLocalFixed = _engine.SupportsLocalFixed,
            engine = _engine.EngineName
        };
    }

    private object BuildCacheStatsResponse(string? requestId)
    {
        var stats = _engine.GetCacheStatistics();
        return new
        {
            ok = true,
            requestId,
            cacheStats = stats
        };
    }

    private object HandleMount(JsonElement root, string? requestId)
    {
        var driveId = root.TryGetProperty("driveId", out var driveEl) ? driveEl.GetString() : null;
        var letter = root.TryGetProperty("letter", out var letEl) ? letEl.GetString() : null;
        var sourcePath = root.TryGetProperty("sourcePath", out var srcEl) ? srcEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(letter) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return new { ok = false, requestId, error = "driveId, letter and sourcePath are required" };
        }

        var result = _engine.Mount(driveId, letter, sourcePath);
        if (!result.Ok)
        {
            return new { ok = false, requestId, error = result.Error };
        }

        return new
        {
            ok = true,
            requestId,
            driveId,
            path = result.Path,
            driveLetter = result.Letter,
            engine = _engine.EngineName,
            supportsLocalFixed = _engine.SupportsLocalFixed
        };
    }

    private object HandleUnmount(JsonElement root, string? requestId)
    {
        var driveId = root.TryGetProperty("driveId", out var driveEl) ? driveEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(driveId))
        {
            return new { ok = false, requestId, error = "driveId is required" };
        }

        var result = _engine.Unmount(driveId);
        if (!result.Ok)
        {
            return new { ok = false, requestId, error = result.Error };
        }

        return new { ok = true, requestId, driveId };
    }

    private async Task<object> HandleMountRawAsync(JsonElement root, string? requestId)
    {
        var driveId = root.TryGetProperty("driveId", out var driveEl) ? driveEl.GetString() : null;
        var letter = root.TryGetProperty("letter", out var letEl) ? letEl.GetString() : null;
        var physicalDrivePath = root.TryGetProperty("physicalDrivePath", out var pathEl) ? pathEl.GetString() : null;
        var fileSystemHint = root.TryGetProperty("fileSystemHint", out var hintEl) ? hintEl.GetString() : null;
        var password = root.TryGetProperty("password", out var passwordEl) ? passwordEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(letter) || string.IsNullOrWhiteSpace(physicalDrivePath))
        {
            return new { ok = false, requestId, error = "driveId, letter and physicalDrivePath are required" };
        }

        try
        {
            var plan = await _rawDiskEngine.AnalyzeAsync(
                new MountRequest(physicalDrivePath, fileSystemHint ?? string.Empty, ReadOnly: true)
            ).ConfigureAwait(false);

            if (plan.NeedsPassword && string.IsNullOrWhiteSpace(password))
            {
                return new
                {
                    ok = false,
                    requestId,
                    error = "Encrypted APFS volume requires a password.",
                    needsPassword = true,
                    plan = CreatePlanPayload(plan)
                };
            }

            if (plan.IsEncrypted && !string.IsNullOrWhiteSpace(password))
            {
                return new
                {
                    ok = false,
                    requestId,
                    error = "Native raw provider cannot unlock encrypted APFS volumes yet.",
                    suggestion = "Retry with bridge fallback enabled so the bundled APFS bridge can use the supplied password.",
                    plan = CreatePlanPayload(plan)
                };
            }

            var provider = await _rawDiskEngine.CreateFileSystemProviderAsync(plan).ConfigureAwait(false);
            var result = _engine.MountRawProvider(driveId, letter, plan, provider);
            if (!result.Ok)
            {
                return new { ok = false, requestId, error = result.Error };
            }

            return new
            {
                ok = true,
                requestId,
                driveId,
                path = result.Path,
                driveLetter = result.Letter,
                engine = _engine.EngineName,
                supportsLocalFixed = _engine.SupportsLocalFixed,
                plan = CreatePlanPayload(plan)
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, requestId, error = ex.Message };
        }
    }

    private async Task<object> HandleAnalyzeRawAsync(JsonElement root, string? requestId)
    {
        var physicalDrivePath = root.TryGetProperty("physicalDrivePath", out var driveEl) ? driveEl.GetString() : null;
        var fileSystemHint = root.TryGetProperty("fileSystemHint", out var hintEl) ? hintEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(physicalDrivePath))
        {
            return new { ok = false, requestId, error = "physicalDrivePath is required" };
        }

        try
        {
            var plan = await _rawDiskEngine.AnalyzeAsync(
                new MountRequest(physicalDrivePath, fileSystemHint ?? string.Empty, ReadOnly: true)
            ).ConfigureAwait(false);

            return new
            {
                ok = true,
                requestId,
                plan = CreatePlanPayload(plan)
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, requestId, error = ex.Message };
        }
    }

    private static object CreatePlanPayload(MountPlan plan) => new
    {
        plan.PhysicalDrivePath,
        plan.FileSystemType,
        plan.TotalBytes,
        plan.Writable,
        plan.Notes,
        plan.IsEncrypted,
        plan.NeedsPassword,
        plan.PartitionOffsetBytes,
        plan.PartitionLengthBytes
    };
}

internal interface IMountEngine
{
    string EngineName { get; }
    bool SupportsLocalFixed { get; }
    MountResult Mount(string driveId, string letter, string sourcePath);
    MountResult MountRaw(string driveId, string letter, MountPlan plan);
    MountResult MountRawProvider(string driveId, string letter, MountPlan plan, IRawFileSystemProvider provider);
    MountResult Unmount(string driveId);
    IReadOnlyCollection<MountedDrive> GetMounted();
    object? GetCacheStatistics();
}

internal sealed class WinFspMountEngine : IMountEngine
{
    private readonly ConcurrentDictionary<string, MountedDrive> _mounted = new(StringComparer.OrdinalIgnoreCase);

    public string EngineName => "winfsp";
    public bool SupportsLocalFixed => true;

    public MountResult Mount(string driveId, string letter, string sourcePath)
    {
        letter = NormalizeLetter(letter);
        if (string.IsNullOrEmpty(letter))
        {
            return MountResult.Fail("invalid drive letter");
        }

        if (!Directory.Exists(sourcePath))
        {
            return MountResult.Fail($"sourcePath does not exist: {sourcePath}");
        }

        if (_mounted.TryRemove(driveId, out var existing))
        {
            existing.Dispose();
        }

        try
        {
            var fs = new LocalReadOnlyPassthroughFileSystem(sourcePath);
            return MountWithFileSystem(driveId, letter, sourcePath, fs);
        }
        catch (DllNotFoundException)
        {
            return MountResult.Fail("WinFsp runtime not installed. Install WinFsp first.");
        }
        catch (TypeInitializationException tie)
        {
            var detail = tie.InnerException?.Message ?? tie.Message;
            return MountResult.Fail($"WinFsp initialization failed. Install/repair WinFsp runtime. Detail: {detail}");
        }
        catch (Exception ex)
        {
            return MountResult.Fail(ex.Message);
        }
    }

    public MountResult MountRaw(string driveId, string letter, MountPlan plan)
    {
        letter = NormalizeLetter(letter);
        if (string.IsNullOrEmpty(letter))
        {
            return MountResult.Fail("invalid drive letter");
        }

        if (_mounted.TryRemove(driveId, out var existing))
        {
            existing.Dispose();
        }

        try
        {
            var fs = new RawProbeFileSystem(plan);
            return MountWithFileSystem(driveId, letter, plan.PhysicalDrivePath, fs);
        }
        catch (DllNotFoundException)
        {
            return MountResult.Fail("WinFsp runtime not installed. Install WinFsp first.");
        }
        catch (TypeInitializationException tie)
        {
            var detail = tie.InnerException?.Message ?? tie.Message;
            return MountResult.Fail($"WinFsp initialization failed. Install/repair WinFsp runtime. Detail: {detail}");
        }
        catch (Exception ex)
        {
            return MountResult.Fail(ex.Message);
        }
    }

    public MountResult MountRawProvider(string driveId, string letter, MountPlan plan, IRawFileSystemProvider provider)
    {
        letter = NormalizeLetter(letter);
        if (string.IsNullOrEmpty(letter))
        {
            provider.Dispose();
            return MountResult.Fail("invalid drive letter");
        }

        if (_mounted.TryRemove(driveId, out var existing))
        {
            existing.Dispose();
        }

        try
        {
            var fs = new RawProviderFileSystem(provider, plan);
            return MountWithFileSystem(driveId, letter, plan.PhysicalDrivePath, fs);
        }
        catch (DllNotFoundException)
        {
            provider.Dispose();
            return MountResult.Fail("WinFsp runtime not installed. Install WinFsp first.");
        }
        catch (TypeInitializationException tie)
        {
            provider.Dispose();
            var detail = tie.InnerException?.Message ?? tie.Message;
            return MountResult.Fail($"WinFsp initialization failed. Install/repair WinFsp runtime. Detail: {detail}");
        }
        catch (Exception ex)
        {
            provider.Dispose();
            return MountResult.Fail(ex.Message);
        }
    }

    public MountResult Unmount(string driveId)
    {
        if (_mounted.TryRemove(driveId, out var mounted))
        {
            mounted.Dispose();
            return MountResult.Success(mounted.Path, mounted.Letter);
        }

        return MountResult.Fail("drive not mounted in native engine");
    }

    public IReadOnlyCollection<MountedDrive> GetMounted()
    {
        return _mounted.Values.ToArray();
    }

    public object? GetCacheStatistics()
    {
        // Aggregate cache stats from all mounted drives that use caching
        var stats = new List<object>();
        foreach (var mounted in _mounted.Values)
        {
            if (mounted.FileSystem is RawProviderFileSystem rawFs)
            {
                var providerStats = rawFs.GetCacheStatistics?.Invoke();
                if (providerStats != null)
                {
                    stats.Add(providerStats);
                }
            }
        }
        return stats.Count > 0 ? stats : null;
    }

    private static string NormalizeLetter(string letter)
    {
        if (string.IsNullOrWhiteSpace(letter)) return string.Empty;
        var s = letter.Trim().TrimEnd(':').ToUpperInvariant();
        if (s.Length != 1 || s[0] < 'A' || s[0] > 'Z') return string.Empty;
        return s;
    }

    private MountResult MountWithFileSystem(string driveId, string letter, string sourcePath, FileSystemBase fs)
    {
        var host = new FileSystemHost(fs)
        {
            FileSystemName = "MacMount",
            Prefix = "",
            SectorSize = 4096,
            SectorsPerAllocationUnit = 1,
            CaseSensitiveSearch = false,
            CasePreservedNames = true,
            UnicodeOnDisk = true,
            PersistentAcls = false,
            ReparsePoints = false,
            NamedStreams = false,
            ExtendedAttributes = false
        };

        var mountPoint = letter + ":";
        var preflight = host.Preflight(mountPoint);
        if (preflight != 0)
        {
            host.Dispose();
            return MountResult.Fail($"WinFsp preflight failed: 0x{preflight:X8}");
        }

        var rc = host.Mount(mountPoint, null, false, 0);
        if (rc != 0)
        {
            host.Dispose();
            return MountResult.Fail($"WinFsp mount failed: 0x{rc:X8}");
        }

        var mounted = new MountedDrive(driveId, letter, mountPoint + "\\", sourcePath, EngineName, DateTimeOffset.UtcNow, host, fs);
        _mounted[driveId] = mounted;
        return MountResult.Success(mounted.Path, letter);
    }
}

internal sealed class RawProbeFileSystem : FileSystemBase
{
    private readonly string _infoText;
    private readonly byte[] _infoBytes;
    private readonly DirectoryBuffer _dirBuffer = new();
    private readonly DateTime _now = DateTime.UtcNow;

    public RawProbeFileSystem(MountPlan plan)
    {
        _infoText =
            $"MacMount Raw Probe FileSystem{Environment.NewLine}" +
            $"PhysicalDrivePath: {plan.PhysicalDrivePath}{Environment.NewLine}" +
            $"FileSystemType: {plan.FileSystemType}{Environment.NewLine}" +
            $"TotalBytes: {plan.TotalBytes}{Environment.NewLine}" +
            $"Writable: {plan.Writable}{Environment.NewLine}" +
            $"Notes: {plan.Notes}{Environment.NewLine}";
        _infoBytes = Encoding.UTF8.GetBytes(_infoText);
    }

    public override int GetVolumeInfo(out Fsp.Interop.VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = (ulong)Math.Max(1, _infoBytes.Length);
        volumeInfo.FreeSize = 0;
        return 0;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var p = Normalize(fileName);
        if (p == "\\" || p == string.Empty)
        {
            fileAttributes = (uint)FileAttributes.Directory;
            securityDescriptor = Array.Empty<byte>();
            return 0;
        }
        if (p.Equals("\\INFO.txt", StringComparison.OrdinalIgnoreCase))
        {
            fileAttributes = (uint)FileAttributes.ReadOnly;
            securityDescriptor = Array.Empty<byte>();
            return 0;
        }
        fileAttributes = 0;
        securityDescriptor = Array.Empty<byte>();
        return unchecked((int)0xC0000034);
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode, out object fileDesc, out Fsp.Interop.FileInfo fileInfo, out string normalizedName)
    {
        fileInfo = default;
        normalizedName = Normalize(fileName);
        fileNode = normalizedName;
        fileDesc = normalizedName;

        if (normalizedName == "\\" || normalizedName == string.Empty)
        {
            PopulateInfo(isDirectory: true, size: 0, ref fileInfo);
            normalizedName = "\\";
            return 0;
        }

        if (normalizedName.Equals("\\INFO.txt", StringComparison.OrdinalIgnoreCase))
        {
            PopulateInfo(isDirectory: false, size: _infoBytes.Length, ref fileInfo);
            normalizedName = "\\INFO.txt";
            return 0;
        }

        return unchecked((int)0xC0000034);
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        var p = Normalize(fileNode as string);
        if (p == "\\" || p == string.Empty)
        {
            PopulateInfo(isDirectory: true, size: 0, ref fileInfo);
            return 0;
        }
        if (p.Equals("\\INFO.txt", StringComparison.OrdinalIgnoreCase))
        {
            PopulateInfo(isDirectory: false, size: _infoBytes.Length, ref fileInfo);
            return 0;
        }
        return unchecked((int)0xC0000034);
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var p = Normalize(fileNode as string);
        if (!p.Equals("\\INFO.txt", StringComparison.OrdinalIgnoreCase))
        {
            return unchecked((int)0xC00000BA);
        }

        if ((long)offset >= _infoBytes.Length) return 0;
        var count = (int)Math.Min(length, _infoBytes.Length - (long)offset);
        Marshal.Copy(_infoBytes, (int)offset, buffer, count);
        bytesTransferred = (uint)count;
        return 0;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out Fsp.Interop.FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        return unchecked((int)0xC00000BB);
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        return unchecked((int)0xC00000BB);
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        return unchecked((int)0xC00000BB);
    }

    public override int ReadDirectory(object fileNode, object fileDesc, string pattern, string marker, IntPtr buffer, uint length, out uint bytesTransferred)
    {
        return BufferedReadDirectory(_dirBuffer, fileNode, fileDesc, pattern, marker, buffer, length, out bytesTransferred);
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker, ref object context, out string fileName, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        fileName = null!;

        var p = Normalize(fileNode as string);
        if (!(p == "\\" || p == string.Empty))
        {
            return false;
        }

        var index = context is int i ? i : 0;
        if (index > 0) return false;

        context = 1;
        fileName = "INFO.txt";
        PopulateInfo(isDirectory: false, size: _infoBytes.Length, ref fileInfo);
        return true;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) => unchecked((int)0xC00000BB);
    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) => unchecked((int)0xC00000BB);

    private static string Normalize(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "\\";
        var n = p.Replace('/', '\\');
        if (!n.StartsWith("\\")) n = "\\" + n.TrimStart('\\');
        return n;
    }

    private void PopulateInfo(bool isDirectory, long size, ref Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo.FileAttributes = (uint)(isDirectory ? FileAttributes.Directory : FileAttributes.ReadOnly);
        fileInfo.FileSize = (ulong)Math.Max(0, size);
        fileInfo.AllocationSize = (ulong)Math.Max(0, ((size + 4095) / 4096) * 4096);
        fileInfo.CreationTime = (ulong)_now.ToFileTimeUtc();
        fileInfo.LastAccessTime = (ulong)_now.ToFileTimeUtc();
        fileInfo.LastWriteTime = (ulong)_now.ToFileTimeUtc();
        fileInfo.ChangeTime = fileInfo.LastWriteTime;
        fileInfo.IndexNumber = 0;
        fileInfo.HardLinks = 0;
    }
}

internal sealed class RawProviderFileSystem : FileSystemBase
{
    private readonly IRawFileSystemProvider _provider;
    private readonly MountPlan _plan;
    private readonly DirectoryBuffer _dirBuffer = new();

    public RawProviderFileSystem(IRawFileSystemProvider provider, MountPlan plan)
    {
        _provider = provider;
        _plan = plan;
    }

    public Func<object?>? GetCacheStatistics => _provider is CachedRawFileSystemProvider cached 
        ? () => cached.GetStatistics() 
        : null;

    public override int GetVolumeInfo(out Fsp.Interop.VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = (ulong)Math.Max(1, _provider.TotalBytes);
        volumeInfo.FreeSize = (ulong)Math.Max(0, _provider.FreeBytes);
        return 0;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var entry = _provider.GetEntry(Normalize(fileName));
        if (entry is null)
        {
            fileAttributes = 0;
            securityDescriptor = Array.Empty<byte>();
            return unchecked((int)0xC0000034);
        }

        fileAttributes = (uint)entry.Attributes;
        securityDescriptor = Array.Empty<byte>();
        return 0;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode, out object fileDesc, out Fsp.Interop.FileInfo fileInfo, out string normalizedName)
    {
        fileInfo = default;
        var path = Normalize(fileName);
        var entry = _provider.GetEntry(path);
        if (entry is null)
        {
            fileNode = null!;
            fileDesc = null!;
            normalizedName = path;
            return unchecked((int)0xC0000034);
        }

        fileNode = entry;
        fileDesc = entry;
        normalizedName = path;
        PopulateInfo(entry, ref fileInfo);
        return 0;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileNode is RawFsEntry entry)
        {
            PopulateInfo(entry, ref fileInfo);
            return 0;
        }
        return unchecked((int)0xC000000D);
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        if (fileNode is not RawFsEntry entry || entry.IsDirectory)
        {
            return unchecked((int)0xC00000BA);
        }

        var temp = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            var read = _provider.ReadFile(entry.Path, (long)offset, temp.AsSpan(0, (int)length));
            if (read > 0)
            {
                Marshal.Copy(temp, 0, buffer, read);
            }
            bytesTransferred = (uint)Math.Max(0, read);
            return 0;
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out Fsp.Interop.FileInfo fileInfo)
    { bytesTransferred = 0; fileInfo = default; return unchecked((int)0xC00000BB); }
    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out Fsp.Interop.FileInfo fileInfo)
    { fileInfo = default; return unchecked((int)0xC00000BB); }
    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out Fsp.Interop.FileInfo fileInfo)
    { fileInfo = default; return unchecked((int)0xC00000BB); }

    public override int ReadDirectory(object fileNode, object fileDesc, string pattern, string marker, IntPtr buffer, uint length, out uint bytesTransferred)
    {
        return BufferedReadDirectory(_dirBuffer, fileNode, fileDesc, pattern, marker, buffer, length, out bytesTransferred);
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker, ref object context, out string fileName, out Fsp.Interop.FileInfo fileInfo)
    {
        fileName = null!;
        fileInfo = default;
        if (fileNode is not RawFsEntry entry || !entry.IsDirectory)
        {
            return false;
        }

        if (context is not IEnumerator<RawFsEntry> iterator)
        {
            var entries = _provider.ListDirectory(entry.Path);
            iterator = entries.GetEnumerator();
            context = iterator;
        }

        if (!iterator.MoveNext()) return false;
        var current = iterator.Current;
        fileName = current.Name;
        PopulateInfo(current, ref fileInfo);
        return true;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) => unchecked((int)0xC00000BB);
    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) => unchecked((int)0xC00000BB);

    private static string Normalize(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "\\";
        var n = p.Replace('/', '\\');
        if (!n.StartsWith("\\")) n = "\\" + n.TrimStart('\\');
        return n;
    }

    private static void PopulateInfo(RawFsEntry entry, ref Fsp.Interop.FileInfo fileInfo)
    {
        var size = entry.IsDirectory ? 0L : Math.Max(0, entry.Size);
        fileInfo.FileAttributes = (uint)entry.Attributes;
        fileInfo.FileSize = (ulong)size;
        fileInfo.AllocationSize = (ulong)(((size + 4095) / 4096) * 4096);
        var t = (ulong)entry.LastWriteUtc.UtcDateTime.ToFileTimeUtc();
        fileInfo.CreationTime = t;
        fileInfo.LastAccessTime = t;
        fileInfo.LastWriteTime = t;
        fileInfo.ChangeTime = t;
        fileInfo.IndexNumber = 0;
        fileInfo.HardLinks = 0;
    }
}

internal sealed class LocalReadOnlyPassthroughFileSystem : FileSystemBase
{
    private readonly string _rootPath;
    private readonly DirectoryBuffer _dirBuffer = new();

    public LocalReadOnlyPassthroughFileSystem(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
    }

    public override int GetVolumeInfo(out Fsp.Interop.VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        try
        {
            var root = Path.GetPathRoot(_rootPath) ?? _rootPath;
            var drive = new DriveInfo(root);
            volumeInfo.TotalSize = (ulong)Math.Max(0, drive.TotalSize);
            volumeInfo.FreeSize = (ulong)Math.Max(0, drive.AvailableFreeSpace);
            return 0;
        }
        catch
        {
            volumeInfo.TotalSize = 0;
            volumeInfo.FreeSize = 0;
            return 0;
        }
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var full = ResolvePath(fileName);
        if (Directory.Exists(full))
        {
            fileAttributes = (uint)FileAttributes.Directory;
            securityDescriptor = Array.Empty<byte>();
            return 0;
        }

        if (File.Exists(full))
        {
            fileAttributes = (uint)File.GetAttributes(full);
            securityDescriptor = Array.Empty<byte>();
            return 0;
        }

        fileAttributes = 0;
        securityDescriptor = Array.Empty<byte>();
        return unchecked((int)0xC0000034);
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode, out object fileDesc, out Fsp.Interop.FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        var full = ResolvePath(fileName);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return unchecked((int)0xC0000034);
        }

        var isDirectory = Directory.Exists(full);
        var entry = new Entry(full, isDirectory);
        fileNode = entry;
        fileDesc = entry;
        normalizedName = NormalizeToFsPath(full);
        PopulateFileInfo(entry, ref fileInfo);
        return 0;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileNode is not Entry entry)
        {
            return unchecked((int)0xC000000D);
        }

        PopulateFileInfo(entry, ref fileInfo);
        return 0;
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;

        if (fileNode is not Entry entry)
        {
            return unchecked((int)0xC000000D);
        }

        if (entry.IsDirectory)
        {
            return unchecked((int)0xC00000BA);
        }

        try
        {
            using var stream = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if ((long)offset >= stream.Length)
            {
                bytesTransferred = 0;
                return 0;
            }

            stream.Position = (long)offset;
            var temp = new byte[length];
            var read = stream.Read(temp, 0, (int)length);
            if (read > 0)
            {
                Marshal.Copy(temp, 0, buffer, read);
            }

            bytesTransferred = (uint)read;
            return 0;
        }
        catch (Exception ex)
        {
            return NtStatusFromWin32((uint)Marshal.GetHRForException(ex));
        }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        bytesTransferred = 0;
        return unchecked((int)0xC00000BB);
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        return unchecked((int)0xC00000BB);
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        return unchecked((int)0xC00000BB);
    }

    public override int ReadDirectory(object fileNode, object fileDesc, string pattern, string marker, IntPtr buffer, uint length, out uint bytesTransferred)
    {
        return BufferedReadDirectory(_dirBuffer, fileNode, fileDesc, pattern, marker, buffer, length, out bytesTransferred);
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker, ref object context, out string fileName, out Fsp.Interop.FileInfo fileInfo)
    {
        fileName = null!;
        fileInfo = default;

        if (fileNode is not Entry entry || !entry.IsDirectory)
        {
            return false;
        }

        if (context is not IEnumerator<Entry> iterator)
        {
            var entries = EnumerateDirectory(entry.FullPath, pattern);
            iterator = entries.GetEnumerator();
            context = iterator;
        }

        if (!iterator.MoveNext())
        {
            return false;
        }

        var current = iterator.Current;
        fileName = Path.GetFileName(current.FullPath);
        PopulateFileInfo(current, ref fileInfo);
        return true;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        return unchecked((int)0xC00000BB);
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        return unchecked((int)0xC00000BB);
    }

    private IEnumerable<Entry> EnumerateDirectory(string fullDir, string pattern)
    {
        var dir = new DirectoryInfo(fullDir);
        if (!dir.Exists)
        {
            return Array.Empty<Entry>();
        }

        IEnumerable<FileSystemInfo> items;
        try
        {
            items = dir.EnumerateFileSystemInfos();
        }
        catch
        {
            return Array.Empty<Entry>();
        }

        if (!string.IsNullOrWhiteSpace(pattern) && pattern != "*")
        {
            items = items.Where(i => MatchPattern(i.Name, pattern));
        }

        return items.Select(i => new Entry(i.FullName, (i.Attributes & FileAttributes.Directory) != 0)).ToArray();
    }

    private static bool MatchPattern(string name, string pattern)
    {
        if (pattern == "*" || string.IsNullOrWhiteSpace(pattern)) return true;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private string ResolvePath(string fileName)
    {
        var normalized = string.IsNullOrWhiteSpace(fileName)
            ? ""
            : fileName.Replace('/', Path.DirectorySeparatorChar).TrimStart('\\');

        var combined = Path.GetFullPath(Path.Combine(_rootPath, normalized));
        if (!combined.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return _rootPath;
        }

        return combined;
    }

    private string NormalizeToFsPath(string fullPath)
    {
        if (string.Equals(fullPath, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return "\\";
        }

        var relative = Path.GetRelativePath(_rootPath, fullPath)
            .Replace(Path.DirectorySeparatorChar, '\\');
        return "\\" + relative;
    }

    private static void PopulateFileInfo(Entry entry, ref Fsp.Interop.FileInfo fileInfo)
    {
        FileSystemInfo fsi = entry.IsDirectory
            ? new DirectoryInfo(entry.FullPath)
            : new FileInfo(entry.FullPath);

        if (!fsi.Exists)
        {
            return;
        }

        var attrs = fsi.Attributes;
        var len = entry.IsDirectory ? 0L : ((FileInfo)fsi).Length;

        fileInfo.FileAttributes = (uint)attrs;
        fileInfo.ReparseTag = 0;
        fileInfo.FileSize = (ulong)Math.Max(0, len);
        fileInfo.AllocationSize = (ulong)Math.Max(0, ((len + 4095) / 4096) * 4096);
        fileInfo.CreationTime = ToFileTimeUtc(fsi.CreationTimeUtc);
        fileInfo.LastAccessTime = ToFileTimeUtc(fsi.LastAccessTimeUtc);
        fileInfo.LastWriteTime = ToFileTimeUtc(fsi.LastWriteTimeUtc);
        fileInfo.ChangeTime = fileInfo.LastWriteTime;
        fileInfo.IndexNumber = 0;
        fileInfo.HardLinks = 0;
    }

    private static ulong ToFileTimeUtc(DateTime dt)
    {
        return (ulong)dt.ToFileTimeUtc();
    }

    private sealed record Entry(string FullPath, bool IsDirectory);
}

internal sealed class MountedDrive : IDisposable
{
    public string DriveId { get; }
    public string Letter { get; }
    public string Path { get; }
    public string SourcePath { get; }
    public string Engine { get; }
    public DateTimeOffset MountedAtUtc { get; }
    public FileSystemBase? FileSystem { get; }
    private readonly FileSystemHost _host;

    public MountedDrive(string driveId, string letter, string path, string sourcePath, string engine, DateTimeOffset mountedAtUtc, FileSystemHost host, FileSystemBase? fileSystem = null)
    {
        DriveId = driveId;
        Letter = letter;
        Path = path;
        SourcePath = sourcePath;
        Engine = engine;
        MountedAtUtc = mountedAtUtc;
        _host = host;
        FileSystem = fileSystem;
    }

    public void Dispose()
    {
        try
        {
            _host.Unmount();
        }
        catch
        {
            // ignore
        }

        try
        {
            _host.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}

internal readonly record struct MountResult(bool Ok, string? Error, string? Path, string? Letter)
{
    public static MountResult Success(string path, string letter) => new(true, null, path, letter);
    public static MountResult Fail(string error) => new(false, error, null, null);
}
