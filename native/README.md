# Native V2 Workstream

## Runtime behavior (current app)
- The app runtime mount path is intentionally **WSL UNC only** for stability.
- Native local-drive mounting is disabled in `server.js` while raw-disk engine work is in progress.

## Projects
- `native/MacMount.NativeService`: WinFsp host and IPC service scaffolding.
- `native/MacMount.RawDiskEngine`: new raw-disk APFS/HFS+ engine scaffold (source of truth for true local drive support).

## Available native actions (pipe/API)
- `analyze_raw`: analyze `\\.\PHYSICALDRIVE*`, detect GPT APFS/HFS+ partition candidates.
- `mount_raw`: development mount path that mounts a WinFsp probe filesystem from raw analysis output.
- `unmount`: unmount a native mounted drive by `driveId`.

## Why this split
- WSL path based sources (`\\wsl.localhost\...`) are not a reliable basis for a true local Windows drive.
- Real local-drive support requires reading raw disks directly in Windows and serving data through a native filesystem host.

## Immediate roadmap
1. Expand APFS/HFS+ parsing from signature-only to metadata tree traversal.
2. Replace probe filesystem with real directory/file projection from parsed metadata.
3. Add safe caching/paging and robust error isolation for raw reads.
4. Add validation suite and crash-safe mount lifecycle.
