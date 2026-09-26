#!/usr/bin/env node
// Reward-claim parity oracle (US-11.1 to US-11.3). Records what the SHIPPED reward door does to a run once a fight has
// paid out, as the reference for the run loop's C# RewardDoor (Unity/Assets/Game/Domain/Loop/RewardDoor.cs), whose own
// oracle (Tools/oracle-loop.mjs) covers Continue but not the tap path. It records the menu rows and claim status
// (model/rewardplan.js), each claim (tap or chooser Confirm), Skip, a reload (the door re-mounted from the checkpoint),
// Continue under both rewardCollect modes and the door closing. reward.js is
// a browser screen, so the harness below re-implements its run-writing handlers FAITHFULLY and calls the real shipped
// model functions (rewardPlan, rewardClaimStatus, resolveContinue, flaskSlotCap, pickClassNode, spendSkillDraft,
// skillLevel, skillUpgradesCards, classSkillId, syncFlaskGrowth, addToStorage, carriedIds, createRng). It mirrors:
//   src/ui/screens/reward.js  95-105  the plan facts (flaskSlotsFree, armamentSlotsFree)
//   src/ui/screens/reward.js 112-133  the door's state (states + the Smithing Stone merge, chosen picks) and persistProgress
//   src/ui/screens/reward.js 159-203  apply: one function per kind
//   src/ui/screens/reward.js 205-246  take (the rollback branch is not reached: persistence never fails here)
//   src/ui/screens/reward.js 347-351  collectMode (meta.settings.rewardCollect over balance.ui.rewardCollect)
//   src/ui/screens/reward.js 371-378  grantCinders (the door's mount)
//   src/ui/screens/reward.js 460-465  a row's Skip
//   src/ui/screens/reward.js 471-485  finish (Continue): resolveContinue with the 'cardRewards' pick, apply, persist
//   src/ui/screens/reward.js 717-731  the chooser's Confirm: take({ ...row, cardId | nodeId: selected })
//   src/main.js 2456-2477            mountPendingReward onDone: delete run.pendingReward, then `after`
//   src/main.js 1636-1668            collectArmament + recordCollectedArmament (meta.found; the discovery receipt
//                                     recordArmamentDiscovery writes is profile-only and not recorded)
// Left out, because they write no run field: sfx, the 'new' markers and meta.seen (recordSeen), tooltips, focus, the
// inspect door, and persist()'s saveRun stamps (streamCounters, savedAt), which belong to the save layer.
//
// Inputs are the rewards oracle's recorded pending-reward checkpoints (Unity/Assets/Tests/Oracle/rewards, outcome
// 'reward'): each case's run after onCombatEnd, its RNG counters after the fight and its profile found set. Every case
// runs several scripts (claim each row, auto sweep, manual sweep, partial then reload then auto, blocked belt/bag,
// a spent draft, a duplicate armament); a script that mutates the run first records that input run.
//
//   node Tools/oracle-claims.mjs [--source D:/repos/AshenSpire]
//
// Writes Unity/Assets/Tests/Oracle/claims/*.json (committed); RewardClaimsParityTests replays them through RewardDoor.
// Only .json files are deleted on regeneration.
import { writeFileSync, mkdirSync, rmSync, readdirSync, readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const SOURCE = arg('--source', process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'claims');
const REWARDS = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'rewards');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng } = await load('src/engine/rng.js');
const { rewardPlan, rewardClaimStatus, resolveContinue } = await load('src/model/rewardplan.js');
const { flaskSlotCap } = await load('src/model/gracerefill.js');
const { syncFlaskGrowth } = await load('src/model/flaskgrowth.js');
const { skillLevel, skillUpgradesCards, spendSkillDraft, classSkillId } = await load('src/model/skills.js');
const { pickClassNode } = await load('src/model/classTree.js');
const { addToStorage, carriedIds } = await load('src/model/loadout.js');

const bundle = configuredContentBundle(contentBundle, {});
const registries = createRegistries(bundle);
const plain = (v) => JSON.parse(JSON.stringify(v));

// ------------------------------------------------------------------ the harness (reward.js mountRewards)

