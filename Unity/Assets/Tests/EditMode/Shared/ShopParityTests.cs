using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Content;
using Ashen.Domain.Random;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-9.1–9.3 merchant parity (PF-04/05; D-036, D-037, D-040): every session Tools/oracle-shop.mjs recorded from the
    /// SHIPPED merchant — main.js enterNode 'merchant' and the shop screen's handlers over the shipped models, on the
    /// shipped and the reference presets — is replayed on CONTENT-built data through <see cref="Shop.Open"/>,
    /// <see cref="Shop.Execute"/> and <see cref="Shop.View"/>. The stock, every receipt or refusal, the run document
    /// after every step (presence, values and key order), the shop view and the RNG counters must equal the shipped ones.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ShopParityTests
    {
        private static string ShopDir => Path.Combine(TestContent.OracleRoot, "shop");

        private static readonly Dictionary<string, ShopData> DataByPreset = new Dictionary<string, ShopData>(StringComparer.Ordinal);

        internal static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        /// <summary>The merchant's data built from content for a preset (config layers → runtime registries → ShopData).</summary>
        internal static ShopData Data(string preset)
        {
            if (DataByPreset.TryGetValue(preset, out var cached)) return cached;
            var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = preset });
            var content = snapshot.Content;
            var registries = RuntimeRegistries.Build(content, (JObject)content.Get(ContentFiles.StringsEn));
            return DataByPreset[preset] = registries.ToShopData(
                (JObject)content.Get(ContentFiles.RulesMechanics), (JObject)content.Get(ContentFiles.RulesCombatEngine),
                (JObject)content.Get(ContentFiles.RulesHandRules), (JObject)content.Get(ContentFiles.RulesRunEngine),
                snapshot.ContentVersion, (JArray)content.Get(ContentFiles.BalanceCustomRun)["ASCENSION_ORDER"],
                (JObject)content.Get(ContentFiles.RulesRewardsEngine), (JObject)content.Get(ContentFiles.RulesShopEngine));
        }

        private static JObject Index => ReadJson(Path.Combine(ShopDir, "index.json"));

        public static IEnumerable<string> SessionFiles() =>
            Directory.Exists(ShopDir)
                ? Directory.GetFiles(ShopDir, "session-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        internal static Dictionary<RngStream, uint> Counters(JObject o) =>
            o.Properties().ToDictionary(p => RngStreamNames.Parse(p.Name), p => (uint)p.Value.Value<long>());

        /// <summary>A recorded run delta applied to the expected document: set in place (new keys appended), removals, then the recorded key order.</summary>
        internal static void ApplyDelta(JObject doc, JObject delta)
        {
            foreach (var p in ((JObject)delta["set"]).Properties()) doc[p.Name] = p.Value.DeepClone();
            foreach (var k in (JArray)delta["removed"]) doc.Remove(k.Value<string>());
            if (!(delta["keys"] is JArray keys)) return;
            var props = keys.Select(k => doc.Property(k.Value<string>())).ToList();
            doc.RemoveAll();
            foreach (var p in props) doc.Add(p);
        }

        private static ShopAction ActionFor(ShopData d, JObject run, JObject a)
        {
            var action = ShopAction.FromJson(a);
            var mode = a.Value<string>("quote");
            if (mode == null) return action;
            if (action.Kind == ShopValues.ActionBuyArmament || action.Kind == ShopValues.ActionBuyWeaponArt)
            {
                var quote = Shop.QuotePurchase(d, run, action.Kind == ShopValues.ActionBuyArmament ? ShopValues.KindArmament : ShopValues.KindWeaponArt, action.Index);
                if (quote != null && mode == "staleCost") quote.Cost += 1;
                if (quote != null && mode == "staleRevision") quote.Revision += 1;
                action.PurchaseQuote = quote;
            }
            else if (action.Kind == ShopValues.ActionSellArmament)
            {
                var quote = Shop.QuoteSale(d, run, action.Id);
                if (mode == "stalePrice") quote.Price += 1;
                if (mode == "staleRevision") quote.Revision += 1;
                if (mode == "staleSignature") quote.Signature += "~";
                action.SaleQuote = quote;
            }
            return action;
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var sessions = (JArray)Index["sessions"];
            Assert.That(SessionFiles().Count(), Is.EqualTo(sessions.Count));
            Assert.That(sessions.Count, Is.GreaterThanOrEqualTo(200));
            foreach (var preset in new[] { "shipped", "reference" })
                Assert.That(sessions.Count(s => s.Value<string>("preset") == preset), Is.GreaterThan(50), preset);
            var accepted = (JObject)Index["tally"]["accepted"];
            foreach (var kind in new[] { "buyCard", "buyRelic", "buyFlask", "buyArmament", "buyWeaponArt", "removeCard", "sellRelic", "sellFlask", "sellArmament", "smithUpgrade", "smithExtract", "smithInstall", "leave" })
                Assert.That(accepted.Value<int?>(kind) ?? 0, Is.GreaterThan(0), kind);
            var refused = ((JObject)Index["tally"]["refused"]).Properties().Select(p => p.Name.Split(':')[1]).Distinct().ToList();
            foreach (var key in JObject.Parse(File.ReadAllText(Path.Combine(TestContent.Root, "strings", "shop.en.json"))).Properties().Select(p => p.Name))
                if (key != "shop.refusal.noTrader" && key != "shop.refusal.notBuying")
                    Assert.That(refused, Does.Contain(key), "no recorded refusal says " + key);
        }

        [Test]
        public void EveryRefusalKeyResolvesToText()
        {
            var shop = JObject.Parse(File.ReadAllText(Path.Combine(TestContent.Root, "strings", "shop.en.json")));
            var en = JObject.Parse(File.ReadAllText(Path.Combine(TestContent.Root, "strings", "en.json")));
            foreach (var key in new[] { ShopValues.AvailCinders, ShopValues.AvailFull, ShopValues.AvailLocked })
                Assert.That(en.Value<string>(key + ".short"), Is.Not.Null.And.Not.Empty, key);
            foreach (var field in typeof(ShopStringKeys).GetFields())
                Assert.That(shop.Value<string>((string)field.GetValue(null)), Is.Not.Null.And.Not.Empty, field.Name);
        }

        [Test]
        public void DeferredJourneyShopThrowsByName()
        {
            var log = ReadJson(Path.Combine(ShopDir, SessionFiles().First()));
            var d = Data(log.Value<string>("preset"));
            var run = (JObject)log["run"].DeepClone();
            run["journey"] = new JObject();
            var rng = new Rng((uint)log["seed"].Value<long>(), Counters((JObject)log["rngBefore"]));
            var e = Assert.Throws<NotSupportedException>(() => Shop.Open(d, run, rng));
            Assert.That(e.Message, Is.EqualTo(ShopMessages.JourneyShopDeferred));
            run.Remove("journey");
            Shop.Open(d, run, rng);
            run["journey"] = new JObject();
            e = Assert.Throws<NotSupportedException>(() => Shop.Execute(d, run, new ShopAction { Kind = ShopValues.ActionLeave }, true));
            Assert.That(e.Message, Is.EqualTo(ShopMessages.JourneyShopDeferred));
        }

        [TestCaseSource(nameof(SessionFiles))]
        public void MerchantSessionMatchesTheShippedMerchant(string file)
        {
            var log = ReadJson(Path.Combine(ShopDir, file));
            var d = Data(log.Value<string>("preset"));
            var sellOn = log.Value<bool>("sellOn");
            var run = (JObject)log["run"].DeepClone();
            var expectedRun = (JObject)log["run"].DeepClone();
            var rng = new Rng((uint)log["seed"].Value<long>(), Counters((JObject)log["rngBefore"]));
            var open = (JObject)log["expected"]["open"];

            var stock = Shop.Open(d, run, rng);
            Check(file, "open stock", open["stock"], stock);
            if (log["stockPatch"] is JObject patch)
                foreach (var p in patch.Properties()) ((JObject)run["shopStock"])[p.Name] = p.Value.DeepClone();
            ApplyDelta(expectedRun, (JObject)open["runDelta"]);
            Check(file, "open run", expectedRun, run);
            foreach (var p in ((JObject)open["rngAfter"]).Properties())
                Assert.That(rng.Counter(RngStreamNames.Parse(p.Name)), Is.EqualTo((uint)p.Value.Value<long>()), file + " rng " + p.Name);
            JToken view = open["view"];
            Check(file, "open view", view, (JToken)Shop.View(d, run, sellOn) ?? JValue.CreateNull());

            var steps = (JArray)log["expected"]["steps"];
            for (var i = 0; i < steps.Count; i++)
            {
                var step = (JObject)steps[i];
                var label = $"step {i} {((JObject)step["action"]).ToString(Formatting.None)}";
                var result = Shop.Execute(d, run, ActionFor(d, run, (JObject)step["action"]), sellOn);
                Check(file, label + " result", step["result"], result.ToJson());
                ApplyDelta(expectedRun, (JObject)step["runDelta"]);
                Check(file, label + " run", expectedRun, run);
                if (step.ContainsKey("view")) view = step["view"];
                Check(file, label + " view", view, (JToken)Shop.View(d, run, sellOn) ?? JValue.CreateNull());
            }
            Assert.That(rng.Counters().All(kv => kv.Value == (uint)((JObject)open["rngAfter"])[RngStreamNames.ToWire(kv.Key)].Value<long>()), file + ": a shop action drew from the RNG");
        }

        private static void Check(string file, string label, JToken want, JToken got)
        {
            var diff = RewardsParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{file} {label}: first difference at {diff}");
        }
    }
}
