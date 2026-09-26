#!/usr/bin/env node
// Regenerates Tools/codegen.d/combat.json from the names the combat engine uses (D-041): keys, events, ops,
// predicates and formula ops are read off Ashen.Domain.Combat by their aliases (K./E./Op./Pr./F.) and spelled
// as their wire names; closed values and messages come from Tools/combat-keys/values.json and messages.json.
//   node Tools/combat-keys.mjs        (then node Tools/codegen.mjs)
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
const DIR = process.env.COMBAT_DIR || 'Unity/Assets/Game/Domain/Combat';
const src = readdirSync(DIR).filter(f => f.endsWith('.cs')).map(f => readFileSync(`${DIR}/${f}`, 'utf8')).join('\n');
const used = (alias) => [...new Set([...src.matchAll(new RegExp(`(?<![A-Za-z0-9_.])${alias}[.]([A-Z][A-Za-z0-9]*)`, 'g'))].map(m => m[1]))].sort();
const camel = (s) => s[0].toLowerCase() + s.slice(1);
const auto = (alias) => Object.fromEntries(used(alias).map(k => [k, camel(k)]));
const out = { comment: 'Combat engine names (stream B, us-5.1): state/effect/event keys, opcodes, predicates, closed values and messages the C# port of engine/combat.js reads. Values are the shipped wire strings (D-037).', keySets: [], numberSets: [] };
out.keySets.push({ name: 'CombatKeys', doc: 'Keys of combat documents, content rows and rules the combat engine reads (shipped wire names).', keys: auto('K') });
out.keySets.push({ name: 'CombatEvents', doc: 'Combat bus event types (shipped TRIGGER_EVENTS subset the engine emits or hooks).', keys: auto('E') });
out.keySets.push({ name: 'CombatOps', doc: 'Combat effect opcodes the interpreter implements (engine/actions.js runOpcode).', keys: auto('Op') });
out.keySets.push({ name: 'CombatPredicates', doc: 'Trigger/effect predicate names (engine/triggers.js evalPredicate).', keys: auto('Pr') });
out.keySets.push({ name: 'FormulaOps', doc: 'Formula ops (model/formulas.js FORMULA_OPS).', keys: auto('F') });
const V = JSON.parse(readFileSync('Tools/combat-keys/values.json', 'utf8'));
const usedV = used('V'); const missV = usedV.filter(k => !(k in V)); if (missV.length) { console.error('missing V', missV); process.exitCode = 1; }
out.keySets.push({ name: 'CombatValues', doc: 'Closed combat values (entity kinds, phases, reasons, zones, targets, separators) — shipped wire strings.', keys: Object.fromEntries(usedV.map(k => [k, V[k]])) });
const MM = JSON.parse(readFileSync('Tools/combat-keys/messages.json', 'utf8'));
const usedM = used('M'); const missM = usedM.filter(k => !(k in MM)); if (missM.length) { console.error('missing M', missM); process.exitCode = 1; }
out.keySets.push({ name: 'CombatMessages', doc: 'Combat engine error messages (developer-facing; {0}.. are string.Format slots).', keys: Object.fromEntries(usedM.map(k => [k, MM[k]])) });
const P = { CostAction: 'cost.action', CostMana: 'cost.mana', CostStamina: 'cost.stamina', UtilityEvasion: 'utility.evasion', LifecycleInnate: 'lifecycle.innate', InternalUnplayable: 'internal.unplayable', LifecycleExhaust: 'lifecycle.exhaust', LifecycleRecallAfterUse: 'lifecycle.recall.afterUse', ClassificationPower: 'classification.power', LifecycleRetain: 'lifecycle.retain', LifecycleEthereal: 'lifecycle.ethereal' };
const usedP = used('P'); const missP = usedP.filter(k => !(k in P)); if (missP.length) { console.error('missing P', missP); process.exitCode = 1; }
out.keySets.push({ name: 'CardPropertyIds', doc: 'Framework property ids whose semantics the engine implements (framework lifecycle/costs contract).', keys: Object.fromEntries(usedP.map(k => [k, P[k]])) });
out.keySets.push({ name: 'CombatPatterns', doc: 'Patterns the combat port parses (shipped parseMod, tokens.js TOKEN_PATTERN, the legacy relic gate key).', keys: {
  CardMod: String.raw`^(?<prefix>[A-Za-z]\w*)\.(?<field>[A-Za-z]\w*)=(?<sign>[+-]?)(?<num>\d+(?:\.\d+)?)$`,
  TextToken: String.raw`\{(?<token>[A-Za-z][\w.]*)\}`,
  LegacyRelicGate: String.raw`^relic:(?<owner>.+):(?<relic>[^:]+):(?<index>\d+)$`,
  ArmamentRef: String.raw`^armament/(?<id>[^/]+)$`,
  ArmorRef: String.raw`^armor/(?<classId>[^/]+)/(?<id>[^/]+)$`,
  RelicRef: String.raw`^relic/(?<id>[^/]+)$`,
} });
out.numberSets.push({ name: 'CombatMath', type: 'double', doc: 'Arithmetic constants of the combat rules (definitions, not tuning): percent scale, the float-floor epsilon, JS safe-integer bits.', values: { Percent: '100', Epsilon: '1e-9', Two: '2', Half: '0.5', SafeIntegerBits: '53' } });
writeFileSync('Tools/codegen.d/combat.json', JSON.stringify(out, null, 2) + '\n');
console.log(Object.fromEntries(out.keySets.map(k => [k.name, Object.keys(k.keys).length])));
