using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-23 (D-1386): the Character pane's Progression door over a new Reaver (web CharacterSheetModel.characterLadder, model/progression.js levelProgress, read from the running web at seed ASHEN1): twenty levels, level one the
    /// start, each later level an attribute point, the pools it grows and the deck floor when it climbs. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class CharacterProgressionTests
    {
        private string _dir;
        private SaveService _saves;
        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-progress-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession NewRun() => Core2RunSession.New(Core2HostParityTests.Content, _saves, 1, 779u, "reaver", "Aldric", null);

        [Test]
        public void TheLadderRunsTwentyLevelsFromTheStartAndEachLaterLevelPaysAPoint()
        {
            var view = CharacterProgression.For(NewRun(), Ui);
            Assert.That(view.Rows.Count, Is.EqualTo(20));
            Assert.That(view.Cadence, Is.EqualTo("Levels 1–20. Each level is claimed with Level up; its rewards are below."));
            Assert.That(view.Label, Is.EqualTo("Level 1"));
            Assert.That(view.Rows[0].State, Is.EqualTo("current"));
            Assert.That(view.Rows[1].State, Is.EqualTo("next"));
            Assert.That(view.Rows[2].State, Is.EqualTo("locked"));
            Assert.That(view.Rows[0].Grants.Select(g => g.Text), Is.EqualTo(new[] { "Starting level" }));
            Assert.That(view.Rows[1].Grants[0].Text, Is.EqualTo("Attribute points +1"));
            Assert.That(view.Rows[1].Grants[0].Routine, Is.True);
            Assert.That(view.Rows[1].Grants.Skip(1).All(g => !g.Routine), Is.True, "a pool or a deck floor is what the eye lands on");
            Assert.That(view.Rows[1].Step, Does.StartWith("+").And.EndWith("XP"));
            Assert.That(view.PointsLabel, Is.EqualTo("0 points available"));
            // the web's own ladder at this run (CharacterSheetModel.characterLadder, configured profile): level, step XP, total XP, grants
            var web = new (int Level, string Step, string Total, string Grants)[]
            {
                (2, "+200 XP", "200 total", "Attribute points +1, Max HP +2, Deck minimum 9"),
                (3, "+260 XP", "460 total", "Attribute points +1, Max HP +2"),
                (6, "+580 XP", "1,820 total", "Attribute points +1, Max HP +2, Mana +1, Deck minimum 11"),
                (11, "+2,170 XP", "8,660 total", "Attribute points +1, Max HP +2, Stamina +1, Mana +1"),
                (20, "+23,440 XP", "100,160 total", "Attribute points +1, Max HP +2, Deck minimum 18"),
            };
            foreach (var w in web)
            {
                var row = view.Rows[w.Level - 1];
                Assert.That(row.Step, Is.EqualTo(w.Step), "step " + w.Level);
                Assert.That(row.Total, Is.EqualTo(w.Total), "total " + w.Level);
                Assert.That(string.Join(", ", row.Grants.Select(g => g.Text)), Is.EqualTo(w.Grants), "grants " + w.Level);
            }
        }

        [Test]
        public void TheAttributesCardListsTheCharactersOwnAttributes()
        {
            var view = CharacterProgression.For(NewRun(), Ui);
            Assert.That(view.Attributes, Is.Not.Empty);
            Assert.That(view.Summary, Does.Contain(view.Attributes[0].Short + ": " + view.Attributes[0].Value));
        }
    }
}
