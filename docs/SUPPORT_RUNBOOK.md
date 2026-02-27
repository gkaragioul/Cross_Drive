# Support Runbook

## Scope
- Commercial support workflow for MacMount installer, mount operations, and browse performance issues.

## First Response Checklist

1. Confirm Windows version and whether app runs elevated.
2. Confirm MacMount version (`Help/About` or installer version).
3. Collect logs:
- app logs from UI logs panel
- backend logs from runtime output
- Windows Event Viewer entries for app/service faults
4. Confirm drive path shown by app:
- expected local drive letter path (e.g., `R:\`)
- fallback UNC path (if present) and reason

## Standard Diagnostic Commands

```powershell
npm run test
npm run security:audit
powershell -ExecutionPolicy Bypass -File scripts/release-audit.ps1 -AllowUnsigned
```

## Incident Categories

- `P0`: Data loss/corruption, app cannot mount any supported drive.
- `P1`: Frequent mount failures or inaccessible mounted drive.
- `P2`: Performance degradation vs baseline SLO.
- `P3`: UX issue or non-blocking warning.

## Escalation

1. Support -> Engineering on any `P0`/`P1` within 15 minutes.
2. Attach diagnostics and exact reproduction steps.
3. Open linked defect with:
- expected behavior
- actual behavior
- logs and environment metadata

## Release Hotfix Rules

- Hotfix requires signed artifact and full `release:gate` pass.
- No emergency release without updated incident note and rollback plan.
