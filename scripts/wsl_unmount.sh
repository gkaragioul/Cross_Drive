#!/bin/bash
# wsl_unmount.sh — Unmount and tidy a previously-WSL-mounted Mac drive.
# Run as root.
#
# Args:
#   $1 = WSL mount target path (e.g. /mnt/macdrive_3_abc12345)
#
# Output (stdout): one JSON line: {"success":true|false, "error":"..."}

set -e
exec 3>&1
exec >&2

TARGET="${1:?target path required}"
emit_json() { printf '%s\n' "$1" >&3; }
json_escape() {
    python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1" 2>/dev/null \
        || printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr -d '\n')"
}

if mountpoint -q "$TARGET" 2>/dev/null; then
    sync
    if umount "$TARGET" 2>/tmp/mm_umount_err; then
        rmdir "$TARGET" 2>/dev/null || true
        emit_json "{\"success\":true}"
        exit 0
    fi
    # Try lazy unmount as fallback (handles "device or resource busy")
    if umount -l "$TARGET" 2>>/tmp/mm_umount_err; then
        rmdir "$TARGET" 2>/dev/null || true
        emit_json "{\"success\":true,\"lazy\":true}"
        exit 0
    fi
    ERR=$(cat /tmp/mm_umount_err 2>/dev/null | tr '\n' ' ' | head -c 500)
    emit_json "{\"success\":false,\"error\":$(json_escape "umount failed: $ERR")}"
    exit 0
fi

# Already unmounted — just clean up the empty dir
rmdir "$TARGET" 2>/dev/null || true
emit_json "{\"success\":true,\"alreadyUnmounted\":true}"
