# 11 — Core 2 port plan (Unity rebuild ⇐ web dev HEAD `b8cfe4fac`)

Status: plan, 2026-10-23. Companion to [`docs/qa/web-gap-2026-10-23.md`](../qa/web-gap-2026-10-23.md), which covers the snapshot `0adaae515` and the 405-era "current" profile.

**Target.** The web's default new-run ruleset: profile `default` = `{ formatVersion:1, core:'ashen.core@2.0.0', modules:{} }`. It resolves to catalog id `core`, hash `sha256:0b5c8941b89d6be675240f83e9e025cc7b17021fcdf2271eb6c07f156a95bd27` at HEAD.

**Binding rulings:**
- D-138: the shipped web game is the reference; no save compatibility.
- The web repo's `docs/FIXCORE-DECISIONS.md`, `docs/STANCE-COMBAT-RULES.md`, `docs/combat-reaction-contract.md`, `docs/combat-card-traits-contract.md` and `docs/enemy-knowledge-contract.md`.

**Paths.** Web paths are relative to the web repo root (`cehinds/ashenspire`). Unity paths are relative to this repo.

## 1. How a Core 2 run is assembled in the web

### 1.1 Module graph (headless; `src/main.js` is only the glue)

```
content/source/*.json|csv ──tools/content-build.mjs──▶ src/content/generated/*.js ─┐
engine/foundation + engine/modules/*/{module,defaults,settings.schema}.json         │
games/ashenspire/packs/{core,base-cards}/*.json + content/source/fixCoreMechanics.json
        ──tools/game-config.mjs build──▶ src/content/generated/gameProfiles.js ─────┤
engine/mechanics/*/{mechanic,model.schema,interface,service,cases}.json             │
        ──tools/mechanics-browser.mjs──▶ src/content/generated/fixCoreProtection.js ┤
                                                                                    ▼
contentBundle (src/content/index.js:204)
  → applyGameProfileToContent(contentBundle, 'default')   src/model/gameProfile.js:112
  → createRegistries(bundle)                               src/model/registries.js
  → bindGameProfileRegistries(reg, profile)                gameProfile.js:150
```

What `applyGameProfileToContent` changes:
- adds the profile's 211 cards, including base cards and each class's HP and special card;
- strips equipment requirements;
- filters resource rows;
- pins Core enemy `poiseMax`;
- projects enemy range and closing moves, and tagging.

**Configured runs skip two layers.** For a configured run, `main.js:263-279 rebuildRegistries` skips `configuredContentBundle` (Advanced settings) and `registriesForClassMastery`. So Core 2 has none of these:
- the Advanced-config overlay;
- class mastery, master or respec;
- the legacy market, blacksmith, sigils, consumables or companions. `main.js:3946` routes shops to `gameProfileShopStock`.

These systems belong to the legacy profile only and are out of scope (WP-21).

### 1.2 Headless entry points an oracle can call

