#!/usr/bin/env node
// Core 2 combatant door oracle (P-7b, docs/DECISIONS.md D-1730..). Drives the SHIPPED web at core2.webSha and records what the combatant door reads for the player and each
// living enemy: ui/screens/combat.js combatantSubject through ui/components/combatantInspector.js combatantDetailBody (the sections of ui/models/CombatantInspectorSections.js,
// the bestiary panel and the prediction panel of ui/components/enemyKnowledge.js, the enemy deck section) and combatantInspectorPreview (the pool meters beside the figure).
// The web builds DOM from kit pieces; this script walks the same data in the same order and writes what each piece says (its words, not its markup), so the C# view model
// (Application/Combat/Core2CombatantInspector) is compared block for block.
//
//   fights-<class>.json   fights (a class x encounters) played by the cards bot; after the opening and after each turn, under setups laid on a detached copy of the fight (the
//                         moves unread, read, then with Block, a stance, a prepared Evade and statuses laid on both sides); per setup the door of the player and of each living enemy.
//   index.json
//
//   node Tools/oracle-core2-inspector.mjs [--source <web checkout>] [--classes herald,reaver] [--encounters 3] [--turns 4]
//
// Deterministic. Writes Unity/Assets/Tests/Oracle/core2/inspector/*.json (sorted keys).
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, detach, legalIntents, pickIntent, botLcg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'inspector');
const ENCOUNTERS = Number(arg('--encounters', '3'));
const TURNS = Number(arg('--turns', '4'));
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [sections, strings, abilities, intentRead, projection, burden, deckModel, know, kview, moveCards, preview, ratings, tactics, level, classCard, intentVis, state, intentDamage, tooltip, uiContent] = await Promise.all([
  'src/ui/models/CombatantInspectorSections.js', 'src/ui/strings.js', 'src/ui/components/combatAbilities.js', 'src/ui/models/EnemyIntentReadModel.js',
  'src/ui/models/CombatIntentProjectionModel.js', 'src/ui/models/EnemyBurdenModel.js', 'src/ui/models/EnemyDeckInspectionModel.js', 'src/engine/enemyKnowledge.js',
  'src/model/enemyKnowledgeView.js', 'src/model/enemyMoveCards.js', 'src/engine/enemyActionPreview.js', 'src/model/combatRatings.js', 'src/model/combatTacticsDisplay.js',
  'src/model/levelup.js', 'src/model/classCard.js', 'src/model/combatIntentVisibility.js', 'src/model/state.js', 'src/model/intentDamage.js', 'src/model/tooltipSettings.js', 'src/ui/uiContent.js',
].map(load));
const { t } = strings;
const { helpText } = tooltip;
const registries = w.registries;
const classes = arg('--classes', classIds(w).join(',')).split(',');
const all = encounters(w);
const json = (v) => `${JSON.stringify(canon(v))}\n`;
const clone = (v) => (v === undefined ? undefined : structuredClone(v));

// combat.js `words` (the door's own).
const words = (value) => String(value || '')
  .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  .replace(/[_-]+/g, ' ')
  .replace(/\b\w/g, (letter) => letter.toUpperCase());

// combat.js moveDetail.
function moveDetail(move, preview = null, entity = null) {
  const source = preview || { ...(move || {}), damage: state.enemyMoveDamage(entity, move) };
  const pieces = [];
  const sequence = intentDamage.variableIntentDamage(source);
  if (source.damage != null) pieces.push(`${sequence ? `${sequence.text} (${sequence.totalDamage} total)` : `${source.damage}${source.hits > 1 ? ` × ${source.hits}` : ''}`} damage`);
  if (source.block != null) pieces.push(`${source.block} Block`);
  for (const effect of move?.effects || []) {
    if (effect.op === 'applyStatus') pieces.push(`applies ${words(effect.status)}`);
    else if (effect.op === 'heal') pieces.push('heals');
    else if (effect.op === 'addCard') pieces.push(`adds ${words(effect.card)}`);
    else pieces.push(words(effect.op));
  }
  return pieces.join(' · ') || words(source.kind || move?.intent || 'Unknown action');
}

function statusDetails(entity) {
  return Object.entries(entity.statuses || {}).flatMap(([statusId, instance]) => {
    if (!instance) return [];
    const amount = instance.meter ? instance.meter.value : instance.stacks;
    if (!(amount > 0)) return [];
    const def = registries.frameworkTerms.withStatusWords(registries.statuses.get(statusId, entity));
    if (!def) return [];
    const presentation = uiContent.statusInstancePresentation(def, instance);
    return [{ name: def.name, detail: presentation.tooltip }];
  });
}

// combat.js inspectorResources without the tooltip html (the C# door carries the words, not the hover markup).
const inspectorResources = (rows) => rows.map(({ tipId, ...row }) => row);

/** combat.js combatantSubject for a fight with no paced snapshot (`dv(entity)` is the entity). */
function combatantSubject(combat, run, role, entity, customName) {
  const configuredArt = run.gameProfile?.foundation?.mode === 'configured';
  const className = classCard.runClassIdentity(registries, run).name;
  if (role === 'player') {
    const abilityRows = abilities.activeCombatAbilities(registries, { ...entity, stanceId: entity.stanceId, evade: entity.evade }, Boolean(combat.foundation));
    return {
      role: 'player',
      name: (customName || className).toUpperCase(),
      subtitle: `${className} · Level ${level.characterLevel(run)}`,
      resources: inspectorResources([
        { label: 'HP', value: entity.hp, max: entity.maxHp },
        { label: 'MP', value: entity.mana, max: entity.maxMana },
        { label: 'Poise', value: entity.poiseMeter?.value || 0, max: entity.poiseMeter?.max || entity.poiseMeter?.max || 0 },
        ...(entity.wardMeter ? [{ label: 'Ward', value: entity.wardMeter?.value || 0, max: entity.wardMeter?.max || entity.wardMeter.max }] : []),
        ...(entity.poiseGuard > 0 ? [{ label: t('combat.protection.poiseGuard'), value: entity.poiseGuard }] : []),
        ...(entity.wardGuard > 0 ? [{ label: t('combat.protection.wardGuard'), value: entity.wardGuard }] : []),
        ...(entity.ratings ? (combat.breakMeterVersion === 1 ? ['ar', 'dr', 'pr', 'ward'] : ['ar', 'dr', 'pr']).map((id) => ({ label: id.toUpperCase(), value: ratings.ratingValue(combat, entity, id) })) : []),
        { label: t('combat.protection.block'), value: entity.block || 0 },
      ]),
      skillLabel: 'Active skills & stance',
      abilities: abilityRows,
      skills: abilityRows,
      statuses: abilityRows.filter((row) => row.kind !== 'stance'),
      entityId: 'player',
      history: null, traits: null, lore: null,
    };
  }
  const def = registries.enemies.get(entity.enemyId);
  const intent = configuredArt && !w.gp.gameModule(run, 'intent') ? { hidden: true, label: t('gameProfile.combat.unreadIntent') } : projection.projectCombatIntent(combat, entity.id);
  const options = { registries, combatMatchupRules: combat.combatMatchupRules, combatExpansionVersion: combat.combatExpansionVersion, stanceRules: combat.stanceRules };
  const learning = combat.enemyKnowledge ? kview.projectEnemyKnowledge(def, combat.enemyKnowledge.owners.player?.knowledge.enemies[def.id], { ...options, ratingsRules: combat.ratingsRules }) : null;
  if (!learning) throw new Error('oracle-core2-inspector: a Core 2 enemy reads through its bestiary; the C# door ports that path only');
  const currentMoveId = intent.moveId;
  const past = entity.performedMoves || [];
  const actionPreviews = Object.fromEntries(Object.keys(def.moves || {}).map((moveId) => [moveId, preview.previewEnemyAction(combat, entity, moveId)]));
  const inspectedMoves = moveCards.enemyMoveCards(def, { ...options, enemy: entity, preview: intent, actionPreviews });
  const deckInspection = deckModel.enemyDeckInspection(combat, entity, inspectedMoves, intent);
  return {
    role: 'enemy',
    deckInspection,
    learning, perception: combat.skills?.perception?.level || 0,
    prediction: know.knowledgePredictionModel(combat, entity),
    name: def.name,
    subtitle: (def.tags || []).map(words).join(' · ') || t('combat.log.role.enemy'),
    resources: inspectorResources([
      { label: 'HP', value: entity.hp, max: entity.maxHp },
      ...(Number.isFinite(entity.maxStamina) ? [{ label: 'SP', value: entity.fixCoreStaminaRemaining ?? entity.stamina ?? 0, max: entity.maxStamina }] : []),
      { label: 'Poise', value: entity.poiseMeter?.value || 0, max: entity.poiseMeter?.max || entity.poiseMeter?.max || 0 },
      ...(entity.wardMeter ? [{ label: 'Ward', value: entity.wardMeter?.value || 0, max: entity.wardMeter?.max || entity.wardMeter.max }] : []),
      ...(combat.breakMeterVersion === 1 && entity.ratings ? [{ label: t('combat.rating.ward'), value: ratings.ratingValue(combat, entity, 'ward') }] : []),
      { label: t('combat.protection.block'), value: entity.block || 0 },
    ]),
    intent: intentRead.enemyIntentRead(combat, entity, { preview: intent, inspectedMoves, unknownDetail: t('combat.targetHud.unknown') }),
    burdenRead: burden.projectEnemyBurdenHand(entity, { turn: combat.turn, registries, ownerId: combat.playerKey || 'player', legacySolo: !(combat.players instanceof Map) }),
    skillLabel: 'Move set',
    moveCards: learning.moveCards,
    skills: null,
    statuses: [...[intent.hidden ? null : tactics.combatStanceRow(entity), tactics.preparedEvadeRow(entity)].filter(Boolean), ...statusDetails(entity)],
    entityId: entity.id,
    history: past.map((moveId) => ({ name: words(moveId), detail: moveDetail(def.moves?.[moveId], null, entity) })),
    traits: learning.traits,
    lore: learning.lore,
  };
}

// What the door's pieces say, in the order combatantDetailBody puts them.
const rowOf = (label, status = '', extra = {}) => ({ label, status, ...extra });

function factRows(section) {
  if (section.knowledge !== 'known') return [rowOf(t(`inspector.empty.${section.knowledge}`), '', { disabled: true })];
  return section.rows.map((r) => rowOf(r.label, [r.value, r.detail].filter(Boolean).join(' · ')));
}
const projectedSection = (section) => ({ kind: 'section', id: section.id, title: section.title, knowledge: section.knowledge, rows: factRows(section) });

function cardRows(cards) {
  return cards.map((card) => ({ name: card.name, detail: card.detail, meta: card.meta, selected: !!card.active }));
}

function knowledgeBlock(view, perception) {
  const progress = view.progress;
  const lines = [
    { k: 'eyebrow', text: t('knowledge.bestiary') },
    { k: 'title', text: t('knowledge.progress', { label: progress.label, points: progress.points, target: progress.target }) },
    { k: 'prose', text: t('knowledge.lifetime') },
    { k: 'status', text: progress.nextStage ? t('knowledge.next', { label: progress.nextStage.label, points: progress.nextStage.points }) : t('knowledge.mastered') },
    ...(perception === null ? [] : [{ k: 'prose', text: t('knowledge.perception', { level: perception }) }]),
    ...view.stages.map((stage) => ({ k: 'stage', text: t('knowledge.stage', { stage: stage.stage, label: stage.label, status: stage.learned ? t('knowledge.learned') : t('knowledge.unlock', { points: stage.requiredPoints }) }), learned: stage.learned })),
    { k: 'hairline' },
    { k: 'eyebrow', text: t('knowledge.resources') },
    ...(view.resources === null ? [{ k: 'status', text: t('knowledge.resources.locked', { points: view.stages[1].requiredPoints }) }] : view.resources.length
      ? view.resources.map((fact) => ({ k: 'row', label: fact.label, status: fact.value })) : [{ k: 'status', text: t('knowledge.resources.empty') }]),
    ...(view.traits || []).map((fact) => ({ k: 'row', label: fact.name, status: fact.detail || '' })),
    { k: 'hairline' },
    { k: 'eyebrow', text: t('knowledge.moves') },
    ...(view.moveCards === null ? [{ k: 'status', text: t('knowledge.moves.locked', { points: view.stages[2].requiredPoints }) }] : [{ k: 'cards', cards: cardRows(view.moveCards) }]),
    ...(progress.stage >= 3 && progress.stage < 4 ? [{ k: 'status', text: t('knowledge.effects.locked', { points: view.stages[3].requiredPoints }) }] : []),
    ...(progress.stage >= 3 && progress.stage < 5 ? [{ k: 'status', text: t('knowledge.rules.locked', { points: view.stages[4].requiredPoints }) }] : []),
    { k: 'hairline' },
    { k: 'eyebrow', text: t('knowledge.lore') },
    ...(view.lore === null ? [{ k: 'status', text: t('knowledge.lore.locked') }]
      : [...(view.role ? [{ k: 'prose', text: view.role }] : []), ...view.lore.map((line) => ({ k: 'prose', text: line })), ...(!view.lore.length ? [{ k: 'status', text: t('knowledge.lore.empty') }] : [])]),
  ];
  return { kind: 'knowledge', stage: progress.stage, lines };
}

function predictionBlock(model) {
  const lines = [
    { k: 'eyebrow', text: t('knowledge.prediction') },
    { k: 'status', text: model.prediction
      ? t('knowledge.prediction.status', { prediction: model.prediction, status: t(model.resolved ? model.correct === true ? 'knowledge.prediction.correct' : model.correct === false ? 'knowledge.prediction.incorrect' : 'knowledge.prediction.cancelled' : 'knowledge.prediction.awaiting') })
      : t('knowledge.prediction.choose') },
    ...(model.feedback || []).map((receipt) => ({ k: 'status', text: t('knowledge.prediction.feedback', { serial: receipt.actionSerial, prediction: receipt.prediction, outcome: t(receipt.outcome === 'cancelled' ? 'knowledge.prediction.feedback.cancelled' : receipt.correct ? 'knowledge.prediction.feedback.correct' : 'knowledge.prediction.feedback.incorrect') }) })),
  ];
  if (!model.eligible) return { kind: 'prediction', eligible: false, lines };
  return { kind: 'prediction', eligible: true, serial: model.actionSerial, lines: [...lines, { k: 'prose', text: t('knowledge.prediction.help') },
    { k: 'select', label: t('knowledge.prediction.expected'), aria: t('knowledge.prediction.action'), choices: [...w.predictionChoices] },
    { k: 'button', text: t('knowledge.prediction.commit') }] };
}

function deckBlock(read) {
  const action = (label, card, empty) => ({ summary: `${label}: ${card?.name || empty}`, cards: card ? cardRows([card]) : [] });
  return { kind: 'deck', eyebrow: 'Enemy deck & played cards',
    note: 'Action SP includes combat modifiers. Card base shows authored SP/MP; effects use combat move rules.',
    current: action('Current action', read.current, read.currentHidden ? t('gameProfile.combat.unreadIntent') : 'None'),
    last: action('Last played', read.last, 'None yet'),
    authored: { summary: `Authored deck (${read.cards.reduce((count, card) => count + (card.copies || 1), 0)})`, cards: cardRows(read.cards) } };
}

function abilityBlock(list) {
  return { kind: 'abilities', title: `${helpText('activeListTitle')} (${list.length})`, items: list.map((a) => ({ name: a.name, detail: a.detail })), empty: list.length ? null : 'No active skills or effects.' };
}

function revealedHandBlock(read) {
  if (read?.visible !== true) return null;
  return { kind: 'revealedHand', title: 'Revealed enemy hand', rows: read.cards.map((card) => rowOf(`${card.name} ×${card.count}`, card.type)),
    empty: read.cards.length ? null : 'Hand is empty.', counts: ['draw', 'hand', 'discard', 'exhaust'].map((zone) => `${zone}: ${read.counts[zone]}`).join(' · ') };
}

/** combatantDetailBody + combatantInspectorPreview + the door's head, as what they say. */
function door(subject) {
  const view = sections.projectCombatantInspector(subject, t);
  const byId = Object.fromEntries(view.sections.map((s) => [s.id, s]));
  let abilitiesBlock = projectedSection(byId.abilities);
  if (byId.abilities.knowledge === 'known') {
    abilitiesBlock = subject.abilities ? abilityBlock(subject.abilities) : null;
    if (!abilitiesBlock) throw new Error('oracle-core2-inspector: a known ability list without abilities is outside the Core 2 door');
  }
  const blocks = [
    projectedSection(byId.summary),
    projectedSection(byId.state),
    revealedHandBlock(subject.burdenRead || subject.intent?.burdenRead),
    subject.learning ? knowledgeBlock(subject.learning, subject.perception) : null,
    subject.prediction ? predictionBlock(subject.prediction) : null,
    projectedSection(byId.history),
    subject.deckInspection ? deckBlock(subject.deckInspection) : null,
    subject.learning ? null : abilitiesBlock,
    projectedSection(byId.traits),
    projectedSection(byId.lore),
  ].filter(Boolean);
  return {
    eyebrow: subject.subtitle || 'Combatant', title: subject.name,
    preview: { name: view.preview.name, meters: view.preview.meters.map((r) => ({ label: r.label, value: r.max == null ? String(r.value) : `${r.value} / ${r.max}`, cur: r.value, max: r.max == null ? r.value : r.max,
      pct: r.max ? Math.max(0, Math.min(100, (r.value / r.max) * 100)) : (r.value > 0 ? 100 : 0) })) },
    blocks,
  };
}

// The setups: laid on a detached copy of the fight (the C# test lays the same fields on its own). Statuses are [id, instance] pairs: the order they are laid in is the order the door lists them.
const SCENARIOS = [
  { name: 'asIs', setup: {} },
  { name: 'read', setup: { read: true } },
  { name: 'laid', setup: {
    read: true, playerBlock: 7, enemyBlock: 4, playerStance: { maneuver: 'defend', camp: 'physical', range: 'melee', reach: 'melee' },
    enemyStance: { maneuver: 'attack', camp: 'physical', range: 'melee' }, playerEvade: { charges: 2 }, enemyEvade: { charges: 1 },
    playerStatuses: [['strength', { stacks: 3 }], ['vulnerable', { stacks: 2, duration: 2 }], ['magicVulnerable', { stacks: 25, duration: 2 }]],
    enemyStatuses: [['vulnerable', { stacks: 1, duration: 1 }], ['bleed', { stacks: 0, duration: 0, meter: { value: 3, max: 8 } }], ['strength', { stacks: 2 }], ['weak', { stacks: 1 }]] } },
  // A Read Intent card (proposalIntentRevealedUntilByOwner) opens the move, and the enemy's burden hand with it.
  { name: 'revealed', setup: { read: false, reveal: 2, burdenCards: ['strike', 'strike', 'defend'], burdenCounts: { draw: 1, discard: 2 } } },
  { name: 'staggered', setup: { read: true, enemyStance: { staggered: true, maneuver: 'staggered', staminaLoss: 1, vulnerableStacks: 2 }, playerStance: { staggered: true, maneuver: 'staggered', staminaLoss: 2, vulnerableStacks: 0 } } },
];

