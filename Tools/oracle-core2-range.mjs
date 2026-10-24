#!/usr/bin/env node
// Core 2 range oracle (WP-12, docs/design/11-CORE2-PORT-PLAN.md): the units of the engagement planner on real Core 2 fights. Drives the SHIPPED
// src/engine/combatEngagement.js (quoteRangeActions, acceptRangeActions, rangeHitContact, executeEngagementMovement, previewRangeRoutes),
// src/engine/combatRangeLineage.js (extendRangeEnvelope), src/model/combatEngagement.js (movedEngagementState) and src/model/formationMovement.js
// (formationMovePlan) at the pinned sha. The fights of `--bot range` (Tools/oracle-core2-reactions.mjs) play the whole thing; these are the cases that
// pin each function on its own.
//
//   node Tools/oracle-core2-range.mjs [--source <web checkout>]
//
// File (Unity/Assets/Tests/Oracle/core2/range/quotes.json, sorted keys, two runs are byte-identical):
//   quote      the plan of a card's actions against enemies at every band, by each route, as a preview and as the committed quote: the plan, the lineage
//              graph the hooks the player owns reserve, the target batches and site batches the actions carry, the RNG counters.
//   accept     the same actions accepted (the Stamina to advance is paid and told) or refused (not enough Stamina, a route that cannot reach).
//   move       the engagement moved one band either way (clamped), refused without an opponent, nothing without range mechanics.
//   formation  formationMovePlan over the settings and the cells.
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, botLcg } from './oracle-core2.mjs';

const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const expansion = await load('src/engine/combatExpansion.js');
const tactics = await load('src/engine/combatCardTactics.js');
const engagement = w.engagement;
const modelEngagement = await load('src/model/combatEngagement.js');
const formationMovement = await load('src/model/formationMovement.js');
const all = encounters(w);
const classes = classIds(w);
const OUT = join(ORACLE_DIR, 'range');

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
function merge(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete target[key];
    else if (isObject(value) && isObject(target[key])) merge(target[key], value);
    else target[key] = structuredClone(value);
  }
  return target;
}
const pairKey = (enemy) => JSON.stringify([`enemy:${enemy.id}`, 'player:player'].sort());

/** The fight a case names: the run, the patch merged over the player, the band of every enemy, the range policy. */
function open(spec) {
  const run = createRun(w, spec.seed, spec.classId);
  const encounter = all.find((e) => e.id === spec.encounterId);
  const { combat } = createFight(w, run, encounter);
  if (spec.patch?.player) merge(combat.player, spec.patch.player);
  for (const [index, patch] of Object.entries(spec.patch?.enemies || {})) if (combat.enemies[Number(index)]) merge(combat.enemies[Number(index)], patch);
  if (spec.fields) merge(combat, spec.fields);
  (spec.bands || []).forEach((band, i) => { if (combat.enemies[i]) combat.rangeState.engagements[pairKey(combat.enemies[i])] = band; });
  return combat;
}

const entity = (combat, ref) => (ref === 'player' ? combat.player : combat.enemies[Number(ref.slice(6))]);
const refOf = (combat, e) => (e === combat.player ? 'player' : `enemy:${combat.enemies.indexOf(e)}`);

/** The actions a card's effects make against the fight: its carrier is the card's own, frozen into the case so the port reads the same input. */
function actionsOf(combat, spec) {
  const inst = { instanceId: 'u1', cardId: spec.cardId, upgraded: false };
  const def = expansion.resolveCombatCard(combat, inst);
  const carrier = tactics.tacticalCarrier(def, { cardId: def.id, instanceId: inst.instanceId, type: def.type }, combat, combat.player);
  const enemyTargets = ['enemy', 'allEnemies', 'randomEnemy', 'otherEnemies'];
  const living = combat.enemies.filter((e) => e.alive);
  return { carrier: canon(carrier), effects: (def.effects || []).filter((e) => !(def.tags || def.cardTags || []).includes('maneuver:counter') || !['damage', 'poiseDamage'].includes(e.op)),
    rows: (def.effects || []).map((effect) => ({ effect, target: enemyTargets.includes(effect.target) ? refOf(combat, living[spec.aim % living.length]) : 'player' })) };
}

function buildActions(combat, built) {
  return built.rows.map((row) => ({ effect: structuredClone(row.effect), source: combat.player, owner: combat.player, target: entity(combat, row.target), card: structuredClone(built.carrier), meta: {} }));
}

const view = (combat, actions) => ({
  actions: actions.map((a) => ({ batches: a.meta?.rangeTargetBatches ?? null, sites: a.meta?.rangeSiteBatches ?? null, resolved: a.card?.resolvedRangeSource ?? null })),
  staminaAfter: combat.player.energy, rng: combat.rng.getCounters(), engagements: combat.rangeState.engagements,
});

const lcg = botLcg(0x12c0de);
const STATUSES = [null, 'waxingMoon', 'thornHalo', 'zealotry', 'bloodUnction', 'envenom', 'constellation'];
const CARDS = ['core.strike', 'core.smash', 'core.sweep', 'core.defend', 'core.counter', 'core.heraldSpecial', 'core.reaverSpecial', 'core.rogueSpecial', 'core.starseerSpecial', 'core.cantrip'];
const ENCOUNTERS = ['loneSoldier', 'patrol', 'packHunt', 'twinPatrol', 'bruiser', 'lanternFlight', 'a2_kennel'];

const cases = [];
let n = 0;
function worldSpec() {
  const classId = classes[lcg(classes.length)];
  const encounterId = ENCOUNTERS[lcg(ENCOUNTERS.length)];
  const status = STATUSES[lcg(STATUSES.length)];
  const encounter = all.find((e) => e.id === encounterId);
  return { seed: w.rngMod.sweepSeed(96000 + n++), classId, encounterId, bands: encounter.enemies.map(() => lcg(3)), aim: lcg(3),
    ...(status ? { patch: { player: { statuses: { [status]: { stacks: 1 + lcg(3) } } } } } : {}),
    ...(lcg(4) === 0 ? { fields: { rangeState: { policy: { advanceSpPerBand: 1 + lcg(3), throwPrecisionPenalty: lcg(3), reachPrecisionBonus: lcg(3) } } } } : {}) };
}
function safe(fn) { try { return fn(); } catch (e) { return { error: e.message }; } }

for (let i = 0; i < 260; i++) {
  const spec = { ...worldSpec(), cardId: CARDS[lcg(CARDS.length)] };
  const route = ['advance', 'throw'][lcg(2)];
  const kind = ['quote', 'quote', 'accept'][lcg(3)];
  let combat;
  try { combat = open(spec); } catch { continue; }
  let built;
  try { built = actionsOf(combat, spec); } catch { continue; }
  if (kind === 'quote') {
    for (const preview of [true, false]) {
      const c = open(spec);
      const actions = buildActions(c, built);
      const record = safe(() => {
        const plan = engagement.quoteRangeActions(c, c.player, actions, { route, preview });
        return { plan: canon(plan), lineage: canon(plan?.lineage ?? null) };
      });
      cases.push({ kind, spec, built: { carrier: built.carrier, rows: built.rows }, route, preview, ...canon(record), ...canon(view(c, actions)) });
    }
  } else {
    const c = open(spec);
    c.player.energy = lcg(4);
    const before = c.player.energy;
    const actions = buildActions(c, built);
    const mark = c.eventLog.length;
    const record = safe(() => {
      const carrier = actions[0]?.card;
      if (!carrier) return { skipped: true };
      carrier.rangeRoute = route;
      carrier.rangeBaseCosts = { energy: 0, stamina: 0, mana: 0 };
      const plan = engagement.acceptRangeActions(c, c.player, actions, carrier, 'root:1');
      return { plan: canon(plan), lineage: canon(carrier.rangeLineagePlan ?? null) };
    });
    cases.push({ kind, spec, built: { carrier: built.carrier, rows: built.rows }, route, stamina: before, ...canon(record), ...canon(view(c, actions)), events: canon(c.eventLog.slice(mark).map((e) => ({ type: e.type, amount: e.amount ?? null }))) });
  }
}

