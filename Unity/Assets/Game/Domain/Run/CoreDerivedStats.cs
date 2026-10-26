using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2RunKeys;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// The Core 2 derived-stat rules (shipped model/derivedStats.js at ruleset 7): every stat is a row of one table,
    /// <c>value = clamp(base + sum floor(attribute x weight) + level term, min, max)</c>, resolved with the profile's
    /// override layer and frozen into the run's snapshot. The legacy 405 door stays in <see cref="DerivedStats"/>. The row
    /// validators are not ported: the table is the profile's own content (checked when it loads), and the only layer is
    /// the profile's explicit override (<see cref="CoreRunRules.StatOptions"/>).
    /// </summary>
    public static class CoreDerivedStats
    {
        private static double Epsilon(CoreRunData d) => d.RuleSection(CK.Derived).Num(CK.Epsilon);

        private static List<string> NonWeightFields(CoreRunData d) => RunJs.Strs(d.RuleSection(CK.Derived)[CK.NonWeightFields]);

        private static bool Own(JObject o, string key) => o != null && o[key] != null;

        // ---- normalization: a legacy row in the one vocabulary (a no-op for a ruleset 6 or 7 row) ----

        /// <summary>normalizeRule(row): { sourceStat, pointsPerTier, gainPerTier, perLevel } carried onto the weights, increase, gain and level cadence.</summary>
        public static JObject NormalizeRule(JObject row)
        {
            if (row == null) return null;
            var output = new JObject();
            foreach (var p in row.Properties())
            {
                if (p.Name == RK.SourceStat || p.Name == K.PointsPerTier || p.Name == K.GainPerTier || p.Name == RK.PerLevel) continue;
                output[p.Name] = p.Value.DeepClone();
            }
            if (Js.IsStr(row[RK.SourceStat])) output.Put(row.Str(RK.SourceStat), 1);
            if (Own(row, K.PointsPerTier)) output[CK.PointsPerIncrease] = row[K.PointsPerTier].DeepClone();
            if (Own(row, K.GainPerTier)) output[CK.Gain] = row[K.GainPerTier].DeepClone();
            if (row[RK.PerLevel] is JObject cadence)
            {
                output[RK.PerLevel] = cadence[RK.Gain]?.DeepClone();
                output[CK.PerLevelEvery] = cadence[RK.Every]?.DeepClone();
            }
            else if (Own(row, RK.PerLevel)) output[RK.PerLevel] = row[RK.PerLevel].DeepClone();
            return output;
        }

        private static JObject NormalizeDefaultsPatch(JObject patch)
        {
            if (patch == null) return null;
            var output = new JObject();
            foreach (var p in patch.Properties()) output[p.Name == K.PointsPerTier ? CK.PointsPerIncrease : p.Name] = p.Value.DeepClone();
            return output;
        }

        private static JObject NormalizeRulePatch(JObject patch, List<string> attributeIds)
        {
            if (patch == null) return null;
            var output = NormalizeRule(patch);
            if (Js.IsStr(patch[RK.SourceStat]))
                foreach (var id in attributeIds) if (id != patch.Str(RK.SourceStat)) output.Put(id, 0);
            return output;
        }

        /// <summary>rowForClass(row, classId): the row one class reads, its per-class form over the shared bounds.</summary>
        public static JObject RowForClass(CoreRunData d, JObject row, string classId)
        {
            if (row == null || !Own(row, CK.ByClass)) return row;
            var shared = new JObject();
            foreach (var p in row.Properties()) if (p.Name != CK.ByClass) shared[p.Name] = p.Value.DeepClone();
            var own = classId != null && row[CK.ByClass] is JObject byClass ? byClass[classId] as JObject : null;
            if (own == null) return shared;
            var bounds = new JObject();
            var nonWeight = NonWeightFields(d);
            foreach (var p in shared.Properties()) if (nonWeight.Contains(p.Name)) bounds[p.Name] = p.Value.DeepClone();
            return Js.Spread(bounds, own);
        }

        /// <summary>resolvedRuleRow(table, statId, classId).</summary>
        public static JObject ResolvedRow(CoreRunData d, JObject table, string statId, string classId)
        {
            var row = table?.Obj(K.Rules)?.Obj(statId);
            if (row == null) return null;
            return RowForClass(d, NormalizeRule(Js.Spread(NormalizeDefaultsPatch(table.Obj(K.Defaults) ?? new JObject()), row)), classId);
        }

        /// <summary>ruleWeights(row): the attributes the row answers to, as (id, weight), weight finite and not zero.</summary>
        public static List<KeyValuePair<string, double>> RuleWeights(CoreRunData d, JObject row)
        {
            var result = new List<KeyValuePair<string, double>>();
            if (row == null) return result;
            var nonWeight = NonWeightFields(d);
            foreach (var p in row.Properties())
                if (!nonWeight.Contains(p.Name) && Js.IsFinite(p.Value) && Js.D(p.Value) != 0) result.Add(new KeyValuePair<string, double>(p.Name, Js.D(p.Value)));
            return result;
        }

        /// <summary>ruleTierSize(rule): how many points of the rule's one attribute buy one unit of its gain, or null.</summary>
        public static double? RuleTierSize(CoreRunData d, JObject rule)
        {
            var row = NormalizeRule(rule);
            var weights = RuleWeights(d, row);
            if (weights.Count != 1) return null;
            var perIncrease = Js.IsFinite(row[CK.PointsPerIncrease]) ? row.Num(CK.PointsPerIncrease) : 1;
            if (perIncrease != 1 || (row[CK.Gain] != null && Js.D(row[CK.Gain]) != 1)) return weights[0].Value == 1 ? perIncrease : (double?)null;
            return Math.Floor(weights[0].Value) == weights[0].Value ? 1 : (double?)null;
        }

        private static string RelicFoldTarget(JObject rule)
        {
            var row = NormalizeRule(rule);
            var perIncrease = Js.IsFinite(row[CK.PointsPerIncrease]) ? row.Num(CK.PointsPerIncrease) : 1;
            return perIncrease != 1 || (row[CK.Gain] != null && Js.D(row[CK.Gain]) != 1) ? CK.Gain : CK.Weight;
        }

        private static bool CanFold(CoreRunData d, JObject term, JObject rule)
        {
            if (term == null || rule == null) return false;
            var row = NormalizeRule(rule);
            var weights = RuleWeights(d, row);
            if (weights.Count != 1) return false;
            if (term.Str(RK.SourceStat) != weights[0].Key) return false;
            var size = RuleTierSize(d, row);
            if (size == null) return false;
            if (term[K.PointsPerTier] != null && Js.D(term[K.PointsPerTier]) != size.Value) return false;
            return (row.Str(RK.Rounding) ?? RV.Floor) == RV.Floor;
        }

        // ---- the layered resolve ----

        /// <summary>The override layers in order: mode, run (one or many), explicit.</summary>
        private static List<JObject> Layers(DerivedOptions options)
        {
            var layers = new List<JObject> { options?.ModeModifiers };
            if (options?.RunModifiers is JArray runs) layers.AddRange(runs.OfType<JObject>());
            else if (Js.Truthy(options?.RunModifiers)) layers.Add(options.RunModifiers as JObject);
            layers.Add(options?.ExplicitOverride);
            return layers;
        }

        /// <summary>resolveDerivedStatRules(source, options): the authored table plus the override layers, every row in the one vocabulary.</summary>
        public static JObject ResolveRules(CoreRunData d, JObject source, DerivedOptions options)
        {
            var defaults = source.Obj(K.Defaults) ?? new JObject();
            var rules = new JObject();
            foreach (var p in (source.Obj(K.Rules) ?? new JObject()).Properties())
                rules[p.Name] = NormalizeRule(Js.Spread(defaults, (JObject)p.Value.DeepClone()));
            var replayed = new JObject
            {
                [RK.RulesetVersion] = source[RK.RulesetVersion]?.DeepClone(),
                [K.Defaults] = Js.Spread(Js.Obj(RK.PerLevel, 0), NormalizeDefaultsPatch(defaults)),
                [K.Rules] = rules,
            };
            foreach (var layer in Layers(options))
            {
                if (!Js.Truthy(layer)) continue;
                if (Js.Truthy(layer[K.Defaults]))
                {
                    var patch = NormalizeDefaultsPatch(layer.Obj(K.Defaults));
                    Assign(replayed.Obj(K.Defaults), patch);
                    foreach (var row in rules.Properties()) Assign((JObject)row.Value, patch);
                }
                if (Js.Truthy(layer[K.Rules]))
                    foreach (var p in layer.Obj(K.Rules).Properties())
                    {
                        if (rules.Obj(p.Name) == null) throw new InvalidOperationException(RunJs.Fmt(RM.OverridePatchesMissingRow, p.Name, RunJs.Show(replayed[RK.RulesetVersion])));
                        if (p.Value is JObject raw && (Own(raw, K.GainPerTier) || Own(raw, RK.SourceStat)))
                            throw new InvalidOperationException(RunJs.Fmt(Ashen.Generated.Core2RunMessages.RowHasNoTierFields, p.Name, Own(raw, K.GainPerTier) ? K.GainPerTier : RK.SourceStat));
                        Assign(rules.Obj(p.Name), NormalizeRulePatch(p.Value as JObject, options?.AttributeIds ?? new List<string>()));
                    }
            }
            return replayed;
        }

        private static void Assign(JObject target, JObject source)
        {
            if (source == null) return;
            foreach (var p in source.Properties()) target[p.Name] = p.Value.DeepClone();
        }

        // ---- the formula ----

        private static double ClassField(JToken reference, JObject classDef, string statId, string message)
        {
            if (Js.IsFinite(reference)) return Js.D(reference);
            var field = Js.Str(Js.Get(reference, RK.Field));
            var value = classDef != null && reference != null && field != null ? classDef[field] : null;
            if (!Js.IsFinite(value)) throw new InvalidOperationException(RunJs.Fmt(message, statId, field ?? Ashen.Generated.CombatValues.Undefined));
            return Js.D(value);
        }

        private static double Rounded(string rounding, double x)
        {
            if (rounding == RV.Floor) return Math.Floor(x);
            if (rounding == RV.Ceil) return Math.Ceiling(x);
            if (rounding == RV.Round) return RunJs.Round(x);
            throw new InvalidOperationException(RunJs.Fmt(RM.RoundingNotExecutable, rounding));
        }

        /// <summary>levelBonus(row, level): the row's level term at a character level; 0 at level 1 or for a row with none.</summary>
        public static double LevelBonus(CoreRunData d, JObject row, double level)
        {
            if (row == null || Math.Floor(level) != level || level <= 1) return 0;
            var term = row[RK.PerLevel];
            if (term is JObject cadence)
            {
                if (!Js.IsInt(cadence[RK.Every]) || cadence.Num(RK.Every) <= 0 || !Js.IsFinite(cadence[RK.Gain])) return 0;
                return Math.Floor((level - 1) / cadence.Num(RK.Every)) * cadence.Num(RK.Gain);
            }
            if (!Js.IsFinite(term) || Js.D(term) == 0) return 0;
            if (Js.IsInt(row[CK.PerLevelEvery]) && row.Num(CK.PerLevelEvery) > 0) return Math.Floor((level - 1) / row.Num(CK.PerLevelEvery)) * Js.D(term);
            var steps = Math.Floor(level - 1);
            var rounding = row.Str(RK.Rounding);
            var floors = rounding == null || rounding == RV.Floor || !(rounding == RV.Ceil || rounding == RV.Round);
            var raw = steps * Js.D(term);
            return floors ? Math.Floor(raw + Epsilon(d)) : Rounded(rounding, raw);
        }

        /// <summary>statRowValue(row, { attributes, level }): the receipt of one row.</summary>
        public static StatReceipt RowValue(CoreRunData d, JObject row, JObject attributes, JObject classDef, double level, string statId)
        {
            if (row == null) throw new InvalidOperationException(RunJs.Fmt(Ashen.Generated.Core2RunMessages.StatHasNoRow, statId));
            var perIncrease = Js.IsFinite(row[CK.PointsPerIncrease]) ? row.Num(CK.PointsPerIncrease) : 1;
            if (perIncrease <= 0) throw new InvalidOperationException(Ashen.Generated.Core2RunMessages.PointsPerIncreaseInvalid);
            double points = 0;
            var terms = new List<KeyValuePair<string, double>>();
            foreach (var weight in RuleWeights(d, row))
            {
                var value = attributes?[weight.Key];
                if (!Js.IsFinite(value)) throw new InvalidOperationException(RunJs.Fmt(Ashen.Generated.Core2RunMessages.AttributeNotFinite, weight.Key));
                var counted = Js.IsFinite(row[CK.AttributeBaseline]) && row.Num(CK.AttributeBaseline) > 0 ? Math.Max(0, Js.D(value) - row.Num(CK.AttributeBaseline)) : Js.D(value);
                var term = Math.Floor(counted * weight.Value + Epsilon(d));
                terms.Add(new KeyValuePair<string, double>(weight.Key, term));
                points += term;
            }
            var gain = row[CK.Gain] == null ? 1 : ClassField(row[CK.Gain], classDef, statId, RM.GainClassFieldNotFinite);
            var rounding = row[RK.Rounding] == null ? RV.Floor : row.Str(RK.Rounding);
            var baselined = Js.IsFinite(row[CK.PointsBaseline]) && row.Num(CK.PointsBaseline) > 0 ? Math.Max(0, points - row.Num(CK.PointsBaseline)) : points;
            var scaled = Js.IsFinite(row[CK.Multiplier]) ? Math.Floor(baselined * row.Num(CK.Multiplier) + Epsilon(d)) : baselined;
            var tier = perIncrease == 1 ? scaled : Rounded(rounding, scaled / perIncrease);
            var baseValue = ClassField(row[K.Base], classDef, statId, RM.BaseClassFieldNotFinite);
            var bonus = LevelBonus(d, row, level);
            var raw = baseValue + tier * gain + bonus;
            var cap = row[RK.Cap];
            double? min = Js.IsFinite(row[CK.Min]) ? row.Num(CK.Min) : (double?)null;
            double? max = Js.IsFinite(row[CK.Max]) ? row.Num(CK.Max) : (double?)null;
            var result = cap == null || cap.Type == JTokenType.Null ? raw : Math.Min(raw, Js.D(cap));
            if (min.HasValue) result = Math.Max(min.Value, result);
            if (max.HasValue) result = Math.Min(max.Value, result);
            var receipt = new StatReceipt { Id = statId, Points = points, Tier = tier, Base = baseValue, GainPerTier = gain, LevelBonus = bonus, Raw = raw, Value = result, PointsPerIncrease = perIncrease, Min = min, Max = max };
            receipt.Terms.AddRange(terms);
            return receipt;
        }

        /// <summary>deriveStat(resolved, statId, { attributes, classDef, level }).</summary>
        public static StatReceipt Derive(CoreRunData d, JObject resolved, string statId, JObject attributes, JObject classDef, double level) =>
            RowValue(d, ResolvedRow(d, resolved, statId, classDef?.Str(K.Id)), attributes, classDef, level, statId);

        // ---- the snapshot ----

        private static JObject ResolveSnapshotNumbers(CoreRunData d, JObject rules, JObject classDef, JObject relicReceipt, JObject explicitOverride)
        {
            if (classDef == null) throw new InvalidOperationException(RM.SnapshotNeedsClass);
            var output = RunJs.Clone(rules);
            var table = output.Obj(K.Rules);
            foreach (var p in table.Properties().ToList()) table[p.Name] = RowForClass(d, (JObject)p.Value, classDef.Str(K.Id));
            foreach (var p in table.Properties())
            {
                var row = (JObject)p.Value;
                row.Put(K.Base, ClassField(row[K.Base], classDef, p.Name, RM.BaseClassFieldNotFinite));
                if (row[CK.Gain] != null) row.Put(CK.Gain, ClassField(row[CK.Gain], classDef, p.Name, RM.GainClassFieldNotFinite));
            }
            var resources = relicReceipt?.Obj(RK.Resources) ?? new JObject();
            foreach (var p in resources.Properties())
            {
                var bonus = p.Value as JObject;
                if (!Js.Truthy(bonus) || (!bonus.Is(RK.Flat) && !Js.Items(bonus[RK.AttributeTiers]).Any())) continue;
                var row = table.Obj(p.Name);
                if (row == null) throw new InvalidOperationException(RunJs.Fmt(RM.RelicTargetsUnknownResource, p.Name));
                var explicitRow = explicitOverride?.Obj(K.Rules)?.Obj(p.Name) ?? new JObject();
                if (explicitRow[K.Base] == null) row.Put(K.Base, row.Num(K.Base) + bonus.Or0(RK.Flat));
                if (explicitRow[CK.Gain] == null && explicitRow[K.GainPerTier] == null && RuleWeights(d, row).All(w => explicitRow[w.Key] == null))
                    foreach (var term in Js.Items(bonus[RK.AttributeTiers]).OfType<JObject>())
                    {
                        if (!CanFold(d, term, row))
                            throw new InvalidOperationException(RunJs.Fmt(Ashen.Generated.Core2RunMessages.RelicTierCannotFold, p.Name, term.Str(RK.SourceStat), RunJs.Show(term[K.PointsPerTier])));
                        var amount = term.Num(RK.AmountPerTier);
                        if (RelicFoldTarget(row) == CK.Gain) row.Put(CK.Gain, (row[CK.Gain] == null ? 1 : row.Num(CK.Gain)) + amount);
                        else row.Put(term.Str(RK.SourceStat), row.Num(term.Str(RK.SourceStat)) + amount);
                    }
            }
            return output;
        }

        /// <summary>createDerivedStatRuleSnapshot(source, options): the host-created rules receipt a run carries (envelope 3, with its base rules and override).</summary>
        public static JObject CreateSnapshot(CoreRunData d, JObject source, DerivedOptions options)
        {
            if (options.Authority != RV.Host) throw new InvalidOperationException(RM.SnapshotNeedsHost);
            var resolved = ResolveRules(d, source, options);
            var rules = ResolveSnapshotNumbers(d, resolved, options.ClassDef, options.RelicModifierReceipt, options.ExplicitOverride);
            var baseRules = ResolveSnapshotNumbers(d, ResolveRules(d, source, options), options.ClassDef, null, options.ExplicitOverride);
            var receipt = options.RelicModifierReceipt;
            return RunJs.Clone(new JObject
            {
                [K.SnapshotVersion] = Js.N(d.RuleSection(CK.Derived).Num(K.SnapshotVersion)),
                [RK.RulesetVersion] = rules[RK.RulesetVersion]?.DeepClone(),
                [K.Rules] = rules,
                [CK.BaseRules] = baseRules,
                [CK.ModifierOverride] = options.ExplicitOverride != null ? options.ExplicitOverride.DeepClone() : new JObject(),
                [RK.RelicModifiers] = receipt != null
                    ? Js.Obj(K.DamageBySchoolAdd, receipt[K.DamageBySchoolAdd]?.DeepClone(), RK.Sources, receipt[RK.Sources]?.DeepClone())
                    : Js.Obj(K.DamageBySchoolAdd, new JObject(), RK.Sources, new JArray()),
            });
        }
    }
}
