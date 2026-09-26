#!/usr/bin/env node
// Map parity oracle (US-4.1 to 4.4; D-033 part 2). Drives the SHIPPED act-boot path exactly as main.js does —
// engine/actmap.js buildActMap (generateActMap on the 'map' stream, one 'events' roll per Unknown node, the seat's
// boss destinations) — and records every map JSON-plain with the RNG counters after the call. The C# port
// (Ashen.Domain.Map) must reproduce every map and every counter.
//
//   node Tools/oracle-map.mjs [--source D:/repos/AshenSpire] [--seeds 20]
//
// Writes Unity/Assets/Tests/Oracle/map/*.json (committed; CI has no access to the old repo). Only .json outputs are
// deleted on regeneration, so the committed Unity .meta files survive.
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const SOURCE = arg('--source', process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const SEEDS = Number(arg('--seeds', '20'));
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'map');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng, sweepSeed } = await load('src/engine/rng.js');
const { buildActMap, bossEncounterForNode, drawSeatOrder } = await load('src/engine/actmap.js');
const { sampleActShape, generateActMap } = await load('src/engine/mapgen.js');
const { rollEncounter, resolveUnknownNode } = await load('src/engine/encounters.js');
const { applyRunShape, resolveFloorPlan, minViableFloors } = await load('src/model/floorplan.js');
const { MAP_SHAPE_LIMITS, LEGACY_ACT_BOSSES } = await load('src/content/mapconfig.js');
const { BOSS_LOCATIONS } = await load('src/content/bossDestinations.js');

const bundle = configuredContentBundle(contentBundle, {});
const registries = createRegistries(bundle);
const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x)));
const seats = registries.seats.all().map((s) => s.id);
const tiers = Object.keys(registries.mapConfigs).map(Number);
const seeds = Array.from({ length: SEEDS }, (_, i) => sweepSeed(3000 + i));
const errorKeys = (errors) => errors.map((e) => e.key);

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const write = (name, data) => writeFileSync(join(OUT, name), `${JSON.stringify(data)}\n`);

// One recorded buildActMap call: the map JSON-plain, the counters after it, or the error it threw.
function record(seat, tier, seed, shape, history) {
  const rng = createRng(seed);
  try {
    const map = buildActMap(registries, rng, seat, tier, shape, { history });
    return { seat, tier, seed, map: plain(map), rng: rng.getCounters() };
  } catch (e) {
    return { seat, tier, seed, error: e.message, rng: rng.getCounters() };
  }
}

// ---- maps-*.json: every seat × every tier × SEEDS seeds, no shape, empty history ----
let total = 0;
const files = [];
seats.forEach((seat, si) => tiers.forEach((tier) => {
  const cases = seeds.map((seed) => record(seat, tier, seed, null, []));
  const name = `maps-${si}-${tier}.json`;
  write(name, { source: 'engine/actmap.js buildActMap(registries, rng, seat, tier, null, { history: [] })', cases });
  files.push(name);
  total += cases.length;
}));

// ---- history.json: quest-gated Unknown nodes (E12). Each history earns, completes or fails closed. ----
const row = (eventId, choiceId, extra = {}) => ({ kind: 'eventChoice', eventId, choiceId, actNumber: 1, floor: 4, mapNodeId: 'n4_2', ...extra });
const HISTORIES = [
  [row('lastLantern', 'buyOil')],
  [row('graveOfTheNameless', 'payRespects')],
  [row('graveOfTheNameless', 'digForCinders'), row('namelessKeeper', 'returnCinders', { actNumber: 2, floor: 7, mapNodeId: null })],
  [{ kind: 'questCompleted', questId: 'survey:crownfall', source: 'atlas' }, row('lastLantern', 'haulBeacon'), row('graveOfTheNameless', 'digForCinders')],
  [row('lastLantern', 'buyOil', { actNumber: 0 })],
  [row('lastLantern', 'buyOil', { mapNodeId: '9bad' })],
  [row('lastLantern', 'nope'), row('graveOfTheNameless', 'payRespects', { floor: -1 })],
];
const gated = Object.keys(registries.eventHistoryRequirements);
const historyCases = [];
let gatedHits = 0;
HISTORIES.forEach((history, hi) => seats.forEach((seat) => tiers.forEach((tier) => {
  for (const seed of seeds.slice(0, 4)) {
    const c = record(seat, tier, seed, null, history);
    c.history = hi;
    if (c.map) for (const n of Object.values(c.map.nodes)) if (n.resolved && gated.includes(n.resolved.eventId)) gatedHits++;
    historyCases.push(c);
  }
})));
write('history.json', { source: 'buildActMap with a run choice history', histories: HISTORIES, cases: historyCases });
total += historyCases.length;

