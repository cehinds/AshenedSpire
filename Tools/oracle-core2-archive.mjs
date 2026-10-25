#!/usr/bin/env node
// Core 2 profile archive oracle (P-3a, docs/BUILD-LOG.md). Drives the SHIPPED web's archive drawer and the words the Profile screen puts on it:
//   src/engine/save.js                  createSaveManager(storage): the drawer (readArchiveIndex, writeArchiveEntry, archiveMeta, archive), listArchives, drawerNotices, exportArchive, driven through its public doors
//                                       (loadMeta of an unreadable profile, startNewProfile, loadRun of an unreadable run) over an in-memory storage and a clock this script holds
//   src/ui/screens/profileArchive.js    humanTime, humanSize, describe and humanReason (module-private, so their source is cut out of the file and run as it stands)
//   src/ui/models/ProfileWorkspaceModel.js profileWorkspaceView
//
//   node Tools/oracle-core2-archive.mjs [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/archive/, sorted keys; two runs are byte-identical):
//   drawer.json   { steps: [{ now, kind, save, reason, slot, listing, notices }], exports: [{ id, now, step, text }] }: each step puts bytes in the drawer at a moment the clock holds, with the drawer's listing and notices after it; the C# drawer replays the steps and the exports are what exportArchive said right after a step
//   words.json    { times, sizes, reasons, describes, workspace }: what the screen's helpers say, with the U+202F the browser puts before AM/PM written as a plain space (the Unity fonts carry no narrow no-break space)
//   index.json    the sha, the profile hash and the tallies
//
// The zone is UTC, so the times the helpers print do not depend on the machine.
process.env.TZ = 'UTC';
import { writeFileSync, mkdirSync, rmSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon } from './oracle-core2.mjs';

// The zone must be UTC before the first Date is made, so a run in another zone starts itself again in UTC.
if (new Date(0).getTimezoneOffset() !== 0) {
  execFileSync(process.execPath, [fileURLToPath(import.meta.url), ...process.argv.slice(2)], { env: { ...process.env, TZ: 'UTC' }, stdio: 'inherit' });
  process.exit(0);
}
const OUT = join(ORACLE_DIR, 'archive');
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);

// ---- the clock: Date.now() and new Date() read it, so ids and times are the ones the script chose
const RealDate = Date;
let NOW = RealDate.UTC(2026, 9, 24, 16, 38, 0, 123);
globalThis.Date = class extends RealDate {
  constructor(...a) { if (a.length === 0) super(NOW); else super(...a); }
  static now() { return NOW; }
};
const at = (ms) => { NOW = ms; };
const DAY = 24 * 60 * 60 * 1000;
const T0 = NOW;

const save = await load('src/engine/save.js');
const store = new Map();
const storage = { getItem: (k) => (store.has(k) ? store.get(k) : null), setItem: (k, v) => { store.set(k, String(v)); }, removeItem: (k) => { store.delete(k); } };
const saves = save.createSaveManager(storage);
const reg = w.registries;

const listing = () => saves.listArchives().map((e) => ({ id: e.id, kind: e.kind, slot: e.slot ?? null, reason: e.reason, at: e.at, bytes: e.bytes,
  count: saves.getArchive(e.id).count ?? null, lastSeenAt: saves.getArchive(e.id).lastSeenAt ?? null }));
const notices = () => saves.drawerNotices().map((n) => ({ at: n.at, kind: n.kind, id: n.id, was: n.was }));
const steps = [];
const record = (kind, saved, reason, slot) => steps.push({ now: NOW, kind, save: saved, reason, slot, listing: listing(), notices: notices() });

// An unreadable profile read again and again (a boot reads it many times): one entry, counted.
const exports = [];
const exportOf = (id) => exports.push({ id, now: NOW, step: steps.length - 1, text: saves.exportArchive(id) });
const corrupt = (text) => {
  storage.setItem(save.META_KEY, text);
  saves.loadMeta();
  record('meta', text, saves.profileStatus().reason, null);
};
at(T0); corrupt('{"progress": {"runs": 12, "wins"');
at(T0 + 1000); corrupt('{"progress": {"runs": 12, "wins"');
at(T0 + 2000); corrupt('{"progress": {"runs": 12, "wins"');
exportOf(listing()[0].id);
at(T0 + 3600000); corrupt('not json at all');
// A new profile keeps the old one: the reason is the fixed sentence.
const newProfile = (text) => {
  storage.setItem(save.META_KEY, text);
  saves.startNewProfile();
  record('meta', text, 'kept when you started a new profile', null);
};
at(T0 + 2 * 3600000); newProfile(JSON.stringify({ schemaVersion: 4, progress: { runs: 3, wins: 1 } }));
// Runs that cannot be read are set aside and their slot cleared.
const run = (slot, text) => {
  storage.setItem(save.runKey(slot), text);
  saves.loadRun(reg, slot);
  const st = saves.runStatus();
  record('run', text, st.reason, slot);
};
for (let i = 0; i < 15; i++) { at(T0 + DAY * (i + 1)); run((i % 3) + 1, `{"broken run ${i}`); }
exportOf(listing().find((e) => e.kind === 'run').id);
// Two hundred days on, the runs older than about six months age out; profiles do not.
at(T0 + DAY * 200); run(2, '{"late run');
// Profiles alone fill the drawer: the oldest are moved out to their own place with a notice, never deleted.
for (let i = 0; i < 30; i++) { at(T0 + DAY * 201 + i * 60000); newProfile(JSON.stringify({ schemaVersion: 4, progress: { runs: i, wins: i % 2 ? i : undefined } })); }

