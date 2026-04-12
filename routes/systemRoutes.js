const { exec } = require('child_process');
const fs = require('fs');
const path = require('path');

module.exports = function mountSystemRoutes(app, ctx) {
    const {
        addLog, logs, setupState, getNativeStatus,
        RUNTIME_MOUNT_MODE, RUNTIME_NATIVE_MOUNT_ENABLED,
        RUNTIME_CANARY_PERCENT, RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK,
        isAdmin, hasRawDiskAccess
    } = ctx;

    function runPsScript(action, callback) {
        const scriptPath = path.join(__dirname, '..', 'scripts', 'MacMount.ps1');
        const cmd = `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${scriptPath}" -Action "${action}"`;
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
            elevated: !!isAdmin?.(),
            rawDiskAccess: !!hasRawDiskAccess?.()
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
        const taskName = `MacMountOpen_${Date.now()}`;
        const openCmd =
            `$p='${psEscaped}'; ` +
            `$a=New-ScheduledTaskAction -Execute 'explorer.exe' -Argument $p; ` +
            `$pr=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive; ` +
            `$t=New-ScheduledTask -Action $a -Principal $pr; ` +
            `Register-ScheduledTask -TaskName '${taskName}' -InputObject $t -Force | Out-Null; ` +
            `Start-ScheduledTask -TaskName '${taskName}' | Out-Null; ` +
            `Start-Sleep -Seconds 1; ` +
            `Unregister-ScheduledTask -TaskName '${taskName}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null;`;

        exec(`powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "${openCmd}"`, { windowsHide: true }, (err) => {
            if (err) {
                addLog(`Explorer Open Error: ${err.message}`, 'error');
                return;
            }
            addLog(`Explorer open dispatched in user session: ${safePath}`, 'success');
        });
        res.json({ success: true });
    });

    app.get('/api/support/bundle', async (req, res) => {
        try {
            const outDir = path.join(process.env.ProgramData || 'C:\\ProgramData', 'MacMount', 'Support');
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
};
