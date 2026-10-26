using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>What one screen answers to (ui/controls.json hotkeys.scopes; the web's per-screen key handlers, screens/combat.js:2834-2985 and screens/map.js:455-490).</summary>
    public sealed class HotkeyScope
    {
        public string Screen;

        /// <summary>The registry ids the screen handles, in the order its handler asks them.</summary>
        public IReadOnlyList<string> Actions = Array.Empty<string>();

        /// <summary>False: Ctrl, Alt and Meta swallow the key (the fight does; the map does not).</summary>
        public bool Modifiers;

        /// <summary>The positional card keys are live.</summary>
        public bool Cards;

        /// <summary>Fixed keys that run a screen command (the map's zoom keys), exact match.</summary>
        public IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>();
    }

    /// <summary>A positional card key (web cardHotkeyAction): <c>1</c> to <c>9</c> name hand slots 1 to 9 (and the Nth living enemy while aiming), <c>Q</c> names slot 10 and never aims.</summary>
    public sealed class CardKeyDef
    {
        public string Key;
        public int Index;
        public bool Target;
    }

    /// <summary>ui/controls.json hotkeys: the tab ring's keys and buttons, the d-pad's directions, the card keys, the screen scopes and the hint bars.</summary>
    public sealed class HotkeyData
    {
        public string RingPrevKey;
        public string RingNextKey;
        public int RingPrevButton;
        public int RingNextButton;
        public IReadOnlyDictionary<int, string> PadNav = new Dictionary<int, string>();
        public IReadOnlyList<CardKeyDef> CardKeys = Array.Empty<CardKeyDef>();
        public IReadOnlyDictionary<string, HotkeyScope> Scopes = new Dictionary<string, HotkeyScope>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<string>> HintBars = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> HintLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        public string MenuTipTitle;
        public string MenuTipList;
        public string MenuTipDirect;

        public static HotkeyData From(JObject json)
        {
            var data = new HotkeyData();
            if (json == null) return data;
            if (json[HotkeyKeys.Ring] is JObject ring)
            {
                data.RingPrevKey = (string)ring[HotkeyKeys.PrevKey];
                data.RingNextKey = (string)ring[HotkeyKeys.NextKey];
                data.RingPrevButton = (int)ring[HotkeyKeys.PrevButton];
                data.RingNextButton = (int)ring[HotkeyKeys.NextButton];
            }
            data.PadNav = (json[HotkeyKeys.PadNav] as JObject ?? new JObject()).Properties()
                .ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => (string)p.Value);
            data.CardKeys = (json[HotkeyKeys.CardKeys] as JArray ?? new JArray()).OfType<JObject>()
                .Select(k => new CardKeyDef { Key = (string)k[HotkeyKeys.Key], Index = (int)k[HotkeyKeys.Index], Target = (bool)k[HotkeyKeys.Target] }).ToList();
            var scopes = new Dictionary<string, HotkeyScope>(StringComparer.Ordinal);
            foreach (var p in (json[HotkeyKeys.Scopes] as JObject ?? new JObject()).Properties())
            {
                var s = (JObject)p.Value;
                scopes[p.Name] = new HotkeyScope
                {
                    Screen = p.Name,
                    Actions = (s[ControlsKeys.Actions] as JArray ?? new JArray()).Select(t => (string)t).ToList(),
                    Modifiers = (bool?)s[HotkeyKeys.Modifiers] ?? false,
                    Cards = (bool?)s[HotkeyKeys.Cards] ?? false,
                    Keys = (s[HotkeyKeys.Keys] as JObject ?? new JObject()).Properties().ToDictionary(k => k.Name, k => (string)k.Value, StringComparer.Ordinal),
                };
            }
            data.Scopes = scopes;
            data.HintBars = (json[HotkeyKeys.HintBars] as JObject ?? new JObject()).Properties()
                .ToDictionary(p => p.Name, p => (IReadOnlyList<string>)((JArray)p.Value).Select(t => (string)t).ToList(), StringComparer.Ordinal);
            data.HintLabels = (json[HotkeyKeys.HintLabels] as JObject ?? new JObject()).Properties().ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.Ordinal);
            if (json[HotkeyKeys.MenuTip] is JObject tip)
            {
                data.MenuTipTitle = (string)tip[HotkeyKeys.Title];
                data.MenuTipList = (string)tip[HotkeyKeys.List];
                data.MenuTipDirect = (string)tip[HotkeyKeys.Direct];
            }
            return data;
        }

        public CardKeyDef CardKey(string key) =>
            string.IsNullOrEmpty(key) ? null : CardKeys.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What a key or button turned into.</summary>
    public enum HotkeyKind
    {
        /// <summary>Nothing: the press is not this layer's, so it goes on to whatever else listens.</summary>
        None,

        /// <summary>A registry action the screen handles (<see cref="HotkeyResult.Id"/>).</summary>
        Action,

        /// <summary>A screen command a fixed key runs (the map's zoom keys).</summary>
        Command,

        /// <summary>A positional card key (<see cref="HotkeyResult.Index"/>, <see cref="HotkeyResult.Target"/>).</summary>
        Card,

        TabPrev,
        TabNext,

        /// <summary>The d-pad moves the focus (<see cref="HotkeyResult.Delta"/>: next 1, previous -1).</summary>
        Move,

        /// <summary>The pad's Confirm button: activate the focused control.</summary>
        Confirm,

        /// <summary>The pad's Cancel button: Escape.</summary>
        Cancel,
    }

    public sealed class HotkeyResult
    {
        public static readonly HotkeyResult Nothing = new HotkeyResult();

        public HotkeyKind Kind;
        public string Id;
        public int Index;
        public bool Target;
        public int Delta;

        /// <summary>The press was this layer's: the caller stops it (web preventDefault, stopPropagation).</summary>
        public bool Consumed => Kind != HotkeyKind.None;
    }

    /// <summary>One key press as the web's keydown carries it (<c>key</c>, the modifiers, <c>repeat</c>).</summary>
    public sealed class KeyPress
    {
        public string Key;
        public bool Ctrl;
        public bool Alt;
        public bool Meta;
        public bool Repeat;
    }

    /// <summary>Where the press lands: the top screen, and what stands over it.</summary>
    public sealed class HotkeyState
    {
        /// <summary>The top screen's id (ui/screens.json): its hotkey scope.</summary>
        public string Screen;

        /// <summary>A text field has the focus: typing is not a hotkey.</summary>
        public bool Typing;

        /// <summary>The screen takes no input (the title gate), or a Rebind is armed and the next key or button is the binding.</summary>
        public bool Blocked;

        /// <summary>A veil stands over the screen (a pile, a prompt, a planned door): web veilIsOpen. Any veil owns input while it stands.</summary>
        public bool Veil;

        /// <summary>The top screen holds the bumpers for its tab set (web setTabRing).</summary>
        public bool TabRing;
    }

    /// <summary>
    /// The web's input layer as one engine-free decision (src/ui/input.js onKeydown and pollPads, and the per-screen handlers they hand a key to): a key or a pad button, the screen and what stands over it, and the player's bindings
    /// in, an action out. Honours rebinding (the live <see cref="ControlBindings"/>), modal precedence (a veil swallows every screen key), text-field suppression, the screen's own scope (which actions and fixed keys it answers to,
    /// and whether Ctrl, Alt and Meta swallow a key), the tab ring taking the bumpers and <c>[</c> <c>]</c> from their global bindings, and the pad's d-pad moving the focus unless an action is bound to it. A pad button is the
    /// web's synthetic keydown of the action's bound key, so it reaches exactly what the keyboard reaches.
    /// </summary>
    public sealed class HotkeyDispatcher
    {
        private readonly ControlsData _data;

        public HotkeyDispatcher(ControlsData data, ControlBindings bindings)
        {
            _data = data;
            Bindings = bindings;
        }

        /// <summary>The live bindings: replaced when the player rebinds.</summary>
        public ControlBindings Bindings { get; set; }

        private HotkeyData Hotkeys => _data.Hotkeys;

        /// <summary>A key press on the keyboard.</summary>
        public HotkeyResult Key(KeyPress press, HotkeyState state)
        {
            if (press == null || state == null || state.Blocked || string.IsNullOrEmpty(press.Key)) return HotkeyResult.Nothing;
            if (state.Typing) return HotkeyResult.Nothing;
            if (state.TabRing)
            {
                if (press.Key == Hotkeys.RingNextKey) return new HotkeyResult { Kind = HotkeyKind.TabNext };
                if (press.Key == Hotkeys.RingPrevKey) return new HotkeyResult { Kind = HotkeyKind.TabPrev };
            }
            return Screen(press, state);
        }

        /// <summary>A pad button that went down (a rising edge).</summary>
        public HotkeyResult Pad(int button, HotkeyState state)
        {
            if (state == null || state.Blocked) return HotkeyResult.Nothing;
            if (state.TabRing)
            {
                if (button == Hotkeys.RingNextButton) return new HotkeyResult { Kind = HotkeyKind.TabNext };
                if (button == Hotkeys.RingPrevButton) return new HotkeyResult { Kind = HotkeyKind.TabPrev };
            }
            var id = Bindings.ActionForButton(button);
            if (id != null) return Perform(id, state);
            if (Hotkeys.PadNav.TryGetValue(button, out var direction))
                return new HotkeyResult { Kind = HotkeyKind.Move, Delta = direction == HotkeyValues.DirUp || direction == HotkeyValues.DirLeft ? -1 : 1 };
            return HotkeyResult.Nothing;
        }

        /// <summary>
        /// An action reached by a pad button or a hint chip (web doAction): Confirm activates the focus, Cancel is Escape, and any other is the synthetic keydown of the key it is bound to, so the screen answers it
        /// exactly as it answers the keyboard (modal precedence and scope included; a text field cannot have the focus on a pad).
        /// </summary>
        public HotkeyResult Perform(string id, HotkeyState state)
        {
            var action = _data.Find(id);
            if (action == null || state == null || state.Blocked) return HotkeyResult.Nothing;
            if (action.Kind == ControlsValues.KindCursor) return new HotkeyResult { Kind = HotkeyKind.Confirm, Id = id };
            if (id == HotkeyActions.Cancel) return new HotkeyResult { Kind = HotkeyKind.Cancel, Id = id };
            var key = Bindings.Key(id) ?? action.DefKey ?? action.FixedKey;
            if (string.IsNullOrEmpty(key)) return HotkeyResult.Nothing;
            return Screen(new KeyPress { Key = key }, state);
        }

        /// <summary>The screen's handler for a key (web: the keydown listener a screen mounts).</summary>
        private HotkeyResult Screen(KeyPress press, HotkeyState state)
        {
            if (!Hotkeys.Scopes.TryGetValue(state.Screen ?? string.Empty, out var scope)) return HotkeyResult.Nothing;
            if (!scope.Modifiers && (press.Ctrl || press.Alt || press.Meta)) return HotkeyResult.Nothing;
            if (state.Veil || press.Repeat) return HotkeyResult.Nothing;
            foreach (var id in scope.Actions)
            {
                if (!Bindings.Matches(id, press.Key)) continue;
                // web actionDestinationForEvent: the three Armoury doors answer only when exactly one of them is on this key.
                var action = _data.Find(id);
                if (action?.Destination != null && DestinationMatches(press.Key) != 1) continue;
                return new HotkeyResult { Kind = HotkeyKind.Action, Id = id };
            }
            if (scope.Keys.TryGetValue(press.Key, out var command)) return new HotkeyResult { Kind = HotkeyKind.Command, Id = command };
            if (scope.Cards && Hotkeys.CardKey(press.Key) is CardKeyDef card)
                return new HotkeyResult { Kind = HotkeyKind.Card, Index = card.Index, Target = card.Target };
            return HotkeyResult.Nothing;
        }

        private int DestinationMatches(string key) => _data.Actions.Count(a => a.Destination != null && Bindings.Matches(a.Id, key));
    }

    /// <summary>What a positional card key does (web cardHotkeyAction).</summary>
    public enum CardKeyKind
    {
        /// <summary>A card key with nothing under it: swallowed, no-op.</summary>
        None,

        /// <summary>Arm or play hand slot <see cref="CardKeyResult.Index"/>.</summary>
        Select,

        /// <summary>Commit the armed card or flask on living enemy <see cref="CardKeyResult.Index"/>.</summary>
        Target,
    }

    public sealed class CardKeyResult
    {
        public CardKeyKind Kind;
        public int Index;
    }

    public static class CardHotkeys
    {
        /// <summary>While a card or flask is armed a digit names the Nth living enemy; Q never aims, so it stays slot 10. Otherwise the key names a hand slot.</summary>
        public static CardKeyResult Resolve(int index, bool digit, bool targeting, int handSize, int livingEnemies)
        {
            if (targeting && digit) return index < livingEnemies ? new CardKeyResult { Kind = CardKeyKind.Target, Index = index } : new CardKeyResult();
            return index < handSize ? new CardKeyResult { Kind = CardKeyKind.Select, Index = index } : new CardKeyResult();
        }
    }

    /// <summary>The tab ring (web setTabRing, Law 3): the set's own order, wrapping at both ends.</summary>
    public static class TabRing
    {
        public static int Step(int count, int current, int delta)
        {
            if (count <= 0) return 0;
            var at = current < 0 ? 0 : current;
            return ((at + delta) % count + count) % count;
        }
    }

    /// <summary>The quick menu setting (web quicknav.js quickNavMode, quickNavFolds): off sends the menu button straight to Settings; mirror adds the destination list to the overlay; switcher folds the overlay's tab strip into one button on a narrow screen.</summary>
    public static class QuickNav
    {
        /// <summary>A fresh or invalid value is mirror.</summary>
        public static string Resolve(string value) =>
            value == HotkeyValues.QuickOff || value == HotkeyValues.QuickMirror || value == HotkeyValues.QuickSwitcher ? value : HotkeyValues.QuickMirror;

        /// <summary>The menu button opens Settings directly (the list is off).</summary>
        public static bool Direct(string value) => Resolve(value) == HotkeyValues.QuickOff;

        /// <summary>The overlay carries the destination list's own button (mirror).</summary>
        public static bool Launcher(string value) => Resolve(value) == HotkeyValues.QuickMirror;

        /// <summary>The overlay's tab strip is one switcher button (switcher, on a narrow layout).</summary>
        public static bool Folds(string value, bool narrow) => Resolve(value) == HotkeyValues.QuickSwitcher && narrow;
    }

    /// <summary>One chip of the hint bar (web components/hints.js chipsHtml).</summary>
    public sealed class HintChip
    {
        public string Id;

        /// <summary>The keycap: the pad's glyph while a pad is connected, else the key's label (web actionLabel).</summary>
        public string Key;

        /// <summary>The short words (web actionShort).</summary>
        public string Short;

        /// <summary>The tooltip: "Deck (D)" (web actionHint).</summary>
        public string Title;
    }

    /// <summary>The hint bar and every other place a binding is named in prose, derived from the live bindings and the device in use (web hints.js, input.js actionLabel and actionHint). Engine-free.</summary>
    public static class HintBarView
    {
        /// <summary>The symbol to put in front of a player right now: the pad's glyph when a pad is connected and the action has a button, else its key.</summary>
        public static string Label(UiData data, ControlBindings bindings, string id, bool pad)
        {
            if (pad && bindings.Pad(id) is int button) return data.Controls.PadGlyph(button);
            return bindings.KeyLabel(id);
        }

        public static string Short(UiData data, string id) =>
            data.Controls.Hotkeys.HintLabels.TryGetValue(id, out var key) ? data.Strings.Get(key) : id;

        /// <summary>"Menu (M)" (web actionHint).</summary>
        public static string Hint(UiData data, ControlBindings bindings, string id, bool pad) =>
            data.Strings.Format(StringKeys.ControlsHintTitle, new StringArgs().Add(UiPlaceholders.Name, Short(data, id)).Add(ControlsPlaceholders.Key, Label(data, bindings, id, pad)));

        /// <summary>The chips of a context ('combat', 'map', 'targeting') in order.</summary>
        public static IReadOnlyList<HintChip> Build(UiData data, ControlBindings bindings, string context, bool pad)
        {
            if (!data.Controls.Hotkeys.HintBars.TryGetValue(context, out var ids)) return Array.Empty<HintChip>();
            return ids.Select(id => new HintChip
            {
                Id = id,
                Key = Label(data, bindings, id, pad),
                Short = Short(data, id),
                Title = Hint(data, bindings, id, pad),
            }).ToList();
        }
    }
}
