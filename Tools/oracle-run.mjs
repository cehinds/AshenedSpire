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

// ---------------------------------------------------------------------------------------------------------------
// us-5.11: the creation options and doors the first pass left out, appended after the cases above (whose files and
// encounter draws stay byte-identical).
//   - custom attribute allocations (normalizeRunAttributes): a point moved in every creation mode, a retired id
//     carried to its heir, and the refusals (wrong total, out of range, unknown, mixed retired/heir, non-integer,
//     missing cell, an alternate kit the allocation cannot hold);
//   - derived-stat option layers (the Settings tier dial is an explicitOverride defaults layer; mode and run layers;
//     equipment-profile patches) and their refusals;
//   - a saved derived-stat rule snapshot to be born under (another class's, a ruleset-3 save's, a ruleset-2 save's)
//     and its refusals;
//   - hand-rule Settings (gameConfig.handRules.*) on the fights' createCombat args, with the unreadable values the
//     resolver ignores and a group whose minimum exceeds its maximum;
//   - the load door's initializeRunDerivedStats(run, registries, { preserveDeficits: true }) on older-shape runs
//     (restore-*.json): a current save validated and trusted, wounds carried, schema v3/v4 pool fields inferred, a
//     save without a snapshot and one under an older ruleset re-derived, the two shipped test fixtures
//     (tests/fixtures/run-save-*.json), and the contradictions it refuses.
const { initializeRunDerivedStats } = await load('src/model/state.js');
const { equipPiece, ownership, stampDeck, addToStorage } = await load('src/model/loadout.js');
const { readFileSync } = await import('node:fs');
const fixture = (name) => JSON.parse(readFileSync(join(SOURCE, 'tests', 'fixtures', name), 'utf8'));
const FIXTURE_HP = 'run-save-hp-5597166.json';
const FIXTURE_CON = 'run-save-constitution-acb8ffe.json';
const HR = 'gameConfig.handRules.';
let extra = 0;

function recordCreation(classId, label, seed, options, settings = null) {
  let run;
  try {
    run = createRunState({ seed, classId, registries, ...structuredClone(options) });
  } catch (e) {
    refused.push({ classId, seed, variant: label, options, error: e.message });
    return;
  }
  const rng = createRng(seed);
  const before = JSON.stringify(run);
  const runPlain = plain(run);
  const fightsOut = [];
  // One fight per creation case (its start holds the pools, deck and ratings); two per hand-rule case.
  for (let j = 0; j < (settings ? 2 : 1); j++) {
    const encounter = encounters[(extra * 2 + j + 7) % encounters.length];
    const args = combatArgs(run, encounter.id, settings || {});
    const liveRng = createRng(seed);
    const combat = createCombat({ ...combatArgs(run, encounter.id, settings || {}), registries, rng: liveRng });
    const snapshot = plain(serializeCombatSnapshot(combat));
    const replayRng = createRng(seed);
    const replay = plain(serializeCombatSnapshot(createCombat({ ...JSON.parse(JSON.stringify(args)), registries, rng: replayRng })));
    if (JSON.stringify(replay) !== JSON.stringify(snapshot)) throw new Error(`${classId}/${label}: recorded args do not reproduce the combat start`);
    fightsOut.push({ encounterId: encounter.id, ...(settings ? { settings } : {}), args, rngCounters: liveRng.getCounters(), snapshot });
    fights++;
  }
  if (JSON.stringify(run) !== before) throw new Error(`${classId}/${label}: starting a combat mutated the run`);
  const name = `run-${classId}-${label}.json`;
  writeFileSync(join(OUT, name), `${JSON.stringify({ classId, seed, variant: label, options, run: runPlain, rngCounters: rng.getCounters(), fights: fightsOut })}\n`);
  index.push({ file: name, classId, seed, variant: label });
  extra++;
}

const modes = registries.creationModes.all();
const attrIds = registries.attributes.all().slice().sort((a, b) => a.order - b.order).map((a) => a.id);
const retired = Object.entries(registries.attributeRules.retired || {});
const profileIds = registries.equipment.basicCardProfiles.map((p) => p.id);
const otherClass = (classId) => classes[(classes.indexOf(classId) + 1) % classes.length];
const bornSnapshots = {};
for (const classId of classes) bornSnapshots[classId] = createRunState({ seed: 1, classId, registries }).derivedStatRuleSnapshot;

