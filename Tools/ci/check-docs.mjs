#!/usr/bin/env node
// CI `docs` job (docs/design/10 §3 job 2): relative Markdown links resolve; Mermaid blocks are extracted
// to Logs/mermaid/*.mmd so the workflow can render each with mermaid-cli.
import { readFileSync, existsSync, readdirSync, statSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const OUT = join(ROOT, 'Logs', 'mermaid');
// Provenance/ holds verbatim archival copies whose links point into the original repo.
const SKIP = new Set(['node_modules', 'Library', 'Temp', 'Logs', '.git', 'Unity', 'Provenance', '.cache']);
const errors = [];
let blocks = 0;

function walk(dir) {
  return readdirSync(dir).flatMap(f => {
    if (SKIP.has(f)) return [];
    const p = join(dir, f);
    return statSync(p).isDirectory() ? walk(p) : f.endsWith('.md') ? [p] : [];
  });
}
mkdirSync(OUT, { recursive: true });
for (const file of walk(ROOT)) {
  const text = readFileSync(file, 'utf8');
  for (const m of text.matchAll(/\]\(([^)\s#]+)(#[^)]*)?\)/g)) {
    const target = m[1];
    if (/^(https?:|mailto:)/.test(target) || !/[./]/.test(target)) continue;
    if (!existsSync(resolve(dirname(file), decodeURI(target)))) errors.push(`${file.slice(ROOT.length + 1)}: broken link ${target}`);
  }
  let i = 0;
  for (const m of text.matchAll(/```mermaid\n([\s\S]*?)```/g)) {
    blocks++;
    writeFileSync(join(OUT, `${file.slice(ROOT.length + 1).replace(/[\\/]/g, '_')}.${++i}.mmd`), m[1]);
  }
}
if (errors.length) { console.error(`docs: ${errors.length} broken link(s)\n  ${errors.join('\n  ')}`); process.exit(1); }
console.log(`docs: ok — links resolve; ${blocks} mermaid blocks extracted to Logs/mermaid`);
