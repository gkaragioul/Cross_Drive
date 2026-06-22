// wslSetup.js — first-run setup for the WSL2-backed mount path.
//
// Responsibilities:
//   - Detect WSL2 + Ubuntu presence
//   - Stage the bundled custom kernel (~16 MB) into %LOCALAPPDATA%\CrossDrive-Kernel\
//   - Write %USERPROFILE%\.wslconfig pointing at it (vmIdleTimeout=2147483647)
//   - Install hfsplus.ko + hfs.ko + apfs.ko into the running distro's
//     /lib/modules/<KVER>/ via wsl_install_modules.sh
//   - Idempotent — safe to run on every app launch; bails early if already set up
//
// What this DOES NOT do:
//   - Install WSL2 itself (`wsl --install`) — that requires a reboot and is
//     surfaced through a UI prompt instead.
//
// All operations are best-effort. On failure, the app continues with the
// native fallback so a partially-set-up machine can still open Mac drives,
// just without the WSL2 R/W path.

const fs = require('fs');
const path = require('path');
const { exec } = require('child_process');
const { promisify } = require('util');

const execAsync = promisify(exec);

const WSL_DISTRO = 'Ubuntu';
const KERNEL_LANDING = path.join(process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE || '', 'AppData', 'Local'), 'CrossDrive-Kernel');
const WSLCONFIG_PATH = path.join(process.env.USERPROFILE || '', '.wslconfig');
const readCrossDriveEnv = (name, fallbackName) => process.env[name] ?? process.env[fallbackName];
const DEFAULT_WSL_MEMORY = readCrossDriveEnv('CROSSDRIVE_WSL_MEMORY', 'CROSSDRIVE_WSL_MEMORY') || '4GB';
const DEFAULT_WSL_PROCESSORS = readCrossDriveEnv('CROSSDRIVE_WSL_PROCESSORS', 'CROSSDRIVE_WSL_PROCESSORS') || '2';
const DEFAULT_WSL_SWAP = readCrossDriveEnv('CROSSDRIVE_WSL_SWAP', 'CROSSDRIVE_WSL_SWAP') || '1GB';

function resolveBundleRoot() {
    // Packaged: extraResources land at process.resourcesPath/crossdrive-kernel
    // (configured by package.json build.extraResources)
    if (process.resourcesPath) {
        const packaged = path.join(process.resourcesPath, 'crossdrive-kernel');
        if (fs.existsSync(packaged)) return packaged;
        // Some builds nest under resources/prereqs/crossdrive-kernel
        const nested = path.join(process.resourcesPath, 'prereqs', 'crossdrive-kernel');
        if (fs.existsSync(nested)) return nested;
        const unpacked = path.join(process.resourcesPath, 'app.asar.unpacked', 'prereqs', 'crossdrive-kernel');
        if (fs.existsSync(unpacked)) return unpacked;
    }
    // Dev: prereqs/crossdrive-kernel/ in the project root
    const dev = path.join(__dirname, '..', 'prereqs', 'crossdrive-kernel');
    if (fs.existsSync(dev)) return dev;
    return null;
}

function resolveInstallModulesScript() {
    const candidates = [];
    if (process.resourcesPath) {
        candidates.push(path.join(process.resourcesPath, 'scripts', 'wsl_install_modules.sh'));
        candidates.push(path.join(process.resourcesPath, 'app.asar.unpacked', 'scripts', 'wsl_install_modules.sh'));
    }
    candidates.push(path.join(__dirname, 'wsl_install_modules.sh'));
    for (const p of candidates) {
        try { if (fs.existsSync(p)) return p; } catch { /* ignore */ }
    }
    return path.join(__dirname, 'wsl_install_modules.sh');
}

function toWslPath(winPath) {
    const normalized = String(winPath).replace(/\\/g, '/');
    const m = /^([A-Za-z]):\/(.*)$/.exec(normalized);
    if (!m) return null;
    return `/mnt/${m[1].toLowerCase()}/${m[2]}`;
}

// wsl.exe writes UTF-16LE on stdout; child_process decodes as UTF-8 by default
// which gives us NUL-separated mojibake. Re-decode the buffer correctly.
async function execWslCapturingUtf16(args, timeoutMs = 10000) {
    return await new Promise((resolve, reject) => {
        const { execFile } = require('child_process');
        execFile(
            'wsl.exe',
            args,
            { timeout: timeoutMs, windowsHide: true, maxBuffer: 65536, encoding: 'buffer' },
            (err, stdout, stderr) => {
                if (err) {
                    err.stdoutText = Buffer.isBuffer(stdout) ? stdout.toString('utf16le') : String(stdout);
                    err.stderrText = Buffer.isBuffer(stderr) ? stderr.toString('utf16le') : String(stderr);
                    return reject(err);
                }
                const text = Buffer.isBuffer(stdout) ? stdout.toString('utf16le') : String(stdout);
                resolve(text.replace(/\u0000/g, ''));
            }
        );
    });
}

