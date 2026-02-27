const fs = require('fs');
const path = require('path');

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
const auditPath = path.join(root, 'scripts', 'release-audit.ps1');
const secAuditPath = path.join(root, 'scripts', 'security-audit.js');
const gatePath = path.join(root, 'scripts', 'commercial-gate.js');

for (const p of [pkgPath, mainPath, preloadPath, auditPath, secAuditPath, gatePath]) {
  if (!fs.existsSync(p)) fail(`missing file: ${p}`);
  else pass(`exists: ${path.basename(p)}`);
}

const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
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

if (process.exitCode && process.exitCode !== 0) {
  console.error('Self-test failed.');
} else {
  console.log('Self-test passed.');
}
