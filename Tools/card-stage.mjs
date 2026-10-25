#!/usr/bin/env node
// The player's combat figure (P-3b, docs/DECISIONS.md D-1490..): the data of the web's card stage, src/ui/alternativeCardStage.js.
//
//   node Tools/card-stage.mjs            writes Unity/Assets/StreamingAssets/Content/ui/cardStage.json
//   node Tools/card-stage.mjs --check    fails when the committed file differs from what the web sources give
//
// The web draws the combat figure of every class (display appearance "alternative", the default; DEFAULT_COMBAT_WEAPONS paints the class's starter hands) as a card stage:
// one canvas on which the class's card-action frames, its held stance frames, the Reaver greatsword pilot frames or the other classes' smash frames, the counter and sweep
// frames and their effect trails are drawn at their own place, sampled along a sequence of poses. The stage is assembled by model/cardAnimationFamily.js
// alternativeCardAnimationFamily; this reads that assembly itself (so no list of frames is copied) and writes, per class, what a renderer without a canvas needs:
//   frames[name]   the art id of the frame (assets/registry.json alt.*, sweep.*) and the rectangle paint() draws it in (stage units: the stage's own 768x544 canvas with its padding
//                  cancelled, so the anchor x is 384 and the floor y is 480), its body landmarks (ui/models/CombatBodyModel.js bodyPlacement) and the pose it is part of;
//   sequences      the card actions (poses, durations, travel, impact, trails), including the Reaver's own attack, smash and sweep clips;
//   reactions      the pilot's body reactions (evade, guard-break, fully-defended, defend; model/selectedWeaponSequences.js);
//   hurt, defeated the hurt clip's registration over the ready figure and the downed figure's art.
// Sources, all at the sha pinned in Tools/export/sources.json (core2.webSha).
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const WEB = arg('--source', process.env.ASHEN_SOURCE || '/home/user/cehinds/ashenspire');
const OUT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'ui', 'cardStage.json');
const REGISTRY = JSON.parse(readFileSync(join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'assets', 'registry.json'), 'utf8'));
const CHECK = process.argv.includes('--check');
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const read = (rel) => readFileSync(join(WEB, rel), 'utf8');

const [family, anim, weapon, weaponPresentation, landmarks, hurtFrames, defeated, seq, smash] = await Promise.all([
  load('src/model/cardAnimationFamily.js'), load('src/model/alternativeCardAnimation.js'), load('src/model/selectedWeaponStances.js'), load('src/model/combatWeaponPresentation.js'),
  load('src/content/combatBodyLandmarks.js'), load('src/content/playerHurtFrames.js'), load('src/content/defeatedArt.js'), load('src/model/selectedWeaponSequences.js'), load('src/model/classSmashAnimations.js'),
]);
const CLASSES = ['reaver', 'rogue', 'starseer', 'herald'];

// The art id of a source file: the registry's own mapping (Tools/assets.config.json release.rules), never a second spelling of it.
const artBySource = new Map(Object.entries(REGISTRY).map(([id, row]) => [row.source, id]));
const art = (path) => {
  const id = artBySource.get(path);
  if (!id || !REGISTRY[id].unityPath) throw new Error(`card-stage: ${path} is not an imported asset (run Tools/scan-assets.mjs --release ... --import)`);
  return id;
};

