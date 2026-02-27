const { contextBridge } = require('electron');

contextBridge.exposeInMainWorld('macmount', {
  platform: process.platform,
});

