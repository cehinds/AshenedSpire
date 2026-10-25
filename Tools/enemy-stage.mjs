#!/usr/bin/env node
// The enemies' combat figure (P-4, docs/DECISIONS.md D-1540..): the data of the web's expansion stage, src/ui/enemyExpansionStage.js.
//
//   node Tools/enemy-stage.mjs            writes Unity/Assets/StreamingAssets/Content/ui/enemyStage.json
//   node Tools/enemy-stage.mjs --check    fails when the committed file differs from what the web sources give
//
// assets.js enemySprite asks enemyExpansionSprite FIRST (src/ui/assets.js:139), and every enemy of the Core 2 roster (33 of 33) is an expansion enemy, so the figure the web draws for them
// is the expansion stage (assets/enemy-poses/expansion/<id>/<pose>.webp on one 512 canvas with its floor at y 480), not assets-display/alternative/<id>.webp (alternativeSprite, which is
// reached only by an enemy with no expansion frames: none of the roster). This reads the stage's own data and rules from the web and writes what a renderer without a DOM needs:
//   stage        the stage's literals (height 190, canvas 512, default floor 480, the 260 ms a clip's durations are written against), read from the source text;
//   aliases      the stage's pose aliases (guard -> block, hurt -> hit, ...);
//   actions      the clips (poses and their durations) and poses (every frame the figure has);
//   enemies      per enemy: its idle size, its held stance frames per action category (content/enemyStanceSprites.js) and the body landmarks of its frames (content/combatBodyLandmarks.js);
//   stanceOf     the action category of a public intent stance (model/enemyStancePose.js);
//   aura         the colour of a theme, the theme of a buff status and the shadow the stage draws for them (ui/enemyStates.js);
//   animation    per move of every enemy the action family and whether it is a cast (model/actionAnimation.js resolveActionAnimation with the registry's own tags), which is what the stage's attack pose is
//                chosen by, the motion of each family and the tempo and reach of each actor.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { loadWeb, WEB } from './oracle-core2.mjs';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'ui', 'enemyStage.json');
const REGISTRY = JSON.parse(readFileSync(join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'assets', 'registry.json'), 'utf8'));
const CHECK = process.argv.includes('--check');
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const read = (rel) => readFileSync(join(WEB, rel), 'utf8');

const w = await loadWeb();
const [expansion, stances, landmarks, states, pose, plan, animation] = await Promise.all([
  load('src/content/enemyExpansion.js'), load('src/content/enemyStanceSprites.js'), load('src/content/combatBodyLandmarks.js'), load('src/ui/enemyStates.js'),
  load('src/model/enemyActionPose.js'), load('src/model/enemyStancePose.js'), load('src/model/actionAnimation.js'),
]);

// The literals the stage keeps inline, read from its source so a change of them fails the check.
const stageSource = read('src/ui/enemyExpansionStage.js');
const literal = (pattern, what) => { const m = pattern.exec(stageSource); if (!m) throw new Error(`enemy-stage: ${what} not found in enemyExpansionStage.js`); return Number(m[1]); };
const height = literal(/const scale = (\d+) \/ art\.idleHeight/, 'the stage height');
const canvas = literal(/width:\$\{(\d+) \* scale\}px;height:\$\{\1 \* scale\}px/, 'the canvas size');
const floorDefault = literal(/const floor = landmarks\?\.feetY \?\? (\d+);/, 'the default floor');
const clipMs = literal(/frame\.duration \/ (\d+) \* ms/, 'the reference length of a clip');
const bottom = literal(/bottom:\$\{-(\d+) \* scale\}px/, 'the idle floor offset');
if (bottom !== canvas - floorDefault) throw new Error('enemy-stage: the idle floor offset is not canvas - floor');
const aliases = Object.fromEntries([.../const aliases = \{([^}]*)\}/.exec(stageSource)[1].matchAll(/(\w+): '(\w+)'/g)].map((m) => [m[1], m[2]]));

const ids = Object.keys(expansion.enemyExpansion);
const first = expansion.enemyExpansion[ids[0]];
for (const id of ids) {
  const art = expansion.enemyExpansion[id];
  if (JSON.stringify(art.actions) !== JSON.stringify(first.actions) || JSON.stringify(art.poses) !== JSON.stringify(first.poses)) throw new Error(`enemy-stage: ${id} has its own clips (the data shares one set)`);
}
const actions = Object.fromEntries(Object.entries(first.actions).map(([name, frames]) => [name, { poses: frames.map((f) => f.pose), durations: frames.map((f) => f.duration) }]));

const enemies = {};
for (const id of ids) {
  const art = expansion.enemyExpansion[id];
  const marks = {};
  const prefix = `assets/enemy-poses/expansion/${id}/`;
  for (const [path, m] of Object.entries(landmarks.COMBAT_BODY_LANDMARKS)) if (path.startsWith(prefix)) marks[path.slice(prefix.length).replace(/\.webp$/, '')] = [m.headY, m.feetY];
  for (const [pose, ] of Object.entries(marks)) {
    const row = REGISTRY[`exp.${id}.${pose}`];
    if (!row) throw new Error(`enemy-stage: landmarks name ${id}/${pose}, which is not a registered frame`);
  }
  const held = stances.enemyStanceSprites[id];
  for (const framePose of new Set([...first.poses, ...Object.values(held)])) {
    const row = REGISTRY[`exp.${id}.${framePose}`];
    if (!row || !row.unityPath) console.warn(`enemy-stage: ${id} has no imported frame ${framePose}`);
  }
  enemies[id] = { idleWidth: art.idleWidth, idleHeight: art.idleHeight, stances: held, marks };
}

// model/enemyStancePose.js: the public action category of an intent stance.
const stanceNames = ['smashing', 'attacking', 'ranged', 'countering', 'sweeping', 'defending', 'casting', 'preparing', 'staggered'];
const stanceOf = Object.fromEntries(stanceNames.map((s) => [s, plan.enemyStanceCategory({ stance: s })]));

