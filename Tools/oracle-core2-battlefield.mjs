#!/usr/bin/env node
// Core 2 battlefield oracle (P-4, docs/DECISIONS.md D-1540..). Records what the web's combat board does with a formation:
//
//   model.json   the three functions of the default (alternative) appearance run on a matrix of fields, party and enemy lists, as battlefieldStage.js feeds them: alternativeFormation
//                (src/ui/models/AlternativeFormationModel.js) with the plan combatFormation hands it, fitAlternativeSprites, alternativeCombatComposition (CombatCompositionModel.js).
//   board.json   the shipped board measured in a browser: the field, the cells of the grid (their boxes and foot anchors), each combatant's foot, height and zoom, for 1 to 4 enemies at
//                1920x1080 and 390x844.
//   index.json
//
//   node Tools/oracle-core2-battlefield.mjs [--base http://localhost:8731/] [--source <web checkout>]
//
// --base serves the web at the pinned sha (board.json drives its combat shot). Deterministic. Writes Unity/Assets/Tests/Oracle/core2/battlefield/*.json.
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon, arg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'battlefield');
const BASE = arg('--base', 'http://localhost:8731/');
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const w = await loadWeb();
const [formation, composition, scaleModel, formationLayout, presentationModule, defaults, stature, expansion] = await Promise.all([
  load('src/ui/models/AlternativeFormationModel.js'), load('src/ui/models/CombatCompositionModel.js'), load('src/ui/models/CombatSpriteScaleModel.js'), load('src/model/formationLayout.js'),
  load('src/model/advancedConfig.js'), load('src/content/settingsDefaults.js'), load('src/ui/components/stature.js'), load('src/content/enemyExpansion.js'),
]);
const combatFormationModel = await load('src/ui/models/CombatFormationModel.js');
const json = (v) => `${JSON.stringify(canon(v), null, 1)}\n`;
const index = { webSha: w.head, files: [] };
const write = (file, value) => {
  const text = json(value);
  writeFileSync(join(OUT, file), text);
  index.files.push({ file, bytes: Buffer.byteLength(text) });
};
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const round = (v) => (typeof v === 'number' ? Math.round(v * 1e6) / 1e6 : v);

// ---------------------------------------------------------------------------------------------------------------
// The model functions, as battlefieldStage.js feeds them (refresh(), lines 636-800).
// ---------------------------------------------------------------------------------------------------------------
const presentation = { ...presentationModule.presentationConfig(defaults.SETTINGS_DEFAULTS.values), perspectiveGrid: true };
const STAGE = 190;
const roster = Object.fromEntries(w.registries.enemies.all().map((def) => [def.id, def]));
const rosterIds = Object.keys(roster);
const rows = 'ABCDEF';
const pick = (list, n) => Array.from({ length: n }, (_, i) => list[i % list.length]);
// Enemy lists: plain, mixed statures, low silhouettes, a boss.
const enemyLists = {
  plain: ['wanderingSoldier'], hounds: ['blightHound', 'blightHound', 'graveWisp'], bruiser: ['huskBrute', 'graveWisp'],
  elite: ['wyrmAspirant'], boss: ['fellWarden'], dragon: ['ashheartDragon'], mixed: ['wanderingSoldier', 'wyrmAspirant', 'lanternMoth', 'huskBrute'],
};
const fields = [
  { width: 1920, height: 529.015625, formFactor: 'desktop', narrow: false }, { width: 1280, height: 352.6770833, formFactor: 'desktop', narrow: false },
  { width: 1366, height: 380, formFactor: 'desktop', narrow: false }, { width: 1024, height: 300, formFactor: 'tablet', narrow: false },
  { width: 2560, height: 705, formFactor: 'desktop', narrow: false }, { width: 390, height: 420.6875, formFactor: 'mobile', narrow: true },
  { width: 430, height: 463.8, formFactor: 'mobile', narrow: true }, { width: 360, height: 300, formFactor: 'mobile', narrow: true },
  { width: 844, height: 260, formFactor: 'mobile', narrow: false },
];
const cases = [];
for (const field of fields) for (const [listName, list] of Object.entries(enemyLists)) for (const count of [1, 2, 3, 4, 5, 7]) {
  const ids = pick(list, count);
  const enemyIdsOfFight = ids.map((id, i) => `e${i + 1}`);
  const plan = combatFormationModel.combatFormation({ width: field.width, height: field.height, presentation, footerClearance: 0, friends: ['player'], enemies: enemyIdsOfFight });
  const placed = formation.alternativeFormation(plan, field.width, field.height, field.narrow, { ...presentation, formFactor: field.formFactor });
  const actors = placed.slots.map((slot) => {
    const enemy = slot.id !== 'player';
    const defId = enemy ? ids[Number(slot.id.slice(1)) - 1] : null;
    const def = enemy ? roster[defId] : null;
    const statureOf = enemy ? stature.statureFor(w.registries, defId) : 'normal';
    const ratio = enemy ? scaleModel.combatSpriteRatio(statureOf, defId) : 1;
    const art = enemy ? expansion.enemyExpansion[defId] : null;
    const visibleWidth = enemy ? art.idleWidth * STAGE / art.idleHeight : 179.08;
    const multiplier = (presentation[`row${rows[slot.row]}Scale`] ?? 1) * (enemy ? presentation.enemySpriteScale : presentation.playerSpriteScale) * 1.1;
    const leading = Math.max(Math.min(66, field.height * 0.25), 0 + 0 + 14);
    return { slot, side: enemy ? 'enemy' : 'player', stature: statureOf, enemyId: defId, ratio, multiplier, boxHeight: STAGE, visibleHeight: STAGE, visibleWidth, footOffset: 0, leading, artLeading: 0 };
  });
  const sizes = formation.fitAlternativeSprites({ width: field.width, height: field.height, actors, narrow: field.narrow });
  const composed = composition.alternativeCombatComposition({ width: field.width, height: field.height, actors, sizes, perspectiveGrid: true, handTop: null, handLeft: 0, solo: true });
  cases.push({
    field, list: listName, enemies: ids, rows: placed.rows,
    cells: placed.visualCells.map((c) => ({ cell: c.cell, side: c.side, lane: c.lane, rank: c.rank, band: c.visualBand, role: c.formationRole, x: round(c.x), ground: round(c.ground), box: c.box && Object.fromEntries(Object.entries(c.box).map(([k, v]) => [k, round(v)])), width: round(c.width), artWidth: round(c.artWidth), miniHudLeft: round(c.miniHudLeft), miniHudWidth: round(c.miniHudWidth), depth: c.depth, layer: c.layer, visualOnly: !!c.visualOnly })),
    slots: placed.slots.map((s) => ({ id: s.id, cell: s.cell, row: s.row, lane: s.lane, rank: s.rank, x: round(s.x), ground: round(s.ground) })),
    actors: actors.map((a) => ({ id: a.slot.id, enemyId: a.enemyId, stature: a.stature, ratio: a.ratio, multiplier: round(a.multiplier), visibleWidth: round(a.visibleWidth), leading: round(a.leading) })),
    sizes: sizes.map((s) => ({ id: s.id, scale: round(s.scale), visibleHeight: round(s.visibleHeight), multiplier: round(s.multiplier), x: round(s.x) })),
    composed: composed.map((s) => ({ id: s.id, scale: round(s.scale), visibleHeight: round(s.visibleHeight), x: round(s.x), ground: round(s.ground) })),
  });
}
write('model.json', { cases });

