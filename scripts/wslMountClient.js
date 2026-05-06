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

const { exec, execFile, spawn } = require('child_process');
const { promisify } = require('util');
const path = require('path');
const fs = require('fs');

const execAsync = promisify(exec);
const execFileAsync = promisify(execFile);

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
        candidates.push(path.join(process.resourcesPath, 'app.asar.unpacked', 'scripts', name));
    }
    candidates.push(path.join(__dirname, name));
    candidates.push(path.join(__dirname, '..', 'scripts', name));
    for (const p of candidates) {
        try { if (fs.existsSync(p)) return p; } catch { /* ignore */ }
    }
    // Best-effort fallback: assume project root layout.
    return path.join(__dirname, '..', 'scripts', name);
}

function resolveUserSessionHelperPath() {
    const candidates = [];
    if (process.resourcesPath) {
        candidates.push(path.join(process.resourcesPath, 'native-bin', 'user-session', 'MacMount.UserSessionHelper.exe'));
        candidates.push(path.join(process.resourcesPath, 'native-bin', 'MacMount.UserSessionHelper.exe'));
        candidates.push(path.join(process.resourcesPath, 'app.asar.unpacked', 'native', 'bin', 'user-session', 'MacMount.UserSessionHelper.exe'));
        candidates.push(path.join(process.resourcesPath, 'app.asar.unpacked', 'native', 'bin', 'MacMount.UserSessionHelper.exe'));
    }
    candidates.push(path.join(__dirname, '..', 'native', 'bin', 'user-session', 'MacMount.UserSessionHelper.exe'));
    for (const p of candidates) {
        try { if (fs.existsSync(p)) return p; } catch { /* ignore */ }
    }
    return null;
}

const WSL_MOUNT_SCRIPT_LINUX   = toWslPath(resolveScriptPath('wsl_mount.sh'));
const WSL_UNMOUNT_SCRIPT_LINUX = toWslPath(resolveScriptPath('wsl_unmount.sh'));
const WSL_VALIDATE_SCRIPT_LINUX = toWslPath(resolveScriptPath('wsl_validate_mount.sh'));

function parseTrailingJson(stdout) {
    // Our scripts emit a single JSON object on stdout (last line). Be tolerant
    // of trailing whitespace or rare extra characters.
    const trimmed = normalizeWslText(stdout).trim();
    if (!trimmed) return null;
    const lastBrace = trimmed.lastIndexOf('}');
    const firstBrace = trimmed.lastIndexOf('{');
    if (lastBrace < 0 || firstBrace < 0 || firstBrace > lastBrace) return null;
    const candidate = trimmed.slice(firstBrace, lastBrace + 1);
    try { return JSON.parse(candidate); }
    catch { return null; }
}

function normalizeWslText(value) {
    // Windows-side wsl.exe errors can arrive UTF-16-ish through pipes with a
    // NUL byte after every character. Normalize before regex/error parsing.
    return String(value || '').replace(/\u0000/g, '');
}

async function runWsl(args, options = {}) {
    return await execFileAsync('wsl.exe', args, {
        timeout: options.timeout || 30000,
        windowsHide: true,
        maxBuffer: options.maxBuffer || MAX_OUTPUT_BYTES,
        encoding: options.encoding || 'utf8'
    });
}

async function waitForPathAccessible(targetPath, timeoutMs = 10000) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        try {
            if (fs.existsSync(targetPath)) return true;
        } catch {
            // Network-backed paths can throw while the redirector is waking up.
        }
        await new Promise((resolve) => setTimeout(resolve, 300));
    }
    return false;
}

async function validateWslMountTarget(wslTarget, expectedDevice, expectedFsType, mountNamespace = 'user') {
    try {
        let stdout;
        if (mountNamespace === 'elevated') {
            ({ stdout } = await runWsl([
                '-d', WSL_DISTRO, '-u', 'root', '--',
                'bash',
                WSL_VALIDATE_SCRIPT_LINUX,
                String(wslTarget || ''),
                String(expectedDevice || ''),
                String(expectedFsType || '')
            ], { timeout: 60000 }));
        } else {
            ({ stdout } = await runUserSessionHelperWithOutput([
                'wslvalidate',
                WSL_VALIDATE_SCRIPT_LINUX,
                String(wslTarget || ''),
                String(expectedDevice || ''),
                String(expectedFsType || '')
            ], 60000));
        }
        const parsed = parseTrailingJson(stdout);
        if (!parsed?.success) {
            return { ok: false, error: parsed?.error || 'WSL mount validation failed' };
        }
        return {
            ok: true,
            totalBytes: Number(parsed.totalBytes) || 0,
            freeBytes: Number(parsed.freeBytes) || 0
        };
    } catch (err) {
        const msg = normalizeWslText(err.stdout || err.stderr || err.message || '').trim();
        const transient = /Command failed|UserSessionHelper|timed out|timeout|ScheduledTask|CLIXML|Preparing modules/i.test(msg);
        return {
            ok: false,
            error: msg || 'WSL mount validation failed',
            transient
        };
    }
}

