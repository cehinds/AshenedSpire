#!/usr/bin/env node
// Regenerates Tools/codegen.d/rewards.json from the names the post-combat port uses (us-11.1): Ashen.Domain.Rewards
// reads combat and run names through their aliases (K./E./V./M. = CombatKeys/Events/Values/Messages, RK./RV. =
// RunKeys/RunValues) and its own through WK./WV./WM. (RewardsKeys, RewardsValues, RewardsMessages). A rewards name
// that already exists in the combat or run fragment must use that alias (no duplicate identifiers across fragments),
// and every combat/run name it uses must exist there.
//   node Tools/rewards-keys.mjs        (then node Tools/codegen.mjs)
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';

const DIR = process.env.REWARDS_DIR || 'Unity/Assets/Game/Domain/Rewards';
const src = readdirSync(DIR).filter((f) => f.endsWith('.cs')).map((f) => readFileSync(`${DIR}/${f}`, 'utf8')).join('\n');
const used = (alias) => [...new Set([...src.matchAll(new RegExp(`(?<![A-Za-z0-9_.])${alias}[.]([A-Z][A-Za-z0-9]*)`, 'g'))].map((m) => m[1]))].sort();
const camel = (s) => s[0].toLowerCase() + s.slice(1);
const fragment = (file) => JSON.parse(readFileSync(`Tools/codegen.d/${file}`, 'utf8'));
const combat = fragment('combat.json');
const run = fragment('run.json');
const set = (frag, name) => (frag.keySets.find((k) => k.name === name) || { keys: {} }).keys;
const CK = set(combat, 'CombatKeys');
const CE = set(combat, 'CombatEvents');
const CV = set(combat, 'CombatValues');
const CM = set(combat, 'CombatMessages');
const RKS = set(run, 'RunKeys');
const RVS = set(run, 'RunValues');
let failed = false;
const fail = (what, list) => { if (list.length) { console.error(`${what}: ${list.join(', ')}`); failed = true; } };

fail('K. names missing from CombatKeys', used('K').filter((k) => !(k in CK)));
fail('E. names missing from CombatEvents', used('E').filter((k) => !(k in CE)));
fail('V. names missing from CombatValues', used('V').filter((k) => !(k in CV)));
fail('M. names missing from CombatMessages', used('M').filter((k) => !(k in CM)));
fail('RK. names missing from RunKeys', used('RK').filter((k) => !(k in RKS)));
fail('RV. names missing from RunValues', used('RV').filter((k) => !(k in RVS)));
fail('WK. names already in CombatKeys (use K.)', used('WK').filter((k) => CK[k] === camel(k)));
fail('WK. names already in RunKeys (use RK.)', used('WK').filter((k) => RKS[k] === camel(k)));
const WVALUES = JSON.parse(readFileSync('Tools/rewards-keys/values.json', 'utf8'));
fail('WV. names missing from Tools/rewards-keys/values.json', used('WV').filter((k) => !(k in WVALUES)));
fail('WV. names already in CombatValues with the same value (use V.)', used('WV').filter((k) => CV[k] === WVALUES[k]));
fail('WV. names already in RunValues with the same value (use RV.)', used('WV').filter((k) => RVS[k] === WVALUES[k]));
const WMESSAGES = JSON.parse(readFileSync('Tools/rewards-keys/messages.json', 'utf8'));
fail('WM. names missing from Tools/rewards-keys/messages.json', used('WM').filter((k) => !(k in WMESSAGES)));
if (failed) process.exit(1);

const out = {
  comment: 'Post-combat names (stream D, us-11.1): run-document, receipt, balance and rules/rewardsEngine.json keys, closed values and messages the C# port of main.js onCombatEnd (skills, class tree, level, smithing faucet, reward rolls) reads that the combat and run fragments do not already name. Values are the shipped wire strings (D-037).',
  keySets: [
    { name: 'RewardsKeys', doc: 'Keys of run documents, receipts, balance tables and rules/rewardsEngine.json the post-combat port reads (shipped wire names).', keys: Object.fromEntries(used('WK').map((k) => [k, camel(k)])) },
    { name: 'RewardsValues', doc: 'Closed post-combat values (door pools, track kinds, checkpoint doors, receipt outcomes) — shipped wire strings.', keys: Object.fromEntries(used('WV').map((k) => [k, WVALUES[k]])) },
    { name: 'RewardsMessages', doc: 'Post-combat error messages (developer-facing; {0}.. are string.Format slots).', keys: Object.fromEntries(used('WM').map((k) => [k, WMESSAGES[k]])) },
  ],
};
writeFileSync('Tools/codegen.d/rewards.json', `${JSON.stringify(out, null, 2)}\n`);
console.log(Object.fromEntries(out.keySets.map((k) => [k.name, Object.keys(k.keys).length])));
