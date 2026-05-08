# Native WinFSP Port for CrossDrive: APFS Reader

## Objective
To eliminate the WSL network bottleneck, we are porting the Linux `apfs-fuse` component to a native Windows executable using the WinFSP (Windows File System Proxy) architecture. This allows APFS volumes to be mapped strictly within the Windows kernel as true local block devices, yielding an order-of-magnitude increase in browsing and read speeds.

## Milestones
1. **Source Acquisition**: Clone the `apfs-fuse` reference implementation. *(Completed)*
2. **Environment Setup**: Stand up a `CMake` toolchain under Windows pointing to the native `WinFSP` FUSE compatibility layer (`winfsp/inc/fuse`).
3. **POSIX to Win32 Translation**: Replace Linux-specific I/O routines (`pread`, `pwrite`, `mmap`, `stat`) with their Win32 equivalents (`ReadFile`, `CreateFileMapping`, etc.) or configure a MinGW/MSYS2 compatibility shim.
4. **Device Abstraction**: Modify the block device opener to accept raw Windows Device Paths (e.g., `\\.\PhysicalDriveX`) instead of Linux `/dev/sdx` paths.
5. **Driver Registration**: Build the payload into an `.exe` service that registers the drive letter securely via the WinFSP API natively.
6. **Electron Integration**: Update our `Node.js` backend to spawn the new native bridging `.exe` instead of invoking WSL.

## Current Status
- Created `native-bridge` directory.
- Cloned `apfs-fuse` into `native-bridge/apfs-fuse`.
- Preparing CMake mapping to the WinFSP FUSE layer.

