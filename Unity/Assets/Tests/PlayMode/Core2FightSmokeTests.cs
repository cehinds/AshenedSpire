using System;
using System.Collections;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.Generated;
using Ashen.Presentation;
using Ashen.Presentation.UI;
using Ashen.Presentation.UI.Kit;
using Ashen.Presentation.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Ashen.Tests.Play
{
    /// <summary>
    /// WP-C1b smoke (docs/design/11 §4; US-5.x, AF-05, AF-10, PF-06): a New Game starts a Core 2 run (rules/runFlow.json newRun.ruleset), Boot → title → New → W-03 → W-04 Begin → the Core 2 act map →
    /// the first fight node → W-07 for a Core 2 fight, driven with real keyboard and pad devices like the other smokes: a card is chosen with the arrow keys and played (a stance or a target asked
    /// for with Enter), the footer's End Turn is reached with the d-pad, the enemy turn is walked at the combat pace and skipped, Escape opens W-20, Save &amp; quit and Continue resume the same
    /// fight state (the Core 2 save, format 3), the fight is finished through the screen's own flow, Continue returns to the Core 2 map with the 'not yet available' notice for the rewards, and a
    /// second run is lost on purpose to reach W-15. The Core 2 flow's logic (prompts, intents, refusals) is held by Core2CombatFlowTests in the engine-free suite; this smoke is the wiring.
    /// </summary>
    [TestFixture, Category("Smoke")]
    public class Core2FightSmokeTests
    {
        private string _saves;
        private Keyboard _keyboard;
        private Gamepad _pad;
        private InputSettings.BackgroundBehavior _background;
        private InputSettings.EditorInputBehaviorInPlayMode _editorInput;

        [SetUp]
        public void SetUp()
        {
            _saves = Path.Combine(Path.GetTempPath(), "ashen-core2-smoke-" + Guid.NewGuid().ToString("N"));
            AshenBoot.SaveDirectoryOverride = _saves;
            RunFlow.SeedOverride = 20261024u;
            RunFlow.OpeningOverride = false;
            TitleScreen.ResetSession();
            _background = InputSystem.settings.backgroundBehavior;
            _editorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _pad = InputSystem.AddDevice<Gamepad>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_keyboard != null) InputSystem.RemoveDevice(_keyboard);
            if (_pad != null) InputSystem.RemoveDevice(_pad);
            InputSystem.settings.backgroundBehavior = _background;
            InputSystem.settings.editorInputBehaviorInPlayMode = _editorInput;
            AshenBoot.SaveDirectoryOverride = null;
            RunFlow.SeedOverride = null;
            RunFlow.OpeningOverride = null;
            if (Directory.Exists(_saves)) Directory.Delete(_saves, true);
        }

        // ------------------------------------------------------------------ input helpers

        private IEnumerator Press(Key key)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return null;
            yield return null;
        }

        private IEnumerator Hold(Key key, float seconds)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return null;
            yield return null;
        }

        private IEnumerator Press(GamepadButton button)
        {
            InputSystem.QueueStateEvent(_pad, new GamepadState(button));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(_pad, new GamepadState());
            yield return null;
            yield return null;
        }

        private static IEnumerator Until(Func<bool> condition, float seconds, string what)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end) Assert.Fail("timed out waiting for " + what + " — focused: " + Describe(Focused) + ", screen: " + Nav?.CurrentId);
                yield return null;
            }
        }

        private IEnumerator PressUntil(Key key, Func<bool> condition, int n, string what)
        {
            for (var i = 0; i < n && !condition(); i++) yield return Press(key);
            Assert.That(condition(), Is.True, what + " — focused: " + Describe(Focused));
        }

        private static Navigator Nav => AshenBoot.Current != null && AshenBoot.Current.Host != null ? AshenBoot.Current.Host.Navigator : null;
        private static VisualElement Focused => Nav?.Top?.Focus.Focused;
        private static UiContext Ui => AshenBoot.Current.Host.Context;

        private static string Describe(VisualElement e) => e == null ? "nothing" : e.GetType().Name + " '" + e.name + "' " + ((e as TextElement)?.text ?? string.Empty);

        // ------------------------------------------------------------------ flows

        /// <summary>Boot → title → New → W-03 slot 1 → the start door → W-04 Begin: the Core 2 act map is on screen.</summary>
        private IEnumerator TitleToCore2Map()
        {
            yield return SceneManager.LoadSceneAsync("Boot", LoadSceneMode.Single);
            yield return Until(() => Nav != null && Nav.CurrentId == ScreenIds.Title, 60f, "the title screen");
            var title = (TitleScreen)Nav.Top.View;
            yield return Press(Key.Space);
            yield return Until(() => !title.GateActive && Focused != null, 5f, "the gate to lift");
            yield return PressUntil(Key.DownArrow, () => Focused == title.Buttons[1], 4, "focus on New");
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Slots && Focused != null, 5f, "W-03 New");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the start door");
            yield return Press(Key.DownArrow);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Creation && Focused != null, 20f, "W-04 creation");
            var creation = (CreationScreen)Nav.Top.View;
            var content = Ui.RunContent;
            Assert.That(RunHost.Ruleset(Ui.RunContent), Is.EqualTo(RunFlowValues.RulesetCore2), "the data's New Game is the Core 2 run (rules/runFlow.json)");
            Assert.That(creation.Classes.ToList(), Is.EqualTo(content.Core2.ClassIds.ToList()), "W-04 offers the Core 2 profile's classes");
            var begin = Nav.Top.Root.Q(UiNames.FooterPrimary);
            yield return PressUntil(Key.DownArrow, () => Focused == begin, 8, "focus on Begin");
            yield return Press(Key.Enter); // Next: the Class pane opens Review
            yield return Until(() => ((CreationScreen)Nav.Top.View).ViewModel.Pane == FrontValues.PaneReview, 5f, "the Review pane");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.ActMap && Focused != null, 20f, "the Core 2 act map after Begin");
        }

        /// <summary>The map's first reachable node: focus, select, wait for the tray, Enter; the fight screen opens.</summary>
        private IEnumerator TravelToFirstNode()
        {
            var map = (ActMapScreen)Nav.Top.View;
            Assert.That(map.Core2, Is.Not.Null, "the map shows a Core 2 run");
            var first = map.Board.Node(map.Climb.ReachableNodes()[0]);
            yield return PressUntil(Key.RightArrow, () => Focused == first, 12, "focus on the first reachable node");
            yield return Press(Key.Enter);
            yield return Until(() => map.TrayOpen, 5f, "the node tray");
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Core2Combat && Focused != null, 20f, "W-07 for a Core 2 fight");
        }

        /// <summary>One card by keyboard: focus a playable card, Enter, and answer what it asks (a stance, a target, a route) with Enter on the first option.</summary>
        private IEnumerator PlayACard(Core2CombatScreen combat)
        {
            var index = combat.View.Hand.FindIndex(c => c.Playable);
            if (index < 0) yield break;
            var card = combat.HandCards[index];
            yield return PressUntil(Key.RightArrow, () => Focused == card || Focused == combat.HandCards[index], combat.HandCards.Count + 1, "focus on a playable card");
            var before = combat.Run.StateHash();
            yield return Press(Key.Enter);
            for (var guard = 0; guard < 6 && (combat.Mode == UiValues.FocusTargeting || combat.Mode == Core2UiValues.FocusPrompt); guard++)
            {
                yield return Until(() => Focused != null && (Focused is Fighter || Focused is LocButton), 5f, "focus on the card's question");
                yield return Press(Key.Enter);
            }
            Assert.That(combat.Run.StateHash(), Is.Not.EqualTo(before), "the card was played");
        }

        // ------------------------------------------------------------------ the smoke

        [UnityTest]
        public IEnumerator ANewGamePlaysACore2FightByKeyboardAndPadAndResumesIt()
        {
            yield return TitleToCore2Map();
            var map = (ActMapScreen)Nav.Top.View;
            Assert.That(map.View.Nodes.Any(n => n.Reachable), Is.True, "the first row is reachable");
            Assert.That(map.View.Nodes.All(n => n.Col >= 0d), Is.True, "every lane is a number (a rest depth sits between lanes)");
            Assert.That(Ui.Core2Session, Is.Not.Null);
            yield return TravelToFirstNode();

            // The fight: both sides are drawn from the Core 2 view.
            var combat = (Core2CombatScreen)Nav.Top.View;
            var session = combat.Run;
            Assert.That(combat.View.Enemies.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(combat.Enemies.Count, Is.EqualTo(combat.View.Enemies.Count), "a fighter per enemy");
            Assert.That(combat.HandCards.Count, Is.EqualTo(combat.View.Hand.Count));
            foreach (var enemy in combat.View.Enemies)
                Assert.That(combat.Enemies[enemy.Id].Data.IntentText, Is.Not.Empty, "every enemy shows its intent or the knowledge label");
            Assert.That(combat.Player.Data.Hp, Is.EqualTo((int)Math.Floor(combat.View.Player.Hp)));
            Assert.That(Nav.Top.Root.Q(UiNames.CombatLog), Is.Not.Null);

            // Play a card by keyboard, then the footer's End Turn by pad; the enemy turn is walked at the combat pace and skipped.
            yield return PlayACard(combat);
            Assert.That(session.Combat.State.EventLog.Count, Is.GreaterThan(0));
            yield return Until(() => Focused != null && !combat.EnemyTurn, 10f, "focus after the play");
            for (var i = 0; i < 30 && Focused != combat.Footer.EndTurnButton; i++) yield return Press(GamepadButton.DpadDown);
            Assert.That(Focused, Is.SameAs(combat.Footer.EndTurnButton), "the pad reaches End Turn");
            var turn = combat.View.Turn;
            yield return Press(GamepadButton.South);
            if (combat.EnemyTurn)
            {
                Assert.That(Nav.Top.Root.ClassListContains(UiClasses.CombatModePrefix + UiValues.FocusEnemyTurn), Is.True, "the enemy turn is visibly distinct");
                yield return Press(GamepadButton.South);
            }
            yield return Until(() => !combat.EnemyTurn, 30f, "the enemy turn to end");
            if (combat.Flow.Prompt?.Kind == Core2UiValues.PromptOffer)
            {
                // A reaction offer waits: Back (Take the hit) declines it.
                var back = Nav.Top.Root.Q(UiNames.PromptBack);
                yield return Until(() => Focused != null, 5f, "focus in the offer");
                for (var i = 0; i < 6 && Focused != back; i++) yield return Press(Key.DownArrow);
                yield return Press(Key.Enter);
                yield return Until(() => combat.Flow.Prompt == null && !combat.EnemyTurn, 30f, "the offer to be answered");
            }
            if (!combat.Ended) Assert.That(combat.View.Turn, Is.GreaterThan(turn));

            // Pause: Escape opens W-20; Save and Quit (no door, as the web's) returns to the title; Continue resumes the same fight.
            if (!combat.Ended)
            {
                yield return Until(() => Focused != null, 5f, "focus on the player's turn");
                yield return Press(Key.Escape);
                yield return Until(() => Nav.CurrentId == ScreenIds.Pause && Focused != null, 5f, "W-20 pause");
                var pause = (PauseScreen)Nav.Top.View;
                var saveQuit = pause.Row("saveQuit");
                yield return PressUntil(Key.DownArrow, () => Focused == saveQuit, 6, "focus on Save and Quit");
                var hash = session.StateHash();
                yield return Press(Key.Enter);
                yield return Until(() => Nav.CurrentId == ScreenIds.Title && Nav.Stack.Count == 1, 10f, "the title after Save and Quit");
                var title = (TitleScreen)Nav.Top.View;
                yield return Until(() => Focused == title.Buttons[0], 5f, "focus on Continue");
                yield return new WaitForSecondsRealtime(0.3f);
                yield return Press(Key.Enter);
                yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the load door");
                yield return Press(Key.DownArrow);
                yield return Press(Key.Enter);
                yield return Until(() => Nav.CurrentId == ScreenIds.Core2Combat, 20f, "W-07 after Continue");
                combat = (Core2CombatScreen)Nav.Top.View;
                Assert.That(combat.Run, Is.Not.SameAs(session), "a fresh session loaded from the slot (save format 3)");
                Assert.That(combat.Run.StateHash(), Is.EqualTo(hash), "resume mid-fight gives the identical state (PF-06)");
                session = combat.Run;
            }

            // Finish the fight through the screen's own flow (the bot of the engine-free suite presses the same controls).
            for (var i = 0; i < 400 && !combat.Flow.Ended; i++)
            {
                var step = PressNextControl(combat);
                combat.Apply(step);
                if (combat.EnemyTurn) combat.Skip();
                yield return null;
            }
            Assert.That(combat.Flow.Ended, Is.True, "the fight ends");

            if (!session.RunOver)
            {
                // A won fight: after the victory beat the spoils screen holds the reward door (VP-7); its level notices are read, then Skip leaves it for the Core 2 map.
                yield return Until(() => Nav.CurrentId == ScreenIds.Core2Rewards && Focused != null, 10f, "the spoils screen after the fight");
                var spoils = (Core2RewardsScreen)Nav.Top.View;
                for (var guard = 0; guard < 4 && spoils.Flow.Stage == Core2SpoilsStage.Notices; guard++)
                {
                    yield return Until(() => Focused == spoils.ConfirmButton, 5f, "focus on Continue");
                    yield return Press(Key.Enter);
                    yield return null;
                }
                Assert.That(spoils.Flow.Stage, Is.EqualTo(Core2SpoilsStage.Offer), "the offer follows the notices");
                Assert.That(spoils.Options, Is.Not.Empty, "the offer's options are drawn");
                yield return PressUntil(Key.DownArrow, () => Focused == spoils.BackButton, 8, "focus on Skip");
                yield return Press(Key.Enter);
                yield return Until(() => Nav.CurrentId == ScreenIds.ActMap && Focused != null, 10f, "the Core 2 map after the fight");
                var after = (ActMapScreen)Nav.Top.View;
                Assert.That(after.Core2, Is.Not.Null);
                Assert.That(after.Climb.Location, Is.EqualTo("map"));
                Assert.That(after.Climb.ReachableNodes(), Is.Not.Empty, "the climb goes on");
            }
        }

        [UnityTest]
        public IEnumerator ADefeatEndsTheCore2RunAndOpensTheRunEnd()
        {
            yield return TitleToCore2Map();
            var map = (ActMapScreen)Nav.Top.View;
            map.Core2.EditRunForReview(r => { r["hp"] = 1; r["maxHp"] = 1; });
            yield return TravelToFirstNode();
            var combat = (Core2CombatScreen)Nav.Top.View;
            var session = combat.Run;
            for (var i = 0; i < 400 && !combat.Flow.Ended; i++)
            {
                combat.Apply(combat.Flow.EndTurn());
                if (combat.EnemyTurn) combat.Skip();
                if (combat.Flow.Prompt?.Kind == Core2UiValues.PromptOffer) combat.Apply(combat.Flow.DeclineOffer());
                yield return null;
            }
            Assert.That(combat.Flow.Ended, Is.True, "the fight ends");
            Assert.That(session.RunOver, Is.True, "a death closes the run out");
            Assert.That(Ui.Saves.Exists(session.Slot), Is.False, "permadeath: the slot is cleared");
            yield return Until(() => combat.Ended && Focused != null, 10f, "the end state");
            Assert.That(Nav.Top.Root.Q(UiNames.EndContinue).ClassListContains(UiClasses.Hidden), Is.True, "no Continue after a death");
            yield return Until(() => Focused != null && Focused.name == UiNames.EndSummary, 5f, "focus on Run summary");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Core2RunEnd && Focused != null, 5f, "the run end (VP-12)");
            var runEnd = (Core2RunEndScreen)Nav.Top.View;
            Assert.That(runEnd.Flow.Victory, Is.False, "You perished");
            Assert.That(runEnd.Flow.Deck, Is.Not.Empty, "the final deck is listed");
            Assert.That(runEnd.Flow.Earned, Is.Not.Empty, "the finished run opened the next class");
            Assert.That(Focused, Is.SameAs(runEnd.AgainButton), "Run again is where focus lands, as the web's");
            runEnd.ToTitle();
            yield return Until(() => Nav.CurrentId == ScreenIds.Title && Focused != null, 5f, "the title after the defeat");
        }

        /// <summary>One press of the engine-free bot's policy through the screen's flow: answer what is open, else play the first playable card, else End Turn.</summary>
        private static Core2StepResult PressNextControl(Core2CombatScreen combat)
        {
            var flow = combat.Flow;
            if (flow.Prompt != null)
            {
                if (flow.Prompt.Kind == Core2UiValues.PromptOffer) return flow.DeclineOffer();
                if (flow.Prompt.Kind == Core2UiValues.PromptCards)
                {
                    foreach (var o in flow.Prompt.Options.Take(flow.Prompt.Count)) flow.Choose(o);
                    return flow.ConfirmPicks();
                }
                var option = flow.Prompt.Options.FirstOrDefault(o => o.Enabled);
                if (option != null) return flow.Choose(option);
                flow.Back();
                return new Core2StepResult();
            }
            if (flow.Targeting) return flow.PickTarget(flow.LegalTargets.First());
            for (var i = 0; i < flow.View.Hand.Count; i++)
            {
                if (!flow.View.Hand[i].Playable) continue;
                var step = flow.SelectCard(i);
                if (step.Refusal == null) return step;
            }
            return flow.EndTurn();
        }
    }
}
