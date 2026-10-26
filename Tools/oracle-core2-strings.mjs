#!/usr/bin/env node
// P-6 (D-1649): the words of the Core 2 flow against the web's. Tools/web-strings.config.json lists, for the Core 2 screens' keys, where the web says the same thing: a row of its string table
// (content/source/uiStrings.csv, generated into src/content/generated/uiStrings.js and read through the same extends rule as src/ui/strings.js) or a literal at a line of a file under src/.
// This script resolves each reference against the pinned web checkout and writes what the web says, so the C# test compares this build's text with it and a key whose text drifts fails.
//
//   node Tools/oracle-core2-strings.mjs           write Unity/Assets/Tests/Oracle/core2/strings
//   node Tools/oracle-core2-strings.mjs --check   fail on drift (the text a reference resolves to, a literal that moved, a key this build no longer has)
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { ORACLE_DIR, WEB, guardWebSha, canon } from './oracle-core2.mjs';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const CONFIG = JSON.parse(readFileSync(join(ROOT, 'Tools', 'web-strings.config.json'), 'utf8'));
const OUT = join(ORACLE_DIR, 'strings');
const STRINGS = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'strings');
const head = guardWebSha(WEB);

// This build's strings, every table.
const unity = {};
for (const f of ['en.json', 'app.en.json', 'meta.en.json', 'combat.en.json', 'nodes.en.json', 'shop.en.json', 'events.en.json']) {
  const p = join(STRINGS, f);
  if (existsSync(p)) Object.assign(unity, JSON.parse(readFileSync(p, 'utf8')));
}

// The web's table with its extends rule (src/ui/strings.js resolve).
const { uiStrings } = await import(pathToFileURL(join(WEB, 'src', 'content', 'generated', 'uiStrings.js')).href);
const rows = new Map(uiStrings.map((r) => [String(r.id).trim(), r]));
const resolved = new Map();
function resolve(id) {
  if (resolved.has(id)) return resolved.get(id);
  const row = rows.get(id);
  if (!row) throw new Error(`oracle-core2-strings: no uiStrings row '${id}'`);
  const parentId = String(row.extends || '').trim();
  const parent = parentId ? resolve(parentId) : null;
  const out = {};
  for (const form of ['short', 'full', 'tip']) {
    const own = row[form] == null ? '' : String(row[form]);
    out[form] = own || (parent ? parent[form] : '');
  }
  resolved.set(id, out);
  return out;
}

// The comparison the C# test makes too: placeholders are one token, curly quotes are straight, the narrow and no-break spaces are spaces, case is the stylesheet's.
export const norm = (s) => String(s).replace(/\$\{[^}]*\}/g, '{}').replace(/\{[^}]*\}/g, '{}').replace(/[‘’]/g, "'").replace(/[“”]/g, '"').replace(/[  ]/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();

const literalsOnLine = (line) => {
  const found = [];
  const rx = /'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"|`((?:[^`\\]|\\.)*)`/g;
  let m;
  while ((m = rx.exec(line))) {
    const s = m[1] ?? m[2] ?? m[3];
    found.push(s.replace(/\\'/g, "'").replace(/\\"/g, '"').replace(/\\n/g, '\n').replace(/\\u2019/g, '’').replace(/\\u2014/g, '—').replace(/\\u00a0/g, ' '));
  }
  return found;
};

const strings = {};
const problems = [];
for (const [key, ref] of Object.entries(CONFIG.refs)) {
  if (!(key in unity)) { problems.push(`${key}: this build has no such string`); continue; }
  try {
    if (ref.startsWith('table:')) {
      const [id, form] = [ref.slice(6, ref.lastIndexOf('.')), ref.slice(ref.lastIndexOf('.') + 1)];
      const text = resolve(id)[form];
      if (!text) throw new Error(`'${id}' has no ${form} form`);
      strings[key] = { ref, web: text };
    } else if (ref.startsWith('lit:')) {
      const at = ref.slice(4);
      const file = at.slice(0, at.lastIndexOf(':'));
      const lineNo = Number(at.slice(at.lastIndexOf(':') + 1));
      const line = readFileSync(join(WEB, 'src', file), 'utf8').split('\n')[lineNo - 1] ?? '';
      // A literal of its own, else the words inside a longer one (a template literal holding markup, which the quote scan cannot split).
      const hit = literalsOnLine(line).find((s) => norm(s) === norm(unity[key])) ?? (line.replace(/[‘’]/g, "'").toLowerCase().includes(String(unity[key]).replace(/[‘’]/g, "'").toLowerCase()) ? unity[key] : null);
      if (hit == null) throw new Error(`no literal on ${file}:${lineNo} says "${unity[key]}"`);
      strings[key] = { ref, web: hit };
    } else throw new Error(`unknown reference '${ref}'`);
  } catch (e) { problems.push(`${key}: ${e.message}`); }
}
if (problems.length) { console.error(`oracle-core2-strings: ${problems.length} references do not resolve:\n${problems.slice(0, 40).join('\n')}`); process.exit(1); }

const doc = { source: 'content/source/uiStrings.csv (src/content/generated/uiStrings.js) and literals of src/ui, src/model, src/main.js', webSha: head, keys: Object.keys(strings).length, unchecked: CONFIG.unchecked.length, strings };
const text = `${JSON.stringify(canon(doc))}\n`;
const file = join(OUT, 'strings.json');
if (process.argv.includes('--check')) {
  if (!existsSync(file) || readFileSync(file, 'utf8') !== text) { console.error('oracle-core2-strings: Unity/Assets/Tests/Oracle/core2/strings/strings.json is out of date (run node Tools/oracle-core2-strings.mjs)'); process.exit(1); }
  console.log(`oracle-core2-strings: check ok (${doc.keys} keys, ${doc.unchecked} unchecked)`);
} else {
  mkdirSync(OUT, { recursive: true });
  writeFileSync(file, text);
  console.log(`oracle-core2-strings: ${doc.keys} keys resolved, ${doc.unchecked} unchecked -> Unity/Assets/Tests/Oracle/core2/strings`);
}
