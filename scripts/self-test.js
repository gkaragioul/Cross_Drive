const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

function fail(msg) {
  console.error(`FAIL: ${msg}`);
  process.exitCode = 1;
}

function pass(msg) {
  console.log(`PASS: ${msg}`);
}

const root = path.resolve(__dirname, '..');
const pkgPath = path.join(root, 'package.json');
const mainPath = path.join(root, 'main.js');
const preloadPath = path.join(root, 'preload.js');
const serverPath = path.join(root, 'server.js');
const auditPath = path.join(root, 'scripts', 'release-audit.ps1');
const validateReleasePath = path.join(root, 'scripts', 'validate-release.ps1');
const secAuditPath = path.join(root, 'scripts', 'security-audit.js');
const gatePath = path.join(root, 'scripts', 'commercial-gate.js');
const noticesPath = path.join(root, 'build', 'THIRD_PARTY_NOTICES.txt');
const gplOfferPath = path.join(root, 'build', 'GPL_SOURCE_OFFER.txt');
const gplManifestPath = path.join(root, 'docs', 'GPL_SOURCE_MANIFEST.md');
const licensePath = path.join(root, 'LICENSE');
const routesDir = path.join(root, 'routes');

for (const p of [pkgPath, mainPath, preloadPath, serverPath, auditPath, secAuditPath, gatePath, noticesPath, gplOfferPath, gplManifestPath, licensePath]) {
  if (!fs.existsSync(p)) fail(`missing file: ${p}`);
  else pass(`exists: ${path.basename(p)}`);
}

const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
const auditScript = fs.readFileSync(auditPath, 'utf8');
const validateReleaseScript = fs.existsSync(validateReleasePath) ? fs.readFileSync(validateReleasePath, 'utf8') : '';
const nativeBrokerClientScript = fs.readFileSync(path.join(root, 'scripts', 'nativeBrokerClient.js'), 'utf8');
const nativeBrokerProgram = fs.readFileSync(path.join(root, 'native', 'MacMount.NativeBroker', 'Program.cs'), 'utf8');
const licenseText = fs.readFileSync(licensePath, 'utf8');
const noticesText = fs.readFileSync(noticesPath, 'utf8');
const gplManifestText = fs.readFileSync(gplManifestPath, 'utf8');
const eulaText = fs.readFileSync(path.join(root, 'build', 'EULA.txt'), 'utf8');

function parseMajor(range) {
  const match = String(range || '').match(/(\d+)(?:\.\d+)?(?:\.\d+)?/);
  return match ? Number(match[1]) : NaN;
}

function assertDependencyMajor(group, name, expectedMajor) {
  const actual = pkg[group] && pkg[group][name];
  if (!actual) {
    fail(`package.json missing ${group}.${name}`);
    return;
  }
  const major = parseMajor(actual);
  if (major !== expectedMajor) {
    fail(`${group}.${name} must target major ${expectedMajor}; found ${actual}`);
  } else {
    pass(`${group}.${name} targets major ${expectedMajor}`);
  }
}

function assertNodeSyntax(filePath) {
  try {
    execFileSync(process.execPath, ['--check', filePath], { stdio: 'pipe' });
    pass(`${path.basename(filePath)} syntax is valid`);
  } catch (e) {
    const stderr = e.stderr ? String(e.stderr).trim() : e.message;
    fail(`${path.basename(filePath)} syntax is invalid: ${stderr}`);
  }
}

if (!pkg.scripts || !pkg.scripts.test) fail('package.json missing scripts.test');
else pass('package.json has scripts.test');

for (const scriptName of ['security:audit', 'commercial:gate', 'release:prep', 'release:win:unsigned', 'release:audit', 'signing:verify', 'release:candidate']) {
  if (!pkg.scripts || !pkg.scripts[scriptName]) fail(`package.json missing scripts.${scriptName}`);
  else pass(`package.json has scripts.${scriptName}`);
}

if (!(pkg.build && Array.isArray(pkg.build.files) && pkg.build.files.includes('preload.js'))) {
  fail('electron build files missing preload.js');
} else {
  pass('electron build includes preload.js');
}

const winTargets = (((pkg.build || {}).win || {}).target || []).map(String);
if (!winTargets.includes('nsis')) fail('package.json build.win.target missing nsis');
else pass('win target includes nsis');

if (!winTargets.includes('portable')) fail('package.json build.win.target missing portable');
else pass('win target includes portable');

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

const mainJs = fs.readFileSync(mainPath, 'utf8');
if (!mainJs.includes('contextIsolation: true')) fail('main.js missing contextIsolation: true');
else pass('contextIsolation enabled');

if (!mainJs.includes('nodeIntegration: false')) fail('main.js missing nodeIntegration: false');
else pass('nodeIntegration disabled');

