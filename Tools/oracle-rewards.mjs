#!/usr/bin/env node
// Post-combat parity oracle (US-11.1, PF-04/05). Records what the SHIPPED main.js onCombatEnd does to a run when a
// fight ends: the write-back from the combat, the skill/class/character ledgers, the smithing faucet, the reward
// rolls and the pending-reward checkpoint (or the defeat / summit outcome). main.js is a browser module, so the
// harness below re-implements onCombatEnd's ~130 lines FAITHFULLY, calling the real shipped model/engine functions
// in the same order and on the same RNG streams. It mirrors, line for line:
//   src/main.js 2279-2411  onCombatEnd(result, combat, enc)
//   src/main.js 2412-2456  rollSkillDrafts(pool), rollClassDrafts(), beginPendingReward(rewards, { source, after })
//   src/main.js 2273-2276  victoryTitle(enc)
//   src/main.js 1618-1626  rollDrop(source)            (meta.found is the case's `meta.found`)
//   src/main.js 1135-1137  endlessOn(); 2482-2484 chaosRewardsOn()
// Left out, because they touch no run field (D-067): audio/sfx, sendLanStatus, the victory beat, saves.clearRun,
// finishRun (profile meta: recordResult/recordProgress/evaluateUnlocks), mountGameOver, persist and mountRewards.
// Journeys and legacy dungeons (resolveDungeonNode / completeJourneyNode) are not recorded (D-066).
//
// Cases: (1) every golden combat in Unity/Assets/Tests/Oracle/combat replayed to its end in the shipped engine and
// handed to onCombatEnd with the run it started from; (2) generated fights over normal/elite/boss doors with run
// variants that reach level-ups, skill-track climbs and the auto-upgrade rule, class drafts, smithing claims,
// boss and summit outcomes, Chaos Rewards, Endless, consolation cinders and the flask pity counter.
//
//   node Tools/oracle-rewards.mjs [--source D:/repos/AshenSpire] [--generated 108] [--coverage]
//
// Writes Unity/Assets/Tests/Oracle/rewards/*.json (committed; CI has no access to the old repo). Only .json files
// are deleted on regeneration, so the Unity .meta files beside them survive; a missing .meta is created.
import { writeFileSync, mkdirSync, rmSync, readdirSync, readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const SOURCE = arg('--source', process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const GENERATED = Number(arg('--generated', '108'));
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'rewards');
const GOLDEN = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'combat');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng, sweepSeed } = await load('src/engine/rng.js');
const { createRunState } = await load('src/model/state.js');
const { createCombat, dispatch } = await load('src/engine/combat.js');
const { serializeCombatSnapshot, restoreCombatSnapshot } = await load('src/engine/combatSnapshot.js');
const { resolveHandRules } = await load('src/model/handRules.js');
const { resolveSwapCostRule, runMods, stampDeck, carriedIds, equippedPieces } = await load('src/model/loadout.js');
const { seatAtTier, seatTierHpMult } = await load('src/model/seats.js');
const { skillXpReceipt, applySkillXp } = await load('src/engine/skillXp.js');
const { skillTracks, classSkillId } = await load('src/model/skills.js');
const { awardClassXp } = await load('src/model/classTree.js');
const { awardLevelXp, combatLevelXp } = await load('src/model/levelup.js');
const { combatXpGains } = await load('src/model/rewardprogress.js');
const { grantSmithingReward } = await load('src/model/smithing.js');
const { activeMods } = await load('src/content/customMods.js');
const {
  rollRuneReward, rollCardRewardIds, rollSkillDraftIds, rollClassDraftIds, rollFlaskDrop, rollRelicReward, rollArmamentDrop,
} = await load('src/engine/encounters.js');

const bundle = configuredContentBundle(contentBundle, {});
const registries = createRegistries(bundle);
const classes = bundle.classes.map((c) => c.id);
const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x)));

// ------------------------------------------------------------------ the harness (main.js onCombatEnd)

// ctx = { run, rng, meta, pointsPerLevel }: main.js's module state (`run`, `rng`), saves.loadMeta() and
// resolveLevelUpValue(saves.loadMeta().settings), handed in instead of read from globals.
function victoryTitle(enc) {
  if (enc.pool === 'boss') return `${registries.enemies.get(enc.enemies[0]).name.toUpperCase()} FALLS`;
  return enc.pool === 'elite' ? 'ELITE VANQUISHED' : 'VICTORY';
}
const endlessOn = ({ run }) => !!(run.custom && activeMods(run.custom).endless);
const chaosRewardsOn = ({ run }) => !!(run.custom && activeMods(run.custom).chaosRewards);

