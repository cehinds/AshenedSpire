# 03 — Features and user stories

- **Roles:** **Player**; **Designer**, who edits JSON and assets only; **QA**, who covers the automated checks; **Owner**, who handles licensing and product rulings.
- **Priorities** follow [02-SCOPE](02-SCOPE.md): P0 must ship, P1 should, P2 could, S is a seam only.
- **IDs** are stable. The mega prompt and commits cite them (`US-5.3`).
- **Screens** are named by their `screen:` id from [04-WIREFRAMES](04-WIREFRAMES.md). **Flows** cite AF and PF numbers from [05](05-ACTIVITY-FLOWS.md) and [06](06-PROCESS-FLOWS.md).
- **Universal acceptance criteria.** These apply to every story without being repeated:
  1. There are no literal strings or tuning numbers in C#. Everything comes from data ([08](08-DATA-CONVENTIONS.md)).
  2. The feature works with mouse, keyboard, pad and touch.
  3. Previews come from the resolver.
  4. The state survives save and reload.

---

## Feature map

| Epic | Features | Pri |
|---|---|---|
| E0 Foundation | Asset pipeline, registry, content loader, validator, codegen, config layers, PRNG streams, save service, enforcement tests | P0 |
| E1 Boot & Title | Profile recovery, input gate, title menu, load slots, credits | P0 |
| E2 Character creation | Class, Character, Equipment, Review steps; stat modes; keepsakes; overwrite confirm | P0 |
| E3 Prologue | 9 authored scene slots (imported shipped subset), skip, per-scene resume, data-staged scenes | P0 |
| E4 Classic Climb | Seats and tiers, act map, node resolution, boss destinations, legacy dungeons, causeway, victory and death | P0 |
| E5 Combat core | Turn loop, costs, pools, retained hand, damage order, Block, Dodge Roll, meters, breaks, intents, targeting | P0 |
| E6 Combat content | 50 statuses and procs, DoTs, Madness, 3 stances, enemy AI, 33 enemies and 35 encounters, state frames, 56 VFX | P0 |
| E7 Equipment | Slots and sets, grip, weight class, weapon packages, starting deck, lent cards, re-arming | P0 |
| E8 Places & smithing | Shrine/camp/inn/chapel tags, rest screen, smith, extract/install, flask re-split, level up | P0 (inn/chapel P1) |
| E9 Merchant | Stock, shelves, removal pricing, sell/buy-back | P0 |
| E10 Events & quests | 25 events on the DSL, history-gated chains, Turncoat Mirror | P0 |
| E11 Rewards | Reward menu, drop tables, collect-all, persistence | P0 |
| E12 Progression | XP and levels, attributes, skill tracks and drafts, class tree, deck floor | P0 |
| E13 Profile & Journal | Run history, progress, unlocks, seen/new markers, collection | P0 / P1 |
| E14 World Journey | Atlas, route generation, world map, local maps, quest board, journal, finale | P1 |
| E15 Custom Run & Settings | Ascension, modifiers, deck modes; all settings | P0 settings / P1 custom |
| E16 Audio | Generated music and stingers for 8 contexts, SFX (generated or synth), procedural fallback, silence | P0 / P2 |
| E17 Accessibility & input | One focus model, text scale, reduced motion, colour-blind glyphs, hold-to-confirm | P0 / P1 |
| E18 Quality | Golden replays, headless bot, screenshot review, performance budget | P0 |
| E19 Seams | Co-op seats, 4th tower, pending rule ideas | S |

---

