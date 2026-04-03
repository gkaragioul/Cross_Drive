const express = require('express');
const { exec, execSync, spawn } = require('child_process');
const cors = require('cors');
const path = require('path');
const fs = require('fs');
const { startNativeService, stopNativeService, sendNativeRequest, getNativeStatus } = require('./scripts/nativeServiceClient');
const { sendBrokerRequest, ensureBrokerReady } = require('./scripts/nativeBrokerClient');

const mountSystemRoutes = require('./routes/systemRoutes');
const mountDriveRoutes = require('./routes/driveRoutes');
const mountMountRoutes = require('./routes/mountRoutes');
const mountNativeRoutes = require('./routes/nativeRoutes');

const app = express();
const port = 3001;
const host = '127.0.0.1';
let httpServer = null;

let logs = [];
const VALID_RUNTIME_MOUNT_MODES = new Set(['wsl_unc', 'hybrid_canary', 'experimental_raw', 'native_first']);
const RUNTIME_MOUNT_MODE = (() => {
    const raw = String(process.env.MACMOUNT_MOUNT_MODE || '').trim().toLowerCase();
    if (!raw) return 'native_first';
    return VALID_RUNTIME_MOUNT_MODES.has(raw) ? raw : 'native_first';
})();
const RUNTIME_CANARY_PERCENT = (() => {
    const raw = Number.parseInt(String(process.env.MACMOUNT_CANARY_PERCENT || '100'), 10);
    if (!Number.isFinite(raw)) return 100;
    return Math.max(0, Math.min(100, raw));
})();
const RUNTIME_NATIVE_MOUNT_ENABLED = RUNTIME_MOUNT_MODE !== 'wsl_unc';
const RUNTIME_ALLOW_WSL_FALLBACK = RUNTIME_MOUNT_MODE !== 'experimental_raw';
const PREFER_SUBST_LOCAL_FAST_PATH = true;
const nativeMountState = new Map();
const inFlightOps = new Set();
const ALLOWED_CORS_ORIGINS = new Set([
    'http://localhost:5173',
    'http://127.0.0.1:5173'
]);

// Driver installation state
let setupState = {
    status: 'ready',
    message: 'Core runtime ready.',
    ready: true
};

function addLog(message, type = 'info') {
    const logEntry = {
        timestamp: new Date().toLocaleTimeString(),
        message: String(message),
        type
    };
    logs.push(logEntry);
    if (logs.length > 200) logs.shift();
    console.log(`[${type.toUpperCase()}] ${message}`);
}

function isLoopbackAddress(remoteAddress) {
    const addr = String(remoteAddress || '').trim();
    return (
        addr === '127.0.0.1' ||
        addr === '::1' ||
        addr === '::ffff:127.0.0.1'
    );
}

function bucketizeDriveId(driveId) {
    const s = String(driveId || '');
    let hash = 0;
    for (let i = 0; i < s.length; i += 1) {
        hash = ((hash << 5) - hash) + s.charCodeAt(i);
        hash |= 0;
    }
    return Math.abs(hash) % 100;
}

function shouldAttemptNativeMountForDrive(driveId, forceNative = false) {
    if (!RUNTIME_NATIVE_MOUNT_ENABLED) return false;
    // Always attempt native broker first — it supports HFS+, HFSX, and APFS.
    // PowerShell fallback only handles APFS via apfs-fuse.
    return true;
}

