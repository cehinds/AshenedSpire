#!/usr/bin/env node
// Core 2 snapshot oracle (WP-13, docs/design/11-CORE2-PORT-PLAN.md). Replays the recorded Core 2 fights (damage, matchups, statuses, reactions,
// enemy and range sets: the fixtures under Unity/Assets/Tests/Oracle/core2/<set>/) on the SHIPPED web engine at the pinned sha and records
// `serializeCombatSnapshot(combat)` (src/engine/combatSnapshot.js) at sampled steps, so the C# snapshot can be held to the web's shape.
//
//   node Tools/oracle-core2-snapshot.mjs [--per-signature 2] [--max 48] [--max-events 70] [--source <web checkout>]
//
// SAMPLING: after every step of every fight the snapshot's SIGNATURE is computed (the top-level keys it carries, the reaction stack's sides, whether an
// enemy holds a plan, a hidden card or a Barrier, the phase); a snapshot is kept while fewer than --per-signature of its signature were kept, up to
// --max in all. So a paused fight with an enemy entry on the stack, a plan, a nested choice and a resting turn are each seen.
//
// A snapshot is taken only while the event log is short (--max-events): the log is part of the snapshot and grows with the fight, so the fixture stays
// modest and the early turns (where every reaction shape first appears) are the ones it keeps.
//
// WHAT IS NOT RECORDED: the previews and decoration the C# port does not build (an offer's `intentPreview`, an option's `range`, and the `abilityBefore`,
// `abilityEntities`, `cardTargetsAllEnemies`, `intentPreview`, `actionPreview` every event carries); the pinned `gameProfile` body (a 640 KB document the C# snapshot replaces with its `{ id, hash }` pin, D-280); the record keeps the
// pin and the C# test holds the pin to the profile it has loaded. Everything else is the web's snapshot as written, sorted keys.
//
// File: Unity/Assets/Tests/Oracle/core2/snapshot/snapshots.json  { webSha, profileHash, snapshots: [{ set, file, collection, index, step, signature, snapshot }] }
import { writeFileSync, readFileSync, mkdirSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, encounters, createRun, createFight } from './oracle-core2.mjs';

const PER_SIGNATURE = Number(arg('--per-signature', '2'));
const MAX = Number(arg('--max', '48'));
const MAX_EVENTS = Number(arg('--max-events', '70'));
const SETS = ['reactions', 'range', 'damage', 'matchups', 'statuses', 'enemy'];
const OUT = join(ORACLE_DIR, 'snapshot');
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const S = await load('src/engine/statuses.js');
const actions = await load('src/engine/actions.js');
const knowledgeRules = await load('src/model/enemyKnowledgeRules.js');
const knowledgeRun = await load('src/model/enemyKnowledgeRun.js');
const all = encounters(w);

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
function merge(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete target[key];
    else if (isObject(value) && isObject(target[key])) merge(target[key], value);
    else target[key] = structuredClone(value);
  }
  return target;
}
const entityOf = (c, who) => (who === 'p' ? c.player : who === null || who === undefined ? null : c.enemies[Number(who.slice(1))]);

/** The fight a record names (the superset of every set's open): the run, the cards laid over it, the patch merged over the fighters. */
function open(spec) {
  const run = createRun(w, spec.seed, spec.classId);
  if (spec.runPatch) merge(run, spec.runPatch);
  if (spec.knowledge) {
    run.enemyKnowledgeRules = knowledgeRules.snapshotEnemyKnowledgeRules(w.registries);
    knowledgeRun.openRunEnemyKnowledge(run, { receiptId: spec.knowledge.receiptId, bankable: spec.knowledge.bankable !== false });
  }
  if (spec.deckRemove) run.deck = run.deck.filter((card) => !spec.deckRemove.includes(card.cardId));
  for (const add of spec.deckAdd || []) run.deck.push({ instanceId: add.instanceId, cardId: add.cardId, upgraded: false });
  const encounter = spec.enemyIds ? { id: `solo:${spec.enemyIds.join('+')}`, enemies: spec.enemyIds, pool: 'normal' } : all.find((e) => e.id === spec.encounterId);
  const { combat } = createFight(w, run, encounter);
  if (spec.hand) combat.piles.hand = spec.hand.map((cardId, i) => ({ instanceId: `mx${i + 1}`, cardId, upgraded: false }));
  if (spec.fields) merge(combat, spec.fields);
  if (spec.patch?.player) merge(combat.player, spec.patch.player);
  for (const [index, patch] of Object.entries(spec.patch?.enemies || {})) if (combat.enemies[Number(index)]) merge(combat.enemies[Number(index)], patch);
  return combat;
}

/** Runs the queue to empty (the statuses oracle's drain). */
function drain(c) {
  let guard = 0;
  while (c.queue.length) {
    if (++guard > 200) throw new Error('queue did not drain');
    const action = c.queue.shift();
    actions.executeAction(c, action);
    if (c.result) { c.queue.length = 0; break; }
  }
}

function play(combat, step) {
  const intent = step.intent;
  if (Array.isArray(step.staggered)) for (const id of step.staggered) combat.enemies.find((e) => e.id === id).skipNextTurn = true;
  try {
    if (intent.type === 'applyStatus') {
      S.applyStatus(combat, entityOf(combat, intent.who), intent.status, intent.stacks, entityOf(combat, intent.from));
      drain(combat);
    } else w.combat.dispatch(combat, structuredClone(intent));
  } catch (e) { return e.message; }
  return null;
}

const DECORATION = ['abilityBefore', 'abilityEntities', 'cardTargetsAllEnemies', 'intentPreview', 'actionPreview'];
const undecorate = (event) => Object.fromEntries(Object.entries(event).filter(([key]) => !DECORATION.includes(key)));

const signatureOf = (s) => JSON.stringify({
  keys: Object.keys(s).sort(),
  stack: s.reactionStack ? s.reactionStack.entries.map((e) => e.side).join('') : null,
  resume: !!s.reactionResume,
  plans: s.enemies.some((e) => (e.plannedMoves || []).length > 0),
  hidden: (s.hiddenCards || []).length > 0,
  barrier: [s.player, ...s.enemies].some((e) => (e.barrier || 0) > 0),
  counter: [s.player, ...s.enemies].some((e) => e.combatCounter !== undefined && e.combatCounter !== null),
  phase: s.phase,
  queue: s.abilityQueue ? Math.min(s.abilityQueue.length, 3) : null,
  knowledge: s.enemyKnowledge ? Object.keys(s.enemyKnowledge.profiles || {}).length > 0 : null,
});

const kept = [];
const seen = new Map();
let considered = 0;
const failures = {};
for (const set of SETS) {
  const dir = join(ORACLE_DIR, set);
  if (!existsSync(dir)) continue;
  const files = readdirSync(dir).filter((f) => f === 'exchanges.json' || /^fights-.*\.json$/.test(f)).sort();
  for (const file of files) {
    const doc = JSON.parse(readFileSync(join(dir, file), 'utf8'));
    const collection = doc.fights ? 'fights' : 'exchanges';
    doc[collection].forEach((record, index) => {
      const combat = open(record);
      record.steps.forEach((step, n) => {
        const error = play(combat, step);
        if (error || kept.length >= MAX || combat.eventLog.length > MAX_EVENTS) return;
        considered++;
        let snapshot;
        try { snapshot = w.snapshot.serializeCombatSnapshot(combat); } catch (e) { const key = e.message.slice(0, 80); failures[key] = (failures[key] || 0) + 1; return; }
        const signature = signatureOf(snapshot);
        const count = seen.get(signature) || 0;
        if (count >= PER_SIGNATURE) return;
        seen.set(signature, count + 1);
        const pinned = { ...snapshot, gameProfile: { id: snapshot.gameProfile.id, hash: snapshot.gameProfile.hash }, eventLog: snapshot.eventLog.map(undecorate) };
        if (pinned.pendingReaction) {
          const { intentPreview, ...offer } = pinned.pendingReaction;
          pinned.pendingReaction = { ...offer, options: offer.options.map(({ range, ...option }) => option) };
        }
        kept.push({ set, file, collection, index, step: n, signature: JSON.parse(signature), snapshot: canon(pinned) });
      });
    });
  }
}

mkdirSync(OUT, { recursive: true });
const text = `${JSON.stringify(canon({ webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, considered, failures, snapshots: kept }))}\n`;
writeFileSync(join(OUT, 'snapshots.json'), text);
console.log(`oracle-core2-snapshot: ${kept.length} snapshots kept of ${considered} steps (${seen.size} signatures), ${(Buffer.byteLength(text) / 1024).toFixed(0)} KB, serialize failures ${JSON.stringify(failures)}`);
