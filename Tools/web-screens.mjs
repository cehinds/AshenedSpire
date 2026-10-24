#!/usr/bin/env node
// Tools/web-screens.mjs - photograph the WEB game (the visual reference) so the Unity UI can be compared to it.
//
//   node Tools/web-screens.mjs                         capture everything at 1920x1080 and 390x844
//   node Tools/web-screens.mjs --only combat,reward    only scenarios whose id starts with one of these
//   node Tools/web-screens.mjs --sizes 1920x1080       one size only
//   node Tools/web-screens.mjs --out docs/qa/web-screens
//   node Tools/web-screens.mjs --web /path/to/ashenspire   web checkout (default $WEB_REPO or /home/user/cehinds/ashenspire)
//   node Tools/web-screens.mjs --list                  print the scenario ids and exit
//
// HOW THE WEB GAME IS SERVED. The web repo (pinned b8cfe4fac) serves itself with `node tools/serve.mjs` (a zero
// dependency static server, exported as serve({root, port, open, quiet})); its art, fonts and music live in an
// external release (art-release.json -> hd-assets-v23) that `node tools/fetch-art.mjs` unpacks into
// <root>/.art-cache. The web repo is read-only for this work, so this script MIRRORS it (without .git, docs,
// tests, pose-studio ...) into a scratch directory, runs fetch-art there (public GitHub release, ~500 MB, cached
// under $WEB_SCRATCH between runs) and serves the mirror. Nothing is written to the web repo.
//
// HOW IT IS DRIVEN. Playwright + Chromium (require('playwright'), no browser install). Core 2 is the default
// ruleset (`gameProfile=core` on every URL). Screens are reached two ways:
//   1. the web's own screenshot hooks (`?shot=<state>` with `shotSeed`, `shotReward`, `shotAt`, `shotWalk`,
//      `shotRestState`, `shotEvent`, `shotReaction`, `shotEnemyContext`, `shotHand` ... see src/main.js
//      "Dev screenshot hook"). A ?shot boot runs on memory storage, so it never touches a save and is fully
//      reproducible. This is state injection; it is the web's documented QA door, not a patch.
//   2. real input on top of a hook (click a card, hover an enemy, press D for the deck ...), and one real
//      end-to-end flow (startup gate -> title -> slots -> creation -> prologue -> first map) with the seed
//      typed into the creation form.
// Fixed seed everywhere: SEED below. Output: <out>/<screen>-<WxH>.jpg (JPEG q80, the PNGs were too large for a
// 25 MB budget) plus <out>/manifest.json (what was captured, what failed).
import { createRequire } from 'node:module';
import { existsSync, mkdirSync, mkdtempSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import os from 'node:os';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, '..');
const argv = process.argv.slice(2);
const arg = (name, dflt) => { const i = argv.indexOf(name); return i >= 0 && argv[i + 1] ? argv[i + 1] : dflt; };
const OUT = resolve(REPO, arg('--out', 'docs/qa/web-screens'));
const WEB = resolve(arg('--web', process.env.WEB_REPO || '/home/user/cehinds/ashenspire'));
const ONLY = arg('--only', '').split(',').filter(Boolean);
const SIZES = arg('--sizes', '1920x1080,390x844').split(',').map((s) => { const [w, h] = s.split('x').map(Number); return { w, h, tag: `${w}x${h}`, phone: w < 600 }; });
const SHARD = arg('--shard', '0/1').split('/').map(Number); // i/n: run every n-th scenario (parallel runs share --out)
const SEED = 'ASHEN1';
const QUALITY = 80;

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------------------------------------------------
// Scenarios. `url` is the query after index.html? (the profile is added); `run(h)` drives the page.
// h.snap(name) writes <name>-<size>.jpg; h.click(sel|text); h.key(k); h.eval(fn); h.wait(ms).
// ---------------------------------------------------------------------------------------------------------
const shot = (state, extra = '') => `shot=${state}&shotSeed=${SEED}${extra}`;
const NOONE = '&shotSettings=' + encodeURIComponent(JSON.stringify({ oneTapPlay: false }));

