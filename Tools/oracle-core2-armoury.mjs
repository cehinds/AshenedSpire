#!/usr/bin/env node
// Core 2 Armoury oracle (P-3a, docs/BUILD-LOG.md). Drives the SHIPPED web for the configured `default` profile:
//   src/model/equipmentPresentation.js   equipmentSurfaceReceipt(..., { candidate, meta }): the candidate comparison the Armoury shows beside an item
//                                        (the card packages, the attack package counts, the requirement, the added effects, the resource changes and
//                                        swap prices, the Poise threshold and the equip load, before and after)
//   src/model/equipmentCard.js, src/ui/models/PossessionVariantModel.js   what the poker-style equipment card says (the facts, the bonuses, the tags,
//                                        the requirement line, the card package) for each armament and each class's armour
//   src/model/inventoryPresentation.js   the class row of the Inventory (name, glyph, tint, description, tags, the Core slot label)
//
//   node Tools/oracle-core2-armoury.mjs [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/armoury/, sorted keys; two runs are byte-identical):
//   candidates.json   per class and variant (`start`: the run as created; `rich`: every armament and the class's armour carried, two tiers forged,
//                     other pieces in the other hand), every piece that fits each position of the hands and the armour (and an unequip), under the
//                     default swap rule; the other three swap rules record only their resource changes
//   cards.json        the card model and its possession variant for every armament and every armour of every class, at tiers 0..2
//   classes.json      the Inventory's class row of each class
//   index.json        the sha, the profile hash and the tallies
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon, classIds, createRun } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'armoury');
const w = await loadWeb();
const reg = w.registries;
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [presentation, cardModel, possession, loadout, upgrades, inventory] = await Promise.all([
  load('src/model/equipmentPresentation.js'), load('src/model/equipmentCard.js'), load('src/ui/models/PossessionVariantModel.js'),
  load('src/model/loadout.js'), load('src/model/itemUpgrades.js'), load('src/model/inventoryPresentation.js'),
]);
const eq = reg.equipment;
const SEED = 779;
const RULES = [null, 'flat', 'gear', 'category'];
const classes = classIds(w);

const pieceRef = (slot, classId, piece) => (slot.kinds.includes('armor') ? `armor/${classId}/${piece.id}` : `armament/${piece.id}`);
const authoredFor = (slot, classId) => (slot.kinds.includes('armor')
  ? eq.armour.filter((p) => p.classId === classId && loadout.fitsSlot(slot, p))
  : eq.armaments.filter((p) => loadout.fitsSlot(slot, p)));

/** The run a variant stands on: `start` as created, `rich` with the whole catalog carried and two pieces forged. */
function runFor(classId, variant) {
  const run = createRun(w, SEED, classId);
  const edit = {};
  if (variant === 'rich') {
    const sets = run.loadout.sets;
    sets.rightHand = ['straightSword', 'dagger', null];
    sets.leftHand = ['buckler', null, null];
    run.loadout.storage = [...eq.armaments.map((p) => p.id).filter((id) => !['straightSword', 'dagger', 'buckler'].includes(id)),
      ...eq.armour.filter((p) => p.classId === classId && p.id !== run.loadout.sets.armor[0]).map((p) => p.id)];
    run.itemUpgradeLevels = { 'armament/greatsword': 1, [`armor/${classId}/${run.loadout.sets.armor[0]}`]: 1 };
    edit.loadout = { sets: { rightHand: sets.rightHand, leftHand: sets.leftHand }, storage: run.loadout.storage };
    edit.itemUpgradeLevels = run.itemUpgradeLevels;
  }
  return { run, edit };
}

const slots = eq.slots.filter((s) => ['rightHand', 'leftHand', 'armor'].includes(s.id));
const tally = { candidates: 0, errors: 0, withPackages: 0, withResources: 0, withSwapPrice: 0, withEffects: 0, cards: 0, tiers: 0, swapPrices: {} };
const errors = {};

