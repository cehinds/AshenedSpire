using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Op = Ashen.Generated.CombatOps;
using P = Ashen.Generated.CardPropertyIds;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>A card's cost profile (framework contract: Cost compilation).</summary>
    public struct CostProfile
    {
        public double Action;
        public double Mana;
        public double Stamina;
        public bool Variable;
    }

    /// <summary>
    /// The framework bridge (shipped framework/bridge.js with lifecycle.js, costs.js and importer.js
    /// cardPropertyInstances): the property view of a resolved card def, and the lifecycle and cost decisions the
    /// engine delegates to it. The legacy-field → property maps are data (rules/combatEngine.json cardProperties).
    /// </summary>
    public static class Framework
    {
        private sealed class CardView
        {
            public readonly List<KeyValuePair<string, JObject>> Properties = new List<KeyValuePair<string, JObject>>();

            public bool Has(string id) => Properties.Any(p => p.Key == id);

            public JObject Params(string id) => Properties.Where(p => p.Key == id).Select(p => p.Value).FirstOrDefault();
        }

        private static readonly ConditionalWeakTable<JObject, CardView> Views = new ConditionalWeakTable<JObject, CardView>();

        private static CardView ViewFor(CombatData data, JObject def) => Views.GetValue(def, d => BuildView(data, d));

        private static string Mapped(JObject table, string key, string what, string cardId)
        {
            var value = table.Str(key ?? string.Empty);
            if (value == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownCardVocabulary, cardId, what, key));
            return value;
        }

        /// <summary>cardPropertyInstances(def): the one mapping from a resolved def's mechanics fields to properties.</summary>
        private static CardView BuildView(CombatData data, JObject card)
        {
            var view = new CardView();
            var id = card.Str(K.Id);
            var kinds = card.Arr(K.KindIds);
            if (kinds == null || kinds.Count != 1) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.CardHasNoKind, id));
            view.Properties.Add(new KeyValuePair<string, JObject>(Js.Str(kinds[0]), new JObject()));
            if (card[K.Cost] != null)
            {
                var isX = card.Str(K.Cost) == V.XCost;
                view.Properties.Add(new KeyValuePair<string, JObject>(P.CostAction,
                    isX ? Js.Obj(K.Amount, 0, K.Variable, true) : Js.Obj(K.Amount, card[K.Cost].DeepClone())));
            }
            if (!Js.Nullish(card[K.ManaCost])) view.Properties.Add(new KeyValuePair<string, JObject>(P.CostMana, Js.Obj(K.Amount, card[K.ManaCost].DeepClone())));
            if (!Js.Nullish(card[K.StaminaCost])) view.Properties.Add(new KeyValuePair<string, JObject>(P.CostStamina, Js.Obj(K.Amount, card[K.StaminaCost].DeepClone())));
            var maps = data.Engine.Obj(K.CardProperties);
            foreach (var keyword in Js.Items(card[K.Keywords]))
                view.Properties.Add(new KeyValuePair<string, JObject>(Mapped(maps.Obj(K.Keyword), Js.Str(keyword), K.Keyword, id), new JObject()));
            if (Js.Truthy(card[K.DamageSchool]))
                view.Properties.Add(new KeyValuePair<string, JObject>(Mapped(maps.Obj(K.DamageSchool), card.Str(K.DamageSchool), K.DamageSchool, id), new JObject()));
            var targets = Js.Items(card[K.Effects]).OfType<JObject>().Select(e => e[K.Target]).Where(Js.Truthy).Select(t => Js.Str(t))
                .Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal);
            foreach (var target in targets)
                view.Properties.Add(new KeyValuePair<string, JObject>(Mapped(maps.Obj(K.Target), target, K.Target, id), new JObject()));
            if (Js.Items(card[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Op) == Op.DodgeRoll))
                view.Properties.Add(new KeyValuePair<string, JObject>(P.UtilityEvasion, new JObject()));
            return view;
        }

        /// <summary>The pure dodge: every effect is the dodge roll (priced by the weight class, not the card).</summary>
        public static bool IsPureDodge(JObject def)
        {
            var effects = Js.Items(def[K.Effects]).OfType<JObject>().ToList();
            return effects.Count > 0 && effects.All(e => e.Str(K.Op) == Op.DodgeRoll);
        }

        public static bool IsInnate(CombatData data, JObject def) => ViewFor(data, def).Has(P.LifecycleInnate);

        public static bool IsUnplayable(CombatData data, JObject def) => ViewFor(data, def).Has(P.InternalUnplayable);

        /// <summary>destinationAfterPlay for a legal, uncancelled play.</summary>
        public static string AfterPlayDestination(CombatData data, JObject def)
        {
            var view = ViewFor(data, def);
            if (view.Has(P.LifecycleExhaust)) return V.ExhaustPile;
            if (view.Has(P.LifecycleRecallAfterUse)) return V.HandZone;
            if (view.Has(P.ClassificationPower)) return V.RemovedFromPlay;
            return V.DiscardPile;
        }

        /// <summary>End-of-turn fate of one card in hand: keep (Retain), exhaust (Ethereal) or discard.</summary>
        public static string EndTurnFate(CombatData data, JObject def)
        {
            var view = ViewFor(data, def);
            if (view.Has(P.LifecycleRetain)) return V.Keep;
            if (view.Has(P.LifecycleEthereal)) return V.Exhaust;
            return V.Discard;
        }

        /// <summary>costProfile(def, { powerCostReduction, weightClass }).</summary>
        public static CostProfile Costs(CombatData data, JObject def, double powerCostReduction, JObject weightClass)
        {
            if (weightClass != null && IsPureDodge(def))
                return new CostProfile { Action = weightClass.Num(K.DodgeActionCost), Mana = 0, Stamina = weightClass.Num(K.DodgeStaminaCost), Variable = false };
            var view = ViewFor(data, def);
            var resources = data.Map(K.CardProperties, K.CostResource);
            var entries = new List<KeyValuePair<string, double>>();
            foreach (var p in view.Properties)
            {
                var resource = resources.Str(p.Key);
                if (resource != null) entries.Add(new KeyValuePair<string, double>(resource, Js.Coalesce(p.Value[K.Amount], 1)));
            }
            if (powerCostReduction != 0 && view.Has(P.ClassificationPower))
            {
                var i = entries.FindIndex(e => e.Key == V.ActionResource);
                if (i >= 0) entries[i] = new KeyValuePair<string, double>(entries[i].Key, Math.Max(0, entries[i].Value - powerCostReduction));
            }
            double Amount(string resource)
            {
                var i = entries.FindIndex(e => e.Key == resource);
                return i >= 0 ? entries[i].Value : 0;
            }
            var action = view.Params(P.CostAction);
            return new CostProfile
            {
                Action = Amount(V.ActionResource),
                Mana = Amount(V.ManaResource),
                Stamina = Amount(V.StaminaResource),
                Variable = action != null && action.Is(K.Variable),
            };
        }
    }
}
