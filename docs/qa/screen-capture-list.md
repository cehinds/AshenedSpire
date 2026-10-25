# Screen capture list (VP-16, D-1208)

What the owner's editor capture should produce, so each Unity screen can be set beside its web shot in `docs/qa/web-screens/`. Two sizes only: **1920x1080** and **390x844** (the two sizes of the web shots; `manifest.json` lists them). Name each file like the web shot with the size suffix, e.g. `rest-1920x1080.png`, and put them in `docs/screens/` (same folder `Cli.CaptureScreens` writes to, D-060). The PNGs are not committed unless the owner says so.

## How the fixtures drive a Core 2 run

`Unity/Assets/Tests/Fixtures/screens/c2_*.json` are the Core 2 fixtures (D-1381). A fixture with a `"core2"` object is handled by `Core2Capture` (`Unity/Assets/Tests/PlayMode/Core2Capture.cs`, called from `ScreenCaptureTests.Show` and `AfterShow`): it builds a Core 2 run in slot 1 (`seed`, `class`; default 20261025, Reaver), walks a bot to the state named by `until` (`map`, `combat`, `rewards`, `rest`, `shop`, `event`, `runEnd`) clearing the way to the nearest node of the kind (`prefer`, `walk` more fights first), tries the next seeds until the run stands there (a merchant is rare; `eventId` finds a named event), edits it for review (`hpRatio`, `cinders`), and shows it with `RunFlow.Show`, as the game does. The map fixtures can also select a node, open the legend, bring the Potions minis out (`potionsTray`) or open a door over the map (`modal`: `potions`, `pause`, `armoury` with `tab`). Every fixture skips the two sizes the web shots do not have (`skipSizes`). The output is `docs/screens/<fixture>_<w>x<h>.png`.

| Fixture | Web shot it sets beside |
|---|---|
| `c2_map_start` | `map-start`, `flow-08-first-map` |
| `c2_map_selected` | `flow-09-map-node-selected`, `map-trail-selected` |
| `c2_map_trail`, `c2_map_midclimb` | `map-trail`, `map-midclimb` |
| `c2_map_legend` | `map-legend` |
| `c2_map_potions`, `c2_map_potions_tray` | `map-potions` (and the hover minis, which the web only shows under a pointer) |
| `c2_map_pause` | `map-pause-menu` |
| `c2_map_deck`, `c2_map_equipment`, `c2_map_stats` | `map-deck`, `map-equipment`, `map-stats` |
| `c2_combat`, `c2_combat_enemy_turn` | `flow-10-first-combat`, `combat`, `combat-next-player-turn` |
| `c2_reward_full` | `reward-full` |
| `c2_rest`, `c2_rest_wounded` | `rest`, `rest-wounded` |
| `c2_shop` | `shop` |
| `c2_event_pilgrim`, `c2_event_grave` | `event-pilgrim-choices`, `event-dialogue` |
| `c2_runend_defeat` | `run-end-defeat` |
| `c2_settings` | `settings-general` |

States the fixtures cannot put on screen (no review hook on the screen, or a pointer or a key held) are still taken by hand from the table below: the combat screen's picked card, hover, inspect, piles, log, quick menu, reactions and hold states; the Smith door and its selected item; the shop's shelves, Inspect and Buy question; the event result and the Grave's replies; run-end victory; the Settings tabs other than General; the creation, slots and title states (the 405 fixtures `creation`, `slots_*`, `title_*` already cover the screens' first states).

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
