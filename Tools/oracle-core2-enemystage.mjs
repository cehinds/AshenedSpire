#!/usr/bin/env node
// Core 2 enemy figure oracle (P-4, docs/DECISIONS.md D-1540..). Drives the SHIPPED web at core2.webSha and records what the enemies' expansion stage (src/ui/enemyExpansionStage.js) does:
//
//   placement.json  per enemy, per frame: where the stage draws it (measured in a browser from the stage's own DOM: the frame's rectangle from the idle figure's centre and the stage's top),
//                   as a rest (setState, normalized to the idle body) and as a clip frame (play, not normalized).
//   presentation.json  enemyPresentation (ui/enemyStates.js) for a matrix of entities, and the aura filter of each theme set.
//   stance.json     enemyStanceCategory (model/enemyStancePose.js) for every public intent stance and the frame each enemy holds for each category.
//   actions.json    enemyActionPose (model/enemyActionPose.js) for every move of every enemy, from the web registry's own tags (resolveActionAnimation).
//   clips.json      the frame the stage shows at a moment of a clip (the loop of play(): boundary += duration / 260 * ms) for every clip and a few lengths.
//   index.json
//
//   node Tools/oracle-core2-enemystage.mjs [--base http://localhost:8731/] [--source <web checkout>]
//
// --base serves the web at the pinned sha (the stage is built in its page, no art is needed: the frames are placed by style). Deterministic. Writes Unity/Assets/Tests/Oracle/core2/enemystage/*.json.
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon, arg, createRun, createFight, encounters } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'enemystage');
const BASE = arg('--base', 'http://localhost:8731/');
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const w = await loadWeb();
const [expansion, stances, states, pose, plan, animation] = await Promise.all([
  load('src/content/enemyExpansion.js'), load('src/content/enemyStanceSprites.js'), load('src/ui/enemyStates.js'),
  load('src/model/enemyActionPose.js'), load('src/model/enemyStancePose.js'), load('src/model/actionAnimation.js'),
]);
const json = (v) => `${JSON.stringify(canon(v), null, 1)}\n`;
const index = { webSha: w.head, files: [] };
const write = (file, value) => {
  const text = json(value);
  writeFileSync(join(OUT, file), text);
  index.files.push({ file, bytes: Buffer.byteLength(text) });
};
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const ids = Object.keys(expansion.enemyExpansion);
const round = (v) => Math.round(v * 1e6) / 1e6;

// ---------------------------------------------------------------------------------------------------------------
// Placement: the stage's own DOM, measured.
// ---------------------------------------------------------------------------------------------------------------
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
await page.goto(`${BASE}index.html?gameProfile=core&shot=combat&shotSeed=ASHEN1&shotEnemyCount=1`);
await page.waitForTimeout(1500);
const placement = await page.evaluate(async (enemyIds) => {
  const stageModule = await import('/src/ui/enemyExpansionStage.js');
  const animator = await import('/src/ui/services/PoseAnimator.js');
  const expansionModule = await import('/src/content/enemyExpansion.js');
  const host = document.createElement('div');
  // The page scales its body (the game's UI size); the stage is measured at its own css pixels.
  host.style.cssText = `position:fixed;left:100px;top:100px;width:600px;height:600px;zoom:${1 / (parseFloat(getComputedStyle(document.body).zoom) || 1)}`;
  document.body.append(host);
  const rect = (el) => { const b = el.getBoundingClientRect(); return { left: b.left, top: b.top, width: b.width, height: b.height }; };
  const result = {};
  for (const id of enemyIds) {
    const root = stageModule.enemyExpansionSprite({ id, name: id }, {});
    host.append(root);
    const stage = animator.stageFor(root);
    const img = root.querySelector('img.pose-frame');
    const read = () => {
      const r = rect(root), i = rect(img);
      return { frame: root.dataset.pose, left: i.left - (r.left + r.width / 2), top: i.top - r.top, width: i.width, height: i.height };
    };
    const row = { rootWidth: rect(root).width, rests: {}, frames: {}, stances: {} };
    const entities = {
      idle: {}, guard: { block: 5 }, wounded: { hp: 3, maxHp: 10 }, afflicted: { statuses: { weak: { stacks: 1 } } }, defeated: { alive: false, hp: 0 },
    };
    for (const [name, entity] of Object.entries(entities)) { stage.setState({ hp: 10, maxHp: 10, ...entity }); row.rests[name] = read(); }
    stage.setState({ hp: 10, maxHp: 10 });
    for (const category of ['smash', 'attack', 'counter', 'sweep', 'defend', 'preparing']) { stage.setState({ hp: 10, maxHp: 10, presentationStance: category }); row.stances[category] = read(); }
    stage.setState({ hp: 10, maxHp: 10 });
    for (const framePose of expansionModule.enemyExpansion[id].poses) {
      if (!stage.play(framePose, 100000)) continue;
      row.frames[framePose] = read();
      stage.settle();
      stage.setState({ hp: 10, maxHp: 10 });
    }
    stage.dispose();
    root.remove();
    result[id] = row;
  }
  return result;
}, ids);
await browser.close();
for (const row of Object.values(placement)) for (const group of ['rests', 'frames', 'stances']) for (const r of Object.values(row[group])) for (const k of ['left', 'top', 'width', 'height']) r[k] = round(r[k]);
write('placement.json', { stageHeight: 190, enemies: placement });

