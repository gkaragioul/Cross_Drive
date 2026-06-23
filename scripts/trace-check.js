const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');

function textFromCodes(codes) {
  return String.fromCharCode(...codes);
}

const forbiddenTerms = [
  [77, 97, 99, 68, 114, 105, 118, 101],
  [109, 97, 99, 100, 114, 105, 118, 101],
  [77, 97, 99, 32, 68, 114, 105, 118, 101],
  [77, 97, 99, 77, 111, 117, 110, 116],
  [109, 97, 99, 109, 111, 117, 110, 116],
  [77, 65, 67, 77, 79, 85, 78, 84],
  [77, 121, 77, 97, 99, 68, 114, 105, 118, 101],
  [71, 75, 77, 97, 99, 79, 112, 101, 110, 101, 114],
  [103, 107, 109, 97, 99, 111, 112, 101, 110, 101, 114],
  [77, 101, 100, 105, 97, 102, 111, 117, 114],
  [79, 87, 67],
].map(textFromCodes);

const ignoredDirectories = new Set([
  '.git',
  'node_modules',
  'dist',
  'build',
  'bin',
  'obj',
  '.codegraph',
]);

const ignoredFiles = new Set([
  'package-lock.json',
]);

const binaryExtensions = new Set([
  '.cab',
  '.dll',
  '.exe',
  '.ico',
  '.jpg',
  '.kernel',
  '.msi',
  '.png',
  '.pfx',
  '.sys',
]);

function shouldSkipFile(filePath) {
  const base = path.basename(filePath);
  if (ignoredFiles.has(base)) return true;
  return binaryExtensions.has(path.extname(base).toLowerCase());
}

function walk(dir, files = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (ignoredDirectories.has(entry.name)) continue;
      walk(path.join(dir, entry.name), files);
      continue;
    }
    if (!entry.isFile()) continue;
    const filePath = path.join(dir, entry.name);
    if (!shouldSkipFile(filePath)) files.push(filePath);
  }
  return files;
}

const findings = [];
for (const filePath of walk(root)) {
  const rel = path.relative(root, filePath).replace(/\\/g, '/');
  let text;
  try {
    text = fs.readFileSync(filePath, 'utf8');
  } catch {
    continue;
  }
  if (text.includes('\u0000')) {
    continue;
  }
  const lines = text.split(/\r?\n/);
  for (const term of forbiddenTerms) {
    lines.forEach((line, index) => {
      if (line.includes(term)) {
        findings.push(`${rel}:${index + 1}: forbidden legacy trace`);
      }
    });
  }
}

if (findings.length > 0) {
  console.error('Forbidden legacy traces found:');
  for (const finding of findings.slice(0, 200)) {
    console.error(`- ${finding}`);
  }
  if (findings.length > 200) {
    console.error(`... ${findings.length - 200} more`);
  }
  process.exit(1);
}

console.log('PASS: no forbidden legacy traces found.');
