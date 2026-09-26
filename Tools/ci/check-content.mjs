#!/usr/bin/env node
// CI `content` job (docs/design/10 §3 job 3). Runs without Unity and without the old repo.
// Checks: manifest integrity (hashes, no unlisted files), count gate, schemas present per data file,
// AI disclosure present (README + strings once the key exists), provenance registry origins (when present).
import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const CONTENT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content');
const CONFIG = JSON.parse(readFileSync(join(ROOT, 'Tools', 'transform.config.json'), 'utf8'));
const errors = [];
const sha = (s) => createHash('sha256').update(s, 'utf8').digest('hex');
const read = (rel) => readFileSync(join(CONTENT, rel), 'utf8').replace(/\r\n/g, '\n');

function walk(dir) {
  return readdirSync(dir).flatMap(f => {
    const p = join(dir, f);
    return statSync(p).isDirectory() ? walk(p) : [relative(CONTENT, p).replace(/\\/g, '/')];
  });
}

const manifest = JSON.parse(read('manifest.json'));
for (const [file, meta] of Object.entries(manifest.files)) {
  if (!existsSync(join(CONTENT, file))) { errors.push(`manifest lists missing file ${file}`); continue; }
  if (sha(read(file)) !== meta.sha256) errors.push(`hash mismatch ${file} (edit content through the tools, or regenerate the manifest)`);
}
for (const file of walk(CONTENT).filter(f => f.endsWith('.json') && f !== 'manifest.json')) {
  if (!(file in manifest.files)) errors.push(`unlisted content file ${file}`);
}
for (const [file, n] of Object.entries(CONFIG.expectedCounts)) {
  if (manifest.counts[file] !== n) errors.push(`count gate: ${file} expected ${n}, manifest says ${manifest.counts[file]}`);
}
for (const file of Object.keys(manifest.files)) {
  if (file.startsWith('schemas/') || file === 'strings/en.json' || file.startsWith('settings/presets/')) continue;
  const schema = `schemas/${file.replace(/\//g, '.').replace(/\.json$/, '')}.schema.json`;
  if (!(schema in manifest.files)) errors.push(`no schema for ${file} (expected ${schema})`);
}
const disclosure = /created with generative AI/i;
if (!disclosure.test(readFileSync(join(ROOT, 'README.md'), 'utf8'))) errors.push('README.md is missing the AI disclosure');
const about = JSON.parse(read('about.json'));
const appStrings = JSON.parse(read('strings/app.en.json'));
const full = appStrings[about.aiDisclosure.fullKey];
if (!full || !disclosure.test(full)) errors.push(`strings/app.en.json ${about.aiDisclosure.fullKey} must state the game was created with generative AI`);
for (const m of about.aiDisclosure.modalities) if (full && !full.toLowerCase().includes(m)) errors.push(`AI disclosure does not name modality "${m}"`);
if (!appStrings[about.aiDisclosure.shortKey]) errors.push(`missing ${about.aiDisclosure.shortKey}`);
if (!about.aiDisclosure.showOnTitle || !about.aiDisclosure.showOnFirstLaunch) errors.push('AI disclosure must show on the title and on first launch (owner ruling 2026-09-26)');

// UI tokens: Tokens.uss is generated from ui/tokens.json and USS may only use its variables (Tools/ui-tokens.mjs).
const tokensCheck = spawnSync(process.execPath, [join(ROOT, 'Tools', 'ui-tokens.mjs'), '--check'], { encoding: 'utf8' });
if (tokensCheck.status !== 0) errors.push((tokensCheck.stderr || tokensCheck.stdout).trim());

if (errors.length) { console.error(`content: ${errors.length} problem(s)\n  ${errors.join('\n  ')}`); process.exit(1); }
console.log(`content: ok — ${Object.keys(manifest.files).length} files, contentHash ${manifest.contentHash.slice(0, 12)}, counts gate ${Object.keys(CONFIG.expectedCounts).length} tables`);
