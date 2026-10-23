using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DN = Ashen.Generated.Core2DamageNumbers;
using DV = Ashen.Generated.Core2DamageValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.Core2ReactionKeys;
using RM = Ashen.Generated.Core2ReactionMessages;
using RN = Ashen.Generated.Core2ReactionNumbers;
using RV = Ashen.Generated.Core2ReactionValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The reaction windows around a committed action and the player's reaction stack (web engine/combatReactions.js, engine/enemyReactions.js
    /// fightReacts and playerRootSeat). Before an enemy's hit lands on the player, a hand card that carries the Reaction trait and could answer it is
    /// offered: the fight pauses on <c>pendingReaction</c> (an id, the owner, the attacker and every option with its price) until <c>chooseReaction</c>
    /// answers. A choice is committed to a LIFO stack (<c>reactionStack</c>); when nobody is offered again the stack resolves top first, each card played
    /// as a reaction (off the player's turn, with the queue it interrupted held in <c>reactionResume</c>) before the hit goes on. A fight whose player has
    /// the switch off is told so (one receipt per incoming action, only when a card in the hand could have answered), and the hit goes on. Enemy
    /// reactions (a reaction move of an enemy answering a card or a reaction) are the enemy work package's (WP-11): refused by name.
    /// </summary>
    public static class CoreReactions
    {
        private static bool Positive(JObject contact) => contact != null && (contact.Num(CK.Amount) > 0 || contact.Num(DK.Pressure) > 0);

        private static JObject Entity(CombatState c, string id) => id == null ? null : Triggers.FindEntity(c, id);

        // ------------------------------------------------------------------ the rules

        /// <summary>usesReactions(ctx): reaction rules version 1 or 2.</summary>
        public static bool Active(CombatState c) => c.CoreData.ReactionVersion >= 1;

        /// <summary>stackedReactions(ctx): version 2 (card traits and the reaction stack).</summary>
        public static bool Stacked(CombatState c) => c.CoreData.ReactionVersion == DN.ReactionStackVersion;

        /// <summary>reactionPaused(ctx): an offer, a card held mid-play or a choice is waiting.</summary>
        public static bool Paused(CombatState c) => c.Fields[DK.PendingReaction] != null || c.Fields[DK.ReactionResume] != null || CoreCardZones.HasChoice(c);

        /// <summary>enemyReactionsActive(ctx): version 2 with the flag.</summary>
        private static bool EnemyReactionsActive(CombatState c) => Stacked(c) && c.Fields.Obj(CK.ReactionRules)?.Is(DK.EnemyReactions) == true;

        /// <summary>reactionMovesOf(ctx, enemy, window): the enemy's reaction moves that list the window, in authored order (a locked move once it is unlocked).</summary>
        private static List<KeyValuePair<string, JObject>> ReactionMovesOf(CombatState c, JObject enemy, string window)
        {
            var moves = c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Obj(K.Moves);
            var unlocked = enemy.Arr(RK.UnlockedMoves);
            var rows = new List<KeyValuePair<string, JObject>>();
            if (moves == null) return rows;
            foreach (var p in moves.Properties())
                if (p.Value is JObject move && Js.Includes(move.Obj(DK.Reaction)?[K.On], window) && (!move.Is(RK.Locked) || (unlocked != null && Js.Includes(unlocked, p.Name))))
                    rows.Add(new KeyValuePair<string, JObject>(p.Name, move));
            return rows;
        }

        /// <summary>fightReacts(ctx, window): any enemy of the fight holds a reaction move for the window (for either window without one).</summary>
        public static bool FightReacts(CombatState c, string window = null)
        {
            if (!EnemyReactionsActive(c)) return false;
            var windows = window != null ? new[] { window } : new[] { RV.WindowPlayerAction, RV.WindowPlayerReaction };
            return c.Enemies.Any(enemy => windows.Any(name => ReactionMovesOf(c, enemy, name).Count > 0));
        }

        /// <summary>incomingAgainst(group, id): the largest positive contact amount a committed group aims at the creature.</summary>
        private static double IncomingAgainst(ExpansionGroup group, string id)
        {
            double best = 0;
            if (group == null) return best;
            foreach (var pair in group.Targets.Entries())
            {
                if (pair.Value.Id != id) continue;
                foreach (var contact in pair.Value.Contacts) if (contact.Num(CK.Amount) > best) best = contact.Num(CK.Amount);
            }
            return best;
        }

        /// <summary>
        /// decideEnemyReaction(ctx, enemy, window, ...): whether the enemy answers with a reaction move for this window. An enemy that would react, or
        /// whose chance is drawn on the enemyReaction stream, is the enemy package's (WP-11): refused by name. Every other outcome (no move that
        /// qualifies, an enemy that cannot act) is "no reaction" and draws nothing.
        /// </summary>
        private static void DecideEnemyReaction(CombatState c, JObject enemy, string window, double incoming)
        {
            if (!EnemyReactionsActive(c) || !enemy.Is(K.Alive) || enemy.Is(K.SkipNextTurn)) return;
            if (Js.IsNum(enemy[RK.CombatReactionTurn]) && enemy.Num(RK.CombatReactionTurn) == c.Turn) return;
            if (CoreControl.Locked(c, enemy)) return;
            foreach (var row in ReactionMovesOf(c, enemy, window))
            {
                var reaction = row.Value.Obj(DK.Reaction);
                var minIncoming = Js.IsNum(reaction[RK.MinIncoming]) ? reaction.Num(RK.MinIncoming) : 1;
                var chance = Js.IsNum(reaction[RK.Chance]) ? reaction.Num(RK.Chance) : 1;
                if (minIncoming != 0 && !(incoming > 0 && incoming >= minIncoming)) continue;
                if (chance >= 1) throw new NotSupportedException(DM.EnemyReactionsNotPorted);
                if (chance <= 0) continue;
                // A candidate state reads the memo only: an unmemoised roll there is "does not react" and never draws.
                if (c.ReactionPlaying) continue;
                throw new NotSupportedException(DM.EnemyReactionsNotPorted);
            }
        }

        /// <summary>haltReason(ctx, source): why an attacker cannot go on with its action, if it cannot.</summary>
        public static string HaltReason(CombatState c, JObject source) =>
            source.Is(K.SkipNextTurn) ? DV.ReasonAttackerStaggered : CoreControl.Locked(c, source) ? DV.ReasonAttackerControlled : null;

        private static bool SeatPresent(CombatState c) => c.Player.Is(K.Alive);

        /// <summary>seatGone(ctx, playerId): the seat is dead (or at 0 Health before the end check marks it).</summary>
        private static bool SeatGone(CombatState c) => !c.Player.Is(K.Alive) || c.Player.Num(K.Hp) <= 0;

        /// <summary>playerRootSeat(ctx, group): the acting seat of a player-card root ("player" in a solo fight), else null.</summary>
        public static string PlayerRootSeat(CombatState c, ExpansionGroup group) =>
            group?.Carrier == null || !Js.Truthy(group.Carrier[K.InstanceId]) || c.Enemies.Any(enemy => enemy.Str(K.Id) == group.SourceId) ? null : V.Player;

        private static TargetRow PlayerRow(CombatState c, ExpansionGroup group)
        {
            foreach (var pair in group.Targets.Entries()) if (pair.Value.Id == c.Player.Str(K.Id)) return pair.Value;
            return null;
        }

        private static bool ContactTargetPresent(CombatState c, ExpansionGroup group) =>
            group.Targets.Entries().Any(pair => pair.Value.Contacts.Any(Positive) && Entity(c, pair.Value.Id)?.Is(K.Alive) == true);

        private static List<string> TagsOf(JObject def) => CoreStances.TagIds(def) ?? new List<string>();

        private static bool ReactionsOff(JObject entity) => entity[DK.ReactionsEnabled]?.Type == JTokenType.Boolean && !entity.Is(DK.ReactionsEnabled);

        /// <summary>setReactionsEnabled(ctx, entity, enabled): the player's switch, changed between actions on the player's turn.</summary>
        public static void SetReactions(CombatState c, JToken enabled)
        {
            if (!Active(c)) throw new InvalidOperationException(RM.ReactionsUnavailable);
            if (enabled == null || enabled.Type != JTokenType.Boolean) throw new InvalidOperationException(RM.ReactionPreference);
            if (c.Phase != V.PhasePlayer || Paused(c)) throw new InvalidOperationException(RM.ChangeBetweenActions);
            c.Player[DK.ReactionsEnabled] = enabled.DeepClone();
        }

        // ------------------------------------------------------------------ what could answer

        /// <summary>The hand cards that could answer an attack by their trait (version 2: the Reaction trait).</summary>
        private static bool CandidateByTrait(JObject def) => TagsOf(def).Contains(DV.TagTraitReaction);

        private static List<string> CandidateCardIds(CombatState c)
        {
            var ids = new List<string>();
            foreach (var inst in c.Piles.Hand)
            {
                if (CandidateByTrait(CoreCosts.Resolve(c, inst)) && !ids.Contains(inst.Str(K.CardId))) ids.Add(inst.Str(K.CardId));
            }
            return ids;
        }

        private static string AttackKind(TargetRow row)
        {
            var hit = row.Contacts.FirstOrDefault(contact => Positive(contact) && !contact.Is(DK.Effect));
            if (hit == null) return DV.AttackStatusOnly;
            if (hit.Str(CK.Maneuver) == CV.StanceSweep) return DV.AttackSweep;
            if (hit.Str(CK.Targeting) == CV.TargetingArea) return DV.AttackArea;
            if (hit.Str(CK.Camp) == CV.CampSpell) return DV.AttackSpell;
            if (hit.Str(CK.Range) == CV.RangeReach || hit.Str(CK.Range) == CV.RangeRanged) return DV.AttackRanged;
            return DV.AttackAttack;
        }

        /// <summary>eligible(ctx, def, row): a Reaction-trait card answers a hit; a Counter also needs its coverage to reach the hit.</summary>
        private static bool Eligible(CombatState c, JObject def, TargetRow row)
        {
            var tags = TagsOf(def);
            if (!tags.Contains(DV.TagTraitReaction)) return false;
            return tags.Contains(CV.TagManeuver + CV.StanceCounter)
                ? row.Contacts.Any(contact => CoreMatchups.CounterCoversAttack(c, def, contact, contact.Str(DK.Effect) ?? Ashen.Generated.Core2MatchupValues.EffectDamage))
                : row.Contacts.Any(Positive);
        }

        private static readonly Dictionary<string, int> ReasonRank = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RV.ReasonNotCovered] = RN.RankCovered, [RV.ReasonOther] = RN.RankOther, [RV.ReasonControlled] = RN.RankControlled, [RV.ReasonUnplayable] = RN.RankUnplayable, [RV.ReasonCost] = RN.RankCost,
        };

        // ------------------------------------------------------------------ receipts

        /// <summary>reportUnavailable: at most one receipt per incoming root and seat, and only for a seat holding a candidate.</summary>
        private static void ReportUnavailable(CombatState c, ExpansionGroup group, string id, TargetRow row, string reason, JObject extra = null)
        {
            if (group.ReactionNoticed != null && group.ReactionNoticed.Contains(id)) return;
            var cardIds = CandidateCardIds(c);
            if (cardIds.Count == 0) return;
            (group.ReactionNoticed ??= new List<string>()).Add(id);
            var payload = Js.Obj(DK.OwnerId, id, K.SourceId, group.SourceId, K.Reason, reason, K.CardId, extra != null && extra.Has(K.CardId) ? extra[K.CardId] : (JToken)cardIds[0], DK.CardIds, new JArray(cardIds), DK.AttackKind, AttackKind(row));
            if (extra != null) foreach (var p in extra.Properties()) if (p.Name != K.CardId && p.Name != K.Reason) payload[p.Name] = p.Value.DeepClone();
            c.Emit(DK.CombatReactionUnavailable, payload);
        }

        /// <summary>reportRestOfRoot: the one offer covers the whole root; the seat hears once that the rest of its hits cannot be answered.</summary>
        private static void ReportRestOfRoot(CombatState c, ExpansionGroup group)
        {
            if (group.ContactSequence != null) return;
            if (PlayerRootSeat(c, group) != null) return;
            var row = PlayerRow(c, group);
            if (row == null || row.Contacts.Count(Positive) < RN.MinimumHitsForRestReceipt || !SeatPresent(c)) return;
            var source = c.Enemies.FirstOrDefault(enemy => enemy.Str(K.Id) == group.SourceId);
            if (source != null && (!source.Is(K.Alive) || source.Is(K.SkipNextTurn) || CoreControl.Locked(c, source))) return;
            ReportUnavailable(c, group, V.Player, row, DV.ReasonAlreadyAnswered);
        }

        /// <summary>reportAlreadyAnswered: a later action of an answered root is told, once, that it cannot be answered again.</summary>
        private static void ReportAlreadyAnswered(CombatState c, CombatAction action, ExpansionGroup group)
        {
            // Per-hit choices have already settled this contact, not the later hits; those contacts get their own offer.
            if (group.ContactSequence != null) return;
            var contacts = action.Meta.ExpansionContacts?.SelectMany(batch => batch) ?? Enumerable.Empty<ContactRef>();
            foreach (var id in (group.ReactionAnswered ?? new List<string>()).ToList())
            {
                var row = PlayerRow(c, group);
                if (row == null || (group.ReactionNoticed?.Contains(id) == true) || !SeatPresent(c)) continue;
                if (!contacts.Any(contact => group.Targets.TryGetValue(contact.Key, out var r) && r == row && Positive(row.Contacts[contact.Index]))) continue;
                ReportUnavailable(c, group, id, row, DV.ReasonAlreadyAnswered);
            }
        }

        // ------------------------------------------------------------------ the stack

        /// <summary>The committed reaction stack of this incoming root (version 2 only).</summary>
        private static JObject StackOf(CombatState c, ExpansionGroup group)
        {
            if (!Stacked(c)) return null;
            var stack = c.Fields.Obj(DK.ReactionStack);
            return stack != null && stack.Str(RK.GroupKey) == group.Key ? stack : null;
        }

        private static JArray EntriesOf(JObject stack) => stack?.Arr(CK.Entries) ?? new JArray();

        private static bool IsPlayerEntry(JToken entry) => entry is JObject e && e.Str(RK.Side) == V.Player;

        /// <summary>Version 2 root ended: the attacker is dead (or a player-sourced root's seat is gone). Every entry still stacked then fizzles.</summary>
        private static bool RootEnded(CombatState c, ExpansionGroup group)
        {
            var source = c.Enemies.FirstOrDefault(enemy => enemy.Str(K.Id) == group.SourceId);
            if (source != null) return !source.Is(K.Alive);
            return PlayerRootSeat(c, group) != null && SeatGone(c);
        }

        /// <summary>cancelEndedPlayerRoot: a player-card root whose acting seat is gone once its stack has resolved is cancelled.</summary>
        private static void CancelEndedPlayerRoot(CombatState c, ExpansionGroup group)
        {
            if (PlayerRootSeat(c, group) != null && SeatGone(c)) group.Cancelled = true;
            RestoreRootPlay(c);
        }

        private static ExpansionGroup GroupOf(CombatState c, string key) => c.Queue.FirstOrDefault(action => action.Meta?.Group?.Key == key)?.Meta.Group;

        // ------------------------------------------------------------------ the offer

        private static int Rank(string reason) => ReasonRank.TryGetValue(reason ?? string.Empty, out var rank) ? rank : -1;

        private static string Capitalized(string id) => id.Length == 0 ? id : char.ToUpperInvariant(id[0]).ToString(CultureInfo.InvariantCulture) + id.Substring(1);

        /// <summary>
        /// optionsFor(ctx, row, group, hooks, refusal, seatId): every way a card of the hand answers the hit. Each candidate is played on a private
        /// copy of the fight (the stacked cards after it, top first, as they would resolve) and is an option only if it pays and plays; the refusal
        /// names the first failing check of the candidate that got furthest.
        /// </summary>
        private static List<JObject> OptionsFor(CombatState c, TargetRow row, ExpansionGroup group, JObject refusal)
        {
            var options = new List<JObject>();
            JObject inst = null;
            void Note(string reason, JObject extra = null)
            {
                if (refusal == null || (refusal.Has(K.Reason) && !(Rank(reason) > Rank(refusal.Str(K.Reason))))) return;
                refusal.RemoveAll();
                refusal[K.Reason] = reason;
                if (extra != null) foreach (var p in extra.Properties()) refusal[p.Name] = p.Value.DeepClone();
                refusal[K.CardId] = inst[K.CardId].DeepClone();
            }
            // Version 2: stacked instances are not offered again, and a seat holding a Counter entry is offered no further Counter.
            var entries = EntriesOf(StackOf(c, group)).OfType<JObject>().ToList();
            if (entries.Any(entry => !IsPlayerEntry(entry))) throw new NotSupportedException(DM.EnemyReactionsNotPorted);
            var counterStacked = entries.Any(entry => entry.Str(RK.Maneuver) == CV.StanceCounter);
            // A pushed card's contacts against each enemy decide the playerReaction window.
            var captureIncoming = FightReacts(c, RV.WindowPlayerReaction);
            // The rows the seat answers on this root: the root's own row against its attacker.
            var threats = new List<(string enemyId, TargetRow row)>();
            if (row != null && PlayerRootSeat(c, group) == null) threats.Add((group.SourceId, row));
            foreach (var card in c.Piles.Hand.ToList())
            {
                inst = card;
                if (entries.Any(entry => entry.Obj(RK.Play)?.Str(RK.CardInstanceId) == inst.Str(K.InstanceId))) continue;
                var def = CoreCosts.Resolve(c, inst);
                if (counterStacked && TagsOf(def).Contains(CV.TagManeuver + CV.StanceCounter)) continue;
                var answered = threats.Where(threat => Eligible(c, def, threat.row)).ToList();
                if (answered.Count == 0) { if (CandidateByTrait(def)) Note(RV.ReasonNotCovered); continue; }
                var choices = CoreCardPlay.ChoiceOptions(def) ?? new List<string> { null };
                var immediate = CoreStances.ProfileFor(c, def).Str(CK.Maneuver) == CV.StanceCounter
                    ? Js.Items(def[K.Effects]).OfType<JObject>().Where(e => e.Str(K.Op) != Ashen.Generated.CombatOps.Damage && e.Str(K.Op) != Ashen.Generated.CombatOps.PoiseDamage)
                    : Js.Items(def[K.Effects]).OfType<JObject>();
                var aimed = immediate.Any(e => CoreSets.HostileSpecs.Contains(e.Str(K.Target)));
                var aims = aimed ? answered.Select(threat => threat.enemyId).Distinct().ToList() : new List<string> { answered[0].enemyId };
                if (def[RK.Upcast] != null) throw new NotSupportedException(RM.UpcastNotPorted);
                var routes = c.Fields.Obj(CK.RangeState) != null && TagsOf(def).Contains(CV.TagManeuver + CV.StanceCounter) ? new List<string> { DV.RouteAdvance, DV.RouteThrow } : new List<string> { null };
                foreach (var rangeRoute in routes)
                    foreach (var aim in aims)
                        foreach (var choice in choices)
                        {
                            var candidate = c.Detach();
                            candidate.Fields.Remove(DK.PendingReaction);
                            candidate.Fields.Remove(DK.ReactionResume);
                            candidate.ReactionPlaying = true;
                            candidate.Fields[Ashen.Generated.Core2StatusKeys.ReactionSourceId] = aim;
                            candidate.Fields[DK.PendingExpansionActions] = 0;
                            var play = new JObject { [RK.CardInstanceId] = inst[K.InstanceId].DeepClone() };
                            if (aimed) play[RK.TargetId] = aim;
                            if (choice != null) play[RK.Choice] = choice;
                            if (rangeRoute != null) play[RK.RangeRoute] = rangeRoute;
                            try
                            {
                                if (captureIncoming) candidate.GroupSink = new List<ExpansionGroup>();
                                var restoreRange = CoreRange.IncomingScope(candidate, group, candidate.Player);
                                try { CoreCardPlay.Play(candidate, inst.Str(K.InstanceId), aimed ? aim : null, choice == null ? null : (JToken)choice, rangeRoute); }
                                finally { restoreRange(); }
                                var planned = candidate.GroupSink?.FirstOrDefault(row2 => row2.Carrier?.Str(K.InstanceId) == inst.Str(K.InstanceId));
                                candidate.GroupSink = null;
                                var paid = candidate.EventLog.FirstOrDefault(e => e.Str(K.Type) == DK.CardPlayed);
                                if (paid == null || !candidate.Player.Is(K.Alive)) { Note(RV.ReasonOther); continue; }
                                var reservation = candidate.Player.Obj(CK.CombatCounter)?.Obj(CK.Carrier)?.Obj(DK.RangeCounterReservation);
                                if (rangeRoute == DV.RouteThrow && !(Js.Items(reservation?.Obj(RK.RangePlan)?[RK.Targets]).OfType<JObject>().Any(target => target.Num(DK.MissingBands) > 0))) continue;
                                // The stacked entries resolve after this option, last pushed first; all must play.
                                foreach (var entry in Enumerable.Reverse(entries))
                                {
                                    if (RootEnded(candidate, group)) break;
                                    var start = candidate.EventLog.Count;
                                    var stacked = entry.Obj(RK.Play);
                                    CoreCardPlay.Play(candidate, stacked.Str(RK.CardInstanceId), stacked.Str(RK.TargetId), stacked[RK.Choice], stacked.Str(RK.RangeRoute));
                                    if (!candidate.EventLog.Skip(start).Any(e => e.Str(K.Type) == DK.CardPlayed)) throw new InvalidOperationException(RM.StackedNotPlayed);
                                }
                                var id = string.Join(DV.KeySeparator, inst.Str(K.InstanceId), 0.ToString(CultureInfo.InvariantCulture), choice ?? RV.ChoiceDefault) + (rangeRoute == DV.RouteThrow ? DV.KeySeparator + RV.ThrowSuffix : string.Empty);
                                var option = new JObject
                                {
                                    [K.Id] = id, [K.Name] = def[K.Name]?.DeepClone() ?? Js.Null(), [K.CardId] = inst[K.CardId].DeepClone(), [RK.Play] = play,
                                    [RK.StaminaCost] = Js.N(!Js.Nullish(paid[DK.StaminaSpent]) ? paid.Num(DK.StaminaSpent) : paid.Or0(DK.EnergySpent)),
                                    [RK.ManaCost] = Js.N(paid.Or0(DK.ManaSpent)), [RK.ChoiceName] = choice == null ? Js.Null() : (JToken)Capitalized(choice),
                                };
                                if (captureIncoming)
                                {
                                    var incoming = new JObject();
                                    foreach (var enemy in c.Enemies)
                                    {
                                        var amount = IncomingAgainst(planned, enemy.Str(K.Id));
                                        if (amount > 0) incoming[enemy.Str(K.Id)] = Js.N(amount);
                                    }
                                    option[RK.Incoming] = incoming;
                                }
                                options.Add(option);
                            }
                            catch (NotSupportedException) { throw; }
                            catch (InvalidOperationException error)
                            {
                                // Ineligible under the normal card, payment and control rules; the refusal names why.
                                var refused = error as PlayRefusalException;
                                Note(refused != null && ReasonRank.ContainsKey(refused.Reason ?? string.Empty) ? refused.Reason : RV.ReasonOther, refused?.Extra);
                            }
                        }
            }
            return options;
        }

        /// <summary>beforeReactionAction(ctx, action): true while the action must wait for a choice.</summary>
        public static bool Before(CombatState c, CombatAction action)
        {
            CoreExpansion.Activate(c, action);
            if (!Active(c) || c.ReactionPlaying) return false;
            if (c.Fields[DK.PendingReaction] != null) return true;
            var group = action.Meta?.Group;
            if (group == null || group.Carrier.Is(DK.ReactionDepth)) return false;
            if (action.Source?.Str(K.Kind) != V.Enemy) return !group.Begun && action.Source?.Str(K.Kind) == V.Player && BeforePlayerRoot(c, group);
            if (group.Begun)
            {
                ReportAlreadyAnswered(c, action, group);
                return false;
            }
            if (c.Fields.Obj(CK.ReactionRules)?.Str(DK.ContactPolicy) == DV.PolicyPerHit && !group.ContactTelegraphed && action.Source.Is(K.Alive) && HaltReason(c, action.Source) == null && ContactTargetPresent(c, group))
            {
                group.ContactTelegraphed = true;
                var payload = Js.Spread(group.EnemyMoveStart ?? group.CounterReturn);
                payload[K.SourceId] = action.Source[K.Id].DeepClone();
                payload[DK.ContactKey] = group.Key;
                payload[K.Kind] = DV.KindAttack;
                payload[DK.ContactIndex] = group.ContactSequence != null ? group.ContactSequence.Index : 0;
                payload[DK.ContactCount] = group.ContactSequence != null ? group.ContactSequence.Contacts.Count : 1;
                c.Emit(DK.CombatContactPrepared, payload);
            }
            if (!Stacked(c)) throw new NotSupportedException(RM.ReactionRulesVersionOne);
            return BeforeStacked(c, action, group);
        }

        /// <summary>
        /// beforeStackedReaction: the seat is offered again after every card it pushes, until it passes, has no option left or reaches the depth cap. A
        /// stack that exists for this root is kept even if the attacker is now halted or dead; it only stops new offers.
        /// </summary>
        private static bool BeforeStacked(CombatState c, CombatAction action, ExpansionGroup group)
        {
            var stack = StackOf(c, group);
            if (stack == null)
            {
                if (!action.Source.Is(K.Alive)) return false;
                var halted = HaltReason(c, action.Source);
                if (halted != null)
                {
                    var row = PlayerRow(c, group);
                    if (row != null && row.Contacts.Any(Positive) && !(group.ReactionAnswered?.Contains(V.Player) == true) && SeatPresent(c)) ReportUnavailable(c, group, V.Player, row, halted);
                    return false;
                }
            }
            var open = action.Source.Is(K.Alive) && HaltReason(c, action.Source) == null;
            return OfferSeat(c, group, open);
        }

        /// <summary>
        /// Enemy reactions, playerAction window: before a player card's root group begins, the enemies it hits decide in roster order. An enemy that
        /// would push is the enemy package's (WP-11); with none that does, the root runs as it would without the windows.
        /// </summary>
        private static bool BeforePlayerRoot(CombatState c, ExpansionGroup group)
        {
            if (!Stacked(c) || !FightReacts(c)) return false;
            var acting = PlayerRootSeat(c, group);
            if (acting == null || group.Carrier.Is(DK.CombatReaction) || group.CounterReturn != null) return false;
            if (StackOf(c, group) == null)
            {
                // A stack whose last entry paused for its own choice has emptied: the root resumes.
                if (group.EnemyWindows != null)
                {
                    if (c.Fields[DK.ReactionStack] == null) RestoreRootPlay(c);
                    return false;
                }
                if (c.Fields[DK.ReactionStack] != null || SeatGone(c)) return false;
                OpenEnemyWindow(c, group, RV.WindowPlayerAction, null);
                if (StackOf(c, group) == null) return false;
            }
            return OfferSeat(c, group, !SeatGone(c));
        }

        /// <summary>
        /// openEnemyWindow(ctx, group, window, pushed): one enemy window on the root. playerAction: the enemies the root card hits with a positive
        /// contact. playerReaction: the enemies that own the (non Counter-return) root or that the root card hits; each qualifies on the pushed card's contact
        /// against it. Roster order, each enemy at most once.
        /// </summary>
        private static void OpenEnemyWindow(CombatState c, ExpansionGroup group, string window, JObject pushed)
        {
            if (!FightReacts(c, window) || group.CounterReturn != null || group.Carrier.Is(DK.CombatReaction) || group.Carrier.Is(DK.ReactionDepth)) return;
            var index = group.EnemyWindows ?? 0;
            group.EnemyWindows = index + 1;
            if (window == RV.WindowPlayerAction ? PlayerRootSeat(c, group) == null : pushed == null) return;
            foreach (var enemy in c.Enemies)
            {
                var incoming = window == RV.WindowPlayerAction ? IncomingAgainst(group, enemy.Str(K.Id)) : pushed.Obj(RK.Incoming)?.Or0(enemy.Str(K.Id)) ?? 0;
                // The window opens for enemies the root card targets, or (playerReaction) that own the root or that the root card targets.
                if (window == RV.WindowPlayerAction ? !(incoming > 0) : group.SourceId != enemy.Str(K.Id) && !(IncomingAgainst(group, enemy.Str(K.Id)) > 0)) continue;
                DecideEnemyReaction(c, enemy, window, incoming);
            }
        }

        private static double ChainCap(CombatState c, ExpansionGroup group)
        {
            var rules = c.Fields.Obj(CK.ReactionRules);
            if (rules?.Str(DK.ContactPolicy) == DV.PolicyPerHit) return 1;
            if (group.CounterReturn != null) return rules.Obj(RK.Stack).Num(RK.CounterReturnMaxDepth);
            var limit = c.CoreData.Configured(CK.ReactionsModule)?[RK.ChainLimit];
            return Js.IsFinite(limit) ? Js.D(limit) : rules.Obj(RK.Stack).Num(RK.MaxDepth);
        }

        /// <summary>
        /// The seat is offered again after every card it pushes, until it passes, has no option left or reaches the depth cap; then the stack
        /// resolves.
        /// </summary>
        private static bool OfferSeat(CombatState c, ExpansionGroup group, bool open)
        {
            var stack = StackOf(c, group);
            var entries = EntriesOf(stack).OfType<JObject>().ToList();
            var playerRoot = PlayerRootSeat(c, group) != null;
            var cap = ChainCap(c, group);
            group.ReactionAnswered ??= new List<string>();
            var id = V.Player;
            var row = playerRoot ? null : PlayerRow(c, group);
            var skip = (row == null || !row.Contacts.Any(Positive)) || group.ReactionAnswered.Contains(id);
            if (!skip)
            {
                var depth = entries.Count(IsPlayerEntry);
                var entity = c.Player;
                if (!open || depth >= cap || !entity.Is(K.Alive) || ReactionsOff(entity))
                {
                    if (row != null && open && depth < cap && ReactionsOff(entity) && SeatPresent(c)) ReportUnavailable(c, group, id, row, DV.ReasonSwitchOff);
                    group.ReactionAnswered.Add(id);
                }
                else
                {
                    var refusal = new JObject();
                    var options = OptionsFor(c, row, group, refusal);
                    if (options.Count == 0)
                    {
                        if (depth == 0 && row != null)
                        {
                            var reason = refusal.Has(K.Reason) ? refusal.Str(K.Reason) : RV.ReasonOther;
                            refusal.Remove(K.Reason);
                            ReportUnavailable(c, group, id, row, reason, refusal);
                        }
                        group.ReactionAnswered.Add(id);
                    }
                    else
                    {
                        var offerId = string.Join(DV.KeySeparator, group.Key, id, depth.ToString(CultureInfo.InvariantCulture));
                        c.Fields[DK.PendingReaction] = new JObject
                        {
                            [K.Id] = offerId, [RK.GroupKey] = group.Key, [DK.OwnerId] = id, [K.SourceId] = group.SourceId, [RK.Options] = new JArray(options),
                        };
                        c.Emit(RV.EventCombatReactionOffered, Js.Obj(RK.OfferId, offerId, DK.OwnerId, id, K.SourceId, group.SourceId));
                        return true;
                    }
                }
            }
            return entries.Count > 0 ? Resolve(c) : false;
        }

        // ------------------------------------------------------------------ resolving the stack

        /// <summary>
        /// resolveReactionStack(ctx): resolves the committed stack top first; true while a paid entry's own choice pauses it. The remaining entries
        /// stay on the stack and resume when the paused action is handed back, through the same <see cref="Before"/> call.
        /// </summary>
        public static bool Resolve(CombatState c)
        {
            var stack = c.Fields.Obj(DK.ReactionStack);
            var entries = stack?.Arr(CK.Entries);
            if (entries == null || entries.Count == 0) return false;
            var group = GroupOf(c, stack.Str(RK.GroupKey));
            if (group == null) throw new InvalidOperationException(RM.NoIncomingAction);
            stack[RK.Resolving] = true;
            while (entries.Count > 0)
            {
                // Checked after every resolved entry: a root that has ended fizzles what is left.
                if (RootEnded(c, group))
                {
                    while (entries.Count > 0)
                    {
                        var dead = (JObject)entries[entries.Count - 1];
                        entries.RemoveAt(entries.Count - 1);
                        if (!IsPlayerEntry(dead)) throw new NotSupportedException(DM.EnemyReactionsNotPorted);
                        c.Emit(RV.EventCombatReactionFizzled, Js.Obj(DK.OwnerId, dead[DK.OwnerId], RK.CardInstanceId, dead.Obj(RK.Play)[RK.CardInstanceId], K.Reason, RV.ReasonRootEnded));
                    }
                    c.Fields.Remove(DK.ReactionStack);
                    CancelEndedPlayerRoot(c, group);
                    return false;
                }
                var entry = (JObject)entries[entries.Count - 1];
                entries.RemoveAt(entries.Count - 1);
                if (!IsPlayerEntry(entry)) throw new NotSupportedException(DM.EnemyReactionsNotPorted);
                if (PlayStackEntry(c, group, entry))
                {
                    if (entries.Count == 0) c.Fields.Remove(DK.ReactionStack);
                    return true;
                }
            }
            c.Fields.Remove(DK.ReactionStack);
            CancelEndedPlayerRoot(c, group);
            return false;
        }

        /// <summary>The queue the reaction interrupts is held aside (reactionResume) while the card plays on an empty one.</summary>
        private static void HoldQueue(CombatState c)
        {
            c.ReactionResumeQueue = c.Queue.ToList();
            c.Fields[DK.ReactionResume] = new JObject { [RK.Queue] = c.ReactionResumeQueue.Count, [RK.Foundation] = Js.Null() };
            c.Queue.Clear();
        }

        /// <summary>
        /// One entry under today's single-reaction mechanics. An entry whose card has left the hand, or whose play is refused, fizzles and pays nothing.
        /// Returns true when it paused.
        /// </summary>
        private static bool PlayStackEntry(CombatState c, ExpansionGroup group, JObject entry)
        {
            var ownerId = entry.Str(DK.OwnerId);
            var play = entry.Obj(RK.Play);
            var instanceId = play.Str(RK.CardInstanceId);
            // A seat that died after committing pays nothing and plays nothing.
            if (!SeatPresent(c))
            {
                c.Emit(RV.EventCombatReactionFizzled, Js.Obj(DK.OwnerId, ownerId, RK.CardInstanceId, instanceId, K.Reason, RV.ReasonOwnerGone));
                return false;
            }
            var instance = c.Piles.Hand.FirstOrDefault(inst => inst.Str(K.InstanceId) == instanceId);
            if (instance == null)
            {
                c.Emit(RV.EventCombatReactionFizzled, Js.Obj(DK.OwnerId, ownerId, RK.CardInstanceId, instanceId, K.Reason, RV.ReasonNotInHand));
                ReportRestOfRoot(c, group);
                return false;
            }
            // One Counter per seat per stack, read from the card itself rather than the saved entry.
            if (TagsOf(CoreCosts.Resolve(c, instance)).Contains(CV.TagManeuver + CV.StanceCounter) && c.Fields.Obj(DK.ReactionStack) != null)
            {
                var stack = c.Fields.Obj(DK.ReactionStack);
                var seats = stack.Arr(RK.CounterSeats);
                if (seats == null) stack[RK.CounterSeats] = seats = new JArray();
                if (seats.Any(seat => Js.Str(seat) == ownerId))
                {
                    c.Emit(RV.EventCombatReactionFizzled, Js.Obj(DK.OwnerId, ownerId, RK.CardInstanceId, instanceId, K.Reason, RV.ReasonSecondCounter));
                    ReportRestOfRoot(c, group);
                    return false;
                }
                seats.Add(ownerId);
            }
            HoldQueue(c);
            c.ReactionPlaying = true;
            c.Fields[Ashen.Generated.Core2StatusKeys.ReactionSourceId] = entry[K.SourceId]?.DeepClone() ?? Js.S(group.SourceId);
            PlayRefusalException refusal = null;
            var restoreRange = CoreRange.IncomingScope(c, group, c.Player);
            try { CoreCardPlay.Play(c, instanceId, play.Str(RK.TargetId), play[RK.Choice], play.Str(RK.RangeRoute)); }
            catch (PlayRefusalException error) { refusal = error; }
            finally
            {
                restoreRange();
                c.ReactionPlaying = false;
                c.Fields.Remove(Ashen.Generated.Core2StatusKeys.ReactionSourceId);
            }
            if (refusal != null)
            {
                RestoreQueue(c);
                c.Emit(RV.EventCombatReactionFizzled, Js.Obj(DK.OwnerId, ownerId, RK.CardInstanceId, instanceId, K.Reason, refusal.Reason ?? RV.ReasonOther));
                ReportRestOfRoot(c, group);
                return false;
            }
            var counter = c.Player.Obj(CK.CombatCounter);
            if (counter != null && instanceId == counter.Obj(CK.Carrier)?.Str(K.InstanceId)) (group.ManualCounterSerials ??= new Dictionary<string, double>(StringComparer.Ordinal))[ownerId] = counter.Num(CK.Serial);
            ReportRestOfRoot(c, group);
            return !RestoreQueue(c);
        }

        // ------------------------------------------------------------------ answering

        /// <summary>finishReactions(ctx): the fight ended; nothing of the reaction state outlives it.</summary>
        public static void Finish(CombatState c)
        {
            foreach (var field in new[] { DK.ReactionCursor, DK.PendingReaction, DK.ReactionResume, CK.ReactionHandCleanup, DK.ReactionStack, DK.ReactionRootPlay }) c.Fields.Remove(field);
            c.ReactionResumeQueue = null;
            c.Fields[DK.PendingExpansionActions] = 0;
            c.Queue.Clear();
        }

        /// <summary>
        /// answerReaction(ctx, ownerId, { offerId, optionId }): the player's choice, validated against a fresh list of options. A card is committed to
        /// the stack (and the seat may be offered again); Back passes the hit on.
        /// </summary>
        public static void Answer(CombatState c, string offerId, string optionId)
        {
            var pending = c.Fields.Obj(DK.PendingReaction);
            var ownerId = V.Player;
            if (pending == null || pending.Str(K.Id) != offerId || pending.Str(DK.OwnerId) != ownerId) throw new InvalidOperationException(RM.ReactionChoiceUnavailable);
            var group = GroupOf(c, pending.Str(RK.GroupKey));
            if (group == null || group.Begun) throw new InvalidOperationException(RM.IncomingResolved);
            var option = optionId == null ? null : Js.Items(pending[RK.Options]).OfType<JObject>().FirstOrDefault(row => row.Str(K.Id) == optionId);
            if (optionId != null && option == null) throw new InvalidOperationException(RM.ChooseAvailable);
            if (option != null)
            {
                var row = PlayerRootSeat(c, group) != null ? null : PlayerRow(c, group);
                var canonical = OptionsFor(c, row, group, null).FirstOrDefault(current => current.Str(K.Id) == optionId);
                if (canonical == null || !JToken.DeepEquals(canonical[RK.Play], option[RK.Play]) || canonical.Str(K.CardId) != option.Str(K.CardId)
                    || canonical.Num(RK.StaminaCost) != option.Num(RK.StaminaCost) || canonical.Num(RK.ManaCost) != option.Num(RK.ManaCost)) throw new InvalidOperationException(RM.NoLongerPlayable);
                option = canonical;
            }
            if (option != null && Stacked(c))
            {
                var play = option.Obj(RK.Play);
                var instance = c.Piles.Hand.FirstOrDefault(inst => inst.Str(K.InstanceId) == play.Str(RK.CardInstanceId));
                var def = CoreCosts.Resolve(c, instance);
                var stack = c.Fields.Obj(DK.ReactionStack);
                if (stack == null) c.Fields[DK.ReactionStack] = stack = new JObject { [RK.GroupKey] = group.Key, [CK.Entries] = new JArray(), [RK.Resolving] = false };
                var entry = new JObject
                {
                    [RK.Side] = V.Player, [DK.OwnerId] = ownerId, [RK.OptionId] = option[K.Id].DeepClone(), [RK.Play] = play.DeepClone(), [K.CardId] = option[K.CardId].DeepClone(),
                    [RK.CardInstanceId] = play[RK.CardInstanceId].DeepClone(), [RK.Maneuver] = TagsOf(def).Contains(CV.TagManeuver + CV.StanceCounter) ? (JToken)CV.StanceCounter : Js.Null(),
                };
                if (option.Has(RK.Incoming)) entry[RK.Incoming] = option[RK.Incoming].DeepClone();
                stack.Arr(CK.Entries).Add(entry);
                if (c.Fields.Obj(CK.ReactionRules)?.Str(DK.ContactPolicy) == DV.PolicyPerHit) group.ReactionAnswered.Add(ownerId);
                c.Fields.Remove(DK.PendingReaction);
                var depth = stack.Arr(CK.Entries).OfType<JObject>().Count(IsPlayerEntry);
                c.Emit(RV.EventCombatReactionChosen, Js.Obj(RK.OfferId, offerId, DK.OwnerId, ownerId, RK.CardInstanceId, play[RK.CardInstanceId]));
                c.Emit(RV.EventCombatReactionStacked, Js.Obj(RK.OfferId, offerId, DK.OwnerId, ownerId, RK.CardInstanceId, play[RK.CardInstanceId], RK.Depth, depth));
                // Enemy reactions: the push opens a playerReaction window, then the seat is re-offered.
                OpenEnemyWindow(c, group, RV.WindowPlayerReaction, entry);
                return;
            }
            if (option != null) throw new NotSupportedException(RM.ReactionRulesVersionOne);
            group.ReactionAnswered.Add(ownerId);
            c.Fields.Remove(DK.PendingReaction);
            c.Emit(RV.EventCombatReactionChosen, Js.Obj(RK.OfferId, offerId, DK.OwnerId, ownerId, RK.CardInstanceId, Js.Null()));
            ReportRestOfRoot(c, group);
        }

        /// <summary>
        /// prepareTacticalCard's reaction Sweep: a Sweep played as a reaction intercepts the break pools of a physical melee attacker, its Poise and Ward
        /// added to the face. The attacker is the card's target, else the incoming attacker of the reaction; its hit is the one held in the interrupted queue,
        /// else the move it intends.
        /// </summary>
        public static List<JObject> SweepReactionEffects(CombatState c, JObject target, JObject carrier, List<JObject> effects)
        {
            if (!c.ReactionPlaying || carrier.Obj(CK.CombatProfile)?.Str(CK.Maneuver) != CV.StanceSweep) return effects;
            var enemy = target?.Str(K.Kind) == V.Enemy ? target : Js.Truthy(c.Fields[Ashen.Generated.Core2StatusKeys.ReactionSourceId]) ? c.Enemies.FirstOrDefault(e => e.Str(K.Id) == c.Fields.Str(Ashen.Generated.Core2StatusKeys.ReactionSourceId)) : null;
            var pending = c.ReactionResumeQueue?.FirstOrDefault(action => action.Meta?.Group?.SourceId == enemy?.Str(K.Id))?.Meta?.Group?.Carrier?.Obj(CK.CombatProfile);
            var incoming = pending ?? enemy?.Obj(K.Intent)?.Obj(CK.CombatProfile);
            var range = Js.Truthy(incoming?[CK.Range]) ? incoming.Str(CK.Range) : incoming?.Str(CK.Maneuver) == CV.StanceRanged || Js.Truthy(incoming?[RK.Projectile]) ? CV.StanceRanged : CV.RangeMelee;
            if (enemy == null || !enemy.Is(K.Alive) || incoming?.Str(CK.Camp) != CV.CampPhysical || range != CV.RangeMelee) return effects;
            var rules = CoreStances.Rules(c);
            var next = new List<JObject>(effects)
            {
                new JObject { [K.Op] = Ashen.Generated.CombatOps.PoiseDamage, [K.Target] = DV.TargetEnemy, [K.Amount] = rules[RK.SweepReactionPoise]?.DeepClone() },
                new JObject { [K.Op] = DV.OpWardDamage, [K.Target] = DV.TargetEnemy, [K.Amount] = rules[RK.SweepReactionWard]?.DeepClone() },
            };
            return next;
        }

        // ------------------------------------------------------------------ deferring and resuming

        /// <summary>
        /// deferRootPlay(ctx): while a player-card root waits on its reaction window, the card that opened it stays unresolved (no cardResolved, no
        /// discard). Only an enemy window (WP-11) has such a root.
        /// </summary>
        public static bool DeferRootPlay(CombatState c)
        {
            var pending = c.Fields.Obj(DK.PendingReaction);
            if (pending == null || c.PendingAbilityPlay == null) return false;
            var group = GroupOf(c, pending.Str(RK.GroupKey));
            if (PlayerRootSeat(c, group) == null) return false;
            c.Fields[DK.ReactionRootPlay] = c.PendingAbilityPlay;
            c.StashedPlayInstance = c.PendingPlayInstance;
            c.StashedPlayRef = c.PendingPlayRef;
            c.StashedPlayBefore = c.PendingPlayBefore;
            c.PendingAbilityPlay = null;
            c.PendingPlayInstance = null;
            c.PendingPlayRef = null;
            c.PendingPlayBefore = null;
            return true;
        }

        /// <summary>restoreRootPlay(ctx): once the stack has resolved, the deferred card is the resolving play again.</summary>
        public static void RestoreRootPlay(CombatState c)
        {
            var play = c.Fields.Obj(DK.ReactionRootPlay);
            if (play == null || c.PendingAbilityPlay != null) return;
            c.PendingAbilityPlay = Js.Spread(play, new JObject { [RK.DeferredRoot] = true });
            c.PendingPlayInstance = c.StashedPlayInstance;
            c.PendingPlayRef = c.StashedPlayRef;
            c.PendingPlayBefore = c.StashedPlayBefore;
            c.StashedPlayInstance = null;
            c.StashedPlayRef = null;
            c.StashedPlayBefore = null;
            c.Fields.Remove(DK.ReactionRootPlay);
        }

        /// <summary>restoreReactionQueue(ctx): the queue a reaction card interrupted is handed back once the card has nothing left to choose. True when it was.</summary>
        public static bool RestoreReactionQueue(CombatState c) => RestoreQueue(c);

        private static bool RestoreQueue(CombatState c)
        {
            if (c.Fields[DK.ReactionResume] == null || CoreCardZones.HasChoice(c) || c.PendingAbilityPlay != null) return false;
            foreach (var action in c.ReactionResumeQueue ?? new List<CombatAction>()) c.Queue.AddLast(action);
            c.ReactionResumeQueue = null;
            c.Fields.Remove(DK.ReactionResume);
            return true;
        }

        // ------------------------------------------------------------------ the enemy's start

        /// <summary>startQueuedEnemy(ctx, action): the first action of an enemy's move announces it (a later hit of the same move announces the hit).</summary>
        public static void StartQueuedEnemy(CombatState c, CombatAction action)
        {
            var group = action.Meta?.Group;
            if (group == null) return;
            if (group.ContactSequence != null && (!action.Source.Is(K.Alive) || HaltReason(c, action.Source) != null)) group.Cancelled = true;
            if (group.EnemyMoveStart == null && group.CounterReturn == null) return;
            if (group.Cancelled || group.PayloadStarted) return;
            group.PayloadStarted = true;
            var enemy = action.Source;
            group.Cancelled = !enemy.Is(K.Alive) || enemy.Is(K.SkipNextTurn) || CoreControl.Locked(c, enemy);
            if (group.Cancelled) return;
            if (group.ContactSequence != null && !ContactTargetPresent(c, group)) return;
            var prepared = group.ContactTelegraphed ? new JObject { [DK.ContactPrepared] = true, [DK.ContactKey] = group.Key } : new JObject();
            // A Counter's reply is announced as the Counter triggering, once, where a move announces itself.
            if (group.CounterReturn != null) { c.Emit(Ashen.Generated.Core2MatchupValues.EventCombatCounterTriggered, Js.Spread(group.CounterReturn, prepared)); return; }
            if (group.ContactSequence != null && group.ContactSequence.Index > 0)
            {
                var hit = Js.Spread(group.EnemyMoveStart, prepared);
                hit[DK.ContactIndex] = group.ContactSequence.Index;
                hit[DK.ContactCount] = group.ContactSequence.Contacts.Count;
                c.Emit(DK.CombatHitStarted, hit);
                return;
            }
            var performed = enemy.Arr(DK.PerformedMoves);
            if (performed == null) enemy[DK.PerformedMoves] = performed = new JArray();
            performed.Add(group.EnemyMoveStart[K.MoveId].DeepClone());
            c.Emit(E.EnemyMoveStarted, Js.Spread(group.EnemyMoveStart, prepared));
        }
    }
}
