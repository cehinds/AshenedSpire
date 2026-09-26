using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-11.1 post-combat parity (PF-04/05; D-036, D-037, D-040): every case Tools/oracle-rewards.mjs recorded from
    /// the SHIPPED onCombatEnd pipeline — the golden combats replayed to their end, plus generated fights over normal,
    /// elite and boss doors with run variants — is replayed through <see cref="CombatEnd.Apply"/>: the combat end state
    /// is restored from its snapshot on an RNG at the recorded counters, the run document is handed in as it stood,
    /// and the run after, the receipt and the RNG counters after must equal the shipped ones. Documents compare
    /// strictly: presence, values (numbers as doubles) and object key order.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class RewardsParityTests
    {
        private static string RewardsDir => Path.Combine(TestContent.OracleRoot, "rewards");

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static RewardsData _data;

        private static RewardsData Data => _data ??= RewardsData.FromRegistryDump(
            ReadJson(Path.Combine(RewardsDir, "registries.json")),
            TestContent.ContentJson("rules/mechanics.json"),
            TestContent.ContentJson("rules/combatEngine.json"),
            TestContent.ContentJson("rules/handRules.json"),
            TestContent.ContentJson("rules/runEngine.json"),
            (JArray)TestContent.ContentJson("balance/customRun.json")["ASCENSION_ORDER"],
            TestContent.ContentJson("rules/rewardsEngine.json"));

        private static JObject Index => ReadJson(Path.Combine(RewardsDir, "index.json"));

        public static IEnumerable<string> CaseFiles() =>
            Directory.Exists(RewardsDir)
                ? Directory.GetFiles(RewardsDir, "case-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        private static Dictionary<RngStream, uint> Counters(JObject o) =>
            o.Properties().ToDictionary(p => RngStreamNames.Parse(p.Name), p => (uint)p.Value.Value<long>());

        /// <summary>Replays one recorded case: restore the fight, apply the pipeline, return (run, receipt, rng).</summary>
        private static (JObject Run, JObject Receipt, Rng Rng) Replay(JObject log)
        {
            var rng = new Rng((uint)log["seed"].Value<long>(), Counters((JObject)log["rngBefore"]));
            var combat = CombatSnapshot.Restore(Data.Combat, rng, (JObject)log["snapshot"].DeepClone());
            var run = (JObject)log["run"].DeepClone();
            var options = new CombatEndOptions
            {
                Found = ((JArray)log["meta"]["found"]).Select(t => t.Value<string>()).ToList(),
                PointsPerLevel = log["pointsPerLevel"]?.DeepClone(),
            };
            var receipt = CombatEnd.Apply(Data, run, combat, (JObject)log["enc"], log.Value<string>("result"), rng, options);
            return (run, receipt.ToJson(), rng);
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var cases = (JArray)Index["cases"];
            Assert.That(CaseFiles().Count(), Is.EqualTo(cases.Count));
            Assert.That(cases.Count, Is.GreaterThanOrEqualTo(100));
            foreach (var outcome in new[] { "reward", "defeat", "victory" })
                Assert.That(cases.Count(c => c.Value<string>("outcome") == outcome), Is.GreaterThan(0), outcome);
            foreach (var pool in new[] { "normal", "elite", "boss" })
                Assert.That(cases.Count(c => c.Value<string>("pool") == pool && c.Value<string>("outcome") == "reward"), Is.GreaterThan(0), pool);
        }

        [TestCaseSource(nameof(CaseFiles))]
        public void PostCombatMatchesTheShippedPipeline(string file)
        {
            var log = ReadJson(Path.Combine(RewardsDir, file));
            var (run, receipt, rng) = Replay(log);
            var expected = (JObject)log["expected"];
            Check(file, "receipt", expected["receipt"], receipt);
            Check(file, "run", expected["run"], run);
            foreach (var p in ((JObject)expected["rngAfter"]).Properties())
                Assert.That(rng.Counter(RngStreamNames.Parse(p.Name)), Is.EqualTo((uint)p.Value.Value<long>()), file + " rng " + p.Name);
        }

        [Test]
        public void DeferredPathsThrowByName()
        {
            var file = CaseFiles().First();
            var log = ReadJson(Path.Combine(RewardsDir, file));
            foreach (var key in new[] { "journey", "legacyDungeon" })
            {
                var rng = new Rng((uint)log["seed"].Value<long>(), Counters((JObject)log["rngBefore"]));
                var combat = CombatSnapshot.Restore(Data.Combat, rng, (JObject)log["snapshot"].DeepClone());
                var run = (JObject)log["run"].DeepClone();
                run[key] = new JObject();
                var e = Assert.Throws<NotSupportedException>(() => CombatEnd.Apply(Data, run, combat, (JObject)log["enc"], log.Value<string>("result"), rng));
                Assert.That(e.Message, Is.EqualTo(RewardsMessages.JourneyCombatEndDeferred));
            }
        }

        // ------------------------------------------------------------------ comparison

        private static void Check(string file, string label, JToken want, JToken got)
        {
            var diff = FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{file} {label}: first difference at {diff}");
        }

        /// <summary>
        /// Strict deep equality: an absent key and an explicit null differ (D-045), numbers compare as doubles, and
        /// object keys must appear in the same order (D-040: the shipped insertion order is part of the document).
        /// </summary>
        internal static string FirstDifference(JToken a, JToken b, string path)
        {
            if (a == null || b == null) return a == null && b == null ? null : $"{path}: {Show(a)} vs {Show(b)}";
            if (Js.IsNum(a) && Js.IsNum(b)) return Js.D(a) == Js.D(b) ? null : $"{path}: {Show(a)} vs {Show(b)}";
            if (a.Type != b.Type) return $"{path}: {Show(a)} vs {Show(b)}";
            if (a is JObject oa)
            {
                var ob = (JObject)b;
                foreach (var name in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)))
                {
                    var d = FirstDifference(oa[name], ob[name], path + "." + name);
                    if (d != null) return d;
                }
                var ka = oa.Properties().Select(p => p.Name).ToList();
                var kb = ob.Properties().Select(p => p.Name).ToList();
                for (var i = 0; i < ka.Count; i++)
                    if (ka[i] != kb[i]) return $"{path}: key order [{string.Join(",", ka)}] vs [{string.Join(",", kb)}]";
                return null;
            }
            if (a is JArray aa)
            {
                var ab = (JArray)b;
                if (aa.Count != ab.Count) return $"{path}: length {aa.Count} vs {ab.Count}";
                for (var i = 0; i < aa.Count; i++)
                {
                    var d = FirstDifference(aa[i], ab[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    if (d != null) return d;
                }
                return null;
            }
            return JToken.DeepEquals(a, b) ? null : $"{path}: {Show(a)} vs {Show(b)}";
        }

        private static string Show(JToken t)
        {
            if (t == null) return "(absent)";
            var s = t.ToString(Formatting.None);
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }
}
