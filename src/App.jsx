import React, { useState, useEffect } from 'react';
import './index.css';

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

// Animated dots for loading text
const Dots = () => {
  const [count, setCount] = useState(0);
  useEffect(() => {
    const t = setInterval(() => setCount(c => (c + 1) % 4), 500);
    return () => clearInterval(t);
  }, []);
  return <span>{'.'.repeat(count)}</span>;
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
  const [passwordPrompt, setPasswordPrompt] = useState(null); // { id, name }
  const [passwordValue, setPasswordValue] = useState('');
  const [runtimeConfig, setRuntimeConfig] = useState(null);
  const [bundleStatus, setBundleStatus] = useState(null); // null | 'generating' | { path } | { error }

  useEffect(() => {
    fetchDrives();
    const logInterval = setInterval(fetchLogs, 2000);
    const statusInterval = setInterval(fetchStatus, 3000);
    const nativeInterval = setInterval(fetchNativeStatus, 3000);
    const driveInterval = setInterval(fetchDrives, 5000);
    fetchStatus();
    fetchNativeStatus();
    fetchRuntimeConfig();
    return () => {
      clearInterval(logInterval);
      clearInterval(statusInterval);
      clearInterval(nativeInterval);
      clearInterval(driveInterval);
    };
  }, []);

  const fetchStatus = async () => {
    try {
      const res = await fetch('http://localhost:3001/api/status');
      const data = await res.json();
      setSetup(data);
    } catch (e) { /* silent */ }
  };

  const fetchRuntimeConfig = async () => {
    try {
      const res = await fetch('http://localhost:3001/api/runtime/config');
      const data = await res.json();
      setRuntimeConfig(data);
    } catch (e) { /* silent */ }
  };

  const fetchNativeStatus = async () => {
    try {
      const res = await fetch('http://localhost:3001/api/native/status');
      const data = await res.json();
      setNativeStatus(data);
    } catch (e) {
      setNativeStatus({ available: false });
    }
  };

  const fetchLogs = async () => {
    try {
      const response = await fetch('http://localhost:3001/api/logs');
      const data = await response.json();
      setLogs(data);
    } catch (e) { /* silent */ }
  };

  const logRemote = async (message, type = 'info') => {
    try {
      await fetch('http://localhost:3001/api/logs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ message, type })
      });
      fetchLogs();
    } catch (e) { }
  };

  const fetchDrives = async () => {
    try {
      const response = await fetch('http://localhost:3001/api/drives');
      const data = await response.json();
      if (data.error) {
        setDrives(data.mockData || []);
        setErrorMessage(data.error);
      } else {
        setDrives(Array.isArray(data) ? data : []);
        setErrorMessage(null);
      }
    } catch (error) {
      setErrorMessage("Could not connect to backend server.");
    } finally {
      setIsLoading(false);
    }
  };

  const mountDrive = async (id, password = '') => {
    setIsMounting(id);
    setErrorMessage(null);
    logRemote(`Frontend: User requested mount for ${id} ${password ? '(with password)' : ''}`);
    try {
      const response = await fetch('http://localhost:3001/api/mount', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id, password })
      });
      const text = await response.text();
      let result;
      try { result = JSON.parse(text); }
      catch (e) { setErrorMessage("Backend returned an invalid response."); return; }

      if (response.ok) {
        logRemote(`SUCCESS: Drive ${id} mounted at ${result.path}`, 'success');
        setErrorMessage(null);
        setPasswordPrompt(null);
        setPasswordValue('');
        fetchDrives();
      } else {
        if (result.needsPassword) {
          const drive = drives.find(d => d.id === id);
          setPasswordPrompt({ id, name: drive?.name || `Physical Drive ${id}` });
          return;
        }
        setErrorMessage(`${result.error}. ${result.suggestion || ''}`);
        logRemote(`Mount Failure: ${result.error}`, 'error');
      }
    } catch (error) {
      setErrorMessage("System error during mount.");
    } finally {
      setIsMounting(null);
    }
  };

  const unmountDrive = async (id) => {
    setIsMounting(id);
    setErrorMessage(null);
    logRemote(`Frontend: User requested unmount for ${id}`);
    try {
      const response = await fetch('http://localhost:3001/api/unmount', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id })
      });
      if (response.ok) {
        logRemote(`Drive ${id} unmounted.`, 'success');
        setErrorMessage(null);
        fetchDrives();
      } else {
        const result = await response.json();
        setErrorMessage(`Unmount failed: ${result.error || 'unknown error'}`);
      }
    } catch (error) {
      setErrorMessage("System error during unmount.");
    } finally {
      setIsMounting(null);
    }
  };

  const openInExplorer = async (p) => {
    await fetch('http://localhost:3001/api/open', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: p })
    });
  };

  const getExplorerTarget = (drive) => {
    // Prefer mountPath which includes the root/ subfolder for APFS containers
    if (drive?.mountPath) return drive.mountPath;
    if (drive?.driveLetter) {
      return `${String(drive.driveLetter).replace(':', '')}:\\`;
    }
    return '';
  };

  // Setup status banner
  const SetupBanner = () => {
    if (setup.status === 'ready') return null;

    const isInstalling = setup.status === 'installing' || setup.status === 'checking';
    const isFailed = setup.status === 'failed';

    return (
      <div style={{
        backgroundColor: isFailed ? 'rgba(192,57,43,0.1)' : 'rgba(229,83,0,0.08)',
        border: `1px solid ${isFailed ? 'var(--danger)' : 'rgba(229,83,0,0.3)'}`,
        color: isFailed ? 'var(--danger)' : 'var(--primary)',
        padding: '14px 18px',
        marginBottom: '20px',
        display: 'flex',
        alignItems: 'center',
        gap: '12px',
        fontSize: '12px',
        fontFamily: 'var(--font-mono)',
        letterSpacing: '0.5px'
      }}>
        {isInstalling && (
          <div style={{
            width: 14, height: 14,
            border: '2px solid var(--primary)', borderTopColor: 'transparent',
            animation: 'spin 0.8s linear infinite', flexShrink: 0
          }} />
        )}
        {isFailed && <span style={{ fontSize: 18 }}>⚠️</span>}
        <div>
          <strong>{isInstalling ? 'Setting up Mac drivers' : 'Setup failed'}</strong>
          {isInstalling && <Dots />}
          {' — '}
          <span style={{ opacity: 0.85 }}>{isInstalling ? 'Preparing runtime components. This can take a moment on first launch.' : setup.message}</span>
        </div>
      </div>
    );
  };

  const renderContent = () => {
    const environmentReady = setup.ready;

    if (activeTab === 'drives') {
      return (
        <>
          <section className="header-section fade-in">
            <h1>Physical Drives</h1>
            <p>Select a Mac-formatted drive to mount it as a Windows volume.</p>
          </section>

          <SetupBanner />

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
              {drives.map((drive, index) => (
                <div key={drive.id} className="drive-card fade-in" style={{ animationDelay: `${index * 0.1}s`, opacity: drive.isMac ? 1 : 0.6 }}>
                  <div className="drive-info">
                    <div className="drive-icon"><DriveIcon /></div>
                    <div className="drive-details">
                      <h3>{drive.name}</h3>
                      <span>{drive.size} • {drive.type} • <b style={{ color: drive.isMac ? 'var(--success)' : 'inherit' }}>{drive.format}</b></span>
                    </div>
                  </div>
                  <div className="mount-status">
                    <div className={`status-dot ${drive.mounted ? 'status-mounted' : 'status-unmounted'}`} />
                    {drive.mounted
                      ? `Mounted${drive.driveLetter ? ` as ${drive.driveLetter}:` : ''}`
                      : 'Unmounted'}
                  </div>
                  <div className="card-actions">
                    <button
                      className={`btn ${drive.mounted ? 'btn-outline' : 'btn-primary'}`}
                      onClick={() => drive.mounted ? unmountDrive(drive.id) : mountDrive(drive.id)}
                      disabled={isMounting !== null || (!drive.mounted && !environmentReady)}
                      title={!environmentReady && !drive.mounted ? 'Finish setup first.' : ''}
                    >
                      {isMounting === drive.id
                        ? (drive.mounted ? 'Unmounting...' : 'Mounting...')
                        : !environmentReady && !drive.mounted
                          ? '⏳ Preparing...'
                          : drive.mounted ? 'Unmount' : 'Mount Drive'}
                    </button>
                    <button
                      className="btn btn-outline"
                      disabled={!drive.mounted || !getExplorerTarget(drive)}
                      onClick={() => openInExplorer(getExplorerTarget(drive))}
                    >
                      Open Explorer
                    </button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </>
      );
    }

    if (activeTab === 'logs') {
      const copyLogs = () => {
        const text = logs.map(l => `[${l.timestamp}] [${l.type.toUpperCase()}] ${l.message}`).join('\n');
        navigator.clipboard.writeText(text);
        logRemote("Logs copied to clipboard", 'success');
      };

      return (
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
    }

    if (activeTab === 'settings') {
      const generateBundle = async () => {
        setBundleStatus('generating');
        try {
          const res = await fetch('http://localhost:3001/api/support/bundle');
          const data = await res.json();
          setBundleStatus(data.success ? { path: data.path } : { error: data.error || 'Unknown error' });
        } catch (e) {
          setBundleStatus({ error: e.message });
        }
      };

      const row = (label, value) => (
        <div style={{ display: 'flex', justifyContent: 'space-between', padding: '8px 0', borderBottom: '1px solid var(--border)' }}>
          <span style={{ color: 'var(--text-dim)', fontSize: '12px', letterSpacing: '1px', textTransform: 'uppercase' }}>{label}</span>
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '12px', color: 'var(--text-main)' }}>{value}</span>
        </div>
      );

      return (
        <section className="fade-in">
          <h1>Settings</h1>

          <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>Native Engine</h3>
          <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
            {row('Status', nativeStatus.available ? 'Connected' : 'Not connected')}
            {nativeStatus.available && row('Engine', nativeStatus.engine || 'unknown')}
            {nativeStatus.available && row('Local Fixed Support', nativeStatus.supportsLocalFixed ? 'Yes' : 'No')}
          </div>

          <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>Runtime Configuration</h3>
          <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
            {runtimeConfig ? (
              <>
                {row('Mount Mode', runtimeConfig.mode)}
                {row('Native Mount Enabled', runtimeConfig.nativeEnabled ? 'Yes' : 'No')}
                {row('Canary Rollout %', `${runtimeConfig.canaryPercent}%`)}
                {row('WSL Fallback Allowed', runtimeConfig.allowWslFallback ? 'Yes' : 'No')}
              </>
            ) : (
              <div style={{ color: 'var(--text-dim)', fontSize: '13px' }}>Loading...</div>
            )}
          </div>

          <h3 style={{ marginTop: '24px', marginBottom: '12px', opacity: 0.5, fontSize: '11px', textTransform: 'uppercase', letterSpacing: '2.5px', fontFamily: 'var(--font-heading)', color: 'var(--primary)' }}>Support</h3>
          <div style={{ background: '#0e0e0e', border: '1px solid var(--border)', padding: '16px' }}>
            <p style={{ fontSize: '13px', color: 'var(--text-dim)', margin: '0 0 12px' }}>
              Generate a diagnostic bundle saved to <code>%ProgramData%\MacMount\Support\</code>.
            </p>
            <button
              className="btn btn-outline"
              style={{ width: 'auto', padding: '8px 16px' }}
              onClick={generateBundle}
              disabled={bundleStatus === 'generating'}
            >
              {bundleStatus === 'generating' ? 'Generating...' : 'Generate Support Bundle'}
            </button>
            {bundleStatus && bundleStatus !== 'generating' && (
              <div style={{
                marginTop: '12px', fontSize: '13px',
                color: bundleStatus.error ? 'var(--danger)' : 'var(--success)'
              }}>
                {bundleStatus.error
                  ? `Error: ${bundleStatus.error}`
                  : `Saved to: ${bundleStatus.path}`}
              </div>
            )}
          </div>
        </section>
      );
    }

    return null;
  };

  return (
    <div className="app-container">
      <aside className="sidebar">
        <div className="sidebar-header">
          <div className="logo">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="2.5" strokeLinecap="square" strokeLinejoin="miter">
              <rect x="2" y="4" width="20" height="16" rx="0" />
              <line x1="2" y1="10" x2="22" y2="10" />
              <line x1="12" y1="10" x2="12" y2="20" />
            </svg>
          </div>
          <h2>MacMount</h2>
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
      </aside>

      <main className="main-content">
        {renderContent()}
      </main>

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
              <button
                className="btn btn-primary"
                style={{ flex: 1 }}
                onClick={() => mountDrive(passwordPrompt.id, passwordValue)}
                disabled={isMounting !== null}
              >
                {isMounting ? 'Unlocking...' : 'Unlock Drive'}
              </button>
              <button
                className="btn btn-outline"
                style={{ width: 'auto' }}
                onClick={() => { setPasswordPrompt(null); setPasswordValue(''); }}
              >
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
