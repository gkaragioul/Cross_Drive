<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends SHA-256 hashes for all three assets. -->

## Summary

Source-built WSL filesystem components and matching GPL source archive.

**Unsigned Windows release:** Neither the installer nor the portable executable
has a code-signing signature. Verify the SHA-256 hashes in this release before
installing. Download the GPL source ZIP alongside the Windows executable.

## Notable changes

- **Fresh GPL builds:** The bundled WSL kernel and APFS/HFS/HFS+ modules were rebuilt from pinned source for v1.5.35.
- **Matching source download:** The release includes the exact source revisions, final kernel configuration, build script, and documented host-tool patch as a separate GPL source archive.
- **Release checks:** Binary hashes, module ABI, and source archive contents are checked before publication.

## Where to download

Permanent installer link: https://github.com/gkaragioul/Cross_Drive/releases/latest/download/CrossDriveSetup.exe
