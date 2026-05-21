<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

Desktop icon and assisted updater test release.

## Notable changes

- **Updater feed:** confirms updates are received from `gkaragioul/Cross_Drive`.
- **Assisted install path:** keeps the same `CrossDriveSetup.exe` asset name and release-note checksum format expected by the app.
- **Desktop shortcut icon:** installer-created shortcuts now point directly at the packaged CrossDrive icon so Windows does not show the generic Electron fallback icon.

## Where to download

Permanent installer link: https://github.com/gkaragioul/Cross_Drive/releases/latest/download/CrossDriveSetup.exe
