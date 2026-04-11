const { exec, execSync } = require('child_process');
const { promisify } = require('util');
const fs = require('fs');
const path = require('path');

const execAsync = promisify(exec);

function mapDriveLetterInUserSession(letter, mapScriptPath, logFn) {
    // WinFsp mounts in the elevated session; Explorer runs non-elevated.
    // scripts/map-drive-user-session.ps1 maps the same NT device into the
    // interactive user's session (correct DOMAIN\\user principal + SHChangeNotify).
    const L = String(letter || '').trim().toUpperCase().replace(':', '');
    if (!/^[A-Z]$/.test(L)) return;
    const scriptPath = String(mapScriptPath || '').trim();
    if (!scriptPath || !fs.existsSync(scriptPath)) {
        logFn?.(`User-session map skipped: script missing (${scriptPath || 'unset'})`, 'warning');
        return;
    }
    try {
        execSync(
            `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${scriptPath}" -Letter "${L}"`,
            { timeout: 60000, windowsHide: true, stdio: 'ignore' }
        );
        logFn?.(`User-session drive map completed for ${L}: (Explorer should list this PC).`, 'info');
    } catch (e) {
        const tail = (() => {
            try {
                const logFile = path.join(process.env.ProgramData || 'C:\\ProgramData', 'MacMount', 'user-session-map.log');
                if (fs.existsSync(logFile)) {
                    const lines = fs.readFileSync(logFile, 'utf8').trim().split(/\r?\n/);
                    return lines.slice(-6).join(' | ');
                }
            } catch { /* ignore */ }
            return '';
        })();
        logFn?.(
            `User-session drive map failed for ${L}: ${e.message || e}. ${tail ? `Log tail: ${tail}` : 'See C:\\ProgramData\\MacMount\\user-session-map.log'}`,
            'warning'
        );
    }
}

function syncAssignedLetter(driveId, letter = null) {
    const resolvedDriveId = String(driveId || '').trim();
    if (!/^\d+$/.test(resolvedDriveId)) return;

    const regBase = 'HKCU:\\Software\\MacMount\\DriveMap';
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

    execSync(`powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "${script}"`, {
        timeout: 10000,
        windowsHide: true,
        stdio: ['ignore', 'ignore', 'ignore']
    });
}

module.exports = function mountMountRoutes(app, ctx) {
    const {
        addLog, inFlightOps, nativeMountState,
        shouldAttemptNativeMountForDrive, tryMountRawWithFallbackLetters, execPsMount, sendBrokerRequest,
        RUNTIME_MOUNT_MODE, RUNTIME_NATIVE_MOUNT_ENABLED, RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK,
        PS_PATH, MAP_USER_SESSION_PS_PATH, hasRawDiskAccess, cleanupGhostDriveLetters
    } = ctx;

    app.post('/api/mount', async (req, res) => {
        const { id, password, forceNative } = req.body || {};
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
            const attemptNative = shouldAttemptNativeMountForDrive(driveId, forceNative === true);
            if (attemptNative) {
                if (!hasRawDiskAccess?.()) {
                    try { cleanupGhostDriveLetters?.(); } catch {}
                    return res.status(403).json({
                        error: 'Administrator privileges are required for raw disk access.',
                        suggestion: 'Restart MacMount as Administrator so it can open physical drives and mount them properly.',
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
                            mapDriveLetterInUserSession(resolvedLetter, MAP_USER_SESSION_PS_PATH, addLog);
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
                const isPasswordRequired = nativeResult.needsPassword === true && !password;

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
                        suggestion: 'This drive was detected as CoreStorage. Native APFS fallback cannot open it yet.',
                        analysis: nativeResult.analysis || null,
                        mode: RUNTIME_MOUNT_MODE
                    });
                }

                if (!RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK) {
                    return res.status(502).json({
                        error: 'Native mount failed and fallback is disabled.',
                        details: nativeResult.error || 'unknown native mount error',
                        analysis: nativeResult.analysis || null,
                        needsPassword: nativeResult.needsPassword === true,
                        suggestion: nativeResult.suggestion || '',
                        mode: RUNTIME_MOUNT_MODE
                    });
                }

                if (!isApfsPlan) {
                    return res.status(502).json({
                        error: nativeResult.error || 'Native mount failed.',
                        suggestion: analyzedFsType
                            ? `Fallback is only available for APFS right now. Detected filesystem: ${analyzedFsType}.`
                            : 'Fallback is only available for APFS right now.',
                        analysis: nativeResult.analysis || null,
                        mode: RUNTIME_MOUNT_MODE
                    });
                }

                addLog(`Mount rollout fallback: using compatibility script mount for drive ${driveId}.`, 'warning');
                const fallbackResult = await execPsMount(driveId, password, false);
                if (fallbackResult?.error) {
                    addLog(`Engine Error: ${fallbackResult.error}`, 'error');
                    return res.status(500).json({
                        ...fallbackResult,
                        nativeAttemptError: nativeResult.error || 'unknown native mount error',
                        nativeAttemptAnalysis: nativeResult.analysis || null,
                        nativeAttemptNeedsPassword: nativeResult.needsPassword === true,
                        nativeAttemptSuggestion: nativeResult.suggestion || '',
                        mode: RUNTIME_MOUNT_MODE,
                        fallbackUsed: true
                    });
                }

                return res.json({
                    success: true,
                    path: fallbackResult.path,
                    driveLetter: fallbackResult.driveLetter,
                    mountType: fallbackResult.mountType || 'native_winfsp',
                    mode: RUNTIME_MOUNT_MODE,
                    fallbackUsed: true
                });
            }

            const result = await execPsMount(driveId, password, false);
            if (result?.error) {
                addLog(`Engine Error: ${result.error}`, 'error');
                return res.status(500).json(result);
            }

            return res.json({
                success: true,
                path: result.path,
                driveLetter: result.driveLetter,
                mountType: result.mountType || 'native_winfsp',
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
        const rememberedLetter = String(nativeMountState.get(driveId)?.letter || '').trim().toUpperCase().replace(':', '');
        const opKey = `unmount:${id}`;
        if (inFlightOps.has(opKey)) {
            return res.status(429).json({ error: 'Unmount already in progress for this drive.' });
        }
        inFlightOps.add(opKey);
        addLog(`USER ACTION: Requesting unmount for Physical Drive ${id}`);

        try {
            // Record state before clearing (preserve existing behavior)
            if (/^\d+$/.test(driveId) && /^[A-Z]$/.test(rememberedLetter)) {
                try {
                    syncAssignedLetter(driveId, rememberedLetter);
                } catch (e) {
                    addLog(`Native unmount state persistence warning for drive ${driveId}: ${e.message}`, 'warning');
                }
            }

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

            // 4. Parse and return result
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
