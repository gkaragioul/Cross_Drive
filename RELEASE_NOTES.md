<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

Update-check feedback fix. Manual update checks now show both an in-app toast and a native Windows notification when CrossDrive is already current.

## Notable changes

- **Manual update check:** shows "You're running the latest version." when no newer release is available.
- **Native notification:** sends the same update-check status through Electron's Windows notification path.
- **In-app toast:** pins the update-check status at the top-right of the window and auto-dismisses it after a short delay.

## Where to download

Permanent installer link: https://github.com/georgekgr12/CrossDrive/releases/latest/download/CrossDriveSetup.exe
