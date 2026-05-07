<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

Adds a **Refresh** button on each mounted drive card to clear stale kernel directory state after heavy writes. Workaround for a known Linux `hfsplus.ko` + WSL2 9P issue where deleting a folder via Explorer can leave one file behind because the kernel's catalog B-tree iterator loses an entry while files are unlinked under it.

## Notable changes

- **Drive card → Refresh button:** unmounts the drive, waits 800ms for the kernel/9P side to release, then remounts it. The drive letter and contents come back exactly as they were. One click; ~3-5 seconds.
- **When to use:** if a folder delete on a mounted drive leaves a single file stranded, click Refresh and try the delete again.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe
