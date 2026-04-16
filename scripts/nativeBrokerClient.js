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
  return new Promise((resolve, reject) => {
    const brokerExe = getBrokerExecutable();

    // Why this is more involved than it looks:
    //
    // The broker is a .NET CONSOLE application. When Task Scheduler launches a
    // console exe (even via `powershell -WindowStyle Hidden -Command "& '...'"`)
    // in interactive session mode, the spawned console child still gets its own
    // visible conhost window because Task Scheduler allocates a session-attached
    // console for it. The PowerShell wrapper's `-WindowStyle Hidden` only hides
    // PowerShell's own window, not the broker child's.
    //
    // The fix: have the scheduled task launch a hidden PowerShell that uses
    // `Start-Process -WindowStyle Hidden -FilePath '...'`. Start-Process calls
    // CreateProcess with STARTUPINFO.dwFlags = STARTF_USESHOWWINDOW and
    // wShowWindow = SW_HIDE, which actually hides the broker's conhost — no
    // visible window even with interactive Task Scheduler.
    const brokerDir = brokerExe ? path.dirname(brokerExe) : path.join(__dirname, '..');
    const startProcessArgs = brokerExe
      ? `-FilePath '${brokerExe.replace(/'/g, "''")}' -WindowStyle Hidden -WorkingDirectory '${brokerDir.replace(/'/g, "''")}'`
      : `-FilePath 'dotnet' -ArgumentList 'run','--project','${path.join(__dirname, '..', 'native', 'MacMount.NativeBroker', 'MacMount.NativeBroker.csproj').replace(/'/g, "''")}' -WindowStyle Hidden -WorkingDirectory '${brokerDir.replace(/'/g, "''")}'`;

    const innerPsCommand = `Start-Process ${startProcessArgs}`;
    const actionArg = `-NoProfile -NonInteractive -WindowStyle Hidden -Command "${innerPsCommand.replace(/"/g, '\\"')}"`;

    const actionPs = `New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '${actionArg.replace(/'/g, "''")}' -WorkingDirectory '${brokerDir.replace(/'/g, "''")}'`;

    const cmd = [
      `$action = ${actionPs};`,
      "$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest;",
      "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10);",
      "$task = New-ScheduledTask -Action $action -Principal $principal -Settings $settings;",
      `Register-ScheduledTask -TaskName '${BROKER_TASK}' -InputObject $task -Force | Out-Null;`,
      `Start-ScheduledTask -TaskName '${BROKER_TASK}' | Out-Null;`,
      'Start-Sleep -Seconds 2;',
      `Unregister-ScheduledTask -TaskName '${BROKER_TASK}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null;`
    ].join(' ');

    exec(`powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "${cmd}"`, { windowsHide: true }, (error) => {
      if (error) return reject(error);
      resolve();
    });
  });
}

async function ensureBrokerReady(retries = 8, requireElevated = false) {
  // Only attempt to launch the broker at most twice per call (once at the
  // start, once at the midpoint) regardless of requireElevated.  Launching on
  // every retry creates N simultaneous broker processes all racing to bind the
  // same named pipe — only one wins, the rest fail silently and waste seconds.
  let startCount = 0;
  const MAX_STARTS = 2;

  for (let i = 0; i < retries; i++) {
    try {
      const ping = await sendBrokerRequest({ action: 'ping', requestId: String(Date.now()) }, 1500);
      if (ping?.ok && (!requireElevated || ping?.elevated === true)) return true;
    } catch {
      // broker not yet up — fall through to start logic
    }

    const atStartBoundary = i === 0 || i === Math.floor(retries / 2);
    if (atStartBoundary && startCount < MAX_STARTS) {
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
