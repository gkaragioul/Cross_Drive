const { exec } = require('child_process');
const { promisify } = require('util');
const fs = require('fs');
const path = require('path');

const execAsync = promisify(exec);

async function mapDriveLetterInUserSession(letter, mapScriptPath, logFn) {
    // WinFsp mounts in the elevated session; Explorer runs non-elevated.
    // scripts/map-drive-user-session.ps1 maps the same NT device into the
    // interactive user's session (correct DOMAIN\\user principal + SHChangeNotify)
    // via the GUI-subsystem helper, so no console window flashes.
    const L = String(letter || '').trim().toUpperCase().replace(':', '');
    if (!/^[A-Z]$/.test(L)) return false;
    const scriptPath = String(mapScriptPath || '').trim();
    if (!scriptPath || !fs.existsSync(scriptPath)) {
        logFn?.(`User-session map skipped: script missing (${scriptPath || 'unset'})`, 'warning');
        return false;
    }
    try {
        await execAsync(
        `powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "${scriptPath}" -Letter "${L}"`,
        { timeout: 60000, windowsHide: true }
        );
        logFn?.(`User-session drive map completed for ${L}: (Explorer should list this PC).`, 'info');
        return true;
    } catch (error) {
        const tail = (() => {
            try {
                const logFile = path.join(process.env.ProgramData || 'C:\\ProgramData', 'CrossDrive', 'user-session-map.log');
                if (fs.existsSync(logFile)) {
                    const lines = fs.readFileSync(logFile, 'utf8').trim().split(/\r?\n/);
                    return lines.slice(-6).join(' | ');
                }
            }
            catch { /* ignore */ }
            return '';
        })();
        logFn?.(
            `User-session drive map failed for ${L}: ${error.message || error}. ${tail ? `Log tail: ${tail}` : 'See C:\\ProgramData\\CrossDrive\\user-session-map.log'}`,
            'warning'
        );
        return false;
    }
}

function syncAssignedLetter(driveId, letter = null) {
    const resolvedDriveId = String(driveId || '').trim();
    if (!/^\d+$/.test(resolvedDriveId)) return;

    const regBase = 'HKCU:\\Software\\CrossDrive\\DriveMap';
    let script = `$regBase = '${regBase}'; `;

    if (letter === null || letter === undefined || String(letter).trim() === '') {
        script += `if (Test-Path $regBase) { Remove-ItemProperty -Path $regBase -Name 'Drive${resolvedDriveId}' -ErrorAction SilentlyContinue }`;
    } else {
        const resolvedLetter = String(letter).trim().toUpperCase().replace(':', '');
        if (!/^[A-Z]$/.test(resolvedLetter)) return;

        script += [
            `if (-not (Test-Path $regBase)) { New-Item -Path $regBase -Force | Out-Null }`,
            `Set-ItemProperty -Path $regBase -Name 'Drive${resolvedDriveId}' -Value '${resolvedLetter}'`
        ].join('; ');
    }

    // Fire-and-forget — registry writes are best-effort state persistence and
    // must not block the mount/unmount HTTP response.
    exec(`powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "${script}"`, {
        timeout: 10000,
        windowsHide: true
    }, () => {});
}

