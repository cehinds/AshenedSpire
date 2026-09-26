# 05 — Activity flows (what the player does)

These are Mermaid activity diagrams. Nodes name **screens** (`screen:id`, matching `ui/screens.json` and [04](04-WIREFRAMES.md)) and **decisions** (diamonds). Tuning values appear as **data keys**, never as numbers. Every node has an exit. A pause is reachable from every run screen through AF-12.

Revision 2 applies the findings of the four sub-agent reviews ([07](07-DIAGRAM-REVIEW.md)). The rules parity items were checked against `D:\repos\AshenSpire\src` with file and line evidence.

Conventions:
- `([...])` is a start or end.
- `[...]` is an activity or screen.
- `{...}` is a decision.
- `[[...]]` is a sub-flow drawn in another diagram.

---

## AF-01 Boot → Title

```mermaid
flowchart TD
    S([App launch]) --> P{profile readable?}
    P -- "no / older / newer" --> R[screen:profileRecovery<br/>restore copy or start fresh]
    R --> G
    P -- yes --> G[screen:title · startup gate]
    G -- any input --> AI{AI disclosure already acknowledged in profile?}
    AI -- no --> AIN[W2 notice: made with generative AI · Continue] -- continue, record in profile --> T
    AI -- yes --> T[screen:title · menu · footer shows AI disclosure]
    T -- "Continue (enabled only if a valid slot exists)" --> RES[[AF-10 Resume]]
    T -- New --> NS[screen:slots · new]
    NS -- pick slot --> NR[W2 Start in slot n?]
    NR -- confirm --> CR[[AF-02 Character creation]]
    NR -- back --> NS
    NS -- back --> T
    T -- Load --> L[screen:slots · load]
    L -- pick filled slot --> LR[W2 Load slot n?] -- confirm --> RES
    LR -- back --> L
    L -- pick empty slot --> L
    L -- back --> T
    NS & L -- "delete ✕" --> W2B[W2b delete confirm · focus Back] -- "back or confirm" --> T
    T -- Journal --> J[screen:journal<br/>History · Profile · Collection] -- back --> T
    T -- Custom Run --> CU[screen:customRun] -- continue --> NS
    CU -- back --> T
    T -- Settings --> ST[screen:settings<br/>About holds credits and AI disclosure] -- back --> T
    T -- Quit --> W2E[W2e quit confirm] -- confirm --> Q([Exit])
    W2E -- back --> T
```

## AF-02 Character creation (screen:creation, W1c rail)

```mermaid
flowchart TD
    S([From screen:slots · new]) --> C1[pane Class<br/>nothing preselected]
    C1 -- Next with no class --> RF1[refusal: choose a class · focus the grid] --> C1
    C1 -- "pick class (autoAdvance on)" --> C2
    C1 -- "pick class, then Next" --> C2
    C2[pane Character<br/>name · stat mode · keepsake · sprite · sigil · tint] --> SM{stat mode}
    SM -- Standard --> C2OK[character complete]
    SM -- Assign --> AS[allocate points<br/>attributeRules.lean] --> AP{all points spent?}
    AP -- "no, Next pressed" --> RF2[refusal: points left · focus allocator] --> AS
    AP -- yes --> C2OK
    AS -- switch to Standard --> C2OK
    C2OK -- Next --> C3[pane Equipment<br/>armour · right · left · equip · relic · kit]
    C3 --> EQ{armour chosen and grip legal?}
    EQ -- "no, Next pressed" --> RF3[refusal names the socket] --> C3
    EQ -- yes --> C4[pane Review<br/>journey · seed · slot receipts]
    C4 -- Begin --> V{full loadout valid?}
    V -- no --> RF4[focus the failing field] --> C3
    V -- yes --> OW{slot occupied?}
    OW -- yes --> CF[W2c replace save ⟲ · focus Back]
    CF -- back --> C4
    CF -- confirm --> B[[AF-03 Begin run]]
    OW -- no --> B
    C2 -- Back --> C1
    C3 -- Back --> C2
    C4 -- Back --> C3
    C2 & C3 & C4 -. rail jump to a completed pane .-> C1
    C1 -- "Back / ×" --> SL([screen:slots])
```

## AF-03 Begin run → Prologue → first map

