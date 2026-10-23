#!/usr/bin/env node
// Core 2 map oracle (WP-14): the SHIPPED act-boot path of the `default` profile, engine/actmap.js buildActMap, which for a
// configured `routes` module is buildGameProfileActMap (7 pre-boss depths in 3 lanes, mandatory rests, firstFloor, shopShare,
// fog, the boss pick, the profile hash). Records every map JSON-plain with the RNG counters after the call. The C# port
// (Ashen.Domain.Map.CoreRoutes) must reproduce every map and every counter.
//
//   node Tools/oracle-core2-map.mjs [--source /home/user/cehinds/ashenspire] [--seeds 40]
//
// Refuses to run unless the web HEAD is core2.webSha (Tools/oracle-core2.mjs drift guard). Writes
// Unity/Assets/Tests/Oracle/core2/map/ (sorted keys, D-165; `nodeOrder` records the insertion order the Unknown rolls walk):
//   index.json          webSha, profile id and hash, seats, tiers, seeds, counts
//   maps-<seat>-<tier>.json   every seat x tier x SEEDS seeds, empty history
//   history.json        quest-gated Unknown nodes (an event answers the choices before it)
//   variants.json       routes content / module variants (every knob of buildGameProfileActMap) and their refusals
//   seats.json          drawSeatOrder on the `seats` stream
//   refusals.json       unknown seat, tier out of range
import { writeFileSync, mkdirSync, rmSync, existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon } from './oracle-core2.mjs';

const SEEDS = Number(arg('--seeds', '40'));
const OUT = join(ORACLE_DIR, 'map');
const w = await loadWeb();
const reg = w.registries;
const { buildActMap, drawSeatOrder } = await import(pathToFileURL(join(WEB, 'src/engine/actmap.js')).href);
const { sweepSeed } = w.rngMod;

const seats = reg.seats.all().map((s) => s.id);
const routes = w.profile.content.routes;
const tiers = Array.from({ length: routes.acts }, (_, i) => i + 1);
const seeds = Array.from({ length: SEEDS }, (_, i) => sweepSeed(5000 + i));

if (existsSync(OUT)) for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
mkdirSync(OUT, { recursive: true });
const write = (name, data) => writeFileSync(join(OUT, name), `${JSON.stringify(data)}\n`);
const writeLines = (name, head, cases) => writeFileSync(join(OUT, name), `${JSON.stringify(head).slice(0, -1)},"cases":[\n${cases.map((c) => JSON.stringify(c)).join(',\n')}\n]}\n`);

// One recorded buildActMap call: the map (sorted keys + insertion order of its nodes), the counters after it, or the error it threw.
function record(registries, seat, tier, seed, history) {
  const rng = w.createRng(seed);
  try {
    const map = buildActMap(registries, rng, seat, tier, null, { history });
    return { seat, tier, seed, map: canon(map), nodeOrder: Object.keys(map.nodes), rng: canon(rng.getCounters()) };
  } catch (e) {
    return { seat, tier, seed, error: e.message, rng: canon(rng.getCounters()) };
  }
}

let total = 0;
const files = [];
seats.forEach((seat, si) => tiers.forEach((tier) => {
  const cases = seeds.map((seed) => record(reg, seat, tier, seed, []));
  const name = `maps-${si}-${tier}.json`;
  writeLines(name, { source: 'engine/actmap.js buildActMap(registries, rng, seat, tier, null, { history: [] })' }, cases);
  files.push(name);
  total += cases.length;
}));

// ---- history.json: an Unknown node's event is drawn from the pool the run's choices have earned ----
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
const gated = Object.keys(reg.eventHistoryRequirements);
const historyCases = [];
let gatedHits = 0;
HISTORIES.forEach((history, hi) => seats.forEach((seat) => tiers.forEach((tier) => {
  for (const seed of seeds.slice(0, 4)) {
    const c = record(reg, seat, tier, seed, history);
    c.history = hi;
    if (c.map) for (const n of Object.values(c.map.nodes)) if (n.resolved && gated.includes(n.resolved.eventId)) gatedHits++;
    historyCases.push(c);
  }
})));
writeLines('history.json', { source: 'buildActMap with a run choice history', histories: HISTORIES }, historyCases);
total += historyCases.length;

