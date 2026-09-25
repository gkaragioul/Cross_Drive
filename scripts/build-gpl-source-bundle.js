const { execFileSync } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const version = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8')).version;
const release = `v${version}`;
const recordDir = path.join(root, 'docs', 'gpl-source', release);
const manifest = JSON.parse(fs.readFileSync(path.join(recordDir, 'provenance.json'), 'utf8'));
const sourceRoot = process.env.CROSSDRIVE_GPL_SOURCE_ROOT;
if (process.argv.includes('--release') && manifest.status !== 'verified') {
  throw new Error('Cannot publish a GPL source bundle while binary provenance is unverified');
}
const stage = fs.mkdtempSync(path.join(os.tmpdir(), 'crossdrive-gpl-source-'));

function run(command, args) {
  return execFileSync(command, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] }).trim();
}

function archiveSource(name, source) {
  const checkout = sourceRoot ? path.join(sourceRoot, name) : path.join(stage, `checkout-${name}`);
  if (!sourceRoot) {
    run('git', ['-c', 'http.sslBackend=openssl', '-c', 'core.protectNTFS=false',
      'clone', '--no-checkout', '--depth', '1', '--branch', source.ref,
      source.repository, checkout]);
  }
  // Source trees may be staged by another local account; trust only this exact path.
  const safeDirectory = ['-c', `safe.directory=${checkout.replace(/\\/g, '/')}`];
  const commit = run('git', [...safeDirectory, '-C', checkout, 'rev-parse', 'HEAD']);
  if (commit !== source.commit) {
    throw new Error(`${name} is at ${commit}, expected ${source.commit}`);
  }
  // Git for Windows otherwise exports checkout-style CRLF into Linux build files.
  run('git', [...safeDirectory, '-c', 'core.autocrlf=false', '-c', 'core.protectNTFS=false', '-C', checkout, 'archive',
    '--format=tar.gz', `--prefix=${name}/`, `--output=${path.join(stage, `${name}.tar.gz`)}`, 'HEAD']);
  if (source.archiveSha256) {
    const actual = crypto.createHash('sha256')
      .update(fs.readFileSync(path.join(stage, `${name}.tar.gz`))).digest('hex');
    if (actual !== source.archiveSha256) {
      throw new Error(`${name} source archive hash mismatch: ${actual}`);
    }
  }
}

try {
  run(process.execPath, [path.join(__dirname, 'gpl-source-audit.js')]);
  archiveSource('WSL2-Linux-Kernel', manifest.kernelSource);
  archiveSource('linux-apfs-rw', manifest.apfsSource);
  for (const file of ['kernel.config', 'provenance.json', 'README.md', 'REBUILD.md']) {
    fs.copyFileSync(path.join(recordDir, file), path.join(stage, file));
  }
  fs.copyFileSync(path.join(root, 'build', 'LICENSE.GPL-2.0.txt'),
    path.join(stage, 'LICENSE.GPL-2.0.txt'));
  fs.copyFileSync(path.join(root, 'scripts', 'wsl_install_modules.sh'),
    path.join(stage, 'install-modules.sh'));
  fs.copyFileSync(path.join(root, 'scripts', 'wslSetup.js'),
    path.join(stage, 'wslSetup.js'));
  if (manifest.status === 'verified') {
    if (!Array.isArray(manifest.localPatches) || !manifest.originalBuildScript) {
      throw new Error('Verified provenance requires an explicit patch list and original build script');
    }
    fs.copyFileSync(path.join(root, manifest.originalBuildScript),
      path.join(stage, 'original-build.sh'));
    const patchDir = path.join(stage, 'patches');
    fs.mkdirSync(patchDir);
    for (const patch of manifest.localPatches) {
      fs.copyFileSync(path.join(root, patch), path.join(patchDir, path.basename(patch)));
    }
    fs.writeFileSync(path.join(stage, 'PATCHES.txt'),
      manifest.localPatches.length
        ? manifest.localPatches.map(patch => `patches/${path.basename(patch)}`).join('\n') + '\n'
        : 'No local patches.\n');
  } else {
    fs.writeFileSync(path.join(stage, 'PATCHES.txt'),
      'Original patch history is unknown; this archive is source materials, not verified complete corresponding source.\n');
  }

  const label = manifest.status === 'verified' ? 'GPL-Source' : 'GPL-Source-Materials';
  const output = path.join(root, 'dist', `CrossDrive-${label}-${release}.zip`);
  fs.mkdirSync(path.dirname(output), { recursive: true });
  const bundleFiles = ['WSL2-Linux-Kernel.tar.gz', 'linux-apfs-rw.tar.gz',
    'kernel.config', 'provenance.json', 'README.md', 'REBUILD.md',
    'LICENSE.GPL-2.0.txt', 'install-modules.sh', 'wslSetup.js', 'PATCHES.txt'];
  if (manifest.status === 'verified') bundleFiles.push('original-build.sh', 'patches');
  // The staging directory also holds temporary Git checkouts. Do not ship those
  // .git packfiles alongside the pinned source archives.
  run('tar', ['-a', '-cf', output, '-C', stage,
    ...bundleFiles.map(file => `./${file}`)]);
  console.log(`${output} (${fs.statSync(output).size} bytes)`);
} finally {
  const tempRoot = path.resolve(os.tmpdir()) + path.sep;
  if (path.resolve(stage).startsWith(tempRoot)) fs.rmSync(stage, { recursive: true, force: true });
}
