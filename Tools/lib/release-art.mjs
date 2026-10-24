// Art-release pass of the asset pipeline (VP-2; docs/DECISIONS.md D-550..D-553).
//
//   node Tools/scan-assets.mjs --release <dir> [--web <web repo>] [--import] [--check] [--force]
//
// <dir> is the web's .art-cache/<tag> (high/ and common/), fetched by that repo's tools/fetch-art.mjs. The pass is additive:
// it reads the committed registry, adds the ids the config's `release.rules` name (never touching an id that already has a
// committed Unity import), records provenance and, with --import, writes the downscaled/rasterised images and their .meta
// files into Unity/Assets/Art/Imported. The images themselves stay uncommitted (D-018); the .meta files are committed.
import { readFileSync, writeFileSync, mkdirSync, existsSync, statSync } from 'node:fs';
import { join, dirname, relative, extname } from 'node:path';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import { walk, webpInfo, pngInfo, svgInfo, applyTemplate, canonical, mb, inventoryText, INVENTORY_DERIVATIONS, gapsText } from './assets-lib.mjs';

const IMPORTED_UNITY = 'Assets/Art/Imported';
const md5 = (s) => createHash('md5').update(s).digest('hex');
const sha256 = (b) => createHash('sha256').update(b).digest('hex');

/** The size an image is written at (deterministic, so the registry can record it without converting). */
export function targetSize(width, height, spec, isSvg) {
  if (isSvg) {
    const k = spec.svgSize ? spec.svgSize / Math.max(width, height) : (spec.scale || 1);
    return [Math.round(width * k), Math.round(height * k)];
  }
  let k = 1;
  if (spec.maxWidth) k = Math.min(k, spec.maxWidth / width);
  if (spec.maxHeight) k = Math.min(k, spec.maxHeight / height);
  return k < 1 ? [Math.round(width * k), Math.round(height * k)] : [width, height];
}

/** The Unity-relative path an id is imported to (D-018 layout: one folder per id segment, or flat under the domain). */
export function unityPathFor(id, spec) {
  const parts = id.split('.');
  const ext = spec.format === 'jpg' ? '.jpg' : '.png';
  if (spec.flat) return `${IMPORTED_UNITY}/${parts[0]}/${parts.slice(1).join('.')}${ext}`;
  return `${IMPORTED_UNITY}/${parts.join('/')}${ext}`;
}

function textureMeta(guid, { alpha, maxSize }) {
  return `fileFormatVersion: 2
guid: ${guid}
TextureImporter:
  internalIDToNameTable: []
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
  maxTextureSize: ${maxSize}
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
  spriteMeshType: 1
  alignment: 0
  spritePivot: {x: 0.5, y: 0.5}
  spritePixelsToUnits: 100
  spriteBorder: {x: 0, y: 0, z: 0, w: 0}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: ${alpha ? 1 : 0}
  alphaIsTransparency: ${alpha ? 1 : 0}
  spriteTessellationMethod: 0
  spriteTessellationDetail: -1
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
    maxTextureSize: ${maxSize}
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData:
    physicsShape: []
    bones: []
    spriteID:
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {}
  mipmapLimitGroupName:
  pSDRemoveMatte: 0
  userData:
  assetBundleName:
  assetBundleVariant:
`;
}

const folderMeta = (guid) => `fileFormatVersion: 2\nguid: ${guid}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n`;

function loadSharp(ROOT) {
  const tried = [process.env.ASHEN_SHARP, join(ROOT, 'Tools', 'package.json')].filter(Boolean);
  for (const base of tried) {
    try { return createRequire(base.endsWith('.json') ? base : join(base, 'x.js'))('sharp'); } catch { /* next */ }
  }
  console.error('release: the sharp package is needed for --import. Run: cd Tools && npm install   (or set ASHEN_SHARP to a folder whose node_modules has sharp)');
  process.exit(1);
}