## E0 Foundation

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-0.1 | As a **Designer**, I want every image and sound to have a stable asset ID, so that I can swap art without touching code. | The pipeline (PF-01) writes `registry.json`. Every sprite reference in content is an asset ID. Dangling IDs fail validation; orphans are listed. | P0 |
| US-0.2 | As the **Owner**, I want every shipped asset traceable and free of licensed material. | The registry records the source path and hash for every entry, or `origin: generated` with modality, tool, prompt and references (AI generation of any kind is allowed, 2026-09-26). No third-party licensed files; a test fails on any file without a registry origin. `CREDITS.md`, every `provenance.json` and every `prompts*.json` are copied into `Provenance/`. A test fails on any file in `Art/Imported` without a registry source. | P0 |
| US-0.3 | As a **Designer**, I want content in JSON with schemas, so that I can add a card, enemy or relic without C#. | Adding a card row plus strings plus asset IDs makes it appear in the reward pools after validation. No code change is needed. | P0 |
| US-0.4 | As a **Designer**, I want the validator to tell me exactly what is wrong, so that I can fix data quickly. | Every error names file, row id, field and rule. The rules cover cross-references, cost rules (Mana ⇒ ≥ 1 Action and ≥ 1 Stamina), rarity cost-census targets, string keys, asset IDs and `"silence"`. | P0 |
| US-0.5 | As **QA**, I want the same seed and inputs to give the same run, so that bugs are reproducible. | Named PRNG streams; golden replays for 10 seeds hash-match; the Domain assembly has no engine references. | P0 |
| US-0.6 | As a **Player**, I want my saves never to be lost or corrupted. | Atomic write, verified mirror, schema version, migrations. A newer save is refused but kept. The round-trip tests pass (PF-06). | P0 |
| US-0.7 | As a **Designer**, I want tuning and mod overrides to layer predictably. | Config stack per [08 §8](08-DATA-CONVENTIONS.md). The run snapshot is frozen into the save. The override folder works in the player build. | P0 |
| US-0.8 | As an **Engineer**, I want the build to fail on magic strings or numbers, so that the data-driven rule can't erode. | The [08 §12](08-DATA-CONVENTIONS.md) tests exist and are green. | P0 |
| US-0.10 | As the **Owner**, I want the game to state clearly that it was made with AI. | The disclosure key `about.aiDisclosure` names the modalities actually used. It appears in the title footer, in a one-time first-launch notice and in Settings → About, with a "Made with AI" list from the provenance manifest, and in the README, `build-info.json` and `ABOUT.txt`. The test `AiDisclosurePresent` enforces it. | P0 |
| US-0.9 | As an **Engineer**, I want generated key constants, so that no schema or UI key is typed by hand. | `Ashen/Codegen` is idempotent; CI checks for no diff. | P0 |

## E1 Boot & Title

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-1.1 | As a **Player** with an unreadable profile, I want to be told and offered a restore. | `screen:profileRecovery` offers "Restore copy" when the mirror is valid, and "Start fresh", which archives the bad file. | P0 |
| US-1.2 | As a **Player**, I want a press-any-input gate. | Any key, button, click or tap advances. The gate focuses the first valid title item. | P0 |
| US-1.3 | As a **Player**, I want a clear title menu that reaches everything. | Entries per [04 W-02](04-WIREFRAMES.md), in the owner order: Continue, Load, New, Journal, Custom Run, Settings, Quit. Continue is shown **disabled** when there is no valid slot (shipped, AW:245). With a save present, a slot preview sits beside the menu (W3b). History, Profile and Collection are reachable through Journal (this fixes the unwired callbacks). Entries come from `ui/menus.json`. | P0 |
| US-1.4 | As a **Player**, I want to load any of the 3 slots and see what is in each. | Each slot card shows class sprite, name, act/floor, seed, playtime and journey type; an empty slot says so. | P0 |
| US-1.5 | As the **Owner**, I want credits and the AI disclosure visible. | See US-0.10. The About credits are generated from `CREDITS.md`, the old `prompts*.json` and `Provenance/generated/manifest.json`, grouped by modality. | P0 |

