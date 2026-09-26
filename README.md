https://github.com/user-attachments/assets/c7755cff-ae9e-4af9-bac5-8dbea1d96bd5

<div align="center">
<h1>CrossDrive</h1>

<p>
  <strong>Abandoned research project: experimental access to Mac-formatted (APFS / HFS+) drives from Windows.</strong><br>
  <em>Unmaintained. Provided as-is, with no warranty and no support.</em>
</p>

<p>
  <a href="#known-dangerous-behaviour">Known dangerous behaviour</a> -
  <a href="#status">Status</a> -
  <a href="#what-it-does">What it does</a> -
  <a href="#development">Building</a> -
  <a href="#license">License</a>
</p>

</div>

> [!CAUTION]
> **CrossDrive is abandoned research software. It can damage or destroy the data on drives you connect to it.**
>
> - It is not maintained, not supported and not safe for everyday use. Bugs will not be fixed and issues will not be answered (GPL source requests excepted, see [SUPPORT.md](SUPPORT.md)).
> - Only use it on a drive you have fully backed up, or on a disk image. Never use it on your only copy of anything.
> - It is provided "as is" under the [MIT License](LICENSE), without warranty of any kind. You alone are responsible for how you use it and for any loss that results. See [DISCLAIMER.md](DISCLAIMER.md).

## Status

CrossDrive is a research project that has been **abandoned**. Its source is
published so that others can study it, fork it and improve it under the MIT
License. There will be no further releases, fixes or support from the original
author. See [SUPPORT.md](SUPPORT.md).

## Known dangerous behaviour

Read this before running any build of CrossDrive.

- **HFS+ volumes are mounted read-write by default.** The native engine only
  mounts HFS+ read-only if the environment variable
  `CROSSDRIVE_EXPERIMENTAL_HFS_WRITES=0` is set before CrossDrive starts.
