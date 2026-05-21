<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

APFS native mount free-space fix.

## Notable changes

- **APFS free-space reporting:** read-only APFS mounts now load APFS spaceman metadata for capacity reporting instead of returning `0 bytes free` to Explorer.
- **Safer fallback:** if APFS free-space metadata cannot be read, CrossDrive reports an optimistic capacity hint instead of making the drive look completely full.
- **Release guardrail:** self-test now verifies APFS read-only mounts do not regress to zero-free-space reporting.

## Where to download

Permanent installer link: https://github.com/gkaragioul/Cross_Drive/releases/latest/download/CrossDriveSetup.exe
