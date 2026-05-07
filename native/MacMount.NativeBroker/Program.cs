using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using System.IO.Enumeration;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Fsp;
using MacMount.RawDiskEngine;

Mutex? singleInstanceMutex = null;
try
{
    singleInstanceMutex = new Mutex(initiallyOwned: true, "Global\\GKMacOpener.NativeBroker", out var ownsMutex);
    if (!ownsMutex)
    {
        Console.Error.WriteLine("MacMount.NativeBroker already running; exiting duplicate process.");
        return;
    }
}
catch (UnauthorizedAccessException)
{
    singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\GKMacOpener.NativeBroker", out var ownsMutex);
    if (!ownsMutex)
    {
        Console.Error.WriteLine("MacMount.NativeBroker already running in this logon session; exiting duplicate process.");
        return;
    }
}

using (singleInstanceMutex)
{
    EnsureWinFspRuntimePath();

    var broker = new BrokerService();
    await broker.RunAsync();
}

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

internal sealed class BrokerService
{
    private readonly ConcurrentDictionary<string, MountedDrive> _mounted = new(StringComparer.OrdinalIgnoreCase);
    private readonly IRawDiskEngine _rawDiskEngine = new RawDiskEngine();

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

    public async Task RunAsync()
    {
        Console.WriteLine($"MacMount.NativeBroker starting (pid={Environment.ProcessId})");

        while (true)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    "macmount.broker",
                    PipeDirection.InOut,
                    16,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                );

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
                Console.Error.WriteLine($"Broker accept error: {ex.Message}");
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
            var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
            var requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;

            object response;
            if (string.Equals(action, "ping", StringComparison.Ordinal))
            {
                response = new { ok = true, requestId, service = "MacMount.NativeBroker", pid = Environment.ProcessId, elevated = IsElevated() };
            }
            else if (string.Equals(action, "status", StringComparison.Ordinal))
            {
                response = new
                {
                    ok = true,
                    requestId,
                    elevated = IsElevated(),
                    mounted = _mounted.Values.Select(x => new { x.DriveId, x.Letter, x.Path, x.SourceSummary, x.MountedAtUtc })
                };
            }
            else if (string.Equals(action, "mount_probe", StringComparison.Ordinal))
            {
                response = HandleMountProbe(root, requestId);
            }
            else if (string.Equals(action, "mount_passthrough", StringComparison.Ordinal))
            {
                response = HandleMountPassthrough(root, requestId);
            }
            else if (string.Equals(action, "update_volume_info", StringComparison.Ordinal))
            {
                response = HandleUpdateVolumeInfo(root, requestId);
            }
            else if (string.Equals(action, "mount_raw_provider", StringComparison.Ordinal))
            {
                response = await HandleMountRawProviderAsync(root, requestId).ConfigureAwait(false);
            }
            else if (string.Equals(action, "unmount", StringComparison.Ordinal))
            {
                response = HandleUnmount(root, requestId);
            }
            else
            {
                response = new { ok = false, requestId, error = "unsupported action", suggestion = "Use ping|status|mount_probe|mount_passthrough|update_volume_info|mount_raw_provider|unmount" };
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        }
        catch (Exception ex)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    private object HandleMountProbe(JsonElement root, string? requestId)
    {
        var driveId = root.TryGetProperty("driveId", out var d) ? d.GetString() : null;
        var letter = root.TryGetProperty("letter", out var l) ? l.GetString() : null;
        var sourceSummary = root.TryGetProperty("sourceSummary", out var s) ? s.GetString() : "raw";
        var infoText = root.TryGetProperty("infoText", out var i) ? i.GetString() : "MacMount probe";

        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(letter))
        {
            return new { ok = false, requestId, error = "driveId and letter are required" };
        }

        letter = NormalizeLetter(letter);
        if (string.IsNullOrWhiteSpace(letter)) return new { ok = false, requestId, error = "invalid letter" };

        if (_mounted.TryRemove(driveId, out var existing))
        {
            existing.Dispose();
        }

        try
        {
            var fs = new BrokerProbeFileSystem(infoText ?? "MacMount probe");
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
                return new { ok = false, requestId, error = $"WinFsp preflight failed: 0x{preflight:X8}" };
            }

            var rc = host.Mount(mountPoint, null, false, 0);
            if (rc != 0)
            {
                host.Dispose();
                return new { ok = false, requestId, error = $"WinFsp mount failed: 0x{rc:X8}" };
            }

