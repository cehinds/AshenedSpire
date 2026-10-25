#!/usr/bin/env node
// Core 2 run-loop oracle (WP-19, docs/design/11-CORE2-PORT-PLAN.md). Plays WHOLE Core 2 runs on the SHIPPED web for the configured `default` profile, from the
// new run to the run's end, and records every node entered and the run state after each step. The web's main.js is not headless, so the oracle mirrors it line for
// line (and calls the shipped model and engine functions for everything they do):
//   src/main.js enterNode (2805-2877), startFight (2976-3000), enterCombat (3002-3170), showEvent (4036-4062), showRest (3769-3792), showShop (3945-3956),
//   beginGameProfileReward / showGameProfileReward (3896-3940), onCombatEnd (3225-3345), advanceAct (1426-1440), runResult (2276-2297), finishRun (2376-2420)
//   src/engine/quests.js commitEventChoice, src/ui/screens/event.js (the event's view), src/model/runCompletion.js completedRunMeta (the class unlock ledger)
// The fights are driven with the combat drivers of the rewards oracle (a greedy bot over the legal Core intents, enemies weakened by a test edit in most fights so
// that runs reach their summit); the map walk, the events, the rests, the shops, the reward doors and the finishing take bot choices from the run's own LCG.
//
//   node Tools/oracle-core2-loop.mjs [--runs 8] [--source <web checkout>] [--allow-dirty]
//
// Files (Unity/Assets/Tests/Oracle/core2/loop/, sorted keys; two runs are byte-identical):
//   run-<class>-seedNN.json   one whole run: the profile's unlock ledger before it, then the steps, then the run's end
//   index.json                the sha, the profile hash and the tallies
// A run's steps (every step records `changes` = { set: { key: value }, del: [key] } over the run document's top-level keys, the stream counters aside, and the
// `counters` of the run's streams):
//   travel   { nodeId } -> { door: fight | event | rest | shop | treasure, ... }   enterNode and the door's first screen (a fight's combat is created, a rest visit
//            begun, a shop's stock drawn, a treasure's offer opened, an event's view recorded)
//   fight    { encounterId, weaken, intents } -> outcome   the intents are played, then onCombatEnd settles the fight and opens the reward door
//   ack | take | add | leave   the reward door (`leave` carries the act advance after a boss)
//   commit | restLeave   the rest site;   buy | shopLeave   the shop;   choose | finish   the event (finish enters the fight a response started)
//   end      { victory } the finishing: the run's record and the profile's ledgers
// Test edits are only the fight's `weaken` (every enemy at most N Hit Points, the C# test makes the same edit) and the ids the bot names.
import { writeFileSync, mkdirSync, rmSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, botLcg, legalIntents, pickIntent } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'loop');
const RUNS = Number(arg('--runs', '8'));
const w = await loadWeb();
const reg = w.registries;
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [rewards, progression, skills, classTree, skillXp, encountersMod, gameProfile, actmap, seatsMod, feats, rest, revisit, quests, modelQuests, contentEvents, consequence, choiceBody,
  runCompletion, classCard, classSwap, customMods, rngMod, smithing, profileRun] = await Promise.all([
  load('src/model/gameProfileRewards.js'), load('src/model/gameProfileProgression.js'), load('src/model/skills.js'), load('src/model/classTree.js'), load('src/engine/skillXp.js'),
  load('src/engine/encounters.js'), load('src/model/gameProfile.js'), load('src/engine/actmap.js'), load('src/model/seats.js'), load('src/model/feats.js'),
  load('src/model/gameProfileRest.js'), load('src/model/restRevisit.js'), load('src/engine/quests.js'), load('src/model/quests.js'), load('src/content/events.js'),
  load('src/model/consequence.js'), load('src/ui/models/ChoiceBodyModel.js'), load('src/model/runCompletion.js'), load('src/model/classCard.js'), load('src/model/classSwap.js'),
  load('src/content/customMods.js'), load('src/engine/rng.js'), load('src/model/smithing.js'), load('src/model/gameProfileRun.js'),
]);

