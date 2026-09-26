# Ashen Spire — Unity Rebuild, One-Session Mega Prompt

> Paste everything below the line into a fresh Claude Code session opened at `D:\repo\AshenedSpire`.
> The session runs **unattended to completion**. It never asks the user anything.

---

## 0. Mission and operating mode

You are the whole team: lead engineer, systems designer, UI engineer, technical artist, audio implementer and QA. In **this one session**, build **Ashen Spire** in **Unity 6000.6.0f1**:
- **All code is new.** The shipped web game is a behaviour reference and a data source only.
- **Content is the existing asset set plus AI-generated content of almost any kind**: art, music, SFX, voice, text (§3.1). **No licensed or third-party material.** The game **clearly states that it was made with AI**.
- **The game is data-driven end to end.** Designers must be able to rebalance, re-skin, re-word and re-flow the game by editing JSON and registered assets alone.

**Operating rules. Every one is mandatory.**

1. **Never ask the user for input.** When a decision is genuinely open:
   - pick the most reversible default consistent with the shipped game;
   - record it in `docs/DECISIONS.md` as `D-###` with *context · options · choice · how to reverse · status: provisional*;
   - continue.
2. **Never claim approval.** Only the owner approves. Never write "approved", "owner confirmed" or "signed off" about your own work. A previous project was burned by automated messages that falsely claimed approval.
3. **Git workflow.** Follow [10-BRANCHING-VERSIONING-CI](10-BRANCHING-VERSIONING-CI.md) exactly.
   - **One branch per user story:** `feature/<feature>/<story>` (for example `feature/combat-core/us-5.3`), cut from the lane `feature/<feature>/main`, which is cut from `dev`.
   - Commit after every green component with a conventional message citing the story ID and a `Co-Authored-By` trailer.
   - When a story is green, merge it `--no-ff` into its lane, then merge the lane into `dev` and bump with `Tools/version.mjs bump story`. When the lane carries the feature's final story, bump `feature` instead.
   - When a build phase (F0–F6) completes, the last commit on `dev` is `bump epic`. Then promote `dev → test → main` per 10 §5, and tag `main` `v<E.F.S.P>`.
   - The version scheme is `E.F.S.P`. The repo is at **0.0.1.0** (the design package).
   - Never force-push, never rebase shared branches, never skip a version check;
   - **never commit build outputs** (`Build/`, player builds, generated HTML) or Unity `Library/` files, because the Git LFS budget is exhausted.
4. **Always playable.** After each component, the game boots and every finished slice plays end-to-end. Never leave `dev` red. A story branch may be red only while its story is in progress.
5. **Build component by component, wireframe first.** For every screen and component, follow the loop in §7 exactly: data → domain → tests → view-model → UXML grey box → skin → screenshot → review.
6. **Truth order:**
   1. shipped code and data in `D:\repos\AshenSpire`;
   2. the design package in `docs/design/` (this repo);
   3. the shipped docs.

   Where the design package quotes a number, it is a **check value** for your import, not something to type into code.
7. **Context hygiene.** This is a long session:
   - delegate broad reads, conversions and reviews to sub-agents (§6.4) and keep only their conclusions;
   - write progress to `docs/BUILD-LOG.md` after every component, so the session can survive compaction;
   - re-read `docs/BUILD-LOG.md` and `docs/DECISIONS.md` after any context summary.

## 1. Inputs (read these first, in this order)

| # | Path | Why |
|---|---|---|
| 1 | `docs/design/01-PROMPT-REVIEW.md` §C | The **corrected** shipped rules and counts, and the lessons from the previous Unity attempt |
| 2 | `docs/design/02-SCOPE.md` | P0, P1, P2 and seams-only; the scope-cut ladder |
| 3 | `docs/design/08-DATA-CONVENTIONS.md` | The normative data and no-magic-strings contract |
| 4 | `docs/design/04-WIREFRAMES.md` | Every screen's blueprint and the build order |
| 5 | `docs/design/05-ACTIVITY-FLOWS.md`, `06-PROCESS-FLOWS.md`, `07-DIAGRAM-REVIEW.md` | Player flows, system flows and the review resolutions |
| 6 | `docs/design/03-FEATURES-AND-USER-STORIES.md` | Acceptance criteria. Cite story IDs in commits |
| 7 | `D:\repos\AshenSpire\docs\architecture-handoff\CURRENT-SPECIFICATION.md`, `RESPONSIVE-WIREFRAMES.md`, `docs\implementation\approved-wireframes.md` | Layout tie-breakers and dated owner decisions |
| 8 | `D:\repos\AshenSpire\docs\GDD.md` §1–§14, `IP-SCRUB.md`, `ENEMY-ROSTER.md`, `LORE*.md`, `WORLD-ATLAS.md` | Pillars, vocabulary and placement |
| 9 | `D:\repos\AshenSpire\src\content\**`, `content\source\**`, `content\framework\**` | **The data you import** |
| 10 | `D:\repos\AshenSpire\src\engine\**`, `src\model\**` | Behaviour reference. Read it to understand the rules; **never copy it** |
| 11 | `D:\repos\AshenSpire-Unity\docs\*`, `tools\unity-parity-import.mjs` | Lessons, and the parity-oracle technique |
| 11b | `docs/design/10-BRANCHING-VERSIONING-CI.md` | Branches (`feature/<feature>/main`, `feature/<feature>/<story>`), `E.F.S.P` versioning from 0.0.1.0, CI of at most 9 concurrent jobs, at most 9 test suites |
| 12 | `docs/design/reference/ashen-spire-game-config.json` | **The owner's current reference config** (web export format, `schemaVersion 1`, content 0.7.1). It is the game's **default active preset** (§4 step 6) |

