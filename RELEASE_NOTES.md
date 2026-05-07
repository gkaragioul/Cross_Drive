<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

Fixes the in-app update flow ending with the app silently closing and no installer wizard appearing. The PowerShell relaunch helper was using `Start-Process -Wait`, which returned prematurely when NSIS spawned its elevated child, then triggered the post-install fallback against the just-killed old exe path. v1.5.8 launches the installer detached with an explicit normal window style and lets NSIS' `runAfterFinish` handle relaunch.

## Notable changes

- **Updater (PowerShell relaunch helper):** `Start-Process -FilePath $installer -WindowStyle Normal` (no `-Wait`, no post-launch fallback). The wizard UI now displays reliably; the new app launches via NSIS Finish-page checkbox after the install completes.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe
