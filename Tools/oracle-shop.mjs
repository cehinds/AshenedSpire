#!/usr/bin/env node
// Merchant parity oracle (US-9.1–9.3, PF-04/05). Records what the SHIPPED merchant does to a run: main.js enterNode's
// 'merchant' case (buildShopStock, shopPriceMult, the smith's roll, run.shopStock) and every run-mutating handler of
// the shop screen (src/ui/screens/shop.js mountShop: buy card / relic / flask, armament and weapon-art trades through
// model/armamentTrading.js, the card burn through model/cardRemoval.js, the sell shelf when the `shopSell` setting is
// on, the smith the merchant keeps — model/smithing.js commitSmithing and model/cardExtraction.js commitExtraction /
// commitInstall — and Leave). The screen is a browser module, so the harness below re-implements those handlers
// FAITHFULLY (UI-free), calling the real shipped model functions in the same order. It mirrors, line for line:
//   src/main.js 1952-1968            enterNode 'merchant'   (stock, price multiplier, stock.smith, run.shopStock)
//   src/main.js 2485-2491            shopPriceMult()
//   src/main.js 2591-2612            showShop onLeave       (run.shopStock = null; journeys are not recorded)
//   src/ui/screens/shop.js 49-58     sellPriceFor
//   src/ui/screens/shop.js 196-590   the offers, their availability and the handlers (buy, burn, smith, sell)
//   src/ui/screens/shop.js 701-745   buyItem, openWeaponArt, inspectArmament (plan → commit with the plan as quote)
//   src/ui/models/ShopWorkspaceModel.js offerAvailability  (a plan's refusal, then a full belt, then the purse)
// What the screen refuses without calling a model (no stock, an unaffordable price, a full flask belt, a burn the
// grid would not offer, a smith service not rolled) is recorded with the screen's own availability key
// (ui.shop.avail.*); what a model refuses is recorded with its message, which the C# port names by a key in
// strings/shop.en.json whose text must equal the shipped message.
//
// Sessions: class × seed × run variant (purse, relics, a full flask belt, a full inventory, Smithing Stones, emptied
// and extra mounts, smithed items, removals bought, a thin deck, Greedy Merchants / Hoarder / Ascension, the sell
// toggle off, acts 1–3), each opening the merchant and then taking a generated sequence of legal and illegal actions
// (stale quotes included). Each step records the action, the receipt or refusal, the top-level run fields it changed
// and the shop view (every offer's availability).
//
//   node Tools/oracle-shop.mjs [--source D:/repos/AshenSpire] [--sessions 240] [--coverage]
//
// Writes Unity/Assets/Tests/Oracle/shop/*.json (committed; CI has no access to the old repo). Only .json files are
// deleted on regeneration, so the Unity .meta files beside them survive; a missing .meta is created.
import { writeFileSync, mkdirSync, rmSync, readdirSync, readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const SOURCE = arg('--source', process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const SESSIONS = Number(arg('--sessions', '0'));
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'shop');
const STRINGS = JSON.parse(readFileSync(join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'strings', 'shop.en.json'), 'utf8'));
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng, sweepSeed } = await load('src/engine/rng.js');
const { createRunState } = await load('src/model/state.js');
const { buildShopStock } = await load('src/engine/encounters.js');
const { activeMods } = await load('src/content/customMods.js');
const { carriedIds, addToStorage } = await load('src/model/loadout.js');
const { canRemoveDeckCard, removeDeckCard } = await load('src/model/cardRemoval.js');
const { armamentPurchasePlan, armamentSalePlan, commitArmamentPurchase, commitArmamentSale, eligibleWeaponArts } = await load('src/model/armamentTrading.js');
const { smithingPlan, commitSmithing } = await load('src/model/smithing.js');
const { smithServicesAt, extractionPlan, installPlan, commitExtraction, commitInstall } = await load('src/model/cardExtraction.js');
const { syncFlaskGrowth } = await load('src/model/flaskgrowth.js');
const { flaskSlotCap } = await load('src/model/gracerefill.js');
const { utilityFlaskIds } = await load('src/model/gracerefill.js');

// Two presets (D-019/D-029): the shipped defaults and the owner's reference tuning, applied through the shipped import
// path exactly as Tools/export-content.mjs did (raw/reference.settings.json). The C# side builds the same two presets
// through ConfigLayers.
const REFERENCE_SETTINGS = JSON.parse(readFileSync(join(ROOT, 'Tools', 'export', 'raw', 'reference.settings.json'), 'utf8'));
const PRESETS = {
  shipped: createRegistries(configuredContentBundle(contentBundle, {})),
  reference: createRegistries(configuredContentBundle(contentBundle, REFERENCE_SETTINGS)),
};
let registries = PRESETS.shipped;
const classes = contentBundle.classes.map((c) => c.id);
const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x)));

// ------------------------------------------------------------------ refusal keys

