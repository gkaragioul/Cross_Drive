// wslMountClient.js — drives the WSL2-backed Mac drive R/W mount path.
//
// Responsibilities:
//   - Attach a physical drive to WSL2 (`wsl --mount \\.\PHYSICALDRIVE<N> --bare`)
//   - Run wsl_mount.sh inside WSL2 to detect FS + mount R/W via kernel
//   - Detach + cleanup on unmount
//   - Translate WSL paths to Windows-side UNC paths so Explorer can browse them
//
// Why WSL2 instead of our native HFS+/APFS writer?
//   The native engine's catalog B-tree split has bugs that surface on real-world
//   bulk copies. Linux's hfsplus.ko driver and the linux-apfs-rw DKMS module are
//   battle-tested. We bundle a custom WSL2 kernel with both modules enabled and
//   let the Linux kernel do the heavy lifting.
//
// Required runtime state (set up by the installer):
//   - WSL2 + Ubuntu distro installed
//   - Custom WSL2 kernel with CONFIG_HFSPLUS_FS=m at
//     %LOCALAPPDATA%\MacMount-Kernel\wsl_kernel  (referenced by .wslconfig)
//   - hfsplus.ko + apfs.ko present in /lib/modules/<KVER>/

const { exec } = require('child_process');
const { promisify } = require('util');
const path = require('path');
const fs = require('fs');

const execAsync = promisify(exec);

const WSL_DISTRO = 'Ubuntu';
const MAX_OUTPUT_BYTES = 1 * 1024 * 1024;

// Translate Windows project script paths to /mnt/<drive>/... paths visible from WSL2.
function toWslPath(winPath) {
    const normalized = String(winPath).replace(/\\/g, '/');
    const m = /^([A-Za-z]):\/(.*)$/.exec(normalized);
    if (!m) throw new Error(`toWslPath: not a drive-rooted path: ${winPath}`);
    return `/mnt/${m[1].toLowerCase()}/${m[2]}`;
}

// Prefer the packaged copy in process.resourcesPath/scripts; fall back to the
// dev project root so `npm run start` works without a packaged build.
function resolveScriptPath(name) {
    const candidates = [];
    if (process.resourcesPath) {
        candidates.push(path.join(process.resourcesPath, 'scripts', name));
    }
    candidates.push(path.join(__dirname, name));
    candidates.push(path.join(__dirname, '..', 'scripts', name));
    for (const p of candidates) {
        try { if (fs.existsSync(p)) return p; } catch { /* ignore */ }
    }
    // Best-effort fallback: assume project root layout.
    return path.join(__dirname, '..', 'scripts', name);
}

const WSL_MOUNT_SCRIPT_LINUX   = toWslPath(resolveScriptPath('wsl_mount.sh'));
const WSL_UNMOUNT_SCRIPT_LINUX = toWslPath(resolveScriptPath('wsl_unmount.sh'));

