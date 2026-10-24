#!/usr/bin/env node
// Core 2 registries oracle (WP-1): the SHIPPED registries of the `default` game profile as the web builds them for a
// configured run (applyGameProfileToContent -> createRegistries -> bindGameProfileRegistries), plus the resolved
// profile. WP-2's C# parity test compares the Content files and the C# registry builder against these dumps.
//
//   node Tools/oracle-core2-registries.mjs [--source /home/user/cehinds/ashenspire]
//
// Refuses to run unless the web HEAD is core2.webSha (Tools/export/sources.json). Writes Unity/Assets/Tests/Oracle/core2/registries/:
//   index.json            webSha, profile id and hash, one row per file (rows / keys, bytes)
//   tables/<name>.json    id-keyed registries as { table, count, ids, rows[] }, rows in REGISTRY ORDER (one row per line)
//   objects/<name>.json   the registries that are plain objects, key order kept as the web built them (D-040)
//   profile/core.json     resolveGameProfile('default') without `content` (id, hash, modules, foundation, contracts, schemas, lock, ...)
//   profile/content/<key>.json   one file per profile.content key (characters, cards, enemyCards, gear, starterDeck, stats, ...)
// Not dumped: `legacyProgressionSource` (the 405 progression, 939 KB, unused by Core 2), the `gameProfile` copy (= profile/),
// the `mapConfig` function.
import { writeFileSync, mkdirSync, rmSync, existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { loadWeb, ORACLE_DIR, plain } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'registries');
const w = await loadWeb();
const reg = w.registries;

// Remove only the previous JSON outputs: the Unity .meta files beside them are committed (Tools/refresh-manifest.mjs writes them).
function clean(dir) {
  if (!existsSync(dir)) return;
  for (const f of readdirSync(dir, { withFileTypes: true })) {
    if (f.isDirectory()) clean(join(dir, f.name));
    else if (f.name.endsWith('.json')) rmSync(join(dir, f.name));
  }
}
clean(OUT);
const files = [];
function emit(rel, text, meta) {
  const p = join(OUT, rel);
  mkdirSync(join(p, '..'), { recursive: true });
  writeFileSync(p, text);
  files.push({ file: rel, bytes: Buffer.byteLength(text), ...meta });
}

const SKIP = new Set(['legacyProgressionSource', 'gameProfile', 'mapConfig']);
for (const name of Object.keys(reg).sort()) {
  if (SKIP.has(name)) continue;
  const v = reg[name];
  if (typeof v === 'function') continue;
  if (v && typeof v.all === 'function') {
    const rows = plain(v.all());
    const head = JSON.stringify({ table: name, count: rows.length, ids: rows.map((r) => r.id) });
    emit(`tables/${name}.json`, `${head.slice(0, -1)},"rows":[\n${rows.map((r) => JSON.stringify(r)).join(',\n')}\n]}\n`, { rows: rows.length });
  } else {
    const value = plain(v);
    emit(`objects/${name}.json`, `${JSON.stringify(value, null, 1)}\n`, { keys: value && typeof value === 'object' ? Object.keys(value).length : undefined });
  }
}

const { content, ...core } = plain(w.profile);
emit('profile/core.json', `${JSON.stringify(core, null, 1)}\n`, { keys: Object.keys(core).length });
for (const key of Object.keys(content)) {
  const v = content[key];
  emit(`profile/content/${key}.json`, `${JSON.stringify(v, null, 1)}\n`, { keys: v && typeof v === 'object' ? Object.keys(v).length : undefined });
}

files.sort((a, b) => (a.file < b.file ? -1 : 1));
const total = files.reduce((n, f) => n + f.bytes, 0);
emit('index.json', `${JSON.stringify({ webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, contentVersion: reg.contentVersion, files, totalBytes: total }, null, 1)}\n`, {});
const biggest = files.reduce((a, b) => (b.bytes > a.bytes ? b : a));
console.log(`oracle-core2-registries: ${files.length} files, ${(total / 1024).toFixed(0)} KB (largest ${biggest.file} ${(biggest.bytes / 1024).toFixed(0)} KB) -> Unity/Assets/Tests/Oracle/core2/registries`);
