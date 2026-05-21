<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

APFS Time Machine resolution and taskbar icon fix.

## Notable changes

- **APFS checkpoint parsing:** fixed NX checkpoint field offsets so CrossDrive can scan the APFS checkpoint data area instead of showing empty placeholder volumes.
- **Time Machine visibility:** this should let the native APFS path resolve the real backup volume/catalog instead of only `Volumes` and `APFS_CONTAINER_INFO`.
- **Taskbar icon:** unsigned builds now stamp `CrossDrive.exe` with the CrossDrive icon during packaging, so the running app should not show Electron's generic taskbar icon.
- **Release guardrails:** self-test now verifies APFS checkpoint offsets, APFS root presentation, and unsigned exe icon stamping.

## Where to download

Permanent installer link: https://github.com/gkaragioul/Cross_Drive/releases/latest/download/CrossDriveSetup.exe