function getPresentationLinksRoot() {
    return path.join(process.env.ProgramData || 'C:\\ProgramData', 'MacMount', 'Links');
}

function makeSafeLinkName(driveId) {
    return `Drive${String(driveId).replace(/[^\dA-Za-z_-]/g, '')}_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
}

async function createLocalPresentationLink(driveId, uncPath, logFn = () => {}) {
    const root = getPresentationLinksRoot();
    const linkPath = path.join(root, makeSafeLinkName(driveId));
    try {
        fs.mkdirSync(root, { recursive: true });
        try { fs.rmSync(linkPath, { recursive: true, force: true }); } catch { /* ignore */ }
        fs.symlinkSync(uncPath, linkPath, 'dir');
        if (await waitForPathAccessible(linkPath, 5000)) {
            logFn(`Created local presentation link ${linkPath} -> ${uncPath}`, 'info');
            return linkPath;
        }
        try { fs.rmSync(linkPath, { recursive: true, force: true }); } catch { /* ignore */ }
        logFn(`Local presentation link was created but not reachable; falling back to UNC mapping.`, 'warning');
    } catch (err) {
        logFn(`Local presentation link failed: ${String(err.message || err).slice(0, 200)}. Falling back to UNC mapping.`, 'warning');
    }
    return null;
}

function removeLocalPresentationLink(linkPath) {
    const resolved = String(linkPath || '').trim();
    if (!resolved) return;
    const root = path.resolve(getPresentationLinksRoot());
    const target = path.resolve(resolved);
    if (!target.toLowerCase().startsWith(root.toLowerCase() + path.sep.toLowerCase())) {
        return;
    }
    try { fs.rmSync(target, { recursive: true, force: true }); } catch { /* ignore */ }
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

function psSingleQuote(value) {
    return `'${String(value).replace(/'/g, "''")}'`;
}

function shSingleQuote(value) {
    return `'${String(value).replace(/'/g, `'\\''`)}'`;
}

async function runPowerShellEncoded(script, timeoutMs = 15000) {
    const encoded = Buffer.from(script, 'utf16le').toString('base64');
    return await execFileAsync('powershell.exe', [
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-WindowStyle', 'Hidden',
        '-ExecutionPolicy', 'Bypass',
        '-EncodedCommand', encoded
    ], {
        timeout: timeoutMs,
        windowsHide: true,
        maxBuffer: MAX_OUTPUT_BYTES
    });
}

/**
 * Run the Windows-subsystem user-session helper in the interactive user session.
 * The scheduled task action MUST be a GUI-subsystem executable. If it is
 * powershell.exe/cmd.exe/subst.exe/net.exe, Windows Terminal can pop a visible
 * terminal even with -WindowStyle Hidden.
 */
async function runUserSessionHelper(args, timeoutMs = 15000) {
    const helperPath = resolveUserSessionHelperPath();
    if (!helperPath) {
        throw new Error('MacMount.UserSessionHelper.exe is missing from native-bin/user-session.');
    }

    const taskName = `MacMountUser_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
    const argumentList = args.map((arg) => `"${String(arg).replace(/"/g, '\\"')}"`).join(' ');
    const waitSeconds = Math.max(10, Math.ceil(timeoutMs / 1000));
    const wrapper = [
        `$ProgressPreference = 'SilentlyContinue';`,
        `$VerbosePreference = 'SilentlyContinue';`,
        `$InformationPreference = 'SilentlyContinue';`,
        `$a = New-ScheduledTaskAction -Execute ${psSingleQuote(helperPath)} -Argument ${psSingleQuote(argumentList)};`,
        `$pr = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited;`,
        `$set = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2);`,
        `$t = New-ScheduledTask -Action $a -Principal $pr -Settings $set;`,
        `Register-ScheduledTask -TaskName ${psSingleQuote(taskName)} -InputObject $t -Force | Out-Null;`,
        `Start-ScheduledTask -TaskName ${psSingleQuote(taskName)} | Out-Null;`,
        `$deadline = (Get-Date).AddSeconds(${waitSeconds});`,
        `do {`,
        `  Start-Sleep -Milliseconds 200;`,
        `  $task = Get-ScheduledTask -TaskName ${psSingleQuote(taskName)} -ErrorAction SilentlyContinue;`,
        `  if (-not $task -or $task.State -ne 'Running') { break }`,
        `} while ((Get-Date) -lt $deadline);`,
        `$info = Get-ScheduledTaskInfo -TaskName ${psSingleQuote(taskName)} -ErrorAction SilentlyContinue;`,
        `$result = if ($info) { [int]$info.LastTaskResult } else { 9999 };`,
        `Unregister-ScheduledTask -TaskName ${psSingleQuote(taskName)} -Confirm:$false -ErrorAction SilentlyContinue | Out-Null;`,
        `if ($result -ne 0) { throw "UserSessionHelper failed with exit code $result" }`
    ].join(' ');

    await runPowerShellEncoded(wrapper, timeoutMs);
}

