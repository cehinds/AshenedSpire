using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Run;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The deck editor's share of the input layer (web screens/deckEditor.js dispatch, 1030-1075, and its tab ring, 1104; P-7c, D-1750.., P-7d, D-1770..). While the editor stands: the Menu key (and pad Start) is Done; the Deck key
    /// (and pad Y) cycles the filters (<see cref="DeckEditor.NextFilterPreset"/>); the End Turn key (and pad X) picks up the row the focus is on, or puts it down, under Play in deck order, and while a row is held the arrows
    /// (the D-pad) move it, and Enter and pad A put it down; LB and RB (and <c>[</c> <c>]</c>) switch the pane. A confirmation or dialog over it owns the keys.
    /// </summary>
    public sealed partial class ArmouryScreen
    {
        public override bool VeilOpen => DialogOpen;

        public override bool HandleHotkey(HotkeyResult result)
        {
            if (result.Kind != HotkeyKind.Action || _editor == null) return false;
            switch (result.Id)
            {
                case HotkeyActions.Menu:
                    DonePressed();
                    return true;
                case HotkeyActions.Deck:
                    _view = DeckEditor.NextFilterPreset(_editor.Model(_view), _view);
                    Render();
                    return true;
                case HotkeyActions.EndTurn:
                    var row = FocusedRow();
                    if (!_editor.EndTurnKey(row, _view)) return false;
                    if (_editor.Model(_view).Ordered) _focusKey = PickKey(row);
                    Render();
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ the held row

        /// <summary>The focus key of a row's pick control (web <c>row-pick:id</c>): focus lands on it when the row is picked up or put down.</summary>
        private static string PickKey(string instanceId) => instanceId + UiFormats.TagSeparator + ArmouryValues.StepPick;

        /// <summary>web focusedRow(): the deck row the focus is in, or null (a collection tile, a chip, a button).</summary>
        private string FocusedRow()
        {
            for (var at = Context.Instance.Focus.Focused; at != null; at = at.parent)
                if (at.ClassListContains(ArmouryClasses.Row) && at.userData is string id) return id;
            return null;
        }

        private void TogglePick(string instanceId)
        {
            _editor.TogglePick(instanceId);
            _focusKey = PickKey(instanceId);
            Render();
        }

        /// <summary>The input gate while a row is held (web setInputGate, heldInput): up and down move the row instead of the focus.</summary>
        public override bool HandleMove(int delta)
        {
            if (_editor?.Held == null || DialogOpen) return false;
            _editor.MoveHeld(delta);
            _focusKey = PickKey(_editor.Held);
            Render();
            return true;
        }

        /// <summary>Enter and pad A put the held row down, whatever the focus is on (web heldInput: the drop).</summary>
        public override bool HandleSubmit()
        {
            if (_editor?.Held == null || DialogOpen) return false;
            _focusKey = PickKey(_editor.Held);
            _editor.Drop();
            Render();
            return true;
        }

        // ------------------------------------------------------------------ the pane ring

        public override bool HasTabRing => _editor != null && !DialogOpen;

        /// <summary>
        /// LB and RB, <c>[</c> and <c>]</c> (web switchPane): the other pane, the first card of it in focus (and shown in the inspector, as focus does there). On a narrow layout the deck pane is
        /// out of reach unless the deck is ordered, so the ring does nothing for an unordered deck there.
        /// </summary>
        public override void StepTabs(int delta)
        {
            if (_editor == null) return;
            var narrow = Nav.Layout != null && Nav.Layout.Narrow;
            if (!DeckEditor.CanSwitchPane(_editor.Model(_view).Ordered, narrow)) return;
            _pane = DeckEditor.SwitchPane(_pane, delta);
            var host = _pane == DeckEditor.Panes[1] ? _deckPane : _libraryPane;
            var first = host?.Query<LocButton>(className: ArmouryClasses.RowName).First();
            if (first == null || !(first.userData is string key)) return;
            _selected = key;
            _focusKey = key;
            Render();
        }

        // ------------------------------------------------------------------ the key line

        private string KeysLine()
        {
            var hotkeys = Context.Host.Hotkeys;
            var bindings = hotkeys?.Bindings ?? new ControlBindings(Ui.Data.Controls);
            var held = _editor?.Held == null ? null : _editor.Model(_view).Deck.FirstOrDefault(r => r.InstanceId == _editor.Held)?.Name ?? string.Empty;
            return HintBarView.DeckEditorLine(Ui.Data, bindings, hotkeys?.PadConnected == true, held);
        }

        /// <summary>The line names the keys as bound: it follows a rebind and a pad arriving or leaving.</summary>
        private void BindKeysLine()
        {
            var hotkeys = Context.Host.Hotkeys;
            if (hotkeys == null) return;
            _keysChanged = () => _keysLine?.SetResolved(KeysLine());
            hotkeys.Changed += _keysChanged;
        }

        private void UnbindKeysLine()
        {
            if (_keysChanged != null && Context.Host.Hotkeys != null) Context.Host.Hotkeys.Changed -= _keysChanged;
            _keysChanged = null;
        }
    }
}
