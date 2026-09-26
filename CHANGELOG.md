# Changelog

Versions follow `E.F.S.P` — `<epic release>.<feature>.<user story>.<patch>` — see [docs/design/10-BRANCHING-VERSIONING-CI.md](docs/design/10-BRANCHING-VERSIONING-CI.md).

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
