using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MacMount.RawDiskEngine;

/// <summary>
/// A file-backed implementation of IRawBlockDevice for testing.
/// Reads and writes go to a regular file instead of a raw physical drive.
/// Supports sector-aligned I/O (512-byte alignment) to match real device behavior.
/// </summary>
public sealed class FileBackedBlockDevice : IRawBlockDevice
{
    private readonly FileStream _stream;
    private readonly bool _writable;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private bool _disposed;

    private const int SectorSize = 512;

    public string DevicePath { get; }
    public long Length { get; }
    public bool CanWrite => _writable;

    private FileBackedBlockDevice(string filePath, FileStream stream, long length, bool writable)
    {
        DevicePath = filePath;
        _stream = stream;
        Length = length;
        _writable = writable;
    }

    /// <summary>
    /// Create a new file-backed block device from an existing file.
    /// </summary>
    public static FileBackedBlockDevice Open(string filePath, bool writable)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Image file not found: {filePath}");

        var access = writable ? FileAccess.ReadWrite : FileAccess.Read;
        var share = FileShare.None;
        var stream = new FileStream(filePath, FileMode.Open, access, share, 4096, FileOptions.None);
        var length = stream.Length;

        return new FileBackedBlockDevice(filePath, stream, length, writable);
    }

    /// <summary>
    /// Create a new image file of the given size and return a writable device.
    /// </summary>
    public static FileBackedBlockDevice CreateNew(string filePath, long sizeBytes)
    {
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size must be positive.");
        if (sizeBytes % SectorSize != 0)
            throw new ArgumentException($"Size must be a multiple of {SectorSize} bytes.", nameof(sizeBytes));

        // Create the file and set its length
        using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(sizeBytes);
        }

        return Open(filePath, writable: true);
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

        _ioLock.Wait(cancellationToken);
        try
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            var read = _stream.Read(buffer, 0, effectiveCount);
            return new ValueTask<int>(read);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public ValueTask WriteAsync(long offset, byte[] buffer, int count, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_writable)
            throw new InvalidOperationException("Device was opened read-only.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return ValueTask.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        _ioLock.Wait(cancellationToken);
        try
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.Write(buffer, 0, count);
            _stream.Flush();
            return ValueTask.CompletedTask;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        _ioLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FileBackedBlockDevice));
    }
}
