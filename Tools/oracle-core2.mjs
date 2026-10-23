// Core 2 oracle harness (WP-1, docs/design/11-CORE2-PORT-PLAN.md). Shared library for the Core 2 oracles: it loads
// the web engine at the sha pinned in Tools/export/sources.json (core2.webSha), builds the `default` profile
// registries, creates runs and fights, enumerates the legal Core intents, plays a deterministic bot and projects the
// Core combat fields and the previews. Importing this file runs nothing except the drift guard (inside loadWeb).
//
//   import { loadWeb, runCombatFixture, ... } from './oracle-core2.mjs';
//
// The web checkout comes from --source, ASHEN_SOURCE or /home/user/cehinds/ashenspire. It must be at core2.webSha with
// no tracked changes: the oracle refuses otherwise, like the exporter (D-164).
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
export const ORACLE_DIR = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'core2');
export const SOURCES = JSON.parse(readFileSync(join(ROOT, 'Tools', 'export', 'sources.json'), 'utf8'));
export const PINNED_SHA = SOURCES.core2.webSha;
export const PROFILE_ID = SOURCES.core2.profile;
export const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
export const WEB = arg('--source', process.env.ASHEN_SOURCE || '/home/user/cehinds/ashenspire');

/** The same drift guard as the exporter: HEAD must be the pinned sha, and tracked files must be unmodified. */
export function guardWebSha(web = WEB) {
  const git = (...a) => execFileSync('git', ['-C', web, ...a], { encoding: 'utf8' }).trim();
  let head;
  try { head = git('rev-parse', 'HEAD'); } catch (e) { throw new Error(`oracle-core2: ${web} is not a git checkout (${e.message.split('\n')[0]})`); }
  if (!head.startsWith(PINNED_SHA)) throw new Error(`oracle-core2: web HEAD ${head.slice(0, 9)} != core2.webSha ${PINNED_SHA} in Tools/export/sources.json (re-pinning is its own WP; check out ${PINNED_SHA} in ${web})`);
  const dirty = git('status', '--porcelain', '--untracked-files=no');
  if (dirty && !process.argv.includes('--allow-dirty')) throw new Error(`oracle-core2: web checkout has tracked changes, refusing (--allow-dirty to override, as the exporter):\n${dirty}`);
  return head;
}

// ---------------------------------------------------------------------------------------------------------------
// Canonical JSON: keys sorted, functions dropped, so a fixture is independent of engine insertion order. (Order that
// is semantic for the C# port lives in the Content files and the registry dump, which keeps its own order.)
// ---------------------------------------------------------------------------------------------------------------
export function sortKeys(v) {
  if (Array.isArray(v)) return v.map(sortKeys);
  if (v && typeof v === 'object') {
    const out = {};
    for (const k of Object.keys(v).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))) {
      const x = v[k];
      if (typeof x === 'function' || x === undefined) continue;
      out[k] = sortKeys(x);
    }
    return out;
  }
  return v;
}
export const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' || x === undefined ? undefined : typeof x === 'number' && !Number.isFinite(x) ? String(x) : x)));
export const canon = (v) => sortKeys(plain(v));

/** The bot's own LCG: never the engine RNG, so choosing cannot move a stream. */
export function botLcg(seed) {
  let s = (seed >>> 0) || 1;
  return (n) => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return n <= 1 ? 0 : (s >>> 8) % n; };
}

// ---------------------------------------------------------------------------------------------------------------
// Loading
// ---------------------------------------------------------------------------------------------------------------
let cached = null;
/** Imports the web modules the oracles use and builds the `default` profile registries once. */
export async function loadWeb() {
  if (cached) return cached;
  const head = guardWebSha(WEB);
  const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
  const [content, regs, gp, state, rngMod, runCombat, combat, snapshot, enemyPreview, stanceHit, engagement, formation, rules, knowledge] = await Promise.all([
    load('src/content/index.js'), load('src/model/registries.js'), load('src/model/gameProfile.js'), load('src/model/state.js'),
    load('src/engine/rng.js'), load('src/engine/runCombat.js'), load('src/engine/combat.js'), load('src/engine/combatSnapshot.js'),
    load('src/engine/enemyActionPreview.js'), load('src/engine/stanceHit.js'), load('src/engine/combatEngagement.js'),
    load('src/model/formationMovement.js'), load('src/engine/combatRules.js'), load('src/model/enemyIntentKnowledge.js'),
  ]);
  const profile = gp.resolveGameProfile(PROFILE_ID);
  // The pipeline of plan 1.1: applyGameProfileToContent -> createRegistries -> bindGameProfileRegistries.
  const projected = gp.applyGameProfileToContent(content.contentBundle, PROFILE_ID);
  const registries = gp.bindGameProfileRegistries(regs.createRegistries(projected), profile);
  cached = { head, content, gp, state, rngMod, runCombat, combat, snapshot, enemyPreview, stanceHit, engagement, formation, rules,
    predictionChoices: knowledge.PREDICTION_CHOICES, profile, projected, registries, createRng: rngMod.createRng };
  return cached;
}

