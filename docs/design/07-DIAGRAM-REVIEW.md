# 07 — Diagram and wireframe review (sub-agents)

Four independent sub-agents reviewed revision 1 of the diagrams and wireframes. None of them edited any files.

- **AF:** activity-flow reviewer, covering completeness, softlocks and screen-id consistency.
- **PF:** Unity architecture reviewer, covering feasibility, determinism and save integrity.
- **RP:** rules-parity reviewer, who checked every combat and run step against `D:\repos\AshenSpire\src` with file and line evidence.
- **WF:** UX reviewer, who checked against the shipped wireframe atlas, `CURRENT-SPECIFICATION.md` and `approved-wireframes.md` (AW).

**Render check.** All 24 Mermaid diagrams in revision 2 were rendered with Mermaid 11 in a browser, and all passed. In revision 1, PF-06 failed because a `;` inside a sequence message ends the statement. That is fixed.

**Outcome.** Every blocker and major finding was applied to revision 2 of [04](04-WIREFRAMES.md), [05](05-ACTIVITY-FLOWS.md), [06](06-PROCESS-FLOWS.md) and [08](08-DATA-CONVENTIONS.md), and the corrections carried into [01 §C3](01-PROMPT-REVIEW.md), [03](03-FEATURES-AND-USER-STORIES.md) and [09](09-UNITY-MEGA-PROMPT.md). Owner-level questions are listed at the end. They were not decided; each has a provisional default in 09 §9.

---

## Rules parity (RP): the shipped behaviour that changed the design

| ID | Finding (evidence) | Resolution |
|---|---|---|
| RP-1 | HP, Mana and **Stamina carry over** between fights; there is no full Stamina at combat start (`main.js:2141,2282`) | AF-05, US-5.8, 01 §C3, 09 App. A |
| RP-2 | Turn start: clear Block → Actions − stagger → **draw** → Madness and DoT hooks (`combat.js:320-338`) | AF-05 |
| RP-3 | +1 Stamina at **player turn end**, only if none was spent (`combat.js:355-367`) | AF-05 |
| RP-4 | Defaults `retain on`, `overflow keep`: **no forced discard**, and only Ethereal cards leave (`engine/handRules.js:15`) | AF-05, US-5.2; the discard chooser appears only when `overflow = discard` |
| RP-5 | A fully blocked hit fills no meter; meter, break, `hpLost` and arcane build-up happen only when HP is lost (`actions.js:152-171`) | PF-04 |
| RP-6 | Triggers run on a **FIFO queue**, not inline (`triggers.js:6`); combat end is checked after every action (`combat.js:278`) | PF-04, 08 §7 |
| RP-7 | Resist is granted only when the target's tags match the row (`engine/statuses.js:141-170`) | PF-04, US-6.1 |
| RP-8 | `whileCharging` runs **once** on the commit turn; the move resolves at `resolveOnTurn` (`combat.js:421-455`) | PF-05, US-6.5 |
| RP-9 | HP phases fire on HP change, not at intent selection (`triggers.js:238`) | PF-05, US-6.5 |
| RP-10 | DoTs tick per enemy at that enemy's turn start, even when it is broken (`combat.js:401-416`) | PF-05 |
| RP-11 | Streams are the 14 in `engine/rng.js`; there are no `combat` or `rewards` streams | PF-07, AF-08, 08, 09 |
| RP-12 | Maps are built **per act at act start**, with rule-filtered rolls, 40 re-rolls, force-placement and a rest before elites (`mapgen.js`) | PF-07 |
| RP-13 | Unknown nodes are **resolved at map build** on `events`; a shrine result opens the field camp (`actmap.js:72`, `main.js:1929`) | AF-04, US-4.3 |
| RP-14 | Legacy dungeons are swapped in **on entering the boss node** (`main.js:2080-2097`) | AF-04, PF-07, US-4.5 |
| RP-15 | The Valkyrie is a no-seat boss row in the **final-tier boss pool**, not an extra step (`actmap.js:82-91`) | AF-04, PF-07, US-4.8 |
| RP-16 | Merchant stock is rolled on `shop` at each entry (smith offer on `smith`), reused on resume, cleared on leave (`main.js:1953-1966`) | AF-08, US-9.1 |
| RP-17 | At a single-use place, Rest, Smith, Extract or Install ends the visit. Continue appears only for multi-use or when a relic forbids Rest (`rest.js`) | AF-06, W-10, D-009 |
| RP-18 | The final boss victory skips the rewards screen; a dungeon boss counts once the player leaves the dungeon (`main.js:2353-2358`) | AF-04 |
| RP-19 | There is no "tower lit" state in the shipped code | Kept as a **new seam** for the 4th tower (02 §3) and marked as such |

## Activity flows (AF)

| ID | Sev | Finding | Resolution |
|---|---|---|---|
| AF-R1 | blocker | No pause, Save & quit or Abandon anywhere | New **AF-12 Pause**, linked from every run diagram |
| AF-R2 | major | A single-use rest could trap the player | AF-06 now shows the shipped rule; the validator guarantees an exit |
| AF-R3 | major | An event with no legal choice loops | Validator rule: an always-legal leave response; review → commit → Resolved |
| AF-R4 | major | Death and victory were checked too late (Madness, DoTs, loseHp, enemy-turn deaths) | Checks after turn start, after every queued action and after the enemy turn |
| AF-R5 | minor | No cancel for targeting or the popover; affordability checked after targeting | Cancel edges added; affordability now comes before targeting |
| AF-R6 | major | Merchant used a non-existent `rewards` stream | Now `shop`, and `smith` for the smith offer |
| AF-R7 | major | Card removal ignored the deck floor and affordability | `RMC` decision added |
| AF-R8 | major | Resume missed locations and checked in the wrong order | Hash first, then version, then migrate, then log-or-snapshot; branches for event, dungeon, local map and pending pick |
| AF-R9 | major | Victory bypassed record and clear; the two writes weren't atomic | AF-11 is now "Run end" (defeat, victory, abandon), with one atomic commit |
| AF-R10 | major | `PREV` had no exit; there was no way back to Standard | Explicit Back edges, rail jumps and a switch-mode edge; validation runs before W2c |
| AF-R11 | major | Boss and treasure skipped rewards; dungeon branches incomplete | Rewards edges and flee-fail and fight-win edges added |
| AF-R12 | minor | `screen:pressAnyInput` didn't exist; Continue branches were contradictory | Startup gate lives inside `screen:title`; Continue is enabled only with a valid slot; pre-review doors added |
| AF-R13 | minor | No way out of the prologue | Pause, then Quit to title |
| AF-R14 | minor | `screen:smith` didn't exist | It is the W1i sub-workspace of `rest` |
| AF-R15 | minor | World Journey fights skipped rewards; no dungeon defeat | Edges added |
| AF-R16/17/18 | minor | Missing screen ids, numbers in labels, the fragile `<` | Labels now use data keys and "act below 3" wording |

## Process flows (PF)

| ID | Sev | Finding | Resolution |
|---|---|---|---|
| PF-R1 | blocker | PF-06 didn't render (`;`) | Fixed |
| PF-R2 | blocker | The log was truncated before the snapshot was durable, so commands could be lost or doubled | **Gen-stamped** snapshot and log; old logs deleted only after replace and mirror succeed |
| PF-R3/R4 | major | Atomic write under-specified; torn log records | `Flush(true)`, read-back hash, `File.Replace` with a backup (`File.Move` on first save, retried); CRC32 records truncate at the first bad one |
| PF-R5/R6 | major | Hash checked after migration; silent divergence when content changes | Hash raw bytes first; `stateHashAfter` stops replay at the first mismatch; a changed content hash resumes from the snapshot (D-017) |
| PF-R7 | major | Synchronous reads fail on Android and WebGL | `ReadTextAsync` with File or UnityWebRequest sources; files listed from the manifest only |
| PF-R8/R9 | major | Validation ran before alias resolution; the manifest hash was trusted blindly | Aliases resolved first; an **effective** content hash computed at load |
| PF-R10 | major | Merge-patch wipes whole arrays; override security was unspecified | **Id-keyed catalogs**; overrides are gated, path-matched, size- and depth-capped, and off for golden replays |
| PF-R11 | minor | Config layer naming was ambiguous; the profile was never loaded | `rules + balance` base; profile load added to PF-02 |
| PF-R12 | major | `double` parsing and culture leaks | `FloatParseHandling.Decimal`, exact-hundredth validation, `InvariantCulture`, `eol=lf` |
| PF-R13/R14 | major | Trigger order, unstable sorts, multi-hit death | Ordering rules in 08 §7; loop over hit, then target; stable sorts; ordinal comparisons |
| PF-R15 | minor | Resistance formula ambiguous; possible overflow | Quoted formula; `long` intermediates; truncation matched to the oracle |
| PF-R16 | major | Preview could advance the RNG | Preview runs on a cloned state and cloned cursors; random parts shown as ranges |
| PF-R17 | minor | No death checks in the enemy turn; stale intent numbers | Checks added; the intent stores a move id and numbers are computed at render |
| PF-R18/R19 | major | Wrong stream names; unbounded repair; a content name in a system flow | Rewritten PF-07 using the RP findings |
| PF-R20/R21/R22 | major | Pipeline assumed audio and fonts; imported before the postprocessor existed; atlas and Addressables details missing | PF-01 rewritten: postprocessor first, batch import, Sprite Atlas V2, Analyze rule, gaps logged |
| PF-R23/R24 | major | Codegen domain reload; blank batchmode screenshots | `Ashen.Codegen` isolated; capture through RenderTextures |
| PF-R25 | major | Unity 6 drops the undeclared `data-string` UXML attribute | `[UxmlElement] LocLabel` with `stringKey` (08 §9) |
| PF-R26 | minor | IL2CPP strips Newtonsoft DTOs | `link.xml` (PF-11) |
| — | new | Missing processes | **PF-10** content export and parity oracle · **PF-11** Windows build · **PF-12** procedural audio |

