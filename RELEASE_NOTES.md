<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

Updater smoke-test release for the GitHub repo migration. This version exists so an installed `v1.5.17` build can confirm that in-app update checks now read from the main `GK_Mac_Opener` repository.

## Notable changes

- **Update feed:** confirms the app checks `https://api.github.com/repos/georgekgr12/GK_Mac_Opener/releases/latest`.
- **Release packaging:** publishes the normal installer and portable artifacts to the main repo Releases tab.
- **No functional app changes:** this is intentionally a version bump and release-feed validation build.

## Where to download

Permanent installer link: https://github.com/georgekgr12/GK_Mac_Opener/releases/latest/download/GKMacOpenerSetup.exe
