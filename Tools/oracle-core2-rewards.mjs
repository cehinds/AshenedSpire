#!/usr/bin/env node
// Core 2 rewards and combat-end oracle (WP-15, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED web for the configured `default` profile:
//   src/model/gameProfileRewards.js   gameProfileRewardOffers, claimGameProfileReward, claimGameProfileRewardToDeck, rewardDeckAddRefusal
//   engine/kernel/rewardPolicy.js     rewardGroupProblems, pickRewardCategory          src/model/stanceCoverage.js  reserveStanceCards
//   src/model/skills.js, classTree.js, engine/skillXp.js, engine/encounters.js   the skill / class ledgers a fight pays, the cinders
//   src/main.js onCombatEnd (3241-3340), beginGameProfileReward / showGameProfileReward (3900-3940)   the combat end of a configured run,
//   which the oracle mirrors line for line (main.js is not headless); a won fight leads to a persisted offer, the player takes one,
//   adds a card to the deck or leaves, and the climb goes on (advanceAct after a boss, the run's end after the last one).
//
//   node Tools/oracle-core2-rewards.mjs [--claims 8] [--climbs 8] [--fights 6] [--source <web checkout>] [--allow-dirty] [--with-start]
//   (--with-start adds each climb fight's opening projection, to find where a diverging fight began; the shipped fixtures do not carry it.)
//
// Files (Unity/Assets/Tests/Oracle/core2/rewards/, sorted keys; two runs are byte-identical):
//   units.json                      the pure policy: rewardGroupProblems, pickRewardCategory draws, the skill tracks and curve, awardSkillXp /
//                                   awardClassXp / rollRuneReward sequences
//   claims-<class>-seedNN.json      a bot works a WP-3 run through offers (combat and level), claims, Add-to-deck (with the refusals of every option
//                                   before it), invalid and repeated claims, deck removals, range and deck-size switches
//   climb-<class>-seedNN.json       a bot plays fights on a Core 2 climb (the host oracle's run: seats and map drawn, one RNG), settles each fight as
//                                   onCombatEnd does, and works the reward door; every step records the run's changed top-level keys and the RNG counters
//   index.json                      the sha, the profile hash and the tallies
// Test edits, recorded as steps and made the same way by the C# test: heal (Hit Points to the maximum), prime (a skill track a few XP short of its next level),
// node (the map node and floor the next fight is at), drop (cards out of the deck), range (the range policy switch), settings (the deck-size setting), and a
// fight's `weaken` (every enemy at most N Hit Points, so that bosses and elites are won as well as lost).
// A step's `changes` is { set: { key: value }, del: [key] } over the run document's top-level keys (the streamCounters aside), values canonical.
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, botLcg, legalIntents, pickIntent, projectFull } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'rewards');
const CLAIMS = Number(arg('--claims', '8'));
const CLIMBS = Number(arg('--climbs', '8'));
const FIGHTS = Number(arg('--fights', '6'));
const w = await loadWeb();
const reg = w.registries;
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [rewards, policy, stanceCoverage, skills, classTree, skillXp, encountersMod, progression, gameProfile, actmap, seatsMod, flasks] = await Promise.all([
  load('src/model/gameProfileRewards.js'), load('engine/kernel/rewardPolicy.js'), load('src/model/stanceCoverage.js'), load('src/model/skills.js'),
  load('src/model/classTree.js'), load('src/engine/skillXp.js'), load('src/engine/encounters.js'), load('src/model/gameProfileProgression.js'),
  load('src/model/gameProfile.js'), load('src/engine/actmap.js'), load('src/model/seats.js'), load('src/model/gracerefill.js'),
]);
const feats = await load('src/model/feats.js');

const classes = classIds(w);
const UNLOCKED = { gameProfileUnlocks: { [w.profile.id]: { completedRunIds: classes.map((_, i) => `r${i}`) } } };
const create = (seed, classId) => w.state.createRunState({ seed, classId, registries: reg, profileMeta: structuredClone(UNLOCKED) });
const call = (fn) => { try { return fn(); } catch (e) { return { error: e.message }; } };
const counters = (rng) => canon(rng.getCounters());
const refusalOf = (run, offerId, i, settings) => call(() => rewards.rewardDeckAddRefusal(reg, run, offerId, i, settings));

/** { set, del } over the run's top-level keys: what an operation changed (the stream counters are recorded apart). */
function snapshot(run) { const { streamCounters, ...rest } = run; return Object.fromEntries(Object.entries(rest).map(([k, v]) => [k, JSON.stringify(canon(v ?? null)) + (v === undefined ? '#u' : '')])); }
function changes(before, run) {
  const after = snapshot(run);
  const set = {}, del = [];
  for (const [k, v] of Object.entries(after)) if (before[k] !== v) set[k] = canon(run[k] ?? null);
  for (const k of Object.keys(before)) if (!(k in after)) del.push(k);
  return { set, del };
}
const runDocument = (run) => { const { gameProfile, runId, streamCounters, ...rest } = run; return canon({ ...rest, gameProfile: { id: gameProfile.id, hash: gameProfile.hash } }); };
const refOf = (c) => `${c.instanceId}:${c.cardId}${c.upgraded ? '+' : ''}`;

// ---------------------------------------------------------------------------------------------------------------
// Units
// ---------------------------------------------------------------------------------------------------------------
const cardGroups = [{ id: 'cards', weight: 60, categories: ['combatCards', 'weaponArts'] }, { id: 'equipment', weight: 10, categories: ['weapons', 'armor', 'relics'] }, { id: 'consumables', weight: 30, categories: ['potions'] }];
const allCategories = gameProfile.gameContent(reg, 'rewards').categories;

function groupUnits() {
  const cases = [
    { groups: cardGroups, categories: allCategories },
    { groups: [], categories: allCategories },
    { groups: 'nope', categories: allCategories },
    { groups: [{ id: 'a', weight: 1, categories: ['combatCards'] }], categories: allCategories },
    { groups: [{ id: 'a', weight: 1, categories: ['combatCards', 'weaponArts', 'weapons', 'armor', 'relics', 'potions'] }], categories: allCategories },
    { groups: [{ id: 'a', weight: -1, categories: allCategories }], categories: allCategories },
    { groups: [{ id: 'a', weight: 0, categories: allCategories }], categories: allCategories },
    { groups: [{ id: 'a', weight: 'x', categories: allCategories }], categories: allCategories },
    { groups: [{ id: 'a', weight: 1, categories: ['combatCards'] }, { id: 'a', weight: 1, categories: ['weaponArts', 'weapons', 'armor', 'relics', 'potions'] }], categories: allCategories },
    { groups: [{ id: '', weight: 1, categories: allCategories }], categories: allCategories },
    { groups: [{ id: 'a', weight: 1, categories: [] }], categories: allCategories },
    { groups: [{ id: 'a', weight: 1, categories: ['combatCards', 'combatCards'] }, { id: 'b', weight: 1, categories: ['weaponArts', 'weapons', 'armor', 'relics', 'potions'] }], categories: allCategories },
    { groups: [{ id: 'a', weight: 1, categories: ['bogus', 'combatCards'] }], categories: allCategories },
    { groups: [null], categories: allCategories },
    { groups: [{ id: 'a', weight: 2, categories: ['combatCards'] }, { id: 'b', weight: 0, categories: ['weaponArts'] }], categories: ['combatCards', 'weaponArts'] },
  ];
  return cases.map((c) => ({ ...c, problems: call(() => policy.rewardGroupProblems(c.groups, c.categories)) }));
}

function pickUnits() {
  const cases = [];
  const sets = [
    { categories: allCategories, groups: cardGroups },
    { categories: allCategories, groups: [] },
    { categories: ['combatCards', 'potions'], groups: cardGroups },
    { categories: ['relics'], groups: cardGroups },
    { categories: ['weapons', 'armor'], groups: [{ id: 'x', weight: 0, categories: ['weapons', 'armor'] }] },
    { categories: [], groups: cardGroups },
    { categories: allCategories, groups: [{ id: 'a', weight: 1, categories: ['combatCards'] }, { id: 'b', weight: 3, categories: ['weaponArts', 'weapons', 'armor', 'relics', 'potions'] }] },
  ];
  sets.forEach((set, si) => {
    for (let n = 0; n < 3; n++) {
      const seed = w.rngMod.sweepSeed(30000 + si * 10 + n);
      const rng = w.createRng(seed);
      const picks = [];
      for (let i = 0; i < 16; i++) picks.push(policy.pickRewardCategory(rng, 'cardRewards', set.categories, set.groups));
      cases.push({ seed, ...set, picks, counters: counters(rng) });
    }
  });
  return cases;
}

function skillUnits() {
  const tracks = skills.skillTracks(reg).map((t) => ({ id: t.id, kind: t.kind, label: t.label }));
  const kinds = [...new Set(tracks.map((t) => t.kind))];
  const curve = kinds.map((kind) => ({ kind, maxLevel: call(() => skills.skillMaxLevel(reg, kind)), costs: Array.from({ length: 14 }, (_, level) => call(() => skills.xpToNext(reg, kind, level))) }));
  const amounts = [0, -5, 3, 7, 100, 95, 250, 5000, 40000, 1e6, 12.9];
  const sequences = ['item:blade', 'item:shield', 'class:reaver', 'class:herald', 'item:magic-focus'].map((skillId) => {
    const run = create(w.rngMod.sweepSeed(31000), 'reaver');
    const steps = amounts.map((amount) => {
      const award = call(() => skills.awardSkillXp(reg, run, skillId, amount, { queueRewards: false }));
      return { amount, award: canon(award), row: canon(run.skills[skillId] ?? null) };
    });
    return { skillId, steps };
  });
  const bogus = call(() => skills.awardSkillXp(reg, create(w.rngMod.sweepSeed(31000), 'reaver'), 'item:nothing', 10, { queueRewards: false }));
  const classAwards = [];
  for (const classId of classes) {
    for (const pool of ['normal', 'elite', 'boss']) for (const victory of [true, false]) for (const multiplier of [1, 1.5, 0]) {
      const run = create(w.rngMod.sweepSeed(31100), classId);
      const award = call(() => classTree.awardClassXp(reg, run, { victory, pool, multiplier, queueRewards: false }));
      classAwards.push({ classId, pool, victory, multiplier, award: canon(award), row: canon(run.skills?.[`class:${classId}`] ?? null) });
    }
  }
  return { tracks, curve, sequences, bogus, classAwards };
}

function runeUnits() {
  const relicSets = [[], ['forsakenMedallion'], ['forsakenMedallion', 'ashenGrip'], reg.relics.all().map((r) => r.id)];
  const cases = [];
  for (const pool of ['normal', 'elite', 'boss']) relicSets.forEach((relics, ri) => {
    for (let n = 0; n < 4; n++) {
      const seed = w.rngMod.sweepSeed(32000 + n);
      const rng = w.createRng(seed);
      const amounts = [encountersMod.rollRuneReward(reg, rng, pool, relics), encountersMod.rollRuneReward(reg, rng, pool, relics)];
      cases.push({ pool, relics, seed, amounts, counters: counters(rng) });
    }
  });
  return cases;
}

// ---------------------------------------------------------------------------------------------------------------
// The reward door (main.js 3900-3940), shared by both scenario kinds
// ---------------------------------------------------------------------------------------------------------------
const DECK_SETTINGS = [{}, { deckMaxSize: 11, deckMaxUnlimited: false }, { deckMaxSize: 13, deckMaxUnlimited: false }, { deckMaxSize: 9, deckMaxUnlimited: false }];

/** main.js beginGameProfileReward. Returns what the door does next. */
function beginReward(run, rng, { source = 'combat', after = 'map', levelOffers = 0, progression: notices = [], noticeOnly = false } = {}) {
  noticeOnly ||= !gameProfile.gameModule(run, 'rewards');
  if (noticeOnly && !notices.length) return { next: after === 'victory' ? 'victory' : after === 'defeat' ? 'defeat' : after === 'advanceAct' ? 'advanceAct' : 'map' };
  const offerId = `${run.actNumber}:${run.mapNodeId}:${source}`;
  if (!noticeOnly) rewards.gameProfileRewardOffers(reg, run, rng, { offerId, source });
  run.gameProfilePendingReward = { offerId, source, after, levelOffers, ...(notices.length ? { progression: notices } : {}), ...(noticeOnly ? { noticeOnly } : {}) };
  return { next: 'door', offerId };
}

/** The progression modal's Continue, then a notice-only door's exit (showGameProfileReward). */
function acknowledge(run) {
  const pending = run.gameProfilePendingReward;
  if (!pending?.progression?.length) return { error: 'no notices' };
  progression.acknowledgeSkillLevelNotices(pending);
  if (pending.noticeOnly) {
    delete run.gameProfilePendingReward;
    return { next: pending.after === 'victory' ? 'victory' : pending.after === 'defeat' ? 'defeat' : pending.after === 'advanceAct' ? 'advanceAct' : 'map' };
  }
  return { next: 'door' };
}

