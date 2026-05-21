<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

APFS native mount root presentation fix.

## Notable changes

- **Normal Explorer root:** APFS mounts now expose the primary user volume at the drive root instead of showing a container wrapper with `Volumes` and `APFS_CONTAINER_INFO`.
- **Primary volume selection:** CrossDrive prefers APFS Data/User volumes and avoids Recovery/Preboot/VM volumes for the root view.
- **Release guardrail:** self-test now verifies APFS mounts expose the primary user volume at the drive root.

## Where to download

Permanent installer link: https://github.com/gkaragioul/Cross_Drive/releases/latest/download/CrossDriveSetup.exe
