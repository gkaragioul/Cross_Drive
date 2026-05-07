const path = require('path');
const fs = require('fs');
const os = require('os');
const crypto = require('crypto');
const https = require('https');
const { spawn } = require('child_process');
const express = require('express');

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
