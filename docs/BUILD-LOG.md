# Build log

Newest entries go at the top. Each entry records the story, what landed, the tests, what wasn't verified, and the next step. After a context summary, re-read this file and `DECISIONS.md` before continuing.

## us-2.1 (Class pane), us-5.1–us-5.10, us-6.x display, us-17.1, pf-06: F1 first fight, pause and resume (feature/first-fight, 2026-09-26)

**Landed**
- **Application (`Ashen.App.Run`, engine-free):**
  - `RunContent`: the layered content (default preset unless named, optional patches), run and combat data through `RuntimeRegistries.ToRunData` (D-069), `rules/runFlow.json` and the seed codec; `ClassProblem`, `DefaultClass` and `Preview` for W-04; `FirstFightEncounter` and `RegionOf` from data (D-071).
  - `RunSession`: New (createRunState) → StartEncounter (enterCombat args) → Execute through `CombatSession` with autosave (append each command; checkpoint on end turn, fight end and Save & quit) → Save (versioned payload with the D-059 summary) → Load (hash-verified, log replay, D-017 fallback with a warning, re-commit) (D-070).
  - `CombatSession.Commit` and a checkpoint + log `Load`; `CombatOutcome.EnemyActors`; `CombatViewModel` flasks (charge pools and carried flasks, with refusals); `CombatText` (refusals from `strings/combat.en.json`, intent chips with live numbers, card kinds).
  - UI data: `screens.json#focusModes`; `menus.json#pause` (with `hideInCombat`) and `#creationPanes`; new doors `replaceSlot`, `saveQuit` and `saveFailed`; `components.json#combat` art templates and wildcard art includes (D-076). The combat strings join the string table.
- **Presentation:**
  - **W-04 creation, Class pane:** the four classes in authoring order with portrait, name and summary. The selected class unfolds with its attributes, pools and flask split, read from createRunState. The rail shows Class plus the planned Character, Equipment and Review (D-058). Begin (W2c Replace when the slot is occupied) → `RunSession.New` → first fight → W-07. The Reaver cannot begin under the reference preset (D-073).
  - **W-07 combat:**
    - HUD: portrait, identity, act · region · turn, labelled HP/MP/SP meters, Menu.
    - Battlefield: the new `Combatant` kit element (intent chip coloured by kind, figure art, Block badge, HP, labelled Poise and Ward meters, stance, status chips with `+N`, staggered and defeated badges) over the region's combat art.
    - Hand fan of CardViews (cost pips, resolved text, kind, refusal line and refusal tooltip) and the footer group (Actions, Draw, End Turn, Discard · Exhaust, Potions).
    - States with their own focus orders (D-074): targeting (legal targets outlined with a ghost HP preview), the discard chooser when the hand rules prompt, the flask panel (targeted flasks arm the target layer), the enemy turn (red wash, the acting enemy lifts in order, Skip ▶▶, reduced motion; D-075) and the end state with rewards planned (D-072).
    - Every intent is a command through `RunSession`; a refusal shows at the control that started it and keeps focus there.
  - **W-20 pause:** seed and slot; rows from data (Deck, Settings and Abandon planned; Armoury hidden in combat); full-width Resume. Save & quit goes through its hold door → checkpoint → title; a failed save opens W1r.
  - Title **Continue** and W-03 **Load** resume the slot mid-fight (`RunFlow.Resume`); W-03 **New** opens W-04 (D-077). Pad Start is a new router intent (Menu).
- **Screenshots:** 8 new fixtures (creation, combat, combat_intents, combat_targeting, combat_discard, combat_refusal, combat_end, pause): 27 PNGs, about 18 MB. The five state fixtures skip 1920×1080 through the new `skipSizes` fixture field. Fixtures can name layer patches, an encounter, bot commands and a screen state.

**Tests**
- `RunSessionTests` (16):
  - content-built runs and combat args equal 8 recorded shipped runs, and the creation tables equal the shipped registries;
  - the first fight is the data rule's encounter, and every class that can begin starts it under the default preset;
  - new → fight → save → load gives the identical state hash;
  - resume after 1, 3, 7 and 12 commands equals the live fight and plays on identically;
  - the append/checkpoint policy; a refusal is neither applied nor saved;
  - a tampered log and changed content resume with a warning and re-commit;
  - empty, newer, foreign and damaged payloads; the live slot summary; a finished fight is checkpointed.
- `FirstFightUiTests` (10): every combat refusal resolves; intent text; the first fight's view (two targets, intents, flasks, card kinds); a charge flask spends a charge; the prompted discard chooser refuses too many and accepts a choice; pause rows in a fight; the creation rail; the class preview; focus modes; art includes.
- PlayMode `FirstFightSmokeTests`: Boot → title → New → W-03 → door → W-04 (the data default preselected and focused) → Begin → W-07 → a targeted card played on an enemy with the keyboard (autosaved to the log) → End Turn with the pad → the enemy turn (a distinct state) → the next intents → Escape pauses, pad Start resumes and pauses again → Save & quit (Enter held past holdMs) → title with Continue focused → Continue → **the identical state hash** → the fight finished through the screen's command path → the end state (Rewards disabled) → title.
- Results:
  - dotnet 1104/1104;
  - Unity EditMode 1105/1105, Enforcement green;
  - PlayMode 2 passed, 1 skipped (the capture test);
  - `codegen --check`, `check-content`, `transform --check` and `check-docs` green.

**Review** (own screenshot review against W-04, W-07 and W-20; fixed and recaptured):
- the region combat art is a 2×2 sheet of variants (W-07 now shows the first quadrant);
- cards clipped at the top of the hand band (combat card size; the hand takes the remaining height);
- enemy meters overflowing their combatant; unlabelled Poise, Ward and HUD meters (labels from the resource strings);
- the target and discard prompts hidden behind the cards (moved to the foot of the battlefield);
- discard footer buttons not spread; narrow footer labels ellipsized (short labels on narrow);
- the creation rail's planned values ellipsized; the intent-variety fixture ending the fight before two kinds showed.

**Not verified / open**
- **Mouse:** cards, combatants and buttons take UI Toolkit pointer events, but there is no automated pointer test. Keyboard and pad are covered by the smoke test.
- **Compact band (844×390):** W-07's "rails" layout (Actions and Draw on the left, End Turn, Discard and Potions on the right) is not built. The hand is small there, card text is raised to the physical minimum and clips, and a lifted target overlaps the HUD. The upright gate is not wired.
- Not built:
  - US-7.1 weapon-set swap (there is no domain command yet) and the Armaments popover;
  - the W-17 inspector and the pile viewer (planned);
  - per-hit animation, VFX and enemy state frames (D-075); status icons (text chips, D-076);
  - the relic rail and cinders in the HUD; Abandon run.
