using System.Collections.Generic;
using System.Linq;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>US-0.4 validator (suite: Content). Runs against the committed content and against mutated in-memory copies.</summary>
    [TestFixture, Category("Content")]
    public class ContentValidatorTests
    {
        /// <summary>In-memory overlay over the committed content, so tests can mutate single files.</summary>
        private sealed class OverlaySource : IContentSource
        {
            private readonly IContentSource _inner;
            private readonly Dictionary<string, string> _overrides = new Dictionary<string, string>();
            public OverlaySource(IContentSource inner) => _inner = inner;
            public void Set(string path, JToken value) => _overrides[path] = value.ToString();
            public void Remove(string path) => _overrides[path] = null;
            public bool Exists(string p) => _overrides.TryGetValue(p, out var v) ? v != null : _inner.Exists(p);
            public string ReadText(string p) => _overrides.TryGetValue(p, out var v) ? v : _inner.ReadText(p);
            public JObject Json(string p) => (JObject)JsonContent.Parse(ReadText(p));
        }

        private static OverlaySource Overlay() => new OverlaySource(TestContent.Source);

        [Test]
        public void CommittedContentIsValid()
        {
            var report = new ContentValidator(TestContent.Source).Validate();
            Assert.That(report.FilesChecked, Is.GreaterThan(100));
            Assert.That(report.IsValid, Is.True, report.Summary(40));
        }

        [Test]
        public void ManifestCountsMatchTheShippedGame()
        {
            var m = ContentManifest.Load(TestContent.Source);
            Assert.That(m.Counts[ContentFiles.CatalogCards], Is.EqualTo(195));
            Assert.That(m.Counts[ContentFiles.CatalogRelics], Is.EqualTo(63));
            Assert.That(m.Counts[ContentFiles.CatalogEnemies], Is.EqualTo(33));
            Assert.That(m.Counts[ContentFiles.CatalogEvents], Is.EqualTo(25));
        }

        [Test]
        public void EditedFileWithoutManifestUpdateFailsTheHashCheck()
        {
            var src = Overlay();
            var cards = src.Json(ContentFiles.CatalogCards);
            cards["strike"]["cost"] = 2;
            src.Set(ContentFiles.CatalogCards, cards);
            var report = new ContentValidator(src).Validate();
            Assert.That(report.ByRule(ValidationRules.Hash).Select(i => i.File), Does.Contain(ContentFiles.CatalogCards));
        }

        [Test]
        public void DanglingReferenceIsReportedWithFileRowAndField()
        {
            var src = Overlay();
            var enc = src.Json(ContentFiles.CatalogEncounters);
            var first = enc.Properties().First();
            ((JArray)first.Value["enemies"])[0] = "noSuchEnemy";
            src.Set(ContentFiles.CatalogEncounters, enc);
            var issue = new ContentValidator(src).Validate().ByRule(ValidationRules.Ref).Single();
            Assert.That(issue.File, Is.EqualTo(ContentFiles.CatalogEncounters));
            Assert.That(issue.Path, Does.StartWith(first.Name + ".enemies"));
            Assert.That(issue.Message, Does.Contain("noSuchEnemy"));
        }

        [Test]
        public void TypeErrorsAndUnknownPropertiesAreReported()
        {
            var src = Overlay();
            var cards = src.Json(ContentFiles.CatalogCards);
            cards["strike"]["rarity"] = 5;
            cards["strike"]["costt"] = 1;
            src.Set(ContentFiles.CatalogCards, cards);
            var report = new ContentValidator(src).Validate();
            Assert.That(report.ByRule(ValidationRules.Type).Any(i => i.Path == "strike.rarity"), Is.True, report.Summary(10));
            Assert.That(report.ByRule(ValidationRules.Unknown).Any(i => i.Path == "strike.costt"), Is.True, report.Summary(10));
        }

        [Test]
        public void UnknownEffectOpIsReported()
        {
            var src = Overlay();
            var cards = src.Json(ContentFiles.CatalogCards);
            cards["strike"]["effects"][0]["op"] = "teleport";
            src.Set(ContentFiles.CatalogCards, cards);
            Assert.That(new ContentValidator(src).Validate().ByRule(ValidationRules.UnknownOp).Any(i => i.Message.Contains("teleport")), Is.True);
        }

        [Test]
        public void MissingRequiredStringKeyIsReported()
        {
            var src = Overlay();
            var strings = src.Json(ContentFiles.StringsEn);
            strings.Remove("card.strike.name");
            src.Set(ContentFiles.StringsEn, strings);
            Assert.That(new ContentValidator(src).Validate().ByRule(ValidationRules.String).Any(i => i.Path == "card.strike.name"), Is.True);
        }

        [Test]
        public void ManaCardWithoutStaminaViolatesTheCostRule()
        {
            var src = Overlay();
            var cards = src.Json(ContentFiles.CatalogCards);
            var manaCard = cards.Properties().First(p => (int?)p.Value["manaCost"] > 0);
            manaCard.Value["staminaCost"] = 0;
            src.Set(ContentFiles.CatalogCards, cards);
            var issue = new ContentValidator(src).Validate().ByRule(ValidationRules.Cost).Single();
            Assert.That(issue.Path, Is.EqualTo(manaCard.Name + ".staminaCost"));
        }

        [Test]
        public void ReferencePresetAppliesCleanlyOverItsBaseFiles()
        {
            var report = new ContentValidator(TestContent.Source).Validate();
            Assert.That(report.Issues.Where(i => i.File.StartsWith(RuleKeys.PresetFolder)), Is.Empty, report.Summary(20));
        }

        [TestCase("{\"a\":1,\"b\":{\"c\":2}}", "{\"b\":{\"c\":null,\"d\":3}}", "{\"a\":1,\"b\":{\"d\":3}}")]
        [TestCase("{\"a\":[1,2]}", "{\"a\":[3]}", "{\"a\":[3]}")]
        [TestCase("{\"a\":1}", "{\"a\":null}", "{}")]
        public void MergePatchFollowsRfc7386(string target, string patch, string expected)
        {
            var result = MergePatch.Apply(JToken.Parse(target), JToken.Parse(patch));
            Assert.That(JToken.DeepEquals(result, JToken.Parse(expected)), Is.True, result.ToString());
        }
    }
}
