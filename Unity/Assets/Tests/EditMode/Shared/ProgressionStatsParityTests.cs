using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-3a (D-1450..D-1459): the Progression hub's Character Sheet against the shipped web. Tools/oracle-core2-progression.mjs drives progressionStats(registries, run, settings) for every class on the run as created, on a
    /// levelled run (level 7, points waiting, attributes raised) and on a geared one, and for every allocation the dialog can preview (one more point on each attribute, two on the first). The port must give the same stat
    /// rows with their formulas, the same attribute cards with what each feeds, scales and unlocks, and the same points the dialog may spend. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ProgressionStatsParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "progression");

        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private string _dir;
        private SaveService _saves;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-progression-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            var profile = ProfileStore.Load(_saves);
            profile.Doc["gameProfileUnlocks"] = Core2HostParityTests.Unlocked(Core2HostParityTests.Content.Core2)["gameProfileUnlocks"];
            profile.Save(Core2HostParityTests.Content.ContentHash);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        public static IEnumerable<TestCaseData> Cases() =>
            ((JArray)OracleDocuments.Read(Path.Combine(Dir, "stats.json"))["cases"]).OfType<JObject>()
                .Select(c => new TestCaseData(c.Value<string>("classId"), c.Value<string>("variant")).SetName("ProgressionStats_" + c.Value<string>("classId") + "_" + c.Value<string>("variant")));

        private static void Edit(JObject run, JObject edit)
        {
            if (edit == null) return;
            foreach (var p in edit.Properties())
            {
                if (run[p.Name] is JObject have && p.Value is JObject want) Edit(have, want);
                else run[p.Name] = p.Value.DeepClone();
            }
        }

        private static JToken Rows(ProgressionStatsView view) => new JArray(view.Rows.Select(r => new JObject
        {
            ["id"] = r.Id, ["label"] = r.Label, ["faceLabel"] = r.FaceLabel, ["value"] = r.Value, ["formula"] = r.Formula,
        }));

        private static JToken Cards(ProgressionStatsView view) => new JArray(view.Attributes.Select(a => new JObject
        {
            ["id"] = a.Id, ["value"] = a.Value, ["label"] = a.Short, ["mainSummary"] = a.MainSummary, ["title"] = a.Title, ["sense"] = a.Sense, ["flavour"] = a.Flavour,
            ["lines"] = new JArray(a.Lines), ["tooltip"] = a.Tooltip,
        }));

        private static JToken WantRows(JObject stats) => new JArray(((JArray)stats["rows"]).OfType<JObject>().Select(r => new JObject
        {
            ["id"] = r["id"], ["label"] = r["label"], ["faceLabel"] = r["faceLabel"], ["value"] = r["value"], ["formula"] = r["formula"],
        }));

        private static JToken WantCards(JObject stats) => new JArray(((JArray)stats["attributes"]).OfType<JObject>().Select(a => new JObject
        {
            ["id"] = a["id"], ["value"] = a["value"], ["label"] = a["label"], ["mainSummary"] = a["mainSummary"], ["title"] = a["title"], ["sense"] = a["sense"], ["flavour"] = a["flavour"],
            ["lines"] = a["lines"], ["tooltip"] = a["tooltip"],
        }));

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail(label + ": first difference at " + diff + "\nwant " + want.ToString(Newtonsoft.Json.Formatting.None) + "\ngot  " + got.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = OracleDocuments.Read(Path.Combine(Dir, "index.json"));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2HostParityTests.Content.Core2.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(((JObject)index["tally"]).Value<int>("cases"), Is.EqualTo(12));
            Assert.That(((JObject)index["tally"]).Value<int>("previews"), Is.EqualTo(72));
        }

        [TestCaseSource(nameof(Cases))]
        public void TheSheetIsTheWebs(string classId, string variant)
        {
            var entry = ((JArray)OracleDocuments.Read(Path.Combine(Dir, "stats.json"))["cases"]).OfType<JObject>().First(c => c.Value<string>("classId") == classId && c.Value<string>("variant") == variant);
            var session = Core2RunSession.New(Core2HostParityTests.Content, _saves, 1, entry.Value<uint>("seed"), classId, "Aldric", null);
            session.EditRunForReview(run => Edit(run, (JObject)entry["edit"]));
            var stats = (JObject)entry["stats"];
            var got = ProgressionStats.For(session, Ui);
            Same(classId + " " + variant + " rows", WantRows(stats), Rows(got));
            Same(classId + " " + variant + " cards", WantCards(stats), Cards(got));

            var plan = (JObject)entry["plan"];
            var points = CharacterProgression.For(session, Ui).Points;
            Assert.That(points, Is.EqualTo(plan.Value<int>("points")), "the points the dialog may spend");
            Assert.That(session.Run.Obj("level").Value<int>("level"), Is.EqualTo(plan.Value<int>("level")));

            foreach (var preview in ((JArray)entry["previews"]).OfType<JObject>())
            {
                var attributes = (JObject)session.Run["attributes"];
                foreach (var pending in ((JObject)preview["pending"]).Properties()) attributes[pending.Name] = attributes.Value<double>(pending.Name) + pending.Value.Value<double>();
                var shown = ProgressionStats.For(session, Ui, attributes);
                Same(classId + " " + variant + " preview " + preview["pending"].ToString(Newtonsoft.Json.Formatting.None) + " rows", WantRows((JObject)preview["stats"]), Rows(shown));
                Same(classId + " " + variant + " preview " + preview["pending"].ToString(Newtonsoft.Json.Formatting.None) + " cards", WantCards((JObject)preview["stats"]), Cards(shown));
            }
        }
    }
}
