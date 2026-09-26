# 02 — Scope: Ashen Spire (Unity rebuild)

| | |
|---|---|
| Product | Ashen Spire: a dark-fantasy, seeded, run-based tactical deckbuilder RPG |
| Target | Unity 6000.6.0f1: URP (2D renderer), UI Toolkit, Input System, Addressables, Test Framework. Windows standalone first, then Android. WebGL goes last because of a known UI Toolkit WebGL renderer bug in this editor version |
| Code | All new C#. The old TypeScript repo (`D:\repos\AshenSpire`) is a **behaviour reference and data source only** |
| Assets | Only the existing art from the old repo (5,241 files, almost all WebP), re-exported (WebP → PNG, SVG → PNG, atlases). **AI-generated content of almost any kind is allowed** (owner, 2026-09-26): art, music, SFX, voice and text. It must match the existing style, and each item's provenance is recorded. **No licensed or third-party material** (stock, marketplace, downloaded fonts, sample libraries, copyrighted IP). The game **clearly states that it was made with AI**. The asset set contains no audio or font files today: music is generated, the synth is the fallback, and text uses Unity's bundled font |
| Session model | One unattended implementation session, run with the Unity MCP, Blender (optional), Python tooling and sub-agents. No human input |
| Reference layout | 1280×720 reference, with the shipped height bands: ≥465 px standard, 340–464 px compact short-wide, under 340 px the upright gate. Portrait uses the iPhone SE 375×667 and Galaxy S24 360×780 references |

---

## 1. Goals

1. **Parity of play.** A player of the shipped web build meets the same rules, numbers and flow (GDD §0: "preserve player behavior before replacing implementation").
2. **Better build.** It is fully data-driven ([08](08-DATA-CONVENTIONS.md)), deterministic, testable headless, and skinned from existing assets through a single registry.
3. **Configurable.** Designers change the game by editing JSON and swapping registered assets, never C#. That covers balance, content, layout tokens, strings, audio contexts, screen flow and difficulty.
4. **Always playable.** After each component lands, the game boots and the completed slice plays end-to-end.

## 2. Non-goals

- Licensed or third-party material of any kind. Rule changes the owner has not ruled on.
- A full art re-paint. Generated content fills gaps and adds music; it does not replace existing art that works.
- Copying old source code, even where it is convenient.
- Pixel-matching the web layout. The wireframes in [04](04-WIREFRAMES.md) are the layout authority.
- Online services, accounts, telemetry and monetization.

---

## 3. Scope tiers

### P0 — must ship in the session (the Classic Climb, complete)

