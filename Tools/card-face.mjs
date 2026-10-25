#!/usr/bin/env node
// Card face data and sprites (VP-3, D-650): ports what the web draws around a card's painting into data and small sprites for UI Toolkit,
// which has no SVG, gradients, clip-path, blend modes or text auto-fit.
//
//   node Tools/card-face.mjs [--web /home/user/cehinds/ashenspire] [--check]
//
// Writes (the committed outputs):
//   Unity/Assets/StreamingAssets/Content/ui/cardface.json     the cutout compositions + selection rules, the type band and tag rail rules
//   Unity/Assets/Game/Presentation/UI/Resources/UI/CardFace/  sigil-*.png, tag-*.png (white 64 px marks, USS/C# tint them), title-fade,
//                                                              footer-frame, rank-bar, glow (9-slice) and ring (the frame's bronze ring)
//   Unity/Assets/StreamingAssets/Content/ui/tokens.json      the `face` group: the card face's geometry in design px (360x540) and its colours
// `ring.png` is cut from the imported frame `Assets/Art/Imported/cardframe/base.png` (gitignored art, D-018); without that file the ring is
// skipped with a warning. `--check` compares cardface.json and lists missing sprites without writing.
//
// Web sources (pinned b8cfe4fac):
//   src/content/layeredCardArt.generated.js        cutout selection rules + compositions (src/ui/layeredCardArt.js approvedCardArt)
//   src/content/combatSigils.js                    ACTION_SIGILS, the type band's marks (src/ui/components/combatSigilView.js cardSigilsHtml)
//   src/ui/components/cardTagSymbols.js            SHAPES/ALIASES/SYMBOL_TRAITS and shapeFor (the tag rail's marks)
//   src/ui/components/illustratedCard.js           actionBaseFrame SVG, the layer geometry (CARD_COMPONENTS.template)
//   styles/illustrated-cards.css                   title fade :20-27, rank bar :74-86, footer backdrop :28, glow :39
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const CONTENT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content');
const OUT = join(ROOT, 'Unity', 'Assets', 'Game', 'Presentation', 'UI', 'Resources', 'UI', 'CardFace');
const BASE_PNG = join(ROOT, 'Unity', 'Assets', 'Art', 'Imported', 'cardframe', 'base.png');
const args = process.argv.slice(2);
const webArg = args.indexOf('--web');
const WEB = webArg >= 0 ? args[webArg + 1] : '/home/user/cehinds/ashenspire';
const CHECK = args.includes('--check');

const webImport = (p) => import(pathToFileURL(join(WEB, p)).href);

// ------------------------------------------------------------------ data from the web sources

function tagShapes() {
  const text = readFileSync(join(WEB, 'src/ui/components/cardTagSymbols.js'), 'utf8');
  const take = (re) => { const m = text.match(re); if (!m) throw new Error(`card-face: ${re} not found in cardTagSymbols.js`); return m[1]; };
  const forceShape = take(/const FORCE_SHAPE = '([^']+)'/);
  const bluntShape = take(/const SHAPES_BLUNT = '([^']+)'/);
  const body = take(/const SHAPES = Object\.freeze\(\{([\s\S]*?)\n\}\);/);
  const shapes = new Function('FORCE_SHAPE', 'SHAPES_BLUNT', 'COUNTER_SHAPE', `return {${body}};`);
  const aliases = new Function(`return ${take(/const ALIASES = Object\.freeze\((\{[^}]*\})\)/)};`)();
  const traits = [...take(/const SYMBOL_TRAITS = new Set\(\[([^\]]*)\]\)/).matchAll(/'([^']+)'/g)].map((m) => m[1]);
  return { shapes: (counter) => shapes(forceShape, bluntShape, counter), aliases, traits };
}

const selectionKeys = (s) => ({
  physical: s.physical, melee: s.melee, meleePalettes: s.meleePalettes, mintElements: s.mintElements, projectiles: s.projectiles,
  projectileTags: s.projectileTags, utility: s.utility, spellUtility: s.spellUtility,
});

const round = (n) => Math.round(n * 1e6) / 1e6;

// ------------------------------------------------------------------ motifs (VP-15, src/ui/cardArtLayers.js + cardArtComposition.js)

const CSS_VARS = { '--muted': '#8e826a', '--gold': '#c9a227', '--grace': '#9fc3e8', '--ember': '#e46a44', '--rot': '#b5541c', '--frost': '#7fa8c9' };

/** The motif path bodies, the status tables and the element palettes the web draws over a card's art (cardArtLayersHtml). */
async function motifData() {
  const layers = readFileSync(join(WEB, 'src/ui/cardArtLayers.js'), 'utf8');
  const composition = readFileSync(join(WEB, 'src/ui/cardArtComposition.js'), 'utf8');
  const take = (text, re) => { const m = text.match(re); if (!m) throw new Error(`card-face: ${re} not found`); return m[1]; };
  const paths = new Function(`return {${take(layers, /const MOTIFS = Object\.freeze\(\{([\s\S]*?)\n\}\);/)}};`)();
  const statusMotif = new Function(`return {${take(composition, /const STATUS_MOTIFS = Object\.freeze\(\{([\s\S]*?)\n\}\);/)}};`)();
  const statusColor = new Function(`return ${take(composition, /const STATUS_COLORS = Object\.freeze\((\{[^}]*\})\)/)};`)();
  const alias = new Function(`return ${take(composition, /const ALIASES = Object\.freeze\((\{[^}]*\})\)/)};`)();
  const { CARD_ART_ELEMENTS } = await webImport('src/ui/cardArtComposition.js');
  const elements = Object.fromEntries(Object.entries(CARD_ART_ELEMENTS).map(([id, e]) => [id, { color: e.color, glow: e.glow, motif: e.motif }]));
  // The registry's statuses (a status absent from it draws nothing, cardArtStatuses) and their own tint when it has a plain colour.
  const catalog = JSON.parse(readFileSync(join(CONTENT, 'profile', 'bundle', 'statuses.json'), 'utf8'));
  const statuses = Object.fromEntries(Object.entries(catalog).sort(([a], [b]) => a.localeCompare(b)).map(([id, row]) => {
    const tint = typeof row.tint === 'string' ? (row.tint.startsWith('var(') ? CSS_VARS[row.tint.slice(4, -1)] : row.tint) : undefined;
    return [id, tint ?? ''];
  }));
  return { paths, statusMotif, statusColor, alias, elements, statuses };
}