// ---- variants.json: every knob of the routes content and module, and each refusal ----
const baseModule = w.profile.modules.routes;
const variant = (routesPatch, modulePatch = {}) => {
  const content = { ...routes };
  for (const [k, v] of Object.entries(routesPatch)) if (v === undefined) delete content[k]; else content[k] = v;
  const module = { ...baseModule, ...modulePatch };
  const registries = { ...reg, gameProfile: { ...reg.gameProfile, content: { ...reg.gameProfile.content, routes: content }, modules: { ...reg.gameProfile.modules, routes: module } } };
  return { content, module, registries };
};
const VARIANTS = [
  ['the shipped content', {}],
  ['fog off', {}, { hideUnknownNodes: false }],
  ['fog on, nothing hidden', { hiddenTypes: [], visibleTypes: ['fight', 'event', 'treasure', 'shop', 'rest', 'elite', 'boss'] }],
  ['no shopShare', { shopShare: undefined }],
  ['shopShare 0', { shopShare: 0 }],
  ['shopShare 1', { shopShare: 1 }],
  ['shopShare 0.5', { shopShare: 0.5 }],
  ['shopShare 2', { shopShare: 2 }],
  ['shopShare -0.1', { shopShare: -0.1 }],
  ['no firstFloor', { firstFloor: undefined }],
  ['firstFloor event', { firstFloor: 'event' }],
  ['firstFloor treasure, fog off', { firstFloor: 'treasure' }, { hideUnknownNodes: false }],
  ['firstFloor shop', { firstFloor: 'shop' }],
  ['firstFloor elite refused by minimumThreatFloor', { firstFloor: 'elite' }],
  ['firstFloor elite allowed', { firstFloor: 'elite', minimumThreatFloor: 1 }],
  ['firstFloor rest refused', { firstFloor: 'rest' }],
  ['firstFloor unknown refused', { firstFloor: 'dragon' }],
  ['firstFloor on a rest depth refused', { restFloors: [1, 4, 7] }],
  ['two lanes', { branchColumns: 2 }],
  ['four lanes', { branchColumns: 4 }],
  ['five lanes', { branchColumns: 5 }],
  ['one lane refused', { branchColumns: 1 }],
  ['six lanes refused', { branchColumns: 6 }],
  ['fractional lanes refused', { branchColumns: 2.5 }],
  ['five depths, rests 2 4 5', { prebossNodes: 5, restFloors: [2, 4, 5] }],
  ['nine depths, rests 2 5 9', { prebossNodes: 9, restFloors: [2, 5, 9] }],
  ['one rest', { restsPerAct: 1, restFloors: [7] }],
  ['no rests', { restsPerAct: 0, restFloors: [], requirePrebossRest: false }],
  ['no pre-boss rest required', { restFloors: [2, 4, 6], requirePrebossRest: false }],
  ['pre-boss rest missing refused', { restFloors: [2, 4, 6] }],
  ['rest floor out of range refused', { restFloors: [3, 5, 8] }],
  ['rest floor repeated refused', { restFloors: [3, 5, 5] }],
  ['rest floor count mismatch refused', { restFloors: [3, 7] }],
  ['restFloors absent refused', { restFloors: undefined }],
  ['no depths refused', { prebossNodes: 0, restFloors: [], restsPerAct: 0 }],
  ['fewer depths than rests refused', { prebossNodes: 2 }],
  ['fractional depths refused', { prebossNodes: 7.5 }],
  ['threat floor 1', { minimumThreatFloor: 1 }],
  ['threat floor 8', { minimumThreatFloor: 8 }],
  ['threat floor 9 refused', { minimumThreatFloor: 9 }],
  ['threat floor 0 refused', { minimumThreatFloor: 0 }],
  ['custom visible types', { hiddenTypes: ['event', 'treasure'], visibleTypes: ['fight', 'shop', 'rest', 'elite', 'boss'] }],
  ['visible only', { hiddenTypes: undefined, visibleTypes: ['fight', 'event', 'rest', 'boss'] }],
  ['shop is the only plain type', { hiddenTypes: [], visibleTypes: ['shop', 'elite', 'rest', 'boss'] }],
  ['no alternatives refused', { hiddenTypes: [], visibleTypes: ['rest', 'boss'] }],
  ['unknown type refused', { hiddenTypes: ['dragon'] }],
  ['elite only, no permitted type at depth 1', { hiddenTypes: [], visibleTypes: ['elite', 'rest', 'boss'], firstFloor: undefined }],
  ['two acts, tier 3 out of range', { acts: 2 }],
  ['zero acts', { acts: 0 }],
];
const variantCases = [];
let fogHits = 0;
let refused = 0;
VARIANTS.forEach(([label, patch, modulePatch], vi) => {
  const v = variant(patch, modulePatch);
  const specs = [[seats[0], 1], [seats[1], 2], [seats[2], 3]];
  for (const [seat, tier] of specs) {
    for (const seed of seeds.slice(0, 5)) {
      const c = record(v.registries, seat, tier, seed, []);
      c.variant = vi;
      if (c.error) refused++;
      if (c.map) for (const n of Object.values(c.map.nodes)) if (n.type === 'event' && n.actualType !== 'event') fogHits++;
      variantCases.push(c);
    }
  }
});
writeLines('variants.json', { source: 'buildActMap over routes content / module variants', variants: VARIANTS.map(([label, patch, modulePatch], vi) => ({ index: vi, label, routes: variant(patch, modulePatch).content, module: variant(patch, modulePatch).module })) }, variantCases);
total += variantCases.length;

// ---- seats.json: drawSeatOrder (one shuffle on `seats`; a pinned first seat rotates the draw) ----
const seatOrders = seeds.slice(0, 20).map((seed) => {
  const rng = w.createRng(seed);
  const order = drawSeatOrder(reg, rng);
  return { seed, order, rng: canon(rng.getCounters()), pinned: seats.map((firstSeat) => ({ firstSeat, order: drawSeatOrder(reg, w.createRng(seed), { firstSeat }) })) };
});
write('seats.json', { source: 'engine/actmap.js drawSeatOrder', cases: seatOrders });

// ---- refusals.json: buildActMap refuses before any draw ----
const refusals = [record(reg, 'nowhere', 1, seeds[0], []), record(reg, seats[0], 0, seeds[0], []), record(reg, seats[0], 4, seeds[0], []), record(reg, seats[0], -1, seeds[0], [])];
write('refusals.json', { source: 'engine/actmap.js buildActMap', cases: refusals });

write('index.json', { source: 'engine/actmap.js buildGameProfileActMap (default profile)', webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, seeds, seats, tiers, files, maps: total, variants: VARIANTS.length, refusedVariantCases: refused, gatedHits, fogHits });
console.log(`oracle-core2-map: ${total} maps (${files.length} seat x tier files x ${SEEDS} seeds, ${historyCases.length} with history [${gatedHits} gated events rolled], ${VARIANTS.length} variants [${variantCases.length} cases, ${refused} refused, ${fogHits} fogged]) -> Unity/Assets/Tests/Oracle/core2/map`);
