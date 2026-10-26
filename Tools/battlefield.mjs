#!/usr/bin/env node
// The battlefield's formation (P-4, docs/DECISIONS.md D-1540..): the data of the web's combat board layout, src/ui/models/AlternativeFormationModel.js, CombatCompositionModel.js,
// CombatSpriteScaleModel.js, ui/components/battlefieldStage.js.
//
//   node Tools/battlefield.mjs            writes Unity/Assets/StreamingAssets/Content/ui/battlefield.json
//   node Tools/battlefield.mjs --check    fails when the committed file differs from what the web sources give
//
// The default (alternative) appearance lays one perspective grid over the field: enemies fill a back rank and then a front rank across the lanes, the party shares one rank, every cell is the same
// parallelogram and each cell's foot anchor is where its figure's feet stand. This reads the configuration of that model (content/config/ui/presentation/combatFormationModel.json through
// config/generated/ui.js), the player's presentation settings at the web's defaults (content/settingsDefaults.js through model/advancedConfig.js presentationConfig), the stature of every enemy
// (ui/components/stature.js: its encounter pool) and the literals the fit keeps inline. Every inline literal is read back from the web source before it is written, so a change of one fails the check.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { loadWeb, WEB } from './oracle-core2.mjs';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'ui', 'battlefield.json');
const CHECK = process.argv.includes('--check');
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const read = (rel) => readFileSync(join(WEB, rel), 'utf8');

const w = await loadWeb();
const [ui, wireframe, advanced, defaults, stature, layout] = await Promise.all([
  load('src/config/generated/ui.js'), load('src/content/wireframeUi.js'), load('src/model/advancedConfig.js'), load('src/content/settingsDefaults.js'), load('src/ui/components/stature.js'),
  load('src/model/formationLayout.js'),
]);
const model = JSON.parse(JSON.stringify(ui.uiConfig.presentation.combatFormationModel));
const grid = model.positioning.perspectiveGrid;
delete grid.scene;
const presentation = advanced.presentationConfig(defaults.SETTINGS_DEFAULTS.values);

// The literals of the fit, each with the source line it is read from (a regex that must match the web's text).
const sources = {
  formation: read('src/ui/models/AlternativeFormationModel.js'),
  composition: read('src/ui/models/CombatCompositionModel.js'),
  scale: read('src/ui/models/CombatSpriteScaleModel.js'),
  stage: read('src/ui/components/battlefieldStage.js'),
  stature: read('src/ui/components/stature.js'),
};
const expect = (name, pattern, what) => { if (!pattern.test(sources[name])) throw new Error(`battlefield: ${what} is not in ${name} (${pattern})`); };
expect('formation', /height \* \(enemy \? \(narrow \? \.46 : \.73\) : \(narrow \? \.46 : \.31\)\) \* ratio/, 'the requested height fractions');
expect('formation', /const ratio = !enemy \? 1 : low \? \.50 : Math\.min\(1\.3, a\.ratio\)/, 'the enemy ratio');
expect('formation', /Math\.min\(1\.25, a\.multiplier \|\| 1\)/, 'the multiplier cap');
expect('formation', /Math\.min\(width - 12, boxWidth/, 'the width margin');
expect('formation', /Math\.min\(a\.slot\.x - 5, width - a\.slot\.x - 5\)/, 'the edge room');
expect('formation', /a\.slot\.ground - a\.leading - 6/, 'the headroom');
expect('formation', /2 \* \(GRID\.footprintInsetPx \+ \.5\)/, 'the footprint inset');
expect('formation', /sideCount <= 3/, 'the spacious side count');
expect('formation', /const rowHeight = height \* profile\.rowHeight;/, 'the row height');
expect('formation', /const baseline = height \* \(profile\.top \+ 4 \* profile\.rowHeight\);/, 'the baseline');
expect('composition', /Math\.max\(halfWidth \+ 6, Math\.min\(width - halfWidth - 6, actor\.slot\.x\)\)/, 'the screen margin');
expect('composition', /\* size\.scale \* \.08/, 'the shadow height share');
expect('composition', /actor\.boxHeight \?\? actor\.visibleHeight \?\? 150/, 'the box height default');
expect('scale', /BOSS_SCALE = Object\.freeze\(\{ ashheartDragon: 3 \}\)/, 'the boss scale');
expect('scale', /stature === 'large'\) return 1\.75/, 'the large ratio');
expect('scale', /return BOSS_SCALE\[enemyId\] \|\| 2/, 'the huge ratio');
expect('stage', /Math\.max\(Math\.min\(66, fieldRect\.height \* \.25\)/, 'the leading cap');
expect('stage', /compactCombat === 'true' \? 4 : 14/, 'the overhead gap');
expect('stature', /\{ normal: 'normal', elite: 'large', boss: 'huge' \}/, 'the statures');
const low = /const LOW_SILHOUETTES = (\[[^\]]*\]);/.exec(sources.formation);
if (!low) throw new Error('battlefield: LOW_SILHOUETTES not found');

const statures = {};
for (const def of w.registries.enemies.all()) statures[def.id] = stature.statureFor(w.registries, def.id);

const built = {
  comment: 'Generated by Tools/battlefield.mjs from the web at core2.webSha (src/ui/models/AlternativeFormationModel.js, CombatCompositionModel.js, CombatSpriteScaleModel.js, ui/components/battlefieldStage.js, ui/components/stature.js, '
    + 'content/config/ui/presentation/combatFormationModel.json and the settings defaults). Do not edit. Lengths are the web\'s client pixels at its own layout: the Unity board scales them by the layout\'s reference ratio.',
  rows: layout.FORMATION_ROWS, visualBands: /visualBand: '(ABCD)'\[band\.index\]/.exec(sources.formation)[1],
  grid,
  presentation: {
    playerSpriteScale: presentation.playerSpriteScale, enemySpriteScale: presentation.enemySpriteScale, displayScale: wireframe.wireframeUi.formation.displayScale,
    rowScale: Object.fromEntries('ABCDEF'.split('').map((row) => [row, presentation[`row${row}Scale`]])),
    rowLayer: Object.fromEntries('ABCDEF'.split('').map((row) => [row, presentation[`row${row}Layer`]])),
    frontLayer: presentation.frontLayer, backLayer: presentation.backLayer, columns: 2, lanes: 3, playerSpawnRow: presentation.playerSpawnRow,
  },
  fit: {
    requested: { enemy: { wide: 0.73, narrow: 0.46 }, player: { wide: 0.31, narrow: 0.46 } },
    lowRatio: 0.5, ratioCap: 1.3, multiplierCap: 1.25, widthMargin: 12, edgeMargin: 5, headroom: 6, spaciousMaxEnemies: 3,
    leading: { cap: 66, heightShare: 0.25, gap: 14, gapCompact: 4 },
    screenMargin: 6, shadowHeightShare: 0.08, defaultBoxHeight: 150,
  },
  stature: { ratio: { large: 1.75, huge: 2 }, bossScale: { ashheartDragon: 3 }, of: statures },
  low: JSON.parse(low[1].replace(/'/g, '"')),
};
const text = `${JSON.stringify(built, null, 1)}\n`;
if (CHECK) {
  if (!existsSync(OUT) || readFileSync(OUT, 'utf8') !== text) { console.error('battlefield: ui/battlefield.json differs from the web sources (run node Tools/battlefield.mjs)'); process.exit(1); }
  console.log(`battlefield: ok (${Object.keys(statures).length} enemies)`);
} else {
  writeFileSync(OUT, text);
  console.log(`battlefield: ui/battlefield.json ${text.length} bytes`);
}
