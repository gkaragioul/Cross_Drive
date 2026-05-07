const net = require('net');
const path = require('path');
const fs = require('fs');
const { exec, spawn } = require('child_process');

const PIPE_PATH = '\\\\.\\pipe\\macmount.broker';
const BROKER_TASK = 'MacMountStartUserBroker';
let brokerStartPromise = null;

function resolveExistingPath(candidates) {
  for (const p of candidates) {
    if (!p) continue;
    try {
      if (fs.existsSync(p)) return p;
    } catch {}
  }
  return null;
}

function getBrokerExecutable() {
  const candidates = [
    process.resourcesPath ? path.join(process.resourcesPath, 'native-bin', 'MacMount.NativeBroker.exe') : null,
    process.resourcesPath ? path.join(process.resourcesPath, 'native-bin', 'broker', 'MacMount.NativeBroker.exe') : null,
    process.resourcesPath ? path.join(process.resourcesPath, 'app.asar.unpacked', 'native', 'bin', 'MacMount.NativeBroker.exe') : null,
    process.resourcesPath ? path.join(process.resourcesPath, 'app.asar.unpacked', 'native', 'bin', 'broker', 'MacMount.NativeBroker.exe') : null,
    path.join(__dirname, '..', 'native', 'bin', 'MacMount.NativeBroker.exe'),
    path.join(__dirname, '..', 'native', 'bin', 'broker', 'MacMount.NativeBroker.exe'),
  ];
  return resolveExistingPath(candidates);
}

function sendBrokerRequest(payload, timeoutMs = 5000) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(PIPE_PATH);
    let buffer = '';
    let settled = false;

    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      socket.destroy();
      reject(new Error('broker timeout'));
    }, timeoutMs);

    socket.on('connect', () => {
      socket.write(JSON.stringify(payload) + '\n');
    });

    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8');
      if (!buffer.includes('\n')) return;
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      const line = buffer.split('\n')[0].trim();
      try {
        resolve(JSON.parse(line));
      } catch {
        reject(new Error('invalid broker response'));
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
      reject(new Error('broker disconnected'));
    });
  });
}

function startBrokerInInteractiveSession() {
  if (brokerStartPromise) return brokerStartPromise;

  brokerStartPromise = startBrokerInInteractiveSessionInner().finally(() => {
    brokerStartPromise = null;
  });

  return brokerStartPromise;
}

function startBrokerInInteractiveSessionInner() {
  return new Promise((resolve, reject) => {
    const brokerExe = getBrokerExecutable();

    // The Express server runs inside the elevated Electron main process (main.js
    // re-launches itself as Administrator via UAC), so we already have admin
    // rights — no need to bounce through Task Scheduler to get them. Spawning
    // the broker as a direct, detached, hidden child of this process gives:
    //   • Admin rights (inherited).
    //   • CREATE_NO_WINDOW (windowsHide:true) — Windows actually honors this for
    //     a direct child, unlike Task Scheduler which always allocates a
    //     visible conhost in the user's interactive session.
    //   • detached: true → broker survives if Express dies briefly (rare).
    //   • stdio:'ignore' → broker's Console.WriteLine doesn't keep a console
    //     handle alive and doesn't backpressure if no one reads it.
    //
    // The previous Task-Scheduler-based path popped a visible conhost on every
    // ensureBrokerReady() retry — observed as "endless terminal windows
    // flashing". Direct spawn is cleaner AND invisible.
    const brokerDir = brokerExe ? path.dirname(brokerExe) : path.join(__dirname, '..');

    try {
      let child;
      if (brokerExe) {
        child = spawn(brokerExe, [], {
          cwd: brokerDir,
          detached: true,
          stdio: 'ignore',
          windowsHide: true,
        });
      } else {
        // Fallback: dotnet run the project directly (dev environments without a
        // published broker.exe). Same hidden+detached treatment.
        const projPath = path.join(__dirname, '..', 'native', 'MacMount.NativeBroker', 'MacMount.NativeBroker.csproj');
        child = spawn('dotnet', ['run', '--project', projPath], {
          cwd: brokerDir,
          detached: true,
          stdio: 'ignore',
          windowsHide: true,
        });
      }
      // unref() so this child doesn't keep the Node event loop alive — the broker
      // outlives this process intentionally.
      child.unref();
      child.on('error', (err) => reject(err));
      // Resolve immediately; ensureBrokerReady() polls for the pipe to confirm.
      resolve();
    } catch (err) {
      reject(err);
    }
  });
}

async function ensureBrokerReady(retries = 8, requireElevated = false) {
  // Only attempt to launch the broker once per call. startBrokerInInteractiveSession()
  // is guarded by a module-level promise so concurrent startup/runtime probes
  // do not create duplicate broker processes that split WinFsp mount state.
  let startCount = 0;

  for (let i = 0; i < retries; i++) {
    try {
      const ping = await sendBrokerRequest({ action: 'ping', requestId: String(Date.now()) }, 1500);
      if (ping?.ok && (!requireElevated || ping?.elevated === true)) return true;
    } catch {
      // broker not yet up — fall through to start logic
    }

    if (i === 0 && startCount < 1) {
      startCount += 1;
      try { await startBrokerInInteractiveSession(); } catch {}
    }

    await new Promise((r) => setTimeout(r, 600));
  }
  return false;
}

module.exports = {
  sendBrokerRequest,
  ensureBrokerReady,
};
