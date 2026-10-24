#!/usr/bin/env node
// Core 2 stances and card costs oracle (WP-5, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED web at core2.webSha for the
// configured `default` profile and records three things.
//
//   grid.json     The unit grid: stanceStaminaCost / stanceManaCost / stanceEffectiveness / stanceEntryBonuses / sameStance / spellChain
//                 for every profile card (211) against every stance a fight can hold (the maneuvers x camps, mirrored Spells, none,
//                 two concurrent-stance states, a state with the Evade-gating status on), in the player's and the enemy's phase,
//                 plus the same functions for an enemy entity with and without a Mana price.
//   prices.json   The price derivation (model/actionCosts.js): cardCostBudget and applyActionCosts for all 211 profile cards and the
//                 138 enemy cards at ranks 0..3, as authored (every profile card is `costRule: fixed`, so none changes) and forced
//                 through the derivation; enemyPoolOf for every enemy.
//   fights-<class>.json   Fights (4 classes x encounters x seeds): the opening hand priced (cardPlayCosts, resolvedCardPlayCosts,
//                 the cost part of previewCard, the legality of a play with the Stamina and Mana one short and exactly enough), the
//                 same for a hand of every profile card under state variants (stance, statuses, phase, resources, cost growth), and
//                 chains of plays (the front half of doPlayCard: pay, leave the hand, bump the counters, enter the stance) with the
//                 costs of the whole hand after every play.
//
//   node Tools/oracle-core2-costs.mjs [--seeds 2] [--encounters 6] [--chains 3] [--chain-length 5] [--classes ...] [--source <web checkout>]
//
// Deterministic: seeds come from sweepSeed, the bot from botLcg. Writes Unity/Assets/Tests/Oracle/core2/costs/*.json (sorted keys).
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, detach, botLcg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'costs');
const SEEDS = Number(arg('--seeds', '2'));
const ENCOUNTERS = Number(arg('--encounters', '6'));
const CHAINS = Number(arg('--chains', '2'));
const CHAIN_LENGTH = Number(arg('--chain-length', '4'));
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [stanceRules, actionCosts, expansion, gameProfileCombat, tactics, tree, stanceHit] = await Promise.all([
  load('src/model/stanceRules.js'), load('src/model/actionCosts.js'), load('src/engine/combatExpansion.js'), load('src/engine/gameProfileCombat.js'),
  load('src/engine/combatCardTactics.js'), load('src/model/tree.js'), load('src/engine/stanceHit.js'),
]);
const classes = arg('--classes', classIds(w).join(',')).split(',');
const all = encounters(w);
const profileCards = w.gp.gameContent(w.profile, 'cards');
const enemyCards = w.gp.gameContent(w.profile, 'enemyCards');
const json = (v) => `${JSON.stringify(canon(v))}\n`;

const guarded = (fn) => { try { return fn(); } catch (e) { return { error: e.message }; } };
const clone = (v) => (v === undefined ? undefined : structuredClone(v));

// ---------------------------------------------------------------------------------------------------------------
// A prepared fight: the opening state plus the setups the scenarios lay over it. A setup is plain data, so the C# test applies the
// very same fields to its own opened fight.
//   hand: 'opening' | 'all'  (all = one instance of every profile card, ids x1..xN)
//   stance / concurrent: the player's combatStance record and concurrentStances list
//   statuses: { id: stacks }
//   phase, stamina, mana, riders (cardUsesCombat / instanceUsesCombat counts)
// ---------------------------------------------------------------------------------------------------------------
const allInstances = () => profileCards.map((c, i) => ({ instanceId: `x${i + 1}`, cardId: c.id, upgraded: false }));

function applySetup(combat, setup) {
  const p = combat.player;
  if (setup.hand === 'all') combat.piles.hand = allInstances();
  if (Object.hasOwn(setup, 'stance')) { if (setup.stance) p.combatStance = clone(setup.stance); else delete p.combatStance; }
  if (setup.concurrent) p.concurrentStances = clone(setup.concurrent);
  for (const [id, stacks] of Object.entries(setup.statuses || {})) p.statuses[id] = { stacks };
  if (setup.phase) combat.phase = setup.phase;
  if (setup.stamina !== undefined) p.energy = setup.stamina;
  if (setup.mana !== undefined) p.mana = setup.mana;
  if (setup.riders) {
    p.abilityRiders = p.abilityRiders || {};
    for (const [key, value] of Object.entries(setup.riders)) p.abilityRiders[key] = clone(value);
  }
  return combat;
}
const prepare = (combat, setup) => applySetup(detach(w, combat), setup);

// The player's resource and stance state, as the scenarios record it.
const playerState = (combat) => {
  const p = combat.player;
  return canon({ stamina: p.energy, mana: p.mana, combatStance: p.combatStance ?? null, concurrentStances: p.concurrentStances ?? null,
    matchingStanceCardMultiplier: p.matchingStanceCardMultiplier ?? null,
    precision: p.statuses.precision?.stacks ?? 0, evade: p.statuses.evade?.stacks ?? 0, counters: p.counters, handSize: combat.piles.hand.length });
};

// ---------------------------------------------------------------------------------------------------------------
// The cost reads of one card
// ---------------------------------------------------------------------------------------------------------------
const COST_REFUSALS = ['Not enough Stamina or Mana', 'Not enough Actions (Stamina) to play this card', 'Not enough mana to play this card'];

function costsOf(combat, instanceId) {
  const inst = combat.piles.hand.find((c) => c.instanceId === instanceId);
  return guarded(() => {
    const def = expansion.resolveCombatCard(combat, inst);
    // [cardPlayCosts energy, mana, stamina, resolvedCardPlayCosts energy, mana, stamina, cost is X]: one row per card keeps the files small.
    const paid = w.combat.cardPlayCosts(combat, instanceId);
    const resolved = w.combat.resolvedCardPlayCosts(combat, def);
    return [paid.energy, paid.mana, paid.stamina, resolved.energy, resolved.mana, resolved.stamina, def.cost === 'X' ? 1 : 0];
  });
}

function previewCosts(combat, instanceId, targetId) {
  return guarded(() => {
    const p = w.combat.previewCard(combat, instanceId, targetId);
    const advance = p.range?.routes?.find((r) => r.id === 'advance');
    return { cost: p.cost, advanceSp: advance?.advanceSp ?? 0, costIsX: p.costIsX, manaCost: p.manaCost, staminaCost: p.staminaCost,
      costShift: p.costShift ?? null, displayCosts: p.displayCosts ?? null, entryPrecision: p.entryPrecision ?? null, needsTarget: p.needsTarget };
  });
}

// The legality of playing a card with the Stamina and Mana one short and exactly enough. The target is what the live play needs.
function dispatchOutcome(combat, instanceId) {
  const aimed = guarded(() => w.combat.cardNeedsEnemyTargetNow(combat, instanceId));
  const target = aimed === true ? combat.enemies.find((e) => e.alive)?.id : undefined;
  const copy = detach(w, combat);
  try { w.combat.dispatch(copy, { type: 'playCard', cardInstanceId: instanceId, ...(target ? { targetId: target } : {}) }); return { ok: true }; } catch (e) { return { ok: false, error: e.message }; }
}

function legality(combat, instanceId, costs) {
  if (!Array.isArray(costs)) return null;
  const paid = { energy: costs[0], mana: costs[1], stamina: costs[2] };
  const out = {};
  const run = (name, energy, mana) => {
    const copy = detach(w, combat);
    copy.player.energy = energy;
    copy.player.mana = mana;
    out[name] = dispatchOutcome(copy, instanceId);
  };
  run('exact', paid.energy, paid.mana);
  if (paid.energy > 0) run('shortStamina', paid.energy - 1, paid.mana);
  if (paid.mana > 0) run('shortMana', paid.energy, paid.mana - 1);
  return out;
}

// One card, all its reads. `deep` adds the previews and the legality (dispatches on detached copies).
function cardRow(combat, instanceId, deep, targetId) {
  const costs = costsOf(combat, instanceId);
  return { costs, ...(deep ? { preview: previewCosts(combat, instanceId, targetId), legality: legality(combat, instanceId, costs) } : {}) };
}

