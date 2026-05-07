# Commercial Readiness Matrix

## Current Date
- May 7, 2026

## Status Summary
- `NOT READY` for public GA until a real signing certificate is configured, signed artifacts pass audit, and clean-machine physical-drive smoke testing is complete.
- Production architecture is WSL2 kernel primary for APFS/HFS+ mounting, with WinFsp/native components used for Windows drive-letter presentation and fallback/debug paths.
- Licensing path is MIT/FLOSS. WinFsp is used through its FLOSS exception; do not ship GKMacOpener as proprietary software with WinFsp unless a separate WinFsp commercial license is obtained.
- APFS write support remains experimental and disabled unless `MACMOUNT_EXPERIMENTAL_APFS_WRITES=1`.
- CoreStorage/FileVault 1 is detected but unsupported for GA.

## Release Gates

| Area | Gate | Check Command | Pass Criteria |
|---|---|---|---|
| Build | Installer + portable artifacts | `npm run release:win:full` | Both artifacts generated in `dist/` |
| Security | Electron hardening + route smoke | `npm run test` | All hardening, runtime packaging, docs, and route-registration assertions pass |
| Dependencies | Production vulnerability threshold | `npm run security:audit` | No production vulnerability above configured severity threshold |
| Runtime packaging | WSL kernel/modules + native binaries | `npm run release:audit` | WinFsp MSI, WSL kernel, `apfs.ko`, `hfs.ko`, `hfsplus.ko`, and native binaries are present |
| Governance | Commercial documentation | `npm run commercial:gate` | Required docs and release scripts present |
| Signing | Authenticode | `npm run release:audit` | Real cert configured and signature status `Valid` |
| Final | End-to-end release gate | `npm run release:gate` | All above gates pass in one run |

## Blocking Items Before GA

1. Configure a real code-signing certificate (`CSC_LINK`/`WIN_CSC_LINK`) and password env vars on the release machine.
2. Produce and verify signed NSIS + portable artifacts.
3. Run clean-machine smoke tests for APFS read, encrypted APFS password unlock, HFS+ read-write, unsupported CoreStorage messaging, and stale-drive cleanup.
4. Keep APFS writes hidden/gated unless the environment explicitly opts in with `MACMOUNT_EXPERIMENTAL_APFS_WRITES=1`.

## Operational SLO Targets

- App launch to usable UI: <= 5s after first-run WSL setup completes.
- Mount success rate: >= 99% on the supported APFS/HFS+ hardware matrix.
- Crash-free sessions: >= 99.5%.
- Support diagnostics bundle available in <= 2 minutes.
