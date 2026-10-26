using System.Collections.Generic;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The map's hotkeys (web screens/map.js mapKeys, 455-490; P-7c, D-1750..): the Menu key opens the Menu overlay on Settings, Deck, Equipment and Stats open the Armoury, + and = zoom in, - and _ zoom out, 0 resets the framing.
    /// A planned door is a veil: every key waits. The key bar below the board is the web's hint bar (components/hints.js): four chips whose keycaps follow the player's bindings and the pad.
    /// </summary>
    public sealed partial class ActMapScreen
    {
        private HintBar _hints;
        private Tooltip _menuTip;

        public HintBar Hints => _hints;

        public override bool VeilOpen => _door != null;

        /// <summary>The key bar: Deck, Equipment, Stats and Menu are chips whose keycap is the live binding (the letters, or the pad's glyphs).</summary>
        private void BindHints()
        {
            var hotkeys = Context.Host.Hotkeys;
            if (hotkeys == null) return;
            _hints = new HintBar(Ui, hotkeys, HotkeyValues.HintMap, new Dictionary<string, LocButton>
            {
                [HotkeyActions.Deck] = Root.Q<LocButton>(UiNames.MapKeyDeck),
                [HotkeyActions.Relics] = Root.Q<LocButton>(UiNames.MapKeyEquipment),
                [HotkeyActions.Stats] = Root.Q<LocButton>(UiNames.MapKeyStats),
                [HotkeyActions.Menu] = Root.Q<LocButton>(UiNames.MapKeyMenu),
            });
        }

        public override bool HandleHotkey(HotkeyResult result)
        {
            if (_climb == null) return false;
            if (result.Kind == HotkeyKind.Command)
            {
                switch (result.Id)
                {
                    case HotkeyValues.CommandZoomIn: Zoom(1); return true;
                    case HotkeyValues.CommandZoomOut: Zoom(-1); return true;
                    case HotkeyValues.CommandZoomReset: CentreOnRun(); return true;
                }
                return false;
            }
            switch (result.Id)
            {
                case HotkeyActions.Menu: MenuDoor.Open(Context); return true;
                case HotkeyActions.Deck: OpenArmoury(ArmouryValues.TabDeck, Root.Q(UiNames.MapKeyDeck)); return true;
                case HotkeyActions.Relics: OpenArmoury(ArmouryValues.TabEquipment, Root.Q(UiNames.MapKeyEquipment)); return true;
                case HotkeyActions.Stats: OpenArmoury(ArmouryValues.TabStats, Root.Q(UiNames.MapKeyStats)); return true;
                default: return false;
            }
        }

        /// <summary>The menu button: the quick list, or Settings straight away when the quick menu is off (web runHud.js menuBtn).</summary>
        private void PressMenuButton()
        {
            if (QuickNav.Direct(MenuDoor.Mode(_climb))) MenuDoor.Open(Context);
            else OpenPause();
        }
    }
}
