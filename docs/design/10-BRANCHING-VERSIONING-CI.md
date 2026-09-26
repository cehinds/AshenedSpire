# 10 — Branching, versioning and CI

This file is normative for the implementation session ([09 §0 rule 3](09-UNITY-MEGA-PROMPT.md)). Owner ruling, 2026-09-26.

## 1. Branches

| Branch | Purpose | Merges into | Who or what merges |
|---|---|---|---|
| `main` | Released epic releases only. Every commit is tagged | — | Promotion from `test` |
| `test` | Release candidate: the whole CI plus the bot and a Windows build | `main` | Promotion from `dev` when an epic release is complete and green |
| `dev` | Integration. Always playable | `test` | Feature lanes and fixes |
| `feature/<feature>/main` | The **feature lane** (one per feature in [03](03-FEATURES-AND-USER-STORIES.md)) | `dev` | After each story lands, and again when the feature is complete |
| `feature/<feature>/<story>` | One **user story** (for example `feature/combat-core/us-5.3`) | its `feature/<feature>/main` | When the story's acceptance criteria and CI are green |
| `fix/<short-name>` | A defect found after a merge | `dev` (or `test` for a release blocker, then back into `dev`) | When CI is green |

- **Naming.** `<feature>` is the kebab-case feature name from the 03 feature map, for example `foundation`, `boot-title`, `creation`, `combat-core`, `combat-content`, `equipment`, `places-smithing`, `merchant`, `events`, `rewards`, `progression`, `profile-journal`, `world-journey`, `settings-custom`, `audio`, `accessibility`, `quality`. `<story>` is the lower-case story ID: `us-5.3`.
- **Why `/main` for the lane.** Git cannot hold both `feature/combat-core` and `feature/combat-core/us-5.3`, because a branch name cannot also be a folder. The lane is therefore `feature/combat-core/main`.
- **Merge style.** `git merge --no-ff`, so every story and feature is one visible merge commit. There is no force-pushing, and no rebasing of shared branches.
- **Cleanup.** Delete story branches after they merge. Delete a feature lane after its final merge into `dev`.

## 2. Version scheme — `E.F.S.P`

`<epic release>.<feature>.<user story>.<patch>`. The first version is **`0.0.1.0`**: the design package.

| Part | Bumps when… | Resets |
|---|---|---|
| **E**: epic release | an epic release (a build phase F0–F6 in 09 §8) is complete. The bump is the last commit on `dev`, and that commit is then promoted `dev → test → main` | F, S, P → 0 |
| **F**: feature | a feature lane merges into `dev` with its **final** story, completing the feature | S, P → 0 |
| **S**: user story | a feature lane merges into `dev` carrying one or more **newly completed stories**. S increases by the number of stories landed | P → 0 |
| **P**: patch | a `fix/*` merge, or a non-feature change (docs, CI, tooling) lands on `dev` | — |

Worked example:

```
0.0.1.0   design package (this merge)
0.0.2.0   feature/foundation/main → dev with us-0.1 done
0.0.3.0   … with us-0.2 done
0.0.3.1   fix/registry-orphans → dev
0.1.0.0   feature/foundation/main → dev, final story: the feature is complete
0.1.1.0   feature/boot-title/main → dev with us-1.1 done
…
1.0.0.0   epic release F0 complete → bump on dev → promoted to test → main, tagged v1.0.0.0
```

The version is **monotonic and single-writer**:
- only merges into `dev` (plus hotfixes on `test`) change it;
- story merges into a feature lane never touch `VERSION`, so parallel features cannot conflict.

`main` and `test` receive versions only by promotion. `main` gets a tag `v<E.F.S.P>` on every promotion.

### The incrementer

`Tools/version.mjs` is the only thing that edits the version.

| Command | What it does |
|---|---|
| `node Tools/version.mjs bump <epic\|feature\|story\|patch> [--count n]` | Updates `VERSION`, `Unity/ProjectSettings` `bundleVersion` (`E.F.S.P`), the Android `bundleVersionCode` (E×1,000,000 + F×10,000 + S×100 + P), `build-info.json`, and prepends a `## [E.F.S.P] — date` section to `CHANGELOG.md` listing the merged story IDs |
| `node Tools/version.mjs check <base-ref>` | Used by CI. Fails unless the head `VERSION` is **greater** than the base's, and the bumped part matches the branch type (see below). It also checks the format `^\d+\.\d+\.\d+\.\d+$` and that the CHANGELOG has the entry |
| `node Tools/version.mjs next <part>` | Prints the next version without writing it |

