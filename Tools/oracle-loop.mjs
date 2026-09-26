#!/usr/bin/env node
// Run-loop parity oracle (US-8.1, US-8.3–8.6, US-4.4–4.9, US-13.1/13.2; PF-04/05). Records what the SHIPPED
// controller does to a run between fights: travelling to a node, entering a fight (the encounter and the createCombat
// arguments), the fight's end and its routing (death, the summit, the reward checkpoint), claiming a reward door, the
// rest places and their actions, advancing an act, legacy dungeons, and closing a run out into the profile.
//
// main.js is a browser module, so the harness below re-implements the controller functions FAITHFULLY, calling the
// real shipped model/engine functions in the same order and on the same RNG streams. It mirrors, line for line:
//   src/main.js  989-1094   newRun + startClimb           (the sequence's starting run; not a checked step)
//   src/main.js 1135-1157   endlessOn, contentAct, currentSeat, runMapShape
//   src/main.js 1159-1174   advanceAct
//   src/main.js 1577-1600   runResult;  1618-1626 rollDrop;  1636-1669 collectArmament/recordCollectedArmament
//   src/main.js 1671-1679   finishRun
//   src/main.js 1921-1990   enterNode   (merchant and event bodies are the shop/events stream's: recorded as external)
//   src/main.js 1995-2018   combatMods
//   src/main.js 2029-2084   enterDungeonLocation, showDungeonDialogue (commit/onDone), leaveLegacyDungeon, openLegacyEntrance
//   src/main.js 2086-2103   startFight
//   src/main.js 2105-2185   enterCombat (the createCombat arguments; no snapshot resume)
//   src/main.js 2279-2411   onCombatEnd (incl. the legacy-dungeon branch, finishRun on death and at the summit)
//   src/main.js 2412-2456   rollSkillDrafts, rollClassDrafts, beginPendingReward
//   src/main.js 2459-2479   mountPendingReward onDone (delete the checkpoint; advanceAct)
//   src/main.js 2482-2484   chaosRewardsOn
//   src/main.js 2522-2589   showRest (the visit, the arrival, smith services, onDone)
//   src/main.js 2632-2645   showEvent onDone (an event's startCombat enters the fight)
//   src/ui/screens/reward.js 84-227, 370-378, 466-484, 744-746   mountRewards: apply per kind, grantCinders, Continue
//   src/ui/screens/rest.js   366-379, 400-421, 480-488, 554-560, 578-590   Rest, flask moves, Assign, Smith, card services
// Left out, because they touch no run or profile field: audio, sfx, animation, screens, the victory beat, the boss
// intro, sendLanStatus, persist (saves.saveRun stamps streamCounters/savedAt: the save service's job), and
// saves.clearRun (the slot).
//
// Every sequence starts from newRun's run and walks the map node by node under a fixed policy (a seeded LCG that
// never touches the game RNG). Each checked step records its inputs, its outcome and patches of the run document,
// the RNG counters and the profile document; external steps (an event's choice, a merchant visit — another stream's
// port) record their patches and the inputs they chose (an event's open choices and the one taken). RunLoopParityTests
// re-applies each checked step to the document before it; ChainedRunTests carries the C# documents through every step.
//
//   node Tools/oracle-loop.mjs [--source D:/repos/AshenSpire] [--sequences 108] [--coverage]
//
// Writes Unity/Assets/Tests/Oracle/loop/*.json (committed; CI has no access to the old repo). Only .json files are
// deleted on regeneration, so the Unity .meta files beside them survive; a missing .meta is created.
import { writeFileSync, mkdirSync, rmSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const SOURCE = arg('--source', process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const SEQUENCES = Number(arg('--sequences', '108'));
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'loop');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle, advancedConfigSnapshot } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng, sweepSeed, seedToString } = await load('src/engine/rng.js');
const { createRunState, createDeck, createIdGen } = await load('src/model/state.js');
const { createCombat, dispatch } = await load('src/engine/combat.js');
const { serializeCombatSnapshot } = await load('src/engine/combatSnapshot.js');
const { resolveHandRules } = await load('src/model/handRules.js');
const { resolveSwapCostRule, runMods, stampDeck, carriedIds, equippedPieces, addToStorage } = await load('src/model/loadout.js');
const { seatAtTier, seatTierHpMult } = await load('src/model/seats.js');
const { skillXpReceipt, applySkillXp } = await load('src/engine/skillXp.js');
const { skillTracks, classSkillId, spendSkillDraft, skillUpgradesCards, skillLevel } = await load('src/model/skills.js');
const { awardClassXp, pickClassNode } = await load('src/model/classTree.js');
const { awardLevelXp, combatLevelXp, levelUpPlan, applyLevelUp } = await load('src/model/levelup.js');
const { combatXpGains } = await load('src/model/rewardprogress.js');
const { grantSmithingReward, smithingPlan, commitSmithing } = await load('src/model/smithing.js');
const { activeMods, isCustomRun, endlessActInfo, ENDLESS_HP_PER_LOOP, ENDLESS_STR_PER_LOOP } = await load('src/content/customMods.js');
const {
  rollRuneReward, rollCardRewardIds, rollSkillDraftIds, rollClassDraftIds, rollFlaskDrop, rollRelicReward, rollArmamentDrop,
  rollEncounter,
} = await load('src/engine/encounters.js');
const { buildActMap, bossEncounterForNode, drawSeatOrder } = await load('src/engine/actmap.js');
const { executeRunEffects } = await load('src/engine/actions.js');
const { createLocationVisit, arriveAt, leaveLocation, restAt, previewRest } = await load('src/engine/locations.js');
const { CAMP_LOCATION } = await load('src/model/locations.js');
const { flaskChargePlan, moveFlaskCharge, flaskSlotCap } = await load('src/model/gracerefill.js');
const { syncFlaskGrowth } = await load('src/model/flaskgrowth.js');
const { smithServicesAt, extractionPlan, installPlan, commitExtraction, commitInstall } = await load('src/model/cardExtraction.js');
const { recordProgress, evaluateUnlocks } = await load('src/model/unlocks.js');
const { recordArmamentDiscovery } = await load('src/model/startingKits.js');
const { runClassIdentity } = await load('src/model/classCard.js');
const { peakClassLevel } = await load('src/model/classSwap.js');
const { rewardPlan, resolveContinue } = await load('src/model/rewardplan.js');
const {
  dungeonForEncounter, dungeonNode, dungeonNodeAction, beginDungeon, travelDungeon, dungeonChoices, chooseDungeon, continueDungeon,
  resolveDungeonNode, dungeonNeighbors, dungeonDefinition,
} = await load('src/model/legacyDungeon.js');
const { createSaveManager, createMemoryStorage } = await load('src/engine/save.js');
const { resolveGraceRefill, resolveLevelUpValue, settingOn, derivedStatDialOptions } = await load('src/ui/screens/settings.js');
const { shouldPlayPrologue, PROLOGUE_STATE_VERSION } = await load('src/model/prologue.js');
const { commitEventChoice, choiceAffordable } = await load('src/engine/quests.js');
const { eventChoicesWithHistory } = await load('src/content/events.js');
const { availableEventChoices, questChainForEvent } = await load('src/model/quests.js');
const { buildShopStock } = await load('src/engine/encounters.js');

const plain = (v) => (v === undefined ? undefined : JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x))));

// ------------------------------------------------------------------ patches (applied by the C# test)
// A patch turns the previous document into the next, keeping key order (D-040):
//   ['=', value]                     replace
//   ['o', { key: patch }, del, order]  object: patch/insert keys (new keys append), delete `del`, then reorder to `order`
//   ['a', keep, { index: patch }, tail] array: keep the first `keep` items, patch some of them, append `tail`
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
function diff(a, b) {
  if (same(a, b)) return undefined;
  if (isObj(a) && isObj(b)) {
    const set = {};
    const del = Object.keys(a).filter((k) => !(k in b));
    for (const k of Object.keys(b)) {
      if (!(k in a)) set[k] = ['=', b[k]];
      else { const d = diff(a[k], b[k]); if (d) set[k] = d; }
    }
    const order = [...Object.keys(a).filter((k) => k in b), ...Object.keys(b).filter((k) => !(k in a))];
    const wantOrder = Object.keys(b);
    return ['o', set, del.length ? del : 0, same(order, wantOrder) ? 0 : wantOrder];
  }
  if (Array.isArray(a) && Array.isArray(b)) {
    let keep = 0;
    while (keep < a.length && keep < b.length && same(a[keep], b[keep])) keep++;
    // Keep the longest prefix of old positions that the new array still has, patching changed items in place.
    const n = Math.min(a.length, b.length);
    const edits = {};
    let changed = 0;
    for (let i = 0; i < n; i++) if (!same(a[i], b[i])) { edits[i] = diff(a[i], b[i]); changed++; }
    const patch = ['a', n, edits, b.slice(n)];
    const whole = ['=', b];
    return JSON.stringify(patch).length < JSON.stringify(whole).length ? patch : whole;
  }
  return ['=', b];
}
function applyPatch(a, p) {
  if (p === undefined) return a;
  if (p[0] === '=') return structuredClone(p[1]);
  if (p[0] === 'o') {
    const out = {};
    for (const k of Object.keys(a)) out[k] = a[k];
    for (const [k, sub] of Object.entries(p[1])) out[k] = applyPatch(a[k], sub);
    if (p[2]) for (const k of p[2]) delete out[k];
    if (!p[3]) return out;
    const ordered = {};
    for (const k of p[3]) ordered[k] = out[k];
    return ordered;
  }
  const out = a.slice(0, p[1]);
  for (const [i, sub] of Object.entries(p[2])) out[Number(i)] = applyPatch(out[Number(i)], sub);
  return [...out, ...structuredClone(p[3])];
}

// ------------------------------------------------------------------ the harness (main.js module state per sequence)

// ctx = { registries, run, rng, saves, settings, restLocationId, openVisit }: main.js's module state. `registries`
// is rebuilt per run from the settings (newRun → rebuildRegistries); every sequence uses empty advanced settings.
function makeRegistries(settings) {
  return createRegistries(configuredContentBundle(contentBundle, advancedConfigSnapshot(settings || {})));
}

const endlessOn = (ctx) => !!(ctx.run.custom && activeMods(ctx.run.custom).endless);                        // 1135
const contentAct = (ctx) => (endlessOn(ctx) ? endlessActInfo(ctx.run.actNumber).contentAct : ctx.run.actNumber); // 1138
const currentSeat = (ctx) => seatAtTier(ctx.run.seatOrder, contentAct(ctx));                                    // 1143
const runMapShape = (ctx) => (ctx.run.custom && ctx.run.custom.mapShape) || null;                              // 1151
const chaosRewardsOn = (ctx) => !!(ctx.run.custom && activeMods(ctx.run.custom).chaosRewards);                 // 2482

// main.js 989-1094 (newRun + startClimb), minus the seed-string refusal banner, the draft screen and the prologue
// screen; `run.prologue` is written as startClimb writes it.
function newRun(ctx, { classId, seed, custom, keepsakeId = null, customization = null, profileMeta }) {
  const { registries, saves } = ctx;
  saves.ensureProfile();
  const configSnapshot = advancedConfigSnapshot(ctx.settings);
  const run = createRunState({
    seed, classId, registries, profileMeta: profileMeta || saves.loadMeta(), derivedStatOptions: derivedStatDialOptions(ctx.settings),
  });
  run.advancedConfigSnapshot = configSnapshot;
  run.seedString = seedToString(seed);
  run.customization = customization || { name: 'Forsaken', glyph: '⚔', tint: 'gold' };
  run.custom = custom || { ascension: 0, mods: {}, deckMode: 'standard' };
  run.stats = { fightsWon: 0, damageDealt: 0, damageTaken: 0 };
  run.path = [];
  run.seenEvents = [];
  run.lastEncounters = [];
  ctx.run = run;
  const rng = createRng(seed);
  ctx.rng = rng;
  run.seatOrder = drawSeatOrder(registries, rng, { firstSeat: run.custom.firstSeat || null });
  const keepsake = (registries.characterCreation.keepsakes || []).find((k) => k.id === keepsakeId);
  if (keepsake && keepsake.effects.length) executeRunEffects({ run, registries, rng }, keepsake.effects);
  const deckMode = run.custom.deckMode || 'standard';
  const mods = activeMods(run.custom);
  if (deckMode === 'sealed') {
    const pool = registries.classes.get(classId).cardPool.slice();
    const ids = ['strike', 'strike', 'strike', 'strike', 'defend', 'defend', 'defend'];
    for (let i = 0; i < 3 && pool.length; i++) { const id = rng.pick('misc', pool); pool.splice(pool.indexOf(id), 1); ids.push(id); }
    run.deck = createDeck(ids, createIdGen('rc'));
  }
  if (mods.cursedStart) run.deck.push(...createDeck(['guilt'], createIdGen('cx')));
  if (mods.hoarder) run.cinders += registries.balance.customMods.hoarderCinders;
  // startClimb
  run.mapGraph = buildActMap(registries, rng, currentSeat(ctx), contentAct(ctx), runMapShape(ctx), { history: run.history });
  if (shouldPlayPrologue(ctx.settings, ctx.settings?.prologueSeen === true)) run.prologue = { version: PROLOGUE_STATE_VERSION, status: 'pending', scene: 0 };
}

// main.js 1159-1174
function advanceAct(ctx) {
  const { run, registries, rng } = ctx;
  run.actNumber += 1;
  run.floor = 0;
  run.mapNodeId = null;
  run.path = [];
  run.lastEncounters = [];
  if (run.custom && activeMods(run.custom).lessHealing) {
    run.hp = Math.min(run.maxHp, run.hp + Math.floor((run.maxHp - run.hp) * registries.balance.customMods.lessHealingMult));
  } else {
    run.hp = run.maxHp;
  }
  run.mapGraph = buildActMap(registries, rng, currentSeat(ctx), contentAct(ctx), runMapShape(ctx), { history: run.history });
}

// main.js 1577-1600
function runResult(ctx, victory) {
  const { run, registries } = ctx;
  return {
    victory,
    seed: run.seedString,
    class: run.class,
    className: runClassIdentity(registries, run).name,
    act: run.actNumber,
    floor: run.floor,
    fightsWon: run.stats.fightsWon,
    damageDealt: run.stats.damageDealt,
    damageTaken: run.stats.damageTaken,
    name: run.customization && run.customization.name,
    custom: isCustomRun(run.custom),
    ascension: (run.custom && run.custom.ascension) || 0,
    bosses: [...(run.bossesBeaten || [])],
    maxClassLevel: peakClassLevel(run),
    bossGroups: structuredClone(run.bossGroups || {}),
  };
}

// main.js 1618-1626
function rollDrop(ctx, source) {
  const meta = ctx.saves.loadMeta();
  return rollArmamentDrop(ctx.registries, ctx.rng, { source, found: meta.found || [], carried: carriedIds(ctx.run.loadout) });
}

// main.js 1636-1669
function collectArmament(ctx, id, source) {
  if (!id) return false;
  const stored = addToStorage(ctx.run.loadout, id, ctx.registries.balance.equipment.storageSlots || 8);
  if (!stored) return false;
  recordCollectedArmament(ctx, id, source);
  return true;
}
function recordCollectedArmament(ctx, id, source) {
  const { run, registries, saves } = ctx;
  if (!carriedIds(run.loadout).includes(id)) return;
  if ((registries.balance.equipment.drops || {}).permanentOnFind) {
    const meta = saves.loadMeta();
    if (!(meta.found || []).includes(id)) {
      meta.found = [...(meta.found || []), id];
      const progressionMode = isCustomRun(run.custom) ? 'custom' : 'normal';
      const recorded = recordArmamentDiscovery(meta, id, {
        progressionMode, source, runSeed: run.seedString,
        receiptLimit: registries.balance.equipment.startingKitDiscovery.receiptLimit,
      });
      saves.saveMeta(recorded.meta);
    }
  }
  return true;
}

// main.js 1671-1679
function finishRun(ctx, victory) {
  const { registries, saves } = ctx;
  const result = runResult(ctx, victory);
  const meta = saves.recordResult(result);
  meta.progress = recordProgress(meta.progress, result);
  const fresh = evaluateUnlocks(registries.unlocks, meta);
  if (fresh.length) meta.unlocked = [...(meta.unlocked || []), ...fresh];
  saves.saveMeta(meta);
  return { result, earned: fresh.map((id) => registries.unlocks.find((u) => u.id === id)).filter(Boolean).map((u) => u.id) };
}

// main.js 1995-2018
function combatMods(ctx, pool) {
  const { run, registries } = ctx;
  const mods = run.custom ? activeMods(run.custom) : {};
  let hpMult = 1;
  const enemyStatuses = [];
  const playerStatuses = [];
  const cm = registries.balance.customMods;
  if ((pool === 'elite' || pool === 'boss') && mods.toughElites) hpMult *= cm.toughElitesHpMult;
  if (pool === 'boss' && mods.bigBosses) hpMult *= cm.bigBossesHpMult;
  if (!run.journey && Array.isArray(run.seatOrder)) hpMult *= seatTierHpMult(registries, currentSeat(ctx), contentAct(ctx));
  if (mods.deadlyEnemies) enemyStatuses.push({ status: 'strength', stacks: 1 });
  if (mods.glassCannon) playerStatuses.push({ status: 'glassCannon', stacks: 1 });
  if (mods.endless) {
    const { loop } = endlessActInfo(run.actNumber);
    if (loop > 0) {
      hpMult *= 1 + ENDLESS_HP_PER_LOOP * loop;
      enemyStatuses.push({ status: 'strength', stacks: ENDLESS_STR_PER_LOOP * loop });
    }
  }
  return { hpMult, enemyStatuses, playerStatuses };
}

// main.js 2105-2185 (a fresh entry: no snapshot to resume). Returns the outcome with the createCombat arguments.
function enterCombat(ctx, nodeId, encounterId) {
  const { run, registries, saves } = ctx;
  run.combatEntered = { nodeId, encounterId };
  if (run.journey && !run.legacyDungeon) throw new Error('oracle-loop: journeys are not recorded');
  const enc = registries.encounters.get(encounterId);
  const cm = combatMods(ctx, enc.pool);
  const args = {
    ratingsRules: registries.balance.combatRatings || null,
    handRules: resolveHandRules(saves.loadMeta().settings || {}, contentBundle.attributes),
    player: {
      classId: run.class, attributes: run.attributes, derivedStatRuleSnapshot: run.derivedStatRuleSnapshot, skills: run.skills,
      coreTags: run.coreTags, maxHp: run.maxHp, hp: run.hp, maxMana: run.maxMana, mana: run.mana, maxStamina: run.maxStamina,
      stamina: run.stamina, energyMax: run.energyMax, drawPerTurn: run.drawPerTurn, damageBySchoolAdd: run.damageBySchoolAdd,
      equipmentProfileRuleSnapshot: run.equipmentProfileRuleSnapshot, equipmentAttackSlotCount: run.equipmentAttackSlotCount,
      removedAttackSlotIds: run.removedAttackSlotIds, equipmentPoolDeficits: run.equipmentPoolDeficits,
      itemUpgradeLevels: run.itemUpgradeLevels, itemMounts: run.itemMounts, armamentLevels: run.armamentLevels, deck: run.deck,
      relicIds: run.relics, flasks: run.flasks, flaskCharges: run.flaskCharges, loadout: run.loadout,
    },
    enemyIds: enc.enemies,
    hpMult: cm.hpMult,
    enemyStatuses: cm.enemyStatuses,
    swapCostRule: resolveSwapCostRule(registries, saves.loadMeta()),
    playerStatuses: [...cm.playerStatuses, ...runMods(registries, run.loadout, run.class).startStatuses],
  };
  ctx.fight = { enc, args };
  // Recorded without the player block (the run's own fields, RunCombat.CreateArgs' parity) and the two settings-only
  // documents (ratings rules, hand rules); the C# fight step starts the combat from its own full arguments.
  const shown = plain(args);
  for (const key of ['player', 'ratingsRules', 'handRules']) delete shown[key];
  return { kind: 'fight', encounterId, pool: enc.pool, args: shown };
}

// main.js 2080-2084
function openLegacyEntrance(ctx, encounterId, nodeId) {
  const def = dungeonForEncounter(encounterId);
  if (!def) return false;
  beginDungeon(ctx.run, def, nodeId);
  return true;
}

// main.js 2086-2103
function startFight(ctx, pool, nodeId) {
  const { run, registries, rng } = ctx;
  if (run.journey) throw new Error('oracle-loop: journeys are not recorded');
  if (pool === 'normal' && run.custom && activeMods(run.custom).allElite) pool = 'elite';
  const encounterId = pool === 'boss'
    ? bossEncounterForNode(registries, run.mapGraph, nodeId, { seat: currentSeat(ctx), tier: contentAct(ctx) })
    : rollEncounter(registries, rng, { pool, seat: currentSeat(ctx), exclude: run.lastEncounters });
  if (pool === 'boss' && openLegacyEntrance(ctx, encounterId, nodeId)) return { kind: 'dungeon', dungeonId: run.legacyDungeon.id, encounterId };
  if (pool === 'normal') {
    run.lastEncounters.push(encounterId);
    if (run.lastEncounters.length > 2) run.lastEncounters.shift();
  }
  return enterCombat(ctx, nodeId, encounterId);
}

// main.js 1921-1990
function enterNode(ctx, nodeId) {
  const { run, registries, rng } = ctx;
  const node = run.mapGraph.nodes[nodeId];
  run.mapNodeId = nodeId;
  if (!run.path.includes(nodeId)) run.path.push(nodeId);
  run.floor = node.floor;
  let kind = node.type;
  if (kind === 'event') {
    const res = node.resolved || { kind: 'fight' };
    if (res.kind === 'event') {
      run.seenEvents.push(res.eventId);
      return { kind: 'event', eventId: res.eventId };
    }
    kind = res.kind;
  }
  switch (kind) {
    case 'monster':
    case 'fight':
      return startFight(ctx, 'normal', nodeId);
    case 'elite':
      return startFight(ctx, 'elite', nodeId);
    case 'boss':
      return startFight(ctx, 'boss', nodeId);
    case 'shrine':
      return { kind: 'rest', location: node.type === 'event' ? CAMP_LOCATION : 'shrine' };
    case 'merchant':
      return { kind: 'merchant' };
    case 'treasure': {
      const relicId = rollRelicReward(registries, rng, run.relics);
      const armamentId = rollDrop(ctx, 'treasure');
      return { kind: 'treasure', rewards: { relicId, armamentId, title: 'TREASURE' } };
    }
    default:
      throw new Error(`Unknown node kind '${kind}'`);
  }
}

// main.js 2274-2277
function victoryTitle(ctx, enc) {
  if (enc.pool === 'boss') return `${ctx.registries.enemies.get(enc.enemies[0]).name.toUpperCase()} FALLS`;
  return enc.pool === 'elite' ? 'ELITE VANQUISHED' : 'VICTORY';
}

// main.js 2412-2440
function rollSkillDrafts(ctx, pool) {
  const { run, rng, registries } = ctx;
  const perDoor = registries.balance.skill.draftsPerCombat;
  const out = [];
  for (const track of skillTracks(registries)) {
    const row = run.skills && run.skills[track.id];
    if (!row || !(row.pendingDrafts > 0)) continue;
    for (let i = 0; i < Math.min(perDoor, row.pendingDrafts); i++) {
      const cardIds = rollSkillDraftIds(registries, rng, { classId: run.class, loadout: run.loadout, skillId: track.id, level: row.level, pool, flatRarity: chaosRewardsOn(ctx) });
      if (cardIds.length) out.push({ skillId: track.id, level: row.level, cardIds });
    }
  }
  return out;
}
function rollClassDrafts(ctx) {
  const { run, rng, registries } = ctx;
  const row = run.skills && run.skills[classSkillId(run.class)];
  if (!row || !(row.pendingDrafts > 0)) return [];
  const nodeIds = rollClassDraftIds(registries, rng, { classId: run.class, coreTags: run.coreTags, level: row.level });
  return nodeIds.length ? [{ classId: run.class, level: row.level, nodeIds }] : [];
}

// main.js 2441-2455
function beginPendingReward(ctx, rewards, { source, after }) {
  ctx.run.pendingReward = {
    schemaVersion: 1,
    source,
    after,
    rewards: structuredClone(rewards),
    states: rewards.smithingStoneReceipt?.amount > 0 ? { smithingStone: 'taken' } : {},
    chosenCardId: null,
    chosenDraftCardIds: {},
    chosenDraftNodeIds: {},
  };
}

// main.js 2279-2411. Returns the outcome the caller routes on.
function onCombatEnd(ctx, result, combat, enc) {
  const { run, rng, registries } = ctx;
  run.flasks = combat.player.flasks;
  run.flaskCharges = combat.player.flaskCharges ? { ...combat.player.flaskCharges } : run.flaskCharges;
  for (const field of ['hp', 'mana', 'stamina']) {
    run[field] = combat.player[field];
    const maxField = `max${field[0].toUpperCase()}${field.slice(1)}`;
    run[maxField] = combat.player[maxField];
  }
  run.equipmentPoolDeficits = { ...combat.equipmentPoolDeficits };
  const trackReceipt = skillXpReceipt(combat);
  applySkillXp(registries, run, trackReceipt);
  const classAward = awardClassXp(registries, run, { victory: result === 'victory', pool: enc.pool });
  const levelAward = awardLevelXp(registries, run, combatLevelXp(registries, {
    victory: result === 'victory', pool: enc.pool, kills: combat.eventLog.filter((e) => e.type === 'enemyDied').length,
  }), { pointsPerLevel: resolveLevelUpValue(ctx.saves.loadMeta().settings) });
  const xpGains = combatXpGains({ receipt: trackReceipt, awards: [classAward], levelGained: levelAward.gained });
  stampDeck(registries, run, undefined, { adoptEquipmentBonuses: combat.equipmentChanged });

  if (result !== 'victory') {
    run.hp = 0;
    const earned = finishRun(ctx, false);
    return { outcome: 'defeat', ...earned };
  }
  run.stats.fightsWon += 1;
  if (run.legacyDungeon) resolveDungeonNode(run);
  else if (run.journey) throw new Error('oracle-loop: journeys are not recorded');
  run.combatEntered = null;
  const smithingStoneReceipt = grantSmithingReward(
    registries, run, enc.pool,
    `combat:${run.actNumber}:${run.floor}:${run.mapNodeId || 'unknown'}${run.legacyDungeon ? `:${run.legacyDungeon.current}` : ''}:${enc.pool}`,
  );
  if (enc.pool === 'boss') {
    run.bossesBeaten = run.bossesBeaten || [];
    for (const id of enc.enemies) if (!run.bossesBeaten.includes(id)) run.bossesBeaten.push(id);
    run.bossGroups = run.bossGroups || {};
    const held = [...new Set(equippedPieces(registries, run.loadout, run.class).flatMap((piece) => piece.itemTypeTags || []))];
    for (const id of enc.enemies) run.bossGroups[id] = [...new Set([...(run.bossGroups[id] || []), ...held])];
    if (!run.legacyDungeon && ((run.journey && run.journey.currentNodeId === run.journey.anchors.final) || (run.actNumber >= 3 && !endlessOn(ctx)))) {
      const earned = finishRun(ctx, true);
      return { outcome: 'victory', ...earned };
    }
    const bossArmament = rollDrop(ctx, 'boss');
    const drops = registries.balance.equipment.drops || {};
    const bossDrafts = rollSkillDrafts(ctx, 'boss');
    const bossClassDrafts = rollClassDrafts(ctx);
    const bossRewards = {
      title: victoryTitle(ctx, enc),
      cinders: rollRuneReward(registries, rng, 'boss', run.relics) + (bossArmament ? 0 : drops.consolationCinders || 0),
      classDrafts: bossClassDrafts,
      skillDrafts: bossDrafts,
      cardIds: bossDrafts.length || bossClassDrafts.length ? [] : rollCardRewardIds(registries, rng, { classId: run.class, pool: 'boss', relicIds: run.relics, flatRarity: chaosRewardsOn(ctx) }),
      relicId: rollRelicReward(registries, rng, run.relics, { rarities: ['boss'] }),
      armamentId: bossArmament,
      smithingStoneReceipt,
      xpGains,
    };
    beginPendingReward(ctx, bossRewards, { source: 'boss', after: run.journey || run.legacyDungeon ? 'map' : 'advanceAct' });
    return { outcome: 'reward' };
  }
  const drafts = rollSkillDrafts(ctx, enc.pool);
  const classDrafts = rollClassDrafts(ctx);
  const rewards = {
    title: victoryTitle(ctx, enc),
    cinders: rollRuneReward(registries, rng, enc.pool, run.relics),
    classDrafts,
    skillDrafts: drafts,
    cardIds: drafts.length || classDrafts.length ? [] : rollCardRewardIds(registries, rng, { classId: run.class, pool: enc.pool, relicIds: run.relics, flatRarity: chaosRewardsOn(ctx) }),
    flaskId: rollFlaskDrop(registries, rng, run),
    relicId: enc.pool === 'elite' ? rollRelicReward(registries, rng, run.relics) : null,
    armamentId: rollDrop(ctx, enc.pool),
    smithingStoneReceipt,
    xpGains,
  };
  beginPendingReward(ctx, rewards, { source: enc.pool, after: 'map' });
  return { outcome: 'reward' };
}

// reward.js mountRewards (84-227, 370-378, 744-746) + Continue (466-484), then main.js 2459-2479 (the checkpoint's
// onDone) or the treasure door's onDone (1972-1977). `mode` is the rewardCollect dial; the auto pick draws on
// 'cardRewards'. Returns the keys taken in order and the checkpoint's `after`.
function claimRewards(ctx, { rewards, checkpoint, source, mode }) {
  const { run, registries, rng, saves } = ctx;
  const plan = rewardPlan(rewards, {
    flaskSlotsFree: Math.max(0, flaskSlotCap(registries.balance) - run.flasks.length),
    armamentSlotsFree: Math.max(0, (registries.balance.equipment.storageSlots || 8) - (((run.loadout || {}).storage) || []).length),
  });
  const states = { ...(checkpoint?.states || {}), ...(rewards.smithingStoneReceipt?.amount > 0 ? { smithingStone: 'taken' } : {}) };
  let chosenCardId = checkpoint?.chosenCardId || null;
  const chosenDraftCardIds = { ...(checkpoint?.chosenDraftCardIds || {}) };
  const chosenDraftNodeIds = { ...(checkpoint?.chosenDraftNodeIds || {}) };
  const persistProgress = () => {
    if (checkpoint) {
      checkpoint.states = { ...states };
      checkpoint.chosenCardId = chosenCardId;
      checkpoint.chosenDraftCardIds = { ...chosenDraftCardIds };
      checkpoint.chosenDraftNodeIds = { ...chosenDraftNodeIds };
    }
  };
  const recordSeen = (kind, ids) => {
    if (!ids.length) return;
    const m = saves.loadMeta() || {};
    const seen = { ...(m.seen || {}) };
    const key = { card: 'cards', relic: 'relics', flask: 'flasks' }[kind];
    if (!key) return;
    seen[key] = [...new Set([...(seen[key] || []), ...ids])];
    saves.saveMeta({ ...m, seen });
  };
  const apply = {
    cinders(row) { run.cinders += row.amount; return true; },
    smithingStone() { return false; },
    card(row) {
      run.deck.push({ instanceId: `r${run.deck.length}_${row.cardId}`, cardId: row.cardId, upgraded: false });
      chosenCardId = row.cardId;
      return true;
    },
    classDraft(row) {
      if (!pickClassNode(registries, run, row.nodeId)) return false;
      if (!spendSkillDraft(run, classSkillId(run.class))) { run.coreTags.pop(); return false; }
      chosenDraftNodeIds[row.key] = row.nodeId;
      return true;
    },
    skillDraft(row) {
      if (!spendSkillDraft(run, row.skillId)) return false;
      run.deck.push({ instanceId: `r${run.deck.length}_${row.cardId}`, cardId: row.cardId, upgraded: skillUpgradesCards(registries, skillLevel(run, row.skillId)) });
      chosenDraftCardIds[row.key] = row.cardId;
      return true;
    },
    flask(row) { run.flasks.push({ flaskId: row.flaskId }); recordSeen('flask', [row.flaskId]); return true; },
    relic(row) { run.relics.push(row.relicId); syncFlaskGrowth(registries, run); recordSeen('relic', [row.relicId]); return true; },
    armament(row) { return collectArmament(ctx, row.armamentId, source) !== false; },
  };
  const taken = [];
  // grantCinders (370-378), at mount
  const cinderRow = plan.rows.find((r) => r.kind === 'cinders');
  if (cinderRow && !states.cinders && !cinderRow.blockedBy && apply.cinders(cinderRow)) { states.cinders = 'taken'; persistProgress(); taken.push('cinders'); }
  // Continue (466-484)
  const pickFn = (n) => rng.int('cardRewards', 0, n - 1);
  const { take } = resolveContinue(plan, states, mode, pickFn);
  for (const row of take) {
    if (apply[row.kind](row)) { states[row.key] = 'taken'; persistProgress(); taken.push(row.key); }
  }
  let after = null;
  if (checkpoint) {
    after = checkpoint.after;
    delete run.pendingReward;
  }
  return { taken, chosenCardId, after };
}

// main.js 2522-2589 showRest + rest.js actions. `actions` is the policy's list; returns the receipts the screen reads.
function restVisit(ctx, locationId, actions) {
  const { run, registries, rng, saves } = ctx;
  if (locationId) ctx.restLocationId = locationId;
  const healMult = run.custom && activeMods(run.custom).lessHealing ? registries.balance.customMods.lessHealingMult : 1;
  const { counts } = resolveGraceRefill(saves.loadMeta().settings || {});
  const restState = run.legacyDungeon?.activeRest || null;
  const visit = createLocationVisit({ run, registries, rng }, ctx.restLocationId, { healMult, refillCounts: counts });
  if (!restState?.refilled) {
    arriveAt(visit);
    if (restState) restState.refilled = true;
  }
  const services = visit.services.smith ? smithServicesAt(registries, 'shrine', rng) : null;
  const multiUse = settingOn(saves.loadMeta().settings, 'shrineMultiUse');
  const opened = {
    location: ctx.restLocationId, tags: visit.tags, services: visit.services, restDenied: visit.restDenied,
    refill: plain(visit.refill), smith: services ? plain(services) : null, multiUse,
  };
  const receipts = [];
  let rested = false;
  let left = false;
  for (const action of actions) {
    if (left) break;
    const relicNoRest = !!visit.restDenied;
    const noRest = relicNoRest || (multiUse && rested);
    const preview = relicNoRest ? null : previewRest(visit);
    const offered = visit.services.smith ? (services && Array.isArray(services.services) ? services.services : ['upgrade']) : [];
    switch (action.op) {
      case 'rest': {
        if (noRest) { receipts.push({ op: 'rest', refused: relicNoRest ? 'relic' : 'rested', preview: plain(preview) }); break; }
        const r = restAt(visit);
        receipts.push({ op: 'rest', preview: plain(preview), heal: r.heal, mana: r.mana, hp: r.hp, maxHp: r.maxHp, manaAfter: r.manaAfter });
        if (multiUse) rested = true; else left = true;
        break;
      }
      case 'flask': {
        if (!visit.services.flasks) { receipts.push({ op: 'flask', refused: 'service' }); break; }
        const charge = flaskChargePlan(registries, run.flaskCharges);
        const row = charge.rows.find((r) => r.kind === action.kind);
        const allowed = action.step > 0 ? row.canAdd : row.canSub;
        if (!allowed) { receipts.push({ op: 'flask', refused: 'edge' }); break; }
        const partner = action.step > 0 ? row.donor : row.receiver;
        moveFlaskCharge(registries, run.flaskCharges, action.step > 0 ? { from: partner, to: action.kind } : { from: action.kind, to: partner });
        receipts.push({ op: 'flask', charges: plain(run.flaskCharges) });
        break;
      }
      case 'level': {
        const level = levelUpPlan(registries, run);
        if (!(level.offerable && visit.services.levelUp)) { receipts.push({ op: 'level', refused: 'offer' }); break; }
        // The allocation card: pending points per attribute in the table's order, at most `points` in total.
        const pending = Object.fromEntries(level.attributes.map((a) => [a.id, 0]));
        let budget = level.points;
        for (const id of action.attrs) { if (budget > 0 && id in pending) { pending[id] += 1; budget -= 1; } }
        for (const attr of level.attributes) for (let i = 0; i < pending[attr.id]; i++) applyLevelUp(registries, run, attr.id);
        receipts.push({ op: 'level', assigned: pending, points: run.level.unspentPoints });
        break;
      }
      case 'smith': {
        const smith = smithingPlan(registries, run);
        const canInspect = offered.includes('upgrade') && smith.candidates.length > 0;
        const candidate = canInspect ? smith.candidates.filter((c) => c.affordable)[action.pick % Math.max(1, smith.candidates.filter((c) => c.affordable).length)] : null;
        if (!candidate) { receipts.push({ op: 'smith', refused: canInspect ? 'stones' : 'none' }); break; }
        const receipt = commitSmithing(registries, run, candidate.itemRef);
        receipts.push({ op: 'smith', itemRef: receipt.itemRef, afterLevel: receipt.afterLevel, spent: receipt.spent });
        if (!multiUse) left = true;
        break;
      }
      case 'extract':
      case 'install': {
        const plan = offered.includes(action.op) ? (action.op === 'extract' ? extractionPlan(registries, run) : installPlan(registries, run)) : null;
        if (!plan || !plan.candidates.length) { receipts.push({ op: action.op, refused: 'none' }); break; }
        const candidate = plan.candidates[action.pick % plan.candidates.length];
        const mount = candidate.mounts[action.pick % candidate.mounts.length];
        const receipt = action.op === 'extract'
          ? commitExtraction(registries, run, candidate.itemRef, mount.mountKey)
          : commitInstall(registries, run, candidate.itemRef, mount.mountKey, mount.cards[action.pick % mount.cards.length].instanceId);
        receipts.push({ op: action.op, itemRef: receipt.itemRef, mountKey: receipt.mountKey, cardId: receipt.cardId, instanceId: receipt.instanceId });
        if (!multiUse) left = true;
        break;
      }
      default:
        throw new Error(`unknown rest action ${action.op}`);
    }
  }
  // onDone (2577-2584): LEAVE, or the visit ended by an action.
  leaveLocation(visit);
  if (run.legacyDungeon?.activeRest) resolveDungeonNode(run);
  return { opened, receipts };
}

// main.js 2029-2044
function enterDungeonLocation(ctx) {
  const { run, registries, rng } = ctx;
  switch (dungeonNodeAction(run)) {
    case 'rest':
      run.legacyDungeon.activeRest ||= { nodeId: run.legacyDungeon.current, refilled: false };
      return { kind: 'rest', location: null };
    case 'treasure': {
      const relicId = rollRelicReward(registries, rng, run.relics);
      const armamentId = rollDrop(ctx, 'treasure');
      resolveDungeonNode(run);
      beginPendingReward(ctx, { relicId, armamentId, title: 'TREASURE' }, { source: 'treasure', after: 'map' });
      return { kind: 'treasure' };
    }
    case 'combat': return enterCombat(ctx, run.legacyDungeon.parentNodeId, dungeonNode(run).encounter);
    case 'dialogue': return { kind: 'dialogue' };
    default: return { kind: 'map' };
  }
}

// main.js 2068-2078: leaving a cleared dungeon ends the run at the summit or advances the act.
function leaveLegacyDungeon(ctx) {
  const { run } = ctx;
  if (!run.legacyDungeon?.cleared) return { kind: 'refused' };
  delete run.legacyDungeon;
  if (run.actNumber >= 3 && !endlessOn(ctx)) {
    const earned = finishRun(ctx, true);
    return { kind: 'victory', ...earned };
  }
  advanceAct(ctx);
  return { kind: 'advanced' };
}

// ------------------------------------------------------------------ the fight bot

// Its own LCG (never the engine RNG).
function lcg(seed) { let s = seed >>> 0 || 1; return (n) => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s % n; }; }

// Plays a fight to its end: drink a Crimson charge when low, otherwise play cards (hand order shuffled by the bot),
// else end the turn. Commands are recorded compactly: 'e', 'f<kind>', 'p<instanceId>'. Every refused dispatch
// throws before touching the fight (checked by the replay below).
function playFight(combat, pick, cardBias, maxCommands) {
  const cmds = [];
  while (!combat.result && cmds.length < maxCommands) {
    const p = combat.player;
    if (p.hp * 10 < p.maxHp * 4 && p.flaskCharges && p.flaskCharges.hpCurrent > 0 && pick(3) === 0) {
      try { dispatch(combat, { type: 'useFlask', chargeKind: 'hp' }); cmds.push('fhp'); continue; } catch { /* refused */ }
    }
    let played = false;
    if (pick(10) < cardBias) {
      const hand = combat.piles.hand.map((c) => c.instanceId);
      for (let i = hand.length - 1; i > 0; i--) { const j = pick(i + 1); [hand[i], hand[j]] = [hand[j], hand[i]]; }
      for (const id of hand) {
        try { dispatch(combat, { type: 'playCard', cardInstanceId: id }); cmds.push(`p${id}`); played = true; break; } catch { /* next card */ }
      }
    }
    if (!played && !combat.result) { dispatch(combat, { type: 'endTurn' }); cmds.push('e'); }
  }
  return cmds;
}
// The fight's state for the replay self-check (serializeCombatSnapshot refuses a deck whose run-level effects minted
// the same instance id twice — two events that each add a card mint 'run1' — which play itself tolerates).
const fightState = (c) => plain({ player: c.player, enemies: c.enemies, piles: c.piles, turn: c.turn, phase: c.phase, result: c.result, events: c.eventLog.length });
function decode(cmd) {
  if (cmd === 'e') return { type: 'endTurn' };
  if (cmd[0] === 'f') return { type: 'useFlask', chargeKind: cmd.slice(1) };
  return { type: 'playCard', cardInstanceId: cmd.slice(1) };
}

// ------------------------------------------------------------------ sequences

const bundle = configuredContentBundle(contentBundle, {});
const baseRegistries = createRegistries(bundle);
const classes = bundle.classes.map((c) => c.id);
const restDeniers = baseRegistries.relics.all().filter((r) => r.passives && r.passives.restDenied).map((r) => r.id);
const restHealers = baseRegistries.relics.all().filter((r) => r.passives && r.passives.restHealMult).map((r) => r.id);

// Sequence variants: settings, Custom Climb rules, fight difficulty (the harness's enemy-HP ratio per door, applied
// on top of the recorded arguments), the map/rest/claim policies, and profile or run seeds.
const VARIANTS = [
  { name: 'climb', hp: { normal: 0.35, elite: 0.25, boss: 0.12 } },
  { name: 'climb-multiuse', settings: { shrineMultiUse: true }, hp: { normal: 0.35, elite: 0.25, boss: 0.12 } },
  { name: 'hard', hp: { normal: 1, elite: 1, boss: 1 } },
  { name: 'endless', custom: { ascension: 0, mods: { endless: true }, deckMode: 'standard' }, hp: { normal: 0.25, elite: 0.18, boss: 0.08 }, maxAct: 5 },
  { name: 'ascension', custom: { ascension: 6, mods: {}, deckMode: 'standard' }, hp: { normal: 0.35, elite: 0.22, boss: 0.1 } },
  { name: 'chaos', custom: { ascension: 0, mods: { allElite: true, chaosRewards: true, glassCannon: true, hoarder: true }, deckMode: 'standard' }, hp: { normal: 0.3, elite: 0.2, boss: 0.1 } },
  { name: 'scarce', custom: { ascension: 0, mods: { lessHealing: true, deadlyEnemies: true, toughElites: true, bigBosses: true }, deckMode: 'standard' }, hp: { normal: 0.35, elite: 0.22, boss: 0.1 } },
  { name: 'manual', settings: { rewardCollect: 'manual', levelUpValue: 3 }, hp: { normal: 0.35, elite: 0.25, boss: 0.12 } },
  { name: 'relics', relics: () => [...restHealers.slice(0, 1), ...restDeniers.slice(0, 1)], hp: { normal: 0.35, elite: 0.25, boss: 0.12 } },
  { name: 'veteran', profile: 'veteran', hp: { normal: 0.35, elite: 0.25, boss: 0.12 }, keepsake: true },
  { name: 'dungeon-seeker', prefer: ['boss', 'elite'], hp: { normal: 0.3, elite: 0.2, boss: 0.1 } },
  { name: 'rester', prefer: ['shrine', 'event', 'treasure'], settings: { shrineMultiUse: true, levelUpValue: 2 }, hp: { normal: 0.4, elite: 0.3, boss: 0.14 } },
  // Carries the two armaments whose arts a smith can lift out, so extraction and installation meet real mounts.
  { name: 'armoury', prefer: ['shrine'], settings: { shrineMultiUse: true }, storage: ['katana', 'greatsword'], hp: { normal: 0.35, elite: 0.25, boss: 0.12 } },
];

function veteranProfile(saves) {
  const meta = saves.loadMeta();
  meta.results = Array.from({ length: 20 }, (_, i) => ({ victory: i % 5 === 0, seed: `VET${i}`, class: classes[i % classes.length], act: 1 + (i % 3), floor: i }));
  meta.progress = { runs: 20, wins: 4, maxAct: 3, bosses: ['stitchedKing'], wonClasses: [classes[0]], maxClassLevel: 2, bossGroups: {} };
  meta.unlocked = ['winAsReaver'];
  meta.found = baseRegistries.equipment.armaments.slice(0, 6).map((a) => a.id);
  meta.seen = { cards: [], relics: [], flasks: [] };
  saves.saveMeta(meta);
}

function ensureMeta(file) {
  const meta = `${file}.meta`;
  if (existsSync(meta)) return;
  const guid = createHash('md5').update(`ashen-oracle-loop:${file.split(/[\\/]/).pop()}`).digest('hex');
  writeFileSync(meta, `fileFormatVersion: 2\nguid: ${guid}\n`);
}

let coverage = null;
if (process.argv.includes('--coverage')) {
  const { Session } = await import('node:inspector/promises');
  coverage = new Session();
  coverage.connect();
  await coverage.post('Profiler.enable');
  await coverage.post('Profiler.startPreciseCoverage', { callCount: true, detailed: true });
  await coverage.post('Profiler.takePreciseCoverage');
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));

const tally = {};
const count = (k) => { tally[k] = (tally[k] || 0) + 1; };
const index = [];