if (!mainJs.includes('sandbox: true')) fail('main.js missing sandbox: true');
else pass('sandbox enabled');

if (!mainJs.includes('preload: path.join(__dirname, \'preload.js\')')) {
  fail('main.js missing preload path');
} else {
  pass('preload path configured');
}

if (!mainJs.includes('installAppMenu') || !mainJs.includes('THIRD_PARTY_NOTICES.txt')) {
  fail('main.js missing Help / third-party legal menu wiring');
} else {
  pass('third-party legal menu wired');
}

if (!mainJs.includes('About ${APP_NAME}') || !mainJs.includes('WinFsp - Windows File System Proxy')) {
  fail('main.js missing About dialog legal attribution');
} else {
  pass('About dialog legal attribution wired');
}

if (!nativeBrokerProgram.includes('Global\\\\GKMacOpener.NativeBroker') || !nativeBrokerProgram.includes('exiting duplicate process')) {
  fail('NativeBroker missing single-instance guard');
} else {
  pass('NativeBroker has single-instance guard');
}

if (!nativeBrokerClientScript.includes('brokerStartPromise') || !nativeBrokerClientScript.includes('concurrent startup/runtime probes')) {
  fail('nativeBrokerClient missing broker start coalescing');
} else {
  pass('nativeBrokerClient coalesces broker starts');
}

if (!nativeBrokerProgram.includes('DeletePathWithRetry') || !nativeBrokerProgram.includes('Passthrough delete failed')) {
  fail('NativeBroker passthrough delete cleanup can silently fail');
} else {
  pass('NativeBroker logs and retries passthrough delete cleanup');
}

if (!nativeBrokerProgram.includes('MACMOUNT_ENABLE_UNC_METADATA_CACHE') || !nativeBrokerProgram.includes('_enableMetadataCache && _dirCache')) {
  fail('NativeBroker can serve stale WSL passthrough metadata cache');
} else {
  pass('NativeBroker disables WSL passthrough metadata cache by default');
}

assertDependencyMajor('dependencies', 'express', 5);
assertDependencyMajor('devDependencies', 'electron', 42);
assertDependencyMajor('devDependencies', 'electron-builder', 26);
assertDependencyMajor('devDependencies', 'vite', 8);
assertDependencyMajor('devDependencies', '@vitejs/plugin-react', 6);

for (const entryPath of [mainPath, preloadPath, serverPath]) {
  assertNodeSyntax(entryPath);
}

for (const needle of [
  'Bundled WSL kernel',
  'Bundled WSL module: apfs.ko',
  'Bundled WSL module: hfs.ko',
  'Bundled WSL module: hfsplus.ko',
  'Native service published',
  'Native broker published',
  'User-session helper published'
]) {
  if (!auditScript.includes(needle)) fail(`release audit missing check: ${needle}`);
  else pass(`release audit checks ${needle}`);
}

if (!auditScript.includes('Extracted WinFsp payload not packaged')) fail('release audit does not block extracted WinFsp payloads');
else pass('release audit blocks extracted WinFsp payloads');

if (!auditScript.includes('Packaging avoids dev script globs') || !auditScript.includes('Dev/release scripts not packaged')) {
  fail('release audit does not block dev/release scripts from packaging');
} else {
  pass('release audit blocks dev/release scripts from packaging');
}

if (!auditScript.includes('GPL source manifest present')) fail('release audit missing GPL source manifest check');
else pass('release audit checks GPL source manifest');

if (!licenseText.includes('Copyright (c) 2026 GKMacOpener contributors')) fail('LICENSE copyright is not GKMacOpener 2026');
else pass('LICENSE copyright is GKMacOpener 2026');

if (!eulaText.includes('GKMacOpener is distributed under the MIT License') || !eulaText.includes('WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos')) {
  fail('EULA missing MIT/WinFsp FLOSS notice');
} else {
  pass('EULA includes MIT/WinFsp FLOSS notice');
}

if (!noticesText.includes('WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos') || !noticesText.includes('Custom WSL2 kernel and filesystem modules')) {
  fail('third-party notices missing WinFsp or WSL GPL notices');
} else {
  pass('third-party notices include WinFsp and WSL GPL notices');
}

if (!/linux-msft-wsl-6\.6\.87\.2/.test(gplManifestText) || !/linux-apfs-rw/.test(gplManifestText) || !/0\.3\.20/.test(gplManifestText) || !/kernel `\.config`/.test(gplManifestText)) {
  fail('GPL source manifest missing kernel/APFS source requirements');
} else {
  pass('GPL source manifest documents kernel/APFS source requirements');
}

if (validateReleaseScript.includes('Encrypted APFS volumes will not be unlockable')) {
  fail('validate-release still treats legacy apfs-fuse as required for encrypted APFS');
} else {
  pass('validate-release does not require legacy apfs-fuse for encrypted APFS');
}