            var mounted = new MountedDrive(driveId, letter, mountPoint + "\\", sourceSummary ?? "raw", DateTimeOffset.UtcNow, host);
            _mounted[driveId] = mounted;
            return new { ok = true, requestId, driveId, path = mounted.Path, driveLetter = letter, broker = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, requestId, error = ex.ToString() };
        }
    }

    private object HandleMountPassthrough(JsonElement root, string? requestId)
    {
        var driveId = root.TryGetProperty("driveId", out var d) ? d.GetString() : null;
        var letter = root.TryGetProperty("letter", out var l) ? l.GetString() : null;
        var sourcePath = root.TryGetProperty("sourcePath", out var sp) ? sp.GetString() : null;
        var totalBytes = root.TryGetProperty("totalBytes", out var tb) && tb.ValueKind == JsonValueKind.Number ? tb.GetInt64() : 0L;
        var freeBytes = root.TryGetProperty("freeBytes", out var fb) && fb.ValueKind == JsonValueKind.Number ? fb.GetInt64() : 0L;

        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(letter) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return new { ok = false, requestId, error = "driveId, letter and sourcePath are required" };
        }

        letter = NormalizeLetter(letter);
        if (string.IsNullOrWhiteSpace(letter)) return new { ok = false, requestId, error = "invalid letter" };

        if (!Directory.Exists(sourcePath))
        {
            return new { ok = false, requestId, error = $"sourcePath not reachable: {sourcePath}" };
        }

        sourcePath = ResolveUserFacingRoot(sourcePath);

        if (_mounted.TryRemove(driveId, out var existing))
        {
            existing.Dispose();
        }

        try
        {
            var fs = new BrokerPassthroughFileSystem(sourcePath, totalBytes, freeBytes);
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
                return new { ok = false, requestId, error = $"WinFsp preflight failed: 0x{preflight:X8}" };
            }

            var rc = host.Mount(mountPoint, null, false, 0);
            if (rc != 0)
            {
                host.Dispose();
                return new { ok = false, requestId, error = $"WinFsp mount failed: 0x{rc:X8}" };
            }

            var mounted = new MountedDrive(driveId, letter, mountPoint + "\\", sourcePath, DateTimeOffset.UtcNow, host, fs);
            _mounted[driveId] = mounted;
            fs.WarmupRoot();
            return new { ok = true, requestId, driveId, path = mounted.Path, driveLetter = letter, broker = true, mountType = "passthrough" };
        }
        catch (Exception ex)
        {
            return new { ok = false, requestId, error = ex.ToString() };
        }
    }

    private object HandleUpdateVolumeInfo(JsonElement root, string? requestId)
    {
        var driveId = root.TryGetProperty("driveId", out var d) ? d.GetString() : null;
        var totalBytes = root.TryGetProperty("totalBytes", out var tb) && tb.ValueKind == JsonValueKind.Number ? tb.GetInt64() : 0L;
        var freeBytes = root.TryGetProperty("freeBytes", out var fb) && fb.ValueKind == JsonValueKind.Number ? fb.GetInt64() : 0L;

        if (string.IsNullOrWhiteSpace(driveId))
        {
            return new { ok = false, requestId, error = "driveId is required" };
        }

        if (!_mounted.TryGetValue(driveId, out var mounted))
        {
            return new { ok = false, requestId, error = "drive not mounted" };
        }

        if (!mounted.UpdateVolumeInfo(totalBytes, freeBytes))
        {
            return new { ok = false, requestId, error = "mounted filesystem does not support live volume info updates" };
        }

        return new { ok = true, requestId, driveId, totalBytes = Math.Max(0, totalBytes), freeBytes = Math.Max(0, freeBytes) };
    }

    private async Task<object> HandleMountRawProviderAsync(JsonElement root, string? requestId)
    {
        var driveId = root.TryGetProperty("driveId", out var d) ? d.GetString() : null;
        var letter = root.TryGetProperty("letter", out var l) ? l.GetString() : null;
        var physicalDrivePath = root.TryGetProperty("physicalDrivePath", out var p) ? p.GetString() : null;
        var fileSystemHint = root.TryGetProperty("fileSystemHint", out var h) ? h.GetString() : null;
        var password = root.TryGetProperty("password", out var pw) ? pw.GetString() : null;

        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(letter) || string.IsNullOrWhiteSpace(physicalDrivePath))
        {
            return new { ok = false, requestId, error = "driveId, letter and physicalDrivePath are required" };
        }

        letter = NormalizeLetter(letter);
        if (string.IsNullOrWhiteSpace(letter)) return new { ok = false, requestId, error = "invalid letter" };

        if (_mounted.TryRemove(driveId, out var existing))
        {
            try { existing.Dispose(); } catch { /* best-effort: clear old mount before replacing */ }
        }

        IRawFileSystemProvider? provider = null;
        FileSystemHost? host = null;
        try
        {
            var plan = await _rawDiskEngine.AnalyzeAsync(
                new MountRequest(physicalDrivePath, fileSystemHint ?? string.Empty, ReadOnly: false)
            ).ConfigureAwait(false);

            // Short-circuit T2 / Apple Silicon hardware-bound encryption: there is
            // no password we can accept that would unlock these drives because the
            // unwrap key never leaves the original Mac's Secure Enclave. Surface a
            // clear message instead of asking the user for a password we can't use.
            if (plan.HardwareBound)
            {
                return new
                {
                    ok = false,
                    requestId,
                    error = "This drive is encrypted with hardware-bound keys (T2 chip or Apple Silicon Secure Enclave) and can only be unlocked on the original Mac.",
                    needsPassword = false,
                    hardwareBound = true,
                    suggestion = "Connect this drive to the Mac it was created on, decrypt it there (diskutil apfs decryptVolume), then re-attach to Windows.",
                    plan = new
                    {
                        plan.PhysicalDrivePath,
                        plan.FileSystemType,
                        plan.TotalBytes,
                        plan.Writable,
                        plan.Notes,
                        plan.IsEncrypted,
                        plan.NeedsPassword,
                        plan.HardwareBound,
                        plan.PartitionOffsetBytes,
                        plan.PartitionLengthBytes
                    }
                };
            }

            var fsType = plan.FileSystemType ?? string.Empty;
            var isApfs = string.Equals(fsType, "APFS", StringComparison.OrdinalIgnoreCase);
            var isHfs = string.Equals(fsType, "HFS+", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fsType, "HFSX", StringComparison.OrdinalIgnoreCase);

            if (plan.NeedsPassword && string.IsNullOrWhiteSpace(password))
            {
                return new
                {
                    ok = false,
                    requestId,
                    error = isApfs
                        ? "Encrypted APFS volume requires a password."
                        : "Encrypted or locked HFS volume cannot be mounted as plain HFS+.",
                    needsPassword = true,
                    suggestion = isApfs
                        ? "Enter the disk password and retry."
                        : "This looks like an encrypted HFS/CoreStorage-style volume. Native HFS unlock is not implemented yet; decrypt it on a Mac first, then retry.",
                    plan = new
                    {
                        plan.PhysicalDrivePath,
                        plan.FileSystemType,
                        plan.TotalBytes,
                        plan.Writable,
                        plan.Notes,
                        plan.IsEncrypted,
                        plan.NeedsPassword,
                        plan.HardwareBound,
                        plan.PartitionOffsetBytes,
                        plan.PartitionLengthBytes
                    }
                };
            }

            if (plan.IsEncrypted && !isApfs)
            {
                return new
                {
                    ok = false,
                    requestId,
                    error = isHfs
                        ? "Encrypted HFS/CoreStorage-style volumes are detected but cannot be unlocked by the native HFS provider yet."
                        : $"Encrypted filesystem '{fsType}' is not supported by the native unlock path yet.",
                    needsPassword = false,
                    suggestion = "Use a Mac to decrypt or convert the drive to an unencrypted external volume, then retry.",
                    plan = new
                    {
                        plan.PhysicalDrivePath,
                        plan.FileSystemType,
                        plan.TotalBytes,
                        plan.Writable,
                        plan.Notes,
                        plan.IsEncrypted,
                        plan.NeedsPassword,
                        plan.HardwareBound,
                        plan.PartitionOffsetBytes,
                        plan.PartitionLengthBytes
                    }
                };
            }

            if (plan.IsEncrypted && isApfs && !string.IsNullOrWhiteSpace(password))
            {
                var vek = await TryUnlockEncryptedApfsAsync(plan, password).ConfigureAwait(false);
                if (vek is null)
                {
                    return new
                    {
                        ok = false,
                        requestId,
                        error = "Failed to unlock encrypted APFS volume. Wrong password or unsupported encryption type.",
                        needsPassword = true,
                        plan = new
                        {
                            plan.PhysicalDrivePath,
                            plan.FileSystemType,
                            plan.TotalBytes,
                            plan.Writable,
                            plan.Notes,
                            plan.IsEncrypted,
                            plan.NeedsPassword,
                            plan.HardwareBound,
                            plan.PartitionOffsetBytes,
                            plan.PartitionLengthBytes
                        }
                    };
                }
                plan = plan with { IsEncrypted = false, NeedsPassword = false, EncryptionKey = vek, Notes = $"{plan.Notes} Encrypted volume unlocked." };
            }

            if (string.Equals(fsType, "CoreStorage", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    ok = false,
                    requestId,
                    error = "CoreStorage/FileVault 1 drives are not yet supported.",
                    suggestion = "This is an older Mac encryption format. Use a Mac to decrypt the drive first (diskutil cs revert), or use APFS encryption on newer macOS versions.",
                    plan = new
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
                    }
                };
            }

            var supportsRawProvider =
                string.Equals(fsType, "HFS+", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fsType, "HFSX", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fsType, "APFS", StringComparison.OrdinalIgnoreCase);
            if (!supportsRawProvider)
            {
                return new
                {
                    ok = false,
                    requestId,
                    error = $"Raw provider does not yet support filesystem '{fsType}'.",
                    plan = new
                    {
                        plan.PhysicalDrivePath,
                        plan.FileSystemType,
                        plan.TotalBytes,
                        plan.Writable,
                        plan.Notes
                    }
                };
            }

            provider = await _rawDiskEngine.CreateFileSystemProviderAsync(plan).ConfigureAwait(false);

            var fs = new BrokerRawProviderFileSystem(provider);
            host = new FileSystemHost(fs)
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
                // Dispose in correct order; swallow secondary exceptions so the
                // original error code is preserved in the returned response.
                try { host.Dispose(); } catch { }
                try { provider.Dispose(); } catch { }
                return new { ok = false, requestId, error = $"WinFsp preflight failed: 0x{preflight:X8}" };
            }

            var rc = host.Mount(mountPoint, null, false, 0);
            if (rc != 0)
            {
                try { host.Dispose(); } catch { }
                try { provider.Dispose(); } catch { }
                return new { ok = false, requestId, error = $"WinFsp mount failed: 0x{rc:X8}" };
            }

            var mounted = new MountedDrive(driveId, letter, mountPoint + "\\", physicalDrivePath, DateTimeOffset.UtcNow, host, resource: provider);
            _mounted[driveId] = mounted;

            return new
            {
                ok = true,
                requestId,
                driveId,
                path = mounted.Path,
                driveLetter = letter,
                broker = true,
                mountType = "raw_provider",
                plan = new
                {
                    plan.PhysicalDrivePath,
                    plan.FileSystemType,
                    plan.TotalBytes,
                    plan.Writable,
                    plan.Notes
                }
            };
        }
        catch (Exception ex)
        {
            try { host?.Dispose(); } catch {}
            try { provider?.Dispose(); } catch {}
            return new { ok = false, requestId, error = ex.ToString() };
        }
    }

    private object HandleUnmount(JsonElement root, string? requestId)
    {
        var driveId = root.TryGetProperty("driveId", out var d) ? d.GetString() : null;
        if (string.IsNullOrWhiteSpace(driveId)) return new { ok = false, requestId, error = "driveId is required" };

        if (_mounted.TryRemove(driveId, out var mounted))
        {
            mounted.Dispose();
            return new { ok = true, requestId, driveId };
        }

        return new { ok = false, requestId, error = "drive not mounted" };
    }

    private static string NormalizeLetter(string input)
    {
        var s = input.Trim().TrimEnd(':').ToUpperInvariant();
        return s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z' ? s : string.Empty;
    }

    private static string ResolveUserFacingRoot(string inputPath)
    {
        try
        {
            var rootCandidate = Path.Combine(inputPath, "root");
            var hasRoot = Directory.Exists(rootCandidate);
            var hasPrivate = Directory.Exists(Path.Combine(inputPath, "private-dir"));
            if (hasRoot && hasPrivate)
            {
                return rootCandidate;
            }
        }
        catch
        {
            // keep original input path
        }

        return inputPath;
    }

    private async Task<byte[]?> TryUnlockEncryptedApfsAsync(MountPlan plan, string password)
    {
        try
        {
            var drivePath = plan.PhysicalDrivePath;
            var hashIdx = drivePath.IndexOf("#part", StringComparison.OrdinalIgnoreCase);
            if (hashIdx > 0) drivePath = drivePath[..hashIdx];

            var device = await new WindowsRawBlockDeviceFactory().OpenReadOnlyAsync(drivePath).ConfigureAwait(false);
            using (device)
            {
                var offset = Math.Max(0L, plan.PartitionOffsetBytes);
                var blockSize = 4096u;
                var keyManager = new ApfsKeyManager(device, (ulong)offset, blockSize);

                var volumeUuid = Guid.Empty;
                if (!string.IsNullOrEmpty(plan.Notes))
                {
                    var uuidMatch = System.Text.RegularExpressions.Regex.Match(plan.Notes, @"VolumeUuid=([0-9a-fA-F-]{36})");
                    if (uuidMatch.Success)
                    {
                        Guid.TryParse(uuidMatch.Groups[1].Value, out volumeUuid);
                    }
                }

                if (volumeUuid == Guid.Empty) return null;

                return await keyManager.TryUnlockVolumeAsync(volumeUuid, password).ConfigureAwait(false);
            }
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class BrokerProbeFileSystem : FileSystemBase
{
    private readonly byte[] _infoBytes;
    private readonly DirectoryBuffer _dirBuffer = new();
    private readonly DateTime _now = DateTime.UtcNow;

    public BrokerProbeFileSystem(string info)
    {
        _infoBytes = Encoding.UTF8.GetBytes(info + Environment.NewLine);
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
        securityDescriptor = Array.Empty<byte>();
        if (p == "\\") { fileAttributes = (uint)FileAttributes.Directory; return 0; }
        if (p.Equals("\\INFO.txt", StringComparison.OrdinalIgnoreCase)) { fileAttributes = (uint)FileAttributes.ReadOnly; return 0; }
        fileAttributes = 0; return unchecked((int)0xC0000034);
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode, out object fileDesc, out Fsp.Interop.FileInfo fileInfo, out string normalizedName)
    {
        fileInfo = default;
        normalizedName = Normalize(fileName);
        fileNode = normalizedName;
        fileDesc = normalizedName;

        if (normalizedName == "\\") { PopulateInfo(true, 0, ref fileInfo); return 0; }
        if (normalizedName.Equals("\\INFO.txt", StringComparison.OrdinalIgnoreCase)) { normalizedName = "\\INFO.txt"; PopulateInfo(false, _infoBytes.Length, ref fileInfo); return 0; }
        return unchecked((int)0xC0000034);
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        var p = Normalize(fileNode as string);
        if (p == "\\") { PopulateInfo(true, 0, ref fileInfo); return 0; }
        if (p.Equals("\\INFO.txt", StringComparison.OrdinalIgnoreCase)) { PopulateInfo(false, _infoBytes.Length, ref fileInfo); return 0; }
        return unchecked((int)0xC0000034);
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var p = Normalize(fileNode as string);
        if (!p.Equals("\\INFO.txt", StringComparison.OrdinalIgnoreCase)) return unchecked((int)0xC00000BA);
        if ((long)offset >= _infoBytes.Length) return 0;
        var count = (int)Math.Min(length, _infoBytes.Length - (long)offset);
        Marshal.Copy(_infoBytes, (int)offset, buffer, count);
        bytesTransferred = (uint)count;
        return 0;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out Fsp.Interop.FileInfo fileInfo)
    { bytesTransferred = 0; fileInfo = default; return unchecked((int)0xC00000BB); }
    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out Fsp.Interop.FileInfo fileInfo)
    { fileInfo = default; return unchecked((int)0xC00000BB); }
    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out Fsp.Interop.FileInfo fileInfo)
    { fileInfo = default; return unchecked((int)0xC00000BB); }

    public override int ReadDirectory(object fileNode, object fileDesc, string pattern, string marker, IntPtr buffer, uint length, out uint bytesTransferred)
    { return BufferedReadDirectory(_dirBuffer, fileNode, fileDesc, pattern, marker, buffer, length, out bytesTransferred); }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker, ref object context, out string fileName, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        fileName = null!;
        var p = Normalize(fileNode as string);
        if (p != "\\") return false;
        var index = context is int i ? i : 0;
        if (index > 0) return false;
        context = 1;
        fileName = "INFO.txt";
        PopulateInfo(false, _infoBytes.Length, ref fileInfo);
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
        var t = (ulong)_now.ToFileTimeUtc();
        fileInfo.CreationTime = t;
        fileInfo.LastAccessTime = t;
        fileInfo.LastWriteTime = t;
        fileInfo.ChangeTime = t;
        fileInfo.IndexNumber = 0;
        fileInfo.HardLinks = 0;
    }
}

