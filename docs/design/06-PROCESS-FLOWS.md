# 06 — Process flows (what the system does)

These are system-side flows. The actors are the layers **Content**, **Domain**, **Application**, **Presentation** and **Platform** ([08 §2](08-DATA-CONVENTIONS.md)).

This is revision 2, which applies the architecture and rules-parity reviews ([07](07-DIAGRAM-REVIEW.md)). The combat and run-generation flows mirror the shipped engine (`D:\repos\AshenSpire\src\engine\combat.js`, `actions.js`, `triggers.js`, `mapgen.js`, `actmap.js`) as the rules-parity review verified it. The parity oracle (PF-10) proves the C# matches.

---

## PF-01 Asset pipeline (editor or batchmode, before anything else)

```mermaid
flowchart TD
    SRC[(Old repo assets and art<br/>5,241 files · 5,211 webp · 6 svg · 0 audio · 0 fonts)] --> SCAN[Tools/scan_assets: walk allowed roots · hash · dims · alpha]
    SCAN --> PRE[install AssetPostprocessor + presets first<br/>rules from Editor/Config/importRules.json]
    PRE --> CONV{format}
    CONV -- webp --> W2P[Blender 4.0 headless webp to png, lossless, alpha kept<br/>fallback: Pillow]
    CONV -- svg --> RAS[Vector Graphics import<br/>fallback: Blender SVG render, then Edge headless screenshot]
    CONV -- "json · md provenance" --> PROV[copy to Provenance/]
    GEN[gap list from ASSET-GAPS.md · generate style-matched art with 1–3 references ·<br/>Provenance/generated/prompts.json · Assets/Art/Generated, committed] --> IMP
    W2P & RAS --> IMP["copy into Assets/Art/Imported inside StartAssetEditing / StopAssetEditing"]
    IMP --> ATL[Sprite Atlas V2 per suite × outfit, per VFX, per enemy<br/>SpriteAtlasUtility.PackAllAtlases]
    ATL --> ADDR[Addressables: atlas is the entry · labels from Editor/Config/addressablesPlan.json ·<br/>Include in Build off for served atlases · Analyze duplicate-dependency rule]
    ADDR --> REG[assets/registry.json GENERATED<br/>assetId → address + sprite name · dims · alpha · owner · origin: imported or generated · source or prompt + hash]
    REG --> GAP[fonts: Unity default via PanelTextSettings · audio: synth only · both logged in ASSET-GAPS]
    GAP --> VAL{validator}
    VAL -- dangling id --> FAIL([build fails])
    VAL -- orphans --> WARN[ASSET-GAPS.md#unused]
    VAL -- ok --> DONE([ASSET-INVENTORY.md with counts and derivations])
```

## PF-02 Content load and config layering (boot)

```mermaid
sequenceDiagram
    participant Boot as Platform.Boot
    participant Src as Content.IContentSource
    participant Store as Platform.Storage
    participant Val as Content.Validator
    participant Lay as Content.ConfigLayers
    participant Reg as Content.Registry
    participant App as Application.GameRoot
    Boot->>Boot: set InvariantCulture as default thread culture
    Boot->>Src: await ReadTextAsync manifest.json
    Src-->>Boot: file list, schemaVersion, build contentHash
    loop each manifest entry, ordinal path order
        Boot->>Src: await ReadTextAsync entry (File or UnityWebRequest source)
        opt settings.modding is true and override path matches a manifest entry
            Boot->>Src: apply id-keyed merge-patch, max 1 MB, depth 64
        end
    end
    Boot->>Reg: resolve aliases (warn on each use)
    Boot->>Val: validate all tables, schemas, registry, rule sanity
    alt errors
        Val-->>Boot: ValidationReport with file, row, field, rule
        Boot->>App: screen:contentError in dev, safe notice in player
    else ok
        Boot->>Reg: index by id and freeze
        Boot->>Boot: effective contentHash over canonical bytes incl. overrides
        Boot->>Lay: base config = rules plus balance
        Boot->>Store: load profile with mirror fallback
        Boot->>App: start with Registry, BaseConfig, Profile, contentHash
    end
```

## PF-03 Command → resolve → events → view (every gameplay action)