// ---- shapes.json: applyRunShape (Custom Climb) — accepted shapes build maps; refused shapes name the knob ----
const SHAPES = [
  { floors: 8 },
  { floors: 7, columns: 4 },
  { columns: 2, typeWeights: { elite: 30, merchant: 0 } },
  { typeWeights: { monster: 0 } },
  { floors: 7, typeWeights: { event: 100, monster: 0, shrine: 0, elite: 0, merchant: 0 } },
  { floors: 7, columns: 2, typeWeights: { monster: 0, event: 0, shrine: 50, elite: 0, merchant: 50 } },
  { floors: 9, columns: 3, typeWeights: { monster: 0, event: 0, shrine: 0, elite: 100, merchant: 0 } },
  { floors: 7, columns: 2, typeWeights: { monster: 0, event: 0, shrine: 0, elite: 0, merchant: 100 } },
  { floors: 8, columns: 3, typeWeights: { monster: 0, event: 40, shrine: 0, elite: 0, merchant: 60 } },
  { columns: 3 },
  { floors: 20, columns: 9 },
  { floors: 12, columns: 7 },
  { typeWeights: {} },
  { floors: 10, columns: 5, typeWeights: { monster: 5, event: 5, shrine: 90, elite: 60, merchant: 1 } },
  { floors: 3 },
  { columns: 1 },
  { floors: 9.5 },
  { typeWeights: { dragon: 5 } },
  { typeWeights: { elite: 101 } },
  { typeWeights: { elite: -1, merchant: 'x' } },
  { typeWeights: { monster: 0, event: 0, shrine: 0, elite: 0, merchant: 0 } },
  { typeWeights: [1, 2] },
  { bogus: 1, floors: 8 },
  [8],
  'tall',
];
const shapeCases = SHAPES.map((shape, i) => {
  const perTier = tiers.map((tier) => {
    const r = applyRunShape(registries.mapConfig(tier), shape, MAP_SHAPE_LIMITS);
    return { tier, config: plain(r.config), changed: r.changed, errors: errorKeys(r.errors), notes: r.notes };
  });
  const maps = [];
  for (const seat of seats) for (const tier of tiers) for (const seed of seeds.slice(0, 3)) maps.push(record(seat, tier, seed, shape, []));
  total += maps.length;
  return { index: i, shape, perTier, maps };
});
write('shapes.json', { source: 'model/floorplan.js applyRunShape + buildActMap(…, mapShape)', limits: MAP_SHAPE_LIMITS, cases: shapeCases });

