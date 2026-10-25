#!/usr/bin/env node
// Core 2 card face text oracle (P-1, docs/DECISIONS.md D-1320..). Drives the SHIPPED web at core2.webSha for the configured `default`
// profile and records what a card's face SAYS: the lines of model/cardFaceText.js cardFaceLines, and the rules html that
// ui/components/card.js renderCard makes of them (the represented sets of the tag rail, the profile's face line limits, the hand's
// three-line rest, the authored text a face the model refuses keeps).
//
//   static.json            every profile card (and every upgraded variant) with no fight: what a reward, shop, deck or pile face says.
//   fights-<class>.json    fights (a class x encounters): the opening hand and a hand of every profile card under stance variants, each
//                          card with the previewCard the hand prints from (a summary of it, so a mismatch can be told from the lines) and the
//                          rules html at the three levels (hand, face, inspect).
//   index.json
//
//   node Tools/oracle-core2-face.mjs [--source <web checkout>] [--classes herald,reaver] [--encounters 2]
//
// Deterministic. Writes Unity/Assets/Tests/Oracle/core2/face/*.json (sorted keys).
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, encounters, createRun, createFight, detach } from './oracle-core2.mjs';
import { readFileSync } from 'node:fs';

const OUT = join(ORACLE_DIR, 'face');
const ENCOUNTERS = Number(arg('--encounters', '5'));
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [faceText, playing, sideTags, profileMod, baseCards, sigilView, tooltip, validate, gp] = await Promise.all([
  load('src/model/cardFaceText.js'), load('src/model/playingCard.js'), load('src/ui/components/cardTagSymbols.js'), load('src/model/combatCardProfile.js'),
  load('src/content/generated/baseCards.js'), load('src/ui/components/combatSigilView.js'), load('src/ui/components/tooltip.js'), load('src/model/validate.js'),
  load('src/model/gameProfile.js'),
]);
const smash = await load('src/model/smashClause.js');
const receiptView = await load('src/ui/components/damageReceipt.js');
const wardOff = await load('src/model/wardOffCardText.js');
const spellDef = await load('src/model/spellDefense.js');
const registries = w.registries;
const classes = arg('--classes', classIds(w).join(',')).split(',');
const all = encounters(w);
const profileCards = gp.gameContent(w.profile, 'cards');
const json = (v) => `${JSON.stringify(canon(v))}\n`;
const guarded = (fn) => { try { return fn(); } catch (e) { return { error: e.message }; } };

// card.js fillTemplate, taken verbatim from the pinned source (it is not exported): the authored text a face the model refuses keeps.
const cardSource = readFileSync(join(WEB, 'src/ui/components/card.js'), 'utf8');
const start = cardSource.indexOf('function fillTemplate(');
let depth = 0, end = -1;
for (let i = cardSource.indexOf('{', start); i < cardSource.length; i++) {
  if (cardSource[i] === '{') depth++;
  else if (cardSource[i] === '}' && --depth === 0) { end = i + 1; break; }
}
const fillTemplate = new Function('coreSmashClause', 'wardOffCardText', 'spellDefenseSplit', 'esc', 'tokenRe', `${cardSource.slice(start, end)}\nreturn fillTemplate;`)(
  smash.coreSmashClause, wardOff.wardOffCardText, spellDef.spellDefenseSplit, tooltip.esc, validate.tokenRe);

/** card.js renderCard, the part that makes the rules html (card.js:187-341), for one card at the three levels. */
function faces(ref, { preview = null, rules = null } = {}) {
  const def = playing.playingCardDefinition(registries, ref, { preview, rules });
  const rawModel = playing.playingCardModel(registries, ref, { preview, definition: def });
  const combatProfile = profileMod.combatProfileFor(def);
  const combatTags = profileMod.combatProfileTags(combatProfile, registries);
  const expanded = preview?.combatExpansionVersion === 2 || rules?.combatExpansionVersion === 2 || def.defenseRulesVersion === 2 || def.minCombatExpansionVersion === 2;
  const model = { ...rawModel, ...(expanded ? { faceType: playing.combatCardType(def) } : {}),
    tags: [...rawModel.tags, ...combatTags.filter((tag) => !rawModel.tags.some((existing) => existing.id === tag.id))] };
  model.sideTags = sideTags.cardSideTags(model, def, registries, preview, rules);
  const fallbackDamageLabel = expanded && (def.effects?.some((effect) => effect.op === 'damage') || def.counterPayload?.hp)
    ? playing.combatCardDamageLabel(def, preview, registries) : '';
  const faceRules = gp.gameModule(registries, 'cards');
  const wardPoise = wardOff.wardReadsAsPoise(registries) ? { preview } : null;
  const representedEffects = model.sideTags.flatMap((tag) => tag.representedEffects || []);
  const representedEntry = model.sideTags.flatMap((tag) => tag.representedEntry || []);
  const representedRules = model.sideTags.flatMap((tag) => tag.representedRules || []);
  const allFaceLines = baseCards.baseCardPresentation[def.id] ? null : faceText.cardFaceLines(def, preview, registries, rules,
    { stanceEntry: false, representedEffects, representedEntry, representedRules });
  const sliceable = allFaceLines && registries.gameProfile?.foundation?.mode === 'configured';
  const sliced = sliceable ? allFaceLines.slice(0, Math.min(faceRules?.faceMaxEffects ?? Infinity, faceRules?.faceMaxLines ?? Infinity)) : allFaceLines;
  const note = { kind: 'detail', html: 'Cost reflects your current weight' };
  const faceLines = sliced && def.weightClassPriced ? [...sliced, note] : sliced;
  const everyLine = allFaceLines && def.weightClassPriced ? [...allFaceLines, note] : allFaceLines;
  const authored = () => {
    const text = def.weightClassPriced
      ? `${fillTemplate(def, rawModel.tokens, rawModel.baseTokens, rawModel.damageSequences, wardPoise, registries).replace(/ Light:.*$/, '')} Cost reflects your current weight.`
      : fillTemplate(def, rawModel.tokens, rawModel.baseTokens, rawModel.damageSequences, wardPoise, registries);
    return sigilView.compactCardRules(`${fallbackDamageLabel ? `${tooltip.esc(fallbackDamageLabel)}: ` : ''}${text}`);
  };
  const render = (lines, limit) => lines
    ? sigilView.compactCardRules(lines.map((line, i) => `<span class="face-line" data-face-kind="${line.kind}"${limit !== null && (i >= limit || (line.kind === 'detail' && i > 0)) ? ' data-face-extra="true"' : ''}>${line.html}</span>`).join(''))
    : authored();
  return {
    lines: allFaceLines ?? null,
    represented: { effects: representedEffects, entry: representedEntry, rules: representedRules },
    sideTags: model.sideTags.map((tag) => ({ id: tag.id, count: tag.count ?? null })),
    hand: render(faceLines, 3), face: render(faceLines, null), inspect: render(everyLine, null),
    // card.js cardTooltip: the card's own template with the live numbers, none marked (what the inspector reads).
    reading: fillTemplate(def, rawModel.tokens, null, rawModel.damageSequences, wardOff.wardReadsAsPoise(registries) ? { preview: null } : null, registries),
  };
}

// ---------------------------------------------------------------------------------------------------------------
// The preview, as the face reads it
// ---------------------------------------------------------------------------------------------------------------
const VALUE_FIELDS = ['op', 'value', 'hits', 'hitDamages', 'totalDamage', 'faceValue', 'splitBase', 'ratingBonus', 'ratingPart', 'physicalDamageType', 'defenseSplit', 'tags', 'status', 'token', 'counterReturn'];
// The calculation behind a damage number (P-1: the hand's underlined numbers and their tooltip): the hits with the sources that made them.
const receiptOf = (receipt) => receipt && {
  targetId: receipt.targetId ?? null, totalDamage: receipt.totalDamage,
  hits: (receipt.hits || []).map((hit) => ({ incoming: hit.incoming, hpLoss: hit.hpLoss, physical: hit.physical ?? null, magical: hit.magical ?? null, blocked: hit.blocked ?? 0,
    poiseReduced: hit.poiseReduced ?? 0, wardReduced: hit.wardReduced ?? 0, barrierReduced: hit.barrierReduced ?? 0, poiseDamage: hit.poiseDamage ?? 0,
    sources: (hit.sources || []).map((source) => ({ id: source.id, label: source.label ?? null, operation: source.operation ?? null, stage: source.stage ?? null, amount: source.amount ?? null, multiplier: source.multiplier ?? null })) })),
};
function summary(preview) {
  if (!preview) return null;
  return canon({
    values: (preview.values || []).map((row) => ({ ...Object.fromEntries(VALUE_FIELDS.filter((key) => row[key] !== undefined).map((key) => [key, row[key]])), ...(row.damageReceipt ? { damageReceipt: receiptOf(row.damageReceipt) } : {}) })),
    tokens: preview.tokens, damageSequences: preview.damageSequences, counterReturn: preview.counterReturn ?? null, entryPrecision: preview.entryPrecision ?? null,
    costShift: preview.costShift ?? null, chargedEffects: preview.chargedEffects?.length ?? 0, expansion: preview.combatExpansionVersion,
  });
}

// The tooltip of an underlined damage number (damageReceipt.js damageReceiptHtml, collapsed form), as its parts: the summary, the scope and the three topics.
const unesc = (text) => text.replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
function calculation(preview) {
  const receipt = receiptView.cardDamageReceipt(preview);
  if (!receipt) return null;
  const html = receiptView.damageReceiptHtml(receipt, { targetName: preview.targetName });
  if (!html) return null;
  const paragraphs = [...html.matchAll(/<p>(.*?)<\/p>/g)].map((m) => unesc(m[1]));
  const topics = [...html.matchAll(/<span class="tooltip-keyword"[^>]*data-tip="([^"]*)">([^<]*)<\/span>/g)].map((m) => [unesc(m[2]), unesc(m[1])]);
  return { summary: paragraphs[0], scope: paragraphs[1], topics };
}