const classes = classIds(w);
const PROFILE = w.profile;
const ROUTES_ACTS = gameProfile.gameContent(reg, 'routes').acts;
const NAME = 'Aldric';
const call = (fn) => { try { return fn(); } catch (e) { return { error: e.message }; } };
const counters = (rng) => canon(rng.getCounters());
const seatNow = (run) => seatsMod.seatAtTier(run.seatOrder, run.actNumber);

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
// The new run (main.js beginPreparedRun + startClimb for the default profile)
// ---------------------------------------------------------------------------------------------------------------
/** The ledger of a profile that has completed `n` runs: enough ids to open the n-th class of the sequence. */
const ledger = (n) => ({ gameProfileUnlocks: { [PROFILE.id]: { completedRunIds: Array.from({ length: n }, (_, i) => `before-${i}`) } } });
/** How many completed runs open a class (0 for the first). */
const completedFor = (classId) => { for (let n = 0; n < 8; n++) if (profileRun.gameProfileClassAvailable(PROFILE, ledger(n), classId)) return n; throw new Error(`class ${classId} never opens`); };

function newClimb(seed, classId, meta, runId) {
  const run = w.state.createRunState({ seed, classId, registries: reg, profileMeta: structuredClone(meta), runId });
  run.seedString = rngMod.seedToString(seed);
  run.customization = { name: NAME };
  run.stats = { fightsWon: 0, damageDealt: 0, damageTaken: 0 };
  run.path = [];
  run.seenEvents = [];
  run.lastEncounters = [];
  const rng = w.createRng(seed);
  run.seatOrder = actmap.drawSeatOrder(reg, rng, { firstSeat: null });
  run.mapGraph = actmap.buildActMap(reg, rng, seatNow(run), run.actNumber, null, { history: run.history });
  return { run, rng };
}

/** main.js advanceAct for a run that took no healing mod. */
function advanceAct(run, rng) {
  run.actNumber += 1;
  run.floor = 0;
  run.mapNodeId = null;
  run.path = [];
  run.lastEncounters = [];
  run.hp = run.maxHp;
  run.mapGraph = actmap.buildActMap(reg, rng, seatNow(run), run.actNumber, null, { history: run.history });
}

// ---------------------------------------------------------------------------------------------------------------
// The reward door (main.js 3896-3940)
// ---------------------------------------------------------------------------------------------------------------
const nextOf = (after) => (after === 'victory' ? 'victory' : after === 'defeat' ? 'defeat' : after === 'advanceAct' ? 'advanceAct' : 'map');

function beginReward(run, rng, { source = 'combat', after = 'map', levelOffers = 0, progression: notices = [], noticeOnly = false } = {}) {
  noticeOnly ||= !gameProfile.gameModule(run, 'rewards');
  if (noticeOnly && !notices.length) return { next: nextOf(after) };
  const offerId = `${run.actNumber}:${run.mapNodeId}:${source}`;
  if (!noticeOnly) rewards.gameProfileRewardOffers(reg, run, rng, { offerId, source });
  run.gameProfilePendingReward = { offerId, source, after, levelOffers, ...(notices.length ? { progression: notices } : {}), ...(noticeOnly ? { noticeOnly } : {}) };
  return { next: 'door', offerId };
}
function acknowledge(run) {
  const pending = run.gameProfilePendingReward;
  if (!pending?.progression?.length) return { error: 'no notices' };
  progression.acknowledgeSkillLevelNotices(pending);
  if (pending.noticeOnly) {
    delete run.gameProfilePendingReward;
    return { next: nextOf(pending.after) };
  }
  return { next: 'door' };
}
function leaveDoor(run, rng) {
  const pending = run.gameProfilePendingReward;
  delete run.gameProfilePendingReward;
  if (pending.levelOffers > 0) {
    const offerId = `${pending.offerId}:level:${pending.levelOffers}`;
    rewards.gameProfileRewardOffers(reg, run, rng, { offerId, source: 'level' });
    run.gameProfilePendingReward = { ...pending, offerId, source: 'level', levelOffers: pending.levelOffers - 1 };
    return { next: 'door', offerId };
  }
  return { next: pending.after === 'advanceAct' ? 'advanceAct' : 'map' };
}

// ---------------------------------------------------------------------------------------------------------------
// The fight (main.js enterCombat and onCombatEnd)
// ---------------------------------------------------------------------------------------------------------------
function combatMods(run, encounter) {
  let hpMult = 1, damageMult = 1;
  if (Array.isArray(run.seatOrder)) {
    const boss = seatsMod.bossTierScale(reg, { encounter, tier: run.actNumber });
    hpMult *= boss ? boss.hp : seatsMod.seatTierHpMult(reg, seatNow(run), run.actNumber);
    if (boss) damageMult = boss.damage;
  }
  return { hpMult, damageMult };
}

/** enterCombat(nodeId, encounterId): the entry receipt on the run and the combat on the run's streams. */
function enterCombat(run, rng, nodeId, encounterId) {
  run.combatEntered = { nodeId, encounterId };
  const enc = reg.encounters.get(encounterId);
  const cm = combatMods(run, enc);
  const combat = w.runCombat.createRunCombat({ registries: reg, rng, run, enemyIds: enc.enemies, encounter: enc, settings: {}, hpMult: cm.hpMult, enemyDamageMult: cm.damageMult });
  return { combat, enc };
}

function onCombatEnd(run, rng, combat, enc) {
  const result = combat.result;
  delete run.combatPendingOutcome;
  w.runCombat.runCombatEnd(run, combat);
  const skillProgression = progression.configuredSkillProgression(run);
  const trackReceipt = skillXp.skillXpReceipt(combat);
  for (const [skillId, amount] of Object.entries(trackReceipt)) {
    const kind = skills.skillKindOf(reg, skillId);
    const bonus = kind === 'armour' ? 'armourXp' : 'weaponXp';
    trackReceipt[skillId] = Math.floor(amount * feats.featMultiplier(run, bonus));
  }
  const awards = skillXp.settleConfiguredSkillProgression(reg, run, combat, { receipt: trackReceipt, victory: result === 'victory', pool: enc.pool, classMultiplier: feats.featMultiplier(run, 'classXp') });
  const notices = skillProgression ? progression.skillLevelNotices(reg, [awards.classAward, ...awards.trackAwards], { usedArmaments: combat.skillXp?.[combat.playerKey || 'player']?.usedArmaments || [] }) : [];
  const out = { result, pool: enc.pool, trackReceipt: canon(trackReceipt), trackAwards: canon(awards.trackAwards), classAward: canon(awards.classAward), notices: canon(notices) };
  if (result !== 'victory') {
    run.hp = 0;
    run.combatEntered = null;
    return { ...out, ...beginReward(run, rng, { after: 'defeat', progression: notices, noticeOnly: true }) };
  }
  run.hp = Math.min(run.maxHp, run.hp + 5 * feats.featStacks(run, 'vitalRenewal'));
  run.stats.fightsWon += 1;
  run.combatEntered = null;
  const cinders = Math.floor(encountersMod.rollRuneReward(reg, rng, enc.pool, run.relics));
  run.cinders += cinders;
  const boss = enc.pool === 'boss';
  if (boss) run.bossesBeaten = [...new Set([...(run.bossesBeaten || []), ...enc.enemies])];
  if (boss && run.actNumber >= (gameProfile.gameModule(run, 'routes')?.acts || 3)) return { ...out, cinders, ...beginReward(run, rng, { after: 'victory', progression: notices, noticeOnly: true }) };
  return { ...out, cinders, ...beginReward(run, rng, { source: 'combat', after: boss ? 'advanceAct' : 'map', levelOffers: 0, progression: notices }) };
}

function pickGreedy(legal, pick) {
  const cards = legal.filter((i) => i.type === 'playCard');
  if (cards.length && pick(10) < 9 && !legal.some((i) => ['chooseReaction', 'chooseCards', 'chooseDiscard'].includes(i.type))) return cards[pick(cards.length)];
  return pickIntent('cards', legal, pick);
}

function playFight(run, combat, weaken, salt) {
  if (weaken) for (const enemy of combat.enemies) enemy.hp = Math.min(enemy.hp, weaken);
  const pick = botLcg(run.seed ^ 0x2c1b3c6d ^ (run.stats.fightsWon * 977 + run.actNumber * 31 + salt));
  const intents = [];
  for (let i = 0; i < 700 && !combat.result; i++) {
    const legal = legalIntents(w, combat, { full: true });
    const intent = pickGreedy(legal, pick);
    if (!intent) break;
    w.combat.dispatch(combat, structuredClone(intent));
    intents.push(intent);
  }
  return intents;
}

