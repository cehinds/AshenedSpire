using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// VP-3: the card face's data (ui/cardface.json, written by Tools/card-face.mjs from the web at b8cfe4fac). Cutout selection is
    /// the web's approvedCardArt, the band its cardSigilsHtml, the rail a reduced cardSideTags (suite: Presentation, engine-free).
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class CardFaceTests
    {
        private static UiData _ui;
        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));
        private static CardFaceLookup Face => Ui.CardFace;

        private static string GlyphFolder => Path.Combine(TestContent.RepoRoot, "Unity", "Assets", "Game", "Presentation", "UI", "Resources", "UI", "CardFace");

        private static IEnumerable<JObject> Cards() => TestContent.ContentJson(ContentFiles.ProfileContentCards).Properties().Select(p => (JObject)p.Value);

        private static CardFacePlan PlanOf(string id) => Face.Plan(CardFaceQuery.From(Cards().First(c => (string)c["id"] == id)));

        private static bool Sprite(string resourcePath, string id) =>
            File.Exists(Path.Combine(GlyphFolder, Path.GetFileName(string.Format(System.Globalization.CultureInfo.InvariantCulture, resourcePath, id)) + ".png"));

        [Test]
        public void EveryGlyphTheFaceNamesIsASprite()
        {
            foreach (var id in Face.Config.SigilGlyphs) Assert.That(Sprite(UiFormats.CardSigilGlyph, id), Is.True, "sigil " + id);
            foreach (var id in Face.Config.TagGlyphs) Assert.That(Sprite(UiFormats.CardTagGlyph, id), Is.True, "tag " + id);
        }

        [Test]
        public void EverySigilHasItsWord()
        {
            foreach (var id in Face.Config.SigilGlyphs)
                Assert.That(Ui.Strings.Has(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiFormats.CardSigilKey, id)), Is.True, id);
        }

        [Test]
        public void EveryCoreTwoCardGetsAFace()
        {
            var composed = 0;
            foreach (var card in Cards())
            {
                var plan = Face.Plan(CardFaceQuery.From(card));
                var id = (string)card["id"];
                Assert.That(plan.Painting.Kind, Is.Not.EqualTo(CardArtKind.None), id);
                Assert.That(Face.Config.SigilGlyphs, Does.Contain(plan.Sigil), id);
                Assert.That(plan.Rail.Count, Is.LessThanOrEqualTo(5), id);
                foreach (var mark in plan.Rail) Assert.That(Face.Config.TagGlyphs, Does.Contain(mark), id);
                if (!plan.Composed) continue;
                composed++;
                Assert.That(Ui.Art.Has(plan.BackgroundId), Is.True, id);
                foreach (var layer in plan.Layers) Assert.That(Ui.Art.Has(layer.AssetId), Is.True, id + " " + layer.Id);
            }
            Assert.That(composed, Is.GreaterThan(100), "attacks, defends, smashes, spells and utility skills compose cutouts");
        }

        [Test]
        public void AStrikeComposesTheSilverBlueAttack()
        {
            var plan = PlanOf("core.strike");
            Assert.That(plan.Code, Is.EqualTo("O00"));
            Assert.That(plan.BackgroundId, Is.EqualTo("cardcut.O.background.0"));
            Assert.That(plan.Layers.Select(l => l.Id), Is.EqualTo(new[] { "backEffect", "object", "foreground" }));
            Assert.That(plan.Layers.Select(l => l.Opacity), Is.EqualTo(new[] { 0.28, 1, 0.24 }));
            Assert.That(plan.Layers[1].W, Is.EqualTo(0.84375).Within(1e-6));
        }

        [Test]
        public void ADefendKeepsTheCounterShieldAndHidesTheSecondObject()
        {
            var plan = PlanOf("core.defend");
            Assert.That(plan.Code, Is.EqualTo("O44"));
            Assert.That(plan.Layers.Select(l => l.Id), Does.Not.Contain("secondaryObject"));
            Assert.That(plan.Layers.Select(l => l.Id), Does.Contain("object"));
        }

        [Test]
        public void ACounterShowsBothObjectsAndTheTurnedSword()
        {
            var counter = Cards().First(c => ((JArray)c["tags"]).Any(t => (string)t == "maneuver:counter") && ((JArray)c["tags"]).Any(t => (string)t == "camp:physical"));
            var plan = Face.Plan(CardFaceQuery.From(counter));
            Assert.That(plan.Code, Is.EqualTo("O44"));
            var sword = plan.Layers.First(l => l.Id == "secondaryObject");
            Assert.That(sword.Rotation, Is.EqualTo(-20));
        }

        [Test]
        public void TheTypeBandNamesTheManeuverThenTheType()
        {
            Assert.That(PlanOf("core.strike").Sigil, Is.EqualTo("attack"));
            Assert.That(PlanOf("core.defend").Sigil, Is.EqualTo("defense"));
            Assert.That(PlanOf("core.reaverSpecial").Sigil, Is.EqualTo("smash"));
            var power = Cards().First(c => (string)c["type"] == "power");
            Assert.That(Face.Plan(CardFaceQuery.From(power)).Sigil, Is.EqualTo("power"));
        }

        [Test]
        public void TheRailReadsLikeTheWebsHand()
        {
            // web combat-card-inspect: Defend wears the Skill diamond and the Reaction arrow; Sundering Art the camp mark and the Smash fist.
            Assert.That(PlanOf("core.defend").Rail, Is.EqualTo(new[] { "skill", "reaction" }));
            Assert.That(PlanOf("core.reaverSpecial").Rail, Is.EqualTo(new[] { "guard", "smash" }));
            Assert.That(PlanOf("core.strike").Rail, Is.EqualTo(new[] { "guard" }));
        }

        [Test]
        public void ASpellComposesByItsElementAndAProjectileByItsPalette()
        {
            string Code(string maneuver, string element, string type = "attack") =>
                Face.Plan(new CardFaceQuery { CardId = "x", Type = type, Tags = new[] { "camp:spell", "maneuver:" + maneuver, "element:" + element } }).Code;
            Assert.That(Code("casting", "fire"), Is.EqualTo("Y00"), "a blue melee spell is Y00");
            Assert.That(Code("casting", "earth"), Is.EqualTo("Y05"), "a mint element shifts the palette by 5");
            var bolt = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:spell", "maneuver:ranged", "element:lightning" } });
            Assert.That(bolt.Code, Is.EqualTo("Z06"));
            var unknown = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:spell", "maneuver:ranged", "element:nonsense" } });
            Assert.That(unknown.Code, Is.EqualTo("Z02"), "an unknown element reads as force");
        }

        [Test]
        public void AUtilitySkillTakesTheCardOfItsOperation()
        {
            var heal = new CardFaceQuery
            {
                CardId = "x", Type = "skill", Tags = new[] { "camp:physical", "maneuver:casting" },
                Effects = new[] { new CardEffectRef { Op = "heal", Target = "self" } },
            };
            Assert.That(Face.Plan(heal).Code, Is.EqualTo("G00"));
            var spellUtility = new CardFaceQuery { CardId = "x", Type = "skill", Tags = new[] { "camp:spell", "maneuver:casting" } };
            Assert.That(Face.Plan(spellUtility).Code, Is.EqualTo("I00"));
            var plain = new CardFaceQuery { CardId = "x", Type = "skill", Tags = new[] { "camp:physical", "maneuver:casting" } };
            Assert.That(Face.Plan(plain).Composed, Is.False, "a physical utility skill with no operation of its own keeps the painting");
        }

        [Test]
        public void AMissingCutoutGivesTheCompositionBackToThePainting()
        {
            var ids = Ui.Art.Config.Lookup.ContainsKey(ArtKinds.Cutout)
                ? TestContent.ContentJson(ContentFiles.AssetsRegistry).Properties().Where(p => p.Value[ArtRegistryKeys.UnityPath].Type == JTokenType.String && !p.Name.StartsWith("cardcut.", System.StringComparison.Ordinal)).Select(p => p.Name)
                : Enumerable.Empty<string>();
            var art = new ArtLookup(Ui.Art.Config, new ArtRegistry(ids));
            var plan = new CardFaceLookup(Face.Config, art).Plan(CardFaceQuery.From(Cards().First(c => (string)c["id"] == "core.strike")));
            Assert.That(plan.Composed, Is.False);
            Assert.That(plan.Painting.Kind, Is.EqualTo(CardArtKind.Library));
        }

        [Test]
        public void TextFitsAreAuthoredLargestToSmallest()
        {
            foreach (var fit in new[] { Face.Config.Fit(CardFaceKeys.Title), Face.Config.Fit(CardFaceKeys.Rules) })
            {
                Assert.That(fit.Max, Is.GreaterThan(fit.Min));
                Assert.That(fit.Lines, Is.GreaterThan(0));
            }
            Assert.That(Face.Config.DesignHeight / Face.Config.DesignWidth, Is.EqualTo(1.5).Within(1e-9), "the template is 2:3");
        }
    }
}
