#!/usr/bin/env node
// Builds docs/screens/index.html: each Unity capture (docs/screens/<fixture>_<w>x<h>.png) beside the web shot it
// mirrors (docs/qa/web-screens/<shot>-<w>x<h>.jpg). The pairing is the fixture table in docs/qa/screen-capture-list.md.
// The screens workflow runs it after the capture and uploads docs/screens/ as one artifact (web shots copied in).
//   node Tools/ci/screens-compare.mjs [--check]
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..', '..');
const listPath = path.join(root, 'docs/qa/screen-capture-list.md');
const webDir = path.join(root, 'docs/qa/web-screens');
const outDir = path.join(root, 'docs/screens');
const sizes = ['1920x1080', '390x844'];
const check = process.argv.includes('--check');

export function parsePairs(markdown) {
  const pairs = [];
  for (const line of markdown.split('\n')) {
    const cells = line.split('|').map(c => c.trim());
    if (cells.length < 4 || !cells[1].includes('`c2_')) continue;
    const ticks = s => [...s.matchAll(/`([^`]+)`/g)].map(m => m[1]);
    pairs.push({ fixtures: ticks(cells[1]), shots: ticks(cells[2]) });
  }
  return pairs;
}

const pairs = parsePairs(fs.readFileSync(listPath, 'utf8'));
const problems = [];
for (const p of pairs) {
  for (const f of p.fixtures) if (!fs.existsSync(path.join(root, 'Unity/Assets/Tests/Fixtures/screens', f + '.json'))) problems.push('no fixture ' + f);
  for (const s of p.shots) for (const z of sizes) if (!fs.existsSync(path.join(webDir, `${s}-${z}.jpg`))) problems.push(`no web shot ${s}-${z}.jpg`);
}
if (problems.length) { console.error(problems.join('\n')); process.exit(1); }
if (check) { console.log(`screens-compare: ${pairs.length} rows pair cleanly`); process.exit(0); }

fs.mkdirSync(path.join(outDir, 'web'), { recursive: true });
const esc = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
let missing = 0;
const rows = [];
for (const z of sizes) {
  rows.push(`<h2>${z}</h2>`);
  for (const p of pairs) {
    const web = p.shots.map(s => {
      fs.copyFileSync(path.join(webDir, `${s}-${z}.jpg`), path.join(outDir, 'web', `${s}-${z}.jpg`));
      return `<figure><img loading="lazy" src="web/${esc(s)}-${z}.jpg"><figcaption>web ${esc(s)}</figcaption></figure>`;
    }).join('');
    const unity = p.fixtures.map(f => {
      const file = `${f}_${z}.png`;
      if (!fs.existsSync(path.join(outDir, file))) { missing++; return `<figure class="miss"><figcaption>unity ${esc(f)}: not captured</figcaption></figure>`; }
      return `<figure><img loading="lazy" src="${esc(file)}"><figcaption>unity ${esc(f)}</figcaption></figure>`;
    }).join('');
    rows.push(`<section class="row s${z}"><div class="side">${web}</div><div class="side">${unity}</div></section>`);
  }
}
fs.writeFileSync(path.join(outDir, 'index.html'), `<!doctype html><meta charset="utf-8"><title>Unity vs web screens</title>
<style>body{background:#111;color:#ddd;font:14px system-ui;margin:16px}section{display:grid;grid-template-columns:1fr 1fr;gap:12px;border-top:1px solid #333;padding:12px 0}
.side{display:flex;flex-wrap:wrap;gap:8px}figure{margin:0;flex:1 1 300px}.s390x844 figure{flex:0 1 220px}img{width:100%;display:block}figcaption{color:#999;padding:4px 0}.miss{border:1px dashed #a44;padding:8px}</style>
<h1>Unity (right) vs web (left)</h1>${rows.join('\n')}`);
console.log(`screens-compare: ${pairs.length} rows, ${missing} Unity captures missing -> docs/screens/index.html`);
