using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-3a (D-1440..D-1449): the Armoury's candidate comparison against the shipped web. Tools/oracle-core2-armoury.mjs drives equipmentSurfaceReceipt(..., { candidate, meta }) for every class, on the run as created and on a
    /// run that carries the whole catalog with two pieces forged, for every piece that fits each hand and the armour (and an unequip), under the default swap rule; the other three swap rules record their resource changes.
    /// The port must give the same roles, attack package counts, requirement, added effects, resource changes and swap prices, Poise and load, before and after. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ArmouryCandidateParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "armoury");

        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private string _dir;
        private SaveService _saves;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-candidate-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            // Every class open, as the oracle's runs are made.
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
            ((JArray)OracleDocuments.Read(Path.Combine(Dir, "candidates.json"))["cases"]).OfType<JObject>()
                .Select(c => new TestCaseData(c.Value<string>("classId"), c.Value<string>("variant")).SetName("ArmouryCandidate_" + c.Value<string>("classId") + "_" + c.Value<string>("variant")));

        [Test]
        public void TheOracleIsPresent()
        {
            var index = OracleDocuments.Read(Path.Combine(Dir, "index.json"));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2HostParityTests.Content.Core2.ProfileHash), "the oracle ran the profile that is loaded");
            var tally = (JObject)index["tally"];
            Assert.That(tally.Value<int>("candidates"), Is.GreaterThan(1000));
            Assert.That(tally.Value<int>("withEffects"), Is.GreaterThan(500), "pieces that add effects");
            Assert.That(((JObject)tally["swapPrices"]).Value<int>("category"), Is.GreaterThan(100), "the weapon category rule prices a swap");
        }

        private static void Edit(JObject run, JObject edit)
        {
            if (edit == null) return;
            foreach (var p in edit.Properties())
            {
                if (run[p.Name] is JObject have && p.Value is JObject want) Edit(have, want);
                else run[p.Name] = p.Value.DeepClone();
            }
        }

        private static JToken Roles(CandidateView view) => new JArray(view.Roles.Select(r => new JObject
        {
            ["role"] = r.Role, ["beforeName"] = r.BeforeName, ["afterName"] = r.AfterName, ["hint"] = r.Hint, ["beforeValue"] = r.Before, ["afterValue"] = r.After,
        }));

        private static JToken Want(JArray roles) => new JArray(roles.OfType<JObject>().Select(r => new JObject
        {
            ["role"] = r["role"], ["beforeName"] = r["beforeName"], ["afterName"] = r["afterName"],
            ["hint"] = string.Join(" · ", new[] { r.Value<string>("afterSchool") }.Concat(((JArray)r["afterTags"]).Select(t => (string)t))),
            ["beforeValue"] = r["beforeValue"], ["afterValue"] = r["afterValue"],
        }));

        private static JToken Packages(CandidateView view) => new JArray(view.AttackPackages.Select(p => new JObject
        {
            ["cardId"] = p.CardId, ["profileId"] = p.ProfileId, ["name"] = p.Name, ["beforeCount"] = p.BeforeCount, ["afterCount"] = p.AfterCount, ["sourceHands"] = new JArray(p.SourceHands),
        }));

        private static JToken Resources(CandidateView view) => new JArray(view.Resources.Select(r =>
        {
            var row = new JObject { ["id"] = r.Id, ["label"] = r.Label, ["before"] = r.Before, ["after"] = r.After };
            if (r.RuleId != null) { row["ruleId"] = r.RuleId; row["note"] = r.Note; }
            return row;
        }));

        private static JToken Requirement(CandidateView view)
        {
            if (view.Requirement == null) return new JArray();
            return new JArray(view.Requirement.Rows.Select(r => new JObject { ["label"] = r.Label, ["required"] = r.Required, ["actual"] = r.Actual.HasValue ? (JToken)r.Actual.Value : JValue.CreateNull() }));
        }

        private static JToken WantRequirement(JToken requirement) =>
            new JArray(((JArray)((requirement as JObject)?["requirements"] ?? new JArray())).OfType<JObject>().Select(r => new JObject
            {
                ["label"] = r["label"], ["required"] = r["required"], ["actual"] = r["actual"] ?? JValue.CreateNull(),
            }));

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail(label + ": first difference at " + diff + "\nwant " + want.ToString(Newtonsoft.Json.Formatting.None) + "\ngot  " + got.ToString(Newtonsoft.Json.Formatting.None));
        }

        [TestCaseSource(nameof(Cases))]
        public void TheComparisonIsTheWebs(string classId, string variant)
        {
            var entry = ((JArray)OracleDocuments.Read(Path.Combine(Dir, "candidates.json"))["cases"]).OfType<JObject>().First(c => c.Value<string>("classId") == classId && c.Value<string>("variant") == variant);
            var session = Core2RunSession.New(Core2HostParityTests.Content, _saves, 1, entry.Value<uint>("seed"), classId, "Aldric", null);
            session.EditRunForReview(run => Edit(run, entry["edit"] as JObject));
            var compared = 0;
            foreach (var record in ((JArray)entry["results"]).OfType<JObject>())
            {
                var at = classId + " " + variant + " " + record.Value<string>("slotId") + "." + record.Value<int>("setIndex") + " <- " + (record.Value<string>("pieceId") ?? "(unequip)");
                var slot = record.Value<string>("slotId");
                var set = record.Value<int>("setIndex");
                var piece = record.Value<string>("pieceId");
                var candidate = (JObject)record["candidate"];
                var view = ArmouryReceipts.Candidate(session, Ui, slot, set, piece);
                Assert.That(view, Is.Not.Null, at + ": the web made a comparison");
                Same(at + " roles", Want((JArray)candidate["roles"]), Roles(view));
                Same(at + " attack packages", new JArray(((JArray)candidate["attackPackageChanges"]).OfType<JObject>().Select(p => new JObject
                    { ["cardId"] = p["cardId"], ["profileId"] = p["profileId"], ["name"] = p["name"], ["beforeCount"] = p["beforeCount"], ["afterCount"] = p["afterCount"], ["sourceHands"] = p["sourceHands"] })), Packages(view));
                Same(at + " requirement", WantRequirement(candidate["requirement"]), Requirement(view));
                Same(at + " added effects", new JArray(((JArray)candidate["addedEffects"]).Select(e => e["label"])), new JArray(view.AddedEffects));
                Same(at + " resource changes", Wanted((JArray)candidate["resourceChanges"]), Resources(view));
                Assert.That(view.PoiseBefore, Is.EqualTo(candidate["poise"].Value<double>("before")), at + " Poise before");
                Assert.That(view.PoiseAfter, Is.EqualTo(candidate["poise"].Value<double>("after")), at + " Poise after");
                Assert.That(view.PoiseNote, Is.EqualTo(candidate["poise"].Value<string>("note")), at + " Poise note");
                var load = candidate["load"];
                Assert.That(view.LoadBefore.Load, Is.EqualTo(load.Value<double>("before")), at + " load before");
                Assert.That(view.LoadAfter.Load, Is.EqualTo(load.Value<double>("after")), at + " load after");
                Assert.That(view.LoadAfter.Capacity, Is.EqualTo(load.Value<double>("capacity")), at + " capacity");
                Assert.That(view.LoadBefore.Percent, Is.EqualTo(load.Value<double>("beforePercent")), at + " percent before");
                Assert.That(view.LoadAfter.Percent, Is.EqualTo(load.Value<double>("afterPercent")), at + " percent after");
                Assert.That(view.LoadBefore.ClassId, Is.EqualTo(load.Value<string>("beforeClassId")), at + " class before");
                Assert.That(view.LoadAfter.ClassId, Is.EqualTo(load.Value<string>("afterClassId")), at + " class after");
                Assert.That(view.LoadAfter.Word, Is.EqualTo(load.Value<string>("afterWord")), at + " word after");
                Assert.That(view.ChangesClass, Is.EqualTo(load.Value<bool>("changesClass")), at + " class change");
                Assert.That(view.LoadNote, Is.EqualTo(load.Value<string>("note")), at + " load note");
                foreach (var rule in new[] { "flat", "gear", "category" })
                {
                    var priced = ArmouryReceipts.Candidate(session, Ui, slot, set, piece, rule);
                    Same(at + " resource changes under " + rule, Wanted((JArray)record["rules"][rule]["resourceChanges"]), Resources(priced));
                }
                compared++;
            }
            Assert.That(compared, Is.GreaterThan(100));
        }

        private static JToken Wanted(JArray rows) => new JArray(rows.OfType<JObject>().Select(r =>
        {
            var row = new JObject { ["id"] = r["id"], ["label"] = r["label"], ["before"] = r["before"], ["after"] = r["after"] };
            if (r["ruleId"] != null) { row["ruleId"] = r["ruleId"]; row["note"] = r["note"]; }
            return row;
        }));
    }
}
