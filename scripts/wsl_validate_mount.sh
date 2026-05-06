#!/bin/bash

TARGET="${1:-}"
EXPECTED_DEVICE="${2:-}"
EXPECTED_FS="${3:-}"

json_escape() {
    python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1" 2>/dev/null \
        || printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr -d '\n')"
}

emit_json() { printf '%s\n' "$1"; }
fail() { emit_json "{\"success\":false,\"error\":$(json_escape "$1")}"; exit 0; }

[ -n "$TARGET" ] || fail "target path missing"
[ -n "$EXPECTED_DEVICE" ] || fail "expected device missing"
[ -n "$EXPECTED_FS" ] || fail "expected filesystem missing"

if ! mountpoint -q "$TARGET" 2>/dev/null; then
    fail "not a mountpoint: $TARGET"
fi

ACTUAL_DEVICE=$(findmnt -rn -T "$TARGET" -o SOURCE 2>/dev/null | head -n 1)
ACTUAL_FS=$(findmnt -rn -T "$TARGET" -o FSTYPE 2>/dev/null | head -n 1)
MOUNT_OPTS=$(findmnt -rn -T "$TARGET" -o OPTIONS 2>/dev/null | head -n 1)

if [ "$ACTUAL_DEVICE" != "$EXPECTED_DEVICE" ]; then
    fail "source mismatch: expected $EXPECTED_DEVICE got ${ACTUAL_DEVICE:-none}"
fi

case "$EXPECTED_FS:$ACTUAL_FS" in
    hfsplus:hfsplus|hfsplus:hfs|hfs:hfs|hfs:hfsplus|apfs:apfs) ;;
    *) fail "filesystem mismatch: expected $EXPECTED_FS got ${ACTUAL_FS:-unknown}" ;;
esac

if printf '%s' "$MOUNT_OPTS" | tr ',' '\n' | grep -qx ro; then
    fail "mount is read-only"
fi

TOTAL_BYTES=0
FREE_BYTES=0
if DF_LINE=$(df -B1 "$TARGET" 2>/dev/null | awk 'NR==2 {print $2 " " $4}'); then
    TOTAL_BYTES=$(echo "$DF_LINE" | awk '{print $1}')
    FREE_BYTES=$(echo "$DF_LINE" | awk '{print $2}')
fi
case "$TOTAL_BYTES" in ''|*[!0-9]*) TOTAL_BYTES=0 ;; esac
case "$FREE_BYTES" in ''|*[!0-9]*) FREE_BYTES=0 ;; esac

emit_json "{\"success\":true,\"totalBytes\":$TOTAL_BYTES,\"freeBytes\":$FREE_BYTES}"
