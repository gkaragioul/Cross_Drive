const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const GZIP_MAGIC = Buffer.from([0x1f, 0x8b, 0x08]);
const CONFIG_START = Buffer.from('IKCFG_ST');
const CONFIG_END = Buffer.from('IKCFG_ED');

function extractKernelConfig(binary) {
  let offset = -1;
  while ((offset = binary.indexOf(GZIP_MAGIC, offset + 1)) !== -1) {
    let kernel;
    try {
      kernel = zlib.gunzipSync(binary.subarray(offset));
    } catch {
      continue;
    }

    const start = kernel.indexOf(CONFIG_START);
    const end = start === -1 ? -1 : kernel.indexOf(CONFIG_END, start + CONFIG_START.length);
    if (end === -1) continue;

    const config = zlib.gunzipSync(kernel.subarray(start + CONFIG_START.length, end));
    if (!config.includes(Buffer.from('CONFIG_HFS_FS=m')) ||
        !config.includes(Buffer.from('CONFIG_HFSPLUS_FS=m'))) {
      throw new Error('Embedded kernel configuration does not enable the expected HFS modules');
    }
    return config;
  }
  throw new Error('No embedded IKCONFIG found in the supplied kernel image');
}

if (require.main === module) {
  const input = process.argv[2];
  const output = process.argv[3];
  if (!input || !output) {
    console.error('Usage: node scripts/extract-kernel-config.js <wsl_kernel> <output.config>');
    process.exitCode = 2;
  } else {
    const config = extractKernelConfig(fs.readFileSync(input));
    fs.mkdirSync(path.dirname(output), { recursive: true });
    fs.writeFileSync(output, config);
    console.log(`Extracted ${config.length} bytes of kernel configuration to ${output}`);
  }
}

module.exports = { extractKernelConfig };
