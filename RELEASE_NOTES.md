<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

APFS Time Machine checkpoint scan fix.

## Notable changes

- **Full checkpoint data scan:** CrossDrive now scans large APFS checkpoint data windows instead of stopping after the first 8,192 blocks.
- **Time Machine visibility:** this targets Time Machine disks where the object map/volume records live later in the checkpoint data area, causing `Volume_410` placeholders with no files.
- **Release guardrail:** self-test now verifies the APFS checkpoint data scan covers large Time Machine checkpoint windows.

## Where to download

Permanent installer link: https://github.com/gkaragioul/Cross_Drive/releases/latest/download/CrossDriveSetup.exe
