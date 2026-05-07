<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

Fixes the in-app updater going completely silent on `app.quit()`. v1.5.8's helper was correct, but the way it was spawned tied it to Electron's Windows Job Object: when Electron exited, Windows terminated the helper before it could launch the installer. v1.5.10 launches the helper through `cmd /c start /B`, which breaks the helper out of Electron's job so it survives the parent's quit.

## Notable changes

- **Updater spawn:** `child_process.spawn('cmd.exe', ['/c', 'start', '""', '/B', 'powershell.exe', ...])`. `start` internally sets `CREATE_BREAKAWAY_FROM_JOB`, decoupling the helper from Electron's lifetime — without which `detached: true` alone is not enough on Windows.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe
