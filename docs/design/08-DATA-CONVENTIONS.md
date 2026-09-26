# 08 — Data-driven conventions (the "no magic strings" contract)

This file is **normative** for the Unity rebuild. The mega prompt ([09](09-UNITY-MEGA-PROMPT.md)) cites it by section number. If code and this file disagree, the code is wrong.

---

## 1. Principles

1. **Data decides *what*. Code decides *how*.** Code implements generic mechanisms: effect ops, formula evaluation, state machines and layout primitives. Data names every concrete thing: cards, enemies, numbers, strings, screens, sounds and sprites.
2. **No content ID appears in C#.** Code never branches on `"bleed"`, `"reaver"` or `"shrine"`. Where behaviour differs, the difference is a **field, tag or op** in data.
3. **No schema key is typed by hand.** Keys come from generated code (§6).
4. **No user-facing text in C# or UXML.** It lives in string tables (§9).
5. **No tuning number in C#.** Outside `0`, `1` and `-1` in arithmetic helpers, every number lives in data (§5).
6. **Everything is validated before play.** A content error fails the editor validation and CI. It never becomes a runtime surprise.

## 2. Folder layout

```
Assets/
  StreamingAssets/Content/          ← shipping JSON (source of truth, human-editable post-build)
    manifest.json                   ← lists every content file + schemaVersion + contentHash
    schemas/*.schema.json           ← one schema per table (drives validator + codegen)
    balance/*.json                  ← formulas, economy, map weights, reward tables
    rules/*.json                    ← damage order, hand rules, dodge roll, meters, turn structure
    catalog/*.json                  ← cards, enemies, encounters, statuses, stances, relics, flasks,
                                      events, weapons, weaponPackages, armour, keepsakes, kits,
                                      classes, classTree, skillTracks, seats, bossDestinations,
                                      legacyDungeons, worldAtlas, localMaps, places, services,
                                      quests, unlocks, prologue
    ui/screens.json                 ← screen registry: id → uxml, viewModel, transitions, input map
    ui/tokens.json                  ← colours, spacing, radii, type scale, motion durations
    ui/layout.json                  ← breakpoints, reference resolution, per-screen region sizes
    ui/components.json              ← component kit defaults (meter segments, card sizes…)
    audio/contexts.json             ← context → track id | synth bed | "silence"
    audio/synth.json                ← synth beds and SFX recipes
    strings/en.json                 ← every user-facing string, keyed
    assets/registry.json            ← GENERATED: assetId → addressable key, dims, alpha, owner, tags
    settings/defaults.json          ← Advanced settings defaults + ranges
    settings/customRun.json         ← modifiers, ascension levels, deck modes
    settings/presets/*.json         ← id-keyed tuning patches: shipped (empty), reference (default, owner config)
  Game/
    Domain/        (asmdef, noEngineReferences: true)  pure rules, PRNG, formulas, effect interpreter
    Application/   (asmdef, noEngineReferences: true)  run + combat state machines, commands, events, saves
    Content/       (asmdef)  loaders, validator, registry, config layering
    Presentation/  (asmdef)  UI Toolkit views, view-models, animation, VFX, audio playback
    Platform/      (asmdef)  storage, input, screenshots, build info
    Generated/     (asmdef)  codegen output — never hand-edited; preserved by link.xml for IL2CPP
  Editor/          (asmdef)  asset pipeline, validator menu, bot runner, capture tools, CLI entry points
  Editor/Codegen/  (asmdef Ashen.Codegen) references NOTHING in Game/* or Generated/ so a broken output can't break the generator
  Art/Imported/    GENERATED from the old repo by the pipeline — never hand-edited
  Tests/EditMode, Tests/PlayMode
Tools/             python/shell scripts: webp→png, svg raster, atlas plan, asset scan
```

Loading goes through `IContentSource.ReadTextAsync(relPath) : Awaitable<string>`. There are two implementations:

- `FileContentSource`: the editor and Windows players. It reads the files directly, which allows hot reload in the editor.
- `WebRequestContentSource`: Android and WebGL, where StreamingAssets sits inside the APK or behind HTTP, so reads go through `UnityWebRequest`.

The list of files always comes from `manifest.json`. It is never built by listing a directory.

