using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using FK = Ashen.Generated.SettingsPageKeys;
using SV = Ashen.Generated.SettingsValues;

namespace Ashen.App.Settings
{
    /// <summary>A group of the web's Settings picker: the topics (W-18 categories) it holds (or the About text).</summary>
    public sealed class SettingsGroupDef
    {
        public string Id;
        public string LabelKey;
        public IReadOnlyList<string> Categories = Array.Empty<string>();
        public bool About;

        /// <summary>Shown only while Developer tools are on (the web's debug-only sections).</summary>
        public bool Developer;
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
                    Developer = g.Is(FK.Developer),
                }).ToList(),
            }).ToList();
    }

    /// <summary>What the page shows: the tab, group and topic in front, a search text, and the Changed filter.</summary>
    public sealed class SettingsPageQuery
    {
        public string Tab;
        public string Group;
        public string Topic;
        public string Search;
        public bool ChangedOnly;

        /// <summary>A dev or editor build: Developer tools are on until the player turns them off, and the switch is offered.</summary>
        public bool DevBuild;

        public bool Filtering => ChangedOnly || !string.IsNullOrWhiteSpace(Search);
    }

    public sealed class SettingsTabView
    {
        public string Id;
        public string Label;

        /// <summary>The sentence the tab says on hover (the web's categoryTip): how many settings and the first three names.</summary>
        public string Tip;
        public bool Active;
    }

    public sealed class SettingsGroupView
    {
        public string Id;
        public string Label;
        public bool Active;
        public bool About;
    }

    public sealed class SettingsTopicView
    {
        public string Id;
        public string Label;
        public bool Active;
    }

    /// <summary>The web's Settings page for one query: tabs, the groups of the open tab, the topics of the open group, the rows to draw and the counts.</summary>
    public sealed class SettingsPageView
    {
        public string Title;
        public string Tip;
        public string Tab;
        public string Group;
        public string Topic;
        public readonly List<SettingsTabView> Tabs = new List<SettingsTabView>();
        public readonly List<SettingsGroupView> Groups = new List<SettingsGroupView>();
        public readonly List<SettingsTopicView> Topics = new List<SettingsTopicView>();
        public readonly List<SettingControlView> Rows = new List<SettingControlView>();

        /// <summary>The Developer tools switch drawn above the Advanced sections (null on a release build and on the other tabs).</summary>
        public SettingControlView DeveloperSwitch;

        /// <summary>Developer tools are on: the debug-only groups are listed.</summary>
        public bool DeveloperOn;

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
        /// <summary>The Developer tools switch: the stored choice, else on for a dev or editor build.</summary>
        public bool DeveloperTools(bool devBuild)
        {
            var stored = Stored?[SettingIds.DeveloperTools];
            return stored is JValue v && v.Type == JTokenType.Boolean ? (bool)v : devBuild;
        }

        /// <summary>The controls the page can show: not the 405 build's, and not the Developer tools switch (it has its own place above the Advanced sections).</summary>
        private IEnumerable<SettingDef> PageDefs() => Defs.Controls.Where(c => !c.LegacyOnly && c.Row == null);

        /// <summary>
        /// The page of the web's Settings (src/ui/screens/settings.js): the tabs, the groups of the open tab, the topics of the open group and the rows of the open topic;
        /// a search or the Changed filter draws the matching rows of every section instead. Engine-free.
        /// </summary>
        public SettingsPageView Page(UiData ui, IReadOnlyList<SettingsTabDef> layout, SettingsPageQuery query)
        {
            query = query ?? new SettingsPageQuery();
            var strings = ui.Strings;
            var devOn = DeveloperTools(query.DevBuild);
            var tab = layout.FirstOrDefault(t => t.Id == query.Tab) ?? layout.FirstOrDefault();
            var groups = tab == null ? new List<SettingsGroupDef>() : tab.Groups.Where(g => devOn || !g.Developer).Where(g => g.About || HasRows(g)).ToList();
            var group = groups.FirstOrDefault(g => g.Id == query.Group) ?? groups.FirstOrDefault();
            var topics = group == null ? new List<string>() : group.Categories.Where(HasRows).ToList();
            var topic = topics.FirstOrDefault(t => t == query.Topic) ?? topics.FirstOrDefault();
            var view = new SettingsPageView
            {
                Title = strings.Get(StringKeys.SettingsPageTitle),
                Tip = strings.Get(StringKeys.SettingsPageTip),
                Tab = tab?.Id,
                Group = group?.Id,
                Topic = topic,
                DeveloperOn = devOn,
                Filtering = query.Filtering,
                ResetLabel = strings.Get(StringKeys.SettingsPageReset),
                ChangedLabel = strings.Get(StringKeys.SettingsPageChanged),
                SearchHint = strings.Get(StringKeys.SettingsPageSearch),
            };
            foreach (var t in layout) view.Tabs.Add(new SettingsTabView { Id = t.Id, Label = strings.Get(t.LabelKey), Tip = TabTip(ui, t, devOn), Active = t == tab });
            foreach (var g in groups) view.Groups.Add(new SettingsGroupView { Id = g.Id, Label = strings.Get(g.LabelKey), Active = g == group, About = g.About });
            if (topics.Count > 1)
                foreach (var t in topics) view.Topics.Add(new SettingsTopicView { Id = t, Label = strings.Get(Defs.Categories.First(c => c.Key == t).Value), Active = t == topic });
            var all = PageDefs().Select(def => ControlOf(ui, def)).ToList();
            view.ChangedCount = all.Count(c => !c.IsDefault);
            if (tab != null && tab.Id == AdvancedTab && query.DevBuild)
                view.DeveloperSwitch = ControlOf(ui, Defs.Controls.First(c => c.Row == SV.RowDeveloperSwitch));
            if (query.Filtering)
            {
                var text = (query.Search ?? string.Empty).Trim();
                view.Rows.AddRange(all.Where(c => (!query.ChangedOnly || !c.IsDefault)
                    && (text.Length == 0 || Matches(c, text))));
            }
            else if (group != null && group.About) view.ShowAbout = true;
            else if (topic != null)
                view.Rows.AddRange(all.Where(c => c.Category == topic));
            view.Count = strings.Format(StringKeys.SettingsPageCount, new StringArgs().Add(UiPlaceholders.Count, view.Rows.Count));
            if (view.Rows.Count == 0 && !view.ShowAbout) view.Empty = strings.Get(StringKeys.SettingsPageEmpty);
            return view;
        }

        /// <summary>The tab that holds the Developer tools switch (the web's Advanced).</summary>
        public const string AdvancedTab = SV.TabAdvanced;

        private bool HasRows(SettingsGroupDef group) => group.Categories.Any(HasRows);

        private bool HasRows(string category) => PageDefs().Any(c => c.Category == category);

        /// <summary>The sentence a tab says on hover (settings.js categoryTip): the count of settings and the first three names.</summary>
        private string TabTip(UiData ui, SettingsTabDef tab, bool devOn)
        {
            var strings = ui.Strings;
            if (tab.Id == AdvancedTab) return strings.Get(devOn ? StringKeys.SettingsTabAdvancedTip : StringKeys.SettingsTabAdvancedTipRelease);
            var categories = tab.Groups.Where(g => devOn || !g.Developer).SelectMany(g => g.Categories).ToList();
            var labels = PageDefs().Where(c => categories.Contains(c.Category)).Select(c => strings.Get(c.LabelKey)).ToList();
            if (labels.Count == 0) return null;
            var shown = (int)Js.Or0(ui.Meta?[FK.TipNames]);
            var names = string.Join(strings.Get(StringKeys.SettingsTabListSeparator), labels.Take(shown)) + (labels.Count > shown ? strings.Get(StringKeys.SettingsTabEllipsis) : string.Empty);
            return strings.Format(StringKeys.SettingsTabGeneralTip, new StringArgs().Add(UiPlaceholders.Count, labels.Count).Add(UiPlaceholders.Text, names));
        }

        private static bool Matches(SettingControlView control, string text) =>
            control.Label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || (control.Help != null && control.Help.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Returns a group's settings to their defaults ([Reset to defaults]): every stored value of its topics is cleared. Returns how many topics changed.</summary>
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

        /// <summary>Returns one setting to its default (the row's Reset).</summary>
        public void ResetKey(string key) => ResetKeys(new[] { key });

        /// <summary>
        /// Returns the named settings to their defaults (the open topic's, a filter's results, one row). Resetting a bundled value by hand
        /// returns its preset to Custom, as the web's resetKeys does for the pacing pair.
        /// </summary>
        public void ResetKeys(IEnumerable<string> keys)
        {
            if (!(Stored is JObject stored)) return;
            var changed = false;
            foreach (var key in keys)
            {
                var def = Defs.Find(key);
                if (def == null) continue;
                changed |= stored.Remove(key);
                if (def.ResetsPreset != null) changed |= stored.Remove(def.ResetsPreset);
            }
            if (!changed) return;
            _profile.Save(_content.ContentHash);
            Changed?.Invoke(this, null);
        }

        /// <summary>Returns every setting to its default (the page's reset while a filter is on, and Reset all settings).</summary>
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