// The screen's availability ids (strings/en.json ui.shop.avail.*): the C# port returns these keys.
const AVAIL = { cinders: 'ui.shop.avail.cinders', full: 'ui.shop.avail.full', locked: 'ui.shop.avail.locked' };
const TEMPLATES = Object.entries(STRINGS).map(([key, text]) => ({
  key,
  re: new RegExp(`^${text.replace(/[.*+?^${}()|[\]\\]/g, (c) => (c === '{' || c === '}' ? c : `\\${c}`)).replace(/\{\d+\}/g, '(.*?)')}$`),
}));
/** A model's message as a refusal: the strings/shop.en.json key whose text (args filled) is the message. */
function refusalFor(message) {
  for (const t of TEMPLATES) {
    const m = t.re.exec(message);
    if (m) return { key: t.key, args: m.slice(1) };
  }
  throw new Error(`oracle-shop: no strings/shop.en.json key says "${message}"`);
}
const refuse = (key, args = []) => ({ ok: false, refusal: { key, args } });
const accept = (receipt) => ({ ok: true, receipt: plain(receipt) });

// ------------------------------------------------------------------ the harness (main.js merchant + shop.js handlers)

function shopPriceMult(run) {
  const mods = run.custom ? activeMods(run.custom) : {};
  let m = 1;
  if (mods.expensiveShops) m *= registries.balance.customMods.expensiveShopsMult;
  if (mods.hoarder) m *= registries.balance.customMods.hoarderShopMult;
  return m;
}

/** main.js enterNode, case 'merchant' (the journey's atlas shop is not recorded). */
function openMerchant(run, rng) {
  const stock = buildShopStock(registries, rng, run);
  const pm = shopPriceMult(run);
  if (pm !== 1) {
    for (const kind of ['cards', 'relics', 'flasks']) {
      for (const item of stock[kind]) item.cost = Math.ceil(item.cost * pm);
    }
    stock.removeCost = Math.ceil(stock.removeCost * pm);
  }
  stock.smith = smithServicesAt(registries, 'merchant', rng);
  run.shopStock = stock;
  return stock;
}

function sellPriceFor(kind, def) {
  const shop = registries.balance.shop;
  const fraction = shop.sellFraction;
  if (!(fraction > 0)) return 0;
  if (kind === 'relic') {
    const range = shop.relicCost[def.rarity];
    return range ? Math.floor(range[0] * fraction) : 0;
  }
  return Math.floor(shop.flaskCost[0] * fraction);
}

/** ShopWorkspaceModel offerAvailability → the refusal key, or null when the offer can be taken. */
function availability({ price = null, cinders = 0, reason = null, capacityFull = false } = {}) {
  if (reason) return refusalFor(reason).key;
  if (capacityFull) return AVAIL.full;
  if (price != null && !(cinders >= price)) return AVAIL.cinders;
  return null;
}

const smithHere = (stock) => (stock.smith && stock.smith.offered ? stock.smith.services : []);

/** What the screen shows: every offer with its price and whether it can be taken now (and why not). */
function shopView(run, sellOn) {
  const stock = run.shopStock;
  if (!stock) return null;
  const slotsFree = run.flasks.length < flaskSlotCap(registries.balance);
  const view = {};
  view.cards = stock.cards.map((item, index) => ({ index, id: item.id, cost: item.cost, refusal: availability({ price: item.cost, cinders: run.cinders }) }));
  view.armaments = (stock.armaments || []).map((item, index) => ({ index, item, plan: armamentPurchasePlan(registries, run, item) }))
    .filter(({ plan }) => plan.def)
    .map(({ index, item, plan }) => ({ index, id: item.id, cost: plan.cost, refusal: plan.ok ? null : refusalFor(plan.reason).key }));
  view.weaponArts = (stock.weaponArts || []).map((item, index) => {
    const quote = armamentPurchasePlan(registries, run, item, 'weaponArt');
    return { index, id: item.id, cost: item.cost, refusal: quote.ok ? null : refusalFor(quote.reason).key };
  });
  view.relics = stock.relics.map((item, index) => ({ index, id: item.id, cost: item.cost, refusal: availability({ price: item.cost, cinders: run.cinders }) }));
  view.flasks = stock.flasks.map((item, index) => ({ index, id: item.id, cost: item.cost, refusal: availability({ price: item.cost, cinders: run.cinders, capacityFull: !slotsFree }) }));
  const removeAvail = availability({ price: stock.removeCost, cinders: run.cinders });
  const removeOpen = !removeAvail && run.deck.length > 1;
  view.remove = {
    cost: stock.removeCost,
    refusal: removeOpen ? null : (removeAvail || AVAIL.locked),
    cards: run.deck.filter((inst) => canRemoveDeckCard(inst)).map((inst) => inst.instanceId),
  };
  const here = smithHere(stock);
  const smith = { offered: !!(stock.smith && stock.smith.offered), services: [...here] };
  if (here.includes('upgrade')) {
    const plan = smithingPlan(registries, run);
    smith.upgrade = { refusal: plan.candidates.length ? null : AVAIL.locked, stones: plan.stones, candidates: plan.candidates.map((c) => c.itemRef) };
  }
  for (const service of ['extract', 'install']) {
    if (!here.includes(service)) continue;
    const plan = service === 'extract' ? extractionPlan(registries, run) : installPlan(registries, run);
    smith[service] = { refusal: plan.candidates.length ? null : AVAIL.locked, stones: plan.stones, cost: plan.cost, candidates: plan.candidates.map((c) => c.itemRef) };
  }
  view.smith = smith;
  if (sellOn) {
    const armaments = carriedIds(run.loadout).map((id) => armamentSalePlan(registries, run, id)).filter((plan) => plan.def)
      .map((plan) => ({ id: plan.id, price: plan.price, refusal: plan.ok ? null : refusalFor(plan.reason).key }));
    const goods = [];
    run.relics.forEach((rid, at) => {
      const price = sellPriceFor('relic', registries.relics.get(rid));
      if (price > 0) goods.push({ kind: 'relic', index: at, id: rid, price });
    });
    run.flasks.forEach((f, at) => {
      const price = sellPriceFor('flask', registries.flasks.get(f.flaskId));
      if (price > 0) goods.push({ kind: 'flask', index: at, id: f.flaskId, price });
    });
    view.sell = { armaments, goods };
  } else view.sell = null;
  return plain(view);
}

