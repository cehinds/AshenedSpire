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

        // ------------------------------------------------------------------ Settings (W-18 laid out as the web's)

        private static System.Collections.Generic.IReadOnlyList<Ashen.App.Settings.SettingsTabDef> Layout => Ashen.App.Settings.SettingsLayout.From(Ui.Meta);

        [Test]
        public void TheLayoutNamesEveryControlOnce()
        {
            var settings = Settings();
            var tabs = Layout;
            Assert.That(tabs.Select(t => t.Id), Is.EqualTo(new[] { "general", "accessibility", "advanced" }));
            var shown = tabs.SelectMany(t => t.Groups).SelectMany(g => g.Categories).ToList();
            Assert.That(shown, Is.Unique, "a category is in one group");
            Assert.That(settings.Defs.Categories.Select(c => c.Key), Is.EquivalentTo(shown), "every category of settings/defaults.json has a place");
            foreach (var tab in tabs)
            {
                Assert.That(Ui.Strings.Has(tab.LabelKey), tab.LabelKey);
                foreach (var group in tab.Groups) Assert.That(Ui.Strings.Has(group.LabelKey), group.LabelKey);
            }
        }

        [Test]
        public void ThePageShowsTheOpenGroupsControls()
        {
            var settings = Settings();
            var page = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery());
            Assert.That(page.Tab, Is.EqualTo("general"));
            Assert.That(page.Group, Is.EqualTo("display"), "the first group opens");
            Assert.That(page.Tabs.Select(t => t.Label), Is.EqualTo(new[] { "General", "Accessibility", "Advanced" }));
            Assert.That(page.Groups.Select(g => g.Label), Is.EqualTo(new[] { "Display", "Combat", "Audio", "Game rules" }));
            Assert.That(page.Rows.Select(r => r.Category).Distinct(), Is.EquivalentTo(new[] { "interface", "map" }));
            Assert.That(page.Rows.Select(r => r.Key), Does.Contain("uiScale").And.Contain(SettingIds.MapMode));
            Assert.That(page.Count, Is.EqualTo(page.Rows.Count + " settings"));
            var audio = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Tab = "general", Group = "audio" });
            Assert.That(audio.Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.MasterVolume, SettingIds.MusicVolume, SettingIds.SfxVolume }));
            var access = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Tab = "accessibility" });
            Assert.That(access.Rows.Select(r => r.Category).Distinct(), Is.EqualTo(new[] { "accessibility" }));
            Assert.That(access.Groups, Has.Count.EqualTo(1));
            var advanced = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Tab = "advanced" });
            Assert.That(advanced.Rows.Select(r => r.Key), Is.EquivalentTo(new[] { SettingIds.PlayPrologue, SettingIds.OpeningAutoAdvance }));
            var about = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Tab = "advanced", Group = "about" });
            Assert.That(about.ShowAbout, Is.True);
            Assert.That(about.Rows, Is.Empty);
            Assert.That(about.Empty, Is.Null, "the About text is not an empty page");
            foreach (var row in page.Rows.Concat(audio.Rows).Concat(access.Rows).Concat(advanced.Rows))
                Assert.That(row.Label, Does.Not.Contain("settings.").And.Not.Contain("{"), row.Key);
        }

        [Test]
        public void SearchAndTheChangedFilterReachEverySection()
        {
            var settings = Settings();
            var volume = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Search = "volume" });
            Assert.That(volume.Filtering, Is.True);
            Assert.That(volume.Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.MasterVolume, SettingIds.MusicVolume, SettingIds.SfxVolume }), "found in the Audio group from the Display page");
            Assert.That(settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Search = "no such setting at all" }).Empty, Is.Not.Null);
            Assert.That(settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { ChangedOnly = true }).Rows, Is.Empty, "nothing is changed yet");
            Assert.That(settings.Set(SettingIds.MasterVolume, 50), Is.Null);
            Assert.That(settings.Set(SettingIds.ReducedMotion, true), Is.Null);
            var changed = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { ChangedOnly = true });
            Assert.That(changed.Rows.Select(r => r.Key), Is.EquivalentTo(new[] { SettingIds.MasterVolume, SettingIds.ReducedMotion }));
            Assert.That(changed.ChangedCount, Is.EqualTo(2));
            Assert.That(settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Search = "volume", ChangedOnly = true }).Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.MasterVolume }));
        }

        [Test]
        public void ResetGroupReturnsItsSettingsToTheirDefaults()
        {
            var settings = Settings();
            Assert.That(settings.Set(SettingIds.MasterVolume, 20), Is.Null);
            Assert.That(settings.Set(SettingIds.ReducedMotion, true), Is.Null);
            var audio = Layout.SelectMany(t => t.Groups).Single(g => g.Id == "audio");
            Assert.That(settings.ResetGroup(audio), Is.EqualTo(1));
            Assert.That(settings.Number(SettingIds.MasterVolume), Is.EqualTo(80), "the default volume is back");
            Assert.That(settings.On(SettingIds.ReducedMotion), Is.True, "another group is untouched");
            Assert.That(settings.ResetGroup(audio), Is.EqualTo(0), "nothing left to reset");
            settings.ResetAll();
            Assert.That(settings.On(SettingIds.ReducedMotion), Is.False);
        }

        [Test]
        public void ASliderSnapsToItsStepsInsideItsRange()
        {
            var settings = Settings();
            var control = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Tab = "general", Group = "audio" }).Rows[0];
            Assert.That(Ashen.App.Settings.SettingsSession.Stepped(control, control.Max + 500), Is.EqualTo(control.Max));
            Assert.That(Ashen.App.Settings.SettingsSession.Stepped(control, control.Min - 500), Is.EqualTo(control.Min));
            var inside = Ashen.App.Settings.SettingsSession.Stepped(control, control.Min + control.Step * 2.4);
            Assert.That(settings.Set(control.Key, inside), Is.Null, "a stepped value always fits the control");
        }

        // ------------------------------------------------------------------ Run history, Compendium, Profile, About

        private static JObject Profile(params string[] results) => JObject.Parse("{\"results\":[" + string.Join(",", results) + "]}");

        [Test]
        public void TheRunHistoryReadsLikeTheWebsRecords()
        {
            var profile = Profile(
                "{\"class\":\"reaver\",\"victory\":true,\"act\":3,\"floor\":15,\"fightsWon\":11,\"seed\":\"SHOWCASE\",\"name\":\"Aldric\"}",
                "{\"class\":\"starseer\",\"victory\":false,\"act\":1,\"floor\":4,\"fightsWon\":3,\"seed\":\"GOLDBOUGH\",\"name\":\"Mira\"}",
                "{\"class\":\"reaver\",\"victory\":false,\"act\":2,\"floor\":7,\"fightsWon\":6,\"seed\":\"ASHFALL\",\"name\":\"Aldric\",\"custom\":true}");
            var view = JournalView.Build(profile, Content, Ui);
            Assert.That(view.Summary, Is.EqualTo("2 standard runs · 1 won · 50% win rate · 1 custom (not counted)"));
            Assert.That(view.ClassRecords.Select(r => r.Value), Is.EquivalentTo(new[] { "1/2", "0/1" }), "a record per class with a run");
            Assert.That(view.History.Select(r => r.ResultPill), Is.EqualTo(new[] { "Perished", "Perished", "Rune restored" }), "newest first");
            Assert.That(view.History[2].WhereLine, Is.EqualTo("Act 3 cleared · 11 fights"));
            Assert.That(view.History[0].WhereLine, Is.EqualTo("Act 2 · Floor 7 · 6 fights"));
            Assert.That(view.History[0].Custom, Is.True);
            Assert.That(view.History[0].Seed, Does.Contain("ASHFALL"));
            Assert.That(JournalView.Build(new JObject(), Content, Ui).Summary, Is.Null, "no runs: no line");
        }

        [Test]
        public void TheCompendiumCountsTheArmamentsTheProfileHolds()
        {
            var empty = CompendiumView.Build(new JObject(), Content, Ui);
            Assert.That(empty.Total, Is.EqualTo(28));
            Assert.That(empty.Found, Is.EqualTo(0));
            Assert.That(empty.Eyebrow, Is.EqualTo("Armaments · 0 of 28 held"));
            Assert.That(empty.Tabs.Select(t => t.Label + " " + t.Count), Is.EqualTo(new[] { "Weapons 0/11", "Shields 0/8", "Staves 0/9" }));
            Assert.That(empty.Tabs[0].Entries.All(e => e.Name == "Unknown weapon" && !e.Found), Is.True);
            Assert.That(empty.Tabs[0].Entries[0].Detail, Does.StartWith("Not yet found."));
            var held = CompendiumView.Build(JObject.Parse("{\"discoveredArmaments\":[\"greatsword\"]}"), Content, Ui);
            Assert.That(held.Found, Is.EqualTo(1));
            var entry = held.Tabs[0].Entries.Single(e => e.Found);
            Assert.That(entry.Id, Is.EqualTo("greatsword"));
            Assert.That(entry.Name, Is.Not.EqualTo("Unknown weapon").And.Not.EqualTo("greatsword"));
            Assert.That(entry.Detail, Is.EqualTo("Slow, and it does not care."));
            Assert.That(entry.Meta, Is.EqualTo("uncommon · either hand"));
            Assert.That(entry.Stats, Is.Not.Empty);
        }

        [Test]
        public void TheProfileScreenSaysWhetherItReadsAndListsTheSlots()
        {
            var view = ProfileView.Build(ProfileStore.Load(_saves), _saves, Content, Ui);
            Assert.That(view.Readable, Is.True);
            Assert.That(view.Status, Is.EqualTo("Your profile is fine."));
            Assert.That(view.Slots, Has.Count.EqualTo(_saves.Rules.RunSlots));
            Assert.That(view.Slots.All(s => s.Empty && s.Facts == "Empty slot"), Is.True);
            Assert.That(view.SlotCount, Is.EqualTo(0));
            Assert.That(view.Tally, Is.Not.Empty);
            Core2RunSession.New(Content, _saves, 2, 1234u, Classes().Default(), "Aldric");
            var after = ProfileView.Build(ProfileStore.Load(_saves), _saves, Content, Ui);
            Assert.That(after.SlotCount, Is.EqualTo(1));
            Assert.That(after.Slots[1].Empty, Is.False);
            Assert.That(after.Slots[1].Facts, Does.Contain("Aldric"));
        }

        [Test]
        public void TheAboutTextIsTheOwnersDisclosureAndTheCredits()
        {
            var view = AboutView.Build(Ui, "9.9.9");
            Assert.That(view.Body, Is.EqualTo(Ui.Strings.Get((string)Ui.About["aiDisclosure"]["fullKey"])));
            Assert.That(view.Heading, Is.EqualTo("Made with generative AI"));
            Assert.That(view.Credits, Has.Count.EqualTo(2));
            Assert.That(view.Build, Does.Contain("9.9.9"));
        }
    }
}
