# AGENTS.md - GKMacOpener

## Project Overview

GKMacOpener is a Windows desktop app for mounting, browsing, and reading/writing APFS/HFS+ Mac drives on Windows as real local drive letters. Built on Electron + React + WSL2 kernel filesystem drivers, with the legacy .NET/WinFsp path kept as a fallback.

**Status:** Pre-GA. APFS write is experimental. Code-signing certificate not yet configured.

## Tech Stack

- **Frontend:** React 18 + Vite 5 (dev on port 5173)
- **Desktop shell:** Electron 32 (requires admin/UAC elevation)
- **Backend:** Express on `127.0.0.1:3001` (loopback only, started from `server.js`)
- **Filesystem driver (primary):** Custom WSL2 kernel with `hfsplus.ko`, `hfs.ko`, and `apfs.ko`, exposed to Windows through `\\wsl.localhost\Ubuntu\...` and mapped with `subst`.
- **Native fallback:** .NET 9 (projects under `native/`)
  - `MacMount.NativeService` — named-pipe filesystem service
  - `MacMount.RawDiskEngine` — low-level APFS/HFS+ parser + direct disk I/O
  - `MacMount.NativeBroker` — privileged broker for UAC-separated ops
- **Filesystem layer:** WSL2 kernel mount + Windows drive-letter mapping; WinFsp is legacy fallback only

## Architecture

```
Electron (main.js) → Express API (server.js :3001) ─┬─ WSL2 kernel mount (primary)
React UI (src/)    ← polls Express for drive state   └─ .NET/WinFsp fallback
```

**Mount modes** (set via `MACMOUNT_MOUNT_MODE` env var):
- `wsl_kernel` — default, mounts through WSL2 kernel drivers and maps a local drive letter
- `native_first` — debug fallback, tries the legacy native raw provider first
- `native_only` — debug fallback, disables the native bridge fallback path
- `wsl_unc` / `hybrid_canary` — legacy aliases that now resolve to `wsl_kernel`
- `experimental_raw` — legacy alias that now resolves to `native_only`

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
  MacMount.ps1       Main PowerShell orchestration script
  nativeServiceClient.js   IPC client for NativeService
  nativeBrokerClient.js    IPC client for NativeBroker
  self-test.js       Test suite
  security-audit.js  Dependency vulnerability scan
  commercial-gate.js Release documentation gate
  mount_drive.sh     WSL mount helper
native/
  MacMount.NativeService/   .NET named-pipe service
  MacMount.RawDiskEngine/   .NET raw disk parser
  MacMount.NativeBroker/    .NET privileged broker
  MacMount.HfsWriteTest/    .NET HFS+ write test harness (file-backed, no real disk)
native-bridge/       WinFsp port roadmap (future)
docs/                Commercial readiness docs (GO_NO_GO, RISK_REGISTER, etc.)
.github/workflows/   CI (gkmacopener-ci) + release (gkmacopener-release)
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

- **CI workflow** (`gkmacopener-ci`): Runs on all pushes/PRs. Node 20, .NET 9. Steps: npm ci, self-test, security audit, commercial gate, unsigned build, release audit.
- **Release workflow** (`gkmacopener-release`): Triggered by `v*` tags or manual dispatch. Requires signing certificate secrets for code signing.

## Key Conventions

- **Admin required:** App uses `\\.\PHYSICALDRIVE#` raw disk access. Electron main process checks admin and relaunches with UAC if needed.
- **Security:** Electron uses `contextIsolation: true`, `sandbox: true`, `nodeIntegration: false`. CORS is restricted to `localhost:5173` and `127.0.0.1:5173`. Express binds to loopback only.
- **No external network calls:** Backend communicates only with local .NET services via named pipes and local WSL.
- **Env vars:** `MACMOUNT_MOUNT_MODE`, `MACMOUNT_CANARY_PERCENT` control mount behavior.
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

The native engine supports full HFS+ read-write for external Mac drives:
- Block device opened with GENERIC_READ|GENERIC_WRITE via `WindowsRawBlockDevice.OpenReadWrite`
- Allocation bitmap management: `AllocateBlocksAsync`/`FreeBlocksAsync` (contiguous block allocation, bit-level bitmap)
- Catalog B-tree insertion with automatic node splitting and index propagation
- Catalog B-tree deletion with record removal
- File data write with extent allocation (`WriteFileDataAsync`)
- File size set/truncate (`SetFileSizeAsync`) with block free on shrink
- Volume header flush to both primary (partition+1024) and alternate (end of volume) locations
- Journal disable on mount (`DisableJournalAsync`) for safe external drive writes
- WinFsp Create/Write/Delete/Rename/SetFileSize/Flush callbacks via `BrokerRawProviderFileSystem`
- HFS+ drives mount read-write by default; APFS remains read-only
- **Not supported:** Extents overflow file, hard links, resource forks on write, T2/hardware-bound volumes

## Known Gotchas

- WinFsp must be installed before native mount engine works. Bundled in installer via `prereqs/winfsp.msi`.
- The app always relaunches itself elevated if not running as admin — this is by design.
- `native-bridge/apfs-fuse/` is gitignored (external dependency, not a submodule).
- Vite dev server must be running before Electron connects (`wait-on` handles this in `npm run start`).
