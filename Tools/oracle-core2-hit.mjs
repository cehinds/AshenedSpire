#!/usr/bin/env node
// Core 2 d20 hit-resolution oracle (WP-6, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED src/engine/stanceHit.js
// (attackHitPreview, resolveAttackHit, previewCardAttackHit, effectiveEvasion, evadeStackGain, precisionStatMix) and
// src/engine/combatAvoidance.js (rollChanceDie, resolveAvoidance, effectiveChance) on real Core 2 fights (createRunCombat for the
// configured `default` profile, WP-4) with a declared overlay on the fighters.
//
//   node Tools/oracle-core2-hit.mjs [--cases 360] [--seqs 60] [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/hit/, sorted keys, two runs are byte-identical):
//   hit-<n>.json   grid cases. Each: the fight (seed, class, encounter), the overlay (`patch` on source/target, `fields` on the combat,
//                  `rules` on the fixCoreRules snapshot, the range state mode), the carrier, the options, the web's own INPUTS to the
//                  preview (the range contact, the proposal plan: both owned by later work packages), the preview, and `scripts`: the
//                  same attack resolved with scripted dice (the die list, the dice the engine asked for, the result, the target after,
//                  the events). Scripted dice include naturals 1 and 20 and the die either side of the need.
//   seq.json       sequences: the same attack repeated on the real `combatAvoidance` stream; per step the result, the die count and
//                  every RNG counter (the evade stack is consumed on a miss, so the sequence changes as it runs).
//   units.json     effectiveEvasion, evadeStackGain, precisionStatMix/precisionEntryGrant, previewCardAttackHit, rollChanceDie,
//                  effectiveChance and resolveAvoidance on their own.
// A case the web itself refuses (it throws) is not recorded; the count is in index.json.
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, botLcg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'hit');
const CASES = Number(arg('--cases', '360'));
const SEQS = Number(arg('--seqs', '60'));
const PER_FILE = 90;
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const hit = w.stanceHit;
const avoid = await load('src/engine/combatAvoidance.js');
const proposalRuntime = await load('src/engine/proposalStatusRuntime.js');
const profileModule = await load('src/model/combatCardProfile.js');
const all = encounters(w);
const classes = classIds(w);

// ---------------------------------------------------------------------------------------------------------------
// Overlay helpers
// ---------------------------------------------------------------------------------------------------------------
const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
/** RFC 7386 merge: an object merges key by key, null deletes, anything else replaces. Both sides (this and the C# test) apply it. */
export function merge(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete target[key];
    else if (isObject(value) && isObject(target[key])) merge(target[key], value);
    else target[key] = structuredClone(value);
  }
  return target;
}
const restore = (entity, snap) => { for (const k of Object.keys(entity)) delete entity[k]; Object.assign(entity, structuredClone(snap)); };

function scriptedRng(real, dice) {
  const rng = Object.create(real);
  const calls = [];
  let i = 0;
  rng.int = (stream, min, max) => {
    calls.push([stream, min, max]);
    if (i >= dice.length) throw new Error('script exhausted');
    return dice[i++];
  };
  rng.float = () => { throw new Error('the hit roll must draw with int'); };
  return { rng, calls, used: () => i };
}

/** Builds the fight and lays the declared overlay on it. Returns everything a step needs. */
function build(spec) {
  const run = createRun(w, spec.seed, spec.classId);
  const encounter = all.find((e) => e.id === spec.encounterId);
  const { combat } = createFight(w, run, encounter);
  if (spec.fields) merge(combat, spec.fields);
  if (spec.rules) merge(combat.fixCoreRules, spec.rules);
  // Proposal rules gate on the profile's capabilities (modules, elements, protections); a legacy foundation opens every rule.
  if (spec.openCapabilities) combat.gameProfile = { ...combat.gameProfile, foundation: { ...combat.gameProfile.foundation, mode: 'legacy' } };
  const player = combat.player, enemy = combat.enemies[spec.enemyIndex ?? 0];
  const source = spec.direction === 'playerToEnemy' ? player : enemy;
  const target = spec.direction === 'playerToEnemy' ? enemy : player;
  if (spec.patch?.source) merge(source, spec.patch.source);
  if (spec.patch?.target) merge(target, spec.patch.target);
  const range = spec.range || { mode: 'keep' };
  if (range.mode === 'delete') delete combat.rangeState;
  else if (range.mode === 'band') for (const key of Object.keys(combat.rangeState.engagements)) combat.rangeState.engagements[key] = range.band;
  return { combat, source, target, carrier: structuredClone(spec.carrier), options: spec.options || {} };
}