| Area | Functions (web) | Notes |
|---|---|---|
| Profile and content | `resolveGameProfile('default')`, `applyGameProfileToContent`, `bindGameProfileRegistries` (`src/model/gameProfile.js`); `createRegistries` (`src/model/registries.js`) | The resolved profile has `id`, `hash`, `foundation`, `modules` and `content`. `content` holds characters 37, cards 211, enemyCards 138, enemies 33, gear 47, enemyRelics 6, plus starterDeck, stats, routes, rests, rewards, shops, classUnlocks, cardPools, resourceVariants, classAttributes, startingHands and baseCards |
| Rule snapshots | `fixCoreRules(ctx)`, `fixCoreEnabled`, `protectionReceipt`, `snapshotFixCoreRules` (`src/model/fixCoreMechanics.js`); `reactionRulesSnapshot()` (`src/content/combatCardTraits.js`); `createCombatExpansionRuleSnapshot()` (`src/model/combatExpansionRules.js`); `snapshotEnemyKnowledgeRules(reg)` (`src/model/enemyKnowledgeRules.js`); `snapshotRangePolicy(fixCoreMechanics.range, {})` (`src/engine/combatEngagement.js`) | All data. `fixCoreRules` resolves through `src/model/mechanicsKernel.js` (`compileBindings`, `queryBinding`) over the `engine/mechanics/combat.*` and `defense.*` manifests |
| Run creation | `createRunState({seed, classId, registries, profileMeta})` (`src/model/state.js:148`) | Stamps `combatExpansionVersion 2`, `reactionRulesVersion 2` + `reactionRules`, `rangePolicy`, `ashenBlight`, `combatExpansionRules`, `enemyKnowledgeRules`. Derived stats come from `gameProfileStatOptions` (hand 4 + INT; pools from `stats.json`). The starter deck comes from `initializeGameProfileRun` / `gameProfileStarterDeck` (`src/model/gameProfileRun.js`). Class availability is `gameProfileClassAvailable` (`classUnlocks.json`: Reaver first, then Starseer, Rogue and Herald, one per completed run) |
| Map | `drawSeatOrder`, `buildGameProfileActMap(reg, rng, seat, tier, {history})` (`src/engine/actmap.js:122,133`) | Streams `seats` and `map`. 7 pre-boss floors in 3 lanes. Rests at floors 3, 5 and 7 (one node each). `firstFloor:'fight'`. Shops at `shopShare 0.15`, drawn with `rng.chance('map', 15)`. Fog (`hideUnknownNodes`) shows hidden node types as `event`. The boss is `rng.pick('map', pool)` |
| Combat build | `createRunCombat({registries, rng, run, enemyIds, encounter, settings})` (`src/engine/runCombat.js:126`) → `createCombat` (`src/engine/combat.js:151`); `runCombatEnd(run, combat)` | A configured run forces `combatExpansionVersion 2`, reactions v2 (`chainLimit 16`) and `ratingsRules null`. Player ratings come from `gameProfileRatingReceipt` (`gameProfileRun.js:104`) and attributes from `gameProfileEffectiveAttributes` (`gameProfileGear.js`). Enemy levels come from `resolveEnemyLevel` (`src/model/levels.js`), seeded by the run seed rather than the RNG streams |
| Combat step | `dispatch(combat, intent)` (`combat.js:1119`) | Core intents: `playCard{cardInstanceId,targetId,choice,upcastRanks,rangeRoute}`, `endTurn{discardIds}`, `useFlask`, `setReactions`, `chooseReaction{offerId,optionId}`, `predictIntent`, `recoverControl`, `chooseCards`, `chooseDiscard`, `moveCharacter`. `swapArmament` and `changeEquipment` are refused, because equipment is `editorWhere:'noncombat'`. `basicAttack` and `pass` don't exist while the cards module is on |
| Previews (same math as resolve) | `previewCard`, `previewIntent`, `previewCardHit`, `cardPlayCosts`, `resolvedCardPlayCosts`, `cardChoicePlan`, `cardNeedsEnemyTargetNow` (`combat.js`); `previewEnemyAction` (`src/engine/enemyActionPreview.js`); `attackHitPreview` (`src/engine/stanceHit.js:81`); `counterAttackPreview` (`src/engine/counterReturn.js`); `previewRangeRoutes` (`combatEngagement.js:170`) | Record them after every step, as `Tools/oracle-combat.mjs` already does |
| Resolution internals (unit oracles) | `computeAttackDamage`, `applyAttackDamage`, `drainFixCorePoise`, `fixCoreStagger`, `gainBlock`, `gainBarrier`, `dealPoiseDamage` (`src/engine/actions.js`); `profileDamageReceipt`, `setProfileStance` (`src/engine/gameProfileCombat.js`); `resolveAttackHit`, `effectiveEvasion`, `evadeStackGain`, `precisionStatMix` (`stanceHit.js`); `rollChanceDie`, `resolveAvoidance` (`src/engine/combatAvoidance.js`); `stanceMitigation` (`stanceMitigation.js`); `stanceStaminaCost`, `stanceManaCost`, `stanceEffectiveness`, `stanceEntryBonuses`, `sameStance` (`src/model/stanceRules.js`); `applyActionCosts`, `cardCostBudget`, `enemyMoveSpOf` (`src/model/actionCosts.js`); `sourceRatingValue`, `splitContactParts`, `magicShareOf` (`src/model/combatRatings.js`) | The web's own unit harness is `tests/helpers/fixcore-combat-rules.mjs`; its `live()` builds a Core 2 fight on seed 511 |
| Enemy AI and reactions | `rollIntents`, `weightedMovePick`, `enemyPhase` (`combat.js:1000-1090, 756`, on the `enemyAI` stream); the SP plan through `Rounds.takePlannedMove` (`src/engine/combatRoundsAdapter.js`); `enemyReactions.js` (`reactionMovesOf`, `decideEnemyReaction`, `openEnemyWindow`); `combatReactions.js` (`beforeReactionAction`, `resolveReactionStack`, `answerReaction`, `deferRootPlay`); `enemyKnowledge.js` + `src/model/enemyIntentKnowledge.js` | Enemy decks: `content.enemies[].defaultDeck` → `enemyCards`. Their `cardHints{aiWeight,maxConsecutive,costSource,legacyMove}` map back onto `def.moves` (`gameProfile.js:60-110`, `src/model/enemyMoveSources.js`). Reaction moves are the enemy cards tagged `trait:reaction` (`src/content/traitProjection.js`). `intent.reads:'exact'` skips the visibility roll |
| Rewards and XP | `gameProfileRewardOffers(reg, run, rng, {offerId, source})`, `claimGameProfileReward`, `claimGameProfileRewardToDeck`, `rewardDeckAddRefusal` (`src/model/gameProfileRewards.js`); `pickRewardCategory`, `rewardGroupProblems` (`engine/kernel/rewardPolicy.js`); `reserveStanceCards` (`src/model/stanceCoverage.js`); `combatXpReceipt`, `awardLevelXp` (`src/model/levelup.js`); `awardSkillXp`, `skillTracks` (`src/model/skills.js`) | Character XP is 0 under Core (`coreSkillOnlyProgression`). Streams: `cardRewards` (combat and level) and `shop`. Combat-end orchestration is in `main.js:3241-3340` and isn't headless, so the oracle mirrors it |
| Rest | `beginGameProfileRest`, `createGameProfileRestDraft`, `previewGameProfileRest`, `commitGameProfileRest` (`src/model/gameProfileRest.js`); `commitSmithing` (`src/model/smithing.js`) + `smithingCore.js`; `applyLevelUp`, `rederivePools` (`levelup.js`) | Arriving heals fully, once. The `smith` service (free, one tier) and `levelUp` can each be used once. Continue leaves |
| Shop | `gameProfileShopStock(reg, run, rng, {shopId})`, `purchaseGameProfileShopOffer` (`gameProfileRewards.js:166-185`) | 3 items per category × `rewards.categories`, flat price 10. The offer is persisted in `run.gameProfileRewardOffers['shop:<id>']` |
| Deck editor | `deckEditBounds`, `deckEditRefusal`, `moveToSideboard`, `moveFromSideboard`, `beginDeckEdit`, `cancelDeckEdit`, `commitDeckEditDraft`, `orderedDrawPile`, `playInDeckOrder` (`src/model/deckRules.js`) | |
| Run loop | No headless controller. `main.js`: `enterNode`, `enterCombat` (3035), `onCombatEnd` (3241-3340), `beginGameProfileReward` (3900-3940), `showRest` (3770), the shop (3946); `gameProfileCompletedRunMeta` (`gameProfileRun.js:18`) at run end | `tools/runsim.mjs` and `tools/simrun.mjs` are legacy-only. The oracle must mirror `main.js` line for line, as `Tools/oracle-loop.mjs` does for the 405 loop |
| Snapshot | `serializeCombatSnapshot`, `restoreCombatSnapshot` (`src/engine/combatSnapshot.js`); `combatSnapshotProblems` (`src/model/combatSnapshot.js`) | Core fields: `rangeState`, `pendingReaction`, `reactionResume`, `reactionCursor`, `persistentPoise`, `barrier`, `combatStance`, `combatCounter`, `enemyKnowledge`, `plannedMoves` |

