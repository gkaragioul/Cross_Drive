# CLAUDE.md - CrossDrive

## Project Overview

CrossDrive is a Windows desktop app for mounting, browsing, and reading **and writing** APFS/HFS+ Mac drives on Windows as real local drive letters. Built on Electron + React + WSL2 (Linux kernel hfsplus driver + linux-apfs-rw module). The legacy native .NET writer is kept as a fallback only.

**Status:** v1.4.0 ships WSL2-backed R/W for HFS+. APFS R/W via apfs.ko v0.3.20 module is built but not yet wired through the UI. Code-signing certificate not yet configured.

## Tech Stack

- **Frontend:** React 18 + Vite 5 (dev on port 5173)
- **Desktop shell:** Electron 32 (requires admin/UAC elevation)
- **Backend:** Express on `127.0.0.1:3001` (loopback only, started from `server.js`)
- **Filesystem driver (primary):** Linux kernel `hfsplus.ko` + `apfs.ko` running inside a custom WSL2 kernel; mount exposed to Windows via `\\wsl.localhost\Ubuntu\mnt\macdrive_<id>_<rand>` UNC, then `subst <L>:` in the user's interactive session for a real drive letter
- **Filesystem driver (fallback):** WinFsp + .NET native engine — kept for systems without WSL2 but has known B-tree split bugs on writes
- **Native:** .NET 9 (four projects under `native/`)
  - `MacMount.NativeService` — named-pipe filesystem service
  - `MacMount.RawDiskEngine` — low-level APFS/HFS+ parser + direct disk I/O (still used for read & drive enumeration)
  - `MacMount.NativeBroker` — privileged broker for UAC-separated ops
  - `MacMount.HfsFormatTool` — one-off HFS+ format dev tool

## Architecture (v1.4.0+)

```
Electron (main.js) → Express API (server.js :3001) ─┬─ WSL2 (primary):
React UI (src/)                                      │   wsl --mount → fsck.hfsplus → mount -t hfsplus → subst <L>:
                                                     └─ Native (fallback): WinFsp + .NET broker
```

**Mount path selection** (no env var needed; falls through automatically):
1. WSL2 path: attach drive via `wsl --mount \\.\PHYSICALDRIVEN --bare`, run [`scripts/wsl_mount.sh`](scripts/wsl_mount.sh) which fscks + mounts via the kernel, then `subst <L>:` from a Scheduled Task with `LogonType Interactive` so Explorer (non-elevated) sees the drive letter.
2. Native fallback: only used if WSL is unavailable; for HFS+ writes it is unreliable — DO NOT recommend it.

Set `forceNative: true` in `/api/mount` body to skip WSL2 (debugging only). `CROSSDRIVE_MOUNT_MODE=wsl_kernel` is the default; legacy aliases `wsl_unc` and `hybrid_canary` resolve to the same WSL2 path. Use `native_first` or `native_only` only for debugging the legacy native engine.

## WSL2 Setup (required for primary mount path)

The installer is responsible for the steps below. Manual setup for development:

1. `wsl --install` (Microsoft Store) → Ubuntu distro
2. Inside Ubuntu: `apt install hfsfuse hfsplus hfsprogs apfs-fuse`
3. Build a custom WSL2 kernel from [microsoft/WSL2-Linux-Kernel](https://github.com/microsoft/WSL2-Linux-Kernel) tag `linux-msft-wsl-6.6.87.2` with `CONFIG_HFSPLUS_FS=m` and `CONFIG_HFS_FS=m`. Drop `bzImage` into `%LOCALAPPDATA%\CrossDrive-Kernel\wsl_kernel`.
4. Build `apfs.ko` from [linux-apfs/linux-apfs-rw](https://github.com/linux-apfs/linux-apfs-rw) (tag `0.3.20+`) against the custom kernel headers.
5. Drop `hfsplus.ko`, `hfs.ko`, `apfs.ko` into `%LOCALAPPDATA%\CrossDrive-Kernel\modules\`. Installer copies them to `/lib/modules/<KVER>/extra/` and runs `depmod -a`.
6. Write `%USERPROFILE%\.wslconfig`:
   ```
   [wsl2]
   kernel=C:\\Users\\<user>\\AppData\\Local\\CrossDrive-Kernel\\wsl_kernel
   vmIdleTimeout=2147483647
   ```
7. `wsl --shutdown` to apply.

## Project Structure

```
main.js              Electron main process, UAC elevation
server.js            Express API server (port 3001)
preload.js           Electron preload bridge (context-isolated, sandboxed)
vite.config.js       Vite config (base: './', port 5173)
src/
  App.jsx            Root React component + dashboard UI
  main.jsx           React entry point
  index.css          Main stylesheet
  App.css            Component styles
routes/
  driveRoutes.js     Physical disk enumeration
  mountRoutes.js     Mount/unmount operations
  nativeRoutes.js    Native .NET service IPC bridge
  systemRoutes.js    Health, logs, setup status
scripts/
  CrossDrive.ps1            Main PowerShell orchestration script (legacy native path)
  nativeServiceClient.js    IPC client for NativeService
  nativeBrokerClient.js     IPC client for NativeBroker
  self-test.js              Test suite
  security-audit.js         Dependency vulnerability scan
  commercial-gate.js        Release documentation gate
  wslMountClient.js         Node.js client for WSL2 mount path (PRIMARY)
  wsl_mount.sh              Bash mount helper run inside WSL2 (fsck + mount + emit JSON)
  wsl_unmount.sh            Bash unmount helper
  wsl_format_and_mount.sh   Recovery tool: mkfs.hfsplus + mount via WSL2
  mount_drive.sh            Legacy WSL FUSE mount helper (apfs-fuse / hfsfuse)
native/
  MacMount.NativeService/   .NET named-pipe service
  MacMount.RawDiskEngine/   .NET raw disk parser (read paths still active; write paths bypassed by WSL2)
  MacMount.NativeBroker/    .NET privileged broker
  MacMount.HfsWriteTest/    .NET HFS+ write test harness (file-backed, no real disk)
  MacMount.HfsFormatTool/   One-off HFS+ format tool (dev diagnostic; not shipped to users)
native-bridge/       WinFsp port roadmap (future)
docs/                Commercial readiness docs (GO_NO_GO, RISK_REGISTER, etc.)
.github/workflows/   CI (crossdrive-ci) + release (crossdrive-release)
```

## Development Commands

```bash
npm install                  # Install JS dependencies
npm run start                # Dev mode: Vite + Electron in parallel
npm run start:prod           # Production build then launch
npm run build                # Vite production bundle only
npm run test                 # Self-test suite (Electron hardening, config)
npm run security:audit       # Dependency vulnerability scan
```

### .NET builds

```bash
npm run native:build         # Build MacMount.NativeService
npm run raw:build            # Build MacMount.RawDiskEngine
npm run broker:build         # Build MacMount.NativeBroker
npm run native:publish       # Publish all .NET binaries to native/bin/
npm run hfs:test             # HFS+ write test harness (10 tests, file-backed image)
```

### Release

```bash
npm run release:prep         # native:publish + vite build
npm run release:win:unsigned # Unsigned installer + portable (CI/staging)
npm run release:win:full     # NSIS installer + portable
npm run release:gate         # Full gate: test + audit + signing check
npm run release:candidate    # Full production release pipeline
```

## CI/CD

- **CI workflow** (`crossdrive-ci`): Runs on all pushes/PRs. Node 20, .NET 9. Steps: npm ci, self-test, security audit, commercial gate, unsigned build, release audit.
- **Release workflow** (`crossdrive-release`): Triggered by `v*` tags or manual dispatch. Requires signing certificate secrets for code signing.

## Key Conventions

- **Admin required:** App uses `\\.\PHYSICALDRIVE#` raw disk access. Electron main process checks admin and relaunches with UAC if needed.
- **Security:** Electron uses `contextIsolation: true`, `sandbox: true`, `nodeIntegration: false`. CORS is restricted to `localhost:5173` and `127.0.0.1:5173`. Express binds to loopback only.
- **No external network calls:** Backend communicates only with local .NET services via named pipes and local WSL.
- **Env vars:** `CROSSDRIVE_MOUNT_MODE` defaults to `wsl_kernel`; `CROSSDRIVE_CANARY_PERCENT` is legacy telemetry/config only. Legacy `MACMOUNT_*` aliases are accepted for compatibility.
- **Secrets never committed:** `.gitignore` excludes `.env*`, `*.pfx`, `*.p12`, `signing-env.ps1`.

## Testing

Run `npm run test` which executes `scripts/self-test.js`. This validates Electron hardening settings and config integrity. There is no unit test framework (Jest/Vitest) — tests are custom self-test scripts.

## APFS Encryption Support

The native engine supports password-based APFS encrypted volume unlock:
- Volume UUID read from APFS volume superblock (offset 0xF0)
- Container keybag read from NX superblock `nx_keylocker` (offset 0x4F8)
- PBKDF2 key derivation + RFC 3394 AES key unwrap → VEK
- AES-XTS decryption via `DecryptingRawBlockDevice`
- **Not supported:** T2/Apple Silicon hardware-bound encryption, CoreStorage/FileVault 1

## APFS Compression Support

- Inline zlib-compressed files (decmpfs type 3) are decompressed on read
- Inline uncompressed (type 1) served directly
- **Not yet supported:** LZVN (type 7), LZFSE (type 11), resource-fork types (4, 8, 12)

## HFS+ Read-Write Support

**Primary path (v1.4.0+):** Linux kernel `hfsplus.ko` inside WSL2.
- Mount script [`scripts/wsl_mount.sh`](scripts/wsl_mount.sh) runs `fsck.hfsplus -f -y` to clear the dirty flag, then `mount -t hfsplus -o rw,umask=000,uid=1000,gid=1000,force`. Without the fsck step the kernel auto-mounts read-only.
- Mount target: `/mnt/macdrive_<id>_<rand>` — random suffix avoids Windows' 9P client cache pinning the first-seen state of a path.
- Exposed to Windows at `\\wsl.localhost\Ubuntu\mnt\macdrive_<id>_<rand>`, then `subst <L>:` from the user's interactive session for a real drive letter.
- Throughput: ~300 MB/s for sequential writes via Explorer.

**Fallback path (legacy):** The native .NET engine in `MacMount.RawDiskEngine` still has full HFS+ R/W code (catalog B-tree insert/delete/split, allocator, extents, journal disable on mount) but **the catalog B-tree split has unresolved bugs** that surface on real-world bulk copies. v1.4.0 routes around it via WSL2; the native engine is only invoked if WSL2 is unavailable AND `forceNative: true` is set.

**Not supported (either path):** Extents overflow file write, hard links on write, resource forks on write, T2/hardware-bound volumes, FileVault.

## APFS Read-Write Support

- `apfs.ko` v0.3.20 from [linux-apfs/linux-apfs-rw](https://github.com/linux-apfs/linux-apfs-rw) builds against the WSL2 kernel; module loads in WSL2.
- [`scripts/wsl_mount.sh`](scripts/wsl_mount.sh) detects APFS via NXSB magic at offset 32 of the partition and `mount -t apfs -o rw,uid=1000,gid=1000[,pass=...]`.
- **Status:** Module compiles and loads. Mount path is wired but not yet exercised end-to-end against a real APFS drive. T2/Apple Silicon hardware-bound encryption is unsupported (keys live in the Mac's Secure Enclave).

## Known Gotchas

- **WSL2 required for R/W.** Without WSL2 + the bundled custom kernel, CrossDrive falls back to the legacy native engine. Installer must `wsl --install` and drop `wsl_kernel` + module bundles.
- **Custom WSL2 kernel.** `vmIdleTimeout=2147483647` is set in `.wslconfig` to prevent VM idle-shutdown which otherwise tears down the kernel mount. The Node backend also holds an in-VM keep-alive process for belt-and-braces.
- **fsck.hfsplus is mandatory.** The Linux kernel refuses R/W on HFS+ volumes with the dirty flag set; the mount script always runs fsck first.
- **Drive letters need user-session subst.** `subst` writes per-token DOS device namespaces — running it from the elevated Electron process won't show up in non-elevated Explorer. We schedule a Task with `LogonType Interactive` to run subst as the user.
- **Admin required.** App uses `\\.\PHYSICALDRIVE#` raw disk access (also required for `wsl --mount`). Electron main process checks admin and relaunches with UAC.
- **WinFsp** still bundled in installer for the native fallback; not strictly required when WSL2 path is healthy.
- The app always relaunches itself elevated if not running as admin — this is by design.
- `native-bridge/apfs-fuse/` is gitignored (external dependency, not a submodule).
- Vite dev server must be running before Electron connects (`wait-on` handles this in `npm run start`).