async function build() {
  const { layeredCardArtCatalog: catalog } = await webImport('src/content/layeredCardArt.generated.js');
  const { ACTION_SIGILS, SCHOOL_SIGILS } = await webImport('src/content/combatSigils.js');
  const { shapes: shapesOf, aliases, traits } = tagShapes();
  const SHAPES = shapesOf(ACTION_SIGILS.counter.shape);
  const motifs = await motifData();

  const space = catalog.selection.coordinateSpace;
  const compositions = {};
  for (const [code, composition] of Object.entries(catalog.compositions)) {
    compositions[code] = composition.layers.map((layer) => {
      const t = layer.transform;
      if (t.anchor.x !== 0.5 || t.anchor.y !== 0.5) throw new Error(`card-face: ${code}.${layer.id} has an anchor other than the centre`);
      const row = {
        id: layer.id,
        asset: layer.assetId,
        x: round(t.x / space.width), y: round(t.y / space.height), w: round(t.width / space.width), h: round(t.height / space.height),
        opacity: round(t.opacity),
      };
      if (t.rotationDeg) row.rotation = round(t.rotationDeg);
      if (t.scaleX !== 1) row.scaleX = round(t.scaleX);
      if (t.scaleY !== 1) row.scaleY = round(t.scaleY);
      if (t.blendMode !== 'normal') row.blend = t.blendMode;
      return row;
    });
  }

  // The tag rail's marks: a key takes the solid action sigil when it has one, else its own shape, else the Skill diamond (shapeFor).
  const solid = Object.fromEntries(Object.entries(ACTION_SIGILS).filter(([, m]) => m.solid).map(([id, m]) => [id, m.shape]));
  const tagKeys = [...new Set([...Object.keys(SHAPES), ...Object.keys(solid)])].sort();
  const tagShape = (key) => solid[key] ?? SHAPES[key];

  const json = {
    comment: 'VP-3 card face data (D-650), generated by Tools/card-face.mjs from the web at b8cfe4fac; do not hand-edit. design is the web component template (CARD_COMPONENTS.template, 360x540) the card face is laid out in; stage is the 512x640 coordinate space of the cutout layers (layeredCardArt.generated.js). cutouts.selection is approvedCardArt (src/ui/layeredCardArt.js) as data: camp+maneuver+element+tags+effect ops pick a composition code, a composition is its layers (centre x/y and w/h as fractions of the stage, rotation in degrees, blend is the web CSS mix-blend-mode, which USS cannot draw: such a layer is drawn normally). band maps a card to the type band mark and word (combatSigilView.js cardSigilsHtml). rail maps a card to the tag symbols down its right edge (cardTagSymbols.js cardSideTags, reduced: damage types and bonus counts need data the Unity card does not carry). glyphs lists the sprites in Resources/UI/CardFace.',
    design: { width: 360, height: 540 },
    stage: { width: space.width, height: space.height },
    prefixes: { trait: 'trait:', damage: 'damage:', status: 'status:', range: 'range:' },
    cutouts: {
      selection: {
        ...selectionKeys(catalog.selection),
        magicalCode: { melee: 'Y0{digit}', projectile: 'Z0{digit}' },
      },
      background: 'background',
      compositions,
    },
    // The cost icons of the banner (cardframe.<name>): a card cost kind takes the icon of its frame; a second non-mana cost takes the overflow one.
    inspectIcon: 'info',
    cost: { frame: { actions: 'stamina', stamina: 'stamina', mana: 'mana' }, overflow: 'energy', secondary: 'mana' },
    // Text auto-fit (CARD_COMPONENTS.template title/rules layers): design px, the face shrinks the text from max toward min until it fits.
    fit: {
      title: { max: round(32.96296296296296), min: round(22.273333333333333), lines: 1 },
      rules: { max: 31, min: 22, lines: 3 },
    },
    band: {
      maneuver: { attack: 'attack', smash: 'smash', sweep: 'sweep', counter: 'counter', defend: 'defense', evade: 'evade' },
      type: { attack: 'attack', skill: 'skill', power: 'power', status: 'status' },
      fallback: 'skill',
      // cardPreparationCategory: a Power first, then a Spell (explicit by tag or type, or a spell-camp attack/skill); a Spell prints the Spell mark.
      power: { types: ['power'], tags: ['classification.power'] },
      spell: { id: 'spell', tags: ['classification.spell', 'source:spell'], types: ['spell'], campTypes: ['attack', 'skill'], campTags: ['classification.attack', 'classification.skill'] },
      // cardSigilIdentity: a Spell that mirrors a basic card shows the Spell mark then that card's mark and its word; Technique wears the Skill mark.
      mirror: { exclude: ['spell'], technique: { id: 'technique', sigil: 'skill', word: 'technique' } },
    },
    rail: {
      max: 5,
      statusMax: 3,
      category: { power: 'power', skill: 'skill' },
      categoryExcludeCamp: { skill: 'spell' },
      camp: { physical: 'guard', spell: 'spell' },
      campWithDamage: 'physical',
      maneuver: { evade: 'evade', smash: 'smash' },
      evadeManeuver: 'evade',
      evadeOp: 'dodgeRoll',
      damageOp: 'damage',
      statusOps: ['applyStatus', 'buildup'],
      traits,
      alias: aliases,
      // shapeFor: these keys take their mark before the alias table does (a physical hit reads as Guard, slashing as the Attack swords).
      damageAlias: { physical: 'guard', evasion: 'evade', dodge: 'evade', slashing: 'attack', blade: 'attack' },
      // cardBonusTags: a self Precision or Evade grant (and a stance entry's) is a counted tag; a numbered trait takes its value.
      benefit: { op: 'applyStatus', target: 'self', statuses: ['precision', 'evade'], precision: 'precision', evade: 'evade' },
      smashManeuver: 'smash',
      fallback: 'skill',
      glyphs: tagKeys,
    },
    glyphs: {
      sigil: Object.keys(ACTION_SIGILS),
      tag: tagKeys,
      motif: Object.keys(motifs.paths),
      school: Object.keys(SCHOOL_SIGILS),
    },
    // The card inspector's rule hints (card.js cardTooltip): which sigils are stances, the ranges a defence covers in order, and the numbers its rules turn on.
    hints: {
      counterManeuver: 'counter', sweepManeuver: 'sweep', defenseSigil: 'defense', evadeSigil: 'evade', sweepSigil: 'sweep', sweepDefensiveRank: 2, returnRulesVersion: 2,
      reactionTag: 'trait:reaction',
      stanceSigils: ['attack', 'smash', 'sweep', 'counter', 'defense', 'evade'],
      ranges: ['melee', 'reach', 'ranged'],
      schoolAlias: { earth: 'alteration' },
    },
    // The line-art motifs and colour wash over the art of a library or composed card (cardArtLayersHtml). Colours are hex; a status takes its
    // registry tint (when plain) over its motif's colour. Strokes and opacities are the web's (.55/.34 for an element, .8/.42 for a status).
    motifs: {
      element: { opacity: 0.34, stroke: 0.55 },
      status: { opacity: 0.42, stroke: 0.8, offset: 7, offsetFrom: 2, max: 3 },
      elementAlias: motifs.alias,
      elements: motifs.elements,
      defaultElement: 'force',
      statusMotif: motifs.statusMotif,
      statusDefault: 'stars',
      statusColor: motifs.statusColor,
      statuses: motifs.statuses,
    },
  };
  return { json, motifs, ACTION_SIGILS, SCHOOL_SIGILS, tagShape, tagKeys, actionBaseFrame: await actionFrame() };
}