const drawer = { steps, exports };

// ---- the screen's own words, cut out of profileArchive.js and run as written
const source = readFileSync(join(WEB, 'src/ui/screens/profileArchive.js'), 'utf8');
const cut = (name) => {
  const start = source.indexOf(`function ${name}(`);
  if (start < 0) throw new Error(`oracle-core2-archive: function ${name} not found in profileArchive.js`);
  let depth = 0;
  for (let i = source.indexOf('{', start); i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1);
  }
  throw new Error(`oracle-core2-archive: function ${name} does not end`);
};
const helpers = new Function(`${cut('humanTime')}\n${cut('humanSize')}\n${cut('describe')}\n${cut('humanReason')}\nreturn { humanTime, humanSize, describe, humanReason };`)();
const plainSpace = (s) => (typeof s === 'string' ? s.replace(/ /g, ' ') : s);
const model = await load('src/ui/models/ProfileWorkspaceModel.js');

const times = [null, '', 'not a time', '2026-10-24T16:38:00.123Z', '2026-10-24T00:05:00.000Z', '2026-10-24T12:00:00.000Z', '2026-10-24T23:59:59.000Z', '2027-01-02T09:07:00.000Z', '2026-03-31T13:30:00.000Z']
  .map((iso) => ({ iso, text: plainSpace(helpers.humanTime(iso)) }));
const sizes = [0, 1, 812, 1023, 1024, 1535, 1536, 4096, 4608, 20000].map((bytes) => ({ bytes, text: helpers.humanSize(bytes) }));
const reasons = ['', 'Unexpected token } in JSON at position 41', 'profile could not be read: no valid copy of profile', 'run could not be read: no valid copy of run-1', 'profile schemaVersion 2 is older than this build (4) and has no migration',
  'content is from an older version', 'kept when you started a new profile', 'set aside when you restored meta-1-1', 'dangling id: card.x', 'something else entirely', 'Restored']
  .map((raw) => ({ raw, text: helpers.humanReason(raw) }));
const describes = [
  { entry: { kind: 'run', slot: 2 }, save: null }, { entry: { kind: 'run', slot: 0 }, save: null }, { entry: { kind: 'run' }, save: null },
  { entry: { kind: 'meta' }, save: '{"progress":{"runs":12,"wins":3}}' }, { entry: { kind: 'meta' }, save: '{"progress":{"runs":12}}' }, { entry: { kind: 'meta' }, save: '{"progress":{"wins":2}}' },
  { entry: { kind: 'meta' }, save: '{"progress":{"runs":0,"wins":0}}' }, { entry: { kind: 'meta' }, save: '{"progress":{}}' }, { entry: { kind: 'meta' }, save: '{"settings":{}}' }, { entry: { kind: 'meta' }, save: 'not json' }, { entry: { kind: 'meta' }, save: '[]' },
].map(({ entry, save: saved }) => ({ entry, save: saved, text: helpers.describe({ ...entry, id: 'x' }, { getArchive: () => ({ save: saved }) }) }));
const mk = (kind, n) => Array.from({ length: n }, (_, i) => ({ id: `${kind}${i}`, kind }));
const workspace = [[[], null], [mk('meta', 2), null], [mk('run', 3), null], [[...mk('meta', 1), ...mk('run', 2)], 'run'], [mk('meta', 2), 'run'], [mk('run', 2), 'meta'], [mk('run', 2), 'bogus']]
  .map(([archives, current]) => ({ archives: archives.map((a) => a.kind), current, view: model.profileWorkspaceView(archives, current) }))
  .map((r) => ({ archives: r.archives, current: r.current, view: { categories: r.view.categories, current: r.view.current, empty: r.view.empty, entries: r.view.entries.length } }));

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const write = (name, data) => writeFileSync(join(OUT, name), `${JSON.stringify(canon(data))}\n`);
write('drawer.json', drawer);
write('words.json', { times, sizes, reasons, describes, workspace });
write('index.json', { source: 'engine/save.js (the drawer), ui/screens/profileArchive.js (humanTime, humanSize, describe, humanReason), ui/models/ProfileWorkspaceModel.js', webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash,
  tally: { steps: steps.length, finalEntries: listing().length, finalNotices: notices().length, exports: exports.length, times: times.length, sizes: sizes.length, reasons: reasons.length, describes: describes.length, workspace: workspace.length } });
console.log(`oracle-core2-archive: ${steps.length} steps, ${listing().length} entries and ${notices().length} notices at the end -> Unity/Assets/Tests/Oracle/core2/archive`);
