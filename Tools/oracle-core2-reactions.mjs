#!/usr/bin/env node
// Core 2 reactions oracle (WP-10, docs/design/11-CORE2-PORT-PLAN.md): the player's reaction stack. Drives the SHIPPED src/engine/combatReactions.js
// (beforeReactionAction, optionsFor, resolveReactionStack, answerReaction, restoreReactionQueue, finishReactions, setReactionsEnabled),
// src/engine/combatMatchups.js (counterCoversAttack) and src/engine/combat.js dispatch (chooseReaction, setReactions, the enemy phase resumed by its
// reactionCursor) at the pinned sha, on real Core 2 fights (createRunCombat for the configured `default` profile).
//
//   node Tools/oracle-core2-reactions.mjs [--bot reactions] [--fights 6] [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/reactions/, sorted keys, two runs are byte-identical):
//   fights-<class>.json  real fights. The `reactions` bot plays attack, Smash, Sweep, Defend and Counter cards (the deck is given a few extra of
//                        each, `deckAdd`), ends the turn, and ANSWERS every reaction offer the enemies' hits open: from its own LCG, half the time
//                        it plays the first option of the offer, half the time it goes Back. Per step: the intent, the answer, the offer the step
//                        paused on (its id, group, owner, source and every option: id, card, Stamina, Mana, the play), the full combat delta (the
//                        paused state included: pendingReaction, reactionCursor, reactionResume, reactionHandCleanup, reactionStack), the event
//                        types, the payload of every reaction and Counter event and every RNG counter.
//   exchanges.json       declared exchanges found by a deterministic seed search: a Defend offered against each maneuver, a Counter offered against
//                        a covered hit, a multi-hit move (one offer per hit), the switch off with a card in the hand, no Stamina, a locked hand,
//                        the offer of a hit that kills, Back after Back. Same record as a fight, plus the `hand` laid over the opening hand and the
//                        `patch` merged over the fighters.
//
// THE FILTER (what a recorded sequence avoids; the C# port refuses these by name and the test asserts it never has to): a step is recorded only when
// its dry run on a detached copy (a) throws nothing, (b) emits only event types in COVERED_EVENTS, (c) leaves no card choice, and (d) every card it
// plays has only effects in COVERED_OPS. An end turn is dry-run to its end: the same bot answers every offer it opens, from a copy of its LCG.
// An enemy whose move would break (a) to (c) is marked staggered for that end turn (the flag a Poise break sets), as the WP-7 oracle does. Enemy
// reactions, delayed moves and the draws on the enemyReaction stream (WP-11) are inside the filter.
//
// What the record leaves out: `intentPreview` of an offer and `range` of an option (the previews the UI reads; no rule reads them), and the queue
// a paused card holds (`reactionResume.queue` is recorded as its length).
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, detach, projectFull, delta, typesSince } from './oracle-core2.mjs';

const BOT = arg('--bot', 'reactions');
if (!['reactions', 'range'].includes(BOT)) { console.error(`oracle-core2-reactions: unknown --bot '${BOT}' (reactions, range)`); process.exit(1); }
const RANGE = BOT === 'range';
const OUT = join(ORACLE_DIR, RANGE ? 'range' : 'reactions');
const FIGHTS = Number(arg('--fights', '8'));
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const triggers = await load('src/engine/triggers.js');
const expansion = await load('src/engine/combatExpansion.js');
const tactics = await load('src/engine/combatCardTactics.js');
const policy = await load('src/model/autoCombatPolicy.js');
const all = encounters(w);
const classes = classIds(w);

// ---------------------------------------------------------------------------------------------------------------
// The filter
// ---------------------------------------------------------------------------------------------------------------
const REACTION_EVENTS = ['combatReactionOffered', 'combatReactionChosen', 'combatReactionStacked', 'combatReactionUnavailable', 'combatReactionFizzled', 'combatEnemyReactionStacked'];
const COUNTER_EVENTS = ['combatCounterArmed', 'combatCounterConsumed', 'combatCounterTriggered', 'combatGuardBroken', 'combatRangeReturnUnavailable'];
const COVERED_EVENTS = new Set(['playerTurnEnd', 'playerTurnStart', 'enemyTurnStart', 'enemyTurnEnd', 'enemyActorTurnStarted', 'combatContactPrepared',
  'enemyMoveStarted', 'enemyMoveUnavailable', 'attackHit', 'attackMissed', 'damageDealt', 'hpLost', 'blockGained', 'blockBroken', 'persistentPoiseChanged', 'poiseBroken', 'playerStaggered',
  'enemyStaggered', 'combatStanceChanged', 'statusApplied', 'statusExpired', 'statusRemoved', 'deckShuffled', 'cardDrawn', 'cardPreparing', 'cardPlayed', 'cardResolved', 'cardExhausted',
  'energySpent', 'staminaSpent', 'manaSpent', 'enemyDied', 'combatEnd', 'perceptionGained', 'combatHitStarted', 'controlRecovered', 'controlResolveScheduled', 'energyGained', 'healed',
  'manaRestored', 'relicTriggered', 'statusGaugeFilled', 'statusPressure', 'statusRecoveryRolled', 'combatEngagementMoved', 'combatRangeActionUnavailable', 'characterMoved',
  'cardAdded', 'barrierGained', 'barrierBroken', 'statusPoiseBreak', ...REACTION_EVENTS, ...COUNTER_EVENTS]);
