# Source-Built CrossDrive Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Produce a new CrossDrive installer whose bundled GPL kernel and modules have documented corresponding source, and make the release path publish that source beside the installer.

**Architecture:** Build the pinned WSL kernel and APFS module inside Ubuntu, starting from a checked-in configuration and a checked-in build script. Record the resulting binary hashes, config, source commits, and toolchain in a new versioned provenance record. Package those exact inputs and the built Windows installer as paired artifacts; keep the historical v1.5.34 record explicitly unverified.

**Tech Stack:** Linux kernel 6.6.87.2, linux-apfs-rw v0.3.20, Bash/Make/GCC, Electron Builder, Node.js, PowerShell, GitHub Actions.

**Spec:** `docs/gpl-source/v1.5.34/README.md` describes the historical gap; the requested outcome is a new build that can be offered as an online download with its source.

## Global Constraints

- Do not relabel the historical v1.5.34 binaries as verified.
- Build every GPL binary from pinned source and retain the exact final `.config`, build script, and any local patches.
- Preserve APFS, HFS, and HFS+ runtime module support.
- Do not publish a binary without a matching source archive.
- A missing code-signing certificate must be reported, not simulated.

## Review Focus

- Kernel and all three modules report the same release string; assert this before packaging.
- Bundled bytes match the new provenance hashes; run the GPL audit on the built installer inputs.
- The source archive contains both upstream source trees, the final config, build script, and patch declaration; inspect its entries.
- A failed Linux build must not leave old GPL binaries available for packaging; build into a fresh staging directory and copy only after success.
- An unsigned installer must be identified as unsigned and must not pass the signed release gate.

---

### Task 1: Establish a clean Linux source build

**Files:** Create `docs/gpl-source/v1.5.35/build-components.sh`; create `docs/gpl-source/v1.5.35/kernel.config`; modify `package.json` and `package-lock.json` for version 1.5.35.

**Interfaces:** The script takes an output directory and writes `wsl_kernel`, `apfs.ko`, `hfs.ko`, `hfsplus.ko`, final `kernel.config`, and build metadata there.

- [x] Inspect Ubuntu prerequisites and install missing compiler/build dependencies.
- [x] Extract the two pinned source archives; verify their commits against the recorded refs.
- [x] Compile the kernel and modules with the build script; resolve any build failures in the script or recorded config.
- [x] Check all four outputs and their module version strings before copying into the repo.
- [x] Commit the script, config, version change, and freshly built binaries.

### Task 2: Make provenance and source packaging verifiable

**Files:** Create `docs/gpl-source/v1.5.35/provenance.json`, `README.md`, and `REBUILD.md`; modify `scripts/gpl-source-audit.js`, `scripts/build-gpl-source-bundle.js`, and `docs/GPL_SOURCE_MANIFEST.md`.

**Interfaces:** `npm run license:audit` verifies recorded binary hashes and config; `npm run license:source-bundle -- --release` creates `dist/CrossDrive-GPL-Source-v1.5.35.zip`.

- [x] Record output hashes, upstream commits, toolchain, the applied patch, and the actual build script.
- [x] Extend the audit to verify module ABI and source-archive contents in release mode.
- [x] Generate the release source archive and inspect its file list and sizes.
- [x] Run the audit, then intentionally corrupt a staged copy to confirm it rejects mismatched bytes.
- [x] Commit the provenance and packaging changes.

### Task 3: Build and verify the Windows installer

**Files:** Modify release scripts and workflows only where needed to ensure source pairing and stale-artifact prevention; update release notes for v1.5.35.

**Interfaces:** `npm run release:win:unsigned` produces `dist/CrossDriveSetup.exe` and `dist/CrossDrive-1.5.35.exe`; signed release remains gated on a real certificate.

- [x] Clear or isolate the output directory so old installers cannot satisfy release checks.
- [x] Run project tests, commercial and security checks, then build the unsigned installer.
- [x] Inspect installer contents for the new four GPL binaries and notices.
- [x] Run the unsigned release audit and record SHA-256 hashes for installer and source archive.
- [x] Commit release-flow fixes; push the branch and update its draft PR after all checks pass.
