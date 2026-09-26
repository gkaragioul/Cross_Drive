# CrossDrive Go/No-Go (Commercial Release)

> **Historical planning note (June 2026).** CrossDrive has since been abandoned and will not be released again. Nothing here is a plan or a promise. See the [README](../README.md) for its status and known dangerous behaviour.

## Mandatory Go Gates

1. `npm run release:win:full` completes and emits both:
- `CrossDriveSetup.exe`
- `CrossDrive-<version>.exe` (portable)

2. `npm run release:gate` passes with:
- zero failing checks
- no unsigned-production bypass
- native service, broker, and user-session helper published
- removed external runtime artifacts absent from packaged resources

3. Signing:
- real production code-signing certificate configured
- Authenticode signature status `Valid` for installer artifact
- timestamping enabled in signing chain

4. Security:
- Electron hardening checks pass (`contextIsolation`, `sandbox`, `nodeIntegration=false`, preload-only bridge)
- Express 5 route-registration smoke checks pass
- no `high` or `critical` production dependency vulnerabilities

5. Functional quality:
- Native runtime path works as the primary mount path for supported APFS/HFS/HFS+ disks
- local drive-letter exposure works through WinFsp/user-session mapping
- classic HFS read-only browsing is validated on real media or explicitly scoped out of the release
- APFS write support remains experimental and disabled by default
- CoreStorage/FileVault 1 is shown as unsupported
- mount/unmount and stale-drive cleanup smoke tests pass on supported Windows versions

6. Support readiness:
- `docs/SUPPORT_RUNBOOK.md` updated for current release
- diagnostics/log collection path verified on a clean machine

## No-Go Conditions

- Placeholder PFX/certificate in any release path.
- Installer not signed or signature invalid.
- Missing WinFsp MSI, native service, native broker, or user-session helper from release artifacts.
- Removed external runtime artifacts appear in packaged resources.
- APFS writes exposed without `CROSSDRIVE_EXPERIMENTAL_APFS_WRITES=1`.
- CoreStorage/FileVault 1 presented as mountable.
- Regressions in mount stability, drive visibility, or data correctness.
- Unresolved `P0` or `P1` defects in current release candidate.

## Release Approval

- Engineering owner: __________________
- QA owner: __________________
- Release manager: __________________
- Date: __________________
- Decision: `GO` / `NO-GO`
