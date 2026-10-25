using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-17 (D-330 to D-335): the Core 2 shop. Tools/oracle-core2-shop.mjs drives the shipped web for the configured default profile: the merchant node's door
    /// (main.js enterNode and showShop), the stock (gameProfileShopStock: three of each kind, drawn once on the <c>shop</c> stream), buying (purchaseGameProfileShopOffer,
    /// with every refusal the web has) and what the screen lets the player buy (mountConfiguredShop). 32 scenarios of six shops each work a run through purses, deck
    /// removals, range switches, tampered prices and, in a quarter of them, other settings of the shops module. The Domain port must give the same stock, receipts,
    /// refusals, run changes and RNG counters at every step, and leave the same run document. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2ShopParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "shop");

        private static Core2Content Core => Core2HostParityTests.Content.Core2;

        private static Core2RewardsData Data => Core.Rewards;

        internal static JObject Read(string name) => OracleDocuments.Read(Path.Combine(Dir, name));

        private static JObject Index => Read("index.json");

        public static IEnumerable<TestCaseData> Files() =>
            ((JArray)Index["scenarios"]).OfType<JObject>().Select(c => new TestCaseData(c.Value<string>("file")).SetName("Core2Shop_" + Path.GetFileNameWithoutExtension(c.Value<string>("file"))));

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = OracleCompare.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail(label + ": first difference at " + diff);
        }

        private static Dictionary<string, double> Live(Rng rng) => rng.AllCounters().Where(p => p.Value != 0).ToDictionary(p => p.Key, p => (double)p.Value);

        private static Dictionary<string, double> Want(JToken counters) => ((JObject)counters).Properties().Where(p => p.Value.Value<double>() != 0).ToDictionary(p => p.Name, p => p.Value.Value<double>());

        // The pinned profile is megabytes and no shop operation edits it: the changes of a step never look at it.
        private static bool Skipped(string name) => name == "streamCounters" || name == Ashen.Generated.Core2RunSnapshotKeys.GameProfile;

        internal static Dictionary<string, JToken> Snapshot(JObject run) => run.Properties().Where(p => !Skipped(p.Name)).ToDictionary(p => p.Name, p => p.Value.DeepClone());

        internal static JObject Changes(Dictionary<string, JToken> before, JObject run)
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

        internal static JToken Document(JObject run)
        {
            var copy = new JObject(run.Properties().Where(p => !Skipped(p.Name) && p.Name != Ashen.Generated.Core2RunKeys.RunId).Select(p => new JProperty(p.Name, p.Value.DeepClone())));
            copy[Ashen.Generated.Core2RunSnapshotKeys.GameProfile] = CoreRunRules.PinOf(run);
            return copy;
        }

        private static JToken Failure(string error) => new JObject { ["error"] = error };

        private static JToken Try(Func<JToken> fn)
        {
            try { return fn(); }
            catch (InvalidOperationException e) { return Failure(e.Message); }
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Index;
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(((JArray)index["scenarios"]).Count, Is.GreaterThanOrEqualTo(Core.Combat.Run.Run.Classes.Count * 8));
            var tally = (JObject)index["tally"];
            Assert.That(tally.Value<int>("stocks"), Is.GreaterThanOrEqualTo(150));
            Assert.That(tally.Value<int>("bought"), Is.GreaterThan(100));
            Assert.That(tally.Value<int>("refused"), Is.GreaterThan(100));
            Assert.That(tally.Value<int>("entersToMap"), Is.GreaterThan(0), "a profile without shops sends the player back to the map");
            Assert.That(((JObject)index["kinds"]).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "armor", "combatCards", "potions", "relics", "weaponArts", "weapons" }), "every shelf is stocked");
            var errors = ((JObject)index["errors"]).Properties().Select(p => p.Name).ToList();
            foreach (var wanted in new[] { "Insufficient cinders", "Missing configured shop", "Invalid configured shop price", "Invalid reward option", "Potion slots are full", "Reward offer is missing or already claimed" })
                Assert.That(errors.Any(e => e.EndsWith(wanted)), Is.True, "the web refuses with: " + wanted);
        }

        private static void Configure(JObject run, JToken module)
        {
            var profile = (JObject)run["gameProfile"].DeepClone();
            run["gameProfile"] = profile;
            var modules = (JObject)profile["modules"];
            if (module is JValue)
            {
                ((JObject)((JObject)profile["content"])["shops"]).Remove("itemsPerCategory");
                ((JObject)modules["shops"]).Remove("itemsPerCategory");
                return;
            }
            var spec = (JObject)module;
            if (spec.Value<bool?>("off") == true)
            {
                modules.Remove("shops");
                return;
            }
            var shops = (JObject)modules["shops"].DeepClone();
            foreach (var p in spec.Properties())
            {
                if (p.Value.Type == JTokenType.Null) shops.Remove(p.Name);
                else shops[p.Name] = p.Value.DeepClone();
            }
            modules["shops"] = shops;
        }

        private static JToken Entered(string shopId, JObject run) =>
            shopId == null ? new JObject { ["map"] = true } : new JObject { ["shopId"] = shopId, ["stock"] = Stock(run, shopId).DeepClone() };

        private static JObject Stock(JObject run, string shopId) => run.Obj("gameProfileRewardOffers").Obj("shop:" + shopId);

        [TestCaseSource(nameof(Files))]
        public void TheShopIsTheWebs(string file)
        {
            var sc = Read(file);
            var seed = (uint)sc.Value<long>("seed");
            var run = CoreRunRules.Create(Core.Combat.Run, seed, sc.Value<string>("classId"), CoreRunOptions.FromJson(null, Core2HostParityTests.Unlocked(Core)));
            var rng = CoreSnapshot.RngFromJson(seed, run.Obj("streamCounters"));
            var n = 0;
            foreach (var step in ((JArray)sc["steps"]).OfType<JObject>())
            {
                var op = step.Value<string>("op");
                var at = file + " step " + n++ + " " + op;
                if (op == "node") run["actNumber"] = step["act"].DeepClone();
                if (op == "available")
                {
                    var stock = Stock(run, step.Value<string>("shopId"));
                    var got = new JArray(Enumerable.Range(0, stock.Arr("options").Count).Select(i => (JToken)Core2Shop.Available(Data, run, stock, i)));
                    Same(at, step["available"], got);
                    Assert.That(Live(rng), Is.EqualTo(Want(step["counters"])), at + " counters");
                    continue;
                }
                var before = Snapshot(run);
                switch (op)
                {
                    case "config":
                        Configure(run, step["module"]);
                        break;
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
                    case "cinders":
                        run["cinders"] = step["amount"].DeepClone();
                        break;
                    case "node":
                        run["mapNodeId"] = step["nodeId"].DeepClone();
                        run["floor"] = step["floor"].DeepClone();
                        break;
                    case "enter":
                        Same(at, step["result"] ?? Failure(step.Value<string>("error")), Try(() => Entered(Core2Shop.Enter(Data, run, rng, step.Value<string>("nodeId")), run)));
                        break;
                    case "show":
                        Same(at, step["result"] ?? Failure(step.Value<string>("error")), Try(() =>
                        {
                            var stock = Core2Shop.Show(Data, run, rng);
                            return stock == null ? new JObject { ["map"] = true } : new JObject { ["shopId"] = run.Value<string>("gameProfileOpenShop"), ["stock"] = stock.DeepClone() };
                        }));
                        break;
                    case "buy":
                        Same(at, step["result"] ?? Failure(step.Value<string>("error")), Try(() => Core2Shop.Purchase(Data, run, step.Value<string>("shopId"), step.Value<int>("index"))));
                        break;
                    case "tamper":
                        Stock(run, step.Value<string>("shopId"))["price"] = step["price"].DeepClone();
                        break;
                    case "leave":
                        Core2Shop.Leave(run);
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
