# 04 — Wireframes

These wireframes are the **build blueprint**. The implementation session builds each screen as a UI Toolkit UXML grey box that matches the frame below, binds it to a view-model, and only then skins it with registry assets ([06 PF-08](06-PROCESS-FLOWS.md)).

They follow the shipped wireframe atlas (`D:\repos\AshenSpire\docs\architecture-handoff\RESPONSIVE-WIREFRAMES.md`, W0–W4). Where a detail is not drawn here, **that atlas and `implementation/approved-wireframes.md` are the tie-breakers**, together with the owner decisions quoted in them.

## 0. Global rules (all encoded in `ui/layout.json` and `ui/tokens.json`, never in USS or C#)

| Rule | Value (data key) |
|---|---|
| Reference canvas | **Two PanelSettings**, both scaling with screen size: **wide 1280×720** and **narrow 430×780**, the shipped narrow board. The Navigator swaps them by layout mode. The ASCII frames below are drawn for wide. The portrait references are iPhone SE 375×667 and Galaxy S24 360×780 |
| Physical minimums | `layout.json#minPhysical`, enforced **after** scaling: touch target 44 px · hand 208 px · combat footer 56 px · map header 52 px · pile button 64 px · value text 12 px · header text 10 px |
| Height bands | `standard` ≥465 px · `compact` 340–464 px ("short-wide rails"; the shipped build supports 844×390 and gates 740×360 and 667×375 by default) · below `gateBelowH` the **upright gate** asks the player to rotate. All thresholds are data (`gateBelowH`, `shortWideMinH`, setting `uprightGate`) |
| Layout mode | `narrow` when width ≤ `narrowMax` (520 px) → narrow PanelSettings and portrait layouts |
| Verification viewports | 1440×860 · 1280×800 · 1280×720 · 844×390 · 390×844 · 375×667 · 360×780 |
| **W0 shell** | Title top-left · exit `[×]` top-right · Back bottom-left · Primary bottom-right. Header, body and footer bands are 10/70/10 of a 95vw×90vh frame. One footer action fills the footer width; zero actions means no footer. **Footer labels never wrap, and the footer is always one inline row.** Choice rows may wrap. An ellipsized label keeps its full accessible name |
| **Rule 11** | Any screen with several categories uses a **rail** beside the pane or a `[Category ▾]` selector. **No horizontal tabs. No accordions.** The choice is made per screen by the `CategoryNav` model (width, height, category count, tap floor, with hysteresis), not by one breakpoint. At 844×390 the merchant uses a selector while the Armoury keeps its rail |
| W2 confirmation | 50vw×50vh on wide, 90vw×50vh on compact or portrait. Slots: **question title · target identity · exact consequence**. Focus always starts on **Back**. Escape closes an open selector list first, then the door |
| Buttons | 9 sizes, {third 30%, half 50%, full 100%} × {standard, tall, double}. Standard height is 44 px (touch minimum) |
| Colour roles | Exit and Back turn red on focus · ready Primary is green · neutral controls are brown and gold · disabled or busy overrides every other state |
| Destructive tone | Destructive primaries (Delete, Replace, Abandon, Reset) are **red and never green**, and Back gets the initial focus. `Confirm.tone` is bound from the consequence policy in data |
| End Turn exception | Neutral at rest while ending early is legal; green when highlighted or when Actions are spent |
| Non-colour cues | Every colour state also has a glyph or text: reachable (rim + `›`), travelled (gold + dotted trail), unaffordable (cost struck through + reason), all claimed (`✓ All claimed`) |
| Selection | One gold glow of 0.35 rem. The Inspect `(i)` control appears after `inspectDelayMs` = 1000, as a 44 px target 10 px above the item |
| Touch | The first tap selects; the second tap explains or confirms |
| Focus (every screen) | `screens.json#focusOrder` is required, and the validator fails without it. **Defaults:** rail → pane (first item) → footer Primary → footer Back. After a rail pick, focus lands on the first pane item. Escape or pad B closes the innermost layer (selector list → popover → door). After a refusal, focus returns to the control that started the action |
| Tooltips | Four sizes: WT0 overlay, WT1 compact, WT2 standard, WT3 expanded. The delay is a setting: 0.25, 0.5, 1 or 1.5 s |
| Hold-to-confirm | A short tap opens a review; a hold of `holdMs` (1100) commits. Used for binding choices. With `holdConfirm = off`, the review's `[Confirm]` commits on a tap. The progress ring has a text equivalent. Reduced motion makes the ring static but does not skip the hold |
| Glyphs | Actions `◆` · Smithing Stone `⬡` · Cinders `✦` · Potions `◎`. Status icons are 1.575 rem squares with accessible names |

**Notation.** The frames are 100 columns wide.
- `[Label]` is a button, `(o)` a radio, `[x]` a toggle and `▓▓░░` a meter.
- `{…}` is bound text and `‹ ›` a pager.
- `▾` is a selector, `⟲` means hold-to-confirm, and `(i)` is the inspect control.
- Region names in `UPPER_SNAKE` become UXML `name=` attributes, which codegen turns into `UiNames.<Screen>.<Region>`.
- The literal text shown is placeholder English. At runtime every string is a key.
- Focus order is listed per screen and copied into `screens.json`.

## 1. Component kit