const COVERED_OPS = new Set(['damage', 'block', 'gainBarrier', 'poiseDamage', 'wardDamage', 'moveEngagement', 'applyStatus']);
const ENEMY_OPS = new Set(['damage', 'poiseDamage', 'wardDamage', 'applyStatus', 'block', 'gainBarrier', 'moveEngagement', 'addCard']);
const bandOf = (combat, enemy) => combat.rangeState?.engagements?.[JSON.stringify([`enemy:${enemy.id}`, 'player:player'].sort())];

const tagsOf = (def) => (def.cardTags || def.tags || []).map((t) => (typeof t === 'string' ? t : t.id));
const maneuverOfMove = (combat, enemy, id) => tactics.enemyMoveCarrier(enemy, combat.registries.enemies.get(enemy.enemyId).moves[id], id, combat).combatProfile.maneuver;

// An enemy takes its turn inside the filter when every move it can play this turn (its intent, its planned follow-ups and the move that charges) stays within
// ENEMY_OPS. Closing the distance (Stamina to advance, the closing move) and the hooks that strike are inside it (WP-12).
function enemyFits(combat, enemy) {
  const def = combat.registries.enemies.get(enemy.enemyId);
  const ids = [enemy.pendingMove?.moveId, enemy.intent?.moveId, ...(enemy.plannedMoves || []).map((m) => m.moveId), def.rangeClosingMove].filter(Boolean);
  for (const id of ids) {
    const move = tactics.expandedEnemyMove(enemy, def.moves[id], id, combat);
    if ((move.delay?.whileCharging?.effects || []).some((effect) => !ENEMY_OPS.has(effect.op))) return false;
    if ((move.effects || []).some((effect) => !ENEMY_OPS.has(effect.op))) return false;
  }
  return true;
}

// model/combatCardEffects.js immediateCardEffects: a Counter plans its target by the support that resolves now, not by the reply it defers.
const immediate = (def) => (tagsOf(def).includes('maneuver:counter') ? def.effects.filter((e) => !['damage', 'poiseDamage'].includes(e.op)) : def.effects);

// A card is played inside the filter when its resolved face carries only the ops of COVERED_OPS (no gate, repeat or random target), no choice, no
// variant, no Ashen Blight cost, nothing listens for it being prepared and the player holds no hostile hook.
function playable(combat, card) {
  const def = expansion.resolveCombatCard(combat, card);
  const ops = (def?.effects || []).map((e) => e.op);
  const tags = tagsOf(def);
  if (!ops.length || !ops.every((op) => COVERED_OPS.has(op))) return false;
  if (def.effects.some((e) => e.if || e.script || e.oncePerTurn || e.repeat !== undefined || e.choose || e.target === 'randomEnemy')) return false;
  if (def.effectVariants?.length || def.ashenBlightCost > 0) return false;
  if (Object.keys(combat.player.abilityRiders?.charges || {}).length) return false;
  if (triggers.hasEventTriggers(combat, 'cardPreparing', { cardTags: tags })) return false;
  return true;
}

// ---------------------------------------------------------------------------------------------------------------
// The bot's LCG (own state, never the engine's; cloneable so an end turn can be dry-run with the answers it will give)
// ---------------------------------------------------------------------------------------------------------------
function makeLcg(seed) {
  let s = (seed >>> 0) || 1;
  const next = (n) => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return n <= 1 ? 0 : (s >>> 8) % n; };
  next.clone = () => makeLcg(s);
  return next;
}

// ---------------------------------------------------------------------------------------------------------------
// Recording
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

// The web decorates every event with the ability riders and the previews the UI and the card predicates read (abilityBefore, abilityEntities,
// cardTargetsAllEnemies, intentPreview, actionPreview): the port builds none of them (no state or rule reads them), so the record leaves them out.
const DECORATION = ['abilityBefore', 'abilityEntities', 'cardTargetsAllEnemies', 'intentPreview', 'actionPreview'];
const PAYLOAD_EVENTS = new Set([...REACTION_EVENTS, ...COUNTER_EVENTS]);
const payloadEvents = (combat, mark) => combat.eventLog.slice(mark).filter((e) => PAYLOAD_EVENTS.has(e.type) || (e.type === 'blockGained' && e.source === 'counter'))
  .map((e) => Object.fromEntries(Object.entries(e).filter(([key]) => !DECORATION.includes(key))));