function shellQuoteSingle(s) {
    // For passing args to bash through `wsl -e bash -c "..."`.
    return `'${String(s).replace(/'/g, `'\\''`)}'`;
}

function parseTrailingJson(stdout) {
    // Our scripts emit a single JSON object on stdout (last line). Be tolerant
    // of trailing whitespace or rare extra characters.
    const trimmed = String(stdout || '').trim();
    if (!trimmed) return null;
    const lastBrace = trimmed.lastIndexOf('}');
    const firstBrace = trimmed.lastIndexOf('{');
    if (lastBrace < 0 || firstBrace < 0 || firstBrace > lastBrace) return null;
    const candidate = trimmed.slice(firstBrace, lastBrace + 1);
    try { return JSON.parse(candidate); }
    catch { return null; }
}

// Drive letters we will NOT use for subst mapping (system + common reserved).
const RESERVED_LETTERS = new Set(['A', 'B', 'C']);

/**
 * Find the first free drive letter, preferring M, N, O, ... working backward.
 * Returns null if no letter is available.
 */
async function findFreeDriveLetter() {
    const used = new Set();
    // List in-use drive letters via PowerShell — replaces deprecated `wmic`
    // (which prints a deprecation banner that flashes a window even under
    // windowsHide on some Win11 builds).
    try {
        const { stdout } = await execAsync(
            `powershell -NoProfile -NonInteractive -WindowStyle Hidden -Command "(Get-PSDrive -PSProvider FileSystem).Name -join ','"`,
            { timeout: 10000, windowsHide: true, maxBuffer: 65536 }
        );
        for (const ltr of String(stdout).trim().split(/[\s,]+/)) {
            if (/^[A-Z]$/i.test(ltr)) used.add(ltr.toUpperCase());
        }
    } catch { /* fall back to subst output */ }
    try {
        const { stdout } = await execAsync('subst', { timeout: 5000, windowsHide: true });
        for (const line of String(stdout).split(/\r?\n/)) {
            const m = /^([A-Z]):/i.exec(line.trim());
            if (m) used.add(m[1].toUpperCase());
        }
    } catch { /* ignore */ }

    const order = ['M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z',
                   'L', 'K', 'J', 'I', 'H', 'G', 'F', 'E', 'D'];
    for (const ltr of order) {
        if (RESERVED_LETTERS.has(ltr)) continue;
        if (used.has(ltr)) continue;
        return ltr;
    }
    return null;
}

/**
 * Run a PowerShell command in the interactive user's session via Scheduled Task.
 * MacMount runs elevated; subst/net-use mappings made there don't show up in the
 * user's Explorer (each token has its own DOS device namespace). The Scheduled
 * Task trick (-LogonType Interactive) executes our command as the logged-in user.
 *
 * SILENCE: every layer here must be invisible. The outer powershell uses
 * windowsHide + -WindowStyle Hidden. The Scheduled Task action itself runs
 * powershell.exe with -WindowStyle Hidden too — without that flag, the task
 * scheduler briefly allocates a visible conhost in the user's interactive
 * session every time it fires. Inner commands MUST avoid cmd.exe (each
 * cmd.exe /c spawn pops its own console window even under hidden parents).
 */
async function runInUserSession(psCommand, timeoutMs = 15000) {
    const taskName = `MacMountSubst_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
    // Encode the inner command to avoid quoting hell.
    const innerB64 = Buffer.from(psCommand, 'utf16le').toString('base64');
    const wrapper =
        // -WindowStyle Hidden on the action's powershell prevents the brief
        // conhost flash in the user session every time the task fires.
        `$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand ${innerB64}'; ` +
        `$pr = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive; ` +
        `$set = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; ` +
        `$t = New-ScheduledTask -Action $a -Principal $pr -Settings $set; ` +
        `Register-ScheduledTask -TaskName '${taskName}' -InputObject $t -Force | Out-Null; ` +
        `Start-ScheduledTask -TaskName '${taskName}' | Out-Null; ` +
        `Start-Sleep -Milliseconds 800; ` +
        `Unregister-ScheduledTask -TaskName '${taskName}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null;`;
    await execAsync(
        `powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "${wrapper.replace(/"/g, '\\"')}"`,
        { timeout: timeoutMs, windowsHide: true, maxBuffer: MAX_OUTPUT_BYTES }
    );
}

/**
 * Map a UNC path to a Windows drive letter so it appears in This PC.
 *
 * `subst` writes to the per-session DOS device namespace, so mapping from the
 * elevated MacMount process won't be visible to the user's Explorer. We run
 * `subst` inside the user's interactive logon session via a scheduled task.
 */