| Component | UXML template | Bindable properties |
|---|---|---|
| `Shell` (W0) | `Kit/Shell` | title key, exit visible, back action, primary action, single-action mode |
| `Workspace` (W1) | `Kit/Workspace` | categories[] (rail or selector), active pane slot, footer actions |
| `Confirm` (W2) | `Kit/Confirm` | title key, body, destructive flag, back and confirm actions, hold |
| `Meter` | `Kit/Meter` | value, max, referenceMax (track length), kind (hp, mp, sp, actions, poise, ward, buildUp), glyph |
| `CardView` | `Kit/Card` | cardId, costs (A/S/M or X), name, type, rarity, art, templated text, keywords, affordable + refusal key, lentBy, upgraded. Ratio 5:8 |
| `Combatant` | `Kit/Combatant` | actor, figure frame, intent, HP, block, the lower stack (HP · resources · build-up · stance · status tiles, at most 5 rows, last tile `+N`), selected (shows name and secondary meters) |
| `IntentChip` | `Kit/Intent` | kind (attack, block, buff, debuff, charging, staggered), value, hits, glyph |
| `StatusTile` | `Kit/Status` | statusId, stacks, build-up (current and threshold), turns |
| `Tooltip` | `Kit/Tooltip` | size (WT0–3), title key, templated body, glyph |
| `Tray` | `Kit/Tray` | items[], fold state, snap heights (30–90vh, default 45) |
| `RelicPip` / `FlaskChip` | `Kit/Relic`, `Kit/Flask` | id, icon (painting or typographic fallback), counter or charges |
| `Receipt` | `Kit/Receipt` | before and after rows (stats, equipment, deck) |
| `HoldButton` | `Kit/Hold` | label key, holdMs, progress |
| `SlotRow` | `Kit/Slot` | save-slot summary, delete `✕` |
| `Figure` | `Kit/Figure` | actor, suite (by grip), frame, tint, outfit, facing, state frame, fallback chain |
| `RunHud` | `Kit/RunHud` | context (`combat` hides Armoury · `map-compact` · `atlas` adds Save & quit), identity, cinders, act and floor trail, HP/MP/SP meters (track = max/referenceMax), relic rail, `[Armoury]`, `[Menu]`. **It never shows potions** |
| `CategoryNav` | `Kit/CategoryNav` | categories[] with counts or values, active id, mode (rail or selector) from the CategoryNav model |
| `IconTray` | `Kit/IconTray` | Shared parent for relics, statuses and potion minis (owner, 2026-09-14): items[], overflow `+N`, tooltip delay |
| `InspectButton` | `Kit/Inspect` | target id; appears after `inspectDelayMs`; 44 px target |
| `FooterGroup` | `Kit/FooterGroup` | combat group (Actions · Draw · End Turn · Discard/Exhaust · Potions): packed, centred, with shrink order |
| `PotionsControl` | `Kit/Potions` | charges, carried items, minis on hover, armed targeting |
| `HandFan` | `Kit/HandFan` | cards[], fan angle, lift, minimum exposed px, overflow scroll, chooser mode (discard / draft) |
| `TargetLayer` | `Kit/Targets` | legal target ids, armed source, dashed outline |
| `GuardBadge` | `Kit/Guard` | block value; anchor player upper-right 12%, enemy lower-left 88% |
| `ArmamentsPopover` | `Kit/Armaments` | right and left sets [1..3], active set, swap cost, refusal |
| `NodeTray` | `Kit/NodeTray` | node kind, hint, boss preview, Back and Enter; an overlay that takes no layout height |
| `ChoiceRow` / `Pager` | `Kit/ChoiceRow`, `Kit/Pager` | options, selected, wrap allowed |
| `Toast` / `Refusal` | `Kit/Toast`, `Kit/Refusal` | text key, duration; refusal is anchored to its control |
| `CardPick` | `Kit/CardPick` | 3 offers, select then Confirm, skip allowed (data) |
| `UprightGate` | `Kit/Gate` | message key, rotate glyph |

---

## W-01 `screen:profileRecovery` — W2 confirmation (AF-01, US-1.1)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│                               BG: bg.tower_city_background_unlit                                 │
│                   ┌───────────────────────────────────────────────────────────┐                  │
│                   │ {recovery.title}                                      [×] │                  │
│                   ├───────────────────────────────────────────────────────────┤                  │
│                   │ {recovery.body}: what happened, what is kept              │                  │
│                   │ MIRROR_INFO  {recovery.mirrorFrom(date)}                  │                  │
│                   ├───────────────────────────────────────────────────────────┤                  │
│                   │ [Start fresh]                           [Restore copy ⟲]  │                  │
│                   └───────────────────────────────────────────────────────────┘                  │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- With no valid mirror, the footer shows only `[Start fresh]`, full width.
- "Start fresh" archives the bad file. It never deletes it.
- The same template covers the "older or newer profile" notices.

## W-02 `screen:title` — W3a (no save) / W3b (save present) (AF-01, US-1.2–1.5)

```
W3b (save present)
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ BG: bg.title_city_tower (lit when profile.wins > 0, else unlit; rule in ui/title.json)           │
│                                   WORDMARK "ASHEN SPIRE" (typographic)                           │
│                                   SUBTITLE {title.subtitle}                                      │
│                                                                                                  │
│                  MENU                              SAVE_PREVIEW                                  │
│                  [Continue]  ← gold, focused       [portrait] {name} · {class}                   │
│                  [Load]                            Act {a} · Floor {f} · HP {hp}                 │
│                  [New]                             Slot {n} · Seed {seed}                        │
│                  [Journal]                                                                       │
│                  [Custom Run]                                                                    │
│                  [Settings]                                                                      │
│                  [Quit]                                                                          │
│ FOOTER {build}                                                AI_DISCLOSURE {title.aiDisclosure} │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Startup gate:** shown before the menu, with ash particles (procedural), the wordmark, and `{title.pressAny}`. Any input dismisses it.
- **W3a:** a lone centred menu with Continue shown **disabled** (AW:245) and no preview placeholder.
- Menu rows come from `ui/menus.json#title`: id, label key, visibility rule and target. The co-op row exists with `visible:false`.
- The wordmark stays centred on screen in both variants.
- **Narrow:** the preview stacks below the menu, and rows are 48 px.
- Focus: menu top to bottom, then the disclosure link.

