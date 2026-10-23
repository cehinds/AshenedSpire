#!/usr/bin/env node
// Core 2 damage-receipt and protection oracle (WP-7, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED
// src/engine/gameProfileCombat.js (profileDamageReceipt), src/engine/actions.js (applyAttackDamage, drainFixCorePoise, fixCoreStagger,
// gainBlock, gainBarrier, dealPoiseDamage), src/model/fixCoreMechanics.js (fixCoreRules, protectionReceipt, protectionBindings),
// src/model/mechanicsKernel.js, src/engine/stanceMitigation.js and the mechanic runtime (engine/runtime/interpreter.mjs) on real Core 2
// fights (createRunCombat for the configured `default` profile) with a declared overlay on the fighters.
//
//   node Tools/oracle-core2-damage.mjs [--cases 600] [--units 160] [--fights 4] [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/damage/, sorted keys, two runs are byte-identical):
//   receipts-<n>.json   the receipt grid. Each case: the fight (seed, class, encounter), the overlay (`patch` on source/target, `fields` on the
//                       combat, `rules` on the fixCoreRules snapshot), the carrier, the base, the web's own INPUTS to the receipt (the range
//                       contact, owned by WP-12), the `receipt` (profileDamageReceipt: every field, the `sources[]` lines included) and
//                       `applied`: the same attack through applyAttackDamage on scripted dice (a hit and a miss): what it returned, both
//                       fighters after, the events (type and payload), the actions it queued.
//   units.json          protectionReceipt, stanceMitigation and fixCoreRules (with entity mechanic attachments), the Poise drain, the
//                       Staggered stance, gainBlock / gainBarrier and dealPoiseDamage on their own; plus kernel units on inline mechanics
//                       (stacking modes, priorities, conditions, formulas, refusals).
//   mechanics.json      every engine/mechanics/<slug>/cases.json replayed on the web's own runtime: per case what the web produced (state after,
//                       events, value or error). The C# runs the same cases and must produce the same.
//   fights-<class>.json real fights with the damage chain live. Two bots: `acts` (the enemies take their turn: end turn until a step the port
//                       does not cover) and `attacks` (the player plays attack and Block cards, then ends the turn). Per step the full combat
//                       projection as a delta, the event types and every RNG counter.
//
// THE FILTER (what a recorded sequence avoids; the C# port refuses these by name and the test asserts it never has to): a step is recorded only
// when its dry run on a detached copy (a) throws nothing, (b) emits only event types in COVERED_EVENTS, (c) leaves no card choice, and (d) every
// card it plays has only effects in COVERED_OPS. An enemy whose move would break (b) or (c) is marked staggered for that end turn (the flag a Poise
// break sets, as the start oracle does), so the others still act. The player's reactions are ON (WP-10): the offers the enemies' hits open are
// answered by the bot (half the time it plays the first option, half the time it goes Back, from its own LCG), each answer one recorded step,
// and a dry run answers with a copy of that LCG. An offer outside what the port covers (oracle-core2.mjs reactionFilter) ends the sequence there.
import { writeFileSync, readFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, botLcg, detach, projectFull, delta, typesSince, statics, SOURCES, answerer, dispatchAnswering, reactionFilter, REACTION_EVENTS } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'damage');
const CASES = Number(arg('--cases', '600'));
const UNITS = Number(arg('--units', '160'));
const FIGHTS = Number(arg('--fights', '4'));
const PER_FILE = 150;
const w = await loadWeb();
await reactionFilter(w);
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const actions = await load('src/engine/actions.js');
const gpc = await load('src/engine/gameProfileCombat.js');
const fixcore = await load('src/model/fixCoreMechanics.js');
const kernel = await load('src/model/mechanicsKernel.js');
const mitigation = await load('src/engine/stanceMitigation.js');
const interpreter = await load('engine/runtime/interpreter.mjs');
const hitModule = await load('src/engine/stanceHit.js');
const engagement = await load('src/engine/combatEngagement.js');
const profileModule = await load('src/model/combatCardProfile.js');
const proposalRuntime = await load('src/engine/proposalStatusRuntime.js');
const tactics = await load('src/engine/combatCardTactics.js');
const triggers = await load('src/engine/triggers.js');
const expansion = await load('src/engine/combatExpansion.js');
const all = encounters(w);
const classes = classIds(w);

// ---------------------------------------------------------------------------------------------------------------
// Overlay helpers (RFC 7386 merge, shared in spirit with the hit oracle: an object merges, null deletes, anything else replaces)
// ---------------------------------------------------------------------------------------------------------------
const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
export function merge(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete target[key];
    else if (isObject(value) && isObject(target[key])) merge(target[key], value);
    else target[key] = structuredClone(value);
  }
  return target;
}

function scriptedRng(real, dice) {
  const rng = Object.create(real);
  const calls = [];
  let i = 0;
  rng.int = (stream, min, max) => { calls.push([stream, min, max]); if (i >= dice.length) throw new Error('script exhausted'); return dice[i++]; };
  rng.float = () => { throw new Error('the damage chain must draw with int'); };
  return { rng, calls, used: () => i };
}

/** Builds the fight and lays the declared overlay on it. */
function build(spec) {
  const run = createRun(w, spec.seed, spec.classId);
  const encounter = all.find((e) => e.id === spec.encounterId);
  const { combat } = createFight(w, run, encounter);
  if (spec.fields) merge(combat, spec.fields);
  if (spec.rules) merge(combat.fixCoreRules, spec.rules);
  const player = combat.player, enemy = combat.enemies[spec.enemyIndex ?? 0];
  const source = spec.direction === 'playerToEnemy' ? player : enemy;
  const target = spec.direction === 'playerToEnemy' ? enemy : player;
  if (spec.patch?.source) merge(source, spec.patch.source);
  if (spec.patch?.target) merge(target, spec.patch.target);
  const range = spec.range || { mode: 'keep' };
  if (range.mode === 'delete') delete combat.rangeState;
  else if (range.mode === 'band') for (const key of Object.keys(combat.rangeState.engagements)) combat.rangeState.engagements[key] = range.band;
  return { combat, source, target, carrier: structuredClone(spec.carrier) };
}