/** The combat as the port projects it (the offer without its previews, the paused card's queue as its length: see projectFull). */
const view = projectFull;

/** The offer the fight is paused on, as the test checks it. */
const offerOf = (combat) => (combat.pendingReaction ? canon({
  id: combat.pendingReaction.id, groupKey: combat.pendingReaction.groupKey, ownerId: combat.pendingReaction.ownerId, sourceId: combat.pendingReaction.sourceId,
  options: combat.pendingReaction.options.map((o) => ({ id: o.id, cardId: o.cardId, name: o.name, staminaCost: o.staminaCost, manaCost: o.manaCost, choiceName: o.choiceName, play: o.play, ...(o.incoming ? { incoming: o.incoming } : {}) })),
}) : null);

/** The fight a record names: the run, the cards laid over it, the patch merged over the fighters. */
function open(spec) {
  const run = createRun(w, spec.seed, spec.classId);
  if (spec.deckRemove) run.deck = run.deck.filter((card) => !spec.deckRemove.includes(card.cardId));
  for (const add of spec.deckAdd || []) run.deck.push({ instanceId: add.instanceId, cardId: add.cardId, upgraded: false });
  const encounter = all.find((e) => e.id === spec.encounterId);
  const { combat } = createFight(w, run, encounter);
  if (spec.hand) combat.piles.hand = spec.hand.map((cardId, i) => ({ instanceId: `mx${i + 1}`, cardId, upgraded: false }));
  if (spec.fields) merge(combat, spec.fields);
  if (spec.patch?.player) merge(combat.player, spec.patch.player);
  for (const [index, patch] of Object.entries(spec.patch?.enemies || {})) if (combat.enemies[Number(index)]) merge(combat.enemies[Number(index)], patch);
  return combat;
}

const wiredCounters = {};
function record(spec, drive) {
  const combat = open(spec);
  let last = view(combat);
  const start = { events: typesSince(combat, 0), state: last };
  const steps = [];
  const step = (intent, extra = {}) => {
    const mark = combat.eventLog.length;
    let error = null;
    try { w.combat.dispatch(combat, structuredClone(intent)); } catch (e) { error = e.message; }
    const state = view(combat);
    const events = typesSince(combat, mark);
    for (const t of events) wiredCounters[t] = (wiredCounters[t] || 0) + 1;
    steps.push({ intent, ...extra, ...(error ? { error } : {}), events, payloads: canon(payloadEvents(combat, mark)), offer: offerOf(combat), delta: delta(last, state) ?? {} });
    last = state;
    return !error;
  };
  for (const intent of spec.setup || []) step(intent);
  drive(combat, step);
  return { ...spec, start, steps };
}

// ---------------------------------------------------------------------------------------------------------------
// The dry runs
// ---------------------------------------------------------------------------------------------------------------
const rejected = {};
const why = (reason) => { const key = reason.replace(/[0-9]+/g, 'N').slice(0, 90); rejected[key] = (rejected[key] || 0) + 1; return { ok: false, why: reason }; };

/** Why the offer the fight is paused on is outside the filter: a reaction card of the hand the port could not play to build it (WP-12, cardPreparing), else null. */
function offerOutside(combat) {
  for (const card of combat.piles.hand) {
    const def = expansion.resolveCombatCard(combat, card);
    const tags = tagsOf(def);
    if (!tags.includes('trait:reaction')) continue;
    if (triggers.hasEventTriggers(combat, 'cardPreparing', { cardTags: tags })) return 'cardPreparing listener at an offer';
  }
  return null;
}

/**
 * The intent played to its end on a detached copy with the answers the bot will give (from a copy of its LCG): nothing outside the filter happens.
 * At every offer the player holds no hostile hook (the range lineage of the candidates that are played to build it, WP-12).
 */
function dry(combat, intent, chooser) {
  const copy = detach(w, combat);
  const answers = chooser.clone();
  const mark = copy.eventLog.length;
  try {
    w.combat.dispatch(copy, structuredClone(intent));
    for (let n = 0; copy.pendingReaction; n++) {
      if (n > 60) return why('too many offers');
      const outside = offerOutside(copy);
      if (outside) return why(outside);
      w.combat.dispatch(copy, answers.next(copy));
    }
  } catch (e) { return why(`throws: ${e.message}`); }
  const types = typesSince(copy, mark);
  const bad = types.find((t) => !COVERED_EVENTS.has(t));
  if (bad) return why(`event ${bad}`);
  if (copy.pendingCardChoice || copy.pendingAbilityDiscard) return why('pending choice');
  return { ok: true };
}

