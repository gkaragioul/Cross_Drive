<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

Source-built WSL filesystem components and matching GPL source archive.

## Notable changes

- **Fresh GPL builds:** The bundled WSL kernel and APFS/HFS/HFS+ modules were rebuilt from pinned source for v1.5.35.
- **Matching source download:** The release includes the exact source revisions, final kernel configuration, build script, and documented host-tool patch as a separate GPL source archive.
- **Release checks:** Binary hashes, module ABI, and source archive contents are checked before publication.

## Where to download

Permanent installer link: https://github.com/gkaragioul/Cross_Drive/releases/latest/download/CrossDriveSetup.exe