## 2. Preflight (first 15 minutes; log the results in BUILD-LOG)

Run these checks and record each result.

| Check | Expected | If missing |
|---|---|---|
| Unity editor | `D:\Unity\6000.6.0f1\Editor\Unity.exe` | Search `C:\Program Files\Unity\Hub\Editor\*`. If no 6000.6 editor exists, use the newest 6000.x found and log a decision |
| Unity MCP tools | `ToolSearch("unity")` returns editor-control tools (scene, asset, script, console, tests, screenshot or menu execution) | Add the MCP for Unity package (`https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity`) to `Packages/manifest.json` for future sessions. **In this session use the batchmode fallback (§6.2).** If a `unity-cli` skill is listed, load it |
| Blender | `C:\Program Files\Blender Foundation\Blender 4.0\blender.exe` | Fall back to `pip install pillow` for WebP. Blender is otherwise optional |
| Python | 3.13.2 first on PATH; check `python -c "import PIL"` | Blender is the WebP path; `pip install --user pillow` is the second choice |
| Unity modules and licence | Windows (IL2CPP and Mono), Android (OpenJDK, SDK, NDK) and Web build support installed; Unity Personal active (reported 2026-09-26) | Batchmode needs an active licence: run `Unity.Licensing.Client.exe --showEntitlements`, and if none is found, stop the build steps and report it |
| Node | 22.x | Required for content export (§4) |
| .NET SDK | 9.x | Optional fast Domain tests outside Unity |
| Edge | `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe` | SVG rasterize fallback (`--headless --screenshot`) |
| gh | Logged in | Used to read CI results and set the `UNITY_*` secret status. Merges are local `git merge --no-ff` plus push. If gh isn't logged in, gate on local runs and say so in the final report |

Then:
- Create the Unity project at `Unity/`: URP 2D template, or empty plus the URP package.
- Packages:
  - `com.unity.render-pipelines.universal`
  - `com.unity.inputsystem`
  - `com.unity.addressables`
  - `com.unity.test-framework`
  - `com.unity.nuget.newtonsoft-json`
  - `com.unity.2d.sprite`
  - `com.unity.vectorgraphics` (if it resolves for this editor; otherwise log a decision and use the SVG fallbacks)
  - the MCP bridge
- Player settings:
  - Active Input Handling = Input System;
  - Scripting Backend = IL2CPP for the player, Mono in the editor;
  - API Compatibility = .NET Standard 2.1;
  - Color Space = Linear.
- Create `.gitignore` (Library, Temp, Logs, UserSettings, Build, **`Unity/Assets/Art/Imported/**/*.png`**) and `.gitattributes` (`*.json text eol=lf`; **no LFS**).
- The converted PNGs are **generated, not committed**. The lossless conversion of 192 MB of WebP would bloat git, and the LFS budget is exhausted. **Commit their `.meta` files**, so GUIDs stay stable for atlases and Addressables. `Tools/import-assets` rebuilds the PNGs from `D:\repos\AshenSpire` deterministically. Log this as D-018.

## 3. Non-negotiable rules

### 3.1 Assets and AI generation (owner ruling, 2026-09-26)

**Generating almost anything is allowed. Licensed third-party material is not, and the game must say clearly that AI was used to make it.**

- **Allowed sources:**
  - the existing files under `D:\repos\AshenSpire\assets\**` and `art\**`, which were themselves AI-generated;
  - **new AI-generated content of any kind**: art, UI art, icons, a logo, backdrops, VFX frames, **music, SFX, ambience, voice/narration**, fonts or typefaces if a generator can produce them, and text and lore that fit the IP-scrub vocabulary;
  - procedural content (synth audio, `Painter2D`, particles, shaders);
  - engine-bundled defaults that ship inside Unity itself (the UI Toolkit default font, built-in shaders).
- **Forbidden: licensed or third-party material.**
  - No stock or marketplace assets.
  - No downloaded fonts, including OFL or other free licences.
  - No music, samples or sound libraries.
  - No Asset Store content.
  - No copyrighted characters, names, logos or likenesses, including anything from FromSoftware.
  - No prompts that name living artists or copyrighted works.
- **Generation rules:**
  - **Reuse first.** Generate when no existing asset fits, or when a generated asset clearly beats a placeholder or a procedural stand-in.
  - **Match the style.** Pass 1–3 existing assets of the same domain as references. Keep that domain's canvas size, facing, ground line and transparent background from its manifest (for example, enemy states are 384×384, ground at 364, facing left). For music, keep one consistent palette across contexts (dark-fantasy, restrained, loopable).
  - **Use any available generation tool.** Discover them with `ToolSearch` (for example "generate image", "generate music", "generate speech"). If a modality has no tool, use the procedural fallback (synth audio, typographic treatment).
  - **Provenance for every generated file.** Record it in `Provenance/generated/manifest.json` (asset ID, modality, tool, model, prompt, references, date, file hash) and tag the registry entry `origin: generated`.
  - **Where it goes.** Generated files land in `Unity/Assets/Generated/{Art,Audio,Fonts}/` and are **committed**, because they can't be regenerated deterministically. Compress audio (Vorbis) and images. Keep the total under 150 MB; if it would go over, prefer lower bitrates and shorter loops.
