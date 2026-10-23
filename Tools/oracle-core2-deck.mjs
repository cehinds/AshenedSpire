#!/usr/bin/env node
// Core 2 deck editor oracle (WP-18, docs/design/11-CORE2-PORT-PLAN.md). Drives the SHIPPED src/model/deckRules.js for the
// configured `default` profile: the editor's bounds and refusals, the sideboard moves, the begin / cancel / commit draft
// door, the copy limits and equipment eligibility, and the opening draw pile in deck order. The C# port
// (Ashen.Domain.Run.DeckRules) must give the same answers and leave the same run document.
//
//   node Tools/oracle-core2-deck.mjs [--seeds 6] [--steps 90] [--source <web checkout>] [--allow-dirty]
//
// Cases (Unity/Assets/Tests/Oracle/core2/deck/, sorted keys; two runs are byte-identical):
//   settings.json            every settings variant (valid, tight, max below min, malformed, off) with bounds, problems, the
//                            switches and the refusal sentence for every deck size 0..45
//   probe-<class>.json       per class: the basics, the unlimited ids, and for every registry card its equipment eligibility
//                            (range on and off), copy limit and basic classification on the class's opening run
//   edit-<class>-seedNN.json an LCG bot edits a WP-3 style run: sideboard moves (incl. refused ones), rewards that land in
//                            either pile, begin / cancel / commit of drafts (valid and corrupt), settings and range policy
//                            switches; every step records its answer, the editor state and the run document at the end
//   order.json               orderedDrawPile / orderedReturn over shuffled decks (the Innate cards come first)
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, botLcg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'deck');
const SEEDS = Number(arg('--seeds', '6'));
const STEPS = Number(arg('--steps', '90'));
const w = await loadWeb();
const reg = w.registries;
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const dr = await load('src/model/deckRules.js');
const { resolveCard } = await load('src/model/registries.js');
const { pureEngagementMovement } = await load('src/model/rangeCardAvailability.js');

const classes = classIds(w);
const UNLOCKED = { gameProfileUnlocks: { [w.profile.id]: { completedRunIds: classes.map((_, i) => `r${i}`) } } };
const create = (seed, classId) => w.state.createRunState({ seed, classId, registries: reg, profileMeta: structuredClone(UNLOCKED) });

// ---------------------------------------------------------------------------------------------------------------
// Settings variants
// ---------------------------------------------------------------------------------------------------------------
const SETTINGS = [
  {},
  { deckMinSize: 8, deckMaxSize: 12, deckMaxUnlimited: false },
  { deckMinUnlimited: true, deckMaxSize: 11, deckMaxUnlimited: false },
  { deckMinSize: 12, deckMaxSize: 6, deckMaxUnlimited: false },
  { deckMinSize: 'ten' },
  { deckMinSize: 2.5 },
  { deckMaxSize: 0, deckMaxUnlimited: false },
  { deckMinSize: null, deckMaxSize: null },
  { deckEditing: false, deckEditingWhere: 'restOnly' },
  { deckEditingWhere: 'bogus' },
  { playInDeckOrder: true, deckMinSize: 9, deckMaxSize: 9, deckMaxUnlimited: false },
  { deckMinSize: 0, deckMaxUnlimited: true },
];

const call = (fn) => { try { return fn(); } catch (e) { return { error: e.message }; } };

const settingsCases = SETTINGS.map((settings) => ({
  settings,
  bounds: call(() => dr.deckEditBounds(settings)),
  problems: call(() => dr.deckSettingsProblems(settings)),
  editingOn: dr.deckEditingOn(settings),
  editingWhere: dr.deckEditingWhere(settings),
  inDeckOrder: dr.playInDeckOrder(settings),
  refusals: Array.from({ length: 46 }, (_, n) => call(() => dr.deckEditRefusal(n, settings))),
}));

// ---------------------------------------------------------------------------------------------------------------
// Probes
// ---------------------------------------------------------------------------------------------------------------
const allCards = reg.cards.all();
const movers = allCards.filter((c) => pureEngagementMovement(c)).map((c) => c.id);

const LEGACY_SETTINGS = [{}, { classSpellPowerCopies: 2 }, { classSpellPowerCopies: 0 }];
const legacyLimits = (cardId, classId) => LEGACY_SETTINGS.map((settings) => call(() => dr.deckCopyLimit(reg, cardId, settings, classId)));

