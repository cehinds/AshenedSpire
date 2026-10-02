#!/usr/bin/env node
// Combat parity oracle (US-5.x/6.x, PF-04/05/10). Drives the SHIPPED combat engine exactly as main.js does,
// records the initial combat snapshot and a seeded bot's command sequence with a projection after every step.
// The C# engine restores the same snapshot, replays the same commands and must match every projection.
//
//   node Tools/oracle-combat.mjs [--source D:/repos/AshenSpire] [--combats 60] [--steps 80] [--swaps 48] [--swap-steps 60] [--coverage]
//
// Writes Unity/Assets/Tests/Oracle/combat/*.json (committed; CI has no access to the old repo).
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const SOURCE = arg('--source', process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const COMBATS = Number(arg('--combats', '60'));
const SWAPS = Number(arg('--swaps', '48'));
const SWAP_STEPS = Number(arg('--swap-steps', '60'));
const STEPS = Number(arg('--steps', '80'));
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'combat');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng, sweepSeed } = await load('src/engine/rng.js');
const { createRunState } = await load('src/model/state.js');
const { createCombat, dispatch, previewCard, previewIntent } = await load('src/engine/combat.js');
const { serializeCombatSnapshot, restoreCombatSnapshot } = await load('src/engine/combatSnapshot.js');
const { resolveHandRules } = await load('src/model/handRules.js');
const { resolveSwapCostRule, runMods, addToStorage, equipPiece, ownership, swapCostFor, canSwap, canEquip } = await load('src/model/loadout.js');
const { passiveSum } = await load('src/model/registries.js');
const { propertyMountsOf } = await load('src/engine/properties.js');


// --coverage: V8 precise coverage (node:inspector) over the whole run, printed as the shipped functions executed per
// file — the port surface of this oracle (as in oracle-run.mjs).
let coverage = null;
if (process.argv.includes('--coverage')) {
  const { Session } = await import('node:inspector/promises');
  coverage = new Session();
  coverage.connect();
  await coverage.post('Profiler.enable');
  await coverage.post('Profiler.startPreciseCoverage', { callCount: true, detailed: true });
  await coverage.post('Profiler.takePreciseCoverage');
}

const bundle = configuredContentBundle(contentBundle, {});
const registries = createRegistries(bundle);
const classes = bundle.classes.map(c => c.id);
const encounters = bundle.encounters.filter(e => e.pool === 'normal' || e.pool === 'elite' || e.pool === 'boss');

// Deterministic bot choices use their own LCG — never the engine RNG, so the bot cannot perturb the streams.
function botRng(seed) { let s = seed >>> 0 || 1; return (n) => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s % n; }; }

function projection(combat, rng) {
  const ent = (e) => ({
    id: e.id ?? e.instanceId, hp: e.hp, maxHp: e.maxHp, block: e.block, alive: e.alive !== false,
    statuses: Object.fromEntries(Object.entries(e.statuses || {}).map(([k, v]) => [k, typeof v === 'object' ? (v.stacks ?? v.value ?? v) : v]).sort()),
    ...(e.intent ? { intent: { moveId: e.intent.moveId ?? null, kind: e.intent.kind ?? e.intent.type ?? null, amount: e.intent.amount ?? e.intent.damage ?? null } } : {}),
  });
  const p = combat.player;
  return {
    turn: combat.turn, phase: combat.phase, result: combat.result,
    player: { ...ent(p), energy: p.energy, mana: p.mana, stamina: p.stamina },
    enemies: combat.enemies.map(ent),
    hand: combat.piles.hand.map(c => c.instanceId),
    draw: combat.piles.draw.length, discard: combat.piles.discard.length, exhaust: combat.piles.exhaust.length,
    rng: rng.getCounters(),
    skillXp: structuredClone(combat.skillXp?.player ?? null),
  };
}