async function substMapDriveLetter(uncPath, logFn = () => {}) {
    const letter = await findFreeDriveLetter();
    if (!letter) {
        logFn('No free drive letter available for subst mapping; UNC path only.', 'warning');
        return null;
    }
    try {
        // Log success/failure to a per-mount file we can read back to confirm
        // the mapping actually took. Use Start-Process -WindowStyle Hidden
        // -Wait for subst.exe so the console subsystem app doesn't flash a
        // window in the user session — `cmd.exe /c "subst ..."` does, every
        // call. Direct subst.exe under a hidden Start-Process does not.
        const logFile = path.join(process.env.ProgramData || 'C:\\ProgramData', 'MacMount', `subst-${letter}.log`).replace(/\\/g, '\\\\');
        const userCmd =
            `try { ` +
            // Best-effort: clear any prior mapping (silent, ignore errors).
            `  Start-Process -FilePath subst.exe -ArgumentList '${letter}:','/D' -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue; ` +
            // Apply the new mapping. Capture exit code via -PassThru.
            `  $p = Start-Process -FilePath subst.exe -ArgumentList '${letter}:','"${uncPath}"' -WindowStyle Hidden -Wait -PassThru; ` +
            `  Set-Content -Path '${logFile}' -Value ('exit ' + $p.ExitCode) -Encoding utf8 -Force; ` +
            `} catch { Set-Content -Path '${logFile}' -Value ('error ' + $_.Exception.Message) -Encoding utf8 -Force }`;
        await runInUserSession(userCmd);
        // Give Explorer's drive-letter watcher a moment.
        await new Promise(r => setTimeout(r, 1200));
        logFn(`Mapped ${uncPath} -> ${letter}: in user session`, 'success');
        return letter;
    } catch (err) {
        logFn(`subst user-session mapping failed: ${String(err.message || err).slice(0, 200)}`, 'warning');
        return null;
    }
}

async function substRemoveDriveLetter(letter, logFn = () => {}) {
    const L = String(letter || '').trim().toUpperCase().replace(':', '');
    if (!/^[A-Z]$/.test(L)) return;
    try {
        // Direct subst.exe via Start-Process -WindowStyle Hidden — no cmd.exe
        // wrapper, no visible console flash in the user session.
        const userCmd = `Start-Process -FilePath subst.exe -ArgumentList '${L}:','/D' -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue`;
        await runInUserSession(userCmd);
        // Also try in our own (elevated) session as a belt-and-braces.
        try { await execAsync(`subst ${L}: /D`, { timeout: 5000, windowsHide: true }); } catch {}
        logFn(`Released subst ${L}: in user session`, 'info');
    } catch {
        // common when letter is no longer mapped — non-fatal
    }
}

/**
 * Attach a physical drive to WSL2 (idempotent — succeeds if already attached).
 */
async function attachPhysicalDriveToWsl(driveId) {
    const target = `\\\\.\\PHYSICALDRIVE${driveId}`;
    try {
        await execAsync(`wsl.exe --mount "${target}" --bare`, {
            timeout: 30000,
            windowsHide: true,
            maxBuffer: MAX_OUTPUT_BYTES
        });
        return { ok: true };
    } catch (err) {
        const msg = String(err.stderr || err.stdout || err.message || '');
        if (/already attached|WSL_E_DISK_ALREADY_ATTACHED/i.test(msg)) {
            return { ok: true, alreadyAttached: true };
        }
        if (/elevation needed|elevated|administrator/i.test(msg)) {
            return { ok: false, error: 'wsl --mount requires Administrator. Restart MacMount as Administrator.', needsAdmin: true };
        }
        return { ok: false, error: `wsl --mount failed: ${msg.trim().slice(0, 400)}` };
    }
}

/**
 * Detach a physical drive from WSL2 (idempotent — succeeds if not attached).
 */
async function detachPhysicalDriveFromWsl(driveId) {
    const target = `\\\\.\\PHYSICALDRIVE${driveId}`;
    try {
        await execAsync(`wsl.exe --unmount "${target}"`, {
            timeout: 30000,
            windowsHide: true,
            maxBuffer: MAX_OUTPUT_BYTES
        });
        return { ok: true };
    } catch (err) {
        const msg = String(err.stderr || err.stdout || err.message || '');
        if (/not found|not.*mounted|ERROR_FILE_NOT_FOUND/i.test(msg)) {
            return { ok: true, notAttached: true };
        }
        return { ok: false, error: `wsl --unmount failed: ${msg.trim().slice(0, 400)}` };
    }
}

