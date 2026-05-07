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