// ---- plans.json: resolveFloorPlan over hand-built configs (every refusal names its key) ----
const base = registries.mapConfig(1);
const withRules = (floorRules, extra = {}) => ({ ...base, ...extra, floorRules });
const R = base.floorRules;
const PLANS = [
  base,
  { ...base, floors: 6 },
  { ...base, floors: 7 },
  { ...base, floors: 15 },
  { ...base, floors: 2 },
  { ...base, floors: 1 },
  { ...base, floorRules: undefined },
  { ...base, floorRules: [1] },
  { ...base, unknownWeights: undefined },
  { ...base, unknownWeights: { event: 0, fight: 0 } },
  { ...base, unknownWeights: [3] },
  withRules({ ...R, fixed: 'x' }),
  withRules({ ...R, fixed: [null, { at: 'first', type: 'dragon' }, { at: 'floor', index: 4, type: 'elite' }, { at: 'floor', index: 99, type: 'event' }] }),
  withRules({ ...R, fixed: [{ at: 'first', type: 'monster' }, { at: 'first', type: 'event' }, { at: 'last', type: 'merchant' }, { at: 'fraction', of: 0, type: 'event' }, { at: 'floor', index: 2.5, type: 'event' }, { at: 'sideways', type: 'event' }, { type: 'event' }] }),
  withRules({ ...R, fixed: [{ at: 'floor', index: 2, type: 'shrine' }, { at: 'floor', index: 5, type: 'elite' }] }),
  withRules({ ...R, fixed: [{ at: 'floor', index: 3, type: 'elite' }] }),
  withRules({ ...R, noEliteOrShrineBefore: 6 }),
  withRules({ ...R, noShrineBefore: { at: 'floor', index: 12 }, noEliteBefore: [2] }),
  withRules({ ...R, noShrineBefore: { at: 'last' }, noEliteBefore: { at: 'last' } }),
  withRules({ ...R, restBeforeElite: 'yes' }),
  withRules({ ...R, restBeforeElite: false, noShrineOn: undefined }),
  withRules({ ...R, minElites: 1.5, minMerchants: -1 }),
  withRules({ ...R, minElites: undefined, minReachableElites: 3, minMerchants: undefined, minReachableMerchants: 2 }),
  withRules({ fixed: [], restBeforeElite: true }),
  withRules({}),
  withRules({ ...R, noShrineBefore: { at: 'fraction', of: 0.6 }, noEliteBefore: { at: 'fraction', of: 0.61 } }),
];
const planCases = PLANS.map((config) => {
  const r = resolveFloorPlan(config);
  return { config: plain(config), plan: plain(r.plan), errors: errorKeys(r.errors) };
});
write('plans.json', { source: 'model/floorplan.js resolveFloorPlan + minViableFloors', minViableFloors: minViableFloors(base), cases: planCases });

