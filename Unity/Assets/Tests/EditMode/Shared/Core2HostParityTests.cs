using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Domain.Random;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-C1a (D-302): the Core 2 climb the host walks, held to the shipped web. Tools/oracle-core2-host.mjs records, for 4 classes x 8 seeds, the new run's ledgers, seat order and
    /// first act map, then a bot's whole climb through enterNode / startFight / advanceAct as main.js does them: the path and floor at each node, the door it opens (the encounter a
    /// fight rolls on the seat's pool or a boss names, the event an Unknown node resolved to, the rest, shop and treasure doors), the recent encounters, the seen events, every RNG
    /// counter, and each later act's map. <see cref="Core2RunLoop"/> must equal it step by step. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2HostParityTests
    {
        private static string Path_ => Path.Combine(TestContent.OracleRoot, "core2", "host", "climbs.json");

        private static JObject _doc;
        private static RunContent _content;

        private static JObject Doc => _doc ??= (JObject)JsonContent.Parse(File.ReadAllText(Path_));
        internal static RunContent Content => _content ??= RunContent.Load(TestContent.Source, "shipped");

        internal static JObject Unlocked(Core2Content core) => new JObject
        {
            ["gameProfileUnlocks"] = new JObject { [core.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, core.Combat.Run.Run.Classes.Count).Select(i => "r" + i)) } },
        };

        public static IEnumerable<TestCaseData> Climbs()
        {
            if (!File.Exists(Path_)) yield break;
            var climbs = (JArray)Doc["climbs"];
            for (var i = 0; i < climbs.Count; i++) yield return new TestCaseData(i).SetName("Core2Host_" + climbs[i].Value<string>("classId") + "_" + i);
        }

        private static Dictionary<string, double> Live(Rng rng) => rng.AllCounters().Where(p => p.Value != 0).ToDictionary(p => p.Key, p => (double)p.Value);

        private static Dictionary<string, double> Want(JToken counters) => ((JObject)counters).Properties().ToDictionary(p => p.Name, p => p.Value.Value<double>());

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail(label + ": first difference at " + diff);
        }

        /// <summary>A map in few bytes, as the oracle writes it.</summary>
        private static JObject Compact(JObject graph)
        {
            var nodes = new JObject();
            foreach (var p in graph.Obj("nodes").Properties())
            {
                var n = (JObject)p.Value;
                nodes[p.Name] = new JArray(n["floor"], n["type"], n["actualType"] ?? JValue.CreateNull(), n["resolved"] != null ? (JToken)n["resolved"].ToString(Newtonsoft.Json.Formatting.None) : JValue.CreateNull(), string.Join(",", ((JArray)n["next"]).Select(t => (string)t)));
            }
            return new JObject { ["startIds"] = graph["startIds"].DeepClone(), ["bossIds"] = graph["bossIds"]?.DeepClone() ?? JValue.CreateNull(), ["nodes"] = nodes };
        }

        [Test]
        public void TheOracleIsPresent()
        {
            Assert.That(Doc.Value<string>("profileHash"), Is.EqualTo(Content.Core2.ProfileHash));
            Assert.That(((JArray)Doc["climbs"]).Count, Is.GreaterThanOrEqualTo(32));
            Assert.That(Doc.Value<int>("routesActs"), Is.EqualTo(Content.Core2.Loop.Acts));
        }

        [TestCaseSource(nameof(Climbs))]
        public void TheClimbWalksAsTheWebWalksIt(int index)
        {
            var climb = (JObject)((JArray)Doc["climbs"])[index];
            var core = Content.Core2;
            var seed = (uint)climb["seed"].Value<long>();
            var label = "climb " + index + " " + climb.Value<string>("classId");
            var run = Core2RunLoop.NewClimb(core.Loop, seed, climb.Value<string>("classId"), Unlocked(core), out var rng);
            var start = (JObject)climb["start"];
            var ledgers = (JObject)start["ledgers"];
            foreach (var p in ledgers.Properties()) Same(label + " start " + p.Name, p.Value, run[p.Name]);
            Same(label + " the first act's map", start["mapGraph"], run["mapGraph"]);
            Assert.That(Live(rng), Is.EqualTo(Want(start["counters"])), label + " counters after the new run");

            foreach (var node in ((JArray)climb["nodes"]).OfType<JObject>())
            {
                if (node["advanced"] != null)
                {
                    Core2RunLoop.AdvanceAct(core.Loop, run, rng);
                    var at = label + " act " + node["actNumber"];
                    Assert.That(run.Value<double>("actNumber"), Is.EqualTo(node.Value<double>("actNumber")), at);
                    Assert.That(run.Value<double>("hp"), Is.EqualTo(node.Value<double>("hp")), at + " hp");
                    Same(at + " floor", node["floor"], run["floor"]);
                    Same(at + " mapNodeId", node["mapNodeId"], run["mapNodeId"]);
                    Same(at + " path", node["path"], run["path"]);
                    Same(at + " map", node["map"], Compact((JObject)run["mapGraph"]));
                    Assert.That(Live(rng), Is.EqualTo(Want(node["counters"])), at + " counters");
                    continue;
                }
                var nodeId = node.Value<string>("nodeId");
                var where = label + " " + nodeId;
                var door = Core2RunLoop.EnterNode(core.Loop, run, rng, nodeId);
                var want = (JObject)node["door"];
                Assert.That(door.Kind, Is.EqualTo(want.Value<string>("door")), where + " door");
                Assert.That(door.Pool, Is.EqualTo(want.Value<string>("pool")), where + " pool");
                Assert.That(door.EncounterId, Is.EqualTo(want.Value<string>("encounterId")), where + " encounter");
                Assert.That(door.EventId, Is.EqualTo(want.Value<string>("eventId")), where + " event");
                if (want["camp"] != null) Assert.That(door.Camp, Is.EqualTo(want.Value<bool>("camp")), where + " camp");
                Same(where + " floor", node["floor"], run["floor"]);
                Same(where + " path", node["path"], run["path"]);
                Same(where + " lastEncounters", node["lastEncounters"], run["lastEncounters"]);
                Same(where + " seenEvents", node["seenEvents"], run["seenEvents"]);
                Assert.That(Live(rng), Is.EqualTo(Want(node["counters"])), where + " counters");
            }
        }
    }
}
