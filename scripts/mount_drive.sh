#!/bin/bash
# mount_drive.sh - Mount a Mac-formatted drive in WSL2
# Usage: bash mount_drive.sh <DRIVE_ID> [EXPLICIT_DEV] [PASSWORD]

DRIVE_ID="$1"
EXPLICIT_DEV="$2"
PASSWORD="$3"
TARGET="/mnt/mac_drive_${DRIVE_ID}"

# FUSE options for maximum read performance
FUSE_OPTS="allow_other,default_permissions,uid=1000,gid=1000"

mkdir -p "$TARGET"
> /tmp/mount_err

# --- Device detection ---
if [ -n "$EXPLICIT_DEV" ]; then
  DEV="$EXPLICIT_DEV"
  echo "Using explicitly passed device: $DEV" >> /tmp/mount_err
else
  DEV=$(dmesg | grep -oE '\bsd[a-z]+\b' | tail -1)
  if [ -z "$DEV" ]; then
    DEV=$(lsblk -dno NAME | grep -E '^(sd|nvme|vd)' | tail -1)
  fi
fi

if [ -z "$DEV" ]; then
  echo '{"error":"No disk device found in WSL after attach."}'
  exit 0
fi

# Log the disk layout for debugging
lsblk "/dev/$DEV" >> /tmp/mount_err 2>&1

# --- Try each partition in order (skip EFI/small partitions < 500MB) ---
PARTITIONS=$(lsblk -nlo NAME "/dev/$DEV" 2>/dev/null | grep -v "^${DEV}$")

if [ -z "$PARTITIONS" ]; then
  # No partitions — try the raw disk
  PARTITIONS="$DEV"
fi

MOUNTED=0
SELECTED_DEV=""
SELECTED_VOL=""
ENCRYPTED_FOUND=0

unmount_target() {
  fusermount3 -u "$TARGET" >/dev/null 2>&1 || fusermount -u "$TARGET" >/dev/null 2>&1 || umount "$TARGET" >/dev/null 2>&1 || true
}

score_mount_root() {
  local root="$1"
  local visible userish penalize score

  visible=$(ls -1A "$root" 2>/dev/null | grep -vE '^\.' | wc -l | tr -d ' ')
  userish=$(ls -1A "$root" 2>/dev/null | grep -E '^(Users|home|Documents|Desktop|Downloads|Movies|Music|Pictures|Projects|Work|Data)$' | wc -l | tr -d ' ')
  penalize=$(ls -1A "$root" 2>/dev/null | grep -E '^(Preboot|Recovery|VM)$' | wc -l | tr -d ' ')

  score=$(( visible * 2 + userish * 10 - penalize * 20 ))
  echo "$score"
}

try_apfs_best_volume() {
  local devpath="$1"
  local pass="$2"
  local bestScore=-999999
  local bestVol=""
  local score vol
  local extra_opts=""

  if [ -n "$pass" ]; then
    extra_opts="-r $pass"
  fi

  # Check if volume is encrypted using apfsutil if available
  if command -v apfsutil >/dev/null 2>&1; then
      if apfsutil "$devpath" 2>/dev/null | grep -qi "FileVault:.*Yes"; then
          ENCRYPTED_FOUND=1
          echo "FileVault detected on $devpath (needs password)" >> /tmp/mount_err
          if [ -z "$pass" ]; then
              return 1
          fi
      fi
  fi

  # Probe likely APFS volume ids and pick the most user-data-like root.
  for vol in $(seq 0 15); do
    unmount_target
    if [ -n "$pass" ]; then
        apfs-fuse -v "$vol" -r "$pass" -o "$FUSE_OPTS" "$devpath" "$TARGET" 2>>/tmp/mount_err
    else
        apfs-fuse -v "$vol" -o "$FUSE_OPTS" "$devpath" "$TARGET" 2>>/tmp/mount_err
    fi

    if mount | grep -q "$TARGET"; then
      score=$(score_mount_root "$TARGET")
      echo "APFS probe $devpath vol=$vol score=$score" >> /tmp/mount_err
      if [ "$score" -gt "$bestScore" ]; then
        bestScore="$score"
        bestVol="$vol"
      fi
      unmount_target
    fi
  done

  if [ -n "$bestVol" ]; then
    if [ -n "$pass" ]; then
        apfs-fuse -v "$bestVol" -r "$pass" -o "$FUSE_OPTS" "$devpath" "$TARGET" 2>>/tmp/mount_err
    else
        apfs-fuse -v "$bestVol" -o "$FUSE_OPTS" "$devpath" "$TARGET" 2>>/tmp/mount_err
    fi
    
    if mount | grep -q "$TARGET"; then
      SELECTED_VOL="$bestVol"
      return 0
    fi
  fi

  return 1
}

