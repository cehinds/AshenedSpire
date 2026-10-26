using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Map;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-14 (docs/design/11) Core 2 map parity: every act map Tools/oracle-core2-map.mjs recorded from the web's
    /// engine/actmap.js at the pinned sha (buildActMap under the configured routes module of the profile `default`) is rebuilt
    /// by Ashen.Domain.Map.CoreRoutes over Core2Registries and must match exactly: the nodes (types, fog display types, actual
    /// types, resolved Unknown nodes, edges, columns), their insertion order, start, shrine and boss ids, the boss and its
    /// label, the profile hash, and every RNG counter. Also held to the recorded answer: quest histories, 49 routes
    /// content and module variants with each refusal's message, the seat order draw and the refusals before any draw.
    /// Fixture JSON has sorted keys (D-165), so maps are compared in canonical form and the node order by its own list.
    /// Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2MapParityTests
    {
        private static string MapDir => Path.Combine(TestContent.OracleRoot, "core2", "map");

        private static JObject Oracle(string name)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(Path.Combine(MapDir, name)))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static Core2Registries _core2;
        private static MapData _data;

        private static Core2Registries Core2
        {
            get
            {
                if (_core2 != null) return _core2;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                return _core2 = Core2Registries.Load(snapshot.Content);
            }
        }

        private static MapData Data => _data ??= Core2.ToMapData(TestContent.ContentJson(ContentFiles.RulesMapEngine));

        public static IEnumerable<string> MapFiles() =>
            Directory.Exists(MapDir)
                ? Directory.GetFiles(MapDir, "maps-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        /// <summary>Recursively sorts object keys (the fixtures are canonical, D-165); array order is kept.</summary>
        private static JToken Canon(JToken t)
        {
            if (t is JObject o)
            {
                var sorted = new JObject();
                foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)) sorted[p.Name] = Canon(p.Value);
                return sorted;
            }
            if (t is JArray a) return new JArray(a.Select(Canon));
            return t.DeepClone();
        }

        private static string Plain(JToken t) => Canon(t).ToString(Formatting.None);

        private static Dictionary<string, long> Counters(JToken recorded) =>
            ((JObject)recorded).Properties().ToDictionary(p => p.Name, p => p.Value.Value<long>());

        /// <summary>
        /// The counters after a call, over every stream the web records (24). Rng carries the 14 core streams; the other ten
        /// (the Core 2 split, D-164) are not drawn by the map, so the recording must hold them at 0 and they are listed as 0.
        /// </summary>
        private static Dictionary<string, long> Counters(Rng rng, JToken recorded)
        {
            var counters = rng.Counters().ToDictionary(kv => RngStreamNames.ToWire(kv.Key), kv => (long)kv.Value);
            foreach (var name in Counters(recorded).Where(kv => !counters.ContainsKey(kv.Key)))
            {
                Assert.That(name.Value, Is.EqualTo(0), "the map must not draw on the Core 2 stream " + name.Key);
                counters[name.Key] = 0;
            }
            return counters;
        }

        /// <summary>Replays one recorded buildActMap call and compares the map (or the refusal) and the counters. Returns 1 for a map, 0 for a refusal.</summary>
        private static int CheckCase(CoreRoutesConfig config, JObject c, string where, JArray history = null)
        {
            var rng = new Rng((uint)c["seed"].Value<long>());
            ActMapGraph graph;
            try
            {
                graph = CoreRoutes.BuildActMap(Data, config, rng, (string)c["seat"], c["tier"].Value<int>(), history ?? new JArray());
            }
            catch (Exception e) when (!(e is AssertionException))
            {
                if (c["error"] == null) Assert.Fail($"{where}: unexpected {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                if (e is InvalidOperationException) Assert.That(e.Message, Is.EqualTo((string)c["error"]), where + ": refusal");
                Assert.That(Counters(rng, c["rng"]), Is.EqualTo(Counters(c["rng"])), where + ": counters after the refusal");
                return 0;
            }
            if (c["error"] != null) Assert.Fail($"{where}: the web refused ({c["error"]}) but the port built a map");
            Assert.That(Plain(graph.ToJson()), Is.EqualTo(Plain(c["map"])), where + ": map");
            Assert.That(graph.Nodes.Keys, Is.EqualTo(((JArray)c["nodeOrder"]).Select(t => (string)t)), where + ": node insertion order");
            Assert.That(Counters(rng, c["rng"]), Is.EqualTo(Counters(c["rng"])), where + ": RNG counters");
            return 1;
        }

        private static string Where(JObject c, string file) => $"{file} seat={c["seat"]} tier={c["tier"]} seed={c["seed"]}";

        private static CoreRoutesConfig Shipped => Core2.RoutesConfig;

        [Test]
        public void TheOracleIsPresentAndCoversEverySeatAndTier()
        {
            var index = Oracle("index.json");
            var seats = ((JArray)index["seats"]).Select(t => (string)t).ToList();
            var tiers = ((JArray)index["tiers"]).Select(t => t.Value<int>()).ToList();
            Assert.That(seats, Is.EqualTo(Data.Seats.All.Select(s => (string)s["id"])));
            Assert.That(tiers, Is.EqualTo(Enumerable.Range(1, (int)Core2.Routes["acts"])));
            Assert.That(MapFiles().Count(), Is.EqualTo(seats.Count * tiers.Count));
            Assert.That(((JArray)index["seeds"]).Count, Is.GreaterThanOrEqualTo(40));
            Assert.That(index["gatedHits"].Value<int>(), Is.GreaterThan(0), "the history cases must actually roll a gated event");
            Assert.That(index["fogHits"].Value<int>(), Is.GreaterThan(0), "the fog must actually hide nodes");
            Assert.That(index["profileHash"].Value<string>(), Is.EqualTo(Core2.Profile.Hash));
        }

        [TestCaseSource(nameof(MapFiles))]
        public void EveryRecordedMapMatchesTheWebBuild(string file)
        {
            var cases = (JArray)Oracle(file)["cases"];
            Assert.That(cases.Count, Is.GreaterThanOrEqualTo(40));
            var built = cases.Cast<JObject>().Sum(c => CheckCase(Shipped, c, Where(c, file)));
            Assert.That(built, Is.EqualTo(cases.Count));
        }

        [Test]
        public void QuestHistoriesGateTheUnknownNodesAsInTheWeb()
        {
            var doc = Oracle("history.json");
            var histories = (JArray)doc["histories"];
            var cases = ((JArray)doc["cases"]).Cast<JObject>().ToList();
            Assert.That(cases.Count, Is.GreaterThan(200));
            foreach (var c in cases) CheckCase(Shipped, c, Where(c, "history") + " history=" + c["history"], (JArray)histories[c["history"].Value<int>()]);
        }

        [Test]
        public void EveryRoutesVariantBuildsOrIsRefusedAsInTheWeb()
        {
            var doc = Oracle("variants.json");
            var variants = ((JArray)doc["variants"]).Cast<JObject>().ToList();
            var cases = ((JArray)doc["cases"]).Cast<JObject>().ToList();
            Assert.That(variants.Count, Is.GreaterThanOrEqualTo(40));
            var built = 0;
            var refused = 0;
            foreach (var c in cases)
            {
                var variant = variants[c["variant"].Value<int>()];
                var config = new CoreRoutesConfig((JObject)variant["routes"], (JObject)variant["module"], Core2.Profile.Hash);
                var n = CheckCase(config, c, Where(c, "variants") + " variant=" + variant["label"]);
                built += n;
                refused += 1 - n;
            }
            Assert.That(built, Is.GreaterThan(100));
            Assert.That(refused, Is.GreaterThan(100), "the refusals must be exercised");
        }

        [Test]
        public void TheFogShowsHiddenTypesAsEventsAndKeepsTheActualType()
        {
            var routes = Core2.Routes as JObject;
            var hidden = ((JArray)routes["hiddenTypes"]).Select(t => (string)t).ToHashSet();
            var firstFloor = (string)routes["firstFloor"];
            var vocab = Data.Rules.Routes;
            var fogged = 0;
            foreach (var seat in Data.Seats.All.Select(s => (string)s["id"]))
                for (var tier = 1; tier <= (int)routes["acts"]; tier++)
                    for (uint seed = 1; seed <= 20; seed++)
                    {
                        var graph = CoreRoutes.BuildActMap(Data, Shipped, new Rng(seed), seat, tier);
                        foreach (var node in graph.AllNodes())
                        {
                            var forced = node.Floor == 1;
                            var expected = hidden.Contains(node.ActualType) && !forced ? Data.Rules.Event : vocab.DisplayType(node.ActualType);
                            Assert.That(node.Type, Is.EqualTo(expected), $"{seat}/{tier}/{seed} {node.Id}");
                            if (node.Type == Data.Rules.Event && node.ActualType != vocab.Event) fogged++;
                            if (forced) Assert.That(node.ActualType, Is.EqualTo(firstFloor), "the opening depth is the authored firstFloor");
                            if (node.Type == Data.Rules.Event) Assert.That(node.Resolved, Is.Not.Null, node.Id);
                        }
                        Assert.That(graph.GameProfileHash, Is.EqualTo(Core2.Profile.Hash), "the profile hash is stamped verbatim");
                    }
            Assert.That(fogged, Is.GreaterThan(0));
        }

        [Test]
        public void TheSeatOrderDrawMatchesTheWeb()
        {
            foreach (var c in ((JArray)Oracle("seats.json")["cases"]).Cast<JObject>())
            {
                var rng = new Rng((uint)c["seed"].Value<long>());
                Assert.That(ActMap.DrawSeatOrder(Data, rng), Is.EqualTo(((JArray)c["order"]).Select(t => (string)t)), "seed " + c["seed"]);
                Assert.That(Counters(rng, c["rng"]), Is.EqualTo(Counters(c["rng"])), "seed " + c["seed"] + " counters");
                foreach (var pinned in ((JArray)c["pinned"]).Cast<JObject>())
                    Assert.That(ActMap.DrawSeatOrder(Data, new Rng((uint)c["seed"].Value<long>()), (string)pinned["firstSeat"]),
                        Is.EqualTo(((JArray)pinned["order"]).Select(t => (string)t)), $"seed {c["seed"]} first seat {pinned["firstSeat"]}");
            }
        }

        [Test]
        public void ARefusedSeatOrTierDrawsNothing()
        {
            var cases = ((JArray)Oracle("refusals.json")["cases"]).Cast<JObject>().ToList();
            Assert.That(cases.Count, Is.GreaterThanOrEqualTo(4));
            foreach (var c in cases)
            {
                var rng = new Rng((uint)c["seed"].Value<long>());
                Assert.Throws(Is.InstanceOf<Exception>(), () => CoreRoutes.BuildActMap(Data, Shipped, rng, (string)c["seat"], c["tier"].Value<int>()), Where(c, "refusals"));
                Assert.That(Counters(rng, c["rng"]), Is.EqualTo(Counters(c["rng"])), Where(c, "refusals") + " counters");
                Assert.That(c["error"], Is.Not.Null);
            }
        }

        [Test]
        public void TheCore2MapPathLeavesTheLegacyMapAlone()
        {
            // The 405 act map keeps its own rules and its own parity suite; the Core 2 builder only adds optional fields to the shared graph types.
            var plain = new MapNode("n1_0", 1, 0);
            Assert.That(plain.ToJson().ContainsKey(MapKeys.ActualType), Is.False);
            Assert.That(new ActMapGraph().ToJson().ContainsKey(MapKeys.GameProfileHash), Is.False);
        }
    }
}
