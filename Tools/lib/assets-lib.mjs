// Helpers shared by Tools/scan-assets.mjs and Tools/lib/release-art.mjs (asset pipeline, US-0.1).
import { readdirSync, statSync, existsSync } from 'node:fs';
import { join } from 'node:path';

export function walk(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir).flatMap(f => { const p = join(dir, f); return statSync(p).isDirectory() ? walk(p) : [p]; });
}

export function webpInfo(buf) {
  if (buf.toString('ascii', 0, 4) !== 'RIFF' || buf.toString('ascii', 8, 12) !== 'WEBP') return null;
  const chunk = buf.toString('ascii', 12, 16);
  if (chunk === 'VP8 ') return { width: buf.readUInt16LE(26) & 0x3fff, height: buf.readUInt16LE(28) & 0x3fff, alpha: false, codec: 'lossy' };
  if (chunk === 'VP8L') {
    const b = buf.subarray(21, 25);
    return { width: 1 + (((b[1] & 0x3f) << 8) | b[0]), height: 1 + (((b[3] & 0x0f) << 10) | (b[2] << 2) | ((b[1] & 0xc0) >> 6)), alpha: ((b[3] >> 4) & 1) === 1, codec: 'lossless' };
  }
  if (chunk === 'VP8X') return { width: 1 + buf.readUIntLE(24, 3), height: 1 + buf.readUIntLE(27, 3), alpha: (buf[20] & 0x10) !== 0, codec: 'extended' };
  return null;
}

export function pngInfo(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) return null;
  const colorType = buf[25];
  return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20), alpha: colorType === 4 || colorType === 6, codec: 'png' };
}

export function svgInfo(text) {
  const num = (re) => { const m = re.exec(text); return m ? Math.round(parseFloat(m[1])) : null; };
  let width = num(/<svg[^>]*\swidth="([\d.]+)/), height = num(/<svg[^>]*\sheight="([\d.]+)/);
  const vb = /viewBox="[\d.-]+\s+[\d.-]+\s+([\d.]+)\s+([\d.]+)"/.exec(text);
  if ((!width || !height) && vb) { width = Math.round(parseFloat(vb[1])); height = Math.round(parseFloat(vb[2])); }
  return { width, height, alpha: true, codec: 'svg' };
}

export function applyTemplate(template, m, tables = {}) {
  return template
    .replace(/\{lower:\$(\d+)\}/g, (_, n) => m[+n].toLowerCase())
    .replace(/\{(\w+):\$(\d+)\}/g, (_, table, n) => { const v = tables[table]?.[m[+n]]; if (v === undefined) throw new Error(`template ${template}: no ${table} entry for ${m[+n]}`); return v; })
    .replace(/\$(\d+)/g, (_, n) => m[+n]);
}

export const canonical = (v) => {
  const sort = (x) => Array.isArray(x) ? x.map(sort) : x && typeof x === 'object' ? Object.fromEntries(Object.keys(x).sort().map(k => [k, sort(x[k])])) : x;
  return `${JSON.stringify(sort(v), null, 2)}\n`;
};

export const mb = (b) => (b / 1048576).toFixed(1);

/** The generated docs/ASSET-INVENTORY.md text for a registry. */
export function inventoryText(registry, sourceLine, derivations) {
  const byDomain = {};
  for (const [id, e] of Object.entries(registry)) {
    const d = id.split('.')[0];
    (byDomain[d] ??= { count: 0, bytes: 0, imported: 0 });
    byDomain[d].count++; byDomain[d].bytes += e.bytes; if (e.unityPath) byDomain[d].imported++;
  }
  const total = Object.values(byDomain).reduce((a, d) => ({ count: a.count + d.count, bytes: a.bytes + d.bytes, imported: a.imported + d.imported }), { count: 0, bytes: 0, imported: 0 });
  return ['# Asset inventory', '', ...sourceLine, '',
    '| Domain | Assets | MB (source) | Imported into Unity this phase |', '|---|---:|---:|---:|',
    ...Object.entries(byDomain).sort().map(([d, s]) => `| ${d} | ${s.count} | ${mb(s.bytes)} | ${s.imported} |`),
    `| **total** | **${total.count}** | **${mb(total.bytes)}** | **${total.imported}** |`, '',
    '## Derivations', '', ...derivations, ''].join('\n');
}

export const INVENTORY_DERIVATIONS = [
  '- WebP → PNG: Blender 4.0 headless (`Tools/blender/webp_to_png.py`), Standard view transform, verified pixel-identical (max diff 0).',
  '- SVG: copied for the built-in `com.unity.modules.vectorgraphics` importer (D-025).',
  '- Art release (`--release`, VP-2, D-550..D-553): rows with a `release` field come from `hd-assets-v23`. WebP/PNG are downscaled to the UI display size and written as PNG (alpha) or JPG (opaque) with sharp; SVG is rasterised (icons 128 px, white so USS tints them; components at 2x). Rows with `unityPath: null` are registered, not imported.',
];

/** docs/ASSET-GAPS.md for a scan (the table is the standing list of known gaps; `orphans` the unmatched files). */
export function gapsText(orphans) {
  return ['# Asset gaps', '', 'Owner ruling (2026-09-26): gaps may be filled with **AI-generated** assets (provenance recorded) or procedural/typographic fallbacks; no licensed third-party material.', '',
    '## Known gaps', '', '| Gap | Fallback now | Plan |', '|---|---|---|',
    "| ~~No font files in the asset set~~ | Cinzel, Inter, IM Fell English (OFL, D-500) in `Resources/UI/Fonts`; bold is synthesised (only 400 + italic exist). Other lore faces not shipped | Add Garamond/Cormorant/Crimson/Spectral/Baskerville when the lore font setting is built |",
    '| No audio files | Procedural synth | Generated music for 8 contexts + stingers (D-006) |',
    '| No Reaver readiness poses | `STANCE-READY` frame | Generate |',
    '| `greatsword`/`sword-shield` suites lack `CONVERSATION` | `PORTRAIT` frame | Generate |',
    '| 51 of 63 relics have no painting | Typographic sigil | Generate in the style of the 12 paintings (D-011) |',
    '| No logo/wordmark | Typographic | Generate (IP-safe) |',
    '| ~~No card art in the imported set~~ | Art release imported (D-550..D-553): `cardlib.*` flat paintings per camp/maneuver for Core 2, `card.<id>.art512` paintings for the 237 web card ids, `cardcut.*` layers. Skills and status cards use a stated placeholder (`ArtPlaceholders` in `Ui.Art`) | Compose the cutout layers with the element/status overlays in the card face (VP-3) |',
    "| Unity's default font has no `⬡` (Smithing Stone glyph, 04 §0) | Glyph renders as an empty box, so the kit uses `◆` in samples | `icon.*` sprites exist (VP-2); the kit swaps the glyph in VP-3 |",
    '| Title gate ash particles (W-02) | None; the gate prompt pulses | Procedural particles in the SKIN step |',
    '| Title background lit/unlit rule (`profile.wins > 0`) | Always `bg.title-city-tower` | Needs the profile service (F4) and a lit/unlit pair |', '',
    '## Unused', '', `Files under the scanned roots that no ID rule matches (${orphans.length}). They are not shipped.`, '', ...orphans.map(o => `- \`${o}\``), ''].join('\n');
}