// ctx = { run, rng, meta }: main.js's `run` and `rng`, and saves.loadMeta() (found set and settings).
function collectArmament(ctx, id) { // main.js 1636-1648
  const { run } = ctx;
  if (!id) return false;
  const stored = addToStorage(run.loadout, id, registries.balance.equipment.storageSlots || 8);
  if (!stored) return false;
  recordCollectedArmament(ctx, id);
  return true;
}
function recordCollectedArmament(ctx, id) { // main.js 1651-1668 (the meta.found half)
  const { run, meta } = ctx;
  if (!carriedIds(run.loadout).includes(id)) return;
  if ((registries.balance.equipment.drops || {}).permanentOnFind) {
    if (!(meta.found || []).includes(id)) meta.found = [...(meta.found || []), id];
  }
  return true;
}

function mount(ctx) {
  const { run } = ctx;
  const checkpoint = run.pendingReward;
  const rewards = checkpoint.rewards;
  const plan = rewardPlan(rewards, { // 95-105
    flaskSlotsFree: Math.max(0, flaskSlotCap(registries.balance) - run.flasks.length),
    armamentSlotsFree: Math.max(0, (registries.balance.equipment.storageSlots || 8) - (((run.loadout || {}).storage) || []).length),
  });
  const states = { // 112-115
    ...(checkpoint?.states || {}),
    ...(rewards.smithingStoneReceipt?.amount > 0 ? { smithingStone: 'taken' } : {}),
  };
  let chosenCardId = checkpoint?.chosenCardId || null;
  const chosenDraftCardIds = { ...(checkpoint?.chosenDraftCardIds || {}) };
  const chosenDraftNodeIds = { ...(checkpoint?.chosenDraftNodeIds || {}) };
  function persistProgress() { // 125-133 (onPersist: persist → saveRun, whose stamps belong to the save layer)
    if (checkpoint) {
      checkpoint.states = { ...states };
      checkpoint.chosenCardId = chosenCardId;
      checkpoint.chosenDraftCardIds = { ...chosenDraftCardIds };
      checkpoint.chosenDraftNodeIds = { ...chosenDraftNodeIds };
    }
  }
  const apply = { // 159-203
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
    flask(row) { run.flasks.push({ flaskId: row.flaskId }); return true; },
    relic(row) { run.relics.push(row.relicId); syncFlaskGrowth(registries, run); return true; },
    armament(row) { return collectArmament(ctx, row.armamentId) !== false; },
  };
  function take(row) { // 205-246
    if (states[row.key]) return false;
    if (!apply[row.kind](row)) return false;
    states[row.key] = 'taken';
    persistProgress();
    return true;
  }
  function grantCinders() { // 371-378
    const row = plan.rows.find((r) => r.kind === 'cinders');
    if (!row || states.cinders || row.blockedBy) return false;
    if (apply.cinders(row)) {
      states.cinders = 'taken';
      persistProgress();
      return true;
    }
    return false;
  }
  function collectMode() { // 347-351
    const settings = ctx.meta.settings || {};
    const dial = registries.balance.ui.rewardCollect || { def: 'auto', modes: ['auto', 'manual'] };
    return dial.modes.includes(settings.rewardCollect) ? settings.rewardCollect : dial.def;
  }
  const door = {
    plan,
    status: () => rewardClaimStatus(plan, states),
    states,
    open: () => grantCinders(), // the mount's grantCinders()
    // A row press (cinders, flask, armament, relic) or the chooser's Confirm (717-731) with the selected id.
    take(key, pick) {
      const row = plan.rows.find((r) => r.key === key);
      const pickField = row.kind === 'classDraft' ? 'nodeId' : 'cardId';
      return take(pick == null ? row : { ...row, [pickField]: pick });
    },
    skip(key) { states[key] = 'skipped'; persistProgress(); return true; }, // 460-465
    finish() { // 471-485
      const rng = ctx.rng;
      const picks = {};
      const pickFn = rng ? (n) => rng.int('cardRewards', 0, n - 1) : () => 0;
      const { take: toTake } = resolveContinue(plan, states, collectMode(), pickFn);
      const taken = [];
      for (const row of toTake) {
        if (apply[row.kind](row)) {
          states[row.key] = 'taken';
          persistProgress();
          taken.push(row.key);
          if (row.cardId || row.nodeId) picks[row.key] = row.nodeId || row.cardId;
        }
      }
      // onDone (main.js 2468-2476): the checkpoint leaves the run; advanceAct / showMap belong to the run loop.
      const after = checkpoint.after;
      delete run.pendingReward;
      return { mode: collectMode(), taken, picks, after };
    },
  };
  return door;
}

// ------------------------------------------------------------------ scripts

const choiceKinds = new Set(['card', 'skillDraft', 'classDraft']);
const pickOf = (row, salt) => {
  const ids = Array.isArray(row.nodeIds) ? row.nodeIds : row.cardIds;
  return ids[salt % ids.length];
};

// Each script: { name, setup?(run, meta), mode, steps(door, salt) → [step] } run through `play`.
const SCRIPTS = {
  // Open the door, press every row in order (a choice with a pick, a blocked row skipped, a claimed row refused),
  // reload, then Continue under manual (nothing left to take).
  eachRow: { mode: 'manual', plan: (rows, salt) => [{ op: 'open' }, ...rows.map((r, i) => (r.blockedBy ? { op: 'skip', key: r.key }
    : choiceKinds.has(r.kind) ? { op: 'take', key: r.key, pick: pickOf(r, salt + i) } : { op: 'take', key: r.key })), { op: 'open' }, { op: 'continue' }] },
  // Continue at once under auto: every pending row is taken, choice rows picked on 'cardRewards'.
  sweepAuto: { mode: 'auto', plan: () => [{ op: 'open' }, { op: 'continue' }] },
  // Continue at once under manual: everything but the cinders is left.
  sweepManual: { mode: 'manual', plan: () => [{ op: 'open' }, { op: 'continue' }] },
  // Take the first choice row, skip the first other pending row, reload, try the choice again (refused), auto sweep.
  partialAuto: {
    mode: 'auto',
    plan: (rows, salt) => {
      const steps = [{ op: 'open' }];
      const choice = rows.find((r) => choiceKinds.has(r.kind) && !r.blockedBy);
      const other = rows.find((r) => !choiceKinds.has(r.kind) && r.kind !== 'cinders' && r.kind !== 'smithingStone' && !r.blockedBy);
      if (choice) steps.push({ op: 'take', key: choice.key, pick: pickOf(choice, salt) });
      if (other) steps.push({ op: 'skip', key: other.key });
      steps.push({ op: 'open' });
      if (choice) steps.push({ op: 'take', key: choice.key, pick: pickOf(choice, salt + 1) });
      steps.push({ op: 'take', key: 'cinders' });
      steps.push({ op: 'continue' });
      return steps;
    },
  },
  // A full belt and a full bag: the flask and armament rows are blocked, skipped, and left by the auto sweep.
  blocked: {
    mode: 'auto',
    setup: (run) => {
      const cap = flaskSlotCap(registries.balance);
      const filler = registries.flasks.all().find((f) => f.id !== run.pendingReward.rewards.flaskId) || registries.flasks.all()[0];
      while (run.flasks.length < cap) run.flasks.push({ flaskId: filler.id });
      const bag = registries.balance.equipment.storageSlots || 8;
      run.loadout.storage = run.loadout.storage || [];
      for (const a of registries.equipment.armaments) {
        if (run.loadout.storage.length >= bag) break;
        if (a.id !== run.pendingReward.rewards.armamentId && !run.loadout.storage.includes(a.id)) run.loadout.storage.push(a.id);
      }
    },
    plan: (rows) => [{ op: 'open' }, ...rows.filter((r) => r.blockedBy).map((r) => ({ op: 'skip', key: r.key })), { op: 'continue' }],
  },
  // The drafts' ledger no longer holds a queued draft: the chooser's Confirm lands nothing (reward.skillDraft.spent).
  spentDraft: {
    mode: 'auto',
    setup: (run) => { for (const row of Object.values(run.skills || {})) if (row) row.pendingDrafts = 0; },
    plan: (rows, salt) => [{ op: 'open' }, ...rows.filter((r) => r.kind === 'skillDraft' || r.kind === 'classDraft').map((r) => ({ op: 'take', key: r.key, pick: pickOf(r, salt) })), { op: 'continue' }],
  },
  // The bag already holds the armament: addToStorage refuses the duplicate, so the take lands nothing and meta stays clean.
  duplicateArmament: {
    mode: 'auto',
    setup: (run) => { run.loadout.storage = [...(run.loadout.storage || []), run.pendingReward.rewards.armamentId]; },
    plan: () => [{ op: 'open' }, { op: 'take', key: 'armament' }, { op: 'continue' }],
  },
};

