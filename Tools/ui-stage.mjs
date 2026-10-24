#!/usr/bin/env node
// Combat stage grid (VP-4, D-700): the web's ground grid is a sheared 3 x 4 lattice of faint lines over the battlefield
// (docs/qa/web-screens/combat-1920x1080.jpg: bottom edge x 218..1295, top edge x 437..1514, rows y 383..615 at 1920x1080;
// 390x844: bottom x 5..385, top x 40..385?, rows y 228..497). UI Toolkit draws no skewed lines, so the lattice is rendered once
// to a PNG per layout (Painter2D would need numeric constants in Game C#). Output: Resources/UI/Stage/grid-{wide,narrow}.png (+ .meta),
// laid over an element with the same size (tokens size.boardGrid*) and stretched to it.
//
// VP-10 (D-902) adds the act map's three textures to the same script: the parchment ground (a repeating texture, the web's mapFogDefs paper
// pattern, tools/mapFog 741 seed) and the two sprites a node and the board wear (the dashed ring of an unknown place, the fog vignette).
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
function meta(guid, name, w, h, repeat = false) {
  const [x, y, z, wb] = [0, 0, 0, 0];
  const text = `fileFormatVersion: 2
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
  if (!repeat) return text;
  // A repeating texture (USS background-repeat): a plain Texture2D with wrap mode Repeat, no sprite sheet.
  return text
    .replace(/internalIDToNameTable:\n  - first:\n      213: \d+\n    second: [\w-]+\n/, 'internalIDToNameTable: []\n')
    .replace('wrapU: 1', 'wrapU: 0').replace('wrapV: 1', 'wrapV: 0').replace('wrapW: 1', 'wrapW: 0')
    .replace('spriteMode: 1', 'spriteMode: 0').replace('textureType: 8', 'textureType: 0')
    .replace(/  spriteSheet:[\s\S]*?(  mipmapLimitGroupName:)/, '$1');
}

// ---------------------------------------------------------------------------------------------------------------------
// The act map's textures (D-902).

const rgbaRows = (W, H, pixel) => {
  const rows = [];
  for (let y = 0; y < H; y++) {
    const row = Buffer.alloc(1 + W * 4);
    for (let x = 0; x < W; x++) {
      const [r, g, b, a] = pixel(x, y);
      const o = 1 + x * 4;
      row[o] = Math.round(r);
      row[o + 1] = Math.round(g);
      row[o + 2] = Math.round(b);
      row[o + 3] = Math.round(a * 255);
    }
    rows.push(row);
  }
  return { W, H, data: Buffer.concat(rows) };
};

// The web's parchment (src/ui/components/mapFog.js mapFogDefs, 256 x 256 user units, one texel per unit): the ground #8b7d61, two wave bands
// (#71664f at .16), six engraved lines (#524c3a, .65 wide, .11) and 360 grain dots from the file's own generator (seed 741).
function parchment() {
  const N = 256;
  const px = new Float32Array(N * N * 3);
  for (let i = 0; i < N * N; i++) { px[i * 3] = 0x8b; px[i * 3 + 1] = 0x7d; px[i * 3 + 2] = 0x61; }
  const blend = (i, [r, g, b], a) => {
    px[i * 3] += (r - px[i * 3]) * a;
    px[i * 3 + 1] += (g - px[i * 3 + 1]) * a;
    px[i * 3 + 2] += (b - px[i * 3 + 2]) * a;
  };
  // Path pieces: M x y, Q cx cy x y, T x y (the control reflects the last one), L (V / H as lines), sampled into polylines.
  const sample = (commands) => {
    const pts = [];
    let cur = [0, 0];
    let ctrl = null;
    for (const [op, ...a] of commands) {
      if (op === 'M') { cur = [a[0], a[1]]; ctrl = null; pts.push(cur); }
      else if (op === 'L') { cur = [a[0], a[1]]; ctrl = null; pts.push(cur); }
      else {
        const c = op === 'Q' ? [a[0], a[1]] : [2 * cur[0] - ctrl[0], 2 * cur[1] - ctrl[1]];
        const e = op === 'Q' ? [a[2], a[3]] : [a[0], a[1]];
        for (let t = 1; t <= 64; t++) {
          const u = t / 64;
          pts.push([(1 - u) * (1 - u) * cur[0] + 2 * (1 - u) * u * c[0] + u * u * e[0], (1 - u) * (1 - u) * cur[1] + 2 * (1 - u) * u * c[1] + u * u * e[1]]);
        }
        cur = e;
        ctrl = c;
      }
    }
    return pts;
  };
  const fill = (pts, color, alpha) => {
    // Non-zero fill by scanline at texel centres.
    for (let y = 0; y < N; y++) {
      const yy = y + 0.5;
      const xs = [];
      for (let i = 0; i < pts.length; i++) {
        const [x0, y0] = pts[i];
        const [x1, y1] = pts[(i + 1) % pts.length];
        if ((y0 <= yy && y1 > yy) || (y1 <= yy && y0 > yy)) xs.push(x0 + ((yy - y0) / (y1 - y0)) * (x1 - x0));
      }
      xs.sort((a, b) => a - b);
      for (let k = 0; k + 1 < xs.length; k += 2) {
        for (let x = Math.max(0, Math.ceil(xs[k] - 0.5)); x < Math.min(N, Math.ceil(xs[k + 1] - 0.5)); x++) blend(y * N + x, color, alpha);
      }
    }
  };
  const wave = [0x71, 0x66, 0x4f];
  fill(sample([['M', 0, 42], ['Q', 50, 6, 121, 40], ['T', 256, 27], ['L', 256, 100], ['Q', 193, 142, 114, 91], ['T', 0, 120]]), wave, 0.16);
  fill(sample([['M', 0, 214], ['Q', 62, 156, 140, 201], ['T', 256, 177], ['L', 256, 256], ['L', 0, 256]]), wave, 0.16);
  const stroke = (pts, color, alpha, half) => {
    for (let i = 0; i + 1 < pts.length; i++) {
      const [x0, y0] = pts[i];
      const [x1, y1] = pts[i + 1];
      const dx = x1 - x0;
      const dy = y1 - y0;
      const len2 = dx * dx + dy * dy || 1;
      for (let y = Math.max(0, Math.floor(Math.min(y0, y1) - 2)); y <= Math.min(N - 1, Math.ceil(Math.max(y0, y1) + 2)); y++) {
        for (let x = Math.max(0, Math.floor(Math.min(x0, x1) - 2)); x <= Math.min(N - 1, Math.ceil(Math.max(x0, x1) + 2)); x++) {
          const t = Math.max(0, Math.min(1, ((x + 0.5 - x0) * dx + (y + 0.5 - y0) * dy) / len2));
          const d = Math.hypot(x + 0.5 - (x0 + t * dx), y + 0.5 - (y0 + t * dy));
          const cover = Math.max(0, Math.min(1, half + 0.5 - d));
          if (cover > 0) blend(y * N + x, color, alpha * cover);
        }
      }
    }
  };
  const ink = [0x52, 0x4c, 0x3a];
  const lines = [
    [['M', -12, 95], ['Q', 30, 40, 95, 65], ['T', 212, 71], ['T', 272, 33]], [['M', -12, 104], ['Q', 30, 50, 95, 75], ['T', 212, 81], ['T', 272, 43]],
    [['M', -12, 113], ['Q', 30, 59, 95, 84], ['T', 212, 90], ['T', 272, 52]], [['M', 28, 272], ['Q', 3, 228, 44, 198], ['T', 130, 190], ['T', 213, 145], ['T', 274, 166]],
    [['M', 36, 272], ['Q', 12, 233, 51, 207], ['T', 137, 199], ['T', 220, 154], ['T', 274, 175]], [['M', 44, 272], ['Q', 21, 238, 58, 216], ['T', 144, 208], ['T', 227, 163], ['T', 274, 184]],
  ];
  for (const line of lines) stroke(sample(line), ink, 0.11, 0.325);
  let state = 741;
  const random = () => ((state = (Math.imul(state, 1664525) + 1013904223) >>> 0) / 4294967296);
  for (let i = 0; i < 360; i++) {
    const cx = random() * 256;
    const cy = random() * 256;
    const r = 0.2 + random() * 0.65;
    const color = random() > 0.5 ? [0x30, 0x2d, 0x22] : [0xd6, 0xc7, 0xa1];
    for (let y = Math.max(0, Math.floor(cy - r - 1)); y <= Math.min(N - 1, Math.ceil(cy + r + 1)); y++) {
      for (let x = Math.max(0, Math.floor(cx - r - 1)); x <= Math.min(N - 1, Math.ceil(cx + r + 1)); x++) {
        const cover = Math.max(0, Math.min(1, r + 0.5 - Math.hypot(x + 0.5 - cx, y + 0.5 - cy)));
        if (cover > 0) blend(y * N + x, color, 0.23 * cover);
      }
    }
  }
  return rgbaRows(N, N, (x, y) => [px[(y * N + x) * 3], px[(y * N + x) * 3 + 1], px[(y * N + x) * 3 + 2], 1]);
}

// The dashed ring of an unknown place (web map.css .k-placed circle { stroke-dasharray: 3 4 }): 24 dashes, 3 of every 7 units drawn, in white (USS tints it), hugging the
// edge of a 174 texel square (a 87 px node at two texels a reference px).
function dashedRing() {
  const N = 174;
  const DASHES = 24;
  const DUTY = 3 / 7;
  const OUTER = N / 2;
  const WIDTH = 7;
  return rgbaRows(N, N, (x, y) => {
    const dx = x + 0.5 - N / 2;
    const dy = y + 0.5 - N / 2;
    const d = Math.hypot(dx, dy);
    const ring = Math.max(0, Math.min(1, Math.min(OUTER - d, d - (OUTER - WIDTH)) + 0.5));
    const turn = (Math.atan2(dy, dx) / (2 * Math.PI) + 1) % 1;
    const within = (turn * DASHES) % 1;
    const edge = (DUTY / 2 - Math.abs(within - 0.5)) * (2 * Math.PI * (OUTER - WIDTH / 2)) / DASHES;
    return [255, 255, 255, ring * Math.max(0, Math.min(1, edge + 0.5))];
  });
}

// The fog vignette (map.css .map-fog .map-vignette: an ellipse 85% x 68% at 50% 46%, clear to 55% of it and then to 45% black): 128 x 72 texels, stretched over the viewport.
function vignette() {
  const W = 128;
  const H = 72;
  return rgbaRows(W, H, (x, y) => {
    const dx = ((x + 0.5) / W - 0.5) / 0.85;
    const dy = ((y + 0.5) / H - 0.46) / 0.68;
    const d = Math.hypot(dx, dy);
    const t = Math.max(0, Math.min(1, (d - 0.55) / (1 - 0.55)));
    return [0, 0, 0, 0.45 * t];
  });
}

const extras = [
  { file: 'map-parchment', make: parchment, repeat: true },
  { file: 'map-node-dashed', make: dashedRing },
  { file: 'map-vignette', make: vignette },
];

let problems = 0;
mkdirSync(OUT, { recursive: true });
for (const l of [...layouts, ...extras]) {
  const img = l.make ? l.make() : raster(l);
  const buf = png(img);
  const file = join(OUT, `${l.file}.png`);
  const metaPath = `${file}.meta`;
  const guid = existsSync(metaPath) ? readFileSync(metaPath, 'utf8').match(/^guid: (\w+)/m)[1] : hex(`file:${l.file}`);
  const metaText = meta(guid, l.file, img.W, img.H, !!l.repeat);
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
