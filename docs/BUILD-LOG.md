# Build log

Newest entries go at the top. Each entry records the story, what landed, the tests, what wasn't verified, and the next step. After a context summary, re-read this file and `DECISIONS.md` before continuing.

## Phase F0 Foundation (in progress)

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