// The enemy/player bodies sit under transparent hit-boxes, so real input goes through absolute mouse coordinates
// taken from the sprite's own box (Playwright's actionability checks refuse a covered element).
async function spot(loc, fy = 0.55) {
  const b = await loc.boundingBox();
  if (!b) throw new Error('no box');
  return { x: b.x + b.width / 2, y: b.y + b.height * fy };
}
async function hoverOn(h, loc, fy) { const p = await spot(loc, fy); await h.page.mouse.move(p.x - 6, p.y - 6); await h.page.mouse.move(p.x, p.y, { steps: 4 }); }
async function clickOn(h, loc, fy) { const p = await spot(loc, fy); await h.page.mouse.move(p.x, p.y, { steps: 3 }); await h.page.mouse.down(); await h.page.mouse.up(); }
const enemySprite = (h, i) => h.page.locator('.combatant.enemy .sprite').nth(i);
async function combatPlay(h, cardName, enemyIndex = 0) {
  // select a hand card by its name, then click an enemy body (the web's own two-step: select, then target)
  await clickOn(h, h.page.locator(`.hand .card:has-text("${cardName}")`).first(), 0.45);
  await h.wait(500);
  await clickOn(h, enemySprite(h, enemyIndex));
  await h.wait(900);
}

export const SCENARIOS = [
  // ---- boot / menus (real flow, fresh profile) -----------------------------------------------------------
  { id: 'flow', url: '', run: async (h) => {
      await h.snap('flow-01-startup-gate');
      await h.key('Enter'); await h.wait(5000);
      await h.snap('flow-02-title-no-save');
      await h.click('#new-game'); await h.wait(900);
      await h.snap('flow-03-slots');
      await h.click('.title-modal-continue'); await h.wait(900);
      await h.snap('flow-04-slot-confirm');
      await h.click('.title-load-review-confirm'); await h.wait(1800);
      await h.snap('flow-05-creation-class');
      await h.click('#cz-next'); await h.wait(1200);
      await h.page.fill('#seed-input', SEED); await h.wait(300);
      await h.snap('flow-06-creation-review');
      await h.click('#cz-start'); await h.wait(2500);
      await h.snap('flow-07-prologue');
      await h.click('Skip opening'); await h.wait(2500);
      await h.snap('flow-08-first-map');
      await h.page.locator('.map-node.reachable').first().click({ force: true }); await h.wait(900);
      await h.snap('flow-09-map-node-selected');
      await h.click('#map-enter'); await h.wait(3500);
      await h.snap('flow-10-first-combat');
    } },
  { id: 'startup', url: shot('startup'), run: (h) => h.snap('startup') },
  { id: 'title', url: shot('title'), run: async (h) => {
      await h.snap('title');
      await h.click('#settings'); await h.wait(1200); await h.snap('title-settings');
    } },
  { id: 'prologue', url: shot('prologue'), run: async (h) => {
      await h.wait(1200); await h.snap('prologue-1');
      await h.click('Continue'); await h.wait(1500); await h.snap('prologue-2');
    } },
  { id: 'customize', url: shot('customize'), run: async (h) => { await h.snap('class-select'); } },

  // ---- act map -------------------------------------------------------------------------------------------
  { id: 'map', url: shot('map'), run: async (h) => {
      await h.snap('map-start');
      await h.click('#map-legend'); await h.wait(700); await h.snap('map-legend');
      await h.key('Escape'); await h.wait(400);
      await h.click('.run-potions-btn'); await h.wait(700); await h.snap('map-potions');
      await h.key('Escape'); await h.wait(400);
      await h.key('d'); await h.wait(1000); await h.snap('map-deck');
      await h.key('Escape'); await h.wait(400);
      await h.key('r'); await h.wait(1200); await h.snap('map-equipment');
      await h.key('Escape'); await h.wait(400);
      await h.key('t'); await h.wait(1000); await h.snap('map-stats');
      await h.key('Escape'); await h.wait(400);
      await h.key('m'); await h.wait(1000); await h.snap('map-pause-menu');
    } },
  { id: 'map-walk', url: shot('map', '&shotWalk=3&shotCarried=crimsonFlask,azureFlask'), run: async (h) => {
      await h.snap('map-trail');
      await h.page.locator('.map-node.reachable').first().click({ force: true }); await h.wait(900);
      await h.snap('map-trail-selected');
    } },
  { id: 'map-mid', url: shot('map', '&shotAt=floor:4'), run: (h) => h.snap('map-midclimb') },
  { id: 'atlas', url: shot('atlas'), run: async (h) => { await h.snap('atlas-world'); } },

  // ---- combat --------------------------------------------------------------------------------------------
  { id: 'combat', url: shot('combat') + NOONE, ready: 'combat', run: async (h) => {
      // Seed ASHEN1 opens against one Ash Revenant (20 HP); Sundering Art kills it, so the lethal blow comes last.
      await h.snap('combat');
      const hand = h.page.locator('.hand .card');
      await h.step(async () => {
        await clickOn(h, hand.nth(await hand.count() - 1), 0.45); await h.wait(800);
        await h.snap('combat-card-selected');
        await hoverOn(h, enemySprite(h, 0)); await h.wait(1000);
        await h.snap('combat-target-hover');
        await clickOn(h, h.page.locator('.battlefield, .field, .board').first(), 0.9).catch(() => {}); await h.wait(500);
      });
      await h.step(async () => {
        await hoverOn(h, h.page.locator('.combatant.enemy .intent').first(), 0.5); await h.wait(1000);
        await h.snap('combat-intent-tooltip');
        await h.page.mouse.move(5, 5); await h.wait(300);
      });
      await h.step(async () => {
        await h.page.locator('.hand .card .card-info-button').first().click({ force: true }); await h.wait(1200);
        await h.snap('combat-card-inspect'); await h.key('Escape'); await h.wait(500);
      });
      await h.step(async () => { const lg = h.page.locator('button:has-text("Combat log")').first(); await lg.click({ force: true }); await h.wait(900); await h.snap('combat-log-open'); await h.page.getByRole('button', { name: 'Close', exact: true }).first().click({ force: true }); await h.wait(700); });
      await h.step(async () => { await h.click('.combat-potions'); await h.wait(900); await h.snap('combat-potions'); await h.key('Escape'); await h.wait(500); });
      await h.step(async () => { await h.click('.pile.spent'); await h.wait(900); await h.snap('combat-discard-pile'); await h.key('Escape'); await h.wait(500); });
      await h.step(async () => { await h.click('.pile.draw'); await h.wait(900); await h.snap('combat-draw-pile'); await h.key('Escape'); await h.wait(500); });
      await h.step(async () => { await hoverOn(h, h.page.locator('.combatant.player .sprite').first()); await h.wait(1000); await h.snap('combat-player-hover'); await h.page.mouse.move(5, 5); });
      await h.step(async () => { await h.click('#combat-menu'); await h.wait(1000); await h.snap('combat-quick-menu'); await h.key('Escape'); await h.wait(400); });
      await h.step(async () => {
        await combatPlay(h, 'Sundering', 0);
        await h.snap('combat-hit-feedback'); await h.wait(2500);
        await h.snap('combat-victory-card-reward'); await h.wait(500);
        await h.click('Confirm'); await h.wait(2500); await h.snap('combat-after-reward');
      });
    } },
  { id: 'combat-win', url: shot('combat') + NOONE, ready: 'combat', run: async (h) => {
      // Sundering Art (42) kills the lone Ash Revenant (20 HP): the web answers with a card-pick overlay on the dimmed field
      await combatPlay(h, 'Sundering', 0);
      await h.wait(3500); await h.snap('combat-victory-card-pick');
      await h.step(async () => { await h.click('Confirm'); await h.wait(3000); await h.snap('combat-victory-after-pick'); });
    } },
  { id: 'combat-turn', url: shot('combat', '&shotEnemyCount=3') + NOONE, ready: 'combat', run: async (h) => {
      // end the turn (End Turn is a hold control: hold E) and photograph the enemy phase and the next draw
      await h.snap('combat-three-enemies');
      await h.page.keyboard.down('e'); await h.wait(1500); await h.snap('combat-end-turn-hold'); await h.page.keyboard.up('e'); await h.wait(900);
      await h.snap('combat-enemy-turn'); await h.wait(2000); await h.snap('combat-enemy-turn-2');
      await h.wait(2000); await h.snap('combat-enemy-turn-3');
      await h.wait(4000); await h.snap('combat-next-player-turn');
    } },
  { id: 'combat-reaction', url: shot('combat', '&shotReaction=counter') + NOONE, ready: 'combat', run: async (h) => {
      await h.snap('combat-reaction-hand');
      await h.step(async () => { await clickOn(h, h.page.locator('.hand .card:has-text("Counter"), .hand .card:has-text("Guard")').first(), 0.45); await h.wait(900); await h.snap('combat-reaction-armed'); });
      await h.page.keyboard.down('e'); await h.wait(1400); await h.page.keyboard.up('e'); await h.wait(1200);
      await h.snap('combat-reaction-offer'); await h.wait(2500); await h.snap('combat-reaction-after');
    } },
  { id: 'combat-status', url: shot('combat') + NOONE, ready: 'combat', run: async (h) => {
      // state injection through the web's own fixture hook (window.__combat + __renderCombatForShot, as tests/fixcore-combat-ui.browser.mjs does)
      await h.page.evaluate(() => {
        const c = window.__combat; const e = c.enemies[0];
        e.persistentPoise = { value: 12, max: 12 }; e.persistentWard = { value: 8, max: 8 }; e.block = 3;
        e.statuses = { ...e.statuses, weak: { stacks: 2 }, vulnerable: { stacks: 3 }, staggered: { stacks: 1 }, evade: { stacks: 2 } };
        c.player.block = 8; c.player.statuses = { ...c.player.statuses, strength: { stacks: 2 }, frail: { stacks: 1 } };
        window.__renderCombatForShot();
      });
      await h.wait(1200); await h.snap('combat-statuses-block');
      await hoverOn(h, h.page.locator('.combatant.enemy .sprite').first()); await h.wait(1200); await h.snap('combat-statuses-target-hud');
    } },
  { id: 'combat-crowd', url: shot('combat', '&shotEnemyCount=4') + NOONE, ready: 'combat', run: (h) => h.snap('combat-four-enemies') },
  { id: 'combat-defeat', url: shot('combat', '&shotMaxHp=4') + NOONE, ready: 'combat', run: async (h) => {
      for (let i = 0; i < 14; i++) {
        if (await h.page.getByText(/you perished|the climb ends/i).count()) break;
        if (await h.page.getByText('Respond to the incoming action').count()) { await h.snap('combat-defeat-reaction-offer'); await h.page.getByRole('button', { name: 'Back', exact: true }).first().click({ force: true }).catch(() => {}); await h.wait(2200); continue; }
        await h.page.keyboard.down('e'); await h.wait(1400); await h.page.keyboard.up('e'); await h.wait(2500);
      }
      await h.wait(1500); await h.snap('combat-defeat');
    } },
  { id: 'fx', url: shot('fx'), ready: 'combat', run: (h) => h.snap('combat-fx-frozen') },
  { id: 'boss', url: shot('boss'), run: async (h) => { await h.wait(1200); await h.snap('boss-intro'); } },

  // ---- run screens ---------------------------------------------------------------------------------------
  { id: 'reward', url: shot('reward'), run: async (h) => {
      await h.snap('reward-full');
      await h.page.locator('.reward-row:has-text("Card"), button:has-text("Card")').first().click().catch(() => {}); await h.wait(1200);
      await h.snap('reward-card-pick');
    } },
  { id: 'reward-level', url: shot('reward', '&shotReward=level'), run: (h) => h.snap('reward-level-up') },
  { id: 'reward-boss', url: shot('reward', '&shotReward=bossRelics'), run: (h) => h.snap('reward-boss-relics') },
  { id: 'reward-draft', url: shot('reward', '&shotReward=draft'), run: (h) => h.snap('reward-skill-draft') },
  { id: 'reward-empty', url: shot('reward', '&shotReward=empty'), run: (h) => h.snap('reward-empty') },
  { id: 'rest', url: shot('rest'), run: async (h) => { await h.snap('rest'); } },
  { id: 'rest-wounded', url: shot('rest', '&shotRestState=wounded'), run: async (h) => {
      await h.snap('rest-wounded');
    } },
  { id: 'smith', url: shot('smith'), run: async (h) => {
      await h.click('#smith-opt'); await h.wait(1500); await h.snap('smith-modal');
      await h.page.locator('.smith-modal button, .modal button').filter({ hasText: /upgrade|select|choose|greatsword/i }).first().click().catch(() => {});
      await h.wait(1000); await h.snap('smith-modal-selected');
    } },
  { id: 'shop', url: shot('shop'), run: async (h) => {
      await h.snap('shop');
      await h.click('Inspect'); await h.wait(1000); await h.snap('shop-inspect'); await h.key('Escape'); await h.wait(500);
      await h.page.getByText('RELICS', { exact: true }).first().click().catch(() => {}); await h.wait(900); await h.snap('shop-relics');
      await h.page.getByText('FLASKS', { exact: true }).first().click().catch(() => {}); await h.wait(900); await h.snap('shop-flasks');
      await h.page.locator('button:has-text("Buy")').first().click().catch(() => {}); await h.wait(900); await h.snap('shop-buy-press');
    } },
  { id: 'blacksmith', url: shot('blacksmith'), run: (h) => h.snap('blacksmith') },
  { id: 'master', url: shot('master'), run: (h) => h.snap('master-merchant') },
  { id: 'event', url: shot('event'), run: async (h) => {
      await h.snap('event-dialogue');
      await h.click('Continue'); await h.wait(1200); await h.snap('event-dialogue-2');
      for (let i = 0; i < 6 && !(await h.page.locator('.ev-choice').count()); i++) { await h.click('Continue').catch(() => {}); await h.wait(900); }
      await h.snap('event-choices');
      await h.page.locator('.ev-choice').first().click().catch(() => {}); await h.wait(1500); await h.snap('event-result');
    } },
  { id: 'event-pilgrim', url: shot('event', '&shotEvent=weepingPilgrim'), run: async (h) => {
      for (let i = 0; i < 6 && !(await h.page.locator('.ev-choice').count()); i++) { await h.click('Continue').catch(() => {}); await h.wait(900); }
      await h.snap('event-pilgrim-choices');
    } },

  // ---- run end / meta ------------------------------------------------------------------------------------
  { id: 'victory', url: shot('victory'), run: (h) => h.snap('run-end-victory') },
  { id: 'death', url: shot('death'), run: (h) => h.snap('run-end-defeat') },
  { id: 'history', url: shot('history'), run: (h) => h.snap('run-history') },
  { id: 'compendium', url: shot('compendium'), run: (h) => h.snap('compendium') },
  { id: 'profile', url: shot('profile'), run: (h) => h.snap('profile') },
  { id: 'settings', url: shot('settings'), run: async (h) => {
      await h.snap('settings-advanced');
      await h.page.getByRole('button', { name: 'General' }).first().click().catch(() => {}); await h.wait(900); await h.snap('settings-general');
      await h.page.getByRole('button', { name: 'Accessibility' }).first().click().catch(() => {}); await h.wait(900); await h.snap('settings-accessibility');
    } },
  { id: 'about', url: shot('about'), run: (h) => h.snap('about') },
];

