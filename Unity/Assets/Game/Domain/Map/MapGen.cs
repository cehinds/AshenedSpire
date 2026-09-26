using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CM = Ashen.Generated.CombatMath;
using MK = Ashen.Generated.MapKeys;
using MM = Ashen.Generated.MapMessages;

namespace Ashen.Domain.Map
{
    /// <summary>One boss terminal to seat on the map (actmap's selection).</summary>
    public readonly struct BossDestination
    {
        public BossDestination(string encounterId, string label)
        {
            EncounterId = encounterId;
            Label = label;
        }

        public string EncounterId { get; }

        public string Label { get; }
    }

    /// <summary>sampleActShape's answer: node-count statistics and mean nodes per act by type (in first-seen order).</summary>
    public sealed class ActShapeSample
    {
        public int Seeds { get; internal set; }
        public double Mean { get; internal set; }
        public int Min { get; internal set; }
        public int Max { get; internal set; }
        public List<KeyValuePair<string, double>> ByType { get; } = new List<KeyValuePair<string, double>>();
    }

    /// <summary>
    /// The procedural act-map generator (shipped engine/mapgen.js, SPEC §3.8, §6), line for line: walk the paths bottom
    /// to top (edges merge, never cross), type the nodes under the floor plan with bounded retries, relax, keep the
    /// rest-before-Elite promise, then seat the boss destinations. Randomness only from the 'map' stream, in the
    /// shipped draw order.
    /// </summary>
    public static class MapGen
    {
        /// <summary>generateActMap({ config, rng }).</summary>
        public static ActMapGraph GenerateActMap(MapRules rules, JObject config, Rng rng)
        {
            var floors = (int)config.Num(MK.Floors);
            var cols = (int)config.Num(MK.Columns);
            var plan = FloorPlans.Resolve(rules, config, out var errors);
            if (plan == null || errors.Count > 0)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.FloorRulesDoNotResolve,
                    errors.Count > 0 ? MapError.Join(errors) : MM.FloorRulesMissingShort));

            // ---- 1. Path walk (floors 1..floors-1); floor `floors` is the lone shrine.
            var pathFloors = floors - 1;
            var edges = new List<OrderedSet<int, OrderedSet<int, bool>>>();
            for (var i = 0; i < Math.Max(0, pathFloors); i++) edges.Add(new OrderedSet<int, OrderedSet<int, bool>>());
            var usedCols = new List<OrderedSet<int, bool>>();
            for (var i = 0; i < Math.Max(0, pathFloors + 1); i++) usedCols.Add(new OrderedSet<int, bool>());

            var entriesToken = config[MK.Entries];
            int? entries = Js.IsInt(entriesToken) ? (int)Js.D(entriesToken) : (int?)null;
            var starts = new List<int>();
            var pathCount = config.Num(MK.PathCount);
            for (var p = 0; p < pathCount; p++)
            {
                var col = rng.Int(RngStream.Map, 0, cols - 1);
                if (entries == null ? p == 1 : p < entries.Value)
                {
                    var guard = 0;
                    while (starts.Contains(col) && ++guard < rules.DoorGuard) col = rng.Int(RngStream.Map, 0, cols - 1);
                }
                else if (entries != null)
                {
                    col = starts[rng.Int(RngStream.Map, 0, entries.Value - 1)];
                }
                starts.Add(col);
                usedCols[1].Add(col);

                for (var floor = 1; floor < pathFloors; floor++)
                {
                    var next = Clamp(col + rng.Int(RngStream.Map, rules.StepMin, rules.StepMax), 0, cols - 1);
                    // No-cross rule (StS): an existing edge from this floor that would cross ours lends its destination.
                    foreach (var fromCol in edges[floor].Keys)
                        foreach (var toCol in edges[floor][fromCol].Keys)
                        {
                            var crosses = (fromCol < col && toCol > next) || (fromCol > col && toCol < next);
                            if (crosses) next = toCol;
                        }
                    if (!edges[floor].Contains(col)) edges[floor][col] = new OrderedSet<int, bool>();
                    edges[floor][col].Add(next);
                    col = next;
                    usedCols[floor + 1].Add(col);
                }
            }