## E2 Character creation

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-2.1 | As a **Player**, I want to pick one of the 4 classes, with locked classes shown but unavailable. | Nothing is preselected. Unlockable classes show their unlock condition from `unlocks.json`. Next is always pressable; with no class chosen it refuses ("Choose a class.") and moves focus to the grid. | P0 |
| US-2.2 | As a **Player**, I want to name my character and choose Standard or Assign stats. | Standard applies the class preset. Assign starts all attributes at 1 with 3 points (floor 1, cap 4, both from data). With points left, Next refuses with the count and moves focus to the allocator. Derived stats preview live. | P0 |
| US-2.3 | As a **Player**, I want to choose a keepsake, sprite, sigil and tint. | The tint picks the frame set (no shader recolour). The preview updates instantly. | P0 |
| US-2.4 | As a **Player**, I want to choose starting armour, hands, equip slot, relic and kit. | Armour is required. Grip legality is validated (two-hand requires an empty off hand). One item instance occupies one hand; moving it never duplicates it. | P0 |
| US-2.5 | As a **Player**, I want to choose Classic or World Journey, a seed and a save slot. | The seed can be typed or random. An occupied slot needs confirmation. World Journey options appear only when P1 is shipped (flag in data). | P0 |
| US-2.6 | As a **Player**, I want Back to keep my choices. | Moving between steps preserves state. Finished steps collapse into receipts that can be reopened. | P0 |
| US-2.7 | As a **Player**, I want the creation screen to auto-advance if I enable it. | The setting `creationAutoAdvance` is honoured. | P1 |
| US-2.8 | As a **Player**, I want the creation steps on a rail I can jump around. | W1 shell: the rail lists Class, Character, Equipment and Review, and each item shows its current value. The footer is always Back plus Next (Begin on Review). The CategoryNav model switches the rail to a `[Class ▾]`-style selector when it does not fit. | P0 |

## E3 Prologue

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-3.1 | As a **Player**, I want a short prologue that I can skip. | Scenes, order and subset are imported from the shipped opening config into `prologue.json` (art exists for warmth, year, night, carry, road and step). Controls: Pause/Resume on the left; Continue and Hold-to-skip on the right; "Set forth" on the last scene. The `carry` line and item vary by class. | P0 |
| US-3.2 | As a **Player**, I want to resume the prologue where I quit. | Progress is saved per scene. If the saved scene is disabled, resume starts at the next enabled scene. | P0 |
| US-3.3 | As a **Designer**, I want to add a scene without code. | 9 slots exist in total. Filling an empty slot with art ID, text key, hold time and transition in data plays the new scene. | P0 |

## E4 Classic Climb

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-4.1 | As a **Player**, I want three seats in a seeded order, getting harder by tier. | Seat order on the `seats` stream. HP and boss scaling per tier from data. | P0 |
| US-4.2 | As a **Player**, I want a readable act map with fog of war. | 12×7 map with 6 paths; node weights and placement rules from `balance/map.json`. Fog or path mode is a setting. Camera and zoom persist. Travelled paths are highlighted and reachable nodes glow. **Select, then Enter:** a repeat pick travels only after the data delay (400 ms), and the node tray opens after 150 ms. | P0 |
| US-4.3 | As a **Player**, I want unknown nodes to surprise me fairly. | Unknown nodes are **resolved at map build** on the `events` stream from `unknownWeights` (shipped 55/25/12/8). An unknown that resolves to shrine opens the field camp. | P0 |
| US-4.4 | As a **Player**, I want my route to decide which boss I face. | Boss destinations per seat come from `bossDestinations.json`, and the map shows the boss at the path end. | P0 |
| US-4.5 | As a **Player**, I want legacy dungeons before certain bosses. | 3 dungeons (Briar Sanctum, Hall of Mirrors, Furnace Chapel) with 12 scenes on the legacy background and floor art, covering dialogue, rest, treasure and fights. A dungeon opens when the player **enters its boss node**, replacing the fight. A boss beaten inside counts once the player leaves the cleared dungeon. Fleeing is a DEX roll using the numbers in data. | P0 |
| US-4.6 | As a **Player**, I want a full heal and a new map between acts. | The seat's tower is marked lit and saved (4th-tower seam). | P0 |
| US-4.7 | As a **Player**, I want to win by beating any act-3 boss, or keep going in Endless. | The victory check is a rule list in `rules/victory.json`. A post-victory hook exists. The run is recorded. | P0 |
| US-4.8 | As a **Player** at the final tier, I want the causeway to the Ashen Crown. | At the final tier the boss pool is the seat's boss rows plus boss rows with no seat (the Blighted Valkyrie), drawn as a map destination. There is no extra node or step. | P0 |
| US-4.9 | As a **Player**, I want death to be final. | The slot is cleared, the result recorded, and the run summary shown (AF-11). | P0 |

