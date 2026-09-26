using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Run
{
    /// <summary>The options object the shipped derived-stat door takes (derivedOptions + the snapshot extras).</summary>
    public sealed class DerivedOptions
    {
        public JObject ModeModifiers;
        public JToken RunModifiers;
        public JObject ExplicitOverride;
        public string Authority;
        public List<string> AttributeIds;
        public List<string> ClassFields;
        public List<string> DamageSchools;
        public JObject ClassDef;
        public JObject RelicModifierReceipt;

        public DerivedOptions With(JObject classDef, JObject relicModifierReceipt)
        {
            var copy = (DerivedOptions)MemberwiseClone();
            copy.ClassDef = classDef;
            copy.RelicModifierReceipt = relicModifierReceipt;
            return copy;
        }
    }

    /// <summary>A derived-stat receipt (shipped deriveStat): the pieces and the capped value.</summary>
    public sealed class StatReceipt
    {
        public string Id;
        public double Points;
        public double Tier;
        public double Base;
        public double GainPerTier;
        public double LevelBonus;
        public double Raw;
        public double Value;
    }

    /// <summary>
    /// The post-Phase-1 derived-stat rules (shipped model/derivedStats.js): validation, the layered resolve, the
    /// pure calculation and the host-created rule snapshot a run carries. The row vocabulary is data
    /// (rules/runEngine.json derived.*).
    /// </summary>
    public static class DerivedStats
    {
        private static List<string> StatIds(RunData d) => d.RuleList(RK.Derived, RK.StatIds);

        /// <summary>derivedStatIdsFor(rulesetVersion): a row introduced by a later ruleset is required only from it on.</summary>
        public static List<string> StatIdsFor(RunData d, JToken rulesetVersion)
        {
            var since = d.RuleObj(RK.Derived, RK.RowSinceRuleset);
            var version = RunJs.Number(rulesetVersion);
            if (double.IsNaN(version)) version = 0;
            return StatIds(d).Where(id => since[id] == null || version >= since.Num(id)).ToList();
        }

        private static void Add(List<Problem> out_, string path, string msg) => out_.Add(new Problem(path, msg));

        private static string Sub(string path, string key) => string.IsNullOrEmpty(path) ? key : path + RV.PathDot + key;

        private static void UnknownFields(RunData d, List<Problem> out_, JToken value, string allowedKey, string path)
        {
            if (!(value is JObject o)) return;
            var allowed = d.RuleList(RK.Derived, allowedKey);
            foreach (var p in o.Properties()) if (!allowed.Contains(p.Name)) Add(out_, Sub(path, p.Name), RM.UnknownField);
        }

        private static void ValidatePoints(List<Problem> out_, JToken value, string path, bool required)
        {
            if (value == null && !required) return;
            if (!Js.IsFinite(value) || Js.D(value) <= 0) Add(out_, path, RM.MustBePositiveFinite);
        }

        private static void ValidateClassRef(RunData d, List<Problem> out_, JToken value, string path, List<string> classFields)
        {
            if (!(value is JObject o))
            {
                Add(out_, path, RM.MustBeFiniteOrClassField);
                return;
            }
            UnknownFields(d, out_, o, RK.BaseFields, path);
            if (o.Str(RK.Strategy) != RV.ClassField) Add(out_, Sub(path, RK.Strategy), RM.MustBeClassField);
            if (!Js.IsStr(o[RK.Field]) || !classFields.Contains(o.Str(RK.Field)))
                Add(out_, Sub(path, RK.Field), RunJs.Fmt(RM.MustNameOneOf, string.Join(RV.ListJoiner, classFields)));
        }

        private static void ValidateGain(RunData d, List<Problem> out_, JToken value, string path, bool required, List<string> classFields)
        {
            if (value == null && !required) return;
            if (Js.IsFinite(value)) return;
            ValidateClassRef(d, out_, value, path, classFields);
        }

        private static void ValidateRounding(RunData d, List<Problem> out_, JToken value, string path, bool required)
        {
            if (value == null && !required) return;
            var legal = d.RuleList(RK.Derived, RK.Rounding);
            if (!Js.IsStr(value) || !legal.Contains(Js.Str(value))) Add(out_, path, RunJs.Fmt(RM.MustBeOneOf, string.Join(RV.ListJoiner, legal)));
        }

        private static void ValidateCap(List<Problem> out_, JToken value, string path, bool required)
        {
            if (value == null && !required) return;
            if (!(value != null && value.Type == JTokenType.Null) && (!Js.IsFinite(value) || Js.D(value) < 0)) Add(out_, path, RM.MustBeNullOrNonNegative);
        }

        private static void ValidateBase(RunData d, List<Problem> out_, JToken value, string path, bool required, List<string> classFields)
        {
            if (value == null && !required) return;
            if (Js.IsFinite(value)) return;
            ValidateClassRef(d, out_, value, path, classFields);
        }

        private static void ValidatePerLevel(RunData d, List<Problem> out_, JToken value, string path)
        {
            if (value == null) return;
            if (!(value is JObject o))
            {
                Add(out_, path, RM.MustBePerLevel);
                return;
            }
            UnknownFields(d, out_, o, RK.PerLevelFields, path);
            if (!Js.IsInt(o[RK.Every]) || o.Num(RK.Every) <= 0) Add(out_, Sub(path, RK.Every), RM.MustBePositiveLevels);
            if (!Js.IsFinite(o[RK.Gain]) || o.Num(RK.Gain) < 0) Add(out_, Sub(path, RK.Gain), RM.MustBeNonNegativeFinite);
        }

        private static void ValidateDefaults(RunData d, List<Problem> out_, JToken value, string path, bool partial)
        {
            if (!(value is JObject o))
            {
                Add(out_, path, RM.MustBePlainObject);
                return;
            }
            UnknownFields(d, out_, o, RK.DefaultFields, path);
            foreach (var key in d.RuleList(RK.Derived, RK.DefaultFields)) if (!partial && o[key] == null) Add(out_, Sub(path, key), RM.Missing);
            ValidatePoints(out_, o[RK.PointsPerTier], Sub(path, RK.PointsPerTier), !partial);
            ValidateRounding(d, out_, o[RK.Rounding], Sub(path, RK.Rounding), !partial);
            ValidateCap(out_, o[RK.Cap], Sub(path, RK.Cap), !partial);
        }

        private static void ValidateRule(RunData d, List<Problem> out_, JToken value, string path, DerivedOptions options, bool partial)
        {
            if (!(value is JObject o))
            {
                Add(out_, path, RM.MustBePlainObject);
                return;
            }
            UnknownFields(d, out_, o, RK.RuleFields, path);
            if (!partial)
                foreach (var key in d.RuleList(RK.Derived, RK.RequiredRuleFields))
                    if (o[key] == null) Add(out_, Sub(path, key), RM.Missing);
            ValidateBase(d, out_, o[K.Base], Sub(path, K.Base), !partial, options.ClassFields);
            var source = o[RK.SourceStat];
            if ((source != null || !partial) && (!Js.IsStr(source) || !options.AttributeIds.Contains(Js.Str(source))))
            {
                var got = Js.IsStr(source) ? RunJs.Fmt(RM.QuotedPrefix, Js.Str(source)) : string.Empty;
                Add(out_, Sub(path, RK.SourceStat), got + RunJs.Fmt(RM.MustNameOneOf, string.Join(RV.ListJoiner, options.AttributeIds)));
            }
            ValidatePoints(out_, o[RK.PointsPerTier], Sub(path, RK.PointsPerTier), false);
            ValidateGain(d, out_, o[RK.GainPerTier], Sub(path, RK.GainPerTier), !partial, options.ClassFields);
            ValidateRounding(d, out_, o[RK.Rounding], Sub(path, RK.Rounding), false);
            ValidateCap(out_, o[RK.Cap], Sub(path, RK.Cap), false);
            ValidatePerLevel(d, out_, o[RK.PerLevel], Sub(path, RK.PerLevel));
        }

        /// <summary>normalizedOptions: the three lists, with the shipped default class fields.</summary>
        private static DerivedOptions Normalized(RunData d, DerivedOptions options)
        {
            return new DerivedOptions
            {
                AttributeIds = options?.AttributeIds != null ? new List<string>(options.AttributeIds) : new List<string>(),
                ClassFields = options?.ClassFields != null ? new List<string>(options.ClassFields) : d.RuleList(RK.Derived, RK.ClassFields),
                DamageSchools = options?.DamageSchools != null ? new List<string>(options.DamageSchools) : new List<string>(),
            };
        }

        /// <summary>derivedStatRuleProblems: named schema problems; never throws, never repairs.</summary>
        public static List<Problem> RuleProblems(RunData d, JToken source, DerivedOptions options)
        {
            var out_ = new List<Problem>();
            var opts = Normalized(d, options);
            if (!(source is JObject s)) return new List<Problem> { new Problem(RK.DerivedStatRules, RM.MustBePlainObject) };
            UnknownFields(d, out_, s, RK.RootFields, RK.DerivedStatRules);
            var versions = d.RuleNums(RK.Derived, RK.RulesetVersions);
            if (!Js.IsInt(s[RK.RulesetVersion]) || !versions.Contains(s.Num(RK.RulesetVersion)))
                Add(out_, RK.RulesetVersion, RunJs.Fmt(RM.MustBeOneOf, string.Join(RV.ListJoiner, versions.Select(RunJs.NumStr))));
            ValidateDefaults(d, out_, s[K.Defaults], K.Defaults, false);
            if (!(s[RK.Rules] is JObject rules))
            {
                Add(out_, RK.Rules, RM.MustBePlainObject);
                return out_;
            }
            var required = StatIdsFor(d, s[RK.RulesetVersion]);
            foreach (var id in required)
            {
                if (rules[id] == null) Add(out_, RK.Rules + RV.PathDot + id, RM.MissingDerivedRow);
                else ValidateRule(d, out_, rules[id], RK.Rules + RV.PathDot + id, opts, false);
            }
            foreach (var id in StatIds(d))
                if (!required.Contains(id) && rules[id] != null) ValidateRule(d, out_, rules[id], RK.Rules + RV.PathDot + id, opts, false);
            foreach (var p in rules.Properties())
                if (!StatIds(d).Contains(p.Name)) Add(out_, RK.Rules + RV.PathDot + p.Name, RunJs.Fmt(RM.UnknownDerivedRow, p.Name));
            return out_;
        }

        private static List<Problem> OverrideProblems(RunData d, JToken value, string path, DerivedOptions options)
        {
            var out_ = new List<Problem>();
            if (!(value is JObject o)) return new List<Problem> { new Problem(path, RM.MustBePlainObject) };
            UnknownFields(d, out_, o, RK.OverrideFields, path);
            if (o[K.Defaults] != null) ValidateDefaults(d, out_, o[K.Defaults], path + RV.PathDot + K.Defaults, true);
            if (o[RK.Rules] != null)
            {
                if (!(o[RK.Rules] is JObject rules)) Add(out_, path + RV.PathDot + RK.Rules, RM.MustBePlainObject);
                else
                    foreach (var p in rules.Properties())
                    {
                        var rowPath = path + RV.PathDot + RK.Rules + RV.PathDot + p.Name;
                        if (!StatIds(d).Contains(p.Name)) Add(out_, rowPath, RunJs.Fmt(RM.UnknownDerivedRow, p.Name));
                        else ValidateRule(d, out_, p.Value, rowPath, options, true);
                    }
            }
            return out_;
        }

        private static void ThrowProblems(string label, List<Problem> problems)
        {
            if (problems.Count > 0) throw new InvalidOperationException(RunJs.Fmt(RM.PathMessage, label, Problem.Join(problems)));
        }

        /// <summary>The override layers in order: mode, run (one or many), explicit.</summary>
        private static List<KeyValuePair<string, JToken>> Layers(DerivedOptions options)
        {
            var layers = new List<KeyValuePair<string, JToken>> { new KeyValuePair<string, JToken>(RK.ModeModifiers, options?.ModeModifiers) };
            var run = options?.RunModifiers;
            var runs = run is JArray a ? a.ToList() : Js.Truthy(run) ? new List<JToken> { run } : new List<JToken>();
            for (var i = 0; i < runs.Count; i++) layers.Add(new KeyValuePair<string, JToken>(RunJs.Fmt(RM.RunModifierPath, i), runs[i]));
            layers.Add(new KeyValuePair<string, JToken>(RK.ExplicitOverride, options?.ExplicitOverride));
            return layers;
        }

        /// <summary>resolveDerivedStatRules: authored rows plus the mode/run/debug layers, as one self-contained table.</summary>
        public static JObject ResolveRules(RunData d, JObject source, DerivedOptions options)
        {
            var opts = Normalized(d, options);
            ThrowProblems(RK.DerivedStatRules, RuleProblems(d, source, opts));
            var layers = Layers(options);
            foreach (var layer in layers)
            {
                if (Js.Nullish(layer.Value)) continue;
                ThrowProblems(layer.Key, OverrideProblems(d, layer.Value, layer.Key, opts));
            }
            var defaults = source.Obj(K.Defaults);
            var rules = new JObject();
            foreach (var id in StatIds(d))
                if (source.Obj(RK.Rules)[id] != null) rules[id] = Js.Spread(defaults, (JObject)source.Obj(RK.Rules)[id].DeepClone());
            var replayed = new JObject
            {
                [RK.RulesetVersion] = source[RK.RulesetVersion]?.DeepClone(),
                [K.Defaults] = Js.Spread(defaults),
                [RK.Rules] = rules,
            };
            foreach (var layer in layers)
            {
                if (!(layer.Value is JObject l) || !Js.Truthy(layer.Value)) continue;
                if (Js.Truthy(l[K.Defaults]))
                {
                    Assign(replayed.Obj(K.Defaults), l.Obj(K.Defaults));
                    foreach (var id in StatIds(d)) if (rules.Obj(id) != null) Assign(rules.Obj(id), l.Obj(K.Defaults));
                }
                if (Js.Truthy(l[RK.Rules]))
                    foreach (var p in l.Obj(RK.Rules).Properties())
                    {
                        if (rules.Obj(p.Name) == null) throw new InvalidOperationException(RunJs.Fmt(RM.OverridePatchesMissingRow, p.Name, RunJs.Show(replayed[RK.RulesetVersion])));
                        Assign(rules.Obj(p.Name), p.Value as JObject);
                    }
            }
            return replayed;
        }

        /// <summary><c>Object.assign(target, source)</c>.</summary>
        private static void Assign(JObject target, JObject source)
        {
            if (source == null) return;
            foreach (var p in source.Properties()) target[p.Name] = p.Value.DeepClone();
        }

        private static double ClassFieldValue(JToken reference, JObject classDef, string statId, string message)
        {
            if (Js.IsFinite(reference)) return Js.D(reference);
            var field = Js.Str(Js.Get(reference, RK.Field));
            var value = classDef != null && reference != null && field != null ? classDef[field] : null;
            if (!Js.IsFinite(value)) throw new InvalidOperationException(RunJs.Fmt(message, statId, field ?? V.Undefined));
            return Js.D(value);
        }

        private static double BaseValue(JToken b, JObject classDef, string statId) => ClassFieldValue(b, classDef, statId, RM.BaseClassFieldNotFinite);

        private static double GainValue(JToken g, JObject classDef, string statId) => ClassFieldValue(g, classDef, statId, RM.GainClassFieldNotFinite);

        private static double Rounded(string rounding, double x)
        {
            if (rounding == RV.Floor) return Math.Floor(x);
            if (rounding == RV.Ceil) return Math.Ceiling(x);
            if (rounding == RV.Round) return RunJs.Round(x);
            throw new InvalidOperationException(RunJs.Fmt(RM.RoundingNotExecutable, rounding));
        }

        /// <summary>deriveAttributeTierReceipt: tier = round(points / pointsPerTier), value = tier × gain.</summary>
        public static StatReceipt AttributeTierReceipt(JObject rule, JObject attributes, JObject classDef, string statId)
        {
            if (rule == null) throw new InvalidOperationException(RM.TierRuleNotResolved);
            var sourceStat = rule.Str(RK.SourceStat);
            var points = attributes?[sourceStat ?? string.Empty];
            if (!Js.IsFinite(points)) throw new InvalidOperationException(RunJs.Fmt(RM.SourceStatNotFinite, sourceStat));
            if (!Js.IsFinite(rule[RK.PointsPerTier]) || rule.Num(RK.PointsPerTier) <= 0) throw new InvalidOperationException(RM.PointsPerTierInvalid);
            var gain = GainValue(rule[RK.GainPerTier], classDef, statId);
            var tier = Rounded(rule.Str(RK.Rounding), Js.D(points) / rule.Num(RK.PointsPerTier));
            return new StatReceipt { Id = statId, Points = Js.D(points), Tier = tier, GainPerTier = gain, Value = tier * gain };
        }

        /// <summary>levelBonus(row, level): floor((level − 1) / every) × gain; 0 without the term or at level 1.</summary>
        public static double LevelBonus(JObject row, double level)
        {
            var term = row?[RK.PerLevel] as JObject;
            if (term == null || Math.Floor(level) != level || level <= 1) return 0;
            if (!Js.IsInt(term[RK.Every]) || term.Num(RK.Every) <= 0 || !Js.IsFinite(term[RK.Gain])) return 0;
            return Math.Floor((level - 1) / term.Num(RK.Every)) * term.Num(RK.Gain);
        }

        /// <summary>deriveStat(resolved, statId, { attributes, classDef, level }).</summary>
        public static StatReceipt Derive(JObject resolved, string statId, JObject attributes, JObject classDef, double level)
        {
            var row = resolved?.Obj(RK.Rules)?.Obj(statId);
            if (row == null) throw new InvalidOperationException(RunJs.Fmt(RM.UnknownDerivedStat, statId));
            var tier = AttributeTierReceipt(row, attributes, classDef, statId);
            var b = BaseValue(row[K.Base], classDef, statId);
            var bonus = LevelBonus(row, level);
            var raw = b + tier.Tier * tier.GainPerTier + bonus;
            var cap = row[RK.Cap];
            var value = cap != null && cap.Type == JTokenType.Null ? raw : Math.Min(raw, Js.D(cap));
            return new StatReceipt { Id = statId, Points = tier.Points, Tier = tier.Tier, Base = b, GainPerTier = tier.GainPerTier, LevelBonus = bonus, Raw = raw, Value = value };
        }

        private static List<string> FoldProblems(RunData d, JObject term, JObject rule)
        {
            var problems = new List<string>();
            if (term == null || rule == null) return new List<string> { RM.FoldNeedsRule };
            if (term.Str(RK.SourceStat) != rule.Str(RK.SourceStat)) problems.Add(RunJs.Fmt(RM.FoldSourceStat, rule.Str(RK.SourceStat)));
            if (term[RK.PointsPerTier] != null && Js.D(term[RK.PointsPerTier]) != Js.D(rule[RK.PointsPerTier]))
                problems.Add(RunJs.Fmt(RM.FoldPointsPerTier, RunJs.Show(rule[RK.PointsPerTier])));
            if (rule.Str(RK.Rounding) != RV.Floor) problems.Add(RunJs.Fmt(RM.FoldRounding, rule.Str(RK.Rounding)));
            return problems;
        }

        private static JObject ResolveSnapshotNumbers(RunData d, JObject rules, JObject classDef, JObject relicModifierReceipt, JObject explicitOverride)
        {
            if (classDef == null) throw new InvalidOperationException(RM.SnapshotNeedsClass);
            var out_ = RunJs.Clone(rules);
            foreach (var p in out_.Obj(RK.Rules).Properties())
            {
                var row = (JObject)p.Value;
                row.Put(K.Base, BaseValue(row[K.Base], classDef, p.Name));
                row.Put(RK.GainPerTier, GainValue(row[RK.GainPerTier], classDef, p.Name));
            }
            var resources = relicModifierReceipt?.Obj(RK.Resources) ?? new JObject();
            foreach (var p in resources.Properties())
            {
                var bonus = p.Value as JObject;
                if (!Js.Truthy(bonus) || (!bonus.Is(RK.Flat) && !(Js.Items(bonus[RK.AttributeTiers]).Any()))) continue;
                var row = out_.Obj(RK.Rules).Obj(p.Name);
                if (row == null) throw new InvalidOperationException(RunJs.Fmt(RM.RelicTargetsUnknownResource, p.Name));
                var explicitRow = explicitOverride?.Obj(RK.Rules)?.Obj(p.Name) ?? new JObject();
                if (explicitRow[K.Base] == null) row.Put(K.Base, row.Num(K.Base) + bonus.Or0(RK.Flat));
                if (explicitRow[RK.GainPerTier] == null)
                    foreach (var term in Js.Items(bonus[RK.AttributeTiers]).OfType<JObject>())
                    {
                        if (FoldProblems(d, term, row).Count > 0)
                            throw new InvalidOperationException(RunJs.Fmt(RM.RelicTierCannotFold, p.Name, term.Str(RK.SourceStat), RunJs.Show(term[RK.PointsPerTier]),
                                row.Str(RK.SourceStat), RunJs.Show(row[RK.PointsPerTier]), row.Str(RK.Rounding)));
                        row.Put(RK.GainPerTier, row.Num(RK.GainPerTier) + term.Num(RK.AmountPerTier));
                    }
            }
            return out_;
        }

        /// <summary>createDerivedStatRuleSnapshot: the host-created, immutable-by-convention rules receipt.</summary>
        public static JObject CreateSnapshot(RunData d, JObject source, DerivedOptions options)
        {
            if (options.Authority != RV.Host) throw new InvalidOperationException(RM.SnapshotNeedsHost);
            var rules = ResolveSnapshotNumbers(d, ResolveRules(d, source, options), options.ClassDef, options.RelicModifierReceipt, options.ExplicitOverride);
            var receipt = options.RelicModifierReceipt;
            return RunJs.Clone(new JObject
            {
                [K.SnapshotVersion] = Js.N(d.RuleNum(RK.Derived, K.SnapshotVersion)),
                [RK.RulesetVersion] = rules[RK.RulesetVersion]?.DeepClone(),
                [RK.Rules] = rules,
                [RK.RelicModifiers] = receipt != null
                    ? Js.Obj(K.DamageBySchoolAdd, receipt[K.DamageBySchoolAdd]?.DeepClone(), RK.Sources, receipt[RK.Sources]?.DeepClone())
                    : Js.Obj(K.DamageBySchoolAdd, new JObject(), RK.Sources, new JArray()),
            });
        }
    }
}
