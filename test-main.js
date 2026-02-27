const { app, BrowserWindow } = require('electron');

function createWindow() {
    const mainWindow = new BrowserWindow({
        width: 1200,
        height: 800,
        title: "MacMount Test",
        backgroundColor: '#000000',
    });

    mainWindow.loadURL('file://' + __dirname + '/dist/index.html');
}

app.on('ready', () => {
    console.log('App is ready, creating window...');
    createWindow();
});

app.on('window-all-closed', () => {
    app.quit();
});
