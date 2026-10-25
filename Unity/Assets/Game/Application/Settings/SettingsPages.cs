using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using FK = Ashen.Generated.SettingsPageKeys;

namespace Ashen.App.Settings
{
    /// <summary>A group of the web's Settings picker: the W-18 categories it shows (or the About text).</summary>
    public sealed class SettingsGroupDef
    {
        public string Id;
        public string LabelKey;
        public IReadOnlyList<string> Categories = Array.Empty<string>();
        public bool About;
    }

    /// <summary>One of the three tabs of the web's Settings (General, Accessibility, Advanced).</summary>
    public sealed class SettingsTabDef
    {
        public string Id;
        public string LabelKey;
        public IReadOnlyList<SettingsGroupDef> Groups = Array.Empty<SettingsGroupDef>();
    }

    /// <summary>ui/meta.json <c>settingsTabs</c>: how the W-18 controls are laid out as the web's Settings (VP-13, D-1005).</summary>
    public static class SettingsLayout
    {
        public static IReadOnlyList<SettingsTabDef> From(JObject meta) =>
            Js.Items(meta?[FK.SettingsTabs]).OfType<JObject>().Select(t => new SettingsTabDef
            {
                Id = t.Str(FK.Id),
                LabelKey = t.Str(FK.LabelKey),
                Groups = Js.Items(t[FK.Groups]).OfType<JObject>().Select(g => new SettingsGroupDef
                {
                    Id = g.Str(FK.Id),
                    LabelKey = g.Str(FK.LabelKey),
                    Categories = Js.Items(g[FK.Categories]).Select(Js.Str).Where(c => c != null).ToList(),
                    About = g.Is(FK.About),
                }).ToList(),
            }).ToList();
    }

    /// <summary>What the page shows: the tab and group in front, a search text, and the Changed filter.</summary>
    public sealed class SettingsPageQuery
    {
        public string Tab;
        public string Group;
        public string Search;
        public bool ChangedOnly;

        public bool Filtering => ChangedOnly || !string.IsNullOrWhiteSpace(Search);
    }

    public sealed class SettingsTabView
    {
        public string Id;
        public string Label;
        public bool Active;
    }

    public sealed class SettingsGroupView
    {
        public string Id;
        public string Label;
        public bool Active;
        public bool About;
    }

    /// <summary>The web's Settings page for one query: tabs, the groups of the open tab, the rows to draw and the counts.</summary>
    public sealed class SettingsPageView
    {
        public string Title;
        public string Tip;
        public string Tab;
        public string Group;
        public readonly List<SettingsTabView> Tabs = new List<SettingsTabView>();
        public readonly List<SettingsGroupView> Groups = new List<SettingsGroupView>();
        public readonly List<SettingControlView> Rows = new List<SettingControlView>();

        /// <summary>The group in front shows the About text instead of controls.</summary>
        public bool ShowAbout;

        /// <summary>A search or the Changed filter is on: the rows come from every section.</summary>
        public bool Filtering;

        /// <summary>"4 settings" (the rows drawn).</summary>
        public string Count;

        /// <summary>Said when the filter leaves nothing.</summary>
        public string Empty;

        /// <summary>Settings the player has changed from their defaults, in every section.</summary>
        public int ChangedCount;

        public string ResetLabel;
        public string ChangedLabel;
        public string SearchHint;
    }

    public sealed partial class SettingsSession
    {
        /// <summary>
        /// The page of the web's Settings (src/ui/screens/settings.js): the tabs, the groups of the open tab, and the rows of the open group (a group is some categories of settings/defaults.json);
        /// a search or the Changed filter draws the matching rows of every section instead. Engine-free.
        /// </summary>
        public SettingsPageView Page(UiData ui, IReadOnlyList<SettingsTabDef> layout, SettingsPageQuery query)
        {
            query = query ?? new SettingsPageQuery();
            var strings = ui.Strings;
            var tab = layout.FirstOrDefault(t => t.Id == query.Tab) ?? layout.FirstOrDefault();
            var group = tab?.Groups.FirstOrDefault(g => g.Id == query.Group) ?? tab?.Groups.FirstOrDefault();
            var view = new SettingsPageView
            {
                Title = strings.Get(StringKeys.SettingsPageTitle),
                Tip = strings.Get(StringKeys.SettingsPageTip),
                Tab = tab?.Id,
                Group = group?.Id,
                Filtering = query.Filtering,
                ResetLabel = strings.Get(StringKeys.SettingsPageReset),
                ChangedLabel = strings.Get(StringKeys.SettingsPageChanged),
                SearchHint = strings.Get(StringKeys.SettingsPageSearch),
            };
            foreach (var t in layout) view.Tabs.Add(new SettingsTabView { Id = t.Id, Label = strings.Get(t.LabelKey), Active = t == tab });
            if (tab != null)
                foreach (var g in tab.Groups) view.Groups.Add(new SettingsGroupView { Id = g.Id, Label = strings.Get(g.LabelKey), Active = g == group, About = g.About });
            var all = Defs.Controls.Select(def => ControlOf(ui, def)).ToList();
            view.ChangedCount = all.Count(c => !c.IsDefault);
            if (query.Filtering)
            {
                var text = (query.Search ?? string.Empty).Trim();
                view.Rows.AddRange(all.Where(c => (!query.ChangedOnly || !c.IsDefault)
                    && (text.Length == 0 || Matches(c, text))));
            }
            else if (group != null && group.About) view.ShowAbout = true;
            else if (group != null)
                foreach (var category in group.Categories) view.Rows.AddRange(all.Where(c => c.Category == category));
            view.Count = strings.Format(StringKeys.SettingsPageCount, new StringArgs().Add(UiPlaceholders.Count, view.Rows.Count));
            if (view.Rows.Count == 0 && !view.ShowAbout) view.Empty = strings.Get(StringKeys.SettingsPageEmpty);
            return view;
        }

        private static bool Matches(SettingControlView control, string text) =>
            control.Label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || (control.Help != null && control.Help.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Returns a group's settings to their defaults ([Reset to defaults]): every stored value of its categories is cleared. Returns how many categories changed.</summary>
        public int ResetGroup(SettingsGroupDef group)
        {
            var changed = 0;
            foreach (var category in group.Categories)
            {
                var before = Defs.Controls.Count(c => c.Category == category && Stored?[c.Key] != null);
                if (before == 0) continue;
                ResetCategory(category);
                changed++;
            }
            return changed;
        }

        /// <summary>Returns every setting to its default (the page's reset while a filter is on).</summary>
        public void ResetAll()
        {
            foreach (var category in Defs.Categories) ResetCategory(category.Key);
        }

        /// <summary>The number a slider shows for a step from its value (clamped to its range, on its grid).</summary>
        public static double Stepped(SettingControlView control, double value)
        {
            var n = Math.Max(control.Min, Math.Min(control.Max, value));
            if (control.Step <= 0) return n;
            var steps = Math.Round((n - control.Min) / control.Step);
            return Math.Min(control.Max, control.Min + steps * control.Step);
        }

        /// <summary>The text of a number as a setting shows it (invariant).</summary>
        public static string NumberText(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