/** The inert quote a stale inspection leaves behind (the screen held a plan, then the shop changed). */
function quoteFor(plan, mode) {
  if (!mode || mode === 'fresh') return plan;
  if (mode === 'staleCost') return { ...plan, cost: plan.cost + 1 };
  if (mode === 'stalePrice') return { ...plan, price: plan.price + 1 };
  if (mode === 'staleRevision') return { ...plan, revision: plan.revision + 1 };
  if (mode === 'staleSignature') return { ...plan, signature: `${plan.signature}~` };
  throw new Error(`quote mode ${mode}`);
}

/** One shop action on the run: the screen's own gate first, then the model call it makes. */
function execute(run, sellOn, action) {
  const stock = run.shopStock;
  if (action.kind === 'leave') {
    if (!stock) return refuse(AVAIL.locked);
    run.shopStock = null; // showShop onLeave (finishWorldService is a journey's)
    return accept({ kind: 'leave' });
  }
  if (!stock) return refuse(AVAIL.locked);
  switch (action.kind) {
    case 'buyCard': {
      const item = stock.cards[action.index];
      if (!item) return refuse(refusalFor('This offer is no longer available.').key);
      if (!(run.cinders >= item.cost)) return refuse(AVAIL.cinders);
      run.cinders -= item.cost;
      const instance = { instanceId: `s${run.deck.length}_${item.id}`, cardId: item.id, upgraded: false };
      run.deck.push(instance);
      stock.cards.splice(action.index, 1);
      return accept({ kind: 'card', id: item.id, spent: item.cost, instance });
    }
    case 'buyRelic': {
      const item = stock.relics[action.index];
      if (!item) return refuse(refusalFor('This offer is no longer available.').key);
      if (!(run.cinders >= item.cost)) return refuse(AVAIL.cinders);
      run.cinders -= item.cost;
      run.relics.push(item.id);
      syncFlaskGrowth(registries, run);
      stock.relics.splice(action.index, 1);
      return accept({ kind: 'relic', id: item.id, spent: item.cost });
    }
    case 'buyFlask': {
      const item = stock.flasks[action.index];
      if (!item) return refuse(refusalFor('This offer is no longer available.').key);
      if (!(run.flasks.length < flaskSlotCap(registries.balance))) return refuse(AVAIL.full);
      if (!(run.cinders >= item.cost)) return refuse(AVAIL.cinders);
      run.cinders -= item.cost;
      run.flasks.push({ flaskId: item.id });
      stock.flasks.splice(action.index, 1);
      return accept({ kind: 'flask', id: item.id, spent: item.cost });
    }
    case 'buyArmament':
    case 'buyWeaponArt': {
      const kind = action.kind === 'buyArmament' ? 'armament' : 'weaponArt';
      const shelf = kind === 'armament' ? 'armaments' : 'weaponArts';
      const item = (stock[shelf] || [])[action.index];
      // The screen only inspects an offer on the shelf; the plan of a missing weapon art would throw (cards.get).
      if (!item) return refuse(refusalFor('This offer is no longer available.').key);
      const plan = armamentPurchasePlan(registries, run, item, kind);
      try { return accept(commitArmamentPurchase(registries, run, quoteFor(plan, action.quote))); }
      catch (e) { const r = refusalFor(e.message); return refuse(r.key, r.args); }
    }
    case 'removeCard': {
      if (!(run.cinders >= stock.removeCost)) return refuse(AVAIL.cinders);
      if (!(run.deck.length > 1)) return refuse(AVAIL.locked);
      const card = run.deck.find((c) => c.instanceId === action.instanceId);
      const cost = stock.removeCost;
      if (!removeDeckCard(run, action.instanceId, { keepOne: true })) return refuse(AVAIL.locked);
      run.cinders -= cost;
      run.removesPurchased = (run.removesPurchased || 0) + 1;
      stock.removeCost = registries.balance.shop.removeBase + registries.balance.shop.removeStep * run.removesPurchased;
      return accept({ kind: 'remove', instanceId: action.instanceId, cardId: card.cardId, spent: cost, removeCost: stock.removeCost });
    }
    case 'sellRelic':
    case 'sellFlask': {
      if (!sellOn) return refuse(AVAIL.locked);
      const kind = action.kind === 'sellRelic' ? 'relic' : 'flask';
      const list = kind === 'relic' ? run.relics : run.flasks;
      const held = list[action.index];
      if (held === undefined) return refuse(refusalFor('This offer is no longer available.').key);
      const id = kind === 'relic' ? held : held.flaskId;
      const price = sellPriceFor(kind, kind === 'relic' ? registries.relics.get(id) : registries.flasks.get(id));
      if (!(price > 0)) return refuse(AVAIL.locked);
      if (kind === 'relic') {
        run.relics.splice(action.index, 1);
        syncFlaskGrowth(registries, run);
      } else run.flasks.splice(action.index, 1);
      run.cinders += price;
      return accept({ kind, id, received: price });
    }
    case 'sellArmament': {
      if (!sellOn) return refuse(AVAIL.locked);
      const plan = armamentSalePlan(registries, run, action.id);
      try { return accept(commitArmamentSale(registries, run, quoteFor(plan, action.quote))); }
      catch (e) { const r = refusalFor(e.message); return refuse(r.key, r.args); }
    }
    case 'smithUpgrade': {
      if (!smithHere(stock).includes('upgrade')) return refuse(AVAIL.locked);
      if (!smithingPlan(registries, run).candidates.length) return refuse(AVAIL.locked);
      try { return accept(commitSmithing(registries, run, action.itemRef)); }
      catch (e) { const r = refusalFor(e.message); return refuse(r.key, r.args); }
    }
    case 'smithExtract':
    case 'smithInstall': {
      const service = action.kind === 'smithExtract' ? 'extract' : 'install';
      if (!smithHere(stock).includes(service)) return refuse(AVAIL.locked);
      const plan = service === 'extract' ? extractionPlan(registries, run) : installPlan(registries, run);
      if (!plan.candidates.length) return refuse(AVAIL.locked);
      try {
        return accept(service === 'extract'
          ? commitExtraction(registries, run, action.itemRef, action.mountKey)
          : commitInstall(registries, run, action.itemRef, action.mountKey, action.instanceId));
      } catch (e) { const r = refusalFor(e.message); return refuse(r.key, r.args); }
    }
    default:
      throw new Error(`oracle-shop: unknown action ${action.kind}`);
  }
}