function scriptsFor(i, rows) {
  const has = (kind) => rows.some((r) => r.kind === kind);
  const names = ['eachRow', 'sweepAuto'];
  if (i % 3 === 0) names.push('sweepManual');
  if (i % 2 === 1) names.push('partialAuto');
  if ((has('flask') || has('armament')) && i % 2 === 0) names.push('blocked');
  if (has('skillDraft') || has('classDraft')) names.push('spentDraft');
  if (has('armament') && i % 2 === 1) names.push('duplicateArmament');
  return names;
}

function play(source, name, salt) {
  const script = SCRIPTS[name];
  const run = structuredClone(source.expected.run);
  const meta = { found: [...(source.meta.found || [])], settings: { rewardCollect: script.mode } };
  if (script.setup) script.setup(run, meta);
  const inputRun = script.setup ? plain(run) : undefined;
  const foundBefore = [...meta.found];
  const rng = createRng(source.seed, source.expected.rngAfter);
  const ctx = { run, rng, meta };
  let door = mount(ctx);
  const steps = [];
  for (const step of script.plan(door.plan.rows, salt)) {
    const record = { ...step };
    switch (step.op) {
      case 'open':
        door = mount(ctx); // a reload re-mounts from the checkpoint; the first open is the door's mount
        record.landed = door.open();
        record.plan = plain(door.plan.rows);
        break;
      case 'take':
        record.ok = door.take(step.key, step.pick);
        break;
      case 'skip':
        record.ok = door.skip(step.key);
        break;
      case 'continue':
        Object.assign(record, door.finish());
        break;
      default:
        throw new Error(`unknown op ${step.op}`);
    }
    if (step.op !== 'continue') record.status = plain(door.status());
    record.found = [...meta.found];
    steps.push(record);
  }
  return {
    name, mode: script.mode, ...(inputRun ? { inputRun } : {}), found: foundBefore, steps,
    expected: { run: plain(run), found: [...meta.found], rngAfter: rng.getCounters() },
  };
}

function ensureMeta(file) {
  const meta = `${file}.meta`;
  if (existsSync(meta)) return;
  const guid = createHash('md5').update(`ashen-oracle-claims:${file.split(/[\\/]/).pop()}`).digest('hex');
  writeFileSync(meta, `fileFormatVersion: 2\nguid: ${guid}\n`);
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const index = [];
const tally = {};
const cases = JSON.parse(readFileSync(join(REWARDS, 'index.json'), 'utf8')).cases.filter((c) => c.outcome === 'reward');
cases.forEach((c, i) => {
  const source = JSON.parse(readFileSync(join(REWARDS, c.file), 'utf8'));
  const rows = mount({ run: structuredClone(source.expected.run), rng: null, meta: { found: [] } }).plan.rows;
  const scripts = scriptsFor(i, rows).map((name) => play(source, name, i));
  // Self-check: every script replays byte-identically.
  for (const s of scripts) {
    const again = play(source, s.name, i);
    if (JSON.stringify(again) !== JSON.stringify(s)) throw new Error(`${c.file} ${s.name}: the script does not replay identically`);
  }
  for (const s of scripts) {
    tally[s.name] = (tally[s.name] || 0) + 1;
    for (const step of s.steps) {
      if (step.op === 'take') tally[step.ok ? `take:${rows.find((r) => r.key === step.key)?.kind}` : 'take:refused'] = (tally[step.ok ? `take:${rows.find((r) => r.key === step.key)?.kind}` : 'take:refused'] || 0) + 1;
      if (step.op === 'continue') for (const key of step.taken) tally[`swept:${key.split(':')[0]}`] = (tally[`swept:${key.split(':')[0]}`] || 0) + 1;
    }
  }
  const file = c.file.replace(/^case-/, 'claims-');
  writeFileSync(join(OUT, file), `${JSON.stringify({ source: c.file, seed: source.seed, scripts })}\n`);
  ensureMeta(join(OUT, file));
  index.push({ file, source: c.file, scripts: scripts.map((s) => s.name) });
});
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify({
  source: 'reward.js mountRewards handler harness over model/rewardplan.js, model/{skills,classTree,flaskgrowth,gracerefill,loadout}.js and main.js onDone/collectArmament (shipped preset)',
  cases: index, tally,
}, null, 1)}\n`);
ensureMeta(join(OUT, 'index.json'));
console.log(`oracle-claims: ${index.length} cases, ${index.reduce((n, c) => n + c.scripts.length, 0)} scripts, self-check ok → Unity/Assets/Tests/Oracle/claims`);
console.log(JSON.stringify(tally));
