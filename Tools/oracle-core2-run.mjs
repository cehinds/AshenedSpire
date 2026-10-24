#!/usr/bin/env node
// Core 2 run-creation oracle (WP-3, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED createRunState for the
// configured `default` profile, exactly as the 405 oracle (oracle-run.mjs) does for the legacy one, and records for each
// case the run document and the arguments main.js enterCombat would hand createRunCombat for two encounters (the call's
// arguments only, not the fight). The C# port (Ashen.Domain.Run.CoreRunRules) must produce the same documents.
//
//   node Tools/oracle-core2-run.mjs [--seeds 12] [--source <web checkout>] [--allow-dirty]
//
// Cases: 4 classes x 12 seeds x the creation modes a configured run offers (`lean`, the default, and `assign`: the two
// attributeRules modes whose baseline is 1, the Core 2 scale), every class unlocked through profileMeta; the class-unlock
// gate (every class at every completed-run count) and the refusals (a locked class, other attributes than the fixed
// ones, an unknown mode). Writes Unity/Assets/Tests/Oracle/core2/run/*.json (sorted keys; two runs are byte-identical).
// `runId` is a random UUID and is left out; `gameProfile` (641 KB, the resolved profile) is recorded as its id and hash.
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'run');
const SEEDS = Number(arg('--seeds', '12'));
const MODES = ['lean', 'assign'];
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const { seatAtTier, seatTierHpMult } = await load('src/model/seats.js');
const { runMods, resolveSwapCostRule } = await load('src/model/loadout.js');
const { gameProfileClassAvailable } = await load('src/model/gameProfileRun.js');
const { playInDeckOrder } = await load('src/model/deckRules.js');

const classes = classIds(w);
const pool = encounters(w).filter((e) => (e.pool === 'normal' || e.pool === 'elite') && !/^a[23]_/.test(e.id));
const unlocked = (completed) => ({ gameProfileUnlocks: { [w.profile.id]: { completedRunIds: Array.from({ length: completed }, (_, i) => `r${i}`) } } });
const profileMeta = unlocked(classes.length);

const create = (seed, classId, options = {}, meta = profileMeta) =>
  w.state.createRunState({ seed, classId, registries: w.registries, profileMeta: structuredClone(meta), ...structuredClone(options) });

// The run as the fixture holds it: no runId (random), the resolved profile as its id and hash.
function runDocument(run) {
  const { gameProfile, runId, ...rest } = run;
  return canon({ ...rest, gameProfile: { id: gameProfile.id, hash: gameProfile.hash } });
}

// main.js enterCombat -> combatMods -> createRunCombat for a fresh act-1 normal or elite fight (no journey, no Custom
// Climb; the only combat modifier is the seat-tier HP ratio, exactly 1 at the baseline). `player` is runCombatPlayer(run)
// verbatim, the run fields a fight consumes. A configured fight's ratings and hand rows are WP-4's.
function combatArgs(run, encounter, settings = {}) {
  const hpMult = Array.isArray(run.seatOrder) ? seatTierHpMult(w.registries, seatAtTier(run.seatOrder, run.actNumber), run.actNumber) : 1;
  return canon({
    encounterId: encounter.id,
    enemyIds: encounter.enemies,
    pool: encounter.pool,
    settings,
    hpMult,
    enemyDamageMult: 1,
    orderedDraw: playInDeckOrder(settings), // runCombat.js:156
    enemyStatuses: [],
    playerStatuses: [],
    startStatuses: runMods(w.registries, run.loadout, run.class).startStatuses,
    swapCostRule: resolveSwapCostRule(w.registries, { settings }),
    combatKey: `${run.seed}/${run.actNumber}/${run.floor}/${run.mapNodeId || encounter.enemies.join(',')}`,
    combatExpansionVersion: run.combatExpansionVersion || 1,
    reactionRulesVersion: run.reactionRulesVersion || null,
    breakMeterVersion: run.advancedConfigSnapshot?.breakMeterVersion,
    rangePolicy: run.rangePolicy || null,
    gameProfile: { id: run.gameProfile.id, hash: run.gameProfile.hash },
    player: w.runCombat.runCombatPlayer(run),
  });
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));

