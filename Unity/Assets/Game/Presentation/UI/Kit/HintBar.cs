using System;
using System.Collections.Generic;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The hint bar (web components/hints.js): one chip per action of a context, each a keycap and the action's short words, derived from the live bindings and the device in use (the pad's glyph while a pad is connected, else the key)
    /// and re-derived when a binding changes or a pad arrives or leaves (refreshHintBars). Its tooltip is "Deck (D)". A press on a chip is the same door as the key and the pad (<see cref="HotkeyHost.Perform"/>: a thumb arriving
    /// at a binding the keyboard and pad already reach, refused while a veil stands). The web presses on pointer-down; a Submit on a focused chip is a click.
    /// </summary>
    public sealed class HintBar : IDisposable
    {
        private readonly UiContext _ui;
        private readonly HotkeyHost _hotkeys;
        private readonly string _context;
        private readonly Dictionary<string, LocButton> _chips;
        private bool _pressedByPointer;

        public HintBar(UiContext ui, HotkeyHost hotkeys, string context, IReadOnlyDictionary<string, LocButton> chips)
        {
            _ui = ui;
            _hotkeys = hotkeys;
            _context = context;
            _chips = new Dictionary<string, LocButton>(StringComparer.Ordinal);
            foreach (var chip in chips) _chips[chip.Key] = chip.Value;
            foreach (var pair in _chips)
            {
                var id = pair.Key;
                pair.Value.RegisterCallback<PointerDownEvent>(e =>
                {
                    _pressedByPointer = true;
                    _hotkeys.Perform(id);
                }, TrickleDown.TrickleDown);
                pair.Value.clicked += () =>
                {
                    if (_pressedByPointer) _pressedByPointer = false;
                    else _hotkeys.Perform(id);
                };
            }
            _hotkeys.Changed += Refresh;
            Refresh();
        }

        public IReadOnlyDictionary<string, LocButton> Chips => _chips;

        /// <summary>Says each chip's key, words and tooltip as the bindings and the device say them now.</summary>
        public void Refresh()
        {
            foreach (var chip in HintBarView.Build(_ui.Data, _hotkeys.Bindings, _context, _hotkeys.PadConnected))
            {
                if (!_chips.TryGetValue(chip.Id, out var button)) continue;
                button.Q<LocLabel>(className: HotkeyClasses.HintChipKey)?.SetResolved(chip.Key);
                button.Q<LocLabel>(className: HotkeyClasses.HintChipLabel)?.SetResolved(chip.Short);
                button.tooltip = chip.Title;
            }
        }

        public void Dispose() => _hotkeys.Changed -= Refresh;
    }
}
