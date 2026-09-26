#!/usr/bin/env node
// Combat parity oracle (US-5.x/6.x, PF-04/05/10). Drives the SHIPPED combat engine exactly as main.js does,
// records the initial combat snapshot and a seeded bot's command sequence with a projection after every step.
// The C# engine restores the same snapshot, replays the same commands and must match every projection.
//
//   node Tools/oracle-combat.mjs [--source D:/repos/AshenSpire] [--combats 48] [--steps 80]
//
// Writes Unity/Assets/Tests/Oracle/combat/*.json (committed; CI has no access to the old repo).
import { writeFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const SOURCE = arg('--source', process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const COMBATS = Number(arg('--combats', '48'));
const STEPS = Number(arg('--steps', '80'));
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'combat');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng, sweepSeed } = await load('src/engine/rng.js');
const { createRunState } = await load('src/model/state.js');
const { createCombat, dispatch } = await load('src/engine/combat.js');
const { serializeCombatSnapshot, restoreCombatSnapshot } = await load('src/engine/combatSnapshot.js');
const { resolveHandRules } = await load('src/model/handRules.js');
const { resolveSwapCostRule, runMods } = await load('src/model/loadout.js');

const bundle = configuredContentBundle(contentBundle, {});
const registries = createRegistries(bundle);
const classes = bundle.classes.map(c => c.id);
const encounters = bundle.encounters.filter(e => e.pool === 'normal' || e.pool === 'elite' || e.pool === 'boss');

// Deterministic bot choices use their own LCG — never the engine RNG, so the bot cannot perturb the streams.
function botRng(seed) { let s = seed >>> 0 || 1; return (n) => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s % n; }; }

function projection(combat, rng) {
  const ent = (e) => ({
    id: e.id ?? e.instanceId, hp: e.hp, maxHp: e.maxHp, block: e.block, alive: e.alive !== false,
    statuses: Object.fromEntries(Object.entries(e.statuses || {}).map(([k, v]) => [k, typeof v === 'object' ? (v.stacks ?? v.value ?? v) : v]).sort()),
    ...(e.intent ? { intent: { moveId: e.intent.moveId ?? null, kind: e.intent.kind ?? e.intent.type ?? null, amount: e.intent.amount ?? e.intent.damage ?? null } } : {}),
  });
  const p = combat.player;
  return {
    turn: combat.turn, phase: combat.phase, result: combat.result,
    player: { ...ent(p), energy: p.energy, mana: p.mana, stamina: p.stamina },
    enemies: combat.enemies.map(ent),
    hand: combat.piles.hand.map(c => c.instanceId),
    draw: combat.piles.draw.length, discard: combat.piles.discard.length, exhaust: combat.piles.exhaust.length,
    rng: rng.getCounters(),
  };
}

function cloneCombat(combat, seed) {
  const rng = createRng(seed, combat.rng.getCounters());
  return restoreCombatSnapshot({ registries, rng, snapshot: serializeCombatSnapshot(combat) });
}

function legalCommands(combat, seed) {
  const out = [];
  const targets = combat.enemies.filter(e => e.alive !== false).map(e => e.instanceId ?? e.id);
  for (const card of combat.piles.hand) {
    for (const targetId of [undefined, ...targets]) {
      const cmd = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
      try { dispatch(cloneCombat(combat, seed), cmd); out.push(cmd); break; } catch { /* illegal: try the next target */ }
    }
  }
  out.push({ type: 'endTurn' });
  return out;
}

if (existsSync(OUT)) rmSync(OUT, { recursive: true });
mkdirSync(OUT, { recursive: true });
const index = [];
let totalSteps = 0, victories = 0, defeats = 0;
for (let i = 0; i < COMBATS; i++) {
  const seed = sweepSeed(1000 + i);
  const classId = classes[i % classes.length];
  const encounter = encounters[i % encounters.length];
  const rng = createRng(seed);
  const run = createRunState({ seed, classId, registries });
  const handRules = resolveHandRules({}, bundle.attributes);
  const combat = createCombat({
    ratingsRules: registries.balance.combatRatings || null, handRules, registries, rng,
    player: {
      classId: run.class, attributes: run.attributes, derivedStatRuleSnapshot: run.derivedStatRuleSnapshot, skills: run.skills,
      coreTags: run.coreTags, maxHp: run.maxHp, hp: run.hp, maxMana: run.maxMana, mana: run.mana, maxStamina: run.maxStamina,
      stamina: run.stamina, energyMax: run.energyMax, drawPerTurn: run.drawPerTurn, damageBySchoolAdd: run.damageBySchoolAdd,
      equipmentProfileRuleSnapshot: run.equipmentProfileRuleSnapshot, equipmentAttackSlotCount: run.equipmentAttackSlotCount,
      removedAttackSlotIds: run.removedAttackSlotIds, equipmentPoolDeficits: run.equipmentPoolDeficits,
      itemUpgradeLevels: run.itemUpgradeLevels, itemMounts: run.itemMounts, deck: run.deck, relicIds: run.relics,
      flasks: run.flasks, flaskCharges: run.flaskCharges, loadout: run.loadout,
    },
    enemyIds: encounter.enemies, hpMult: 1, enemyStatuses: [],
    swapCostRule: resolveSwapCostRule(registries, null),
    playerStatuses: [...runMods(registries, run.loadout, run.class).startStatuses],
  });
  const initial = serializeCombatSnapshot(combat);
  const initialRng = rng.getCounters();
  const pick = botRng(seed);
  const steps = [];
  const start = projection(combat, rng);
  for (let s = 0; s < STEPS && !combat.result; s++) {
    const legal = legalCommands(combat, seed);
    // Prefer playing cards (80%) so fights progress; endTurn is always legal.
    const cards = legal.filter(c => c.type === 'playCard');
    const cmd = cards.length && pick(10) < 8 ? cards[pick(cards.length)] : { type: 'endTurn' };
    let error = null;
    try { dispatch(combat, cmd); } catch (e) { error = e.message; }
    steps.push({ command: cmd, ...(error ? { error } : {}), after: projection(combat, rng) });
  }
  totalSteps += steps.length;
  if (combat.result === 'victory') victories++;
  if (combat.result === 'defeat') defeats++;
  const name = `combat-${String(i).padStart(3, '0')}.json`;
  writeFileSync(join(OUT, name), `${JSON.stringify({ seed, classId, encounterId: encounter.id, rngCounters: initialRng, snapshot: initial, start, steps, result: combat.result }, null, 1)}\n`);
  index.push({ file: name, seed, classId, encounterId: encounter.id, steps: steps.length, result: combat.result });
}
// The registry tables these combats ran against, in the SHIPPED insertion order (D-040), so the C# parity test
// exercises the engine alone; a separate test holds the content-built registries to the same tables.
const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x)));
const REGISTRY_TABLES = ['attributes', 'cards', 'relics', 'statuses', 'stances', 'keywords', 'enemies', 'flasks', 'classes', 'propertyRules'];
const dump = Object.fromEntries(REGISTRY_TABLES.map((t) => [t, plain(registries[t].all())]));
for (const t of ['classTree', 'equipment', 'balance']) dump[t] = plain(registries[t]);
writeFileSync(join(OUT, 'registries.json'), `${JSON.stringify(dump)}
`);
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify({ source: 'engine/combat.js createCombat + dispatch (shipped preset)', combats: index }, null, 1)}\n`);
console.log(`oracle-combat: ${COMBATS} combats, ${totalSteps} steps, ${victories} victories, ${defeats} defeats → Unity/Assets/Tests/Oracle/combat`);
