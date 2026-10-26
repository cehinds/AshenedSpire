#!/usr/bin/env node
// Core 2 framework-config parity oracle (D-1420..D-1421). Drives the SHIPPED web for the configured `default` profile on the numbers that live in
// content/framework/mechanics.json and src/model/statRows.js, which an export from the older legacy sha does not carry:
//   src/model/statProjection.js   pieceWeight (mechanics.weight.itemWeightScale: 0.2), playerLoadReceipt   src/model/combatWeight.js   playerWeightClass
//   src/framework/weight.js       computeWeightClass, attributeModifier (dexterityCentreByMode), dodgeRollCheck, the class rows' dodge prices
//   src/model/loadout.js          equipmentKitReceipt / roleAmountReceipt: a card's rating is priced by LEGACY_RATING_FORMULA (model/statRows.js:37)
//                                 because a configured run's registries carry no balance.combatRatings (src/main.js rebuildRegistries)
//
//   node Tools/oracle-core2-parity.mjs [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/parity/, sorted keys; two runs are byte-identical):
//   weight.json   per armament and armour: the weight pieceWeight gives; per class a grid of worn loadouts (every armament in the main hand over every armour of the
//                 class, a second hand for the first armaments) against four attribute sets from capacity 3 to 29: the load, capacity, percent and class row
//   dodge.json    dodgeRollCheck over creation modes, Dexterity 1..10, dice and the three classes: check, difficulty, success, temporary guard, price
//   kit.json      equipmentKitReceipt for every class's kit at attribute sets that cross the point where LEGACY_RATING_FORMULA and the 405 rows part (per role: value and attribute part)
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon, classIds } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'parity');
const w = await loadWeb();
const reg = w.registries;
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [statProjection, weightMod, loadoutMod, mechanicsMod] = await Promise.all([
  load('src/model/statProjection.js'), load('src/framework/weight.js'), load('src/model/loadout.js'), load('src/framework/data/mechanics.js'),
]);
const { mechanics } = mechanicsMod;
const classes = classIds(w);
const armaments = reg.equipment.armaments;
const armour = reg.equipment.armour;
const UNLOCKED = { gameProfileUnlocks: { [w.profile.id]: { completedRunIds: classes.map((_, i) => `r${i}`) } } };
const create = (seed, classId) => w.state.createRunState({ seed, classId, registries: reg, profileMeta: structuredClone(UNLOCKED) });

mkdirSync(OUT, { recursive: true });
let total = 0;
const write = (name, value) => { const text = `${JSON.stringify(canon(value))}\n`; writeFileSync(join(OUT, name), text); total += Buffer.byteLength(text); return Buffer.byteLength(text); };

// ---- weight ------------------------------------------------------------------------------------------------------
const pieces = {
  armaments: Object.fromEntries(armaments.map((a) => [a.id, statProjection.pieceWeight(a)])),
  armour: Object.fromEntries(armour.map((a) => [`${a.classId}:${a.id}`, statProjection.pieceWeight(a)])),
};
const ATTRIBUTE_SETS = [
  { constitution: 1, strength: 1 }, { constitution: 2, strength: 2 }, { constitution: 5, strength: 4 }, { constitution: 10, strength: 9 },
];
const grid = [];
for (const classId of classes) {
  const base = create(5, classId);
  const own = armour.filter((a) => a.classId === classId);
  const withLoadout = (right, left, armourId) => {
    const loadout = structuredClone(base.loadout);
    loadout.sets.rightHand = [right, null, null];
    loadout.sets.leftHand = [left, null, null];
    loadout.sets.armor = [armourId];
    return loadout;
  };
  const rows = [];
  const record = (right, left, armourId) => {
    const loadout = withLoadout(right, left, armourId);
    for (const attrs of ATTRIBUTE_SETS) {
      const attributes = { ...base.attributes, ...attrs };
      const r = w.combat.playerWeightClass({ registries: reg, loadout, attributes, player: { classId }, itemUpgradeLevels: {} });
      rows.push({ right, left, armour: armourId, attributes: attrs, load: r.load, capacity: r.capacity, percent: r.percent, classId: r.weightClass.id });
    }
  };
  for (const a of armaments) for (const set of own) record(a.id, null, set.id);
  for (const a of armaments.slice(0, 4)) for (const b of armaments.slice(0, 4)) if (a.id !== b.id) record(a.id, b.id, own[0].id);
  grid.push({ classId, rows });
}
write('weight.json', { pieces, scale: mechanics.weight.itemWeightScale, classes: mechanics.weight.classes.map((c) => c.id), grid });

// ---- dodge -------------------------------------------------------------------------------------------------------
const dodge = [];
for (const attributeMode of ['lean', 'assign', 'tuned', 'standard', 'pointbuy', null]) {
  for (const dexterity of [1, 2, 3, 4, 5, 7, 10]) {
    for (const cls of mechanics.weight.classes) {
      for (const roll of [1, 8, 10, 11, 14, 20]) {
        const r = weightMod.dodgeRollCheck({ roll, dexterity, attributeMode: attributeMode ?? undefined, weightClass: cls });
        dodge.push({ attributeMode, dexterity, classId: cls.id, roll, check: r.check, difficulty: r.difficulty, success: r.success, temporaryGuard: r.temporaryGuard, cost: r.cost });
      }
    }
  }
}
write('dodge.json', { dodge });

// ---- kit ---------------------------------------------------------------------------------------------------------
const KIT_ATTRIBUTES = [
  null,
  { strength: 3, dexterity: 3, constitution: 3, wisdom: 3, intelligence: 3 },
  { strength: 6, dexterity: 4, constitution: 5, wisdom: 7, intelligence: 8 },
  { strength: 9, dexterity: 9, constitution: 9, wisdom: 9, intelligence: 9 },
  { strength: 12, dexterity: 5, constitution: 7, wisdom: 11, intelligence: 2 },
];
const kit = [];
for (const classId of classes) {
  const run = create(9, classId);
  for (const attrs of KIT_ATTRIBUTES) {
    const attributes = attrs || run.attributes;
    const rows = loadoutMod.equipmentKitReceipt(reg, run.loadout, classId, attributes, run.equipmentProfileRuleSnapshot, { ...run, attributes });
    kit.push({
      classId, attributes,
      rows: rows.map((r) => ({ role: r.role, profileId: r.profile.id, value: r.receipt.value, raw: r.receipt.raw, attributeValue: r.receipt.rating.attributeValue, equipmentBase: r.receipt.rating.equipmentBase })),
    });
  }
}
write('kit.json', { kit });

const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, bytes: total,
  tallies: { weightRows: grid.reduce((n, g) => n + g.rows.length, 0), dodge: dodge.length, kit: kit.length } };
write('index.json', index);
console.log(`oracle-core2-parity: ${JSON.stringify(index.tallies)} ${(total / 1024).toFixed(0)} KB`);
