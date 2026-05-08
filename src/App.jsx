import React, { useState, useEffect } from 'react';
import './index.css';
import appLogo from './assets/gkmacopener-logo.png';
import {
  fetchDrives as apiFetchDrives,
  fetchStatus,
  fetchRuntimeConfig,
  fetchPreflight,
  fixPreflight,
  fetchNativeStatus,
  fetchLogs,
  postLog,
  mountDrive as apiMountDrive,
  unmountDrive as apiUnmountDrive,
  openInExplorer as apiOpenInExplorer,
  generateSupportBundle,
  checkForUpdate,
  dismissUpdate,
} from './api';
import { POLL_INTERVALS } from './config';
import UpdateBanner from './UpdateBanner';
import UpdateModal from './UpdateModal';

const DriveIcon = () => (
  <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M22 12H2M22 6H2M22 18H2" />
  </svg>
);

const FolderIcon = () => (
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
  </svg>
);

const SettingsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <circle cx="12" cy="12" r="3" />
    <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z" />
  </svg>
);

const Dots = () => {
  const [count, setCount] = useState(0);
  useEffect(() => {
    const t = setInterval(() => setCount(c => (c + 1) % 4), 500);
    return () => clearInterval(t);
  }, []);
  return <span>{'.'.repeat(count)}</span>;
};