// ---------------------------------------------------------------------------------------------------------------
// Events (main.js showEvent, ui/screens/event.js, engine/quests.js commitEventChoice)
// ---------------------------------------------------------------------------------------------------------------
const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x)));

function refusalFor(message, eventId, choiceId) {
  if (message === `commitEventChoice: event '${eventId}' has no choice '${choiceId}'`) return { key: 'events.refusal.unknownChoice', args: [] };
  if (message === `commitEventChoice: '${eventId}/${choiceId}' is not open to this run's history`) return { key: 'events.refusal.history', args: [] };
  if (message === `commitEventChoice: '${eventId}/${choiceId}' costs more than the run holds`) return { key: 'events.refusal.cinders', args: [] };
  return null;
}

function openEvent(run, eventId) {
  const def = reg.events.get(eventId);
  const questId = modelQuests.questChainForEvent(reg.questChains, eventId);
  const all = contentEvents.eventChoicesWithHistory(def);
  const visible = modelQuests.availableEventChoices(all, run);
  const choices = visible.map(({ choice, index }) => ({
    index, choiceId: choice.id, label: choice.label, resultText: choice.resultText, affordable: quests.choiceAffordable(choice, run), priced: !!choice.requires,
    binding: consequence.bindingReasons(choice, reg).length > 0, bindingReasons: consequence.bindingReasons(choice, reg),
    refusal: quests.choiceAffordable(choice, run) ? null : 'events.refusal.cinders',
  }));
  const hidden = all.map((choice, index) => ({ index, choiceId: choice.id, refusal: 'events.refusal.history' })).filter((row) => !visible.some((v) => v.index === row.index));
  let speaker = null;
  if (questId) {
    const s = reg.speakers.get(reg.eventSpeakers[eventId]);
    speaker = { id: s.id, name: s.name, portraitKey: s.portraitKey, portraitAvailable: !!s.portraitKey && reg.enemies.has(s.portraitKey) };
  }
  const status = choiceBody.eventResponseStatus(choices.map((c) => ({ index: c.index, affordable: c.affordable, priced: c.priced, binding: c.binding })));
  return plain({ eventId, questId, title: def.name, text: def.text, art: def.art, speaker, choices, hidden,
    status: { total: status.total, available: status.available, blocked: status.blocked, binding: status.binding, phase: status.phase } });
}

function chooseEvent(run, rng, eventId, choiceId) {
  try {
    const { choice, receipt, completions, events } = quests.commitEventChoice({ run, registries: reg, rng }, { eventId, choiceId });
    const fight = run.combatEntered ? (typeof run.combatEntered === 'string' ? run.combatEntered : run.combatEntered.encounterId) : null;
    return { ok: true, outcome: plain({ choiceId: choice.id, resultText: choice.resultText, receipt, completions, events, fight }) };
  } catch (e) {
    const refusal = refusalFor(e.message, eventId, choiceId);
    return refusal ? { ok: false, refusal } : { ok: false, error: e.message };
  }
}

/** showEvent's onDone: a fight the choice started leaves the run for enterCombat. */
function finishEvent(run) {
  if (run.combatEntered) {
    const encounterId = typeof run.combatEntered === 'string' ? run.combatEntered : run.combatEntered.encounterId;
    run.combatEntered = null;
    return { fight: encounterId };
  }
  return { fight: null };
}