function runSequence(j) {
  const variant = VARIANTS[j % VARIANTS.length];
  const classId = classes[Math.floor(j / VARIANTS.length) % classes.length];
  const seed = sweepSeed(7000 + j);
  const settings = { ...(variant.settings || {}) };
  const storage = createMemoryStorage();
  const saves = createSaveManager(storage);
  saves.ensureProfile();
  if (Object.keys(settings).length) { const m = saves.loadMeta(); m.settings = { ...m.settings, ...settings }; saves.saveMeta(m); }
  if (variant.profile === 'veteran') veteranProfile(saves);
  const registries = makeRegistries({});
  const ctx = { registries, run: null, rng: null, saves, settings: saves.loadMeta().settings, restLocationId: 'shrine', fight: null };
  const keepsakes = registries.characterCreation.keepsakes || [];
  const keepsakeId = variant.keepsake && keepsakes.length ? keepsakes[j % keepsakes.length].id : null;
  newRun(ctx, { classId, seed, custom: variant.custom ? structuredClone(variant.custom) : undefined, keepsakeId });
  // The newRun inputs and the harness's own edits after it, so a chained replay can make the starting run in C# (D-102).
  const start = {
    classId, custom: variant.custom ? plain(variant.custom) : null, keepsakeId,
    storage: variant.storage ? [...variant.storage] : null, relics: variant.relics ? variant.relics() : null,
  };
  if (variant.storage) { ctx.run.loadout.storage = [...(ctx.run.loadout.storage || []), ...variant.storage.filter((id) => !(ctx.run.loadout.storage || []).includes(id))]; }
  if (variant.relics) { for (const id of variant.relics()) if (!ctx.run.relics.includes(id)) ctx.run.relics.push(id); syncFlaskGrowth(registries, ctx.run); }
  const pick = lcg(seed ^ 0x5bd1e995);
  const seq = {
    name: `seq-${String(j).padStart(3, '0')}-${classId}-${variant.name}`, classId, variant: variant.name, seed: ctx.rng.seed,
    settings: plain(ctx.settings),
    // The settings the loop reads, resolved as the settings screen resolves them (the C# loop takes them resolved).
    resolved: {
      pointsPerLevel: resolveLevelUpValue(ctx.settings), multiUse: settingOn(ctx.settings, 'shrineMultiUse'),
      rewardCollect: ctx.settings.rewardCollect === 'manual' ? 'manual' : 'auto', refillCounts: resolveGraceRefill(ctx.settings).counts,
      // newRun's settings-resolved inputs.
      advancedConfigSnapshot: plain(advancedConfigSnapshot(ctx.settings)), derivedStatOptions: plain(derivedStatDialOptions(ctx.settings)),
      prologue: shouldPlayPrologue(ctx.settings, ctx.settings?.prologueSeen === true),
    },
    start,
    init: { run: plain(ctx.run), rng: ctx.rng.getCounters(), profile: plain(saves.loadMeta()) }, steps: [],
  };
  let prev = { run: seq.init.run, rng: seq.init.rng, profile: seq.init.profile };
  const snap = (step) => {
    const now = { run: plain(ctx.run), rng: ctx.rng.getCounters(), profile: plain(saves.loadMeta()) };
    const r = diff(prev.run, now.run); if (r) step.r = r;
    const g = diff(prev.rng, now.rng); if (g) step.g = g;
    const p = diff(prev.profile, now.profile); if (p) step.p = p;
    // Self-check: the patches rebuild the documents exactly.
    for (const key of ['run', 'rng', 'profile']) {
      const rebuilt = applyPatch(prev[key], step[{ run: 'r', rng: 'g', profile: 'p' }[key]]);
      if (!same(rebuilt, now[key])) throw new Error(`${seq.name}: ${key} patch does not rebuild the document`);
    }
    prev = now;
    seq.steps.push(step);
    count(step.k + (step.o && step.o.kind ? `:${step.o.kind}` : step.o && step.o.outcome ? `:${step.o.outcome}` : ''));
  };
  const hpFor = (pool) => (variant.hp[pool] ?? 1);
  let ended = null;
  let guard = 0;

  // A fight after its entry: play it, self-check the command log, then the fight's end.
  const fight = () => {
    const { enc, args } = ctx.fight;
    const ratio = hpFor(enc.pool);
    const before = ctx.rng.getCounters();
    const live = createCombat({ ...args, hpMult: args.hpMult * ratio, registries, rng: ctx.rng });
    const cmds = playFight(live, pick, variant.name === 'hard' ? 8 : 10, 900);
    if (!live.result) return 'stuck';
    // Self-check: the recorded commands replay to the same end on a fresh fight from a plain copy of the arguments.
    const replayRng = createRng(ctx.rng.seed, before);
    const replay = createCombat({ ...plain(args), hpMult: args.hpMult * ratio, registries, rng: replayRng });
    for (const c of cmds) dispatch(replay, decode(c));
    if (!same(fightState(replay), fightState(live)) || !same(replayRng.getCounters(), ctx.rng.getCounters())) {
      throw new Error(`${seq.name}: the command log does not replay the fight`);
    }
    ctx.run.loadout = live.loadout;
    const out = onCombatEnd(ctx, live.result, live, enc);
    snap({ k: 'fight', i: { cmds, hp: ratio, result: live.result }, o: out });
    ctx.fight = null;
    if (out.outcome !== 'reward') return out.outcome;
    // The reward door, then (after a boss) the act.
    const checkpoint = ctx.run.pendingReward;
    const mode = settings.rewardCollect === 'manual' ? 'manual' : 'auto';
    const claimed = claimRewards(ctx, { rewards: checkpoint.rewards, checkpoint, source: checkpoint.source, mode });
    snap({ k: 'claim', i: { mode, from: 'pending' }, o: claimed });
    if (claimed.after === 'advanceAct') {
      if (ctx.run.actNumber >= (variant.maxAct || 99)) return 'cap';
      advanceAct(ctx);
      snap({ k: 'act', i: {}, o: {} });
    }
    return null;
  };

  const restPolicy = () => {
    const options = [
      [{ op: 'rest' }],
      [{ op: 'level', attrs: ['strength', 'dexterity', 'constitution', 'wisdom'].slice(0, 1 + pick(3)) }, { op: 'rest' }],
      [{ op: 'flask', kind: pick(2) ? 'hp' : 'mana', step: pick(2) ? 1 : -1 }, { op: 'rest' }],
      [{ op: 'smith', pick: pick(5) }],
      [{ op: 'extract', pick: pick(5) }],
      [{ op: 'install', pick: pick(5) }],
      [{ op: 'level', attrs: ['constitution', 'constitution', 'intelligence'] }, { op: 'smith', pick: pick(3) }, { op: 'rest' }],
      [{ op: 'flask', kind: 'hp', step: 1 }, { op: 'flask', kind: 'hp', step: 1 }, { op: 'smith', pick: 0 }, { op: 'rest' }, { op: 'rest' }],
      [{ op: 'extract', pick: pick(5) }, { op: 'install', pick: pick(5) }, { op: 'rest' }],
    ];
    return options[pick(options.length)];
  };

  const rest = (location) => {
    const actions = restPolicy();
    const out = restVisit(ctx, location, actions);
    snap({ k: 'rest', i: { location: ctx.restLocationId, actions }, o: out });
  };

  // A merchant visit or an event (another stream's port). The body returns the inputs it chose (an event's id, the
  // open affordable choices and the one taken), so a chained replay can drive the shop/events ports (D-101).
  const external = (what, body) => { const inputs = body() || {}; snap({ k: 'x', i: { what, ...inputs } }); };

  const dungeon = () => {
    let turns = 0;
    while (ctx.run.legacyDungeon && !ended && turns++ < 200) {
      const s = ctx.run.legacyDungeon;
      if (ctx.run.pendingReward) {
        const cp = ctx.run.pendingReward;
        const claimed = claimRewards(ctx, { rewards: cp.rewards, checkpoint: cp, source: cp.source, mode: settings.rewardCollect === 'manual' ? 'manual' : 'auto' });
        snap({ k: 'claim', i: { mode: settings.rewardCollect === 'manual' ? 'manual' : 'auto', from: 'pending' }, o: claimed });
        continue;
      }
      if (s.activeRest) { rest(null); continue; }
      if (s.pending) {
        const encounterId = dungeonNode(ctx.run).encounter;
        const action = continueDungeon(ctx.run);
        let o = { kind: action };
        if (action === 'combat') o = enterCombat(ctx, ctx.run.legacyDungeon.parentNodeId, encounterId);
        snap({ k: 'dungeon', i: { op: 'continue' }, o });
        if (action === 'combat') { const r = fight(); if (r) { ended = r; return; } }
        continue;
      }
      if (s.cleared) {
        const o = leaveLegacyDungeon(ctx);
        snap({ k: 'dungeon', i: { op: 'leave' }, o });
        if (o.kind === 'victory') ended = 'victory';
        return;
      }
      const action = dungeonNodeAction(ctx.run);
      if (action === 'dialogue') {
        const choices = dungeonChoices(ctx.run);
        const choice = choices[pick(choices.length)];
        const receipt = chooseDungeon(ctx.run, choice.id, ctx.rng);
        snap({ k: 'dungeon', i: { op: 'choose', choiceId: choice.id }, o: plain(receipt) });
        continue;
      }
      if (action === 'map') {
        // Travel one step toward the boss node (the harness's route): the neighbour nearest the boss by BFS.
        const def = dungeonDefinition(ctx.run);
        const dist = { [def.bossNode]: 0 };
        const queue = [def.bossNode];
        while (queue.length) {
          const at = queue.shift();
          for (const e of def.edges) {
            const other = e.a === at ? e.b : e.b === at ? e.a : null;
            if (other && dist[other] === undefined) { dist[other] = dist[at] + 1; queue.push(other); }
          }
        }
        // Unvisited rooms first (nearest the boss first), then the way back; a third of the time the second choice.
        const seen = new Set(ctx.run.legacyDungeon.visited);
        const options = dungeonNeighbors(ctx.run).sort((a, b) => (seen.has(a) - seen.has(b)) || dist[a] - dist[b] || (a < b ? -1 : 1));
        const to = pick(3) === 0 && options.length > 1 && !seen.has(options[1]) ? options[1] : options[0];
        const moved = travelDungeon(ctx.run, to);
        const o = moved ? enterDungeonLocation(ctx) : { kind: 'refused' };
        snap({ k: 'dungeon', i: { op: 'travel', to }, o });
        if (o.kind === 'fight') { const r = fight(); if (r) { ended = r; return; } }
        continue;
      }
      const o = enterDungeonLocation(ctx);
      snap({ k: 'dungeon', i: { op: 'inspect' }, o });
      if (o.kind === 'fight') { const r = fight(); if (r) { ended = r; return; } }
    }
    if (ctx.run.legacyDungeon && !ended) ended = 'dungeon-guard';
  };

  while (!ended && guard++ < 200) {
    const run = ctx.run;
    const graph = run.mapGraph;
    const options = run.mapNodeId == null ? graph.startIds : graph.nodes[run.mapNodeId].next;
    if (!options || !options.length) { ended = 'map-end'; break; }
    const prefer = variant.prefer || [];
    const typed = (id) => { const n = graph.nodes[id]; return n.type === 'event' ? 'event' : n.type; };
    const liked = options.filter((id) => prefer.includes(typed(id)));
    const nodeId = liked.length && pick(3) ? liked[pick(liked.length)] : options[pick(options.length)];
    const out = enterNode(ctx, nodeId);
    snap({ k: 'enter', i: { node: nodeId }, o: plain(out) });
    if (out.kind === 'fight') { const r = fight(); if (r) ended = r; continue; }
    if (out.kind === 'dungeon') { dungeon(); continue; }
    if (out.kind === 'rest') { rest(out.location); continue; }
    if (out.kind === 'treasure') {
      const mode = settings.rewardCollect === 'manual' ? 'manual' : 'auto';
      const claimed = claimRewards(ctx, { rewards: out.rewards, checkpoint: null, source: 'treasure', mode });
      snap({ k: 'claim', i: { mode, from: 'treasure' }, o: claimed });
      continue;
    }
    if (out.kind === 'merchant') {
      // The shop stream's body (main.js 1948-1966 and showShop's onLeave): stock rolled, nothing bought, left.
      external('merchant', () => {
        const stock = buildShopStock(registries, ctx.rng, ctx.run);
        const pm = (() => { const mods = activeMods(ctx.run.custom); let m = 1; if (mods.expensiveShops) m *= registries.balance.customMods.expensiveShopsMult; if (mods.hoarder) m *= registries.balance.customMods.hoarderShopMult; return m; })();
        if (pm !== 1) { for (const kind of ['cards', 'relics', 'flasks']) for (const item of stock[kind]) item.cost = Math.ceil(item.cost * pm); stock.removeCost = Math.ceil(stock.removeCost * pm); }
        stock.smith = smithServicesAt(registries, 'merchant', ctx.rng);
        ctx.run.shopStock = stock;
        ctx.run.shopStock = null;
      });
      continue;
    }
    if (out.kind === 'event') {
      external('event', () => {
        const def = registries.events.get(out.eventId);
        if (questChainForEvent(registries.questChains, out.eventId)) return { eventId: out.eventId, quest: true, open: [], choiceId: null };
        const open = availableEventChoices(eventChoicesWithHistory(def), ctx.run).map((e) => e.choice).filter((c) => choiceAffordable(c, ctx.run));
        const choiceId = open.length ? open[pick(open.length)].id : null;
        if (choiceId) commitEventChoice({ run: ctx.run, registries, rng: ctx.rng }, { eventId: out.eventId, choiceId });
        return { eventId: out.eventId, quest: false, open: open.map((c) => c.id), choiceId };
      });
      if (ctx.run.combatEntered) {
        // showEvent onDone (2636-2641)
        const encounterId = typeof ctx.run.combatEntered === 'string' ? ctx.run.combatEntered : ctx.run.combatEntered.encounterId;
        ctx.run.combatEntered = null;
        const o = enterCombat(ctx, ctx.run.mapNodeId, encounterId);
        snap({ k: 'eventFight', i: {}, o });
        const r = fight(); if (r) ended = r;
      }
      if (ctx.run.hp <= 0 && !ended) ended = 'event-death';
      continue;
    }
    throw new Error(`unhandled outcome ${out.kind}`);
  }
  seq.ended = ended || 'guard';
  count(`end:${seq.ended}`);
  return seq;
}