## E5 Combat core

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-5.1 | As a **Player**, I want Actions, Stamina and Mana costs that are checked before payment. | All costs are checked, then paid. A refusal names the missing resource and costs nothing. X-cost cards are supported. | P0 |
| US-5.2 | As a **Player**, I want my hand kept between turns. | Opening hand, draw mode and capacity come from `rules/hand.json`. Shipped: opening 3, `drawMode: fill` refills to capacity 10, `retain: on`, `overflow: keep` (no forced discard; Ethereal exhausts). Other modes stay selectable in Advanced settings; with `overflow: discard` a discard chooser appears at turn end. The discard pile reshuffles when the draw pile is empty. Keywords: Exhaust, Ethereal, Innate, Retain, Unplayable. | P0 |
| US-5.3 | As a **Player**, I want damage numbers I can trust. | The 9-step order from `rules/damage.json`. The preview equals the resolved value (property test). Intents show exact live numbers, including multi-hit per-hit values. | P0 |
| US-5.4 | As a **Player**, I want Block to work predictably. | Block = ⌊(base + rating bonus + blockAdd) × blockGainedMult⌋, capped by blockCap. Player Block clears at player turn start; enemy Block at enemy turn start. | P0 |
| US-5.5 | As a **Player**, I want a Dodge Roll whose odds depend on my build. | d20 + ⌊(DEX − dexPivot)/2⌋ + weight modifier (+3/+1/−3) > 10. Shipped `dexPivot` is 10; it is data, flagged in `DECISIONS.md` for balance. The success chance is shown as a percentage. Cost (Stamina/Actions): Light 1/0, Medium 2/1, Heavy 3/2. On success, gain temporary guard = 3 + DEX mod + 3/1/0. Empty hands deal it into the deck. | P0 |
| US-5.6 | As a **Player**, I want to break enemies with Poise and Ward damage. | Meters fill from HP-damaging hits by impact. Impact bands by weapon weight: ≤3 → 1, ≤6 → 2, ≤8 → 3, else 4; magic and unarmed 1; enemy physical 2. On a break the enemy skips a turn and loses its charge; the threshold grows ×1.25 (data, rounded up). Arcane Exposure breaks into `magicVulnerable`. | P0 |
| US-5.7 | As a **Player**, I want to be staggered when my own Poise breaks. | −1 Action next turn (data), shown on the HUD. | P0 |
| US-5.8 | As a **Player**, I want Stamina pools that reward pacing. | **HP, Mana and Stamina carry over from the run** into each fight and are written back after it (shipped, `main.js:2141`). At player turn end, +1 Stamina if none was spent that turn. Mana has no natural regeneration. Pools derive from `derivedStats` (Stamina = 1 + CON, Mana = 1 + WIS, Actions = 3 + ⌊DEX/5⌋). | P0 |
| US-5.9 | As a **Player**, I want to target any legal target with any input. | One legality service. Focus is kept across redraws. An illegal target is refused with the reason, and focus returns to the control that started the action. | P0 |
| US-5.10 | As a **Player**, I want to end my turn and watch enemies act in order. | The enemy timeline is skippable and honours reduced motion. The next intents appear before my turn. | P0 |

## E6 Combat content

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-6.1 | As a **Player**, I want threshold statuses (Bleed, Frost, Insanity) that burst. | Numbers come from the status rows. Build-up doesn't decay; it resets on a burst. Resist then halves build-up for 2 turns, only on the tagged creature types in the row (Bleed and Frost: beast/humanoid; Insanity: humanoid/spirit). Meters are visible on the combatant. | P0 |
| US-6.2 | As a **Player**, I want damage over time (Crimson Blight, Burn, Venom) and Regen. | DoT ignores Block. Crimson Blight stacks don't decay; it expires after 3 turns and refreshes on re-apply. Burn and Venom lose 1 stack per turn. Regen heals by stacks and doesn't decay. | P0 |
| US-6.3 | As a **Player**, I want Madness as a risk and reward. | At turn start: −2 HP and +1 Action per stack, then it clears (data). | P0 |
| US-6.4 | As a **Player**, I want stances (Gorefire, Bulwark, Brace). | One stance at a time. Entry, per-event and exit effects all come from the DSL. The aura VFX comes from the stance row. | P0 |
| US-6.5 | As a **Player**, I want enemies whose behaviour I can learn. | Weighted moves on the `enemyAI` stream, `maxConsecutive` (with a fallback to all unlocked moves), `firstMove` on the opening roll only, and locked or unlockable moves. HP phases fire once each when HP changes. Charged moves run `whileCharging` once on the commit turn and resolve on `resolveOnTurn` (PF-05). | P0 |
| US-6.6 | As a **Player**, I want enemies to react visibly. | Intent-to-frame map from `ui/enemyStates.json`: attack → projectile, block → guard, hit → hurt or guardHit, ≤ 50% HP → wounded, debuffed → afflicted, break → hurt + staggerBreak VFX, death → defeated pose. | P0 |
| US-6.7 | As a **Designer**, I want all 33 enemies placed by seat, region and role. | 33 enemies (11 per act) and 35 encounters imported from the shipped data. Every enemy resolves base art (`sprites/enemy_*` or `enemies-expansion/*`), 7 state frames, idle/attack poses and a defeated pose through the registry. Placement follows `ENEMY-ROSTER.md`. | P0 |
| US-6.8 | As a **Player**, I want each hit to feel distinct. | 56 six-frame VFX sequences are chosen by the card's `vfx` asset ID. The weapon suite frame is chosen by grip and attack index, with the fallback chain for missing frames (CONVERSATION → PORTRAIT; readiness → STANCE-READY). | P0 |