```mermaid
sequenceDiagram
    participant V as Presentation.View
    participant VM as ViewModel
    participant A as Application.CommandBus
    participant L as Domain.Legality
    participant R as Domain.Resolver
    participant E as Application.EventBus
    participant S as Application.SaveService
    V->>VM: intent from pointer, keys, pad or touch
    VM->>R: Preview on cloned state and cloned RNG cursors
    R-->>VM: display values, random parts shown as ranges
    VM->>A: Submit Command with type and payload
    A->>L: CanExecute state, cmd
    alt illegal
        L-->>A: Refusal reasonKey
        A-->>VM: Refused, nothing paid
        VM-->>V: show refusal string at the control
    else legal
        A->>R: Execute state, cmd, rng cursors
        R-->>A: new state, new cursors, ordered DomainEvents
        A->>S: Append cmd with stateHashAfter, checkpoint at commit points
        A->>E: publish events in order
        E-->>VM: events
        VM-->>V: animate timeline, skippable, reduced-motion aware
    end
```

## PF-04 Card play resolution (the shipped queue semantics)

```mermaid
flowchart TD
    S([PlayCard cmd]) --> C1{check all costs<br/>Actions or X, then Mana, then Stamina}
    C1 -- short --> REF([Refusal names the resource · nothing paid])
    C1 -- ok --> PAY[pay all costs · remove card from hand]
    PAY --> Q[enqueue card effects, then cardPlayed triggers · FIFO queue]
    Q --> NEXT{next queued action}
    NEXT -- damage op --> HIT0[for each hit, then each target in board order · skip dead targets]
    HIT0 --> D1["1 base + rating bonus, bonus capped by ratingCap"]
    D1 --> D2[2 school add] --> D3[3 Strength] --> D4[4 dealt multipliers]
    D4 --> D5[5 taken multipliers] --> D6["6 rating resistance: 1 − min(cap, R/(R+k)), R = target Poise or Ward rating"]
    D6 --> D7[7 tag exposures, tagged hits only] --> D8[8 school resistance and vulnerability]
    D8 --> D9[9 floor · Block absorbs · then HP]
    D9 --> HPL{HP lost greater than 0?}
    HPL -- no --> DD[emit damageDealt · queue onHit triggers]
    HPL -- yes --> HIT[emit damageDealt · fill Poise or Ward meter by impact<br/>break applies at once · hpLost · arcane build-up · queue triggers]
    DD & HIT --> DEATH[HP ≤ 0 marks dead now · HP-phase triggers queued]
    DEATH --> HIT0
    HIT0 -- hits done --> ENDC
    NEXT -- applyStatus with a proc row --> PROC{build-up ≥ threshold?}
    PROC -- yes --> BURST[reset build-up · queue burst: loseHp, poise, stagger, effects ·<br/>grant Resist only if target tags match the row] --> ENDC
    PROC -- no --> ENDC
    NEXT -- other op --> OP[dispatch by EffectOp enum] --> ENDC
    ENDC{combat over? checked after every action}
    ENDC -- yes --> FIN([stop · card is not placed])
    ENDC -- no --> NEXT
    NEXT -- queue drained --> KW[place card: Exhaust · Power removed · discard] --> OUT([events published])
```

All multipliers, caps, `k`, impact bands and thresholds come from `rules/damage.json`, `rules/meters.json` and the status rows. Previews run the same path on a clone.

## PF-05 Enemy turn and intent selection

```mermaid
flowchart TD
    S([Enemy turn start]) --> CB[clear all enemy Block]
    CB --> EACH{next living enemy in board order}
    EACH --> DOT[this enemy's turn-start hooks: DoTs] --> DEAD1{combat over?}
    DEAD1 -- yes --> STOP([end combat])
    DEAD1 -- no --> BRK{skip flag set?}
    BRK -- yes --> SKIP[skip turn · clear flag] --> TE
    BRK -- no --> CH{pending charged move?}
    CH -- "turn ≥ resolveOnTurn" --> EX
    CH -- waiting --> TE
    CH -- none --> DL{intent move has a delay?}
    DL -- yes --> WC[run whileCharging once · pending = now + delay.turns] --> TE
    DL -- no --> EX[execute the move through the DSL] --> DEAD2{combat over?}
    DEAD2 -- yes --> STOP
    DEAD2 -- no --> TE[this enemy's turn-end hooks, then status decay]
    TE --> EACH
    EACH -- all done --> NI{for each living enemy: next intent}
    NI --> PEND{pending charge or skip flag?}
    PEND -- charge --> KEEP[keep the pending intent]
    PEND -- skip --> STG[intent = staggered]
    PEND -- no --> POOL[pool = unlocked moves minus those at maxConsecutive ·<br/>empty pool falls back to all unlocked]
    POOL --> WR[weighted roll on enemyAI stream · zero total weight picks the first]
    KEEP & STG & WR --> STORE[store intent as move id · numbers come from Preview.Intent at render time]
```