// ---------------------------------------------------------------------------------------------------------------
// enterNode (main.js 2805-2877), rest (3769-3792), shop (3945-3956)
// ---------------------------------------------------------------------------------------------------------------
function enterNode(run, rng, nodeId) {
  const node = run.mapGraph.nodes[nodeId];
  revisit.clearRestRevisitOnTravel(run, nodeId);
  run.mapNodeId = nodeId;
  if (!run.path.includes(nodeId)) run.path.push(nodeId);
  run.floor = node.floor;
  let kind = node.type;
  if (kind === 'event') {
    const res = node.resolved || { kind: 'fight' };
    if (res.kind === 'event') { run.seenEvents.push(res.eventId); return { door: 'event', eventId: res.eventId }; }
    kind = res.kind;
  }
  const fight = (pool) => {
    const encounterId = pool === 'boss'
      ? actmap.bossEncounterForNode(reg, run.mapGraph, nodeId, { seat: seatNow(run), tier: run.actNumber })
      : encountersMod.rollEncounter(reg, rng, { pool, seat: seatNow(run), exclude: run.lastEncounters });
    if (pool === 'normal') { run.lastEncounters.push(encounterId); if (run.lastEncounters.length > 2) run.lastEncounters.shift(); }
    return { door: 'fight', pool, encounterId };
  };
  switch (kind) {
    case 'monster': case 'fight': return fight('normal');
    case 'elite': return fight('elite');
    case 'boss': return fight('boss');
    case 'shrine': return { door: 'rest', camp: node.type === 'event' };
    case 'merchant': return { door: 'shop' };
    case 'treasure': return { door: 'treasure' };
    default: throw new Error(`Unknown node kind '${kind}'`);
  }
}
const reachable = (run) => {
  const at = run.mapNodeId;
  const next = at == null ? run.mapGraph.startIds : run.mapGraph.nodes[at].next;
  const out = [...next];
  const back = revisit.restRevisitNodeId(run);
  if (back && !out.includes(back)) out.push(back);
  return out;
};

function showRest(run) {
  revisit.openRestRecord(run, { multiUse: false });
  const visit = rest.beginGameProfileRest(run, `${run.actNumber}:${run.mapNodeId}`);
  return { visit: structuredClone(visit) };
}
function restCommit(run, service, payload) {
  const receipt = rest.commitGameProfileRest(reg, run, service, payload);
  revisit.recordRestConsumed(run, service);
  return canon(receipt);
}

function showShop(run, rng) {
  if (!gameProfile.gameModule(run, 'shops')) return { map: true };
  run.gameProfileOpenShop = `${run.actNumber}:${run.mapNodeId}`;
  const shopId = run.gameProfileOpenShop;
  return { shopId, stock: structuredClone(rewards.gameProfileShopStock(reg, run, rng, { shopId })) };
}

// ---------------------------------------------------------------------------------------------------------------
// finishRun (main.js 2376-2420, 2276-2297): the record and the profile's ledgers
// ---------------------------------------------------------------------------------------------------------------
function runResult(run, victory) {
  return {
    victory, seed: run.seedString, class: run.class, className: classCard.runClassIdentity(reg, run).name, act: run.actNumber, floor: run.floor,
    fightsWon: run.stats.fightsWon, damageDealt: run.stats.damageDealt, damageTaken: run.stats.damageTaken, name: run.customization && run.customization.name,
    custom: customMods.isCustomRun(run.custom), ascension: (run.custom && run.custom.ascension) || 0, bosses: [...(run.bossesBeaten || [])],
    maxClassLevel: classSwap.peakClassLevel(run), bossGroups: structuredClone(run.bossGroups || {}),
  };
}
function finishRun(run, meta, victory) {
  const resultRecord = { ...runResult(run, victory), gameProfile: run.gameProfile, runId: run.runId };
  const result = { ...resultRecord, victory, finishId: run.runId };
  const completed = runCompletion.completedRunMeta(reg, meta, result);
  return { meta: completed.meta, unlocked: completed.unlocked, changed: completed.meta !== meta };
}

// ---------------------------------------------------------------------------------------------------------------
// One whole run
// ---------------------------------------------------------------------------------------------------------------
const ATTRIBUTES = Object.keys(w.state.createRunState({ seed: 1, classId: classes[0], registries: reg, profileMeta: ledger(8) }).attributes);