internal sealed class BrokerRawProviderFileSystem : FileSystemBase
{
    private readonly IRawFileSystemProvider _provider;
    private readonly DirectoryBuffer _dirBuffer = new();
    private static readonly string _debugLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MacMount", "winfsp_debug.log");

    private static void DebugLog(string msg)
    {
        // Logging is unconditional — gated previously behind #if DEBUG which excluded
        // Release builds (the only build the installer ships). For diagnostics we want
        // these write/create failures captured even on Release.
        try { File.AppendAllText(_debugLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    // WinFsp constants for Create disposition
    private const uint FILE_CREATE = 0x00000002;
    private const uint FILE_OVERWRITE_IF = 0x00000005;
    // FILE_DIRECTORY_FILE and FILE_DELETE_ON_CLOSE are inherited from FileSystemBase

    public BrokerRawProviderFileSystem(IRawFileSystemProvider provider)
    {
        _provider = provider;
    }

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

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc, out Fsp.Interop.FileInfo fileInfo, out string normalizedName)
    {
        fileInfo = default;
        var path = Normalize(fileName);
        fileNode = null!;
        fileDesc = null!;
        normalizedName = path;

        DebugLog($"Create: path='{path}' writable={_provider.IsWritable} opts=0x{createOptions:X} alloc={allocationSize}");

        if (!_provider.IsWritable)
            return unchecked((int)0xC00000BB); // STATUS_MEDIA_WRITE_PROTECTED

        try
        {
            if ((createOptions & FILE_DIRECTORY_FILE) != 0)
            {
                DebugLog($"Create: mkdir '{path}'");
                _provider.CreateDirectory(path);
            }
            else
            {
                DebugLog($"Create: mkfile '{path}'");
                _provider.CreateFile(path);
                if (allocationSize > 0)
                {
                    try { _provider.SetFileSize(path, (long)allocationSize); } catch (Exception sz) { DebugLog($"Create: SetFileSize failed: {sz.Message}"); }
                }
            }

            var entry = _provider.GetEntry(path);
            if (entry is null)
            {
                DebugLog($"Create: GetEntry returned null after create for '{path}'");
                return unchecked((int)0xC0000001);
            }

            fileNode = entry;
            fileDesc = entry;
            PopulateInfo(entry, ref fileInfo);
            DebugLog($"Create OK: '{path}' isDir={entry.IsDirectory} size={entry.Size}");
            return 0;
        }
        catch (Exception ex)
        {
            DebugLog($"Create FAILED '{path}': {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return unchecked((int)0xC0000001);
        }
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

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        if (!_provider.IsWritable)
            return unchecked((int)0xC00000BB);

        if (fileNode is not RawFsEntry entry)
            return unchecked((int)0xC000000D);

        try
        {
            _provider.SetFileSize(entry.Path, 0); // truncate
            if (allocationSize > 0)
                _provider.SetFileSize(entry.Path, (long)allocationSize);

            var updated = _provider.GetEntry(entry.Path);
            if (updated != null) PopulateInfo(updated, ref fileInfo);
            else PopulateInfo(entry, ref fileInfo);
            return 0;
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
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
    {
        bytesTransferred = 0;
        fileInfo = default;

        if (!_provider.IsWritable)
            return unchecked((int)0xC00000BB);

        if (fileNode is not RawFsEntry entry || entry.IsDirectory)
            return unchecked((int)0xC00000BA);

        try
        {
            DebugLog($"Write: path='{entry.Path}' offset={offset} len={length} endOfFile={writeToEndOfFile}");
            var temp = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                Marshal.Copy(buffer, temp, 0, (int)length);
                // Re-query live size for append writes — entry was captured at Open() time
                // and may be stale if another write has extended the file since.
                var writeOffset = writeToEndOfFile
                    ? (_provider.GetEntry(entry.Path)?.Size ?? entry.Size)
                    : (long)offset;
                var written = _provider.WriteFile(entry.Path, writeOffset, temp.AsSpan(0, (int)length));
                bytesTransferred = (uint)Math.Max(0, written);
                DebugLog($"Write OK: path='{entry.Path}' written={written}");

                var updated = _provider.GetEntry(entry.Path);
                if (updated != null) PopulateInfo(updated, ref fileInfo);
                else PopulateInfo(entry, ref fileInfo);

                return 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(temp);
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Write FAILED: path='{entry.Path}' {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return unchecked((int)0xC0000001);
        }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileNode is RawFsEntry entry)
        {
            PopulateInfo(entry, ref fileInfo);
            return 0;
        }
        return unchecked((int)0xC000000D);
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;

        if (!_provider.IsWritable)
            return unchecked((int)0xC00000BB);

        if (fileNode is not RawFsEntry entry)
            return unchecked((int)0xC000000D);

        try
        {
            _provider.SetFileSize(entry.Path, (long)newSize);
            var updated = _provider.GetEntry(entry.Path);
            if (updated != null) PopulateInfo(updated, ref fileInfo);
            else PopulateInfo(entry, ref fileInfo);
            return 0;
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
    }

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

        if (!iterator.MoveNext())
        {
            iterator.Dispose();
            return false;
        }

        var current = iterator.Current;
        fileName = current.Name;
        PopulateInfo(current, ref fileInfo);
        return true;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        if (!_provider.IsWritable) return unchecked((int)0xC00000BB);
        return 0; // allow deletion
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        DebugLog($"Cleanup: path='{fileName}' flags=0x{flags:X}");
        // If DELETE_ON_CLOSE flag is set, delete the file/directory
        if ((flags & 1) != 0 && _provider.IsWritable) // FspCleanupDelete = 1
        {
            try
            {
                var path = Normalize(fileName);
                DebugLog($"Cleanup: DELETE_ON_CLOSE -> Delete('{path}')");
                _provider.Delete(path);
            }
            catch (Exception ex)
            {
                DebugLog($"Cleanup: Delete failed for '{fileName}': {ex.Message}");
                Console.Error.WriteLine($"[WinFsp] Cleanup delete failed: {ex.Message}");
            }
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        if (!_provider.IsWritable) return unchecked((int)0xC00000BB);

        try
        {
            var oldPath = Normalize(fileName);
            var newPath = Normalize(newFileName);
            if (!replaceIfExists && _provider.GetEntry(newPath) != null)
                return unchecked((int)0xC0000035); // STATUS_OBJECT_NAME_COLLISION
            _provider.Rename(oldPath, newPath);
            return 0;
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
    }

    public override int Flush(object fileNode, object fileDesc, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            _provider.Flush();
            if (fileNode is RawFsEntry entry)
            {
                var updated = _provider.GetEntry(entry.Path);
                if (updated != null) PopulateInfo(updated, ref fileInfo);
                else PopulateInfo(entry, ref fileInfo);
            }
            return 0;
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
    }

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

internal sealed class BrokerPassthroughFileSystem : FileSystemBase
{
    private readonly string _rootPath;
    private readonly string _localCacheRoot;
    private readonly bool _enableMetadataCache;
    private readonly bool _enableLocalMirrorCache;
    private readonly bool _enableAggressivePrefetch;
    private readonly DirectoryBuffer _dirBuffer = new();
    private long _volumeTotalBytes;
    private long _volumeFreeBytes;
    private readonly ConcurrentDictionary<string, CachedDirectory> _dirCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedEntry> _entryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedPrefix> _prefixCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedBlob> _blobCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _dirSyncInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DirCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EntryCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PrefixCacheTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BlobCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(25);
    private const int SlowOpLogThresholdMs = 500;
    private const int PrefixBytesPerFile = 128 * 1024;
    private const long MaxPrefixCacheBytes = 128L * 1024L * 1024L;
    private const long MaxBlobCacheBytes = 768L * 1024L * 1024L;
    private const long MaxBlobFileBytes = 32L * 1024L * 1024L;
    private const int MaxSyncEntriesPerDir = 160;
    private long _prefixCacheBytes = 0;
    private long _blobCacheBytes = 0;
    private readonly SemaphoreSlim _prefetchLimiter = new(4);
    private int _warmupStarted = 0;
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif"
    };

    public BrokerPassthroughFileSystem(string rootPath, long totalBytes = 0, long freeBytes = 0)
    {
        _rootPath = Path.GetFullPath(rootPath);
        var isUncBacked = _rootPath.StartsWith("\\\\", StringComparison.Ordinal);
        _enableMetadataCache =
            !isUncBacked ||
            string.Equals(Environment.GetEnvironmentVariable("MACMOUNT_ENABLE_UNC_METADATA_CACHE"), "1", StringComparison.Ordinal);
        _enableLocalMirrorCache =
            string.Equals(Environment.GetEnvironmentVariable("MACMOUNT_ENABLE_LOCAL_MIRROR_CACHE"), "1", StringComparison.Ordinal) &&
            !isUncBacked;
        _enableAggressivePrefetch =
            string.Equals(Environment.GetEnvironmentVariable("MACMOUNT_ENABLE_AGGRESSIVE_PREFETCH"), "1", StringComparison.Ordinal);
        _localCacheRoot = BuildLocalCacheRoot(_rootPath);
        if (_enableLocalMirrorCache)
        {
            try { Directory.CreateDirectory(_localCacheRoot); } catch {}
        }
        _volumeTotalBytes = Math.Max(0, totalBytes);
        _volumeFreeBytes = Math.Max(0, freeBytes);
        if (_volumeTotalBytes > 0 && _volumeFreeBytes > _volumeTotalBytes) {
            _volumeFreeBytes = _volumeTotalBytes;
        }
        if (_volumeTotalBytes > 0 && _volumeFreeBytes == 0) {
            // if caller didn't provide free space, fall back to non-zero placeholder
            _volumeFreeBytes = _volumeTotalBytes;
        }
    }

    public void WarmupRoot()
    {
        if (Interlocked.Exchange(ref _warmupStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var queued = 0;
            var processed = 0;
            var queue = new Queue<(string Path, int Depth)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue((_rootPath, 0));
            seen.Add(_rootPath);

            try
            {
                while (queue.Count > 0)
                {
                    if (sw.Elapsed > WarmupTimeout) break;
                    if (processed >= 1200) break;

                    var (dirPath, depth) = queue.Dequeue();
                    var entries = EnumerateDirectory(dirPath, "*").ToArray();
                    processed += entries.Length;

                    if (depth >= 1) continue;

                    foreach (var e in entries)
                    {
                        if (!e.IsDirectory) continue;
                        var child = e.EffectivePath;
                        if (seen.Add(child))
                        {
                            queue.Enqueue((child, depth + 1));
                            queued++;
                            if (queued >= 500) break;
                        }
                    }

                    if (queued >= 500) break;
                }
            }
            catch
            {
                // best-effort warmup only
            }

            Console.WriteLine($"[perf] warmup complete root={_rootPath} elapsedMs={sw.ElapsedMilliseconds} processedEntries={processed}");
        });
    }

    public void UpdateVolumeInfo(long totalBytes, long freeBytes)
    {
        var total = Math.Max(0, totalBytes);
        var free = Math.Max(0, freeBytes);
        if (total > 0 && free > total)
        {
            free = total;
        }

        Interlocked.Exchange(ref _volumeTotalBytes, total);
        Interlocked.Exchange(ref _volumeFreeBytes, free);
    }

    public override int GetVolumeInfo(out Fsp.Interop.VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        var hintedTotal = Interlocked.Read(ref _volumeTotalBytes);
        var hintedFree = Interlocked.Read(ref _volumeFreeBytes);
        if (TryGetLiveDiskSpace(out var liveTotal, out var liveFree))
        {
            volumeInfo.TotalSize = (ulong)liveTotal;
            volumeInfo.FreeSize = (ulong)Math.Min(liveFree, liveTotal);
            return 0;
        }

        if (hintedTotal > 0)
        {
            volumeInfo.TotalSize = (ulong)hintedTotal;
            volumeInfo.FreeSize = (ulong)Math.Min(hintedFree, hintedTotal);
            return 0;
        }

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

    private bool TryGetLiveDiskSpace(out long totalBytes, out long freeBytes)
    {
        totalBytes = 0;
        freeBytes = 0;

        try
        {
            var path = _rootPath.EndsWith(Path.DirectorySeparatorChar) || _rootPath.EndsWith(Path.AltDirectorySeparatorChar)
                ? _rootPath
                : _rootPath + Path.DirectorySeparatorChar;
            if (!GetDiskFreeSpaceEx(path, out var available, out var total, out _))
            {
                return false;
            }

            var liveTotal = (long)Math.Min(total, (ulong)long.MaxValue);
            var liveFree = (long)Math.Min(available, (ulong)long.MaxValue);
            if (liveTotal <= 0)
            {
                return false;
            }

            // WSL UNC paths can report the WSL ext4 root if the Linux mount has
            // disappeared. Never replace the Mac volume hint with an unrelated
            // backing filesystem size; that is how Explorer shows convincing but
            // false capacity for a stale folder.
            var hintedTotal = Interlocked.Read(ref _volumeTotalBytes);
            if (hintedTotal > 0)
            {
                var delta = Math.Abs(liveTotal - hintedTotal);
                var allowedDelta = Math.Max(64L * 1024L * 1024L, hintedTotal / 20L);
                if (delta > allowedDelta)
                {
                    return false;
                }
            }

            totalBytes = liveTotal;
            freeBytes = Math.Max(0, Math.Min(liveFree, liveTotal));
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        if (ContainsHiddenSegment(fileName))
        {
            fileAttributes = 0;
            securityDescriptor = Array.Empty<byte>();
            return unchecked((int)0xC0000034);
        }

        var full = ResolvePath(fileName);
        if (TryGetEntryCached(full, resolveReparse: true, out var entry))
        {
            fileAttributes = (uint)entry.Attributes;
            securityDescriptor = Array.Empty<byte>();
            return 0;
        }
        fileAttributes = 0;
        securityDescriptor = Array.Empty<byte>();
        return unchecked((int)0xC0000034);
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc, out Fsp.Interop.FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;

        if (ContainsHiddenSegment(fileName))
        {
            return unchecked((int)0xC0000034);
        }

        var full = ResolvePath(fileName);
        normalizedName = NormalizeToFsPath(full);

        if (TryGetEntryCached(full, resolveReparse: true, out _))
        {
            return unchecked((int)0xC0000035);
        }

        try
        {
            var isDirectory = (createOptions & FILE_DIRECTORY_FILE) != 0;
            FileStream? stream = null;
            if (isDirectory)
            {
                Directory.CreateDirectory(full);
            }
            else
            {
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                stream = OpenReadWriteStream(full, FileMode.CreateNew);
                if (allocationSize > 0)
                {
                    stream.SetLength((long)allocationSize);
                }
            }

            InvalidatePathCaches(full);
            if (!TryCreateEntry(full, out var entry, resolveReparse: true))
            {
                stream?.Dispose();
                return unchecked((int)0xC0000001);
            }

            var handle = new OpenHandle(entry, stream);
            fileNode = handle;
            fileDesc = handle;
            normalizedName = NormalizeToFsPath(entry.FullPath);
            PopulateFileInfo(entry, ref fileInfo);
            return 0;
        }
        catch (IOException)
        {
            return unchecked((int)0xC0000035);
        }
        catch (UnauthorizedAccessException)
        {
            return unchecked((int)0xC0000022);
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode, out object fileDesc, out Fsp.Interop.FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;

        if (ContainsHiddenSegment(fileName))
        {
            return unchecked((int)0xC0000034);
        }

        var full = ResolvePath(fileName);
        if (!TryGetEntryCached(full, resolveReparse: true, out var entry))
        {
            return unchecked((int)0xC0000034);
        }

        FileStream? stream = null;
        if (!entry.IsDirectory)
        {
            try
            {
                var readPath = GetReadPath(entry);
                stream = OpenReadWriteStream(readPath, FileMode.Open);
            }
            catch
            {
                try
                {
                    var readPath = GetReadPath(entry);
                    stream = OpenReadStream(readPath);
                }
                catch
                {
                    return unchecked((int)0xC0000034);
                }
            }
        }

        var handle = new OpenHandle(entry, stream);
        fileNode = handle;
        fileDesc = handle;
        normalizedName = NormalizeToFsPath(entry.FullPath);
        PopulateFileInfo(entry, ref fileInfo);
        return 0;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileNode is OpenHandle h)
        {
            PopulateFileInfo(h.Entry, ref fileInfo);
            return 0;
        }
        if (fileNode is Entry e)
        {
            PopulateFileInfo(e, ref fileInfo);
            return 0;
        }
        return unchecked((int)0xC000000D);
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileDesc is OpenHandle h)
        {
            h.Dispose();
        }
        else if (fileNode is OpenHandle h2)
        {
            h2.Dispose();
        }
    }

    public override int Flush(object fileNode, object fileDesc, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        if (GetHandle(fileNode, fileDesc) is { } h)
        {
            try
            {
                h.Stream?.Flush(true);
            }
            catch
            {
                return unchecked((int)0xC0000001);
            }

            if (TryCreateEntry(h.Entry.FullPath, out var updated, resolveReparse: true))
            {
                PopulateFileInfo(updated, ref fileInfo);
            }
            else
            {
                PopulateFileInfo(h.Entry, ref fileInfo);
            }
            return 0;
        }
        if (fileNode is Entry e)
        {
            PopulateFileInfo(e, ref fileInfo);
            return 0;
        }
        return 0;
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        OpenHandle? handle = null;
        if (fileDesc is OpenHandle hd) handle = hd;
        else if (fileNode is OpenHandle hn) handle = hn;

        Entry entry;
        if (handle is not null) entry = handle.Entry;
        else if (fileNode is Entry e) entry = e;
        else
        {
            return unchecked((int)0xC000000D);
        }
        if (entry.IsDirectory)
        {
            return unchecked((int)0xC00000BA);
        }

        if (TryReadFromBlobCache(entry.EffectivePath, offset, length, buffer, out bytesTransferred))
        {
            return 0;
        }

        if (TryReadFromPrefixCache(entry.EffectivePath, offset, length, buffer, out bytesTransferred))
        {
            return 0;
        }

        try
        {
            var stream = handle?.Stream;
            var tempHandle = false;
            if (stream is null)
            {
                var readPath = GetReadPath(entry);
                stream = OpenReadStream(readPath);
                handle = new OpenHandle(entry, stream);
                tempHandle = true;
            }
            if (handle is null)
            {
                return unchecked((int)0xC000000D);
            }

            try
            {
                lock (handle.Sync)
                {
                    if ((long)offset >= stream.Length)
                    {
                        bytesTransferred = 0;
                        return 0;
                    }

                    stream.Position = (long)offset;
                    var rented = ArrayPool<byte>.Shared.Rent((int)length);
                    try
                    {
                        var read = stream.Read(rented, 0, (int)length);
                        if (read > 0)
                        {
                            Marshal.Copy(rented, 0, buffer, read);
                        }
                        bytesTransferred = (uint)read;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }
            finally
            {
                if (tempHandle) handle.Dispose();
            }
            return 0;
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out Fsp.Interop.FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;

        var handle = GetHandle(fileNode, fileDesc);
        Entry entry;
        if (handle is not null) entry = handle.Entry;
        else if (fileNode is Entry e) entry = e;
        else return unchecked((int)0xC000000D);

        if (entry.IsDirectory)
        {
            return unchecked((int)0xC00000BA);
        }

        FileStream? tempStream = null;
        try
        {
            var stream = handle?.Stream;
            if (stream is null || !stream.CanWrite)
            {
                tempStream = OpenReadWriteStream(entry.EffectivePath, FileMode.OpenOrCreate);
                stream = tempStream;
            }

            var rented = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                Marshal.Copy(buffer, rented, 0, (int)length);
                lock (handle?.Sync ?? stream)
                {
                    stream.Position = writeToEndOfFile ? stream.Length : (long)offset;
                    stream.Write(rented, 0, (int)length);
                    bytesTransferred = length;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            InvalidatePathCaches(entry.FullPath);
            if (TryCreateEntry(entry.FullPath, out var updated, resolveReparse: true))
            {
                PopulateFileInfo(updated, ref fileInfo);
            }
            else
            {
                PopulateFileInfo(entry, ref fileInfo);
            }
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return unchecked((int)0xC0000022);
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
        finally
        {
            tempStream?.Dispose();
        }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;

        var entry = GetEntry(fileNode, fileDesc);
        if (entry is null)
        {
            return unchecked((int)0xC000000D);
        }

        try
        {
            var path = entry.EffectivePath;
            if (fileAttributes != 0 && fileAttributes != uint.MaxValue)
            {
                File.SetAttributes(path, (FileAttributes)fileAttributes);
            }
            if (creationTime > 0) File.SetCreationTimeUtc(path, DateTime.FromFileTimeUtc((long)creationTime));
            if (lastAccessTime > 0) File.SetLastAccessTimeUtc(path, DateTime.FromFileTimeUtc((long)lastAccessTime));
            if (lastWriteTime > 0) File.SetLastWriteTimeUtc(path, DateTime.FromFileTimeUtc((long)lastWriteTime));

            InvalidatePathCaches(entry.FullPath);
            if (TryCreateEntry(entry.FullPath, out var updated, resolveReparse: true))
            {
                PopulateFileInfo(updated, ref fileInfo);
            }
            else
            {
                PopulateFileInfo(entry, ref fileInfo);
            }
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return unchecked((int)0xC0000022);
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;

        var handle = GetHandle(fileNode, fileDesc);
        Entry entry;
        if (handle is not null) entry = handle.Entry;
        else if (fileNode is Entry e) entry = e;
        else return unchecked((int)0xC000000D);

        if (entry.IsDirectory)
        {
            return unchecked((int)0xC00000BA);
        }

        FileStream? tempStream = null;
        try
        {
            var stream = handle?.Stream;
            if (stream is null || !stream.CanWrite)
            {
                tempStream = OpenReadWriteStream(entry.EffectivePath, FileMode.OpenOrCreate);
                stream = tempStream;
            }

            lock (handle?.Sync ?? stream)
            {
                stream.SetLength((long)newSize);
            }

            InvalidatePathCaches(entry.FullPath);
            if (TryCreateEntry(entry.FullPath, out var updated, resolveReparse: true))
            {
                PopulateFileInfo(updated, ref fileInfo);
            }
            else
            {
                PopulateFileInfo(entry, ref fileInfo);
            }
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return unchecked((int)0xC0000022);
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
        finally
        {
            tempStream?.Dispose();
        }
    }

    public override int ReadDirectory(object fileNode, object fileDesc, string pattern, string marker, IntPtr buffer, uint length, out uint bytesTransferred)
    {
        return BufferedReadDirectory(_dirBuffer, fileNode, fileDesc, pattern, marker, buffer, length, out bytesTransferred);
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker, ref object context, out string fileName, out Fsp.Interop.FileInfo fileInfo)
    {
        fileInfo = default;
        fileName = null!;
        Entry entry;
        if (fileNode is OpenHandle h) entry = h.Entry;
        else if (fileNode is Entry e) entry = e;
        else return false;

        if (!entry.IsDirectory)
        {
            return false;
        }

        if (context is not IEnumerator<Entry> iterator)
        {
            var entries = EnumerateDirectory(entry.EffectivePath, pattern);
            iterator = entries.GetEnumerator();
            context = iterator;
        }

        if (!iterator.MoveNext()) return false;
        var current = iterator.Current;
        fileName = Path.GetFileName(current.FullPath);
        PopulateFileInfo(current, ref fileInfo);
        return true;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.Equals(fileName.Trim(), "\\", StringComparison.Ordinal))
        {
            return unchecked((int)0xC0000022);
        }

        var full = ResolvePath(fileName);
        return File.Exists(full) || Directory.Exists(full)
            ? 0
            : unchecked((int)0xC0000034);
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if ((flags & 1) == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(fileName) || string.Equals(fileName.Trim(), "\\", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var full = ResolvePath(fileName);
            DeletePathWithRetry(full);
            InvalidatePathCaches(full);
            InvalidateParentDirectoryCache(full);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WinFsp] Passthrough delete failed for '{fileName}': {ex.GetType().Name}: {ex.Message}");
            try
            {
                var full = ResolvePath(fileName);
                InvalidatePathCaches(full);
                InvalidateParentDirectoryCache(full);
            }
            catch
            {
                // cache invalidation is best-effort
            }
        }
    }

    private static void DeletePathWithRetry(string full)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                DeletePathOnce(full);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(75 * (attempt + 1));
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }
    }

    private static void DeletePathOnce(string full)
    {
        if (Directory.Exists(full))
        {
            ClearAttributesForDelete(full);
            Directory.Delete(full, recursive: true);
            return;
        }

        if (File.Exists(full))
        {
            ClearAttributesForDelete(full);
            File.Delete(full);
        }
    }

    private static void ClearAttributesForDelete(string full)
    {
        try
        {
            if (File.Exists(full))
            {
                File.SetAttributes(full, FileAttributes.Normal);
                return;
            }

            if (!Directory.Exists(full)) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch {}
            }
            try { File.SetAttributes(full, FileAttributes.Directory); } catch {}
        }
        catch
        {
            // Attribute changes are only a compatibility aid for Windows delete semantics.
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        if (ContainsHiddenSegment(fileName) || ContainsHiddenSegment(newFileName))
        {
            return unchecked((int)0xC0000034);
        }

        var oldPath = ResolvePath(fileName);
        var newPath = ResolvePath(newFileName);

        try
        {
            if (!replaceIfExists && (File.Exists(newPath) || Directory.Exists(newPath)))
            {
                return unchecked((int)0xC0000035);
            }

            var parent = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (Directory.Exists(oldPath))
            {
                if (replaceIfExists && Directory.Exists(newPath))
                {
                    Directory.Delete(newPath, recursive: true);
                }
                Directory.Move(oldPath, newPath);
            }
            else
            {
                if (replaceIfExists && File.Exists(newPath))
                {
                    File.Delete(newPath);
                }
                File.Move(oldPath, newPath);
            }

            InvalidatePathCaches(oldPath);
            InvalidatePathCaches(newPath);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return unchecked((int)0xC0000022);
        }
        catch
        {
            return unchecked((int)0xC0000001);
        }
    }

    private IEnumerable<Entry> EnumerateDirectory(string fullDir, string pattern)
    {
        var sw = Stopwatch.StartNew();
        var cacheKey = fullDir;
        var nowUtc = DateTimeOffset.UtcNow;
        var dirStamp = GetDirectoryStampUtc(fullDir);

        if (_enableMetadataCache && _dirCache.TryGetValue(cacheKey, out var cached))
        {
            if ((nowUtc - cached.CreatedUtc) <= DirCacheTtl && cached.DirectoryStampUtc == dirStamp)
            {
                if (sw.ElapsedMilliseconds > SlowOpLogThresholdMs)
                {
                    Console.WriteLine($"[perf] dir-list cache-hit path={fullDir} elapsedMs={sw.ElapsedMilliseconds} entries={cached.Entries.Length}");
                }
                return ApplyPattern(cached.Entries, pattern);
            }
            _dirCache.TryRemove(cacheKey, out _);
        }

        var dir = new DirectoryInfo(fullDir);
        if (!dir.Exists) return Array.Empty<Entry>();
        Entry[] entries;
        try
        {
            entries = EnumerateDirectoryFast(fullDir);
        }
        catch
        {
            // Fallback path.
            IEnumerable<FileSystemInfo> items;
            try { items = dir.EnumerateFileSystemInfos(); } catch { return Array.Empty<Entry>(); }
            var results = new List<Entry>();
            foreach (var i in items)
            {
                if (IsHiddenName(i.Name)) continue;
                if (TryCreateEntry(i, out var e, resolveReparse: false, computeLength: false))
                {
                    results.Add(e);
                }
            }
            entries = results.ToArray();
        }

        if (_enableMetadataCache)
        {
            _dirCache[cacheKey] = new CachedDirectory(nowUtc, dirStamp, entries);
            // Prime non-reparse cache so Explorer follow-up probes avoid extra stat calls.
            foreach (var e in entries)
            {
                _entryCache["N|" + e.FullPath] = new CachedEntry(nowUtc, e);
            }
        }
        QueueThumbnailPrefetch(entries);
        if (_enableLocalMirrorCache)
        {
            QueueDirectorySync(fullDir, entries);
        }
        if (sw.ElapsedMilliseconds > SlowOpLogThresholdMs)
        {
            Console.WriteLine($"[perf] dir-list cache-miss path={fullDir} elapsedMs={sw.ElapsedMilliseconds} entries={entries.Length}");
        }
        return ApplyPattern(entries, pattern);
    }

    private static Entry[] EnumerateDirectoryFast(string fullDir)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
            MatchType = MatchType.Simple
        };

        var enumerable = new FileSystemEnumerable<Entry>(
            fullDir,
            (ref FileSystemEntry fse) =>
            {
                var name = fse.FileName.ToString();
                var full = fse.ToFullPath();
                var attrs = (FileAttributes)fse.Attributes;
                var isDir = fse.IsDirectory;
                var length = isDir ? 0L : fse.Length;
                var ctime = fse.CreationTimeUtc.UtcDateTime;
                var atime = fse.LastAccessTimeUtc.UtcDateTime;
                var mtime = fse.LastWriteTimeUtc.UtcDateTime;
                return new Entry(full, full, isDir, attrs, length, ctime, atime, mtime);
            },
            options
        )
        {
            ShouldIncludePredicate = (ref FileSystemEntry fse) =>
            {
                var name = fse.FileName;
                return !(name.Length > 0 && name[0] == '.');
            }
        };

        return enumerable.ToArray();
    }

    private static IEnumerable<Entry> ApplyPattern(IEnumerable<Entry> entries, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
        {
            return entries;
        }
        return entries.Where(e => MatchPattern(Path.GetFileName(e.FullPath), pattern));
    }

    private static bool MatchPattern(string name, string pattern)
    {
        if (pattern == "*" || string.IsNullOrWhiteSpace(pattern)) return true;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsHiddenName(string name)
    {
        // Dot-prefixed files are normal on macOS (.DS_Store, .git, app config,
        // shell profiles). Do not hide or block them in the Windows projection.
        return false;
    }

    private static bool ContainsHiddenSegment(string? fsPath)
    {
        if (string.IsNullOrWhiteSpace(fsPath)) return false;
        var cleaned = fsPath.Replace('/', '\\').Trim('\\');
        if (string.IsNullOrEmpty(cleaned)) return false;
        var segments = cleaned.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (IsHiddenName(segment)) return true;
        }
        return false;
    }

    private string ResolvePath(string fileName)
    {
        var normalized = string.IsNullOrWhiteSpace(fileName) ? "" : fileName.Replace('/', Path.DirectorySeparatorChar).TrimStart('\\');
        var combined = Path.GetFullPath(Path.Combine(_rootPath, normalized));
        if (!combined.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase)) return Path.Combine(_rootPath, "__invalid__");
        return combined;
    }

    private bool TryGetEntryCached(string fullPath, bool resolveReparse, out Entry entry)
    {
        var cacheKey = (resolveReparse ? "R|" : "N|") + fullPath;
        if (_enableMetadataCache && _entryCache.TryGetValue(cacheKey, out var cached))
        {
            if ((DateTimeOffset.UtcNow - cached.CreatedUtc) <= EntryCacheTtl)
            {
                entry = cached.Entry;
                return true;
            }
            _entryCache.TryRemove(cacheKey, out _);
        }

        if (TryCreateEntry(fullPath, out entry, resolveReparse))
        {
            if (_enableMetadataCache)
            {
                _entryCache[cacheKey] = new CachedEntry(DateTimeOffset.UtcNow, entry);
            }
            return true;
        }

        return false;
    }

    private void InvalidatePathCaches(string fullPath)
    {
        _entryCache.TryRemove("R|" + fullPath, out _);
        _entryCache.TryRemove("N|" + fullPath, out _);
        _prefixCache.TryRemove(fullPath, out _);
        _blobCache.TryRemove(fullPath, out _);

        try
        {
            var parent = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                _dirCache.TryRemove(parent, out _);
            }
        }
        catch
        {
            // cache invalidation is best-effort
        }
    }

    private void InvalidateParentDirectoryCache(string fullPath)
    {
        try
        {
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                _dirCache.TryRemove(parent, out _);
            }
        }
        catch
        {
            // cache invalidation is best-effort
        }
    }

    private static OpenHandle? GetHandle(object fileNode, object fileDesc)
    {
        if (fileDesc is OpenHandle hd) return hd;
        if (fileNode is OpenHandle hn) return hn;
        return null;
    }

    private static Entry? GetEntry(object fileNode, object fileDesc)
    {
        if (GetHandle(fileNode, fileDesc) is { } handle) return handle.Entry;
        if (fileNode is Entry entry) return entry;
        if (fileDesc is Entry descEntry) return descEntry;
        return null;
    }

    private string NormalizeToFsPath(string fullPath)
    {
        if (string.Equals(fullPath, _rootPath, StringComparison.OrdinalIgnoreCase)) return "\\";
        var relative = Path.GetRelativePath(_rootPath, fullPath).Replace(Path.DirectorySeparatorChar, '\\');
        return "\\" + relative;
    }

    private static void PopulateFileInfo(Entry entry, ref Fsp.Interop.FileInfo fileInfo)
    {
        var attrs = entry.Attributes;
        var len = entry.IsDirectory ? 0L : Math.Max(0, entry.Length);
        fileInfo.FileAttributes = (uint)attrs;
        fileInfo.ReparseTag = 0;
        fileInfo.FileSize = (ulong)Math.Max(0, len);
        fileInfo.AllocationSize = (ulong)Math.Max(0, ((len + 4095) / 4096) * 4096);
        fileInfo.CreationTime = (ulong)entry.CreationTimeUtc.ToFileTimeUtc();
        fileInfo.LastAccessTime = (ulong)entry.LastAccessTimeUtc.ToFileTimeUtc();
        fileInfo.LastWriteTime = (ulong)entry.LastWriteTimeUtc.ToFileTimeUtc();
        fileInfo.ChangeTime = fileInfo.LastWriteTime;
        fileInfo.IndexNumber = 0;
        fileInfo.HardLinks = 0;
    }

    private static bool TryCreateEntry(string fullPath, out Entry entry, bool resolveReparse, bool computeLength = true)
    {
        entry = default!;
        try
        {
            var fsi = Directory.Exists(fullPath)
                ? (FileSystemInfo)new DirectoryInfo(fullPath)
                : new FileInfo(fullPath);
            if (!fsi.Exists) return false;
            return TryCreateEntry(fsi, out entry, resolveReparse, computeLength);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateEntry(FileSystemInfo fsi, out Entry entry, bool resolveReparse, bool computeLength = true)
    {
        entry = default!;
        try
        {
            var fullPath = fsi.FullName;
            var attrs = fsi.Attributes;
            var isDir = (attrs & FileAttributes.Directory) != 0;
            var effectivePath = fullPath;

            // Resolve reparse links on-demand so traversal works, but skip during large listings for speed.
            if (resolveReparse && (attrs & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo? target = null;
                try { target = Directory.ResolveLinkTarget(fullPath, false); } catch { }
                if (target == null)
                {
                    try { target = File.ResolveLinkTarget(fullPath, false); } catch { }
                }

                if (target != null)
                {
                    effectivePath = target.FullName;
                    if ((target.Attributes & FileAttributes.Directory) != 0)
                    {
                        isDir = true;
                        attrs |= FileAttributes.Directory;
                    }
                }
            }

            var ctime = fsi.CreationTimeUtc;
            var atime = fsi.LastAccessTimeUtc;
            var mtime = fsi.LastWriteTimeUtc;
            long length = 0;
            if (!isDir && computeLength)
            {
                if (fsi is FileInfo fi)
                {
                    try { length = fi.Length; } catch { length = 0; }
                }
                else
                {
                    try { length = new FileInfo(effectivePath).Length; } catch { length = 0; }
                }
            }

            entry = new Entry(fullPath, effectivePath, isDir, attrs, length, ctime, atime, mtime);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsThumbnailCandidate(Entry e)
    {
        if (e.IsDirectory) return false;
        if (e.Length <= 0 || e.Length > 64L * 1024L * 1024L) return false;
        var ext = Path.GetExtension(e.FullPath);
        return ThumbnailExtensions.Contains(ext);
    }

    private static bool IsBlobCandidate(Entry e)
    {
        if (!IsThumbnailCandidate(e)) return false;
        return e.Length > 0 && e.Length <= MaxBlobFileBytes;
    }

    private void QueueThumbnailPrefetch(Entry[] entries)
    {
        var take = _enableAggressivePrefetch ? 40 : 12;
        var candidates = entries.Where(IsThumbnailCandidate).Take(take).ToArray();
        if (candidates.Length == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var e in candidates)
            {
                if (_prefixCache.ContainsKey(e.EffectivePath))
                {
                    continue;
                }

                await _prefetchLimiter.WaitAsync().ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        EnsureLocalFileCache(e);
                        await PrefetchPrefixAsync(e.EffectivePath, e.Length).ConfigureAwait(false);
                    }
                    finally
                    {
                        _prefetchLimiter.Release();
                    }
                });
            }
        });
    }

    private void QueueDirectorySync(string fullDir, Entry[] entries)
    {
        if (!_enableLocalMirrorCache)
        {
            return;
        }

        if (!_dirSyncInFlight.TryAdd(fullDir, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await SyncDirectoryToLocalCache(entries).ConfigureAwait(false);
                Console.WriteLine($"[perf] dir-sync path={fullDir} elapsedMs={sw.ElapsedMilliseconds} entries={entries.Length}");
            }
            catch
            {
                // best-effort sync only
            }
            finally
            {
                _dirSyncInFlight.TryRemove(fullDir, out _);
            }
        });
    }

    private async Task SyncDirectoryToLocalCache(Entry[] entries)
    {
        if (!_enableLocalMirrorCache)
        {
            return;
        }

        var candidates = entries
            .Where(IsThumbnailCandidate)
            .Take(MaxSyncEntriesPerDir)
            .ToArray();

        if (candidates.Length == 0)
        {
            return;
        }

        var tasks = new List<Task>(candidates.Length);
        foreach (var e in candidates)
        {
            await _prefetchLimiter.WaitAsync().ConfigureAwait(false);
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    EnsureLocalFileCache(e);
                }
                finally
                {
                    _prefetchLimiter.Release();
                }
            }));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task PrefetchPrefixAsync(string path, long fileLength)
    {
        try
        {
            var readLen = (int)Math.Min(PrefixBytesPerFile, Math.Max(0, fileLength));
            if (readLen <= 0) return;

            var data = new byte[readLen];
            using var stream = OpenReadStream(path);
            var offset = 0;
            while (offset < readLen)
            {
                var n = await stream.ReadAsync(data, offset, readLen - offset).ConfigureAwait(false);
                if (n <= 0) break;
                offset += n;
            }
            if (offset <= 0) return;

            AddPrefixCache(path, data, offset);
        }
        catch
        {
            // best-effort prefetch only
        }
    }

    private void EnsureLocalFileCache(Entry e)
    {
        if (!_enableLocalMirrorCache)
        {
            return;
        }

        try
        {
            if (e.IsDirectory) return;
            var relative = GetSafeRelative(e.EffectivePath);
            if (relative == null) return;

            var target = Path.Combine(_localCacheRoot, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var sourceInfo = new FileInfo(e.EffectivePath);
            if (!sourceInfo.Exists) return;

            var targetInfo = new FileInfo(target);
            if (targetInfo.Exists &&
                targetInfo.Length == sourceInfo.Length &&
                targetInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
            {
                return;
            }

            using var src = OpenReadStream(e.EffectivePath);
            using var dst = new FileStream(
                target,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    BufferSize = 512 * 1024,
                    Options = FileOptions.SequentialScan
                }
            );
            src.CopyTo(dst, 512 * 1024);
            dst.Flush(true);
            File.SetLastWriteTimeUtc(target, sourceInfo.LastWriteTimeUtc);
        }
        catch
        {
            // best-effort cache only
        }
    }

    private void AddPrefixCache(string path, byte[] data, int length)
    {
        if (length <= 0) return;
        if (length < data.Length)
        {
            Array.Resize(ref data, length);
        }

        var item = new CachedPrefix(DateTimeOffset.UtcNow, data);
        if (_prefixCache.TryGetValue(path, out var existing))
        {
            Interlocked.Add(ref _prefixCacheBytes, -existing.Data.Length);
        }
        _prefixCache[path] = item;
        var total = Interlocked.Add(ref _prefixCacheBytes, item.Data.Length);
        if (total > MaxPrefixCacheBytes)
        {
            _prefixCache.Clear();
            Interlocked.Exchange(ref _prefixCacheBytes, 0);
        }
    }

    private bool TryReadFromPrefixCache(string path, ulong offset, uint length, IntPtr buffer, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        if (!_prefixCache.TryGetValue(path, out var cached))
        {
            return false;
        }

        if ((DateTimeOffset.UtcNow - cached.CreatedUtc) > PrefixCacheTtl)
        {
            _prefixCache.TryRemove(path, out _);
            Interlocked.Add(ref _prefixCacheBytes, -cached.Data.Length);
            return false;
        }

        if (offset >= (ulong)cached.Data.Length)
        {
            return false;
        }

        var available = cached.Data.Length - (int)offset;
        var copy = (int)Math.Min((uint)available, length);
        if (copy <= 0) return false;
        Marshal.Copy(cached.Data, (int)offset, buffer, copy);
        bytesTransferred = (uint)copy;
        return true;
    }

    private void TryPopulateBlobCache(Entry entry)
    {
        if (_blobCache.ContainsKey(entry.EffectivePath)) return;

        try
        {
            var len = (int)Math.Min(MaxBlobFileBytes, Math.Max(0, entry.Length));
            if (len <= 0) return;

            var data = new byte[len];
            using var stream = OpenReadStream(entry.EffectivePath);
            var offset = 0;
            while (offset < len)
            {
                var n = stream.Read(data, offset, len - offset);
                if (n <= 0) break;
                offset += n;
            }
            if (offset <= 0) return;
            if (offset < data.Length) Array.Resize(ref data, offset);

            AddBlobCache(entry.EffectivePath, data);
        }
        catch
        {
            // best-effort
        }
    }

    private void AddBlobCache(string path, byte[] data)
    {
        if (data.Length <= 0) return;

        var item = new CachedBlob(DateTimeOffset.UtcNow, data);
        if (_blobCache.TryGetValue(path, out var existing))
        {
            Interlocked.Add(ref _blobCacheBytes, -existing.Data.Length);
        }
        _blobCache[path] = item;
        var total = Interlocked.Add(ref _blobCacheBytes, item.Data.Length);
        if (total > MaxBlobCacheBytes)
        {
            _blobCache.Clear();
            Interlocked.Exchange(ref _blobCacheBytes, 0);
        }
    }

    private bool TryReadFromBlobCache(string path, ulong offset, uint length, IntPtr buffer, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        if (!_blobCache.TryGetValue(path, out var cached))
        {
            return false;
        }

        if ((DateTimeOffset.UtcNow - cached.CreatedUtc) > BlobCacheTtl)
        {
            _blobCache.TryRemove(path, out _);
            Interlocked.Add(ref _blobCacheBytes, -cached.Data.Length);
            return false;
        }

        if (offset >= (ulong)cached.Data.Length)
        {
            return false;
        }

        var available = cached.Data.Length - (int)offset;
        var copy = (int)Math.Min((uint)available, length);
        if (copy <= 0) return false;
        Marshal.Copy(cached.Data, (int)offset, buffer, copy);
        bytesTransferred = (uint)copy;
        return true;
    }

    private string GetReadPath(Entry e)
    {
        if (!_enableLocalMirrorCache)
        {
            return e.EffectivePath;
        }

        var cached = TryGetValidLocalCachePath(e);
        return cached ?? e.EffectivePath;
    }

    private string? TryGetValidLocalCachePath(Entry e)
    {
        try
        {
            if (e.IsDirectory) return null;
            var relative = GetSafeRelative(e.EffectivePath);
            if (relative == null) return null;

            var cachedPath = Path.Combine(_localCacheRoot, relative);
            if (!File.Exists(cachedPath)) return null;

            var src = new FileInfo(e.EffectivePath);
            var dst = new FileInfo(cachedPath);
            if (!src.Exists || !dst.Exists) return null;
            if (src.Length != dst.Length) return null;
            if (src.LastWriteTimeUtc != dst.LastWriteTimeUtc) return null;
            return cachedPath;
        }
        catch
        {
            return null;
        }
    }

    private string? GetSafeRelative(string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(_rootPath, fullPath);
            if (string.IsNullOrWhiteSpace(rel)) return null;
            if (rel.StartsWith("..")) return null;
            return rel;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildLocalCacheRoot(string sourceRoot)
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MacMount",
            "ReadCache"
        );
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceRoot.ToUpperInvariant()));
        var hash = Convert.ToHexString(hashBytes).Substring(0, 24);
        return Path.Combine(basePath, hash);
    }

    private sealed record Entry(
        string FullPath,
        string EffectivePath,
        bool IsDirectory,
        FileAttributes Attributes,
        long Length,
        DateTime CreationTimeUtc,
        DateTime LastAccessTimeUtc,
        DateTime LastWriteTimeUtc
    );

    private sealed record CachedDirectory(DateTimeOffset CreatedUtc, long DirectoryStampUtc, Entry[] Entries);
    private sealed record CachedEntry(DateTimeOffset CreatedUtc, Entry Entry);
    private sealed record CachedPrefix(DateTimeOffset CreatedUtc, byte[] Data);
    private sealed record CachedBlob(DateTimeOffset CreatedUtc, byte[] Data);

    private static long GetDirectoryStampUtc(string fullDir)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(fullDir).Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static FileStream OpenReadStream(string path)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 256 * 1024,
                Options = FileOptions.SequentialScan
            }
        );
    }

    private static FileStream OpenReadWriteStream(string path, FileMode mode)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = mode,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 256 * 1024,
                Options = FileOptions.RandomAccess
            }
        );
    }

    private sealed class OpenHandle : IDisposable
    {
        public Entry Entry { get; }
        public FileStream? Stream { get; }
        public object Sync { get; } = new();

        public OpenHandle(Entry entry, FileStream? stream)
        {
            Entry = entry;
            Stream = stream;
        }

        public void Dispose()
        {
            try { Stream?.Dispose(); } catch { }
        }
    }
}

