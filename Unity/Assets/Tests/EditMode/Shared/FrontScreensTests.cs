using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// VP-12 (D-1003 to D-1005): the logic behind the front-of-house screens, held without the editor. The creation Review reads the run a class would make without writing a slot or the
    /// profile; it lists the armour, the main hand, the relic and the starting deck with its copies; a locked class says how many games open it.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class FrontScreensTests
    {
        private string _dir;
        private SaveService _saves;
        private static UiData _ui;

        private static RunContent Content => Core2HostParityTests.Content;
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-front-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private CreationClasses Classes() => CreationClasses.For(Content, RunFlowValues.RulesetCore2, ProfileStore.Load(_saves).Doc);

        // ------------------------------------------------------------------ the Review pane

        [Test]
        public void AClassPreviewWritesNothing()
        {
            var classId = Classes().Default();
            var preview = Core2RunSession.Preview(Content, _saves, 1u, classId, "Aldric");
            Assert.That(preview.ClassId, Is.EqualTo(classId));
            Assert.That(SlotSummaries.ReadOne(_saves, 1).Status, Is.EqualTo(SlotStatus.Empty), "no slot is written");
            Assert.That(Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*", SearchOption.AllDirectories) : new string[0], Is.Empty, "nothing is on disk, not even the profile");
        }

        [Test]
        public void TheReviewListsTheKitTheClassStartsWith()
        {
            var classes = Classes();
            var classId = classes.Default();
            var review = Core2CreationReview.Build(Core2RunSession.Preview(Content, _saves, 1u, classId, "Aldric"), Ui);
            Assert.That(review.ClassName, Is.Not.Empty);
            Assert.That(review.Epithet, Is.Not.Empty);
            Assert.That(review.Attributes, Has.Count.EqualTo(5), "STR DEX CON WIS INT");
            Assert.That(review.Resources, Has.Count.EqualTo(6), "HP MP SP HAND DRAW SIZE");
            Assert.That(review.ClassStats, Has.Count.EqualTo(5), "HP SP MP HAND DRAW");
            Assert.That(review.Armour, Is.Not.Null, "the body piece");
            Assert.That(review.MainHand, Is.Not.Null, "the right hand's weapon");
            Assert.That(review.Relic, Is.Not.Null, "the starting relic");
            Assert.That(review.RelicName, Is.EqualTo(review.Relic.Face.Name));
            Assert.That(review.Deck.Sum(c => c.Count), Is.EqualTo(review.DeckSize), "the copies add up to the deck");
            Assert.That(review.Deck.Count, Is.LessThan(review.DeckSize), "equal cards share one face with a count");
            Assert.That(review.BeginHint, Does.Contain(review.ClassName).And.Contain(review.Relic.Face.Name));
            foreach (var text in review.Attributes.Select(a => a.Key).Concat(review.Resources.Select(r => r.Key)).Concat(new[] { review.Armour.Heading, review.MainHand.Heading, review.Relic.Heading, review.BeginHint }))
                Assert.That(text, Does.Not.Contain("{").And.Not.Contain("creation."), text);
        }

        [Test]
        public void EveryClassThatCanBeginHasAReview()
        {
            var classes = Classes();
            var shown = 0;
            foreach (var id in classes.Ids.Where(i => classes.Problem(i) == null))
            {
                var review = Core2CreationReview.Build(Core2RunSession.Preview(Content, _saves, 1u, id, "Aldric"), Ui);
                Assert.That(review.Deck, Is.Not.Empty, id);
                Assert.That(review.MainHand ?? review.Armour, Is.Not.Null, id);
                shown++;
            }
            Assert.That(shown, Is.GreaterThan(0));
        }

        [Test]
        public void ALockedClassSaysHowManyGamesOpenIt()
        {
            var classes = Classes();
            var locked = classes.Ids.Where(classes.Locked).ToList();
            Assert.That(locked, Is.Not.Empty, "a fresh profile has locked classes");
            Assert.That(classes.GamesToUnlock(locked[0]), Is.EqualTo(1), "the next class opens after one finished game");
            Assert.That(locked.Select(classes.GamesToUnlock), Is.Ordered, "later classes need at least as many");
            Assert.That(classes.GamesToUnlock(classes.Default()), Is.EqualTo(0), "an open class needs none");
        }

        [Test]
        public void ThePreviewSeedDoesNotChangeTheKit()
        {
            var classId = Classes().Default();
            var a = Core2CreationReview.Build(Core2RunSession.Preview(Content, _saves, 1u, classId, "A"), Ui);
            var b = Core2CreationReview.Build(Core2RunSession.Preview(Content, _saves, 4242u, classId, "B"), Ui);
            Assert.That(a.Deck.Select(c => c.Card.CardId + c.Count), Is.EqualTo(b.Deck.Select(c => c.Card.CardId + c.Count)));
            Assert.That(a.Armour.Face.Name, Is.EqualTo(b.Armour.Face.Name));
            Assert.That(a.MainHand.Face.Name, Is.EqualTo(b.MainHand.Face.Name));
        }

        [Test]
        public void TheSeedFieldRoundTripsThroughTheCodec()
        {
            var text = Content.Seeds.Format(90210u);
            Assert.That(Content.Seeds.Parse(text), Is.EqualTo(90210u));
            Assert.That(Content.Seeds.Problem("not a seed!"), Is.Not.Null, "punctuation is not part of the vocabulary");
        }

        // ------------------------------------------------------------------ the opening (W-05)

        private Ashen.App.Settings.SettingsSession Settings() => new Ashen.App.Settings.SettingsSession(Content, ProfileStore.Load(_saves));

        [Test]
        public void ANewCoreClimbPlaysTheFiveScenesOfTheOpeningAndEndsOnSetForth()
        {
            var flow = PrologueFlow.For(Content, Settings(), "reaver", "Aldric");
            Assert.That(flow, Is.Not.Null, "the data and the setting turn the opening on");
            var seen = new System.Collections.Generic.List<PrologueViewState>();
            while (flow.Pending)
            {
                seen.Add(flow.View(Ui));
                flow.Advance();
            }
            Assert.That(seen.Select(v => v.SceneId), Is.EqualTo(new[] { "warmth", "year", "carry", "night", "step" }));
            Assert.That(seen.Select(v => v.IsLast), Is.EqualTo(new[] { false, false, false, false, true }));
            Assert.That(seen.Select(v => v.Progress), Is.EqualTo(new[] { "1 / 5", "2 / 5", "3 / 5", "4 / 5", "5 / 5" }));
            Assert.That(seen[0].Title, Is.EqualTo("Remembered warmth"));
            Assert.That(seen[0].Speaker, Is.EqualTo("Hamlet witness"));
            Assert.That(seen[0].Line, Is.EqualTo("Three hearths once kept the roads warm.\nWe lived beyond their walls."));
            Assert.That(seen[2].ArtWide, Is.EqualTo("prologue.carry-reaver-desktop"), "the class's own painting");
            Assert.That(seen[2].Speaker, Is.EqualTo("Reaver"), "the carry scene is spoken by the class");
            Assert.That(seen[2].Line, Does.Contain("Fell Courtyard"), "and says the class's line");
            Assert.That(seen[4].Speaker, Is.EqualTo("Aldric"), "the first step is the character's");
            Assert.That(seen[4].Continue, Is.EqualTo("Set forth"));
            Assert.That(seen[0].Continue, Is.EqualTo("Continue"));
            Assert.That(seen[0].Skip, Is.EqualTo("Skip opening"));
            Assert.That(flow.View(Ui), Is.Null, "nothing is left");
        }

        [Test]
        public void SkippingEndsTheOpeningAtOnce()
        {
            var flow = PrologueFlow.For(Content, Settings(), "rogue", "Mira");
            flow.Advance();
            flow.Skip();
            Assert.That(flow.Pending, Is.False);
            Assert.That(flow.View(Ui), Is.Null);
        }

        [Test]
        public void TheSettingTurnsTheOpeningOff()
        {
            var settings = Settings();
            Assert.That(settings.Set(SettingIds.PlayPrologue, false), Is.Null);
            Assert.That(PrologueFlow.For(Content, settings, "reaver", "Aldric"), Is.Null);
            Assert.That(settings.Set(SettingIds.PlayPrologue, true), Is.Null);
            Assert.That(PrologueFlow.For(Content, settings, "reaver", "Aldric"), Is.Not.Null);
        }

        [Test]
        public void TheSavedOpeningOfThe405RunReadsThroughTheSameScenes()
        {
            var content = RunContent.Load(TestContent.Source, null, new[] { JObject.Parse("{\"files\":{\"rules/runFlow.json\":{\"newRun\":{\"prologue\":true}}}}") });
            var session = RunSession.New(content, _saves, 1, 77u, content.DefaultClass(), "Aldric");
            Assert.That(session.Opening.Pending, Is.True);
            var view = session.Opening.View(Ui);
            Assert.That(view.SceneId, Is.EqualTo("warmth"));
            Assert.That(view.Title, Is.EqualTo("Remembered warmth"));
            session.Opening.Skip();
            Assert.That(session.ProloguePending, Is.False);
        }
    }
}
