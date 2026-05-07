<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

Hotfix on top of v1.5.3: the Start Menu / Desktop shortcuts now show the GKMacOpener app icon instead of a blank Windows shortcut icon. v1.5.3 introduced the new logo for the installer + taskbar but accidentally shipped the application executable without the icon embedded into its resources.

## Notable changes

- **Icon:** `GKMacOpener.exe` now has the new logo embedded as its icon resource. Desktop and Start Menu shortcuts created by the installer pick it up automatically.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe
