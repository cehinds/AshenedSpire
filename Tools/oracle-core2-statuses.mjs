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
// Status control: the build-up gauges, the recovery rolls, Sleep, the locks and the persistent Ward and Poise
// ---------------------------------------------------------------------------------------------------------------
const controlRules = createFight(w, createRun(w, 1, classes[0]), all[0]).combat.combatExpansionRules.statuses;
const GAUGES = Object.keys(controlRules.statuses);

function controlGrid() {
  for (const gauge of GAUGES) {
    const row = controlRules.statuses[gauge];
    for (const who of ['p', 'e0']) {
      const from = who === 'p' ? 'e0' : 'p';
      const reads = [{ op: 'read', who, read: 'restrictions' }, { op: 'read', who, read: 'controls' }, { op: 'read', who, read: 'chance', status: gauge }, { op: 'read', who, read: 'mods' }];
      // a ladder of pressure: below the first threshold, across it, to the cap and beyond
      let s = scenario(`pressure ${gauge} ${who}`);
      for (const units of [row.initialThreshold - 1, 1, row.activeThreshold, row.activeThreshold * 3, 40]) s.steps.push({ op: 'pressure', who, status: gauge, units, from }, { op: 'drain' });
      s.steps.push(...reads);
      // the recovery rolls of the owner's next turns
      for (let cycle = 2; cycle <= 7; cycle++) s.steps.push({ op: 'turn' }, { op: 'decayStart', who }, { op: 'recover', who, options: { cycle } }, { op: 'drain' }, { op: 'endControl', who }, { op: 'decayEnd', who });
      s.steps.push(...reads);
      // each application option: a camp, a resistance, advantage and disadvantage on the recovery
      for (const [label, options] of [['spell', { camp: 'spell' }], ['physical', { camp: 'physical' }], ['resist', { resistancePercent: 50 }], ['immune', { traits: { immuneStatuses: [gauge] } }], ['profile', { recoveryProfile: 'mental' }]]) {
        s = scenario(`pressure ${gauge} ${who} ${label}`);
        s.steps.push({ op: 'pressure', who, status: gauge, units: row.initialThreshold + 2, from, options }, { op: 'drain' }, { op: 'pressure', who, status: gauge, units: row.activeThreshold, from, options }, { op: 'drain' }, ...reads);
        s.steps.push({ op: 'turn' }, { op: 'recover', who, options: { cycle: 2, advantage: true } }, { op: 'recover', who, options: { cycle: 3, disadvantage: true } }, { op: 'drain' });
      }
      // Ward answers a Spell fill; Poise strain answers a physical one
      for (const [label, patch, options] of [['ward', { persistentWard: { max: 4, value: 4 } }, { camp: 'spell' }], ['poise', { poiseMeter: { max: 2, value: 0 } }, { camp: 'physical' }]]) {
        s = scenario(`pressure ${gauge} ${who} ${label}`, { patch: { [who]: patch } });
        for (let n = 0; n < 4; n++) s.steps.push({ op: 'pressure', who, status: gauge, units: row.initialThreshold, from, options }, { op: 'drain' });
        s.steps.push(...reads);
      }
      // the owner's traits: a recovery bonus and a recovery advantage
      s = scenario(`recover ${gauge} ${who} traits`, { patch: { [who]: { combatTraits: { recoveryBonus: { [row.profile]: 15 }, recoveryAdvantage: [row.profile], recoveryDisadvantage: ['mental'] } } } });
      s.steps.push({ op: 'pressure', who, status: gauge, units: row.initialThreshold * 2, from }, { op: 'drain' });
      for (let cycle = 2; cycle <= 6; cycle++) s.steps.push({ op: 'recover', who, options: { cycle } }, { op: 'drain' }, { op: 'endControl', who });
    }
    // an active status applied straight (a card that names it), then recovered by hand
    if (row.lock) {
      for (const who of ['p', 'e0']) {
        const s = scenario(`lock ${gauge} ${who}`, { patch: { [who]: who === 'p' ? { stamina: 6, energy: 6 } : { stamina: 6 } } });
        s.steps.push({ op: 'apply', who, status: row.activeStatus, stacks: 2, from: who === 'p' ? 'e0' : 'p' }, { op: 'drain' }, { op: 'read', who, read: 'restrictions' }, { op: 'read', who, read: 'controls' },
          { op: 'read', who, read: 'gate', card: {} }, { op: 'read', who, read: 'gate', card: { usableWhile: [row.activeStatus] } }, { op: 'read', who, read: 'gate', card: { builtinRecovery: true } },
          { op: 'manual', who, selections: [] }, { op: 'manual', who, selections: [{ status: row.activeStatus, stacks: 5 }] }, { op: 'manual', who, selections: [{ status: row.activeStatus, stacks: 1 }] }, { op: 'drain' },
          { op: 'read', who, read: 'restrictions' }, { op: 'endControl', who, selected: row.activeStatus }, { op: 'turn' }, { op: 'recover', who, options: { cycle: 2 } }, { op: 'sleep', who }, { op: 'actionLoss', who },
          { op: 'endControl', who }, { op: 'turn' }, { op: 'recover', who, options: { cycle: 3 } }, { op: 'actionLoss', who }, { op: 'read', who, read: 'restrictions' });
      }
    }
  }
}

function controlInteractions() {
  const i = controlRules.interactions;
  for (const who of ['p', 'e0']) {
    const from = who === 'p' ? 'e0' : 'p';
    // Sleep: the damage it invites, the Health and Ward it restores, waking when hit
    let s = scenario(`sleep ${who}`, { patch: { [who]: { hp: 20 } } });
    s.steps.push({ op: 'apply', who, status: 'sleep', stacks: 3, from }, { op: 'drain' }, { op: 'read', who, read: 'incoming', profile: {} }, { op: 'sleep', who }, { op: 'sleep', who },
      { op: 'complete', who, profile: {}, actionKey: 'a1' }, { op: 'complete', who, profile: {}, actionKey: 'a1' }, { op: 'apply', who, status: 'sleep', stacks: 2, from }, { op: 'turn' }, { op: 'recover', who, options: { cycle: 2 } },
      { op: 'sleep', who, options: { modifierPercent: 50 } }, { op: 'complete', who, profile: {}, actionKey: 'a2' }, { op: 'read', who, read: 'incoming', profile: {} });
    // Prone: melee and ranged hits, Reach and Ranged evade, area and projectile
    s = scenario(`prone ${who}`);
    s.steps.push({ op: 'apply', who, status: 'prone', stacks: 1, from }, { op: 'drain' });
    for (const profile of [{ range: 'melee' }, { range: 'reach', camp: 'physical' }, { range: 'ranged', camp: 'physical' }, { range: 'ranged', camp: 'spell', targeting: 'area' }, { range: 'ranged', camp: 'physical', targeting: 'area', projectile: true }, { range: 'melee', damageType: 'blunt' }])
      s.steps.push({ op: 'read', who, read: 'incoming', profile });
    s.steps.push({ op: 'read', who, read: 'controls' });
    // Frozen, Chilled, fire and blunt: thawing, shattering
    s = scenario(`frozen ${who}`);
    s.steps.push({ op: 'apply', who, status: 'frozen', stacks: 2, from }, { op: 'apply', who, status: 'chilled', stacks: 2, from }, { op: 'drain' }, { op: 'read', who, read: 'incoming', profile: { damageType: 'blunt' } },
      { op: 'actionLoss', who }, { op: 'actionLoss', who }, { op: 'before', who, profile: { damageType: 'blunt' } }, { op: 'complete', who, profile: { damageType: 'blunt' }, actionKey: 'b1' },
      { op: 'complete', who, profile: { damageType: 'blunt' }, actionKey: 'b2' }, { op: 'before', who, profile: { damageType: 'fire' }, connected: false }, { op: 'before', who, profile: { damageType: 'fire' } }, { op: 'read', who, read: 'restrictions' });
    s = scenario(`frost gauge ${who}`);
    s.steps.push({ op: 'pressure', who, status: 'frost', units: 8, from }, { op: 'drain' }, { op: 'before', who, profile: { containsFire: true } }, { op: 'before', who, profile: { school: 'fire' } }, { op: 'read', who, read: 'mods' });
    // Reveal: Decoy and Concealed
    s = scenario(`reveal ${who}`);
    s.steps.push({ op: 'apply', who, status: 'decoy', stacks: 2, from }, { op: 'apply', who, status: 'concealed', stacks: 1, from }, { op: 'drain' }, { op: 'complete', who, profile: { maneuver: 'sweep' }, actionKey: 'r1' },
      { op: 'apply', who, status: 'concealed', stacks: 1, from }, { op: 'complete', who, profile: { damageType: 'sacred' }, actionKey: 'r2' }, { op: 'apply', who, status: 'decoy', stacks: 1, from }, { op: 'complete', who, profile: { containsReveal: true }, actionKey: 'r3', damaging: false },
      { op: 'complete', who, profile: { containsReveal: true }, actionKey: 'r4' });
    // Grounded and the electrical traits
    s = scenario(`grounded ${who}`, { patch: { [who]: { combatTraits: { conductive: true } } } });
    s.steps.push({ op: 'read', who, read: 'electrical' }, { op: 'pressure', who, status: 'paralysis', units: 8, from }, { op: 'drain' }, { op: 'apply', who, status: 'grounded', stacks: 1, from }, { op: 'drain' }, { op: 'read', who, read: 'electrical' },
      { op: 'ground', who }, { op: 'apply', who, status: 'paralysis', stacks: 1, from }, { op: 'ground', who, clear: true }, { op: 'read', who, read: 'restrictions' }, { op: 'turn' }, { op: 'recover', who, options: { cycle: 2 } }, { op: 'drain' });
    // an Insulated owner and a Resolve turn
    s = scenario(`resolve ${who}`, { patch: { [who]: { combatTraits: { insulated: true } } } });
    s.steps.push({ op: 'read', who, read: 'electrical' }, { op: 'apply', who, status: 'dazed', stacks: 1, from }, { op: 'drain' }, { op: 'endControl', who }, { op: 'turn' }, { op: 'recover', who, options: { cycle: 2 } }, { op: 'read', who, read: 'restrictions' },
      { op: 'actionLoss', who }, { op: 'endControl', who }, { op: 'read', who, read: 'restrictions' });
  }
}

function wardGrid() {
  for (const who of ['p', 'e0']) {
    const from = who === 'p' ? 'e0' : 'p';
    let s = scenario(`ward spend ${who}`, { patch: { [who]: { persistentWard: { max: 6, value: 6 } } } });
    for (const amount of [1, 2, 4, 9]) s.steps.push({ op: 'spendWard', who, amount, meta: { status: 'frost', sourceId: from } }, { op: 'drain' });
    s.steps.push({ op: 'restoreWard', who, nominal: 3, options: { source: 'block-card', modifierPercent: 0 } }, { op: 'restoreWard', who, nominal: 5, options: { source: 'spell', modifierPercent: 50, cap: 2, onceKey: 'k1' } },
      { op: 'restoreWard', who, nominal: 5, options: { source: 'spell', onceKey: 'k1' } }, { op: 'breakWard', who, meta: {} }, { op: 'drain' });
    s = scenario(`ward break ${who}`, { patch: { [who]: { persistentWard: { max: 5, value: 5 } } } });
    s.steps.push({ op: 'breakWard', who, meta: { sourceId: from, cause: 'attack' } }, { op: 'drain' }, { op: 'restoreWard', who, nominal: 4, options: { source: 'sleep' } }, { op: 'drain' });
    s = scenario(`poise restore ${who}`, { patch: { [who]: { persistentPoise: { max: 8, value: 2 } } } });
    for (const nominal of [3, 8, 2]) s.steps.push({ op: 'restorePoise', who, nominal, options: { source: 'block-card' } });
    s.steps.push({ op: 'restorePoise', who, nominal: 4, options: { source: 'stance', modifierPercent: 25, cap: 3, onceKey: 'p1' } }, { op: 'restorePoise', who, nominal: 4, options: { source: 'stance', onceKey: 'p1' } });
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
controlGrid();
controlInteractions();
wardGrid();
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