classes.forEach((classId, ci) => {
  const seedAt = (k) => sweepSeed(6000 + ci * 100 + k);
  let k = 0;
  // Custom allocations: in every mode, the class preset with one point moved from its highest attribute to its
  // lowest that can take it (a legal edit), recorded as the player's own allocation.
  for (const mode of modes) {
    const preset = { ...registries.attributeRules.presets[mode.id][classId] };
    const floor = mode.belowBaseline === 'forbid' ? Math.max(mode.minimum, mode.baseline) : mode.minimum;
    const from = attrIds.filter((id) => preset[id] - 1 >= floor).sort((a, b) => preset[b] - preset[a])[0];
    const to = attrIds.filter((id) => id !== from && preset[id] + 1 <= mode.maximum).sort((a, b) => preset[a] - preset[b])[0];
    if (from && to) { preset[from] -= 1; preset[to] += 1; }
    recordCreation(classId, `attrs-${mode.id}`, seedAt(k++), { attributeMode: mode.id, attributes: preset });
  }
  const lean = registries.attributeRules.presets.lean[classId];
  // A retired id carried to its heir (the allocation is written in attribute order), and mixed with its heir.
  for (const [dead, heir] of retired) {
    const renamed = Object.fromEntries(Object.entries(lean).map(([id, v]) => [id === heir ? dead : id, v]));
    recordCreation(classId, 'attrs-retired', seedAt(k++), { attributes: renamed });
    recordCreation(classId, 'refused-attrs-mixed', seedAt(k++), { attributes: { ...lean, [dead]: 1 } });
  }
  recordCreation(classId, 'attrs-reordered', seedAt(k++), { attributes: Object.fromEntries(Object.entries(lean).reverse()) });
  recordCreation(classId, 'refused-attrs-total', seedAt(k++), { attributes: { ...lean, [attrIds[0]]: lean[attrIds[0]] + 1 } });
  recordCreation(classId, 'refused-attrs-range', seedAt(k++), { attributeMode: 'standard', attributes: { ...registries.attributeRules.presets.standard[classId], [attrIds[0]]: 99 } });
  recordCreation(classId, 'refused-attrs-unknown', seedAt(k++), { attributes: { ...lean, luck: 1 } });
  recordCreation(classId, 'refused-attrs-fraction', seedAt(k++), { attributes: { ...lean, [attrIds[1]]: lean[attrIds[1]] + 0.5, [attrIds[2]]: lean[attrIds[2]] - 0.5 } });
  const missing = { ...lean };
  delete missing[attrIds[4]];
  recordCreation(classId, 'refused-attrs-missing', seedAt(k++), { attributes: missing });
  recordCreation(classId, 'refused-attrs-mode', seedAt(k++), { attributeMode: 'epic', attributes: { ...lean } });
  const kit = registries.equipment.startingKits.find((row) => row.classId === classId && row.baseline !== true);
  if (kit) {
    // The alternate kit under the preset (as a custom allocation), and under a legal allocation with the lowest
    // Strength (the greatsword kit then refuses on its requirement; the others hold it).
    const meta = { discoveredArmaments: [kit.rightHand, kit.leftHand].filter(Boolean) };
    recordCreation(classId, 'attrs-kit', seedAt(k++), { startingKitId: kit.id, profileMeta: meta, attributes: { ...lean } });
    const low = { strength: 1, dexterity: 2, constitution: 2, wisdom: 2, intelligence: 1 };
    recordCreation(classId, 'attrs-kit-low-strength', seedAt(k++), { startingKitId: kit.id, profileMeta: meta, attributes: low });
  }
  // Derived-stat option layers.
  recordCreation(classId, 'derived-tier3', seedAt(k++), { derivedStatOptions: { explicitOverride: { defaults: { pointsPerTier: 3 } } } });
  recordCreation(classId, 'derived-tier1-standard', seedAt(k++), { attributeMode: 'standard', derivedStatOptions: { explicitOverride: { defaults: { pointsPerTier: 1 } } } });
  recordCreation(classId, 'derived-mode-rows', seedAt(k++), { derivedStatOptions: { modeModifiers: { rules: { hp: { gainPerTier: 5 } } } } });
  recordCreation(classId, 'derived-run-layers', seedAt(k++), { derivedStatOptions: { runModifiers: [{ defaults: { rounding: 'round' } }, { rules: { mana: { base: 3 }, draw: { perLevel: { every: 2, gain: 1 } } } }] } });
  recordCreation(classId, 'derived-run-single', seedAt(k++), { derivedStatOptions: { runModifiers: { rules: { energy: { base: 4, cap: 5 } } } } });
  recordCreation(classId, 'derived-profiles', seedAt(k++), { derivedStatOptions: { modeModifiers: { equipmentProfiles: { [profileIds[2]]: { baseValue: 7 } } }, explicitOverride: { equipmentProfiles: { [profileIds[9]]: { cap: 9 } }, rules: { stamina: { gainPerTier: 2 } } } } });
  recordCreation(classId, 'refused-derived-row', seedAt(k++), { derivedStatOptions: { explicitOverride: { rules: { luck: { base: 1 } } } } });
  recordCreation(classId, 'refused-derived-tier', seedAt(k++), { derivedStatOptions: { modeModifiers: { defaults: { pointsPerTier: 0 } } } });
  recordCreation(classId, 'refused-derived-field', seedAt(k++), { derivedStatOptions: { runModifiers: [{ rules: { hp: { bonus: 2 } } }] } });
  recordCreation(classId, 'refused-derived-profile', seedAt(k++), { derivedStatOptions: { explicitOverride: { equipmentProfiles: { nowhere: { baseValue: 1 } } } } });
  recordCreation(classId, 'refused-derived-profile-field', seedAt(k++), { derivedStatOptions: { explicitOverride: { equipmentProfiles: { [profileIds[0]]: { damageSchool: 'fire' } } } } });
  // Born under a saved rule snapshot.
  recordCreation(classId, 'snapshot-other-class', seedAt(k++), { derivedStatRuleSnapshot: bornSnapshots[otherClass(classId)] });
  recordCreation(classId, 'snapshot-ruleset3', seedAt(k++), { derivedStatRuleSnapshot: fixture(FIXTURE_HP).derivedStatRuleSnapshot });
  recordCreation(classId, 'snapshot-ruleset2', seedAt(k++), { derivedStatRuleSnapshot: fixture(FIXTURE_CON).derivedStatRuleSnapshot });
  recordCreation(classId, 'refused-snapshot-version', seedAt(k++), { derivedStatRuleSnapshot: { ...bornSnapshots[classId], snapshotVersion: 7 } });
  recordCreation(classId, 'refused-snapshot-disagrees', seedAt(k++), { derivedStatRuleSnapshot: { ...bornSnapshots[classId], rulesetVersion: 4 } });
  recordCreation(classId, 'refused-snapshot-envelope', seedAt(k++), { derivedStatRuleSnapshot: { ...fixture(FIXTURE_CON).derivedStatRuleSnapshot, snapshotVersion: 2 } });
  recordCreation(classId, 'refused-snapshot-envelope1', seedAt(k++), { derivedStatRuleSnapshot: { ...bornSnapshots[classId], snapshotVersion: 1 } });
  recordCreation(classId, 'refused-snapshot-modifiers', seedAt(k++), { derivedStatRuleSnapshot: { ...bornSnapshots[classId], relicModifiers: { damageBySchoolAdd: {}, sources: [] } } });
  // Hand-rule Settings on the fights.
  const handSettings = [
    ['hand-fixed', { [`${HR}drawMode`]: 'fixed', [`${HR}turn.base`]: 4, [`${HR}turn.statEnabled`]: true, [`${HR}turn.stat`]: 'dexterity', [`${HR}turn.baseline`]: 1, [`${HR}turn.pointsPerCard`]: 2 }],
    ['hand-opening', { [`${HR}starting.base`]: '6', [`${HR}capacity.maximum`]: 7.9, [`${HR}retain`]: false, [`${HR}starting.pointsPerCard`]: 0 }],
    ['hand-discard', { [`${HR}overflow`]: 'discard', [`${HR}promptDiscard`]: true, [`${HR}discardLimit`]: 150, [`${HR}replaceDiscards`]: true, [`${HR}reshuffle`]: false, [`${HR}capacity.base`]: 0 }],
    ['hand-ignored', { [`${HR}drawMode`]: 'sometimes', [`${HR}retain`]: 'yes', [`${HR}turn.base`]: 'many', [`${HR}starting.stat`]: 'luck', [`${HR}capacity.minimum`]: null, 'gameConfig.other': 3 }],
    ['hand-reset', { [`${HR}starting.minimum`]: 9, [`${HR}starting.maximum`]: 2, [`${HR}capacity.base`]: -4 }],
  ];
  for (const [label, settings] of handSettings) recordCreation(classId, label, seedAt(k++), {}, settings);
});

