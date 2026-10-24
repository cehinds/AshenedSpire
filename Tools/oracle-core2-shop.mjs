#!/usr/bin/env node
// Core 2 shop oracle (WP-17, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED web for the configured `default` profile:
//   src/model/gameProfileRewards.js   gameProfileShopStock, purchaseGameProfileShopOffer (and makeOffer's shop branch: itemsPerCategory, the `shop` stream)
//   src/main.js enterNode (2836-2842) and showShop (3945-3956)   the merchant node's door for a configured run, which the oracle mirrors line for line
//   src/ui/screens/shop.js mountConfiguredShop (137-146)   what makes an offer buyable on the screen
//
//   node Tools/oracle-core2-shop.mjs [--seeds 8] [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/shop/, sorted keys; two runs are byte-identical):
//   shop-<class>-seedNN.json   a bot works a WP-3 run through several shops: enter (the stock is drawn once on the `shop` stream), show again (a remount: nothing
//                              moves), buy (every refusal the web has: cinders, taken, claimed, invalid index, potion slots full, a missing shop, a tampered price),
//                              leave; between shops it sets the purse, drops cards, switches the range policy and, in some scenarios, the shops module's settings
//                              (itemsPerCategory, price, options) so that the stock's other branch is held to the web as well
//   index.json                 the sha, the profile hash and the tallies
// A step's `changes` is { set: { key: value }, del: [key] } over the run document's top-level keys (the streamCounters aside), values canonical.
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, botLcg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'shop');
const SEEDS = Number(arg('--seeds', '8'));
const w = await loadWeb();
const reg = w.registries;
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [rewards, gameProfile, flasks] = await Promise.all([load('src/model/gameProfileRewards.js'), load('src/model/gameProfile.js'), load('src/model/gracerefill.js')]);

const classes = classIds(w);
const UNLOCKED = { gameProfileUnlocks: { [w.profile.id]: { completedRunIds: classes.map((_, i) => `r${i}`) } } };
const create = (seed, classId) => w.state.createRunState({ seed, classId, registries: reg, profileMeta: structuredClone(UNLOCKED) });
const call = (fn) => { try { return fn(); } catch (e) { return { error: e.message }; } };
const counters = (rng) => canon(rng.getCounters());

function snapshot(run) { const { streamCounters, gameProfile: pinned, ...rest } = run; return Object.fromEntries(Object.entries(rest).map(([k, v]) => [k, JSON.stringify(canon(v ?? null)) + (v === undefined ? '#u' : '')])); }
function changes(before, run) {
  const after = snapshot(run);
  const set = {}, del = [];
  for (const [k, v] of Object.entries(after)) if (before[k] !== v) set[k] = canon(run[k] ?? null);
  for (const k of Object.keys(before)) if (!(k in after)) del.push(k);
  return { set, del };
}
const runDocument = (run) => { const { gameProfile: pinned, runId, streamCounters, ...rest } = run; return canon({ ...rest, gameProfile: { id: pinned.id, hash: pinned.hash } }); };

/** main.js enterNode's merchant branch for a configured run, then showShop (3945-3956): the shop's id is marked open and its stock found or drawn. */
function enter(run, rng, nodeId) {
  if (!gameProfile.gameModule(run, 'shops')) return { map: true };
  run.gameProfileOpenShop = `${run.actNumber}:${nodeId}`;
  return show(run, rng);
}
/** showShop for a configured run (also the remount after every buy). */
function show(run, rng) {
  if (!gameProfile.gameModule(run, 'shops')) return { map: true };
  const shopId = run.gameProfileOpenShop || `${run.actNumber}:${run.mapNodeId}`;
  run.gameProfileOpenShop = shopId;
  return { shopId, stock: structuredClone(rewards.gameProfileShopStock(reg, run, rng, { shopId })) };
}
/** mountConfiguredShop's `available` for every option of a stock. */
function availability(run, stock) {
  return stock.options.map((option, index) => {
    const plan = option.kind === 'potions' ? flasks.flaskAcquisitionPlan(reg, run, option.id) : null;
    return !stock.taken.includes(index) && stock.claimed === null && run.cinders >= stock.price && (!plan || plan.ok);
  });
}