const eventsSince = (combat, m) => canon(combat.eventLog.slice(m));
const looks = (entity) => canon({ ...entity, statusControl: undefined, abilityRiders: undefined, ratingSources: undefined, knowledgeAction: undefined, intent: undefined, plannedMoves: undefined, movesHistory: undefined, arcaneExposure: undefined });

// ---------------------------------------------------------------------------------------------------------------
// Carriers: synthetic profiles over the maneuver / camp / type / school grid, and real ones (a hand card, an enemy intent)
// ---------------------------------------------------------------------------------------------------------------
const MANEUVERS = ['attack', 'smash', 'sweep', 'counter', 'defend', 'ranged', 'casting', null];
const STANCES = ['attack', 'smash', 'defend', 'counter', 'sweep', 'prepared', 'evade', 'neutral', 'staggered'];
const TYPES = [null, 'slashing', 'piercing', 'blunt', 'fire', 'frost', 'lightning', 'arcane', 'sacred', 'decay', 'poison', 'psychic', 'earth', 'slash', 'pierce', 'holy'];
const SCHOOLS = [null, null, null, 'fire', 'frost', 'lightning', 'force', 'alteration', 'divine', 'decay', 'earth', 'illusion', 'physical'];
const RANGES = ['melee', 'reach', 'ranged'];