/** The bot's answers: half Play the first option, half Back, from its LCG. */
function lcgChooser(lcg) {
  return { clone: () => lcgChooser(lcg.clone()), next(combat) {
    const pending = combat.pendingReaction;
    const play = lcg(2) === 0 && pending.options.length > 0;
    return { type: 'chooseReaction', offerId: pending.id, optionId: play ? pending.options[0].id : null };
  } };
}
/** A scripted exchange's answers (`play` or `back`), the last one repeating. */
function scriptChooser(answers, at = 0) {
  let n = at;
  return { clone: () => scriptChooser(answers, n), next(combat) {
    const pending = combat.pendingReaction;
    const answer = answers[Math.min(n++, answers.length - 1)];
    return { type: 'chooseReaction', offerId: pending.id, optionId: answer === 'play' && pending.options.length ? pending.options[0].id : null };
  } };
}

const dryRun = dry;
const dryTurn = (combat, chooser) => dry(combat, { type: 'endTurn', discardIds: [] }, chooser);

/** Ends the turn inside the filter: the enemies that cannot act in it are staggered for it, the rest take their turn, and every offer is answered. */
function endTurn(combat, step, lcg) {
  const intent = { type: 'endTurn', discardIds: [] };
  const staggered = [];
  for (const enemy of combat.enemies.filter((e) => e.alive && !e.skipNextTurn)) if (!enemyFits(combat, enemy)) { enemy.skipNextTurn = true; staggered.push(enemy.id); }
  if (!dryTurn(combat, lcgChooser(lcg)).ok) {
    for (const enemy of combat.enemies.filter((e) => e.alive)) {
      const probe = detach(w, combat);
      for (const other of probe.enemies) if (other.id !== enemy.id) other.skipNextTurn = true;
      if (!dryTurn(probe, lcgChooser(lcg)).ok && !staggered.includes(enemy.id)) staggered.push(enemy.id);
    }
    for (const id of staggered) combat.enemies.find((e) => e.id === id).skipNextTurn = true;
    if (!dryTurn(combat, lcgChooser(lcg)).ok) { for (const id of staggered) combat.enemies.find((e) => e.id === id).skipNextTurn = false; return false; }
  }
  if (!step(intent, { staggered })) return false;
  return settle(combat, step, lcg);
}

/** Answers the offers a step left open, one step each, until the fight is no longer paused. */
function settle(combat, step, lcg) {
  for (let n = 0; combat.pendingReaction && !combat.result; n++) {
    if (n > 60) return false;
    const intent = lcgChooser(lcg).next(combat);
    if (!step(intent, { answer: intent.optionId === null ? 'back' : 'play' })) return false;
  }
  return true;
}

/** The play intents the filter lets through for the current hand, each with the targets it can have. */
function options(combat, lcg) {
  const out = [];
  const targets = combat.enemies.filter((e) => e.alive).map((e) => e.id);
  for (const card of combat.piles.hand) {
    if (!playable(combat, card)) continue;
    const def = expansion.resolveCombatCard(combat, card);
    const aimed = immediate(def).some((e) => e.target === 'enemy');
    for (const targetId of aimed ? targets : [undefined]) {
      const base = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
      // The range bot takes the route the card's own preview offers, alternating between the two.
      const routes = RANGE ? (() => { try { return w.combat.previewCard(combat, card.instanceId, targetId).range?.routes || []; } catch { return []; } })().filter((r) => r.available).map((r) => r.id) : ['advance'];
      for (const route of routes.length ? routes : ['advance']) {
        const intent = route === 'advance' ? base : { ...base, rangeRoute: route };
        if (dryRun(combat, intent, lcgChooser(lcg)).ok) out.push({ intent, reaction: tagsOf(def).includes('trait:reaction'), cardId: card.cardId });
      }
    }
  }
  if (RANGE) for (const cell of ['A1', 'B1', 'C1', 'D1']) {
    const intent = { type: 'moveCharacter', cell, settings: MOVE_SETTINGS };
    if (dryRun(combat, intent, lcgChooser(lcg)).ok) out.push({ intent, reaction: false, cardId: null });
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Fights: the reactions bot
// ---------------------------------------------------------------------------------------------------------------
// Cards given to the deck: the reaction cards (Defend, Counter) and the attacks the player answers with.
const MOVE_SETTINGS = { 'gameConfig.presentation.movementEnabled': true };
const DECK_POOL = RANGE ? ['core.defend', 'core.counter', 'core.counter', 'core.strike', 'core.smash', 'core.sweep', 'core.advance', 'core.advance', 'core.closingDodge', 'core.backstep']
  : ['core.defend', 'core.defend', 'core.counter', 'core.counter', 'core.strike', 'core.smash', 'core.sweep', 'core.defend'];

function fight(seed, classId, encounter, steps, fields) {
  const lcg = makeLcg(seed ^ 0x2f6b9d35);
  const deckRemove = undefined;
  const pool = DECK_POOL;
  const deckAdd = Array.from({ length: 6 }, (_, i) => ({ instanceId: `mx${i + 1}`, cardId: pool[lcg(pool.length)] }));
  return record({ seed, classId, encounterId: encounter.id, ...(deckRemove ? { deckRemove } : {}), ...(fields ? { fields } : {}), deckAdd }, (combat, step) => {
    for (let i = 0; i < steps && !combat.result; i++) {
      if (combat.pendingReaction) { if (!settle(combat, step, lcg)) break; continue; }
      if (combat.phase !== 'player') break;
      const choices = options(combat, lcg);
      // The reaction cards are kept for the enemies' hits most of the time; the others are played on the turn.
      const spend = choices.filter((o) => !o.reaction);
      const pool = spend.length && lcg(10) < 8 ? spend : choices;
      if (pool.length && lcg(10) < 6) { if (!step(pool[lcg(pool.length)].intent)) break; continue; }
      if (!endTurn(combat, step, lcg)) break;
    }
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Exchanges
// ---------------------------------------------------------------------------------------------------------------
const seeds = (base) => (n) => w.rngMod.sweepSeed(base + n);

/** The first fights, by a deterministic seed search over the classes and encounters, whose opening state satisfies `want`. */
function search({ base, count, classIds: wanted = classes, pool = all, hand, patch, fields, setup, want, need = [], limit = 600 }) {
  const found = [];
  const seedAt = seeds(base);
  for (let n = 0; n < limit && found.length < count; n++) {
    const classId = wanted[n % wanted.length];
    const encounter = pool[(Math.floor(n / wanted.length) * 3 + n) % pool.length];
    const spec = { seed: seedAt(n), classId, encounterId: encounter.id, ...(hand ? { hand } : {}), ...(patch ? { patch } : {}), ...(fields ? { fields } : {}), ...(setup ? { setup } : {}) };
    let combat;
    try { combat = open(spec); for (const intent of setup || []) w.combat.dispatch(combat, structuredClone(intent)); } catch { continue; }
    if (need.some((cardId) => { const card = combat.piles.hand.find((c) => c.cardId === cardId); return !card || !playable(combat, card); })) continue;
    const verdict = want(combat);
    if (verdict) found.push({ spec, why: verdict });
  }
  return found;
}

const intentManeuver = (combat, enemy) => (enemy.intent?.moveId ? maneuverOfMove(combat, enemy, enemy.intent.moveId) : null);
const intentHits = (combat, enemy) => {
  const def = combat.registries.enemies.get(enemy.enemyId);
  const move = enemy.intent?.moveId ? tactics.expandedEnemyMove(enemy, def.moves[enemy.intent.moveId], enemy.intent.moveId, combat) : null;
  return (move?.damage != null ? (typeof move.hits === 'number' ? move.hits : 1) : 0) + (move?.effects || []).filter((e) => e.op === 'damage').reduce((sum, e) => sum + (typeof e.hits === 'number' ? e.hits : 1), 0);
};
const fitsNow = (combat, enemy) => enemy.alive && enemyFits(combat, enemy);

const exchanges = [];
const addExchange = (kind, label, spec, drive) => {
  try {
    const exchange = { kind, label, ...record(spec, (combat, step) => drive(combat, step, makeLcg(spec.seed ^ 0x7a31c5e9))) };
    if (exchange.steps.length) exchanges.push(exchange);
  } catch (e) { throw new Error(`exchange ${kind} ${label}: ${e.stack}`); }
};
/** Ends the turn and answers every offer with the given answers in turn (`play` / `back`), the last one repeating. */
function scripted(combat, step, answers) {
  const intent = { type: 'endTurn', discardIds: [] };
  const staggered = [];
  for (const enemy of combat.enemies.filter((e) => e.alive && !e.skipNextTurn)) if (!enemyFits(combat, enemy)) { enemy.skipNextTurn = true; staggered.push(enemy.id); }
  if (!dryRun(combat, intent, scriptChooser(answers)).ok) { for (const id of staggered) combat.enemies.find((e) => e.id === id).skipNextTurn = false; return false; }
  if (!step(intent, { staggered })) return false;
  const chooser = scriptChooser(answers);
  for (let n = 0; combat.pendingReaction && !combat.result; n++) {
    if (n > 60) return false;
    const answer = chooser.next(combat);
    if (!step(answer, { answer: answer.optionId === null ? 'back' : 'play' })) return false;
  }
  return true;
}

// (a) Defend offered against each maneuver of the enemy's move, played and declined.
for (const maneuver of ['attack', 'smash', 'sweep', 'ranged']) for (const answer of ['play', 'back']) {
  const hits = search({ base: 71000 + ['attack', 'smash', 'sweep', 'ranged'].indexOf(maneuver) * 60 + (answer === 'back' ? 30 : 0), count: 3, hand: ['core.defend', 'core.defend', 'core.strike'],
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && intentManeuver(combat, e) === maneuver) ? maneuver : null) });
  for (const { spec, why: m } of hits) addExchange('defend', `Defend against ${m}, ${answer}`, spec, (combat, step) => { scripted(combat, step, [answer]); });
}

// (b) A Counter card offered against each maneuver: played (it arms, the hit meets it) and declined.
for (const maneuver of ['attack', 'smash', 'sweep', 'ranged']) for (const answer of ['play', 'back']) {
  const hits = search({ base: 72000 + ['attack', 'smash', 'sweep', 'ranged'].indexOf(maneuver) * 60 + (answer === 'back' ? 30 : 0), count: 3, hand: ['core.counter', 'core.defend', 'core.strike'],
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && intentManeuver(combat, e) === maneuver) ? maneuver : null) });
  for (const { spec, why: m } of hits) addExchange('counter', `Counter against ${m}, ${answer}`, spec, (combat, step) => { scripted(combat, step, [answer]); });
}

// (c) A move with several hits opens one offer per hit (the per-hit contact policy): play then back, back then play, play twice.
for (const answers of [['play', 'back'], ['back', 'play'], ['play', 'play'], ['back', 'back']]) {
  const hits = search({ base: 73000 + answers.join('').length * 7 + (answers[0] === 'play' ? 40 : 0) + (answers[1] === 'play' ? 80 : 0), count: 3, hand: ['core.defend', 'core.defend', 'core.defend', 'core.strike'],
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && intentHits(combat, e) > 1) ? 'multi' : null) });
  for (const { spec } of hits) addExchange('multiHit', `a move of several hits, ${answers.join(' then ')}`, spec, (combat, step) => { scripted(combat, step, answers); });
}

// (d) The switch: reactions off with a card in the hand (the receipt, no offer), and back on.
{
  const hits = search({ base: 74000, count: 4, hand: ['core.defend', 'core.counter', 'core.strike'],
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && ['attack', 'smash', 'sweep'].includes(intentManeuver(combat, e))) ? 'attacked' : null) });
  for (const { spec } of hits) addExchange('switch', 'reactions off, then on again', spec, (combat, step) => {
    step({ type: 'setReactions', enabled: false });
    scripted(combat, step, ['back']);
    step({ type: 'setReactions', enabled: true });
    scripted(combat, step, ['play', 'back']);
  });
}

// (e) No Stamina: the card is in the hand and cannot be paid for (the `cost` reason names the resource).
{
  const hits = search({ base: 75000, count: 4, hand: ['core.defend', 'core.counter', 'core.strike'], patch: { player: { stamina: 0, energy: 0 } },
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && ['attack', 'smash', 'sweep'].includes(intentManeuver(combat, e))) ? 'attacked' : null) });
  for (const { spec } of hits) addExchange('noStamina', 'a Defend in hand and no Stamina', spec, (combat, step) => { scripted(combat, step, ['play']); });
}

// (f) A hand with no card that could answer: no offer and no receipt.
{
  const hits = search({ base: 76000, count: 3, hand: ['core.strike', 'core.smash', 'core.strike'],
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && ['attack', 'smash', 'sweep'].includes(intentManeuver(combat, e))) ? 'attacked' : null) });
  for (const { spec } of hits) addExchange('noCandidate', 'attacked with no reaction in the hand', spec, (combat, step) => { scripted(combat, step, ['play']); });
}

// (g) A control status on the player: the hand cannot play (the `controlled` reason).
for (const status of ['dazed', 'frozen']) {
  const hits = search({ base: 77000 + (status === 'frozen' ? 50 : 0), count: 3, hand: ['core.defend', 'core.counter', 'core.strike'], setup: [], patch: { player: { statuses: { [status]: { stacks: 2 } } } },
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && ['attack', 'smash', 'sweep'].includes(intentManeuver(combat, e))) ? 'attacked' : null) });
  for (const { spec } of hits) addExchange('controlled', `${status} on the player with a Defend in hand`, spec, (combat, step) => { scripted(combat, step, ['play']); });
}