## W-03 `screen:slots` — W1l New / W1m Load (US-1.4, W2b/W2c/W2d)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ {slots.title.new | slots.title.load}                                                         [×] │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ SLOT_LIST                                                                                        │
│  ┌──────────────────────────────────────────────────────────────────────────────────────────┐    │
│  │ (o) Slot 1  [portrait] Reaver · Act 2 · Floor 7 · HP 31/40 · Saved 12:29 AM          [✕] │    │
│  ├──────────────────────────────────────────────────────────────────────────────────────────┤    │
│  │ ( ) Slot 2  [portrait] Starseer · World Journey 9/20 · Saved yesterday               [✕] │    │
│  ├──────────────────────────────────────────────────────────────────────────────────────────┤    │
│  │ ( ) Slot 3  {slots.empty}                                                                │    │
│  └──────────────────────────────────────────────────────────────────────────────────────────┘    │
│ NOTICE  {slots.newerBuildKept}  (for saves made by a newer build)                                │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ [Back]                                                   [Create character | Load]               │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- `✕` opens **W2b Delete** (focus on Back).
- Picking a slot opens an owner-requested pre-review door (2026-09-04), a W2 frame with no danger tone: "Start in slot n?" (new) or "Load slot n?" (load).
- **W2d** (load over an unfinished run in another context) names the saved climb, its seed, and exactly what is lost.
- **Empty state:** "No saved climbs yet" plus a full-width `[Create character]`.
- Rows show class, act/floor/HP, seed and "Saved {time}". Journey and playtime move into the row tooltip.
- **W2c Replace** is **not** shown here. It appears at the write boundary, when Begin is pressed in creation.

## W-04 `screen:creation` — W1c workspace with rail (AF-02, US-2.1–2.8)

Every step shares this shell. The rail items show their current values.

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ {creation.title}                                              [mini portrait]  [▦|☰]        [×] │
├──────────────────┬───────────────────────────────────────────────────────────────────────────────┤
│ RAIL             │ PANE: CLASS                                                                   │
│ ▶ Class          │  CLASS_GRID (▦) / CLASS_LIST (☰)  — nothing preselected                       │
│   — none —       │  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐│
│   Character      │  │ Reaver   │ │ Starseer │ │ Rogue    │ │ Herald   │ │🔒 Grave   │ │🔒 Ash     ││
│   {name/mode}    │  └──────────┘ └──────────┘ └──────────┘ └──────────┘ │ Warden   │ │ Child    ││
│   Equipment      │  UNFOLDED (selected class):                          └──────────┘ └──────────┘│
│   {armour/hands} │  ┌ FIGURE (STANCE-READY, default tint) ┐  {class.fantasy} (2-line clamp)     │
│   Review         │  │                                      │  VERBS  {class.verbs}               │
│   {seed/slot}    │  │                                      │  STATS  STR 3 DEX 1 CON 2 WIS 1 INT 1│
│                  │  └──────────────────────────────────────┘  DERIVED HP · MP · SP · Actions     │
│                  │                                            FLASKS crimson 3 / azure 1        │
├──────────────────┴───────────────────────────────────────────────────────────────────────────────┤
│ [Back]                                              [Next]  (always pressable; refuses w/ reason)│
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Fit ladder** (owner, 2026-09-19): every class stays visible without scrolling. Only the pane scrolls.
- A locked class shows its unlock condition string from `unlocks.json`.
- **Compact or narrow:** the rail becomes a `[Class ▾]` selector above the pane.

**Pane: CHARACTER**

```
│ NAME [ ____________ ] [Random]      STAT_MODE (o) Standard ( ) Assign     POINTS_LEFT {n}        │
│ ATTRIBUTES (2 columns; 1 column below 64 rem)       LIVE_PREVIEW (side set in layout.json)       │
│   STR  [-] 3 [+]    DEX [-] 1 [+]                     FIGURE tint {t} · sprite {s} · SIGIL {g}   │
│   CON  [-] 2 [+]    WIS [-] 1 [+]                     DERIVED HP 38 · MP 2 · SP 3 · Actions 3    │
│   INT  [-] 1 [+]    (+/- hidden in Standard)          RATINGS AR 1 · DR 0 · PR 1 · Poise 4 · Ward 2│
│ KEEPSAKE (o) None ( ) Old Cinder ( ) Traveler's Flask ( ) Whetstone Memory   (i) effect preview  │
│ SPRITE ‹1/3›   SIGIL ‹2/6›   TINT ● ember ○ frost ○ gold ○ grace ○ rot  (procedural swatches)     │
```

- Every number comes from `Preview.Derived`. The worked values above are illustrative only.
- In Assign mode, pressing Next with points left refuses ("{n} points left") and moves the cursor to the allocator (shipped behaviour, AW:2545).
- **Narrow:** `[Face]` moves onto its own header row, and a single active-pane scroll is allowed at extreme text scale.

**Pane: EQUIPMENT**

```
│ SOCKETS                        │ CHOICES for focused socket          │ DECK_PREVIEW (11)           │
│  Armour*   {body_reaver_vigil} │  [icon][icon][icon][icon]           │ 4 attack · 4 guard ·        │
│  Right     {weapon_greatsword} │  DETAIL name · weight · grip ·      │ 1 technique · 1 signature · │
│  Left      — empty —           │   rating · lent cards (mini)        │ 1 class ability             │
│  Equip     {icon_whetstone}    │  LEGALITY {equip.twoHandNeedsEmpty} │ WEIGHT Medium 62% · GRIP 2h │
│  Relic · Kit                   │                                     │                             │
```

- Choosing an item already held in the other hand **moves** it and shows a receipt. It is never duplicated.

**Pane: REVIEW**

```
│ RECEIPTS (each row reopens its step)       │ JOURNEY (o) Classic Climb                           │
│  Class · Character · Equipment · Deck [View]│         ( ) World Journey · Wanderer 20  [P1 flag] │
│                                            │         ( ) World Journey · Long Expedition 26      │
│                                            │ SEED [ random ] [Reroll]    SLOT {chosen slot}       │
```

- On Review, the Primary reads `[Begin the Climb]`.
- Begin on an occupied slot opens **W2c Replace ⟲**.
- A validation failure moves focus to the failing field (AF-03).