```mermaid
flowchart TD
    S([Begin, loadout already valid]) --> SNAP[ensure profile · freeze RunSnapshot · draw seat order on seats stream ·<br/>apply keepsake · apply deck mode · build act-1 map · checkpoint]
    SNAP --> DM{deck mode sealed or draft?}
    DM -- yes --> DR[screen:rewards · draft variant<br/>pick until deck complete] --> PP
    DM -- no --> PP{prologue playback on?}
    PP -- no --> MAP
    PP -- yes --> NX{next enabled scene<br/>in prologue.json order}
    NX -- scene --> SC[screen:prologue<br/>art · class layer · narration]
    SC -. checkpoint scene index .-> SAVE[(slot)]
    SC -- Continue --> NX
    SC -- Pause --> PA[paused]
    PA -- Resume --> SC
    PA -- Quit to title --> T([screen:title])
    SC -- "Hold to skip ⟲" --> MAP
    NX -- "none left: Set forth" --> MAP
    MAP{journey type} -- Classic --> AM[[AF-04 Act map]]
    MAP -- World Journey --> WJ[[AF-09 World Journey]]
```

## AF-04 Classic Climb act loop

```mermaid
flowchart TD
    S([Act start: seat × tier<br/>map built now on map stream]) --> M[screen:actMap]
    M -- select node --> TR0[node tray overlay: type · hint · boss preview]
    TR0 -- "Enter, or repeat pick after ui.map.repeatPickDelayMs" --> N{node kind}
    TR0 -- Back --> M
    M -- Armoury --> AMY[screen:armoury] --> M
    M -- Menu --> PZ[[AF-12 Pause]]
    N -- "monster / elite" --> F[[AF-05 Combat]]
    N -- "unknown (resolved at map build)" --> U{node.resolved.kind}
    U -- event --> EV[[AF-07 Event]]
    U -- fight --> F
    U -- shrine --> CAMP[[AF-06 Rest · field camp]]
    U -- treasure --> TRS
    N -- shrine --> RS[[AF-06 Rest · shrine]]
    N -- merchant --> SH[[AF-08 Merchant]]
    N -- treasure --> TRS[treasure] --> RW
    N -- boss --> LDQ{boss encounter has a legacy dungeon?}
    LDQ -- yes --> LD[screen:legacyDungeon<br/>scenes: dialogue · rest · treasure · fight]
    LDQ -- no --> BF[[AF-05 Combat: boss]]
    LD -- fight --> LF[[AF-05 Combat]]
    LF -- win --> LRW[screen:rewards] --> LD
    LD -- "flee: DEX roll per data" --> FL{roll succeeds?}
    FL -- yes --> M
    FL -- no --> LF
    LD -- boss door --> BF
    F -- win --> RW[screen:rewards] --> M
    EV & RS & CAMP & SH --> M
    F & LF -- lose --> END[[AF-11 Run end: defeat]]
    BF -- lose --> END
    BF -- win --> VC{victory rules<br/>rules/victory.json}
    VC -- "not final: boss rewards" --> BRW[screen:rewards] --> TL[mark seat tower lit · full heal per data ·<br/>next seat, act map built] --> S
    VC -- final --> PV{post-victory hook}
    PV -- default --> WIN[[AF-11 Run end: victory]]
    PV -- endless --> BRW
    PV -. "reserved: 4th tower" .-> FUT[(future journey)]
```

The Blighted Valkyrie causeway needs no special node. At the final tier, the boss pool is the seat's boss rows plus the one boss row that has no seat (PF-07).

## AF-05 Combat turn (screen:combat)