function rollDrop(ctx, source) {
  const { run, rng, meta } = ctx;
  return rollArmamentDrop(registries, rng, { source, found: meta.found || [], carried: carriedIds(run.loadout) });
}

function rollSkillDrafts(ctx, pool) {
  const { run, rng } = ctx;
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
  const { run, rng } = ctx;
  const row = run.skills && run.skills[classSkillId(run.class)];
  if (!row || !(row.pendingDrafts > 0)) return [];
  const nodeIds = rollClassDraftIds(registries, rng, { classId: run.class, coreTags: run.coreTags, level: row.level });
  return nodeIds.length ? [{ classId: run.class, level: row.level, nodeIds }] : [];
}

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

function onCombatEnd(ctx, result, combat, enc) {
  const { run, rng } = ctx;
  if (run.journey || run.legacyDungeon) throw new Error('oracle-rewards: journeys and legacy dungeons are not recorded');
  run.flasks = combat.player.flasks;
  run.flaskCharges = combat.player.flaskCharges ? { ...combat.player.flaskCharges } : run.flaskCharges;
  for (const field of ['hp', 'mana', 'stamina']) {
    run[field] = combat.player[field];
    const maxField = `max${field[0].toUpperCase()}${field.slice(1)}`;
    run[maxField] = combat.player[maxField];
  }
  run.equipmentPoolDeficits = { ...combat.equipmentPoolDeficits };
  const trackReceipt = skillXpReceipt(combat);
  const skillAwards = applySkillXp(registries, run, trackReceipt);
  const classAward = awardClassXp(registries, run, { victory: result === 'victory', pool: enc.pool });
  const levelXp = combatLevelXp(registries, {
    victory: result === 'victory', pool: enc.pool, kills: combat.eventLog.filter((e) => e.type === 'enemyDied').length,
  });
  const levelAward = awardLevelXp(registries, run, levelXp, { pointsPerLevel: ctx.pointsPerLevel });
  const xpGains = combatXpGains({ receipt: trackReceipt, awards: [classAward], levelGained: levelAward.gained });
  stampDeck(registries, run, undefined, { adoptEquipmentBonuses: combat.equipmentChanged });
  const receipt = { outcome: null, trackReceipt, skillAwards, classAward, levelXp, levelAward, xpGains };

  if (result !== 'victory') {
    run.hp = 0;
    receipt.outcome = 'defeat';
    return receipt;
  }

  run.stats.fightsWon += 1;
  run.combatEntered = null;
  const smithingStoneReceipt = grantSmithingReward(
    registries,
    run,
    enc.pool,
    `combat:${run.actNumber}:${run.floor}:${run.mapNodeId || 'unknown'}${run.legacyDungeon ? `:${run.legacyDungeon.current}` : ''}:${enc.pool}`,
  );
  receipt.smithingStoneReceipt = smithingStoneReceipt;

  if (enc.pool === 'boss') {
    run.bossesBeaten = run.bossesBeaten || [];
    for (const id of enc.enemies) if (!run.bossesBeaten.includes(id)) run.bossesBeaten.push(id);
    run.bossGroups = run.bossGroups || {};
    const held = [...new Set(equippedPieces(registries, run.loadout, run.class).flatMap((piece) => piece.itemTypeTags || []))];
    for (const id of enc.enemies) run.bossGroups[id] = [...new Set([...(run.bossGroups[id] || []), ...held])];
    if (!run.legacyDungeon && ((run.journey && run.journey.currentNodeId === run.journey.anchors.final) || (run.actNumber >= 3 && !endlessOn(ctx)))) {
      receipt.outcome = 'victory';
      return receipt;
    }
    const bossArmament = rollDrop(ctx, 'boss');
    const drops = registries.balance.equipment.drops || {};
    const bossDrafts = rollSkillDrafts(ctx, 'boss');
    const bossClassDrafts = rollClassDrafts(ctx);
    const bossRewards = {
      title: victoryTitle(enc),
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
    receipt.outcome = 'reward';
    receipt.rewards = bossRewards;
    return receipt;
  }

  const drafts = rollSkillDrafts(ctx, enc.pool);
  const classDrafts = rollClassDrafts(ctx);
  const rewards = {
    title: victoryTitle(enc),
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
  receipt.outcome = 'reward';
  receipt.rewards = rewards;
  return receipt;
}

// ------------------------------------------------------------------ runs and fights

// main.js newRun's additions to a created run (the fields a real run carries into its first fight).
function newRun(seed, classId) {
  const run = createRunState({ seed, classId, registries });
  run.customization = { name: 'Forsaken', glyph: '⚔', tint: 'gold' };
  run.custom = { ascension: 0, mods: {}, deckMode: 'standard' };
  run.stats = { fightsWon: 0, damageDealt: 0, damageTaken: 0 };
  run.path = [];
  run.seenEvents = [];
  run.lastEncounters = [];
  return run;
}

// main.js enterCombat's createCombat arguments for an ordinary run (the run oracle's combatArgs), with an optional
// extra enemy-HP ratio so elite and boss doors can be won by the bot.
function combatArgs(run, enc, hpRatio = 1) {
  let hpMult = hpRatio;
  if (Array.isArray(run.seatOrder)) hpMult *= seatTierHpMult(registries, seatAtTier(run.seatOrder, run.actNumber), run.actNumber);
  return {
    ratingsRules: registries.balance.combatRatings || null,
    handRules: resolveHandRules({}, bundle.attributes),
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
    hpMult,
    enemyStatuses: [],
    swapCostRule: resolveSwapCostRule(registries, { settings: {} }),
    playerStatuses: [...runMods(registries, run.loadout, run.class).startStatuses],
  };
}

// The combat oracle's bot: its own LCG (never the engine RNG), cards preferred 80% of the time.
function botRng(seed) { let s = seed >>> 0 || 1; return (n) => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s % n; }; }
function cloneCombat(combat) {
  return restoreCombatSnapshot({ registries, rng: createRng(combat.rng.seed, combat.rng.getCounters()), snapshot: serializeCombatSnapshot(combat) });
}
function legalCards(combat) {
  const out = [];
  const targets = combat.enemies.filter((e) => e.alive !== false).map((e) => e.instanceId ?? e.id);
  for (const card of combat.piles.hand) {
    for (const targetId of [undefined, ...targets]) {
      const cmd = { type: 'playCard', cardInstanceId: card.instanceId, ...(targetId ? { targetId } : {}) };
      try { dispatch(cloneCombat(combat), cmd); out.push(cmd); break; } catch { /* illegal: next target */ }
    }
  }
  return out;
}
function playOut(combat, seed, steps) {
  const pick = botRng(seed);
  for (let s = 0; s < steps && !combat.result; s++) {
    const cards = legalCards(combat);
    const cmd = cards.length && pick(10) < 8 ? cards[pick(cards.length)] : { type: 'endTurn' };
    try { dispatch(combat, cmd); } catch { /* recorded by nobody: the bot only offers legal plays */ }
  }
  return combat.result;
}

const encountersOf = (pool) => bundle.encounters.filter((e) => e.pool === pool);
const tracksOf = (kind) => skillTracks(registries).filter((t) => t.kind === kind).map((t) => t.id);
const heldTrack = (run) => {
  const held = equippedPieces(registries, run.loadout, run.class).flatMap((p) => p.itemTypeTags || []);
  return tracksOf('weapon').concat(tracksOf('focus')).find((id) => held.includes(id)) || tracksOf('weapon')[0];
};
const treeRows = (classId) => registries.classTree.filter((r) => r.classId === classId);
const allArmaments = () => registries.equipment.armaments.map((a) => a.id);

// Run variants, applied before the fight (so the fight sees them) or, for `after`, just before onCombatEnd.
const VARIANTS = [
  { name: 'base' },
  { name: 'level-threshold', before: (run) => { run.level = { xp: 95, level: 1, unspentPoints: 0 }; } },
  { name: 'level-multi', before: (run) => { run.level = { xp: 460, level: 2, unspentPoints: 1 }; }, pointsPerLevel: 3 },
  { name: 'track-upgrade-cross', before: (run) => { run.skills[heldTrack(run)] = { xp: 70, level: 4, pendingDrafts: 0 }; } },
  { name: 'track-upgrade-standing', before: (run) => { run.skills[heldTrack(run)] = { xp: 3, level: 6, pendingDrafts: 1 }; } },
  { name: 'class-threshold', before: (run) => { run.skills[classSkillId(run.class)] = { xp: 55, level: 0, pendingDrafts: 0 }; } },
  {
    name: 'class-tier2-picked',
    before: (run) => {
      const rows = treeRows(run.class);
      run.coreTags = rows.filter((r) => r.tier === 1).slice(0, 1).map((r) => r.nodeId);
      run.skills[classSkillId(run.class)] = { xp: 20, level: 3, pendingDrafts: 2 };
    },
  },
  { name: 'relics-rune-feral', before: (run) => { run.relics = [...run.relics, 'cinderPouch', 'feralEye', 'saltedRelic']; } },
  { name: 'smithing-claimed', after: (run, enc) => { run.smithingRewardClaims = [...run.smithingRewardClaims, `combat:${run.actNumber}:${run.floor}:${run.mapNodeId || 'unknown'}:${enc.pool}`]; } },
  { name: 'chaos-rewards', before: (run) => { run.custom.mods.chaosRewards = true; } },
  { name: 'endless-act3', act: 3, before: (run) => { run.custom.mods.endless = true; } },
  { name: 'act3', act: 3 },
  { name: 'found-everything', found: () => allArmaments() },
  { name: 'flask-pity-full', before: (run) => { run.flaskChancePct = 100; run.stats.fightsWon = 4; } },
  { name: 'equipment-changed', afterCombat: (combat) => { combat.equipmentChanged = true; } },
  {
    name: 'drafts-queued',
    before: (run) => {
      for (const id of tracksOf('weapon').concat(tracksOf('focus'), tracksOf('dual'))) run.skills[id] = { xp: 1, level: 2, pendingDrafts: 2 };
      run.skills[classSkillId(run.class)] = { xp: 0, level: 1, pendingDrafts: 1 };
    },
  },
  { name: 'boss-history', before: (run, enc) => { run.bossesBeaten = [enc.enemies[0]]; run.bossGroups = { [enc.enemies[0]]: ['item:ghost'] }; } },
  { name: 'low-hp', before: (run) => { run.hp = 2; run.equipmentPoolDeficits = { ...run.equipmentPoolDeficits, hp: run.maxHp - 2 }; }, hpRatio: 1 },
];

// Deterministic .meta for a new oracle file (Unity text asset); an existing .meta is never rewritten.
function ensureMeta(file) {
  const meta = `${file}.meta`;
  if (existsSync(meta)) return;
  const guid = createHash('md5').update(`ashen-oracle-rewards:${file.split(/[\\/]/).pop()}`).digest('hex');
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
const index = [];
const skipped = [];
const tally = {};

function record(name, meta, { run, rng, combat, enc, result, found = [], pointsPerLevel = 1 }) {
  const runBefore = plain(run);
  const snapshot = plain(serializeCombatSnapshot(combat));
  const rngBefore = rng.getCounters();
  // Self-check: the recorded snapshot restores to a combat whose post-combat pipeline is byte-identical.
  const replayRun = structuredClone(runBefore);
  const replayRng = createRng(rng.seed, rngBefore);
  const replayCombat = restoreCombatSnapshot({ registries, rng: replayRng, snapshot: structuredClone(snapshot) });
  replayRun.loadout = replayCombat.loadout; // enterCombat's rejoin: the run and the fight share one loadout
  const replayReceipt = onCombatEnd({ run: replayRun, rng: replayRng, meta: { found }, pointsPerLevel }, result, replayCombat, enc);

  const ctx = { run, rng, meta: { found }, pointsPerLevel };
  const receipt = onCombatEnd(ctx, result, combat, enc);
  const out = {
    name, ...meta, encounterId: enc.id, pool: enc.pool, result, seed: rng.seed, meta: { found }, pointsPerLevel,
    rngBefore, run: runBefore, snapshot, enc: plain(enc),
    expected: { run: plain(run), receipt: plain(receipt), rngAfter: rng.getCounters() },
  };
  if (JSON.stringify(plain(replayRun)) !== JSON.stringify(out.expected.run) || JSON.stringify(plain(replayReceipt)) !== JSON.stringify(out.expected.receipt)
    || JSON.stringify(replayRng.getCounters()) !== JSON.stringify(out.expected.rngAfter)) {
    throw new Error(`${name}: the recorded snapshot does not reproduce the post-combat pipeline`);
  }
  const file = `case-${name}.json`;
  writeFileSync(join(OUT, file), `${JSON.stringify(out)}\n`);
  ensureMeta(join(OUT, file));
  const key = `${enc.pool}/${receipt.outcome}`;
  tally[key] = (tally[key] || 0) + 1;
  index.push({ file, name, ...meta, encounterId: enc.id, pool: enc.pool, result, outcome: receipt.outcome });
}

// (1) The golden combats, replayed to their end from the run they started from (combat-XXX.json `create` is
// createRunState's run for that seed and class, main.js enterCombat's args).
const goldens = JSON.parse(readFileSync(join(GOLDEN, 'index.json'), 'utf8')).combats;
for (const g of goldens) {
  const log = JSON.parse(readFileSync(join(GOLDEN, g.file), 'utf8'));
  const run = newRun(log.seed, log.classId);
  const enc = registries.encounters.get(log.encounterId);
  const i = index.length;
  run.actNumber = 1 + (i % 3);
  run.floor = 1 + (i % 13);
  run.mapNodeId = i % 4 === 0 ? null : `${run.actNumber}-${run.floor}-${i % 3}`;
  run.combatEntered = { nodeId: run.mapNodeId || 'start', encounterId: enc.id };
  const rng = createRng(log.seed);
  const combat = createCombat({ ...structuredClone(log.create), registries, rng });
  for (const step of log.steps) { try { dispatch(combat, step.command); } catch { /* recorded as an error step */ } }
  if (combat.result !== log.result) throw new Error(`${g.file}: replay ended ${combat.result}, recorded ${log.result}`);
  run.loadout = combat.loadout;
  record(`golden-${g.file.replace(/\D/g, '')}`, { family: 'golden', classId: log.classId, variant: 'golden' }, { run, rng, combat, enc, result: combat.result });
}

// (2) Generated fights: class × door × variant, spread so every variant meets every door.
const POOLS = ['normal', 'elite', 'boss'];
for (let j = 0; j < GENERATED; j++) {
  const classId = classes[j % classes.length];
  const pool = POOLS[j % POOLS.length];
  const variant = VARIANTS[Math.floor(j / POOLS.length) % VARIANTS.length];
  const seed = sweepSeed(5000 + j);
  const encs = encountersOf(pool);
  const enc = registries.encounters.get(encs[j % encs.length].id);
  const run = newRun(seed, classId);
  run.actNumber = variant.act ?? 1 + (j % 2);
  run.floor = 1 + (j % 13);
  run.mapNodeId = j % 5 === 0 ? null : `${run.actNumber}-${run.floor}-${j % 4}`;
  run.combatEntered = { nodeId: run.mapNodeId || 'start', encounterId: enc.id };
  if (variant.before) variant.before(run, enc);
  const rng = createRng(seed);
  const hpRatio = variant.hpRatio ?? (pool === 'normal' ? 1 : pool === 'elite' ? 0.2 : 0.1);
  const combat = createCombat({ ...combatArgs(run, enc, hpRatio), registries, rng });
  const result = playOut(combat, seed, 160);
  const name = `gen-${String(j).padStart(3, '0')}-${classId}-${pool}-${variant.name}`;
  if (!result) { skipped.push(name); continue; }
  if (variant.after) variant.after(run, enc);
  if (variant.afterCombat) variant.afterCombat(combat);
  record(name, { family: 'generated', classId, variant: variant.name }, {
    run, rng, combat, enc, result, found: variant.found ? variant.found() : [], pointsPerLevel: variant.pointsPerLevel ?? 1,
  });
}

let coverageReport = null;
if (coverage) {
  const { result } = await coverage.post('Profiler.takePreciseCoverage');
  coverageReport = {};
  for (const script of result.filter((s) => s.url.includes('/src/')).sort((a, b) => (a.url < b.url ? -1 : 1))) {
    const names = script.functions.filter((f) => f.functionName && f.ranges[0].count > 0).map((f) => f.functionName);
    if (names.length) coverageReport[script.url.split('/src/')[1]] = [...new Set(names)];
  }
  for (const [file, names] of Object.entries(coverageReport)) console.log(`${file}: ${names.join(', ')}`);
}

// The registry tables the pipeline reads, in the SHIPPED insertion order (D-040): the run dump's tables plus the
// item-type/card-domain tree (`nodes`), which skills.js derives the tracks and draft schools from.
const TABLES = ['attributes', 'creationModes', 'cards', 'relics', 'statuses', 'stances', 'keywords', 'enemies', 'encounters', 'flasks', 'classes', 'seats', 'propertyRules'];
const dump = Object.fromEntries(TABLES.map((t) => [t, plain(registries[t].all())]));
for (const t of ['classTree', 'nodes', 'equipment', 'balance', 'attributeRules', 'characterCreation', 'derivedStatRules', 'tagFamilies', 'contentVersion']) dump[t] = plain(registries[t]);
writeFileSync(join(OUT, 'registries.json'), `${JSON.stringify(dump)}\n`);
ensureMeta(join(OUT, 'registries.json'));
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify({
  source: 'main.js onCombatEnd harness over engine/skillXp.js, model/{skills,classTree,levelup,rewardprogress,smithing,loadout}.js, engine/encounters.js (shipped preset)',
  cases: index, skipped, tally,
}, null, 1)}\n`);
ensureMeta(join(OUT, 'index.json'));
console.log(`oracle-rewards: ${index.length} cases (${skipped.length} generated fights without a result skipped), self-check ok → Unity/Assets/Tests/Oracle/rewards`);
console.log(JSON.stringify(tally));
