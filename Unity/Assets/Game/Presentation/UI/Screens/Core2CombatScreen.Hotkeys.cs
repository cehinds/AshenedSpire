using System.Linq;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The fight's hotkeys (web screens/combat.js keyHandler, 2834-2985; P-7c, D-1750..): the Menu key opens the Menu overlay on Settings; Deck, Equipment and Stats open the Armoury where Core lets the deck and the equipment
    /// change (outside combat only: D15), and do nothing during a fight; End Turn, the three flask keys and the card keys act on the player's turn only; every key is swallowed while a veil (a pile, a prompt, the boss
    /// intro) stands over the board. Escape is Back, which the Navigator routes: it closes or cancels what is open and does nothing with nothing open (web keyHandler 2876-2903; D-1770).
    /// </summary>
    public sealed partial class Core2CombatScreen
    {
        private static readonly string[] FlaskActions = { HotkeyActions.Flask1, HotkeyActions.Flask2, HotkeyActions.Flask3 };

        private Tooltip _menuTip;
        private System.Action _keyChanged;

        /// <summary>End Turn wears its bound key and follows a rebind or a pad arriving (web refreshHintBars: the .et-key).</summary>
        private void BindHotkeyLabels()
        {
            var hotkeys = Context.Host.Hotkeys;
            if (hotkeys == null) return;
            _keyChanged = () => _footer.SetKey(HintBarView.Label(Ui.Data, hotkeys.Bindings, HotkeyActions.EndTurn, hotkeys.PadConnected));
            hotkeys.Changed += _keyChanged;
            _keyChanged();
        }

        private void UnbindHotkeyLabels()
        {
            if (_keyChanged != null && Context.Host.Hotkeys != null) Context.Host.Hotkeys.Changed -= _keyChanged;
            _keyChanged = null;
        }

        /// <summary>A pile, the details, a prompt or the boss intro stands over the board (web veilIsOpen): every hotkey waits.</summary>
        public override bool VeilOpen => (_overlays != null && _overlays.IsOpen) || _introOpen || ShowingPrompt;

        public override bool HandleHotkey(HotkeyResult result)
        {
            if (_flow == null) return false;
            switch (result.Kind)
            {
                case HotkeyKind.Card: return CardKey(result);
                case HotkeyKind.Action: return ActionKey(result.Id);
                default: return false;
            }
        }

        /// <summary>The menu button: the quick list, or Settings straight away when the quick menu is off (web combat.js menuBtn).</summary>
        private void PressMenuButton(VisualElement menu)
        {
            if (QuickNav.Direct(MenuDoor.Mode(_run))) OpenMenuOverlay();
            else OpenQuickMenu(menu);
        }

        private void OpenMenuOverlay()
        {
            if (_busy) Skip();
            MenuDoor.Open(Context);
        }

        private bool ActionKey(string id)
        {
            switch (id)
            {
                case HotkeyActions.Menu:
                    OpenMenuOverlay();
                    return true;
                case HotkeyActions.Deck: return ArmouryKey(ArmouryValues.TabDeck);
                case HotkeyActions.Relics: return ArmouryKey(ArmouryValues.TabEquipment);
                case HotkeyActions.Stats: return ArmouryKey(ArmouryValues.TabStats);
                case HotkeyActions.Inspect: return InspectKey();
            }
            // web: if (busy || combat.result || combat.phase !== 'player') return;
            if (_busy || _flow.Ended) return true;
            switch (id)
            {
                case HotkeyActions.EndTurn:
                    OnEndTurn();
                    return true;
                case HotkeyActions.Flask1:
                case HotkeyActions.Flask2:
                case HotkeyActions.Flask3:
                    return FlaskKey(System.Array.IndexOf(FlaskActions, id));
                default: return false;
            }
        }

        /// <summary>
        /// Inspect (web combat.js:2850-2860): the full read of whatever the player is looking at, the living enemy they selected, else themselves. It sits ahead of the busy, result and phase test, so it answers during an enemy
        /// turn and after the fight too; only a veil, a modifier or a text field stops it (all three are the dispatcher's).
        /// </summary>
        private bool InspectKey()
        {
            var living = View.Enemies.Where(e => e.Alive).Select(e => e.Id);
            var subject = InspectSubject.Subject(_targets?.SelectedId, living);
            var fighter = subject != null && _enemies.TryGetValue(subject, out var enemy) ? enemy : _player;
            if (fighter?.Data == null) return true;
            var focused = Root.panel?.focusController?.focusedElement as VisualElement;
            _targets?.Hide();
            _overlays.OpenDetail(fighter.Data, focused ?? fighter);
            return true;
        }

        /// <summary>D15: Core changes equipment and the deck outside combat only, so the door stays shut until the fight is resolved (web openCombatArmoury: if (editLocked()) return).</summary>
        private bool ArmouryKey(string tab)
        {
            if (_busy || !_flow.Ended) return true;
            ArmouryDoor.Open(Context, _run, tab);
            return true;
        }

        /// <summary>A flask key opens the flask panel on that flask; it never drinks (web: Flask keys open the corresponding potion action menu).</summary>
        private bool FlaskKey(int slot)
        {
            Handle(_flow.OpenFlasks(), _footer.PotionsButton);
            if (slot < _options.Count) FocusSoon(_options[slot]);
            return true;
        }

        /// <summary>1 to 9 and Q (web cardHotkeyAction): a slot of the hand, or while a card or flask is armed the Nth living enemy.</summary>
        private bool CardKey(HotkeyResult key)
        {
            if (_busy || _flow.Ended) return true;
            var living = View.Enemies.Where(e => e.Alive).ToList();
            var hotkey = CardHotkeys.Resolve(key.Index, key.Target, _flow.Targeting, _hand.Cards.Count, living.Count);
            switch (hotkey.Kind)
            {
                case CardKeyKind.Target:
                    if (_enemies.TryGetValue(living[hotkey.Index].Id, out var fighter)) OnFighter(fighter);
                    break;
                case CardKeyKind.Select:
                    OnCard(hotkey.Index, _hand.Cards[hotkey.Index]);
                    break;
            }
            return true;
        }
    }
}
