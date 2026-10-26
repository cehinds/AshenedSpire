using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// What the three doors of the quick menu share (the combat menu button's list, the pause door the other run screens open, and the Menu overlay's Go to... list): filling the Kit list from the web's rows
    /// (<see cref="QuickMenuView"/>), their tooltips, what a refused row says, and which rows leave the list open (web quicknav.js: a switch, Save and a tab row act in place).
    /// </summary>
    internal static class QuickMenuRows
    {
        /// <summary>The player's settings for a climb (the one the run holds), or the profile's own with no run.</summary>
        public static SettingsSession SettingsFor(UiContext ui, IClimb climb) =>
            climb?.PlayerSettings ?? new SettingsSession(ui.RunContent, ProfileStore.Load(ui.Saves), core2: true);

        /// <summary>Builds the rows for the run as it stands, fills the list and gives them their tooltips. <paramref name="currentTab"/> marks the overlay's open tab.</summary>
        public static IReadOnlyList<QuickMenuRow> Fill(ScreenContext context, QuickMenu menu, IClimb climb, string currentTab = null)
        {
            var ui = context.Ui;
            var strings = ui.Data.Strings;
            var rows = QuickMenuView.Build(ui.Data, ui.Data.MenuContext(true, climb?.IsInCombat ?? false), SettingsFor(ui, climb), currentTab);
            var data = rows.Select(r => new QuickRowData
            {
                Id = r.Def.Id,
                Label = r.Label,
                Note = r.Enabled || r.Locked ? null : strings.Get(StringKeys.PauseLater),
                Enabled = r.Enabled,
                Danger = r.Danger,
                Band = r.Band,
                Tip = r.Tip,
                Current = r.Current,
                Switch = r.Switch,
                Checked = r.Checked,
                Condition = r.Condition,
                State = r.State,
            }).ToList();
            menu.SetRows(data);
            menu.AttachTips(data, context.Overlay, ui.Data.Components.TooltipPlain, ui.Data.Tokens.Duration(TokenKeys.TooltipDelay));
            return rows;
        }

        /// <summary>A disabled row says why where it was pressed: the web's lock in a fight, or that its screen is a later build.</summary>
        public static void Refuse(ScreenContext context, IReadOnlyList<QuickMenuRow> rows, string id, VisualElement anchor)
        {
            var row = rows?.FirstOrDefault(r => r.Def.Id == id);
            var key = row != null && row.Locked ? StringKeys.PauseLocked : StringKeys.PauseLater;
            Ashen.Presentation.UI.Kit.Refusal.Show(anchor, context.Overlay, context.Ui.Data.Strings.Get(key), context.Ui.Data.Tokens.Duration(TokenKeys.Refusal));
        }

        /// <summary>The rows that act in place and leave the list open: a switch, Save (it says Saved · Slot n on its own row) and, in the overlay, a tab (web quicknav.js onActivate 'keep').</summary>
        public static bool KeepsOpen(MenuEntryDef def, bool inOverlay) =>
            def.Action == MenuActions.Toggle || def.Action == MenuActions.Save || (inOverlay && def.Action == MenuActions.Open && def.Tab != null);
    }
}