## W-05 `screen:prologue` (AF-03, US-3.x)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ART  prologue.{scene}.desktop (narrow: .mobile) + class item layer + motif wash (procedural)      │
│  ~76% height (narrow ~70%)                                                                       │
│                                                                                                  │
│                                                                                                  │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ CAPTION (~16%) variant per scene: below art · over art · letterbox · side panel                  │
│   {prologue.<scene>.line}   (live narration reveal)                                              │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ CONTROLS (~8%)  [Pause]                          ● ● ○ ○ ○            [Hold to skip ⟲] [Continue]│
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- There is no HUD.
- On the final scene, Continue reads `{prologue.setForth}`.
- **The reference preset** uses the `caption` layout, a typewriter reveal, `advanceOnClick`, a 30vh caption, and **hides Pause and Skip** (`showPause` and `showSkip` false). The pad and keyboard Back action still offers hold-to-skip (D-020).
- **Narrow:** still one inline row, `[Pause] [Skip ⟲] [Continue]`, in 3 columns. The band floor is max(8%, 44 px + 8 px). Subtitles are 18 px and touch targets 48 px.
- Pause opens a small menu: Resume · Quit to title (progress is saved at the scene boundary).
- Every staging field comes from `prologue.json`: art fit, focus, camera, wash, reveal, transition, music and hold time.

## W-06 `screen:actMap` — W4b: header max(10%, 52 px) · map fills to footer · tray is an overlay (AF-04, US-4.x)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│RUN_HUD {portrait} {name} Reaver L3 · ✦{cinders} · Act 2 · Hollow Weald · Fl 5 │ HP ▓▓▓▓▓░ MP ▓░ │
│        relic rail ○○○○○○ (25 px pips)                                  SP ▓▓ │ [Armoury] [Menu] │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ MAP_VIEWPORT (parchment_act{n} + {region}-map art · pan · zoom · fog/path mode)      ☠ BOSS_ICON │
│                 ◇     ◇           ◇                                                              │
│              ◇     ?     $     ◇     ◇           nodes: opaque dark discs + glyph                │
│                 ⚔     ⌂     ⚔     ?              reachable: parchment rim light                  │
│              ⚔     ◉     ⚔     ⚔                  travelled path: earned gold + dotted trail     │
│   ┌ NODE_TRAY overlay (slides over the map foot after 150 ms; takes no layout height) ─────────┐ │
│   │ {node.type} · {hint} · {boss preview if path end}                                          │ │
│   │                              [Back]        [Enter {kind}]   (centred)                      │ │
│   └────────────────────────────────────────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ FOOTER (one row)  [−] [+] [⊙]              {map.hint}                                (POTIONS ◎) │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Select, then Enter.** A repeat pick travels only after `repeatPickDelayMs` = 400.
- `POTIONS` is a corner button 1.6× the touch row. On hover (after the tooltip delay) mini flask chips rise out of it.
- Node glyphs and labels come from `ui/mapGlyphs.json`.
- **Narrow:** the HUD uses three lines and the map scrolls vertically.
- Focus: reachable nodes → tray → zoom → potions → HUD.

## W-07 `screen:combat` — W4a bands 10/55/30/5 (AF-05, PF-03/04/05, US-5.x/6.x/7.1)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│RUN_HUD identity · ✦cinders (centred) · Act/Floor trail · HP ▓▓▓▓░ MP ▓░ SP ▓▓ · relics ○○○ [Menu]│
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ BATTLEFIELD  sky 20% / ground 80% · {region}-combat bg · rows A–C × cols 1–2 ally | 3–4 enemy    │
│              (filled left→right, no recentring; mirrored diagonal tracks)                        │
│                                                     (i)          (i)          (i)                │
│                                                  [⚔ 12×2]      [🛡 8]        [⏳ 2]                │
│   ┌──────────┐🛡5  ← guard badge upper-right 12%  ┌───────┐    ┌───────┐    ┌───────┐              │
│   │ PLAYER B2│  faces target · suite by grip   🛡│ ENEMY │    │ ENEMY │    │ ENEMY │ state frames │
│   │ FIGURE   │  [STAGGERED −1◆] when broken      │  A3   │    │  B4   │    │  C3   │              │
│   └──────────┘                                  └───────┘    └───────┘    └───────┘              │
│   HP ▓▓▓▓░ 31/40                                HP ▓▓▓░ 30/42  HP ▓▓░ 18/30  HP ▓▓▓▓ 60/60       │
│   build-up ◔◑ (half-height)                     ◔ bleed ring   ◑ frost ring                      │
│   [◉][▲][+1]  status icons 1.575rem             [◉][▼][+2]     [❄]         (details in tooltip)  │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ HAND (≥208 px) cards 5:8, fan 2.5°, selected lifts 1 rem, ≥44 px exposed per card, overflow scrolls│
│   ┌─────┐┌─────┐┌─────┐┌─────┐┌─────┐┌─────┐┌─────┐┌─────┐┌─────┐┌─────┐                        │
│   │A1 S1││A1   ││A1 S2││A1S1M1│ …    (unaffordable dimmed; reason in tooltip)                    │
│   └─────┘└─────┘└─────┘└─────┘                                                                   │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ FOOTER (≥56 px) one packed centred group:      (◆2/3) [Draw 12] [ END TURN ] [Disc 4|Exh 1] (◎) │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Footer group:**
  - Actions `(◆)` and Potions `(◎)` are circles at 95% of footer height, each capped at 20% width.
  - End Turn shares that height, up to 40% width.
  - The pile buttons take up to 10% each, with a **64 px readable floor** (owner, 2026-09-13).
  - End Turn gives up width first. It turns **green** when it is legal and no Actions are left.
  - There is no space-between: the group is packed and centred.
  - Empty piles and resources fade.
