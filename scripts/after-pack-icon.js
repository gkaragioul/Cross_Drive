const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

module.exports = async function afterPackIcon(context) {
  if (context.electronPlatformName !== 'win32') return;

  const root = context.packager.projectDir;
  const exePath = path.join(context.appOutDir, 'CrossDrive.exe');
  const iconPath = path.join(root, 'build', 'icon.ico');
  const rceditPath = path.join(root, 'node_modules', 'electron-winstaller', 'vendor', 'rcedit.exe');

  if (!fs.existsSync(exePath) || !fs.existsSync(iconPath) || !fs.existsSync(rceditPath)) {
    return;
  }

  execFileSync(rceditPath, [
    exePath,
    '--set-icon', iconPath,
    '--set-version-string', 'FileDescription', 'CrossDrive',
    '--set-version-string', 'ProductName', 'CrossDrive',
    '--set-version-string', 'InternalName', 'CrossDrive',
    '--set-version-string', 'OriginalFilename', 'CrossDrive.exe'
  ], { stdio: 'inherit', windowsHide: true });
};