const mark = (combat) => combat.eventLog.length;
// The events a hit owns. A miss against Evade or a prevented hit also credits the defender's enemy knowledge (a perceptionGained receipt from
// engine/enemyKnowledge.js recordKnowledgeEvent): that crediting is the knowledge port's hook (WP-11), so the receipt is not recorded here.
const OWN_EVENTS = new Set(['attackHit', 'attackMissed', 'statusRemoved', 'statusExpired', 'combatAvoidanceResolved']);
const eventsSince = (combat, m) => canon(combat.eventLog.slice(m).filter((e) => OWN_EVENTS.has(e.type)));
const after = (entity) => canon({ statuses: entity.statuses, nextEvadeMode: entity.nextEvadeMode ?? null, combatEvade: entity.combatEvade ?? null });

/** What the preview takes from later work packages: the range contact (WP-12) and the proposal plan (statuses). */
function inputsOf(combat, source, target, carrier, options) {
  const contact = combat.rangeState ? w.engagement.rangeHitContact(combat, source, target, carrier) : null;
  return {
    rangeContact: contact ? canon(contact) : null,
    damageType: proposalRuntime.proposalDamageType(carrier) ?? null,
    proposal: canon(proposalRuntime.proposalTargetPlan(combat, source, target, carrier)),
  };
}

// ---------------------------------------------------------------------------------------------------------------
// Carriers: synthetic profiles over the maneuver / camp / range / reach grid, and real ones (a hand card, an enemy intent)
// ---------------------------------------------------------------------------------------------------------------
const MANEUVERS = ['attack', 'smash', 'sweep', 'counter', 'defend', null];
const RANGES = ['melee', 'reach', 'ranged'];

function syntheticCarrier(pick) {
  const maneuver = MANEUVERS[pick(MANEUVERS.length)];
  const spell = pick(4) === 0;
  const fire = pick(4) === 0;
  const profile = { camp: spell ? 'spell' : 'physical', maneuver, school: spell ? (fire ? 'fire' : 'frost') : null, damageType: fire ? 'fire' : spell ? 'frost' : 'slash', counterMode: null,
    range: RANGES[pick(RANGES.length)], targeting: 'single', stanceTrigger: maneuver != null, traits: [] };
  if (pick(9) === 0) profile.traits = ['adequateReach'];
  if (pick(14) === 0) profile.rangeOverride = true;
  if (pick(14) === 0) profile.adequateReach = true;
  if (spell && pick(3) === 0) profile.mirrors = ['attack', 'smash', 'sweep'][pick(3)];
  const tags = [`camp:${profile.camp}`, ...(maneuver ? [`maneuver:${maneuver}`] : []), `range:${profile.range}`, ...(spell && pick(2) === 0 ? ['target'] : [])];
  const carrier = { cardId: 'oracle.synthetic', tags, authoredTags: tags, combatProfile: profile };
  if (pick(10) === 0) carrier.counterRequiredRange = RANGES[pick(RANGES.length)];
  if (pick(10) === 0) carrier.additionalTargets = 1 + pick(3);
  return carrier;
}

