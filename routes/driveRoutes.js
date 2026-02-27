const { exec } = require('child_process');
const fs = require('fs');

module.exports = function mountDriveRoutes(app, ctx) {
    const { addLog, nativeMountState, getBrokerMountedMap, PREFER_SUBST_LOCAL_FAST_PATH, PS_PATH } = ctx;

    // Cache to avoid spawning PowerShell on every 5-second poll
    let driveCache = { data: null, time: 0 };
    const CACHE_TTL_MS = 3000;
    // Expose invalidator so mount/unmount routes can force a fresh scan
    ctx.invalidateDriveCache = () => { driveCache = { data: null, time: 0 }; };

    app.get('/api/drives', (req, res) => {
        const now = Date.now();
        if (driveCache.data && (now - driveCache.time) < CACHE_TTL_MS) {
            return res.json(driveCache.data);
        }

        addLog("Scanning for Mac-formatted drives...");
        exec(`powershell -ExecutionPolicy Bypass -File "${PS_PATH}" -Action List`, { windowsHide: true }, async (error, stdout, stderr) => {
            if (stderr) addLog(`PS Scan Output: ${stderr}`, 'warning');
            if (error) {
                addLog(`PS Scan Failure: ${error.message}`, 'error');
                return res.json({ error: "Drives could not be scanned. Admin rights might be needed." });
            }
            try {
                const data = JSON.parse(stdout);
                const macDrives = Array.isArray(data) ? data.filter(d => d.isMac) : [];
                const brokerMounted = await getBrokerMountedMap();

                for (const drive of macDrives) {
                    const id = String(drive.id);
                    const broker = brokerMounted.get(id);

                    if (broker) {
                        nativeMountState.set(id, { letter: broker.letter });
                        drive.mounted = true;
                        drive.mountPath = broker.path;
                        drive.driveLetter = broker.letter;
                        drive.mountType = 'native_raw';
                        continue;
                    }

                    const scriptLetter = String(drive.driveLetter || '').trim().toUpperCase().replace(':', '');
                    if (PREFER_SUBST_LOCAL_FAST_PATH && /^[A-Z]$/.test(scriptLetter) && fs.existsSync(`${scriptLetter}:\\`)) {
                        drive.mounted = true;
                        // APFS containers expose a root/ subfolder with actual volume data
                        drive.mountPath = fs.existsSync(`${scriptLetter}:\\root`) ? `${scriptLetter}:\\root\\` : `${scriptLetter}:\\`;
                        drive.driveLetter = scriptLetter;
                        drive.mountType = 'subst_local';
                        nativeMountState.delete(id);
                        continue;
                    }

                    // If broker status is temporarily unavailable, keep a valid native letter visible.
                    const remembered = nativeMountState.get(id);
                    const rememberedLetter = String(remembered?.letter || '').trim().toUpperCase().replace(':', '');
                    if (/^[A-Z]$/.test(rememberedLetter)) {
                        drive.mounted = true;
                        drive.mountPath = fs.existsSync(`${rememberedLetter}:\\root`) ? `${rememberedLetter}:\\root\\` : `${rememberedLetter}:\\`;
                        drive.driveLetter = rememberedLetter;
                        drive.mountType = 'native_raw';
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