function probe(classId) {
  const run = create(w.rngMod.sweepSeed(7000 + classes.indexOf(classId)), classId);
  const rangeOff = structuredClone(run);
  rangeOff.rangePolicy = { ...rangeOff.rangePolicy, enabled: false };
  const cards = {};
  for (const def of allCards) {
    const instance = { instanceId: `probe:${def.id}`, cardId: def.id, upgraded: false };
    cards[def.id] = {
      eligible: call(() => dr.deckCardEquipmentEligible(reg, run, instance)),
      eligibleRangeOff: call(() => dr.deckCardEquipmentEligible(reg, rangeOff, instance)),
      limit: call(() => dr.deckCopyLimit(reg, def.id, {}, run)),
      basic: dr.isBasicCard(instance, run),
      unlimitedBasic: dr.isUnlimitedBasic(instance, run),
      // The class-string door (consumables, blacksmith, startingAbilities): the legacy single-copy rule of content/deckRules.js.
      legacy: legacyLimits(def.id, classId),
    };
  }
  // A card lent by a source the run does not wear, and one lent by the worn gear but under another instance id.
  const granted = run.deck.filter((c) => c.grantedBy);
  const lent = [
    ...granted.map((c) => ({ ...c })),
    ...granted.map((c) => ({ ...c, instanceId: `${c.instanceId}:copy` })),
    { instanceId: 'granted:foreign:core.manaSurge:0', cardId: 'core.manaSurge', upgraded: false, equipmentRole: 'granted', grantedBy: 'foreign', grantSource: 'weapon' },
    { instanceId: 'kit:1', cardId: 'core.strike', upgraded: false, equipmentRole: 'attack', equipmentAttackSlotId: 'attack:0' },
  ].map((card) => ({ card, eligible: call(() => dr.deckCardEquipmentEligible(reg, run, card)), basic: dr.isBasicCard(card, run), unlimitedBasic: dr.isUnlimitedBasic(card, run), setAside: dr.isSetAsideBasic(card, run) }));
  const variants = run.deck.slice(0, 3).flatMap((card) => [{ ...card, upgraded: true }, { ...card, rank: 2 }, { ...card, mods: ['m'] }])
    .map((card) => ({ card, basic: dr.isBasicCard(card, run), unlimitedBasic: dr.isUnlimitedBasic(card, run), setAside: dr.isSetAsideBasic(card, run) }));
  return {
    classId, seed: run.seed, legacySettings: LEGACY_SETTINGS, otherClassLegacy: allCards.filter((c) => c.class && c.class !== 'colorless').slice(0, 40).map((c) => ({ id: c.id, limits: legacyLimits(c.id, classId) })), basicCardIds: dr.basicCardIds(run), unlimitedCardIds: dr.unlimitedCardIds(run), isEquippedRun: dr.isEquippedRun(run),
    deck: run.deck.map((c) => `${c.instanceId}:${c.cardId}`), cards, lent, variants,
    owned: Object.fromEntries([...new Set(run.deck.map((c) => c.cardId))].map((id) => [id, dr.ownedCopies(run, id)])),
  };
}

// ---------------------------------------------------------------------------------------------------------------
// The editing bot
// ---------------------------------------------------------------------------------------------------------------
const EDIT_KEYS = ['deck', 'sideboard', 'equipmentAttackSlotCount', 'removedAttackSlotIds', 'editMintCounter'];
const ref = (c) => `${c.instanceId}:${c.cardId}${c.upgraded ? '+' : ''}`;
const runDocument = (run) => { const { gameProfile, runId, ...rest } = run; return canon({ ...rest, gameProfile: { id: gameProfile.id, hash: gameProfile.hash } }); };
const draftOf = (run) => Object.fromEntries(EDIT_KEYS.filter((k) => run[k] !== undefined).map((k) => [k, structuredClone(run[k])]));

// Cards a reward or purchase can add: the player's own pool, the base cards, and every card that only moves.
const rewardPool = allCards.filter((c) => /^(core|base)\./.test(c.id)).map((c) => c.id);

function editScenario(classId, seedIndex) {
  const seed = w.rngMod.sweepSeed(8000 + classes.indexOf(classId) * 100 + seedIndex);
  const run = create(seed, classId);
  const pick = botLcg(seed ^ 0x2545f491);
  const start = run.deck.map(ref);
  let settingsIndex = seedIndex % 3 === 0 ? 0 : 1 + (seedIndex % 2);
  const settingsStart = settingsIndex;
  let snapshot = null;
  const steps = [];
  const state = () => ({
    deck: run.deck.map(ref), sideboard: (run.sideboard || []).map(ref),
    keys: canon(Object.fromEntries(EDIT_KEYS.filter((k) => !['deck', 'sideboard'].includes(k)).map((k) => [k, run[k]]))),
  });
  const limits = () => {
    const settings = SETTINGS[settingsIndex];
    const ids = [...new Set([...run.deck, ...(run.sideboard || [])].map((c) => c.cardId))];
    return {
      limits: Object.fromEntries(ids.map((id) => [id, call(() => dr.deckCopyLimit(reg, id, settings, run))])),
      eligible: Object.fromEntries((run.sideboard || []).map((c) => [c.instanceId, call(() => dr.deckCardEquipmentEligible(reg, run, c))])),
      refusal: call(() => dr.deckEditRefusal(run.deck.length, settings)),
    };
  };
  const corrupt = (draft) => {
    const kind = pick(9);
    const any = [...draft.deck, ...draft.sideboard];
    switch (kind) {
      case 0: draft.deck.push({ instanceId: 'x1', cardId: 'no.such.card', upgraded: false }); return 'unknown';
      case 1: if (any.length) draft.deck.push(structuredClone(any[pick(any.length)])); return 'duplicate';
      case 2: draft.deck.push({ cardId: 'core.strike', upgraded: false }); return 'noId';
      case 3: if (any.length) any[pick(any.length)].upgraded = true; return 'upgrade';
      case 4: draft.deck.push({ instanceId: 'inv1', cardId: rewardPool[pick(rewardPool.length)], upgraded: false }); return 'invent';
      case 5: if (any.length) { const id = any[pick(any.length)].instanceId; draft.deck = draft.deck.filter((c) => c.instanceId !== id); draft.sideboard = draft.sideboard.filter((c) => c.instanceId !== id); } return 'drop';
      case 6: draft.deck = []; return 'empty';
      case 7: if (draft.sideboard.length) draft.deck.push(draft.sideboard.pop()); return 'restore';
      default: draft.editMintCounter = 5; return 'counter';
    }
  };

  for (let i = 0; i < STEPS; i++) {
    const settings = SETTINGS[settingsIndex];
    const op = pick(20);
    let step;
    if (op < 5) {
      const target = run.deck.length && pick(10) > 0 ? run.deck[pick(run.deck.length)].instanceId : 'missing';
      step = { op: 'toSideboard', id: target, result: call(() => dr.moveToSideboard(reg, run, target)) };
    } else if (op < 10) {
      const pile = run.sideboard || [];
      const target = pile.length && pick(10) > 0 ? pile[pick(pile.length)].instanceId : 'missing';
      step = { op: 'fromSideboard', id: target, result: call(() => dr.moveFromSideboard(reg, run, target, settings)) };
    } else if (op < 12) {
      // A reward or purchase: a fresh instance in the deck or the sideboard (a mover while range is off is the interesting one).
      const cardId = pick(3) === 0 && movers.length ? movers[pick(movers.length)] : rewardPool[pick(rewardPool.length)];
      const card = { instanceId: dr.unusedInstanceId(run, 'rw', cardId), cardId, upgraded: pick(4) === 0 };
      const to = pick(2) === 0 ? 'deck' : 'sideboard';
      if (to === 'deck') run.deck.push(card); else (run.sideboard = run.sideboard || []).push(card);
      step = { op: 'reward', card, to };
    } else if (op === 12) {
      snapshot = dr.beginDeckEdit(run);
      step = { op: 'begin', snapshot: canon(snapshot) };
    } else if (op === 13) {
      if (snapshot) { dr.cancelDeckEdit(run, snapshot); step = { op: 'cancel', had: true }; snapshot = null; } else step = { op: 'cancel', had: false };
    } else if (op < 18) {
      const draft = draftOf(run);
      const kinds = [];
      const edits = pick(4);
      for (let k = 0; k < edits; k++) {
        const roll = pick(10);
        if (roll < 6) {
          const from = pick(2) === 0 ? 'deck' : 'sideboard';
          const to = from === 'deck' ? 'sideboard' : 'deck';
          const list = draft[from] || [];
          if (list.length) { const [card] = list.splice(pick(list.length), 1); (draft[to] = draft[to] || []).push(card); kinds.push(`${from}->${to}`); }
        } else kinds.push(corrupt(draft));
      }
      const before = JSON.stringify(state());
      const result = call(() => dr.commitDeckEditDraft(reg, run, draft, settings));
      // A refused draft must leave the run as it was (the C# test asserts it); the oracle refuses to record a web that does not.
      if (result.error && JSON.stringify(state()) !== before) throw new Error(`oracle-core2-deck: a refused draft changed the run (${classId}/${seed}): ${result.error}`);
      // The draft is kept as text: the web compares an owned card with its draft by JSON.stringify, so the key order is part of the case.
      step = { op: 'commit', edits: kinds, draftJson: JSON.stringify(draft), result };
    } else if (op === 18) {
      settingsIndex = pick(SETTINGS.length);
      step = { op: 'settings', index: settingsIndex };
    } else {
      run.rangePolicy = { ...run.rangePolicy, enabled: run.rangePolicy?.enabled === false };
      step = { op: 'range', enabled: run.rangePolicy.enabled };
    }
    steps.push(canon({ ...step, after: state(), ...limits() }));
  }
  return canon({ classId, seed, settings: SETTINGS, settingsStart, start, steps, run: runDocument(run) });
}

