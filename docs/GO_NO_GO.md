# MacMount Go/No-Go (Commercial Release)

## Mandatory Go Gates

1. `npm run release:win:full` completes and emits both:
- `MacMount Setup *.exe`
- `MacMount *.exe` (portable)

2. `npm run release:gate` passes with:
- zero failing checks
- no unsigned-production bypass

3. Signing:
- real production code-signing certificate configured
- Authenticode signature status `Valid` for installer artifact
- timestamping enabled in signing chain

4. Security:
- Electron hardening checks pass (`contextIsolation`, `sandbox`, `nodeIntegration=false`, preload-only bridge)
- no `high` or `critical` production dependency vulnerabilities

5. Functional quality:
- mount/unmount smoke tests pass on supported Windows versions
- local drive-letter exposure works for primary APFS/HFS test disks
- file browsing sanity checks pass on large and small directories

6. Support readiness:
- `docs/SUPPORT_RUNBOOK.md` updated for current release
- diagnostics/log collection path verified on clean machine

## No-Go Conditions

- Placeholder PFX/certificate in any release path.
- Installer not signed or signature invalid.
- Regressions in mount stability, drive visibility, or data correctness.
- Unresolved `P0` or `P1` defects in current release candidate.

## Release Approval

- Engineering owner: __________________
- QA owner: __________________
- Release manager: __________________
- Date: __________________
- Decision: `GO` / `NO-GO`