## E7 Equipment

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-7.1 | As a **Player**, I want 3 weapon sets per hand that I can swap in combat. | The swap costs a data amount of Actions. An unaffordable swap is refused and costs nothing. Lent cards are restamped and pools re-derived. | P0 |
| US-7.2 | As a **Player**, I want my weapons to define my deck. | Starting deck of 11 from the weapon packages: 4 attacks from the right hand, 4 guards from the left (else the right), 1 technique, 1 signature, 1 class ability. Empty hands give Unarmed Strike, Evasive Guard and Dodge Roll. | P0 |
| US-7.3 | As a **Player**, I want lent cards to leave and return with their item. | This holds mid-fight and across saves (ledger in the save). | P0 |
| US-7.4 | As a **Player**, I want armour weight to matter. | Capacity = 2·CON + STR from data. The weight class is shown in the Armoury and changes Dodge. | P0 |
| US-7.5 | As a **Player**, I want the grip to change how my character animates. | One-hand, two-hand and dual are chosen from tags. The animation suite is resolved from `equipmentAnimations.json`. | P0 |
| US-7.6 | As a **Player**, I want an Armoury screen out of combat. | `screen:armoury` shows body, head, hands, feet (body only authored), talismans ×3, storage ×8 and hand sets, with before/after comparison receipts. | P0 |

## E8 Places & smithing

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-8.1 | As a **Player**, I want rest places to behave by their tags and never trap me. | `places.json` sets heal %, mana, flask refill and services per place. Shipped: shrine 35%, camp 25%, inn full, chapel 35%; mana tops up to 50% (or full) except at the inn, which restores it fully. Places are single-use by default (setting `shrineMultiUse`): committing Rest, Smith, Extract or Install ends the visit. Continue appears when multi-use or when a relic forbids Rest. The validator guarantees an exit. | P0 |
| US-8.2 | As a **Player**, I want to hold to confirm resting. | A short tap opens the review; a hold (settings, shipped 1.1 s) commits. Releasing early cancels. | P0 |
| US-8.3 | As a **Player**, I want to smith an armament with Smithing Stones. | The tier-up updates every lent card. Costs come from data. | P0 |
| US-8.4 | As a **Player**, I want to extract and install cards on item mounts for free. | Moves between mount and storage are shown as receipts. | P0 |
| US-8.5 | As a **Player**, I want to re-split my flask charges. | Crimson/Azure within a shared pool of **4**. Class allocations come from data (Reaver 3/1, Starseer 2/2, Rogue 3/1, Herald 3/1). Golden Sprout adds +1 Crimson. Utility flasks use the separate 3 `flaskSlots`. | P0 |
| US-8.6 | As a **Player**, I want to spend banked attribute points at rest places. | Derived stats re-derive, with a before/after preview. | P0 |
| US-8.7 | As a **Player**, I want inns and chapels in World Journey. | Full heal at an inn; quest board; level up. | P1 |

