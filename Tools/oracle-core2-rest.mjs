#!/usr/bin/env node
// Core 2 rest oracle (WP-16, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED web for the configured `default` profile:
//   src/model/gameProfileRest.js   beginGameProfileRest (the arrival heal), commitGameProfileRest / previewGameProfileRest (the free Smith, Level Up, the refusals)
//   src/model/smithing.js, smithingCore.js, itemUpgrades.js   the Smith's plan (stat rows, requirements, armour extras, the generic relic path) and the live card rows
//   src/model/levelup.js           applyLevelUp, rederivePools          src/model/restRevisit.js   the re-entry record of a left rest site
//   src/ui/models/GameProfileRestModel.js, src/ui/components/gameProfileRest.js   what the rest screen shows (services, reasons, headlines, recovery rows)
//   src/main.js enterNode (2806-2830) and showRest (3769-3792), which the oracle mirrors line for line (main.js is not headless)
//
//   node Tools/oracle-core2-rest.mjs [--seeds 6] [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/rest/, sorted keys; two runs are byte-identical):
//   units.json                 the Smith's plan on runs of every class with every owned item the content upgrades: the candidates, their stat rows, the live
//                              card rows (the combat simulation), the requirements; rederivePools after attribute and gear changes
//   rest-<class>-seedNN.json   a bot works a Core 2 climb through the rest sites of its act maps: travel to the shrine, the arrival heal, the free Smith (every owned
//                              item, one tier), Level Up (earned points set by a test edit), every refusal (spent, unknown service, stale, no candidate, no points),
//                              leave and re-enter; between visits it hurts the pools, changes the loadout, adds relics and carried weapons and advances the act
//   index.json                 the sha, the profile hash and the tallies
// Test edits, recorded as steps and made the same way by the C# test: hurt (the pools and flask charges), own (a carried weapon, a worn weapon or armour set, a
// relic), points (earned attribute points), attributes (a point moved by hand), act (advanceAct), node (the map node the visit stands at).
// A step's `changes` is { set: { key: value }, del: [key] } over the run document's top-level keys (the streamCounters aside), values canonical.
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, botLcg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'rest');
const SEEDS = Number(arg('--seeds', '4'));
const w = await loadWeb();
const reg = w.registries;
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [rest, revisit, smithing, smithCore, levelup, gameProfile, actmap, seatsMod, restModelMod, choiceBody, strings, progression] = await Promise.all([
  load('src/model/gameProfileRest.js'), load('src/model/restRevisit.js'), load('src/model/smithing.js'), load('src/model/smithingCore.js'), load('src/model/levelup.js'),
  load('src/model/gameProfile.js'), load('src/engine/actmap.js'), load('src/model/seats.js'), load('src/ui/models/GameProfileRestModel.js'), load('src/ui/models/ChoiceBodyModel.js'),
  load('src/ui/strings.js'), load('src/model/gameProfileProgression.js'),
]);
const t = strings.t;

const classes = classIds(w);
const UNLOCKED = { gameProfileUnlocks: { [w.profile.id]: { completedRunIds: classes.map((_, i) => `r${i}`) } } };
const create = (seed, classId) => w.state.createRunState({ seed, classId, registries: reg, profileMeta: structuredClone(UNLOCKED) });
const call = (fn) => { try { return fn(); } catch (e) { return { error: e.message }; } };
const counters = (rng) => canon(rng.getCounters());

function snapshot(run) { const { streamCounters, gameProfile: pinned, ...rest } = run; return Object.fromEntries(Object.entries(rest).map(([k, v]) => [k, JSON.stringify(canon(v ?? null)) + (v === undefined ? '#u' : '')])); }
function changes(before, run) {
  const after = snapshot(run);
  const set = {}, del = [];
  for (const [k, v] of Object.entries(after)) if (before[k] !== v) set[k] = canon(run[k] ?? null);
  for (const k of Object.keys(before)) if (!(k in after)) del.push(k);
  return { set, del };
}
const runDocument = (run) => { const { gameProfile: pinned, runId, streamCounters, ...rest } = run; return canon({ ...rest, gameProfile: { id: pinned.id, hash: pinned.hash } }); };