const index = [];
let n = 0;
let bytes = 0;
for (const classId of classes) {
  for (const mode of MODES) {
    for (let i = 0; i < SEEDS; i++) {
      const seed = w.rngMod.sweepSeed(2000 + classes.indexOf(classId) * 100 + i);
      const options = mode === 'lean' && i % 2 === 0 ? {} : { attributeMode: mode };
      const run = create(seed, classId, options);
      const before = JSON.stringify(run);
      const rng = w.createRng(seed, run.streamCounters || {});
      const fights = [0, 1].map((j) => {
        const encounter = pool[(n * 2 + j) % pool.length];
        return combatArgs(run, encounter);
      });
      if (JSON.stringify(run) !== before) throw new Error(`oracle-core2-run: building the combat args mutated ${classId}/${seed}`);
      const label = `${mode}-seed${String(i).padStart(2, '0')}`;
      const file = `run-${classId}-${label}.json`;
      const text = `${JSON.stringify({ classId, seed, mode, options, run: runDocument(run), rngCounters: canon(rng.getCounters()), fights })}\n`;
      writeFileSync(join(OUT, file), text);
      bytes += Buffer.byteLength(text);
      index.push({ file, classId, seed, mode, options });
      n++;
    }
  }
}

// The class-unlock gate (gameProfileClassAvailable over classUnlocks): every class at every completed-run count up to one
// past the sequence, and what createRunState does with a locked class.
const gate = [];
for (let completed = 0; completed <= classes.length + 1; completed++) {
  const meta = unlocked(completed);
  for (const classId of classes) {
    const available = gameProfileClassAvailable({ gameProfile: w.profile }, meta, classId);
    let error = null;
    try { create(w.rngMod.sweepSeed(9000), classId, {}, meta); } catch (e) { error = e.message; }
    gate.push({ completed, classId, available, ...(error ? { error } : {}) });
  }
}
// A profile with no ledger yet (a fresh meta), a meta with another profile's ledger, and an unknown class.
for (const [label, meta] of [['fresh-meta', {}], ['other-profile-ledger', { gameProfileUnlocks: { elsewhere: { completedRunIds: ['a', 'b', 'c'] } } }]]) {
  for (const classId of classes) {
    let error = null;
    try { create(w.rngMod.sweepSeed(9001), classId, {}, meta); } catch (e) { error = e.message; }
    gate.push({ label, classId, available: gameProfileClassAvailable({ gameProfile: w.profile }, meta, classId), ...(error ? { error } : {}) });
  }
}

// Refusals the shipped creation makes for a configured run.
const refused = [];
function refuse(label, classId, options, meta = profileMeta) {
  let error = null;
  try { create(w.rngMod.sweepSeed(9100 + refused.length), classId, options, meta); } catch (e) { error = e.message; }
  if (!error) throw new Error(`oracle-core2-run: '${label}' was meant to be refused`);
  refused.push({ label, classId, seed: w.rngMod.sweepSeed(9100 + refused.length), options, error });
}
const first = classes[0];
const fixed = w.profile.content.classAttributes[first];
refuse('other-attributes', first, { attributes: { ...fixed, [Object.keys(fixed)[0]]: fixed[Object.keys(fixed)[0]] + 1 } });
refuse('unknown-mode', first, { attributeMode: 'epic' });
refuse('locked-class', classes[1], {}, unlocked(0));
refuse('unknown-class', 'wizard', {});

writeFileSync(join(OUT, 'unlocks.json'), `${JSON.stringify(canon({ rules: w.profile.content.classUnlocks, cases: gate }))}\n`);
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon({ source: 'model/state.js createRunState (configured, profile default) + main.js enterCombat -> createRunCombat arguments', webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, modes: MODES, runs: index, refused }), null, 1)}\n`);
console.log(`oracle-core2-run: ${index.length} runs (${classes.length} classes x ${MODES.length} modes x ${SEEDS} seeds), ${gate.length} unlock cases, ${refused.length} refusals, ${(bytes / 1024 / 1024).toFixed(1)} MB -> Unity/Assets/Tests/Oracle/core2/run`);
