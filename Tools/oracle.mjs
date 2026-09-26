#!/usr/bin/env node
// Parity oracle (US-18.4, PF-10): calls the SHIPPED JS engine to produce expected values that the C#
// EditMode/dotnet parity tests must reproduce exactly. Read-only against the old repo.
//
//   node Tools/oracle.mjs [--source D:/repos/AshenSpire]
//
// Writes Unity/Assets/Tests/Oracle/*.json (committed — CI has no access to the old repo).
import { writeFileSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const argSource = process.argv.indexOf('--source');
const SOURCE = argSource > 0 ? process.argv[argSource + 1] : (process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);
const write = (name, data) => writeFileSync(join(OUT, name), `${JSON.stringify(data, null, 1)}\n`);
mkdirSync(OUT, { recursive: true });

const rngMod = await load('src/engine/rng.js');
const { createRng, STREAM_NAMES, sweepSeed, seedToString, seedFromString } = rngMod;
const SEEDS = [0, 1, 42, 123456789, 4294967295, ...Array.from({ length: 10 }, (_, i) => sweepSeed(i))];

// ---- rng.json: raw draws (u = float * 2^32, exact), int/shuffle/chance samples, restored counters ----
const rngCases = [];
for (const seed of SEEDS) {
  const streams = {};
  for (const name of STREAM_NAMES) {
    const r = createRng(seed);
    const draws = Array.from({ length: 64 }, () => r.float(name) * 4294967296);
    const r2 = createRng(seed);
    const ints = Array.from({ length: 32 }, (_, i) => r2.int(name, -3, 3 + i));
    const r3 = createRng(seed);
    const shuffle = r3.shuffle(name, Array.from({ length: 10 }, (_, i) => i));
    const r4 = createRng(seed);
    const chances = [5, 12.5, 25, 50, 75, 99.99].map(p => ({ pct: p, hits: Array.from({ length: 16 }, () => r4.chance(name, p)) }));
    const r5 = createRng(seed, { [name]: 1000 });
    const afterRestore = Array.from({ length: 4 }, () => r5.float(name) * 4294967296);
    streams[name] = { draws, ints, shuffle, chances, afterRestore };
  }
  rngCases.push({ seed, streams });
}
write('rng.json', { source: 'engine/rng.js', streams: STREAM_NAMES, cases: rngCases });

// ---- seeds.json: display codec ----
const seedSamples = [0, 1, 34, 35, 36, 1234, 987654321, 4294967295, ...SEEDS];
write('seeds.json', {
  source: 'engine/rng.js',
  format: seedSamples.map(s => ({ seed: s >>> 0, text: seedToString(s) })),
  parse: ['ab12', ' zz9 ', 'OOPS1', 'HELLO', '0', 'Z'.repeat(10)].map(t => ({ text: t, seed: seedFromString(t) })),
});

// ---- seats.json: seeded seat order (drawSeatOrder over the shipped seats) ----
const { contentBundle } = await load('src/content/index.js');
const { drawSeatOrder } = await load('src/engine/actmap.js');
const registries = { seats: { all: () => contentBundle.seats, get: (id) => contentBundle.seats.find(s => s.id === id) } };
const seatsCases = [];
for (let i = 0; i < 50; i++) {
  const seed = sweepSeed(i);
  seatsCases.push({ seed, order: drawSeatOrder(registries, createRng(seed)), pinnedMarches: drawSeatOrder(registries, createRng(seed), { firstSeat: 'marches' }) });
}
write('seats.json', { source: 'engine/actmap.js drawSeatOrder', seats: contentBundle.seats.map(s => ({ id: s.id, baseTier: s.baseTier })), cases: seatsCases });

console.log(`oracle: wrote rng.json (${SEEDS.length} seeds × ${STREAM_NAMES.length} streams), seeds.json, seats.json (50 seeds) to Unity/Assets/Tests/Oracle`);
