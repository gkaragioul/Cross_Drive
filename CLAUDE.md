# CLAUDE.md — MacMount

## Project Overview

MacMount is a Windows desktop app for mounting, browsing, and reading APFS/HFS+ Mac drives on Windows as real local drive letters. Built on Electron + React + .NET with WinFsp filesystem integration.

**Status:** Pre-GA. APFS write is experimental. Code-signing certificate not yet configured.

## Tech Stack

- **Frontend:** React 18 + Vite 5 (dev on port 5173)
- **Desktop shell:** Electron 32 (requires admin/UAC elevation)
- **Backend:** Express on `127.0.0.1:3001` (loopback only, started from `server.js`)
- **Native:** .NET 8 (three projects under `native/`)
  - `MacMount.NativeService` — named-pipe filesystem service
  - `MacMount.RawDiskEngine` — low-level APFS/HFS+ parser + direct disk I/O
  - `MacMount.NativeBroker` — privileged broker for UAC-separated ops
- **Filesystem layer:** WinFsp (FUSE) + WSL 2 UNC fallback

## Architecture

```
Electron (main.js) → Express API (server.js :3001) → .NET services (named-pipe IPC)
React UI (src/)    ← polls Express for drive/mount/status updates     ↓
                                                              WinFsp / WSL mount
```

**Mount modes** (set via `MACMOUNT_MOUNT_MODE` env var):
- `hybrid_canary` — default, splits traffic between native engine and WSL
- `wsl_unc` — all mounts via WSL, max compatibility
- `experimental_raw` — all mounts via native engine, max performance

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
native-bridge/       WinFsp port roadmap (future)
docs/                Commercial readiness docs (GO_NO_GO, RISK_REGISTER, etc.)
.github/workflows/   CI (macmount-ci.yml) + release (macmount-release.yml)
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

- **CI workflow** (`macmount-ci.yml`): Runs on all pushes/PRs. Node 20, .NET 9. Steps: npm ci, self-test, security audit, commercial gate, unsigned build, release audit.
- **Release workflow** (`macmount-release.yml`): Triggered by `v*` tags or manual dispatch. Requires `MACMOUNT_PFX_BASE64` and `MACMOUNT_PFX_PASSWORD` secrets for code signing.

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

## Known Gotchas

- WinFsp must be installed before native mount engine works. Bundled in installer via `prereqs/winfsp.msi`.
- The app always relaunches itself elevated if not running as admin — this is by design.
- `native-bridge/apfs-fuse/` is gitignored (external dependency, not a submodule).
- Vite dev server must be running before Electron connects (`wait-on` handles this in `npm run start`).