// The literals alternativeCardStage.js keeps inline, read from its source so a change of them fails the check.
const stageSource = read('src/ui/alternativeCardStage.js');
const literal = (pattern, what) => { const m = pattern.exec(stageSource); if (!m) throw new Error(`card-stage: ${what} not found in alternativeCardStage.js`); return Number(m[1]); };
const flashMs = literal(/flashAt=performance\.now\(\);flashMs=Math\.max\(HURT_CLIP_MIN_MS,ms>0\?ms:(\d+)\)/, 'the default hit length');
const playMs = literal(/play\(action,ms,aura=\[\]\)\{[^]*?ms\?\?=(\d+);/, 'the default action length');
const reactMs = literal(/reactPose\(kind, ms=(\d+)\)/, 'the default reaction length');
const sourceOf = (rel, pattern, what) => { const m = pattern.exec(read(rel)); if (!m) throw new Error(`card-stage: ${what} not found in ${rel}`); return Number(m[1]); };
const durationMs = sourceOf('src/model/alternativeCardAnimation.js', /speed\.lungeMs \/ (\d+)/, 'the reference length of a clip');
const pilotUnits = sourceOf('src/model/selectedWeaponSequences.js', /sequence\.timingUnits \|\| (\d+)/, 'the units of a pilot clip');
const FRAME_TOP = literal(/left:leftPadding\+offset\[0\]\+bodyX\+place\.dx, top:(16)\+topPadding/, 'the frame top');
const DEFAULT_OFFSET = literal(/:\s*\[(128),0\];/, 'the default draw offset');
const DEFAULT_SIZE = literal(/:\s*\[(512),512\];/, 'the default draw size');
const ANCHOR_X = literal(/anchorX:leftPadding\+(384)\+bodyX/, 'the anchor x');
const FLOOR_Y = literal(/bodyPlacement\(authored, landmarks, bodyHeight, (480)\+topPadding\)/, 'the floor y');

// The tint of a stagger on the card stage (styles/combat.css rear-stagger-flash): red from the start to this share of the wobble, then none.
const css = read('styles/combat.css');
const stagger = /@keyframes rear-stagger-flash\s*\{\s*0%,\s*[\d.]+%\s*\{[^}]*\}\s*([\d.]+)%/.exec(css);
if (!stagger) throw new Error('card-stage: rear-stagger-flash not found in styles/combat.css');
const staggerTintShare = Number(stagger[1]) / 100;
const reactionKinds = ['evade', 'guard-break', 'fully-defended', 'defend'];
const classes = {};
for (const classId of CLASSES) {
  const profiles = weaponPresentation.combatWeaponProfilesFor({ appearance: 'alternative', classId, armourId: 'default', grip: { mode: 'one' } });
  const assembled = family.alternativeCardAnimationFamily(classId, { weaponProfile: profiles.weaponProfile, smashProfile: profiles.smashProfile });
  const { family: fam, selected, pilotFrames, classSmash, frames } = assembled;
  const unit = 464 - fam.frames.ready.bounds[1];
  const ready = landmarks.COMBAT_BODY_LANDMARKS[fam.frames.ready.path];
  const readyDraw = fam.frames.ready.size?.[1] || DEFAULT_SIZE;
  const bodyHeight = ready ? (ready.feetY - ready.headY) * readyDraw / ready.height : unit;
  const rows = {};
  for (const [name, frame] of Object.entries(frames).sort(([a], [b]) => (a < b ? -1 : 1))) {
    const offset = frame.drawOffset ? frame.drawOffset : [DEFAULT_OFFSET, 0];
    const size = frame.drawOffset ? frame.size : [DEFAULT_SIZE, DEFAULT_SIZE];
    const marks = landmarks.COMBAT_BODY_LANDMARKS[frame.path];
    rows[name] = { art: art(frame.path), x: offset[0], y: FRAME_TOP + offset[1], w: size[0], h: size[1], marks: marks ? [marks.headY, marks.feetY, marks.height] : null };
  }
  // The trails the counter and sweep clips lay over the body: 512 square at the default offset (paint: drawImage(overlay, 128 + x, 16, 512, 512)).
  const effects = {};
  for (const [name, frame] of Object.entries(selected.effects || {}).sort(([a], [b]) => (a < b ? -1 : 1))) effects[name] = art(frame.path);
  const sequences = {};
  for (const [name, sequence] of Object.entries(fam.sequences).sort(([a], [b]) => (a < b ? -1 : 1))) {
    sequences[name] = {
      poses: sequence.poses, durations: sequence.durations, travel: sequence.travel, impact: sequence.impact, hold: !!sequence.hold,
      ...(sequence.effects ? { effects: sequence.effects } : {}), ...(sequence.pilot ? { pilot: true } : {}), ...(sequence.timingUnits ? { timingUnits: sequence.timingUnits } : {}),
      ...(sequence.contactCue ? { contactCue: true } : {}), ...(sequence.reaction ? { reaction: true } : {}),
    };
  }
  const hurt = hurtFrames.playerHurtFrames[classId];
  const down = defeated.DEFEATED_ART[classId];
  classes[classId] = {
    unit, bodyHeight, pilot: !!pilotFrames, classSmash: !!classSmash,
    held: Object.keys(assembled.heldFrames).sort(), counterRest: selected.stances.counter,
    hurt: { art: art(hurt.file), dx: hurt.dx, dy: hurt.dy, scale: hurt.scale }, defeated: art(down.file),
    frames: rows, effects, sequences,
  };
}

const reactions = {};
for (const kind of reactionKinds) {
  const s = seq.selectedWeaponSequence(kind);
  reactions[kind] = { poses: s.poses, durations: s.durations, travel: s.travel, impact: s.impact, hold: !!s.hold, pilot: true, ...(s.reaction ? { reaction: true } : {}) };
}

const built = {
  comment: 'Generated by Tools/card-stage.mjs from the web at core2.webSha (src/ui/alternativeCardStage.js, model/cardAnimationFamily.js, model/alternativeCardAnimation.js, model/selectedWeaponSequences.js, content/combatBodyLandmarks.js, content/playerHurtFrames.js, content/defeatedArt.js). Do not edit. '
    + 'The combat figure of a class is a card stage: frames[name] is a frame\'s art id, the rectangle (x, y, w, h) it is drawn in on the stage\'s canvas with the padding cancelled (the anchor is x 384 and the floor is y 480) and its body landmarks [headY, feetY, height] of the source raster; '
    + 'sequences[name] the poses of an action with their durations, the travel of the body and the impact index; unit is the height (canvas units) of the ready figure, which the figure box shows.',
  stage: { anchorX: ANCHOR_X, floorY: FLOOR_Y, frameTop: FRAME_TOP, defaultX: DEFAULT_OFFSET, defaultSize: DEFAULT_SIZE },
  motion: {
    hurtFrames: anim.HURT_CLIP_FRAMES, hurtRedOpacity: anim.HURT_RED_OPACITY, hurtMinMs: anim.HURT_CLIP_MIN_MS, recoilPx: anim.HIT_RECOIL_PX, recoilPeak: anim.HIT_RECOIL_PEAK,
    hitMs: flashMs, playMs, reactMs, durationMs, pilotUnits, staggerTintShare,
  },
  aliases: family.CARD_ANIMATION_ALIASES,
  reactionKinds,
  reactions,
  classes,
};
const text = `${JSON.stringify(built, null, 1)}\n`;
if (CHECK) {
  if (!existsSync(OUT) || readFileSync(OUT, 'utf8') !== text) { console.error('card-stage: ui/cardStage.json differs from the web sources (run node Tools/card-stage.mjs)'); process.exit(1); }
  console.log(`card-stage: ok (${CLASSES.length} classes, ${Object.values(classes).reduce((n, c) => n + Object.keys(c.frames).length, 0)} frames)`);
} else {
  writeFileSync(OUT, text);
  console.log(`card-stage: ui/cardStage.json ${text.length} bytes, ${Object.values(classes).reduce((n, c) => n + Object.keys(c.frames).length, 0)} frames`);
}
