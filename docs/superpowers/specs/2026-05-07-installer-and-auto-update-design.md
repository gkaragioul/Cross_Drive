# Installer Polish + Assisted Auto-Update — Design

**Date:** 2026-05-07
**Status:** Approved (verbal, this session)
**Pattern source:** [`H:\DevWork\Win_Apps\My_Local_Backup`](file://H:/DevWork/Win_Apps/My_Local_Backup) and [`H:\DevWork\Win_Apps\MyLocalBackup-releases`](file://H:/DevWork/Win_Apps/MyLocalBackup-releases) — copy this pattern with minor adaptations.

## Goal (plain English)

1. Ship GKMacOpener as a normal Windows installer that shows the EULA and requires the user to accept it.
2. When a new version is released, the running app shows an "Update available" banner. The user clicks **Update now**, the app downloads the new installer, verifies integrity, and launches the same Windows installer (which shows the EULA again). After the wizard finishes, the new version of the app starts automatically.
3. Releases live in a new public repo `georgekgr12/GK_Mac_Opener_Releases`. That repo holds nothing but a README and a LICENSE; every actual installer is a GitHub Release asset attached to a tag.
4. Publishing a new version is one command in the source repo.

## Non-goals

- No code-signing this iteration (existing release-audit gate stays unsigned-friendly).
- No MSI; only the NSIS `.exe` installer (and the existing portable `.exe` as a non-updatable side asset).
- No silent (`/passive`) update path. Every update goes through the wizard with the EULA on screen, by user direction.
- No periodic re-check while the app is open. Auto-check fires once on launch; manual button covers the rest.
- No third-party updater framework (Squirrel, electron-updater) — direct port of `MyLocalBackup.Core/Services/UpdateService.cs` to Node, since that's what we know works in production.

## Two repositories

### `georgekgr12/GK_Mac_Opener` (existing source repo)

Stays the home of source, docs, and CI. Adds the update backend, React UI, PowerShell helper template, publish script, and audit gates listed below.

### `georgekgr12/GK_Mac_Opener_Releases` (new, public)

Mirrors [`MyLocalBackup-releases`](file://H:/DevWork/Win_Apps/MyLocalBackup-releases) exactly:

```
README.md     — Title, big Download badge linking to
                releases/latest/download/GKMacOpenerSetup.exe,
                features list (pulled from current source-repo README),
                system requirements, MIT license summary, contact email,
                link back to source repo.
LICENSE       — MIT (verbatim copy of source repo's LICENSE).
```

No source, no scripts, no CHANGELOG file. Each release's notes live only in the GitHub Release body.

Release assets per tag (e.g. `v1.5.3`):

| Asset | Purpose | Used by updater? |
|---|---|---|
| `GKMacOpenerSetup.exe` | NSIS installer (assisted wizard, EULA, admin install) | **Yes** — both fresh install and in-app update download this. |
| `GKMacOpener-<version>.exe` | Existing portable build | No — informational only, for users who don't want to install. |

Release notes body ends with a single line: `SHA256: <64-hex>` — the SHA256 of `GKMacOpenerSetup.exe`. The updater parses this and refuses to run a download whose hash doesn't match.

## Installer changes (source repo)

`package.json` `build.nsis`:

| Field | New value | Reason |
|---|---|---|
| `oneClick` | `false` | Show the wizard with the EULA gate. |
| `allowToChangeInstallationDirectory` | `false` | Lock install path so updates land in the same place. |
| `artifactName` | `"GKMacOpenerSetup.exe"` | Stable filename so `releases/latest/download/GKMacOpenerSetup.exe` works for the updater. |
| `license` | `"build/EULA.txt"` (already set) | EULA accept screen. |
| `perMachine` | `true` (already set) | Install for all users; required because the app needs admin. |
| `requestedExecutionLevel` | `requireAdministrator` (already set) | Raw disk + WSL operations need admin. |

`package.json` `build.portable`:

| Field | New value |
|---|---|
| `artifactName` | `"GKMacOpener-${version}.exe"` |

The existing `installer.nsh` `customInstall` macro that runs `winfsp.msi` stays unchanged.

## In-app updater (source repo)

### Architecture

Direct port of `MyLocalBackup.Core/Services/UpdateService.cs` to Node, running inside the existing Express backend on `127.0.0.1:3001`. The React UI talks to it over the existing loopback API; no IPC bridge needed.

### Endpoints — `routes/updateRoutes.js` (new)

| Method + path | Behavior |
|---|---|
| `GET /api/update/check?auto=0\|1` | ETag-conditional GET to `https://api.github.com/repos/georgekgr12/GK_Mac_Opener_Releases/releases/latest`. Compares the tag (`vX.Y.Z`) to `package.json` version. If newer (and, when `auto=1`, not in `dismissed_update.txt`), returns `{available:true, version, downloadUrl, sha256, releaseNotes}`. Otherwise `{available:false}`. ETag + body cached in `github_etag.txt` so a 304 response doesn't burn rate limit. |
| `POST /api/update/download` | Streams the asset to `%TEMP%\gkmo_<guid>_GKMacOpenerSetup.exe`, computing SHA256 during write. On mismatch, deletes the file and returns 400. On success, returns the local path. |
| `GET /api/update/progress` | Server-sent events (`text/event-stream`) emitting `{percent, bytes, totalBytes}` for the active download. |
| `POST /api/update/launch` | Writes `pending_update.txt` (target version), spawns the PowerShell helper script (see below), then triggers `app.quit()` from main via an existing IPC channel. |
| `POST /api/update/dismiss` | Writes the version tag into `dismissed_update.txt`. |

### State files — `%LOCALAPPDATA%\GKMacOpener\updates\`

Same names and on-disk format as `MyLocalBackup` so the C# logic transfers without surprises:

| File | Content |
|---|---|
| `github_etag.txt` | Line 1: ETag string. Line 2+: cached JSON body of the last `releases/latest` response. |
| `dismissed_update.txt` | A single tag string (e.g. `v1.6.0`). Auto-checks skip this version. |
| `pending_update.txt` | Tag the update is moving toward. Written before installer launch; checked on next start. If the running version still matches the old version, the update silently failed — log a warning and clear the marker. |
| `previous_version.txt` | `<version>\|<download-url>` written before update so a future "rollback" feature has the data. Not used yet but kept for parity. |

### PowerShell relaunch helper

Verbatim port of `MyLocalBackup.Core/Services/UpdateService.cs:399-415`. Emitted at runtime to `%TEMP%\gkmo_relaunch_<guid>.ps1`. Steps:

1. `Start-Sleep -Seconds 2` — let the calling Electron process exit cleanly.
2. `Start-Process` the installer (no `/passive` — the user sees the full wizard with EULA).
3. `-Wait -PassThru` — block until the installer exits.
4. `Start-Process` the new `GKMacOpener.exe` from `%LOCALAPPDATA%\Programs\GKMacOpener\GKMacOpener.exe` (locked install path, so always known).
5. Fall back to the old exe path captured at script-emission time if the new path doesn't exist (installer was cancelled).
6. `Remove-Item $MyInvocation.MyCommand.Path` — self-delete.

The launcher invokes PowerShell with `-ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden` and `UseShellExecute=true` so the helper survives the Electron parent's exit.

### React UI

| Component | Where | Purpose |
|---|---|---|
| `<UpdateBanner>` | Top of Drives view (above `<SetupBanner>`) | Non-blocking. Shows "Update available: v1.6.0 — [Update now] [Later] [Skip this version]". |
| `<UpdateModal>` | Triggered by Update now button | Shows release notes (rendered from markdown), Download progress bar, install button, error states. |
| Settings → Updates card | Settings tab | Current version, last-checked timestamp, manual `Check for updates` button, Skipped-version display + Reset. |

### Frontend flow on launch

1. App boots, backend starts.
2. After Drives initial load, frontend calls `GET /api/update/check?auto=1` once.
3. If `available:true`, banner appears. If `available:false`, nothing changes.
4. Banner buttons:
   - **Update now** → opens `<UpdateModal>` → calls `POST /api/update/download`, listens to `GET /api/update/progress` SSE → on success, calls `POST /api/update/launch`.
   - **Later** → dismisses the banner for this session only.
   - **Skip this version** → calls `POST /api/update/dismiss`, banner disappears for that version permanently.

## Publishing a release

`scripts/publish-release.ps1` (new, modeled on `My_Local_Backup/release-build.ps1`):

```
publish-release.ps1 -Version 1.5.3
```

Steps:
1. Validate version format (X.Y.Z).
2. Verify clean git tree, on `main` branch.
3. Update `package.json` `version` field, commit, tag `v1.5.3`, push.
4. Run `npm run release:win:full` (build) followed by `npm run release:gate` (test + security audit + commercial gate + release audit including new gates below). Abort on any failure.
5. Compute SHA256 of `dist/GKMacOpenerSetup.exe`.
6. Read `RELEASE_NOTES.md` from the source repo (a small markdown file the user edits before running the script — empty template auto-created if missing).
7. Append `\n\nSHA256: <hex>` to the notes.
8. Run `gh release create v1.5.3 --repo georgekgr12/GK_Mac_Opener_Releases --notes-file <tempfile> dist/GKMacOpenerSetup.exe dist/GKMacOpener-1.5.3.exe`.
9. Update `GK_Mac_Opener_Releases/README.md`'s "Recent Releases" line to `v1.5.3 — <date>` and push to that repo.

If the user prefers to upload via the GitHub web UI (matching how MyLocalBackup is run today), the script supports `-Manual` to skip step 8 and just print the SHA256 line + the asset paths.

## Hardening (release-audit + self-test)

`scripts/release-audit.ps1` adds gates:
- `nsis.oneClick == false`
- `nsis.allowToChangeInstallationDirectory == false`
- `nsis.artifactName == "GKMacOpenerSetup.exe"`
- `routes/updateRoutes.js` exists
- `updateRoutes.js` references `github.com/georgekgr12/GK_Mac_Opener_Releases`
- `dist/GKMacOpenerSetup.exe` exists after `release:win:full`

`scripts/self-test.js` adds:
- `routes/updateRoutes.js` parses with `node --check` and exports a register function
- All four state-file paths in updateRoutes are absolute paths under `%LOCALAPPDATA%\GKMacOpener\updates\`

## Error handling

| Failure | UX |
|---|---|
| Network down on auto-check | Silent. No banner. Settings card shows "Last check: failed (network)". |
| GitHub rate-limited (403) | Silent on auto. Manual button shows "Rate limit hit, try again later". |
| Network down during download | Modal shows error + Retry button. |
| SHA256 mismatch | File deleted. Modal shows "Integrity check failed — possible tampered download. Aborted." No retry. |
| Installer process fails to launch | Modal shows error with the path. User can click "Open downloads folder" and double-click manually. |
| Installer wizard cancelled by user | Helper falls back to launching the old exe. App reopens unchanged. Pending marker left in place — cleared on next successful start with that version. |
| `pending_update.txt` present at start, version unchanged | Log warning ("Update to vX.Y.Z did not complete"), clear marker, continue boot. Don't re-prompt for that version this session. |
| Releases repo doesn't exist yet | Auto-check returns `{available:false}` and logs a one-time "no releases yet" line. Banner never appears. |

## Order of work (sub-projects)

These will become four sections of the implementation plan, run in order:

1. **Releases repo bootstrap** — `gh repo create georgekgr12/GK_Mac_Opener_Releases --public`, copy LICENSE, write README from template, push initial commit. No source-repo changes.
2. **Installer rework** — flip the four package.json fields, run `release:win:full`, manually verify the EULA gate appears.
3. **Updater backend + UI + helper script** — `routes/updateRoutes.js`, state file paths, React `<UpdateBanner>`/`<UpdateModal>`/Settings card, PowerShell helper template, IPC plumbing for `app.quit()` after launch, register in `server.js`.
4. **Publish pipeline + audit gates** — `scripts/publish-release.ps1`, `RELEASE_NOTES.md` template, new `release-audit.ps1` gates, new `self-test.js` assertions.
5. **End-to-end smoke test** — publish `v1.5.3` (or a `v1.5.3-test` pre-release) on the releases repo using the new script, run installed `v1.5.2`, walk the full Update → Download → Install → Relaunch flow on this machine.

## Manual-test acceptance criteria (for step 5)

- Banner appears on launch within 5 seconds.
- Clicking "Skip this version" makes the banner disappear and not reappear on next launch.
- Clicking "Update now" downloads, verifies SHA256, launches the wizard.
- Wizard shows the EULA; "I accept" must be clicked to proceed.
- After Finish, the new app launches automatically; About dialog shows the new version; `pending_update.txt` is gone.
- Repeating the smoke test with the SHA256 line manually corrupted in the release notes produces an integrity-check error in the modal and no installer is launched.

## Files touched

**New files**
- `routes/updateRoutes.js`
- `src/components/UpdateBanner.jsx`
- `src/components/UpdateModal.jsx`
- `scripts/publish-release.ps1`
- `RELEASE_NOTES.md` (template)

**Edited files**
- `package.json` (build config + new dependency: a markdown renderer for the modal, e.g. `marked`)
- `server.js` (register update route)
- `main.js` (IPC: receive "quit-for-update" from renderer)
- `preload.js` (expose the IPC)
- `src/App.jsx` (render banner, settings card)
- `scripts/release-audit.ps1` (new gates)
- `scripts/self-test.js` (new assertions)

**No changes**
- `LICENSE`, `build/EULA.txt`, `build/THIRD_PARTY_NOTICES.txt`, `build/GPL_SOURCE_OFFER.txt`, `build/LICENSE.GPL-2.0.txt` — license audit work from this session is intact.
- WSL2 / WinFsp / native engine code — none of it interacts with the updater.

## Open questions deferred to plan

- Exact React markdown renderer choice (`marked` vs. inline regex). Pick during implementation; both are MIT.
- Whether `publish-release.ps1` automates the README "Recent Releases" line edit in the releases repo or just opens the file for manual edit. Pick during implementation; preference is to automate.
