const { execSync } = require('child_process');

function log(msg) {
  process.stdout.write(`${msg}\n`);
}

function fail(msg) {
  process.stderr.write(`FAIL: ${msg}\n`);
  process.exitCode = 1;
}

const maxAllowedSeverity = (process.env.MAX_AUDIT_SEVERITY || 'moderate').toLowerCase();
const allowAuditFailure = process.env.ALLOW_AUDIT_FAILURE === '1';
const severityOrder = ['info', 'low', 'moderate', 'high', 'critical'];
const maxIndex = severityOrder.indexOf(maxAllowedSeverity);

if (maxIndex === -1) {
  fail(`invalid MAX_AUDIT_SEVERITY: ${maxAllowedSeverity}`);
  process.exit(process.exitCode || 1);
}

let raw = '';
try {
  raw = execSync('npm audit --omit=dev --json', {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  });
} catch (err) {
  raw = (err && err.stdout ? String(err.stdout) : '').trim();
  if (!raw) {
    const message = err && err.message ? err.message : 'npm audit failed without JSON output';
    if (allowAuditFailure) {
      log(`WARN: ${message}`);
      log('WARN: ALLOW_AUDIT_FAILURE=1 set, continuing.');
      process.exit(0);
    }
    fail(message);
    process.exit(process.exitCode || 1);
  }
}

let report;
try {
  report = JSON.parse(raw);
} catch (err) {
  if (allowAuditFailure) {
    log(`WARN: unable to parse npm audit JSON: ${err.message}`);
    log('WARN: ALLOW_AUDIT_FAILURE=1 set, continuing.');
    process.exit(0);
  }
  fail(`unable to parse npm audit JSON: ${err.message}`);
  process.exit(process.exitCode || 1);
}

const vulns = report.metadata && report.metadata.vulnerabilities
  ? report.metadata.vulnerabilities
  : { info: 0, low: 0, moderate: 0, high: 0, critical: 0 };

log('Dependency vulnerability counts:');
for (const s of severityOrder) {
  log(`- ${s}: ${Number(vulns[s] || 0)}`);
}

let worstIndex = 0;
for (let i = 0; i < severityOrder.length; i += 1) {
  const sev = severityOrder[i];
  if (Number(vulns[sev] || 0) > 0) {
    worstIndex = i;
  }
}

if (worstIndex > maxIndex) {
  const worst = severityOrder[worstIndex];
  fail(`severity threshold exceeded. worst=${worst}, allowed<=${maxAllowedSeverity}`);
  process.exit(process.exitCode || 1);
}

log(`PASS: dependency audit within threshold (allowed<=${maxAllowedSeverity})`);
