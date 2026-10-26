#!/usr/bin/env node
// Core 2 Progression oracle (P-3a, docs/BUILD-LOG.md). Drives the SHIPPED web for the configured `default` profile:
//   src/ui/models/ProgressionInspectionModel.js   progressionStats(registries, run, settings): the stat rows (HP, SP, MP, AR, PR, DR, Hand Size, Draw: value and the
//                                                formula the tooltip prints) and the attribute cards (what each attribute feeds, scales and unlocks)
//   src/model/levelup.js                          levelUpPlan: the points waiting and what the allocation dialog may spend
//   src/ui/models/CharacterSheetModel.js          characterSheetModel's character ladder (the level rows the Character tab lists)
//
//   node Tools/oracle-core2-progression.mjs [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/progression/, sorted keys; two runs are byte-identical):
//   stats.json   per class and variant (`start`: as created; `levelled`: level 7 with points waiting and attributes raised; `geared`: the whole catalog
//                carried, the heaviest armour and weapons worn) the progressionStats output, and the same for each allocation the dialog can preview
//                (one more point on each attribute, then two on the first)
//   index.json   the sha, the profile hash and the tallies
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon, classIds, createRun } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'progression');
const w = await loadWeb();
const reg = w.registries;
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [inspection, sheet, levelup, loadout] = await Promise.all([
  load('src/ui/models/ProgressionInspectionModel.js'), load('src/ui/models/CharacterSheetModel.js'), load('src/model/levelup.js'), load('src/model/loadout.js'),
]);
const SEED = 779;
const classes = classIds(w);
const eq = reg.equipment;

/** The run a variant stands on, and the edit that rebuilds it from a fresh run (the C# test replays `edit` through EditRunForReview). */
function runFor(classId, variant) {
  const run = createRun(w, SEED, classId);
  const edit = {};
  if (variant === 'levelled') {
    const raised = Object.fromEntries(Object.entries(run.attributes).map(([id, v], i) => [id, v + i + 1]));
    run.attributes = raised;
    run.level = { ...run.level, level: 7, xp: 40, unspentPoints: 3 };
    edit.attributes = raised;
    edit.level = { level: 7, xp: 40, unspentPoints: 3 };
  } else if (variant === 'geared') {
    const sets = run.loadout.sets;
    sets.rightHand = ['greatsword', 'dagger', null];
    sets.leftHand = ['buckler', null, null];
    run.loadout.storage = [...eq.armaments.map((p) => p.id).filter((id) => !['greatsword', 'dagger', 'buckler'].includes(id)),
      ...eq.armour.filter((p) => p.classId === classId && p.id !== run.loadout.sets.armor[0]).map((p) => p.id)];
    edit.loadout = { sets: { rightHand: sets.rightHand, leftHand: sets.leftHand }, storage: run.loadout.storage };
  }
  return { run, edit };
}

const cut = (stats) => ({
  rows: stats.rows.map((r) => ({ id: r.id, label: r.label, faceLabel: r.faceLabel, value: r.value, formula: r.formula ?? null })),
  attributes: stats.attributes.map((a) => ({
    id: a.id, value: a.face.value, label: a.face.label, mainSummary: a.face.mainSummary, summary: a.face.summary, compact: a.face.compact === true,
    title: a.reveal.title, sense: a.reveal.sense, flavour: a.reveal.flavour, lines: a.reveal.lines, receipt: a.reveal.receipt, tooltip: a.tooltip,
  })),
});

const tally = { cases: 0, previews: 0, errors: 0 };
const cases = [];
for (const classId of classes) {
  for (const variant of ['start', 'levelled', 'geared']) {
    const { run, edit } = runFor(classId, variant);
    const regs = sheet.characterSheetRegistries(reg, run);
    const entry = { classId, variant, seed: SEED, edit, stats: null, plan: null, previews: [] };
    try {
      entry.stats = cut(inspection.progressionStats(regs, run, {}));
      const plan = levelup.levelUpPlan(regs, run);
      entry.plan = { points: plan.points, offerable: plan.offerable, capped: plan.capped, blockedBy: plan.blockedBy, level: plan.level, xp: plan.xp, xpToNext: plan.xpToNext, attributes: plan.attributes.map((a) => ({ id: a.id, label: a.label })) };
      tally.cases++;
    } catch (e) { entry.error = e.message; tally.errors++; }
    if (!entry.error) {
      // The dialog's preview: the same card model over the run with the pending points added.
      const ids = Object.keys(run.attributes);
      const pendings = [...ids.map((id) => ({ [id]: 1 })), { [ids[0]]: 2 }];
      for (const pending of pendings) {
        const preview = { ...run, attributes: Object.fromEntries(ids.map((id) => [id, run.attributes[id] + (pending[id] || 0)])) };
        entry.previews.push({ pending, stats: cut(inspection.progressionStats(regs, preview, {})) });
        tally.previews++;
      }
    }
    cases.push(entry);
  }
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
let bytes = 0;
const write = (name, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, name), text); bytes += Buffer.byteLength(text); };
write('stats.json', { cases });
write('index.json', { source: 'ui/models/ProgressionInspectionModel.js, model/levelup.js, ui/models/CharacterSheetModel.js (configured profile default)', webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, seed: SEED, tally });
console.log(`oracle-core2-progression: ${tally.cases} cases, ${tally.previews} allocation previews, ${tally.errors} refused by the web, ${(bytes / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/progression`);