async function actionFrame() {
  const text = readFileSync(join(WEB, 'src/ui/components/illustratedCard.js'), 'utf8');
  const m = text.match(/const actionBaseFrame = `(<svg[\s\S]*?<\/svg>)`;/);
  if (!m) throw new Error('card-face: actionBaseFrame not found in illustratedCard.js');
  return m[1].replace('class="card-base-action-frame"', 'width="360" height="540"');
}

// ------------------------------------------------------------------ tokens (the face group of ui/tokens.json)

const TOKENS = join(CONTENT, 'ui', 'tokens.json');
const r2 = (n) => Math.round(n * 100) / 100;

async function faceTokens() {
  const { CARD_COMPONENTS } = await webImport('src/content/cardComponents.generated.js');
  const doc = CARD_COMPONENTS.template;
  const layer = (id) => doc.layers.find((l) => l.id === id);
  const lift = doc.height * 0.061; // illustratedCardHtml: the action band raises the parchment, rules and art
  const t = { w: doc.width, h: doc.height };
  const box = (p, x, y, w, h) => { t[`${p}X`] = r2(x); t[`${p}Y`] = r2(y); t[`${p}W`] = r2(w); t[`${p}H`] = r2(h); };
  // A trimmed image layer: the image is drawn bigger than its box and shifted so the trim rectangle fills it (illustratedCardHtml crop).
  const crop = (p, l, w, h) => {
    const [x, y, tw, th] = l.trim;
    t[`${p}CropW`] = r2(w * l.imageWidth / tw); t[`${p}CropH`] = r2(h * l.imageHeight / th);
    t[`${p}CropX`] = r2(-w * x / tw); t[`${p}CropY`] = r2(-h * y / th);
  };
  const text = (p, l, dy = 0) => { box(p, l.x, l.y + dy, l.w, l.h); t[`${p}Size`] = r2(l.fontSize); };

  box('base', 0, 0, doc.width, doc.height); crop('base', layer('base'), doc.width, doc.height);
  const art = layer('art');
  box('art', art.x, art.y, art.w, art.h - lift);
  // The cutout stage (cardArtLayers componentLayersHtml): min(100cqw,80cqh) by min(125cqw,100cqh) in the art window, centred.
  t.stageW = r2(Math.min(art.w, (art.h - lift) * 0.8)); t.stageH = r2(Math.min(art.w * 1.25, art.h - lift));
  const panel = layer('panel');
  box('panel', panel.x, panel.y - lift, panel.w, panel.h); crop('panel', panel, panel.w, panel.h);
  const flag = layer('flag');
  const short = doc.costLayouts.staminaOnly.layers.flag, tall = doc.costLayouts.staminaMana.layers.flag;
  box('flag', flag.x, flag.y, flag.w, short.h); crop('flag', flag, flag.w, short.h);
  t.flagTallH = r2(tall.h);
  t.flagTallCropH = r2(tall.h * flag.imageHeight / flag.trim[3]); t.flagTallCropY = r2(-tall.h * flag.trim[1] / flag.trim[3]);
  for (const [name, iconId, valueId] of [['stamina', 'stamina-icon', 'stamina-value'], ['mana', 'mana-icon', 'mana-value'], ['energy', 'energy-icon', 'energy-value']]) {
    const icon = layer(iconId);
    box(`${name}Icon`, icon.x, icon.y, icon.w, icon.h); crop(`${name}Icon`, icon, icon.w, icon.h);
    text(`${name}Value`, layer(valueId));
  }
  text('title', layer('title'));
  text('rules', layer('rules'), -lift);
  box('band', doc.width * 0.1, doc.height * 0.858, doc.width * 0.8, doc.height * 0.116);
  const bar = doc.layouts[doc.defaultLayout]['rank-bar'], word = doc.layouts[doc.defaultLayout]['rank-text'];
  box('rank', bar.x, bar.y, bar.w, bar.h);
  t.rankTextX = r2(word.x - bar.x); t.rankTextY = r2(word.y - bar.y); t.rankTextW = r2(word.w); t.rankTextH = r2(word.h);
  t.rankSize = r2(word.h / 1.15); // illustratedCardHtml: font-size r.h * text.h% / 1.15
  const cqw = doc.width / 100; // the web sizes the face's own marks in container-query widths
  Object.assign(t, {
    bandIcon: r2(11.5 * cqw), bandGap: r2(4 * cqw), bandPadX: r2(3 * cqw), bandPadBottom: r2(0.6 * cqw), bandSize: r2(7 * cqw), bandTrack: r2(0.085 * 7 * cqw),
    fadeX: r2(doc.width * 0.06), fadeY: r2(doc.height * 0.035), fadeW: r2(doc.width * 0.88), fadeH: r2(doc.height * 0.22), fadeRadius: r2(3 * cqw),
    footerX: r2(doc.width * 0.06), footerY: r2(doc.height * 0.853), footerW: r2(doc.width * 0.88), footerH: r2(doc.height * 0.113),
    railRight: r2(doc.width * 0.09), railTop: r2(doc.height * 0.16), railMark: r2(8 * cqw), railGap: r2(2 * cqw),
    noteX: panel.x, noteW: panel.w, noteBottom: r2(doc.height - (panel.y - lift)), noteSize: 22, notePad: 6, noteGap: 4,
    nameOutline: r2(0.65 * cqw / 2), nameShadowY: r2(0.6 * cqw), nameShadowBlur: r2(0.8 * cqw), costOutline: 1,
    glowOut: -32, glowSlice: 1, infoSize: 28, infoX: r2((doc.width - 28) / 2), infoY: -42,
    nameColor: layer('title').color, rulesColor: layer('rules').color, costColor: layer('stamina-value').color, costOutlineColor: layer('stamina-value').outline,
    nameOutlineColor: '#100b06', bandColor: '#efd09a', rankColor: '#f3f6ff', railColor: '#e8bd78', inkColor: '#000000',
    // illustrated-cards.css:74 .card-tag-count: a pill at the symbol's lower right; the band's second mark sits after the first (the gap is the band's).
    countSize: r2(6 * cqw), countRight: r2(-1.6 * cqw), countBottom: r2(-1.6 * cqw), countPadX: r2(0.9 * cqw), countRadius: r2(3 * cqw), countBorder: r2(0.35 * cqw), countFont: r2(5.2 * cqw),
    countBorderColor: '#e8bd78', countBack: '#16110e', countColor: '#fff0cc',
    glowColor: '#c49b56', glowArmedColor: '#5b9dff', glowChosenColor: '#d4622e', dimColor: '#a6a6a6',
    noteBack: 'rgba(21, 17, 15, 0.88)', footerBack: 'rgba(16, 13, 9, 0.38)', infoBack: '#15110f', infoLine: '#c9a227',
  });
  return t;
}