// ---------------------------------------------------------------------------------------------------------------
// The front half of doPlayCard: what a card play does before its effects run (combat.js doPlayCard): pay the price, leave the hand,
// bump the counters, build the carrier the stance reads and enter the stance (gameProfileCombat.js setProfileStance).
// ---------------------------------------------------------------------------------------------------------------
function carrierOf(combat, inst, def) {
  const p = combat.player;
  const kind = tree.cardKind(def);
  const ref = {
    abilityKind: def.abilityKind || ((def.cardTags || def.tags || []).includes('source:spell') ? 'spell' : 'maneuver'), abilityFamily: def.abilityFamily, abilityRank: def.abilityRank,
    instanceId: inst.instanceId, cardId: inst.cardId, upgraded: inst.upgraded,
    type: kind, tags: def.cardTags ?? (def.tags?.length ? def.tags : undefined), attack: def.attack,
    authoredTags: def.cardTags ?? (def.tags?.length ? def.tags : []),
    damageSchool: inst.damageSchool ?? def.damageSchool,
    exposureBuildupPerHit: inst.exposureBuildupPerHit ?? def.exposureBuildupPerHit,
  };
  Object.assign(ref, tactics.tacticalCarrier(def, ref, combat, p));
  return { ref, kind };
}

function enterByPlay(combat, instanceId) {
  const p = combat.player;
  const idx = combat.piles.hand.findIndex((c) => c.instanceId === instanceId);
  if (idx < 0) return { error: 'not in hand' };
  const inst = combat.piles.hand[idx];
  const def = expansion.resolveCombatCard(combat, inst);
  const paid = w.combat.cardPlayCosts(combat, instanceId);
  if (p.energy < paid.energy || p.mana < paid.mana) return { error: 'cost', paid };
  const events = [];
  const emit = combat.emit;
  combat.emit = (type, payload) => { events.push(type === 'combatStanceChanged' ? { type, maneuver: payload.maneuver } : type); return emit(type, payload); };
  // doPlayCard pays: Stamina (the Actions pool), Mana, then the spent receipts.
  p.energy -= paid.energy;
  if (paid.energy > 0 || def.cost === 'X') combat.emit('energySpent', { amount: paid.energy });
  p.mana -= paid.mana;
  if (paid.mana > 0) combat.emit('manaSpent', { amount: paid.mana });
  if (paid.stamina > 0) {
    p.counters.staminaSpentThisTurn = (p.counters.staminaSpentThisTurn || 0) + paid.stamina;
    combat.emit('staminaSpent', { amount: paid.stamina });
  }
  combat.piles.hand.splice(idx, 1);
  p.counters.cardsPlayedThisTurn += 1;
  p.counters.cardsPlayedThisCombat += 1;
  const { ref, kind } = carrierOf(combat, inst, def);
  if (kind === 'attack') p.counters.attacksPlayedThisCombat += 1;
  gameProfileCombat.setProfileStance(combat, p, ref);
  combat.emit = emit;
  const keep = ['type', 'tags', 'authoredTags', 'abilityKind', 'stanceTrigger', 'mirrors', 'evade', 'range', 'appliedStatuses', 'appliedStatusEffects', 'appliedBuildupStatuses',
    'fixCoreSameStance', 'traits', 'counterCoverage', 'targeting', 'projectile'];
  const profile = { ...ref.combatProfile };
  delete profile.damageType;
  delete profile.damageComponents;
  return { paid, cardId: inst.cardId, carrier: Object.fromEntries(keep.filter((k) => ref[k] !== undefined).map((k) => [k, ref[k]])), carrierProfile: profile, events };
}