/**
 * Mount the drive read-write via the Linux kernel and return a Windows-side
 * UNC path Explorer can browse.
 */
async function wslMountDrive(driveId, password = null, logFn = () => {}) {
    const id = String(driveId).trim();
    if (!/^\d+$/.test(id)) {
        return { error: `Invalid drive id: ${driveId}` };
    }

    // Keep WSL2 alive — without this, vmIdleTimeout (default 60s) tears down
    // the kernel mount and the \\wsl.localhost\Ubuntu\mnt\... UNC goes dead.
    ensureWslKeepAlive();

    logFn(`WSL mount: attaching PhysicalDrive${id}`, 'info');
    const attach = await attachPhysicalDriveToWsl(id);
    if (!attach.ok) {
        return { error: attach.error, needsAdmin: attach.needsAdmin };
    }

    // Allow a moment for /dev/sd? to enumerate after attach.
    await new Promise((r) => setTimeout(r, 1500));

    // Run mount script inside WSL as root.
    const args = [id];
    if (password) args.push(password);
    const cmd = `wsl.exe -d ${WSL_DISTRO} -u root -- bash ${shellQuoteSingle(WSL_MOUNT_SCRIPT_LINUX)} ${args.map(shellQuoteSingle).join(' ')}`;
    logFn(`WSL mount: running mount script for drive ${id}`, 'info');

    let stdout = '';
    let stderr = '';
    try {
        ({ stdout, stderr } = await execAsync(cmd, {
            timeout: 60000,
            windowsHide: true,
            maxBuffer: MAX_OUTPUT_BYTES
        }));
    } catch (err) {
        stdout = String(err.stdout || '');
        stderr = String(err.stderr || err.message || '');
    }

    if (stderr && stderr.trim()) {
        logFn(`WSL mount stderr: ${stderr.trim().slice(0, 800)}`, 'info');
    }

    const parsed = parseTrailingJson(stdout);
    if (!parsed) {
        return { error: `WSL mount script returned no JSON. stderr=${stderr.slice(0, 400)} stdout=${stdout.slice(0, 400)}` };
    }
    if (!parsed.success) {
        return {
            error: parsed.error || 'WSL mount failed',
            needsPassword: parsed.needsPassword === true
        };
    }

    // Translate Linux mount target into a Windows UNC path.
    // /mnt/macdrive_3_abc12345  →  \\wsl.localhost\Ubuntu\mnt\macdrive_3_abc12345
    const wslTarget = parsed.target;
    const uncPath = `\\\\wsl.localhost\\${WSL_DISTRO}${wslTarget.replace(/\//g, '\\')}`;

    // Map a Windows drive letter via subst so the volume shows up in "This PC"
    // alongside other drives (matches user expectations from MacDrive et al).
    const driveLetter = await substMapDriveLetter(uncPath, logFn);

    logFn(`WSL mount: drive ${id} now at ${uncPath}${driveLetter ? ` (mapped to ${driveLetter}:)` : ''} (fs=${parsed.fsType})`, 'success');

    return {
        uncPath,
        wslTarget,
        driveLetter,                 // null if subst failed
        fsType: parsed.fsType,
        device: parsed.device,
        mountType: 'wsl_kernel'
    };
}

/**
 * Unmount + detach the drive. Idempotent.
 * mountInfo: { wslTarget, driveLetter? } — driveLetter is the subst letter to release.
 */