An optional **override layer** at `persistentDataPath/Overrides/**.json` lets people tune or mod the game without a rebuild. It is **id-keyed** (see §8) and has these safety rules:

- It is read only when `settings.modding` is `true`.
- An override path must match a manifest entry. `..` and absolute paths are rejected.
- Each file is at most 1 MB, and JSON depth is capped at 64.
- Golden replays always run with overrides off.

Build-only configuration (`importRules.json`, `addressablesPlan.json`, codegen settings) lives in `Editor/Config/`, never in StreamingAssets.

## 3. Identifier scheme

| Rule | Example |
|---|---|
| IDs are the **shipped IDs** (lowerCamelCase; D-030), unique **within their table** | `graveWisp`, `cleave`, `bleed` |
| References are **typed by the schema** (`"x-ref": "statuses"`), never by a prefix inside the string | `{ "op": "applyStatus", "status": "bleed", "amount": 3 }` |
| Cross-kind references in free positions use `kind:id` | `"reward": "relic:golden_sprout"` |
| Asset IDs are dotted paths from the registry | `enemy.grave_wisp.hurt`, `vfx.slash`, `pose.reaver.oathsworn.atk_03.ember` |
| Legacy IDs are kept for traceability | `"legacyId": "graveWisp"` |
| Rename = new ID + `aliases` entry; the loader resolves aliases and warns | |

The validator checks: uniqueness, dangling references, unknown kinds, asset IDs missing from the registry, and orphaned assets (warning only).

## 4. Schemas drive everything

Each table has a `*.schema.json` using a JSON-Schema subset (type, enum, min/max, required, `x-ref`, `x-asset`, `x-string`, `x-formula`, `x-effect`). Schemas feed three consumers:

1. **The validator**: an in-house C# walker. It must not use Newtonsoft.Json.Schema, which is AGPL.
2. **Codegen**: C# DTOs and key constants (§6).
3. **The editor inspector**: a generic JSON table editor under `Window/Ashen/Content`.

## 5. Formulas and numbers

Every formula is a structured object, evaluated by one `FormulaEvaluator` in Domain:

```json
{ "id": "hp", "base": 30,
  "terms": [ { "attr": "con", "pointsPerTier": 1, "gainPerTier": 4 } ],
  "level": { "every": 5, "gain": 5 }, "rounding": "floor" }

{ "id": "ar", "base": 0, "terms": [ { "attr": "str", "weight": 0.5 } ],
  "multiplier": 1, "rounding": "floorEachTermThenSum" }
```

Both formula shapes the shipped game uses are supported:

- **Tiered (derived stats):** `base + ⌊attr/pointsPerTier⌋·gainPerTier + ⌊(level−1)/every⌋·gain`.
- **Weighted (ratings):** `base + ⌊Σ⌊attr·w⌋ · multiplier⌋`.

The values above are the shipped HP and AR rows, and PF-10 imports them. They are shown here only to illustrate the shape.

Value expressions are used wherever an effect needs a number:

```json
{ "const": 6 } | { "stat": "ar" } | { "status": "strength", "of": "self" } | { "count": "hand" }
{ "add": [ … ] } | { "mul": [ … ] } | { "min": [ … ] } | { "max": [ … ] } | { "floor": … }
{ "pctOf": { "stat": "maxHp", "of": "target" }, "pct": 15, "clamp": [8, 35] }
```

Domain arithmetic is fixed point in **hundredths**: 0.35 is stored as 35. It follows these rules:

- **Parsing.** Newtonsoft reads numbers with `FloatParseHandling.Decimal`, then multiplies the `decimal` by 100. The validator rejects any value that isn't an exact hundredth (such as `0.125`).
- **Arithmetic.** Intermediate products are `long`, rescaled once per step. Where each step truncates must match the parity oracle.
- **No floating point.** There is no `float` or `double` anywhere in Domain.
- **Culture.** Boot sets `CultureInfo.DefaultThreadCurrentCulture = InvariantCulture`. Every `ToString`, hash and template also passes `InvariantCulture` explicitly.
- **Strings.** Comparison and sorting always use `StringComparer.Ordinal`.
- **Collections.** Sorts are stable (`OrderBy`, never `List.Sort`). No rule code iterates a `Dictionary` or `HashSet`.
- **Line endings.** `.gitattributes` sets `*.json text eol=lf`, so the content hash is identical on every machine.