export async function runRelease({ ROOT, CONFIG, releaseDir, webDir, doImport, check, force }) {
  const R = CONFIG.release;
  if (!releaseDir || !existsSync(releaseDir)) { console.error('release: pass the unpacked art cache, e.g. --release <web>/.art-cache/hd-assets-v23'); process.exit(1); }
  const CONTENT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content');
  const registryPath = join(CONTENT, 'assets', 'registry.json');
  const registry = JSON.parse(readFileSync(registryPath, 'utf8'));
  const before = canonical(registry);
  const ignore = R.ignore.map(r => new RegExp(r));
  const rules = R.rules.map(r => ({ re: new RegExp(r.match), id: r.id }));
  const legacyRules = CONFIG.rules.map(r => new RegExp(r.match));
  const converts = R.convert.map(c => ({ ...c, re: new RegExp(c.match) }));

  // ----- the files of each pack (and of the web repo's tracked roots) -----
  const files = [];
  for (const pack of Object.values(R.packs)) for (const root of pack.roots) {
    const base = join(releaseDir, pack.dir);
    for (const abs of walk(join(base, root))) files.push({ abs, rel: relative(base, abs).replace(/\\/g, '/') });
  }
  if (webDir) for (const root of R.web.roots) for (const abs of walk(join(webDir, root))) files.push({ abs, rel: relative(webDir, abs).replace(/\\/g, '/') });
  files.sort((a, b) => (a.rel < b.rel ? -1 : 1));

  const stats = { files: 0, added: 0, replaced: 0, kept: 0, unchanged: 0, legacy: 0 };
  const orphans = [];
  const seen = new Map();
  for (const f of files) {
    if (ignore.some(re => re.test(f.rel))) continue;
    stats.files++;
    const rule = rules.find(r => r.re.test(f.rel));
    if (!rule) { if (legacyRules.some(re => re.test(f.rel))) stats.legacy++; else orphans.push(f.rel); continue; }
    const id = applyTemplate(rule.id, rule.re.exec(f.rel), { maps: R.maps });
    if (seen.has(id)) { console.error(`release: duplicate asset id ${id}: ${seen.get(id)} | ${f.rel}`); process.exit(1); }
    seen.set(id, f.rel);
    const buf = readFileSync(f.abs);
    const ext = extname(f.rel).toLowerCase();
    const info = ext === '.webp' ? webpInfo(buf) : ext === '.png' ? pngInfo(buf) : ext === '.svg' ? svgInfo(buf.toString('utf8')) : null;
    if (!info) { console.error(`release: cannot read ${f.rel}`); process.exit(1); }
    const spec = converts.find(c => c.re.test(id));
    const entry = {
      origin: 'imported', release: R.tag, source: f.rel, sha256: sha256(buf), bytes: buf.length,
      format: ext.slice(1), width: info.width, height: info.height, alpha: info.alpha, unityPath: null,
    };
    if (spec) {
      const [w, h] = targetSize(info.width, info.height, spec, ext === '.svg');
      entry.unityPath = unityPathFor(id, spec);
      entry.unityWidth = w; entry.unityHeight = h;
    }
    const old = registry[id];
    if (old && old.unityPath && old.release !== R.tag) { stats.kept++; continue; }
    if (old && canonical(old) === canonical(entry)) { stats.unchanged++; continue; }
    if (old) stats.replaced++; else stats.added++;
    registry[id] = entry;
  }

  const releaseIds = Object.entries(registry).filter(([, e]) => e.release === R.tag);
  const importedIds = releaseIds.filter(([, e]) => e.unityPath);
  const after = canonical(registry);
  const summary = `release ${R.tag}: ${stats.files} files · +${stats.added} added, ${stats.replaced} replaced, ${stats.kept} kept (already imported), ${stats.unchanged} unchanged, ${stats.legacy} on legacy rules (not registered) · ${orphans.length} unused · ${releaseIds.length} release ids, ${importedIds.length} for import`;
  if (check) {
    if (before !== after) { console.error(`${summary}\nrelease: --check found drift; run without --check`); process.exit(1); }
    console.log(`${summary}\nrelease: check ok`);
    return;
  }

  writeFileSync(registryPath, after);
  const sourceLine = [`Generated by \`node Tools/scan-assets.mjs\` (legacy source) and \`--release\` (art release \`${R.tag}\`, VP-2). Every shipped image resolves through \`Content/assets/registry.json\` (US-0.1).`];
  writeFileSync(join(ROOT, 'docs', 'ASSET-INVENTORY.md'), inventoryText(registry, sourceLine, INVENTORY_DERIVATIONS));
  writeFileSync(join(ROOT, 'docs', 'ASSET-GAPS.md'), gapsText(orphans));
  writeProvenance({ ROOT, R, releaseDir, webDir, stats, releaseIds, importedIds, orphans });

  // ----- import -----
  let converted = 0, skipped = 0, failed = 0, bytes = 0;
  if (doImport) {
    const sharp = loadSharp(ROOT);
    const packOf = (rel) => rel.startsWith('assets-display/') ? webDir : null;
    const jobs = [];
    for (const [id, e] of importedIds) {
      const dst = join(ROOT, 'Unity', e.unityPath);
      if (existsSync(dst) && !force) { skipped++; if (!existsSync(`${dst}.meta`)) jobs.push({ id, e, dst, metaOnly: true }); continue; }
      jobs.push({ id, e, dst });
    }
    const srcOf = (e) => {
      if (packOf(e.source)) return join(webDir, e.source);
      const hit = Object.values(R.packs).map(p => join(releaseDir, p.dir, e.source)).find(existsSync);
      return hit;
    };
    const worker = async (job) => {
      const { id, e, dst } = job;
      try {
        const spec = converts.find(c => c.re.test(id));
        if (!job.metaOnly) {
          const src = srcOf(e);
          mkdirSync(dirname(dst), { recursive: true });
          let img;
          if (e.format === 'svg') {
            let svg = readFileSync(src, 'utf8');
            if (spec.svgColor) svg = svg.replace(/currentColor/g, spec.svgColor).replace(/\scolor="[^"]*"/, '');
            const k = e.unityWidth / e.width;
            img = sharp(Buffer.from(svg), { density: Math.max(1, 72 * k) }).resize(e.unityWidth, e.unityHeight, { fit: 'fill' });
          } else {
            img = sharp(src);
            if (e.unityWidth !== e.width || e.unityHeight !== e.height) img = img.resize(e.unityWidth, e.unityHeight, { fit: 'fill', kernel: 'lanczos3' });
          }
          if (spec.format === 'jpg') {
            const m = await sharp(src).stats();
            if (e.format !== 'svg' && m.channels[3] && m.channels[3].min < 255) throw new Error('has transparency but the convert entry is jpg');
            await img.removeAlpha().jpeg({ quality: spec.quality || 85, mozjpeg: true }).toFile(dst);
          } else await img.png({ compressionLevel: 9 }).toFile(dst);
          const out = await sharp(dst).metadata();
          if (out.width !== e.unityWidth || out.height !== e.unityHeight) throw new Error(`wrote ${out.width}x${out.height}, registry says ${e.unityWidth}x${e.unityHeight}`);
          bytes += statSync(dst).size; converted++;
        }
        const metaPath = `${dst}.meta`;
        if (!existsSync(metaPath)) {
          const maxSize = Math.max(32, Math.min(2048, 2 ** Math.ceil(Math.log2(Math.max(e.unityWidth, e.unityHeight)))));
          writeFileSync(metaPath, textureMeta(md5(`Unity/${e.unityPath}`), { alpha: spec.format === 'png' && e.alpha !== false, maxSize }));
        }
        for (let d = dirname(e.unityPath); d.length > IMPORTED_UNITY.length; d = dirname(d)) {
          const folder = join(ROOT, 'Unity', `${d}.meta`);
          if (!existsSync(folder)) writeFileSync(folder, folderMeta(md5(`Unity/${d}`)));
        }
      } catch (err) { failed++; console.error(`release: ${id}: ${err.message}`); }
    };
    const queue = [...jobs];
    await Promise.all(Array.from({ length: 6 }, async () => { for (let j = queue.shift(); j; j = queue.shift()) await worker(j); }));
  }
  console.log(`${summary}` + (doImport ? `\nrelease: import wrote ${converted} images (${mb(bytes)} MB), ${skipped} already present, ${failed} failed` : ''));
  if (failed) process.exit(1);
}

