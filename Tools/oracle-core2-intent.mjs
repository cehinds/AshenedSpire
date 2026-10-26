#!/usr/bin/env node
// Core 2 enemy intent projection oracle (P-1, docs/DECISIONS.md D-1320..). Drives the SHIPPED web at core2.webSha for the configured `default` profile and records what
// the player is told of an enemy's revealed move: engine/combat.js previewIntent through ui/models/CombatIntentProjectionModel.js projectCombatIntent (the HP the move
// can take as a range, '0-16', with the baseline it is judged against), ui/models/CombatIntentModel.js intentDamageRead (the badge number and the 'Total 0-16 HP' line)
// and combatIntentPresentation (the badge: title, stance, damage class, parts).
//
//   fights-<class>.json   fights (a class x encounters) played by the end-turn bot (so the enemies roll many moves); after the opening and after each turn, under setups
//                         laid on a detached copy of the fight: the move unread, read, read with Block, in a Defend / Evade / Counter / Attack stance, with Barrier, with
//                         Vulnerable, with an emptied Poise; per living enemy the projected intent and its badge.
//   index.json
//
//   node Tools/oracle-core2-intent.mjs [--source <web checkout>] [--classes herald,reaver] [--encounters 4] [--turns 5]
//
// Deterministic. Writes Unity/Assets/Tests/Oracle/core2/intent/*.json (sorted keys). Probabilities are rounded to nine decimals (the C# walk adds in the same order, the
// rounding only removes the last-bit noise of summing floats).
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, detach, legalIntents, pickIntent, botLcg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'intent');
const ENCOUNTERS = Number(arg('--encounters', '4'));
const TURNS = Number(arg('--turns', '5'));
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [projection, model] = await Promise.all([load('src/ui/models/CombatIntentProjectionModel.js'), load('src/ui/models/CombatIntentModel.js')]);
const classes = arg('--classes', classIds(w).join(',')).split(',');
const all = encounters(w);
const json = (v) => `${JSON.stringify(canon(v))}\n`;
const r9 = (x) => (typeof x === 'number' ? Math.round(x * 1e9) / 1e9 : x);

const clone = (v) => (v === undefined ? undefined : structuredClone(v));

// The setups: laid on a detached copy of the fight (the C# test lays the same fields on its own).
const SCENARIOS = [
  { name: 'unread', setup: {} },
  { name: 'read', setup: { read: true } },
  { name: 'block', setup: { read: true, block: 7 } },
  { name: 'defend', setup: { read: true, block: 5, stance: { maneuver: 'defend', camp: 'physical', range: 'melee' } } },
  { name: 'evade', setup: { read: true, stance: { maneuver: 'evade', camp: 'physical', range: 'melee' }, statuses: { evade: 2 } } },
  { name: 'counter', setup: { read: true, stance: { maneuver: 'counter', camp: 'physical', range: 'melee' } } },
  { name: 'attack', setup: { read: true, stance: { maneuver: 'attack', camp: 'physical', range: 'melee' } } },
  { name: 'barrier', setup: { read: true, barrier: 6, block: 3 } },
  { name: 'vulnerable', setup: { read: true, statuses: { vulnerable: 2 } } },
  { name: 'poise', setup: { read: true, poise: 0 } },
];

function apply(state, setup) {
  const p = state.player;
  if (setup.read) for (const e of state.enemies) e.actorIntentRevealed = true;
  if (setup.block !== undefined) p.block = setup.block;
  if (setup.barrier !== undefined) p.barrier = setup.barrier;
  if (setup.stance) p.combatStance = clone(setup.stance);
  for (const [id, stacks] of Object.entries(setup.statuses || {})) p.statuses[id] = { stacks };
  if (setup.poise !== undefined && p.persistentPoise) p.persistentPoise.value = setup.poise;
  return state;
}

const PICK = ['kind', 'moveId', 'stance', 'hidden', 'revealed', 'damage', 'hits', 'hitDamages', 'totalDamage', 'hpLoss', 'hitChance', 'uncertain', 'hpLossBounds', 'block',
  'counterDamage', 'counterPoiseDamage', 'delayed', 'pending', 'dodgeCharges', 'label', 'knowledgeRead'];

function slim(intent) {
  const out = {};
  for (const key of PICK) if (intent[key] !== undefined) out[key] = key === 'hitChance' ? r9(intent[key]) : intent[key];
  if (intent.profile) out.profile = { camp: intent.profile.camp ?? null, maneuver: intent.profile.maneuver ?? null, range: intent.profile.range ?? null };
  const p = intent.projectedDamage;
  if (p) out.projectedDamage = { ...p, hitChance: r9(p.hitChance), expected: r9(p.expected), baseline: p.baseline && { ...p.baseline, expected: r9(p.baseline.expected) } };
  return out;
}

function badgeOf(intent) {
  const b = model.combatIntentPresentation(intent);
  const read = model.intentDamageRead(intent);
  return { visibility: b.visibility, title: b.title, stance: b.stance, damageClass: b.damageClass ?? null, parts: b.parts, tone: b.projectedDamage?.tone ?? null,
    read: read ? { label: read.label, summary: read.summary } : null };
}

function snapshot(combat) {
  const out = {};
  for (const s of SCENARIOS) {
    const state = apply(detach(w, combat), s.setup);
    const enemies = {};
    for (const e of state.enemies) {
      if (e.alive === false || !(e.hp > 0)) continue;
      try {
        const intent = projection.projectCombatIntent(state, e.id);
        enemies[e.id] = { intent: slim(intent), badge: badgeOf(intent) };
      } catch (error) { enemies[e.id] = { error: error.message }; }
    }
    out[s.name] = { setup: s.setup, enemies };
  }
  return out;
}

function fight(seed, classId, encounter) {
  const run = createRun(w, seed, classId);
  const { combat } = createFight(w, run, encounter);
  const pick = botLcg(seed ^ 0x9e3779b9);
  const intents = [];
  const turns = [{ intents: [], scenarios: snapshot(combat) }];
  for (let turn = 0; turn < TURNS && !combat.result; turn++) {
    const done = [];
    // one turn: the bot ends it (answering any offer) until the turn number moves on
    const before = combat.turn;
    for (let i = 0; i < 12 && !combat.result && combat.turn === before; i++) {
      const legal = legalIntents(w, combat, { only: 'endTurn' });
      const intent = pickIntent('endTurnOnly', legal, pick);
      if (!intent) break;
      w.combat.dispatch(combat, structuredClone(intent));
      done.push(intent);
      intents.push(intent);
    }
    if (combat.result) break;
    turns.push({ intents: done, scenarios: snapshot(combat) });
  }
  return { seed, classId, encounterId: encounter.id, turns };
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, files: [] };
classes.forEach((classId, ci) => {
  const fights = [];
  for (let e = 0; e < ENCOUNTERS; e++) {
    const ei = (e * 7 + ci * 5) % all.length;
    fights.push(fight(w.rngMod.sweepSeed(8100 + ci * 1000 + ei), classId, all[ei]));
  }
  const text = json({ classId, fights });
  writeFileSync(join(OUT, `fights-${classId}.json`), text);
  index.files.push({ file: `fights-${classId}.json`, bytes: Buffer.byteLength(text), fights: fights.length });
});
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-intent: ${index.files.length} files, ${(index.files.reduce((n, f) => n + f.bytes, 0) / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/intent`);
