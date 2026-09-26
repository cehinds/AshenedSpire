using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Domain.Loop;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// Unbroken C# runs (us-8.10, D-101i): every run Tools/oracle-loop.mjs recorded from the SHIPPED controller is made in
    /// C# (<see cref="RunLoop.NewRun"/> over createRunState, D-102i — it must equal the recorded starting documents) and
    /// played through ONE <see cref="LoopContext"/> — the run, the RNG and the profile the C# ports
    /// produce are carried forward through every step, never reset to the recorded ones — and after every step the
    /// chained documents must equal the recorded ones strictly (presence, values, key order), as must every outcome.
    /// Unlike <see cref="RunLoopParityTests"/>, the external steps are driven through the real ports: a merchant visit
    /// through <see cref="Shop.Open"/> and Leave (the harness rolls the stock and leaves without buying), an event through
    /// <see cref="Events.Open"/> (its affordable open choices must be the ones the harness saw) and
    /// <see cref="Events.Choose"/> with the recorded choice; a fight the choice starts is the next, checked, step
    /// (<see cref="RunLoop.EnterEventCombat"/>). A rest place opens where the chained run stands (the enter outcome's
    /// place, or — inside a legacy dungeon — where the last stay stood), and the context's remembered place must be the
    /// recorded one.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ChainedRunTests
    {
        private static LoopData Data => RunLoopParityTests.Data;

        public static IEnumerable<string> SequenceFiles() => RunLoopParityTests.SequenceFiles();

        [Test]
        public void TheRecordedExternalStepsCarryTheirInputs()
        {
            var events = 0;
            var merchants = 0;
            foreach (var file in SequenceFiles())
            {
                var seq = RunLoopParityTests.ReadJson(Path.Combine(RunLoopParityTests.LoopDir, file));
                foreach (var step in ((JArray)seq["steps"]).OfType<JObject>().Where(s => s.Value<string>("k") == "x"))
                {
                    var i = (JObject)step["i"];
                    if (i.Value<string>("what") == "merchant") { merchants++; continue; }
                    Assert.That(i.Value<string>("what"), Is.EqualTo("event"), file);
                    Assert.That(i["eventId"]?.Type, Is.EqualTo(JTokenType.String), file);
                    Assert.That(i["open"], Is.InstanceOf<JArray>(), file);
                    Assert.That(i["choiceId"], Is.Not.Null, file);
                    events++;
                }
            }
            Assert.That(merchants, Is.GreaterThan(100));
            Assert.That(events, Is.GreaterThan(200));
        }

        [TestCaseSource(nameof(SequenceFiles))]
        public void RunChainsEndToEnd(string file)
        {
            var seq = RunLoopParityTests.ReadJson(Path.Combine(RunLoopParityTests.LoopDir, file));
            var seed = (uint)seq["seed"].Value<long>();
            var resolved = (JObject)seq["resolved"];
            var settings = new LoopSettings
            {
                PointsPerLevel = resolved["pointsPerLevel"]?.DeepClone(),
                MultiUse = resolved.Value<bool>("multiUse"),
                RewardCollect = resolved.Value<string>("rewardCollect"),
                RefillCounts = (JObject)resolved["refillCounts"].DeepClone(),
            };
            var wantRun = (JObject)seq["init"]["run"].DeepClone();
            var wantRng = (JObject)seq["init"]["rng"].DeepClone();
            var wantProfile = (JObject)seq["init"]["profile"].DeepClone();
            var ctx = Start(seq, seed, settings, (JObject)wantProfile.DeepClone());
            var rng = ctx.Rng;
            RunLoopParityTests.Check(file + " newRun", "run", wantRun, ctx.Run);
            RunLoopParityTests.Check(file + " newRun", "rng", wantRng, RunLoopParityTests.CountersJson(rng));
            RunLoopParityTests.Check(file + " newRun", "profile", wantProfile, ctx.Profile);
            FightEntry fight = null;
            NodeOutcome lastOutcome = null;
            var steps = (JArray)seq["steps"];
            for (var s = 0; s < steps.Count; s++)
            {
                var step = (JObject)steps[s];
                var kind = step.Value<string>("k");
                var label = $"{file} chained step {s} ({kind})";
                wantRun = (JObject)RunLoopParityTests.Patch.Apply(wantRun, step["r"]);
                wantRng = (JObject)RunLoopParityTests.Patch.Apply(wantRng, step["g"]);
                wantProfile = (JObject)RunLoopParityTests.Patch.Apply(wantProfile, step["p"]);
                JToken got;
                switch (kind)
                {
                    case "x":
                        External(ctx, step, label, lastOutcome);
                        got = null;
                        break;
                    case "rest":
                    {
                        // The harness's showRest(null) inside a dungeon stands where the last stay stood; elsewhere the
                        // place is the one the chained enter outcome named.
                        var inDungeon = ctx.Run["legacyDungeon"] is JObject dungeon && Js.Truthy(dungeon["activeRest"]);
                        var location = inDungeon ? null : lastOutcome?.Location;
                        got = RunLoopParityTests.Rest(ctx, step, label, location);
                        Assert.That(ctx.RestLocationId, Is.EqualTo(step["i"].Value<string>("location")), label + " rest place");
                        break;
                    }
                    default:
                        got = RunLoopParityTests.Apply(ctx, step, label, ref fight, ref lastOutcome);
                        break;
                }
                if (step["o"] != null) RunLoopParityTests.Check(label, "outcome", step["o"], got);
                RunLoopParityTests.Check(label, "run", wantRun, ctx.Run);
                RunLoopParityTests.Check(label, "rng", wantRng, RunLoopParityTests.CountersJson(rng));
                RunLoopParityTests.Check(label, "profile", wantProfile, ctx.Profile);
            }
            Assert.That(ctx.Fight, Is.Null, file + ": a fight was left open at the end of the run");
        }

        private static SeedCodec _codec;

        internal static SeedCodec Codec
        {
            get
            {
                if (_codec != null) return _codec;
                var rules = TestContent.ContentJson(Ashen.Generated.ContentFiles.RulesRng)["seed"];
                return _codec = new SeedCodec((string)rules["alphabet"],
                    rules["homoglyphs"].Select(p => new KeyValuePair<string, string>((string)p[0], (string)p[1])).ToList(), (int)rules["maxLength"]);
            }
        }

        /// <summary>
        /// The recorded run's start made in C#: newRun with the recorded inputs and the settings resolved as recorded, then
        /// the harness's own edits after it (carried armaments put in storage; rest relics granted, flask growth synced).
        /// </summary>
        private static LoopContext Start(JObject seq, uint seed, LoopSettings settings, JObject profile)
        {
            var start = (JObject)seq["start"];
            var resolved = (JObject)seq["resolved"];
            var ctx = RunLoop.NewRun(Data, profile, settings, new NewRunOptions
            {
                ClassId = start.Value<string>("classId"),
                Seed = seed,
                SeedString = Codec.Format(seed),
                Custom = start["custom"] as JObject,
                KeepsakeId = start.Value<string>("keepsakeId"),
                Creation = new RunOptions { DerivedStatOptions = (JObject)resolved["derivedStatOptions"] },
                AdvancedConfigSnapshot = (JObject)resolved["advancedConfigSnapshot"],
                Prologue = resolved.Value<bool>("prologue"),
            });
            var run = ctx.Run;
            if (start["storage"] is JArray storage)
            {
                var loadout = (JObject)run["loadout"];
                var held = loadout["storage"] as JArray ?? new JArray();
                var next = new JArray(held.Select(t => t.DeepClone()));
                foreach (var id in storage) if (!held.Any(t => JToken.DeepEquals(t, id))) next.Add(id.DeepClone());
                loadout["storage"] = next;
            }
            if (start["relics"] is JArray relics)
            {
                foreach (var id in relics) if (!((JArray)run["relics"]).Any(t => JToken.DeepEquals(t, id))) ((JArray)run["relics"]).Add(id.DeepClone());
                Ashen.Domain.Run.Creation.SyncFlaskGrowth(Data.Run, run);
            }
            return ctx;
        }

        /// <summary>
        /// A merchant visit (the harness: enterNode's stock roll and price multiplier, the smith's roll, then Leave with
        /// nothing bought) or an event (the harness: nothing for a quest chain's step; otherwise one of the open,
        /// affordable choices through commitEventChoice, its fight left on the run for the next step).
        /// </summary>
        private static void External(LoopContext ctx, JObject step, string label, NodeOutcome lastOutcome)
        {
            var i = (JObject)step["i"];
            var what = i.Value<string>("what");
            if (what == "merchant")
            {
                Assert.That(lastOutcome?.Kind, Is.EqualTo("merchant"), label + " (the chained travel did not reach a merchant)");
                Shop.Open(Data.Shop, ctx.Run, ctx.Rng);
                var left = Shop.Execute(Data.Shop, ctx.Run, new ShopAction { Kind = "leave" }, true);
                Assert.That(left.Ok, Is.True, label + " leave");
                return;
            }
            Assert.That(what, Is.EqualTo("event"), label);
            var eventId = i.Value<string>("eventId");
            Assert.That(lastOutcome?.Kind, Is.EqualTo("event"), label + " (the chained travel did not reach an event)");
            Assert.That(lastOutcome.EventId, Is.EqualTo(eventId), label + " event id");
            var questStep = Quests.QuestChainForEvent(Data.Events, eventId) != null;
            Assert.That(questStep, Is.EqualTo(i.Value<bool>("quest")), label + " quest chain");
            if (questStep) return;
            var view = Events.Open(Data.Events, ctx.Run, eventId);
            var open = new JArray(((JArray)view["choices"]).OfType<JObject>().Where(c => c.Value<bool>("affordable")).Select(c => c["choiceId"].DeepClone()));
            RunLoopParityTests.Check(label, "open choices", i["open"], open);
            var choiceId = i.Value<string>("choiceId");
            if (choiceId == null) return;
            var result = Events.Choose(Data.Events, ctx.Run, eventId, choiceId, ctx.Rng);
            Assert.That(result.Ok, Is.True, label + " choice refused: " + result.Refusal?.ToJson());
        }
    }
}
