const { exec } = require('child_process');
const { promisify } = require('util');
const fs = require('fs');
const path = require('path');
const { wslMountDrive, wslUnmountDrive, verifyWslMountStillAlive, checkWslKeepAliveAlive, findFreeDriveLetter, substMapDriveLetter } = require('../scripts/wslMountClient');

const execAsync = promisify(exec);
const readCrossDriveEnv = (name, fallbackName) => process.env[name] ?? process.env[fallbackName];
const ENABLE_WSL_WINFSP_PRESENTATION = readCrossDriveEnv('CROSSDRIVE_DISABLE_WSL_WINFSP', 'MACMOUNT_DISABLE_WSL_WINFSP') !== '1';
const ENABLE_WSL_DRIVE_LETTER = readCrossDriveEnv('CROSSDRIVE_ENABLE_WSL_DRIVE_LETTER', 'MACMOUNT_ENABLE_WSL_DRIVE_LETTER') !== '0';

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

async function mountWslThroughWinFsp({ driveId, wslResult, ensureBrokerReady, sendBrokerRequest, addLog }) {
    if (!ENABLE_WSL_WINFSP_PRESENTATION) {
        throw new Error('WinFsp WSL passthrough is disabled while WSL mount stability is being validated.');
    }

    if (typeof sendBrokerRequest !== 'function') {
        throw new Error('Native broker IPC is unavailable.');
    }

    if (typeof ensureBrokerReady === 'function') {
        const ready = await ensureBrokerReady(8, true);
        if (!ready) {
            throw new Error('Native broker is unavailable or not elevated.');
        }
    }

    const letter = await findFreeDriveLetter();
    if (!letter) {
        throw new Error('No free drive letter is available.');
    }

    const result = await sendBrokerRequest({
        action: 'mount_passthrough',
        requestId: String(Date.now()),
        driveId: String(driveId),
        letter,
        sourcePath: String(wslResult.uncPath),
        totalBytes: Number(wslResult.totalBytes) || 0,
        freeBytes: Number(wslResult.freeBytes) || 0
    }, 20000);

    if (!result?.ok) {
        throw new Error(result?.error || 'WinFsp passthrough mount failed.');
    }

    addLog?.(`WinFsp presentation mounted ${wslResult.uncPath} as ${letter}:`, 'success');
    return {
        driveLetter: letter,
        path: `${letter}:\\`,
        mountType: 'wsl_winfsp_passthrough'
    };
}

