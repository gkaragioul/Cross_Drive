// One-shot icon generator. Reads the source logo PNG, pads it to 1024x1024
// on a transparent canvas, writes the padded PNG to build/icon.png and
// src/assets/crossdrive-logo.png, and generates a multi-resolution ICO at
// build/icon.ico. Re-run whenever the source logo changes.
const fs = require('fs');
const path = require('path');
const sharp = require('sharp');
const pngToIco = require('png-to-ico');

const root = path.resolve(__dirname, '..');
const SOURCE = path.join(root, 'Gemini_Generated_Image_hk2lpxhk2lpxhk2l-removebg-preview.png');
const OUT_PNG = path.join(root, 'build', 'icon.png');
const OUT_ICO = path.join(root, 'build', 'icon.ico');
const LOGO_PNG = path.join(root, 'src', 'assets', 'crossdrive-logo.png');

async function main() {
  if (!fs.existsSync(SOURCE)) {
    console.error(`Source logo not found: ${SOURCE}`);
    process.exit(1);
  }
  // 1) Trim transparent borders so the subject fills the canvas (without this
  //    the drive image ends up tiny inside a sea of transparent padding).
  // 2) Pad to a square ~8% larger than the long side, then resize to 1024x1024.
  //    The 8% margin keeps the subject from touching the icon edge.
  const trimmed = await sharp(SOURCE).trim({ threshold: 5 }).png().toBuffer();
  const meta = await sharp(trimmed).metadata();
  const longSide = Math.max(meta.width || 1, meta.height || 1);
  const target = Math.round(longSide / 0.92);
  const padded = await sharp(trimmed)
    .resize({ width: target, height: target, fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .resize({ width: 1024, height: 1024, fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .png()
    .toBuffer();
  fs.writeFileSync(OUT_PNG, padded);
  fs.writeFileSync(LOGO_PNG, padded);
  console.log(`Wrote ${OUT_PNG}`);
  console.log(`Wrote ${LOGO_PNG}`);

  const sizes = [256, 128, 64, 48, 32, 16];
  const buffers = await Promise.all(sizes.map(s =>
    sharp(padded).resize(s, s).png().toBuffer()
  ));
  const ico = await pngToIco(buffers);
  fs.writeFileSync(OUT_ICO, ico);
  console.log(`Wrote ${OUT_ICO} (sizes: ${sizes.join(',')})`);
}

main().catch(err => { console.error(err); process.exit(1); });
