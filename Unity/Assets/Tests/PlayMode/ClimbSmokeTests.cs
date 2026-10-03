using System;
using System.Collections;
using System.IO;
using System.Linq;
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
    /// F3 exit-gate smoke (09 §F3: "a seeded run from title to an act-3 boss win and to a death; resume works at every commit
    /// point"; US-4.x, US-8.x; AF-04, AF-06, AF-10, AF-11): Boot → title → New → W-03 → W-04 Begin → W-06, then a short
    /// seeded Custom Climb (the map rules' shortest act; enemy HP on the bot's assisted scale, a test hook) played through the
    /// screens with real keyboard and pad devices: map nodes are walked to and picked (select, then a repeat pick or the
    /// tray's Enter), fights are auto-played through the screen's command path by the smoke policy, reward doors are left
    /// with Continue (and their door), rest places take a hold-to-confirm Rest then Continue, the merchant is left, an
    /// event takes an open response through its review door then Continue, and a legacy dungeon's tray takes its first
    /// control each step (the node screens, D-140) — through act advances to the act-3 keeper and W-15. Save
    /// &amp; quit then Continue is exercised once each at the map, a rest place, a reward door and mid-fight, and each resume
    /// lands on the same screen with the identical state hash. The policy mirrors the EditMode driver, whose run with this
    /// seed is a victory (ClimbSessionTests.TheShortClimbWinsFastForThePlayModeSmoke).
    /// </summary>
    [TestFixture, Category("Smoke")]
    public class ClimbSmokeTests
    {
        private string _saves;
        private Keyboard _keyboard;
        private Gamepad _pad;
        private InputSettings.BackgroundBehavior _background;
        private InputSettings.EditorInputBehaviorInPlayMode _editorInput;

        [SetUp]
        public void SetUp()
        {
            _saves = Path.Combine(Path.GetTempPath(), "ashen-climb-smoke-" + Guid.NewGuid().ToString("N"));
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
            RunFlow.SeedOverride = null;
            RunFlow.CustomOverride = null;
            RunSession.ReviewEnemyHpScale = null;
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

        /// <summary>Walk focus with a key (or the pad's d-pad on odd presses) until the condition holds, at most n presses.</summary>
        private IEnumerator Walk(Func<bool> condition, int n, string what, bool down = true)
        {
            for (var i = 0; i < n && !condition(); i++)
            {
                if (i % 2 == 0) yield return Press(down ? Key.DownArrow : Key.UpArrow);
                else yield return Press(down ? GamepadButton.DpadDown : GamepadButton.DpadUp);
            }
            Assert.That(condition(), Is.True, what + " — focused: " + Describe(Focused));
        }

        private static Navigator Nav => AshenBoot.Current != null && AshenBoot.Current.Host != null ? AshenBoot.Current.Host.Navigator : null;
        private static VisualElement Focused => Nav?.Top?.Focus.Focused;

        private static string Describe(VisualElement e) => e == null ? "nothing" : e.GetType().Name + " '" + e.name + "' " + ((e as TextElement)?.text ?? string.Empty);

        private static int HoldMs => AshenBoot.Current.Host.Context.Data.Tokens.Duration(TokenKeys.HoldConfirm);

        private static RunSession Current => AshenBoot.Current.Host.Context.Session;

        // ------------------------------------------------------------------ flows

        private IEnumerator TitleToNewClimb()
        {
            yield return SceneManager.LoadSceneAsync("Boot", LoadSceneMode.Single);
            yield return Until(() => Nav != null && Nav.CurrentId == ScreenIds.Title, 60f, "the title screen");
            var title = (TitleScreen)Nav.Top.View;
            yield return Press(Key.Space);
            yield return Until(() => !title.GateActive && Focused != null, 5f, "the gate to lift");
            yield return Walk(() => Focused == title.Buttons[2], 4, "focus on New");
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Slots && Focused != null, 5f, "W-03 New");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the start door");
            yield return Press(Key.DownArrow);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Creation && Focused != null, 20f, "W-04 creation");
            var begin = Nav.Top.Root.Q(UiNames.FooterPrimary);
            yield return Walk(() => Focused == begin, 8, "focus on Begin");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.ActMap && Focused != null, 20f, "W-06 after Begin");
        }

        /// <summary>
        /// Escape → W-20 → Save &amp; quit (hold) → title → Continue → the load door → back on the same screen, with the
        /// identical state hash (AF-10, PF-06). Returns through <paramref name="resumed"/> the reloaded session.
        /// </summary>
        private IEnumerator SaveQuitResume(string screen, Action<RunSession> resumed)
        {
            var session = Current;
            var hash = session.StateHash();
            var location = session.Location;
            for (var i = 0; i < 4 && Nav.CurrentId != ScreenIds.Pause; i++) yield return Press(Key.Escape);
            yield return Until(() => Nav.CurrentId == ScreenIds.Pause && Focused != null, 5f, "W-20 over " + screen);
            var pause = (PauseScreen)Nav.Top.View;
            var saveQuit = pause.Rows.Single(r => r.enabledSelf);
            yield return Walk(() => Focused == saveQuit, 6, "focus on Save & quit", false);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the Save & quit door");
            yield return Press(Key.DownArrow);
            yield return Hold(Key.Enter, HoldMs / 1000f + 0.5f);
            yield return Until(() => Nav.CurrentId == ScreenIds.Title && Nav.Stack.Count == 1, 10f, "the title after Save & quit at " + screen);
            var title = (TitleScreen)Nav.Top.View;
            yield return Until(() => Focused == title.Buttons[0], 5f, "focus on Continue");
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the load door");
            yield return Press(Key.DownArrow);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == screen && Focused != null, 20f, screen + " after Continue");
            var loaded = Current;
            Assert.That(loaded, Is.Not.SameAs(session), "a fresh session loaded from the slot");
            Assert.That(loaded.Location, Is.EqualTo(location), "resume lands where the run stood");
            Assert.That(loaded.StateHash(), Is.EqualTo(hash), "resume at " + location + " gives the identical state (PF-06)");
            resumed(loaded);
        }

        [UnityTest]
        public IEnumerator AShortSeededClimbReachesTheSummitByKeyboardAndPad()
        {
            RunFlow.SeedOverride = ClimbPilot.ShortWinSeed;
            RunFlow.CustomOverride = ClimbPilot.ShortClimb();
            RunSession.ReviewEnemyHpScale = ClimbPilot.Assisted;
            yield return TitleToNewClimb();
            var pilot = new ClimbPilot(ClimbPilot.ShortWinSeed);
            var resumedAt = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var seen = new System.Collections.Generic.SortedDictionary<string, int>(StringComparer.Ordinal);
            var acts = Current.Act;
            var travels = 0;
            for (var step = 0; step < 600 && Nav.CurrentId != ScreenIds.RunEnd; step++)
            {
                var id = Nav.CurrentId;
                var at = Current?.Location ?? "none";
                seen[at] = seen.TryGetValue(at, out var n) ? n + 1 : 1;
                switch (id)
                {
                    case ScreenIds.ActMap:
                    {
                        var map = (ActMapScreen)Nav.Top.View;
                        if (map.PlannedOpen)
                        {
                            var before = Current.StateHash();
                            var instance = Nav.Top;
                            yield return Until(() => Focused == map.PlannedAction, 5f, "focus on the planned door's action");
                            yield return step % 2 == 0 ? Press(Key.Enter) : Press(GamepadButton.South);
                            yield return Until(() => Nav.Top != instance || Current.StateHash() != before, 10f, "the planned door's step");
                            break;
                        }
                        if (!resumedAt.Contains("map"))
                        {
                            resumedAt.Add("map");
                            yield return SaveQuitResume(ScreenIds.ActMap, _ => { });
                            break;
                        }
                        var target = Current.ReachableNodes()[0];
                        var node = map.Board.Node(target);
                        Assert.That(node, Is.Not.Null, "the reachable node is drawn");
                        Assert.That(node.focusable, Is.True, "reachable nodes take focus");
                        for (var i = 0; i < 16 && Focused != node; i++) yield return i % 2 == 0 ? Press(Key.RightArrow) : Press(GamepadButton.DpadRight);
                        Assert.That(Focused, Is.SameAs(node), "keyboard and pad reach the node");
                        var mapInstance = Nav.Top;
                        yield return Press(Key.Enter);
                        yield return Until(() => map.TrayOpen && map.Selected == target, 5f, "the node tray");
                        if (travels++ % 2 == 0)
                        {
                            yield return new WaitForSecondsRealtime(0.5f);
                            yield return Press(GamepadButton.South);
                        }
                        else
                        {
                            yield return Walk(() => Focused == map.EnterButton, 12, "focus on the tray's Enter");
                            yield return Press(Key.Enter);
                        }
                        yield return Until(() => Nav.Top != mapInstance, 10f, "travel away from the map");
                        break;
                    }
                    case ScreenIds.Combat:
                    {
                        var combat = (CombatScreen)Nav.Top.View;
                        for (var i = 0; i < 4000 && !combat.Ended; i++)
                        {
                            if (i == 3 && !resumedAt.Contains("combat") && !combat.Ended)
                            {
                                resumedAt.Add("combat");
                                yield return SaveQuitResume(ScreenIds.Combat, _ => { });
                                combat = (CombatScreen)Nav.Top.View;
                            }
                            combat.Command(pilot.Choose(combat.Session.Combat.State));
                            if (combat.EnemyTurn) combat.Skip();
                            yield return null;
                        }
                        Assert.That(combat.Ended, Is.True, "the fight ends");
                        var next = Nav.Top.Root.Q(combat.Session.RunOver ? UiNames.EndSummary : UiNames.EndRewards);
                        yield return Until(() => Focused == next, 5f, "focus on the end state's way on");
                        yield return Press(Key.Enter);
                        yield return Until(() => Nav.CurrentId != ScreenIds.Combat, 10f, "leaving the fight");
                        break;
                    }
                    case ScreenIds.Rewards:
                    {
                        if (!resumedAt.Contains("rewards"))
                        {
                            resumedAt.Add("rewards");
                            yield return SaveQuitResume(ScreenIds.Rewards, _ => { });
                        }
                        var rewards = (RewardsScreen)Nav.Top.View;
                        var instance = Nav.Top;
                        yield return Walk(() => Focused == rewards.ContinueButton, 16, "focus on Continue");
                        yield return Press(Key.Enter);
                        if (Nav.CurrentId == ScreenIds.Confirm)
                        {
                            yield return Until(() => Focused != null && Focused.name == UiNames.ConfirmBack, 5f, "the leave door opens on Back");
                            yield return Press(Key.DownArrow);
                            yield return Press(Key.Enter);
                        }
                        yield return Until(() => Nav.Top != instance && Nav.CurrentId != ScreenIds.Confirm, 10f, "leaving the rewards");
                        if (Current != null && Current.Act > acts)
                        {
                            acts = Current.Act;
                            seen["act"] = seen.TryGetValue("act", out var a) ? a + 1 : 1;
                        }
                        break;
                    }
                    case ScreenIds.Rest:
                    {
                        if (!resumedAt.Contains("rest"))
                        {
                            resumedAt.Add("rest");
                            yield return SaveQuitResume(ScreenIds.Rest, _ => { });
                        }
                        var rest = (RestScreen)Nav.Top.View;
                        var instance = Nav.Top;
                        if (rest.View.Option("rest").Available)
                        {
                            var hold = rest.RestHold;
                            yield return Walk(() => Focused == hold, 12, "focus on Rest ⟲", false);
                            yield return Hold(Key.Enter, HoldMs / 1000f + 0.5f);
                            yield return Until(() => Nav.Top != instance || !rest.View.Option("rest").Available, 10f, "the hold commits the Rest");
                        }
                        if (Nav.Top == instance && Nav.CurrentId == ScreenIds.Rest)
                        {
                            yield return Walk(() => Focused == rest.ContinueButton, 12, "focus on Continue");
                            yield return Press(Key.Enter);
                            yield return Until(() => Nav.Top != instance, 10f, "leaving the rest place");
                        }
                        break;
                    }
                    case ScreenIds.Merchant:
                    {
                        var merchant = (MerchantScreen)Nav.Top.View;
                        var instance = Nav.Top;
                        yield return Walk(() => Focused == merchant.LeaveButton, 40, "focus on the merchant's Leave");
                        yield return step % 2 == 0 ? Press(Key.Enter) : Press(GamepadButton.South);
                        yield return Until(() => Nav.Top != instance, 10f, "leaving the merchant");
                        break;
                    }
                    case ScreenIds.Event:
                    case ScreenIds.Dialogue:
                    {
                        var ev = (EventScreen)Nav.Top.View;
                        var instance = Nav.Top;
                        if (!ev.View.Resolved)
                        {
                            var open = ev.View.Responses.FindIndex(r => r.Available);
                            yield return Walk(() => Focused == ev.Responses[open], 12, "focus on an open response");
                            yield return Press(Key.Enter);
                            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the response's review door");
                            yield return Press(Key.DownArrow);
                            if (Focused.name == UiNames.ConfirmHold) yield return Hold(Key.Enter, HoldMs / 1000f + 0.5f);
                            else yield return Press(Key.Enter);
                            yield return Until(() => ev.View.Resolved, 5f, "the response taken");
                        }
                        yield return Walk(() => Focused == ev.ContinueButton, 12, "focus on the event's Continue");
                        yield return Press(Key.Enter);
                        yield return Until(() => Nav.Top != instance, 10f, "leaving the event");
                        break;
                    }
                    case ScreenIds.LegacyDungeon:
                    {
                        var dungeon = (DungeonScreen)Nav.Top.View;
                        var instance = Nav.Top;
                        var before = Current.StateHash();
                        yield return Walk(() => dungeon.Actions.Count > 0 && Focused == dungeon.Actions[0], 8, "focus on the tray's first control");
                        yield return step % 2 == 0 ? Press(Key.Enter) : Press(GamepadButton.South);
                        if (Nav.CurrentId == ScreenIds.Confirm)
                        {
                            yield return Until(() => Focused != null && Focused.name == UiNames.ConfirmBack, 5f, "the leave door opens on Back");
                            yield return Press(Key.DownArrow);
                            yield return Press(Key.Enter);
                        }
                        yield return Until(() => Nav.Top != instance || Current.StateHash() != before, 10f, "the dungeon step");
                        break;
                    }
                    default:
                        yield return null;
                        break;
                }
            }
            TestContext.Progress.WriteLine("climb smoke: " + string.Join(", ", seen.Select(p => p.Key + " " + p.Value)) + "; resumed at " + string.Join(", ", resumedAt));
            Assert.That(Nav.CurrentId, Is.EqualTo(ScreenIds.RunEnd), "the climb ends on W-15");
            var end = (RunEndScreen)Nav.Top.View;
            Assert.That(end.View.Victory, Is.True, "the seeded climb reaches the act-3 keeper and wins (US-4.7)");
            Assert.That(end.Session.Act, Is.EqualTo(3));
            foreach (var place in new[] { "map", "combat", "rewards", "rest" }) Assert.That(resumedAt, Does.Contain(place), "resumed at " + place);
            Assert.That(seen.Keys, Does.Contain("dungeon").And.Contain("merchant").And.Contain("event"), "the node screens were crossed");
            yield return Until(() => Focused == end.TitleButton, 5f, "focus on Return to title");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Title && Focused != null, 10f, "the title after the run");
            Assert.That(((TitleScreen)Nav.Top.View).Buttons[0].enabledSelf, Is.False, "the slot is clear: Continue is disabled");
        }
    }
}
