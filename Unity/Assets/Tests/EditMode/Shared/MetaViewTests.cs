using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Settings;
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

        // ------------------------------------------------------------------ W-18 settings (US-15.1)

        [Test]
        public void EverySettingHasAControlItsDefaultFitsAndEveryStringResolves()
        {
            var settings = new SettingsSession(Default, ProfileStore.Load(_saves));
            Assert.That(settings.Defs.Controls, Is.Not.Empty);
            Assert.That(settings.Defs.Controls.Select(c => c.Key).Distinct().Count(), Is.EqualTo(settings.Defs.Controls.Count), "one control per key");
            var categories = settings.Defs.Categories.Select(c => c.Key).ToList();
            foreach (var def in settings.Defs.Controls)
            {
                Assert.That(categories, Has.Member(def.Category), def.Key);
                Assert.That(def.Refusal(def.Def), Is.Null, def.Key + ": the def fits its control");
                if (def.IsEnum) Assert.That(def.Options, Is.Not.Empty, def.Key + ": options resolve");
                if (def.IsSlider) Assert.That(def.Max, Is.GreaterThan(def.Min), def.Key + ": the range resolves");
            }
            // Every preset's player setting is a known control and fits it (the reference preset's values are new-profile defaults).
            foreach (var preset in new[] { "shipped", "reference" })
            {
                var content = RunContent.Load(TestContent.Source, preset);
                foreach (var p in content.Snapshot.PlayerSettings?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var def = settings.Defs.Find(p.Name);
                    Assert.That(def, Is.Not.Null, preset + " playerSettings." + p.Name + " has a control");
                    Assert.That(def.Refusal(p.Value), Is.Null, preset + " playerSettings." + p.Name + " fits");
                }
            }
            foreach (var category in categories)
            {
                var view = settings.View(Ui, category);
                Assert.That(view.Controls, Is.Not.Empty, category);
                Resolved(view.Controls.SelectMany(c => new[] { c.Label, c.Help, c.ValueText }.Concat(c.Options.Select(o => o.Value))).Concat(view.Rail.Select(r => r.Value)).Concat(new[] { view.Title, view.Reset }).ToArray());
                foreach (var c in view.Controls) Assert.That(c.Label.StartsWith("settings.", StringComparison.Ordinal), Is.False, c.Key);
            }
        }

        [Test]
        public void AValueComesFromTheProfileThenThePresetThenTheDefault()
        {
            var settings = new SettingsSession(Default, ProfileStore.Load(_saves));
            Assert.That(Default.PresetId, Is.EqualTo("reference"));
            Assert.That(settings.On("shrineMultiUse"), Is.True, "the reference preset's player setting");
            Assert.That(settings.Text("holdConfirm"), Is.EqualTo("short"));
            Assert.That(settings.Text(SettingIds.MapMode), Is.EqualTo("full"), "the control's def");
            Assert.That(settings.Number("levelUpValue"), Is.EqualTo(1), "a def read from balance.levelUp");

            Assert.That(settings.Set("shrineMultiUse", false), Is.Null);
            Assert.That(settings.Set(SettingIds.MapMode, "fog"), Is.Null);
            var reread = new SettingsSession(Default, ProfileStore.Load(_saves));
            Assert.That(reread.On("shrineMultiUse"), Is.False, "saved to the profile");
            Assert.That(reread.Text(SettingIds.MapMode), Is.EqualTo("fog"));

            // The run reads the same stored value.
            var session = RunSession.New(Default, _saves, 1, 7, Default.DefaultClass(), "Aldric");
            Assert.That(session.Settings().MultiUse, Is.False);

            reread.ResetCategory("gameplay");
            Assert.That(new SettingsSession(Default, ProfileStore.Load(_saves)).On("shrineMultiUse"), Is.True, "reset shows the preset again");
            Assert.That(new SettingsSession(Default, ProfileStore.Load(_saves)).Text(SettingIds.MapMode), Is.EqualTo("fog"), "other categories keep their values");
        }

        [Test]
        public void AValueThatDoesNotFitIsRefusedAndChangesNothing()
        {
            var settings = new SettingsSession(Default, ProfileStore.Load(_saves));
            Assert.That(settings.Set("noSuchSetting", true), Is.EqualTo(MetaStringKeys.SettingsRefusalUnknown));
            Assert.That(settings.Set("shopSell", "yes"), Is.EqualTo(MetaStringKeys.SettingsRefusalType));
            Assert.That(settings.Set("holdConfirm", "forever"), Is.EqualTo(MetaStringKeys.SettingsRefusalOption));
            Assert.That(settings.Set("textScale", 400), Is.EqualTo(MetaStringKeys.SettingsRefusalRange));
            Assert.That(settings.Set("textScale", 85), Is.EqualTo(MetaStringKeys.SettingsRefusalRange), "off the step grid");
            Assert.That(settings.RefusalText(Ui, "textScale", MetaStringKeys.SettingsRefusalRange), Is.EqualTo("Choose a value from 80 to 150."));
            Assert.That(ProfileStore.Load(_saves).Doc["settings"], Is.Null, "nothing was written");
            Assert.That(settings.Set("textScale", 120), Is.Null);
            Assert.That(settings.View(Ui, "accessibility").Controls.First(c => c.Key == "textScale").ValueText, Is.EqualTo("120%"));
        }

        [Test]
        public void InARunTheRunScopedControlsSayTheyApplyNextRun()
        {
            var settings = new SettingsSession(Default, ProfileStore.Load(_saves), inRun: true);
            var gameplay = settings.View(Ui, "gameplay");
            Assert.That(gameplay.Controls.First(c => c.Key == "levelUpValue").AppliesNextRun, Is.EqualTo("Applies from the next run"));
            Assert.That(gameplay.Controls.First(c => c.Key == "shopSell").AppliesNextRun, Is.Null);
            Assert.That(new SettingsSession(Default, ProfileStore.Load(_saves)).View(Ui, "gameplay").Controls.All(c => c.AppliesNextRun == null), Is.True);
        }

        // ------------------------------------------------------------------ W-06 reveal modes (US-4.2, D-151)

        [Test]
        public void TheMapRevealFollowsTheMapModeSetting()
        {
            var session = RunSession.New(Default, _saves, 1, 7, Default.DefaultClass(), "Aldric");
            var full = ActMapView.Build(session, Ui);
            Assert.That(full.Mode, Is.EqualTo(SettingIds.MapFull));
            Assert.That(full.Nodes.Any(n => n.Dimmed || n.Hidden), Is.False, "full draws the whole act");

            // Step onto the map so some of the act falls behind the run.
            var first = session.ReachableNodes().First();
            Assert.That(session.PlayerSettings.Set(SettingIds.MapMode, SettingIds.MapPath), Is.Null);
            var start = ActMapView.Build(session, Ui);
            Assert.That(start.Nodes.Any(n => n.Dimmed), Is.False, "before the first step everything is still ahead");

            var after = RunSession.New(Default, _saves, 2, 7, Default.DefaultClass(), "Aldric");
            after.PlayerSettings.Set(SettingIds.MapMode, SettingIds.MapPath);
            after.Travel(first);
            while (after.Location != RunFlowValues.LocationMap && !after.RunOver) Step(after);
            var path = ActMapView.Build(after, Ui);
            var reachable = new System.Collections.Generic.HashSet<string>(after.ReachableNodes());
            Assert.That(path.Nodes.Where(n => reachable.Contains(n.Id)).Any(n => n.Dimmed), Is.False, "the reachable row is lit");
            Assert.That(path.Nodes.Where(n => n.Floor == path.Node(path.CurrentId).Floor && !n.Current).All(n => n.Dimmed), Is.True, "the floor's other nodes are behind");
            Assert.That(path.Nodes.Any(n => n.Hidden), Is.False, "path hides nothing");

            after.PlayerSettings.Set(SettingIds.MapMode, SettingIds.MapFog);
            var fog = ActMapView.Build(after, Ui);
            var row = fog.Nodes.Where(n => n.Reachable).Min(n => n.Floor);
            Assert.That(fog.Nodes.Where(n => n.Floor <= row + 1).Any(n => n.Hidden), Is.False, "the reachable row and the next floor are revealed");
            Assert.That(fog.Nodes.Where(n => n.Floor > row + 1 && n.Kind != "boss").All(n => n.Hidden), Is.True, "beyond them the kinds are unseen");
            Assert.That(fog.Nodes.Where(n => n.Hidden).All(n => n.Label == "Unseen" && n.Kind == "unseen" && n.BossLabel == null), Is.True);
            Assert.That(fog.Nodes.Any(n => n.Kind == "boss" && !n.Hidden), Is.True, "the boss destinations stay drawn (US-4.4)");
            Assert.That(fog.Legend.Any(l => l.Kind == "unseen"), Is.True);
            Resolved(fog.Nodes.SelectMany(n => new[] { n.Label, n.Hint, n.AccessibleName }).Concat(fog.Legend.Select(l => l.Label)).ToArray());
        }

        /// <summary>Plays out whatever the first node opened (a fight's end turns, a reward's Continue, a place's Leave) back to the map.</summary>
        private static void Step(RunSession s)
        {
            var driver = new ClimbSessionTests.Driver(Default, null, s, 7) { Reload = false };
            driver.StepOnce();
        }

        // ------------------------------------------------------------------ W-05 prologue (US-3.1-3.3)

        /// <summary>The content with prologue playback on (rules/runFlow.json newRun.prologue stays off until W-05's screen is built; D-152).</summary>
        private static RunContent WithPrologue(params JObject[] more) =>
            RunContent.Load(TestContent.Source, null, new[] { JObject.Parse("{\"files\":{\"rules/runFlow.json\":{\"newRun\":{\"prologue\":true}}}}") }.Concat(more).ToArray());

        [Test]
        public void ANewRunPlaysThePrologueSceneByScene()
        {
            var content = WithPrologue();
            var session = RunSession.New(content, _saves, 1, 7, "rogue", "Aldric");
            Assert.That(session.ProloguePending, Is.True);
            Assert.That(session.ReachableNodes(), Is.Empty, "the map waits on the prologue");
            var seen = new System.Collections.Generic.List<string>();
            var guard = 0;
            while (session.ProloguePending && guard++ < 20)
            {
                var view = session.PrologueView(Ui);
                seen.Add(view.SceneId);
                Assert.That(content.HasAsset(view.ArtWide) && content.HasAsset(view.ArtNarrow), Is.True, view.SceneId + ": art resolves");
                Assert.That(view.Dots.Count(d => d), Is.EqualTo(seen.Count));
                Assert.That(view.Continue, Is.EqualTo(view.IsLast ? "Set forth" : "Continue"));
                Resolved(view.Line, view.Continue, view.Skip, view.Progress);
                Assert.That(view.Line, Is.Not.Empty);
                if (view.SceneId == "carry")
                {
                    Assert.That(view.ArtWide, Is.EqualTo("prologue.carry-rogue-desktop"), "the class's own art");
                    Assert.That(view.Item, Is.EqualTo("prologue.rogue"));
                    Assert.That(view.Line, Does.Contain("knives"), "the class's own line");
                }
                // Every scene is a checkpoint: a reload stands at the same scene.
                var loaded = RunSession.Load(content, _saves, 1).Session;
                Assert.That(loaded.PrologueView(Ui).SceneId, Is.EqualTo(view.SceneId));
                session.AdvancePrologue();
            }
            Assert.That(seen, Is.EqualTo(new[] { "warmth", "year", "carry", "night", "step" }), "the shipped subset and order; the empty slots are skipped");
            Assert.That(session.ProloguePending, Is.False);
            Assert.That(session.ReachableNodes(), Is.Not.Empty, "Set forth lands on the map");
            Assert.That(RunSession.Load(content, _saves, 1).Session.ProloguePending, Is.False, "saved done");
        }

        [Test]
        public void HoldToSkipEndsThePrologueAndTheSettingTurnsItOff()
        {
            var content = WithPrologue();
            var session = RunSession.New(content, _saves, 1, 7, Default.DefaultClass(), "Aldric");
            session.AdvancePrologue();
            session.SkipPrologue();
            Assert.That(session.ProloguePending, Is.False);
            Assert.That(RunSession.Load(content, _saves, 1).Session.ReachableNodes(), Is.Not.Empty);

            Assert.That(new SettingsSession(content, ProfileStore.Load(_saves)).Set(SettingIds.PlayPrologue, false), Is.Null);
            var off = RunSession.New(content, _saves, 2, 7, Default.DefaultClass(), "Aldric");
            Assert.That(off.ProloguePending, Is.False, "playback off: the run starts at the map");
            Assert.That(RunSession.New(Default, _saves, 3, 7, Default.DefaultClass(), "Aldric").ProloguePending, Is.False, "the shipped flow keeps it off");
        }

        [Test]
        public void ADisabledSavedSceneResumesAtTheNextAndANewSlotPlaysFromData()
        {
            var content = WithPrologue();
            var session = RunSession.New(content, _saves, 1, 7, Default.DefaultClass(), "Aldric");
            session.AdvancePrologue();
            Assert.That(session.PrologueView(Ui).SceneId, Is.EqualTo("year"));

            // The designer disables 'year' (slot 2) and fills the first extra slot: no code change (US-3.2, US-3.3).
            var scenes = (JArray)TestContent.ContentJson("ui/prologue.json")["scenes"];
            scenes[1]["enabled"] = false;
            scenes[5] = JObject.Parse("{\"id\":\"extraA\",\"order\":6,\"enabled\":true,\"art\":{\"wide\":\"prologue.road-desktop\",\"narrow\":\"prologue.road-mobile\"},\"lineKey\":\"prologue.step.line\",\"holdMs\":3000}");
            var edited = WithPrologue(new JObject { ["files"] = new JObject { ["ui/prologue.json"] = new JObject { ["scenes"] = scenes } } });
            var resumed = RunSession.Load(edited, _saves, 1).Session;
            Assert.That(resumed.PrologueView(Ui).SceneId, Is.EqualTo("carry"), "the disabled scene is passed over");
            var order = new System.Collections.Generic.List<string>();
            while (resumed.ProloguePending) { order.Add(resumed.PrologueView(Ui).SceneId); resumed.AdvancePrologue(); }
            Assert.That(order, Is.EqualTo(new[] { "carry", "night", "step", "extraA" }));
        }
    }
}
