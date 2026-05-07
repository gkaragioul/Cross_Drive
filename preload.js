const { contextBridge, ipcRenderer } = require('electron');

const BACKEND_URL = 'http://localhost:3001';

contextBridge.exposeInMainWorld('macmount', {
  platform: process.platform,
  backendUrl: BACKEND_URL,

  invoke: (channel, ...args) => {
    const allowed = ['open-explorer', 'get-app-paths'];
    if (allowed.includes(channel)) {
      return ipcRenderer.invoke(channel, ...args);
    }
    return Promise.reject(new Error(`Blocked IPC channel: ${channel}`));
  },

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