function syntheticCarrier(pick) {
  const maneuver = MANEUVERS[pick(MANEUVERS.length)];
  const spell = pick(3) === 0;
  const school = spell || pick(5) === 0 ? SCHOOLS[pick(SCHOOLS.length)] : null;
  const damageType = TYPES[pick(TYPES.length)];
  const profile = { camp: spell ? 'spell' : 'physical', maneuver, school, damageType, counterMode: null, range: RANGES[pick(RANGES.length)], targeting: 'single', stanceTrigger: maneuver != null, traits: [] };
  if (pick(8) === 0) profile.reach = ['contact', 'melee', 'reach'][pick(3)];
  if (pick(10) === 0) profile.physicalDamageType = ['slashing', 'piercing', 'blunt'][pick(3)];
  if (pick(9) === 0) profile.damageComponents = [{ type: ['fire', 'frost', 'arcane'][pick(3)], weight: 1 + pick(3) }, { type: ['slashing', 'blunt'][pick(2)], weight: 1 + pick(3) }];
  if (pick(10) === 0) profile.riderSpell = true;
  const tags = [`camp:${profile.camp}`, ...(maneuver ? [`maneuver:${maneuver}`] : []), `range:${profile.range}`, ...(school && pick(2) ? [`school:${school}`] : [])];
  const carrier = { cardId: 'oracle.synthetic', tags, authoredTags: tags, combatProfile: profile };
  if (pick(7) === 0) carrier.magicShare = [0, 0.25, 0.5, 1][pick(4)];
  if (pick(10) === 0) carrier.skipRatingBonus = true;
  if (pick(14) === 0) carrier.counterReturnAttack = true;
  if (pick(6) === 0) carrier.fixCoreSameStance = true;
  if (pick(7) === 0) carrier.sourceArmamentId = 'sword';
  if (pick(14) === 0) carrier.equipmentScoped = true;
  if (pick(10) === 0) carrier.damageSchool = school || 'physical';
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

function withRangeSource(carrier, combat, direction, pick) {
  if (direction !== 'playerToEnemy') return carrier;
  const arms = combat.registries.equipment.armaments;
  const piece = arms[pick(arms.length)];
  const kind = carrier.combatProfile?.camp === 'spell' ? 'spell' : pick(5) === 0 ? 'unarmed' : 'weapon';
  carrier.resolvedRangeSource = kind === 'weapon'
    ? { sourceType: 'weapon', itemId: piece.id, range: piece.range || 'melee', propertyTags: [...(piece.propertyTags || [])] }
    : { sourceType: kind, range: carrier.combatProfile?.range || 'melee', propertyTags: [] };
  return carrier;
}

// ---------------------------------------------------------------------------------------------------------------
// The receipt grid
// ---------------------------------------------------------------------------------------------------------------
const RATING = [0, 1, 3, 6, 10, 15, 30];
const POOL = [0, 1, 3, 5, 8, 12, 20, 40];
const BLOCK = [0, 0, 2, 5, 9, 14, 30];
const BASES = [0, 1, 2, 3, 5, 6, 8, 10, 13, 18, 25, 40, 70];
const PRESSURE = [0, 0, 0, 1, 2, 3];
const BASE_STATUS = ['base.weak', 'base.mirrors', 'base.intangible', 'base.reflect', 'base.auto-counter', 'base.auto-sweep', 'base.persistent-block', 'base.greased', 'base.necrotic-healing', 'base.elemental-weapon', 'base.guard-allies'];

function entityPatch(pick, role) {
  const e = { ratings: {}, statuses: {} };
  if (pick(2)) e.ratings = { ar: RATING[pick(RATING.length)], pr: RATING[pick(RATING.length)], dr: RATING[pick(RATING.length)], poise: POOL[pick(POOL.length)], ward: POOL[pick(POOL.length)] };
  else delete e.ratings;
  if (pick(3) === 0) {
    const sources = [{ name: 'Attributes', kind: 'attribute', ar: RATING[pick(RATING.length)], dr: RATING[pick(RATING.length)], pr: RATING[pick(RATING.length)] }];
    if (pick(2)) sources.push({ name: 'Sword', kind: 'equipment', sourceId: 'armament/sword', slot: 'rightHand', ar: RATING[pick(RATING.length)], pr: RATING[pick(RATING.length)], dr: 0 });
    if (pick(2)) sources.push({ name: 'Staff', kind: 'equipment', sourceId: 'armament/staff', slot: 'leftHand', ar: RATING[pick(RATING.length)], pr: RATING[pick(RATING.length)], dr: 0 });
    if (pick(2)) sources.push({ name: 'Plate', kind: 'equipment', sourceId: 'armor/plate', slot: 'armor', ar: 0, pr: pick(3), dr: RATING[pick(RATING.length)] });
    if (pick(5) === 0) sources.push({ kind: 'relic', ar: 1 + pick(3), pr: 0, dr: 0 });
    e.ratingSources = sources;
  }
  const block = BLOCK[pick(BLOCK.length)];
  if (block || pick(3) === 0) e.block = block;
  if (pick(5) === 0) e.barrier = BLOCK[pick(BLOCK.length)];
  const poise = POOL[pick(POOL.length)], ward = POOL[pick(POOL.length)];
  if (pick(2)) e.persistentPoise = { value: Math.min(poise, poise - pick(3)), max: poise };
  if (pick(3) === 0) e.persistentWard = { value: ward, max: ward };
  if (pick(6) === 0) e.statuses.weak = { stacks: 1 + pick(3) };
  if (pick(5) === 0) e.statuses.vulnerable = { stacks: 1 + pick(3) };
  if (pick(7) === 0) e.statuses[BASE_STATUS[pick(BASE_STATUS.length)]] = { stacks: 1 + pick(2) };
  if (pick(2)) e.combatStance = { maneuver: STANCES[pick(STANCES.length)], camp: pick(4) === 0 ? 'spell' : 'physical', range: 'melee' };
  if (pick(14) === 0) {
    // Many Forms: two stances at once; the defense that serves best against the carrier answers.
    e.statuses['base.many-forms'] = { stacks: 1 };
    e.concurrentStances = [{ maneuver: STANCES[pick(7)], camp: 'physical', range: 'melee' }, { maneuver: STANCES[pick(7)], camp: 'physical', range: 'melee' }];
    e.combatOwnerCycle = 1;
  }
  if (role === 'target' && pick(12) === 0) e.mechanicBindings = [{ tag: 'defense:poiseProtection', parameters: { multiplier: [0.5, 0.9, 1][pick(3)] } }];
  if (role === 'target' && pick(16) === 0) e.mechanicTags = [];
  if (role === 'source' && pick(14) === 0) e.mechanicBindings = [{ tag: 'combat:stance', parameters: { effectivenessMultiplier: [1, 1.5, 2][pick(3)] } }];
  if (!Object.keys(e.statuses).length) delete e.statuses;
  return e;
}

function receiptSpec(index) {
  const pick = botLcg(0x6d2b79f5 ^ (index * 2654435761));
  const direction = pick(3) === 0 ? 'enemyToPlayer' : 'playerToEnemy';
  const classId = classes[pick(classes.length)];
  const encounter = all[pick(all.length)];
  const seed = w.rngMod.sweepSeed(31000 + index);
  const patch = { source: entityPatch(pick, 'source'), target: entityPatch(pick, 'target') };
  const fields = {};
  if (pick(3) === 0) fields.characterLevel = [1, 2, 5, 9][pick(4)];
  const rules = {};
  if (pick(4) === 0) rules.ward = { enabled: true };
  if (pick(10) === 0) rules.statuses = { vulnerabilityMultiplier: [1.1, 1.5, 2][pick(3)], weakARMultiplier: [0.5, 0.9][pick(2)] };
  if (pick(10) === 0) rules.stance = { effectivenessMultiplier: [1, 1.5, 2][pick(3)] };
  if (pick(10) === 0) rules.smashVsDefensive = { damageMultiplier: [1, 3][pick(2)], blockFraction: [0, 0.5][pick(2)], poiseFraction: [0.25, 1][pick(2)] };
  if (pick(10) === 0) rules.protectionParameters = { 'defense.poiseProtection': { multiplier: [0.5, 0.9][pick(2)] }, 'defense.wardProtection': { multiplier: [0.5, 0.9][pick(2)] } };
  const real = pick(3) === 0;
  const spec = { index, seed, classId, encounterId: encounter.id, direction, patch, fields, rules, base: BASES[pick(BASES.length)], proposal: pick(8) !== 0,
    range: pick(2) ? { mode: 'delete' } : { mode: 'band', band: pick(3) } };
  let carrier = null;
  if (real) {
    const probe = build({ ...spec, patch: {}, fields: {}, rules: {} });
    carrier = realCarrier(probe.combat, direction, pick);
  }
  spec.carrier = withRangeSource(carrier || syntheticCarrier(pick), build({ ...spec, patch: {}, fields: {}, rules: {} }).combat, direction, pick);
  return spec;
}

function attackTags(carrier) { return carrier.tags || []; }

function inputsOf(b, spec) {
  const contact = b.combat.rangeState ? engagement.rangeHitContact(b.combat, b.source, b.target, b.carrier) : null;
  return { rangeContact: contact ? canon(contact) : null, damageType: proposalRuntime.proposalDamageType(b.carrier) ?? null, proposal: canon(proposalRuntime.proposalTargetPlan(b.combat, b.source, b.target, b.carrier)) };
}

function runReceipt(spec) {
  const b = build(spec);
  const inputs = inputsOf(b, spec);
  const receipt = gpc.profileDamageReceipt(b.combat, b.source, b.target, spec.base, b.carrier, { roll: null, proposal: spec.proposal });
  return { inputs, receipt: canon(receipt) };
}

function runApplied(spec, dice) {
  const b = build(spec);
  const { rng, calls } = scriptedRng(b.combat.rng, dice);
  b.combat.rng = rng;
  const m = b.combat.eventLog.length;
  const returned = actions.applyAttackDamage(b.combat, b.source, b.target, spec.base, attackTags(b.carrier), b.carrier);
  // A mounted trigger that asks for a hostile action (the Reaver's Medallion, the Rogue's Coin) is quoted and paid for through the range lineage
  // (engine/combatRangeLineage.js acceptIndependentTrigger), which is WP-12's: the case is kept, marked, and the port skips what follows the trigger.
  const lineage = eventsSince(b.combat, m).some((e) => ['energySpent', 'staminaSpent', 'combatRangeActionUnavailable'].includes(e.type));
  return canon({ lineage, dice, draws: calls.length, returned, source: looks(b.source), target: looks(b.target), events: eventsSince(b.combat, m),
    queue: b.combat.queue.map((a) => ({ effect: a.effect, source: a.source?.id ?? null, target: a.target?.id ?? null })), interrupts: b.combat.gameProfileInterrupts ?? null });
}

function receiptCase(index) {
  const spec = receiptSpec(index);
  let r;
  try { r = runReceipt(spec); } catch (e) { return { refused: e.message }; }
  const applied = {};
  for (const [label, dice] of [['hit', [20]], ['miss', [1]]]) {
    try { applied[label] = runApplied(spec, dice); } catch (e) { applied[label] = { error: e.message }; }
  }
  return { spec: canon(spec), ...r, applied };
}

// ---------------------------------------------------------------------------------------------------------------
// Units
// ---------------------------------------------------------------------------------------------------------------
const OWNER_KINDS = ['physical', 'magical'];

function unitProtection(index) {
  const pick = botLcg(0x2545f491 ^ (index * 2246822519));
  const spec = { seed: w.rngMod.sweepSeed(33000 + index), classId: classes[pick(classes.length)], encounterId: all[pick(all.length)].id, direction: 'playerToEnemy',
    patch: { target: entityPatch(pick, 'target') }, rules: pick(3) === 0 ? { ward: { enabled: pick(2) === 0 } } : {} };
  const kind = OWNER_KINDS[pick(2)];
  const amount = BASES[pick(BASES.length)] + pick(3);
  const b = build(spec);
  let out;
  try { out = canon(fixcore.protectionReceipt(b.combat, b.target, kind, amount)); } catch (e) { out = { error: e.message }; }
  return { kind: 'protectionReceipt', spec: canon(spec), damageKind: kind, amount, result: out };
}

function unitRules(index) {
  const pick = botLcg(0x3c6ef372 ^ (index * 2246822519));
  const spec = { seed: w.rngMod.sweepSeed(34000 + index), classId: classes[pick(classes.length)], encounterId: all[pick(all.length)].id, direction: 'playerToEnemy', patch: { source: entityPatch(pick, 'source') }, rules: {} };
  if (pick(2)) spec.patch.source.mechanicBindings = [{ tag: ['combat:stance', 'combat:accuracy', 'combat:statuses', 'combat:perception', 'combat:actionCosts'][pick(5)], parameters: pick(2) ? {} : { unknown: 1 } }];
  if (pick(4) === 0) spec.patch.source.mechanicTags = ['defense:poiseProtection'];
  if (pick(6) === 0) spec.patch.source.mechanicBindings = [{ tag: 'combat:stance', enabled: false }];
  const b = build(spec);
  let out;
  try { out = canon(fixcore.fixCoreRules(b.combat, b.source, null)); } catch (e) { out = { error: e.message }; }
  let bindings;
  try { bindings = canon(fixcore.protectionBindings(b.combat, b.source).map((x) => ({ id: x.id, mechanicId: x.mechanicId, tag: x.tag, ownerId: x.ownerId, config: x.config }))); } catch (e) { bindings = { error: e.message }; }
  return { kind: 'fixCoreRules', spec: canon(spec), result: out, bindings };
}

function unitMitigation(index) {
  const pick = botLcg(0x1b873593 ^ (index * 2246822519));
  const spec = { seed: w.rngMod.sweepSeed(35000 + index), classId: classes[pick(classes.length)], encounterId: all[pick(all.length)].id, direction: 'playerToEnemy', patch: {}, rules: {} };
  const b = build(spec);
  const names = ['attack', 'smash', 'defend', 'sweep', 'counter', 'prepared', 'casting', 'cast', 'ranged', 'evade', 'neutral', 'staggered', null];
  const incoming = names[pick(names.length)], defending = names[pick(names.length)];
  return { kind: 'stanceMitigation', spec: canon(spec), incoming, defending, result: canon(mitigation.stanceMitigation(b.combat, incoming, defending)) };
}

function unitEffect(index) {
  const pick = botLcg(0x7f4a7c15 ^ (index * 2246822519));
  const which = ['drain', 'stagger', 'poiseDamage', 'block', 'barrier'][pick(5)];
  const side = pick(3) === 0 ? 'enemyToPlayer' : 'playerToEnemy';
  const spec = { seed: w.rngMod.sweepSeed(36000 + index), classId: classes[pick(classes.length)], encounterId: all[pick(all.length)].id, direction: side,
    patch: { target: entityPatch(pick, 'target'), source: entityPatch(pick, 'source') }, rules: pick(3) === 0 ? { ward: { enabled: true } } : {} };
  if (pick(3) === 0) spec.patch.target.combatCounter = { charges: pick(2), serial: 1 + pick(3), expiresOnOwnerCycle: 4, carrier: { combatProfile: { maneuver: 'counter', camp: 'physical' } }, coverage: {}, payload: { hp: 1, poise: 1, ward: 0 } };
  if (pick(4) === 0) spec.fields = { turn: 1 + pick(4) };
  const amount = BASES[pick(BASES.length)] + pick(2);
  const b = build(spec);
  const m = b.combat.eventLog.length;
  let returned = null, error = null, cause = null;
  const card = which === 'block' || which === 'barrier' ? { cardId: 'core.defend', instanceId: 'x1', combatProfile: { camp: pick(3) === 0 ? 'spell' : 'physical', maneuver: 'defend' }, tags: ['camp:physical', 'maneuver:defend'] } : null;
  try {
    if (which === 'drain') returned = actions.drainFixCorePoise(b.combat, b.source, b.target, amount);
    else if (which === 'stagger') { cause = pick(2) ? 'poiseBreak' : null; actions.fixCoreStagger(b.combat, b.target, cause ? { cause } : {}); }
    else if (which === 'poiseDamage') returned = actions.dealPoiseDamage(b.combat, b.target, amount, { source: b.source, cause: 'oracle' });
    else if (which === 'block') returned = actions.gainBlock(b.combat, b.target, amount, card);
    else returned = actions.gainBarrier(b.combat, b.target, amount, card);
  } catch (e) { error = e.message; }
  return { kind: which, spec: canon(spec), amount, card, ...(cause ? { cause } : {}), returned: returned ?? null, ...(error ? { error } : {}), target: looks(b.target), events: eventsSince(b.combat, m),
    queue: b.combat.queue.length, interrupts: b.combat.gameProfileInterrupts ?? null };
}

// Kernel units on inline mechanics: the generic engine (stacking policies, priorities, conditions, formulas, the refusals) beyond the
// seven mechanics the fight registers.
const modifier = (id, extra) => ({ id, phase: 'damage.mitigation', subject: 'target', operation: 'multiply', value: 0.5, stacking: { group: id, mode: 'strongest' }, ...extra });
const mech = (id, tag, modifiers, extra = {}) => ({
  mechanic: { id, tag, version: '1.0.0', family: tag.split(':')[0], label: id, attaches: ['entity', 'gear', 'status', 'card', 'run'], portability: 'portable', parameters: { type: 'object', properties: { k: { type: 'number', default: 0.5, minimum: 0, maximum: 2 }, flag: { type: 'boolean', default: true }, name: { type: 'string', enum: ['a', 'b'] } }, additionalProperties: false }, ...extra },
  interface: { mechanic: id, commands: {}, queries: { parameters: { params: {}, returns: 'object', value: '$config' }, doubled: { params: { x: 'number' }, returns: 'number', value: { f: 'mul', args: ['$x', '$config.k'] } } }, events: {}, requires: [], listens: [] },
  service: { mechanic: id, handlers: [], modifiers },
});
const KERNEL_UNITS = [
  { name: 'strongest multiplier keeps the smallest', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m1', { value: '$config.k', stacking: { group: 'g', mode: 'strongest' } })]), 'k.b': mech('k.b', 'k:b', [modifier('m2', { value: 0.25, stacking: { group: 'g', mode: 'strongest' } })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }, { ownerId: 'o', sourceInstanceId: 's2', kind: 'entity', tags: ['k:b'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: {}, amount: 40, quantity: true } },
  { name: 'strongest add keeps the largest', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m1', { operation: 'add', value: 3, stacking: { group: 'g', mode: 'strongest' } })]), 'k.b': mech('k.b', 'k:b', [modifier('m2', { operation: 'add', value: 7, stacking: { group: 'g', mode: 'strongest' } })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }, { ownerId: 'o', sourceInstanceId: 's2', kind: 'entity', tags: ['k:b'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: {}, amount: 10, quantity: false } },
  { name: 'additive and multiplicative groups in priority order', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('add', { operation: 'add', value: 5, priority: 2, stacking: { group: 'ga', mode: 'additive' } }), modifier('mul', { value: 1.5, priority: 1, stacking: { group: 'gm', mode: 'multiplicative' } })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }, { ownerId: 'o', sourceInstanceId: 's2', kind: 'gear', tags: ['k:a'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: {}, amount: 7, quantity: true } },
  { name: 'dependencies order the rows', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('late', { operation: 'add', value: 1, after: ['early'], stacking: { group: 'g1', mode: 'additive' } }), modifier('early', { value: 3, operation: 'multiply', stacking: { group: 'g2', mode: 'multiplicative' } })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: {}, amount: 5, quantity: true } },
  { name: 'select and when gate a row', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('gated', { select: { damageKind: 'physical' }, when: { all: [{ cmp: ['>', '$owner.pool.value', 0] }, { not: { cmp: ['==', '$owner.state', 'broken'] } }, { any: [{ hasTag: ['$owner', 'x'] }, { hasStatus: ['$owner', 'vulnerable'] }] }] } })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], variants: [
      { phase: 'damage.mitigation', subjectId: 'o', context: { owner: { pool: { value: 2 }, state: 'ok', tags: ['x'] }, damageKind: 'physical' }, amount: 20, quantity: true },
      { phase: 'damage.mitigation', subjectId: 'o', context: { owner: { pool: { value: 2 }, state: 'ok', tags: [] }, damageKind: 'physical' }, amount: 20, quantity: true },
      { phase: 'damage.mitigation', subjectId: 'o', context: { owner: { pool: { value: 0 }, state: 'ok', tags: ['x'] }, damageKind: 'physical' }, amount: 20, quantity: true },
      { phase: 'damage.mitigation', subjectId: 'o', context: { owner: { pool: { value: 2 }, state: 'broken', tags: ['x'] }, damageKind: 'physical' }, amount: 20, quantity: true },
      { phase: 'damage.mitigation', subjectId: 'o', context: { owner: { pool: { value: 2 }, state: 'ok', tags: ['x'] }, damageKind: 'magical' }, amount: 20, quantity: true },
      { phase: 'damage.mitigation', subjectId: 'other', context: { owner: { pool: { value: 2 }, state: 'ok', tags: ['x'] }, damageKind: 'physical' }, amount: 20, quantity: true }] },
  { name: 'a formula value', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('f', { operation: 'add', value: { f: 'mul', args: ['$owner.n', 2] }, stacking: { group: 'g', mode: 'additive' } })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: { owner: { n: 3.5 } }, amount: 1, quantity: false } },
  { name: 'parameters merge over the module and the defaults', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m', { value: '$config.k' })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', mechanicBindings: [{ tag: 'k:a', parameters: { k: 0.2 } }] }], moduleParameters: { 'k.a': { flag: false } }, query: { query: 'parameters' } },
  { name: 'a query with parameters', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], query: { query: 'doubled', params: { x: 5 } } },
  { name: 'upgrade parameters are a third layer', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'gear', mechanicBindings: [{ tag: 'k:a', parameters: { k: 0.2 }, upgradeParameters: { k: 0.9 } }] }], query: { query: 'parameters' } },
  { name: 'refusal: unknown parameter', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', mechanicBindings: [{ tag: 'k:a', parameters: { nope: 1 } }] }], query: { query: 'parameters' } },
  { name: 'refusal: out of range', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', mechanicBindings: [{ tag: 'k:a', parameters: { k: 5 } }] }], query: { query: 'parameters' } },
  { name: 'refusal: wrong type', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', mechanicBindings: [{ tag: 'k:a', parameters: { flag: 'yes' } }] }], query: { query: 'parameters' } },
  { name: 'refusal: not in the enum', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', mechanicBindings: [{ tag: 'k:a', parameters: { name: 'z' } }] }], query: { query: 'parameters' } },
  { name: 'refusal: cannot attach to a kind', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')], { attaches: ['gear'] }) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], query: { query: 'parameters' } },
  { name: 'refusal: unknown behavior tag', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', mechanicTags: ['k:nope'] }], query: { query: 'parameters' } },
  { name: 'refusal: unknown tag in a known family', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:zzz'] }], query: { query: 'parameters' } },
  { name: 'refusal: duplicate binding rows', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', mechanicBindings: [{ tag: 'k:a' }, { tag: 'k:a' }] }], query: { query: 'parameters' } },
  { name: 'refusal: duplicate attachment', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }, { ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], query: { query: 'parameters' } },
  { name: 'refusal: unknown query', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], query: { query: 'nope' } },
  { name: 'refusal: unknown query parameter', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], query: { query: 'doubled', params: { y: 1 } } },
  { name: 'refusal: conflicting stacking policy', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m1', { stacking: { group: 'g', mode: 'strongest' } })]), 'k.b': mech('k.b', 'k:b', [modifier('m2', { stacking: { group: 'g', mode: 'multiplicative' } })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a', 'k:b'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: {}, amount: 5, quantity: true } },
  { name: 'refusal: two exclusive rows', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m1', { stacking: { group: 'g', mode: 'exclusive' } })]), 'k.b': mech('k.b', 'k:b', [modifier('m2', { stacking: { group: 'g', mode: 'exclusive' } })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a', 'k:b'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: {}, amount: 5, quantity: true } },
  { name: 'refusal: an unresolved reference', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m', { value: '$owner.missing.deep' })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: { owner: {} }, amount: 5, quantity: true } },
  { name: 'refusal: a dependency cycle', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('a', { after: ['b'], stacking: { group: 'ga', mode: 'strongest' } }), modifier('b', { after: ['a'], stacking: { group: 'gb', mode: 'strongest' } })]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: {}, amount: 5, quantity: true } },
  { name: 'refusal: unknown phase', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m', { phase: 'nope' })]) }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], call: { phase: 'damage.mitigation', subjectId: 'o', context: {}, amount: 5, quantity: true } },
  { name: 'refusal: a mechanic with no behavior', mechanics: { 'k.a': { ...mech('k.a', 'k:a', []), service: { mechanic: 'k.a', handlers: [], modifiers: [] }, interface: { mechanic: 'k.a', queries: {} } } }, attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', tags: ['k:a'] }], query: { query: 'parameters' } },
  { name: 'a disabled and an inactive attachment compile to nothing', mechanics: { 'k.a': mech('k.a', 'k:a', [modifier('m')]) },
    attachments: [{ ownerId: 'o', sourceInstanceId: 's1', kind: 'entity', mechanicBindings: [{ tag: 'k:a', enabled: false }] }, { ownerId: 'o', sourceInstanceId: 's2', kind: 'entity', tags: ['k:a'], active: false }], bindings: true },
];

function kernelUnit(unit) {
  const mechanics = new Map(Object.entries(unit.mechanics).map(([slug, m]) => [slug, { manifest: m.mechanic, interface: m.interface, service: m.service, model: {}, cases: [] }]));
  const compile = () => kernel.compileBindings({ mechanics, attachments: structuredClone(unit.attachments), moduleParameters: structuredClone(unit.moduleParameters || {}) });
  const run = (fn) => { try { return canon(fn()); } catch (e) { return { error: e.message }; } };
  const out = { name: unit.name };
  if (unit.query) out.result = run(() => { const b = compile(); if (b.length !== 1) throw new Error('one active binding'); return kernel.queryBinding(b[0], unit.query.query, structuredClone(unit.query.params || {})); });
  else if (unit.bindings) out.result = run(() => compile().map((x) => ({ id: x.id, tag: x.tag, config: x.config })));
  else out.result = (unit.variants || [unit.call]).map((call) => run(() => kernel.evaluateModifiers({ bindings: compile(), ...structuredClone(call) })));
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Mechanic cases, replayed on the web's own runtime
// ---------------------------------------------------------------------------------------------------------------
function mechanicCases() {
  const root = join(WEB, SOURCES.core2.mechanicsDir);
  const mechanics = interpreter.loadMechanics(root);
  const runtime = interpreter.createRuntime({ mechanics, ops: interpreter.loadOps() });
  const rows = [];
  for (const [slug, mechanic] of mechanics) {
    if (!(mechanic.cases || []).length) continue;
    const verdict = interpreter.runCases(mechanic, runtime);
    mechanic.cases.forEach((c, i) => {
      const row = { mechanic: slug, name: c.name, index: i, web: verdict[i] };
      if (!verdict[i].ok) throw new Error(`oracle-core2-damage: the web's own case ${slug} / ${c.name} fails: ${verdict[i].error}`);
      const when = c.when || {};
      try {
        if (when.command) { const r = runtime.command(interpreter.createState(c.given || {}), when.command, when.with || {}); row.state = canon({ entities: r.state.entities, piles: r.state.piles, mechanics: r.state.mechanics }); row.events = r.events.map((e) => e.name); }
        else if (when.event) { const r = runtime.emit(interpreter.createState(c.given || {}), when.event, when.with || {}); row.state = canon({ entities: r.state.entities, piles: r.state.piles, mechanics: r.state.mechanics }); row.events = r.events.map((e) => e.name); }
        else if (when.bindingQuery) {
          const { attachments, moduleParameters, query, params } = when.bindingQuery;
          row.value = canon(kernel.queryBinding(kernel.compileBindings({ mechanics: new Map([[slug, mechanic]]), attachments, moduleParameters })[0], query, params));
        } else if (when.modifiers) {
          const { attachments, moduleParameters, ...options } = when.modifiers;
          row.value = kernel.evaluateModifiers({ ...options, bindings: kernel.compileBindings({ mechanics: new Map([[slug, mechanic]]), attachments, moduleParameters }) }).amount;
        }
      } catch (e) { row.error = e.message; }
      rows.push(row);
    });
  }
  return rows;
}

// ---------------------------------------------------------------------------------------------------------------
// Fights
// ---------------------------------------------------------------------------------------------------------------
// The event types a recorded step may emit, and the effect ops a played card may carry (see THE FILTER at the top). Anything else is a later
// work package's: status procs, control and card traits (WP-9), range moves (WP-12). The player's reactions (WP-10) and the enemies' (WP-11) are covered.
const COVERED_EVENTS = new Set([...REACTION_EVENTS, 'playerTurnEnd', 'playerTurnStart', 'enemyTurnStart', 'enemyTurnEnd', 'enemyActorTurnStarted', 'combatContactPrepared',
  'enemyMoveStarted', 'enemyMoveUnavailable', 'attackHit', 'attackMissed', 'damageDealt', 'hpLost', 'blockGained', 'blockBroken', 'persistentPoiseChanged', 'poiseBroken', 'playerStaggered',
  'enemyStaggered', 'combatStanceChanged', 'statusApplied', 'statusExpired', 'statusRemoved', 'deckShuffled', 'cardDrawn', 'cardPreparing', 'cardPlayed', 'cardResolved', 'cardExhausted',
  'energySpent', 'staminaSpent', 'manaSpent', 'enemyDied', 'combatEnd', 'combatGuardBroken', 'perceptionGained']);
const COVERED_OPS = new Set(['damage', 'block', 'gainBarrier']);
// The ops an enemy's move may carry once it is expanded (a pressure status becomes a `buildup`, a charge a `delay`): the damage, Block and Barrier of
// the move itself, Poise damage and a status it puts on a creature. A Counter, a delayed move, a heal, a card it adds or a closing move is a later
// package's.
const ENEMY_OPS = new Set(['damage', 'poiseDamage', 'applyStatus', 'block', 'gainBarrier']);
const bandOf = (combat, enemy) => combat.rangeState?.engagements?.[JSON.stringify([`enemy:${enemy.id}`, 'player:player'].sort())];

// An enemy takes its turn inside the filter when every move it can play this turn (its intent and its planned follow-ups) stays within ENEMY_OPS,
// none charges or counters, and it already stands in melee range of the player (no Stamina to advance, no closing move).
function enemyFits(combat, enemy) {
  if (bandOf(combat, enemy) !== 0) return false;
  const def = combat.registries.enemies.get(enemy.enemyId);
  const ids = [enemy.pendingMove?.moveId, enemy.intent?.moveId, ...(enemy.plannedMoves || []).map((m) => m.moveId)].filter(Boolean);
  for (const id of ids) {
    const move = tactics.expandedEnemyMove(enemy, def.moves[id], id, combat);
    if ((move.delay?.whileCharging?.effects || []).some((effect) => !ENEMY_OPS.has(effect.op))) return false;
    if (tactics.enemyMoveCarrier(enemy, def.moves[id], id, combat).combatProfile.maneuver === 'counter') return false;
    if ((move.effects || []).some((effect) => !ENEMY_OPS.has(effect.op))) return false;
    if (hasHostileHooks(combat, enemy, move.effects || [])) return false;
  }
  return true;
}

// The player's reactions are on. An offer a hit opens is answered by the bot (half the time it plays the first option, half the time it goes Back, from its
// own LCG); a dry run answers with a copy of that LCG, so it takes the same road the real run will.
function dryRun(combat, intent, answers) {
  const r = dispatchAnswering(w, combat, intent, answers);
  if (r.error) return { ok: false, why: `throws: ${r.error}` };
  if (r.why) return { ok: false, why: r.why };
  const types = typesSince(r.copy, r.mark);
  const bad = types.find((t) => !COVERED_EVENTS.has(t));
  if (bad) return { ok: false, why: `event ${bad}` };
  if (r.copy.pendingCardChoice || r.copy.pendingAbilityDiscard) return { ok: false, why: 'pending choice' };
  return { ok: true };
}

const HOSTILE_OPS = new Set(['damage', 'applyStatus', 'buildup', 'poiseDamage', 'wardDamage', 'loseHp', 'stagger', 'arcaneBuildup']);
const FRIENDLY_TARGETS = new Set(['self', 'owner', 'player', 'ally', 'allAllies']);
// engine/combatRangeLineage.js: an owned hook that strikes another creature makes the quote follow its lineage (WP-12). The same test the port
// refuses on (CoreRange.HasHostileHooks): the statuses the actor holds or the action gives itself, the hooks of its stance, mounted rules and statuses.
function hasHostileHooks(combat, owner, effects = []) {
  const ids = new Set(Object.keys(owner.statuses || {}));
  for (const effect of effects) if (effect.op === 'applyStatus' && ['self', 'owner'].includes(effect.target)) ids.add(effect.status);
  let rows;
  for (;;) {
    rows = triggers.rangeHookSources(combat, owner, [...ids]);
    let changed = false;
    for (const { trigger } of rows) for (const effect of trigger.do || [])
      if (effect.op === 'applyStatus' && ['self', 'owner'].includes(effect.target) && !ids.has(effect.status)) { ids.add(effect.status); changed = true; }
    if (!changed) break;
  }
  return rows.some(({ trigger }) => (trigger.do || []).some((effect) => HOSTILE_OPS.has(effect.op) && !FRIENDLY_TARGETS.has(effect.target)));
}

// A card is played inside the filter when its resolved face carries only the ops of COVERED_OPS (no gate, repeat or random target), no choice,
// no variant, no Counter, trait, charge or Ashen Blight cost, nothing listens for it being prepared and the player holds no hostile hook.
function playable(combat, card) {
  const def = expansion.resolveCombatCard(combat, card);
  const ops = (def?.effects || []).map((e) => e.op);
  const tags = def.cardTags || def.tags || [];
  if (!ops.length || !ops.every((op) => COVERED_OPS.has(op))) return false;
  if (def.effects.some((e) => e.if || e.script || e.oncePerTurn || e.repeat !== undefined || e.choose || e.target === 'randomEnemy')) return false;
  if (def.effectVariants?.length || tags.includes('maneuver:counter') || tags.some((t) => /^trait:(counterstrike|impact|flux)$/.test(t)) || def.ashenBlightCost > 0) return false;
  if (Object.keys(combat.player.abilityRiders?.charges || {}).length) return false;
  if (triggers.hasEventTriggers(combat, 'cardPreparing', { cardTags: tags })) return false;
  return !hasHostileHooks(combat, combat.player, def.effects);
}

function fight(seed, classId, encounter, bot, steps) {
  const run = createRun(w, seed, classId);
  const { combat } = createFight(w, run, encounter);
  const pick = botLcg(seed ^ 0x2f6b9d35);
  const answers = answerer(seed ^ 0x5a17c3e1);
  let last = projectFull(combat);
  const start = { events: typesSince(combat, 0), state: last };
  const record = [];
  const step = (intent, extra = {}) => {
    const mark = combat.eventLog.length;
    let error = null;
    try { w.combat.dispatch(combat, structuredClone(intent)); } catch (e) { error = e.message; }
    const state = projectFull(combat);
    record.push({ intent, ...extra, ...(error ? { error } : {}), events: typesSince(combat, mark), delta: delta(last, state) ?? {} });
    last = state;
    return !error;
  };
  // Answers the offers a step left open, one step each, until the fight is no longer paused.
  const settle = () => {
    for (let n = 0; combat.pendingReaction && !combat.result; n++) {
      if (n > 60) return false;
      const answer = answers.next(combat);
      if (!step(answer, { answer: answer.optionId === null ? 'back' : 'play' })) return false;
    }
    return true;
  };
  for (let i = 0; i < steps && !combat.result; i++) {
    if (combat.phase !== 'player') break;
    const options = [];
    if (bot === 'attacks') {
      const targets = combat.enemies.filter((e) => e.alive && bandOf(combat, e) === 0).map((e) => e.id);
      for (const card of combat.piles.hand) {
        if (!playable(combat, card)) continue;
        const def = expansion.resolveCombatCard(combat, card);
        if (def.effects.some((e) => e.op === 'damage' && e.target !== 'enemy') && combat.enemies.some((e) => e.alive && bandOf(combat, e) !== 0)) continue;
        const aimed = def.effects.some((e) => e.op === 'damage');
        for (const targetId of aimed ? targets : [undefined]) {
          const intent = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
          if (dryRun(combat, intent, answers).ok) options.push(intent);
        }
      }
    }
    let intent;
    if (options.length && pick(10) < 8) intent = options[pick(options.length)];
    else {
      intent = { type: 'endTurn', discardIds: [] };
      // The enemies that cannot act inside the filter are staggered for this end turn; the rest take their turn.
      let staggered = [];
      for (const enemy of combat.enemies.filter((e) => e.alive && !e.skipNextTurn)) if (!enemyFits(combat, enemy)) { enemy.skipNextTurn = true; staggered.push(enemy.id); }
      if (!dryRun(combat, intent, answers).ok) {
        for (const enemy of combat.enemies.filter((e) => e.alive)) {
          const probe = detach(w, combat);
          for (const other of probe.enemies) if (other.id !== enemy.id) other.skipNextTurn = true;
          if (!dryRun(probe, intent, answers).ok) staggered.push(enemy.id);
        }
        for (const id of staggered) combat.enemies.find((e) => e.id === id).skipNextTurn = true;
        if (!dryRun(combat, intent, answers).ok) { for (const id of staggered) combat.enemies.find((e) => e.id === id).skipNextTurn = false; break; }
      }
      if (!step(intent, { staggered }) || !settle()) break;
      continue;
    }
    if (!step(intent) || !settle()) break;
  }
  return { seed, classId, encounterId: encounter.id, bot, start, steps: record, ...(bot === 'acts' ? { statics: statics(combat) } : {}) };
}

// ---------------------------------------------------------------------------------------------------------------
// Write
// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const write = (file, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, file), text); return Buffer.byteLength(text); };
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, coveredEvents: [...COVERED_EVENTS].sort(), coveredOps: [...COVERED_OPS].sort(), files: [] };
let total = 0;

// (a) the receipt grid
const cases = [];
let refused = 0;
for (let i = 0; i < CASES; i++) { const c = receiptCase(i); if (c.refused) refused++; else cases.push({ index: i, ...c }); }
for (let f = 0; f * PER_FILE < cases.length; f++) {
  const part = cases.slice(f * PER_FILE, (f + 1) * PER_FILE);
  const file = `receipts-${f}.json`;
  const bytes = write(file, { cases: part });
  total += bytes;
  index.files.push({ file, cases: part.length, bytes });
}
index.refused = refused;

// units and kernel units
const units = [];
for (let i = 0; i < UNITS; i++) {
  const which = i % 5;
  try {
    units.push(which === 0 ? unitProtection(i) : which === 1 ? unitRules(i) : which === 2 ? unitMitigation(i) : unitEffect(i));
  } catch (e) { /* a case the web refuses while building is not recorded */ }
}
const kernelUnits = KERNEL_UNITS.map((u) => ({ ...kernelUnit(u), mechanics: u.mechanics, attachments: u.attachments, moduleParameters: u.moduleParameters ?? null, call: u.call ?? null, variants: u.variants ?? null, query: u.query ?? null, bindings: u.bindings ?? false }));
let bytes = write('units.json', { units, kernel: kernelUnits });
total += bytes; index.units = { file: 'units.json', units: units.length, kernel: kernelUnits.length, bytes };

// (b) the mechanic cases
const rows = mechanicCases();
bytes = write('mechanics.json', { cases: rows });
total += bytes; index.mechanics = { file: 'mechanics.json', cases: rows.length, bytes, mechanics: [...new Set(rows.map((r) => r.mechanic))] };

// (c) real fights
const fightCount = {};
classes.forEach((classId, ci) => {
  const fights = [];
  const pool = all.filter((e) => !/^a[23]_/.test(e.id));
  for (const bot of ['acts', 'attacks']) for (let n = 0; n < FIGHTS * 4; n++) {
    const encounter = pool[(ci * 7 + n * 5 + (bot === 'acts' ? 0 : 3)) % pool.length];
    const seed = w.rngMod.sweepSeed(38000 + ci * 100 + n + (bot === 'acts' ? 0 : 50));
    fights.push(fight(seed, classId, encounter, bot, bot === 'acts' ? 6 : 24));
  }
  const file = `fights-${classId}.json`;
  const bytes2 = write(file, { classId, fights });
  total += bytes2;
  const steps = fights.reduce((s, f) => s + f.steps.length, 0);
  fightCount[classId] = { fights: fights.length, steps };
  index.files.push({ file, classId, fights: fights.length, steps, bytes: bytes2 });
});
index.fights = fightCount;
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-damage: ${cases.length} receipt cases (${refused} refused by the web), ${units.length} units, ${kernelUnits.length} kernel units, ${rows.length} mechanic cases, `
  + `${Object.values(fightCount).reduce((s, f) => s + f.fights, 0)} fights / ${Object.values(fightCount).reduce((s, f) => s + f.steps, 0)} steps, ${(total / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/damage`);
