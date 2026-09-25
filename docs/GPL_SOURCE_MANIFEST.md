# GPL Source Manifest

This manifest records the source references for GPL-covered binary artifacts
bundled with CrossDrive releases.

## v1.5.34 provenance status

The exact kernel `.config` was recovered from the shipped `wsl_kernel` and is
stored with binary hashes and pinned upstream commits in
[`gpl-source/v1.5.34/`](gpl-source/v1.5.34/README.md). The original build
tree, build script, and any local patches have not been recovered, so these
materials are **not yet verified as complete corresponding source** for the
historical binaries. The written source offer remains in force. A source
materials archive must not be labeled a complete source bundle until this
provenance gap is resolved.

## Bundled Artifacts

| Artifact | Version / ABI | Source | License |
| --- | --- | --- | --- |
| `prereqs/crossdrive-kernel/wsl_kernel` | `6.6.87.2-microsoft-standard-WSL2+` | `microsoft/WSL2-Linux-Kernel`, tag `linux-msft-wsl-6.6.87.2` | GPL-2.0 |
| `prereqs/crossdrive-kernel/modules/hfs.ko` | `vermagic=6.6.87.2-microsoft-standard-WSL2+ SMP preempt mod_unload modversions` | Linux kernel HFS driver from the same WSL2 Linux kernel source tree | GPL-2.0 |
| `prereqs/crossdrive-kernel/modules/hfsplus.ko` | `vermagic=6.6.87.2-microsoft-standard-WSL2+ SMP preempt mod_unload modversions` | Linux kernel HFS+ driver from the same WSL2 Linux kernel source tree | GPL-2.0 |
| `prereqs/crossdrive-kernel/modules/apfs.ko` | `linux-apfs-rw 0.3.20`, `vermagic=6.6.87.2-microsoft-standard-WSL2+ SMP preempt mod_unload modversions` | `linux-apfs/linux-apfs-rw`, release/tag `v0.3.20` or the corresponding `0.3.20` source revision | GPL |

## Known Build Configuration

The recovered configuration from the bundled WSL2 kernel has HFS/HFS+ built
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