module.exports = function mountMountRoutes(app, ctx) {
    const {
        addLog, inFlightOps, nativeMountState,
        tryMountRawWithFallbackLetters, sendBrokerRequest,
        RUNTIME_MOUNT_MODE, RUNTIME_NATIVE_MOUNT_ENABLED,
        PS_PATH, MAP_USER_SESSION_PS_PATH, hasRawDiskAccess, cleanupGhostDriveLetters, cleanupSingleDriveLetter
    } = ctx;

    app.post('/api/mount', async (req, res) => {
        const { id, password } = req.body || {};
        const driveId = String(id || '').trim();
        if (!/^\d+$/.test(driveId)) {
            return res.status(400).json({ error: 'Invalid drive id.' });
        }
        const opKey = `mount:${id}`;
        if (inFlightOps.has(opKey)) {
            return res.status(429).json({ error: 'Mount already in progress for this drive.' });
        }
        inFlightOps.add(opKey);
        addLog(`USER ACTION: Requesting mount for Physical Drive ${driveId}`);
        try {
            if (!hasRawDiskAccess?.()) {
                try { cleanupGhostDriveLetters?.(); } catch {}
                return res.status(403).json({
                    error: 'Administrator privileges are required for raw disk access.',
                    suggestion: 'Restart CrossDrive as Administrator so it can open physical drives and mount them properly.',
                    requiresAdmin: true,
                    mode: RUNTIME_MOUNT_MODE
                });
            }

            addLog(`Mount rollout: attempting native flow for drive ${driveId} (mode=${RUNTIME_MOUNT_MODE}).`);
            const physicalDrivePath = `\\\\.\\PHYSICALDRIVE${driveId}`;

            const nativeResult = await tryMountRawWithFallbackLetters(
                driveId, '', '', 0, 0, physicalDrivePath, password
            );

            if (nativeResult.ok) {
                const resolvedLetter = String(nativeResult.letter || '').trim().toUpperCase().replace(':', '');
                if (/^[A-Z]$/.test(resolvedLetter)) {
                    nativeMountState.set(String(driveId), { letter: resolvedLetter });
                    try {
                        syncAssignedLetter(driveId, resolvedLetter);
                    } catch (e) {
                        addLog(`Native mount state persistence warning for drive ${driveId}: ${e.message}`, 'warning');
                    }
                    // Map drive letter in non-elevated user session so Explorer can see it
                    try {
                        mapDriveLetterInUserSession(resolvedLetter, MAP_USER_SESSION_PS_PATH, addLog).catch((e) => {
                            addLog(`User session drive map warning: ${e.message}`, 'warning');
                        });
                    } catch (e) {
                        addLog(`User session drive map warning: ${e.message}`, 'warning');
                    }
                }
                return res.json({
                    success: true,
                    path: /^[A-Z]$/.test(resolvedLetter) ? `${resolvedLetter}:\\` : '',
                    driveLetter: /^[A-Z]$/.test(resolvedLetter) ? resolvedLetter : undefined,
                    mountType: nativeResult.result?.mountType || 'native_raw',
                    mode: RUNTIME_MOUNT_MODE
                });
            }

            addLog(`Native rollout mount failed for drive ${driveId}: ${nativeResult.error || 'unknown error'}`, 'warning');
            const analyzedPlan = nativeResult.analysis?.plan || null;
            const analyzedFsType = String(analyzedPlan?.FileSystemType || '').trim();
            const isApfsPlan = /^APFS$/i.test(analyzedFsType);
            const isCoreStoragePlan = /^CoreStorage$/i.test(analyzedFsType);
            const isEncryptedHfsPlan = analyzedPlan?.IsEncrypted === true && (/^HFS\+$/i.test(analyzedFsType) || /^HFSX$/i.test(analyzedFsType));
            const isPasswordRequired = nativeResult.needsPassword === true && !password;
            const isHardwareBound = nativeResult.hardwareBound === true || analyzedPlan?.HardwareBound === true;

            if (isHardwareBound) {
                return res.status(501).json({
                    error: 'This drive is encrypted with hardware-bound keys (T2 chip or Apple Silicon Secure Enclave) and cannot be unlocked on Windows.',
                    suggestion: 'Connect to the original Mac and run `diskutil apfs decryptVolume` first, then retry.',
                    hardwareBound: true,
                    analysis: nativeResult.analysis || null,
                    mode: RUNTIME_MOUNT_MODE
                });
            }

            if (isPasswordRequired) {
                return res.status(409).json({
                    error: nativeResult.error || 'Encrypted APFS volume requires a password.',
                    needsPassword: true,
                    suggestion: nativeResult.suggestion || 'Enter the disk password and retry.',
                    analysis: nativeResult.analysis || null,
                    mode: RUNTIME_MOUNT_MODE
                });
            }

            if (isCoreStoragePlan) {
                return res.status(501).json({
                    error: 'CoreStorage/FileVault unlock is not implemented yet.',
                    suggestion: 'This drive was detected as CoreStorage. CrossDrive cannot open CoreStorage volumes yet.',
                    analysis: nativeResult.analysis || null,
                    mode: RUNTIME_MOUNT_MODE
                });
            }

            if (isEncryptedHfsPlan) {
                return res.status(501).json({
                    error: 'Encrypted HFS/CoreStorage-style volumes are detected but cannot be unlocked by the native HFS provider yet.',
                    suggestion: 'Use a Mac to decrypt or convert the drive to an unencrypted external volume, then retry.',
                    analysis: nativeResult.analysis || null,
                    mode: RUNTIME_MOUNT_MODE
                });
            }

            return res.status(502).json({
                error: nativeResult.error || (isApfsPlan ? 'Native APFS mount failed.' : 'Native mount failed.'),
                details: nativeResult.error || 'unknown native mount error',
                analysis: nativeResult.analysis || null,
                needsPassword: nativeResult.needsPassword === true,
                suggestion: nativeResult.suggestion || (
                    isApfsPlan
                        ? 'Native APFS support needs to handle this volume case before CrossDrive can mount it.'
                        : (analyzedFsType
                            ? `Detected filesystem: ${analyzedFsType}. CrossDrive needs native support for this volume case before it can mount it.`
                            : 'CrossDrive needs native support for this volume case before it can mount it.')
                ),
                mode: RUNTIME_MOUNT_MODE
            });
        } catch (e) {
            return res.status(500).json({ error: e.message || 'System execution failure.' });
        } finally {
            inFlightOps.delete(opKey);
            ctx.invalidateDriveCache?.();
        }
    });

    app.post('/api/unmount', async (req, res) => {
        const { id } = req.body;
        const driveId = String(id || '').trim();
        const mountInfo = nativeMountState.get(driveId);
        const rememberedLetter = String(mountInfo?.letter || '').trim().toUpperCase().replace(':', '');
        const opKey = `unmount:${id}`;
        if (inFlightOps.has(opKey)) {
            return res.status(429).json({ error: 'Unmount already in progress for this drive.' });
        }
        inFlightOps.add(opKey);
        addLog(`USER ACTION: Requesting unmount for Physical Drive ${id}`);

        try {
            // 1. Broker unmount first — awaited so it completes before PS tears down the mount point
            if (RUNTIME_NATIVE_MOUNT_ENABLED && nativeMountState.has(driveId)) {
                try {
                    const r = await sendBrokerRequest({
                        action: 'unmount',
                        requestId: String(Date.now()),
                        driveId
                    }, 10000);
                    if (r?.ok) addLog(`Native raw unmount complete for drive ${id}`, 'info');
                    else addLog(`Native raw unmount warning for drive ${id}: ${r?.error || 'unknown'}`, 'warning');
                } catch (e) {
                    addLog(`Native raw unmount warning for drive ${id}: ${e.message}`, 'warning');
                }
                nativeMountState.delete(driveId);
            }

            // 2. PowerShell unmount (runs after broker has finished)
            let stdout = '';
            let stderr = '';
            try {
                ({ stdout, stderr } = await execAsync(
                    `powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "${PS_PATH}" -Action Unmount -DriveID ${id}`,
                    { timeout: 30000, windowsHide: true }
                ));
            } catch (execError) {
                if (execError.stderr) addLog(`PS Unmount Info: ${execError.stderr}`, 'info');
                addLog(`PS Unmount Error: ${execError.message}`, 'error');
                if (/^\d+$/.test(driveId)) {
                    try { syncAssignedLetter(driveId, null); } catch {}
                }
                // The broker unmount above already removed the WinFsp host, so the
                // user-session drive map is now a ghost letter. Clean it up
                // explicitly — otherwise Explorer keeps showing a dead drive
                // until the next session restart.
                if (/^[A-Z]$/.test(rememberedLetter)) {
                    try { cleanupSingleDriveLetter(rememberedLetter); } catch {}
                }
                return res.status(500).json({ error: execError.message });
            }

            if (stderr) addLog(`PS Unmount Info: ${stderr}`, 'info');

            // 3. Clear registry state after successful unmount
            if (/^\d+$/.test(driveId)) {
                try {
                    syncAssignedLetter(driveId, null);
                } catch (e) {
                    addLog(`Native unmount state cleanup warning for drive ${driveId}: ${e.message}`, 'warning');
                }
            }

            // 4. Clean up the drive letter from the user session immediately
            if (/^[A-Z]$/.test(rememberedLetter)) {
                try { cleanupSingleDriveLetter(rememberedLetter); } catch {}
            }

            // 5. Parse and return result
            try {
                const jsonMatch = stdout.match(/\{[\s\S]*\}/);
                const result = jsonMatch ? JSON.parse(jsonMatch[0]) : JSON.parse(stdout);
                addLog(`Drive ${id} unmounted successfully.`, 'success');
                return res.json(result);
            } catch {
                return res.json({ success: true });
            }
        } catch (e) {
            return res.status(500).json({ error: e.message || 'System execution failure.' });
        } finally {
            inFlightOps.delete(opKey);
            ctx.invalidateDriveCache?.();
        }
    });
};
