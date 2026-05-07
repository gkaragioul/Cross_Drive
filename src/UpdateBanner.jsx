import React from 'react';

export default function UpdateBanner({ update, onUpdateNow, onLater, onSkip }) {
  if (!update || !update.available) return null;
  return (
    <div style={{
      backgroundColor: 'rgba(229,83,0,0.08)',
      border: '1px solid rgba(229,83,0,0.3)',
      color: 'var(--primary)',
      padding: '14px 18px',
      marginBottom: '20px',
      display: 'flex',
      alignItems: 'center',
      gap: '12px',
      fontSize: '12px',
      fontFamily: 'var(--font-mono)',
      letterSpacing: '0.5px'
    }}>
      <span style={{ fontSize: 18 }}>&#x21bb;</span>
      <div style={{ flex: 1 }}>
        <strong>Update available: {update.version}</strong>
        {update.current && (
          <span style={{ opacity: 0.7, marginLeft: 8 }}>(installed: v{update.current})</span>
        )}
      </div>
      <button className="btn btn-primary" style={{ width: 'auto', padding: '6px 14px', fontSize: '11px' }} onClick={onUpdateNow}>
        Update now
      </button>
      <button className="btn btn-outline" style={{ width: 'auto', padding: '6px 12px', fontSize: '11px' }} onClick={onLater}>
        Later
      </button>
      <button className="btn btn-outline" style={{ width: 'auto', padding: '6px 12px', fontSize: '11px' }} onClick={onSkip}>
        Skip this version
      </button>
    </div>
  );
}
