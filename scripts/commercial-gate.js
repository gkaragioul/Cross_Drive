const fs = require('fs');
const path = require('path');

function pass(msg) {
  process.stdout.write(`PASS: ${msg}\n`);
}

function fail(msg) {
  process.stderr.write(`FAIL: ${msg}\n`);
  process.exitCode = 1;
}

const root = path.resolve(__dirname, '..');
const pkgPath = path.join(root, 'package.json');
const requiredDocs = [
  path.join(root, 'docs', 'GO_NO_GO.md'),
  path.join(root, 'docs', 'COMMERCIAL_READINESS.md'),
  path.join(root, 'docs', 'RISK_REGISTER.md'),
  path.join(root, 'docs', 'SUPPORT_RUNBOOK.md'),
];

for (const p of requiredDocs) {
  if (fs.existsSync(p)) pass(`exists: ${path.relative(root, p)}`);
  else fail(`missing required doc: ${path.relative(root, p)}`);
}

if (!fs.existsSync(pkgPath)) {
  fail('missing package.json');
  process.exit(process.exitCode || 1);
}

const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
const scripts = pkg.scripts || {};
for (const name of ['test', 'security:audit', 'release:prep', 'release:audit']) {
  if (scripts[name]) pass(`script defined: ${name}`);
  else fail(`missing script: ${name}`);
}

const targets = (((pkg.build || {}).win || {}).target || []).map(String);
if (targets.includes('nsis')) pass('windows target includes nsis installer');
else fail('windows target missing nsis installer');

if (targets.includes('portable')) pass('windows target includes portable build');
else fail('windows target missing portable build');

if (process.exitCode && process.exitCode !== 0) {
  process.stderr.write('Commercial gate failed.\n');
  process.exit(process.exitCode);
}

process.stdout.write('Commercial gate passed.\n');