// What the UI is shown: previewCard for every card in hand and previewIntent for every living enemy — the SAME
// math the engine executes (SPEC §3.13). Recorded after every step so preview/resolve divergence is caught.
function previews(combat) {
  const cards = combat.piles.hand.map((c) => {
    const p = previewCard(combat, c.instanceId);
    return {
      id: c.instanceId, cost: p.cost, costIsX: p.costIsX, needsTarget: p.needsTarget, manaCost: p.manaCost, staminaCost: p.staminaCost,
      values: p.values.map((v) => ({
        op: v.op, target: v.target ?? null, value: v.value ?? null,
        ...(v.hits != null ? { hits: v.hits } : {}), ...(v.perTarget ? { perTarget: v.perTarget } : {}),
        ...(v.status ? { status: v.status } : {}), ...(v.token ? { token: v.token } : {}), ...(v.boostTint ? { boostTint: v.boostTint } : {}),
      })),
      tokens: p.tokens,
    };
  });
  const intents = combat.enemies.filter((e) => e.alive).map((e) => {
    const i = previewIntent(combat, e.id);
    return { id: e.id, kind: i.kind ?? null, moveId: i.moveId ?? null, damage: i.damage ?? null, hits: i.hits ?? null, totalDamage: i.totalDamage ?? null, block: i.block ?? null, pending: !!i.pending };
  });
  return { cards, intents };
}

function cloneCombat(combat, seed) {
  const rng = createRng(seed, combat.rng.getCounters());
  return restoreCombatSnapshot({ registries, rng, snapshot: serializeCombatSnapshot(combat) });
}

function legalCommands(combat, seed) {
  const out = [];
  const targets = combat.enemies.filter(e => e.alive !== false).map(e => e.instanceId ?? e.id);
  for (const card of combat.piles.hand) {
    for (const targetId of [undefined, ...targets]) {
      const cmd = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
      try { dispatch(cloneCombat(combat, seed), cmd); out.push(cmd); break; } catch { /* illegal: try the next target */ }
    }
  }
  out.push({ type: 'endTurn' });
  return out;
}

// Remove only the previous JSON outputs: the Unity .meta files beside them are committed and must survive.
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const index = [];
let totalSteps = 0, victories = 0, defeats = 0;
for (let i = 0; i < COMBATS; i++) {
  const seed = sweepSeed(1000 + i);
  const classId = classes[i % classes.length];
  const encounter = encounters[i % encounters.length];
  const rng = createRng(seed);
  const run = createRunState({ seed, classId, registries });
  const handRules = resolveHandRules({}, bundle.attributes);
  const createArgs = {
    ratingsRules: registries.balance.combatRatings || null, handRules,
    player: {
      classId: run.class, attributes: run.attributes, derivedStatRuleSnapshot: run.derivedStatRuleSnapshot, skills: run.skills,
      coreTags: run.coreTags, maxHp: run.maxHp, hp: run.hp, maxMana: run.maxMana, mana: run.mana, maxStamina: run.maxStamina,
      stamina: run.stamina, energyMax: run.energyMax, drawPerTurn: run.drawPerTurn, damageBySchoolAdd: run.damageBySchoolAdd,
      equipmentProfileRuleSnapshot: run.equipmentProfileRuleSnapshot, equipmentAttackSlotCount: run.equipmentAttackSlotCount,
      removedAttackSlotIds: run.removedAttackSlotIds, equipmentPoolDeficits: run.equipmentPoolDeficits,
      itemUpgradeLevels: run.itemUpgradeLevels, itemMounts: run.itemMounts, deck: run.deck, relicIds: run.relics,
      flasks: run.flasks, flaskCharges: run.flaskCharges, loadout: run.loadout,
    },
    enemyIds: encounter.enemies, hpMult: 1, enemyStatuses: [],
    swapCostRule: resolveSwapCostRule(registries, null),
    playerStatuses: [...runMods(registries, run.loadout, run.class).startStatuses],
  };
  // The exact createCombat inputs (JSON-plain, captured before the call), so the C# CreateCombat is held to the
  // shipped combat-start sequence: HP rolls, shuffle, innate order, combatStart triggers, first intents, turn 1.
  const create = JSON.parse(JSON.stringify(createArgs));
  const combat = createCombat({ ...createArgs, registries, rng });
  const initial = serializeCombatSnapshot(combat);
  const initialRng = rng.getCounters();
  const pick = botRng(seed);
  const steps = [];
  const start = { ...projection(combat, rng), previews: previews(combat) };
  for (let s = 0; s < STEPS && !combat.result; s++) {
    const legal = legalCommands(combat, seed);
    // Prefer playing cards (80%) so fights progress; endTurn is always legal.
    const cards = legal.filter(c => c.type === 'playCard');
    const cmd = cards.length && pick(10) < 8 ? cards[pick(cards.length)] : { type: 'endTurn' };
    let error = null;
    try { dispatch(combat, cmd); } catch (e) { error = e.message; }
    const after = projection(combat, rng);
    steps.push({ command: cmd, ...(error ? { error } : {}), after: combat.result ? after : { ...after, previews: previews(combat) } });
  }
  totalSteps += steps.length;
  if (combat.result === 'victory') victories++;
  if (combat.result === 'defeat') defeats++;
  const name = `combat-${String(i).padStart(3, '0')}.json`;
  writeFileSync(join(OUT, name), `${JSON.stringify({ seed, classId, encounterId: encounter.id, create, rngCounters: initialRng, snapshot: initial, start, steps, result: combat.result })}\n`);
  index.push({ file: name, seed, classId, encounterId: encounter.id, steps: steps.length, result: combat.result });
}
// The registry tables these combats ran against, in the SHIPPED insertion order (D-040), so the C# parity test
// exercises the engine alone; a separate test holds the content-built registries to the same tables. Since us-5.11 the
// dump also carries the run-creation tables (RunData): the mid-fight equipment doors restamp the deck through the
// run's composition port (Ashen.Domain.Run), so the swap cases below replay on RunData built from this same file.
const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x)));
const REGISTRY_TABLES = ['attributes', 'cards', 'relics', 'statuses', 'stances', 'keywords', 'enemies', 'flasks', 'classes', 'propertyRules'];
const RUN_TABLES = ['creationModes', 'seats', 'encounters'];
const RUN_OBJECTS = ['attributeRules', 'characterCreation', 'derivedStatRules', 'tagFamilies', 'contentVersion'];
function dumpOf(reg) {
  const out = Object.fromEntries(REGISTRY_TABLES.map((t) => [t, plain(reg[t].all())]));
  for (const t of ['classTree', 'equipment', 'balance']) out[t] = plain(reg[t]);
  for (const t of RUN_TABLES) out[t] = plain(reg[t].all());
  for (const t of RUN_OBJECTS) out[t] = plain(reg[t]);
  return out;
}
const dump = dumpOf(registries);
writeFileSync(join(OUT, 'registries.json'), `${JSON.stringify(dump)}
`);

// ---------------------------------------------------------------------------------------------------------------
// Mid-fight equipment (us-5.11): doSwapArmament / doChangeEquipment, their prices (swapCostFor) and refusals, the
// unrated Poise vessel (combat ratings off) and createCombat's fallbacks (no profile snapshot, no swap-cost rule).
// Each case starts a run of one class and kit, carries extra armaments in the Armoury (storage, and the second and
// third set cells, placed through the shipped equipPiece outside combat), then plays a seeded bot that also swaps
// sets and re-arms positions. Recorded per case: the createCombat inputs and the start snapshot (as above), every
// step's projection and previews, and for each equipment step the events it emitted and the equipment projection
// (loadout, full piles, player entity, pool deficits, flags). The equipment options — every set of the hand slots, the armour and
// the talisman, and every carried piece for every hand position (plus unequipping the armour) — are recorded with the shipped
// refusal (dispatch on a restored clone) and the price the engine would charge, at the start, after every
// equipment step and after the first End Turn. Configuration variants are shipped Settings (combat ratings off,
// swaps end the turn, changes locked, equipment off) plus one content edit (swapCostKind 'allowance', which no
// Setting chooses); each variant's registry overlay holds only the top-level keys that differ from registries.json.
const VARIANTS = {
  shipped: { settings: {} },
  unrated: { settings: { 'gameConfig.combatRatings.enabled': false } },
  endsTurn: { settings: { 'gameConfig.balance.equipment.swapEndsTurn': true, 'gameConfig.balance.equipment.swapCost': 1 } },
  locked: { settings: { 'gameConfig.balance.equipment.allowChangesInCombat': false } },
  disabled: { settings: { 'gameConfig.balance.equipment.enabled': false } },
  allowance: { settings: {}, edit: (b) => { b.balance.equipment.swapCostKind = 'allowance'; } },
};
const variantRegs = {};
for (const [name, v] of Object.entries(VARIANTS)) {
  if (name === 'shipped') { variantRegs[name] = registries; continue; }
  const b = configuredContentBundle(contentBundle, v.settings);
  if (v.edit) v.edit(b);
  const reg = createRegistries(b);
  variantRegs[name] = reg;
  const d = dumpOf(reg);
  const overlay = {};
  for (const key of Object.keys(d)) if (JSON.stringify(d[key]) !== JSON.stringify(dump[key])) overlay[key] = d[key];
  if (JSON.stringify(Object.keys(d)) !== JSON.stringify(Object.keys(dump))) throw new Error(`${name}: dump keys differ`);
  writeFileSync(join(OUT, `registries-${name}.json`), `${JSON.stringify({ variant: name, settings: v.settings, contentEdit: v.edit ? v.edit.toString() : null, overlay })}\n`);
}

