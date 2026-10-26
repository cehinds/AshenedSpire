using System.Collections.Generic;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Platform;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Controls tab (web src/ui/screens/controls.js renderControls): the eyebrow, the title, the two lines of help with the pad's state, and one row per action: its words, its key and pad as keycaps and a Key and a Pad
    /// button beside them (a fixed key has its keycap alone). A Rebind button arms ("Press…"); the next key, or the next pad button, is the binding; Escape cancels an armed key; pressing the armed button again disarms
    /// it. Each rebind is stored in the profile at once (settings.keyBindings, settings.bindings) and tells the live consumers.
    /// </summary>
    public sealed class ControlsPanel
    {
        private readonly UiContext _ui;
        private readonly VisualElement _screen;
        private readonly SettingsSession _settings;
        private readonly ControlsData _data;
        private readonly ControlBindings _bindings;
        private readonly Dictionary<string, LocLabel> _keyCaps = new Dictionary<string, LocLabel>();
        private readonly Dictionary<string, LocLabel> _padCaps = new Dictionary<string, LocLabel>();
        private readonly HashSet<int> _held = new HashSet<int>();
        private LocButton _armed;
        private string _armedId;
        private bool _armedKey;
        private IVisualElementScheduledItem _poll;

        public ControlsPanel(UiContext ui, VisualElement screen, SettingsSession settings)
        {
            _ui = ui;
            _screen = screen;
            _settings = settings;
            _data = ui.Data.Controls;
            _bindings = settings.Bindings(_data);
            _screen.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
        }

        /// <summary>A Rebind is armed: the screen takes no navigation (the next key or button is the binding) and Escape cancels.</summary>
        public bool Armed => _armed != null;

        /// <summary>
        /// Back belongs to the panel while a Rebind is armed and for the frame after one settles: the web's capture owns the whole keydown (stopImmediatePropagation) so an armed Escape cancels the capture and never closes
        /// the menu, and here the Escape may reach the navigator before or after the key event does.
        /// </summary>
        public bool OwnsBack => Armed || Time.frameCount - _settledFrame <= 1;

        private int _settledFrame = -1;

        public ControlBindings Bindings => _bindings;

        /// <summary>The Key and Pad buttons in the list's order (a screen test presses them).</summary>
        public IReadOnlyList<LocButton> Rebinds => _rebinds;

        private readonly List<LocButton> _rebinds = new List<LocButton>();

        public void Fill(VisualElement host)
        {
            host.Clear();
            _keyCaps.Clear();
            _padCaps.Clear();
            _rebinds.Clear();
            var view = ControlsView.Build(_ui.Data, _bindings, PadInput.Connected);
            var pane = new VisualElement();
            pane.AddToClassList(MenuClasses.ControlsPane);
            pane.Add(Text(view.Eyebrow, MenuClasses.ControlsEyebrow, true));
            pane.Add(Text(view.Title, MenuClasses.ControlsTitle));
            pane.Add(Text(view.Intro, MenuClasses.ControlsIntro));
            var pad = new VisualElement { pickingMode = PickingMode.Ignore };
            pad.AddToClassList(MenuClasses.ControlsIntro);
            pad.style.flexDirection = FlexDirection.Row;
            pad.style.flexWrap = Wrap.Wrap;
            pad.Add(Text(view.PadState, view.PadConnected ? MenuClasses.ControlsPadLive : MenuClasses.ControlsPadOff));
            pad.Add(Text(view.PadNote, MenuClasses.ControlsPad));
            pane.Add(pad);
            var rule = new VisualElement { pickingMode = PickingMode.Ignore };
            rule.AddToClassList(MenuClasses.ControlsRule);
            pane.Add(rule);
            foreach (var row in view.Rows) pane.Add(Row(view, row));
            host.Add(pane);
        }

        private VisualElement Row(ControlsView view, ControlRowView row)
        {
            var element = new VisualElement();
            element.AddToClassList(MenuClasses.ControlsRow);
            element.Add(Text(row.Label, MenuClasses.ControlsLabel));
            var trail = new VisualElement();
            trail.AddToClassList(MenuClasses.ControlsTrail);
            var key = Cap(row.KeyText);
            if (row.KeyRebindable) _keyCaps[row.Id] = key;
            trail.Add(key);
            if (row.KeyRebindable) trail.Add(Rebind(view.KeyButton, row.Id, true));
            var pad = Cap(row.PadText);
            _padCaps[row.Id] = pad;
            trail.Add(pad);
            trail.Add(Rebind(view.PadButton, row.Id, false));
            element.Add(trail);
            return element;
        }

        private LocButton Rebind(string label, string id, bool key)
        {
            var button = new LocButton { userData = id + (key ? ControlsValues.RebindKey : ControlsValues.RebindPad) };
            button.AddToClassList(MenuClasses.ControlsRebind);
            button.SetResolved(label);
            button.clicked += () => Arm(button, id, key);
            _rebinds.Add(button);
            return button;
        }

        private static LocLabel Cap(string text)
        {
            var cap = new LocLabel { pickingMode = PickingMode.Ignore };
            cap.AddToClassList(MenuClasses.ControlsKeycap);
            cap.SetResolved(text);
            return cap;
        }

        private static LocLabel Text(string text, string className, bool upper = false)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            label.EnableInClassList(UiClasses.Upper, upper);
            label.SetResolved(text);
            return label;
        }

        // ------------------------------------------------------------------ arming

        private string RestingLabel(bool key) => _ui.Data.Strings.Get(key ? StringKeys.ControlsRebindKey : StringKeys.ControlsRebindPad);

        /// <summary>Clicking a Rebind arms it; clicking the armed one disarms it; clicking another moves the capture (web controls.js).</summary>
        private void Arm(LocButton button, string id, bool key)
        {
            if (_armed != null)
            {
                var same = _armed == button;
                Disarm();
                if (same) return;
            }
            _armed = button;
            _armedId = id;
            _armedKey = key;
            button.SetResolved(_ui.Data.Strings.Get(StringKeys.ControlsRebindListening));
            button.AddToClassList(MenuClasses.ControlsListening);
            if (key) return;
            _held.Clear();
            foreach (var def in _data.PadButtons)
                if (PadInput.IsDown(def.Source)) _held.Add(def.Index);
            _poll = button.schedule.Execute(PollPad).Every(_ui.Data.Tokens.Duration(TokenKeys.ControlsPoll));
        }

        /// <summary>Puts the armed button back to its label (web reset()); nothing is bound.</summary>
        public void Disarm()
        {
            if (_armed == null) return;
            _poll?.Pause();
            _poll = null;
            _armed.SetResolved(RestingLabel(_armedKey));
            _armed.RemoveFromClassList(MenuClasses.ControlsListening);
            _settledFrame = Time.frameCount;
            _armed = null;
            _armedId = null;
        }

        // ------------------------------------------------------------------ the capture

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (_armed == null || !_armedKey) return;
            var name = _data.KeyName(evt.keyCode.ToString(), evt.character);
            if (name == null) return;
            evt.StopImmediatePropagation();
            evt.PreventDefault();
            var button = _armed;
            if (name == _data.CancelKey)
            {
                Disarm();
                button.Focus();
                return;
            }
            var id = _armedId;
            if (_bindings.SetKey(id, name))
            {
                _settings.SaveBindings(_data, _bindings);
                if (_keyCaps.TryGetValue(id, out var cap)) cap.SetResolved(_bindings.KeyLabel(id));
            }
            Disarm();
            button.Focus();
        }

        private void PollPad()
        {
            if (_armed == null || _armedKey) return;
            foreach (var def in _data.PadButtons)
            {
                var down = PadInput.IsDown(def.Source);
                if (!down) _held.Remove(def.Index);
                else if (_held.Add(def.Index))
                {
                    Commit(def.Index);
                    return;
                }
            }
        }

        private void Commit(int pad)
        {
            var id = _armedId;
            var button = _armed;
            if (_bindings.SetPad(id, pad))
            {
                _settings.SaveBindings(_data, _bindings);
                if (_padCaps.TryGetValue(id, out var cap)) cap.SetResolved(_bindings.PadName(id));
            }
            Disarm();
            button.Focus();
        }

        public void Dispose()
        {
            Disarm();
            _screen.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
        }
    }
}
