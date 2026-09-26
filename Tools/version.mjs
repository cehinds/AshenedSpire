#!/usr/bin/env node
// Ashen Spire version incrementer — the ONLY writer of the version (docs/design/10 §2).
// Scheme: E.F.S.P = <epic release>.<feature>.<user story>.<patch>
//
//   node Tools/version.mjs current
//   node Tools/version.mjs next  <epic|feature|story|patch> [--count n]
//   node Tools/version.mjs bump  <epic|feature|story|patch> [--count n] [--note "text"] [--stories us-0.1,us-0.2]
//   node Tools/version.mjs check <base-ref> [--head-branch name] [--base-branch name]
//   node Tools/version.mjs set <E.F.S.P> --reason "..."   (corrections only)
// Epic bumps are owner-only (ASHEN_OWNER_RELEASE=1).
//
// Files touched by `bump`: VERSION, CHANGELOG.md, Unity/ProjectSettings/ProjectSettings.asset
// (bundleVersion + AndroidBundleVersionCode) when present, build-info.json.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const PARTS = ['epic', 'feature', 'story', 'patch'];
const FORMAT = /^\d+\.\d+\.\d+\.\d+$/;
const PATHS = {
  version: join(ROOT, 'VERSION'),
  changelog: join(ROOT, 'CHANGELOG.md'),
  projectSettings: join(ROOT, 'Unity', 'ProjectSettings', 'ProjectSettings.asset'),
  buildInfo: join(ROOT, 'build-info.json'),
};
// Merge kind → allowed bump parts (10 §2 "Rules for check"). null = versions must be equal.
const ALLOWED = [
  { head: /^feature\/[^/]+\/main$/, base: /^dev$/, parts: ['story', 'feature'] },
  { head: /^feature\/[^/]+\/us-[\d.]+$/, base: /^feature\/[^/]+\/main$/, parts: null },
  { head: /^(fix|chore)\//, base: /^(dev|test)$/, parts: ['patch'] },
  { head: /^dev$/, base: /^test$/, parts: null },
  { head: /^test$/, base: /^main$/, parts: null },
  { head: /^dev$/, base: /^dev$/, parts: ['feature', 'story', 'patch'] },
];

function fail(msg) { console.error(`version: ${msg}`); process.exit(1); }
export function parse(text) {
  const v = String(text).trim();
  if (!FORMAT.test(v)) fail(`bad version "${v}" (expected E.F.S.P)`);
  return v.split('.').map(Number);
}
export function format(parts) { return parts.join('.'); }
export function compare(a, b) {
  for (let i = 0; i < 4; i++) if (a[i] !== b[i]) return a[i] < b[i] ? -1 : 1;
  return 0;
}
export function next(parts, part, count = 1) {
  const i = PARTS.indexOf(part);
  if (i < 0) fail(`unknown part "${part}" (use ${PARTS.join('|')})`);
  const out = parts.slice();
  out[i] += part === 'story' ? count : 1;
  for (let j = i + 1; j < 4; j++) out[j] = 0;
  return out;
}
export function bumpedPart(from, to) {
  for (let i = 0; i < 4; i++) if (from[i] !== to[i]) return PARTS[i];
  return null;
}
function readVersion() {
  if (!existsSync(PATHS.version)) fail('VERSION file missing');
  return parse(readFileSync(PATHS.version, 'utf8'));
}
function arg(name, fallback = null) {
  const i = process.argv.indexOf(name);
  return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
}
function today() { return new Date().toISOString().slice(0, 10); }
function git(args) {
  try { return execFileSync('git', args, { cwd: ROOT, encoding: 'utf8' }).trim(); } catch { return ''; }
}

function writeAll(version, note, stories) {
  writeFileSync(PATHS.version, `${version}\n`);
  const log = existsSync(PATHS.changelog) ? readFileSync(PATHS.changelog, 'utf8') : '# Changelog\n';
  const lines = [`## [${version}] — ${today()}`, ''];
  if (stories.length) lines.push(`Stories: ${stories.join(', ')}`, '');
  if (note) lines.push(`- ${note}`, '');
  const marker = log.indexOf('\n## [');
  const updated = marker >= 0
    ? `${log.slice(0, marker + 1)}${lines.join('\n')}\n${log.slice(marker + 1)}`
    : `${log.trimEnd()}\n\n${lines.join('\n')}\n`;
  writeFileSync(PATHS.changelog, updated);
  if (existsSync(PATHS.projectSettings)) {
    const [e, f, s, p] = parse(version);
    const code = e * 1000000 + f * 10000 + s * 100 + p;
    let ps = readFileSync(PATHS.projectSettings, 'utf8');
    ps = ps.replace(/^(\s*bundleVersion:).*$/m, `$1 ${version}`);
    ps = ps.replace(/^(\s*AndroidBundleVersionCode:).*$/m, `$1 ${code}`);
    writeFileSync(PATHS.projectSettings, ps);
  }
  const info = { version, sha: git(['rev-parse', '--short', 'HEAD']), date: today(),
    aiDisclosure: 'Ashen Spire was created with generative AI. Its code, art, music, sound, text and design were produced with AI tools under human direction.' };
  writeFileSync(PATHS.buildInfo, `${JSON.stringify(info, null, 2)}\n`);
}

function check(baseRef) {
  const head = readVersion();
  const baseText = git(['show', `${baseRef}:VERSION`]);
  if (!baseText) { console.log(`version: no VERSION on ${baseRef}; head ${format(head)} accepted`); return; }
  const base = parse(baseText);
  const headBranch = arg('--head-branch', process.env.GITHUB_HEAD_REF || git(['rev-parse', '--abbrev-ref', 'HEAD']));
  const baseBranch = arg('--base-branch', process.env.GITHUB_BASE_REF || baseRef.replace(/^origin\//, ''));
  const rule = ALLOWED.find(r => r.head.test(headBranch) && r.base.test(baseBranch));
  const cmp = compare(head, base);
  if (cmp < 0) fail(`version went backwards: ${format(base)} → ${format(head)}`);
  const part = bumpedPart(base, head);
  if (rule && rule.parts === null && cmp !== 0) fail(`${headBranch} → ${baseBranch} must not bump (${format(base)} → ${format(head)})`);
  if (rule && rule.parts && cmp === 0) fail(`${headBranch} → ${baseBranch} must bump one of ${rule.parts.join('|')}`);
  if (rule && rule.parts && !rule.parts.includes(part)) fail(`${headBranch} → ${baseBranch} bumped "${part}", allowed ${rule.parts.join('|')}`);
  if (cmp > 0) {
    const expected = next(base, part, part === 'story' ? head[2] - base[2] : 1);
    if (compare(expected, head) !== 0) fail(`lower parts must reset: expected ${format(expected)}, got ${format(head)}`);
    const log = existsSync(PATHS.changelog) ? readFileSync(PATHS.changelog, 'utf8') : '';
    if (!log.includes(`## [${format(head)}]`)) fail(`CHANGELOG.md has no entry for ${format(head)}`);
  }
  console.log(`version: ok ${format(base)} → ${format(head)}${part ? ` (${part})` : ''} [${headBranch} → ${baseBranch}]`);
}

const [cmd, part] = process.argv.slice(2);
const count = Number(arg('--count', '1'));
if (cmd === 'current') console.log(format(readVersion()));
else if (cmd === 'next') console.log(format(next(readVersion(), part, count)));
else if (cmd === 'bump') {
  // Owner ruling (2026-09-26): nothing is 1.x until the OWNER declares a release. Phase completions are
  // promotions (dev → test → main) with the current version, never an epic bump.
  if (part === 'epic' && process.env.ASHEN_OWNER_RELEASE !== '1') fail('epic bumps are owner-only: set ASHEN_OWNER_RELEASE=1 when the owner declares a release');
  const v = format(next(readVersion(), part, count));
  const stories = (arg('--stories', '') || '').split(',').filter(Boolean);
  writeAll(v, arg('--note'), stories);
  console.log(v);
} else if (cmd === 'set') {
  // Corrections only (e.g. an owner-ordered renumber). Requires --reason; recorded in the CHANGELOG.
  const reason = arg('--reason');
  if (!reason) fail('set requires --reason');
  const v = format(parse(part));
  writeAll(v, `Version correction: ${reason}`, []);
  console.log(v);
} else if (cmd === 'check') check(part || 'origin/dev');
else if (cmd === 'selftest') {
  const t = (a, b, m) => { if (JSON.stringify(a) !== JSON.stringify(b)) fail(`selftest ${m}: ${a} != ${b}`); };
  t(format(next([0, 0, 1, 0], 'story')), '0.0.2.0', 'story');
  t(format(next([0, 0, 3, 1], 'story', 2)), '0.0.5.0', 'story count');
  t(format(next([0, 0, 3, 1], 'feature')), '0.1.0.0', 'feature');
  t(format(next([0, 4, 3, 1], 'epic')), '1.0.0.0', 'epic');
  t(format(next([1, 2, 3, 4], 'patch')), '1.2.3.5', 'patch');
  t(bumpedPart([0, 1, 0, 0], [0, 1, 1, 0]), 'story', 'bumpedPart');
  console.log('version: selftest ok');
} else {
  console.log('usage: node Tools/version.mjs <current|next|bump|check|selftest> ...');
  process.exit(cmd ? 1 : 0);
}
