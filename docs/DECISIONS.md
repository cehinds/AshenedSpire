# Decisions

Format: **D-###: title**, with context · choice · how to reverse · status. Every decision is **provisional** until the owner rules on it. Nothing here claims owner approval unless it quotes a dated owner ruling.

| ID | Decision | How to reverse | Status |
|---|---|---|---|
| D-001 | 1280×720 reference; height bands 465 and 340 px; narrow layout at ≤520 px, all in `ui/layout.json` | Edit `layout.json` | provisional |
| D-002 | Dodge modifier ⌊(DEX−dexPivot)/2⌋ with the shipped `dexPivot = 10` (always negative on the lean scale). Flagged for balance review | `rules/dodge.json#dexPivot` | provisional |
| D-003 | Hand rules as shipped: `drawMode fill`, opening 3, capacity 10, retain on, overflow keep. The reference preset changes the opening hand | `rules/hand.json`, presets | provisional |
| D-004 | Title menu: Continue · Load · New · Journal · Custom Run · Settings · Quit. Credits live in Settings → About | `ui/menus.json` | provisional |
| D-005 | Fonts: Unity's bundled default. A generated display face is optional. No downloaded fonts, including OFL ones (owner, 2026-09-26: no licensed material) | `fonts.json` | provisional |
| D-006 | Audio: generated music in the 8 context file slots. The procedural synth is the fallback and handles UI SFX | `audio/contexts.json` | provisional |
| D-007 | Co-op, the 4th tower and the pending rule ideas are seams only | Scope doc | provisional |
| D-008 | WebGL build deferred (UI Toolkit WebGL renderer bug in 6000.6.0f1). Windows first, then Android | Build profiles | provisional |
| D-009 | Single-use places end the visit on any committed service (shipped rule); the reference preset makes places multi-use. The validator guarantees an exit | `places.json`, settings | provisional |
| D-010 | Weapon-set swap lives in an Armaments popover opened from the player figure | UXML | provisional |
| D-011 | Relics without paintings get generated icons; typographic sigils are the fallback | registry | provisional |
| D-012 | Unity project at `Unity/`, tools at `Tools/`, engine-free tests at `Tests/Domain.Tests` | Repo layout | provisional |
| D-013 | Potions sit in the combat footer and the map corner. In rooms they are reached through Menu → Potions | UXML | provisional |
| D-014 | Extract moves the card into the deck, as shipped | `rules/smithing.json` | provisional |
| D-015 | RUN_HUD XP strip behind `ui/runHud.json#showXp`, off by default | Flag | provisional |
| D-016 | Two PanelSettings (wide 1280×720, narrow 430×780), with `minPhysical` enforced after scaling | `layout.json` | provisional |
| D-017 | If the command log can't be replayed (version or content changed), resume from the snapshot and warn | SaveService | provisional |
| D-018 | Converted PNGs are gitignored and their `.meta` files committed. `Tools/import-assets` regenerates them from `D:\repos\AshenSpire` | `.gitignore` | provisional |
| D-019 | The owner's reference config (`docs/design/reference/ashen-spire-game-config.json`) is the default preset; `shipped` stays available | `settings/presets` | provisional |
| D-020 | The reference config hides prologue Pause and Skip. Hold-to-skip stays on the pad and keyboard Back action | `prologue.json` | provisional |
| D-021 | Feature lanes are `feature/<feature>/main`; story branches are `feature/<feature>/<story>` (git can't hold both `feature/x` and `feature/x/y`) | Branch names | provisional |
| D-022 | `E.F.S.P` versioning from 0.0.1.0. Only merges into `dev` bump the version; `main` is tagged on promotion | `Tools/version.mjs` | provisional |
| D-023 | **No Unity MCP bridge in this session.** All Unity work runs through batchmode via `Tools/unity.sh` (§6.2). The MCP package is added only for future sessions | Add MCP later | provisional |
| D-024 | The machine's UPM `cacheRoot` points at an unavailable drive (`H:\…`). `Tools/unity.sh` sets `UPM_CACHE_ROOT` to the per-user Unity cache for batchmode runs. The global config is not modified | Unset the env var | provisional |
| D-025 | SVG import uses the built-in `com.unity.modules.vectorgraphics` module of 6000.6. The Edge and Blender rasterizers are fallbacks | `manifest.json` | provisional |
| D-026 | Minimal package set; Unity's default template packages (IAP, analytics, XR, multiplayer center, timeline, navigation) were removed | `manifest.json` | provisional |
| D-027 | Application-layer namespace is `Ashen.App` (assembly `Ashen.Application`), so it can't shadow `UnityEngine.Application` inside `Ashen.*` code | Rename the namespace | provisional |
| D-028 | Codegen is a Node tool (`Tools/codegen.mjs`) instead of an Editor asmdef. It runs without a Unity licence in CI and can't break on a domain reload | Port it to the Editor | provisional |
| D-029 | The owner reference config is imported through the shipped strict importer. A data-driven legacy key map handles renames. Only values it rejects (out of range, or unknown in the shipped build) are set aside and applied directly, and they are recorded in the preset | `Tools/export/legacy-key-map.json` | provisional |
| D-030 | Content IDs stay as shipped (camelCase), because web configs, saves and the oracle use them. New IDs use lowerCamelCase. This amends 08 §3 (`lower_snake_case`) | Add an ID migration with aliases | provisional |
| D-031 | Repo-local `nuget.config` clears machine sources (this machine maps `nuget.org` to `C:\Python\Lib`) and keeps packages in the gitignored `.cache/nuget`. The Unity UPM cache also moved to `.cache/upm` because **C: is full (0 bytes)** | Delete `nuget.config` | provisional |
| D-032 | Tests are shared engine-free NUnit sources in `Unity/Assets/Tests/EditMode/Shared`. They run under Unity EditMode **and** `dotnet test` (`Tests/Domain.Tests`) from one codebase | — | provisional |
| D-033 | us-0.5 lands in two parts. **Part 1 (now):** named streams, bit-exact parity with the shipped RNG, seed codec and seat order. **Part 2:** golden replays and map-generation parity land with the map generator (F3) and the combat loop (F2), which they need. F0's "map oracle" exit item moves to F3 | Track in BUILD-LOG | provisional |
| D-034 | PRNG algorithm constants are generated numbers (`RngAlgorithm`, from `codegen.config.json` numberSets), not content. They define the algorithm and aren't tuning. Stream names and the seed alphabet are content (`rules/rng.json`) | — | provisional |
| D-035 | **Import by phase (disk-constrained).** The registry covers **all** 5,265 source assets from metadata (IDs, dimensions, alpha, SHA-256) with no conversion. Only the ID prefixes listed in `Tools/assets.config.json#import` are converted into Unity per phase. F0 pilot: 899 assets (UI flasks, class sprites, enemies with states, poses and painted art, VFX, backgrounds, environments, map, relics, equipment icons, defeated poses). Animations (3,071), outfits, poses and the prologue import in F1/F2/F3, as needed and as disk allows. D: has 3.7 GB free and C: 0.2 GB | Add prefixes to `import` | provisional |