function realCarrier(combat, direction, pick) {
  if (direction === 'enemyToPlayer') {
    const intent = combat.enemies[0].intent;
    if (!intent?.combatProfile) return null;
    return { moveId: intent.moveId, combatProfile: structuredClone(intent.combatProfile), tags: [], authoredTags: [] };
  }
  const hand = combat.piles.hand;
  if (!hand.length) return null;
  const card = hand[pick(hand.length)];
  const def = combat.registries.cards.get(card.cardId);
  const ids = (def.tags || []).map((t) => (typeof t === 'string' ? t : t.id));
  const profile = profileModule.combatProfileFor({ ...def, cardTags: ids });
  return { cardId: card.cardId, tags: ids, authoredTags: ids, combatProfile: canon(profile) };
}

/** A weapon-shaped range source so rangeSourceContact never reaches for the legacy foundation. */
function withRangeSource(carrier, combat, direction, pick) {
  if (direction !== 'playerToEnemy') return carrier;
  const arms = combat.registries.equipment.armaments;
  const piece = arms[pick(arms.length)];
  const kind = carrier.combatProfile?.camp === 'spell' ? 'spell' : pick(5) === 0 ? 'unarmed' : 'weapon';
  carrier.resolvedRangeSource = kind === 'weapon'
    ? { sourceType: 'weapon', itemId: piece.id, range: piece.range || 'melee', propertyTags: [...(piece.propertyTags || []), ...(pick(3) === 0 ? ['throw'] : [])] }
    : { sourceType: kind, range: carrier.combatProfile?.range || 'melee', propertyTags: [] };
  return carrier;
}

// ---------------------------------------------------------------------------------------------------------------
// The grid
// ---------------------------------------------------------------------------------------------------------------
const ATTR = [0, 1, 3, 6, 10, 15];
const ATTR_SMALL = [0, 0, 1, 2, 3, 5];
const LEVELS = [1, 2, 5, 9];
const LEVELS_SMALL = [1, 1, 2, 3];
const STACKS = [0, 0, 1, 2, 3, 5, 8];
const STACKS_SMALL = [0, 0, 0, 1, 2];
const DEX = [0, 1, 3, 6, 10];
const EVADE = [0, 0, 1, 2, 3, 5];
const EVADE_BIG = [1, 2, 3, 4, 6, 8];
const MODES = ['none', 'none', 'none', 'srcAdv', 'srcDis', 'evadeAdv', 'evadeDis', 'cancel', 'preparedAdv', 'preparedDis', 'nextMode', 'nextModePrepared', 'preparedExpired'];
const STANCE_RANGE = [null, 'melee', 'reach', 'ranged'];

