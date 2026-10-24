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
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, projectFull, delta, botLcg, detach, SOURCES, answerer, dispatchAnswering, reactionFilter } from './oracle-core2.mjs';
import { whyTally } from './oracle-core2-why.mjs';

const OUT = join(ORACLE_DIR, 'statuses');
const PER_FILE = 60;
const FIGHTS = Number(arg('--fights', '3'));
const w = await loadWeb();
const REJ = whyTally('statuses');
await reactionFilter(w);
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const S = await load('src/engine/statuses.js');
const Control = await load('src/engine/combatStatusControl.js');
const triggers = await load('src/engine/triggers.js');
const actions = await load('src/engine/actions.js');
const fixcore = await load('src/model/fixCoreMechanics.js');
const schemas = await load('src/model/schemas.js');
const traitMath = await load('src/engine/combatCardTraitMath.js');
const matchups = await load('src/engine/combatMatchups.js');
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
  delete combat.rangeState; // a scenario isolates the status from the engagement
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
    case 'tacticalComplete': {
      const receipt = { completed: false, profiles: [], covered: [], ...structuredClone(step.receipt) };
      matchups.completeTacticalAction(c, e, entityOf(c, step.targetWho), structuredClone(step.carrier || {}), receipt, {});
      return null;
    }
    case 'effect': {
      const action = { effect: structuredClone(step.effect), source: e, owner: e, target: step.targetWho ? entityOf(c, step.targetWho) : null, ...(step.card ? { card: structuredClone(step.card) } : {}), meta: structuredClone(step.meta || {}) };
      actions.executeAction(c, action);
      return null;
    }
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
// Fights
// ---------------------------------------------------------------------------------------------------------------
// THE FILTER (WP-C0: none for the content a default run reaches). A step is recorded when the web accepts it, tried on a detached copy first; the player's
// reactions are ON (WP-10) and every offer a hit opens is answered by the bot (half the first option, half Back, from its own LCG, one recorded step each),
// the dry run answering with a copy of that LCG. Every card is played, its variants, scripts, stances, Counters, Burdens, Ashen Blight costs and
// cardPreparing listeners included; every enemy takes its turn. What the web refuses is tallied in `rejections` (`--why`). Statuses are laid on the
// fighters by `applyStatus` steps first, so the hooks, the clocks, the controls and the procs run on real turns.
const expansion = await load('src/engine/combatExpansion.js');
const tactics = await load('src/engine/combatCardTactics.js');
const typesSince = (c, mark) => c.eventLog.slice(mark).map((e) => e.type);
const bandOf = (combat, enemy) => combat.rangeState?.engagements?.[JSON.stringify([`enemy:${enemy.id}`, 'player:player'].sort())];

// The player's reactions are on in the fights. An offer a hit opens is answered by the bot (half the time it plays the first option, half the time it goes
// Back, from its own LCG), and a dry run answers with a copy of that LCG (ANSWERS, set by `fight`).
let ANSWERS = null;
function dryRun(combat, intent) {
  const r = dispatchAnswering(w, combat, intent, ANSWERS);
  if (r.error) return { ok: false, why: `throws: ${r.error}` };
  if (r.why) return REJ.why(r.why);
  return { ok: true };
}

const facesOf = (def) => [def.effects || [], ...(def.effectVariants || []).map((v) => v.effects || [])];
const safeStatus = () => true;
const RESTING = new Set(['staggered', 'base.stagger']);

function fight(seed, classId, encounter, bot, steps) {
  const run = createRun(w, seed, classId);
  const { combat } = createFight(w, run, encounter);
  const pick = botLcg(seed ^ 0x51ed270b);
  const answers = answerer(seed ^ 0x5a17c3e1);
  ANSWERS = answers;
  let last = projectFull(combat);
  const start = { events: typesSince(combat, 0), state: last };
  const record = [];
  const one = (intent, extra = {}) => {
    const mark = combat.eventLog.length;
    let error = null;
    try {
      if (intent.type === 'applyStatus') {
        S.applyStatus(combat, entityOf(combat, intent.who), intent.status, intent.stacks, entityOf(combat, intent.from));
        drain(combat);
      } else w.combat.dispatch(combat, structuredClone(intent));
    } catch (e) { error = e.message; }
    const state = projectFull(combat);
    record.push({ intent, ...extra, ...(error ? { error } : {}), events: typesSince(combat, mark), delta: delta(last, state) ?? {} });
    last = state;
    return !error;
  };
  // A step that opens a reaction offer is followed by the bot's answers, one step each.
  const step = (intent, extra = {}) => {
    if (!one(intent, extra)) return false;
    for (let n = 0; combat.pendingReaction && !combat.result; n++) {
      if (n > 60) return false;
      const answer = answers.next(combat);
      if (!one(answer, { answer: answer.optionId === null ? 'back' : 'play' })) return false;
    }
    return true;
  };
  // the opening statuses: a few on each side, chosen from those whose hooks stay inside the filter
  const pool = statusIds.filter((id) => !RESTING.has(id) && safeStatus(combat, id));
  const openers = bot === 'statuses' ? 5 : 2;
  for (let i = 0; i < openers; i++) {
    const id = pool[pick(pool.length)];
    const who = pick(3) === 0 ? 'p' : `e${pick(combat.enemies.length)}`;
    step({ type: 'applyStatus', who, status: id, stacks: 1 + pick(3), from: who === 'p' ? 'e0' : 'p' });
  }
  for (let i = 0; i < steps && !combat.result; i++) {
    if (combat.phase !== 'player') break;
    // a pending choice must be answered first
    if (combat.pendingCardChoice) {
      const ids = [...combat.pendingCardChoice.candidateIds];
      const chosen = [];
      while (chosen.length < combat.pendingCardChoice.count && ids.length) chosen.push(ids.splice(pick(ids.length), 1)[0]);
      if (!step({ type: 'chooseCards', cardInstanceIds: chosen })) break;
      continue;
    }
    if (combat.pendingAbilityDiscard) {
      const ids = combat.piles.hand.map((card) => card.instanceId);
      const chosen = [];
      while (chosen.length < combat.pendingAbilityDiscard.count && ids.length) chosen.push(ids.splice(pick(ids.length), 1)[0]);
      if (!step({ type: 'chooseDiscard', cardInstanceIds: chosen })) break;
      continue;
    }
    const options = [];
    {
      const targets = combat.enemies.filter((e) => e.alive).map((e) => e.id);
      const alive = combat.enemies.filter((e) => e.alive).map((e) => e.id);
      for (const card of combat.piles.hand) {
        const def = expansion.resolveCombatCard(combat, card);
        const faces = facesOf(def).flat();
        const strikes = faces.some((e) => ['damage', 'poiseDamage', 'wardDamage'].includes(e.op) && e.target !== 'self');
        if (strikes && !targets.length) continue;
        const aimed = faces.some((e) => e.target === 'enemy');
        const choices = faces.some((e) => e.op === 'enterStance' && e.choose === 'maneuver') ? ['attack', 'smash', 'defend', 'sweep', 'casting', 'evade'] : [undefined];
        for (const choice of choices) for (const targetId of aimed ? (strikes ? targets : alive) : [undefined]) {
          const intent = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}), ...(choice ? { choice } : {}) };
          if (dryRun(combat, intent).ok) options.push(intent);
        }
      }
    }
    // the built-in recoveries of a held creature
    const controls = Control.recoveryControls(combat, combat.player).filter((row) => !row.disabled && row.costPerStack <= combat.player.energy);
    if (controls.length && pick(2) === 0) {
      const intent = { type: 'recoverControl', selections: [{ status: controls[0].status, stacks: 1 }] };
      if (dryRun(combat, intent).ok) { if (!step(intent)) break; continue; }
    }
    let intent;
    if (options.length && pick(10) < 9) {
      // a card that does more than strike or guard is the point of this oracle: it is picked first most of the time
      const rich = options.filter((o) => { const card = combat.piles.hand.find((x) => x.instanceId === o.cardInstanceId); return facesOf(expansion.resolveCombatCard(combat, card)).flat().some((e) => !['damage', 'block', 'gainBarrier', 'draw'].includes(e.op)); });
      intent = rich.length && pick(10) < 7 ? rich[pick(rich.length)] : options[pick(options.length)];
    }
    else {
      intent = { type: 'endTurn', discardIds: [] };
      if (!dryRun(combat, intent).ok) break;
      if (!step(intent)) break;
      continue;
    }
    const played = intent.type === 'playCard' ? combat.piles.hand.find((card) => card.instanceId === intent.cardInstanceId) : null;
    const face = played ? expansion.resolveCombatCard(combat, played) : null;
    if (!step(intent, face ? { cardId: played.cardId, ops: facesOf(face).flat().map((e) => e.op) } : {})) break;
  }
  return { seed, classId, encounterId: encounter.id, bot, start, steps: record };
}

function fightFiles() {
  const out = [];
  classes.forEach((classId, ci) => {
    const fights = [];
    const pool = all.filter((e) => !/^a[23]_/.test(e.id));
    for (const bot of ['plays', 'statuses']) for (let n = 0; n < FIGHTS * 4; n++) {
      const encounter = pool[(ci * 5 + n * 7 + (bot === 'plays' ? 0 : 3)) % pool.length];
      const seed = w.rngMod.sweepSeed(41000 + ci * 100 + n + (bot === 'plays' ? 0 : 50));
      fights.push(fight(seed, classId, encounter, bot, 60));
    }
    out.push({ classId, fights });
  });
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// traits.json: the card-trait math and the projection over every card of the registry
// ---------------------------------------------------------------------------------------------------------------
const tags = await load('src/content/tags.js');
function traitsFile() {
  const tuned = structuredClone(traitRules.CARD_TRAIT_RULES);
  tuned.counterstrike = { pct: 0.4, advantagedPct: 0.9 };
  tuned.impact = { base: { smash: 3, default: 2 }, perTwoTiers: 2 };
  tuned.flux = { base: 2, perTwoTiers: 1 };
  tuned.advantage = { ...structuredClone(traitRules.CARD_TRAIT_RULES.advantage), casting: ['defend'], defend: ['sweep', 'attack'] };
  const ruleSets = { shipped: structuredClone(traitRules.CARD_TRAIT_RULES), tuned };
  const defs = {}, baseTags = {};
  const projection = [];
  for (const def of w.registries.cards.all()) {
    defs[def.id] = canon(def);
    baseTags[def.id] = tags.objectTagIds('card', def.id);
    for (const [name, rules] of Object.entries(ruleSets)) {
      const input = structuredClone(def);
      const out = traitProjection.applyCardTraits(input, structuredClone(rules));
      projection.push({ id: def.id, rules: name, tier: traitProjection.cardTier(def), traitValues: canon(out.traitValues ?? null), tags: canon(out === input ? null : out.tags), cardTags: canon(out.cardTags ?? null) });
    }
  }
  const maneuvers = [null, 'attack', 'smash', 'defend', 'sweep', 'counter', 'casting', 'ranged', 'evade', 'prepared'];
  const math = [];
  for (const [name, rules] of Object.entries(ruleSets)) {
    const ctx = { reactionRules: rules };
    for (const player of maneuvers) for (const target of maneuvers) {
      math.push({ rules: name, kind: 'advantage', player, target, value: traitMath.hasStanceAdvantage(ctx, player, target) });
      math.push({ rules: name, kind: 'pct', player, target, value: traitMath.counterstrikePct(ctx, player, target) });
    }
  }
  for (const amount of [-3, 0, 1, 2, 3, 4, 5, 7, 10, 13, 100, 0.5, 2.5, null, 'x', true]) for (const pct of [0.4, 0.5, 0.75, 0.9, 1])
    math.push({ kind: 'amount', amount, pct, value: traitMath.counterstrikeAmount(amount, pct) });
  for (const own of [-1, 0, 1, 3, 10]) for (const n of [-2, 0, 1, 2, 5]) math.push({ kind: 'hit', own, n, value: traitMath.traitHitAmount(own, n) });
  const entities = [null, {}, { combatStance: { maneuver: 'smash' } }, { intent: { combatProfile: { maneuver: 'sweep' } } }, { combatProfile: { maneuver: 'counter' } },
    { combatStance: { maneuver: 'attack' }, intent: { combatProfile: { maneuver: 'sweep' } } }, { combatStance: {}, intent: { combatProfile: { maneuver: 'defend' } }, combatProfile: { maneuver: 'counter' } }];
  entities.forEach((entity, index) => { for (const resolving of [null, 'sweep']) math.push({ kind: 'maneuver', index, entity, resolving, value: traitMath.stanceManeuverOf(entity, resolving) }); });
  return { rules: canon(ruleSets), defs, baseTags, projection, math: canon(math) };
}

// ---------------------------------------------------------------------------------------------------------------
// The effect ops: each op of the DSL that this package ports, run on its own through the interpreter with the variants it reads
// ---------------------------------------------------------------------------------------------------------------
const cardIds = w.registries.cards.all().map((c) => c.id);
const STRIKE = cardIds.includes('core.strike') ? 'core.strike' : cardIds[0];
const SPELL = cardIds.includes('core.cantrip') ? 'core.cantrip' : cardIds[1];

function opGrid() {
  const variants = [];
  const add = (name, effect, extra = {}) => variants.push({ name, effect, ...extra });
  for (const amount of [0, 4, 40]) add(`block ${amount}`, { op: 'block', amount, target: 'self' });
  add('block formula', { op: 'block', amount: { f: 'stacks', of: 'self', status: 'strength' }, target: 'self' }, { patch: { p: { statuses: { strength: { stacks: 3 } } } } });
  add('block unbreakable', { op: 'block', amount: 30, target: 'self' }, { patch: { p: { statuses: { unbreakable: { stacks: 1 } } } } });
  add('block charged', { op: 'block', amount: 5, target: 'self' }, { meta: { abilityChargeBlock: 4 } });
  add('block floor', { op: 'block', amount: 5, target: 'self' }, { card: { proposalProtectionFloor: true } });
  add('barrier', { op: 'gainBarrier', amount: 6, target: 'self' });
  add('barrier floor', { op: 'gainBarrier', amount: 6, target: 'self' }, { card: { proposalProtectionFloor: true } });
  for (const amount of [1, 3, 60]) add(`draw ${amount}`, { op: 'draw', amount });
  add('draw once', { op: 'draw', amount: 1, oncePerTurn: true }, { twice: true });
  add('draw marker', { op: 'draw', amount: 1, oncePerTurn: 'm1' }, { twice: true });
  add('draw if', { op: 'draw', amount: 1, if: { p: 'hasStatus', of: 'self', status: 'strength' } });
  add('draw if held', { op: 'draw', amount: 1, if: { p: 'hasStatus', of: 'self', status: 'strength' } }, { patch: { p: { statuses: { strength: { stacks: 1 } } } } });
  add('draw repeat', { op: 'draw', amount: 1, repeat: 3 });
  add('discard', { op: 'discard', amount: 2 });
  add('discard random', { op: 'discard', amount: 2, random: true });
  add('discard choose', { op: 'discard', amount: 1, choose: true });
  add('exhaust', { op: 'exhaust', amount: 2 });
  add('exhaust random', { op: 'exhaust', amount: 2, random: true });
  for (const pile of ['hand', 'draw', 'discard', 'exhaust', 'bogus']) for (const position of [undefined, 'top', 'bottom']) add(`addCard ${pile} ${position}`, { op: 'addCard', card: STRIKE, pile, position, count: 2 });
  add('addCard unknown', { op: 'addCard', card: 'nothing.here' });
  add('addCard hand full', { op: 'addCard', card: STRIKE, pile: 'hand', count: 20 });
  add('gainEnergy', { op: 'gainEnergy', amount: 2 });
  add('gainEnergy funded', { op: 'gainEnergy', amount: 3, funded: 2 });
  add('restoreMana', { op: 'restoreMana', amount: 2, target: 'self' }, { patch: { p: { mana: 0 } } });
  add('restoreMana floor', { op: 'restoreMana', toFloorPct: 50, target: 'self' }, { patch: { p: { mana: 0 } } });
  add('restoreMana floor full', { op: 'restoreMana', toFloorPct: 50, target: 'self' });
  add('restoreStamina', { op: 'restoreStamina', amount: 2, target: 'self' }, { patch: { p: { stamina: 0 } } });
  for (const amount of [0, 3, 500]) add(`loseHp ${amount}`, { op: 'loseHp', amount, target: 'self' });
  add('loseHp cause', { op: 'loseHp', amount: 2, target: 'self', cause: 'proc:test' });
  add('loseHp nonlethal', { op: 'loseHp', amount: 3, target: 'self', nonlethal: true }, { patch: { p: { hp: 3 } } });
  add('loseHp offering', { op: 'loseHp', amount: 3, target: 'self', offering: true, nonlethal: true });
  add('loseHp enemy', { op: 'loseHp', amount: 4, target: 'enemy' }, { targetWho: 'e0' });
  add('loseHp kill', { op: 'loseHp', amount: 9999, target: 'enemy' }, { targetWho: 'e0' });
  for (const [label, who, patch] of [['self', 'self', { p: { hp: 30 } }], ['enemy', 'enemy', { e0: { hp: 10 } }]])
    for (const amount of [4, 100]) add(`heal ${label} ${amount}`, { op: 'heal', amount, target: who }, { patch, targetWho: 'e0' });
  add('heal inverted', { op: 'heal', amount: 6, target: 'self' }, { patch: { p: { hp: 30, statuses: { 'base.divine-harm': { stacks: 2 } } } } });
  add('heal charged', { op: 'heal', amount: 3, target: 'self' }, { patch: { p: { hp: 30 } }, meta: { abilityChargeHeal: 2 } });
  add('shuffle', { op: 'shuffleDiscardIntoDraw' });
  for (const maneuver of ['attack', 'smash', 'defend', 'sweep', 'counter', 'casting', 'evade', 'bogus']) add(`stance ${maneuver}`, { op: 'enterStance', maneuver, target: 'self' });
  add('stance choose', { op: 'enterStance', choose: 'maneuver' }, { meta: { choice: 'sweep' } });
  add('stance choose none', { op: 'enterStance', choose: 'maneuver' });
  add('stagger', { op: 'stagger', target: 'enemy' }, { targetWho: 'e0' });
  add('cinders', { op: 'addCinders', amount: 3 });
  for (const [label, status, amount] of [['bleed', 'bleed', 3], ['bleed big', 'bleed', 30], ['frost', 'frost', 8], ['sleep', 'sleep', 8], ['unknown', 'nothing', 3]])
    add(`buildup ${label}`, { op: 'buildup', target: 'enemy', status, amount }, { targetWho: 'e0' });
  add('buildup self', { op: 'buildup', target: 'self', status: 'bleed', amount: 9 });
  add('buildup chance', { op: 'buildup', target: 'enemy', status: 'bleed', amount: 9, chance: 50 }, { targetWho: 'e0', twice: true });
  add('buildup camp', { op: 'buildup', target: 'enemy', status: 'weak', amount: 9, camp: 'spell', recoveryProfile: 'mental' }, { targetWho: 'e0' });
  add('buildup charged', { op: 'buildup', target: 'enemy', status: 'bleed', amount: 4 }, { targetWho: 'e0', meta: { abilityChargeBuildup: 3 } });
  add('buildup immune', { op: 'buildup', target: 'enemy', status: 'bleed', amount: 4, traits: { immuneStatuses: ['bleed'] } }, { targetWho: 'e0' });
  add('buildup ward', { op: 'buildup', target: 'enemy', status: 'frost', amount: 12 }, { targetWho: 'e0', patch: { e0: { persistentWard: { max: 4, value: 4 } } } });
  for (const amount of [0, 3, 30]) add(`gainPoise ${amount}`, { op: 'gainPoise', amount, target: 'self' });
  add('gainWard', { op: 'gainWard', amount: 3, target: 'self' });
  for (const amount of [0, 3, 30]) add(`poiseDamage ${amount}`, { op: 'poiseDamage', amount, target: 'enemy' }, { targetWho: 'e0' });
  for (const amount of [0, 3, 30]) add(`wardDamage ${amount}`, { op: 'wardDamage', amount, target: 'enemy' }, { targetWho: 'e0', patch: { e0: { persistentWard: { max: 6, value: 6 } } } });
  add('wardDamage none', { op: 'wardDamage', amount: 3, target: 'enemy' }, { targetWho: 'e0' });
  add('wardDamage self', { op: 'wardDamage', amount: 2, target: 'self' }, { patch: { p: { persistentWard: { max: 6, value: 6 } } } });
  add('charge damage', { op: 'grantCardCharge', target: 'self', key: 'k1', cardType: 'attack', damage: 6 });
  add('charge all', { op: 'grantCardCharge', target: 'self', key: 'k2', cardTag: 'source:spell', abilityKind: 'spell', damage: 3, block: 2, heal: 1, break: 2, buildup: 2, buildupStatus: 'bleed', manaDiscount: 1, damageScope: 'effect' });
  add('charge formula', { op: 'grantCardCharge', target: 'self', key: 'k3', cardType: 'attack', damage: { f: 'stacks', of: 'self', status: 'strength' } }, { patch: { p: { statuses: { strength: { stacks: 2 } } } } });
  add('charge twice', { op: 'grantCardCharge', target: 'self', key: 'k4', cardType: 'attack', damage: 2, block: 1 }, { twice: true });
  add('charge enemy', { op: 'grantCardCharge', target: 'enemy', key: 'k5', damage: 2 }, { targetWho: 'e0' });
  add('linked spell', { op: 'castLinkedSpell', card: SPELL }, { targetWho: 'e0', card: { cardId: 'x' } });
  add('linked spell deep', { op: 'castLinkedSpell', card: SPELL }, { targetWho: 'e0', meta: { linkedDepth: 8 } });
  add('linked unknown', { op: 'castLinkedSpell', card: 'nothing.here' }, { targetWho: 'e0' });
  for (const amount of [1, 5, 50]) add(`revealIntent ${amount}`, { op: 'revealIntent', amount, target: 'enemy' }, { targetWho: 'e0' });
  add('revealIntent self', { op: 'revealIntent', amount: 2, target: 'self' });
  add('retain', { op: 'retain', amount: 2 });
  add('retain all', { op: 'retain', amount: -1 });
  add('rollMode', { op: 'grantRollMode', roll: 'evade', advantage: true, target: 'self' });
  add('rollMode dis', { op: 'grantRollMode', roll: 'evade', disadvantage: true, target: 'self' });
  add('rollMode bad', { op: 'grantRollMode', roll: 'other', advantage: true, target: 'self' });
  add('dodgeRoll', { op: 'dodgeRoll', target: 'self' });
  for (const selection of ['first', 'random', 'choose']) {
    add(`move ${selection} hand discard`, { op: 'moveCards', amount: 2, from: 'hand', to: 'discard', selection });
    add(`move ${selection} draw hand top`, { op: 'moveCards', amount: 2, from: 'draw', to: 'hand', selection, position: 'top', look: 3 });
    add(`move ${selection} discard draw random`, { op: 'moveCards', amount: 3, from: 'discard', to: 'draw', selection, position: 'random' });
    add(`move ${selection} hand exhaust`, { op: 'moveCards', amount: 1, from: 'hand', to: 'exhaust', selection });
  }
  add('move transform', { op: 'moveCards', amount: 1, from: 'hand', to: 'hand', selection: 'first', transformTo: STRIKE });
  add('move bad zone', { op: 'moveCards', amount: 1, from: 'nowhere', to: 'hand' });
  add('move bad field', { op: 'moveCards', amount: 1, from: 'hand', to: 'hand', surprise: true });
  add('move formula', { op: 'moveCards', amount: { f: 'stacks', of: 'self', status: 'strength' }, from: 'hand', to: 'discard', selection: 'first' }, { patch: { p: { statuses: { strength: { stacks: 2 } } } } });
  for (const [label, changes] of [['retain', { retain: true }], ['retain turns', { retain: true, retainTurns: 3 }], ['maneuver', { maneuver: 'smash' }], ['range', { range: 'reach' }], ['hide', { hideTurns: 2 }], ['hide formula', { hideTurns: { f: 'stacks', of: 'self', status: 'strength' } }], ['bad', { retain: 'yes' }]])
    for (const selection of ['first', 'random']) add(`modify ${label} ${selection}`, { op: 'modifyCards', amount: 2, from: 'hand', selection, changes }, { patch: { p: { statuses: { strength: { stacks: 2 } } } } });
  add('modify choose', { op: 'modifyCards', amount: 1, from: 'hand', selection: 'choose', changes: { retain: true } });
  add('unknown op', { op: 'nothingAtAll' });
  add('addBurden', { op: 'addBurden', target: 'enemy', card: STRIKE }, { targetWho: 'e0' });
  variants.forEach((v, i) => {
    const who = i % 5 === 4 ? 'e0' : 'p';
    const s = scenario(`op ${v.name}`, v.patch ? { patch: v.patch } : {});
    const step = { op: 'effect', who, effect: v.effect, ...(v.targetWho ? { targetWho: v.targetWho } : {}), ...(v.card ? { card: v.card } : {}), ...(v.meta ? { meta: v.meta } : {}) };
    s.steps.push(step, { op: 'drain' });
    if (v.twice) s.steps.push(step, { op: 'drain' });
  });
}

// ---------------------------------------------------------------------------------------------------------------
// The Defend trait guard: it answers the first enemy hit that Block, Barrier or Ward reduced
// ---------------------------------------------------------------------------------------------------------------
function traitGuardGrid() {
  const receipts = [
    ['reduced', { amount: 5, blocked: 3, wardResisted: 0 }], ['warded', { amount: 5, blocked: 0, wardResisted: 2 }], ['bare', { amount: 5, blocked: 0, wardResisted: 0 }],
    ['avoided', { amount: 5, blocked: 3, wardResisted: 0, avoided: true }], ['none', { amount: 0, blocked: 0, wardResisted: 0 }],
  ];
  for (const [label, receipt] of receipts) for (const guard of [{ poise: 2, ward: 1 }, { poise: 0, ward: 2 }, { poise: 3, ward: 0 }]) for (const attacker of ['e0', 'dead']) {
    const patch = { e0: { persistentWard: { max: 4, value: 4 }, ...(attacker === 'dead' ? { alive: false, hp: 0 } : {}) }, p: { combatTraitGuard: guard } };
    const s = scenario(`trait guard ${label} ${guard.poise}/${guard.ward} ${attacker}`, { patch });
    s.steps.push({ op: 'tacticalComplete', who: 'e0', targetWho: 'p', receipt }, { op: 'drain' }, { op: 'tacticalComplete', who: 'e0', targetWho: 'p', receipt: { ...receipt, amount: 4 } }, { op: 'drain' });
  }
  const s = scenario('trait guard player attacker', { patch: { e0: { combatTraitGuard: { poise: 2, ward: 1 } } } });
  s.steps.push({ op: 'tacticalComplete', who: 'p', targetWho: 'e0', receipt: { amount: 5, blocked: 3, wardResisted: 0 } }, { op: 'drain' });
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
opGrid();
traitGuardGrid();
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
bytes = write('traits.json', traitsFile());
total += bytes; index.traits = { file: 'traits.json', bytes };
const fightCount = {};
for (const { classId, fights } of fightFiles()) {
  const file = `fights-${classId}.json`;
  const bytes2 = write(file, { classId, fights });
  total += bytes2;
  const stepCount = fights.reduce((n, f) => n + f.steps.length, 0);
  fightCount[classId] = { fights: fights.length, steps: stepCount };
  index.fightFiles = [...(index.fightFiles || []), { file, classId, fights: fights.length, steps: stepCount, bytes: bytes2 }];
}
index.fights = fightCount;
index.rejections = REJ.finish();
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-statuses: ${statusIds.length} statuses, ${recorded.length} scenarios, ${(total / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/statuses`);