function tokensBlock(t) {
  const body = Object.entries(t).map(([k, v]) => `    ${JSON.stringify(k)}: ${typeof v === 'number' ? v : JSON.stringify(v)}`).join(',\n');
  return `,\n  "face": {\n${body}\n  }\n}\n`;
}

function spliceTokens(text, t) {
  const block = tokensBlock(t);
  const existing = /,\n  "face": \{[\s\S]*?\n  \}\n\}\n$/;
  if (existing.test(text)) return text.replace(existing, () => block);
  if (!/\n\}\n$/.test(text)) throw new Error('card-face: ui/tokens.json does not end with a closing brace');
  return text.replace(/\n\}\n$/, () => block);
}

// ------------------------------------------------------------------ sprites

const hex = (s) => createHash('md5').update(`ashen-card-face:${s}`).digest('hex');
const spriteGuid = (name) => hex(name).slice(0, 31) + '0';
const spriteId = (name) => (BigInt('0x' + hex(`id:${name}`).slice(0, 15)) + 1n).toString();

function meta(guid, name, w, h, [x, y, z, wb]) {
  // Same importer settings as Resources/UI/Frames (D-600): a single Sprite, bilinear, no mips, uncompressed.
  return `fileFormatVersion: 2
guid: ${guid}
TextureImporter:
  internalIDToNameTable:
  - first:
      213: ${spriteId(name)}
    second: ${name}
  externalObjects: {}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 1
  spriteExtrude: 1
  spriteMeshType: 0
  alignment: 0
  spritePivot: {x: 0.5, y: 0.5}
  spritePixelsToUnits: 100
  spriteBorder: {x: ${x}, y: ${y}, z: ${z}, w: ${wb}}
  spriteGenerateFallbackPhysicsShape: 0
  alphaUsage: 1
  alphaIsTransparency: 1
  spriteTessellationMethod: 0
  spriteTessellationDetail: -1
  spriteGeometrySubdivision: -1
  textureType: 8
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: 4
    textureCompression: 0
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites:
    - serializedVersion: 2
      name: ${name}
      rect:
        serializedVersion: 2
        x: 0
        y: 0
        width: ${w}
        height: ${h}
      alignment: 0
      pivot: {x: 0.5, y: 0.5}
      border: {x: ${x}, y: ${y}, z: ${z}, w: ${wb}}
      customData:
      outline: []
      physicsShape: []
      tessellationDetail: -1
      bones: []
      spriteID: ${spriteGuid(name)}
      internalID: ${spriteId(name)}
      vertices: []
      indices:
      edges: []
      weights: []
    outline: []
    customData:
    physicsShape: []
    bones: []
    spriteID: ${spriteGuid(name + '-sheet')}
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable:
      ${name}: ${spriteId(name)}
  mipmapLimitGroupName:
  pSDRemoveMatte: 0
  userData:
  assetBundleName:
  assetBundleVariant:
`;
}