// ------------------------------------------------------------------ runs, variants and generated sessions

// main.js newRun's additions to a created run.
function newRun(seed, classId) {
  const run = createRunState({ seed, classId, registries });
  run.customization = { name: 'Forsaken', glyph: '⚔', tint: 'gold' };
  run.custom = { ascension: 0, mods: {}, deckMode: 'standard' };
  run.stats = { fightsWon: 0, damageDealt: 0, damageTaken: 0 };
  run.path = [];
  run.seenEvents = [];
  run.lastEncounters = [];
  return run;
}

// A deterministic chooser independent of the game RNG (the harness's own LCG).
function lcg(seed) { let s = seed >>> 0 || 1; return (n) => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return n > 0 ? s % n : 0; }; }

const armamentIds = () => registries.equipment.armaments.map((a) => a.id);
const eligibleArts = () => eligibleWeaponArts(registries);
const rewardRelics = () => registries.relics.all().filter((r) => (r.pool || 'reward') === 'reward' && ['common', 'uncommon', 'rare'].includes(r.rarity)).map((r) => r.id);

/** Extract then (sometimes) seat cards for free, the smith's own commits, to leave emptied and extra mounts behind. */
function workMounts(run, pick, installToo) {
  // Few pieces lend a card a smith can lift (an `extractable` weapon art); carry them so the bench has work.
  for (const id of armamentIds()) {
    const probe = structuredClone(run);
    probe.loadout.storage = [id];
    if (extractionPlan(registries, probe).candidates.some((c) => c.itemRef === `armament/${id}`)) addToStorage(run.loadout, id, registries.balance.equipment.storageSlots);
  }
  const ex = extractionPlan(registries, run);
  if (ex.candidates.length) {
    const c = ex.candidates[pick(ex.candidates.length)];
    const m = c.mounts[pick(c.mounts.length)];
    commitExtraction(registries, run, c.itemRef, m.mountKey, undefined, { free: true });
  }
  if (!installToo) return;
  const ins = installPlan(registries, run);
  if (ins.candidates.length) {
    const c = ins.candidates[pick(ins.candidates.length)];
    const m = c.mounts[pick(c.mounts.length)];
    commitInstall(registries, run, c.itemRef, m.mountKey, m.cards[pick(m.cards.length)].instanceId, undefined, { free: true });
  }
}