/** Class ids in registry order. */
export const classIds = (w) => w.registries.classes.all().map((c) => c.id);
/** The encounters of the `default` profile, in registry order. */
export const encounters = (w) => w.registries.encounters.all();

// All four classes must be selectable: classUnlocks grants one more class per completed run (plan 1.2).
const UNLOCK_META = { gameProfileUnlocks: { core: { completedRunIds: ['a', 'b', 'c'] } } };

/** createRunState for a seed and class, with every class unlocked. */
export function createRun(w, seed, classId) {
  return w.state.createRunState({ seed, classId, registries: w.registries, profileMeta: structuredClone(UNLOCK_META) });
}

/**
 * Builds the fight for an encounter the way main.js enterCombat does for a Core 2 run: one run RNG at the run's own
 * counters, default settings, the encounter's enemies. Returns { run, rng, combat, encounter }.
 */
export function createFight(w, run, encounter, { settings = {} } = {}) {
  const rng = w.createRng(run.seed, run.streamCounters || {});
  const combat = w.runCombat.createRunCombat({ registries: w.registries, rng, run, enemyIds: encounter.enemies, encounter, settings });
  return { run, rng, combat, encounter };
}

// ---------------------------------------------------------------------------------------------------------------
// Probing: legal intents are the ones a dispatch on a detached copy accepts (the web's own candidateState clone;
// dispatch is also transactional for Core fights, but a probe that succeeds must not touch the real fight).
// ---------------------------------------------------------------------------------------------------------------
export function detach(w, combat) { return w.rules.candidateState(combat, { detached: true }); }

export function tryIntent(w, combat, intent) {
  const copy = detach(w, combat);
  try { w.combat.dispatch(copy, structuredClone(intent)); return { ok: true }; } catch (e) { return { ok: false, error: e.message }; }
}

const livingEnemies = (combat) => combat.enemies.filter((e) => e.alive !== false && e.hp > 0);
const FORMATION_CELLS = ['A1', 'A2', 'A3', 'B1', 'B2', 'B3', 'C1', 'C2', 'C3', 'D1', 'D2', 'E1', 'F1'];

function selections(idList, count) {
  if (!count || idList.length < count) return [];
  if (count === 1) return idList.map((id) => [id]);
  return [idList.slice(0, count), idList.slice(-count)];
}

/**
 * Every legal Core intent for the current state, in a stable order. Kinds: chooseReaction (while an offer is pending),
 * chooseCards / chooseDiscard (while a card choice is pending), otherwise playCard (targets x stance choices x
 * rangeRoutes), endTurn, useFlask, setReactions, predictIntent, moveCharacter, recoverControl.
 */
export function legalIntents(w, combat, { only = null } = {}) {
  const out = [];
  if (combat.result) return out;
  const keep = (intent) => { if (tryIntent(w, combat, intent).ok) { out.push(intent); return true; } return false; };

  if (combat.pendingReaction) {
    const p = combat.pendingReaction;
    for (const o of p.options || []) keep({ type: 'chooseReaction', offerId: p.id, optionId: o.id });
    keep({ type: 'chooseReaction', offerId: p.id, optionId: null });
    return out;
  }
  if (combat.pendingCardChoice) {
    const c = combat.pendingCardChoice;
    for (const idList of selections(c.candidateIds, c.count)) keep({ type: 'chooseCards', cardInstanceIds: idList });
    return out;
  }
  if (combat.pendingAbilityDiscard) {
    const c = combat.pendingAbilityDiscard;
    for (const idList of selections(combat.piles.hand.map((x) => x.instanceId), c.count)) keep({ type: 'chooseDiscard', cardInstanceIds: idList });
    return out;
  }

  if (only === 'endTurn') { out.push({ type: 'endTurn', discardIds: [] }); return out; }
  const targets = livingEnemies(combat).map((e) => e.id);
  for (const card of combat.piles.hand) {
    let plan = null;
    try { plan = w.combat.cardChoicePlan(combat, card.instanceId); } catch { /* no plan: no choice */ }
    const choices = plan?.options?.length ? plan.options.map((o) => o.id) : [undefined];
    for (const choice of choices) {
      let aimed = true;
      try { aimed = w.combat.cardNeedsEnemyTargetNow(combat, card.instanceId); } catch { /* try every target */ }
      for (const targetId of aimed ? [undefined, ...targets] : [undefined]) {
        const base = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}), ...(choice !== undefined ? { choice } : {}) };
        if (!keep(base)) continue;
        // Range routes beyond the default 'advance' come from the card's own preview.
        let routes = [];
        try { routes = (w.combat.previewCard(combat, card.instanceId, targetId, undefined, {}).range?.routes || []).map((r) => r.id); } catch { /* none */ }
        for (const rangeRoute of routes) if (rangeRoute !== 'advance') keep({ ...base, rangeRoute });
      }
    }
  }
  keep({ type: 'endTurn', discardIds: [] });
  const p = combat.player;
  (p.flasks || []).forEach((_, slot) => keep({ type: 'useFlask', slot }));
  for (const kind of ['hp', 'mana']) if (p.flaskCharges && p.flaskCharges[`${kind}Current`] > 0) keep({ type: 'useFlask', chargeKind: kind });
  keep({ type: 'setReactions', enabled: p.reactionsEnabled === false });
  for (const enemy of livingEnemies(combat)) {
    const a = enemy.knowledgeAction;
    const read = a?.reads?.player;
    if (!a || read?.visibility !== 'unknown' || read.prediction !== null) continue;
    for (const maneuver of w.predictionChoices) keep({ type: 'predictIntent', enemyInstanceId: enemy.id, actionSerial: a.serial, maneuver });
  }
  for (const cell of FORMATION_CELLS) {
    let plan = null;
    try { plan = w.formation.formationMovePlan(combat, cell, {}); } catch { /* unavailable */ }
    if (plan?.ok) keep({ type: 'moveCharacter', cell });
  }
  for (const [status, s] of Object.entries(p.statuses || {})) if (s?.stacks > 0) keep({ type: 'recoverControl', selections: [{ status, stacks: 1 }] });
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Bots
// ---------------------------------------------------------------------------------------------------------------
export const BOTS = ['endTurnOnly', 'cards'];

/**
 * endTurnOnly: ends the turn, answering any pending choice with the first legal answer (so reaction and choice paths
 * are reached without the bot spending cards). cards: 70% a legal card, otherwise another legal intent (30%), with
 * endTurn as the always-legal fallback; every choice comes from the bot's LCG.
 */
export function pickIntent(bot, legal, pick) {
  if (!legal.length) return null;
  const pending = legal.filter((i) => ['chooseReaction', 'chooseCards', 'chooseDiscard'].includes(i.type));
  if (pending.length) return bot === 'endTurnOnly' ? pending[0] : pending[pick(pending.length)];
  const end = legal.find((i) => i.type === 'endTurn');
  if (bot === 'endTurnOnly') return end || legal[0];
  const cards = legal.filter((i) => i.type === 'playCard');
  if (cards.length && pick(10) < 7) return cards[pick(cards.length)];
  const others = legal.filter((i) => i.type !== 'playCard' && i.type !== 'endTurn');
  if (others.length && pick(10) < 3) return others[pick(others.length)];
  return end || legal[0];
}

// ---------------------------------------------------------------------------------------------------------------
// Projection of Core combat state
// ---------------------------------------------------------------------------------------------------------------
const statusMap = (e) => Object.fromEntries(Object.entries(e.statuses || {}).sort(([a], [b]) => (a < b ? -1 : 1)).map(([k, v]) => [k, v && typeof v === 'object' ? canon(v) : v]));
const cardRefs = (pile) => (pile || []).map((c) => `${c.instanceId}:${c.cardId}`);

function projectEntity(e) {
  return {
    id: e.id, ...(e.enemyId ? { enemyId: e.enemyId, level: e.level } : { classId: e.classId }),
    alive: e.alive !== false, hp: e.hp, maxHp: e.maxHp,
    stamina: e.stamina ?? null, maxStamina: e.maxStamina ?? null, mana: e.mana ?? null, maxMana: e.maxMana ?? null,
    ...(e.kind === 'player' ? { energy: e.energy, energyMax: e.energyMax, reactionsEnabled: e.reactionsEnabled ?? null } : {}),
    persistentPoise: e.persistentPoise ? { value: e.persistentPoise.value, max: e.persistentPoise.max } : null,
    poiseMeter: e.poiseMeter ? { value: e.poiseMeter.value, max: e.poiseMeter.max } : null,
    persistentWard: e.persistentWard ? { value: e.persistentWard.value, max: e.persistentWard.max } : null,
    block: e.block, barrier: e.barrier ?? 0,
    combatStance: e.combatStance ? canon(e.combatStance) : null,
    combatCounter: e.combatCounter ? canon(e.combatCounter) : null,
    statuses: statusMap(e), ratings: canon(e.ratings || {}),
    formationCell: e.formationCell ?? null,
    ...(e.kind === 'enemy' ? {
      intent: e.intent ? canon({ kind: e.intent.kind, moveId: e.intent.moveId ?? null, damage: e.intent.damage ?? null, hits: e.intent.hits ?? null, block: e.intent.block ?? null,
        barrier: e.intent.barrier ?? null, delayed: e.intent.delayed ?? false, pending: e.intent.pending ?? false, combatProfile: e.intent.combatProfile ?? null }) : null,
      plannedMoves: canon(e.plannedMoves || []), pendingMove: e.pendingMove ? canon(e.pendingMove) : null, skipNextTurn: !!e.skipNextTurn,
      knowledge: e.knowledgeAction ? canon({ serial: e.knowledgeAction.serial, category: e.knowledgeAction.category, executed: !!e.knowledgeAction.executed,
        reads: Object.fromEntries(Object.entries(e.knowledgeAction.reads || {}).map(([k, r]) => [k, { visibility: r.visibility, prediction: r.prediction, resolved: r.resolved }])) }) : null,
    } : {}),
  };
}

const projectReaction = (p) => p && canon({ id: p.id, groupKey: p.groupKey, ownerId: p.ownerId, sourceId: p.sourceId,
  options: (p.options || []).map((o) => ({ id: o.id, cardId: o.cardId, staminaCost: o.staminaCost, manaCost: o.manaCost, play: o.play })) });

/** The Core combat state the C# engine must match after every step. */
export function project(combat) {
  return canon({
    turn: combat.turn, phase: combat.phase, result: combat.result ?? null,
    player: projectEntity(combat.player),
    enemies: combat.enemies.map(projectEntity),
    piles: { hand: cardRefs(combat.piles.hand), draw: cardRefs(combat.piles.draw), discard: cardRefs(combat.piles.discard), exhaust: cardRefs(combat.piles.exhaust) },
    rangeState: combat.rangeState ? { engagements: combat.rangeState.engagements } : null,
    pendingReaction: projectReaction(combat.pendingReaction) ?? null,
    reactionStack: combat.reactionStack ? combat.reactionStack.entries.map((x) => ({ side: x.side, ownerId: x.ownerId, optionId: x.optionId, cardInstanceId: x.cardInstanceId })) : null,
    pendingCardChoice: combat.pendingCardChoice ? { count: combat.pendingCardChoice.count, candidateIds: combat.pendingCardChoice.candidateIds } : null,
    pendingAbilityDiscard: combat.pendingAbilityDiscard ? { count: combat.pendingAbilityDiscard.count } : null,
    rng: combat.rng.getCounters(),
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Previews (the same math the resolve uses). Every call is wrapped: a refusal is part of the oracle ({ error }).
// A preview must not move state: capturePreviews asserts the projection and the RNG counters are unchanged.
// ---------------------------------------------------------------------------------------------------------------
function guarded(fn) { try { return canon(fn()); } catch (e) { return { error: e.message }; } }

// previewCard and previewCardHit both embed the whole range-route quote (every route x target x contact, repeated per
// conditional rider). The oracle keeps one flat row per route instead: the numbers a UI shows and the C# quote must match.
function routeRows(range) {
  if (!range?.routes) return null;
  return range.routes.map((r) => ({
    id: r.id, available: r.available, affordable: r.affordable, reason: r.reason ?? null, advanceSp: r.advanceSp, precisionDelta: r.precisionDelta, costs: r.costs,
    targets: (r.targets || []).map((t) => ({ targetId: t.targetId, band: t.band, inRange: t.inRange, hitChance: t.hitChance, hitChances: t.hitChances, currentHitChance: t.currentHitChance, conditional: t.conditional })),
  }));
}
// `resolvedDefinition` is the content row (held by the registry dump); `name` is display text.
function slim(p, { dropRange = false } = {}) {
  if (!p || typeof p !== 'object') return p;
  const { range, resolvedDefinition, name, ...rest } = p;
  return { ...rest, ...(range && !dropRange ? { range: { enabled: range.enabled, choiceRequired: range.choiceRequired, routes: routeRows(range) } } : {}) };
}

export function capturePreviews(w, combat) {
  if (combat.result) return null;
  const before = JSON.stringify(project(combat));
  const C = w.combat;
  const target = livingEnemies(combat)[0]?.id;
  const out = { cards: {}, enemies: {} };
  for (const card of combat.piles.hand) {
    const id = card.instanceId;
    out.cards[id] = {
      cardId: card.cardId,
      costs: guarded(() => C.cardPlayCosts(combat, id)),
      needsEnemyTarget: guarded(() => C.cardNeedsEnemyTargetNow(combat, id)),
      choices: guarded(() => C.cardChoicePlan(combat, id)),
      preview: guarded(() => slim(C.previewCard(combat, id, target))),
      hit: target ? guarded(() => slim(C.previewCardHit(combat, id, target, undefined, { observerId: 'player' }), { dropRange: true })) : null,
    };
  }
  for (const enemy of livingEnemies(combat)) {
    out.enemies[enemy.id] = {
      intent: guarded(() => C.previewIntent(combat, enemy.id)),
      action: enemy.intent?.moveId ? guarded(() => w.enemyPreview.previewEnemyAction(combat, enemy, enemy.intent.moveId)) : null,
    };
  }
  if (JSON.stringify(project(combat)) !== before) throw new Error('oracle-core2: a preview mutated the combat (state or RNG counters moved)');
  return canon(out);
}

// ---------------------------------------------------------------------------------------------------------------
// A combat fixture: the run and fight inputs, the opening projection and previews, then each step.
// ---------------------------------------------------------------------------------------------------------------
export function runCombatFixture(w, { seed, classId, encounterId, bot, maxSteps = 60, previews = true }) {
  const encounter = encounters(w).find((e) => e.id === encounterId);
  if (!encounter) throw new Error(`oracle-core2: unknown encounter '${encounterId}'`);
  const run = createRun(w, seed, classId);
  const { combat, rng } = createFight(w, run, encounter);
  const pick = botLcg(seed ^ 0x9e3779b9);
  const start = { ...project(combat), previews: previews ? capturePreviews(w, combat) : null };
  const steps = [];
  for (let i = 0; i < maxSteps && !combat.result; i++) {
    const legal = legalIntents(w, combat, { only: bot === 'endTurnOnly' ? 'endTurn' : null });
    const intent = pickIntent(bot, legal, pick);
    if (!intent) break;
    let error = null;
    try { w.combat.dispatch(combat, structuredClone(intent)); } catch (e) { error = e.message; }
    steps.push({ intent, legalCount: bot === 'endTurnOnly' ? null : legal.length, ...(error ? { error } : {}), after: { ...project(combat), previews: previews ? capturePreviews(w, combat) : null } });
  }
  return canon({
    seed, classId, encounterId, bot, profileHash: w.profile.hash, webSha: w.head,
    run: { streamCounters: run.streamCounters ?? {}, deck: (run.deck || []).map((c) => (typeof c === 'string' ? c : c.cardId ?? c.id)), maxHp: run.maxHp },
    rngCounters: rng.getCounters(), start, steps, result: combat.result ?? null,
  });
}

// ---------------------------------------------------------------------------------------------------------------
// The projection: the combat object field by field. What is static (the rule snapshots, the pinned profile, the registries)
// is recorded once per class under `statics`; everything a turn can move is recorded at every step.
// ---------------------------------------------------------------------------------------------------------------
export const STATIC_KEYS = ['fixCoreRules', 'combatExpansionRules', 'reactionRules', 'combatMatchupRules', 'combatIntentRules'];
export const SKIPPED = new Set(['registries', 'rng', 'eventLog', 'triggerState', 'gameProfile', 'foundation', 'propertyMounts', 'enemyKnowledge', 'player', 'enemies',
  'piles', 'loadout', 'equipmentProfileRuleSnapshot', 'derivedStatRuleSnapshot', 'queue', '_buffer', '_emitDepth', '_idCounter', 'itemMounts', ...STATIC_KEYS]);

function mounts(combat) {
  const out = {};
  for (const [owner, sources] of Object.entries(combat.propertyMounts || {})) {
    out[owner] = Object.fromEntries(Object.entries(sources).map(([key, m]) => [key, { kind: m.kind, id: m.id, instanceId: m.instanceId, scopeTags: m.scopeTags, ruleTags: m.rules.map((r) => r.tag),
      ...(m.configuredEnemy ? { configuredEnemy: true } : {}) }]));
  }
  return out;
}

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

export function projectFull(combat) {
  const out = {};
  for (const [key, value] of Object.entries(combat)) if (!SKIPPED.has(key) && typeof value !== 'function') out[key] = value;
  // The offer without the previews the UI reads (the intent preview, an option's range quote); the queue a reaction card interrupted as its length.
  if (out.pendingReaction) {
    const { intentPreview, ...offer } = out.pendingReaction;
    out.pendingReaction = { ...offer, options: offer.options.map(({ range, ...option }) => option) };
  }
  if (out.reactionResume) out.reactionResume = { queue: out.reactionResume.queue.length, foundation: out.reactionResume.foundation ?? null };
  // The card whose play is held (pendingAbilityPlay, reactionRootPlay) holds its instance, carrier and snapshot by reference in the web: the record keeps what identifies it.
  for (const key of ['pendingAbilityPlay', 'reactionRootPlay']) {
    const play = out[key];
    if (play?.instance) out[key] = canon({ instanceId: play.instance.instanceId, cardId: play.instance.cardId, kind: play.kind, printedManaCost: play.printedManaCost, targetId: play.targetId, ...(play.deferredRoot ? { deferredRoot: true } : {}) });
  }
  if (out.reactionStack?.entries) out.reactionStack = { ...out.reactionStack, entries: out.reactionStack.entries.map((entry) => (entry.side === 'enemy' ? { ...entry, plan: canon(compactPlan(entry.plan)) } : entry)) };
  const knowledge = combat.enemyKnowledge ? { ...combat.enemyKnowledge } : null;
  if (knowledge) delete knowledge.rules;
  return canon({
    ...out,
    piles: Object.fromEntries(Object.entries(combat.piles).map(([pile, cards]) => [pile, cards])),
    player: combat.player, enemies: combat.enemies,
    enemyKnowledge: knowledge,
    propertyMounts: mounts(combat),
    triggerState: Object.fromEntries([...combat.triggerState.entries()].sort(([a], [b]) => (a < b ? -1 : 1))),
    queue: combat.queue.length,
    rng: combat.rng.getCounters(),
  });
}
export const statics = (combat) => canon({ ...Object.fromEntries(STATIC_KEYS.map((k) => [k, combat[k]])), enemyKnowledgeRules: combat.enemyKnowledge.rules });
export const typesSince = (combat, mark) => combat.eventLog.slice(mark).map((e) => e.type);

// A step records what changed since the one before it: the full projection at the start of a fight or scenario, then a delta.
// Objects are walked key by key, arrays of the same length index by index ({"$a": {index: delta}}); anything else that differs (a
// scalar, an array that changed length, a value that changes kind) is replaced whole as {"$": value}; a key that went away is
// {"$del": 1}. The test rebuilds each full state by applying the deltas in order.
export const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
export function delta(prev, next) {
  if (isObject(prev) && isObject(next)) {
    const out = {};
    for (const key of Object.keys(next)) { const d = delta(prev[key], next[key]); if (d !== undefined) out[key] = d; }
    for (const key of Object.keys(prev)) if (!(key in next)) out[key] = { $del: 1 };
    return Object.keys(out).length ? out : undefined;
  }
  if (Array.isArray(prev) && Array.isArray(next) && prev.length === next.length) {
    const at = {};
    next.forEach((item, i) => { const d = delta(prev[i], item); if (d !== undefined) at[i] = d; });
    return Object.keys(at).length ? { $a: at } : undefined;
  }
  return JSON.stringify(prev) === JSON.stringify(next) ? undefined : { $: next };
}


// ---------------------------------------------------------------------------------------------------------------
// Reactions (WP-10). The player's reaction offers are answered by the fight oracles' bots, and every offer must stay inside what the port covers.
// ---------------------------------------------------------------------------------------------------------------
export const REACTION_EVENTS = ['combatReactionOffered', 'combatReactionChosen', 'combatReactionStacked', 'combatReactionUnavailable', 'combatReactionFizzled', 'combatEnemyReactionStacked'];

/**
 * The bot's answers to the offers the enemies' hits open: half the time it plays the first option of the offer, half the time it goes Back, from its
 * own LCG (never the engine's). Cloneable, so a dry run answers exactly as the real run will.
 */
export function answerer(seed) {
  let s = (seed >>> 0) || 1;
  const draw = () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return (s >>> 8) % 2; };
  return {
    clone: () => answerer(s),
    /** The chooseReaction intent for the offer the fight is paused on. */
    next(combat) {
      const pending = combat.pendingReaction;
      const play = draw() === 0 && pending.options.length > 0;
      return { type: 'chooseReaction', offerId: pending.id, optionId: play ? pending.options[0].id : null };
    },
  };
}

let reactionModules = null;
/** The web modules the offer filter reads (loaded once). */
async function reactionTools(w) {
  if (reactionModules) return reactionModules;
  const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
  const [expansion, triggers] = await Promise.all([load('src/engine/combatExpansion.js'), load('src/engine/triggers.js')]);
  reactionModules = { expansion, triggers };
  return reactionModules;
}

let filter = null;
/**
 * Loads the offer filter (once): why an offer the fight is paused on is outside what the port covers. The port plays every reaction card of the hand
 * on a copy of the fight to build the offer; it refuses a card that listens for being prepared (WP-9 leaves it). Range, lineage and a reply that pays
 * to advance are inside (WP-12).
 */
export async function reactionFilter(w) {
  if (filter) return filter;
  const { expansion, triggers } = await reactionTools(w);
  const tagsOf = (def) => (def.cardTags || def.tags || []).map((t) => (typeof t === 'string' ? t : t.id));
  filter = {
    offerOutside(combat) {
      for (const card of combat.piles.hand) {
        const def = expansion.resolveCombatCard(combat, card);
        const tags = tagsOf(def);
        if (!tags.includes('trait:reaction')) continue;
        if (triggers.hasEventTriggers(combat, 'cardPreparing', { cardTags: tags })) return 'cardPreparing listener at an offer';
      }
      return null;
    },
  };
  return filter;
}

/**
 * Dispatches the intent on a detached copy and answers every offer it opens with a copy of the bot's answers. Returns { copy, error, why }: `why` names
 * the first thing outside the filter (an offer the port cannot build).
 */
export function dispatchAnswering(w, combat, intent, answers) {
  if (!filter) throw new Error('oracle-core2: await reactionFilter(w) before dispatchAnswering');
  const copy = detach(w, combat);
  const mark = copy.eventLog.length;
  const mine = answers.clone();
  try {
    w.combat.dispatch(copy, structuredClone(intent));
    for (let n = 0; copy.pendingReaction; n++) {
      if (n > 60) return { copy, mark, why: 'too many offers' };
      const outside = filter.offerOutside(copy);
      if (outside) return { copy, mark, why: outside };
      w.combat.dispatch(copy, mine.next(copy));
    }
  } catch (e) { return { copy, mark, error: e.message }; }
  return { copy, mark };
}