- **The AI disclosure is mandatory and prominent** (US-1.5, US-0.10):
  - **Wording** (string key `about.aiDisclosure`): "Ashen Spire was created with generative AI. Its code, art, music, sound, text and design were produced with AI tools under human direction." Adjust the list of modalities to what was actually used.
  - **Title screen:** a short line in the footer, always visible, `{title.aiDisclosure}`.
  - **First launch:** a one-time notice (W2 frame, single `[Continue]`) with the full disclosure. It is recorded in the profile so it is shown once.
  - **Settings → About:** the full disclosure, plus a generated **"Made with AI" credits list** grouped by modality, taken from `Provenance/generated/manifest.json` and the old `prompts*.json` files.
  - **Outside the game:** `README.md`, `build-info.json` and the Windows build's `ABOUT.txt` carry the same statement.
  - **Enforced by the test `AiDisclosurePresent`:** the string key exists; the title, first-launch and About views bind it; the About list is non-empty; `README.md` contains the statement.
- **Gaps.** When content is missing, use the first of these that works:
  - reuse the closest existing asset;
  - generate a style-matched asset;
  - use a procedural stand-in;
  - give it a typographic treatment;
  - use `framework/missing.svg`.

  Log every gap and every generated asset in `docs/ASSET-GAPS.md`. **Never block a feature on missing content.**
- **Known gaps, logged on day one:**
  - **no fonts:** use Unity's bundled default font; a generated display typeface is optional;
  - **no audio files:**
    - generate music beds for the 8 contexts and key stingers (victory, defeat, boss);
    - SFX may be generated or synthesized;
    - the procedural synth (§5.4) is the always-available fallback and the default for UI blips;
  - no Reaver readiness poses: generate them, or fall back to `STANCE-READY`;
  - `greatsword` and `sword-shield` have no `CONVERSATION` frame: generate it, or fall back to `PORTRAIT`;
  - **no painted icons for 51 of the 63 relics:** generate them in the style of the 12 existing relic paintings (sizes 64, 128, 256 and 512, as in `art/relic-icons-pack-0*`); typographic sigil chips are the fallback;
  - **no logo or wordmark:** generate one (IP-safe), or use the typographic fallback.
- **Provenance of existing assets.** Copy `CREDITS.md`, every `provenance.json` and every `prompts*.json` into `Provenance/`.
- **Vocabulary.** Use only the post-scrub terms from `docs/IP-SCRUB.md` (cinders, Reaver, Starseer, Herald, Goldbough, Crimson Blight), in game text and in every generation prompt.

### 3.2 Data-driven, with no magic strings
Implement [08-DATA-CONVENTIONS](08-DATA-CONVENTIONS.md) exactly:
- content JSON in `StreamingAssets/Content`, with schemas, codegen and config layers;
- the effect DSL and formula objects;
- string tables and UI registries;
- **all ten enforcement tests in 08 §12**, created in the first hour so that every later component is policed.

In C#, content IDs, tuning numbers, user-facing strings, UXML element names and schema keys typed as literals are all defects.

### 3.3 Determinism
- The Domain and Application assemblies have `noEngineReferences: true`.
- Maths is integer or fixed-point in hundredths. No `float` or `double` in Domain. Use `CultureInfo.InvariantCulture` everywhere.
- Collections are ordered: no iteration over `Dictionary` or `HashSet` in rule code.
- The PRNG is **mulberry32**, with the shipped named streams: `map, shuffle, cardRewards, relicRewards, flaskRewards, armaments, enemyAI, enemyHP, events, shop, misc, smith, combatProcs, seats`. Derive streams exactly as `src/engine/rng.js` does, so that **a seed gives the same seat order and map as the web build**. A parity test proves it.
- Time, frame rate and animation never affect rules. The Presentation layer only observes events.

### 3.4 Saves
- Location: `Application.persistentDataPath`.
- **3 run slots, a profile, and a verified mirror of each.** Write to `.tmp`, flush, read back and verify the hash, then `File.Replace` with a backup, then refresh the mirror.
- The run save is a **snapshot plus a command log**: checkpoint at every commit point listed in PF-06, and append commands in between.
- Each save carries `schemaVersion`, `contentHash` and `configSnapshotHash`. Apply migrations in order. A newer save is **refused but kept**.
- The save holds: seat order, `towerLit` per seat, the `runChoices` ledger, RNG cursors, pending rewards, merchant stock, prologue scene index and the camera state.

## 4. Content import (make the shipped data the source of truth)

1. **`Tools/export-content.mjs`** (Node 22):
   - `import()` `D:\repos\AshenSpire\src\content\index.js` to get `contentBundle`;
   - also the generated modules (`src/content/generated/*.js`, `src/framework/data/*.js`), `mechanics.json`, the CSV and JSON sources, and `advancedConfigRows(bundle)` from `src/model/advancedConfig.js`;
   - write **raw** JSON to `Tools/export/raw/*.json`;
   - list every non-serializable field (functions, class instances, Symbols) in `Tools/export/export-gaps.json`. The build fails until each one is mapped to the DSL (PF-10).

   The script only reads the old repo; it never writes to it.
2. **`Tools/transform-content.mjs`** maps raw rows into the new schemas (`StreamingAssets/Content/**`):
   - catalogs are written as **id-keyed objects**, not arrays, so that merge-patch overrides touch one row (08 §8);
   - output is canonical JSON: sorted keys, LF line endings, no BOM;
   - IDs are converted to `lower_snake_case`, with `legacyId` kept;
   - numbers are converted to hundredths where 08 §5 requires it;
   - behaviour is re-expressed in the effect DSL.
   - Any shipped effect that doesn't map to an existing op becomes a **new op** (schema enum → codegen → `IEffectOp` → unit test). Never special-case a content ID.
