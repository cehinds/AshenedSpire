#!/usr/bin/env node
// Registries parity oracle: the SHIPPED createRegistries() output (tags stamped, attack-card damage projected,
// value bonuses materialized) for both presets. The C# RegistryBuilder must produce identical runtime tables.
//
//   node Tools/oracle-registries.mjs [--source D:/repos/AshenSpire]
//
// Writes Unity/Assets/Tests/Oracle/registries/{shipped,reference}/<table>.json (id-keyed, canonical).
import { writeFileSync, mkdirSync, readFileSync, rmSync, existsSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const argSource = process.argv.indexOf('--source');
const SOURCE = argSource > 0 ? process.argv[argSource + 1] : (process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'registries');
const RAW = join(ROOT, 'Tools', 'export', 'raw');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');

const sortKeys = (v) => Array.isArray(v) ? v.map(sortKeys) : v && typeof v === 'object' ? Object.fromEntries(Object.keys(v).sort().map(k => [k, sortKeys(v[k])])) : v;
const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x)));
const TABLES = ['cards', 'relics', 'statuses', 'stances', 'keywords', 'enemies', 'encounters', 'flasks', 'classes', 'events'];

function write(preset, registries) {
  const dir = join(OUT, preset);
  mkdirSync(dir, { recursive: true });
  for (const t of TABLES) {
    const byId = Object.fromEntries(registries[t].all().map(row => [row.id, plain(row)]));
    writeFileSync(join(dir, `${t}.json`), `${JSON.stringify(sortKeys(byId), null, 1)}\n`);
  }
  writeFileSync(join(dir, 'equipment.json'), `${JSON.stringify(sortKeys(plain(registries.equipment)), null, 1)}\n`);
  writeFileSync(join(dir, 'balance.json'), `${JSON.stringify(sortKeys(plain(registries.balance)), null, 1)}\n`);
}

// Remove only the previous JSON outputs: the Unity .meta files beside them are committed and must survive.
for (const preset of ['shipped', 'reference']) {
  const dir = join(OUT, preset);
  if (existsSync(dir)) for (const f of readdirSync(dir)) if (f.endsWith('.json')) rmSync(join(dir, f));
}
write('shipped', createRegistries(configuredContentBundle(contentBundle, {})));
const refSettings = JSON.parse(readFileSync(join(RAW, 'reference.settings.json'), 'utf8'));
write('reference', createRegistries(configuredContentBundle(contentBundle, refSettings.settings || refSettings)));
console.log(`oracle-registries: ${TABLES.length + 2} tables × 2 presets → Unity/Assets/Tests/Oracle/registries`);
