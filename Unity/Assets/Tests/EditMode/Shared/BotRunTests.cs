using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Domain.Loop;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using LoopSmithing = Ashen.Domain.Loop.ItemSmithing;

namespace Ashen.Tests
{
    /// <summary>
    /// A seeded bot plays complete Classic runs in C# only (us-8.10, D-103; the seed of docs/design/09 F4's "bot runs 200
    /// Classic seeds clean"): <see cref="RunLoop.NewRun"/> → the map → fights (a legal-move policy asked of
    /// <see cref="CombatLegality"/>) → reward doors → rest places, merchants and events (fixed policies driven by the
    /// bot's own LCG, never the game RNG) → act transitions and legacy dungeons → the summit or death. For every seed ×
    /// playable class it asserts: no exception, a terminal state within a step bound, the same final state hash when
    /// replayed, and after every step the invariants (HP within [0, maxHp], deck instance ids unique except the run-level
    /// door's <c>run&lt;n&gt;</c> ids — the shipped defect D-099 — and RNG counters that never go back).
    /// </summary>
    [TestFixture]
    public class BotRunTests
    {
        private const int StepBound = 1500;
        private const int CommandBound = 4000;

        private static readonly Dictionary<string, LoopData> DataByPreset = new Dictionary<string, LoopData>(StringComparer.Ordinal);