- **Potions** is the only flask control (WGC11). It opens charges and carried items, and the minis hang above it. Arming a flask that needs a target shows the target layer, the same as an armed card.
- **Lower stack** (at most 5 rows): HP · other resources · build-up (half-height meters or rings) · stance · status **icons only** (last tile `+N`). The selected combatant also shows name, Poise and Ward.
- **Enemy-turn state:** the footer group goes busy and End Turn becomes `[Skip ▶▶]`, which fast-forwards the timeline.
- **Discard chooser**, only when `handRules.overflow = discard` (shipped default: keep):
  ```
  │ DISCARD_CHOOSER  "Discard {n} to keep {cap}"   selected {k}/{n}                               │
  │ HAND (same fan; tap toggles; ghost mark on chosen)                                             │
  │ FOOTER  [Back]                                              [Discard {n}] (refuses if k < n)   │
  ```
- **Summon overflow:** a faction holds at most 6 slots. When they are full, a summon is refused and the refusal is logged in the timeline (data key `rules/combat.json#summonWhenFull`).
- **Armaments popover:**
  ```
  ┌ ARMAMENTS ─────────────────────────┐
  │ Right  [1 greatsword ●] [2 —] [3 —]│
  │ Left   [1 —]            [2 —] [3 —]│
  │ Swap cost ◆2 · {refusal if short}  │
  │ [Close]                    [Swap ⟲]│
  └────────────────────────────────────┘
  ```
- **Targeting:** an armed card shows dashed outlines on legal targets. Previews swap in ghost values ("30 → 18") taken from `Preview.Damage`.
- **Weapon sets:** selecting the player figure opens the **Armaments popover**. It lists the right and left sets `[1][2][3]` with the swap cost from data. A swap it cannot afford is refused with a reason and costs nothing.
- **Inspect** `(i)` on a combatant opens the W1w entity inspector (§W-17).
- **Stances** are entered through cards (DSL). The active stance shows its aura VFX and a stance row in the lower stack.
- **Layers, top first:** effects z7 · HUD and footer z6 · hand z5 · targets z4 · actors z3 · floor z2 · skyline z1.
- **Compact (340–464 px), "rails":** (Actions)[Draw] sit on a left rail and [End Turn] over [Discard](Potions) on a right rail. The hand sits between them, and notch insets are applied.
- **Narrow portrait (375×667)** keeps the diagonal formation, with an 8 px minimum row stagger:
  ```
  ┌─────────────────────────────┐
  │HUD 2 rows       [Menu]  52px│
  │ ally C1/B2 ╲     ╱ enemy C3 │
  │  (diag, 8px stagger)  B3 B4 │
  │ BATTLEFIELD (rest)          │
  │ HAND ≥208 · 5 cards exposed │
  │ (◆)[D][END TURN][E](◎) ≥56px│
  └─────────────────────────────┘
  ```
- If a combatant can't fit readably, the screen reports `unsupported` and shows the upright gate.
- Focus: hand → targets → footer group → HUD. Pad shoulder buttons scroll the hand.

## W-08 `screen:rewards` — W1t (US-11.x)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ {rewards.title}   {claimed} of {total} claimed        (no close control — shipped W1t)           │
├──────────────────────────────────────────────────────────────┬───────────────────────────────────┤
│ REWARD_LIST                                                  │ CLAIM_STATUS                      │
│  [✦ 62 Cinders                                       ]       │ Taken                             │
│  [⬡ Smithing Stone                                   ]       │ Available                         │
│  [🂠 Choose a card (3)   → CARD_PICK: 3 CardViews,     ]       │ Required choice                   │
│  [   select → green outline → Confirm | Skip          ]       │                                   │
│  [⚱ Crimson flask                                    ]       │ Full (no free slot)               │
│  [⚔ Armament: {name} (new) / duplicate → +40 cinders ]       │ Available                         │
│  [◈ Relic: {name}                                    ]       │ Skipped                           │
├──────────────────────────────────────────────────────────────┴───────────────────────────────────┤
│ [Continue — full width] (gold until all resolved, then green + "✓ All claimed")                  │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- Back exists only inside the card-pick sub-state.
- The **draft variant** (class or skill draft, and the sealed/draft deck modes) reuses this frame. The list becomes a `CardPick` round counter ("Round {r} of {n}"), with no Skip unless the data allows it.
- Continue is always pressable. The `rewardCollect` setting decides whether it sweeps or leaves the rest.
- Pending rewards are saved.
- **Narrow:** the status column stacks after the whole list (AW:278).

## W-09 `screen:merchant` — W1d/W1v workspace with rail (US-9.x)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│RUN_HUD (cinders shown here only)                                                                 │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ {merchant.title}                                                                             [×] │
├───────────────┬──────────────────────────────────────────────────────┬───────────────────────────┤
│ RAIL          │ PANE  {n} for sale                                   │ DETAIL (≤ half body)      │
│ ▶ Cards       │  ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐                  │  CardView / item preview  │
│   Armaments   │  │card│ │card│ │card│ │card│ │card│  tile: price ✦,  │  price ✦ 75               │
│   Weapon arts │  └────┘ └────┘ └────┘ └────┘ └────┘  sold / reason   │  {refusal: need 30 more}  │
│   Relics      │                                                      │                           │
│   Flasks      │  SERVICES pane: Card removal ✦225 (+75 each)         │                           │
│   Services    │                 Smith (if rolled)                    │                           │
│   Sell*       │                                                      │                           │
├───────────────┴──────────────────────────────────────────────────────┴───────────────────────────┤
│ [Leave]                                                          [Buy · 75 cinders]              │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- `Sell*` appears only when `shopSell` is on, and then the Primary reads `[Sell · N cinders]`. The Sell pane lists a **Buy back** section below the sellable items.
- Buying goes through **W2a**: "Buy {item}?" · target · "Spend 242 of your 999 cinders (757 left)".
- **Empty states:** a sold-out shelf shows `{merchant.soldOut}`, and an empty Services pane shows `{merchant.noServices}`.
- **Portrait:** a `[Cards ▾]` selector, then the offers, then the detail capped at half the body.
- The rail is used at 60 rem or wider (21.6/95 share, clamped 11–28 rem). Otherwise it becomes `[Cards ▾]`.
- Merchant art: the figure shown is chosen per data from existing art.

