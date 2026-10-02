#!/usr/bin/env node
// Refreshes Content/manifest.json for the hand-written content files and the committed schemas only, without the
// shipped export (Tools/export/raw) that transform-content.mjs needs. Generated files must be unchanged: their manifest
// hash is checked, never rewritten. Also writes a Unity .meta (a GUID derived from the path) for any file under
// Unity/Assets/StreamingAssets/Content or Unity/Assets/Game that has none, as the editor would on import.
//
//   node Tools/refresh-manifest.mjs           write
//   node Tools/refresh-manifest.mjs --check   fail on drift
import { readFileSync, writeFileSync, existsSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const CONTENT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content');
const CHECK = process.argv.includes('--check');
const sha256 = (s) => createHash('sha256').update(s, 'utf8').digest('hex');
function sortKeys(v) {
  if (Array.isArray(v)) return v.map(sortKeys);
  if (v && typeof v === 'object') {
    const out = {};
    for (const k of Object.keys(v).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))) out[k] = sortKeys(v[k]);
    return out;
  }
  return v;
}
const sortedCanonical = (v) => `${JSON.stringify(sortKeys(v), null, 2)}\n`;
const fail = (m) => { console.error(`refresh-manifest: ${m}`); process.exit(1); };

// The one list of hand-written files lives in transform-content.mjs.
const src = readFileSync(join(ROOT, 'Tools', 'transform-content.mjs'), 'utf8');
const m = src.match(/const HAND_WRITTEN = new Set\(\[([^\]]*)\]\)/);
if (!m) fail('HAND_WRITTEN not found in transform-content.mjs');
const handWritten = new Set([...m[1].matchAll(/'([^']+)'/g)].map((x) => x[1]));
for (const f of readdirSync(join(CONTENT, 'schemas')).filter((f) => f.endsWith('.json'))) handWritten.add(`schemas/${f}`);

const manifestPath = join(CONTENT, 'manifest.json');
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const read = (rel) => readFileSync(join(CONTENT, rel), 'utf8').replace(/\r\n/g, '\n');
for (const [rel, entry] of Object.entries(manifest.files)) {
  if (handWritten.has(rel)) continue;
  if (!existsSync(join(CONTENT, rel))) fail(`generated file missing: ${rel}`);
  if (sha256(read(rel)) !== entry.sha256) fail(`generated file changed by hand: ${rel} (run transform-content.mjs with the shipped export)`);
}
const files = { ...manifest.files };
for (const rel of handWritten) {
  if (!existsSync(join(CONTENT, rel))) { delete files[rel]; continue; }
  const text = read(rel);
  files[rel] = { sha256: sha256(text), bytes: Buffer.byteLength(text), generated: false };
}
const ordered = {};
for (const p of Object.keys(files).sort()) ordered[p] = files[p];
const contentHash = sha256(Object.keys(ordered).sort().map((p) => `${p}:${ordered[p].sha256}`).join('\n'));
const text = sortedCanonical({ ...manifest, contentHash, files: ordered });

const metas = [];
function walk(dir) {
  for (const f of readdirSync(dir)) {
    const p = join(dir, f);
    if (f.endsWith('.meta')) continue;
    if (!existsSync(`${p}.meta`)) metas.push(p);
    if (statSync(p).isDirectory()) walk(p);
  }
}
walk(CONTENT);
walk(join(ROOT, 'Unity', 'Assets', 'Game'));
walk(join(ROOT, 'Unity', 'Assets', 'Tests'));
function meta(p) {
  const guid = createHash('md5').update(relative(ROOT, p).replace(/\\/g, '/')).digest('hex');
  if (statSync(p).isDirectory()) return `fileFormatVersion: 2\nguid: ${guid}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n`;
  if (p.endsWith('.cs')) return `fileFormatVersion: 2\nguid: ${guid}\nMonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n`;
  return `fileFormatVersion: 2\nguid: ${guid}\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n`;
}

if (CHECK) {
  const drift = [];
  if (readFileSync(manifestPath, 'utf8').replace(/\r\n/g, '\n') !== text) drift.push('manifest.json');
  for (const p of metas) drift.push(`${relative(ROOT, p)}.meta (missing)`);
  if (drift.length) fail(`drift:\n  ${drift.join('\n  ')}`);
  console.log(`refresh-manifest: check ok (contentHash ${contentHash.slice(0, 12)})`);
} else {
  writeFileSync(manifestPath, text);
  for (const p of metas) writeFileSync(`${p}.meta`, meta(p));
  console.log(`refresh-manifest: wrote manifest (contentHash ${contentHash.slice(0, 12)}), ${metas.length} .meta files`);
}
