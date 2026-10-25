# Build log

Newest entries go at the top. Each entry records the story, what landed, the tests, what wasn't verified, and the next step. After a context summary, re-read this file and `DECISIONS.md` before continuing.

## WP-21: the 405 run retired, Core 2 the only ruleset (worktree branch, 2026-10-24)

**Landed (D-1300..D-1303)**
- **One ruleset:** New Game and Continue go through `Core2RunSession` only. A 405 slot (save format 2) reports `OtherRuleset` and stays on disk untouched.
- **Deleted, 405 run:** `RunSession` and partials, `Application/Nodes` (but `NodeRules`), the 405 combat, rewards, rest, merchant, event / dialogue, legacy dungeon and run-end screens with UXML, `Nodes.uss`, screen registry entries and `ScreenIds`, the runFlow `screens` map; Domain `RunLoop`, the 405 rest, reward door, legacy dungeons, new run, act advance, shop, shelf stock, armament trading, card extraction and buy-back.
- **Kept because Core 2 calls them:** `RunEnd`, `RewardRolls`, `ItemSmithing`, `SmithServices`, `CardRemoval`, `ClassTree`, `CombatEnd`, the `Refusal` types, the reward-face helpers of `RewardsView`.
- **Tests and oracles:** the 405 session / node / climb tests, the run, loop, shop, rewards and claims parity tests, the PlayMode climb, first-fight and node smokes, and the screen fixtures that needed a 405 run are gone; so are the `loop`, `shop`, `claims` and `registries/reference` oracles (about 33 MB) and their generators. `OracleCompare` holds the shared compare helpers. The audio, settings-layer and web-config tests were rewritten without a 405 run.
- **Content:** the reference preset is deleted (`shipped` is the default); `webKeyMap.json` imports player settings only and reports every `gameConfig.*` key; `mapMode` fog, `walkedFade` half (the web's four options), `shrineMultiUse` on, `useRestorativeFlasksOutsideCombat` on, `showPlayedCard` off (web `b8cfe4fac`).

**Tests run:** the full engine-free suite, one class at a time: 13,038 passed, 0 failed before the content step; after it, the full suite again: 12,811 passed, 0 failed (the 227 fewer are the reference preset's registry and event cases). Node: `codegen --check`, `refresh-manifest --check`, `ci/check-content`, `ci/check-docs`, `web-key-map --check`, `ui-tokens --check` pass. Presentation and the PlayMode tests compile against the `UnityEngine.Modules` 2021.3.33 reference assemblies with Input System / Unity 6 stubs (D-314 harness, rebuilt in the session scratchpad; the one known error is `MapBoard`'s `mgc.painter2D`, as before).

**Not run:** `transform-content.mjs` and `card-face.mjs --check` (no raw export and no web checkout at `b8cfe4fac` on this machine; the generated preset and strings were re-rendered with the transform's canonical writer). Unity itself (no editor here): UXML/USS load after the deletions, the PlayMode smokes left (`BootTitleSmokeTests`, `Core2FightSmokeTests`, `ScreenCaptureTests`).

**Owner: check in the editor**
- Title → Continue on a slot saved by the 405 build: refused as unreadable, the slot still listed and still on disk.
- New Game → a Core 2 climb; the map opens in fog; Settings shows Fade the walked path as Off / Subtle / Half / Strong at Half, Show played card off.
- Settings → Import a web config: the player settings land, the tuning keys are listed as not placed.

**Next:** map what the Core 2 fight still reads of the 405 content (D-1303), then retire the 405 content tables, combat paths and their oracles; a Core 2 driver for `ScreenCaptureTests.Show` (VP-16).

## VP-16: chrome and phone residuals (worktree branch, 2026-10-25)

**Landed (D-1200..D-1208)**
- **Chrome:** the hold fill is the web's diagonal light / dark stripe behind the label, growing across the whole control (`Frames/hold-stripe.png`, a repeating texture from `Tools/ui-frames.mjs`); the focus ring is a 2 px gold overlay 2 px outside the focused control for keys and pad (`Kit/FocusRing.cs`, made by `UiHost`); the tooltip has its arrow (`Frames/tooltip-arrow.png`, `Tooltip.PointAt`); the modal head and foot bands are the web's 94% bg (`color.modalBar` on the combat modal, the shell and the settings frame).
- **Phone:** enum settings and the group picker are selects (`Kit/SelectField.cs`, the list opens in place); the creation header carries the class portrait, a menu button and, on a phone, the List | Grid switch; the shell's selector mode shows the green `1 of 2 · Class` step beside the gold `All steps` select (`CategoryNav.StepKey`). The map node size needs no rule (D-1202: measured, the wide tokens already give the phone shot).
- **Uppercase / chips / frames:** audited every tracked-caps rule against its elements (D-1207); the pile caption and the 405 screens' eyebrows were the only ones missing `kit-upper`. Chips, frames and dividers were already on the screens the web shows them on.
- **Capture list:** `docs/qa/screen-capture-list.md` (D-1208): the Core 2 screens cannot be driven by `ScreenCaptureTests.Show`, so the list says how to reach each web shot at 1920x1080 and 390x844.
- `class-view--on` had no USS rule (the chosen List / Grid view never showed); it is gold now. `size.holdBarH` is gone with the bar.

**Tests run (small filters, one at a time):** `Enforcement|UiData|UiRules|Core2Screens|ContentValid|FrontScreens|Settings` 101/101; node checks ui-tokens, codegen, refresh-manifest, check-content, check-docs, ui-stage, card-face, web-key-map ok; the Presentation compile check builds.

**Not verified (Unity was not run):** `background-repeat: repeat` with `background-size` on the stripe texture (and that `hold-stripe.png` imports as a repeating Texture2D, not a sprite); `FocusRing` (the overlay's rect follows `worldBound` under the narrow panel's scaling, and `FocusInEvent` at the root with TrickleDown sees every focus change); the tooltip arrow's `resolvedStyle.left` read before the first layout; `SelectField` inside the settings' scroll view and the Navigator's `nav--open` Back handling; the creation header's insert before the shell's close, and the stepped CategoryNav row; every size against the shots. The capture list's states for the Core 2 screens are a hand route, not a fixture.

**Owner: check in the editor (compare `docs/qa/web-screens/`)**
- Hold a hold button (End Turn with hold on, `combat-end-turn-hold`; a Buy; Upgrade): the stripe grows from the left behind the label, the edge is gold while held; release empties it.
- Press Tab / arrow keys or the pad on any screen: a gold ring 2 px outside the control; click with the mouse: no ring until a key is used.
- Hover or focus a control with a tooltip: a gold triangle on the tooltip's top edge under the control's centre, clamped 5 px from the corners.
- Settings (`settings-advanced`, `settings-general-390x844`): Show opening, the Display and Interface pickers and the group picker are dark selects with a chevron; pressing one opens its list under it, picking closes it and keeps the focus on the select; Back (Escape / pad B) closes an open list first.
- Creation at 390x844 (`flow-05`, `flow-06`): the header shows the title, List | Grid, the portrait, the menu and the close; under it a green `1 of 2 · Class` and a gold `All steps ▾` (its list jumps to Review). At 1920x1080 the header has the portrait, menu and close after the title.
- Modal heads and feet are a shade darker than the body (combat modals, Settings, the shell screens).

**Not done:** the leather texture under modal fills; the tooltip's flip above the trigger and the side edges; the smith's item select and the profile's rail select as selects; the web's inline hold dial; a Core 2 driver for `ScreenCaptureTests.Show` and fixture files that use it; the editor pass.

## VP-14: the phone pass for the Core 2 screens (worktree branch, 2026-10-25)

**Landed (D-1050..D-1053)**
- **A bug under every earlier phone rule:** 29 narrow rules used token names that were never generated (`--size-board-hud-h-narrow` for `--size-board-hud-hnarrow`), so USS dropped them: the phone board, spoils faces, shop inspector and combat modals had only their wide sizes. Fixed in seven sheets; `ui-tokens --check` now fails on any undefined `var()`.
- **Modals** 96% wide on a phone, the run end's door 88%, Smith 94% x 90%; **rest**, **shop** and **event** place their door and painting from the 390 x 844 shots (all values `ui/tokens.json size.*Narrow*`, citations in `webSource.phone`); the **map** strip is two rows with no key bar, bigger tools, Potions, legend and tray; the **Armoury** stacks its rail and panes; **creation**'s class card and review rows; the **opening**'s bar order (`PrologueScreen` moves Continue behind the progress); **settings** keep a switch at the right of its words (`FrontClasses.SettingsRowToggle`).
- No behaviour or binding changed.

**Tests run (small filters):** `Enforcement|UiData|UiRules|Core2Screens` 54/54; node checks ui-tokens, codegen, refresh-manifest (regenerated), transform-content, check-content, check-docs, ui-stage, card-face, web-key-map all ok; the Presentation compile check builds.

**Not verified (Unity was not run):** every rule above in the editor at 390 x 844 (and 430 x 932, 520 wide); `PlaceInFront` on the prologue bar; `align-content: center` and `flex-wrap` on the map strip; the Armoury's stacked panes inside the modal's scroll body (the deck editor's two lists in a column need a bounded height); percent heights (86%, 90%) on modals inside the scrim.

**Owner: check in the editor at 390 x 844 (compare `docs/qa/web-screens/*-390x844.jpg`)**
- Combat: the HUD, footer, hand, piles and End Turn now take the narrow sizes (first time); compare `combat-390x844`, `combat-hit-feedback`, `combat-log-open`, `combat-potions`, `combat-quick-menu`.
- Map: two-row strip, no key bar, tools / Potions / legend / tray (`flow-09`, `map-legend`, `map-potions`); the Armoury tabs (`map-equipment`, `map-deck`, `map-stats`).
- Rest, shop, event and run end doors (`rest`, `shop`, `event-pilgrim-choices`, `run-end-defeat`); the smith modal; the spoils door.
- Creation class cards and review, the opening bar, Settings switches (`flow-05..07`, `settings-general`).

**Next:** map node size on a phone, the creation header cluster and step button, native selects in settings, screenshot fixtures (`Tests/Fixtures/screens`), then the editor pass.

## VP-9 (events) and VP-12 (run end): the Core 2 event screen and the web's game over (worktree branch, 2026-10-24)

**Landed (D-960..D-966)**
- **Event screen** (`core2Event`, `Core2EventScreen`, `Core2Event.uxml/.uss`): a one-off event is the web's choice body under the painting band (EVENT, the gold name, the status, the narrative with its art well beside the response cards, `Cannot afford` and the HOLD mark, a full-width Continue with `Choose a response first.` under it, the result in place of the responses, `Steel yourself` before a fight); a quest chain's step is the spoken frame (the player and the speaker over a context band: the beats, the responses on the last one, Back, Skip speech, Continue, `Choose a reply first.`). The responses refuse at the card (price, history, the Mirror's class swap in the web's words); a binding one is held to confirm; Continue is `FinishEvent`. `PendingPort` no longer reports an event: the act map's notice is left only for a rest place of a profile without the rest module.
- **Run end** (`core2RunEnd`, `Core2RunEndScreen`, `Core2RunEnd.uxml/.uss`): the web's game over: THE CLIMB ENDS / Defeat (THE CLIMB / Victory) over the spire vista, `You perished` / `Ember restored`, the identity card with the blow that struck it, the four numbers, the Earned card (`ClassesUnlocked`), the final deck, Run again (a new climb, same class and name, new seed), Run history, Return to title.
- **The run's tally** (`Core2RunTally`, D-962): damage dealt and taken and the last blow, kept by the session (saved with it) because the configured run's `stats` stay as the web oracle holds them.
- **Logic engine-free and tested:** `Core2EventFlow`, `Core2RunEndFlow`, `Core2RunTally`, `Core2EventEndFlowTests` (15). Routing: `core2Screens.event/runEnd`, `ScreenFactory`, `ui/screens.json`, schema; tokens `size.event*`, `size.end*`; strings `core2.event.*`, `core2.end.*`; codegen fragment `ui-eventend.json`.

**Tests run (small filters, one at a time):** `Core2EventEndFlow` 15/15, `Core2Screens` + `Enforcement` + `UiData` 33/33; `Core2RunSession` + `Core2RestShopFlow` + `Core2RewardsFlow` 34/34, `Core2LoopSession` 5/5 (the slow one, run once at the end); node checks `ui-tokens`, `codegen`, `refresh-manifest`, `transform-content`, `ci/check-content`, `ci/check-docs`, `ui-stage`, `card-face` ok; Presentation compiles against the UnityEngine reference assemblies (D-314 harness).

**Not verified (Unity was not run):** every USS rule of `Core2Event.uss` and `Core2RunEnd.uss` and the layout of both screens; the door's two columns (`flex-wrap` with `flex-basis: 0` and a minimum width), the stage's figures (`height: 300%` from the top with `background-position-y: top` and `background-size: contain`, clipped by `overflow: hidden` of the side) and the plate on the reveal line, the context band's scroll over the footer's percentages, `LocButton`s holding children (response cards), a `kit-frame` card with a background colour over its sprite, the run end's wrapped chip strip and deck, `scale-and-crop` for the two paintings, the focus on Run again; the saved tally across a mid-turn resume.

**Owner: check in the editor (Core 2 run at 1920x1080; compare `docs/qa/web-screens/event-pilgrim-choices`, `event-choices`, `event-dialogue`, `event-dialogue-2`, `event-result`, `run-end-defeat`, `run-end-victory`)**
- Enter an Unknown node that resolves to a one-off (Wayward Pilgrim, Golden Moth): the painting band under the HUD, the door below it with EVENT, the gold name, the status at the right, the art well and the prose at the left, the response cards at the right (Give 50 cinders dimmed with `Cannot afford` and refusing when pressed with 20 cinders; Refuse with HOLD when it binds), a dimmed Continue with `Choose a response first.` under it. Take a response: the result sentence replaces the cards, the status reads Resolved, Continue is green; it returns to the map (`Steel yourself` and a fight when the response starts one, e.g. Wyrm Trial).
- Enter the Grave of the Nameless (a quest step): the player left and the speaker right over a dark context band with `Grave of the Nameless` and the first line, Back disabled, Skip speech and Continue live; Continue reads the second line and shows the three responses (Continue closes with `Choose a reply first.`); a response shows its result in the caption and Continue leaves.
- Die: THE CLIMB ENDS / Defeat, `You perished` in the loss colour, `Forsaken`, the name, `Floor 1 / 8 · 0 fights won`, the seed, `Slain by <enemy> · <move> for <n>`, the four numbers (`0 / <max>` final HP), the Earned card with the class the run opened, the final deck, Run again focused (green); Run again starts a new map; Return to title goes to the title. Win: THE CLIMB / Victory, `Ember restored`, no blow line, the run's hit points.

**Next:** the web's inline hold dial on a binding response and the spoken step's entrance (D-966); phone compositions (VP-14); the run history screen; the tally in the record with the oracle's bot tallying (D-962); WP-21 de-scope.
## VP-12 and VP-13: the title, slots, creation, opening, settings and the meta screens (worktree branch, 2026-10-25)

**Landed (D-1000..D-1007)**
- **Title and gate** (web `title.js`, `kit.css:596-614`, `startup-*`, `title-*`): the door painting, the glowing wordmark, plate entries in the web's order (Continue as the journey card with the class and `ACT 1 . FLOOR 0 . 71/71 HP`, New Game in the ready green, Load Game, Journal, Settings with the gear, Exit), dividers, tagline, build stamp; the gate shows the head in parchment over the unlit city with the pulsing prompt. Custom Run is hidden.
- **Slots** (`flow-03`): the narrow modal with number-first rows (`Empty slot` / `Start a new climb here`), Back and the green Create character.
- **Creation** (`flow-05`, `flow-06`): the rail Class | Review, class cards (figure, name, sentence, HP SP MP HAND DRAW, the starting relic, the lock note) as a list or grid, Next, then Review: the seed field, the destination, the CHARACTER / ARMOUR / MAIN HAND / RELIC cards and the starting deck with `xN`. `Core2RunSession.Preview` reads the run a class would make without writing anything; `CoreRunRules.GamesToUnlock` words the lock.
- **Opening** (`flow-07`): `PrologueScenes` / `PrologueFlow` (Core 2 in memory; the 405 run reads its saved row through the same scenes), `PrologueScreen` with the painting, caption (name, speaker, typewriter line) and the bar (n / N, Pause, Settings, Skip opening, Continue / Set forth); the web's lines; the new setting `openingAutoAdvance`; `newRun.opening` in `rules/runFlow.json`.
- **Settings** (`settings-*`): `ui/meta.json settingsTabs` + `SettingsSession.Page` (tabs, groups, rows, search, Changed, reset) and `SettingsScreen` (switch, choices, slider with minus / plus, the header tools, Done and Save). Reached from the title, the pause menu and the opening.
- **Run history, Compendium, Profile, About** (`run-history-*`, `compendium-*`, `profile-*`, the About group): `JournalView` (records line, class records, result pills), `CompendiumView` (28 armaments by kind), `ProfileView`, `AboutView` and their screens; the title's Journal row opens the history, which reaches the others.
- Strings in `strings/app.en.json` (and the web's opening lines in `strings/meta.en.json`), tokens in `ui/tokens.json` (`webSource` for each), codegen fragment `Tools/codegen.d/ui-front.json`, rules in `Screens/Front.uss`.

**Tests run (small filters, one at a time):** `FrontScreens` 19, `UiRules`, `UiData`, `FirstFightUi`, `Enforcement`, `MetaView`, `Settings` (121 together, after updating the title order, the creation rail and the pause settings row); node: `ui-tokens`, `codegen`, `refresh-manifest`, `transform-content` `--check`, `ci/check-content`, `ci/check-docs`, `ui-stage --check`, `card-face --check`, `web-key-map --check` pass; the D-314 Presentation compile passes. The PlayMode smokes were edited (title indices, Next then Begin, `OpeningOverride`) but not run.

**Not verified (Unity was not run):** every USS rule and the layout of every screen above; the `Button` children of the plates and class cards (`.unity-button` text vs children); `-unity-background-scale-mode: scale-and-crop` for the title and journal paintings; the new art catalogs (`UiArtCatalog` must list `bg.tower-entrance-hall-phone`, `UiSceneArtCatalog` the `scene.spire-vista-desktop` and `prologue.*` ids: run `Cli.BuildUiArt`); `Slider` styling (`unity-base-slider__dragger`); `TextField` focus and typing on pad; `ScrollView.ScrollTo` on focus; the typewriter timer, the fade and the auto-advance; focus order and the `review` / `modal` focus modes; the phone compositions (VP-14).

**Owner: check in the editor (1920x1080, then 390x844; compare `docs/qa/web-screens/`)**
- `startup-*` and `title-*`: the gate shows ASHEN SPIRE in parchment over the dim city with PRESS ENTER OR SPACE pulsing; any key lifts it; the title shows the door vault, the gold wordmark with its glow, plates (New Game green, Settings with the gear), the Continue card only with a save, the tagline and the build stamp. Down from the gate lands on Continue (with a save) or New Game.
- `flow-03`: New Game opens the narrow door; the chosen row has the gold edge; Load Game with no save shows the empty state.
- `flow-05`, `flow-06`: class cards, the List | Grid switch, the locked class with its lock and note; Next, then Review: the seed field, Slot n, the four pieces and the deck with `x4`; Begin; a bad seed (punctuation) refuses.
- `flow-07`: after Begin the opening plays five paintings with their captions; Pause holds the auto-advance, Skip opening goes straight to the map, Settings opens over it; Continue becomes Set forth on the last.
- `settings-*`: the three tabs, the group chips, a switch, a choice row, a slider (drag, and the minus / plus), the dot beside a changed setting, Changed, the magnifier, Reset to defaults, About under Advanced; open it from the pause menu mid-run too.
- `run-history-*`, `compendium-*`, `profile-*`: Journal on the title; a finished climb adds a row; the Compendium counts 0 of 28 and shows unknowns; the Profile says it is fine and lists the three slots.

**Next:** the phone compositions (VP-14); the web's settings that need engine support (accent, high contrast, haptics ...); the profile archive (set-aside copies) if the owner wants it; music and staging per opening scene; Load settings / Download and saves once a file picker exists.

## VP-8 and VP-9: the Core 2 rest site (with the Smith modal and Level Up) and the shop (worktree branch, 2026-10-24)

**Landed (D-950..D-953)**
- **Rest screen** (`core2Rest`, `Core2RestScreen`, `Core2Rest.uxml/.uss`): the painted chapel with the traveler at the fire under the board HUD, the door on the right (REST, the gold title, `1 of 1 available`, the arrival line, the line under it, the recovery preview table, a card per service, Continue). A service that cannot be taken refuses at its card with its reason. The Smith modal lists the items (picture, tier step, owned count), shows the one chosen (stat rows, requirements with `You have n`, the deck cards the tier changes before and after from `SmithCardRows`, the recovery, the Stone cost), asks `Upgrade X?` (PERMANENT FOR THIS RUN) and takes the tier; Level Up lists the attributes (a point goes to one; pressing it again takes it back) and confirms with `Assign point`.
- **Shop screen** (`core2Shop`, `Core2ShopScreen`, `Core2Shop.uxml/.uss`): the merchant's tent with the merchant standing left and the frame on the right: `Merchant` (the close glyph leaves), the shelf's name, an offer per row (card face or picture with Inspect, gold name, one line, Buy with its price and, when it cannot be bought, the web's reason under it), the dock of shelf tabs and Leave. Inspect opens the item (a card as its large face; others with picture, words, numbers and the weapon art they grant); Buy asks `Buy this item?` with the purse before and after and `BUY IT`.
- **Logic engine-free and tested:** `Core2RestFlow`, `Core2ShopFlow` (and `Core2ItemFaces`, the faces shared with the spoils door; `Core2SpoilsFlow.Option` now delegates to it), `Core2RestShopFlowTests` (12). Routing: `core2Screens.merchant/rest`, `ScreenFactory`, `ui/screens.json`; `PendingPort` no longer reports an open shop or site (the map's notice remains for the treasure room, the event and a profile without a rest module); the tests that used a rest or shop as the not-yet-ported place use a treasure room. New: `BoardHud` (the heart, HP line, pips, armoury and menu buttons, cinders of the rooms of a run), `Kit/ItemFaces`, `UiTokens.Ratio` and `RatioKeys`.

**Tests run (small filters, one at a time):** `Core2RestShopFlow` 12/12, `Core2Screens` 16/16, `Core2RunSessionTests` 10/10, `Core2ShopSession`, `Core2RestSession`, `Core2RewardsFlow`, `Enforcement` 7/7, `UiData` 9/9; node checks `ui-tokens`, `codegen`, `refresh-manifest`, `transform-content`, `ci/check-content`, `ci/check-docs`, `ui-stage`, `card-face` ok; Presentation compiles against the UnityEngine reference assemblies (D-314 harness).

**Not verified (Unity was not run):** every USS rule of `Core2Rest.uss` and `Core2Shop.uss` and the layout of both screens; the painting's square stage and the traveler's placement (`LayoutScene`: geometry-changed, a stage taller than the room clipped by `overflow: hidden`), the merchant's fitted portrait (`LayoutPortrait`), `-unity-background-scale-mode: scale-and-crop` for the tent, nested `ScrollView`s inside the modal's own scroll body (the Smith's two columns: `flex-grow` on the content container), `LocButton`s holding children (service cards, item rows, offer media, attribute rows: `.unity-button` rules reset the kit button), the tabs' three columns (30% + the gap), `CardView` at 44 x 66 inside an offer row and at 267 x 400 in Inspect, the modal width rung classes `cmodal--rest/smith/shop`, focus order inside the modals, art lookups of the scene, illustration and armament icons through the lazily loaded scene catalog (`scene.chapel-rest-master`, `illus.rest-traveler-fire`, `scene.merchant-desktop`, `illus.merchant-portrait`), armour has no icon in the art release (the kind's icon stands in), phone compositions (VP-14: only the narrow rules that keep the door and frame on screen).

**Owner: check in the editor (Core 2 run at 1920x1080 then 390x844; compare `docs/qa/web-screens/rest-*`, `rest-wounded-*`, `smith-modal-*`, `shop-*`, `shop-inspect-*`, `shop-buy-press-*`, `master-merchant-*`)**
- Enter a rest node: the chapel fills the room under the HUD (the traveler sits on the floor at the fire, left of centre), the door at right: REST, `Shrine of Ember`, `1 of 1 available`, the healed line, `Use any service here...`, five recovery rows (HP, MP, SP, Healing flasks, MP flasks: `71 -> 71` and the dash), the Smith card, a green Continue. Pressing the Smith card opens the wide modal: the item list at left with `Greatsword Tier 0 -> 1` and a count chip, the smith icon and the instruction at right; choose the item to see its stat rows, requirements, the Strike/Block rows before and after and Upgrade (0); Upgrade asks `Upgrade Greatsword?`; the Smith card then reads `Done at this site.` and refuses at the card when pressed. Escape closes a modal, then opens the pause screen. Continue returns to the map; the node can be entered again without healing twice (D-338). Level Up appears only with a point (set `level.unspentPoints` by a test edit).
- Enter a merchant: the tent with the merchant at left, the frame at right with the tabs CARDS, WEAPON ARTS, ARMAMENTS, ARMOUR, RELICS, FLASKS (stock order), three offers a shelf (the card shelf shows card faces), `10 cinders` under each Buy. Inspect shows the item; Buy asks the question with the purse before and after, BUY IT spends and the row reads taken and dimmed, `Unavailable` under its price; with too few cinders Buy refuses at the button with `Not enough cinders`. Leave (or the close glyph) goes to the map; Continue from the title returns to the same shop with the same stock.

**Next:** the web's HOLD on Buy (the Unity hold dial) if the owner wants the shot's look rather than the question; the full-game blacksmith and master merchant doors; phone compositions (VP-14); the event screens (VP-9's other half) with WP-19.

## WP-19: the Core 2 run loop, start to finish (worktree branch, 2026-10-24)

**Landed (D-345..D-350)**
- **Nodes:** treasure = the combat reward door at the node; event = `Core2RunSession.EventId/EventDomainView/ChooseEvent/FinishEvent` over `Core2Content.Events` (the 405 event code on the profile's registries; Core `upgradeCard` through `Core2Smith`, the configured `swapClass` refusal, history gates from the catalog sidecar); `Core2EventView` carries the web's words. Nothing is answered "not yet ported" any more (`NotYetPorted` is always false); `PendingPort` is `event` while one is open so the act map's stand-in keeps the run moving.
- **Run end:** `Core2RunEnd` is the configured `completedRunMeta` (record with run id and profile pin, class unlock ledger, no progress tally).
- **Defects found by the oracle:** rest drafts lost the sideboard from `collection` (draft shares the profile pin now); fights refused event curses (`guilt`).
- **Oracle and tests:** `Tools/oracle-core2-loop.mjs` (32 whole runs), `Core2LoopParityTests` (33), `Core2LoopSessionTests` (5); `Core2RunSessionTests`/`Core2ScreensTests` updated (events are the remaining stand-in notice; treasure is the reward door); merged `dev` (VP-8/9).
- Checks: codegen, refresh-manifest and the dev merge's tests run; Unity itself was not run.

**Not verified (Unity was not run):** nothing of the event screen exists yet (Presentation); the stand-in notice's Continue picks the first response the purse can pay.

**Next:** the event/dialogue screen over `Core2EventView`; the game-over screen over `ClassesUnlocked`; WP-21 de-scope.

## VP-10: the act map, the Armoury modal and the deck editor (worktree branch, 2026-10-24)

**Landed (D-900..D-905)**
- **Map:** the web's act map in UI Toolkit: parchment ground (a generated repeating texture), the painted atlas of the seed's world revealed around the fog's lights (a textured mesh with per-vertex alpha), the walked route as an outlined line, 87 px node discs with the kind's icon (unknown places in a dashed ring, the walked ones faded, the current one filled, the reachable ones haloed), the progress strip with a slot per floor, the zoom bar, the Potions disc, the legend popover, the tray (floor, kind, blurb, Back and Enter) and the Deck / Equipment / Stats / Menu key bar with its keys. A Core 2 run's fog is pinned by its profile (`routes.mapMode`), so it draws the web's lit set whatever the setting says; the 405 run keeps D-151.
- **Armoury modal:** Character (class, level, XP, sheet, relics), Armory > Cards (the deck as card faces), Armory > Equipment (equipped positions, locked sets with their unlock hints, an inspector), and the Edit Deck tab, the deck editor over `Core2DeckEditor` (rows, tiles, counter, cost curve, filter and sort chips, inspector, Undo / Done / Cancel with the discard door, refusals at the control). Doors: the map's keys, the HUD button, the pause row.
- **Tests (small filters, one at a time):** `MapAtlas` (15), `Core2Screens` (16, five new for the Armoury views and the deck editor door), `Enforcement`, `UiData`, `Core2DeckEditorSession` all pass; node: `ui-tokens`, `codegen`, `refresh-manifest`, `transform-content`, `ci/check-content`, `ci/check-docs`, `ui-stage`, `card-face` `--check` pass; the D-314 Presentation compile passes.
- Merged `dev` (VP-7 spoils): conflicts were additive (tokens.json webSource, manifest).

**Not verified (Unity was not run):** every USS rule and the layout of both screens; `Allocate` / `SetNextVertex` mesh drawing and the uv orientation of the atlas tiles (v up assumed); the parchment's `background-repeat` over a Texture2D; the dashed ring's tint and the vignette stretch; `KeyDownEvent` hotkeys reaching the screen root; the nested scrolling of the editor panes (the whole body scrolls); the Armoury over the pause menu; focus restore after a re-render (by `userData` keys).

**Owner: check in the editor (Core 2 run at 1920x1080; compare `docs/qa/web-screens/map-start`, `map-trail`, `map-trail-selected`, `map-midclimb`, `map-legend`, `map-deck`, `map-equipment`, `map-stats`)**
- New game: the map shows the entrance: three monster discs on parchment with the painting revealed around them only, the strip with 7 empty slots, the key bar; the boss is lit at the top (scroll up) with its own reveal. Pick a node: tray with FLOOR n, the kind, its blurb, Back and a green Enter; Enter travels. After a fight the walked node is cream, the route line is drawn to it, its split is lit and revealed, and the strip slot shows its icon with the current ring.
- Keys D, R, T, M and the four buttons; the HUD armoury button; pause > Armoury. Deck: a grid of card faces and Edit deck. Equipment: BODY and MH1 with their pieces, MH2 and OH2 locked with "Reach the ..." hints, OH1 Empty and Active. Stats: the class, level 1 and its XP bar, HP SP MP Draw, relics.
- Edit Deck: add and remove copies (refusals show over the pressed control), Undo, Done saves, Cancel with changes asks to discard; Escape does the same. During a fight the door refuses ("The deck cannot be changed during a fight").
- Settings: Map reveal does not change a Core 2 run (pinned); a 405 run still follows it.

**Next:** the spoils review's Edit deck action (needs the pending reward and the editor reconciled); Combat Power and the derived ratings on the Character pane; Replace / Unequip and Inventory; Potions outside a fight; phone compositions (VP-14).
## Core 2 fight oracles regenerated with the boss tier scale (D-324 follow-up, 2026-10-24)

**Landed:** every oracle that builds a fight through `createFight` was re-run on the pinned web (`b8cfe4fac`) after D-324: `combat`, `costs`, `damage`, `enemy`, `hit`, `matchups`, `range` (`oracle-core2-reactions.mjs --bot range` plus `oracle-core2-range.mjs`), `reactions`, `remaining`, `statuses`, `snapshot` (`start`, `session` and `deck` were done with D-324). `.meta` files untouched.
- Unchanged (byte-identical, no boss in their fights): `combat`, `costs`, `hit`, `range/quotes.json`.
- Changed, every changed fight record a boss encounter, all others identical: `damage` (fights 30 of 128; receipts and units: target / source `hp`, `maxHp`, `damageMult`), `enemy` (fights and exchanges 14 of 101), `matchups` (11 of 69), `range` (fights 9 of 32, exchanges 46 of 328), `reactions` (fights 10 of 32, exchanges 7 of 120), `remaining` (fights 89 of 340, units 12 of 48), `statuses` (fights 24 of 96, scenarios 11 changed), `snapshot` (5 enemies; 3,121 states considered becomes 3,120, still 42 snapshots). What changed inside a boss fight: enemy `hp` / `maxHp`, the new `damageMult`, move and intent damage, and what follows (events, turn counts, later steps and the deltas of a fight that ends sooner or later).
- Checked with a JSON-aware per-record diff; `matchups` re-run twice gives identical bytes.

## VP-7: the Core 2 spoils screen (worktree branch, 2026-10-24)

**Landed (D-850, D-851)**
- **Screen:** `core2Rewards` (`Screens/Core2Rewards.uxml`, `Spoils.uss`, `Core2RewardsScreen`): the web's reward-door modal (`Kit/CombatModal`, md) over the act map's background and the run HUD. Level notices first ("Level Up" / "Level up", one "label . Level a -> b" sentence per crossing, Continue), then the offer: the cinders row with TAKEN, the note, a column per option (card = VP-3 `CardView`; relic, flask, armament, armour = collectible face from `Kit/SpoilsOption`) with NEW/TAKEN chip and its name as the radio, a status line, Skip and Confirm. Confirm reviews the pick: a card gets Add to deck, Keep in collection and Back, anything else Continue and Back.
- **Refusals (the Refusal pattern):** Add to deck at the deck limit shows the web's "Your deck is full (N cards). Edit your deck to make room." at the control and in the status line (button dimmed, still pressable); nothing picked says "Select one first."; a taken option says "Already taken."; claim refusals are the domain's sentences. A failed save asks to retry (the shared door).
- **Routing:** `runFlow.json core2Screens.rewards`; after the victory beat the fight goes to the spoils (a defeat keeps its panel); a save made at the door resumes on it (Continue on the title); leaving goes where `Core2RunSession` says (map, next act after a boss, run end). The act map's spoils notice (`PendingPort == rewards`, `core2.port.rewards.*`) is removed.
- **Engine-free:** `Core2SpoilsFlow` (+ `Core2SpoilsOption`, `Core2SpoilsNotice`, stages and chips) owns every field and press; `Core2RewardsFlowTests` (12) hold it over a real won fight. Data: tokens `size.spoils*`, strings `spoils.*`, codegen `ui-spoils.json`, screen registry entry.
- **PlayMode smoke** `Core2FightSmokeTests` now expects the spoils screen after a win (notices read by Continue, Skip back to the map); not run (no Unity here).

**Tests run (small filters, one at a time, lean project):** `Core2RewardsFlow` 12/12, `Core2Screens` + `Enforcement` + `UiData` 24/24, `Core2RewardsSession` 9/9; node checks `ui-tokens`, `codegen`, `refresh-manifest`, `ui-stage`, `card-face`, `ci/check-content`, `ci/check-docs` ok (`transform-content --check` needs `Tools/export/raw/bundle.shipped.json`, absent here, as before). Presentation compiled with the D-314 harness.

**Not verified (Unity was not run):** every USS rule of `Spoils.uss`, the three card faces sharing the 560 modal (168 x 252 each), `CardView` inside a column with a chip child, collectible art (relic and flask icons, armament icons) and the icon fallback, `-unity-background-image-tint-color` on the cinders icon, focus order (options, Skip, Confirm; Confirm after a pick), the dimmed Confirm, Refusal placement at a pick button, the map background under the veil, narrow layout (`layout-narrow` sizes), the PlayMode smoke change.

**Owner: check in the editor (win a Core 2 fight at 1920x1080, then 390x844; compare `docs/qa/web-screens/reward-*`, `combat-victory-card-pick-*`)**
- After the victory ribbon the map appears dimmed with the modal: eyebrow "CLAIM YOUR SPOILS", gold "VICTORY", "0 of 1 claimed", a dimmed cinders row ("32 cinders / Granted . 52 total / TAKEN"), the note, three columns (card faces or collectible faces) each with a NEW chip and its name as a button under it.
- Pick one: the face and name wear the gold edge; Confirm with nothing picked shows "Select one first." at the button. Confirm: only that option, Back / Keep in collection / Add to deck (a non-card: Back / Continue). Fill the deck (Settings deck max) to see the full-deck words and the dimmed Add to deck.
- Win a fight that crosses a level (class or weapon track): the notices modal comes first ("Level Up"), then the offer. Quit with Save & quit at the door and Continue from the title: the same door returns. Skip, Add to deck and Continue go to the map; a boss goes to the next act's map.
- Pad: options, then Skip, then Confirm; B/Escape goes Back under review, else opens the pause screen.

**Next:** Edit deck / Equip now once the deck editor and armoury screens exist; the full-game door's Progression column and Continue HOLD if the owner wants the shot's look rather than the Core door's strip; VP-8 rest.

## VP-5: combat feedback and animation (worktree branch, 2026-10-24)

**Landed (D-750, D-751)**
- **Data:** `ui/feedback.json` (+ schema), tokens `fx*` and the unitless group `ratio`, strings `core2.fx.*` (the web's words), the `fx` art group and lookups, codegen fragment `ui-fx.json` (event and key names, enums over the data's callout kinds and tracks).
- **Engine-free:** `FeedbackDefs`, `FxCurve`, `CombatPace`, `CalloutPlan`, `Core2Feedback`, `FxTimeline`; `CombatFeedbackTests` (22) pin the maths, the pace, each event rule and the stage order.
- **Screen:** `FxLayer` (floats, words, banners, marks, effect frames, shake, card flight), `FxMotion` (keyframe tracks), `Fighter` (figure element, pose frames, recoil/lunge/wobble), `Core2CombatScreen.Fx.cs` (plan, schedule, cues, boss splash, victory beat), `Fx.uss`, UXML for the splash and the beat, the ribbon's web tints.
- Merged VP-6 (combat modals) twice; conflicts were only additive (tokens, art lookups, UXML styles, the HAND_WRITTEN list).

**Tests run:** `CombatFeedback` 22/22, `Enforcement`, `UiData`, `ArtLookup`, `AssetRegistry` pass (small filters; the test host aborts when several filters run in one process); `ui-tokens`, `codegen`, `refresh-manifest`, `ci/check-content`, `ci/check-docs`, `ui-stage`, `card-face` checks ok; Presentation builds with the D-314 harness.

**Not verified (Unity was not run):** every USS rule, the scheduler-driven motion, `-unity-text-outline`, glyph rendering of the ribbon's `‹ ✧ ›` in Cinzel (move the glyphs to a fallback font if they show boxes), pose frames sized as squares over the figure box, hit tint multiply look, effect frames (art imported by the owner's pipeline; this checkout has no PNGs).

**Owner: check in the editor (Core 2 fight at 1920x1080; compare `docs/qa/web-screens/combat-hit-feedback-*`, `combat-enemy-turn-*`, `boss-intro-*`, `combat-victory-card-pick-*`)**
- Play Strike on an enemy: the card flies to it (220 ms), "Attack · Strike" rises over you, you lunge right, a slash arc and "Hit" appear, the enemy flashes red, recoils right 22 px and shows its hurt frame, the screen shakes, then a red `-12` pops (bigger tiers for a third of its HP) 500 ms apart per stage at the default pacing.
- End Turn: "ENEMY TURN" pops, the ribbon turns rust, each enemy's action word, lunge/attack frame, `Missed`/`Dodged`/`BLOCKED` words, `-N` on you (recoil left 13 px), "YOUR TURN" at the end; the log lines appear with their numbers; Skip and a click end it. Change Settings: combat speed, step pause, animation step, animation speed (instant: no waits), reduced motion (plain fades, no lunge/shake/effects), reduce flashes, screen shake.
- Block, Poise, statuses: gray `4` beside `-5`, "POISONED" in green, effect frames where imported; an enemy at 35% HP or with Block wears its wounded/guard frame; a kill shows `Defeated` and the defeated figure.
- Boss fight: splash with "ACT 1 · BOSS" and the name for 2.3 s, any click closes it. Victory: the title ribbon for 0.6 s then the panel. Defeat: panel with the orange title.

**Next:** `ui/feedback.json` hooks for player pose frames and card-cast effect plans once art is imported; HP bars that update after the numbers; VP-12 run-end restyle.
## WP-16: Core 2 rest site (worktree branch, 2026-10-24)

**Landed (D-334..D-340)**
- **Domain (`Domain/Loop/Core2Rest.cs`, `Domain/Shop/Core2Smith.cs`, `Core2CardFace.cs`):** the web's `beginGameProfileRest` (the arrival heal, once), `commitGameProfileRest` (free Smith, Level Up, every refusal), `restRevisit` (the re-entry record), `rederivePools`, and the Smith's plan and commit for a Core 2 run with its live card rows (two throwaway fights, `createRng(17)`). The 405 smith is untouched but for four helpers made `internal`; `CoreRunRules.CombatArgs` accepts no encounter; `CoreCosts.PreviewPlayCosts` prices a card in any pile.
- **Application:** `Core2RunSession` opens a rest node (`Location` `rest`, `PendingPort` `rest`, Continue leaves), `TakeRestService` / `UpgradeItem` / `SpendPoint` / `LeaveRest` / `SmithCardRows`; `ReachableNodes()` keeps a left site enterable; a reload lands on the map as the web's does. `Core2RestView` is the display data (services, reasons, recovery rows, Smith items, attributes, the modal lines) with the web's strings as `core2.rest.*`.
- **Content:** `rules/core2Rest.json` + schema, `Tools/codegen.d/core2-rest.json`, `Core2RewardsData` carries the Smith's data (`ShopData` over the same registries) and the rest rules.
- **Oracle:** `Tools/oracle-core2-rest.mjs` (units: 24 plans, 20 re-derivations, 4 door runs; 16 climbs of 8 visits, 4.0 MB under `Unity/Assets/Tests/Oracle/core2/rest`).

**Tests run (one filter at a time):** `Core2RestParityTests` 20/20, `Core2RestSessionTests` 8/8, `Core2RunSessionTests` 10/10 (5 min), `Core2ScreensTests` + `Core2RewardsSessionTests` + `Core2ShopSessionTests` + `Core2DeckEditorSessionTests` 33/33, `Core2RewardsParityTests` 71/71, `Core2HostParityTests` + `Core2RunParityTests` 362/362, `EnforcementTests` 7/7; `codegen --check`, `refresh-manifest`, `ci/check-content`, `ci/check-docs` ok. `transform-content --check` cannot run without the raw export (`Tools/export/raw` is not in the tree).

**Not verified:** no rest screen exists (the seam is `Core2RestView`); Unity was not run; Level Up is reachable only by a test edit; a card in a pile other than the opening hand is priced without the web's `preparingPreview` clone (no card of the shipped content listens for preparing).

**Next:** WP-19 routes the treasure and event nodes and the class-unlock ledger; a rest and a shop screen over `Core2RestView` and `Core2ShopView`.


## WP-17: Core 2 shop (worktree branch, 2026-10-24)

**Landed (D-330..D-333)**
- **Domain (`Domain/Shop/Core2Shop.cs`):** the web's `gameProfileShopStock` and `purchaseGameProfileShopOffer`, `Core2Rewards.MakeOffer` extended with `itemsPerCategory` (the `shop` stream, authored category order, no stance reserve). The legacy merchant is untouched.
- **Application:** `Core2RunSession` is partial; `Core2RunSession.Shop.cs` enters the shop at a merchant node (a profile without shops goes on to the map), keeps `Location` `merchant` while `gameProfileOpenShop` is on the run (so a save resumes at the shop), `BuyFromShop`, `LeaveShop`; the placeholder Continue leaves it. `Core2ShopView` is the display data (shelves, prices, availability and reasons, the confirmation text) with the web's `shop.*` strings as `core2.shop.*`.
- **Oracle:** `Tools/oracle-core2-shop.mjs` (32 scenarios, 3.7 MB under `Unity/Assets/Tests/Oracle/core2/shop`): stocks, buys with every refusal, remounts, tampered prices, other `shops` module settings.

**Tests run (one filter at a time):** `Core2ShopParityTests` 33/33 (32 scenarios and the oracle's tallies), `Core2ShopSessionTests` 8/8, `Core2RunSessionTests`, `EnforcementTests` 7/7; `codegen --check`, `refresh-manifest`, `ci/check-content` ok.

**Not verified:** no shop screen exists (the seam is `Core2ShopView`); Unity was not run.

**Next:** WP-16 (rest) shares the door work in `Core2RunSession`; WP-19 routes the treasure and event nodes.


## VP-6: combat overlays (worktree branch, 2026-10-24)

**Landed (D-800..D-804)**
- **Modal shell (`Kit/CombatModal`, D-800):** the web's modal (framed panel, shadow, eyebrow/title/status/close head, scrolling body, foot of buttons, veil) as one kit element; the flow's prompt panel is the same modal (`PROMPT_PANEL` is the scrim), so every prompt wears it.
- **Reaction offer (D-801):** eyebrow "Defensive reaction", title "Respond to the incoming action", the incoming action and help line, card tiles (name, the card's text, SP/MP pills) that select, then Play; X, Back and Escape decline. The web has no timer and no skip beyond Back.
- **Potions (D-802):** the web's list (flask art, gold name, effect and charges, count, Use HOLD) from the flask definitions; refused flasks stay dimmed and say why.
- **Piles (D-803):** Draw pile grid (sorted), Card piles with a Discard/Exhaust rail, faces and the reading of the selected card; `Core2CombatView.Pile` builds the cards on demand.
- **Card inspector (D-803):** `CardView.ShowInspect` on for the hand (the "i" on the hovered/focused card), a large face beside name, type, cost, text and keywords.
- **Target card and quick menu (D-804):** hover/focus/tap card with name, HP, chips and intent, the "i" reading; the menu button opens the dropdown (pause rows, red exit row) and runs the pause actions (now `PauseActions`).
- **Data/API:** `Core2FlaskView.Text/ArtKey`, `Core2CombatView.Pile`, `FooterGroup.DrawButton/DiscardButton`, `OverlayKeys/Values/Formats`, art lookup kind `flask`, 43 `combatModal.*` strings, tokens (`size.cmodal*`, `option*`, `potion*`, `pile*`, `inspect*`, `quick*`, `target*`, `font.tile*`, `color.veil`, `duration.targetHide`), screens.json `overlay` focus mode and `PROMPT_CLOSE`.

**Tests run (small filters):** `EnforcementTests` + `UiDataTests` 16/16, `Core2ScreensTests` + `Core2CombatFlowTests` + `CodegenTests` 25/25, new `Core2OverlayTests` 4/4 (each pile lists exactly its cards; the draw pile is sorted; every flask has text and art that resolves; the quick menu is the pause list in a fight and the overlay focus mode is registered); `ui-tokens`, `codegen`, `refresh-manifest --check`, `ci/check-content`, `ci/check-docs`, `ui-stage --check`, `card-face --check` ok. Presentation compiles against the Unity 2021.3 module DLLs with the D-314 stubs.

**Not verified (Unity was not run):** every USS rule of the new overlays, the 9-slice modal frame at the three widths, the scroll view inside the modal, `Button` tiles with child labels, the flask art textures (the `flask.*` ids are not in a lazy art group, so they must be in the always-loaded catalog that `BuildUiArt` writes), hold buttons inside a scroll view, the hover card placement near the screen edge, and the 390x844 layout of every overlay.

**Owner: check in the editor (play a Core 2 fight at 1920x1080, then 390x844)**
- Reaction offer (enemy turn with a defensive card in hand, reactions on): compare `combat-defeat-reaction-offer-1920x1080.jpg`; Play dim until a tile is picked; X, Back and Escape skip; the pad reaches X, tiles, Back and Play.
- Potions button: compare `combat-potions-1920x1080.jpg`; Use must be held (HOLD fill), a refused flask shows its words, an aimed flask then asks for its enemy.
- Draw button (`combat-draw-pile-*`) and Discard button (rail and reading); Escape closes and focus returns to the button.
- Hover a hand card: the round "i" above it opens the inspector; Escape closes.
- Hover an enemy and the player: the target card (`combat-target-hover-*`, `combat-intent-tooltip-*`); a tap pins it; "i" opens the reading.
- Menu button: the dropdown (`combat-quick-menu-*`); Save & quit and Abandon open their doors; pad Start still opens the pause screen.

**Next:** VP-9 and VP-10 can reuse `CombatModal` and `OptionTile` for their modals; the web's stance/counter rule hints on the inspector, status tooltips in the combatant reading and the Fullscreen/Music rows of the quick menu need data the view does not carry.

## VP-4: Core 2 combat board (worktree branch, 2026-10-24)

**Landed (D-700..D-703)**
- **Board composition:** `Core2Combat.uxml/.uss` are rebuilt as absolutely placed layers from the web's 1920x1080 and 390x844 shots (tokens `size.board*`, cited in `tokens.json#webSource.board`): environment art `cover`-fit above a 107 px dark footer, the sheared ground grid (`Tools/ui-stage.mjs`, D-701), the ally slot at the left and the enemy lane (three to a row, the fourth wraps) at the right, the 3 px HP line with heart, relic pips, Player/Enemy Turn ribbon, armoury and menu icon buttons and `CINDERS n` at the top, the hand centred over the footer's top edge (136x204 cards, 97 px step; 120x180 on the phone), the log as a framed panel.
- **Fighters (D-702):** overhead with intent range and icon, a slot-wide HP bar with the value under it, thin Poise/Ward bars and icon chips (Evade, Block, Precision, Barrier, Counter, statuses with `icon.<id>`); ground shadow; name, ghost HP and receipt below the shadow on hover/focus/target; the white hit chance over the figure.
- **Footer:** new `Orb` kit element (orb, harness, SP number, mana diamonds on a 30-degree ring), `FooterGroup` re-skinned (draw/discard/potion art with counts and captions, End Turn on `footer.plate`), tools row with Combat log, Grid check and the Reaction pill (D-703). Behaviour and bindings in `Core2CombatScreen` are unchanged; the screen only sets art by name, builds chips from the view's own statuses and toggles the log, grid and pill classes.
- **Data:** tokens, 9 strings (`core2.board.*`), codegen (`UiNames`, `UiClasses`, `UiResources.KitOrb`, new `BoardArt`, `BoardIcons`, `BoardMath` in `codegen.d/ui-board.json`), `COMBAT_LOG` added to the combat focus order, manifest refreshed.

**Tests run (small filters, the test host is OOM-killed intermittently with other workers running; each passed when it ran to completion):** `EnforcementTests` 7/7 (no magic strings or numbers), `UiDataTests` 9/9 (generated names in UXML, string keys, USS tokens only), `UiRulesTests` 21/21, `CodegenTests` 2/2, `Core2ScreensTests` 7/7; `ui-tokens --check`, `codegen --check`, `refresh-manifest --check`, `ci/check-content`, `ci/check-docs`, `ui-stage --check`. Presentation compiles against the Unity 2021.3 module DLLs with the D-314 stubs (a copy of the harness in the scratchpad plus a `GetValueOrDefault` stub for net48).

**Not verified (Unity was not run):** every USS rule, the Painter-free grid sprite, the `Rotate`d diamond ring, the 9-slice log panel, the hand padding maths, the background `cover` crop (the imported `env.*-combat` art is a legacy Blender render that the release import does not carry, so the fallback `bg.bg_act1` shows until it is), and the phone layout at 390x844.

**Owner: check in the editor (play a Core 2 fight at 1920x1080, then 390x844 in the Device Simulator)**
- Compare with `docs/qa/web-screens/combat-card-selected-*`, `combat-three-enemies-*`, `combat-four-enemies-390x844`, `combat-enemy-turn-*`: HP line and ribbon at the top, grid under the feet, three equal long HP bars over three enemies, the player bar over the player at the left, footer art row.
- Select a card: legal targets lift, show their name plate and the `95%` hit chance; the card stays in the fan above the footer (cards must not be clipped at the top when hovered: `size.boardHandTop`).
- Orb: the stamina number sits in the harness; one diamond per Mana point at the top, spent ones greyed; the number updates as cards are played.
- Footer: Draw/Discard art with counts under them, Potions tray, End Turn plate tints (rest, hover, green-ish text when stamina is spent, red while the enemy acts).
- Combat log opens as a framed panel, Close and Escape close it; Grid hides the lattice; the Reaction pill flips green/red with the knob side.
- Pad: Down from the hand still reaches End Turn; `COMBAT_LOG` joins the order when open.
- Select `Resources/UI/Stage/grid-wide.png` in the editor: Sprite, no border. Frames, Stage and imported art import settings are the same as `Resources/UI/Frames`.

**Next:** VP-5 feedback hooks (hit flash, floating numbers: the fighter art and the `FIGHTER_HIT` label are the anchors), VP-6 modals for the log (web is a full-screen modal), piles and potions; the target HUD card; Auto toggle if auto-play ever lands in Core 2; relic pip tooltips.
## VP-3: card face rebuild (worktree branch, 2026-10-24)

**Landed (D-650..D-652)**
- **Kit card (`Kit/CardView.cs`, `Card.uxml`, `Card.uss`):** the web's illustrated card, laid out in the template's 360x540 design px (token group `face`, 148 values) and scaled to the element's width: bronze base frame, art window, title fade, footer backdrop and action frame, parchment, the bronze ring over the painting, the hanging flag with the stamina orb and mana diamond and their numbers, name and rules fitted to their boxes (the web's autoFit), the type band (action mark and word), the tag rail, the rank gem, the glow (hover, focus, armed, chosen), the dim tint and refusal strip of an unaffordable card, the inspect "i" button (hidden until `ShowInspect`). Sizes are 2:3 now (hand 140x210, pick 184x276, narrow 112x168).
- **Art composed like the web (`ui/cardface.json`, `CardFaceLookup`):** `Tools/card-face.mjs` ports `approvedCardArt` (28 cutout compositions, selection by camp, maneuver, element, tags and effect ops), the type band and a reduced tag rail from the web at b8cfe4fac; 211 Core 2 cards all resolve (Strike -> O00 silver-blue attack, Defend -> O44 shield, spells -> Y/Z by element). A card with its own painting keeps it; a missing cutout gives the card back to the D-552 library painting.
- **Sprites:** `Resources/UI/CardFace` (50 PNGs, 450 KB, committed like Frames): 14 sigils, 31 tag marks, title fade, footer frame, rank gem, glow, ring.
- **Data/API:** `CardViewData.Face` and `Rank`, `CardView.Source` (set by `UiContext.Create`), `Core2CardView.Effects/Face`, `RewardPickView.Face`; one-line mapping changes in `Core2CombatScreen.CardData`, `CombatScreen.CardData`, `RewardsScreen`, `MerchantScreen`; `.combat-hand .card-art` and `.band-compact .pick-card .card-art` USS removed.
- **Tests:** `CardFaceTests` (12: glyphs exist as files, every sigil has its word, every Core 2 card plans a face, Strike/Defend/Counter/Sundering Art compositions and rail, spell and projectile codes, utility skills, missing cutout fallback, text fits). Filtered runs (CardFace, ArtLookup, UiData, Enforcement, Codegen, AssetRegistry, ContentValidator) passed before the shared test host started being OOM-killed by other workers' 5 GB runs; `Core2CombatViewTests` aborts the test host with and without this change (pre-existing, as the VP-2 entry notes for the full run). Presentation was compiled with the D-314 throwaway csproj (net48 reference assemblies, C# 9): Build succeeded. `ui-tokens --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `card-face --check` ok. An offline HTML render of the same tokens with the real sprites (Chromium) matched the web's Strike and Sundering Art cards.

**Owner: check in the editor**
- Open the kit gallery and a Core 2 fight: cards show the bronze frame, the painted art with the cutout object (Strike = sword on blue, Defend = shield, Sundering Art = hammer), the parchment plate, the green flag (and the blue diamond under it for a mana card), ATTACK/DEFENSE/SMASH band with its mark, and the tag marks down the right edge. Compare with `docs/qa/web-screens/combat-card-inspect-1920x1080.jpg`.
- UI Toolkit scale: the face is a 360x540 child with `scale`; check text is sharp at 140 px (hand) and 184 px (reward pick), the face is not visible before layout (no flash of a huge card), and clicks and focus still land on the card (the face ignores picking). If the scaled text looks soft, give the labels `-unity-font-definition` FontAssets at the scaled size or render the face at the card's size (set the design tokens per size class).
- `MeasureTextSize` fit: long names shrink from 33 to 22 design px, rules from 31 to 22 and clip at three lines; check "Roll to evade: on a success..." and a four-line card.
- Sprites import as Sprites (`Resources.Load<Sprite>` in `CardView.Glyph`); `ring.png` and the frame crops line up (no seam at the corners); the glow is a soft ring outside the card, tinted gold on hover and blue when armed.
- `-unity-text-outline-width` on the name and numbers (the web strokes them), and `text-shadow` on the name.
- The hand fan: cards 140 wide overlap by -48 and lift on hover; the flag of a card hangs over its left neighbour as in the web.

**Not done:** element and status line-art motifs and screen-blend glows over the art, damage-type and counted tags on the rail, up/down colouring of previewed numbers (needs the view data), the hand's selected-card face lines, per-card layout overrides, the mirrored-spell second mark, the inspect modal that the "i" button should open (VP-6), the combat board (VP-4).

## VP-2: art release import, asset lookup by name (worktree branch, 2026-10-24)

**Landed**
- **Pipeline (D-550):** `scan-assets.mjs --release` + `Tools/lib/{assets-lib,release-art}.mjs` add the `hd-assets-v23` art additively (1,083 ids; 631 imported), downscale/rasterise with sharp, write PNG/JPG and deterministic `.meta`s (images gitignored per D-018). Registry schema allows `png`, `release`, `unityWidth/unityHeight`. Provenance and CREDITS refreshed. Run: `cd Tools && npm install`, fetch the release with the web's `tools/fetch-art.mjs` (high + common packs) into a scratch copy, then `node Tools/scan-assets.mjs --release <cache>/hd-assets-v23 --web <web repo> --import`.
- **What (D-551):** card paintings (237), card library (60), cutouts (44), card frame layers (7), 79 white icons + 10 UI components, stamina orb (5), footer art (5), flasks (5), events (20), scenes/illustrations (17), shop (51), prologue (32), creation (16), map-detail tiles (40). Generated size 74.6 MB (card 20.5, prologue 9.8, cutouts 12.8, illus 8.7, creation 3.6, mapdetail 2.6, shop 2.5, scene 2.4, rest under 2.2 each); committed: metas, registry (+1,059 ids), code.
- **Lookup (D-552, D-553):** `ui/art.json`, `ArtLookup`/`ArtConfig`/`ArtRegistry` (Application/Ui), `UiData.Art`, `UiContext.Icon/CardArt/Texture` with lazy group catalogs, `Cli.BuildUiArt` builds them; import rules for the new folders.
- **Tests:** `ArtLookupTests` (12): groups cover every imported release id, every Core 2 card/enemy/icon/event resolves (explicit event list), web hash parity. `AssetRegistryTests` scans `.jpg` metas too.

**Not verified:** Unity was not run: the `.meta` files are hand-written (Unity rewrites them on first import), `UiContext`/`UiTools` changes are uncompiled, the group `.asset` catalogs do not exist until `BuildUiArt` runs. The full Domain.Tests run aborts with a test-host crash in `BotRunTests`; filtered runs pass.

**Card frame layout (web `CARD_COMPONENTS.template`, 360x540):** base 0,0,360x540; art 10,40,340x336; panel 24,346,312x147; flag -20,-23,97x233; energy icon -37,147,130x113; mana icon -24,60,104x91; stamina icon -9,-20,78x80; title text 67,20,260x50; rules 57,383,246x84; tags 61,490,250x40. VP-3 uses these.

**Next:** VP-3 card face (frame layers, cutout composition), icons into the kit (replace glyph characters), event/shop/prologue/map screens read `ArtLookup`.
## VP-11: panel and button chrome (worktree branch, 2026-10-24)

**Landed (D-600)**
- **Sprites from the web's art:** `Tools/ui-frames.mjs` (Playwright Chromium, reproducible) writes 11 PNGs + `.meta` to `Resources/UI/Frames` (24 KB): modal/panel frame with corner brackets and gold top line, five ornate button plates, ornate divider, button bevel, modal and tooltip soft shadows. Sprite `spriteBorder` carries the 9-slice; USS slice scale comes from tokens `size.sliceFrame` (0.5 px) and `size.sliceSoft` (2 px).
- **Shared USS:** new `Styles/Chrome.uss` (shadow plate, `kit-frame`, `kit-divider`, `kit-chip`, 8 px scroll bar) imported by the theme; `Base.uss` buttons follow the live web kit button (Inter, 1 px edge, bevel, primary/ready/danger/back/disabled states) plus `kit-button--plate`; Shell, Confirm, SlotRow, Tooltip, Hold restyled in place, shadow plates added to `Shell/Confirm/Tooltip.uxml`. No screen files touched.
- **Data:** tokens (danger/primary fills, slice scales, shadow offsets, scroll bar, hold hint), `webSource` citations, codegen classes (`ButtonDanger`, `ButtonPlate`, `Chip*`, `Frame*`, `Divider`, `ShadowPlate*`).

**Tests:** `ui-tokens --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, Domain filter (Enforcement|Token|Ui|Manifest|ContentValid) run in this worktree; Unity not run.

**Owner: check in the editor**
- The Frames PNGs import as Sprites with borders (select one: Sprite Editor shows the green 9-slice lines), and a USS `background-image` of a Sprite is sliced by its border with `-unity-slice-scale` honoured (frames should show 12 px corners, not 24).
- Shell/Confirm show the brass frame with corner brackets, a soft shadow around the panel (not inside it), no 1 px double line at the edge.
- Buttons: Inter text, thin edge, bevel subtle; focus shows a 2 px gold edge; plate class on a LocButton (`kit-button--plate`) looks like the title menu entries in `docs/qa/web-screens/title-1920x1080.jpg`.
- Scroll bars are thin with no arrows; the hold tag reads HOLD TO CONFIRM in gold under the label.
- `kit-button` text changed from Cinzel gold to Inter parchment (D-600 point 4); say if you want Cinzel back.

**Not done:** End Turn raster plate and footer (VP-4), focus outline offset, tooltip arrow, modal head gradient and leather texture, hold diagonal stripe fill, applying `kit-chip`/`kit-frame`/`kit-divider` inside each screen (screen workers).

## VP-1: fonts, tokens and the web palette (worktree branch, 2026-10-24)

**Landed**
- **Fonts (D-500):** Cinzel, Inter (+ italic), IM Fell English (+ italic) from `hd-assets-v23` as TTF in `Resources/UI/Fonts` with `OFL.txt` and committed `.meta` files (new GUIDs, `TrueTypeFontImporter`); 397 KB. `Base.uss` sets Inter on `:root` and `.unity-text-element`, Cinzel on wordmark, eyebrow, header text and buttons, plus `.font-body/-italic/-display/-lore/-lore-italic`. Bold is synthesised.
- **Tokens:** `ui/tokens.json` takes the web hi-contrast palette, ready green `#255c39`, the gold/panel base button, new rot/frost/grace/ember/readyText/textInk/modalShadow/modalTopLine/wordmarkGlow colours, radii 3/6/8/12/pill/round, focus ring 2 px, the web type scale (body 14, button 15, heading 18, title 25.5, label/chip 9.3, caption 10, wordmark 46) and tracking/shadow lengths. `webSource` records the web file:line of each. `Tokens.uss` regenerated.
- **Type roles:** `.kit-label`, `.kit-title`, `.kit-heading`, `.text-shadowed` (USS `text-shadow`), wordmark glow, tracked eyebrow/button. `kit-upper` + `Loc.Display` upper-case `LocLabel`/`LocButton` text (USS has no text-transform); `UiClasses.Upper` generated.

**Tests:** Enforcement|Token|Ui filter 49 passed; `ui-tokens --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs` ok. Unity not run; `Loc.cs` not compiled (no editor).

**Owner: check in the editor**
- The five TTFs import as fonts and `-unity-font-definition: url("../Fonts/...ttf")` resolves from `Styles/Base.uss` (else make FontAssets: Create > UI Toolkit > Font Asset, dynamic, and point the USS at them).
- Cinzel/Inter/Fell appear on title, buttons and body; synthesised bold looks acceptable on Cinzel.
- `text-shadow` blur renders (wordmark glow, 60 px blur); if too heavy lower `size.wordmarkGlowBlur`.
- Text is now ~0.8x its old size (body 17 to 14, button 19 to 15): re-check clipping and row heights at 1280x720, 1920x1080 and 390x844. Card/intent/map font tokens were left alone for VP-3/4/10.
- Focus state: border is 2 px now (was 6) with parchment text on gold.

**Not done:** the other five lore faces, bevel/inset button shading and the modal drop shadow (9-slice, VP-11), uppercase classes applied to specific UXML (add `kit-upper` per screen), card face (VP-3).
## Core 2 WP-18: the deck editor session, its projection and Play in deck order (dev, 2026-10-24)

The rules half of WP-18 (D-190 to D-194) was already in; this finishes the work package: the engine-facing half of the web's `DeckEditorModel.js`, the Application door on `Core2RunSession`, and Play in deck order reaching the fight.

**Landed**
- **Play in deck order (D-400):** `createRunCombat`'s `orderedDraw` for a Core 2 fight: the deck as listed (Innate first), no `shuffle` value consumed, the order on the fight, the discard returned in deck order by `Actions.DrawCards`; the round refresh still shuffles, as in the web. Oracle: 72 fights x 24 steps (plays, drains, draws, end turns).
- **`DeckEditor` (D-401):** `openDeckEdit` (add by collection key, remove, place, undo, confirm, cancel), `deckEditorModel` (counter, Done, cost curve, deck rows, collection tiles with their refusals, filter chips, sorts), `deckEditorDoors`; refusals carry the sentence and the string key behind it; `LocaleCompare` stands in for the browser's collation. The web's `deckEditor.*` sentences are in `strings/app.en.json` (96 added).
- **`Core2RunSession` (D-402):** `DeckEditorRefusal()`, `OpenDeckEditor(strings)` (`Core2DeckEditor`: Confirm saves the slot, Cancel writes nothing), `CommitDeckDraft(draft)` (atomic, the web's sentences). New codegen sets: `Core2DeckEditorValues`, `Core2DeckText`, `Core2DeckTextFormats`, `Core2DeckEditorNumbers`.
- **Oracle (D-403):** `Tools/oracle-core2-deck.mjs` now also writes 12 editor sessions (4 classes x 3 seeds x 55 steps), the collation cases and the ordered fights.
- **No screen (D-404):** nothing to extend (the 405 has none either); the seam and what a Core 2 screen needs are in the decision.

**Tests** (targeted, with `Enforcement`): `Core2DeckParityTests` 59, `Core2DeckOrderParityTests` 74, `Core2DeckEditorParityTests` 14 (12 sessions, collation, presence), `Core2DeckEditorSessionTests` 8; with `Core2CombatStart*` and `Enforcement` 490/490. `codegen --check`, `refresh-manifest --check`, `check-content` and `check-docs` green. The full suite was not run.

**Not verified / open**
- `transform-content.mjs --check` needs `Tools/export/raw` (absent in this container); nothing it reads changed (`strings/app.en.json` and `rules/core2Deck.json` are hand-written).
- No Unity editor: nothing in Presentation changed.
- A host crash of `dotnet test --no-build` seen after incremental builds (not reproduced on a clean build or with `--blame`); not a test failure.
- Deferred: the row's `cardCategory`, `nextFilterPreset`, `rankZeroLabelId`; the Armoury's equipment editing; a player control for the deck rows (WP-20); a screen (D-404).

**Next:** WP-19 (run loop glue) can call `DeckEditorRefusal()` for the Armoury door; WP-20 puts the deck settings behind `settings/webKeyMap.json`.
## Core 2 WP-15: rewards and the combat end (dev, 2026-10-24)

**Landed** (decisions D-316 to D-323)
- **Rewards (D-316):** `Core2Rewards` is `model/gameProfileRewards.js`: the offer (pools from the character's eligibility, the stance reserve, the 60/10/30 groups, `lowerRankChance`, all on `cardRewards`), `Claim`, `ClaimToDeck` and the Add to deck refusals (`notCard`, `full`, `limit`) in the web's words; `Core2RewardPolicy` (`rewardGroupProblems`, `pickRewardCategory`) and `Core2StanceCoverage` (`reserveStanceCards`). A claim works on the run and restores it on a refusal instead of cloning the run (its pinned profile is megabytes).
- **Skills and the combat end (D-318, D-319):** `Core2Skills` (tracks, curve with the level cap, awards without draft queues, the class award, ability tracks opening at level 1, level notices) and `Core2CombatEnd.Settle`, a line-for-line `onCombatEnd` for a configured run: the fight is written back, the skill and class tracks are paid once (a death pays too), cinders, a boss's record, the last boss, the persisted offer.
- **The door (D-320):** `Core2RewardDoor` (`gameProfilePendingReward` beside the stored offers) and, on `Core2RunSession`, `AcknowledgeProgression`, `TakeReward`, `AddRewardToDeck`, `RewardDeckRefusal`, `LeaveRewards`; a won fight leads to `Location = rewards`, the door resumes from a save, the next act starts when a boss's spoils are left. `Core2RewardsView` is the display data a spoils screen reads. The map's planned-door notice and `DismissPort` are the seam until that screen exists (UXML/USS not written: nothing can be looked at without the editor).
- **Found by the oracle:** a Core 2 run's `collection` is deck + sideboard (D-317); a boss's HP and damage are scaled by the tier it is met at (D-321); a first XP of Spellcraft/maneuvers opens the track at level 1 (D-318).
- **Data:** `rules/core2Rewards.json` (+ schema), codegen fragment `core2-rewards.json` (`Core2RewardsKeys/Values/Messages`), `core2.port.rewards.*` strings, `Core2Registries.ToCoreRewardsData`, `Core2Content.Rewards`.

**Oracle** `Tools/oracle-core2-rewards.mjs` (D-322): units, 32 claim scenarios (448 offers, 290 takes + 207 adds, 483 refusals), 32 climbs (130 fights: 106 won, 24 lost, 23 bosses, 20 elites, 25 with level notices, 14 acts advanced), 5.7 MB under `Tests/Oracle/core2/rewards`; two runs are byte-identical.

**Tests:** `Core2RewardsParityTests` (71: 6 unit tests incl. the door's steps for every kind of door, 32 claim scenarios, 32 climbs, the oracle check), `Core2RewardsSessionTests` (9), `Core2RunSessionTests` updated for the door (10), `Core2DeckParityTests` + `Core2RunParityTests` (388), `Core2ScreensTests`, `Core2CombatFlowTests`, `EnforcementTests` all green; `codegen --check`, `transform-content --check`, `refresh-manifest --check`, `ci/check-content`, `ci/check-docs` ok. The full suite was not run (80 minutes; the shared memory limit kills the host when workers overlap, D-323).

**Oracle fix (D-324):** the shared `createFight` passed no `combatMods` to `createRunCombat`, so the start and session fixtures held unscaled bosses (start: 120 HP, Unity 96 after D-321). The oracle now applies main.js `combatMods` like `enterCombat`; `start` and `session` regenerated, only boss fights differ (checked structurally). Core2CombatStart 328/328, Core2SessionParity green.

**Not done / deferred**
- The spoils screen (Take, Add to deck, Continue and the level-notice modal on `Core2RewardsView`); `ActMapScreen` shows the planned-door notice and leaving it leaves the spoils untaken. Not compiled or looked at in the editor.
- Shop stock (`itemsPerCategory`, `shop` stream): WP-17 extends `Core2Rewards.MakeOffer`.
- After-combat recovery percentages and the profile bank of enemy knowledge (WP-20 / a Core run carries none).
- The `limit` refusal of Add to deck is not reached in play (the deck oracle holds the copy limit).

## Core 2 WP-C1b: the Unity screens play Core 2 fights (dev, 2026-10-24)

**Landed**
- **Run screens over `IClimb` (D-310):** the act map, run HUD, run end, pause, music and sfx read either ruleset; `MapNodeView.Col` is a double; a place not yet ported shows a "not yet available in this build" notice with Continue (`Core2PortView`); a Core 2 death/victory reaches W-15 with the class it opened.
- **`Core2CombatFlow` + `Core2CombatText` (D-311):** the W-07 logic and words, engine-free. Stance, target (hit %, ghost HP, receipt), route (odds) asked only when the legal plays differ; reaction offer and card choice as required prompts; flasks, equipment swap/change, Perception predict, recover, reactions switch, End Turn refusal; pacing from `CombatPacing`. A throwing intent is caught, reported to the screen (logged) and the view rebuilt; the enemy-turn lock is dropped in `Unlock()` (manager note on the web freeze).
- **Screens:** `Fighter` kit element (D-315), `core2Combat` (UXML/USS, `Core2CombatScreen`), `ActMapScreen` shared, W-04 classes per ruleset (`CreationClasses`, D-312), Continue/Load through `RunHost.Load` (formats 2 and 3), New Game through `RunHost.Start`.
- **Data:** `newRun.ruleset` is `core2` (D-313; 405 saves still load; `RunFlow.RulesetOverride` pins `legacy` in the 405 PlayMode smokes); `core2Screens`, `ui/screens.json` `core2Combat` (built), `ui/components.json` `fighter`/`combat.logLines`, tokens, `core2.*` strings, codegen fragment `core2-ui.json`.
- **Tests:** `Core2CombatFlowTests` (13: 8 bot fights through the controls, texts, refusals, stance step by step, reaction offer, illegal target, throwing intent), `Core2ScreensTests` (9), PlayMode `Core2FightSmokeTests` (keyboard/pad Core 2 fight, save-and-quit resume, defeat to W-15).

**Compiled (D-314):** Presentation, Platform and the PlayMode tests were compiled for real with `csc` (C# 9, net48) against the `UnityEngine.Modules` 2021.3.33 reference assemblies from NuGet plus stubs for Unity 6-only types (`UxmlElement`, `UxmlAttribute`, `Painter2D`, `Unity.Properties`) and the Input System; zero errors. It found and fixed `FocusController.IgnoreEvent` (not an API). **Not run:** nothing in Unity (UXML/USS parsing, layout, focus walking, UxmlElement generation, the PlayMode smokes).

**Tests** (`Category!=Bot`): 14065 passed, 0 failed (1 h 13 m); all node checks ok

**Owner: check in the editor**
- Core 2 New Game end to end (title, slot, W-04 with three locked classes, map, fight); Continue mid-fight and at the map.
- W-07 layout at 1920x1080, compact and narrow: Fighter rating rows, prompt panel overlap with the hand, combat log, footer tools.
- Focus/keyboard: arrows over hand/enemies/tools; prompt panel options; Predict buttons; ScrollView option list focus.
- View build cost per press on Mono/IL2CPP (the legal list is 0.2-0.8 s on desktop .NET 8); enemy-turn pacing (stage gap + animation step).
- `Core2FightSmokeTests` and the 405 smokes (now pinned to `legacy`).

**Not done / deferred:** formation moves, an auto-combat bot, pile viewer, deck editor and Armoury (planned screens), rewards/rest/shop/event (WP-15 to WP-19), a Core 2 capture fixture.

## Core 2 WP-C1a: whole-fight parity through the session, a hosted Core 2 run, and the combat view (dev, 2026-10-24)

**Landed**
- **Legal-intent parity (D-300, D-307):** `Core2CombatSession.LegalIntents(settings)` is the web player's list at every step, candidate for candidate and in order: offer options and Back, card-choice selections, every card with its stance choices and (when its printed face aims) every target, each with the non-advance routes of its own preview, `endTurn`, `useFlask` (carried flasks, aimed at each enemy when targeted, then the `hp`/`mana` charge pools), `setReactions`, `predictIntent`, `moveCharacter` (under the player settings handed in) and `recoverControl`. **The known deviation is settled in favour of the session:** the web UI offers a throw when the default advance is too dear (`CombatRangeActionModel.canChooseRangeRoute`), so `legalIntents(full: true)` tries every route of the card's preview whether or not the advance was accepted; the bots keep their old list, their fixtures are unchanged. `Why(intent)` gives the rules' refusal.
- **Oracles:** `Tools/oracle-core2-session.mjs` (new): 4 classes x all 35 encounters x 2 seeds played to their end, per decision the legal list (compact keys, once per fight + indexes), then intent, events, delta, RNG counters and `runCombatEnd`'s write-back. `Tools/oracle-core2-remaining.mjs` now records the legal list too (flasks of the slots, second weapon sets, charges, choices, relics). `Tools/oracle-core2-host.mjs` (new): 32 Core 2 climbs through `enterNode` / `startFight` / `advanceAct`.
- **Whole fights through the session (D-301):** `Core2SessionParityTests`: 280 fights, 3,449 steps, `LegalIntents` and the step equal to the web at every decision, a third of the fights saved and reloaded half way (checkpoint, or a slot of the save service), the run after the fight equal to `runCombatEnd`.
- **Run hosting (D-302, D-303):** `Core2RunLoop` (Domain) and `Core2RunSession` / `Core2Content` / `RunHost` (Application): New (the run, the seat order, the first act's map), Travel, Execute with autosave, the fight's end written back (`CoreRunCombat.End`), defeat or the last act's boss closes the run into the profile (history row stating the profile, class ledger via `CoreRunRules.CompletedRun`), an earlier boss advances the act; rest, shop, treasure and event nodes and everything a won fight pays answer "not yet ported". Save format 3 with the profile pinned, resumes at the map and mid-fight; `RunHost.Start` / `Load` pick the ruleset from `rules/runFlow.json` `newRun.ruleset` and a slot's format. **The data switch stays on `legacy`**: a Core 2 run has no rewards, rest, shop or events until WP-15 to WP-19, so flipping it now would hand the player a run that cannot grow; flipping the word starts Core 2 runs (tested).
- **`Core2CombatView` (D-304):** fighters (HP, Block, Barrier, Poise/Ward meters, Evade/Precision, stance, Staggered, Counter, statuses, intent with the knowledge gate), the hand (costs, targets, choices, refusal, legal plays as intents, hit chance and damage receipt per target, range routes), the reaction offer (options, Back), the pending card choice/discard, flasks, equipment swap and re-arm, the reaction switch, end turn, the log lines (`combat.log.<type>` sentences added to `strings/combat.en.json`), the legal list.
- **Review fixes (D-305):** (1) `LegalIntents` includes `useFlask`, the parity test no longer tolerates its absence; (2) a refused intent no longer rebuilds the fight, the one rollback keeps the run's shared `Rng`; (3) `CoreTransaction` restores the queue a pause suspended (and the reaction resume queue); probes carry it too (`DetachPaused`).
- **Found by the new fixtures (D-307):** `CoreRangeLineage` read the range-trigger bound from the 405 engine file (no such section: NaN), so a card whose kill re-fires a relic trigger (Carrion Talon) recursed to a stack overflow when previewed. Fixed to read the Core 2 combat rules.

**Tests** (`Category!=Bot`): Domain.Tests (`Category!=Bot`): 14,044 total (baseline 13,356 + 688), 1 h 10 m. The full run had one failure, `ContentValidatorTests.CommittedContentIsValid` (the schema of `strings/combat.en.json` did not list the new `combat.log.*` lines); the schema was fixed after that run (data only, no code changed) and the content-sensitive classes (content, manifest, strings, schema, enforcement, settings, saves, climb, rewards, creation: 836 tests) were rerun green. New: `Core2SessionParityTests` (281), `Core2RemainingLegal` (340), `Core2HostParityTests` (33), `Core2RunSessionTests` (10), `Core2CombatViewTests` (33), `Core2CombatSessionTests` (12, three new: shared generator after a refusal, a refusal while an offer waits, the flasks / formation / selections). `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `ci/check-content`, `ci/check-docs`, `web-key-map --check` green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Application` and `Ashen.Content`, compiled and tested under dotnet only.
- The card face's damage is the engine's receipt of the card's first plain damage effect (formula amounts, charged effects and Counter returns show no number); the web's full `previewCard` values are not ported.

**Next:** the Unity UI step (the combat screen binds `Core2CombatView`, the title calls `RunHost`), then WP-15 (rewards and combat end), WP-16/17 (rest, shop), WP-18, WP-19 (the rest of the loop, then flip `newRun.ruleset`).

## Core 2 WP-C0: the remaining combat features, so a default Core 2 fight never meets a guard (dev, 2026-10-23)

**Inventory (`node Tools/core2-reachability.mjs [--check]`, profile `default` at web b8cfe4fac).** Every `NotSupported` / `DeferredOps` site of `Domain/Combat/Core*.cs`, whether a default run reaches it, and what was done:
| Guard | Reachable in a default run? | Outcome |
|---|---|---|
| `useFlask` (intent), flask `script` (`wondrousDraught`) | yes (7 flasks, 1 scripted) | **ported** (`CoreFlasks`, script as data in `core2Combat.json`, D-296) |
| `swapArmament`, `changeEquipment` | yes: rightHand and leftHand have 3 sets; the web does NOT refuse them in a Core fight (the task assumed it did) | **ported** (`CoreEquipment`, D-292) |
| card charges (damage, damageEffect, break), card choices | yes (`oathKept` on all 4 classes grants charges; 31 `grantCardCharge` rules) | **ported** (D-297); choices answered in every oracle |
| `cardPreparing` listeners and the preparing price guard | no profile card or relic is listened to (0 cards in 672 fights: every class x relic x weapon), but a rule can mount one | **ported** (`CorePreparing`, D-293); unit-tested by injecting a listener mount |
| `previewCard` hiding an unread enemy's stance, Counter and charging move | yes | **ported** (`CoreVisibility`, D-295) |
| `pass`, `basicAttack`, `unqueueCard`, `chooseBlightFeat` intents | `pass` yes; the others no | `pass` ported; the rest **refused in the web's words** (D-298) |
| `arcaneBuildup` (`DeferredOps`) | no: only the `resonance` orb rule uses it and the profile has no orb | refused, documented |
| Ashen Blight cost and milestones (`AshenBlightNotPorted` x3) | no: no profile card pays it (3 `blighted*` cards sit outside the profile) | refused |
| upcast reaction cards (`UpcastNotPorted`) | no: no card ships `upcast` | refused |
| Combat Rounds (`CombatRoundsNotPorted`) | no: a setting no profile module turns on | refused |
| crit rules, combo hooks | no profile card carries `critRules` / `comboHook` | refused |
| revive token (`ReviveNotPorted`) | no: a run gains a consumable only off a market shelf and the profile's shops lay out none | refused |
| skill feats / companions / sigils (`CarriersNotPorted`) | no source in the profile | refused |
| legacy branches (block, avoidance, hit, knowledge, decay, break, stance, counter, ward), `HandRefreshNotPorted`, `ReactionRulesVersionOne` | no: fixCore 1, Ward off, `roundShuffle: entireDeck` | refused |
| `CardOutsideProfile` (card with no projection) | no | refused |

**Landed**
- **Oracle first (D-290):** `Tools/oracle-core2-remaining.mjs` is a free bot (no filter): any card with any target / choice / route, flasks (charge pools and slots), second weapon sets and carried pieces, swaps, `pass`, `setReactions`, every offer and choice; groups of cards, every relic alone, 4 classes x every encounter (about 340 fights, 6,800 steps). It also records the preparing units and the refusal probes. `Core2RemainingParityTests` replays it.
- **Ported:** `CoreFlasks`, `CoreEquipment`, `CorePreparing`, `CoreTargets`, `CoreVisibility`, `CoreSerial` (the JS NaN serial, D-291), `CoreTransaction` (the web's candidate-copy dispatch, D-294), `CoreRefusals`, the charge damage/break in the contact plan, the damage op and the Counter payload (D-297), `allAllies`, and the cost/price door `PlayRefusalFor` (price from the preview).
- **Fight oracles widened (D-299):** damage, matchups, statuses, enemy, reactions and range lost their event/op/card/enemy filters and the enemy staggering; a card choice or discard takes the first cards offered; `--why` tallies (`Tools/oracle-core2-why.mjs`), `rejections` in each index. All regenerated.

**Rejection tally (candidates the oracle turned away), before -> after:**
| Oracle | Before | After | What is left |
|---|---|---|---|
| damage | 2,683 (enemy ops 298; card gates 2,385) | 0 | |
| matchups | 660 | 6 | web refusals: combat over, an enemy already dead |
| statuses | 577 | 0 | |
| enemy | 449 (252+86 card gates) | 177 | web refusals: Stamina or Mana, Stamina to Advance, an unplayable Wound |
| reactions | 1,544 | 236 | web refusals: costs, Advance, unplayable Dazed / Wound |
| range | 4,152 | 1,974 | web refusals: costs, a tile already taken or on the wrong side, a control status, an unreachable route |
| remaining (free bot) | n/a | 0 feature rejections; the web's refusals are replayed as errors | |
Zero rejections are for a feature the port lacks; every remaining one is a refusal the web gives and the C# replays as the same error.

**Parity mismatches found, all fixed:** the web's `pendingAbilityPlay` was missing from the projection; the expansion serial is NaN in the web where C# said 1 (D-291); a friendly variant of a card whose printed face wants an enemy needs a target (D-293); `allAllies` had no target resolver; a deep refusal (Stamina to Advance mid-play) left C# half-mutated (D-294); charge damage/break were not added to the first hit or the Counter payload, `cardChargeConsumed` came after the stance; equipment: the Stamina charge precedes the pool move, the Poise vessel max, and a configured deck must not restamp a duplicate weapon plan (Rogue daggers threw).

**Tests:** `Core2RemainingParityTests` (fights, previews, preparing units, refusal probes), the widened `Core2FightParityTests`, `Core2MatchupParityTests`, `Core2StatusFightParityTests`, `Core2EnemyParityTests`, `Core2ReactionParityTests`, `Core2RangeParityTests`, and WP-13's `Core2SnapshotParityTests` now also replays the `remaining` fights from their own snapshots (340 cases; no `CoreSnapshot` change was needed: the new state lives in the combat fields it already carries). Domain.Tests (`Category!=Bot`): 13,343 total (baseline 11,546 + WP-13 + WP-C0), 47 min 55 s; the first run had 9 failures, all `Core2SnapshotShape_*` on a snapshot oracle recorded from the pre-widening fights; `node Tools/oracle-core2-snapshot.mjs` regenerated it and the 85 shape cases pass (data only, no code changed after that run). EnforcementTests green (7). `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Content` and `Ashen.Generated`, compiled under dotnet only.
- The refused features above stay refused until a profile can reach them: reachability is asserted by `Tools/core2-reachability.mjs --check`.

**Next:** WP-14 onward (the combat UI / run integration); a profile that turns on Combat Rounds, Ashen Blight cards, an orb or market shelves must first port the matching row of the table.

## Core 2 WP-13: the snapshot and the session of a Core 2 fight, proven by playing every recorded fight from its own snapshots (dev, 2026-10-23)

**Landed**
- **Snapshot (`CoreSnapshot`, D-280):** `Serialize(CombatState)` / `Restore(CoreCombatData, Rng, snapshot)` are `serializeCombatSnapshot` / `restoreCombatSnapshot` for a configured profile, in the web's shape: every Core field (`rangeState`, `formationPolicy`, `enemyKnowledge`, the rule snapshots, `reactionRulesVersion` / `reactionRules`, `reactionCursor`, `reactionHandCleanup`, `pendingReaction`, `reactionStack`, `enemyReactionRolls`, `reactionRootPlay`, `reactionResume`, `hiddenCards`, `proposalRewards`, the card choice left open with its `pendingAbilityPlay`), the fighters whole (persistent Poise and Ward, `barrier`, `combatStance`, `combatCounter`, `plannedMoves`, burden piles, statuses and meters), the piles, the event log, the trigger gates, the skill ledger and the id counter. A fight between two dispatches is data; a paused fight also writes its queued work (`abilityQueue`: `CoreSnapshotActions` turns an action, its meta, its committed-action group with the per-hit contact sequence and an enemy entry's plan into the web's JSON and back, joining the actions of one group by key as the web does). The profile body is a pin `{ id, hash }` and a restore under another hash is the recoverable `ProfileMismatch` (D-171 settled, D-280). `RngToJson` / `RngFromJson` carry all 25 stream counters (D-283).
- **Validation (`CoreSnapshotProblems`, D-282):** the field-addressed refusals of `combatSnapshotProblems` for a Core 2 fight (scalars, the ruleset-7 derived-stat receipt, fighters, piles, the choice, the reaction state, the engagement state, the formation, the knowledge ledger); `Restore` and `Serialize` both refuse by name (`SnapshotRefusedException`).
- **Session (`Application/Combat/Core2CombatSession`, D-283, D-284):** starts a fight from a run on the run's streams (`CoreRunCombat`, D-131), `Execute(intent)` through `CoreDispatch` with refusal-costs-nothing and rollback, `LegalIntents`, `PreviewCard` / `PreviewHit` / `CardCosts` / `PlayRefusal` / `PreviewEnemy`, `PendingOffer` / `PendingChoice`, `ReconcileKnowledge`; `Save` / `Commit` / `ExecuteAndLog` / `Load` through `SaveService` with the verified command log (D-017) and the profile pin; the Core 2 save is format 3, the 405 `saveFormat` 2 and its saves are untouched.
- **Oracle (`Tools/oracle-core2-snapshot.mjs`):** replays the recorded fights on the pinned web and records `serializeCombatSnapshot` at sampled steps (43 snapshots in 22 shape signatures, 2.4 MB; the web refuses to write 94 of the 3,245 early states it looked at).
- **Fix found by the shape test (D-286):** the round refresh's `deckShuffled` reason is `configuredRoundRefresh`.
- **Review fix (`Core2CombatSession.LegalIntents`):** card plays also try each non-advance `rangeRoute` of `CoreRangePreview.Card` (a throw is offered even when the advance is unaffordable) and `recoverControl` (one stack per held status, as `legalIntents` in `Tools/oracle-core2.mjs`) is enumerated and probed; 3 tests (recovery with/without Stamina, advance+throw / throw only, the web's per-step legal count over the 12 recorded card-bot fights up to the first `useFlask`, which this dispatch does not have yet).

**Tests**
- `Core2SnapshotParityTests`: clone-and-continue over all 969 recorded fights and exchanges (reactions, range, damage, matchups, statuses, enemy sets: the fight is its own snapshot after every step; the four replay loops take a `between` hook), the shape of 43 web snapshots, and the web's own snapshots restored and written back unchanged (43).
- `Core2SnapshotUnitTests` (33): a sound snapshot round trips, a resolving or queued-but-unpaused fight is not saved, 27 malformed snapshots are refused by name, another profile and an unknown stream are refused, a card choice left open (`base.exhaust-choose`) is saved with its paid card and the fight goes on from it.
- `Core2CombatSessionTests` (7): start on the run's streams, the recorded fight through the session (refusals cost nothing), save paused on an offer / reload / finish on the same state, the log replays on its checkpoint, a save the build cannot read is refused by name and kept, previews and legal intents, a fight played to its end.
- Domain.Tests (`Category!=Bot`) 12,642 (baseline 11,546 + 1,096), all green after the last code change (33 min 47 s). `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `ci/check-content`, `ci/check-docs` and `web-key-map --check` pass.

**Not verified / deferred**
- A reaction card that itself leaves a choice (`reactionResume` with its nested choice): the codec is the queue's and the validator checks it, but no recorded or unit fight pauses there (D-285).
- The deep receipt-graph validators for a paused range plan (D-282); flasks, swaps, charges, arcane buildup and Ashen Blight state (the other stream's) join the snapshot with their rules: any new `Fields` key a fight carries fails the clone-and-continue test until the snapshot carries it.
- No `RunSession` hosts the Core 2 session yet (the run flow, map and rewards are WP-14 and after); the 405 `CombatSession` is unchanged.

**Next:** WP-14 onward: the Core 2 run loop on `Core2CombatSession`.

## Core 2 WP-12: range, engagement and the burden cards in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-reactions.mjs --bot range`, `Tools/oracle-core2-range.mjs`, D-270):** `node Tools/oracle-core2-combat.mjs --bot range` writes `Tests/Oracle/core2/range/`: `fights-<class>.json` (4 classes x 12 fights, 725 steps: the bot plays attack, Smash, Sweep, Defend, Counter, Advance, Backstep and Closing Dodge cards by the route (advance or throw) each card offers, steps across its side of the field, answers every offer; the first eleven fights per class meet the encounters that start at a distance and the enemies that add a burden) and `exchanges.json` (328 exchanges, 1,243 steps: approach by band and route, Advance cards, Closing Dodge and Backstep as answers, enemies closing the distance on their own Stamina or with their closing move, one fight per burden enemy, the formation step, hooks that strike at bands 0 and 2 with the approach at 1 and 5 per band, a Counter whose reply cannot pay to advance, a Counter at a distance by each route, a Slimed, a Wound and a Dazed in hand). Every player decision records the range quote of each hand card against each target (`previewCard(...).range`, `previewCardHit(...).range`). `quotes.json` (604 units from `oracle-core2-range.mjs`): 352 quotes (preview and committed: plan, lineage graph, target and site batches, every RNG counter), 84 acceptances, 18 engagement moves, 150 formation plans.
- **Advance and closing moves (D-271):** `CoreRange.Accept` pays the Stamina to advance (a player's Actions, an enemy's pool; a short purse refuses), `CoreEnemyMoves.Execute` swaps a move the enemy cannot pay to reach for its `rangeClosingMove`, `moveEngagement` is a `CoreOps` opcode. The four delayed enemies that close with `advance` and every enemy that starts at a distance now fight.
- **The range lineage (D-272):** `CoreRangeLineage` is `combatRangeLineage.js`, wired into `Triggers` (`rangeOrigin` on events, inherited carriers and site batches on a hook's action, independent turn hooks quoted, paid or told as `combatRangeActionUnavailable`) and `CoreRange.Quote` (preview on a copy of the actions and the RNG; random targets are drawn once). Reaver and Rogue (relic hooks that strike) play and are offered their Counters; zealotry and constellation (`randomEnemy`) are in the recorded hook exchanges.
- **Previews and `moveCharacter` (D-273, D-275):** `CoreRange.PreviewRoutes`, `CoreRangePreview.Card` / `Hit`, `CoreHitRoll.Preview` reads the contact from the planner; `CoreFormation` and the `moveCharacter` intent.
- **Burden cards (D-274):** `CoreCards.Project` resolves a registry `status` card (Dazed, Slimed, Wound) with the face the web gives support cards; they are costed, drawn, discarded, exhausted and refused as unplayable like the web.
- **Guards removed:** `RangeAdvanceNotPorted` (two sites), `RandomTargetNotPorted`, `RangeLineageNotPorted`, `RangeContactNotPorted`, `NeedsLineage`, the `moveEngagement` entry of `CoreSets.DeferredOps`, `CardOutsideProfile` for a status card. **Fight oracles widened (D-276):** reactions, damage, matchups, statuses and enemy fights no longer hold back distance, hooks that strike, Counters of the hooked classes, replies that pay to advance or burden enemies (they were regenerated; the shared `reactionFilter` in `oracle-core2.mjs` lost its lineage and pays-to-advance tests). The damage, hit and receipt tests now assert the port's range contact equals the web's and compare applied hits after a lineage trigger in full.

**Parity mismatches found, all fixed:** the per-hit site batches were spread instead of wrapped (a one-element JArray built from a JArray copies its items), so no hook inherited its root and every trigger paid as an independent action (the expansion serial differed by one); the oracle's own: the bot's `moveCharacter` needs the settings that switch movement on, `Assert.Throws` was exact-typed (a refusal subclass), a drive that edited the fight after the record's start, `stamina` clashed with the view in the unit records, `energy` patches need the `stamina` twin the port reads.

**Tests:** `Core2RangeParityTests` (48 fights, 328 exchanges, 604 units, with the card previews of every step), the widened `Core2ReactionParityTests`, `Core2DamageParityTests`, `Core2HitParityTests`, `Core2MatchupParityTests`, `Core2StatusFightParityTests`, `Core2EnemyParityTests`. Domain.Tests (`Category!=Bot`) 11,546 (baseline 10,517 + 1,029), 23 min 20 s, all green after the last code change. `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Content` and `Ashen.Generated`, compiled under dotnet only.
- `previewCard` hides the stance, Counter and charging move of an enemy whose intent the player has not read; the port does not (no recorded set holds an enemy whose hidden stance changes a route row).
- The statuses scenarios still isolate a status from the engagement (`rangeState` removed); the statuses fights have the range in.
- Still outside every fight oracle, for reasons that are not range or burdens: Ashen Blight, charges and card choices, arcane buildup (`arcaneBuildup` stays in `CoreSets.DeferredOps`), flasks and swaps, legacy stances, a cardPreparing listener (`PreparingNotPorted`).
- In the reactions and range oracles (`--why`) no step is rejected for a range or burden reason; the only dry-run rejections are `Not enough Stamina`, `Not enough Stamina to Advance`, a route that cannot reach and the formation step's own refusals, all web refusals. The damage, matchups, statuses and enemy oracles do not tally their rejections; their range and burden tests are gone from the code.

**Next:** WP-13 (snapshot and session): the range state, the plans and lineages on carriers, `rangeLineagePlan` and the held batches must serialise.

## Core 2 WP-11: enemy AI, enemy reactions and enemy knowledge in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-enemy.mjs`, `Tools/oracle-core2-hook.mjs`, D-260):** the hook is a Node `module.register` loader that appends `__weightedMovePick`, `__rollIntents`, `__presentIntent`, `__buildIntent`, `__startDelayedMove`, `__executeMovePayload` and `__enemyPhase` to the source of the web's `engine/combat.js` at load time (the checkout on disk is never touched). Files under `Tests/Oracle/core2/enemy/`: `enemies.json` (all 33 enemies: the move table after projection, the plannedMoves pool, the SP of every move plain, Silenced and per stance, the expanded move, carrier, intent, `previewEnemyAction`, reaction rows, and 5 seeds x 200 weighted picks = 33,000 picks), `units.json` (reaction decisions, the knowledge ledger, intent reads, enemy gear mounts), `fights-<class>.json` (4 classes x 6 fights where enemies react and the player predicts) and `exchanges.json` (delayed, knight, duelist, predict, predictRefused, credit). Every step records the intent, the event types and payloads, the offer, the delta of the full state and every RNG counter; the run is deterministic.
- **The pick and the pool:** `CoreIntents.TakePlannedMove` is public and the weighted pick with the SP budget (stance costs, `maxConsecutive`, the plannedMoves pool) is held to the web draw for draw; the `intent.reads: 'exact'` branch is in `Present` and does not draw (D-265).
- **Delayed moves (D-261):** `CoreEnemyMoves.StartDelayed`, `pendingMove`, the resolving turn in `CoreTurns` (`EV.OutcomeCharging`), `whileCharging` effects and tags, ten shipped delayed moves (six are in a recorded fight; four close with `advance` and wait for WP-12).
- **Enemy reactions (D-262):** `CoreEnemyReactions` (`PlanAction`, `EnemyPlan`, `Push`, `Relink`, `Halt`, `Run`, `View`), `CoreReactions.DecideEnemyReaction` (public), `OpenEnemyWindow` with its cap, enemy entries in `OptionsFor`, `Resolve` (run, fizzle when the seat left), `Finish` clearing the plans, `DeferRootPlay` under an enemy window, `combatEnemyReactionStacked`. The two shipped reaction moves, `gildedKnight.gildedGuard` and `courtDuelist.bindingRiposte`, are both covered by exchanges including the staggered-fizzle case.
- **Enemy knowledge (D-263, D-264):** `CoreKnowledge` (`Initialize` from the snapshot, `Feedback`, `Predict` with the Perception DC and roll, `CreditResponse` / `CreditBenefit` / `CreditImpact`, `Reconcile`), `CoreKnowledgeLedger` (the Bestiary ledger and banking from a Ward reply, held by units), the `predictIntent` dispatch case, `meta.enemyKnowledgeActionSerial` on a Counter reply. The Ward-reply credit throw in `CoreMatchups` is gone.
- **Previews and mounts (D-266):** `CoreEnemyPreview` ports `previewEnemyAction` for every move (formulas evaluated, uncertain conditions, charging effects, `delayTurns`); enemy gear mounts through the property mounts (`MechanicTags`, `MechanicBindings`, `ConfiguredEnemy`).
- **Guards removed:** the four WP-11 message constants (enemy reactions, delayed moves, the enemyReaction stream, the Ward-reply credit); `deferRootPlay` / `restoreRootPlay` under an enemy window work.
- **Fight oracles widened (D-267):** hit, damage, matchups, statuses and reactions leave the delayed moves, enemy reactions and the `enemyReaction` draws inside the filter; `perceptionGained` is a hit event; `projectFull` records a held play by what identifies it and an enemy entry's plan compact. All five were regenerated.

**Parity mismatches found, all fixed:** the preview profile key (`profile`, not `combatProfile`); `whileCharging.cardTags` null versus absent; oracle value references mutated by later steps (the ledger log: `structuredClone` in `guarded`); `combat.__run` leaking into the projection (now non-enumerable); the held-play shape (`reactionRootPlay`, normalised); a frozen gear row in the mount unit (a clone of the profile); the hit oracle's avoidance unit recorded the knowledge action after the call instead of before; the reaction exchanges now carry `combatEnemyReactionStacked`, which the payload filter of `Core2ReactionParityTests` had not listed.

**Tests:** `Core2EnemyParityTests` (statics for 33 enemies, 33,000 weighted picks, reaction, ledger, read and mount units, 24 fights, the exchanges including 21 knight exchanges with enemy fizzles), plus the widened `Core2HitParityTests`, `Core2DamageParityTests`, `Core2MatchupParityTests`, `Core2StatusFightParityTests` and `Core2ReactionParityTests` (532 cases).

**Not verified / deferred (D-268)**
- Burden cards that enemy moves add (`dazed`, `slimed`, `wound`) are legacy-registry cards outside the profile: `CoreCosts.Resolve` refuses them (WP-5), so the enemies that add them are held by statics and not by a fight.
- Four delayed enemies close with `advance` (`moveEngagement`, `RangeAdvanceNotPorted`): WP-12.
- A configured run has no run-level knowledge ledger; the ledger is only unit-held.
- Snapshots of a paused fight with enemy entries: WP-13.

**Next:** WP-12 (range and engagement: `advance`, `moveEngagement`, the range lineage).

## Core 2 WP-10: the player's reaction stack in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-reactions.mjs`, `--bot reactions`, D-256):** drives the SHIPPED `combatReactions.js` (`beforeReactionAction`, `optionsFor`, `resolveReactionStack`, `answerReaction`, `restoreReactionQueue`, `finishReactions`), `combatMatchups.js` (`counterCoversAttack`), `combat.dispatch` (`chooseReaction`, `setReactions`, the enemy phase resumed from `reactionCursor`) and `autoCombatPolicy.js` at `b8cfe4fac` on real Core 2 fights. `oracle-core2-combat.mjs --bot reactions` runs it. Files under `Tests/Oracle/core2/reactions/`: `fights-<class>.json` (4 classes x 8 fights: a bot that plays attack, Smash, Sweep, Defend and Counter cards from a deck given six extra, ends the turn and answers every offer from its own LCG, half Play the first option, half Back; every fourth fight has no per-hit policy, so the player stacks cards and the stack is seen at rest), `exchanges.json` (declared exchanges found by seed search: Defend and Counter against every maneuver, several hits, the switch off then on, no Stamina, no card, a locked hand, a Poise on the edge, two attackers, a lethal hit, the player's attack into an enemy Counter whose reply is offered a Defend, stacks of several cards, a Counter that pays to reach an attacker at a distance) and `units.json` (400 cases of auto-combat's chooser). Per step: the intent, the offer the step paused on (id, group, owner, source and every option with id, card, price, play), the full combat delta (the paused state included), the event types, the payload of every reaction and Counter event and every RNG counter.
- **Offers and the stack (`CoreReactions`, D-250, D-251):** `Before` (windows, eligibility by `trait:reaction` and coverage, the receipts `combatReactionUnavailable` with the reason of the candidate that got furthest), `OptionsFor` (every candidate played on `CombatState.Detach()`, then the stacked entries after it), `Answer` (Play / Back, validated against a fresh offer), `Resolve` / `PlayStackEntry` (LIFO, the interrupted queue held in `reactionResume`, fizzles), `RestoreReactionQueue`, `DeferRootPlay`, `SetReactions`, `Finish`. `PlayRefusalException` carries the reason and amounts of a refused play.
- **The enemy phase resumes (`CoreTurns`, `CoreDispatch`, D-252):** `reactionCursor` `{ index, stage }`, `Paused`, `Resume`, the hand dealt with after the enemies, and the one door `CoreDispatch.Dispatch` (`playCard`, `endTurn`, `chooseReaction`, `setReactions`, `chooseCards`, `chooseDiscard`, `recoverControl`).
- **The reply the player pays for (D-253):** `CoreCounterReturn.PaidRange` / `AuthorizeRange` (a bound reservation) / `DiscardReservation`, `CoreRange.IncomingScope`, `RangeContext` / `RangeIncoming`, `manualCounterSerials` on the root.
- **Guards removed or narrowed:** `ReactionsNotPorted`, `PaidCounterNotPorted` (gone), the blanket range-lineage guard (now `NeedsLineage`, D-254), the enemy-window guard (now the decision, D-255), `CombatRoundsNotPorted` (re-worded: not part of a Core 2 run, WP-21).
- **Fight oracles widened (D-256):** the damage, matchups and statuses fight oracles leave the reactions on and answer offers with the same bot; their records now include offers.
- **Auto-combat (D-257):** `AutoReactionPolicy`.

**Parity mismatches found, all fixed:** the first full run of the fights failed in three places. (1) The range-lineage guard refused the Defend candidate of the Reaver and the Rogue, whose class passives hold a hook that strikes: the web reserves a causal envelope only when such a hook listens for an event the quoted actions can cause, and a Block causes none (`NeedsLineage`, D-254). (2) The enemy-window guard refused a fight that merely contains an enemy with a reaction move: the window is a decision, and only an enemy that would react (or whose chance would be drawn) is the enemy package's (D-255). (3) The port's own: the enemy phase resumed with a cursor object that Newtonsoft had cloned when it was handed to the state again, so the index never advanced on resume. The oracle's own: the filter had to learn that an offer is outside what the port covers when a hand card needs a lineage or listens for being prepared, that an enemy reaction may draw on the `enemyReaction` stream, and that a reply may pay Stamina to advance; the Reaver and the Rogue lose their Counters in these fights (a Counter's reply is the lineage), and the multi-hit exchanges needed the move's `damage` / `hits` (not an effect row).

**Tests:** `Core2ReactionParityTests` (32 fights / 733 steps, 95 exchanges / 273 steps, 400 auto-combat cases: 246 offers, 114 played, 132 declined, 86 unavailable receipts, stacks up to depth 4, a root with several hits answered hit by hit, the player's attack into an enemy Counter answered on the player's turn, 24 of them), `Core2ReactionUnitTests` (the switch, the answer without an offer, the deferred root play, the held queue), 54 `paidAuthorize` units in `Core2MatchupParityTests` (the reply a Counter pays for, advance 0 to 2, both routes), and the widened fight oracles: the damage fights now hold 96 offers, the matchups fights 36 and the statuses fights 200, answered, all played through `CoreDispatch`. Domain.Tests (`Category!=Bot`) 9,880 (baseline 9,292 + 588), all green after the last code change (20 min 53 s). `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Content` and `Ashen.Generated`, compiled under dotnet only.
- Refused by name: an enemy that would react to a card or a reaction, or whose chance is drawn, and enemy stack entries (WP-11); a Counter reply or a card whose play needs a range lineage, so the Reaver and the Rogue (hooks that strike) are offered their Defends but not their Counters (WP-12); a reply that pays Stamina on its own to advance, a closing move (WP-12); a card that listens for being prepared; an upcast reaction card (none ships).
- Not ported: the intent preview of an offer and an option's range quote (UI), the snapshot of a paused fight (WP-13), reaction rules version 1 (not part of a Core 2 run).
- `deferRootPlay` / `restoreRootPlay` run only for a player-card root under an enemy window (WP-11): they are held by units, not by a recorded fight.

**Next:** WP-11 (enemy AI, enemy reactions, knowledge) decides the windows this package already opens and the enemy entries it refuses.

## Core 2 WP-9: statuses, status control, card traits and the remaining effect ops in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-statuses.mjs`, D-240):** drives the SHIPPED `statuses.js`, `combatStatusControl.js`, `triggers.js`, `actions.js` (`executeAction`), `fixCoreMechanics.js` (`fixCoreStatusDefinition`), `combatCardTraitMath.js`, `traitProjection.js`, `combatMatchups.js` (the trait guard) and `combat.dispatch` at `b8cfe4fac` on real Core 2 fights. Files under `Tests/Oracle/core2/statuses/`: `defs.json` (all 101 statuses: the definition with the FixCore override, and getMult / getAdd / getFlag / getCap of an owner with 1, 2 and 3 stacks), `scenarios-N.json` (apply / stack / decay / hook grids for every status on the player and an enemy, the event grid for the hooked ones, the pressure / recovery / lock / Sleep / Prone / Frozen / Reveal / Grounded / Ward / Poise control scenarios, every ported effect op with its variants, the Defend trait guard), `traits.json` (the trait projection of every card under two rule sets and the trait math tables) and `fights-<class>.json` (4 classes x 24 fights: statuses laid on the fighters, the player playing the cards of its hand that stay inside the filter, answering their choices and recovering from control, the enemies taking their turns; per step the full combat delta and the event types).
- **Statuses (`CoreStatuses`, `CoreStatusControl`, `CoreStatusHooks`, D-241):** `Definition` (the FixCore override and the text facts), `Apply` (stack modes, resists, procs, meters, the `statusApplied` receipt), `Remove`, `DecayAtTurnStart/End`, the modifier reads, the build-up gauges and their fills (Ward, Poise strain), recovery rolls, Sleep, Prone, Frozen, the locks and their recovery, action loss, the Defend trait guard.
- **Card play (`CoreCardPlay`, `CoreVariants`, `CoreRiders`, `CoreCardZones`, D-242):** the card's choice, effect variant, Health payments, charges (matched, consumed, attached to the first effect that can use them), the status plan (extra weapon / shield, taxes, restrictions), `finishAbilityPlay` with pending zone and discard choices, `chooseCards`, `chooseDiscard` and `recoverControl` entry points; turn-start return of hidden cards, retained Block, the proposal turn rules, turn-end-in-hand effects, retain through `proposalChanges`.
- **Ops (`CoreOps`, `CoreBurdens`, D-243):** buildup, heal, addCard, gainPoise / gainWard, restorePoise, wardDamage, grantCardCharge, castLinkedSpell, grantRollMode, retain, modifyCards / moveCards, discard, exhaust, loseHp (offering, nonlethal), revealIntent, stagger, addCinders, addBurden and the enemy burden turn rules, dodgeRoll, enterStance by maneuver. `moveEngagement` and `arcaneBuildup` stay guarded (WP-12).
- **Traits (`CoreCardTraits`, `CoreFormulas`, D-244):** the projection, Counterstrike scaling, Impact and Flux on a completed card, the `metric` and `traitBonus` formulas. Shipped Core 2 cards carry no trait values (the configured projection skips the projection), so execution is covered by the math tables and the guard scenarios.
- **Fixes the oracle forced (D-245):** a status rule's follow-up inherits the hit's contact (no second roll), `triggeredBy` on events, a cardless guard is whole Block, a physical fill strains the Poise meter, `attackHitPreview` answers nothing for a creature acting on its own side before it reads the engagement.

**Tests:** the Domain suite (`Category!=Bot`) runs 9,292 tests, all green after the magic-number fix (zone caps moved to `Core2StatusNumbers.ZoneCountMax` / `TurnsMax`); `Core2StatusParityTests` (definitions, scenarios, traits) and `Core2StatusFightParityTests` (fights) are the new suites; the Core 2 fight, damage and cost parity suites stay green.

**Guards that remain:** `moveEngagement`, `arcaneBuildup`, `script` (flasks), legacy stances (`stance:` ids and `classStance` choices), Counter / Smash (WP-8), reactions (WP-10), range lineage and closing moves (WP-12), Ashen Blight milestones.

## Core 2 WP-8: Counter, Smash and Sweep in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-matchups.mjs`, D-234):** drives the SHIPPED `combatMatchups.js` (`armCombatCounter`, `preparedCounter`, `counterCoverageMatches`, `previewTacticalAction`, `completeTacticalAction`, `stancePressureMultiplier`, `decayStanceRoundStart`), `counterRule.js`, `counterReturn.js`, `combatCardTactics.js` (`prepareTacticalCard`), `combatEngagement.js` (`authorizeCounterRange`) and `combat.dispatch` at `b8cfe4fac` on real Core 2 fights, with the WP-7 filter widened (the Counter events, `poiseDamage` / `wardDamage` cards, enemy Counter moves). Three records under `Tests/Oracle/core2/matchups/` (3.2 MB, two runs byte-identical): `fights-<class>.json` (24 fights, 388 steps: a bot that plays attack, Smash, Sweep and Counter cards from a deck given five extra of them, then ends the turn; Counters armed 62 times, consumed and answered 35), `exchanges.json` (41 exchanges, 139 steps, found by a deterministic seed search: a Counter card against each enemy maneuver, each attack card into an enemy Counter, a Poise break one point away on either side of a Counter, two attackers against one Counter, a Smash against a deep-Poise caster) and `units.json` (952 units: Counter arithmetic, coverage, the Counter cards, arming, the receipt a Counter gives, the stance pressure grid, the reply's Accuracy check on scripted dice, `authorizeCounterRange`, the Guard break, the decay, `wardDamage`). Every step records the combat delta, the event types, the payload of each Counter event and every RNG counter.
- **The Counter (`CoreMatchups`, `CoreCounterReturn`, D-230..D-232):** `PreparedCounter`, `CounterCoverageMatches`, `CounterCoversAttack`, `IsSpellCounter`, `ArmsAsCoreCounter`, the receipt's coverage and the Counter spent by an attack that is not avoided (`CoverContacts`, `ConsumeCounter`); `PrepareCounter` (arming from a Counter card), the Block bonus before the hit is absorbed, the reply (`Complete`: half the incoming damage, Poise worth a fifth, the advantage shares, one reply per owner cycle, the covered hit that breaks Poise still answers), `AuthorizeRange` (the reply priced by the Counter's own reach, the incoming distance as its context), `AttackPreview` / `ResolveAttack` (`counterAttackPreview`, `resolveCounterAttack`), the arithmetic (`Of`, `ForReceipt`, `BlockBonus`, `BlockGrant`, `Spent`, `PaidContact`).
- **Smash, Sweep, pressure (D-233):** `SplitSmashPressure` (a Smash against a caster pools its Poise and Ward and splits them), `RunWardDamage` (Ward is off: it drains Poise), `IsSmashGuardBreak` / `CompleteGuardBreak` (inert in a Core fight, a unit), `DecayStanceRoundStart` (a FixCore fight never calls it; a unit). `stancePressureMultiplier` (WP-7, `CoreTactical`) is now covered by 198 units.
- **Call sites (surgical):** `CoreTactical.Preview/Begin/Complete` (three calls), `CoreImpact.ApplyProfileDamage` (the Counter's Block), `CoreActions` (the reply's guard, `wardDamage`, the Smash split), `CoreExpansion` (a reply is the one reaction carrier it accepts, the Guard break of a dead target, a contact's generated actions go to the front of the queue, the incoming contact id), `CoreRange` (`ActionPlan` / `Quote` take a context, `Accept` takes the quote a reply carries), `CoreReactions` (a reply is announced when its first action starts), `CoreGroups` (two fields), `CoreCardPlay.Play` (a Counter arms and plays only its support), `CoreSets.CardOps`, `CoreCounters.Arm` (the full event payload).
- **Data:** `matchups.counterDefaults` in `rules/core2Combat.json` (+ schema), codegen fragment `core2-matchups.json` (`Core2MatchupKeys`, `Values`, `Messages`, `Numbers`); the messages of the removed guards are gone, and the synthetic Block message now names WP-9.

**Parity mismatches found, all fixed:** the first full run of the fights failed in the event order only: the reply is announced (`combatCounterTriggered`) when its first action starts, not when it is queued; a per-hit contact's generated actions go to the front of the queue before the next incoming contact; `combatCounterArmed` carried only the source and mode (WP-4 compared types; the web carries the event carrier); a Counter plans its target by the support that resolves now (`immediateCardEffects`), where the port gave a Counter with a `damage` effect the first enemy as its target; the web's `{ ...decoration, ...payload }` lets an undefined `cardId` of an enemy's Counter carrier override the executing card's (the port holds null there); the reply's authorization carries `lineage: null`. The oracle's own: a card must pass `playable` in an exchange as in a fight (the Reaver's mounted hook strikes, which the range quote refuses), only the profile's cards are in a Core 2 run, and a Smash only meets a spell stance when the Poise pool survives the first hit.

**Tests:** `Core2MatchupParityTests` 1018 (the oracle is present, 24 fights, 41 exchanges, 952 units). Domain.Tests (`Category!=Bot`) 7539/7539 (baseline 6521 + 1018). `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Content` and `Ashen.Generated`, compiled under dotnet only.
- Refused by name (the oracle's filter keeps them out of the record): every player Counter but `core.counter` (all carry Counterstrike, Impact or Flux, WP-9) and the Smash and Sweep cards that carry Impact; a reply the player pays for and a Counter against a player's reaction offer (WP-10); an enemy that holds a reaction move, banking an enemy's knowledge from a Ward reply (WP-11); a reply or an attack that needs its owner to pay Stamina to advance, the lineage of a hook that strikes, so the Rogue and the Reaver (a mounted hook) play no card in the record (WP-12).
- Not ported: `counterPreviewAgainst` (the card face's preview of what a Counter would return, the UI work package), the own-pressure book the traits read (WP-9), the Lightning thaw of a legacy preview.
- A Guard break and `decayStanceRoundStart` happen in no Core 2 fight (the receipt carries no `smash` flag, the web does not call the decay under FixCore): they are units.
- `combatRangeReturnUnavailable` happens in no recorded fight (an attack that reached its target has already closed the distance the reply needs); the 24 units that force it are compared.

**Next:** WP-9 (statuses and card traits) removes the trait refusals and finishes the Counter cards (Counterstrike, Impact, Flux); WP-10 plays the player's reactions through the same reply.

## Core 2 WP-7: damage receipt, protections and the fight wiring in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-damage.mjs`, D-228):** drives the SHIPPED `gameProfileCombat.profileDamageReceipt`, `actions.js` (`applyAttackDamage`, `drainFixCorePoise`, `fixCoreStagger`, `gainBlock`, `gainBarrier`, `dealPoiseDamage`), `fixCoreMechanics.js`, `mechanicsKernel.js`, `stanceMitigation.js`, the mechanic runtime and, for the fights, `combat.dispatch` at `b8cfe4fac` on real Core 2 fights. Four kinds of file under `Tests/Oracle/core2/damage/` (9.8 MB, two runs byte-identical): `receipts-N.json` (595 grid cases: both directions, 4 classes, attributes, ratings with per-piece sources, stances, Vulnerable / Weak, Block / Barrier / Poise / Ward, rule variants; each with every field of the receipt including `sources[]` and the same attack applied on scripted dice, a hit and a miss), `units.json` (160 units: protection receipt, stance mitigation, FixCore rules with mechanic attachments, Poise drain, the Staggered stance, `gainBlock` / `gainBarrier`, `dealPoiseDamage`; 27 kernel units on inline mechanics), `mechanics.json` (every `engine/mechanics/*/cases.json`, 56 cases, replayed on the web's own runtime) and `fights-<class>.json` (128 fights, 1516 steps: the enemies taking their turns, and the player playing attack and Block cards; per step the full combat delta, the event types and every RNG counter).
- **The damage chain (`CoreDamage`, `CoreImpact`, D-221, D-222):** AR / PR x `attackRatingWeight` with per-source lines, Weak, matching stance, Vulnerable, the proposal status rules, the stance advantage (1.25) or Smash against a defensive stance (x2, Block and Poise shares), the defence mitigation, floors at each stage, the physical / magic split, Block, Barrier, Poise (25% of what Block let through) and Ward (Ward off: Poise), Poise drain as prevented damage, a Poise break (Staggered stance, -1 Stamina, +1 Vulnerable), Block restoring Poise, `gainBlock` / `gainBarrier` with their events, `dealPoiseDamage`. `CoreFixCore` resolves the protections through the registered mechanics.
- **The mechanics kernel subset (`CoreMechanicsKernel`, `CoreMechanicsRuntime`, D-220):** `compileBindings`, `queryBinding('parameters')`, `evaluateModifiers` and the case runtime, data-driven over `rules/core2Mechanics.json`, `rules/core2CombatRules.json` and the `mechanics` / `damage` sections of `rules/core2Combat.json`; every shipped `cases.json` is a test.
- **The wiring (D-223 to D-227):** `CoreExpansion` / `CoreTactical` / `CoreGroups` (a card or move as one committed action: contacts planned, per-hit units, the receipt, the one d20), `CoreRange` (the action quote and the hit contact), `CoreAttack`, `CoreEnemyMoves` and the enemy phase in `CoreTurns` (the moves, the planned follow-ups, `followTrace`), `CoreReactions` (the windows the switch can answer), `CoreEvents` (payload decoration, ability riders, knowledge settlement), `SkillXp` Core branch, `CoreCardPlay.Play` (the whole play of a card made of damage, Block and Barrier). `RecordSkillXp` starts with the first dispatch.
- **Data:** `rules/core2CombatRules.json` (new, from `content/combatRules.js`), `mechanics` and `damage` sections of `rules/core2Combat.json` (+ schemas), codegen fragment `core2-damage.json` (`Core2DamageKeys`, `Values`, `Messages`, `Numbers`). The not-ported messages of `core2-combat.json` now name the right package (they said WP-5 / WP-6 / WP-7 for work that belongs to WP-8, WP-9, WP-11 and WP-12); four unused ones are gone.

**Parity mismatches found, all fixed:** the web key `weakARMultiplier` against WP-4's `weakArMultiplier` (every receipt was NaN); `energy` / `energyMax` absent from the compared player; the lineage of a hook on a hit (the range lineage is WP-12, so the receipt cases flag it and the test compares only what the hit returned); a restore against a clamped pool pays the hidden `missing` first; `blockGained` carries the card fields; `sourceHand` / `grantedBy` are absent, not null, on a card without them; `fixCoreStagger`'s `cause` was not recorded by the oracle; `finishCombat` clears the reaction fields and sets `pendingExpansionActions` to 0; the abilityRiders count Health lost on every creature; the knowledge action is executed when `enemyMoveStarted` fires and a miss against Evade pays Perception (the WP-6 hit tests now compare only the events a hit owns, as their oracle records them); `doPlayCard` enters the stance twice; the Core skill XP credits the group of `usedArmamentId`; `Triggers.EmitEvent` decorates the payload with the executing action.

**Tests:** `Core2DamageParityTests` (receipt grid, applied hit and miss, units, kernel units), `Core2MechanicCasesTests` (every `cases.json`, the kernel units), `Core2FightParityTests` (128 fights, 1516 steps). 1564 new; Domain.Tests (`Category!=Bot`) RESULT. `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Content` and `Ashen.Generated`, compiled under dotnet only.
- Refused by name, and kept out of the recorded fights by the oracle's filter (D-228): a prepared Counter and the Counter return, a Smash against a spell stance (WP-8); reaction offers (WP-10); enemy reactions, delayed moves, banking an enemy's knowledge (WP-11); paying Stamina to advance, the range lineage of a hook, a random target (WP-12); card traits, charges, choices, variants, `buildup`, `heal`, `addCard`, status control, Sleep and Ward damage (WP-9); critical hits and Ashen Blight are not part of a Core 2 run.
- Event payloads that no state or rule reads are not built: `abilityBefore` / `abilityEntities`, `intentPreview`, `actionPreview` (the UI and the card-ability predicates, WP-9).
- The detached prefix evaluation of an action (D-223) is replaced by the live state.

**Next:** WP-8 (Counter, Smash, Sweep) plays on the committed-action machinery; WP-9 adds the opcodes and traits; WP-12 completes the quote.

## Core 2 WP-5: stances and card costs in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-costs.mjs`, D-206):** the shipped web at `b8cfe4fac`, three kinds of file under `Tests/Oracle/core2/costs/` (6.9 MB, two runs byte-identical). `grid.json`: all 211 profile cards against 23 stances (none, nine physical, six Spell, four mirrored Spell, ranged, two Many Forms concurrent states) for `sameStance`, `spellChain`, `stanceEffectiveness` (1.25), `stanceEntryBonuses`, `stanceStaminaCost` in the player's and the enemy phase, `stanceManaCost`, and an enemy entity with and without a Mana price; plus `levels` (D-205): player `characterLevel` 0, 1, 4, 9 with every card's entry bonuses, grant, mix and the Precision and Evade a real `setProfileStance` leaves. `prices.json`: `cardCostBudget` / `applyActionCosts` for the 211 profile cards and 138 enemy cards at ranks 0..3 (as authored, forced through the derivation, upgraded) and every enemy's pool. `fights-<class>.json`: 4 classes x 12 fights: the opening hand and a hand of all 211 cards under 14 state variants priced (`cardPlayCosts`, `resolvedCardPlayCosts`, the cost parts of `previewCard`, the play legality with the Stamina and Mana one short and exactly enough), and chains of plays (the front half of `doPlayCard`) with the whole hand priced after each.
- **`CoreCosts`** (D-200): `Resolve`, `Behavior` (play limit, cost growth), `ProposalCardPlan` (status restriction, flat tax, extra stance capacity), `StanceCapacity`, `RememberStance` (D-202), `ProfileCardCosts`, `PriceReceipt`, `PlayCosts`, `CardPlayCosts`, `PreviewCosts`, `PlayRefusal`. **`CoreStances`** (D-201): the player branch of `StaminaCost` (Reaction card in the enemy phase), `ManaCost`, `SpellChain`; `SetProfileStance` remembers the stance it leaves. **`CoreActionCosts`** (D-203): `Budget`, `IsCostOptOut`, `Apply`, `OwnRestoreFunds`. **`CoreCardPlay`** (D-204): `Carrier` and `Enter`, the front half of a card play.
- **D-214 fixed (D-205):** `CoreStances.PrecisionStatMix` / `PrecisionEntryGrant` delegate to `CoreHitRoll`; a player stance entry now counts the fight's `characterLevel`. Signatures of `StanceOf`, `SameStance`, `EntryBonuses`, `MirroredManeuver` unchanged.
- **Data:** `costs` section of `rules/core2Combat.json` (+ schema; poise line, cost growth cap, default stance capacity, reaction discount, `scaleFromSp`), `ProposalStatusRules` on `CoreCombatData`, codegen fragment `core2-costs.json` (`Core2CostKeys`, `Core2CostValues`, `Core2CostMessages`). `CoreFixCore.Rules` / `Family` take an optional carrier for the mechanics refusal.

**Parity mismatches found, all fixed:** the player's stance-entry Precision was short by the character level (D-214, the one real bug); a magic `2` in the effect-scaling threshold (now `costs.scaleFromSp`); the test's expectation for a probe the web refuses earlier than the cost gates (the player-turn gate, a stance choice): the C# port has no such gates yet, so only the cost errors are compared there, and a short-resource probe may return a cost refusal or none.

**Tests:** `Core2CostParityTests` 127 (oracle present, 23 grid cases, 4 player-level cases, 211 + 138 price tables, enemy pools, 48 fights of costs, previews and legality, 48 fights of play chains). Domain.Tests (`Category!=Bot`) 4957/4957 (baseline 4830 + 127). `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Content` and `Ashen.Generated`, compiled under dotnet only.
- Not ported (refused by name): Combat Rounds queues, a `cardPreparing` listener, extra plays, proposal changes on an instance, a card outside the profile, mechanic tags or bindings on a card (the mechanics kernel, WP-7), and the range quote's Advance price in `previewCard` (WP-12). The effects, hit roll and damage of a play are WP-7+: `CoreCardPlay.Enter` stops after the stance.
- Every shipped profile card is `costRule: fixed`, so `applyActionCosts` prices nothing in the shipped profile; it is proven by forcing it.

**Next:** WP-7 (damage and protections) plays the effects after `CoreCardPlay.Enter`; WP-12 adds the Advance price to the preview.

## Core 2 WP-6: d20 hit resolution in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-hit.mjs`, D-215):** drives the SHIPPED `stanceHit.js` and `combatAvoidance.js` at `b8cfe4fac` on real Core 2 fights (`createRunCombat`, 4 classes x all encounters) with a declared overlay on the fighters, the rule snapshot and the engagement state. 360 grid cases (both directions; attributes, level, Precision and Evade stacks, flat bonuses, advantage and disadvantage on either side, prepared Evade, a pending next-Evade mode, maneuver x camp x range x reach x Target x mirrored Spell, the target's stance range, engagement bands, rule variants, real hand cards and enemy intents as carriers) with the preview and six resolutions each on scripted dice (naturals 1 and 20, the die either side of the need; the draws asked for, the Evade left behind, the events); 60 sequences (577 rolls) on the real `combatAvoidance` stream with every RNG counter after every step; units for evasion, stack gain, stat mix, the selected-card preview with its stance entry, the avoidance receipt, the proposal plan and the chance dice (5.1 MB, two runs byte-identical).
- **`CoreHitRoll`** (D-210): `Preview` (the 20 / 400-outcome fixed-defense enumeration; Accuracy = Precision base + stat mix x (stacks + 1) + flat bonus + maneuver + proposal + range, Evasion = base + DEX + Evade stacks x (baseAC + DEX); in-range, Target and `unavoidable` bypass, nat 1 and 20, advantage and disadvantage), `Resolve` (the `combatAvoidance` d20 through an injected die; Evade stack, prepared charge and next-Evade mode spent; `attackHit` / `attackMissed`), `PreviewCard` (the stance entry's Precision), `EffectiveEvasion`, `EvadeStackGain`, `PrecisionStatMix` / `PrecisionEntryGrant`. **`CoreAvoidance`** (D-213): `ChanceMode`, `EffectiveChance`, `RollChanceDie`, the expanded-fight `PreviewAvoidance` / `Resolve`. **`CoreProposal`** (D-212): the status-rule plan a hit reads (accuracy delta, Greased fire, Veil / Evade checks).
- **Data:** the `hit` section of `rules/core2Combat.json` (+ schema), codegen fragment `core2-hit.json` (`Core2HitKeys`, `Core2HitValues`, `Core2HitMessages`).

**Parity mismatches found, all fixed:** the first full run failed 274 of 1152: the proposal behaviors are an array (read as an object, so Charm never applied); the web's `perceptionGained` receipt (enemy knowledge credited by a miss against Evade) is the knowledge port's hook, so the oracle records only the events a hit owns (D-211); and re-assigning a child token to its own parent property clones it in Newtonsoft (`projected[statuses] = statuses` detached the object the projected Precision was written to, so every selected-card preview ignored its stance entry).

**Found in WP-4 code, not edited (D-214):** `CoreStances.PrecisionStatMix` reads the entity's level only; the web counts the fight's `characterLevel` for the player, so a player stance-entry Precision grant would be short by the level. `CoreHitRoll.PrecisionStatMix` / `PrecisionEntryGrant` follow the web. On merge with WP-5, `CoreStances` should delegate to them.

**Tests:** `Core2HitParityTests` 1152 (the oracle is present, 360 previews, 360 scripted-roll cases of 2160 rolls, 60 real-stream sequences, 368 units, 3 chance-dice tests). Domain.Tests (`Category!=Bot`) 4830/4830. `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain` and `Ashen.Generated`, compiled under dotnet only.
- Handed in rather than computed (D-211): the range contact (WP-12; a fight with a `rangeState` and no contact is refused), the proposal damage type (WP-7), the knowledge credit of a missed or prevented attack (WP-11). `hitReadExact` / `previewTargetFor` (the hit-% badge's Perception gate) are not ported.
- Refused by name: Accuracy without FixCore, the 405 distance and Evade rolls, an entity carrying mechanic tags or bindings (the mechanics kernel, WP-7).
- Nothing calls `CoreHitRoll` yet: card play and the enemy action phase (`CoreActions.Execute`, WP-5, WP-7, WP-11) are its callers.

**Next:** WP-7 (damage receipt and protections) consumes the hit result and the damage type; WP-12 supplies the range contact.

## Core 2 WP-4: combat start, hand and resources in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-start.mjs`, D-182):** 140 fights (4 classes x all 35 encounters, `sweepSeed(7000 + ...)`) from the web's `createRunCombat` at `b8cfe4fac`: the whole combat object when the fight opens (Stamina full, hand drawn, first intents rolled, event types, every RNG counter), then 468 end-turn steps with every enemy staggered (the enemy phase runs in full without an action; two encounters per class run 9 turns, the rest 3), stored as deltas (2.9 MB); `hand.json` has 47 scenarios on the hand functions; the rule snapshots are recorded once per class. Two runs are byte-identical.
- **`CoreRunCombat.Create` / `CoreCombatStart.Create`** (D-180): ratings with `ratingSources`, effective attributes, `baseEvasionBonus` and `basePrecisionBonus`, persistent Poise (Ward is off), Barrier, full Stamina as the shared Actions pool (`energy` is the pool), hand 4 + INT (`CoreHand`), draw rows from the run's own stat rows, nothing retained, the entire-deck round shuffle on the `shuffle` stream, the discard reshuffle, Innate cards first, the Poise vessel from the Poise row, armour and relics, the perspective formation grid (`CoreRange`), the engagement bands from the starting weapon, enemy HP on `enemyHP`, enemy level, attributes (seeded hash variance), SP and MP pools and ratings from the profile row (`CoreEnemies`), the property mounts, the first plan and intent (`CoreIntents`: SP-pool plan on `enemyAI`, the weighted pick, Counter priming), the d20 intent read on `enemyIntentVisibility` and `enemyIntentClue` (`CoreKnowledge`), the stance and its Precision grant (`CoreStances`), and the end-turn loop (`CoreTurns`: owner-cycle status control, decay, the skipped enemy turn, the hand cleanup and the next turn's draw).
- **RNG (D-181):** `Rng` gained the Core 2 stream set beside the 405 counters; no 405 fixture moved (the 405 suite is unchanged).
- **Data:** hand-written `rules/core2Combat.json` (+ schema, D-183), exported `rules/stanceRules.json` and `rules/core2HandRules.json`, codegen fragment `core2-combat.json` (`Core2CombatKeys`, `Core2CombatValues`, `Core2CombatMessages`), `Core2Registries.ToCoreCombatData`.
- **WP-3 gap closed (D-185):** `CoreGear.ResolveItem` also returns `modifiers` and `pools`, which the fight's `ratingSources` carry.

**Parity mismatches found, all fixed:** the first full run failed 327 of 328 cases, almost all from a few wiring slips. The ones worth keeping: `rules/actionCosts.json` keeps its keys in upper snake case; the module ids are kebab case; the enemy Precision grant read the stance rules one level too deep (so every enemy stance entry granted none); the knowledge label is `?`; `resolved` lacked the gear `modifiers` and `pools` (WP-3); `perception` is the skill key; `weaponARPerTier` and `armorDRPerTier` are cased as the web does; `Js.Truthy` threw on a null `string` (shared helper, the 405 is unaffected); an unset optional deck field must be absent, not null.

**Tests:** `Core2CombatStartParityTests` 328 (the oracle is present, 140 fights open as the web opens them, 140 end-turn loops of 468 steps equal field for field with the event types and RNG counters, 47 hand scenarios). Domain.Tests (`Category!=Bot`) 3619/3619 (baseline 3291 + the 328 of this package). `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Content` and `Ashen.Generated`, compiled under dotnet only.
- Refused by name until their work package (messages in `Core2CombatMessages`): card play and the player's card costs, script and once-per-turn effects, every opcode but applyStatus, removeStatus, draw, restoreStamina, restoreMana, block and gainBarrier (WP-5 to WP-9), status procs, meters and control (WP-9), Ward restore and Poise overflow, an enemy that acts (WP-6, WP-11: the loop staggers every enemy), planned follow-up moves, Flux, Ashen Blight milestones and burdens (WP-10), turn-end-in-hand hooks, the mechanics kernel (WP-7), enemy gear with mechanics, feats, companions and sigils, and a hand refresh other than the entire-deck shuffle.
- `resolveEnemyLevel` is dead for a configured fight (D-184). `combatStartRefill` stays in `rules/core2Run.json` (D-183).
- Nothing consumes `CoreRunCombat` yet (no `CombatSession` over a Core 2 fight); that is WP-19.

**Next:** WP-5 (stances and costs) and WP-6 (d20 hit resolution) can start in parallel: `CoreStances.SetProfileStance`, `CoreActions.Execute` and the NotSupported guards are their hooks.

## Core 2 WP-18: the deck editor and its rules in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-deck.mjs`, D-194):** the web's `model/deckRules.js` at `b8cfe4fac` on WP-3 style runs, under `Tests/Oracle/core2/deck/` (3.4 MB, byte-identical on rerun): 12 settings variants x refusals for deck sizes 0..45, 4 class probes (every one of 529 registry cards: equipment eligibility with range on and off, copy limit, basic classification, the class-string limit), 24 bot sessions (4 classes x 6 seeds x 90 steps: moves in both directions incl. 1,000+ refused or accepted answers, rewards into either pile, begin/cancel, 423 commits of valid and corrupt drafts, settings and range-policy switches) and 16 draw-pile cases (`orderedDrawPile`, `orderedReturn`).
- **`DeckRules`** (`Domain/Run`, D-190..D-193): bounds, settings problems and refusals, the doors, basics (`BasicCardIds`, `UnlimitedCardIds`, `IsBasicCard`, `IsUnlimitedBasic`, `IsSetAsideBasic`), the configured copy limit (`ConfiguredCopyLimit`, with the pool/gear resolution of `resolveCharacterDeck`) and the class-string limit, the equipment gate (`CardEquipmentEligible`), `MoveToSideboard` / `MoveFromSideboard` (restamp = `ReconcilePools` + `ReconcileGearGrants`), `UnusedInstanceId`, `BeginEdit` / `CancelEdit` / `CommitDraft`, `OrderedDrawPile` / `OrderedReturn`. `CoreDeckData` (+ `Core2Registries.ToCoreDeckData`) hands it `rules/core2Deck.json` and the card-domain tag ids. `CoreRunRules.GearGrantInstances` is public.
- **Data:** hand-written `rules/core2Deck.json` (+ schema), codegen fragment `core2-deck.json` (`Core2DeckKeys`, `Core2DeckValues`, `Core2DeckMessages`), `transform-content` HAND_WRITTEN row.

**Parity mismatches found:** none in the port. Two test-side points: the web's commit compares an owned card with its draft by `JSON.stringify`, so key order is part of the case (the oracle stores the draft as text and a reward is built `{ instanceId, cardId, upgraded }` in the test); and the oracle refs use the last `:` because granted instance ids contain colons. The 405 `Armoury` is untouched (the 405 has no deck editor).

**Tests:** dotnet Domain.Tests (`Category!=Bot`) 3350/3350, 59 of them `Core2DeckParityTests`: oracle present, settings (12), per-class probes (4), editing sessions (24, every step: answer, editor state, limits, sideboard eligibility, refusal, and the run document at the end), draw pile (16), cancel restores the exact run, refused moves change nothing. `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Content` and `Ashen.Generated`, compiled under dotnet only.
- Not ported: `addBasicCard` and the 405 slot-true basics (a Core 2 run has none; D-193), the Settings/lobby/DeckEditorModel UI, `deckEditorDoors`, `armouryRules.editingLocked` (the door gating outside combat is main.js/UI glue, WP-19/UI). The commit door's copy-limit refusal is unreachable under Core 2 (D-194).
- Nothing consumes `DeckRules` yet: combat start (WP-4) calls `OrderedDrawPile` when `PlayInDeckOrder` is on; the run loop (WP-19) opens the editor.

**Next:** WP-4 reads `DeckRules.PlayInDeckOrder` / `OrderedDrawPile` at combat start (no `shuffle` draw when on); WP-19 gates the editor with `EditingOn` / `EditingWhere`.
## Core 2 WP-3: run creation for the configured profile in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **Oracle first (`Tools/oracle-core2-run.mjs`, D-173):** 96 run documents from the web's `createRunState` at `b8cfe4fac` (4 classes x 12 seeds x the modes `lean` and `assign`, every class unlocked through the meta), each with the `createRunCombat` arguments `enterCombat` builds for two act-1 encounters; `unlocks.json` (the class gate at every completed-run count, a fresh meta, another profile's ledger); four refusals. `runId` is left out, the 640 KB `gameProfile` is kept as id and hash. Two runs are byte-identical (SHA-256 over the folder).
- **`CoreRunRules.Create`** (`Domain/Run`, D-170): class availability (`classUnlocks`: Reaver first, one more per completed run in the profile's ledger), fixed class attributes and the fixed-attribute mode snapshot, the profile's `stats` as the explicit override layer (hand rows 4 + INT, pool bases), the ruleset 7 stat-row table and run snapshot with the relic fold (`CoreDerivedStats`), worn gear resolved by the armor budget (`CoreGear`: pool bonuses, effective attributes), the equipment profile snapshot, starter deck from `starterDeck` (`gp1...`), the gear-granted cards (Bone Sceptre lends Mana Surge to Starseer and Herald), cinders from the balance, flask charges and growth, zones, and the stamps: `gameProfile` (the pinned snapshot), `rangePolicy`, `reactionRules`, `combatExpansionRules`, `ashenBlight`, `advancedConfigSnapshot`, `progressionRuleSnapshot`, schema and content versions. `CoreRunRules.CombatArgs` builds the fight arguments. `Core2Registries.ToCoreRunData(ContentSet)` hands the Domain what it reads (`CoreRunData`).
- **Data:** hand-written `rules/core2Run.json` (+ schema) holds the web's literals; codegen fragment `core2-run.json` (`Core2RunKeys`, `Core2RunValues`, `Core2RunMessages`, `Core2RunNumbers`). The 405 reader gained the additive `magicShare` profile carrier.
- **D-167 resolved (D-171):** the web does not refuse an old run under a new profile hash; it resumes under the profile the run pinned (the whole snapshot rides in the run). The load door refuses only a registry with another hash than the pin, recoverably (slot kept). `CoreRunRules.PinOf` and `MatchesProfile` are in, with tests.
- **Streams (D-172):** creation draws nothing; the `RngStream` enum is not merged with `core2Streams` yet (the 405 documents record every counter).

**Parity mismatches found:** one. The first run differed only in `equipmentProfileRuleSnapshot.profiles.*.magicShare` (the web carries a blank magic share as `null`; the 405 reader had no such carrier). Fixed in `Loadout.ProfileRule` / `RestoreProfileSnapshot`, guarded so the 405 content (which has no `magicShare`) is unchanged. All 96 runs, 192 fight argument sets, 32 gate cases and 4 refusals then matched on the first full run.

**Tests:** dotnet Domain.Tests (`Category!=Bot`) 3275/3275 (baseline 2943; Core2RunParityTests are 328 of them, the others arrived with the dev commits merged during the work). `Core2RunParityTests`: run equals oracle field for field (96), no RNG drawn and stream names known (96), fight arguments (96), refusals (4), gate (32), gate order, pin and profile guard, determinism and no shared state. `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green.

**Not verified / deferred**
- Unity (no editor): everything is in `Ashen.Domain`, `Ashen.Content` and `Ashen.Generated`, compiled under dotnet only.
- Not ported: the `derivedStats.js` row validators and the restore door for a ruleset 7 snapshot (WP-13, with the save); class mastery's `creationReferenceOpen` (open for a meta without `classMastery`); starting-hand, kit and ability options other than the baseline (a configured class has fixed loadout); the 405's creation variants. The fight's ratings receipt, hand rows and enemy levels are WP-4.
- `rules/mechanics.json` is still the 405 file; `combatStartRefill` lives in `rules/core2Run.json` until WP-4 exports the framework mechanics.
- Nothing consumes `CoreRunRules` yet (no `RunSession`); the run loop is WP-19.

**Next:** WP-4 (combat start and hand: `CombatArgs` is its input), WP-14 and WP-18 can start (WP-2 and WP-3 exist).
## Core 2 WP-14: the Core 2 act map in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **`Tools/oracle-core2-map.mjs`** (over `Tools/oracle-core2.mjs`, so the drift guard applies) -> `Unity/Assets/Tests/Oracle/core2/map/`, 2.8 MB: 9 `maps-<seat>-<tier>.json` (3 acts x 3 seats x 40 seeds), `history.json` (252), `variants.json` (49 routes variants, 735 cases, 365 refusals with the web's messages), `seats.json`, `refusals.json`, `index.json`; 1347 maps in all. Sorted keys plus `nodeOrder`; counters for all 24 streams. Byte-identical on rerun (SHA-256 over the folder equal).
- **`CoreRoutes`** (`Domain/Map`, D-175): `buildGameProfileActMap` as a data-driven builder over `CoreRoutesConfig` (routes content, module, profile hash verbatim) and `CoreRoutesVocabulary` (`rules/mapEngine.json` `routes`). `ActMapGraph`/`MapNode` gain the optional Core 2 fields `actualType`, `label`, `gameProfileHash`; `Col` is a double. `MapEncounters.ResolveUnknownNode` has an overload over explicit odds. `Core2Registries.ToMapData` / `RoutesConfig` feed it.
- **Boss destinations** (D-177): `profile/bundle/bossDestinations.json` from the web module `bossDestinations.js` (new `core2.modules` row and transform row).
- **Codegen:** `Tools/map-keys/messages.json` (13 route messages), `node Tools/map-keys.mjs` then `node Tools/codegen.mjs` (MapKeys 114, MapMessages 65).

**Tests:** `Core2MapParityTests` (16): the shared suite compares every recorded map by canonical JSON, node insertion order and all RNG counters, plus histories, variants and refusals, the seat order draw, the fog rule and the hash stamp. No parity mismatches. dotnet Domain.Tests (`Category!=Bot`) 2959/2959 (2943 + 16). `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green (no literals in Game code). `rules.mapEngine.schema.json` gained the `routes` section and `profile.bundle.bossDestinations.schema.json` is new.

**Not verified / deferred**
- Unity (no editor): the new files are in `Ashen.Domain` and `Ashen.Content`, compiled under dotnet only.
- `ClimbViews` still reads `col` as an int; a Core 2 rest node on an even lane count has a half column (the shipped content has 3 lanes, column 1). WP-19 (run loop glue) wires the Core 2 map into the UI.
- `buildGameProfileActivities` (foundation launcher) and the Custom Climb shape are not ported for Core 2; the `default` profile configures routes.

**Next:** WP-3 (run creation) can call `CoreRoutes.BuildActMap` with the run RNG; WP-19 routes the nodes.

## Core 2 WP-14: the Core 2 act map in C# with oracle parity (dev, 2026-10-23)

**Landed**
- **`Tools/oracle-core2-map.mjs`** (over `Tools/oracle-core2.mjs`, so the drift guard applies) -> `Unity/Assets/Tests/Oracle/core2/map/`, 2.8 MB: 9 `maps-<seat>-<tier>.json` (3 acts x 3 seats x 40 seeds), `history.json` (252), `variants.json` (49 routes variants, 735 cases, 365 refusals with the web's messages), `seats.json`, `refusals.json`, `index.json`; 1347 maps in all. Sorted keys plus `nodeOrder`; counters for all 24 streams. Byte-identical on rerun (SHA-256 over the folder equal).
- **`CoreRoutes`** (`Domain/Map`, D-175): `buildGameProfileActMap` as a data-driven builder over `CoreRoutesConfig` (routes content, module, profile hash verbatim) and `CoreRoutesVocabulary` (`rules/mapEngine.json` `routes`). `ActMapGraph`/`MapNode` gain the optional Core 2 fields `actualType`, `label`, `gameProfileHash`; `Col` is a double. `MapEncounters.ResolveUnknownNode` has an overload over explicit odds. `Core2Registries.ToMapData` / `RoutesConfig` feed it.
- **Boss destinations** (D-177): `profile/bundle/bossDestinations.json` from the web module `bossDestinations.js` (new `core2.modules` row and transform row).
- **Codegen:** `Tools/map-keys/messages.json` (13 route messages), `node Tools/map-keys.mjs` then `node Tools/codegen.mjs` (MapKeys 114, MapMessages 65).

**Tests:** `Core2MapParityTests` (16): the shared suite compares every recorded map by canonical JSON, node insertion order and all RNG counters, plus histories, variants and refusals, the seat order draw, the fog rule and the hash stamp. No mismatches. Full `Category!=Bot` run and the node checks are recorded in the commit message of this entry's commit.

**Not verified / deferred**
- Unity (no editor): the new files are in `Ashen.Domain` and `Ashen.Content`, compiled under dotnet only.
- `ClimbViews` still reads `col` as an int; a Core 2 rest node on an even lane count has a half column (the shipped content has 3 lanes, column 1). WP-19 (run loop glue) wires the Core 2 map into the UI.
- `buildGameProfileActivities` (foundation launcher) and the Custom Climb shape are not ported for Core 2; the `default` profile configures routes.

**Next:** WP-3 (run creation) can call `CoreRoutes.BuildActMap` with the run RNG; WP-19 routes the nodes.

## Core 2 WP-2: profile loading and the Core 2 registries in C# (dev, 2026-10-23)

**Landed**
- **`CoreProfile`** (`Content/Runtime`): `profile/core.json` (id, hash verbatim, foundation, modules) plus the 26 `profile/content` keys; list-shaped files (characters, cards, enemyCards, enemyRelics, gear, enemies, classes) are rows in registry order (`rules/rowOrder.json`), the rest as shipped. `Validate()` returns a `ValidationReport`: missing id, foundation, module (every `Core2ModuleIds`), content key (every `Core2ProfileContentKeys`), or a hash that is not `sha256:` + 64 hex. New codegen keys: `Core2Layout`, `Core2ProfileRules`, `Core2ProfileMessages`, `Core2ProfileValues`, `Core2MoveKeys`.
- **`Core2Registries`** (the seam, D-168): `Load(ContentSet)` validates, then `RuntimeRegistries.BuildCore2`. `RegistryBundle.LoadCore2` reads each source from its `profile/bundle/` twin and turns every other file in that folder into a top-level bundle key; the same projection, tag stamp and equipment steps as the 405 build then run. Core 2 adds the enemy-move merge (enemy rows take their `moves` from the stamped flat rows) and the `resources`, `sigils`, `consumables`, `companions` tables. Thin accessors: `Cards`, `Statuses`, `Relics`, `Enemies`, `Stances`, `Classes`, `Resources`, `EnemyMoves`, `MovesOf`, `Characters`, `ProfileCards`, `EnemyCards`, `EnemyRelics`, `Gear`, `Stats`, `Routes`, `Rests`, `Rewards`, `Shops`, `ClassUnlocks`, `CardPools`, `StarterDeck`, `StartingHands`, `ClassAttributes`, `ResourceVariants`, `Table(name)`, `Row`, `Object(name)`. Reads are deep clones. The 405 path (`Build`, `Load`) is unchanged.
- **One parity mismatch found and fixed (D-169):** `cards/hewingArc.upgrade.effects[0].amount` was 9, the dump has 8. The web at `b8cfe4fac` prices a `costRule: fixed` upgrade at the base face's cost (`priceFace`); the C# port of `attackCardDamage.js` was from the 405 pin. 21 Core 2 cards are affected; the 405 has none, so its fixtures did not move.

**Tests:** dotnet Domain.Tests (`Category!=Bot`) 2943/2943 (2865 + 78). `Core2RegistryParityTests`: 19 tables (ids in order, each row by value and key order), 25 data registries, profile document and hash, 26 content keys, typed accessors, copies on read, validation findings (missing module, content key, hash, foundation; no profile), legacy registries untouched. `transform-content --check`, `codegen --check`, `refresh-manifest --check` (8 `.meta` written), `check-content`, `check-docs`, `web-key-map --check` green. EnforcementTests green (no literals in Game code; new keys generated).

**Not verified / deferred**
- Unity (no editor): the new files are in `Ashen.Content` (engine-free) and `Ashen.Generated`; compiled under dotnet only.
- Not rebuilt (the web computes them from code, not content): `contentVersion`, `eventHistoryRequirements`, `eventSpeakers`, `questChains`, `scripts`, `statDomains`, `tree`, `framework`, `frameworkTerms`. `equipment` is compared without its top-level section order.
- `CombatData` for Core 2 (`ToCombatData` over these registries) is WP-3/4; nothing consumes `Core2Registries` yet. No run is created from the profile (WP-3), so D-167's resume pin is still open.

**Next:** WP-3 (`oracle-core2-run`, run creation from `Core2Registries`) and the rules loaders over `rules/*.json`.

## Core 2 WP-1: the oracle harness over the web at `b8cfe4fac` (dev, 2026-10-23)

**Landed**
- **`Tools/oracle-core2.mjs`** (shared library, imports run nothing): the drift guard (web HEAD must equal `core2.webSha` in `Tools/export/sources.json` and tracked files must be clean, `--allow-dirty` as the exporter); `loadWeb` (`applyGameProfileToContent` -> `createRegistries` -> `bindGameProfileRegistries` for `default`); `createRun` (all four classes unlocked through `profileMeta`); `createFight` (the run RNG at `run.streamCounters`, as `main.js enterCombat` does for a Core 2 run); `legalIntents`; a bot on its own LCG; `project`; `capturePreviews`; `runCombatFixture`.
- **Legal intents** are the ones a `dispatch` on a detached copy (`candidateState`) accepts: `playCard` (targets x stance choices x extra `rangeRoute`s read from `previewCard`), `endTurn`, `useFlask`, `setReactions`, `chooseReaction` (only while an offer is pending, with the decline), `chooseCards` / `chooseDiscard` (only while a choice is pending), `predictIntent` (only for an `unknown` read), `moveCharacter` (formation movement is off in `default`, so none is ever legal), `recoverControl`. `swapArmament`, `changeEquipment`, `basicAttack`, `pass` are not enumerated (refused under Core).
- **Projection** (sorted keys): turn, phase, result; player and enemies with hp, stamina, mana, energy, `persistentPoise`, `poiseMeter`, `persistentWard`, block, barrier, `combatStance`, `combatCounter`, statuses (full rows), ratings, cell, intent, planned and pending moves, knowledge reads; hand, draw, discard and exhaust as `instanceId:cardId`; `rangeState.engagements`; `pendingReaction` offers; the reaction stack; pending card choices; `rng.getCounters()`.
- **Previews** after every step: `cardPlayCosts`, `cardNeedsEnemyTargetNow`, `cardChoicePlan`, `previewCard` and `previewCardHit` (the hit preview is `attackHitPreview` plus the range quote) for every card in hand against the first living enemy, `previewIntent` and `previewEnemyAction` for every enemy. The range quote is flattened to one row per route and `resolvedDefinition` is dropped (the registry dump holds it): full output was 20 KB a step. `capturePreviews` throws if a preview moves the projection or any RNG counter. `counterAttackPreview` and `resolvedCardPlayCosts` are not captured (they need a carrier or a resolved def that the engine does not export).
- **`Tools/oracle-core2-registries.mjs`** -> `Unity/Assets/Tests/Oracle/core2/registries/`: 81 files, 2.9 MB, largest `tables/cards.json` 641 KB. `tables/<name>.json` (19 id-keyed registries as `{table, count, ids, rows[]}` in registry order), `objects/<name>.json` (the 34 object registries, key order kept), `profile/core.json` and `profile/content/<key>.json` (26), `index.json` (webSha, profile hash, bytes). Not dumped: `legacyProgressionSource` (939 KB, 405 only), the `gameProfile` copy, the `mapConfig` function.
- **`Tools/oracle-core2-combat.mjs`** -> `.../core2/combat/`: `--bot endTurnOnly|cards|all`, 4 classes x 3 seeds x 2 bots = 24 fixtures (first-act normal and elite encounters, rotated), 294 steps, 4.5 MB, largest 433 KB. The cards bot plays a card 70% of the time and another legal intent 30%; endTurnOnly ends the turn and takes the first answer to any pending choice. Steps cover `playCard`, `endTurn`, `useFlask`, `setReactions`, `chooseReaction`, `predictIntent`; 9 victories, 14 defeats, 1 unfinished; no refused intent. Runtime about 90 s.
- Fixtures take `.meta` files from `Tools/refresh-manifest.mjs` (it already walks `Unity/Assets/Tests`): 113 written; the manifest is unchanged. D-165.

**Determinism:** both oracles re-run to the same bytes (SHA-256 over all `core2/**/*.json` equal before and after a re-run, including a per-bot partial run that merges `index.json`).

**Tests:** `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `web-key-map --check` green; dotnet Domain.Tests (Category!=Bot) unchanged (no C#).

**Web entry points that are not headless:** none of the combat ones. `main.js` still owns the run loop (WP-14/15/19 mirror it). `createRunCombat` needs the run RNG object and an `encounter` row (the oracle passes the registry's); `enemyKnowledgeProfile` defaults to empty (a fresh profile).

**Next:** WP-2 (registries and rules in C#) consumes `core2/registries`; WP-3 adds `oracle-core2-run`.

## Core 2 WP-20: Core 2 player settings, pacing dials and profile pinning (dev, 2026-10-23)

**Landed**
- **Controls (`settings/defaults.json`):** a Combat category (`autoCombat`, `combatSpeed`, `autoCombatSpeed`, `combatStepPause`, `animationStepDuration`, `combatPacingPreset`, `animSpeed`, `askToUpcastAfterTarget`, `screenShake`) and, under Accessibility, `oneTapPlay` (off) and `reduceFlashes`. Defaults, ranges and wording come from the web at `b8cfe4fac` (`actionPacing.json`, `screens/settings.js`). Auto combat runs at x1.5 (D11); the web's shipped pause is 500 ms, not the 1 s D11 asked for (D-166).
- **Preset coupling as data:** `presets` on `combatPacingPreset` (mobile-fast = 250 ms and 195 ms) and `resetsPreset` on the two values; `SettingsSession.Set` applies them. Schema and codegen keys added.
- **`CombatPacing` (`Application/Settings`):** reads the dials (effective speed, scaled stage gap and animation step) for presentation; none reaches the engine.
- **Web config:** keys use the web names, so `settings.<key>` import and export take them with no `webKeyMap.json` change (D-164 untouched; the check is green).
- **Import coupling (fix):** `WebConfig.Apply` stores imported `settings.*` through `SettingsSession.StoreImported`, the same preset coupling as `Set`; an explicit bundled value wins over an imported preset, which stays only if every imported bundled value matches it, else returns to `custom`. The web has no import path for this, so the rule is ours.
- **Profile pinning (D-167):** the run record gains `profileId` and `profileHash` from `profile/core.json`.

**Tests:** five new in `MetaViewTests` (Core 2 defaults and one-tap off, auto speed and bounds, preset round trip, web import and export of the new keys, profile pin); the gates listed in the task are green.

**Not verified / deferred**
- Unity (no editor). No screen reads `CombatPacing` yet (Presentation untouched); the Combat category appears in the generated settings screen only.
- Defaults of older controls that differ from the web (`mapMode` fog, `walkedFade` has `subtle`/`half`, `shrineMultiUse`, `useRestorativeFlasksOutsideCombat`, `showPlayedCard`) are unchanged pending D-164.
- `animSpeed` auto resolves by render quality in the web; here it is stored only.
- WP-3: whether a live run refuses to resume under another profile hash.

## Core 2 WP-0: content pipeline regenerated for the `default` profile at web `b8cfe4fac` (dev, 2026-10-23)

**Landed**
- **Exporter (`Tools/export-content.mjs`):** two reads of the web repo, each pinned in `Tools/export/sources.json`.
  - `legacy.sha` `0adaae515` is unpacked with `git archive` (src and content only) under `.cache/` and exported as before, so the 405 tables are byte-identical.
  - `core2.webSha` `b8cfe4fac` is read from the checkout itself (the sha is verified) into `Tools/export/raw/core2/`: the projected bundle (`applyGameProfileToContent(contentBundle, 'default')`), `resolveGameProfile('default')` and its content keys, and the rule snapshots (`fixCoreMechanics` verbatim, `snapshotFixCoreRules`, the 257 `engine/mechanics/*` folders, `engine/runtime` ops and formulas, `reactionRulesSnapshot`, `createCombatExpansionRuleSnapshot`, `snapshotEnemyKnowledgeRules`, `snapshotRangePolicy`, version scalars, `STREAM_NAMES`).
  - The seven new modules (`combatMatchups`, `combatStatusRules`, `combatExpansionEquipment`, `combatCardTraits`, `actionCosts`, `enemyKnowledge`, `ashenBlight`) are in `sources.json` under `core2.modules`.
  - The profile hash is shipped verbatim: `sha256:0b5c8941...bd27`.
- **Transform:** new `core2` block in `Tools/transform.config.json` (51 tables, 49 documents). Optional key parts (`scope?`) fix the 255 scope-less tagging rows (4708 in the Core 2 bundle). Key order and `rowOrder` are kept for every new table (D-040). `manifest.json` gains `webSources` (both shas and the profile id and hash). `rules/rng.json` keeps its 14 `streams` and gains `core2Streams` (the web's other 11, enum `Core2RngStream`); the transform fails unless the two together equal the web's `STREAM_NAMES`. Appending to `streams` itself broke about 800 parity tests, because run, combat and map documents record every stream counter. `rules/effectOps.json` gains the 16 Core 2 ops used by the new content so it validates (no interpreter yet).
- **Content (all new, the 405 files untouched):** `profile/core.json`, `profile/content/*.json` (26 keys), `profile/bundle/*.json` (cards 529, statuses 101, relics 73, enemies 33, enemyMoves 138, encounters 35, classes 4, classTree 40, sigils 7, consumables 11, companions 2, classMastery 229, classSkillFeats 70, tags 436, tagging 4708, nodes 464, propertyRules 182), `profile/content` (characters 37, cards 211, enemyCards 138, enemyRelics 6, gear 47, enemies 33), and 16 `rules/*` files. `rules/core2Vocabulary.json` (hand-written: camps, reaction windows, dispatch intent types) holds the vocabularies the web keeps only in code. D-164.
- **Schemas:** 100 new. `Tools/infer-schemas.mjs` produced the shapes (it now reads the `core2` tables and skips `schemas/` entries) with Core 2 x-refs in `Tools/schema-refs.json` (profile cards, relics, statuses, stances, enemies, seats, classes, armaments). Hand-written: `rules.{versions,rangePolicy,reactionRules,enemyKnowledgeRules,core2Mechanics,runtime,core2Vocabulary}`.
- **Codegen:** `Tools/codegen.d/core2-{profile,rules,enums}.json` add 11 key sets (profile, module, content, bundle, rule, snapshot, version keys) and the enums `Stance`, `Camp`, `RangeBand`, `ReactionWindow`, `CoreIntentType`, `RewardOptionKind`, `RestService`. `codegen.mjs` learned `keys: true` (an enum over an object's keys).

**Tests:** `transform-content --check`, `codegen --check`, `refresh-manifest --check`, `check-content` (67 count-gate tables), `check-docs` green; dotnet 2860/2860 (Domain.Tests; the 405 parity fixtures untouched)

**Not verified / deferred**
- Unity (no editor). The new `Ashen.Generated` types (`Stance`, `Camp`, ...) compile under dotnet only if the Domain test project includes `Generated`.
- `settings/webKeyMap.json` is not regenerated (WP-20); the HEAD Advanced rows are exported to `raw/core2/advancedRows.settings.json` (the 4 non-`gameConfig` rows) only.
- Core 2 display text stays inline in `profile/` rows (no `strings/` keys yet). `weaponCardPackages` and the other 405-only extras (weapons, outfits, events module, music, sfx) are not re-exported for Core 2.
- The 405 replacement (plan 2.1) is still the owner's call.

**Next:** WP-1 (the oracle harness: registries, `createRunState`, `createRunCombat`, a bot, a registry dump) over `Tools/export/raw/core2` and the web at `b8cfe4fac`.

## Promotion: 0.1.37.3 (2026-10-03)

- `dev` 0.1.37.3 promoted to `test` (cehinds/AshenedSpire#1) and to `main` (cehinds/AshenedSpire#2, merge `e99920c`). No version change and no owner approval implied (D-039).
- CI green on dev and test for the engine-free lanes: version, docs, domain, content, bot (200 seeds).
- **Unverified:** EditMode, PlayMode and the Windows build. CI's `unity-test-runner` and `unity-builder` steps skip without the `UNITY_*` secrets, and no local batchmode run was made for this promotion.
- Tag `v0.1.37.3` on `e99920c` is still to be created; the session's GitHub access refused tag writes.

## Owner rulings and the last engine-free gaps: abandon, buy-back, XP strip, hand example, web config, combat sounds (dev, 2026-10-02)

**Landed**
- **Owner rulings:** D-146–D-148 and D-150–D-157 accepted; D-134 ruled (Abandon run). D-158 (by a helper agent): promotions may carry dev's accumulated version bumps, so the `dev → test` version check passes.
- **W-20 Abandon run (D-134):** a destructive hold door; the run is recorded as abandoned (it counts as a run, earns no unlock, names no killer), the slot is cleared, W-15 reads "CLIMB ABANDONED".
- **Buy-back (US-9.3, D-159):** what is sold at a merchant can be bought back at its price until the player leaves; the list rides in the save payload, so the run document and shop parity are untouched.
- **XP strip (D-160):** HUD data behind `ui/meta.json#runHud.showXp` (off).
- **Hand-rules worked example (US-15.2, D-161).**
- **Web config import/export (US-15.4, D-162):** `settings/webKeyMap.json` generated by `Tools/web-key-map.mjs`; strict import (existing paths, same kind, array elements by index, schemas); every key not taken is reported. The owner's reference file imports 90 game keys and 8 settings over the shipped preset and reproduces the reference preset's values.
- **Combat sounds (US-16.2, D-163):** `SfxDirector` and a blind `SfxPlayer`.

**Tests:** dotnet 2860/2860; `codegen`, `refresh-manifest`, `web-key-map` checks, `check-content`, `check-docs` green.

**Not verified:** Unity (no editor): `PauseScreen` abandon door, `SfxPlayer`, the merchant's buy-back rows on screen.

## Meta lane, engine-free: journal, settings, map reveal, prologue, creation panes, progression, armoury, piles, music; the 200-seed bot (dev, 2026-10-02)

This session had .NET 8 but no Unity editor, so every remaining screen landed as its engine-free half (views, sessions and data in `Ashen.App`, tested by the shared suite). Their UI Toolkit screens stay `planned` in `ui/screens.json` (D-149).

**Landed**
- **US-18.1 bot report:** `BotRunTests.BotClassic200` (explicit, Category Bot) plays 200 Classic seeds × both policies, each replayed; `docs/qa/bot-report.md`: 400 runs, **0 exceptions, 0 softlocks**. It found a shipped defect: reward cards minted `r<deck length>_<card>` repeat an id after a removal. Fixed in the port (D-146, D-147).
- **W-16 journal (US-13.1):** `JournalView` (history newest first, per-class win rates, profile tally, found armaments). The run record now carries killer, duration and deck (D-148).
- **W-18 settings (US-15.1):** `settings/defaults.json` (23 controls in 6 categories; options and ranges can point into balance data) and `SettingsSession` (profile → preset → def; validation; reset per category; "applies next run" in a run) (D-150).
- **Map reveal (US-4.2):** the `mapMode` setting: full, path (dims what is behind) or fog (hides kinds beyond the next floor; bosses kept) (D-151).
- **W-05 prologue (US-3.1–3.3):** `ui/prologue.json` (the 9 shipped slots and staging), Continue / Set forth / hold to skip, per-scene resume, a disabled saved scene resumes at the next, a filled slot plays from data. The lines are written for this build and recorded in `Provenance/generated/manifest.json`. Playback is still off in `rules/runFlow.json` until the screen exists (D-152).
- **W-04 creation panes (US-2.2–2.6, US-2.8):** `CreationSession`: name, Standard/Assign, keepsake, tint, sigil, armour, hands (moves, never duplicates), relic, kit, seed, slot with Replace; Next always refuses with the reason and the focus. `RunSession.New` takes the choices (D-153).
- **W-22 progression (US-12.5):** `ProgressionView` (level, XP, banked points, tracks, the class tree by tier with chosen / can be drafted / closed by / not yet, and the exclusive tier) (D-154).
- **W-12 armoury and W-17 piles (US-7.6):** out-of-combat equip and set cycling through the shipped doors, then stampDeck and a save; a preview lists receipts; the pile viewer (D-155).
- **Music (US-16.1):** `audio/beds.json`, `MusicDirector` (location → context, pool overrides, run end, stingers) and `BedRenderer` (deterministic PCM loops from `audio/music.json`); a `MusicPlayer` in `AshenBoot` (D-156).
- **Tooling:** `Tools/refresh-manifest.mjs` refreshes the manifest for hand-written files and writes `.meta` files without the shipped export; CI runs its `--check`.

**Tests**
- dotnet **2849/2849** (`MetaViewTests`: 19 new); `BotClassic200` 400/400; `codegen --check`, `refresh-manifest --check`, `check-content`, `check-docs`, `ui-tokens --check` green.

**Not verified / open**
- No Unity in this session: EditMode, PlayMode and captures were not run. The blind Presentation edits are `MapBoard` (two USS classes), `Climb.uss`, `AshenBoot` and the new `Presentation/Audio/MusicPlayer.cs`; none were compiled.
- Screens still to build in the editor: W-04 panes 2–4, W-05, W-12, W-16, W-17 piles, W-18, W-22; then flip `rules/runFlow.json#newRun.prologue` and mark the screens built.
- Not built: US-15.4 web config import/export, US-15.2 the hand-rules example, the buy-back list (US-9.3), Abandon run (D-134 still needs the owner), the XP strip, SFX wiring, generated music files.

**Next:** in the editor: build and capture the planned screens over these views; compile and hear `MusicPlayer`.

## us-9.1–9.3, us-10.1–10.3, us-4.5: F3 node screens — W-09 merchant, W-11 event and dialogue, W-13 legacy dungeon (feature/node-ui/us-9.2, 2026-09-26)

**Landed**
- **Merged `origin/dev` 0.1.13.2** (one smith port) and **0.1.15.0** (weapon swap, run-creation options; `Choices` moved to `RunKeys`) into the story branch.
- **Application (`Ashen.App.Nodes`, engine-free):**
  - `MerchantSession` (W-09 on `Shop`):
    - Start rolls the stock only when the run holds none (the 'shop' and 'smith' streams); a reload reuses the saved stock (US-9.1).
    - `Execute` takes a purchase, burn, smith service or sale and saves when it lands; `Leave` clears the stock and saves at the map.
  - `MerchantView` builds the shelves in `ui/nodes.json` order: cards, armaments, weapon arts, relics, flasks, services and sell.
    - Each offer carries its name, text, art, price, availability and W2a door.
    - An unaffordable offer reads "Need n more cinders". Other refusal keys resolve from `shop.en.json`, or the shipped `ui.shop.avail.*.short`.
    - The card burn is priced at its rising price, with its pick.
    - The smith's upgrade, extract and install come from the shipped plans, priced in stones.
    - The sell shelf shows only while `shopSell` is on, with the Buy back note (D-081).
  - `EventSession` and `EventView` (W-11 / W4c on `Events`):
    - Open → responses, each with an effect preview, a binding mark and its requirement. The history-gated choices the shipped door hides show as locked rows.
    - `Choose` saves the resolved state; the response is part of the resume entry.
    - `Continue` is null until a response is taken. A fight is entered through `RunLoop.EnterEventCombat` and handed over; otherwise the run returns to the map.
    - Continue reads "Steel yourself" when the response started a fight.
    - A quest chain's step has its speaker and portrait. The Turncoat Mirror's swap is its op; the saved portrait follows the new class.
  - `DungeonSession` and `DungeonView` (W-13 on `LegacyDungeons`):
    - Responses: listen, fight, and flee with its Dexterity odds. Continue after a response.
    - Travel along an edge, then enter the room: Rest / Open / Fight (D-141).
    - Leave a cleared dungeon: the summit's victory, or the next act.
    - The view gives the scene's background and floor, the occupant, and the scene-graph mini map.
  - `NodeScreens.Refine` puts a quest chain's event step on `screen:dialogue`; `NodeReview` holds the review and test hooks (D-145).
- **One path with the climb-ui lane (merged feature/climb-ui/main, then local dev):** the sessions are thin over `RunSession` (D-140):
  - `MerchantSession` → `RunSession.MerchantStep` (new; a refusal saves nothing) and `LeaveMerchant`;
  - `EventSession` → `EventView`, `ChooseEvent`, `FinishEvent`;
  - `DungeonSession` → `RunSession.NodeStep` (new; a loop step whose door is routed as travel routes it, then one save);
  - a Turncoat Mirror swap re-saves the slot portrait (`FollowClass`).
  - The first cut's own router, node payload entry, `StartFight` and HUD view were removed in favour of the lane's seams.
- **Presentation:**
  - `RunFlow.Show` (rules/runFlow.json#screens) opens the four screens, which are now built; the act map's planned door stays only as a fallback.
  - `NodeScreen` base: the `RunHud` kit band, W-20 on Escape / Start / [Menu], refusals, W1r "Could not save" with Retry, a focus keeper, and `Follow()`: redraw while the run stays at the node, else `RunFlow.Show`.
  - `MerchantScreen` (W1d/W1v workspace with rail):
    - the rail folds into the [Cards ▾] selector when the scaled tap floor does not fit: 844×390 and portrait;
    - first press selects, a second press or the Primary opens the door;
    - burn and install open a card pick (`focusModes.pick`).
  - `EventScreen` serves `screen:event` (W1u) and `screen:dialogue` (W4c).
  - `DungeonScreen` (W-13).
  - `ClimbSmokeTests` now drives the merchant, event and dungeon screens instead of the planned doors.
- **Data:**
  - `ui/screens.json`: merchant, event, dialogue and legacyDungeon are built;
  - `ui/nodes.json` and `strings/nodes.en.json` (new, with schemas; `HAND_WRITTEN`);
  - `ui/menus.json`: the confirms merchantBuy, merchantRemove (destructive, hold), merchantSell, merchantSmith, eventChoose, eventChooseBinding (hold) and dungeonLeave;
  - `ui/components.json art.include` adds the merchant's hall, the region maps and the legacy scenes;
  - `ui/tokens.json` gains node sizes;
  - codegen: the `Tools/codegen.d/nodes.json` fragment, plus screen ids, UXML names and confirm ids in `ui.json`;
  - `UiData` also loads `strings/shop.en.json`, `strings/events.en.json` and `strings/nodes.en.json`.
- **Screenshots:** 26 PNGs at the ui/layout.json sizes, with `skipSizes`:
  - merchant (4), merchant_unaffordable (2), merchant_sell (2), merchant_services (1);
  - event (4), event_refusal (2), event_fight (2), dialogue (3);
  - legacyDungeon (4), legacyDungeon_room (2).

**Tests**
- **`NodeSessionTests`** (17, shared): each session runs on a seeded newRun run travelled to its node. After every step the run, the RNG counters and the profile must equal the same domain calls made directly on a copy of the context; the save's streamCounters stamp must equal the live counters. The tests cover:
  - routing by outcome kind;
  - the stock rolled once and reused after a reload;
  - card, burn (rising price), relic, flask, armament and weapon-art purchases and a sale, each saved;
  - an unaffordable refusal that changes and saves nothing;
  - the shopSell toggle;
  - every shelf and smith row reading with resolved text;
  - Leave clearing the stock;
  - Continue waiting for a response, then the resolved resume;
  - the fight response's "Steel yourself" and the same createCombat arguments as `EnterEventCombat`, saved in combat;
  - the requirement refusals (price, and the history in the Nameless chain);
  - the dialogue speaker;
  - the Turncoat Mirror as the only swapClass event, with the swap equal to the domain's;
  - every event reading with resolved text;
  - the dungeon: entering, listen, resume with the pending response, travel;
  - rooms (fight, shrine, cache) equal to the shipped TravelTo;
  - flee on the events stream;
  - leaving into the next act, and victory at the summit clearing the slot.
- **PlayMode `NodeScreensSmokeTests`** (4). Real keyboard and pad devices drive the kit Navigator; each screen is opened through `NodeRouter.Open`.
  - Merchant: buy a card (select, then the W2a door); a refusal at Buy when unaffordable; burn a card from the Services rail pick (the pick, then the destructive hold door); sell with the pad; Save & quit → Continue → the same stock; Leave → saved at the map.
  - Event: a priced response shows and refuses its requirement; a legal one through its review door; Save & quit → Continue → resolved; Continue.
  - Fight and dialogue: "Steel yourself" → W-07 in combat, saved; a quest-gated step opens as dialogue with its speaker, and the Keeper's thanks gives the bell.
  - Dungeon: listen at the gate, Continue, travel with the pad, a response; Save & quit → Continue → the same node and pending state; leave a cleared dungeon → act 2.
- **Results (after merging climb-ui / local dev, one router):**
  - dotnet 2830/2830;
  - Unity EditMode 2831 passed / 5 skipped (explicit);
  - PlayMode 8 passed / 1 skipped (capture): the 4 node smokes, the climb smoke (now through the merchant, event and dungeon screens) and the F1 smokes;
  - `codegen --check`, `check-content`, `transform-content --check`, `ui-tokens --check` and `check-docs` green.

**Review** (own screenshot review against W-09, W-11 and W-13; fixed and recaptured):
- at 844×390 the merchant rail overflowed: the model counted 44 reference px per item, but the touch minimum after scaling is taller. The screen now gives the model the scaled tap floor, and the rail folds into the selector as 04 W-09 draws;
- "Services · 1 of 1 open" was ellipsized in the rail (the count is now the open number);
- the stone glyph ⬡ has no glyph in the default font (prices now read "n stones");
- a focused ready control lost its green (the node pages keep it under the glow);
- the dialogue title repeated the speaker when the speaker is the event; the dialogue footer had no band;
- the legacy scene cropped to the floor (now centred);
- a capture's refusal was anchored before layout (the fixture presses after it).
- Nothing overlaps at the four sizes, the minimum text holds, and focus is visible on rail, tiles, responses and tray controls.

**Not verified / open**
- `ui.json`, `screens.json`, `menus.json`, `tokens.json`, `components.json`, the manifest and `ScreenCaptureTests.cs` are shared with the other lanes; expect textual conflicts (take the union, then rerun transform-content and codegen).
- The 26 node captures were taken before the RunHud consolidation (the HUD band now is the climb's kit band); not recaptured.
- US-9.3 is only partly met: there is no buy-back list (D-081); the Sell shelf states it.
- An event's damage to 0 HP is not closed out (the shipped door has no death path there either).
- W4c is a single beat, with no Skip speech or Voice control. Offers are rows; the card face shows in the detail pane.
- Mouse: pointer clicks on tiles and responses are not synthesized in the smoke; keyboard and pad are.
- The ☠ boss glyph renders in the default font here, but it is not checked on every platform.

**Next:** integrate with the act-map router (W-06) and W-10 for dungeon stays; a buy-back list if the owner wants one (US-9.3).
## us-4.2, us-4.1–4.9, us-8.1–8.6, us-13.1 (partial): the Classic Climb run flow — W-06 act map, W-10 rest, W-15 run end (feature/climb-ui/us-4.2, 2026-09-26)

**Landed**
- **Merged `origin/dev` 0.1.13.2** (one smith port; the rest stop reads `Shop.ItemSmithing`/`CardExtraction`) **and 0.1.15.0** (armament swap; `Choices` moved to `RunKeys`) into the story branch.
- **RunSession on the run loop (Application):**
  - `New` makes the climb through `RunLoop.NewRun` (D-130): name, glyph and tint, the empty advanced settings' config snapshot, no prologue while W-05 is planned; `saveFormat` 2. It saves at the act map.
  - `Travel(nodeId)` goes only to a reachable node and routes the loop's door (PF-06 "node entered"):
    - a fight enters through `RunLoop.EnterCombat` on the run's own RNG (`CombatSession.Start(data, rng, args)`, D-131);
    - a rest place opens its stay;
    - a treasure room becomes a reward checkpoint (D-125);
    - a merchant rolls its stock;
    - an event or a legacy dungeon waits for its screen.
  - The reward door is unchanged; `FinishRewards` also runs `Acts.Advance` after a boss, in one commit (D-133).
  - Rest actions: `RestHere`, `MoveFlask`, `AssignPoints`, `Smith`, `Extract`, `Install`, `LeaveRest`. Each saves; a committed service at a single-use place closes the stay.
  - Node seams: `EventView`/`ChooseEvent`/`FinishEvent`, `LeaveMerchant`, `AdvanceDungeon` (the planned dungeon walk, D-127), and `CommitStep` for the node screens' own steps (D-126).
  - The run's end: a death or the summit closes the run out (`End` is finishRun's receipt), as does leaving a cleared dungeon at the summit.
  - Load resumes at every place: the fight from its checkpoint and log, the rest stay re-opened on the streams it opened on (D-124), the reward door, the map, the merchant, the event (with its taken response) and the dungeon.
  - Settings come from the profile, then the preset, then data: multi-use shrines, the sell shelf, and the Rest hold from the shipped hold dial (D-132).
- **Views (Application):**
  - `ActMapView`: nodes, edges, the reachable row, the trail, boss destinations, the seat and floor header and the legend. The whole act is drawn (D-128).
  - `RestView`: options per place tags with availability, the Rest preview before and after, smith candidates with tier and changes, mount rows with receipts, the flask split, attributes and the live level receipt.
  - `RunEndView`: result, seed, class, act and floor, the killer or felled keeper, time, cinders, fights, the deck and the unlocks (D-129).
  - `RunHudView`: identity, trail, the three pools, cinders, stones, flask charges and the deck.
  - `PlannedDoorView`: the merchant, event and dungeon stand-ins.
- **Presentation:**
  - **W-06 `screen:actMap`:**
    - the `RunHud` kit band;
    - the `MapBoard` kit element: discs laid out by floor and column, edges drawn with Painter2D, the travelled trail in gold dots, reachable discs rimmed with the › cue, boss labels;
    - select, then Enter: the tray opens after 150 ms and a repeat pick after 400 ms travels (D-136);
    - zoom −/+, centre ⊙, the legend ?, and the Potions refusal;
    - the planned door (D-126).
  - **W-10 `screen:rest`:**
    - option cards beside their availability column;
    - Rest is a `HoldButton` (a short press shows the review; the hold commits);
    - panes for smith (W1i), extract and install (W1j/W1k), the flask split (−/+ within the pool) and level up (+/− and the derived receipt);
    - Continue at a multi-use place or when a relic forbids Rest.
  - **W-15 `screen:runEnd`:** victory over the lit citadel, or death over the fight's region dimmed; stats, deck and unlocks; [Run history] refuses (W-16 is planned); [Return to title].
  - **Routing:** `RunFlow.Show` is the one router (`rules/runFlow.json#screens`, D-126). W-04 Begin opens W-06. W-07's end state offers [Claim rewards] or [Run summary]. W-08's Continue goes on to the map. W-20's Abandon run stays planned (D-134).
- **Data:**
  - `rules/runFlow.json`: `newRun`, `settings`, `screens` (schema updated);
  - `ui/screens.json`: `actMap`, `rest`, `runEnd` built; `merchant`, `event`, `dialogue`, `legacyDungeon` planned;
  - `ui/components.json#climb` (board metrics, backgrounds) and `art.include` (`env.*-map`, `bg.river-citadel-lit`);
  - map, rest and HUD tokens; strings `hud.*`, `map.*`, `rest.*`, `runEnd.*`, `glyph.node.*`;
  - codegen names in `ui.json` and `runflow.json`; the `planned` menu rule.
  - The default font lacks ⬡ and ⟲ (D-135).

**Tests**
- **`ClimbSessionTests`** (engine-free, 11 plus 3 explicit seed searches):
  - a new climb stands at the map with its start row reachable;
  - travel is legal only to reachable nodes;
  - the map view draws every node and edge; the short Custom Climb shape is valid;
  - **a seeded Classic climb (seed 2) reaches the act-3 summit through 29 fights, 7 rests, 31 reward doors, 3 treasures, a merchant, events and two legacy dungeons, loading the slot into a fresh session at every commit point (228 reloads, hash and location equal each time) and playing on from the loaded session;**
  - a climb ends in a death that clears the slot and names the killer;
  - the short climb the PlayMode smoke plays wins with the smoke policy;
  - a rest stay resumes on its streams, and its Rest is saved and refused a second time across a reload;
  - the level preview, the routes and the settings.
- **PlayMode `ClimbSmokeTests`** (76 s): Boot → title → New → W-03 → W-04 Begin → W-06, then a short seeded climb (D-137) played with the keyboard and pad:
  - map nodes are walked to and picked (select, then a repeat pick or the tray's Enter);
  - fights are auto-played through the screen's command path;
  - reward doors take Continue and its door;
  - rest places take a held Rest, then Continue;
  - the merchant, event and legacy-dungeon doors take their actions;
  - acts advance to the act-3 keeper and W-15 shows **victory**;
  - Save & quit → Continue is done once each at the map, a rest place, a reward door and mid-fight, each landing on the same screen with the identical state hash.
- **PlayMode `FirstFightSmokeTests`** follows the climb: Begin opens W-06 and the first node is picked with the keyboard. After the rewards the slot is at the map. The defeat test goes W-07 → [Run summary] → W-15 (**death**, the killer named) → title, with Continue disabled.
- `FirstFightUiTests` expects W-06 built and checks the new focus modes.
- **Results:** dotnet 2813/2813; Unity EditMode 2814 passed, 5 explicit skipped (the bot sweep and the seed searches); PlayMode 4 passed, 1 skipped (capture); `codegen --check`, `check-content`, `transform-content --check` and `check-docs` green.

**Screenshots** (docs/screens; 25 new PNGs): actmap_start (4 sizes), actmap_mid (tray open, 2), actmap_boss (legend open, 2), actmap_planned (1), rest (4), rest_smith (2), rest_flasks (2), rest_level (2), runend_win (4), runend_death (2); combat_end recaptured (3).

**Review** (own screenshot review against W-06/W-10/W-15; fixed and recaptured):
- The board's edges drew black: rgba tokens did not parse in `TokenColor`, which now reads rgb()/rgba().
- ⬡ and ⟲ showed as boxes (D-135).
- The start row was below the fold: the board centres on the run at first layout.
- In the compact band the discs overlapped once the touch minimum raised them. A floor is now never below the minimum × 1.15.
- The HUD name collapsed to "The F…": the identity now has a minimum width, and the meters are narrower.
- On narrow, [Menu] wrapped to its own line (it now sits top-right) and the boss labels piled up (now hidden; the tray names the path's end).
- The smith pane kept the shell's Continue (now hidden in panes) and repeated identical change lines (deduplicated).
- Ready buttons lost their green under focus (the W-08 rule was extended).
- The run end's unlocks overflowed (now a ScrollView), and long killer chips clipped on narrow (they wrap).

**Not verified / open**
- W-09 merchant, W-11 events and dialogue, and W-13 legacy dungeon belong to the node-screens stream. Until they land, the planned doors keep the run legal (D-126, D-127).
- No fog (D-128). Flasks outside combat are not ported (the Potions button refuses). There is no settings screen, so the hold and multi-use values come from the preset (D-132).
- US-13.1: W-15 shows the killer, duration and deck, but the profile's history rows are the shipped record (D-129). W-16 is planned.
- Mouse: node picks are exercised by Submit only. A pointer click takes the same `clicked` path, but no synthesized pointer test was written.
- Only the PlayMode smoke's short climb is played through the screens. The full-length Classic climb is covered engine-free.
- Abandon run needs an owner ruling (D-134).

**Next:** the node screens land on the seam (`RunFlow.Show` + the RunSession node APIs); a settings screen for the hold, multi-use and map-reveal settings; fog of war.

## us-5.11: deferred rule paths closed — mid-fight equipment, unrated Poise, run-creation options, the load door's derived stats (feature/combat-complete/us-5.11, 2026-09-26)

**Landed**
- **Mid-fight equipment (D-116, D-117).** Shipped `doSwapArmament` and `doChangeEquipment` are ported in the shipped order: guards, price and currency, the mutation, the property remount, the pool moves, the charge, the granted-card reconcile, four pile restamps, the Poise and ratings refresh, the events, and `swapEndsTurn`.
  - New intents: `CombatCommand.SwapArmament(slotId, setIndex)` and `ChangeEquipment(slotId, setIndex, pieceId)`. The wire form adds `slotId`, `setIndex` and `pieceId`.
  - The loadout.js model they need is `Run.Armoury` (canSwap, swapCostFor, canEquip, gripRefusal, cycleSet, openedSets, equipPiece, the storage transition, ownership, fitsSlot, the change payload), `Run.CombatEquipment`, `StartingDeck.StampPile` and `StartingDeck.ReconcileGrantedCardsInCombat`.
  - Combat reaches the model through `Combat.IEquipmentPort`, which `RunData` attaches to its `CombatData`. Combat data built alone refuses the intents by name.
  - Legality and prices: `CombatLegality.CanSwap`, `CombatLegality.CanChangeEquipment` (and `Check`), `CombatPreview.SwapPrice` and `CombatPreview.ChangePrice` (the shipped `swapCostFor` receipt). There are 13 new `combat.refusal.*` strings.
- **Application (data only).** `CombatViewState.Armoury` holds the hand slots, each set (item, active flag, price, cost, refusal, command), the carried pieces, the cost kind and the allowance left. `CombatViewModel.EquipmentChanges(c, slotId, setIndex)` lists the re-arm options. `CombatSession` runs the new commands through its legality gate unchanged. Presentation is untouched.
- **Unrated Poise (D-119).** `Combat.Equipment.PoiseThreshold` ports `playerPoiseThresholdReceipt` with combat ratings off. The data is in `rules/combatEngine.json#playerPoise`. createCombat, snapshot restore and both intents stamp it.
- **createCombat fallbacks (D-120).** A missing profile snapshot is created through the port. A missing or null swap-cost rule resolves to the configured default (`Equipment.ResolveSwapCostRule`).
- **Run creation (D-121).** Newly supported:
  - custom allocations (`CreationStats.NormalizeRunAttributes`, with the retired-id migration);
  - `derivedStatOptions` layers;
  - being born under a saved derived-stat snapshot;
  - hand-rule Settings (`RunCombat.ResolveHandRules`, with `rules/runEngine.json#handRuleSettings`).
- **Load door (D-122).** `RunState.RestoreDerivedStats` ports initializeRunDerivedStats' restore and migration branches: current-snapshot validation, the v4 pool-bonus inference, deficit capture, the v3 max-HP adjustment and re-derivation under an older ruleset. `DerivedStats.RestoreSnapshot` ports restoreDerivedStatRuleSnapshot.
- **Keys (D-123).** Shared names were moved to the earlier fragment. `combat-keys.mjs` now also reads event names from `Domain/Run`.
- **Decisions.** D-041 and D-043 now list what is closed. D-063 and D-064 note that the swap is ported.

**Oracles**
- `Tools/oracle-combat.mjs` gained 48 swap combats (`swap-*.json`) and five registry overlays (`registries-<variant>.json`), and `registries.json` now carries the run tables.
  - Counts: 1,124 steps, 49 swaps, 141 changes and 14,182 recorded options (7,985 refused, 11,250 priced).
  - Configurations: shipped, ratings off, swaps end the turn, changes locked, equipment off, and the allowance content edit.
  - The 60 existing combat files are byte-identical. Added about 11 MB.
- `Tools/oracle-run.mjs` gained 91 creation runs, 69 refusals and 74 load-door cases (`restore-*.json`; 36 are refused, 2 are the shipped `tests/fixtures/run-save-*.json`). The 76 existing run files are byte-identical. Added about 8 MB.
- **Coverage** (`--coverage` on both oracles). Every newly ported shipped function executed:
  - combat.js `doSwapArmament`, `doChangeEquipment`;
  - loadout.js `canSwap`, `swapCostFor`, `canEquip`, `gripRefusal`, `cycleSet`, `openedSets`, `ownership`, `equipPiece`, `equipTransitionPlan`, `applyEquipTransition`, `reconcileGrantedCardsInCombat`, `loadoutSignature`, `equipmentChangedPayload`, `createEquipmentProfileRuleSnapshot`;
  - statProjection.js `playerPoiseThresholdReceipt`;
  - attributes.js `normalizeRunAttributes`, `migrateRetiredAttributeNames`, `grantedAttributePoints`;
  - derivedStats.js `restoreDerivedStatRuleSnapshot`;
  - handRules.js `resolveHandRules`;
  - state.js `initializeRunDerivedStats`.

**Tests**
- New `CombatParityTests` cases:
  - `SwapReplayMatchesTheShippedEngine`: projection and previews every step; events and the equipment projection on each equipment step; every recorded option (legality, the exact shipped message from a restored clone, the price); the final state.
  - `SwapCreateCombatMatchesTheShippedStart`.
  - `SwapSnapshotRoundTripsAndResumesIdentically`: includes a wire-form round trip.
  - `ArmouryViewAndSessionAgreeWithTheShippedOptions`: the view model and `CombatSession`.
  - `TheSwapLogsCoverEveryDoor`.
- New `RunParityTests` cases:
  - `LoadDoorMatchesTheShippedRestore` and `TheLoadDoorOracleCoversEveryShape`;
  - the hand-rule settings on args and starts;
  - `DeferredCreationPathsThrowByName`, which now checks only Custom Climb.
- `ContentRunDataTests` passes the recorded settings. `FirstFightUiTests` formats refusals with four arguments.
- dotnet: 2,802 of 2,802 passed in about 6 m 15 s (2,101 before this story).
- Mutation check: twelve mutations, each caught, and each file restored byte for byte (sha256 checked).
  - Combat side, of the 144 swap tests: energy charge +1 (62 failed); category price ignored (12); displaced piece not stored (40); granted-card reconcile skipped (58); the unrated Poise attribute term dropped (24); the profile-snapshot fallback altered (6); the swap-rule fallback nulled (6).
  - Run side, of the 655 run tests: retired-id migration skipped (16); run-modifier layers dropped (28); the hand-rule clamp removed (8); the inferred pool bonus +1 (10); the snapshot envelope check removed (4). `codegen --check`, `check-content`, `transform-content --check` and `check-docs` are green.

**Not verified**
- Unity EditMode and PlayMode (not run).
- The grip refusal's sentence: no shipped armament is two-handed, so `gripRefusal` runs but never refuses.
- The phase and no-loadout refusals: no recorded option reaches them.
- The rest of shipped `loadRun` beyond the derived-stat door is not wired (D-122).

**Next**
- Bind `CombatViewState.Armoury` in the W-07 Armaments popover (D-010).
- Wire the Application load door.
## us-8.11: domain unification — one smith port, one item resolver (feature/domain-unify/us-8.11, 2026-09-26)

**Landed**
- **One smith port (D-112).** The rest stop's smith (`Loop/Smith.cs`, `Loop/CardServices.cs`, `Loop.SmithServices`) and the merchant's (`Shop/ItemSmithing.cs`, `Shop/CardExtraction.cs`, `Shop/SmithServices.cs`) were two ports of shipped smithing.js, smithingRules.js `normalizeServices` and cardExtraction.js. The shop copy is now the only one.
  - `RestVisit` (Smith, Extract, Install, the refusals and the arrival's `smithServicesAt`) calls it with `LoopData.Shop`. The event door already did (D-085).
  - The shop copy was kept because it has the shipped return shapes and messages. Five behaviours only the loop copy had were merged into it:
    - JS `===` on receipt values;
    - `?? null` on a card's numeric effect;
    - `itemByRef` through `resolveUpgradedItem(…, 0)` inside a catch;
    - the commit receipt's item name read from the owned piece;
    - `rng.chance` for the smith roll.
  - `rules/loopEngine.json` loses its `smithing` and `mounts` sections, and the schema and manifest follow. 52 LoopKeys, 13 LoopValues and 34 LoopMessages go.
- **One `resolveUpgradedItem` (D-113).** `Combat.Equipment` now holds the faithful resolvers: tags checked per item kind, and poise and passives never below zero. The smith's private copy is deleted. 4 CombatMessages are added. The ref regexes, their group names and `ItemUpgrades.Parse` are gone.
- **Smaller readers with one home each (D-114):**
  - `CardMounts.IsExtraMountKey`/`OpenExtraMountKey`;
  - `StartingDeck.ItemMountInstances` (the shop's copy is deleted);
  - `Rewards.Smithing.StoneBalance` (three copies before);
  - `Creation.TierOf` (`MapSeats.TierOf` is deleted);
  - `ItemUpgrades.CostField`.
- **Scan:** every shipped function name in the Domain's and Application's doc comments, checked for more than one port. The quest-history gate (`Events.Quests` and `Map.EventHistory`) is left for its own story; `rollDrop` and `drawSeatOrder` are adapters over one implementation (D-115).
- **Size:** 34 files outside the docs, +173 −1,468 lines. In the Domain: +146 −1,022.

**Tests**
- dotnet 2,101/2,101 in about 3 m 48 s (2,101 before; nothing added or removed), the explicit sweeps excluded. No oracle file changed.
- `RunLoopParityTests` now drives the shop port through the loop oracle's rest stays: 446 smith, 175 extract and 155 install commits, plus 82 smith, 131 extract and 126 install refusals. `ChainedRunTests`, `BotRunTests`, `ShopParityTests`, `EventsParityTests`, `RewardsParityTests` and `RewardClaimsParityTests` are green.
- `BotRunTests` reads the smith plan from `Shop.ItemSmithing`, the only change to a test.
- Mutation check: `receipt.transaction` in the shop's `CommitExtraction` was set to n + 1 and the loop and chain tests run. 30 of 219 failed, each at the rest step's `$.lastMountReceipt.transaction` (1 vs 2). The file was restored byte for byte (sha256 checked).
- `codegen --check`, `check-content`, `transform-content --check` and `check-docs` are green.

**Not verified**
- Unity EditMode and PlayMode (not run). Presentation, Platform and the PlayMode tests were grepped for every removed generated name and API; none uses one.
- A malformed upgrade tier now throws in combat too (D-113). No valid content reaches that path, and no test covers the throw.

**Next**
- Unify the quest-history gate (D-115).
## Promotion: F1 first playable loop (v0.1.13.0, 2026-09-26)

- `dev` 0.1.13.0 promoted to `test` (CI green, incl. build-windows and bot jobs) and to `main`, tagged `v0.1.13.0`. No version change and no owner approval implied (D-039).
- F1 exit gate (09 §8): boot → title → slots → class → fight → rewards → save & quit → resume mid-fight and on rewards, with keyboard, pad and mouse (PlayMode smoke, local batchmode: EditMode 2102 passed / 2 skipped, PlayMode 3 passed / 1 skipped).
- Open owner questions: the reference preset Reaver flask allocation (D-073) and cinders granted on arrival vs US-11.1 (D-105).
- CI Unity jobs still skip without the `UNITY_*` secrets; local batchmode remains the Unity gate.

## us-11.1–us-11.3, pf-06: W-08 rewards and the F1 loop closed (feature/first-fight/us-11.2, 2026-09-26)

**Landed**
- **Merged `origin/feature/run-loop/main`, then `origin/dev` 0.1.12.0** (the run loop and shop/events integrated) into the story branch, and W-08 is built on its `RewardDoor` and `RunLoop.EndCombat` (D-104). An earlier `Ashen.Domain.Rewards.RewardClaims` port was dropped as a duplicate before commit; its oracle now verifies `RewardDoor`.
- **Claims parity:** `Tools/oracle-claims.mjs` is a faithful harness of `reward.js`' run-writing handlers plus `main.js` onDone/collectArmament, over the real shipped model functions. Its inputs are the 102 pending-reward checkpoints in `Oracle/rewards`, run through 356 scripts: claim each row, auto and manual sweeps, partial then reload then auto, a blocked belt and bag with Skip, a spent draft and a duplicate armament. The files are in `Oracle/claims` (8 MB); a self-check replays each script byte-identically.
- **Application (`Ashen.App.Run`):**
  - `RunSession` runs the post-combat door with the command that ends the fight (D-107). A win saves at the rewards (`location: rewards`); a death writes the profile's run record and clears the slot.
  - `Door`, `ClaimReward`, `SkipReward` and `FinishRewards` each save at once (PF-06 "reward claimed"). A failed save restores the run and the profile.
  - The `rewardCollect` and level-up dials come from the profile, then the preset (D-106).
  - After Continue the save is at `location: map` (D-108). The run has main.js newRun's additions (D-109), and `run.streamCounters` is stamped on saves outside a fight.
  - `ProfileStore` is the profile slot.
  - `RewardsView` is the W-08 display data: rows, states, sentences, picks, icons, the head count, the waiting choice and the Continue hint (D-111). It also holds `rewardClaimStatus` and the claim refusals.
- **Presentation:**
  - **W-08 `screen:rewards`** (W1t):
    - The head reads "{claimed} of {total} claimed", with no close control.
    - Reward rows have an icon or glyph, a title, a body and a state chip. A blocked row has Skip.
    - The claim-status column lists every row and the waiting choice; it stacks below the list on narrow screens.
    - The pick sub-state has its own focus mode (`focusModes.pick`): three CardViews (larger in compact, art hidden) or class-tree node tiles. Select, then Confirm; Back; Skip on the card offer only.
    - Continue is full width: gold while rows are open, with a door stating what the setting will do; green with "✓ All claimed" once everything is resolved (D-110).
    - Refusals come from `strings rewards.refusal.*` at the control that asked. Escape leaves the pick, else opens W-20. Pad Start opens W-20.
    - Save & quit on W-08 checkpoints and returns to the title.
  - **W-07 end state:** a win shows [Claim rewards] (green, focused) and opens W-08; a death shows "Your climb has ended. Slot n is free again." with only [Return to title].
  - **Resume:** title Continue and W-03 Load land on W-08 when the slot holds a pending reward. A post-reward slot refuses with the later-build note.
- **Data:**
  - strings `rewards.*`, `confirm.rewardLeave.*`, and the combat end strings and glyphs;
  - `ui/screens.json#rewards` (built) and the confirms `rewardLeaveAuto`/`rewardLeaveManual`;
  - `ui/components.json#rewards` (icon templates, relic modifier tokens) and `art.include` (relic icons, armament icons, flasks);
  - reward tokens and `rules/runFlow.json#newRun`, with schemas extended by hand;
  - codegen names in `ui.json` and `runflow.json`.
- **Screenshots:** the new fixtures are rewards (4 sizes), rewards_partial, rewards_pick and rewards_draft (3 sizes each), and rewards_done (1280×720 only); combat_end was recaptured. That is 17 PNGs.

**Tests**
- **Claims parity:** `RewardClaimsParityTests` (104 tests) replays the 102 claim files through `RewardDoor`, strictly, and checks a coverage tally and the session's refusals.
  - Mutation check: four corruptions each failed exactly one test, and the failure named the path. They were an expected run's cinders, the order of two expected run keys, one take's `ok`, and one RNG counter. Each file was restored byte for byte (sha256 checked).
- **`RewardsSessionTests`** (7):
  - the smoke seed wins the first fight with a card offer and a flask;
  - a win resumes at the rewards, and the cinders are granted once;
  - claims, skips and refusals are saved and never applied twice across a reload;
  - manual Continue leaves the rest and resumes at the map;
  - auto takes every unskipped row;
  - a death writes the profile's record and clears the slot;
  - the view reads all 102 recorded offers (every kind) with resolved titles and picks.
- `RunSessionTests` has a new test in place of the old finished-fight one: a finished fight is checkpointed at its rewards, or cleared by a death. `FirstFightUiTests` now expects W-08 built with its pick focus mode, and the act map still planned.
- **PlayMode `FirstFightSmokeTests`** extends the F1 flow: … → the fight won (seed pinned through `RunFlow.SeedOverride`) → the end state → Claim rewards → W-08.
  - The cinders are taken on arrival.
  - The card offer is claimed: the keyboard opens the pick, Submit selects, a synthesized pointer release on the last card moves the selection, and Confirm takes it; the deck gains it.
  - The flask is claimed with the pad.
  - Escape opens pause, then Save & quit (hold) → title → Continue → the load door → W-08 with the same claim count and row states, and the card not granted twice.
  - Continue → title; the slot then holds no pending reward and is at `location: map`.
  - A second test, **defeat**, ends every turn: the run is closed out, the slot cleared, no rewards are offered, and the title's Continue is disabled.
- **Results:**
  - dotnet 1977/1977 after the dev 0.1.12.0 merge (1325 before it);
  - Unity EditMode 1978 passed, 1 explicit skipped (the seed search), after the merge;
  - PlayMode 3 passed, 1 skipped (capture);
  - `codegen --check`, `check-content`, `transform-content --check` and `check-docs` green.

**Review** (own screenshot review against W-08; fixed and recaptured):
- the pick cards stayed at hand-card size (the kit's `.cardview` rule won; the selector is now scoped);
- at 844×390 the claim column squeezed its names to one letter per line (the rows stack name over state there), and the compact cards overflowed their text once the physical minimum raised it (larger compact cards, art hidden);
- a focused ready Continue lost its green (the focus glow now sits on top of the green inside W-08).
- The minimum text size holds, nothing overlaps, and focus is visible on rows, cards, tiles and Continue at all sizes.

**Not verified / open**
- **D-105 needs an owner ruling** (arrival cinders vs US-11.1).
- Relic sentences bind property-rule numbers by op, not by variable (D-111); a mismatched token keeps its braces.
- Not built:
  - the 'new' markers;
  - the progression panel (XP gains);
  - the inspect door for flask, armament and relic;
  - a skill-draft (card) pick capture (the draft fixture is a class draft);
  - boss rewards' `advanceAct`;
  - W-15 run end.
- **Mouse:** only card selection is exercised by a synthesized pointer event; buttons are driven by keyboard and pad.
- `custom` is omitted from new runs until fights enter through the run loop (D-109).

**Next:** W-06 act map and the run-loop screens (rest, merchant, events) on the ported domain; W-15 run end.
## us-8.10: run integration — one run-effects door, C# newRun, chained runs, bot smoke (feature/run-integration, 2026-09-26)

**Landed**
- **One run-level effect door (D-100).** The events port's `RunEffects.Execute` (D-085) and the run loop's `RunEffectContext` (D-093) were two ports of the shipped `createRunContext`/`executeRunEffects`. They are now one: `Ashen.Domain.Events.RunEffectContext` + `RunEffects`.
  - It keeps both behaviour sets: the full run-opcode vocabulary, the rest heal scale, `Sync`/`Drain`/`EmitAndDrain` for mounted location rules, and the script deferral.
  - `refillFlasks` is now an opcode of the door (`RunEffects.GraceRefill`), which closes D-088's deferral.
  - Both the rest visit and event choices run on it; Loop's copy and `GraceRefill.Apply` are deleted.
  - `LoopData` now wraps `EventsData` (and through it `ShopData`); `ToLoopData` takes the shop and events rules.
  - `loopEngine.json#runContext` is removed (the guard is `eventsEngine.json#effects.queueGuard`).
- **newRun and startClimb (D-102).** `RunLoop.NewRun` ports main.js 989–1094's run-writing half, with Classic defaults in `rules/loopEngine.json#newRun`. Keepsakes run through the unified door. Sealed and Draft decks throw by name.
- **Oracles.**
  - `Tools/oracle-loop.mjs` records the external steps' inputs (event id, quest flag, open choices, the choice taken) and newRun's inputs (`start`, plus `resolved` config snapshot, derived-stat options and prologue flag). Every other byte is unchanged (checked against HEAD for all 108 sequences).
  - `Tools/oracle-events.mjs` appends 8 `refillFlasks` lists. The existing 420 sessions are unchanged.

**Tests**
- `ChainedRunTests` (D-101), 109 cases. Each of the 108 recorded runs is made in C# (`NewRun` equals the recorded start: run, RNG, profile) and played through one `LoopContext`. That covers every step, including the 489 external ones: 182 merchant visits through `Shop.Open` + Leave, and 307 events through `Events.Open` (open choices checked) + `Events.Choose`, with 28 event fights handed to `RunLoop.EnterEventCombat`. The documents are compared strictly after each step.
  - **Result: 108/108 chain end to end. No integration divergence was found.**
  - Mutation checks (each file restored): skipping the event choice (and the merchant roll) fails the sequences at their first x-step (`$.history`, `$.cinders`, `$.hp`, `$.maxHp`); skipping the keepsake in `NewRun` fails the veteran sequences at their start (`$.cinders`, `$.flaskCharges.capacity`, `$.itemUpgradeLevels`).
- `BotRunTests` (D-103): a seeded C# bot over the default preset (reference: Starseer, Rogue and Herald can begin; the Reaver cannot, D-073) and the shipped preset (all 4 classes).
  - Default suite: 7 tests, about 12 s. That is 3 seeds × 3 classes × {fair, assisted}, each played twice for the hash, plus 4 shipped runs. A run takes 30–500 ms.
  - Explicit sweep (`--filter TestCategory=Bot`): 144 runs, each replayed, in about 54 s. Outcomes: assisted 54 victories / 18 defeats, fair 10 victories / 62 defeats. Doors reached: 2,131 fights, 598 rests, 321 treasures, 174 merchants, 318 events, 71 legacy dungeons, 107 act advances.
  - Every run is terminal and deterministic, with the invariants holding at every step.
- `EventsParityTests` now 433 (8 refill lists; the deferral test checks only the legacy refill). `RunLoopParityTests` unchanged and green.
- dotnet 1,990/1,990 (enforcement included; the explicit sweep excluded), about 3 m 44 s; `codegen --check`, `check-content`, `transform-content --check` and `check-docs` green.

**Found**
- Two copies of smithing remain: `Ashen.Domain.Loop.ItemSmithing` (rest smith) and `Ashen.Domain.Shop.ItemSmithing` (merchant smith, event `upgradeCard`). The same holds for card extraction (`Loop.CardServices` / `Shop.CardExtraction`). Both copies are oracle-green and the chains agree, but they should be unified the way the effect door was.
- A random heal-and-block card policy can hold a fight forever: the shipped combat has no turn cap. The bot plays aimed cards first.

**Not verified**
- Unity EditMode (not run).
- The chains cover the shipped preset only (the loop oracle's).
- The merchant in the chain buys nothing (the harness buys nothing). Purchases are covered by `ShopParityTests` and exercised, unverified against shipped, by the bot.

**Next**
- Unify the smithing and card-service copies.
- The Application layer drives `RunLoop.NewRun` and the loop in place of F1's first-fight-only `RunSession.New`.
- Raise the bot sweep toward F4's 200 seeds once journeys and settings resolution land.

## us-8.1, us-8.3–8.6, us-4.4–4.9, us-13.1, us-13.2: the run loop with shipped parity (feature/run-loop, 2026-09-26)

**Landed**
- `Ashen.Domain.Loop` (D-089): the run-writing half of the shipped `main.js` controller between fights, ported line by line in the shipped order and on the shipped RNG streams. Scope was taken from V8 precise coverage of the shipped calls (`node Tools/oracle-loop.mjs --coverage`):
  - travel: `RunLoop.EnterNode` (path, floor, Unknown resolutions, the field camp), `StartFight` (Elite Gauntlet, boss destinations including the causeway's no-seat boss, the recent-encounter window), `EnterCombat` (the createCombat arguments under `CombatMods`: Tough Elites, Dread Bosses, Deadly Foes, Glass Cannon, the seat tier ratio, Endless loops) and `EnterEventCombat`. Merchants and events come back as outcomes for the shop/events stream;
  - fights' ends: `RunLoop.EndCombat` over `CombatEnd.Apply`, closing a death or the summit out through `RunEnd.Finish`;
  - the reward door: `RewardDoor` (arrival cinders, auto and manual Continue with the pick on `cardRewards`, drafts via `pickClassNode`/`spendSkillDraft`, flasks and relics with flask growth, `collectArmament` with the profile's found set and discovery receipts, `meta.seen`), then `Acts.Advance` after a boss;
  - rest places: `RestVisit` over the location carrier (tags from tagging, `restMana` resolved by mode, services, `restDenied`, `restHealMult`, Scarce Embers), the arrival's flask refill, `previewRest`, Rest, the flask re-split, level points (`applyLevelUp`), Smith (`ItemSmithing`: plan, commit, restamp, value receipts, `lastSmithingReceipt`), Extract and Install (`CardServices`, `lastMountReceipt`), single-use and multi-use stays;
  - acts: `Acts.Advance` (full or halved heal, the next seat's map, Endless content loops);
  - legacy dungeons: `LegacyDungeons` (the entrance in place of the boss fight, rooms, dialogue with listen/fight/flee on `events`, caches, shrines, retreat, leaving to the summit or the next act) (D-097);
  - the run's end: `RunEnd.Finish` (`runResult` with the subclass name and the class peak, `recordResult` capped at 20, `recordProgress`, `evaluateUnlocks`) (D-092).
- Combat port: smithed card faces resolve (`ItemUpgrades`, D-096); the run-level context hooks `HealMult`/`RunOpcodes` and the location carrier mount (D-093). Run port: `RunCombat.Arguments` (combat modifiers) and `StartingDeck.ItemMountInstances`. Rewards port: `CombatEndOptions.DungeonResolver`, `BeginPendingReward` made public.
- Content: `rules/loopEngine.json` (hand-written, schema inferred, in the manifest) and the item-upgrade vocabulary in `rules/combatEngine.json`. Names: `Tools/codegen.d/loop.json` via `Tools/loop-keys.mjs` (169 keys, 68 values, 60 messages), reusing the combat, run, map and rewards names. `RuntimeRegistries.ToLoopData`.
- `Tools/oracle-loop.mjs` (D-090): 108 sequences and 14,983 steps.
  - 2,383 fight entries: 62 summit victories, 31 deaths, 5 Endless runs capped at act 5; all ten boss encounters, the causeway boss 29 times.
  - 3,079 reward doors (auto and manual), 126 act advances.
  - 740 rest stays: 340 Rests, 182 Smith commits (7 on armour), 22 extractions, 12 installs, 182 flask moves, 138 level assignments, 73 relic-denied Rests.
  - 91 legacy dungeons (all three) with 1,025 dungeon steps.
  - Regenerations are byte-identical. Size: 12 MB.

**Tests**
- `RunLoopParityTests`:
  - 108 sequence cases. Each checked step starts from the recorded documents (run, RNG counters, profile); its outcome and the three documents after it must equal the shipped ones strictly (presence, values and key order). Fights are replayed from their command logs in `CombatEngine`;
  - an oracle-presence test (tally floors per door and outcome);
  - a deferred-paths test (journey travel and rest, and the legacy refill, throw by name).
- Mutation check (each file restored byte for byte, sha256 checked):
  - the recent-encounter trim `>` changed to `>=` failed 98 sequences at `$.lastEncounters`;
  - ignoring the rest heal scale failed 34 at `receipts[..].preview.heal`;
  - the flee roll on `misc` instead of `events` failed 35 at the dungeon receipt's roll text or action;
  - swapping two run-record keys failed 108;
  - a corrupted profile patch in the oracle failed exactly that sequence, at `$.seen.flasks[0]`.
- dotnet 1,188/1,188, enforcement included; `codegen --check`, `check-content`, `transform-content --check` and `check-docs` green.

**Not verified**
- Unity EditMode (the UI stream owns Unity).
- The reward door's tap path (`Take`/`Skip`) is not in the oracle (D-098).
- Settings resolution is the caller's (`LoopSettings`).
- Chains are checked step by step from the recorded documents, not as one uninterrupted C# run.

**Deferred**
- World Journey (D-091); the legacy flask-slot refill (D-094); the run-level opcodes beyond `refillFlasks`, which the shop/events stream owns (D-093); a `rules/victory.json` and a lit-towers ledger (D-095).
- Shipped defects recorded, not fixed (D-099).

**Next**
- Integration with the shop/events stream (one `executeRunEffects` context).
- The UI binds `NodeOutcome`, `CombatOutcome`, `RewardDoor`, `RestVisit` and `RunEndReceipt`.
## us-10.1, us-10.2, us-10.3: events with shipped parity (feature/shop-events, 2026-09-26)

**Landed**
- `Ashen.Domain.Events` (D-084 to D-088), a line-faithful port of the run-writing half of the shipped event door:
  - `Events.Open`: the event and dialogue screens' facts — the visible choices (model/quests.js `availableEventChoices` over content/events.js `eventChoicesWithHistory`), each with its price (`choiceAffordable`), binding reasons (model/consequence.js, fail closed) and refusal key, the choices history hides, a quest step's speaker with its portrait check, and the head's status;
  - `Events.Choose`: engine/quests.js `commitEventChoice` — the three refusals, then the choice's effects through the run-level door (`RunEffects.Execute`, engine/actions.js `executeRunEffects` over a player facade on the combat interpreter: cinders, cards in and out with attack slots retired, card upgrades through the merchant's smithing, relics, flasks, flask capacity, max HP, fights, the class swap, and damage/heal/loseHp through the facade), the history row and quest completion at most once per quest;
  - `Events.Finish`: main.js onDone — a fight the choice started leaves `run.combatEntered` and is returned as its encounter id for the run loop's enterCombat;
  - model/classSwap.js `swapRunClass` (the Turncoat's Mirror): class tracks reset, tree picks pruned, armour the new class cannot wear set aside, the deck restamped, a `classSwapped` history row, the zones projection.
- Content: `rules/eventsEngine.json` and `strings/events.en.json` (hand-written, schema'd, in the manifest); `catalog/eventChoiceRequirements.json` exported from the shipped content/events.js; the speakers catalog is now a runtime registry; `RuntimeRegistries.ToEventsData`.
- Names through `Tools/shop-keys.mjs`: 35 event keys, 32 values, 24 messages and the EventStringKeys table, reusing the shop, combat, run, rewards and map names.
- `Tools/oracle-events.mjs` records 420 sessions on the shipped and reference presets: the sweep (every one of the 25 events' 71 choices, opened and shut: 284), walks (19 run variants — purses, low HP, upgraded and smithed-out decks, owning every relic, each quest step taken, a completed quest, a looted cart, a malformed history row, a class ready to swap, carried armaments, act 3 — × 2 classes × 8 visits: 76) and 60 run-effect lists. 808 commits (every event × choice), 84 refusals (all three keys), 75 fights handed on. Byte-identical on re-run. V8 block coverage over the 27 ported functions: 125 of 218 blocks ran; the rest are defaults on present content (17), throws on malformed content or state (16), the malformed-requirement and malformed-ref validation the shipped content never triggers, combat opcodes the run door never receives from events (parity-tested in combat), the foundation ruleset the shipped game does not create, and the deferred `refillFlasks` and scripts.

**Tests**
- `EventsParityTests`, 425 cases: 360 sessions replayed on content-built data (every view, outcome or refusal, event log, the run after every choice and after onDone, the RNG counters), 60 run-effect lists (run, events, counters; the unknown op throws), the content-built event tables against the shipped registries on both presets, oracle presence, refusal texts and the deferred ops throwing by name.
- Mutation check (shop and events; each file restored byte for byte, sha256 checked): a cinder value after a purchase, two stock keys swapped, the `shop` counter and a Smithing receipt's balance each failed exactly one shop session, naming the path, the key order or the stream; rounding the price multiplier down failed 16. A cinder value after a choice, two view keys swapped, the `misc` counter and a refusal key each failed exactly one event session; removing the cinder floor failed 4.
- dotnet 1730/1730, enforcement included; `codegen --check`, `check-content`, `transform-content --check` and `check-docs` green.

**Not verified:** Unity EditMode. Malformed requirement groups in content are refused as shipped but not recorded (the shipped content is valid).

**Deferred:** `refillFlasks` and script effects (D-088); a journey's `atlasQuestAction`.

**Next:** the event and dialogue screens (W1u, W4c) over `Events.Open`/`Choose`/`Finish`; the run loop calls `Events.Open` from enterNode for an Unknown node's event.

## us-9.1, us-9.2, us-9.3: the merchant with shipped parity (feature/shop-events, 2026-09-26)

**Landed**
- `Ashen.Domain.Shop` (D-078 to D-083), a line-faithful port of the run-writing half of the shipped merchant:
  - main.js enterNode `'merchant'` and `shopPriceMult`: the stock on the `shop` stream (engine/encounters.js `buildShopStock`, `rollShopCards`), the Greedy Merchants / Hoarder multiplier on cards, relics, flasks and the removal, and the smith's roll on the `smith` stream, all stored on `run.shopStock` (it persists with the run);
  - the shop screen's handlers (ui/screens/shop.js), UI-free: buy card / relic (with `syncFlaskGrowth`) / flask (flask belt cap), armament and weapon-art trades with inert quotes and revalidating commits (model/armamentTrading.js), the card burn with its rising price (model/cardRemoval.js, attack slots retired in order), the sell shelf behind `shopSell` (relics, flasks, armaments), and Leave;
  - the smith the merchant keeps: `smithingPlan`/`commitSmithing` with exact card receipts (model/smithing.js), the service table (model/smithingRules.js `normalizeServices`, closing D-065) and `extractionPlan`/`installPlan`/`commitExtraction`/`commitInstall` (model/cardExtraction.js with `mountRows` and `itemMountInstances`);
  - `Shop.View`: every offer with its price and availability, the burn grid's cards, the smith's candidates and the sell shelf.
- Smithed card faces now resolve in combat (`Cards.Resolve` applies the tier rows; D-080).
- Content: `rules/shopEngine.json` and `strings/shop.en.json` (hand-written, schema'd, in the manifest); `rules/combatEngine.json#itemUpgrades` gains the tag grammar lists; `catalog/eventChoiceRequirements.json` is exported from the shipped content/events.js (for the events story). `RuntimeRegistries.ToShopData` builds the merchant's data from content.
- Names from `Tools/codegen.d/shop.json` via the new `Tools/shop-keys.mjs` (covering Shop and Events): 102 keys, 36 values, 27 messages and the ShopStringKeys table, with combat, run, rewards and map names reused.
- `Tools/oracle-shop.mjs` records 208 sessions (4 classes × 26 run variants × the shipped and reference presets) × 14 steps: purses, relics, full and part flask belts, full and part inventories, Smithing Stones, emptied, worked and carried mounts, smithed items, legacy armament levels, removals bought, a thin deck, Greedy Merchants / Hoarder / Ascension, the sell toggle off, no loadout, a fractional purse, edited shelves and acts 1–3. Accepted: every action kind (buy card 152, relic 92, flask 130, armament 96, weapon art 96, burn 146, sell relic 21 / flask 22 / armament 19, smith upgrade 82 / extract 35 / install 31, leave 72); refused: every strings/shop.en.json key but the two a shop can never reach (`noTrader`, `notBuying`), plus the screen's three availability ids. Byte-identical on re-run. V8 block coverage over the 62 ported functions: 278 of 400 blocks ran; of the 122 that did not, 49 are defaults on always-present content, 35 are throws on malformed content or state, and the rest are features the shipped content does not author (card-cost upgrade tags, extra mounts, armour mounts, smith chances of 0 or 100, a missing services table) or nulls unreachable by construction.

**Tests**
- `ShopParityTests`, 211 cases: the 208 sessions replayed on content-built data for their preset — the stock, every receipt or refusal, the run document after every step (strict: presence, values, key order), the shop view and the RNG counters (no shop action draws); an oracle-presence test (both presets, every action kind accepted, every reachable refusal key recorded); every refusal key resolves to text; the deferred journey shop throws by name.
- Mutation check: in the us-10.1 entry.
- dotnet 1305/1305, enforcement included; `codegen --check`, `check-content`, `transform-content --check` and `check-docs` green.

**Not verified:** Unity EditMode. The shipped merchant's role-source branch of `sourceArmamentId` (a role instance with no source key) and the legacy level conflict are ported but not recorded.

**Deferred:** a World Journey's atlas shop service (D-083).

**Next:** the shop screen (W1d) over `Shop.View`/`Shop.Execute`; the run loop calls `Shop.Open` from enterNode.
## us-2.1 (Class pane), us-5.1–us-5.10, us-6.x display, us-17.1, pf-06: F1 first fight, pause and resume (feature/first-fight, 2026-09-26)

**Landed**
- **Application (`Ashen.App.Run`, engine-free):**
  - `RunContent`: the layered content (default preset unless named, optional patches), run and combat data through `RuntimeRegistries.ToRunData` (D-069), `rules/runFlow.json` and the seed codec; `ClassProblem`, `DefaultClass` and `Preview` for W-04; `FirstFightEncounter` and `RegionOf` from data (D-071).
  - `RunSession`: New (createRunState) → StartEncounter (enterCombat args) → Execute through `CombatSession` with autosave (append each command; checkpoint on end turn, fight end and Save & quit) → Save (versioned payload with the D-059 summary) → Load (hash-verified, log replay, D-017 fallback with a warning, re-commit) (D-070).
  - `CombatSession.Commit` and a checkpoint + log `Load`; `CombatOutcome.EnemyActors`; `CombatViewModel` flasks (charge pools and carried flasks, with refusals); `CombatText` (refusals from `strings/combat.en.json`, intent chips with live numbers, card kinds).
  - UI data: `screens.json#focusModes`; `menus.json#pause` (with `hideInCombat`) and `#creationPanes`; new doors `replaceSlot`, `saveQuit` and `saveFailed`; `components.json#combat` art templates and wildcard art includes (D-076). The combat strings join the string table.
- **Presentation:**
  - **W-04 creation, Class pane:** the four classes in authoring order with portrait, name and summary. The selected class unfolds with its attributes, pools and flask split, read from createRunState. The rail shows Class plus the planned Character, Equipment and Review (D-058). Begin (W2c Replace when the slot is occupied) → `RunSession.New` → first fight → W-07. The Reaver cannot begin under the reference preset (D-073).
  - **W-07 combat:**
    - HUD: portrait, identity, act · region · turn, labelled HP/MP/SP meters, Menu.
    - Battlefield: the new `Combatant` kit element (intent chip coloured by kind, figure art, Block badge, HP, labelled Poise and Ward meters, stance, status chips with `+N`, staggered and defeated badges) over the region's combat art.
    - Hand fan of CardViews (cost pips, resolved text, kind, refusal line and refusal tooltip) and the footer group (Actions, Draw, End Turn, Discard · Exhaust, Potions).
    - States with their own focus orders (D-074): targeting (legal targets outlined with a ghost HP preview), the discard chooser when the hand rules prompt, the flask panel (targeted flasks arm the target layer), the enemy turn (red wash, the acting enemy lifts in order, Skip ▶▶, reduced motion; D-075) and the end state with rewards planned (D-072).
    - Every intent is a command through `RunSession`; a refusal shows at the control that started it and keeps focus there.
  - **W-20 pause:** seed and slot; rows from data (Deck, Settings and Abandon planned; Armoury hidden in combat); full-width Resume. Save & quit goes through its hold door → checkpoint → title; a failed save opens W1r.
  - Title **Continue** and W-03 **Load** resume the slot mid-fight (`RunFlow.Resume`); W-03 **New** opens W-04 (D-077). Pad Start is a new router intent (Menu).
- **Screenshots:** 8 new fixtures (creation, combat, combat_intents, combat_targeting, combat_discard, combat_refusal, combat_end, pause): 27 PNGs, about 18 MB. The five state fixtures skip 1920×1080 through the new `skipSizes` fixture field. Fixtures can name layer patches, an encounter, bot commands and a screen state.

**Tests**
- `RunSessionTests` (16):
  - content-built runs and combat args equal 8 recorded shipped runs, and the creation tables equal the shipped registries;
  - the first fight is the data rule's encounter, and every class that can begin starts it under the default preset;
  - new → fight → save → load gives the identical state hash;
  - resume after 1, 3, 7 and 12 commands equals the live fight and plays on identically;
  - the append/checkpoint policy; a refusal is neither applied nor saved;
  - a tampered log and changed content resume with a warning and re-commit;
  - empty, newer, foreign and damaged payloads; the live slot summary; a finished fight is checkpointed.
- `FirstFightUiTests` (10): every combat refusal resolves; intent text; the first fight's view (two targets, intents, flasks, card kinds); a charge flask spends a charge; the prompted discard chooser refuses too many and accepts a choice; pause rows in a fight; the creation rail; the class preview; focus modes; art includes.
- PlayMode `FirstFightSmokeTests`: Boot → title → New → W-03 → door → W-04 (the data default preselected and focused) → Begin → W-07 → a targeted card played on an enemy with the keyboard (autosaved to the log) → End Turn with the pad → the enemy turn (a distinct state) → the next intents → Escape pauses, pad Start resumes and pauses again → Save & quit (Enter held past holdMs) → title with Continue focused → Continue → **the identical state hash** → the fight finished through the screen's command path → the end state (Rewards disabled) → title.
- Results:
  - dotnet 1104/1104;
  - Unity EditMode 1105/1105, Enforcement green;
  - PlayMode 2 passed, 1 skipped (the capture test);
  - `codegen --check`, `check-content`, `transform --check` and `check-docs` green.

**Review** (own screenshot review against W-04, W-07 and W-20; fixed and recaptured):
- the region combat art is a 2×2 sheet of variants (W-07 now shows the first quadrant);
- cards clipped at the top of the hand band (combat card size; the hand takes the remaining height);
- enemy meters overflowing their combatant; unlabelled Poise, Ward and HUD meters (labels from the resource strings);
- the target and discard prompts hidden behind the cards (moved to the foot of the battlefield);
- discard footer buttons not spread; narrow footer labels ellipsized (short labels on narrow);
- the creation rail's planned values ellipsized; the intent-variety fixture ending the fight before two kinds showed.

**Not verified / open**
- **Mouse:** cards, combatants and buttons take UI Toolkit pointer events, but there is no automated pointer test. Keyboard and pad are covered by the smoke test.
- **Compact band (844×390):** W-07's "rails" layout (Actions and Draw on the left, End Turn, Discard and Potions on the right) is not built. The hand is small there, card text is raised to the physical minimum and clips, and a lifted target overlaps the HUD. The upright gate is not wired.
- Not built:
  - US-7.1 weapon-set swap (there is no domain command yet) and the Armaments popover;
  - the W-17 inspector and the pile viewer (planned);
  - per-hit animation, VFX and enemy state frames (D-075); status icons (text chips, D-076);
  - the relic rail and cinders in the HUD; Abandon run.
- W-04's other panes, name entry, seed choice and journey are planned; the default name is `creation.defaultName`.
- **D-073 needs an owner ruling** (the reference preset's Reaver flask split).

**Next:** W-08 rewards on the post-combat pipeline (feature/rewards), then the compact rails layout and a pointer test.

## us-0.4: run and rewards data from content (feature/content-data, 2026-09-26)

**Landed**
- `RuntimeRegistries.ToRunData` / `ToRewardsData` (D-069): the run port's and the post-combat port's data built from the layered content, replacing the oracle dumps for real play. `RegistryBundle` now also loads creation modes, characterCreation, attributeRules, derivedStatRules and the tag tree; `RunSnapshot.ContentVersion` carries the manifest's contentVersion.
- Key order parity for content-built rows: `rules/textAnchors.json` (generated by `Tools/transform-content.mjs`, with its schema) records where each display-text field sat, and the bundle re-attaches text there; tag stamping writes keys in the shipped spread order.
- `run-keys.mjs` fixed on dev (`RunState` uses `K.Xp`).

**Tests**
- `ContentRunDataTests`: registries and documents equal the rewards oracle dump exactly (key order included); all 76 recorded runs with their 152 combat starts and all 168 post-combat cases replay identically on content-built data.
- `ContentCombatDataTests` tightened from sorted-key to exact comparison (equipment top level compared by name).

**Not verified**
- Non-shipped presets (reference) are only covered by the existing layer tests, not replayed.

**Next**
- The first-fight UI stream switches RunSession to `ToRunData`.

## us-11.1: post-combat pipeline with shipped parity (feature/rewards, 2026-09-26)

**Landed**
- `Ashen.Domain.Rewards` (D-062). `CombatEnd.Apply(data, run, combat, enc, result, rng, options)` is the run-writing half of the shipped `main.js onCombatEnd`, ported line by line in the shipped order. Scope was taken from V8 precise coverage of the shipped calls (`node Tools/oracle-rewards.mjs --coverage`). It covers:
  - the write-back of flasks, charges, pools and deficits from the fight, with the shared loadout rejoined (D-063);
  - the skill tracks: `applySkillXp`/`awardSkillXp`, derived tracks, the curve, draft schools, rarity unlocks and the standing auto-upgrade rule;
  - the class track (`awardClassXp`) and the class-tree draft pool (tiers, REQUIRES and CONFLICTS_WITH read in both directions);
  - the character level: `combatLevelXp`, `awardLevelXp` with the points dial and cap, and `rederivePools` through `reconcileRunLoadoutHp`;
  - `combatXpGains`, and `stampDeck` with `adoptEquipmentBonuses`;
  - the defeat path (`hp = 0`), the win count, the cleared combat receipt and `grantSmithingReward` (with idempotent claims);
  - the boss ledgers (`bossesBeaten`, `bossGroups`), the act-3 summit and Endless;
  - the reward rolls on their shipped streams: cinders with `runeGainMult`, the card offer (Feral Eye, Chaos Rewards), skill and class drafts, the flask pity counter, elite and boss relics, armament drops with consolation cinders;
  - `run.pendingReward` (`beginPendingReward`).
- `rules/rewardsEngine.json` (hand-written, schema inferred) holds the tree parents, track kinds, level-curve defaults, the roll guard, flat Chaos odds, relic rarities, the legacy charge-flask seam, smithing pools, the summit act and the victory titles. Names come from `Tools/codegen.d/rewards.json` via `Tools/rewards-keys.mjs`: 85 keys, 13 values and 14 messages, with combat and run names reused.
- `Tools/oracle-rewards.mjs` records 168 cases: the 60 golden combats replayed to their end, and 108 generated fights (4 classes × normal/elite/boss × 18 run variants). The variants reach level thresholds and multi-level climbs, track crossings and the standing upgrade, class thresholds and tier-2 pools, queued drafts on every track, rune and Feral Eye relics, a claimed Smithing reward, Chaos Rewards, Endless and the act-3 summit, a fully found armoury, full flask pity, `equipmentChanged` and low-HP defeats. Each case stores the run before, the combat end snapshot, the encounter, the result, the RNG counters, the options, and the expected run, receipt and counters after. A JS self-check requires each snapshot to restore to an identical pipeline. Re-running the oracle gives byte-identical files. Outcomes: normal 55 reward / 20 defeat, elite 26 / 15, boss 21 reward / 29 defeat / 2 summit. Size: 12 MB with the registry dump (which adds `nodes`).

**Tests**
- `RewardsParityTests`, 170 cases:
  - 168 cases, each restoring the fight with `CombatSnapshot.Restore` on an RNG at the recorded counters and running `CombatEnd.Apply`. The run document and the receipt must equal the shipped ones strictly (presence, values and key order), and every RNG counter must match;
  - an oracle-presence test;
  - a deferred-path test (journey and legacy dungeon throw by name).
- Mutation check: corrupting a pending-reward cinder value, swapping two reward keys, bumping an expected `cardRewards` counter and bumping a receipt `levelAward.points` each failed exactly one test. The failures named the path (or the key order, or the stream). Each file was then restored byte for byte (sha256 checked).
- dotnet 768/768, enforcement included; `codegen --check`, `check-content` and `transform-content --check` green.

**Not verified:** Unity EditMode (the UI stream owns Unity). The rewards data is built from the oracle dump; there is no content-built `RunData`/`RewardsData` path yet. Swapping equipment mid-fight is exercised only through the flag, because the C# combat has no swap commands yet.

**Deferred:**
- journey and legacy-dungeon fights (D-066);
- the smith's services-table normalization (D-065);
- the caller-side profile, save, audio and screen work (`finishRun`, `collectArmament`, game over and the reward screen; D-067).

**Next:** the reward screen (take/skip rows, `collectArmament`, draft picks via `spendSkillDraft`/`pickClassNode`) and the game-over flow's `finishRun`, driven by `CombatEndReceipt`.
## us-1.2, us-1.3, us-1.4, us-0.10 (title footer), us-17.1: F1 UI foundation, boot, title and slots (feature/boot-title, 2026-09-26)

**Landed**
- **UI data** (hand-written, schema'd, in the manifest):
  - `ui/tokens.json`: colours, space, radius, font, size, border, duration and opacity groups;
  - `ui/layout.json`: two panels, bands, `minPhysical`, CategoryNav rules and the verification sizes;
  - `ui/screens.json`: the registry, with a required `focusOrder`, `initialFocus`, back behaviour, transitions, and `planned`/`dev` flags;
  - `ui/menus.json`: title rows in the owner order, plus the W2 doors;
  - `ui/components.json`: kit defaults and the art prefixes.
  - All display strings are in `strings/app.en.json`.
- **Codegen:** the new fragment `Tools/codegen.d/ui.json` holds ScreenIds, UiNames, UiClasses, UiKeys, UiValues, menu actions and rules, confirm ids, placeholders, formats, SlotSummaryKeys, TokenKeys, UiResources, UiMessages and the UiMath numbers (D-054).
- **Tokens:** `Tools/ui-tokens.mjs` generates `Tokens.uss` from `ui/tokens.json`. Its `--check` mode also lints every USS file for raw values (D-055), and `Tools/ci/check-content.mjs` runs it.
- **Application (engine-free, `Ashen.App.Ui`):**
  - `ScreenRegistry`, `MenuSet`, `LayoutRules`, `UiTokens`, `ComponentDefaults`, `ConfirmPolicies`;
  - `LayoutClassifier` (bands, narrow mode, panel scale, physical minimums);
  - `CategoryNavModel` (Rule 11 fit with hysteresis);
  - `FocusOrder` (regions, then items; skips unusable items; wraps);
  - `MenuRules` (always, hasValidSlot, targetBuilt);
  - `SlotSummaries` (Ready, Newer, Unreadable, Empty; Continue target) (D-059);
  - `StringTable` (named templates);
  - `UiData`.
- **Platform:** `PlatformPaths` and `InputRouter`. The router is the one Move/Submit/Cancel source, built on the Input System's UI actions plus Tab. Active Input Handling is now the Input System (D-053).
- **Presentation (`Ashen.Presentation.UI`):**
  - `UiHost`: the UIDocument and two runtime PanelSettings (D-057).
  - `Navigator`: screen stack and modal layer; band and layout classes on the root; PanelSettings swap; `minPhysical` after scaling; innermost-first Back; focus return.
  - `FocusModel` and the `ViewModel` base (`INotifyBindablePropertyChanged`).
  - `LocLabel` and `LocButton` (`[UxmlElement]`, `string-key`), and the `UiArtCatalog` (D-056).
  - Kit, each as UXML + USS + C#: Shell, Workspace, CategoryNav, Confirm, Meter, CardView, HandFan, FooterGroup, Tooltip, HoldButton, Refusal, and SlotRow.
- **Screens:**
  - W-24 boot: determinate bar over the manifest, then validation and the profile check.
  - W-24 content error: dev report list with Copy/Quit; player notice with Quit.
  - W-01 recovery: **stub**.
  - W-02 title: press-any-input gate (D-052); menu from `menus.json` with Continue disabled when no slot is valid; W3b preview; footer with the build and the AI disclosure. The disclosure is focusable and opens the full notice.
  - W-03 slots:
    - Load and New modes with 3 rows;
    - portrait, name, class, act, floor, HP, saved time and seed;
    - empty, newer-build and unreadable states, and the empty state with a full-width Create character;
    - the pre-review doors, and the destructive hold-to-confirm delete.
  - W-23: the doors as a modal (D-061).
  - `kitGallery` (dev): a review sheet for the kit.
- **Boot scene:** `Assets/Scenes/Boot.unity` (a camera plus `AshenBoot`), built by `Cli.CreateBootScene`. It is the only scene in the build settings. `Cli.BuildUiArt` builds the art catalog.
- **Screenshots:** `Cli.CaptureScreens` runs the `ScreenCaptureTests` PlayMode test over the 13 fixtures in `Tests/Fixtures/screens` at 1280×720, 1920×1080, 844×390 and 390×844, and writes `docs/screens/<fixture>_<w>x<h>.png`: 52 PNGs, about 21 MB (D-060). Run it without `-quit` and without `-nographics`.

**Tests**
- `UiRulesTests` (21 cases) and `UiDataTests` (9) in the shared suite, run by both runners:
  - owner menu order; Continue enablement; planned targets;
  - CategoryNav fit and hysteresis; focus order and wrap;
  - bands and scales at 6 viewports; physical minimums; string templates;
  - slot summaries for empty, ready, newer and damaged saves;
  - ScreenIds match the registry; every built screen has UXML and a focusOrder; focus regions exist in the UXML;
  - transitions, menus and doors resolve; backgrounds and art are in the registry;
  - UXML names are generated and UXML carries no literal text; USS uses tokens only;
  - the title footer binds `title.aiDisclosure`.
- PlayMode `BootTitleSmokeTests`:
  - the Boot scene reaches the gated title;
  - Space lifts the gate and focus lands on Load (Continue is disabled with no save);
  - Down/Up and wrap work; Enter opens W-03 Load, where focus is on Create character in the empty state;
  - Escape returns with focus on Load; the pad d-pad moves focus.
- Results:
  - dotnet 249/249;
  - Unity EditMode 250/250, Enforcement green (no literals in Presentation or Platform);
  - PlayMode 1 passed, 1 skipped (the capture test is skipped unless `ASHEN_CAPTURE=1`);
  - `codegen --check` and `check-content` (with the tokens check) green.

**Review** (screenshot-review sub-agent against 04 and the story ACs; fixed and recaptured):
- slot row delete overlapping the text on narrow;
- no portrait or character name in the rows;
- ready primaries not green;
- hold text below the minimum on compact;
- focus on an empty slot in the empty state;
- a raw saved timestamp ("yesterday" and the time of day now);
- a duplicated newer-build notice;
- New mode preselecting an occupied slot;
- touching footer buttons;
- a clipped content-error title and a doubled separator;
- the compact title overlapping its footer;
- the blue default-theme focus ring (now gold, and red on Back/Exit).

**Not verified / open**
- **US-1.1:** the recovery screen is a stub. It does not archive or restore files.
- **US-1.3:** Journal, Custom Run and Settings are disabled as planned (D-058). Confirming Continue, Load or New ends in a refusal until creation and the run screens exist.
- **US-1.4:** journey type and playtime are read but not shown; they belong in the row tooltip.
- **US-0.10:** the first-launch notice (once per profile) and About are not built. The notice frame exists as the `aiNotice` door.
- Not verified:
  - touch input on a device;
  - UI Toolkit's native navigation in a player build (it is deliberately bypassed; D-053);
  - the `⬡` glyph, which the default font lacks (ASSET-GAPS).
- No ash particles on the gate; the title's lit/unlit rule waits for the profile service.

**Next:** W-04 creation (Class pane), then W-07 combat with the kit (Meter, CardView, HandFan, FooterGroup), then W-08 rewards and W-20 pause.
## us-4.2 (with the map parts of us-4.1, us-4.3, us-4.4): act-map generation with shipped parity (feature/map, 2026-09-26)

**Landed**
- `Ashen.Domain.Map` (D-046 to D-051), a line-faithful port of:
  - `engine/mapgen.js`: `generateActMap` (path walk with the no-cross rule, entrances, typing with bounded retries, `rollType`, `relaxPlace`, `ensureRestBeforeElite` with its victim order and donor restore, `finish`), `assignBossDestinations`, `sampleActShape` and `sweepSeed`;
  - `model/floorplan.js`: `resolveAnchor`, `resolveFloorPlan` (every refusal keeps its shipped key), `minViableFloors`, `applyRunShape`;
  - `engine/actmap.js`: `buildActMap`, `bossEncounterForNode` (including legacy singular graphs), `drawSeatOrder` (reuses `SeatOrder`);
  - `resolveUnknownNode` with the quest-history gate (`eventChoiceRequirementMet`), `rollEncounter`, `encounterFitsSeat`, `finalTier`, `tierOf`, `bossDestinationLabel`.
- `MapData` holds what the map reads. It is built from the oracle dump (`FromTableDump`) or from content (`RuntimeRegistries.ToMapData`). `RuntimeRegistries` now also carries the seats table and the map documents (`balance/mapConfigs.json`, eventMeta's gates, `catalog/bossDestinations.json`, `balance/mapShape.json`'s limits and legacy bosses).
- `rules/mapEngine.json` (hand-written, schema inferred): the retry and guard counts and the vocabularies the shipped code kept as constants.
- Names: `Tools/codegen.d/map.json`, regenerated by `Tools/map-keys.mjs` from the `MK.`/`MV.`/`MM.` aliases. It refuses a name that CombatKeys or CombatValues already owns, so the port reuses `K.`/`V.`.
- `Tools/oracle-map.mjs` records the shipped outputs to `Unity/Assets/Tests/Oracle/map` (4.6 MB):
  - every seat × every tier × 20 seeds (`sweepSeed(3000+i)`);
  - 7 run histories (earned, completed and malformed quest gates; 54 gated events rolled);
  - 25 run shapes (14 accepted, 11 refused);
  - 347 authored configs through `generateActMap` directly, including acts found by instrumenting a scratch copy of mapgen.js that reach the rare exits (spare victim, any victim, donor restore, surplus-Shrine donor);
  - 26 floor plans, plus `sampleActShape`, `rollEncounter`, Unknown rolls, seat orders, legacy bosses and refusals.

**Tests**
- `MapParityTests` (32 cases). 1,883 maps are compared node for node, in insertion order, with every RNG counter: 180 seat × tier maps, 252 history maps, 378 shaped maps and 1,073 direct `generateActMap` maps. The 180 seat × tier maps are also rebuilt from content-built data. On top of those: 300 refusals (with counters), 26 plans, 108 encounter rolls, 180 Unknown rolls, the seat order, legacy and live boss lookups, and the content tables in shipped order.
- Mutation checks:
  - Corrupted oracle values (a node type, an events counter, a destination label) fail.
  - So do code mutations of the no-cross rule, the rest-before-Elite roll bar, the relax placement and rest opening, the victim order, the donor and Shrine-donor rules, the completed-quest gate, malformed history rows, the label and the shape minimum.
- dotnet 392/392 (enforcement included). `codegen --check`, `check-content`, `transform --check` and `check-docs` are green.

**Not ported (deferred):** the `describePlan` readout text (tooling); `refreshBossDestinationLabels`; the problem strings of the quest validators (only pass/fail is needed here); the JS-only `act` argument refusals.

**Not verified:** Unity EditMode (the UI stream owns Unity; this runs at integration).

**Next:** run the map from the run state (map screen W-05, node entry, `bossEncounterForNode` on arrival), once run creation lands.

## us-2.2: run creation with shipped parity (feature/run-state, 2026-09-26)

**Landed**
- `Ashen.Domain.Run` (D-042): `RunState.Create` is the shipped `createRunState`, ported line by line onto the JSON run document. Scope was taken from V8 precise coverage of the shipped calls (`node Tools/oracle-run.mjs --coverage`). It covers:
  - creation modes and class presets, with the allocation rules;
  - the derived-stat rule resolve, validation, derive and host snapshot;
  - the equipment profile snapshot (create and restore), the kit plan and role receipts, `effectiveEquipmentRating`;
  - weapon card packages and the equipped attack plan;
  - the composed starting deck, grant refs, `orderStartingDeck`, item-owned cards (`reconcileGrantedCards`, `desiredGrantInstances`, card mounts);
  - `stampDeck`, card and run mods, the pool reconcile, requirement receipts;
  - starting kits, armour, hands and relic; relic modifiers; flask charges and growth; zones; the default seat order.
- `RunCombat.CreateArgs(run, encounterId, data, settings)`: the `createCombat` arguments `main.js enterCombat` builds (seat-tier HP ratio, hand-rule defaults, swap-cost rule, start statuses). A fight can now start from content alone.
- `rules/runEngine.json` (hand-written, schema inferred): versions, defaults and vocabulary lists. Names come from `Tools/codegen.d/run.json` via `Tools/run-keys.mjs`: 202 keys, 53 values, 154 messages and 2 patterns, with combat names reused.
- `Tools/oracle-run.mjs` records, for every class:
  - twelve default seeds (`sweepSeed(2000+i)`);
  - seven creation variants: two more modes, the alternate kit, armour and relic, and two hand picks;
  - two refused variants.
  For each run it writes the run document, the RNG counters, and two fights (args, combat-start snapshot, counters). Its JS self-check replays each fight from the JSON-plain args and requires the identical start. Output: 76 runs, 152 fights, 7.0 MB, plus the registry dump.

**Tests**
- `RunParityTests`, 238 cases:
  - the run document equals the shipped one for all 76 runs, with strict presence (D-045);
  - the args equal the recorded ones;
  - `CombatStart.Create` on those args reproduces the shipped snapshot and counters, and a turn can be ended;
  - the 8 refused variants throw the shipped message;
  - the deferred paths throw `NotSupportedException`.
- Mutation check: corrupting a run `maxHp`, an args card id and a snapshot enemy HP failed the three tests, naming the first differing path. The file was then restored.
- dotnet 517/517, enforcement included; `codegen --check` and `check-content` green.

**Not verified:** Unity EditMode (the UI stream owns Unity). The creation-mode `equipmentProfiles` layer is ported, but no shipped mode authors one, so it is not exercised.

**Deferred (D-043, D-044):**
- custom allocations;
- derived-stat option layers;
- the load door (snapshot restore and migrations);
- hand-rule settings overrides;
- journey, legacy-dungeon and Custom Climb fights;
- the heal ledger.

**Next:** F1 playable loop: a run-backed combat screen. Then the load door (`restoreDerivedStatRuleSnapshot`, schema migrations) with save round-trip parity.
## Combat session, legality, content-built data and skill XP (feature/combat-core, 2026-09-26)

- **Content → engine:** `RuntimeRegistries.ToCombatData` builds the combat data from content, adding attributes, property rules by tag, and the class tree. `ContentCombatDataTests` confirm:
  - every table equals the shipped one (rows, row order, enemy move order);
  - all 60 golden combats start and replay identically on it. (0.1.4.1)
- **Legality and session** (engine side of US-5.9), 0.1.4.2:
  - `CombatLegality` answers the engine's pre-payment checks without mutating. Refusal keys come from `strings/combat.en.json`.
  - `CombatSession` (Application) handles refusals, rolls back to the last safe point, and checkpoints to SaveService with a hashed command log (D-017).
  - A property test shows legality equals engine acceptance for every card and target at every step of 10 golden combats.
- **Skill XP** (removes the D-041 deferral): `SkillXp.Record` listens on the bus exactly as the shipped `attachSkillXp` does. The vocabulary lives in `rules/combatEngine.json` `skillXp`.
  - The oracle now projects the receipt after every step. This also fixed a recorder bug: it had stored a live reference, so every step showed the final value.
  - All 60 combats match.

## us-5.3: combat start and previews with shipped parity (feature/combat-core, 2026-09-26)

**Landed**
- `CombatStart.Create` (shipped `createCombat`), covering:
  - the entity factories and `stampPlayerPoiseMax`, with the rated poise threshold as the vessel;
  - property mounts, enemy HP on `enemyHP` (with the JS `Math.round`), enemy ratings and meters;
  - the deck-instance copy, shuffle with Innate on top, start statuses, opening intents and turn 1.
- `CombatPreview.PreviewCard` and `PreviewIntent` (shipped previews, SPEC §3.13) plus `TokenBindings` (`computeTokenBindings`). These use the same math as execution and consume no RNG.
- The oracle now records the exact `createCombat` inputs and previews (every hand card and every living enemy's intent) after every step. Output is compact (5.6 MB for 60 combats).
- `Tools/oracle-replay.mjs` also checks the previews: 60/60, 0 failures.
- Oracle scripts delete only their JSON outputs, so committed `.meta` files survive regeneration.
- `Tools/combat-keys.mjs` regenerates `Tools/codegen.d/combat.json` from the engine's alias usage, plus `Tools/combat-keys/{values,messages}.json`.
- `rules/combatEngine.json` gains `defaults.handMax`, `damageSchools` and `tokenizableOps`.

**Tests**
- `CombatParityTests` now runs 181 cases:
  - `CreateCombatMatchesTheShippedStart`: the full serialized snapshot equals the shipped one for all 60 combats (event log, piles, meters, intents, RNG).
  - The replay compares previews after every step.
  - The resume test also checks previews.
- Mutation check: a changed event-log type or preview value fails the create and replay tests.
- dotnet 279/279.

**Not verified:** Unity EditMode in this worktree. The UI stream owns the main checkout's Unity instance; this runs at integration.

**Next:** run creation (`createRunState`) for a default character with oracle parity, so F1 can start a fight from content alone.

## us-5.1: combat engine port with shipped parity (feature/combat-core, 2026-09-26)

**Landed**
- `Ashen.Domain.Combat` (D-041): the shipped engine/combat.js family ported line by line onto a JSON document state (D-036, D-037).
  - Covered: `dispatch` (playCard, endTurn, useFlask), queue drain, end check, player and enemy turns, delayed moves, weighted move picks with `maxConsecutive`, and phases.
  - Every combat opcode, status stack modes, meters, procs, resists and decay, triggers (stance, property mount, status, phase) and the full predicate set.
  - Formulas, AR/DR/PR/Poise/Ward ratings with break meters, hand rules and the discard choice, relic/equipment/class property mounts, card resolution (upgrade, profile, school variant, mods), and snapshot restore/serialize.
- `CombatData` plus `rules/combatEngine.json` (limits, defaults, card-property vocabulary, grip tags, upgrade tags, flask kinds).
- Names come from `Tools/codegen.d/combat.json`: 374 keys, 42 events, 19 ops, 22 predicates, 118 values, 50 messages.
- **Content order fix (D-040):** generated content now keeps the shipped key order. Enemy move order and registry order decide seeded outcomes.
- Tools:
  - `Tools/oracle-replay.mjs` replays the golden logs from their snapshots through the shipped engine: 60/60 combats, 0 failures.
  - `oracle-combat` now also writes the registry tables it ran against.
- The registries port (us-0.3, background agent) merged into the lane first: 26 parity tests, both presets exact.

**Tests**
- `CombatParityTests`: every golden combat (60 combats, 1,068 steps, 31 victories and 29 defeats, all 35 encounters) replays through the C# engine with the projection identical after every step. The projection covers HP, block, statuses, intents, resources, hand, pile sizes and all RNG counters.
- The same test also saves mid-fight, restores and resumes to the end identically.
- Mutation check: corrupting one oracle HP value fails both tests.
- dotnet 219/219; Unity EditMode 220/220. Enforcement green (no literals in the engine).

**Not verified / open**
- The engine rules for US-5.2 to 5.8 and 6.1 to 6.5 are in place, but their UI acceptance items (previews, targeting, HUD, enemy timeline) are not done. Only US-5.1 is counted complete.
- `useFlask` is ported but not in the golden logs (the bot never drinks). `createCombat` and previews come in the next story, with oracle-recorded inputs.

**Next:** us-5.3: `createCombat` and previews (`previewCard`/`previewIntent`), with oracle parity; then an F1 playable-loop combat screen.

## Phase F0 Foundation — complete (promoted at 0.1.1.1; renumbered from 1.0.0.0 by owner ruling, 2026-09-26)

**Exit gate** (09 §8):

| Check | Result |
|---|---|
| Validator | Green: 150 content files, 65+ schemas |
| Counts | Match the shipped game (195 cards, 63 relics, 33 enemies, 35 encounters, 25 events, …) |
| RNG and seat oracles | Green: 13,440 draws plus 100 seat cases, bit-identical |
| Registry | 5,265 assets, no dangling IDs, 899 imported pixel-identical |
| Tests | dotnet 72/72, Unity EditMode 73/73 |
| CI on `dev` | 7/7 jobs green; the Unity jobs skip because the licence secret isn't set; the Windows build runs from `test` onward |

**Moved out of F0:**
- Map-generation parity and golden replays go to F3/F2 (D-033).
- Most art domains import per phase (D-035).

**Stories:**

| Story | Version |
|---|---|
| us-0.3 | 0.0.2.0 |
| us-0.9 | 0.0.3.0 |
| CI fix | 0.0.3.1 |
| us-0.4 | 0.0.4.0 |
| us-0.5 (part 1) and us-18.4 | 0.0.6.0 |
| us-0.7 | 0.0.7.0 |
| us-0.6 | 0.0.8.0 |
| us-0.8 | 0.0.9.0 |
| us-0.1 and us-0.2 | 0.0.11.0 |
| us-0.10 | 0.0.12.0 |
| foundation feature complete | 0.1.0.0 |
| us-16.2 | 0.1.1.0 |
| docs-check fix | 0.1.1.1 |
| phase F0 promotion | 0.1.1.1 (was wrongly tagged v1.0.0.0; renumbered) |

**Environment:** **C: is full (0.2 GB free)** and D: has about 3.7 GB. The owner needs to free space before the F1/F2 art imports (animations are 122 MB of WebP source, several times that as PNG plus Library).

**Next:** F1, the first playable loop (kit → W-02 title → W-03 slots → W-04 Class pane → W-07 combat → W-08 rewards → W-23 → W-20 → checkpoint and resume).

### us-16.2: procedural SFX core (2026-09-26)

- **`Ashen.App.Audio.SynthRenderer`** (engine-free) renders the shipped `tone`/`noise` recipe layers to mono PCM with the shipped Web Audio shapes:
  - tones: exponential glide, and an exponential attack of min(attackMax, dur·fraction) followed by an exponential decay;
  - noise: filtered through high- and low-pass stages, with an exponential decay.
- **Recipe resolution:** exact id, then family (text before `_`), then `default`, which is audible.
- **Deterministic noise:** its own xorshift seeded from the recipe id, never a domain RNG stream.
- **Data:**
  - `audio/synth.json` holds the engine parameters;
  - `audio/contexts.json` maps the 8 contexts plus `quiet` to a bed or `"silence"`, and holds generated-file slots (null today) and stingers.
  - Generated: `SynthKeys`, `Waveforms`, `DspConstants`, `NoiseAlgorithm`.
- **Unity:** `Ashen.Presentation.Audio.SynthClipCache` wraps the PCM in cached `AudioClip`s.
- **Tests** (`SynthRendererTests`, 10):
  - resolution order;
  - every recipe renders finite, audible, bounded samples;
  - length covers the longest layer;
  - determinism, and a missing id is never silent;
  - contexts map to beds or silence, and stingers resolve.
- **Enforcement:** the number allowlist now also accepts the identities 0.0 and 1.0.
- **Results:** dotnet 72/72 · Unity 73/73.
- **Not yet built:** music-bed synthesis (drone, pulse, arpeggio variants) and context playback, with the F1 title and map screens; generated music tracks go in the file slots then (D-006).

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
