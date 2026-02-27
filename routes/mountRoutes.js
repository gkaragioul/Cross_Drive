const { exec } = require('child_process');

module.exports = function mountMountRoutes(app, ctx) {
    const {
        addLog, inFlightOps, nativeMountState,
        shouldAttemptNativeMountForDrive, tryMountRawWithFallbackLetters, execPsMount, sendBrokerRequest,
        RUNTIME_MOUNT_MODE, RUNTIME_NATIVE_MOUNT_ENABLED, RUNTIME_ALLOW_WSL_FALLBACK,
        PS_PATH
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
                addLog(`Mount rollout: attempting native flow for drive ${driveId} (mode=${RUNTIME_MOUNT_MODE}).`);
                const physicalDrivePath = `\\\\.\\PHYSICALDRIVE${driveId}`;

                const nativeResult = await tryMountRawWithFallbackLetters(
                    driveId, '', '', 0, 0, physicalDrivePath
                );

                if (nativeResult.ok) {
                    const resolvedLetter = String(nativeResult.letter || '').trim().toUpperCase().replace(':', '');
                    if (/^[A-Z]$/.test(resolvedLetter)) {
                        nativeMountState.set(String(driveId), { letter: resolvedLetter });
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
                if (!RUNTIME_ALLOW_WSL_FALLBACK) {
                    return res.status(502).json({
                        error: 'Native mount failed and fallback is disabled.',
                        details: nativeResult.error || 'unknown native mount error',
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

    app.post('/api/unmount', (req, res) => {
        const { id } = req.body;
        const opKey = `unmount:${id}`;
        if (inFlightOps.has(opKey)) {
            return res.status(429).json({ error: 'Unmount already in progress for this drive.' });
        }
        inFlightOps.add(opKey);
        addLog(`USER ACTION: Requesting unmount for Physical Drive ${id}`);

        if (RUNTIME_NATIVE_MOUNT_ENABLED && nativeMountState.has(String(id))) {
            sendBrokerRequest({
                action: 'unmount',
                requestId: String(Date.now()),
                driveId: String(id)
            }, 10000)
                .then((r) => {
                    if (r?.ok) addLog(`Native raw unmount complete for drive ${id}`, 'info');
                })
                .catch((e) => addLog(`Native raw unmount warning for drive ${id}: ${e.message}`, 'warning'))
                .finally(() => nativeMountState.delete(String(id)));
        }

        exec(`powershell -ExecutionPolicy Bypass -File "${PS_PATH}" -Action Unmount -DriveID ${id}`, { timeout: 30000, windowsHide: true }, (error, stdout, stderr) => {
            if (stderr) addLog(`PS Unmount Info: ${stderr}`, 'info');
            if (error) {
                addLog(`PS Unmount Error: ${error.message}`, 'error');
                inFlightOps.delete(opKey);
                return res.status(500).json({ error: error.message });
            }
            try {
                const jsonMatch = stdout.match(/\{[\s\S]*\}/);
                const result = jsonMatch ? JSON.parse(jsonMatch[0]) : JSON.parse(stdout);
                addLog(`Drive ${id} unmounted successfully.`, 'success');
                res.json(result);
            } catch (e) {
                res.json({ success: true });
            } finally {
                inFlightOps.delete(opKey);
                ctx.invalidateDriveCache?.();
            }
        });
    });
};
