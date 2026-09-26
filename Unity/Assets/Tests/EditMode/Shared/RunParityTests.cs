using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-2.2 run-creation parity (PF-04/05; D-036, D-037): every run recorded from the SHIPPED createRunState by
    /// Tools/oracle-run.mjs (every class, twelve seeds each, plus creation variants: modes, the alternate kit,
    /// armour, relic and hand picks) is created again by <see cref="RunState.Create"/> and must equal the shipped
    /// run document; the createCombat arguments main.js would build for two encounters must equal
    /// <see cref="RunCombat.CreateArgs"/>, and <see cref="CombatStart.Create"/> on those arguments must produce the
    /// shipped combat-start snapshot and RNG counters. Variants the shipped creation refuses must be refused.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class RunParityTests
    {
        private static string RunDir => Path.Combine(TestContent.OracleRoot, "run");

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static RunData _data;

        private static RunData Data => _data ??= RunData.FromRegistryDump(
            ReadJson(Path.Combine(RunDir, "registries.json")),
            TestContent.ContentJson("rules/mechanics.json"),
            TestContent.ContentJson("rules/combatEngine.json"),
            TestContent.ContentJson("rules/handRules.json"),
            TestContent.ContentJson("rules/runEngine.json"));

        private static JObject Index => ReadJson(Path.Combine(RunDir, "index.json"));

        public static IEnumerable<string> RunFiles() =>
            Directory.Exists(RunDir)
                ? Directory.GetFiles(RunDir, "run-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> RefusedCases()
        {
            if (!File.Exists(Path.Combine(RunDir, "index.json"))) yield break;
            foreach (var r in ((JArray)Index["refused"]).OfType<JObject>())
                yield return new TestCaseData(r).SetName("Refused_" + r.Value<string>("classId") + "_" + r.Value<string>("variant"));
        }

        private static JObject Create(JObject log) =>
            RunState.Create(Data, (uint)log["seed"].Value<long>(), log.Value<string>("classId"), RunOptions.FromJson(log["options"] as JObject));

        [Test]
        public void TheOracleIsPresent()
        {
            var runs = (JArray)Index["runs"];
            Assert.That(RunFiles().Count(), Is.EqualTo(runs.Count));
            foreach (var classId in Data.Classes.Ids)
            {
                var defaults = runs.Count(r => r.Value<string>("classId") == classId && r.Value<string>("variant") == "default");
                Assert.That(defaults, Is.GreaterThanOrEqualTo(12), classId);
            }
        }

        [TestCaseSource(nameof(RunFiles))]
        public void RunMatchesTheShippedRun(string file)
        {
            var log = ReadJson(Path.Combine(RunDir, file));
            Check(file, "createRunState", log["run"], Create(log));
            // createRunState consumes no RNG: the orchestrator's stream counters are untouched after creation.
            foreach (var p in ((JObject)log["rngCounters"]).Properties()) Assert.That(p.Value.Value<long>(), Is.EqualTo(0), file + " rng " + p.Name);
        }

        [TestCaseSource(nameof(RunFiles))]
        public void CombatArgsMatchTheShippedEnterCombat(string file)
        {
            var log = ReadJson(Path.Combine(RunDir, file));
            var run = Create(log);
            foreach (var fight in ((JArray)log["fights"]).OfType<JObject>())
                Check(file, "createCombat args " + fight.Value<string>("encounterId"), fight["args"], RunCombat.CreateArgs(run, fight.Value<string>("encounterId"), Data));
        }

        [TestCaseSource(nameof(RunFiles))]
        public void CombatStartsFromTheArgs(string file)
        {
            var log = ReadJson(Path.Combine(RunDir, file));
            var run = Create(log);
            foreach (var fight in ((JArray)log["fights"]).OfType<JObject>())
            {
                var args = RunCombat.CreateArgs(run, fight.Value<string>("encounterId"), Data);
                var rng = new Rng((uint)log["seed"].Value<long>());
                var combat = CombatStart.Create(Data.Combat, rng, (JObject)JToken.Parse(args.ToString(Formatting.None)));
                Check(file, "combat start " + fight.Value<string>("encounterId"), fight["snapshot"], CombatSnapshot.Serialize(combat));
                foreach (var p in ((JObject)fight["rngCounters"]).Properties())
                    Assert.That(rng.Counter(RngStreamNames.Parse(p.Name)), Is.EqualTo((uint)p.Value.Value<long>()), file + " rng " + p.Name);
                // The fight is live: the opening turn is the player's and a turn can be ended.
                Assert.That(combat.Result, Is.Null, file);
                CombatEngine.Dispatch(combat, CombatCommand.EndTurn());
            }
        }

        [TestCaseSource(nameof(RefusedCases))]
        public void RefusedVariantsAreRefused(JObject refused)
        {
            var options = RunOptions.FromJson(refused["options"] as JObject);
            var e = Assert.Throws<InvalidOperationException>(() => RunState.Create(Data, (uint)refused["seed"].Value<long>(), refused.Value<string>("classId"), options));
            Assert.That(e.Message, Is.EqualTo(refused.Value<string>("error")));
        }

        [Test]
        public void DeferredCreationPathsThrowByName()
        {
            var classId = Data.Classes.Ids.First();
            Assert.Throws<NotSupportedException>(() => RunState.Create(Data, 1, classId, new RunOptions { Attributes = new JObject() }));
            Assert.Throws<NotSupportedException>(() => RunState.Create(Data, 1, classId, new RunOptions { DerivedStatRuleSnapshot = new JObject() }));
            var run = RunState.Create(Data, 1, classId);
            var encounterId = Data.Encounters.Ids.First();
            Assert.Throws<NotSupportedException>(() => RunCombat.CreateArgs(run, encounterId, Data, new JObject { [RunValues.HandRulesPrefix + "retain"] = false }));
            run["custom"] = new JObject();
            Assert.Throws<NotSupportedException>(() => RunCombat.CreateArgs(run, encounterId, Data));
        }

        // ------------------------------------------------------------------ comparison

        private static void Check(string file, string label, JToken want, JToken got)
        {
            var diff = FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{file} {label}: first difference at {diff}");
        }

        /// <summary>
        /// Order-insensitive deep equality with numbers compared as doubles. Unlike the combat projection compare, an
        /// absent key and an explicit null differ: the shipped document drops undefined and keeps null.
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
