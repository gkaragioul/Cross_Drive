# Commercial Readiness Matrix

> **Historical planning note (June 2026).** CrossDrive has since been abandoned and will not be released again. Nothing here is a plan or a promise. See the [README](../README.md) for its status and known dangerous behaviour.

## Current Date
- June 22, 2026

## Status Summary
- `NOT READY` for public GA until a real signing certificate is configured, signed artifacts pass audit, clean-machine physical-drive smoke testing is complete, and native filesystem gaps are closed or explicitly scoped out.
- Production architecture is native Windows runtime first, with WinFsp/native components used for Windows drive-letter presentation.
- Licensing path is MIT/FLOSS. WinFsp is used through its FLOSS exception; do not ship CrossDrive as proprietary software with WinFsp unless a separate WinFsp commercial license is obtained.
- APFS write support remains experimental and disabled unless `CROSSDRIVE_EXPERIMENTAL_APFS_WRITES=1`.
- CoreStorage/FileVault 1 is detected but unsupported for GA.

## Release Gates

| Area | Gate | Check Command | Pass Criteria |
|---|---|---|---|
| Build | Installer + portable artifacts | `npm run release:win:full` | Both artifacts generated in `dist/` |
| Security | Electron hardening + route smoke | `npm run test` | All hardening, runtime packaging, docs, and route-registration assertions pass |
| Dependencies | Production vulnerability threshold | `npm run security:audit` | No production vulnerability above configured severity threshold |
| Runtime packaging | Native runtime + WinFsp | `npm run release:audit` | WinFsp MSI and native binaries are present; removed external runtime artifacts are absent |
| Governance | Commercial documentation | `npm run commercial:gate` | Required docs and release scripts present |
| Signing | Authenticode | `npm run release:audit` | Real cert configured and signature status `Valid` |
| Final | End-to-end release gate | `npm run release:gate` | All above gates pass in one run |

## Blocking Items Before GA

1. Configure a real code-signing certificate (`CSC_LINK`/`WIN_CSC_LINK`) and password env vars on the release machine.
2. Produce and verify signed NSIS + portable artifacts.
3. Run clean-machine smoke tests for APFS read, APFS zlib-inline compressed file reads, APFS resource-fork sidecars, encrypted APFS password unlock, HFS+ read-write, classic HFS read-only browsing, unsupported CoreStorage messaging, and stale-drive cleanup.
4. Extend or explicitly scope native classic HFS limits beyond linked catalog leaf chains and complete real-media validation.
5. Keep APFS writes hidden/gated unless the environment explicitly opts in with `CROSSDRIVE_EXPERIMENTAL_APFS_WRITES=1`.

## Operational SLO Targets

- App launch to usable UI: <= 5s after installation on a clean supported Windows machine.
- Mount success rate: >= 99% on the supported APFS/HFS/HFS+ hardware matrix.
- Crash-free sessions: >= 99.5%.
- Support diagnostics bundle available in <= 2 minutes.
