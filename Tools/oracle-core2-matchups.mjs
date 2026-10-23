#!/usr/bin/env node
// Core 2 matchups oracle (WP-8, docs/design/11-CORE2-PORT-PLAN.md): Counter, Smash and Sweep. Drives the SHIPPED src/engine/combatMatchups.js
// (armCombatCounter, preparedCounter, counterCoverageMatches, previewTacticalAction, beginTacticalAction, completeTacticalAction,
// stancePressureMultiplier, decayStanceRoundStart), src/engine/counterRule.js, src/engine/counterReturn.js, src/engine/combatCardTactics.js
// (prepareTacticalCard) and src/engine/combat.js dispatch at the pinned sha, on real Core 2 fights (createRunCombat for the `default` profile).
//
//   node Tools/oracle-core2-matchups.mjs [--fights 6] [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/matchups/, sorted keys, two runs are byte-identical):
//   fights-<class>.json  real fights. The `cards` bot plays attack, Smash, Sweep and Counter cards (the deck is given a few extra of each, `deckAdd`)
//                        and ends the turn; the enemies take theirs, Counters included. Per step the full combat delta, the event types, the
//                        Counter events with their payloads and every RNG counter.
//   exchanges.json       one declared exchange each, found by a deterministic seed search: a card of a given maneuver against a Counter armed on
//                        an enemy, an enemy attack of a given maneuver against a Counter the player armed, a Poise break that interrupts a Counter,
//                        two attackers against one Counter, a Smash against a spell stance. Same record as a fight, plus the `hand` laid over the
//                        opening hand and the `patch` merged over the fighters.
//   units.json           the pure pieces on their own: the Counter arithmetic, the coverage filter, the prepared Counter and the receipt it
//                        gives, the stance pressure grid, the Counter's Accuracy check, the Guard break, the round-start decay, and the reply a
//                        Counter played as a reaction pays for (WP-10: paidCounterRange and the reservation authorizeCounterRange accepts).
//
// THE FILTER (what a recorded sequence avoids; the C# port refuses these by name and the test asserts it never has to): a step is recorded only
// when the web accepts it (WP-C0: no card, enemy move or event is held back; the filters that did are gone). A card choice or a discard takes the first
// cards offered, one recorded step. The fights leave the player's reactions on (WP-10) and answer every offer with the bot's LCG, one recorded step each;
// the exchanges switch them off to isolate the Counter. What the web refuses is tallied in `rejections` (`--why`).
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, botLcg, detach, projectFull, delta, typesSince, answerer, dispatchAnswering, reactionFilter, pendingAny, choiceAnswer } from './oracle-core2.mjs';
import { whyTally } from './oracle-core2-why.mjs';

const OUT = join(ORACLE_DIR, 'matchups');
const FIGHTS = Number(arg('--fights', '6'));
const w = await loadWeb();
const REJ = whyTally('matchups');
await reactionFilter(w);
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const matchups = await load('src/engine/combatMatchups.js');
const counterRule = await load('src/engine/counterRule.js');
const counterReturn = await load('src/engine/counterReturn.js');
const tactics = await load('src/engine/combatCardTactics.js');
const triggers = await load('src/engine/triggers.js');
const expansion = await load('src/engine/combatExpansion.js');
const reactions = await load('src/engine/combatReactions.js');
const gameProfileCombat = await load('src/engine/gameProfileCombat.js');
const all = encounters(w);
const classes = classIds(w);

// ---------------------------------------------------------------------------------------------------------------
// The filter
// ---------------------------------------------------------------------------------------------------------------
const COUNTER_EVENTS = ['combatCounterArmed', 'combatCounterConsumed', 'combatCounterTriggered', 'combatGuardBroken', 'combatRangeReturnUnavailable'];
const bandOf = (combat, enemy) => combat.rangeState?.engagements?.[JSON.stringify([`enemy:${enemy.id}`, 'player:player'].sort())];

const maneuverOfMove = (combat, enemy, id) => tactics.enemyMoveCarrier(enemy, combat.registries.enemies.get(enemy.enemyId).moves[id], id, combat).combatProfile.maneuver;

// Every enemy takes its turn and every card is played (WP-C0); only a refusal of the web keeps a step out of the record.
const enemyFits = () => true;
const playable = () => true;

// The fights leave the player's reactions on: an offer a hit opens is answered by the bot (half the time it plays the first option, half the time it goes
// Back, from its own LCG) and a dry run answers with a copy of that LCG (ANSWERS, set by `record` for the fight it records). The exchanges switch them off.
let ANSWERS = null;
function dryRun(combat, intent) {
  if (ANSWERS) {
    const r = dispatchAnswering(w, combat, intent, ANSWERS);
    if (r.error) return { ok: false, why: `throws: ${r.error}` };
    if (r.why) return REJ.why(r.why);
    return { ok: true };
  }
  const copy = detach(w, combat);
  try {
    w.combat.dispatch(copy, structuredClone(intent));
    for (let n = 0; (copy.pendingCardChoice || copy.pendingAbilityDiscard) && n < 60; n++) w.combat.dispatch(copy, choiceAnswer(copy));
  } catch (e) { return REJ.why(`throws: ${e.message}`); }
  if (copy.pendingReaction) return REJ.why('pending reaction offer with the reactions off');
  return { ok: true };
}

// model/combatCardEffects.js immediateCardEffects: a Counter plans its target by the support that resolves now, not by the reply it defers.
const immediate = (def) => (maneuverOfCard(def) === 'counter' ? def.effects.filter((e) => !['damage', 'poiseDamage'].includes(e.op)) : def.effects);
const maneuverOfCard = (def) => (def.cardTags || def.tags || []).map((t) => (typeof t === 'string' ? t : t.id)).find((t) => t.startsWith('maneuver:'))?.slice(9) ?? null;

// ---------------------------------------------------------------------------------------------------------------
// Overlay and recording
// ---------------------------------------------------------------------------------------------------------------
const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
function merge(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete target[key];
    else if (isObject(value) && isObject(target[key])) merge(target[key], value);
    else target[key] = structuredClone(value);
  }
  return target;
}