internal sealed class MountedDrive : IDisposable
{
    public string DriveId { get; }
    public string Letter { get; }
    public string Path { get; }
    public string SourceSummary { get; }
    public DateTimeOffset MountedAtUtc { get; }
    private readonly FileSystemHost _host;
    private readonly IDisposable? _resource;
    private readonly BrokerPassthroughFileSystem? _passthroughFileSystem;

    public MountedDrive(string driveId, string letter, string path, string sourceSummary, DateTimeOffset mountedAtUtc, FileSystemHost host, BrokerPassthroughFileSystem? passthroughFileSystem = null, IDisposable? resource = null)
    {
        DriveId = driveId;
        Letter = letter;
        Path = path;
        SourceSummary = sourceSummary;
        MountedAtUtc = mountedAtUtc;
        _host = host;
        _passthroughFileSystem = passthroughFileSystem;
        _resource = resource;
    }

    public bool UpdateVolumeInfo(long totalBytes, long freeBytes)
    {
        if (_passthroughFileSystem is null) return false;
        _passthroughFileSystem.UpdateVolumeInfo(totalBytes, freeBytes);
        return true;
    }

    public void Dispose()
    {
        try { _host.Unmount(); } catch {}
        try { _host.Dispose(); } catch {}
        try { _resource?.Dispose(); } catch {}
    }
}