## 6. Code generation (Editor menu `Ashen/Codegen`, and CI)

| Input | Output (`Game/Generated/`) | Used for |
|---|---|---|
| `schemas/*.schema.json` | `*Def` DTO classes + `Keys.<Table>.<Field>` consts | Deserialization, config lookup |
| `effect-ops` schema enum | `EffectOp` enum + registry binding | The interpreter dispatches on enum, not string |
| `ui/screens.json` | `ScreenId` enum | Navigation |
| UXML `name=` attributes | `UiNames.<Screen>.<Element>` consts | `Q<T>(UiNames…)` lookups |
| `ui/tokens.json` | `Tokens.uss` (CSS variables) + `TokenKeys` | Styling |
| `strings/en.json` keys | `StringKeys.<Group>.<Key>` consts | Only for strings code must name (e.g. errors) |
| Input Actions asset | `InputActionIds` | Input binding |
| `audio/contexts.json` | `AudioContextId` enum | Audio routing |

Codegen is **idempotent** and checked by CI: regenerating must produce no diff.

## 7. The effect DSL

Cards, relics, statuses, stances, enemy moves, events, place services, keepsakes and quest rewards all resolve through one interpreter.

```json
{ "trigger": "onPlay",
  "effects": [
    { "op": "damage", "target": "selectedEnemy", "amount": { "const": 6 }, "school": "physical",
      "impact": { "weaponWeight": true }, "ratingCap": 4, "hits": 1 },
    { "op": "applyStatus", "target": "selectedEnemy", "status": "bleed", "amount": { "const": 3 } },
    { "op": "if", "cond": { "statusAtLeast": { "status": "gorefire", "of": "self", "n": 1 } },
      "then": [ { "op": "draw", "amount": { "const": 1 } } ] } ] }
```

- **Ops (initial set):** `damage, block, heal, loseHp, applyStatus, removeStatus, buildUp, draw, discard, exhaust, addCard, upgradeCard, gainResource, spendResource, enterStance, exitStance, summon, setIntent, chargeMove, gainCinders, gainXp, grantReward, setFlag, recordChoice, openScreen, if, repeat, forEach, chooseOne`.
- **Triggers:** `onPlay, onDraw, onDiscard, onExhaust, turnStart, turnEnd, onHit, onDamaged, onBlockBroken, onBreak, onKill, onDeath, onEnterStance, onExitStance, onCombatStart, onCombatEnd, onRest, onArrive, onPurchase, onThreshold`.
- **Targets:** `self, selectedEnemy, randomEnemy, allEnemies, allAllies, attacker, owner, lowestHpEnemy`.
- **Conditions:** `statusAtLeast, resourceAtLeast, hpBelowPct, tagged, flagSet, historyHas, weightClass, grip, chance` (the chance roll uses the stream named in data).

Adding an op means: add an enum value to the schema, run codegen, implement `IEffectOp`, add a unit test, and the validator then accepts the op. **The interpreter never contains the name of a status or card.**

**Ordering is deterministic and set in data** (`rules/effects.json`):

- A card's effects resolve first, then the stance's triggers, then relics in acquisition order, then statuses in application order. Ties break on id, compared with `StringComparer.Ordinal`.
- Effects that a trigger spawns go into a FIFO queue, with a `maxDepth` set in data.
- Multi-hit and multi-target damage loops over hit, then target in board order. A target killed mid-card resolves `onKill` and `onDeath` at once, and later hits skip it.
- Preview runs on a **cloned** state and cloned RNG cursors. Random outcomes (`randomEnemy`, `chance`) show a range or a "random target" label. They are never rolled during a preview.

## 8. Config layering

```
rules/* + balance/* ─► preset (settings/presets/<id>.json; default: reference) ─► ascension[n] ─► customRun modifiers ─► advanced settings (settings/defaults.json + player values) ─► RunSnapshot (frozen in save)
        ▲
        persistentDataPath/Overrides (id-keyed; only when settings.modding = true)
```