// ---------------------------------------------------------------------------------------------------------------
// enemyPresentation
// ---------------------------------------------------------------------------------------------------------------
const statusNames = ['strength', 'rallyingStandard', 'dexterity', 'unbreakable', 'regen', 'goreblood', 'frostResist', 'starstoneCharge', 'thornHalo', 'emberTide', 'afterimage', 'weak', 'vulnerable', 'burn', 'venom', 'silenced', 'staggered', 'madness'];
const entities = [];
const hps = [[10, 10], [3, 10], [4, 10], [0, 10], [1, 1], [10, 0]];
for (const [hp, maxHp] of hps) for (const block of [0, 4]) {
  entities.push({ hp, maxHp, block });
  for (const status of statusNames) entities.push({ hp, maxHp, block, statuses: { [status]: { stacks: 2 } } });
}
entities.push({ hp: 10, maxHp: 10, statuses: { strength: { stacks: 1 }, dexterity: { stacks: 1 }, regen: { stacks: 1 } } });
entities.push({ hp: 10, maxHp: 10, statuses: { weak: { stacks: 0 }, strength: { stacks: 0 } } });
entities.push({ hp: 10, maxHp: 10, statuses: { weak: { stacks: 3, meter: true } } });
entities.push({ hp: 10, maxHp: 10, alive: false });
const presentation = entities.map((entity) => ({ entity, statusOrder: Object.keys(entity.statuses || {}), result: states.enemyPresentation(entity), filter: states.enemyAuraFilter(states.enemyPresentation(entity).auraThemes) }));
write('presentation.json', { cases: presentation });

// ---------------------------------------------------------------------------------------------------------------
// stances and action poses
// ---------------------------------------------------------------------------------------------------------------
const intents = [];
for (const stance of ['smashing', 'attacking', 'ranged', 'countering', 'sweeping', 'defending', 'casting', 'preparing', 'staggered']) intents.push({ stance });
for (const camp of [null, 'physical', 'spell', 'spells', 'magic']) for (const maneuver of [null, 'attack', 'defend', 'counter', 'sweep', 'smash', 'ranged']) for (const kind of [undefined, 'attack', 'block', 'defend', 'buff', 'debuff', 'attackDebuff', 'staggered', 'unknown']) {
  intents.push({ kind, combatProfile: { camp, maneuver } });
  intents.push({ kind, profile: { camp, maneuver } });
}
intents.push({}, null, { kind: 'unknown' });
write('stance.json', { cases: intents.map((intent) => ({ intent, category: plan.enemyStanceCategory(intent ?? undefined) ?? null })) });

const actions = {};
for (const def of w.registries.enemies.all()) for (const [moveId, move] of Object.entries(def.moves || {})) {
  const resolved = animation.resolveActionAnimation({ actorId: def.id, actionId: moveId, tags: move.tags || [], type: undefined, intent: move.intent, availablePoses: [] });
  const kinds = [...new Set(['attack', 'block', 'buff', 'debuff', move.intent])];
  actions[`${def.id}:${moveId}`] = { intent: move.intent, byKind: Object.fromEntries(kinds.map((kind) => [kind, pose.enemyActionPose(kind, resolved)])), family: resolved.family, casting: resolved.casting, tempo: resolved.tempo, reach: resolved.reach };
}
write('actions.json', { moves: actions });

// The category each enemy of each encounter holds at the start of the fight and the one each of its moves announces once it starts it (ui/screens/combat.js applyBeatToDisp:
// enemyStanceCategory of the move's kind and the combat profile of enemyMoveCarrier), on the fights the oracle builds.
const tactics = await load('src/engine/combatCardTactics.js');
const fights = [];
for (const encounter of encounters(w)) {
  const seed = 7;
  const run = createRun(w, seed, 'reaver');
  const { combat } = createFight(w, run, encounter);
  const row = { encounterId: encounter.id, seed, classId: 'reaver', enemies: {} };
  for (const enemy of combat.enemies) {
    const def = w.registries.enemies.get(enemy.enemyId);
    const moves = {};
    for (const [moveId, move] of Object.entries(def.moves || {})) {
      const carrier = tactics.enemyMoveCarrier(enemy, move, moveId, combat);
      moves[moveId] = plan.enemyStanceCategory({ kind: move.intent, combatProfile: carrier.combatProfile }) ?? null;
    }
    row.enemies[enemy.id] = { enemyId: enemy.enemyId, start: plan.enemyStanceCategory(enemy.intent) ?? null, moves };
  }
  fights.push(row);
}
write('fights.json', { fights });

// ---------------------------------------------------------------------------------------------------------------
// clips: play()'s loop (enemyExpansionStage.js), evaluated at a few moments
// ---------------------------------------------------------------------------------------------------------------
const clips = {};
const sample = expansion.enemyExpansion[ids[0]];
for (const [name, sequence] of Object.entries(sample.actions)) {
  const rows = [];
  for (const ms of [120, 260, 500, 1000]) for (const at of [0, 0.1, 0.25, 0.4, 0.5, 0.6, 0.75, 0.9, 0.999]) {
    const elapsed = ms * at;
    let boundary = 0;
    const frame = sequence.find((f) => { boundary += f.duration / 260 * ms; return elapsed < boundary; }) || sequence.at(-1);
    rows.push({ ms, elapsed, frame: frame.pose });
  }
  clips[name] = rows;
}
write('clips.json', { clips });
write('index.json', index);
console.log(`oracle-core2-enemystage: ${index.files.length} files in ${OUT}`);
