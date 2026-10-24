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

async function build() {
  const { layeredCardArtCatalog: catalog } = await webImport('src/content/layeredCardArt.generated.js');
  const { ACTION_SIGILS } = await webImport('src/content/combatSigils.js');
  const { shapes: shapesOf, aliases, traits } = tagShapes();
  const SHAPES = shapesOf(ACTION_SIGILS.counter.shape);

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
    prefixes: { trait: 'trait:' },
    cutouts: {
      selection: {
        ...selectionKeys(catalog.selection),
        magicalCode: { melee: 'Y0{digit}', projectile: 'Z0{digit}' },
      },
      background: 'background',
      compositions,
    },
    // The cost icons of the banner (cardframe.<name>): a card cost kind takes the icon of its frame; a second non-mana cost takes the overflow one.
    cost: { frame: { actions: 'stamina', stamina: 'stamina', mana: 'mana' }, overflow: 'energy', secondary: 'mana' },
    // Text auto-fit (CARD_COMPONENTS.template title/rules layers): design px, the face shrinks the text from max toward min until it fits.
    fit: {
      title: { max: round(32.96296296296296), min: round(22.273333333333333), lines: 1 },
      rules: { max: 31, min: 22, lines: 3 },
    },
    band: {
      maneuver: { attack: 'attack', smash: 'smash', sweep: 'sweep', counter: 'counter', defend: 'defense', evade: 'evade' },
      attackManeuver: { casting: 'spell', ranged: 'ranged' },
      type: { attack: 'attack', skill: 'skill', power: 'power', status: 'status' },
      fallback: 'skill',
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
      fallback: 'skill',
      glyphs: tagKeys,
    },
    glyphs: {
      sigil: Object.keys(ACTION_SIGILS),
      tag: tagKeys,
    },
  };
  return { json, ACTION_SIGILS, tagShape, tagKeys, actionBaseFrame: await actionFrame() };
}

async function actionFrame() {
  const text = readFileSync(join(WEB, 'src/ui/components/illustratedCard.js'), 'utf8');
  const m = text.match(/const actionBaseFrame = `(<svg[\s\S]*?<\/svg>)`;/);
  if (!m) throw new Error('card-face: actionBaseFrame not found in illustratedCard.js');
  return m[1].replace('class="card-base-action-frame"', 'width="360" height="540"');
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
  for (const key of built.tagKeys) await render(`tag-${key}`, glyphSvg(built.tagShape(key), true), 64, 64);
  await render('title-fade', titleFade, 4, 119, [0, 0, 0, 0], 2);
  await render('footer-frame', page(built.actionBaseFrame), 360, 540, [0, 0, 0, 0], 2);
  await render('rank-bar', rankBar, RANK_W, Math.ceil(RANK_H), [0, 0, 0, 0], 4);
  await render('glow', glow, 112, 112, [32, 32, 32, 32], 1);

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
  ...Object.keys(built.ACTION_SIGILS).map((id) => `sigil-${id}`), ...built.tagKeys.map((k) => `tag-${k}`),
  'title-fade', 'footer-frame', 'rank-bar', 'glow', 'ring',
];

async function main() {
  if (!existsSync(join(WEB, 'src/content/layeredCardArt.generated.js'))) throw new Error(`web repo not found: ${WEB} (use --web <path>)`);
  const built = await build();
  const text = JSON.stringify(built.json, null, 2) + '\n';
  const target = join(CONTENT, 'ui', 'cardface.json');
  if (CHECK) {
    const drift = !existsSync(target) || readFileSync(target, 'utf8') !== text;
    const missing = files(built).filter((f) => !existsSync(join(OUT, `${f}.png`)) && f !== 'ring');
    if (drift || missing.length) { console.error(`card-face: ${drift ? 'cardface.json differs; ' : ''}${missing.length} sprites missing`); process.exit(1); }
    console.log('card-face: ok');
    return;
  }
  mkdirSync(OUT, { recursive: true });
  writeFileSync(target, text);
  console.log(`ui/cardface.json ${text.length} bytes, ${Object.keys(built.json.cutouts.compositions).length} compositions`);
  for (const line of await sprites(built)) console.log(line);
}

main().catch((e) => { console.error(e); process.exit(1); });