// ---------------------------------------------------------------------------------------------------------------
// The climb the visits stand in (the host oracle's new run) and main.js's doors
// ---------------------------------------------------------------------------------------------------------------
const seatNow = (run) => seatsMod.seatAtTier(run.seatOrder, run.actNumber);
function newClimb(seed, classId) {
  const run = create(seed, classId);
  run.stats = { fightsWon: 0, damageDealt: 0, damageTaken: 0 };
  run.path = [];
  run.seenEvents = [];
  run.lastEncounters = [];
  const rng = w.createRng(seed);
  run.seatOrder = actmap.drawSeatOrder(reg, rng, { firstSeat: null });
  run.mapGraph = actmap.buildActMap(reg, rng, seatNow(run), run.actNumber, null, { history: run.history });
  return { run, rng };
}
function advanceAct(run, rng) {
  run.actNumber += 1;
  run.floor = 0;
  run.mapNodeId = null;
  run.path = [];
  run.lastEncounters = [];
  run.hp = run.maxHp;
  run.mapGraph = actmap.buildActMap(reg, rng, seatNow(run), run.actNumber, null, { history: run.history });
}
/** main.js enterNode (2806-2830) up to the node's door. */
function travel(run, nodeId) {
  const node = run.mapGraph.nodes[nodeId];
  revisit.clearRestRevisitOnTravel(run, nodeId);
  run.mapNodeId = nodeId;
  if (!run.path.includes(nodeId)) run.path.push(nodeId);
  run.floor = node.floor;
  return { kind: node.type, actualType: node.actualType };
}
const shrines = (run) => Object.values(run.mapGraph.nodes).filter((n) => n.type === 'shrine').sort((a, b) => a.floor - b.floor).map((n) => n.id);

/** main.js showRest for a configured run (3769-3792): the record, the visit (the arrival heal), then what the screen is drawn from. */
function showRest(run) {
  revisit.openRestRecord(run, { multiUse: false });
  const visit = rest.beginGameProfileRest(run, `${run.actNumber}:${run.mapNodeId}`);
  return { visit: structuredClone(visit) };
}
function commit(run, service, payload) {
  const receipt = rest.commitGameProfileRest(reg, run, service, payload);
  revisit.recordRestConsumed(run, service);
  return canon(receipt);
}
function leave(run) { revisit.markRestLeft(run); return { revisit: revisit.restRevisitNodeId(run) }; }

