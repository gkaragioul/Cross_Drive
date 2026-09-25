const { execFileSync } = require('node:child_process');
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
  const commit = run('git', ['-C', checkout, 'rev-parse', 'HEAD']);
  if (commit !== source.commit) {
    throw new Error(`${name} is at ${commit}, expected ${source.commit}`);
  }
  run('git', ['-c', 'core.protectNTFS=false', '-C', checkout, 'archive',
    '--format=tar.gz', `--prefix=${name}/`, `--output=${path.join(stage, `${name}.tar.gz`)}`, 'HEAD']);
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
      manifest.localPatches.length ? manifest.localPatches.join('\n') + '\n' : 'No local patches.\n');
  } else {
    fs.writeFileSync(path.join(stage, 'PATCHES.txt'),
      'Original patch history is unknown; this archive is source materials, not verified complete corresponding source.\n');
  }

  const label = manifest.status === 'verified' ? 'GPL-Source' : 'GPL-Source-Materials';
  const output = path.join(root, 'dist', `CrossDrive-${label}-${release}.zip`);
  fs.mkdirSync(path.dirname(output), { recursive: true });
  run('tar', ['-a', '-cf', output, '-C', stage, '.']);
  console.log(`${output} (${fs.statSync(output).size} bytes)`);
} finally {
  const tempRoot = path.resolve(os.tmpdir()) + path.sep;
  if (path.resolve(stage).startsWith(tempRoot)) fs.rmSync(stage, { recursive: true, force: true });
}
