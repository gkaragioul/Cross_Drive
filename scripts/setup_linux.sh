#!/bin/bash
# setup_linux.sh - Run this inside WSL to prepare the Mac drivers

echo "Updating Linux environment..."
sudo apt-get update

echo "Installing HFS+ support..."
sudo apt-get install -y hfsprogs hfsplus

echo "Installing APFS support (read-only)..."
sudo apt-get install -y apfs-fuse

echo "Optional: Installing experimental APFS write support (linux-apfs-rw)..."
# This requires building from source, which we can automate if needed
# For now, we stick to stable drivers

echo "Creating mount points..."
sudo mkdir -p /mnt/mac
sudo chmod 777 /mnt/mac

echo "Setup Complete!"