## E9 Merchant

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-9.1 | As a **Player**, I want the merchant stock to survive a reload. | Rolled on node entry on the **`shop`** stream (smith offer on the `smith` stream), saved, reused on resume, and cleared on leave. | P0 |
| US-9.2 | As a **Player**, I want shelves for cards, armaments, arts, relics, flasks and services. | Prices come from data. A purchase I can't afford is refused. Removal starts at the data base price and rises by the step each time. A smith service is offered at the data chance. | P0 |
| US-9.3 | As a **Player**, I want to sell and buy back if the setting allows. | The `shopSell` setting is honoured, and there is a buy-back list. | P0 |

## E10 Events & quests

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-10.1 | As a **Player**, I want events with meaningful choices. | All 25 events are expressed in the DSL. Illegal choices show their requirement. Continue is disabled until a response is chosen, and reads "Steel yourself" when the response starts a fight. | P0 |
| US-10.2 | As a **Player**, I want quest chains that remember my past runs. | Last Lantern and Nameless are gated by `historyHas` conditions. | P0 |
| US-10.3 | As a **Player**, I want the Turncoat Mirror to be the one way to change class. | A class-swap op exists and is used only by that event (validator rule). | P0 |

## E11 Rewards

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-11.1 | As a **Player**, I want to open a reward menu and choose what to take. | Nothing is auto-granted. Cinders, Smithing Stone, card pick of 3 (or class/skill draft), flask, armament and relic follow the drop tables in `balance/rewards.json`. The head shows "{claimed} of {total} claimed", with a claim-status column. Card offers are select-then-Confirm. | P0 |
| US-11.2 | As a **Player**, I want found armaments to persist across runs. | The profile records them. A duplicate converts to cinders (data amount). | P0 |
| US-11.3 | As a **Player**, I want Continue to sweep the remaining rewards if I choose. | The `rewardCollect` setting is honoured. Pending rewards survive a reload. | P0 |

## E12 Progression

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-12.1 | As a **Player**, I want character XP and levels. | XP sources and the curve come from `balance/xp.json` (shipped: fight win 50, kills 25/75/200, quest 125). Each level banks 1 attribute point. | P0 |
| US-12.2 | As a **Player**, I want skill tracks that grow with what I use. | One track per weapon family, armour weight, focus, dual-wield and class. Level-ups offer a draft of 1 in 3. Rarity unlocks at data levels, and the whole track upgrades at level 5. | P0 |
| US-12.3 | As a **Player**, I want a class tree. | 6 nodes per class, in tiers by class level. The tier-3 nodes are mutually exclusive. Picks modify the class card. | P0 |
| US-12.5 | As a **Player**, I want clear progression screens. | W-22: a level-up allocator with a live receipt, a skill-track draft (1 of 3, select then Confirm), and a class tree with the mutually exclusive tier-3 nodes marked. The XP strip sits behind a flag (open owner decision). | P0 |
| US-12.4 | As a **Player**, I want a deck floor that stops over-thinning. | Floor = 8 + ⌊level/2⌋ (data). Removal is refused below it. | P0 |

## E13 Profile & Journal

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-13.1 | As a **Player**, I want my run history. | Each entry records seed, class, result, floor, killer, duration and deck. It is viewable in `screen:journal`. | P0 |
| US-13.2 | As a **Player**, I want unlocks tied to achievements. | Conditions come from `unlocks.json` (win, boss, act, level). A toast appears on unlock. | P0 |
| US-13.3 | As a **Player**, I want to browse cards, relics, enemies and armaments I have seen. | Seen/new markers; unseen entries appear as silhouettes. | P1 |

## E14 World Journey

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-14.1 | As a **Player**, I want a seeded route across a fixed atlas. | 200 nodes and 5 regions from `worldAtlas.json`. The route is 20 or 26 nodes: Crownfall → city hub → dungeon finale. | P1 |
| US-14.2 | As a **Player**, I want to explore a world map that reveals as I travel. | Travel only along open edges. Inspecting a node never moves me. | P1 |
| US-14.3 | As a **Player**, I want cities and dungeons as local maps. | 11 local maps with services, quests, gates and a boss chamber. | P1 |
| US-14.4 | As a **Player**, I want a quest board and journal. | Survey quests are spoken through the dialogue component. | P1 |
| US-14.5 | As a **Player**, I want to win by clearing the finale dungeon. | No seat or tier scaling. The victory rule comes from data. | P1 |

