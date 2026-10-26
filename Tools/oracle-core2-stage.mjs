#!/usr/bin/env node
// Core 2 combat figure oracle (P-3b, docs/DECISIONS.md D-1490..). Drives the SHIPPED web at core2.webSha for the configured `default` profile and
// records what the player's card stage (src/ui/alternativeCardStage.js) is told about each card:
//
//   cards.json     per class, every profile card (plain and upgraded) resolved as a fight resolves it: the card action (model/alternativeCardAnimation.js
//                  cardActionFor, cardActionPlan: the technique, the rest it leaves the figure in), the stance family it holds the figure in
//                  (model/alternativeStance.js stanceForCard), the Reaver pilot's stance and counter kind (model/selectedWeaponStances.js).
//   placement.json per class, every frame of the stage assembled as the combat assembles it, placed as alternativeCardStage.js paint places it (body
//                  registration included, ui/models/CombatBodyModel.js bodyPlacement) at the held and at the playing registration.
//   index.json
//
//   node Tools/oracle-core2-stage.mjs [--source <web checkout>]
//
// Deterministic. Writes Unity/Assets/Tests/Oracle/core2/stage/*.json (sorted keys).
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon, classIds, encounters, createRun, createFight } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'stage');
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [expansion, action, stances, weaponStances, weaponPresentation, family, bodyModel, landmarks, combatAnimation, tagServiceMod] = await Promise.all([
  load('src/engine/combatExpansion.js'), load('src/model/alternativeCardAnimation.js'), load('src/model/alternativeStance.js'), load('src/model/selectedWeaponStances.js'),
  load('src/model/combatWeaponPresentation.js'), load('src/model/cardAnimationFamily.js'), load('src/ui/models/CombatBodyModel.js'), load('src/content/combatBodyLandmarks.js'),
  load('src/model/combatAnimation.js'), load('src/model/tagService.js'),
]);
const registries = w.registries;
const json = (v) => `${JSON.stringify(canon(v), null, 1)}\n`;
const guarded = (fn) => { try { return fn(); } catch (e) { return { error: e.message }; } };
const all = encounters(w);
const profileCards = w.gp.gameContent(w.profile, 'cards');
const tagsOf = (def) => (def.cardTags?.length ? def.cardTags : tagServiceMod.tagService(registries).tagsOf('card', def)).map((tag) => (typeof tag === 'string' ? tag : tag.id));

// What combat.js hands the stage for a class: DEFAULT_COMBAT_WEAPONS paints the starter hands (model/combatWeaponPresentation.js combatWeaponProfilesFor).
const profilesFor = (classId) => weaponPresentation.combatWeaponProfilesFor({ appearance: 'alternative', classId, armourId: 'default', grip: { mode: 'one' } });

const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, files: [] };
const write = (file, value) => {
  const text = json(value);
  writeFileSync(join(OUT, file), text);
  index.files.push({ file, bytes: Buffer.byteLength(text) });
};
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));

// ---------------------------------------------------------------------------------------------------------------
// The cards
// ---------------------------------------------------------------------------------------------------------------
const cards = {};
for (const [ci, classId] of classIds(w).entries()) {
  const seed = w.rngMod.sweepSeed(7300 + ci);
  const encounter = all[ci % all.length];
  const run = createRun(w, seed, classId);
  const { combat } = createFight(w, run, encounter);
  const profiles = profilesFor(classId);
  const rows = {};
  for (const card of profileCards) {
    for (const upgraded of [false, true]) {
      const key = `${card.id}${upgraded ? '+' : ''}`;
      rows[key] = guarded(() => {
        const inst = { instanceId: `s:${key}`, cardId: card.id, upgraded };
        const def = expansion.resolveCombatCard(combat, inst);
        const tags = tagsOf(def);
        const resolved = { ...def, cardTags: tags };
        const plan = action.cardActionPlan(resolved, classId);
        const animation = combatAnimation.resolveCombatAnimation({ ...resolved, animationTags: [] }, [], { classId, appearance: 'alternative', combatExpansionVersion: combat.combatExpansionVersion });
        const triggered = combatAnimation.resolveCombatAnimation({ ...resolved, animationTags: [] }, [], { classId, appearance: 'alternative', combatExpansionVersion: combat.combatExpansionVersion, counterTriggered: true });
        const stance = stances.stanceForCard(resolved);
        const profile = profiles.weaponProfile;
        return {
          technique: action.cardActionFor(resolved, classId), group: plan?.group ?? null, rest: plan?.rest ?? null,
          animation: { technique: animation.technique, rest: animation.rest ?? null, group: animation.group }, triggered: { technique: triggered.technique, rest: triggered.rest ?? null },
          stance, pilotStance: weaponStances.selectedWeaponStanceFor({ profile, stance, card: resolved, entity: undefined }),
          pilotStanceReady: weaponStances.selectedWeaponStanceFor({ profile, stance, card: resolved, entity: { alive: true, evade: 1 } }),
          counter: weaponStances.selectedWeaponCounterFor(resolved, profile, registries.cards.get(card.id)),
        };
      });
    }
  }
  cards[classId] = { seed, encounterId: encounter.id, weaponProfile: profiles.weaponProfile, smashProfile: profiles.smashProfile, cards: rows };
}
write('cards.json', { classes: cards });

// ---------------------------------------------------------------------------------------------------------------
// The placement of every frame (alternativeCardStage.js paint)
// ---------------------------------------------------------------------------------------------------------------
const placements = {};
for (const classId of classIds(w)) {
  const profiles = profilesFor(classId);
  const assembled = family.alternativeCardAnimationFamily(classId, { weaponProfile: profiles.weaponProfile, smashProfile: profiles.smashProfile });
  const { family: fam, frames } = assembled;
  const height = 464 - fam.frames.ready.bounds[1];
  const readyBody = landmarks.COMBAT_BODY_LANDMARKS[fam.frames.ready.path];
  const readyDrawHeight = fam.frames.ready.size?.[1] || 512;
  const bodyHeight = readyBody ? (readyBody.feetY - readyBody.headY) * readyDrawHeight / readyBody.height : height;
  const rows = {};
  for (const [name, frame] of Object.entries(frames)) {
    const offset = frame.drawOffset ? frame.drawOffset : [128, 0];
    const dimensions = frame.drawOffset ? frame.size : [512, 512];
    // The stage's canvas is padded (leftPadding, topPadding); the placement below is in the stage's own units (anchor x 384, floor y 480), padding cancelled.
    const authored = { left: offset[0], top: 16 + offset[1], width: dimensions[0], height: dimensions[1], anchorX: 384 };
    const marks = landmarks.COMBAT_BODY_LANDMARKS[frame.path];
    const held = marks ? bodyModel.bodyPlacement(authored, marks, bodyHeight, 480) : authored;
    rows[name] = { authored, held: { left: held.left, top: held.top, width: held.width, height: held.height }, landmarks: marks ?? null };
  }
  placements[classId] = { unit: height, bodyHeight, frames: rows };
}
write('placement.json', { classes: placements });
writeFileSync(join(OUT, 'index.json'), json(index));
console.log(`oracle-core2-stage: ${index.files.length} files, ${(index.files.reduce((n, f) => n + f.bytes, 0) / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/stage`);
