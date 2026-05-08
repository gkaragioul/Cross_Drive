#!/bin/bash
# Mount the attached HFS+ drive in WSL2 and run a write test.
# Run as root after `wsl --mount \\.\PHYSICALDRIVEx --bare`.
set -e

# Find the disk holding an hfsplus partition (look for FSTYPE column)
DEV=""
while IFS= read -r line; do
    name=$(echo "$line" | awk '{print $1}')
    fstype=$(echo "$line" | awk '{print $2}')
    if [ "$fstype" = "hfsplus" ] || [ "$fstype" = "apfs" ]; then
        # Strip trailing digits (partition number) to get the disk
        DEV=$(echo "$name" | sed 's/[0-9]*$//')
        break
    fi
done < <(lsblk -nlo NAME,FSTYPE 2>/dev/null)

if [ -z "$DEV" ]; then
    echo "ERROR: no hfsplus/apfs disk found"
    lsblk -bo NAME,SIZE,TYPE,FSTYPE
    exit 1
fi

echo "Selected disk: /dev/$DEV"
lsblk -bo NAME,SIZE,TYPE,FSTYPE "/dev/$DEV"

# Find the largest partition
PART=""
LARGEST=0
while IFS= read -r line; do
    name=$(echo "$line" | awk '{print $1}')
    size=$(echo "$line" | awk '{print $2}')
    if [ "$name" != "$DEV" ] && [ "$size" -gt "$LARGEST" ]; then
        LARGEST="$size"
        PART="$name"
    fi
done < <(lsblk -nlbo NAME,SIZE "/dev/$DEV" 2>/dev/null)

[ -z "$PART" ] && PART="$DEV"
DEVPATH="/dev/$PART"
echo "Mounting $DEVPATH"

mkdir -p /mnt/crossdrive_test
modprobe hfsplus
mount -t hfsplus -o rw,uid=1000,gid=1000,umask=000,force "$DEVPATH" /mnt/crossdrive_test 2>&1
echo "--- mount table ---"
mount | grep crossdrive_test
echo "--- root contents ---"
ls -la /mnt/crossdrive_test 2>&1
echo "--- WRITE TEST ---"
echo "Hello from real Linux kernel hfsplus driver! $(date)" > /mnt/crossdrive_test/wsl_kernel_write.txt
mkdir -p /mnt/crossdrive_test/MyFolder
echo "subfolder write" > /mnt/crossdrive_test/MyFolder/inside.txt
ls -la /mnt/crossdrive_test 2>&1
echo "--- READ BACK ---"
cat /mnt/crossdrive_test/wsl_kernel_write.txt
cat /mnt/crossdrive_test/MyFolder/inside.txt
echo "--- COPY 100MB FILE TEST ---"
dd if=/dev/urandom of=/mnt/crossdrive_test/big_test.bin bs=1M count=100 2>&1 | tail -3
ls -la /mnt/crossdrive_test/big_test.bin
echo "--- sync + free space ---"
sync
df -h /mnt/crossdrive_test
echo "DONE"

