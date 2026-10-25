using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Domain.Combat;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using SV = Ashen.Generated.SettingsValues;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-18 settings as the web's Settings (src/ui/screens/settings.js; D-1005): a framed modal with the tabs General | Accessibility | Advanced, the groups of the open tab (Display, Combat, Audio, Game rules;
    /// Opening sequence and About under Advanced), one row per setting (its name, a dot when it is changed, its help and its control: a switch, a row of choices or a slider with minus and plus) and
    /// the header's Reset to defaults, Changed and search. Every change is validated by its control and saved to the profile at once; a slider commits a moment after it stops. The layout is data (ui/meta.json
    /// settingsTabs), the page is <see cref="SettingsSession.Page"/>. Opened from the title or the pause menu (the prologue's bar); Escape and Done and Save leave.
    /// </summary>
    public sealed class SettingsScreen : ScreenView
    {
        private readonly SettingsPageQuery _query = new SettingsPageQuery();
        private SettingsSession _settings;
        private IReadOnlyList<SettingsTabDef> _layout;
        private SettingsPageView _page;
        private string _focusKey;
        private IVisualElementScheduledItem _commit;
        private string _pendingKey;
        private JToken _pendingValue;
        private ScrollView _scroll;
        private VisualElement _rows;

        /// <summary>The page on screen (tests and captures read it).</summary>
        public SettingsPageView Page => _page;

        public SettingsPageQuery Query => _query;

        public SettingsSession Settings => _settings;

        protected override void OnBind(object args)
        {
            _settings = Ui.Climb?.PlayerSettings ?? new SettingsSession(Ui.RunContent, ProfileStore.Load(Ui.Saves));
            _layout = SettingsLayout.From(Ui.Data.Meta);
            _scroll = Root.Q<ScrollView>(UiNames.SettingsScroll);
            _rows = Root.Q(UiNames.SettingsRows);
            _rows.RegisterCallback<FocusInEvent>(e => { if (e.target is VisualElement target && _scroll != null) _scroll.ScrollTo(target); });
            Root.Q<LocButton>(UiNames.SettingsClose).clicked += Leave;
            Root.Q<LocButton>(UiNames.SettingsDone).clicked += Leave;
            Root.Q<LocButton>(UiNames.SettingsReset).clicked += Reset;
            Root.Q<LocButton>(UiNames.SettingsChanged).clicked += () => { _query.ChangedOnly = !_query.ChangedOnly; Render(); };
            Root.Q<LocButton>(UiNames.SettingsSearchBtn).clicked += ToggleSearch;
            Root.Q<LocButton>(UiNames.SettingsProfile).clicked += OpenProfile;
            var search = Root.Q<TextField>(UiNames.SettingsSearch);
            search.RegisterValueChangedCallback(e => { _query.Search = e.newValue; Render(); });
            BoardHud.SetImage(Root.Q(UiNames.SettingsSearchBtn), Ui.Icon(FrontArt.IconSearch));
            UiDom.Show(Root.Q(UiNames.SettingsProfile), Ui.Data.Screens.IsBuilt(ScreenIds.Profile));
            Render();
            Context.Instance.LastFocused = Root.Q(UiNames.SettingsTabs).Children().FirstOrDefault() ?? Root.Q(UiNames.SettingsDone);
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            var strings = Ui.Data.Strings;
            _page = _settings.Page(Ui.Data, _layout, _query);
            Root.Q<LocLabel>(UiNames.SettingsTip).SetResolved(_page.Tip);
            var tabs = Root.Q(UiNames.SettingsTabs);
            tabs.Clear();
            foreach (var tab in _page.Tabs)
            {
                var button = new LocButton();
                button.AddToClassList(FrontClasses.SettingsTab);
                button.EnableInClassList(FrontClasses.SettingsTabOn, tab.Active && !_page.Filtering);
                button.SetResolved(tab.Label);
                var id = tab.Id;
                button.clicked += () => { _query.Tab = id; _query.Group = null; _query.Search = null; _query.ChangedOnly = false; Root.Q<TextField>(UiNames.SettingsSearch).SetValueWithoutNotify(string.Empty); Render(); };
                tabs.Add(button);
            }
            var groups = Root.Q(UiNames.SettingsGroups);
            groups.Clear();
            UiDom.Show(groups, !_page.Filtering && _page.Groups.Count > 1);
            if (_page.Groups.Count > 1) groups.Add(GroupSelect());
            if (!_page.ShowAbout)
            {
                var count = new LocLabel { pickingMode = PickingMode.Ignore };
                count.AddToClassList(FrontClasses.SettingsCount);
                count.SetResolved(_page.Count);
                groups.Add(count);
            }
            var changed = Root.Q<LocButton>(UiNames.SettingsChanged);
            changed.SetResolved(_page.ChangedCount > 0 ? strings.Format(StringKeys.SettingsPageChangedCount, new StringArgs().Add(UiPlaceholders.Count, _page.ChangedCount)) : _page.ChangedLabel);
            changed.EnableInClassList(FrontClasses.SettingsToolOn, _query.ChangedOnly);
            Root.Q<LocButton>(UiNames.SettingsReset).SetEnabled(HasToReset());
            _rows.Clear();
            if (_page.ShowAbout) AboutPanel.Fill(_rows, AboutView.Build(Ui.Data, Ui.BuildVersion));
            else if (_page.Empty != null) _rows.Add(Text(_page.Empty, FrontClasses.SettingsEmpty));
            else foreach (var row in _page.Rows) _rows.Add(Row(row));
            RestoreFocus();
        }

        private bool HasToReset() => _page.Filtering ? _page.ChangedCount > 0 : _page.Rows.Any(r => !r.IsDefault);

        private static LocLabel Text(string text, string cls)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            label.AddToClassList(cls);
            label.SetResolved(text);
            return label;
        }

        private VisualElement Row(SettingControlView control)
        {
            var row = new VisualElement();
            row.AddToClassList(FrontClasses.SettingsRow);
            var texts = new VisualElement { pickingMode = PickingMode.Ignore };
            texts.AddToClassList(FrontClasses.SettingsRowTexts);
            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList(FrontClasses.SettingsRowHead);
            if (!control.IsDefault) head.Add(Text(Ui.Data.Strings.Get(StringKeys.SettingsPageDot), FrontClasses.SettingsDot));
            head.Add(Text(control.Label, FrontClasses.SettingsRowLabel));
            texts.Add(head);
            if (!string.IsNullOrEmpty(control.Help)) texts.Add(Text(control.Help, FrontClasses.SettingsRowHelp));
            if (!string.IsNullOrEmpty(control.AppliesNextRun)) texts.Add(Text(control.AppliesNextRun, FrontClasses.SettingsRowNote));
            row.Add(texts);
            var host = new VisualElement();
            host.AddToClassList(FrontClasses.SettingsControl);
            row.Add(host);
            row.EnableInClassList(FrontClasses.SettingsRowToggle, control.Type == SV.Toggle);
            switch (control.Type)
            {
                case SV.Toggle: host.Add(Switch(control)); break;
                case SV.Enum: host.Add(Choices(control)); break;
                case SV.Slider: host.Add(Slider(control)); break;
            }
            return row;
        }

        private VisualElement Switch(SettingControlView control)
        {
            var on = control.Value is JValue v && v.Type == JTokenType.Boolean && (bool)v;
            var button = new LocButton { userData = control.Key };
            button.AddToClassList(FrontClasses.SettingsSwitch);
            button.EnableInClassList(FrontClasses.SettingsSwitchOn, on);
            var knob = new VisualElement { pickingMode = PickingMode.Ignore };
            knob.AddToClassList(FrontClasses.SettingsKnob);
            button.Add(knob);
            button.tooltip = control.ValueText;
            var key = control.Key;
            button.clicked += () => Apply(key, new JValue(!on), button);
            return button;
        }

        /// <summary>The group picker: a select of the open tab's groups (settings.js renders it as a select on the wide screen and the phone).</summary>
        private SelectField GroupSelect()
        {
            var select = new SelectField();
            select.AddToClassList(FrontClasses.SettingsGroup);
            select.Face.userData = UiNames.SettingsGroups;
            select.SetOptions(_page.Groups.Select(g => new SelectOption { Value = g.Id, Label = g.Label }).ToList(), _page.Groups.FirstOrDefault(g => g.Active)?.Id);
            select.Picked += id => { _focusKey = UiNames.SettingsGroups; _query.Group = id; Render(); };
            return select;
        }

        /// <summary>An enum setting as the web's select: the chosen words with a chevron and the list of choices.</summary>
        private VisualElement Choices(SettingControlView control)
        {
            var select = new SelectField();
            select.AddToClassList(FrontClasses.SettingsSelect);
            select.Face.userData = control.Key;
            select.SetOptions(control.Options.Select(o => new SelectOption { Value = o.Key, Label = o.Value }).ToList(), Js.Str(control.Value));
            var key = control.Key;
            select.Picked += value => Apply(key, new JValue(value), select.Face);
            return select;
        }

        private VisualElement Slider(SettingControlView control)
        {
            var wrap = new VisualElement();
            wrap.AddToClassList(FrontClasses.SettingsSlider);
            var current = Js.Or0(control.Value);
            var readout = Text(control.ValueText, FrontClasses.SettingsReadout);
            var slider = new Slider((float)control.Min, (float)control.Max) { userData = control.Key };
            slider.SetValueWithoutNotify((float)current);
            slider.RegisterValueChangedCallback(e =>
            {
                var stepped = SettingsSession.Stepped(control, e.newValue);
                readout.SetResolved(SettingsSession.NumberText(stepped));
                Defer(control.Key, new JValue(stepped), slider);
            });
            wrap.Add(StepButton(StringKeys.SettingsPageMinus, control, slider, -1));
            wrap.Add(slider);
            wrap.Add(readout);
            wrap.Add(StepButton(StringKeys.SettingsPagePlus, control, slider, 1));
            return wrap;
        }

        private LocButton StepButton(string glyphKey, SettingControlView control, Slider slider, int direction)
        {
            var button = new LocButton { userData = control.Key + glyphKey };
            button.AddToClassList(FrontClasses.SettingsStep);
            button.SetResolved(Ui.Data.Strings.Get(glyphKey));
            var step = control.Step > 0 ? control.Step : (control.Max - control.Min) / UiMath.Percent;
            button.clicked += () =>
            {
                var next = SettingsSession.Stepped(control, slider.value + direction * step);
                Apply(control.Key, new JValue(next), button);
            };
            return button;
        }

        // ------------------------------------------------------------------ changes

        /// <summary>Stores a value at once; a refusal is said at the control that asked.</summary>
        private void Apply(string key, JToken value, VisualElement anchor)
        {
            Flush();
            var refusal = _settings.Set(key, value);
            if (refusal != null)
            {
                if (anchor != null) Ashen.Presentation.UI.Kit.Refusal.Show(anchor, Context.Overlay, _settings.RefusalText(Ui.Data, key, refusal), Ui.Data.Tokens.Duration(TokenKeys.Refusal));
                return;
            }
            _focusKey = anchor?.userData as string;
            Render();
        }

        /// <summary>A slider's value is stored a moment after it stops moving.</summary>
        private void Defer(string key, JToken value, VisualElement anchor)
        {
            _pendingKey = key;
            _pendingValue = value;
            _commit?.Pause();
            _commit = Root.schedule.Execute(() => Apply(_pendingKey, _pendingValue, anchor)).StartingIn(Ui.Data.Tokens.Duration(TokenKeys.SettingsCommit));
        }

        private void Flush()
        {
            _commit?.Pause();
            _commit = null;
            if (_pendingKey == null) return;
            var key = _pendingKey;
            var value = _pendingValue;
            _pendingKey = null;
            _pendingValue = null;
            _settings.Set(key, value);
        }

        private void RestoreFocus()
        {
            if (_focusKey == null) return;
            var key = _focusKey;
            _focusKey = null;
            var target = Root.Query<VisualElement>().Where(e => key.Equals(e.userData)).First();
            target?.Focus();
        }

        private void Reset()
        {
            Flush();
            if (_page.Filtering) _settings.ResetAll();
            else _settings.ResetGroup(_layout.First(t => t.Id == _page.Tab).Groups.First(g => g.Id == _page.Group));
            Render();
        }

        private void ToggleSearch()
        {
            var row = Root.Q(UiNames.SettingsSearchRow);
            var open = row.ClassListContains(UiClasses.Hidden);
            UiDom.Show(row, open);
            var field = Root.Q<TextField>(UiNames.SettingsSearch);
            if (open) field.Focus();
            else
            {
                field.SetValueWithoutNotify(string.Empty);
                _query.Search = null;
                Render();
            }
        }

        private void OpenProfile()
        {
            Flush();
            if (Ui.Data.Screens.IsBuilt(ScreenIds.Profile)) Nav.Go(ScreenIds.Profile);
        }

        private void Leave()
        {
            Flush();
            Nav.Pop();
        }

        public override bool HandleBack()
        {
            Flush();
            return false;
        }

        public override void Unbind() => Flush();
    }
}
