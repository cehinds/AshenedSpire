using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-C1a (D-300, D-301): whole fights through the session the screen uses. Tools/oracle-core2-session.mjs plays whole fights on the shipped web (4 classes x every
    /// encounter of the profile x 2 seeds, a bot that chooses among the legal intents a player has) and records, at every decision, the LIST of legal intents, then
    /// the step (the combat as a delta, the event types, every RNG counter) and, at the end, what runCombatEnd wrote back to the run. <see cref="Core2CombatSession"/>
    /// is started from the same run and driven through Execute: its LegalIntents must be the web's list, in the web's order, at every step; every step must equal the
    /// web; a share of the fights is saved and reloaded in the middle (a checkpoint, or a slot through the save service) and goes on from the reload; and the run
    /// takes the fight back as the web's does. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2SessionParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "session");

        private static CoreCombatData Data => Core2DamageParityTests.Data;

        private static readonly Dictionary<string, JObject> Files = new Dictionary<string, JObject>();

        internal static JObject Read(string name)
        {
            lock (Files)
            {
                if (!Files.TryGetValue(name, out var doc)) Files[name] = doc = (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, name)));
                return doc;
            }
        }

        public static IEnumerable<string> FightFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Fights()
        {
            foreach (var file in FightFiles())
            {
                var fights = (JArray)Read(file)["fights"];
                for (var i = 0; i < fights.Count; i++)
                    yield return new TestCaseData(file, i).SetName("Core2Session_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + fights[i].Value<string>("encounterId") + "_" + i);
            }
        }

        /// <summary>The compact key of an intent, as the oracle writes it (its type, then every other field by name order; null is -, an array [a,b], an object {k=v}).</summary>
        internal static string Key(JObject intent)
        {
            string Show(JToken v)
            {
                switch (v.Type)
                {
                    case JTokenType.Null: return "-";
                    case JTokenType.Array: return "[" + string.Join(",", ((JArray)v).Select(Show)) + "]";
                    case JTokenType.Object: return "{" + string.Join(",", ((JObject)v).Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + Show(p.Value))) + "}";
                    case JTokenType.Boolean: return v.Value<bool>() ? "true" : "false";
                    case JTokenType.Integer:
                    case JTokenType.Float: return Js.D(v).ToString("R", CultureInfo.InvariantCulture);
                    default: return v.Value<string>();
                }
            }
            return string.Join(" ", new[] { intent.Value<string>("type") }.Concat(intent.Properties().Where(p => p.Name != "type").OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + Show(p.Value))));
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(FightFiles().Count(), Is.EqualTo(4), "one file per class");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash));
            Assert.That(index["total"].Value<int>("fights"), Is.EqualTo(Data.Run.Run.Classes.Count * Data.Run.Run.Encounters.Count * index.Value<int>("seeds")), "every class meets every encounter");
            Assert.That(index["total"].Value<int>("ended"), Is.EqualTo(index["total"].Value<int>("fights")), "every fight is played to its end");
        }

        private string _dir;
        private SaveService _saves;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-session-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        [TestCaseSource(nameof(Fights))]
        public void TheFightPlaysThroughTheSessionAsTheWebPlaysIt(string file, int index)
        {
            var fight = (JObject)((JArray)Read(file)["fights"])[index];
            var keys = ((JArray)fight["keys"]).Select(t => t.Value<string>()).ToList();
            var seed = (uint)fight["seed"].Value<long>();
            var label = file + "[" + index + "] " + fight.Value<string>("encounterId");
            var run = CoreRunRules.Create(Data.Run, seed, fight.Value<string>("classId"), CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            var shared = new Rng(seed);
            var session = Core2CombatSession.Start(Data, shared, run, fight.Value<string>("encounterId"));
            JToken want = fight["start"]["state"].DeepClone();
            Core2CombatStartParityTests.Same(label + " start", want, Core2CombatStartParityTests.Project(session.State));
            Assert.That(Core2CombatStartParityTests.Events(session.State, 0), Is.EqualTo(fight["start"]["events"].Select(t => t.Value<string>()).ToList()), label + " start events");

            var steps = ((JArray)fight["steps"]).OfType<JObject>().ToList();
            // A share of the fights is saved and loaded in the middle: a third reload the checkpoint, a sixth go through a slot of the save service.
            var reloadAt = steps.Count / 2;
            var viaSlot = index % 6 == 0;
            var reload = index % 3 == 0;
            for (var n = 0; n < steps.Count; n++)
            {
                var step = steps[n];
                var at = label + " step " + n;
                var legal = session.LegalIntents().Select(Key).ToList();
                Assert.That(legal, Is.EqualTo(((JArray)step["legal"]).Select(t => keys[t.Value<int>()]).ToList()), at + " the legal intents are the web's list, in order");
                var intent = (JObject)step["intent"];
                Assert.That(legal, Does.Contain(Key(intent)), at + " the chosen intent is legal");
                var mark = session.State.EventLog.Count;
                var outcome = session.Execute(intent);
                Assert.That(outcome.Accepted, Is.True, at + " " + intent.ToString(Newtonsoft.Json.Formatting.None) + " " + outcome.Refusal?.Message);
                want = Core2CombatStartParityTests.Apply(want, step["delta"]);
                Core2CombatStartParityTests.Same(at + " after " + intent.Value<string>("type"), want, Core2CombatStartParityTests.Project(session.State));
                Assert.That(outcome.Events.Select(e => e.Value<string>("type")).ToList(), Is.EqualTo(step["events"].Select(t => t.Value<string>()).ToList()), at + " events");
                if (reload && n == reloadAt && !session.IsOver)
                {
                    var before = session.StateHash();
                    if (viaSlot)
                    {
                        session.Save(_saves, "mid", "content-hash", run);
                        var loaded = Core2CombatSession.Load(Data, _saves, "mid");
                        Assert.That(loaded.Status, Is.EqualTo(Core2LoadStatus.Loaded), string.Join("; ", loaded.Warnings));
                        session = loaded.Session;
                    }
                    else session = Core2CombatSession.Resume(Data, session.Checkpoint());
                    Assert.That(session.StateHash(), Is.EqualTo(before), at + " the reload is the saved state");
                    Core2CombatStartParityTests.Same(at + " reloaded", want, Core2CombatStartParityTests.Project(session.State));
                }
            }
            var end = (JObject)fight["end"];
            Assert.That(session.IsOver, Is.EqualTo(end["result"].Type != JTokenType.Null), label + " the fight ends where the web's did");
            Assert.That(session.State.Result, Is.EqualTo(end["result"].Type == JTokenType.Null ? null : end.Value<string>("result")), label + " result");
            if (session.IsOver) Assert.That(session.LegalIntents(), Is.Empty, label + " a finished fight offers nothing");
            foreach (var pair in session.State.Rng.AllCounters().Where(p => p.Value != 0))
                Assert.That(end["rng"].Value<uint?>(pair.Key) ?? 0u, Is.EqualTo(pair.Value), label + " counter " + pair.Key);

            if (end["runDelta"] == null) return;
            // The run takes the fight back as runCombatEnd does.
            var runBefore = (JObject)run.DeepClone();
            CoreRunCombat.End(Data, run, session.State);
            var wantRun = Core2CombatStartParityTests.Apply(runBefore, end["runDelta"]);
            var diff = RunParityTests.FirstDifference(wantRun, run, "$");
            if (diff != null) Assert.Fail(label + " the run after the fight: first difference at " + diff);
        }
    }
}