const VARIANTS = [
  { name: 'poor', setup: (run) => { run.cinders = 0; } },
  { name: 'modest', setup: (run) => { run.cinders = 300; } },
  { name: 'rich', setup: (run) => { run.cinders = 6000; } },
  { name: 'relics', setup: (run, pick) => { run.cinders = 2500; const pool = rewardRelics(); for (let i = 0; i < 3; i++) { const id = pool[pick(pool.length)]; if (!run.relics.includes(id)) run.relics.push(id); } syncFlaskGrowth(registries, run); } },
  { name: 'flasks-full', setup: (run, pick) => { run.cinders = 2500; const ids = utilityFlaskIds(registries); while (run.flasks.length < flaskSlotCap(registries.balance)) run.flasks.push({ flaskId: ids[pick(ids.length)] }); } },
  { name: 'flasks-some', setup: (run, pick) => { run.cinders = 900; const ids = utilityFlaskIds(registries); run.flasks.push({ flaskId: ids[pick(ids.length)] }); } },
  { name: 'inventory-full', setup: (run, pick) => { run.cinders = 6000; const ids = armamentIds(); let guard = 0; while ((run.loadout.storage || []).length < registries.balance.equipment.storageSlots && guard++ < 200) addToStorage(run.loadout, ids[pick(ids.length)], registries.balance.equipment.storageSlots); } },
  { name: 'inventory-some', setup: (run, pick) => { run.cinders = 3000; const ids = armamentIds(); for (let i = 0; i < 3; i++) addToStorage(run.loadout, ids[pick(ids.length)], registries.balance.equipment.storageSlots); } },
  { name: 'stones', setup: (run) => { run.cinders = 1200; run.smithingStones = 6; }, smith: true },
  { name: 'bench', setup: (run, pick) => { run.cinders = 900; run.smithingStones = 3; workMounts(run, pick, false); run.loadout.storage.reverse(); }, smith: true },
  { name: 'stones-short', setup: (run) => { run.cinders = 400; run.smithingStones = 0; }, smith: true },
  { name: 'mounts-emptied', setup: (run, pick) => { run.cinders = 800; run.smithingStones = 2; workMounts(run, pick, false); }, smith: true },
  { name: 'mounts-carried', setup: (run, pick) => { run.cinders = 800; run.smithingStones = 1; const ex = extractionPlan; void ex; for (const id of armamentIds()) { const probe = structuredClone(run); probe.loadout.storage = [id]; if (extractionPlan(registries, probe).candidates.length) addToStorage(run.loadout, id, registries.balance.equipment.storageSlots); } }, smith: true },
  { name: 'mounts-worked', setup: (run, pick) => { run.cinders = 800; run.smithingStones = 2; workMounts(run, pick, true); workMounts(run, pick, false); }, smith: true },
  {
    name: 'smithed',
    setup: (run, pick) => {
      run.cinders = 3000;
      run.smithingStones = 3;
      const ids = armamentIds();
      addToStorage(run.loadout, ids[pick(ids.length)], registries.balance.equipment.storageSlots);
      const plan = smithingPlan(registries, run);
      if (plan.candidates.length) commitSmithing(registries, run, plan.candidates[pick(plan.candidates.length)].itemRef, undefined, { free: true });
    },
    smith: true,
  },
  { name: 'removes-bought', setup: (run) => { run.cinders = 1500; run.removesPurchased = 3; } },
  { name: 'thin-deck', setup: (run) => { run.cinders = 1500; run.deck = run.deck.filter((c) => canRemoveDeckCard(c)).slice(0, 2); } },
  { name: 'greedy', setup: (run) => { run.cinders = 2000; run.custom.mods.expensiveShops = true; } },
  { name: 'hoarder', setup: (run) => { run.cinders = 3000; run.custom.mods.hoarder = true; } },
  { name: 'greedy-hoarder', setup: (run) => { run.cinders = 5000; run.custom.mods.expensiveShops = true; run.custom.mods.hoarder = true; } },
  { name: 'ascension', setup: (run) => { run.cinders = 2000; run.custom.ascension = 6; } },
  { name: 'sell-off', setup: (run) => { run.cinders = 2000; }, sellOn: false },
  { name: 'legacy-levels', setup: (run) => { run.cinders = 900; run.smithingStones = 4; const id = carriedIds(run.loadout).find((x) => armamentIds().includes(x)); if (id) run.armamentLevels = { [id]: 1 }; delete run.itemUpgradeLevels; }, smith: true },
  { name: 'no-loadout', setup: (run) => { run.cinders = 5000; run.loadout = null; }, sellOn: false },
  { name: 'fractional-cinders', setup: (run, pick) => { run.cinders = 2500.5; const ids = armamentIds(); addToStorage(run.loadout, ids[pick(ids.length)], registries.balance.equipment.storageSlots); } },
  {
    // A save whose shelves were edited: a carried armament, a piece with no price, a card that is no weapon art, an
    // unknown piece, and an art the deck already bought on an earlier visit (so its instance id is taken).
    name: 'tampered',
    setup: (run, pick) => {
      run.cinders = 4000;
      const arts = eligibleArts();
      const art = arts[pick(arts.length)];
      run.deck.push({ instanceId: `shop-art:1:${art}`, cardId: art, upgraded: false });
    },
    patch: (run) => {
      const art = run.deck.find((c) => c.instanceId.startsWith('shop-art:1:')).cardId;
      const carried = carriedIds(run.loadout).find((x) => armamentIds().includes(x));
      const plainCard = registries.cards.all().find((c) => !eligibleArts().includes(c.id) && c.class === run.class).id;
      return {
        armaments: [...(run.shopStock.armaments || []), ...(carried ? [{ id: carried, cost: 120 }] : []), { id: armamentIds()[0], cost: 0 }, { id: 'noSuchArmament', cost: 50 }],
        weaponArts: [...(run.shopStock.weaponArts || []), { id: art, cost: 90 }, { id: plainCard, cost: 90 }],
      };
    },
    // Walk the edited shelves from the end: the unknown piece, the unpriced one, the carried one, then both cards.
    forced: (run) => [
      ...run.shopStock.armaments.map((_, i) => ({ kind: 'buyArmament', index: run.shopStock.armaments.length - 1 - i })).slice(0, 3),
      { kind: 'buyWeaponArt', index: run.shopStock.weaponArts.length - 1 },
      { kind: 'buyWeaponArt', index: run.shopStock.weaponArts.length - 2 },
    ],
  },
  { name: 'act2', setup: (run) => { run.cinders = 1800; run.actNumber = 2; run.floor = 7; } },
  { name: 'act3', setup: (run, pick) => { run.cinders = 2600; run.actNumber = 3; run.floor = 11; const pool = rewardRelics(); run.relics.push(pool[pick(pool.length)]); } },
];

