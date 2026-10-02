using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// The meta screens' engine-free views (W-16 journal and the rest of the meta lane): built from a real profile written
    /// by a played climb, every string resolved.
    /// </summary>
    [TestFixture]
    public class MetaViewTests
    {
        private static RunContent _default;
        private static UiData _ui;
        private string _dir;
        private SaveService _saves;

        private static RunContent Default => _default ??= RunContent.Load(TestContent.Source);
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-meta-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            RunSession.ReviewEnemyHpScale = null;
        }

        [TearDown]
        public void TearDown()
        {
            RunSession.ReviewEnemyHpScale = null;
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static void Resolved(params string[] texts)
        {
            foreach (var t in texts.Where(t => t != null))
                Assert.That(t.Contains("{") || t.StartsWith("journal.", StringComparison.Ordinal), Is.False, "unresolved: " + t);
        }

        // ------------------------------------------------------------------ W-16 journal (US-13.1)

        [Test]
        public void AnEmptyProfileShowsAnEmptyJournal()
        {
            var view = JournalView.Build(new JObject(), Default, Ui);
            Assert.That(view.History, Is.Empty);
            Assert.That(view.HistoryEmpty, Is.Not.Null);
            Assert.That(view.ArmamentsEmpty, Is.Not.Null);
            Assert.That(view.Rail.Select(r => r.Id), Is.EqualTo(new[] { MetaValues.JournalHistory, MetaValues.JournalProfile, MetaValues.JournalArmaments }));
            Resolved(view.Rail.Select(r => r.Label).Concat(view.Profile).Concat(view.Bosses).Concat(view.Unlocks).Concat(new[] { view.Title, view.HistoryEmpty, view.ArmamentsEmpty }).ToArray());
        }

        [Test]
        public void ADeathIsRecordedWithItsKillerDurationAndDeck()
        {
            var session = RunSession.New(Default, _saves, 1, 90210, Default.DefaultClass(), "Aldric");
            var driver = new ClimbSessionTests.Driver(Default, _saves, session, 90210) { EndTurnsOnly = true, Reload = false };
            Assert.That(driver.Play(400), Is.EqualTo("defeat"));

            // Read back from disk, as the title's Journal would.
            var profile = ProfileStore.Load(_saves).Doc;
            var record = (JObject)((JArray)profile["results"]).Last();
            Assert.That(record[MetaKeys.Killer], Is.InstanceOf<JArray>().And.Not.Empty, "the killer's enemy ids (D-148)");
            Assert.That(record[MetaKeys.Duration], Is.Not.Null);
            Assert.That(((JArray)record[MetaKeys.Deck]).Count, Is.EqualTo(((JArray)session.Run["deck"]).Count), "the final deck");
            Assert.That(JToken.DeepEquals(record, session.End.Result), Is.True, "the receipt carries the same record");

            var view = JournalView.Build(profile, Default, Ui);
            Assert.That(view.History, Has.Count.EqualTo(1));
            var row = view.History[0];
            Assert.That(row.Victory, Is.False);
            Assert.That(row.Title, Does.Contain("Aldric"));
            Assert.That(row.Detail.Any(d => d.StartsWith("Slain by", StringComparison.Ordinal)), Is.True, "the killer is named");
            Assert.That(row.Detail.Any(d => d.StartsWith("Time", StringComparison.Ordinal)), Is.True);
            Assert.That(row.Deck, Has.Count.EqualTo(((JArray)record[MetaKeys.Deck]).Count));
            Assert.That(view.WinRates, Has.Count.EqualTo(1));
            Assert.That(view.WinRates[0], Does.Contain("0 of 1"));
            Resolved(row.Detail.Concat(row.Deck).Concat(row.Pills).Concat(view.Profile).Concat(view.WinRates).Concat(new[] { row.Title, row.Where, row.Outcome, row.DeckTitle }).ToArray());
        }

        [Test]
        public void AnOlderRecordWithoutTheJournalFieldsReadsAsUnknown()
        {
            var profile = JObject.Parse("{\"results\":[{\"victory\":true,\"seed\":\"ABC123\",\"class\":\"herald\",\"className\":\"\",\"act\":3,\"floor\":12,\"fightsWon\":30,\"custom\":true,\"ascension\":2}],"
                + "\"progress\":{\"runs\":1,\"wins\":1,\"maxAct\":3,\"bosses\":[\"bellKeeper\"],\"wonClasses\":[\"herald\"],\"maxClassLevel\":4,\"bossGroups\":{}},\"unlocked\":[],\"discoveredArmaments\":[\"dagger\",\"dagger\"]}");
            var view = JournalView.Build(profile, Default, Ui);
            var row = view.History.Single();
            Assert.That(row.Victory, Is.True);
            Assert.That(row.Pills, Has.Count.EqualTo(2), "custom and ascension pills");
            Assert.That(row.Detail, Has.Member("Unknown."));
            Assert.That(row.Deck, Is.Empty);
            Assert.That(view.Armaments, Is.EqualTo(new[] { "Dagger" }), "found armaments, once each, by name");
            Assert.That(view.Bosses.Single(), Is.Not.EqualTo("bellKeeper"), "bosses by name");
            Resolved(row.Detail.Concat(row.Pills).Concat(view.Profile).Concat(view.Bosses).Concat(new[] { row.Title, row.Where, row.DeckTitle }).ToArray());
        }
    }
}
