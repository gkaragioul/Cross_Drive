module.exports = function mountNativeRoutes(app, ctx) {
    const {
        addLog, getNativeStatus, sendNativeWithBoot, ensureBrokerReady,
        RUNTIME_NATIVE_MOUNT_ENABLED, RUNTIME_MOUNT_MODE, RUNTIME_CANARY_PERCENT, RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK
    } = ctx;

    app.get('/api/native/status', async (req, res) => {
        const native = await getNativeStatus();
        res.json({
            ...native,
            runtimeNativeEnabled: RUNTIME_NATIVE_MOUNT_ENABLED,
            mode: RUNTIME_MOUNT_MODE,
            canaryPercent: RUNTIME_CANARY_PERCENT,
            allowBridgeFallback: RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK
        });
    });

    app.post('/api/native/mount', async (req, res) => {
        if (!RUNTIME_NATIVE_MOUNT_ENABLED) {
            return res.status(501).json({
                ok: false,
                error: 'Native runtime mount disabled in app flow.',
                suggestion: 'Use /api/mount while the native runtime is being repaired.'
            });
        }
        return res.status(400).json({
            ok: false,
            error: 'Use /api/native/mount-raw for direct native mount testing.'
        });
    });

    app.post('/api/native/unmount', async (req, res) => {
        if (!RUNTIME_NATIVE_MOUNT_ENABLED) {
            return res.status(501).json({
                ok: false,
                error: 'Native runtime unmount disabled in app flow.'
            });
        }
        return res.status(400).json({
            ok: false,
            error: 'Use /api/native/unmount-raw for direct native unmount testing.'
        });
    });

    app.post('/api/native/mount-raw', async (req, res) => {
        const { driveId, letter, physicalDrivePath, physicalDriveId, fileSystemHint, password } = req.body || {};
        const resolvedPath = String(physicalDrivePath || '').trim()
            || (physicalDriveId !== undefined && physicalDriveId !== null
                ? `\\\\.\\PHYSICALDRIVE${String(physicalDriveId).trim()}`
                : '');

        if (!driveId || !letter || !resolvedPath) {
            return res.status(400).json({
                ok: false,
                error: 'driveId, letter and (physicalDrivePath or physicalDriveId) are required.'
            });
        }
        if (!/^[A-Za-z]$/.test(String(letter))) {
            return res.status(400).json({
                ok: false,
                error: 'letter must be a single drive letter A–Z.'
            });
        }

        try {
            const result = await sendNativeWithBoot({
                action: 'mount_raw',
                requestId: String(Date.now()),
                driveId: String(driveId),
                letter: String(letter),
                physicalDrivePath: resolvedPath,
                fileSystemHint: String(fileSystemHint || ''),
                password: String(password || '')
            }, 20000, 10);
            if (!result.ok) {
                const status = result.needsPassword ? 409 : 500;
                return res.status(status).json(result);
            }
            return res.json(result);
        } catch (e) {
            return res.status(500).json({ ok: false, error: e.message });
        }
    });

    app.post('/api/native/unmount-raw', async (req, res) => {
        const { driveId } = req.body || {};
        if (!driveId) {
            return res.status(400).json({ ok: false, error: 'driveId is required.' });
        }

        try {
            const result = await sendNativeWithBoot({
                action: 'unmount',
                requestId: String(Date.now()),
                driveId: String(driveId)
            }, 10000, 10);
            if (!result.ok) return res.status(500).json(result);
            return res.json(result);
        } catch (e) {
            return res.status(500).json({ ok: false, error: e.message });
        }
    });

    app.post('/api/native/analyze-raw', async (req, res) => {
        const { physicalDrivePath, physicalDriveId, fileSystemHint } = req.body || {};
        const resolvedPath = String(physicalDrivePath || '').trim()
            || (physicalDriveId !== undefined && physicalDriveId !== null
                ? `\\\\.\\PHYSICALDRIVE${String(physicalDriveId).trim()}`
                : '');

        if (!resolvedPath) {
            return res.status(400).json({
                ok: false,
                error: 'Provide physicalDrivePath or physicalDriveId.'
            });
        }

        try {
            const result = await sendNativeWithBoot({
                action: 'analyze_raw',
                requestId: String(Date.now()),
                physicalDrivePath: resolvedPath,
                fileSystemHint: String(fileSystemHint || '')
            }, 20000, 10);
            if (!result.ok) return res.status(500).json(result);
            return res.json(result);
        } catch (e) {
            return res.status(500).json({ ok: false, error: e.message });
        }
    });

    app.get('/api/runtime/config', (req, res) => {
        res.json({
            mode: RUNTIME_MOUNT_MODE,
            nativeEnabled: RUNTIME_NATIVE_MOUNT_ENABLED,
            canaryPercent: RUNTIME_CANARY_PERCENT,
            allowBridgeFallback: RUNTIME_ALLOW_NATIVE_BRIDGE_FALLBACK
        });
    });

    app.post('/api/runtime/check', async (req, res) => {
        let nativeService = false;
        let brokerReady = false;
        try {
            const native = await getNativeStatus();
            nativeService = !!native?.available;
        } catch {
            nativeService = false;
        }
        try {
            brokerReady = await ensureBrokerReady(3);
        } catch {
            brokerReady = false;
        }
        return res.json({
            ok: nativeService && brokerReady,
            mode: RUNTIME_MOUNT_MODE,
            checks: { nativeService, brokerReady }
        });
    });
};