// ---- configs.json: generateActMap directly over authored configs a run shape cannot reach (pathCount, entries,
// fixed ranks, minima). These are the acts that drive the relax path, the rest-before-Elite victim choice (a
// Monster, else a spare type, else anything) and the donor restore, which the shipped act never needs.
const WEIGHTS = [
  base.typeWeights,
  { monster: 0, event: 50, shrine: 0, elite: 0, merchant: 50 },
  { monster: 0, event: 0, shrine: 50, elite: 0, merchant: 50 },
  { monster: 0, event: 0, shrine: 0, elite: 0, merchant: 100 },
  { monster: 10, event: 0, shrine: 30, elite: 60, merchant: 0 },
  { monster: 0, event: 20, shrine: 40, elite: 0, merchant: 40 },
];
const FIXED = [
  [{ at: 'first', type: 'monster' }],
  [{ at: 'first', type: 'merchant' }],
  [{ at: 'first', type: 'monster' }, { at: 'floor', index: 4, type: 'elite' }],
  [{ at: 'first', type: 'merchant' }, { at: 'fraction', of: 0.64, type: 'treasure' }],
  [{ at: 'floor', index: 5, type: 'elite' }],
  [],
];
const actConfig = ({ floors, columns, pathCount, entries, weights, fixed, minElites, minMerchants, restBeforeElite = true, shrineFrom = 0.27 }) => ({
  floors, columns, pathCount, ...(entries === undefined ? {} : { entries }), typeWeights: WEIGHTS[weights], unknownWeights: base.unknownWeights,
  floorRules: { fixed: FIXED[fixed], noShrineBefore: { at: 'fraction', of: shrineFrom }, noEliteBefore: { at: 'fraction', of: 0.43 }, noShrineOn: { at: 'last' }, restBeforeElite, minElites, minMerchants },
});
const configCases = [];
const addConfig = (spec, caseSeeds) => {
  const config = actConfig(spec);
  if (resolveFloorPlan(config).errors.length) return;
  const maps = caseSeeds.map((seed) => {
    const rng = createRng(seed);
    return { seed, map: plain(generateActMap({ config, rng })), rng: rng.getCounters() };
  });
  configCases.push({ spec, config, maps });
};
// A deterministic slice of the grid (entries unset / 1 / 2, one or several walkers, every weight and fixed set)…
let combo = 0;
for (const floors of [7, 8, 10]) for (const pathCount of [1, 2, 4]) for (const entries of [undefined, 1, 2]) for (const weights of WEIGHTS.keys()) for (const fixed of FIXED.keys()) for (const [minElites, minMerchants] of [[2, 1], [1, 2], [0, 3], [2, 4]]) for (const restBeforeElite of [true, false]) {
  if (combo++ % 23 !== 0) continue;
  addConfig({ floors, columns: pathCount === 1 ? 2 : 5, pathCount, entries, weights, fixed, minElites, minMerchants, restBeforeElite }, seeds.slice(0, 3));
}
// …plus the acts that reach the rare exits, found by instrumenting a scratch copy of mapgen.js: the victim is a
// spare type (no Monster below the first Elite), the victim is any node (nothing spare), a donor restores a
// promised type, and a surplus Shrine donates.
const probe = Array.from({ length: 8 }, (_, i) => sweepSeed(3000 + i));
addConfig({ floors: 7, columns: 2, pathCount: 2, entries: 1, weights: 0, fixed: 0, minElites: 2, minMerchants: 3 }, probe);
addConfig({ floors: 7, columns: 3, pathCount: 2, entries: 1, weights: 5, fixed: 0, minElites: 2, minMerchants: 3 }, probe);
addConfig({ floors: 7, columns: 3, pathCount: 2, entries: 1, weights: 0, fixed: 0, minElites: 1, minMerchants: 2 }, probe);
addConfig({ floors: 7, columns: 2, pathCount: 1, entries: 1, weights: 1, fixed: 4, minElites: 1, minMerchants: 2 }, probe);
addConfig({ floors: 7, columns: 2, pathCount: 1, entries: 1, weights: 1, fixed: 4, minElites: 0, minMerchants: 2 }, probe);
addConfig({ floors: 7, columns: 2, pathCount: 1, entries: 1, weights: 5, fixed: 2, minElites: 0, minMerchants: 3 }, [17, 54, 3, 9].map((i) => sweepSeed(i)));
addConfig({ floors: 7, columns: 2, pathCount: 1, entries: 1, weights: 5, fixed: 2, minElites: 1, minMerchants: 3, shrineFrom: 0.15 }, [17, 54, 1, 2].map((i) => sweepSeed(i)));
addConfig({ floors: 8, columns: 2, pathCount: 1, entries: 1, weights: 2, fixed: 1, minElites: 1, minMerchants: 4 }, Array.from({ length: 8 }, (_, i) => sweepSeed(60 + i)));
write('configs.json', { source: 'engine/mapgen.js generateActMap({ config, rng }) over authored configs', cases: configCases });
total += configCases.reduce((a, c) => a + c.maps.length, 0);