function writeProvenance({ ROOT, R, releaseDir, webDir, stats, releaseIds, importedIds, orphans }) {
  const pinCandidates = [webDir && join(webDir, 'art-release.json'), join(releaseDir, '..', '..', 'art-release.json')].filter(Boolean);
  const pinPath = pinCandidates.find(existsSync);
  const pin = pinPath ? JSON.parse(readFileSync(pinPath, 'utf8')) : null;
  let webCommit = null;
  if (webDir) { const r = spawnSync('git', ['-C', webDir, 'rev-parse', 'HEAD'], { encoding: 'utf8' }); if (r.status === 0) webCommit = r.stdout.trim(); }
  const provDir = join(ROOT, 'Provenance', 'imported');
  mkdirSync(join(provDir, 'releases'), { recursive: true });
  const releaseFile = join(provDir, 'releases', `${R.tag}.json`);
  writeFileSync(releaseFile, canonical({
    tag: R.tag, repo: pin?.repo ?? null, packs: pin?.packs ?? null, webCommit,
    note: 'Every id below the registry marks with release=<tag> was read from these pack zips (sha256 verified by the web tools/fetch-art.mjs before unpacking). Rights: CREDITS.md (first-party / project-owned AI-generated or authored; no third-party license claimed). No game-icons.net (CC BY 3.0) file is imported: the legacy outline card icons (cardoutline.*) and official v1 card paintings (cardofficial.*) are registered, not imported.',
    counts: { files: stats.files, releaseIds: releaseIds.length, imported: importedIds.length, unused: orphans.length },
    imported: importedIds.map(([id, e]) => ({ id, source: e.source, sha256: e.sha256 })).sort((a, b) => (a.id < b.id ? -1 : 1)),
  }));
  const manifestPath = join(provDir, 'manifest.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const credits = webDir && existsSync(join(webDir, 'CREDITS.md')) ? readFileSync(join(webDir, 'CREDITS.md')) : null;
  if (credits) writeFileSync(join(provDir, 'CREDITS.md'), credits);
  const upsert = (source, file) => {
    const sha = sha256(readFileSync(file));
    const row = manifest.files.find(f => f.source === source);
    if (row) row.sha256 = sha; else manifest.files.push({ source, sha256: sha });
  };
  if (credits) upsert('CREDITS.md', join(provDir, 'CREDITS.md'));
  upsert(`releases/${R.tag}.json`, releaseFile);
  manifest.files.sort((a, b) => (a.source < b.source ? -1 : 1));
  writeFileSync(manifestPath, canonical(manifest));
}