3. **Count gate.** A test asserts that the imported counts equal the shipped counts:
   - cards 195 (40 per class × 4, 20 colorless, 7 armament, 3 co-op, plus the starter basics);
   - enemies 33; encounters 35; statuses 50; stances 3; relics 63 (8/16/19/14/6); flasks 7; events 25;
   - weapons 28 and weapon packages 28; armour 35; starting kits 8;
   - legacy dungeons 3 (12 scenes); world nodes 200 plus 91 local, 571 edges, 6 quests;
   - class tree 24; unlocks 18; Advanced settings 3,180 rows.

   If a count differs, the export is wrong. Fix the export and never edit the numbers.
4. **Parity oracle.** Following the old Unity repo's `unity-parity-import.mjs` technique, **`Tools/oracle.mjs`** calls the shipped JS functions to produce `Unity/Assets/Tests/Oracle/*.json`:
   - derived stats for every attribute combination on the lean scale × levels 1–10;
   - ratings;
   - `computeAttackDamage` for a sampled grid of cards × statuses × targets;
   - Block, Dodge odds, meter fill and break, and proc bursts;
   - map generation for 20 seeds (node types and edges);
   - seat order for 50 seeds;
   - hand rules.

   The EditMode tests assert that the C# output equals the oracle. This is how you prove the rules without copying code.
