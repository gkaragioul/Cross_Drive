# GPL Source Manifest

This manifest records the source references for GPL-covered binary artifacts
bundled with CrossDrive releases.

## v1.5.35 source-built artifacts

The binaries currently in `prereqs/crossdrive-kernel/` were rebuilt for
v1.5.35 from the pinned upstream revisions, final kernel configuration,
[`build-components.sh`](gpl-source/v1.5.35/build-components.sh), and one
documented patch to the kernel's libbpf host tool. Their hashes and common
module ABI are in the [v1.5.35 provenance record](gpl-source/v1.5.35/provenance.json).
The release source archive is named `CrossDrive-GPL-Source-v1.5.35.zip` and
must be offered alongside any installer containing these binaries.

| Artifact | Kernel ABI / version | Source | License |
| --- | --- | --- | --- |
| `prereqs/crossdrive-kernel/wsl_kernel` | `6.6.87.2-microsoft-standard-WSL2` | `microsoft/WSL2-Linux-Kernel`, tag `linux-msft-wsl-6.6.87.2` plus recorded host-tool patch | GPL-2.0 |
| `prereqs/crossdrive-kernel/modules/hfs.ko` | `6.6.87.2-microsoft-standard-WSL2` | HFS driver in the same kernel source tree | GPL-2.0 |
| `prereqs/crossdrive-kernel/modules/hfsplus.ko` | `6.6.87.2-microsoft-standard-WSL2` | HFS+ driver in the same kernel source tree | GPL-2.0 |
| `prereqs/crossdrive-kernel/modules/apfs.ko` | `0.3.20?`, built for the same kernel ABI | `linux-apfs/linux-apfs-rw`, tag `v0.3.20` | GPL-2.0 |

## v1.5.34 provenance status

The exact kernel `.config` was recovered from the shipped `wsl_kernel` and is
stored with binary hashes and pinned upstream commits in
[`gpl-source/v1.5.34/`](gpl-source/v1.5.34/README.md). The original build
tree, build script, and any local patches have not been recovered, so these
materials are **not yet verified as complete corresponding source** for the
historical binaries. The written source offer remains in force. A source
materials archive must not be labeled a complete source bundle until this
provenance gap is resolved.

## Known Build Configuration

The final v1.5.35 configuration has HFS/HFS+ built
as modules:

```text
CONFIG_HFS_FS=m
CONFIG_HFSPLUS_FS=m
```

The APFS module is built against the same WSL2 kernel headers and ABI.

## Source Publication Requirement

Before any public binary release is distributed, publish the complete
corresponding source package for the artifacts above. That package must include
the exact upstream source revisions, the kernel `.config`, any local patches,
and build commands/scripts sufficient to reproduce the shipped binaries.

If no local patches were used, state that explicitly in the source package.
