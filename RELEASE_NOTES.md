<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

Startup hotfix and assisted updater test release. This version is intended to replace 1.5.23, which could crash on startup if the packaged native service executable was not found and the app tried to fall back to `dotnet`.

## Notable changes

- **Updater feed:** confirms updates are received from `gkaragioul/Cross_Drive`.
- **Assisted install path:** keeps the same `CrossDriveSetup.exe` asset name and release-note checksum format expected by the app.
- **Startup hotfix:** packaged builds no longer fall back to `dotnet run` for native helpers and no longer crash on native-service spawn errors.

## Where to download

Permanent installer link: https://github.com/gkaragioul/Cross_Drive/releases/latest/download/CrossDriveSetup.exe