for (let j = 0; j < SEQUENCES; j++) {
  const seq = runSequence(j);
  const file = `${seq.name}.json`;
  writeFileSync(join(OUT, file), `${JSON.stringify(seq)}\n`);
  ensureMeta(join(OUT, file));
  index.push({ file, classId: seq.classId, variant: seq.variant, steps: seq.steps.length, ended: seq.ended, acts: seq.steps.filter((s) => s.k === 'act').length });
}

if (coverage) {
  const { result } = await coverage.post('Profiler.takePreciseCoverage');
  for (const script of result.filter((s) => s.url.includes('/src/')).sort((a, b) => (a.url < b.url ? -1 : 1))) {
    const names = script.functions.filter((f) => f.functionName && f.ranges[0].count > 0).map((f) => f.functionName);
    if (names.length) console.log(`${script.url.split('/src/')[1]}: ${[...new Set(names)].join(', ')}`);
  }
}

writeFileSync(join(OUT, 'index.json'), `${JSON.stringify({
  source: 'main.js controller harness (enterNode, startFight, enterCombat args, onCombatEnd routing, reward door, showRest, advanceAct, legacy dungeons, finishRun) over the shipped model/engine modules (shipped preset)',
  sequences: index, tally,
}, null, 1)}\n`);
ensureMeta(join(OUT, 'index.json'));
console.log(`oracle-loop: ${index.length} sequences, ${index.reduce((n, s) => n + s.steps, 0)} steps → Unity/Assets/Tests/Oracle/loop`);
console.log(JSON.stringify(tally));
