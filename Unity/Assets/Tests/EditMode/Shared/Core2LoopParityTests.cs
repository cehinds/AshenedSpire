using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-19 (D-345 to D-352): the whole Core 2 run. Tools/oracle-core2-loop.mjs plays 32 whole runs on the shipped web for the configured default profile (4 classes x 8 seeds, from
    /// the new run to its end, mirroring main.js: enterNode, the events with their responses and refusals, the rests, the shops, the treasures, the fights with the combat end and
    /// the reward door, the act advance, finishRun and the class unlock ledger) and records every step: what was entered, the answer, the run's changed keys and the random
    /// streams. <see cref="Core2RunSession"/> is driven through the same steps and must give the same doors, answers, run changes and counters at every step, and the same record and
    /// unlock ledger at the end. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2LoopParityTests
    {
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        private string _dir;
        private SaveService _saves;

        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "loop");

        private static RunContent Content => Core2HostParityTests.Content;

        private static readonly Dictionary<string, JObject> Files = new Dictionary<string, JObject>();

        internal static JObject Read(string name)
        {
            lock (Files)
            {
                if (!Files.TryGetValue(name, out var doc)) Files[name] = doc = (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, name)));
                return doc;
            }
        }

        private static JObject Index => Read("index.json");

        public static IEnumerable<TestCaseData> RunFiles() =>
            ((JArray)Index["runs"]).OfType<JObject>().Select(c => new TestCaseData(c.Value<string>("file")).SetName("Core2Loop_" + Path.GetFileNameWithoutExtension(c.Value<string>("file"))));

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-loop-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail(label + ": first difference at " + diff);
        }

        private static Dictionary<string, double> Live(IReadOnlyDictionary<string, uint> counters) => counters.Where(p => p.Value != 0).ToDictionary(p => p.Key, p => (double)p.Value);

        private static Dictionary<string, double> Want(JToken counters) => ((JObject)counters).Properties().Where(p => p.Value.Value<double>() != 0).ToDictionary(p => p.Name, p => p.Value.Value<double>());

        // The pinned profile is megabytes and no operation edits it: the changes of a step never look at it.
        private static bool Skipped(string name) => name == "streamCounters" || name == Ashen.Generated.Core2RunSnapshotKeys.GameProfile;

        private static Dictionary<string, JToken> Snapshot(Core2RunSession session)
        {
            Dictionary<string, JToken> snapshot = null;
            session.EditRunForReview(run => snapshot = run.Properties().Where(p => !Skipped(p.Name)).ToDictionary(p => p.Name, p => p.Value.DeepClone()));
            return snapshot;
        }

        /// <summary>{ set, del } over the run's top-level keys: what a step changed (the stream counters aside).</summary>
        private static JObject Changes(Dictionary<string, JToken> before, Core2RunSession session)
        {
            var set = new JObject();
            var del = new JArray();
            session.EditRunForReview(run =>
            {
                foreach (var p in run.Properties())
                {
                    if (Skipped(p.Name)) continue;
                    if (!before.TryGetValue(p.Name, out var was) || RunParityTests.FirstDifference(was, p.Value, "$") != null) set[p.Name] = p.Value.DeepClone();
                }
                foreach (var name in before.Keys) if (run[name] == null) del.Add(name);
            });
            return new JObject { ["set"] = set, ["del"] = del };
        }

        private static JToken Failure(string error) => new JObject { ["error"] = error };

        // ------------------------------------------------------------------ the oracle

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Index;
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Content.Core2.ProfileHash), "the oracle ran the profile that is loaded");
            var classes = Content.Core2.ClassIds.Count;
            Assert.That(((JArray)index["runs"]).Count, Is.GreaterThanOrEqualTo(classes * 8));
            var tally = (JObject)index["tally"];
            Assert.That(tally.Value<int>("fights"), Is.GreaterThanOrEqualTo(100));
            Assert.That(tally.Value<int>("bosses"), Is.GreaterThan(0));
            Assert.That(tally.Value<int>("elites"), Is.GreaterThan(0));
            Assert.That(tally.Value<int>("acts"), Is.GreaterThan(0), "a boss that advances the act");
            Assert.That(tally.Value<int>("events"), Is.GreaterThan(20));
            Assert.That(tally.Value<int>("eventFights"), Is.GreaterThan(0), "a response that starts a fight");
            Assert.That(tally.Value<int>("rests"), Is.GreaterThan(20));
            Assert.That(tally.Value<int>("shops"), Is.GreaterThan(10));
            Assert.That(tally.Value<int>("treasures"), Is.GreaterThan(10));
            Assert.That(tally.Value<int>("unlocks"), Is.GreaterThan(0), "a run that opens the next class");
            Assert.That(((JObject)index["ends"]).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "defeat", "victory" }), "runs end both ways");
            Assert.That(((JObject)index["doors"]).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "event", "fight", "rest", "shop", "treasure" }), "every kind of node is entered");
        }

        // ------------------------------------------------------------------ a whole run

        [TestCaseSource(nameof(RunFiles))]
        public void TheWholeRunIsTheWebs(string file)
        {
            var sc = Read(file);
            var seed = (uint)sc.Value<long>("seed");
            // The profile's ledger before the run: the classes it has opened.
            var profile = ProfileStore.Load(_saves);
            profile.Doc["gameProfileUnlocks"] = sc["metaBefore"]["gameProfileUnlocks"].DeepClone();
            profile.Save(Content.ContentHash);
            var session = Core2RunSession.New(Content, _saves, 1, seed, sc.Value<string>("classId"), sc.Value<string>("name"), () => Epoch);
            session.EditRunForReview(run => run["runId"] = sc["runId"].DeepClone());
            var before = Snapshot(session);
            var n = 0;
            foreach (var step in ((JArray)sc["steps"]).OfType<JObject>())
            {
                var op = step.Value<string>("op");
                var at = file + " step " + n++ + " " + op;
                var checkChanges = true;
                switch (op)
                {
                    case "travel": Travel(at, session, step); break;
                    case "fight": Fight(at, session, step); break;
                    case "ack":
                        {
                            var r = session.AcknowledgeProgression();
                            Assert.That(r.Done, Is.True, at + " " + r.Refusal);
                            Assert.That(r.Step.Next, Is.EqualTo(step["result"].Value<string>("next")), at);
                            break;
                        }
                    case "take":
                    case "add":
                        {
                            var index = step.Value<int>("index");
                            var r = op == "take" ? session.TakeReward(index) : session.AddRewardToDeck(index);
                            if (step["error"] != null) Assert.That(r.Refusal, Is.EqualTo(step.Value<string>("error")), at);
                            else
                            {
                                Assert.That(r.Refusal, Is.Null, at);
                                Same(at, step["result"], r.Option);
                            }
                            break;
                        }
                    case "leave":
                        {
                            var r = session.LeaveRewards();
                            Assert.That(r.Done, Is.True, at + " " + r.Refusal);
                            Assert.That(r.Step.Next, Is.EqualTo(step["result"].Value<string>("next")), at);
                            break;
                        }
                    case "enter":
                        // A remount of the rest screen: the visit is the one that is open and nothing moves.
                        Assert.That(step["changes"]["set"], Is.Empty, at);
                        checkChanges = false;
                        break;
                    case "commit":
                        {
                            var service = step.Value<string>("service");
                            var payload = new JObject();
                            if (step["itemRef"] != null) payload["itemRef"] = step["itemRef"].DeepClone();
                            if (step["attributeId"] != null) payload["attributeId"] = step["attributeId"].DeepClone();
                            var r = session.TakeRestService(service, payload);
                            if (step["error"] != null) Assert.That(r.Refusal, Is.EqualTo(step.Value<string>("error")), at);
                            else
                            {
                                Assert.That(r.Refusal, Is.Null, at);
                                Same(at, step["result"], r.Receipt);
                            }
                            break;
                        }
                    case "restLeave":
                        {
                            var r = session.LeaveRest();
                            Assert.That(r.Done, Is.True, at + " " + r.Refusal);
                            string revisit = null;
                            session.EditRunForReview(run => revisit = Core2Rest.RevisitNodeId(run));
                            Assert.That(revisit, Is.EqualTo(step["result"].Value<string>("revisit")), at);
                            break;
                        }
                    case "buy":
                        {
                            var r = session.BuyFromShop(step.Value<int>("index"));
                            if (step["error"] != null) Assert.That(r.Refusal, Is.EqualTo(step.Value<string>("error")), at);
                            else
                            {
                                Assert.That(r.Refusal, Is.Null, at);
                                Same(at, step["result"], r.Item);
                            }
                            break;
                        }
                    case "shopLeave":
                        Assert.That(session.LeaveShop().Done, Is.True, at);
                        break;
                    case "choose": Choose(at, session, step); break;
                    case "finish":
                        {
                            var end = session.FinishEvent();
                            Same(at, step["result"]["fight"], end.Fight == null ? JValue.CreateNull() : (JToken)end.Fight);
                            break;
                        }
                    case "end": End(at, session, step); checkChanges = false; break;
                    default:
                        Assert.Fail(at + " unknown op");
                        break;
                }
                if (checkChanges && step["changes"] != null)
                {
                    var got = Changes(before, session);
                    if (RunParityTests.FirstDifference(step["changes"], got, "$") != null && Environment.GetEnvironmentVariable("ASHEN_LOOP_DEBUG") != null)
                        File.WriteAllText(Environment.GetEnvironmentVariable("ASHEN_LOOP_DEBUG"), "WANT\n" + step["changes"].ToString() + "\nGOT\n" + got + "\nBEFORE collection\n" + before.GetValueOrDefault("collection"));
                    Same(at + " changes", step["changes"], got);
                }
                if (op != "end") before = Snapshot(session);
                Assert.That(Live(session.StreamCounters), Is.EqualTo(Want(step["counters"])), at + " counters");
            }
            Assert.That(session.RunOver, Is.True, file + " the run is over");
            Assert.That(session.End.Victory, Is.EqualTo(sc.Value<string>("over") == "victory"), file + " how it ended");
        }

        private static void Travel(string at, Core2RunSession session, JObject step)
        {
            var result = session.Travel(step.Value<string>("nodeId"));
            Assert.That(result.Travelled, Is.True, at + " " + result.Refusal);
            Assert.That(result.NotYetPorted, Is.False, at + " every node kind has its door");
            var door = (JObject)step["door"];
            var kind = door.Value<string>("door");
            Assert.That(result.Door.Kind, Is.EqualTo(kind), at + " door");
            switch (kind)
            {
                case "fight":
                    Assert.That(result.Door.EncounterId, Is.EqualTo(door.Value<string>("encounterId")), at + " encounter");
                    Assert.That(result.Door.Pool, Is.EqualTo(door.Value<string>("pool")), at + " pool");
                    Assert.That(session.IsInCombat, Is.True, at);
                    break;
                case "event":
                    Assert.That(session.EventId, Is.EqualTo(door.Value<string>("eventId")), at);
                    Assert.That(session.Location, Is.EqualTo("event"), at);
                    Same(at + " view", door["view"], session.EventDomainView());
                    break;
                case "rest":
                    Assert.That(session.IsResting, Is.True, at);
                    Same(at + " visit", door["rest"]["visit"], session.RestVisit);
                    break;
                case "shop":
                    Assert.That(session.OpenShopId, Is.EqualTo(door.Value<string>("shopId")), at);
                    Same(at + " stock", door["stock"], session.ShopStock);
                    break;
                case "treasure":
                    Assert.That(session.HasPendingReward, Is.True, at);
                    Assert.That(session.PendingReward.Value<string>("offerId"), Is.EqualTo(door.Value<string>("offerId")), at);
                    Assert.That(session.Location, Is.EqualTo("rewards"), at);
                    break;
            }
        }

        private static void Fight(string at, Core2RunSession session, JObject step)
        {
            Assert.That(session.IsInCombat, Is.True, at + " in a fight");
            Assert.That(session.EncounterId, Is.EqualTo(step.Value<string>("encounterId")), at + " encounter");
            var weaken = step.Value<double>("weaken");
            if (weaken > 0) foreach (var enemy in session.Combat.State.Enemies) enemy["hp"] = Math.Min(enemy.Value<double>("hp"), weaken);
            var intents = ((JArray)step["intents"]).Select(t => JObject.Parse(t.Value<string>())).ToList();
            for (var i = 0; i < intents.Count; i++)
            {
                var outcome = session.Execute(intents[i]);
                Assert.That(outcome.Accepted, Is.True, at + " intent " + i + " " + intents[i].ToString(Newtonsoft.Json.Formatting.None) + " " + outcome.Refusal?.Message);
            }
            Assert.That(session.IsInCombat, Is.False, at + " the fight is over after its recorded intents");
            var receipt = session.LastEnd.Receipt;
            var want = (JObject)step["outcome"];
            Assert.That(receipt.Result, Is.EqualTo(want.Value<string>("result")), at + " result");
            Assert.That(receipt.Pool, Is.EqualTo(want.Value<string>("pool")), at + " pool");
            Same(at + " track receipt", want["trackReceipt"], receipt.TrackReceipt);
            Same(at + " track awards", want["trackAwards"], receipt.TrackAwards);
            Same(at + " class award", want["classAward"], (JToken)receipt.ClassAward ?? JValue.CreateNull());
            Same(at + " notices", want["notices"], receipt.Notices);
            if (want["cinders"] != null) Assert.That(receipt.Cinders, Is.EqualTo(want.Value<double>("cinders")), at + " cinders");
            Assert.That(receipt.Next, Is.EqualTo(want.Value<string>("next")), at + " next");
            Assert.That(receipt.OfferId, Is.EqualTo(want.Value<string>("offerId")), at + " offer id");
            Same(at + " the door", step["door"], (JToken)session.PendingReward ?? JValue.CreateNull());
            Same(at + " the offer", step["offer"], (JToken)session.RewardOffer ?? JValue.CreateNull());
            var next = want.Value<string>("next");
            Assert.That(session.RunOver, Is.EqualTo(next == "victory" || next == "defeat"), at + " the run's end");
        }

        private static void Choose(string at, Core2RunSession session, JObject step)
        {
            var r = session.ChooseEvent(step.Value<string>("choiceId"));
            if (step["error"] != null)
            {
                Assert.That(r.Ok, Is.False, at);
                Assert.That(r.Error, Is.EqualTo(step.Value<string>("error")), at);
                return;
            }
            var want = (JObject)step["result"];
            if (want.Value<bool>("ok"))
            {
                Assert.That(r.Ok, Is.True, at + " " + r.RefusalKey + r.Error);
                Same(at + " outcome", want["outcome"], r.Result.Outcome);
            }
            else if (want["refusal"] != null)
            {
                Assert.That(r.Ok, Is.False, at);
                Assert.That(r.RefusalKey, Is.EqualTo(want["refusal"].Value<string>("key")), at);
            }
            else
            {
                Assert.That(r.Ok, Is.False, at);
                Assert.That(r.Error, Is.EqualTo(want.Value<string>("error")), at);
            }
        }

        private void End(string at, Core2RunSession session, JObject step)
        {
            Assert.That(session.RunOver, Is.True, at);
            Assert.That(session.End.Victory, Is.EqualTo(step.Value<bool>("victory")), at + " victory");
            Assert.That(session.ClassesUnlocked, Is.EqualTo(step["unlocked"].Select(t => t.Value<string>()).ToList()), at + " the classes the run opened");
            var doc = ProfileStore.Load(_saves).Doc;
            var ledger = doc["gameProfileUnlocks"];
            Same(at + " the unlock ledger", step["ledger"], ledger);
            var results = doc["results"] as JArray ?? new JArray();
            if (step.Value<bool>("changed"))
            {
                var row = (JObject)results.Last;
                var want = (JObject)step["record"];
                // The web's record; the run summary adds its own fields beside it (killer, duration, deck, the profile's ids).
                // The oracle has no combat screen, so its damage tallies are zero; the session writes the real tally into the record (D-962), so those two fields are not compared.
                foreach (var p in want.Properties().Where(p => p.Name != LoopKeys.DamageDealt && p.Name != LoopKeys.DamageTaken)) Same(at + " record." + p.Name, p.Value, row[p.Name] ?? JValue.CreateNull());
            }
            Assert.That(_saves.Load(session.Slot).Status, Is.EqualTo(LoadStatus.Empty), at + " the slot is cleared");
        }
    }
}