const COUNTER_EVENT_SET = new Set(COUNTER_EVENTS);
/** The events of a step whose payload the port must build too: the Counter's, and the Block it adds. */
// The web decorates every event with the ability riders and the previews the UI and the card predicates read (abilityBefore, abilityEntities,
// cardTargetsAllEnemies, intentPreview, actionPreview): the port builds none of them (no state or rule reads them), so the record leaves them out.
const DECORATION = ['abilityBefore', 'abilityEntities', 'cardTargetsAllEnemies', 'intentPreview', 'actionPreview'];
const payloadEvents = (combat, mark) => combat.eventLog.slice(mark).filter((e) => COUNTER_EVENT_SET.has(e.type) || (e.type === 'blockGained' && e.source === 'counter'))
  .map((e) => Object.fromEntries(Object.entries(e).filter(([key]) => !DECORATION.includes(key))));

/** The fight a record names: the run, the cards laid over it, the patch merged over the fighters. */
function open(spec) {
  const run = createRun(w, spec.seed, spec.classId);
  for (const add of spec.deckAdd || []) run.deck.push({ instanceId: add.instanceId, cardId: add.cardId, upgraded: false });
  const encounter = all.find((e) => e.id === spec.encounterId);
  const { combat } = createFight(w, run, encounter);
  if (spec.hand) combat.piles.hand = spec.hand.map((cardId, i) => ({ instanceId: `mx${i + 1}`, cardId, upgraded: false }));
  if (spec.patch?.player) merge(combat.player, spec.patch.player);
  for (const [index, patch] of Object.entries(spec.patch?.enemies || {})) if (combat.enemies[Number(index)]) merge(combat.enemies[Number(index)], patch);
  return combat;
}

function record(spec, drive, answers = null) {
  const combat = open(spec);
  let last = projectFull(combat);
  const start = { events: typesSince(combat, 0), state: last };
  const steps = [];
  const one = (intent, extra = {}) => {
    const mark = combat.eventLog.length;
    let error = null;
    try { w.combat.dispatch(combat, structuredClone(intent)); } catch (e) { error = e.message; }
    const state = projectFull(combat);
    steps.push({ intent, ...extra, ...(error ? { error } : {}), events: typesSince(combat, mark), payloads: canon(payloadEvents(combat, mark)), delta: delta(last, state) ?? {} });
    last = state;
    return !error;
  };
  // With answers the player's reactions stay on and every offer a step opens is answered at once, one step each.
  const step = (intent, extra = {}) => {
    if (!one(intent, extra)) return false;
    for (let n = 0; (answers ? pendingAny(combat) : combat.pendingCardChoice || combat.pendingAbilityDiscard) && !combat.result; n++) {
      if (n > 60) return false;
      const answer = combat.pendingReaction ? answers.next(combat) : choiceAnswer(combat);
      if (!one(answer, { answer: answer.type === 'chooseReaction' ? (answer.optionId === null ? 'back' : 'play') : 'choose' })) return false;
    }
    return true;
  };
  if (!answers) step({ type: 'setReactions', enabled: false });
  ANSWERS = answers;
  try { drive(combat, step); } finally { ANSWERS = null; }
  return { ...spec, start, steps };
}

/** End the turn inside the filter: the enemies that cannot act in it are staggered for it (the flag a Poise break sets), the rest take their turn. */
function endTurn(combat, step) {
  const intent = { type: 'endTurn', discardIds: [] };
  const staggered = [];
  for (const enemy of combat.enemies.filter((e) => e.alive && !e.skipNextTurn)) if (!enemyFits(combat, enemy)) { enemy.skipNextTurn = true; staggered.push(enemy.id); }
  if (!dryRun(combat, intent).ok) {
    for (const enemy of combat.enemies.filter((e) => e.alive)) {
      const probe = detach(w, combat);
      for (const other of probe.enemies) if (other.id !== enemy.id) other.skipNextTurn = true;
      if (!dryRun(probe, intent).ok && !staggered.includes(enemy.id)) staggered.push(enemy.id);
    }
    for (const id of staggered) combat.enemies.find((e) => e.id === id).skipNextTurn = true;
    if (!dryRun(combat, intent).ok) { for (const id of staggered) combat.enemies.find((e) => e.id === id).skipNextTurn = false; return false; }
  }
  return step(intent, { staggered });
}

/** The play intents the filter lets through for the current hand, each with the targets it can have. */
function options(combat) {
  const out = [];
  const targets = combat.enemies.filter((e) => e.alive).map((e) => e.id);
  for (const card of combat.piles.hand) {
    if (!playable(combat, card)) continue;
    const def = expansion.resolveCombatCard(combat, card);
    const aimed = immediate(def).some((e) => e.target === 'enemy');
    for (const targetId of aimed ? targets : [undefined]) {
      const intent = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
      if (dryRun(combat, intent).ok) out.push({ intent, maneuver: maneuverOfCard(def), cardId: card.cardId });
    }
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Fights: the cards bot
// ---------------------------------------------------------------------------------------------------------------
// Cards given to the deck: attack, Smash, Sweep and Counter, the traits they carry (Counterstrike, Impact, Flux: WP-9) play as the web plays them.
const DECK_POOL = ['core.counter', 'core.counter', 'core.smash', 'base.smash', 'core.reaverSpecial', 'core.sweep', 'core.strike', 'core.defend'];

function fight(seed, classId, encounter, steps) {
  const pick = botLcg(seed ^ 0x2f6b9d35);
  const deckAdd = Array.from({ length: 5 }, (_, i) => ({ instanceId: `mx${i + 1}`, cardId: DECK_POOL[pick(DECK_POOL.length)] }));
  return record({ seed, classId, encounterId: encounter.id, deckAdd }, (combat, step) => {
    for (let i = 0; i < steps && !combat.result; i++) {
      if (combat.phase !== 'player') break;
      const choices = options(combat);
      const special = choices.filter((o) => ['counter', 'smash', 'sweep'].includes(o.maneuver));
      const armed = !!combat.player.combatCounter;
      let intent = null;
      if (special.length && pick(10) < (armed ? 3 : 7)) intent = special[pick(special.length)].intent;
      else if (choices.length && pick(10) < (armed ? 2 : 6)) intent = choices[pick(choices.length)].intent;
      if (intent) { if (!step(intent)) break; continue; }
      if (!endTurn(combat, step)) break;
    }
  }, answerer(seed ^ 0x5a17c3e1));
}

// ---------------------------------------------------------------------------------------------------------------
// Exchanges
// ---------------------------------------------------------------------------------------------------------------
const seeds = (base) => (n) => w.rngMod.sweepSeed(base + n);

/** The first fights, by a deterministic seed search over the classes and encounters, whose opening state satisfies `want`. */
function search({ base, count, classIds: wanted = classes, pool = all, hand, patch, want, need = [], limit = 400 }) {
  const found = [];
  const seedAt = seeds(base);
  for (let n = 0; n < limit && found.length < count; n++) {
    const classId = wanted[n % wanted.length];
    const encounter = pool[(Math.floor(n / wanted.length) * 3 + n) % pool.length];
    const spec = { seed: seedAt(n), classId, encounterId: encounter.id, ...(hand ? { hand } : {}), ...(patch ? { patch } : {}) };
    let combat;
    try { combat = open(spec); } catch { continue; }
    if (need.some((cardId) => { const card = combat.piles.hand.find((c) => c.cardId === cardId); return !card || !playable(combat, card); })) continue;
    const why = want(combat);
    if (why) found.push({ spec, why });
  }
  return found;
}

const intentManeuver = (combat, enemy) => (enemy.intent?.moveId ? maneuverOfMove(combat, enemy, enemy.intent.moveId) : null);
const fitsNow = (combat, enemy) => enemy.alive && enemyFits(combat, enemy);

const exchanges = [];
const addExchange = (kind, label, spec, drive) => {
  try { exchanges.push({ kind, label, ...record(spec, drive) }); } catch (e) { throw new Error(`exchange ${kind} ${label}: ${e.stack}`); }
};
/** Plays a card of the hand inside the filter (playable, and the dry run clean); false when it cannot be. */
function play(combat, step, cardId, targetId) {
  const card = combat.piles.hand.find((c) => c.cardId === cardId);
  if (!card || combat.result || combat.phase !== 'player' || !playable(combat, card)) return false;
  const intent = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
  return dryRun(combat, intent).ok && step(intent);
}

// (a) The player arms a Counter; an enemy attacks it with each maneuver.
for (const counterCard of ['core.counter']) for (const maneuver of ['attack', 'smash', 'sweep', 'ranged']) {
  const hits = search({ base: 51000 + ['attack', 'smash', 'sweep', 'ranged'].indexOf(maneuver) * 50, count: 3, hand: [counterCard, 'core.defend', 'core.strike'], need: [counterCard],
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && intentManeuver(combat, e) === maneuver) ? maneuver : null) });
  for (const { spec, why } of hits) addExchange('playerCounter', `${counterCard} vs ${why}`, spec, (combat, step) => {
    play(combat, step, counterCard);
    endTurn(combat, step);
  });
}

// (b) An enemy arms a Counter (its move); the player attacks it with each maneuver.
for (const attackCard of ['core.strike', 'core.smash', 'core.sweep', 'base.smash', 'core.reaverSpecial']) {
  const hits = search({ base: 52000 + ['core.strike', 'core.smash', 'core.sweep', 'base.smash', 'core.reaverSpecial'].indexOf(attackCard) * 40, count: 4, hand: [attackCard, 'core.defend', 'core.strike', 'core.smash'], need: [attackCard], pool: all.filter((e) => /chain|knight|duelist|mirror|cinder|procession/i.test(e.id)),
    want: (combat) => (combat.enemies.some((e) => e.alive && e.combatCounter && bandOf(combat, e) === 0) ? 'counter' : null) });
  for (const { spec } of hits) addExchange('enemyCounter', `${attackCard} into a Counter`, spec, (combat, step) => {
    const armed = combat.enemies.find((e) => e.alive && e.combatCounter && bandOf(combat, e) === 0);
    play(combat, step, attackCard, armed.id);
    play(combat, step, 'core.strike', armed.id);
    endTurn(combat, step);
  });
}

// (c) A Poise break interrupts the Counter: the covered hit that breaks the pool still answers.
{
  const hits = search({ base: 53000, count: 4, hand: ['core.smash', 'core.strike', 'core.smash'], need: ['core.smash'], pool: all.filter((e) => /chain|knight|duelist|mirror|cinder|procession/i.test(e.id)),
    patch: { enemies: { 0: { persistentPoise: { value: 1 }, block: 0 }, 1: { persistentPoise: { value: 1 }, block: 0 } } },
    want: (combat) => (combat.enemies.some((e) => e.alive && e.combatCounter && bandOf(combat, e) === 0) ? 'counter' : null) });
  for (const { spec } of hits) addExchange('poiseBreak', 'a Counter whose owner is one Poise from breaking', spec, (combat, step) => {
    const armed = combat.enemies.find((e) => e.alive && e.combatCounter && bandOf(combat, e) === 0);
    for (const cardId of ['core.smash', 'core.strike']) play(combat, step, cardId, armed.id);
    endTurn(combat, step);
  });
}

// (c2) The same for the player's Counter: the enemy's covered hit breaks the player's Poise and the reply still comes.
for (const maneuver of ['attack', 'smash']) {
  const hits = search({ base: 53500 + (maneuver === 'smash' ? 100 : 0), count: 3, hand: ['core.counter', 'core.defend', 'core.strike'], need: ['core.counter'], patch: { player: { persistentPoise: { value: 1 } } },
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && intentManeuver(combat, e) === maneuver) ? maneuver : null) });
  for (const { spec, why } of hits) addExchange('poiseBreakPlayer', `a Counter held by a caster one Poise from breaking, against ${why}`, spec, (combat, step) => {
    play(combat, step, 'core.counter');
    endTurn(combat, step);
  });
}

// (d) Two attackers meet one Counter in a single enemy phase: one reply per owner cycle.
{
  const hits = search({ base: 54000, count: 3, hand: ['core.counter', 'core.defend', 'core.strike'], need: ['core.counter'], pool: all.filter((e) => e.enemies.length > 1),
    want: (combat) => (combat.enemies.filter((e) => fitsNow(combat, e) && ['attack', 'smash'].includes(intentManeuver(combat, e))).length > 1 ? 'two' : null) });
  for (const { spec } of hits) addExchange('twoAttackers', 'one Counter, two attackers', spec, (combat, step) => {
    play(combat, step, 'core.counter');
    endTurn(combat, step);
  });
}