function candidatesFile() {
  const cases = [];
  for (const classId of classes) {
    for (const variant of ['start', 'rich']) {
      const { run, edit } = runFor(classId, variant);
      const entry = { classId, variant, seed: SEED, edit, results: [] };
      for (const slot of slots) {
        const count = run.loadout.sets[slot.id].length;
        const pieces = [...authoredFor(slot, classId).map((p) => p.id), null];
        for (let setIndex = 0; setIndex < count; setIndex++) {
          for (const pieceId of pieces) {
            const record = { slotId: slot.id, setIndex, pieceId };
            RULES.forEach((rule) => {
              const meta = rule ? { settings: { swapCostRule: rule } } : null;
              let got;
              try { got = presentation.equipmentSurfaceReceipt(reg, run, { candidate: { slotId: slot.id, setIndex, pieceId }, meta }).candidate; }
              catch (e) { got = { error: e.message }; }
              if (got.error) {
                if (rule === null) { tally.errors++; errors[got.error] = (errors[got.error] || 0) + 1; }
                (record.rules ||= {})[rule || 'default'] = { error: got.error };
                return;
              }
              if (rule === null) {
                tally.candidates++;
                if (got.attackPackageChanges.length) tally.withPackages++;
                if (got.resourceChanges.some((r) => !r.id.startsWith('swapCost'))) tally.withResources++;
                if (got.resourceChanges.some((r) => r.id.startsWith('swapCost'))) tally.withSwapPrice++;
                if (got.addedEffects.length) tally.withEffects++;
                record.candidate = {
                  attackPackageChanges: got.attackPackageChanges, resourceChanges: got.resourceChanges, addedEffects: got.addedEffects,
                  roles: got.roles, requirement: got.requirement, poise: got.poise, load: got.load, pieceName: got.pieceName,
                };
              }
              if (rule && got.resourceChanges.length) tally.swapPrices[rule] = (tally.swapPrices[rule] || 0) + 1;
              (record.rules ||= {})[rule || 'default'] = { resourceChanges: got.resourceChanges };
            });
            entry.results.push(record);
          }
        }
      }
      cases.push(entry);
    }
  }
  return { cases };
}

function cardsFile() {
  const out = [];
  const project = (slotKind, classId, piece, level) => {
    const base = level ? upgrades.resolveUpgradedEquipment(reg, slotKind === 'armor' ? `armor/${classId}/${piece.id}` : `armament/${piece.id}`, level) : piece;
    const model = cardModel.equipmentCardModel(reg, base);
    const variant = possession.possessionVariant(reg, base, { kind: 'equipment', bonuses: model.bonuses,
      attributeRequirement: model.requirement.startsWith('Requires ') ? model.requirement : null });
    return { id: piece.id, classId: slotKind === 'armor' ? classId : null, level, model,
      variant: { requirement: variant.requirement, heading: variant.heading, entries: variant.entries, empty: variant.empty, families: variant.families } };
  };
  for (const piece of eq.armaments) {
    for (const level of [0, 1, 2]) {
      try { out.push(project('armament', null, piece, level)); tally.cards++; if (level) tally.tiers++; } catch (e) { if (level === 0) throw e; }
    }
  }
  const seen = new Set();
  for (const classId of classes) {
    for (const piece of eq.armour.filter((p) => p.classId === classId)) {
      for (const level of [0, 1, 2]) {
        try { out.push(project('armor', classId, piece, level)); tally.cards++; if (level) tally.tiers++; } catch (e) { if (level === 0) throw e; }
      }
    }
  }
  // The poker card's solved rows (equipmentCardTokens): with tag badges, and with none (the tags row takes no floor).
  const rows = (collapse) => { const t = cardModel.equipmentCardTokens(undefined, { collapse, omit: [] }); return { regions: t.regions, heights: t.heights, budget: t.budget, frameWidthPx: t.frameWidthPx, frameHeightPx: t.frameHeightPx, paddingPx: t.paddingPx, gapPx: t.gapPx, bonusMaxLines: t.bonusMaxLines }; };
  return { cards: out, layout: { withTags: rows([]), noTags: rows(['tags']) } };
}

function classesFile() {
  return { rows: classes.map((classId) => {
    const run = createRun(w, SEED, classId);
    const row = inventory.inventoryRows(reg, run, {}).find((r) => r.classCard);
    const def = reg.classes.get(classId);
    const rowsOf = (variant) => {
      const { run: shown } = runFor(classId, variant);
      return inventory.inventoryRows(reg, shown, {}).map((r) => ({ key: r.key, id: r.id, name: r.name, category: r.category, count: r.count, equippedLabels: r.equippedLabels, read: !!r.read }));
    };
    return { classId, key: row.key, name: row.name, category: row.category, count: row.count, equippedLabels: row.equippedLabels,
      glyph: row.item.icon, blurb: row.item.blurb, rarity: row.item.rarity ?? null, tags: row.item.tags ?? null, tint: def.cardTint ?? null,
      inventory: { start: rowsOf('start'), rich: rowsOf('rich') } };
  }) };
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
let bytes = 0;
const write = (name, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, name), text); bytes += Buffer.byteLength(text); };
write('candidates.json', candidatesFile());
write('cards.json', cardsFile());
write('classes.json', classesFile());
write('index.json', { source: 'model/equipmentPresentation.js, model/equipmentCard.js, ui/models/PossessionVariantModel.js, model/inventoryPresentation.js (configured profile default)',
  webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, seed: SEED, rules: RULES.map((r) => r || 'default'), errors, tally });
console.log(`oracle-core2-armoury: ${tally.candidates} candidates (${tally.errors} refused by the web: ${JSON.stringify(errors)}), ${tally.withPackages} with package changes, ${tally.withResources} with resource changes, ${JSON.stringify(tally.swapPrices)} swap prices by rule, ${tally.withEffects} with added effects, ${tally.cards} cards (${tally.tiers} forged), ${(bytes / 1024 / 1024).toFixed(1)} MB -> Unity/Assets/Tests/Oracle/core2/armoury`);
