# Raw Disk Engine (V2)

This module is the native core for true local-drive support.

## Goal
Read APFS/HFS+ directly from raw physical disks on Windows and feed a native filesystem host (WinFsp) without any WSL path dependency.

## Current status
- Project scaffold created.
- Contracts and interfaces created:
  - `MountRequest`, `MountPlan`, `FileEntry`
  - `IRawBlockDevice`, `IFileSystemParser`, `IRawDiskEngine`
- `RawDiskEngine` exists as a non-functional placeholder.

## Build
- `npm run raw:build`

## Next steps
1. Implement `IRawBlockDevice` for `\\.\PHYSICALDRIVE*` (read-only first).
2. Implement APFS superblock/container parser.
3. Implement HFS+ volume/header parser.
4. Add file/directory traversal APIs.
5. Integrate parser output into WinFsp host service.