| Area | Included |
|---|---|
| Foundation | Asset pipeline (import, convert, registry, atlases), content loader, validator, codegen, config layers, PRNG streams, save system (3 slots + profile + mirror), enforcement tests |
| Boot and title | Profile recovery notice, press-any-input gate, title menu (owner order plus Journal and Custom Run), load slots, Settings → About with credits and the AI disclosure |
| Creation | 4 panes on a rail (Class, Character, Equipment, Review/Seed). Next is always pressable and refuses with the reason. Includes Standard and Assign stat modes, keepsakes, sprite/tint/sigil, slot overwrite confirm |
| Prologue | 9 authored scene slots imported from the shipped opening config (the shipped subset and order, with art for warmth, year, night, carry, road and step), skippable, resumes per scene. Everything is driven from `prologue.json` |
| Classic Climb | 3 seeded seats × tiers, 12×7 act map with fog, node weights and rules from data, boss destinations by route, act transitions with a full heal, win on any act-3 boss (extensible victory check), permadeath |
| Combat | The full shipped rule set (the original prompt's §3.3 **as corrected in [01 §C3](01-PROMPT-REVIEW.md)**, with the values imported from the shipped data): costs, pools, retained hand, damage order, Block, Dodge Roll, Poise/Ward meters and breaks, Arcane Exposure, threshold procs, DoTs, Madness, stances, enemy AI (weights, `maxConsecutive`, phases, charged moves), intents with live numbers, state frames and VFX |
| Classes | All 4 playable classes, with outfits, tints and weapon animation suites chosen by grip. Grave Warden and Ash Child as profile unlocks **if their data exists** |
| Equipment | Hand sets (3 each, swap in combat at a data cost), body armour, weight class, weapon card packages, the 11-card starting deck, lent cards following their item, re-arming, smithing tiers, extract and install |
| Places | Shrine, camp, rest screen (hold to rest, smith, extract/install, flask re-split, level up), treasure |
| Merchant | Rolled-once stock, all shelves, removal pricing, optional sell/buy-back |
| Events | All 25 shipped events through the effect DSL, including quest chains gated by run history |
| Rewards | Opened menu, cinders/stones/card pick/flask/armament/relic, collect-all setting, survives reload |
| Progression | Character XP and levels, attribute points, skill tracks with drafts, class tree, deck floor |
| Profile | Run history, progress, unlocks, seen/new markers, settings |
| Enemies | All 33 enemies (data and art sets) and 35 encounters placed by seat, role and region; 3 legacy dungeons (12 scenes); the Blighted Valkyrie causeway at tier 3 |
| Audio | **Generated music** for the 8 contexts plus stingers, through the file slots in `audio/contexts.json`. A runtime PCM synth (the `tone` and `noise` recipes and beds from data) is the fallback and handles UI SFX. `"silence"` is supported |
| Settings | Everything in the original §3.6, except the LAN entries. **The owner's reference config is the default preset**, and web-format config import/export is supported |
| Quality | EditMode and PlayMode tests, a JS parity oracle for formulas and damage, golden replays, a headless bot (200 Classic seeds), MCP screenshot review of every screen at 3 sizes |

### P1 — should ship (build after P0 is green)

| Area | Included |
|---|---|
| World Journey | 200-node atlas across 5 regions, seeded 20/26-node routes, world map reveal, 11 local maps, inn/chapel, quest board and journal, dungeon finale win |
| Custom Run | Ascension, the 11 modifiers, deck modes (standard, sealed, draft), first seat, map shape |
| Journal | Collection browser (cards, relics, enemies, armaments), with seen/new markers |
| Accessibility | Text scaling, reduced motion, colour-blind-safe glyphs, full pad and keyboard play, hold-to-confirm |
| Bot | 50 World Journey seeds |

### P2 — could ship if time remains

- A mobile (Android) build profile with a size budget, touch flick-to-play, and a portrait layout pass.
- A WebGL build profile.
- A user music folder override.
- An in-editor content table editor window (`Window/Ashen/Content`).

### Out of scope — seams only

| Feature | Seam that must exist |
|---|---|
| LAN co-op ("Forsaken Together") | Player state indexed by seat; commands are serializable intents; combat supports N allies in data; the lobby screen id is reserved in `screens.json` but disabled |
| 4th tower | Per-seat `towerLit` in the save; a `runChoices` ledger; the victory check is a data-driven rule list; a post-victory hook can route to a journey instead of the end screen |
| Pending rule ideas (typed damage caps, Evade charges, +1 Stamina per turn, affinities, runes, sockets, quality tiers, weapon traits, a new stance roster, 50-card pools, enemy-level scaling) | Schemas leave room for them: open `tags`, an optional `sockets`, `quality` and `traits` on armaments, a school-resistance table. No implementation |

---

## 4. Scope-cut ladder

If the session runs short, cut from the top of this list. Never cut from the bottom.

1. P2 items
2. The Collection browser (keep only the run-history list)
3. World Journey local maps (keep the atlas route with direct node resolution)
4. Custom Run modifiers beyond ascension
5. Skill-track drafts (keep the XP bookkeeping)
6. Legacy dungeon dialogue (keep the rooms and fights)
7. Outfit variants beyond the first per class (keep all tints)
8. — **Floor. Never cut:** saves and resume, determinism, validator and enforcement tests, the combat rules, the 4 classes, the Classic Climb end-to-end, asset provenance (the registry, with origin and prompts), credits and AI disclosure.

## 5. Constraints

- **Licensing:** carry `CREDITS.md`, all `provenance.json` and `prompts*.json` files, and the AI disclosure. Use the IP-scrub vocabulary only, in game text and in generation prompts. **No licensed or third-party material.** Every generated item is recorded in `Provenance/generated/manifest.json`. **AI disclosure** appears on the title, on first launch, in About and in the README. Fonts: Unity's bundled default, or a generated typeface.
- **Determinism:** same seed + same commands = the same hashes on every platform.
- **Performance:** 60 fps in combat on a mid-range machine or phone; first interactive frame in under 3 s on desktop.
- **Save integrity:** no path loses a save. A newer save version is refused but kept.

## 6. Assumptions

These are provisional; each one is logged in `DECISIONS.md` at session start.

- A1: Unity **6000.6.0f1** is installed (confirmed at `D:\Unity\6000.6.0f1\Editor\Unity.exe`, the Hub secondary install path). No Unity MCP bridge is configured yet: the session installs one in preflight, or falls back to batchmode `-executeMethod`.
- A2: Python 3.13.2 is first on PATH; Pillow is not known to be installed. The .NET 8 and 9 SDKs are installed. Unity has Windows, Android and Web build support and an active Personal licence. WebP→PNG runs through **Blender 4.0 headless** (it reads and writes WebP), with `pip install pillow` as the second choice. SVG goes through Unity Vector Graphics, then Blender's SVG import, then `msedge --headless --screenshot`. Node 22 is available, and is used to export the shipped JS content modules to JSON.
- A3: Blender **4.0** is installed at `C:\Program Files\Blender Foundation\Blender 4.0` and runs headless (`blender -b -P script.py`). It is optional. It is used only for re-export and composition (for example, turning parallax backdrop layers into quads or batch-rendering existing sprites onto cards). It is never used to model or paint new content.
- A4: The old repo's `content/source/*` and `src/content/**` are importable to JSON by a one-shot converter script. The converter is committed. The old repo is never modified.
- A5: English only at launch, but every string is keyed.
- A6: `docs/design/reference/ashen-spire-game-config.json` (content 0.7.1) is the owner's current tuning. It applies on top of the shipped defaults as the default preset.

## 7. Success criteria

1. A new player can boot, create any of the 4 classes, skip the prologue, climb 3 seats and beat an act-3 boss, or die, with every screen reachable by mouse, keyboard and pad.
2. Quitting at any point and resuming returns to the exact state.
3. All enforcement tests in [08 §12](08-DATA-CONVENTIONS.md) pass, and the bot runs 200 Classic seeds with zero exceptions or softlocks.
4. Changing any number in `balance/*.json` or any string in `strings/en.json` changes the game with no code edit.
5. Every image and sound shipped resolves through `assets/registry.json`, either to a file derived from the old asset set or to a generated file with recorded provenance (`origin: generated`, with prompt and references). Nothing is licensed from third parties. Gaps are listed in `ASSET-GAPS.md`.
6. A player can see that the game was made with AI, on the title screen, on first launch and in About.
