const { app, BrowserWindow, dialog, Menu, shell } = require('electron');
const path = require('path');
const fs = require('fs');
const { execSync, execFile } = require('child_process');

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
                label: 'Third-Party Notices',
                click: () => openLegalFile('THIRD_PARTY_NOTICES.txt', 'Third-Party Notices')
            },
            {
                label: 'GPL Source Offer',
                click: () => openLegalFile('GPL_SOURCE_OFFER.txt', 'GPL Source Offer')
            },
            { type: 'separator' },
            {
                label: 'MacMount License (MIT)',
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
        execSync('net session', { stdio: 'ignore' });
        return true;
    } catch {
        return false;
    }
}

function relaunchAsAdmin() {
    const exePath = process.execPath;
    const argv = process.argv.slice(1);

    // Pass --prod through UAC elevation since env vars are not inherited by Start-Process -Verb RunAs.
    if (process.env.NODE_ENV === 'production' && !argv.includes('--prod')) {
        argv.push('--prod');
    }

    // Preserve app arguments so elevated relaunch starts MacMount, not the Electron default app.
    const escapedArgv = argv.map((arg) => `'${String(arg).replace(/'/g, "''")}'`).join(', ');
    const psCommand = escapedArgv.length > 0
        ? `Start-Process -Verb RunAs -FilePath '${exePath.replace(/'/g, "''")}' -ArgumentList @(${escapedArgv})`
        : `Start-Process -Verb RunAs -FilePath '${exePath.replace(/'/g, "''")}'`;

    // Silently elevate — no info dialog needed (UAC prompt appears if enabled, or elevation is automatic)
    console.log('[MacMount] Requesting elevation via PowerShell RunAs...');

    execFile(
        'powershell.exe',
        ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', psCommand],
        { env: process.env, windowsHide: true },
        (error) => {
            if (error) {
                console.error('Failed to relaunch as admin:', error);
                dialog.showErrorBox(
                    'Administrator Rights Required',
                    'MacMount could not restart as Administrator automatically.\n\n' +
                    'Right-click MacMount (or your terminal) and choose "Run as administrator", then start the app again.'
                );
            }
            app.quit();
        }
    );
}

function createWindow() {
    mainWindow = new BrowserWindow({
        width: 1200,
        height: 800,
        title: "MacMount - Mac Drive Manager",
        webPreferences: {
            preload: path.join(__dirname, 'preload.js'),
            nodeIntegration: false,
            contextIsolation: true,
            sandbox: true,
        },
        backgroundColor: '#000000',
        show: true
    });

    const isProd = process.env.NODE_ENV === 'production' || process.argv.includes('--prod');
    const isDev = !app.isPackaged && !isProd;
    const startUrl = app.isPackaged
        ? `file://${path.join(process.resourcesPath, 'app.asar', 'dist', 'index.html')}`
        : isDev
            ? 'http://localhost:5173'
            : `file://${path.join(__dirname, 'dist', 'index.html')}`;

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

app.on('ready', () => {
    // Check admin and relaunch if needed
    if (!isAdmin()) {
        console.log('Not running as admin, attempting to relaunch...');
        relaunchAsAdmin();
        return;
    }
    
    console.log('Starting MacMount as administrator...');
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
