using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// D-1420, D-1421: the framework numbers a Core 2 fight reads that the legacy export did not carry. Everything recorded by Tools/oracle-core2-parity.mjs from the web's shipped modules
    /// (statProjection.pieceWeight, combatWeight.playerWeightClass, framework/weight.js, loadout.equipmentKitReceipt) is recomputed by the C# port: the weight of every armament and armour
    /// (mechanics.weight.itemWeightScale), the class a worn kit puts a character in across capacities that cross the Light/Medium/Heavy ceilings, the dodge roll and its Dexterity centre by
    /// creation mode, and the rating part of a kit card at attributes where the frozen LEGACY_RATING_FORMULA and the 405 rows part. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2FrameworkParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "parity");

        private static CoreCombatData _data;

        private static CoreCombatData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var registries = Core2Registries.Load(snapshot.Content);
                return _data = registries.ToCoreCombatData(snapshot.Content, registries.ToCoreRunData(snapshot.Content));
            }
        }

        private static JObject Read(string name) => OracleDocuments.Read(Path.Combine(Dir, name));

        private static JObject Unlocked => new JObject
        {
            ["gameProfileUnlocks"] = new JObject { [Data.Run.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) } },
        };

        private static JObject NewRun(uint seed, string classId) => CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Unlocked));

        private static void Same(string label, double want, double got) => Assert.That(got, Is.EqualTo(want).Within(1e-9), label);

        [Test]
        public void EveryPieceWeighsWhatTheWebScalesItTo()
        {
            var doc = Read("weight.json");
            var combat = Data.Combat;
            Assert.That(combat.Mechanics.Value<JObject>("weight").Value<double>("itemWeightScale"), Is.EqualTo(doc.Value<double>("scale")), "mechanics.weight.itemWeightScale");
            foreach (var p in doc.Value<JObject>("pieces").Value<JObject>("armaments").Properties())
            {
                var piece = combat.Equipment.Value<JArray>("armaments").OfType<JObject>().Single(a => a.Value<string>("id") == p.Name);
                Same("armament " + p.Name, p.Value.Value<double>(), Equipment.PieceWeight(combat, piece));
            }
            foreach (var p in doc.Value<JObject>("pieces").Value<JObject>("armour").Properties())
            {
                var piece = combat.Equipment.Value<JArray>("armour").OfType<JObject>().Single(a => a.Value<string>("classId") + ":" + a.Value<string>("id") == p.Name);
                Same("armour " + p.Name, p.Value.Value<double>(), Equipment.PieceWeight(combat, piece));
            }
        }

        public static IEnumerable<TestCaseData> ClassCases() =>
            new[] { "reaver", "starseer", "rogue", "herald" }.Select(c => new TestCaseData(c).SetName("Core2Weight_" + c));

        [TestCaseSource(nameof(ClassCases))]
        public void AWornKitPutsACharacterInTheWebsWeightClass(string classId)
        {
            var doc = Read("weight.json");
            var rows = doc.Value<JArray>("grid").OfType<JObject>().Single(g => g.Value<string>("classId") == classId).Value<JArray>("rows");
            var run = NewRun(5, classId);
            var tally = new Dictionary<string, int>();
            foreach (JObject row in rows)
            {
                var loadout = (JObject)run["loadout"].DeepClone();
                var sets = (JObject)loadout["sets"];
                sets["rightHand"] = new JArray(row["right"].DeepClone(), JValue.CreateNull(), JValue.CreateNull());
                sets["leftHand"] = new JArray(row["left"].DeepClone(), JValue.CreateNull(), JValue.CreateNull());
                sets["armor"] = new JArray(row["armour"].DeepClone());
                var attributes = (JObject)run["attributes"].DeepClone();
                foreach (var a in ((JObject)row["attributes"]).Properties()) attributes[a.Name] = a.Value.DeepClone();
                var got = Equipment.LoadoutWeightClass(Data.Combat, loadout, attributes, classId, new JObject());
                var label = $"{row["right"]}/{row["left"]}/{row["armour"]} at {row["attributes"].ToString(Newtonsoft.Json.Formatting.None)}";
                Same("load " + label, row.Value<double>("load"), got.Load);
                Same("capacity " + label, row.Value<double>("capacity"), got.Capacity);
                Same("percent " + label, row.Value<double>("percent"), got.Percent);
                Assert.That(got.WeightClass.Value<string>("id"), Is.EqualTo(row.Value<string>("classId")), "class " + label);
                tally[got.WeightClass.Value<string>("id")] = tally.TryGetValue(got.WeightClass.Value<string>("id"), out var n) ? n + 1 : 1;
            }
            Assert.That(tally.Keys, Is.SupersetOf(new[] { "light", "medium", "heavy" }), "the grid crosses every ceiling");
        }

        [Test]
        public void TheDodgeRollIsTheWebsAtEveryCreationMode()
        {
            var doc = Read("dodge.json");
            var classes = Data.Combat.Mechanics.Value<JObject>("weight").Value<JArray>("classes").OfType<JObject>().ToDictionary(c => c.Value<string>("id"));
            foreach (JObject row in doc.Value<JArray>("dodge"))
            {
                var mode = row["attributeMode"].Type == JTokenType.Null ? null : row.Value<string>("attributeMode");
                var cls = classes[row.Value<string>("classId")];
                var got = Equipment.DodgeRoll(Data.Combat, row.Value<double>("roll"), row.Value<double>("dexterity"), cls, mode);
                var label = $"{mode} dex {row["dexterity"]} {row["classId"]} roll {row["roll"]}";
                Same("check " + label, row.Value<double>("check"), got.Check);
                Same("difficulty " + label, row.Value<double>("difficulty"), got.Difficulty);
                Assert.That(got.Success, Is.EqualTo(row.Value<bool>("success")), "success " + label);
                Same("guard " + label, row.Value<double>("temporaryGuard"), got.TemporaryGuard);
                Same("stamina " + label, row["cost"].Value<double>("stamina"), cls.Value<double>("dodgeStaminaCost"));
                Same("actions " + label, row["cost"].Value<double>("actions"), cls.Value<double>("dodgeActionCost"));
            }
        }

        [Test]
        public void AKitCardIsPricedByTheFrozenRatingFormulaTheWebFallsBackTo()
        {
            var doc = Read("kit.json");
            var crossed = 0;
            foreach (JObject entry in doc.Value<JArray>("kit"))
            {
                var classId = entry.Value<string>("classId");
                var run = NewRun(9, classId);
                var attributes = (JObject)entry["attributes"];
                var rows = Loadout.KitReceipt(Data.Run.Run, (JObject)run["loadout"], classId, attributes, (JObject)run["equipmentProfileRuleSnapshot"]);
                var want = entry.Value<JArray>("rows");
                Assert.That(rows.Select(r => r.Role), Is.EqualTo(want.Select(r => r.Value<string>("role"))), classId + " roles");
                foreach (var row in rows)
                {
                    var w = want.OfType<JObject>().Single(r => r.Value<string>("role") == row.Role);
                    var label = $"{classId} {row.Role} at {attributes.ToString(Newtonsoft.Json.Formatting.None)}";
                    Same("value " + label, w.Value<double>("value"), row.Receipt.Value<double>("value"));
                    Same("raw " + label, w.Value<double>("raw"), row.Receipt.Value<double>("raw"));
                    Same("attribute part " + label, w.Value<double>("attributeValue"), row.Receipt["rating"].Value<double>("attributeValue"));
                    if (w.Value<double>("attributeValue") >= 3) crossed++;
                }
            }
            Assert.That(crossed, Is.GreaterThan(0), "some attribute part is where the 405 rows and the frozen formula part");
        }
    }
}
