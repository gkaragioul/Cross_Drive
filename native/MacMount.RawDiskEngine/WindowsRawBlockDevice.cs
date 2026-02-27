using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MacMount.RawDiskEngine;

public sealed class WindowsRawBlockDevice : IRawBlockDevice, IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private WindowsRawBlockDevice(string devicePath, SafeFileHandle handle, long length)
    {
        DevicePath = devicePath;
        _handle = handle;
        Length = length;
    }

    public string DevicePath { get; }
    public long Length { get; }

    public static WindowsRawBlockDevice OpenReadOnly(string physicalDrivePath)
    {
        var normalizedPath = PhysicalDrivePath.Normalize(physicalDrivePath);
        var handle = NativeMethods.CreateFile(
            normalizedPath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero
        );

        if (handle.IsInvalid)
        {
            var win32 = Marshal.GetLastWin32Error();
            throw new IOException($"CreateFile failed for {normalizedPath} (win32={win32}).");
        }

        long length;
        try
        {
            length = NativeMethods.GetDiskLength(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new WindowsRawBlockDevice(normalizedPath, handle, length);
    }

    public ValueTask<int> ReadAsync(long offset, byte[] buffer, int count, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (offset >= Length || count == 0) return new ValueTask<int>(0);

        var remaining = Length - offset;
        var effectiveCount = (int)Math.Min(count, remaining);
        cancellationToken.ThrowIfCancellationRequested();

        var read = RandomAccess.Read(
            _handle,
            buffer.AsSpan(0, effectiveCount),
            fileOffset: offset
        );
        return new ValueTask<int>(read);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsRawBlockDevice));
        }
    }
}

internal static class PhysicalDrivePath
{
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("PhysicalDrivePath is required.", nameof(input));
        }

        var trimmed = input.Trim();

        if (int.TryParse(trimmed, out var diskId) && diskId >= 0)
        {
            return $"\\\\.\\PHYSICALDRIVE{diskId}";
        }

        if (!trimmed.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "\\\\.\\" + trimmed.TrimStart('\\');
        }

        return trimmed.ToUpperInvariant();
    }
}

internal static class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out GET_LENGTH_INFORMATION lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped
    );

    public static long GetDiskLength(SafeFileHandle handle)
    {
        if (!DeviceIoControl(
                handle,
                IOCTL_DISK_GET_LENGTH_INFO,
                IntPtr.Zero,
                0,
                out var info,
                Marshal.SizeOf<GET_LENGTH_INFORMATION>(),
                out _,
                IntPtr.Zero))
        {
            var win32 = Marshal.GetLastWin32Error();
            if (!GetFileSizeEx(handle, out var fileSize))
            {
                var sizeErr = Marshal.GetLastWin32Error();
                throw new IOException($"Disk length query failed (ioctl win32={win32}, filesize win32={sizeErr}).");
            }
            return fileSize;
        }

        return info.Length;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct GET_LENGTH_INFORMATION
    {
        public long Length;
    }
}

public sealed class WindowsRawBlockDeviceFactory : IRawBlockDeviceFactory
{
    public Task<IRawBlockDevice> OpenReadOnlyAsync(string physicalDrivePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IRawBlockDevice device = WindowsRawBlockDevice.OpenReadOnly(physicalDrivePath);
        return Task.FromResult(device);
    }
}
