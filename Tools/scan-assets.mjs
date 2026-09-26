#!/usr/bin/env node
// Asset pipeline (US-0.1, US-0.2; PF-01). Read-only against the shipped repo.
//
//   node Tools/scan-assets.mjs            scan → registry.json, ASSET-INVENTORY.md, ASSET-GAPS.md, Provenance/
//   node Tools/scan-assets.mjs --import   also convert the configured import subset into Unity/Assets/Art/Imported
//                                         (WebP → PNG via Blender headless, lossless + verified; SVG copied for the
//                                         built-in Vector Graphics importer)
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, statSync, copyFileSync } from 'node:fs';
import { join, dirname, relative, extname } from 'node:path';
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

function walk(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir).flatMap(f => { const p = join(dir, f); return statSync(p).isDirectory() ? walk(p) : [p]; });
}
function webpInfo(buf) {
  if (buf.toString('ascii', 0, 4) !== 'RIFF' || buf.toString('ascii', 8, 12) !== 'WEBP') return null;
  const chunk = buf.toString('ascii', 12, 16);
  if (chunk === 'VP8 ') return { width: buf.readUInt16LE(26) & 0x3fff, height: buf.readUInt16LE(28) & 0x3fff, alpha: false, codec: 'lossy' };
  if (chunk === 'VP8L') {
    const b = buf.subarray(21, 25);
    return { width: 1 + (((b[1] & 0x3f) << 8) | b[0]), height: 1 + (((b[3] & 0x0f) << 10) | (b[2] << 2) | ((b[1] & 0xc0) >> 6)), alpha: ((b[3] >> 4) & 1) === 1, codec: 'lossless' };
  }
  if (chunk === 'VP8X') {
    return { width: 1 + buf.readUIntLE(24, 3), height: 1 + buf.readUIntLE(27, 3), alpha: (buf[20] & 0x10) !== 0, codec: 'extended' };
  }
  return null;
}
function svgInfo(text) {
  const num = (re) => { const m = re.exec(text); return m ? Math.round(parseFloat(m[1])) : null; };
  let width = num(/<svg[^>]*\swidth="([\d.]+)/), height = num(/<svg[^>]*\sheight="([\d.]+)/);
  const vb = /viewBox="[\d.-]+\s+[\d.-]+\s+([\d.]+)\s+([\d.]+)"/.exec(text);
  if ((!width || !height) && vb) { width = Math.round(parseFloat(vb[1])); height = Math.round(parseFloat(vb[2])); }
  return { width, height, alpha: true, codec: 'svg' };
}
function applyTemplate(template, m) {
  return template.replace(/\{lower:\$(\d+)\}/g, (_, n) => m[+n].toLowerCase()).replace(/\$(\d+)/g, (_, n) => m[+n]);
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
if (duplicates.length) { console.error(`scan: duplicate asset IDs\n  ${duplicates.join('\n  ')}`); process.exit(1); }

const canonical = (v) => {
  const sort = (x) => Array.isArray(x) ? x.map(sort) : x && typeof x === 'object' ? Object.fromEntries(Object.keys(x).sort().map(k => [k, sort(x[k])])) : x;
  return `${JSON.stringify(sort(v), null, 2)}\n`;
};
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
const mb = (b) => (b / 1048576).toFixed(1);
const inv = ['# Asset inventory', '', `Generated by \`node Tools/scan-assets.mjs\` from \`${SOURCE}\`. Every shipped image resolves through \`Content/assets/registry.json\` (US-0.1).`, '',
  '| Domain | Assets | MB (source) | Imported into Unity this phase |', '|---|---:|---:|---:|',
  ...Object.entries(byDomain).sort().map(([d, s]) => `| ${d} | ${s.count} | ${mb(s.bytes)} | ${s.imported} |`),
  `| **total** | **${total.count}** | **${mb(total.bytes)}** | **${total.imported}** |`, '',
  '## Derivations', '', '- WebP → PNG: Blender 4.0 headless (`Tools/blender/webp_to_png.py`), Standard view transform, verified pixel-identical (max diff 0).', '- SVG: copied for the built-in `com.unity.modules.vectorgraphics` importer (D-025).', ''];
writeFileSync(join(ROOT, 'docs', 'ASSET-INVENTORY.md'), inv.join('\n'));
const gaps = ['# Asset gaps', '', 'Owner ruling (2026-09-26): gaps may be filled with **AI-generated** assets (provenance recorded) or procedural/typographic fallbacks; no licensed third-party material.', '',
  '## Known gaps', '', '| Gap | Fallback now | Plan |', '|---|---|---|',
  '| No font files in the asset set | Unity bundled default font | Optional generated display face (D-005) |',
  '| No audio files | Procedural synth | Generated music for 8 contexts + stingers (D-006) |',
  '| No Reaver readiness poses | `STANCE-READY` frame | Generate |',
  '| `greatsword`/`sword-shield` suites lack `CONVERSATION` | `PORTRAIT` frame | Generate |',
  '| 51 of 63 relics have no painting | Typographic sigil | Generate in the style of the 12 paintings (D-011) |',
  '| No logo/wordmark | Typographic | Generate (IP-safe) |', '',
  '## Unused', '', `Files under the scanned roots that no ID rule matches (${orphans.length}). They are not shipped.`, '', ...orphans.map(o => `- \`${o}\``), ''];
writeFileSync(join(ROOT, 'docs', 'ASSET-GAPS.md'), gaps.join('\n'));

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
