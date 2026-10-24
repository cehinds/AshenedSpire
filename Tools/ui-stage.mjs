#!/usr/bin/env node
// Combat stage grid (VP-4, D-700): the web's ground grid is a sheared 3 x 4 lattice of faint lines over the battlefield
// (docs/qa/web-screens/combat-1920x1080.jpg: bottom edge x 218..1295, top edge x 437..1514, rows y 383..615 at 1920x1080;
// 390x844: bottom x 5..385, top x 40..385?, rows y 228..497). UI Toolkit draws no skewed lines, so the lattice is rendered once
// to a PNG per layout (Painter2D would need numeric constants in Game C#). Output: Resources/UI/Stage/grid-{wide,narrow}.png (+ .meta),
// laid over an element with the same size (tokens size.boardGrid*) and stretched to it.
//
//   node Tools/ui-stage.mjs          write the PNGs and metas (pure Node, no dependencies)
//   node Tools/ui-stage.mjs --check  fail when a file is missing, has the wrong size or its .meta differs
//
// Sizes below are reference px (1280x720 wide, 430 wide narrow); 2 texels per reference px. The tokens that size the element
// (size.boardGridW/H, size.boardGridWNarrow/HNarrow) must equal W/H here.
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import zlib from 'node:zlib';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = join(ROOT, 'Unity', 'Assets', 'Game', 'Presentation', 'UI', 'Resources', 'UI', 'Stage');
const CHECK = process.argv.includes('--check');

const TEXELS = 2;
const COLUMNS = 3;
const ROWS = 4;
const LINE_ALPHA = 0.3; // web: faint parchment lines, about 25-30% over the dark floor
const LINE_WIDTH = 1; // reference px
const LINE = [253, 246, 230]; // tokens color.parchment

const layouts = [
  { file: 'grid-wide', w: 864, h: 155, shear: 146 },
  { file: 'grid-narrow', w: 424, h: 297, shear: 38 },
];

function raster({ w, h, shear }) {
  const W = w * TEXELS;
  const H = h * TEXELS;
  const S = shear * TEXELS;
  const half = (LINE_WIDTH * TEXELS) / 2;
  const alpha = new Float32Array(W * H);
  const segment = (x0, y0, x1, y1) => {
    const minX = Math.max(0, Math.floor(Math.min(x0, x1) - half - 1));
    const maxX = Math.min(W - 1, Math.ceil(Math.max(x0, x1) + half + 1));
    const minY = Math.max(0, Math.floor(Math.min(y0, y1) - half - 1));
    const maxY = Math.min(H - 1, Math.ceil(Math.max(y0, y1) + half + 1));
    const dx = x1 - x0;
    const dy = y1 - y0;
    const len2 = dx * dx + dy * dy;
    for (let y = minY; y <= maxY; y++) {
      for (let x = minX; x <= maxX; x++) {
        const px = x + 0.5;
        const py = y + 0.5;
        const t = len2 === 0 ? 0 : Math.max(0, Math.min(1, ((px - x0) * dx + (py - y0) * dy) / len2));
        const d = Math.hypot(px - (x0 + t * dx), py - (y0 + t * dy));
        const a = Math.max(0, Math.min(1, half + 0.5 - d));
        if (a > alpha[y * W + x]) alpha[y * W + x] = a;
      }
    }
  };
  const inset = (k) => S * (1 - k / ROWS);
  for (let k = 0; k <= ROWS; k++) {
    const y = Math.min(H - half, Math.max(half, (H * k) / ROWS));
    segment(inset(k), y, inset(k) + (W - S), y);
  }
  for (let c = 0; c <= COLUMNS; c++) {
    const bottom = ((W - S) * c) / COLUMNS;
    const clamp = (x) => Math.min(W - half, Math.max(half, x));
    segment(clamp(bottom), H - half, clamp(bottom + S), half);
  }
  const rows = [];
  for (let y = 0; y < H; y++) {
    const row = Buffer.alloc(1 + W * 4);
    for (let x = 0; x < W; x++) {
      const o = 1 + x * 4;
      row[o] = LINE[0];
      row[o + 1] = LINE[1];
      row[o + 2] = LINE[2];
      row[o + 3] = Math.round(alpha[y * W + x] * LINE_ALPHA * 255);
    }
    rows.push(row);
  }
  return { W, H, data: Buffer.concat(rows) };
}

function png({ W, H, data }) {
  const chunk = (type, body) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(body.length);
    const t = Buffer.from(type, 'ascii');
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(zlib.crc32(Buffer.concat([t, body])) >>> 0);
    return Buffer.concat([len, t, body, crc]);
  };
  const head = Buffer.alloc(13);
  head.writeUInt32BE(W, 0);
  head.writeUInt32BE(H, 4);
  head[8] = 8; // bit depth
  head[9] = 6; // RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', head),
    chunk('IDAT', zlib.deflateSync(data, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

const hex = (s) => createHash('md5').update(`ashen-ui-stage:${s}`).digest('hex');
const spriteGuid = (name) => hex(name).slice(0, 31) + '0';
const spriteId = (name) => (BigInt('0x' + hex(`id:${name}`).slice(0, 15)) + 1n).toString();

// Same importer settings as Resources/UI/Frames (a Sprite, so USS background-image takes it), no 9-slice border.
function meta(guid, name, w, h) {
  const [x, y, z, wb] = [0, 0, 0, 0];
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

let problems = 0;
mkdirSync(OUT, { recursive: true });
for (const l of layouts) {
  const img = raster(l);
  const buf = png(img);
  const file = join(OUT, `${l.file}.png`);
  const metaPath = `${file}.meta`;
  const guid = existsSync(metaPath) ? readFileSync(metaPath, 'utf8').match(/^guid: (\w+)/m)[1] : hex(`file:${l.file}`);
  const metaText = meta(guid, l.file, img.W, img.H);
  if (CHECK) {
    if (!existsSync(file) || readFileSync(file).readUInt32BE(16) !== img.W || readFileSync(file).readUInt32BE(20) !== img.H || !existsSync(metaPath) || readFileSync(metaPath, 'utf8') !== metaText) {
      console.error(`ui-stage: ${l.file} is missing or stale (run node Tools/ui-stage.mjs)`);
      problems++;
    }
    continue;
  }
  writeFileSync(file, buf);
  writeFileSync(metaPath, metaText);
  console.log(`${l.file}.png ${img.W}x${img.H} ${buf.length} bytes`);
}
if (problems) process.exit(1);
