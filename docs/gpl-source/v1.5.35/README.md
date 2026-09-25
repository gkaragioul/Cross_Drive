# v1.5.35 GPL source provenance

The WSL kernel and APFS/HFS/HFS+ modules bundled with this version are built
from the pinned upstream revisions in `provenance.json`, using the final
`kernel.config`, the `build-components.sh` script, and the listed local patch.
The patch fixes a const-qualification warning in an upstream host build tool
on the recorded Ubuntu toolchain; it does not change the kernel filesystem
drivers. Binary SHA-256 digests and module ABI are recorded in the provenance
file and checked before packaging.

The APFS module reports the literal version string `0.3.20?`. The upstream
`genver.sh` adds the `?` when built from its release source archive without a
`.git` directory; the source revision itself is pinned by commit and archive
hash in `provenance.json`.

The release source archive contains both upstream source trees, the final
kernel config, the original build script (named `original-build.sh` inside the
archive), the patch, installation scripts, the provenance record, and rebuild instructions. See
`REBUILD.md` for the exact procedure.

The historical v1.5.34 binaries have a separate, unverified record. This
version's source archive does not retroactively establish source provenance
for those older binaries.
