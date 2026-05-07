<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

UI tweaks. Adds developer attribution to the About surfaces and a permanent "Check for updates" entry point in the sidebar so the auto-update flow doesn't depend solely on the launch-time check or the Settings page.

## Notable changes

- **About dialog (Help → About GKMacOpener):** new line "Developed by George Karagioules".
- **Settings → About card:** new "Developed by" row showing the same.
- **Sidebar:** new footer pinned to the bottom of the left nav. Shows the current version. Button says "Check for updates" by default; flips to a primary "Update to vX.Y.Z" button when an update is available, opening the update modal directly.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe
