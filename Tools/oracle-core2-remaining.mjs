#!/usr/bin/env node
// Core 2 remaining-features oracle (WP-C0, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED Core 2 combat (createRunCombat + dispatch) with a bot that
// plays ANY intent a player can send (a card with every target, stance choice and range route, endTurn, useFlask by slot and by charge pool, setReactions,
// predictIntent, recoverControl, chooseReaction, chooseCards, chooseDiscard, swapArmament, changeEquipment) and records every step in the shape the damage
// fights use, so the same replay holds the port to the web after every step. There is NO filter: the enemies take every turn in full, every offer and every
// choice is answered, nothing is staggered. The bot offers an intent to the web and records it when the web takes it; what the web refuses is tallied by its
// own message (`--why`): those are the web's rejections, never the oracle's.
//
//   node Tools/oracle-core2-remaining.mjs [--seeds 1] [--steps 40] [--classes reaver,rogue] [--only <feature|plain>] [--encounters 12] [--why] [--source <web checkout>]
//
// With no --classes it runs one process per class and merges the tally. Files (Unity/Assets/Tests/Oracle/core2/remaining/, sorted keys, byte-identical on rerun):
//   fights-<class>.json   one fight for every encounter of the profile (35) per seed, plus the `featured` fights: the same bot on a run that was handed a
//                         feature the default start does not hold (flasks in the slots, relics, cards that open choices, charges or listeners, a second weapon set).
//                         A fight is { seed, classId, encounterId, bot, spec, start: { events, state }, steps: [{ intent, events, delta }] }.
//   index.json            the tally: fights, steps, the intents, events, ops reached, the web's refusals, and the REJECTIONS (steps this oracle left out: none).
import { writeFileSync, readFileSync, mkdirSync, rmSync, readdirSync, existsSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadWeb, ORACLE_DIR, arg, canon, classIds, encounters, createRun, createFight, botLcg, projectFull, delta, typesSince } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'remaining');
const WHY = process.argv.includes('--why');
const SEEDS = Number(arg('--seeds', '1'));
const STEPS = Number(arg('--steps', '40'));
const ONLY = arg('--only', null);
const ENCOUNTERS = Number(arg('--encounters', '1000'));
const HERE = dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------------------------------------------
// The specs: a run is created from (seed, class) and handed what the spec names before the fight opens.
// ---------------------------------------------------------------------------------------------------------------
export function applySpec(run, spec) {
  for (const [i, cardId] of (spec.deckAdd || []).entries()) run.deck.push({ instanceId: `rm${i + 1}`, cardId, upgraded: false });
  for (const flaskId of spec.flasks || []) run.flasks.push({ flaskId });
  for (const id of spec.relics || []) if (!run.relics.includes(id)) run.relics.push(id);
  if (spec.hp !== undefined) run.hp = spec.hp;
  return run;
}

/** The runs the default start does not hold but a run can (a reward, a shop, an event). Each is played against a rotation of encounters. */
export const FEATURED = [
  { name: 'flasks', spec: { flasks: ['crimsonFlask', 'azureFlask', 'flaskOfFerocity'] } },
  { name: 'flasks2', spec: { flasks: ['flaskOfStone', 'blightCoating', 'bloodUnction'] } },
  { name: 'wondrous', spec: { flasks: ['wondrousDraught', 'wondrousDraught', 'crimsonFlask'] } },
];

const tally = { fights: 0, steps: 0, intents: {}, events: {}, refused: {}, rejections: {}, attempts: 0 };
const count = (map, key, n = 1) => { map[key] = (map[key] || 0) + n; };

// ---------------------------------------------------------------------------------------------------------------
// The bot
// ---------------------------------------------------------------------------------------------------------------
const living = (combat) => combat.enemies.filter((e) => e.alive !== false && e.hp > 0);

