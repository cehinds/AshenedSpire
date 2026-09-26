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
                var text = CombatText.Refusal(Ui.Strings, new Refusal(key, 2d, 1d));
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
        public void TheFirstFightViewIsDisplayReadyWithFlasksAndIntents()
        {
            var session = RunSession.New(Content, Saves(), 1, 424242, Content.DefaultClass(), "Aldric");
            session.StartEncounter();
            var view = CombatViewModel.Build(session.Combat.State);
            Assert.That(view.Enemies.Count, Is.GreaterThanOrEqualTo(2), "the patrol has a choice of targets");
            Assert.That(view.Enemies.All(e => e.Intent != null), Is.True, "every living enemy telegraphs");
            foreach (var e in view.Enemies) Assert.That(CombatText.Intent(Ui.Strings, e.Intent), Does.Not.Contain("{"), e.DefId);
            Assert.That(view.Flasks.Where(f => f.IsCharge).Select(f => f.ChargeKind), Is.EquivalentTo(new[] { "hp", "mana" }));
            var run = session.Run.Value<JObject>("flaskCharges");
            foreach (var f in view.Flasks.Where(f => f.IsCharge))
            {
                Assert.That(f.Max, Is.EqualTo(run.Value<double>(f.ChargeKind)), f.ChargeKind);
                Assert.That(f.Name, Is.Not.Empty, f.ChargeKind);
                Assert.That(f.Usable, Is.EqualTo(f.Current > 0), f.ChargeKind);
            }
            foreach (var card in view.Hand)
            {
                Assert.That(CombatText.CardKind(Ui.Strings, card.Kind), Is.Not.Null, card.CardId);
                if (!card.Playable) Assert.That(CombatText.Refusal(Ui.Strings, card.Refusal), Is.Not.Empty, card.CardId);
            }
        }

        [Test]
        public void DrinkingAChargeFlaskSpendsOneCharge()
        {
            var session = RunSession.New(Content, Saves(), 1, 99, Content.DefaultClass(), "Aldric");
            session.StartEncounter();
            var flask = CombatViewModel.Build(session.Combat.State).Flasks.First(f => f.IsCharge && f.Usable);
            var outcome = session.Execute(CombatCommand.UseFlask(0, flask.ChargeKind, flask.Targeted ? session.Combat.State.Enemies[0].Value<string>("id") : null));
            Assert.That(outcome.Accepted, Is.True, CombatText.Refusal(Ui.Strings, outcome.Refusal));
            var after = CombatViewModel.Build(session.Combat.State).Flasks.First(f => f.ChargeKind == flask.ChargeKind);
            Assert.That(after.Current, Is.EqualTo(flask.Current - 1));
        }

        [Test]
        public void ThePromptedDiscardChooserRefusesTooManyAndAcceptsAChoice()
        {
            var patch = new JObject { ["files"] = new JObject { [ContentFiles.RulesHandRules] = new JObject { ["promptDiscard"] = true } } };
            var content = RunContent.Load(TestContent.Source, null, new[] { patch });
            var session = RunSession.New(content, Saves(), 1, 7, content.DefaultClass(), "Aldric");
            session.StartEncounter();
            var view = CombatViewModel.Build(session.Combat.State);
            Assert.That(view.DiscardChoice, Is.True, "promptDiscard offers the chooser at turn end (US-5.2)");
            var eligible = HandRules.Plan(session.Combat.State).Cards.Select(c => c.Value<string>("instanceId")).ToList();
            var tooMany = eligible.Take((int)view.DiscardMaximum + 1).ToList();
            if (tooMany.Count > view.DiscardMaximum)
            {
                var refused = session.Execute(CombatCommand.EndTurn(tooMany));
                Assert.That(refused.Refusal?.Key, Is.EqualTo(CombatStringKeys.CombatRefusalDiscardSelection));
            }
            var handBefore = session.Combat.State.Piles.Hand.Count;
            Assert.That(session.Execute(CombatCommand.EndTurn(eligible.Take(1))).Accepted, Is.True);
            Assert.That(session.Combat.State.Piles.Discard.Any(c => c.Value<string>("instanceId") == eligible[0]), Is.True);
            Assert.That(handBefore, Is.GreaterThan(0));
        }

        [Test]
        public void PauseRowsFollowTheirRulesInAFight()
        {
            var states = MenuRules.Evaluate(Ui.Menus.Pause, Ui.MenuContext(true, true));
            var byId = states.ToDictionary(s => s.Def.Id);
            Assert.That(byId["armoury"].Visible, Is.False, "the Armoury is out of combat only (W-20)");
            Assert.That(byId["saveQuit"].Enabled, Is.True);
            Assert.That(byId["deck"].Enabled, Is.False, "the pile viewer is planned (D-058)");
            Assert.That(byId["settings"].Enabled, Is.False, "settings are planned");
            Assert.That(Ui.Menus.Confirm(ConfirmIds.SaveQuit).Hold, Is.True, "Save & quit is hold-to-confirm (W-20 ⟲)");
            Assert.That(Ui.Policies.IsDestructive(Ui.Menus.Confirm(ConfirmIds.ReplaceSlot).Policy), Is.True, "W2c Replace is destructive");
            Assert.That(MenuRules.Evaluate(Ui.Menus.Pause, Ui.MenuContext(true, false)).Single(s => s.Def.Id == "armoury").Visible, Is.True);
        }

        [Test]
        public void TheCreationRailHasTheClassPaneBuiltAndTheRestPlanned()
        {
            Assert.That(Ui.Menus.CreationPanes.Select(p => p.Id), Is.EqualTo(new[] { "class", "character", "equipment", "review" }), "04 W-04 rail order");
            Assert.That(Ui.Menus.CreationPanes.Where(p => p.Built).Select(p => p.Id), Is.EqualTo(new[] { "class" }));
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
            var combat = Ui.Screens.Get(ScreenIds.Combat);
            foreach (var mode in new[] { UiValues.FocusTargeting, UiValues.FocusDiscard, UiValues.FocusFlasks, UiValues.FocusEnemyTurn, UiValues.FocusEnded })
            {
                Assert.That(combat.FocusModes.ContainsKey(mode), Is.True, mode);
                foreach (var region in combat.FocusOrderFor(mode)) Assert.That(names, Does.Contain(region), mode + " " + region);
            }
            Assert.That(combat.FocusOrderFor(null), Is.EqualTo(combat.FocusOrder));
            var rewards = Ui.Screens.Get(ScreenIds.Rewards);
            Assert.That(Ui.Screens.IsBuilt(ScreenIds.Rewards), Is.True, "W-08 rewards is built (us-11.2)");
            Assert.That(rewards.FocusModes.ContainsKey(UiValues.FocusPick), Is.True, "the pick sub-state has its own focus order");
            foreach (var region in rewards.FocusOrderFor(UiValues.FocusPick).Concat(rewards.FocusOrder)) Assert.That(names, Does.Contain(region), "rewards " + region);
            Assert.That(Ui.Screens.IsBuilt(ScreenIds.ActMap), Is.True, "the act map is built (us-4.2), so W-08 Continue goes on to the map");
            foreach (var id in new[] { ScreenIds.ActMap, ScreenIds.Rest })
            {
                var screen = Ui.Screens.Get(id);
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