function cloneWith(reg, combat, seed) {
  const rng = createRng(seed, combat.rng.getCounters());
  return restoreCombatSnapshot({ registries: reg, rng, snapshot: serializeCombatSnapshot(combat) });
}

function refusalOf(reg, combat, seed, cmd) {
  try { dispatch(cloneWith(reg, combat, seed), cmd); return null; } catch (e) { return e.message; }
}

// Every equipment intent worth asking about: each set of each slot (swap), and for each hand position the empty
// hand plus every carried piece (change), plus unequipping the worn armour. Refused ones are recorded too.
function equipmentOptions(reg, combat) {
  const out = [];
  const loadout = combat.loadout;
  if (!loadout) return out;
  const slots = reg.equipment.slots || [];
  // Swaps: the hand slots, plus the armour and the talisman (the fastened refusal and a slot with several sets).
  for (const slot of slots) {
    if (!(slot.hand === 'left' || slot.hand === 'right' || slot.id === 'armor' || slot.id === 'talisman')) continue;
    const ids = (loadout.sets || {})[slot.id] || [];
    for (let i = 0; i < ids.length; i++) out.push({ type: 'swapArmament', slotId: slot.id, setIndex: i });
  }
  const hands = slots.filter((s) => s.hand === 'left' || s.hand === 'right');
  const pieces = [null];
  for (const id of loadout.storage || []) if (!pieces.includes(id)) pieces.push(id);
  for (const slot of hands) for (const id of (loadout.sets || {})[slot.id] || []) if (id && !pieces.includes(id)) pieces.push(id);
  for (const slot of hands) {
    const ids = (loadout.sets || {})[slot.id] || [];
    for (let i = 0; i < ids.length; i++) for (const pieceId of pieces) out.push({ type: 'changeEquipment', slotId: slot.id, setIndex: i, pieceId });
  }
  if ((loadout.sets || {}).armor) out.push({ type: 'changeEquipment', slotId: 'armor', setIndex: 0, pieceId: null });
  return out;
}

// The price the engine would charge for an intent — null where it refuses before pricing (the order of
// doSwapArmament / doChangeEquipment: phase, equipment on, a loadout, canSwap / canEquip + the preview equip).
function priceOf(reg, combat, cmd) {
  if (combat.result || combat.phase !== 'player') return null;
  const cfg = reg.balance.equipment || {};
  if (!cfg.enabled || !combat.loadout) return null;
  const p = combat.player;
  const relicDelta = passiveSum(reg, p.relicIds, 'swapCostDelta', {}, propertyMountsOf(combat, p));
  const base = { rule: combat.swapCostRule, classId: p.classId, slotId: cmd.slotId, setIndex: cmd.setIndex, relicDelta };
  if (cmd.type === 'swapArmament') {
    if (!canSwap(reg, cmd.slotId, { inCombat: true }).ok) return null;
    return swapCostFor(reg, { ...base, loadout: combat.loadout });
  }
  if (!canEquip(reg, cmd.slotId, { inCombat: true }).ok) return null;
  const owned = ownership(reg, { meta: {}, loadout: combat.loadout });
  const preview = structuredClone(combat.loadout);
  const ctx = { inCombat: true, attributes: combat.attributes, itemUpgradeLevels: combat.itemUpgradeLevels, armamentLevels: combat.armamentLevels, classId: p.classId };
  if (!equipPiece(reg, preview, cmd.slotId, cmd.setIndex, cmd.pieceId, owned, ctx)) return null;
  return swapCostFor(reg, { ...base, loadout: preview });
}

function optionRecords(reg, combat, seed) {
  return equipmentOptions(reg, combat).map((command) => {
    const error = refusalOf(reg, combat, seed, command);
    const price = priceOf(reg, combat, command);
    return { command, error, ...(price ? { price } : {}) };
  });
}

function equipProjection(combat) {
  return plain({
    loadout: combat.loadout,
    piles: combat.piles,
    player: combat.player,
    equipmentPoolDeficits: combat.equipmentPoolDeficits,
    equipmentChanged: combat.equipmentChanged,
    swapsLeft: combat.swapsLeft,
  });
}

