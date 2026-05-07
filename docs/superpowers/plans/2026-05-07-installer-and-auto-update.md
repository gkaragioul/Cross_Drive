# GKMacOpener Installer + Auto-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship GKMacOpener as a normal Windows installer with EULA gate, plus an in-app update flow backed by a new public `GK_Mac_Opener_Releases` repo. Mirrors the MyLocalBackup-releases pattern.

**Architecture:** Two repos (source + releases-only). NSIS assisted wizard. Express updateRoutes polls GitHub Releases API with ETag caching, downloads installer to %TEMP%, verifies SHA256 from release notes body, hands off to a PowerShell helper that runs the installer (full UI) then relaunches the app. React UI: launch-time banner + modal + Settings card. Direct port of `MyLocalBackup.Core/Services/UpdateService.cs` to Node.

**Tech Stack:** Electron 42, React 18, Express 5, Node 20, NSIS via electron-builder 26, PowerShell 7. No new npm dependencies (Node 20 fetch + crypto are built-in; release notes rendered as `<pre>` plain text).

**Spec:** [docs/superpowers/specs/2026-05-07-installer-and-auto-update-design.md](../specs/2026-05-07-installer-and-auto-update-design.md)

---

## Sub-project A — Releases Repo Bootstrap

### Task A1: Create the public releases repo on GitHub

**Files:**
- None (gh CLI side effect only)

- [ ] **Step 1: Create the empty public repo**

```bash
gh repo create georgekgr12/GK_Mac_Opener_Releases \
  --public \
  --description "Official Windows installer downloads for GKMacOpener" \
  --homepage "https://github.com/georgekgr12/GK_Mac_Opener"
```

Expected output: `✓ Created repository georgekgr12/GK_Mac_Opener_Releases on GitHub`

- [ ] **Step 2: Confirm it exists and is public**

```bash
gh api repos/georgekgr12/GK_Mac_Opener_Releases --jq '{full_name, private, description}'
```

Expected: `{"full_name":"georgekgr12/GK_Mac_Opener_Releases","private":false,"description":"Official Windows installer downloads for GKMacOpener"}`

---

### Task A2: Initialize the releases repo with README + LICENSE

**Files:**
- Create (in temp clone): `README.md`, `LICENSE`

- [ ] **Step 1: Clone the empty repo to a temp location**

```bash
mkdir -p /tmp/gkmo_releases_init && cd /tmp/gkmo_releases_init
gh repo clone georgekgr12/GK_Mac_Opener_Releases .
```

- [ ] **Step 2: Copy LICENSE from source repo**

```bash
cp h:/DevWork/Win_Apps/GK_Mac_Opener/LICENSE LICENSE
```

- [ ] **Step 3: Create README.md**

Write `README.md` with this exact content:

```markdown
# GKMacOpener — Releases

Official Windows installer downloads for **GKMacOpener** — a free, open-source desktop app for mounting, browsing, and copying files from APFS and HFS+ Mac-formatted drives on Windows.

## Download Latest

[![Download Installer](https://img.shields.io/badge/Download-GKMacOpenerSetup.exe-blue?style=for-the-badge&logo=windows)](https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe)

> **Direct link:** https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/latest/download/GKMacOpenerSetup.exe

## Features

- Mount APFS and HFS+ Mac drives as Windows drive letters
- WSL2 kernel filesystem drivers (primary path) for read/write HFS+ support
- Native fallback engine for systems without WSL2
- Password unlock for encrypted APFS volumes (software-key)
- One-click in-app updates with SHA256 integrity verification

## System Requirements

- Windows 10/11 (64-bit)
- Administrator privileges (raw disk + WSL mount require it)
- WSL2 with Ubuntu (the installer can configure this on first launch)
- ~250 MB free disk space

## License

GKMacOpener is **free, open-source software** distributed under the MIT License. Full terms in [LICENSE](LICENSE) and shown during installation. Source code and contribution guidelines: https://github.com/georgekgr12/GK_Mac_Opener.

For licensing inquiries: open an issue at https://github.com/georgekgr12/GK_Mac_Opener/issues.

---
© 2026 GKMacOpener contributors.
```

- [ ] **Step 4: Commit and push initial content**

```bash
cd /tmp/gkmo_releases_init
git add README.md LICENSE
git commit -m "Initial commit: README + MIT LICENSE"
git push
```

- [ ] **Step 5: Verify the repo's README renders on GitHub**

```bash
gh repo view georgekgr12/GK_Mac_Opener_Releases --web
```

Visually confirm the README and Download badge appear. Then:

```bash
rm -rf /tmp/gkmo_releases_init
```

- [ ] **Step 6: Commit nothing in source repo (this task only touches the new repo)**

No commit needed in the GK_Mac_Opener source repo for this task.

---

## Sub-project B — Installer Rework + App Icon

### Task B1: Convert the new logo PNG to a square padded PNG + multi-resolution ICO

**Background:** The source `Gemini_Generated_Image_hk2lpxhk2lpxhk2l-removebg-preview.png` is 613×407 (non-square). Windows app icons must be square, so we pad it to 1024×1024 on a transparent canvas, then generate a multi-resolution `.ico` (256/128/64/48/32/16) for `build/icon.ico`. The same padded PNG goes to `build/icon.png` (used by Linux/macOS by some tooling) and `src/assets/gkmacopener-logo.png` (used in the app sidebar).

**Files:**
- Create: `h:/DevWork/Win_Apps/GK_Mac_Opener/scripts/build-icons.js`
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/package.json` (add devDependencies + script)
- Output (regenerated): `build/icon.png`, `build/icon.ico`, `src/assets/gkmacopener-logo.png`

- [ ] **Step 1: Add `sharp` and `png-to-ico` as devDependencies**

```bash
npm install --save-dev sharp@^0.33 png-to-ico@^2.1
```

Expected: both packages added under `devDependencies` in `package.json`.

- [ ] **Step 2: Create `scripts/build-icons.js`**

```js
// One-shot icon generator. Reads the source logo PNG, pads it to 1024x1024
// on a transparent canvas, writes the padded PNG to build/icon.png and
// src/assets/gkmacopener-logo.png, and generates a multi-resolution ICO at
// build/icon.ico. Re-run whenever the source logo changes.
const fs = require('fs');
const path = require('path');
const sharp = require('sharp');
const pngToIco = require('png-to-ico');

const root = path.resolve(__dirname, '..');
const SOURCE = path.join(root, 'Gemini_Generated_Image_hk2lpxhk2lpxhk2l-removebg-preview.png');
const OUT_PNG = path.join(root, 'build', 'icon.png');
const OUT_ICO = path.join(root, 'build', 'icon.ico');
const LOGO_PNG = path.join(root, 'src', 'assets', 'gkmacopener-logo.png');