## 2. Export strategy

### 2.1 Decision: replace the 405 content rather than keep it as a preset

The 405 "current" profile isn't a tuning of Core 2. It is a different rules engine, with energy, Ward and 50 statuses. `settings/presets/*` are merge-patches over a single rule set, so they can't express it.

The owner's reference config (`docs/design/reference/ashen-spire-game-config.json`) targets Advanced keys, and a configured run ignores those (`isConfiguredRun` skips `configuredContentBundle`).

**Owner ruling 2026-10-23 (D-164): accepted.** Core 2 replaces the 405 content. The plan:
- Keep the last pre-port tree: branch `archive/content-405` (`e99920c`; tag writes were refused by the session's GitHub access).
- In WP-21: delete the `reference` preset, shrink `settings/webKeyMap.json` to the player `settings.*` keys, and move the settings defaults held back for the 405 tests (`mapMode`, `walkedFade`, `shrineMultiUse`, `useRestorativeFlasksOutsideCombat`, `showPlayedCard`) to the web's values.
- Make Core 2 the only content.

The legacy oracles (`Tools/oracle-*.mjs`, `Unity/Assets/Tests/Oracle/*`) are retired one WP at a time as each domain is replaced. `Domain.Tests` stays green throughout, because each WP swaps a fixture set and its parity test together.

### 2.2 Pipeline changes (WP-0)

1. **`Tools/export-content.mjs`.** Add `--profile default`, on by default. It writes to `Tools/export/raw/`:
   - **Bundle:** `bundle.shipped.json` = `applyGameProfileToContent(contentBundle, 'default')`, the Core-projected bundle, without `configuredContentBundle`.
   - **Profile:** `profile.json` = `resolveGameProfile('default')` (modules and content), plus `profile.content.*` split per key.
   - **Rule snapshots:**
     - `rules/fixCoreMechanics.json` (verbatim);
     - `rules/mechanics.json`: every `engine/mechanics/*/` folder's manifest, model schema, interface, service and cases;
     - `rules/runtime.json` (`engine/runtime/{ops,formulas}.json`);
     - `rules/reactionRules.json`, `rules/combatExpansionRules.json`, `rules/enemyKnowledgeRules.json` and `rules/rangePolicy.json`, from the snapshot functions in §1.2.
   - **New `sources.json` modules:** `src/content/combatMatchups.js`, `combatStatusRules.js`, `combatExpansionEquipment.js`, `combatCardTraits.js`, `actionCosts.js`, `enemyKnowledge.js` and `ashenBlight.js`. `traitProjection.js` is logic, so it is ported, not exported.
   - **Advanced rows:** keep `advancedRows.json` only for the player-setting subset.
2. **`Tools/transform-content.mjs`.**
   - **Optional key parts:** `scope` becomes optional in the `tagging` key, which fixes the 255 scope-less rows.
   - **Counts:** move `expectedCounts` to the HEAD values, taken from the first clean export and then frozen.
   - **New tables:** add mappings for `shops`, `sigils`, `consumables`, `companions`, `enemyMoves`, `classMastery`, `classSkillFeats`, `proposalStatusRules`, `legacyProgression` and `progressionModuleOwnership`, plus the version scalars in `rules/versions.json`.
   - **New outputs:** `profile/core.json`, `profile/content/*.json` and the `rules/*.json` files above, plus `rules/actionCosts.json` and `rules/combatMatchups.json`. The card-trait rules (`CARD_TRAIT_RULES`) are canonical in `rules/reactionRules.json`; no separate `rules/cardTraits.json` is shipped (D-164).
3. **Schemas and streams.**
   - Run `Tools/infer-schemas.mjs` for the changed shapes:
     - enemies: `attributes`, `equipmentPower`, `blighted`, `levelProfile`;
     - cards: `rank`, `tags`, `costRule`, `damageSchool`, `range`;
     - statuses: `mechanicTags`.
   - Write hand-written schemas for the new rules files.
   - `rules/rng.json`: append the HEAD streams from `src/engine/rng.js STREAM_NAMES`.
4. **Codegen.** `Tools/codegen.d/core2-*.json` fragments for:
   - the profile, rule and snapshot keys;
   - intent types, reward option kinds and rest services;
   - the enums `Stance`, `Camp`, `RangeBand` and `ReactionWindow`.

   Values stay data, never literals (EnforcementTests).
5. **Checks.** The counts in `Tools/ci/check-content.mjs` and the manifest follow automatically.

## 3. Work packages

Each WP delivers, in order:
1. **The oracle, first:** `Tools/oracle-core2-<area>.mjs`, writing `Unity/Assets/Tests/Oracle/core2/<area>/*.json`.
2. **The C# port** in the named namespace.
3. **A parity test,** `Unity/Assets/Tests/EditMode/Shared/Core2<Area>ParityTests.cs`, run by `dotnet test Tests/Domain.Tests`. EnforcementTests stay green.
4. **A BUILD-LOG and DECISIONS entry.**

Every WP pins the same web sha. Size is approximate lines of port.

| WP | Goal | Unity targets | Oracle | Size | Deps |
|---|---|---|---|---|---|
| WP-0 Pipeline | §2.2. Content regenerated at the pinned sha; `transform --check`, `codegen --check` and `check-content` green | `Tools/export*`, `transform*`, `codegen.d/core2-*`, `Content/schemas`, `rules/rng.json` | — | 600 JS | — |
| WP-1 Oracle harness | Shared loader: registries for `default`, `createRunState`, `createRunCombat`, an LCG bot, legal-intent enumeration, projection of the Core fields and previews; plus a registry dump | `Tools/oracle-core2.mjs`, `oracle-core2-registries.mjs` | itself | 500 JS | 0 |
| WP-2 Profile loading | `CoreProfile`; `RegistryBundle` learns the profile content, in its projection order | `Content/Runtime/CoreProfile.cs`, `RegistryBundle.cs`, `RuntimeRegistries.cs` | registries dump | 900 | 1 |
| WP-3 Run creation | Configured `createRunState`: class attributes, stats, hand rows, starter deck, class unlocks, rule snapshots, cinders 20, gear | `Domain/Run/*`, new `CoreRunRules.cs` | `oracle-core2-run` (4 classes × 12 seeds × 2 modes) | 1,200 | 2 |
| WP-4 Combat start, hand and resources | Configured `createCombat`: ratings, persistent poise, barrier, full Stamina, the shared Actions = Stamina pool, hand 4 + INT, draw rows, reshuffle, enemy SP/MP pools, enemy level HP, first intents | `Domain/Combat/CombatStart.cs`, `HandRules.cs`, new `Pools.cs` | `oracle-core2-combat --bot endTurnOnly` | 1,500 | 3 |
| WP-5 Stances and costs | `setProfileStance`, entry grants, stance SP/MP costs, effectiveness 1.25, `applyActionCosts` price derivation, `cardPlayCosts` | new `Stances.cs`, `ActionCosts.cs` | `oracle-core2-costs` | 1,100 | 4; ∥ 6 |
| WP-6 d20 hit resolution | Precision vs evasion, evade stacks, nat 1 and 20, advantage, `fixedDefense` enumeration, the `combatAvoidance` stream | new `HitRoll.cs` | `oracle-core2-hit` grid | 700 | 4; ∥ 5 |
| WP-7 Damage receipt and protections | `profileDamageReceipt` chain; Block→Poise absorb; Barrier→Ward; poise drain and break → Staggered; `MechanicsKernel` subset; every mechanic `cases.json` replayed | new `DamageReceipt.cs`, `Protection.cs`, `MechanicsKernel.cs` | `oracle-core2-damage` | 1,800 | 5, 6 |
| WP-8 Matchups: Counter, Smash, Sweep | Counter arm and return, Sweep vs Counter, Prepared interruption, stance pressure, Evade decay | new `Matchups.cs`, `CounterReturn.cs` | `oracle-core2-combat --bot cards` | 1,500 | 7 |
| WP-9 Statuses and card traits | 101 statuses with Core overrides, status control, Counterstrike/Impact/Flux traits, lifecycle tags, new effect ops | `Statuses.cs`, `Triggers.cs`, new `CardTraits.cs`, `StatusControl.cs`, `EffectOp` | WP-8 fixtures + `oracle-core2-statuses` | 1,600 | 7; ∥ 8 |
| WP-10 Player reaction stack | Windows, offers, `chooseReaction`, LIFO stack (chain limit 16), defer and resume | new `Reactions.cs`; `CombatEngine.cs`, `CombatSnapshot.cs` | `--bot reactions` | 1,400 | 8, 9 |
| WP-11 Enemy AI, reactions, knowledge | SP-budget weighted pick, planned moves, enemy reactions, intent visibility and Perception, `previewEnemyAction` | new `EnemyAi.cs`, `EnemyReactions.cs`, `EnemyKnowledge.cs` | `oracle-core2-enemy` | 1,700 | 10 |
| WP-12 Range and engagement | Starting bands, Advance and throw quotes, routes, `moveCharacter`, enemy closing moves | new `Engagement.cs` | `--bot range` | 900 | 11 |
| WP-13 Snapshot and session | All Core fields serialized; `CombatSession` on the run RNG; saveFormat 3 | `CoreSnapshot*.cs`, `Core2CombatSession.cs` (D-280 to D-286: the Core 2 save is its own format 3, the 405 stays 2) | clone-and-continue in every fixture | 700 | 12 |
| WP-14 Map | `buildGameProfileActMap`, fog, `shopShare`, boss pick, profile hash | `Domain/Map/*` | `oracle-core2-map` (3 acts × 3 seats × 40 seeds) | 500 | 2; ∥ 3–13 |
| WP-15 Rewards and combat end | Offer groups 60/10/30, stance reserve, `lowerRankChance`, claims, Add-to-deck refusals; cinders, skill XP, boss outcome | `Domain/Rewards/*`, `Loop/RewardDoor.cs` | `oracle-core2-rewards` | 1,300 | 13; ∥ 14 |
| WP-16 Rest | Arrival heal, free smith (one tier), level up, refusals | `Domain/Loop/Rest.cs`, `ItemSmithing.cs`, `RunSession.Rest.cs` | `oracle-core2-rest` | 700 | 15; ∥ 17 |
| WP-17 Shop | 3 per category, price 10, persisted stock | `Domain/Shop/*` (legacy merchant deleted) | `oracle-core2-shop` | 400 | 15 |
| WP-18 Deck editor and rules | Bounds, copy limits, sideboard, draft commit, play in deck order | new `Domain/Run/DeckRules.cs`, `Armoury.cs` | `oracle-core2-deck` | 800 | 3; ∥ 4–17 |
| WP-19 Run loop glue | `enterNode` for Core node kinds, combat-end routing, act advance, class unlock ledger, `finishRun` | `Domain/Loop/*`, `Application/Run/*`, `ProfileStore.cs` | `oracle-core2-loop` (mirrors `main.js`) | 1,200 | 14–18 |
| WP-20 Settings and key map | `webKeyMap.json` for the player `settings.*` rows; profile pinning; D11/D12 pacing dials | `Tools/web-key-map.mjs`, `Content/settings`, `Application/Settings` | — | 400 | 0; ∥ all |
| WP-21 De-scope | Record that class mastery, market, sigils, companions, combat rounds and Ashen Blight feats are not part of a Core 2 run; delete the 405 merchant, smith, buy-back and class-tree code after WP-19 | docs | — | 100 | 19 |

**Status 2026-10-24 (WP-C1a):** a Core 2 run is hosted through the Application layer (`Core2RunSession`, `RunHost`, `Core2RunLoop`; D-302): new run, map, travel, fights on the run's streams, save and resume, run end. The rewards, rest, shop and event nodes are answered "not yet ported" until WP-15 to WP-19; `rules/runFlow.json` `newRun.ruleset` was `legacy` until the Unity screens landed (D-303).

**Status 2026-10-24 (WP-C1b):** the Unity screens play Core 2 fights (D-310 to D-315); `newRun.ruleset` is `core2` (D-313). Done from the list below: fighter HUD order, hit-% badge, stance and Staggered, Precision/Evade chips, reaction modal and switch, intent reveal with `?` labels and Perception, Advance/Throw choice, rank badges and trait chips, D11 pacing, the fog map (display types) and the class unlock order (locked tiles). Still to do: the deck editor and Armoury, the Core rest, shop and reward Take / Add to deck (WP-15 to WP-19).

**Status 2026-10-24 (WP-18):** done (D-190 to D-194 the rules, D-400 to D-404 the editor session, projection, Application door and Play in deck order). Left for presentation: a deck editor screen and the Armoury door over `Core2DeckEditor` (D-404).
**Status 2026-10-24 (WP-15):** rewards and the combat end are ported (`Core2Rewards`, `Core2CombatEnd`, `Core2RewardDoor`, `Core2Skills`; D-316 to D-323; oracle `oracle-core2-rewards`). A won fight leads to a persisted offer on `Core2RunSession` (take, add to deck, leave); the spoils screen is a seam (`Core2RewardsView`). The shop stock is WP-17's.

**Status 2026-10-24 (WP-17):** the shop is ported (`Core2Shop`, `Core2RunSession.Shop`, `Core2ShopView`; D-330 to D-333; oracle `oracle-core2-shop`): a merchant node opens a persisted stock of three per kind at ten cinders on the `shop` stream, bought from with the web's refusals; the shop screen is a seam (`Core2ShopView`).

**Status 2026-10-24 (WP-16):** the rest site is ported (`Core2Rest`, `Core2Smith`, `Core2RunSession.Rest`, `Core2RestView`; D-334 to D-340; oracle `oracle-core2-rest`): a rest node heals on arrival, offers the free Smith (one tier, with the live card rows) and Level Up (unreachable in Core, ported), each once; Continue leaves and the site stays enterable until the run travels elsewhere. The rest screen is a seam (`Core2RestView`). The treasure and event nodes are still answered "not yet ported" (WP-19).

**Status 2026-10-24 (WP-19):** done (D-345 to D-350; oracle `oracle-core2-loop`): a Core 2 run is playable from the new run to its end through `Core2RunSession` with no node unanswered. Treasure nodes open the combat reward door, event nodes run the shared event code over the profile (`Core2EventView`, the web's text), a boss advances the act or ends the run, and `Core2RunEnd` writes the record and the class unlock ledger as the web's configured `completedRunMeta` does. Left for presentation: the event screen (the act map's stand-in notice covers it) and the game-over screen's use of `ClassesUnlocked`.

**Critical path:** 0 → 1 → 2 → 3 → 4 → {5 ∥ 6} → 7 → {8 ∥ 9} → 10 → 11 → 12 → 13 → 15 → {16 ∥ 17} → 19.

**Parallel lanes:**
- 14, 18 and 20 once WP-2 and WP-3 exist;
- 16 and 17 together once WP-15 exists (WP-14 alongside).

**Estimate:** about 21k lines of C# and 4k lines of oracle JS.

## 4. Risks

- **Not cleanly headless.** `main.js` owns combat-end routing, the reward door, rest and shop entry, act advance, the class-unlock ledger and the boss-relic choice. Loop oracles must mirror those lines and cite the line ranges. Re-check them whenever the pinned sha moves.
- **Rules DSL.** Port the `mechanicsKernel` subset (`compileBindings`, `queryBinding('parameters')`, `evaluateModifiers`) data-driven, and run every `engine/mechanics/*/cases.json` as tests. The full `engine/runtime` interpreter isn't needed: Core 2's foundation is `legacy-adapter`, so behaviour lives in `src/engine/*.js`. Each new effect op becomes an `EffectOp` member with a unit test.
- **Determinism.**
  - Draw kinds matter: `float` for weighted picks, `int(1,20)` for dice, `pick` for `lowerRankChance`, `chance` for `shopShare`.
  - `pick` consumes a draw even on a one-element array.
  - `reads:'exact'` must not draw.
  - Enemy levels use a seeded hash, not a stream.
  - Ship the profile hash verbatim; never recompute it.
  - Key order after projection is semantic (D-040).
- **Numerics.** Floor at each receipt stage, as the web does. Atomic draft refusals need deep-copy transactions.
- **Co-op residue.** Port only the single-seat path, and assert there is one seat.
- **Content churn.** Pin the export to a sha, recorded in `manifest.json` and BUILD-LOG. Re-pinning is its own WP.
- **Presentation afterwards (list only):**
  - the fighter HUD order Evade/Poise/Ward/Block/HP;
  - the hit-% badge;
  - stance icons and the Staggered state;
  - Precision/Evade chips;
  - the reaction modal and reaction switch;
  - intent reveal, the `?` labels and the Perception UI;
  - range bands and the Advance/Throw choice;
  - rank badges, derived costs and trait chips;
  - D11 pacing;
  - the deck editor and Armoury out of combat;
  - the Core rest, the 3-per-category shop and the reward Take / Add to deck;
  - the fog map with shops;
  - the class unlock order;
  - one-tap off by default.
