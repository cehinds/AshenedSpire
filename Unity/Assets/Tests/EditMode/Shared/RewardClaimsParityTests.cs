using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.Content;
using Ashen.Domain.Loop;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-11.1 to US-11.3 reward-claim parity (D-036, D-037, D-040): every script Tools/oracle-claims.mjs recorded from the
    /// SHIPPED reward door (reward.js handlers over model/rewardplan.js, main.js onDone and collectArmament) is replayed
    /// through the run loop's <see cref="RewardDoor"/> on CONTENT-built loop data (the shipped preset) — the tap path
    /// (Take with a pick, Skip, a reload re-opening the door) as well as Continue under both rewardCollect modes. Each
    /// step's result, the menu rows at every mount, the claim status after every step (<see cref="RewardsView.Status"/>,
    /// the W-08 head and column) and the profile's found set must match, and the run document after the door closed
    /// (strict: presence, values and key order), the found set and every RNG counter must equal the shipped ones.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class RewardClaimsParityTests
    {
        private static string ClaimsDir => Path.Combine(TestContent.OracleRoot, "claims");
        private static string RewardsDir => Path.Combine(TestContent.OracleRoot, "rewards");

        private static LoopData _data;

        private static LoopData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var content = snapshot.Content;
                JObject Doc(string file) => (JObject)content.Get(file);
                var registries = RuntimeRegistries.Build(content, Doc(ContentFiles.StringsEn));
                return _data = registries.ToLoopData(Doc(ContentFiles.RulesMechanics), Doc(ContentFiles.RulesCombatEngine), Doc(ContentFiles.RulesHandRules),
                    Doc(ContentFiles.RulesRunEngine), snapshot.ContentVersion, (JArray)Doc(ContentFiles.BalanceCustomRun)["ASCENSION_ORDER"],
                    Doc(ContentFiles.RulesRewardsEngine), Doc(ContentFiles.RulesMapEngine), Doc(ContentFiles.RulesLoopEngine));
            }
        }

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        public static IEnumerable<string> CaseFiles() =>
            Directory.Exists(ClaimsDir)
                ? Directory.GetFiles(ClaimsDir, "claims-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        private static Dictionary<RngStream, uint> Counters(JObject o) =>
            o.Properties().ToDictionary(p => RngStreamNames.Parse(p.Name), p => (uint)p.Value.Value<long>());

        private static List<string> Strings(JToken t) => (t as JArray ?? new JArray()).Select(x => x.Value<string>()).ToList();

        [Test]
        public void TheOracleCoversEveryClaimPath()
        {
            var index = ReadJson(Path.Combine(ClaimsDir, "index.json"));
            Assert.That(CaseFiles().Count(), Is.EqualTo(((JArray)index["cases"]).Count));
            Assert.That(CaseFiles().Count(), Is.GreaterThanOrEqualTo(100));
            var tally = (JObject)index["tally"];
            foreach (var key in new[] { "take:card", "take:skillDraft", "take:classDraft", "take:flask", "take:relic", "take:armament", "take:refused",
                         "swept:card", "swept:skillDraft", "swept:classDraft", "swept:flask", "swept:relic", "swept:armament",
                         "eachRow", "sweepAuto", "sweepManual", "partialAuto", "blocked", "spentDraft", "duplicateArmament" })
                Assert.That(tally.Value<int?>(key) ?? 0, Is.GreaterThan(0), key);
        }

        [TestCaseSource(nameof(CaseFiles))]
        public void RewardDoorClaimsMatchTheShippedDoor(string file)
        {
            var log = ReadJson(Path.Combine(ClaimsDir, file));
            var source = ReadJson(Path.Combine(RewardsDir, log.Value<string>("source")));
            foreach (var script in ((JArray)log["scripts"]).OfType<JObject>())
                Replay(file + " " + script.Value<string>("name"), source, script);
        }

        private static void Replay(string label, JObject source, JObject script)
        {
            var run = (JObject)(script["inputRun"] ?? source["expected"]["run"]).DeepClone();
            var rng = new Rng((uint)source["seed"].Value<long>(), Counters((JObject)source["expected"]["rngAfter"]));
            var profile = new JObject { ["found"] = new JArray(Strings(script["found"])) };
            var ctx = new LoopContext(Data, run, rng, profile, new LoopSettings { RewardCollect = script.Value<string>("mode") });
            RewardDoor door = null;
            var takenHere = new List<string>();
            var i = 0;
            foreach (var step in ((JArray)script["steps"]).OfType<JObject>())
            {
                var at = label + " step " + i++ + " " + step.Value<string>("op");
                switch (step.Value<string>("op"))
                {
                    case "open":
                    {
                        var before = run["pendingReward"]?["states"]?["cinders"] != null;
                        door = RewardDoor.OpenPending(ctx);
                        takenHere.Clear();
                        var landed = !before && run["pendingReward"]?["states"]?["cinders"] != null;
                        if (landed) takenHere.Add("cinders");
                        Assert.That(landed, Is.EqualTo(step.Value<bool>("landed")), at + " landed");
                        Check(at, "plan", step["plan"], door.Rows);
                        break;
                    }
                    case "take":
                    {
                        var ok = door.Take(step.Value<string>("key"), step.Value<string>("pick"));
                        Assert.That(ok, Is.EqualTo(step.Value<bool>("ok")), at + " landed");
                        if (ok) takenHere.Add(step.Value<string>("key"));
                        break;
                    }
                    case "skip":
                        door.Skip(step.Value<string>("key"));
                        Assert.That(step.Value<bool>("ok"), Is.True, at);
                        break;
                    case "continue":
                    {
                        Assert.That(step.Value<string>("mode"), Is.EqualTo(script.Value<string>("mode")), at + " mode");
                        var receipt = door.Continue(script.Value<string>("mode"));
                        Assert.That(receipt.Taken, Is.EqualTo(takenHere.Concat(Strings(step["taken"])).ToList()), at + " taken");
                        var picks = (JObject)step["picks"];
                        if (picks.Value<string>("card") != null) Assert.That(receipt.ChosenCardId, Is.EqualTo(picks.Value<string>("card")), at + " auto pick");
                        Assert.That(receipt.After, Is.EqualTo(step.Value<string>("after")), at + " after");
                        Assert.That(run["pendingReward"], Is.Null, at + " the checkpoint closed");
                        break;
                    }
                    default:
                        Assert.Fail(at + ": unknown op");
                        break;
                }
                if (step["status"] != null) Check(at, "status", step["status"], RewardsView.Status(door.Rows, door.States));
                Assert.That(Strings(ctx.Profile["found"]), Is.EqualTo(Strings(step["found"])), at + " found");
            }
            var expected = (JObject)script["expected"];
            Check(label, "run", expected["run"], run);
            Assert.That(Strings(ctx.Profile["found"]), Is.EqualTo(Strings(expected["found"])), label + " found");
            foreach (var p in ((JObject)expected["rngAfter"]).Properties())
                Assert.That(rng.Counter(RngStreamNames.Parse(p.Name)), Is.EqualTo((uint)p.Value.Value<long>()), label + " rng " + p.Name);
        }

        [Test]
        public void TheSessionRefusesWhatTheScreenNeverOffers()
        {
            var row = new JObject { ["kind"] = "card", ["key"] = "card", ["blockedBy"] = null, ["cardIds"] = new JArray("a", "b"), ["choice"] = true };
            var states = new JObject();
            Assert.That(RewardsView.PreRefusal(null, states, null), Is.EqualTo(RunFlowValues.RefusalNoSuchRow));
            Assert.That(RewardsView.PreRefusal(row, states, null), Is.EqualTo(RunFlowValues.RefusalNoPick));
            Assert.That(RewardsView.PreRefusal(row, states, "c"), Is.EqualTo(RunFlowValues.RefusalNotOffered));
            Assert.That(RewardsView.PreRefusal(row, states, "a"), Is.Null);
            states["card"] = "taken";
            Assert.That(RewardsView.PreRefusal(row, states, "a"), Is.EqualTo(RunFlowValues.RefusalClaimed));
            var flask = new JObject { ["kind"] = "flask", ["key"] = "flask", ["blockedBy"] = "slots", ["flaskId"] = "x" };
            Assert.That(RewardsView.PreRefusal(flask, new JObject(), null), Is.EqualTo(RunFlowValues.RefusalBlocked));
            Assert.That(RewardsView.ApplyRefusal(new JObject { ["kind"] = "skillDraft" }), Is.EqualTo(RunFlowValues.RefusalSpent));
            Assert.That(RewardsView.ApplyRefusal(new JObject { ["kind"] = "armament" }), Is.EqualTo(RunFlowValues.RefusalNoRoom));
        }

        // ------------------------------------------------------------------ comparison (strict, key order included)

        private static void Check(string label, string what, JToken want, JToken got)
        {
            var diff = RewardsParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{label} {what}: first difference at {diff}");
        }
    }
}
