using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using PC = Ashen.Generated.Core2ProfileContentKeys;
using V = Ashen.Generated.CombatValues;
using RngStream = Ashen.Generated.RngStream;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The profile's cards as a fight reads them (web model/gameProfile.js projectGameCard): the card row of the pinned profile, with the
    /// authored variant for the resources the profile includes laid over it. Cards are the profile's own rows, not the registry's.
    /// </summary>
    public static class CoreCards
    {
        /// <summary>projectGameCard(ctx, instance): the card a fight plays for an instance (null when the profile has no cards module).</summary>
        public static JObject Project(CombatState c, JObject instance)
        {
            var cardsModule = c.CoreData.Module(CK.CardsModule);
            if (cardsModule == null || cardsModule.Str(K.Mode) == CV.ModeLegacy) return null;
            var id = Js.Truthy(instance[K.CardId]) ? instance.Str(K.CardId) : instance.Str(K.Id);
            var def = Js.Items(c.CoreData.Content(PC.Cards)).OfType<JObject>().FirstOrDefault(card => card.Str(K.Id) == id);
            if (def == null) return null;
            var enabled = Js.Items(c.CoreData.Module(CK.ResourcesModule)?[CK.Include]).Select(Js.Str).ToList();
            var variants = (c.CoreData.Content(PC.ResourceVariants) as JObject)?.Obj(id);
            var key = string.Join(CV.ListJoin, new[] { CV.PoolSpShort, CV.PoolMpShort }.Where(pool => enabled.Contains(pool)));
            var alternate = variants?.Obj(key.Length > 0 ? key : CV.PoolNone);
            if ((!enabled.Contains(CV.PoolSpShort) && Js.D(def[CK.StaminaCost]) > 0) || (!enabled.Contains(CV.PoolMpShort) && Js.D(def[CK.ManaCost]) > 0))
                if (alternate == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, CM.MissingResourceVariant, id));
            var chosen = alternate != null ? Js.Spread(def, alternate) : (JObject)def.DeepClone();
            chosen[CK.KindIds] = new JArray(CV.KindClassificationPrefix + chosen.Str(K.Type));
            chosen[CK.ResourceCosts] = new JObject { [CV.PoolSpShort] = Js.Or0(chosen[CK.StaminaCost]), [CV.PoolMpShort] = Js.Or0(chosen[CK.ManaCost]) };
            return chosen;
        }

        /// <summary>The keywords a card carries in this fight (Innate, Ethereal, Retain, Unplayable ...).</summary>
        public static List<string> Keywords(CombatState c, JObject instance)
        {
            var def = Project(c, instance);
            return Js.Items(def?[CK.Keywords]).Select(Js.Str).ToList();
        }
    }

    /// <summary>
    /// A Core 2 hand (web model/gameProfile and engine/gameProfileCombat.js profileHandSize and refreshProfileRound, engine/handRules.js
    /// turnDrawCount, endTurnCardFate, returnUnplayedCards, discardChoicePlan; engine/combat.js finishPlayerHand): the hand is 4 +
    /// Intelligence cards each turn, nothing is kept (Ethereal cards exhaust, the rest return), and the whole deck is shuffled at the end
    /// of every round on the <c>shuffle</c> stream. Drawing from an empty pile reshuffles the discard, as in the 405 hand.
    /// </summary>
    public static class CoreHand
    {
        /// <summary>profileHandSize(ctx): the cards a hand holds and a turn draws: the profile's base plus the hand attribute.</summary>
        public static double HandSize(CombatState c)
        {
            var cards = c.CoreData.Configured(CK.CardsModule);
            if (cards == null) return 0;
            var baseCards = Js.Nullish(cards[CK.HandBase]) ? c.CoreData.Section(CK.Hand).Num(CK.DefaultBase) : cards.Num(CK.HandBase);
            var attribute = Js.Truthy(cards[CK.HandAttribute]) ? cards.Str(CK.HandAttribute) : c.CoreData.Section(CK.Hand).Str(CK.DefaultAttribute);
            return Math.Max(0, Math.Floor(baseCards + Js.Or0(c.Attributes?[attribute])));
        }

        /// <summary>turnDrawCount(ctx): a configured turn draws up to the full hand.</summary>
        public static double TurnDrawCount(CombatState c)
        {
            c.HandMax = HandSize(c);
            return c.HandMax;
        }

        /// <summary>drawCards(ctx, n): draw to the hand size, reshuffling the discard into an empty draw pile.</summary>
        public static void Draw(CombatState c, double n) => Actions.DrawCards(c, n);

        /// <summary>endTurnCardFate(ctx, card): a configured hand keeps nothing; Ethereal exhausts, the rest is discarded.</summary>
        public static string EndTurnFate(CombatState c, JObject card)
        {
            if (Js.Includes(c.Player[CK.CombatRetainedCards], card.Str(K.InstanceId))) return V.Keep;
            var keywords = CoreCards.Keywords(c, card);
            return keywords.Contains(CV.KeywordEthereal) ? V.Exhaust : V.Discard;
        }

        /// <summary>discardChoicePlan(ctx): a configured hand has no discard choice.</summary>
        public static DiscardPlan Plan(CombatState c) => new DiscardPlan();

        /// <summary>validateDiscardChoice(ctx, ids): nothing may be chosen to discard.</summary>
        public static void ValidateDiscardChoice(CombatState c, IReadOnlyList<string> ids)
        {
            var plan = Plan(c);
            var distinct = new HashSet<string>(ids, StringComparer.Ordinal).Count == ids.Count;
            if (!distinct || ids.Any(id => !plan.Cards.Any(card => card.Str(K.InstanceId) == id)) || ids.Count > plan.Maximum || ids.Count < plan.Minimum)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Ashen.Generated.CombatMessages.DiscardSelection, plan.Minimum, plan.Maximum));
        }

        /// <summary>applyDiscardChoice(ctx, ids): a chosen discard moves from the hand; the replacement draw is owed when the rules say.</summary>
        public static void ApplyDiscardChoice(CombatState c, IReadOnlyList<string> ids)
        {
            double discarded = 0;
            foreach (var id in ids)
            {
                var index = c.Piles.Hand.FindIndex(card => card.Str(K.InstanceId) == id);
                if (index < 0) continue;
                var card = c.Piles.Hand[index];
                if (EndTurnFate(c, card) != V.Keep) continue;
                c.Piles.Hand.RemoveAt(index);
                c.Piles.Discard.Add(card);
                c.Emit(E.CardDiscarded, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, V.ReasonChoice));
                discarded++;
            }
            c.PendingDiscardDraw = c.HandRules != null && c.HandRules.Is(K.ReplaceDiscards) ? discarded : 0;
        }

        /// <summary>refreshProfileRound(ctx, returned): with roundShuffle entireDeck, the returned cards and the discard join the draw pile and the whole pile is shuffled.</summary>
        public static bool RefreshRound(CombatState c, List<JObject> returned)
        {
            var cards = c.CoreData.Configured(CK.CardsModule);
            if (cards == null || cards.Str(CK.RoundShuffle) != CV.ShuffleEntireDeck) return false;
            c.Piles.Draw.AddRange(returned);
            c.Piles.Draw.AddRange(c.Piles.Discard);
            c.Piles.Discard = new List<JObject>();
            c.Piles.Draw = c.Rng.Shuffle(RngStream.Shuffle, c.Piles.Draw);
            c.Emit(E.DeckShuffled, Js.Obj(K.Size, c.Piles.Draw.Count, K.Reason, CV.ReasonRoundRefresh));
            return true;
        }

        /// <summary>returnUnplayedCards(ctx, cards): ordinary unplayed cards return to the deck at round end.</summary>
        public static void ReturnUnplayed(CombatState c, List<JObject> cards)
        {
            if (RefreshRound(c, cards)) return;
            throw new NotSupportedException(CM.HandRefreshNotPorted);
        }

        /// <summary>finishPlayerHand(combat, preserveLockedHand): the hand is dealt with at the end of the round and the turn's Stamina is spent.</summary>
        public static void FinishPlayerHand(CombatState c, bool preserveLockedHand)
        {
            var keep = new List<JObject>();
            var toDiscard = new List<JObject>();
            var toExhaust = new List<JObject>();
            foreach (var card in c.Piles.Hand)
            {
                if (preserveLockedHand) { keep.Add(card); continue; }
                var fate = EndTurnFate(c, card);
                if (fate == V.Keep) keep.Add(card);
                else if (fate == V.Exhaust) toExhaust.Add(card);
                else toDiscard.Add(card);
            }
            c.Piles.Hand = keep;
            foreach (var card in toExhaust)
            {
                c.Piles.Exhaust.Add(card);
                c.Emit(E.CardExhausted, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, V.ReasonEthereal));
            }
            ReturnUnplayed(c, toDiscard);
            c.Player.Put(K.Stamina, 0);
            CoreActions.Drain(c);
        }
    }
}
