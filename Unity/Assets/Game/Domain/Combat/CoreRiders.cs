using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;
using SV = Ashen.Generated.Core2StatusValues;
using SK = Ashen.Generated.Core2StatusKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The ability riders of a Core 2 fight (web engine/abilityRiders.js): the per-turn books an entity keeps (cards played, tags played, Mana spent,
    /// Health lost, discards, offerings), the card charges one card grants the next (damage, Block, heal, a Mana discount, a Poise break or a status
    /// buildup, for a card type, tag or ability kind), the once-per-turn marks of an effect, and the snapshot taken before a play that the rider
    /// predicates read. Charges and riders are data: no card or status is named here.
    /// </summary>
    public static class CoreRiders
    {
        private static readonly string[] ChargeFields = { K.Damage, SV.ManaDiscount, K.Block, Op.Heal, SV.Break };

        /// <summary>abilitySnapshot(entity): the books as they are now.</summary>
        public static JObject Snapshot(JObject entity) => (JObject)CoreEvents.RidersOf(entity).DeepClone();

        /// <summary>abilityMetric(entity, predicate, snapshot): one of the books' numbers (the snapshot's when given).</summary>
        public static double Metric(JObject entity, JObject predicate, JObject snapshot)
        {
            var state = snapshot ?? entity?.Obj(CK.AbilityRiders) ?? new JObject();
            var metric = predicate.Str(SK.Metric);
            if (metric == DK.TagPlays) return state.Obj(DK.TagPlays)?.Or0(predicate.Str(K.Tag) ?? string.Empty) ?? 0;
            if (metric == DK.DistinctTagPlays) return state.Obj(DK.DistinctTagPlays)?.Obj(predicate.Str(K.Tag) ?? string.Empty)?.Properties().Count() ?? 0;
            if (metric == CK.SameCardPlays) return state.Obj(CK.SameCardPlays)?.Or0(predicate.Str(K.CardId) ?? string.Empty) ?? 0;
            return state.Or0(metric ?? string.Empty);
        }

        /// <summary>allowAbilityOnce(entity, marker, card, effectIndex): true the first time an effect of a family runs this turn.</summary>
        public static bool AllowOnce(JObject entity, JToken marker, JObject card, JToken effectIndex)
        {
            var state = CoreEvents.RidersOf(entity);
            var key = OnceKey(marker, card, effectIndex);
            var once = state.Obj(DK.Once) ?? (JObject)(state[DK.Once] = new JObject());
            if (once.Is(key)) return false;
            once[key] = true;
            return true;
        }

        /// <summary>The key of a once-per-turn effect: the card's family (or card, or <c>effect</c>) and the marker the effect names (else its index in the card).</summary>
        public static string OnceKey(JToken marker, JObject card, JToken effectIndex)
        {
            var family = Js.Truthy(card?[Ashen.Generated.Core2CostKeys.AbilityFamily]) ? card.Str(Ashen.Generated.Core2CostKeys.AbilityFamily) : Js.Truthy(card?[K.CardId]) ? card.Str(K.CardId) : V.CauseEffect;
            return family + V.KeySeparator + (Js.IsStr(marker) ? Js.Str(marker) : effectIndex != null && effectIndex.Type != JTokenType.Null ? Js.IsStr(effectIndex) ? Js.Str(effectIndex) : Js.D(effectIndex).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
        }

        /// <summary>Whether the effect's once-per-turn key is still free on the entity (nothing is marked).</summary>
        public static bool IsFree(JObject entity, JToken marker, JObject card, JToken effectIndex) =>
            !CoreEvents.RidersOf(entity).Obj(DK.Once).Is(OnceKey(marker, card, effectIndex));

        private static bool Matches(JObject charge, JObject card)
        {
            var tags = !Js.Nullish(card[DK.AuthoredTags]) && Js.Truthy(card[DK.AuthoredTags]) ? card[DK.AuthoredTags] : card[K.Tags];
            return (!charge.Is(K.CardType) || charge.Str(K.CardType) == card.Str(K.Type))
                && (!charge.Is(SK.CardTag) || Js.Includes(tags, charge.Str(SK.CardTag)))
                && (!charge.Is(CK.AbilityKind) || charge.Str(CK.AbilityKind) == card.Str(DK.AbilityKind));
        }

        /// <summary>matchingAbilityCharges(entity, card, { enemyAvailable }): the charges the entity holds for this card and what they add.</summary>
        public static JObject Matching(JObject entity, JObject card, bool enemyAvailable = true)
        {
            var charges = entity?.Obj(CK.AbilityRiders)?.Obj(DK.Charges) ?? new JObject();
            var entries = charges.Properties().Where(p => p.Value is JObject charge && Matches(charge, card)
                && (enemyAvailable || !charge.Is(Ashen.Generated.Core2CombatValues.OpBuildup) || ChargeFields.Any(field => charge.Num(field) > 0))).Select(p => new KeyValuePair<string, JObject>(p.Name, (JObject)p.Value)).ToList();
            var result = new JObject { [SK.Keys] = new JArray(entries.Select(e => (JToken)e.Key)) };
            foreach (var field in ChargeFields)
                result[field] = Js.N(entries.Sum(e => field == K.Damage && e.Value.Str(SK.DamageScope) == V.CauseEffect ? 0 : e.Value.Or0(field)));
            result[SK.DamageEffect] = Js.N(entries.Sum(e => e.Value.Str(SK.DamageScope) == V.CauseEffect ? e.Value.Or0(K.Damage) : 0));
            result[SK.BuildupBonuses] = new JArray(entries.Where(e => e.Value.Is(Ashen.Generated.Core2CombatValues.OpBuildup)).Select(e => (JToken)Js.Obj(K.Status, e.Value[SK.BuildupStatus], K.Amount, e.Value[Ashen.Generated.Core2CombatValues.OpBuildup])));
            return result;
        }

        /// <summary>cardTargetsAllEnemies(def): the card has a damage effect aimed at every enemy.</summary>
        public static bool TargetsAllEnemies(JObject def) => Js.Items(def[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Op) == Op.Damage && e.Str(K.Target) == K.AllEnemies);

        /// <summary>abilityChargedEffects(effects, charges, enemyAvailable, ctx): a buildup charge adds the application it augments when the card has none.</summary>
        public static List<JObject> ChargedEffects(CombatState c, List<JObject> effects, JObject charges, bool enemyAvailable)
        {
            if (!enemyAvailable) return effects;
            var expanded = Js.D(c.Fields[CK.CombatExpansionVersion]) == c.CoreData.ExpandedVersion;
            string StatusId(string id) => expanded && id == SV.Insanity ? SV.Dazed : id;
            var missing = Js.Items(charges[SK.BuildupBonuses]).OfType<JObject>().Select(b => StatusId(b.Str(K.Status))).Distinct()
                .Where(status => !effects.Any(e => (e.Str(K.Op) == Op.ApplyStatus || (expanded && e.Str(K.Op) == Ashen.Generated.Core2CombatValues.OpBuildup)) && e.Str(K.Status) == status)).ToList();
            if (missing.Count == 0) return effects;
            var primary = effects.FindIndex(e => new[] { Op.Damage, Op.Block, Op.Heal }.Concat(expanded ? new[] { Ashen.Generated.Core2CombatValues.OpGainBarrier } : new string[0]).Contains(e.Str(K.Op)));
            var index = primary < 0 ? effects.Count : primary + 1;
            var added = missing.Select(status => expanded
                ? Js.Obj(K.Op, Ashen.Generated.Core2CombatValues.OpBuildup, K.Target, V.RefEnemy, K.Status, status, K.Amount, 0)
                : Js.Obj(K.Op, Op.ApplyStatus, K.Target, V.RefEnemy, K.Status, status, K.Stacks, 0));
            var result = new List<JObject>(effects.Take(index));
            result.AddRange(added);
            result.AddRange(effects.Skip(index));
            return result;
        }

        /// <summary>consumeAbilityCharges(entity, keys).</summary>
        public static void Consume(JObject entity, IEnumerable<string> keys)
        {
            var charges = CoreEvents.RidersOf(entity).Obj(DK.Charges);
            foreach (var key in keys) charges.Remove(key);
        }

        /// <summary>grantAbilityCharge(entity, effect): a repeated source replaces its own charge, never accumulates it.</summary>
        public static void Grant(JObject entity, JObject effect)
        {
            var charges = CoreEvents.RidersOf(entity).Obj(DK.Charges);
            var key = effect.Str(Ashen.Generated.Core2DeckKeys.Key);
            var old = charges.Obj(key);
            var charge = new JObject();
            foreach (var field in new[] { K.CardType, SK.CardTag, CK.AbilityKind }) if (effect.Is(field)) charge[field] = effect[field].DeepClone();
            charge[K.Damage] = Js.N(Math.Max(old?.Or0(K.Damage) ?? 0, effect.Or0(K.Damage)));
            charge[SV.ManaDiscount] = Js.N(Math.Max(old?.Or0(SV.ManaDiscount) ?? 0, effect.Or0(SV.ManaDiscount)));
            charges[key] = charge;
            foreach (var field in new[] { K.Block, Op.Heal, SV.Break, Ashen.Generated.Core2CombatValues.OpBuildup })
                if (effect.Is(field)) charge[field] = Js.N(Math.Max(old?.Or0(field) ?? 0, effect.Num(field)));
            if (effect.Is(SK.BuildupStatus)) charge[SK.BuildupStatus] = effect[SK.BuildupStatus].DeepClone();
            if (effect.Is(SK.DamageScope)) charge[SK.DamageScope] = effect[SK.DamageScope].DeepClone();
        }

        /// <summary>attachAbilityCharges(action, charges, applied): the first effect that can use a charged amount carries it as a rider.</summary>
        public static void Attach(CombatState c, CombatAction action, JObject charges, HashSet<string> applied)
        {
            var expanded = Js.D(c.Fields[CK.CombatExpansionVersion]) == c.CoreData.ExpandedVersion;
            var op = action.Effect.Str(K.Op);
            action.Meta ??= new ActionMeta();
            var meta = action.Meta = action.Meta.Clone();
            meta.Charge = meta.Charge != null ? (JObject)meta.Charge.DeepClone() : new JObject();
            if (op == Op.Damage && charges.Is(SV.Break) && !applied.Contains(SV.Break)) { applied.Add(SV.Break); meta.Charge[SV.Break] = charges[SV.Break].DeepClone(); }
            if (op == Op.Damage && charges.Is(SK.DamageEffect) && !applied.Contains(SK.DamageEffect)) { applied.Add(SK.DamageEffect); meta.Charge[SK.DamageEffect] = charges[SK.DamageEffect].DeepClone(); }
            if ((op == Op.ApplyStatus || (expanded && op == Ashen.Generated.Core2CombatValues.OpBuildup)) && !applied.Contains(SV.BuildupPrefix + action.Effect.Str(K.Status)))
            {
                var amount = Js.Items(charges[SK.BuildupBonuses]).OfType<JObject>()
                    .Where(b => (expanded && b.Str(K.Status) == SV.Insanity ? SV.Dazed : b.Str(K.Status)) == action.Effect.Str(K.Status)).Sum(b => b.Num(K.Amount));
                if (amount != 0) { applied.Add(SV.BuildupPrefix + action.Effect.Str(K.Status)); meta.Charge[Ashen.Generated.Core2CombatValues.OpBuildup] = Js.N(amount); }
            }
            string field = op == Op.Damage ? K.Damage : op == Op.Block ? K.Block : op == Op.Heal ? Op.Heal : expanded && op == Ashen.Generated.Core2CombatValues.OpGainBarrier ? K.Block : null;
            if (field == null || !charges.Is(field) || applied.Contains(field)) return;
            applied.Add(field);
            meta.Charge[field] = charges[field].DeepClone();
        }

        /// <summary>recordAbilityOffering(entity, amount).</summary>
        public static void RecordOffering(JObject entity, double amount)
        {
            if (!(amount > 0)) return;
            var state = CoreEvents.RidersOf(entity);
            state.Put(DK.OfferingsPaid, state.Or0(DK.OfferingsPaid) + 1);
        }

        /// <summary>
        /// beforeAbilityPlay(ctx, source): the books and the combatants as they were before a play, for the predicates that ask what the play changed.
        /// </summary>
        public static JObject Before(CombatState c, JObject source)
        {
            var entities = new JObject();
            foreach (var entity in new[] { c.Player }.Concat(c.Enemies)) entities[entity.Str(K.Id)] = entity.DeepClone();
            return Js.Obj(SK.AbilityBefore, Snapshot(source), SK.AbilityEntities, entities);
        }

        /// <summary>priorAbilityEntity(ctx, entity, meta): the entity as it was before the play (its statuses before the kill, when the meta names them).</summary>
        public static JObject PriorEntity(CombatState c, JObject entity, JObject before, string targetId, JObject targetStatusesBefore)
        {
            if (entity != null && targetId != null && entity.Str(K.Id) == targetId && targetStatusesBefore != null)
            {
                var copy = Js.Spread(entity);
                copy[K.Statuses] = targetStatusesBefore.DeepClone();
                return copy;
            }
            return before?.Obj(SK.AbilityEntities)?.Obj(entity?.Str(K.Id) ?? string.Empty) ?? entity;
        }
    }
}