if (argv.includes('--list')) { console.log(SCENARIOS.map((s) => s.id).join('\n')); process.exit(0); }

// ---------------------------------------------------------------------------------------------------------
// Web root preparation + server
// ---------------------------------------------------------------------------------------------------------
function artCacheReady(root) { const d = join(root, '.art-cache'); return existsSync(d) && readdirSync(d).length > 0; }

function prepareRoot() {
  if (!existsSync(join(WEB, 'tools', 'serve.mjs'))) throw new Error(`not a web checkout: ${WEB}`);
  if (artCacheReady(WEB)) return WEB;
  const scratch = process.env.WEB_SCRATCH || join(os.tmpdir(), 'ashenspire-web-mirror');
  const mirror = join(scratch, 'root');
  if (!artCacheReady(mirror)) {
    mkdirSync(mirror, { recursive: true });
    console.log(`mirroring ${WEB} -> ${mirror} (no .git/docs/tests)`);
    const tar = spawnSync('bash', ['-c', `cd "${WEB}" && tar cf - --exclude=./.git --exclude=./docs --exclude=./pose-studio --exclude=./tests --exclude=./dist --exclude=./workbench --exclude=./tools/hud-component-studio . | tar xf - -C "${mirror}"`], { stdio: 'inherit' });
    if (tar.status !== 0) throw new Error('mirror failed');
    console.log('fetching the pinned art release (public GitHub release; ~500 MB, cached)');
    // a stale/expired GITHUB_TOKEN makes fetch-art warn but fall back to the public URL
    const f = spawnSync('node', ['tools/fetch-art.mjs', '--pack', 'light,common,high'], { cwd: mirror, stdio: 'inherit' });
    if (f.status !== 0) throw new Error('fetch-art failed');
  }
  return mirror;
}

