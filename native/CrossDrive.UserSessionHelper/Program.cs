using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

static class Program
{
    private const uint DDD_RAW_TARGET_PATH = 0x00000001;
    private const uint DDD_REMOVE_DEFINITION = 0x00000002;
    private const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;
    private const uint RESOURCETYPE_DISK = 0x00000001;
    private const uint CONNECT_TEMPORARY = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string? targetPath);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, uint max);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? username, uint flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, uint flags, bool force);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) return 2;
            var action = args[0].Trim().ToLowerInvariant();

            var letter = NormalizeLetter(args[1]);
            if (letter is null) return 3;

            return action switch
            {
                "map" when args.Length >= 3 => Map(letter, args[2]),
                "unmap" => Unmap(letter),
                _ => 4
            };
        }
        catch
        {
            return 1;
        }
    }

    private static string? NormalizeLetter(string raw)
    {
        var s = (raw ?? string.Empty).Trim().TrimEnd(':').ToUpperInvariant();
        return s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z' ? $"{s}:" : null;
    }

    private static int Map(string letter, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return 5;
        Unmap(letter);

        if (target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var nativeUncTarget = ToNativeDosTarget(target);
            if (DefineDosDevice(DDD_RAW_TARGET_PATH, letter, nativeUncTarget))
            {
                SetExplorerLabel(letter, "CrossDrive");
                return 0;
            }

            var nr = new NetResource
            {
                Type = RESOURCETYPE_DISK,
                LocalName = letter,
                RemoteName = target.TrimEnd('\\')
            };

            var result = WNetAddConnection2(ref nr, null, null, CONNECT_TEMPORARY);
            if (result == 0)
            {
                SetExplorerLabel(letter, "CrossDrive");
                return 0;
            }

            // Fall through to DefineDosDevice as a compatibility fallback. This
            // keeps the write path usable even if the network provider refuses a
            // drive-letter mapping on a specific Windows build.
        }

        var nativeTarget = ToNativeDosTarget(target);
        if (!DefineDosDevice(DDD_RAW_TARGET_PATH, letter, nativeTarget))
        {
            return Marshal.GetLastWin32Error();
        }

        SetExplorerLabel(letter, "CrossDrive");
        return 0;
    }

    private static int Unmap(string letter)
    {
        WNetCancelConnection2(letter, 0, true);

        var sb = new StringBuilder(32768);
        var len = QueryDosDevice(letter, sb, (uint)sb.Capacity);
        if (len > 0)
        {
            var target = sb.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(target))
            {
                DefineDosDevice(DDD_REMOVE_DEFINITION | DDD_RAW_TARGET_PATH | DDD_EXACT_MATCH_ON_REMOVE, letter, target);
            }
        }

        DefineDosDevice(DDD_REMOVE_DEFINITION, letter, null);
        ClearExplorerLabel(letter);
        return 0;
    }

    private static void SetExplorerLabel(string letter, string label)
    {
        try
        {
            var drive = letter.TrimEnd(':');
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{drive}\DefaultLabel");
            key?.SetValue(string.Empty, label, RegistryValueKind.String);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    private static void ClearExplorerLabel(string letter)
    {
        try
        {
            var drive = letter.TrimEnd(':');
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{drive}", false);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    private static string ToNativeDosTarget(string target)
    {
        var trimmed = target.Trim().TrimEnd('\\');
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\??\UNC\" + trimmed.TrimStart('\\');
        }

        if (trimmed.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (trimmed.StartsWith(@"\Device\", StringComparison.Ordinal) ||
            trimmed.StartsWith(@"\GLOBAL??\", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return @"\??\" + trimmed;
    }
}