## W-10 `screen:rest` — W1s (+ W1i Smith, W1j Extract, W1k Install, level-up) (AF-06, US-8.x)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│RUN_HUD                                                                                           │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ {place.name} · {available} of {total} available      tags: heal 35% · mana to 50% · flasks refill│
│ OPTION_CARDS                                              │ AVAILABILITY                         │
│  [ Rest ⟲ ]   +14 HP · MP → 50%  (Receipt)                │ Rest ........ available              │
│  [ Smith ]    ⬡2                                          │ Smith ....... ⬡2                     │
│  [ Extract a card ]  [ Install a card ]                   │ Extract ..... 1 mount                │
│  [ Flask split ] crimson 3 · azure 1 ‹ ›                  │ Flask split . available              │
│  [ Level up ]  2 points banked                            │ Level up .... 2 points               │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ [Continue]   (multi-use places, or when a relic forbids Rest; otherwise no footer)               │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Shipped rule kept:** at a single-use place, committing Rest, Smith, Extract or Install ends the visit. The validator guarantees every place yields at least one exit.
- **Extract** moves the card **into the deck**, as shipped (AW:2104). Whether it should go to storage instead is logged as an owner question.
- **Potions in rooms:** there is no control. The "flasks outside combat" setting uses the RUN_HUD `[Menu]` → Potions entry. This is logged as provisional D-013.

- **W1i Smith** is a workspace with no categories:
  - ITEM_COLUMN (44vw, clamped 14–60 rem; `[Selection ▾]` on compact);
  - a pane with the detail and before/after for the lent cards;
  - a cost row pinned at the bottom;
  - footer `[Back] [Upgrade ◆×n]`.
  - On touch, it takes two taps.
- **W1j/W1k Extract/Install** use the same frame: mounts on one side, storage on the other, and a receipt.
- **Level up:** attribute rows with `[+]` and a live Receipt of derived stats and ratings; footer `[Back] [Confirm]`.

## W-11 `screen:event` — W1u (AF-07, US-10.x) and `screen:dialogue` — W4c

```
W1u
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ {event.name}   {event.status: Choose a response | n of m available | Resolved}                   │
├───────────────────────────────────────────────┬──────────────────────────────────────────────────┤
│ NARRATIVE  art (reused asset) + templated text│ RESPONSES (buttons: tap = review, ⟲ = binding)   │
│                                               │  [{choice 1} — {effect preview}]                 │
│                                               │  [{choice 2} — {effect preview} ⟲]               │
│                                               │  [{choice 3} — locked: {requirement}]            │
│                                               │  RESOLVED state: result sentence replaces list   │
├───────────────────────────────────────────────┴──────────────────────────────────────────────────┤
│ [Continue | Steel yourself — full width] (disabled until Resolved)                               │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘

W4c dialogue (legacy dungeons, quest board)       bands 10/40/35/15 (12/40/33/15 with compact HUD)
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│RUN_HUD                                                                                           │
│ PLAYER figure (left, CONVERSATION→PORTRAIT fallback)            NPC figure (right; listener dim)│
│ CONTEXT_BAND (opaque): {title} · {line} · up to 4 responses, no scrolling                        │
│ [Back]                         [Skip speech]                                  [Continue]         │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- W1u has no close control and no Back. Every event has an always-legal leave response (validator rule).
- W1u keeps the RUN_HUD band above it, like W-10.
- **W4c data** (AW:2383):
  - floor 60%;
  - bands fold below 500 px height;
  - up to 4 responses without scrolling, 5 or more scroll;
  - responses appear on the last beat only;
  - binding responses keep hold-to-confirm;
  - narrow: responses become a single column and a `[Voice Ⅱ]` control is added.

## W-12 `screen:armoury` — W1e/W1n workspace with rail (US-7.x)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ {armoury.title}   WEIGHT Medium 62% ▓▓▓▓▓▓░░░ (capacity 2·CON+STR)                           [×] │
├──────────────┬───────────────────────────────────────┬───────────────────────────────────────────┤
│ RAIL         │ PANE (0.5)                            │ INSPECT (0.5) "Compared with equipped"    │
│ ▶ Character  │  FIGURE paper-doll (pose by grip;     │  {item} tier +1 · weight 3 · grip 2h      │
│   Equipment  │   equipment/components parts)         │  lent cards [mini][mini][mini]            │
│   Inventory  │  Right [1][2][3]  Left [1][2][3]      │  Receipt AR 1→2 · deck +1 attack          │
│   Cards      │  Body · Head · Hands · Feet           │  eligibility {equip.eligible | reason}    │
│              │  Talismans [ ][ ][ ] · Core [class]   │                                           │
│              │  STORAGE [ ][ ][ ][ ][ ][ ][ ][ ]     │                                           │
├──────────────┴───────────────────────────────────────┴───────────────────────────────────────────┤
│ [Back]                                                                  [Equip ⟲]                │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- The panes become rows below 760 px.
- Folding trays default to 45vh and snap between 30 and 90vh.
- The Armoury is **not** reachable in combat. Only the Armaments popover is.

## W-13 `screen:legacyDungeon` (W4b variant, US-4.5)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│RUN_HUD                                                                                           │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ SCENE  legacy/{BS|HM|FC}-ENV-0n background + floor · figure · occupant art                       │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ SCENE_TRAY  {scene name} · exits · [Advance] [Rest] [Open] [Flee (DEX roll {n}+)]  ☠ {boss door} │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ FOOTER  scene graph mini (MAP-0n art) · (POTIONS ◎)                                              │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## W-14 `screen:worldMap` and `screen:localMap` — W4b (P1, AF-09, US-14.x)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│RUN_HUD (journey 9/20 places) · [Armoury] [Menu] [Save & quit]                                    │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ SCENE  world map art (fivefold-frontier | fractured-realm | shattered-gulf) · fog  [−][Fit][+]   │
│      ●──●──◉──○        ▣ city   ⛫ dungeon   open edges lit                                        │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ CONTEXT (≤20%)  {place} · {type} · {region} · OPEN_ROADS list          [Details]                 │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ [Recenter]                                      [Enter | Travel]                     (POTIONS ◎) │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- Potions keep the usual corner position in map mode (owner, AW:127).
- **W1b Town** (P1) is the city service view. It is a W1 workspace whose rail lists the services (Inn · Chapel · Smith · Merchant · Quest board). Each service opens its own screen (W-10, W-09, W-11 dialogue).

- Enter travels, or opens the location if you are already there. Inspecting never moves you.
- The **Location dialog** (local map) is a W1 frame: local-map camera with markers, a detail pane, and `[Return to world]` / `[Travel to …]`.
- **Narrow:** the header uses two rows.

## W-15 `screen:runEnd` — victory / defeat (AF-11, US-4.7, 4.9)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ BG victory: river_citadel_lit · defeat: region combat bg (desaturate shader; no new art)         │
│                          TITLE_L {runEnd.victory "EMBER RESTORED" | runEnd.defeat "YOU PERISHED"}│
│ DETAIL_CARD who & where    FIGURE (victory: STANCE-READY | defeat: defeated pose)                │
│ STAT_STRIP seed · class · floor · killer · duration · cinders                                    │
│ KIT_LINE final deck (scroll)                    UNLOCKS [new: …]                                 │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ [Run history]                                                        [Return to title]           │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## W-16 `screen:journal` — W1f/W1g workspace with rail (US-13.x)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ {journal.title}                                                                              [×] │
├───────────────┬──────────────────────────────────────────────────┬───────────────────────────────┤
│ RAIL          │ PANE                                             │ DETAIL                        │
│ ▶ History     │  win-rate STAT_STRIP per class                   │  deck · killer · seed [copy]  │
│   Profile     │  rows: outcome glyph · class · where · pills     │                               │
│   Cards (P1)  │  Profile: runs · highest act · bosses · wins     │                               │
│   Relics (P1) │  Collection: grid; unseen = silhouette; NEW badge│                               │
│   Enemies(P1) │                                                  │                               │
│   Armaments   │                                                  │                               │
├───────────────┴──────────────────────────────────────────────────┴───────────────────────────────┤
│ [Back]                                                                                           │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- The rail items show counts.
- The single footer action spans the full width.

## W-17 Inspectors — W1w entity, W1h piles, W1o item, W1q potion

```
W1w ENTITY INSPECTOR                                          W1h PILE VIEWER
┌───────────────────────────────────────────────────────┐    ┌───────────────────────────────────┐
│ {name}                                            [×] │    │ {piles.title}                 [×] │
├──────────────────┬────────────────────────────────────┤    ├──────────┬────────────────────────┤
│ PREVIEW (38%)    │ DETAILS (scrolls)                  │    │ RAIL     │                         │
│ sprite · name ·  │ Summary: HP, intent, defence       │    │ ▶ Discard│ card grid │ reading     │
│ HP/MP/Poise      │ Current state · Previous actions   │    │   Exhaust│ empty: {piles.empty}    │
│                  │ Known abilities · traits · Lore    │    ├──────────┴────────────────────────┤
└──────────────────┴────────────────────────────────────┘    │ [Close]                           │
                                                             └───────────────────────────────────┘