## Wireframes (WF)

| ID | Sev | Finding | Resolution |
|---|---|---|---|
| WF-R1 | blocker | A single 1280×720 PanelSettings shrinks physical minimums on phones | **Two PanelSettings** (wide 1280×720, narrow 430×780), with `minPhysical` enforced after scaling (D-016) |
| WF-R2 | blocker | Combat was missing states: discard chooser, enemy turn, flask targeting, stagger | Added to W-07 |
| WF-R3 | blocker | No progression screens | **W-22** level-up, draft and class tree; the XP strip sits behind a flag (D-015) |
| WF-R4 | major | Slot names were wrong | Enemy columns 3–4 (A3, B4, C3); allies 1–2 |
| WF-R5 | major | The map tray was drawn as a band | Overlay tray; one-row footer; header max(10%, 52 px) |
| WF-R6 | major | Rewards and Event had invented close and Back controls | Removed; responses are tap-to-review, hold-to-commit, then a Resolved state |
| WF-R7 | major | Next was "locked" | Always pressable; it refuses with a reason and moves focus (AW:2545) |
| WF-R8 | major | Pre-review doors and W2d were missing | Added to W-03 |
| WF-R9 | major | The narrow prologue footer stacked | One inline row of 3 columns with a floor |
| WF-R10 | major | The owner's Settings → Advanced → Wireframes group was missing | Added to W-18 |
| WF-R11/R12 | major | Destructive tone, the End Turn exception and W2 slots were missing | Added to §0; W-23 covers W2a–e and W1r |
| WF-R13 | major | Pause was parented to W2 | W1 frame without categories |
| WF-R14 | major | Status text tiles; guard badge in the wrong place | Icon tiles, build-up rings, badge anchors |
| WF-R15 | major | Potions were missing on the world map and undefined in rooms | World-map corner added; rooms follow D-013 |
| WF-R16/R17 | major | Missing screens, states and kit components | W1b Town, buy-back, draft variant, W-24 system states and empty states, summon overflow, Armaments popover; the kit gained 15 components |
| WF-R18 | major | No portrait frames | Portrait frame for combat; notes on creation, merchant, W4c and W1w |
| WF-R19/R20 | minor | Gate thresholds; the Rule 11 switch | Put in data; the `CategoryNav` model decides per screen |
| WF-R21/R22 | major | Hold-to-confirm needed a non-hold path; focus order was missing | `holdConfirm = off`; a required focus order per screen, with defaults and Escape rules |
| WF-R23/R24 | minor | Colour-only cues; wrapping versus text scale | Non-colour cues row; the no-wrap rule scoped to footers |
| WF-R25–R29 | minor | Id and wording drift; glyph clash (◆); W1h rail; missing HUDs; W-19 without a rail | Aligned: Stone is `⬡`; W1h is Discard/Exhaust with a separate draw viewer; RUN_HUD on W-09 and W-11; W-19 has a rail |

---

## Open owner questions (not decided; each has a provisional default)

- Should single-use places get a `[Leave]` action?
  - Why: shipped behaviour ends the visit on any service. D-009 keeps it that way.
- Should Extract move the card into the deck (as shipped) or into storage?
  - Why: the old prompt said storage. D-014 keeps the shipped behaviour.
- Should rooms have a Potions control?
  - Why: the "flasks outside combat" setting exists, but rooms have no home for the control. D-013 routes it through Menu.
- Should the RUN_HUD show an XP strip (WGH5)?
  - Why: this is still pending in AW:1802. D-015 keeps it behind a flag, off.
- The Dodge modifier ⌊(DEX−10)/2⌋ is always negative on the lean scale. Is that intended?
  - Why: it may be a leftover from the 1–20 scale. D-002 keeps the shipped value, tunable in data.
- Should the 4th-tower "tower lit" seam use the per-seat flag as specified?
  - Why: it is a new state that doesn't exist in the shipped code (RP-19).
