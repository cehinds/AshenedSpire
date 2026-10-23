#!/usr/bin/env node
// Core 2 combat-start oracle (WP-4, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED createRunCombat for the configured
// `default` profile and records the whole fight state the instant it opens (turn 1, hand drawn, first intents rolled), then
// the end-turn loop with no card play.
//
//   node Tools/oracle-core2-start.mjs [--turns 3] [--long-turns 9] [--seeds 1] [--classes ...] [--source <web checkout>]
//
// The end-turn loop (what `oracle-core2-combat --bot endTurnOnly` cannot give a port before WP-6 to WP-11): the endTurnOnly bot's
// first dispatch reaches the enemy's first attack, which opens the player's reaction window and then needs damage, hit rolls and
// reactions. This oracle instead marks every living enemy staggered (`skipNextTurn`, exactly the flag a Poise break sets) before each
// endTurn, so the enemy phase runs in full (turn-start hooks, status decay, the knowledge action cancelled, the turn-end hooks)
// without an action; the round refresh shuffles the whole deck on the `shuffle` stream, the intents are re-rolled on `enemyAI`,
// `enemyIntentVisibility` and `enemyIntentClue`, and the next hand is drawn. Every step records the full projection.
//
// Cases: 4 classes x every encounter of the profile (35) x `--seeds` seeds, rotated so each class meets every encounter; the first
// two encounters of each class run `--long-turns` turns (enough for several round shuffles), the rest `--turns`. A second file,
// `hand.json`, drives the hand functions directly on prepared piles (draw count, round refresh, discard reshuffle, end-of-turn fate).
// Writes Unity/Assets/Tests/Oracle/core2/start/*.json (sorted keys; two runs are byte-identical).
import { writeFileSync, mkdirSync, rmSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, botLcg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'start');
const TURNS = Number(arg('--turns', '3'));
const LONG_TURNS = Number(arg('--long-turns', '9'));
const SEEDS = Number(arg('--seeds', '1'));
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const actions = await load('src/engine/actions.js');
const handRules = await load('src/engine/handRules.js');
const classes = arg('--classes', classIds(w).join(',')).split(',');
const all = encounters(w);

// ---------------------------------------------------------------------------------------------------------------
// The projection: the combat object field by field. What is static (the rule snapshots, the pinned profile, the registries)
// is recorded once per class under `statics`; everything a turn can move is recorded at every step.
// ---------------------------------------------------------------------------------------------------------------
const STATIC_KEYS = ['fixCoreRules', 'combatExpansionRules', 'reactionRules', 'combatMatchupRules', 'combatIntentRules'];
const SKIPPED = new Set(['registries', 'rng', 'eventLog', 'triggerState', 'gameProfile', 'foundation', 'propertyMounts', 'enemyKnowledge', 'player', 'enemies',
  'piles', 'loadout', 'equipmentProfileRuleSnapshot', 'derivedStatRuleSnapshot', 'queue', '_buffer', '_emitDepth', '_idCounter', 'itemMounts', ...STATIC_KEYS]);

function mounts(combat) {
  const out = {};
  for (const [owner, sources] of Object.entries(combat.propertyMounts || {})) {
    out[owner] = Object.fromEntries(Object.entries(sources).map(([key, m]) => [key, { kind: m.kind, id: m.id, instanceId: m.instanceId, scopeTags: m.scopeTags, ruleTags: m.rules.map((r) => r.tag),
      ...(m.configuredEnemy ? { configuredEnemy: true } : {}) }]));
  }
  return out;
}

export function projectFull(combat) {
  const out = {};
  for (const [key, value] of Object.entries(combat)) if (!SKIPPED.has(key) && typeof value !== 'function') out[key] = value;
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
const statics = (combat) => canon({ ...Object.fromEntries(STATIC_KEYS.map((k) => [k, combat[k]])), enemyKnowledgeRules: combat.enemyKnowledge.rules });
const typesSince = (combat, mark) => combat.eventLog.slice(mark).map((e) => e.type);

// A step records what changed since the one before it: the full projection at the start of a fight or scenario, then a delta.
// Objects are walked key by key, arrays of the same length index by index ({"$a": {index: delta}}); anything else that differs (a
// scalar, an array that changed length, a value that changes kind) is replaced whole as {"$": value}; a key that went away is
// {"$del": 1}. The test rebuilds each full state by applying the deltas in order.
const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
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
// Fights
// ---------------------------------------------------------------------------------------------------------------
function fight(seed, classId, encounter, turns, withStatics = false) {
  const run = createRun(w, seed, classId);
  const { combat } = createFight(w, run, encounter);
  let last = projectFull(combat);
  const start = { events: typesSince(combat, 0), state: last };
  const steps = [];
  for (let i = 0; i < turns && !combat.result; i++) {
    const mark = combat.eventLog.length;
    for (const enemy of combat.enemies) if (enemy.alive) enemy.skipNextTurn = true;
    let error = null;
    try { w.combat.dispatch(combat, { type: 'endTurn', discardIds: [] }); } catch (e) { error = e.message; }
    const state = projectFull(combat);
    steps.push({ staggered: combat.enemies.filter((e) => e.alive).length, ...(error ? { error } : {}), events: typesSince(combat, mark), delta: delta(last, state) ?? {} });
    last = state;
    if (error || combat.pendingReaction) break;
  }
  return { seed, classId, encounterId: encounter.id, start, steps, ...(withStatics ? { statics: statics(combat) } : {}) };
}

// ---------------------------------------------------------------------------------------------------------------
// Hand scenarios: the hand functions on piles prepared away from what a fight reaches by itself.
// ---------------------------------------------------------------------------------------------------------------
function handScenario(seed, classId, encounter, index) {
  const pick = botLcg(seed ^ 0x51ed270b);
  const run = createRun(w, seed, classId);
  const { combat } = createFight(w, run, encounter);
  const setup = { toDiscard: 0, drawKeep: 0, exhaust: 0, turn: combat.turn };
  const cards = [...combat.piles.hand, ...combat.piles.draw, ...combat.piles.discard];
  // Play some of the hand (to the discard), leave a random part of the draw pile, exhaust a card or two.
  setup.toDiscard = pick(combat.piles.hand.length + 1);
  for (let i = 0; i < setup.toDiscard; i++) combat.piles.discard.push(combat.piles.hand.splice(pick(combat.piles.hand.length), 1)[0]);
  setup.drawKeep = pick(combat.piles.draw.length + 1);
  while (combat.piles.draw.length > setup.drawKeep) combat.piles.discard.push(combat.piles.draw.pop());
  setup.exhaust = pick(3) === 0 ? 1 : 0;
  if (setup.exhaust && combat.piles.discard.length) combat.piles.exhaust.push(combat.piles.discard.splice(pick(combat.piles.discard.length), 1)[0]);
  setup.turn = 1 + pick(4);
  combat.turn = setup.turn;
  let last = projectFull(combat);
  const record = { setup, before: last, cards: cards.length };
  const step = () => { const state = projectFull(combat); const d = delta(last, state) ?? {}; last = state; return d; };
  const mark = combat.eventLog.length;
  const draws = pick(8);
  actions.drawCards(combat, draws);
  record.draw = { n: draws, events: typesSince(combat, mark), delta: step() };
  // The turn-end move: ordinary unplayed cards return (the round refresh), Ethereal exhaust, then the next turn's count.
  const fates = combat.piles.hand.map((card) => ({ id: card.instanceId, fate: handRules.endTurnCardFate(combat, card) }));
  const keep = [], toDiscard = [], toExhaust = [];
  for (const card of combat.piles.hand) {
    const fate = handRules.endTurnCardFate(combat, card);
    (fate === 'keep' ? keep : fate === 'exhaust' ? toExhaust : toDiscard).push(card);
  }
  combat.piles.hand = keep;
  for (const card of toExhaust) combat.piles.exhaust.push(card);
  const mark2 = combat.eventLog.length;
  handRules.returnUnplayedCards(combat, toDiscard);
  record.refresh = { fates, events: typesSince(combat, mark2), delta: step() };
  combat.turn = setup.turn + 1;
  const plan = handRules.discardChoicePlan(combat);
  const count = handRules.turnDrawCount(combat);
  const mark3 = combat.eventLog.length;
  actions.drawCards(combat, count);
  record.next = { plan: { minimum: plan.minimum, maximum: plan.maximum, prompt: plan.prompt, cards: plan.cards.length }, count, events: typesSince(combat, mark3), delta: step() };
  return { seed, classId, encounterId: encounter.id, index, ...record };
}

// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json') && (f.startsWith('start-') || f === 'hand.json' || f === 'index.json')) rmSync(join(OUT, f));

const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, turns: TURNS, longTurns: LONG_TURNS, files: [] };
let total = 0;
classes.forEach((classId, ci) => {
  const fights = [];
  for (let s = 0; s < SEEDS; s++) all.forEach((encounter, ei) => {
    const seed = w.rngMod.sweepSeed(7000 + ci * 1000 + s * 100 + ei);
    // Rotate the long ones so every class meets different enemies for its several-turn fights.
    const long = (ei + ci * 7) % all.length < 2;
    fights.push(fight(seed, classId, encounter, long ? LONG_TURNS : TURNS, fights.length === 0));
  });
  const file = `start-${classId}.json`;
  const text = `${JSON.stringify(canon({ classId, fights }))}\n`;
  writeFileSync(join(OUT, file), text);
  total += Buffer.byteLength(text);
  index.files.push({ file, classId, fights: fights.length, steps: fights.reduce((n, f) => n + f.steps.length, 0), bytes: Buffer.byteLength(text) });
});
const scenarios = [];
classes.forEach((classId, ci) => all.forEach((encounter, ei) => {
  if ((ei + ci) % 3 !== 0) return;
  scenarios.push(handScenario(w.rngMod.sweepSeed(9000 + ci * 1000 + ei), classId, encounter, scenarios.length));
}));
const handText = `${JSON.stringify(canon({ scenarios }))}\n`;
writeFileSync(join(OUT, 'hand.json'), handText);
total += Buffer.byteLength(handText);
index.hand = { file: 'hand.json', scenarios: scenarios.length, bytes: Buffer.byteLength(handText) };
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-start: ${index.files.reduce((n, f) => n + f.fights, 0)} fights, ${index.files.reduce((n, f) => n + f.steps, 0)} steps, ${scenarios.length} hand scenarios, ${(total / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/start`);