            // ---- 2. Materialize nodes + wire edges.
            var g = new ActMapGraph();
            MapNode AddNode(int floor, int col)
            {
                var id = rules.NodeId(floor, col);
                if (!g.Nodes.TryGetValue(id, out var node))
                {
                    node = new MapNode(id, floor, col);
                    g.Nodes[id] = node;
                }
                return node;
            }
            for (var floor = 1; floor <= pathFloors; floor++)
                foreach (var col in usedCols[floor].Keys) AddNode(floor, col);
            for (var floor = 1; floor < pathFloors; floor++)
                foreach (var fromCol in edges[floor].Keys)
                {
                    var from = g.Nodes[rules.NodeId(floor, fromCol)];
                    foreach (var toCol in edges[floor][fromCol].Keys)
                    {
                        var toId = rules.NodeId(floor + 1, toCol);
                        if (!from.Next.Contains(toId)) from.Next.Add(toId);
                    }
                }

            var middle = (int)Math.Floor(cols / CM.Two);
            var shrine = AddNode(floors, middle);
            shrine.Type = rules.Shrine;
            if (pathFloors >= 0)
                foreach (var col in usedCols[pathFloors].Keys) g.Nodes[rules.NodeId(pathFloors, col)].Next.Add(shrine.Id);
            var boss = AddNode(floors + 1, middle);
            boss.Type = rules.Boss;
            shrine.Next.Add(boss.Id);

