#!/usr/bin/env node
// Core 2 run-host oracle (WP-C1a, docs/design/11-CORE2-PORT-PLAN.md, D-302). Records, on the shipped web, what main.js does to a Core 2 run between the title and a
// fight, headlessly: the new run (createRunState; the run's ledgers; the seat order on the `seats` stream; the first act's map), then a climb that mirrors enterNode,
// startFight and advanceAct line for line (main.js 1181-1345, 1426, 2805-2985): the path, the floor, the encounter each fight node rolls (`enemyAI` stream) or
// names (a boss node), the nodes that are not fights (rest, shop, treasure, event) as the kind main.js routes them to, and the act advance after each boss. Nothing is
// fought: the climb is the walk of a bot that picks a random next node. The oracle mirrors main.js, so re-check the line ranges when the pinned sha moves.
//
//   node Tools/oracle-core2-host.mjs [--seeds 6]    -> Unity/Assets/Tests/Oracle/core2/host/climbs.json
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, arg, canon, classIds, createRun, botLcg } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'host');
const SEEDS = Number(arg('--seeds', '8'));
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [actmap, encountersMod, seats, gameProfile] = await Promise.all([load('src/engine/actmap.js'), load('src/engine/encounters.js'), load('src/model/seats.js'), load('src/model/gameProfile.js')]);

const ROUTES_ACTS = gameProfile.gameContent(w.registries, 'routes').acts;

/** main.js beginPreparedRun + startClimb for a Core 2 run, as far as the run document goes. */
function newClimb(seed, classId) {
  const run = createRun(w, seed, classId);
  run.stats = { fightsWon: 0, damageDealt: 0, damageTaken: 0 };
  run.path = [];
  run.seenEvents = [];
  run.lastEncounters = [];
  const rng = w.createRng(seed);
  run.seatOrder = actmap.drawSeatOrder(w.registries, rng, { firstSeat: null });
  run.mapGraph = actmap.buildActMap(w.registries, rng, seats.seatAtTier(run.seatOrder, run.actNumber), run.actNumber, null, { history: run.history });
  return { run, rng };
}

/** The stream counters that moved (a zero counter is no draw). */
const live = (rng) => Object.fromEntries(Object.entries(rng.getCounters()).filter(([, n]) => n));
const seatNow = (run) => seats.seatAtTier(run.seatOrder, run.actNumber);

/** main.js enterNode + startFight: what a node does to the run, and the door it opens. */
function enterNode(run, rng, nodeId) {
  const node = run.mapGraph.nodes[nodeId];
  run.mapNodeId = nodeId;
  if (!run.path.includes(nodeId)) run.path.push(nodeId);
  run.floor = node.floor;
  let kind = node.type;
  if (kind === 'event') {
    const res = node.resolved || { kind: 'fight' };
    if (res.kind === 'event') { run.seenEvents.push(res.eventId); return { door: 'event', eventId: res.eventId }; }
    kind = res.kind;
  }
  const fight = (pool) => {
    const encounterId = pool === 'boss'
      ? actmap.bossEncounterForNode(w.registries, run.mapGraph, nodeId, { seat: seatNow(run), tier: run.actNumber })
      : encountersMod.rollEncounter(w.registries, rng, { pool, seat: seatNow(run), exclude: run.lastEncounters });
    if (pool === 'normal') { run.lastEncounters.push(encounterId); if (run.lastEncounters.length > 2) run.lastEncounters.shift(); }
    return { door: 'fight', pool, encounterId };
  };
  switch (kind) {
    case 'monster': case 'fight': return fight('normal');
    case 'elite': return fight('elite');
    case 'boss': return fight('boss');
    case 'shrine': return { door: 'rest', camp: node.type === 'event' };
    case 'merchant': return { door: 'shop' };
    case 'treasure': return { door: 'treasure' };
    default: throw new Error(`Unknown node kind '${kind}'`);
  }
}

/** main.js advanceAct for a run that took no healing mod. */
function advanceAct(run, rng) {
  run.actNumber += 1;
  run.floor = 0;
  run.mapNodeId = null;
  run.path = [];
  run.lastEncounters = [];
  run.hp = run.maxHp;
  run.mapGraph = actmap.buildActMap(w.registries, rng, seatNow(run), run.actNumber, null, { history: run.history });
}

/** A map in few bytes: the full graph is held once per climb (the first act's); the later acts keep each node's depth, types, resolution and exits. */
const compact = (graph) => ({ startIds: graph.startIds, bossIds: graph.bossIds ?? null, nodes: Object.fromEntries(Object.entries(graph.nodes).map(([id, n]) => [id, [n.floor, n.type, n.actualType ?? null, n.resolved ? JSON.stringify(n.resolved) : null, n.next.join(',')]])) });

function climb(seed, classId) {
  const { run, rng } = newClimb(seed, classId);
  const pick = botLcg(seed ^ 0x6a09e667);
  // The run document itself is held by the run oracle (oracle-core2-run); what this host adds to it is recorded here.
  const start = canon({ ledgers: { stats: run.stats, path: run.path, seenEvents: run.seenEvents, lastEncounters: run.lastEncounters, seatOrder: run.seatOrder, actNumber: run.actNumber, floor: run.floor, mapNodeId: run.mapNodeId }, mapGraph: run.mapGraph, counters: live(rng) });
  const nodes = [];
  const acts = [];
  for (let guard = 0; guard < 200; guard++) {
    const at = run.mapNodeId;
    const next = at == null ? run.mapGraph.startIds : run.mapGraph.nodes[at].next;
    if (!next?.length) throw new Error(`oracle-core2-host: a dead end at ${at} (${seed} ${classId})`);
    const nodeId = next[pick(next.length)];
    const door = enterNode(run, rng, nodeId);
    nodes.push(canon({ nodeId, door, floor: run.floor, path: run.path, lastEncounters: run.lastEncounters, seenEvents: run.seenEvents, counters: live(rng) }));
    if (door.pool === 'boss') {
      acts.push(run.actNumber);
      if (run.actNumber >= ROUTES_ACTS) break;
      advanceAct(run, rng);
      nodes.push(canon({ advanced: true, actNumber: run.actNumber, hp: run.hp, floor: run.floor, mapNodeId: run.mapNodeId, path: run.path, map: compact(run.mapGraph), counters: live(rng) }));
    }
  }
  return { seed, classId, start, nodes, acts };
}

const climbs = [];
classIds(w).forEach((classId, ci) => { for (let n = 0; n < SEEDS; n++) climbs.push(climb(w.rngMod.sweepSeed(80000 + ci * 100 + n), classId)); });
mkdirSync(OUT, { recursive: true });
const text = `${JSON.stringify(canon({ webSha: w.head, profileHash: w.profile.hash, routesActs: ROUTES_ACTS, climbs }))}\n`;
writeFileSync(join(OUT, 'climbs.json'), text);
console.log(`oracle-core2-host: ${climbs.length} climbs, ${climbs.reduce((s, c) => s + c.nodes.length, 0)} nodes, ${(text.length / 1024).toFixed(0)} KB -> Unity/Assets/Tests/Oracle/core2/host/climbs.json`);
