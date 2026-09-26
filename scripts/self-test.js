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
const unsignedBuildScript = fs.readFileSync(path.join(root, 'scripts', 'build-release-unsigned.ps1'), 'utf8');
const validateReleaseScript = fs.existsSync(validateReleasePath) ? fs.readFileSync(validateReleasePath, 'utf8') : '';
const nativeBrokerClientScript = fs.readFileSync(path.join(root, 'scripts', 'nativeBrokerClient.js'), 'utf8');
const nativeServiceClientScript = fs.readFileSync(path.join(root, 'scripts', 'nativeServiceClient.js'), 'utf8');
const crossDriveScript = fs.readFileSync(path.join(root, 'scripts', 'CrossDrive.ps1'), 'utf8');
const nativeServiceProgram = fs.readFileSync(path.join(root, 'native', 'CrossDrive.NativeService', 'Program.cs'), 'utf8');
const nativeBrokerProgram = fs.readFileSync(path.join(root, 'native', 'CrossDrive.NativeBroker', 'Program.cs'), 'utf8');
const userSessionHelperProgram = fs.readFileSync(path.join(root, 'native', 'CrossDrive.UserSessionHelper', 'Program.cs'), 'utf8');
const rawDiskEngineSource = fs.readFileSync(path.join(root, 'native', 'CrossDrive.RawDiskEngine', 'RawDiskEngine.cs'), 'utf8');
const cachedRawFileSystemProviderSource = fs.readFileSync(path.join(root, 'native', 'CrossDrive.RawDiskEngine', 'CachedRawFileSystemProvider.cs'), 'utf8');
const virtualFsContractsSource = fs.readFileSync(path.join(root, 'native', 'CrossDrive.RawDiskEngine', 'VirtualFsContracts.cs'), 'utf8');
const hfsPlusNativeReaderSource = fs.readFileSync(path.join(root, 'native', 'CrossDrive.RawDiskEngine', 'HfsPlusNativeReader.cs'), 'utf8');
const hfsClassicProviderSource = fs.readFileSync(path.join(root, 'native', 'CrossDrive.RawDiskEngine', 'HfsClassicRawFileSystemProvider.cs'), 'utf8');
const apfsProviderSource = fs.readFileSync(path.join(root, 'native', 'CrossDrive.RawDiskEngine', 'ApfsRawFileSystemProvider.cs'), 'utf8');
const apfsFileOpsTestsSource = fs.readFileSync(path.join(root, 'native', 'CrossDrive.ApfsWriteTest', 'ApfsFileOpsTests.cs'), 'utf8');
const appSource = fs.readFileSync(path.join(root, 'src', 'App.jsx'), 'utf8');
const apiSource = fs.readFileSync(path.join(root, 'src', 'api.js'), 'utf8');
const preloadSource = fs.readFileSync(preloadPath, 'utf8');
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

for (const scriptName of ['fs:test', 'installer:smoke', 'clean-install:smoke', 'production:gate', 'security:audit', 'commercial:gate', 'release:prep', 'release:win:unsigned', 'release:audit', 'signing:verify', 'release:candidate']) {
  if (!pkg.scripts || !pkg.scripts[scriptName]) fail(`package.json missing scripts.${scriptName}`);
  else pass(`package.json has scripts.${scriptName}`);
}

if (!pkg.scripts['fs:test'].includes('apfs:test') || !pkg.scripts['fs:test'].includes('hfs:test')) {
  fail('scripts.fs:test must run both APFS and HFS regression suites');
} else {
  pass('scripts.fs:test runs both APFS and HFS regression suites');
}

if (!pkg.scripts['release:gate'].includes('fs:test')) {
  fail('release:gate must run filesystem regression suites');
} else {
  pass('release:gate runs filesystem regression suites');
}

if (!pkg.scripts['release:gate'].includes('installer:smoke')) {
  fail('release:gate must validate the packaged installer layout');
} else {
  pass('release:gate validates the packaged installer layout');
}

if (!pkg.scripts['release:win:unsigned'].includes('installer:smoke')) {
  fail('release:win:unsigned must validate the packaged installer layout');
} else {
  pass('release:win:unsigned validates the packaged installer layout');
}

if (!pkg.scripts['release:win:signed'].includes('installer:smoke')) {
  fail('release:win:signed must validate the packaged installer layout');
} else {
  pass('release:win:signed validates the packaged installer layout');
}

if (!fs.existsSync(path.join(root, 'scripts', 'installer-smoke.ps1'))) {
  fail('installer-smoke.ps1 missing');
} else {
  pass('installer-smoke.ps1 exists');
}

if (!fs.existsSync(path.join(root, 'scripts', 'clean-install-smoke.ps1'))) {
  fail('clean-install-smoke.ps1 missing');
} else {
  pass('clean-install-smoke.ps1 exists');
}

const productionGateScriptPath = path.join(root, 'scripts', 'production-release-gate.ps1');
if (!fs.existsSync(productionGateScriptPath)) {
  fail('production-release-gate.ps1 missing');
} else {
  pass('production-release-gate.ps1 exists');
}

const releaseCandidateScript = fs.readFileSync(path.join(root, 'scripts', 'release-candidate.ps1'), 'utf8');
if (!releaseCandidateScript.includes('npm run fs:test') ||
    !releaseCandidateScript.includes('npm run installer:smoke') ||
    !releaseCandidateScript.includes('npm run release:audit') ||
    !releaseCandidateScript.includes('npm run production:gate')) {
  fail('release-candidate must run filesystem tests, installer smoke, strict release audit, and production evidence gate');
} else {
  pass('release-candidate runs filesystem tests, installer smoke, strict release audit, and production evidence gate');
}

const productionGateScript = fs.existsSync(productionGateScriptPath) ? fs.readFileSync(productionGateScriptPath, 'utf8') : '';
if (!productionGateScript.includes('CROSSDRIVE_REAL_MEDIA_EVIDENCE') ||
    !productionGateScript.includes('CROSSDRIVE_CLEAN_INSTALL_EVIDENCE') ||
    !productionGateScript.includes('verify-signing-config.ps1') ||
    !productionGateScript.includes('Get-AuthenticodeSignature') ||
    !productionGateScript.includes('CoreStorage') ||
    !productionGateScript.includes('unsupported') ||
    !productionGateScript.includes('Encrypted APFS') ||
    !productionGateScript.includes('clean-windows-install')) {
  fail('production gate must require signing, clean-install evidence, and real-media coverage including CoreStorage unsupported policy');
} else {
  pass('production gate requires signing, clean-install evidence, and real-media coverage');
}

