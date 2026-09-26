#!/usr/bin/env node
// Regenerates Tools/codegen.d/shop.json from the names the merchant and events ports use (us-9.1, us-10.1).
// Ashen.Domain.Shop reads its own names through SK./SV./SM. (ShopKeys, ShopValues, ShopMessages) and Ashen.Domain.Events
// through EK./EV./EM. (EventKeys, EventValues, EventMessages); both reuse the combat, run, rewards and map names through
// their aliases (K./E./V./M./Op./Pr./F. = CombatKeys/Events/Values/Messages/Ops/Predicates/FormulaOps, RK./RV. =
// RunKeys/RunValues, WK./WV./WM. = RewardsKeys/Values/Messages, MK./MV. = MapKeys/MapValues), and events reuse the shop
// names through SK./SV./SM. A name that already exists in a fragment it may reuse must use that alias (no duplicate
// identifiers across fragments), and every reused name must exist there. String tables: strings/shop.en.json →
// ShopStringKeys (SS.), strings/events.en.json → EventStringKeys (ES.).
//   node Tools/shop-keys.mjs        (then node Tools/codegen.mjs)
import { readFileSync, readdirSync, writeFileSync, existsSync } from 'node:fs';

const SHOP_DIR = 'Unity/Assets/Game/Domain/Shop';
const EVENTS_DIR = 'Unity/Assets/Game/Domain/Events';
const read = (dir) => (existsSync(dir) ? readdirSync(dir).filter((f) => f.endsWith('.cs')).map((f) => readFileSync(`${dir}/${f}`, 'utf8')).join('\n') : '');
const shopSrc = read(SHOP_DIR);
const eventsSrc = read(EVENTS_DIR);
const both = `${shopSrc}\n${eventsSrc}`;
const usedIn = (src, alias) => [...new Set([...src.matchAll(new RegExp(`(?<![A-Za-z0-9_.])${alias}[.]([A-Z][A-Za-z0-9]*)`, 'g'))].map((m) => m[1]))].sort();
const camel = (s) => s[0].toLowerCase() + s.slice(1);
const fragment = (file) => JSON.parse(readFileSync(`Tools/codegen.d/${file}`, 'utf8'));
const set = (frag, name) => (frag.keySets.find((k) => k.name === name) || { keys: {} }).keys;
const combat = fragment('combat.json');
const run = fragment('run.json');
const rewards = fragment('rewards.json');
const map = fragment('map.json');
const REUSE = {
  K: set(combat, 'CombatKeys'), E: set(combat, 'CombatEvents'), V: set(combat, 'CombatValues'), M: set(combat, 'CombatMessages'),
  Op: set(combat, 'CombatOps'), Pr: set(combat, 'CombatPredicates'), F: set(combat, 'FormulaOps'),
  RK: set(run, 'RunKeys'), RV: set(run, 'RunValues'), WK: set(rewards, 'RewardsKeys'), WV: set(rewards, 'RewardsValues'), WM: set(rewards, 'RewardsMessages'),
  MK: set(map, 'MapKeys'), MV: set(map, 'MapValues'),
};
let failed = false;
const fail = (what, list) => { if (list.length) { console.error(`${what}: ${list.join(', ')}`); failed = true; } };
for (const [alias, keys] of Object.entries(REUSE)) fail(`${alias}. names missing from their key set`, usedIn(both, alias).filter((k) => !(k in keys)));

const KEYSETS = ['K', 'RK', 'WK', 'MK'];
const VALUESETS = ['V', 'RV', 'WV', 'MV'];
const values = JSON.parse(readFileSync('Tools/shop-keys/values.json', 'utf8'));
const messages = JSON.parse(readFileSync('Tools/shop-keys/messages.json', 'utf8'));

