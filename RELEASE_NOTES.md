<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

Reliability hotfix for the in-app updater. v1.5.3/v1.5.4 could leave the upgrade installer crashing partway through file replacement, because the native broker/service/user-session helpers (spawned by Electron via `child_process.spawn`) survived the parent's quit and held file locks on their own .exe files inside the install folder. v1.5.5 makes the installer self-protective.

## Notable changes

- **Installer:** new NSIS `customInit` and `customUnInit` macros run `taskkill /F /IM` against `GKMacOpener.exe`, `MacMount.NativeBroker.exe`, `MacMount.NativeService.exe`, and `MacMount.UserSessionHelper.exe` before any install or uninstall step. Eliminates "sharing violation" crashes during upgrade.
- **In-app updater (PowerShell relaunch helper):** also runs `Stop-Process -Force` on the same process names before launching the installer. Belt-and-braces for installs triggered from already-deployed app builds whose helpers predate this fix.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe
