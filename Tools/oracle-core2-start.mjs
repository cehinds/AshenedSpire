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
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, botLcg, projectFull, delta, statics, typesSince } from './oracle-core2.mjs';

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
