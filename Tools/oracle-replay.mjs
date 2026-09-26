#!/usr/bin/env node
// Replays the committed combat golden logs through the SHIPPED engine from their snapshots (restore, not
// createCombat) and checks every projection. This is the exact path the C# parity test takes, so a pass here
// proves the logs are replayable from the snapshot alone; run it under NODE_V8_COVERAGE to see the port surface.
//
//   node Tools/oracle-replay.mjs [--source D:/repos/AshenSpire]
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const argSource = process.argv.indexOf('--source');
const SOURCE = argSource > 0 ? process.argv[argSource + 1] : (process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const DIR = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'combat');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng } = await load('src/engine/rng.js');
const { dispatch, previewCard, previewIntent } = await load('src/engine/combat.js');
const { restoreCombatSnapshot } = await load('src/engine/combatSnapshot.js');

const registries = createRegistries(configuredContentBundle(contentBundle, {}));
const ent = (e) => ({
  id: e.id ?? e.instanceId, hp: e.hp, maxHp: e.maxHp, block: e.block, alive: e.alive !== false,
  statuses: Object.fromEntries(Object.entries(e.statuses || {}).map(([k, v]) => [k, typeof v === 'object' ? (v.stacks ?? v.value ?? v) : v]).sort()),
  ...(e.intent ? { intent: { moveId: e.intent.moveId ?? null, kind: e.intent.kind ?? e.intent.type ?? null, amount: e.intent.amount ?? e.intent.damage ?? null } } : {}),
});
const projection = (c, rng) => ({
  turn: c.turn, phase: c.phase, result: c.result,
  player: { ...ent(c.player), energy: c.player.energy, mana: c.player.mana, stamina: c.player.stamina },
  enemies: c.enemies.map(ent), hand: c.piles.hand.map(x => x.instanceId),
  draw: c.piles.draw.length, discard: c.piles.discard.length, exhaust: c.piles.exhaust.length, rng: rng.getCounters(),
});

const previews = (c) => ({
  cards: c.piles.hand.map((x) => {
    const p = previewCard(c, x.instanceId);
    return {
      id: x.instanceId, cost: p.cost, costIsX: p.costIsX, needsTarget: p.needsTarget, manaCost: p.manaCost, staminaCost: p.staminaCost,
      values: p.values.map((v) => ({
        op: v.op, target: v.target ?? null, value: v.value ?? null,
        ...(v.hits != null ? { hits: v.hits } : {}), ...(v.perTarget ? { perTarget: v.perTarget } : {}),
        ...(v.status ? { status: v.status } : {}), ...(v.token ? { token: v.token } : {}), ...(v.boostTint ? { boostTint: v.boostTint } : {}),
      })),
      tokens: p.tokens,
    };
  }),
  intents: c.enemies.filter((e) => e.alive).map((e) => {
    const i = previewIntent(c, e.id);
    return { id: e.id, kind: i.kind ?? null, moveId: i.moveId ?? null, damage: i.damage ?? null, hits: i.hits ?? null, totalDamage: i.totalDamage ?? null, block: i.block ?? null, pending: !!i.pending };
  }),
});

let files = 0, steps = 0, failures = 0;
for (const f of readdirSync(DIR).filter(n => /^combat-\d+\.json$/.test(n)).sort()) {
  const log = JSON.parse(readFileSync(join(DIR, f), 'utf8'));
  const rng = createRng(log.seed, log.rngCounters);
  const combat = restoreCombatSnapshot({ registries, rng, snapshot: log.snapshot });
  const check = (label, want) => {
    const got = JSON.stringify(want.previews ? { ...projection(combat, rng), previews: previews(combat) } : projection(combat, rng));
    if (got !== JSON.stringify(want)) { failures++; console.error(`${f} ${label}: mismatch\n  want ${JSON.stringify(want)}\n  got  ${got}`); return false; }
    return true;
  };
  files++;
  if (!check('start', log.start)) continue;
  for (let i = 0; i < log.steps.length; i++) {
    const step = log.steps[i];
    try { dispatch(combat, step.command); } catch (e) { if (!step.error) { failures++; console.error(`${f} step ${i}: unexpected ${e.message}`); } }
    steps++;
    if (!check(`step ${i}`, step.after)) break;
  }
}
console.log(`oracle-replay: ${files} combats, ${steps} steps, ${failures} failures`);
process.exit(failures ? 1 : 0);