- **A read-write HFS+ mount switches the volume's journal off.** CrossDrive
  clears the "journaled" flag and zeroes the journal pointer in both volume
  headers **without replaying the journal first**
  (`HfsPlusNativeReader.DisableJournalAsync`, called from `RawDiskEngine.cs`).
  Changes still waiting in the journal are lost, and the volume can stop
  mounting on a Mac. Most Mac external drives are formatted
  "Mac OS Extended (Journaled)" and are affected. A user reported exactly this
  in [#3](https://github.com/gkaragioul/Cross_Drive/issues/3).
- **The optional WSL2 path repairs and force-mounts drives.**
  `scripts/wsl_mount.sh` runs `fsck.hfsplus -f -y` (automatic repair) and then
  mounts HFS+ with `-o rw,force`.
- **`scripts/wsl_format_and_mount.sh` erases a drive.** It reformats the target
  with `mkfs.hfsplus`. It ships with the app as a recovery tool. Never run it on
  a drive that holds data.
- APFS writes are experimental and off by default
  (`CROSSDRIVE_EXPERIMENTAL_APFS_WRITES=1` turns them on). Do not turn them on.

If you only need to read files from a Mac drive on Windows, use a maintained
tool instead.

## What it does

- Attempts to mount APFS, HFS and HFS+ Mac-formatted volumes on Windows.
- Exposes mounted volumes through local Windows drive letters.
- Uses bundled native Windows helper services as the default mount path.
- Keeps WSL2 kernel filesystem drivers as an optional advanced path.
- Keeps backend communication local through loopback HTTP and named pipes.

CoreStorage / FileVault 1 is detected but not supported.

## Downloads

Past builds are kept for research only. They all include the dangerous
behaviour described above.

The v1.5.35 Windows installer and portable executable are **unsigned**. Obtain
them and `CrossDrive-GPL-Source-v1.5.35.zip` from the same
[GitHub release](https://github.com/gkaragioul/Cross_Drive/releases), and verify
the SHA-256 hashes in its notes. The previously published v1.5.34 binaries have
an unresolved GPL source provenance gap and should not be redistributed.

## License

CrossDrive application source code is Free/Libre/Open Source Software
distributed under the MIT License. See [LICENSE](LICENSE).

Third-party dependencies, bundled prerequisites, and GPL-covered kernel/module
binaries remain under their own license terms. See the third-party and GPL
source notices below for the full binary-distribution license picture.

Copyright (c) 2026 CrossDrive contributors.

## Third-Party Notices

Binary distributions include third-party components under their own terms. See:

- `build/THIRD_PARTY_NOTICES.txt`
- `build/GPL_SOURCE_OFFER.txt`
- `docs/GPL_SOURCE_MANIFEST.md`
- `build/LICENSE.GPL-2.0.txt`

The [v1.5.35 GPL provenance record](docs/gpl-source/v1.5.35/README.md)
documents the new source-built binaries. The
[v1.5.34 record](docs/gpl-source/v1.5.34/README.md) retains the historical
build-provenance gap.

Required WinFsp attribution:

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos

https://github.com/winfsp/winfsp

CrossDrive uses the WinFsp FLOSS exception path by distributing the app under
MIT and shipping the unmodified WinFsp installer. Do not distribute
CrossDrive as proprietary software with WinFsp unless you have a separate
commercial WinFsp license.

## Architecture

```text
Electron main process -> Express API on 127.0.0.1:3001
React UI              -> polls local API for drive state
.NET native helpers   -> default broker, service, and user-session drive mapping
WinFsp                -> bundled Windows filesystem presentation support
WSL2 kernel path      -> optional APFS/HFS/HFS+ mount path
```

Mount modes are controlled by `CROSSDRIVE_MOUNT_MODE`:

- `native_first` - default customer path, using bundled native helpers first.
- `native_only` - native helpers only, with WSL disabled.
- `wsl_kernel` - optional WSL2 kernel path for advanced/testing installs.

## Requirements

- Windows 10/11 64-bit
- Administrator privileges
- WinFsp runtime, bundled as `prereqs/winfsp.msi` for installers
- WSL2 with Ubuntu only if using the optional `wsl_kernel` mount mode
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

Native source folders still use the historical `CrossDrive.*` namespace. Those
names are internal implementation details; shipped app branding, helper
processes, installer metadata, update feed paths, and user-visible state paths
use CrossDrive.

## Release

```bash
npm run release:candidate
```

This signed path requires a real Authenticode certificate. For an explicitly
unsigned release candidate, run `npm run release:candidate:unsigned`. Both
paths build and audit the matching GPL source archive. The release artifacts
are:

- `dist/CrossDriveSetup.exe`
- `dist/CrossDrive-<version>.exe`
- `dist/CrossDrive-GPL-Source-v<version>.zip`

From a clean `main` branch, run the following to publish all three assets and
label the release unsigned:

```powershell
.\scripts\publish-release.ps1 -Version 1.5.35 -AllowUnsigned
```

Omit `-AllowUnsigned` to require signing.

For production Authenticode signing, configure a real certificate with
`CSC_LINK` / `WIN_CSC_LINK` and matching password environment variables.

## Packaging Policy

The installer should ship:

- unmodified `prereqs/winfsp.msi`
- `prereqs/crossdrive-kernel/wsl_kernel`
- `prereqs/crossdrive-kernel/modules/apfs.ko`
- `prereqs/crossdrive-kernel/modules/hfs.ko`
- `prereqs/crossdrive-kernel/modules/hfsplus.ko`
- published native service, broker, and user-session helper binaries
- `LICENSE.txt`
- `THIRD_PARTY_NOTICES.txt`
- `GPL_SOURCE_OFFER.txt`
- `GPL_SOURCE_MANIFEST.md`

The installer should not ship extracted WinFsp SDK/runtime folders such as
`prereqs/winfsp-extract`.

The bundled WSL kernel/modules are GPL-covered components. Keep
`build/GPL_SOURCE_OFFER.txt` and `docs/GPL_SOURCE_MANIFEST.md` up to date for
every binary release. Before distributing a public installer, publish the
complete corresponding source package for those GPL-covered binaries, including
the exact source revisions, kernel `.config`, local patches, and build
commands/scripts.

## Known issues

These will not be fixed by the original author.

- [#3](https://github.com/gkaragioul/Cross_Drive/issues/3): an HFS+ drive became
  unmountable on a Mac after being mounted by CrossDrive (see
  [Known dangerous behaviour](#known-dangerous-behaviour)).
- [#1](https://github.com/gkaragioul/Cross_Drive/issues/1): some drives are
  detected but show no files.
- [#2](https://github.com/gkaragioul/Cross_Drive/issues/2): the WSL2 path looks
  for a distro named exactly `Ubuntu` and fails when it has another name
  (for example `Ubuntu-26.04`).
- APFS writes are experimental and hidden by default.
- Hardware-bound APFS encryption requires the original Mac.
- CoreStorage / FileVault 1 is unsupported.
- CrossDrive was never validated on real physical drives for general release.
