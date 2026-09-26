# CrossDrive Current Status

> **Historical planning note (June 2026).** CrossDrive has since been abandoned and will not be released again. Nothing here is a plan or a promise. See the [README](../README.md) for its status and known dangerous behaviour.

## Current Date
- June 22, 2026

## Executive Summary
- `PRE-GA`
- CrossDrive now targets a bundled native Windows runtime path for scanning and mounting Mac-formatted drives.
- The app must not require customers to install a separate runtime, distro, or developer tooling after installation.
- GA is still blocked on signed release artifacts, clean-machine physical-drive validation, and native filesystem parity.
- APFS writes are experimental test coverage only and remain disabled unless `CROSSDRIVE_EXPERIMENTAL_APFS_WRITES=1`.
- CoreStorage/FileVault 1 is detected but unsupported for GA.

## Current Working Areas

### Build And Runtime
- Electron/React/Vite app builds in production mode.
- Express backend binds to loopback only.
- Electron hardening is checked by `npm run test`.
- WinFsp MSI is bundled as the installer-managed Windows filesystem prerequisite.
- Native service, native broker, and user-session helper are published under `native/bin`.
- User-session helper exposes only native Windows drive-letter map/unmap actions; it no longer carries WSL or distro command branches.

### Mount Architecture
- Default mount mode is `native_first`.
- Native raw-disk analysis selects supported APFS/HFS/HFS+/HFSX paths and reports unsupported cases explicitly.
- WinFsp/native broker is used for Windows drive-letter presentation.
- Startup cleanup removes stale managed drive-letter state after crashes or failed mounts.

### Filesystem Support
- APFS detection, browsing, password-needed state, hardware-bound encryption state, extent-backed sparse reads with zero-filled internal/tail holes, zlib and uncompressed inline decmpfs reads, and inline plus extent-backed `com.apple.ResourceFork` AppleDouble `._` sidecars are implemented.
- APFS inline decmpfs uses 16-byte decmpfs header parsing, uncompressed logical file sizes, and zero-filled tails for short resident type-1 payloads.
- Password-based encrypted APFS unlock is wired in the native path but still requires real encrypted APFS validation before GA approval.
- HFS+/HFSX detection, HFS+ read-write test harness coverage, and read-only `com.apple.ResourceFork` AppleDouble `._` sidecars are present, including extents-overflow continuation.
- Classic HFS has a native read-only provider for standard catalog B-tree browsing, linked catalog leaf chains, MacRoman filenames, inline and extents-overflow data-fork extents, and resource forks exposed as AppleDouble `._` sidecars, including resource-fork extents-overflow continuation. Broader unusual catalog layouts and real-media validation remain open.
- CoreStorage/FileVault 1 is explicitly unsupported and should remain blocked with a clear message.

## Current Gaps

1. Configure real Authenticode signing and produce signed NSIS + portable artifacts.
2. Run clean-machine smoke tests with no developer tools installed.
3. Validate real media for APFS read, password-encrypted APFS unlock, hardware-bound APFS rejection, HFS+ read-write/resource-fork browsing, HFSX browsing, classic HFS read-only browsing, CoreStorage unsupported messaging, and crash/restart stale-drive cleanup.
4. Extend classic HFS coverage beyond linked catalog leaf chains and complete real-media validation.
5. Decide whether additional APFS compression support is required after real-media validation. LZVN/LZFSE and compressed resource-fork variants are not currently supported.

## Definition Of GA Candidate

CrossDrive is a GA candidate only when:

1. `npm run release:gate` passes without unsigned bypass.
2. `npm run release:audit` verifies real signed artifacts.
3. Native runtime packaging checks pass without external runtime artifacts.
4. APFS writes remain experimental and disabled by default.
5. CoreStorage/FileVault 1 is detected and shown as unsupported.
6. Clean Windows smoke testing confirms scan, mount, browse, copy, unmount, and restart cleanup for the supported drive matrix.

## Bottom Line
- The project is moving to the correct customer model: install once, run with bundled Windows-native components.
- It is not commercially releasable until native support gaps, signing, and real-device validation gates pass.
