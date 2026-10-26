#!/usr/bin/env node
// Asset pipeline (US-0.1, US-0.2; PF-01). Read-only against the shipped repo.
//
//   node Tools/scan-assets.mjs            scan → registry.json, ASSET-INVENTORY.md, ASSET-GAPS.md, Provenance/
//   node Tools/scan-assets.mjs --import   also convert the configured import subset into Unity/Assets/Art/Imported
//                                         (WebP → PNG via Blender headless, lossless + verified; SVG copied for the
//                                         built-in Vector Graphics importer)
//   node Tools/scan-assets.mjs --release <dir> [--web <repo>] [--import] [--check]
//                                         VP-2: add the art-release pack (hd-assets-v23, fetched by the web's
//                                         tools/fetch-art.mjs; <dir> holds high/ and common/) to the registry, additively,
//                                         and with --import rasterise/downscale it with sharp (docs/DECISIONS.md D-550..)
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, statSync, copyFileSync } from 'node:fs';
import { join, dirname, relative, extname } from 'node:path';
import { walk, webpInfo, svgInfo, applyTemplate, canonical, mb, inventoryText, INVENTORY_DERIVATIONS, gapsText } from './lib/assets-lib.mjs';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const argSource = process.argv.indexOf('--source');
const SOURCE = argSource > 0 ? process.argv[argSource + 1] : (process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const CONFIG = JSON.parse(readFileSync(join(ROOT, 'Tools', 'assets.config.json'), 'utf8'));
const IMPORT = process.argv.includes('--import');
const BLENDER = process.env.BLENDER_EXE || 'C:/Program Files/Blender Foundation/Blender 4.0/blender.exe';
const CONTENT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content');
const IMPORTED = join(ROOT, 'Unity', 'Assets', 'Art', 'Imported');
const rel = (p) => relative(SOURCE, p).replace(/\\/g, '/');

const argRelease = process.argv.indexOf('--release');
if (argRelease > 0) {
  const arg = (name) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : null; };
  const { runRelease } = await import('./lib/release-art.mjs');
  await runRelease({ ROOT, CONFIG, releaseDir: process.argv[argRelease + 1], webDir: arg('--web'), doImport: IMPORT, check: process.argv.includes('--check'), force: process.argv.includes('--force') });
  process.exit(0);
}

// ---------- scan ----------
const ignore = CONFIG.ignore.map(r => new RegExp(r));
const rules = CONFIG.rules.map(r => ({ re: new RegExp(r.match), id: r.id }));
const files = CONFIG.roots.flatMap(r => walk(join(SOURCE, r))).map(p => ({ abs: p, rel: rel(p) })).filter(f => !ignore.some(re => re.test(f.rel))).sort((a, b) => (a.rel < b.rel ? -1 : 1));
const registry = {};
const orphans = [];
const duplicates = [];
for (const f of files) {
  const rule = rules.find(r => r.re.test(f.rel));
  if (!rule) { orphans.push(f.rel); continue; }
  const id = applyTemplate(rule.id, rule.re.exec(f.rel));
  const buf = readFileSync(f.abs);
  const ext = extname(f.rel).toLowerCase();
  const info = ext === '.webp' ? webpInfo(buf) : ext === '.svg' ? svgInfo(buf.toString('utf8')) : null;
  if (registry[id]) { duplicates.push(`${id}: ${registry[id].source} | ${f.rel}`); continue; }
  const imported = CONFIG.import.some(p => id.startsWith(p));
  registry[id] = {
    origin: 'imported', source: f.rel, sha256: createHash('sha256').update(buf).digest('hex'), bytes: buf.length,
    format: ext.slice(1), width: info?.width ?? null, height: info?.height ?? null, alpha: info?.alpha ?? null,
    unityPath: imported ? `Assets/Art/Imported/${id.replace(/\./g, '/')}${ext === '.svg' ? '.svg' : '.png'}` : null,
  };
}
// Entries added by the art-release pass (--release) survive a rescan of the old source.
const existingPath = join(CONTENT, 'assets', 'registry.json');
if (existsSync(existingPath)) for (const [id, e] of Object.entries(JSON.parse(readFileSync(existingPath, 'utf8')))) if (e.release && !registry[id]) registry[id] = e;
if (duplicates.length) { console.error(`scan: duplicate asset IDs\n  ${duplicates.join('\n  ')}`); process.exit(1); }