const PURSES = [0, 3, 9, 10, 11, 25, 40, 90, 400, 400, 200, 60, 150];
const CONFIGS = [
  { itemsPerCategory: 1 }, { itemsPerCategory: 5 }, { itemsPerCategory: 0 }, { price: 0 }, { price: 7 }, { itemsPerCategory: null, options: 4, price: 12 },
  { itemsPerCategory: null, options: 0 }, { itemsPerCategory: 2, price: 1 }, { off: true },
];

function scenario(classId, ci, index) {
  const seed = w.rngMod.sweepSeed(60000 + ci * 100 + index);
  const run = create(seed, classId);
  const rng = w.createRng(seed, run.streamCounters || {});
  const pick = botLcg(seed ^ 0x5b0ce001);
  const steps = [];
  const record = (op, fn, extra = {}) => {
    const before = snapshot(run);
    const result = call(fn);
    steps.push(canon({ op, ...extra, ...(result?.error ? { error: result.error } : { result }), changes: changes(before, run), counters: counters(rng) }));
    return result;
  };
  const plain = (op, fn, extra = {}) => {
    const before = snapshot(run);
    fn();
    steps.push(canon({ op, ...extra, changes: changes(before, run), counters: counters(rng) }));
  };
  // Every fourth scenario runs the shops module under other settings (a copy of the profile, so that no other scenario sees them).
  if (index % 4 === 3) {
    const config = { ...CONFIGS[(ci + index) % CONFIGS.length] };
    run.gameProfile = structuredClone(run.gameProfile);
    plain('config', () => {
      if (config.off) { delete run.gameProfile.modules.shops; return; }
      run.gameProfile.modules.shops = { ...run.gameProfile.modules.shops };
      for (const [k, v] of Object.entries(config)) { if (v === null) delete run.gameProfile.modules.shops[k]; else run.gameProfile.modules.shops[k] = v; }
    }, { module: config });
    if (config.itemsPerCategory === null && !config.off) plain('config', () => { delete run.gameProfile.modules.shops.itemsPerCategory; delete run.gameProfile.content.shops.itemsPerCategory; }, { module: 'noShelves' });
  }
  const SHOPS = 6;
  const lean = ['potions', 'relics', 'weapons', 'armor', 'combatCards', null, 'potions', 'weaponArts'][index % 8];
  for (let k = 0; k < SHOPS; k++) {
    if (pick(4) === 0 && run.deck.length > 3) {
      const ids = [];
      for (let n = 0, count = 1 + pick(4); n < count && run.deck.length - ids.length > 1; n++) { const c = run.deck[pick(run.deck.length)]; if (!ids.includes(c.instanceId)) ids.push(c.instanceId); }
      plain('drop', () => { run.deck = run.deck.filter((c) => !ids.includes(c.instanceId)); w.state.syncZones(run); }, { ids });
    }
    if (pick(8) === 0) {
      plain('range', () => { run.rangePolicy = { ...run.rangePolicy, enabled: run.rangePolicy?.enabled === false }; }, { enabled: run.rangePolicy?.enabled === false });
    }
    const purse = PURSES[pick(PURSES.length)];
    plain('cinders', () => { run.cinders = purse; }, { amount: purse });
    run.actNumber = 1 + (k % 3);
    const nodeId = `n${k}_${pick(3)}`;
    plain('node', () => { run.mapNodeId = nodeId; run.floor = k + 1; }, { nodeId, act: run.actNumber, floor: k + 1 });
    const entered = record('enter', () => enter(run, rng, nodeId), { nodeId });
    if (entered?.error || entered?.map) continue;
    const shopId = entered.shopId;
    steps.push(canon({ op: 'available', shopId, available: availability(run, run.gameProfileRewardOffers[`shop:${shopId}`]), counters: counters(rng) }));
    // A remount, and the same stock asked for again: nothing moves.
    if (pick(2) === 0) record('show', () => show(run, rng), { shopId });
    const n = entered.stock.options.length;
    const buys = pick(5) + (purse >= 10 ? 1 : 0);
    for (let b = 0; b < buys; b++) {
      const roll = pick(14);
      const leaning = lean ? entered.stock.options.findIndex((o, i) => o.kind === lean && !run.gameProfileRewardOffers[`shop:${shopId}`].taken.includes(i)) : -1;
      const indexToBuy = roll === 12 ? n + pick(3) : roll === 13 ? -1 : leaning >= 0 && pick(10) < 7 ? leaning : pick(Math.max(1, n));
      if (roll === 11) {
        // A shop that was never opened, then one whose price was tampered with.
        record('buy', () => canon(rewards.purchaseGameProfileShopOffer(reg, run, `${run.actNumber}:nowhere`, 0)), { shopId: `${run.actNumber}:nowhere`, index: 0 });
        continue;
      }
      if (roll === 10 && b === 0 && pick(3) === 0) {
        const bad = [-1, 2.5, 'x', null][pick(4)];
        plain('tamper', () => { run.gameProfileRewardOffers[`shop:${shopId}`].price = bad; }, { shopId, price: bad });
      }
      record('buy', () => canon(rewards.purchaseGameProfileShopOffer(reg, run, shopId, indexToBuy)), { shopId, index: indexToBuy });
      const stockNow = run.gameProfileRewardOffers[`shop:${shopId}`];
      steps.push(canon({ op: 'available', shopId, available: availability(run, stockNow), counters: counters(rng) }));
      if (pick(3) === 0) record('show', () => show(run, rng), { shopId });
    }
    plain('leave', () => { delete run.gameProfileOpenShop; }, { shopId });
  }
  return canon({ classId, seed, steps, run: runDocument(run), counters: counters(rng) });
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
let bytes = 0;
const write = (name, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, name), text); bytes += Buffer.byteLength(text); };
const index = { source: 'model/gameProfileRewards.js gameProfileShopStock / purchaseGameProfileShopOffer, main.js enterNode + showShop (configured profile default)', webSha: w.head,
  profileId: w.profile.id, profileHash: w.profile.hash, scenarios: [], kinds: {}, errors: {},
  tally: { shops: 0, stocks: 0, buys: 0, bought: 0, refused: 0, entersToMap: 0, tampers: 0, configs: 0, shown: 0, unavailable: 0 } };
