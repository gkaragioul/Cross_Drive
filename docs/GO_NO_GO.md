# GKMacOpener Go/No-Go (Commercial Release)

## Mandatory Go Gates

1. `npm run release:win:full` completes and emits both:
- `GKMacOpener Setup *.exe`
- `GKMacOpener *.exe` (portable)

2. `npm run release:gate` passes with:
- zero failing checks
- no unsigned-production bypass
- WSL2 kernel runtime artifacts present
- native service, broker, and user-session helper published

3. Signing:
- real production code-signing certificate configured
- Authenticode signature status `Valid` for installer artifact
- timestamping enabled in signing chain

4. Security:
- Electron hardening checks pass (`contextIsolation`, `sandbox`, `nodeIntegration=false`, preload-only bridge)
- Express 5 route-registration smoke checks pass
- no `high` or `critical` production dependency vulnerabilities

5. Functional quality:
- WSL kernel path works as the primary mount path for supported APFS/HFS+ disks
- local drive-letter exposure works through WinFsp/user-session mapping
- APFS write support remains experimental and disabled by default
- CoreStorage/FileVault 1 is shown as unsupported
- mount/unmount and stale-drive cleanup smoke tests pass on supported Windows versions

6. Support readiness:
- `docs/SUPPORT_RUNBOOK.md` updated for current release
- diagnostics/log collection path verified on a clean machine

## No-Go Conditions

- Placeholder PFX/certificate in any release path.
- Installer not signed or signature invalid.
- Missing WinFsp MSI, WSL kernel, `apfs.ko`, `hfs.ko`, `hfsplus.ko`, native service, native broker, or user-session helper from release artifacts.
- APFS writes exposed without `MACMOUNT_EXPERIMENTAL_APFS_WRITES=1`.
- CoreStorage/FileVault 1 presented as mountable.
- Regressions in mount stability, drive visibility, or data correctness.
- Unresolved `P0` or `P1` defects in current release candidate.

## Release Approval

- Engineering owner: __________________
- QA owner: __________________
- Release manager: __________________
- Date: __________________
- Decision: `GO` / `NO-GO`
