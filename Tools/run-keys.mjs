#!/usr/bin/env node
// Regenerates Tools/codegen.d/run.json from the names the run port uses (us-2.2): Ashen.Domain.Run reads combat
// names through the combat aliases (K. = CombatKeys, V. = CombatValues) and its own through RK./RV./RM.
// (RunKeys, RunValues, RunMessages). A run name that already exists in the combat fragment must use the combat
// alias (no duplicate identifiers across fragments), and every combat name it uses must exist there.
//   node Tools/run-keys.mjs        (then node Tools/codegen.mjs)
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';

const DIR = process.env.RUN_DIR || 'Unity/Assets/Game/Domain/Run';
const src = readdirSync(DIR).filter((f) => f.endsWith('.cs')).map((f) => readFileSync(`${DIR}/${f}`, 'utf8')).join('\n');
const used = (alias) => [...new Set([...src.matchAll(new RegExp(`(?<![A-Za-z0-9_.])${alias}[.]([A-Z][A-Za-z0-9]*)`, 'g'))].map((m) => m[1]))].sort();
const camel = (s) => s[0].toLowerCase() + s.slice(1);
const combat = JSON.parse(readFileSync('Tools/codegen.d/combat.json', 'utf8'));
const set = (name) => combat.keySets.find((k) => k.name === name).keys;
const CK = set('CombatKeys');
const CV = set('CombatValues');
let failed = false;
const fail = (what, list) => { if (list.length) { console.error(`${what}: ${list.join(', ')}`); failed = true; } };

fail('K. names missing from CombatKeys (use RK.)', used('K').filter((k) => !(k in CK)));
fail('RK. names already in CombatKeys (use K.)', used('RK').filter((k) => k in CK && CK[k] === camel(k)));
fail('V. names missing from CombatValues (use RV.)', used('V').filter((k) => !(k in CV)));
const RVALUES = JSON.parse(readFileSync('Tools/run-keys/values.json', 'utf8'));
fail('RV. names already in CombatValues with the same value (use V.)', used('RV').filter((k) => k in CV && CV[k] === RVALUES[k]));
fail('RV. names missing from Tools/run-keys/values.json', used('RV').filter((k) => !(k in RVALUES)));
const RMESSAGES = JSON.parse(readFileSync('Tools/run-keys/messages.json', 'utf8'));
fail('RM. names missing from Tools/run-keys/messages.json', used('RM').filter((k) => !(k in RMESSAGES)));
const PATTERNS = {
  AttackSlot: String.raw`^attack:(?<index>0|[1-9][0-9]*)$`,
  ItemRef: String.raw`^(armament/[^/]+|armor/[^/]+/[^/]+)$`,
};
fail('RunPatterns names missing', used('Ashen.Generated.RunPatterns').filter((k) => !(k in PATTERNS)));
if (failed) process.exit(1);

const out = {
  comment: 'Run engine names (stream C, us-2.2): run-document, creation-table and rule keys, closed values, messages and patterns the C# port of model/state.js createRunState reads that the combat fragment does not already name. Values are the shipped wire strings (D-037).',
  keySets: [
    { name: 'RunKeys', doc: 'Keys of run documents, creation tables and rules/runEngine.json the run port reads (shipped wire names).', keys: Object.fromEntries(used('RK').map((k) => [k, camel(k)])) },
    { name: 'RunValues', doc: 'Closed run values (roles, mount spellings, sources, tags, separators) — shipped wire strings.', keys: Object.fromEntries(used('RV').map((k) => [k, RVALUES[k]])) },
    { name: 'RunMessages', doc: 'Run engine error messages (developer-facing; {0}.. are string.Format slots).', keys: Object.fromEntries(used('RM').map((k) => [k, RMESSAGES[k]])) },
    { name: 'RunPatterns', doc: 'Patterns the run port parses (retired attack-slot ids, namespaced item refs).', keys: PATTERNS },
  ],
};
writeFileSync('Tools/codegen.d/run.json', `${JSON.stringify(out, null, 2)}\n`);
console.log(Object.fromEntries(out.keySets.map((k) => [k.name, Object.keys(k.keys).length])));
