const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { extractKernelConfig } = require('./extract-kernel-config');

const root = path.resolve(__dirname, '..');
const version = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8')).version;
const release = `v${version}`;
const manifestPath = path.join(root, 'docs', 'gpl-source', release, 'provenance.json');
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));

function sha256(data) {
  return crypto.createHash('sha256').update(data).digest('hex');
}

assert.equal(manifest.release, release, 'GPL provenance must match the package version');
assert.match(manifest.kernelSource.commit, /^[0-9a-f]{40}$/);
assert.match(manifest.apfsSource.commit, /^[0-9a-f]{40}$/);

for (const [relativePath, expectedHash] of Object.entries(manifest.binaries)) {
  const actualHash = sha256(fs.readFileSync(path.join(root, relativePath)));
  assert.equal(actualHash, expectedHash, `${relativePath} does not match GPL provenance`);
}

const config = fs.readFileSync(path.join(root, manifest.kernelConfig.path));
assert.equal(sha256(config), manifest.kernelConfig.sha256, 'Kernel config digest mismatch');
const kernel = fs.readFileSync(path.join(root, 'prereqs', 'crossdrive-kernel', 'wsl_kernel'));
assert.deepEqual(extractKernelConfig(kernel), config, 'Recorded config differs from the shipped kernel');

if (process.argv.includes('--release')) {
  assert.equal(manifest.status, 'verified',
    'Cannot release with unverified GPL source provenance');
  assert.ok(Array.isArray(manifest.localPatches),
    'GPL provenance must explicitly list local patches or an empty list');
  assert.ok(manifest.originalBuildScript &&
    fs.existsSync(path.join(root, manifest.originalBuildScript)),
    'GPL provenance must include the actual binary build script');
  const bundle = path.join(root, 'dist', `CrossDrive-GPL-Source-${release}.zip`);
  assert.ok(fs.existsSync(bundle) && fs.statSync(bundle).size > 0,
    `Missing GPL source bundle: ${bundle}`);
  const entries = require('node:child_process').execFileSync('tar', ['-tf', bundle],
    { encoding: 'utf8' }).split(/\r?\n/);
  for (const file of ['kernel.config', 'provenance.json', 'REBUILD.md',
    'WSL2-Linux-Kernel.tar.gz', 'linux-apfs-rw.tar.gz', 'original-build.sh',
    'PATCHES.txt']) {
    assert.ok(entries.includes(`./${file}`), `GPL source bundle is missing ${file}`);
  }
}

console.log(`GPL source audit passed for ${release} (${manifest.status})`);
