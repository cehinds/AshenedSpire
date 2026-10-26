using System;
using System.Collections.Generic;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The two Settings groups that are panels and not rows: Advanced > Changelog (web ui/screens/about.js renderChangelogSection: "What changed", the newest changes first, a heading per day and a fold per change with its build)
    /// and General > Game rules (web ui/components/gameProfileSelector.js mountGameProfileSelector: the ruleset new games begin under and, for the core ruleset, the XP multipliers). Pure construction over the data.
    /// </summary>
    internal static class SettingsPanels
    {
        private static LocLabel Text(VisualElement host, string text, params string[] classes)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) label.AddToClassList(c);
            label.SetResolved(text ?? string.Empty);
            host.Add(label);
            return label;
        }

        /// <summary>renderChangelogSection: a section head, the blurb, a rule, then each day's changes (an entry with detail opens when it is pressed; the open ones are kept across redraws).</summary>
        public static void Changelog(VisualElement host, UiData ui, ISet<string> open, Action<string> toggle)
        {
            var strings = ui.Strings;
            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList(SettingsPanelClasses.Head);
            Text(head, strings.Get(StringKeys.SettingsChangelogEyebrow), SettingsPanelClasses.Eyebrow, UiClasses.Upper);
            Text(head, strings.Get(StringKeys.SettingsChangelogTitle), SettingsPanelClasses.Title);
            host.Add(head);
            Text(host, strings.Get(StringKeys.SettingsChangelogBlurb), SettingsPanelClasses.Blurb);
            var rule = new VisualElement { pickingMode = PickingMode.Ignore };
            rule.AddToClassList(SettingsPanelClasses.Rule);
            host.Add(rule);
            if (ui.Changelog.Groups.Count == 0) Text(host, strings.Get(StringKeys.SettingsChangelogEmpty), SettingsPanelClasses.Blurb);
            foreach (var group in ui.Changelog.Groups)
            {
                var title = new VisualElement { pickingMode = PickingMode.Ignore };
                title.AddToClassList(SettingsPanelClasses.GroupHead);
                Text(title, strings.Get(StringKeys.SettingsChangelogGroup), SettingsPanelClasses.Eyebrow, UiClasses.Upper);
                Text(title, group.Title, SettingsPanelClasses.Title);
                host.Add(title);
                foreach (var entry in group.Entries)
                {
                    var isOpen = open.Contains(entry.Id);
                    var hasDetail = entry.Detail.Length > 0;
                    var change = new VisualElement { focusable = hasDetail, pickingMode = hasDetail ? PickingMode.Position : PickingMode.Ignore, userData = entry.Id };
                    change.AddToClassList(SettingsPanelClasses.Change);
                    change.EnableInClassList(SettingsPanelClasses.ChangeOpen, isOpen);
                    var line = new VisualElement { pickingMode = PickingMode.Ignore };
                    line.AddToClassList(SettingsPanelClasses.ChangeHead);
                    Text(line, entry.Summary, SettingsPanelClasses.ChangeSummary);
                    Text(line, entry.Build, SettingsPanelClasses.ChangeBuild);
                    change.Add(line);
                    if (isOpen && hasDetail) Text(change, entry.Detail, SettingsPanelClasses.ChangeBody);
                    if (hasDetail)
                    {
                        var id = entry.Id;
                        change.RegisterCallback<ClickEvent>(_ => toggle(id));
                    }
                    host.Add(change);
                }
            }
        }

        /// <summary>
        /// mountGameProfileSelector for the core ruleset: "New game:" with the ruleset new games begin under, what it applies to, the XP policy and the two multipliers (class XP and used weapon XP, 0 to 10). This build plays one ruleset,
        /// so the select holds Ashen core alone and the web's Current game and Import JSON are not drawn (D-1451). A number outside 0 to 10 is put back and the bounds said (the web's xpBounds).
        /// </summary>
        public static void Rules(VisualElement host, UiData ui, SettingsSession settings, Action<string, JToken, VisualElement> apply)
        {
            var strings = ui.Strings;
            var panel = new VisualElement();
            panel.AddToClassList(SettingsPanelClasses.RulesPanel);
            host.Add(panel);
            var selector = new VisualElement { pickingMode = PickingMode.Ignore };
            selector.AddToClassList(SettingsPanelClasses.RulesLine);
            panel.Add(selector);
            Text(selector, strings.Get(StringKeys.SettingsRulesSelector), SettingsPanelClasses.RulesNote);
            var select = new SelectField();
            select.AddToClassList(FrontClasses.SettingsSelect);
            select.SetOptions(new List<SelectOption> { new SelectOption { Value = StringKeys.SettingsRulesCore, Label = strings.Get(StringKeys.SettingsRulesCore) } }, StringKeys.SettingsRulesCore);
            select.Face.tooltip = strings.Get(StringKeys.SettingsRulesSelectorLabel);
            selector.Add(select);
            var status = Text(panel, strings.Format(StringKeys.SettingsRulesFuture, new StringArgs().Add(SettingsRulesPlaceholders.Profile, strings.Get(StringKeys.SettingsRulesCore))), SettingsPanelClasses.RulesStatus);
            Text(panel, strings.Get(StringKeys.SettingsRulesXpPolicy), SettingsPanelClasses.RulesNote);
            foreach (var key in new[] { SettingsRulesKeys.ClassXp, SettingsRulesKeys.WeaponXp })
            {
                var def = settings.Defs.Find(key);
                var line = new VisualElement { pickingMode = PickingMode.Ignore };
                line.AddToClassList(SettingsPanelClasses.RulesLine);
                panel.Add(line);
                Text(line, strings.Get(def.LabelKey), SettingsPanelClasses.RulesNote);
                var field = new TextField { userData = key, value = settings.Number(key).ToString(System.Globalization.CultureInfo.InvariantCulture), isDelayed = true };
                field.AddToClassList(SettingsPanelClasses.RulesField);
                var name = key;
                field.RegisterValueChangedCallback(e =>
                {
                    if (!double.TryParse(e.newValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < def.Min || number > def.Max)
                    {
                        field.SetValueWithoutNotify(settings.Number(name).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        status.SetResolved(strings.Get(StringKeys.SettingsRulesXpBounds));
                        return;
                    }
                    apply(name, new JValue(number), field);
                });
                line.Add(field);
            }
        }
    }
}
