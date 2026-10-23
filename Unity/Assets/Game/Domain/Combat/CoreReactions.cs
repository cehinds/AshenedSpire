using System;
using System.Collections.Generic;
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
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The reaction windows around a committed action (web engine/combatReactions.js beforeReactionAction, reportUnavailable, startQueuedEnemy;
    /// engine/enemyReactions.js fightReacts): before an enemy's hit lands the player may answer with a reaction card. A fight whose player has
    /// the switch off is told so (one receipt per incoming action, only when a card in the hand could have answered), and the hit goes on. An
    /// offer the player could take, or an enemy that reacts to a card, is the reaction work packages' (WP-10, WP-11): it is refused by name.
    /// </summary>
    public static class CoreReactions
    {
        private static bool Positive(JObject contact) => contact != null && (contact.Num(CK.Amount) > 0 || contact.Num(DK.Pressure) > 0);

        private static JObject Entity(CombatState c, string id) => id == null ? null : Triggers.FindEntity(c, id);

        /// <summary>enemyReactionsActive(ctx): version 2 with the flag.</summary>
        private static bool EnemyReactionsActive(CombatState c) => c.CoreData.ReactionVersion == DN.ReactionStackVersion && c.Fields.Obj(CK.ReactionRules)?.Is(DK.EnemyReactions) == true;

        /// <summary>fightReacts(ctx): any enemy of the fight holds a reaction move.</summary>
        public static bool FightReacts(CombatState c) =>
            EnemyReactionsActive(c) && c.Enemies.Any(enemy => (c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Obj(K.Moves)?.Properties().Select(p => p.Value).OfType<JObject>() ?? Enumerable.Empty<JObject>()).Any(move => move.Obj(DK.Reaction)?.Arr(K.On)?.Count > 0));

        /// <summary>haltReason(ctx, source): why an attacker cannot go on with its action, if it cannot.</summary>
        public static string HaltReason(CombatState c, JObject source) =>
            source.Is(K.SkipNextTurn) ? DV.ReasonAttackerStaggered : CoreControl.Locked(c, source) ? DV.ReasonAttackerControlled : null;

        private static bool SeatPresent(CombatState c) => c.Player.Is(K.Alive);

        private static TargetRow PlayerRow(CombatState c, ExpansionGroup group)
        {
            foreach (var pair in group.Targets.Entries()) if (pair.Value.Id == c.Player.Str(K.Id)) return pair.Value;
            return null;
        }

        private static bool ContactTargetPresent(CombatState c, ExpansionGroup group) =>
            group.Targets.Entries().Any(pair => pair.Value.Contacts.Any(Positive) && Entity(c, pair.Value.Id)?.Is(K.Alive) == true);

        /// <summary>The hand cards that could answer an attack by their maneuver (version 2: the Reaction trait).</summary>
        private static List<string> CandidateCardIds(CombatState c)
        {
            var ids = new List<string>();
            foreach (var inst in c.Piles.Hand)
            {
                var def = CoreCosts.Resolve(c, inst);
                var tags = !Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : def[K.Tags];
                if (Js.Includes(tags, DV.TagTraitReaction) && !ids.Contains(inst.Str(K.CardId))) ids.Add(inst.Str(K.CardId));
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

        private static void ReportUnavailable(CombatState c, ExpansionGroup group, string id, TargetRow row, string reason)
        {
            if (group.ReactionNoticed != null && group.ReactionNoticed.Contains(id)) return;
            var cardIds = CandidateCardIds(c);
            if (cardIds.Count == 0) return;
            (group.ReactionNoticed ??= new List<string>()).Add(id);
            c.Emit(DK.CombatReactionUnavailable, Js.Obj(DK.OwnerId, id, K.SourceId, group.SourceId, K.Reason, reason, K.CardId, cardIds[0], DK.CardIds, new JArray(cardIds), DK.AttackKind, AttackKind(row)));
        }

        /// <summary>beforeReactionAction(ctx, action): true while the action must wait for a choice (never in a ported fight).</summary>
        public static bool Before(CombatState c, CombatAction action)
        {
            CoreExpansion.Activate(c, action);
            if (c.CoreData.ReactionVersion < 1) return false;
            var group = action.Meta?.Group;
            if (group == null || group.Carrier.Is(DK.ReactionDepth)) return false;
            if (action.Source?.Str(K.Kind) != V.Enemy)
            {
                if (!group.Begun && action.Source?.Str(K.Kind) == V.Player && FightReacts(c)) throw new NotSupportedException(DM.EnemyReactionsNotPorted);
                return false;
            }
            var player = c.Player.Str(K.Id);
            if (group.Begun)
            {
                if (group.ContactSequence == null && group.ReactionAnswered != null)
                    foreach (var id in group.ReactionAnswered.ToList())
                    {
                        var row = PlayerRow(c, group);
                        if (row == null || (group.ReactionNoticed?.Contains(id) == true) || !SeatPresent(c)) continue;
                        var contacts = action.Meta.ExpansionContacts?.SelectMany(batch => batch) ?? Enumerable.Empty<ContactRef>();
                        if (!contacts.Any(contact => group.Targets.TryGetValue(contact.Key, out var r) && r == row && Positive(row.Contacts[contact.Index]))) continue;
                        ReportUnavailable(c, group, id, row, DV.ReasonAlreadyAnswered);
                    }
                return false;
            }
            if (c.Fields.Obj(CK.ReactionRules)?.Str(DK.ContactPolicy) == DV.PolicyPerHit && !group.ContactTelegraphed && action.Source.Is(K.Alive) && HaltReason(c, action.Source) == null && ContactTargetPresent(c, group))
            {
                group.ContactTelegraphed = true;
                var payload = Js.Spread(group.EnemyMoveStart);
                payload[K.SourceId] = action.Source[K.Id].DeepClone();
                payload[DK.ContactKey] = group.Key;
                payload[K.Kind] = DV.KindAttack;
                payload[DK.ContactIndex] = group.ContactSequence != null ? group.ContactSequence.Index : 0;
                payload[DK.ContactCount] = group.ContactSequence != null ? group.ContactSequence.Contacts.Count : 1;
                c.Emit(DK.CombatContactPrepared, payload);
            }
            if (!action.Source.Is(K.Alive)) return false;
            var halted = HaltReason(c, action.Source);
            var seat = PlayerRow(c, group);
            if (halted != null)
            {
                if (seat != null && seat.Contacts.Any(Positive) && !(group.ReactionAnswered?.Contains(player) == true) && SeatPresent(c)) ReportUnavailable(c, group, player, seat, halted);
                return false;
            }
            group.ReactionAnswered ??= new List<string>();
            if (seat == null || !seat.Contacts.Any(Positive) || group.ReactionAnswered.Contains(player)) return false;
            if (!c.Player.Is(K.Alive) || c.Player[DK.ReactionsEnabled]?.Type == JTokenType.Boolean && !c.Player.Is(DK.ReactionsEnabled))
            {
                if (c.Player[DK.ReactionsEnabled]?.Type == JTokenType.Boolean && !c.Player.Is(DK.ReactionsEnabled) && SeatPresent(c)) ReportUnavailable(c, group, player, seat, DV.ReasonSwitchOff);
                group.ReactionAnswered.Add(player);
                return false;
            }
            // Reactions are on: a card in the hand that could answer would be offered (WP-10); with none, the hit goes on unanswered.
            if (CandidateCardIds(c).Count > 0) throw new NotSupportedException(DM.ReactionsNotPorted);
            group.ReactionAnswered.Add(player);
            return false;
        }

        /// <summary>startQueuedEnemy(ctx, action): the first action of an enemy's move announces it (a later hit of the same move announces the hit).</summary>
        public static void StartQueuedEnemy(CombatState c, CombatAction action)
        {
            var group = action.Meta?.Group;
            if (group == null) return;
            if (group.ContactSequence != null && (!action.Source.Is(K.Alive) || HaltReason(c, action.Source) != null)) group.Cancelled = true;
            if (group.EnemyMoveStart == null) return;
            if (group.Cancelled || group.PayloadStarted) return;
            group.PayloadStarted = true;
            var enemy = action.Source;
            group.Cancelled = !enemy.Is(K.Alive) || enemy.Is(K.SkipNextTurn) || CoreControl.Locked(c, enemy);
            if (group.Cancelled) return;
            if (group.ContactSequence != null && !ContactTargetPresent(c, group)) return;
            var prepared = group.ContactTelegraphed ? new JObject { [DK.ContactPrepared] = true, [DK.ContactKey] = group.Key } : new JObject();
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
