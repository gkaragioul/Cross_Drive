const net = require('net');
const path = require('path');
const fs = require('fs');
const { spawn } = require('child_process');

const PIPE_PATH = '\\\\.\\pipe\\macmount.native';
let nativeProcess = null;

function resolveExistingPath(candidates) {
  for (const p of candidates) {
    if (!p) continue;
    try {
      if (fs.existsSync(p)) return p;
    } catch {}
  }
  return null;
}

function getNativeServiceExecutable() {
  const candidates = [
    process.resourcesPath ? path.join(process.resourcesPath, 'native-bin', 'MacMount.NativeService.exe') : null,
    process.resourcesPath ? path.join(process.resourcesPath, 'native-bin', 'service', 'MacMount.NativeService.exe') : null,
    path.join(__dirname, '..', 'native', 'bin', 'MacMount.NativeService.exe'),
    path.join(__dirname, '..', 'native', 'bin', 'service', 'MacMount.NativeService.exe'),
  ];
  return resolveExistingPath(candidates);
}

function startNativeService() {
  if (nativeProcess && !nativeProcess.killed) return;

  const exe = getNativeServiceExecutable();
  if (exe) {
    nativeProcess = spawn(exe, [], {
      cwd: path.dirname(exe),
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe']
    });
  } else {
    const projectPath = path.join(__dirname, '..', 'native', 'MacMount.NativeService', 'MacMount.NativeService.csproj');
    nativeProcess = spawn('dotnet', ['run', '--project', projectPath], {
      cwd: path.join(__dirname, '..'),
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe']
    });
  }

  nativeProcess.stdout.on('data', () => {});
  nativeProcess.stderr.on('data', () => {});
  nativeProcess.on('exit', () => {
    nativeProcess = null;
  });
}

function stopNativeService() {
  if (nativeProcess && !nativeProcess.killed) {
    nativeProcess.kill();
    nativeProcess = null;
  }
}

function sendNativeRequest(payload, timeoutMs = 3000) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(PIPE_PATH);
    let buffer = '';
    let settled = false;

    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      socket.destroy();
      reject(new Error('native service timeout'));
    }, timeoutMs);

    socket.on('connect', () => {
      socket.write(JSON.stringify(payload) + '\n');
    });

    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8');
      if (buffer.includes('\n')) {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        socket.destroy();
        const line = buffer.split('\n')[0].trim();
        try {
          resolve(JSON.parse(line));
        } catch (e) {
          reject(new Error('invalid native response'));
        }
      }
    });

    socket.on('error', (err) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(err);
    });

    socket.on('end', () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(new Error('native service disconnected'));
    });
  });
}

async function getNativeStatus() {
  try {
    const res = await sendNativeRequest({ action: 'status', requestId: String(Date.now()) }, 2000);
    return { available: true, ...res };
  } catch {
    return { available: false, ok: false, error: 'native service unavailable' };
  }
}

module.exports = {
  startNativeService,
  stopNativeService,
  sendNativeRequest,
  getNativeStatus,
};