const t = index.tally;
const bump = (table, key) => { table[key] = (table[key] || 0) + 1; };
for (const [ci, classId] of classes.entries()) {
  for (let i = 0; i < SEEDS; i++) {
    const name = `shop-${classId}-seed${String(i).padStart(2, '0')}.json`;
    const sc = scenario(classId, ci, i);
    write(name, sc);
    index.scenarios.push({ file: name, classId, seed: sc.seed });
    for (const s of sc.steps) {
      if (s.op === 'enter') {
        t.shops++;
        if (s.result?.map) t.entersToMap++;
        else if (s.result?.stock) { t.stocks++; for (const o of s.result.stock.options) bump(index.kinds, o.kind); }
        else if (s.error) bump(index.errors, `enter: ${s.error}`);
      }
      if (s.op === 'show') t.shown++;
      if (s.op === 'tamper') t.tampers++;
      if (s.op === 'config') t.configs++;
      if (s.op === 'buy') { t.buys++; if (s.error) { t.refused++; bump(index.errors, `buy: ${s.error}`); } else t.bought++; }
      if (s.op === 'available') t.unavailable += s.available.filter((a) => !a).length;
    }
  }
}
write('index.json', index);
console.log(`oracle-core2-shop: ${index.scenarios.length} scenarios (${t.shops} shop visits, ${t.stocks} stocks, ${t.buys} buys: ${t.bought} bought, ${t.refused} refused), ${(bytes / 1024 / 1024).toFixed(1)} MB -> Unity/Assets/Tests/Oracle/core2/shop`);
console.log(JSON.stringify({ kinds: index.kinds, errors: index.errors, tally: t }));
