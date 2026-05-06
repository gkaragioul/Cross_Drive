#!/bin/bash
# wsl_install_modules.sh — install bundled HFS+/APFS kernel modules into the
# running WSL2 distro. Run as root, after WSL has booted with the custom kernel.
#
# Args:
#   $1 = absolute Linux path to the modules directory containing
#        hfsplus.ko, hfs.ko, apfs.ko (e.g. /mnt/c/.../prereqs/macmount-kernel/modules)
#
# Output (stdout): one JSON line: {"success":true|false, "kver":"...", "loaded":[...], "error":"..."}

exec 3>&1
exec >&2

MOD_DIR="${1:?modules dir required}"
KVER=$(uname -r)

emit_json() { printf '%s\n' "$1" >&3; }
json_escape() {
    python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1" 2>/dev/null \
        || printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr -d '\n')"
}

if [ ! -d "$MOD_DIR" ]; then
    emit_json "{\"success\":false,\"error\":$(json_escape "modules dir not found: $MOD_DIR"),\"kver\":$(json_escape "$KVER")}"
    exit 0
fi

# Custom kernel marker — only attempt to install modules if the running kernel
# was built with this suffix (which our build_wsl_kernel script does).
case "$KVER" in
    *+) ;;
    *)
        emit_json "{\"success\":false,\"error\":$(json_escape "running kernel ($KVER) is not the MacMount custom kernel; .wslconfig may not be applied. Run wsl --shutdown and retry."),\"kver\":$(json_escape "$KVER")}"
        exit 0
        ;;
esac

DEST_DIR="/lib/modules/$KVER/extra"
mkdir -p "$DEST_DIR"
mkdir -p "/lib/modules/$KVER/kernel/fs/hfsplus"
mkdir -p "/lib/modules/$KVER/kernel/fs/hfs"

LOADED=()

install_one() {
    local src="$1"     # e.g. .../hfsplus.ko
    local dst="$2"     # absolute target
    local name="$3"    # module name (e.g. hfsplus)
    if [ ! -f "$src" ]; then
        echo "skip: $src not in bundle"
        return 1
    fi
    cp -f "$src" "$dst"
    echo "installed $dst"
    return 0
}

install_one "$MOD_DIR/hfsplus.ko" "/lib/modules/$KVER/kernel/fs/hfsplus/hfsplus.ko" "hfsplus"
install_one "$MOD_DIR/hfs.ko"     "/lib/modules/$KVER/kernel/fs/hfs/hfs.ko"         "hfs"
install_one "$MOD_DIR/apfs.ko"    "$DEST_DIR/apfs.ko"                                "apfs"

depmod -a "$KVER" 2>&1

# Try to modprobe each one and record what loaded.
for mod in hfsplus hfs apfs; do
    if lsmod | grep -q "^$mod\b" || modprobe "$mod" 2>/dev/null; then
        LOADED+=("$mod")
    fi
done

# Build JSON array of loaded modules
loaded_json='['
for m in "${LOADED[@]}"; do
    loaded_json+="\"$m\","
done
loaded_json="${loaded_json%,}]"

emit_json "{\"success\":true,\"kver\":$(json_escape "$KVER"),\"loaded\":$loaded_json}"