async function checkWslAvailable() {
    try {
        await execWslCapturingUtf16(['--status'], 10000);
        return true;
    } catch {
        return false;
    }
}

async function checkUbuntuInstalled() {
    try {
        const out = await execWslCapturingUtf16(['-l', '-q'], 10000);
        return /\bUbuntu\b/i.test(out);
    } catch {
        return false;
    }
}

function copyBundleArtifacts(bundleRoot, logFn) {
    fs.mkdirSync(KERNEL_LANDING, { recursive: true });
    fs.mkdirSync(path.join(KERNEL_LANDING, 'modules'), { recursive: true });

    const kSrc = path.join(bundleRoot, 'wsl_kernel');
    const kDst = path.join(KERNEL_LANDING, 'wsl_kernel');
    if (fs.existsSync(kSrc)) {
        const srcSize = fs.statSync(kSrc).size;
        const dstExists = fs.existsSync(kDst);
        const dstSize = dstExists ? fs.statSync(kDst).size : -1;
        if (!dstExists || srcSize !== dstSize) {
            fs.copyFileSync(kSrc, kDst);
            logFn(`WSL setup: staged kernel (${(srcSize / 1024 / 1024).toFixed(1)} MB) at ${kDst}`, 'info');
        }
    } else {
        logFn(`WSL setup: bundled kernel missing at ${kSrc}; skipping kernel staging`, 'warning');
    }

    for (const mod of ['hfsplus.ko', 'hfs.ko', 'apfs.ko']) {
        const mSrc = path.join(bundleRoot, 'modules', mod);
        const mDst = path.join(KERNEL_LANDING, 'modules', mod);
        if (fs.existsSync(mSrc)) {
            const srcSize = fs.statSync(mSrc).size;
            const dstSize = fs.existsSync(mDst) ? fs.statSync(mDst).size : -1;
            if (srcSize !== dstSize) {
                fs.copyFileSync(mSrc, mDst);
                logFn(`WSL setup: staged ${mod} (${(srcSize / 1024 / 1024).toFixed(1)} MB)`, 'info');
            }
        }
    }
}

function writeWslConfig(logFn) {
    const kernelPath = path.join(KERNEL_LANDING, 'wsl_kernel').replace(/\\/g, '\\\\');
    const expected = `[wsl2]
# CrossDrive custom kernel - CONFIG_HFSPLUS_FS=m + apfs.ko v0.3.20.
# Required for R/W on Mac-formatted drives. Edit at your own risk.
kernel=${kernelPath}

# CrossDrive keeps WSL lightweight. Without explicit limits, VmmemWSL can balloon
# to 10+ GB during large Explorer copies because Linux aggressively caches I/O.
memory=${DEFAULT_WSL_MEMORY}
processors=${DEFAULT_WSL_PROCESSORS}
swap=${DEFAULT_WSL_SWAP}

# Keep VM alive while drives are mounted (kernel mount is torn down on
# auto-shutdown otherwise). Ceiling is ~24 days; restart Windows to reset.
vmIdleTimeout=2147483647
`;

    let existing = null;
    try { existing = fs.readFileSync(WSLCONFIG_PATH, 'utf8'); } catch { /* not present */ }

    if (existing && (existing.includes('CrossDrive custom kernel') || existing.includes('CrossDrive custom kernel') || existing.includes('CrossDrive custom kernel')) && existing.includes(kernelPath)) {
        let updated = existing;
        const upsertWsl2Key = (text, key, value) => {
            const pattern = new RegExp(`(^\\s*${key}\\s*=\\s*).*$`, 'mi');
            if (pattern.test(text)) return text.replace(pattern, `$1${value}`);
            const wsl2 = /^\s*\[wsl2\]\s*$/mi.exec(text);
            if (!wsl2) return `${text.replace(/\s+$/, '')}\n\n[wsl2]\n${key}=${value}\n`;
            const insertAt = wsl2.index + wsl2[0].length;
            return `${text.slice(0, insertAt)}\n${key}=${value}${text.slice(insertAt)}`;
        };

        updated = upsertWsl2Key(updated, 'memory', DEFAULT_WSL_MEMORY);
        updated = upsertWsl2Key(updated, 'processors', DEFAULT_WSL_PROCESSORS);
        updated = upsertWsl2Key(updated, 'swap', DEFAULT_WSL_SWAP);
        updated = upsertWsl2Key(updated, 'vmIdleTimeout', '2147483647');

        if (updated === existing) {
            return false; // already correct
        }

        fs.writeFileSync(WSLCONFIG_PATH, updated, 'utf8');
        logFn(`WSL setup: updated .wslconfig resource limits (memory=${DEFAULT_WSL_MEMORY}, processors=${DEFAULT_WSL_PROCESSORS}, swap=${DEFAULT_WSL_SWAP})`, 'info');
        return true;
    }
    if (existing && !existing.includes('[wsl2]')) {
        // user has some other wslconfig content we shouldn't blow away;
        // append a [wsl2] section if missing
        existing = existing.replace(/\s+$/, '');
        fs.writeFileSync(WSLCONFIG_PATH, `${existing}\n\n${expected}`, 'utf8');
        logFn(`WSL setup: appended [wsl2] section to existing .wslconfig`, 'info');
        return true;
    }
    fs.writeFileSync(WSLCONFIG_PATH, expected, 'utf8');
    logFn(`WSL setup: wrote ${WSLCONFIG_PATH}`, 'info');
    return true;
}

