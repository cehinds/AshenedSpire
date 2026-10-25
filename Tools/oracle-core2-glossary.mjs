#!/usr/bin/env node
// Core 2 glossary oracle (P-3a, docs/BUILD-LOG.md). Drives the SHIPPED web's tooltip glossary for the configured `default` profile:
//   src/ui/components/tooltipGlossary.js   configureTooltipGlossary(registries) and keywordExplanations(registries, text): the terms, keywords, statuses and stances a text names, each once,
//                                          with the words the tooltip says (the framework terms' `term.tooltip.*` rows, the keywords' tooltips, the statuses' tooltips with their numbers resolved)
//   src/ui/components/card.js              cardPlainText: the rules text of every card the Edit Deck inspector reads
//   src/model/loadout.js                   modEffectLines: the effect lines of every armament and armour set the equipment inspection decorates
//
//   node Tools/oracle-core2-glossary.mjs [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/glossary/, sorted keys; two runs are byte-identical):
//   texts.json   [{ source, text, entries: [{ name, explanation }] }]: every card's rules text and every piece's effect lines, and a few synthetic lines, with what the glossary finds in each
//   index.json   the sha, the profile hash and the tallies
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon, classIds, createRun } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'glossary');
const w = await loadWeb();
const reg = w.registries;
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [glossary, card, loadout] = await Promise.all([load('src/ui/components/tooltipGlossary.js'), load('src/ui/components/card.js'), load('src/model/loadout.js')]);
const classes = classIds(w);
const run = createRun(w, 779, classes[0]);

const rows = [];
const add = (source, text) => {
  if (!text) return;
  rows.push({ source, text, entries: glossary.keywordExplanations(reg, text).map((e) => ({ name: e.name, explanation: e.explanation })) });
};

for (const def of reg.cards.all()) {
  for (const upgraded of [false, true]) {
    let text = '';
    try { text = card.cardPlainText(reg, { cardId: def.id, upgraded }, { rules: run }); } catch { text = ''; }
    if (!upgraded || (def.upgrade && text)) add(`card:${def.id}${upgraded ? '+' : ''}`, text);
  }
}
for (const piece of [...reg.equipment.armaments, ...reg.equipment.armour]) {
  const lines = loadout.modEffectLines(reg, piece);
  add(`piece:${piece.id}`, lines.join('. '));
  add(`blurb:${piece.id}`, piece.blurb);
}
for (const [i, text] of ['Gain 3 Block. Keep Block.', 'Poise damage builds toward Stagger. Exhaust. Retain. Ethereal.', 'Played while holding a dual-wield armament; fire and sacred damage hit all allies.', 'Costs 2 Stamina and 1 Mana. Innate.'].entries()) add(`line:${i}`, text);

const found = rows.filter((r) => r.entries.length);
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const write = (name, data) => writeFileSync(join(OUT, name), `${JSON.stringify(canon(data))}\n`);
const names = new Set(found.flatMap((r) => r.entries.map((e) => e.name.toLowerCase())));
write('texts.json', { texts: rows });
write('index.json', { source: 'ui/components/tooltipGlossary.js, ui/components/card.js, model/loadout.js (configured profile default)', webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash,
  tally: { texts: rows.length, withEntries: found.length, distinctNames: names.size, entries: found.reduce((n, r) => n + r.entries.length, 0) } });
console.log(`oracle-core2-glossary: ${rows.length} texts, ${found.length} with entries (${names.size} distinct names) -> Unity/Assets/Tests/Oracle/core2/glossary`);
