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
  // Endpoints land in subsequent tasks (C2, C3, C4, C5).
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
