using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-16 (D-334 to D-340): the Core 2 rest site. Tools/oracle-core2-rest.mjs drives the shipped web for the configured default profile: the arrival heal (beginGameProfileRest),
    /// the free Smith and Level Up (commitGameProfileRest) with every refusal, the Smith's plan on runs that own every item the content upgrades (stat rows, requirements, the equipment
    /// Poise threshold and relic passives, the live card rows read off two real fights), the pool re-derivation, the re-entry record of a left site and what the rest screen shows.
    /// 16 scenarios of eight visits each work a climb through the rest sites of its act maps. The Domain port must give the same visit, receipts, run changes, refusals, screen figures
    /// and RNG counters at every step, and leave the same run document. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RestParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "rest");

        private static Core2Content Core => Core2HostParityTests.Content.Core2;

        private static Core2RewardsData Data => Core.Rewards;

        internal static JObject Read(string name) => OracleDocuments.Read(Path.Combine(Dir, name));

        private static JObject Index => Read("index.json");

        private static UiData _ui;

        private static StringTable Strings => (_ui ??= UiData.Load(TestContent.Source)).Strings;

        public static IEnumerable<TestCaseData> Files() =>
            ((JArray)Index["scenarios"]).OfType<JObject>().Select(c => new TestCaseData(c.Value<string>("file")).SetName("Core2Rest_" + Path.GetFileNameWithoutExtension(c.Value<string>("file"))));

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = OracleCompare.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail(label + ": first difference at " + diff);
        }

        private static Dictionary<string, double> Live(Rng rng) => rng.AllCounters().Where(p => p.Value != 0).ToDictionary(p => p.Key, p => (double)p.Value);

        private static Dictionary<string, double> Want(JToken counters) => ((JObject)counters).Properties().Where(p => p.Value.Value<double>() != 0).ToDictionary(p => p.Name, p => p.Value.Value<double>());

        private static JToken Failure(string error) => new JObject { ["error"] = error };

        private static JToken Try(Func<JToken> fn)
        {
            try { return fn(); }
            catch (InvalidOperationException e) { return Failure(e.Message); }
        }

        private static JObject Unlocked => Core2HostParityTests.Unlocked(Core);

        private static JObject Create(uint seed, string classId) => CoreRunRules.Create(Core.Combat.Run, seed, classId, CoreRunOptions.FromJson(null, Unlocked));

        // The pinned profile is megabytes and no rest operation edits it: the changes of a step never look at it.
        private static bool Skipped(string name) => name == "streamCounters" || name == Ashen.Generated.Core2RunSnapshotKeys.GameProfile;

        private static Dictionary<string, JToken> Snapshot(JObject run) => run.Properties().Where(p => !Skipped(p.Name)).ToDictionary(p => p.Name, p => p.Value.DeepClone());

        private static JObject Changes(Dictionary<string, JToken> before, JObject run)
        {
            var set = new JObject();
            var del = new JArray();
            foreach (var p in run.Properties())
            {
                if (Skipped(p.Name)) continue;
                if (!before.TryGetValue(p.Name, out var was) || OracleCompare.FirstDifference(was, p.Value, "$") != null) set[p.Name] = p.Value.DeepClone();
            }
            foreach (var name in before.Keys) if (run[name] == null) del.Add(name);
            return new JObject { ["set"] = set, ["del"] = del };
        }

        private static JToken Document(JObject run)
        {
            var copy = new JObject(run.Properties().Where(p => !Skipped(p.Name) && p.Name != Ashen.Generated.Core2RunKeys.RunId).Select(p => new JProperty(p.Name, p.Value.DeepClone())));
            copy[Ashen.Generated.Core2RunSnapshotKeys.GameProfile] = CoreRunRules.PinOf(run);
            return copy;
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Index;
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(((JArray)index["scenarios"]).Count, Is.GreaterThanOrEqualTo(Core.Combat.Run.Run.Classes.Count * 4));
            var tally = (JObject)index["tally"];
            Assert.That(tally.Value<int>("visits"), Is.GreaterThanOrEqualTo(100));
            Assert.That(tally.Value<int>("healed"), Is.GreaterThan(30), "arrivals that healed");
            Assert.That(tally.Value<int>("smiths"), Is.GreaterThan(30));
            Assert.That(tally.Value<int>("levelUps"), Is.GreaterThan(10));
            Assert.That(tally.Value<int>("refused"), Is.GreaterThan(50));
            Assert.That(tally.Value<int>("cardRows"), Is.GreaterThan(50), "the Smith's live card rows");
            var errors = ((JObject)index["errors"]).Properties().Select(p => p.Name).ToList();
            foreach (var wanted in new[] { "Rest visit is unavailable or spent", "Character leveling is disabled for this profile", "Unknown rest service 'bogus'", "is not an eligible Smithing candidate", "is not an attribute id" })
                Assert.That(errors.Any(e => e.Contains(wanted)), Is.True, "the web refuses with: " + wanted);
        }

        // ------------------------------------------------------------------ units

        /// <summary>
        /// A test edit the oracle recorded (its documents hold keys in sorted order): written field by field into the run, an object into the object the run has so that the
        /// run's own key order stands (the Smith lists carried items in the order the loadout lists its slots).
        /// </summary>
        private static void Edit(JObject target, string name, JToken value)
        {
            if (target[name] is JObject have && value is JObject want)
            {
                foreach (var p in want.Properties()) Edit(have, p.Name, p.Value);
                return;
            }
            target[name] = value.DeepClone();
        }

        private static JObject PlainCandidate(JObject c)
        {
            var copy = (JObject)c.DeepClone();
            copy.Remove("cardRows");
            return copy;
        }

        [Test]
        public void TheSmithsPlanIsTheWebs()
        {
            var rowsCompared = 0;
            foreach (var u in ((JArray)Read("units.json")["plans"]).OfType<JObject>())
            {
                var at = "plan " + u.Value<string>("classId") + " " + u.Value<string>("variant");
                var run = Create((uint)u.Value<long>("seed"), u.Value<string>("classId"));
                var edited = (JObject)u["edited"];
                Edit(run, "loadout", edited["loadout"]);
                Edit(run, "relics", edited["relics"]);
                if (edited["itemUpgradeLevels"] != null && edited["itemUpgradeLevels"].Type != JTokenType.Null) Edit(run, "itemUpgradeLevels", edited["itemUpgradeLevels"]);
                var plan = Core2Smith.Plan(Data, run, true);
                var got = Js.Items(plan["candidates"]).OfType<JObject>().ToList();
                var want = ((JArray)u["candidates"]).OfType<JObject>().ToList();
                Assert.That(got.Select(c => c.Value<string>("itemRef")), Is.EqualTo(want.Select(c => c.Value<string>("itemRef"))), at + " candidates");
                for (var i = 0; i < want.Count; i++)
                {
                    var name = at + " " + want[i].Value<string>("itemRef");
                    Same(name, PlainCandidate(want[i]), got[i]);
                    if (want[i]["core"] == null || want[i]["cardRows"] == null) continue;
                    var rows = Core2Smith.CardRows(Data, run, got[i].Value<string>("itemRef"), got[i].Value<double>("currentLevel"), got[i].Value<double>("nextLevel"));
                    Same(name + " card rows", want[i]["cardRows"], rows);
                    rowsCompared += rows.Count;
                }
            }
            Assert.That(rowsCompared, Is.GreaterThan(20), "the units hold worn items whose cards change");
        }

        [Test]
        public void ThePoolsAreRederivedAsTheWebDoes()
        {
            foreach (var u in ((JArray)Read("units.json")["rederive"]).OfType<JObject>())
            {
                var at = "rederive " + u.Value<string>("classId") + " " + u.Value<string>("attributeId");
                var run = Create((uint)u.Value<long>("seed"), u.Value<string>("classId"));
                run["hp"] = Math.Max(1, run.Value<double>("hp") - 3);
                run["mana"] = Math.Max(0, run.Value<double>("mana") - 1);
                run["stamina"] = Math.Max(0, run.Value<double>("stamina") - 1);
                var before = Snapshot(run);
                var attributes = (JObject)run["attributes"];
                attributes[u.Value<string>("attributeId")] = attributes.Value<double>(u.Value<string>("attributeId")) + 1;
                run.Obj("level")["unspentPoints"] = 0;
                var moved = Core2Rest.RederivePools(Data, run);
                Assert.That(moved, Is.EqualTo(u.Value<double>("moved")), at + " moved");
                Same(at + " changes", u["changes"], Changes(before, run));
            }
        }

        [Test]
        public void TheDoorsRefusalsAreTheWebs()
        {
            foreach (var u in ((JArray)Read("units.json")["doors"]).OfType<JObject>())
            {
                var seed = (uint)u.Value<long>("seed");
                var run = Core2RunLoop.NewClimb(Core.Loop, seed, u.Value<string>("classId"), Unlocked, out var rng);
                var n = 0;
                foreach (var step in ((JArray)u["steps"]).OfType<JObject>())
                {
                    var op = step.Value<string>("op");
                    var at = "door " + u.Value<string>("classId") + " step " + n++ + " " + op;
                    var before = Snapshot(run);
                    JToken got;
                    switch (op)
                    {
                        case "travel":
                            got = Try(() => Travel(run, rng, step.Value<string>("nodeId")));
                            break;
                        case "enter":
                            got = Try(() => { Core2Rest.OpenRecord(run); return new JObject { ["visit"] = Core2Rest.Begin(Data, run).DeepClone() }; });
                            break;
                        case "commit":
                            got = Try(() => { var receipt = Core2Rest.Commit(Data, run, step.Value<string>("service"), new JObject { ["itemRef"] = "armament/greatsword" }); Core2Rest.RecordConsumed(Data, run, step.Value<string>("service")); return receipt; });
                            break;
                        case "begin":
                            got = Try(() => Core2Rest.Begin(Data, run, step.Value<string>("visitId")).DeepClone());
                            break;
                        case "revisit":
                            got = new JObject { ["revisit"] = Core2Rest.RevisitNodeId(run) is string revisit ? (JToken)new JValue(revisit) : JValue.CreateNull() };
                            break;
                        default:
                            Assert.Fail(at + " unknown op");
                            return;
                    }
                    Same(at, step["result"] ?? Failure(step.Value<string>("error")), got);
                    Same(at + " changes", step["changes"], Changes(before, run));
                }
                Same("door " + u.Value<string>("classId") + " the run document", u["run"], Document(run));
            }
        }

        /// <summary>The oracle's travel: the bookkeeping of main.js enterNode up to the door. A rest node goes through the loop's own door; any other node is not entered (no fight is rolled).</summary>
        private static JToken Travel(JObject run, Rng rng, string nodeId)
        {
            var node = run.Obj("mapGraph").Obj("nodes").Obj(nodeId);
            if (node.Value<string>("type") == "shrine") Core2RunLoop.EnterNode(Core.Loop, run, rng, nodeId);
            else
            {
                Core2Rest.ClearRevisitOnTravel(run, nodeId);
                run["mapNodeId"] = nodeId;
                if (!run.Arr("path").Any(p => p.Value<string>() == nodeId)) run.Arr("path").Add(nodeId);
                run["floor"] = node["floor"].DeepClone();
            }
            return new JObject { ["kind"] = node.Value<string>("type"), ["actualType"] = node.Value<string>("actualType") };
        }

        // ------------------------------------------------------------------ the screen

        private static JArray RowsOf(IEnumerable<Core2RecoveryRowView> rows) =>
            rows == null ? null : new JArray(rows.Select(r => (JToken)new JObject { ["id"] = r.Id, ["label"] = r.Label, ["before"] = r.Before, ["after"] = r.After }));

        /// <summary>The oracle's screenOf, from the view the session gives a screen.</summary>
        private static JObject ScreenJson(JObject run, bool full)
        {
            var view = Core2RestView.Build(Data, run, Strings);
            var candidates = new JArray();
            foreach (var item in view.SmithItems)
            {
                if (!full)
                {
                    candidates.Add(new JObject { ["itemRef"] = item.ItemRef, ["currentLevel"] = item.CurrentLevel, ["nextLevel"] = item.NextLevel, ["changes"] = item.Plan["changes"].DeepClone() });
                    continue;
                }
                var plan = (JObject)item.Plan.DeepClone();
                plan.Remove("refinedCost");
                plan.Remove("refinedAffordable");
                if (plan["core"] != null) plan["cardRows"] = Core2Smith.CardRows(Data, run, item.ItemRef, item.CurrentLevel, item.NextLevel);
                candidates.Add(plan);
            }
            return new JObject
            {
                ["status"] = view.Status,
                ["healed"] = view.Healed ?? (JToken)JValue.CreateNull(),
                ["line"] = view.Line ?? (JToken)JValue.CreateNull(),
                ["recovery"] = RowsOf(view.Recovery),
                ["services"] = new JArray(view.Services.Select(s => (JToken)new JObject
                {
                    ["id"] = s.Id, ["title"] = s.Title, ["description"] = s.Description, ["available"] = s.Available, ["reason"] = s.Reason, ["state"] = s.State, ["used"] = s.Used,
                })),
                ["smithFree"] = view.SmithFree,
                ["smith"] = new JObject { ["stones"] = view.Stones, ["candidates"] = candidates },
                ["smithPreviews"] = new JArray(view.SmithItems.Select(i => (JToken)new JObject
                {
                    ["itemRef"] = i.ItemRef, ["available"] = i.Available, ["reason"] = i.Reason, ["recovery"] = (JToken)RowsOf(i.Recovery) ?? JValue.CreateNull(),
                })),
                ["attributes"] = new JArray(view.Attributes.Select(a => (JToken)new JObject
                {
                    ["id"] = a.Id, ["label"] = a.Label, ["shortLabel"] = a.ShortLabel, ["value"] = a.Value, ["available"] = a.Available, ["reason"] = a.Reason, ["recovery"] = (JToken)RowsOf(a.Recovery) ?? JValue.CreateNull(),
                })),
            };
        }

        // ------------------------------------------------------------------ the climbs

        [TestCaseSource(nameof(Files))]
        public void TheRestSiteIsTheWebs(string file)
        {
            var sc = Read(file);
            var seed = (uint)sc.Value<long>("seed");
            var run = Core2RunLoop.NewClimb(Core.Loop, seed, sc.Value<string>("classId"), Unlocked, out var rng);
            var n = 0;
            foreach (var step in ((JArray)sc["steps"]).OfType<JObject>())
            {
                var op = step.Value<string>("op");
                var at = file + " step " + n++ + " " + op;
                if (op == "screen")
                {
                    Same(at + " " + step.Value<string>("label"), step["screen"], ScreenJson(run, step.Value<string>("label") == "arrival"));
                    Assert.That(Live(rng), Is.EqualTo(Want(step["counters"])), at + " counters");
                    continue;
                }
                var before = Snapshot(run);
                var nodeId = step.Value<string>("nodeId");
                switch (op)
                {
                    case "hurt":
                    case "own":
                    case "points":
                        // A test edit: the oracle recorded exactly the fields it changed.
                        foreach (var p in ((JObject)step["changes"]["set"]).Properties()) Edit(run, p.Name, p.Value);
                        break;
                    case "act":
                        Core2RunLoop.AdvanceAct(Core.Loop, run, rng);
                        break;
                    case "travel":
                        Same(at, step["result"], Travel(run, rng, nodeId));
                        break;
                    case "enter":
                        Same(at, step["result"] ?? Failure(step.Value<string>("error")), Try(() => { Core2Rest.OpenRecord(run); return new JObject { ["visit"] = Core2Rest.Begin(Data, run).DeepClone() }; }));
                        break;
                    case "commit":
                        var service = step.Value<string>("service");
                        var payload = new JObject();
                        if (step["itemRef"] != null) payload["itemRef"] = step["itemRef"].DeepClone();
                        if (step["attributeId"] != null) payload["attributeId"] = step["attributeId"].DeepClone();
                        Same(at, step["result"] ?? Failure(step.Value<string>("error")), Try(() => { var receipt = Core2Rest.Commit(Data, run, service, payload); Core2Rest.RecordConsumed(Data, run, service); return receipt; }));
                        break;
                    case "leave":
                        Core2Rest.MarkLeft(run);
                        Same(at, step["result"], new JObject { ["revisit"] = Core2Rest.RevisitNodeId(run) is string revisit ? (JToken)new JValue(revisit) : JValue.CreateNull() });
                        break;
                    default:
                        Assert.Fail(at + " unknown op");
                        break;
                }
                Same(at + " changes", step["changes"], Changes(before, run));
                Assert.That(Live(rng), Is.EqualTo(Want(step["counters"])), at + " counters");
            }
            Same(file + " the run document", sc["run"], Document(run));
            Assert.That(Live(rng), Is.EqualTo(Want(sc["counters"])), file + " final counters");
        }
    }
}
