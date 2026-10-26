using System;
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
    /// P-3a (D-1440..D-1449): the poker-style equipment card against the shipped web. Tools/oracle-core2-armoury.mjs reads equipmentCardModel and the possession variant for every armament and every armour set of
    /// every class at tiers 0 to 2 (the configured gear's points, the type line, the requirement, the tags with their blurbs, the weapon art the piece grants, the mods worded). The port must say the same. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ArmouryCardParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "armoury");

        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private string _dir;
        private SaveService _saves;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-card-" + Guid.NewGuid().ToString("N"));
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

        private static JArray Cards => (JArray)OracleDocuments.Read(Path.Combine(Dir, "cards.json"))["cards"];

        [Test]
        public void TheOracleHoldsEveryPiece()
        {
            Assert.That(Cards.Count, Is.GreaterThan(150));
            Assert.That(Cards.OfType<JObject>().Count(c => c.Value<int>("level") > 0), Is.GreaterThan(80), "the tiers a piece is forged to");
        }

        [Test]
        public void TheFramesRowsAreSolvedAsTheWebSolvesThem()
        {
            var layout = Ui.EquipmentCard;
            var oracle = (JObject)OracleDocuments.Read(Path.Combine(Dir, "cards.json"))["layout"];
            foreach (var (name, collapse) in new[] { ("withTags", new string[0]), ("noTags", new[] { CardLayoutKeys.TagsRegion }) })
            {
                var want = (JObject)oracle[name];
                Assert.That(layout.FrameWidth, Is.EqualTo(want.Value<double>("frameWidthPx")));
                Assert.That(layout.FrameHeight, Is.EqualTo(want.Value<double>("frameHeightPx")));
                Assert.That(layout.Padding, Is.EqualTo(want.Value<double>("paddingPx")));
                Assert.That(layout.Gap, Is.EqualTo(want.Value<double>("gapPx")));
                Assert.That(layout.BonusMaxLines, Is.EqualTo(want.Value<int>("bonusMaxLines")));
                var got = layout.Solve(collapse);
                Assert.That(got.Select(g => g.Key), Is.EqualTo(((JArray)want["regions"]).Select(r => (string)r)), name + " regions in order");
                foreach (var row in got) Assert.That(row.Value, Is.EqualTo(want["heights"].Value<double>(row.Key)).Within(1e-9), name + " " + row.Key);
            }
        }

        private static JToken Lines(System.Collections.Generic.IEnumerable<EquipmentCardLine> lines, bool values) =>
            new JArray(lines.Select(l => values ? new JObject { ["label"] = l.Label, ["value"] = l.Value, ["explanation"] = l.Explanation } : new JObject { ["label"] = l.Label, ["explanation"] = l.Explanation }));

        [Test]
        public void EveryCardSaysWhatTheWebsSays()
        {
            var checkedCards = 0;
            var sessions = new System.Collections.Generic.Dictionary<string, Core2RunSession>();
            foreach (var wanted in Cards.OfType<JObject>())
            {
                var classId = wanted.Value<string>("classId") ?? "reaver";
                if (!sessions.TryGetValue(classId, out var session)) sessions[classId] = session = Core2RunSession.New(Core2HostParityTests.Content, _saves, 1, 779u, classId, "Aldric", null);
                var data = session.CombatData;
                var id = wanted.Value<string>("id");
                var level = wanted.Value<double>("level");
                var armour = wanted.Value<string>("classId") != null;
                var itemRef = armour ? string.Join("/", "armor", classId, id) : string.Join("/", "armament", id);
                var piece = Equipment.ResolveUpgradedEquipment(data, itemRef, level);
                var view = EquipmentCards.For(session, Ui, piece, level);
                var at = classId + " " + id + " tier " + level;
                var model = (JObject)wanted["model"];
                var variant = (JObject)wanted["variant"];
                Assert.That(view.Name, Is.EqualTo(model.Value<string>("name")), at + " name");
                Assert.That(view.Type, Is.EqualTo(model.Value<string>("type")), at + " type");
                Assert.That(view.TypeExplanation, Is.EqualTo(model.Value<string>("typeExplanation")), at + " type explanation");
                Assert.That(view.Flavor, Is.EqualTo(model.Value<string>("flavor")), at + " flavour");
                var accent = model.Value<string>("accent");
                Assert.That(view.Accent, Is.EqualTo(accent == "#87b5d0" ? "staff" : accent == "#aebdc6" ? "blade" : "gold"), at + " accent");
                if (!armour) Assert.That(view.ArtId, Is.Null.Or.StartsWith("equipment.icon."), at + " an armament's picture is its icon");
                else Assert.That(view.ArtId, Is.Null.Or.StartsWith("outfit.").Or.StartsWith("equipment.icon."), at + " an armour set shows its icon or its painted outfit (the outfits are registered, not imported, until the art release is)");
                Assert.That(view.Rarity, Is.EqualTo(model.Value<string>("rarity")), at + " rarity");
                Assert.That(view.RequirementExplanation, Is.EqualTo(model.Value<string>("requirementExplanation")), at + " requirement explanation");
                Assert.That(view.Requirement, Is.EqualTo(variant.Value<string>("requirement") ?? model.Value<string>("requirement")), at + " requirement line");
                Assert.That(view.Heading, Is.EqualTo(variant.Value<string>("heading")), at + " heading");
                Assert.That(view.Empty, Is.EqualTo(model.Value<bool>("armor") ? "No additional bonuses" : variant.Value<string>("empty") ?? "No additional bonuses"), at + " empty line");
                var facts = new JArray(((JArray)model["facts"]).OfType<JObject>().Select(f => new JObject { ["label"] = f["label"], ["value"] = f["value"].ToString(), ["explanation"] = f["explanation"] }));
                Assert.That(OracleCompare.FirstDifference(facts, Lines(view.Facts, true), "$"), Is.Null, at + " facts: " + Lines(view.Facts, true).ToString(Newtonsoft.Json.Formatting.None) + " vs " + facts.ToString(Newtonsoft.Json.Formatting.None));
                var tags = new JArray(((JArray)model["tags"]).OfType<JObject>().Select(t => new JObject { ["label"] = t["label"], ["explanation"] = t["explanation"] }));
                Assert.That(OracleCompare.FirstDifference(tags, Lines(view.Tags, false), "$"), Is.Null, at + " tags");
                var effects = new JArray(((JArray)variant["entries"]).OfType<JObject>().Select(e => new JObject { ["label"] = e["label"], ["explanation"] = e["explanation"] }));
                Assert.That(OracleCompare.FirstDifference(effects, Lines(view.Effects, false), "$"), Is.Null, at + " effects: " + Lines(view.Effects, false).ToString(Newtonsoft.Json.Formatting.None) + " vs " + effects.ToString(Newtonsoft.Json.Formatting.None));
                checkedCards++;
            }
            Assert.That(checkedCards, Is.GreaterThan(150));
        }
    }
}
