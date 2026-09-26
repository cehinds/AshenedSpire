#!/usr/bin/env node
// Transform the exported shipped content into Unity StreamingAssets/Content (docs/design/08, 09 §4, PF-10).
// Input: Tools/export/raw (from export-content.mjs). Mapping: Tools/transform.config.json (data).
//
//   node Tools/transform-content.mjs            write Content + presets + strings + manifest, run the count gate
//   node Tools/transform-content.mjs --check    regenerate in memory and fail if the committed Content differs
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, rmSync, statSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const RAW = join(ROOT, 'Tools', 'export', 'raw');
const CONTENT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content');
const CONFIG = JSON.parse(readFileSync(join(ROOT, 'Tools', 'transform.config.json'), 'utf8'));
const CHECK = process.argv.includes('--check');
const GENERATED_DIRS = ['catalog', 'balance', 'rules', 'tags', 'strings', 'settings/presets', 'audio', 'ui'];
const HAND_WRITTEN = new Set(['ui/screens.json', 'ui/menus.json', 'ui/layout.json', 'ui/tokens.json', 'ui/components.json', 'audio/contexts.json', 'settings/defaults.json', 'about.json', 'rules/effectOps.json', 'rules/validation.json', 'rules/rng.json', 'rules/saves.json', 'assets/registry.json', 'strings/app.en.json', 'audio/synth.json', 'rules/combatEngine.json', 'strings/combat.en.json', 'rules/runEngine.json', 'rules/mapEngine.json', 'rules/rewardsEngine.json', 'rules/runFlow.json', 'rules/loopEngine.json']);

const fail = (m) => { console.error(`transform: ${m}`); process.exit(1); };
const readRaw = (name) => {
  const p = join(RAW, name);
  if (!existsSync(p)) fail(`missing ${relative(ROOT, p)} — run node Tools/export-content.mjs first`);
  return JSON.parse(readFileSync(p, 'utf8'));
};

// ---------- canonical JSON: SOURCE key order, 2-space indent, LF, trailing newline ----------
// Key order is kept exactly as the shipped bundle authored it (D-040): object order is semantic in the rules
// (weighted enemy move picks walk `moves` in order; reward pools and RNG picks walk registry order), so sorting
// would silently change seeded outcomes. The export is deterministic, so the rendering still is.
function sortKeys(v) {
  if (Array.isArray(v)) return v.map(sortKeys);
  if (v && typeof v === 'object') {
    const out = {};
    for (const k of Object.keys(v).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))) out[k] = sortKeys(v[k]);
    return out;
  }
  return v;
}
export const canonical = (v) => `${JSON.stringify(v, null, 2)}\n`;
// Sorted form, for files whose key order carries no meaning and reads better sorted (string tables, manifest).
export const sortedCanonical = (v) => `${JSON.stringify(sortKeys(v), null, 2)}\n`;
const sha256 = (s) => createHash('sha256').update(s, 'utf8').digest('hex');

function at(roots, path) {
  const [head, ...rest] = path.split('.');
  let cur = roots[head];
  for (const seg of rest) { if (cur == null) return undefined; cur = cur[seg]; }
  return cur;
}
function deepClone(v) { return v === undefined ? undefined : JSON.parse(JSON.stringify(v)); }