const root = prepareRoot();
const { serve } = await import(pathToFileURL(join(root, 'tools', 'serve.mjs')).href);
const server = await serve({ root, port: 0, open: false, quiet: true });
const base = `${server.url.replace(/\/$/, '')}/`;
console.log(`serving ${root} at ${base}`);

// ---------------------------------------------------------------------------------------------------------
// Runner
// ---------------------------------------------------------------------------------------------------------
mkdirSync(OUT, { recursive: true });
const manifest = { web: root, seed: SEED, profile: 'core', captured: [], failed: [] };
const browser = await chromium.launch();

async function settle(page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
    await Promise.all([...document.images].filter((i) => !i.complete).map((i) => new Promise((r) => { i.onload = i.onerror = r; setTimeout(r, 3000); })));
  }).catch(() => {});
}

for (const size of SIZES) {
  for (const sc of SCENARIOS) {
    if (ONLY.length && !ONLY.some((o) => sc.id.startsWith(o))) continue;
    if (SCENARIOS.indexOf(sc) % SHARD[1] !== SHARD[0]) continue;
    const ctx = await browser.newContext({ viewport: { width: size.w, height: size.h }, deviceScaleFactor: 1, isMobile: size.phone, hasTouch: size.phone });
    const page = await ctx.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    const h = {
      page,
      wait: sleep,
      // one failing sub-step must not lose the rest of a long scenario
      step: async (fn) => {
        try { await fn(); } catch (e) {
          const msg = String(e.message).split('\n')[0];
          manifest.failed.push({ scenario: sc.id, size: size.tag, step: msg });
          console.warn(`STEP FAIL ${sc.id} @${size.tag}: ${msg}`);
        }
      },
      key: (k) => page.keyboard.press(k),
      click: async (target) => {
        if (/^[#.\[]/.test(target)) await page.locator(target).first().click({ timeout: 4000 });
        else await page.getByText(target, { exact: false }).first().click({ timeout: 4000 });
      },
      snap: async (name) => {
        await settle(page);
        const file = `${name}-${size.tag}.jpg`;
        await page.screenshot({ path: join(OUT, file), type: 'jpeg', quality: QUALITY });
        manifest.captured.push(file);
      },
    };
    try {
      await page.goto(`${base}index.html?${sc.url ? sc.url + '&' : ''}gameProfile=core`, { waitUntil: 'load' });
      await sleep(sc.ready ? 3500 : 2500);
      await sc.run(h);
    } catch (e) {
      manifest.failed.push({ scenario: sc.id, size: size.tag, error: String(e.message).split('\n')[0] });
      console.warn(`FAIL ${sc.id} @${size.tag}: ${String(e.message).split('\n')[0]}`);
      try { await h.snap(`${sc.id}-FAILED-state`); } catch { /* ignore */ }
    }
    if (errors.length) manifest.failed.push({ scenario: sc.id, size: size.tag, pageerrors: errors.slice(0, 3) });
    await ctx.close();
    console.log(`done ${sc.id} @${size.tag}`);
  }
}
await browser.close();
server.close?.();
let bytes = 0; for (const f of readdirSync(OUT)) if (f.endsWith('.jpg')) bytes += statSync(join(OUT, f)).size;
manifest.totalBytes = bytes;
writeFileSync(join(OUT, SHARD[1] > 1 ? `manifest-${SHARD[0]}of${SHARD[1]}.json` : 'manifest.json'), JSON.stringify(manifest, null, 1));
console.log(`captured ${manifest.captured.length} images, ${manifest.failed.length} problems, ${(bytes / 1e6).toFixed(1)} MB -> ${OUT}`);
process.exit(0);
