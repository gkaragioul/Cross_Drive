const { spawn } = require('child_process');

const electronBinary = require('electron');
const env = { ...process.env, NODE_ENV: 'development' };
delete env.ELECTRON_RUN_AS_NODE;

const child = spawn(electronBinary, ['.'], {
  stdio: 'inherit',
  env,
  shell: false,
  windowsHide: true,
});

child.on('exit', (code) => {
  process.exit(code ?? 0);
});

child.on('error', (err) => {
  console.error('Failed to launch Electron:', err.message);
  process.exit(1);
});
