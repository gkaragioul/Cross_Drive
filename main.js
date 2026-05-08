const { app, BrowserWindow, dialog, Menu, shell, ipcMain } = require('electron');
const path = require('path');
const fs = require('fs');
const { execSync, execFile } = require('child_process');

const APP_NAME = 'GKMacOpener';
const APP_ID = 'com.gkmacopener.app';
const COPYRIGHT_NOTICE = 'Copyright (c) 2026 GKMacOpener contributors';
const WINFSP_NOTICE = 'WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos';

app.setName(APP_NAME);
if (process.platform === 'win32') {
    app.setAppUserModelId(APP_ID);
}

function resolveAppIconPath() {
    const candidates = [];
    if (app.isPackaged && process.resourcesPath) {
        candidates.push(path.join(process.resourcesPath, 'icon.ico'));
        candidates.push(path.join(process.resourcesPath, 'icon.png'));
    }
    candidates.push(path.join(__dirname, 'build', 'icon.ico'));
    candidates.push(path.join(__dirname, 'build', 'icon.png'));
    return candidates.find((candidate) => fs.existsSync(candidate));
}

function resolveBundledLegalPath(fileName) {
    if (app.isPackaged && process.resourcesPath) {
        const res = path.join(process.resourcesPath, fileName);
        if (fs.existsSync(res)) return res;
    }
    const dev = path.join(__dirname, 'build', fileName);
    if (fs.existsSync(dev)) return dev;
    if (process.resourcesPath) {
        return path.join(process.resourcesPath, fileName);
    }
    return dev;
}

function openLegalFile(fileName, title) {
    const p = resolveBundledLegalPath(fileName);
    shell.openPath(p).then((err) => {
        if (err) {
            dialog.showErrorBox(
                title,
                `Could not open:\n${p}\n\n${err}`
            );
        }
    });
}

function showAboutDialog() {
    dialog.showMessageBox({
        type: 'info',
        title: `About ${APP_NAME}`,
        message: APP_NAME,
        detail: [
            `Version ${app.getVersion()}`,
            'Developed by George Karagioules',
            COPYRIGHT_NOTICE,
            'License: MIT',
            '',
            WINFSP_NOTICE,
            'https://github.com/winfsp/winfsp',
            '',
            'See Help > License, Third-Party Notices, and GPL Source Offer for full legal notices.'
        ].join('\n'),
        buttons: ['OK']
    });
}

function installAppMenu() {
    const template = [];
    if (process.platform === 'darwin') {
        template.push({
            label: app.name,
            submenu: [
                { role: 'about' },
                { type: 'separator' },
                { role: 'quit' }
            ]
        });
    }
    template.push({
        label: 'Help',
        submenu: [
            {
                label: `About ${APP_NAME}`,
                click: showAboutDialog
            },
            { type: 'separator' },
            {
                label: 'Third-Party Notices',
                click: () => openLegalFile('THIRD_PARTY_NOTICES.txt', 'Third-Party Notices')
            },
            {
                label: 'GPL Source Offer',
                click: () => openLegalFile('GPL_SOURCE_OFFER.txt', 'GPL Source Offer')
            },
            {
                label: 'GNU GPL v2 (kernel + modules)',
                click: () => openLegalFile('LICENSE.GPL-2.0.txt', 'GNU GPL v2')
            },
            { type: 'separator' },
            {
                label: `${APP_NAME} License (MIT)`,
                click: () => openLegalFile('LICENSE.txt', 'License')
            }
        ]
    });
    Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

let mainWindow;
let backendModule = null;

// Check if running as Administrator (required for wsl --mount)
function isAdmin() {
    try {
        // windowsHide:true keeps `net.exe` from flashing a console window on
        // every app start. stdio:'ignore' alone isn't enough — `net.exe` is
        // a console-subsystem app and Windows allocates a conhost for it
        // unless we pass CREATE_NO_WINDOW (which Node maps from windowsHide).
        execSync('net session', { stdio: 'ignore', windowsHide: true });
        return true;
    } catch {
        return false;
    }
}

function relaunchAsAdmin() {
    const exePath = process.execPath;
    const argv = process.argv.slice(1);

    if (process.env.NODE_ENV === 'production' && !argv.includes('--prod')) {
        argv.push('--prod');
    }

    const argList = argv.map((a) => `'${String(a).replace(/'/g, "''")}'`).join(',');
    const psCommand = argList.length > 0
        ? `Start-Process -Verb RunAs -FilePath '${exePath.replace(/'/g, "''")}' -ArgumentList @(${argList})`
        : `Start-Process -Verb RunAs -FilePath '${exePath.replace(/'/g, "''")}'`;

    const encoded = Buffer.from(psCommand, 'utf16le').toString('base64');

    console.log(`[${APP_NAME}] Requesting elevation via PowerShell RunAs...`);

    execFile(
        'powershell.exe',
        ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded],
        { env: process.env, windowsHide: true },
        (error) => {
            if (error) {
                console.error('Failed to relaunch as admin:', error);
                dialog.showErrorBox(
                    'Administrator Rights Required',
                    `${APP_NAME} could not restart as Administrator automatically.\n\n` +
                    `Right-click ${APP_NAME} (or your terminal) and choose "Run as administrator", then start the app again.`
                );
            }
            app.quit();
        }
    );
}

function createWindow() {
    const iconPath = resolveAppIconPath();
    mainWindow = new BrowserWindow({
        width: 1200,
        height: 800,
        title: `${APP_NAME} - Mac Drive Manager`,
        webPreferences: {
            preload: path.join(__dirname, 'preload.js'),
            nodeIntegration: false,
            contextIsolation: true,
            sandbox: true,
        },
        backgroundColor: '#000000',
        show: true,
        ...(iconPath ? { icon: iconPath } : {})
    });

    const isProd = process.env.NODE_ENV === 'production' || process.argv.includes('--prod');
    const isDev = !app.isPackaged && !isProd;
    const startUrl = app.isPackaged
        ? `file://${path.join(process.resourcesPath, 'app.asar', 'dist', 'renderer', 'index.html')}`
        : isDev
            ? 'http://localhost:5173'
            : `file://${path.join(__dirname, 'dist', 'renderer', 'index.html')}`;

    console.log('Loading URL:', startUrl);
    console.log('Is packaged:', app.isPackaged);
    console.log('NODE_ENV:', process.env.NODE_ENV);

    mainWindow.loadURL(startUrl);

    mainWindow.on('closed', function () {
        mainWindow = null;
    });

    // Show window when ready
    mainWindow.once('ready-to-show', () => {
        mainWindow.show();
    });
}

function startBackend() {
    backendModule = require('./server');
    backendModule.startServer();
}

ipcMain.handle('quit-for-update', () => {
    console.log(`[${APP_NAME}] Quit-for-update requested by renderer.`);
    setTimeout(() => app.quit(), 250); // give the renderer time to settle the response
    return true;
});

app.on('ready', () => {
    // Check admin and relaunch if needed
    if (!isAdmin()) {
        console.log('Not running as admin, attempting to relaunch...');
        relaunchAsAdmin();
        return;
    }
    
    console.log(`Starting ${APP_NAME} as administrator...`);
    installAppMenu();
    startBackend();
    createWindow();
});

app.on('window-all-closed', function () {
    if (process.platform !== 'darwin') {
        app.quit();
    }
});

app.on('quit', () => {
    if (backendModule && typeof backendModule.stopServer === 'function') {
        backendModule.stopServer();
    }
});

app.on('activate', function () {
    if (mainWindow === null) {
        createWindow();
    }
});