async function main() {
  if (!fs.existsSync(SOURCE)) {
    console.error(`Source logo not found: ${SOURCE}`);
    process.exit(1);
  }
  const padded = await sharp(SOURCE)
    .resize({ width: 1024, height: 1024, fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .png()
    .toBuffer();
  fs.writeFileSync(OUT_PNG, padded);
  fs.writeFileSync(LOGO_PNG, padded);
  console.log(`Wrote ${OUT_PNG}`);
  console.log(`Wrote ${LOGO_PNG}`);

  const sizes = [256, 128, 64, 48, 32, 16];
  const buffers = await Promise.all(sizes.map(s =>
    sharp(padded).resize(s, s).png().toBuffer()
  ));
  const ico = await pngToIco(buffers);
  fs.writeFileSync(OUT_ICO, ico);
  console.log(`Wrote ${OUT_ICO} (sizes: ${sizes.join(',')})`);
}

main().catch(err => { console.error(err); process.exit(1); });
```

- [ ] **Step 3: Add an npm script for it**

In `package.json` under `"scripts"`, add:

```json
    "build:icons": "node scripts/build-icons.js"
```

- [ ] **Step 4: Run it and verify outputs**

```bash
npm run build:icons
```

Expected stdout:

```
Wrote h:\DevWork\Win_Apps\GK_Mac_Opener\build\icon.png
Wrote h:\DevWork\Win_Apps\GK_Mac_Opener\src\assets\gkmacopener-logo.png
Wrote h:\DevWork\Win_Apps\GK_Mac_Opener\build\icon.ico (sizes: 256,128,64,48,32,16)
```

Confirm `build/icon.png` is now ~1024×1024 by inspecting in any image viewer or:

```bash
file h:/DevWork/Win_Apps/GK_Mac_Opener/build/icon.png
```

Expected: `PNG image data, 1024 x 1024, 8-bit/color RGBA`.

- [ ] **Step 5: Commit**

```bash
git add scripts/build-icons.js package.json package-lock.json build/icon.png build/icon.ico src/assets/gkmacopener-logo.png
git commit -m "feat(branding): regenerate icon set from new logo (square 1024 PNG + multi-res ICO)"
```

---

### Task B2: Wire the icon into the NSIS installer explicitly

Electron-builder picks up `build/icon.ico` automatically for the .exe icon, but we make it explicit on the `nsis` block so the wizard header and the installer file icon both come from the new asset. We also point the .exe file icon directly via `win.icon`, and the existing `extraResources` already copies `build/icon.ico` and `build/icon.png` into the install folder.

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/package.json`

- [ ] **Step 1: Add explicit installer icons under `build.nsis`**

Inside `build.nsis` (added in Task B3 below — adjust the order if you're executing tasks strictly in document order; if Task B3 hasn't run yet, just add these two fields to the existing `nsis` block first):

```json
      "installerIcon": "build/icon.ico",
      "uninstallerIcon": "build/icon.ico",
      "installerHeaderIcon": "build/icon.ico",
```

- [ ] **Step 2: Confirm `build.win.icon` already points to `build/icon.ico`**

Read `package.json` `build.win`. It currently has `"icon": "build/icon.ico"` — leave it. If it's missing, add it.

- [ ] **Step 3: Run a dev build to confirm the config parses**

```bash
npm run release:prep
```

Expected: succeeds with no schema warnings about icons.

- [ ] **Step 4: Commit**

```bash
git add package.json
git commit -m "build(nsis): explicit installerIcon + uninstallerIcon + header icon"
```

---

### Task B3: Switch NSIS to assisted wizard with locked path and stable filename

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/package.json` (build.nsis + add build.portable artifactName)

- [ ] **Step 1: Edit `package.json` build section**

Find the existing `"nsis": { ... }` block (currently around lines 149–158) and replace with:

```json
    "nsis": {
      "oneClick": false,
      "perMachine": true,
      "allowElevation": true,
      "allowToChangeInstallationDirectory": false,
      "license": "build/EULA.txt",
      "createDesktopShortcut": true,
      "createStartMenuShortcut": true,
      "include": "build/installer.nsh",
      "artifactName": "GKMacOpenerSetup.exe",
      "uninstallDisplayName": "GKMacOpener",
      "runAfterFinish": true
    },
    "portable": {
      "artifactName": "GKMacOpener-${version}.exe"
    },
```

(The `portable` block sits as a sibling of `nsis`, not inside it.)

- [ ] **Step 2: Run a clean build to confirm electron-builder accepts the config**

```bash
npm run release:prep
```

Expected: builds succeed, no schema errors. Don't run a full build yet — that comes in Task B3.

- [ ] **Step 3: Commit**

```bash
git add package.json
git commit -m "build(installer): assisted NSIS wizard with EULA gate, locked path, stable filename"
```

---

### Task B4: Update self-test.js to assert the new build config

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/scripts/self-test.js` (insert near the existing `winTargets` checks around line 86)

- [ ] **Step 1: Add the new assertions**

After the existing block:

```js
if (!winTargets.includes('portable')) fail('package.json build.win.target missing portable');
else pass('win target includes portable');
```

insert:

```js
const nsisCfg = (pkg.build && pkg.build.nsis) || {};
if (nsisCfg.oneClick !== false) fail('nsis.oneClick must be false (assisted wizard with EULA gate)');
else pass('nsis.oneClick is false');

if (nsisCfg.allowToChangeInstallationDirectory !== false) fail('nsis.allowToChangeInstallationDirectory must be false (locked install path for updates)');
else pass('nsis.allowToChangeInstallationDirectory is false');

if (nsisCfg.artifactName !== 'GKMacOpenerSetup.exe') fail(`nsis.artifactName must be 'GKMacOpenerSetup.exe', found '${nsisCfg.artifactName}'`);
else pass('nsis.artifactName is GKMacOpenerSetup.exe');

if (nsisCfg.license !== 'build/EULA.txt') fail(`nsis.license must be 'build/EULA.txt', found '${nsisCfg.license}'`);
else pass('nsis.license points to build/EULA.txt');

const portableCfg = (pkg.build && pkg.build.portable) || {};
if (portableCfg.artifactName !== 'GKMacOpener-${version}.exe') fail(`portable.artifactName must be 'GKMacOpener-\${version}.exe', found '${portableCfg.artifactName}'`);
else pass('portable.artifactName is versioned');
```

- [ ] **Step 2: Run self-test**

```bash
npm run test
```

Expected output: `Self-test passed.` with five new PASS lines for the nsis/portable assertions.

- [ ] **Step 3: Commit**

```bash
git add scripts/self-test.js
git commit -m "test(self-test): assert new NSIS + portable build config"
```

---

### Task B5: Run a full unsigned build and manually verify the EULA gate + icon appear

**Files:**
- None (verification only)

- [ ] **Step 1: Build the installer + portable**

```bash
npm run release:win:unsigned
```

Expected: `dist/GKMacOpenerSetup.exe` and `dist/GKMacOpener-1.5.2.exe` are produced. The release-audit may print warnings about new gates that don't exist yet — that's fine for now; full hardening lands in sub-project E.

- [ ] **Step 2: Confirm artifact names**

```bash
ls -la "h:/DevWork/Win_Apps/GK_Mac_Opener/dist/" | grep -E "GKMacOpener(Setup)?(-1\.5\.2)?\.exe"
```

Expected: exactly two matches — `GKMacOpenerSetup.exe` and `GKMacOpener-1.5.2.exe`.

- [ ] **Step 3: Manually run the NSIS installer in a sandbox or VM**

Run `dist/GKMacOpenerSetup.exe` and walk the wizard. Verify:
- The installer's `.exe` file icon (visible in Explorer before double-clicking) is the new logo.
- A "Welcome / I accept the agreement" screen appears showing the EULA from `build/EULA.txt`. The wizard's header/title-bar icon is the new logo.
- The user must click "I Agree" to proceed.
- No install-folder selection screen appears (locked path).
- After install, the new GKMacOpener launches automatically because `runAfterFinish: true`.
- The taskbar icon and Start Menu shortcut both show the new logo.

If any step fails, fix `package.json` and rebuild before continuing. If it works, uninstall the test install via Apps & Features so the smoke test in sub-project F is clean.

- [ ] **Step 4: No commit (verification step)**

---

## Sub-project C — Updater Backend

### Task C1: Create state-file path helpers in updateRoutes.js

**Files:**
- Create: `h:/DevWork/Win_Apps/GK_Mac_Opener/routes/updateRoutes.js`

- [ ] **Step 1: Create the file with state-file helpers**

```js
const path = require('path');
const fs = require('fs');
const os = require('os');
const crypto = require('crypto');
const https = require('https');
const { spawn } = require('child_process');

const STATE_DIR = path.join(
  process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'),
  'GKMacOpener',
  'updates'
);
const ETAG_FILE = path.join(STATE_DIR, 'github_etag.txt');
const DISMISSED_FILE = path.join(STATE_DIR, 'dismissed_update.txt');
const PENDING_FILE = path.join(STATE_DIR, 'pending_update.txt');
const PREVIOUS_FILE = path.join(STATE_DIR, 'previous_version.txt');

const RELEASES_OWNER = 'georgekgr12';
const RELEASES_REPO = 'GK_Mac_Opener_Releases';
const RELEASES_API = `https://api.github.com/repos/${RELEASES_OWNER}/${RELEASES_REPO}/releases/latest`;
const INSTALLER_ASSET = 'GKMacOpenerSetup.exe';

function ensureStateDir() {
  try { fs.mkdirSync(STATE_DIR, { recursive: true }); } catch { /* ignore */ }
}

function readState(file) {
  try { return fs.readFileSync(file, 'utf8'); } catch { return null; }
}

function writeState(file, text) {
  ensureStateDir();
  try { fs.writeFileSync(file, text, 'utf8'); } catch { /* ignore */ }
}

function deleteState(file) {
  try { fs.unlinkSync(file); } catch { /* ignore */ }
}

module.exports = function mountUpdateRoutes(app, ctx) {
  // Endpoints land in subsequent tasks.
  return { STATE_DIR, ETAG_FILE, DISMISSED_FILE, PENDING_FILE, PREVIOUS_FILE };
};

// Exported for self-test
module.exports.STATE_DIR = STATE_DIR;
module.exports.ETAG_FILE = ETAG_FILE;
module.exports.DISMISSED_FILE = DISMISSED_FILE;
module.exports.PENDING_FILE = PENDING_FILE;
module.exports.PREVIOUS_FILE = PREVIOUS_FILE;
module.exports.RELEASES_API = RELEASES_API;
module.exports.INSTALLER_ASSET = INSTALLER_ASSET;
```

- [ ] **Step 2: Verify it parses**

```bash
node --check routes/updateRoutes.js && echo "OK"
```

Expected: `OK`.

- [ ] **Step 3: Commit**

```bash
git add routes/updateRoutes.js
git commit -m "feat(updater): updateRoutes scaffold + state-file paths"
```

---

### Task C2: Implement `GET /api/update/check`

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/routes/updateRoutes.js`

- [ ] **Step 1: Add the check handler and helpers**

Inside the `module.exports = function mountUpdateRoutes(app, ctx)` body, before the `return`, add:

```js
  const { addLog } = ctx || {};
  const log = (msg, type = 'info') => { try { addLog?.(`Updater: ${msg}`, type); } catch {} };

  function getCurrentVersion() {
    try {
      const pkg = require(path.join(__dirname, '..', 'package.json'));
      return pkg.version || '0.0.0';
    } catch { return '0.0.0'; }
  }

  function compareVersions(a, b) {
    // Returns positive if a > b, negative if a < b, 0 if equal. Compares X.Y.Z only.
    const pa = String(a).replace(/^v/, '').split('.').map(n => parseInt(n, 10) || 0);
    const pb = String(b).replace(/^v/, '').split('.').map(n => parseInt(n, 10) || 0);
    for (let i = 0; i < 3; i++) {
      const d = (pa[i] || 0) - (pb[i] || 0);
      if (d !== 0) return d;
    }
    return 0;
  }

  function fetchLatestRelease(callback) {
    ensureStateDir();
    const cached = readState(ETAG_FILE) || '';
    const [cachedEtag, ...rest] = cached.split('\n');
    const cachedBody = rest.join('\n');

    const headers = {
      'User-Agent': `GKMacOpener/${getCurrentVersion()}`,
      'Accept': 'application/vnd.github+json'
    };
    if (cachedEtag) headers['If-None-Match'] = cachedEtag;

    const req = https.request(RELEASES_API, { method: 'GET', headers, timeout: 15000 }, (res) => {
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => {
        const body = Buffer.concat(chunks).toString('utf8');
        if (res.statusCode === 304 && cachedBody) {
          callback(null, cachedBody);
          return;
        }
        if (res.statusCode === 200) {
          const etag = res.headers.etag || '';
          if (etag) writeState(ETAG_FILE, `${etag}\n${body}`);
          callback(null, body);
          return;
        }
        callback(new Error(`GitHub returned ${res.statusCode}`), null);
      });
    });
    req.on('timeout', () => { req.destroy(new Error('timeout')); });
    req.on('error', err => callback(err, null));
    req.end();
  }

  function parseSha256FromBody(body) {
    if (!body) return null;
    const m = body.match(/SHA256:\s*([a-fA-F0-9]{64})/);
    return m ? m[1].toLowerCase() : null;
  }

  app.get('/api/update/check', (req, res) => {
    const auto = String(req.query.auto || '0') === '1';
    fetchLatestRelease((err, json) => {
      if (err) {
        log(`check failed: ${err.message}`, 'warn');
        return res.json({ available: false, error: err.message });
      }
      let release;
      try { release = JSON.parse(json); } catch { return res.json({ available: false, error: 'invalid release JSON' }); }
      const tag = release.tag_name || '';
      if (!tag) return res.json({ available: false });
      const current = getCurrentVersion();
      if (compareVersions(tag, current) <= 0) return res.json({ available: false, current, latest: tag });

      if (auto) {
        const dismissed = (readState(DISMISSED_FILE) || '').trim();
        if (dismissed === tag) return res.json({ available: false, dismissed: tag });
      }

      const assets = Array.isArray(release.assets) ? release.assets : [];
      const installer = assets.find(a => a.name === INSTALLER_ASSET);
      if (!installer || !installer.browser_download_url) return res.json({ available: false, error: 'no installer asset' });

      const sha256 = parseSha256FromBody(release.body || '');
      if (!sha256) return res.json({ available: false, error: 'no SHA256 in release notes' });

      res.json({
        available: true,
        version: tag,
        current,
        downloadUrl: installer.browser_download_url,
        sha256,
        releaseNotes: release.body || ''
      });
    });
  });
```

- [ ] **Step 2: Add a self-test that the file parses and exports the register fn + state paths**

In `scripts/self-test.js`, after the existing `routeModules` for-loop (around line 204–214), insert:

```js
const updateRoutes = require(path.join(routesDir, 'updateRoutes.js'));
if (typeof updateRoutes !== 'function') fail('updateRoutes.js does not export a register function');
else pass('updateRoutes.js exports a register function');

for (const key of ['STATE_DIR', 'ETAG_FILE', 'DISMISSED_FILE', 'PENDING_FILE', 'PREVIOUS_FILE']) {
  const value = updateRoutes[key];
  if (typeof value !== 'string' || !path.isAbsolute(value) || !value.includes('GKMacOpener')) {
    fail(`updateRoutes.${key} must be an absolute path under GKMacOpener (got: ${value})`);
  } else {
    pass(`updateRoutes.${key} is absolute and namespaced`);
  }
}

if (updateRoutes.RELEASES_API !== 'https://api.github.com/repos/georgekgr12/GK_Mac_Opener_Releases/releases/latest') {
  fail(`updateRoutes.RELEASES_API does not point to GK_Mac_Opener_Releases`);
} else {
  pass('updateRoutes.RELEASES_API points to GK_Mac_Opener_Releases');
}
```

- [ ] **Step 3: Run self-test**

```bash
npm run test
```

Expected: all PASS, including the new updateRoutes assertions.

- [ ] **Step 4: Commit**

```bash
git add routes/updateRoutes.js scripts/self-test.js
git commit -m "feat(updater): GET /api/update/check with ETag + dismissed-version handling"
```

---

### Task C3: Implement `POST /api/update/download` + `GET /api/update/progress`

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/routes/updateRoutes.js`

- [ ] **Step 1: Add download + progress handlers**

Inside `mountUpdateRoutes`, after the `app.get('/api/update/check', ...)` handler:

```js
  let activeDownload = null; // { totalBytes, bytesDone, status: 'running'|'done'|'error', error, path, version }

  app.get('/api/update/progress', (req, res) => {
    res.json(activeDownload || { status: 'idle' });
  });

  app.post('/api/update/download', express.json(), (req, res) => {
    const { downloadUrl, sha256, version } = req.body || {};
    if (!downloadUrl || !sha256 || !version) return res.status(400).json({ error: 'downloadUrl, sha256, and version are required' });
    if (activeDownload && activeDownload.status === 'running') return res.status(409).json({ error: 'a download is already in progress' });

    const tmpPath = path.join(os.tmpdir(), `gkmo_${crypto.randomUUID()}_${INSTALLER_ASSET}`);
    activeDownload = { totalBytes: 0, bytesDone: 0, status: 'running', error: null, path: tmpPath, version };
    log(`download starting: ${version} -> ${tmpPath}`);

    function downloadOnce(url, redirectsLeft) {
      const req2 = https.request(url, { method: 'GET', headers: { 'User-Agent': `GKMacOpener/${getCurrentVersion()}` }, timeout: 30000 }, (resp) => {
        if ([301, 302, 303, 307, 308].includes(resp.statusCode) && resp.headers.location && redirectsLeft > 0) {
          resp.resume();
          return downloadOnce(resp.headers.location, redirectsLeft - 1);
        }
        if (resp.statusCode !== 200) {
          activeDownload.status = 'error';
          activeDownload.error = `HTTP ${resp.statusCode}`;
          resp.resume();
          return;
        }
        activeDownload.totalBytes = parseInt(resp.headers['content-length'] || '0', 10) || 0;
        const file = fs.createWriteStream(tmpPath);
        resp.on('data', (chunk) => { activeDownload.bytesDone += chunk.length; });
        resp.pipe(file);
        file.on('finish', () => {
          file.close(() => {
            const hash = crypto.createHash('sha256');
            const rs = fs.createReadStream(tmpPath);
            rs.on('data', c => hash.update(c));
            rs.on('end', () => {
              const actual = hash.digest('hex').toLowerCase();
              if (actual !== String(sha256).toLowerCase()) {
                try { fs.unlinkSync(tmpPath); } catch {}
                activeDownload.status = 'error';
                activeDownload.error = `Integrity check failed. Expected ${sha256}, got ${actual}.`;
                log(`download SHA256 mismatch: ${actual} vs ${sha256}`, 'error');
                return;
              }
              activeDownload.status = 'done';
              log(`download verified: ${tmpPath}`);
            });
            rs.on('error', err => {
              activeDownload.status = 'error';
              activeDownload.error = err.message;
            });
          });
        });
        file.on('error', err => {
          activeDownload.status = 'error';
          activeDownload.error = err.message;
        });
      });
      req2.on('timeout', () => { req2.destroy(new Error('download timeout')); });
      req2.on('error', err => {
        activeDownload.status = 'error';
        activeDownload.error = err.message;
      });
      req2.end();
    }

    downloadOnce(downloadUrl, 5);
    res.json({ accepted: true, path: tmpPath });
  });
```

Also add `const express = require('express');` to the top of the file (if not already present — check the require block at the top; if missing, insert below the `const fs = require('fs');` line).

- [ ] **Step 2: Verify the file still parses**

```bash
node --check routes/updateRoutes.js && echo "OK"
```

Expected: `OK`.

- [ ] **Step 3: Verify self-test still passes**

```bash
npm run test
```

Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add routes/updateRoutes.js
git commit -m "feat(updater): /download (streamed) + /progress (polled) with SHA256 verification"
```

---

### Task C4: Implement `POST /api/update/launch` (PowerShell helper spawn)

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/routes/updateRoutes.js`

- [ ] **Step 1: Add the launch handler and PS-helper builder**

Inside `mountUpdateRoutes`, after `/download`:

```js
  function buildRelaunchScript(installerPath, oldExePath) {
    // Verbatim port of MyLocalBackup.Core/Services/UpdateService.cs:399-415.
    // Sleep 2s -> run installer (full UI, no /passive) -> wait -> launch new app.
    const escSingle = (s) => String(s).replace(/'/g, "''");
    const newApp = `${process.env.LOCALAPPDATA}\\Programs\\GKMacOpener\\GKMacOpener.exe`;
    return [
      `$installer = '${escSingle(installerPath)}'`,
      `$oldApp = '${escSingle(oldExePath)}'`,
      `$newApp = '${escSingle(newApp)}'`,
      `Start-Sleep -Seconds 2`,
      `try { $proc = Start-Process $installer -Wait -PassThru } catch { $proc = $null }`,
      `if (Test-Path $newApp) { Start-Process $newApp }`,
      `elseif (Test-Path $oldApp) { Start-Process $oldApp }`,
      `Remove-Item $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue`
    ].join('\r\n');
  }

  app.post('/api/update/launch', express.json(), (req, res) => {
    const { installerPath, version } = req.body || {};
    if (!installerPath || !version) return res.status(400).json({ error: 'installerPath and version are required' });
    if (!fs.existsSync(installerPath)) return res.status(400).json({ error: 'installer not found at path' });

    writeState(PENDING_FILE, version);
    writeState(PREVIOUS_FILE, `${getCurrentVersion()}|`);

    const oldExe = process.execPath; // current Electron exe — fallback if installer is cancelled
    const helperPath = path.join(os.tmpdir(), `gkmo_relaunch_${crypto.randomUUID()}.ps1`);
    fs.writeFileSync(helperPath, buildRelaunchScript(installerPath, oldExe), 'utf8');

    const child = spawn(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass', '-File', helperPath],
      { detached: true, stdio: 'ignore', windowsHide: true }
    );
    child.unref();
    log(`launch helper spawned: ${helperPath}`);

    res.json({ accepted: true, helperPath });
  });
```

- [ ] **Step 2: Verify the file still parses**

```bash
node --check routes/updateRoutes.js && echo "OK"
```

Expected: `OK`.

- [ ] **Step 3: Verify self-test still passes**

```bash
npm run test
```

Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add routes/updateRoutes.js
git commit -m "feat(updater): /launch endpoint spawns PowerShell helper for full-UI install + relaunch"
```

---

### Task C5: Implement `POST /api/update/dismiss` and pending-update detection on startup

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/routes/updateRoutes.js`

- [ ] **Step 1: Add /dismiss handler + run a startup check inside the register function**

Inside `mountUpdateRoutes`, after `/launch`:

```js
  app.post('/api/update/dismiss', express.json(), (req, res) => {
    const { version } = req.body || {};
    if (!version) return res.status(400).json({ error: 'version required' });
    writeState(DISMISSED_FILE, version);
    log(`dismissed version: ${version}`);
    res.json({ ok: true });
  });

  // Startup check: if pending_update.txt exists and version didn't advance, the install failed silently.
  try {
    const pending = (readState(PENDING_FILE) || '').trim();
    if (pending) {
      const current = getCurrentVersion();
      if (compareVersions(pending, current) > 0) {
        log(`pending update did not complete (expected ${pending}, on ${current})`, 'warn');
      } else {
        log(`pending update succeeded: now on ${current}`);
      }
      deleteState(PENDING_FILE);
    }
  } catch (e) { log(`pending check failed: ${e.message}`, 'warn'); }
```

- [ ] **Step 2: Verify the file still parses**

```bash
node --check routes/updateRoutes.js && echo "OK"
```

- [ ] **Step 3: Run self-test**

```bash
npm run test
```

Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add routes/updateRoutes.js
git commit -m "feat(updater): /dismiss + startup detection of failed pending updates"
```

---

### Task C6: Wire updateRoutes into server.js

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/server.js`

- [ ] **Step 1: Add the require near the other route requires**

After line 13 (`const mountNativeRoutes = require('./routes/nativeRoutes');`), insert:

```js
const mountUpdateRoutes = require('./routes/updateRoutes');
```

- [ ] **Step 2: Find the existing route registration block**

Search server.js for `mountSystemRoutes(app,` — that's where the routes get registered. There should be a block like:

```js
mountSystemRoutes(app, ctx);
mountDriveRoutes(app, ctx);
mountMountRoutes(app, ctx);
mountNativeRoutes(app, ctx);
```

- [ ] **Step 3: Add the update route registration**

Append immediately after `mountNativeRoutes(app, ctx);`:

```js
mountUpdateRoutes(app, { addLog });
```

(If `addLog` isn't in scope at that point, pass `ctx` as in the other calls — the routes file only uses `ctx.addLog`.)

- [ ] **Step 4: Run self-test (it loads each route module)**

```bash
npm run test
```

Expected: PASS includes "updateRoutes.js exports a register function" and the API smoke test passes.

- [ ] **Step 5: Commit**

```bash
git add server.js
git commit -m "feat(server): register updateRoutes"
```

---

### Task C7: Add IPC channel `quit-for-update` (main + preload)

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/main.js`
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/preload.js`

- [ ] **Step 1: Add ipcMain handler in main.js**

In `main.js`, near the top after `const { app, BrowserWindow, dialog, Menu, shell } = require('electron');`, change to:

```js
const { app, BrowserWindow, dialog, Menu, shell, ipcMain } = require('electron');
```

Then, just before `app.on('ready', ...)` (around current line 207), add:

```js
ipcMain.handle('quit-for-update', () => {
    console.log(`[${APP_NAME}] Quit-for-update requested by renderer.`);
    setTimeout(() => app.quit(), 250); // give the renderer time to settle the response
    return true;
});
```

- [ ] **Step 2: Add the channel to the preload allowlist**

In `preload.js`, change line 10:

```js
    const allowed = ['open-explorer', 'get-app-paths'];
```

to:

```js
    const allowed = ['open-explorer', 'get-app-paths', 'quit-for-update'];
```

- [ ] **Step 3: Run self-test (validates main.js / preload.js syntax)**

```bash
npm run test
```

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add main.js preload.js
git commit -m "feat(ipc): expose quit-for-update channel for the in-app updater"
```

---

## Sub-project D — React UI

### Task D1: Add update API client functions

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/src/api.js`

- [ ] **Step 1: Append the new API functions**

At the end of `src/api.js`:

```js
export async function checkForUpdate(auto = false) {
  return fetchJson(`${BACKEND_URL}/api/update/check?auto=${auto ? 1 : 0}`);
}

export async function startUpdateDownload(downloadUrl, sha256, version) {
  return postJson(`${BACKEND_URL}/api/update/download`, { downloadUrl, sha256, version });
}

export async function fetchUpdateProgress() {
  return fetchJson(`${BACKEND_URL}/api/update/progress`);
}

export async function launchUpdateInstaller(installerPath, version) {
  return postJson(`${BACKEND_URL}/api/update/launch`, { installerPath, version });
}

export async function dismissUpdate(version) {
  return postJson(`${BACKEND_URL}/api/update/dismiss`, { version });
}
```

- [ ] **Step 2: Confirm Vite builds**

```bash
npm run build
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/api.js
git commit -m "feat(api): add update client functions"
```

---

### Task D2: Create the UpdateBanner component

**Files:**
- Create: `h:/DevWork/Win_Apps/GK_Mac_Opener/src/UpdateBanner.jsx`

- [ ] **Step 1: Write the component**

```jsx
import React from 'react';

export default function UpdateBanner({ update, onUpdateNow, onLater, onSkip }) {
  if (!update || !update.available) return null;
  return (
    <div style={{
      backgroundColor: 'rgba(229,83,0,0.08)',
      border: '1px solid rgba(229,83,0,0.3)',
      color: 'var(--primary)',
      padding: '14px 18px',
      marginBottom: '20px',
      display: 'flex',
      alignItems: 'center',
      gap: '12px',
      fontSize: '12px',
      fontFamily: 'var(--font-mono)',
      letterSpacing: '0.5px'
    }}>
      <span style={{ fontSize: 18 }}>&#x21bb;</span>
      <div style={{ flex: 1 }}>
        <strong>Update available: {update.version}</strong>
        {update.current && (
          <span style={{ opacity: 0.7, marginLeft: 8 }}>(installed: v{update.current})</span>
        )}
      </div>
      <button className="btn btn-primary" style={{ width: 'auto', padding: '6px 14px', fontSize: '11px' }} onClick={onUpdateNow}>
        Update now
      </button>
      <button className="btn btn-outline" style={{ width: 'auto', padding: '6px 12px', fontSize: '11px' }} onClick={onLater}>
        Later
      </button>
      <button className="btn btn-outline" style={{ width: 'auto', padding: '6px 12px', fontSize: '11px' }} onClick={onSkip}>
        Skip this version
      </button>
    </div>
  );
}
```

- [ ] **Step 2: Confirm Vite builds**

```bash
npm run build
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/UpdateBanner.jsx
git commit -m "feat(ui): UpdateBanner component"
```

---

### Task D3: Create the UpdateModal component

**Files:**
- Create: `h:/DevWork/Win_Apps/GK_Mac_Opener/src/UpdateModal.jsx`

- [ ] **Step 1: Write the component**

```jsx
import React, { useEffect, useState } from 'react';
import { startUpdateDownload, fetchUpdateProgress, launchUpdateInstaller } from './api';

export default function UpdateModal({ update, onClose }) {
  const [phase, setPhase] = useState('idle'); // idle | downloading | verifying | ready | launching | error
  const [progress, setProgress] = useState({ bytesDone: 0, totalBytes: 0 });
  const [installerPath, setInstallerPath] = useState(null);
  const [error, setError] = useState(null);

  useEffect(() => {
    if (phase !== 'downloading') return;
    const t = setInterval(async () => {
      try {
        const p = await fetchUpdateProgress();
        if (p.status === 'running') setProgress({ bytesDone: p.bytesDone || 0, totalBytes: p.totalBytes || 0 });
        else if (p.status === 'done') {
          setProgress({ bytesDone: p.totalBytes, totalBytes: p.totalBytes });
          setInstallerPath(p.path);
          setPhase('ready');
        } else if (p.status === 'error') {
          setError(p.error || 'Download failed');
          setPhase('error');
        }
      } catch { /* ignore poll errors */ }
    }, 250);
    return () => clearInterval(t);
  }, [phase]);

  const startDownload = async () => {
    setError(null);
    setPhase('downloading');
    try {
      const r = await startUpdateDownload(update.downloadUrl, update.sha256, update.version);
      if (!r.accepted) {
        setError(r.error || 'Could not start download');
        setPhase('error');
      }
    } catch (e) {
      setError(e.message);
      setPhase('error');
    }
  };

  const installAndRelaunch = async () => {
    setPhase('launching');
    try {
      await launchUpdateInstaller(installerPath, update.version);
      // tell main to quit so the helper script can run the installer
      if (window.macmount?.invoke) {
        try { await window.macmount.invoke('quit-for-update'); } catch {}
      }
    } catch (e) {
      setError(e.message);
      setPhase('error');
    }
  };

  const pct = progress.totalBytes > 0 ? Math.floor(progress.bytesDone / progress.totalBytes * 100) : 0;
  const mb = (n) => (n / 1048576).toFixed(1);

  return (
    <div className="modal-overlay fade-in">
      <div className="modal-content glass" style={{ maxWidth: 560, width: '92%' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 16 }}>
          <h3 style={{ margin: 0, fontFamily: 'var(--font-heading)', letterSpacing: 2, textTransform: 'uppercase', fontSize: 15 }}>
            Update GKMacOpener
          </h3>
          <span style={{ fontSize: 11, opacity: 0.6, fontFamily: 'var(--font-mono)' }}>
            v{update.current} &rarr; {update.version}
          </span>
        </div>

        <div style={{
          background: '#080808',
          border: '1px solid var(--border)',
          padding: 14,
          height: 200,
          overflowY: 'auto',
          fontFamily: 'var(--font-mono)',
          fontSize: 12,
          color: 'var(--text-dim)',
          marginBottom: 16,
          whiteSpace: 'pre-wrap'
        }}>
          {update.releaseNotes || '(no release notes)'}
        </div>

        {phase === 'downloading' && (
          <div style={{ marginBottom: 16 }}>
            <div style={{ fontSize: 12, fontFamily: 'var(--font-mono)', marginBottom: 6 }}>
              Downloading {mb(progress.bytesDone)} MB / {mb(progress.totalBytes)} MB ({pct}%)
            </div>
            <div style={{ height: 8, background: '#222', border: '1px solid var(--border)' }}>
              <div style={{ width: `${pct}%`, height: '100%', background: 'var(--primary)', transition: 'width 200ms' }} />
            </div>
          </div>
        )}

        {phase === 'ready' && (
          <div style={{ fontSize: 12, color: 'var(--success)', marginBottom: 16, fontFamily: 'var(--font-mono)' }}>
            Download verified. Click Install to launch the installer and restart the app.
          </div>
        )}

        {phase === 'error' && (
          <div style={{ fontSize: 12, color: 'var(--danger)', marginBottom: 16, fontFamily: 'var(--font-mono)' }}>
            ERROR: {error}
          </div>
        )}

        <div style={{ display: 'flex', gap: 12, justifyContent: 'flex-end' }}>
          {phase === 'idle' && (
            <>
              <button className="btn btn-outline" style={{ width: 'auto' }} onClick={onClose}>Cancel</button>
              <button className="btn btn-primary" style={{ width: 'auto' }} onClick={startDownload}>Download &amp; Install</button>
            </>
          )}
          {phase === 'downloading' && (
            <button className="btn btn-outline" style={{ width: 'auto' }} disabled>Downloading...</button>
          )}
          {phase === 'ready' && (
            <>
              <button className="btn btn-outline" style={{ width: 'auto' }} onClick={onClose}>Close</button>
              <button className="btn btn-primary" style={{ width: 'auto' }} onClick={installAndRelaunch}>Install &amp; Relaunch</button>
            </>
          )}
          {phase === 'launching' && (
            <button className="btn btn-outline" style={{ width: 'auto' }} disabled>Launching installer...</button>
          )}
          {phase === 'error' && (
            <button className="btn btn-outline" style={{ width: 'auto' }} onClick={onClose}>Close</button>
          )}
        </div>
      </div>
    </div>
  );
}
```

- [ ] **Step 2: Confirm Vite builds**

```bash
npm run build
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/UpdateModal.jsx
git commit -m "feat(ui): UpdateModal with progress, SHA256-verified download, install/relaunch"
```

---

### Task D4: Wire banner + modal + Settings card into App.jsx

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/src/App.jsx`

- [ ] **Step 1: Add imports + state**

Near the top of `App.jsx`, find the existing import block. After:

```js
import {
  fetchDrives as apiFetchDrives,
  fetchStatus,
  ...
  generateSupportBundle,
} from './api';
```

add:

```js
import {
  checkForUpdate,
  dismissUpdate,
} from './api';
import UpdateBanner from './UpdateBanner';
import UpdateModal from './UpdateModal';
```

(Either merge into the existing import or add as a second import block. Both work; cleaner is to add to the existing import list.)

Inside the `App` component, after the existing `useState` calls (around line 120), add:

```js
  const [update, setUpdate] = useState(null);
  const [updateModalOpen, setUpdateModalOpen] = useState(false);
  const [lastCheckedAt, setLastCheckedAt] = useState(null);
  const [manualCheckBusy, setManualCheckBusy] = useState(false);
```

- [ ] **Step 2: Add a launch-time check inside the existing `useEffect`**

Find the existing `useEffect(() => { ... }, []);` (around line 128). Inside it, after `loadDrives();` add:

```js
    checkForUpdate(true).then(safe(setUpdate)).then(() => safe(setLastCheckedAt)(new Date())).catch(() => {});
```

- [ ] **Step 3: Add update handlers**

After the existing `unmountDrive` function (around line 207), add:

```js
  const onUpdateLater = () => setUpdate(null);
  const onUpdateSkip = async () => {
    if (!update?.version) return;
    try { await dismissUpdate(update.version); } catch {}
    setUpdate(null);
  };
  const onUpdateNow = () => setUpdateModalOpen(true);
  const runManualUpdateCheck = async () => {
    setManualCheckBusy(true);
    try {
      const u = await checkForUpdate(false);
      setUpdate(u);
      setLastCheckedAt(new Date());
    } catch { /* ignore */ }
    finally { setManualCheckBusy(false); }
  };
```

- [ ] **Step 4: Render the banner above the SetupBanner in renderDrives**

Find the existing `renderDrives` function. Inside the returned fragment, just before `<SetupBanner setup={setup} />` (around line 259), add:

```jsx
        <UpdateBanner update={update} onUpdateNow={onUpdateNow} onLater={onUpdateLater} onSkip={onUpdateSkip} />
```

- [ ] **Step 5: Render the modal in the App return**

Find the existing `passwordPrompt && (` block (around line 500). Just before that block, inside the same fragment that contains the `<aside>` and `<main>`, add:

```jsx
      {updateModalOpen && update && (
        <UpdateModal update={update} onClose={() => setUpdateModalOpen(false)} />
      )}
```

- [ ] **Step 6: Add an "Updates" card to Settings**

In `renderSettings`, after the existing "Native Engine" card (`<h3>Native Engine</h3>...</div>` around line 415-420), add a new card:

```jsx
      <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>Updates</h3>
      <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
        <SettingsRow label="Installed Version" value={APP_VERSION} />
        <SettingsRow label="Latest Available" value={update?.available ? update.version : (update ? 'Up to date' : 'Unknown')} />
        <SettingsRow label="Last Check" value={lastCheckedAt ? lastCheckedAt.toLocaleString() : '—'} />
        <div style={{ marginTop: '12px', display: 'flex', gap: '8px' }}>
          <button className="btn btn-outline" style={{ width: 'auto', padding: '8px 16px' }} onClick={runManualUpdateCheck} disabled={manualCheckBusy}>
            {manualCheckBusy ? 'Checking...' : 'Check for updates'}
          </button>
          {update?.available && (
            <button className="btn btn-primary" style={{ width: 'auto', padding: '8px 16px' }} onClick={onUpdateNow}>
              Update now ({update.version})
            </button>
          )}
        </div>
      </div>
```

- [ ] **Step 7: Build and run dev to confirm UI compiles**

```bash
npm run build
```

Expected: build succeeds, no JSX errors.

- [ ] **Step 8: Commit**

```bash
git add src/App.jsx
git commit -m "feat(ui): wire UpdateBanner + UpdateModal + Settings update card into App"
```

---

## Sub-project E — Publish Pipeline + Audit Gates

### Task E1: Create RELEASE_NOTES.md template

**Files:**
- Create: `h:/DevWork/Win_Apps/GK_Mac_Opener/RELEASE_NOTES.md`

- [ ] **Step 1: Write the template**

```markdown
<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom — do not add it manually. -->

## Summary

(What changed in this release in 1-3 sentences.)

## Notable changes

- (bullet list)

## Known issues

- (bullet list, or remove this section if none)
```

- [ ] **Step 2: Commit**

```bash
git add RELEASE_NOTES.md
git commit -m "docs: add RELEASE_NOTES.md template for the publish pipeline"
```

---

### Task E2: Create scripts/publish-release.ps1

**Files:**
- Create: `h:/DevWork/Win_Apps/GK_Mac_Opener/scripts/publish-release.ps1`

- [ ] **Step 1: Write the script**

```powershell
#!/usr/bin/env pwsh
# Publish a GKMacOpener release to GK_Mac_Opener_Releases on GitHub.
# Usage: .\scripts\publish-release.ps1 -Version 1.5.3 [-Manual]
#   -Manual: skip the gh release create step; print the SHA256 line for manual upload.

param(
  [Parameter(Mandatory=$true)][string]$Version,
  [switch]$Manual
)

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  throw "Invalid version '$Version'. Expected X.Y.Z (e.g. 1.5.3)."
}

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
  Write-Host "=== GKMacOpener Release v$Version ===" -ForegroundColor Cyan

  # 1. Verify clean tree on main
  $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
  if ($branch -ne 'main') { throw "Not on main (currently '$branch'). Switch first." }
  $dirty = & git status --porcelain
  if ($dirty) { throw "Working tree is dirty. Commit or stash first." }

  # 2. Bump package.json version
  Write-Host "[1/6] Bumping package.json version..." -ForegroundColor Yellow
  $pkgPath = Join-Path $root "package.json"
  $pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
  $pkg.version = $Version
  ($pkg | ConvertTo-Json -Depth 32) | Set-Content $pkgPath -Encoding UTF8
  & git add package.json
  & git commit -m "chore(release): v$Version"

  # 3. Build + audit
  Write-Host "[2/6] Building installer + portable..." -ForegroundColor Yellow
  & npm run release:win:full
  if ($LASTEXITCODE -ne 0) { throw "release:win:full failed" }

  Write-Host "[3/6] Running release gate..." -ForegroundColor Yellow
  & npm run release:gate
  if ($LASTEXITCODE -ne 0) { throw "release:gate failed" }

  # 4. Locate artifacts and compute hash
  $setupExe = Join-Path $root "dist\GKMacOpenerSetup.exe"
  $portableExe = Join-Path $root ("dist\GKMacOpener-{0}.exe" -f $Version)
  if (-not (Test-Path $setupExe)) { throw "Missing $setupExe" }
  if (-not (Test-Path $portableExe)) { throw "Missing $portableExe" }

  Write-Host "[4/6] Computing SHA256 of installer..." -ForegroundColor Yellow
  $hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLower()
  Write-Host "  SHA256: $hash" -ForegroundColor Cyan

  # 5. Build release notes from RELEASE_NOTES.md + SHA256 line
  $notesSource = Join-Path $root "RELEASE_NOTES.md"
  if (-not (Test-Path $notesSource)) { throw "RELEASE_NOTES.md missing. Edit it before running this script." }
  $notes = Get-Content $notesSource -Raw
  $notesFinal = "$notes`n`nSHA256: $hash`n"
  $tmpNotes = Join-Path $env:TEMP "gkmo_release_notes_$Version.md"
  Set-Content $tmpNotes -Value $notesFinal -Encoding UTF8

  # 6. Tag and push
  Write-Host "[5/6] Tagging v$Version and pushing..." -ForegroundColor Yellow
  & git tag "v$Version"
  & git push origin main
  & git push origin "v$Version"

  if ($Manual) {
    Write-Host ""
    Write-Host "=== Manual upload ===" -ForegroundColor Green
    Write-Host "Upload these two files to a new release v$Version on georgekgr12/GK_Mac_Opener_Releases:"
    Write-Host "  $setupExe"
    Write-Host "  $portableExe"
    Write-Host ""
    Write-Host "Paste this into the release notes (replace the existing notes):"
    Write-Host "---"
    Write-Host $notesFinal
    Write-Host "---"
    Write-Host "SHA256 line is at the bottom." -ForegroundColor Cyan
    return
  }

  # 7. gh release create on the releases repo
  Write-Host "[6/6] Creating GitHub release on GK_Mac_Opener_Releases..." -ForegroundColor Yellow
  & gh release create "v$Version" `
      --repo georgekgr12/GK_Mac_Opener_Releases `
      --title "v$Version" `
      --notes-file $tmpNotes `
      $setupExe $portableExe
  if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

  Write-Host ""
  Write-Host "=== Release v$Version published ===" -ForegroundColor Green
  Write-Host "URL: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/tag/v$Version"
}
finally {
  Pop-Location
}
```

- [ ] **Step 2: Confirm the script parses**

```powershell
powershell -NoProfile -Command "Get-Command -Syntax scripts/publish-release.ps1" 2>&1 | Out-String
```

(Expected: prints a syntax line. Any parse error would surface here.)

- [ ] **Step 3: Commit**

```bash
git add scripts/publish-release.ps1
git commit -m "feat(release): publish-release.ps1 builds, hashes, and pushes to GK_Mac_Opener_Releases"
```

---

### Task E3: Add release-audit gates for the new install + updater config

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/scripts/release-audit.ps1`

- [ ] **Step 1: Add four new checks**

After the existing FFmpeg LGPL gate (`Check = "FFmpeg LGPL attribution in third-party notices"`), insert:

```powershell
$nsisCfg = $pkg.build.nsis
$checks += [pscustomobject]@{
    Check = "NSIS oneClick is false"
    Passed = ($nsisCfg.oneClick -eq $false)
    Detail = "package.json build.nsis.oneClick"
}

$checks += [pscustomobject]@{
    Check = "NSIS install path is locked"
    Passed = ($nsisCfg.allowToChangeInstallationDirectory -eq $false)
    Detail = "package.json build.nsis.allowToChangeInstallationDirectory"
}

$checks += [pscustomobject]@{
    Check = "NSIS artifactName is GKMacOpenerSetup.exe"
    Passed = ($nsisCfg.artifactName -eq "GKMacOpenerSetup.exe")
    Detail = "package.json build.nsis.artifactName = $($nsisCfg.artifactName)"
}

$updateRoutesPath = Join-Path $root "routes\updateRoutes.js"
$updateRoutesText = if (Test-Path $updateRoutesPath) { Get-Content $updateRoutesPath -Raw } else { "" }
$checks += [pscustomobject]@{
    Check = "updateRoutes targets GK_Mac_Opener_Releases"
    Passed = (Test-Path $updateRoutesPath) -and ($updateRoutesText -match "GK_Mac_Opener_Releases")
    Detail = $updateRoutesPath
}

$setupExePath = Join-Path $root "dist\GKMacOpenerSetup.exe"
$checks += [pscustomobject]@{
    Check = "Stable installer artifact present"
    Passed = (Test-Path $setupExePath)
    Detail = $setupExePath
}
```

- [ ] **Step 2: Run release:audit against current dist (it will fail because `release:win:full` hasn't run; that's OK — we're verifying the gate logic is wired)**

```bash
npm run release:audit:unsigned 2>&1 | tail -20
```

Expected: the new check rows appear in the table. They may report False until a fresh `release:win:full` is run; that's expected at this stage.

- [ ] **Step 3: Commit**

```bash
git add scripts/release-audit.ps1
git commit -m "test(release-audit): gate on assisted installer config + GK_Mac_Opener_Releases URL"
```

---

### Task E4: Add `npm run publish:release` script alias

**Files:**
- Modify: `h:/DevWork/Win_Apps/GK_Mac_Opener/package.json`

- [ ] **Step 1: Add the script entry**

In the `"scripts"` block, after `"release:candidate"`, add:

```json
    "publish:release": "powershell -ExecutionPolicy Bypass -File scripts/publish-release.ps1"
```

(Don't forget to add a comma to the previous line if needed.)

- [ ] **Step 2: Run self-test**

```bash
npm run test
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add package.json
git commit -m "chore(npm): add publish:release script alias"
```

---

## Sub-project F — End-to-End Smoke Test

### Task F1: Publish a real test release and walk the full update flow

**Files:**
- Edit: `RELEASE_NOTES.md` (one-time, summarizing what changed in this PR)

- [ ] **Step 1: Edit RELEASE_NOTES.md with a summary**

Replace the template content with something like:

```markdown
## Summary

GKMacOpener v1.5.3: assisted Windows installer with EULA acceptance gate, plus in-app one-click updates from a public releases repo.

## Notable changes

- Installer: new assisted wizard (Welcome → EULA → Install → Finish), locks install path, auto-launches the app.
- Updates: app checks `GK_Mac_Opener_Releases` on launch and via Settings → Check for updates. SHA256 from release notes is verified before any installer is launched.
- Releases repo: https://github.com/georgekgr12/GK_Mac_Opener_Releases
```

- [ ] **Step 2: Build the current branch as v1.5.2 first (to act as the "old" version that will be updated)**

Confirm `package.json` says `"version": "1.5.2"`. Then build:

```bash
npm run release:win:full
```

Manually run `dist/GKMacOpenerSetup.exe` to install GKMacOpener v1.5.2 on this machine.

- [ ] **Step 3: Bump and publish v1.5.3**

```powershell
.\scripts\publish-release.ps1 -Version 1.5.3
```

Expected: builds, hashes, tags, and uploads to `GK_Mac_Opener_Releases`. Final line prints the release URL.

- [ ] **Step 4: Launch the installed v1.5.2**

Run GKMacOpener from Start Menu. Within ~5 seconds the **Update banner** should appear at the top of the Drives view: "Update available: v1.5.3".

- [ ] **Step 5: Click "Update now"**

Modal opens with the release notes from step 1 + a "Download & Install" button. Click it. Progress bar fills. On completion, the modal shows "Download verified" and an "Install & Relaunch" button.

- [ ] **Step 6: Click "Install & Relaunch"**

Electron quits. The NSIS installer wizard appears. Verify:
- The EULA from `build/EULA.txt` is shown and must be accepted.
- After Finish, the new GKMacOpener v1.5.3 launches automatically.
- About dialog (Help → About GKMacOpener) shows v1.5.3.
- `%LOCALAPPDATA%\GKMacOpener\updates\pending_update.txt` no longer exists.

- [ ] **Step 7: Tampered-download negative test**

On the GitHub release, edit the release notes and change one hex character of `SHA256:` to make it incorrect. On the v1.5.2 install (use a second machine or reinstall v1.5.2), trigger the update. The modal should report "Integrity check failed" after download and refuse to launch the installer. Restore the correct hash before continuing.

- [ ] **Step 8: Skip-version test**

In the update banner, click "Skip this version". Confirm the banner disappears. Restart the app — banner does not reappear.

- [ ] **Step 9: No commit (manual verification)**

If any step fails, file the bug and fix before declaring smoke complete.

---

## Self-Review

**Spec coverage check:**

| Spec section | Covered by |
|---|---|
| Releases repo (README + LICENSE only) | A1, A2 |
| Permanent installer download URL | A2 (README badge), F1 (smoke test reaches via `releases/latest/download/GKMacOpenerSetup.exe`) |
| New logo on installer + app icon (taskbar / wizard header / .exe file icon) | B1 (square pad + multi-res ICO), B2 (NSIS config), B5 (manual verification) |
| Installer rework (oneClick=false, locked path, stable name) | B3, B4, B5, E3 |
| `routes/updateRoutes.js` `/check` | C1, C2 |
| `/download` + `/progress` | C3 |
| `/launch` + PowerShell helper | C4 |
| `/dismiss` + pending-update detection | C5 |
| Server registration | C6 |
| IPC channel `quit-for-update` | C7 |
| State files under LocalAppData | C1 (paths), C2 (self-test) |
| `<UpdateBanner>` | D1, D2, D4 |
| `<UpdateModal>` (release notes, progress, error states) | D3 |
| Settings → Updates card (manual check) | D4 |
| `publish-release.ps1` | E2, E4 |
| `RELEASE_NOTES.md` template | E1 |
| Hardened release-audit gates | E3 |
| Hardened self-test assertions | B2, C2 |
| End-to-end smoke (banner, download, verify, install, relaunch, skip, tamper) | F1 |

No gaps.

**Placeholder scan:** No TBD/TODO/"add appropriate handling". Every step has real code or real commands.

**Type consistency:** Endpoint contract is consistent across `routes/updateRoutes.js`, `src/api.js`, `UpdateBanner`, `UpdateModal`. State-file constants `STATE_DIR`/`ETAG_FILE`/`DISMISSED_FILE`/`PENDING_FILE`/`PREVIOUS_FILE` are defined once in C1 and reused in C2/C3/C4/C5. `INSTALLER_ASSET = "GKMacOpenerSetup.exe"` matches `nsis.artifactName` set in B1 and gated in E3.

**Scope:** One implementation plan, six sub-projects. Each commits independently. Smoke test in F1 verifies the whole stack end-to-end.