            // ---- 3. Node typing under constraints, bounded retries then relax.
            var rollable = g.AllNodes().Where(n => n.Type == null).ToList();
            var weights = config.Obj(MK.TypeWeights) ?? new JObject();
            for (var attempt = 0; attempt < rules.TypingRetries; attempt++)
            {
                foreach (var node in rollable) node.Type = null;
                TypeOnce(rules, g, rollable, plan, weights, rng);
                if (CountType(g, rules.Elite) >= plan.MinElites && CountType(g, rules.Merchant) >= plan.MinMerchants)
                {
                    EnsureRestBeforeElite(rules, g, plan, rng);
                    return Finish(rules, g, starts, shrine.Id, boss.Id, floors, cols);
                }
            }
            RelaxPlace(rules, g, rules.Elite, plan.MinElites, plan, rng);
            RelaxPlace(rules, g, rules.Merchant, plan.MinMerchants, plan, rng);
            EnsureRestBeforeElite(rules, g, plan, rng);
            return Finish(rules, g, starts, shrine.Id, boss.Id, floors, cols);
        }

        /// <summary>typeOnce: roll floor by floor (stable ascending sort) so the adjacency rule sees typed parents.</summary>
        public static void TypeOnce(MapRules rules, ActMapGraph g, List<MapNode> rollable, FloorPlan plan, JObject weights, Rng rng)
        {
            var byFloor = rollable.OrderBy(n => n.Floor).ToList();
            var lowestShrine = double.PositiveInfinity;
            var all = g.AllNodes();
            foreach (var node in byFloor)
            {
                var fixedType = plan.FixedAt(node.Floor);
                if (fixedType != null)
                {
                    node.Type = fixedType;
                }
                else
                {
                    var parents = all.Where(p => p.Next.Contains(node.Id) && !string.IsNullOrEmpty(p.Type)).ToList();
                    node.Type = RollType(rules, node, parents, plan, weights, rng, lowestShrine < node.Floor);
                }
                if (node.Type == rules.Shrine && node.Floor < lowestShrine) lowestShrine = node.Floor;
            }
        }

        /// <summary>rollType: weighted roll with per-node hard filters (floor rules, no same non-monster type along an edge).</summary>
        public static string RollType(MapRules rules, MapNode node, List<MapNode> typedParents, FloorPlan plan, JObject weights, Rng rng, bool restBelow)
        {
            var banned = new HashSet<string>(typedParents.Select(p => p.Type).Where(t => t != rules.Monster), StringComparer.Ordinal);
            var entries = new List<KeyValuePair<string, double>>();
            foreach (var prop in weights.Properties())
            {
                var type = prop.Name;
                var w = Js.D(prop.Value);
                if (w <= 0) continue;
                if (banned.Contains(type)) continue;
                if (type == rules.Elite && node.Floor < plan.EliteFrom) continue;
                if (type == rules.Shrine && node.Floor < plan.ShrineFrom) continue;
                if (type == rules.Shrine && node.Floor == plan.NoShrineOn) continue;
                if (type == rules.Elite && plan.RestBeforeElite && !restBelow) continue;
                entries.Add(new KeyValuePair<string, double>(type, w));
            }
            if (entries.Count == 0) return rules.Monster;
            var total = 0d;
            foreach (var e in entries) total = total + e.Value;
            var r = rng.Float(RngStream.Map) * total;
            foreach (var e in entries)
            {
                r -= e.Value;
                if (r < 0) return e.Key;
            }
            return rules.Monster;
        }

        /// <summary>relaxPlace: force-place what the rolls never produced (opening the promised rest first for Elites).</summary>
        public static void RelaxPlace(MapRules rules, ActMapGraph g, string type, int min, FloorPlan plan, Rng rng)
        {
            var have = CountType(g, type);
            if (have >= min) return;
            if (type == rules.Elite && plan.RestBeforeElite)
            {
                var lowestShrine = LowestFloorOf(g, rules.Shrine);
                if (!(lowestShrine < plan.EliteFrom))
                {
                    var onRestFloors = g.AllNodes().Where(n => plan.RestFloors.Contains(n.Floor)).ToList();
                    var monsters = onRestFloors.Where(n => n.Type == rules.Monster).ToList();
                    var pool = monsters.Count > 0 ? monsters : onRestFloors;
                    if (pool.Count > 0) pool[rng.Below(RngStream.Map, pool.Count)].Type = rules.Shrine;
                }
            }
            var restFloor = plan.RestBeforeElite ? LowestFloorOf(g, rules.Shrine) : double.NegativeInfinity;
            var eligible = g.AllNodes().Where(n => n.Type == rules.Monster
                && !(type == rules.Elite && n.Floor < plan.EliteFrom)
                && !(type == rules.Elite && plan.RestBeforeElite && n.Floor <= restFloor)).ToList();
            while (have < min && eligible.Count > 0)
            {
                var idx = rng.Below(RngStream.Map, eligible.Count);
                var picked = eligible[idx];
                eligible.RemoveAt(idx);
                picked.Type = type;
                have++;
            }
        }

        /// <summary>ensureRestBeforeElite: the promise, kept on every exit path, without paying for it out of another promise.</summary>
        public static void EnsureRestBeforeElite(MapRules rules, ActMapGraph g, FloorPlan plan, Rng rng)
        {
            if (!plan.RestBeforeElite) return;
            var firstElite = LowestFloorOf(g, rules.Elite);
            if (double.IsInfinity(firstElite)) return;
            if (LowestFloorOf(g, rules.Shrine) < firstElite) return;
            var below = g.AllNodes().Where(n => n.Floor < firstElite && plan.RestFloors.Contains(n.Floor)).ToList();
            if (below.Count == 0) return;

            var counts = Tally(g);
            bool Spare(string t) => !(plan.MinimumOf(t) > 0) || CountOf(counts, t) > plan.MinimumOf(t);
            var monsters = below.Where(n => n.Type == rules.Monster).ToList();
            var spares = below.Where(n => Spare(n.Type)).ToList();
            var pool = monsters.Count > 0 ? monsters : (spares.Count > 0 ? spares : below);
            pool[rng.Below(RngStream.Map, pool.Count)].Type = rules.Shrine;

            foreach (var kv in plan.Minima)
            {
                var type = kv.Key;
                var min = kv.Value;
                if (!(min > 0)) continue;
                while (CountType(g, type) < min)
                {
                    RelaxPlace(rules, g, type, min, plan, rng);
                    if (CountType(g, type) >= min) break;
                    var held = Tally(g);
                    bool ShrineSpare(MapNode n)
                    {
                        if (n.Floor == plan.Floors) return false;
                        if (double.IsInfinity(firstElite) || n.Floor >= firstElite) return true;
                        return g.AllNodes().Any(o => o != n && o.Type == rules.Shrine && o.Floor < firstElite);
                    }
                    var donor = g.AllNodes().FirstOrDefault(n =>
                        n.Type != type && n.Type != rules.Boss &&
                        (n.Type != rules.Shrine || ShrineSpare(n)) &&
                        plan.FixedAt(n.Floor) == null &&
                        (!(plan.MinimumOf(n.Type) > 0) || CountOf(held, n.Type) > plan.MinimumOf(n.Type)));
                    if (donor == null) break;
                    donor.Type = type;
                }
            }
        }

        /// <summary>finish: the unique start ids (floor 1, first-seen order) and the geometry that travels with the graph.</summary>
        public static ActMapGraph Finish(MapRules rules, ActMapGraph g, List<int> startCols, string shrineId, string bossId, int floors, int columns)
        {
            g.StartIds = startCols.Distinct().Select(col => rules.NodeId(1, col)).ToList();
            g.ShrineId = shrineId;
            g.BossId = bossId;
            g.Floors = floors;
            g.Columns = columns;
            return g;
        }

        /// <summary>assignBossDestinations: replace only the terminal row (no RNG); terminals sit adjacent around the shrine.</summary>
        public static ActMapGraph AssignBossDestinations(MapRules rules, ActMapGraph map, IReadOnlyList<BossDestination> destinations)
        {
            if (destinations == null || destinations.Count == 0 || destinations.Count > map.Columns) throw new InvalidOperationException(MM.DestinationsDoNotFit);
            if (destinations.Select(d => d.EncounterId).Distinct().Count() != destinations.Count) throw new InvalidOperationException(MM.DestinationsNotUnique);
            var shrine = map.Node(map.ShrineId);
            var original = map.Node(map.BossId);
            if (shrine == null || shrine.Type != rules.Shrine || original == null || original.Type != rules.Boss) throw new InvalidOperationException(MM.DestinationsNeedRest);
            map.Nodes.Remove(map.BossId);
            var firstColumn = (int)Math.Floor((map.Columns - destinations.Count) / CM.Two);
            var terminals = new List<string>();
            for (var index = 0; index < destinations.Count; index++)
            {
                var destination = destinations[index];
                var col = destinations.Count == 1 ? original.Col : firstColumn + index;
                var id = rules.NodeId(original.Floor, col);
                map.Nodes[id] = new MapNode(id, original.Floor, col)
                {
                    Type = original.Type,
                    Next = new List<string>(),
                    Resolved = original.Resolved,
                    EncounterId = destination.EncounterId,
                    DestinationLabel = destination.Label,
                };
                terminals.Add(id);
            }
            shrine.Next = new List<string>(terminals);
            map.BossIds = terminals;
            map.BossId = terminals[0];
            return map;
        }

        /// <summary>
        /// sampleActShape(config, seeds): node-count mean/min/max over <c>sweepSeed(0..seeds-1)</c> and mean nodes per
        /// act by type, each rounded to hundredths with the JS Math.round.
        /// </summary>
        public static ActShapeSample SampleActShape(MapRules rules, JObject config, int seeds)
        {
            var counts = new List<int>();
            var byType = new OrderedMap<int>();
            for (var i = 0; i < seeds; i++)
            {
                var g = GenerateActMap(rules, config, new Rng(SweepSeed(i)));
                var all = g.AllNodes();
                counts.Add(all.Count);
                foreach (var n in all) byType[n.Type] = (byType.TryGetValue(n.Type, out var c) ? c : 0) + 1;
            }
            var sum = 0d;
            foreach (var c in counts) sum = sum + c;
            var sample = new ActShapeSample
            {
                Seeds = seeds,
                Mean = MapMath.Round((sum / counts.Count) * CM.Percent) / CM.Percent,
                Min = counts.Min(),
                Max = counts.Max(),
            };
            foreach (var e in byType.Entries()) sample.ByType.Add(new KeyValuePair<string, double>(e.Key, MapMath.Round(((double)e.Value / seeds) * CM.Percent) / CM.Percent));
            return sample;
        }

        /// <summary>sweepSeed(i) (engine/rng.js): Knuth's multiplicative hash on (i + 1).</summary>
        public static uint SweepSeed(int i) => unchecked((uint)(i + 1) * MapNumbers.SweepMultiplier);

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        private static double LowestFloorOf(ActMapGraph g, string type)
        {
            var lowest = double.PositiveInfinity;
            foreach (var n in g.AllNodes())
                if (n.Type == type && n.Floor < lowest) lowest = n.Floor;
            return lowest;
        }

        private static int CountType(ActMapGraph g, string type) => g.AllNodes().Count(n => n.Type == type);

        private static Dictionary<string, int> Tally(ActMapGraph g)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in g.AllNodes())
            {
                var key = n.Type ?? string.Empty;
                counts[key] = (counts.TryGetValue(key, out var c) ? c : 0) + 1;
            }
            return counts;
        }

        private static int CountOf(Dictionary<string, int> counts, string type) => counts.TryGetValue(type ?? string.Empty, out var c) ? c : 0;
    }

    /// <summary>An insertion-ordered keyed set (the shipped Map/Set iteration order) over value-type keys.</summary>
    internal sealed class OrderedSet<TKey, TValue>
    {
        private readonly List<TKey> _keys = new List<TKey>();
        private readonly Dictionary<TKey, TValue> _values = new Dictionary<TKey, TValue>();

        public IEnumerable<TKey> Keys => _keys.ToArray();

        public bool Contains(TKey key) => _values.ContainsKey(key);

        public void Add(TKey key)
        {
            if (_values.ContainsKey(key)) return;
            _keys.Add(key);
            _values[key] = default;
        }

        public TValue this[TKey key]
        {
            get => _values[key];
            set
            {
                if (!_values.ContainsKey(key)) _keys.Add(key);
                _values[key] = value;
            }
        }
    }
}
