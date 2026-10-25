#!/usr/bin/env node
// Visible boxes of the spoken step's figures (WP-23, D-1383): the web's W4c close-up places a figure by the box of its OPAQUE pixels, not the image's
// (src/ui/components/combatSpriteGeometry.js imageBounds: alpha >= 40, found from the edges in; visibleArtBox: ground = the last opaque row + 1), so the
// port needs the same four numbers per figure. This writes them into ui/components.json `dialogue.figures`, as fractions of the image:
//   id -> { top, height, centerX, width }      (top and height in image heights, centerX and width in image widths)
//
//   node Tools/figure-boxes.mjs [--base http://localhost:8731/] [--check]
//
// --base is a web build (the pinned b8cfe4fac) that serves the registry's `source` paths (assets/sprites/*.webp). The figures are the class sprites
// (class.<class>.sprite.<tint>, the player) and every enemy's base art (enemy.<id>.base, a speaker's portrait). --check recomputes and fails when the file differs.
// Needs Playwright Chromium (NODE_PATH=/opt/node-tools/node_modules), as Tools/ui-frames.mjs does.
import { readFileSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const CONTENT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content');
const baseArg = process.argv.indexOf('--base');
const BASE = baseArg > 0 ? process.argv[baseArg + 1] : 'http://localhost:8731/';
const CHECK = process.argv.includes('--check');
const ALPHA = 40;

const registry = JSON.parse(readFileSync(join(CONTENT, 'assets', 'registry.json'), 'utf8'));
const ids = Object.keys(registry).filter((id) => /^(class\.[^.]+\.sprite\.[^.]+|enemy\.[^.]+\.base)$/.test(id)).sort();

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const browser = await chromium.launch();
const page = await browser.newPage();
await page.goto(BASE);
const round = (v) => Math.round(v * 10000) / 10000;
const figures = {};
for (const id of ids) {
  const source = registry[id].source;
  const box = await page.evaluate(async ([url, alpha]) => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.src = url;
    await img.decode();
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth;
    canvas.height = img.naturalHeight;
    const context = canvas.getContext('2d', { willReadFrequently: true });
    context.drawImage(img, 0, 0);
    const { data } = context.getImageData(0, 0, canvas.width, canvas.height);
    const w = canvas.width, h = canvas.height;
    const opaque = (x, y) => data[(y * w + x) * 4 + 3] >= alpha;
    const rowHit = (y) => { for (let x = 0; x < w; x++) if (opaque(x, y)) return true; return false; };
    let y0 = 0; while (y0 < h && !rowHit(y0)) y0++;
    if (y0 >= h) return null;
    let y1 = h - 1; while (y1 > y0 && !rowHit(y1)) y1--;
    const colHit = (x) => { for (let y = y0; y <= y1; y++) if (opaque(x, y)) return true; return false; };
    let x0 = 0; while (!colHit(x0)) x0++;
    let x1 = w - 1; while (x1 > x0 && !colHit(x1)) x1--;
    return { w, h, x0, y0, x1, y1 };
  }, [new URL(source, BASE).href, ALPHA]);
  if (!box) continue;
  figures[id] = {
    top: round(box.y0 / box.h),
    height: round((box.y1 + 1 - box.y0) / box.h),
    centerX: round(((box.x0 + box.x1 + 1) / 2) / box.w),
    width: round((box.x1 + 1 - box.x0) / box.w),
  };
}
await browser.close();

const path = join(CONTENT, 'ui', 'components.json');
const text = readFileSync(path, 'utf8');
const doc = JSON.parse(text);
if (!doc.dialogue) throw new Error('ui/components.json has no dialogue object: add it (slot, portraits) before generating figures');
const next = { ...doc.dialogue, figures };
// One figure per line, so the file diffs by figure.
const rendered = '"figures": {\n' + Object.entries(figures).map(([id, box]) => `      "${id}": { "top": ${box.top}, "height": ${box.height}, "centerX": ${box.centerX}, "width": ${box.width} }`).join(',\n') + '\n    }';
const pattern = /"figures": \{[\s\S]*?\n    \}/;
if (!pattern.test(text)) throw new Error('ui/components.json dialogue.figures placeholder not found');
const out = text.replace(pattern, rendered);
if (CHECK) {
  if (out !== text) { console.error('figure-boxes: drift in ui/components.json dialogue.figures'); process.exit(1); }
  console.log(`figure-boxes: check ok (${ids.length} figures)`);
} else {
  writeFileSync(path, out);
  console.log(`figure-boxes: wrote ${Object.keys(next.figures).length} figures`);
}
