using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// F1 first-fight UI data and view text (W-04, W-07, W-20; US-2.1, US-5.1, US-5.2, US-5.9, US-17.1): refusals and
    /// intents resolve to player text, flasks and the discard chooser come from the view-model, the pause rows follow
    /// their rules in a fight, the creation rail has one built pane, focus modes name real regions, and art includes
    /// match by prefix or wildcard. Engine-free.
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class FirstFightUiTests
    {
        private static UiData _ui;
        private static RunContent _content;
        private string _dir;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);
        private static RunContent Content => _content ??= RunContent.Load(TestContent.Source);

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "ashen-ff-ui-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private SaveService Saves() => new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));

        [Test]
        public void EveryCombatRefusalResolvesToASentence()
        {
            var combat = TestContent.ContentJson(ContentFiles.StringsCombatEn);
            foreach (var key in combat.Properties().Select(p => p.Name).Where(k => k.StartsWith("combat.refusal.", StringComparison.Ordinal)))
            {
                var text = CombatText.Refusal(Ui.Strings, new Refusal(key, 2d, 1d, 3d, 4d));
                Assert.That(text, Is.Not.Empty.And.Not.EqualTo(key), key);
                Assert.That(Regex.IsMatch(text, "\\{\\d\\}"), Is.False, key + " → " + text);
            }
            Assert.That(CombatText.Refusal(Ui.Strings, new Refusal(CombatStringKeys.CombatRefusalEnergy, 2d, 1d)), Is.EqualTo("Needs 2 Energy (you have 1)."));
            Assert.That(CombatText.Refusal(Ui.Strings, null), Is.Null);
        }

        [Test]
        public void IntentsShowTheirLiveNumbers()
        {
            Assert.That(CombatText.Intent(Ui.Strings, new IntentView { LabelKey = CombatStringKeys.CombatIntentAttack, Damage = 7, Hits = 1 }), Is.EqualTo("Attack 7"));
            Assert.That(CombatText.Intent(Ui.Strings, new IntentView { LabelKey = CombatStringKeys.CombatIntentAttack, Damage = 4, Hits = 2 }), Is.EqualTo("Attack 4×2"));
            Assert.That(CombatText.Intent(Ui.Strings, new IntentView { LabelKey = CombatStringKeys.CombatIntentBlock, Block = 6 }), Is.EqualTo("Defend 6"));
            Assert.That(CombatText.Intent(Ui.Strings, new IntentView { LabelKey = CombatStringKeys.CombatIntentStaggered }), Is.EqualTo("Staggered"));
            Assert.That(CombatText.Intent(Ui.Strings, new IntentView { LabelKey = CombatStringKeys.CombatIntentAttack, Damage = 9, Pending = true }), Does.Contain("charging"));
            foreach (var kind in new[] { "attack", "skill", "power", "status", "curse" })
                Assert.That(CombatText.CardKind(Ui.Strings, kind), Is.Not.Empty.And.Not.StartWith("combat."), kind);
        }

        [Test]
        public void PauseRowsFollowTheirRulesInAFight()
        {
            var states = MenuRules.Evaluate(Ui.Menus.Pause, Ui.MenuContext(true, true));
            var byId = states.ToDictionary(s => s.Def.Id);
            Assert.That(byId["armoury"].Visible, Is.False, "the Armoury is out of combat only (W-20)");
            Assert.That(byId["saveQuit"].Enabled, Is.True);
            Assert.That(byId["deck"].Enabled, Is.False, "the pile viewer is planned (D-058)");
            Assert.That(byId["settings"].Enabled, Is.True, "settings are built (VP-13)");
            Assert.That(Ui.Menus.Confirm(ConfirmIds.SaveQuit).Hold, Is.True, "Save & quit is hold-to-confirm (W-20 ⟲)");
            Assert.That(Ui.Policies.IsDestructive(Ui.Menus.Confirm(ConfirmIds.ReplaceSlot).Policy), Is.True, "W2c Replace is destructive");
            Assert.That(MenuRules.Evaluate(Ui.Menus.Pause, Ui.MenuContext(true, false)).Single(s => s.Def.Id == "armoury").Visible, Is.True);
        }

        [Test]
        public void TheCreationRailHasTheClassPaneBuiltAndTheRestPlanned()
        {
            Assert.That(Ui.Menus.CreationPanes.Select(p => p.Id), Is.EqualTo(new[] { "class", "review" }), "the web creation rail order (Class, Review)");
            Assert.That(Ui.Menus.CreationPanes.Where(p => p.Built).Select(p => p.Id), Is.EqualTo(new[] { "class", "review" }));
            foreach (var p in Ui.Menus.CreationPanes) Assert.That(Ui.Strings.Has(p.LabelKey), Is.True, p.LabelKey);
            Assert.That(Ui.Strings.Has(Content.Flow.NameKey), Is.True, "the default character name is a string");
        }

        [Test]
        public void TheClassPreviewReadsTheStartingRun()
        {
            var id = Content.DefaultClass();
            var preview = Content.Preview(id);
            var run = Ashen.Domain.Run.RunState.Create(Content.Data, 1u, id);
            Assert.That(preview.MaxHp, Is.EqualTo(run.Value<double>("maxHp")));
            Assert.That(preview.Actions, Is.EqualTo(run.Value<double>("energyMax")));
            Assert.That(preview.Attributes.Select(a => a.Key), Is.EqualTo(Content.Data.Attributes.All.Select(a => a.Value<string>("id")).Where(a => run["attributes"][a] != null)));
            foreach (var a in preview.Attributes) Assert.That(Ui.Strings.Has(string.Format(UiFormats.AttributeShortKey, a.Key)), Is.True, a.Key);
            foreach (var cls in Content.ClassIds)
            {
                Assert.That(Ui.Strings.Has(string.Format(UiFormats.ClassNameKey, cls)), Is.True, cls);
                Assert.That(Ui.Strings.Has(string.Format(UiFormats.ClassDescriptionKey, cls)), Is.True, cls);
                Assert.That(Content.Preview(cls) == null, Is.EqualTo(Content.ClassProblem(cls) != null), cls);
            }
        }

        [Test]
        public void FocusModesNameRegionsThatExist()
        {
            var names = new HashSet<string>(typeof(UiNames).GetFields().Select(f => (string)f.GetRawConstantValue()));
            foreach (var id in new[] { ScreenIds.Core2Combat, ScreenIds.Core2Rewards, ScreenIds.ActMap, ScreenIds.Core2Rest, ScreenIds.Core2Shop, ScreenIds.Core2Event })
            {
                Assert.That(Ui.Screens.IsBuilt(id), Is.True, id);
                var screen = Ui.Screens.Get(id);
                Assert.That(screen.FocusOrderFor(null), Is.EqualTo(screen.FocusOrder), id);
                foreach (var region in screen.FocusOrder) Assert.That(names, Does.Contain(region), id + " " + region);
                foreach (var mode in screen.FocusModes.Keys)
                    foreach (var region in screen.FocusOrderFor(mode)) Assert.That(names, Does.Contain(region), id + " " + mode + " " + region);
            }
        }

        [Test]
        public void ArtIncludesMatchByPrefixOrWildcard()
        {
            Assert.That(ComponentDefaults.ArtMatches("class.", "class.reaver.sprite.ember"), Is.True);
            Assert.That(ComponentDefaults.ArtMatches("enemy.*.base", "enemy.blightHound.base"), Is.True);
            Assert.That(ComponentDefaults.ArtMatches("enemy.*.base", "enemy.blightHound.state.hurt"), Is.False);
            Assert.That(ComponentDefaults.ArtMatches("env.*-combat", "env.hollow-weald-combat"), Is.True);
            Assert.That(ComponentDefaults.ArtMatches("env.*-combat", "env.hollow-weald-map"), Is.False);
            var registry = TestContent.ContentJson(ContentFiles.AssetsRegistry);
            foreach (var enemy in Content.Data.Encounters.Get(Content.FirstFightEncounter())["enemies"].Select(t => (string)t))
                Assert.That(Ui.Components.ArtIncludes(StringTable.Fill(Ui.Components.EnemyArt, new StringArgs().Add(UiPlaceholders.Id, enemy))), Is.True, enemy);
            var region = Content.RegionOf(Content.FirstFightEncounter());
            var bg = StringTable.Fill(Ui.Components.CombatBackground, new StringArgs().Add(UiPlaceholders.Region, region));
            Assert.That(registry[bg], Is.Not.Null, bg);
            Assert.That(Ui.Components.ArtIncludes(bg), Is.True, bg);
            Assert.That(registry[Content.Portrait(Content.DefaultClass())], Is.Not.Null);
        }
    }

}