function gridSpec(index) {
  const pick = botLcg(0x6d2b79f5 ^ (index * 2654435761));
  const direction = pick(3) === 0 ? 'enemyToPlayer' : 'playerToEnemy';
  const classId = classes[pick(classes.length)];
  const encounter = all[pick(all.length)];
  const seed = w.rngMod.sweepSeed(14000 + index);
  // Half the grid is contested (small attackers against stacked Evade); the rest spans the wide ranges.
  const small = pick(2) === 0;
  const AT = small ? ATTR_SMALL : ATTR;
  const attrs = () => ({ strength: AT[pick(AT.length)], dexterity: AT[pick(AT.length)], wisdom: AT[pick(AT.length)], intelligence: AT[pick(AT.length)], constitution: 1 });
  const source = { attributes: attrs(), statuses: {}, basePrecisionBonus: pick(4) === 0 ? 1 + pick(3) : 0 };
  const level = (small ? LEVELS_SMALL : LEVELS)[pick(small ? LEVELS_SMALL.length : LEVELS.length)];
  const fields = {};
  if (direction === 'playerToEnemy') fields.characterLevel = level; else source.level = level;
  const stacks = (small ? STACKS_SMALL : STACKS)[pick(small ? STACKS_SMALL.length : STACKS.length)];
  if (stacks) source.statuses.precision = { stacks };
  const target = { attributes: { dexterity: DEX[pick(DEX.length)] }, statuses: {}, baseEvasionBonus: pick(5) === 0 ? 1 + pick(3) : 0 };
  const evade = (small ? EVADE_BIG : EVADE)[pick(6)];
  if (evade) target.statuses.evade = { stacks: evade };
  // Base-card statuses carry proposal rules (accuracy delta, Greased fire): the plan is an input the web computes.
  const proposalRoll = pick(10);
  if (proposalRoll === 0) source.statuses['base.charm'] = { stacks: 1 };
  if (proposalRoll === 1) source.statuses['base.greased'] = { stacks: 1 };
  if (proposalRoll === 2) target.statuses['base.greased'] = { stacks: 1 };
  if (proposalRoll === 3) { source.statuses['base.charm'] = { stacks: 2 }; target.statuses['base.greased'] = { stacks: 1 }; }
  const mode = MODES[pick(MODES.length)];
  const prepared = (extra = {}) => ({ charges: 1 + pick(2), bonus: pick(3), ...extra });
  if (mode === 'srcAdv') source.accuracyAdvantage = true;
  if (mode === 'srcDis') source.accuracyDisadvantage = true;
  if (mode === 'evadeAdv') target.evadeAdvantage = true;
  if (mode === 'evadeDis') target.evadeDisadvantage = true;
  if (mode === 'cancel') { source.accuracyAdvantage = true; target.evadeAdvantage = true; }
  if (mode === 'preparedAdv') { target.combatEvade = prepared({ advantage: true }); }
  if (mode === 'preparedDis') { target.combatEvade = prepared({ disadvantage: true }); }
  if (mode === 'nextMode') { target.nextEvadeMode = pick(2) ? { advantage: true } : { disadvantage: true }; if (!evade) target.combatEvade = prepared(); }
  if (mode === 'nextModePrepared') {
    target.statuses.prepared = { stacks: pick(2) };
    target.nextEvadeMode = { advantage: true, whileStatus: 'prepared' };
    target.combatEvade = prepared({ whileStatus: 'prepared' });
  }
  if (mode === 'preparedExpired') { target.combatOwnerCycle = 3; target.combatEvade = prepared({ expiresOnOwnerCycle: 2 }); }
  const stanceRange = STANCE_RANGE[pick(STANCE_RANGE.length)];
  if (stanceRange) target.combatStance = { maneuver: 'attack', camp: pick(4) === 0 ? 'spell' : 'physical', range: stanceRange };
  const rangeRoll = pick(10);
  const range = rangeRoll === 0 ? { mode: 'delete' } : rangeRoll < 2 ? { mode: 'band', band: 1 + pick(2) } : { mode: 'keep' };
  const options = {};
  if (pick(9) === 0) options.evadeDisabled = true;
  // The rules snapshot is data: a grid over its accuracy block too.
  const rules = pick(4) === 0 ? { accuracy: { precisionBase: pick(3), evasionBase: pick(3), baseAC: 1 + pick(2), maneuverPrecision: { smash: pick(3), sweep: pick(3) },
    precisionWeights: { dex: [0.5, 0.3, 1][pick(3)], str: [0.1, 0, 0.25][pick(3)], wis: 0.2, int: 0.2, level: [1, 0.5, 2][pick(3)] } } } : null;
  const openCapabilities = pick(6) === 0;
  return { index, seed, classId, encounterId: encounter.id, direction, fields, rules, patch: { source, target }, range, options, mode, openCapabilities, pick };
}