if (!(pkg.build && Array.isArray(pkg.build.files) && pkg.build.files.includes('preload.js'))) {
  fail('electron build files missing preload.js');
} else {
  pass('electron build includes preload.js');
}
if (pkg.build.afterPack !== 'scripts/after-pack-icon.js' || !fs.existsSync(path.join(root, 'scripts', 'after-pack-icon.js'))) {
  fail('electron build must stamp the Windows exe icon in unsigned release builds');
} else {
  pass('electron build stamps the Windows exe icon in unsigned release builds');
}
if (pkg.build.files.includes('dist/**/*')) {
  fail('electron build files must not include broad dist/**/* because release output lives under dist');
} else {
  pass('electron build avoids broad dist glob');
}
if (!pkg.build.files.includes('dist/renderer/**/*')) {
  fail('electron build files must include dist/renderer/**/*');
} else {
  pass('electron build includes renderer output only');
}
if (pkg.build.files.includes('native/bin/**/*')) {
  fail('electron build files must not pack native/bin into app.asar; use extraResources/native-bin only');
} else {
  pass('electron build does not pack native/bin into app.asar');
}
if (Array.isArray(pkg.build.asarUnpack) && pkg.build.asarUnpack.includes('native/bin/**/*')) {
  fail('electron build asarUnpack must not duplicate native/bin; use extraResources/native-bin only');
} else {
  pass('electron build does not duplicate native/bin in app.asar.unpacked');
}
const nativeBinResource = Array.isArray(pkg.build.extraResources)
  ? pkg.build.extraResources.find(r => r && r.from === 'native/bin' && r.to === 'native-bin')
  : null;
if (!nativeBinResource) {
  fail('electron build must package native/bin as extraResources/native-bin');
} else {
  pass('electron build packages native/bin as native-bin resource');
}

const serializedBuildConfig = JSON.stringify(pkg.build || {});
for (const forbiddenRuntime of [
  'wslMountClient.js',
  'wslSetup.js',
  'wsl_mount.sh',
  'wsl_unmount.sh',
  'wsl_install_modules.sh',
  'wsl_validate_mount.sh',
  'wsl_format_and_mount.sh',
  'wsl_mount_test.sh',
  'wsl_test_mount.sh',
  'mount_drive.sh',
  'setup_linux.sh',
  'crossdrive-kernel',
  'native-bridge-bin',
  'apfs-fuse.exe'
]) {
  if (serializedBuildConfig.includes(forbiddenRuntime)) {
    fail(`electron build must not package external-runtime component: ${forbiddenRuntime}`);
  } else {
    pass(`electron build excludes ${forbiddenRuntime}`);
  }
}

for (const removedSource of [
  'scripts/wslMountClient.js',
  'scripts/wsl_mount.sh',
  'scripts/wsl_unmount.sh',
  'scripts/wsl_validate_mount.sh',
  'scripts/wsl_format_and_mount.sh',
  'scripts/wsl_mount_test.sh',
  'scripts/wsl_test_mount.sh',
  'scripts/mount_drive.sh',
  'scripts/setup_linux.sh',
  'scripts/build-apfs-fuse.ps1'
]) {
  const fullPath = path.join(root, ...removedSource.split('/'));
  if (fs.existsSync(fullPath)) {
    fail(`removed external-runtime source remains: ${removedSource}`);
  } else {
    pass(`removed external-runtime source absent: ${removedSource}`);
  }
}

// The v1.5.35 release shipped these GPL-covered binaries and scripts. They stay in
// the repository as part of its GPL source correspondence (see
// docs/gpl-source/v1.5.35), but the native-only build must not package them.
for (const gplRecordSource of [
  'scripts/wslSetup.js',
  'scripts/wsl_install_modules.sh',
  'prereqs/crossdrive-kernel/wsl_kernel',
  'prereqs/crossdrive-kernel/modules/apfs.ko',
  'prereqs/crossdrive-kernel/modules/hfs.ko',
  'prereqs/crossdrive-kernel/modules/hfsplus.ko'
]) {
  const fullPath = path.join(root, ...gplRecordSource.split('/'));
  const fileName = gplRecordSource.split('/').pop();
  if (!fs.existsSync(fullPath)) {
    fail(`v1.5.35 GPL source record is missing: ${gplRecordSource}`);
  } else if (serializedBuildConfig.includes(fileName) || serializedBuildConfig.includes('crossdrive-kernel')) {
    fail(`v1.5.35 GPL record file must not be packaged by the native build: ${gplRecordSource}`);
  } else {
    pass(`v1.5.35 GPL record kept and not packaged: ${gplRecordSource}`);
  }
}

const winTargets = (((pkg.build || {}).win || {}).target || []).map(String);
if (!winTargets.includes('nsis')) fail('package.json build.win.target missing nsis');
else pass('win target includes nsis');

if (!winTargets.includes('portable')) fail('package.json build.win.target missing portable');
else pass('win target includes portable');

const nsisCfg = (pkg.build && pkg.build.nsis) || {};
if (nsisCfg.guid !== 'com.crossdrive.app') fail('nsis.guid must use the clean CrossDrive installer identity');
else pass('nsis.guid uses the clean CrossDrive installer identity');

if (nsisCfg.oneClick !== false) fail('nsis.oneClick must be false (assisted wizard with EULA gate)');
else pass('nsis.oneClick is false');

if (nsisCfg.allowToChangeInstallationDirectory !== false) fail('nsis.allowToChangeInstallationDirectory must be false (locked install path for updates)');
else pass('nsis.allowToChangeInstallationDirectory is false');

if (nsisCfg.artifactName !== 'CrossDriveSetup.exe') fail(`nsis.artifactName must be 'CrossDriveSetup.exe', found '${nsisCfg.artifactName}'`);
else pass('nsis.artifactName is CrossDriveSetup.exe');

const installerNsh = fs.readFileSync(path.join(root, 'build', 'installer.nsh'), 'utf8');
if (!installerNsh.includes('resources\\icon.ico') || !installerNsh.includes('CreateShortCut "$DESKTOP\\CrossDrive.lnk"')) {
  fail('installer must explicitly set CrossDrive shortcut icons from resources\\icon.ico');
} else {
  pass('installer explicitly sets CrossDrive shortcut icons');
}