// The armaments a case carries besides its kit: a deterministic draw over the table, so every class meets pool
// mods (towerShield, twinblade, blightRod, duskChime), category prices (heavy, flourish), shields, staves and
// requirement gates.
const ARMAMENT_IDS = registries.equipment.armaments.map((a) => a.id);
const upgradeRefs = new Set((registries.equipment.itemUpgradeChanges || []).filter((r) => r.nextTier === 1).map((r) => r.itemRef));
const SWAP_VARIANTS = ['shipped', 'shipped', 'unrated', 'shipped', 'endsTurn', 'shipped', 'unrated', 'locked', 'shipped', 'allowance', 'shipped', 'disabled'];
const RULES = ['flat', 'gear', 'category'];
const swapIndex = [];
let swapSteps = 0, swapEquipSteps = 0, swapOptions = 0;
for (let i = 0; i < SWAPS; i++) {
  const variant = SWAP_VARIANTS[i % SWAP_VARIANTS.length];
  const reg = variantRegs[variant];
  const seed = sweepSeed(5000 + i);
  const classId = classes[i % classes.length];
  const kits = reg.equipment.startingKits.filter((k) => k.classId === classId);
  const kit = kits[Math.floor(i / classes.length) % kits.length];
  const pick = botRng(seed);
  const encounterPool = reg.encounters.all().filter((e) => e.pool === 'normal' || e.pool === 'elite' || e.pool === 'boss');
  const encounter = encounterPool[(i * 7) % encounterPool.length];
  const options = { attributeMode: i % 5 === 3 ? 'pointbuy' : undefined, startingKitId: kit.id, profileMeta: { discoveredArmaments: [kit.rightHand, kit.leftHand].filter(Boolean) } };
  const run = createRunState({ seed, classId, registries: reg, ...options });
  // Carry 3–5 more armaments; place some into the second and third set cells (the Armoury, outside combat).
  const carried = [];
  const want = 3 + pick(3);
  while (carried.length < want) {
    const id = ARMAMENT_IDS[pick(ARMAMENT_IDS.length)];
    if (!carried.includes(id) && id !== kit.rightHand && id !== kit.leftHand) carried.push(id);
  }
  const cap = reg.balance.equipment.storageSlots;
  for (const id of carried) addToStorage(run.loadout, id, cap);
  const owned = ownership(reg, { meta: {}, loadout: run.loadout });
  const outside = { inCombat: false, attributes: run.attributes, itemUpgradeLevels: run.itemUpgradeLevels, classId };
  const placements = [['rightHand', 1], ['leftHand', 1], ['rightHand', 2]];
  for (let k = 0; k < placements.length && k < carried.length; k++) {
    if (pick(4) === 0) continue;
    equipPiece(reg, run.loadout, placements[k][0], placements[k][1], carried[k], owned, outside);
  }
  // Smithing tiers on some carried pieces, so a restamp reads a non-zero smithingLevel.
  if (i % 3 === 1) for (const id of carried.slice(0, 2)) if (upgradeRefs.has(`armament/${id}`)) run.itemUpgradeLevels[`armament/${id}`] = 1;
  const rule = RULES[i % RULES.length];
  const createArgs = {
    ratingsRules: reg.balance.combatRatings || null, handRules: resolveHandRules({}, bundle.attributes),
    player: {
      classId: run.class, attributes: run.attributes, derivedStatRuleSnapshot: run.derivedStatRuleSnapshot, skills: run.skills,
      coreTags: run.coreTags, maxHp: run.maxHp, hp: run.hp, maxMana: run.maxMana, mana: run.mana, maxStamina: run.maxStamina,
      stamina: run.stamina, energyMax: run.energyMax, drawPerTurn: run.drawPerTurn, damageBySchoolAdd: run.damageBySchoolAdd,
      equipmentProfileRuleSnapshot: run.equipmentProfileRuleSnapshot, equipmentAttackSlotCount: run.equipmentAttackSlotCount,
      removedAttackSlotIds: run.removedAttackSlotIds, equipmentPoolDeficits: run.equipmentPoolDeficits,
      itemUpgradeLevels: run.itemUpgradeLevels, itemMounts: run.itemMounts, deck: run.deck, relicIds: run.relics,
      flasks: run.flasks, flaskCharges: run.flaskCharges, loadout: run.loadout,
    },
    enemyIds: encounter.enemies, hpMult: 1, enemyStatuses: [],
    swapCostRule: resolveSwapCostRule(reg, { settings: { swapCostRule: rule } }),
    playerStatuses: [...runMods(reg, run.loadout, run.class).startStatuses],
  };
  // createCombat's own fallbacks: a player without the run's profile snapshot (createEquipmentProfileRuleSnapshot)
  // and a fight without a resolved swap-cost rule (resolveSwapCostRule(registries, null)).
  if (i % 8 === 5) delete createArgs.player.equipmentProfileRuleSnapshot;
  if (i % 8 === 6) createArgs.swapCostRule = null;
  const create = JSON.parse(JSON.stringify(createArgs));
  const rng = createRng(seed);
  const combat = createCombat({ ...createArgs, registries: reg, rng });
  const initial = serializeCombatSnapshot(combat);
  const initialRng = rng.getCounters();
  const start = { ...projection(combat, rng), previews: previews(combat) };
  const steps = [];
  let prevType = null;
  let turnRecorded = false;
  for (let s = 0; s < SWAP_STEPS && !combat.result; s++) {
    const records = optionRecords(reg, combat, seed);
    // Recorded at the start, after every equipment step and after the first End Turn (a fresh turn's energy).
    const equipStep = prevType === 'swapArmament' || prevType === 'changeEquipment';
    if (s === 0 || equipStep || (prevType === 'endTurn' && !turnRecorded)) {
      if (prevType === 'endTurn') turnRecorded = true;
      (s === 0 ? start : steps[s - 1]).options = records;
      swapOptions += records.length;
    }
    const equip = records.filter((r) => r.error === null).map((r) => r.command);
    const cards = [];
    const targets = combat.enemies.filter((e) => e.alive !== false).map((e) => e.instanceId ?? e.id);
    for (const card of combat.piles.hand) {
      for (const targetId of [undefined, ...targets]) {
        const cmd = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
        if (refusalOf(reg, combat, seed, cmd) === null) { cards.push(cmd); break; }
      }
    }
    const r = pick(20);
    const cmd = equip.length && r < 5 ? equip[pick(equip.length)]
      : cards.length && r < 17 ? cards[pick(cards.length)] : { type: 'endTurn' };
    let error = null;
    let events = null;
    try { events = dispatch(combat, cmd).events; } catch (e) { error = e.message; }
    const after = projection(combat, rng);
    const step = { command: cmd, ...(error ? { error } : {}), after: combat.result ? after : { ...after, previews: previews(combat) } };
    if (cmd.type === 'swapArmament' || cmd.type === 'changeEquipment') {
      step.events = plain(events);
      step.equip = equipProjection(combat);
      swapEquipSteps++;
    }
    steps.push(step);
    prevType = cmd.type;
  }
  swapSteps += steps.length;
  // The final committed state, without the event log (each equipment step's events are recorded above).
  const { eventLog, ...final } = plain(serializeCombatSnapshot(combat));
  const name = `swap-${String(i).padStart(3, '0')}.json`;
  writeFileSync(join(OUT, name), `${JSON.stringify({ seed, variant, classId, kitId: kit.id, rule, encounterId: encounter.id, create, rngCounters: initialRng, snapshot: initial, start, steps, final, result: combat.result })}\n`);
  swapIndex.push({ file: name, seed, variant, classId, kitId: kit.id, rule, encounterId: encounter.id, steps: steps.length, equipSteps: steps.filter((x) => x.equip).length, result: combat.result });
}

if (coverage) {
  const { result } = await coverage.post('Profiler.takePreciseCoverage');
  for (const script of result.filter((sc) => sc.url.includes('/src/')).sort((a, b) => (a.url < b.url ? -1 : 1))) {
    const names = script.functions.filter((f) => f.functionName && f.ranges[0].count > 0).map((f) => f.functionName);
    if (names.length) console.log(`${script.url.split('/src/')[1]}: ${[...new Set(names)].join(', ')}`);
  }
}
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify({ source: 'engine/combat.js createCombat + dispatch (shipped preset)', combats: index, swaps: swapIndex }, null, 1)}\n`);
console.log(`oracle-combat: ${COMBATS} combats, ${totalSteps} steps, ${victories} victories, ${defeats} defeats; ${SWAPS} equipment combats, ${swapSteps} steps (${swapEquipSteps} equipment), ${swapOptions} recorded options → Unity/Assets/Tests/Oracle/combat`);