/** The actions a player could try now, legal and not (the generator picks from these). */
function candidates(run, sellOn) {
  const stock = run.shopStock;
  const groups = [];
  if (!stock) return [[{ kind: 'buyCard', index: 0 }, { kind: 'leave' }, { kind: 'sellRelic', index: 0 }]];
  groups.push([...stock.cards.map((_, index) => ({ kind: 'buyCard', index })), { kind: 'buyCard', index: stock.cards.length }]);
  groups.push([...stock.relics.map((_, index) => ({ kind: 'buyRelic', index })), { kind: 'buyRelic', index: stock.relics.length }]);
  groups.push([...stock.flasks.map((_, index) => ({ kind: 'buyFlask', index })), { kind: 'buyFlask', index: stock.flasks.length }]);
  const arms = (stock.armaments || []).map((_, index) => ({ kind: 'buyArmament', index }));
  groups.push([...arms, ...arms, { kind: 'buyArmament', index: arms.length }, ...arms.slice(0, 1).map((a) => ({ ...a, quote: 'staleCost' })), ...arms.slice(0, 1).map((a) => ({ ...a, quote: 'staleRevision' }))]);
  const arts = (stock.weaponArts || []).map((_, index) => ({ kind: 'buyWeaponArt', index }));
  groups.push([...arts, ...arts, { kind: 'buyWeaponArt', index: arts.length }, ...arts.slice(0, 1).map((a) => ({ ...a, quote: 'staleRevision' }))]);
  const removable = run.deck.filter((c) => canRemoveDeckCard(c)).map((c) => ({ kind: 'removeCard', instanceId: c.instanceId }));
  const pinned = run.deck.filter((c) => !canRemoveDeckCard(c)).slice(0, 1).map((c) => ({ kind: 'removeCard', instanceId: c.instanceId }));
  groups.push([...removable, ...pinned, { kind: 'removeCard', instanceId: 'noSuchCard' }]);
  groups.push([...run.relics.map((_, index) => ({ kind: 'sellRelic', index })), ...run.flasks.map((_, index) => ({ kind: 'sellFlask', index })), { kind: 'sellFlask', index: run.flasks.length }]);
  const carried = carriedIds(run.loadout).map((id) => ({ kind: 'sellArmament', id }));
  groups.push([...carried, ...carried.slice(0, 1).map((a) => ({ ...a, quote: 'stalePrice' })), ...carried.slice(0, 1).map((a) => ({ ...a, quote: 'staleSignature' })), ...carried.slice(0, 1).map((a) => ({ ...a, quote: 'staleRevision' })), { kind: 'sellArmament', id: 'noSuchArmament' }, ...armamentIds().filter((id) => !carriedIds(run.loadout).includes(id)).slice(0, 1).map((id) => ({ kind: 'sellArmament', id }))]);
  const up = smithingPlan(registries, run).candidates.map((c) => ({ kind: 'smithUpgrade', itemRef: c.itemRef }));
  groups.push([...up, ...up, ...up.filter((a) => a.itemRef.startsWith('armament/')).slice(0, 1).map((a) => ({ ...a, itemRef: a.itemRef.slice('armament/'.length) })), { kind: 'smithUpgrade', itemRef: 'armament/noSuchArmament' }]);
  const ex = extractionPlan(registries, run).candidates.flatMap((c) => c.mounts.map((m) => ({ kind: 'smithExtract', itemRef: c.itemRef, mountKey: m.mountKey })));
  groups.push([...ex, ...ex, { kind: 'smithExtract', itemRef: (ex[0] || {}).itemRef || 'armament/noSuchArmament', mountKey: 'noSuchMount' }, { kind: 'smithExtract', itemRef: 'armament/noSuchArmament', mountKey: 'noSuchMount' }]);
  const ins = installPlan(registries, run).candidates.flatMap((c) => c.mounts.flatMap((m) => m.cards.map((card) => ({ kind: 'smithInstall', itemRef: c.itemRef, mountKey: m.mountKey, instanceId: card.instanceId }))));
  groups.push([...ins, ...ins, ...ins.slice(0, 1).map((a) => ({ ...a, instanceId: 'noSuchCard' })), ...ins.slice(0, 1).map((a) => ({ ...a, mountKey: 'noSuchMount' })), { kind: 'smithInstall', itemRef: 'armament/noSuchArmament', mountKey: 'noSuchMount', instanceId: 'noSuchCard' }]);
  return groups.filter((g) => g.length);
}

