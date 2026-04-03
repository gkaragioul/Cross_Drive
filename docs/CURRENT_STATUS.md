# MacMount Current Status

## Current Date
- April 3, 2026

## Executive Summary
- `NOT FINISHED`
- The app is buildable and partially functional, but it does **not** yet meet the product goal of opening all supported Mac drive formats on Windows out of the box.
- The codebase is now much better at:
  - detecting Mac disk formats
  - exposing native mount state
  - handling stuck drive letters
  - identifying encrypted APFS volumes
  - failing early and clearly for unsupported cases
- The main remaining gap is not UI polish or routing. The main remaining gap is actual filesystem unlock/decryption support.

## Current Working Areas

### Build and Runtime
- `npm test` passes.
- `npm run build` passes.
- `dotnet build` passes for:
  - `native/MacMount.RawDiskEngine`
  - `native/MacMount.NativeBroker`
  - `native/MacMount.NativeService`

### Native Mount Flow
- Native-first app flow is in place.
- Stale native drive-letter cleanup was fixed.
- Native mount/unmount bookkeeping is more reliable.
- The app now avoids pointless retry loops for terminal failures such as:
  - encrypted APFS needing a password
  - CoreStorage detection
  - unsupported raw-provider filesystem cases

### Filesystem Detection
- Better native detection now exists for:
  - `APFS`
  - `HFS+`
  - `HFSX`
  - `Apple Partition Map (APM)` layouts
  - `CoreStorage` partition discovery
- APFS analysis now exposes:
  - volume names
  - role hints
  - encryption state
  - password-needed state

### UI / User Feedback
- Drive cards now surface richer scan-time information.
- Encrypted APFS can show as an unlock-needed case before mount.
- CoreStorage is shown as unsupported instead of failing ambiguously.

## Current Partially Working Areas

### APFS
- APFS detection and metadata traversal are much better than before.
- The raw provider can build a deeper directory catalog than the old shallow preview-only flow.
- Inline zlib-compressed files (decmpfs type 3) can now be read.
- Other compression types (LZVN, LZFSE, resource-fork-based) still return 0 bytes.
- However, APFS support is still incomplete and has not been validated across a serious image/device corpus.

### HFS+ / HFSX
- Detection is present.
- HFSX is now labeled distinctly.
- HFS browsing depends on the existing provider path and still needs broader validation with real media.

## Current Non-Working / Incomplete Areas

### APFS Encrypted Unlock
- The app can detect encrypted APFS and request a password.
- Native APFS decryption is now wired end-to-end:
  - Volume UUID is read from the APFS volume superblock (offset 0xF0).
  - Container keybag is read from the correct NX superblock offset (0x4F8).
  - PBKDF2 key derivation + RFC 3394 AES key unwrap produce the VEK.
  - DecryptingRawBlockDevice applies AES-XTS decryption transparently.
- **Status:** Implemented but needs validation against real encrypted APFS images.
- Known limitation: Only password-based unlock is supported. Hardware-bound keys (T2/Apple Silicon) cannot be unlocked.

### CoreStorage / Older FileVault
- CoreStorage is detected.
- Unlock/decryption is **not** implemented.
- These drives are currently blocked with a clear unsupported message.

### Native APFS Bridge Packaging
- The repo still does not contain a working bundled bridge binary at:
  - `native-bridge\apfs-fuse\build\apfs-fuse.exe`
- Without that binary, the PowerShell fallback path for APFS cannot succeed on a clean machine.

### “All Mac Formats” Goal
- The product goal is still unmet.
- The app does **not** yet provide end-to-end support for all intended Mac drive scenarios.

## Known Technical Constraints

### Platform Constraint
- Some detached internal Apple silicon / T2 encrypted Mac storage is not realistically mountable on Windows because of Apple hardware-bound encryption behavior.
- That is a platform limit, not just a repo gap.

### Windows Delivery Constraint
- If the drive must appear in Explorer as a real local drive, some native runtime component is still required.
- In practice, the project still depends on:
  - `WinFsp`
  - native broker/service components
  - a bundled APFS bridge or equivalent native unlock implementation

## Highest-Priority Remaining Work

### 1. Implement APFS Encrypted Unlock
- Decide the real unlock path:
  - finish and bundle the APFS bridge path
  - or implement native APFS decryption support in-house
- Required outcome:
  - user enters password
  - app unlocks encrypted APFS
  - mounted volume appears in Explorer

### 2. Implement CoreStorage / Older FileVault Unlock
- Parse and unlock CoreStorage-backed volumes.
- Do not route these through the APFS fallback path.
- Required outcome:
  - clear support path for older FileVault disks

### 3. Bundle Missing Native Runtime Pieces
- Bundle the required APFS bridge binary with the app.
- Ensure preflight checks reflect the actual shipped runtime.
- Remove the current gap where the app advertises native fallback but the required binary is absent.

### 4. Validate on Real Disk Images and Physical Media
- Build a real test matrix for:
  - APFS
  - APFS encrypted
  - APFS case-sensitive
  - HFS+
  - HFSX
  - CoreStorage / older FileVault
  - APM-based disks
- Required validation:
  - scan
  - mount
  - browse
  - open in Explorer
  - copy files out
  - unmount
  - reboot / crash cleanup

### 5. Tighten Product Packaging
- Produce a true out-of-box installer flow.
- Verify on a clean Windows machine with no developer tools installed.
- Ensure runtime prerequisites are bundled or installed by the app installer.

## Suggested Implementation Order

1. Bundle or restore the APFS bridge runtime.
2. Get encrypted APFS mounting working end to end with password flow.
3. Build a repeatable APFS test corpus and validate correctness.
4. Implement CoreStorage / FileVault unlock.
5. Run full mount/browse/export/unmount smoke tests on all supported format families.
6. Only after that, revisit commercial packaging, signing, and public-release readiness.

## Definition of “Working as Expected”

For this project, the app should only be considered working as expected when all of the following are true:

1. A supported Mac drive is detected correctly in the drive list.
2. The UI shows accurate filesystem and encryption status before mount.
3. Mount succeeds through the shipped runtime on a clean Windows machine.
4. The mounted drive appears in Explorer with a normal drive letter.
5. Browsing and file reads are correct and stable.
6. Encrypted supported formats can be unlocked with user-provided credentials.
7. Unmount and stale-drive cleanup work reliably after crashes, restarts, and failed mounts.

## Bottom Line
- The project has moved from prototype-quality behavior toward a more honest and structured native mount product.
- It is now better at detection, status reporting, and failure handling.
- It is **not** yet complete.
- The next phase must focus on real decryption/unlock capability and broader filesystem validation, not more surface-level wiring.
