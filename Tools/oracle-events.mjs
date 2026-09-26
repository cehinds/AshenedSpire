#!/usr/bin/env node
// Events parity oracle (US-10.1–10.3, PF-04/05). Records what the SHIPPED event door does to a run: main.js showEvent,
// the event and dialogue screens' view facts (ui/screens/event.js mountEvent, ui/screens/dialogue.js mountDialogue:
// the visible choices through model/quests.js availableEventChoices over content/events.js eventChoicesWithHistory,
// engine/quests.js choiceAffordable, model/consequence.js bindingReasons, the chain's speaker and its portrait), the
// commit through engine/quests.js commitEventChoice (engine/actions.js executeRunEffects with every run opcode the
// events use — cinders, cards in and out, card upgrades through model/smithing.js, relics, max HP, fights, the class
// swap of model/classSwap.js — then the history row and the quest completion) and main.js showEvent's onDone (a fight
// the choice started is taken off the run and handed on). The screens are browser modules, so the harness below
// re-implements their run-facing lines FAITHFULLY, calling the real shipped functions. It mirrors, line for line:
//   src/main.js 2615-2633                 showEvent / onDone (run.combatEntered read, cleared, handed to enterCombat)
//   src/ui/screens/event.js 30-118        mountEvent's visible choices, prices and binding facts, the commit
//   src/ui/screens/dialogue.js 55-80      the chain step's speaker, portrait and responses
//   src/ui/models/ChoiceBodyModel.js      eventResponseStatus (the head's status)
// A refused commit (a choice the event lacks, one the history has not opened, one the purse cannot pay) is recorded
// by its shipped message; the C# port names it by a key of strings/events.en.json.
//
// Sessions: (a) a sweep — every event × every choice on both presets, once opened by the history and the purse, once
// with neither — and (b) generated walks: class × run variant × preset, each visiting a run of events (quest chains
// included) and taking legal and illegal choices, the history growing as it goes. Each visit records the view, the
// result, the run fields that changed, the RNG counters and the onDone step.
//
//   node Tools/oracle-events.mjs [--source D:/repos/AshenSpire] [--coverage]
//
// Writes Unity/Assets/Tests/Oracle/events/*.json (committed). Only .json files are deleted on regeneration.
import { writeFileSync, mkdirSync, rmSync, readdirSync, readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const SOURCE = arg('--source', process.env.ASHEN_SOURCE || 'D:/repos/AshenSpire');
const OUT = join(ROOT, 'Unity', 'Assets', 'Tests', 'Oracle', 'events');
const load = (rel) => import(pathToFileURL(join(SOURCE, rel)).href);

const { contentBundle } = await load('src/content/index.js');
const { configuredContentBundle } = await load('src/model/advancedConfig.js');
const { createRegistries } = await load('src/model/registries.js');
const { createRng, sweepSeed } = await load('src/engine/rng.js');
const { createRunState } = await load('src/model/state.js');
const { commitEventChoice, choiceAffordable } = await load('src/engine/quests.js');
const { availableEventChoices, questChainForEvent } = await load('src/model/quests.js');
const { eventChoicesWithHistory } = await load('src/content/events.js');
const { bindingReasons } = await load('src/model/consequence.js');
const { eventResponseStatus } = await load('src/ui/models/ChoiceBodyModel.js');
const { smithingPlan, commitSmithing } = await load('src/model/smithing.js');
const { addToStorage } = await load('src/model/loadout.js');
const { classSkillId } = await load('src/model/skills.js');
const { executeRunEffects } = await load('src/engine/actions.js');

const REFERENCE_SETTINGS = JSON.parse(readFileSync(join(ROOT, 'Tools', 'export', 'raw', 'reference.settings.json'), 'utf8'));
const PRESETS = {
  shipped: createRegistries(configuredContentBundle(contentBundle, {})),
  reference: createRegistries(configuredContentBundle(contentBundle, REFERENCE_SETTINGS)),
};
let registries = PRESETS.shipped;
const classes = contentBundle.classes.map((c) => c.id);
const plain = (v) => JSON.parse(JSON.stringify(v, (k, x) => (typeof x === 'function' ? undefined : x)));

// ------------------------------------------------------------------ the harness

// The shipped refusals of commitEventChoice, by message, as the C# port's keys (strings/events.en.json).
function refusalFor(message, eventId, choiceId) {
  if (message === `commitEventChoice: event '${eventId}' has no choice '${choiceId}'`) return { key: 'events.refusal.unknownChoice', args: [] };
  if (message === `commitEventChoice: '${eventId}/${choiceId}' is not open to this run's history`) return { key: 'events.refusal.history', args: [] };
  if (message === `commitEventChoice: '${eventId}/${choiceId}' costs more than the run holds`) return { key: 'events.refusal.cinders', args: [] };
  throw new Error(`oracle-events: unrecorded refusal "${message}"`);
}

/** What the event (or, for a chain step, the dialogue) screen shows before a response is taken. */
function openEvent(run, eventId) {
  const def = registries.events.get(eventId);
  const questId = questChainForEvent(registries.questChains, eventId);
  const all = eventChoicesWithHistory(def);
  const visible = availableEventChoices(all, run);
  const choices = visible.map(({ choice, index }) => ({
    index,
    choiceId: choice.id,
    label: choice.label,
    resultText: choice.resultText,
    affordable: choiceAffordable(choice, run),
    priced: !!choice.requires,
    binding: bindingReasons(choice, registries).length > 0,
    bindingReasons: bindingReasons(choice, registries),
    refusal: choiceAffordable(choice, run) ? null : 'events.refusal.cinders',
  }));
  const hidden = all.map((choice, index) => ({ index, choiceId: choice.id, refusal: 'events.refusal.history' })).filter((row) => !visible.some((v) => v.index === row.index));
  let speaker = null;
  if (questId) {
    const s = registries.speakers.get(registries.eventSpeakers[eventId]);
    speaker = { id: s.id, name: s.name, portraitKey: s.portraitKey, portraitAvailable: !!s.portraitKey && registries.enemies.has(s.portraitKey) };
  }
  const status = eventResponseStatus(choices.map((c) => ({ index: c.index, affordable: c.affordable, priced: c.priced, binding: c.binding })));
  return plain({
    eventId, questId, title: def.name, text: def.text, art: def.art, speaker, choices, hidden,
    status: { total: status.total, available: status.available, blocked: status.blocked, binding: status.binding, phase: status.phase },
  });
}

/** The commit (engine/quests.js commitEventChoice) and what the screen learns from it. */
function choose(run, rng, eventId, choiceId) {
  try {
    const { choice, receipt, completions, events } = commitEventChoice({ run, registries, rng }, { eventId, choiceId });
    const fight = run.combatEntered ? (typeof run.combatEntered === 'string' ? run.combatEntered : run.combatEntered.encounterId) : null;
    return { ok: true, outcome: plain({ choiceId: choice.id, resultText: choice.resultText, receipt, completions, events, fight }) };
  } catch (e) {
    return { ok: false, refusal: refusalFor(e.message, eventId, choiceId) };
  }
}

/** main.js showEvent onDone: a fight the choice started leaves the run and goes to enterCombat. */
function finish(run) {
  if (run.combatEntered) {
    const encounterId = typeof run.combatEntered === 'string' ? run.combatEntered : run.combatEntered.encounterId;
    run.combatEntered = null;
    return { fight: encounterId };
  }
  return { fight: null };
}

// ------------------------------------------------------------------ runs and variants

function newRun(seed, classId) {
  const run = createRunState({ seed, classId, registries });
  run.customization = { name: 'Forsaken', glyph: '⚔', tint: 'gold' };
  run.custom = { ascension: 0, mods: {}, deckMode: 'standard' };
  run.stats = { fightsWon: 0, damageDealt: 0, damageTaken: 0 };
  run.path = [];
  run.seenEvents = [];
  run.lastEncounters = [];
  run.actNumber = 1;
  run.floor = 4;
  run.mapNodeId = 'n4_1';
  return run;
}

function lcg(seed) { let s = seed >>> 0 || 1; return (n) => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return n > 0 ? s % n : 0; }; }
const eventIds = () => registries.events.ids();
const historyRow = (run, eventId, choiceId) => ({ kind: 'eventChoice', eventId, choiceId, actNumber: run.actNumber, floor: run.floor, mapNodeId: run.mapNodeId ?? null });
const rewardRelicIds = () => registries.relics.ids().filter((id) => (registries.relics.get(id).pool || 'reward') === 'reward');

const VARIANTS = [
  { name: 'base', setup: () => {} },
  { name: 'rich', setup: (run) => { run.cinders = 500; } },
  { name: 'modest', setup: (run) => { run.cinders = 45; } },
  { name: 'low-hp', setup: (run) => { run.hp = 4; run.cinders = 70; } },
  { name: 'upgraded-deck', setup: (run) => { run.cinders = 120; for (const c of run.deck) if (!c.sourceArmamentId && !c.grantedBy && registries.cards.get(c.cardId).upgrade) c.upgraded = true; } },
  {
    name: 'smithed-out',
    setup: (run) => {
      run.cinders = 60;
      for (let guard = 0; guard < 40; guard++) {
        const plan = smithingPlan(registries, run);
        const c = plan.candidates.find((x) => x.itemKind === 'armament' && x.affectedCards.length);
        if (!c) break;
        commitSmithing(registries, run, c.itemRef, undefined, { free: true });
      }
    },
  },
  { name: 'relics-all', setup: (run) => { for (const id of rewardRelicIds()) if (!run.relics.includes(id)) run.relics.push(id); } },
  { name: 'relics-most', setup: (run, pick) => { const pool = rewardRelicIds(); for (let i = 0; i < pool.length - 2; i++) { const id = pool[pick(pool.length)]; if (!run.relics.includes(id)) run.relics.push(id); } } },
  { name: 'lantern-oil', setup: (run) => { run.cinders = 100; run.history.push(historyRow(run, 'lastLantern', 'buyOil')); } },
  { name: 'lantern-beacon', setup: (run) => { run.history.push(historyRow(run, 'lastLantern', 'haulBeacon')); } },
  { name: 'grave-dug', setup: (run) => { run.cinders = 200; run.history.push(historyRow(run, 'graveOfTheNameless', 'digForCinders')); } },
  { name: 'grave-mourned', setup: (run) => { run.history.push(historyRow(run, 'graveOfTheNameless', 'payRespects')); run.history.push(historyRow(run, 'namelessKeeper', 'acceptThanks')); } },
  { name: 'keeper-fought', setup: (run) => { run.history.push(historyRow(run, 'graveOfTheNameless', 'digForCinders')); run.history.push(historyRow(run, 'namelessKeeper', 'faceKeeper')); } },
  { name: 'quest-done', setup: (run) => { run.history.push(historyRow(run, 'lastLantern', 'buyOil')); run.history.push({ kind: 'questCompleted', questId: 'lastLantern', source: 'event' }); run.cinders = 300; } },
  { name: 'cart-looted', setup: (run) => { run.cinders = 80; run.history.push(historyRow(run, 'abandonedCart', 'lootStrongbox')); } },
  { name: 'history-malformed', setup: (run) => { run.history.push({ kind: 'eventChoice', eventId: 'lastLantern', choiceId: 'buyOil', actNumber: 0, floor: 1, mapNodeId: null }); } },
  { name: 'swap-ready', setup: (run) => { swapReady(run); } },
  { name: 'carried', setup: (run, pick) => { run.cinders = 150; const ids = registries.equipment.armaments.map((a) => a.id); for (let i = 0; i < 2; i++) addToStorage(run.loadout, ids[pick(ids.length)], 8); } },
  { name: 'act3', setup: (run) => { run.actNumber = 3; run.floor = 9; run.mapNodeId = null; run.cinders = 90; } },
];