**Catalogs are objects keyed by id**, not arrays: `{ "cards": { "cleave": { … } } }`. RFC 7386 merge-patch therefore overrides a single row and never wipes the whole table. To delete a row, set it to `null`. The **effective content hash** is computed at load time over canonical bytes (paths in ordinal order, LF line endings, no BOM, overrides included). The `contentHash` in the manifest is only a build-time check.

- Each layer is a JSON merge-patch validated against the same schema.
- `RunSnapshot` is immutable for the life of the run. Changing settings mid-run affects only presentation keys, which are flagged `"x-live": true` in the schema.
- A save stores the snapshot hash and the content hash. Loading with different content warns, and loading with a newer save version refuses but keeps the file.

## 9. Strings and text

- `strings/<locale>.json`: flat keys grouped by screen or kind (`title.menu.continue`, `card.cleave.name`, `status.bleed.tooltip`).
- Content rows name their strings by convention (`card.<id>.name`, `.text`, `.flavor`). The validator checks every row has its keys.
- Card and tooltip text are **templates**: `"Deal {damage} damage. Apply {bleed} Bleed."` The placeholders are filled by `Preview.*`, the same functions the resolver uses (§11).
- UXML contains **no literal text**. Text elements are `[UxmlElement] partial class LocLabel : Label` with a `[UxmlAttribute] string stringKey`, or they bind to a string source through Unity 6 data binding. Unity 6 drops undeclared UXML attributes such as `data-string`, so a custom attribute cannot carry the key.

## 10. UI data

- `ui/screens.json` registers every screen: `{ "id": "combat", "uxml": "Screens/Combat", "viewModel": "CombatViewModel", "regions": [...], "focusOrder": [...], "back": "pause" }`.
- `ui/layout.json` holds the reference resolution (1280×720), breakpoints (compact height 340, portrait) and per-region sizes in reference px. The USS reads them as variables. No pixel value is written in USS by hand except the token file.
- Components (meter, card, tooltip, modal, tray, relic, flask, intent chip) are UXML templates with **bindable properties only**. Their defaults come from `ui/components.json`.

## 11. One source of truth for numbers shown

`Preview.Damage(ctx, card, target)`, `Preview.Block(...)` and `Preview.Intent(...)` are the functions `Resolver` uses. View-models call them. Views never compute anything. A test plays 1,000 random seeded actions and asserts that the preview equals the resolved outcome.

## 12. Enforcement (tests that fail the build)

These checks run inside the `Enforcement` and `Content` suites ([10 §4](10-BRANCHING-VERSIONING-CI.md)). They are test **cases**, not separate CI jobs.

| Test | Checks |
|---|---|
| `NoEngineInDomain` | The Domain and Application assemblies do not reference `UnityEngine` |
| `NoMagicStrings` | A Roslyn-free scan of `Game/Domain`, `Game/Application` and `Game/Presentation` finds no string literal except in `Generated/`, the allowlist (`nameof`, log categories from `LogKeys`) and tests |
| `NoMagicNumbers` | Domain has no numeric literal other than `0`, `1` or `-1` outside `Generated/` |
| `NoContentIdsInCode` | The set of all content IDs has no intersection with the C# string literal set |
| `ContentValid` | The validator reports zero errors |
| `CodegenClean` | Regenerating produces no diff |
| `RegistryComplete` | No dangling asset ID; orphans are listed in `docs/ASSET-GAPS.md#unused` |
| `PreviewEqualsResolve` | The property test from §11 |
| `GoldenReplays` | Seeded replays match the stored hashes |
| `SaveRoundTrip` | Serialize, then deserialize, gives an equal state; migrations are applied in order; resume-equivalence: checkpoint every k commands, reload, and the state hash matches |
| `AiDisclosurePresent` | `about.aiDisclosure` exists and names the modalities recorded in `Provenance/generated/manifest.json`. The title footer, first-launch notice and About bind it. `README.md` contains the statement |
| `ProvenanceComplete` | Every registry entry has `origin` (`imported` from the old repo, or `generated` with tool and prompt). Nothing is third-party licensed |
| `ParityOracle` | C# results equal `Tests/Oracle/*.json` generated from the shipped JS (RNG streams, derived stats, damage, maps, seats, hand rules) |
| `AsmdefGraph` | Assembly references point one way only (Domain → Application → Content → Presentation → Platform); `Ashen.Codegen` references none of them |