if (!installerNsh.includes('IfFileExists "$PROGRAMFILES32\\WinFsp\\bin\\launchctl-x64.exe"') ||
    !installerNsh.includes('Skipping bundled WinFsp runtime; already installed') ||
    installerNsh.indexOf('IfFileExists "$PROGRAMFILES32\\WinFsp\\bin\\launchctl-x64.exe"') > installerNsh.indexOf('ExecWait \'msiexec /i "$INSTDIR\\resources\\prereqs\\winfsp.msi"')) {
  fail('installer must skip the bundled WinFsp MSI when WinFsp is already installed');
} else {
  pass('installer skips bundled WinFsp MSI when WinFsp is already installed');
}

if (nsisCfg.license !== 'build/EULA.txt') fail(`nsis.license must be 'build/EULA.txt', found '${nsisCfg.license}'`);
else pass('nsis.license points to build/EULA.txt');

const portableCfg = (pkg.build && pkg.build.portable) || {};
if (portableCfg.artifactName !== 'CrossDrive-${version}.exe') fail(`portable.artifactName must be 'CrossDrive-\${version}.exe', found '${portableCfg.artifactName}'`);
else pass('portable.artifactName is versioned');

const mainJs = fs.readFileSync(mainPath, 'utf8');
const serverSource = fs.readFileSync(serverPath, 'utf8');
const mountRoutesSource = fs.readFileSync(path.join(routesDir, 'mountRoutes.js'), 'utf8');
const nativeRoutesSource = fs.readFileSync(path.join(routesDir, 'nativeRoutes.js'), 'utf8');
if (!mainJs.includes('contextIsolation: true')) fail('main.js missing contextIsolation: true');
else pass('contextIsolation enabled');

if (!mainJs.includes("'dist', 'renderer', 'index.html'")) fail('main.js does not load packaged renderer from dist/renderer');
else pass('main.js loads renderer from dist/renderer');

if (!mainJs.includes('nodeIntegration: false')) fail('main.js missing nodeIntegration: false');
else pass('nodeIntegration disabled');

if (!mainJs.includes('sandbox: true')) fail('main.js missing sandbox: true');
else pass('sandbox enabled');

if (!mainJs.includes("const APP_NAME = 'CrossDrive'")) fail('main.js APP_NAME must be CrossDrive');
else pass('main.js APP_NAME is CrossDrive');

if (!appSource.includes('<h2>CrossDrive</h2>') || !appSource.includes('value="CrossDrive"')) {
  fail('App.jsx must show CrossDrive in sidebar and About settings');
} else {
  pass('App.jsx shows CrossDrive in the UI');
}

if (!mainJs.includes("ipcMain.handle('open-github-releases'") || !preloadSource.includes('openGitHubReleases')) {
  fail('main/preload must expose a safe GitHub Releases opener');
} else {
  pass('main/preload expose GitHub Releases opener');
}

if (!appSource.includes('GitHub Releases') || !appSource.includes('openGitHubReleases')) {
  fail('App.jsx must direct users to GitHub Releases for updates');
} else {
  pass('App.jsx directs users to GitHub Releases for updates');
}

for (const forbidden of ['checkForUpdate', 'UpdateBanner', 'UpdateModal', 'update-check-notice', 'show-update-status-notification', 'quit-for-update', '/api/update']) {
  const haystack = `${appSource}\n${mainJs}\n${preloadSource}\n${serverSource}`;
  if (haystack.includes(forbidden)) {
    fail(`assisted updater reference remains: ${forbidden}`);
  } else {
    pass(`assisted updater reference removed: ${forbidden}`);
  }
}

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

const staleRuntimeWording = [
  ['main.js', mainJs],
  ['App.jsx', appSource],
  ['systemRoutes.js', fs.readFileSync(path.join(routesDir, 'systemRoutes.js'), 'utf8')],
  ['CrossDrive.ps1', crossDriveScript]
].flatMap(([name, text]) => [
  ['kernel', '+ modules'].join(' '),
  ['Setting up Mac', 'drivers'].join(' '),
  ['Driver repair', 'endpoint'].join(' '),
  ['native bridge', 'helpers'].join(' '),
  ['Legacy setup', 'bootstrap'].join(' '),
  ['Legacy driver', 'repair'].join(' ')
].filter(phrase => text.includes(phrase)).map(phrase => `${name}: ${phrase}`));

if (staleRuntimeWording.length > 0) {
  fail(`stale external-runtime wording remains: ${staleRuntimeWording.join(', ')}`);
} else {
  pass('user-facing runtime wording avoids retired prerequisite labels');
}

if (!nativeBrokerProgram.includes('Global\\\\CrossDrive.NativeBroker') || !nativeBrokerProgram.includes('exiting duplicate process')) {
  fail('NativeBroker missing single-instance guard');
} else {
  pass('NativeBroker has single-instance guard');
}

if (!nativeServiceProgram.includes('TryUnlockEncryptedApfsAsync') ||
    !nativeServiceProgram.includes('ApfsKeyManager') ||
    !nativeServiceProgram.includes('EncryptionKey = encryptionKey') ||
    !nativeServiceProgram.includes('HardwareBound') ||
    nativeServiceProgram.includes(['Native raw provider', 'cannot unlock encrypted APFS volumes', 'yet'].join(' ')) ||
    /bridge\s+fallback/i.test(nativeServiceProgram)) {
  fail('NativeService must attempt native encrypted APFS unlock without external fallback messaging');
} else {
  pass('NativeService attempts native encrypted APFS unlock without external fallback messaging');
}

if (nativeServiceProgram.includes(['Install WinFsp', 'first'].join(' ')) ||
    nativeServiceProgram.includes(['Install/repair WinFsp', 'runtime'].join(' ')) ||
    !nativeServiceProgram.includes('CrossDrive runtime is not ready') ||
    !nativeServiceProgram.includes('CrossDrive installer repair option')) {
  fail('NativeService missing-runtime errors must direct users to CrossDrive runtime repair, not manual prerequisites');
} else {
  pass('NativeService missing-runtime errors direct users to CrossDrive runtime repair');
}

if (!nativeBrokerClientScript.includes('brokerStartPromise') || !nativeBrokerClientScript.includes('concurrent startup/runtime probes')) {
  fail('nativeBrokerClient missing broker start coalescing');
} else {
  pass('nativeBrokerClient coalesces broker starts');
}

