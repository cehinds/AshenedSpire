using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-3 (D-170): Core 2 run creation. Every run recorded from the web's createRunState for the configured profile
    /// (Tools/oracle-core2-run.mjs: 4 classes x 12 seeds x the two creation modes) is created again by
    /// <see cref="CoreRunRules.Create"/> and must equal the web's run document field for field (the random runId is left out and the
    /// 640 KB profile snapshot is compared by id and hash); the createRunCombat arguments main.js would build for two encounters
    /// must equal <see cref="CoreRunRules.CombatArgs"/>; the class-unlock gate and the creation refusals must match; creation draws
    /// no RNG. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RunParityTests
    {
        private static string RunDir => Path.Combine(TestContent.OracleRoot, "core2", "run");

        private static CoreRunData _data;

        private static CoreRunData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                return _data = Core2Registries.Load(snapshot.Content).ToCoreRunData(snapshot.Content);
            }
        }

        private static JObject Read(string path) => (JObject)JsonContent.Parse(File.ReadAllText(path));

        private static JObject Index => Read(Path.Combine(RunDir, "index.json"));

        public static IEnumerable<string> RunFiles() =>
            Directory.Exists(RunDir)
                ? Directory.GetFiles(RunDir, "run-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> RefusedCases() =>
            ((JArray)Index["refused"]).OfType<JObject>().Select(r => new TestCaseData(r).SetName("Core2Refused_" + r.Value<string>("label")));

        public static IEnumerable<TestCaseData> GateCases() =>
            ((JArray)Read(Path.Combine(RunDir, "unlocks.json"))["cases"]).OfType<JObject>().Select((c, i) =>
                new TestCaseData(c).SetName("Core2Gate_" + i + "_" + c.Value<string>("classId") + "_" + (c["label"] != null ? c.Value<string>("label") : "completed" + c.Value<int>("completed"))));

        /// <summary>The profile meta the oracle used: every class unlocked.</summary>
        private static JObject Meta(int completed) => new JObject
        {
            [Core2RunKeys.GameProfileUnlocks] = new JObject
            {
                [Data.ProfileId] = new JObject { [Core2RunKeys.CompletedRunIds] = new JArray(Enumerable.Range(0, completed).Select(i => "r" + i)) },
            },
        };

        private static JObject Unlocked => Meta(Data.Run.Classes.Count);

        private static JObject Create(JObject log) =>
            CoreRunRules.Create(Data, (uint)log["seed"].Value<long>(), log.Value<string>("classId"), CoreRunOptions.FromJson(log["options"] as JObject, Unlocked));

        /// <summary>The run as the fixture holds it: no runId, the profile snapshot as its id and hash.</summary>
        private static JObject Document(JObject run)
        {
            var copy = (JObject)run.DeepClone();
            copy.Remove(Core2RunKeys.RunId);
            var pin = CoreRunRules.PinOf(run);
            Assert.That(pin, Is.Not.Null);
            copy[Core2RunSnapshotKeys.GameProfile] = pin;
            return copy;
        }

        private static void Check(string file, string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{file} {label}: first difference at {diff}");
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var runs = (JArray)Index["runs"];
            Assert.That(RunFiles().Count(), Is.EqualTo(runs.Count));
            foreach (var classId in Data.Run.Classes.Ids)
                foreach (var mode in ((JArray)Index["modes"]).Select(m => m.Value<string>()))
                    Assert.That(runs.Count(r => r.Value<string>("classId") == classId && r.Value<string>("mode") == mode), Is.GreaterThanOrEqualTo(12), classId + " " + mode);
            Assert.That(Index.Value<string>("profileHash"), Is.EqualTo(Data.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(Index.Value<string>("profileHash"), Is.EqualTo(Data.Versions.Value<string>(Core2VersionKeys.ProfileHash)));
        }

        [TestCaseSource(nameof(RunFiles))]
        public void RunMatchesTheWebRun(string file)
        {
            var log = Read(Path.Combine(RunDir, file));
            Check(file, "createRunState", log["run"], Document(Create(log)));
        }

        [TestCaseSource(nameof(RunFiles))]
        public void CreationDrawsNoRandomNumbers(string file)
        {
            var log = Read(Path.Combine(RunDir, file));
            var run = Create(log);
            Assert.That(run[RunKeys.StreamCounters], Is.InstanceOf<JObject>());
            Assert.That(((JObject)run[RunKeys.StreamCounters]).Count, Is.EqualTo(0), file + " stream counters");
            var wire = TestContent.ContentJson("rules/rng.json");
            var known = ((JArray)wire["streams"]).Concat((JArray)wire["core2Streams"]).Select(t => t.Value<string>()).ToList();
            foreach (var p in ((JObject)log["rngCounters"]).Properties())
            {
                Assert.That(known, Does.Contain(p.Name), file + " stream " + p.Name);
                Assert.That(p.Value.Value<long>(), Is.EqualTo(0), file + " rng " + p.Name);
            }
        }

        [TestCaseSource(nameof(RunFiles))]
        public void CombatArgsMatchTheWebEnterCombat(string file)
        {
            var log = Read(Path.Combine(RunDir, file));
            var run = Create(log);
            foreach (var fight in ((JArray)log["fights"]).OfType<JObject>())
            {
                var args = CoreRunRules.CombatArgs(Data, run, fight.Value<string>("encounterId"));
                Check(file, "createRunCombat args " + fight.Value<string>("encounterId"), fight, args);
            }
        }

        [TestCaseSource(nameof(RefusedCases))]
        public void RefusedCreationsAreRefused(JObject refused)
        {
            var meta = refused.Value<string>("label") == "locked-class" ? Meta(0) : Unlocked;
            var options = CoreRunOptions.FromJson(refused["options"] as JObject, meta);
            var e = Assert.Throws<InvalidOperationException>(() => CoreRunRules.Create(Data, (uint)refused["seed"].Value<long>(), refused.Value<string>("classId"), options));
            Assert.That(e.Message, Is.EqualTo(refused.Value<string>("error")));
        }

        [TestCaseSource(nameof(GateCases))]
        public void ClassUnlockGateMatchesTheWeb(JObject gate)
        {
            var meta = gate["label"] == null ? Meta(gate.Value<int>("completed"))
                : gate.Value<string>("label") == "fresh-meta" ? new JObject()
                : new JObject { [Core2RunKeys.GameProfileUnlocks] = new JObject { ["elsewhere"] = new JObject { [Core2RunKeys.CompletedRunIds] = new JArray("a", "b", "c") } } };
            var classId = gate.Value<string>("classId");
            Assert.That(CoreRunRules.ClassAvailable(Data, meta, classId), Is.EqualTo(gate.Value<bool>("available")));
            if (gate["error"] == null)
            {
                Assert.That(CoreRunRules.Create(Data, 1, classId, new CoreRunOptions { ProfileMeta = meta })[RunKeys.Class].Value<string>(), Is.EqualTo(classId));
            }
            else
            {
                var e = Assert.Throws<InvalidOperationException>(() => CoreRunRules.Create(Data, 1, classId, new CoreRunOptions { ProfileMeta = meta }));
                Assert.That(e.Message, Is.EqualTo(gate.Value<string>("error")));
            }
        }

        [Test]
        public void TheGateOpensOneClassPerCompletedRun()
        {
            var rules = (JObject)Read(Path.Combine(RunDir, "unlocks.json"))["rules"];
            var order = rules["initial"].Concat(rules["sequence"]).Select(t => t.Value<string>()).ToList();
            Assert.That(order, Is.EquivalentTo(Data.Run.Classes.Ids));
            for (var completed = 0; completed <= order.Count + 1; completed++)
                Assert.That(CoreRunRules.AvailableClasses(Data, Meta(completed)), Is.EquivalentTo(order.Take(Math.Min(order.Count, ((JArray)rules["initial"]).Count + completed))), "after " + completed);
        }

        [Test]
        public void ARunPinsItsProfileAndRefusesAnother()
        {
            var classId = Data.Run.Classes.Ids.First();
            var run = CoreRunRules.Create(Data, 1, classId, new CoreRunOptions { ProfileMeta = Unlocked });
            var pin = CoreRunRules.PinOf(run);
            Assert.That(pin.Value<string>(Core2ProfileKeys.Hash), Is.EqualTo(Data.ProfileHash));
            Assert.That(pin.Value<string>(Core2ProfileKeys.Id), Is.EqualTo(Data.ProfileId));
            Assert.That(CoreRunRules.MatchesProfile(run, Data.ProfileHash), Is.True);
            Assert.That(CoreRunRules.MatchesProfile(run, "sha256:" + new string('0', 64)), Is.False, "a run pinned to one profile does not load under another");
            run.Remove(Core2RunSnapshotKeys.GameProfile);
            Assert.That(CoreRunRules.MatchesProfile(run, "sha256:" + new string('0', 64)), Is.True, "a run with no pin loads under any");
            Assert.That(CoreRunRules.PinOf(run), Is.Null);
        }

        [Test]
        public void CreationIsDeterministicAndLeavesTheContentUntouched()
        {
            var classId = Data.Run.Classes.Ids.Last();
            var options = new CoreRunOptions { ProfileMeta = Unlocked, RunId = "fixed" };
            var a = CoreRunRules.Create(Data, 7, classId, options);
            var b = CoreRunRules.Create(Data, 7, classId, options);
            Assert.That(JToken.DeepEquals(a, b), Is.True);
            a[Core2RunKeys.RunId] = "other";
            a[RunKeys.Cinders] = 99;
            Assert.That(CoreRunRules.Create(Data, 7, classId, options)[RunKeys.Cinders].Value<int>(), Is.EqualTo(20), "no shared state with an earlier run");
        }
    }
}