// ---------------------------------------------------------------------------------------------------------------
// What the rest screen shows (GameProfileRestModel.js and gameProfileRest.js mountGameProfileRest, without the DOM)
// ---------------------------------------------------------------------------------------------------------------
const POOLS = [['health', 'gameProfile.rest.pool.hp', 'hp'], ['mana', 'gameProfile.rest.pool.mp', 'mana'], ['stamina', 'gameProfile.rest.pool.sp', 'stamina']];
function recoveryRows(run, after) {
  return [
    ...POOLS.map(([id, label, key]) => ({ id, label: t(label), before: run[key], after: after[key] })),
    ...(run.flaskCharges ? [
      { id: 'flask-health', label: t('gameProfile.rest.pool.healingFlask'), before: run.flaskCharges.hpCurrent, after: after.flaskCharges.hpCurrent },
      { id: 'flask-mana', label: t('gameProfile.rest.pool.mpFlask'), before: run.flaskCharges.manaCurrent, after: after.flaskCharges.manaCurrent },
    ] : []),
  ];
}
function screenOf(run, visit, full) {
  const model = restModelMod.gameProfileRestModel(reg, run, visit);
  const all = rest.restServicesAll(run);
  const usedOf = (id) => all ? rest.restServiceUsed(visit, id) : visit.service === id;
  const availability = choiceBody.restChoiceStatus(model.services.map((service) => ({ ...service, used: usedOf(service.id) })));
  const preview = model.services.find((service) => service.available)?.preview || run;
  const smithCandidates = model.smith.candidates.map((c) => {
    const { coreCardRows, refinedCost, refinedAffordable, ...plain } = c; // the refined purse is the 405 smith's: a free Smith pays nothing // the card rows run combats: asked for apart
    return canon(full ? { ...plain, cardRows: coreCardRows } : { itemRef: plain.itemRef, currentLevel: plain.currentLevel, nextLevel: plain.nextLevel, changes: plain.changes });
  });
  return canon({
    status: t('rest.status.available', { available: availability.available, total: availability.total }),
    healed: all ? (rest.restHealedAny(visit.healed) ? t('gameProfile.rest.healed', { hp: visit.healed.hp, mp: visit.healed.mana, sp: visit.healed.stamina }) : t('gameProfile.rest.healedFull')) : null,
    line: all ? (visit.service ? t(`gameProfile.rest.${rest.restCompletionKey(model.services, usedOf)}`, { service: t(`gameProfile.rest.service.${visit.service}`) }) : t(`gameProfile.rest.${rest.restIntroKey(model.services, { smithFree: model.smithFree })}`)) : null,
    recovery: recoveryRows(run, preview),
    services: model.services.map((s) => ({ id: s.id, title: s.title, description: usedOf(s.id) ? t('gameProfile.rest.usedService') : s.description, available: s.available, reason: s.available ? '' : usedOf(s.id) ? t('gameProfile.rest.usedService') : s.reason,
      state: usedOf(s.id) ? 'used' : s.available ? 'available' : 'unavailable', used: usedOf(s.id) })),
    smithFree: model.smithFree,
    smith: { stones: model.smith.stones, candidates: smithCandidates },
    smithPreviews: [...model.smithPreviews].map(([itemRef, r]) => ({ itemRef, available: r.available, reason: r.reason, recovery: r.available ? recoveryRows(run, r.preview) : null })),
    attributes: model.attributes.map((a) => ({ id: a.id, label: a.label, shortLabel: a.shortLabel, value: a.value, available: a.available, reason: a.reason, recovery: a.available ? recoveryRows(run, a.preview) : null })),
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Units: the Smith's plan on runs that own every item the content upgrades
// ---------------------------------------------------------------------------------------------------------------
const armamentIds = reg.equipment.armaments.map((a) => a.id);
const ownEverything = (run) => {
  run.loadout.storage = armamentIds.filter((id) => !Object.values(run.loadout.sets).flat().includes(id));
  run.relics = [...new Set([...run.relics, 'curedHide', 'ancestralHorn'])];
};

function planUnits() {
  const out = [];
  classes.forEach((classId, ci) => {
    const seed = w.rngMod.sweepSeed(70000 + ci);
    const variants = [
      { variant: 'start', edit: () => {} },
      { variant: 'carried', edit: (run) => ownEverything(run) },
      { variant: 'worn', edit: (run) => { ownEverything(run); const wearable = reg.equipment.armaments.filter((a) => a.kind === 'weapon' || a.kind === 'staff'); run.loadout.sets.rightHand[0] = wearable[(ci * 3 + 1) % wearable.length].id; run.loadout.storage = run.loadout.storage.filter((id) => id !== run.loadout.sets.rightHand[0]); } },
      { variant: 'shielded', edit: (run) => { ownEverything(run); run.loadout.sets.leftHand[0] = 'buckler'; run.loadout.storage = run.loadout.storage.filter((id) => id !== 'buckler'); } },
      { variant: 'armoured', edit: (run) => { ownEverything(run); const armour = reg.equipment.armour.filter((a) => a.classId === classId && a.id !== 'default'); run.loadout.sets.armor[0] = armour[0].id; run.loadout.boughtArmour = [{ classId, id: armour[0].id }]; } },
      { variant: 'tiered', edit: (run) => { ownEverything(run); run.itemUpgradeLevels = { [`armament/${run.loadout.sets.rightHand[0]}`]: 1 }; } },
    ];
    for (const spec of variants) {
      const run = create(seed, classId);
      spec.edit(run);
      const plan = call(() => smithing.smithingPlan(reg, run, undefined, { free: true }));
      const candidates = plan.error ? plan : plan.candidates.map((c) => {
        const { coreCardRows, refinedCost, refinedAffordable, ...plain } = c; // the refined purse is the 405 smith's: a free Smith pays nothing
        return canon({ ...plain, cardRows: coreCardRows });
      });
      out.push({ classId, variant: spec.variant, seed, edited: canon({ loadout: run.loadout, relics: run.relics, itemUpgradeLevels: run.itemUpgradeLevels ?? null }), candidates });
    }
  });
  return out;
}

/** rederivePools after a point lands on every attribute, and after each carried armament is worn (the pools move with the gear), on a run per class. */
function rederiveUnits() {
  const out = [];
  classes.forEach((classId, ci) => {
    const seed = w.rngMod.sweepSeed(71000 + ci);
    const attributes = Object.keys(create(seed, classId).attributes);
    for (const attributeId of attributes) {
      const run = create(seed, classId);
      run.hp = Math.max(1, run.hp - 3); run.mana = Math.max(0, run.mana - 1); run.stamina = Math.max(0, run.stamina - 1);
      const before = snapshot(run);
      run.attributes[attributeId] += 1;
      run.level.unspentPoints = 0;
      const moved = levelup.rederivePools(reg, run, `point on ${attributeId}`);
      out.push({ classId, attributeId, seed, moved, changes: changes(before, run) });
    }
  });
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Rest scenarios
// ---------------------------------------------------------------------------------------------------------------
const WEARABLE = reg.equipment.armaments.filter((a) => a.kind === 'weapon' || a.kind === 'staff').map((a) => a.id);
const SHIELDS = reg.equipment.armaments.filter((a) => a.kind === 'shield').map((a) => a.id);

function scenario(classId, ci, index) {
  const seed = w.rngMod.sweepSeed(80000 + ci * 100 + index);
  const { run, rng } = newClimb(seed, classId);
  const pick = botLcg(seed ^ 0x2e57c0de);
  const steps = [];
  const record = (op, fn, extra = {}) => {
    const before = snapshot(run);
    const result = call(fn);
    steps.push(canon({ op, ...extra, ...(result?.error ? { error: result.error } : { result }), changes: changes(before, run), counters: counters(rng) }));
    return result;
  };
  const plain = (op, fn, extra = {}) => {
    const before = snapshot(run);
    fn();
    steps.push(canon({ op, ...extra, changes: changes(before, run), counters: counters(rng) }));
  };
  const screen = (label) => steps.push(canon({ op: 'screen', label, screen: call(() => screenOf(run, run.gameProfileRestVisits[run.gameProfileRestVisitId], label === 'arrival')), counters: counters(rng) }));
  const attributeIds = Object.keys(run.attributes);
  let visits = 0;
  const VISITS = 8;
  // A visit at the climb's next shrine; when the act's shrines are used up the act advances.
  const queue = [];
  const lastAct = gameProfile.gameContent(reg, 'routes').acts;
  const nextNode = () => {
    while (!queue.length) {
      const fresh = shrines(run).filter((id) => !run.path.includes(id));
      if (fresh.length) { queue.push(...fresh); break; }
      if (run.actNumber >= lastAct) return null;
      plain('act', () => advanceAct(run, rng), {});
    }
    return queue.shift();
  };
  while (visits < VISITS) {
    // State a bot sets up before a visit.
    if (pick(2) === 0) plain('hurt', () => {
      run.hp = Math.max(1, run.hp - 1 - pick(Math.max(1, run.maxHp - 1))); run.mana = pick(run.maxMana + 1); run.stamina = pick(run.maxStamina + 1);
      if (run.flaskCharges) { run.flaskCharges.hpCurrent = pick(run.flaskCharges.hp + 1); run.flaskCharges.manaCurrent = pick(run.flaskCharges.mana + 1); }
    }, { hp: run.hp });
    const own = pick(6);
    if (own === 0) plain('own', () => { run.loadout.storage = [...new Set([...run.loadout.storage, ...[0, 1, 2].map(() => WEARABLE[pick(WEARABLE.length)])])].filter((id) => !Object.values(run.loadout.sets).flat().includes(id)); }, { what: 'carried' });
    else if (own === 1) plain('own', () => { const id = WEARABLE[pick(WEARABLE.length)]; run.loadout.sets.rightHand[0] = id; run.loadout.storage = run.loadout.storage.filter((x) => x !== id); }, { what: 'worn' });
    else if (own === 2) plain('own', () => { const id = SHIELDS[pick(SHIELDS.length)]; run.loadout.sets.leftHand[0] = id; run.loadout.storage = run.loadout.storage.filter((x) => x !== id); }, { what: 'shield' });
    else if (own === 3) plain('own', () => {
      const armour = reg.equipment.armour.filter((a) => a.classId === classId); const row = armour[pick(armour.length)];
      run.loadout.sets.armor[0] = row.id; run.loadout.boughtArmour ||= []; if (!run.loadout.boughtArmour.some((o) => o.id === row.id)) run.loadout.boughtArmour.push({ classId, id: row.id });
    }, { what: 'armour' });
    else if (own === 4) plain('own', () => { run.relics = [...new Set([...run.relics, pick(2) ? 'curedHide' : 'ancestralHorn'])]; }, { what: 'relic' });
    if (pick(3) === 0) plain('points', () => { run.level.unspentPoints = 1 + pick(2); }, { points: run.level.unspentPoints });
    const nodeId = nextNode();
    if (nodeId === null) break;
    record('travel', () => travel(run, nodeId), { nodeId });
    const entered = record('enter', () => showRest(run), { nodeId });
    visits++;
    if (entered?.error) continue;
    screen('arrival');
    const acts = 2 + pick(4);
    for (let a = 0; a < acts; a++) {
      const roll = pick(16);
      const candidates = call(() => smithing.smithingPlan(reg, run, undefined, { free: true }));
      const refs = candidates.error ? [] : candidates.candidates.map((c) => c.itemRef);
      if (roll < 6) {
        const itemRef = refs.length && pick(8) ? refs[pick(refs.length)] : (pick(2) ? 'armament/nothing' : 'relic/nothing');
        record('commit', () => commit(run, 'smith', { itemRef }), { service: 'smith', itemRef });
      } else if (roll < 10) {
        const attributeId = pick(6) ? attributeIds[pick(attributeIds.length)] : 'luck';
        record('commit', () => commit(run, 'levelUp', { attributeId }), { service: 'levelUp', attributeId });
      } else if (roll === 10) record('commit', () => commit(run, 'strategize', {}), { service: 'strategize' });
      else if (roll === 11 && pick(2)) record('commit', () => commit(run, 'bogus', {}), { service: 'bogus' });
      else if (roll < 14) {
        // The remount after a commit (nothing heals again), then the player leaves and comes back through the map.
        record('enter', () => showRest(run), { nodeId, again: true });
      } else {
        const gone = record('leave', () => leave(run), { nodeId });
        if (!gone?.error && pick(2) === 0) {
          record('enter', () => showRest(run), { nodeId, again: true, afterLeave: true });
        } else break;
      }
      screen(`after ${a}`);
    }
    if (run.restRevisit && !run.restRevisit.left) record('leave', () => leave(run), { nodeId });
  }
  return canon({ classId, seed, steps, run: runDocument(run), counters: counters(rng) });
}

/** The refusals of the rest door that need no climb: no rest node, a stale visit, the module off. */
function doorUnits() {
  const out = [];
  classes.forEach((classId, ci) => {
    const seed = w.rngMod.sweepSeed(72000 + ci);
    const { run } = newClimb(seed, classId);
    const steps = [];
    const log = (op, fn, extra = {}) => { const before = snapshot(run); const result = call(fn); steps.push(canon({ op, ...extra, ...(result?.error ? { error: result.error } : { result }), changes: changes(before, run) })); };
    const fight = Object.values(run.mapGraph.nodes).find((n) => n.type === 'monster');
    const shrine = Object.values(run.mapGraph.nodes).find((n) => n.type === 'shrine');
    log('travel', () => travel(run, fight.id), { nodeId: fight.id });
    log('enter', () => showRest(run), { nodeId: fight.id });
    log('commit', () => commit(run, 'smith', { itemRef: 'armament/greatsword' }), { service: 'smith' });
    log('travel', () => travel(run, shrine.id), { nodeId: shrine.id });
    log('begin', () => rest.beginGameProfileRest(run, 'elsewhere'), { visitId: 'elsewhere' });
    log('commit', () => commit(run, 'smith', { itemRef: 'armament/greatsword' }), { service: 'smith' });
    log('enter', () => showRest(run), { nodeId: shrine.id });
    log('begin', () => rest.beginGameProfileRest(run, 'elsewhere'), { visitId: 'elsewhere' });
    log('travel', () => travel(run, fight.id), { nodeId: fight.id });
    log('commit', () => commit(run, 'smith', { itemRef: 'armament/greatsword' }), { service: 'smith' });
    log('travel', () => travel(run, shrine.id), { nodeId: shrine.id });
    log('revisit', () => ({ revisit: revisit.restRevisitNodeId(run) }), {});
    out.push({ classId, seed, steps, run: runDocument(run) });
  });
  return out;
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
let bytes = 0;
const write = (name, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, name), text); bytes += Buffer.byteLength(text); };
write('units.json', { plans: planUnits(), rederive: rederiveUnits(), doors: doorUnits() });
const index = { source: 'model/gameProfileRest.js, smithing.js, smithingCore.js, levelup.js, restRevisit.js, main.js enterNode + showRest (configured profile default)', webSha: w.head,
  profileId: w.profile.id, profileHash: w.profile.hash, scenarios: [], errors: {}, services: {},
  tally: { visits: 0, healed: 0, commits: 0, done: 0, refused: 0, smiths: 0, levelUps: 0, leaves: 0, reenters: 0, acts: 0, screens: 0, cardRows: 0, candidates: 0, receipts: 0 } };
const tl = index.tally;
const bump = (table, key) => { table[key] = (table[key] || 0) + 1; };
for (const [ci, classId] of classes.entries()) {
  for (let i = 0; i < SEEDS; i++) {
    const name = `rest-${classId}-seed${String(i).padStart(2, '0')}.json`;
    const sc = scenario(classId, ci, i);
    write(name, sc);
    index.scenarios.push({ file: name, classId, seed: sc.seed });
    for (const s of sc.steps) {
      if (s.op === 'enter' && !s.again) { tl.visits++; if (s.result?.visit?.healed && (s.result.visit.healed.hp || s.result.visit.healed.mana || s.result.visit.healed.stamina)) tl.healed++; }
      if (s.op === 'enter' && s.again) tl.reenters++;
      if (s.op === 'enter' && s.error) bump(index.errors, `enter: ${s.error}`);
      if (s.op === 'commit') {
        tl.commits++;
        if (s.error) { tl.refused++; bump(index.errors, `commit ${s.service}: ${s.error}`); } else { tl.done++; bump(index.services, s.service); if (s.service === 'smith') tl.smiths++; else tl.levelUps++; if (s.changes.set.lastSmithingReceipt) tl.receipts++; }
      }
      if (s.op === 'leave') tl.leaves++;
      if (s.op === 'act') tl.acts++;
      if (s.op === 'screen') { tl.screens++; for (const c of s.screen?.smith?.candidates || []) { tl.candidates++; tl.cardRows += c.cardRows?.length || 0; } }
    }
  }
}
write('index.json', index);
console.log(`oracle-core2-rest: ${index.scenarios.length} scenarios, ${tl.visits} visits (${tl.healed} healed on arrival), ${tl.commits} commits (${tl.done} done, ${tl.refused} refused), ${tl.screens} screens, ${tl.candidates} smith candidates (${tl.cardRows} card rows), ${(bytes / 1024 / 1024).toFixed(1)} MB -> Unity/Assets/Tests/Oracle/core2/rest`);
console.log(JSON.stringify({ errors: index.errors, services: index.services, tally: tl }));