function playRun(classId, ci, index) {
  const seed = w.rngMod.sweepSeed(90000 + ci * 100 + index);
  const completed = completedFor(classId);
  const metaBefore = ledger(completed);
  const runId = `run-${classId}-${index}`;
  const { run, rng } = newClimb(seed, classId, metaBefore, runId);
  const pick = botLcg(seed ^ 0x6c0de001);
  // The run's style: how often its fights are weakened (a test edit), so that some runs fall early and most reach the summit.
  const style = index % 4;
  const weakenChance = [100, 85, 60, 0][style];
  const steps = [];
  let over = null;
  let fights = 0;
  const record = (op, fn, extra = {}) => {
    const before = snapshot(run);
    const result = call(fn);
    steps.push(canon({ op, ...extra, ...(result?.error ? { error: result.error } : { result }), changes: changes(before, run), counters: counters(rng) }));
    return result;
  };
  const logFight = (nodeId, entry) => {
    // The fight itself: the intents, the settlement, the door.
    const { combat, enc } = entry;
    const weaken = pick(100) < weakenChance ? 1 + pick(8) : 0;
    const before = entry.before;
    const outcome = call(() => {
      const intents = playFight(run, combat, weaken, ++fights);
      if (!combat.result) throw new Error('fight unfinished');
      entry.intents = intents;
      return onCombatEnd(run, rng, combat, enc);
    });
    if (outcome.error) throw new Error(`oracle-core2-loop: ${classId}/${seed} ${enc.id}: ${outcome.error}`);
    const door = run.gameProfilePendingReward ? canon(run.gameProfilePendingReward) : null;
    const offer = door && run.gameProfileRewardOffers?.[door.offerId] ? canon(run.gameProfileRewardOffers[door.offerId]) : null;
    steps.push(canon({ op: 'fight', nodeId, encounterId: enc.id, weaken, intents: entry.intents.map((i) => JSON.stringify(i)), outcome, door, offer, changes: changes(before, run), counters: counters(rng) }));
    return outcome.next;
  };

  /** Works the reward door until it closes: the notices, then the offer (take, add to the deck, leave). Returns what comes next. */
  const workDoor = (next) => {
    for (let guard = 0; next === 'door' && guard < 12; guard++) {
      const pending = run.gameProfilePendingReward;
      if (pending.progression?.length) { next = record('ack', () => acknowledge(run)).next; continue; }
      const offerId = pending.offerId;
      const offerNow = run.gameProfileRewardOffers[offerId];
      const roll = pick(10);
      const i = pick(offerNow.options.length);
      if (roll < 4) record('take', () => canon(rewards.claimGameProfileReward(reg, run, offerId, i)), { offerId, index: i });
      else if (roll < 8) record('add', () => canon(rewards.claimGameProfileRewardToDeck(reg, run, offerId, i, {})), { offerId, index: i });
      if (pick(6) === 0) { const bad = offerNow.options.length + pick(2) - 1; record('take', () => canon(rewards.claimGameProfileReward(reg, run, offerId, bad)), { offerId, index: bad }); }
      const left = record('leave', () => {
        const step = leaveDoor(run, rng);
        if (step.next === 'advanceAct') advanceAct(run, rng);
        return step;
      }, { offerId });
      next = left.next;
    }
    return next;
  };

  const finish = (victory) => {
    over = victory ? 'victory' : 'defeat';
    const done = finishRun(run, metaBefore, victory);
    const row = done.changed ? done.meta.results[done.meta.results.length - 1] : null;
    steps.push(canon({ op: 'end', victory, record: row, unlocked: done.unlocked, ledger: done.meta.gameProfileUnlocks, changed: done.changed, counters: counters(rng) }));
  };

  for (let node = 0; node < 200 && !over; node++) {
    let options = reachable(run);
    // A left rest site stays enterable (restRevisit.js); the bot goes back to it now and then.
    const back = revisit.restRevisitNodeId(run);
    if (back && options.length > 1 && pick(8) !== 0) options = options.filter((id) => id !== back);
    if (!options.length) throw new Error(`oracle-core2-loop: ${classId}/${seed} stuck at ${run.mapNodeId}`);
    // The walk leans toward the places that are not fights or rests (events, shops, treasures), so that every kind of node is entered often.
    const special = options.filter((id) => !['monster', 'fight', 'elite', 'boss', 'shrine'].includes(run.mapGraph.nodes[id].type) || run.mapGraph.nodes[id].resolved?.kind === 'event');
    const nodeId = special.length && pick(10) < 7 ? special[pick(special.length)] : options[pick(options.length)];
    // The travel: enterNode, then the door's first screen.
    const before = snapshot(run);
    const door = enterNode(run, rng, nodeId);
    let entry = null;
    let next = 'map';
    switch (door.door) {
      case 'fight': {
        const fightEntry = enterCombat(run, rng, nodeId, door.encounterId);
        entry = { ...fightEntry, before: snapshot(run) };
        break;
      }
      case 'event': door.view = openEvent(run, door.eventId); break;
      case 'rest': door.rest = showRest(run); break;
      case 'shop': Object.assign(door, showShop(run, rng)); break;
      case 'treasure': Object.assign(door, beginReward(run, rng, { source: 'combat', after: 'map' })); break;
      default: throw new Error(door.door);
    }
    steps.push(canon({ op: 'travel', nodeId, door, changes: changes(before, run), counters: counters(rng) }));
    if (door.door === 'fight') next = logFight(nodeId, entry);
    else if (door.door === 'treasure') next = door.next;
    else if (door.door === 'event') {
      // The event: responses (legal and not) until one is taken, then Continue; a response may start a fight.
      const def = reg.events.get(door.eventId);
      const all = contentEvents.eventChoicesWithHistory(def);
      let taken = false;
      for (let attempt = 0; attempt < 8 && !taken; attempt++) {
        const roll = pick(12);
        const choiceId = roll === 11 ? 'nonesuch' : all[pick(all.length)].id;
        const result = record('choose', () => chooseEvent(run, rng, door.eventId, choiceId), { eventId: door.eventId, choiceId });
        taken = result.ok === true;
      }
      if (!taken) {
        const view = openEvent(run, door.eventId);
        const open = view.choices.find((c) => c.affordable) || view.choices[0];
        const result = record('choose', () => chooseEvent(run, rng, door.eventId, open.choiceId), { eventId: door.eventId, choiceId: open.choiceId });
        if (!result.ok) throw new Error(`oracle-core2-loop: ${classId}/${seed} event ${door.eventId} cannot be left`);
      }
      // Continue: a fight the response started is entered in the same step (showEvent's onDone calls enterCombat).
      let started = null;
      const finished = record('finish', () => {
        const done = finishEvent(run);
        if (done.fight) started = enterCombat(run, rng, run.mapNodeId, done.fight);
        return done;
      }, { eventId: door.eventId });
      if (started) next = logFight(run.mapNodeId, { ...started, before: snapshot(run) });
    } else if (door.door === 'rest') {
      const acts = 1 + pick(4);
      for (let a = 0; a < acts; a++) {
        const roll = pick(14);
        const plan = call(() => smithing.smithingPlan(reg, run, undefined, { free: true }));
        const refs = plan?.candidates ? plan.candidates.map((c) => c.itemRef) : [];
        if (roll < 6) {
          const itemRef = refs.length && pick(8) ? refs[pick(refs.length)] : (pick(2) ? 'armament/nothing' : 'relic/nothing');
          record('commit', () => restCommit(run, 'smith', { itemRef }), { service: 'smith', itemRef });
        } else if (roll < 9) {
          const attributeId = pick(6) ? ATTRIBUTES[pick(ATTRIBUTES.length)] : 'luck';
          record('commit', () => restCommit(run, 'levelUp', { attributeId }), { service: 'levelUp', attributeId });
        } else if (roll === 9) record('commit', () => restCommit(run, 'bogus', {}), { service: 'bogus' });
        else if (roll < 12) record('enter', () => showRest(run), { nodeId, again: true });
        else break;
      }
      record('restLeave', () => { revisit.markRestLeft(run); return { revisit: revisit.restRevisitNodeId(run) }; }, { nodeId });
    } else if (door.door === 'shop' && !door.map) {
      const n = door.stock.options.length;
      const buys = pick(4);
      for (let b = 0; b < buys; b++) {
        const roll = pick(10);
        const i = roll === 9 ? n + pick(2) : pick(n);
        record('buy', () => canon(rewards.purchaseGameProfileShopOffer(reg, run, door.shopId, i)), { shopId: door.shopId, index: i });
      }
      record('shopLeave', () => { delete run.gameProfileOpenShop; return {}; }, { shopId: door.shopId });
    }
    if (next === 'door') next = workDoor(next);
    else if (door.door === 'treasure' && next === 'map') next = 'map';
    if (next === 'victory') finish(true);
    else if (next === 'defeat') finish(false);
  }
  if (!over) throw new Error(`oracle-core2-loop: ${classId}/${seed} did not end`);
  return canon({ classId, seed, name: NAME, runId, style, completedBefore: completed, metaBefore, steps, over, counters: counters(rng) });
}

