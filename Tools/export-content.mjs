#!/usr/bin/env node
// Export the shipped web game's content as raw JSON (docs/design/09 §4 steps 1 and 6; PF-10).
// Read-only against the old repo. Writes Tools/export/raw/*.json (gitignored; reproducible).
//
//   node Tools/export-content.mjs [--source D:/repos/AshenSpire]
//
// Outputs:
//   raw/bundle.shipped.json    contentBundle with no overrides
//   raw/bundle.reference.json  contentBundle after the owner's reference config, applied through the
//                              shipped import path (parseAdvancedConfigFile → configuredContentBundle)
//   raw/advancedRows.json      advancedConfigRows(bundle) — the key catalogue for the config mapping
//   raw/export-gaps.json       every non-serializable value found (functions, class instances, symbols)
//   raw/summary.json           counts per table
import { writeFileSync, mkdirSync, readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const argSource = process.argv.indexOf('--source');
const SOURCE = argSource > 0 ? process.argv[argSource + 1] : (process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const OUT = join(ROOT, 'Tools', 'export', 'raw');
const REFERENCE_CONFIG = join(ROOT, 'docs', 'design', 'reference', 'ashen-spire-game-config.json');

if (!existsSync(join(SOURCE, 'src', 'content', 'index.js'))) {
  console.error(`export: shipped game not found at ${SOURCE} (set --source or ASHEN_SOURCE)`);
  process.exit(1);
}
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

// Serialize while recording anything JSON would silently drop or mangle.
function toPlain(value, path, gaps, seen = new WeakSet()) {
  if (value === null || typeof value === 'number' || typeof value === 'string' || typeof value === 'boolean') {
    if (typeof value === 'number' && !Number.isFinite(value)) { gaps.push({ path, kind: 'nonFiniteNumber', value: String(value) }); return null; }
    return value;
  }
  if (value === undefined) return undefined;
  if (typeof value === 'function') { gaps.push({ path, kind: 'function', name: value.name || '(anonymous)' }); return undefined; }
  if (typeof value === 'symbol' || typeof value === 'bigint') { gaps.push({ path, kind: typeof value }); return undefined; }
  if (seen.has(value)) { gaps.push({ path, kind: 'cycle' }); return undefined; }
  seen.add(value);
  let out;
  if (Array.isArray(value)) out = value.map((v, i) => toPlain(v, `${path}[${i}]`, gaps, seen));
  else if (value instanceof Map) { gaps.push({ path, kind: 'Map' }); out = Object.fromEntries([...value].map(([k, v]) => [k, toPlain(v, `${path}.${k}`, gaps, seen)])); }
  else if (value instanceof Set) { gaps.push({ path, kind: 'Set' }); out = [...value].map((v, i) => toPlain(v, `${path}[${i}]`, gaps, seen)); }
  else {
    const proto = Object.getPrototypeOf(value);
    if (proto !== Object.prototype && proto !== null) gaps.push({ path, kind: 'classInstance', ctor: proto?.constructor?.name });
    out = {};
    for (const key of Object.keys(value)) {
      const v = toPlain(value[key], `${path}.${key}`, gaps, seen);
      if (v !== undefined) out[key] = v;
    }
  }
  seen.delete(value);
  return out;
}

function summarize(bundle) {
  const s = {};
  for (const [k, v] of Object.entries(bundle)) {
    if (Array.isArray(v)) s[k] = v.length;
    else if (v && typeof v === 'object') s[k] = `object(${Object.keys(v).length})`;
    else s[k] = typeof v;
  }
  return s;
}

const { contentBundle } = await load('src/content/index.js');
mkdirSync(OUT, { recursive: true });
const gaps = [];
// The shipped build materializes some defaults in code at runtime (e.g. balance.combatRatings).
// Export the bundle as the game actually runs it: configuredContentBundle(bundle, {}).
let shippedBundle = contentBundle;
try { shippedBundle = (await load('src/model/advancedConfig.js')).configuredContentBundle(contentBundle, {}); } catch { /* fall back to the raw bundle */ }
const shipped = toPlain(shippedBundle, 'bundle', gaps);
writeFileSync(join(OUT, 'bundle.shipped.json'), JSON.stringify(shipped, null, 1));
writeFileSync(join(OUT, 'bundle.unconfigured.json'), JSON.stringify(toPlain(contentBundle, 'bundle', []), null, 1));

let referenceNote = 'skipped';
let rows = [];
try {
  const adv = await load('src/model/advancedConfig.js');
  rows = adv.advancedConfigRows(contentBundle).map(r => toPlain(r, 'row', []));
  writeFileSync(join(OUT, 'advancedRows.json'), JSON.stringify(rows, null, 1));
  if (existsSync(REFERENCE_CONFIG)) {
    const warnings = [];
    const text = readFileSync(REFERENCE_CONFIG, 'utf8');
    // D-029: the owner's file is the reference. The shipped importer is all-or-nothing and rejects values
    // outside the current row ranges (e.g. startingCinders 100 > max 20). Set aside ONLY the keys it
    // rejects, run the strict import on the rest (so shipped migrations of legacy keys still apply),
    // then apply the set-aside values directly and record them so the new schema ranges are widened.
    const file = JSON.parse(text);
    const keyMap = JSON.parse(readFileSync(join(ROOT, 'Tools', 'export', 'legacy-key-map.json'), 'utf8'));
    const unmapped = {};
    // 1. Renames (data-driven) and known-unmappable legacy keys.
    const renamed = {};
    for (const [rawKey, value] of Object.entries(file.overrides || {})) {
      const bare = rawKey.startsWith('settings.gameConfig.') ? rawKey.slice('settings.'.length) : rawKey;
      if (keyMap.unmappedInShippedBuild.some(u => new RegExp(u.pattern).test(bare))) { unmapped[bare] = value; continue; }
      let key = bare;
      for (const r of keyMap.renames) { const re = new RegExp(r.from); if (re.test(key)) { key = key.replace(re, r.to); break; } }
      if (key !== bare) warnings.push({ renamed: bare, to: key });
      renamed[key] = value;
    }
    file.overrides = renamed;
    // 2. Strict import; set aside only what it rejects.
    const byLabel = new Map(rows.map(r => [r.label, r]));
    const setAside = {};
    let settings = null;
    for (let attempt = 0; attempt < 200 && settings === null; attempt++) {
      try {
        const parsed = adv.parseAdvancedConfigFile(JSON.stringify(file), contentBundle, {}, [], warnings);
        settings = parsed?.settings || parsed;
      } catch (err) {
        const unknown = /Unknown setting: (\S+?)\.? Nothing was imported/.exec(err.message)?.[1];
        if (unknown) { unmapped[unknown] = file.overrides[unknown]; delete file.overrides[unknown]; warnings.push({ key: unknown, unknownKey: true }); continue; }
        const label = /Invalid value for (.+?)\. Nothing was imported/.exec(err.message)?.[1];
        const row = label && byLabel.get(label);
        if (!row || !(row.key in file.overrides)) throw err;
        setAside[row.key] = file.overrides[row.key]; delete file.overrides[row.key];
        warnings.push({ key: row.key, value: setAside[row.key], outOfRange: [row.min ?? null, row.max ?? null] });
      }
    }
    Object.assign(settings, setAside);
    writeFileSync(join(OUT, 'reference.unmapped.json'), JSON.stringify(unmapped, null, 1));
    writeFileSync(join(OUT, 'reference.outOfRange.json'), JSON.stringify(setAside, null, 1));
    const configured = adv.configuredContentBundle(contentBundle, settings);
    const refGaps = [];
    writeFileSync(join(OUT, 'bundle.reference.json'), JSON.stringify(toPlain(configured, 'bundle', refGaps), null, 1));
    writeFileSync(join(OUT, 'reference.settings.json'), JSON.stringify(toPlain(settings, 'settings', []), null, 1));
    writeFileSync(join(OUT, 'reference.warnings.json'), JSON.stringify(warnings.map(w => toPlain(w, 'w', [])), null, 1));
    referenceNote = `applied (${warnings.length} import warnings)`;
  }
} catch (err) {
  referenceNote = `FAILED: ${err.message}`;
}

// Extra modules and framework JSON not carried by contentBundle (Tools/export/sources.json).
const sources = JSON.parse(readFileSync(join(ROOT, 'Tools', 'export', 'sources.json'), 'utf8'));
const extras = {};
const extraNotes = {};
for (const rel of sources.modules) {
  const name = rel.split('/').pop().replace(/\.js$/, '');
  try {
    const mod = await load(rel);
    extras[name] = toPlain({ ...mod }, `extras.${name}`, gaps);
    extraNotes[name] = Object.keys(mod).length;
  } catch (err) { extraNotes[name] = `FAILED: ${err.message}`; }
}
for (const rel of sources.json) {
  const name = rel.split('/').pop().replace(/\.json$/, '');
  try { (extras.framework ??= {})[name] = JSON.parse(readFileSync(join(SOURCE, rel), 'utf8')); extraNotes[`framework.${name}`] = 'json'; }
  catch (err) { extraNotes[`framework.${name}`] = `FAILED: ${err.message}`; }
}
writeFileSync(join(OUT, 'extras.json'), JSON.stringify(extras, null, 1));

writeFileSync(join(OUT, 'export-gaps.json'), JSON.stringify(gaps, null, 1));
const summary = { source: SOURCE, contentVersion: shipped.version ?? shipped.contentVersion ?? null, tables: summarize(shipped), gaps: gaps.length, advancedRows: rows.length, reference: referenceNote, extras: extraNotes };
writeFileSync(join(OUT, 'summary.json'), JSON.stringify(summary, null, 1));
console.log(JSON.stringify(summary, null, 1));