if (!nativeServiceClientScript.includes('isPackagedRuntime') ||
    !nativeServiceClientScript.includes("nativeProcess.on('error'") ||
    !nativeBrokerClientScript.includes('CrossDrive.NativeBroker.exe is missing from native-bin/broker')) {
  fail('packaged native helpers must not fall back to dotnet or crash on spawn errors');
} else {
  pass('packaged native helpers avoid dotnet fallback and handle spawn errors');
}

if (!serverSource.includes("if (!raw) return 'native_first'")) {
  fail('server.js must default to the bundled native runtime');
} else {
  pass('server.js defaults to the bundled native runtime');
}

if (!serverSource.includes("const VALID_RUNTIME_MOUNT_MODES = new Set(['native_first', 'native_only'])") ||
    !serverSource.includes("status: 'ready'") ||
    serverSource.includes('wslSetup') ||
    serverSource.includes('ensureWslMountPathReady') ||
    serverSource.includes('wsl_kernel')) {
  fail('server.js must expose only bundled native runtime modes');
} else {
  pass('server.js exposes only bundled native runtime modes');
}

if (crossDriveScript.includes('function Test-WslRuntimeReady') ||
    crossDriveScript.includes('function Convert-WslOutputText') ||
    crossDriveScript.includes('id = "wslRuntime"') ||
    crossDriveScript.includes('id = "ubuntuDistro"') ||
    crossDriveScript.includes('function Install-WslRuntime') ||
    crossDriveScript.includes('wsl.exe') ||
    crossDriveScript.includes('apfs-fuse') ||
    crossDriveScript.includes('native-bridge') ||
    crossDriveScript.includes('CROSSDRIVE_APFS_FUSE_EXE') ||
    /Ubuntu/i.test(crossDriveScript)) {
  fail('CrossDrive.ps1 must not install or require an external runtime');
} else {
  pass('CrossDrive.ps1 avoids external-runtime installation');
}

if (!userSessionHelperProgram.includes('DefineDosDevice') ||
    !userSessionHelperProgram.includes('WNetAddConnection2') ||
    userSessionHelperProgram.includes('wsl.exe') ||
    userSessionHelperProgram.includes('wslmount') ||
    userSessionHelperProgram.includes('wslvalidate') ||
    userSessionHelperProgram.includes('wslkeepalive') ||
    /Ubuntu|RunWslCommand|StartWslKeepAlive/i.test(userSessionHelperProgram)) {
  fail('CrossDrive.UserSessionHelper must expose only native drive-letter mapping actions');
} else {
  pass('CrossDrive.UserSessionHelper exposes only native drive-letter mapping actions');
}

if (!appSource.includes('preflight.message') ||
    !appSource.includes('preflight.rebootRequired') ||
    !appSource.includes('Native Runtime') ||
    appSource.includes('WSL') ||
    appSource.includes('Ubuntu') ||
    appSource.includes('Auto-Install') ||
    appSource.includes('Prerequisites Missing')) {
  fail('App.jsx preflight card must present native runtime repair without external-runtime prompts');
} else {
  pass('App.jsx preflight card presents native runtime repair only');
}

if (mountRoutesSource.includes('wslMountClient') ||
    mountRoutesSource.includes('attemptApfsWslFallback') ||
    mountRoutesSource.includes('attemptClassicHfsWslFallback') ||
    mountRoutesSource.includes('wsl_kernel') ||
    /WSL|Ubuntu|Linux/i.test(mountRoutesSource)) {
  fail('mountRoutes.js must not contain external-runtime mount paths');
} else {
  pass('mountRoutes.js contains only native mount paths');
}

if (mountRoutesSource.includes('Classic HFS native mounting is not implemented yet') ||
    !mountRoutesSource.includes('Native APFS mount failed')) {
  fail('mountRoutes.js must attempt native classic HFS and report remaining native support gaps without external fallbacks');
} else {
  pass('mountRoutes.js attempts native classic HFS and reports remaining native support gaps honestly');
}

if (!rawDiskEngineSource.includes('HfsClassicRawFileSystemProvider.CreateAsync') ||
    !hfsClassicProviderSource.includes('Classic HFS') ||
    !hfsClassicProviderSource.includes('ReadFile') ||
    !nativeBrokerProgram.includes('string.Equals(fsType, "HFS", StringComparison.OrdinalIgnoreCase)')) {
  fail('native engine and broker must wire classic HFS into the read-only raw provider path');
} else {
  pass('native engine and broker wire classic HFS into the read-only raw provider path');
}

const hfsWriteTestsSource = fs.readFileSync(path.join(root, 'native', 'CrossDrive.HfsWriteTest', 'HfsPlusWriteTests.cs'), 'utf8');
if (!hfsClassicProviderSource.includes('LoadExtentsOverflowAsync') ||
    !hfsClassicProviderSource.includes('ExtentsOverflowExtents') ||
    !hfsClassicProviderSource.includes('TryParseOverflowExtentRecord') ||
    !hfsWriteTestsSource.includes('TestMountApmClassicHfsExtentsOverflow')) {
  fail('classic HFS provider must read data-fork extents-overflow records and keep a regression test for fragmented files');
} else {
  pass('classic HFS provider reads data-fork extents-overflow records with regression coverage');
}

if (!hfsClassicProviderSource.includes('BuildAppleDoubleHeader') ||
    !hfsClassicProviderSource.includes('_appleDoubleByPath') ||
    !hfsWriteTestsSource.includes('TestMountApmClassicHfsResourceForkAppleDouble') ||
    !hfsWriteTestsSource.includes('TestMountApmClassicHfsResourceForkExtentsOverflow')) {
  fail('classic HFS provider must expose resource forks, including extents-overflow continuation, as AppleDouble sidecars with regression coverage');
} else {
  pass('classic HFS provider exposes resource forks as AppleDouble sidecars with inline and overflow regression coverage');
}

if (!hfsClassicProviderSource.includes('AppleDoubleFinderInfoEntryId = 9') ||
    !hfsClassicProviderSource.includes('ReadClassicFinderInfo') ||
    !hfsWriteTestsSource.includes('TestMountApmClassicHfsFinderInfoAppleDouble')) {
  fail('classic HFS provider must preserve Finder Info as AppleDouble entry 9 with regression coverage');
} else {
  pass('classic HFS provider preserves Finder Info as AppleDouble entry 9 with regression coverage');
}

