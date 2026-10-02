# Changelog

Versions follow `E.F.S.P` — `<epic release>.<feature>.<user story>.<patch>` — see [docs/design/10-BRANCHING-VERSIONING-CI.md](docs/design/10-BRANCHING-VERSIONING-CI.md).

## [0.1.37.1] — 2026-10-02

- Meta lane engine-free halves: journal, settings, map reveal, prologue, creation panes, progression, armoury, piles, music (D-148-D-156)

## [0.1.37.0] — 2026-10-02

Stories: us-18.1

- 200-seed Classic bot report; reward card ids stay unique (D-146, D-147)

## [0.1.36.0] — 2026-09-27

Stories: us-9.1, us-9.2, us-9.3, us-10.1, us-10.2, us-10.3, us-4.5

- F3 node screens: W-09 merchant, W-11 event and dialogue, W-13 legacy dungeon on the climb router (US-9.3 sell only; the shipped game has no buy-back, D-081)

## [0.1.29.0] — 2026-09-27

Stories: us-4.1, us-4.2, us-4.3, us-4.4, us-4.6, us-4.7, us-4.8, us-4.9, us-8.1, us-8.2, us-8.3, us-8.4, us-8.5, us-8.6

- F3 climb flow: W-06 act map, W-10 rest, W-15 run end; RunSession drives whole runs on RunLoop; owner ruling D-138 (shipped game is reference; no save compat)

## [0.1.15.0] — 2026-09-26

Stories: us-5.11, us-2.3

- Combat: weapon-set swap and re-arming, unrated poise, profile snapshot; run creation: custom attributes, derived-stat options, hand-rule settings, derived-stat restore and save migrations (all oracle-verified)

## [0.1.13.2] — 2026-09-26

- Refactor: rest stop and merchant share one smithing/card-extraction port; upgraded-item resolution has one home (-1,469 lines, oracles unchanged)

## [0.1.13.1] — 2026-09-26

- Record the F1 promotion (v0.1.13.0) in CHANGELOG and BUILD-LOG

## [0.1.13.0] — 2026-09-26

Stories: us-11.1, us-11.2, us-11.3, us-8.10

- **Promoted dev → test → main, tagged v0.1.13.0 (phase F1 First playable loop complete). A promotion is a process step, not an owner sign-off; the epic stays 0 (D-039).** CI green on dev and test; Unity EditMode 2102 passed / 2 skipped and PlayMode 3 passed / 1 skipped locally (CI Unity jobs skip without the UNITY_* secrets).
- F1 complete: W-08 rewards on RewardDoor (claims oracle incl. tap path), resume on rewards, permadeath on defeat; one run-effects door, chained full runs (108/108), bot smoke

## [0.1.12.0] — 2026-09-26

Stories: us-8.1, us-4.4, us-4.5, us-4.6, us-4.7, us-4.8, us-4.9

- Run loop (node travel, rest stops, acts, legacy dungeons, victory/death/Endless, run record and unlocks) ported with shipped parity: 108 runs, 14,983 steps

## [0.1.11.0] — 2026-09-26

Stories: us-9.1, us-10.1

- Merchant (stock, purchases, removal, sell, smith) and all 25 events ported with shipped parity

## [0.1.10.0] — 2026-09-26

Stories: us-2.1, us-5.10, pf-06

- F1 first fight: W-04 class pane, W-07 combat screen, W-20 pause, save and resume mid-fight (RunSession)

## [0.1.9.0] — 2026-09-26

Stories: us-0.4

- Run and rewards data built from content with exact key order (textAnchors, tag stamp order)

## [0.1.8.0] — 2026-09-26

Stories: us-11.1

- Post-combat pipeline (skills, class tree, levels, smithing, reward rolls, pendingReward) ported with shipped parity: 168 cases

## [0.1.7.0] — 2026-09-26

Stories: us-1.2, us-1.3, us-1.4, us-17.1

- F1 UI kit, boot gate, title, save slots, confirmations, keyboard/pad focus (US-1.2, 1.3 partial, 1.4, 0.10 footer, 17.1)

## [0.1.6.0] — 2026-09-26

Stories: us-4.2

