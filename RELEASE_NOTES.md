<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

Fix for "Could not mount" failures right after a Windows reboot or WSL `--shutdown`. The first mount attempt timed out at 10 seconds because cold-starting the WSL VM with the custom kernel took longer than the keep-alive call's timeout.

## Notable changes

- **Mount keep-alive (`scripts/wslMountClient.js`):** raise the elevated-keep-alive timeout from 10s → 60s to cover cold WSL VM starts. Add explicit `disown` to the bash command so the keep-alive job is fully detached on every bash version.
- **Workaround if you hit it on an older build:** open a terminal, run `wsl.exe -d Ubuntu -- echo warm`, then click Mount again.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener/releases/latest/download/GKMacOpenerSetup.exe
