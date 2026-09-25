# Rebuilding the GPL components

These commands reconstruct a build from the upstream commits recorded in
`provenance.json` and the exact configuration extracted from the shipped
kernel. They are **not** a record of the original build commands for v1.5.34.
The original build tree and any local patches have not been recovered.

Use a Linux build environment with GCC 13.3 and binutils 2.42 (the versions
recorded in `kernel.config`). Extract the two upstream source archives from
the source-materials bundle first. From a directory containing
`WSL2-Linux-Kernel/`, `linux-apfs-rw/`, and `kernel.config`:

```sh
cp kernel.config WSL2-Linux-Kernel/.config
make -C WSL2-Linux-Kernel olddefconfig
make -C WSL2-Linux-Kernel -j"$(nproc)" bzImage modules
make -C linux-apfs-rw KERNEL_DIR="$(pwd)/WSL2-Linux-Kernel"
```

The resulting files are `WSL2-Linux-Kernel/arch/x86/boot/bzImage`,
`WSL2-Linux-Kernel/fs/hfs/hfs.ko`,
`WSL2-Linux-Kernel/fs/hfsplus/hfsplus.ko`, and `linux-apfs-rw/apfs.ko`.
Compare their SHA-256 digests with `provenance.json`. A mismatch means this
reconstruction has not established binary correspondence; do not describe it
as complete corresponding source without resolving the difference.