// ---------- build one content set from a bundle ----------
function build(bundle, extras) {
  const roots = { bundle, extras };
  const files = {};
  const strings = {};
  const counts = {};
  // Canonical JSON sorts every object's keys, so an id-keyed table loses its authoring order. The shipped engine
  // iterates its registries in that order (tag lists, equipment arrays, card order), so it is recorded here.
  const rowOrder = {};
  // Where each extracted text field sat in its row (D-069): the key it followed ('' = first). Rows are copied verbatim
  // into run documents (a creation mode snapshot, a relic, a card), so re-attached text must land where it was authored.
  const textAnchors = {};
  for (const t of CONFIG.tables) {
    const rows = at(roots, t.from);
    if (!Array.isArray(rows)) fail(`${t.from} is not an array`);
    if (t.list) { files[t.out] = { rows: deepClone(rows) }; counts[t.out] = rows.length; continue; }
    const table = {};
    const order = [];
    for (const src of rows) {
      const row = deepClone(src);
      const key = t.key.map(k => row[k]).join(':');
      if (!key || t.key.some(k => row[k] == null)) fail(`${t.out}: row without key ${t.key.join('+')}`);
      if (key in table) fail(`${t.out}: duplicate key "${key}"`);
      const keys = Object.keys(row);
      const anchors = {};
      for (const [field, name] of Object.entries(t.text || {})) {
        if (typeof row[field] === 'string') { strings[`${t.stringPrefix}.${key}.${name}`] = row[field]; delete row[field]; }
      }
      for (const [i, k] of keys.entries()) if (t.text && k in t.text && typeof src[k] === 'string') anchors[k] = i === 0 ? '' : keys[i - 1];
      if (Object.keys(anchors).length) (textAnchors[t.out] ||= {})[key] = anchors;
      table[key] = row;
      order.push(key);
    }
    files[t.out] = table;
    rowOrder[t.out] = order; // an array: integer-like keys would reorder an object's keys
    counts[t.out] = Object.keys(table).length;
  }
  for (const o of CONFIG.objects) {
    let v = deepClone(at(roots, o.from));
    if (v == null) fail(`${o.from} not found`);
    if (o.pick) v = Object.fromEntries(o.pick.map(k => [k, v[k]]));
    if (o.omit) for (const k of o.omit) delete v[k];
    files[o.out] = v;
  }
  // Required string keys per keyed catalog: a text field present on every source row is required (validator reads this).
  const stringKeys = {};
  for (const t of CONFIG.tables) {
    if (t.list || !t.text) continue;
    const rows = at(roots, t.from);
    const required = Object.entries(t.text).filter(([field]) => rows.every(r => typeof r[field] === 'string')).map(([, name]) => name);
    const optional = Object.entries(t.text).filter(([field]) => !rows.every(r => typeof r[field] === 'string')).map(([, name]) => name);
    stringKeys[t.out] = { prefix: t.stringPrefix, required, optional, fields: { ...t.text } };
  }
  files['rules/stringKeys.json'] = stringKeys;
  files['rules/rowOrder.json'] = rowOrder;
  files['rules/textAnchors.json'] = textAnchors;
  const us = at(roots, CONFIG.uiStrings.from) || [];
  for (const row of us) for (const f of CONFIG.uiStrings.fields) if (typeof row[f] === 'string') strings[`${CONFIG.uiStrings.prefix}.${row.id}.${f}`] = row[f];
  files['strings/en.json'] = strings;
  return { files, counts };
}

// ---------- RFC 7386 merge-patch diff ----------
function diff(a, b) {
  if (JSON.stringify(a) === JSON.stringify(b)) return undefined;
  const isObj = (x) => x && typeof x === 'object' && !Array.isArray(x);
  if (!isObj(a) || !isObj(b)) return b === undefined ? null : b;
  const patch = {};
  for (const k of new Set([...Object.keys(a), ...Object.keys(b)])) {
    if (!(k in b)) patch[k] = null;
    else { const d = diff(a[k], b[k]); if (d !== undefined) patch[k] = d; }
  }
  return Object.keys(patch).length ? patch : undefined;
}

// Owner-only keys the shipped build cannot express (Tools/export/legacy-key-map.json → unmappedInShippedBuild)
function referenceExtras(unmapped) {
  const out = { rulePatches: {}, playerSettings: {}, unresolved: {} };
  const hand = {};
  for (const [key, value] of Object.entries(unmapped)) {
    let m;
    if ((m = /^gameConfig\.derivedStatRules\.rules\.openingHand\.(\w+)$/.exec(key))) hand[m[1]] = value;
    else if ((m = /^settings\.(.+)$/.exec(key))) out.playerSettings[m[1]] = value;
    else out.unresolved[key] = value;
  }
  if (Object.keys(hand).length) {
    const attrs = ['strength', 'dexterity', 'constitution', 'wisdom', 'intelligence'];
    out.rulePatches['rules/handRules.json'] = {
      openingWeighted: {
        base: hand.base ?? 0,
        terms: attrs.filter(a => hand[a]).map(a => ({ attr: a, weight: hand[a] })),
        perLevel: hand.perLevel ?? 0,
        min: hand.min ?? 0, max: hand.max ?? 99,
        rounding: 'floor',
        note: 'Owner reference model (weighted multi-attribute). Replaces handRules.starting when present.',
      },
    };
  }
  return out;
}

// ---------- main ----------
const shipped = readRaw('bundle.shipped.json');
const reference = readRaw('bundle.reference.json');
const extras = readRaw('extras.json');
const unmapped = existsSync(join(RAW, 'reference.unmapped.json')) ? readRaw('reference.unmapped.json') : {};
const outOfRange = existsSync(join(RAW, 'reference.outOfRange.json')) ? readRaw('reference.outOfRange.json') : {};

