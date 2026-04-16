const { exec } = require('child_process');
const fs = require('fs');

module.exports = function mountDriveRoutes(app, ctx) {
    const { addLog, nativeMountState, getBrokerMountedMap, PREFER_SUBST_LOCAL_FAST_PATH, PS_PATH, sendNativeWithBoot, cleanupGhostDriveLetters } = ctx;

    // Cache to avoid spawning PowerShell on every 5-second poll
    let driveCache = { data: null, time: 0 };
    const analysisCache = new Map();
    const CACHE_TTL_MS = 3000;
    const ANALYSIS_CACHE_TTL_MS = 15000;
    // Expose invalidator so mount/unmount routes can force a fresh scan
    ctx.invalidateDriveCache = () => {
        driveCache = { data: null, time: 0 };
        analysisCache.clear();
    };

    async function getNativeAnalysisForDrive(driveId) {
        const cacheKey = String(driveId);
        const now = Date.now();
        const cached = analysisCache.get(cacheKey);
        if (cached && (now - cached.time) < ANALYSIS_CACHE_TTL_MS) {
            return cached.value;
        }

        if (typeof sendNativeWithBoot !== 'function') {
            return null;
        }

        try {
            const result = await sendNativeWithBoot({
                action: 'analyze_raw',
                requestId: String(Date.now()),
                physicalDrivePath: `\\\\.\\PHYSICALDRIVE${cacheKey}`
            }, 12000, 2);

            const value = result?.ok && result?.plan ? result.plan : null;
            analysisCache.set(cacheKey, { time: now, value });
            return value;
        } catch {
            analysisCache.set(cacheKey, { time: now, value: null });
            return null;
        }
    }

    app.get('/api/drives', (req, res) => {
        const now = Date.now();
        if (driveCache.data && (now - driveCache.time) < CACHE_TTL_MS) {
            return res.json(driveCache.data);
        }

        addLog("Scanning for Mac-formatted drives...");
        exec(`powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "${PS_PATH}" -Action List`, { windowsHide: true }, async (error, stdout, stderr) => {
            if (stderr) addLog(`PS Scan Output: ${stderr}`, 'warning');
            if (error) {
                addLog(`PS Scan Failure: ${error.message}`, 'error');
                return res.json({ error: "Drives could not be scanned. Admin rights might be needed." });
            }
            try {
                const data = JSON.parse(stdout);
                const macDrives = Array.isArray(data) ? data.filter(d => d.isMac) : [];
                const brokerMounted = await getBrokerMountedMap();
                const activeLetters = new Set();
                for (const mounted of brokerMounted.values()) {
                    const letter = String(mounted?.letter || '').trim().toUpperCase().replace(':', '');
                    if (/^[A-Z]$/.test(letter)) activeLetters.add(letter);
                }
                const nativePlans = new Map(
                    await Promise.all(
                        macDrives.map(async (drive) => [String(drive.id), await getNativeAnalysisForDrive(drive.id)])
                    )
                );

                for (const drive of macDrives) {
                    const id = String(drive.id);
                    const plan = nativePlans.get(id) || null;
                    if (plan) {
                        const fsType = String(plan.FileSystemType || '').trim();
                        if (fsType) {
                            drive.format = fsType;
                        }
                        drive.isEncrypted = plan.IsEncrypted === true;
                        drive.needsPassword = plan.NeedsPassword === true;
                        drive.hardwareBound = plan.HardwareBound === true;
                        drive.analysisNotes = String(plan.Notes || '');
                        drive.supported =
                            /^APFS$/i.test(fsType) ||
                            /^HFS\+$/i.test(fsType) ||
                            /^HFSX$/i.test(fsType);

                        if (drive.hardwareBound) {
                            // T2 / Apple Silicon — keys live in the Mac's Secure Enclave; nothing
                            // we can do about it on Windows. Mark unsupported so the mount button
                            // disables and the UI shows a clear message.
                            drive.supported = false;
                            drive.mountHint = 'Hardware-bound encryption (T2 chip / Apple Silicon). Decrypt on the original Mac first.';
                        } else if (/^CoreStorage$/i.test(fsType)) {
                            drive.supported = false;
                            drive.mountHint = 'CoreStorage/FileVault unlock is not implemented yet.';
                        } else if (drive.needsPassword) {
                            drive.mountHint = 'Encrypted volume. Password required to unlock.';
                        } else if (drive.supported) {
                            drive.mountHint = '';
                        } else if (fsType) {
                            drive.mountHint = `Detected ${fsType}. Native support is not complete yet.`;
                        }
                    } else {
                        drive.isEncrypted = false;
                        drive.needsPassword = false;
                        drive.hardwareBound = false;
                        drive.supported = true;
                        drive.mountHint = '';
                        drive.analysisNotes = '';
                    }

                    const broker = brokerMounted.get(id);

                    if (broker) {
                        nativeMountState.set(id, { letter: broker.letter });
                        drive.mounted = true;
                        drive.mountPath = broker.path;
                        drive.driveLetter = broker.letter;
                        drive.mountType = 'native_raw';
                        activeLetters.add(String(broker.letter || '').trim().toUpperCase().replace(':', ''));
                        continue;
                    }

                    const scriptLetter = String(drive.driveLetter || '').trim().toUpperCase().replace(':', '');
                    if (PREFER_SUBST_LOCAL_FAST_PATH && /^[A-Z]$/.test(scriptLetter) && fs.existsSync(`${scriptLetter}:\\`)) {
                        drive.mounted = true;
                        // APFS containers expose a root/ subfolder with actual volume data
                        drive.mountPath = fs.existsSync(`${scriptLetter}:\\root`) ? `${scriptLetter}:\\root\\` : `${scriptLetter}:\\`;
                        drive.driveLetter = scriptLetter;
                        drive.mountType = 'subst_local';
                        activeLetters.add(scriptLetter);
                        nativeMountState.delete(id);
                        continue;
                    }

                    // If broker status is temporarily unavailable, keep a valid native letter
                    // visible — but only if the drive root actually exists. Otherwise the UI
                    // shows a phantom mount and the next mount attempt collides with the stale
                    // entry.
                    const remembered = nativeMountState.get(id);
                    const rememberedLetter = String(remembered?.letter || '').trim().toUpperCase().replace(':', '');
                    if (/^[A-Z]$/.test(rememberedLetter) && fs.existsSync(`${rememberedLetter}:\\`)) {
                        drive.mounted = true;
                        drive.mountPath = fs.existsSync(`${rememberedLetter}:\\root`) ? `${rememberedLetter}:\\root\\` : `${rememberedLetter}:\\`;
                        drive.driveLetter = rememberedLetter;
                        drive.mountType = 'native_raw';
                        activeLetters.add(rememberedLetter);
                        continue;
                    }

                    // Clear stale local mount state if broker no longer reports the drive.
                    nativeMountState.delete(id);
                    // Do not expose UNC/network mount as mounted in user-facing flow.
                    drive.mounted = false;
                    drive.mountPath = null;
                    drive.driveLetter = null;
                    drive.mountType = 'not_mounted';
                }
                try { cleanupGhostDriveLetters?.([...activeLetters]); } catch {}
                addLog(`Scan complete. Found ${Array.isArray(data) ? data.length : 0} disks. Showing ${macDrives.length} Mac drives.`);
                driveCache = { data: macDrives, time: Date.now() };
                res.json(macDrives);
            } catch (e) {
                addLog(`Parse error on scan output. Raw: ${stdout}`, 'error');
                res.json([]);
            }
        });
    });
};