const page = (body) => `<html><body style="margin:0;background:transparent">${body}</body></html>`;
const glyphSvg = (shape, solidMark) => page(solidMark
  ? `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="64" height="64" fill="#fff" fill-rule="evenodd">${shape}</svg>`
  : `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="64" height="64" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${shape}</svg>`);

// illustrated-cards.css:20-27 .card-title-fade: 22% of the card tall, #15110f f5 to 48%, b3 at 66%, clear at the bottom.
const titleFade = page('<div style="width:4px;height:119px;background:linear-gradient(to bottom,#15110ff5 0%,#15110ff5 48%,#15110fb3 66%,#15110f00 100%)"></div>');

// illustrated-cards.css:74-86 .card-rank-bar: a hexagon (7% points) in a light-blue bevel gradient with an inset navy gem (.65cqw = 2.34 px).
const RANK_W = 138;
const RANK_H = 26.3;
const rankBar = page(`<div style="position:relative;width:${RANK_W}px;height:${RANK_H}px">
<div style="position:absolute;inset:0;background:linear-gradient(160deg,#c0dcee,#6e94bf 40%,#244968 60%,#88b6d1);clip-path:polygon(7% 0,93% 0,100% 50%,93% 100%,7% 100%,0 50%)"></div>
<div style="position:absolute;inset:2.34px;clip-path:polygon(6% 0,94% 0,100% 50%,94% 100%,6% 100%,0 50%);background:linear-gradient(160deg,#6597c1 0%,#2c5d91 24%,#123a6a 48%,#092448 52%,#1b4d7b 83%,#407fa9 100%);box-shadow:inset 0 2.9px 3.6px #89bdde50,inset 0 -2.2px 2.9px #020e25"></div></div>`);