if (!hfsPlusNativeReaderSource.includes('dataOffset + 168') ||
    !hfsPlusNativeReaderSource.includes('ResourceFork') ||
    !rawDiskEngineSource.includes('_resourceForkSidecars') ||
    !rawDiskEngineSource.includes('BuildAppleDoubleResourceForkHeader') ||
    !rawDiskEngineSource.includes('results.Add(sidecarEntry)') ||
    !rawDiskEngineSource.includes('0xFF') ||
    !hfsWriteTestsSource.includes('TestMountHfsPlusResourceForkAppleDouble') ||
    !hfsWriteTestsSource.includes('Expected listed AppleDouble sidecar ._Forked.txt') ||
    !hfsWriteTestsSource.includes('TestMountHfsPlusResourceForkExtentsOverflow')) {
  fail('HFS+ provider must expose resource forks, including extents-overflow continuation, as listed read-only AppleDouble sidecars with regression coverage');
} else {
  pass('HFS+ provider exposes resource forks as listed read-only AppleDouble sidecars with inline and overflow regression coverage');
}

if (!hfsPlusNativeReaderSource.includes('dataOffset + 48') ||
    !rawDiskEngineSource.includes('AppleDoubleFinderInfoEntryId = 9') ||
    !rawDiskEngineSource.includes('FinderInfoOffset') ||
    !hfsWriteTestsSource.includes('TestMountHfsPlusFinderInfoAppleDouble')) {
  fail('HFS+ provider must preserve Finder Info as AppleDouble entry 9 with regression coverage');
} else {
  pass('HFS+ provider preserves Finder Info as AppleDouble entry 9 with regression coverage');
}

if (!hfsPlusNativeReaderSource.includes('ReadCatalogMode') ||
    !hfsPlusNativeReaderSource.includes('IsSymbolicLink') ||
    !rawDiskEngineSource.includes('TryReadHfsPlusSymlinkTarget') ||
    !rawDiskEngineSource.includes('FileAttributes.ReparsePoint') ||
    !hfsWriteTestsSource.includes('TestMountHfsPlusSymlinkReparsePoint')) {
  fail('HFS+ symlinks must preserve targets and expose WinFsp reparse-point metadata with regression coverage');
} else {
  pass('HFS+ symlinks preserve targets and expose WinFsp reparse-point metadata with regression coverage');
}

if (!hfsPlusNativeReaderSource.includes('0x4858') ||
    !rawDiskEngineSource.includes('string.Equals(plan.FileSystemType, "HFSX"') ||
    !hfsWriteTestsSource.includes('TestMountHfsxReadOnlyBrowsing') ||
    !hfsWriteTestsSource.includes('PatchHfsxSignatureAsync')) {
  fail('HFSX volumes must be analyzed and browsed through the native HFS provider with regression coverage');
} else {
  pass('HFSX volumes are analyzed and browsed through the native HFS provider with regression coverage');
}

if (!hfsPlusNativeReaderSource.includes('_catalogNameComparison = header.IsHfsx ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase') ||
    !hfsPlusNativeReaderSource.includes('private int CompareCatalogKeys') ||
    !hfsPlusNativeReaderSource.includes('string.Compare(recName, targetName, _catalogNameComparison)') ||
    !hfsWriteTestsSource.includes('TestHfsxCatalogIndexCompareIsCaseSensitive')) {
  fail('HFSX catalog index comparison must be case-sensitive with regression coverage');
} else {
  pass('HFSX catalog index comparison is case-sensitive with regression coverage');
}

if (!cachedRawFileSystemProviderSource.includes('IsCaseSensitiveFileSystem') ||
    !cachedRawFileSystemProviderSource.includes('_pathComparer') ||
    !cachedRawFileSystemProviderSource.includes('_pathComparison') ||
    !rawDiskEngineSource.includes('new Dictionary<string, RawFsEntry>(_pathComparer)') ||
    !rawDiskEngineSource.includes('key.StartsWith(prefix, _pathComparison)') ||
    !nativeServiceProgram.includes('CaseSensitiveSearch = caseSensitiveSearch') ||
    !nativeServiceProgram.includes('IsCaseSensitiveFileSystem(plan.FileSystemType)') ||
    !nativeBrokerProgram.includes('var caseSensitiveSearch = IsCaseSensitiveFileSystem(plan.FileSystemType)') ||
    !nativeBrokerProgram.includes('CaseSensitiveSearch = caseSensitiveSearch') ||
    !hfsWriteTestsSource.includes('TestHfsxCaseSensitiveCacheKeepsDistinctNames')) {
  fail('HFSX provider/cache and WinFsp host paths must remain case-sensitive with regression coverage');
} else {
  pass('HFSX provider/cache and WinFsp host paths remain case-sensitive with regression coverage');
}

if (!hfsClassicProviderSource.includes('MacRomanHighChars') ||
    !hfsWriteTestsSource.includes('TestMountApmClassicHfsMacRomanFilename')) {
  fail('classic HFS provider must decode MacRoman filenames with regression coverage');
} else {
  pass('classic HFS provider decodes MacRoman filenames with regression coverage');
}

if (!hfsClassicProviderSource.includes('visited.Add(current)') ||
    !hfsClassicProviderSource.includes('BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(0, 4))') ||
    !hfsWriteTestsSource.includes('TestMountApmClassicHfsCatalogLeafChain')) {
  fail('classic HFS provider must follow linked catalog leaf nodes with regression coverage');
} else {
  pass('classic HFS provider follows linked catalog leaf nodes with regression coverage');
}

if (!appSource.includes('const environmentReady = setup.ready !== false') ||
    !appSource.includes('showSetupBanner')) {
  fail('App.jsx must keep native mounting available without optional runtime setup state');
} else {
  pass('App.jsx keeps native mounting available without optional runtime setup state');
}

if (!apfsProviderSource.includes('ReadUInt32LittleEndian(buffer.AsSpan(104, 4))') ||
    !apfsProviderSource.includes('ReadUInt32LittleEndian(buffer.AsSpan(108, 4))') ||
    !apfsProviderSource.includes('ReadUInt64LittleEndian(buffer.AsSpan(120, 8))')) {
  fail('APFS NX checkpoint fields must use the correct offsets');
} else {
  pass('APFS NX checkpoint fields use the correct offsets');
}

if (!apfsProviderSource.includes('MaxCheckpointScanBlocks = 65536') ||
    !apfsProviderSource.includes('nx.CheckpointDataBlocks, (ulong)MaxCheckpointScanBlocks')) {
  fail('APFS checkpoint data scan must cover large Time Machine checkpoint windows');
} else {
  pass('APFS checkpoint data scan covers large Time Machine checkpoint windows');
}

