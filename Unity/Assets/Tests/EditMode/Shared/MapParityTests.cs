using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// US-4.1 to 4.4 map parity (D-033 part 2): every act map Tools/oracle-map.mjs recorded from the SHIPPED
    /// engine/actmap.js buildActMap — every seat × every tier × 20 seeds, quest-gated histories and Custom Climb run
    /// shapes — is rebuilt by Ashen.Domain.Map and must match exactly: nodes and their insertion order, types, edge
    /// order, start ids, shrine and boss ids, resolved Unknown nodes and boss destinations, plus every RNG counter.
    /// The floor-plan resolver, run-shape refusals, sampleActShape, rollEncounter, the Unknown-node roll, the seat
    /// order and legacy boss lookup are held to their recorded answers too, and a content-built MapData must produce
    /// the same maps.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class MapParityTests
    {
        private static string MapDir => Path.Combine(TestContent.OracleRoot, "map");

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static JObject Oracle(string name) => ReadJson(Path.Combine(MapDir, name));

        private static MapData _data;
        private static MapData _content;

        private static MapData Data => _data ??= MapData.FromTableDump(Oracle("tables.json"), TestContent.ContentJson("rules/mapEngine.json"));

        private static MapData ContentData
        {
            get
            {
                if (_content != null) return _content;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var registries = RuntimeRegistries.Build(snapshot.Content, (JObject)snapshot.Content.Get(ContentFiles.StringsEn));
                return _content = registries.ToMapData((JObject)snapshot.Content.Get(ContentFiles.RulesMapEngine));
            }
        }

        public static IEnumerable<string> MapFiles() =>
            Directory.Exists(MapDir)
                ? Directory.GetFiles(MapDir, "maps-*.json").Where(f => f.EndsWith(".json", StringComparison.Ordinal)).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        private static string Plain(JToken t) => t.ToString(Formatting.None);

        private static Dictionary<string, long> Counters(Rng rng) =>
            rng.Counters().ToDictionary(kv => RngStreamNames.ToWire(kv.Key), kv => (long)kv.Value);

        private static Dictionary<string, long> Counters(JToken recorded) =>
            ((JObject)recorded).Properties().ToDictionary(p => p.Name, p => p.Value.Value<long>());

        /// <summary>Replays one recorded buildActMap call and compares the map (or the refusal) and the counters.</summary>
        private static int CheckCase(MapData data, JObject c, string where, JArray history = null)
        {
            var rng = new Rng((uint)c["seed"].Value<long>());
            var seat = (string)c["seat"];
            var tier = c["tier"].Value<int>();
            var shape = c["shape"];
            string got;
            try
            {
                got = Plain(ActMap.BuildActMap(data, rng, seat, tier, shape, history ?? new JArray()).ToJson());
            }
            catch (Exception e) when (!(e is AssertionException))
            {
                if (c["error"] == null) Assert.Fail($"{where}: unexpected {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                Assert.That(Counters(rng), Is.EqualTo(Counters(c["rng"])), where + ": counters after the refusal");
                return 0;
            }
            if (c["error"] != null) Assert.Fail($"{where}: the shipped engine refused ({c["error"]}) but the port built a map");
            Assert.That(got, Is.EqualTo(Plain(c["map"])), where + ": map");
            Assert.That(Counters(rng), Is.EqualTo(Counters(c["rng"])), where + ": RNG counters");
            return 1;
        }

        private static string Where(JObject c, string file) => $"{file} seat={c["seat"]} tier={c["tier"]} seed={c["seed"]}";

        [Test]
        public void TheOracleIsPresentAndCoversEverySeatAndTier()
        {
            var index = Oracle("index.json");
            var seats = ((JArray)index["seats"]).Select(t => (string)t).ToList();
            var tiers = ((JArray)index["tiers"]).Select(t => t.Value<int>()).ToList();
            Assert.That(seats, Is.EqualTo(Data.Seats.All.Select(s => (string)s["id"])));
            Assert.That(MapFiles().Count(), Is.EqualTo(seats.Count * tiers.Count));
            Assert.That(((JArray)index["seeds"]).Count, Is.GreaterThanOrEqualTo(20));
            Assert.That(index["gatedHits"].Value<int>(), Is.GreaterThan(0), "the history cases must actually roll a gated event");
        }

        [TestCaseSource(nameof(MapFiles))]
        public void EveryRecordedMapMatchesTheShippedBuild(string file)
        {
            var cases = (JArray)Oracle(file)["cases"];
            Assert.That(cases.Count, Is.GreaterThanOrEqualTo(20));
            var built = cases.Cast<JObject>().Sum(c => CheckCase(Data, c, Where(c, file)));
            Assert.That(built, Is.EqualTo(cases.Count));
        }

        [TestCaseSource(nameof(MapFiles))]
        public void ContentBuiltDataProducesTheSameMaps(string file)
        {
            foreach (var c in ((JArray)Oracle(file)["cases"]).Cast<JObject>()) CheckCase(ContentData, c, "content " + Where(c, file));
        }

        [Test]
        public void QuestHistoriesGateTheUnknownNodesAsShipped()
        {
            var doc = Oracle("history.json");
            var histories = (JArray)doc["histories"];
            var cases = ((JArray)doc["cases"]).Cast<JObject>().ToList();
            Assert.That(cases.Count, Is.GreaterThan(200));
            foreach (var c in cases) CheckCase(Data, c, Where(c, "history") + " history=" + c["history"], (JArray)histories[c["history"].Value<int>()]);
        }

        [Test]
        public void RunShapesResolveAndBuildAsShipped()
        {
            var doc = Oracle("shapes.json");
            var limits = (JObject)doc["limits"];
            var built = 0;
            foreach (var c in ((JArray)doc["cases"]).Cast<JObject>())
            {
                var shape = c["shape"];
                foreach (var t in ((JArray)c["perTier"]).Cast<JObject>())
                {
                    var where = $"shape {c["index"]} {Plain(shape)} tier {t["tier"]}";
                    var r = FloorPlans.ApplyRunShape(Data.Rules, Data.MapConfig(t["tier"].Value<int>()), shape, limits);
                    Assert.That(Plain(r.Config), Is.EqualTo(Plain(t["config"])), where + ": config");
                    Assert.That(r.Changed, Is.EqualTo(t["changed"].Value<bool>()), where + ": changed");
                    Assert.That(r.Errors.Select(e => e.Key), Is.EqualTo(((JArray)t["errors"]).Select(k => (string)k)), where + ": refusal keys");
                    Assert.That(r.Notes.Select(NoteText), Is.EqualTo(((JArray)t["notes"]).Select(k => (string)k)), where + ": notes");
                }
                foreach (var m in ((JArray)c["maps"]).Cast<JObject>())
                {
                    m["shape"] = shape?.DeepClone();
                    built += CheckCase(Data, m, $"shape {c["index"]} " + Where(m, "shapes"));
                }
            }
            Assert.That(built, Is.GreaterThan(300));
        }

        [Test]
        public void GenerateActMapMatchesOverAuthoredConfigs()
        {
            var cases = ((JArray)Oracle("configs.json")["cases"]).Cast<JObject>().ToList();
            Assert.That(cases.Count, Is.GreaterThan(50));
            foreach (var c in cases)
                foreach (var m in ((JArray)c["maps"]).Cast<JObject>())
                {
                    var rng = new Rng((uint)m["seed"].Value<long>());
                    var where = $"config {Plain(c["config"])} seed={m["seed"]}";
                    Assert.That(Plain(MapGen.GenerateActMap(Data.Rules, (JObject)c["config"], rng).ToJson()), Is.EqualTo(Plain(m["map"])), where + ": map");
                    Assert.That(Counters(rng), Is.EqualTo(Counters(m["rng"])), where + ": RNG counters");
                }
        }

        /// <summary>The shipped readout wording of a run-shape note (the screen's words; the port keeps notes structured).</summary>
        private static string NoteText(RunShapeNote n) => n.Count == 0
            ? "Monster 0 — a node whose every other type is barred by a floor rule or the no-repeat-neighbour ban still falls back to Monster, so Monsters do not reach zero."
            : $"{char.ToUpperInvariant(n.Type[0])}{n.Type.Substring(1)} 0 — but this act promises at least {n.Count} a map, so {n.Count} are force-placed. Zero weight means never ROLLED, not never present.";

        [Test]
        public void FloorPlansResolveAsShipped()
        {
            var doc = Oracle("plans.json");
            foreach (var c in ((JArray)doc["cases"]).Cast<JObject>())
            {
                var config = (JObject)c["config"];
                var plan = FloorPlans.Resolve(Data.Rules, config, out var errors);
                var where = "plan " + Plain(config);
                Assert.That(errors.Select(e => e.Key), Is.EqualTo(((JArray)c["errors"]).Select(k => (string)k)), where + ": refusal keys");
                if (c["plan"].Type == JTokenType.Null) Assert.That(plan, Is.Null, where);
                else Assert.That(Plain(plan.ToJson()), Is.EqualTo(Plain(c["plan"])), where + ": plan");
            }
            var viable = FloorPlans.MinViableFloors(Data.Rules, Data.MapConfig(1), out var error);
            Assert.That(error, Is.Null);
            Assert.That(viable, Is.EqualTo(doc["minViableFloors"]["floors"].Value<int>()));
        }

        [Test]
        public void SampleActShapeMatches()
        {
            foreach (var s in ((JArray)Oracle("extras.json")["samples"]).Cast<JObject>())
            {
                var config = s["shape"] == null
                    ? Data.MapConfig(s["config"].Value<int>())
                    : FloorPlans.ApplyRunShape(Data.Rules, Data.MapConfig(1), s["shape"], Data.MapShapeLimits).Config;
                var got = MapGen.SampleActShape(Data.Rules, config, s["seeds"].Value<int>());
                var want = (JObject)s["result"];
                Assert.That(got.Seeds, Is.EqualTo(want["seeds"].Value<int>()));
                Assert.That(got.Mean, Is.EqualTo(want["nodes"]["mean"].Value<double>()));
                Assert.That(got.Min, Is.EqualTo(want["nodes"]["min"].Value<int>()));
                Assert.That(got.Max, Is.EqualTo(want["nodes"]["max"].Value<int>()));
                Assert.That(got.ByType.Select(kv => kv.Key), Is.EqualTo(((JObject)want["byType"]).Properties().Select(p => p.Name)));
                Assert.That(got.ByType.Select(kv => kv.Value), Is.EqualTo(((JObject)want["byType"]).Properties().Select(p => p.Value.Value<double>())));
            }
        }

        [Test]
        public void RollEncounterMatches()
        {
            foreach (var c in ((JArray)Oracle("extras.json")["encounterRolls"]).Cast<JObject>())
            {
                var rng = new Rng((uint)c["seed"].Value<long>());
                var where = $"rollEncounter {c["seat"]} {c["pool"]} seed={c["seed"]}";
                string got = null;
                var threw = false;
                try { got = MapEncounters.RollEncounter(Data, rng, (string)c["pool"], (string)c["seat"], ((JArray)c["exclude"]).Select(t => (string)t).ToList()); }
                catch (InvalidOperationException) { threw = true; }
                Assert.That(threw, Is.EqualTo(c["error"].Type != JTokenType.Null), where + ": refusal");
                if (!threw) Assert.That(got, Is.EqualTo((string)c["id"]), where);
                Assert.That(Counters(rng), Is.EqualTo(Counters(c["rng"])), where + ": counters");
            }
        }

        [Test]
        public void UnknownNodeRollsMatch()
        {
            var doc = Oracle("history.json");
            var histories = (JArray)doc["histories"];
            foreach (var c in ((JArray)Oracle("extras.json")["unknownRolls"]).Cast<JObject>())
            {
                var rng = new Rng((uint)c["seed"].Value<long>());
                var seen = ((JArray)c["seenEvents"]).Select(t => (string)t).ToList();
                var results = (JArray)c["results"];
                for (var k = 0; k < results.Count; k++)
                {
                    var got = MapEncounters.ResolveUnknownNode(Data, rng, seen, c["tier"].Value<int>(), (JArray)histories[k % 3]);
                    Assert.That(Plain(got.ToJson()), Is.EqualTo(Plain(results[k])), $"unknown tier={c["tier"]} seed={c["seed"]} roll {k}");
                }
                Assert.That(Counters(rng), Is.EqualTo(Counters(c["rng"])));
            }
        }

        [Test]
        public void SeatOrderDrawMatches()
        {
            var seats = Data.Seats.All.Select(s => (string)s["id"]).ToList();
            foreach (var c in ((JArray)Oracle("extras.json")["seatOrders"]).Cast<JObject>())
            {
                var seed = (uint)c["seed"].Value<long>();
                Assert.That(ActMap.DrawSeatOrder(Data, new Rng(seed)), Is.EqualTo(((JArray)c["order"]).Select(t => (string)t)));
                for (var i = 0; i < seats.Count; i++)
                    Assert.That(ActMap.DrawSeatOrder(ContentData, new Rng(seed), seats[i]), Is.EqualTo(((JArray)c["pinned"][i]).Select(t => (string)t)));
            }
        }

        [Test]
        public void BossEncounterForNodeMatches()
        {
            var extras = Oracle("extras.json");
            var legacy = ActMapGraph.FromJson((JObject)extras["legacy"]);
            foreach (var c in ((JArray)extras["legacyBosses"]).Cast<JObject>())
            {
                string got = null;
                try { got = ActMap.BossEncounterForNode(Data, legacy, legacy.BossId, (string)c["seat"], c["tier"].Value<int>()); }
                catch (InvalidOperationException) { got = null; }
                Assert.That(got, Is.EqualTo(c["id"].Type == JTokenType.Null ? null : (string)c["id"]), $"legacy {c["seat"]} tier {c["tier"]}");
            }
            // Every terminal of every recorded map resolves to the encounter seated on it.
            var checkedTerminals = 0;
            foreach (var file in MapFiles())
                foreach (var c in ((JArray)Oracle(file)["cases"]).Cast<JObject>())
                {
                    var graph = ActMapGraph.FromJson((JObject)c["map"]);
                    Assert.That(Plain(graph.ToJson()), Is.EqualTo(Plain(c["map"])), "graph JSON round trip");
                    foreach (var id in graph.BossIds)
                    {
                        Assert.That(ActMap.BossEncounterForNode(Data, graph, id, (string)c["seat"], c["tier"].Value<int>()), Is.EqualTo(graph.Node(id).EncounterId));
                        checkedTerminals++;
                    }
                    Assert.Throws<InvalidOperationException>(() => ActMap.BossEncounterForNode(Data, graph, graph.ShrineId, (string)c["seat"], c["tier"].Value<int>()));
                }
            Assert.That(checkedTerminals, Is.GreaterThan(500));
        }

        [Test]
        public void RefusalsHappenBeforeAnyDraw()
        {
            foreach (var c in ((JArray)Oracle("extras.json")["refusals"]).Cast<JObject>())
            {
                Assert.That(c["error"], Is.Not.Null);
                CheckCase(Data, c, "refusal " + c["error"]);
            }
        }

        [Test]
        public void ContentTablesEqualTheShippedTablesInOrder()
        {
            var dump = Oracle("tables.json");
            var content = ContentData;
            Assert.That(content.Encounters.All.Select(r => (string)r["id"]), Is.EqualTo(((JArray)dump["encounters"]).Select(r => (string)r["id"])), "encounters order");
            foreach (var want in ((JArray)dump["encounters"]).Cast<JObject>())
            {
                var got = content.Encounters.Get((string)want["id"]);
                foreach (var key in new[] { "pool", "seat", "weight", "enemies" })
                    Assert.That(Canonical(got[key]), Is.EqualTo(Canonical(want[key])), $"encounters[{want["id"]}].{key}");
            }
            Assert.That(content.Events.Ids, Is.EqualTo(((JArray)dump["events"]).Select(r => (string)r["id"])), "events order");
            foreach (var want in ((JArray)dump["enemies"]).Cast<JObject>())
                Assert.That((string)content.Enemies.Get((string)want["id"])["name"], Is.EqualTo((string)want["name"]), $"enemies[{want["id"]}].name");
            Assert.That(content.Seats.All.Select(s => Plain(new JObject { ["id"] = s["id"], ["baseTier"] = s["baseTier"] })),
                Is.EqualTo(((JArray)dump["seats"]).Select(s => Plain(new JObject { ["id"] = s["id"], ["baseTier"] = s["baseTier"] }))), "seats");
            Assert.That(Plain(content.MapConfigs), Is.EqualTo(Plain(dump["mapConfigs"])), "mapConfigs (key order is semantic)");
            Assert.That(Plain(content.EventHistoryRequirements), Is.EqualTo(Plain(dump["eventHistoryRequirements"])), "eventHistoryRequirements");
            Assert.That(Plain(content.BossLocations), Is.EqualTo(Plain(dump["bossLocations"])), "bossLocations");
            Assert.That(Plain(content.MapShapeLimits), Is.EqualTo(Plain(dump["mapShapeLimits"])), "mapShapeLimits");
            Assert.That(Plain(content.LegacyActBosses), Is.EqualTo(Plain(dump["legacyActBosses"])), "legacyActBosses");
            Assert.That(Canonical(content.Endless), Is.EqualTo(Canonical(dump["endless"])), "balance.endless");
        }

        [Test]
        public void SweepSeedMatchesTheShippedHash()
        {
            var seeds = ((JArray)Oracle("index.json")["seeds"]).Select(t => (uint)t.Value<long>()).ToList();
            for (var i = 0; i < seeds.Count; i++) Assert.That(MapGen.SweepSeed(3000 + i), Is.EqualTo(seeds[i]));
        }

        [Test]
        public void JsRoundHalvesUp()
        {
            Assert.That(MapMath.Round(2.5), Is.EqualTo(3));
            Assert.That(MapMath.Round(-2.5), Is.EqualTo(-2));
            Assert.That(MapMath.Round(0.64 * 11), Is.EqualTo(7));
            Assert.That(MapMath.Round(0.27 * 11), Is.EqualTo(3));
        }

        /// <summary>Numbers as doubles, keys sorted: row equality where display re-attachment may reorder keys.</summary>
        private static string Canonical(JToken t)
        {
            switch (t)
            {
                case null:
                    return "undefined";
                case JObject o:
                    return "{" + string.Join(",", o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + ":" + Canonical(p.Value))) + "}";
                case JArray a:
                    return "[" + string.Join(",", a.Select(Canonical)) + "]";
                default:
                    return t.Type == JTokenType.Integer || t.Type == JTokenType.Float
                        ? t.Value<double>().ToString("R", CultureInfo.InvariantCulture)
                        : t.ToString(Formatting.None);
            }
        }
    }
}
