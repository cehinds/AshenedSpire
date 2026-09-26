using System;
using System.Collections;
using System.IO;
using Ashen.Generated;
using Ashen.Presentation;
using Ashen.Presentation.UI;
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
    /// PlayMode smoke (suite: Smoke; US-1.2, US-1.3, US-17.1): the Boot scene boots through W-24 to the title gate;
    /// a key lifts the gate and focus lands on the first enabled row (Load, since Continue is disabled with no save);
    /// the keyboard walks the menu, opens W-03 Load and Escape returns with focus back on Load; the pad moves focus too.
    /// Real Input System devices drive UI Toolkit through its InputSystemProvider.
    /// </summary>
    [TestFixture, Category("Smoke")]
    public class BootTitleSmokeTests
    {
        private string _saves;
        private Keyboard _keyboard;
        private Gamepad _pad;
        private InputSettings.BackgroundBehavior _background;
        private InputSettings.EditorInputBehaviorInPlayMode _editorInput;

        [SetUp]
        public void SetUp()
        {
            _saves = Path.Combine(Path.GetTempPath(), "ashen-smoke-" + Guid.NewGuid().ToString("N"));
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

        private IEnumerator Press(GamepadButton button)
        {
            InputSystem.QueueStateEvent(_pad, new GamepadState(button));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(_pad, new GamepadState());
            yield return null;
            yield return null;
        }

        private static IEnumerator Frames(int n)
        {
            for (var i = 0; i < n; i++) yield return null;
        }

        private static IEnumerator Until(Func<bool> condition, float seconds, string what)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end) Assert.Fail("timed out waiting for " + what);
                yield return null;
            }
        }

        private static Navigator Nav => AshenBoot.Current != null && AshenBoot.Current.Host != null ? AshenBoot.Current.Host.Navigator : null;

        private static VisualElement Focused => Nav?.Top?.Focus.Focused;

        private static string Describe(VisualElement e) => e == null ? "nothing" : e.GetType().Name + " '" + e.name + "' " + ((e as TextElement)?.text ?? string.Empty);

        private static void AssertFocus(VisualElement expected, string because) =>
            Assert.That(Focused, Is.SameAs(expected), because + " — focused: " + Describe(Focused) + ", expected: " + Describe(expected));

        [UnityTest]
        public IEnumerator BootGateTitleLoadAndBackWithKeyboardAndPad()
        {
            yield return SceneManager.LoadSceneAsync("Boot", LoadSceneMode.Single);
            yield return Until(() => Nav != null && Nav.CurrentId == ScreenIds.Title, 60f, "the title screen");

            var title = (TitleScreen)Nav.Top.View;
            Assert.That(title.GateActive, Is.True, "the press-any-input gate shows first (US-1.2)");
            Assert.That(Focused, Is.Null, "nothing is focused behind the gate");
            Assert.That(title.Buttons.Count, Is.EqualTo(7), "Continue, Load, New, Journal, Custom Run, Settings, Quit");
            Assert.That(title.Buttons[0].enabledSelf, Is.False, "Continue is disabled with no save");

            yield return Press(Key.Space);
            yield return Until(() => !title.GateActive, 5f, "the gate to lift");
            yield return Until(() => Focused != null, 5f, "focus after the gate");
            AssertFocus(title.Buttons[1], "focus lands on the first enabled row (Load)");

            yield return Press(Key.DownArrow);
            AssertFocus(title.Buttons[2], "Down moves to New");
            yield return Press(Key.UpArrow);
            AssertFocus(title.Buttons[1], "Up moves back to Load");
            yield return Press(Key.UpArrow);
            AssertFocus(Nav.Top.Root.Q(UiNames.AiDisclosure), "Up from the first enabled row wraps to the disclosure link");
            yield return Press(Key.DownArrow);
            AssertFocus(title.Buttons[1], "and Down wraps back past the disabled Continue");

            yield return new WaitForSecondsRealtime(0.3f);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Slots, 5f, "W-03 slots");
            yield return Until(() => Focused != null, 5f, "focus on W-03");
            AssertFocus(Nav.Top.Root.Q(UiNames.FooterPrimary), "with no saves, W-03 Load shows the empty state and focus starts on Create character");

            yield return Press(Key.Escape);
            yield return Until(() => Nav.CurrentId == ScreenIds.Title, 5f, "back to the title");
            yield return Until(() => Focused != null, 5f, "focus back on the title");
            AssertFocus(title.Buttons[1], "focus returns to Load");

            yield return Press(GamepadButton.DpadDown);
            AssertFocus(title.Buttons[2], "the pad moves focus too");
        }
    }
}
