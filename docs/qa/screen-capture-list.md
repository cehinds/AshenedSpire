# Screen capture list (VP-16, D-1208)

What the owner's editor capture should produce, so each Unity screen can be set beside its web shot in `docs/qa/web-screens/`. Two sizes only: **1920x1080** and **390x844** (the two sizes of the web shots; `manifest.json` lists them). Name each file like the web shot with the size suffix, e.g. `rest-1920x1080.png`, and put them in `docs/screens/` (same folder `Cli.CaptureScreens` writes to, D-060). The PNGs are not committed unless the owner says so.

## Why this is a list and not fixture files

`Unity/Assets/Tests/Fixtures/screens/*.json` is the fixture format (`ScreenCaptureTests`, one file per screen state, sizes from `ui/layout.json#verification`: 1280x720, 1920x1080, 844x390, 390x844). Its fixtures that drove the 405 screens (`combat`, `rewards`, `rest`, `merchant`, ...) through `RunSession` and `ClimbPilot` were deleted with the 405 run (WP-21, D-1300); the remaining ones cover the title, slots, creation, confirm doors, boot, content error, profile recovery and the kit gallery. The Core 2 screens (`core2Combat`, `core2Rewards`, `core2Rest`, `core2Shop`, `core2Event`, `core2RunEnd`, the Core 2 `actMap`) are reached through `Core2RunSession`, which `ScreenCaptureTests.Show` does not drive, so a fixture file for them would be a file nothing reads. Until that driver exists (a PlayMode edit; the folder is the owner's), take these by hand in the editor (Game view set to each size), or extend `Show` with a `core2` case that builds a `Core2RunSession` and edits the run through `EditRunForReview`. A fixture file added to `screens/` that `Show` cannot handle fails the whole capture run (the default branch calls `nav.Go` with the fixture's screen id), so do not add one before the driver.

The verification sizes 1280x720 and 844x390 are not in the web shots; skip them for this comparison.

## How to reach each state (Core 2 run, seed `ASHEN1`, class Reaver, profile `core`)