// ---- extras.json: sampleActShape, rollEncounter, resolveUnknownNode, drawSeatOrder, bossEncounterForNode ----
const samples = [
  { config: 1, seeds: 24, result: sampleActShape(registries.mapConfig(1), 24) },
  { config: 'shape', seeds: 12, shape: { floors: 8, columns: 4 }, result: sampleActShape(applyRunShape(registries.mapConfig(1), { floors: 8, columns: 4 }, MAP_SHAPE_LIMITS).config, 12) },
];
const encounterRolls = [];
for (const seat of seats) for (const pool of ['normal', 'elite', 'boss', 'none']) {
  const ids = registries.encounters.all().filter((e) => e.pool === pool && e.seat === seat).map((e) => e.id);
  for (const [si, seed] of seeds.slice(0, 8).entries()) {
    const exclude = si % 3 === 0 ? [] : si % 3 === 1 ? ids.slice(0, 1) : ids.slice(0, 2);
    const rng = createRng(seed);
    let id = null; let error = null;
    try { id = rollEncounter(registries, rng, { pool, seat, exclude }); } catch (e) { error = e.message; }
    encounterRolls.push({ seat, pool, seed, exclude, id, error, rng: rng.getCounters() });
  }
  const rng = createRng(seeds[0]);
  let id = null; let error = null;
  try { id = rollEncounter(registries, rng, { pool, seat, exclude: ids }); } catch (e) { error = e.message; }
  encounterRolls.push({ seat, pool, seed: seeds[0], exclude: ids, id, error, rng: rng.getCounters() });
}
const unknownRolls = [];
for (const tier of tiers) for (const seed of seeds.slice(0, 10)) {
  const rng = createRng(seed);
  const seenEvents = registries.events.ids().slice(0, (seed % 30));
  const out = [];
  for (let k = 0; k < 6; k++) out.push(resolveUnknownNode(registries, rng, { seenEvents, tier, history: HISTORIES[k % 3] }));
  unknownRolls.push({ tier, seed, seenEvents, histories: [0, 1, 2], results: out, rng: rng.getCounters() });
}
const seatOrders = seeds.slice(0, 10).map((seed) => ({
  seed,
  order: drawSeatOrder(registries, createRng(seed)),
  pinned: seats.map((firstSeat) => drawSeatOrder(registries, createRng(seed), { firstSeat })),
}));
// A legacy singular graph (pre-§13 save): its lone boss node has no encounterId, so the tier names the boss.
const legacy = { nodes: { n13_3: { id: 'n13_3', floor: 13, col: 3, type: 'boss', next: [] } }, bossId: 'n13_3' };
const legacyBosses = [];
for (const seat of seats) for (const tier of tiers) {
  let id = null; let error = null;
  try { id = bossEncounterForNode(registries, legacy, 'n13_3', { seat, tier }); } catch (e) { error = e.message; }
  legacyBosses.push({ seat, tier, id, error });
}
// Unknown seat, bad tier: buildActMap refuses before any draw.
const refusals = [record('nowhere', 1, seeds[0], null, []), record(seats[0], 0, seeds[0], null, []), record(seats[0], 4, seeds[0], null, [])];
write('extras.json', { samples, encounterRolls, unknownRolls, seatOrders, legacy, legacyBosses, refusals });

// ---- tables.json: what the map port reads, in the SHIPPED insertion order (D-040) ----
const pick = (rowOf) => (r) => rowOf(plain(r));
write('tables.json', {
  encounters: registries.encounters.all().map(pick((r) => r)),
  events: registries.events.all().map(pick((r) => ({ id: r.id }))),
  enemies: registries.enemies.all().map(pick((r) => ({ id: r.id, name: r.name }))),
  seats: registries.seats.all().map(pick((r) => r)),
  mapConfigs: plain(registries.mapConfigs),
  eventHistoryRequirements: plain(registries.eventHistoryRequirements),
  endless: plain(registries.balance.endless),
  bossLocations: plain(BOSS_LOCATIONS),
  mapShapeLimits: plain(MAP_SHAPE_LIMITS),
  legacyActBosses: plain(LEGACY_ACT_BOSSES),
});
write('index.json', { source: 'engine/actmap.js (shipped preset)', seeds, seats, tiers, files, maps: total, configs: configCases.length, gatedHits });
console.log(`oracle-map: ${total} maps (${configCases.length} direct generateActMap configs, ${files.length} seat×tier files × ${SEEDS} seeds, ${historyCases.length} with history [${gatedHits} gated events rolled], shapes ${SHAPES.length}) → Unity/Assets/Tests/Oracle/map`);
