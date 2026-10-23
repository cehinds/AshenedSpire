#!/usr/bin/env node
// Core 2 combat smoke oracle (WP-1): drives the SHIPPED Core 2 combat (createRunCombat + dispatch) with a deterministic
// bot and records, for every step, the intent, the projection of the Core fields and the previews. WP-5+ port against
// these; the full per-feature oracles (--bot reactions, range, ...) arrive with their packages.
//
//   node Tools/oracle-core2-combat.mjs [--bot endTurnOnly|cards|all] [--seeds 3] [--steps 40] [--end-turn-steps 24]
//                                      [--classes reaver,starseer,rogue,herald] [--no-previews] [--source <web checkout>]
//
// Refuses to run unless the web HEAD is core2.webSha. Writes Unity/Assets/Tests/Oracle/core2/combat/<bot>-<class>-<n>.json
// plus index.json. The bot uses its own LCG, never the engine RNG; two runs are byte-identical.
import { writeFileSync, readFileSync, mkdirSync, rmSync, existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { loadWeb, ORACLE_DIR, arg, BOTS, classIds, encounters, runCombatFixture, canon } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'combat');
const botArg = arg('--bot', 'all');
const bots = botArg === 'all' ? BOTS : [botArg];
if (bots.some((b) => !BOTS.includes(b))) { console.error(`oracle-core2-combat: unknown --bot '${botArg}' (${BOTS.join(', ')}, all)`); process.exit(1); }
const SEEDS = Number(arg('--seeds', '3'));
const STEPS = { cards: Number(arg('--steps', '40')), endTurnOnly: Number(arg('--end-turn-steps', '24')) };
const previews = !process.argv.includes('--no-previews');

const w = await loadWeb();
const classes = arg('--classes', classIds(w).join(',')).split(',');
// Fights come from the first-act normal and elite pools, rotated so each class meets different enemies.
const pool = encounters(w).filter((e) => (e.pool === 'normal' || e.pool === 'elite') && !/^a[23]_/.test(e.id));

mkdirSync(OUT, { recursive: true });
// Remove only the previous JSON outputs of the bots being written: the Unity .meta files beside them are committed.
for (const f of readdirSync(OUT)) if (f.endsWith('.json') && f !== 'index.json' && bots.some((b) => f.startsWith(`${b}-`))) rmSync(join(OUT, f));

const index = existsSync(join(OUT, 'index.json')) ? JSON.parse(readFileSync(join(OUT, 'index.json'), 'utf8')).fixtures.filter((x) => !bots.includes(x.bot)) : [];
let total = 0;
for (const bot of bots) {
  classes.forEach((classId, ci) => {
    for (let n = 0; n < SEEDS; n++) {
      const seed = w.rngMod.sweepSeed(3000 + ci * 10 + n);
      const encounterId = pool[(ci * 3 + n * 5) % pool.length].id;
      const fixture = runCombatFixture(w, { seed, classId, encounterId, bot, maxSteps: STEPS[bot], previews });
      const file = `${bot}-${classId}-${n}.json`;
      const text = `${JSON.stringify(fixture)}\n`;
      writeFileSync(join(OUT, file), text);
      total += Buffer.byteLength(text);
      index.push({ file, bot, seed, classId, encounterId, steps: fixture.steps.length, result: fixture.result, bytes: Buffer.byteLength(text) });
    }
  });
}
index.sort((a, b) => (a.file < b.file ? -1 : 1));
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon({ webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, previews, fixtures: index }), null, 1)}\n`);
console.log(`oracle-core2-combat: ${bots.join('+')}: ${index.filter((x) => bots.includes(x.bot)).length} fixtures, ${index.filter((x) => bots.includes(x.bot)).reduce((n, x) => n + x.steps, 0)} steps, ${(total / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/combat`);
