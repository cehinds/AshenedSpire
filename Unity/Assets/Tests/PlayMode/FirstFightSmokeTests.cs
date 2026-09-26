using System;
using System.Collections;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.Domain.Combat;
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
    /// F1 acceptance smoke (09 §F1; US-2.1, US-5.1, US-5.9, US-5.10, US-17.1; AF-10, AF-12, PF-06): Boot → title → New →
    /// W-03 slot → W-04 Class (the data default) → Begin → W-07 → play a card on an enemy (keyboard targeting) → End Turn
    /// and watch the enemy turn → pause (Escape; pad Start closes and reopens it) → Save &amp; quit (hold-to-confirm) →
    /// title → Continue → the same state hash → finish the fight through the screen's command path → the end state with
    /// rewards marked for a later build → title. Real Input System keyboard and pad devices drive the focus model.
    /// </summary>
    [TestFixture, Category("Smoke")]
    public class FirstFightSmokeTests
    {
        private string _saves;
        private Keyboard _keyboard;
        private Gamepad _pad;
        private InputSettings.BackgroundBehavior _background;
        private InputSettings.EditorInputBehaviorInPlayMode _editorInput;

        [SetUp]
        public void SetUp()
        {
            _saves = Path.Combine(Path.GetTempPath(), "ashen-first-fight-" + Guid.NewGuid().ToString("N"));
            AshenBoot.SaveDirectoryOverride = _saves;
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
            if (Directory.Exists(_saves)) Directory.Delete(_saves, true);
        }

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

        /// <summary>Press a key until the condition holds (focus walking), at most n times.</summary>
        private IEnumerator PressUntil(Key key, Func<bool> condition, int n, string what)
        {
            for (var i = 0; i < n && !condition(); i++) yield return Press(key);
            Assert.That(condition(), Is.True, what + " — focused: " + Describe(Focused));
        }

        private static Navigator Nav => AshenBoot.Current != null && AshenBoot.Current.Host != null ? AshenBoot.Current.Host.Navigator : null;
        private static VisualElement Focused => Nav?.Top?.Focus.Focused;

        private static string Describe(VisualElement e) => e == null ? "nothing" : e.GetType().Name + " '" + e.name + "' " + ((e as TextElement)?.text ?? string.Empty);

        private static CombatCommand NextCommand(CombatState state)
        {
            var view = CombatViewModel.Build(state);
            var card = view.Hand.FirstOrDefault(c => c.Playable);
            if (card != null) return CombatCommand.PlayCard(card.InstanceId, card.Targets.FirstOrDefault());
            var plan = HandRules.Plan(state);
            return CombatCommand.EndTurn(plan.Cards.Take((int)plan.Minimum).Select(c => c.Value<string>("instanceId")));
        }

        [UnityTest]
        public IEnumerator NewClassFightPauseSaveQuitContinueAndFinish()
        {
            yield return SceneManager.LoadSceneAsync("Boot", LoadSceneMode.Single);
            yield return Until(() => Nav != null && Nav.CurrentId == ScreenIds.Title, 60f, "the title screen");
            var title = (TitleScreen)Nav.Top.View;
            yield return Press(Key.Space);
            yield return Until(() => !title.GateActive && Focused != null, 5f, "the gate to lift");

            // Title → New → W-03 New (slot 1, empty) → Start in slot 1? → Begin.
            yield return PressUntil(Key.DownArrow, () => Focused == title.Buttons[2], 4, "focus on New");
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Slots && Focused != null, 5f, "W-03 New");
            var slots = (SlotsScreen)Nav.Top.View;
            Assert.That(Focused, Is.SameAs(slots.Rows[0]), "New mode focuses the first empty slot");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the start door");
            Assert.That(Focused.name, Is.EqualTo(UiNames.ConfirmBack), "doors open on Back (04 §0)");
            yield return Press(Key.DownArrow);
            Assert.That(Focused.name, Is.EqualTo(UiNames.ConfirmPrimary));
            yield return Press(Key.Enter);

            // W-04 Class pane: the data default is preselected and focused; walk to Begin and press it.
            yield return Until(() => Nav.CurrentId == ScreenIds.Creation && Focused != null, 20f, "W-04 creation");
            var creation = (CreationScreen)Nav.Top.View;
            var content = AshenBoot.Current.Host.Context.RunContent;
            Assert.That(creation.ViewModel.Selected, Is.EqualTo(content.DefaultClass()), "the data default class is preselected (rules/runFlow.json)");
            Assert.That(Focused, Is.SameAs(creation.Tiles[creation.Classes.ToList().IndexOf(content.DefaultClass())]), "focus starts on the selected class");
            var begin = Nav.Top.Root.Q(UiNames.FooterPrimary);
            yield return PressUntil(Key.DownArrow, () => Focused == begin, 8, "focus on Begin");
            yield return Press(Key.Enter);

            // W-07: play a targeted card on an enemy with the keyboard.
            yield return Until(() => Nav.CurrentId == ScreenIds.Combat && Focused != null, 20f, "W-07 combat");
            var combat = (CombatScreen)Nav.Top.View;
            var session = combat.Session;
            Assert.That(session.EncounterId, Is.EqualTo(content.FirstFightEncounter()), "the data rule's Weald encounter");
            Assert.That(combat.Enemies.Count, Is.GreaterThanOrEqualTo(2));
            Func<int> focusedCard = () => combat.HandCards.ToList().IndexOf(Focused as Ashen.Presentation.UI.Kit.CardView);
            Func<bool> onTargetedPlayable = () =>
            {
                var i = focusedCard();
                return i >= 0 && combat.View.Hand[i].Playable && combat.View.Hand[i].NeedsTarget;
            };
            yield return PressUntil(Key.RightArrow, onTargetedPlayable, combat.HandCards.Count + 1, "a playable card that needs a target");
            var armed = combat.View.Hand[focusedCard()];
            var played = armed.InstanceId;
            var stateBefore = session.StateHash();
            yield return Press(Key.Enter);
            Assert.That(combat.Mode, Is.EqualTo(UiValues.FocusTargeting), "an armed card enters targeting");
            yield return Until(() => Focused is Combatant, 5f, "focus on a legal target");
            var target = ((Combatant)Focused).Data.Id;
            var hpBefore = combat.View.Enemies.First(e => e.Id == target).Hp + combat.View.Enemies.First(e => e.Id == target).Block;
            yield return Press(Key.Enter);
            Assert.That(combat.Mode, Is.Null, "targeting ends with the play");
            Assert.That(combat.View.Hand.Any(c => c.InstanceId == played), Is.False, "the card left the hand");
            var enemyAfter = combat.View.Enemies.First(e => e.Id == target);
            if (armed.DamageByTarget.TryGetValue(target, out var damage) && damage > 0)
                Assert.That(!enemyAfter.Alive || enemyAfter.Hp + enemyAfter.Block < hpBefore, Is.True, "the enemy took the hit");
            Assert.That(session.StateHash(), Is.Not.EqualTo(stateBefore));
            Assert.That(session.Saves.Load(session.Slot).Commands.Count, Is.EqualTo(1), "the play was autosaved to the log");

            // End Turn (pad d-pad walks to it), then watch the enemy turn; Skip fast-forwards it.
            yield return Until(() => Focused != null, 5f, "focus after the play");
            for (var i = 0; i < 20 && Focused != combat.Footer.EndTurnButton; i++) yield return Press(GamepadButton.DpadDown);
            Assert.That(Focused, Is.SameAs(combat.Footer.EndTurnButton), "the pad reaches End Turn");
            var turn = combat.View.Turn;
            yield return Press(GamepadButton.South);
            if (combat.Mode == UiValues.FocusDiscard) yield return Press(Key.Escape);
            Assert.That(combat.EnemyTurn, Is.True, "the enemy turn is shown (US-5.10)");
            Assert.That(Nav.Top.Root.ClassListContains(UiClasses.CombatModePrefix + UiValues.FocusEnemyTurn), Is.True, "the enemy turn is visibly distinct");
            yield return Until(() => !combat.EnemyTurn, 20f, "the player's turn");
            Assert.That(combat.View.Turn, Is.GreaterThan(turn));
            Assert.That(combat.View.Enemies.Where(e => e.Alive).All(e => e.Intent != null), Is.True, "the next intents show before the player's turn");

            // Pause: Escape opens it; pad Start resumes and reopens.
            yield return Until(() => Focused != null, 5f, "focus on the player's turn");
            yield return Press(Key.Escape);
            yield return Until(() => Nav.CurrentId == ScreenIds.Pause && Focused != null, 5f, "W-20 pause");
            var pause = (PauseScreen)Nav.Top.View;
            Assert.That(Focused, Is.SameAs(pause.ResumeButton), "pause opens on Resume");
            yield return Press(GamepadButton.Start);
            yield return Until(() => Nav.CurrentId == ScreenIds.Combat, 5f, "pad Start resumes");
            yield return Press(GamepadButton.Start);
            yield return Until(() => Nav.CurrentId == ScreenIds.Pause && Focused != null, 5f, "pad Start pauses");
            pause = (PauseScreen)Nav.Top.View;

            // Save & quit: the row, its hold door, hold Enter past holdMs.
            var saveQuit = pause.Rows.Single(r => r.enabledSelf);
            yield return PressUntil(Key.UpArrow, () => Focused == saveQuit, 4, "focus on Save & quit");
            var hash = session.StateHash();
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the Save & quit door");
            yield return Press(Key.DownArrow);
            Assert.That(Focused.name, Is.EqualTo(UiNames.ConfirmHold), "Save & quit is hold-to-confirm");
            var holdMs = AshenBoot.Current.Host.Context.Data.Tokens.Duration(TokenKeys.HoldConfirm);
            yield return Hold(Key.Enter, holdMs / 1000f + 0.5f);
            yield return Until(() => Nav.CurrentId == ScreenIds.Title && Nav.Stack.Count == 1, 10f, "the title after Save & quit");

            // Continue resumes the same fight, same state.
            title = (TitleScreen)Nav.Top.View;
            yield return Until(() => Focused != null, 5f, "focus on the title");
            Assert.That(title.Buttons[0].enabledSelf, Is.True, "Continue is enabled with a save");
            Assert.That(Focused, Is.SameAs(title.Buttons[0]), "focus lands on Continue");
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the load door");
            yield return Press(Key.DownArrow);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Combat, 20f, "W-07 after Continue");
            combat = (CombatScreen)Nav.Top.View;
            Assert.That(combat.Session, Is.Not.SameAs(session), "a fresh session loaded from the slot");
            Assert.That(combat.Session.StateHash(), Is.EqualTo(hash), "resume mid-fight gives the identical state (PF-06)");

            // Finish the fight through the screen's command path, then the end state and back to the title.
            for (var i = 0; i < 400 && !combat.Ended; i++)
            {
                combat.Command(NextCommand(combat.Session.Combat.State));
                if (combat.EnemyTurn) combat.Skip();
                yield return null;
            }
            Assert.That(combat.Ended, Is.True, "the fight ends");
            var end = Nav.Top.Root.Q(UiNames.CombatEnd);
            Assert.That(end.ClassListContains(UiClasses.Hidden), Is.False, "the end state shows");
            Assert.That(Nav.Top.Root.Q(UiNames.EndRewards).enabledSelf, Is.False, "rewards are marked for a later build (D-058)");
            yield return Until(() => Focused != null && Focused.name == UiNames.EndToTitle, 5f, "focus on Return to title");
            var reloaded = RunSession.Load(content, AshenBoot.Current.Host.Context.Saves, 1);
            Assert.That(reloaded.Session.Combat.IsOver, Is.True, "the finished fight is checkpointed");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Title, 5f, "the title after the fight");
        }
    }
}
