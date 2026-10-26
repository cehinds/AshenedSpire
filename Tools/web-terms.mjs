#!/usr/bin/env node
// Tools/web-terms.mjs - ui/terms.json is the web's content/framework/terms.json at the pinned sha, verbatim (D-1455).
//
//   node Tools/web-terms.mjs --write [--source <web checkout>]   copy it from the pinned web
//   node Tools/web-terms.mjs --check [--source <web checkout>]   refuse any drift between the two
//
// The legacy export (Tools/export-content.mjs, the 405 web) used to carry this file and was eight terms behind the pin (fire, sacred, all allies, dual-wield and their tooltips) with six texts
// reworded (Stamina for Action and Energy, the Dodge Roll words, Retain, the Stamina tooltip). The terms are the vocabulary the web's tooltip glossary explains (src/ui/components/tooltipGlossary.js),
// which `TermGlossary` reads. Like the web's own copy it is read as data and never edited by hand.
import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SOURCES = JSON.parse(readFileSync(join(ROOT, 'Tools', 'export', 'sources.json'), 'utf8'));
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const WEB = arg('--source', process.env.ASHEN_SOURCE || '/home/user/cehinds/ashenspire');
const TARGET = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'ui', 'terms.json');

const head = execFileSync('git', ['-C', WEB, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
if (!head.startsWith(SOURCES.core2.webSha)) { console.error(`web-terms: web HEAD ${head.slice(0, 9)} != core2.webSha ${SOURCES.core2.webSha}`); process.exit(1); }
const next = `${JSON.stringify(JSON.parse(readFileSync(join(WEB, 'content', 'framework', 'terms.json'), 'utf8')), null, 2)}\n`;
if (process.argv.includes('--write')) { writeFileSync(TARGET, next); console.log(`web-terms: wrote ui/terms.json (${JSON.parse(next).terms.length} terms) from ${head.slice(0, 9)}`); }
else {
  let have = '';
  try { have = readFileSync(TARGET, 'utf8'); } catch { /* absent */ }
  if (have !== next) { console.error('web-terms: ui/terms.json differs from the pinned web (node Tools/web-terms.mjs --write)'); process.exit(1); }
  console.log(`web-terms: check ok (${JSON.parse(next).terms.length} terms)`);
}
