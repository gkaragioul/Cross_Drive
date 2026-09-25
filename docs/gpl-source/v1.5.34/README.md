# v1.5.34 GPL source provenance

The `kernel.config` in this directory was extracted from the exact
`wsl_kernel` binary shipped in v1.5.34. `provenance.json` records SHA-256
digests for that binary and the three bundled filesystem modules. The APFS
module identifies itself as `v0.3.20`; all three modules report the same
`6.6.87.2-microsoft-standard-WSL2+` kernel ABI. The upstream Git revisions
in the manifest are the versions named in the project's release documentation.

The original build tree and build script were not present in the retained
CrossDrive project folders. We cannot establish whether local source patches
were applied. The upstream sources and recovered configuration are useful
source materials, but **do not yet establish complete corresponding source**
for the historical binaries. Do not change the status in `provenance.json` to
`verified` without the original build inputs or an equivalent provenance
check. The written source offer for v1.5.34 remains in force.

Future releases should build the GPL-covered binaries from pinned source in
the release workflow, package that exact source/configuration/build script,
and publish the source package alongside the installer.
