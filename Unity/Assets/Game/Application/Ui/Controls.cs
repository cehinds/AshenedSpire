using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>An action the Controls list rebinds (ui/controls.json 'actions'; the web's ACTIONS registry, src/ui/input.js:100-132).</summary>
    public sealed class ControlActionDef
    {
        public string Id;
        public string LabelKey;
        public string Kind;

        /// <summary>A key the player cannot rebind (Confirm is Enter, Cancel is Escape: cursor-activate and overlay-close always work), or null.</summary>
        public string FixedKey;

        /// <summary>How a fixed key reads in the list (Enter, Esc).</summary>
        public string KeyHint;

        public string DefKey;
        public int DefButton;

        /// <summary>The web rebinds a key only for an action that has a default key (controls.js rebindableKey).</summary>
        public bool KeyRebindable => DefKey != null;
    }

    /// <summary>A pad button's names (web uiContent padButtons): the compact glyph, the readable name, and the Input System control it is.</summary>
    public sealed class PadButtonDef
    {
        public int Index;
        public string Glyph;
        public string Name;
        public string Source;
    }

    /// <summary>ui/controls.json: the actions, the pad buttons and the key names.</summary>
    public sealed class ControlsData
    {
        private readonly Dictionary<int, PadButtonDef> _pads = new Dictionary<int, PadButtonDef>();

        public IReadOnlyList<ControlActionDef> Actions { get; private set; } = Array.Empty<ControlActionDef>();
        public IReadOnlyCollection<PadButtonDef> PadButtons => _pads.Values;
        public string PadGlyphFallback { get; private set; }
        public string PadNameFallback { get; private set; }
        public string Unbound { get; private set; }
        public string CancelKey { get; private set; }
        public string StoredKeyBindings { get; private set; }
        public string StoredPadBindings { get; private set; }
        public IReadOnlyDictionary<string, string> KeyLabels { get; private set; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> KeyNames { get; private set; } = new Dictionary<string, string>();
        public IReadOnlyCollection<string> ModifierKeys { get; private set; } = Array.Empty<string>();

        public ControlActionDef Find(string id) => Actions.FirstOrDefault(a => a.Id == id);

        public PadButtonDef Pad(int index) => _pads.TryGetValue(index, out var def) ? def : null;

        public static ControlsData From(JObject json)
        {
            var data = new ControlsData
            {
                Actions = (json[ControlsKeys.Actions] as JArray ?? new JArray()).OfType<JObject>().Select(a => new ControlActionDef
                {
                    Id = (string)a[ControlsKeys.Id],
                    LabelKey = (string)a[ControlsKeys.LabelKey],
                    Kind = (string)a[ControlsKeys.Kind],
                    FixedKey = (string)a[ControlsKeys.FixedKey],
                    KeyHint = (string)a[ControlsKeys.KeyHint],
                    DefKey = (string)a[ControlsKeys.DefKey],
                    DefButton = (int)a[ControlsKeys.DefButton],
                }).ToList(),
                PadGlyphFallback = (string)json[ControlsKeys.PadGlyphFallback],
                PadNameFallback = (string)json[ControlsKeys.PadNameFallback],
                Unbound = (string)json[ControlsKeys.Unbound],
                CancelKey = (string)json[ControlsKeys.CancelKey],
                StoredKeyBindings = (string)json[ControlsKeys.StoredKeyBindings],
                StoredPadBindings = (string)json[ControlsKeys.StoredPadBindings],
                KeyLabels = Map(json[ControlsKeys.KeyLabels]),
                KeyNames = Map(json[ControlsKeys.KeyNames]),
                ModifierKeys = (json[ControlsKeys.ModifierKeys] as JArray ?? new JArray()).Select(t => (string)t).ToList(),
            };
            foreach (var p in (json[ControlsKeys.PadButtons] as JObject ?? new JObject()).Properties())
            {
                var index = int.Parse(p.Name, CultureInfo.InvariantCulture);
                data._pads[index] = new PadButtonDef
                {
                    Index = index,
                    Glyph = (string)p.Value[ControlsKeys.Glyph],
                    Name = (string)p.Value[ControlsKeys.Name],
                    Source = (string)p.Value[ControlsKeys.Source],
                };
            }
            return data;
        }

        private static IReadOnlyDictionary<string, string> Map(JToken token) =>
            (token as JObject ?? new JObject()).Properties().ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.Ordinal);

        /// <summary>The readable name of a pad button for the Controls list (web padName): the unbound dash for none, "Btn n" for a button with no name.</summary>
        public string PadName(int? button)
        {
            if (button == null) return Unbound;
            var def = Pad(button.Value);
            return def != null ? def.Name : StringTable.Fill(PadNameFallback, new StringArgs().Add(ControlsPlaceholders.Button, button.Value));
        }

        /// <summary>The compact glyph of a pad button for the hint bar (web padGlyph): "B n" for a button with none.</summary>
        public string PadGlyph(int button)
        {
            var def = Pad(button);
            return def != null ? def.Glyph : StringTable.Fill(PadGlyphFallback, new StringArgs().Add(ControlsPlaceholders.Button, button));
        }

        /// <summary>
        /// The web's key name for a key the player pressed while a rebind is armed (the keydown's <c>key</c>): a printable key is its character, a named key is
        /// its name from <see cref="KeyNames"/>, and a lone modifier is no key at all (null: web onKeydown ignores Shift, Control, Alt and Meta).
        /// </summary>
        public string KeyName(string engineKey, char character)
        {
            if (engineKey != null && ModifierKeys.Contains(engineKey)) return null;
            if (!char.IsControl(character)) return character.ToString();
            if (engineKey != null && KeyNames.TryGetValue(engineKey, out var name)) return name;
            return engineKey;
        }
    }

    /// <summary>
    /// The player's key and pad bindings (web input.js: bindings, keyBindings, setBindings, setKeyBindings, keyLabel, matchAction): every action's pad button and every rebindable action's key, the defaults overlaid
    /// with what the profile stores. Any key may be bound to any number of actions: the web does not refuse a clash. Engine-free.
    /// </summary>
    public sealed class ControlBindings
    {
        private readonly ControlsData _data;
        private readonly Dictionary<string, string> _keys = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pads = new Dictionary<string, int>(StringComparer.Ordinal);

        public ControlBindings(ControlsData data, JObject storedKeys = null, JObject storedPads = null)
        {
            _data = data;
            foreach (var action in data.Actions)
            {
                if (action.DefKey != null) _keys[action.Id] = action.DefKey;
                _pads[action.Id] = action.DefButton;
            }
            if (storedKeys != null)
                foreach (var p in storedKeys.Properties())
                    if (p.Value.Type == JTokenType.String) _keys[p.Name] = (string)p.Value;
            if (storedPads != null)
                foreach (var p in storedPads.Properties())
                    if (p.Value.Type == JTokenType.Integer) _pads[p.Name] = (int)p.Value;
        }

        /// <summary>The key bound to an action (its web key value), or null when it has none.</summary>
        public string Key(string id) => _keys.TryGetValue(id, out var key) ? key : null;

        /// <summary>The pad button bound to an action, or null.</summary>
        public int? Pad(string id) => _pads.TryGetValue(id, out var button) ? button : (int?)null;

        /// <summary>How an action's key reads (web keyLabel): the key's name, a single character upper-cased, a fixed key's hint, or the unbound dash.</summary>
        public string KeyLabel(string id)
        {
            var key = Key(id);
            if (string.IsNullOrEmpty(key))
            {
                var action = _data.Find(id);
                var fixedText = action?.KeyHint ?? action?.FixedKey;
                return string.IsNullOrEmpty(fixedText) ? _data.Unbound : fixedText;
            }
            if (_data.KeyLabels.TryGetValue(key, out var label)) return label;
            return key.Length == 1 ? key.ToUpperInvariant() : key;
        }

        public string PadName(string id) => _data.PadName(Pad(id));

        /// <summary>Binds a key to an action. False (nothing changes) for an action whose key is fixed or unknown (the web's list has no Key control for it).</summary>
        public bool SetKey(string id, string key)
        {
            var action = _data.Find(id);
            if (action == null || !action.KeyRebindable || string.IsNullOrEmpty(key)) return false;
            _keys[id] = key;
            return true;
        }

        /// <summary>Binds a pad button to an action. False for an unknown action.</summary>
        public bool SetPad(string id, int button)
        {
            if (_data.Find(id) == null) return false;
            _pads[id] = button;
            return true;
        }

        /// <summary>True if a key press is the key currently bound to an action (web matchAction: compared without case).</summary>
        public bool Matches(string id, string key)
        {
            var bound = Key(id);
            return !string.IsNullOrEmpty(bound) && string.Equals(bound, key ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The action a pad button is bound to (web actionForButton): the first in the registry's order.</summary>
        public string ActionForButton(int button) => _data.Actions.FirstOrDefault(a => Pad(a.Id) == button)?.Id;

        /// <summary>The whole key map as the profile stores it (the web writes the whole map after a change: <c>{ ...getKeyBindings(), [id]: key }</c>).</summary>
        public JObject KeysJson() => new JObject(_data.Actions.Where(a => _keys.ContainsKey(a.Id)).Select(a => new JProperty(a.Id, _keys[a.Id])));

        public JObject PadsJson() => new JObject(_data.Actions.Where(a => _pads.ContainsKey(a.Id)).Select(a => new JProperty(a.Id, _pads[a.Id])));
    }

    /// <summary>One row of the Controls list: the action's words and its two bindings (web controls.js, Row·setting with a LabelStack and Keycaps).</summary>
    public sealed class ControlRowView
    {
        public string Id;
        public string Label;
        public string KeyText;
        public string PadText;

        /// <summary>The row has a Key rebind button (web rebindableKey); a fixed key shows its keycap alone.</summary>
        public bool KeyRebindable;
    }

    /// <summary>The Controls pane (web renderControls): the eyebrow, the title, the two lines of help, the pad state and one row per action.</summary>
    public sealed class ControlsView
    {
        public string Eyebrow;
        public string Title;
        public string Intro;
        public bool PadConnected;
        public string PadState;
        public string PadNote;
        public string KeyButton;
        public string PadButton;
        public string Listening;
        public readonly List<ControlRowView> Rows = new List<ControlRowView>();

        public static ControlsView Build(UiData data, ControlBindings bindings, bool padConnected)
        {
            var strings = data.Strings;
            var view = new ControlsView
            {
                Eyebrow = strings.Get(StringKeys.ControlsEyebrow),
                Title = strings.Get(StringKeys.ControlsTitle),
                Intro = strings.Get(StringKeys.ControlsIntro),
                PadConnected = padConnected,
                PadState = strings.Get(padConnected ? StringKeys.ControlsPadLive : StringKeys.ControlsPadOff),
                PadNote = strings.Get(StringKeys.ControlsPadNote),
                KeyButton = strings.Get(StringKeys.ControlsRebindKey),
                PadButton = strings.Get(StringKeys.ControlsRebindPad),
                Listening = strings.Get(StringKeys.ControlsRebindListening),
            };
            foreach (var action in data.Controls.Actions)
                view.Rows.Add(new ControlRowView
                {
                    Id = action.Id,
                    Label = strings.Get(action.LabelKey),
                    KeyText = action.KeyRebindable ? bindings.KeyLabel(action.Id) : action.KeyHint ?? data.Controls.Unbound,
                    PadText = bindings.PadName(action.Id),
                    KeyRebindable = action.KeyRebindable,
                });
            return view;
        }
    }
}