function execPsMount(driveId, password = '', skipLetter = false) {
    const hasPassword = typeof password === 'string' && password.length > 0;
    const args = ['-ExecutionPolicy', 'Bypass', '-File', PS_PATH, '-Action', 'Mount', '-DriveID', String(driveId)];
    if (hasPassword) {
        args.push('-Password', String(password).replace(/'/g, "''"));
    }
    const env = skipLetter ? { ...process.env, MACMOUNT_SKIP_LETTER: '1' } : process.env;

    // Use spawn instead of exec.  exec waits for ALL pipe handles to close,
    // but apfs-fuse inherits PowerShell's pipe handles and runs indefinitely,
    // so exec hangs for the full 120s timeout.  With spawn we can resolve as
    // soon as valid JSON arrives on stdout — no need to wait for pipe closure.
    return new Promise((resolve, reject) => {
        let resolved = false;
        let stdout = '';
        let stderr = '';

        const child = spawn('powershell', args, {
            windowsHide: true,
            env,
            stdio: ['ignore', 'pipe', 'pipe']
        });

        const timer = setTimeout(() => {
            if (resolved) return;
            resolved = true;
            try { child.kill(); } catch {}
            addLog('PS Mount TIMEOUT after 120s', 'error');
            reject(new Error('Mount script timed out after 120 seconds'));
        }, 120000);

        function tryResolveFromStdout() {
            if (resolved) return;
            const jsonMatch = stdout.match(/\{[\s\S]*\}/);
            if (!jsonMatch) return;
            try {
                const result = JSON.parse(jsonMatch[0]);
                // Valid JSON received — resolve immediately
                resolved = true;
                clearTimeout(timer);
                if (stderr) addLog(`PS Mount Info: ${stderr}`, 'info');
                addLog(`PS Raw Output: ${stdout}`);
                // Detach from child's pipes so we don't keep blocking on them
                try { child.stdout.destroy(); } catch {}
                try { child.stderr.destroy(); } catch {}
                try { child.unref(); } catch {}
                resolve(result);
            } catch { /* JSON incomplete, wait for more data */ }
        }

        child.stdout.on('data', (data) => {
            stdout += data.toString();
            tryResolveFromStdout();
        });

        child.stderr.on('data', (data) => {
            stderr += data.toString();
        });

        child.on('exit', (code) => {
            if (resolved) return;
            // Process exited without valid JSON yet — wait briefly for buffered data
            setTimeout(() => {
                if (resolved) return;
                resolved = true;
                clearTimeout(timer);
                if (stderr) addLog(`PS Mount Info: ${stderr}`, 'info');
                if (code !== 0 && code !== null) {
                    addLog(`PS EXEC ERROR (Code ${code})`, 'error');
                    return reject(new Error(`System execution failure: exit code ${code}`));
                }
                addLog(`PS Raw Output: ${stdout}`);
                try {
                    const jsonMatch = stdout.match(/\{[\s\S]*\}/);
                    const result = jsonMatch ? JSON.parse(jsonMatch[0]) : JSON.parse(stdout);
                    return resolve(result);
                } catch {
                    addLog(`CRITICAL: Failed to parse mount result. Raw: ${stdout}`, 'error');
                    return reject(new Error('Invalid response from mount tool.'));
                }
            }, 200);
        });
    });
}

async function sendNativeWithBoot(payload, timeoutMs = 5000, retries = 6) {
    startNativeService();
    let lastErr = null;
    for (let i = 0; i < retries; i++) {
        try {
            return await sendNativeRequest(payload, timeoutMs);
        } catch (e) {
            lastErr = e;
            if (!/ENOENT|ECONNREFUSED|timeout/i.test(String(e.message || e))) break;
            await new Promise((r) => setTimeout(r, 500));
        }
    }
    throw lastErr || new Error('native service unavailable');
}

async function getBrokerMountedMap() {
    const map = new Map();
    try {
        const ready = await ensureBrokerReady(3);
        if (!ready) return map;

        const status = await sendBrokerRequest({
            action: 'status',
            requestId: String(Date.now())
        }, 3000);

        if (status?.ok && Array.isArray(status.mounted)) {
            for (const m of status.mounted) {
                if (!m?.DriveId && !m?.driveId) continue;
                const id = String(m.DriveId ?? m.driveId);
                const letter = String(m.Letter ?? m.letter ?? '').trim().toUpperCase().replace(':', '');
                if (/^[A-Z]$/.test(letter)) {
                    map.set(id, { letter, path: `${letter}:\\` });
                }
            }
        }
    } catch {
        // best-effort only
    }
    return map;
}

function getAvailableDriveLetter(preferred = '') {
    const pool = ['M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z'];
    const p = String(preferred || '').trim().toUpperCase().replace(':', '');
    const used = getUsedDriveLetters();
    if (/^[A-Z]$/.test(p) && !used.has(p)) return p;
    for (const letter of pool) {
        if (!used.has(letter)) return letter;
    }
    return null;
}

function getUsedDriveLetters() {
    try {
        const out = execSync('cmd /c fsutil fsinfo drives', { stdio: ['ignore', 'pipe', 'ignore'] }).toString('utf8');
        const matches = out.match(/[A-Z]:\\/g) || [];
        return new Set(matches.map((m) => m[0].toUpperCase()));
    } catch {
        const used = new Set();
        for (const letter of 'ABCDEFGHIJKLMNOPQRSTUVWXYZ') {
            try {
                if (fs.existsSync(`${letter}:\\`)) used.add(letter);
            } catch {
                // ignore
            }
        }
        return used;
    }
}

function resolveUserFacingSourcePath(sourcePath) {
    const p = String(sourcePath || '').trim();
    if (!p) return p;

    try {
        const rootCandidate = path.join(p, 'root');
        const hasRootDir = fs.existsSync(rootCandidate) && fs.lstatSync(rootCandidate).isDirectory();
        const hasPrivateDir = fs.existsSync(path.join(p, 'private-dir'));
        if (hasRootDir && hasPrivateDir) {
            return rootCandidate;
        }
    } catch {
        // keep original path
    }
    return p;
}

async function tryMountRawWithFallbackLetters(driveId, preferred = '', sourcePath = '', totalBytesHint = 0, freeBytesHint = 0, physicalDrivePath = '') {
    const pool = ['M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z'];
    const p = String(preferred || '').trim().toUpperCase().replace(':', '');
    const ordered = (/^[A-Z]$/.test(p) ? [p, ...pool.filter(x => x !== p)] : pool);
    const used = getUsedDriveLetters();
    let lastError = 'no free letters';

    for (const letter of ordered) {
        if (used.has(letter)) continue;
        try {
            const brokerReady = await ensureBrokerReady();
            if (!brokerReady) {
                lastError = 'user-session broker unavailable';
                continue;
            }

            let result;
            if (sourcePath) {
                const rawPath = String(physicalDrivePath || '').trim() || `\\\\.\\PHYSICALDRIVE${driveId}`;
                try {
                    result = await sendBrokerRequest({
                        action: 'mount_raw_provider',
                        requestId: String(Date.now()),
                        driveId: String(driveId),
                        letter,
                        physicalDrivePath: rawPath
                    }, 30000);
                    if (result.ok) return { ok: true, letter, result, analysis: result.plan ? { plan: result.plan } : null };
                    addLog(`Raw provider mount failed for drive ${driveId} at ${letter}: ${result.error || 'unknown error'}. Falling back to passthrough.`, 'warning');
                } catch (e) {
                    addLog(`Raw provider mount exception for drive ${driveId} at ${letter}: ${e.message}. Falling back to passthrough.`, 'warning');
                }

                const effectiveSourcePath = resolveUserFacingSourcePath(sourcePath);
                let totalBytes = Number(totalBytesHint) || 0;
                let freeBytes = Number(freeBytesHint);
                if (!Number.isFinite(freeBytes) || freeBytes < 0) freeBytes = 0;
                try {
                    if (totalBytes <= 0) {
                        const analysis = await sendNativeWithBoot({
                            action: 'analyze_raw',
                            requestId: String(Date.now()),
                            physicalDrivePath: `\\\\.\\PHYSICALDRIVE${driveId}`
                        }, 20000, 6);
                        if (analysis?.ok && analysis?.plan?.TotalBytes) {
                            totalBytes = Number(analysis.plan.TotalBytes) || 0;
                        }
                    }
                } catch {
                    // best-effort only
                }
                result = await sendBrokerRequest({
                    action: 'mount_passthrough',
                    requestId: String(Date.now()),
                    driveId: String(driveId),
                    letter,
                    sourcePath: String(effectiveSourcePath),
                    totalBytes,
                    freeBytes
                }, 12000);
                addLog(`Broker passthrough capacity hints for drive ${driveId}: totalBytes=${totalBytes}, freeBytes=${freeBytes}`);
            } else {
                const rawPath = String(physicalDrivePath || '').trim() || `\\\\.\\PHYSICALDRIVE${driveId}`;
                try {
                    result = await sendBrokerRequest({
                        action: 'mount_raw_provider',
                        requestId: String(Date.now()),
                        driveId: String(driveId),
                        letter,
                        physicalDrivePath: rawPath
                    }, 30000);
                    if (result.ok) return { ok: true, letter, result, analysis: result.plan ? { plan: result.plan } : null };
                    lastError = result.error || lastError;
                } catch (e) {
                    lastError = e.message;
                }
                continue;
            }

            if (result.ok) return { ok: true, letter, result };
            lastError = result.error || lastError;
            used.add(letter);
        } catch (e) {
            lastError = e.message;
            used.add(letter);
        }
    }

    return { ok: false, error: lastError };
}

// ─── Express setup ──────────────────────────────────────────────────────────
app.disable('x-powered-by');
app.use((req, res, next) => {
    if (!isLoopbackAddress(req.socket?.remoteAddress)) {
        addLog(`Rejected non-loopback request from ${req.socket?.remoteAddress || 'unknown'}`, 'warning');
        return res.status(403).json({ error: 'Forbidden' });
    }
    next();
});
app.use(cors({
    origin: (origin, callback) => {
        if (!origin || origin === 'null' || ALLOWED_CORS_ORIGINS.has(origin)) {
            return callback(null, true);
        }
        return callback(new Error('Not allowed by CORS'));
    },
    methods: ['GET', 'POST'],
    optionsSuccessStatus: 204
}));
app.use(express.json({ limit: '1mb' }));
app.use((err, req, res, next) => {
    if (err && /cors/i.test(String(err.message || ''))) {
        addLog(`CORS rejected origin: ${req.headers.origin || 'unknown'}`, 'warning');
        return res.status(403).json({ error: 'Forbidden by CORS policy' });
    }
    return next(err);
});

addLog("MacMount Backend started and logging initialized.");
addLog(
    `Runtime mount mode: ${RUNTIME_MOUNT_MODE}` +
    ` (nativeEnabled=${RUNTIME_NATIVE_MOUNT_ENABLED}, canaryPercent=${RUNTIME_CANARY_PERCENT}, allowWslFallback=${RUNTIME_ALLOW_WSL_FALLBACK})`
);
startNativeService();
addLog("Native service started for raw-disk analysis endpoints.");

const PS_PATH = (() => {
    const candidates = [
        process.resourcesPath ? path.join(process.resourcesPath, 'scripts', 'MacMount.ps1') : '',
        path.join(__dirname, 'scripts', 'MacMount.ps1')
    ].filter(Boolean);
    for (const p of candidates) {
        try {
            if (fs.existsSync(p)) return p;
        } catch { }
    }
    return path.join(__dirname, 'scripts', 'MacMount.ps1');
})();

function runPsJson(action, extraArgs = '', timeout = 120000) {
    return new Promise((resolve, reject) => {
        const cmd = `powershell -ExecutionPolicy Bypass -File "${PS_PATH}" -Action ${action} ${extraArgs}`.trim();
        exec(cmd, { timeout, windowsHide: true }, (error, stdout, stderr) => {
            if (stderr) addLog(`PS ${action} stderr: ${stderr}`, 'info');
            if (error) return reject(error);
            try {
                const jsonMatch = stdout.match(/\{[\s\S]*\}/);
                const parsed = jsonMatch ? JSON.parse(jsonMatch[0]) : JSON.parse(stdout);
                resolve(parsed);
            } catch (e) {
                reject(new Error(`Invalid ${action} response: ${stdout}`));
            }
        });
    });
}

async function runRuntimeIntegrationChecks() {
    if (!RUNTIME_NATIVE_MOUNT_ENABLED) {
        addLog('Runtime integration checks skipped (mode=wsl_unc).');
        return;
    }

    let nativeServiceReachable = false;
    for (let attempt = 1; attempt <= 5; attempt += 1) {
        try {
            const native = await getNativeStatus();
            if (native?.available) {
                nativeServiceReachable = true;
                break;
            }
        } catch {
            // retry
        }
        await new Promise((r) => setTimeout(r, 500));
    }
    if (nativeServiceReachable) {
        addLog('Runtime check: native service reachable.', 'success');
    } else {
        addLog('Runtime check: native service not reachable after retries.', 'warning');
    }

    try {
        const brokerReady = await ensureBrokerReady(3);
        if (brokerReady) {
            addLog('Runtime check: broker service reachable.', 'success');
        } else {
            addLog('Runtime check: broker service unavailable.', 'warning');
        }
    } catch (e) {
        addLog(`Runtime check: broker probe failed: ${e.message}`, 'warning');
    }
}

runRuntimeIntegrationChecks();

// ─── Mount routes ───────────────────────────────────────────────────────────
const ctx = {
    addLog,
    logs,
    setupState,
    nativeMountState,
    inFlightOps,
    RUNTIME_MOUNT_MODE,
    RUNTIME_NATIVE_MOUNT_ENABLED,
    RUNTIME_CANARY_PERCENT,
    RUNTIME_ALLOW_WSL_FALLBACK,
    PREFER_SUBST_LOCAL_FAST_PATH,
    PS_PATH,
    execPsMount,
    sendNativeWithBoot,
    getBrokerMountedMap,
    shouldAttemptNativeMountForDrive,
    tryMountRawWithFallbackLetters,
    runPsJson,
    getNativeStatus,
    ensureBrokerReady,
    sendBrokerRequest,
    getUsedDriveLetters,
    resolveUserFacingSourcePath,
};

mountSystemRoutes(app, ctx);
mountDriveRoutes(app, ctx);
mountMountRoutes(app, ctx);
mountNativeRoutes(app, ctx);

// ─── Server lifecycle ───────────────────────────────────────────────────────
function startServer() {
    if (httpServer) return httpServer;
    httpServer = app.listen(port, host, () => {
        console.log(`Backend listening at http://${host}:${port}`);
    });
    return httpServer;
}

function stopServer() {
    if (httpServer) {
        httpServer.close();
        httpServer = null;
    }
    stopNativeService();
}

if (require.main === module) {
    startServer();
}

process.on('SIGINT', () => {
    stopServer();
    process.exit(0);
});

process.on('SIGTERM', () => {
    stopServer();
    process.exit(0);
});

module.exports = {
    startServer,
    stopServer
};
