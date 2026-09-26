# Build log

Newest entries go at the top. Each entry records the story, what landed, the tests, what wasn't verified, and the next step. After a context summary, re-read this file and `DECISIONS.md` before continuing.

## Phase F0 Foundation (in progress)

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