mkdirSync(join(CONTENT, 'assets'), { recursive: true });
writeFileSync(join(CONTENT, 'assets', 'registry.json'), canonical(registry));

// ---------- inventory + gaps ----------
const byDomain = {};
for (const [id, e] of Object.entries(registry)) {
  const d = id.split('.')[0];
  (byDomain[d] ??= { count: 0, bytes: 0, imported: 0 });
  byDomain[d].count++; byDomain[d].bytes += e.bytes; if (e.unityPath) byDomain[d].imported++;
}
const total = Object.values(byDomain).reduce((a, d) => ({ count: a.count + d.count, bytes: a.bytes + d.bytes, imported: a.imported + d.imported }), { count: 0, bytes: 0, imported: 0 });
writeFileSync(join(ROOT, 'docs', 'ASSET-INVENTORY.md'), inventoryText(registry, [`Generated by \`node Tools/scan-assets.mjs\` from \`${SOURCE}\`. Every shipped image resolves through \`Content/assets/registry.json\` (US-0.1).`], INVENTORY_DERIVATIONS));
writeFileSync(join(ROOT, 'docs', 'ASSET-GAPS.md'), gapsText(orphans));

// ---------- provenance (US-0.2) ----------
const provFiles = [...CONFIG.provenance.files.map(f => join(SOURCE, f)).filter(existsSync),
  ...walk(join(SOURCE, 'assets')).concat(walk(join(SOURCE, 'art'))).filter(p => /[\\/](provenance\.json|prompts[^\\/]*\.json)$/.test(p))];
const provManifest = [];
for (const p of provFiles.sort()) {
  const dst = join(ROOT, 'Provenance', 'imported', rel(p));
  mkdirSync(dirname(dst), { recursive: true });
  copyFileSync(p, dst);
  provManifest.push({ source: rel(p), sha256: createHash('sha256').update(readFileSync(p)).digest('hex') });
}
writeFileSync(join(ROOT, 'Provenance', 'imported', 'manifest.json'), canonical({ source: SOURCE, files: provManifest }));
mkdirSync(join(ROOT, 'Provenance', 'generated'), { recursive: true });
const genManifest = join(ROOT, 'Provenance', 'generated', 'manifest.json');
if (!existsSync(genManifest)) writeFileSync(genManifest, canonical({ comment: 'One entry per AI-generated asset: id, modality, tool, model, prompt, references, date, sha256 (docs/design/09 §3.1).', assets: [] }));

// ---------- import (convert the configured subset) ----------
let converted = 0, skipped = 0, failed = 0;
if (IMPORT) {
  const pairs = [];
  for (const [id, e] of Object.entries(registry)) {
    if (!e.unityPath) continue;
    const dst = join(ROOT, 'Unity', e.unityPath);
    if (existsSync(dst)) { skipped++; continue; }
    mkdirSync(dirname(dst), { recursive: true });
    if (e.format === 'svg') { copyFileSync(join(SOURCE, e.source), dst); converted++; continue; }
    pairs.push(`${join(SOURCE, e.source).replace(/\\/g, '/')}\t${dst.replace(/\\/g, '/')}`);
  }
  if (pairs.length) {
    const list = join(ROOT, 'Logs', 'import-pairs.tsv');
    mkdirSync(dirname(list), { recursive: true });
    writeFileSync(list, pairs.join('\n'));
    const r = spawnSync(BLENDER, ['-b', '--factory-startup', '-P', join(ROOT, 'Tools', 'blender', 'webp_to_png.py'), '--', list, '--verify'], { encoding: 'utf8', maxBuffer: 1 << 28 });
    const lines = (r.stdout || '').split('\n');
    converted += lines.filter(l => l.startsWith('ok ') && /maxdiff=0\.0000/.test(l)).length;
    const bad = lines.filter(l => l.startsWith('fail ') || (l.startsWith('ok ') && !/maxdiff=0\.0000/.test(l)));
    failed = bad.length;
    if (bad.length) console.error(`import: ${bad.length} failed or lossy:\n  ${bad.slice(0, 20).join('\n  ')}`);
  }
}
console.log(`scan: ${Object.keys(registry).length} assets (${mb(total.bytes)} MB) in ${Object.keys(byDomain).length} domains · ${orphans.length} unused · provenance ${provManifest.length} files` +
  (IMPORT ? ` · import converted ${converted}, already present ${skipped}, failed ${failed}` : ''));
if (failed) process.exit(1);
