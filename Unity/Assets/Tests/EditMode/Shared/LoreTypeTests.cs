using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-3a (D-1450..D-1459): card lore as the web sets it (models/LoreTypeModel.js resolveLoreType and loreParts, styles/kit.css :root[data-lore-*]), the Game rules multipliers and the Changelog. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class LoreTypeTests
    {
        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private string _dir;
        private SettingsSession _settings;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-lore-" + Guid.NewGuid().ToString("N"));
            var saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            _settings = new SettingsSession(Core2HostParityTests.Content, ProfileStore.Load(saves), core2: true);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        [Test]
        public void TheDefaultsAreTheWebs()
        {
            var type = _settings.Lore(Ui.Lore);
            Assert.That(type.Face, Is.EqualTo("fell"));
            Assert.That(type.IdentityItalic, Is.True);
            Assert.That(type.WindowItalic, Is.True);
            Assert.That(type.IdentityPx, Is.EqualTo(16), "1 x 1.6rem");
            Assert.That(type.WindowPx, Is.EqualTo(17), "1.7rem");
            Assert.That(type.WindowIdentityPx, Is.EqualTo(19), "the window's identity paragraph is 0.2rem larger");
            Assert.That(type.LeadingOfNatural, Is.EqualTo(1.6 / 1.2).Within(1e-9));
            Assert.That(type.TrackingEm, Is.EqualTo(0));
        }

        [Test]
        public void EachChoiceMovesWhatTheWebsStylesheetMoves()
        {
            Assert.That(_settings.Set(LoreKeys.Size, "XL"), Is.Null);
            Assert.That(_settings.Set(LoreKeys.Tracking, "wide"), Is.Null);
            Assert.That(_settings.Set(LoreKeys.Leading, "loose"), Is.Null);
            Assert.That(_settings.Set(LoreKeys.Slant, "upright"), Is.Null);
            Assert.That(_settings.Set(LoreKeys.Face, "garamond"), Is.Null);
            var type = _settings.Lore(Ui.Lore);
            Assert.That(type.Face, Is.EqualTo("garamond"));
            Assert.That(type.IdentityPx, Is.EqualTo(20), "1.25 x 1.6rem");
            Assert.That(type.WindowPx, Is.EqualTo(22));
            Assert.That(type.TrackingEm, Is.EqualTo(0.04));
            Assert.That(type.LeadingOfNatural, Is.EqualTo(1.85 / 1.2).Within(1e-9));
            Assert.That(type.WindowItalic, Is.False, "the lore window's slant is its own row");
            Assert.That(type.IdentityItalic, Is.True, "and the identity line's another");
        }

        [Test]
        public void ACinzelLineIsAlwaysUprightAndAFaceNotShippedIsDrawnInItsSubstitute()
        {
            Assert.That(_settings.Set(LoreKeys.Face, "cinzel"), Is.Null);
            var cinzel = _settings.Lore(Ui.Lore);
            Assert.That(cinzel.Face, Is.EqualTo("cinzel"));
            Assert.That(cinzel.IdentityItalic || cinzel.WindowItalic, Is.False, "Cinzel has no italic (kit.css:4414)");
            Assert.That(_settings.Set(LoreKeys.Face, "georgia"), Is.Null);
            Assert.That(_settings.Lore(Ui.Lore).Face, Is.EqualTo(Ui.Lore.Substitutes["georgia"]), "Georgia is the platform's own face, which a build cannot ship");
        }

        [Test]
        public void AValueNotOnItsListFallsBackToTheDefault()
        {
            var type = Ui.Lore.Resolve("comic sans", "XXL", "wild", "huge", "oblique", "oblique");
            Assert.That(type.Face, Is.EqualTo("fell"));
            Assert.That(type.WindowPx, Is.EqualTo(17));
            Assert.That(type.TrackingEm, Is.EqualTo(0));
            Assert.That(type.IdentityItalic && type.WindowItalic, Is.True);
        }

        [Test]
        public void LoreIsSplitIntoItsIdentityHistoryAndClosingLine()
        {
            var card = LoreParts.Split("First cut of the hamlets.\n\nMade with whatever edge the hamlet owns.\n\nThe chosen learned this and nothing more.\n\nThe marked learned a thousand cuts.");
            Assert.That(card.Identity, Is.EqualTo("First cut of the hamlets."));
            Assert.That(card.Body, Is.EqualTo(new[] { "Made with whatever edge the hamlet owns.", "The chosen learned this and nothing more." }));
            Assert.That(card.Closing, Is.EqualTo("The marked learned a thousand cuts."));
            Assert.That(card.HasMore, Is.True);
            var two = LoreParts.Split("Identity.\n\nHistory.");
            Assert.That(two.Body, Is.EqualTo(new[] { "History." }), "one part after the identity is history, not a closing line");
            Assert.That(two.Closing, Is.Empty);
            var blurb = LoreParts.Split("Honest steel. It asks nothing clever of you.");
            Assert.That(blurb.Identity, Is.EqualTo("Honest steel. It asks nothing clever of you."));
            Assert.That(blurb.HasMore, Is.False, "an equipment blurb is an identity line alone");
            Assert.That(LoreParts.Split(null).Identity, Is.Empty);
        }

        [Test]
        public void EveryCardWithLoreHasAnIdentityLine()
        {
            var cards = Core2HostParityTests.Content.Core2.Combat.Run.Run.Cards.All.Where(c => !string.IsNullOrEmpty(c.Str(CombatKeys.Flavor))).ToList();
            Assert.That(cards.Count, Is.GreaterThan(300));
            Assert.That(cards.All(c => LoreParts.Split(c.Str(CombatKeys.Flavor)).Identity.Length > 0), Is.True);
        }

        [Test]
        public void TheXpMultipliersTakeTheWebsBounds()
        {
            Assert.That(_settings.Number(SettingsRulesKeys.ClassXp), Is.EqualTo(1), "the web's default");
            Assert.That(_settings.Set(SettingsRulesKeys.ClassXp, 2.5), Is.Null);
            Assert.That(_settings.Number(SettingsRulesKeys.ClassXp), Is.EqualTo(2.5));
            Assert.That(_settings.Set(SettingsRulesKeys.WeaponXp, 10), Is.Null);
            Assert.That(_settings.Set(SettingsRulesKeys.WeaponXp, 10.5), Is.Not.Null, "between 0 and 10");
            Assert.That(_settings.Set(SettingsRulesKeys.WeaponXp, -1), Is.Not.Null);
            _settings.ResetAll();
            Assert.That(_settings.Number(SettingsRulesKeys.ClassXp), Is.EqualTo(1), "Reset all settings puts them back");
        }

        [Test]
        public void TheChangelogIsChangelogMdNewestFirstByDay()
        {
            var groups = Ui.Changelog.Groups;
            Assert.That(groups, Is.Not.Empty);
            Assert.That(groups.Select(g => g.Title), Is.Ordered.Descending, "newest changes are first");
            Assert.That(groups.SelectMany(g => g.Entries).All(e => e.Summary.Length > 0 && e.Build.Length > 0), Is.True);
            var md = File.ReadAllText(Path.Combine(TestContent.RepoRoot, "CHANGELOG.md"));
            var first = groups[0].Entries[0];
            Assert.That(md, Does.Contain("[" + first.Build + "]"), "an entry names a version CHANGELOG.md lists");
        }
    }
}