```mermaid
flowchart TD
    S([Combat start]) --> SU[carry HP · Mana · Stamina from run · enemies roll HP on enemyHP ·<br/>shuffle deck, Innate on top · combatStart triggers · roll first intents]
    SU --> TS[Player turn start<br/>clear player Block · Actions = max − pending stagger loss ·<br/>draw: opening hand on turn 1, else per drawMode ·<br/>owner turn-start hooks: Madness, DoTs, Regen]
    TS --> PD0{player HP ≤ 0?}
    PD0 -- yes --> LOSE([AF-11 defeat])
    PD0 -- no --> READ[read intents · inspect hand, meters, statuses]
    READ --> A{player action}
    A -- select card --> AFF{affordable?<br/>Actions or X · Mana · Stamina}
    AFF -- no --> REF[refusal names the resource · nothing paid] --> READ
    AFF -- yes --> TG{needs a target?}
    TG -- yes --> PICK[armed: legal targets outlined ·<br/>ghost preview from Preview]
    PICK -- cancel --> READ
    PICK -- choose target --> RES
    TG -- no --> RES[pay all costs · queue card effects · resolve queue]
    RES --> END1{combat over?<br/>checked after every queued action}
    END1 -- "player dead" --> LOSE
    END1 -- "all enemies dead" --> WIN([Victory → rewards])
    END1 -- no --> READ
    A -- Potions --> FL{charge available?}
    FL -- no --> REF
    FL -- yes --> RES
    A -- "select player → Armaments popover" --> SW{swap cost affordable?}
    SW -- no --> REF
    SW -- yes --> RE[pay · restamp lent cards · re-derive pools] --> READ
    SW -- close --> READ
    A -- End turn --> DC{overflow = discard AND hand > capacity?<br/>shipped: overflow keep, retain on}
    DC -- yes --> DIS[discard chooser] --> ET
    DC -- no --> ET[player turn end<br/>turn-end triggers → status decay → +1 Stamina if none spent this turn →<br/>Ethereal exhaust, rest retained → Actions 0]
    ET --> ENT[[PF-05 Enemy turn]]
    ENT --> END2{combat over?}
    END2 -- "player dead" --> LOSE
    END2 -- "all enemies dead" --> WIN
    END2 -- no --> TS
    READ -- Menu --> PZ[[AF-12 Pause]]
```

## AF-06 Rest place (screen:rest)

```mermaid
flowchart TD
    S([Arrive: shrine · field camp · inn · chapel]) --> AR[apply place tags on arrive<br/>refill flasks if tagged · a resumed visit tops up once]
    AR --> R[screen:rest<br/>options filtered by place services and relics]
    R -- "Rest: tap = review, hold = commit" --> H[heal and mana per place row] --> MU{multi-use?}
    R -- "Smith (W1i) · Extract (W1j) · Install (W1k)" --> SV{affordable and legal?}
    SV -- no --> RF[refusal] --> R
    SV -- "yes, confirm" --> DO[commit service] --> MU
    SV -- back --> R
    R -- Flask split --> FS[re-split within the pool] --> R
    R -- Level up --> LV[W-22 level-up allocator] --> R
    MU -- "no: single-use (default)" --> LEAVE([Back to map or journey])
    MU -- yes --> R
    R -- "Continue: multi-use, or a relic forbids Rest" --> LEAVE
    R -- Menu --> PZ[[AF-12 Pause]]
```

- **Shipped behaviour kept:** at a single-use place, the first committed Rest, Smith, Extract or Install ends the visit.
- **Validator rule:** every place row must yield at least one exit option, so a visit can never softlock.

## AF-07 Event (screen:event)

```mermaid
flowchart TD
    S([Event node]) --> G{history or flag gated chain step available?}
    G -- yes --> E[screen:event · chain step]
    G -- no --> PICK[pick an eligible event on the events stream] --> E
    E -- tap response --> RV[review: exact effect preview]
    RV -- back --> E
    RV -- "illegal: requirement unmet" --> E
    RV -- "commit (hold if binding)" --> FX[run response effects through the DSL<br/>recordChoice · setFlag · rewards · combat · class swap op]
    FX --> RSV[state Resolved · result sentence replaces responses]
    RSV -- "Continue / Steel yourself" --> N{effect outcome}
    N -- combat --> CB[[AF-05 Combat]]
    N -- reward --> RW[screen:rewards] --> M
    N -- none --> M([Back to map])
    E -- Menu --> PZ[[AF-12 Pause]]
```

- **Validator rule:** every event has at least one response with no requirement (the always-legal leave).
- The class-swap op is legal only in the event that the data flags (Turncoat Mirror).

## AF-08 Merchant (screen:merchant)

```mermaid
flowchart TD
    S([Enter merchant node]) --> RS{resuming with saved stock?}
    RS -- yes --> SH
    RS -- no --> ROLL[roll stock on shop stream · apply price multiplier ·<br/>roll smith offer on smith stream · checkpoint] --> SH
    SH[screen:merchant · rail: Cards · Armaments · Arts · Relics · Flasks · Services · Sell]
    SH -- select offer --> BUY{enough cinders and a free slot?}
    BUY -- no --> RF[refusal with the reason] --> SH
    BUY -- yes --> CONF[W2a: Spend x of y cinders?] -- confirm --> PAY[pay · grant · mark sold · checkpoint] --> SH
    CONF -- back --> SH
    SH -- card removal --> RMC{deck above floor and enough cinders?}
    RMC -- no --> RF
    RMC -- yes --> RM[pick card · pay removeBase + removeStep × removals] --> SH
    SH -- Sell --> SB{shopSell on?}
    SB -- no --> SH
    SB -- yes --> SL[sell at data price · add to buy-back list] --> SH
    SH -- Leave --> CLR[clear saved stock] --> M([Back to map])
    SH -- Menu --> PZ[[AF-12 Pause]]
```

