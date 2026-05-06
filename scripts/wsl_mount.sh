#!/bin/bash
# wsl_mount.sh — Mount the most recently attached Mac-format drive R/W in WSL2.
# Run as root after `wsl --mount \\.\PHYSICALDRIVE<N> --bare`.
#
# Args:
#   $1 = MacMount drive ID (e.g. 3) — used in the mount path for uniqueness
#   $2 = optional APFS volume password
#
# Output: a single JSON object on stdout, one of:
#   {"success":true, "target":"/mnt/macdrive_<id>_<rand>", "fsType":"hfsplus"|"apfs", "device":"/dev/sdX1"}
#   {"success":false, "error":"...", "needsPassword":true|false}
#
# All diagnostics go to stderr (so stdout stays a single clean JSON line).
# Intentionally NO `set -e` — we always want to emit a JSON line on stdout,
# even if a non-critical step (chmod, rmdir) fails.

exec 3>&1   # save stdout
exec >&2    # send everything to stderr by default

DRIVE_ID="${1:-0}"
PASSWORD="${2:-}"

# --- Helpers ---
emit_json() { printf '%s\n' "$1" >&3; }
fail()      { emit_json "{\"success\":false,\"error\":$(json_escape "$1")}"; exit 0; }
fail_pwd()  { emit_json "{\"success\":false,\"error\":$(json_escape "$1"),\"needsPassword\":true}"; exit 0; }
json_escape() {
    # Minimal JSON string escape for our error strings
    python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1" 2>/dev/null \
        || printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr -d '\n')"
}
emit_success() {
    target="$1"
    fs_type="$2"
    device="$3"
    total_bytes=0
    free_bytes=0
    if df_line=$(df -B1 "$target" 2>/dev/null | awk 'NR==2 {print $2 " " $4}'); then
        total_bytes=$(echo "$df_line" | awk '{print $1}')
        free_bytes=$(echo "$df_line" | awk '{print $2}')
    fi
    case "$total_bytes" in ''|*[!0-9]*) total_bytes=0 ;; esac
    case "$free_bytes" in ''|*[!0-9]*) free_bytes=0 ;; esac
    emit_json "{\"success\":true,\"target\":$(json_escape "$target"),\"fsType\":$(json_escape "$fs_type"),\"device\":$(json_escape "$device"),\"totalBytes\":$total_bytes,\"freeBytes\":$free_bytes}"
}
verify_mount() {
    target="$1"
    expected_device="$2"
    expected_fs="$3"

    if ! mountpoint -q "$target" 2>/dev/null; then
        rmdir "$target" 2>/dev/null || true
        fail "Mounted path verification failed: $target is not a mountpoint. MacMount refused to expose a stale WSL folder as a Windows drive."
    fi

    actual_device=$(findmnt -rn -T "$target" -o SOURCE 2>/dev/null | head -n 1)
    actual_fs=$(findmnt -rn -T "$target" -o FSTYPE 2>/dev/null | head -n 1)
    mount_opts=$(findmnt -rn -T "$target" -o OPTIONS 2>/dev/null | head -n 1)

    if [ "$actual_device" != "$expected_device" ]; then
        umount "$target" 2>/dev/null || true
        rmdir "$target" 2>/dev/null || true
        fail "Mounted path verification failed: expected $expected_device at $target but found ${actual_device:-none}."
    fi

    case "$expected_fs:$actual_fs" in
        hfsplus:hfsplus|hfsplus:hfs|hfs:hfs|hfs:hfsplus|apfs:apfs) ;;
        *)
            umount "$target" 2>/dev/null || true
            rmdir "$target" 2>/dev/null || true
            fail "Mounted path verification failed: expected filesystem $expected_fs but found ${actual_fs:-unknown}."
            ;;
    esac

    if printf '%s' "$mount_opts" | tr ',' '\n' | grep -qx 'ro'; then
        umount "$target" 2>/dev/null || true
        rmdir "$target" 2>/dev/null || true
        fail "Kernel mounted as read-only (volume state may be inconsistent). Run fsck.hfsplus on a Mac/Linux box."
    fi
}

echo "== wsl_mount.sh drive_id=$DRIVE_ID =="

# --- Find the target disk ---
# Heuristic: pick a disk whose partition table contains a partition with
# hfsplus or apfs fstype, AND that is NOT one of WSL's internal disks
# (sda, sdb, sdc — typically system + swap + storage).
TARGET_DEV=""
while IFS= read -r line; do
    name=$(echo "$line" | awk '{print $1}')
    fstype=$(echo "$line" | awk '{print $2}')
    if [ "$fstype" = "hfsplus" ] || [ "$fstype" = "apfs" ]; then
        TARGET_DEV=$(echo "$name" | sed 's/[0-9]*$//')
        break
    fi
done < <(lsblk -nlo NAME,FSTYPE 2>/dev/null)

# If no FS detected (could be a freshly cleaned drive or APFS misdetection),
# fall back: pick the latest-attached disk that is large and unmounted.
if [ -z "$TARGET_DEV" ]; then
    # Disks larger than 10GB that don't look like our well-known WSL disks
    while IFS= read -r line; do
        name=$(echo "$line" | awk '{print $1}')
        size=$(echo "$line" | awk '{print $2}')
        type=$(echo "$line" | awk '{print $3}')
        if [ "$type" = "disk" ] && [ "$size" -gt 10737418240 ]; then
            # skip obvious WSL system disks
            case "$name" in
                sda|sdb|sdd) continue ;;  # the WSL2 stock kernel attaches sda (root), sdb (swap), sdd (system)
            esac
            TARGET_DEV="$name"
        fi
    done < <(lsblk -nlbo NAME,SIZE,TYPE 2>/dev/null)