// The load door on older-shape runs.
const restoreIndex = [];
function recordRestore(classId, label, input, source = 'created') {
  const run = structuredClone(input);
  let output = null, error = null;
  try { initializeRunDerivedStats(run, registries, { preserveDeficits: true }); output = plain(run); } catch (e) { error = e.message; }
  const name = `restore-${classId}-${label}.json`;
  writeFileSync(join(OUT, name), `${JSON.stringify({ classId, label, source, input: plain(input), ...(error ? { error } : { output }) })}\n`);
  restoreIndex.push({ file: name, classId, label, source, ...(error ? { error } : {}) });
}
classes.forEach((classId, ci) => {
  const born = (k, options = {}) => createRunState({ seed: sweepSeed(7000 + ci * 100 + k), classId, registries, ...options });
  let run = born(0);
  recordRestore(classId, 'current', run);
  run = born(1); run.hp -= 7; run.mana = Math.max(0, run.mana - 1); run.equipmentPoolDeficits = { hp: 7, mana: run.maxMana - run.mana, stamina: 0 };
  recordRestore(classId, 'wounded', run);
  run = born(2); run.hp -= 5; run.schemaVersion = 4; delete run.equipmentPoolBonuses; delete run.equipmentPoolDeficits;
  recordRestore(classId, 'v4-pools', run);
  run = born(3); run.maxHp -= 5; run.hp -= 8; run.schemaVersion = 3; delete run.maxHpAdjustment;
  recordRestore(classId, 'v3-cursed', run);
  run = born(4); run.hp -= 3; run.mana = 0; run.schemaVersion = 3; delete run.derivedStatRuleSnapshot; delete run.maxHpAdjustment; delete run.damageBySchoolAdd;
  recordRestore(classId, 'legacy-no-snapshot', run);
  run = born(5); run.schemaVersion = 3; delete run.mana; delete run.maxMana; delete run.derivedStatRuleSnapshot; run.level = { xp: 0, level: 6, unspentPoints: 0 };
  recordRestore(classId, 'legacy-level6', run);
  run = born(6, { attributeMode: 'standard' });
  addToStorage(run.loadout, 'towerShield');
  if (equipPiece(registries, run.loadout, 'leftHand', 0, 'towerShield', ownership(registries, { meta: {}, loadout: run.loadout }), { inCombat: false, classId, attributes: run.attributes, itemUpgradeLevels: run.itemUpgradeLevels })) stampDeck(registries, run);
  run.hp -= 4; run.schemaVersion = 4; delete run.equipmentPoolBonuses; delete run.equipmentPoolDeficits;
  recordRestore(classId, 'equipped-v4', run);
  run = born(7); run.hp -= 2; run.derivedStatRuleSnapshot = fixture(FIXTURE_CON).derivedStatRuleSnapshot;
  recordRestore(classId, 'old-ruleset', run);
  run = born(8); delete run.damageBySchoolAdd;
  recordRestore(classId, 'damage-absent', run);
  run = born(9); run.maxMana += 1; recordRestore(classId, 'refused-mana', run);
  run = born(10); run.maxHp += 2; recordRestore(classId, 'refused-maxhp', run);
  run = born(11); run.energyMax = 1.5; recordRestore(classId, 'refused-energy', run);
  run = born(12); run.maxHpAdjustment = 1.5; recordRestore(classId, 'refused-adjustment', run);
  run = born(13); run.maxHpAdjustment = null; recordRestore(classId, 'refused-adjustment-null', run);
  run = born(14); run.equipmentPoolDeficits.hp = -1; recordRestore(classId, 'refused-deficit', run);
  run = born(15); run.equipmentPoolBonuses.maxHp = 0.5; recordRestore(classId, 'refused-bonus', run);
  run = born(16); run.derivedStatRuleSnapshot.snapshotVersion = 7; recordRestore(classId, 'refused-snapshot', run);
  run = born(17); run.damageBySchoolAdd.fire = 3; recordRestore(classId, 'refused-damage', run);
});
for (const name of [FIXTURE_HP, FIXTURE_CON]) {
  const run = fixture(name);
  recordRestore(run.class, `fixture-${name.replace(/^run-save-|\.json$/g, '')}`, run, `tests/fixtures/${name}`);
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
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify({ source: 'model/state.js createRunState + main.js enterCombat args + engine/combat.js createCombat (shipped preset)', runs: index, refused, restores: restoreIndex }, null, 1)}\n`);
console.log(`oracle-run: ${index.length} runs (${refused.length} refused variants), ${fights} combat starts, ${restoreIndex.length} load-door cases (${restoreIndex.filter((r) => r.error).length} refused), self-check ok → Unity/Assets/Tests/Oracle/run`);
