<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

Smoke-test release. v1.5.11 is identical to v1.5.10 in behaviour and exists to verify v1.5.10's updater can take an installed app to a newer version end-to-end (banner appears → download → SHA256 verify → installer wizard appears → Finish → app relaunches with the new version). v1.5.10's `cmd /c start /B` shim was experimentally proven to keep the relaunch helper alive past Electron's exit on Windows.

## Notable changes

- Version bump only.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe
