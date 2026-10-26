using System.Collections.Generic;
using System.Linq;
using Ashen.App.Settings;
using Ashen.Generated;

namespace Ashen.App.Ui
{
    /// <summary>One row of the quick menu as it is drawn (web quicknav.js menuRows over the quickMenuRows table, menuComponents.js renderQuickMenu).</summary>
    public sealed class QuickMenuRow
    {
        public MenuEntryDef Def;
        public string Label;

        /// <summary>The tooltip's words, or null. A row locked in a fight carries the lock's sentence.</summary>
        public string Tip;

        /// <summary>The first row of a new band: a hairline is drawn above it.</summary>
        public bool Band;

        public bool Enabled;

        /// <summary>Disabled because the fight is on (the web's edit lock), not because its screen is planned.</summary>
        public bool Locked;

        /// <summary>The row reads in the loss colour (Load, Quit Without Saving).</summary>
        public bool Danger;

        /// <summary>The overlay's open tab: its row is marked.</summary>
        public bool Current;

        /// <summary>A switch row: its On or Off state and the sentence that says it (web qn-condition).</summary>
        public bool Switch;

        public bool Checked;
        public string Condition;
        public string State;
    }

    /// <summary>
    /// The quick menu's rows (ui/menus.json pause, the web's quickMenuRows) for a run: their words and tips, the bands, which row is the overlay's open tab, and each switch's state and sentence. Engine-free.
    /// </summary>
    public static class QuickMenuView
    {
        /// <param name="settings">The player's settings: the source of a switch row's state. Null reads every switch as off.</param>
        /// <param name="currentTab">The Menu overlay's open tab (its row is marked), or null outside the overlay.</param>
        public static IReadOnlyList<QuickMenuRow> Build(UiData data, MenuContext context, SettingsSession settings, string currentTab = null)
        {
            var strings = data.Strings;
            var rows = new List<QuickMenuRow>();
            string previousBand = null;
            foreach (var state in MenuRules.Evaluate(data.Menus.Pause, context).Where(s => s.Visible))
            {
                var def = state.Def;
                var row = new QuickMenuRow
                {
                    Def = def,
                    Label = strings.Get(def.LabelKey),
                    Tip = state.Locked ? strings.Get(StringKeys.PauseLocked) : def.TipKey == null ? null : strings.Get(def.TipKey),
                    Band = previousBand != null && def.Band != previousBand,
                    Enabled = state.Enabled,
                    Locked = state.Locked,
                    Danger = def.Tone == MenuValues.ToneDanger,
                    Current = currentTab != null && def.Action == MenuActions.Open && def.Tab != null && def.Tab == currentTab,
                    Switch = def.IsSwitch,
                };
                previousBand = def.Band;
                if (row.Switch)
                {
                    row.Checked = settings != null && def.Setting != null && settings.On(def.Setting);
                    row.State = strings.Get(row.Checked ? MetaStringKeys.SettingsOn : MetaStringKeys.SettingsOff);
                    row.Condition = Condition(strings, settings, def.Setting, row.Checked);
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>The sentence under a switch (web quickMenuControls read().condition: fullscreenCapability, musicEnabledCondition).</summary>
        public static string Condition(StringTable strings, SettingsSession settings, string setting, bool on)
        {
            switch (setting)
            {
                case SettingIds.Fullscreen:
                    return strings.Get(on ? StringKeys.PauseFullscreenOn : StringKeys.PauseFullscreenOff);
                case SettingIds.MusicEnabled:
                    if (!on) return strings.Get(StringKeys.PauseMusicOff);
                    if (settings != null && settings.On(SettingIds.MuteAudio)) return strings.Get(StringKeys.PauseMusicMuted);
                    return strings.Format(StringKeys.PauseMusicOn, new StringArgs().Add(ControlsPlaceholders.Volume, settings == null ? 0 : (int)settings.Number(SettingIds.MusicVolume)));
                default:
                    return string.Empty;
            }
        }
    }
}