// ---------------------------------------------------------------------------------------------------------------
// The draw pile in deck order
// ---------------------------------------------------------------------------------------------------------------
function orderCases() {
  const innateIds = allCards.filter((c) => (c.keywords || []).includes('innate')).map((c) => c.id);
  const isInnate = (card) => (resolveCard(reg, card).keywords || []).includes('innate');
  const cases = [];
  for (const classId of classes) {
    for (let i = 0; i < 4; i++) {
      const seed = w.rngMod.sweepSeed(8500 + classes.indexOf(classId) * 10 + i);
      const run = create(seed, classId);
      const pick = botLcg(seed);
      const deck = run.deck.map((c) => ({ ...c }));
      // Put Innate cards in the deck (the starter decks hold none) and shuffle by the bot, not the engine stream.
      for (let k = 0; k < i && innateIds.length; k++) deck.push({ instanceId: `in${k}`, cardId: innateIds[pick(innateIds.length)], upgraded: false });
      const shuffled = [...deck];
      for (let k = shuffled.length - 1; k > 0; k--) { const j = pick(k + 1); [shuffled[k], shuffled[j]] = [shuffled[j], shuffled[k]]; }
      const discard = shuffled.filter((_, k) => k % 2 === 0).map((c) => ({ ...c }));
      discard.push({ instanceId: 'stray', cardId: 'core.strike', upgraded: false });
      cases.push({
        classId, seed, deck: deck.map(ref), shuffled: shuffled.map(ref), innate: shuffled.filter(isInnate).map((c) => c.instanceId),
        drawPile: dr.orderedDrawPile(shuffled, isInnate).map((c) => c.instanceId), deckOrderPile: dr.orderedDrawPile(deck, isInnate).map((c) => c.instanceId),
        discard: discard.map((c) => c.instanceId), returned: dr.orderedReturn(discard, deck.map((c) => c.instanceId)).map((c) => c.instanceId),
        input: canon(shuffled),
      });
    }
  }
  return cases;
}

// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
let bytes = 0;
const write = (name, data) => { const text = `${JSON.stringify(canon(data))}\n`; writeFileSync(join(OUT, name), text); bytes += Buffer.byteLength(text); };

write('settings.json', { cases: settingsCases });
const index = { source: 'model/deckRules.js (configured profile default)', webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, steps: STEPS, settings: SETTINGS.length, probes: [], edits: [] };
for (const classId of classes) {
  write(`probe-${classId}.json`, probe(classId));
  index.probes.push(`probe-${classId}.json`);
  for (let i = 0; i < SEEDS; i++) {
    const name = `edit-${classId}-seed${String(i).padStart(2, '0')}.json`;
    const scenario = editScenario(classId, i);
    write(name, scenario);
    index.edits.push({ file: name, classId, seed: scenario.seed });
  }
}
write('order.json', { cases: orderCases() });
write('index.json', index);
console.log(`oracle-core2-deck: ${settingsCases.length} settings variants, ${classes.length} probes (${allCards.length} cards), ${index.edits.length} edit scenarios x ${STEPS} steps, ${(bytes / 1024 / 1024).toFixed(1)} MB -> Unity/Assets/Tests/Oracle/core2/deck`);