// The engagement moved: one band either way, clamped at both ends, refused without an opponent, nothing without range mechanics.
for (const [index, bands] of [[0, -1], [0, 1], [1, -1], [1, 1], [2, -1], [2, 1], [1, 2], [1, 0]].entries()) {
  for (const enabled of [true, false]) {
    const spec = { seed: w.rngMod.sweepSeed(97000 + index), classId: classes[index % classes.length], encounterId: 'loneSoldier', bands: [bands[0]], ...(enabled ? {} : { fields: { rangeState: { policy: { enabled: false } } } }) };
    const c = open(spec);
    const action = { effect: { op: 'moveEngagement', target: 'enemy', bands: bands[1] }, source: c.player, owner: c.player, target: c.enemies[0], card: {}, meta: {} };
    const mark = c.eventLog.length;
    const record = safe(() => { engagement.executeEngagementMovement(c, action); return { ok: true }; });
    cases.push({ kind: 'move', spec, effect: action.effect, ...canon(record), engagements: canon(c.rangeState.engagements), events: canon(c.eventLog.slice(mark).map((e) => ({ type: e.type, sourceId: e.sourceId, targetId: e.targetId, bands: e.bands }))) });
  }
}
{
  const spec = { seed: w.rngMod.sweepSeed(97100), classId: 'reaver', encounterId: 'loneSoldier', bands: [1] };
  const c = open(spec);
  const own = { effect: { op: 'moveEngagement', target: 'enemy', bands: -1 }, source: c.player, owner: c.player, target: c.player, card: {}, meta: {} };
  cases.push({ kind: 'move', spec, effect: own.effect, target: 'self', ...canon(safe(() => { engagement.executeEngagementMovement(c, own); return { ok: true }; })), engagements: canon(c.rangeState.engagements) });
  const pending = { effect: { op: 'moveEngagement', target: 'pendingAttacker', bands: 1 }, source: c.player, owner: c.player, target: null, card: {}, meta: {} };
  c._reactionSourceId = c.enemies[0].id;
  cases.push({ kind: 'move', spec, fields: { reactionSourceId: c.enemies[0].id }, effect: pending.effect, target: 'pendingAttacker', ...canon(safe(() => { engagement.executeEngagementMovement(c, pending); return { ok: true }; })), engagements: canon(c.rangeState.engagements) });
}

// The step across the player's side of the field.
const cells = ['A1', 'B1', 'C1', 'D1', 'A2', 'F1', 'F3', 'Z9', 'a1', ''];
const settingsList = [{}, { 'gameConfig.presentation.movementEnabled': true }, { 'gameConfig.presentation.movementEnabled': true, 'gameConfig.presentation.movementCostsAction': false },
  { 'gameConfig.presentation.movementEnabled': true, classicAppearance: true }, { 'gameConfig.presentation.movementEnabled': true, classicAppearance: true, 'gameConfig.presentation.formationColumns': 3, 'gameConfig.presentation.formationRows': 4 }];
for (const settings of settingsList) for (const cell of cells) for (const encounterId of ['loneSoldier', 'packHunt', 'twinPatrol']) {
  const spec = { seed: w.rngMod.sweepSeed(98000 + cases.length), classId: classes[cases.length % classes.length], encounterId, ...(cases.length % 5 === 0 ? { patch: { player: { energy: 0, stamina: 0 } } } : {}) };
  const c = open(spec);
  cases.push({ kind: 'formation', spec, settings, cell, plan: canon(formationMovement.formationMovePlan(c, cell, settings)) });
}

mkdirSync(OUT, { recursive: true });
const text = `${JSON.stringify(canon({ webSha: w.head, profileHash: w.profile.hash, cases }))}\n`;
writeFileSync(join(OUT, 'quotes.json'), text);
const byKind = {};
for (const c of cases) byKind[c.kind] = (byKind[c.kind] || 0) + 1;
const lineages = cases.filter((c) => c.lineage).length;
console.log(`oracle-core2-range: ${cases.length} cases ${JSON.stringify(byKind)}, ${lineages} with a lineage, ${cases.filter((c) => c.error).length} refused by the web, ${(text.length / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/range/quotes.json`);
