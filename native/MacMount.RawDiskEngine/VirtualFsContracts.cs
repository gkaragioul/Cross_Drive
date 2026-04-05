using System.Collections.Generic;

namespace MacMount.RawDiskEngine;

public sealed record RawFsEntry(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastWriteUtc,
    FileAttributes Attributes = FileAttributes.ReadOnly
);

public interface IRawFileSystemProvider : IDisposable
{
    string FileSystemType { get; }
    long TotalBytes { get; }
    long FreeBytes { get; }
    bool IsWritable => false;

    RawFsEntry? GetEntry(string path);
    IReadOnlyList<RawFsEntry> ListDirectory(string path);
    int ReadFile(string path, long offset, Span<byte> destination);

    // Write operations — default implementations for read-only providers
    int WriteFile(string path, long offset, ReadOnlySpan<byte> source)
        => throw new NotSupportedException("This provider is read-only.");
    void CreateFile(string path)
        => throw new NotSupportedException("This provider is read-only.");
    void CreateDirectory(string path)
        => throw new NotSupportedException("This provider is read-only.");
    void Delete(string path)
        => throw new NotSupportedException("This provider is read-only.");
    void Rename(string oldPath, string newPath)
        => throw new NotSupportedException("This provider is read-only.");
    void SetFileSize(string path, long newSize)
        => throw new NotSupportedException("This provider is read-only.");
    void Flush() { }
}
