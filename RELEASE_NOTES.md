<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

Rename release: the app is now CrossDrive across the UI, installer, update feed, release artifacts, docs, and packaged helper binaries.

## Notable changes

- **CrossDrive branding:** renames the app, installer, portable build, EULA, docs, README, release tooling, update state paths, and GitHub feed references.
- **Update compatibility:** keeps the installer upgrade identity stable so the new CrossDrive installer can replace the pre-rename install in place.
- **Packaged helpers:** publishes native helper executables as `CrossDrive.NativeService.exe`, `CrossDrive.NativeBroker.exe`, and `CrossDrive.UserSessionHelper.exe`.
- **Manual update check:** keeps the visible "You're running the latest version." notification when no newer release is available.

## Where to download

Permanent installer link: https://github.com/georgekgr12/CrossDrive/releases/latest/download/CrossDriveSetup.exe