Rules for `check`:

| Merge | Allowed bump |
|---|---|
| `feature/*/main → dev` | `story` or `feature` |
| `fix/*` or chore → `dev` | `patch` |
| Epic release commit on `dev` | `epic` |
| `dev → test` and `test → main` | **none**: versions must be equal |

## 3. CI — at most 9 jobs (hard cap 20), all concurrent

One workflow, `.github/workflows/ci.yml`. It runs on push to `dev`, `test` and `main`, and on PRs into any of them.

- **Every job is independent.** There is no `needs:`. Each job checks out, restores its own cache (keyed by job name) and runs alone, so all of them run concurrently.
- `concurrency: { group: ci-${{ github.ref }}-${{ matrix.job || github.job }}, cancel-in-progress: true }`, so a new push cancels only the same job on the same ref.
- **Path filters** skip jobs whose inputs didn't change. For example `docs` runs only on `docs/**`, and the Unity jobs skip docs-only pushes.

| # | Job | Runs | Needs Unity licence? |
|---|---|---|---|
| 1 | `version` | `Tools/version.mjs check` against the target branch | no |
| 2 | `docs` | Markdown link check; every Mermaid block renders (mermaid-cli) | no |
| 3 | `content` | Content export and transform are reproducible; count gate; schema validation; `ProvenanceComplete`; `AiDisclosurePresent` (Node) | no |
| 4 | `domain` | `dotnet test` on the engine-free Domain and Application assemblies: rules, parity oracle (both presets), golden replays, save round-trip, enforcement scans | no |
| 5 | `editmode` | Unity EditMode suites (game-ci `unity-test-runner`) | yes |
| 6 | `playmode` | Unity PlayMode suites: smoke and focus walk | yes |
| 7 | `bot` | Headless bot on N seeds (N = 20 on PRs, 200 on `test`) | yes |
| 8 | `build-windows` | IL2CPP Win64 player build plus the golden-replay smoke. Runs on pushes to `test` and `main`, and on PRs into them | yes |

That makes 8 jobs, under the target of 10. Adding a job requires a DECISIONS entry, and the cap is 20.

- **Unity licence.** Jobs 5–8 need the `UNITY_LICENSE` secret (plus `UNITY_EMAIL` and `UNITY_PASSWORD` for a Personal licence). If the secrets are missing, those jobs **skip with a notice** instead of failing. Local batchmode runs (09 §6.2) are then the gate, and the session says so in its final report.
- **Caches.** Unity `Library/` is cached per job and per Unity version. Node and NuGet caches are separate. No two jobs write the same cache key.

## 4. Test suites — at most 9 (hard cap 20)

Tests are grouped into **suites**. A suite is one assembly or category that CI runs as a unit. Individual test cases are unlimited; the limit is on suites, so CI stays readable.

| # | Suite | Contents | CI job |
|---|---|---|---|
| 1 | `Rules` | Formulas, damage order, Block, Dodge, meters, procs, DoTs, stances, costs, hand modes, map rules, enemy brain, legality | domain |
| 2 | `Parity` | Oracle comparisons for the `shipped` and `reference` presets | domain |
| 3 | `Replay` | Golden replays, resume-equivalence, preview equals resolve | domain |
| 4 | `Saves` | Round-trip, migrations, mirror and backup recovery, log CRC | domain |
| 5 | `Enforcement` | No magic strings, numbers or content IDs; no engine in Domain; asmdef graph; codegen is clean | domain + editmode |
| 6 | `Content` | Validator, count gate, registry complete, provenance, AI disclosure | content + editmode |
| 7 | `Presentation` | View-model bindings, `CategoryNav`, layout bands, `minPhysical` checks | editmode |
| 8 | `Smoke` | PlayMode boot → flows → reload, and the focus-order walk | playmode |
| 9 | `Bot` | Seeded headless runs | bot |

The 08 §12 enforcement tests map onto suites 5 and 6.

## 5. Promotion checklist (dev → test → main)

1. Every feature in the epic release has merged into `dev`, and the `epic` bump commit is on `dev`.
2. CI is green on `dev`. Then `git checkout test && git merge --no-ff dev` and push. CI runs with `bot` at 200 seeds and `build-windows`.
3. CI is green on `test`. Then `git checkout main && git merge --no-ff test`, tag `v<E.F.S.P>`, and push the tag.
4. Record the release in `CHANGELOG.md` and `docs/BUILD-LOG.md`. **Do not claim owner approval.** A promotion is a process step, not a sign-off.
