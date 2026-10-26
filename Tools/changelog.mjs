#!/usr/bin/env node
// Tools/changelog.mjs - the in-game Changelog (Settings > Advanced > Changelog, web src/ui/screens/about.js renderChangelogSection over src/content/changelog.js).
//
//   node Tools/changelog.mjs --write     CHANGELOG.md -> Unity/Assets/StreamingAssets/Content/ui/changelog.json
//   node Tools/changelog.mjs --check     refuse any drift between the two (the web's tools/about-changelog.mjs does the same)
//
// CHANGELOG.md is the authored owner (D-040); the JSON is only its bundled transport, so the game shows the receipts without a second hand-kept copy of their prose.
// Each `## [version] - date` section with at least one bullet is one entry, newest first as the file lists them: the first bullet is its summary, the other bullets its detail; a section with no bullets
// says nothing and is left out. `Stories:` lines and markdown marks (`**`, backticks, links) are dropped from the words.
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SOURCE = join(ROOT, 'CHANGELOG.md');
const TARGET = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'ui', 'changelog.json');

const plain = (text) => text.replace(/\[([^\]]+)\]\([^)]*\)/g, '$1').replace(/[*`]/g, '').replace(/\s+/g, ' ').trim();

export function parse(markdown) {
  const entries = [];
  let current = null;
  for (const line of markdown.split(/\r?\n/)) {
    const head = /^## \[([^\]]+)\]\s*[—-]\s*(\d{4}-\d{2}-\d{2})\s*$/.exec(line);
    if (head) { current = { id: `v${head[1]}`, date: head[2], group: head[2], build: head[1], bullets: [] }; entries.push(current); continue; }
    const bullet = /^- (.+)$/.exec(line);
    if (current && bullet) current.bullets.push(plain(bullet[1]));
  }
  return entries.filter((entry) => entry.bullets.length).map((entry) => ({
    id: entry.id, date: entry.date, group: entry.group, summary: entry.bullets[0], detail: entry.bullets.slice(1).join('\n'), build: entry.build,
  }));
}

const text = (entries) => `${JSON.stringify({ comment: 'GENERATED from /CHANGELOG.md by Tools/changelog.mjs --write. Do not edit: --check refuses any drift from the authoritative Markdown.', entries }, null, 2)}\n`;

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const next = text(parse(readFileSync(SOURCE, 'utf8')));
  if (process.argv.includes('--write')) { writeFileSync(TARGET, next); console.log(`changelog: wrote ${JSON.parse(next).entries.length} entries -> ui/changelog.json`); }
  else {
    let have = '';
    try { have = readFileSync(TARGET, 'utf8'); } catch { /* absent */ }
    if (have !== next) { console.error('changelog: ui/changelog.json drifted from CHANGELOG.md (node Tools/changelog.mjs --write)'); process.exit(1); }
    console.log('changelog: check ok');
  }
}