// ---------------------------------------------------------------------------------------------------------------
function ensureMeta(file) {
  const meta = `${file}.meta`;
  if (existsSync(meta)) return;
  const guid = createHash('md5').update(`ashen-oracle-core2-loop:${file.split(/[\\/]/).pop()}`).digest('hex');
  writeFileSync(meta, `fileFormatVersion: 2\nguid: ${guid}\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n`);
}
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
let bytes = 0;
const write = (name, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, name), text); ensureMeta(join(OUT, name)); bytes += Buffer.byteLength(text); };

const index = { source: 'main.js enterNode, showEvent, showRest, showShop, beginGameProfileReward, onCombatEnd, advanceAct, finishRun (configured profile default)', webSha: w.head,
  profileId: PROFILE.id, profileHash: PROFILE.hash, routesActs: ROUTES_ACTS, runs: [], doors: {}, events: {}, services: {}, ends: {},
  tally: { runs: 0, steps: 0, travels: 0, fights: 0, victories: 0, defeats: 0, bosses: 0, elites: 0, acts: 0, events: 0, chooses: 0, chooseErrors: 0, eventFights: 0, rests: 0, commits: 0, shops: 0, buys: 0, treasures: 0, takes: 0, adds: 0, ends: 0, unlocks: 0 } };
