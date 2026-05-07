<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

GKMacOpener v1.5.3 ships an assisted Windows installer with a click-through EULA, a refreshed app icon, and an in-app one-click auto-update flow backed by a public releases repo.

## Notable changes

- **Installer:** new assisted wizard (Welcome → EULA accept → Install → Finish). Install path is locked so updates always land in the same place. Auto-launches the app on Finish.
- **Auto-update:** the app checks `georgekgr12/GK_Mac_Opener_Releases` on launch and via Settings → Check for updates. SHA256 from the release notes is verified before any installer is launched. Banner offers Update now / Later / Skip this version.
- **Branding:** new logo applied to the app icon, taskbar, installer header, and uninstaller.
- **License hardening:** per-package copyrights for major bundled libraries, FFmpeg LGPL block, bundled GPL-2.0 license text for the WSL kernel + modules, explicit GitHub URLs in the GPL written offer.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe
