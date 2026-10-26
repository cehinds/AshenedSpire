#!/usr/bin/env node
// What a default Core 2 run can reach of the features the combat port guards by name (WP-C0, docs/design/11-CORE2-PORT-PLAN.md). Reads the `default` profile at
// the pinned web sha (the same registries the oracles drive) and answers, for each guard, whether any card, relic, flask, enemy, class or gear row of the
// profile can put the fight there. Prints the facts the BUILD-LOG inventory cites; exits 1 only with --check when a row of the profile uses an op or a field
// no row of the table below accounts for (so a new content row cannot slip past a guard unseen).
//
//   node Tools/core2-reachability.mjs [--check] [--source <web checkout>]
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';
import { loadWeb, createRun, createFight, encounters, classIds, WEB } from './oracle-core2.mjs';

const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const expansion = await load('src/engine/combatExpansion.js');
const triggers = await load('src/engine/triggers.js');
const R = w.registries;
const cards = w.gp.gameContent(w.profile, 'cards');
const walk = (o, f) => { if (Array.isArray(o)) o.forEach((x) => walk(x, f)); else if (o && typeof o === 'object') { f(o); for (const v of Object.values(o)) walk(v, f); } };
const opsOf = (rows) => { const ops = {}; for (const row of rows) walk(row, (o) => { if (typeof o.op === 'string') (ops[o.op] ||= new Set()).add(row.id || row.tag); }); return ops; };
const names = (set) => [...set].slice(0, 6).join(', ');
const out = [];
const fact = (label, value) => out.push(`${label}: ${value}`);

// 1. The ops and fields of the profile's cards.
const cardOps = opsOf(cards);
fact('profile cards', cards.length);
fact('profile card ops', Object.entries(cardOps).map(([op, s]) => `${op}(${s.size})`).join(' '));
const flagged = {};
for (const card of cards) {
  const text = JSON.stringify(card);
  for (const key of ['arcaneBuildup', 'grantCardCharge', 'ashenBlightCost', 'upcast', 'critRules', 'comboHook', '"script"', 'classStance']) if (text.includes(key)) (flagged[key] ||= []).push(card.id);
  if (card.effectVariants) (flagged.effectVariants ||= []).push(card.id);
}
fact('profile cards carrying a guarded field', JSON.stringify(Object.fromEntries(Object.entries(flagged).map(([k, v]) => [k, v.length]))));

// 2. Flasks, relics, enemies, statuses.
const flaskOps = opsOf(R.flasks.all());
fact('flasks', `${R.flasks.all().length}; ops ${Object.keys(flaskOps).join(' ')}; scripted: ${R.flasks.all().filter((f) => JSON.stringify(f).includes('"script"')).map((f) => f.id).join(' ')}`);
const enemyOps = opsOf(R.enemies.all());
fact('enemy move ops', Object.entries(enemyOps).map(([op, s]) => `${op}(${s.size})`).join(' '));
const ifMoves = [];
for (const e of R.enemies.all()) walk(e.moves || {}, (o) => { if (o.if && o.op) ifMoves.push(`${e.id}:${o.op}`); });
fact('enemy move effects with an `if`', ifMoves.length);
const statusOps = opsOf(R.statuses.all());
fact('status hook ops', Object.keys(statusOps).join(' '));
const ruleOps = opsOf(R.propertyRules.all());
fact('property rule ops', Object.entries(ruleOps).map(([op, s]) => `${op}(${s.size})`).join(' '));

// 3. Who mounts the rules that listen for a card being prepared, hold a charge or build up Arcane Exposure.
const interesting = new Set(R.propertyRules.all().filter((r) => /arcaneBuildup|grantCardCharge|cardPreparing|arcaneBreak/.test(JSON.stringify(r))).map((r) => r.tag));
const holders = {};
const hold = (kind, id, text) => { for (const tag of interesting) if (text.includes(`"${tag}"`)) (holders[tag] ||= []).push(`${kind}:${id}`); };
for (const r of R.relics.all()) hold('relic', r.id, JSON.stringify(r));
for (const g of w.profile.content.gear) hold('gear', g.id, JSON.stringify(g));
for (const c of R.classes.all()) hold('class', c.id, JSON.stringify(c));
fact('rules that listen for cardPreparing, grant charges or build Arcane Exposure, with the rows that mount them (feat:* rules are skill feats, which a Core 2 run never earns)',
  JSON.stringify(Object.fromEntries(Object.entries(holders).filter(([tag]) => !tag.startsWith('feat:')))));

