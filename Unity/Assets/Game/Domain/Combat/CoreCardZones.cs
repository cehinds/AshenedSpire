using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using RngStream = Ashen.Generated.RngStream;
using V = Ashen.Generated.CombatValues;
using SK = Ashen.Generated.Core2StatusKeys;
using SM = Ashen.Generated.Core2StatusMessages;
using SV = Ashen.Generated.Core2StatusValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The card-zone effects of a Core 2 fight (web engine/cardZones.js and the card-choice parts of engine/abilityRiders.js): moving cards between
    /// the draw pile, the hand, the discard pile and the exhaust pile, changing a card (Retain, a hidden card, a new maneuver or range), the cards a
    /// player is asked to choose (a pending choice pauses the queue until the intent that answers it) and the cards hidden for a number of turns.
    /// </summary>
    public static class CoreCardZones
    {
        private static readonly string[] Zones = { K.Draw, K.Hand, K.Discard, K.Exhaust };

        private static List<JObject> Pile(CombatState c, string zone)
        {
            switch (zone)
            {
                case K.Draw: return c.Piles.Draw;
                case K.Hand: return c.Piles.Hand;
                case K.Discard: return c.Piles.Discard;
                case K.Exhaust: return c.Piles.Exhaust;
                default: return null;
            }
        }

        private static void SetPile(CombatState c, string zone, List<JObject> pile)
        {
            if (zone == K.Draw) c.Piles.Draw = pile;
        }

        /// <summary>hasCardChoice(ctx): a chosen discard or a chosen card move is waiting for its answer.</summary>
        public static bool HasChoice(CombatState c) => c.Fields[SK.PendingAbilityDiscard] != null || c.Fields[SK.PendingCardChoice] != null;

        /// <summary>The instances a reaction stack has committed for this seat; they wait to resolve and are not offered as card fodder.</summary>
        private static HashSet<string> Stacked(CombatState c)
        {
            var entries = c.Fields.Obj(DK.ReactionStack)?.Arr(CK.Entries);
            if (entries != null && entries.Count > 0) throw new NotSupportedException(Ashen.Generated.Core2DamageMessages.ReactionsNotPorted);
            return new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>zoneCandidates(ctx, effect): the cards of the source zone the effect may look at, the first <c>look</c> of them.</summary>
        public static List<JObject> Candidates(CombatState c, JObject effect)
        {
            var pile = Pile(c, effect.Str(Ashen.Generated.EventKeys.From));
            if (pile == null) throw new InvalidOperationException(SM.UnknownZone);
            var excluded = Stacked(c);
            var cards = pile.Where(card => !excluded.Contains(card.Str(K.InstanceId))).ToList();
            var look = effect[SK.Look] != null && !Js.Nullish(effect[SK.Look]) ? (int)Js.D(effect[SK.Look]) : cards.Count;
            return cards.Take(Math.Max(0, look)).ToList();
        }

        private static bool Safe(JToken t) => Js.IsInt(t) && Math.Abs(Js.D(t)) <= 9007199254740991d;

        /// <summary>validateZoneEffect(effect): the shape of a moveCards or modifyCards effect.</summary>
        public static void Validate(JObject effect)
        {
            var op = effect?.Str(K.Op);
            if (effect == null || (op != SV.MoveCards && op != SV.ModifyCards)) throw new InvalidOperationException(SM.UnknownZoneOp);
            var allowed = new HashSet<string> { K.Op, Ashen.Generated.EventKeys.From, Ashen.Generated.EventKeys.To, K.Amount, DK.Selection, K.Position, SK.Look, SK.TransformTo, Ashen.Generated.ShopKeys.Changes, K.Target, K.If, K.Repeat, CK.OncePerTurn };
            if (effect.Properties().Any(p => !allowed.Contains(p.Name))) throw new InvalidOperationException(SM.UnknownZoneField);
            var amount = effect[K.Amount];
            var bad = Js.IsNum(amount) ? !Js.IsFinite(amount) || Js.D(amount) < 0 || Js.D(amount) > 100
                : !(amount is JObject f) || !Js.IsStr(f[K.F]);
            if (bad) throw new InvalidOperationException(SM.BadZoneCount);
            if (effect[SK.TransformTo] != null && (!Js.IsStr(effect[SK.TransformTo]) || string.IsNullOrEmpty(effect.Str(SK.TransformTo)))) throw new InvalidOperationException(SM.BadTransform);
            if (!Zones.Contains(effect.Str(Ashen.Generated.EventKeys.From)) || (op != SV.ModifyCards && !Zones.Contains(effect.Str(Ashen.Generated.EventKeys.To)))) throw new InvalidOperationException(SM.UnknownZoneName);
            var selection = Js.Truthy(effect[DK.Selection]) ? effect.Str(DK.Selection) : Ashen.Generated.MapKeys.First;
            if (selection != Ashen.Generated.MapKeys.First && selection != K.Random && selection != DK.Choose) throw new InvalidOperationException(SM.UnknownSelection);
            if (op == SV.ModifyCards)
            {
                var changes = effect[Ashen.Generated.ShopKeys.Changes] as JObject;
                if (changes == null) throw new InvalidOperationException(SM.BadChanges);
                var changeKeys = new HashSet<string> { K.Retain, SK.RetainTurns, SK.HideTurns, CK.Maneuver, CK.Range };
                if (changes.Properties().Any(p => !changeKeys.Contains(p.Name))) throw new InvalidOperationException(SM.UnknownChange);
                if (changes[K.Retain] != null && changes[K.Retain].Type != JTokenType.Boolean) throw new InvalidOperationException(SM.BadRetain);
                if (changes[SK.RetainTurns] != null && (!Safe(changes[SK.RetainTurns]) || changes.Num(SK.RetainTurns) < 1 || changes.Num(SK.RetainTurns) > 10)) throw new InvalidOperationException(SM.BadRetainTurns);
                var hide = changes[SK.HideTurns];
                if (hide != null && !(hide is JObject hf && Js.IsStr(hf[K.F])) && (!Safe(hide) || Js.D(hide) < 1 || Js.D(hide) > 10)) throw new InvalidOperationException(SM.BadHideTurns);
                var maneuvers = new[] { K.Attack, Ashen.Generated.Core2CombatValues.StanceSmash, Ashen.Generated.Core2CombatValues.StanceDefend, Ashen.Generated.Core2CombatValues.StanceSweep, CK.Counter, Ashen.Generated.Core2CombatValues.ManeuverCasting, CK.Evade };
                if (Js.Truthy(changes[CK.Maneuver]) && !maneuvers.Contains(changes.Str(CK.Maneuver))) throw new InvalidOperationException(SM.UnknownManeuver);
                var ranges = new[] { Ashen.Generated.Core2CombatValues.RangeMelee, CK.Reach, Ashen.Generated.Core2CombatValues.ManeuverRanged };
                if (Js.Truthy(changes[CK.Range]) && !ranges.Contains(changes.Str(CK.Range))) throw new InvalidOperationException(SM.UnknownRange);
            }
            var position = Js.Truthy(effect[K.Position]) ? effect.Str(K.Position) : V.PositionBottom;
            if (position != V.PositionTop && position != V.PositionBottom && position != K.Random) throw new InvalidOperationException(SM.UnknownPosition);
            if (effect[SK.Look] != null && (!Safe(effect[SK.Look]) || effect.Num(SK.Look) < 1 || effect.Num(SK.Look) > 100)) throw new InvalidOperationException(SM.BadLook);
        }

        private static void ApplyMove(CombatState c, JObject effect, IReadOnlyList<string> ids)
        {
            var from = effect.Str(Ashen.Generated.EventKeys.From);
            if (effect.Str(K.Op) == SV.ModifyCards)
            {
                var changes = effect.Obj(Ashen.Generated.ShopKeys.Changes);
                foreach (var id in ids)
                {
                    var pile = Pile(c, from);
                    var card = pile.First(x => x.Str(K.InstanceId) == id);
                    var merged = Js.Spread(card.Obj(Ashen.Generated.Core2CostKeys.ProposalChanges), changes);
                    card[Ashen.Generated.Core2CostKeys.ProposalChanges] = merged;
                    if (changes.Is(K.Retain)) merged.Put(SK.RetainUntilTurn, c.Turn + (Js.Truthy(changes[SK.RetainTurns]) ? changes.Num(SK.RetainTurns) : 1) - 1);
                    if (changes.Is(SK.HideTurns))
                    {
                        var hidden = c.Fields.Arr(SK.HiddenCards) ?? (JArray)(c.Fields[SK.HiddenCards] = new JArray());
                        hidden.Add(new JObject { [K.Card] = card.DeepClone(), [K.PlayerId] = Js.Null(), [SK.ReturnTurn] = Js.N(c.Turn + changes.Num(SK.HideTurns)) });
                        pile.Remove(card);
                    }
                    c.Emit(SV.CardModified, Js.Obj(K.CardInstanceId, id, K.CardId, card[K.CardId], Ashen.Generated.ShopKeys.Changes, changes.DeepClone()));
                }
                return;
            }
            var source = Pile(c, from);
            var to = effect.Str(Ashen.Generated.EventKeys.To);
            var destination = Pile(c, to);
            var cards = ids.Select(id => source.First(x => x.Str(K.InstanceId) == id)).ToList();
            foreach (var card in cards) source.Remove(card);
            foreach (var card in cards)
                if (Js.Truthy(effect[SK.TransformTo]))
                {
                    c.Data.Cards.Get(effect.Str(SK.TransformTo));
                    card[K.CardId] = effect[SK.TransformTo].DeepClone();
                    card[K.Upgraded] = false;
                }
            var moved = new List<KeyValuePair<JObject, string>>();
            var position = Js.Truthy(effect[K.Position]) ? effect.Str(K.Position) : V.PositionBottom;
            foreach (var card in cards)
            {
                var actualTo = to == K.Hand && destination.Count >= c.HandMax ? K.Discard : to;
                var pile = Pile(c, actualTo);
                if (effect.Str(K.Position) == K.Random) pile.Insert((int)Math.Floor(c.Rng.Float(RngStream.Shuffle) * (pile.Count + 1)), card);
                else if (effect.Str(K.Position) == V.PositionTop)
                {
                    var prior = moved.Count(row => row.Value == actualTo);
                    pile.Insert(Math.Min(prior, pile.Count), card);
                }
                else pile.Add(card);
                moved.Add(new KeyValuePair<JObject, string>(card, actualTo));
            }
            foreach (var row in moved)
                c.Emit(row.Value == K.Exhaust ? E.CardExhausted : row.Value == K.Discard ? E.CardDiscarded : SV.CardMoved,
                    Js.Obj(K.CardInstanceId, row.Key[K.InstanceId], K.CardId, row.Key[K.CardId], Ashen.Generated.EventKeys.From, from, Ashen.Generated.EventKeys.To, row.Value, K.Reason, row.Value != to ? V.ReasonHandFull : Ashen.Generated.Core2DamageValues.ReasonChosen, SK.Explicit, true, K.SourceId, c.Player[K.Id]));
        }

        /// <summary>moveCards(ctx, action, amount): the cards move now, or (selection <c>choose</c>) a pending choice pauses the queue.</summary>
        public static void Move(CombatState c, CombatAction action, double amount)
        {
            var effect = action.Effect;
            Validate(effect);
            if (Js.Truthy(effect[SK.TransformTo])) c.Data.Cards.Get(effect.Str(SK.TransformTo));
            var candidates = Candidates(c, effect);
            var count = (int)Math.Min(candidates.Count, Math.Min(100, Math.Max(0, Math.Floor(amount))));
            if (count == 0) return;
            if (effect.Str(DK.Selection) == DK.Choose)
            {
                if (c.PendingAbilityPlay == null) throw new InvalidOperationException(SM.ChoiceNeedsCard);
                var stored = (JObject)effect.DeepClone();
                stored.Put(K.Amount, count);
                c.Fields[SK.PendingCardChoice] = new JObject
                {
                    [K.Kind] = SV.MoveCards, [K.Count] = count, [V.CauseEffect] = stored, [SK.CandidateIds] = new JArray(candidates.Select(x => x[K.InstanceId].DeepClone())),
                    [Ashen.Generated.ShopKeys.Candidates] = new JArray(candidates.Select(x => x.DeepClone())), [K.PlayerId] = Js.Null(), [K.CardId] = c.PendingAbilityPlay[K.CardId]?.DeepClone(),
                };
                return;
            }
            var ordered = effect.Str(DK.Selection) == K.Random ? c.Rng.Shuffle(RngStream.Shuffle, candidates) : candidates;
            ApplyMove(c, effect, ordered.Take(count).Select(x => x.Str(K.InstanceId)).ToList());
        }

        /// <summary>chooseZoneCards(ctx, ids): the player's answer to a pending card-move choice.</summary>
        public static void Choose(CombatState c, IReadOnlyList<string> ids)
        {
            var pending = c.Fields.Obj(SK.PendingCardChoice);
            if (pending == null) throw new InvalidOperationException(SM.NoZoneChoice);
            var valid = new HashSet<string>(Candidates(c, pending.Obj(V.CauseEffect)).Select(x => x.Str(K.InstanceId)));
            var count = (int)pending.Num(K.Count);
            var candidateIds = Js.Items(pending[SK.CandidateIds]).Select(Js.Str).ToList();
            if (ids == null || ids.Count != count || new HashSet<string>(ids).Count != ids.Count || ids.Any(id => !candidateIds.Contains(id) || !valid.Contains(id)))
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, SM.ChooseExactly, count));
            ApplyMove(c, pending.Obj(V.CauseEffect), ids);
            c.Fields.Remove(SK.PendingCardChoice);
        }

        /// <summary>returnHiddenCards(ctx): the cards hidden for a number of turns come back to the hand (the discard pile when it is full).</summary>
        public static void ReturnHidden(CombatState c)
        {
            var hidden = c.Fields.Arr(SK.HiddenCards);
            if (hidden == null) return;
            var remaining = new JArray();
            foreach (var row in hidden.OfType<JObject>())
            {
                if (row.Num(SK.ReturnTurn) <= c.Turn && Js.Nullish(row[K.PlayerId]))
                {
                    var card = row.Obj(K.Card);
                    (c.Piles.Hand.Count < c.HandMax ? c.Piles.Hand : c.Piles.Discard).Add(card);
                    c.Emit(SV.CardMoved, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], Ashen.Generated.EventKeys.From, Ashen.Generated.EventKeys.Hidden, Ashen.Generated.EventKeys.To, K.Hand, K.Reason, SV.Returned));
                }
                else remaining.Add(row);
            }
            c.Fields[SK.HiddenCards] = remaining;
        }

        // ------------------------------------------------------------------ chosen discards (engine/abilityRiders.js)

        /// <summary>discardCandidates(ctx): the hand cards that may be discarded (those not committed to a reaction stack).</summary>
        public static List<JObject> DiscardCandidates(CombatState c)
        {
            var stacked = Stacked(c);
            return c.Piles.Hand.Where(card => !stacked.Contains(card.Str(K.InstanceId))).ToList();
        }

        /// <summary>requestAbilityDiscard(ctx, action, amount): the player must choose <paramref name="amount"/> cards to discard (fewer when the hand is smaller).</summary>
        public static void RequestDiscard(CombatState c, double amount)
        {
            var count = (int)Math.Min(Math.Max(0, amount), DiscardCandidates(c).Count);
            if (count == 0) return;
            if (c.PendingAbilityPlay == null) throw new InvalidOperationException(SM.DiscardNeedsCard);
            c.Fields[SK.PendingAbilityDiscard] = new JObject { [K.Count] = count, [K.PlayerId] = Js.Null(), [K.CardId] = c.PendingAbilityPlay[K.CardId]?.DeepClone() };
        }

        /// <summary>chooseAbilityDiscard(ctx, ids): the answer to a pending chosen discard.</summary>
        public static void ChooseDiscard(CombatState c, IReadOnlyList<string> ids)
        {
            var pending = c.Fields.Obj(SK.PendingAbilityDiscard);
            if (pending == null) throw new InvalidOperationException(SM.NoDiscardChoice);
            var stacked = Stacked(c);
            var count = (int)pending.Num(K.Count);
            if (ids == null || ids.Count != count || new HashSet<string>(ids).Count != ids.Count || ids.Any(id => stacked.Contains(id) || !c.Piles.Hand.Any(card => card.Str(K.InstanceId) == id)))
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, SM.ChooseExactDiscard, count));
            foreach (var id in ids)
            {
                var index = c.Piles.Hand.FindIndex(card => card.Str(K.InstanceId) == id);
                var card = c.Piles.Hand[index];
                c.Piles.Hand.RemoveAt(index);
                c.Piles.Discard.Add(card);
                c.Emit(E.CardDiscarded, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, Ashen.Generated.Core2DamageValues.ReasonChosen, SK.Explicit, true, K.SourceId, c.Player[K.Id]));
            }
            c.Fields.Remove(SK.PendingAbilityDiscard);
        }
    }
}
