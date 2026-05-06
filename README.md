# MacMount — Mac Drive Manager for Windows

MacMount is a Windows desktop application that lets you **mount, browse, and read** APFS and HFS+ formatted Mac drives directly on Windows — exposed as real local drive letters (e.g. `M:\`, `N:\`), not network shares.

Built on Electron + React + .NET, with a high-performance native caching engine and WinFsp for OS-level filesystem integration.

---

## Table of Contents

- [Features](#features)
- [Architecture](#architecture)
- [Prerequisites](#prerequisites)
- [Development Setup](#development-setup)
- [Build & Release](#build--release)
- [Mount Modes](#mount-modes)
- [Project Structure](#project-structure)
- [Commercial Readiness](#commercial-readiness)
- [Known Limitations](#known-limitations)
- [License](#license)

---

## Features

| Feature | Status |
|---|---|
| APFS read support | Stable |
| HFS+ read support | Stable |
| APFS write support | Experimental |
| Local drive-letter mounting (e.g. `M:\`) | Stable |
| WinFsp filesystem integration | Stable |
| High-performance native caching engine | Stable |
| Multi-tier block + directory cache | Stable |
| Read-ahead prefetching | Stable |
| Native bridge fallback path | In progress |
| Native .NET raw disk service | Stable |
| Electron UI + real-time dashboard | Stable |
| Unsigned dev installer | Stable |

### Performance highlights

- **Block-level caching** — 64 KB–512 KB blocks with LRU eviction
- **Directory entry caching** — fast repeated directory listings with automatic invalidation
- **Read-ahead prefetching** — predictive sequential load for large file access
- **Async I/O** — non-blocking operations keep the UI responsive
- **Zero-allocation reads** — `ArrayPool` recycling on small files

---

## Architecture

```
┌──────────────────────────────────┐
│  Electron Shell  (main.js)       │  Admin UAC elevation at launch
│  React UI  (src/)                │  Vite dev server / bundled dist/
│  Express API  (server.js :3001)  │  Routes: drive / mount / native / system
└────────────┬─────────────────────┘
             │ IPC / HTTP (localhost only)
    ┌────────▼──────────┐    ┌─────────────────────┐
    │  MacMount.Native  │    │  MacMount.RawDisk   │
    │  Service (.NET)   │    │  Engine (.NET)      │
    │  Named-pipe IPC   │    │  Direct disk I/O    │
    └────────┬──────────┘    └─────────────────────┘
             │
    ┌────────▼──────────┐
    │  WinFsp / Bridge  │  OS-level filesystem host + native fallback
    └───────────────────┘
```

**Mount modes** (selectable at runtime via `MACMOUNT_MOUNT_MODE`):

| Mode | Description |
|---|---|
| `wsl_kernel` | Default. Mount through WSL2 kernel drivers, then expose the mount as a Windows drive letter |
| `native_first` | Debug fallback. Try the raw-disk provider first, then fall back to the native bridge path when available |
| `native_only` | Raw-disk provider only. Disables the native bridge fallback path |

---

## Prerequisites

| Requirement | Notes |
|---|---|
| **Windows 10/11 64-bit** | Latest cumulative updates recommended |
| **Administrator privileges** | Required for raw disk access (`\\.\PHYSICALDRIVE#`) |
| **[WinFsp](https://github.com/winfsp/winfsp/releases)** | v1.10 or later — provides FUSE kernel driver |
| **Node.js 20+** | For development only |
| **[.NET 8 SDK](https://dotnet.microsoft.com/download)** | For building native services |

---

## Development Setup

```bash
# 1. Install JS dependencies
npm install

# 2. Build native .NET services
npm run native:build   # MacMount.NativeService
npm run raw:build      # MacMount.RawDiskEngine
npm run broker:build   # MacMount.NativeBroker

# 3. Start dev server + Electron
npm run start
```

The Vite dev server starts on `http://localhost:5173` and Electron loads it automatically. The Express backend starts on `http://127.0.0.1:3001` (loopback-only).

### Available scripts

| Script | Purpose |
|---|---|
| `npm run start` | Dev mode: Vite + Electron in parallel |
| `npm run start:prod` | Production build then launch |
| `npm run build` | Vite production bundle |
| `npm run test` | Self-test suite (Electron hardening, config checks) |
| `npm run security:audit` | Dependency vulnerability scan |
| `npm run commercial:gate` | Validate release documentation presence |
| `npm run native:publish` | Publish all .NET binaries to `native/bin/` |
| `npm run release:prep` | `native:publish` + `build` |
| `npm run release:win:full` | Build NSIS installer + portable `.exe` without signing |
| `npm run release:win:unsigned` | Unsigned Windows build for local/dev distribution |
| `npm run release:win:dev` | Alias for `release:win:unsigned` |
| `npm run release:gate` | End-to-end gate: test + audit + signing |
| `npm run release:candidate` | Full production release pipeline |
| `npm run signing:setup:real` | Configure real Authenticode certificate |
| `npm run signing:verify` | Validate signing environment |

---

## Build & Release

### Local / unsigned build

```bash
npm run release:win:unsigned
```

Outputs unsigned installer and portable artifacts to `dist/`.

### Optional signed production build

```bash
# 1. Configure certificate
npm run signing:setup:real -- -PfxPath "C:\secure\MacMount-Prod.pfx" -PfxPassword "YOUR_PASSWORD"

# 2. Build and sign
npm run release:win:signed

# 3. Full release gate (recommended on the release machine)
npm run release:candidate
```

Env vars accepted for CI:

```
CSC_LINK              Path or base64 blob of the PFX
CSC_KEY_PASSWORD      PFX passphrase
WIN_CSC_LINK          (optional) Windows-specific override
WIN_CSC_KEY_PASSWORD  (optional) Windows-specific passphrase
```

Place the offline WinFsp installer at `prereqs/winfsp.msi` before building the full installer so it is bundled for end-user machines without internet access.

---

## Mount Modes

Configure via environment variable before launching:

```powershell
# Default: WSL2 kernel-backed drive-letter mount
$env:MACMOUNT_MOUNT_MODE = "wsl_kernel"

# Debug fallback: raw provider first, then native bridge fallback if present
$env:MACMOUNT_MOUNT_MODE = "native_first"

# Debug fallback: raw provider only
$env:MACMOUNT_MOUNT_MODE = "native_only"
```

---

## Project Structure

```
Mac_Opener/
├── main.js                  Electron main process, UAC elevation, window lifecycle
├── server.js                Express API server (port 3001, loopback-only)
├── preload.js               Electron preload bridge (context-isolated)
├── vite.config.js           Vite bundler config
├── package.json             Dependencies, scripts, electron-builder config
│
├── src/                     React UI (Vite)
│   ├── App.jsx              Root component + dashboard
│   ├── main.jsx             React entry point
│   └── App.css / index.css  Styling
│
├── routes/                  Express route modules
│   ├── driveRoutes.js       Physical disk enumeration
│   ├── mountRoutes.js       Mount / unmount operations
│   ├── nativeRoutes.js      Native service IPC bridge
│   └── systemRoutes.js      Health, logs, setup status
│
├── scripts/                 Build, release, signing, and utility scripts
│
├── native/                  .NET projects
│   ├── MacMount.NativeService/   Named-pipe filesystem service
│   ├── MacMount.RawDiskEngine/   Low-level APFS/HFS+ parser + disk I/O
│   └── MacMount.NativeBroker/    Privileged broker for UAC-separated ops
│
├── docs/                    Commercial readiness documentation
│   ├── COMMERCIAL_READINESS.md
│   ├── GO_NO_GO.md
│   ├── RISK_REGISTER.md
│   └── SUPPORT_RUNBOOK.md
│
├── prereqs/                 Bundled redistributables (e.g. winfsp.msi)
├── public/                  Static assets served by Vite
└── build/                   Electron-builder resources (EULA, icons)
```

---

## Commercial Readiness

Current status: **development-only / unsigned distribution ready, not public GA ready.**

Blocking items before general availability:

1. Configure a real Authenticode code-signing certificate (`CSC_LINK` / `WIN_CSC_LINK`).
2. Produce and verify a `Valid` signed installer artifact (`npm run release:gate`) if you move to public distribution.
3. Final smoke test on a clean Windows machine with a standard user workflow.

See [`docs/COMMERCIAL_READINESS.md`](docs/COMMERCIAL_READINESS.md) and [`docs/GO_NO_GO.md`](docs/GO_NO_GO.md) for the full release gate matrix and approval checklist.

### SLO targets (post-GA)

| Metric | Target |
|---|---|
| App launch to usable UI | ≤ 5 s on reference hardware |
| Mount success rate | ≥ 99% on supported hardware |
| Crash-free sessions | ≥ 99.5% |
| Support diagnostics bundle | ≤ 2 minutes to collect |

---

## Known Limitations

- **APFS write support is experimental.** Always keep backups before writing to an APFS volume.
- **Administrator rights are mandatory.** Raw `\\.\PHYSICALDRIVE#` access is not available to standard users.
- **WinFsp must be installed** before the native mount engine will work. The full installer bundles WinFsp automatically; dev setups require manual installation.
- **The raw APFS provider is still incomplete.** Some APFS volumes may expose only partial metadata or preview trees until parser work is finished.

---

## License

MIT — see [LICENSE](LICENSE) for details.
