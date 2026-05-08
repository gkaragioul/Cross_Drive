#!/bin/bash
# Format the just-attached Mac drive as plain HFS+ using mkfs.hfsplus, then mount R/W.
# Run as root after `wsl --mount \\.\PHYSICALDRIVEx --bare`.
set -e

# Wait for the new device to appear (up to 5 seconds)
for i in 1 2 3 4 5; do
    DEV=$(lsblk -nlbo NAME,FSTYPE 2>/dev/null | awk '$2=="hfsplus" || $2=="apfs" {sub(/[0-9]+$/,"",$1); print $1; exit}')
    if [ -n "$DEV" ]; then break; fi
    # If no fstype yet, fall back to a fresh disk that has a partition but no FS
    DEV=$(lsblk -nlbo NAME,SIZE,TYPE 2>/dev/null | awk '$3=="disk" && $2 > 100000000000 {print $1}' | while read d; do
        if [ "$d" != "sda" ] && [ "$d" != "sdb" ] && [ "$d" != "sdd" ]; then
            echo "$d"; break
        fi
    done)
    [ -n "$DEV" ] && break
    sleep 1
done

if [ -z "$DEV" ]; then
    echo "ERROR: could not locate the attached drive in WSL"
    lsblk -bo NAME,SIZE,TYPE,FSTYPE
    exit 1
fi

echo "Disk: /dev/$DEV"

# Find largest partition
PART=""; LARGEST=0
while IFS= read -r line; do
    name=$(echo "$line" | awk '{print $1}')
    size=$(echo "$line" | awk '{print $2}')
    if [ "$name" != "$DEV" ] && [ "$size" -gt "$LARGEST" ]; then
        LARGEST="$size"
        PART="$name"
    fi
done < <(lsblk -nlbo NAME,SIZE "/dev/$DEV" 2>/dev/null)

if [ -z "$PART" ]; then
    echo "ERROR: no partition found on /dev/$DEV"
    lsblk -bo NAME,SIZE,TYPE "/dev/$DEV"
    exit 1
fi
DEVPATH="/dev/$PART"
echo "Partition: $DEVPATH"

# Make sure no stale mount is holding it
umount /mnt/crossdrive_test 2>/dev/null || true

# Format as plain (unjournaled) HFS+ — kernel hfsplus driver needs unjournaled for full R/W
echo "=== mkfs.hfsplus -v MMTEST $DEVPATH ==="
mkfs.hfsplus -v MMTEST "$DEVPATH" 2>&1

# Mount via kernel hfsplus
echo "=== Mounting RW ==="
mkdir -p /mnt/crossdrive_test
modprobe hfsplus
mount -t hfsplus -o rw,uid=1000,gid=1000,umask=000,force "$DEVPATH" /mnt/crossdrive_test 2>&1
mount | grep crossdrive_test

echo "=== Empty filesystem ready: ==="
ls -la /mnt/crossdrive_test
df -h /mnt/crossdrive_test
echo "DONE"

