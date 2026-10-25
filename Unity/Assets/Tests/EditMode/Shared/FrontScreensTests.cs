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
            Assert.That(settings.Set(SettingIds.OpeningPlayback, "off"), Is.Null);
            Assert.That(PrologueFlow.For(Content, settings, "reaver", "Aldric"), Is.Null);
            Assert.That(settings.Set(SettingIds.OpeningPlayback, "every"), Is.Null);
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
            var used = settings.Defs.Controls.Where(c => !c.LegacyOnly).Select(c => c.Category).Distinct().ToList();
            Assert.That(shown, Is.SupersetOf(used), "every category a page control is filed under has a place");
            Assert.That(settings.Defs.Categories.Select(c => c.Key), Is.SupersetOf(shown), "every category of the layout is declared");
            foreach (var tab in tabs)
            {
                Assert.That(Ui.Strings.Has(tab.LabelKey), tab.LabelKey);
                foreach (var group in tab.Groups) Assert.That(Ui.Strings.Has(group.LabelKey), group.LabelKey);
            }
            foreach (var category in settings.Defs.Categories) Assert.That(Ui.Strings.Has(category.Value), category.Value);
        }

        private static Ashen.App.Settings.SettingsPageQuery Query(string tab = null, string group = null, string topic = null, bool dev = false) =>
            new Ashen.App.Settings.SettingsPageQuery { Tab = tab, Group = group, Topic = topic, DevBuild = dev };

        [Test]
        public void ThePageShowsTheOpenTopicsControlsAsTheWebsCoreBuildDoes()
        {
            var settings = Settings();
            var page = settings.Page(Ui, Layout, Query());
            Assert.That(page.Tab, Is.EqualTo("general"));
            Assert.That(page.Group, Is.EqualTo("display"), "the first group opens");
            Assert.That(page.Topic, Is.EqualTo("interface"), "and its first topic");
            Assert.That(page.Tabs.Select(t => t.Label), Is.EqualTo(new[] { "General", "Accessibility", "Advanced" }));
            Assert.That(page.Groups.Select(g => g.Label), Is.EqualTo(new[] { "Display", "Combat", "Audio" }), "Game rules is the web's ruleset picker, which a Core 2-only build has no use for");
            Assert.That(page.Topics.Select(t => t.Label), Is.EqualTo(new[] { "Interface", "Title screen", "Map", "HUD" }));
            Assert.That(page.Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.Fullscreen, SettingIds.Accent, SettingIds.ViewPreset, SettingIds.UiScale, "resetAllSettings" }));
            Assert.That(page.Count, Is.EqualTo(page.Rows.Count + " settings"));
            var hud = settings.Page(Ui, Layout, Query(topic: "hud"));
            Assert.That(hud.Rows.Select(r => r.Label), Is.EqualTo(new[] { "HUD health and resources", "HUD relics", "HUD Cinders", "HUD journey position", "HUD potions" }));
            var combat = settings.Page(Ui, Layout, Query("general", "combat"));
            Assert.That(combat.Topics, Is.Empty, "one topic needs no select");
            Assert.That(combat.Rows.Select(r => r.Key), Is.EqualTo(new[]
            {
                SettingIds.AutoCombat, SettingIds.CombatSpeed, SettingIds.AutoCombatSpeed, SettingIds.CombatStepPause, SettingIds.AnimationStepDuration,
                SettingIds.CombatPacingPreset, SettingIds.AnimSpeed, SettingIds.ScreenShake, SettingIds.ShowPlayedCard,
            }));
            var audio = settings.Page(Ui, Layout, Query("general", "audio"));
            Assert.That(audio.Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.MusicEnabled, SettingIds.MuteAudio, SettingIds.MusicVolume, SettingIds.SfxVolume }));
            var access = settings.Page(Ui, Layout, Query("accessibility"));
            Assert.That(access.Groups, Has.Count.EqualTo(1));
            Assert.That(access.Topics.Select(t => t.Label), Is.EqualTo(new[] { "Tooltips", "Readability", "Motion & colour" }));
            Assert.That(access.Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.HoverTooltips, SettingIds.TooltipDelay, SettingIds.TooltipCloseDelay }));
            Assert.That(settings.Page(Ui, Layout, Query("accessibility", topic: "motion")).Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.ReducedMotion, SettingIds.ColorblindSafe, SettingIds.ReduceFlashes }));
            var about = settings.Page(Ui, Layout, Query("advanced", "about"));
            Assert.That(about.ShowAbout, Is.True);
            Assert.That(about.Rows, Is.Empty);
            Assert.That(about.Empty, Is.Null, "the About text is not an empty page");
            foreach (var row in page.Rows.Concat(hud.Rows).Concat(audio.Rows).Concat(access.Rows))
                Assert.That(row.Label, Does.Not.Contain("settings.").And.Not.Contain("{"), row.Key);
        }

        [Test]
        public void TheAdvancedTabHidesTheDeveloperSectionsOnAReleaseBuild()
        {
            var settings = Settings();
            var release = settings.Page(Ui, Layout, Query("advanced"));
            Assert.That(release.Groups.Select(g => g.Label), Is.EqualTo(new[] { "Deck", "Interface", "About" }), "the web's release sections that a Core 2 build has");
            Assert.That(release.DeveloperSwitch, Is.Null, "a release build offers no switch");
            var dev = settings.Page(Ui, Layout, Query("advanced", dev: true));
            Assert.That(dev.Groups.Select(g => g.Label), Is.EqualTo(new[] { "Opening sequence", "Deck", "Interface", "About" }));
            Assert.That(dev.DeveloperSwitch, Is.Not.Null, "a dev build offers the Developer tools switch");
            Assert.That(dev.DeveloperOn, Is.True, "and has it on");
            Assert.That(dev.Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.OpeningPlayback, SettingIds.OpeningAutoAdvance, SettingIds.OpeningSpeed, SettingIds.OpeningStill }));
            Assert.That(settings.Set(SettingIds.DeveloperTools, false), Is.Null);
            var off = settings.Page(Ui, Layout, Query("advanced", dev: true));
            Assert.That(off.DeveloperOn, Is.False);
            Assert.That(off.Groups.Select(g => g.Label), Is.EqualTo(new[] { "Deck", "Interface", "About" }), "the switch hides them again");
            var map = settings.Page(Ui, Layout, Query("advanced", "interface"));
            Assert.That(map.Topics.Select(t => t.Label), Is.EqualTo(new[] { "Map & HUD", "Controls" }));
            Assert.That(map.Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.WalkedFade }));
        }

        [Test]
        public void ATabSaysHowManySettingsItHoldsAndTheFirstThreeNames()
        {
            var page = Settings().Page(Ui, Layout, Query());
            Assert.That(page.Tabs[0].Tip, Does.StartWith("26 settings — Fullscreen, Accent color, View mode…"));
            Assert.That(page.Tabs[1].Tip, Does.StartWith("9 settings — Hover explanations, Explanation delay, Explanation close delay…"));
            Assert.That(page.Tabs[2].Tip, Is.EqualTo("Map, HUD and card appearance, lore text, the changelog and About."), "the release sentence");
            Assert.That(Settings().Page(Ui, Layout, Query(dev: true)).Tabs[2].Tip, Is.EqualTo("Optional gameplay rules, tuning, diagnostics, sync, and the changelog."));
        }

        [Test]
        public void SearchAndTheChangedFilterReachEverySection()
        {
            var settings = Settings();
            var volume = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Search = "volume" });
            Assert.That(volume.Filtering, Is.True);
            Assert.That(volume.Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.MusicEnabled, SettingIds.MusicVolume }), "found in the Audio group from the Display page (a help line counts)");
            Assert.That(settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Search = "no such setting at all" }).Empty, Is.Not.Null);
            Assert.That(settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { ChangedOnly = true }).Rows, Is.Empty, "nothing is changed yet");
            Assert.That(settings.Set(SettingIds.MusicVolume, 20), Is.Null);
            Assert.That(settings.Set(SettingIds.ReducedMotion, true), Is.Null);
            var changed = settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { ChangedOnly = true });
            Assert.That(changed.Rows.Select(r => r.Key), Is.EquivalentTo(new[] { SettingIds.MusicVolume, SettingIds.ReducedMotion }));
            Assert.That(changed.ChangedCount, Is.EqualTo(2));
            Assert.That(settings.Page(Ui, Layout, new Ashen.App.Settings.SettingsPageQuery { Search = "volume", ChangedOnly = true }).Rows.Select(r => r.Key), Is.EqualTo(new[] { SettingIds.MusicVolume }));
        }

        [Test]
        public void ResetGroupReturnsItsSettingsToTheirDefaults()
        {
            var settings = Settings();
            Assert.That(settings.Set(SettingIds.MusicVolume, 20), Is.Null);
            Assert.That(settings.Set(SettingIds.ReducedMotion, true), Is.Null);
            var audio = Layout.SelectMany(t => t.Groups).Single(g => g.Id == "audio");
            Assert.That(settings.ResetGroup(audio), Is.EqualTo(1));
            Assert.That(settings.Number(SettingIds.MusicVolume), Is.EqualTo(50), "the web's default volume is back");
            Assert.That(settings.On(SettingIds.ReducedMotion), Is.True, "another group is untouched");
            Assert.That(settings.ResetGroup(audio), Is.EqualTo(0), "nothing left to reset");
            settings.ResetAll();
            Assert.That(settings.On(SettingIds.ReducedMotion), Is.False);
        }

        [Test]
        public void OneRowResetsAloneAndAnUndoPutsTheSnapshotBack()
        {
            var settings = Settings();
            Assert.That(settings.Set(SettingIds.MusicVolume, 20), Is.Null);
            Assert.That(settings.Set(SettingIds.SfxVolume, 30), Is.Null);
            var before = settings.Snapshot();
            settings.ResetKey(SettingIds.MusicVolume);
            Assert.That(settings.Number(SettingIds.MusicVolume), Is.EqualTo(50));
            Assert.That(settings.Number(SettingIds.SfxVolume), Is.EqualTo(30), "only that row");
            settings.ResetAll();
            Assert.That(settings.Number(SettingIds.SfxVolume), Is.EqualTo(75));
            settings.Restore(before);
            Assert.That(settings.Number(SettingIds.MusicVolume), Is.EqualTo(20), "Undo brings the values back");
            Assert.That(settings.Number(SettingIds.SfxVolume), Is.EqualTo(30));
        }

        [Test]
        public void ASliderSnapsToItsStepsInsideItsRange()
        {
            var settings = Settings();
            var control = settings.Page(Ui, Layout, Query("general", "audio")).Rows.First(r => r.Type == Ashen.Generated.SettingsValues.Slider);
            Assert.That(Ashen.App.Settings.SettingsSession.Stepped(control, control.Max + 500), Is.EqualTo(control.Max));
            Assert.That(Ashen.App.Settings.SettingsSession.Stepped(control, control.Min - 500), Is.EqualTo(control.Min));
            var inside = Ashen.App.Settings.SettingsSession.Stepped(control, control.Min + control.Step * 2.4);
            Assert.That(settings.Set(control.Key, inside), Is.Null, "a stepped value always fits the control");
        }

        [Test]
        public void TheAudioSwitchesAndVolumesFollowTheWebsRules()
        {
            var settings = Settings();
            Assert.That(settings.MusicLevel, Is.EqualTo(0.5), "the web's music volume 50");
            Assert.That(settings.SfxLevel, Is.EqualTo(0.75), "and effects 75");
            Assert.That(settings.Set(SettingIds.MusicEnabled, false), Is.Null);
            Assert.That(settings.MusicLevel, Is.EqualTo(0), "the music switch silences the music");
            Assert.That(settings.SfxLevel, Is.EqualTo(0.75), "not the effects");
            Assert.That(settings.Set(SettingIds.MusicEnabled, true), Is.Null);
            Assert.That(settings.Set(SettingIds.MuteAudio, true), Is.Null);
            Assert.That(settings.MusicLevel, Is.EqualTo(0));
            Assert.That(settings.SfxLevel, Is.EqualTo(0), "muting silences everything");
            var row = settings.Page(Ui, Layout, Query("general", "audio")).Rows.First(r => r.Key == SettingIds.MuteAudio);
            Assert.That(row.Shown, Is.False, "the Audio switch is off while the audio is muted");
            Assert.That(row.Help, Is.EqualTo("Turn music and sound effects on. Music also has a quick toggle beside the HUD."));
            Assert.That(settings.Page(Ui, Layout, Query("general", "audio")).Rows.First(r => r.Key == SettingIds.MusicEnabled).Help, Is.EqualTo("Music on · all audio muted."));
        }

        [Test]
        public void TheCoreBuildTakesTheWebsDefaultsNotTheReferencePresetsPlayerSettings()
        {
            var reference = RunContent.Load(TestContent.Source, "reference");
            var core2 = new Ashen.App.Settings.SettingsSession(reference, ProfileStore.Load(_saves), core2: true);
            Assert.That(core2.Text(SettingIds.HoldConfirm), Is.EqualTo("normal"), "the web's hold dial, not the reference's short");
            Assert.That(core2.On(SettingIds.ShowPlayedCard), Is.False, "the web's default");
            Assert.That(core2.Text(SettingIds.WalkedFade), Is.EqualTo("half"));
            Assert.That(new Ashen.App.Settings.SettingsSession(reference, ProfileStore.Load(_saves)).Text(SettingIds.HoldConfirm), Is.EqualTo("short"), "the 405 build keeps the reference preset's");
            Assert.That(core2.Defs.Find(SettingIds.MapMode).Def.ToString(), Is.EqualTo("fog"), "the web's reveal is fog; there is no 'full'");
            Assert.That(core2.Defs.Find(SettingIds.MapMode).Options, Is.EqualTo(new[] { "path", "fog" }));
        }

        [Test]
        public void TheOpeningPlaysAsTheWebsPlaybackSettingSays()
        {
            var settings = new Ashen.App.Settings.SettingsSession(Content, ProfileStore.Load(_saves), core2: true);
            Assert.That(PrologueFlow.For(Content, settings, "reaver", "Aldric"), Is.Not.Null, "every new game");
            Assert.That(settings.Set(SettingIds.OpeningPlayback, "once"), Is.Null);
            Assert.That(PrologueFlow.For(Content, settings, "reaver", "Aldric"), Is.Not.Null, "the first time per profile");
            settings.MarkPrologueSeen();
            Assert.That(PrologueFlow.For(Content, settings, "reaver", "Aldric"), Is.Null, "then no more");
            Assert.That(settings.Set(SettingIds.OpeningPlayback, "off"), Is.Null);
            Assert.That(PrologueFlow.For(Content, settings, "reaver", "Aldric"), Is.Null);
            Assert.That(settings.Set(SettingIds.OpeningPlayback, "every"), Is.Null);
            Assert.That(PrologueFlow.For(Content, settings, "reaver", "Aldric"), Is.Not.Null);
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