fi

if [ -z "$TARGET_DEV" ]; then
    lsblk -bo NAME,SIZE,TYPE,FSTYPE
    fail "Could not locate the attached Mac drive in WSL2 — lsblk did not surface a hfsplus/apfs partition or a large unattached disk."
fi
echo "Target disk: /dev/$TARGET_DEV"

# --- Pick the largest partition (skip EFI / recovery) ---
PART=""; LARGEST=0
while IFS= read -r line; do
    name=$(echo "$line" | awk '{print $1}')
    size=$(echo "$line" | awk '{print $2}')
    if [ "$name" != "$TARGET_DEV" ] && [ "$size" -gt "$LARGEST" ]; then
        LARGEST="$size"
        PART="$name"
    fi
done < <(lsblk -nlbo NAME,SIZE "/dev/$TARGET_DEV" 2>/dev/null)
[ -z "$PART" ] && PART="$TARGET_DEV"
DEVPATH="/dev/$PART"
echo "Target partition: $DEVPATH"

# --- Detect FS type ---
FSTYPE=$(blkid -o value -s TYPE "$DEVPATH" 2>/dev/null || true)
if [ -z "$FSTYPE" ]; then
    # HFS+ has 'H+' (0x482B) or HFSX 'HX' (0x4858) signature at offset 1024
    SIG=$(dd if="$DEVPATH" bs=2 count=1 skip=512 2>/dev/null | od -An -tx1 | tr -d ' \n')
    case "$SIG" in
        482b|4858) FSTYPE="hfsplus" ;;
    esac
fi
if [ -z "$FSTYPE" ]; then
    # APFS NXSB magic at offset 32 of NX superblock (block 0)
    APFS_MAGIC=$(dd if="$DEVPATH" bs=1 count=4 skip=32 2>/dev/null)
    [ "$APFS_MAGIC" = "NXSB" ] && FSTYPE="apfs"
fi

echo "Detected FS: $FSTYPE"

# --- Build a unique mount path so Windows' 9P client never sees a stale cache ---
RAND=$(printf '%s' "$RANDOM$RANDOM" | head -c 8)
TARGET="/mnt/macdrive_${DRIVE_ID}_${RAND}"
mkdir -p "$TARGET"
chmod 777 "$TARGET"

case "$FSTYPE" in
    hfsplus|hfs)
        modprobe hfsplus 2>/dev/null || true

        # The Linux kernel mounts HFS+ read-only if the volume's "unmounted clean"
        # flag isn't set — even with -o force. We run fsck.hfsplus -f first to
        # clear the dirty flag (unjournaled volumes only — kernel can't safely
        # write to journaled volumes regardless).
        echo "Running fsck.hfsplus -f to clear dirty flag (if any)..."
        fsck.hfsplus -f -y "$DEVPATH" 2>&1 || echo "fsck warnings (non-fatal)"

        # umask=000 + uid/gid=1000 ensures Explorer can write through the 9P bridge.
        if mount -t hfsplus -o rw,uid=1000,gid=1000,umask=000,force "$DEVPATH" "$TARGET" 2>/tmp/mm_mount_err; then
            verify_mount "$TARGET" "$DEVPATH" "hfsplus"
            chmod 777 "$TARGET" 2>/dev/null || true
            emit_success "$TARGET" "hfsplus" "$DEVPATH"
            exit 0
        fi
        ERR=$(cat /tmp/mm_mount_err 2>/dev/null | tr '\n' ' ' | head -c 500)
        rmdir "$TARGET" 2>/dev/null || true
        # Most common cause: journaled HFS+ — kernel driver mounts these read-only.
        if echo "$ERR" | grep -qi "journal"; then
            fail "HFS+ journal present — run fsck.hfsplus or remount via Mac to flush. Detail: $ERR"
        fi
        fail "hfsplus mount failed: $ERR"
        ;;
    apfs)
        modprobe apfs 2>/dev/null || true
        # apfs.ko module from linux-apfs-rw supports R/W with caveats.
        APFS_OPTS="rw,uid=1000,gid=1000"
        [ -n "$PASSWORD" ] && APFS_OPTS="$APFS_OPTS,pass=$PASSWORD"
        if mount -t apfs -o "$APFS_OPTS" "$DEVPATH" "$TARGET" 2>/tmp/mm_mount_err; then
            verify_mount "$TARGET" "$DEVPATH" "apfs"
            chmod 777 "$TARGET"
            emit_success "$TARGET" "apfs" "$DEVPATH"
            exit 0
        fi
        ERR=$(cat /tmp/mm_mount_err 2>/dev/null | tr '\n' ' ' | head -c 500)
        rmdir "$TARGET" 2>/dev/null || true
        if echo "$ERR" | grep -qiE 'pass|key|encrypt'; then
            fail_pwd "APFS volume requires a password: $ERR"
        fi
        fail "apfs mount failed: $ERR"
        ;;
    *)
        rmdir "$TARGET" 2>/dev/null || true
        fail "Unsupported filesystem on $DEVPATH (detected: '$FSTYPE')"
        ;;
esac
