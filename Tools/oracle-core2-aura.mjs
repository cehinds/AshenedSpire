#!/usr/bin/env node
// Core 2 aura oracle (P-4, docs/DECISIONS.md D-1540..). Records what the web's silhouette glow does:
//
//   aura.json   auraFilter (src/ui/combatAura.js) over a matrix of frames, rests, paid resources and active clips, as the stage calls it (alternativeCardStage.js filterFor: a spell names the
//               power frame, a defend and a counter rest are the guard rest), parsed with the renderer's own reader (alternativeAuraRenderer.js alternativeAuraLayers) into the shadows and the
//               brightness it draws; and resourceAura over costs, self-payments and receipts.
//
//   node Tools/oracle-core2-aura.mjs [--source <web checkout>]
//
// Deterministic. Writes Unity/Assets/Tests/Oracle/core2/aura/*.json (and the .meta files Unity wants).
import { writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'aura');
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const w = await loadWeb();
const [aura, renderer] = await Promise.all([load('src/ui/combatAura.js'), load('src/ui/alternativeAuraRenderer.js')]);
mkdirSync(OUT, { recursive: true });
const md5 = (s) => createHash('md5').update(`ashen-oracle:${s}`).digest('hex');
const meta = (guid) => `fileFormatVersion: 2\nguid: ${guid}\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n`;
const folderMeta = (guid) => `fileFormatVersion: 2\nguid: ${guid}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n`;
if (!existsSync(`${OUT}.meta`)) writeFileSync(`${OUT}.meta`, folderMeta(md5('core2/aura')));

const layersOf = (filter) => {
  const parsed = renderer.alternativeAuraLayers(filter);
  if (!parsed) return { glows: [], brightness: 1 };
  const glows = [];
  for (let i = 0; i + 3 <= parsed.shadows.length; i += 3) {
    const [side, other, below] = parsed.shadows.slice(i, i + 3);
    const m = /rgba\((\d+),(\d+),(\d+),([\d.]+)\)/.exec(side.color);
    glows.push({ radius: side.x, mirror: other.x, belowY: below.y, blur: below.blur, sideBlur: side.blur, rgb: [Number(m[1]), Number(m[2]), Number(m[3])], alpha: Number(m[4]) });
  }
  return { glows, brightness: parsed.brightness };
};

// The stage's call (filterFor): the frame is the pose on show, a spell is the power frame, a braced rest is the guard rest, a clip makes it active.
const stageFilter = (pose, action, rest, resources) =>
  aura.auraFilter(action === 'spell' ? 'power2' : pose, ['defend', 'counter'].includes(rest) ? 'guard' : rest, resources, !!action);

const poses = ['ready', 'stance-offensive', 'stance-defensive', 'attack-contact', 'cast-load', 'guard-brace', 'power1', 'power2', 'power3', 'defeated'];
const actions = [null, 'attack', 'smash', 'sweep', 'counter', 'defend', 'spell', 'evade'];
const rests = ['idle', 'defend', 'counter', 'guard', 'evade', 'defeated'];
const resourceSets = [[], ['stamina'], ['mana'], ['hp'], ['stamina', 'mana'], ['stamina', 'mana', 'hp'], ['power'], ['unknown']];
const stage = [];
for (const pose of poses) for (const action of actions) for (const rest of rests) for (const resources of resourceSets) {
  const filter = stageFilter(pose, action, rest, resources);
  stage.push({ pose, action, rest, resources, filter, ...layersOf(filter) });
}

const costs = [];
for (const staminaCost of [0, 3]) for (const manaCost of [0, 2]) for (const hpCost of [0, 4]) {
  for (const effects of [[], [{ op: 'loseHp', target: 'self', amount: 3 }], [{ op: 'loseHp', target: 'self' }], [{ op: 'loseHp', target: 'self', amount: 0 }], [{ op: 'loseHp', target: 'enemy', amount: 3 }], [{ op: 'damage', target: 'enemy', amount: 6 }]]) {
    for (const receipt of [{}, { staminaSpent: 0 }, { staminaSpent: 5 }, { manaSpent: 0 }, { manaSpent: 2 }, { hpSpent: 0 }, { hpSpent: 5 }]) {
      const card = { staminaCost, manaCost, hpCost, effects };
      costs.push({ card, receipt, resources: aura.resourceAura(card, receipt) });
    }
  }
}

const text = `${JSON.stringify(canon({ webSha: w.head, stage, costs }), null, 1)}\n`;
writeFileSync(join(OUT, 'aura.json'), text);
if (!existsSync(join(OUT, 'aura.json.meta'))) writeFileSync(join(OUT, 'aura.json.meta'), meta(md5('core2/aura/aura.json')));
console.log(`oracle-core2-aura: ${stage.length} stage cases, ${costs.length} cost cases`);