/** A class track, picked talents and an armour set only this class wears (the class swap's work). */
function swapReady(run) {
  const rows = registries.classTree.filter((r) => r.classId === run.class);
  run.coreTags = rows.slice(0, 2).map((r) => r.nodeId);
  run.skills[classSkillId(run.class)] = { xp: 12, level: 3, pendingDrafts: 1 };
  const own = registries.equipment.armour.find((o) => o.classId === run.class && !registries.equipment.armour.some((x) => x.classId !== run.class && x.id === o.id));
  if (own && run.loadout && Array.isArray(run.loadout.sets.armor)) {
    run.loadout.sets.armor = [own.id, ...run.loadout.sets.armor.slice(1)];
  }
}

function ensureMeta(file) {
  const meta = `${file}.meta`;
  if (existsSync(meta)) return;
  const guid = createHash('md5').update(`ashen-oracle-events:${file.split(/[\\/]/).pop()}`).digest('hex');
  writeFileSync(meta, `fileFormatVersion: 2\nguid: ${guid}\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n`);
}

function runDelta(before, after) {
  const set = {};
  for (const [k, v] of Object.entries(after)) if (!(k in before) || JSON.stringify(before[k]) !== JSON.stringify(v)) set[k] = v;
  const removed = Object.keys(before).filter((k) => !(k in after));
  const keys = Object.keys(after);
  const replayed = [...Object.keys(before).filter((k) => k in after), ...keys.filter((k) => !(k in before))];
  return JSON.stringify(replayed) === JSON.stringify(keys) ? { set, removed } : { set, removed, keys };
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
const tally = { accepted: {}, refused: {}, fights: 0 };
const startable = Object.fromEntries(Object.entries(PRESETS).map(([id, r]) => [id, classes.filter((c) => {
  try { createRunState({ seed: 1, classId: c, registries: r }); return true; } catch { return false; }
})]));

/** One session: a start run, then visits (open → choose → onDone). */
function record(name, meta, run, rng, visits) {
  const runBefore = plain(run);
  const rngBefore = rng.getCounters();
  let prev = plain(run);
  const out = [];
  for (const { eventId, pickChoice } of visits) {
    const view = openEvent(run, eventId);
    const choiceId = pickChoice(view);
    const result = choose(run, rng, eventId, choiceId);
    const after = plain(run);
    const visit = { eventId, view, choiceId, result, runDelta: runDelta(prev, after), rngAfter: rng.getCounters() };
    prev = after;
    const label = `${eventId}/${choiceId}`;
    if (result.ok) {
      tally.accepted[label] = (tally.accepted[label] || 0) + 1;
      const done = finish(run);
      if (done.fight) tally.fights++;
      const afterDone = plain(run);
      visit.finish = { ...done, runDelta: runDelta(prev, afterDone) };
      prev = afterDone;
    } else tally.refused[`${label}:${result.refusal.key}`] = (tally.refused[`${label}:${result.refusal.key}`] || 0) + 1;
    out.push(visit);
  }
  const file = `${name}.json`;
  writeFileSync(join(OUT, file), `${JSON.stringify({ name, ...meta, seed: rng.seed, rngBefore, run: runBefore, visits: out })}\n`);
  ensureMeta(join(OUT, file));
  index.push({ file, name, ...meta, visits: out.length });
}

// (a) The sweep: every event × every choice, opened (history and purse satisfy it) and shut (neither).
let n = 0;
for (const preset of ['shipped', 'reference']) {
  registries = PRESETS[preset];
  for (const eventId of eventIds()) {
    const choices = eventChoicesWithHistory(registries.events.get(eventId));
    choices.forEach((choice, ci) => {
      for (const mode of ['open', 'shut']) {
        const classId = startable[preset][n % startable[preset].length];
        const seed = sweepSeed(9000 + n);
        n++;
        const run = newRun(seed, classId);
        if (mode === 'open') {
          run.cinders = 400;
          swapReady(run);
          const req = choice.requiresHistory;
          if (req && req.all) for (const ref of req.all) run.history.push(historyRow(run, ref.eventId, ref.choiceId));
          if (req && req.any) run.history.push(historyRow(run, req.any[0].eventId, req.any[0].choiceId));
        } else {
          run.cinders = 0;
          const req = choice.requiresHistory;
          if (req && req.none) for (const ref of req.none) run.history.push(historyRow(run, ref.eventId, ref.choiceId));
        }
        const rng = createRng(seed);
        for (let i = 0; i < n % 7; i++) { rng.float('misc'); rng.float('relicRewards'); }
        record(`sweep-${String(n).padStart(3, '0')}-${preset}-${eventId}-${choice.id}-${mode}`, { family: 'sweep', preset, classId, variant: mode }, run, rng,
          [{ eventId, pickChoice: () => choice.id }]);
      }
    });
  }
}

// (b) Generated walks: class × variant × preset, eight visits each; mostly visible choices, sometimes a priced one
// the purse cannot pay, a hidden one, or one the event does not have.
const WALK_VISITS = 8;
let w = 0;
for (const preset of ['shipped', 'reference']) {
  registries = PRESETS[preset];
  for (const variant of VARIANTS) {
    for (let c = 0; c < 2; c++) {
      const classId = startable[preset][(w + c) % startable[preset].length];
      const seed = sweepSeed(11000 + w);
      w++;
      const pick = lcg(seed ^ 0x2545f491);
      const run = newRun(seed, classId);
      variant.setup(run, pick);
      const rng = createRng(seed);
      for (let i = 0; i < w % 5; i++) rng.float('misc');
      const ids = eventIds();
      const visits = [];
      for (let v = 0; v < WALK_VISITS; v++) {
        const eventId = ids[pick(ids.length)];
        visits.push({
          eventId,
          pickChoice: (view) => {
            const r = pick(20);
            if (r === 0) return 'noSuchChoice';
            if (r === 1 && view.hidden.length) return view.hidden[pick(view.hidden.length)].choiceId;
            const blocked = view.choices.filter((ch) => !ch.affordable);
            if (r === 2 && blocked.length) return blocked[pick(blocked.length)].choiceId;
            return view.choices.length ? view.choices[pick(view.choices.length)].choiceId : 'noSuchChoice';
          },
        });
      }
      record(`walk-${String(w).padStart(3, '0')}-${preset}-${classId}-${variant.name}`, { family: 'walk', preset, classId, variant: variant.name }, run, rng, visits);
    }
  }
}

// (c) The run-level effect DSL (engine/actions.js executeRunEffects) over every run opcode and a few combat opcodes
// through the player facade — the door events, keepsakes and shop effects share — including what no event authors.
const EFFECTS = [
  { name: 'flasks', setup: (run) => { run.flasks = []; }, effects: () => [{ op: 'addFlask', random: true }, { op: 'addFlask', id: registries.flasks.ids()[0] }] },
  { name: 'flasks-full', setup: (run) => { const id = registries.flasks.ids()[0]; run.flasks = [{ flaskId: id }, { flaskId: id }, { flaskId: id }]; }, effects: () => [{ op: 'addFlask', random: true }] },
  { name: 'flask-capacity', effects: () => [{ op: 'addFlaskCapacity', kind: 'hp', amount: 1 }, { op: 'addFlaskCapacity', kind: 'mana', amount: 2 }, { op: 'addFlaskCapacity', kind: 'poison', amount: 1 }, { op: 'addFlaskCapacity', kind: 'hp', amount: 0 }] },
  { name: 'remove-named', effects: (run) => [{ op: 'removeCardFromDeck', card: run.deck.find((c) => !c.grantedBy).cardId }, { op: 'removeCardFromDeck', card: 'noSuchCard' }, { op: 'removeCardFromDeck' }] },
  { name: 'upgrade-named', effects: (run) => { const plan = smithingPlan(registries, run); const c = plan.candidates.find((x) => x.affectedCards.length); return [{ op: 'upgradeCard', card: c ? c.affectedCards[0].cardId : 'strike' }, { op: 'upgradeCard' }, { op: 'upgradeCard', card: 'noSuchCard' }]; } },
  { name: 'upgrade-ordinary', setup: (run) => { run.deck.push({ instanceId: 'x1', cardId: registries.cards.all().find((c) => c.upgrade && c.class === run.class).id, upgraded: false }); smithOut(run); }, effects: () => [{ op: 'upgradeCard' }, { op: 'upgradeCard', random: true }] },
  { name: 'swap-named', setup: (run) => { swapReady(run); }, effects: (run) => { const other = registries.classes.ids().find((id) => id !== run.class); return [{ op: 'swapClass', classId: other }, { op: 'swapClass', classId: other }]; } },
  { name: 'cinders-floor', setup: (run) => { run.cinders = 30; }, effects: () => [{ op: 'addCinders', amount: -100 }, { op: 'addCinders', amount: 7 }] },
  { name: 'relic-named', effects: (run) => [{ op: 'addRelic', id: run.relics[0] }, { op: 'addRelic', id: rewardRelicIds().find((id) => !run.relics.includes(id)) }, { op: 'addRelic' }] },
  { name: 'fight-named', effects: () => [{ op: 'startCombat', encounterId: registries.encounters.ids()[0] }, { op: 'startCombat', encounterId: registries.encounters.ids()[1], if: { p: 'random', pct: 90 } }] },
  { name: 'cards-in', effects: () => [{ op: 'addCardToDeck', card: 'guilt' }, { op: 'addCardToDeck', card: 'guilt', repeat: 2 }] },
  { name: 'hp-facade', setup: (run) => { run.hp = Math.max(1, run.hp - 20); }, effects: () => [{ op: 'heal', target: 'self', amount: 5, repeat: 2 }, { op: 'loseHp', amount: 3 }, { op: 'block', amount: 4 }, { op: 'restoreMana', amount: 1 }, { op: 'damage', target: 'self', amount: 2, hits: 2 }] },
  { name: 'hp-lethal', effects: () => [{ op: 'damage', target: 'self', amount: 9999 }, { op: 'heal', target: 'self', amount: 10 }, { op: 'loseMaxHpPct', pct: 50 }] },
  { name: 'max-hp', effects: () => [{ op: 'loseMaxHpPct', pct: 99 }, { op: 'loseMaxHpPct', pct: 99 }, { op: 'heal', target: 'self', amount: { f: 'percentMaxHp', of: 'self', pct: 50 } }] },
  { name: 'unknown-op', effects: () => [{ op: 'addCinders', amount: 5 }, { op: 'noSuchOp' }] },
];
function smithOut(run) {
  for (let guard = 0; guard < 40; guard++) {
    const plan = smithingPlan(registries, run);
    const c = plan.candidates.find((x) => x.itemKind === 'armament' && x.affectedCards.length);
    if (!c) break;
    commitSmithing(registries, run, c.itemRef, undefined, { free: true });
  }
}
let e = 0;
for (const preset of ['shipped', 'reference']) {
  registries = PRESETS[preset];
  for (const spec of EFFECTS) {
    for (let c = 0; c < 2; c++) {
      const classId = startable[preset][(e + c) % startable[preset].length];
      const seed = sweepSeed(13000 + e);
      e++;
      const run = newRun(seed, classId);
      if (spec.setup) spec.setup(run);
      const effects = plain(spec.effects(run));
      const rng = createRng(seed);
      for (let i = 0; i < e % 6; i++) { rng.float('misc'); rng.float('flaskRewards'); }
      const runBefore = plain(run);
      const rngBefore = rng.getCounters();
      let expected;
      try {
        const { events } = executeRunEffects({ run, registries, rng }, structuredClone(effects));
        expected = { events: plain(events), runDelta: runDelta(runBefore, plain(run)), rngAfter: rng.getCounters() };
      } catch (err) {
        expected = { error: err.message };
      }
      const name = 'effects-' + String(e).padStart(3, '0') + '-' + preset + '-' + classId + '-' + spec.name;
      const file = name + '.json';
      writeFileSync(join(OUT, file), JSON.stringify({ name, family: 'effects', preset, classId, variant: spec.name, seed, rngBefore, run: runBefore, effects, expected }) + '\n');
      ensureMeta(join(OUT, file));
      index.push({ file, name, family: 'effects', preset, classId, variant: spec.name, error: expected.error || null });
    }
  }
}

if (coverage) {
  const { result } = await coverage.post('Profiler.takePreciseCoverage');
  const FILES = ['engine/quests.js', 'model/quests.js', 'model/consequence.js', 'model/classSwap.js', 'engine/actions.js', 'content/events.js'];
  const FUNCS = new Set(['choiceAffordable', 'completeQuest', 'commitEventChoice', 'validId', 'historyArray', 'choiceRefProblems', 'eventChoiceHistoryProblems', 'recordEventChoice', 'hasEventChoice',
    'eventChoiceRequirementProblems', 'eventChoiceRequirementMet', 'availableEventChoices', 'validQuestId', 'hasQuestCompletion', 'recordQuestCompletion', 'questChainForEvent', 'questsCompletedBy',
    'isSafeOp', 'bindingReasons', 'isBindingChoice', 'swapRunClass', 'runRunOpcode', 'createRunContext', 'drainRunContext', 'executeRunEffects', 'executeAction', 'runOpcode', 'eventChoicesWithHistory']);
  for (const script of result.filter((s) => FILES.some((f) => s.url.endsWith(`/src/${f}`)))) {
    const src = readFileSync(fileURLToPath(script.url), 'utf8');
    const lineOf = (off) => src.slice(0, off).split('\n').length;
    for (const fn of script.functions.filter((f) => FUNCS.has(f.functionName))) {
      if (fn.ranges[0].count === 0) { console.log(`${script.url.split('/src/')[1]} ${fn.functionName}: NEVER CALLED`); continue; }
      const dead = fn.ranges.slice(1).filter((r) => r.count === 0).map((r) => `L${lineOf(r.startOffset)} ${src.slice(r.startOffset, Math.min(r.endOffset, r.startOffset + 60)).replace(/\s+/g, ' ')}`);
      console.log(`${script.url.split('/src/')[1]} ${fn.functionName}: ${fn.ranges[0].count} calls, ${fn.ranges.length - 1} blocks, ${dead.length} unrun${dead.length ? `\n    ${dead.join('\n    ')}` : ''}`);
    }
  }
}

// The event tables the door reads, per preset, in the SHIPPED order (D-040): the content-built registries must equal them.
const { eventChoiceIds, eventChoiceHistoryRequirements } = await load('src/content/events.js');
const tables = Object.fromEntries(Object.entries(PRESETS).map(([id, r]) => [id, plain({
  events: r.events.all(), speakers: r.speakers.all(), questChains: r.questChains, eventSpeakers: r.eventSpeakers,
  eventHistoryRequirements: r.eventHistoryRequirements, eventChoiceIds, eventChoiceHistoryRequirements,
})]));
writeFileSync(join(OUT, 'registries.json'), `${JSON.stringify(tables)}
`);
ensureMeta(join(OUT, 'registries.json'));
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify({
  source: 'main.js showEvent/onDone + ui/screens/event.js & dialogue.js view facts over engine/quests.js commitEventChoice, model/{quests,consequence,classSwap,smithing}.js, engine/actions.js executeRunEffects (shipped and reference presets)',
  sessions: index, tally,
}, null, 1)}\n`);
ensureMeta(join(OUT, 'index.json'));
console.log(`oracle-events: ${index.length} sessions → Unity/Assets/Tests/Oracle/events`);
console.log(JSON.stringify({ accepted: Object.keys(tally.accepted).length, refused: Object.keys(tally.refused).length, fights: tally.fights }));