6. **Reference config (the owner's tuning; the default preset).** `docs/design/reference/ashen-spire-game-config.json` holds 330 override keys in the web format (`gameConfig.<path>` plus `settings.<key>`; each `settings.gameConfig.*` key duplicates its `gameConfig.*` key).
   - `Tools/export-content.mjs` **runs the shipped import path**: `parseAdvancedConfigFile(text, bundle)`, then `configuredContentBundle(bundle, settings)` from `src/model/advancedConfig.js`. That applies the shipped key migrations, retired keys and floors exactly as the web build does. The **effective** bundle is exported alongside the default bundle.
   - `Tools/transform-content.mjs` emits the difference as `StreamingAssets/Content/settings/presets/reference.json`: an id-keyed patch in the **new** schema paths, with a mapping table `Tools/export/key-map.json` (web key → new path). Any unmapped key **fails the build**; it is never dropped silently.
   - Player-facing `settings.*` keys become the defaults in `settings/defaults.json` for new profiles: `holdConfirm: short`, `rewardCollect: manual`, `shrineMultiUse: true`, `useRestorativeFlasksOutsideCombat: true`, `showPlayedCard: true`, `walkedFade: strong`, `cardWidth_focus: 300`, `cardWidth_glance: 250`.
   - Presets `shipped` (no overrides) and `reference` both ship. **`reference` is active by default** (D-019). Settings → Advanced can switch presets.
   - Settings → Saves can **import and export configs in the same web format**, so owner files round-trip between the two builds. Web keys are translated through `key-map.json`.
   - The oracle (step 4) is generated for **both** presets, and the parity suite runs both.
7. **Strings.** Extract every display string (card names and text, statuses, events, UI strings from `content/source/uiStrings.csv`, the prologue lines from the opening config) into `strings/en.json`, keyed by the 08 §9 convention. Card text becomes templates.

## 5. Architecture (build to this; the details are in 08 and 06)

### 5.1 Assemblies
`Ashen.Domain` → `Ashen.Application` → `Ashen.Content` → `Ashen.Presentation` → `Ashen.Platform`, plus `Ashen.Generated`, `Ashen.Editor` and the `Ashen.Tests.*` assemblies. Dependencies point one way only; an asmdef test checks it.

### 5.2 Core services
| Service | Layer | Responsibility |
|---|---|---|
| `Rng`, `RngStreams` | Domain | mulberry32 with named streams; cursors are serializable |
| `FormulaEvaluator` | Domain | Derived stats, ratings, hand rules, XP, prices, from structured formulas |
| `EffectInterpreter` + `IEffectOp` registry | Domain | Everything cards, relics, statuses, stances, enemies, events and places do |
| `Legality` | Domain | Costs, targets, grip, deck floor. Returns a refusal key |
| `Resolver` + `Preview` | Domain | One code path. `Preview` = `Resolver` with `dryRun` |
| `EnemyBrain` | Domain | Weights, `maxConsecutive`, `firstMove`, phases, locked moves, charged moves |
| `MapGenerator`, `AtlasRouter` | Domain | Classic maps; World Journey routes |
| `RunStateMachine`, `CombatStateMachine` | Application | Screens are states; transitions come from `ui/screens.json` and flow data |
| `CommandBus`, `EventBus` | Application | PF-03 |
| `SaveService` | Application + Platform | PF-06 |
| `ContentDb`, `Validator`, `ConfigLayers`, `AssetRegistry` | Content | PF-02 and 08 |
| `Navigator`, `ViewModel` base, component kit | Presentation | PF-08 and the 04 kit table |
| `AnimationDirector` | Presentation | Pose suite by grip, state frames, VFX sequences, reduced motion |
| `AudioRouter` + `SynthAudio` | Presentation | Generated tracks through the context file slots; procedural beds and SFX as the fallback (§5.4) |
| `InputRouter` | Platform | One focus model for mouse, keyboard, pad and touch; Input System actions with generated IDs |

### 5.3 UI Toolkit patterns
- **One UXML per screen** (`UI/Screens/<ScreenId>.uxml`) and one per kit component (`UI/Kit/*.uxml`).
- USS reads only the generated `Tokens.uss` variables.
- View-models expose bindable properties through Unity 6 runtime data binding, or a thin `INotifyBindablePropertyChanged` implementation. Views never call Domain directly.
- **Responsive behaviour** is class-driven:
  - the `Navigator` sets `.band-standard`, `.band-compact` or `.layout-narrow` on the root from `ui/layout.json`;
  - a screen swaps its rail for a selector (Rule 11) by class toggle, never by a second UXML.
- **Two PanelSettings**, wide (1280×720) and narrow (430×780), both Scale With Screen Size. The `Navigator` swaps them by layout mode.
- Physical minimums (`layout.json#minPhysical`) are enforced after scaling.
- Rule 11's rail-or-selector choice comes from a `CategoryNav` model (width, height, category count, tap floor, hysteresis), per screen.
- Text elements are a `[UxmlElement] LocLabel` with a `stringKey` attribute, because Unity 6 drops undeclared UXML attributes.
- Focus: every screen's `focusOrder` from `screens.json` is applied by the `InputRouter`. Pad and keyboard navigation work on every screen from day one.

### 5.4 Audio: generated tracks plus a procedural synth (no audio files exist today)
- **Generated audio.** Music beds for the 8 contexts, stingers, and optionally SFX and narration are generated (§3.1). They are imported as Vorbis `AudioClip`s, registered with `origin: generated`, and assigned through the **file slot** of each context in `audio/contexts.json`. The registry resolves the file slot first.
- **Procedural synth.** It is always built. It is the fallback when a slot is empty or its file is missing, and the default for UI blips:
- Port the **vocabulary**, not the code, of `src/content/sfx.js` and the music beds (`src/content/music.js` or equivalent):
  - `tone` layers: type, freq, glide target, dur, peak, t0;
  - `noise` layers: dur, peak, t0, high-pass, low-pass.
- `SynthAudio` renders recipes to PCM `AudioClip`s at load time (`AudioClip.Create`, 44.1 kHz) and caches them.
- Music beds loop.
- Recipe resolution is family-then-specific (`procBurst_frost` → `procBurst` → `default`), and `default` is audible.
- `audio/contexts.json` maps the 8 contexts (title, map, combat, elite, boss, shop, rest, victory) to a bed or `"silence"`. Each context has an optional file slot, empty today, which the registry would resolve first.

## 6. Tools contract

### 6.1 With the Unity MCP (preferred)
Use it for:
- creating and modifying assets, scenes and prefabs, and PanelSettings;
- triggering script compilation and reading the **console** after every change (zero errors, and no new warnings in your code);
- running EditMode and PlayMode **tests**;
- entering play mode;
- **taking Game-view screenshots** at 1280×720, 1920×1080, 844×390 (compact) and 390×844 (portrait);
- executing `Ashen/*` menu items (codegen, validate, import, atlas, bot).

Discover the exact tool names with `ToolSearch("unity")` and record them in BUILD-LOG.

### 6.2 Without the MCP (batchmode fallback, always available)
- `"D:\Unity\6000.6.0f1\Editor\Unity.exe" -batchmode -projectPath Unity -executeMethod Ashen.Editor.Cli.<Method> -logFile Logs/<m>.log -quit`
- Provide CLI entry points for: `ImportAssets`, `Codegen`, `ValidateContent`, `BuildAtlases`, `BuildAddressables`, `CaptureScreens`, `RunBot`, `BuildWindows`.
- Tests: `-runTests -testPlatform EditMode|PlayMode -testResults Logs/<p>.xml`.
- **Screenshots:** `CaptureScreens` loads each screen with a fixture state from `Tests/Fixtures/screens/*.json`, renders into a RenderTexture at each target size, and writes `docs/screens/<screen>_<size>.png`. Run it without `-nographics`.
- Parse the logs yourself and fail loudly on errors.

### 6.3 Blender 4.0 (headless; transformation only)
- `blender -b -P Tools/blender/webp_to_png.py -- <src> <dst>` batch-converts WebP to PNG losslessly, keeping alpha. This is the primary converter.
- **SVG order:** Vector Graphics import → `msedge --headless --screenshot` at 2× the reference size → `Tools/blender/svg_to_png.py` (the built-in SVG add-on, rendered orthographic and transparent). Record which one was used in DECISIONS. Only 6 SVG files exist.
- Optional: compose existing backdrop layers (`art/webp-maps-*/scene-layers`) into parallax quads.
- **Never model, sculpt or paint new content.** Log every Blender use in `docs/ASSET-INVENTORY.md#derivations`.

### 6.4 Sub-agents (use them; keep the main context lean)
Run independent work in parallel. Give every sub-agent a self-contained brief with file paths and a required output format.

| When | Sub-agent task |
|---|---|
| Content import | One agent per table family (cards, enemies and encounters, relics and statuses and stances, events and quests, equipment, atlas) writes its transform and schema, then reports counts |
| After each screen | A **screenshot reviewer** compares `docs/screens/<screen>_*.png` with the 04 wireframe and the story acceptance criteria. It returns findings: blocker, major or minor |
| After each epic | A **code reviewer** checks for magic strings and numbers, layer violations, determinism hazards, and preview/resolve divergence |
| Rules | A **parity reviewer** compares a subsystem with the shipped engine and extends the oracle where coverage is thin |
| End | A **fresh-eyes playtester** runs the bot report and 10 seeded manual-script runs from `Tests/Scripts/*.json`, then files findings |

Fix every blocker and major finding before moving on. Log minor ones in `docs/BACKLOG.md`.

## 7. The component loop (repeat for every component and screen)

```
1. DATA     schema (+ codegen) and content rows; the validator is green
2. DOMAIN   pure rules plus unit tests (oracle-backed where a formula exists)
3. APP      commands, events, state-machine states; tests for transitions and save round-trip
4. VM       view-model exposing only display-ready values (strings resolved, previews computed by Domain)
5. GREYBOX  UXML matching the 04 wireframe regions exactly (named per UiNames); tokens only; no art
6. BIND     wire the VM; keyboard, pad and touch focus order from screens.json; the PlayMode smoke test passes
7. SKIN     registry sprites, VFX, pose suites, synth SFX; reduced-motion path
8. SHOOT    screenshots at 4 sizes → docs/screens/
9. REVIEW   screenshot-review sub-agent + self-check against the story ACs → fix
10. COMMIT  update BUILD-LOG, DECISIONS, ASSET-GAPS and CHANGELOG; commit citing US-ids
```

A component is **done** only when steps 1–10 are complete and all tests, including the 08 §12 enforcement tests, are green.

## 8. Build plan (execute in order; each phase ends playable)

| Phase | Components (04 build order, 03 stories) | Exit gate |
|---|---|---|
| **F0 Foundation** | Preflight; project and packages; asmdefs (with `Ashen.Codegen` isolated); enforcement tests; codegen; content export, transform and count gate plus the oracle (PF-10); validator; config layers; registry; asset pipeline (PF-01: postprocessor first, then WebP → PNG, SVG, atlases, Addressables); PRNG plus the seat and map parity tests; SaveService (PF-06: gen-stamped snapshot, CRC log, mirror); `SynthAudio` core (PF-12); `docs/ASSET-INVENTORY.md` with counts | Validator green; counts match; oracle tests for RNG, map and seats green; registry has no dangling IDs |
| **F1 First playable loop** | Kit (Shell, Workspace, CategoryNav, Confirm, Meter, CardView, HandFan, FooterGroup, Tooltip, HoldButton, Refusal), W-24 boot and error states → W-02 title → W-03 slots → W-04 Class pane with a default character → W-07 combat (one Weald encounter) → W-08 rewards → W-23 confirmations → W-20 pause (Save & quit) → checkpoint and resume | Boot → fight → reward → quit → resume mid-fight works with mouse, keyboard and pad |
| **F2 Combat complete** | The full combat rules from 01 §C3: costs, pools, hand modes, damage order, Block, Dodge Roll, meters and breaks, Arcane Exposure, procs, DoTs, Madness, stances, enemy brain, intents, targeting, piles, Armaments popover and re-arming, Potions, inspectors (W-17), state frames and VFX, AnimationDirector with suites by grip | Oracle damage and preview tests green; `PreviewEqualsResolve` green; the 33 enemies spawn with correct art |
| **F3 Classic Climb** | Remaining creation panes and the prologue (W-05), act map (W-06), node resolution, rest, smith, extract/install, level up (W-10), merchant (W-09), events and dialogue (W-11), legacy dungeons (W-13), boss destinations, the causeway, victory rules and the post-victory hook, run end (W-15), pause (W-20), permadeath | A seeded run from title to an act-3 boss win and to a death; resume works at every commit point |
| **F4 Build and profile** | Equipment and the Armoury (W-12), weapon packages and lent cards, smithing tiers, XP, skill tracks, class tree and progression screens (W-22), deck floor, profile, unlocks, journal (W-16), settings (W-18) generated from the schema, recovery (W-01), all 4 classes × outfits × tints | Every art set referenced; bot runs 200 Classic seeds clean |
| **F5 P1** | World Journey (W-14), inn and chapel, quest board and journal, Custom Run (W-19), Collection, accessibility pass | Bot runs 50 Journey seeds clean |
| **F6 Ship** | Performance pass (60 fps combat, first input under 3 s), Windows build via PF-11 (`Build/Windows/`, not committed; player smoke replays golden_01), fresh-eyes playtest, final docs, `bump epic`, promotion `dev → test → main` with tag | §11 definition of done |

When time or context runs short, apply the **scope-cut ladder** in 02 §4, from the top. Never cut the floor items.

## 9. Pre-made provisional decisions (log them as D-001… at start)

| ID | Decision |
|---|---|
| D-001 | Reference resolution 1280×720. Height bands 465 and 340 and the narrow mode at 520 are **ported from the shipped CSS**, in data |
| D-002 | Dodge modifier uses the shipped `⌊(DEX−10)/2⌋` (`dexPivot = 10`) even though it is always negative on the lean scale. Flag it for owner balance review |
| D-003 | Hand rules use the shipped `drawMode: fill` (opening 3, capacity 10). Other modes remain available through Advanced settings |
| D-004 | Title menu order: Continue · Load · New · Journal · Custom Run · Settings · Quit. Credits live in Settings → About |
| D-005 | Fonts: Unity's bundled default font (an engine component, not a third-party asset). A generated display typeface is optional. **No downloaded fonts, not even OFL ones** (owner: no licensed material) |
| D-006 | Audio: generated music for the 8 contexts and stingers goes into the file slots. The procedural synth is the fallback and handles UI blips. If no audio-generation tool is available, everything stays synth and the gap is logged |
| D-007 | Co-op, the 4th tower and the pending rule ideas are seams only (02 §3) |
| D-008 | WebGL build is deferred because of the known UI Toolkit renderer bug in 6000.6.0f1. Windows first, then Android as P2 |
| D-009 | *(The reference preset sets `shrineMultiUse: true`, so the default experience shows Continue.)* Single-use places keep the shipped rule: committing Rest, Smith, Extract or Install ends the visit, and Continue appears only for multi-use or when a relic forbids Rest. The validator guarantees every place yields an exit. The owner question "should single-use places get a Leave?" stays open |
| D-010 | The weapon-set swap lives in the Armaments popover opened from the player figure. The shipped combat screen had no confirmed set-swap control |
| D-011 | Relics without paintings get generated, style-matched icons. Typographic sigil chips are the fallback if generation is unavailable |
| D-012 | Unity folder layout per 08 §2. The Unity project lives at `Unity/`, and tools live at the repo root in `Tools/` |
| D-013 | Potions stay in the combat footer and the map corner. In rooms, "flasks outside combat" is reached through `[Menu]` → Potions. Owner decision pending |
| D-014 | Extract moves a card into the deck, as shipped. Whether it should go to storage is logged as an owner question |
| D-015 | The XP strip in the RUN_HUD sits behind `ui/runHud.json#showXp`, default off (open owner decision WGH5) |
| D-016 | Two PanelSettings (wide 1280×720, narrow 430×780), with physical minimums enforced after scaling |
| D-019 | The owner's reference config (`docs/design/reference/ashen-spire-game-config.json`) is the **default active preset**. Shipped defaults remain as the `shipped` preset. Where 01 §C3 check values differ, the reference preset wins at runtime and the oracle covers both |
| D-020 | The reference config hides the prologue Pause and Skip controls (`showPause`/`showSkip: false`). Kept as configured; hold-to-skip stays reachable from the keyboard and pad Back action, so the prologue is never unskippable |
| D-021 | Feature lanes are `feature/<feature>/main` because git can't hold both `feature/x` and `feature/x/us-…`. Story branches are `feature/<feature>/<story>` |
| D-022 | `E.F.S.P` versioning from 0.0.1.0. Only merges into `dev` bump (single writer, via `Tools/version.mjs`); promotions don't. `main` is tagged on every promotion |
| D-018 | Generated PNGs are gitignored and their `.meta` files committed. The pipeline regenerates them from the source repo |
| D-017 | Where a save's command log cannot be replayed (version or content changed), resume from the snapshot and warn. At most one combat turn is lost |

## 10. Quality bar (all automated; results in `docs/qa/`)

**CI and test limits (10 §3–4):**
- One workflow, `.github/workflows/ci.yml`, with **8 independent, concurrent jobs**: version, docs, content, domain, editmode, playmode, bot, build-windows. There is no `needs:`, and each job has its own concurrency group and cache.
- **Suites:** 9 (Rules, Parity, Replay, Saves, Enforcement, Content, Presentation, Smoke, Bot).
- The target is under 10 of each, with a hard cap of 20. Log any addition in DECISIONS.
- Unity jobs skip with a notice if the `UNITY_*` secrets are missing; local batchmode runs are then the gate.
- Create `Tools/version.mjs` and `ci.yml` during F0.


- **EditMode:**
  - formulas, damage order, Block, Dodge odds, meters and breaks, procs, DoTs, stances, costs, hand modes, map rules, the enemy brain, legality;
  - the oracle parity suite;
  - the validator and the count gate;
  - the 08 §12 enforcement tests;
  - save round-trip and migrations;
  - golden replays for 10 seeds.
- **PlayMode:**
  - boot → creation → prologue skip → fight → rewards → merchant → rest and level up → save and reload mid-fight → boss;
  - a focus-order walk of every screen with keyboard and pad.
- **Bot** (`Ashen/Bot`): random legal commands with a seeded policy. P0 is 200 Classic seeds and P1 adds 50 Journey seeds. Zero exceptions, zero softlocks (no legal command and no terminal state), and a report of median turns and deaths by encounter.
- **Screens:** every screen at 4 sizes in `docs/screens/`, each reviewed by a sub-agent.
- **Performance:** a Profiler capture of a 5-enemy combat. Aim for 60 fps, no GC allocations per frame in combat, and first input in under 3 s.

## 11. Definition of done and final report

**Done** means all of the following hold:
- the P0 stories in 03 are met, or explicitly cut per the ladder with a logged reason;
- every test and enforcement check is green;
- the bot is clean;
- a Windows build exists;
- the docs are updated: `BUILD-LOG`, `DECISIONS`, `ASSET-GAPS`, `ASSET-INVENTORY`, `CHANGELOG`, `docs/qa/*` and `docs/screens/*`;
- every completed epic release has been promoted `dev → test → main` and tagged `v<E.F.S.P>`, and `VERSION` and `CHANGELOG.md` agree.

**Final message to the user:**
1. What shipped, per epic, with story IDs.
2. What was cut and why.
3. Test, bot and performance numbers, with failures quoted verbatim.
4. The provisional decisions that need an owner ruling.
5. The asset gaps, and **a list of everything that was AI-generated** (by modality), with the location of the disclosure (title, first launch, About, README).
6. What was **not** verified: for example physical Android devices, audio quality by ear, and long play sessions.
7. The final version and tags, and the branches merged.

Do not claim anything you did not verify.

## Appendix A — Check values (for verifying the import; never type these into code)

The corrected shipped values are in 01 §C3. **The active `reference` preset overrides some of them** (see the table below). Test both.

| Key (web path) | `shipped` | `reference` (default) |
|---|---|---|
| `balance.startingCinders` | 0 | **100** |
| `balance.rewards.cinders` normal / elite / boss | 45–75 / 105–150 / 225–270 | **75–150 / 200–300 / 400–600** |
| `balance.level.xp` base / growth | per shipped curve | **100 / 1.8** |
| `derivedStatRules.rules.ar` (S/D/C/W/I) | .5/0/0/0/0 | **.5/.25/.25/.13/.12** |
| `derivedStatRules.rules.dr` | 0/.5/0/0/0 | **.25/.75/.25/.13/.12** |
| `derivedStatRules.rules.pr` | 0/0/0/.5/.5 | **0/.05/.05/.5/.4** |
| `derivedStatRules.rules.poise` | 1 + C + .5S | **1 + .5C + .25S + .25W + .1I** |
| `derivedStatRules.rules.ward` | 1 + W + .5I | **1 + .1D + .1C + .3W + .5I** |
| `derivedStatRules.rules.openingHand` | 3 (INT-based) | **3 + .1 DEX + .25 WIS + .1/level, min 3, max 20** |
| `combatRatings.breaks.recoveryPerTurn` | 0 | **1** |
| `classes.reaver.startingFlaskAllocation.hp` | 3 | **2** |
| `balance.skill` draftSize · class tierAt · rarityUnlock | shipped | **4 · 3/5/10 · common 3, uncommon 5, rare 10** |
| `balance.smithing` shrine smith chance · stones by pool | shipped | **100% · boss 3, treasure 1, normal 0** |
| `balance.shop` stock and prices | shipped | armaments 5 (100–180 common, 360 uncommon), flasks 4 (75–240), weapon arts 4, cards 70/150, relics 200–300/400–500 |
| Defence card bonuses (defend, brace, backstep, …) | shipped | backstep 6, brace 4, bracingStance 6, defend 4, enterBulwark 5, evasiveGuard 5, ironResolve 7, pocketSand 5, quickstep 5, technique 5 |
| Prologue | shipped order | warmth 1 → year 2 → carry 3 → night 4 → step 5, extras A–D 6–9. Caption layout, typewriter reveal, per-scene staging, music title/boss/rest |
| `settings.*` | shipped defaults | holdConfirm short · rewardCollect manual · shrineMultiUse **on** · flasks outside combat on · showPlayedCard · walkedFade strong |

Shipped values (the `shipped` preset):

- Lean attributes 1–4, baseline 1, 3 points. Presets: Reaver 3/1/2/1/1, Rogue 1/3/2/1/1, Herald 1/1/2/3/1, Starseer 1/1/1/2/3.
- HP = 30 + 4·CON (+5 per 5 levels). Stamina = 1 + CON. Mana = 1 + WIS. Actions = 3 + ⌊DEX/5⌋.
- **HP, Mana and Stamina carry over between fights.** +1 Stamina at player turn end only if none was spent that turn. Hand: opening 3, fill to capacity 10, retain on, overflow keep.
- Turn start: clear Block → Actions (minus stagger loss) → draw → Madness and DoT hooks. Charged moves run `whileCharging` once. HP phases fire on HP change.
- AR .5 STR · DR .5 DEX · PR .5 WIS + .5 INT · Poise 1 + CON + .5 STR · Ward 1 + WIS + .5 INT.
- Weight capacity = 2·CON + STR. Light ≤49%, Medium ≤79%. Dodge costs 1/0, 2/1, 3/2.
- Damage steps 1–9 as in PF-04. Resistance `1 − min(0.8, R/(R+100))`, where R is the target's Poise or Ward rating. Exposures apply only on tagged hits.
- Procs:
  - Bleed 7 / 15% (8–35) / +3 poise;
  - Frost 10 / 8% (4–20) → Weak + frostExposed;
  - Insanity 14 / 18% (10–40) / +8 poise + stagger;
  - Resist ×0.5 for 2 turns on tagged creature types.
- Map 12×7, 6 paths. Weights 45/22/12/8/5. Unknown roll 55/25/12 (shrine)/8.
- Cinders 45–75 / 105–150 / 225–270, starting cinders 0. Removal 225 +75. Duplicate armament = 40 cinders.
- XP: win 50, kills 25/75/200, quest 125. Deck floor = 8 + ⌊level/2⌋.
- Flask pool 4, with allocations Reaver 3/1, Starseer 2/2, Rogue 3/1, Herald 3/1. Crimson heals 25% HP; Azure gives +1 Mana.
- Starting deck 11 (4/4/1/1/1). Re-arm costs 2 Actions.

## Appendix B — Example data shapes

```json
// catalog/cards.json (row)
{ "id": "cleave", "legacyId": "cleave", "pool": "reaver", "rarity": "common", "type": "attack",
  "cost": { "actions": 1, "stamina": 1, "mana": 0 }, "keywords": [], "tags": ["physical"],
  "art": "card.cleave", "vfx": "vfx.slash",
  "effects": [ { "trigger": "onPlay", "effects": [
      { "op": "damage", "target": "allEnemies", "amount": { "const": 8 }, "school": "physical",
        "ratingCap": 4, "impact": { "weaponWeight": true } } ] } ] }

// ui/screens.json (row)
{ "id": "combat", "uxml": "UI/Screens/Combat", "viewModel": "CombatViewModel", "shell": "W4a",
  "bands": { "standard": [10,55,30,5], "compact": "rails" }, "mins": { "hand": 208, "footer": 56 },
  "focusOrder": ["HAND","TARGETS","FOOTER_GROUP","RUN_HUD"], "back": "pause", "audioContext": "combat" }

// audio/sfx.json (row)
{ "id": "procBurst", "layers": [ { "kind": "noise", "dur": 0.18, "peak": 0.5, "lp": 1800 },
                                 { "kind": "tone", "type": "triangle", "freq": 220, "to": 110, "dur": 0.25 } ] }
```

## Appendix C — Screen registry (IDs used across all docs)

`boot, contentError, profileRecovery, title, slots, creation, prologue, actMap, combat, rewards, merchant, rest, event, dialogue, armoury, progression, legacyDungeon, worldMap, localMap, town, runEnd, journal, inspector, pileViewer, settings, customRun, pause, uprightGate, lobby (reserved, disabled)`

Smith, Extract and Install are sub-workspaces (W1i/j/k) of `rest`, not screens.

**Begin with preflight (§2), then F0.** Do not stop until §11 is satisfied or the scope-cut ladder floor is reached.
