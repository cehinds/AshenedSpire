#!/usr/bin/env node
// The player's painted figure outside the combat board (P-5, docs/DECISIONS.md D-1594..): the portrait of the combatant door, the stand pose of the Armoury and the conversation figure.
//
//   node Tools/outfit-art.mjs            writes Unity/Assets/StreamingAssets/Content/ui/outfitArt.json
//   node Tools/outfit-art.mjs --check    fails when the committed file differs from what the web sources give
//
// The web draws these through paintedPresentation (src/ui/paintedOutfits.js): the painted outfit of the class and its armour (src/model/paintedOutfitArt.js paintedOutfit, whose `menu` holds the
// stand, portrait, detail and conversation frames), over which the equipment animation set of the held weapons (src/model/equipmentAnimation.js equipmentAnimationForLoadout, animationArt) puts
// its own frame for each view. This runs those functions for every class, every armour of the class and every pair of hands a slot accepts, and writes what a renderer without them needs:
//   outfits[class|armour]            the frames of the outfit for a view when the hands select no animation set (art ids of the registry: outfit.*)
//   hands["class|armour"]["right|left"] the frames of a view that differ from that outfit's, for the hands that select an animation set (art ids: anim.*)
//   ids                              every art id either table names (the art import keeps exactly these: Tools/assets.config.json release.restrict)
// The art id of a source file is the registry's own mapping (Tools/assets.config.json release.rules).
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { loadWeb, createRun, classIds, WEB } from './oracle-core2.mjs';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'ui', 'outfitArt.json');
const CONFIG = JSON.parse(readFileSync(join(ROOT, 'Tools', 'assets.config.json'), 'utf8'));
const CHECK = process.argv.includes('--check');
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);

const w = await loadWeb();
const reg = w.registries;
const [animation, loadout, painted] = await Promise.all([load('src/model/equipmentAnimation.js'), load('src/model/loadout.js'), load('src/model/paintedOutfitArt.js')]);
// The views a Core 2 screen draws: the Armoury and the creation (stand), the combatant door (portrait) and the dialogue (conversation). 'detail' is drawn by no Core 2 screen.
const VIEWS = ['stand', 'portrait', 'conversation'];

// The art id of a source path: the first release rule that matches it, as Tools/lib/release-art.mjs applies them.
const rules = CONFIG.release.rules.map((r) => ({ re: new RegExp(r.match), id: r.id }));
const idOf = (file) => {
  const rel = file;
  const rule = rules.find((r) => r.re.test(rel));
  if (!rule) throw new Error(`outfit-art: no release rule names ${rel}`);
  const m = rule.re.exec(rel);
  return rule.id.replace(/\{lower:\$(\d)\}/g, (_, n) => m[n].toLowerCase()).replace(/\$(\d)/g, (_, n) => m[n]);
};

const used = new Set();
const framesOf = (art) => {
  if (!art?.menu) return null;
  const out = {};
  for (const view of VIEWS) if (art.menu[view]) { out[view] = idOf(art.menu[view]); used.add(out[view]); }
  return out;
};
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

const outfits = {}, hands = {};
const slot = (id) => reg.equipment.slots.find((s) => s.id === id);
const fits = (slotId, piece) => loadout.fitsSlot(slot(slotId), piece);
let combos = 0, thrown = 0;
for (const classId of classIds(w)) {
  const run = createRun(w, 779, classId);
  for (const armour of reg.equipment.armour.filter((p) => p.classId === classId)) {
    // The loadout the web would hold: this armour in the armour slot, the hands as set below.
    const make = (right, left) => {
      const l = structuredClone(run.loadout);
      l.sets.armor = [armour.id]; l.sets.rightHand = [right, null, null]; l.sets.leftHand = [left, null, null];
      return l;
    };
    const spec = loadout.figureSpec(reg, make(null, null), classId);
    const base = painted.paintedOutfit(classId, spec.armourId);
    const fallback = framesOf(base);
    if (!fallback) continue;
    outfits[`${classId}|${armour.id}`] = fallback;
    const rights = [null, ...reg.equipment.armaments.filter((p) => fits('rightHand', p)).map((p) => p.id)];
    const lefts = [null, ...reg.equipment.armaments.filter((p) => fits('leftHand', p)).map((p) => p.id)];
    for (const right of rights) for (const left of lefts) {
      combos++;
      let set;
      try { set = animation.equipmentAnimationForLoadout(reg, make(right, left), classId); } catch { thrown++; continue; }
      if (!set) continue;
      const art = animation.animationArt(set, base);
      const frames = framesOf(art);
      const diff = Object.fromEntries(Object.entries(frames).filter(([view, id]) => fallback[view] !== id));
      if (Object.keys(diff).length) hands[`${classId}|${armour.id}|${right ?? ''}|${left ?? ''}`] = diff;
    }
  }
}

// Hands that give the same frames are named once: `sets` holds each distinct view table, `hands` points into it.
const sets = [];
const handIndex = {};
for (const [key, diff] of Object.entries(hands)) {
  let i = sets.findIndex((s) => same(s, diff));
  if (i < 0) { sets.push(diff); i = sets.length - 1; }
  const [classId, armourId, right, left] = key.split('|');
  (handIndex[`${classId}|${armourId}`] ||= {})[`${right}|${left}`] = i;
}
const built = {
  comment: 'Generated by Tools/outfit-art.mjs from the web at core2.webSha (src/model/paintedOutfitArt.js, src/model/equipmentAnimation.js, src/model/loadout.js). Do not edit. outfits[class|armour] are the menu frames of the painted outfit '
    + '(stand = the Armoury, portrait = the combatant door, conversation = dialogue); hands[class|armour][right|left] indexes sets, the frames of a view that the equipment animation set of those hands replaces.',
  views: VIEWS,
  outfits,
  sets,
  hands: handIndex,
  ids: [...used].sort(),
};
// One line per entry of each table, so the file stays readable and small.
const rows = (v) => (Array.isArray(v) ? `[\n${v.map((x) => ` ${JSON.stringify(x)}`).join(',\n')}\n]` : `{\n${Object.entries(v).map(([k, x]) => ` ${JSON.stringify(k)}: ${JSON.stringify(x)}`).join(',\n')}\n}`);
const text = `{\n"comment": ${JSON.stringify(built.comment)},\n"views": ${JSON.stringify(built.views)},\n"outfits": ${rows(built.outfits)},\n"sets": ${rows(built.sets)},\n"hands": ${rows(built.hands)},\n"ids": ${JSON.stringify(built.ids)}\n}\n`;
if (CHECK) {
  if (!existsSync(OUT) || readFileSync(OUT, 'utf8') !== text) { console.error('outfit-art: ui/outfitArt.json differs from the web sources (run node Tools/outfit-art.mjs)'); process.exit(1); }
  console.log(`outfit-art: ok (${Object.keys(outfits).length} outfits, ${Object.values(handIndex).reduce((n, h) => n + Object.keys(h).length, 0)} hands, ${sets.length} sets, ${used.size} frames)`);
} else {
  writeFileSync(OUT, text);
  console.log(`outfit-art: wrote ${Object.keys(outfits).length} outfits, ${Object.values(handIndex).reduce((n, h) => n + Object.keys(h).length, 0)} hands (${combos} pairs tried, ${thrown} refused), ${sets.length} sets, ${used.size} frames`);
}