```

- "None." and "Unknown." are distinct strings.
- W1w has a single `[Back]` footer action spanning the full width. **Narrow:** the 38% preview stacks above the details.
- The W1h rail is Discard and Exhaust, as shipped. The **draw pile** has its own viewer opened from `[Draw]`, which shows the count only unless a card effect reveals the order (AW:821).
- W1o item inspection is read-only, with no footer.
- W1q potion inspection has a single `[Use]` action, disabled with a reason when refused.

## W-18 `screen:settings` — W1a workspace with rail (US-15.x)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ {settings.title}                                                                             [×] │
├───────────────┬──────────────────────────────────────────────────────────────────────────────────┤
│ RAIL          │ CONTROLS (generated from settings schema: toggle | slider | enum | segmented)    │
│ ▶ Display     │  fullscreen [x] · textScale ═══○══ · reducedMotion [ ] · uprightGate [x]         │
│   Audio       │  master/music/sfx sliders · synth bed preview ▶                                  │
│   Accessibility│ colour-blind glyphs [x] · tooltip delay ‹1 s› · holdConfirm ‹short|off› · flick │
│   Gameplay    │  mapMode · shrineMultiUse · shopSell · rewardCollect · swapCostRule ·            │
│               │  levelUpValue · flasks outside combat · creationAutoAdvance                      │
│   Hand & draw │  drawMode · opening · capacity + HAND_EXAMPLE (computed for current class)       │
│   Opening     │  prologue playback · scene subset/order                                          │
│   Advanced    │  searchable key/value table over the Advanced schema (run-scoped keys marked)    │
│    Wireframes │  modal width · footer width · category nav (fit|rail|selector) · workspace frame │
│               │  · scene backdrop · ground line — every default "As designed" (owner 2026-09-20) │
│   Saves       │  export/import saves · import/export config (web format) · preset ‹reference|shipped› │
│   About       │  version · CREDITS · provenance · AI disclosure · asset/font/audio gap notes     │
├───────────────┴──────────────────────────────────────────────────────────────────────────────────┤
│ [Back]                                                                   [Reset category ⟲]      │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- There is no heading inside the pane, and help text appears only where the effect isn't obvious.
- When opened in-run, run-scoped keys show `{settings.appliesNextRun}`.

## W-19 `screen:customRun` (P1, US-15.3)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ {custom.title}                                                                               [×] │
├───────────────┬──────────────────────────────────────────────────┬───────────────────────────────┤
│ RAIL          │ PANE                                             │ SUMMARY (detail card)         │
│ ▶ Ascension   │  ASCENSION ═══○═══ {n} → {ascension.n.enables}   │  what this run changes        │
│   Difficulty  │  toggles: toughElites · lessHealing · deadly…    │                               │
│   Chaos       │  toggles: allElite · hoarder · chaosRewards …    │                               │
│   Run shape   │  deck mode ‹standard|sealed|draft› · first seat ·│                               │
│               │  map shape sliders · seed [    ]                 │                               │
├───────────────┴──────────────────────────────────────────────────┴───────────────────────────────┤
│ [Back]                                                            [Continue to creation]         │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## W-20 `screen:pause` — W1 frame without categories (AF-12)

```
┌──────────────────────────────────────────────────┐
│ {pause.title}   Seed {seed} [copy]           [×] │
├──────────────────────────────────────────────────┤
│  [Deck]                                          │
│  [Armoury]           (out of combat only)        │
│  [Settings]                                      │
│  [Save & quit ⟲]     Saved · Slot {n} {time}     │
│  [Abandon run ⟲]     destructive tone            │
├──────────────────────────────────────────────────┤
│ [Resume — full width]                            │
└──────────────────────────────────────────────────┘
```

- Save failure opens **W1r** with `[Retry save]`. It never replays gameplay.
- Quitting opens **W2e**.

## W-21 Overlays

- **Toast:** top-centre, queued, with a duration from data.
- **Refusal:** an inline shake plus the reason string at the control. It is never a blocking modal.
- **Card pick:** 3 large `CardView`s. Select, then Confirm, with `[Skip]`.
- **Upright gate:** a full-screen message and a rotate glyph, shown below `gateBelowH` or when geometry reports `unsupported`.

## W-22 Progression — W1 frames without categories (US-12.x, E12 P0)

```
LEVEL UP (from rest)                                   SKILL-TRACK DRAFT (on track level-up)
┌──────────────────────────────────────────────┐      ┌──────────────────────────────────────────────┐
│ {levelup.title}  {n} points banked       [×] │      │ {draft.title}: {track} level {l}             │
├──────────────────────────┬───────────────────┤      ├──────────────────────────────────────────────┤
│ STR [+] 3  DEX [+] 1     │ RECEIPT           │      │  ┌─────┐  ┌─────┐  ┌─────┐                   │
│ CON [+] 2  WIS [+] 1     │ HP 38→42 · SP 3→4 │      │  │card │  │card │  │card │  select → glow    │
│ INT [+] 1  (cap per data)│ Poise 4→5 …       │      │  └─────┘  └─────┘  └─────┘                   │
├──────────────────────────┴───────────────────┤      ├──────────────────────────────────────────────┤
│ [Back]                              [Confirm]│      │ [Skip] (only if data allows)       [Confirm] │
└──────────────────────────────────────────────┘      └──────────────────────────────────────────────┘