// ---------------------------------------------------------------------------------------------------------------
// Scenarios: the setups the costs oracle lays over a fight (the C# test applies the very same fields)
// ---------------------------------------------------------------------------------------------------------------
const clone = (v) => (v === undefined ? undefined : structuredClone(v));
const allInstances = () => profileCards.map((c, i) => ({ instanceId: `x${i + 1}`, cardId: c.id, upgraded: false }));
function applySetup(combat, setup) {
  const p = combat.player;
  if (setup.hand === 'all') combat.piles.hand = allInstances();
  if (Object.hasOwn(setup, 'stance')) { if (setup.stance) p.combatStance = clone(setup.stance); else delete p.combatStance; }
  if (setup.concurrent) p.concurrentStances = clone(setup.concurrent);
  for (const [id, stacks] of Object.entries(setup.statuses || {})) p.statuses[id] = { stacks };
  if (setup.phase) combat.phase = setup.phase;
  if (setup.stamina !== undefined) p.energy = setup.stamina;
  if (setup.mana !== undefined) p.mana = setup.mana;
  return combat;
}
const evadeGates = [...new Set(profileCards.map((c) => c.evade?.whileStatus).filter(Boolean))];
const SCENARIOS = [
  { name: 'opening', setup: {} },
  { name: 'all', setup: { hand: 'all', stamina: 12, mana: 12 } },
  { name: 'attack', setup: { hand: 'all', stamina: 12, mana: 12, stance: { maneuver: 'attack', camp: 'physical', range: 'melee' } } },
  { name: 'defend', setup: { hand: 'all', stamina: 12, mana: 12, stance: { maneuver: 'defend', camp: 'physical', range: 'melee' } } },
  { name: 'prepared', setup: { hand: 'all', stamina: 12, mana: 12, stance: { maneuver: 'prepared', camp: 'spell', range: 'melee' } } },
  { name: 'evade', setup: { hand: 'all', stamina: 12, mana: 12, stance: { maneuver: 'evade', camp: 'physical', range: 'melee' }, statuses: Object.fromEntries(evadeGates.map((s) => [s, 1])) } },
  { name: 'dry', setup: { hand: 'all', stamina: 0, mana: 0 } },
];

// The cards whose numbers a fight's enemies can move: those that deal damage, and the Counters (their return reads the revealed attack).
const swings = (inst) => {
  const card = profileCards.find((row) => row.id === inst.cardId);
  return !card || card.effects?.some((effect) => effect.op === 'damage') || (card.tags || []).includes('maneuver:counter');
};

function handFaces(combat, setup, onlySwings = false) {
  const state = applySetup(detach(w, combat), setup);
  const out = {};
  for (const inst of state.piles.hand) {
    if (onlySwings && !swings(inst)) continue;
    out[inst.instanceId] = guarded(() => {
      const preview = w.combat.previewCard(state, inst.instanceId, undefined, undefined, { rangeRoute: 'advance' });
      return { cardId: inst.cardId, preview: summary(preview), receipt: calculation(preview), ...faces(inst, { preview }) };
    });
  }
  return out;
}

function fight(seed, classId, encounter, openingOnly) {
  const run = createRun(w, seed, classId);
  const { combat } = createFight(w, run, encounter);
  return { seed, classId, encounterId: encounter.id, scenarios: Object.fromEntries(SCENARIOS.filter((s) => !openingOnly || ['opening', 'all'].includes(s.name)).map((s) => [s.name, { setup: s.setup, cards: handFaces(combat, s.setup, openingOnly && s.name === 'all') }])) };
}

// ---------------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const index = { webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, files: [] };
const write = (file, value, meta = {}) => {
  const text = json(value);
  writeFileSync(join(OUT, file), text);
  index.files.push({ file, bytes: Buffer.byteLength(text), ...meta });
};

const staticFaces = {};
for (const card of profileCards) {
  staticFaces[card.id] = {};
  for (const upgraded of [false, true]) staticFaces[card.id][upgraded ? 'upgraded' : 'plain'] = guarded(() => faces({ cardId: card.id, upgraded, instanceId: `s:${card.id}` }));
}
write('static.json', { cards: staticFaces }, { kind: 'static' });

classes.forEach((classId, ci) => {
  const fights = [];
  for (let e = 0; e < ENCOUNTERS; e++) {
    const ei = (e * 5 + ci * 3) % all.length;
    fights.push(fight(w.rngMod.sweepSeed(6200 + ci * 1000 + ei), classId, all[ei], e > 0));
  }
  write(`fights-${classId}.json`, { classId, fights }, { kind: 'fights', classId, fights: fights.length });
});
writeFileSync(join(OUT, 'index.json'), `${JSON.stringify(canon(index), null, 1)}\n`);
console.log(`oracle-core2-face: ${index.files.length} files, ${(index.files.reduce((n, f) => n + f.bytes, 0) / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/face`);