// (h) The player's Poise one point from breaking: the offer comes before the hit, and the Poise break is the reaction's to prevent or not.
{
  const hits = search({ base: 78000, count: 4, hand: ['core.defend', 'core.defend', 'core.counter'], patch: { player: { persistentPoise: { value: 1 }, block: 0 } },
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && ['attack', 'smash', 'sweep'].includes(intentManeuver(combat, e))) ? 'attacked' : null) });
  for (const { spec } of hits) addExchange('poise', 'a Poise one point from breaking', spec, (combat, step) => { scripted(combat, step, ['play', 'play', 'back']); });
}

// (i) Two attackers: an offer for each hit of each, over one end turn.
{
  const hits = search({ base: 79000, count: 4, hand: ['core.defend', 'core.defend', 'core.counter', 'core.defend'], pool: all.filter((e) => e.enemies.length > 1),
    want: (combat) => (combat.enemies.filter((e) => fitsNow(combat, e) && ['attack', 'smash', 'sweep'].includes(intentManeuver(combat, e))).length > 1 ? 'two' : null) });
  for (const { spec } of hits) addExchange('twoAttackers', 'two attackers, an offer for each hit', spec, (combat, step) => { scripted(combat, step, ['play', 'back', 'play']); });
}

// (j) A lethal hit: the offer before it, played or declined.
for (const answer of ['play', 'back']) {
  const hits = search({ base: 80000 + (answer === 'back' ? 50 : 0), count: 3, hand: ['core.defend', 'core.defend', 'core.strike'], patch: { player: { hp: 3, block: 0 } },
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && ['attack', 'smash', 'sweep'].includes(intentManeuver(combat, e))) ? 'attacked' : null) });
  for (const { spec } of hits) addExchange('lethal', `a hit that can kill, ${answer}`, spec, (combat, step) => { scripted(combat, step, [answer]); });
}

// (k) The player attacks an enemy that holds a Counter: its reply is an incoming attack, and the player's Defend is offered against it on the player's own
// turn. The card that opened it resolves while the offer waits; the answer finishes the reply.
/** Plays a card of the hand and answers every offer it opens with the given answers; false when the play is outside the filter. */
function playAnswer(combat, step, cardId, targetId, answers) {
  const card = combat.piles.hand.find((c) => c.cardId === cardId);
  if (!card || combat.result || combat.phase !== 'player' || !playable(combat, card)) return false;
  const intent = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
  if (!dryRun(combat, intent, scriptChooser(answers)).ok || !step(intent)) return false;
  const chooser = scriptChooser(answers);
  for (let n = 0; combat.pendingReaction && !combat.result; n++) {
    if (n > 60) return false;
    const answer = chooser.next(combat);
    if (!step(answer, { answer: answer.optionId === null ? 'back' : 'play' })) return false;
  }
  return true;
}
const COUNTERING = /chain|knight|duelist|mirror|cinder|procession/i;
for (const [i, attackCard] of ['core.strike', 'core.smash', 'core.sweep', 'core.reaverSpecial'].entries()) for (const answers of [['play'], ['back']]) {
  const hits = search({ base: 82000 + i * 80 + (answers[0] === 'back' ? 40 : 0), count: 3, hand: [attackCard, 'core.defend', 'core.defend', 'core.strike'], need: [attackCard], pool: all.filter((e) => COUNTERING.test(e.id)),
    want: (combat) => (combat.enemies.some((e) => e.alive && e.combatCounter && bandOf(combat, e) === 0) ? 'counter' : null) });
  for (const { spec } of hits) addExchange('counterReturn', `${attackCard} into a Counter, ${answers[0]}`, spec, (combat, step) => {
    const armed = combat.enemies.find((e) => e.alive && e.combatCounter && bandOf(combat, e) === 0);
    playAnswer(combat, step, attackCard, armed.id, answers);
    scripted(combat, step, ['back']);
  });
}

// (l) Without the per-hit policy the whole move is one root: the player may stack several cards (the chain limit), the stack resolves last pushed first.
for (const answers of [['play', 'play', 'back'], ['play', 'play', 'play', 'play'], ['play', 'back']]) {
  const hits = search({ base: 83000 + answers.length * 60 + (answers[1] === 'back' ? 30 : 0), count: 3, hand: ['core.defend', 'core.defend', 'core.defend', 'core.counter', 'core.strike'], fields: { reactionRules: { contactPolicy: null } },
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && ['attack', 'smash', 'sweep'].includes(intentManeuver(combat, e))) ? 'attacked' : null) });
  for (const { spec } of hits) addExchange('stack', `a root with no per-hit policy, ${answers.join(' then ')}`, spec, (combat, step) => { scripted(combat, step, answers); });
}