CLASS TREE (from Armoury → Character, or on class level-up)
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ {classTree.title} · class level {cl}                                                         [×] │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ Tier 1 (lvl 1)   [node]  [node]                                                                  │
│ Tier 2 (lvl 3)   [node]  [node]                                                                  │
│ Tier 3 (lvl 5)   [node] ⟷ [node]   mutually exclusive · picks modify the class card (preview)    │
├──────────────────────────────────────────────────────────────────────────────────────────────────┤
│ [Back]                                                                          [Choose ⟲]       │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- The XP strip in the RUN_HUD (WGH5) is **an open owner decision** (AW:1802). Build it behind the flag `ui/runHud.json#showXp`, default off.

## W-23 Confirmations and save status — W2a–e, W1r

| Frame | Title | Target | Consequence | Footer (focus starts on Back) |
|---|---|---|---|---|
| W2a service | "Buy {item}?" / "Remove {card}?" | item or card | "Spend 242 of your 999 cinders (757 left)" | `[Back] [Buy]` |
| W2b delete | "Delete this save?" | slot summary | "This climb cannot be recovered." | `[Back] [Delete ⟲]` (red) |
| W2c replace | "Replace this save?" | slot summary | "The climb in slot n is lost." | `[Back] [Replace ⟲]` (red) |
| W2d load over | "Load slot n?" | saved climb + seed | exact loss of the unfinished run | `[Back] [Load]` |
| W2 AI notice (first launch, once) | "Made with generative AI" | — | the full `about.aiDisclosure` text (code, art, music, sound, text, design) | `[Continue]`, full width |
| W2e quit | "Quit Ashen Spire?" | — | "Progress is saved at the last checkpoint." | `[Back] [Quit]` |
| W1r save failed | "Could not save" | slot | the error string, "no progress was replayed" | `[Back] [Retry save]` |

## W-24 System states

- **Boot / loading:** the wordmark, a determinate progress bar from the content manifest count, and `{boot.loading}`. No input is accepted.
- **Content error (`screen:contentError`):**
  - in dev builds, a scrollable ValidationReport (file · row · field · rule) with `[Copy]`;
  - in player builds, a safe notice and `[Quit]`.
- **Empty states:** no saves (W-03), empty pile (W-17), no history (W-16: "No climbs recorded yet"), sold-out shelf (W-09), locked class (W-04, with the condition).
- **Refusal:** inline at the control, then focus returns to the control that started the action (US-5.9).

---

## Wireframe → build order

The implementation session builds screens in this order. Each step is a grey box, then view-model binding, then skin, then screenshot review.

1. Kit: Shell, Workspace, CategoryNav, Confirm, Meter, CardView, HandFan, FooterGroup, Tooltip, HoldButton, Refusal, W-24 boot and error states
2. W-02 title → W-03 slots → W-04 creation (Class pane only) → W-07 combat → W-08 rewards → W-23 confirmations. This is the **first playable loop**, the same loop the shipped wireframe-first plan named.
3. W-06 act map → W-10 rest → W-09 merchant → W-11 event and dialogue → W-15 run end → W-20 pause
4. W-04 all panes → W-05 prologue → W-12 armoury → W-22 progression → W-17 inspectors → W-13 legacy dungeon
5. W-16 journal → W-18 settings → W-01 recovery
6. P1: W-14 world and local map, W1b Town → W-19 custom run
