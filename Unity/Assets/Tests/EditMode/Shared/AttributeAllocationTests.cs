using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-3a (D-1450..D-1459): the Progression hub's attribute allocation dialog (web progressionCards.js openProgressionAllocation over statAllocationCard.js). The dialog may spend no more points than the character holds, previews the
    /// attributes it would give (the numbers themselves are Tools/oracle-core2-progression.mjs's, compared in ProgressionStatsParityTests), and Done spends nothing in the configured profile: the web's applyLevelUp refuses outside a Rest
    /// visit's Level up service (model/levelup.js:414). Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class AttributeAllocationTests
    {
        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private string _dir;
        private SaveService _saves;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-allocation-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(Ashen.Generated.ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession WithPoints(int points)
        {
            var session = Core2RunSession.New(Core2HostParityTests.Content, _saves, 1, 779u, "reaver", "Aldric", null);
            session.EditRunForReview(run => run["level"]["unspentPoints"] = points);
            return session;
        }

        [Test]
        public void NoPointsMeansNothingToSpend()
        {
            var allocation = new AttributeAllocation(WithPoints(0), Ui);
            Assert.That(allocation.Points, Is.EqualTo(0));
            Assert.That(allocation.Increase(allocation.Rows()[0].Id), Is.False, "no point to put anywhere");
            Assert.That(allocation.DoneDisabled, Is.True);
        }

        [Test]
        public void PointsAreSpentOneAtATimeAndNeverBeyondWhatTheCharacterHolds()
        {
            var session = WithPoints(2);
            var allocation = new AttributeAllocation(session, Ui);
            var rows = allocation.Rows();
            Assert.That(allocation.Points, Is.EqualTo(2));
            Assert.That(rows.All(r => r.CanIncrease && !r.CanDecrease), Is.True);
            var first = rows[0];
            Assert.That(allocation.Increase(first.Id), Is.True);
            Assert.That(allocation.Increase(first.Id), Is.True);
            Assert.That(allocation.Increase(rows[1].Id), Is.False, "two points held, two spent");
            Assert.That(allocation.Remaining, Is.EqualTo(0));
            Assert.That(allocation.DoneDisabled, Is.False);
            var after = allocation.Rows();
            Assert.That(after[0].Value, Is.EqualTo(first.Value + 2), "the row shows the attribute with its pending points");
            Assert.That(after.All(r => !r.CanIncrease), Is.True);
            Assert.That(after[0].CanDecrease, Is.True);
            Assert.That(allocation.Decrease(first.Id), Is.True);
            Assert.That(allocation.Decrease(rows[1].Id), Is.False, "nothing to take back there");
            Assert.That(allocation.Remaining, Is.EqualTo(1));
        }

        [Test]
        public void ThePreviewMovesTheCardsAndDoneSpendsNothing()
        {
            var session = WithPoints(3);
            var held = (JObject)session.Run["attributes"];
            var allocation = new AttributeAllocation(session, Ui);
            var intelligence = allocation.Rows().First(r => r.Card.Id == "intelligence");
            var before = allocation.Rows().First(r => r.Card.Id == "intelligence").Card.MainSummary;
            allocation.Increase(intelligence.Id);
            allocation.Increase(intelligence.Id);
            Assert.That(allocation.Rows().First(r => r.Card.Id == "intelligence").Card.Value, Is.EqualTo(held.Value<double>("intelligence") + 2));
            Assert.That(allocation.Commit(), Is.False, "the web's applyLevelUp refuses outside a Rest visit's Level up service");
            var run = session.Run;
            Assert.That(run["attributes"].ToString(), Is.EqualTo(held.ToString()), "no attribute moved");
            Assert.That(run["level"].Value<int>("unspentPoints"), Is.EqualTo(3), "no point spent");
            Assert.That(before, Is.Not.Empty);
        }
    }
}
