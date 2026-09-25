# Rebuild the v1.5.35 GPL components

Use a Linux x86-64 environment with GCC 13, Make, flex, bison, bc, libelf
headers, OpenSSL headers, `pahole`, `zstd`, `patch`, and `modinfo`. The recorded
build used Ubuntu 26.04, GCC 13.4.0, and the binutils version in
`provenance.json`. The checked-in script selects `gcc-13` by default.

Extract `WSL2-Linux-Kernel.tar.gz` and `linux-apfs-rw.tar.gz` from the release
source archive. Confirm their SHA-256 hashes against `provenance.json`. Keep
`original-build.sh`, `kernel.config`, and `patches/` together, then run:

```sh
CROSSDRIVE_KERNEL_BUILD_DIR="$PWD/kernel-out" CROSSDRIVE_JOBS=6 \
  bash ./original-build.sh "$PWD/WSL2-Linux-Kernel" \
  "$PWD/linux-apfs-rw" "$PWD/built-components"
```

The script applies the listed patch, normalizes the kernel configuration,
builds the WSL kernel and modules, checks the three module version strings,
and writes `wsl_kernel`, `apfs.ko`, `hfs.ko`, `hfsplus.ko`, `kernel.config`,
and `BUILD_INFO.txt` to a fresh output directory. Compare the five file
hashes with `provenance.json`. A byte-identical hash is useful to confirm the
recorded build, but differences caused by toolchain metadata do not alone
show that different source was used.
