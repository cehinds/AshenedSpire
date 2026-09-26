using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-8.1/8.3–8.6, US-4.4–4.9, US-13.1/13.2 run-loop parity (PF-04/05; D-036, D-037, D-040): every sequence
    /// Tools/oracle-loop.mjs recorded from the SHIPPED controller (a run walked node by node: travel, fights and their
    /// ends, reward doors, rest places and their actions, act advances, legacy dungeons, the run's close-out) is replayed
    /// step by step through Ashen.Domain.Loop on content-built data. Each checked step starts from the recorded documents
    /// before it (run, RNG counters, profile), applies the port, and must produce the recorded outcome and the recorded
    /// documents after it, strictly (presence, values as doubles, key order). External steps (an event's choice, a
    /// merchant visit) only advance the documents here; <see cref="ChainedRunTests"/> drives them through the shop and
    /// events ports in one unbroken C# run.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class RunLoopParityTests
    {
        internal static string LoopDir => Path.Combine(TestContent.OracleRoot, "loop");

        internal static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static LoopData _data;

        internal static LoopData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var content = snapshot.Content;
                var registries = RuntimeRegistries.Build(content, (JObject)content.Get(ContentFiles.StringsEn));
                return _data = registries.ToLoopData(
                    (JObject)content.Get(ContentFiles.RulesMechanics), (JObject)content.Get(ContentFiles.RulesCombatEngine),
                    (JObject)content.Get(ContentFiles.RulesHandRules), (JObject)content.Get(ContentFiles.RulesRunEngine),
                    snapshot.ContentVersion, (JArray)content.Get(ContentFiles.BalanceCustomRun)["ASCENSION_ORDER"],
                    (JObject)content.Get(ContentFiles.RulesRewardsEngine), (JObject)content.Get(ContentFiles.RulesShopEngine),
                    (JObject)content.Get(ContentFiles.RulesEventsEngine), (JObject)content.Get(ContentFiles.RulesMapEngine),
                    (JObject)content.Get(ContentFiles.RulesLoopEngine));
            }
        }

        public static IEnumerable<string> SequenceFiles() =>
            Directory.Exists(LoopDir)
                ? Directory.GetFiles(LoopDir, "seq-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        [Test]
        public void TheOracleIsPresent()
        {
            var index = ReadJson(Path.Combine(LoopDir, "index.json"));
            var sequences = (JArray)index["sequences"];
            Assert.That(SequenceFiles().Count(), Is.EqualTo(sequences.Count));
            Assert.That(sequences.Count, Is.GreaterThanOrEqualTo(100));
            var tally = (JObject)index["tally"];
            foreach (var key in new[] { "enter:fight", "enter:rest", "enter:treasure", "enter:merchant", "enter:event", "enter:dungeon", "fight:reward", "fight:defeat",
                         "fight:victory", "claim", "act", "rest", "dungeon:dialogue", "dungeon:victory", "dungeon:advanced", "eventFight:fight" })
                Assert.That(tally.Value<int?>(key) ?? 0, Is.GreaterThan(0), key);
        }

        /// <summary>
        /// The deferred reachable paths throw by name: a World Journey run's travel (D-091) and a rest for a run without
        /// flask charge pools (the legacy flask-slot refill, D-094).
        /// </summary>
        [Test]
        public void DeferredPathsThrowByName()
        {
            var seq = ReadJson(Path.Combine(LoopDir, SequenceFiles().First()));
            var seed = (uint)seq["seed"].Value<long>();
            LoopContext Context(Action<JObject> edit)
            {
                var run = (JObject)seq["init"]["run"].DeepClone();
                edit(run);
                return new LoopContext(Data, run, new Rng(seed, Counters((JObject)seq["init"]["rng"])), (JObject)seq["init"]["profile"].DeepClone());
            }
            var journey = Context(run => run["journey"] = new JObject());
            var start = journey.Run["mapGraph"]["startIds"][0].Value<string>();
            var e = Assert.Throws<NotSupportedException>(() => RunLoop.EnterNode(journey, start));
            Assert.That(e.Message, Is.EqualTo(LoopMessages.JourneyDeferred));
            e = Assert.Throws<NotSupportedException>(() => RestVisit.Open(journey, "shrine"));
            Assert.That(e.Message, Is.EqualTo(LoopMessages.JourneyDeferred));
            var legacy = Context(run => run.Remove("flaskCharges"));
            e = Assert.Throws<NotSupportedException>(() => RestVisit.Open(legacy, "shrine"));
            Assert.That(e.Message, Is.EqualTo(EventMessages.LegacyGraceRefillDeferred));
        }

        internal static Dictionary<RngStream, uint> Counters(JObject o) =>
            o.Properties().ToDictionary(p => RngStreamNames.Parse(p.Name), p => (uint)p.Value.Value<long>());

        internal static JObject CountersJson(Rng rng)
        {
            var o = new JObject();
            foreach (var name in RngStreamNames.All) o[name] = rng.Counter(RngStreamNames.Parse(name));
            return o;
        }

        private static CombatCommand Decode(string cmd)
        {
            if (cmd == "e") return CombatCommand.EndTurn();
            if (cmd[0] == 'f') return CombatCommand.UseFlask(0, cmd.Substring(1));
            return CombatCommand.PlayCard(cmd.Substring(1));
        }

        [TestCaseSource(nameof(SequenceFiles))]
        public void SequenceMatchesTheShippedController(string file)
        {
            var seq = ReadJson(Path.Combine(LoopDir, file));
            var seed = (uint)seq["seed"].Value<long>();
            var resolved = (JObject)seq["resolved"];
            var settings = new LoopSettings
            {
                PointsPerLevel = resolved["pointsPerLevel"]?.DeepClone(),
                MultiUse = resolved.Value<bool>("multiUse"),
                RewardCollect = resolved.Value<string>("rewardCollect"),
                RefillCounts = (JObject)resolved["refillCounts"].DeepClone(),
            };
            var run = (JObject)seq["init"]["run"].DeepClone();
            var counters = (JObject)seq["init"]["rng"].DeepClone();
            var profile = (JObject)seq["init"]["profile"].DeepClone();
            FightEntry fight = null;
            NodeOutcome lastOutcome = null;
            var steps = (JArray)seq["steps"];
            for (var s = 0; s < steps.Count; s++)
            {
                var step = (JObject)steps[s];
                var kind = step.Value<string>("k");
                var label = $"{file} step {s} ({kind})";
                var wantRun = (JObject)Patch.Apply(run, step["r"]);
                var wantRng = (JObject)Patch.Apply(counters, step["g"]);
                var wantProfile = (JObject)Patch.Apply(profile, step["p"]);
                if (kind != "x")
                {
                    var rng = new Rng(seed, Counters(counters));
                    var ctx = new LoopContext(Data, (JObject)run.DeepClone(), rng, (JObject)profile.DeepClone(), settings) { Fight = fight };
                    var got = Apply(ctx, step, label, ref fight, ref lastOutcome);
                    if (step["o"] != null) Check(label, "outcome", step["o"], got);
                    Check(label, "run", wantRun, ctx.Run);
                    Check(label, "rng", wantRng, CountersJson(rng));
                    Check(label, "profile", wantProfile, ctx.Profile);
                }
                run = wantRun;
                counters = wantRng;
                profile = wantProfile;
            }
        }

        /// <summary>Applies one checked step through the port; returns its outcome document.</summary>
        internal static JToken Apply(LoopContext ctx, JObject step, string label, ref FightEntry fight, ref NodeOutcome lastOutcome)
        {
            var i = (JObject)step["i"];
            switch (step.Value<string>("k"))
            {
                case "enter":
                {
                    var out_ = RunLoop.EnterNode(ctx, i.Value<string>("node"));
                    fight = ctx.Fight;
                    lastOutcome = out_;
                    return out_.ToJson();
                }
                case "eventFight":
                {
                    var out_ = RunLoop.EnterEventCombat(ctx);
                    fight = ctx.Fight;
                    return out_?.ToJson();
                }
                case "fight":
                {
                    var args = (JObject)ctx.Fight.Args.DeepClone();
                    args["hpMult"] = Js.N(Js.D(args["hpMult"]) * i.Value<double>("hp"));
                    var combat = CombatStart.Create(Data.Combat, ctx.Rng, args);
                    foreach (var cmd in ((JArray)i["cmds"]).Select(t => t.Value<string>()))
                        CombatEngine.Dispatch(combat, Decode(cmd));
                    Assert.That(combat.Result, Is.EqualTo(i.Value<string>("result")), label + " fight result");
                    var out_ = RunLoop.EndCombat(ctx, combat, combat.Result);
                    fight = null;
                    return out_.ToJson();
                }
                case "claim":
                {
                    var door = i.Value<string>("from") == "pending"
                        ? RewardDoor.OpenPending(ctx)
                        : RewardDoor.Open(ctx, lastOutcome.Rewards, null, "treasure");
                    return door.Continue(i.Value<string>("mode")).ToJson();
                }
                case "act":
                    Acts.Advance(ctx);
                    return new JObject();
                case "rest":
                    return Rest(ctx, step, label, i.Value<string>("location"));
                case "dungeon":
                {
                    NodeOutcome out_;
                    switch (i.Value<string>("op"))
                    {
                        case "inspect": out_ = LegacyDungeons.EnterLocation(ctx); break;
                        case "travel": out_ = LegacyDungeons.TravelTo(ctx, i.Value<string>("to")); break;
                        case "continue": out_ = LegacyDungeons.ContinueDialogue(ctx); break;
                        case "leave": out_ = LegacyDungeons.Leave(ctx); break;
                        case "choose":
                            return LegacyDungeons.Choose(ctx.Data, ctx.Run, i.Value<string>("choiceId"), ctx.Rng).DeepClone();
                        default: throw new InvalidOperationException(label);
                    }
                    fight = ctx.Fight;
                    return out_.ToJson();
                }
                default:
                    throw new InvalidOperationException(label + ": unknown step kind");
            }
        }

        /// <summary>A rest stay opened at <paramref name="location"/> (null: where the last one stood) and driven by the recorded actions.</summary>
        internal static JToken Rest(LoopContext ctx, JObject step, string label, string location)
        {
            var i = (JObject)step["i"];
            var o = (JObject)step["o"];
            var visit = RestVisit.Open(ctx, location);
            var opened = visit.ToJson();
            var receipts = new JArray();
            var actions = (JArray)i["actions"];
            var recorded = (JArray)o["receipts"];
            for (var a = 0; a < recorded.Count; a++)
            {
                var want = (JObject)recorded[a];
                var action = (JObject)actions[a];
                var refused = want["refused"] != null;
                JObject got;
                switch (want.Value<string>("op"))
                {
                    case "rest": got = visit.Rest(); break;
                    case "flask": got = visit.MoveFlask(action.Value<string>("kind"), action.Value<double>("step")); break;
                    case "level": got = visit.AssignPoints(refused ? new JObject() : (JObject)want["assigned"]); break;
                    case "smith":
                        got = refused ? Js.Obj("op", "smith", "refused", visit.SmithRefusal()) : visit.SmithItem(want.Value<string>("itemRef"));
                        break;
                    case "extract":
                        got = refused ? Js.Obj("op", "extract", "refused", visit.CardServiceRefusal("extract")) : visit.Extract(want.Value<string>("itemRef"), want.Value<string>("mountKey"));
                        break;
                    case "install":
                        got = refused ? Js.Obj("op", "install", "refused", visit.CardServiceRefusal("install"))
                            : visit.Install(want.Value<string>("itemRef"), want.Value<string>("mountKey"), want.Value<string>("instanceId"));
                        break;
                    default: throw new InvalidOperationException(label + ": unknown rest action");
                }
                receipts.Add(got);
                if (visit.Left) break;
            }
            visit.Leave();
            return Js.Obj("opened", opened, "receipts", receipts);
        }

        // ------------------------------------------------------------------ patches and comparison

        /// <summary>The oracle's document patches (Tools/oracle-loop.mjs diff/applyPatch): replace, object, array.</summary>
        internal static class Patch
        {
            public static JToken Apply(JToken a, JToken p)
            {
                if (p == null) return a.DeepClone();
                var op = p[0].Value<string>();
                if (op == "=") return p[1].DeepClone();
                if (op == "o")
                {
                    var src = (JObject)a;
                    var sets = (JObject)p[1];
                    var output = new JObject();
                    foreach (var prop in src.Properties()) output[prop.Name] = prop.Value.DeepClone();
                    foreach (var prop in sets.Properties()) output[prop.Name] = Apply(src[prop.Name], prop.Value);
                    if (p[2] is JArray del) foreach (var k in del) output.Remove(k.Value<string>());
                    if (!(p[3] is JArray order)) return output;
                    var ordered = new JObject();
                    foreach (var k in order) ordered[k.Value<string>()] = output[k.Value<string>()];
                    return ordered;
                }
                var arr = (JArray)a;
                var keep = p[1].Value<int>();
                var result = new JArray(arr.Take(keep).Select(t => t.DeepClone()));
                foreach (var prop in ((JObject)p[2]).Properties())
                {
                    var index = int.Parse(prop.Name, CultureInfo.InvariantCulture);
                    result[index] = Apply(result[index], prop.Value);
                }
                foreach (var t in (JArray)p[3]) result.Add(t.DeepClone());
                return result;
            }
        }

        internal static void Check(string label, string what, JToken want, JToken got)
        {
            var diff = RewardsParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{label} {what}: first difference at {diff}");
        }
    }
}
