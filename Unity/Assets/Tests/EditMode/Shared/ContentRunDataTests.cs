using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-0.4 (D-069): the run and rewards data built from CONTENT (config layers → runtime registries → RunData /
    /// RewardsData) equal the registry dumps the shipped createRunState and onCombatEnd ran against — the same rows in
    /// the same order, equal documents — and the recorded runs and post-combat cases replay identically on them.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ContentRunDataTests
    {
        private static RewardsData _content;

        private static string RunDir => Path.Combine(TestContent.OracleRoot, "run");
        private static string RewardsDir => Path.Combine(TestContent.OracleRoot, "rewards");

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static RewardsData ContentData
        {
            get
            {
                if (_content != null) return _content;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var content = snapshot.Content;
                var registries = RuntimeRegistries.Build(content, (JObject)content.Get(ContentFiles.StringsEn));
                return _content = registries.ToRewardsData(
                    (JObject)content.Get(ContentFiles.RulesMechanics), (JObject)content.Get(ContentFiles.RulesCombatEngine),
                    (JObject)content.Get(ContentFiles.RulesHandRules), (JObject)content.Get(ContentFiles.RulesRunEngine),
                    snapshot.ContentVersion, (JArray)content.Get(ContentFiles.BalanceCustomRun)["ASCENSION_ORDER"],
                    (JObject)content.Get(ContentFiles.RulesRewardsEngine));
            }
        }

        private static RunData Run => ContentData.Run;

        public static IEnumerable<string> Registries() => new[] { "creationModes", "seats", "encounters" };

        [TestCaseSource(nameof(Registries))]
        public void ContentRegistryEqualsTheShippedOneInOrder(string table)
        {
            var dump = ReadJson(Path.Combine(RewardsDir, "registries.json"));
            var want = ((JArray)dump[table]).OfType<JObject>().ToList();
            var got = (table == "creationModes" ? Run.CreationModes : table == "seats" ? Run.Seats : Run.Encounters).All;
            Assert.That(got.Select(r => r.Value<string>("id")), Is.EqualTo(want.Select(r => r.Value<string>("id"))), table + ": row order");
            for (var i = 0; i < want.Count; i++)
                Assert.That(Canonical(got[i]), Is.EqualTo(Canonical(want[i])), $"{table}[{want[i].Value<string>("id")}]");
        }

        [Test]
        public void ContentRunDocumentsEqualTheShippedOnes()
        {
            var dump = ReadJson(Path.Combine(RewardsDir, "registries.json"));
            Assert.That(Canonical(Run.AttributeRules), Is.EqualTo(Canonical(dump["attributeRules"])), "attributeRules");
            Assert.That(Canonical(Run.CharacterCreation), Is.EqualTo(Canonical(dump["characterCreation"])), "characterCreation");
            Assert.That(Canonical(Run.DerivedStatRules), Is.EqualTo(Canonical(dump["derivedStatRules"])), "derivedStatRules");
            Assert.That(Canonical(Run.TagFamilies), Is.EqualTo(Canonical(dump["tagFamilies"])), "tagFamilies");
            Assert.That(Canonical(ContentData.Nodes), Is.EqualTo(Canonical(dump["nodes"])), "nodes");
            Assert.That(Run.ContentVersion, Is.EqualTo(dump.Value<string>("contentVersion")), "contentVersion");
        }

        public static IEnumerable<string> RunFiles() =>
            Directory.Exists(RunDir)
                ? Directory.GetFiles(RunDir, "run-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        /// <summary>createRunState and the combat starts on content-built data equal the recorded shipped ones.</summary>
        [TestCaseSource(nameof(RunFiles))]
        public void RecordedRunReplaysOnContentData(string file)
        {
            var log = ReadJson(Path.Combine(RunDir, file));
            var run = RunState.Create(Run, (uint)log["seed"].Value<long>(), log.Value<string>("classId"), RunOptions.FromJson(log["options"] as JObject));
            Assert.That(Canonical(run), Is.EqualTo(Canonical(log["run"])), file + " run");
            foreach (var fight in ((JArray)log["fights"]).OfType<JObject>())
            {
                var args = RunCombat.CreateArgs(run, fight.Value<string>("encounterId"), Run);
                Assert.That(Canonical(args), Is.EqualTo(Canonical(fight["args"])), file + " args " + fight.Value<string>("encounterId"));
                var combat = CombatStart.Create(Run.Combat, new Rng((uint)log["seed"].Value<long>()), (JObject)JToken.Parse(args.ToString(Formatting.None)));
                Assert.That(Canonical(CombatSnapshot.Serialize(combat)), Is.EqualTo(Canonical(fight["snapshot"])), file + " start " + fight.Value<string>("encounterId"));
            }
        }

        public static IEnumerable<string> CaseFiles() =>
            Directory.Exists(RewardsDir)
                ? Directory.GetFiles(RewardsDir, "case-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        /// <summary>onCombatEnd on content-built data equals the recorded shipped pipeline (run, receipt, RNG counters).</summary>
        [TestCaseSource(nameof(CaseFiles))]
        public void RecordedPostCombatReplaysOnContentData(string file)
        {
            var log = ReadJson(Path.Combine(RewardsDir, file));
            var before = ((JObject)log["rngBefore"]).Properties().ToDictionary(p => RngStreamNames.Parse(p.Name), p => (uint)p.Value.Value<long>());
            var rng = new Rng((uint)log["seed"].Value<long>(), before);
            var combat = CombatSnapshot.Restore(Run.Combat, rng, (JObject)log["snapshot"].DeepClone());
            var run = (JObject)log["run"].DeepClone();
            var options = new CombatEndOptions
            {
                Found = ((JArray)log["meta"]["found"]).Select(t => t.Value<string>()).ToList(),
                PointsPerLevel = log["pointsPerLevel"]?.DeepClone(),
            };
            var receipt = CombatEnd.Apply(ContentData, run, combat, (JObject)log["enc"], log.Value<string>("result"), rng, options);
            var expected = (JObject)log["expected"];
            Assert.That(Canonical(receipt.ToJson()), Is.EqualTo(Canonical(expected["receipt"])), file + " receipt");
            Assert.That(Canonical(run), Is.EqualTo(Canonical(expected["run"])), file + " run");
            foreach (var p in ((JObject)expected["rngAfter"]).Properties())
                Assert.That(rng.Counter(RngStreamNames.Parse(p.Name)), Is.EqualTo((uint)p.Value.Value<long>()), file + " rng " + p.Name);
        }

        /// <summary>Every number printed as a round-trip double; keys in their order, or ordinally sorted.</summary>
        private static string Canonical(JToken t, bool sortKeys = false)
        {
            var sb = new StringBuilder();
            Write(sb, t, sortKeys);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, JToken t, bool sortKeys)
        {
            switch (t)
            {
                case null:
                    sb.Append("undefined");
                    return;
                case JObject o:
                    sb.Append('{');
                    foreach (var p in sortKeys ? o.Properties().OrderBy(x => x.Name, StringComparer.Ordinal) : o.Properties()) { sb.Append(JsonConvert.ToString(p.Name)).Append(':'); Write(sb, p.Value, sortKeys); sb.Append(','); }
                    sb.Append('}');
                    return;
                case JArray a:
                    sb.Append('[');
                    foreach (var x in a) { Write(sb, x, sortKeys); sb.Append(','); }
                    sb.Append(']');
                    return;
                default:
                    sb.Append(Js.IsNum(t) ? Js.D(t).ToString("R", CultureInfo.InvariantCulture) : t.ToString(Formatting.None));
                    return;
            }
        }
    }
}
