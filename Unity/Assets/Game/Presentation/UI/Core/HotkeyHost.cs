using System;
using System.Collections.Generic;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Platform;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI
{
    /// <summary>
    /// The web's input layer, live (src/ui/input.js onKeydown and pollPads; P-7c, D-1750..). A key press on the UI root and a pad button polled each frame go to the engine-free <see cref="HotkeyDispatcher"/> with the
    /// top screen, what stands over it and the player's bindings, and what comes back is carried out: the screen's own hotkey (<see cref="IScreenView.HandleHotkey"/>), the tab ring (<see cref="IScreenView.StepTabs"/>),
    /// the focus (D-pad, Confirm) or Back (Cancel). A pad button is the synthetic key of the action it is bound to, a hint chip the same, so one door serves the keyboard, the pad and the thumb. The bindings follow
    /// <see cref="SettingsSession.Changed"/> (a rebind needs no restart); <see cref="Changed"/> tells the hint bars when they, or a pad arriving or leaving, change what a chip says.
    /// </summary>
    public sealed class HotkeyHost : IDisposable
    {
        private readonly UiHost _host;
        private readonly VisualElement _root;
        private readonly ControlsData _data;
        private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();
        private readonly Dictionary<int, bool> _padHeld = new Dictionary<int, bool>();
        private bool _padSeen;
        private bool _padConnected;

        public HotkeyHost(UiHost host, VisualElement root, ControlsData data)
        {
            _host = host;
            _root = root;
            _data = data;
            Dispatcher = new HotkeyDispatcher(data, new ControlBindings(data));
            // The panel's own root, not the document's: with nothing focused a key event goes to the panel root, above the document (the Navigator hooks its navigation events there for the same reason).
            root.RegisterCallback<AttachToPanelEvent>(_ => Hook());
            root.RegisterCallback<DetachFromPanelEvent>(_ => Unhook());
            Hook();
        }

        private IPanel _panel;

        private void Hook()
        {
            var panel = _root.panel;
            if (panel == null || panel == _panel) return;
            Unhook();
            _panel = panel;
            _panel.visualTree.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _panel.visualTree.RegisterCallback<KeyUpEvent>(OnKeyUp, TrickleDown.TrickleDown);
            _panel.visualTree.RegisterCallback<BlurEvent>(OnBlur, TrickleDown.TrickleDown);
        }

        private void Unhook()
        {
            if (_panel == null) return;
            _panel.visualTree.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _panel.visualTree.UnregisterCallback<KeyUpEvent>(OnKeyUp, TrickleDown.TrickleDown);
            _panel.visualTree.UnregisterCallback<BlurEvent>(OnBlur, TrickleDown.TrickleDown);
            _panel = null;
        }

        private void OnBlur(BlurEvent evt) => _down.Clear();

        public HotkeyDispatcher Dispatcher { get; }

        public ControlsData Data => _data;

        public ControlBindings Bindings => Dispatcher.Bindings;

        /// <summary>A pad is connected (web hasGamepad): the hint bar names its glyphs.</summary>
        public bool PadConnected => PadInput.Connected;

        /// <summary>The bindings, or whether a pad is connected, changed: a hint chip or an End Turn keycap says something else.</summary>
        public event Action Changed;

        /// <summary>Reads the player's bindings (web applyDisplaySettings: setBindings, setKeyBindings).</summary>
        public void Bind(SettingsSession settings)
        {
            if (settings == null) return;
            Dispatcher.Bindings = settings.Bindings(_data);
            var confirm = _data.Pad(Dispatcher.Bindings.Pad(HotkeyActions.Confirm) ?? 0);
            PadInput.ConfirmSource = confirm?.Source ?? ControlsValues.PadSouth;
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ the keyboard

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.None) return; // the character half of a key press: its key event already came
            var repeat = !_down.Add(evt.keyCode);
            var name = _data.KeyName(evt.keyCode.ToString(), evt.character);
            if (name == null) return;
            var state = State(false);
            if (state == null) return;
            var result = Dispatcher.Key(new KeyPress { Key = name, Ctrl = evt.ctrlKey, Alt = evt.altKey, Meta = evt.commandKey, Repeat = repeat }, state);
            if (!result.Consumed) return;
            evt.StopImmediatePropagation();
            evt.PreventDefault();
            Carry(result);
        }

        private void OnKeyUp(KeyUpEvent evt) => _down.Remove(evt.keyCode);

        // ------------------------------------------------------------------ the pad

        /// <summary>Per frame: the pad's buttons that went down (web pollPads, rising edges only; the first read of a pad only records what is held).</summary>
        public void Tick()
        {
            // A key up the window never saw (focus left it while held) must not leave the next press counted as a repeat.
            var keyboard = Keyboard.current;
            if (_down.Count > 0 && keyboard != null && !keyboard.anyKey.isPressed) _down.Clear();
            var connected = PadInput.Connected;
            if (connected != _padConnected)
            {
                _padConnected = connected;
                _padSeen = false;
                _padHeld.Clear();
                Changed?.Invoke();
            }
            if (!connected) return;
            var first = !_padSeen;
            _padSeen = true;
            foreach (var def in _data.PadButtons)
            {
                if (def.Source == null) continue;
                var down = PadInput.IsDown(def.Source);
                _padHeld.TryGetValue(def.Index, out var was);
                _padHeld[def.Index] = down;
                if (!down || was || first) continue;
                var state = State(true);
                if (state == null) continue;
                Carry(Dispatcher.Pad(def.Index, state));
            }
        }

        // ------------------------------------------------------------------ a hint chip

        /// <summary>A thumb on a hint chip arrives at the binding the keyboard and the pad already reach (web beginActionPress → doAction): refused while a veil stands, as the key is. True when it did something.</summary>
        public bool Perform(string actionId)
        {
            var state = State(true);
            if (state == null) return false;
            var result = Dispatcher.Perform(actionId, state);
            Carry(result);
            return result.Consumed;
        }

        // ------------------------------------------------------------------ carrying it out

        /// <summary>Where the press lands, or null when no screen is up to take it. A pad cannot be typing into a field (its synthetic key lands on the document).</summary>
        private HotkeyState State(bool pad)
        {
            var top = _host.Navigator?.Top;
            if (top == null) return null;
            var view = top.View;
            return new HotkeyState
            {
                Screen = top.Id,
                Typing = !pad && Typing(),
                Blocked = !top.Def.AcceptsInput || view.InputBlocked,
                Veil = view.VeilOpen,
                TabRing = view.HasTabRing,
            };
        }

        private bool Typing()
        {
            var focused = _root.panel?.focusController?.focusedElement as VisualElement;
            return focused != null && (focused is TextField || focused.GetFirstAncestorOfType<TextField>() != null);
        }

        private void Carry(HotkeyResult result)
        {
            if (result == null || !result.Consumed) return;
            var nav = _host.Navigator;
            var top = nav.Top;
            switch (result.Kind)
            {
                case HotkeyKind.TabPrev:
                    top.View.StepTabs(-1);
                    break;
                case HotkeyKind.TabNext:
                    top.View.StepTabs(1);
                    break;
                case HotkeyKind.Move:
                    _host.UseKeys();
                    nav.MoveFocus(result.Delta);
                    break;
                case HotkeyKind.Confirm:
                    _host.UseKeys();
                    nav.Submit();
                    break;
                case HotkeyKind.Cancel:
                    nav.Back();
                    break;
                default:
                    top.View.HandleHotkey(result);
                    break;
            }
        }

        public void Dispose()
        {
            Unhook();
        }
    }
}