// ui/enemyStates.js: the themes and their colours are module constants, the rest rule is a function: read the constants from the text, and the rest rule is ported (the oracle compares it).
const stateSource = read('src/ui/enemyStates.js');
const themes = new Function(`return ${/const themes = (\{[^]*?\n\});/.exec(stateSource)[1]}`)();
const colors = new Function(`return ${/const ENEMY_AURA_COLORS = Object\.freeze\((\{[^}]*\})\)/.exec(stateSource)[1]}`)();
const negative = [.../const negative = new Set\(\[([^\]]*)\]/.exec(stateSource)[1].matchAll(/'(\w+)'/g)].map((m) => m[1]);
const woundedShare = Number(/entity\.hp \/ entity\.maxHp <= \.(\d+)/.exec(stateSource)[1]) / 100;
const auraBlur = /drop-shadow\(0 0 \$\{(\d+) \+ i \* (\d+)\}px/.exec(stateSource);
if (!auraBlur) throw new Error('enemy-stage: the aura shadow not found in enemyStates.js');

// model/actionAnimation.js and the tables it reads: every move of every enemy, with the web registry's own tags.
const config = (await load('src/content/actionAnimations.js'));
const moves = {};
for (const def of w.registries.enemies.all()) {
  for (const [moveId, move] of Object.entries(def.moves || {})) {
    const resolved = animation.resolveActionAnimation({ actorId: def.id, actionId: moveId, tags: move.tags || [], type: undefined, intent: move.intent, availablePoses: [] });
    moves[`${def.id}:${moveId}`] = { family: resolved.family, casting: resolved.casting };
  }
}
const actors = {};
for (const [id, a] of Object.entries(config.ACTION_ANIMATION_ACTORS)) actors[id] = { tempo: a.tempo, reach: a.reach };

// The figure's travel while it acts (styles/combat.css family-* keyframes, ui/screens/combat.js playFamilyAnimation): the translate of each family's motion as stops of 0, the recoil or the travel,
// the distances of both (direction * 26 * reach and -direction * 12 * reach, the enemy's direction being -1) and the easing the shorthand names.
const css = read('styles/combat.css');
const combatSource = read('src/ui/screens/combat.js');
const travelPx = Number(/'--action-travel': `\$\{direction \* (\d+) \* reach\}px`/.exec(combatSource)?.[1]);
const recoilPx = Number(/'--action-recoil': `\$\{-direction \* (\d+) \* reach\}px`/.exec(combatSource)?.[1]);
const direction = Number(/const direction = actorEl\.closest\('\.enemy'\) \? (-?\d+) : 1;/.exec(combatSource)?.[1]);
if (!(travelPx > 0 && recoilPx > 0 && direction)) throw new Error('enemy-stage: the action travel is not in combat.js playFamilyAnimation');
const easing = /\.enemy \.sprite\.act-attack \{ animation: lunge-left \d+ms ([\w-]+);/.exec(css)?.[1];
if (!easing) throw new Error('enemy-stage: the enemy lunge easing is not in combat.css');
const motionOf = (name) => {
  const block = new RegExp(`@keyframes ${name} \\{([^]*?)\\n\\}`).exec(css);
  if (!block) throw new Error(`enemy-stage: @keyframes ${name} not found in combat.css`);
  const stops = [];
  for (const m of block[1].matchAll(/([\d.]+%(?:,\s*[\d.]+%)*)\s*\{([^}]*)\}/g)) {
    const translate = /translate:\s*([^;]*);/.exec(m[2])?.[1];
    if (!translate) continue;
    const value = /--action-recoil/.test(translate) ? 'recoil' : /--action-travel/.test(translate) ? 'travel' : 'zero';
    for (const pct of m[1].split(',')) stops.push({ t: Number.parseFloat(pct) / 100, at: value });
  }
  return stops.sort((a, b) => a.t - b.t);
};
const travel = { sweep: motionOf('family-slash'), lunge: motionOf('family-thrust'), impact: motionOf('family-strike') };

const built = {
  comment: 'Generated by Tools/enemy-stage.mjs from the web at core2.webSha (src/ui/enemyExpansionStage.js, content/enemyExpansion.js, content/enemyStanceSprites.js, content/combatBodyLandmarks.js, ui/enemyStates.js, model/enemyStancePose.js, '
    + 'model/enemyActionPose.js, model/actionAnimation.js). Do not edit. The figure of an enemy is the expansion stage: one canvas of canvas units with its floor at the enemy\'s landmark (default floor), drawn so the idle figure is stage.height tall; '
    + 'enemies[id].marks[pose] is [headY, feetY] of the frame\'s source raster and stances[family] the frame held for the category of the public intent.',
  stage: { height, canvas, floor: floorDefault, clipMs },
  aliases,
  poses: first.poses,
  actions,
  families: stances.ENEMY_STANCE_FAMILIES,
  stanceOf,
  enemies,
  aura: { colors, themes, negative, woundedShare, blurBase: Number(auraBlur[1]), blurStep: Number(auraBlur[2]) },
  animation: { travel: { direction, travelPx, recoilPx, easing: easing.replace(/-(\w)/g, (_, c) => c.toUpperCase()), tracks: travel }, motion: Object.fromEntries(Object.entries(config.ACTION_ANIMATION_FAMILIES).map(([family, row]) => [family, row.motion])), actors, moves },
};
const text = `${JSON.stringify(built, null, 1)}\n`;
if (CHECK) {
  if (!existsSync(OUT) || readFileSync(OUT, 'utf8') !== text) { console.error('enemy-stage: ui/enemyStage.json differs from the web sources (run node Tools/enemy-stage.mjs)'); process.exit(1); }
  console.log(`enemy-stage: ok (${ids.length} enemies, ${Object.keys(moves).length} moves)`);
} else {
  writeFileSync(OUT, text);
  console.log(`enemy-stage: ui/enemyStage.json ${text.length} bytes, ${ids.length} enemies, ${Object.keys(moves).length} moves`);
}