- `firstMove` is used only by the combat-start roll.
- HP phases are **not** looked up here. They fire when HP changes, once each, and queue effects such as unlocking a move.

## PF-06 Save, checkpoint and resume

```mermaid
sequenceDiagram
    participant A as Application
    participant S as SaveService
    participant FS as Platform.Storage
    Note over A,S: commit points = node entered, reward claimed, purchase,<br/>rest action, combat turn end, prologue scene end
    A->>S: Checkpoint runState
    S->>S: gen + 1, serialize envelope with saveVersion, gen, contentHash, snapshotHash, rngCursors, state
    S->>FS: write slotN.json.tmp, Flush to disk
    S->>FS: read back tmp, parse, compare hash
    S->>FS: File.Replace tmp over slotN.json keeping slotN.json.bak, or File.Move on first save, retry 3 times
    S->>FS: write slotN.mirror.json through the same tmp and replace path
    S->>FS: start slotN.gen.log, then delete older gen logs
    A->>S: Append cmd between checkpoints
    S->>FS: append record with gen, seq, len, crc32, cmd, stateHashAfter, Flush to disk
    Note over A,FS: Resume
    A->>S: Load slotN
    S->>FS: read primary, mirror and bak, pick highest valid gen
    S->>S: verify hash of raw payload, then version check
    alt saveVersion newer than build
        S-->>A: refuse, keep file, slot read-only
    else older or equal
        S->>S: migrate one version at a time
        S->>S: replay records whose gen matches, stop at first bad crc
        S->>S: stop replay on stateHashAfter mismatch or changed contentHash
        S-->>A: runState, screen to open, warnings
    end
```

## PF-07 Run generation (Classic Climb)

```mermaid
flowchart TD
    S([Begin]) --> SEED[seed → mulberry32 streams as in engine/rng.js, list in rules/rng.json<br/>map · shuffle · cardRewards · relicRewards · flaskRewards · armaments ·<br/>enemyAI · enemyHP · events · shop · misc · smith · combatProcs · seats]
    SEED --> SEATS[shuffle seats on seats stream · a pinned first seat rotates the order]
    SEATS --> ACT([at each act start: build this act's map])
    ACT --> GRID[walk paths on map stream · crossing paths merge ·<br/>top floor = single shrine plus boss terminals · shape from balance/map.json]
    GRID --> W[type nodes floor by floor: weighted roll filtered by rules<br/>floor gates · no adjacent identical non-monster · rest before elite]
    W --> OK{minimum elites and merchants met?}
    OK -- "no, tries below balance.map.maxRerolls" --> W
    OK -- "no, tries used" --> FORCE[force-place to restore minimums] --> RBE
    OK -- yes --> RBE[ensure rest before elite]
    RBE --> UNK[resolve unknown nodes now on events stream by unknownWeights]
    UNK --> BOSS[boss pool = seat boss rows plus, at the final tier, rows with no seat ·<br/>subset on map stream if the pool is wider than the map]
    BOSS --> SAVE[store this act's map · checkpoint]
```

- Legacy dungeons are **not** attached here. Entering a boss node whose encounter carries a dungeon swaps the fight for the dungeon (AF-04).
- The shipped value of `maxRerolls` is 40.

## PF-08 Presentation pipeline (screen composition)

```mermaid
flowchart LR
    SJ[ui/screens.json] --> NAV[Navigator<br/>ScreenId → UXML + ViewModel + shell]
    UX[UXML grey box<br/>regions named per UiNames] --> NAV
    LAY[ui/layout.json<br/>bands · mins · breakpoints] --> NAV
    NAV --> VM[ViewModel binds to Application state]
    VM --> COMP[Component kit<br/>Shell · Workspace · Confirm · Meter · CardView · Combatant · IntentChip · Tray]
    TOK[ui/tokens.json → Tokens.uss] --> SKIN
    STR[strings/en.json → LocLabel stringKey] --> COMP
    COMP --> SKIN[Skin: registry sprites via Addressables]
    SKIN --> ANIM[AnimationDirector<br/>suite by grip · state frames · VFX · reduced motion]
    ANIM --> OUT([Frame])
```

## PF-09 Validation gate (in-session self-gate)

