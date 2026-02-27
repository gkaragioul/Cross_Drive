# Risk Register

| ID | Risk | Severity | Likelihood | Mitigation | Owner | Status |
|---|---|---|---|---|---|---|
| R-001 | Release shipped without valid code signature | Critical | Medium | Enforce `release:audit` in release pipeline; block on placeholder PFX | Release Eng | Open |
| R-002 | Elevation/relaunch path opens wrong app target | High | Medium | Keep argument-preserving admin relaunch tests in smoke suite | Desktop Eng | Mitigated |
| R-003 | Drive mount appears as network path unexpectedly | High | Medium | Keep native mount path as primary; detect and report fallback reason | Backend Eng | Open |
| R-004 | Slow directory enumeration on large media folders | High | High | Optimize native provider caching/read-ahead; benchmark p95 folder open time | Native Eng | Open |
| R-005 | Data correctness mismatch (missing/incorrect entries) | Critical | Low | Regression corpus on APFS/HFS sample disks; diff-based validation | QA | Open |
| R-006 | Dependency vulnerability enters production | High | Medium | Mandatory `security:audit` in CI gate | Security | Mitigated |

## Review Cadence

- Weekly during active development.
- Daily during release-candidate week.