// ---------------------------------------------------------------------------------------------------------------
// The unit grid
// ---------------------------------------------------------------------------------------------------------------
const STANCES = [
  { name: 'none', stance: null },
  ...['attack', 'smash', 'defend', 'sweep', 'counter', 'prepared', 'evade', 'neutral', 'staggered'].map((m) => ({ name: `physical-${m}`, stance: { maneuver: m, camp: 'physical', range: 'melee' } })),
  ...['attack', 'smash', 'defend', 'sweep', 'counter', 'prepared'].map((m) => ({ name: `spell-${m}`, stance: { maneuver: m, camp: 'spell', range: 'melee' } })),
  ...['attack', 'defend', 'smash', 'counter'].map((m) => ({ name: `mirrored-${m}`, stance: { maneuver: m, camp: 'spell', range: 'melee', mirrored: true } })),
  { name: 'ranged-attack', stance: { maneuver: 'attack', camp: 'physical', range: 'ranged' } },
  { name: 'concurrent-attack-prepared', stance: { maneuver: 'counter', camp: 'physical', range: 'melee' }, concurrent: [{ maneuver: 'attack', camp: 'physical', range: 'melee' }, { maneuver: 'prepared', camp: 'spell', range: 'melee' }], statuses: { 'base.many-forms': 1 } },
  { name: 'concurrent-mirrored', stance: { maneuver: 'defend', camp: 'physical', range: 'melee' }, concurrent: [{ maneuver: 'attack', camp: 'spell', range: 'melee', mirrored: true }], statuses: { 'base.many-forms': 1 } },
];
const evadeGates = [...new Set(profileCards.map((c) => c.evade?.whileStatus).filter(Boolean))];
const GRID_BASES = [0, 1, 2, 3];
const GRID_VARIANTS = [{ name: 'plain', statuses: {} }, ...(evadeGates.length ? [{ name: 'evadeGate', statuses: Object.fromEntries(evadeGates.map((s) => [s, 1])) }] : [])];

// D-214: the stance entry's Precision grant counts the fight's characterLevel for the player (the player entity carries no `level`).
const LEVELS = [0, 1, 4, 9];
function levelRows(combat) {
  const out = {};
  for (const level of LEVELS) {
    const state = prepare(combat, {});
    state.characterLevel = level;
    delete state.player.level;
    out[level] = Object.fromEntries(profileCards.map((card) => {
      const inst = { instanceId: 'g', cardId: card.id, upgraded: false };
      return [card.id, guarded(() => {
        const def = expansion.resolveCombatCard(state, inst);
        const entry = stanceRules.stanceEntryBonuses(def, state);
        const { ref } = carrierOf(state, inst, def);
        const copy = detach(w, state);
        copy.characterLevel = level;
        delete copy.player.level;
        const { ref: copyRef } = carrierOf(copy, inst, def);
        gameProfileCombat.setProfileStance(copy, copy.player, copyRef);
        return { entry, grant: stanceHit.precisionEntryGrant(state, state.player, entry.precision, ref), mix: stanceHit.precisionStatMix(state, state.player, ref),
          precision: copy.player.statuses.precision?.stacks ?? 0, evade: copy.player.statuses.evade?.stacks ?? 0 };
      })];
    }));
  }
  return out;
}

function grid(combat) {
  const rows = {};
  const enemy0 = combat.enemies[0];
  for (const variant of GRID_VARIANTS) for (const stance of STANCES) {
    const state = prepare(combat, { stance: stance.stance, concurrent: stance.concurrent, statuses: { ...variant.statuses, ...(stance.statuses || {}) } });
    const asEnemy = prepare(combat, {});
    asEnemy.phase = 'enemy';
    const enemy = asEnemy.enemies[0];
    if (stance.stance) enemy.combatStance = clone(stance.stance); else delete enemy.combatStance;
    if (stance.concurrent) enemy.concurrentStances = clone(stance.concurrent);
    const enemyPhase = prepare(combat, { stance: stance.stance, concurrent: stance.concurrent, statuses: { ...variant.statuses, ...(stance.statuses || {}) }, phase: 'enemy' });
    const key = `${variant.name}/${stance.name}`;
    rows[key] = Object.fromEntries(profileCards.map((card) => {
      const def = (inst => expansion.resolveCombatCard(state, inst))({ instanceId: 'g', cardId: card.id, upgraded: false });
      const row = guarded(() => ({
        same: stanceRules.sameStance(state.player, def),
        chain: stanceRules.spellChain(state.player, def),
        effectiveness: stanceRules.stanceEffectiveness(state, state.player, def),
        entry: stanceRules.stanceEntryBonuses(def, state),
        sp: GRID_BASES.map((b) => stanceRules.stanceStaminaCost(b, state.player, def, state, 0)),
        spEnemyPhase: GRID_BASES.map((b) => stanceRules.stanceStaminaCost(b, enemyPhase.player, def, enemyPhase, 0)),
        mp: GRID_BASES.map((b) => stanceRules.stanceManaCost(b, state.player, def, state)),
        enemy: GRID_BASES.map((b) => [0, 1].map((m) => stanceRules.stanceStaminaCost(b, enemy, def, asEnemy, m))),
        enemySame: stanceRules.sameStance(enemy, def),
      }));
      return [card.id, row];
    }));
  }
  void enemy0;
  return { levels: levelRows(combat), stances: STANCES.map((s) => s.name), stanceSetups: Object.fromEntries(STANCES.map((s) => [s.name, { stance: s.stance, concurrent: s.concurrent ?? null, statuses: s.statuses ?? {} }])),
    variants: GRID_VARIANTS, bases: GRID_BASES, evadeGates, rows };
}

