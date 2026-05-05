#!/bin/bash
# Test WSL2 HFS+ mount via hfsfuse
set -e

DEV_BLOCK="${1:-sde}"   # default: sde (the device WSL sees for our drive)
TARGET="/mnt/macmount_test"

sudo mkdir -p "$TARGET"
sudo chown $(id -u):$(id -g) "$TARGET"

# Find the right partition: largest non-EFI partition on the device
PART=""
for p in $(lsblk -nlo NAME,SIZE -b "/dev/$DEV_BLOCK" 2>/dev/null | awk '$2 > 100000000 && $1 != "'"$DEV_BLOCK"'" {print $1}'); do
    PART="$p"
done
if [ -z "$PART" ]; then
    PART="$DEV_BLOCK"   # fall back to whole disk
fi
DEVPATH="/dev/$PART"
echo "Mounting $DEVPATH at $TARGET"

# hfsfuse needs to run as root to use allow_other
sudo hfsfuse -o allow_other,uid=$(id -u),gid=$(id -g),force "$DEVPATH" "$TARGET"

echo "--- mount table ---"
mount | grep "$TARGET" || echo "NOT MOUNTED"

echo "--- contents ---"
ls -la "$TARGET" 2>&1 | head -10

echo "--- attempt write ---"
echo "Hello from MacMount via WSL2 + hfsfuse @ $(date)" > "$TARGET/test_write.txt"
echo "wrote test_write.txt"
ls -la "$TARGET"

echo "--- attempt read back ---"
cat "$TARGET/test_write.txt"

echo "DONE"
