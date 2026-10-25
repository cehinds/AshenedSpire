using System;
using System.IO;
using System.Linq;
using Ashen.App.Audio;
using Ashen.App.Nodes;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Domain.Shop;
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
        }

        [TearDown]
        public void TearDown()
        {
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
            // Every preset's player setting is a known control and fits it.
            foreach (var preset in new[] { "shipped" })
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

        // ------------------------------------------------------------------ Core 2 player settings (WP-20)

        [Test]
        public void TheCore2PacingAndAutomationDefaultsAreTheWebDevBuildsAndOneTapIsOff()
        {
            var pacing = CombatPacing.Read(new SettingsSession(Default, ProfileStore.Load(_saves)));
            Assert.That(pacing.AutoCombat, Is.False);
            Assert.That(pacing.Speed, Is.EqualTo(1));
            Assert.That(pacing.AutoSpeed, Is.EqualTo(1.5), "FIXCORE D11: auto combat runs at x1.5");
            Assert.That(pacing.StageGapMs, Is.EqualTo(500), "the web dev build's pause between steps (actionPacing.motion.stageGapMs)");
            Assert.That(pacing.AnimationStepMs, Is.EqualTo(390));
            Assert.That(pacing.AnimSpeed, Is.EqualTo("auto"));
            Assert.That(pacing.OneTapPlay, Is.False, "one-tap play is an opt-in");
            Assert.That(pacing.AskToUpcastAfterTarget, Is.False);
            Assert.That(pacing.ScreenShake, Is.True);
            Assert.That(pacing.ReduceFlashes, Is.False);
            Assert.That(pacing.EffectiveSpeed, Is.EqualTo(1));
            Assert.That(pacing.StageGapScaledMs, Is.EqualTo(500));
        }

        [Test]
        public void AutoCombatUsesItsOwnSpeedAndTheBoundsHold()
        {
            var settings = new SettingsSession(Default, ProfileStore.Load(_saves));
            Assert.That(settings.Set(SettingIds.AutoCombat, true), Is.Null);
            var pacing = CombatPacing.Read(settings);
            Assert.That(pacing.EffectiveSpeed, Is.EqualTo(1.5));
            Assert.That(pacing.StageGapScaledMs, Is.EqualTo(500 / 1.5).Within(1e-9));
            Assert.That(settings.Set(SettingIds.AutoCombatSpeed, 0.75), Is.EqualTo(MetaStringKeys.SettingsRefusalRange), "auto speed is x1 to x3");
            Assert.That(settings.Set(SettingIds.AutoCombatSpeed, 3.5), Is.EqualTo(MetaStringKeys.SettingsRefusalRange));
            Assert.That(settings.Set(SettingIds.AutoCombatSpeed, 2), Is.Null);
            Assert.That(settings.Set(SettingIds.CombatStepPause, 2500), Is.EqualTo(MetaStringKeys.SettingsRefusalRange), "0 to 2000 ms");
            Assert.That(settings.Set(SettingIds.CombatStepPause, 0), Is.Null, "0 turns the pauses off");
            Assert.That(settings.Set(SettingIds.CombatSpeed, 9), Is.EqualTo(MetaStringKeys.SettingsRefusalRange));
            Assert.That(settings.Set(SettingIds.AnimSpeed, "ludicrous"), Is.EqualTo(MetaStringKeys.SettingsRefusalOption));
        }

        [Test]
        public void TheMobileFastPresetSetsTheBundleAndEditingAValueReturnsToCustom()
        {
            var settings = new SettingsSession(Default, ProfileStore.Load(_saves));
            Assert.That(settings.Set(SettingIds.CombatPacingPreset, "mobile-fast"), Is.Null);
            Assert.That(settings.Number(SettingIds.CombatStepPause), Is.EqualTo(250));
            Assert.That(settings.Number(SettingIds.AnimationStepDuration), Is.EqualTo(195));
            Assert.That(settings.Text(SettingIds.CombatPacingPreset), Is.EqualTo("mobile-fast"));
            Assert.That(settings.Set(SettingIds.CombatStepPause, 300), Is.Null);
            Assert.That(settings.Text(SettingIds.CombatPacingPreset), Is.EqualTo("custom"), "a hand edit leaves the preset");
            Assert.That(settings.Number(SettingIds.AnimationStepDuration), Is.EqualTo(195), "the other value stays");
            Assert.That(settings.Set(SettingIds.CombatPacingPreset, "custom"), Is.Null);
            Assert.That(settings.Number(SettingIds.CombatStepPause), Is.EqualTo(300), "choosing Custom changes no value");
            Assert.That(new SettingsSession(Default, ProfileStore.Load(_saves)).Number(SettingIds.CombatStepPause), Is.EqualTo(300), "saved");
        }

        [Test]
        public void TheCore2SettingsRoundTripThroughTheWebConfigFormat()
        {
            var web = new JObject
            {
                ["schemaVersion"] = 1,
                ["overrides"] = new JObject
                {
                    ["settings.autoCombat"] = true,
                    ["settings.autoCombatSpeed"] = 2.5,
                    ["settings.combatStepPause"] = 1000,
                    ["settings.animationStepDuration"] = 250,
                    ["settings.oneTapPlay"] = true,
                    ["settings.combatPacingPreset"] = "custom",
                    ["settings.screenShake"] = false,
                },
            };
            var result = WebConfig.Import(web, TestContent.Source, Default.PresetId, SettingsDefs.From(Default.Snapshot.Content));
            Assert.That(result.PlayerSettings.Count, Is.EqualTo(7), "every Core 2 player setting is taken");
            Assert.That(result.Reported, Is.Empty);
            WebConfig.Apply(ProfileStore.Load(_saves), result, Default.ContentHash, SettingsDefs.From(Default.Snapshot.Content));
            var settings = new SettingsSession(Default, ProfileStore.Load(_saves));
            var pacing = CombatPacing.Read(settings);
            Assert.That(pacing.AutoCombat && pacing.OneTapPlay && !pacing.ScreenShake, Is.True);
            Assert.That(pacing.EffectiveSpeed, Is.EqualTo(2.5));
            Assert.That(pacing.StageGapMs, Is.EqualTo(1000));
            var exported = WebConfig.Export(ProfileStore.Load(_saves).Doc, WebConfig.SettingsPrefix(TestContent.Source));
            Assert.That(exported["overrides"]["settings.autoCombatSpeed"].Value<double>(), Is.EqualTo(2.5), "exported again");
            Assert.That(exported["overrides"]["settings.oneTapPlay"].Value<bool>(), Is.True);
        }

        private SettingsSession ImportSettings(JObject overrides, bool startMobileFast = false)
        {
            var defs = SettingsDefs.From(Default.Snapshot.Content);
            if (startMobileFast) Assert.That(new SettingsSession(Default, ProfileStore.Load(_saves)).Set(SettingIds.CombatPacingPreset, "mobile-fast"), Is.Null);
            var web = new JObject { ["schemaVersion"] = 1, ["overrides"] = overrides };
            var result = WebConfig.Import(web, TestContent.Source, Default.PresetId, defs);
            Assert.That(result.Reported, Is.Empty);
            WebConfig.Apply(ProfileStore.Load(_saves), result, Default.ContentHash, defs);
            return new SettingsSession(Default, ProfileStore.Load(_saves));
        }

        [Test]
        public void ImportingAPresetAloneAppliesItsBundledValues()
        {
            var settings = ImportSettings(new JObject { ["settings.combatPacingPreset"] = "mobile-fast" });
            var bundle = (JObject)settings.Defs.Find(SettingIds.CombatPacingPreset).Presets["mobile-fast"];
            Assert.That(settings.Text(SettingIds.CombatPacingPreset), Is.EqualTo("mobile-fast"));
            foreach (var p in bundle.Properties()) Assert.That(settings.Value(p.Name), Is.EqualTo(p.Value), p.Name);
        }

        [Test]
        public void ImportingABundledValueAloneLeavesThePresetCustom()
        {
            var settings = ImportSettings(new JObject { ["settings.combatStepPause"] = 300 }, startMobileFast: true);
            Assert.That(settings.Number(SettingIds.CombatStepPause), Is.EqualTo(300));
            Assert.That(settings.Text(SettingIds.CombatPacingPreset), Is.EqualTo("custom"));
            Assert.That(settings.Number(SettingIds.AnimationStepDuration), Is.EqualTo(195), "the other bundled value stays");
        }

        [Test]
        public void ImportingAPresetWithItsOwnBundledValuesKeepsThem()
        {
            var bundle = (JObject)SettingsDefs.From(Default.Snapshot.Content).Find(SettingIds.CombatPacingPreset).Presets["mobile-fast"];
            var overrides = new JObject { ["settings.combatPacingPreset"] = "mobile-fast" };
            foreach (var p in bundle.Properties()) overrides["settings." + p.Name] = p.Value;
            Assert.That(ImportSettings(overrides).Text(SettingIds.CombatPacingPreset), Is.EqualTo("mobile-fast"));
        }

        [Test]
        public void ImportingAPresetWithDifferingBundledValuesKeepsTheExplicitValuesAndCustom()
        {
            var settings = ImportSettings(new JObject { ["settings.combatPacingPreset"] = "mobile-fast", ["settings.combatStepPause"] = 1000 });
            Assert.That(settings.Number(SettingIds.CombatStepPause), Is.EqualTo(1000), "explicit values win");
            Assert.That(settings.Text(SettingIds.CombatPacingPreset), Is.EqualTo("custom"));
        }

        [Test]
        public void APinnedRunStatesItsPin()
        {
            var core = (JObject)Default.Snapshot.Content.Get(ContentFiles.ProfileCore);
            var run = new JObject { ["gameProfile"] = new JObject { ["id"] = core["id"], ["hash"] = core["hash"], ["content"] = new JObject() } };
            var pin = Ashen.Domain.Run.CoreRunRules.PinOf(run);
            Assert.That(pin["id"].Value<string>(), Is.EqualTo("core"));
            Assert.That(pin["hash"].Value<string>(), Does.StartWith("sha256:"));
            Assert.That(pin["content"], Is.Null, "the pin is the id and hash only");
        }

        // ------------------------------------------------------------------ W-06 reveal modes (US-4.2, D-151)

        // ------------------------------------------------------------------ W-05 prologue (US-3.1-3.3)

        /// <summary>The content with prologue playback on (rules/runFlow.json newRun.prologue stays off until W-05's screen is built; D-152).</summary>
        private static RunContent WithPrologue(params JObject[] more) =>
            RunContent.Load(TestContent.Source, null, new[] { JObject.Parse("{\"files\":{\"rules/runFlow.json\":{\"newRun\":{\"prologue\":true}}}}") }.Concat(more).ToArray());

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

        // ------------------------------------------------------------------ W-22 progression (US-12.1-12.3, US-12.5)

        // ------------------------------------------------------------------ W-12 armoury (US-7.3, US-7.4, US-7.6)

        // ------------------------------------------------------------------ W-17 pile viewer

        // ------------------------------------------------------------------ music (US-16.1)

        [Test]
        public void MusicFollowsWhereTheRunStandsAndEveryContextResolves()
        {
            var director = MusicDirector.From(TestContent.Source);
            Assert.That(director.ContextFor(RunFlowValues.LocationMap), Is.EqualTo("map"));
            Assert.That(director.ContextFor(RunFlowValues.LocationCombat, "normal"), Is.EqualTo("combat"));
            Assert.That(director.ContextFor(RunFlowValues.LocationCombat, "elite"), Is.EqualTo("elite"));
            Assert.That(director.ContextFor(RunFlowValues.LocationCombat, "boss"), Is.EqualTo("boss"));
            Assert.That(director.ContextFor(RunFlowValues.LocationMerchant), Is.EqualTo("shop"));
            Assert.That(director.RunEndContext(true), Is.EqualTo("victory"));
            Assert.That(director.Choose(director.RunEndContext(false)).Silence, Is.True, "defeat: the quiet bed is silence");
            Assert.That(director.Stinger("defeat").Recipe, Is.EqualTo("youDied"));

            var music = TestContent.ContentJson(ContentFiles.AudioMusic);
            var renderer = new BedRenderer(music, TestContent.ContentJson(ContentFiles.AudioBeds), (int)TestContent.ContentJson(ContentFiles.AudioSynth)["sampleRate"]);
            foreach (var context in new[] { "title", "map", "combat", "elite", "boss", "shop", "rest", "victory" })
            {
                var choice = director.Choose(context, 1);
                Assert.That(choice.Silence, Is.False, context);
                Assert.That(choice.Bed, Is.Not.Null, context + ": no generated file yet, so the bed plays");
                var pcm = renderer.RenderLoop(choice.Bed, choice.Variant);
                Assert.That(pcm.Length, Is.GreaterThan(0));
                Assert.That(pcm.Max(Math.Abs), Is.GreaterThan(0.01f).And.LessThanOrEqualTo(1f), context + ": audible, not clipping");
                Assert.That(renderer.RenderLoop(choice.Bed, choice.Variant), Is.EqualTo(pcm), context + ": deterministic");
            }
            Assert.That(director.Choose("map", 0).Variant, Is.Not.EqualTo(director.Choose("map", 1).Variant), "acts sound apart");
        }

        [Test]
        public void AStoredSettingTellsLiveConsumers()
        {
            var settings = new SettingsSession(Default, ProfileStore.Load(_saves));
            var heard = new System.Collections.Generic.List<string>();
            void On(SettingsSession sender, string key) { if (sender == settings) heard.Add(key ?? "*"); }
            SettingsSession.Changed += On;
            try
            {
                Assert.That(settings.Set(SettingIds.MusicVolume, 40), Is.Null);
                Assert.That(settings.Set(SettingIds.MusicVolume, 41), Is.Not.Null, "a refusal stays silent");
                settings.ResetCategory("audio");
                settings.ResetCategory("audio");
            }
            finally { SettingsSession.Changed -= On; }
            Assert.That(heard, Is.EqualTo(new[] { SettingIds.MusicVolume, "*" }), "a reset that cleared nothing stays silent");
        }

        // ------------------------------------------------------------------ W-20 Abandon run (D-134)

        [Test]
        public void ThePauseMenuOffersAbandonBehindADestructiveHoldDoor()
        {
            var row = Ui.Menus.Pause.First(e => e.Id == "abandon");
            Assert.That(row.Action, Is.EqualTo(MenuActions.Confirm));
            Assert.That(row.Confirm, Is.EqualTo(ConfirmIds.Abandon));
            Assert.That(MenuRules.IsEnabled(row, Ui.MenuContext(true)), Is.True);
            var door = Ui.Menus.Confirm(ConfirmIds.Abandon);
            Assert.That(door.Hold, Is.True);
            Assert.That(Ui.Policies.IsDestructive(door.Policy), Is.True);
            Resolved(Ui.Strings.Get(door.TitleKey), Ui.Strings.Get(door.BodyKey), Ui.Strings.Get(door.PrimaryKey));
        }

        // ------------------------------------------------------------------ W-09 buy-back (US-9.3, D-159)

        // ------------------------------------------------------------------ W-18 hand-rules worked example (US-15.2)

        [Test]
        public void TheHandExampleFollowsTheFormulasForTheClass()
        {
            var view = HandExample.ForClass(Default, Ui, "rogue");
            Assert.That(view.Title, Is.EqualTo("Worked example · " + RunHudView.ClassName(Ui, "rogue")));
            Assert.That(view.Rules, Is.EqualTo("Opening 3 · capacity 10 · each turn fills the hand"));
            Assert.That(view.Lines, Is.EqualTo(new[]
            {
                "Turn 1: draw 3, holding 3 of 10", "Play 2; 1 stay in hand",
                "Turn 2: draw 9, holding 10 of 10", "Play 2; 8 stay in hand",
                "Turn 3: draw 2, holding 10 of 10", "Play 2; 8 stay in hand",
            }));

            // Other modes stay selectable in the rules: a fixed draw with discard, and a stat that raises the opening hand.
            var rules = TestContent.ContentJson("rules/handRules.json");
            rules["drawMode"] = "draw";
            rules["retain"] = false;
            var attributes = new JObject { ["intelligence"] = 30.0 };
            var other = HandExample.Build(rules, attributes, "X", Ui);
            Assert.That(other.Rules, Is.EqualTo("Opening 5 · capacity 10 · each turn draws 2"));
            Assert.That(other.Lines[0], Is.EqualTo("Turn 1: draw 5, holding 5 of 10"));
            Assert.That(other.Lines[1], Is.EqualTo("Play 2; the other 3 are discarded"));
            Assert.That(other.Lines[2], Is.EqualTo("Turn 2: draw 2, holding 2 of 10"));
        }

        // ------------------------------------------------------------------ W-18 Saves: web config import/export (US-15.4, D-162)

        private static JObject ReferenceWebConfig() =>
            JObject.Parse(File.ReadAllText(Path.Combine(TestContent.RepoRoot, "docs", "design", "reference", "ashen-spire-game-config.json")));

        [Test]
        public void TheOwnersReferenceFileImportsItsSettingsAndReportsItsTuning()
        {
            var web = ReferenceWebConfig();
            var shipped = RunContent.Load(TestContent.Source, "shipped");
            var defs = SettingsDefs.From(shipped.Snapshot.Content);
            var result = WebConfig.Import(web, TestContent.Source, "shipped", defs);
            var total = ((JObject)web["overrides"]).Count;
            Assert.That(result.Recognised, Is.True);
            Assert.That(result.Taken + result.Reported.Count, Is.EqualTo(total), "every key is taken or reported, never dropped");
            foreach (var r in result.Reported.Where(r => r.Value == WebConfigReasons.Invalid || r.Value == WebConfigReasons.UnknownPath)) TestContext.Progress.WriteLine("not taken: " + r.Key + " (" + r.Value + ")");
            TestContext.Progress.WriteLine($"web import: {result.GameConfig.Count} game keys, {result.PlayerSettings.Count} settings, {result.Reported.Count} reported: "
                + string.Join(", ", result.Reported.GroupBy(r => r.Value).Select(g => g.Key + " " + g.Count())));
            Assert.That(result.PlayerSettings["holdConfirm"].Value<string>(), Is.EqualTo("short"));
            Assert.That(result.PlayerSettings.Count, Is.EqualTo(8), "the owner's eight player settings");
            Assert.That(result.GameConfig.Properties(), Is.Empty, "the tuning keys tuned the 405 tables, which left the run (WP-21)");
            Assert.That(result.Reported.Where(r => r.Key.StartsWith("gameConfig.", StringComparison.Ordinal)).All(r => r.Value == WebConfigReasons.NoRoot), Is.True);
            Assert.That(result.Reported.Any(r => r.Value == WebConfigReasons.Mirror), Is.True);
        }

        [Test]
        public void AnImportIsStoredOnTheProfileDrivesNewRunsAndExportsBackInWebForm()
        {
            var web = new JObject
            {
                ["schemaVersion"] = 1,
                ["overrides"] = new JObject
                {
                    ["gameConfig.balance.startingCinders"] = 77,
                    ["gameConfig.balance.shop.sellFraction"] = "lots",
                    ["gameConfig.balance.nope"] = 1,
                    ["gameConfig.noSuchRoot.x"] = 1,
                    ["settings.mapMode"] = "path",
                    ["settings.textScale"] = 999,
                    ["settings.unknownThing"] = true,
                    ["somethingElse"] = 1,
                },
            };
            var defs = SettingsDefs.From(Default.Snapshot.Content);
            var result = WebConfig.Import(web, TestContent.Source, Default.PresetId, defs);
            var reported = result.Reported.ToDictionary(r => r.Key, r => r.Value);
            foreach (var key in new[] { "gameConfig.balance.startingCinders", "gameConfig.balance.shop.sellFraction", "gameConfig.balance.nope", "gameConfig.noSuchRoot.x" })
                Assert.That(reported[key], Is.EqualTo(WebConfigReasons.NoRoot), key + ": the web's tuning keys have no root since the 405 tables left the run (WP-21)");
            Assert.That(reported["settings.textScale"], Is.EqualTo(WebConfigReasons.SettingDoesNotFit));
            Assert.That(reported["settings.unknownThing"], Is.EqualTo(WebConfigReasons.UnknownSetting));
            Assert.That(reported["somethingElse"], Is.EqualTo(WebConfigReasons.NotAKey));
            Assert.That(result.GameConfig.Properties(), Is.Empty);

            var store = ProfileStore.Load(_saves);
            WebConfig.Apply(store, result, Default.ContentHash, defs);
            var profile = ProfileStore.Load(_saves).Doc;
            Assert.That(new SettingsSession(Default, ProfileStore.Load(_saves)).Text(SettingIds.MapMode), Is.EqualTo("path"));
            Assert.That(WebConfig.ContentFor(TestContent.Source, profile).ContentHash, Is.EqualTo(Default.ContentHash), "nothing tunes the content");

            var exported = WebConfig.Export(profile, WebConfig.SettingsPrefix(TestContent.Source));
            Assert.That(exported["overrides"]["settings.mapMode"].Value<string>(), Is.EqualTo("path"));
            Assert.That(((JObject)exported["overrides"]).Properties().Any(p => p.Name.StartsWith("gameConfig.", StringComparison.Ordinal)), Is.False);
            var again = WebConfig.Import(exported, TestContent.Source, Default.PresetId, defs);
            Assert.That(again.Reported, Is.Empty, "the export imports cleanly");
        }

        // ------------------------------------------------------------------ W-18 settings layers (US-15.1)

        [Test]
        public void AValueComesFromTheProfileThenThePresetThenTheDefault()
        {
            var settings = new SettingsSession(Default, ProfileStore.Load(_saves));
            Assert.That(Default.PresetId, Is.EqualTo("shipped"));
            Assert.That(settings.On("shrineMultiUse"), Is.True, "the control's def, the web's (WP-21)");
            Assert.That(settings.Text(SettingIds.MapMode), Is.EqualTo("fog"), "the control's def, the web's");
            Assert.That(settings.On("showPlayedCard"), Is.False, "the web's def");
            Assert.That(settings.Text("walkedFade"), Is.EqualTo("half"), "the web's def");
            Assert.That(settings.Number("levelUpValue"), Is.EqualTo(1), "a def read from balance.levelUp");

            Assert.That(settings.Set("shrineMultiUse", false), Is.Null);
            Assert.That(settings.Set(SettingIds.MapMode, "path"), Is.Null);
            var reread = new SettingsSession(Default, ProfileStore.Load(_saves));
            Assert.That(reread.On("shrineMultiUse"), Is.False, "saved to the profile");
            Assert.That(reread.Text(SettingIds.MapMode), Is.EqualTo("path"));

            reread.ResetCategory("gameplay");
            Assert.That(new SettingsSession(Default, ProfileStore.Load(_saves)).On("shrineMultiUse"), Is.True, "reset shows the default again");
            Assert.That(new SettingsSession(Default, ProfileStore.Load(_saves)).Text(SettingIds.MapMode), Is.EqualTo("path"), "other categories keep their values");
        }

        // ------------------------------------------------------------------ SFX (US-16.2)

        [Test]
        public void CombatEventsCallForTheirSoundsFamilyFirstOncePerCommand()
        {
            var sfx = SfxDirector.From(TestContent.Source);
            var events = new[]
            {
                JObject.Parse("{\"type\":\"cardPlayed\"}"),
                JObject.Parse("{\"type\":\"damageDealt\"}"),
                JObject.Parse("{\"type\":\"damageDealt\"}"),
                JObject.Parse("{\"type\":\"cardDrawn\"}"),
                JObject.Parse("{\"type\":\"procBurst\",\"status\":\"frost\"}"),
                JObject.Parse("{\"type\":\"enemyDied\"}"),
            };
            Assert.That(sfx.For(events), Is.EqualTo(new[] { "cardPlay", "hit", "procBurst_frost" }), "mapped, each once, at most three");
            var recipes = (JObject)TestContent.ContentJson("audio/sfx.json")["SFX_RECIPES"];
            var renderer = new SynthRenderer(SynthParams.From(TestContent.ContentJson("audio/synth.json")), recipes);
            foreach (var mapped in ((JObject)TestContent.ContentJson("audio/beds.json")["sfx"]["events"]).Properties())
                Assert.That(recipes[(string)mapped.Value], Is.Not.Null, mapped.Name + " → a recipe that exists");
            Assert.That(renderer.Resolve(sfx.For(new[] { JObject.Parse("{\"type\":\"procBurst\",\"status\":\"venom\"}") })[0]), Is.EqualTo("procBurst"), "family fallback");

            // A real Core 2 fight's first command calls for sounds.
            var session = Core2RunSession.New(Default, _saves, 1, 7, "reaver", "Aldric");
            Assert.That(session.Travel(session.ReachableNodes()[0]).Door.IsFight, Is.True);
            var heard = new System.Collections.Generic.List<string>();
            session.CommandCommitted += o => { if (o.Accepted) heard.AddRange(sfx.For(o.Events)); };
            var c = session.Combat.State;
            var target = c.Enemies[0]["id"].Value<string>();
            var played = false;
            foreach (var card in c.Piles.Hand.Select(h => h["instanceId"].Value<string>()).ToList())
            {
                var intent = new JObject { ["type"] = "playCard", ["cardInstanceId"] = card, ["targetId"] = target };
                if (session.Execute(intent).Accepted || session.Execute(new JObject { ["type"] = "playCard", ["cardInstanceId"] = card }).Accepted) { played = true; break; }
            }
            Assert.That(played, Is.True, "a playable opening card");
            Assert.That(heard, Has.Member("cardPlay"), "an accepted play makes its sound");
        }
    }
}
