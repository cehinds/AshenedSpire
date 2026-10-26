#!/usr/bin/env node
// Core 2 whole-fight session oracle (WP-C1a, docs/design/11-CORE2-PORT-PLAN.md, D-300). Plays WHOLE fights on the shipped web (createRunCombat + dispatch) with a
// bot that chooses among the player's legal intents -- the list a screen offers (legalIntents with full: true, the throw-only routes of the UI included) -- and
// records, at every decision, that LIST (compact keys), then the step: the intent, the event types, the combat as a delta and every RNG counter. When the fight
// ends it applies runCombatEnd to the run and records what that wrote back. Every class meets every encounter of the profile, on a few seeds. The C# test drives
// Core2CombatSession through the same fights and holds LegalIntents, Execute, the previews' state, the saves and the run write-back to it.
//
//   node Tools/oracle-core2-session.mjs [--seeds 2] [--steps 400] [--classes reaver] [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/session/, sorted keys, byte-identical on rerun): fights-<class>.json and index.json.
// A fight is { seed, classId, encounterId, keys: [unique intent keys], start: { events, state }, steps: [{ legal: [indexes into keys], intent, events, delta }], end: { result, runDelta, rng } }.
import { writeFileSync, readFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadWeb, ORACLE_DIR, arg, canon, classIds, encounters, createRun, createFight, botLcg, legalIntents, pickIntent, projectFull, delta, typesSince, intentKey } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'session');
const SEEDS = Number(arg('--seeds', '2'));
const STEPS = Number(arg('--steps', '400'));
const HERE = dirname(fileURLToPath(import.meta.url));

function fight(w, seed, classId, encounter) {
  const run = createRun(w, seed, classId);
  const { combat } = createFight(w, run, encounter);
  const pick = botLcg(seed ^ 0x2c1b3c6d);
  const keys = [], index = new Map();
  const id = (intent) => { const k = intentKey(intent); if (!index.has(k)) { index.set(k, keys.length); keys.push(k); } return index.get(k); };
  let last = projectFull(combat);
  const start = { events: typesSince(combat, 0), state: last };
  const steps = [];
  for (let i = 0; i < STEPS && !combat.result; i++) {
    const legal = legalIntents(w, combat, { full: true });
    const intent = pickIntent('cards', legal, pick);
    if (!intent) break;
    const mark = combat.eventLog.length;
    let error = null;
    try { w.combat.dispatch(combat, structuredClone(intent)); } catch (e) { error = e.message; }
    if (error) throw new Error(`oracle-core2-session: a legal intent was refused in ${encounter.id} (${seed}): ${intentKey(intent)}: ${error}`);
    const state = projectFull(combat);
    steps.push({ legal: legal.map(id), intent, events: typesSince(combat, mark), delta: delta(last, state) ?? {} });
    last = state;
  }
  const end = { result: combat.result ?? null, rng: combat.rng.getCounters() };
  if (combat.result) {
    const before = canon(structuredClone(run));
    w.runCombat.runCombatEnd(run, combat);
    end.runDelta = delta(before, canon(run)) ?? {};
  }
  // The decision after the last step is recorded too (a finished fight offers nothing), so the C# side sees the list empty.
  return { seed, classId, encounterId: encounter.id, keys, start, steps, end };
}

async function runClass(classId, ci) {
  const w = await loadWeb();
  const fights = [];
  encounters(w).forEach((encounter, ei) => {
    for (let n = 0; n < SEEDS; n++) fights.push(fight(w, w.rngMod.sweepSeed(70000 + ci * 1000 + n * 100 + ei), classId, encounter));
  });
  mkdirSync(OUT, { recursive: true });
  const text = `${JSON.stringify(canon({ classId, fights }))}\n`;
  writeFileSync(join(OUT, `fights-${classId}.json`), text);
  writeFileSync(join(OUT, `tally-${classId}.tmp`), JSON.stringify({ classId, file: `fights-${classId}.json`, fights: fights.length, steps: fights.reduce((s, f) => s + f.steps.length, 0), ended: fights.filter((f) => f.end.result).length, bytes: Buffer.byteLength(text) }));
}

const classArg = arg('--classes', null);
if (classArg) {
  const all = classIds(await loadWeb());
  for (const classId of classArg.split(',')) await runClass(classId, all.indexOf(classId));
} else {
  const w = await loadWeb();
  const all = classIds(w);
  mkdirSync(OUT, { recursive: true });
  for (const f of readdirSync(OUT)) if ((f.startsWith('fights-') && f.endsWith('.json')) || f === 'index.json' || f.endsWith('.tmp')) rmSync(join(OUT, f));
  const passthrough = process.argv.slice(2);
  await Promise.all(all.map((classId) => new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [join(HERE, 'oracle-core2-session.mjs'), ...passthrough, '--classes', classId], { stdio: ['ignore', 'inherit', 'inherit'] });
    child.on('exit', (code) => (code === 0 ? resolve() : reject(new Error(`${classId} exited ${code}`))));
  })));
  const files = [];
  const total = { fights: 0, steps: 0, ended: 0 };
  for (const classId of all) {
    const part = JSON.parse(readFileSync(join(OUT, `tally-${classId}.tmp`), 'utf8'));
    rmSync(join(OUT, `tally-${classId}.tmp`));
    files.push({ file: part.file, classId, fights: part.fights, steps: part.steps, ended: part.ended, bytes: part.bytes });
    for (const key of Object.keys(total)) total[key] += part[key];
  }
  writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon({ webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, seeds: SEEDS, steps: STEPS, files, total }), null, 1)}\n`);
  console.log(`oracle-core2-session: ${total.fights} fights (${total.ended} played to the end) / ${total.steps} steps, ${(files.reduce((s, f) => s + f.bytes, 0) / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/session`);
}
