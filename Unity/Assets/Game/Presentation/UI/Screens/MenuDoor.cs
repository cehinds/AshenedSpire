using Ashen.App.Run;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The doors to the Menu overlay that the quick menu setting and the Menu key share (web screens/combat.js and map.js onMenu('settings'), components/runHud.js menuBtn, quicknav.js quickNavMode). The key opens the overlay on
    /// Settings whatever the setting says; the menu button opens it directly when the quick menu is off and the quick list otherwise, and its tooltip says which.
    /// </summary>
    internal static class MenuDoor
    {
        /// <summary>The Menu overlay on a tab (Settings by default).</summary>
        public static void Open(ScreenContext context, string tab = null) =>
            context.Navigator.Go(ScreenIds.Menu, new SettingsArgs { Tab = tab ?? MenuValues.TabSettings });

        /// <summary>The player's quick menu setting (web quickNavMode), read from the run's settings.</summary>
        public static string Mode(IClimb climb) => QuickNav.Resolve(climb?.PlayerSettings?.Text(SettingIds.QuickNav));

        /// <summary>The menu button's tooltip (web runHud.js and combat.js: "Menu", and everywhere you can go from here, or armoury, settings, controls and saving when the list is off).</summary>
        public static Tooltip Tip(ScreenContext context, VisualElement button, IClimb climb)
        {
            if (button == null) return null;
            var ui = context.Ui;
            var tip = Tooltip.AttachResolved(button, context.Overlay, ui.Data.Components.TooltipPlain, string.Empty, string.Empty, ui.Data.Tokens.Duration(TokenKeys.TooltipDelay));
            Retip(tip, context, climb);
            return tip;
        }

        /// <summary>The tooltip says what the setting says now (the player may have changed it in the overlay the button opened).</summary>
        public static void Retip(Tooltip tip, ScreenContext context, IClimb climb)
        {
            if (tip == null) return;
            var hotkeys = context.Ui.Data.Controls.Hotkeys;
            var strings = context.Ui.Data.Strings;
            tip.SetText(strings.Get(hotkeys.MenuTipTitle), strings.Get(QuickNav.Direct(Mode(climb)) ? hotkeys.MenuTipDirect : hotkeys.MenuTipList));
        }
    }
}
