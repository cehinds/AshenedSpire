using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CM = Ashen.Generated.CombatMath;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using MM = Ashen.Generated.MapMessages;
using MV = Ashen.Generated.MapValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Map
{
    /// <summary>A named refusal (the shipped <c>{ key, msg }</c>): the key names the entry, the message is developer-facing.</summary>
    public sealed class MapError
    {
        public MapError(string key, string message)
        {
            Key = key;
            Message = message;
        }

        public string Key { get; }

        public string Message { get; }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, MV.ErrorPair, Key, Message);

        public static string Join(IEnumerable<MapError> errors) => string.Join(MV.ErrorSeparator, errors.Select(e => e.ToString()));
    }

    /// <summary>
    /// The resolved floor plan (shipped resolveFloorPlan's <c>plan</c>): every anchor turned into a floor for this act's
    /// length, the rest-before-Elite floors, the promised counts and the Unknown-node weights.
    /// </summary>
    public sealed class FloorPlan
    {
        /// <summary><c>config.floors</c> as authored (compared with <c>===</c> by the rest-donor rule).</summary>
        public JToken FloorsToken { get; internal set; }

        public double Floors => Js.D(FloorsToken);

        public int Band { get; internal set; }

        /// <summary>floor → fixed type. Integer keys iterate ascending, as a JS object's integer keys do.</summary>
        public SortedDictionary<int, string> Fixed { get; } = new SortedDictionary<int, string>();

        public int ShrineFrom { get; internal set; }
        public int EliteFrom { get; internal set; }
        public bool RestBeforeElite { get; internal set; }
        public List<int> RestFloors { get; } = new List<int>();
        public int NoShrineOn { get; internal set; }
        public int MinElites { get; internal set; }
        public int MinMerchants { get; internal set; }

        /// <summary>minima: { elite: minElites, merchant: minMerchants }, in that order.</summary>
        public List<KeyValuePair<string, int>> Minima { get; } = new List<KeyValuePair<string, int>>();

        public JObject UnknownWeights { get; internal set; }

        public string FixedAt(int floor) => Fixed.TryGetValue(floor, out var t) ? t : null;

        public int MinimumOf(string type)
        {
            foreach (var kv in Minima) if (kv.Key == type) return kv.Value;
            return 0;
        }

        /// <summary>The plan JSON in the shipped key order.</summary>
        public JObject ToJson()
        {
            var fixedJson = new JObject();
            foreach (var kv in Fixed) fixedJson[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
            var minima = new JObject();
            foreach (var kv in Minima) minima[kv.Key] = kv.Value;
            return new JObject
            {
                [MK.Floors] = FloorsToken?.DeepClone(),
                [MK.Band] = Band,
                [MK.Fixed] = fixedJson,
                [MK.ShrineFrom] = ShrineFrom,
                [MK.EliteFrom] = EliteFrom,
                [MK.RestBeforeElite] = RestBeforeElite,
                [MK.RestFloors] = new JArray(RestFloors.Select(f => (object)f).ToArray()),
                [MK.NoShrineOn] = NoShrineOn,
                [MK.MinElites] = MinElites,
                [MK.MinMerchants] = MinMerchants,
                [MK.Minima] = minima,
                [MK.UnknownWeights] = UnknownWeights == null ? JValue.CreateNull() : UnknownWeights.DeepClone(),
            };
        }
    }

    /// <summary>A shipped floorplan.js readout note a PLAYER has to read, kept structured so the screen words it.</summary>
    public sealed class RunShapeNote
    {
        public RunShapeNote(string type, int count)
        {
            Type = type;
            Count = count;
        }

        /// <summary>The node type whose zero weight does not mean zero nodes.</summary>
        public string Type { get; }

        /// <summary>0 for the Monster fallback note; the promised minimum for a force-placed type.</summary>
        public int Count { get; }
    }

    /// <summary>applyRunShape's answer: the config to generate from (the authored one when refused), refusals, notes.</summary>
    public sealed class RunShapeResult
    {
        public JObject Config { get; internal set; }
        public List<MapError> Errors { get; } = new List<MapError>();
        public List<RunShapeNote> Notes { get; } = new List<RunShapeNote>();
        public bool Changed { get; internal set; }
    }

    /// <summary>
    /// What a floor rule MEANS in an act of a given length (shipped model/floorplan.js): anchors resolve against the
    /// rollable band; the plan is the one resolution the generator, the validator and the tools read. The human
    /// readout (describePlan) is not ported — it is tooling text.
    /// </summary>
    public static class FloorPlans
    {
        /// <summary>rollableFloors(config) = max(0, (floors | 0) - 1).</summary>
        public static int RollableFloors(JObject config) => Math.Max(0, ToInt32(config?[MK.Floors]) - 1);

        /// <summary>resolveAnchor(anchor, config) → floor, or an error message. Never throws; only fractions clamp.</summary>
        public static int? ResolveAnchor(MapRules rules, JToken anchor, JObject config, out string error)
        {
            error = null;
            var band = RollableFloors(config);
            if (band < 1)
            {
                error = string.Format(CultureInfo.InvariantCulture, MM.NoRollableFloor, Describe(config?[MK.Floors]));
                return null;
            }
            if (!(anchor is JObject a))
            {
                error = string.Format(CultureInfo.InvariantCulture, MM.AnchorNotObject, Describe(anchor));
                return null;
            }
            var at = a[MK.At];
            var kind = Js.Str(at);
            if (kind != null && kind == rules.AnchorFirst) return 1;
            if (kind != null && kind == rules.AnchorLast) return band;
            if (kind != null && kind == rules.AnchorFloor)
            {
                var n = a[MK.Index];
                if (!Js.IsInt(n))
                {
                    error = string.Format(CultureInfo.InvariantCulture, MM.AnchorFloorIndex, Describe(n));
                    return null;
                }
                var index = Js.D(n);
                if (index < 1 || index > band)
                {
                    error = string.Format(CultureInfo.InvariantCulture, MM.AnchorFloorRange, Describe(n), band, Describe(config[MK.Floors]));
                    return null;
                }
                return (int)index;
            }
            if (kind != null && kind == rules.AnchorFraction)
            {
                var f = a[K.Of];
                if (!Js.IsFinite(f) || Js.D(f) <= 0 || Js.D(f) > 1)
                {
                    error = string.Format(CultureInfo.InvariantCulture, MM.AnchorFraction, Describe(f));
                    return null;
                }
                return (int)Math.Min(band, Math.Max(1, MapMath.Round(Js.D(f) * band)));
            }
            error = string.Format(CultureInfo.InvariantCulture, MM.AnchorUnknown, Describe(at), string.Join(MV.ListJoin, rules.AnchorKinds));
            return null;
        }

        /// <summary>resolveFloorPlan(config) → the plan (null when floorRules is missing or not an object) and the refusals.</summary>
        public static FloorPlan Resolve(MapRules rules, JObject config, out List<MapError> errors)
        {
            errors = new List<MapError>();
            var errs = errors;
            void Err(string key, string msg) => errs.Add(new MapError(key, msg));
            string RuleKey(string key) => string.Format(CultureInfo.InvariantCulture, MV.FloorRuleKey, key);

            var band = RollableFloors(config);
            var floorRules = config?[MK.FloorRules];
            if (Js.Nullish(floorRules))
            {
                Err(MK.FloorRules, MM.FloorRulesMissing);
                return null;
            }
            if (!(floorRules is JObject fr))
            {
                Err(MK.FloorRules, string.Format(CultureInfo.InvariantCulture, MM.FloorRulesNotObject, floorRules is JArray ? MV.AnArray : TypeOf(floorRules)));
                return null;
            }

            var plan = new FloorPlan { FloorsToken = config[MK.Floors], Band = band };

            // ---- fixed ranks
            var list = fr[MK.Fixed];
            if (!Js.Nullish(list))
            {
                if (!(list is JArray entries))
                {
                    Err(RuleKey(MK.Fixed), string.Format(CultureInfo.InvariantCulture, MM.FixedNotArray, TypeOf(list)));
                }
                else
                {
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        var at = string.Format(CultureInfo.InvariantCulture, MV.FixedEntryKey, i);
                        if (!(entry is JObject || entry is JArray))
                        {
                            Err(at, MM.FixedEntryNotObject);
                            continue;
                        }
                        var type = Js.Get(entry, K.Type);
                        var typeId = Js.Str(type);
                        if (typeId == null || !rules.NodeTypes.Contains(typeId))
                        {
                            Err(string.Format(CultureInfo.InvariantCulture, MV.FixedEntryTypeKey, i), string.Format(CultureInfo.InvariantCulture, MM.FixedEntryType, Describe(type), string.Join(MV.ListJoin, rules.NodeTypes)));
                            continue;
                        }
                        var floor = ResolveAnchor(rules, entry, config, out var anchorError);
                        if (anchorError != null)
                        {
                            Err(at, string.Format(CultureInfo.InvariantCulture, MV.ErrorPair, typeId, anchorError));
                            continue;
                        }
                        var f = floor.Value;
                        if (plan.Fixed.TryGetValue(f, out var claimed) && claimed != typeId)
                        {
                            Err(at, string.Format(CultureInfo.InvariantCulture, MM.FixedCollision, typeId, f, claimed));
                            continue;
                        }
                        plan.Fixed[f] = typeId;
                    }
                }
            }

            // ---- thresholds
            int AnchorRule(string key, int fallback)
            {
                var a = fr[key];
                if (Js.Nullish(a)) return fallback;
                var floor = ResolveAnchor(rules, a, config, out var anchorError);
                if (anchorError != null)
                {
                    Err(RuleKey(key), anchorError);
                    return fallback;
                }
                return floor.Value;
            }
            if (!Js.Nullish(fr[MK.NoEliteOrShrineBefore])) Err(RuleKey(MK.NoEliteOrShrineBefore), MM.LegacyGate);
            var shrineFrom = AnchorRule(MK.NoShrineBefore, 1);
            var eliteFrom = AnchorRule(MK.NoEliteBefore, 1);
            var noShrineOn = AnchorRule(MK.NoShrineOn, 0);
            foreach (var (key, floor, label) in new[] { (MK.NoShrineBefore, shrineFrom, rules.Shrine), (MK.NoEliteBefore, eliteFrom, rules.Elite) })
                if (band > 0 && floor > band)
                    Err(RuleKey(key), string.Format(CultureInfo.InvariantCulture, MM.GatePastBand, floor, band, label));

            // ---- the rest-before-Elite promise
            var restToken = fr[MK.RestBeforeElite];
            var restBeforeElite = restToken != null && restToken.Type == JTokenType.Boolean && restToken.Value<bool>();
            if (!Js.Nullish(restToken) && restToken.Type != JTokenType.Boolean)
                Err(RuleKey(MK.RestBeforeElite), string.Format(CultureInfo.InvariantCulture, MM.RestNotBoolean, Describe(restToken)));
            int? fixedRest = null;
            if (restBeforeElite && band > 0)
            {
                foreach (var kv in plan.Fixed)
                    if (kv.Value == rules.Shrine && kv.Key < eliteFrom && (fixedRest == null || kv.Key < fixedRest)) fixedRest = kv.Key;
                for (var f = shrineFrom; f < eliteFrom && f <= band; f++)
                    if (f != noShrineOn && !plan.Fixed.ContainsKey(f)) plan.RestFloors.Add(f);
                if (plan.RestFloors.Count == 0 && fixedRest == null)
                    Err(RuleKey(MK.RestBeforeElite), string.Format(CultureInfo.InvariantCulture, MM.RestUnsatisfiable, shrineFrom, eliteFrom, noShrineOn));
                foreach (var kv in plan.Fixed)
                {
                    if (kv.Value != rules.Elite) continue;
                    var floor = kv.Key;
                    var rest = (fixedRest != null && fixedRest < floor) || plan.RestFloors.Any(r => r < floor);
                    if (!rest) Err(RuleKey(MK.Fixed), string.Format(CultureInfo.InvariantCulture, MM.FixedEliteNoRest, floor));
                }
            }

            // ---- counts
            int Count(string key, string legacy)
            {
                var v = !Js.Nullish(fr[key]) ? fr[key] : fr[legacy];
                if (Js.Nullish(v)) return 0;
                if (!Js.IsInt(v) || Js.D(v) < 0)
                {
                    Err(RuleKey(key), string.Format(CultureInfo.InvariantCulture, MM.CountInvalid, Describe(v)));
                    return 0;
                }
                if (!Js.Nullish(fr[legacy])) Err(RuleKey(legacy), string.Format(CultureInfo.InvariantCulture, MM.CountRenamed, key));
                return (int)Js.D(v);
            }
            var minElites = Count(MK.MinElites, MK.MinReachableElites);
            var minMerchants = Count(MK.MinMerchants, MK.MinReachableMerchants);

            // ---- unknown-node weights
            JObject unknownWeights = null;
            var w = config[MK.UnknownWeights];
            if (Js.Nullish(w))
            {
                Err(MK.UnknownWeights, MM.UnknownWeightsMissing);
            }
            else if (!(w is JObject weights))
            {
                Err(MK.UnknownWeights, string.Format(CultureInfo.InvariantCulture, MM.UnknownWeightsNotObject, TypeOf(w)));
            }
            else
            {
                var total = 0d;
                foreach (var p in weights.Properties()) total = total + (Js.IsNum(p.Value) ? Js.D(p.Value) : 0);
                if (total <= 0) Err(MK.UnknownWeights, MM.UnknownWeightsZero);
                unknownWeights = weights;
            }

            plan.ShrineFrom = shrineFrom;
            plan.EliteFrom = eliteFrom;
            plan.RestBeforeElite = restBeforeElite;
            plan.NoShrineOn = noShrineOn;
            plan.MinElites = minElites;
            plan.MinMerchants = minMerchants;
            plan.Minima.Add(new KeyValuePair<string, int>(rules.Elite, minElites));
            plan.Minima.Add(new KeyValuePair<string, int>(rules.Merchant, minMerchants));
            plan.UnknownWeights = unknownWeights;
            return plan;
        }

        /// <summary>minViableFloors(config): the shortest act (from the rules' shortest act up to the authored floors) whose rules resolve.</summary>
        public static int? MinViableFloors(MapRules rules, JObject config, out string error)
        {
            error = null;
            var floorsToken = config?[MK.Floors];
            var ceiling = Js.IsInt(floorsToken) ? (int)Js.D(floorsToken) : 0;
            for (var f = rules.ShortestAct; f <= ceiling; f++)
            {
                var probe = Js.Spread(config);
                probe[MK.Floors] = f;
                Resolve(rules, probe, out var errors);
                if (errors.Count == 0) return f;
            }
            error = string.Format(CultureInfo.InvariantCulture, MM.ShapeNoViableLength, rules.ShortestAct, ceiling);
            return null;
        }

        /// <summary>
        /// applyRunShape(config, shape, limits): caps floors/columns (min(authored, cap)) and re-weights typeWeights. A
        /// refusal returns the authored config unchanged with the errors naming each knob; a shape whose shortened act no
        /// longer resolves its own rules is refused too.
        /// </summary>
        public static RunShapeResult ApplyRunShape(MapRules rules, JObject config, JToken shape, JObject limits)
        {
            var result = new RunShapeResult { Config = config };
            var lim = limits ?? new JObject();
            void Err(string key, string msg) => result.Errors.Add(new MapError(key, msg));
            string ShapeKey(string key) => string.Format(CultureInfo.InvariantCulture, MV.MapShapeKnob, key);
            RunShapeResult Refuse()
            {
                result.Config = config;
                result.Changed = false;
                return result;
            }

            if (Js.Nullish(shape)) return result;
            if (!(shape is JObject s))
            {
                Err(MK.MapShape, string.Format(CultureInfo.InvariantCulture, MM.ShapeNotObject, shape is JArray ? MV.AnArray : TypeOf(shape)));
                return Refuse();
            }
            foreach (var p in s.Properties())
                if (!rules.RunShapeKeys.Contains(p.Name))
                    Err(ShapeKey(p.Name), string.Format(CultureInfo.InvariantCulture, MM.ShapeUnknownKnob, string.Join(MV.ListJoin, rules.RunShapeKeys)));

            JToken Cap(string key, int? min, string whyMin)
            {
                var authored = config[key];
                var v = s[key];
                if (Js.Nullish(v)) return authored;
                if (!Js.IsInt(v))
                {
                    Err(ShapeKey(key), string.Format(CultureInfo.InvariantCulture, MM.ShapeNotWhole, key, Describe(v)));
                    return authored;
                }
                if (min != null && Js.D(v) < min.Value)
                {
                    Err(ShapeKey(key), string.Format(CultureInfo.InvariantCulture, MM.ShapeBelowMin, Describe(v), min.Value, whyMin));
                    return authored;
                }
                if (Js.D(v) > Js.D(authored)) return authored;
                return v;
            }

            var viable = MinViableFloors(rules, config, out var viableError);
            if (viableError != null) Err(ShapeKey(MK.Floors), viableError);
            var floors = Cap(MK.Floors, viable, viable == null ? MM.ShapeFloorsNone
                : string.Format(CultureInfo.InvariantCulture, MM.ShapeFloorsWhy, viable.Value, viable.Value - 1));
            var minColumnsToken = lim[MK.MinColumns];
            int? minColumns = Js.Nullish(minColumnsToken) ? (int?)null : (int)Js.D(minColumnsToken);
            var columns = Cap(MK.Columns, minColumns, string.Format(CultureInfo.InvariantCulture, MM.ShapeColumnsWhy, Describe(minColumnsToken)));

            var authoredWeights = config[MK.TypeWeights];
            JToken typeWeights = authoredWeights;
            var weightsReplaced = false;
            var shapeWeights = s[MK.TypeWeights];
            if (!Js.Nullish(shapeWeights))
            {
                var weightsKey = ShapeKey(MK.TypeWeights);
                if (!(shapeWeights is JObject w))
                {
                    Err(weightsKey, string.Format(CultureInfo.InvariantCulture, MM.ShapeWeightsNotObject, shapeWeights is JArray ? MV.AnArray : TypeOf(shapeWeights)));
                }
                else if (!(authoredWeights is JObject || authoredWeights is JArray))
                {
                    Err(weightsKey, MM.ShapeNoWeights);
                }
                else
                {
                    var authoredObject = authoredWeights as JObject ?? new JObject();
                    var merged = Js.Spread(authoredObject);
                    foreach (var p in w.Properties())
                    {
                        var weightKey = string.Format(CultureInfo.InvariantCulture, MV.MapShapeWeight, p.Name);
                        if (!authoredObject.ContainsKey(p.Name))
                        {
                            Err(weightKey, string.Format(CultureInfo.InvariantCulture, MM.ShapeWeightUnknown, string.Join(MV.ListJoin, authoredObject.Properties().Select(q => q.Name))));
                            continue;
                        }
                        var v = p.Value;
                        if (!Js.IsFinite(v) || Js.D(v) < 0)
                        {
                            Err(weightKey, string.Format(CultureInfo.InvariantCulture, MM.ShapeWeightInvalid, Describe(v)));
                            continue;
                        }
                        var maxWeight = lim[MK.MaxWeight];
                        if (!Js.Nullish(maxWeight) && Js.D(v) > Js.D(maxWeight))
                        {
                            Err(weightKey, string.Format(CultureInfo.InvariantCulture, MM.ShapeWeightAboveMax, Describe(v), Describe(maxWeight)));
                            continue;
                        }
                        merged[p.Name] = v.DeepClone();
                    }
                    var total = 0d;
                    foreach (var p in merged.Properties()) total = total + Js.D(p.Value);
                    if (total <= 0)
                    {
                        Err(weightsKey, MM.ShapeWeightsZero);
                    }
                    else
                    {
                        typeWeights = merged;
                        weightsReplaced = true;
                        if (merged[rules.Monster] != null && Js.IsNum(merged[rules.Monster]) && Js.D(merged[rules.Monster]) == 0)
                            result.Notes.Add(new RunShapeNote(rules.Monster, 0));
                    }
                }
            }

            if (result.Errors.Count > 0) return Refuse();

            var next = Js.Spread(config);
            next[MK.Floors] = floors?.DeepClone();
            next[MK.Columns] = columns?.DeepClone();
            next[MK.TypeWeights] = typeWeights?.DeepClone();
            var changed = !SameNumber(floors, config[MK.Floors]) || !SameNumber(columns, config[MK.Columns]) || weightsReplaced;
            if (!changed) return Refuse();

            var plan = Resolve(rules, next, out var planErrors);
            foreach (var e in planErrors) Err(string.Format(CultureInfo.InvariantCulture, MV.MapShapePlan, e.Key), e.Message);
            if (result.Errors.Count > 0) return Refuse();

            foreach (var kv in plan.Minima)
            {
                var weight = (typeWeights as JObject)?[kv.Key];
                if (kv.Value > 0 && Js.IsNum(weight) && Js.D(weight) == 0) result.Notes.Add(new RunShapeNote(kv.Key, kv.Value));
            }
            result.Config = next;
            result.Changed = true;
            return result;
        }

        /// <summary>JS <c>x | 0</c> (ToInt32) for a JSON value: numbers truncate and wrap, anything else is 0.</summary>
        internal static int ToInt32(JToken t)
        {
            var d = Js.D(t);
            if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
            var m = Math.Truncate(d) % MapMath.TwoPow32;
            if (m < 0) m += MapMath.TwoPow32;
            return unchecked((int)(uint)m);
        }

        /// <summary>JSON.stringify for an error message (undefined prints as undefined).</summary>
        internal static string Describe(JToken t) => t == null ? V.Undefined : t.ToString(Newtonsoft.Json.Formatting.None);

        /// <summary><c>typeof t</c> for a JSON value.</summary>
        internal static string TypeOf(JToken t)
        {
            if (t == null) return V.Undefined;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return MV.TypeNumber;
                case JTokenType.String:
                    return MV.TypeString;
                case JTokenType.Boolean:
                    return MV.TypeBoolean;
                default:
                    return MV.TypeObject;
            }
        }

        private static bool SameNumber(JToken a, JToken b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (Js.IsNum(a) && Js.IsNum(b)) return Js.D(a) == Js.D(b);
            return JToken.DeepEquals(a, b);
        }
    }

    /// <summary>JS arithmetic the map rules need.</summary>
    public static class MapMath
    {
        /// <summary>2^32, for ToInt32.</summary>
        public static readonly double TwoPow32 = Math.Pow(CM.Two, Ashen.Generated.MapNumbers.Uint32Bits);

        /// <summary>JS Math.round: halves round up (toward +∞).</summary>
        public static double Round(double x) => Math.Floor(x + CM.Half);
    }
}
