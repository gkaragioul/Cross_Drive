const net = require('net');
const path = require('path');
const fs = require('fs');
const { exec } = require('child_process');

const PIPE_PATH = '\\\\.\\pipe\\macmount.broker';
const BROKER_TASK = 'MacMountStartUserBroker';

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
      socket.end();
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
  return new Promise((resolve, reject) => {
    const brokerExe = getBrokerExecutable();
    const runCmd = brokerExe
      ? `"${brokerExe.replace(/"/g, '""')}"`
      : `dotnet run --project "${path.join(__dirname, '..', 'native', 'MacMount.NativeBroker', 'MacMount.NativeBroker.csproj').replace(/"/g, '""')}"`;

    const cmd = [
      `$run='${runCmd.replace(/'/g, "''")}';`,
      "$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -Command \"' + $run + '\"');",
      "$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest;",
      "$task = New-ScheduledTask -Action $action -Principal $principal;",
      `Register-ScheduledTask -TaskName '${BROKER_TASK}' -InputObject $task -Force | Out-Null;`,
      `Start-ScheduledTask -TaskName '${BROKER_TASK}' | Out-Null;`,
      'Start-Sleep -Seconds 1;',
      `Unregister-ScheduledTask -TaskName '${BROKER_TASK}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null;`
    ].join(' ');

    exec(`powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "${cmd}"`, { windowsHide: true }, (error) => {
      if (error) return reject(error);
      resolve();
    });
  });
}

async function ensureBrokerReady(retries = 8, requireElevated = false) {
  for (let i = 0; i < retries; i++) {
    try {
      const ping = await sendBrokerRequest({ action: 'ping', requestId: String(Date.now()) }, 1500);
      if (ping?.ok && (!requireElevated || ping?.elevated === true)) return true;
    } catch {
      // ignore
    }

    if (i === 0 || requireElevated) {
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