// (m) An attacker that reaches from a distance: the Counter pays to close it (the Stamina to advance is part of its price), played or declined.
for (const answers of [['play'], ['back']]) {
  const hits = search({ base: 84000 + (answers[0] === 'back' ? 60 : 0), count: 4, hand: ['core.counter', 'core.defend', 'core.strike'], classIds: ['herald', 'starseer'], pool: all.filter((e) => /packHunt|bruiser|lanternFlight/i.test(e.id)),
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && bandOf(combat, e) > 0 && e.intent?.moveId) ? 'distant' : null) });
  for (const { spec } of hits) addExchange('distant', `a Counter against an attacker at a distance, ${answers[0]}`, spec, (combat, step) => { scripted(combat, step, answers); });
}

// ---------------------------------------------------------------------------------------------------------------
// Units: auto-combat's reaction chooser (model/autoCombatPolicy.js, owner ruling D12). Pure: the cases are drawn from its own LCG.
// ---------------------------------------------------------------------------------------------------------------
const units = [];
{
  const lcg = makeLcg(0x0d12);
  const words = ['attack', 'attacking', 'ranged', 'smash', 'smashing', 'sweep', 'sweeping', 'defend', 'defending', 'block', 'counter', 'countering', 'evade', 'evading', 'casting', 'preparing', 'cast', 'prepared', 'wild', null];
  const kinds = ['attack', 'smash', 'sweep', 'defend', 'counter', 'evade', 'prepared', 'casting', 'block', null];
  const matchups = structuredClone(createFight(w, createRun(w, w.rngMod.sweepSeed(1), 'reaver'), all[0]).combat.reactionRules.advantage);
  const pickFrom = (list) => list[lcg(list.length)];
  for (let i = 0; i < 400; i++) {
    const intent = lcg(5) === 0 ? null : { ...(lcg(2) === 0 ? { profile: { maneuver: pickFrom(words) } } : { stance: pickFrom(words) }), ...(lcg(4) === 0 ? { hidden: true } : {}), ...(lcg(5) === 0 ? { revealed: false } : {}) };
    const picks = Object.fromEntries(kinds.filter((k) => k && lcg(3) === 0).map((k) => [k, lcg(4)]));
    const state = { matchups, seats: { player: { intent, lastStance: lcg(3) === 0 ? null : pickFrom(words), picks } } };
    const options = Array.from({ length: lcg(5) }, (_, n) => ({ id: `o${n}`, kind: pickFrom(kinds) }));
    const chosen = policy.chooseReaction(state, 'player', options);
    units.push(canon({ state, seat: 'player', options, perceived: policy.perceivedManeuver(intent), chosen: chosen ? chosen.id : null, answer: policy.reactionAnswer({ id: 'offer' }, chosen) }));
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Write
// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const write = (file, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, file), text); return Buffer.byteLength(text); };
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, coveredEvents: [...COVERED_EVENTS].sort(), coveredOps: [...COVERED_OPS].sort(), deckPool: DECK_POOL, files: [] };
let total = 0;

const fightCount = {};
const seen = { offers: 0, played: 0, back: 0, unavailable: 0, stacked: 0, fizzled: 0 };
const tally = (steps) => {
  for (const s of steps) {
    if (s.offer) seen.offers++;
    if (s.answer === 'play') seen.played++;
    if (s.answer === 'back') seen.back++;
    for (const t of s.events) { if (t === 'combatReactionUnavailable') seen.unavailable++; if (t === 'combatReactionStacked') seen.stacked++; if (t === 'combatReactionFizzled') seen.fizzled++; }
  }
};
classes.forEach((classId, ci) => {
  const fights = [];
  for (let n = 0; n < FIGHTS; n++) {
    const encounter = all[(ci * 5 + n * 7 + 1) % all.length];
    // Every fourth fight has no per-hit policy: the whole move is one root, the player may stack cards and the stack is seen at rest.
    const f = fight(w.rngMod.sweepSeed(81000 + ci * 100 + n), classId, encounter, 40, n % 4 === 3 ? { reactionRules: { contactPolicy: null } } : undefined);
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
const bytes = write('exchanges.json', { exchanges });
total += bytes;
index.exchanges = { file: 'exchanges.json', count: exchanges.length, steps: exchanges.reduce((s, e) => s + e.steps.length, 0), kinds: [...new Set(exchanges.map((e) => e.kind))], bytes };
index.seen = seen;
const unitBytes = write('units.json', { units });
total += unitBytes;
index.units = { file: 'units.json', count: units.length, chosen: units.filter((u) => u.chosen).length, bytes: unitBytes };
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-reactions: ${Object.values(fightCount).reduce((s, f) => s + f.fights, 0)} fights / ${Object.values(fightCount).reduce((s, f) => s + f.steps, 0)} steps, ${exchanges.length} exchanges / ${index.exchanges.steps} steps, ${JSON.stringify(seen)}, ${(total / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/reactions`);
if (process.argv.includes('--why')) console.log(JSON.stringify(rejected, null, 1));
