#!/usr/bin/env node
// Core 2 statuses oracle (WP-9, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED src/engine/statuses.js (applyStatus, removeStatus,
// decayAtTurnStart, decayAtTurnEnd, getMult / getAdd / getFlag / getCap), src/engine/combatStatusControl.js (the build-up gauges, the recovery
// rolls, Sleep, the control locks and their recovery), src/model/fixCoreMechanics.js (fixCoreStatusDefinition), src/engine/triggers.js
// (owner hooks and the event bus), src/engine/actions.js (executeAction: the effects a status asks for) and the card-trait math
// (src/engine/combatCardTraitMath.js, src/content/traitProjection.js) at the pinned sha, on real Core 2 fights (createRunCombat for the
// configured `default` profile) with a declared overlay on the fighters.
//
//   node Tools/oracle-core2-statuses.mjs [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/statuses/, sorted keys, two runs are byte-identical):
//   defs.json           for every status id of the registry (101): fixCoreStatusDefinition (the FixCore override and the facts laid over the row)
//                       and the modifier reads (getMult / getAdd / getFlag / getCap over every modifier key) of an owner holding 1, 2 and 3 stacks.
//   scenarios-<n>.json  scripted scenarios. A scenario is a fight (seed, class, encounter), an overlay (`patch` on the fighters, `fields` on the
//                       combat, `rules` on the fixCoreRules snapshot) and `steps`. A step is one call of the engine (apply, remove, decayStart,
//                       decayEnd, hooks, emit, drain, pressure, recover, sleep, endControl, actionLoss, before, complete, ground, manual, spendWard,
//                       restoreWard, restorePoise, read ...). Per step: the call's return value, the error it threw, the events (type and payload),
//                       the actions it queued, and the full combat projection as a delta. The test plays the same steps on the C# port.
//   traits.json         the card-trait math: the projection (applyCardTraits) over every card of the registry, the Counterstrike percentages and
//                       amounts, the Impact and Flux amounts, the stance advantage table.
//   fights-<class>.json real fights with the statuses and the new effect ops live (see THE FILTER in the fight section).
import { writeFileSync, readFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, projectFull, delta, SOURCES } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'statuses');
const PER_FILE = 60;
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const S = await load('src/engine/statuses.js');
const Control = await load('src/engine/combatStatusControl.js');
const triggers = await load('src/engine/triggers.js');
const actions = await load('src/engine/actions.js');
const fixcore = await load('src/model/fixCoreMechanics.js');
const schemas = await load('src/model/schemas.js');
const traitMath = await load('src/engine/combatCardTraitMath.js');
const traitProjection = await load('src/content/traitProjection.js');
const traitRules = await load('src/content/combatCardTraits.js');
const all = encounters(w);
const classes = classIds(w);
const statusIds = w.registries.statuses.all().map((s) => s.id);
const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

export function merge(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete target[key];
    else if (isObject(value) && isObject(target[key])) merge(target[key], value);
    else target[key] = structuredClone(value);
  }
  return target;
}

// ---------------------------------------------------------------------------------------------------------------
// The scenario player
// ---------------------------------------------------------------------------------------------------------------
const entityOf = (c, who) => (who === 'p' ? c.player : who === null || who === undefined ? null : c.enemies[Number(who.slice(1))]);

function build(spec) {
  const run = createRun(w, spec.seed, spec.classId);
  const encounter = all.find((e) => e.id === spec.encounterId);
  const { combat } = createFight(w, run, encounter);
  delete combat.rangeState; // range lineage (WP-12) is not part of the statuses oracle
  if (spec.fields) merge(combat, spec.fields);
  if (spec.rules) merge(combat.fixCoreRules, spec.rules);
  if (spec.statusRules) merge(combat.combatExpansionRules.statuses, spec.statusRules);
  for (const [who, patch] of Object.entries(spec.patch || {})) merge(entityOf(combat, who), patch);
  return combat;
}

/** Runs the queue to empty one action at a time, as drainQueue does but without the end-of-fight and reaction checks. */
function drain(c) {
  const ran = [];
  let guard = 0;
  while (c.queue.length) {
    if (++guard > 200) throw new Error('queue did not drain');
    const action = c.queue.shift();
    ran.push({ op: action.effect.op, source: action.source?.id ?? null, target: action.target?.id ?? null });
    actions.executeAction(c, action);
    if (c.result) { c.queue.length = 0; break; }
  }
  return ran;
}

const MODIFIERS = schemas.MODIFIER_KEYS;

function read(c, step) {
  const e = entityOf(c, step.who);
  switch (step.read) {
    case 'mods': return Object.fromEntries(MODIFIERS.map((key) => [key, { mult: S.getMult(c, e, key), add: S.getAdd(c, e, key), flag: S.getFlag(c, e, key), cap: S.getCap(c, e, key), any: S.anyCombatantFlag(c, key) }]));
    case 'stacks': return S.getStacks(e, step.status);
    case 'definition': return S.statusDefinition(c, step.status);
    case 'restrictions': return Control.controlRestrictions(c, e);
    case 'gate': return Control.controlGate(c, e, step.card || {});
    case 'controls': return Control.recoveryControls(c, e);
    case 'chance': return Control.recoveryChance(c, e, step.status, step.options || {});
    case 'effectiveChance': return Control.effectiveChance(step.chance, step.options || {});
    case 'incoming': return Control.statusIncomingModifier(c, e, step.profile || {});
    case 'electrical': return Control.electricalTraits(c, e);
    case 'rules': return fixcore.fixCoreRules(c, e);
    default: throw new Error(`unknown read ${step.read}`);
  }
}

/** `$e0` in a payload names the first enemy's id. */
function resolveIds(c, value) {
  if (typeof value === 'string') return /^\$e\d+$/.test(value) ? c.enemies[Number(value.slice(2))].id : value;
  if (Array.isArray(value)) return value.map((v) => resolveIds(c, v));
  if (isObject(value)) return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, resolveIds(c, v)]));
  return value;
}

function run(c, step) {
  const e = entityOf(c, step.who);
  const from = entityOf(c, step.from);
  switch (step.op) {
    case 'apply': return S.applyStatus(c, e, step.status, step.stacks, from, step.tags ? { tags: step.tags } : {});
    case 'remove': return S.removeStatus(c, e, step.status, { ...(step.reason ? { reason: step.reason } : {}), ...(step.amount !== undefined ? { amount: step.amount } : {}), ...(step.silent ? { silent: true } : {}) });
    case 'decayStart': return S.decayAtTurnStart(c, e);
    case 'decayEnd': return S.decayAtTurnEnd(c, e);
    case 'turn': c.turn += step.by ?? 1; return c.turn;
    case 'hooks': return triggers.fireOwnerHooks(c, e, step.hook);
    case 'emit': triggers.emitEvent(c, step.type, resolveIds(c, structuredClone(step.payload || {}))); return null;
    case 'drain': return drain(c);
    case 'set': merge(e, structuredClone(step.patch)); return null;
    case 'setField': merge(c, structuredClone(step.patch)); return null;
    case 'pressure': return Control.applyStatusPressure(c, e, step.status, step.units, from, structuredClone(step.options || {}));
    case 'recover': return Control.recoverStatusesAtOwnerStart(c, e, structuredClone(step.options || {}));
    case 'sleep': return Control.sleepRestoration(c, e, structuredClone(step.options || {}));
    case 'endControl': return Control.endControlTurn(c, e, step.selected ?? null);
    case 'actionLoss': return Control.consumeControlActionLoss(c, e);
    case 'before': return Control.beforeStatusAction(c, e, structuredClone(step.profile || {}), { connected: step.connected !== false });
    case 'complete': return Control.completeStatusAction(c, e, structuredClone(step.profile || {}), { connected: step.connected !== false, actionKey: step.actionKey, damaging: step.damaging !== false });
    case 'ground': return Control.groundStatusPressure(c, e, { clearActiveStacks: !!step.clear });
    case 'manual': return Control.manualRecovery(c, e, structuredClone(step.selections), structuredClone(step.options || {}));
    case 'spendWard': return Control.spendPersistentWard(c, e, step.amount, structuredClone(step.meta || {}));
    case 'restoreWard': return Control.restorePersistentWard(c, e, step.nominal, structuredClone(step.options || {}));
    case 'restorePoise': return Control.restorePersistentPoise(c, e, step.nominal, structuredClone(step.options || {}));
    case 'breakWard': return Control.breakPersistentWard(c, e, structuredClone(step.meta || {}));
    case 'read': return read(c, step);
    default: throw new Error(`unknown step ${step.op}`);
  }
}

