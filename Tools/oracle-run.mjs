#!/usr/bin/env node
// Run-creation parity oracle (US-2.2, PF-04/05). Drives the SHIPPED createRunState exactly as main.js does, then
// builds the createCombat arguments main.js enterCombat would build for two encounters and starts each fight.
// The C# port (Ashen.Domain.Run) must produce the same run document, the same combat args and, from those args,
// the same combat-start snapshot.
//
//   node Tools/oracle-run.mjs [--source D:/repos/AshenSpire] [--seeds 12]
//
// Writes Unity/Assets/Tests/Oracle/run/*.json (committed; CI has no access to the old repo). Only .json files are
// deleted on regeneration, so the Unity .meta files beside them survive.
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const SOURCE = arg('--source', process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const SEEDS = Number(arg('--seeds', '12'));
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'run');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng, sweepSeed } = await load('src/engine/rng.js');
const { createRunState } = await load('src/model/state.js');
const { createCombat } = await load('src/engine/combat.js');
const { serializeCombatSnapshot } = await load('src/engine/combatSnapshot.js');
const { resolveHandRules } = await load('src/model/handRules.js');
const { resolveSwapCostRule, runMods } = await load('src/model/loadout.js');
const { seatAtTier, seatTierHpMult } = await load('src/model/seats.js');

const bundle = configuredContentBundle(contentBundle, {});
const registries = createRegistries(bundle);
const classes = bundle.classes.map((c) => c.id);
const encounters = bundle.encounters.filter((e) => e.pool === 'normal' || e.pool === 'elite' || e.pool === 'boss');
const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x)));

// main.js enterCombat for a fresh run: no journey, no legacy dungeon, no Custom Climb (combatMods is then the seat
// tier ratio alone), settings from an empty profile meta. Returned without registries/rng, JSON-plain.
function combatArgs(run, encounterId, settings = {}) {
  const enc = registries.encounters.get(encounterId);
  let hpMult = 1;
  const enemyStatuses = [];
  const playerStatuses = [];
  if (Array.isArray(run.seatOrder)) hpMult *= seatTierHpMult(registries, seatAtTier(run.seatOrder, run.actNumber), run.actNumber);
  return plain({
    ratingsRules: registries.balance.combatRatings || null,
    handRules: resolveHandRules(settings, bundle.attributes),
    player: {
      classId: run.class,
      attributes: run.attributes,
      derivedStatRuleSnapshot: run.derivedStatRuleSnapshot,
      skills: run.skills,
      coreTags: run.coreTags,
      maxHp: run.maxHp,
      hp: run.hp,
      maxMana: run.maxMana,
      mana: run.mana,
      maxStamina: run.maxStamina,
      stamina: run.stamina,
      energyMax: run.energyMax,
      drawPerTurn: run.drawPerTurn,
      damageBySchoolAdd: run.damageBySchoolAdd,
      equipmentProfileRuleSnapshot: run.equipmentProfileRuleSnapshot,
      equipmentAttackSlotCount: run.equipmentAttackSlotCount,
      removedAttackSlotIds: run.removedAttackSlotIds,
      equipmentPoolDeficits: run.equipmentPoolDeficits,
      itemUpgradeLevels: run.itemUpgradeLevels,
      itemMounts: run.itemMounts,
      armamentLevels: run.armamentLevels,
      deck: run.deck,
      relicIds: run.relics,
      flasks: run.flasks,
      flaskCharges: run.flaskCharges,
      loadout: run.loadout,
    },
    enemyIds: enc.enemies,
    hpMult,
    enemyStatuses,
    swapCostRule: resolveSwapCostRule(registries, { settings }),
    playerStatuses: [...playerStatuses, ...runMods(registries, run.loadout, run.class).startStatuses],
  });
}

// The creation variants: the default character for every class, plus (per class) two more creation modes, the
// non-baseline starting kit, the alternate armour, the alternate relic and custom hand picks. `profileMeta`
// discovers the kit's pieces, as the Armoury would after a find. The last two are refused by the shipped creation
// (an undiscovered kit, another class's relic) and the port must refuse them too.
function variants(classId) {
  const out = [{ name: 'default', options: {} }];
  const cc = registries.characterCreation.classes[classId];
  const kit = registries.equipment.startingKits.find((k) => k.classId === classId && k.baseline !== true);
  for (const mode of ['standard', 'pointbuy']) out.push({ name: `mode-${mode}`, options: { attributeMode: mode } });
  if (kit) {
    const meta = { discoveredArmaments: [kit.rightHand, kit.leftHand].filter(Boolean) };
    out.push({ name: `kit-${kit.id}`, options: { attributeMode: 'standard', startingKitId: kit.id, profileMeta: meta } });
  }
  for (const armourId of cc.armourIds.slice(1)) out.push({ name: `armour-${armourId}`, options: { startingArmourId: armourId } });
  for (const relicId of cc.relicIds.slice(1)) out.push({ name: `relic-${relicId}`, options: { startingRelicId: relicId } });
  const hands = cc.handIds;
  out.push({ name: 'hands-empty', options: { startingHands: { rightHand: null, leftHand: null } } });
  out.push({ name: 'hands-last', options: { startingHands: { rightHand: hands[hands.length - 1], leftHand: null } } });
  if (kit) out.push({ name: 'refused-kit-undiscovered', options: { startingKitId: kit.id } });
  const foreign = classes.map((id) => registries.classes.get(id).startingRelic).find((id) => !cc.relicIds.includes(id));
  out.push({ name: 'refused-relic-foreign', options: { startingRelicId: foreign } });
  return out;
}