function lay(combat, setup) {
  const p = combat.player;
  for (const e of combat.enemies) {
    if (setup.reveal) e.proposalIntentRevealedUntilByOwner = { player: combat.turn + setup.reveal };
    if (setup.burdenCards) e.burdenPiles = { draw: Array.from({ length: setup.burdenCounts.draw }, (_, i) => ({ instanceId: `b${i}`, cardId: 'strike' })),
      hand: setup.burdenCards.map((cardId, i) => ({ instanceId: `h${i}`, cardId })), discard: Array.from({ length: setup.burdenCounts.discard }, (_, i) => ({ instanceId: `d${i}`, cardId: 'defend' })), exhaust: [] };
    if (setup.read) e.actorIntentRevealed = true;
    if (setup.enemyBlock !== undefined) e.block = setup.enemyBlock;
    if (setup.enemyStance) e.combatStance = clone(setup.enemyStance);
    if (setup.enemyEvade) e.combatEvade = clone(setup.enemyEvade);
    for (const [id, inst] of setup.enemyStatuses || []) e.statuses[id] = clone(inst);
  }
  if (setup.playerBlock !== undefined) p.block = setup.playerBlock;
  if (setup.playerStance) p.combatStance = clone(setup.playerStance);
  if (setup.playerEvade) p.combatEvade = clone(setup.playerEvade);
  for (const [id, inst] of setup.playerStatuses || []) p.statuses[id] = clone(inst);
  return combat;
}

function snapshot(combat, run, customName) {
  const out = {};
  for (const s of SCENARIOS) {
    const copy = lay(detach(w, combat), s.setup);
    const doors = {};
    doors.player = door(combatantSubject(copy, run, 'player', copy.player, customName));
    for (const e of copy.enemies) {
      if (e.alive === false || !(e.hp > 0)) continue;
      doors[e.id] = door(combatantSubject(copy, run, 'enemy', e, customName));
    }
    out[s.name] = { setup: s.setup, doors };
  }
  return out;
}

function fight(seed, classId, encounter, customName) {
  const run = createRun(w, seed, classId);
  if (customName) run.customization = { ...(run.customization || {}), name: customName };
  const { combat } = createFight(w, run, encounter);
  const pick = botLcg(seed ^ 0x9e3779b9);
  const turns = [{ intents: [], scenarios: snapshot(combat, run, customName) }];
  for (let turn = 0; turn < TURNS && !combat.result; turn++) {
    const done = [];
    const before = combat.turn;
    // one turn: the bot plays cards and ends it (answering any offer) until the turn number moves on
    for (let i = 0; i < 40 && !combat.result && combat.turn === before; i++) {
      const legal = legalIntents(w, combat, {});
      const intent = pickIntent('cards', legal, pick);
      if (!intent) break;
      try { w.combat.dispatch(combat, structuredClone(intent)); } catch { break; }
      done.push(intent);
    }
    if (combat.result) break;
    turns.push({ intents: done, scenarios: snapshot(combat, run, customName) });
  }
  return { seed, classId, encounterId: encounter.id, playerName: customName || null, turns };
}

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, files: [] };
classes.forEach((classId, ci) => {
  const fights = [];
  for (let e = 0; e < ENCOUNTERS; e++) {
    const ei = (e * 11 + ci * 7) % all.length;
    fights.push(fight(w.rngMod.sweepSeed(9100 + ci * 1000 + ei), classId, all[ei], e === 1 ? 'Ember' : null));
  }
  const text = json({ classId, fights });
  writeFileSync(join(OUT, `fights-${classId}.json`), text);
  index.files.push({ file: `fights-${classId}.json`, bytes: Buffer.byteLength(text), fights: fights.length });
});
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-inspector: ${index.files.length} files, ${(index.files.reduce((n, f) => n + f.bytes, 0) / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/inspector`);
