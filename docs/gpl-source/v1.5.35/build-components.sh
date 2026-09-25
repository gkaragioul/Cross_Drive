#!/usr/bin/env bash
# Build the GPL components shipped with CrossDrive from the pinned source trees.
# Usage: CROSSDRIVE_KERNEL_BUILD_DIR=/linux/path/to/build ./build-components.sh \
#        /linux/path/to/WSL2-Linux-Kernel /linux/path/to/linux-apfs-rw /linux/path/to/output
set -euo pipefail

if (( $# != 3 )); then
  echo "Usage: CROSSDRIVE_KERNEL_BUILD_DIR=<path> $0 <kernel-source> <apfs-source> <output-dir>" >&2
  exit 2
fi

script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
kernel_source=$(realpath "$1")
apfs_source=$(realpath "$2")
output_dir=$(realpath -m "$3")
build_dir=$(realpath -m "${CROSSDRIVE_KERNEL_BUILD_DIR:?Set CROSSDRIVE_KERNEL_BUILD_DIR to a Linux filesystem path}")
jobs=${CROSSDRIVE_JOBS:-4}
compiler=${CROSSDRIVE_CC:-gcc-13}

[[ -f "$kernel_source/Makefile" && -f "$apfs_source/Makefile" ]] || {
  echo "Both source trees are required" >&2
  exit 2
}
[[ ! -e "$output_dir" ]] || {
  echo "Output directory already exists: $output_dir" >&2
  exit 2
}
command -v "$compiler" >/dev/null
command -v modinfo >/dev/null
command -v patch >/dev/null
mkdir -p "$build_dir" "$(dirname -- "$output_dir")"

for source_patch in "$script_dir"/patches/*.patch; do
  if patch --dry-run --silent -d "$kernel_source" -p1 < "$source_patch"; then
    patch --silent -d "$kernel_source" -p1 < "$source_patch"
  elif ! patch --dry-run --silent -R -d "$kernel_source" -p1 < "$source_patch"; then
    echo "Patch does not apply cleanly: $source_patch" >&2
    exit 1
  fi
done

cp "$script_dir/kernel.config" "$build_dir/.config"
make -C "$kernel_source" O="$build_dir" CC="$compiler" HOSTCC="$compiler" olddefconfig
make -C "$kernel_source" O="$build_dir" CC="$compiler" HOSTCC="$compiler" -j "$jobs" bzImage modules

# The APFS Makefile generates version.h and invokes Kbuild against this output tree.
make -C "$apfs_source" KERNEL_DIR="$build_dir" CC="$compiler" HOSTCC="$compiler" -j "$jobs"

kernel_release=$(make -s -C "$kernel_source" O="$build_dir" CC="$compiler" HOSTCC="$compiler" kernelrelease)
for module in "$apfs_source/apfs.ko" "$build_dir/fs/hfs/hfs.ko" "$build_dir/fs/hfsplus/hfsplus.ko"; do
  [[ -s "$module" ]] || { echo "Missing module: $module" >&2; exit 1; }
  vermagic=$(modinfo -F vermagic "$module")
  [[ "$vermagic" == "$kernel_release "* ]] || {
    echo "Module ABI mismatch: $module reports $vermagic, expected $kernel_release" >&2
    exit 1
  }
done
[[ -s "$build_dir/arch/x86/boot/bzImage" ]] || { echo "Missing bzImage" >&2; exit 1; }

stage=$(mktemp -d "${output_dir}.tmp.XXXXXX")
trap 'rm -rf -- "$stage"' EXIT
cp "$build_dir/arch/x86/boot/bzImage" "$stage/wsl_kernel"
cp "$apfs_source/apfs.ko" "$stage/apfs.ko"
cp "$build_dir/fs/hfs/hfs.ko" "$stage/hfs.ko"
cp "$build_dir/fs/hfsplus/hfsplus.ko" "$stage/hfsplus.ko"
cp "$build_dir/.config" "$stage/kernel.config"
{
  printf 'Kernel release: %s\n' "$kernel_release"
  printf 'Compiler: %s\n' "$("$compiler" --version | head -1)"
  printf 'Binutils: %s\n' "$(ld --version | head -1)"
  printf 'APFS version: %s\n' "$(modinfo -F version "$stage/apfs.ko")"
  sha256sum "$stage/wsl_kernel" "$stage/apfs.ko" "$stage/hfs.ko" "$stage/hfsplus.ko" "$stage/kernel.config"
} > "$stage/BUILD_INFO.txt"
mv -- "$stage" "$output_dir"
trap - EXIT
cat "$output_dir/BUILD_INFO.txt"
