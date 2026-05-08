# GPL Source Manifest

This manifest records the source references for GPL-covered binary artifacts
bundled with CrossDrive releases.

## Bundled Artifacts

| Artifact | Version / ABI | Source | License |
| --- | --- | --- | --- |
| `prereqs/crossdrive-kernel/wsl_kernel` | `6.6.87.2-microsoft-standard-WSL2+` | `microsoft/WSL2-Linux-Kernel`, tag `linux-msft-wsl-6.6.87.2` | GPL-2.0 |
| `prereqs/crossdrive-kernel/modules/hfs.ko` | `vermagic=6.6.87.2-microsoft-standard-WSL2+ SMP preempt mod_unload modversions` | Linux kernel HFS driver from the same WSL2 Linux kernel source tree | GPL-2.0 |
| `prereqs/crossdrive-kernel/modules/hfsplus.ko` | `vermagic=6.6.87.2-microsoft-standard-WSL2+ SMP preempt mod_unload modversions` | Linux kernel HFS+ driver from the same WSL2 Linux kernel source tree | GPL-2.0 |
| `prereqs/crossdrive-kernel/modules/apfs.ko` | `linux-apfs-rw 0.3.20`, `vermagic=6.6.87.2-microsoft-standard-WSL2+ SMP preempt mod_unload modversions` | `linux-apfs/linux-apfs-rw`, release/tag `v0.3.20` or the corresponding `0.3.20` source revision | GPL |

## Known Build Configuration

The bundled WSL2 kernel is expected to use the Microsoft WSL2 kernel source
with HFS/HFS+ built as modules:

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