async function shutdownWslIfNeeded(reasonChanged, logFn) {
    if (!reasonChanged) return;
    try {
        await execAsync('wsl.exe --shutdown', { timeout: 30000, windowsHide: true });
        logFn('WSL setup: ran wsl --shutdown so the new kernel/.wslconfig are picked up next boot', 'info');
    } catch (err) {
        logFn(`WSL setup: wsl --shutdown failed: ${err.message}`, 'warning');
    }
}

async function installModulesInsideWsl(logFn) {
    const modulesWinPath = path.join(KERNEL_LANDING, 'modules');
    const modulesWslPath = toWslPath(modulesWinPath);
    if (!modulesWslPath) {
        logFn(`WSL setup: cannot translate '${modulesWinPath}' to WSL path`, 'warning');
        return { ok: false };
    }
    const scriptWinPath = resolveInstallModulesScript();
    const scriptWslPath = toWslPath(scriptWinPath);
    if (!scriptWslPath) {
        logFn(`WSL setup: cannot translate script path '${scriptWinPath}' to WSL`, 'warning');
        return { ok: false };
    }

    const cmd = `wsl.exe -d ${WSL_DISTRO} -u root -- bash "${scriptWslPath}" "${modulesWslPath}"`;
    try {
        const { stdout } = await execAsync(cmd, {
            timeout: 60000,
            windowsHide: true,
            maxBuffer: 1 * 1024 * 1024
        });
        // Last JSON line on stdout is the result.
        const trimmed = String(stdout || '').trim();
        const last = trimmed.lastIndexOf('{');
        if (last < 0) {
            logFn(`WSL setup: install script returned no JSON: ${trimmed.slice(0, 300)}`, 'warning');
            return { ok: false };
        }
        const result = JSON.parse(trimmed.slice(last));
        if (!result.success) {
            logFn(`WSL setup: install_modules failed: ${result.error || 'unknown'}`, 'warning');
            return { ok: false, error: result.error };
        }
        logFn(`WSL setup: kernel ${result.kver} now has modules loaded: ${(result.loaded || []).join(', ') || '(none)'}`, 'success');
        return { ok: true, loaded: result.loaded || [], kver: result.kver };
    } catch (err) {
        logFn(`WSL setup: install_modules error: ${err.message}`, 'warning');
        return { ok: false };
    }
}

/**
 * Run the full first-time/on-launch setup. Returns a summary the caller can log.
 * The caller MUST treat this as best-effort — failures here should not block
 * app startup.
 */
async function ensureWslMountPathReady(logFn = () => {}) {
    const summary = { wslAvailable: false, ubuntu: false, kernelStaged: false, configWritten: false, modulesLoaded: [], error: null };

    if (!await checkWslAvailable()) {
        summary.error = 'Optional WSL2 kernel runtime is not installed.';
        logFn(summary.error, 'warning');
        return summary;
    }
    summary.wslAvailable = true;

    if (!await checkUbuntuInstalled()) {
        summary.error = 'Optional Ubuntu WSL distro is not present.';
        logFn(summary.error, 'warning');
        return summary;
    }
    summary.ubuntu = true;

    const bundleRoot = resolveBundleRoot();
    if (!bundleRoot) {
        summary.error = 'Bundled WSL2 kernel + modules not found (prereqs/crossdrive-kernel/). Build artifacts may be missing from this install.';
        logFn(summary.error, 'warning');
        return summary;
    }

    try {
        copyBundleArtifacts(bundleRoot, logFn);
        summary.kernelStaged = true;
    } catch (err) {
        summary.error = `Could not stage WSL2 kernel: ${err.message}`;
        logFn(summary.error, 'warning');
        return summary;
    }

    let configChanged = false;
    try {
        configChanged = writeWslConfig(logFn);
        summary.configWritten = true;
    } catch (err) {
        logFn(`WSL setup: writing .wslconfig failed: ${err.message}`, 'warning');
    }

    if (configChanged) {
        await shutdownWslIfNeeded(true, logFn);
    }

    const moduleResult = await installModulesInsideWsl(logFn);
    summary.modulesLoaded = moduleResult.loaded || [];
    if (!moduleResult.ok) {
        summary.error = moduleResult.error || 'module install failed';
    }

    return summary;
}

module.exports = { ensureWslMountPathReady };
