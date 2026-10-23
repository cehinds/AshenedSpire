#!/usr/bin/env node
// Core 2 enemy oracle (WP-11, docs/design/11-CORE2-PORT-PLAN.md): the enemies' AI, their reactions and what the player knows of them. Drives the SHIPPED
// src/engine/combat.js (rollIntents, weightedMovePick, presentIntent, buildIntent, startDelayedMove, executeMovePayload, enemyPhase: the module is loaded with
// an export line appended by Tools/oracle-core2-hook.mjs, the checkout is not touched), src/engine/combatRoundsAdapter.js (takePlannedMove, enemyMoveSp),
// src/model/actionCosts.js (enemyPoolOf, enemyMoveSpOf), src/engine/combatCardTactics.js (expandedEnemyMove, enemyMoveCarrier), src/engine/enemyReactions.js
// (reactionMovesOf, decideEnemyReaction, openEnemyWindow, pushEnemyReaction, runEnemyPlan), src/engine/combatReactions.js (the stack with enemy entries),
// src/engine/enemyActionPreview.js (previewEnemyAction), src/engine/enemyKnowledge.js, src/model/enemyIntentKnowledge.js, enemyKnowledgeRun.js,
// enemyKnowledgeProfile.js and enemyKnowledgeRules.js at the pinned sha, on real Core 2 fights (createRunCombat for the configured `default` profile).
//
//   node Tools/oracle-core2-enemy.mjs [--fights 6] [--only statics|units|fights|exchanges] [--why] [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/enemy/, sorted keys, two runs are byte-identical):
//   enemies.json        every enemy of the profile (33), each on a fight of its own (a solo encounter): the move table after the profile's projection, the SP
//                       pool, the price of every move (alone, with Silenced on the payer, and under every stance of its own moves: stanceCosts), the expanded
//                       move, its carrier, previewEnemyAction and the intent box buildIntent makes for it, its reaction moves per window, and picks: 200
//                       rolls of the weighted pick on the enemyAI stream per seed (the plan Rounds.takePlannedMove leaves, the history, the counters).
//   units.json          decideEnemyReaction on every enemy with a reaction move and on one without: per window, incoming amount and state (staggered, dead,
//                       acted this turn, locked, a candidate state that reads the memo only, a memoised roll), in sequences on one fight so the draws on the
//                       enemyReaction stream and the memo are seen; the knowledge ledger (merge, counter bonus, encounter, Perception reconcile, bank); the
//                       intent read of a fight with no knowledge state (reads exact: no draw).
//   fights-<class>.json real fights, as the reaction oracle's: the bot plays attack, Smash, Sweep, Defend and Counter cards, answers every offer, and now
//                       PREDICTS the intents it could not read; the enemies charge delayed moves, react to the cards the player plays and to the reactions it
//                       pushes. Per step the full combat projection as a delta (the stack's enemy entries as a compact plan view), the event types, the payload
//                       of every reaction, Counter and knowledge event, and every RNG counter.
//   exchanges.json      declared sequences found by a deterministic seed search: every delayed move charged and resolved (answered, stacked), the gilded knight
//                       reacting to a heavy card and a Defend offered against its reaction, the duelist's riposte (reacted, not reacted), predictions (right,
//                       wrong), Perception paid for an Evade, a Counter reply and a Ward reply, the encounter ledger of a bankable fight.
//
// THE FILTER (what a recorded sequence avoids; the C# port refuses these by name and the test asserts it never has to): a step is recorded only when its dry
// run on a detached copy (a) throws nothing, (b) emits only event types in COVERED_EVENTS, (c) leaves no card choice, and (d) every card it plays has only
// effects in COVERED_OPS. Delayed moves, enemy reactions and enemy reaction draws are INSIDE it (WP-11). What stays outside is the range work (WP-12): an enemy
// that has to close the distance to act (no Stamina to advance, no closing move) is marked staggered for that end turn (the flag a Poise break sets).
import { writeFileSync, readFileSync, existsSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { register } from 'node:module';
register('./oracle-core2-hook.mjs', import.meta.url);
const { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, detach, projectFull, delta, typesSince } = await import('./oracle-core2.mjs');

const OUT = join(ORACLE_DIR, 'enemy');
const FIGHTS = Number(arg('--fights', '6'));
const ONLY = arg('--only', null);
const wants = (part) => !ONLY || ONLY === part;
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const triggers = await load('src/engine/triggers.js');
const expansion = await load('src/engine/combatExpansion.js');
const tactics = await load('src/engine/combatCardTactics.js');
const lineage = await load('src/engine/combatRangeLineage.js');
const costs = await load('src/model/actionCosts.js');
const rounds = await load('src/engine/combatRoundsAdapter.js');
const enemyReactions = await load('src/engine/enemyReactions.js');
const preview = await load('src/engine/enemyActionPreview.js');
const knowledgeEngine = await load('src/engine/enemyKnowledge.js');
const knowledgeRun = await load('src/model/enemyKnowledgeRun.js');
const knowledgeProfile = await load('src/model/enemyKnowledgeProfile.js');
const knowledgeRules = await load('src/model/enemyKnowledgeRules.js');
const stateMod = await load('src/model/state.js');
const intentKnowledge = await load('src/model/enemyIntentKnowledge.js');
const visibility = await load('src/model/combatIntentVisibility.js');
const X = w.combat; // the combat module with the appended exports
const all = encounters(w);
const classes = classIds(w);
const sweep = (n) => w.rngMod.sweepSeed(n);

// ---------------------------------------------------------------------------------------------------------------
// The filter
// ---------------------------------------------------------------------------------------------------------------
const REACTION_EVENTS = ['combatReactionOffered', 'combatReactionChosen', 'combatReactionStacked', 'combatReactionUnavailable', 'combatReactionFizzled', 'combatEnemyReactionStacked'];
const COUNTER_EVENTS = ['combatCounterArmed', 'combatCounterConsumed', 'combatCounterTriggered', 'combatGuardBroken', 'combatRangeReturnUnavailable'];
const KNOWLEDGE_EVENTS = ['perceptionGained', 'intentPredictionAccepted'];
const COVERED_EVENTS = new Set(['playerTurnEnd', 'playerTurnStart', 'enemyTurnStart', 'enemyTurnEnd', 'enemyActorTurnStarted', 'combatContactPrepared',
  'enemyMoveStarted', 'enemyMoveUnavailable', 'attackHit', 'attackMissed', 'damageDealt', 'hpLost', 'blockGained', 'blockBroken', 'persistentPoiseChanged', 'poiseBroken', 'playerStaggered',
  'enemyStaggered', 'combatStanceChanged', 'statusApplied', 'statusExpired', 'statusRemoved', 'deckShuffled', 'cardDrawn', 'cardPreparing', 'cardPlayed', 'cardResolved', 'cardExhausted',
  'energySpent', 'staminaSpent', 'manaSpent', 'enemyDied', 'combatEnd', 'combatHitStarted', 'controlRecovered', 'controlResolveScheduled', 'energyGained', 'healed',
  'manaRestored', 'relicTriggered', 'statusGaugeFilled', 'statusPressure', 'statusRecoveryRolled', 'combatAvoidanceResolved', 'cardAdded', 'barrierGained', 'barrierBroken',
  ...REACTION_EVENTS, ...COUNTER_EVENTS, ...KNOWLEDGE_EVENTS]);
const COVERED_OPS = new Set(['damage', 'block', 'gainBarrier', 'poiseDamage', 'wardDamage']);
const ENEMY_OPS = new Set(['damage', 'poiseDamage', 'wardDamage', 'applyStatus', 'block', 'gainBarrier', 'heal', 'addCard', 'buildup']);
const bandOf = (combat, enemy) => combat.rangeState?.engagements?.[JSON.stringify([`enemy:${enemy.id}`, 'player:player'].sort())];
const HOSTILE_OPS = new Set(['damage', 'applyStatus', 'buildup', 'poiseDamage', 'wardDamage', 'loseHp', 'stagger', 'arcaneBuildup']);
const FRIENDLY_TARGETS = new Set(['self', 'owner', 'player', 'ally', 'allAllies']);

// engine/combatRangeLineage.js: an owned hook that strikes another creature makes the quote follow its lineage (WP-12); the port refuses on the same test.
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

/** Whether playing the card would need a range lineage (WP-12): asked of the web's own extendRangeEnvelope on the card's actions. */
function needsLineage(combat, card) {
  const def = expansion.resolveCombatCard(combat, card);
  const tags = (def.cardTags || def.tags || []).map((t) => (typeof t === 'string' ? t : t.id));
  const counter = tags.includes('maneuver:counter');
  const carrier = { instanceId: card.instanceId, cardId: card.cardId, tags, authoredTags: tags };
  const enemy = combat.enemies.find((e) => e.alive) || combat.player;
  const row = (effect, target, held) => ({ source: combat.player, owner: combat.player, target, card: held, effect, meta: {} });
  const own = (def.effects || []).filter((e) => !(counter && ['damage', 'poiseDamage'].includes(e.op))).map((e) => row(e, ['enemy', 'allEnemies'].includes(e.target) ? enemy : combat.player, carrier));
  const reply = counter ? ['damage', 'poiseDamage'].map((op) => row({ op, amount: 1, target: 'enemy' }, enemy, { ...carrier, counterReturnAttack: true })) : [];
  try {
    return [own, reply].some((actions) => actions.length && lineage.extendRangeEnvelope(combat, combat.player, actions, [], { preview: true, carrier: actions[0].card }));
  } catch { return true; }
}

const tagsOf = (def) => (def.cardTags || def.tags || []).map((t) => (typeof t === 'string' ? t : t.id));
const maneuverOfMove = (combat, enemy, id) => tactics.enemyMoveCarrier(enemy, combat.registries.enemies.get(enemy.enemyId).moves[id], id, combat).combatProfile.maneuver;

// An enemy takes its turn inside the filter when every move it can play this turn (the move it resolves, its intent and its planned follow-ups) stays within
// ENEMY_OPS and its hostile moves already reach the player from where it stands (no Stamina to advance, no closing move): a melee move from melee range, a
// reach or ranged move from as far as its range goes. A move that charges is inside (WP-11).
const RANGES = ['melee', 'reach', 'ranged'];
function enemyFits(combat, enemy) {
  const band = bandOf(combat, enemy);
  if (band === undefined || band > 2) return false;
  const def = combat.registries.enemies.get(enemy.enemyId);
  const ids = [enemy.pendingMove?.moveId, enemy.intent?.moveId, ...(enemy.plannedMoves || []).map((m) => m.moveId)].filter(Boolean);
  for (const id of ids) {
    const move = tactics.expandedEnemyMove(enemy, def.moves[id], id, combat);
    const effects = [...(move.effects || []), ...(move.delay?.whileCharging?.effects || [])];
    if (effects.some((effect) => !ENEMY_OPS.has(effect.op))) return false;
    if (hasHostileHooks(combat, enemy, effects)) return false;
    const hostile = move.damage != null || effects.some((effect) => HOSTILE_OPS.has(effect.op));
    const range = tactics.enemyMoveCarrier(enemy, def.moves[id], id, combat).combatProfile.range || 'melee';
    if (hostile && RANGES.indexOf(range) < band) return false;
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
  return !needsLineage(combat, card);
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
// cardTargetsAllEnemies, intentPreview, actionPreview): the port builds the first three and the last two only as far as a rule reads them, so the
// record leaves them out.
const DECORATION = ['abilityBefore', 'abilityEntities', 'cardTargetsAllEnemies', 'intentPreview', 'actionPreview'];
const PAYLOAD_EVENTS = new Set([...REACTION_EVENTS, ...COUNTER_EVENTS, ...KNOWLEDGE_EVENTS, 'enemyMoveStarted', 'combatHitStarted', 'enemyActorTurnStarted']);
const payloadEvents = (combat, mark) => combat.eventLog.slice(mark).filter((e) => PAYLOAD_EVENTS.has(e.type) || (e.type === 'blockGained' && e.source === 'counter'))
  .map((e) => Object.fromEntries(Object.entries(e).filter(([key]) => !DECORATION.includes(key) && key !== 'intent')));

// The stack's enemy entries hold their committed plan (the group with its contacts, the planned actions): the record keeps a compact view of it, which is
// what the port can hold the same way (the contacts' numbers and the actions' effects and references).
function compactPlan(plan) {
  const group = plan.group || {};
  return {
    kind: group.kind ?? null, key: group.key ?? null, sourceId: group.sourceId ?? null,
    targets: Object.fromEntries(Object.entries(group.targets || {}).map(([id, row]) => [id, (row.contacts || []).map((c) => ({ amount: c.amount ?? null, pressure: c.pressure ?? null, effect: c.effect ?? null }))])),
    actions: (plan.actions || []).map((a) => ({ effect: a.effect, sourceRef: a.sourceRef ?? null, ownerRef: a.ownerRef ?? null, targetRef: a.targetRef ?? null, moveId: a.meta?.moveId ?? null })),
  };
}

/** The combat as the port projects it (the offer without its previews, the paused card's queue as its length, the enemy entries' plans compact). */
function view(combat) {
  const state = projectFull(combat);
  if (state.reactionStack?.entries) state.reactionStack = { ...state.reactionStack, entries: state.reactionStack.entries.map((entry) => (entry.side === 'enemy' ? { ...entry, plan: canon(compactPlan(entry.plan)) } : entry)) };
  return state;
}

/** The offer the fight is paused on, as the test checks it. */
const offerOf = (combat) => (combat.pendingReaction ? canon({
  id: combat.pendingReaction.id, groupKey: combat.pendingReaction.groupKey, ownerId: combat.pendingReaction.ownerId, sourceId: combat.pendingReaction.sourceId,
  options: combat.pendingReaction.options.map((o) => ({ id: o.id, cardId: o.cardId, name: o.name, staminaCost: o.staminaCost, manaCost: o.manaCost, choiceName: o.choiceName, play: o.play,
    ...(o.incoming ? { incoming: o.incoming } : {}), ...(o.sourceId ? { sourceId: o.sourceId } : {}) })),
}) : null);

/** The fight a record names: the run, the cards laid over it, the patch merged over the fighters. `enemyIds` is a solo encounter of the fight's own. */
function open(spec) {
  const run = createRun(w, spec.seed, spec.classId);
  if (spec.runPatch) merge(run, spec.runPatch);
  if (spec.knowledge) {
    run.enemyKnowledgeRules = knowledgeRules.snapshotEnemyKnowledgeRules(w.registries);
    knowledgeRun.openRunEnemyKnowledge(run, { receiptId: spec.knowledge.receiptId, bankable: spec.knowledge.bankable !== false });
  }
  if (spec.deckRemove) run.deck = run.deck.filter((card) => !spec.deckRemove.includes(card.cardId));
  for (const add of spec.deckAdd || []) run.deck.push({ instanceId: add.instanceId, cardId: add.cardId, upgraded: false });
  const encounter = spec.enemyIds ? { id: `solo:${spec.enemyIds.join('+')}`, enemies: spec.enemyIds, pool: 'normal' } : all.find((e) => e.id === spec.encounterId);
  const { combat } = createFight(w, run, encounter);
  if (spec.hand) combat.piles.hand = spec.hand.map((cardId, i) => ({ instanceId: `mx${i + 1}`, cardId, upgraded: false }));
  if (spec.fields) merge(combat, spec.fields);
  if (spec.patch?.player) merge(combat.player, spec.patch.player);
  for (const [index, patch] of Object.entries(spec.patch?.enemies || {})) if (combat.enemies[Number(index)]) merge(combat.enemies[Number(index)], patch);
  combat.__run = run;
  return combat;
}

/** What the run holds of the fight's knowledge once it is reconciled (the end of a bankable fight). */
function ledgerOf(combat) {
  const run = combat.__run;
  if (!run?.enemyKnowledgeState) return null;
  const copy = structuredClone({ enemyKnowledgeState: run.enemyKnowledgeState, skills: run.skills });
  const probe = { ...run, enemyKnowledgeState: copy.enemyKnowledgeState, skills: copy.skills };
  knowledgeEngine.reconcileCombatKnowledge(probe, combat);
  return canon({ state: probe.enemyKnowledgeState, perception: probe.skills.perception });
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
  const ledger = spec.knowledge ? ledgerOf(combat) : null;
  return { ...spec, start, steps, ...(ledger ? { ledger } : {}) };
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
    if (needsLineage(combat, card)) return 'range lineage at an offer';
  }
  return null;
}

/** The intent played to its end on a detached copy with the answers the bot will give (from a copy of its LCG): nothing outside the filter happens. */
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
  // A reply that must close the distance on its own Stamina pays to advance (WP-12): a payment that follows a Counter's trigger and no card.
  if (types.some((t, i) => (t === 'energySpent' || t === 'staminaSpent') && types.slice(0, i).reverse().find((x) => ['cardPreparing', 'combatCounterTriggered', 'enemyMoveStarted'].includes(x)) === 'combatCounterTriggered')) return why('a reply pays to advance');
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

/** The predictions the bot may make: an enemy whose telegraph it could not read, the right category half the time. */
function predictions(combat, lcg) {
  const out = [];
  if (combat.phase !== 'player' || combat.result || combat.pendingReaction) return out;
  for (const enemy of combat.enemies.filter((e) => e.alive)) {
    const action = enemy.knowledgeAction;
    const read = action?.reads?.player;
    if (!action || action.executed || action.cancelled || read?.visibility !== 'unknown' || read.prediction !== null || read.resolved) continue;
    const right = intentKnowledge.PREDICTION_CHOICES.includes(action.category) && lcg(2) === 0;
    const maneuver = right ? action.category : intentKnowledge.PREDICTION_CHOICES[lcg(intentKnowledge.PREDICTION_CHOICES.length)];
    out.push({ type: 'predictIntent', enemyInstanceId: enemy.id, actionSerial: action.serial, maneuver });
  }
  return out;
}

/** The play intents the filter lets through for the current hand, each with the targets it can have. */
function options(combat, lcg) {
  const out = [];
  const targets = combat.enemies.filter((e) => e.alive && bandOf(combat, e) === 0).map((e) => e.id);
  for (const card of combat.piles.hand) {
    if (!playable(combat, card)) continue;
    const def = expansion.resolveCombatCard(combat, card);
    if (def.effects.some((e) => e.op === 'damage' && e.target !== 'enemy') && combat.enemies.some((e) => e.alive && bandOf(combat, e) !== 0)) continue;
    const aimed = immediate(def).some((e) => e.target === 'enemy');
    for (const targetId of aimed ? targets : [undefined]) {
      const intent = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
      if (dryRun(combat, intent, lcgChooser(lcg)).ok) out.push({ intent, reaction: tagsOf(def).includes('trait:reaction'), cardId: card.cardId });
    }
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Fights: the enemy bot
// ---------------------------------------------------------------------------------------------------------------
// Cards given to the deck: the reaction cards (Defend, Counter) and the attacks the player answers with (Smash is the card that pushes a gilded knight over its
// threshold).
const DECK_POOL = ['core.defend', 'core.defend', 'core.counter', 'core.counter', 'core.strike', 'core.smash', 'core.sweep', 'core.defend'];

function fight(seed, classId, encounter, steps, fields, extra = {}) {
  const lcg = makeLcg(seed ^ 0x2f6b9d35);
  // A class whose passives hold a hook that strikes (WP-12) keeps its Defends and loses its Counters: a Counter's reply would need the lineage.
  const probe = open({ seed, classId, ...(encounter.enemies ? { encounterId: encounter.id } : {}) });
  const deckRemove = hasHostileHooks(probe, probe.player, []) ? ['core.counter'] : undefined;
  const pool = deckRemove ? DECK_POOL.filter((id) => !deckRemove.includes(id)) : DECK_POOL;
  const deckAdd = Array.from({ length: 6 }, (_, i) => ({ instanceId: `mx${i + 1}`, cardId: pool[lcg(pool.length)] }));
  return record({ seed, classId, encounterId: encounter.id, ...(deckRemove ? { deckRemove } : {}), ...(fields ? { fields } : {}), ...extra, deckAdd }, (combat, step) => {
    for (let i = 0; i < steps && !combat.result; i++) {
      if (combat.pendingReaction) { if (!settle(combat, step, lcg)) break; continue; }
      if (combat.phase !== 'player') break;
      const predicting = predictions(combat, lcg);
      if (predicting.length && lcg(10) < 7) { if (!step(predicting[lcg(predicting.length)])) break; continue; }
      const choices = options(combat, lcg);
      // The reaction cards are kept for the enemies' hits most of the time; the others are played on the turn.
      const spend = choices.filter((o) => !o.reaction);
      const chosen = spend.length && lcg(10) < 8 ? spend : choices;
      if (chosen.length && lcg(10) < 6) { if (!step(chosen[lcg(chosen.length)].intent)) break; continue; }
      if (!endTurn(combat, step, lcg)) break;
    }
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Statics: every enemy on a fight of its own
// ---------------------------------------------------------------------------------------------------------------
const enemyIdsAll = w.registries.enemies.all().map((e) => e.id);
const soloSpec = (seed, enemyId, classId = 'reaver') => ({ seed, classId, enemyIds: [enemyId] });
const pricing = (enemy, patch) => ({ ...structuredClone(enemy), ...patch });

function staticEnemy(enemyId, index) {
  const spec = soloSpec(sweep(95000 + index), enemyId);
  const combat = open(spec);
  const enemy = combat.enemies[0];
  const def = combat.registries.enemies.get(enemyId);
  const moveIds = Object.keys(def.moves);
  const stances = [...new Set(moveIds.map((id) => tactics.enemyMoveCarrier(enemy, def.moves[id], id, combat).combatProfile.maneuver).filter(Boolean)), null];
  const silenced = pricing(enemy, { statuses: { ...enemy.statuses, silenced: { stacks: 2 } } });
  const grounded = pricing(enemy, { statuses: { ...enemy.statuses, grounded: { stacks: 1 } } });
  const moves = moveIds.map((id) => {
    const move = def.moves[id];
    const sp = (who) => rounds.enemyMoveSp(move, { enemy: who, moveId: id, combat });
    return {
      id,
      sp: sp(enemy), spSilenced: sp(silenced),
      stanceCosts: Object.fromEntries(stances.map((stance) => [stance ?? 'none', sp(pricing(enemy, { combatStance: stance ? { maneuver: stance } : undefined }))])),
      damage: stateMod.enemyMoveDamage(enemy, move),
      expanded: canon(tactics.expandedEnemyMove(enemy, move, id, combat)),
      carrier: canon(tactics.enemyMoveCarrier(enemy, move, id, combat)),
      carrierGrounded: canon(tactics.enemyMoveCarrier(grounded, move, id, combat).combatProfile),
      intent: canon(X.__buildIntent(move, id, enemy, combat)),
      preview: canon(preview.previewEnemyAction(combat, enemy, id)),
      previewSilenced: canon(preview.previewEnemyAction({ ...combat, enemies: [silenced] }, silenced, id)),
    };
  });
  const reactions = Object.fromEntries(['playerAction', 'playerReaction'].map((window) => [window, enemyReactions.reactionMovesOf(combat, enemy, window).map(([id]) => id)]));
  // The weighted pick on the enemyAI stream: 200 rolls of the plan per seed, the plan and the counters after every one of them.
  const picks = [];
  for (const [n, unlock] of [[0, false], [1, false], [2, false], [3, true], [4, true]]) {
    const fightSpec = soloSpec(sweep(96000 + index * 10 + n), enemyId, classes[n % classes.length]);
    const c = open(fightSpec);
    const e = c.enemies[0];
    const d = c.registries.enemies.get(enemyId);
    if (unlock) e.unlockedMoves = moveIds.filter((id) => d.moves[id].locked);
    const startRolls = c.rng.getCounters();
    const rolls = [];
    for (let i = 0; i < 200; i++) {
      const first = i === 0 && !!d.firstMove;
      const moveId = rounds.takePlannedMove(c, e, d, { isFirstTurn: i === 0, pick: (en, de, maxSp) => X.__weightedMovePick(c, en, de, maxSp) });
      rolls.push({ moveId, plan: canon(e.plannedMoves || []), opener: first });
    }
    picks.push({ seed: fightSpec.seed, classId: fightSpec.classId, unlock, startEnemyAI: startRolls.enemyAI ?? 0, rolls, history: e.movesHistory.length, enemyAI: c.rng.getCounters().enemyAI ?? 0 });
  }
  return canon({
    id: enemyId, spec, pool: costs.enemyPoolOf(def), firstMove: def.firstMove ?? null, rangeClosingMove: def.rangeClosingMove ?? null, startingRange: def.startingRange ?? null,
    moveIds, moves, reactions, picks, unlockable: moveIds.filter((id) => def.moves[id].locked),
    intent: view(combat).enemies[0].intent, plannedMoves: view(combat).enemies[0].plannedMoves, enemyKnowledge: view(combat).enemyKnowledge,
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Units: the reaction decision, the knowledge ledger, the intent read without a knowledge state
// ---------------------------------------------------------------------------------------------------------------
const reactionUnits = [];
{
  const variants = {
    base: () => ({}),
    staggered: () => ({ skipNextTurn: true }),
    dead: () => ({ alive: false }),
    acted: (c) => ({ combatReactionTurn: c.turn }),
    actedBefore: (c) => ({ combatReactionTurn: c.turn - 1 }),
    locked: () => ({ statuses: { frozen: { stacks: 2 } } }),
  };
  const incomings = [0, 1, 4, 7, 8, 9, 30];
  let n = 0;
  for (const enemyId of ['gildedKnight', 'courtDuelist', 'wanderingSoldier', 'huskBrute']) for (const [variant, patch] of Object.entries(variants)) {
    const calls = [];
    const c = open(soloSpec(sweep(97000 + n++), enemyId));
    const enemy = c.enemies[0];
    merge(enemy, patch(c));
    for (const [wi, window] of ['playerAction', 'playerReaction'].entries()) for (const [i, incoming] of incomings.entries()) {
      const call = { window, incoming, groupKey: 'g1', windowIndex: wi * 10 + i };
      const result = enemyReactions.decideEnemyReaction(c, enemy, window, { groupKey: call.groupKey, windowIndex: call.windowIndex, incoming });
      calls.push({ ...call, result, enemyReaction: c.rng.getCounters().enemyReaction ?? 0, rolls: canon(c.enemyReactionRolls ?? null) });
    }
    // The same window again reads the memo and draws nothing; a candidate state (the offer check) reads the memo only and never draws.
    for (const window of ['playerReaction']) {
      const again = { window, incoming: 7, groupKey: 'g1', windowIndex: 15 };
      calls.push({ ...again, result: enemyReactions.decideEnemyReaction(c, enemy, window, { groupKey: again.groupKey, windowIndex: again.windowIndex, incoming: again.incoming }), enemyReaction: c.rng.getCounters().enemyReaction ?? 0, rolls: canon(c.enemyReactionRolls ?? null) });
      const fresh = { window, incoming: 7, groupKey: 'g2', windowIndex: 0, playing: true };
      c._reactionPlaying = true;
      calls.push({ ...fresh, result: enemyReactions.decideEnemyReaction(c, enemy, window, { groupKey: fresh.groupKey, windowIndex: fresh.windowIndex, incoming: fresh.incoming }), enemyReaction: c.rng.getCounters().enemyReaction ?? 0, rolls: canon(c.enemyReactionRolls ?? null) });
      delete c._reactionPlaying;
    }
    reactionUnits.push(canon({ kind: 'decide', enemyId, variant, spec: soloSpec(sweep(97000 + n - 1), enemyId), patch: patch(c), calls,
      fightReacts: { any: enemyReactions.fightReacts(c), playerAction: enemyReactions.fightReacts(c, 'playerAction'), playerReaction: enemyReactions.fightReacts(c, 'playerReaction') } }));
  }
}

// The knowledge ledger (model/enemyKnowledgeRun.js, enemyKnowledgeProfile.js): operations on a run's learning state, each recorded with what it returned.
const ledgerUnits = [];
{
  const rules = knowledgeRules.snapshotEnemyKnowledgeRules(w.registries);
  const lcg = makeLcg(0x11e0);
  const enemyIds = ['wanderingSoldier', 'blightHound', 'gildedKnight', 'courtDuelist', 'huskBrute'];
  const guarded = (fn) => { try { return { value: fn() }; } catch (e) { return { error: e.message }; } };
  const newRun = (bankable) => { const run = { enemyKnowledgeRules: structuredClone(rules), skills: {} }; knowledgeRun.openRunEnemyKnowledge(run, { receiptId: bankable ? `receipt-${lcg(1000)}` : null, bankable }); return run; };
  // targets
  for (const id of [...enemyIds, 'unknown']) ledgerUnits.push(canon({ kind: 'target', enemyId: id, value: knowledgeRules.enemyMasteryTarget(rules, id) }));
  // runs: open encounters, bank counter bonuses, reconcile Perception, acknowledge a bank
  for (let r = 0; r < 24; r++) {
    const bankable = r % 4 !== 3;
    const run = newRun(bankable);
    const log = [];
    let profile = knowledgeProfile.emptyEnemyKnowledge();
    const rec = (op, args, fn) => { const before = structuredClone({ state: run.enemyKnowledgeState, skills: run.skills }); const out = guarded(fn); log.push({ op, args, ...out, after: structuredClone({ state: run.enemyKnowledgeState, skills: run.skills }), before }); return out; };
    for (let k = 0; k < 5; k++) {
      const ids = enemyIds.filter(() => lcg(2) === 0);
      const group = ids.length ? ids : [enemyIds[lcg(enemyIds.length)]];
      const enc = rec('open', { nodeId: `n${k}`, encounterId: `e${lcg(3)}`, enemyIds: group }, () => knowledgeRun.openKnowledgeEncounter(run, `n${k}`, `e${lcg(3)}`, group, profile));
      if (enc.value) {
        const target = group[lcg(group.length)];
        rec('bonus', { enemyId: target }, () => {
          const bonus = knowledgeRun.addKnowledgeCounterBonus(run.enemyKnowledgeState.pending, enc.value, target, knowledgeRules.enemyMasteryTarget(rules, target), profile);
          run.enemyKnowledgeState.pending = bonus;
          return bonus;
        });
        const earned = lcg(40);
        rec('reconcile', { encounterId: enc.value.id, earnedXp: earned }, () => knowledgeRun.reconcilePerception(run, enc.value.id, earned));
        if (lcg(3) === 0) rec('reconcile', { encounterId: enc.value.id, earnedXp: earned + lcg(20) }, () => knowledgeRun.reconcilePerception(run, enc.value.id, earned + 5));
        if (lcg(2) === 0) {
          const captured = structuredClone(run.enemyKnowledgeState.pending);
          rec('bank', {}, () => { const banked = knowledgeProfile.bankEnemyKnowledgeProfile({ id: 'meta' }, captured); profile = banked.meta.enemyKnowledge; return banked; });
          rec('acknowledge', {}, () => { knowledgeRun.acknowledgeKnowledgeBank(run, captured); return run.enemyKnowledgeState.pending; });
        }
      }
    }
    ledgerUnits.push(canon({ kind: 'run', bankable, log, problems: knowledgeRun.enemyKnowledgeRunProblems(run) }));
  }
  // merges on their own, including the refusals
  const sample = (ids) => ({ version: 1, enemies: Object.fromEntries(ids.map((id) => [id, { target: 20 + lcg(31), receipts: Object.fromEntries(Array.from({ length: 1 + lcg(5) }, (_, i) => [`r${lcg(8)}`, { bonus: lcg(2) === 0 }])) }])) });
  for (let m = 0; m < 40; m++) {
    const incoming = sample(enemyIds.filter(() => lcg(2) === 0));
    const current = sample(enemyIds.filter(() => lcg(2) === 0));
    ledgerUnits.push(canon({ kind: 'merge', incoming, current, ...guarded(() => knowledgeProfile.mergeEnemyKnowledge(incoming, current)),
      progress: Object.fromEntries(Object.entries(current.enemies).map(([id, row]) => [id, knowledgeProfile.knowledgeProgress(row)])), points: Object.fromEntries(Object.entries(current.enemies).map(([id, row]) => [id, knowledgeProfile.knowledgePoints(row)])) }));
  }
  ledgerUnits.push(canon({ kind: 'merge', incoming: { version: 2, enemies: {} }, current: knowledgeProfile.emptyEnemyKnowledge(), ...guarded(() => knowledgeProfile.mergeEnemyKnowledge({ version: 2, enemies: {} }, knowledgeProfile.emptyEnemyKnowledge())) }));
  ledgerUnits.push(canon({ kind: 'merge', incoming: { version: 1, enemies: { x: { target: 5, receipts: { a: { bonus: false } } } } }, current: knowledgeProfile.emptyEnemyKnowledge(), ...guarded(() => knowledgeProfile.mergeEnemyKnowledge({ version: 1, enemies: { x: { target: 5, receipts: { a: { bonus: false } } } } }, knowledgeProfile.emptyEnemyKnowledge())) }));
  // the read categories and the hidden-intent chance (model/enemyIntentKnowledge.js, combatIntentVisibility.js)
  for (const kind of ['staggered', 'attack', 'attackDebuff', 'block', 'defend', 'counter', 'casting', 'buff', 'unknown']) for (const profile of [{}, { maneuver: 'smash' }, { maneuver: 'sweep' }, { maneuver: 'ranged' }, { maneuver: 'defend' }, { maneuver: 'counter' }, { camp: 'spell' }, { camp: 'magic', maneuver: 'attack' }, { maneuver: 'attack' }])
    for (const charging of [false, true]) ledgerUnits.push(canon({ kind: 'category', intentKind: kind, profile, charging, value: intentKnowledge.predictionCategory({ kind }, profile, { charging }) }));
  for (const category of ['Attack', 'Smash', 'Sweep', 'Ranged', 'Spell', 'Casting', 'Preparing', 'Defend', 'Counter']) ledgerUnits.push(canon({ kind: 'label', category, value: intentKnowledge.broadIntentLabel(category) }));
}

// The intent read of a fight with no knowledge state (a profile whose intent module reads `exact` shows every intent and draws nothing): rollIntents on a
// fight whose enemyKnowledge is removed, for every encounter.
const readUnits = [];
{
  let n = 0;
  for (const encounter of all) {
    const spec = { seed: sweep(98000 + n++), classId: classes[n % classes.length], encounterId: encounter.id, fields: { enemyKnowledge: null } };
    const c = open(spec);
    const before = c.rng.getCounters();
    const rolled = [];
    for (let turn = 0; turn < 3; turn++) {
      X.__rollIntents(c, false);
      rolled.push(canon({ enemies: c.enemies.map((e) => ({ id: e.id, intent: e.intent?.moveId ?? null, kind: e.intent?.kind ?? null, intentRevealed: e.intentRevealed ?? null, plan: (e.plannedMoves || []).map((m) => m.moveId) })), rng: c.rng.getCounters() }));
    }
    readUnits.push(canon({ spec, start: canon({ enemies: c.enemies.length, before }), rolled, rules: w.profile.modules.intent }));
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Exchanges
// ---------------------------------------------------------------------------------------------------------------
const seeds = (base) => (n) => sweep(base + n);

/** The first fights, by a deterministic seed search over the classes and encounters, whose opening state satisfies `want`. */
function search({ base, count, classIds: wanted = classes, pool = all, enemyIds = null, hand, patch, fields, runPatch, knowledge, setup, want, need = [], limit = 600 }) {
  const found = [];
  const seedAt = seeds(base);
  for (let n = 0; n < limit && found.length < count; n++) {
    const classId = wanted[n % wanted.length];
    const encounter = pool[(Math.floor(n / wanted.length) * 3 + n) % pool.length];
    const spec = { seed: seedAt(n), classId, ...(enemyIds ? { enemyIds } : { encounterId: encounter.id }), ...(hand ? { hand } : {}), ...(patch ? { patch } : {}), ...(fields ? { fields } : {}),
      ...(runPatch ? { runPatch } : {}), ...(knowledge ? { knowledge } : {}), ...(setup ? { setup } : {}) };
    let combat;
    try { combat = open(spec); for (const intent of setup || []) w.combat.dispatch(combat, structuredClone(intent)); } catch { continue; }
    if (need.some((cardId) => { const card = combat.piles.hand.find((c) => c.cardId === cardId); return !card || !playable(combat, card); })) continue;
    const verdict = want(combat);
    if (verdict) found.push({ spec, why: verdict });
  }
  return found;
}

const intentManeuver = (combat, enemy) => (enemy.intent?.moveId ? maneuverOfMove(combat, enemy, enemy.intent.moveId) : null);
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
const firstAlive = (combat) => combat.enemies.find((e) => e.alive && bandOf(combat, e) === 0);
/** Predicts the telegraph of every enemy it could not read, right or wrong. */
function predictAll(combat, step, right) {
  for (const enemy of combat.enemies.filter((e) => e.alive)) {
    const action = enemy.knowledgeAction;
    const read = action?.reads?.player;
    if (!action || action.executed || action.cancelled || read?.visibility !== 'unknown' || read.prediction !== null || read.resolved) continue;
    const wrong = intentKnowledge.PREDICTION_CHOICES.find((m) => m !== action.category);
    step({ type: 'predictIntent', enemyInstanceId: enemy.id, actionSerial: action.serial, maneuver: right && intentKnowledge.PREDICTION_CHOICES.includes(action.category) ? action.category : wrong });
  }
}
const sturdy = { player: { hp: 900, maxHp: 900 } };

function buildExchanges() {
// (a) Every delayed move charges (its Block while it charges), then resolves on the next turn: the turns are played with the offers answered, in a solo
// fight against its enemy, until the move has resolved or the steps are spent.
const delayed = [];
for (const def of w.registries.enemies.all()) for (const [id, move] of Object.entries(def.moves)) if (move.delay) delayed.push({ enemyId: def.id, moveId: id });
for (const { enemyId, moveId } of delayed) for (const answer of ['back', 'play']) {
  // The seed whose first turns give the enemy this move: ends the turn on a copy until it has been charged and resolved.
  const hits = search({ base: 101000 + delayed.findIndex((d) => d.moveId === moveId && d.enemyId === enemyId) * 400 + (answer === 'play' ? 200 : 0), count: 2, enemyIds: [enemyId], hand: ['core.defend', 'core.defend', 'core.counter', 'core.strike'], patch: sturdy, limit: 120,
    want: (combat) => {
      const copy = detach(w, combat);
      const lcg = makeLcg(7);
      let charged = false, resolved = false;
      for (let t = 0; t < 14 && !copy.result; t++) {
        const e = copy.enemies[0];
        if (!enemyFits(copy, e)) return null;
        const mark = copy.eventLog.length;
        const verdict = dry(copy, { type: 'endTurn', discardIds: [] }, scriptChooser([answer]));
        if (!verdict.ok) return null;
        w.combat.dispatch(copy, { type: 'endTurn', discardIds: [] });
        for (let n = 0; copy.pendingReaction && n < 40; n++) w.combat.dispatch(copy, scriptChooser([answer]).next(copy));
        if (copy.enemies[0].pendingMove?.moveId === moveId) charged = true;
        else if (charged && copy.eventLog.slice(mark).some((ev) => ev.type === 'enemyMoveStarted' && ev.moveId === moveId)) { resolved = true; break; }
        void lcg;
      }
      return charged && resolved ? moveId : null;
    } });
  for (const { spec } of hits) addExchange('delayed', `${enemyId}.${moveId} charges and resolves, answers ${answer}`, spec, (combat, step) => {
    for (let t = 0; t < 14 && !combat.result; t++) {
      if (combat.pendingReaction) { if (!settle(combat, step, scriptChooserLcg(answer))) break; continue; }
      const had = combat.enemies[0].pendingMove?.moveId;
      if (!scripted(combat, step, [answer])) break;
      if (had === moveId && !combat.enemies[0].pendingMove) break;
    }
  });
}
function scriptChooserLcg(answer) { const lcg = () => (answer === 'play' ? 0 : 1); lcg.clone = () => lcg; return lcg; }

// (b) The gilded knight reacts to a heavy card (a playerAction window, incoming 8 or more): its Block and strike are stacked, the player's own Defend may
// be offered against the strike, and the stack resolves before the card does.
for (const [i, card] of ['core.smash', 'core.reaverSpecial', 'core.strike'].entries()) for (const answers of [['play'], ['back']]) {
  const hits = search({ base: 103000 + i * 300 + (answers[0] === 'back' ? 150 : 0), count: 3, pool: all.filter((e) => e.enemies.includes('gildedKnight')), hand: [card, 'core.defend', 'core.defend', 'core.strike'], need: [card], patch: sturdy,
    want: (combat) => (combat.enemies.some((e) => e.alive && e.enemyId === 'gildedKnight' && bandOf(combat, e) === 0) ? 'knight' : null) });
  for (const { spec } of hits) addExchange('knight', `${card} into a gilded knight, answers ${answers[0]}`, spec, (combat, step) => {
    const knight = combat.enemies.find((e) => e.alive && e.enemyId === 'gildedKnight' && bandOf(combat, e) === 0);
    playAnswer(combat, step, card, knight.id, answers);
    scripted(combat, step, answers);
  });
}

// (c) The duelist's riposte: the player pushes a Defend against an attack (a playerReaction window, minIncoming 0, one draw on the enemyReaction stream), and the
// duelist answers or does not. Several seeds, so both outcomes are seen.
for (const answers of [['play', 'back'], ['play', 'play'], ['play']]) {
  const hits = search({ base: 105000 + answers.length * 400, count: 6, pool: all.filter((e) => e.enemies.includes('courtDuelist')), hand: ['core.defend', 'core.defend', 'core.defend', 'core.counter', 'core.strike'], patch: sturdy,
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && e.enemyId === 'courtDuelist' && ['attack', 'smash', 'sweep'].includes(intentManeuver(combat, e))) ? 'duelist' : null) });
  for (const { spec } of hits) addExchange('duelist', `a Defend against the duelist, ${answers.join(' then ')}`, spec, (combat, step) => { for (let t = 0; t < 3 && !combat.result; t++) if (!scripted(combat, step, answers)) break; });
}

// (d) Predictions: the telegraphs the player could not read (its Wisdom is lowered so most reads fail), predicted right and wrong, then the enemies act.
for (const right of [true, false]) {
  const hits = search({ base: 107000 + (right ? 0 : 400), count: 8, runPatch: { attributes: { wisdom: 0, intelligence: 0 } }, patch: sturdy,
    want: (combat) => (combat.enemies.some((e) => e.alive && e.knowledgeAction?.reads?.player?.visibility === 'unknown' && fitsNow(combat, e)) ? 'unread' : null) });
  for (const { spec } of hits) addExchange('predict', `unread telegraphs predicted ${right ? 'right' : 'wrong'}`, spec, (combat, step) => {
    for (let t = 0; t < 4 && !combat.result; t++) { predictAll(combat, step, right); if (!scripted(combat, step, ['back'])) break; }
  });
}
// A prediction the port must refuse (a read that was exact, an action already predicted, a staggered enemy, an unknown serial): the web's refusals.
{
  const hits = search({ base: 108000, count: 4, patch: sturdy, want: (combat) => (combat.enemies.some((e) => e.alive && e.knowledgeAction?.reads?.player?.visibility === 'exact') ? 'exact' : null) });
  for (const { spec } of hits) addExchange('predictRefused', 'predictions the web refuses', spec, (combat, step) => {
    const enemy = combat.enemies.find((e) => e.alive && e.knowledgeAction?.reads?.player?.visibility === 'exact');
    step({ type: 'predictIntent', enemyInstanceId: enemy.id, actionSerial: enemy.knowledgeAction.serial, maneuver: 'Attack' });
    step({ type: 'predictIntent', enemyInstanceId: enemy.id, actionSerial: enemy.knowledgeAction.serial + 1, maneuver: 'Attack' });
    step({ type: 'predictIntent', enemyInstanceId: enemy.id, actionSerial: enemy.knowledgeAction.serial, maneuver: 'Nonsense' });
    step({ type: 'predictIntent', enemyInstanceId: 'e99', actionSerial: 1, maneuver: 'Attack' });
  });
}

// (e) Perception paid for a response: a Counter whose reply lands on an action the player could not read, a Ward reply (a spell Counter drains the caster's
// Poise: Ward is off), an Evade that turns the hit; and the bankable fight that keeps the enemy for the encounter's ledger.
for (const bankable of [false, true]) for (const [i, card] of ['core.counter', 'core.starseerSpecial', 'core.rogueSpecial', 'core.defend'].entries()) {
  const hits = search({ base: 109000 + i * 500 + (bankable ? 250 : 0), count: 3, hand: [card, 'core.counter', 'core.defend', 'core.strike'], runPatch: { attributes: { wisdom: 0, intelligence: 0 } }, patch: sturdy,
    ...(bankable ? { knowledge: { receiptId: `bank-${i}` } } : {}), classIds: card === 'core.starseerSpecial' ? ['starseer'] : card === 'core.rogueSpecial' ? ['rogue'] : classes,
    want: (combat) => (combat.enemies.some((e) => fitsNow(combat, e) && e.knowledgeAction?.reads?.player?.visibility === 'unknown' && ['attack', 'smash', 'sweep', 'ranged'].includes(intentManeuver(combat, e))) ? 'unread' : null) });
  for (const { spec } of hits) addExchange('credit', `${card} against an unread telegraph${bankable ? ', bankable' : ''}`, spec, (combat, step) => {
    const card0 = combat.piles.hand.find((c) => c.cardId === card);
    if (card0 && playable(combat, card0)) playAnswer(combat, step, card, undefined, ['play']);
    for (let t = 0; t < 3 && !combat.result; t++) if (!scripted(combat, step, ['play', 'play', 'back'])) break;
  });
}

}
if (wants('exchanges')) buildExchanges();

// ---------------------------------------------------------------------------------------------------------------
// Write
// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
const keepOther = ONLY !== null;
if (!keepOther) for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const write = (file, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, file), text); return Buffer.byteLength(text); };
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, coveredEvents: [...COVERED_EVENTS].sort(), coveredOps: [...COVERED_OPS].sort(), deckPool: DECK_POOL, files: [] };
let total = 0;

if (wants('statics')) {
  const enemies = enemyIdsAll.map((id, i) => staticEnemy(id, i));
  const bytes = write('enemies.json', { enemies });
  total += bytes;
  index.enemies = { file: 'enemies.json', count: enemies.length, moves: enemies.reduce((s, e) => s + e.moves.length, 0), picks: enemies.reduce((s, e) => s + e.picks.reduce((t, p) => t + p.rolls.length, 0), 0), bytes };
}
if (wants('units')) {
  const bytes = write('units.json', { reactions: reactionUnits, ledger: ledgerUnits, reads: readUnits });
  total += bytes;
  index.units = { file: 'units.json', reactions: reactionUnits.length, decisions: reactionUnits.reduce((s, u) => s + u.calls.length, 0), ledger: ledgerUnits.length, reads: readUnits.length, bytes };
}

const seen = { offers: 0, played: 0, back: 0, unavailable: 0, stacked: 0, fizzled: 0, enemyStacked: 0, enemyFizzled: 0, predicted: 0, perception: 0, charged: 0 };
const tally = (steps) => {
  for (const s of steps) {
    if (s.offer) seen.offers++;
    if (s.answer === 'play') seen.played++;
    if (s.answer === 'back') seen.back++;
    for (const t of s.events) {
      if (t === 'combatReactionUnavailable') seen.unavailable++;
      if (t === 'combatReactionStacked') seen.stacked++;
      if (t === 'combatReactionFizzled') seen.fizzled++;
      if (t === 'combatEnemyReactionStacked') seen.enemyStacked++;
      if (t === 'intentPredictionAccepted') seen.predicted++;
      if (t === 'perceptionGained') seen.perception++;
    }
    for (const p of s.payloads || []) if (p.type === 'combatReactionFizzled' && p.side === 'enemy') seen.enemyFizzled++;
  }
};
if (wants('fights')) {
  const fightCount = {};
  classes.forEach((classId, ci) => {
    const fights = [];
    for (let n = 0; n < FIGHTS; n++) {
      const encounter = all[(ci * 5 + n * 7 + 1) % all.length];
      // Every fourth fight has no per-hit policy: the whole move is one root, the player may stack cards and the stack is seen at rest.
      const f = fight(sweep(111000 + ci * 100 + n), classId, encounter, 40, n % 4 === 3 ? { reactionRules: { contactPolicy: null } } : undefined);
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
}
if (wants('exchanges')) {
  for (const e of exchanges) tally(e.steps);
  const bytes = write('exchanges.json', { exchanges });
  total += bytes;
  index.exchanges = { file: 'exchanges.json', count: exchanges.length, steps: exchanges.reduce((s, e) => s + e.steps.length, 0), kinds: [...new Set(exchanges.map((e) => e.kind))], bytes };
}
index.seen = seen;
{
  // A partial run (--only) keeps the sections of the index it did not regenerate.
  const kept = ONLY && existsSync(join(OUT, 'index.json')) ? JSON.parse(readFileSync(join(OUT, 'index.json'), 'utf8')) : {};
  const merged = { ...kept, ...index, files: index.files.length ? index.files : kept.files ?? [], seen: ONLY ? kept.seen ?? index.seen : index.seen };
  writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(merged), null, 1)}\n`);
}
console.log(`oracle-core2-enemy: ${JSON.stringify(index.enemies ?? {})} ${JSON.stringify(index.units ?? {})} fights ${JSON.stringify(index.fights ?? {})} exchanges ${index.exchanges?.count ?? 0}/${index.exchanges?.steps ?? 0} ${JSON.stringify(seen)}, ${(total / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/enemy`);
if (process.argv.includes('--why')) console.log(JSON.stringify(rejected, null, 1));
void visibility; void WEB;