if (apfsProviderSource.includes('if (plan.Writable && summary.SpacemanPhysicalBlock.HasValue)')) {
  fail('APFS free-space metadata must be loaded for read-only mounts too');
} else if (!apfsProviderSource.includes('FreeBytes = spaceman is not null') ||
           !apfsProviderSource.includes(': TotalBytes')) {
  fail('APFS provider must avoid reporting zero free space when spaceman metadata is unavailable');
} else {
  pass('APFS provider reports non-zero capacity for normal read-only mounts');
}

if (!apfsProviderSource.includes('SelectPrimaryVolumePreview') ||
    !apfsProviderSource.includes('PopulateVolumeCatalog("\\\\", primaryVolume, now)') ||
    !apfsProviderSource.includes('JoinPath(currentPath, item.Name)')) {
  fail('APFS provider must expose the primary user volume at the drive root');
} else {
  pass('APFS provider exposes the primary user volume at the drive root');
}

const compressedReadCheck = apfsProviderSource.indexOf('if (readPlan.IsCompressed)');
const inlineReadShortcut = apfsProviderSource.indexOf('if (readPlan.InlineData is not null)');
if (compressedReadCheck < 0 || inlineReadShortcut < 0 || compressedReadCheck > inlineReadShortcut) {
  fail('APFS compressed inline decmpfs reads must be decompressed before the generic inline-data read path');
} else {
  pass('APFS compressed inline decmpfs reads are handled before generic inline data');
}

if (!apfsProviderSource.includes('public const int HeaderLength = 16') ||
    !apfsProviderSource.includes('ApfsDecmpfs.GetInlineDataLogicalSize') ||
    !apfsFileOpsTestsSource.includes('APFS decmpfs zlib inline reports uncompressed size and decompresses bytes') ||
    !apfsFileOpsTestsSource.includes('APFS decmpfs uncompressed inline pads short resident payload to logical size')) {
  fail('APFS decmpfs inline files must use the 16-byte header and expose uncompressed logical size with regression coverage');
} else {
  pass('APFS decmpfs inline files use the 16-byte header and expose uncompressed logical size with padded type-1 coverage');
}

if (!apfsProviderSource.includes('com.apple.decmpfs') ||
    !apfsProviderSource.includes('CompressionResourceFork') ||
	    !apfsProviderSource.includes('TryDecompressResourceForkDecmpfs') ||
	    !apfsProviderSource.includes('TryDecompressResourceForkDecmpfsRange') ||
	    !apfsProviderSource.includes('Func<long, int, byte[]?> readResourceForkRange') ||
	    !apfsProviderSource.includes('TryLocateCmpfResourceData') ||
	    !apfsProviderSource.includes('TryReadPlanRange') ||
	    !apfsProviderSource.includes('MaxFullDecompressionSize') ||
	    !apfsProviderSource.includes('TryExtractCmpfResourceData') ||
	    !apfsFileOpsTestsSource.includes('APFS decmpfs zlib resource-fork cmpf chunks decompress bytes') ||
	    !apfsFileOpsTestsSource.includes('APFS decmpfs zlib resource-fork cmpf range reads cross chunk boundaries') ||
	    !apfsFileOpsTestsSource.includes('APFS decmpfs zlib resource-fork cmpf streamed range reads only intersecting chunks')) {
  fail('APFS decmpfs type-4 resource-fork compression must preserve metadata, retain the raw resource fork, decode cmpf chunks, and serve partial ranges without full-file allocation');
} else {
  pass('APFS decmpfs type-4 resource-fork compression preserves metadata and decodes full/ranged cmpf chunks with regression coverage');
}

if (!apfsProviderSource.includes('com.apple.ResourceFork') ||
    !apfsProviderSource.includes('BuildResourceForkSidecar') ||
    !apfsProviderSource.includes('ResourceForkAppleDoubleByObjectId') ||
    !apfsFileOpsTestsSource.includes('APFS ResourceFork xattr payload is converted to AppleDouble sidecar bytes')) {
  fail('APFS inline resource forks must be exposed as AppleDouble sidecars with regression coverage');
} else {
  pass('APFS inline resource forks are exposed as AppleDouble sidecars with regression coverage');
}

if (!apfsProviderSource.includes('BuildResourceForkSidecarReadPlan') ||
    !apfsProviderSource.includes('ReadInlinePrefixedExtentBackedFile') ||
    !apfsFileOpsTestsSource.includes('APFS extent-backed ResourceFork sidecars stream AppleDouble header and payload')) {
  fail('APFS extent-backed resource forks must stream AppleDouble sidecars with regression coverage');
} else {
  pass('APFS extent-backed resource forks stream AppleDouble sidecars with regression coverage');
}

if (!apfsProviderSource.includes('com.apple.FinderInfo') ||
    !apfsProviderSource.includes('AppleDoubleFinderInfoEntryId = 9') ||
    !apfsProviderSource.includes('BuildAppleDoubleSidecarReadPlan') ||
    !apfsProviderSource.includes('AppleDoubleAttrMagic = 0x41545452') ||
    !apfsProviderSource.includes('IsPreservableExtendedAttributeName') ||
    !apfsFileOpsTestsSource.includes('APFS FinderInfo xattr is preserved as AppleDouble entry 9') ||
    !apfsFileOpsTestsSource.includes('APFS extent-backed ResourceFork sidecars shift after FinderInfo entry') ||
    !apfsFileOpsTestsSource.includes('APFS inline xattrs are packed into FinderInfo AppleDouble ATTR data')) {
  fail('APFS FinderInfo and inline xattrs must be preserved as AppleDouble entry 9/ATTR data with inline and extent-backed regression coverage');
} else {
  pass('APFS FinderInfo and inline xattrs are preserved as AppleDouble entry 9/ATTR data with inline and extent-backed regression coverage');
}

if (!virtualFsContractsSource.includes('SymlinkTarget') ||
    !virtualFsContractsSource.includes('IsSymbolicLink') ||
    !apfsProviderSource.includes('DrecFileType.Symlink') ||
    !apfsProviderSource.includes('TryReadApfsSymlinkTarget') ||
    !nativeServiceProgram.includes('ReparsePoints = fs is RawProviderFileSystem') ||
    !nativeServiceProgram.includes('GetReparsePoint') ||
    !nativeServiceProgram.includes('BuildSymlinkReparseData') ||
    !nativeBrokerProgram.includes('ReparsePoints = true') ||
    !nativeBrokerProgram.includes('GetReparsePointByName') ||
    !nativeBrokerProgram.includes('BrokerRawProviderFileSystem') ||
    !nativeBrokerProgram.includes('BuildSymlinkReparseData') ||
    !apfsFileOpsTestsSource.includes('Raw APFS symlink entries preserve target and reparse attributes')) {
  fail('APFS symlinks must preserve targets and expose WinFsp reparse-point metadata in service and broker raw mounts with regression coverage');
} else {
  pass('APFS symlinks preserve targets and expose WinFsp reparse-point metadata in service and broker raw mounts with regression coverage');
}