// (e) A Smash from an enemy meets the player in a spell stance: its Poise and Ward pooled and split.
{
  // The caster holds a deep Poise pool, so the hit does not break it and the Smash's Poise and Ward still meet the stance of spells.
  const hits = search({ base: 55000, count: 5, hand: ['core.defend'], patch: { player: { combatStance: { maneuver: 'casting', camp: 'spell', range: 'reach' }, persistentPoise: { value: 300, max: 300 } } },
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && intentManeuver(combat, e) === 'smash') ? 'smash' : null) });
  for (const { spec } of hits) addExchange('smashVsSpell', 'an enemy Smash against a caster', spec, (combat, step) => { endTurn(combat, step); });
}

// (f) The player's Smash meets an enemy in a spell stance, and an enemy Sweep meets a Counter.
{
  const hits = search({ base: 56000, count: 4, hand: ['core.smash', 'core.sweep', 'core.strike'], need: ['core.smash'], pool: all, patch: { enemies: { 0: { combatStance: { maneuver: 'casting', camp: 'spell', range: 'reach' } } } },
    want: (combat) => (fitsNow(combat, combat.enemies[0]) && bandOf(combat, combat.enemies[0]) === 0 ? 'caster' : null) });
  for (const { spec } of hits) addExchange('smashIntoSpell', 'the player Smash against a stance of spells', spec, (combat, step) => {
    for (const cardId of ['core.smash', 'core.sweep', 'core.strike']) play(combat, step, cardId, combat.enemies[0].id);
    endTurn(combat, step);
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Units: the pure pieces on their own
// ---------------------------------------------------------------------------------------------------------------
const actionsModule = await load('src/engine/actions.js');
const stanceRulesModule = await load('src/model/stanceRules.js');
const engagementModule = await load('src/engine/combatEngagement.js');
const engagementModel = await load('src/model/combatEngagement.js');
const units = [];
const unitErrors = [];

/** Builds a fight and lays the declared overlay on it (the damage oracle's build). */
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
  return { combat, player, enemy, source, target, carrier: spec.carrier ? structuredClone(spec.carrier) : null };
}
const looks = (entity) => canon({ ...entity, statusControl: undefined, abilityRiders: undefined, ratingSources: undefined, knowledgeAction: undefined, intent: undefined, plannedMoves: undefined, movesHistory: undefined, arcaneExposure: undefined });
const eventsSince = (combat, m) => canon(combat.eventLog.slice(m).map((e) => Object.fromEntries(Object.entries(e).filter(([key]) => !DECORATION.includes(key)))));
function scriptedRng(real, dice) {
  const rng = Object.create(real);
  const calls = [];
  let i = 0;
  rng.int = (stream, min, max) => { calls.push([stream, min, max]); if (i >= dice.length) throw new Error('script exhausted'); return dice[i++]; };
  rng.float = () => { throw new Error('the hit roll must draw with int'); };
  return { rng, calls, used: () => i };
}
const BASE = { seed: w.rngMod.sweepSeed(70000), classId: 'rogue', encounterId: 'loneSoldier' };
const spec = (extra = {}) => ({ ...BASE, direction: 'enemyToPlayer', ...extra });
function unit(kind, unitSpec, fn) {
  try { units.push({ kind, spec: unitSpec, ...fn(build(unitSpec)) }); } catch (e) { unitErrors.push(`${kind}: ${e.message}`); }
}
const pickUnits = botLcg(0x5eed1e55);
const lcg = (n) => pickUnits(n);

// (1) counterReturn: the arithmetic over a table of incoming damage, with the rule variants
const INCOMING = [0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 13, 17, 21, 25, 33, 40, 57, 99, 250, -3, 7.5];
const RULE_VARIANTS = [null, { counter: null }, { counter: { returnFraction: 0.7 } }, { counter: { poiseFraction: 0.33, advantagePoiseFraction: 0.9 } },
  { counter: { advantageIncomingFraction: 0.4, advantageBlockFraction: 0.6, returnFraction: 0.25 } }];
for (const rules of RULE_VARIANTS) {
  unit('counterReturn', spec({ rules: rules ?? undefined }), ({ combat }) => ({
    rows: INCOMING.flatMap((incoming) => [false, true].map((advantaged) => ({ incoming, advantaged, result: canon(counterRule.counterReturn(combat, incoming, advantaged)) }))),
    beats: ['attack', 'smash', 'sweep', 'ranged', 'defend', 'counter', 'casting', 'evade', 'prepared', 'neutral', null].map((maneuver) => ({ maneuver, result: counterRule.counterBeats(combat, maneuver) })),
  }));
}

// (2) counterReturnForReceipt and counterBlockBonus over synthetic receipts
const MANEUVERS = ['attack', 'smash', 'sweep', 'ranged', 'defend', 'counter', 'casting', undefined];
for (let i = 0; i < 60; i++) {
  const n = 1 + lcg(4);
  const receipt = {
    counterEligible: lcg(6) > 0, avoided: lcg(7) === 0,
    covered: Array.from({ length: n }, () => lcg(3) > 0),
    profiles: Array.from({ length: n }, () => (lcg(8) === 0 ? undefined : { maneuver: MANEUVERS[lcg(MANEUVERS.length)] })).map((p) => p ?? {}),
    amounts: Array.from({ length: n }, () => lcg(30)),
  };
  if (lcg(2)) receipt.incomings = Array.from({ length: n }, () => (lcg(5) === 0 ? null : lcg(40)));
  const incoming = lcg(6) === 0 ? undefined : lcg(45);
  unit('receiptReturn', spec(), ({ combat }) => ({
    receipt, incoming: incoming ?? null,
    forReceipt: canon(counterRule.counterReturnForReceipt(combat, receipt)),
    bonus: receipt.covered.map((_, index) => counterRule.counterBlockBonus(combat, receipt, index, incoming)),
  }));
}

// (3) counterBlockGrant with and without a block cap; counterReturnSpent and paidCounterContact
for (const status of [null, 'unbreakable', 'unbreakableUp']) for (const block of [0, 5, 25, 38, 60]) for (const amount of [0, 4, 12, 40]) {
  unit('blockGrant', spec({ patch: { target: { block, ...(status ? { statuses: { [status]: { stacks: 1 } } } : {}) } } }), ({ combat, target }) => ({ amount, result: counterRule.counterBlockGrant(combat, target, amount) }));
}
for (const cycle of [undefined, 0, 1, 2]) for (const owner of [0, 1, 2, undefined]) for (const policy of ['perHit', 'perAction']) for (const manual of [undefined, 1, 2, 'x']) {
  unit('spentPaid', spec({ patch: { target: { combatCounterReturnedCycle: cycle ?? null, combatOwnerCycle: owner ?? null } }, fields: { reactionRules: { contactPolicy: policy } } }), ({ combat, target }) => {
    const carrier = manual === undefined ? {} : { manualCounterSerial: manual };
    return { manual: manual ?? null, serial: 2, spent: counterRule.counterReturnSpent(target), paid: counterRule.paidCounterContact(combat, carrier, { serial: 2 }), paidWithoutCounter: counterRule.paidCounterContact(combat, carrier, undefined) };
  });
}

// (4) counterCoverageMatches: every coverage the game has, and synthetic ones, over a grid of profiles
const PROFILE_FIELDS = { camp: ['physical', 'spell'], school: [undefined, 'fire', 'frost', 'force', 'illusion', 'alteration'], range: ['melee', 'reach', 'ranged'], targeting: ['single', 'area'],
  maneuver: ['attack', 'smash', 'sweep', 'ranged', 'counter', 'casting', 'defend'] };
const syntheticProfile = () => {
  const profile = Object.fromEntries(Object.entries(PROFILE_FIELDS).map(([key, values]) => [key, values[lcg(values.length)]]).filter(([, v]) => v !== undefined));
  if (lcg(6) === 0) profile.counterReturnAttack = true;
  if (lcg(6) === 0) profile.traits = [['conductive', 'grounded', 'projectile', 'mental'][lcg(4)]];
  if (lcg(9) === 0) profile.combatTraits = { [['conductive', 'grounded'][lcg(2)]]: true };
  if (lcg(9) === 0) profile.tags = ['trait:projectile'];
  return profile;
};
const coverages = [];
const rulesCoverage = Object.values(JSON.parse(JSON.stringify(build(spec()).combat.combatExpansionRules.matchups.counter.defaultCoverage)));
coverages.push(...rulesCoverage);
for (const card of w.registries.cards.all()) if (card.counterCoverage) coverages.push(card.counterCoverage);
coverages.push({ camps: ['physical'], requiredTraits: ['projectile'] }, { camps: ['physical', 'spell'], excludedTraits: ['grounded'], targeting: ['single', 'area'] }, { effects: ['status'] }, {}, { maneuvers: ['attack'], ranges: ['melee', 'reach'] },
  { schools: ['fire', 'frost'], requiredTraits: ['conductive'], excludedTraits: ['mental'] });
for (let i = 0; i < 220; i++) {
  const coverage = coverages[i % coverages.length];
  const profile = syntheticProfile();
  const effect = lcg(5) === 0 ? 'status' : lcg(7) === 0 ? 'impact' : 'damage';
  unit('coverage', spec(), () => ({ coverage, profile, effect, omitted: lcg(8) === 0, result: matchups.counterCoverageMatches(coverage, profile, effect), noCoverage: matchups.counterCoverageMatches(null, profile, effect), noProfile: matchups.counterCoverageMatches(coverage, null, effect) }));
}

// (5) the Counter cards: counterCoversAttack, isSpellCounter, armsAsCoreCounter over every card that is a Counter by maneuver
const counterCards = w.registries.cards.all().filter((c) => (c.cardTags || c.tags || []).map((t) => (typeof t === 'string' ? t : t.id)).includes('maneuver:counter'));
const resolvedDef = (combat, id) => expansion.resolveCombatCard(combat, { instanceId: 'mxx', cardId: id, upgraded: false });
for (const card of counterCards) {
  unit('counterCard', spec(), ({ combat, player }) => {
    const def = canon(resolvedDef(combat, card.id));
    const carrier = canon(tactics.tacticalCarrier(resolvedDef(combat, card.id), { cardId: card.id, instanceId: 'mxx', type: 'attack' }, combat, player));
    const profiles = Array.from({ length: 14 }, syntheticProfile);
    return { cardId: card.id, def, carrier,
      covers: profiles.map((profile) => ({ profile, damage: reactions.counterCoversAttack(combat, def, profile, 'damage'), status: reactions.counterCoversAttack(combat, def, profile, 'status') })),
      spellCounter: gameProfileCombat.isSpellCounter(carrier), arms: gameProfileCombat.armsAsCoreCounter(combat, carrier), armsDef: gameProfileCombat.armsAsCoreCounter(combat, def), stance: gameProfileCombat.configuredStance(carrier) };
  });
}

// (6) arming a Counter from a card: the payload the web gives each Counter card (dispatch of the play on a detached copy)
for (const card of counterCards) {
  unit('prepare', spec({ patch: { target: { stamina: 9, maxStamina: 9, energy: 9, mana: 9, maxMana: 9 } } }), ({ combat }) => {
    const def = canon(resolvedDef(combat, card.id));
    combat.piles.hand = [{ instanceId: 'mxx', cardId: card.id, upgraded: false }];
    w.combat.dispatch(combat, { type: 'setReactions', enabled: false });
    const copy = detach(w, combat);
    const needs = def.effects.some((e) => e.target === 'enemy') && !immediate(def).length ? false : immediate(def).some((e) => e.target === 'enemy');
    let error = null;
    try { w.combat.dispatch(copy, { type: 'playCard', cardInstanceId: 'mxx', ...(needs ? { targetId: copy.enemies[0].id } : {}) }); } catch (e) { error = e.message; }
    const counter = copy.player.combatCounter ? canon({ ...copy.player.combatCounter, carrier: undefined }) : null;
    return { cardId: card.id, def, error, counter, stance: canon(copy.player.combatStance ?? null), targetId: needs ? combat.enemies[0].id : null };
  });
}

// (7) previewTacticalAction over armed Counters: the receipt each variation of the Counter gives for each set of contacts
const PREVIEW_CARDS = ['core.counter', 'frostCounter', 'forceCounter', 'barrageCounter', 'lowGuardRiposte', 'fireCounter', 'umbralWard', 'decayCounter'];
const CONTACTS = [
  [{ camp: 'physical', maneuver: 'attack', range: 'melee', targeting: 'single', amount: 6 }],
  [{ camp: 'physical', maneuver: 'smash', range: 'melee', targeting: 'single', amount: 9 }, { camp: 'physical', maneuver: 'smash', range: 'melee', targeting: 'single', amount: 0, effect: 'impact', pressure: 4 }],
  [{ camp: 'spell', maneuver: 'casting', range: 'reach', targeting: 'single', school: 'fire', amount: 5 }, { camp: 'spell', maneuver: 'casting', range: 'reach', targeting: 'single', effect: 'status', pressure: 4, amount: 4 }],
  [{ camp: 'physical', maneuver: 'sweep', range: 'melee', targeting: 'area', amount: 5 }],
  [{ camp: 'physical', maneuver: 'ranged', range: 'reach', targeting: 'single', amount: 7, projectile: true }, { camp: 'physical', maneuver: 'ranged', range: 'ranged', targeting: 'single', amount: 0 }],
];
const VARIATIONS = ['plain', 'spent', 'expired', 'neutral', 'evade', 'moreCards', 'reaction', 'reactionSerial', 'prepared'];
for (const cardId of PREVIEW_CARDS) for (const variation of VARIATIONS) {
  const armed = build(spec({ patch: { target: { stamina: 9, maxStamina: 9, energy: 9, mana: 9, maxMana: 9 } } }));
  armed.combat.piles.hand = [{ instanceId: 'mxx', cardId, upgraded: false }];
  w.combat.dispatch(armed.combat, { type: 'setReactions', enabled: false });
  let played = true;
  try { w.combat.dispatch(armed.combat, { type: 'playCard', cardInstanceId: 'mxx' }); } catch { played = false; }
  const owner = armed.combat.player;
  if (!played || !owner.combatCounter) continue;
  const patch = { combatCounter: canon(owner.combatCounter), combatStance: canon(owner.combatStance), combatOwnerCycle: owner.combatOwnerCycle ?? 0, counters: { cardsPlayedThisCombat: owner.counters.cardsPlayedThisCombat } };
  if (variation === 'spent') patch.combatCounter.charges = 0;
  if (variation === 'expired') patch.combatOwnerCycle = patch.combatCounter.expiresOnOwnerCycle;
  if (variation === 'neutral') patch.combatStance = { maneuver: 'neutral', camp: 'physical', range: 'melee' };
  if (variation === 'evade') patch.combatStance = { maneuver: 'evade', camp: 'physical', range: 'melee' };
  if (variation === 'prepared') patch.combatStance = { maneuver: 'casting', camp: 'spell', range: 'reach' };
  if (variation === 'moreCards') patch.counters.cardsPlayedThisCombat += 1;
  const contacts = CONTACTS[lcg(CONTACTS.length)];
  const carrier = variation === 'reaction' || variation === 'reactionSerial' ? { combatReaction: true, ...(variation === 'reactionSerial' ? { manualCounterSerial: patch.combatCounter.serial } : {}) } : {};
  unit('preview', spec({ patch: { target: patch } }), ({ combat, source, target }) => {
    const receipt = matchups.previewTacticalAction(combat, source, target, carrier, structuredClone(contacts), {});
    return { cardId, variation, carrier, contacts, receipt: canon(receipt) };
  });
}

// (8) stancePressureMultiplier and matchupPoiseMultiplier over the stance grid
const STANCE_MANEUVERS = ['attack', 'smash', 'defend', 'counter', 'sweep', 'casting', 'prepared', 'evade', 'neutral', 'staggered', undefined];
const INCOMING_MANEUVERS = ['attack', 'smash', 'sweep', 'counter', 'ranged', 'casting', 'defend', 'evade', undefined];
for (const defending of STANCE_MANEUVERS) for (const incoming of INCOMING_MANEUVERS) for (const camp of ['physical', 'spell']) {
  const evadeAttempted = lcg(2) === 0;
  const carrier = { cardId: 'oracle.synthetic', combatProfile: { camp, maneuver: incoming, range: 'melee', targeting: incoming === 'sweep' ? 'area' : 'single' }, tags: [], authoredTags: [] };
  if (lcg(9) === 0) carrier.counterReturnAttack = true;
  const hit = lcg(3) === 0 ? null : { evadeAttempted };
  unit('stancePressure', spec({ patch: { target: { combatStance: defending ? { maneuver: defending, camp: lcg(5) === 0 ? 'spell' : 'physical', range: 'melee' } : null } }, carrier }), ({ combat, source, target }) => ({
    carrier, hit,
    result: matchups.stancePressureMultiplier(combat, source, target, carrier, hit),
    explicit: matchups.stancePressureMultiplier(combat, source, target, carrier, { evadeAttempted: true }),
    derived: matchups.stancePressureMultiplier(combat, source, target, carrier),
    poise: matchups.matchupPoiseMultiplier(combat, source, target, carrier),
  }));
}

// (9) counterAttackPreview and resolveCounterAttack: the Accuracy check of a reply, on scripted dice
const REPLY_PROFILES = [{ camp: 'physical', maneuver: 'counter', range: 'melee' }, { camp: 'spell', maneuver: 'counter', range: 'reach' }, { camp: 'physical', maneuver: 'counter', range: 'ranged' }];
for (let i = 0; i < 40; i++) {
  const direction = i % 2 ? 'playerToEnemy' : 'enemyToPlayer';
  const profile = REPLY_PROFILES[lcg(REPLY_PROFILES.length)];
  const carrier = { cardId: 'oracle.reply', counterReturnAttack: lcg(8) > 0, combatProfile: { ...profile, targeting: 'single' }, tags: [], authoredTags: [],
    ...(lcg(2) ? { counterRequiredRange: ['melee', 'reach', 'ranged'][lcg(3)] } : {}) };
  const targetPatch = { statuses: lcg(2) ? { evade: { stacks: 1 + lcg(3) } } : {} };
  if (lcg(4) === 0) targetPatch.nextEvadeMode = { advantage: lcg(2) === 0, disadvantage: lcg(2) === 0 };
  if (lcg(5) === 0) targetPatch.combatEvade = { charges: 1, bonus: 0, advantage: false, disadvantage: false, expiresOnOwnerCycle: 5 };
  const sourcePatch = { combatStance: { maneuver: 'counter', camp: profile.camp, range: profile.range }, statuses: lcg(2) ? { precision: { stacks: 1 + lcg(4) } } : {} };
  const band = lcg(4);
  unit('counterAttack', spec({ direction, carrier, patch: { source: sourcePatch, target: targetPatch } }), ({ combat, source, target }) => {
    for (const key of Object.keys(combat.rangeState.engagements)) combat.rangeState.engagements[key] = band;
    const preview = counterReturn.counterAttackPreview(combat, source, target, structuredClone(carrier));
    const out = { band, preview: canon(preview), scripts: [] };
    if (!preview) return out;
    const dice = [1, 20, Math.max(1, Math.min(20, preview.need)), Math.max(1, Math.min(20, preview.need - 1)), 7, 14];
    const targetSnap = structuredClone(target), sourceSnap = structuredClone(source);
    for (const first of dice) {
      const script = [first, 3 + lcg(15)];
      const real = combat.rng;
      const scripted = scriptedRng(real, preview.mode !== 'normal' && preview.inRange && !preview.bypass ? script : [first]);
      combat.rng = scripted.rng;
      const m = combat.eventLog.length;
      let result, error = null;
      try { result = counterReturn.resolveCounterAttack(combat, source, target, structuredClone(carrier)); } catch (e) { error = e.message; }
      combat.rng = real;
      out.scripts.push(canon({ dice: script, used: scripted.used(), calls: scripted.calls.length, result: result ?? null, error, targetAfter: looks(target), events: eventsSince(combat, m) }));
      combat.eventLog.length = m;
      for (const k of Object.keys(target)) delete target[k]; Object.assign(target, structuredClone(targetSnap));
      for (const k of Object.keys(source)) delete source[k]; Object.assign(source, structuredClone(sourceSnap));
    }
    return out;
  });
}

// (10) the Guard break of a Smash
for (let i = 0; i < 24; i++) {
  const receipt = { smash: lcg(4) > 0 ? true : 1, connected: [true, false, undefined][lcg(3)], guardBefore: [0, 3, 8, undefined][lcg(4)] };
  if (receipt.connected === undefined) delete receipt.connected;
  if (receipt.guardBefore === undefined) delete receipt.guardBefore;
  const carrier = { cardId: 'oracle.smash', combatProfile: { camp: 'physical', maneuver: 'smash', ...(lcg(3) === 0 ? { breakPoiseBonus: [0, 2, 5][lcg(3)] } : {}) } };
  const patch = { block: [0, 0, 4][lcg(3)], ...(lcg(5) === 0 ? { alive: false, hp: 0 } : {}) };
  unit('guardBreak', spec({ direction: lcg(2) ? 'enemyToPlayer' : 'playerToEnemy', patch: { target: patch } }), ({ combat, source, target }) => {
    const verdict = matchups.isSmashGuardBreak(receipt, target);
    const m = combat.eventLog.length;
    combat.queue.length = 0;
    matchups.completeGuardBreak(combat, source, target, carrier, receipt);
    return { receipt, carrier, verdict, none: matchups.isSmashGuardBreak(null, target), events: eventsSince(combat, m), queue: canon(combat.queue.map((a) => ({ effect: a.effect, source: a.source?.id, target: a.target?.id, combatReaction: a.card?.combatReaction }))) };
  });
}

// (11) decayStanceRoundStart: Evade loses one stack at a round boundary
for (let i = 0; i < 12; i++) {
  const turn = [1, 2, 3, 5][lcg(4)];
  const patchOf = () => ({ statuses: { ...(lcg(3) ? { evade: { stacks: lcg(4) } } : {}), ...(lcg(3) === 0 ? { precision: { stacks: 2 } } : {}) } });
  unit('decay', spec({ fields: { turn }, patch: { source: patchOf(), target: { ...patchOf(), ...(lcg(6) === 0 ? { alive: false } : {}) } } }), ({ combat }) => {
    const m = combat.eventLog.length;
    matchups.decayStanceRoundStart(combat);
    return { turn, player: looks(combat.player), enemies: combat.enemies.map(looks), events: eventsSince(combat, m) };
  });
}

// (11b) authorizeCounterRange: what the reply of a Counter costs to reach its attacker, and the reply that cannot pay
{
  const framed = (direction, band, stamina, ward) => {
    const patch = { target: { energy: stamina, stamina, fixCoreStaminaRemaining: stamina, ...(ward ? { combatCounter: { payload: { ward: 2 } } } : {}) } };
    return spec({ direction, patch, classId: 'starseer' });
  };
  // `reach` overrides how far the incoming attack held its attacker from the Counter (its plan's effective band): the reply must close what is left.
  for (const direction of ['enemyToPlayer', 'playerToEnemy']) for (const band of [0, 2]) for (const stamina of [0, 1, 5]) for (const ward of [false, true]) for (const reach of [null, 1, 2]) {
    const unitSpec = framed(direction, band, stamina, ward);
    unit('authorize', unitSpec, ({ combat, source, target }) => {
      for (const key of Object.keys(combat.rangeState.engagements)) combat.rangeState.engagements[key] = band;
      const cardCarrier = (id, owner) => canon(tactics.tacticalCarrier(resolvedDef(combat, id), { cardId: id, instanceId: `mx-${id}`, type: 'attack' }, combat, owner));
      const counterCarrier = cardCarrier('core.counter', target);
      const attackerCarrier = cardCarrier('core.strike', source);
      const plan = engagementModule.quoteRangeActions(combat, source, [{ source, owner: source, target, card: structuredClone(attackerCarrier), effect: { op: 'damage', amount: 5 }, meta: {} }], { route: 'advance' });
      const incoming = canon({ ...attackerCarrier, rangePlan: { ...plan, rootId: 'root', paid: { energy: 0, stamina: 0, mana: 0 } }, rangeContactId: plan.targets[0].contactId });
      if (reach !== null) for (const row of incoming.rangePlan.targets) row.effectiveBand = reach;
      const m = combat.eventLog.length;
      let result, error = null;
      try { result = engagementModule.authorizeCounterRange(combat, target, source, structuredClone(counterCarrier), structuredClone(incoming)); } catch (e) { error = e.message; }
      return { band, stamina, ward, reach, counterCarrier, incoming, error, result: result ? canon(result) : null, events: eventsSince(combat, m) };
    });
  }
}

// (11c) paidCounterRange and the reservation it leaves (WP-10): a Counter played as a reaction pays for its reply's reach when it is played, and the reply is
// accepted as quoted. The incoming hit is the context the reply is priced against (incomingRangeScope sets it for the play).
{
  for (const band of [0, 1, 2]) for (const stamina of [0, 1, 5]) for (const reach of [null, 1, 2]) for (const route of ['advance', 'throw']) {
    const unitSpec = spec({ direction: 'enemyToPlayer', patch: { target: { energy: stamina, stamina, fixCoreStaminaRemaining: stamina } }, classId: 'starseer' });
    unit('paidAuthorize', unitSpec, ({ combat, source, target }) => {
      for (const key of Object.keys(combat.rangeState.engagements)) combat.rangeState.engagements[key] = band;
      const cardCarrier = (id, owner) => canon(tactics.tacticalCarrier(resolvedDef(combat, id), { cardId: id, instanceId: `mx-${id}`, type: 'attack' }, combat, owner));
      const counterCarrier = cardCarrier('core.counter', target);
      const attackerCarrier = cardCarrier('core.strike', source);
      const plan = engagementModule.quoteRangeActions(combat, source, [{ source, owner: source, target, card: structuredClone(attackerCarrier), effect: { op: 'damage', amount: 5 }, meta: {} }], { route: 'advance' });
      const incoming = canon({ ...attackerCarrier, rangePlan: { ...plan, rootId: 'root', paid: { energy: 0, stamina: 0, mana: 0 } }, rangeContactId: plan.targets[0].contactId });
      if (reach !== null) for (const row of incoming.rangePlan.targets) row.effectiveBand = reach;
      const contact = incoming.rangePlan.targets.find((row) => row.contactId === incoming.rangeContactId);
      combat._reactionPlaying = true;
      combat._rangeContext = { sourceKey: incoming.rangePlan.sourceKey, targets: [structuredClone(contact)] };
      combat._rangeIncoming = { rootId: incoming.rangePlan.rootId, contactId: contact.contactId, sourceKey: incoming.rangePlan.sourceKey, targetKey: engagementModel.actorRangeKey(combat, target) };
      let paid = null, paidError = null;
      try { paid = engagementModule.paidCounterRange(combat, target, structuredClone(counterCarrier), { energy: 1, stamina: 1, mana: 0 }, route); } catch (e) { paidError = e.message; }
      delete combat._reactionPlaying; delete combat._rangeContext; delete combat._rangeIncoming;
      const m = combat.eventLog.length;
      let result = null, error = null;
      if (paid) {
        const carrier = { ...structuredClone(counterCarrier), rangeCounterReservation: structuredClone(paid.reservation) };
        try { result = engagementModule.authorizeCounterRange(combat, target, source, carrier, structuredClone(incoming)); } catch (e) { error = e.message; }
      }
      return { band, stamina, reach, route, counterCarrier, incoming, paid: paid ? canon(paid) : null, paidError, error, result: result ? canon(result) : null, events: eventsSince(combat, m) };
    });
  }
}

// (12) the wardDamage opcode: Ward is off, so it drains Poise, with the stance pressure and Piercing's edge
for (let i = 0; i < 16; i++) {
  const carrier = { cardId: 'oracle.ward', combatProfile: { camp: lcg(2) ? 'spell' : 'physical', maneuver: ['casting', 'attack', 'counter'][lcg(3)], range: 'melee', damageType: [null, 'piercing', 'fire'][lcg(3)] }, ...(lcg(4) === 0 ? { combatWardEdgeApplied: true } : {}) };
  const amount = 1 + lcg(12);
  const defending = ['counter', 'attack', 'defend', 'casting', 'smash'][lcg(5)];
  unit('wardDamage', spec({ direction: 'playerToEnemy', carrier, patch: { target: { combatStance: { maneuver: defending, camp: 'physical', range: 'melee' }, persistentPoise: { value: 3 + lcg(12) } } } }), ({ combat, source, target }) => {
    const m = combat.eventLog.length;
    combat.queue.length = 0;
    const action = { effect: { op: 'wardDamage', target: 'enemy', amount }, source, owner: source, target, card: carrier, meta: {} };
    let error = null;
    try { actionsModule.executeAction(combat, action); } catch (e) { error = e.message; }
    return { carrier, amount, error, target: looks(target), events: eventsSince(combat, m), queue: combat.queue.length };
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Write
// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const write = (file, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, file), text); return Buffer.byteLength(text); };
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, counterEvents: COUNTER_EVENTS, deckPool: DECK_POOL, files: [] };
let total = 0;

const fightCount = {};
const seen = { counterArmed: 0, counterConsumed: 0, counterTriggered: 0, guardBroken: 0, rangeReturnUnavailable: 0 };
const tally = (steps) => { for (const s of steps) for (const t of s.events) { if (t === 'combatCounterArmed') seen.counterArmed++; if (t === 'combatCounterConsumed') seen.counterConsumed++; if (t === 'combatCounterTriggered') seen.counterTriggered++; if (t === 'combatGuardBroken') seen.guardBroken++; if (t === 'combatRangeReturnUnavailable') seen.rangeReturnUnavailable++; } };
classes.forEach((classId, ci) => {
  const fights = [];
  for (let n = 0; n < FIGHTS; n++) {
    const encounter = all[(ci * 5 + n * 7 + 1) % all.length];
    const f = fight(w.rngMod.sweepSeed(61000 + ci * 100 + n), classId, encounter, 26);
    tally(f.steps);
    fights.push(f);
  }
  const file = `fights-${classId}.json`;
  const bytes = write(file, { classId, fights });
  total += bytes;
  fightCount[classId] = { fights: fights.length, steps: fights.reduce((s, f) => s + f.steps.length, 0) };
  index.files.push({ file, classId, fights: fights.length, steps: fightCount[classId].steps, bytes });
});
index.fights = fightCount;

for (const e of exchanges) tally(e.steps);
let bytes = write('exchanges.json', { exchanges });
total += bytes;
index.exchanges = { file: 'exchanges.json', count: exchanges.length, steps: exchanges.reduce((s, e) => s + e.steps.length, 0), kinds: [...new Set(exchanges.map((e) => e.kind))], bytes };
bytes = write('units.json', { units });
total += bytes;
index.units = { file: 'units.json', count: units.length, kinds: [...new Set(units.map((u) => u.kind))], bytes, errors: unitErrors };
index.seen = seen;
index.rejections = REJ.finish();
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-matchups: ${Object.values(fightCount).reduce((s, f) => s + f.fights, 0)} fights / ${Object.values(fightCount).reduce((s, f) => s + f.steps, 0)} steps, ${exchanges.length} exchanges / ${index.exchanges.steps} steps, ${JSON.stringify(seen)}, ${(total / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/matchups`);