// Deterministic .meta for a new oracle file (Unity text asset); an existing .meta is never rewritten.
function ensureMeta(file) {
  const meta = `${file}.meta`;
  if (existsSync(meta)) return;
  const guid = createHash('md5').update(`ashen-oracle-shop:${file.split(/[\\/]/).pop()}`).digest('hex');
  writeFileSync(meta, `fileFormatVersion: 2\nguid: ${guid}\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n`);
}

/**
 * The top-level run fields a step changed: new values, removed keys, and — only when it moved — the key order after
 * (a replay applies `set` in place, appends new keys and drops `removed`, then checks `keys` when present).
 */
function runDelta(before, after) {
  const set = {};
  for (const [k, v] of Object.entries(after)) if (!(k in before) || JSON.stringify(before[k]) !== JSON.stringify(v)) set[k] = v;
  const removed = Object.keys(before).filter((k) => !(k in after));
  const keys = Object.keys(after);
  const replayed = [...Object.keys(before).filter((k) => k in after), ...keys.filter((k) => !(k in before))];
  return JSON.stringify(replayed) === JSON.stringify(keys) ? { set, removed } : { set, removed, keys };
}

let coverage = null;
if (process.argv.includes('--coverage')) {
  const { Session } = await import('node:inspector/promises');
  coverage = new Session();
  coverage.connect();
  await coverage.post('Profiler.enable');
  await coverage.post('Profiler.startPreciseCoverage', { callCount: true, detailed: true });
  await coverage.post('Profiler.takePreciseCoverage');
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const index = [];
const tally = { accepted: {}, refused: {} };
const STEPS = 14;
const SESSION_COUNT = SESSIONS || classes.length * VARIANTS.length * 2;

// A class the preset cannot start a run for (the reference tuning's Reaver flask allocation does not fill the
// capacity, so the shipped createRunState refuses it) is replaced by the next class that can start.
const startable = Object.fromEntries(Object.entries(PRESETS).map(([id, r]) => [id, classes.filter((c) => {
  try { createRunState({ seed: 1, classId: c, registries: r }); return true; } catch { return false; }
})]));
for (let j = 0; j < SESSION_COUNT; j++) {
  const variant = VARIANTS[Math.floor(j / classes.length) % VARIANTS.length];
  const preset = Math.floor(j / (classes.length * VARIANTS.length)) % 2 === 0 ? 'shipped' : 'reference';
  registries = PRESETS[preset];
  const classId = startable[preset][j % startable[preset].length];
  const seed = sweepSeed(7000 + j);
  const pick = lcg(seed ^ 0x5bd1e995);
  const run = newRun(seed, classId);
  variant.setup(run, pick);
  const sellOn = variant.sellOn ?? true;
  const rng = createRng(seed);
  // Advance the merchant's streams a little so sessions do not all open on counter 0.
  for (let i = 0; i < j % 5; i++) { rng.float('shop'); rng.float('smith'); }
  const rngBefore = rng.getCounters();
  const runBefore = plain(run);

  let prev = plain(run);
  const stock = plain(openMerchant(run, rng));
  // Every other smith session keeps a smith even when the roll said no: the recorded patch, applied by both sides.
  let stockPatch = null;
  if (variant.smith && !run.shopStock.smith.offered && j % 4 !== 3) {
    stockPatch = { smith: { nodeKind: 'merchant', offered: true, rolled: true, chance: run.shopStock.smith.chance, services: ['upgrade', 'extract', 'install'] } };
  }
  if (variant.patch) stockPatch = { ...(stockPatch || {}), ...plain(variant.patch(run)) };
  if (stockPatch) Object.assign(run.shopStock, structuredClone(stockPatch));
  const openAfter = plain(run);
  const open = { stock, runDelta: runDelta(prev, openAfter), rngAfter: rng.getCounters(), view: shopView(run, sellOn) };
  prev = openAfter;

  const steps = [];
  let lastView = JSON.stringify(open.view);
  const forced = variant.forced ? variant.forced(run) : [];
  for (let s = 0; s < STEPS; s++) {
    let action;
    if (s < forced.length) action = forced[s];
    else if (s === STEPS - 2 && pick(3) === 0) action = { kind: 'leave' };
    else {
      const all = candidates(run, sellOn);
      // Smith sessions spend most steps at the smith's bench.
      const smithGroups = all.filter((g) => g[0].kind.startsWith('smith'));
      const groups = variant.smith && smithGroups.length && pick(10) < 6 ? smithGroups : all;
      const group = groups[pick(groups.length)];
      action = group[pick(group.length)];
    }
    const result = execute(run, sellOn, action);
    const after = plain(run);
    const bucket = result.ok ? tally.accepted : tally.refused;
    const label = result.ok ? action.kind : `${action.kind}:${result.refusal.key}`;
    bucket[label] = (bucket[label] || 0) + 1;
    // The view is recorded when it changed (a replay keeps the last one otherwise).
    const view = shopView(run, sellOn);
    const step = { action, result, runDelta: runDelta(prev, after) };
    if (JSON.stringify(view) !== lastView) step.view = view;
    lastView = JSON.stringify(view);
    steps.push(step);
    prev = after;
  }
  const name = `session-${String(j).padStart(3, '0')}-${classId}-${variant.name}`;
  const out = { name, preset, classId, variant: variant.name, seed, sellOn, rngBefore, run: runBefore, stockPatch, expected: { open, steps } };
  const file = `${name}.json`;
  writeFileSync(join(OUT, file), `${JSON.stringify(out)}\n`);
  ensureMeta(join(OUT, file));
  index.push({ file, name, preset, classId, variant: variant.name, smithOffered: !!run.shopStock?.smith?.offered || !!stockPatch || steps.some((st) => st.view?.smith?.offered) });
}

if (coverage) {
  const { result } = await coverage.post('Profiler.takePreciseCoverage');
  const FILES = ['engine/encounters.js', 'model/armamentTrading.js', 'model/cardRemoval.js', 'model/smithing.js', 'model/smithingRules.js', 'model/cardExtraction.js', 'model/itemUpgrades.js', 'model/cardMounts.js'];
  const FUNCS = new Set(['buildShopStock', 'rollShopCards', 'eligibleWeaponArts', 'armamentPurchasePlan', 'commitArmamentPurchase', 'armamentSalePlan', 'commitArmamentSale', 'canRemoveDeckCard', 'removeDeckCard', 'retiredAttackSlots',
    'smithingPlan', 'commitItemUpgrade', 'commitSmithing', 'restampSmithingCards', 'smithingCardReceipt', 'armamentRolePreviews', 'rolePreviewInstance', 'cardChangesForTier', 'requirementPreview', 'requirementAtLevel', 'genericCardChanges', 'ownedItemRefs', 'activeArmourRef', 'levels', 'stoneBalance', 'sourceArmamentId', 'instanceSourceId', 'sourceCards', 'numericEffect', 'effectsReceipt', 'itemByRef',
    'normalizeSmithingRules', 'normalizeServices', 'smithServiceRules', 'smithServicesAt', 'ownedMountItems', 'mountRows', 'extractionPlan', 'commitExtraction', 'installPlan', 'commitInstall', 'installableCards', 'nextTransaction', 'writeMount', 'identityFields', 'priced',
    'itemRefIdentity', 'parseItemUpgradeTag', 'itemUpgradeTagMatchesKind', 'itemUpgradeRows', 'itemUpgradeTiers', 'itemUpgradeCost', 'cumulativeRequirementDelta', 'resolveUpgradedEquipment', 'resolveUpgradedRelic', 'resolveUpgradedItem', 'itemUpgradeValueReceipts', 'applyItemCardUpgradeRows', 'openExtraMountKey', 'isExtraMountKey']);
  for (const script of result.filter((s) => FILES.some((f) => s.url.endsWith(`/src/${f}`)))) {
    const src = readFileSync(fileURLToPath(script.url), 'utf8');
    const lineOf = (off) => src.slice(0, off).split('\n').length;
    for (const fn of script.functions.filter((f) => FUNCS.has(f.functionName))) {
      if (fn.ranges[0].count === 0) { console.log(`${script.url.split('/src/')[1]} ${fn.functionName}: NEVER CALLED`); continue; }
      const dead = fn.ranges.slice(1).filter((r) => r.count === 0).map((r) => `L${lineOf(r.startOffset)} ${src.slice(r.startOffset, Math.min(r.endOffset, r.startOffset + 60)).replace(/\s+/g, ' ')}`);
      console.log(`${script.url.split('/src/')[1]} ${fn.functionName}: ${fn.ranges[0].count} calls, ${fn.ranges.length - 1} blocks, ${dead.length} unrun${dead.length ? `\n    ${dead.join('\n    ')}` : ''}`);
    }
  }
}

writeFileSync(join(OUT, 'index.json'), `${JSON.stringify({
  source: 'main.js enterNode merchant + shopPriceMult + showShop onLeave; ui/screens/shop.js handlers over engine/encounters.js buildShopStock, model/{armamentTrading,cardRemoval,smithing,cardExtraction,flaskgrowth}.js (shipped preset)',
  sessions: index, tally,
}, null, 1)}\n`);
ensureMeta(join(OUT, 'index.json'));
console.log(`oracle-shop: ${index.length} sessions × ${STEPS} steps → Unity/Assets/Tests/Oracle/shop`);
console.log(JSON.stringify(tally));