// illustrated-cards.css:39 drop-shadow(0 0 3px) glow: a white soft ring around a 48 px box, the box itself clear (9-slice, border 32).
const glow = page('<div style="position:relative;width:112px;height:112px"><div style="position:absolute;left:32px;top:32px;width:48px;height:48px;border-radius:10px;box-shadow:0 0 0 2px #fff,0 0 12px 4px #ffffffb0;clip-path:path(evenodd,\'M0 0H112V112H0Z M32 32H80V80H32Z\')"></div></div>');

const ART_W = 340;
const ART_H = 336;
const motifSvg = (body, stroke) => page(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" preserveAspectRatio="none" width="${ART_W}" height="${ART_H}" fill="none" stroke="#fff" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`);
// cardArtLayersHtml: the element's two soft washes (15% 65% and 85% 30%), white at the web's 14% and 11% and tinted by the face.
const wash = page(`<div style="width:${ART_W}px;height:${ART_H}px;background:radial-gradient(ellipse at 15% 65%,#ffffff24,transparent 42%),radial-gradient(ellipse at 85% 30%,#ffffff1c,transparent 40%)"></div>`);

async function sprites(built) {
  if (CHECK) return [];
  const { chromium } = createRequire(import.meta.url)('playwright');
  const browser = await chromium.launch();
  const written = [];
  const render = async (file, html, w, h, border = [0, 0, 0, 0], scale = 1) => {
    const p = await browser.newPage({ viewport: { width: Math.ceil(w), height: Math.ceil(h) }, deviceScaleFactor: scale });
    await p.setContent(html);
    const buf = await p.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: w, height: h } });
    await p.close();
    const png = join(OUT, `${file}.png`);
    writeFileSync(png, buf);
    const metaPath = `${png}.meta`;
    const guid = existsSync(metaPath) ? readFileSync(metaPath, 'utf8').match(/^guid: (\w+)/m)[1] : hex(`file:${file}`);
    writeFileSync(metaPath, meta(guid, file, Math.round(w * scale), Math.round(h * scale), border));
    written.push(`${file}.png ${Math.round(w * scale)}x${Math.round(h * scale)} ${buf.length} bytes`);
  };

  for (const [id, mark] of Object.entries(built.ACTION_SIGILS)) await render(`sigil-${id}`, glyphSvg(mark.shape, mark.solid), 64, 64);
  for (const [id, mark] of Object.entries(built.SCHOOL_SIGILS)) await render(`school-${id}`, glyphSvg(mark.shape, mark.solid), 64, 64);
  for (const key of built.tagKeys) await render(`tag-${key}`, glyphSvg(built.tagShape(key), true), 64, 64);
  await render('title-fade', titleFade, 4, 119, [0, 0, 0, 0], 2);
  await render('footer-frame', page(built.actionBaseFrame), 360, 540, [0, 0, 0, 0], 2);
  await render('rank-bar', rankBar, RANK_W, Math.ceil(RANK_H), [0, 0, 0, 0], 4);
  await render('glow', glow, 112, 112, [32, 32, 32, 32], 1);
  // cardArtLayersHtml: each motif stretched over the 340x336 art window (viewBox 100, preserveAspectRatio none), white here and tinted by the face.
  for (const [id, body] of Object.entries(built.motifs.paths)) {
    for (const [suffix, stroke] of [['e', built.json.motifs.element.stroke], ['s', built.json.motifs.status.stroke]])
      await render(`motif-${id}-${suffix}`, motifSvg(body, stroke), ART_W, ART_H, [0, 0, 0, 0], 2);
  }
  await render('wash', wash, ART_W, ART_H, [0, 0, 0, 0], 1);

  if (existsSync(BASE_PNG)) {
    // illustratedCardHtml: the base layer is the trimmed image over the 360x540 box, and its 'trim' plane is the part outside the
    // interior octagon (interiors.base, evenodd). Drawn over the painting, it is the bronze ring and the corner flourishes only.
    const [tx, ty, tw, th] = [69, 102, 887, 1327];
    const [iw, ih] = [1024, 1536];
    const inside = '6% 10%,12% 5%,88% 5%,94% 10%,94% 90%,88% 96%,12% 96%,6% 90%';
    const outside = `evenodd,0% 0%,100% 0%,100% 100%,0% 100%,0% 0%,${inside},${inside.split(',')[0]}`;
    const html = page(`<div style="position:relative;width:360px;height:540px;clip-path:polygon(${outside});overflow:hidden"><img src="data:image/png;base64,${readFileSync(BASE_PNG).toString('base64')}" style="position:absolute;max-width:none;width:${iw / tw * 100}%;height:${ih / th * 100}%;left:${-tx / tw * 100}%;top:${-ty / th * 100}%"></div>`);
    await render('ring', html, 360, 540, [0, 0, 0, 0], 1);
  } else console.warn(`card-face: ${BASE_PNG} is missing: import the art (D-018) to cut ring.png`);
  await browser.close();
  return written;
}