const queueRows = (c) => c.queue.map((a) => canon({ effect: a.effect, source: a.source?.id ?? null, target: a.target?.id ?? null, killReceipt: a.meta?.killReceipt ?? null }));

/** Plays a scenario; returns the opening projection and one row per step. */
export function play(spec) {
  const c = build(spec);
  let last = projectFull(c);
  const start = last;
  const rows = [];
  for (const step of spec.steps) {
    const mark = c.eventLog.length;
    const row = { step };
    try { row.result = canon({ v: run(c, step) }).v ?? null; } catch (e) { row.error = e.message; }
    const state = projectFull(c);
    row.events = canon(c.eventLog.slice(mark));
    row.queue = queueRows(c);
    row.delta = delta(last, state) ?? {};
    rows.push(row);
    last = state;
  }
  return { steps: rows };
}

// ---------------------------------------------------------------------------------------------------------------
// defs.json
// ---------------------------------------------------------------------------------------------------------------
function defsFile() {
  const rows = {};
  const spec = { seed: 4001, classId: classes[0], encounterId: all[0].id, steps: [] };
  const c = build(spec);
  for (const id of statusIds) {
    const row = { definition: canon(S.statusDefinition(c, id)), base: canon(c.registries.statuses.get(id)), mods: {} };
    for (const stacks of [1, 2, 3]) {
      const cc = build(spec);
      cc.player.statuses = { [id]: { stacks } };
      cc.enemies[0].statuses = { [id]: { stacks } };
      row.mods[stacks] = canon({ player: read(cc, { read: 'mods', who: 'p' }), enemy: read(cc, { read: 'mods', who: 'e0' }) });
    }
    rows[id] = row;
  }
  const rules = canon(fixcore.fixCoreRules(c));
  return { spec, statuses: rows, rules: rules.statuses, modifierKeys: MODIFIERS };
}

// ---------------------------------------------------------------------------------------------------------------
// Scenario generators
// ---------------------------------------------------------------------------------------------------------------
const scenarios = [];
const seedFor = (n) => w.rngMod.sweepSeed(39000 + n);
let counter = 0;
function scenario(name, extra) {
  const n = counter++;
  const spec = { name, seed: seedFor(n), classId: classes[n % classes.length], encounterId: all[(n * 3) % all.length].id, steps: [], ...extra };
  scenarios.push(spec);
  return spec;
}

const STACKS = [[1], [3], [2, 2], [0], [-1], [6]];
const PLAIN_STACKS = [[1], [2, 2], [0]];
const activeRows = new Set(Object.values(createFight(w, createRun(w, 1, classes[0]), all[0]).combat.combatExpansionRules.statuses.statuses).map((r) => r.activeStatus));
// A status with nothing but a stack mode and no decay needs fewer sequences than one with a hook, a modifier, a proc or a clock.
const plain = (id) => { const d = w.registries.statuses.get(id); return d.decay === 'none' && !d.hooks && !d.modifiers && !d.proc && !d.resists && !d.taggedVulnerability && !d.fluxPerTurn && !d.schoolDamageVulnerability && !activeRows.has(id) && id !== 'precision' && id !== 'evade'; };

