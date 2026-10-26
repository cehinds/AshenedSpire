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

        // ------------------------------------------------------------------ VP-15: motifs, counted tags, the mirrored mark

        private static CardFaceQuery Spell(string element, params CardEffectRef[] effects) => new CardFaceQuery
        {
            CardId = "x", Type = "attack", Tags = new[] { "camp:spell", "maneuver:casting", "element:" + element }, Effects = effects,
        };

        [Test]
        public void ASpellWearsItsElementsWashAndLineArtOverItsPainting()
        {
            var plan = Face.Plan(Spell("fire"));
            Assert.That(plan.Motifs.Count, Is.EqualTo(1));
            var element = plan.Motifs[0];
            Assert.That(element.IsElement, Is.True);
            Assert.That(element.Motif, Is.EqualTo("embers"));
            Assert.That(element.Tint, Is.EqualTo("#ffc17e"));
            Assert.That(element.Glow, Is.EqualTo("#ee7237"));
            Assert.That(element.Opacity, Is.EqualTo(0.34).Within(1e-9));
            Assert.That(Face.Config.MotifGlyphs, Does.Contain(element.Motif));
        }

        [Test]
        public void StatusesJoinTheElementAlphabeticallyInTheirOwnTintAndTheThirdIsShifted()
        {
            var effects = new[]
            {
                new CardEffectRef { Op = "applyStatus", Status = "weak", Target = "enemy" },
                new CardEffectRef { Op = "applyStatus", Status = "bleed", Target = "enemy" },
                new CardEffectRef { Op = "applyStatus", Status = "dexterity", Target = "self" },
            };
            var plan = Face.Plan(Spell("fire", effects));
            Assert.That(plan.Motifs.Select(m => m.Id), Is.EqualTo(new[] { "fire", "bleed", "dexterity" }), "an element leaves room for two statuses, in id order");
            Assert.That(plan.Motifs[1].Motif, Is.EqualTo("drops"));
            Assert.That(plan.Motifs[1].Tint, Is.EqualTo("#e46a44"), "bleed's registry tint (the ember token)");
            Assert.That(plan.Motifs[2].Tint, Is.EqualTo("#aac5dd"), "a status with no tint of its own takes its motif's colour");
            Assert.That(plan.Motifs[1].Opacity, Is.EqualTo(0.42).Within(1e-9));
            Assert.That(plan.Motifs[2].Offset, Is.EqualTo(-0.07).Within(1e-9), "the third layer is lifted 7% of the art");
            var weak = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:attack" }, Effects = new[] { effects[0] } });
            Assert.That(weak.Motifs.Single().Tint, Is.EqualTo("#8e826a"), "weak's registry tint (the muted token) wins over its motif colour");
            Assert.That(weak.Motifs.Single().Motif, Is.EqualTo("fractures"));
        }

        [Test]
        public void AStatusTheRegistryDoesNotKnowDrawsNothingAndAPlainStrikeHasNoMotifs()
        {
            var unknown = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:attack" }, Effects = new[] { new CardEffectRef { Op = "applyStatus", Status = "nonsense", Target = "enemy" } } });
            Assert.That(unknown.Motifs, Is.Empty);
            Assert.That(PlanOf("core.strike").Motifs, Is.Empty);
        }

        [Test]
        public void EveryMotifTheFaceNamesIsASpriteInBothStrokes()
        {
            foreach (var id in Face.Config.MotifGlyphs)
                foreach (var stroke in new[] { UiFormats.CardMotifElement, UiFormats.CardMotifStatus })
                    Assert.That(File.Exists(Path.Combine(GlyphFolder, "motif-" + id + "-" + stroke + ".png")), Is.True, id + stroke);
            Assert.That(File.Exists(Path.Combine(GlyphFolder, "wash.png")), Is.True);
        }

        [Test]
        public void ASpellSkillPrintsTheSpellMarkAndAPowerStaysAPower()
        {
            var skill = Face.Plan(new CardFaceQuery { CardId = "x", Type = "skill", Tags = new[] { "camp:spell", "maneuver:casting" } });
            Assert.That(skill.Sigil, Is.EqualTo("spell"), "a spell-camp skill is a Spell to the web's combatCardType");
            var power = Face.Plan(new CardFaceQuery { CardId = "x", Type = "power", Tags = new[] { "camp:spell", "maneuver:casting" } });
            Assert.That(power.Sigil, Is.EqualTo("power"));
            var physical = Face.Plan(new CardFaceQuery { CardId = "x", Type = "skill", Tags = new[] { "camp:physical", "maneuver:casting" } });
            Assert.That(physical.Sigil, Is.EqualTo("skill"));
            var ranged = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:ranged" } });
            Assert.That(ranged.Sigil, Is.EqualTo("attack"), "a physical ranged attack is an Attack");
        }

        [Test]
        public void ASpellThatMirrorsABasicCardShowsBothMarksAndTheBasicsWord()
        {
            var cantrip = Face.Plan(CardFaceQuery.From(Cards().First(c => (string)c["id"] == "core.cantrip")));
            Assert.That(cantrip.Sigil, Is.EqualTo("spell"));
            Assert.That(cantrip.MirrorSigil, Is.EqualTo("attack"));
            Assert.That(cantrip.Word, Is.EqualTo("attack"));
            var technique = Face.Plan(new CardFaceQuery { CardId = "x", Type = "skill", Tags = new[] { "camp:spell", "maneuver:casting" }, Mirrors = "technique" });
            Assert.That(technique.MirrorSigil, Is.EqualTo("skill"), "Technique wears the Skill mark");
            Assert.That(technique.Word, Is.EqualTo("technique"));
            Assert.That(Ui.Strings.Has(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiFormats.CardSigilKey, technique.Word)), Is.True);
            var notSpell = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:attack" }, Mirrors = "attack" });
            Assert.That(notSpell.MirrorSigil, Is.Null, "only a Spell mirrors");
            var self = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:spell", "maneuver:casting" }, Mirrors = "spell" });
            Assert.That(self.MirrorSigil, Is.Null, "a Spell does not mirror itself");
        }

        [Test]
        public void DamageTypesReplaceTheElementOnTheRailAndSlashingReadsAsTheAttackSwords()
        {
            var hit = new CardEffectRef { Op = "damage", Target = "enemy" };
            var plan = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:attack", "damage:slashing", "damage:physical" }, Effects = new[] { hit } });
            Assert.That(plan.Rail, Is.EqualTo(new[] { "guard", "attack" }), "physical reads Guard (once) and slashing reads Attack");
            var dull = Face.Plan(new CardFaceQuery { CardId = "x", Type = "skill", Tags = new[] { "camp:physical", "damage:fire" } });
            Assert.That(dull.Rail, Does.Not.Contain("fire"), "a card that deals no damage wears no damage type");
        }

        [Test]
        public void AStanceEntrysPrecisionAndAPlainSelfGrantCarryTheirNumbers()
        {
            var smash = new CardFaceQuery
            {
                CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:smash" },
                Effects = new[] { new CardEffectRef { Op = "damage", Target = "enemy", Index = 0 } }, EntryPrecision = 3,
            };
            var plan = Face.Plan(smash);
            Assert.That(plan.Rail, Is.EqualTo(new[] { "guard", "ranged", "smash" }), "web combat-card-inspect: the camp mark, the counted Precision, then the Smash fist");
            Assert.That(plan.RailCounts, Is.EqualTo(new Dictionary<string, int> { ["ranged"] = 3 }));
            var grant = new CardEffectRef { Op = "applyStatus", Target = "self", Status = "evade", Index = 0, Stacks = 2, Plain = true };
            var evade = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:evade" }, Effects = new[] { grant } });
            Assert.That(evade.Rail, Is.EqualTo(new[] { "evade" }), "the Evade identity mark and the Evade benefit are one mark");
            Assert.That(evade.RailCounts["evade"], Is.EqualTo(2));
            var twice = Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:evade" }, Effects = new[] { grant, new CardEffectRef { Op = "applyStatus", Target = "self", Status = "evade", Index = 1, Stacks = 1, Plain = true } } });
            Assert.That(twice.RailCounts, Is.Empty, "two grants are not one promise");
        }

        [Test]
        public void ARiderStatusCountsOnlyWhenOneUngatedApplicationStatesIt()
        {
            CardFacePlan Plan(params CardEffectRef[] effects) => Face.Plan(new CardFaceQuery { CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:attack" }, Effects = effects });
            var once = Plan(new CardEffectRef { Op = "applyStatus", Target = "enemy", Status = "bleed", Index = 1, Stacks = 4, Plain = true });
            Assert.That(once.Rail, Does.Contain("bleed"));
            Assert.That(once.RailCounts["bleed"], Is.EqualTo(4));
            var live = Plan(new CardEffectRef { Op = "applyStatus", Target = "enemy", Status = "bleed", Index = 1, Stacks = 4, Live = 6, Plain = true });
            Assert.That(live.RailCounts["bleed"], Is.EqualTo(6), "the preview's figure wins");
            var gated = Plan(new CardEffectRef { Op = "applyStatus", Target = "enemy", Status = "bleed", Index = 1, Stacks = 4, Gated = true });
            Assert.That(gated.RailCounts, Is.Empty);
            var nested = Plan(new CardEffectRef { Op = "applyStatus", Target = "enemy", Status = "bleed", Index = -1, Stacks = 4 });
            Assert.That(nested.RailCounts, Is.Empty);
        }

        [Test]
        public void ANumberedTraitTakesItsValue()
        {
            var plan = Face.Plan(new CardFaceQuery
            {
                CardId = "x", Type = "attack", Tags = new[] { "camp:physical", "maneuver:attack", "trait:impact" }, TraitValues = new Dictionary<string, double> { ["impact"] = 2 },
            });
            Assert.That(plan.RailCounts["impact"], Is.EqualTo(2));
        }

        [Test]
        public void TheEffectRowsOfACardKnowTheirPlaceAndWhetherANumberStandsForThem()
        {
            var def = JObject.Parse("{\"id\":\"x\",\"type\":\"skill\",\"effects\":[{\"op\":\"applyStatus\",\"target\":\"self\",\"status\":\"precision\",\"stacks\":{\"f\":\"add\",\"args\":[1,2]}},{\"op\":\"applyStatus\",\"target\":\"enemy\",\"status\":\"weak\",\"stacks\":1,\"chance\":50},{\"op\":\"damage\",\"if\":{\"p\":\"x\"},\"amount\":1}]}");
            var rows = CardFaceQuery.From(def).Effects;
            Assert.That(rows.Select(r => r.Index), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(rows[0].Stacks, Is.EqualTo(3d));
            Assert.That(rows[0].Plain, Is.True);
            Assert.That(rows[0].Gated, Is.False);
            Assert.That(rows[1].Gated, Is.True, "a chance below 100 gates it");
            Assert.That(rows[2].Gated, Is.True, "a condition gates it");
        }
    }
}