function startWslMountMonitor(ctx) {
    if (ctx._wslMountMonitorStarted) return;
    ctx._wslMountMonitorStarted = true;
    let running = false;
    const failureCounts = new Map();
    setInterval(async () => {
        if (running) return;
        running = true;
        try {
            for (const [driveId, mountInfo] of ctx.nativeMountState.entries()) {
                if (mountInfo?.mountType !== 'wsl_kernel') continue;
                if (!mountInfo.wslTarget || !mountInfo.device || !mountInfo.fsType) continue;

                // Check WSL keep-alive process is still running before testing individual mounts
                try {
                    const kaAlive = await checkWslKeepAliveAlive(mountInfo.mountNamespace || 'user');
                    if (!kaAlive.ok) {
                        ctx.addLog?.(
                            `WSL keep-alive check failed: ${kaAlive.error}. WSL may have shut down — all WSL mounts at risk.`,
                            'warning'
                        );
                    }
                } catch (kaErr) {
                    ctx.addLog?.(`WSL keep-alive check exception: ${kaErr.message}`, 'debug');
                }

                const health = await verifyWslMountStillAlive(mountInfo);
                if (health.ok) {
                    failureCounts.delete(driveId);
                    if (mountInfo.brokerPassthrough === true && health.totalBytes > 0) {
                        try {
                            await ctx.sendBrokerRequest?.({
                                action: 'update_volume_info',
                                requestId: String(Date.now()),
                                driveId,
                                totalBytes: Number(health.totalBytes) || 0,
                                freeBytes: Number(health.freeBytes) || 0
                            }, 10000);
                            mountInfo.totalBytes = Number(health.totalBytes) || mountInfo.totalBytes;
                            mountInfo.freeBytes = Number(health.freeBytes) || 0;
                            mountInfo.size = mountInfo.totalBytes ? `${(mountInfo.totalBytes / (1024 ** 3)).toFixed(2)} GB` : mountInfo.size;
                        } catch (e) {
                            ctx.addLog?.(`WinFsp volume info refresh warning for drive ${driveId}: ${e.message}`, 'warning');
                        }
                    }
                    continue;
                }

                if (health.transient === true) {
                    ctx.addLog?.(
                        `WSL mount health check transient warning for drive ${driveId}: ${String(health.error || 'unknown').slice(0, 300)}. Keeping Windows drive mounted.`,
                        'warning'
                    );
                    continue;
                }

                const failures = (failureCounts.get(driveId) || 0) + 1;
                failureCounts.set(driveId, failures);
                if (failures < 4) {
                    ctx.addLog?.(
                        `WSL mount health check warning for drive ${driveId} (${failures}/4): ${String(health.error || 'unknown').slice(0, 300)}. Keeping Windows drive mounted while retrying.`,
                        'warning'
                    );
                    continue;
                }

                ctx.addLog?.(
                    `WSL mount health failed for drive ${driveId} after ${failures} consecutive checks: ${health.error}. Unmounting Windows presentation so writes cannot land in a stale WSL folder.`,
                    'error'
                );
                if (mountInfo.brokerPassthrough === true) {
                    try {
                        await ctx.sendBrokerRequest?.({
                            action: 'unmount',
                            requestId: String(Date.now()),
                            driveId
                        }, 10000);
                    } catch (e) {
                        ctx.addLog?.(`WinFsp health cleanup warning for drive ${driveId}: ${e.message}`, 'warning');
                    }
                    try { ctx.cleanupSingleDriveLetter?.(mountInfo.driveLetter); } catch (e) {
                        ctx.addLog?.(`Health cleanup letter removal failed for ${mountInfo.driveLetter}: ${e.message}`, 'debug');
                    }
                } else {
                    try {
                        await wslUnmountDrive(driveId, mountInfo, ctx.addLog);
                    } catch (e) {
                        ctx.addLog?.(`WSL health cleanup warning for drive ${driveId}: ${e.message}`, 'warning');
                    }
                }
                ctx.nativeMountState.delete(driveId);
                ctx.invalidateDriveCache?.();
            }
        } finally {
            running = false;
        }
    }, 15000).unref?.();
}