function applyGrid() {
  for (const id of statusIds) {
    for (const who of ['p', 'e0']) {
      (plain(id) ? PLAIN_STACKS : STACKS).forEach((seq) => {
        const s = scenario(`apply ${id} ${who} ${seq.join(',')}`);
        for (const n of seq) s.steps.push({ op: 'apply', who, status: id, stacks: n, from: who === 'p' ? 'e0' : 'p' }, { op: 'drain' });
        // the owner's clocks: start of its turn, its hooks, end of its turn; a second round for a clock that counts down
        for (let round = 0; round < (plain(id) ? 1 : 2); round++) {
          s.steps.push({ op: 'turn' }, { op: 'decayStart', who }, { op: 'hooks', who, hook: 'ownerTurnStart' }, { op: 'drain' }, { op: 'hooks', who, hook: 'ownerTurnEnd' }, { op: 'drain' }, { op: 'decayEnd', who });
        }
        s.steps.push({ op: 'remove', who, status: id, reason: 'consumed', amount: 1 }, { op: 'remove', who, status: id, reason: 'removed' });
      });
    }
  }
}

function eventGrid() {
  // the bus: a status's hook fires on the event it names
  const events = [
    ['cardPlayed', { cardType: 'attack', cardTags: [], sourceId: 'player' }],
    ['cardPlayed', { cardType: 'skill', cardTags: [], sourceId: 'player' }],
    ['hpLost', { targetId: 'player', amount: 3 }],
    ['healed', { targetId: 'player', amount: 3 }],
    ['damageDealt', { sourceId: 'player', targetId: '$e0', amount: 6, blocked: 0, isAttack: true }],
    ['damageDealt', { sourceId: '$e0', targetId: 'player', amount: 6, blocked: 0, isAttack: true }],
    ['enemyDied', { targetId: '$e0' }],
    ['enemyStaggered', { targetId: '$e0' }],
    ['statusApplied', { targetId: 'player', sourceId: '$e0', status: 'weak', stacks: 1 }],
    ['meterFilled', { targetId: 'player', status: 'x' }],
  ];
  const hooked = statusIds.filter((id) => (w.registries.statuses.get(id).hooks || []).some((h) => !h.on.startsWith('owner')));
  for (const id of hooked) {
    for (const who of ['p', 'e0']) {
      for (const [type, payload] of events) {
        const hooks = w.registries.statuses.get(id).hooks || [];
        if (!hooks.some((h) => h.on === type)) continue;
        const s = scenario(`event ${id} ${who} ${type}`);
        s.steps.push({ op: 'apply', who, status: id, stacks: 2 }, { op: 'drain' });
        for (const times of [1, 2, 3]) s.steps.push({ op: 'emit', who, type, payload: payload, resolve: true }, { op: 'drain' });
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Write
// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const write = (file, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, file), text); return Buffer.byteLength(text); };
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, files: [] };
let total = 0;

let bytes = write('defs.json', defsFile());
total += bytes; index.defs = { file: 'defs.json', statuses: statusIds.length, bytes };

applyGrid();
eventGrid();
const recorded = [];
for (const spec of scenarios) {
  const played = play(spec);
  recorded.push({ ...spec, ...played });
}
for (let f = 0; f * PER_FILE < recorded.length; f++) {
  const part = recorded.slice(f * PER_FILE, (f + 1) * PER_FILE);
  const file = `scenarios-${f}.json`;
  bytes = write(file, { scenarios: part });
  total += bytes;
  index.files.push({ file, scenarios: part.length, bytes });
}
index.scenarios = recorded.length;
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-statuses: ${statusIds.length} statuses, ${recorded.length} scenarios, ${(total / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/statuses`);