/** Every intent worth offering for the current state, in a stable order, each with a weight class. */
function candidates(w, combat, pick) {
  const out = [];
  if (combat.pendingReaction) {
    const p = combat.pendingReaction;
    for (const o of p.options || []) out.push({ intent: { type: 'chooseReaction', offerId: p.id, optionId: o.id }, kind: 'answer' });
    out.push({ intent: { type: 'chooseReaction', offerId: p.id, optionId: null }, kind: 'answer' });
    return out;
  }
  if (combat.pendingCardChoice) {
    const c = combat.pendingCardChoice;
    const ids = c.candidateIds;
    if (ids.length >= c.count) {
      out.push({ intent: { type: 'chooseCards', cardInstanceIds: ids.slice(0, c.count) }, kind: 'answer' });
      out.push({ intent: { type: 'chooseCards', cardInstanceIds: ids.slice(-c.count) }, kind: 'answer' });
    }
    return out;
  }
  if (combat.pendingAbilityDiscard) {
    const c = combat.pendingAbilityDiscard;
    const ids = combat.piles.hand.map((x) => x.instanceId);
    if (ids.length >= c.count) {
      out.push({ intent: { type: 'chooseDiscard', cardInstanceIds: ids.slice(0, c.count) }, kind: 'answer' });
      out.push({ intent: { type: 'chooseDiscard', cardInstanceIds: ids.slice(-c.count) }, kind: 'answer' });
    }
    return out;
  }
  const targets = living(combat).map((e) => e.id);
  for (const card of combat.piles.hand) {
    let plan = null;
    try { plan = w.combat.cardChoicePlan(combat, card.instanceId); } catch { /* no plan: no choice */ }
    const choices = plan?.options?.length ? plan.options.map((o) => o.id) : [undefined];
    let aimed = true;
    try { aimed = w.combat.cardNeedsEnemyTargetNow(combat, card.instanceId); } catch { /* try every target */ }
    for (const choice of choices) for (const targetId of aimed ? targets : [undefined]) {
      out.push({ intent: { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}), ...(choice !== undefined ? { choice } : {}) }, kind: 'card' });
    }
  }
  const p = combat.player;
  (p.flasks || []).forEach((flask, slot) => {
    const def = w.registries.flasks.get(flask.flaskId);
    for (const targetId of def.targeted ? targets : [undefined]) out.push({ intent: { type: 'useFlask', slot, ...(targetId ? { targetId } : {}) }, kind: 'flask' });
  });
  for (const kind of ['hp', 'mana']) out.push({ intent: { type: 'useFlask', chargeKind: kind }, kind: 'flask' });
  out.push({ intent: { type: 'setReactions', enabled: p.reactionsEnabled === false }, kind: 'other' });
  for (const enemy of living(combat)) {
    const a = enemy.knowledgeAction;
    const read = a?.reads?.player;
    if (!a || read?.visibility !== 'unknown' || read.prediction !== null) continue;
    for (const maneuver of w.predictionChoices) out.push({ intent: { type: 'predictIntent', enemyInstanceId: enemy.id, actionSerial: a.serial, maneuver }, kind: 'other' });
  }
  for (const [status, s] of Object.entries(p.statuses || {})) if (s?.stacks > 0) out.push({ intent: { type: 'recoverControl', selections: [{ status, stacks: 1 }] }, kind: 'other' });
  // The equipment doors: cycling the sets a loadout holds, as the web's own screen offers them.
  for (const [slotId, sets] of Object.entries(combat.loadout?.sets || {})) {
    if (!Array.isArray(sets) || sets.filter(Boolean).length < 2) continue;
    sets.forEach((piece, setIndex) => { if (piece && combat.loadout.active[slotId] !== setIndex) out.push({ intent: { type: 'swapArmament', slotId, setIndex }, kind: 'swap' }); });
  }
  return out;
}

/** The weights: cards most of the time, a flask a tenth of it, anything else a few percent, the end of the turn when nothing was left (or 1 in 12). */
function choose(list, pick) {
  const by = (kind) => list.filter((x) => x.kind === kind);
  const roll = pick(100);
  const cards = by('card'), flasks = by('flask'), others = by('other'), swaps = by('swap'), answers = by('answer');
  if (answers.length) return answers[pick(answers.length)];
  if (cards.length && roll < 66) return cards[pick(cards.length)];
  if (flasks.length && roll < 78) return flasks[pick(flasks.length)];
  if (swaps.length && roll < 84) return swaps[pick(swaps.length)];
  if (others.length && roll < 90) return others[pick(others.length)];
  return null;
}

