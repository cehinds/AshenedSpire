#!/usr/bin/env node
// Core 2 Settings oracle (P-3a, docs/BUILD-LOG.md). Reads the SHIPPED web's Settings page for the configured `default` profile:
//   src/ui/screens/settings.js                  generalGroups (the General and Accessibility groups and their topics), visibleAdvancedGroups
//                                               (the Advanced sections of a release build and of a build with Developer tools on) and the rows each topic holds
//   src/ui/models/AdvancedSettingsGroups.js     advancedSubgroups (the topics of an Advanced section)
//   src/ui/models/SettingsCapabilitiesModel.js  settingsRowAvailable (the rows the profile keeps)
//
//   node Tools/oracle-core2-settings.mjs [--source <web checkout>]
//
// Files (Unity/Assets/Tests/Oracle/core2/settings/, sorted keys; two runs are byte-identical):
//   page.json    the page: per tab the groups, per group the topics, per topic the rows (key, label, type, default, choices, bounds); the Advanced sections for a
//                release build (`release`) and with Developer tools on (`developer`)
//   index.json   the sha, the profile hash and the tallies
import { writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadWeb, ORACLE_DIR, WEB, canon } from './oracle-core2.mjs';

const OUT = join(ORACLE_DIR, 'settings');
const w = await loadWeb();
const load = (rel) => import(pathToFileURL(join(WEB, rel)).href);
const [settings, groups, capabilities] = await Promise.all([
  load('src/ui/screens/settings.js'), load('src/ui/models/AdvancedSettingsGroups.js'), load('src/ui/models/SettingsCapabilitiesModel.js'),
]);
const profile = w.profile;

const row = (r) => ({
  key: r.key, label: r.label, type: r.type ?? 'toggle', def: r.def ?? null, note: r.note ?? '', choices: r.choices ?? null, choiceLabels: r.choiceLabels ?? null,
  min: r.min ?? null, max: r.max ?? null, step: r.step ?? null, unit: r.unit ?? null, dropdown: r.dropdown === true, retired: r.retired === true, requires: r.requires ?? null,
  font: r.font ?? null, lore: r.lore === true,
});

const tally = { general: 0, accessibility: 0, advanced: { release: 0, developer: 0 }, rows: 0 };
const general = {};
for (const cat of ['General', 'Accessibility']) {
  const names = cat === 'Accessibility' ? ['Accessibility'] : settings.GENERAL_GROUPS;
  general[cat] = names.map((name) => {
    const topics = settings.generalGroups(name, profile, profile);
    return { id: name, topics: [...topics].map(([label, rows]) => ({ label, rows: rows.map(row) })) };
  });
}

const advancedRows = settings.categoryHandler('Advanced').rows.filter((r) => capabilities.settingsRowAvailable(r, profile));
const advanced = {};
for (const [channel, debug] of [['release', false], ['developer', true]]) {
  advanced[channel] = settings.visibleAdvancedGroups(debug, profile).map((group) => {
    const mounted = ['Changelog', 'About', 'Sync'].includes(group.id);
    const topics = mounted ? [] : groups.advancedSubgroups(advancedRows, group.id)
      .filter((sub) => group.id !== 'Opening' || !sub.rows.some((r) => r.prologuePath?.[0] === 'scenes'))
      .map((sub) => ({ id: sub.id, label: sub.label, rows: sub.rows.map(row) }));
    tally.advanced[channel]++;
    tally.rows += topics.reduce((n, t) => n + t.rows.length, 0);
    return { id: group.id, label: group.label, tip: group.tip, mounted, topics };
  });
}
tally.general = general.General.length;
tally.accessibility = general.Accessibility.length;

mkdirSync(OUT, { recursive: true });
for (const f of readdirSync(OUT)) if (f.endsWith('.json')) rmSync(join(OUT, f));
const write = (name, data) => writeFileSync(join(OUT, name), `${JSON.stringify(canon(data))}\n`);
write('page.json', { tabs: settings.CATEGORY_ORDER, general, advanced, releaseGroups: settings.RELEASE_ADVANCED_GROUP_IDS, gameRulesLabel: settings.generalGroups('Game rules', profile, profile).keys().next().value });
write('index.json', { source: 'ui/screens/settings.js, ui/models/AdvancedSettingsGroups.js, ui/models/SettingsCapabilitiesModel.js (configured profile default)', webSha: w.head, profileId: w.profile.id, profileHash: w.profile.hash, tally });
console.log(`oracle-core2-settings: General ${tally.general} groups, Accessibility ${tally.accessibility}, Advanced ${tally.advanced.release} release / ${tally.advanced.developer} developer sections, ${tally.rows} Advanced rows -> Unity/Assets/Tests/Oracle/core2/settings`);
