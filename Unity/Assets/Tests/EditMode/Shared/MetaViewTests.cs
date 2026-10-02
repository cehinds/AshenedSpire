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

        // ------------------------------------------------------------------ W-04 creation panes (US-2.1-2.6, US-2.8)

        private CreationSession NewCreation(int slot = 1) => new CreationSession(Default, _saves, ProfileStore.Load(_saves).Doc, Ui, slot, () => 4242u);

        [Test]
        public void NextAlwaysAnswersWithTheReasonAndTheControlToFocus()
        {
            var c = NewCreation();
            Assert.That(c.Pane, Is.EqualTo("class"));
            var refusal = c.Next();
            Assert.That(refusal.Text, Is.EqualTo("Choose a class."));
            Assert.That(refusal.Focus, Is.EqualTo("class"));
            var blocked = c.Classes().FirstOrDefault(o => o.Locked);
            if (blocked != null)
            {
                c.ChooseClass(blocked.Id);
                Assert.That(c.Next(), Is.Not.Null, "a class the tuning cannot begin refuses");
            }
            Assert.That(c.ChooseClass("rogue"), Is.Null);
            Assert.That(c.Next(), Is.Null);
            Assert.That(c.Pane, Is.EqualTo("character"));

            c.SetAssign(true);
            Assert.That(c.PointsLeft, Is.EqualTo(3), "the lean mode's bonus pool");
            refusal = c.Next();
            Assert.That(refusal.Text, Is.EqualTo("3 points left."));
            Assert.That(refusal.Focus, Is.EqualTo("attributes"));
            var rows = c.AttributeRows();
            Assert.That(rows.All(r => r.Value == 1 && r.CanRaise && !r.CanLower), Is.True, "every cell starts at the baseline");
            Assert.That(c.Adjust(rows[0].Id, -1), Is.Not.Null, "below the floor is refused");
            Assert.That(c.Adjust(rows[0].Id, 1), Is.Null);
            Assert.That(c.Adjust(rows[0].Id, 1), Is.Null);
            Assert.That(c.Adjust(rows[0].Id, 1), Is.Null);
            Assert.That(c.PointsLeft, Is.EqualTo(0));
            Assert.That(c.Adjust(rows[1].Id, 1).Text, Is.EqualTo("No points left to spend."));
            Assert.That(c.Adjust(rows[0].Id, -1), Is.Null);
            Assert.That(c.Adjust(rows[1].Id, 1), Is.Null);
            Assert.That(c.SetName("   ").Text, Is.EqualTo("Give your climber a name."));
            Assert.That(c.SetName(new string('a', 40)), Is.Not.Null);
            Assert.That(c.SetName("Vessa"), Is.Null);
            Assert.That(c.ChooseKeepsake("oldCinder"), Is.Null);
            Assert.That(c.ChooseTint("nope"), Is.Not.Null);
            Assert.That(c.Tints(), Is.Not.Empty, "the class's portrait frame sets");
            Assert.That(c.ChooseTint(c.Tints().Last().Id), Is.Null);
            Assert.That(c.ChooseGlyph(c.Glyphs()[1].Id), Is.Null);
            Assert.That(c.Next(), Is.Null);
            Assert.That(c.Pane, Is.EqualTo("equipment"));

            c.Back();
            c.Back();
            Assert.That(c.Pane, Is.EqualTo("class"));
            Assert.That(c.ClassId, Is.EqualTo("rogue"), "Back keeps the choices");
            Assert.That(c.Open("review"), Is.Null, "a rail jump over finished panes");
            Assert.That(c.Rail().Select(r => r.Value).Any(v => v.Contains("{")), Is.False);
            Assert.That(c.Rail()[1].Value, Is.EqualTo("Vessa · Assign"));
        }

        [Test]
        public void AnItemNeverSitsInBothHandsAndAnIllegalLoadoutRefusesOnEquipment()
        {
            var c = NewCreation();
            c.ChooseClass("rogue");
            c.Next();
            c.Next();
            Assert.That(c.Pane, Is.EqualTo("equipment"));
            Assert.That(c.Armour().Count(o => o.Selected), Is.EqualTo(1), "the free armour is preselected");
            Assert.That(c.Armour().Where(o => o.Locked).All(o => !string.IsNullOrEmpty(o.LockReason)), Is.True, "locked armour names its unlock");
            var right = c.RightHand;
            Assert.That(right, Is.Not.Null, "the baseline kit's hands");
            Assert.That(c.ChooseHand("leftHand", right, out var receipt), Is.Null);
            Assert.That(c.LeftHand, Is.EqualTo(right));
            Assert.That(c.RightHand, Is.Null, "moved, never duplicated");
            Assert.That(receipt, Does.Contain("moved to the off hand"));
            // Every pair the lists offer either previews a run or refuses on Next with the loadout's reason, never both.
            var illegal = 0;
            foreach (var r in c.Hands("rightHand"))
                foreach (var l in c.Hands("leftHand"))
                {
                    c.ChooseHand("rightHand", r.Id, out _);
                    c.ChooseHand("leftHand", l.Id, out _);
                    var problem = c.Preview().Problem;
                    var next = c.Next();
                    if (next == null) { c.Back(); Assert.That(problem, Is.Null); continue; }
                    illegal++;
                    Assert.That(next.Text, Is.EqualTo(problem));
                    Assert.That(next.Pane, Is.EqualTo("equipment"));
                }
            TestContext.Progress.WriteLine("rogue hand pairs refused: " + illegal);
            // Put a legal pair back and every pane passes.
            c.ChooseHand("rightHand", right, out _);
            c.ChooseHand("leftHand", string.Empty, out _);
            var preview = c.Preview();
            Assert.That(preview.Problem, Is.Null);
            Assert.That(preview.DeckCards, Has.Count.GreaterThanOrEqualTo(11), "the starting deck (US-7.2)");
            Resolved(preview.Derived, preview.Flasks, preview.Deck);
            Assert.That(c.Next(), Is.Null);
            Assert.That(c.Pane, Is.EqualTo("review"));
            Assert.That(c.Relics().Count(o => o.Selected), Is.EqualTo(1));
            Assert.That(c.Kits(), Is.Not.Empty);
        }

        [Test]
        public void BeginMakesTheRunFromEveryChoiceAndAnOccupiedSlotNeedsReplace()
        {
            var c = NewCreation();
            c.ChooseClass("starseer");
            c.SetName("Ilse");
            c.SetAssign(true);
            var rows = c.AttributeRows();
            c.Adjust(rows[3].Id, 1);
            c.Adjust(rows[3].Id, 1);
            c.Adjust(rows[4].Id, 1);
            c.ChooseKeepsake("oldCinder");
            var tint = c.Tints().Last().Id;
            c.ChooseTint(tint);
            Assert.That(c.SetSeed("!!").Text, Does.StartWith("Seeds use only"));
            Assert.That(c.SetSeed(string.Empty).Text, Is.EqualTo("Type a seed, or choose Random."));
            Assert.That(c.SetSeed("ASH42"), Is.Null);
            var seed = c.Seed;

            var session = c.Begin(false, out var refusal);
            Assert.That(refusal, Is.Null, refusal?.Text);
            Assert.That(session.ClassId, Is.EqualTo("starseer"));
            Assert.That(session.Name, Is.EqualTo("Ilse"));
            Assert.That(session.Seed, Is.EqualTo(seed));
            Assert.That(session.Portrait, Does.EndWith("." + tint));
            var attributes = (JObject)session.Run["attributes"];
            Assert.That((int)attributes[rows[3].Id].Value<double>(), Is.EqualTo(3), "the assigned cells");
            var plain = RunSession.New(Default, new SaveService(_dir + "-b", SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves))), 1, seed, "starseer", "Ilse");
            Assert.That(session.Run["cinders"].Value<double>() - plain.Run["cinders"].Value<double>(), Is.EqualTo(50), "the keepsake's effect");

            var again = NewCreation();
            again.ChooseClass("rogue");
            Assert.That(again.SlotOccupied, Is.True);
            Assert.That(again.Begin(false, out refusal), Is.Null);
            Assert.That(refusal.Text, Does.Contain("holds a climb"));
            Assert.That(RunSession.Load(Default, _saves, 1).Session.ClassId, Is.EqualTo("starseer"), "nothing was written");
            Assert.That(again.Begin(true, out refusal).ClassId, Is.EqualTo("rogue"), "Replace confirmed");
            if (Directory.Exists(_dir + "-b")) Directory.Delete(_dir + "-b", true);
        }

        // ------------------------------------------------------------------ W-22 progression (US-12.1-12.3, US-12.5)

        [Test]
        public void TheClassTreeShowsTiersStatesAndTheExclusiveChoice()
        {
            var session = RunSession.New(Default, _saves, 1, 7, "rogue", "Aldric");
            var view = ProgressionView.Build(session, Ui);
            Assert.That(view.Tiers, Is.Not.Empty);
            Assert.That(view.Tiers.All(t => !t.Open), Is.True, "class level 0: every tier is closed");
            Assert.That(view.Tiers.SelectMany(t => t.Nodes).All(n => n.State == ProgressionValues.Closed), Is.True);
            Assert.That(view.Tracks, Has.Count.EqualTo(1), "no track has grown yet");

            // Raise the class track to the top tier: the open tiers' nodes can be drafted.
            var classSkill = Ashen.Domain.Rewards.Skills.ClassSkillId("rogue");
            var run = session.Run;
            if (!(run["skills"] is JObject skills)) run["skills"] = skills = new JObject();
            skills[classSkill] = new JObject { ["level"] = 20.0, ["xp"] = 0.0 };
            view = ProgressionView.Build(session, Ui, run);
            Assert.That(view.Tiers.All(t => t.Open), Is.True);
            var first = view.Tiers[0].Nodes[0];
            Assert.That(first.State, Is.EqualTo(ProgressionValues.Available));
            var top = view.Tiers.Last();
            Assert.That(top.Exclusive, Is.Not.Null.And.StartsWith("Choose one:"), "the tier-3 nodes are mutually exclusive");

            // Choosing one top node closes its partner.
            var core = new JArray(view.Tiers.Take(view.Tiers.Count - 1).SelectMany(t => t.Nodes).Select(n => (JToken)n.Id));
            core.Add(top.Nodes[0].Id);
            run["coreTags"] = core;
            view = ProgressionView.Build(session, Ui, run);
            Assert.That(view.Tiers.Last().Nodes[0].State, Is.EqualTo(ProgressionValues.Picked));
            Assert.That(view.Tiers.Last().Nodes.Skip(1).Any(n => n.State == ProgressionValues.Excluded && n.StateText.StartsWith("Closed by", StringComparison.Ordinal)), Is.True);
            Resolved(view.Tiers.SelectMany(t => t.Nodes.SelectMany(n => new[] { n.Name, n.StateText })).Concat(view.Tiers.Select(t => t.Title)).Concat(view.Tracks)
                .Concat(new[] { view.Title, view.Level, view.Points, view.ClassTreeTitle, view.TracksTitle }).ToArray());
        }

        // ------------------------------------------------------------------ W-12 armoury (US-7.3, US-7.4, US-7.6)

        [Test]
        public void TheArmouryMovesPiecesRestampsTheDeckAndSaves()
        {
            var session = RunSession.New(Default, _saves, 1, 7, "rogue", "Aldric");
            var view = session.ArmouryView(Ui);
            Assert.That(view.Slots.Select(s => s.SlotId), Has.Member("rightHand").And.Member("leftHand").And.Member("armor"));
            Assert.That(view.Slots.First(s => s.SlotId == "rightHand").Cells, Has.Count.EqualTo(3), "three sets per hand");
            Assert.That(view.Weight, Does.StartWith("Weight "));
            Resolved(view.Slots.SelectMany(s => s.Cells.Select(c => c.Label)).Concat(new[] { view.Title, view.Weight, view.StorageTitle, view.StorageEmpty }).ToArray());

            var right = (string)session.Run["loadout"]["sets"]["rightHand"][0];
            var deck = ((JArray)session.Run["deck"]).Count;
            // Clear the off hand into storage: a preview changes nothing, the commit is saved.
            var preview = session.PreviewEquip(Ui, "leftHand", 0, null);
            Assert.That(preview.Done, Is.True, preview.Refusal);
            Assert.That(preview.Receipts, Is.Not.Empty);
            Assert.That(session.Run["loadout"]["sets"]["leftHand"][0].Type, Is.Not.EqualTo(JTokenType.Null), "the preview left the run alone");
            var change = session.Equip(Ui, "leftHand", 0, null);
            Assert.That(change.Done, Is.True);
            var loaded = RunSession.Load(Default, _saves, 1).Session;
            Assert.That(loaded.Run["loadout"]["sets"]["leftHand"][0].Type, Is.EqualTo(JTokenType.Null), "saved");
            Assert.That(loaded.ArmouryView(Ui).Storage, Is.Not.Empty, "the piece went to storage");
            Assert.That(session.Equip(Ui, "rightHand", 0, right).Refusal, Is.EqualTo("That is already there."));

            // Put it back from storage; a second set of the main hand can be made active.
            var stored = loaded.ArmouryView(Ui).Storage[0].Key;
            Assert.That(loaded.ArmouryCandidates("leftHand"), Has.Member(stored));
            Assert.That(loaded.Equip(Ui, "leftHand", 0, stored).Done, Is.True);
            Assert.That(((JArray)loaded.Run["deck"]).Count, Is.EqualTo(deck), "the lent cards came back with the item");
        }

        [Test]
        public void TheArmouryIsClosedInAFight()
        {
            var session = RunSession.New(Default, _saves, 1, 7, "rogue", "Aldric");
            session.StartEncounter();
            Assert.That(session.Equip(Ui, "leftHand", 0, null).Refusal, Is.EqualTo("The Armoury is closed during a fight."));
        }

        // ------------------------------------------------------------------ W-17 pile viewer

        [Test]
        public void ThePileViewerShowsDiscardAndExhaustAndOnlyTheDrawCount()
        {
            var session = RunSession.New(Default, _saves, 1, 7, "rogue", "Aldric");
            var combat = session.StartEncounter();
            var c = combat.State;
            var start = Ashen.App.Combat.CombatViewModel.Piles(c, Ui.Strings, MetaValues.PileDiscard);
            Assert.That(start.Cards, Is.Empty);
            Assert.That(start.Empty, Is.EqualTo("Nothing here yet."));
            Assert.That(start.Draw, Does.Contain(c.Piles.Draw.Count.ToString()));
            c.Piles.Discard.Add(c.Piles.Hand[0]);
            var after = Ashen.App.Combat.CombatViewModel.Piles(c, Ui.Strings, MetaValues.PileDiscard);
            Assert.That(after.Cards, Has.Count.EqualTo(1));
            Assert.That(after.Rail[0].Value, Is.EqualTo("Discard (1)"));
            Resolved(after.Rail.Select(r => r.Value).Concat(new[] { after.Title, after.Draw }).ToArray());
        }
    }
}