- Act map generation (buildActMap, floor plans, run shapes, encounters, seats) ported with shipped parity: 1,883 maps

## [0.1.5.1] — 2026-09-26

- Commit Unity metas for CombatStringKeys.g.cs and SessionKeys.g.cs

## [0.1.5.0] — 2026-09-26

Stories: us-2.2

- Run creation (createRunState) and combat start args ported with shipped parity: 76 runs, 152 combat starts, 8 refusals

## [0.1.4.4] — 2026-09-26

- W-07 combat view-model

## [0.1.4.3] — 2026-09-26

- skill XP receipts recorded on the combat bus with shipped parity

## [0.1.4.2] — 2026-09-26

- legality service and combat session with save/resume (engine side of US-5.9)

## [0.1.4.1] — 2026-09-26

- combat data built from content equals the shipped registries; golden combats replay on it

## [0.1.4.0] — 2026-09-26

Stories: us-5.3

- createCombat and card/intent previews with shipped parity

## [0.1.3.0] — 2026-09-26

Stories: us-0.3, us-5.1

- runtime registries port and C# combat engine with shipped parity (60 golden combats, 1068 steps); content keeps shipped key order (D-040)

## [0.1.1.4] — 2026-09-26

- Version correction: owner ruling 2026-09-26: nothing is 1.x yet; 1.0.0.0-1.0.0.3 renumbered to 0.1.1.1-0.1.1.4, phase F0 completion is a promotion, not an epic release

- combat and registries parity oracles (was 1.0.0.3)

## [0.1.1.3] (was 1.0.0.2) — 2026-09-26

- codegen config fragments

## [0.1.1.2] (was 1.0.0.1) — 2026-09-26

- Repository moved to D:/repo/AshenedSpire

## [phase F0 complete — no version change] (was 1.0.0.0) — 2026-09-26

- Epic release F0 Foundation complete

## [0.1.1.1] — 2026-09-26

- docs link check skips the verbatim Provenance archive

## [0.1.1.0] — 2026-09-26

Stories: us-16.2

- Procedural SFX core and audio contexts (us-16.2)

## [0.1.0.0] — 2026-09-26

- Feature complete: foundation (us-0.1..0.10; us-0.5 part 2 deferred per D-033)

## [0.0.12.0] — 2026-09-26

Stories: us-0.10

- AI disclosure as data with enforcement (us-0.10)

## [0.0.11.0] — 2026-09-26

Stories: us-0.1, us-0.2

- Asset registry, lossless pipeline, provenance (us-0.1, us-0.2)

## [0.0.9.0] — 2026-09-26

Stories: us-0.8

- Enforcement tests: no magic strings/numbers/content IDs, engine-free layers (us-0.8)

## [0.0.8.0] — 2026-09-26

Stories: us-0.6

- Crash-safe saves: verified mirror, gen-stamped command log, migrations (us-0.6)

## [0.0.7.0] — 2026-09-26

Stories: us-0.7

- Config layers, presets and frozen RunSnapshot (us-0.7)

## [0.0.6.0] — 2026-09-26

Stories: us-0.5, us-18.4

- Deterministic RNG streams, seed codec, seat order; parity oracle (us-0.5 part 1, us-18.4)

## [0.0.4.0] — 2026-09-26

Stories: us-0.4

- Content validator: schemas, references, ops, strings, row rules, presets (us-0.4)

## [0.0.3.1] — 2026-09-26

- CI workflow YAML fix

## [0.0.3.0] — 2026-09-26

Stories: us-0.9

- Codegen of C# keys and enums; assembly skeleton; shared test harness (us-0.9)

## [0.0.2.0] — 2026-09-26

Stories: us-0.3

- Content exported to id-keyed JSON with schemas; reference preset (us-0.3)

## [0.0.1.0] — 2026-09-26

### Added
- Unity rebuild design package (`docs/design/00`–`10`): the prompt review and shipped-code fact check, scope, features and user stories, wireframes, activity and process flows, the sub-agent diagram review, data conventions, branching, versioning and CI rules, and the one-session Unity mega prompt.
- The owner's reference game config (`docs/design/reference/ashen-spire-game-config.json`), the default preset for the rebuild.
- `VERSION` file (starting version 0.0.1.0).
