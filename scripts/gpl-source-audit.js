const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
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

function moduleInfo(relativePath, key) {
  const data = fs.readFileSync(path.join(root, relativePath));
  const marker = Buffer.from(`${key}=`);
  let offset = -1;
  while ((offset = data.indexOf(marker, offset + 1)) !== -1) {
    if (offset > 0 && data[offset - 1] !== 0) continue;
    const end = data.indexOf(0, offset);
    if (end !== -1) return data.toString('utf8', offset + marker.length, end);
  }
  throw new Error(`${relativePath} has no ${key} module metadata`);
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
for (const moduleName of ['apfs.ko', 'hfs.ko', 'hfsplus.ko']) {
  const modulePath = `prereqs/crossdrive-kernel/modules/${moduleName}`;
  assert.ok(moduleInfo(modulePath, 'vermagic').startsWith(`${manifest.kernelRelease} `),
    `${moduleName} was not built for ${manifest.kernelRelease}`);
}
assert.equal(moduleInfo('prereqs/crossdrive-kernel/modules/apfs.ko', 'version'),
  manifest.apfsVersion, 'APFS module version differs from provenance');

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
  const entries = execFileSync('tar', ['-tf', bundle],
    { encoding: 'utf8' }).split(/\r?\n/).filter(Boolean);
  const requiredFiles = ['kernel.config', 'provenance.json', 'README.md',
    'REBUILD.md', 'LICENSE.GPL-2.0.txt', 'WSL2-Linux-Kernel.tar.gz',
    'linux-apfs-rw.tar.gz', 'original-build.sh', 'PATCHES.txt',
    'install-modules.sh', 'wslSetup.js',
    ...manifest.localPatches.map(patch => `patches/${path.basename(patch)}`)];
  const expectedEntries = new Set(['./', './patches/',
    ...requiredFiles.map(file => `./${file}`)]);
  for (const file of requiredFiles) {
    assert.ok(entries.includes(`./${file}`), `GPL source bundle is missing ${file}`);
  }
  for (const entry of entries) {
    assert.ok(expectedEntries.has(entry), `GPL source bundle contains unexpected entry ${entry}`);
  }
  for (const patch of manifest.localPatches) {
    assert.ok(entries.includes(`./patches/${path.basename(patch)}`),
      `GPL source bundle is missing patch ${patch}`);
  }
  function assertBundleBytes(entry, sourcePath) {
    const bundled = execFileSync('tar', ['-xOf', bundle, `./${entry}`]);
    const source = fs.readFileSync(path.join(root, sourcePath));
    assert.deepEqual(bundled, source, `GPL source bundle has different ${entry} bytes`);
  }
  assertBundleBytes('kernel.config', manifest.kernelConfig.path);
  assertBundleBytes('provenance.json', path.relative(root, manifestPath));
  assertBundleBytes('README.md', path.join('docs', 'gpl-source', release, 'README.md'));
  assertBundleBytes('REBUILD.md', path.join('docs', 'gpl-source', release, 'REBUILD.md'));
  assertBundleBytes('original-build.sh', manifest.originalBuildScript);
  assertBundleBytes('install-modules.sh', path.join('scripts', 'wsl_install_modules.sh'));
  assertBundleBytes('wslSetup.js', path.join('scripts', 'wslSetup.js'));
  for (const patch of manifest.localPatches) {
    assertBundleBytes(`patches/${path.basename(patch)}`, patch);
  }
}

console.log(`GPL source audit passed for ${release} (${manifest.status})`);