// --coverage: V8 precise coverage (node:inspector) around the recorded calls, printed as the shipped functions
// they executed, per file — the port surface of this oracle.
let coverage = null;
if (process.argv.includes('--coverage')) {
  const { Session } = await import('node:inspector/promises');
  coverage = new Session();
  coverage.connect();
  await coverage.post('Profiler.enable');
  await coverage.post('Profiler.startPreciseCoverage', { callCount: true, detailed: true });
  await coverage.post('Profiler.takePreciseCoverage');
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const index = [];
const refused = [];
let n = 0, fights = 0;
for (const classId of classes) {
  const cases = [];
  for (let i = 0; i < SEEDS; i++) cases.push({ seed: sweepSeed(2000 + i), variant: { name: 'default', options: {} }, label: `seed${String(i).padStart(2, '0')}` });
  variants(classId).slice(1).forEach((variant, i) => cases.push({ seed: sweepSeed(3000 + i), variant, label: variant.name }));
  for (const { seed, variant, label } of cases) {
    const options = variant.options;
    let run;
    try {
      run = createRunState({ seed, classId, registries, ...options });
    } catch (e) {
      // A variant the shipped creation refuses (an unmet requirement, an unowned piece) is recorded as refused:
      // the C# port must refuse it too.
      refused.push({ classId, seed, variant: variant.name, options, error: e.message });
      continue;
    }
    const rng = createRng(seed);
    const before = JSON.stringify(run);
    const runPlain = plain(run);
    const fightsOut = [];
    for (let j = 0; j < 2; j++) {
      const encounter = encounters[(n * 2 + j) % encounters.length];
      const args = combatArgs(run, encounter.id);
      const liveRng = createRng(seed);
      const combat = createCombat({ ...combatArgs(run, encounter.id), registries, rng: liveRng });
      const snapshot = plain(serializeCombatSnapshot(combat));
      // Self-check: the recorded (JSON-plain) args alone start the identical combat.
      const replayRng = createRng(seed);
      const replay = plain(serializeCombatSnapshot(createCombat({ ...JSON.parse(JSON.stringify(args)), registries, rng: replayRng })));
      if (JSON.stringify(replay) !== JSON.stringify(snapshot) || JSON.stringify(replayRng.getCounters()) !== JSON.stringify(liveRng.getCounters())) {
        throw new Error(`${classId}/${label} ${encounter.id}: recorded args do not reproduce the combat start`);
      }
      fightsOut.push({ encounterId: encounter.id, args, rngCounters: liveRng.getCounters(), snapshot });
      fights++;
    }
    if (JSON.stringify(run) !== before) throw new Error(`${classId}/${label}: starting a combat mutated the run`);
    const name = `run-${classId}-${label}.json`;
    writeFileSync(join(OUT, name), `${JSON.stringify({ classId, seed, variant: variant.name, options, run: runPlain, rngCounters: rng.getCounters(), fights: fightsOut })}\n`);
    index.push({ file: name, classId, seed, variant: variant.name });
    n++;
  }
}

if (coverage) {
  const { result } = await coverage.post('Profiler.takePreciseCoverage');
  for (const script of result.filter((s) => s.url.includes('/src/')).sort((a, b) => (a.url < b.url ? -1 : 1))) {
    const names = script.functions.filter((f) => f.functionName && f.ranges[0].count > 0).map((f) => f.functionName);
    if (names.length) console.log(`${script.url.split('/src/')[1]}: ${[...new Set(names)].join(', ')}`);
  }
}

// The registry tables createRunState and createCombat read, in the SHIPPED insertion order (D-040): the combat
// dump's tables plus the creation tables.
const TABLES = ['attributes', 'creationModes', 'cards', 'relics', 'statuses', 'stances', 'keywords', 'enemies', 'encounters', 'flasks', 'classes', 'seats', 'propertyRules'];
const dump = Object.fromEntries(TABLES.map((t) => [t, plain(registries[t].all())]));
for (const t of ['classTree', 'equipment', 'balance', 'attributeRules', 'characterCreation', 'derivedStatRules', 'tagFamilies', 'contentVersion']) dump[t] = plain(registries[t]);
writeFileSync(join(OUT, 'registries.json'), `${JSON.stringify(dump)}\n`);
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify({ source: 'model/state.js createRunState + main.js enterCombat args + engine/combat.js createCombat (shipped preset)', runs: index, refused }, null, 1)}\n`);
console.log(`oracle-run: ${index.length} runs (${refused.length} refused variants), ${fights} combat starts, self-check ok → Unity/Assets/Tests/Oracle/run`);