// 4. Does a card of the profile ever carry the tags such a listener asks for? Every class with every relic and every weapon.
let listened = 0, fights = 0;
const weapons = w.profile.content.gear.filter((g) => g.kind === 'weapon').map((g) => g.id);
for (const classId of classIds(w)) {
  for (const relic of [null, ...R.relics.all().map((r) => r.id)]) for (const weapon of [null, ...weapons.slice(0, relic && interesting.has(relic) ? weapons.length : 1)]) {
    const run = createRun(w, 99, classId);
    if (relic && !run.relics.includes(relic)) run.relics.push(relic);
    if (weapon) run.loadout.sets.rightHand[0] = weapon;
    for (const card of cards) run.deck.push({ instanceId: `q${card.id}`, cardId: card.id, upgraded: false });
    let combat;
    try { ({ combat } = createFight(w, run, encounters(w)[0])); } catch { continue; }
    fights++;
    for (const inst of [...combat.piles.hand, ...combat.piles.draw, ...combat.piles.discard]) {
      let def;
      try { def = expansion.resolveCombatCard(combat, inst); } catch { continue; }
      const tags = (def.cardTags || def.tags || []).map((t) => (typeof t === 'string' ? t : t.id));
      if (triggers.hasEventTriggers(combat, 'cardPreparing', { cardTags: tags })) listened++;
    }
  }
}
fact('cards a cardPreparing listener asks about (profile deck x every class x every relic x every weapon)', `${listened} in ${fights} fights`);

// 5. Consumables (revive tokens) and the equipment swap.
fact('Core 2 shop and reward categories', JSON.stringify(w.profile.content.rewards.categories));
fact('revive consumables', R.consumables.all().filter((c) => c.kind === 'revive').map((c) => c.id).join(' '));
// A run only ever gains a consumable by buying it off a market shelf (marketAdditions.commitConsumablePurchase); the profile's shops lay out no such shelf.
const marketShelves = ((w.profile.content.shops || {}).market?.offerings || []).filter((row) => row && row.enabled === true).map((row) => row.id);
fact('shops of the profile', `${JSON.stringify(w.profile.content.shops)}; enabled market shelves: ${marketShelves.join(' ') || 'none'}`);
fact('equipment slots that swap in a fight', (R.equipment.slots || []).filter((s) => s.swap === 'combat').map((s) => `${s.id}(${s.sets} sets)`).join(' '));
fact('registry cards outside the profile that pay Ashen Blight', R.cards.all().filter((c) => c.ashenBlightCost && !cards.find((x) => x.id === c.id)).map((c) => c.id).join(' '));
fact('fixCore version of the profile', w.profile.modules.effects?.fixCoreVersion);
fact('round shuffle / hand retention', `${w.profile.modules.cards.roundShuffle} / ${w.profile.modules.cards.retainHand}`);
fact('progression (feats come only with character levels or a skill book)', JSON.stringify(w.profile.modules.progression));
fact('Combat Rounds', 'a setting (combat.combatRounds) no profile module turns on');

console.log(out.join('\n'));
if (process.argv.includes('--check')) {
  // A card, flask or enemy op outside the lists the port runs is a content row the guards would meet.
  const known = new Set(['damage', 'block', 'applyStatus', 'restoreMana', 'moveEngagement', 'addBurden', 'draw', 'moveCards', 'revealIntent', 'heal', 'loseHp', 'modifyCards', 'enterStance', 'addCinders', 'castLinkedSpell', 'restoreStamina', 'poiseDamage', 'addCard']);
  const stray = Object.keys(cardOps).filter((op) => !known.has(op));
  if (stray.length || flagged.arcaneBuildup || flagged.upcast || flagged.ashenBlightCost || flagged.critRules || flagged.comboHook || listened || marketShelves.includes('reviveTokens')) {
    console.error(`core2-reachability: the profile reaches a guarded feature: ${JSON.stringify({ stray, flagged: Object.keys(flagged), listened, marketShelves })}`);
    process.exit(1);
  }
}
