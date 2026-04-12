# Bug Fixes Design — 2026-04-11

## Scope

Four targeted fixes identified during workspace audit. No refactoring of surrounding code. Each fix is confined to a single file.

---

## Fix 1 — Unmount race condition

**File:** `routes/mountRoutes.js`

**Problem:** The `/api/unmount` handler fires the broker unmount request as a detached promise (fire-and-forget) then immediately starts the PowerShell unmount via `exec()`. The two operations run in parallel. If the broker hasn't finished tearing down the WinFsp mount point before PowerShell runs its cleanup, state becomes inconsistent. `nativeMountState.delete(driveId)` also appears in three places, making it unclear which one is authoritative.

**Fix:**
- Convert the handler to `async`.
- `await sendBrokerRequest(...)` for the unmount action before calling PowerShell.
- Promisify the `exec` call using `util.promisify` so the entire handler is a clean `async/await` chain.
- Single `nativeMountState.delete(driveId)` immediately after the broker await resolves (success or failure). Remove the duplicate deletes in the exec callback paths.
- `inFlightOps.delete(opKey)` and `ctx.invalidateDriveCache?.()` remain in a `finally` block.

**Sequence after fix:**
1. Broker unmount → await → log result
2. `nativeMountState.delete(driveId)`
3. `syncAssignedLetter(driveId, null)` cleanup
4. PowerShell unmount → await → parse result
5. `res.json(result)`
6. `finally`: delete opKey, invalidate cache

---

## Fix 2 — Ghost drive letter cleanup skipped on rapid unmount

**File:** `server.js`

**Problem:** `cleanupGhostDriveLetters()` has a 10-second rate limit. If multiple drives are unmounted within 10 seconds, all but the first cleanup call silently no-op. Ghost drive letters linger in Explorer.

**Fix:**
- Add a `cleanupSingleDriveLetter(letter)` function that calls `removeUserSessionDriveMapping(letter)` directly, with no rate limit, for a specific letter.
- Call `cleanupSingleDriveLetter(rememberedLetter)` in the unmount route after the drive is fully unmounted (after PS unmount succeeds).
- The existing `cleanupGhostDriveLetters()` with its rate-limited bulk scan is unchanged — it still serves its startup/periodic role.

---

## Fix 3 — APFS write code has no runtime gate

**File:** `native/MacMount.RawDiskEngine/ApfsParser.cs`

**Problem:** The APFS write path is disabled by a hardcoded `Writable: false` and a safety comment. One accidental edit enables untested B-tree mutations on real disks.

**Fix:**
- Read environment variable `MACMOUNT_EXPERIMENTAL_APFS_WRITES`.
- If the value is exactly `"1"`, set `Writable: true`; otherwise keep `Writable: false`.
- Retain the existing safety comment.
- No other files change. `ApfsRawFileSystemProvider` already gates on `_writable = writable && device.CanWrite`, so the defence-in-depth is preserved.

---

## Fix 4 — AES-XTS tweak missing spec reference

**File:** `native/MacMount.RawDiskEngine/AesXts.cs`

**Problem:** The tweak is built with only 8 bytes set (the unit number); the upper 8 bytes remain zero. This is correct per Apple's XTS-AES implementation, but looks like a bug to any reader unfamiliar with the spec.

**Fix:**
- Add a single comment above the tweak initialization:
  ```
  // Apple XTS-AES: tweak = unitNo as 64-bit LE in low half; upper 64 bits are zero.
  // This matches the APFS reference implementation and is intentional.
  ```
- No logic changes.

---

## Out of scope

- Broker `HandleMountRawProviderAsync` exception handling (Program.cs L489-493): reviewed and confirmed correct — original exception is preserved, Dispose errors are intentionally swallowed. Not a bug.
- Allocator O(n²) performance: deferred, not a correctness issue for current drive sizes.
- Named pipe ACLs: .NET defaults are acceptable for this use case.

---

## Testing

After fixes:
1. Mount a drive, then unmount it — verify no ghost letter in Explorer.
2. Mount and unmount two drives in quick succession — verify both letters cleaned up.
3. Confirm `MACMOUNT_EXPERIMENTAL_APFS_WRITES=1` is not set in any `.env` or launch script (it should not be set by default).
4. Run `npm run test` — existing self-test suite must pass.
