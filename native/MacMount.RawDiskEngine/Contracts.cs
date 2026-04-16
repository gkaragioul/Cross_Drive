namespace MacMount.RawDiskEngine;

public sealed record MountRequest(string PhysicalDrivePath, string FileSystemHint, bool ReadOnly = true);

public sealed record MountPlan(
    string PhysicalDrivePath,
    string FileSystemType,
    long TotalBytes,
    bool Writable,
    string Notes,
    long PartitionOffsetBytes = 0,
    long PartitionLengthBytes = 0,
    bool IsEncrypted = false,
    bool NeedsPassword = false,
    byte[]? EncryptionKey = null,
    // True when the volume's encryption keys appear to be sealed in
    // hardware (T2 Secure Enclave or Apple Silicon SEP) — the container
    // keybag is either missing entirely or contains no unlock records for
    // this volume UUID. Such drives cannot be unlocked on Windows by any
    // password because the unwrap key never leaves the original Mac.
    bool HardwareBound = false
);

public sealed record FileEntry(
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastWriteUtc
);