async function runUserSessionHelperWithOutput(args, timeoutMs = 30000) {
    const tempRoot = path.join(process.env.ProgramData || 'C:\\ProgramData', 'MacMount', 'wsl-ipc');
    fs.mkdirSync(tempRoot, { recursive: true });
    const id = `${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
    const stdoutPath = path.join(tempRoot, `${id}.out`);
    const stderrPath = path.join(tempRoot, `${id}.err`);
    try {
        await runUserSessionHelper([...args, stdoutPath, stderrPath], timeoutMs);
        const stdout = fs.existsSync(stdoutPath) ? fs.readFileSync(stdoutPath, 'utf8') : '';
        const stderr = fs.existsSync(stderrPath) ? fs.readFileSync(stderrPath, 'utf8') : '';
        return { stdout, stderr };
    } finally {
        try { fs.rmSync(stdoutPath, { force: true }); } catch { /* ignore */ }
        try { fs.rmSync(stderrPath, { force: true }); } catch { /* ignore */ }
    }
}

/**
 * Map a UNC path to a Windows drive letter so it appears in This PC.
 *
 * Drive-letter mappings are per logon session. MacMount runs elevated, while
 * Explorer is normally non-elevated, so we create/remove the mapping through a
 * tiny GUI-subsystem helper launched in the user's interactive session.
 */
async function substMapDriveLetter(targetPath, logFn = () => {}) {
    const letter = await findFreeDriveLetter();
    if (!letter) {
        logFn('No free drive letter available for subst mapping; UNC path only.', 'warning');
        return null;
    }
    if (!await waitForPathAccessible(targetPath, 12000)) {
        logFn(`Mount target is not reachable yet; skipping drive-letter mapping: ${targetPath}`, 'warning');
        return null;
    }
    try {
        await runUserSessionHelper(['map', letter, targetPath]);
        await new Promise((resolve) => setTimeout(resolve, 1200));
        logFn(`Mapped ${targetPath} -> ${letter}: in user session`, 'success');
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
        await runUserSessionHelper(['unmap', L]);
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
        await runWsl(['--mount', target, '--bare'], { timeout: 30000 });
        return { ok: true };
    } catch (err) {
        const msg = normalizeWslText(err.stderr || err.stdout || err.message || '');
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
        await runWsl(['--unmount', target], { timeout: 30000 });
        return { ok: true };
    } catch (err) {
        const msg = normalizeWslText(err.stderr || err.stdout || err.message || '');
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
async function wslMountDrive(driveId, password = null, logFn = () => {}, options = {}) {
    const id = String(driveId).trim();
    if (!/^\d+$/.test(id)) {
        return { error: `Invalid drive id: ${driveId}` };
    }

    // Keep WSL2 alive — without this, vmIdleTimeout (default 60s) tears down
    // the kernel mount and the \\wsl.localhost\Ubuntu\mnt\... UNC goes dead.
    const mountNamespace = options.mountNamespace === 'elevated' ? 'elevated' : 'user';
    const keepAlive = await ensureWslKeepAlive(logFn, mountNamespace);
    if (!keepAlive) {
        return { error: 'WSL keep-alive could not be started. Refusing to mount because WSL may tear down the Mac volume while Windows is still writing.' };
    }

    logFn(`WSL mount: attaching PhysicalDrive${id}`, 'info');
    const attach = await attachPhysicalDriveToWsl(id);
    if (!attach.ok) {
        return { error: attach.error, needsAdmin: attach.needsAdmin };
    }

    // Allow a moment for /dev/sd? to enumerate after attach.
    await new Promise((r) => setTimeout(r, 1500));

    // For direct WSL/UNC presentation, mount in the interactive user's WSL
    // namespace. For WinFsp passthrough, mount in the elevated namespace because
    // the elevated broker is the process that reads/writes the WSL path.
    logFn(`WSL mount: running mount script for drive ${id} (${mountNamespace} namespace)`, 'info');

    let stdout = '';
    let stderr = '';
    try {
        const tempRoot = path.join(process.env.ProgramData || 'C:\\ProgramData', 'MacMount', 'wsl-ipc');
        fs.mkdirSync(tempRoot, { recursive: true });
        const requestId = `${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
        const stdoutPath = path.join(tempRoot, `${requestId}.out`);
        const stderrPath = path.join(tempRoot, `${requestId}.err`);
        try {
            if (mountNamespace === 'elevated') {
                const args = ['-d', WSL_DISTRO, '-u', 'root', '--', 'bash', WSL_MOUNT_SCRIPT_LINUX, id];
                if (password) args.push(password);
                const result = await runWsl(args, { timeout: 90000 });
                stdout = result.stdout || '';
                stderr = result.stderr || '';
            } else {
                const helperArgs = ['wslmount', WSL_MOUNT_SCRIPT_LINUX, id, stdoutPath, stderrPath];
                if (password) helperArgs.push(password);
                await runUserSessionHelper(helperArgs, 90000);
                stdout = fs.existsSync(stdoutPath) ? fs.readFileSync(stdoutPath, 'utf8') : '';
                stderr = fs.existsSync(stderrPath) ? fs.readFileSync(stderrPath, 'utf8') : '';
            }
        } finally {
            try { fs.rmSync(stdoutPath, { force: true }); } catch { /* ignore */ }
            try { fs.rmSync(stderrPath, { force: true }); } catch { /* ignore */ }
        }
    } catch (err) {
        stdout = normalizeWslText(err.stdout || '');
        stderr = normalizeWslText(err.stderr || err.message || '');
    }

    const normalizedStderr = normalizeWslText(stderr);
    if (normalizedStderr && normalizedStderr.trim()) {
        logFn(`WSL mount stderr: ${normalizedStderr.trim().slice(0, 800)}`, 'info');
    }

    const parsed = parseTrailingJson(stdout);
    if (!parsed) {
        return { error: `WSL mount script returned no JSON. stderr=${normalizedStderr.slice(0, 400)} stdout=${normalizeWslText(stdout).slice(0, 400)}` };
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
    const validated = await validateWslMountTarget(wslTarget, parsed.device, parsed.fsType, mountNamespace);
    if (!validated.ok) {
        return {
            error: `WSL mount validation failed after mount: ${validated.error}. MacMount refused to expose a stale folder as a Windows drive.`
        };
    }

    const uncPath = `\\\\wsl.localhost\\${WSL_DISTRO}${wslTarget.replace(/\//g, '\\')}`;
    const shouldMapDriveLetter = options.mapDriveLetter !== false;
    const presentationPath = shouldMapDriveLetter ? await createLocalPresentationLink(id, uncPath, logFn) : null;

    // Legacy fallback mapping. The preferred commercial presentation is now a
    // WinFsp passthrough mounted by the broker; this path remains as a safety net.
    const driveLetter = shouldMapDriveLetter ? await substMapDriveLetter(presentationPath || uncPath, logFn) : null;

    logFn(`WSL mount: drive ${id} now at ${uncPath}${driveLetter ? ` (mapped to ${driveLetter}:)` : ''} (fs=${parsed.fsType})`, 'success');

    return {
        uncPath,
        presentationPath,
        wslTarget,
        driveLetter,                 // null if subst failed
        fsType: parsed.fsType,
        device: parsed.device,
        totalBytes: Number(validated.totalBytes) || Number(parsed.totalBytes) || 0,
        freeBytes: Number(validated.freeBytes) || Number(parsed.freeBytes) || 0,
        mountNamespace,
        mountType: 'wsl_kernel'
    };
}

/**
 * Unmount + detach the drive. Idempotent.
 * mountInfo: { wslTarget, driveLetter? } — driveLetter is the subst letter to release.
 */
async function wslUnmountDrive(driveId, wslTargetOrInfo, logFn = () => {}) {
    const id = String(driveId).trim();
    let wslTarget, driveLetter, presentationPath;
    if (typeof wslTargetOrInfo === 'object' && wslTargetOrInfo !== null) {
        wslTarget = wslTargetOrInfo.wslTarget;
        driveLetter = wslTargetOrInfo.driveLetter;
        presentationPath = wslTargetOrInfo.presentationPath;
    } else {
        wslTarget = wslTargetOrInfo;
    }

    // Release the subst drive letter first so Explorer stops holding the UNC path open.
    if (driveLetter) {
        await substRemoveDriveLetter(driveLetter, logFn);
    }
    removeLocalPresentationLink(presentationPath);

    if (wslTarget) {
        try {
            const { stdout } = await runWsl(
                ['-d', WSL_DISTRO, '-u', 'root', '--', 'bash', WSL_UNMOUNT_SCRIPT_LINUX, wslTarget],
                { timeout: 30000 }
            );
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

let _wslKeepAliveStarted = false;
let _wslKeepAlivePromise = null;
let _wslElevatedKeepAliveStarted = false;
let _wslElevatedKeepAlivePromise = null;
let _wslKeepAliveProc = null;

/**
 * Start a keep-alive loop *inside* WSL, then let wsl.exe exit immediately.
 * Keeping a detached Windows-side wsl.exe alive can create a visible terminal
 * window on some Windows Terminal/WSL builds. This short hidden bootstrap keeps
 * the VM active without any persistent Windows console process.
 */
async function ensureWslKeepAlive(logFn = () => {}, mountNamespace = 'user') {
    if (mountNamespace === 'elevated') {
        if (_wslElevatedKeepAliveStarted) return true;
        if (_wslElevatedKeepAlivePromise) return await _wslElevatedKeepAlivePromise;

        _wslElevatedKeepAlivePromise = runWsl([
            '-d', WSL_DISTRO, '-u', 'root', '--', 'bash', '-lc',
            "pgrep -f macmount-elevated-keepalive >/dev/null 2>&1 || nohup bash -lc 'exec -a macmount-elevated-keepalive sleep 2147483647' >/dev/null 2>&1 &"
        ], { timeout: 10000, maxBuffer: 65536 }).then(() => {
            _wslElevatedKeepAliveStarted = true;
            return true;
        }).catch((err) => {
            logFn(`Elevated WSL keep-alive failed to start: ${String(err.message || err).slice(0, 200)}`, 'warning');
            _wslElevatedKeepAliveStarted = false;
            return false;
        }).finally(() => {
            _wslElevatedKeepAlivePromise = null;
        });
        return await _wslElevatedKeepAlivePromise;
    }

    if (_wslKeepAliveStarted) return true;
    if (_wslKeepAlivePromise) return await _wslKeepAlivePromise;

    _wslKeepAlivePromise = runUserSessionHelper(['wslkeepalive', 'M'], 10000).then(() => {
        _wslKeepAliveStarted = true;
        return true;
    }).catch((err) => {
        logFn(`WSL keep-alive failed to start: ${String(err.message || err).slice(0, 200)}`, 'warning');
        _wslKeepAliveStarted = false;
        return false;
    }).finally(() => {
        _wslKeepAlivePromise = null;
    });
    return await _wslKeepAlivePromise;
}

async function verifyWslMountStillAlive(mountInfo) {
    if (!mountInfo?.wslTarget || !mountInfo?.device || !mountInfo?.fsType) {
        return { ok: false, error: 'mount metadata missing' };
    }
    return await validateWslMountTarget(mountInfo.wslTarget, mountInfo.device, mountInfo.fsType, mountInfo.mountNamespace);
}

function stopWslKeepAlive() {
    _wslKeepAliveStarted = false;
    _wslElevatedKeepAliveStarted = false;
    if (_wslKeepAliveProc && !_wslKeepAliveProc.killed) {
        try { _wslKeepAliveProc.kill(); } catch { /* ignore */ }
        _wslKeepAliveProc = null;
    }
    runWsl([
        '-d', WSL_DISTRO, '-u', 'root', '--', 'sh', '-lc',
        'pkill -f macmount-keepalive 2>/dev/null || true; pkill -f macmount-elevated-keepalive 2>/dev/null || true; rm -f /tmp/macmount/keepalive.pid'
    ], { timeout: 5000, maxBuffer: 65536 }).catch(() => {});
}

/**
 * Quick health check — verify WSL2 is up, Ubuntu present, custom kernel loaded.
 */
async function checkWslHealth() {
    try {
        const { stdout } = await runWsl(['-d', WSL_DISTRO, '--', 'uname', '-r'], {
            timeout: 15000,
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
    verifyWslMountStillAlive,
    stopWslKeepAlive,
    findFreeDriveLetter,
    substMapDriveLetter
};
