# Ashen Spire — Unity rebuild design package

Read these in order. The last file is the prompt that builds the game.

| # | Doc | What it is |
|---|---|---|
| 01 | [Prompt review](01-PROMPT-REVIEW.md) | Critique of the original web-first mega prompt, the improvements made, and a **fact check against the shipped code** (§C: corrected rules, counts, assets, UI conventions, lessons from the earlier Unity port) |
| 02 | [Scope](02-SCOPE.md) | Goals, non-goals, the P0/P1/P2 tiers, seams-only features, the scope-cut ladder, constraints, assumptions and success criteria |
| 03 | [Features and user stories](03-FEATURES-AND-USER-STORIES.md) | 20 epics, with user stories and acceptance criteria (`US-x.y`) |
| 04 | [Wireframes](04-WIREFRAMES.md) | Every screen at the 1280×720 reference, with compact and portrait rules, the component kit, and the **build order** |
| 05 | [Activity flows](05-ACTIVITY-FLOWS.md) | Mermaid diagrams of what the player does (AF-01 to AF-11) |
| 06 | [Process flows](06-PROCESS-FLOWS.md) | Mermaid diagrams of what the system does (PF-01 onward): pipeline, boot, commands, damage, enemy AI, saves, run generation, UI, gates |
| 07 | [Diagram review](07-DIAGRAM-REVIEW.md) | Findings from four sub-agent reviews (flows, architecture, rules parity, UX) and how each was resolved |
| 08 | [Data conventions](08-DATA-CONVENTIONS.md) | The normative data-driven / no-magic-strings contract: IDs, schemas, codegen, effect DSL, config layers, enforcement tests |
| 10 | [Branching, versioning and CI](10-BRANCHING-VERSIONING-CI.md) | `feature/<feature>/main` lanes and `feature/<feature>/<story>` branches; `E.F.S.P` versions from 0.0.1.0; at most 9 concurrent CI jobs and at most 9 test suites |
| 09 | [**Unity mega prompt**](09-UNITY-MEGA-PROMPT.md) | Paste into a fresh Claude Code session at this repo. It builds the game unattended, in one session |

Reference data: [`reference/ashen-spire-game-config.json`](reference/ashen-spire-game-config.json) is the owner's current tuning. The rebuild ships it as the default preset.

Owner rulings so far (2026-09-26):

- **AI-generated content of almost any kind is allowed**: art, music, SFX, voice and text.
- **No licensed or third-party material.**
- **The game must clearly state that AI was used to make it**, on the title screen, on first launch, in About and in the README.

Sources:
- the shipped web game: `D:\repos\AshenSpire` (dev, 2026-09-23);
- the earlier Unity attempt: `D:\repos\AshenSpire-Unity`;
- the original prompt: `ASHEN-SPIRE-REBUILD-MEGA-PROMPT.md`.
