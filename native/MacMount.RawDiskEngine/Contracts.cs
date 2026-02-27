namespace MacMount.RawDiskEngine;

public sealed record MountRequest(string PhysicalDrivePath, string FileSystemHint, bool ReadOnly = true);

public sealed record MountPlan(
    string PhysicalDrivePath,
    string FileSystemType,
    long TotalBytes,
    bool Writable,
    string Notes,
    long PartitionOffsetBytes = 0,
    long PartitionLengthBytes = 0
);

public sealed record FileEntry(
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastWriteUtc
);
