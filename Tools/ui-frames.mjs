#!/usr/bin/env node
// UI chrome sprites (VP-11, D-600): renders the web game's panel/button chrome to small 9-slice PNGs for UI Toolkit,
// which has no box-shadow, border-image or gradient. Output: Unity/Assets/Game/Presentation/UI/Resources/UI/Frames/*.png
// plus .meta files whose spriteBorder is the 9-slice (Unity uses a Sprite's border as the slice).
//
//   node Tools/ui-frames.mjs [--web /home/user/cehinds/ashenspire]   regenerate (needs Playwright Chromium)
//
// Sources (web repo, pinned b8cfe4fac):
//   assets-mobile/player-polish/ui/components/{panel-frame,button-neutral,button-primary-*,divider-ornate}.svg
//   styles/player-polish.css:30 frame border-image slice 24 (SVG 360x240) drawn 12 px wide: 1 SVG unit = 0.5 ref px
//   styles/player-polish.css:49 button face gradient linear-gradient(#ffffff08, transparent 40%, #00000024), radius 3..8
//   styles/kit.css:451 modal shadow 0 24px 72px rgba(0,0,0,.69) + inset 0 1px 0 gold .09
//   styles/kit.css:693 tooltip shadow 0 8px 22px rgba(0,0,0,.55)
// Texel density: frames and plates are drawn 2 texels per reference px (USS -unity-slice-scale: 0.5px); the soft shadows
// are rendered at half scale, so 1 texel = 2 reference px (-unity-slice-scale: 2px). Both scales are tokens.
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = join(ROOT, 'Unity', 'Assets', 'Game', 'Presentation', 'UI', 'Resources', 'UI', 'Frames');
const webArg = process.argv.indexOf('--web');
const WEB = webArg > 0 ? process.argv[webArg + 1] : '/home/user/cehinds/ashenspire';
const COMPONENTS = join(WEB, 'assets-mobile', 'player-polish', 'ui', 'components');

const GOLD_TOP_LINE = 'rgba(201,162,39,0.09)'; // tokens color.modalTopLine

const svg = (name) => readFileSync(join(COMPONENTS, `${name}.svg`), 'utf8');
const withTopLine = (s) => s.replace('</svg>', `<rect x="2" y="2" width="356" height="2" fill="${GOLD_TOP_LINE}"/></svg>`);
// An SVG laid out at its own pixel size.
const svgPage = (s) => `<html><body style="margin:0;background:transparent">${s}</body></html>`;

// borders are [left, bottom, right, top] texels as Unity writes spriteBorder (x, y, z, w).
const jobs = [
  { file: 'modal-frame', html: svgPage(withTopLine(svg('panel-frame'))), w: 360, h: 240, scale: 1, border: [24, 24, 24, 24] },
  { file: 'panel-frame', html: svgPage(svg('panel-frame')), w: 360, h: 240, scale: 1, border: [24, 24, 24, 24] },
  { file: 'plate-neutral', html: svgPage(svg('button-neutral')), w: 280, h: 56, scale: 1, border: [12, 12, 12, 12] },
  // Highlight of the neutral plate (the web has no neutral hover plate; gold stroke and the raised panel fill, kit.css:90-101).
  { file: 'plate-hover', html: svgPage(svg('button-neutral').replaceAll('#a58a52', '#c9a227').replace('fill="#171310"', 'fill="#241d15"')), w: 280, h: 56, scale: 1, border: [12, 12, 12, 12] },
  { file: 'plate-ready', html: svgPage(svg('button-primary-ready')), w: 280, h: 56, scale: 1, border: [12, 12, 12, 12] },
  { file: 'plate-focus', html: svgPage(svg('button-primary-focus')), w: 280, h: 56, scale: 1, border: [12, 12, 12, 12] },
  { file: 'plate-pressed', html: svgPage(svg('button-primary-pressed')), w: 280, h: 56, scale: 1, border: [12, 12, 12, 12] },
  { file: 'divider-ornate', html: svgPage(svg('divider-ornate').replace('width="480" height="20"', 'width="480" height="20"')), w: 480, h: 20, scale: 1, border: [0, 0, 0, 0] },
  // Button face: the web's neutral gradient on an 8 px radius, 24 ref px square at 2 texels per px.
  {
    file: 'button-bevel',
    html: '<html><body style="margin:0;background:transparent"><div style="width:24px;height:24px;border-radius:8px;background-image:linear-gradient(#ffffff08,transparent 40%,#00000024)"></div></body></html>',
    w: 24, h: 24, scale: 2, border: [16, 16, 16, 16],
  },
  shadow('modal-shadow', 72, 48, 96, '0 24px 72px rgba(0,0,0,0.69)'),
  shadow('tooltip-shadow', 22, 14, 30, '0 8px 22px rgba(0,0,0,0.55)'),
];

// A drop shadow around a 64 px box with the box itself cut out (CSS never paints a shadow under its own box).
// Rendered at half scale, so the sprite borders are the reach / 2 and the hole is the 9-slice middle.
function shadow(file, side, top, bottom, spec) {
  const box = 64;
  const w = side + box + side;
  const h = top + box + bottom;
  const hole = `M0 0H${w}V${h}H0Z M${side} ${top}H${side + box}V${top + box}H${side}Z`;
  const html = `<html><body style="margin:0;background:transparent"><div style="transform:scale(.5);transform-origin:0 0;position:relative;width:${w}px;height:${h}px;clip-path:path(evenodd,'${hole}')">` +
    `<div style="position:absolute;left:${side}px;top:${top}px;width:${box}px;height:${box}px;box-shadow:${spec}"></div></div></body></html>`;
  return { file, html, w: w / 2, h: h / 2, scale: 1, border: [side / 2, bottom / 2, side / 2, top / 2] };
}

function meta(guid, name, w, h, [x, y, z, wb]) {
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

import { createHash } from 'node:crypto';
const hex = (s) => createHash('md5').update(`ashen-ui-frame:${s}`).digest('hex');
const spriteGuid = (name) => hex(name).slice(0, 31) + '0';
// Deterministic positive 63-bit internalID per sprite name.
const spriteId = (name) => (BigInt('0x' + hex(`id:${name}`).slice(0, 15)) + 1n).toString();

async function main() {
  if (!existsSync(COMPONENTS)) throw new Error(`web components not found: ${COMPONENTS} (use --web <path>)`);
  mkdirSync(OUT, { recursive: true });
  const { chromium } = createRequire(import.meta.url)('playwright');
  const browser = await chromium.launch();
  for (const j of jobs) {
    const page = await browser.newPage({ viewport: { width: Math.ceil(j.w), height: Math.ceil(j.h) }, deviceScaleFactor: j.scale });
    await page.setContent(j.html);
    const buf = await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: j.w, height: j.h } });
    await page.close();
    const png = join(OUT, `${j.file}.png`);
    writeFileSync(png, buf);
    const width = j.w * j.scale;
    const height = j.h * j.scale;
    const metaPath = `${png}.meta`;
    const guid = existsSync(metaPath) ? readFileSync(metaPath, 'utf8').match(/^guid: (\w+)/m)[1] : hex(`file:${j.file}`);
    writeFileSync(metaPath, meta(guid, j.file, width, height, j.border));
    console.log(`${j.file}.png ${width}x${height} ${buf.length} bytes border ${j.border.join(',')}`);
  }
  await browser.close();
}

main().catch((e) => { console.error(e); process.exit(1); });