- W-04's other panes, name entry, seed choice and journey are planned; the default name is `creation.defaultName`.
- **D-073 needs an owner ruling** (the reference preset's Reaver flask split).

**Next:** W-08 rewards on the post-combat pipeline (feature/rewards), then the compact rails layout and a pointer test.
## us-8.1, us-8.3–8.6, us-4.4–4.9, us-13.1, us-13.2: the run loop with shipped parity (feature/run-loop, 2026-09-26)

**Landed**
- `Ashen.Domain.Loop` (D-070l): the run-writing half of the shipped `main.js` controller between fights, ported line by line in the shipped order and on the shipped RNG streams. Scope was taken from V8 precise coverage of the shipped calls (`node Tools/oracle-loop.mjs --coverage`):
  - travel: `RunLoop.EnterNode` (path, floor, Unknown resolutions, the field camp), `StartFight` (Elite Gauntlet, boss destinations including the causeway's no-seat boss, the recent-encounter window), `EnterCombat` (the createCombat arguments under `CombatMods`: Tough Elites, Dread Bosses, Deadly Foes, Glass Cannon, the seat tier ratio, Endless loops) and `EnterEventCombat`. Merchants and events come back as outcomes for the shop/events stream;
  - fights' ends: `RunLoop.EndCombat` over `CombatEnd.Apply`, closing a death or the summit out through `RunEnd.Finish`;
  - the reward door: `RewardDoor` (arrival cinders, auto and manual Continue with the pick on `cardRewards`, drafts via `pickClassNode`/`spendSkillDraft`, flasks and relics with flask growth, `collectArmament` with the profile's found set and discovery receipts, `meta.seen`), then `Acts.Advance` after a boss;
  - rest places: `RestVisit` over the location carrier (tags from tagging, `restMana` resolved by mode, services, `restDenied`, `restHealMult`, Scarce Embers), the arrival's flask refill, `previewRest`, Rest, the flask re-split, level points (`applyLevelUp`), Smith (`ItemSmithing`: plan, commit, restamp, value receipts, `lastSmithingReceipt`), Extract and Install (`CardServices`, `lastMountReceipt`), single-use and multi-use stays;
  - acts: `Acts.Advance` (full or halved heal, the next seat's map, Endless content loops);
  - legacy dungeons: `LegacyDungeons` (the entrance in place of the boss fight, rooms, dialogue with listen/fight/flee on `events`, caches, shrines, retreat, leaving to the summit or the next act) (D-078l);
  - the run's end: `RunEnd.Finish` (`runResult` with the subclass name and the class peak, `recordResult` capped at 20, `recordProgress`, `evaluateUnlocks`) (D-073l).
- Combat port: smithed card faces resolve (`ItemUpgrades`, D-077l); the run-level context hooks `HealMult`/`RunOpcodes` and the location carrier mount (D-074l). Run port: `RunCombat.Arguments` (combat modifiers) and `StartingDeck.ItemMountInstances`. Rewards port: `CombatEndOptions.DungeonResolver`, `BeginPendingReward` made public.
- Content: `rules/loopEngine.json` (hand-written, schema inferred, in the manifest) and the item-upgrade vocabulary in `rules/combatEngine.json`. Names: `Tools/codegen.d/loop.json` via `Tools/loop-keys.mjs` (169 keys, 68 values, 60 messages), reusing the combat, run, map and rewards names. `RuntimeRegistries.ToLoopData`.
- `Tools/oracle-loop.mjs` (D-071l): 108 sequences and 14,983 steps.
  - 2,383 fight entries: 62 summit victories, 31 deaths, 5 Endless runs capped at act 5; all ten boss encounters, the causeway boss 29 times.
  - 3,079 reward doors (auto and manual), 126 act advances.
  - 740 rest stays: 340 Rests, 182 Smith commits (7 on armour), 22 extractions, 12 installs, 182 flask moves, 138 level assignments, 73 relic-denied Rests.
  - 91 legacy dungeons (all three) with 1,025 dungeon steps.
  - Regenerations are byte-identical. Size: 12 MB.

**Tests**
- `RunLoopParityTests`:
  - 108 sequence cases. Each checked step starts from the recorded documents (run, RNG counters, profile); its outcome and the three documents after it must equal the shipped ones strictly (presence, values and key order). Fights are replayed from their command logs in `CombatEngine`;
  - an oracle-presence test (tally floors per door and outcome);
  - a deferred-paths test (journey travel and rest, and the legacy refill, throw by name).
- Mutation check (each file restored byte for byte, sha256 checked):
  - the recent-encounter trim `>` changed to `>=` failed 98 sequences at `$.lastEncounters`;
  - ignoring the rest heal scale failed 34 at `receipts[..].preview.heal`;
  - the flee roll on `misc` instead of `events` failed 35 at the dungeon receipt's roll text or action;
  - swapping two run-record keys failed 108;
  - a corrupted profile patch in the oracle failed exactly that sequence, at `$.seen.flasks[0]`.
- dotnet 1,188/1,188, enforcement included; `codegen --check`, `check-content`, `transform-content --check` and `check-docs` green.

**Not verified**
- Unity EditMode (the UI stream owns Unity).
- The reward door's tap path (`Take`/`Skip`) is not in the oracle (D-079l).
- Settings resolution is the caller's (`LoopSettings`).
- Chains are checked step by step from the recorded documents, not as one uninterrupted C# run.

**Deferred**
- World Journey (D-072l); the legacy flask-slot refill (D-075l); the run-level opcodes beyond `refillFlasks`, which the shop/events stream owns (D-074l); a `rules/victory.json` and a lit-towers ledger (D-076l).
- Shipped defects recorded, not fixed (D-080l).

**Next**
- Integration with the shop/events stream (one `executeRunEffects` context).
- The UI binds `NodeOutcome`, `CombatOutcome`, `RewardDoor`, `RestVisit` and `RunEndReceipt`.

## us-0.4: run and rewards data from content (feature/content-data, 2026-09-26)

**Landed**
- `RuntimeRegistries.ToRunData` / `ToRewardsData` (D-069): the run port's and the post-combat port's data built from the layered content, replacing the oracle dumps for real play. `RegistryBundle` now also loads creation modes, characterCreation, attributeRules, derivedStatRules and the tag tree; `RunSnapshot.ContentVersion` carries the manifest's contentVersion.
- Key order parity for content-built rows: `rules/textAnchors.json` (generated by `Tools/transform-content.mjs`, with its schema) records where each display-text field sat, and the bundle re-attaches text there; tag stamping writes keys in the shipped spread order.
- `run-keys.mjs` fixed on dev (`RunState` uses `K.Xp`).

**Tests**
- `ContentRunDataTests`: registries and documents equal the rewards oracle dump exactly (key order included); all 76 recorded runs with their 152 combat starts and all 168 post-combat cases replay identically on content-built data.
- `ContentCombatDataTests` tightened from sorted-key to exact comparison (equipment top level compared by name).

**Not verified**
- Non-shipped presets (reference) are only covered by the existing layer tests, not replayed.

**Next**
- The first-fight UI stream switches RunSession to `ToRunData`.

## us-11.1: post-combat pipeline with shipped parity (feature/rewards, 2026-09-26)

**Landed**
- `Ashen.Domain.Rewards` (D-062). `CombatEnd.Apply(data, run, combat, enc, result, rng, options)` is the run-writing half of the shipped `main.js onCombatEnd`, ported line by line in the shipped order. Scope was taken from V8 precise coverage of the shipped calls (`node Tools/oracle-rewards.mjs --coverage`). It covers:
  - the write-back of flasks, charges, pools and deficits from the fight, with the shared loadout rejoined (D-063);
  - the skill tracks: `applySkillXp`/`awardSkillXp`, derived tracks, the curve, draft schools, rarity unlocks and the standing auto-upgrade rule;
  - the class track (`awardClassXp`) and the class-tree draft pool (tiers, REQUIRES and CONFLICTS_WITH read in both directions);
  - the character level: `combatLevelXp`, `awardLevelXp` with the points dial and cap, and `rederivePools` through `reconcileRunLoadoutHp`;
  - `combatXpGains`, and `stampDeck` with `adoptEquipmentBonuses`;
  - the defeat path (`hp = 0`), the win count, the cleared combat receipt and `grantSmithingReward` (with idempotent claims);
  - the boss ledgers (`bossesBeaten`, `bossGroups`), the act-3 summit and Endless;
  - the reward rolls on their shipped streams: cinders with `runeGainMult`, the card offer (Feral Eye, Chaos Rewards), skill and class drafts, the flask pity counter, elite and boss relics, armament drops with consolation cinders;
  - `run.pendingReward` (`beginPendingReward`).
- `rules/rewardsEngine.json` (hand-written, schema inferred) holds the tree parents, track kinds, level-curve defaults, the roll guard, flat Chaos odds, relic rarities, the legacy charge-flask seam, smithing pools, the summit act and the victory titles. Names come from `Tools/codegen.d/rewards.json` via `Tools/rewards-keys.mjs`: 85 keys, 13 values and 14 messages, with combat and run names reused.
- `Tools/oracle-rewards.mjs` records 168 cases: the 60 golden combats replayed to their end, and 108 generated fights (4 classes × normal/elite/boss × 18 run variants). The variants reach level thresholds and multi-level climbs, track crossings and the standing upgrade, class thresholds and tier-2 pools, queued drafts on every track, rune and Feral Eye relics, a claimed Smithing reward, Chaos Rewards, Endless and the act-3 summit, a fully found armoury, full flask pity, `equipmentChanged` and low-HP defeats. Each case stores the run before, the combat end snapshot, the encounter, the result, the RNG counters, the options, and the expected run, receipt and counters after. A JS self-check requires each snapshot to restore to an identical pipeline. Re-running the oracle gives byte-identical files. Outcomes: normal 55 reward / 20 defeat, elite 26 / 15, boss 21 reward / 29 defeat / 2 summit. Size: 12 MB with the registry dump (which adds `nodes`).

**Tests**
- `RewardsParityTests`, 170 cases:
  - 168 cases, each restoring the fight with `CombatSnapshot.Restore` on an RNG at the recorded counters and running `CombatEnd.Apply`. The run document and the receipt must equal the shipped ones strictly (presence, values and key order), and every RNG counter must match;
  - an oracle-presence test;
  - a deferred-path test (journey and legacy dungeon throw by name).
- Mutation check: corrupting a pending-reward cinder value, swapping two reward keys, bumping an expected `cardRewards` counter and bumping a receipt `levelAward.points` each failed exactly one test. The failures named the path (or the key order, or the stream). Each file was then restored byte for byte (sha256 checked).
- dotnet 768/768, enforcement included; `codegen --check`, `check-content` and `transform-content --check` green.

**Not verified:** Unity EditMode (the UI stream owns Unity). The rewards data is built from the oracle dump; there is no content-built `RunData`/`RewardsData` path yet. Swapping equipment mid-fight is exercised only through the flag, because the C# combat has no swap commands yet.

**Deferred:**
- journey and legacy-dungeon fights (D-066);
- the smith's services-table normalization (D-065);
- the caller-side profile, save, audio and screen work (`finishRun`, `collectArmament`, game over and the reward screen; D-067).

**Next:** the reward screen (take/skip rows, `collectArmament`, draft picks via `spendSkillDraft`/`pickClassNode`) and the game-over flow's `finishRun`, driven by `CombatEndReceipt`.
## us-1.2, us-1.3, us-1.4, us-0.10 (title footer), us-17.1: F1 UI foundation, boot, title and slots (feature/boot-title, 2026-09-26)

**Landed**
- **UI data** (hand-written, schema'd, in the manifest):
  - `ui/tokens.json`: colours, space, radius, font, size, border, duration and opacity groups;
  - `ui/layout.json`: two panels, bands, `minPhysical`, CategoryNav rules and the verification sizes;
  - `ui/screens.json`: the registry, with a required `focusOrder`, `initialFocus`, back behaviour, transitions, and `planned`/`dev` flags;
  - `ui/menus.json`: title rows in the owner order, plus the W2 doors;
  - `ui/components.json`: kit defaults and the art prefixes.
  - All display strings are in `strings/app.en.json`.
- **Codegen:** the new fragment `Tools/codegen.d/ui.json` holds ScreenIds, UiNames, UiClasses, UiKeys, UiValues, menu actions and rules, confirm ids, placeholders, formats, SlotSummaryKeys, TokenKeys, UiResources, UiMessages and the UiMath numbers (D-054).
- **Tokens:** `Tools/ui-tokens.mjs` generates `Tokens.uss` from `ui/tokens.json`. Its `--check` mode also lints every USS file for raw values (D-055), and `Tools/ci/check-content.mjs` runs it.
- **Application (engine-free, `Ashen.App.Ui`):**
  - `ScreenRegistry`, `MenuSet`, `LayoutRules`, `UiTokens`, `ComponentDefaults`, `ConfirmPolicies`;
  - `LayoutClassifier` (bands, narrow mode, panel scale, physical minimums);
  - `CategoryNavModel` (Rule 11 fit with hysteresis);
  - `FocusOrder` (regions, then items; skips unusable items; wraps);
  - `MenuRules` (always, hasValidSlot, targetBuilt);
  - `SlotSummaries` (Ready, Newer, Unreadable, Empty; Continue target) (D-059);
  - `StringTable` (named templates);
  - `UiData`.
- **Platform:** `PlatformPaths` and `InputRouter`. The router is the one Move/Submit/Cancel source, built on the Input System's UI actions plus Tab. Active Input Handling is now the Input System (D-053).
- **Presentation (`Ashen.Presentation.UI`):**
  - `UiHost`: the UIDocument and two runtime PanelSettings (D-057).
  - `Navigator`: screen stack and modal layer; band and layout classes on the root; PanelSettings swap; `minPhysical` after scaling; innermost-first Back; focus return.
  - `FocusModel` and the `ViewModel` base (`INotifyBindablePropertyChanged`).
  - `LocLabel` and `LocButton` (`[UxmlElement]`, `string-key`), and the `UiArtCatalog` (D-056).
  - Kit, each as UXML + USS + C#: Shell, Workspace, CategoryNav, Confirm, Meter, CardView, HandFan, FooterGroup, Tooltip, HoldButton, Refusal, and SlotRow.
- **Screens:**
  - W-24 boot: determinate bar over the manifest, then validation and the profile check.
  - W-24 content error: dev report list with Copy/Quit; player notice with Quit.
  - W-01 recovery: **stub**.
  - W-02 title: press-any-input gate (D-052); menu from `menus.json` with Continue disabled when no slot is valid; W3b preview; footer with the build and the AI disclosure. The disclosure is focusable and opens the full notice.
  - W-03 slots:
    - Load and New modes with 3 rows;
    - portrait, name, class, act, floor, HP, saved time and seed;
    - empty, newer-build and unreadable states, and the empty state with a full-width Create character;
    - the pre-review doors, and the destructive hold-to-confirm delete.
  - W-23: the doors as a modal (D-061).
  - `kitGallery` (dev): a review sheet for the kit.
- **Boot scene:** `Assets/Scenes/Boot.unity` (a camera plus `AshenBoot`), built by `Cli.CreateBootScene`. It is the only scene in the build settings. `Cli.BuildUiArt` builds the art catalog.
- **Screenshots:** `Cli.CaptureScreens` runs the `ScreenCaptureTests` PlayMode test over the 13 fixtures in `Tests/Fixtures/screens` at 1280×720, 1920×1080, 844×390 and 390×844, and writes `docs/screens/<fixture>_<w>x<h>.png`: 52 PNGs, about 21 MB (D-060). Run it without `-quit` and without `-nographics`.

**Tests**
- `UiRulesTests` (21 cases) and `UiDataTests` (9) in the shared suite, run by both runners:
  - owner menu order; Continue enablement; planned targets;
  - CategoryNav fit and hysteresis; focus order and wrap;
  - bands and scales at 6 viewports; physical minimums; string templates;
  - slot summaries for empty, ready, newer and damaged saves;
  - ScreenIds match the registry; every built screen has UXML and a focusOrder; focus regions exist in the UXML;
  - transitions, menus and doors resolve; backgrounds and art are in the registry;
  - UXML names are generated and UXML carries no literal text; USS uses tokens only;
  - the title footer binds `title.aiDisclosure`.
- PlayMode `BootTitleSmokeTests`:
  - the Boot scene reaches the gated title;
  - Space lifts the gate and focus lands on Load (Continue is disabled with no save);
  - Down/Up and wrap work; Enter opens W-03 Load, where focus is on Create character in the empty state;
  - Escape returns with focus on Load; the pad d-pad moves focus.
- Results:
  - dotnet 249/249;
  - Unity EditMode 250/250, Enforcement green (no literals in Presentation or Platform);
  - PlayMode 1 passed, 1 skipped (the capture test is skipped unless `ASHEN_CAPTURE=1`);
  - `codegen --check` and `check-content` (with the tokens check) green.

**Review** (screenshot-review sub-agent against 04 and the story ACs; fixed and recaptured):
- slot row delete overlapping the text on narrow;
- no portrait or character name in the rows;
- ready primaries not green;
- hold text below the minimum on compact;
- focus on an empty slot in the empty state;
- a raw saved timestamp ("yesterday" and the time of day now);
- a duplicated newer-build notice;
- New mode preselecting an occupied slot;
- touching footer buttons;
- a clipped content-error title and a doubled separator;
- the compact title overlapping its footer;
- the blue default-theme focus ring (now gold, and red on Back/Exit).

**Not verified / open**
- **US-1.1:** the recovery screen is a stub. It does not archive or restore files.
- **US-1.3:** Journal, Custom Run and Settings are disabled as planned (D-058). Confirming Continue, Load or New ends in a refusal until creation and the run screens exist.
- **US-1.4:** journey type and playtime are read but not shown; they belong in the row tooltip.
- **US-0.10:** the first-launch notice (once per profile) and About are not built. The notice frame exists as the `aiNotice` door.
- Not verified:
  - touch input on a device;
  - UI Toolkit's native navigation in a player build (it is deliberately bypassed; D-053);
  - the `⬡` glyph, which the default font lacks (ASSET-GAPS).
- No ash particles on the gate; the title's lit/unlit rule waits for the profile service.

**Next:** W-04 creation (Class pane), then W-07 combat with the kit (Meter, CardView, HandFan, FooterGroup), then W-08 rewards and W-20 pause.
## us-4.2 (with the map parts of us-4.1, us-4.3, us-4.4): act-map generation with shipped parity (feature/map, 2026-09-26)

**Landed**
- `Ashen.Domain.Map` (D-046 to D-051), a line-faithful port of:
  - `engine/mapgen.js`: `generateActMap` (path walk with the no-cross rule, entrances, typing with bounded retries, `rollType`, `relaxPlace`, `ensureRestBeforeElite` with its victim order and donor restore, `finish`), `assignBossDestinations`, `sampleActShape` and `sweepSeed`;
  - `model/floorplan.js`: `resolveAnchor`, `resolveFloorPlan` (every refusal keeps its shipped key), `minViableFloors`, `applyRunShape`;
  - `engine/actmap.js`: `buildActMap`, `bossEncounterForNode` (including legacy singular graphs), `drawSeatOrder` (reuses `SeatOrder`);
  - `resolveUnknownNode` with the quest-history gate (`eventChoiceRequirementMet`), `rollEncounter`, `encounterFitsSeat`, `finalTier`, `tierOf`, `bossDestinationLabel`.
- `MapData` holds what the map reads. It is built from the oracle dump (`FromTableDump`) or from content (`RuntimeRegistries.ToMapData`). `RuntimeRegistries` now also carries the seats table and the map documents (`balance/mapConfigs.json`, eventMeta's gates, `catalog/bossDestinations.json`, `balance/mapShape.json`'s limits and legacy bosses).
- `rules/mapEngine.json` (hand-written, schema inferred): the retry and guard counts and the vocabularies the shipped code kept as constants.
- Names: `Tools/codegen.d/map.json`, regenerated by `Tools/map-keys.mjs` from the `MK.`/`MV.`/`MM.` aliases. It refuses a name that CombatKeys or CombatValues already owns, so the port reuses `K.`/`V.`.
- `Tools/oracle-map.mjs` records the shipped outputs to `Unity/Assets/Tests/Oracle/map` (4.6 MB):
  - every seat × every tier × 20 seeds (`sweepSeed(3000+i)`);
  - 7 run histories (earned, completed and malformed quest gates; 54 gated events rolled);
  - 25 run shapes (14 accepted, 11 refused);
  - 347 authored configs through `generateActMap` directly, including acts found by instrumenting a scratch copy of mapgen.js that reach the rare exits (spare victim, any victim, donor restore, surplus-Shrine donor);
  - 26 floor plans, plus `sampleActShape`, `rollEncounter`, Unknown rolls, seat orders, legacy bosses and refusals.

**Tests**
- `MapParityTests` (32 cases). 1,883 maps are compared node for node, in insertion order, with every RNG counter: 180 seat × tier maps, 252 history maps, 378 shaped maps and 1,073 direct `generateActMap` maps. The 180 seat × tier maps are also rebuilt from content-built data. On top of those: 300 refusals (with counters), 26 plans, 108 encounter rolls, 180 Unknown rolls, the seat order, legacy and live boss lookups, and the content tables in shipped order.
- Mutation checks:
  - Corrupted oracle values (a node type, an events counter, a destination label) fail.
  - So do code mutations of the no-cross rule, the rest-before-Elite roll bar, the relax placement and rest opening, the victim order, the donor and Shrine-donor rules, the completed-quest gate, malformed history rows, the label and the shape minimum.
- dotnet 392/392 (enforcement included). `codegen --check`, `check-content`, `transform --check` and `check-docs` are green.

**Not ported (deferred):** the `describePlan` readout text (tooling); `refreshBossDestinationLabels`; the problem strings of the quest validators (only pass/fail is needed here); the JS-only `act` argument refusals.

**Not verified:** Unity EditMode (the UI stream owns Unity; this runs at integration).

**Next:** run the map from the run state (map screen W-05, node entry, `bossEncounterForNode` on arrival), once run creation lands.

## us-2.2: run creation with shipped parity (feature/run-state, 2026-09-26)

**Landed**
- `Ashen.Domain.Run` (D-042): `RunState.Create` is the shipped `createRunState`, ported line by line onto the JSON run document. Scope was taken from V8 precise coverage of the shipped calls (`node Tools/oracle-run.mjs --coverage`). It covers:
  - creation modes and class presets, with the allocation rules;
  - the derived-stat rule resolve, validation, derive and host snapshot;
  - the equipment profile snapshot (create and restore), the kit plan and role receipts, `effectiveEquipmentRating`;
  - weapon card packages and the equipped attack plan;
  - the composed starting deck, grant refs, `orderStartingDeck`, item-owned cards (`reconcileGrantedCards`, `desiredGrantInstances`, card mounts);
  - `stampDeck`, card and run mods, the pool reconcile, requirement receipts;
  - starting kits, armour, hands and relic; relic modifiers; flask charges and growth; zones; the default seat order.
- `RunCombat.CreateArgs(run, encounterId, data, settings)`: the `createCombat` arguments `main.js enterCombat` builds (seat-tier HP ratio, hand-rule defaults, swap-cost rule, start statuses). A fight can now start from content alone.
- `rules/runEngine.json` (hand-written, schema inferred): versions, defaults and vocabulary lists. Names come from `Tools/codegen.d/run.json` via `Tools/run-keys.mjs`: 202 keys, 53 values, 154 messages and 2 patterns, with combat names reused.
- `Tools/oracle-run.mjs` records, for every class:
  - twelve default seeds (`sweepSeed(2000+i)`);
  - seven creation variants: two more modes, the alternate kit, armour and relic, and two hand picks;
  - two refused variants.
  For each run it writes the run document, the RNG counters, and two fights (args, combat-start snapshot, counters). Its JS self-check replays each fight from the JSON-plain args and requires the identical start. Output: 76 runs, 152 fights, 7.0 MB, plus the registry dump.

**Tests**
- `RunParityTests`, 238 cases:
  - the run document equals the shipped one for all 76 runs, with strict presence (D-045);
  - the args equal the recorded ones;
  - `CombatStart.Create` on those args reproduces the shipped snapshot and counters, and a turn can be ended;
  - the 8 refused variants throw the shipped message;
  - the deferred paths throw `NotSupportedException`.
- Mutation check: corrupting a run `maxHp`, an args card id and a snapshot enemy HP failed the three tests, naming the first differing path. The file was then restored.
- dotnet 517/517, enforcement included; `codegen --check` and `check-content` green.

**Not verified:** Unity EditMode (the UI stream owns Unity). The creation-mode `equipmentProfiles` layer is ported, but no shipped mode authors one, so it is not exercised.

**Deferred (D-043, D-044):**
- custom allocations;
- derived-stat option layers;
- the load door (snapshot restore and migrations);
- hand-rule settings overrides;
- journey, legacy-dungeon and Custom Climb fights;
- the heal ledger.

**Next:** F1 playable loop: a run-backed combat screen. Then the load door (`restoreDerivedStatRuleSnapshot`, schema migrations) with save round-trip parity.
## Combat session, legality, content-built data and skill XP (feature/combat-core, 2026-09-26)

- **Content → engine:** `RuntimeRegistries.ToCombatData` builds the combat data from content, adding attributes, property rules by tag, and the class tree. `ContentCombatDataTests` confirm:
  - every table equals the shipped one (rows, row order, enemy move order);
  - all 60 golden combats start and replay identically on it. (0.1.4.1)
- **Legality and session** (engine side of US-5.9), 0.1.4.2:
  - `CombatLegality` answers the engine's pre-payment checks without mutating. Refusal keys come from `strings/combat.en.json`.
  - `CombatSession` (Application) handles refusals, rolls back to the last safe point, and checkpoints to SaveService with a hashed command log (D-017).
  - A property test shows legality equals engine acceptance for every card and target at every step of 10 golden combats.
- **Skill XP** (removes the D-041 deferral): `SkillXp.Record` listens on the bus exactly as the shipped `attachSkillXp` does. The vocabulary lives in `rules/combatEngine.json` `skillXp`.
  - The oracle now projects the receipt after every step. This also fixed a recorder bug: it had stored a live reference, so every step showed the final value.
  - All 60 combats match.

## us-5.3: combat start and previews with shipped parity (feature/combat-core, 2026-09-26)

**Landed**
- `CombatStart.Create` (shipped `createCombat`), covering:
  - the entity factories and `stampPlayerPoiseMax`, with the rated poise threshold as the vessel;
  - property mounts, enemy HP on `enemyHP` (with the JS `Math.round`), enemy ratings and meters;
  - the deck-instance copy, shuffle with Innate on top, start statuses, opening intents and turn 1.
- `CombatPreview.PreviewCard` and `PreviewIntent` (shipped previews, SPEC §3.13) plus `TokenBindings` (`computeTokenBindings`). These use the same math as execution and consume no RNG.
- The oracle now records the exact `createCombat` inputs and previews (every hand card and every living enemy's intent) after every step. Output is compact (5.6 MB for 60 combats).
- `Tools/oracle-replay.mjs` also checks the previews: 60/60, 0 failures.
- Oracle scripts delete only their JSON outputs, so committed `.meta` files survive regeneration.
- `Tools/combat-keys.mjs` regenerates `Tools/codegen.d/combat.json` from the engine's alias usage, plus `Tools/combat-keys/{values,messages}.json`.
- `rules/combatEngine.json` gains `defaults.handMax`, `damageSchools` and `tokenizableOps`.

**Tests**
- `CombatParityTests` now runs 181 cases:
  - `CreateCombatMatchesTheShippedStart`: the full serialized snapshot equals the shipped one for all 60 combats (event log, piles, meters, intents, RNG).
  - The replay compares previews after every step.
  - The resume test also checks previews.
- Mutation check: a changed event-log type or preview value fails the create and replay tests.
- dotnet 279/279.

**Not verified:** Unity EditMode in this worktree. The UI stream owns the main checkout's Unity instance; this runs at integration.

**Next:** run creation (`createRunState`) for a default character with oracle parity, so F1 can start a fight from content alone.

## us-5.1: combat engine port with shipped parity (feature/combat-core, 2026-09-26)

**Landed**
- `Ashen.Domain.Combat` (D-041): the shipped engine/combat.js family ported line by line onto a JSON document state (D-036, D-037).
  - Covered: `dispatch` (playCard, endTurn, useFlask), queue drain, end check, player and enemy turns, delayed moves, weighted move picks with `maxConsecutive`, and phases.
  - Every combat opcode, status stack modes, meters, procs, resists and decay, triggers (stance, property mount, status, phase) and the full predicate set.
  - Formulas, AR/DR/PR/Poise/Ward ratings with break meters, hand rules and the discard choice, relic/equipment/class property mounts, card resolution (upgrade, profile, school variant, mods), and snapshot restore/serialize.
- `CombatData` plus `rules/combatEngine.json` (limits, defaults, card-property vocabulary, grip tags, upgrade tags, flask kinds).
- Names come from `Tools/codegen.d/combat.json`: 374 keys, 42 events, 19 ops, 22 predicates, 118 values, 50 messages.
- **Content order fix (D-040):** generated content now keeps the shipped key order. Enemy move order and registry order decide seeded outcomes.
- Tools:
  - `Tools/oracle-replay.mjs` replays the golden logs from their snapshots through the shipped engine: 60/60 combats, 0 failures.
  - `oracle-combat` now also writes the registry tables it ran against.
- The registries port (us-0.3, background agent) merged into the lane first: 26 parity tests, both presets exact.

**Tests**
- `CombatParityTests`: every golden combat (60 combats, 1,068 steps, 31 victories and 29 defeats, all 35 encounters) replays through the C# engine with the projection identical after every step. The projection covers HP, block, statuses, intents, resources, hand, pile sizes and all RNG counters.
- The same test also saves mid-fight, restores and resumes to the end identically.
- Mutation check: corrupting one oracle HP value fails both tests.
- dotnet 219/219; Unity EditMode 220/220. Enforcement green (no literals in the engine).

**Not verified / open**
- The engine rules for US-5.2 to 5.8 and 6.1 to 6.5 are in place, but their UI acceptance items (previews, targeting, HUD, enemy timeline) are not done. Only US-5.1 is counted complete.
- `useFlask` is ported but not in the golden logs (the bot never drinks). `createCombat` and previews come in the next story, with oracle-recorded inputs.

**Next:** us-5.3: `createCombat` and previews (`previewCard`/`previewIntent`), with oracle parity; then an F1 playable-loop combat screen.

## Phase F0 Foundation — complete (promoted at 0.1.1.1; renumbered from 1.0.0.0 by owner ruling, 2026-09-26)

**Exit gate** (09 §8):

| Check | Result |
|---|---|
| Validator | Green: 150 content files, 65+ schemas |
| Counts | Match the shipped game (195 cards, 63 relics, 33 enemies, 35 encounters, 25 events, …) |
| RNG and seat oracles | Green: 13,440 draws plus 100 seat cases, bit-identical |
| Registry | 5,265 assets, no dangling IDs, 899 imported pixel-identical |
| Tests | dotnet 72/72, Unity EditMode 73/73 |
| CI on `dev` | 7/7 jobs green; the Unity jobs skip because the licence secret isn't set; the Windows build runs from `test` onward |

**Moved out of F0:**
- Map-generation parity and golden replays go to F3/F2 (D-033).
- Most art domains import per phase (D-035).

**Stories:**

| Story | Version |
|---|---|
| us-0.3 | 0.0.2.0 |
| us-0.9 | 0.0.3.0 |
| CI fix | 0.0.3.1 |
| us-0.4 | 0.0.4.0 |
| us-0.5 (part 1) and us-18.4 | 0.0.6.0 |
| us-0.7 | 0.0.7.0 |
| us-0.6 | 0.0.8.0 |
| us-0.8 | 0.0.9.0 |
| us-0.1 and us-0.2 | 0.0.11.0 |
| us-0.10 | 0.0.12.0 |
| foundation feature complete | 0.1.0.0 |
| us-16.2 | 0.1.1.0 |
| docs-check fix | 0.1.1.1 |
| phase F0 promotion | 0.1.1.1 (was wrongly tagged v1.0.0.0; renumbered) |

**Environment:** **C: is full (0.2 GB free)** and D: has about 3.7 GB. The owner needs to free space before the F1/F2 art imports (animations are 122 MB of WebP source, several times that as PNG plus Library).

**Next:** F1, the first playable loop (kit → W-02 title → W-03 slots → W-04 Class pane → W-07 combat → W-08 rewards → W-23 → W-20 → checkpoint and resume).

### us-16.2: procedural SFX core (2026-09-26)

- **`Ashen.App.Audio.SynthRenderer`** (engine-free) renders the shipped `tone`/`noise` recipe layers to mono PCM with the shipped Web Audio shapes:
  - tones: exponential glide, and an exponential attack of min(attackMax, dur·fraction) followed by an exponential decay;
  - noise: filtered through high- and low-pass stages, with an exponential decay.
- **Recipe resolution:** exact id, then family (text before `_`), then `default`, which is audible.
- **Deterministic noise:** its own xorshift seeded from the recipe id, never a domain RNG stream.
- **Data:**
  - `audio/synth.json` holds the engine parameters;
  - `audio/contexts.json` maps the 8 contexts plus `quiet` to a bed or `"silence"`, and holds generated-file slots (null today) and stingers.
  - Generated: `SynthKeys`, `Waveforms`, `DspConstants`, `NoiseAlgorithm`.
- **Unity:** `Ashen.Presentation.Audio.SynthClipCache` wraps the PCM in cached `AudioClip`s.
- **Tests** (`SynthRendererTests`, 10):
  - resolution order;
  - every recipe renders finite, audible, bounded samples;
  - length covers the longest layer;
  - determinism, and a missing id is never silent;
  - contexts map to beds or silence, and stingers resolve.
- **Enforcement:** the number allowlist now also accepts the identities 0.0 and 1.0.
- **Results:** dotnet 72/72 · Unity 73/73.
- **Not yet built:** music-bed synthesis (drone, pulse, arpeggio variants) and context playback, with the F1 title and map screens; generated music tracks go in the file slots then (D-006).

### us-0.10: clear AI disclosure (2026-09-26)

- **Data:** the disclosure text is in `strings/app.en.json`, a hand-written app string table (string-map schema). `about.json` holds the full and short keys, the notice keys, `showOnTitle` and `showOnFirstLaunch` (both true), the 6 modalities the text must name, and the credit sources.
- **Code:** the generated `StringKeys` (from `strings/app.en.json`) and `AboutKeys`.
- **Enforcement:**
  - `AiDisclosureTests` (4): the text says "generative AI" and names every modality and "no licensed third-party"; title and first-launch flags are on; README and `build-info.json` carry the statement; credit sources exist.
  - The CI content job checks the same.
- **Not yet built:** the title footer, the first-launch notice and the About screen that bind these keys. They come with F1 (W-02, W-23) and F4 (W-18).
- **Results:** dotnet 62/62 · Unity 63/63.

### us-0.1 + us-0.2: asset registry, lossless pipeline, provenance (2026-09-26)

- **`Tools/scan-assets.mjs`** plus the data-driven ID rules in `Tools/assets.config.json`:
  - registers **5,265 assets in 17 domains, with 0 unmatched files**;
  - reads dimensions and alpha from WebP (VP8, VP8L, VP8X) and SVG headers;
  - writes a SHA-256 per source file;
  - output `Content/assets/registry.json` is in the manifest and has a schema.
- **Conversion:** Blender 4.0 headless (`Tools/blender/webp_to_png.py`) with the Standard view transform. Every PNG is reloaded and compared: **899/899 pixel-identical (max diff 0.0000)**, 145 MB, 3.5 min. SVG is copied for the built-in Vector Graphics importer.
- **Unity import:** `AshenTextureImporter` applies `Editor/Config/importRules.json` (Sprite, alpha as transparency, no mips, per-domain size and compression; backgrounds and environments at 4096 HQ, VFX at 1024). The batchmode import is clean.
- **Git:** PNGs are gitignored and `.meta` files committed (D-018). `Tools/scan-assets.mjs --import` rebuilds the PNGs deterministically.
- **Docs:** `docs/ASSET-INVENTORY.md` (counts per domain, derivations) and `docs/ASSET-GAPS.md` (known gaps and fallbacks, unused list: empty).
- **Provenance (us-0.2):** `CREDITS.md` and 18 `provenance.json`/`prompts*.json` files are copied into `Provenance/imported` with a SHA-256 manifest. `Provenance/generated/manifest.json` is ready for AI-generated assets.
- **Tests** (`AssetRegistryTests`):
  - IDs, origins and hashes;
  - every imported asset has its committed `.meta`;
  - no unregistered image under `Art/`;
  - all 33 enemies have base art and 7 state frames;
  - provenance is complete, and generated assets must be declared.
- **Results:** dotnet 58/58 · Unity 59/59.
- **Disk:** D: has 3.7 GB free, C: 0.2 GB. The remaining domains import per phase (D-035).

### us-0.8: the build fails on magic strings or numbers (2026-09-26)

- **`EnforcementTests`** (suite: Enforcement) uses a small C# lexer that skips comments and finds string, char and number literals, with file and line. It scans `Game/{Domain,Application,Content,Presentation,Platform}` (not `Generated/`) and checks:
  - `NoMagicStrings`: zero string or char literals;
  - `NoMagicNumbers`: only 0 and 1;
  - `NoContentIdsInCode`: 300+ catalog IDs, none of them in code;
  - `EngineFreeAssembliesDoNotReferenceUnity`: `noEngineReferences` set, and no `using UnityEngine/UnityEditor`;
  - `AssemblyReferencesPointDownTheLayersOnly`: Generated → Domain → Content → Application → Platform → Presentation;
  - `GeneratedCodeIsMarkedAndNotHandEdited`;
  - a lexer self-test.
- **Violations found and fixed (7):** two `bytes.Length * 2` capacity hints, a parse depth of 128 (now `ConfigLimits.MaxParseDepth`), and report-format literals (now `ValidationMessages.IssueFormat` and `MoreFormat`).
- **Results:** dotnet 53/53 · Unity EditMode 54/54.
- **Note:** the 08 §12 table lists `CodegenClean` and `ContentValid` as tests. They run as `codegen --check` in the CI content job and as `ContentValidatorTests`.

### us-0.6: saves are never lost or corrupted (2026-09-26)

- **`Ashen.App.Saves.SaveService`** (engine-free, PF-06):
  - **Envelope:** version, gen, content hash, snapshot hash, payload, and a canonical SHA-256.
  - **Checkpoint:** write tmp → `Flush(true)` → read back and verify → `File.Replace` with `.bak` (or `File.Move` on first save), with data-driven retries. The mirror is written the same way. Then a fresh log for the gen starts, and only then are older logs deleted.
  - **Command log:** one JSON line per record with gen, seq, CRC32 and `stateHashAfter`.
  - **Load:** picks the highest valid gen among primary, mirror and both `.bak` files. Newer versions are refused without touching the files. Older versions migrate step by step and never replay old-schema commands (D-017). Replay stops at the first torn or bad-CRC record.
- **Data** in `rules/saves.json`: 3 run slots, current version, profile slot name, retry policy. Generated: `SaveKeys`, `SaveLayout`, `SaveMessages`, `Crc32Algorithm`.
- **Tests** (`SaveServiceTests`, 13 cases):
  - round trip and gen increments;
  - corrupt primary falls back to the mirror; a tampered payload fails the hash;
  - all copies corrupt reports Corrupt and keeps the files;
  - a newer version is refused and left untouched;
  - two-step migration;
  - the log replays and truncates at a torn record;
  - a new gen drops old logs;
  - slot isolation, and profile mirror recovery;
  - the CRC-32 IEEE check value.
- **Results:** dotnet 46/46 · Unity EditMode 47/47.
- **Not yet covered:** `File.Replace` under antivirus locking was not reproduced; the retry path exists but is untested. Replay against real game state waits for the F1/F2 command loop.

### us-0.7: predictable config layers (2026-09-26)

- **`ConfigLayers.Build(LayerSelection)`** applies base content → preset (default: `reference`) → ordered patches (ascension, custom-run modifiers, advanced settings) → modding overrides.
  - Each layer is an RFC 7386 merge-patch over id-keyed documents.
  - Every changed file is then re-validated against its schema.
- **The result is a `RunSnapshot`:** an immutable `ContentSet` with a canonical SHA-256 (independent of key order), the preset id, the player settings (preset defaults with player overrides), and the list of applied overrides.
- **Modding layer:** off unless enabled; manifest paths only; 1 MB and depth-64 caps (`ConfigLimits`, generated).
- **Tests** (`ConfigLayersTests`, 12 cases):
  - the reference is the default (starting cinders 100, AR DEX .25, break recovery 1, Reaver flasks 2, weighted opening hand, `shrineMultiUse`);
  - `shipped` keeps the web defaults;
  - both presets' effective configs are schema-valid;
  - later patches win and null deletes;
  - an unknown file or an invalid patch is caught;
  - the hash is stable and differs per preset;
  - the snapshot is immutable;
  - the modding gate and size cap hold;
  - player settings layer correctly.
- **Results:** dotnet 33/33 · Unity EditMode 34/34.

### us-0.5 (part 1) + us-18.4: deterministic RNG proven against the shipped JS (2026-09-26)

- **`Ashen.Domain.Random.Rng`** is a mulberry32 per named stream: base = seed XOR fnv1a(name); the value at draw i comes in O(1), so a restored counter is exact.
  - It is integer-only. Ranges use `(u·n) >> 32`; chance uses hundredths.
  - `Clone()` exists for previews that must not advance the real streams.
- **`SeedCodec`** does base-35 with homoglyph folding; the alphabet comes from `rules/rng.json`.
- **`SeatOrder.Draw`** does one shuffle on `seats`; a pinned first seat rotates the order.
- **Generated:** the `RngStream` enum (from `rules/rng.json`) and the `RngAlgorithm` constants (D-034).
- **`Tools/oracle.mjs`** calls the shipped `engine/rng.js` and `drawSeatOrder` and writes `Unity/Assets/Tests/Oracle/{rng,seeds,seats}.json`:
  - 15 seeds × 14 streams × 64 raw draws;
  - int, shuffle and chance samples, and restored counters;
  - the seed codec;
  - 50 seat orders, plus the same seeds with Marches pinned first.
- **`RngParityTests`** (suite: Parity) passes:
  - **13,440 raw draws bit-identical**;
  - ints, shuffles, chances at 5, 12.5, 25, 50, 75 and 99.99%, and restored counters identical;
  - seed format and parse identical;
  - all 100 seat-order cases identical.
- **Results:** dotnet 21/21 · Unity EditMode 22/22.
- **Deferred (D-033):** golden replays and map-generation parity move to F2/F3, when the combat loop and the map generator exist.

### us-0.4: the validator names exactly what is wrong (2026-09-26)

- **`Ashen.Content.ContentValidator`** (engine-free) checks:
  - manifest integrity (SHA-256 per file);
  - a schema per data file, via `SchemaValidator`: types including unions, required, unknown properties, enums, `x-keyed`, `x-ref`;
  - effect ops, only inside the containers listed in `rules/effectOps.json` (`effects`, `do`, `onEnter`, `onFill`);
  - required string keys (`rules/stringKeys.json`, generated from the transform config);
  - data-driven row rules (`rules/validation.json`: Mana ⇒ ≥ 1 Action and ≥ 1 Stamina);
  - every preset, merge-patched (`MergePatch`, RFC 7386) onto its base file and re-validated.
- **Every issue** reports file, JSON path, a stable rule ID (`ValidationRules`) and a message (`ValidationMessages`), all generated constants.
- **Real findings fixed:**
  - The keepsake op `addFlaskCapacity` was missing from the vocabulary. It is appended at the end to keep enum values stable.
  - `equipmentMeta.modFields.*.op` is not an effect op; the check is now scoped to containers.
  - The hand-rules schema now allows the owner's `openingWeighted` rule.
- **Tests:** `ContentValidatorTests` covers committed content valid, counts, hash drift, dangling reference, type and unknown property, unknown op, missing string, cost rule, preset cleanliness, and RFC 7386 cases. dotnet 14/14; Unity EditMode 15/15.
- **Editor CLI:** `Ashen.EditorTools.Cli.ValidateContent` (batchmode exit code) and the menu item `Ashen/Validate Content`. It validates 137 files in the Unity batchmode run.
- **CI** is green on `dev` at 0.0.3.1. The Unity jobs skip because the `UNITY_LICENSE` secret isn't set.

### us-0.9: generated key constants (2026-09-26)

- **`Tools/codegen.mjs`** (Node, D-028) reads `Tools/codegen.config.json` and the content, and writes `Unity/Assets/Game/Generated/*.g.cs`:
  - `EffectOp` (25 ops from `rules/effectOps.json`), with a wire-name `Parse`/`ToWire`;
  - `SchemaKeys`, `SchemaTypes`, `ManifestKeys`, `ContentLayout`, `ValidationRules`;
  - `ContentFiles`, one constant per manifest path.
  - It is idempotent. `--check` runs in the CI content job.
- **Assembly skeleton:** `Ashen.Generated`, `Domain` and `Content` (engine-free, with precompiled Newtonsoft), `Application` (namespace `Ashen.App`, D-027), `Platform`, `Presentation`, `EditorTools`, plus the EditMode and PlayMode test assemblies.
- **Shared tests** (D-032) in `Unity/Assets/Tests/EditMode/Shared`, run through `Tests/Domain.Tests` (net8, NUnit) and through Unity EditMode. `CodegenTests` passes on both runners: dotnet 2/2, Unity EditMode passed.
- **Environment:** **C: is full.** I deleted only my own temp extraction (205 MB). The NuGet and UPM caches now live in the repo's gitignored `.cache/` on D: (D-031), which has 4.9 GB free. **The owner needs to free space on C:.**

### us-0.3: content in JSON with schemas (2026-09-26)

- **Export** (`Tools/export-content.mjs`, read-only against `D:\repos\AshenSpire`) produces:
  - `configuredContentBundle(bundle, {})`, i.e. the bundle as the game runs it, with defaults the shipped code materializes at runtime (such as `combatRatings`);
  - the extra modules listed in `Tools/export/sources.json`;
  - `advancedConfigRows` (3,180 rows).
- **Reference config** goes through the shipped strict importer. A data-driven key map renames the legacy `derivedStatRules.rules.{ar,…}` keys to `combatRatings.ratings.*`. Only the values the importer rejects are set aside and applied directly (D-029): `startingCinders` 100 is above the shipped maximum of 20. Unexpressible keys (the owner's weighted opening hand, 3 prologue layout keys, player `settings.*`) are carried into the preset.
- **Transform** (`Tools/transform-content.mjs` with `Tools/transform.config.json`) produces:
  - 67 generated files: id-keyed catalogs, balance, rules, tags, UI, audio;
  - `strings/en.json` with 2,251 keys;
  - the `shipped` and `reference` presets. `reference` is a 1.7 KB merge-patch over 4 files plus 8 player settings;
  - a canonical manifest with a content hash.
- **Count gate:** green. Cards 195, relics 63, statuses 50, stances 3, enemies 33, encounters 35, events 25, flasks 7, classes 4, unlocks 18, class tree 24, armaments 28, armour 35, kits 8, weapon packages 28, seats 3.
- **Schemas:** 65, bootstrapped once by `Tools/infer-schemas.mjs`, with `x-ref` cross-references from `Tools/schema-refs.json`. They are now committed and authoritative.
- **CI scripts:** `Tools/ci/check-content.mjs` (manifest hashes, unlisted files, counts, a schema per file, AI disclosure) and `Tools/ci/check-docs.mjs` (links, Mermaid extraction; the workflow renders each block). Both are green locally.
- **Export gaps:** one real one, `scripts.wondrousDraught`, a scripted JS effect that needs a DSL port in F2. Four more are helper functions (logic, not data).
- **Effect ops in content:** 25. They become the `EffectOp` enum (us-0.9).
- **Decision (D-030):** content IDs stay **as shipped** (camelCase), because the web configs (`armament:straightSword`), saves and the oracle use them. This amends the `lower_snake_case` rule in 08 §3.
- **Not verified:** the Unity import of StreamingAssets (the next batchmode run creates the `.meta` files).

### Preflight (2026-09-26)

| Check | Result |
|---|---|
| Unity editor | `D:\Unity\6000.6.0f1\Editor\Unity.exe` ✓ |
| Unity licence | Unity Personal (ULF + Assigned), unlimited ✓ |
| Unity MCP tools | **Not available.** Using the batchmode fallback (D-023) |
| UPM cache | Global `cacheRoot` points at a missing `H:` drive; `UPM_CACHE_ROOT` is overridden in `Tools/unity.sh` (D-024) |
| Blender | 4.0 ✓ |
| Python | 3.11.9 in Git Bash (3.13 first on the Windows PATH); Pillow **not** installed |
| Node | 22.14.0 ✓ |
| .NET SDK | 8.0.303, 8.0.408, 9.0.305 ✓ |
| Edge | ✓ |
| gh | logged in as cehinds ✓ |
| Packages (bundled with the editor) | URP 17.6.0 · Input System 1.20.0 · Addressables 2.11.2 · Newtonsoft 3.2.2 · Test Framework 1.8.0 · Vector Graphics module (built-in) |

Branches: lane `feature/foundation/main`; current story branch `feature/foundation/us-0.9`.