// ---------------------------------------------------------------------------------------------------------------
// The shipped board, measured.
// ---------------------------------------------------------------------------------------------------------------
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const browser = await chromium.launch();
const board = [];
for (const [width, height] of [[1920, 1080], [390, 844]]) for (const count of [1, 2, 3, 4]) {
  const page = await browser.newPage({ viewport: { width, height } });
  await page.goto(`${BASE}index.html?gameProfile=core&shot=combat&shotSeed=ASHEN1&shotEnemyCount=${count}`);
  await page.waitForTimeout(5000);
  const measured = await page.evaluate(() => {
    const r = (e) => { if (!e) return null; const b = e.getBoundingClientRect(); return { left: b.left, top: b.top, width: b.width, height: b.height }; };
    const combat = document.querySelector('.combat');
    const field = combat.querySelector('.field');
    const plan = field.formationPlan;
    return {
      layout: document.documentElement.dataset.layout, formFactor: document.documentElement.dataset.formFactor, zoom: getComputedStyle(document.body).zoom,
      topbar: r(combat.querySelector('.topbar')), field: r(field), hand: r(combat.querySelector('.hand-area')),
      plan: { width: plan.width, height: plan.height, rows: plan.plan.rows, ground: plan.plan.ground },
      cells: [...document.querySelectorAll('.formation-grid-cell:not([hidden])')].map((c) => ({ cell: c.dataset.cell, side: c.dataset.side, band: c.dataset.visualBand, occupied: c.dataset.occupied, x: Number(c.dataset.anchorX), ground: Number(c.dataset.anchorY), rect: r(c), style: c.getAttribute('style') })),
      slots: plan.plan.slots.map((s) => ({ id: s.id, cell: s.cell, lane: s.lane, rank: s.rank, x: s.x, ground: s.ground, box: s.box, miniHudLeft: s.miniHudLeft, miniHudWidth: s.miniHudWidth })),
      combatants: [...document.querySelectorAll('.combatant')].map((c) => {
        const sprite = c.querySelector('.sprite');
        return {
          id: c.dataset.eid, cell: c.dataset.formationCell, enemyId: sprite?.firstElementChild?.dataset.enemyId ?? null, stature: c.dataset.stature,
          x: Number(c.dataset.formationX), groundY: Number(c.dataset.groundY), groundRatio: Number(c.dataset.groundRatio), baseScale: Number(c.dataset.baseSpriteScale), presentationScale: Number(c.dataset.presentationScale),
          visibleHeight: Number(c.dataset.spriteVisibleHeight), miniHudLeft: Number(c.dataset.miniHudLeft), miniHudTop: Number(c.dataset.miniHudTop), miniHudWidth: Number(c.dataset.miniHudWidth),
          spriteZoom: parseFloat(sprite?.style.zoom), sprite: r(sprite), frame: r(c), shadowWidth: sprite?.style.getPropertyValue('--floor-shadow-width'),
        };
      }),
    };
  });
  await page.close();
  board.push({ viewport: { width, height }, enemies: count, ...measured });
}
await browser.close();
write('board.json', { scenarios: board });
write('index.json', index);
console.log(`oracle-core2-battlefield: ${index.files.length} files in ${OUT}, ${cases.length} model cases`);
