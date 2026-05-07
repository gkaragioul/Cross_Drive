# GKMacOpener Current Status

## Current Date
- May 7, 2026

## Executive Summary
- `PRE-GA`
- The app is buildable and uses a WSL2 kernel path as the primary mount architecture for Mac-formatted drives on Windows.
- GA is still blocked on signed release artifacts and clean-machine physical-drive validation.
- APFS writes are implemented only as experimental test coverage and remain disabled unless `MACMOUNT_EXPERIMENTAL_APFS_WRITES=1`.
- CoreStorage/FileVault 1 is detected but unsupported for GA.

## Current Working Areas

### Build And Runtime
- Electron/React/Vite app builds in production mode.
- Express backend binds to loopback only.
- Electron hardening is checked by `npm run test`.
- WSL kernel artifacts are bundled under `prereqs/macmount-kernel`.
- Native service, native broker, and user-session helper are published under `native/bin`.

### Mount Architecture
- Default mount mode is `wsl_kernel`.
- WSL attaches the physical disk, mounts APFS/HFS+ through bundled Linux filesystem modules, and exposes the mounted tree to Windows.
- WinFsp/native broker is used for Windows drive-letter presentation and native fallback/debug paths.
- Startup cleanup removes stale managed drive-letter state after crashes or failed mounts.

### Filesystem Support
- APFS detection, browsing, password-needed state, hardware-bound encryption state, and zlib inline compression reads are implemented.
- Password-based encrypted APFS unlock is wired in the native path but still requires real encrypted APFS validation before GA approval.
- HFS+/HFSX detection and HFS+ read-write test harness coverage are present.
- CoreStorage/FileVault 1 is explicitly unsupported and should remain blocked with a clear message.

## Current Gaps

1. Configure real Authenticode signing and produce signed NSIS + portable artifacts.
2. Run clean-machine smoke tests with no developer tools installed.
3. Validate real media for:
- APFS read
- password-encrypted APFS unlock
- hardware-bound APFS rejection
- HFS+ read-write
- HFSX browsing
- CoreStorage unsupported messaging
- crash/restart stale-drive cleanup
4. Decide whether additional APFS compression support is required after real-media validation. LZVN/LZFSE and resource-fork compression types are not currently supported.

## Definition Of GA Candidate

GKMacOpener is a GA candidate only when:

1. `npm run release:gate` passes without unsigned bypass.
2. `npm run release:audit` verifies real signed artifacts.
3. WSL kernel/module runtime checks pass in the release audit.
4. APFS writes remain experimental and disabled by default.
5. CoreStorage/FileVault 1 is detected and shown as unsupported.
6. Clean Windows smoke testing confirms scan, mount, browse, copy, unmount, and restart cleanup for the supported drive matrix.

## Bottom Line
- The project has moved past prototype status and now has the right WSL kernel-first architecture for GA.
- It is not commercially releasable until signing and real-device validation gates pass.