/** Builds one case or returns null when the web refuses it. */
function makeCase(index) {
  const g = gridSpec(index);
  const real = index % 3 === 0;
  const trial = build({ ...g, carrier: {} });
  let carrier = real ? realCarrier(trial.combat, g.direction, g.pick) : null;
  const kindOfCarrier = carrier ? 'real' : 'synthetic';
  carrier ??= syntheticCarrier(g.pick);
  carrier = withRangeSource(carrier, trial.combat, g.direction, g.pick);
  const spec = { seed: g.seed, classId: g.classId, encounterId: g.encounterId, direction: g.direction, fields: g.fields, rules: g.rules, patch: g.patch, range: g.range, carrier, options: g.options, openCapabilities: g.openCapabilities };
  const built = build(spec);
  const { combat, source, target, options } = built;
  try {
    const inputs = inputsOf(combat, source, target, built.carrier, options);
    const preview = hit.attackHitPreview(combat, source, target, built.carrier, options);
    if (!preview) return null;
    const need = preview.need;
    const wantsTwo = preview.mode !== 'normal';
    const pickDie = botLcg(g.seed ^ 0x1f3d5b79);
    const interesting = [1, 20, Math.max(1, Math.min(20, need - 1)), Math.max(1, Math.min(20, need)), Math.max(1, Math.min(20, need + 1)), 1 + pickDie(20), 1 + pickDie(20)];
    const scripts = [];
    const targetSnap = structuredClone(target), sourceSnap = structuredClone(source);
    for (let s = 0; s < 6; s++) {
      const first = interesting[pickDie(interesting.length)];
      const dice = wantsTwo && preview.inRange && !preview.bypass ? [first, interesting[pickDie(interesting.length)]] : [first];
      const script = scriptedRng(combat.rng, dice);
      const real_rng = combat.rng;
      combat.rng = script.rng;
      const m = mark(combat);
      let result, error = null;
      try { result = hit.resolveAttackHit(combat, source, target, built.carrier, structuredClone(options)); } catch (e) { error = e.message; }
      combat.rng = real_rng;
      scripts.push(canon({ dice, calls: script.calls, used: script.used(), result: result ?? null, error, targetAfter: after(target), sourceAfter: after(source), events: eventsSince(combat, m) }));
      combat.eventLog.length = m;
      restore(target, targetSnap); restore(source, sourceSnap);
    }
    return canon({ ...spec, carrierKind: kindOfCarrier, mode: g.mode, inputs, preview, scripts, rng: combat.rng.getCounters() });
  } catch (e) {
    return { skipped: e.message };
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Sequences on the real stream
// ---------------------------------------------------------------------------------------------------------------
function makeSequence(index) {
  const g = gridSpec(100000 + index);
  const trial = build({ ...g, carrier: {} });
  const carrier = withRangeSource(syntheticCarrier(g.pick), trial.combat, g.direction, g.pick);
  carrier.combatProfile.range = 'melee';
  carrier.combatProfile.traits = [];
  delete carrier.combatProfile.rangeOverride; delete carrier.combatProfile.adequateReach; delete carrier.counterRequiredRange;
  // Make most fights contestable: a real evade stack to consume and a need inside the die.
  const patch = structuredClone(g.patch);
  patch.target.statuses.evade = { stacks: 1 + g.pick(4) };
  const spec = { seed: g.seed, classId: g.classId, encounterId: g.encounterId, direction: g.direction, fields: g.fields, rules: g.rules, patch, range: { mode: 'keep' }, carrier, options: {} };
  const built = build(spec);
  const { combat, source, target } = built;
  try {
    const steps = [];
    const rounds = 6 + g.pick(8);
    for (let i = 0; i < rounds; i++) {
      const before = combat.rng.getCounters().combatAvoidance;
      const m = mark(combat);
      const inputs = inputsOf(combat, source, target, built.carrier, {});
      const preview = hit.attackHitPreview(combat, source, target, built.carrier, {});
      const countersBeforeResolve = canon(combat.rng.getCounters());
      const result = hit.resolveAttackHit(combat, source, target, built.carrier, {});
      const counters = canon(combat.rng.getCounters());
      steps.push(canon({ inputs, preview, previewCounters: countersBeforeResolve, result, drawn: counters.combatAvoidance - before, counters, targetAfter: after(target), events: eventsSince(combat, m) }));
    }
    return canon({ ...spec, steps });
  } catch (e) {
    return { skipped: e.message };
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Units
// ---------------------------------------------------------------------------------------------------------------
function unitFight(index, direction = 'playerToEnemy') {
  const g = gridSpec(200000 + index);
  return { g, spec: { seed: g.seed, classId: g.classId, encounterId: g.encounterId, direction, fields: g.fields, rules: g.rules, patch: g.patch, range: { mode: 'keep' }, carrier: {} } };
}

function unitEvasion() {
  const out = [];
  for (let i = 0; i < 70; i++) {
    const { g, spec } = unitFight(i, i % 2 ? 'enemyToPlayer' : 'playerToEnemy');
    const built = build(spec);
    const evadeDisabled = g.pick(4) === 0;
    const useBase = g.pick(3) === 0;
    const options = { evadeDisabled };
    if (useBase) options.baseEntity = 'source';
    const value = hit.effectiveEvasion(built.combat, built.target, { evadeDisabled, ...(useBase ? { baseEntity: built.source } : {}) });
    out.push(canon({ ...spec, options, value }));
  }
  return out;
}

function unitGain() {
  const out = [];
  for (let i = 0; i < 40; i++) {
    const { g, spec } = unitFight(300 + i, i % 2 ? 'enemyToPlayer' : 'playerToEnemy');
    spec.rules = { accuracy: { evadeStackBase: g.pick(3), evadeStackDex: [0, 0.5, 1, 0.25][g.pick(4)] } };
    const built = build(spec);
    out.push(canon({ ...spec, entity: i % 3 ? 'target' : 'source', value: hit.evadeStackGain(built.combat, i % 3 ? built.target : built.source, null),
      noEntity: hit.evadeStackGain(built.combat, null, null) }));
  }
  return out;
}

function unitMix() {
  const out = [];
  for (let i = 0; i < 60; i++) {
    const { g, spec } = unitFight(400 + i, i % 2 ? 'enemyToPlayer' : 'playerToEnemy');
    const built = build(spec);
    const base = g.pick(4);
    out.push(canon({ ...spec, base, mix: hit.precisionStatMix(built.combat, built.source, null), grant: hit.precisionEntryGrant(built.combat, built.source, base, null),
      targetMix: hit.precisionStatMix(built.combat, built.target, null) }));
  }
  return out;
}

/** previewCardAttackHit: the selected card's Accuracy after the stance entry it would make (the stance bonus). */
function unitCardPreview() {
  const out = [];
  for (let i = 0; i < 90; i++) {
    const g = gridSpec(500 + i);
    const direction = 'playerToEnemy';
    const trial = build({ ...g, direction, carrier: {} });
    const pick = botLcg(g.seed ^ 0x2545f491);
    const real = realCarrier(trial.combat, direction, pick);
    let carrier = real ?? syntheticCarrier(g.pick);
    if (i % 2) { carrier = syntheticCarrier(g.pick); carrier.combatProfile.maneuver = ['attack', 'smash', 'sweep', 'defend', 'counter'][g.pick(5)]; carrier.combatProfile.stanceTrigger = true; }
    if (i % 5 === 0) carrier.combatProfile.evade = { stacks: 1 };
    carrier = withRangeSource(carrier, trial.combat, direction, g.pick);
    // A prior stance in force: the same stance as the card (no entry grant) on every third case.
    const patch = structuredClone(g.patch);
    if (i % 3 === 0 && carrier.combatProfile.maneuver) patch.source.combatStance = { maneuver: carrier.combatProfile.maneuver, camp: carrier.combatProfile.camp === 'spell' ? 'spell' : 'physical', range: carrier.combatProfile.range || 'melee' };
    const spec = { seed: g.seed, classId: g.classId, encounterId: g.encounterId, direction, fields: g.fields, rules: g.rules, patch, range: g.range, carrier, options: g.options, openCapabilities: g.openCapabilities };
    try {
      const built = build(spec);
      const inputs = inputsOf(built.combat, built.source, built.target, built.carrier, built.options);
      const value = hit.previewCardAttackHit(built.combat, built.source, built.target, built.carrier, built.options);
      out.push(canon({ ...spec, inputs, value }));
    } catch (e) { out.push({ skipped: e.message }); }
  }
  return out;
}

/** proposalTargetPlan on its own: the status rules of the base cards, closed and opened by capabilities. */
function unitProposal() {
  const out = [];
  const ids = ['base.charm', 'base.greased', 'base.veil', 'base.evade', 'base.foresight', 'base.conceal', 'base.read-intent', 'base.weak'];
  for (let i = 0; i < 60; i++) {
    const g = gridSpec(900 + i);
    const direction = g.direction;
    const patch = structuredClone(g.patch);
    for (const side of ['source', 'target']) for (const id of ids) if (g.pick(5) === 0) patch[side].statuses[id] = { stacks: 1 + g.pick(3) };
    if (g.pick(4) === 0) patch.source.perception = g.pick(12);
    const spell = g.pick(3) === 0;
    const damageType = ['fire', 'slashing', 'frost', null][g.pick(4)];
    const carrier = { cardId: 'oracle.synthetic', tags: [...(spell ? ['camp:spell'] : []), ...(g.pick(3) === 0 ? ['targeting:area'] : []), ...(g.pick(4) === 0 ? ['range:reach'] : [])],
      combatProfile: { camp: spell ? 'spell' : 'physical', range: RANGES[g.pick(3)], damageType, targeting: g.pick(3) === 0 ? 'area' : 'single' } };
    if (g.pick(4) === 0) carrier.targeting = 'area';
    const spec = { seed: g.seed, classId: g.classId, encounterId: g.encounterId, direction, fields: g.fields, rules: null, patch, range: { mode: 'keep' }, carrier, openCapabilities: g.pick(2) === 0 };
    const built = build(spec);
    const plan = proposalRuntime.proposalTargetPlan(built.combat, built.source, built.target, built.carrier);
    out.push(canon({ ...spec, damageType: proposalRuntime.proposalDamageType(built.carrier) ?? null, plan }));
  }
  return out;
}

function unitChance() {
  const flagsList = [{}, { advantage: true }, { disadvantage: true }, { advantage: true, disadvantage: true }, { advantage: 1 }, { disadvantage: 'x' }];
  const modes = flagsList.map((flags) => ({ flags, mode: avoid.chanceMode(flags) }));
  const chances = [];
  for (const base of [-5, 0, 5, 25, 50, 75, 95, 100, 130]) for (const flags of flagsList) chances.push({ base, flags, value: avoid.effectiveChance(base, flags) });
  const dice = [];
  const pick = botLcg(0x9e3779b1);
  for (const die of [20, 100, 6]) for (const flags of flagsList) for (let k = 0; k < 3; k++) {
    const mode = avoid.chanceMode(flags);
    const script = Array.from({ length: mode === 'normal' ? 1 : 2 }, () => 1 + pick(die));
    const s = scriptedRng(w.createRng(1, {}), script);
    const result = avoid.rollChanceDie(s.rng, die, flags);
    dice.push({ die, flags, script, calls: s.calls, result });
  }
  // The real stream: seeds, counters, the dice drawn and the counters after.
  const real = [];
  for (let i = 0; i < 24; i++) {
    const seed = w.rngMod.sweepSeed(16000 + i), counters = { combatAvoidance: pick(50) };
    const die = [20, 100][i % 2], flags = flagsList[i % flagsList.length];
    const rng = w.createRng(seed, counters);
    const result = avoid.rollChanceDie(rng, die, flags);
    real.push({ seed, counters, die, flags, result, after: rng.getCounters() });
  }
  return canon({ modes, chances, dice, real });
}

function unitAvoidance() {
  const out = [];
  for (let i = 0; i < 48; i++) {
    const g = gridSpec(700 + i);
    const direction = g.direction;
    const profile = { range: RANGES[g.pick(3)], targeting: ['single', 'area'][g.pick(2)] };
    if (g.pick(3) === 0) profile.traits = ['unavoidable'];
    const patch = structuredClone(g.patch);
    const which = g.pick(4);
    if (which === 1) patch.target.statuses.invulnerability = { stacks: 1 + g.pick(2) };
    if (which === 2) patch.target.statuses.decoy = { stacks: 1 + g.pick(2) };
    if (which === 3) { patch.target.statuses.invulnerability = { stacks: 1 }; patch.target.statuses.decoy = { stacks: 1 }; }
    const damage = [0, 0, 3, 8][g.pick(4)];
    const options = g.pick(3) === 0 ? { hitCheck: true } : {};
    const saved = g.pick(8) === 0 ? { resolved: true, avoided: true, chance: 7 } : null;
    const spec = { seed: g.seed, classId: g.classId, encounterId: g.encounterId, direction, fields: g.fields, rules: null, patch, range: { mode: 'keep' }, carrier: {} };
    const built = build(spec);
    if (g.pick(5) === 0) built.source.knowledgeAction = { serial: 3, reads: {} };
    const m = mark(built.combat);
    const before = built.combat.rng.getCounters();
    const value = avoid.resolveAvoidance(built.combat, built.source, built.target, profile, damage, options, saved);
    out.push(canon({ ...spec, knowledgeAction: built.source.knowledgeAction ?? null, profile, damage, options, saved, value, targetAfter: after(built.target),
      events: eventsSince(built.combat, m), counters: built.combat.rng.getCounters(), countersBefore: before }));
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));

const cases = [];
let skipped = 0;
for (let i = 0; cases.length < CASES && i < CASES * 3; i++) {
  const c = makeCase(i);
  if (!c || c.skipped) { skipped++; continue; }
  cases.push(c);
}
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, files: [], skipped };
let total = 0;
for (let f = 0; f * PER_FILE < cases.length; f++) {
  const file = `hit-${f}.json`;
  const text = `${JSON.stringify({ cases: cases.slice(f * PER_FILE, (f + 1) * PER_FILE) })}\n`;
  writeFileSync(join(OUT, file), text);
  total += Buffer.byteLength(text);
  index.files.push({ file, cases: Math.min(PER_FILE, cases.length - f * PER_FILE), bytes: Buffer.byteLength(text) });
}
const seqs = [];
for (let i = 0; seqs.length < SEQS && i < SEQS * 3; i++) { const s = makeSequence(i); if (!s.skipped) seqs.push(s); }
const seqText = `${JSON.stringify({ sequences: seqs })}\n`;
writeFileSync(join(OUT, 'seq.json'), seqText);
total += Buffer.byteLength(seqText);
index.seq = { file: 'seq.json', sequences: seqs.length, steps: seqs.reduce((n, s) => n + s.steps.length, 0), bytes: Buffer.byteLength(seqText) };
const units = { evasion: unitEvasion(), gain: unitGain(), mix: unitMix(), cardPreview: unitCardPreview().filter((u) => !u.skipped), chance: unitChance(), avoidance: unitAvoidance(), proposal: unitProposal() };
const unitText = `${JSON.stringify(canon(units))}\n`;
writeFileSync(join(OUT, 'units.json'), unitText);
total += Buffer.byteLength(unitText);
index.units = { file: 'units.json', evasion: units.evasion.length, gain: units.gain.length, mix: units.mix.length, cardPreview: units.cardPreview.length, avoidance: units.avoidance.length, proposal: units.proposal.length, bytes: Buffer.byteLength(unitText) };
index.scripts = cases.reduce((n, c) => n + c.scripts.length, 0);
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-hit: ${cases.length} cases (${index.scripts} scripted rolls, ${skipped} skipped), ${seqs.length} sequences (${index.seq.steps} real-stream rolls), units ${JSON.stringify(index.units)}, ${(total / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/hit`);
