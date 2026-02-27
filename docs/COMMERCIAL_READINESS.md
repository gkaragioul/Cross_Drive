# Commercial Readiness Matrix

## Current Date
- February 24, 2026

## Status Summary
- `NOT READY` for public commercial release until real signing certificate and signed installer gates pass.

## Release Gates

| Area | Gate | Check Command | Pass Criteria |
|---|---|---|---|
| Build | Installer + portable artifacts | `npm run release:win:full` | Both artifacts generated in `dist/` |
| Security | Electron hardening | `npm run test` | All hardening assertions pass |
| Dependencies | Vulnerability threshold | `npm run security:audit` | No vulnerability above configured severity threshold |
| Governance | Commercial documentation | `npm run commercial:gate` | Required docs and release scripts present |
| Signing | Authenticode | `npm run release:audit` | Real cert configured and signature status `Valid` |
| Final | End-to-end release gate | `npm run release:gate` | All above gates pass in one run |

## Blocking Items (Must Resolve Before GA)

1. Configure real code-signing certificate (`CSC_LINK`/`WIN_CSC_LINK`) on release machine.
2. Produce and verify signed installer artifact as `Valid`.
3. Execute final smoke pass on clean Windows machine with standard user workflow.

## Operational SLO Targets

- App launch to usable UI: <= 5s on reference hardware.
- Mount success rate: >= 99% on supported hardware matrix.
- Crash-free sessions: >= 99.5%.
- Support first-response diagnostics bundle available in <= 2 minutes.
