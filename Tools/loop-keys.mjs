#!/usr/bin/env node
// Regenerates Tools/codegen.d/loop.json from the names the run-loop port uses (us-8.1): Ashen.Domain.Loop (and the
// content factory's ToLoopData) reads combat, run, map and rewards names through their aliases (K./E./V./M./Op. =
// CombatKeys/Events/Values/Messages/Ops, RK./RV./RM. = RunKeys/Values/Messages, MK./MV./MM. = MapKeys/Values/Messages,
// WK./WV./WM. = RewardsKeys/Values/Messages) and its own through LK./LV./LM. (LoopKeys, LoopValues, LoopMessages).
// A loop name that already exists in another fragment with the same wire value must use that fragment's alias (no
// duplicate identifiers across fragments), and every borrowed name must exist where its alias points.
//   node Tools/loop-keys.mjs        (then node Tools/codegen.mjs)
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';

const DIRS = ['Unity/Assets/Game/Domain/Loop'];
const FILES = ['Unity/Assets/Game/Content/Runtime/RuntimeRegistries.cs'];
const src = [
  ...DIRS.flatMap((dir) => readdirSync(dir).filter((f) => f.endsWith('.cs')).map((f) => `${dir}/${f}`)),
  ...FILES,
].map((f) => readFileSync(f, 'utf8')).join('\n');
const used = (alias) => [...new Set([...src.matchAll(new RegExp(`(?<![A-Za-z0-9_.])${alias}[.]([A-Z][A-Za-z0-9]*)`, 'g'))].map((m) => m[1]))].sort();
const camel = (s) => s[0].toLowerCase() + s.slice(1);
const fragment = (file) => JSON.parse(readFileSync(`Tools/codegen.d/${file}`, 'utf8'));
const set = (frag, name) => (fragment(frag).keySets.find((k) => k.name === name) || { keys: {} }).keys;
const BORROWED = {
  K: set('combat.json', 'CombatKeys'), E: set('combat.json', 'CombatEvents'), V: set('combat.json', 'CombatValues'),
  M: set('combat.json', 'CombatMessages'), Op: set('combat.json', 'CombatOps'),
  RK: set('run.json', 'RunKeys'), RV: set('run.json', 'RunValues'), RM: set('run.json', 'RunMessages'),
  MK: set('map.json', 'MapKeys'), MV: set('map.json', 'MapValues'), MM: set('map.json', 'MapMessages'),
  WK: set('rewards.json', 'RewardsKeys'), WV: set('rewards.json', 'RewardsValues'), WM: set('rewards.json', 'RewardsMessages'),
};
const KEYS = ['K', 'RK', 'MK', 'WK'];
const VALUES = ['V', 'RV', 'MV', 'WV'];
let failed = false;
const fail = (what, list) => { if (list.length) { console.error(`loop-keys: ${what}: ${list.join(', ')}`); failed = true; } };

for (const [alias, names] of Object.entries(BORROWED)) fail(`${alias}. names missing from their fragment`, used(alias).filter((k) => !(k in names)));
const owner = (aliases, name, value) => aliases.find((a) => BORROWED[a][name] === value);
fail('LK. names another fragment already owns (use its alias)', used('LK').map((k) => [k, owner(KEYS, k, camel(k))]).filter(([, a]) => a).map(([k, a]) => `${k} (${a}.)`));
const LVALUES = JSON.parse(readFileSync('Tools/loop-keys/values.json', 'utf8'));
fail('LV. names missing from Tools/loop-keys/values.json', used('LV').filter((k) => !(k in LVALUES)));
fail('LV. names another fragment already owns with the same value (use its alias)', used('LV').map((k) => [k, owner(VALUES, k, LVALUES[k])]).filter(([, a]) => a).map(([k, a]) => `${k} (${a}.)`));
fail('unused in Tools/loop-keys/values.json', Object.keys(LVALUES).filter((k) => !used('LV').includes(k)));
const LMESSAGES = JSON.parse(readFileSync('Tools/loop-keys/messages.json', 'utf8'));
fail('LM. names missing from Tools/loop-keys/messages.json', used('LM').filter((k) => !(k in LMESSAGES)));
fail('unused in Tools/loop-keys/messages.json', Object.keys(LMESSAGES).filter((k) => !used('LM').includes(k)));
if (failed) process.exit(1);

const out = {
  comment: 'Run-loop names (stream E, us-8.1): run-document, profile, rest-visit, reward-door, dungeon and rules/loopEngine.json keys, closed values and messages the C# port of the shipped main.js controller between fights reads that the combat, run, map and rewards fragments do not already name. Values are the shipped wire strings (D-037).',
  keySets: [
    { name: 'LoopKeys', doc: 'Keys of run and profile documents, outcomes, receipts and rules/loopEngine.json the run loop reads (shipped wire names).', keys: Object.fromEntries(used('LK').map((k) => [k, camel(k)])) },
    { name: 'LoopValues', doc: 'Closed run-loop values (node outcomes, rest actions, reward kinds and states, dungeon actions, unlock conditions, receipt kinds) — shipped wire strings.', keys: Object.fromEntries(used('LV').map((k) => [k, LVALUES[k]])) },
    { name: 'LoopMessages', doc: 'Run-loop error messages (developer-facing; {0}.. are string.Format slots).', keys: Object.fromEntries(used('LM').map((k) => [k, LMESSAGES[k]])) },
  ],
};
writeFileSync('Tools/codegen.d/loop.json', `${JSON.stringify(out, null, 2)}\n`);
console.log(Object.fromEntries(out.keySets.map((k) => [k.name, Object.keys(k.keys).length])));
