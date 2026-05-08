<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

Update-check feedback improvement. Manual update checks now show a visible notification when GKMacOpener is already on the latest version.

## Notable changes

- **Manual update check:** shows "You're running the latest version." when no newer release is available.
- **Update errors:** shows a visible failure notification instead of silently ignoring the check.
- **Self-test:** adds a guard so the manual update-check notification remains wired.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener/releases/latest/download/GKMacOpenerSetup.exe