if (!apfsProviderSource.includes('ReadExtentBackedFile') ||
    !apfsProviderSource.includes('target.Clear()') ||
    !apfsFileOpsTestsSource.includes('APFS sparse extent reads zero-fill holes and return logical byte count')) {
  fail('APFS extent-backed sparse files must zero-fill holes and return the logical read byte count with regression coverage');
} else {
  pass('APFS extent-backed sparse files zero-fill holes with regression coverage');
}

if (!apfsProviderSource.includes('TryReadApfsInodeLogicalSize') ||
    apfsProviderSource.includes('if (extentsByChildId.ContainsKey(key.ObjectId)) continue') ||
    !apfsProviderSource.includes('GetExtentBackedLogicalSize') ||
    !apfsFileOpsTestsSource.includes('APFS extent read plans preserve inode logical size beyond final extent')) {
  fail('APFS extent-backed read plans must preserve inode union_size for sparse tail holes with regression coverage');
} else {
  pass('APFS extent-backed read plans preserve inode logical size with regression coverage');
}

if (!nativeBrokerProgram.includes('DeletePathWithRetry') || !nativeBrokerProgram.includes('Passthrough delete failed')) {
  fail('NativeBroker passthrough delete cleanup can silently fail');
} else {
  pass('NativeBroker logs and retries passthrough delete cleanup');
}

