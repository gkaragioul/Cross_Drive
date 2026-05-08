import React, { useEffect, useState } from 'react';
import { startUpdateDownload, fetchUpdateProgress, launchUpdateInstaller } from './api';

export default function UpdateModal({ update, onClose }) {
  const [phase, setPhase] = useState('idle'); // idle | downloading | verifying | ready | launching | error
  const [progress, setProgress] = useState({ bytesDone: 0, totalBytes: 0 });
  const [installerPath, setInstallerPath] = useState(null);
  const [error, setError] = useState(null);

  useEffect(() => {
    if (phase !== 'downloading') return;
    const t = setInterval(async () => {
      try {
        const p = await fetchUpdateProgress();
        if (p.status === 'running') setProgress({ bytesDone: p.bytesDone || 0, totalBytes: p.totalBytes || 0 });
        else if (p.status === 'done') {
          setProgress({ bytesDone: p.totalBytes, totalBytes: p.totalBytes });
          setInstallerPath(p.path);
          setPhase('ready');
        } else if (p.status === 'error') {
          setError(p.error || 'Download failed');
          setPhase('error');
        }
      } catch { /* ignore poll errors */ }
    }, 250);
    return () => clearInterval(t);
  }, [phase]);

  const startDownload = async () => {
    setError(null);
    setPhase('downloading');
    try {
      const r = await startUpdateDownload(update.downloadUrl, update.sha256, update.version);
      if (!r.accepted) {
        setError(r.error || 'Could not start download');
        setPhase('error');
      }
    } catch (e) {
      setError(e.message);
      setPhase('error');
    }
  };

  const installAndRelaunch = async () => {
    setPhase('launching');
    try {
      await launchUpdateInstaller(installerPath, update.version);
      // tell main to quit so the helper script can run the installer
      if (window.crossdrive?.invoke) {
        try { await window.crossdrive.invoke('quit-for-update'); } catch {}
      }
    } catch (e) {
      setError(e.message);
      setPhase('error');
    }
  };

  const pct = progress.totalBytes > 0 ? Math.floor(progress.bytesDone / progress.totalBytes * 100) : 0;
  const mb = (n) => (n / 1048576).toFixed(1);

  return (
    <div className="modal-overlay fade-in">
      <div className="modal-content glass" style={{ maxWidth: 560, width: '92%' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 16 }}>
          <h3 style={{ margin: 0, fontFamily: 'var(--font-heading)', letterSpacing: 2, textTransform: 'uppercase', fontSize: 15 }}>
            Update CrossDrive
          </h3>
          <span style={{ fontSize: 11, opacity: 0.6, fontFamily: 'var(--font-mono)' }}>
            v{update.current} &rarr; {update.version}
          </span>
        </div>

        <div style={{
          background: '#080808',
          border: '1px solid var(--border)',
          padding: 14,
          height: 200,
          overflowY: 'auto',
          fontFamily: 'var(--font-mono)',
          fontSize: 12,
          color: 'var(--text-dim)',
          marginBottom: 16,
          whiteSpace: 'pre-wrap'
        }}>
          {update.releaseNotes || '(no release notes)'}
        </div>

        {phase === 'downloading' && (
          <div style={{ marginBottom: 16 }}>
            <div style={{ fontSize: 12, fontFamily: 'var(--font-mono)', marginBottom: 6 }}>
              Downloading {mb(progress.bytesDone)} MB / {mb(progress.totalBytes)} MB ({pct}%)
            </div>
            <div style={{ height: 8, background: '#222', border: '1px solid var(--border)' }}>
              <div style={{ width: `${pct}%`, height: '100%', background: 'var(--primary)', transition: 'width 200ms' }} />
            </div>
          </div>
        )}

        {phase === 'ready' && (
          <div style={{ fontSize: 12, color: 'var(--success)', marginBottom: 16, fontFamily: 'var(--font-mono)' }}>
            Download verified. Click Install to launch the installer and restart the app.
          </div>
        )}

        {phase === 'error' && (
          <div style={{ fontSize: 12, color: 'var(--danger)', marginBottom: 16, fontFamily: 'var(--font-mono)' }}>
            ERROR: {error}
          </div>
        )}

        <div style={{ display: 'flex', gap: 12, justifyContent: 'flex-end' }}>
          {phase === 'idle' && (
            <>
              <button className="btn btn-outline" style={{ width: 'auto' }} onClick={onClose}>Cancel</button>
              <button className="btn btn-primary" style={{ width: 'auto' }} onClick={startDownload}>Download &amp; Install</button>
            </>
          )}
          {phase === 'downloading' && (
            <button className="btn btn-outline" style={{ width: 'auto' }} disabled>Downloading...</button>
          )}
          {phase === 'ready' && (
            <>
              <button className="btn btn-outline" style={{ width: 'auto' }} onClick={onClose}>Close</button>
              <button className="btn btn-primary" style={{ width: 'auto' }} onClick={installAndRelaunch}>Install &amp; Relaunch</button>
            </>
          )}
          {phase === 'launching' && (
            <button className="btn btn-outline" style={{ width: 'auto' }} disabled>Launching installer...</button>
          )}
          {phase === 'error' && (
            <>
              {error && !error.includes('Integrity check') && (
                <button className="btn btn-primary" style={{ width: 'auto' }} onClick={startDownload}>Retry</button>
              )}
              <button className="btn btn-outline" style={{ width: 'auto' }} onClick={onClose}>Close</button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
