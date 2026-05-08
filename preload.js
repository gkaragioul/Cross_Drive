const { contextBridge, ipcRenderer } = require('electron');

const BACKEND_URL = 'http://localhost:3001';

contextBridge.exposeInMainWorld('crossdrive', {
  platform: process.platform,
  backendUrl: BACKEND_URL,

  invoke: (channel, ...args) => {
    const allowed = ['open-explorer', 'get-app-paths', 'quit-for-update', 'show-update-status-notification'];
    if (allowed.includes(channel)) {
      return ipcRenderer.invoke(channel, ...args);
    }
    return Promise.reject(new Error(`Blocked IPC channel: ${channel}`));
  },

  showUpdateStatusNotification: (message, type = 'info') =>
    ipcRenderer.invoke('show-update-status-notification', { message, type }),

  on: (channel, callback) => {
    const allowed = ['mount-complete', 'unmount-complete'];
    if (allowed.includes(channel)) {
      const handler = (_event, ...args) => callback(...args);
      ipcRenderer.on(channel, handler);
      return () => ipcRenderer.removeListener(channel, handler);
    }
    return () => {};
  },
});