/** One port's own key/value/message sets, checked against every set it may reuse. */
function own(src, prefix, keyAlias, valueAlias, messageAlias, extraKeys = {}, extraValues = {}) {
  const keys = usedIn(src, keyAlias);
  for (const s of KEYSETS) fail(`${keyAlias}. names already in ${s}. (use ${s}.)`, keys.filter((k) => REUSE[s][k] === camel(k)));
  fail(`${keyAlias}. names already in SK. (use SK.)`, keys.filter((k) => extraKeys[k] === camel(k)));
  const vals = usedIn(src, valueAlias);
  fail(`${valueAlias}. names missing from Tools/shop-keys/values.json (${prefix})`, vals.filter((k) => !(k in (values[prefix] || {}))));
  for (const s of VALUESETS) fail(`${valueAlias}. names already in ${s}. with the same value (use ${s}.)`, vals.filter((k) => REUSE[s][k] !== undefined && REUSE[s][k] === (values[prefix] || {})[k]));
  fail(`${valueAlias}. names already in SV. with the same value (use SV.)`, vals.filter((k) => extraValues[k] !== undefined && extraValues[k] === (values[prefix] || {})[k]));
  const msgs = usedIn(src, messageAlias);
  fail(`${messageAlias}. names missing from Tools/shop-keys/messages.json (${prefix})`, msgs.filter((k) => !(k in (messages[prefix] || {}))));
  return {
    keys: Object.fromEntries(keys.map((k) => [k, camel(k)])),
    values: Object.fromEntries(vals.map((k) => [k, values[prefix][k]])),
    messages: Object.fromEntries(msgs.map((k) => [k, messages[prefix][k]])),
  };
}

const shop = own(shopSrc, 'shop', 'SK', 'SV', 'SM');
// Events reuse the shop's names (SK./SV./SM. in the events port must exist in the shop sets).
fail('SK. names used by events missing from ShopKeys', usedIn(eventsSrc, 'SK').filter((k) => !(k in shop.keys)));
fail('SV. names used by events missing from ShopValues', usedIn(eventsSrc, 'SV').filter((k) => !(k in shop.values)));
fail('SM. names used by events missing from ShopMessages', usedIn(eventsSrc, 'SM').filter((k) => !(k in shop.messages)));
const events = own(eventsSrc, 'events', 'EK', 'EV', 'EM', shop.keys, shop.values);
if (failed) process.exit(1);

const out = {
  comment: 'Merchant and event names (stream shop-events, us-9.1 / us-10.1): run-document, stock, receipt, balance and rules/shopEngine.json / rules/eventsEngine.json keys, closed values and messages the C# ports of the shipped merchant (main.js enterNode merchant, ui/screens/shop.js, model/armamentTrading.js, cardRemoval.js, smithing.js, cardExtraction.js) and events (engine/quests.js, model/quests.js, model/consequence.js, model/classSwap.js, engine/actions.js run opcodes) read that the combat, run, rewards and map fragments do not already name. Values are the shipped wire strings (D-037).',
  keySets: [
    { name: 'ShopKeys', doc: 'Keys of run documents, shop stock, receipts, balance tables and rules/shopEngine.json the merchant port reads (shipped wire names).', keys: shop.keys },
    { name: 'ShopValues', doc: 'Closed merchant values (actions, offer kinds, smith services, mount states, availability ids, instance-id formats) — shipped wire strings.', keys: shop.values },
    { name: 'ShopMessages', doc: 'Merchant error messages (developer-facing; {0}.. are string.Format slots).', keys: shop.messages },
    { name: 'EventKeys', doc: 'Keys of run documents, history rows, event content and rules/eventsEngine.json the events port reads (shipped wire names).', keys: events.keys },
    { name: 'EventValues', doc: 'Closed event values (history kinds, completion sources, binding reasons, requirement groups) — shipped wire strings.', keys: events.values },
    { name: 'EventMessages', doc: 'Event error messages (developer-facing; {0}.. are string.Format slots).', keys: events.messages },
  ].filter((k) => Object.keys(k.keys).length),
  stringTables: [
    { name: 'ShopStringKeys', from: 'strings/shop.en.json', doc: 'Merchant refusal string keys the domain returns (strings/shop.en.json); texts equal the shipped messages.' },
    ...(existsSync('Unity/Assets/StreamingAssets/Content/strings/events.en.json')
      ? [{ name: 'EventStringKeys', from: 'strings/events.en.json', doc: 'Event refusal and requirement string keys the domain returns (strings/events.en.json).' }]
      : []),
  ],
};
writeFileSync('Tools/codegen.d/shop.json', `${JSON.stringify(out, null, 2)}\n`);
console.log(Object.fromEntries(out.keySets.map((k) => [k.name, Object.keys(k.keys).length])));