const SetupBanner = ({ setup }) => {
  if (setup.status === 'ready') return null;
  const isInstalling = setup.status === 'installing' || setup.status === 'checking';
  const isFailed = setup.status === 'failed';
  return (
    <div style={{
      backgroundColor: isFailed ? 'rgba(192,57,43,0.1)' : 'rgba(229,83,0,0.08)',
      border: `1px solid ${isFailed ? 'var(--danger)' : 'rgba(229,83,0,0.3)'}`,
      color: isFailed ? 'var(--danger)' : 'var(--primary)',
      padding: '14px 18px', marginBottom: '20px', display: 'flex',
      alignItems: 'center', gap: '12px', fontSize: '12px',
      fontFamily: 'var(--font-mono)', letterSpacing: '0.5px'
    }}>
      {isInstalling && (
        <div style={{
          width: 14, height: 14,
          border: '2px solid var(--primary)', borderTopColor: 'transparent',
          animation: 'spin 0.8s linear infinite', flexShrink: 0
        }} />
      )}
      {isFailed && <span style={{ fontSize: 18 }}>&#9888;&#65039;</span>}
      <div>
        <strong>{isInstalling ? 'Setting up Mac drivers' : 'Setup failed'}</strong>
        {isInstalling && <Dots />}
        {' \u2014 '}
        <span style={{ opacity: 0.85 }}>{isInstalling ? 'Preparing runtime components. This can take a moment on first launch.' : setup.message}</span>
      </div>
    </div>
  );
};

const SettingsRow = ({ label, value }) => (
  <div style={{ display: 'flex', justifyContent: 'space-between', padding: '8px 0', borderBottom: '1px solid var(--border)' }}>
    <span style={{ color: 'var(--text-dim)', fontSize: '12px', letterSpacing: '1px', textTransform: 'uppercase' }}>{label}</span>
    <span style={{ fontFamily: 'var(--font-mono)', fontSize: '12px', color: 'var(--text-main)' }}>{value}</span>
  </div>
);

const APP_VERSION_FALLBACK = '1.5.2';
const COPYRIGHT_NOTICE = 'Copyright (c) 2026 GKMacOpener contributors';
const WINFSP_NOTICE = 'WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos';

const formatMountError = (result) => {
  if (!result || typeof result !== 'object') return 'Unknown mount error.';
  const parts = [];
  const push = (value) => {
    const text = String(value || '').trim();
    if (!text) return;
    if (!parts.includes(text)) parts.push(text);
  };
  push(result.error);
  push(result.details);
  push(result.nativeAttemptError);
  push(result.suggestion);
  push(result.nativeAttemptSuggestion);
  const analysisNotes = result.analysis?.plan?.Notes
    || result.nativeAttemptAnalysis?.plan?.Notes
    || result.plan?.Notes;
  if (analysisNotes && !/signature detected/i.test(String(analysisNotes))) {
    push(`Analysis: ${analysisNotes}`);
  }
  return parts.join(' ');
};

const App = () => {
  const [drives, setDrives] = useState([]);
  const [activeTab, setActiveTab] = useState('drives');
  const [isMounting, setIsMounting] = useState(null);
  const [isLoading, setIsLoading] = useState(true);
  const [errorMessage, setErrorMessage] = useState(null);
  const [logs, setLogs] = useState([]);
  const [setup, setSetup] = useState({ status: 'ready', message: 'Core runtime ready.', ready: true });
  const [nativeStatus, setNativeStatus] = useState({ available: false });
  const [passwordPrompt, setPasswordPrompt] = useState(null);
  const [passwordValue, setPasswordValue] = useState('');
  const [runtimeConfig, setRuntimeConfig] = useState(null);
  const [bundleStatus, setBundleStatus] = useState(null);
  const [preflight, setPreflight] = useState(null);
  const [fixingPreflight, setFixingPreflight] = useState(false);
  const [update, setUpdate] = useState(null);
  const [updateModalOpen, setUpdateModalOpen] = useState(false);
  const [updateCheckNotice, setUpdateCheckNotice] = useState(null);
  const [lastCheckedAt, setLastCheckedAt] = useState(null);
  const [manualCheckBusy, setManualCheckBusy] = useState(false);

  useEffect(() => {
    let unmounted = false;
    const safe = (fn) => (...args) => { if (!unmounted) fn(...args); };

    const loadDrives = async () => {
      try {
        const { drives: d, error } = await apiFetchDrives();
        safe(setDrives)(d);
        safe(setErrorMessage)(error);
      } catch {
        safe(setErrorMessage)('Could not connect to backend server.');
      } finally {
        safe(setIsLoading)(false);
      }
    };

    loadDrives();
    checkForUpdate(true).then(safe(setUpdate)).then(() => safe(setLastCheckedAt)(new Date())).catch(() => {});
    const logInterval = setInterval(() => fetchLogs().then(safe(setLogs)).catch(() => {}), POLL_INTERVALS.logs);
    const statusInterval = setInterval(() => fetchStatus().then(safe(setSetup)).catch(() => {}), POLL_INTERVALS.status);
    const nativeInterval = setInterval(() => fetchNativeStatus().then(safe(setNativeStatus)).catch(() => {}), POLL_INTERVALS.nativeStatus);
    const driveInterval = setInterval(loadDrives, POLL_INTERVALS.drives);
    const preflightInterval = setInterval(() => fetchPreflight().then(safe(setPreflight)).catch(() => {}), POLL_INTERVALS.preflight);
    fetchStatus().then(safe(setSetup)).catch(() => {});
    fetchNativeStatus().then(safe(setNativeStatus)).catch(() => {});
    fetchRuntimeConfig().then(safe(setRuntimeConfig)).catch(() => {});
    fetchPreflight().then(safe(setPreflight)).catch(() => {});
    return () => {
      unmounted = true;
      clearInterval(logInterval);
      clearInterval(statusInterval);
      clearInterval(nativeInterval);
      clearInterval(driveInterval);
      clearInterval(preflightInterval);
    };
  }, []);

  const logRemote = async (message, type) => {
    try { await postLog(message, type); await fetchLogs().then(setLogs).catch(() => {}); } catch {}
  };

  const doFixPreflight = async () => {
    setFixingPreflight(true);
    try {
      const data = await fixPreflight();
      setPreflight(data);
      if (data.success) {
        const { drives: d } = await apiFetchDrives();
        setDrives(d);
      }
    } catch { /* silent */ }
    finally { setFixingPreflight(false); }
  };

  const mountDrive = async (id, password = '') => {
    setIsMounting(id);
    setErrorMessage(null);
    logRemote(`Frontend: User requested mount for ${id} ${password ? '(with password)' : ''}`);
    try {
      const result = await apiMountDrive(id, password);
      logRemote(`SUCCESS: Drive ${id} mounted at ${result.path}`, 'success');
      setErrorMessage(null);
      setPasswordPrompt(null);
      setPasswordValue('');
      const { drives: d } = await apiFetchDrives();
      setDrives(d);
    } catch (err) {
      if (err.result?.needsPassword) {
        const drive = drives.find(d => d.id === id);
        setPasswordPrompt({ id, name: drive?.name || `Physical Drive ${id}` });
        return;
      }
      const formattedError = formatMountError(err.result || { error: err.message });
      setErrorMessage(formattedError);
      logRemote(`Mount Failure: ${formattedError}`, 'error');
    } finally {
      setIsMounting(null);
    }
  };

  const unmountDrive = async (id) => {
    setIsMounting(id);
    setErrorMessage(null);
    logRemote(`Frontend: User requested unmount for ${id}`);
    try {
      await apiUnmountDrive(id);
      logRemote(`Drive ${id} unmounted.`, 'success');
      setErrorMessage(null);
      const { drives: d } = await apiFetchDrives();
      setDrives(d);
    } catch (err) {
      setErrorMessage(`Unmount failed: ${err.result?.error || err.message || 'unknown error'}`);
    } finally {
      setIsMounting(null);
    }
  };

  const refreshDrive = async (id) => {
    setIsMounting(id);
    setErrorMessage(null);
    logRemote(`Frontend: User requested refresh (unmount+mount) for ${id}`);
    try {
      await apiUnmountDrive(id);
      // Small delay so the kernel/9P side fully releases the mount
      // before we try to remount. Without this, the remount can race
      // and fail with EBUSY.
      await new Promise(r => setTimeout(r, 800));
      const result = await apiMountDrive(id);
      logRemote(`SUCCESS: Drive ${id} refreshed at ${result.path}`, 'success');
      const { drives: d } = await apiFetchDrives();
      setDrives(d);
    } catch (err) {
      const detail = err.result?.error || err.message || 'unknown error';
      setErrorMessage(`Refresh failed: ${detail}`);
      logRemote(`Refresh Failure: ${detail}`, 'error');
    } finally {
      setIsMounting(null);
    }
  };

  const onUpdateLater = () => setUpdate(null);
  const onUpdateSkip = async () => {
    if (!update?.version) return;
    try { await dismissUpdate(update.version); } catch {}
    setUpdate(null);
  };
  const onUpdateNow = () => setUpdateModalOpen(true);
  const runManualUpdateCheck = async () => {
    setManualCheckBusy(true);
    setUpdateCheckNotice(null);
    try {
      const u = await checkForUpdate(false);
      setUpdate(u);
      setLastCheckedAt(new Date());
      if (u?.available) {
        setUpdateCheckNotice({ type: 'success', message: `Update ${u.version} is available.` });
      } else {
        setUpdateCheckNotice({ type: 'success', message: "You're running the latest version." });
      }
    } catch {
      setUpdateCheckNotice({ type: 'error', message: 'Update check failed. Try again later.' });
    }
    finally { setManualCheckBusy(false); }
  };

  const openInExplorer = async (p) => {
    try { await apiOpenInExplorer(p); } catch { /* fire-and-forget */ }
  };

  const getExplorerTarget = (drive) => {
    if (drive?.mountPath) return drive.mountPath;
    if (drive?.driveLetter) return `${String(drive.driveLetter).replace(':', '')}:\\`;
    return '';
  };

  const copyLogs = () => {
    const text = logs.map(l => `[${l.timestamp}] [${l.type.toUpperCase()}] ${l.message}`).join('\n');
    navigator.clipboard.writeText(text);
    logRemote('Logs copied to clipboard', 'success');
  };

  const doGenerateBundle = async () => {
    setBundleStatus('generating');
    try {
      const data = await generateSupportBundle();
      setBundleStatus(data.success ? { path: data.path } : { error: data.error || 'Unknown error' });
    } catch (e) {
      setBundleStatus({ error: e.message });
    }
  };

  const renderDrives = () => {
    const environmentReady = setup.ready;
    return (
      <>
        <section className="header-section fade-in">
          <h1>Physical Drives</h1>
          <p>Select a Mac-formatted drive to mount it as a Windows volume.</p>
        </section>

        <UpdateBanner update={update} onUpdateNow={onUpdateNow} onLater={onUpdateLater} onSkip={onUpdateSkip} />
        <SetupBanner setup={setup} />

        {preflight && !preflight.ready && (
          <div style={{
            backgroundColor: 'rgba(229,83,0,0.08)', border: '1px solid rgba(229,83,0,0.3)',
            color: 'var(--primary)', padding: '16px 18px', marginBottom: '20px',
            fontSize: '12px', fontFamily: 'var(--font-mono)', letterSpacing: '0.5px'
          }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: '12px' }}>
              <strong>Prerequisites Missing</strong>
              <button className="btn btn-primary" style={{ width: 'auto', padding: '6px 14px', fontSize: '11px' }}
                onClick={doFixPreflight} disabled={fixingPreflight}>
                {fixingPreflight ? 'Installing...' : 'Auto-Install'}
              </button>
            </div>
            {preflight.items && preflight.items.map(item => (
              <div key={item.id} style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '6px', color: item.ok ? 'var(--success)' : 'var(--danger)' }}>
                <span>{item.ok ? '\u2713' : '\u2717'}</span>
                <span>{item.title}: {item.detail}</span>
              </div>
            ))}
            {preflight.note && <div style={{ marginTop: '8px', opacity: 0.7, fontSize: '11px' }}>{preflight.note}</div>}
          </div>
        )}

        {errorMessage && (
          <div style={{ backgroundColor: 'rgba(192,57,43,0.08)', border: '1px solid var(--danger)', color: 'var(--danger)', padding: '14px 18px', marginBottom: '24px', fontFamily: 'var(--font-mono)', fontSize: '12px', letterSpacing: '0.5px' }}>
            <strong style={{ letterSpacing: '1.5px', textTransform: 'uppercase' }}>SYS_ERROR:</strong> {errorMessage}
          </div>
        )}

        {isLoading ? (
          <div style={{ display: 'flex', justifyContent: 'center', padding: '100px' }}>
            <div className="spinner">Scanning Disks...</div>
          </div>
        ) : (
          <div className="drive-grid">
            {drives.map((drive, index) => {
              const mountBlocked = !drive.mounted && drive.supported === false;
              const mountTitle = !drive.mounted && !environmentReady
                ? 'Finish setup first.'
                : mountBlocked
                  ? (drive.mountHint || 'This drive type is not supported yet.')
                  : '';
              const mountLabel = isMounting === drive.id
                ? (drive.mounted ? 'Unmounting...' : 'Mounting...')
                : !environmentReady && !drive.mounted
                  ? '\u23F3 Preparing...'
                  : mountBlocked
                    ? 'Unsupported'
                    : drive.mounted
                      ? 'Unmount'
                      : drive.needsPassword
                        ? 'Unlock Drive'
                        : 'Mount Drive';
              return (
                <div key={drive.id} className="drive-card fade-in" style={{ animationDelay: `${index * 0.1}s`, opacity: drive.isMac ? 1 : 0.6 }}>
                  <div className="drive-info">
                    <div className="drive-icon"><DriveIcon /></div>
                    <div className="drive-details">
                      <h3>{drive.name}</h3>
                      <span>{drive.size} &bull; {drive.type} &bull; <b style={{ color: drive.isMac ? 'var(--success)' : 'inherit' }}>{drive.format}</b></span>
                      {(drive.needsPassword || drive.mountHint) && (
                        <div style={{ marginTop: '8px', fontSize: '11px', letterSpacing: '0.5px', color: drive.supported === false ? 'var(--danger)' : drive.needsPassword ? 'var(--warning)' : 'var(--text-dim)', fontFamily: 'var(--font-mono)' }}>
                          {drive.needsPassword ? 'ENCRYPTED' : 'NOTICE'}: {drive.mountHint}
                        </div>
                      )}
                    </div>
                  </div>
                  <div className="mount-status">
                    <div className={`status-dot ${drive.mounted ? 'status-mounted' : 'status-unmounted'}`} />
                    {drive.mounted
                      ? (drive.driveLetter
                          ? `Mounted as ${drive.driveLetter}:`
                          : drive.mountPath && drive.mountPath.startsWith('\\\\')
                            ? 'Mounted (R/W via WSL2 \u2014 click Open Explorer)'
                            : 'Mounted')
                      : 'Unmounted'}
                  </div>
                  <div className="card-actions">
                    <button
                      className={`btn ${drive.mounted ? 'btn-outline' : 'btn-primary'}`}
                      onClick={() => drive.mounted ? unmountDrive(drive.id) : mountDrive(drive.id)}
                      disabled={isMounting !== null || (!drive.mounted && (!environmentReady || mountBlocked))}
                      title={mountTitle}
                    >
                      {mountLabel}
                    </button>
                    <button
                      className="btn btn-outline"
                      disabled={!drive.mounted || !getExplorerTarget(drive)}
                      onClick={() => openInExplorer(getExplorerTarget(drive))}
                    >
                      Open Explorer
                    </button>
                    {drive.mounted && (
                      <button
                        className="btn btn-outline"
                        disabled={isMounting !== null}
                        onClick={() => refreshDrive(drive.id)}
                        title="Unmount and remount the drive. Use after heavy file operations if folder deletes leave files behind."
                      >
                        {isMounting === drive.id ? 'Refreshing...' : 'Refresh'}
                      </button>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </>
    );
  };

  const renderLogs = () => (
    <section className="fade-in">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '24px' }}>
        <h1>System Logs</h1>
        <div style={{ display: 'flex', gap: '8px' }}>
          <button className="btn btn-outline" style={{ width: 'auto', padding: '8px 16px' }} onClick={copyLogs}>Copy Logs</button>
          <button className="btn btn-outline" style={{ width: 'auto', padding: '8px 16px' }} onClick={() => setLogs([])}>Clear</button>
        </div>
      </div>
      <div style={{ backgroundColor: '#080808', border: '1px solid var(--border)', padding: '20px', height: '500px', overflowY: 'auto', fontFamily: 'var(--font-mono)', fontSize: '12px' }}>
        {logs.map((log, i) => (
          <div key={i} style={{ marginBottom: '6px', color: log.type === 'error' ? 'var(--danger)' : log.type === 'success' ? 'var(--success)' : log.type === 'warning' ? 'var(--warning)' : 'var(--text-dim)', lineHeight: '1.6' }}>
            <span style={{ color: '#333', marginRight: '10px' }}>[{log.timestamp}]</span>
            <span>{log.message}</span>
          </div>
        ))}
        {logs.length === 0 && <div style={{ color: '#333', textAlign: 'center', marginTop: '200px', letterSpacing: '2px', textTransform: 'uppercase', fontSize: '11px' }}>-- NO ACTIVITY LOGGED --</div>}
      </div>
    </section>
  );

  const renderSettings = () => (
    <section className="fade-in">
      <h1>Settings</h1>

      <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>Runtime Prerequisites</h3>
      <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
        {preflight ? (
          <>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
              <span style={{ color: preflight.ready ? 'var(--success)' : 'var(--danger)', fontSize: '13px', fontWeight: 'bold' }}>
                {preflight.ready ? 'All prerequisites installed' : 'Prerequisites missing'}
              </span>
              {!preflight.ready && (
                <button className="btn btn-primary" style={{ width: 'auto', padding: '6px 14px', fontSize: '11px' }} onClick={doFixPreflight} disabled={fixingPreflight}>
                  {fixingPreflight ? 'Installing...' : 'Auto-Install'}
                </button>
              )}
            </div>
            {preflight.items && preflight.items.map(item => (
              <div key={item.id} style={{ display: 'flex', justifyContent: 'space-between', padding: '8px 0', borderBottom: '1px solid var(--border)', color: item.ok ? 'var(--success)' : 'var(--danger)' }}>
                <span style={{ fontSize: '12px', letterSpacing: '1px', textTransform: 'uppercase' }}>{item.title}</span>
                <span style={{ fontFamily: 'var(--font-mono)', fontSize: '12px' }}>{item.detail}</span>
              </div>
            ))}
          </>
        ) : (
          <div style={{ color: 'var(--text-dim)', fontSize: '13px' }}>Checking...</div>
        )}
      </div>

      <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>Native Engine</h3>
      <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
        <SettingsRow label="Status" value={nativeStatus.available ? 'Connected' : 'Not connected'} />
        {nativeStatus.available && <SettingsRow label="Engine" value={nativeStatus.engine || 'unknown'} />}
        {nativeStatus.available && <SettingsRow label="Local Fixed Support" value={nativeStatus.supportsLocalFixed ? 'Yes' : 'No'} />}
      </div>

      <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>Updates</h3>
      <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
        <SettingsRow label="Installed Version" value={setup?.version || APP_VERSION_FALLBACK} />
        <SettingsRow label="Latest Available" value={update?.available ? update.version : (update ? 'Up to date' : 'Unknown')} />
        <SettingsRow label="Last Check" value={lastCheckedAt ? lastCheckedAt.toLocaleString() : '—'} />
        <div style={{ marginTop: '12px', display: 'flex', gap: '8px' }}>
          <button className="btn btn-outline" style={{ width: 'auto', padding: '8px 16px' }} onClick={runManualUpdateCheck} disabled={manualCheckBusy}>
            {manualCheckBusy ? 'Checking...' : 'Check for updates'}
          </button>
          {update?.available && (
            <button className="btn btn-primary" style={{ width: 'auto', padding: '8px 16px' }} onClick={onUpdateNow}>
              Update now ({update.version})
            </button>
          )}
        </div>
      </div>

      <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>Runtime Configuration</h3>
      <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
        {runtimeConfig ? (
          <>
            <SettingsRow label="Mount Mode" value={runtimeConfig.mode} />
            <SettingsRow label="Native Mount Enabled" value={runtimeConfig.nativeEnabled ? 'Yes' : 'No'} />
            <SettingsRow label="Raw Engine Rollout %" value={`${runtimeConfig.canaryPercent}%`} />
            <SettingsRow label="Native Bridge Fallback" value={runtimeConfig.allowBridgeFallback ? 'Allowed' : 'Disabled'} />
          </>
        ) : (
          <div style={{ color: 'var(--text-dim)', fontSize: '13px' }}>Loading...</div>
        )}
      </div>

      <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>Support</h3>
      <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
        <p style={{ fontSize: '13px', color: 'var(--text-dim)', margin: '0 0 12px' }}>
          Generate a diagnostic bundle saved to <code>%ProgramData%\GKMacOpener\Support\</code>.
        </p>
        <button className="btn btn-outline" style={{ width: 'auto', padding: '8px 16px' }} onClick={doGenerateBundle} disabled={bundleStatus === 'generating'}>
          {bundleStatus === 'generating' ? 'Generating...' : 'Generate Support Bundle'}
        </button>
        {bundleStatus && bundleStatus !== 'generating' && (
          <div style={{ marginTop: '12px', fontSize: '13px', color: bundleStatus.error ? 'var(--danger)' : 'var(--success)' }}>
            {bundleStatus.error ? `Error: ${bundleStatus.error}` : `Saved to: ${bundleStatus.path}`}
          </div>
        )}
      </div>

      <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>About</h3>
      <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
        <SettingsRow label="App" value="GKMacOpener" />
        <SettingsRow label="Version" value={setup?.version || APP_VERSION_FALLBACK} />
        <SettingsRow label="Developed by" value="George Karagioules" />
        <SettingsRow label="License" value="MIT" />
        <SettingsRow label="Copyright" value={COPYRIGHT_NOTICE} />
        <div style={{ padding: '12px 0 0', color: 'var(--text-dim)', fontSize: '12px', lineHeight: 1.6 }}>
          <div>{WINFSP_NOTICE}</div>
          <div>https://github.com/winfsp/winfsp</div>
          <div style={{ marginTop: '8px' }}>Full notices are available from the Help menu.</div>
        </div>
      </div>
    </section>
  );

  const renderContent = () => {
    if (activeTab === 'drives') return renderDrives();
    if (activeTab === 'logs') return renderLogs();
    if (activeTab === 'settings') return renderSettings();
    return null;
  };

  return (
    <div className="app-container">
      <aside className="sidebar">
        <div className="sidebar-header">
          <div className="logo">
            <img src={appLogo} alt="" aria-hidden="true" />
          </div>
          <h2>GKMacOpener</h2>
        </div>
        <nav className="nav-list">
          <li className={`nav-item ${activeTab === 'drives' ? 'active' : ''}`} onClick={() => setActiveTab('drives')}>
            <DriveIcon /> Drives
            {!setup.ready && <span style={{ marginLeft: 'auto', width: 6, height: 6, background: setup.status === 'failed' ? 'var(--danger)' : 'var(--primary)', display: 'inline-block', animation: setup.status !== 'failed' ? 'pulse 1.5s infinite' : 'none' }} />}
          </li>
          <li className={`nav-item ${activeTab === 'logs' ? 'active' : ''}`} onClick={() => setActiveTab('logs')}>
            <FolderIcon /> Logs
          </li>
          <li className={`nav-item ${activeTab === 'settings' ? 'active' : ''}`} onClick={() => setActiveTab('settings')}>
            <SettingsIcon /> Settings
          </li>
        </nav>
        <div style={{
          marginTop: 'auto',
          padding: '14px 16px',
          borderTop: '1px solid var(--border)',
          background: '#050505'
        }}>
          {update?.available ? (
            <button
              className="btn btn-primary"
              style={{ width: '100%', padding: '8px 10px', fontSize: '11px', letterSpacing: '0.5px' }}
              onClick={onUpdateNow}
            >
              Update to {update.version}
            </button>
          ) : (
            <button
              className="btn btn-outline"
              style={{ width: '100%', padding: '8px 10px', fontSize: '11px', letterSpacing: '0.5px' }}
              onClick={runManualUpdateCheck}
              disabled={manualCheckBusy}
            >
              {manualCheckBusy ? 'Checking...' : 'Check for updates'}
            </button>
          )}
          <div style={{
            marginTop: '8px',
            textAlign: 'center',
            opacity: 0.4,
            fontSize: '10px',
            fontFamily: 'var(--font-mono)',
            letterSpacing: '1px'
          }}>
            v{setup?.version || APP_VERSION_FALLBACK}
          </div>
        </div>
      </aside>

      <main className="main-content">
        {updateCheckNotice && (
          <div className={`update-check-notice ${updateCheckNotice.type}`} role="status">
            <span>{updateCheckNotice.message}</span>
            <button type="button" aria-label="Dismiss update status" onClick={() => setUpdateCheckNotice(null)}>x</button>
          </div>
        )}
        {renderContent()}
      </main>

      {updateModalOpen && update && (
        <UpdateModal update={update} onClose={() => setUpdateModalOpen(false)} />
      )}

      {passwordPrompt && (
        <div className="modal-overlay fade-in">
          <div className="modal-content glass" style={{ maxWidth: '400px', width: '90%' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: '12px', marginBottom: '20px' }}>
              <div style={{ width: 36, height: 36, background: 'rgba(229,83,0,0.1)', border: '1px solid rgba(229,83,0,0.2)', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="var(--primary)" strokeWidth="2" strokeLinecap="square"><rect x="3" y="11" width="18" height="11" /><path d="M7 11V7a5 5 0 0 1 10 0v4" /></svg>
              </div>
              <div>
                <h3 style={{ margin: 0, fontFamily: 'var(--font-heading)', letterSpacing: '2px', textTransform: 'uppercase', fontSize: '15px' }}>Encrypted Drive</h3>
                <span style={{ fontSize: '11px', opacity: 0.5, fontFamily: 'var(--font-mono)', letterSpacing: '1px', textTransform: 'uppercase' }}>FileVault Active</span>
              </div>
            </div>
            <p style={{ fontSize: '14px', marginBottom: '20px' }}>
              Enter the password to unlock <strong>{passwordPrompt.name}</strong>.
            </p>
            <input
              type="password"
              className="form-input"
              style={{ marginBottom: '20px', width: '100%', fontSize: '16px', padding: '12px' }}
              placeholder="Disk Password"
              autoFocus
              value={passwordValue}
              onChange={(e) => setPasswordValue(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && mountDrive(passwordPrompt.id, passwordValue)}
            />
            <div style={{ display: 'flex', gap: '12px' }}>
              <button className="btn btn-primary" style={{ flex: 1 }} onClick={() => mountDrive(passwordPrompt.id, passwordValue)} disabled={isMounting !== null}>
                {isMounting ? 'Unlocking...' : 'Unlock Drive'}
              </button>
              <button className="btn btn-outline" style={{ width: 'auto' }} onClick={() => { setPasswordPrompt(null); setPasswordValue(''); }}>
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