// ---------------------------------------------------------------------------------------------------------------
// Price derivation
// ---------------------------------------------------------------------------------------------------------------
const RANKS = [0, 1, 2, 3];
function derive(card, rank, authored) {
  return guarded(() => {
    const budget = actionCosts.cardCostBudget(card, { rank });
    const applied = actionCosts.applyActionCosts(card, authored ?? card, { rank });
    return { budget, optOut: actionCosts.isCostOptOut(card), unchanged: applied === card, cost: applied.cost, manaCost: applied.manaCost ?? null,
      effects: applied === card ? null : applied.effects ?? null, textTemplate: applied === card ? null : applied.textTemplate ?? null, funds: actionCosts.ownRestoreFunds(applied) };
  });
}
const forced = (card) => { const { costRule, ...rest } = card; return rest; };

function prices() {
  const out = { profileCards: {}, enemyCards: {}, enemies: {} };
  for (const card of profileCards) {
    out.profileCards[card.id] = {
      authored: derive(card, 0),
      forced: RANKS.map((rank) => derive(forced(card), rank)),
      upgraded: typeof card.cost === 'number' ? derive(forced(card), 0, { ...card, cost: card.cost + 1 }) : null,
    };
  }
  for (const card of enemyCards) {
    out.enemyCards[card.id] = { authored: derive(card, 0), forced: RANKS.slice(0, 3).map((rank) => derive(forced(card), rank)) };
  }
  for (const def of w.registries.enemies.all()) {
    out.enemies[def.id] = { pool: actionCosts.enemyPoolOf(def), blighted: actionCosts.enemyPoolOf({ ...def, blighted: true }),
      attributes: actionCosts.enemyAttributesOf({ ...def, blighted: true }) };
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Fights
// ---------------------------------------------------------------------------------------------------------------
const VARIANTS = [
  { name: 'base' },
  { name: 'enemyPhase', setup: { phase: 'enemy' } },
  { name: 'restrained', setup: { statuses: { 'base.restrained': 1 } } },
  { name: 'bound', setup: { statuses: { 'base.bind': 1 } } },
  { name: 'manyForms', setup: { statuses: { 'base.many-forms': 1 }, stance: { maneuver: 'attack', camp: 'physical', range: 'melee' }, concurrent: [{ maneuver: 'prepared', camp: 'spell', range: 'melee' }] } },
  { name: 'attackStance', setup: { stance: { maneuver: 'attack', camp: 'physical', range: 'melee' } } },
  { name: 'defendStance', setup: { stance: { maneuver: 'defend', camp: 'physical', range: 'melee' } } },
  { name: 'preparedStance', setup: { stance: { maneuver: 'prepared', camp: 'spell', range: 'melee' } } },
  { name: 'mirroredStance', setup: { stance: { maneuver: 'attack', camp: 'spell', range: 'melee', mirrored: true } } },
  { name: 'evadeStance', setup: { stance: { maneuver: 'evade', camp: 'physical', range: 'melee' }, statuses: Object.fromEntries(evadeGates.map((s) => [s, 1])) } },
  { name: 'staggered', setup: { stance: { maneuver: 'staggered', camp: 'physical', range: 'melee' } } },
  { name: 'dry', setup: { stamina: 0, mana: 0 } },
  { name: 'rich', setup: { stamina: 12, mana: 12 } },
  { name: 'grown', setup: { riders: { cardUsesCombat: Object.fromEntries(profileCards.filter((c) => c.costGrowth).map((c) => [c.id, 2])), instanceUsesCombat: { x1: 1 } } } },
];

function handScenario(combat, setup, deep) {
  const state = prepare(combat, setup);
  const target = state.enemies.find((e) => e.alive)?.id;
  const cards = {};
  for (const card of state.piles.hand) cards[card.instanceId] = { cardId: card.cardId, ...cardRow(state, card.instanceId, deep, target) };
  return { setup, before: playerState(state), cards };
}

// A chain of plays: each step plays a card the player can afford (the bot's LCG chooses), then prices the whole hand again.
function chain(combat, seed, length, extra = {}) {
  const pick = botLcg(seed);
  const setup = { hand: 'all', stamina: 12, mana: 12, ...extra };
  const state = prepare(combat, setup);
  const steps = [];
  const priceHand = () => Object.fromEntries(state.piles.hand.map((c) => [c.instanceId, costsOf(state, c.instanceId)]));
  const start = { setup, before: playerState(state), hand: priceHand() };
  for (let i = 0; i < length; i++) {
    const options = state.piles.hand.filter((c) => { const costs = costsOf(state, c.instanceId); return Array.isArray(costs) && costs[0] <= state.player.energy && costs[1] <= state.player.mana && !w.registries.framework.isUnplayable(expansion.resolveCombatCard(state, c)); });
    if (!options.length) break;
    const chosen = options[pick(options.length)];
    const play = enterByPlay(state, chosen.instanceId);
    steps.push({ instanceId: chosen.instanceId, play, after: playerState(state), hand: priceHand() });
  }
  return { start, steps };
}

function fight(seed, classId, encounter, ci, ei, deepVariants) {
  const run = createRun(w, seed, classId);
  const { combat } = createFight(w, run, encounter);
  const opening = handScenario(combat, {}, true);
  const variants = {};
  if (deepVariants) for (const v of VARIANTS) variants[v.name] = handScenario(combat, { hand: 'all', ...(v.setup || {}) }, deepVariants.has(v.name));
  const openingVariants = {};
  for (const v of VARIANTS) if (v.setup) openingVariants[v.name] = handScenario(combat, v.setup, true);
  const chains = [];
  for (let k = 0; k < CHAINS; k++) chains.push(chain(combat, w.rngMod.sweepSeed(seed + 31 * (k + 1) + ci * 7 + ei), CHAIN_LENGTH, k === CHAINS - 1 ? { statuses: { 'base.many-forms': 1 } } : {}));
  return { seed, classId, encounterId: encounter.id, opening, openingVariants, variants, chains };
}

// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, files: [] };
const write = (file, value, meta = {}) => {
  const text = json(value);
  writeFileSync(join(OUT, file), text);
  index.files.push({ file, bytes: Buffer.byteLength(text), ...meta });
};

const gridSeed = w.rngMod.sweepSeed(5100);
const baseRun = createRun(w, gridSeed, classes[0]);
write('grid.json', { fight: { seed: gridSeed, classId: classes[0], encounterId: all[0].id }, ...grid(createFight(w, baseRun, all[0]).combat) }, { kind: 'grid' });
write('prices.json', prices(), { kind: 'prices' });
// A hand of all 211 cards under every variant is recorded for the first fight of a class only; the previews and the dispatches of all 211
// cards are slow, so only a few variants do them.
const DEEP = new Set(['base', 'manyForms', 'dry']);
classes.forEach((classId, ci) => {
  const fights = [];
  for (let s = 0; s < SEEDS; s++) for (let e = 0; e < ENCOUNTERS; e++) {
    const ei = (e * 5 + ci * 3 + s) % all.length;
    fights.push(fight(w.rngMod.sweepSeed(5200 + ci * 1000 + s * 100 + ei), classId, all[ei], ci, ei, fights.length === 0 ? DEEP : null));
  }
  write(`fights-${classId}.json`, { classId, fights }, { kind: 'fights', classId, fights: fights.length });
});
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-costs: ${index.files.length} files, ${(index.files.reduce((n, f) => n + f.bytes, 0) / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/costs`);
