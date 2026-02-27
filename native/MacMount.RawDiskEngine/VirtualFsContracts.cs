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

    RawFsEntry? GetEntry(string path);
    IReadOnlyList<RawFsEntry> ListDirectory(string path);
    int ReadFile(string path, long offset, Span<byte> destination);
}