module.exports = function mountMountRoutes(app, ctx) {
    const {
        addLog, inFlightOps, nativeMountState,
        shouldAttemptNativeMountForDrive, tryMountRawWithFallbackLetters, execPsMount, sendBrokerRequest, ensureBrokerReady,
        RUNTIME_MOUNT_MODE, RUNTIME_NATIVE_MOUNT_ENABLED, RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK,
        PS_PATH, MAP_USER_SESSION_PS_PATH, hasRawDiskAccess, cleanupGhostDriveLetters, cleanupSingleDriveLetter
    } = ctx;
    startWslMountMonitor(ctx);

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
            // Primary path: WSL2-backed mount via Linux kernel hfsplus / apfs-rw drivers.
            // Skipped if the caller explicitly forces the native engine.
            if (forceNative !== true) {
                if (!hasRawDiskAccess?.()) {
                    return res.status(403).json({
                        error: 'Administrator privileges are required to attach a physical drive to WSL2.',
                        suggestion: 'Restart CrossDrive as Administrator.',
                        requiresAdmin: true,
                        mode: 'wsl_kernel'
                    });
                }
                const wslResult = await wslMountDrive(driveId, password, addLog, { mapDriveLetter: false, mountNamespace: 'elevated' });
                if (!wslResult.error) {
                    let presentation = null;
                    try {
                        presentation = await mountWslThroughWinFsp({
                            driveId,
                            wslResult,
                            ensureBrokerReady,
                            sendBrokerRequest,
                            addLog
                        });
                    } catch (presentationError) {
                        addLog(`WinFsp presentation failed for drive ${driveId}: ${presentationError.message}. Direct WSL fallback is disabled for elevated WSL mounts to avoid exposing a stale namespace.`, 'error');
                        try {
                            await wslUnmountDrive(driveId, {
                                wslTarget: wslResult.wslTarget,
                                presentationPath: wslResult.presentationPath,
                                driveLetter: null
                            }, addLog);
                        } catch (e) {
                            addLog(`WSL cleanup after WinFsp failure warning for drive ${driveId}: ${e.message}`, 'warning');
                        }
                        return res.status(502).json({
                            error: presentationError.message || 'WinFsp presentation failed.',
                            suggestion: 'CrossDrive could mount the Linux filesystem but could not expose it as a local Windows drive. Direct WSL fallback was intentionally skipped because it would use the wrong namespace.',
                            mode: 'wsl_kernel'
                        });
                    }

                    if (presentation.mountType === 'wsl_winfsp_passthrough' && presentation.driveLetter) {
                        try {
                            const mappedForExplorer = await mapDriveLetterInUserSession(presentation.driveLetter, MAP_USER_SESSION_PS_PATH, addLog);
                            if (!mappedForExplorer) {
                                addLog(`WinFsp mounted ${presentation.driveLetter}: but Explorer session mapping did not complete; falling back to WSL network mapping.`, 'warning');
                                try {
                                    await sendBrokerRequest({
                                        action: 'unmount',
                                        requestId: String(Date.now()),
                                        driveId
                                    }, 10000);
                                } catch (e) {
                                    addLog(`WinFsp fallback cleanup warning for drive ${driveId}: ${e.message}`, 'warning');
                                }
                                const fallbackLetter = ENABLE_WSL_DRIVE_LETTER ? await substMapDriveLetter(wslResult.uncPath, addLog) : null;
                                presentation = fallbackLetter
                                    ? { driveLetter: fallbackLetter, path: `${fallbackLetter}:\\`, mountType: 'wsl_network_fallback' }
                                    : { driveLetter: null, path: wslResult.uncPath, mountType: 'wsl_unc_fallback' };
                            }
                        } catch (e) {
                            addLog(`User-session WinFsp map warning for drive ${driveId}: ${e.message}`, 'warning');
                        }
                    }

                    await new Promise((resolve) => setTimeout(resolve, 5000));
                    const finalHealth = await verifyWslMountStillAlive({
                        wslTarget: wslResult.wslTarget,
                        device: wslResult.device,
                        fsType: wslResult.fsType,
                        mountNamespace: wslResult.mountNamespace
                    });
                    if (!finalHealth.ok) {
                        addLog(
                            `WSL mount vanished before presentation completed for drive ${driveId}: ${finalHealth.error}. Refusing to expose ${presentation.driveLetter || wslResult.uncPath}.`,
                            'error'
                        );
                        if (presentation.mountType === 'wsl_winfsp_passthrough') {
                            try {
                                await sendBrokerRequest({
                                    action: 'unmount',
                                    requestId: String(Date.now()),
                                    driveId
                                }, 10000);
                            } catch (e) {
                                addLog(`WinFsp final-validation cleanup warning for drive ${driveId}: ${e.message}`, 'warning');
                            }
                            try { cleanupSingleDriveLetter(presentation.driveLetter); } catch {}
                        }
                        try {
                            await wslUnmountDrive(driveId, {
                                wslTarget: wslResult.wslTarget,
                                presentationPath: wslResult.presentationPath,
                                driveLetter: presentation.mountType === 'wsl_winfsp_passthrough' ? null : presentation.driveLetter
                            }, addLog);
                        } catch (e) {
                            addLog(`WSL final-validation cleanup warning for drive ${driveId}: ${e.message}`, 'warning');
                        }

                        return res.status(502).json({
                            error: `WSL mount vanished before Windows presentation completed: ${finalHealth.error}`,
                            suggestion: 'CrossDrive refused to expose a stale WSL folder as a writable Windows drive. Retry mount; if this repeats, keep the drive connected and share logs.',
                            mode: 'wsl_kernel'
                        });
                    }

                    nativeMountState.set(String(driveId), {
                        wslTarget: wslResult.wslTarget,
                        uncPath: wslResult.uncPath,
                        presentationPath: wslResult.presentationPath,
                        driveLetter: presentation.driveLetter,
                        brokerPassthrough: presentation.mountType === 'wsl_winfsp_passthrough',
                        mountType: 'wsl_kernel',
                        presentationMountType: presentation.mountType,
                        device: wslResult.device,
                        fsType: wslResult.fsType,
                        mountNamespace: wslResult.mountNamespace,
                        totalBytes: Number(wslResult.totalBytes) || 0,
                        freeBytes: Number(wslResult.freeBytes) || 0,
                        size: wslResult.totalBytes ? `${(wslResult.totalBytes / (1024 ** 3)).toFixed(2)} GB` : ''
                    });
                    const friendlyTarget = presentation.path;
                    addLog(`SUCCESS: Drive ${driveId} mounted at ${friendlyTarget}`, 'success');
                    return res.json({
                        success: true,
                        path: friendlyTarget,
                        mountPath: friendlyTarget,
                        driveLetter: presentation.driveLetter || undefined,
                        uncPath: wslResult.uncPath,
                        mountType: presentation.mountType,
                        fsType: wslResult.fsType,
                        mode: 'wsl_kernel'
                    });
                }
                addLog(`WSL mount failed for drive ${driveId}: ${wslResult.error}`, 'warning');
                if (wslResult.needsPassword === true && !password) {
                    return res.status(409).json({
                        error: wslResult.error || 'Encrypted APFS volume requires a password.',
                        needsPassword: true,
                        suggestion: 'Enter the disk password and retry.',
                        mode: 'wsl_kernel'
                    });
                }
                if (wslResult.needsAdmin === true) {
                    return res.status(403).json({
                        error: wslResult.error,
                        requiresAdmin: true,
                        mode: 'wsl_kernel'
                    });
                }
                if (RUNTIME_MOUNT_MODE === 'wsl_kernel') {
                    return res.status(502).json({
                        error: wslResult.error || 'WSL2 kernel mount failed.',
                        suggestion: 'CrossDrive uses the WSL2 kernel path for writable Mac drives. Native HFS+ write fallback is disabled by default because it is not reliable for real-world copies.',
                        mode: 'wsl_kernel'
                    });
                }

                // Debug fallback only. The legacy native writer is not the commercial path.
                addLog(`Falling back to native engine for drive ${driveId} because runtime mode is ${RUNTIME_MOUNT_MODE}.`, 'warning');
            }

            const attemptNative = shouldAttemptNativeMountForDrive(driveId, forceNative === true);
            if (attemptNative) {
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
                        suggestion: 'This drive was detected as CoreStorage. Native APFS fallback cannot open it yet.',
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
        const mountInfo = nativeMountState.get(driveId);
        const rememberedLetter = String(mountInfo?.letter || '').trim().toUpperCase().replace(':', '');
        const opKey = `unmount:${id}`;
        if (inFlightOps.has(opKey)) {
            return res.status(429).json({ error: 'Unmount already in progress for this drive.' });
        }
        inFlightOps.add(opKey);
        addLog(`USER ACTION: Requesting unmount for Physical Drive ${id}`);

        try {
            // WSL2-backed mounts: unmount via Linux + detach from WSL2.
            if (mountInfo?.mountType === 'wsl_kernel') {
                if (mountInfo.brokerPassthrough === true) {
                    try {
                        const r = await sendBrokerRequest({
                            action: 'unmount',
                            requestId: String(Date.now()),
                            driveId
                        }, 10000);
                        if (r?.ok) {
                            addLog(`WinFsp presentation unmounted for drive ${id}`, 'info');
                        } else {
                            addLog(`WinFsp presentation unmount warning for drive ${id}: ${r?.error || 'unknown error'}`, 'warning');
                        }
                    } catch (e) {
                        addLog(`WinFsp presentation unmount warning for drive ${id}: ${e.message}`, 'warning');
                    }
                    try { cleanupSingleDriveLetter(mountInfo.driveLetter); } catch {}
                }

                try {
                    await wslUnmountDrive(driveId, {
                        wslTarget: mountInfo.wslTarget,
                        presentationPath: mountInfo.presentationPath,
                        driveLetter: mountInfo.brokerPassthrough === true ? null : mountInfo.driveLetter
                    }, addLog);
                    addLog(`WSL unmount complete for drive ${id}`, 'success');
                } catch (e) {
                    addLog(`WSL unmount warning for drive ${id}: ${e.message}`, 'warning');
                }
                nativeMountState.delete(driveId);
                return res.json({ success: true, mountType: 'wsl_kernel' });
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