## AF-09 World Journey (P1)

```mermaid
flowchart TD
    S([Journey start at Crownfall]) --> W[screen:worldMap]
    W -- select place --> I[context band · never moves] --> W
    W -- "Enter / Travel along an open edge" --> N{node type}
    N -- fight --> F[[AF-05 Combat]]
    F -- win --> FR[screen:rewards] --> W
    F -- lose --> D[[AF-11 Run end: defeat]]
    N -- grace --> RS[[AF-06 Rest · chapel rules]] --> W
    N -- "cache / landmark" --> RW[screen:rewards] --> W
    N -- "city / service" --> LM[screen:localMap<br/>inn · chapel · smith · merchant · quest board] --> W
    N -- quest --> Q[screen:dialogue · quest board] --> W
    N -- gate --> GT{requirement met?}
    GT -- yes --> OPEN[open edge] --> W
    GT -- no --> MSG[show requirement] --> W
    N -- dungeon --> DG[screen:localMap · dungeon to boss chamber]
    DG -- boss lost --> D
    DG -- "boss won, not finale" --> W
    DG -- "boss won, finale" --> WIN[[AF-11 Run end: victory]]
    W -- "Menu / Save & quit" --> PZ[[AF-12 Pause]]
```

## AF-10 Resume

```mermaid
flowchart TD
    S([Load slot]) --> PICK[read primary, mirror and bak · choose highest valid gen]
    PICK --> CHK{hash of stored bytes valid?}
    CHK -- "no valid copy" --> K[refuse · keep files · notice] --> T([screen:title])
    CHK -- ok --> V{save version}
    V -- newer --> K
    V -- older --> MIG[migrate one version at a time] --> MOK{migrated ok?}
    MOK -- no --> K
    MOK -- yes --> LOG
    V -- current --> LOG{command log gen matches and content hash same?}
    LOG -- yes --> RPL[replay records · stop at bad crc or stateHash mismatch]
    LOG -- no --> SNAP[resume at snapshot · warn: last turn may be lost]
    RPL & SNAP --> WH{saved location}
    WH -- prologue --> P([screen:prologue · saved or next enabled scene])
    WH -- "rewards · incl. pending pick or draft" --> R([screen:rewards])
    WH -- combat --> C([screen:combat · same turn and hand])
    WH -- merchant --> MC([screen:merchant · same stock])
    WH -- rest --> RS([screen:rest])
    WH -- event --> EV([screen:event · same event and state])
    WH -- legacy dungeon --> LD([screen:legacyDungeon · same scene])
    WH -- "map · worldMap · localMap" --> MP([same map · camera restored])
```

## AF-11 Run end

```mermaid
flowchart TD
    S([Run ends: defeat · victory · abandon]) --> K{kind}
    K -- defeat --> DP[defeated pose · defeat stinger]
    K -- victory --> VP[victory stinger]
    K -- abandon --> AB[no animation]
    DP & VP & AB --> REC[record result · update progress · evaluate unlocks ·<br/>clear slot — one atomic profile + slot commit]
    REC --> SUM[screen:runEnd<br/>EMBER RESTORED or YOU PERISHED · seed · floor · killer · deck · unlocks]
    SUM -- Run history --> H([screen:journal · History])
    SUM -- Return to title --> T([screen:title])
```

## AF-12 Pause (from any run screen)

```mermaid
flowchart TD
    S([Menu from any run screen]) --> P[screen:pause]
    P -- Resume --> B([previous screen, same state])
    P -- Deck --> PV[screen:pileViewer] --> P
    P -- "Armoury (out of combat only)" --> AM[screen:armoury] --> P
    P -- Settings --> ST[screen:settings · run keys read-only] --> P
    P -- "Save & quit ⟲" --> SV{checkpoint ok?}
    SV -- yes --> T([screen:title])
    SV -- no --> W1R[W1r save failed: Retry save] -- retry --> SV
    W1R -- back --> P
    P -- "Abandon run ⟲" --> AB[W2 confirm · destructive tone · focus Back]
    AB -- back --> P
    AB -- confirm --> END[[AF-11 Run end: abandon]]
```
