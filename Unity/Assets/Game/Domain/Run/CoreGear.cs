using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2RunKeys;
using K = Ashen.Generated.CombatKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using PC = Ashen.Generated.Core2ProfileContentKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// A Core 2 run's worn gear (shipped model/gameProfileGear.js and engine/kernel/gameConfig.js resolveGearItem): every item
    /// resolves from its authored points plus a share of its rarity's pool by weight (the armor budget, the merged pool), and
    /// the worn items add up to the receipt that feeds the run's pools, effective attributes and ratings. The vocabulary
    /// (rating and attribute ids, the pool statistics) is rules/core2Run.json gear.
    /// </summary>
    public static class CoreGear
    {
        internal static List<string> Ratings(CoreRunData d) => RunJs.Strs(d.RuleSection(CK.Gear)[CK.Ratings]);

        internal static List<string> Attributes(CoreRunData d) => RunJs.Strs(d.RuleSection(CK.Gear)[CK.Attributes]);

        internal static List<string> AllKeys(CoreRunData d) => Ratings(d).Concat(Attributes(d)).ToList();

        private static double Pos(JToken value) => Js.Truthy(value) ? Js.D(value) : 0;

        /// <summary>gearSplit(total, weights): largest remainder over the positive weights, ties by key order.</summary>
        internal static JObject Split(double total, JObject weights)
        {
            var keys = weights.Properties().Where(p => Js.D(p.Value) > 0).Select(p => p.Name).ToList();
            var output = new JObject();
            foreach (var k in keys) output.Put(k, 0);
            var sum = keys.Sum(k => Js.D(weights[k]));
            if (total <= 0 || sum == 0) return output;
            var raw = keys.Select(k => (Key: k, Value: Js.D(weights[k]) / sum * total)).ToList();
            foreach (var r in raw) output.Put(r.Key, Math.Floor(r.Value));
            var left = total - keys.Sum(k => output.Num(k));
            var ordered = new List<(string Key, double Value)>(raw);
            ordered.Sort((a, b) =>
            {
                var first = (b.Value - Math.Floor(b.Value)) - (a.Value - Math.Floor(a.Value));
                if (first != 0 && !double.IsNaN(first)) return Math.Sign(first);
                return keys.IndexOf(a.Key) - keys.IndexOf(b.Key);
            });
            foreach (var r in ordered)
            {
                if (left > 0) output.Put(r.Key, output.Num(r.Key) + 1);
                left--;
            }
            return output;
        }

        /// <summary>gearWeights(item, settings, pool): the item's own weights, else its tags' weights by priority, else kind, slot, global.</summary>
        private static JObject Weights(JObject item, JObject settings, string pool)
        {
            if (item.Obj(CK.Weights)?.Obj(pool) is JObject own && own.Properties().Any()) return (JObject)own.DeepClone();
            var summed = new JObject();
            foreach (var tag in Js.Items(item[K.Tags]))
            {
                var rule = settings.Obj(CK.Tags)?.Obj(Js.Str(tag));
                var w = rule?.Obj(CK.Weights)?.Obj(pool);
                if (w == null) continue;
                foreach (var p in w.Properties()) summed.Put(p.Name, Pos(summed[p.Name]) + Js.D(p.Value) * rule.Num(CK.Priority) / Ashen.Generated.Core2RunNumbers.Percent);
            }
            if (summed.Properties().Any()) return summed;
            var weights = settings.Obj(CK.Weights);
            var found = weights?.Obj(CK.Kinds)?.Obj(item.Str(K.Kind))?.Obj(pool)
                ?? weights?.Obj(CK.Slots)?.Obj(item.Str(CK.Slot))?.Obj(pool)
                ?? weights?.Obj(CK.Global)?.Obj(pool);
            return found != null ? (JObject)found.DeepClone() : new JObject();
        }

        /// <summary>armorBudgetFor(item, settings, rank): baseline plus the per-rank growth plus the rarity bonus.</summary>
        public static double ArmorBudget(JObject item, JObject settings, double rank)
        {
            var b = settings.Obj(CK.ArmorBudget);
            var bonus = b.Obj(CK.RarityBonus);
            var rarity = item.Str(CK.Rarity);
            return b.Num(K.Baseline) + (rank - 1) * (Js.Nullish(b[CK.PerRank]) ? 0 : b.Num(CK.PerRank))
                + (bonus != null && rarity != null && bonus[rarity] != null ? Js.D(bonus[rarity]) : 0);
        }

        private static JObject SizeOf(JObject settings, string pool, JObject item) =>
            settings.Obj(CK.PoolSizes)?.Obj(pool)?.Obj(item.Str(CK.Rarity)) ?? Js.Obj(CK.Min, 0, CK.Max, 0);

        private static double Target(CoreRunData d, JObject settings, string pool, JObject item)
        {
            var fill = settings.Str(CK.FillTo);
            var gear = d.RuleSection(CK.Gear);
            if (fill == gear.Str(CK.FillNone)) return 0;
            var size = SizeOf(settings, pool, item);
            return fill == gear.Str(CK.FillMin) ? size.Num(CK.Min) : size.Num(CK.Max);
        }

        /// <summary>
        /// resolveGearItem(item, settings, { rank }): the item's points (authored plus pool fill) and their values. The numbers
        /// the run reads; the web's display-only fields (tag modifier lines, pool sizes) are not carried.
        /// </summary>
        public static JObject ResolveItem(CoreRunData d, JObject item, JObject settings, double rank)
        {
            if (!(rank >= 1) || Math.Floor(rank) != rank) throw new InvalidOperationException(Ashen.Generated.Core2RunMessages.GearRankInvalid);
            var gear = d.RuleSection(CK.Gear);
            var keys = AllKeys(d);
            var ratings = Ratings(d);
            var attrKeys = Attributes(d);
            var armorBudgetSettings = settings.Obj(CK.ArmorBudget);
            var isArmor = item.Str(K.Kind) == gear.Str(CK.ArmorKind);
            var isShield = item.Str(K.Kind) == gear.Str(CK.ShieldKind);
            double? armorBudget = isArmor && armorBudgetSettings != null ? ArmorBudget(item, settings, rank) : (double?)null;
            var fillNone = settings.Str(CK.FillTo) == gear.Str(CK.FillNone);
            var explicitPoints = new JObject();
            foreach (var k in keys) explicitPoints.Put(k, Pos(item.Obj(CK.Points)?[k]));
            double Sum(IEnumerable<string> among) => among.Sum(k => explicitPoints.Num(k));
            var tagCost = Js.Items(item[K.Tags]).Sum(t => Pos(settings.Obj(CK.Tags)?.Obj(Js.Str(t))?[CK.Cost]));
            var tierPoints = (rank - 1) * (Js.Nullish(armorBudgetSettings?[CK.PerRank]) ? 0 : armorBudgetSettings.Num(CK.PerRank));
            if ((isArmor || isShield) && armorBudgetSettings?.Str(CK.PerRankStat) == gear.Str(CK.RankStat) && tierPoints > 0)
            {
                var merged0 = settings.Obj(CK.PoolSizes)?.Obj(gear.Str(CK.PoolsMerged))?.Obj(item.Str(CK.Rarity));
                var whole0 = armorBudget ?? tierPoints + (merged0 != null
                    ? (fillNone ? 0 : settings.Str(CK.FillTo) == gear.Str(CK.FillMin) ? merged0.Num(CK.Min) : merged0.Num(CK.Max))
                    : Target(d, settings, CK.Ratings, item) + Target(d, settings, CK.Bonus, item));
                explicitPoints.Put(gear.Str(CK.RankStat), explicitPoints.Num(gear.Str(CK.RankStat))
                    + Math.Max(0, Math.Min(tierPoints, (fillNone ? 0 : whole0) - Sum(keys) - tagCost)));
            }
            var ratingsW = Weights(item, settings, CK.Ratings);
            JObject LimitAttributes(JObject weights)
            {
                var chosen = attrKeys.Where(k => explicitPoints.Num(k) > 0).ToList();
                var candidates = attrKeys.Where(k => !chosen.Contains(k) && Js.D(weights[k]) > 0).ToList();
                var order = candidates.Select((k, i) => (Key: k, Index: i)).OrderByDescending(x => Js.D(weights[x.Key])).ThenBy(x => x.Index).Select(x => x.Key);
                foreach (var k in order) if (chosen.Count < Js.D(settings[CK.MaxAttributes])) chosen.Add(k);
                var result = new JObject();
                foreach (var p in weights.Properties()) if (ratings.Contains(p.Name) || chosen.Contains(p.Name)) result[p.Name] = p.Value.DeepClone();
                return result;
            }
            var bonusW = LimitAttributes(Weights(item, settings, CK.Bonus));
            JObject fill;
            if (settings.Str(CK.Pools) == gear.Str(CK.PoolsMerged) || armorBudget != null)
            {
                JObject Norm(JObject w, double share)
                {
                    var total = w.Properties().Sum(p => Js.D(p.Value));
                    var s = total == 0 ? 1 : total;
                    var o = new JObject();
                    foreach (var p in w.Properties()) o.Put(p.Name, Js.D(p.Value) / s * share);
                    return o;
                }
                var merged = settings.Obj(CK.PoolSizes)?.Obj(gear.Str(CK.PoolsMerged))?.Obj(item.Str(CK.Rarity));
                var shieldTierPoints = isShield && armorBudgetSettings != null ? (rank - 1) * (Js.Nullish(armorBudgetSettings[CK.PerRank]) ? 0 : armorBudgetSettings.Num(CK.PerRank)) : 0;
                var whole = armorBudget ?? shieldTierPoints + (merged != null
                    ? (fillNone ? 0 : settings.Str(CK.FillTo) == gear.Str(CK.FillMin) ? merged.Num(CK.Min) : merged.Num(CK.Max))
                    : Target(d, settings, CK.Ratings, item) + Target(d, settings, CK.Bonus, item));
                var remaining = (fillNone ? 0 : whole) - Sum(keys) - tagCost;
                var shares = settings.Obj(CK.MergedShare);
                var share = Js.Truthy(shares?[item.Str(CK.Rarity) ?? V.Undefined]) ? shares.Obj(item.Str(CK.Rarity)) : shares.Obj(CK.Default);
                fill = Split(remaining, Js.Spread(Norm(ratingsW, share.Num(CK.Ratings)), Norm(bonusW, share.Num(CK.Bonus))));
            }
            else
            {
                fill = Js.Spread(Split(Target(d, settings, CK.Ratings, item) - Sum(ratings), ratingsW),
                    Split(Target(d, settings, CK.Bonus, item) - Sum(attrKeys) - tagCost, bonusW));
            }
            var points = new JObject();
            var values = new JObject();
            foreach (var k in keys) points.Put(k, explicitPoints.Num(k) + Pos(fill[k]));
            foreach (var k in keys) values.Put(k, points.Num(k) * (Js.Nullish(settings.Obj(CK.PointValues)?[k]) ? 0 : settings.Obj(CK.PointValues).Num(k)));
            return new JObject
            {
                [K.Id] = item[K.Id]?.DeepClone(),
                [CK.Rank] = Js.N(rank),
                [CK.Budget] = armorBudget.HasValue ? Js.N(armorBudget.Value) : Js.Null(),
                [CK.Points] = points,
                [CK.Values] = values,
                [K.Tags] = new JArray(Js.Items(item[K.Tags]).Select(t => t.DeepClone())),
                [Ashen.Generated.Core2CombatKeys.Modifiers] = new JArray(Js.Items(item[K.Tags]).Select(t => settings.Obj(CK.Tags)?.Obj(Js.Str(t)))
                    .Where(row => row != null && Js.Truthy(row[Ashen.Generated.Core2CombatKeys.Modifier]))
                    .Select(row => Js.S(row.Str(Ashen.Generated.Core2CombatKeys.Modifier) + Ashen.Generated.Core2CombatValues.ModifierEquals
                        + (row.Num(Ashen.Generated.Core2CombatKeys.Amount) >= 0 ? Ashen.Generated.Core2CombatValues.ModifierPlus : string.Empty)
                        + RunJs.Show(row[Ashen.Generated.Core2CombatKeys.Amount])))),
                [CK.TagCost] = Js.N(tagCost),
                [CK.Pools] = new JObject
                {
                    [CK.Ratings] = SizeOf(settings, CK.Ratings, item),
                    [CK.Bonus] = SizeOf(settings, CK.Bonus, item),
                    [Ashen.Generated.Core2CombatKeys.Merged] = settings.Obj(CK.PoolSizes)?.Obj(gear.Str(CK.PoolsMerged))?.Obj(item.Str(CK.Rarity))?.DeepClone() ?? Js.Null(),
                    [CK.Mode] = settings[CK.Pools]?.DeepClone() ?? Js.Null(),
                },
            };
        }

        /// <summary>
        /// gameProfileEquipmentReceipt(registries, run): the worn items resolved and summed, { totals, sources }. Empty unless the
        /// profile's gear module is configured with an armor budget.
        /// </summary>
        public static JObject EquipmentReceipt(CoreRunData d, JObject run)
        {
            var subject = d.SubjectOf(run);
            var settings = CoreRunData.Module(subject, MI.Gear);
            var keys = AllKeys(d);
            var totals = new JObject();
            foreach (var k in keys) totals.Put(k, 0);
            var sources = new JArray();
            var loadout = run.Obj(K.Loadout);
            if (settings?.Str(CK.Mode) != Ashen.Generated.Core2RunValues.Configured || settings.Obj(CK.ArmorBudget) == null || loadout == null)
                return Js.Obj(CK.Totals, totals, CK.Sources, sources);
            var catalog = (CoreRunData.Content(subject, PC.Gear) as JArray) ?? new JArray();
            var gear = d.RuleSection(CK.Gear);
            foreach (var slot in (loadout.Obj(K.Sets) ?? new JObject()).Properties())
            {
                var sets = slot.Value as JArray;
                var activeIndex = loadout.Obj(K.Active)?[slot.Name];
                var index = Js.Truthy(activeIndex) ? (int)Js.D(activeIndex) : 0;
                var id = sets != null && index >= 0 && index < sets.Count ? sets[index] : null;
                if (!Js.Truthy(id)) continue;
                var armor = slot.Name == RK.Armor;
                var item = catalog.OfType<JObject>().FirstOrDefault(row =>
                    (Js.Truthy(row.Obj(CK.Source)?[K.Id]) ? row.Obj(CK.Source).Str(K.Id) : row.Str(K.Id)) == Js.Str(id)
                    && (row.Str(K.Kind) == gear.Str(CK.ArmorKind)) == armor
                    && (!Js.Truthy(row[CK.Classes]) || Js.Includes(row[CK.Classes], run.Str(RK.Class))));
                if (item == null) continue;
                var sourceId = armor
                    ? string.Join(V.ItemRefSeparator, V.ArmorRefPrefix, run.Str(RK.Class), Js.Str(id))
                    : string.Join(V.ItemRefSeparator, V.ArmamentRefPrefix, Js.Str(id));
                var tier = Pos(run.Obj(K.ItemUpgradeLevels)?[sourceId]);
                var resolved = ResolveItem(d, item, settings, (Js.Nullish(item[CK.Rank]) ? 1 : item.Num(CK.Rank)) + tier);
                var itemValues = resolved.Obj(CK.Values);
                foreach (var k in keys) totals.Put(k, totals.Num(k) + Pos(itemValues[k]));
                var source = new JObject
                {
                    [K.Name] = Js.S(item.Str(K.Name).Replace(Ashen.Generated.Core2RunValues.NameSpaceMark, Ashen.Generated.Core2RunValues.NameSpace)),
                    [K.Kind] = Js.S(Ashen.Generated.Core2RunValues.EquipmentKind),
                    [CK.SourceId] = Js.S(sourceId),
                    [CK.Slot] = Js.S(slot.Name),
                    [CK.ItemId] = item[K.Id].DeepClone(),
                };
                foreach (var p in itemValues.Properties()) source[p.Name] = p.Value.DeepClone();
                source[CK.Resolved] = resolved;
                sources.Add(source);
            }
            return Js.Obj(CK.Totals, totals, CK.Sources, sources);
        }

        /// <summary>gameProfileEffectiveAttributes(registries, run, attributes): the allocated attributes plus what the worn gear adds.</summary>
        public static JObject EffectiveAttributes(CoreRunData d, JObject run, JObject attributes)
        {
            var totals = EquipmentReceipt(d, run).Obj(CK.Totals);
            var result = new JObject();
            foreach (var p in (attributes ?? new JObject()).Properties()) result.Put(p.Name, Js.D(p.Value) + Pos(totals[p.Name]));
            return result;
        }
    }
}
