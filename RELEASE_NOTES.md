<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

Installer startup performance fix. The previous build packaged the native .NET runtime payload more than once, making the installer much larger than necessary and giving Windows/NSIS more data to scan before the setup wizard appeared.

## Notable changes

- **Installer size/startup:** package native binaries only once under `resources/native-bin`.
- **Release audit:** add a gate that fails if `native/bin` is duplicated into `app.asar.unpacked`.
- **Self-test:** add config checks to prevent future native payload duplication.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener/releases/latest/download/GKMacOpenerSetup.exe