| Web shots | Unity screen | State |
|---|---|---|
| `startup`, `flow-01-startup-gate` | `title` (gate) | Fresh boot, before any key |
| `title`, `flow-02-title-no-save` | `title` | No save in any slot |
| `title-settings`, `settings-general`, `settings-accessibility`, `settings-advanced` | `settings` | From the title's Settings; the three tabs; Advanced with Developer tools on and the group select open |
| `flow-03-slots` | `slots` | New Game, slot 1 empty |
| `flow-04-slot-confirm` | `confirm` | A slot holding a climb: New Game on it (the replace door) |
| `flow-05-creation-class`, `class-select` | `creation` | Class pane, List; also Grid |
| `flow-06-creation-review` | `creation` | Review pane, seed `ASHEN1` |
| `flow-07-prologue`, `prologue-1`, `prologue-2` | `prologue` | After Begin: scene 1, scene 2 and the full bar |
| `flow-08-first-map`, `map-start` | `actMap` | Floor 0, nothing walked |
| `flow-09-map-node-selected`, `map-trail-selected` | `actMap` | A reachable node selected (the tray open) |
| `map-trail`, `map-midclimb` | `actMap` | Two floors walked; half the act walked |
| `map-legend` | `actMap` | Legend open (the `?` tool) |
| `map-potions` | `actMap` | Potions door open |
| `map-pause-menu` | `pause` | Menu from the map |
| `map-deck`, `map-equipment`, `map-stats` | `armoury` | Deck, Equipment and Stats tabs from the map |
| `flow-10-first-combat`, `combat` | `core2Combat` | First fight, turn 1, nothing played |
| `combat-card-selected`, `combat-target-hover`, `combat-player-hover`, `combat-card-inspect` | `core2Combat` | A card picked; the pointer on an enemy; on the player; the card's inspect door |
| `combat-three-enemies`, `combat-four-enemies` | `core2Combat` | Encounters with three and four enemies |
| `combat-enemy-turn`, `combat-enemy-turn-2`, `combat-enemy-turn-3`, `combat-next-player-turn` | `core2Combat` | End Turn, each enemy step, then the next player turn |
| `combat-hit-feedback`, `combat-fx-frozen`, `combat-statuses-block`, `combat-statuses-target-hud`, `combat-intent-tooltip` | `core2Combat` | A hit landing; a frozen enemy; block and statuses on the HUD; the target HUD; an intent tooltip |
| `combat-end-turn-hold` | `core2Combat` | End Turn held halfway (the hold stripe fill) |
| `combat-log-open`, `combat-quick-menu`, `combat-potions`, `combat-draw-pile`, `combat-discard-pile` | `core2Combat` | Log, quick menu, flasks, draw pile and discard pile open |
| `combat-reaction-offer`, `combat-reaction-armed`, `combat-reaction-hand`, `combat-reaction-after`, `combat-defeat-reaction-offer` | `core2Combat` | A reaction offered, armed, played from the hand, after it resolved; the offer on the killing blow |
| `combat-defeat`, `boss-intro`, `combat-victory-card-pick` | `core2Combat` | Defeat veil; the boss splash; the victory beat before the spoils |
| `reward-full`, `reward-empty`, `reward-card-pick`, `reward-boss-relics`, `reward-skill-draft`, `reward-level-up` | `core2Rewards` | The spoils door after a fight; nothing to claim; a card offer; the boss's relics; a class draft; a level notice |
| `rest`, `rest-wounded` | `core2Rest` | A shrine at full HP; at half HP |
| `smith-modal`, `smith-modal-selected`, `blacksmith` | `core2Rest` | The Smith door, then an item chosen |
| `shop`, `shop-relics`, `shop-flasks`, `shop-inspect`, `shop-buy-press`, `master-merchant` | `core2Shop` | Each shelf; Inspect on an offer; the Buy question; the merchant portrait on the wide screen |
| `event-pilgrim-choices`, `event-choices`, `event-result` | `core2Event` | The Wayward Pilgrim: the choices (Give 50 cinders dimmed), the result |
| `event-dialogue`, `event-dialogue-2` | `core2Event` | The Grave of the Nameless: the first line, then the replies |
| `run-end-defeat`, `run-end-victory` | `core2RunEnd` | Die on floor 1; win the act |
| `run-history` | `journal` | Journal from the title, one finished climb |
| `compendium`, `profile`, `about` | `compendium`, `profile`, `about` | From the journal's links; About is the Settings group too |
| `atlas-world` | not built | The web's world atlas; no Unity screen yet (D-1053 keeps it open) |

## What each capture is looking at (the VP-16 items)

- **Uppercase labels, chips, frames, dividers:** the eyebrow lines (`REST`, `RECOVERY PREVIEW`, `CINDERS`, `CHARACTER`, the title's `ACT 1 . FLOOR 0`) are upper case; chips (`NEW`, `TAKEN`, the item count on a Smith row, the lock note) are small tracked caps with a hairline; modals and the settings frame wear the corner brackets.
- **Modal chrome:** the head and foot bands are a shade darker than the body, the soft shadow shows around the frame, not inside it.
- **Hold stripe** (`combat-end-turn-hold`, `shop-buy-press`, a Smith Upgrade): the fill is a light and dark diagonal stripe behind the label, growing from the left across the whole control, gold edge while held.
- **Focus ring:** after a key or a pad move, a 2 px gold ring sits 2 px outside the control; after a mouse press there is none.
- **Tooltip arrow** (`combat-intent-tooltip`, the settings tab tooltip in `settings-advanced`): a small gold triangle on the edge facing the control, under its centre.
- **Phone (390x844):** the map nodes are the web's size (D-1202: no change needed); the creation header holds the List / Grid switch, the class portrait, the menu button and the close, with the green `1 of 2 · Class` step beside the gold `All steps` select under it; the settings choices and the group picker are selects (a dark face, the chosen words, a chevron) and their list opens under them.
