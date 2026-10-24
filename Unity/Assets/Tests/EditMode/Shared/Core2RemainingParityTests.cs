using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-C0 (D-290): the Core 2 features a default run reaches that the fight oracles used to leave out. Every fight recorded by
    /// Tools/oracle-core2-remaining.mjs (a bot that plays any intent a player can send: every card with every target, choice and route, the flasks of the
    /// charge pools and of the slots, the answers to every offer and choice; no filter, the enemies take every turn in full; 4 classes x every encounter of the
    /// profile, and the featured runs that hold what the start does not) is played again on the C# port from the same seed and must equal the web after every
    /// step: the whole combat as a delta, the event types in order and every RNG counter. The port refuses nothing the web accepts. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RemainingParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "remaining");

        private static readonly Dictionary<string, JObject> Files = new Dictionary<string, JObject>();

        internal static JObject Read(string name)
        {
            lock (Files)
            {
                if (!Files.TryGetValue(name, out var doc)) Files[name] = doc = (JObject)Ashen.Content.JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, name)));
                return doc;
            }
        }

        public static IEnumerable<string> FightFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Fights()
        {
            foreach (var file in FightFiles())
            {
                var fights = (JArray)Read(file)["fights"];
                for (var i = 0; i < fights.Count; i++)
                    yield return new TestCaseData(file, i).SetName("Core2Remaining_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + (fights[i].Value<string>("feature") ?? "plain") + "_" + fights[i].Value<string>("encounterId") + "_" + i);
            }
        }

        /// <summary>The run a record names: created from the seed and class, then handed what the spec names (the oracle's applySpec).</summary>
        internal static JObject OpenRun(JObject fight)
        {
            var seed = (uint)fight["seed"].Value<long>();
            var data = Core2DamageParityTests.Data;
            var run = CoreRunRules.Create(data.Run, seed, fight.Value<string>("classId"), CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            var spec = fight["spec"] as JObject ?? new JObject();
            if (spec["deck"] is JArray deck)
                run["deck"] = new JArray(deck.Select((t, n) => new JObject { ["instanceId"] = "rm" + (n + 1), ["cardId"] = t.Value<string>(), ["upgraded"] = false }));
            foreach (var piece in (spec["storage"] as JArray ?? new JArray())) ((JArray)run["loadout"]["storage"]).Add(piece.DeepClone());
            foreach (var set in (spec["sets"] as JObject ?? new JObject()).Properties())
                for (var n = 0; n < ((JArray)set.Value).Count; n++) run["loadout"]["sets"][set.Name][n] = set.Value[n].DeepClone();
            var i = 0;
            foreach (var cardId in (spec["deckAdd"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                ((JArray)run["deck"]).Add(new JObject { ["instanceId"] = "rm" + (++i), ["cardId"] = cardId, ["upgraded"] = false });
            foreach (var flaskId in (spec["flasks"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                ((JArray)run["flasks"]).Add(new JObject { ["flaskId"] = flaskId });
            foreach (var id in (spec["relics"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                if (!((JArray)run["relics"]).Any(t => t.Value<string>() == id)) ((JArray)run["relics"]).Add(id);
            if (spec["maxHp"] != null) { run["maxHp"] = spec["maxHp"].DeepClone(); run["hp"] = spec["maxHp"].DeepClone(); }
            return run;
        }

        /// <summary>The fight a record names: the run, then the encounter.</summary>
        internal static CombatState Open(JObject fight) =>
            CoreRunCombat.Create(Core2DamageParityTests.Data, new Rng((uint)fight["seed"].Value<long>()), OpenRun(fight), fight.Value<string>("encounterId"));

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(FightFiles().Count(), Is.EqualTo(4), "one file per class");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2DamageParityTests.Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(index["tally"].Value<int>("fights"), Is.GreaterThan(100));
            Assert.That(((JObject)index["tally"]["rejections"]).Count, Is.EqualTo(0), "the oracle leaves nothing out: no step is rejected by a filter");
        }

        // ------------------------------------------------------------------ the units: a mount that listens for a card being prepared

        public static IEnumerable<TestCaseData> Units()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var units = (JArray)Read("units.json")["units"];
            for (var i = 0; i < units.Count; i++)
                yield return new TestCaseData(i).SetName("Core2RemainingUnit_" + units[i].Value<string>("kind") + "_" + units[i]["rule"].Value<string>("name") + "_" + units[i].Value<string>("classId") + "_" + i);
        }

        /// <summary>The fight the unit names with the mount the oracle injected (a relic whose rule listens for a card being prepared).</summary>
        private static CombatState OpenUnit(JObject unit)
        {
            var c = Open(new JObject { ["seed"] = unit["seed"], ["classId"] = unit["classId"], ["encounterId"] = unit["encounterId"], ["spec"] = new JObject { ["maxHp"] = 300 } });
            var rule = (JObject)unit["rule"];
            var mounts = c.PropertyMounts["player"];
            mounts["relic:unitPreparing"] = new PropertyMount
            {
                Kind = "relic", Id = "unitPreparing", InstanceId = "unitPreparing", ScopeTags = new List<string>(),
                Rules = new List<JObject> { new JObject { ["tag"] = rule.Value<string>("name"), ["requires"] = new JArray(), ["excludes"] = new JArray(), ["textTemplate"] = "", ["triggers"] = rule["triggers"].DeepClone() } },
            };
            return c;
        }

        [TestCaseSource(nameof(Units))]
        public void AMountThatListensForPreparingPricesAndPlaysAsTheWebDoes(int index)
        {
            var unit = (JObject)((JArray)Read("units.json")["units"])[index];
            var c = OpenUnit(unit);
            var label = "unit " + index + " " + unit["rule"].Value<string>("name") + " " + unit.Value<string>("classId") + " " + unit.Value<string>("encounterId");
            JToken want = unit["start"]["state"].DeepClone();
            Core2CombatStartParityTests.Same(label + " start", want, Core2CombatStartParityTests.Project(c));
            var n = 0;
            foreach (var step in ((JArray)unit["steps"]).OfType<JObject>())
            {
                var at = label + " step " + n++;
                foreach (var row in ((JArray)step["hand"]).OfType<JObject>())
                {
                    var costs = row["costs"] as JObject;
                    if (costs != null && costs["error"] == null)
                        Core2CombatStartParityTests.Same(at + " costs of " + row.Value<string>("cardId"), costs, CoreCosts.CardPlayCosts(c, row.Value<string>("instanceId")));
                    if (row["needsTarget"] is JValue needs && needs.Type == JTokenType.Boolean)
                        Assert.That(CoreTargets.CardNeedsEnemyTargetNow(c, row.Value<string>("instanceId")), Is.EqualTo(needs.Value<bool>()), at + " needs a target: " + row.Value<string>("cardId"));
                }
                Core2CombatStartParityTests.Same(at + " a price query moves nothing", want, Core2CombatStartParityTests.Project(c));
                var mark = c.EventLog.Count;
                var intent = (JObject)step["intent"];
                if (step["error"] != null) Assert.Throws(Is.InstanceOf<Exception>(), () => CoreDispatch.Dispatch(c, intent), at + " refused as the web refused it");
                else CoreDispatch.Dispatch(c, intent);
                want = Core2CombatStartParityTests.Apply(want, step["delta"]);
                Core2CombatStartParityTests.Same(at + " after " + intent.Value<string>("type"), want, Core2CombatStartParityTests.Project(c));
                Assert.That(Core2CombatStartParityTests.Events(c, mark), Is.EqualTo(step["events"].Select(t => t.Value<string>()).ToList()), at + " events");
            }
        }

        // ------------------------------------------------------------------ the refusals

        public static IEnumerable<TestCaseData> Refusals()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var probes = (JArray)Read("units.json")["refusals"];
            for (var i = 0; i < probes.Count; i++) yield return new TestCaseData(i).SetName("Core2RemainingRefusals_" + probes[i].Value<string>("classId") + "_" + i);
        }

        [TestCaseSource(nameof(Refusals))]
        public void AnIntentThePlayerCannotSendIsRefusedInTheWebsWords(int index)
        {
            var probe = (JObject)((JArray)Read("units.json")["refusals"])[index];
            var fight = new JObject { ["seed"] = probe["seed"], ["classId"] = probe["classId"], ["encounterId"] = probe["encounterId"], ["spec"] = probe["spec"] };
            var c = Open(fight);
            var n = 0;
            foreach (var result in ((JArray)probe["results"]).OfType<JObject>())
            {
                var intent = (JObject)result["intent"];
                var at = "probe " + index + "." + n++ + " " + intent.ToString(Newtonsoft.Json.Formatting.None);
                var copy = c.Detach();
                if (result["error"] == null) Assert.DoesNotThrow(() => CoreDispatch.Dispatch(copy, intent), at + " is accepted by the web");
                else
                {
                    var thrown = Assert.Catch<Exception>(() => CoreDispatch.Dispatch(copy, intent), at + " is refused by the web");
                    Assert.That(thrown.Message, Is.EqualTo(result.Value<string>("error")), at);
                }
            }
        }

        /// <summary>The decisions whose legal list is held to the web: the opening of the fights that hold what the start does not (flasks in the slots, a second weapon set) and a sample of the rest.</summary>
        private static bool Sampled(string feature, int step) =>
            feature == "flasks" || feature == "flasks2" || feature == "wondrous" || feature == "equipment" ? step < 6 || step % 8 == 0 : step % 15 == 0;

        public static IEnumerable<TestCaseData> LegalFights() =>
            Fights().Select(c => new TestCaseData(c.Arguments).SetName(c.TestName.Replace("Core2Remaining_", "Core2RemainingLegal_")));

        [TestCaseSource(nameof(LegalFights))]
        public void TheLegalIntentsAreTheWebsAtTheSampledDecisions(string file, int index)
        {
            var fight = (JObject)((JArray)Read(file)["fights"])[index];
            var keys = ((JArray)fight["keys"]).Select(t => t.Value<string>()).ToList();
            var run = OpenRun(fight);
            var seed = (uint)fight["seed"].Value<long>();
            var session = Ashen.App.Combat.Core2CombatSession.Start(Core2DamageParityTests.Data, new Rng(seed), run, fight.Value<string>("encounterId"));
            var feature = fight.Value<string>("feature") ?? "plain";
            var n = 0;
            foreach (var step in ((JArray)fight["steps"]).OfType<JObject>())
            {
                var at = file + "[" + index + "] " + feature + " " + fight.Value<string>("encounterId") + " decision " + n;
                if (Sampled(feature, n))
                    Assert.That(session.LegalIntents().Select(Core2SessionParityTests.Key).ToList(), Is.EqualTo(((JArray)step["legal"]).Select(t => keys[t.Value<int>()]).ToList()), at + " the legal intents are the web's list, in order");
                var outcome = session.Execute((JObject)step["intent"]);
                Assert.That(outcome.Accepted, Is.True, at + " " + step["intent"].ToString(Newtonsoft.Json.Formatting.None) + " " + outcome.Refusal?.Message);
                Assert.That(outcome.Events.Select(e => e.Value<string>("type")).ToList(), Is.EqualTo(step["events"].Select(t => t.Value<string>()).ToList()), at + " events");
                n++;
            }
        }

        [TestCaseSource(nameof(Fights))]
        public void TheFightPlaysAsTheWebPlaysIt(string file, int index) =>
            Core2FightParityTests.Replay((JObject)((JArray)Read(file)["fights"])[index], file + "[" + index + "]", Open);
    }
}