for PART in $PARTITIONS; do
  DEVPATH="/dev/$PART"

  # Skip very small partitions (< 500MB) — likely EFI/recovery
  SIZE_KB=$(lsblk -bdno SIZE "$DEVPATH" 2>/dev/null || echo 0)
  if [ -n "$SIZE_KB" ] && [ "$SIZE_KB" -lt 524288000 ]; then
    echo "Skipping $DEVPATH (only ${SIZE_KB} bytes — likely EFI/recovery)" >> /tmp/mount_err
    continue
  fi

  echo "Trying $DEVPATH..." >> /tmp/mount_err

  # Attempt 1: APFS (probe volumes and select best candidate)
  if command -v apfs-fuse > /dev/null 2>&1; then
    if try_apfs_best_volume "$DEVPATH" "$PASSWORD"; then
      MOUNTED=1
      SELECTED_DEV="$DEVPATH"
      break
    fi
  fi

  # Attempt 2: hfsfuse (userspace HFS+ — no kernel module needed)
  if command -v hfsfuse > /dev/null 2>&1; then
    hfsfuse -o "$FUSE_OPTS" "$DEVPATH" "$TARGET" 2>>/tmp/mount_err
    if mount | grep -q "$TARGET"; then
      MOUNTED=1
      SELECTED_DEV="$DEVPATH"
      break
    fi
  else
    echo "hfsfuse not found — run Fix/Install Drivers" >> /tmp/mount_err
  fi

  # Attempt 3: kernel hfsplus (may not be in WSL2 kernel)
  modprobe hfsplus 2>/dev/null
  mount -t hfsplus -o force,rw "$DEVPATH" "$TARGET" 2>>/tmp/mount_err
  if mount | grep -q "$TARGET"; then MOUNTED=1; SELECTED_DEV="$DEVPATH"; break; fi
  mount -t hfsplus -o force,ro "$DEVPATH" "$TARGET" 2>>/tmp/mount_err
  if mount | grep -q "$TARGET"; then MOUNTED=1; SELECTED_DEV="$DEVPATH"; break; fi
done

# --- Result ---
if [ "$MOUNTED" -eq 1 ]; then
  if [ -n "$SELECTED_VOL" ]; then
    echo "{\"success\":true,\"device\":\"$SELECTED_DEV\",\"volumeId\":\"$SELECTED_VOL\"}"
  else
    echo "{\"success\":true,\"device\":\"$SELECTED_DEV\"}"
  fi
else
  if [ "$ENCRYPTED_FOUND" -eq 1 ] && [ -z "$PASSWORD" ]; then
    echo "{\"error\":\"Encrypted volume detected.\",\"needsPassword\":true,\"suggestion\":\"Please enter the FileVault password for this drive.\"}"
  else
    ERR=$(grep -v '^Using\|^NAME\|^SIZE\|^sdd ' /tmp/mount_err | head -10 | tr '"' "'" | tr '\n' ' ')
    echo "{\"error\":\"Mount failed: ${ERR}\",\"suggestion\":\"Ensure the drive is not encrypted or click Fix Drivers.\"}"
  fi
fi

