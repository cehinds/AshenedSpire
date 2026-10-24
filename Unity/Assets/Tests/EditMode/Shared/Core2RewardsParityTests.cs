using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-15 (D-316 to D-324): Core 2 rewards and the combat end. Tools/oracle-core2-rewards.mjs drives the shipped web for the configured default profile:
    /// the reward policy (group problems, weighted category picks), the skill ledger (tracks, curve, awards, the class track, the cinders), 32 claim scenarios
    /// (offers on the cardRewards stream, claims, Add to deck with the refusals of every option, invalid and repeated claims, deck removals, range and deck-size
    /// switches) and 32 climbs whose fights are settled as main.js onCombatEnd settles a configured run (the skill and class awards, the cinders, a boss's record,
    /// the level notices, the persisted offer, the door and what follows it). The Domain port must give the same offers, options, refusals, run changes and RNG
    /// counters at every step, and leave the same run document. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RewardsParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "rewards");

        private static Core2Content Core => Core2HostParityTests.Content.Core2;

        private static Core2RewardsData Data => Core.Rewards;

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

        public static IEnumerable<TestCaseData> ClaimFiles() =>
            ((JArray)Index["claims"]).OfType<JObject>().Select(c => new TestCaseData(c.Value<string>("file")).SetName("Core2Claims_" + Path.GetFileNameWithoutExtension(c.Value<string>("file"))));

        public static IEnumerable<TestCaseData> ClimbFiles() =>
            ((JArray)Index["climbs"]).OfType<JObject>().Select(c => new TestCaseData(c.Value<string>("file")).SetName("Core2Climb_" + Path.GetFileNameWithoutExtension(c.Value<string>("file"))));

        private static JObject Unlocked => Core2HostParityTests.Unlocked(Core);

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail(label + ": first difference at " + diff);
        }

        private static Dictionary<string, double> Live(Rng rng) => rng.AllCounters().Where(p => p.Value != 0).ToDictionary(p => p.Key, p => (double)p.Value);

        private static Dictionary<string, double> Want(JToken counters) => ((JObject)counters).Properties().Where(p => p.Value.Value<double>() != 0).ToDictionary(p => p.Name, p => p.Value.Value<double>());

        private static void Counters(string at, JObject step, Rng rng) => Assert.That(Live(rng), Is.EqualTo(Want(step["counters"])), at + " counters");

        private static T Try<T>(Func<T> fn, out string error)
        {
            error = null;
            try { return fn(); }
            catch (InvalidOperationException e) { error = e.Message; return default; }
        }

        // ------------------------------------------------------------------ the oracle

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Index;
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(((JArray)index["claims"]).Count, Is.GreaterThanOrEqualTo(Core.Combat.Run.Run.Classes.Count * 8));
            Assert.That(((JArray)index["climbs"]).Count, Is.GreaterThanOrEqualTo(Core.Combat.Run.Run.Classes.Count * 8));
            var tally = (JObject)index["tally"];
            Assert.That(tally.Value<int>("fights"), Is.GreaterThanOrEqualTo(100));
            Assert.That(tally.Value<int>("bosses"), Is.GreaterThan(0));
            Assert.That(tally.Value<int>("elites"), Is.GreaterThan(0));
            Assert.That(tally.Value<int>("notices"), Is.GreaterThan(0), "a fight that crosses a level");
            Assert.That(tally.Value<int>("advances"), Is.GreaterThan(0), "a boss that advances the act");
            Assert.That(tally.Value<int>("defeats"), Is.GreaterThan(0));
            Assert.That(((JObject)index["kinds"]).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "armor", "combatCards", "potions", "relics", "weaponArts", "weapons" }), "every kind of reward is offered");
        }

        // ------------------------------------------------------------------ units

        [Test]
        public void GroupProblemsAreTheWebs()
        {
            foreach (var c in ((JArray)Read("units.json")["groups"]).OfType<JObject>())
            {
                var categories = ((JArray)c["categories"]).Select(t => t.Value<string>()).ToList();
                var got = new JArray(Core2RewardPolicy.GroupProblems(c["groups"], categories));
                Same("groups " + c["groups"].ToString(Newtonsoft.Json.Formatting.None), c["problems"], got);
            }
        }

        [Test]
        public void CategoryPicksAreTheWebs()
        {
            foreach (var c in ((JArray)Read("units.json")["picks"]).OfType<JObject>())
            {
                var categories = ((JArray)c["categories"]).Select(t => t.Value<string>()).ToList();
                var rng = new Rng((uint)c.Value<long>("seed"));
                var picks = new JArray();
                for (var i = 0; i < 16; i++) picks.Add(Core2RewardPolicy.PickCategory(rng, categories, c["groups"]) is string pick ? (JToken)pick : JValue.CreateNull());
                var at = "picks seed " + c["seed"];
                Same(at, c["picks"], picks);
                Assert.That(Live(rng), Is.EqualTo(Want(c["counters"])), at + " counters");
            }
        }

        [Test]
        public void SkillTracksAndTheirCurveAreTheWebs()
        {
            var skills = (JObject)Read("units.json")["skills"];
            var tracks = new JArray(Core2Skills.Tracks(Data).Select(t => new JObject { ["id"] = t.Id, ["kind"] = t.Kind, ["label"] = t.Label }));
            Same("tracks", skills["tracks"], tracks);
            foreach (var curve in ((JArray)skills["curve"]).OfType<JObject>())
            {
                var kind = curve.Value<string>("kind");
                var cap = Core2Skills.MaxLevel(Data, kind);
                Same("maxLevel " + kind, curve["maxLevel"], cap == null ? JValue.CreateNull() : (JToken)new JValue(cap.Value));
                var costs = new JArray();
                for (var level = 0; level < 14; level++) costs.Add(Try(() => (JToken)Core2Skills.XpToNext(Data, kind, new JValue(level)), out var error) ?? new JObject { ["error"] = error });
                Same("costs " + kind, curve["costs"], costs);
            }
        }

        [Test]
        public void SkillAwardsAreTheWebs()
        {
            var skills = (JObject)Read("units.json")["skills"];
            foreach (var sequence in ((JArray)skills["sequences"]).OfType<JObject>())
            {
                var run = CoreRunRules.Create(Core.Combat.Run, 31000u, "reaver", CoreRunOptions.FromJson(null, Unlocked));
                var id = sequence.Value<string>("skillId");
                foreach (var step in ((JArray)sequence["steps"]).OfType<JObject>())
                {
                    var amount = step.Value<double>("amount");
                    var at = "award " + id + " " + amount;
                    Same(at, step["award"], Core2Skills.Award(Data, run, id, amount));
                    Same(at + " row", step["row"], run.Obj("skills")?[id] ?? JValue.CreateNull());
                }
            }
            var error = Try(() => Core2Skills.Award(Data, CoreRunRules.Create(Core.Combat.Run, 31000u, "reaver", CoreRunOptions.FromJson(null, Unlocked)), "item:nothing", 10), out var message);
            Assert.That(error, Is.Null);
            Same("an unknown track", skills["bogus"], new JObject { ["error"] = message });
            foreach (var c in ((JArray)skills["classAwards"]).OfType<JObject>())
            {
                var run = CoreRunRules.Create(Core.Combat.Run, 31100u, c.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
                var at = "class award " + c.Value<string>("classId") + " " + c.Value<string>("pool") + " " + c["victory"] + " x" + c["multiplier"];
                var award = Core2Skills.ClassAward(Data, run, c.Value<bool>("victory"), c.Value<string>("pool"), c.Value<double>("multiplier"));
                Same(at, c["award"], (JToken)award ?? JValue.CreateNull());
                Same(at + " row", c["row"], run.Obj("skills")?["class:" + c.Value<string>("classId")] ?? JValue.CreateNull());
            }
        }

        [Test]
        public void CindersAreTheWebs()
        {
            foreach (var c in ((JArray)Read("units.json")["runes"]).OfType<JObject>())
            {
                var rng = new Rng((uint)c.Value<long>("seed"));
                var relics = (JArray)c["relics"];
                var amounts = new JArray(RewardRolls.RuneReward(Data.Rewards, rng, c.Value<string>("pool"), relics), RewardRolls.RuneReward(Data.Rewards, rng, c.Value<string>("pool"), relics));
                var at = "cinders " + c["pool"] + " seed " + c["seed"] + " relics " + relics.Count;
                Same(at, c["amounts"], amounts);
                Assert.That(Live(rng), Is.EqualTo(Want(c["counters"])), at + " counters");
            }
        }

        [Test]
        public void TheDoorStepsAreTheWebs()
        {
            foreach (var c in ((JArray)Read("units.json")["doors"]).OfType<JObject>())
            {
                var seed = (uint)c.Value<long>("seed");
                var run = CoreRunRules.Create(Core.Combat.Run, seed, c.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
                var rng = CoreSnapshot.RngFromJson(seed, run.Obj("streamCounters"));
                var spec = (JObject)c["spec"];
                run["actNumber"] = c["actNumber"].DeepClone();
                run["mapNodeId"] = "n" + (c.Value<int>("actNumber") - 1) + "_0";
                var n = 0;
                foreach (var step in ((JArray)c["steps"]).OfType<JObject>())
                {
                    var op = step.Value<string>("op");
                    var at = "door " + c.Value<string>("classId") + " " + c.Value<string>("variant") + " step " + n++ + " " + op;
                    Core2RewardStep result;
                    if (op == "begin")
                    {
                        var notices = spec.Value<bool>("notices") ? new JArray(new JObject { ["skillId"] = "class:" + c.Value<string>("classId"), ["label"] = c.Value<string>("classId"), ["before"] = 0, ["after"] = 1 }) : new JArray();
                        result = Core2RewardDoor.Begin(Data, run, rng, "combat", spec.Value<string>("after"), spec.Value<int>("levelOffers"), notices, spec.Value<bool>("noticeOnly"));
                    }
                    else result = op == "ack" ? Core2RewardDoor.Acknowledge(run) : Core2RewardDoor.Leave(Data, run, rng);
                    Assert.That(result.Next, Is.EqualTo(step["result"].Value<string>("next")), at + " next");
                    if (step["result"]["offerId"] != null) Assert.That(result.OfferId, Is.EqualTo(step["result"].Value<string>("offerId")), at + " offer");
                    Same(at + " pending", step["pending"], (JToken)Core2RewardDoor.Pending(run) ?? JValue.CreateNull());
                    Same(at + " offers", step["offers"], new JArray((run.Obj("gameProfileRewardOffers")?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => p.Name)));
                    Counters(at, step, rng);
                }
            }
        }

        // ------------------------------------------------------------------ the run's changes

        // The pinned profile is megabytes and no operation edits it: the changes of a step never look at it.
        private static bool Skipped(string name) => name == "streamCounters" || name == Ashen.Generated.Core2RunSnapshotKeys.GameProfile;

        private static Dictionary<string, JToken> Snapshot(JObject run) => run.Properties().Where(p => !Skipped(p.Name)).ToDictionary(p => p.Name, p => p.Value.DeepClone());

        /// <summary>{ set, del } over the run's top-level keys: what an operation changed (the stream counters aside).</summary>
        private static JObject Changes(Dictionary<string, JToken> before, JObject run)
        {
            var set = new JObject();
            var del = new JArray();
            foreach (var p in run.Properties())
            {
                if (Skipped(p.Name)) continue;
                if (!before.TryGetValue(p.Name, out var was) || RunParityTests.FirstDifference(was, p.Value, "$") != null) set[p.Name] = p.Value.DeepClone();
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

        private static JToken Failure(string error) => new JObject { ["error"] = error };

        private static JToken Refusal(JObject run, string offerId, int index, JObject settings)
        {
            try { return Core2Rewards.DeckAddRefusal(Data, run, offerId, index, settings); }
            catch (InvalidOperationException e) { return Failure(e.Message); }
        }

        /// <summary>The refusals of Add to deck for every option of an offer.</summary>
        private static JArray Refusals(JObject run, string offerId, JObject settings)
        {
            var options = run.Obj("gameProfileRewardOffers").Obj(offerId).Arr("options");
            return new JArray(Enumerable.Range(0, options.Count).Select(i => Refusal(run, offerId, i, settings)));
        }

        // ------------------------------------------------------------------ claims

        [TestCaseSource(nameof(ClaimFiles))]
        public void TheOffersAndClaimsAreTheWebs(string file)
        {
            var sc = Read(file);
            var seed = (uint)sc.Value<long>("seed");
            var run = CoreRunRules.Create(Core.Combat.Run, seed, sc.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
            var rng = CoreSnapshot.RngFromJson(seed, run.Obj("streamCounters"));
            JObject settings = new JObject();
            var n = 0;
            foreach (var step in ((JArray)sc["steps"]).OfType<JObject>())
            {
                var op = step.Value<string>("op");
                var at = file + " step " + n++ + " " + op;
                var before = Snapshot(run);
                switch (op)
                {
                    case "drop":
                        var ids = ((JArray)step["ids"]).Select(t => t.Value<string>()).ToList();
                        run["deck"] = new JArray(run.Arr("deck").Where(c => !ids.Contains(c.Value<string>("instanceId"))));
                        RunState.SyncZones(Data.RunData, run);
                        break;
                    case "range":
                        var policy = (JObject)run["rangePolicy"].DeepClone();
                        var enabled = policy["enabled"];
                        policy["enabled"] = enabled != null && enabled.Type == JTokenType.Boolean && !enabled.Value<bool>();
                        run["rangePolicy"] = policy;
                        Assert.That(policy.Value<bool>("enabled"), Is.EqualTo(step.Value<bool>("enabled")), at);
                        break;
                    case "settings":
                        settings = (JObject)step["settings"].DeepClone();
                        break;
                    case "offer":
                    case "offerAgain":
                        var offer = Try(() => (JToken)Core2Rewards.Offer(Data, run, rng, step.Value<string>("offerId"), step.Value<string>("source")).DeepClone(), out var offerError);
                        Same(at, step["result"] ?? Failure(step.Value<string>("error")), offer ?? Failure(offerError));
                        break;
                    case "refusals":
                    case "refusalsAfter":
                        Assert.That(((JObject)step["settings"]).ToString(), Is.EqualTo(settings.ToString()), at + " settings");
                        Same(at, step["refusals"], Refusals(run, step.Value<string>("offerId"), settings));
                        break;
                    case "take":
                        var taken = Try(() => (JToken)Core2Rewards.Claim(Data, run, step.Value<string>("offerId"), step.Value<int>("index")), out var takeError);
                        Same(at, step["result"] ?? Failure(step.Value<string>("error")), taken ?? Failure(takeError));
                        break;
                    case "add":
                        Assert.That(((JObject)step["settings"]).ToString(), Is.EqualTo(settings.ToString()), at + " settings");
                        var added = Try(() => (JToken)Core2Rewards.ClaimToDeck(Data, run, step.Value<string>("offerId"), step.Value<int>("index"), settings), out var addError);
                        Same(at, step["result"] ?? Failure(step.Value<string>("error")), added ?? Failure(addError));
                        break;
                    default:
                        Assert.Fail(at + " unknown op");
                        break;
                }
                if (step["changes"] != null) Same(at + " changes", step["changes"], Changes(before, run));
                Counters(at, step, rng);
            }
            Same(file + " the run document", sc["run"], Document(run));
            Assert.That(Live(rng), Is.EqualTo(Want(sc["counters"])), file + " final counters");
        }

        // ------------------------------------------------------------------ climbs

        [TestCaseSource(nameof(ClimbFiles))]
        public void TheCombatEndAndTheDoorAreTheWebs(string file)
        {
            var sc = Read(file);
            var core = Core;
            var seed = (uint)sc.Value<long>("seed");
            var run = Core2RunLoop.NewClimb(core.Loop, seed, sc.Value<string>("classId"), Unlocked, out var rng);
            JObject settings = new JObject();
            var n = 0;
            foreach (var step in ((JArray)sc["steps"]).OfType<JObject>())
            {
                var op = step.Value<string>("op");
                var at = file + " step " + n++ + " " + op;
                var before = Snapshot(run);
                switch (op)
                {
                    case "heal":
                        run["hp"] = run["maxHp"].DeepClone();
                        break;
                    case "prime":
                        var skills = run.Obj("skills") ?? (JObject)(run["skills"] = new JObject());
                        skills[step.Value<string>("skillId")] = new JObject { ["xp"] = step["xp"].DeepClone(), ["level"] = step["level"].DeepClone(), ["pendingDrafts"] = 0 };
                        break;
                    case "node":
                        run["mapNodeId"] = step["id"].DeepClone();
                        run["floor"] = step["floor"].DeepClone();
                        break;
                    case "settings":
                        settings = (JObject)step["settings"].DeepClone();
                        break;
                    case "fight":
                        Fight(at, core, run, ref rng, step);
                        break;
                    case "ack":
                        {
                            var result = Core2RewardDoor.Acknowledge(run);
                            Assert.That(result.Next, Is.EqualTo(step["result"].Value<string>("next")), at);
                            break;
                        }
                    case "refusals":
                        Assert.That(((JObject)step["settings"]).ToString(), Is.EqualTo(settings.ToString()), at + " settings");
                        Same(at, step["refusals"], Refusals(run, step.Value<string>("offerId"), settings));
                        break;
                    case "take":
                        {
                            var taken = Try(() => (JToken)Core2Rewards.Claim(core.Rewards, run, step.Value<string>("offerId"), step.Value<int>("index")), out var error);
                            Same(at, step["result"] ?? Failure(step.Value<string>("error")), taken ?? Failure(error));
                            break;
                        }
                    case "add":
                        {
                            Assert.That(((JObject)step["settings"]).ToString(), Is.EqualTo(settings.ToString()), at + " settings");
                            var added = Try(() => (JToken)Core2Rewards.ClaimToDeck(core.Rewards, run, step.Value<string>("offerId"), step.Value<int>("index"), settings), out var error);
                            Same(at, step["result"] ?? Failure(step.Value<string>("error")), added ?? Failure(error));
                            break;
                        }
                    case "leave":
                        {
                            var result = Core2RewardDoor.Leave(core.Rewards, run, rng);
                            Assert.That(result.Next, Is.EqualTo(step["result"].Value<string>("next")), at);
                            if (step["result"]["offerId"] != null) Assert.That(result.OfferId, Is.EqualTo(step["result"].Value<string>("offerId")), at + " offer");
                            break;
                        }
                    case "advanceAct":
                        Core2RunLoop.AdvanceAct(core.Loop, run, rng);
                        break;
                    case "end":
                        break;
                    default:
                        Assert.Fail(at + " unknown op");
                        break;
                }
                if (step["changes"] != null) Same(at + " changes", step["changes"], Changes(before, run));
                Counters(at, step, rng);
            }
            if (sc["run"].Type != JTokenType.Null) Same(file + " the run document", sc["run"], Document(run));
        }

        /// <summary>One fight: the encounter on the run's streams, the recorded intents, then the combat end as the web settles it.</summary>
        private static void Fight(string at, Core2Content core, JObject run, ref Rng rng, JObject step)
        {
            var encounterId = step.Value<string>("encounterId");
            var session = Core2CombatSession.Start(core.Combat, rng, run, encounterId);
            var weaken = step.Value<double>("weaken");
            if (weaken > 0) foreach (var enemy in session.State.Enemies) enemy["hp"] = Math.Min(enemy.Value<double>("hp"), weaken);
            var intents = ((JArray)step["intents"]).Select(t => JObject.Parse(t.Value<string>())).ToList();
            for (var i = 0; i < intents.Count; i++)
            {
                var outcome = session.Execute(intents[i]);
                Assert.That(outcome.Accepted, Is.True, at + " intent " + i + " " + intents[i].ToString(Newtonsoft.Json.Formatting.None) + " " + outcome.Refusal?.Message);
            }
            Assert.That(session.IsOver, Is.True, at + " the fight is over after its recorded intents");
            rng = session.State.Rng;
            var encounter = core.Loop.Map.Encounters.Get(encounterId);
            var receipt = Core2CombatEnd.Settle(core.Rewards, run, rng, session.State, encounter);
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
            Same(at + " the door", step["door"], (JToken)Core2RewardDoor.Pending(run) ?? JValue.CreateNull());
            var offerId = Core2RewardDoor.Pending(run)?.Value<string>("offerId");
            Same(at + " the offer", step["offer"], offerId != null && run.Obj("gameProfileRewardOffers")?[offerId] != null ? run.Obj("gameProfileRewardOffers")[offerId] : JValue.CreateNull());
        }
    }
}