        private static LoopData Data(string preset)
        {
            if (DataByPreset.TryGetValue(preset, out var cached)) return cached;
            var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = preset });
            var content = snapshot.Content;
            var registries = RuntimeRegistries.Build(content, (JObject)content.Get(ContentFiles.StringsEn));
            return DataByPreset[preset] = registries.ToLoopData(
                (JObject)content.Get(ContentFiles.RulesMechanics), (JObject)content.Get(ContentFiles.RulesCombatEngine),
                (JObject)content.Get(ContentFiles.RulesHandRules), (JObject)content.Get(ContentFiles.RulesRunEngine),
                snapshot.ContentVersion, (JArray)content.Get(ContentFiles.BalanceCustomRun)["ASCENSION_ORDER"],
                (JObject)content.Get(ContentFiles.RulesRewardsEngine), (JObject)content.Get(ContentFiles.RulesShopEngine),
                (JObject)content.Get(ContentFiles.RulesEventsEngine), (JObject)content.Get(ContentFiles.RulesMapEngine),
                (JObject)content.Get(ContentFiles.RulesLoopEngine));
        }

        /// <summary>The default preset (Classic's tuning) unless named.</summary>
        private static string DefaultPreset => new ConfigLayers(TestContent.Source).Build(new LayerSelection()).PresetId;

        /// <summary>The classes that can begin a run under the preset (the reference preset's Reaver cannot, D-073).</summary>
        private static List<string> PlayableClasses(LoopData d) =>
            d.Run.Classes.Ids.Where(id =>
            {
                try { RunState.Create(d.Run, 1, id); return true; }
                catch (InvalidOperationException) { return false; }
            }).ToList();

        /// <summary>A bot policy: the enemy HP ratio per door (1 = the game's own; an assisted bot reaches the summit more often).</summary>
        public sealed class Policy
        {
            public string Name;
            public double Normal = 1;
            public double Elite = 1;
            public double Boss = 1;

            public double For(string pool) => pool == "boss" ? Boss : pool == "elite" ? Elite : Normal;

            public override string ToString() => Name;
        }

        private static readonly Policy Fair = new Policy { Name = "fair" };
        private static readonly Policy Assisted = new Policy { Name = "assisted", Normal = 0.35, Elite = 0.25, Boss = 0.12 };

        /// <summary>What one bot run ended as.</summary>
        public sealed class BotResult
        {
            public string Outcome;
            public int Steps;
            public int Fights;
            public int Acts;
            public string Hash;
            public double Millis;
            public Dictionary<string, int> Seen;
        }

        // ------------------------------------------------------------------ tests

        public static IEnumerable<TestCaseData> SmokeCases()
        {
            foreach (var seed in new uint[] { 11, 2027, 90210 })
                foreach (var policy in new[] { Fair, Assisted })
                    yield return new TestCaseData(seed, policy).SetName($"BotPlaysEveryPlayableClass(seed {seed}, {policy.Name})");
        }

        /// <summary>Every playable class of the default preset, one run per seed and policy, played twice (determinism).</summary>
        [TestCaseSource(nameof(SmokeCases))]
        public void BotPlaysEveryPlayableClass(uint seed, Policy policy)
        {
            var preset = DefaultPreset;
            var d = Data(preset);
            var classes = PlayableClasses(d);
            Assert.That(classes.Count, Is.GreaterThanOrEqualTo(3), "playable classes under " + preset);
            foreach (var classId in classes)
            {
                var first = Play(d, seed, classId, policy);
                var again = Play(d, seed, classId, policy);
                Assert.That(again.Hash, Is.EqualTo(first.Hash), $"{preset} {classId} seed {seed}: the same seed ended in a different state");
                Assert.That(again.Steps, Is.EqualTo(first.Steps));
                TestContext.Progress.WriteLine($"bot {preset} {policy.Name} {classId} seed {seed}: {first.Outcome} after {first.Steps} steps, {first.Fights} fights, act {first.Acts}, {first.Millis:F0} ms");
            }
        }

        /// <summary>The same bot on the shipped preset, where every class (the Reaver too) can begin.</summary>
        [Test]
        public void BotPlaysTheShippedPresetForAllFourClasses()
        {
            var d = Data("shipped");
            var classes = PlayableClasses(d);
            Assert.That(classes.Count, Is.EqualTo(4));
            foreach (var classId in classes)
            {
                var r = Play(d, 424242, classId, Assisted);
                TestContext.Progress.WriteLine($"bot shipped assisted {classId}: {r.Outcome} after {r.Steps} steps, {r.Fights} fights, act {r.Acts}, {r.Millis:F0} ms");
            }
        }

        /// <summary>The heavier sweep (explicit, Category Bot; about a minute): 24 seeds × every playable class × both policies, each replayed.</summary>
        [Test, Explicit("Heavier sweep: dotnet test --filter TestCategory=Bot"), Category("Bot")]
        public void BotSweep()
        {
            var d = Data(DefaultPreset);
            var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var seen = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var runs = 0;
            var clock = Stopwatch.StartNew();
            for (uint seed = 1; seed <= 24; seed++)
                foreach (var classId in PlayableClasses(d))
                    foreach (var policy in new[] { Fair, Assisted })
                    {
                        var r = Play(d, seed * 7919, classId, policy);
                        Assert.That(Play(d, seed * 7919, classId, policy).Hash, Is.EqualTo(r.Hash), $"{classId} seed {seed * 7919} {policy.Name}");
                        var key = policy.Name + ":" + r.Outcome;
                        tally[key] = tally.TryGetValue(key, out var n) ? n + 1 : 1;
                        foreach (var p in r.Seen) seen[p.Key] = (seen.TryGetValue(p.Key, out var m) ? m : 0) + p.Value;
                        runs++;
                    }
            TestContext.Progress.WriteLine($"bot sweep: {runs} runs in {clock.Elapsed.TotalSeconds:F1} s: " + string.Join(", ", tally.Select(p => $"{p.Key} {p.Value}"))
                + "; doors: " + string.Join(", ", seen.Select(p => $"{p.Key} {p.Value}")));
            foreach (var door in new[] { "fight", "rest", "treasure", "merchant", "event", "dungeon", "act" })
                Assert.That(seen.TryGetValue(door, out var count) ? count : 0, Is.GreaterThan(0), "the sweep never reached " + door);
            Assert.That(tally.Keys.Any(k => k.EndsWith(":victory", StringComparison.Ordinal)), Is.True, "no run reached the summit");
        }

        // ------------------------------------------------------------------ the bot

        /// <summary>The bot's own LCG (never the engine RNG), as the oracle harnesses use.</summary>
        private sealed class Lcg
        {
            private uint _s;

            public Lcg(uint seed) => _s = seed == 0 ? 1 : seed;

            public int Next(int n)
            {
                _s = unchecked(_s * 1664525u + 1013904223u);
                return (int)(_s % (uint)n);
            }
        }

        private sealed class Run
        {
            public LoopContext Ctx;
            public Policy Policy;
            public Lcg Pick;
            public int Steps;
            public int Fights;
            public string Ended;
            public Dictionary<string, uint> Counters;
            public string Label;
            public readonly Dictionary<string, int> Seen = new Dictionary<string, int>(StringComparer.Ordinal);

            public void Saw(string what) => Seen[what] = Seen.TryGetValue(what, out var n) ? n + 1 : 1;
        }

        private static JObject AdvancedConfigDefault() => new JObject { ["schemaVersion"] = 1, ["ratingsVersion"] = 1, ["overrides"] = new JObject() };

        public static BotResult Play(LoopData d, uint seed, string classId, Policy policy)
        {
            var clock = Stopwatch.StartNew();
            // The shipped saves.ensureProfile() profile of a first launch (as the loop oracle records it).
            var profile = JObject.Parse("{\"schemaVersion\":2,\"settings\":{},\"results\":[],\"discoveredArmaments\":[],\"discoveryReceipts\":[]}");
            var ctx = RunLoop.NewRun(d, profile, new LoopSettings(), new NewRunOptions
            {
                ClassId = classId,
                Seed = seed,
                SeedString = ChainedRunTests.Codec.Format(seed),
                AdvancedConfigSnapshot = AdvancedConfigDefault(),
            });
            var r = new Run { Ctx = ctx, Policy = policy, Pick = new Lcg(seed ^ 0x5bd1e995u), Label = $"{classId} seed {seed} {policy.Name}" };
            r.Counters = CountersOf(ctx.Rng);
            Check(r);
            while (r.Ended == null)
            {
                if (r.Steps > StepBound) Assert.Fail($"{r.Label}: no terminal state within {StepBound} steps ({Where(ctx)})");
                Step(r);
            }
            return new BotResult
            {
                Outcome = r.Ended,
                Steps = r.Steps,
                Fights = r.Fights,
                Acts = (int)ctx.Run.Num("actNumber"),
                Hash = Hash(ctx),
                Millis = clock.Elapsed.TotalMilliseconds,
                Seen = r.Seen,
            };
        }

        private static void Step(Run r)
        {
            var ctx = r.Ctx;
            var run = ctx.Run;
            if (run["legacyDungeon"] is JObject)
            {
                Dungeon(r);
                return;
            }
            var graph = (JObject)run["mapGraph"];
            var at = run["mapNodeId"];
            var options = (Js.Nullish(at) ? graph["startIds"] : graph["nodes"][Js.Str(at)]["next"]) as JArray;
            if (options == null || options.Count == 0) { r.Ended = "map-end"; return; }
            var nodeId = Js.Str(options[r.Pick.Next(options.Count)]);
            var out_ = RunLoop.EnterNode(ctx, nodeId);
            r.Saw(out_.Kind);
            After(r);
            switch (out_.Kind)
            {
                case "fight":
                    Fight(r);
                    return;
                case "dungeon":
                    return;
                case "rest":
                    Rest(r, out_.Location);
                    return;
                case "treasure":
                    Claim(r, RewardDoor.Open(ctx, out_.Rewards, null, "treasure"));
                    return;
                case "merchant":
                    Merchant(r);
                    return;
                case "event":
                    Event(r, out_.EventId);
                    return;
                default:
                    Assert.Fail($"{r.Label}: unhandled node outcome {out_.Kind}");
                    return;
            }
        }

        /// <summary>A fight entered by the loop: played to its end, then its routing (death, the summit, the reward door, the act).</summary>
        private static void Fight(Run r)
        {
            var ctx = r.Ctx;
            var args = (JObject)ctx.Fight.Args.DeepClone();
            args["hpMult"] = Js.N(Js.D(args["hpMult"]) * r.Policy.For(ctx.Fight.Pool));
            var combat = CombatStart.Create(ctx.Data.Combat, ctx.Rng, args);
            var commands = 0;
            while (combat.Result == null)
            {
                if (++commands > CommandBound) Assert.Fail($"{r.Label}: a fight did not end within {CommandBound} commands ({Board(combat)})");
                var command = Choose(r, combat);
                var refusal = CombatLegality.Check(combat, command);
                Assert.That(refusal, Is.Null, $"{r.Label}: the bot chose a refused command ({refusal?.Key})");
                CombatEngine.Dispatch(combat, command);
            }
            r.Fights++;
            var outcome = RunLoop.EndCombat(ctx, combat, combat.Result);
            After(r);
            if (outcome.Outcome == "defeat") { r.Ended = "defeat"; return; }
            if (outcome.Outcome == "victory") { r.Ended = "victory"; return; }
            Claim(r, RewardDoor.OpenPending(ctx));
        }

        /// <summary>
        /// The legal-move policy: a Crimson charge when low, an Azure charge when out of mana; else a legal card — one aimed
        /// at a living enemy first (so a heal-and-block hand cannot stall a fight forever), from a bot-shuffled hand — else
        /// End Turn (discarding the fewest cards the hand rules ask for).
        /// </summary>
        private static CombatCommand Choose(Run r, CombatState c)
        {
            var p = c.Player;
            if (p.Num("hp") * 10 < p.Num("maxHp") * 4 && CombatLegality.CanUseFlask(c, 0, "hp") == null && r.Pick.Next(2) == 0)
                return CombatCommand.UseFlask(0, "hp");
            if (p.Num("mana") <= 0 && p.Num("maxMana") > 0 && CombatLegality.CanUseFlask(c, 0, "mana") == null && r.Pick.Next(2) == 0)
                return CombatCommand.UseFlask(0, "mana");
            if (r.Pick.Next(10) < 9)
            {
                var hand = c.Piles.Hand.Select(card => card.Str("instanceId")).ToList();
                var start = hand.Count == 0 ? 0 : r.Pick.Next(hand.Count);
                var order = Enumerable.Range(0, hand.Count).Select(k => hand[(start + k) % hand.Count]).ToList();
                foreach (var aimed in new[] { true, false })
                    foreach (var id in order)
                    {
                        var targets = CombatLegality.Targets(c, id);
                        if ((targets.Count > 0) != aimed) continue;
                        var target = targets.Count > 0 ? targets[r.Pick.Next(targets.Count)] : null;
                        if (CombatLegality.CanPlay(c, id, target) == null) return CombatCommand.PlayCard(id, target);
                    }
            }
            var plan = HandRules.Plan(c);
            var discards = plan.Cards.Take((int)plan.Minimum).Select(card => card.Str("instanceId")).ToList();
            return CombatCommand.EndTurn(discards);
        }

        /// <summary>A reward door: Continue in the collect mode; a boss's spoils lead into the next act.</summary>
        private static void Claim(Run r, RewardDoor door)
        {
            var receipt = door.Continue(r.Ctx.Settings.RewardCollect);
            After(r);
            if (receipt.After == "advanceAct")
            {
                r.Saw("act");
                Acts.Advance(r.Ctx);
                After(r);
            }
        }

        /// <summary>A rest place: spend waiting level points, sometimes move a flask charge, smith when affordable, Rest, leave.</summary>
        private static void Rest(Run r, string location)
        {
            var ctx = r.Ctx;
            var visit = RestVisit.Open(ctx, location);
            After(r);
            var waiting = LevelPoints.Waiting(ctx.Run);
            if (waiting > 0)
            {
                var attrs = CreationStats.OrderedAttributes(ctx.Data.Run).Select(a => a.Str("id")).ToList();
                visit.AssignPoints(new JObject { [attrs[r.Pick.Next(attrs.Count)]] = waiting });
                After(r);
            }
            if (r.Pick.Next(4) == 0)
            {
                visit.MoveFlask(r.Pick.Next(2) == 0 ? "hp" : "mana", r.Pick.Next(2) == 0 ? 1 : -1);
                After(r);
            }
            if (!visit.Left && visit.SmithRefusal() == null && r.Pick.Next(2) == 0)
            {
                var candidate = LoopSmithing.Plan(ctx.Data, ctx.Run).Candidates.First(c => c.Affordable);
                visit.SmithItem(candidate.ItemRef);
                After(r);
            }
            if (!visit.Left)
            {
                visit.Rest();
                After(r);
            }
            visit.Leave();
            After(r);
        }

        /// <summary>A merchant: the stock rolled; maybe a card, a flask and a burn bought when affordable; then Leave.</summary>
        private static void Merchant(Run r)
        {
            var ctx = r.Ctx;
            var shop = ctx.Data.Shop;
            Shop.Open(shop, ctx.Run, ctx.Rng);
            After(r);
            foreach (var shelf in new[] { "cards", "flasks", "relics" })
            {
                var view = Shop.View(shop, ctx.Run, true);
                var offers = ((JArray)view[shelf]).OfType<JObject>().Where(o => Js.Nullish(o["refusal"])).ToList();
                if (offers.Count == 0 || r.Pick.Next(2) == 0) continue;
                var offer = offers[r.Pick.Next(offers.Count)];
                var kind = shelf == "cards" ? "buyCard" : shelf == "flasks" ? "buyFlask" : "buyRelic";
                var bought = Shop.Execute(shop, ctx.Run, new ShopAction { Kind = kind, Index = (int)offer.Num("index") }, true);
                Assert.That(bought.Ok, Is.True, $"{r.Label}: an offered {shelf} purchase was refused");
                After(r);
            }
            var remove = (JObject)Shop.View(shop, ctx.Run, true)["remove"];
            var burnable = ((JArray)remove["cards"]).Select(Js.Str).ToList();
            if (Js.Nullish(remove["refusal"]) && burnable.Count > 0 && r.Pick.Next(3) == 0)
            {
                var burned = Shop.Execute(shop, ctx.Run, new ShopAction { Kind = "removeCard", InstanceId = burnable[r.Pick.Next(burnable.Count)] }, true);
                Assert.That(burned.Ok, Is.True, $"{r.Label}: an offered card burn was refused");
                After(r);
            }
            Assert.That(Shop.Execute(shop, ctx.Run, new ShopAction { Kind = "leave" }, true).Ok, Is.True);
            After(r);
        }

        /// <summary>An event: one of its open, affordable choices (or none); a fight it starts is entered and played.</summary>
        private static void Event(Run r, string eventId)
        {
            var ctx = r.Ctx;
            var view = Events.Open(ctx.Data.Events, ctx.Run, eventId);
            var open = ((JArray)view["choices"]).OfType<JObject>().Where(c => c.Value<bool>("affordable")).ToList();
            if (open.Count > 0)
            {
                var choice = open[r.Pick.Next(open.Count)];
                var result = Events.Choose(ctx.Data.Events, ctx.Run, eventId, choice.Value<string>("choiceId"), ctx.Rng);
                Assert.That(result.Ok, Is.True, $"{r.Label}: an open affordable choice was refused");
                After(r);
            }
            if (Js.Truthy(ctx.Run["combatEntered"]))
            {
                RunLoop.EnterEventCombat(ctx);
                After(r);
                Fight(r);
                return;
            }
            if (ctx.Run.Num("hp") <= 0) r.Ended = "event-death";
        }

        /// <summary>A legacy dungeon, walked as the loop oracle's harness walks it (pending reward, shrine, dialogue, rooms toward the boss).</summary>
        private static void Dungeon(Run r)
        {
            var ctx = r.Ctx;
            var d = ctx.Data;
            var run = ctx.Run;
            var s = (JObject)run["legacyDungeon"];
            if (run["pendingReward"] is JObject)
            {
                Claim(r, RewardDoor.OpenPending(ctx));
                return;
            }
            if (Js.Truthy(s["activeRest"]))
            {
                Rest(r, null);
                return;
            }
            if (Js.Truthy(s["pending"]))
            {
                var o = LegacyDungeons.ContinueDialogue(ctx);
                After(r);
                if (o.Kind == "fight") Fight(r);
                return;
            }
            if (Js.Truthy(s["cleared"]))
            {
                var o = LegacyDungeons.Leave(ctx);
                After(r);
                if (o.Kind == "victory") r.Ended = "victory";
                return;
            }
            var action = LegacyDungeons.NodeAction(d, run);
            if (action == "dialogue")
            {
                var choices = LegacyDungeons.Choices(d, run);
                LegacyDungeons.Choose(d, run, choices[r.Pick.Next(choices.Count)].Str("id"), ctx.Rng);
                After(r);
                return;
            }
            NodeOutcome moved;
            if (action == "map")
            {
                var def = LegacyDungeons.Definition(d, run);
                var boss = def.Str("bossNode");
                var dist = new Dictionary<string, int>(StringComparer.Ordinal) { [boss] = 0 };
                var queue = new Queue<string>();
                queue.Enqueue(boss);
                while (queue.Count > 0)
                {
                    var here = queue.Dequeue();
                    foreach (var e in ((JArray)def["edges"]).OfType<JObject>())
                    {
                        var other = e.Str("a") == here ? e.Str("b") : e.Str("b") == here ? e.Str("a") : null;
                        if (other != null && !dist.ContainsKey(other)) { dist[other] = dist[here] + 1; queue.Enqueue(other); }
                    }
                }
                var seen = new HashSet<string>(Js.Items(s["visited"]).Select(Js.Str));
                var options = LegacyDungeons.Neighbors(d, run)
                    .OrderBy(n => seen.Contains(n) ? 1 : 0).ThenBy(n => dist.TryGetValue(n, out var v) ? v : int.MaxValue).ThenBy(n => n, StringComparer.Ordinal).ToList();
                var to = r.Pick.Next(3) == 0 && options.Count > 1 && !seen.Contains(options[1]) ? options[1] : options[0];
                moved = LegacyDungeons.TravelTo(ctx, to);
            }
            else moved = LegacyDungeons.EnterLocation(ctx);
            After(r);
            if (moved.Kind == "fight") Fight(r);
        }

        private static string Where(LoopContext ctx) =>
            "act " + ctx.Run["actNumber"] + ", floor " + ctx.Run["floor"] + ", dungeon " + (ctx.Run["legacyDungeon"]?.ToString(Formatting.None) ?? "none");

        private static string Board(CombatState c) =>
            "turn " + c.Turn + ", player " + c.Player["hp"] + "/" + c.Player["maxHp"] + " energy " + c.Player["energy"] + " mana " + c.Player["mana"] + "/" + c.Player["maxMana"] + " stamina " + c.Player["stamina"] + ", draw " + c.Piles.Draw.Count + " discard " + c.Piles.Discard.Count + " exhaust " + c.Piles.Exhaust.Count + ", enemies "
            + string.Join(" ", c.Enemies.Select(e => e["id"] + "/" + e["enemyId"] + ":" + e["hp"] + "/" + e["maxHp"] + (e.Is("alive") ? "" : "(dead)")))
            + ", hand " + string.Join(",", c.Piles.Hand.Select(h => h["cardId"]));

        // ------------------------------------------------------------------ invariants

        private static void After(Run r)
        {
            r.Steps++;
            Check(r);
        }

        private static void Check(Run r)
        {
            var run = r.Ctx.Run;
            var label = $"{r.Label} step {r.Steps}";
            Assert.That(run.Num("hp"), Is.InRange(0, run.Num("maxHp")), label + ": HP outside [0, maxHp]");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in ((JArray)run["deck"]).OfType<JObject>())
            {
                var id = card.Str("instanceId");
                // D-099: the shipped run-level door mints run1, run2 … per context, so two events that add cards repeat them.
                if (!ids.Add(id) && !id.StartsWith("run", StringComparison.Ordinal)) Assert.Fail($"{label}: deck instance id {id} is not unique");
            }
            var now = CountersOf(r.Ctx.Rng);
            foreach (var p in now)
                Assert.That(p.Value, Is.GreaterThanOrEqualTo(r.Counters[p.Key]), $"{label}: RNG counter {p.Key} went back");
            r.Counters = now;
        }

        private static Dictionary<string, uint> CountersOf(Rng rng) =>
            RngStreamNames.All.ToDictionary(name => name, name => rng.Counter(RngStreamNames.Parse(name)), StringComparer.Ordinal);

        private static string Hash(LoopContext ctx)
        {
            var doc = new JObject { ["run"] = ctx.Run.DeepClone(), ["rng"] = JObject.FromObject(CountersOf(ctx.Rng)), ["profile"] = ctx.Profile.DeepClone() };
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(doc.ToString(Formatting.None)))).Replace("-", string.Empty);
        }
    }
}
