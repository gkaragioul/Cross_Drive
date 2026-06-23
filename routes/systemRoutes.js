const { exec } = require('child_process');
const fs = require('fs');
const path = require('path');
const pkg = require('../package.json');

module.exports = function mountSystemRoutes(app, ctx) {
    const {
        addLog, logs, setupState, getNativeStatus,
        RUNTIME_MOUNT_MODE, RUNTIME_NATIVE_MOUNT_ENABLED,
        RUNTIME_CANARY_PERCENT, RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK,
        isAdmin, hasRawDiskAccess, PS_PATH, sendNativeWithBoot
    } = ctx;

    const requiredFormats = ['APFS', 'Encrypted APFS', 'HFS+', 'Classic HFS', 'CoreStorage'];

    function runPsScript(action, callback) {
        // Use PS_PATH from context (resolves correctly for both dev and packaged builds)
        const cmd = `powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "${PS_PATH}" -Action "${action}"`;
        exec(cmd, { windowsHide: true, timeout: 60000 }, (err, stdout, stderr) => {
            if (stderr) {
                addLog(`Preflight stderr: ${stderr}`, 'warn');
            }
            try {
                const result = JSON.parse(stdout);
                callback(null, result);
            } catch (e) {
                callback(err || e, null);
            }
        });
    }

    app.get('/api/status', (req, res) => {
        res.json({
            ...setupState,
            wslSetup: setupState.wslSetup || null,
            elevated: !!isAdmin?.(),
            rawDiskAccess: !!hasRawDiskAccess?.(),
            version: pkg.version,
            runtime: {
                mountMode: RUNTIME_MOUNT_MODE,
                nativeMountEnabled: RUNTIME_NATIVE_MOUNT_ENABLED,
                canaryPercent: RUNTIME_CANARY_PERCENT,
                allowNativeBridgeFallback: RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK
            }
        });
    });

    app.get('/api/preflight/check', async (req, res) => {
        runPsScript('PreflightCheck', (err, result) => {
            if (err) {
                addLog(`Preflight check error: ${err.message}`, 'error');
                return res.status(500).json({ success: false, error: err.message });
            }
            res.json(result);
        });
    });

    app.post('/api/preflight/fix', async (req, res) => {
        addLog('Preflight fix requested');
        runPsScript('PreflightFix', (err, result) => {
            if (err) {
                addLog(`Preflight fix error: ${err.message}`, 'error');
                return res.status(500).json({ success: false, error: err.message });
            }
            addLog(`Preflight fix result: ${result.message}`, result.success ? 'success' : 'error');
            res.json(result);
        });
    });

    app.get('/api/logs', (req, res) => {
        res.json(logs);
    });

    app.post('/api/logs', (req, res) => {
        const { message, type } = req.body;
        addLog(message, type || 'info');
        res.json({ success: true });
    });

    app.get('/api/path-exists', (req, res) => {
        const p = String(req.query.path || '').trim();
        if (!p) {
            return res.status(400).json({ ok: false, error: 'path is required' });
        }
        try {
            return res.json({ ok: true, exists: fs.existsSync(p) });
        } catch (e) {
            return res.json({ ok: true, exists: false });
        }
    });

    app.post('/api/setup', (req, res) => {
        addLog("Setup endpoint is disabled in zero-setup runtime mode.");
        return res.status(410).json({
            success: false,
            error: 'Setup endpoint disabled.',
            suggestion: 'Installer-managed prerequisites only.'
        });
    });

    app.post('/api/fix-drivers', (req, res) => {
        addLog("Driver repair endpoint is disabled in zero-setup runtime mode.");
        return res.status(410).json({
            success: false,
            error: 'Driver repair endpoint disabled.',
            suggestion: 'Use installer-based updates for prerequisites.'
        });
    });

    app.post('/api/open', (req, res) => {
        const { path: folderPath } = req.body;
        addLog(`Opening Explorer at: ${folderPath}`);

        const safePath = String(folderPath || '').trim();
        if (!safePath) {
            return res.status(400).json({ success: false, error: 'Path is required.' });
        }

        const psEscaped = safePath.replace(/'/g, "''");
        const taskName = `CrossDriveOpen_${Date.now()}`;
        const openCmd =
            `$p='${psEscaped}'; ` +
            `$a=New-ScheduledTaskAction -Execute 'explorer.exe' -Argument $p; ` +
            `$pr=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive; ` +
            `$set=New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; ` +
            `$t=New-ScheduledTask -Action $a -Principal $pr -Settings $set; ` +
            `Register-ScheduledTask -TaskName '${taskName}' -InputObject $t -Force | Out-Null; ` +
            `Start-ScheduledTask -TaskName '${taskName}' | Out-Null; ` +
            `Start-Sleep -Seconds 1; ` +
            `Unregister-ScheduledTask -TaskName '${taskName}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null;`;

        exec(`powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "${openCmd}"`, { windowsHide: true }, (err) => {
            if (err) {
                addLog(`Explorer Open Error: ${err.message}`, 'error');
            } else {
                addLog(`Explorer open dispatched in user session: ${safePath}`, 'success');
            }
        });
        // Response is intentionally non-blocking — Explorer opens asynchronously.
        // dispatched=true means the shell command was sent; it does not guarantee Explorer opened.
        res.json({ success: true, dispatched: true });
    });

    app.get('/api/support/bundle', async (req, res) => {
        try {
            const outDir = path.join(process.env.ProgramData || 'C:\\ProgramData', 'CrossDrive', 'Support');
            fs.mkdirSync(outDir, { recursive: true });
            const filePath = path.join(outDir, `support-${Date.now()}.json`);
            const payload = {
                createdAt: new Date().toISOString(),
                app: {
                    runtimeNativeMountEnabled: RUNTIME_NATIVE_MOUNT_ENABLED,
                    runtimeMountMode: RUNTIME_MOUNT_MODE,
                    runtimeCanaryPercent: RUNTIME_CANARY_PERCENT,
                    runtimeAllowBridgeFallback: RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK
                },
                setupState,
                nativeStatus: await getNativeStatus(),
                recentLogs: logs.slice(-200)
            };
            fs.writeFileSync(filePath, JSON.stringify(payload, null, 2), 'utf8');
            return res.json({ success: true, path: filePath });
        } catch (e) {
            return res.status(500).json({ success: false, error: e.message });
        }
    });

    function runDriveList() {
        return new Promise((resolve, reject) => {
            const cmd = `powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "${PS_PATH}" -Action "List"`;
            exec(cmd, { windowsHide: true, timeout: 120000 }, (err, stdout, stderr) => {
                if (stderr) addLog(`Validation scan stderr: ${stderr}`, 'warn');
                if (err) return reject(err);
                try {
                    const text = String(stdout || '').trim();
                    const parsed = JSON.parse(text);
                    if (Array.isArray(parsed)) return resolve(parsed);
                    if (Array.isArray(parsed?.drives)) return resolve(parsed.drives);
                    return resolve([]);
                } catch (e) {
                    return reject(new Error(`Invalid drive scan response: ${e.message}`));
                }
            });
        });
    }

    function getPlanFormat(plan, drive) {
        return String(
            plan?.FileSystemType ||
            plan?.fileSystemType ||
            plan?.fsType ||
            drive?.format ||
            drive?.fsType ||
            ''
        ).trim();
    }

    function coverageKeyFor(format, plan, drive) {
        const fsType = String(format || '').trim();
        const encrypted = plan?.IsEncrypted === true || plan?.NeedsPassword === true || drive?.isEncrypted === true || drive?.needsPassword === true;
        if (/CoreStorage/i.test(fsType) || /CoreStorage/i.test(String(plan?.Notes || drive?.analysisNotes || ''))) return 'CoreStorage';
        if (/^APFS$/i.test(fsType) && encrypted) return 'Encrypted APFS';
        if (/^APFS$/i.test(fsType)) return 'APFS';
        if (/^(HFS\+|HFSX)$/i.test(fsType)) return 'HFS+';
        if (/^(HFS|HFS Standard|Classic HFS)$/i.test(fsType)) return 'Classic HFS';
        return null;
    }

    async function analyzeValidationDrive(drive) {
        const id = String(drive?.id ?? drive?.Index ?? drive?.Number ?? '').trim();
        const entry = {
            id,
            name: drive?.name || drive?.friendlyName || drive?.FriendlyName || `Physical Drive ${id || 'unknown'}`,
            size: drive?.size || drive?.sizeGB || drive?.Size || '',
            format: drive?.format || drive?.fsType || '',
            isMac: drive?.isMac === true,
            analysis: null,
            mountSmoke: null,
            error: null
        };

        if (id && typeof sendNativeWithBoot === 'function') {
            try {
                const result = await sendNativeWithBoot({
                    action: 'analyze_raw',
                    requestId: String(Date.now()),
                    physicalDrivePath: `\\\\.\\PHYSICALDRIVE${id}`
                }, 12000, 2);
                entry.analysis = result?.ok && result?.plan ? result.plan : result;
                entry.format = getPlanFormat(entry.analysis, drive) || entry.format;
            } catch (e) {
                entry.error = e.message;
            }
        }

        entry.coverageKey = coverageKeyFor(entry.format, entry.analysis, drive);
        return entry;
    }

    function getValidationBaseUrl(req) {
        const port = req?.socket?.localPort || 3001;
        return `http://127.0.0.1:${port}`;
    }

    async function readResponseJson(response) {
        const text = await response.text();
        try {
            return text ? JSON.parse(text) : {};
        } catch {
            return { error: text || 'Invalid JSON response' };
        }
    }

    function normalizeMountedPath(mountResult) {
        const raw = String(mountResult?.mountPath || mountResult?.path || '').trim();
        if (/^[A-Z]:$/i.test(raw)) return `${raw}\\`;
        return raw;
    }

    function probeMountedRoot(mountPath) {
        if (!mountPath) {
            return { opened: false, error: 'Mount response did not include a path.' };
        }
        try {
            if (!fs.existsSync(mountPath)) {
                return { opened: false, error: `Mounted path does not exist: ${mountPath}` };
            }
            const entries = fs.readdirSync(mountPath);
            return { opened: true, rootEntryCount: entries.length };
        } catch (e) {
            return { opened: false, error: e.message };
        }
    }

    async function runMountSmoke(req, drive, secret) {
        const smoke = {
            attempted: true,
            status: 'mount_failed',
            mountStatusCode: null,
            unmountStatusCode: null,
            path: null,
            mountType: null,
            opened: false,
            rootEntryCount: null,
            passwordProvided: !!secret,
            error: null,
            suggestion: null,
            unmountError: null
        };

        if (!drive.id) {
            smoke.status = 'skipped';
            smoke.error = 'No physical drive id was available.';
            return smoke;
        }

        if (/^CoreStorage$/i.test(String(drive.coverageKey || ''))) {
            smoke.status = 'unsupported';
            smoke.error = 'CoreStorage/FileVault 1 unlock is not implemented yet.';
            return smoke;
        }

        const baseUrl = getValidationBaseUrl(req);
        let mounted = false;
        try {
            const mountResponse = await fetch(`${baseUrl}/api/mount`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    id: drive.id,
                    password: secret || ''
                })
            });
            smoke.mountStatusCode = mountResponse.status;
            const mountResult = await readResponseJson(mountResponse);
            smoke.mountType = mountResult.mountType || mountResult.mode || null;
            smoke.path = normalizeMountedPath(mountResult);

            if (!mountResponse.ok || mountResult.success !== true) {
                smoke.error = mountResult.error || `Mount failed with HTTP ${mountResponse.status}`;
                smoke.suggestion = mountResult.suggestion || null;
                smoke.status = mountResult.needsPassword === true ? 'needs_password' : 'mount_failed';
                return smoke;
            }

            mounted = true;
            const openProbe = probeMountedRoot(smoke.path);
            smoke.opened = openProbe.opened;
            smoke.rootEntryCount = openProbe.rootEntryCount ?? null;
            if (!openProbe.opened) {
                smoke.status = 'mounted_unreadable';
                smoke.error = openProbe.error || 'Mounted path could not be opened.';
                return smoke;
            }
        } catch (e) {
            smoke.error = e.message;
            smoke.status = 'mount_failed';
            return smoke;
        } finally {
            if (mounted) {
                try {
                    const unmountResponse = await fetch(`${baseUrl}/api/unmount`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ id: drive.id })
                    });
                    smoke.unmountStatusCode = unmountResponse.status;
                    const unmountResult = await readResponseJson(unmountResponse);
                    if (!unmountResponse.ok || unmountResult.error) {
                        smoke.unmountError = unmountResult.error || `Unmount failed with HTTP ${unmountResponse.status}`;
                    }
                } catch (e) {
                    smoke.unmountError = e.message;
                }
            }
        }

        if (smoke.opened && !smoke.unmountError) {
            smoke.status = 'opened';
        } else if (smoke.opened) {
            smoke.status = 'cleanup_failed';
        }
        return smoke;
    }

    async function handleRealMediaValidation(req, res) {
        try {
            addLog('Real-media validation report requested');
            const outDir = path.join(process.env.ProgramData || 'C:\\ProgramData', 'CrossDrive', 'Validation');
            fs.mkdirSync(outDir, { recursive: true });
            const mountSmokeEnabled = String(req.query.mount || '1') !== '0';
            const validationPassword = String(req.body?.validationPassword || '');
            const passwordWasProvided = !!validationPassword;

            let scannedDrives = [];
            let scanError = null;
            try {
                scannedDrives = await runDriveList();
            } catch (e) {
                scanError = e.message;
                addLog(`Real-media validation scan failed: ${e.message}`, 'error');
            }

            const analyzedDrives = await Promise.all(
                scannedDrives
                    .filter(d => d?.isMac === true || d?.format || d?.fsType)
                    .map(analyzeValidationDrive)
            );

            if (mountSmokeEnabled) {
                for (const drive of analyzedDrives) {
                    if (!drive.coverageKey || !drive.id) continue;
                    drive.mountSmoke = await runMountSmoke(req, drive, String(validationPassword || ''));
                }
            }

            const coverage = {};
            for (const format of requiredFormats) {
                coverage[format] = {
                    format,
                    status: 'missing',
                    evidence: null,
                    note: `Missing evidence for ${format}. Attach a real ${format} disk and rerun validation.`
                };
            }

            for (const drive of analyzedDrives) {
                if (!drive.coverageKey || !coverage[drive.coverageKey]) continue;
                const unsupported = /CoreStorage/i.test(drive.coverageKey) || /unsupported|not implemented/i.test(String(drive.analysis?.Notes || drive.error || ''));
                const status = unsupported
                    ? 'unsupported'
                    : drive.mountSmoke?.status === 'opened'
                        ? 'opened'
                        : drive.mountSmoke?.status === 'needs_password'
                            ? 'needs_password'
                            : drive.mountSmoke?.attempted
                                ? 'mount_failed'
                                : 'detected';
                coverage[drive.coverageKey] = {
                    format: drive.coverageKey,
                    status,
                    evidence: {
                        driveId: drive.id,
                        name: drive.name,
                        format: drive.format,
                        analysisNotes: drive.analysis?.Notes || '',
                        needsPassword: drive.analysis?.NeedsPassword === true,
                        isEncrypted: drive.analysis?.IsEncrypted === true,
                        mountSmoke: drive.mountSmoke
                    },
                    note: unsupported
                        ? `${drive.coverageKey} was detected but is not fully supported yet.`
                        : status === 'opened'
                            ? `${drive.coverageKey} was mounted, opened, and unmounted successfully.`
                            : status === 'needs_password'
                                ? `${drive.coverageKey} was detected but needs a password for open validation.`
                                : `${drive.coverageKey} was detected on attached real media but open validation did not pass.`
                };
            }

            const missingFormats = requiredFormats.filter(format => coverage[format].status === 'missing');
            const unsupportedFormats = requiredFormats.filter(format => coverage[format].status === 'unsupported');
            const needsPasswordFormats = requiredFormats.filter(format => coverage[format].status === 'needs_password');
            const failedFormats = requiredFormats.filter(format => ['detected', 'mount_failed'].includes(coverage[format].status));
            const complete = requiredFormats.every(format => coverage[format].status === 'opened');
            const filePath = path.join(outDir, `real-media-${Date.now()}.json`);
            const payload = {
                createdAt: new Date().toISOString(),
                requiredFormats,
                complete,
                mountSmokeEnabled,
                passwordProvided: passwordWasProvided,
                scanError,
                missingFormats,
                unsupportedFormats,
                needsPasswordFormats,
                failedFormats,
                host: {
                    elevated: !!isAdmin?.(),
                    rawDiskAccess: !!hasRawDiskAccess?.(),
                    setupState,
                    nativeStatus: await getNativeStatus(),
                    runtime: {
                        mountMode: RUNTIME_MOUNT_MODE,
                        nativeMountEnabled: RUNTIME_NATIVE_MOUNT_ENABLED,
                        canaryPercent: RUNTIME_CANARY_PERCENT,
                        allowNativeBridgeFallback: RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK
                    }
                },
                coverage,
                drives: analyzedDrives
            };

            fs.writeFileSync(filePath, JSON.stringify(payload, null, 2), 'utf8');
            addLog(`Real-media validation report saved: ${filePath}`, complete ? 'success' : 'warn');
            return res.json({
                success: true,
                complete,
                path: filePath,
                passwordProvided: passwordWasProvided,
                missingFormats,
                unsupportedFormats,
                needsPasswordFormats,
                failedFormats,
                coverage
            });
        } catch (e) {
            return res.status(500).json({ success: false, error: e.message });
        }
    }

    app.get('/api/validation/real-media', handleRealMediaValidation);
    app.post('/api/validation/real-media', handleRealMediaValidation);
};