```mermaid
flowchart TD
    S([component finished]) --> CG[batchmode: Ashen.Codegen, which references no game assemblies]
    CG --> CMP{compile clean?<br/>wait until not compiling}
    CMP -- no --> FIX[fix, then rerun]
    FIX --> CG
    CMP -- yes --> T[EditMode: rules · oracle parity · validator · enforcement · golden replays]
    T -- fail --> FIX
    T -- pass --> PM[PlayMode smoke: boot → screen → interaction · focus walk]
    PM -- fail --> FIX
    PM -- pass --> SHOT[capture via PanelSettings.targetTexture and a camera RenderTexture<br/>at 1280×720 · 1920×1080 · 844×390 · 390×844 · 375×667 · 360×780]
    SHOT --> REV[screenshot-review sub-agent vs wireframe and story ACs]
    REV -- "blocker or major" --> FIX
    REV -- clean --> COMMIT[commit · BUILD-LOG · DECISIONS · CHANGELOG] --> NEXT([next component])
```

## PF-10 Content export and parity oracle (new)

```mermaid
flowchart TD
    JS[(Old repo src/content/index.js<br/>plus src/engine and src/model)] --> EXP[Tools/export-content.mjs<br/>Node 22 ESM import, read-only]
    EXP --> FN{non-serializable field?<br/>function, class instance, Symbol}
    FN -- yes --> GAPS[export-gaps.json: manual DSL port list<br/>build fails until every entry is mapped]
    FN -- no --> NORM[normalise: keep legacyId · id to lower_snake_case ·<br/>aliases · decimals checked as exact hundredths]
    NORM --> STR[extract user text to strings/en.json · card text to templates]
    NORM --> CAN[canonical JSON: id-keyed objects · sorted keys · LF · no BOM]
    STR --> CAN
    CAN --> MAN[manifest.json with file list and build contentHash]
    JS --> ORC[Tools/oracle.mjs: seeded calls into the shipped JS]
    ORC --> REF[Tests/Oracle/*.json<br/>14 streams × first 64 draws · derived stats · ratings · damage grid ·<br/>maps per seed · seat order · hand rules · golden combat logs]
    MAN --> VAL{validator, count gate and codegen}
    VAL -- errors --> FIXX([fix exporter or schema, never the numbers])
    VAL -- ok --> TST[EditMode parity tests read the oracle]
    REF --> TST
    TST -- mismatch --> DEC[DECISIONS.md: shipped behaviour kept unless it is a proven bug]
    TST -- pass --> DONE([content and oracle committed])
```

## PF-11 Windows player build (new)

```mermaid
flowchart TD
    S([Ashen.Editor.Cli.BuildWindows]) --> CG[codegen, fail on diff]
    CG --> CMP{compile clean?}
    CMP -- no --> F([fail])
    CMP -- yes --> V[ContentValid and RegistryComplete]
    V -- errors --> F
    V -- ok --> T[EditMode suite incl. parity and golden replays]
    T -- fail --> F
    T -- pass --> SA[pack Sprite Atlases · Addressables Analyze duplicate rule]
    SA --> AB[AddressableAssetSettings.BuildPlayerContent]
    AB --> LX[link.xml preserves Generated DTOs for Newtonsoft under IL2CPP]
    LX --> BP[BuildPipeline.BuildPlayer Win64 IL2CPP · StreamingAssets/Content included]
    BP --> SM[player smoke: -batchmode -replay golden_01 · exit code = stateHash match]
    SM -- mismatch --> F
    SM -- ok --> ZIP[Build/Windows plus build-info.json: git sha · contentHash · saveVersion · not committed]
    ZIP --> OK([artifact ready])
```

## PF-12 Audio: generated tracks with a procedural fallback (new)

```mermaid
flowchart LR
    GENA[AI-generated music beds, stingers, optional SFX and voice<br/>Assets/Generated/Audio · Vorbis · provenance manifest] --> SLOT[context file slots in audio/contexts.json]
    SLOT --> ROUTE
    SFX[audio/sfx.json<br/>tone and noise layers] --> GEN[SynthAudio: render layers to float PCM at 44.1 kHz<br/>off the main thread · own System.Random, never a domain stream]
    MUS[audio/music.json<br/>looping beds] --> GEN
    GEN --> CLIP[AudioClip.Create plus SetData on the main thread · cache by recipe id]
    CTX[audio/contexts.json<br/>8 contexts → bed, file slot or silence] --> ROUTE[AudioRouter]
    CLIP --> ROUTE
    EVT[DomainEvents] --> MAPE[event type → recipe id<br/>family then specific then default]
    MAPE --> ROUTE
    ROUTE --> MIX[AudioMixer groups: music · sfx · ui · master volume from settings]
```
