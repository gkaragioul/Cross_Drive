# GKMacOpener - Mac Drive Manager for Windows

GKMacOpener is a Windows desktop app for mounting, browsing, and copying files
from APFS and HFS+ Mac-formatted drives. Supported volumes are exposed as local
Windows drive letters through an Electron + React UI, a loopback Express API,
WSL2 kernel filesystem drivers, and native Windows helper services.

## Status

GKMacOpener is pre-GA. APFS write support is experimental and disabled by
default unless `MACMOUNT_EXPERIMENTAL_APFS_WRITES=1` is set. CoreStorage /
FileVault 1 is detected but explicitly unsupported.

## License

GKMacOpener is Free/Libre/Open Source Software distributed under the MIT
License. See [LICENSE](LICENSE).

Copyright (c) 2026 GKMacOpener contributors.

## Third-Party Notices

Binary distributions include third-party components under their own terms. See:

- `build/THIRD_PARTY_NOTICES.txt`
- `build/GPL_SOURCE_OFFER.txt`

Required WinFsp attribution:

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos

https://github.com/winfsp/winfsp

GKMacOpener uses the WinFsp FLOSS exception path by distributing the app under
MIT and shipping the unmodified WinFsp installer. Do not distribute
GKMacOpener as proprietary software with WinFsp unless you have a separate
commercial WinFsp license.

## Architecture

```text
Electron main process -> Express API on 127.0.0.1:3001
React UI              -> polls local API for drive state
WSL2 kernel path      -> primary APFS/HFS/HFS+ mount path
.NET native helpers   -> broker, service, and user-session drive mapping
WinFsp                -> Windows presentation/fallback support
```

Mount modes are controlled by `MACMOUNT_MOUNT_MODE`:

- `wsl_kernel` - default production path.
- `native_first` - debug fallback, native raw provider first.
- `native_only` - debug fallback, disables WSL/native bridge fallback.

## Requirements

- Windows 10/11 64-bit
- Administrator privileges
- WSL2 with Ubuntu for the primary kernel mount path
- WinFsp runtime, bundled as `prereqs/winfsp.msi` for installers
- Node.js 20+ for development
- .NET 9 SDK for native builds

## Development

```bash
npm install
npm run start
```

The Vite dev server runs on `http://localhost:5173`. The backend binds only to
`127.0.0.1:3001`.

Useful commands:

```bash
npm run test
npm run build
npm run security:audit
npm run commercial:gate
npm run native:publish
npm run hfs:test
npm run apfs:test
```

Native project names still use the historical `MacMount.*` namespace. Those
names are internal implementation details and are not the public product name.

## Release

```bash
npm run release:win:full
npm run release:audit
```

Release artifacts:

- `dist/GKMacOpener Setup <version>.exe`
- `dist/GKMacOpener <version>.exe`

For unsigned staging audits:

```bash
npm run release:audit:unsigned
```

For production Authenticode signing, configure a real certificate with
`CSC_LINK` / `WIN_CSC_LINK` and matching password environment variables.

## Packaging Policy

The installer should ship:

- unmodified `prereqs/winfsp.msi`
- `prereqs/macmount-kernel/wsl_kernel`
- `prereqs/macmount-kernel/modules/apfs.ko`
- `prereqs/macmount-kernel/modules/hfs.ko`
- `prereqs/macmount-kernel/modules/hfsplus.ko`
- published native service, broker, and user-session helper binaries
- `LICENSE.txt`
- `THIRD_PARTY_NOTICES.txt`
- `GPL_SOURCE_OFFER.txt`

The installer should not ship extracted WinFsp SDK/runtime folders such as
`prereqs/winfsp-extract`.

## Known Limitations

- APFS writes are experimental and hidden by default.
- Hardware-bound APFS encryption requires the original Mac.
- CoreStorage / FileVault 1 is unsupported for GA.
- Final GA still requires real physical-drive validation.