const files = (built) => [
  ...Object.keys(built.ACTION_SIGILS).map((id) => `sigil-${id}`), ...Object.keys(built.SCHOOL_SIGILS).map((id) => `school-${id}`), ...built.tagKeys.map((k) => `tag-${k}`),
  'title-fade', 'footer-frame', 'rank-bar', 'glow', 'ring', 'wash',
  ...Object.keys(built.motifs.paths).flatMap((id) => [`motif-${id}-e`, `motif-${id}-s`]),
];

async function main() {
  if (!existsSync(join(WEB, 'src/content/layeredCardArt.generated.js'))) throw new Error(`web repo not found: ${WEB} (use --web <path>)`);
  const built = await build();
  const tokensText = readFileSync(TOKENS, 'utf8');
  const tokensNext = spliceTokens(tokensText, await faceTokens());
  const text = JSON.stringify(built.json, null, 2) + '\n';
  const target = join(CONTENT, 'ui', 'cardface.json');
  if (CHECK) {
    const drift = !existsSync(target) || readFileSync(target, 'utf8') !== text || tokensNext !== tokensText;
    const missing = files(built).filter((f) => !existsSync(join(OUT, `${f}.png`)) && f !== 'ring');
    if (drift || missing.length) { console.error(`card-face: ${drift ? 'cardface.json or the face tokens differ; ' : ''}${missing.length} sprites missing`); process.exit(1); }
    console.log('card-face: ok');
    return;
  }
  mkdirSync(OUT, { recursive: true });
  writeFileSync(target, text);
  writeFileSync(TOKENS, tokensNext);
  console.log(`ui/cardface.json ${text.length} bytes, ${Object.keys(built.json.cutouts.compositions).length} compositions`);
  for (const line of await sprites(built)) console.log(line);
}

main().catch((e) => { console.error(e); process.exit(1); });