const readinessDocs = [
  path.join(root, 'docs', 'CURRENT_STATUS.md'),
  path.join(root, 'docs', 'COMMERCIAL_READINESS.md'),
  path.join(root, 'docs', 'GO_NO_GO.md')
];
for (const docPath of readinessDocs) {
  const doc = fs.readFileSync(docPath, 'utf8');
  if (!/WSL2 kernel|WSL kernel/i.test(doc)) fail(`${path.basename(docPath)} missing WSL kernel architecture`);
  else pass(`${path.basename(docPath)} documents WSL kernel architecture`);
  if (!/APFS writes?.*experimental|experimental APFS writes?/i.test(doc)) fail(`${path.basename(docPath)} missing experimental APFS write policy`);
  else pass(`${path.basename(docPath)} documents experimental APFS write policy`);
  if (!/CoreStorage.*unsupported|unsupported.*CoreStorage/i.test(doc)) fail(`${path.basename(docPath)} missing CoreStorage unsupported policy`);
  else pass(`${path.basename(docPath)} documents CoreStorage unsupported policy`);
}

const systemRoutes = fs.readFileSync(path.join(routesDir, 'systemRoutes.js'), 'utf8');
if (!systemRoutes.includes('wslSetup')) {
  fail('/api/status does not expose WSL setup details');
} else {
  pass('/api/status exposes WSL setup details');
}

const routeModules = ['systemRoutes.js', 'driveRoutes.js', 'mountRoutes.js', 'nativeRoutes.js'];
for (const routeFile of routeModules) {
  const fullPath = path.join(routesDir, routeFile);
  try {
    const mountRoutes = require(fullPath);
    if (typeof mountRoutes !== 'function') fail(`${routeFile} does not export a mount function`);
    else pass(`${routeFile} exports a mount function`);
  } catch (e) {
    fail(`${routeFile} cannot be required: ${e.message}`);
  }
}

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

if (updateRoutes.RELEASES_API !== 'https://api.github.com/repos/georgekgr12/GK_Mac_Opener/releases/latest') {
  fail(`updateRoutes.RELEASES_API does not point to GK_Mac_Opener`);
} else {
  pass('updateRoutes.RELEASES_API points to GK_Mac_Opener');
}

if (!updateRoutes.ETAG_FILE.endsWith('github_etag_GK_Mac_Opener.txt')) {
  fail(`updateRoutes.ETAG_FILE must be feed-specific for GK_Mac_Opener (got: ${updateRoutes.ETAG_FILE})`);
} else {
  pass('updateRoutes.ETAG_FILE is feed-specific');
}

try {
  const express = require('express');
  const app = express();
  app.use(express.json());
  const noop = () => {};
  const routeCtx = {
    addLog: noop,
    logs: [],
    setupState: { status: 'ready', message: 'test', ready: true, wslSetup: {} },
    getNativeStatus: async () => ({ available: false }),
    RUNTIME_MOUNT_MODE: 'wsl_kernel',
    RUNTIME_NATIVE_MOUNT_ENABLED: true,
    RUNTIME_CANARY_PERCENT: 100,
    RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK: true,
    PREFER_SUBST_LOCAL_FAST_PATH: true,
    isAdmin: () => true,
    hasRawDiskAccess: () => true,
    PS_PATH: path.join(root, 'scripts', 'MacMount.ps1'),
    MAP_USER_SESSION_PS_PATH: path.join(root, 'scripts', 'map-drive-user-session.ps1'),
    nativeMountState: new Map(),
    inFlightOps: new Set(),
    getBrokerMountedMap: async () => new Map(),
    sendNativeWithBoot: async () => ({ ok: false }),
    cleanupGhostDriveLetters: noop,
    cleanupSingleDriveLetter: noop,
    awaitStartupCleanup: async () => {},
    shouldAttemptNativeMountForDrive: () => false,
    tryMountRawWithFallbackLetters: async () => ({ ok: false }),
    execPsMount: async () => ({ error: 'not available in self-test' }),
    sendBrokerRequest: async () => ({ ok: false }),
    ensureBrokerReady: async () => false,
    getUsedDriveLetters: () => new Set(),
    resolveUserFacingSourcePath: (p) => p,
    runPsJson: async () => ({ success: false })
  };
  for (const routeFile of routeModules) {
    require(path.join(routesDir, routeFile))(app, routeCtx);
  }
  pass('Express route modules register successfully');
} catch (e) {
  fail(`Express route registration failed: ${e.message}`);
}

if (process.exitCode && process.exitCode !== 0) {
  console.error('Self-test failed.');
} else {
  console.log('Self-test passed.');
}