/** Plays one fight with the free bot and records it. */
function fight(w, seed, classId, encounter, spec, steps) {
  const run = applySpec(createRun(w, seed, classId), spec);
  const { combat } = createFight(w, run, encounter);
  const pick = botLcg(seed ^ 0x51ed270b);
  let last = projectFull(combat);
  const start = { events: typesSince(combat, 0), state: last };
  const record = [];
  const offer = (intent) => {
    const mark = combat.eventLog.length;
    count(tally, 'attempts');
    try { w.combat.dispatch(combat, structuredClone(intent)); } catch (e) {
      // A refusal leaves the fight as it was: the oracle holds the web to it.
      const now = projectFull(combat);
      if (JSON.stringify(now) !== JSON.stringify(last)) throw new Error(`oracle-core2-remaining: a refused ${intent.type} (${e.message}) moved the fight`);
      count(tally.refused, `${intent.type}: ${e.message.replace(/\d+/g, 'N')}`);
      return false;
    }
    const state = projectFull(combat);
    const events = typesSince(combat, mark);
    record.push({ intent, events, delta: delta(last, state) ?? {} });
    count(tally.intents, intent.type);
    for (const e of events) count(tally.events, e);
    last = state;
    return true;
  };
  for (let i = 0; i < steps && !combat.result; i++) {
    const list = candidates(w, combat, pick);
    let done = false;
    for (let tries = 0; tries < 6 && !done; tries++) {
      const choice = choose(list, pick);
      if (!choice) break;
      done = offer(choice.intent);
      if (!done) list.splice(list.indexOf(choice), 1);
    }
    if (!done) {
      if (combat.pendingReaction || combat.pendingCardChoice || combat.pendingAbilityDiscard) throw new Error(`oracle-core2-remaining: no answer for a pending choice in ${encounter.id} (${seed})`);
      offer({ type: 'endTurn', discardIds: [] });
    }
  }
  tally.fights++; tally.steps += record.length;
  return { seed, classId, encounterId: encounter.id, bot: 'free', spec, start, steps: record };
}

// ---------------------------------------------------------------------------------------------------------------
// One class (a child process) and the merge
// ---------------------------------------------------------------------------------------------------------------
async function runClass(classId, ci) {
  const w = await loadWeb();
  const all = encounters(w).slice(0, ENCOUNTERS);
  const fights = [];
  if (!ONLY || ONLY === 'plain') {
    for (let n = 0; n < SEEDS; n++) all.forEach((encounter, ei) => fights.push(fight(w, w.rngMod.sweepSeed(60000 + ci * 1000 + n * 100 + ei), classId, encounter, {}, STEPS)));
  }
  FEATURED.forEach((feature, fi) => {
    if (ONLY && ONLY !== feature.name) return;
    for (let n = 0; n < 6; n++) {
      const encounter = all[(ci * 5 + n * 3 + fi) % Math.min(12, all.length)];
      const fx = fight(w, w.rngMod.sweepSeed(90000 + ci * 1000 + fi * 100 + n), classId, encounter, feature.spec, STEPS);
      fx.feature = feature.name;
      fights.push(fx);
    }
  });
  mkdirSync(OUT, { recursive: true });
  const text = `${JSON.stringify(canon({ classId, fights }))}\n`;
  writeFileSync(join(OUT, `fights-${classId}.json`), text);
  writeFileSync(join(OUT, `tally-${classId}.tmp`), JSON.stringify({ classId, file: `fights-${classId}.json`, fights: fights.length, steps: fights.reduce((s, f) => s + f.steps.length, 0), bytes: Buffer.byteLength(text), tally }));
}

const classArg = arg('--classes', null);
if (classArg) {
  const w = await loadWeb();
  const all = classIds(w);
  for (const classId of classArg.split(',')) await runClass(classId, all.indexOf(classId));
} else {
  const w = await loadWeb();
  const all = classIds(w);
  mkdirSync(OUT, { recursive: true });
  for (const f of readdirSync(OUT)) if (f.endsWith('.json') || f.endsWith('.tmp')) rmSync(join(OUT, f));
  const passthrough = process.argv.slice(2);
  await Promise.all(all.map((classId) => new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [join(HERE, 'oracle-core2-remaining.mjs'), ...passthrough, '--classes', classId], { stdio: ['ignore', 'ignore', 'inherit'] });
    child.on('exit', (code) => (code === 0 ? resolve() : reject(new Error(`${classId} exited ${code}`))));
  })));
  const merged = { fights: 0, steps: 0, intents: {}, events: {}, refused: {}, rejections: {}, attempts: 0 };
  const files = [];
  for (const classId of all) {
    const part = JSON.parse(readFileSync(join(OUT, `tally-${classId}.tmp`), 'utf8'));
    rmSync(join(OUT, `tally-${classId}.tmp`));
    files.push({ file: part.file, classId, fights: part.fights, steps: part.steps, bytes: part.bytes });
    for (const key of ['fights', 'steps', 'attempts']) merged[key] += part.tally[key];
    for (const key of ['intents', 'events', 'refused', 'rejections']) for (const [name, n] of Object.entries(part.tally[key])) count(merged[key], name, n);
  }
  const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, files, tally: merged };
  writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
  const rejected = Object.values(merged.rejections).reduce((s, n) => s + n, 0);
  if (WHY) console.log('web refusals', merged.refused, '\nintents', merged.intents);
  console.log(`oracle-core2-remaining: ${merged.fights} fights / ${merged.steps} steps (${merged.attempts} offered), rejections by the oracle: ${rejected}, ${(files.reduce((s, f) => s + f.bytes, 0) / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/remaining`);
}