const base = build(shipped, extras);
const ref = build(reference, extras);

// Count gate (docs/design/09 §4 step 3)
const countErrors = Object.entries(CONFIG.expectedCounts).filter(([f, n]) => base.counts[f] !== n).map(([f, n]) => `${f}: expected ${n}, got ${base.counts[f]}`);
if (countErrors.length) fail(`count gate failed:\n  ${countErrors.join('\n  ')}`);

// Presets
const refFiles = {};
for (const path of Object.keys(base.files)) { const d = diff(base.files[path], ref.files[path]); if (d !== undefined) refFiles[path] = d; }
const extra = referenceExtras(unmapped);
for (const [path, patch] of Object.entries(extra.rulePatches)) refFiles[path] = { ...(refFiles[path] || {}), ...patch };
const presets = {
  'settings/presets/shipped.json': { id: 'shipped', labelKey: 'preset.shipped.name', description: 'The shipped web build defaults (content 0.7.1), unmodified.', files: {} },
  'settings/presets/reference.json': {
    id: 'reference', labelKey: 'preset.reference.name', default: true,
    source: 'docs/design/reference/ashen-spire-game-config.json',
    description: "The owner's reference tuning (D-019), applied through the shipped import path (D-029).",
    files: refFiles, playerSettings: extra.playerSettings, unresolved: extra.unresolved, widenedRanges: outOfRange,
  },
};
const strings = base.files['strings/en.json'];
strings['preset.shipped.name'] = 'Shipped defaults';
strings['preset.reference.name'] = 'Reference (owner tuning)';

const all = { ...base.files, ...presets };
const manifestFiles = {};
const rendered = {};
for (const path of Object.keys(all).sort()) { rendered[path] = (path.startsWith('strings/') ? sortedCanonical : canonical)(all[path]); }
// Hand-written content files (not generated) are part of the manifest too, as are the committed schemas.
const handWritten = new Set(HAND_WRITTEN);
const schemaDir = join(CONTENT, 'schemas');
if (existsSync(schemaDir)) for (const f of readdirSync(schemaDir).filter(f => f.endsWith('.json')).sort()) handWritten.add(`schemas/${f}`);
for (const rel of handWritten) {
  const p = join(CONTENT, rel);
  if (existsSync(p)) rendered[rel] = readFileSync(p, 'utf8').replace(/\r\n/g, '\n');
}
for (const path of Object.keys(rendered).sort()) manifestFiles[path] = { sha256: sha256(rendered[path]), bytes: Buffer.byteLength(rendered[path]), generated: !handWritten.has(path) };
const contentHash = sha256(Object.keys(manifestFiles).sort().map(p => `${p}:${manifestFiles[p].sha256}`).join('\n'));
const manifest = { schemaVersion: 1, contentVersion: CONFIG.contentVersion, contentHash, counts: base.counts, files: manifestFiles };
rendered['manifest.json'] = sortedCanonical(manifest);

if (CHECK) {
  const drift = [];
  for (const [path, text] of Object.entries(rendered)) {
    const p = join(CONTENT, path);
    if (!existsSync(p) || readFileSync(p, 'utf8').replace(/\r\n/g, '\n') !== text) drift.push(path);
  }
  if (drift.length) fail(`committed Content differs from a fresh transform:\n  ${drift.join('\n  ')}`);
  console.log(`transform: check ok (${Object.keys(rendered).length} files, contentHash ${contentHash.slice(0, 12)})`);
} else {
  for (const dir of GENERATED_DIRS) {
    const d = join(CONTENT, dir);
    if (existsSync(d)) for (const f of readdirSync(d)) { const fp = join(d, f); if (statSync(fp).isFile() && f.endsWith('.json') && !HAND_WRITTEN.has(`${dir}/${f}`)) rmSync(fp); }
  }
  for (const [path, text] of Object.entries(rendered)) {
    if (handWritten.has(path)) continue;
    const p = join(CONTENT, path);
    mkdirSync(dirname(p), { recursive: true });
    writeFileSync(p, text);
  }
  console.log(`transform: wrote ${Object.keys(rendered).length} files · ${Object.keys(strings).length} strings · reference preset patches ${Object.keys(refFiles).length} files · contentHash ${contentHash.slice(0, 12)}`);
  console.log('counts:', JSON.stringify(base.counts));
}
