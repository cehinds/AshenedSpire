#!/usr/bin/env node
// Export the shipped web game's content as raw JSON (docs/design/09 §4 steps 1 and 6; PF-10).
// Read-only against the old repo. Writes Tools/export/raw/*.json (gitignored; reproducible).
//
//   node Tools/export-content.mjs [--source D:/repos/AshenSpire] [--profile default|none] [--no-legacy] [--legacy-source <dir>] [--allow-sha-drift] [--allow-dirty]
//
// Two exports, from two pinned web commits (Tools/export/sources.json):
//   legacy  the 405-era "current" profile, read from `git archive <legacy.sha>` of the web repo (read-only; unpacked under
//           .cache/). It feeds the legacy tables the C# layer still reads. --legacy-source uses a ready tree instead.
//   core2   the Core 2 default profile, read from the web checkout itself (--source), which must be at core2.webSha
//           (docs/design/11 section 2.2). Skipped with --profile none.
//
// Legacy outputs:
//   raw/bundle.shipped.json    contentBundle with no overrides
//   raw/bundle.reference.json  contentBundle after the owner's reference config, applied through the
//                              shipped import path (parseAdvancedConfigFile → configuredContentBundle)
//   raw/advancedRows.json      advancedConfigRows(bundle) — the key catalogue for the config mapping
//   raw/export-gaps.json       every non-serializable value found (functions, class instances, symbols)
//   raw/summary.json           counts per table
//
// Core 2 outputs (raw/core2/):
//   bundle.shipped.json        applyGameProfileToContent(contentBundle, 'default'): the Core-projected bundle
//   profile.json               resolveGameProfile('default') (modules and content); profile.content.<key>.json one per content key
//   rules/*.json               fixCoreMechanics (verbatim), fixCoreSnapshot, mechanics (engine/mechanics/*), runtime (engine/runtime ops and
//                              formulas), reactionRules, combatExpansionRules, enemyKnowledgeRules, rangePolicy, versions, rng
//   extras.json                the core2.modules of sources.json (combatMatchups, actionCosts, ...)
//   advancedRows.settings.json the player-setting subset of advancedConfigRows
//   summary.json               web sha, profile id and hash, counts
import { writeFileSync, mkdirSync, readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const argValue = (name) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : null; };
const argSource = process.argv.indexOf('--source');
const SOURCE = argSource > 0 ? process.argv[argSource + 1] : (process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const SOURCES = JSON.parse(readFileSync(join(ROOT, 'Tools', 'export', 'sources.json'), 'utf8'));
const PROFILE = argValue('--profile') ?? SOURCES.core2.profile;
const WITH_LEGACY = !process.argv.includes('--no-legacy');
const OUT = join(ROOT, 'Tools', 'export', 'raw');
const REFERENCE_CONFIG = join(ROOT, 'docs', 'design', 'reference', 'ashen-spire-game-config.json');

if (!existsSync(join(SOURCE, 'src', 'content', 'index.js'))) {
  console.error(`export: shipped game not found at ${SOURCE} (set --source or ASHEN_SOURCE)`);
  process.exit(1);
}
const gitOut = (...args) => execFileSync('git', ['-C', SOURCE, ...args], { encoding: 'utf8', maxBuffer: 1 << 30 }).trim();
const loadFrom = (dir) => (rel) => import(pathToFileURL(join(dir, rel)).href);

// The legacy tree: --legacy-source, else `git archive <legacy.sha>` of the web repo unpacked under .cache/ (read-only on the web repo).
function legacyTree() {
  const given = argValue('--legacy-source');
  if (given) return given;
  const sha = SOURCES.legacy.sha;
  const dir = join(ROOT, '.cache', 'web-legacy', sha);
  if (existsSync(join(dir, 'src', 'content', 'index.js'))) return dir;
  mkdirSync(dir, { recursive: true });
  // Only the trees the export reads (the full archive is over 1 GB of art). Streamed through tar, never buffered.
  execFileSync('sh', ['-c', 'git -C "$0" archive "$1" src content | tar -x -C "$2"', SOURCE, sha, dir]);
  return dir;
}

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

if (WITH_LEGACY) {
  const LEGACY_SOURCE = legacyTree();
  const loadLegacy = loadFrom(LEGACY_SOURCE);
  const { contentBundle } = await loadLegacy('src/content/index.js');
  mkdirSync(OUT, { recursive: true });
  const gaps = [];
  // The shipped build materializes some defaults in code at runtime (e.g. balance.combatRatings).
  // Export the bundle as the game actually runs it: configuredContentBundle(bundle, {}).
  let shippedBundle = contentBundle;
  try { shippedBundle = (await loadLegacy('src/model/advancedConfig.js')).configuredContentBundle(contentBundle, {}); } catch { /* fall back to the raw bundle */ }
  const shipped = toPlain(shippedBundle, 'bundle', gaps);
  writeFileSync(join(OUT, 'bundle.shipped.json'), JSON.stringify(shipped, null, 1));
  writeFileSync(join(OUT, 'bundle.unconfigured.json'), JSON.stringify(toPlain(contentBundle, 'bundle', []), null, 1));

  let referenceNote = 'skipped';
  let rows = [];
  try {
    const adv = await loadLegacy('src/model/advancedConfig.js');
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
  const sources = SOURCES;
  const extras = {};
  const extraNotes = {};
  for (const rel of sources.modules) {
    const name = rel.split('/').pop().replace(/\.js$/, '');
    try {
      const mod = await loadLegacy(rel);
      extras[name] = toPlain({ ...mod }, `extras.${name}`, gaps);
      extraNotes[name] = Object.keys(mod).length;
    } catch (err) { extraNotes[name] = `FAILED: ${err.message}`; }
  }
  for (const rel of sources.json) {
    const name = rel.split('/').pop().replace(/\.json$/, '');
    try { (extras.framework ??= {})[name] = JSON.parse(readFileSync(join(LEGACY_SOURCE, rel), 'utf8')); extraNotes[`framework.${name}`] = 'json'; }
    catch (err) { extraNotes[`framework.${name}`] = `FAILED: ${err.message}`; }
  }
  writeFileSync(join(OUT, 'extras.json'), JSON.stringify(extras, null, 1));

  writeFileSync(join(OUT, 'export-gaps.json'), JSON.stringify(gaps, null, 1));
  const summary = { source: LEGACY_SOURCE, webSha: SOURCES.legacy.sha, contentVersion: shipped.version ?? shipped.contentVersion ?? null, tables: summarize(shipped), gaps: gaps.length, advancedRows: rows.length, reference: referenceNote, extras: extraNotes };
  writeFileSync(join(OUT, 'summary.json'), JSON.stringify(summary, null, 1));
  console.log(JSON.stringify(summary, null, 1));

}

// ======================= Core 2 (docs/design/11 section 2.2) =======================
if (PROFILE !== 'none') {
  const C2 = SOURCES.core2;
  const C2_OUT = join(OUT, 'core2');
  const sha = gitOut('rev-parse', 'HEAD');
  if (!sha.startsWith(C2.webSha) && !process.argv.includes('--allow-sha-drift')) {
    console.error(`export: the web checkout is at ${sha.slice(0, 9)} but Tools/export/sources.json pins core2.webSha ${C2.webSha}. Check it out, or pass --allow-sha-drift (re-pinning is its own work package).`);
    process.exit(1);
  }
  const dirty = gitOut('status', '--porcelain', '--', 'src', 'content', 'engine', 'games').length > 0;
  if (dirty && !process.argv.includes('--allow-dirty')) {
    console.error('export: the web checkout has uncommitted changes under src, content, engine or games, so the export would not match the pinned sha. Clean it, or pass --allow-dirty.');
    process.exit(1);
  }
  mkdirSync(join(C2_OUT, 'rules'), { recursive: true });
  const c2Gaps = [];
  const put = (rel, value) => writeFileSync(join(C2_OUT, rel), JSON.stringify(value, null, 1));
  const putPlain = (rel, value, label) => put(rel, toPlain(value, label, c2Gaps));
  const readJson = (rel) => JSON.parse(readFileSync(join(SOURCE, rel), 'utf8'));
  const load2 = loadFrom(SOURCE);

  const { contentBundle } = await load2('src/content/index.js');
  const gp = await load2('src/model/gameProfile.js');
  const profile = gp.resolveGameProfile(PROFILE);
  const projected = gp.applyGameProfileToContent(contentBundle, PROFILE);
  putPlain('bundle.shipped.json', projected, 'bundle');
  putPlain('profile.json', profile, 'profile');
  // A scalar content key (enemyCardsMerged) is wrapped as { value }, so every split file is an object or an array.
  for (const [key, value] of Object.entries(profile.content)) putPlain(`profile.content.${key}.json`, value !== null && typeof value === 'object' ? value : { value }, `profile.content.${key}`);

  // Rule snapshots: the functions a Core 2 run stamps into its save, evaluated on the default profile.
  const { createRegistries } = await load2('src/model/registries.js');
  const registries = gp.bindGameProfileRegistries(createRegistries(projected), profile);
  const fixCore = await load2('src/model/fixCoreMechanics.js');
  const traits = await load2('src/content/combatCardTraits.js');
  const expansion = await load2('src/model/combatExpansionRules.js');
  const knowledge = await load2('src/model/enemyKnowledgeRules.js');
  const engagement = await load2('src/model/combatEngagement.js');
  const fixCoreMechanics = readJson('content/source/fixCoreMechanics.json');
  const ctx = { combatExpansionVersion: 2, gameProfile: profile, registries };
  put('rules/fixCoreMechanics.json', fixCoreMechanics);
  putPlain('rules/fixCoreSnapshot.json', fixCore.snapshotFixCoreRules(ctx), 'rules.fixCoreSnapshot');
  putPlain('rules/reactionRules.json', traits.reactionRulesSnapshot(), 'rules.reactionRules');
  putPlain('rules/combatExpansionRules.json', expansion.createCombatExpansionRuleSnapshot(), 'rules.combatExpansionRules');
  putPlain('rules/enemyKnowledgeRules.json', knowledge.snapshotEnemyKnowledgeRules(registries), 'rules.enemyKnowledgeRules');
  putPlain('rules/rangePolicy.json', engagement.snapshotRangePolicy(fixCoreMechanics.range, {}), 'rules.rangePolicy');

  // engine/mechanics/*: one entry per mechanic, in the order of the web's own index.
  const mechIndex = readJson(`${C2.mechanicsDir}/index.json`);
  const mechanics = {};
  for (const row of mechIndex.mechanics) {
    const entry = { index: row };
    for (const [part, file] of Object.entries(C2.mechanicParts)) {
      const p = join(SOURCE, C2.mechanicsDir, row.id, file);
      if (existsSync(p)) entry[part] = JSON.parse(readFileSync(p, 'utf8'));
    }
    mechanics[row.id] = entry;
  }
  put('rules/mechanics.json', { format: mechIndex.format, mechanics });
  put('rules/runtime.json', { ops: readJson('engine/runtime/ops.json'), formulas: readJson('engine/runtime/formulas.json') });

  // RNG streams, in the web's order (rules/rng.json appends the ones Unity lacks).
  const { STREAM_NAMES } = await load2('src/engine/rng.js');
  put('rules/rng.json', { streams: [...STREAM_NAMES] });

  // Version scalars a Core 2 run is stamped with.
  const { createRunState, RUN_SCHEMA_VERSION } = await load2('src/model/state.js');
  const { META_SCHEMA_VERSION } = await load2('src/engine/save.js');
  const probe = createRunState({ seed: 1, classId: profile.content.classUnlocks.initial[0], registries, profileMeta: {} });
  put('rules/versions.json', {
    webContentVersion: projected.version,
    runSchemaVersion: RUN_SCHEMA_VERSION,
    metaSchemaVersion: META_SCHEMA_VERSION,
    combatExpansionVersion: probe.combatExpansionVersion,
    reactionRulesVersion: probe.reactionRulesVersion,
    breakMeterVersion: probe.advancedConfigSnapshot?.breakMeterVersion ?? null,
    profileFormatVersion: profile.formatVersion,
    foundationVersion: profile.foundation.version,
    profileId: profile.id,
    profileHash: profile.hash,
  });

  // Modules the bundle does not carry (Tools/export/sources.json core2.modules).
  const c2Extras = {}, c2Notes = {};
  for (const rel of C2.modules) {
    const name = rel.split('/').pop().replace(/\.js$/, '');
    try { const mod = await load2(rel); c2Extras[name] = toPlain({ ...mod }, `extras.${name}`, c2Gaps); c2Notes[name] = Object.keys(mod).length; }
    catch (err) { c2Notes[name] = `FAILED: ${err.message}`; }
  }
  put('extras.json', c2Extras);

  // The player-setting subset of the Advanced rows: every row outside gameConfig.* (those tune the legacy profile; a configured run ignores them; docs/design/11 2.2 item 1).
  try {
    const adv = await load2('src/model/advancedConfig.js');
    const all = adv.advancedConfigRows(contentBundle);
    const playerRows = all.filter(r => !String(r.key).startsWith('gameConfig.'));
    putPlain('advancedRows.settings.json', playerRows, 'advancedRows');
    c2Notes.advancedRows = `${all.length} rows, ${playerRows.length} player-setting rows kept (gameConfig.* rows target the legacy profile)`;
  } catch (err) { c2Notes.advancedRows = `FAILED: ${err.message}`; }

  put('export-gaps.json', c2Gaps);
  const c2Summary = {
    webSha: sha, webShaShort: sha.slice(0, 9), pinned: C2.webSha, profileId: profile.id, profileHash: profile.hash,
    contentVersion: projected.version, tables: summarize(projected),
    profileContent: Object.fromEntries(Object.entries(profile.content).map(([k, v]) => [k, Array.isArray(v) ? v.length : typeof v === 'object' && v ? `object(${Object.keys(v).length})` : typeof v])),
    mechanics: Object.keys(mechanics).length, streams: STREAM_NAMES.length, gaps: c2Gaps.length, extras: c2Notes,
  };
  put('summary.json', c2Summary);
  console.log(JSON.stringify(c2Summary, null, 1));
}
