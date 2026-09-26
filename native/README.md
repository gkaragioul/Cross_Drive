# Native V2 Workstream

## Runtime behavior (current app)
- The app runtime mount path is Windows-native: raw-disk analysis and file projection run through the bundled CrossDrive native helpers and WinFsp.
- Customer installs must not require external subsystem runtimes, Node.js, a separate .NET runtime, external bridge binaries, or developer tooling.

## Projects
- `native/CrossDrive.NativeService`: WinFsp host and IPC service scaffolding.
- `native/CrossDrive.RawDiskEngine`: new raw-disk APFS/HFS+ engine scaffold (source of truth for true local drive support).

## Available native actions (pipe/API)
- `analyze_raw`: analyze `\\.\PHYSICALDRIVE*`, detect GPT APFS/HFS+ partition candidates.
- `mount_raw`: development mount path that mounts a WinFsp probe filesystem from raw analysis output.
- `unmount`: unmount a native mounted drive by `driveId`.

## Why this split
- Real local-drive support requires reading raw disks directly in Windows and serving data through a native filesystem host.
- External runtime fallbacks were removed so installer behavior stays predictable on clean customer machines.

## Immediate roadmap
1. Expand APFS/HFS+ parsing from signature-only to metadata tree traversal.
2. Replace probe filesystem with real directory/file projection from parsed metadata.
3. Add safe caching/paging and robust error isolation for raw reads.
4. Add validation suite and crash-safe mount lifecycle.