## E15 Custom Run & Settings

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-15.1 | As a **Player**, I want settings for the map, places, rewards, equipment, hand rules, interface and prologue. | Every key in `settings/defaults.json` has a control generated from its schema (type, range, enum). Live keys apply immediately; run keys apply to new runs only. | P0 |
| US-15.4 | As the **Owner**, I want my web reference config to drive the Unity build and round-trip between them. | `settings/presets/reference.json` is generated from `docs/design/reference/ashen-spire-game-config.json` through the shipped import path, and it is the default preset. Settings → Saves imports and exports the web format (`schemaVersion`, `overrides` with web keys) through `key-map.json`. Unmapped keys are reported, never dropped. The parity oracle covers both the `shipped` and `reference` presets. | P0 |
| US-15.2 | As a **Player**, I want a worked example beside the hand rules. | The example is computed from the formulas with my current class. | P0 |
| US-15.3 | As a **Player**, I want a Custom Run with ascension, modifiers and deck modes. | 11 modifiers plus ascension levels are data patches. Deck modes: standard, sealed, draft. First seat and map shape are options. | P1 |

## E16 Audio

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-16.1 | As a **Player**, I want music that fits the moment. | Each of the 8 contexts (title, map, combat, elite, boss, shop, rest, victory) plays a **generated** loop through its file slot, with stingers for victory, defeat and boss. A procedural synth bed from `audio/music.json` plays when a slot is empty. `"silence"` is honoured. | P0 |
| US-16.2 | As a **Player**, I want sound effects for actions. | SFX recipes (`tone`/`noise` layers) come from `audio/sfx.json`, keyed by domain event type. Resolution is family-then-specific (`procBurst_frost` → `procBurst` → `default`), and `default` is audible. | P0 |
| US-16.3 | As a **Player**, I want my own music folder to override tracks. | A missing file falls back to the synth. | P2 |

## E17 Accessibility & input

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-17.1 | As a **Player**, I want to play fully with keyboard or pad. | One focus model. Every screen has a required focus order in `screens.json` (the validator fails without one), and a PlayMode test walks it. Escape closes the innermost layer. Hold-to-confirm has an `off` mode. | P0 |
| US-17.2 | As a **Player**, I want reduced motion. | Timelines shorten to data durations and screen shake is disabled. | P0 |
| US-17.3 | As a **Player**, I want text scaling and colour-blind-safe glyphs. | Intents and statuses carry shape glyphs as well as colour; the text scale runs 80–150%. | P1 |
| US-17.4 | As a **Player** on touch, I want flick-to-play. | The distance setting comes from data (default 64). | P2 |

## E18 Quality

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-18.1 | As **QA**, I want a headless bot. | 200 Classic seeds (and 50 Journey seeds at P1) with zero exceptions or softlocks. The report goes to `docs/qa/bot-report.md`. | P0 |
| US-18.2 | As **QA**, I want screenshots of every screen at 3 sizes. | Captured through the MCP. A sub-agent reviews them against the wireframes, and findings are fixed or logged. | P0 |
| US-18.4 | As **QA**, I want proof that the rules match the shipped game. | `Tools/oracle.mjs` generates expected values from the shipped JS (14 RNG streams, derived stats, ratings, damage grid, maps, seat order, hand rules, golden combat logs). The EditMode parity suite passes. | P0 |
| US-18.3 | As **QA**, I want a performance budget. | 60 fps in combat and first input in under 3 s; the profiler capture is attached. | P0 |

## E19 Seams (no feature shipped)

| ID | Story | Acceptance criteria | Pri |
|---|---|---|---|
| US-19.1 | As the **Owner**, I want co-op to be addable later. | Seat-indexed player state; serializable commands; N allies in the combat data model; the lobby screen ID is reserved. | S |
| US-19.2 | As the **Owner**, I want a 4th tower to be addable later. | `towerLit` per seat and a `runChoices` ledger in the save; the victory rule list and post-victory hook are tested with a stub rule. | S |
| US-19.3 | As the **Owner**, I want room for pending rule ideas. | Schemas carry optional `sockets`, `quality`, `traits` and a school-resistance table; the validator ignores them unless enabled. | S |