/** The offer screen's Continue (onContinue). */
function leave(run, rng) {
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


/** The reward door's own steps (begin, read the notices, leave -- with level offers owed) for every kind of door, on a fresh run of each class. */
function doorUnits() {
  const variants = [
    { variant: 'levels', after: 'advanceAct', levelOffers: 2, noticeOnly: false, notices: true },
    { variant: 'plain', after: 'map', levelOffers: 0, noticeOnly: false, notices: false },
    { variant: 'noticeOnlyVictory', after: 'victory', levelOffers: 0, noticeOnly: true, notices: true },
    { variant: 'noticeOnlyDefeat', after: 'defeat', levelOffers: 0, noticeOnly: true, notices: false },
  ];
  const out = [];
  classes.forEach((classId, ci) => variants.forEach((spec, vi) => {
    const seed = w.rngMod.sweepSeed(33000 + ci * 10 + vi);
    const run = create(seed, classId);
    const rng = w.createRng(seed, run.streamCounters || {});
    run.actNumber = 1 + vi;
    run.mapNodeId = `n${vi}_0`;
    const notices = spec.notices ? [{ skillId: `class:${classId}`, label: classId, before: 0, after: 1 }] : [];
    const steps = [];
    const log = (op, result) => steps.push(canon({ op, result, pending: run.gameProfilePendingReward ?? null, offers: Object.keys(run.gameProfileRewardOffers || {}), counters: counters(rng) }));
    log('begin', beginReward(run, rng, { source: 'combat', after: spec.after, levelOffers: spec.levelOffers, progression: notices, noticeOnly: spec.noticeOnly }));
    for (let guard = 0; guard < 8 && run.gameProfilePendingReward; guard++) {
      if (run.gameProfilePendingReward.progression?.length) log('ack', acknowledge(run));
      else log('leave', leave(run, rng));
    }
    out.push({ classId, variant: spec.variant, seed, actNumber: run.actNumber, spec, steps });
  }));
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Claim scenarios
// ---------------------------------------------------------------------------------------------------------------
function claimScenario(classId, ci, index) {
  const seed = w.rngMod.sweepSeed(40000 + ci * 100 + index);
  const run = create(seed, classId);
  const rng = w.createRng(seed, run.streamCounters || {});
  const pick = botLcg(seed ^ 0x51ed2701);
  let settingsIndex = index % DECK_SETTINGS.length;
  const steps = [canon({ op: 'settings', settings: DECK_SETTINGS[settingsIndex], counters: counters(rng) })];
  const record = (op, fn, extra = {}) => {
    const before = snapshot(run);
    const result = call(fn);
    steps.push(canon({ op, ...extra, ...(result?.error ? { error: result.error } : { result }), changes: changes(before, run), counters: counters(rng) }));
    return result;
  };
  const OFFERS = 14;
  // Each scenario leans toward one kind of reward, so that the pools run dry (potion slots full, every weapon carried).
  const lean = ['potions', 'relics', 'weapons', 'armor', 'combatCards', null, 'potions', 'weaponArts'][index % 8];
  for (let k = 0; k < OFFERS; k++) {
    // State the bot sets up before a door: removals, the range switch, the deck-size setting.
    if (pick(4) === 0 && run.deck.length > 3) {
      const ids = [];
      for (let n = 0, count = 1 + pick(4); n < count && run.deck.length - ids.length > 1; n++) { const c = run.deck[pick(run.deck.length)]; if (!ids.includes(c.instanceId)) ids.push(c.instanceId); }
      const before = snapshot(run);
      run.deck = run.deck.filter((c) => !ids.includes(c.instanceId));
      w.state.syncZones(run);
      steps.push(canon({ op: 'drop', ids, changes: changes(before, run), counters: counters(rng) }));
    }
    if (pick(8) === 0) {
      const before = snapshot(run);
      run.rangePolicy = { ...run.rangePolicy, enabled: run.rangePolicy?.enabled === false };
      steps.push(canon({ op: 'range', enabled: run.rangePolicy.enabled, changes: changes(before, run), counters: counters(rng) }));
    }
    if (pick(5) === 0) { settingsIndex = pick(DECK_SETTINGS.length); steps.push({ op: 'settings', settings: DECK_SETTINGS[settingsIndex], counters: counters(rng) }); }
    const settings = DECK_SETTINGS[settingsIndex];
    const source = pick(4) === 0 ? 'level' : 'combat';
    const offerId = `${run.actNumber}:n${k}:${source}`;
    const offer = record('offer', () => structuredClone(rewards.gameProfileRewardOffers(reg, run, rng, { offerId, source })), { offerId, source });
    if (offer.error) continue;
    // The same offer asked again is the stored one: nothing moves.
    if (pick(3) === 0) record('offerAgain', () => structuredClone(rewards.gameProfileRewardOffers(reg, run, rng, { offerId, source })), { offerId, source });
    // The refusal of Add to deck for every option, before anything is claimed.
    const refusals = offer.options.map((_, i) => refusalOf(run, offerId, i, settings));
    steps.push(canon({ op: 'refusals', offerId, settings, refusals, counters: counters(rng) }));
    const n = offer.options.length;
    const actions = 1 + pick(3);
    for (let a = 0; a < actions; a++) {
      const roll = pick(12);
      const leaning = lean ? offer.options.findIndex((o) => o.kind === lean) : -1;
      const index = roll === 10 ? n + pick(3) : roll === 11 ? -1 : leaning >= 0 && pick(10) < 8 ? leaning : pick(n);
      if (roll < 5 || roll >= 10) record('take', () => canon(rewards.claimGameProfileReward(reg, run, offerId, index)), { offerId, index });
      else record('add', () => canon(rewards.claimGameProfileRewardToDeck(reg, run, offerId, index, settings)), { offerId, index, settings });
    }
    steps.push(canon({ op: 'refusalsAfter', offerId, settings, refusals: offer.options.map((_, i) => refusalOf(run, offerId, i, settings)), counters: counters(rng) }));
  }
  return canon({ classId, seed, steps, run: runDocument(run), counters: counters(rng) });
}

// ---------------------------------------------------------------------------------------------------------------
// Climb scenarios: fights, the combat end, the door
// ---------------------------------------------------------------------------------------------------------------
const ROUTES_ACTS = gameProfile.gameContent(reg, 'routes').acts;
const seatNow = (run) => seatsMod.seatAtTier(run.seatOrder, run.actNumber);

/** The host oracle's new climb (main.js beginPreparedRun + startClimb). */
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

/**
 * main.js onCombatEnd (3241-3340) for a configured run with skill progression, as far as the run goes: the pools written back, the skill and
 * class ledgers, then a death (the notices, then the run's end) or a win (cinders, the boss's record, the final act's boss, the offer).
 * What it leaves out is the screen, the audio, the victory beat, the profile's enemy knowledge bank (a Core run keeps none) and the receipts
 * only the spoils screen reads (combatXpGains, rollLevelChoices).
 */
function onCombatEnd(run, rng, combat, enc) {
  const result = combat.result;
  delete run.combatPendingOutcome;
  w.runCombat.runCombatEnd(run, combat);
  const configuredProgression = true;
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

/** A bot that plays cards when it can (nine times in ten), so that fights are won as well as lost. */
function pickGreedy(legal, pick) {
  const cards = legal.filter((i) => i.type === 'playCard');
  if (cards.length && pick(10) < 9 && !legal.some((i) => ['chooseReaction', 'chooseCards', 'chooseDiscard'].includes(i.type))) return cards[pick(cards.length)];
  return pickIntent('cards', legal, pick);
}

/** main.js combatMods (2880-2914) for a run with a seat order and no Custom Climb: the seat-tier HP ratio, or a boss's tier row for HP and damage. */
function combatMods(run, encounter) {
  let hpMult = 1, damageMult = 1;
  if (Array.isArray(run.seatOrder)) {
    const boss = seatsMod.bossTierScale(reg, { encounter, tier: run.actNumber });
    hpMult *= boss ? boss.hp : seatsMod.seatTierHpMult(reg, seatNow(run), run.actNumber);
    if (boss) damageMult = boss.damage;
  }
  return { hpMult, damageMult };
}

function playFight(run, rng, encounter, weaken) {
  const cm = combatMods(run, encounter);
  const combat = w.runCombat.createRunCombat({ registries: reg, rng, run, enemyIds: encounter.enemies, encounter, settings: {}, hpMult: cm.hpMult, enemyDamageMult: cm.damageMult });
  // A test edit of the fight before its first decision: every enemy at most `weaken` Hit Points (the C# test makes the same edit).
  if (weaken) for (const enemy of combat.enemies) enemy.hp = Math.min(enemy.hp, weaken);
  const start = process.argv.includes('--with-start') ? projectFull(combat) : undefined;
  const pick = botLcg(run.seed ^ 0x2c1b3c6d ^ (run.stats.fightsWon * 977 + run.actNumber));
  const intents = [];
  for (let i = 0; i < 400 && !combat.result; i++) {
    const legal = legalIntents(w, combat, { full: true });
    const intent = pickGreedy(legal, pick);
    if (!intent) break;
    w.combat.dispatch(combat, structuredClone(intent));
    intents.push(intent);
  }
  return { combat, intents, start };
}

function climbScenario(classId, ci, index) {
  const seed = w.rngMod.sweepSeed(50000 + ci * 100 + index);
  const { run, rng } = newClimb(seed, classId);
  const pick = botLcg(seed ^ 0x7f4a7c15);
  const all = encounters(w);
  const byPool = (pool) => all.filter((e) => e.pool === pool);
  const steps = [];
  let settingsIndex = 0;
  steps.push(canon({ op: 'settings', settings: DECK_SETTINGS[settingsIndex], counters: counters(rng) }));
  const record = (op, fn, extra = {}) => {
    const before = snapshot(run);
    const result = call(fn);
    const step = { op, ...extra, ...(result?.error ? { error: result.error } : { result }), changes: changes(before, run), counters: counters(rng) };
    steps.push(canon(step));
    return result;
  };
  let over = null;
  for (let k = 0; k < FIGHTS && !over; k++) {
    // The climb's act and node: the bot walks the acts (a boss ends one) and names each node as the map would.
    const kindRoll = pick(10);
    const pool = kindRoll < 6 ? 'normal' : kindRoll < 8 ? 'elite' : 'boss';
    const candidates = byPool(pool).filter((e) => e.seat === seatNow(run) || pool === 'boss');
    const encounter = (candidates.length ? candidates : byPool(pool))[pick((candidates.length ? candidates : byPool(pool)).length)];
    if (pick(3) === 0 && run.hp < run.maxHp) { const before = snapshot(run); run.hp = run.maxHp; steps.push(canon({ op: 'heal', changes: changes(before, run), counters: counters(rng) })); }
    // A track primed a few points short of its next level, so that the fight crosses it (and owes a notice).
    if (pick(3) === 0) {
      const skillId = pick(2) === 0 ? `class:${classId}` : 'item:blade';
      const kind = skills.skillKindOf(reg, skillId);
      const level = skills.skillLevel(run, skillId);
      const xp = Math.max(0, skills.xpToNext(reg, kind, level) - (1 + pick(12)));
      const before = snapshot(run);
      (run.skills ||= {})[skillId] = { xp, level, pendingDrafts: 0 };
      steps.push(canon({ op: 'prime', skillId, xp, level, changes: changes(before, run), counters: counters(rng) }));
    }
    const weaken = pick(5) < 3 ? 1 + pick(8) : 0;
    { const before = snapshot(run); run.mapNodeId = `n${k + 1}_0`; run.floor = k + 1; steps.push(canon({ op: 'node', id: run.mapNodeId, floor: run.floor, changes: changes(before, run), counters: counters(rng) })); }
    const startAct = run.actNumber;
    let fought = null;
    const before = snapshot(run);
    const outcome = call(() => {
      const { combat, intents, start } = playFight(run, rng, encounter, weaken);
      if (!combat.result) throw new Error('fight unfinished');
      fought = { intents: intents.map((i) => JSON.stringify(i)), ...(start ? { start } : {}) };
      return onCombatEnd(run, rng, combat, encounter);
    });
    if (outcome.error) throw new Error(`oracle-core2-rewards: ${classId}/${seed} ${encounter.id}: ${outcome.error}`);
    const door = run.gameProfilePendingReward ? canon(run.gameProfilePendingReward) : null;
    const offer = door && run.gameProfileRewardOffers?.[door.offerId] ? canon(run.gameProfileRewardOffers[door.offerId]) : null;
    steps.push(canon({ op: 'fight', encounterId: encounter.id, actBefore: startAct, weaken, ...fought, outcome, door, offer, changes: changes(before, run), counters: counters(rng) }));
    let next = outcome.next;
    // The door: notices first, then the offer (take, add to the deck, leave), then what comes after.
    for (let guard = 0; next === 'door' && guard < 8; guard++) {
      const pending = run.gameProfilePendingReward;
      if (pending.progression?.length) {
        const result = record('ack', () => acknowledge(run), {});
        next = result.next;
        continue;
      }
      const offerId = pending.offerId;
      const offerNow = run.gameProfileRewardOffers[offerId];
      if (pick(5) === 0) { settingsIndex = pick(DECK_SETTINGS.length); steps.push({ op: 'settings', settings: DECK_SETTINGS[settingsIndex], counters: counters(rng) }); }
      const settings = DECK_SETTINGS[settingsIndex];
      steps.push(canon({ op: 'refusals', offerId, settings, refusals: offerNow.options.map((_, i) => refusalOf(run, offerId, i, settings)), counters: counters(rng) }));
      const roll = pick(10);
      const index = pick(offerNow.options.length);
      if (roll < 4) record('take', () => canon(rewards.claimGameProfileReward(reg, run, offerId, index)), { offerId, index });
      else if (roll < 8) record('add', () => canon(rewards.claimGameProfileRewardToDeck(reg, run, offerId, index, DECK_SETTINGS[settingsIndex])), { offerId, index, settings: DECK_SETTINGS[settingsIndex] });
      const left = record('leave', () => leave(run, rng), {});
      next = left.next;
    }
    if (next === 'advanceAct') {
      const before2 = snapshot(run);
      advanceAct(run, rng);
      steps.push(canon({ op: 'advanceAct', changes: changes(before2, run), counters: counters(rng) }));
    } else if (next === 'victory' || next === 'defeat') { over = next; steps.push(canon({ op: 'end', outcome: next, counters: counters(rng) })); }
  }
  return canon({ classId, seed, steps, over, run: over ? null : runDocument(run), counters: counters(rng) });
}

// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
let bytes = 0;
const write = (name, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, name), text); bytes += Buffer.byteLength(text); };

const units = { groups: groupUnits(), picks: pickUnits(), skills: skillUnits(), runes: runeUnits(), doors: doorUnits() };
write('units.json', units);
const index = { source: 'model/gameProfileRewards.js, main.js onCombatEnd (configured profile default)', webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, routesActs: ROUTES_ACTS, claims: [], climbs: [],
  kinds: {}, refusals: {},
  tally: { claimSteps: 0, offers: 0, takes: 0, takeErrors: 0, adds: 0, addErrors: 0, fights: 0, victories: 0, defeats: 0, bosses: 0, elites: 0, notices: 0, advances: 0, ends: 0, levelOffers: 0 } };
const t = index.tally;
const bump = (table, key) => { table[key] = (table[key] || 0) + 1; };
function tallyKinds(step) {
  const options = step.op === 'offer' ? step.result?.options : step.op === 'fight' ? step.offer?.options : null;
  for (const o of options || []) bump(index.kinds, o.kind);
  if (step.refusals) for (const r of step.refusals) bump(index.refusals, typeof r === 'string' ? (r || 'ok') : 'error');
  if (step.op === 'take' || step.op === 'add') bump(index.refusals, step.error ? `${step.op}: ${step.error}` : `${step.op}: done`);
}
for (const [ci, classId] of classes.entries()) {
  for (let i = 0; i < CLAIMS; i++) {
    const name = `claims-${classId}-seed${String(i).padStart(2, '0')}.json`;
    const sc = claimScenario(classId, ci, i);
    write(name, sc);
    index.claims.push({ file: name, classId, seed: sc.seed });
    for (const s of sc.steps) {
      tallyKinds(s);
      t.claimSteps++;
      if (s.op === 'offer') { t.offers++; if (s.source === 'level') t.levelOffers++; }
      if (s.op === 'take') s.error ? t.takeErrors++ : t.takes++;
      if (s.op === 'add') s.error ? t.addErrors++ : t.adds++;
    }
  }
  for (let i = 0; i < CLIMBS; i++) {
    const name = `climb-${classId}-seed${String(i).padStart(2, '0')}.json`;
    const sc = climbScenario(classId, ci, i);
    write(name, sc);
    index.climbs.push({ file: name, classId, seed: sc.seed, over: sc.over });
    for (const s of sc.steps) {
      tallyKinds(s);
      if (s.op === 'fight') {
        t.fights++;
        s.outcome.result === 'victory' ? t.victories++ : t.defeats++;
        if (s.outcome.pool === 'boss') t.bosses++;
        if (s.outcome.pool === 'elite') t.elites++;
        if (s.outcome.notices.length) t.notices++;
      }
      if (s.op === 'take') s.error ? t.takeErrors++ : t.takes++;
      if (s.op === 'add') s.error ? t.addErrors++ : t.adds++;
      if (s.op === 'advanceAct') t.advances++;
      if (s.op === 'end') t.ends++;
    }
  }
}
write('index.json', index);
console.log(`oracle-core2-rewards: ${index.claims.length} claim scenarios (${t.offers} offers, ${t.takes}+${t.takeErrors} takes, ${t.adds}+${t.addErrors} adds), ${index.climbs.length} climbs (${t.fights} fights: ${t.victories} won, ${t.defeats} lost, ${t.bosses} bosses, ${t.elites} elites, ${t.notices} with level notices, ${t.advances} acts, ${t.ends} ends), ${(bytes / 1024 / 1024).toFixed(1)} MB -> Unity/Assets/Tests/Oracle/core2/rewards`);