if (!nativeBrokerProgram.includes('CROSSDRIVE_ENABLE_UNC_METADATA_CACHE') || !nativeBrokerProgram.includes('_enableMetadataCache && _dirCache')) {
  fail('NativeBroker can serve stale passthrough metadata cache');
} else {
  pass('NativeBroker disables passthrough metadata cache by default');
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

for (const forbiddenAuditNeedle of ['Bundled WSL kernel', 'apfs.ko', 'hfs.ko', 'hfsplus.ko']) {
  if (auditScript.includes(forbiddenAuditNeedle)) fail(`release audit must not require external-runtime component: ${forbiddenAuditNeedle}`);
  else pass(`release audit does not require ${forbiddenAuditNeedle}`);
}

if (!licenseText.includes('Copyright (c) 2026 George Karagioules and contributors')) fail('LICENSE copyright line is missing or changed');
else pass('LICENSE copyright is CrossDrive 2026');

// EULA, notices and GPL manifest describe the published v1.5.35 release, which
// bundles the WSL2 kernel and modules, so they keep its GPL wording.
if (!eulaText.includes('CrossDrive is distributed under the MIT License') ||
    !eulaText.includes('WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos')) {
  fail('EULA missing MIT/WinFsp FLOSS notice');
} else {
  pass('EULA includes MIT/WinFsp FLOSS notice');
}

if (!noticesText.includes('WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos') ||
    !noticesText.includes('Custom WSL2 kernel and filesystem modules')) {
  fail('third-party notices missing WinFsp or WSL GPL notices');
} else {
  pass('third-party notices include WinFsp and WSL GPL notices');
}

if (!/linux-msft-wsl-6\.6\.87\.2/.test(gplManifestText) || !/linux-apfs-rw/.test(gplManifestText) ||
    !/0\.3\.20/.test(gplManifestText) || !/kernel `\.config`/.test(gplManifestText)) {
  fail('GPL source manifest missing kernel/APFS source requirements');
} else {
  pass('GPL source manifest documents kernel/APFS source requirements');
}

if (validateReleaseScript.includes('Encrypted APFS volumes will not be unlockable') ||
    validateReleaseScript.includes('apfs-fuse') ||
    validateReleaseScript.includes('Node.js not found') ||
    validateReleaseScript.includes('.NET runtime not found')) {
  fail('validate-release still treats developer or external bridge runtimes as required');
} else {
  pass('validate-release does not require developer runtimes or legacy external helpers');
}

const readinessDocs = [
  path.join(root, 'docs', 'CURRENT_STATUS.md'),
  path.join(root, 'docs', 'COMMERCIAL_READINESS.md'),
  path.join(root, 'docs', 'GO_NO_GO.md')
];
for (const docPath of readinessDocs) {
  const doc = fs.readFileSync(docPath, 'utf8');
  if (/WSL2 kernel|WSL kernel|Ubuntu/i.test(doc)) fail(`${path.basename(docPath)} still documents removed external-runtime architecture`);
  else pass(`${path.basename(docPath)} avoids removed external-runtime architecture`);
  if (!/APFS writes?.*experimental|experimental APFS writes?/i.test(doc)) fail(`${path.basename(docPath)} missing experimental APFS write policy`);
  else pass(`${path.basename(docPath)} documents experimental APFS write policy`);
  if (!/CoreStorage.*unsupported|unsupported.*CoreStorage/i.test(doc)) fail(`${path.basename(docPath)} missing CoreStorage unsupported policy`);
  else pass(`${path.basename(docPath)} documents CoreStorage unsupported policy`);
}

const systemRoutes = fs.readFileSync(path.join(routesDir, 'systemRoutes.js'), 'utf8');
const driveRoutesSource = fs.readFileSync(path.join(routesDir, 'driveRoutes.js'), 'utf8');
if (systemRoutes.includes('wslSetup')) {
  fail('/api/status must not expose external-runtime setup details');
} else {
  pass('/api/status avoids external-runtime setup details');
}

const realMediaFormats = ['APFS', 'Encrypted APFS', 'HFS+', 'Classic HFS', 'CoreStorage'];
if (!systemRoutes.includes('/api/validation/real-media') ||
    !systemRoutes.includes('CrossDrive\', \'Validation') ||
    !systemRoutes.includes('requiredFormats') ||
    !realMediaFormats.every(format => systemRoutes.includes(format))) {
  fail('/api/validation/real-media must create a required-format real-media validation report');
} else {
  pass('/api/validation/real-media creates a required-format real-media validation report');
}

if (!systemRoutes.includes('mountSmoke') ||
    !systemRoutes.includes('/api/mount') ||
    !systemRoutes.includes('/api/unmount') ||
    !systemRoutes.includes("smoke.status = 'opened'") ||
    !systemRoutes.includes("format === 'CoreStorage' ? status === 'unsupported' : status === 'opened'")) {
  fail('/api/validation/real-media must prove supported formats by mounting/opening/unmounting and accept CoreStorage only as unsupported');
} else {
  pass('/api/validation/real-media proves supported formats by opening them and CoreStorage by unsupported policy');
}

const coverageCoreStorageCheck = systemRoutes.indexOf('return \'CoreStorage\'');
const coverageHfsPlusCheck = systemRoutes.indexOf("return 'HFS+'");
if (coverageCoreStorageCheck < 0 || coverageHfsPlusCheck < 0 || coverageCoreStorageCheck > coverageHfsPlusCheck) {
  fail('/api/validation/real-media must classify CoreStorage-style HFS analysis before generic HFS+ coverage');
} else {
  pass('/api/validation/real-media classifies CoreStorage-style HFS analysis before generic HFS+ coverage');
}

if (!systemRoutes.includes("app.post('/api/validation/real-media'") ||
    !systemRoutes.includes('validationPassword') ||
    !systemRoutes.includes('passwordProvided') ||
    systemRoutes.includes('passwordValue') ||
    systemRoutes.includes('validationPassword,') ||
    systemRoutes.includes('validationPassword:')) {
  fail('/api/validation/real-media must accept a temporary validation password without persisting it');
} else {
  pass('/api/validation/real-media accepts a temporary validation password without persisting it');
}

if (!apiSource.includes('generateRealMediaValidation') ||
    !apiSource.includes('/api/validation/real-media')) {
  fail('api.js must expose generateRealMediaValidation');
} else {
  pass('api.js exposes generateRealMediaValidation');
}

if (!appSource.includes('validationStatus') ||
    !appSource.includes('validationPassword') ||
    !appSource.includes('Run Real-Media Validation') ||
    !appSource.includes('Missing evidence') ||
    !appSource.includes('needsPasswordFormats') ||
    !appSource.includes('failedFormats') ||
    !appSource.includes('unsupportedFormats')) {
  fail('App.jsx must expose real-media validation controls and missing-evidence status');
} else {
  pass('App.jsx exposes real-media validation controls and missing-evidence status');
}

if (!appSource.includes('autoPreflightAttempted') ||
    !appSource.includes('hasBlockingPreflightItems') ||
    !appSource.includes('doFixPreflight({ automatic: true })') ||
    appSource.includes('Prerequisites Missing') ||
    appSource.includes('Auto-Install')) {
  fail('App.jsx must automatically repair missing runtime setup instead of requiring a manual prerequisites button');
} else {
  pass('App.jsx automatically repairs missing runtime setup');
}

if (!crossDriveScript.includes('Start-ElevatedPreflightFix') ||
    !crossDriveScript.includes('-Verb RunAs') ||
    !crossDriveScript.includes('elevatedStarted') ||
    !crossDriveScript.includes('Test-IsAdmin')) {
  fail('CrossDrive.ps1 must self-launch elevated runtime setup when Windows requires administrator permission');
} else {
  pass('CrossDrive.ps1 self-launches elevated runtime setup when required');
}

if (!driveRoutesSource.includes('/^HFS$/i.test(fsType)') ||
    !driveRoutesSource.includes('Classic HFS native read-only support') ||
    driveRoutesSource.includes('Classic HFS requires the WSL kernel fallback')) {
  fail('driveRoutes.js must classify classic HFS as native read-only without promising an external fallback');
} else {
  pass('driveRoutes.js classifies classic HFS as native read-only without an external fallback');
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

if (fs.existsSync(path.join(routesDir, 'updateRoutes.js'))) {
  fail('routes/updateRoutes.js must be removed with the assisted updater');
} else {
  pass('routes/updateRoutes.js removed with the assisted updater');
}

try {
  const express = require('express');
  const app = express();
  app.use(express.json());
  const noop = () => {};
  const routeCtx = {
    addLog: noop,
    logs: [],
    setupState: { status: 'ready', message: 'test', ready: true },
    getNativeStatus: async () => ({ available: false }),
    RUNTIME_MOUNT_MODE: 'native_first',
    RUNTIME_NATIVE_MOUNT_ENABLED: true,
    RUNTIME_CANARY_PERCENT: 100,
    PREFER_SUBST_LOCAL_FAST_PATH: true,
    isAdmin: () => true,
    hasRawDiskAccess: () => true,
    PS_PATH: path.join(root, 'scripts', 'CrossDrive.ps1'),
    MAP_USER_SESSION_PS_PATH: path.join(root, 'scripts', 'map-drive-user-session.ps1'),
    nativeMountState: new Map(),
    inFlightOps: new Set(),
    getBrokerMountedMap: async () => new Map(),
    sendNativeWithBoot: async () => ({ ok: false }),
    cleanupGhostDriveLetters: noop,
    cleanupSingleDriveLetter: noop,
    awaitStartupCleanup: async () => {},
    tryMountRawWithFallbackLetters: async () => ({ ok: false }),
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

if (serverSource.includes('RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK') ||
    mountRoutesSource.includes('RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK') ||
    nativeRoutesSource.includes('allowBridgeFallback') ||
    systemRoutes.includes('allowNativeBridgeFallback') ||
    mountRoutesSource.includes('Native mount failed and fallback is disabled') ||
    mountRoutesSource.includes('Fallback is only available for APFS right now') ||
    mountRoutesSource.includes('Native APFS fallback cannot open it yet')) {
  fail('Runtime status and mount errors must not expose retired bridge-fallback paths');
} else {
  pass('Runtime status and mount errors expose only native runtime behavior');
}

if (serverSource.includes('function execPsMount') ||
    serverSource.includes("'-Action', 'Mount'") ||
    serverSource.includes('shouldAttemptNativeMountForDrive') ||
    mountRoutesSource.includes('execPsMount') ||
    mountRoutesSource.includes('shouldAttemptNativeMountForDrive') ||
    crossDriveScript.includes('function Mount-Drive')) {
  fail('/api/mount must not keep retired PowerShell mount branches');
} else {
  pass('/api/mount is native-only without retired PowerShell mount branches');
}

if (process.exitCode && process.exitCode !== 0) {
  console.error('Self-test failed.');
} else {
  console.log('Self-test passed.');
}