const t = index.tally;
const bump = (table, key) => { table[key] = (table[key] || 0) + 1; };
for (const [ci, classId] of classes.entries()) {
  for (let i = 0; i < RUNS; i++) {
    const name = `run-${classId}-seed${String(i).padStart(2, '0')}.json`;
    const sc = playRun(classId, ci, i);
    write(name, sc);
    index.runs.push({ file: name, classId, seed: sc.seed, over: sc.over, steps: sc.steps.length });
    t.runs++;
    for (const s of sc.steps) {
      t.steps++;
      if (s.op === 'travel') { t.travels++; bump(index.doors, s.door.door); if (s.door.door === 'event') { t.events++; bump(index.events, s.door.eventId); } if (s.door.door === 'rest') t.rests++; if (s.door.door === 'shop') t.shops++; if (s.door.door === 'treasure') t.treasures++; }
      if (s.op === 'fight') { t.fights++; s.outcome.result === 'victory' ? t.victories++ : t.defeats++; if (s.outcome.pool === 'boss') t.bosses++; if (s.outcome.pool === 'elite') t.elites++; }
      if (s.op === 'finish' && s.result?.fight) t.eventFights++;
      if (s.op === 'choose') { t.chooses++; if (s.result?.ok === false || s.error) { t.chooseErrors++; bump(index.events, `refused: ${s.result?.refusal?.key || s.result?.error || s.error}`); } }
      if (s.op === 'commit') { t.commits++; bump(index.services, s.error ? `${s.service}: ${s.error}` : s.service); }
      if (s.op === 'buy') { t.buys++; }
      if (s.op === 'take') t.takes++;
      if (s.op === 'add') t.adds++;
      if (s.op === 'leave' && s.result?.next === 'advanceAct') t.acts++;
      if (s.op === 'end') { t.ends++; bump(index.ends, s.victory ? 'victory' : 'defeat'); t.unlocks += s.unlocked.length; }
    }
  }
}
write('index.json', index);
console.log(`oracle-core2-loop: ${t.runs} runs (${t.ends} ended: ${JSON.stringify(index.ends)}), ${t.steps} steps, ${t.fights} fights (${t.victories} won, ${t.bosses} bosses, ${t.elites} elites, ${t.eventFights} from events), ${t.acts} act advances, ${t.events} events (${t.chooses} responses, ${t.chooseErrors} refused), ${t.rests} rests, ${t.shops} shops, ${t.treasures} treasures, ${(bytes / 1024 / 1024).toFixed(1)} MB -> Unity/Assets/Tests/Oracle/core2/loop`);
console.log(JSON.stringify({ doors: index.doors, services: index.services, events: index.events }));