async function wslUnmountDrive(driveId, wslTargetOrInfo, logFn = () => {}) {
    const id = String(driveId).trim();
    let wslTarget, driveLetter;
    if (typeof wslTargetOrInfo === 'object' && wslTargetOrInfo !== null) {
        wslTarget = wslTargetOrInfo.wslTarget;
        driveLetter = wslTargetOrInfo.driveLetter;
    } else {
        wslTarget = wslTargetOrInfo;
    }

    // Release the subst drive letter first so Explorer stops holding the UNC path open.
    if (driveLetter) {
        await substRemoveDriveLetter(driveLetter, logFn);
    }

    if (wslTarget) {
        try {
            const cmd = `wsl.exe -d ${WSL_DISTRO} -u root -- bash ${shellQuoteSingle(WSL_UNMOUNT_SCRIPT_LINUX)} ${shellQuoteSingle(wslTarget)}`;
            const { stdout } = await execAsync(cmd, {
                timeout: 30000,
                windowsHide: true,
                maxBuffer: MAX_OUTPUT_BYTES
            });
            const parsed = parseTrailingJson(stdout);
            if (!parsed?.success) {
                logFn(`WSL umount returned non-success: ${stdout.slice(0, 400)}`, 'warning');
            }
        } catch (err) {
            logFn(`WSL umount script error: ${err.message}`, 'warning');
        }
    }

    if (/^\d+$/.test(id)) {
        const detach = await detachPhysicalDriveFromWsl(id);
        if (!detach.ok) {
            logFn(`WSL detach warning: ${detach.error}`, 'warning');
        }
    }

    return { ok: true };
}

// ─── Keep-alive process so WSL2 doesn't idle-shutdown and tear down our mounts ───

let _wslKeepAliveProc = null;

/**
 * Spawn a long-running `wsl -d Ubuntu -- bash -c "while sleep 3600; do :; done"`
 * so the WSL2 VM stays running even if vmIdleTimeout was missed in .wslconfig.
 * Idempotent — safe to call repeatedly.
 *
 * SILENCE: detached + stdio:'ignore' + windowsHide:true gives Node a
 * CREATE_NO_WINDOW + DETACHED_PROCESS combo so wsl.exe never gets a console
 * allocated. unref() ensures Node's exit isn't blocked by the child.
 */
function ensureWslKeepAlive() {
    if (_wslKeepAliveProc && !_wslKeepAliveProc.killed) return;
    const { spawn } = require('child_process');
    try {
        _wslKeepAliveProc = spawn(
            'wsl.exe',
            ['-d', WSL_DISTRO, '--', 'bash', '-c', 'trap "exit 0" TERM; while true; do sleep 3600; done'],
            { detached: true, stdio: 'ignore', windowsHide: true }
        );
        _wslKeepAliveProc.on('exit', () => { _wslKeepAliveProc = null; });
        _wslKeepAliveProc.on('error', () => { _wslKeepAliveProc = null; });
        // Without unref(), Node's event loop waits on this child forever and the
        // process can't gracefully exit. We still keep a reference so SIGTERM
        // works from stopWslKeepAlive().
        try { _wslKeepAliveProc.unref(); } catch { /* old node */ }
    } catch {
        _wslKeepAliveProc = null;
    }
}

function stopWslKeepAlive() {
    if (_wslKeepAliveProc && !_wslKeepAliveProc.killed) {
        try { _wslKeepAliveProc.kill('SIGTERM'); } catch {}
        _wslKeepAliveProc = null;
    }
}

/**
 * Quick health check — verify WSL2 is up, Ubuntu present, custom kernel loaded.
 */
async function checkWslHealth() {
    try {
        const { stdout } = await execAsync(`wsl.exe -d ${WSL_DISTRO} -- uname -r`, {
            timeout: 15000,
            windowsHide: true,
            maxBuffer: 65536
        });
        const kernel = String(stdout).trim();
        // Our custom kernel ends with "+" suffix when built locally.
        const isCustomKernel = kernel.endsWith('+') || kernel.includes('+');
        return { ok: true, kernel, isCustomKernel };
    } catch (err) {
        return { ok: false, error: String(err.message || err).slice(0, 400) };
    }
}

module.exports = {
    wslMountDrive,
    wslUnmountDrive,
    attachPhysicalDriveToWsl,
    detachPhysicalDriveFromWsl,
    checkWslHealth,
    ensureWslKeepAlive,
    stopWslKeepAlive
};
