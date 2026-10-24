using System;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// VP-6 (D-800): the data the combat overlays draw, held without the editor. The piles viewer lists exactly the cards in the pile (the draw pile sorted so its order is not shown), each with its
    /// name, cost and words; the flask rows have their effect text and an art id that resolves; the quick menu offers the pause rows and hides the armoury in a fight; the overlay focus mode and the
    /// prompt's close control are in the screen registry. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2OverlayTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        private static UiData _ui;

        private static RunContent Content => Core2HostParityTests.Content;
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-overlay-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2CombatFlow Fight()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 31415, "reaver", "Aldric", () => Epoch);
            session.EditRunForReview(r => { r["maxHp"] = 300; r["hp"] = 300; });
            session.Travel(session.ReachableNodes()[0]);
            return new Core2CombatFlow(session, Ui.Strings);
        }

        [Test]
        public void EachPileListsExactlyItsCardsWithTheirNamesCostsAndWords()
        {
            var flow = Fight();
            // Put one card of the hand in each spent pile so every pile has a card to list.
            var piles = flow.Session.State.Piles;
            piles.Discard.Add(piles.Hand[0]);
            piles.Exhaust.Add(piles.Hand[1]);
            piles.Hand.RemoveRange(0, 2);
            flow.Refresh();
            var view = flow.View;
            Assert.That(view.DiscardCount, Is.GreaterThan(0));
            Assert.That(view.ExhaustCount, Is.GreaterThan(0));
            foreach (var (pile, count) in new[] { (OverlayValues.PileDraw, view.DrawCount), (OverlayValues.PileDiscard, view.DiscardCount), (OverlayValues.PileExhaust, view.ExhaustCount) })
            {
                var cards = Core2CombatView.Pile(flow.Session, pile);
                Assert.That(cards.Count, Is.EqualTo(count), pile);
                foreach (var card in cards)
                {
                    Assert.That(card.Name, Is.Not.Empty, pile);
                    Assert.That(card.InstanceId, Is.Not.Empty, pile);
                    Assert.That(card.Text, Is.Not.Null, card.Name + " has its words");
                    Assert.That(card.Stamina, Is.GreaterThanOrEqualTo(0), card.Name);
                }
            }
        }

        [Test]
        public void TheDrawPileIsListedByNameSoItsOrderIsNotShown()
        {
            var flow = Fight();
            var names = Core2CombatView.Pile(flow.Session, OverlayValues.PileDraw).Select(c => c.Name).ToList();
            Assert.That(names, Is.Not.Empty);
            Assert.That(names, Is.EqualTo(names.OrderBy(n => n, StringComparer.Ordinal).ToList()));
            Assert.That(Core2CombatView.Pile(flow.Session, "nowhere"), Is.Empty, "an unknown pile is empty");
        }

        [Test]
        public void EveryFlaskRowHasItsEffectTextAndArtThatResolves()
        {
            var flow = Fight();
            Assert.That(flow.View.Flasks, Is.Not.Empty);
            foreach (var flask in flow.View.Flasks)
            {
                Assert.That(flask.Text, Is.Not.Empty, flask.Name + " says what it does");
                Assert.That(flask.ArtKey, Does.StartWith(OverlayValues.FlaskArtPrefix));
            }
            var catalog = flow.Session.State.Data.Flasks;
            var found = 0;
            foreach (var id in catalog.Ids)
            {
                var key = catalog.Get(id).Value<string>(OverlayKeys.FlaskArtKey);
                if (key == null) continue;
                var name = key.Substring(OverlayValues.FlaskArtPrefix.Length);
                Assert.That(Ui.Art.Find(ArtKinds.Flask, new StringArgs().Add(ArtPlaceholders.Name, name)), Is.Not.Null, id + " art " + key);
                found++;
            }
            Assert.That(found, Is.GreaterThan(0));
        }

        [Test]
        public void TheQuickMenuIsThePauseListInAFightAndTheScreenRegistryNamesTheOverlayFocus()
        {
            var rows = MenuRules.Evaluate(Ui.Menus.Pause, Ui.MenuContext(true, true)).Where(s => s.Visible).ToList();
            Assert.That(rows.Select(r => r.Def.Id), Does.Not.Contain("armoury"), "the Armoury is out of combat only");
            Assert.That(rows.Any(r => r.Def.Confirm == ConfirmIds.SaveQuit && r.Enabled), Is.True);
            Assert.That(rows.Any(r => r.Def.Confirm == ConfirmIds.Abandon && r.Enabled), Is.True);
            var screen = Ui.Screens.Get(ScreenIds.Core2Combat);
            Assert.That(screen.FocusModes[Core2UiValues.FocusOverlay], Does.Contain(UiNames.OverlayModal).And.Contain(UiNames.OverlayQuick));
            Assert.That(screen.FocusModes[Core2UiValues.FocusPrompt], Does.Contain(UiNames.PromptClose), "the offer's X is reachable on the pad");
        }
    }
}
