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
    /// W-18 settings as the web's Settings (src/ui/screens/settings.js; D-1005, D-1370..): a framed modal with the tabs General | Accessibility | Advanced, the group select and the topic select of the open tab
    /// (Display / Combat / Audio, then Interface / Title screen / Map / HUD; Opening sequence, Deck, Interface and About under Advanced), one row per setting (its name, a dot and a Reset when it is changed, its help and its control:
    /// a switch, a select, a slider with minus and plus, or a button) and the header's Reset to defaults, Changed, search and more menu, the first-open tip and the Undo bar a reset leaves for eight seconds. Every change is validated by its
    /// control and saved to the profile at once; a slider commits a moment after it stops. The layout is data (ui/meta.json settingsTabs), the page is <see cref="SettingsSession.Page"/>. Opened from the title or the pause menu (the prologue's bar);
    /// Escape and Done and Save leave.
    /// </summary>
    public sealed class SettingsScreen : ScreenView
    {
        private readonly SettingsPageQuery _query = new SettingsPageQuery();
        private SettingsSession _settings;
        private IReadOnlyList<SettingsTabDef> _layout;
        private SettingsPageView _page;
        private string _focusKey;
        private IVisualElementScheduledItem _commit;
        private IVisualElementScheduledItem _undoTimer;
        private string _pendingKey;
        private JToken _pendingValue;
        private ScrollView _scroll;
        private VisualElement _rows;
        private JObject _undo;
        private string _undoLabel;
        private readonly HashSet<string> _openChanges = new HashSet<string>();

        /// <summary>The page on screen (tests and captures read it).</summary>
        public SettingsPageView Page => _page;

        public SettingsPageQuery Query => _query;

        public SettingsSession Settings => _settings;

        protected override void OnBind(object args)
        {
            _settings = Ui.Climb?.PlayerSettings ?? new SettingsSession(Ui.RunContent, ProfileStore.Load(Ui.Saves), core2: true);
            _layout = SettingsLayout.From(Ui.Data.Meta);
            _query.DevBuild = Ui.DevBuild;
            _scroll = Root.Q<ScrollView>(UiNames.SettingsScroll);
            _rows = Root.Q(UiNames.SettingsRows);
            _rows.RegisterCallback<FocusInEvent>(e => { if (e.target is VisualElement target && _scroll != null) _scroll.ScrollTo(target); });
            Root.Q<LocButton>(UiNames.SettingsClose).clicked += Leave;
            Root.Q<LocButton>(UiNames.SettingsDone).clicked += Leave;
            Root.Q<LocButton>(UiNames.SettingsReset).clicked += AskResetAll;
            Root.Q<LocButton>(UiNames.SettingsChanged).clicked += () => { _query.ChangedOnly = !_query.ChangedOnly; Render(); };
            Root.Q<LocButton>(UiNames.SettingsSearchBtn).clicked += ToggleSearch;
            Root.Q<LocButton>(UiNames.SettingsProfile).clicked += OpenProfile;
            Root.Q<LocButton>(UiNames.SettingsMore).clicked += ToggleMenu;
            Root.Q<LocButton>(UiNames.SettingsMenuGroup).clicked += () => { CloseMenu(); ResetOpenTopic(); };
            Root.Q<LocButton>(UiNames.SettingsMenuAll).clicked += () => { CloseMenu(); AskResetAll(); };
            Root.Q<LocButton>(UiNames.SettingsTipClose).clicked += () => { _settings.DismissTip(); UiDom.Show(Root.Q(UiNames.SettingsTipRow), false); };
            Root.Q<LocButton>(UiNames.SettingsUndoButton).clicked += Undo;
            var search = Root.Q<TextField>(UiNames.SettingsSearch);
            search.RegisterValueChangedCallback(e => { _query.Search = e.newValue; Render(); });
            BoardHud.SetImage(Root.Q(UiNames.SettingsSearchBtn), Ui.Icon(FrontArt.IconSearch));
            UiDom.Show(Root.Q(UiNames.SettingsProfile), Ui.Data.Screens.IsBuilt(ScreenIds.Profile));
            UiDom.Show(Root.Q(UiNames.SettingsTipRow), !_settings.TipDismissed);
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
            var tipDelay = Ui.Data.Tokens.Duration(TokenKeys.TooltipDelay);
            foreach (var tab in _page.Tabs)
            {
                var button = new LocButton();
                button.AddToClassList(FrontClasses.SettingsTab);
                button.EnableInClassList(FrontClasses.SettingsTabOn, tab.Active && !_page.Filtering);
                button.SetResolved(tab.Label);
                var id = tab.Id;
                button.clicked += () => { _query.Tab = id; _query.Group = null; _query.Topic = null; _query.Search = null; _query.ChangedOnly = false; Root.Q<TextField>(UiNames.SettingsSearch).SetValueWithoutNotify(string.Empty); Render(); };
                if (!string.IsNullOrEmpty(tab.Tip)) Tooltip.AttachResolved(button, Context.Overlay, Ui.Data.Components.TooltipPlain, tab.Label, tab.Tip, tipDelay);
                tabs.Add(button);
            }
            var groups = Root.Q(UiNames.SettingsGroups);
            groups.Clear();
            var selects = !_page.Filtering && (_page.Groups.Count > 1 || _page.Topics.Count > 1);
            UiDom.Show(groups, selects || (!_page.Filtering && !_page.ShowAbout));
            if (!_page.Filtering && _page.Groups.Count > 1) groups.Add(GroupSelect());
            if (!_page.Filtering && _page.Topics.Count > 1) groups.Add(TopicSelect());
            if (!_page.ShowAbout && !_page.ShowChangelog && !_page.ShowRules)
            {
                var summary = new VisualElement { pickingMode = PickingMode.Ignore };
                summary.AddToClassList(SettingsPanelClasses.Summary);
                if (!string.IsNullOrEmpty(_page.GroupTip)) summary.Add(Text(_page.GroupTip, SettingsPanelClasses.GroupTip));
                var count = new LocLabel { pickingMode = PickingMode.Ignore };
                count.AddToClassList(FrontClasses.SettingsCount);
                count.SetResolved(_page.Count);
                summary.Add(count);
                groups.Add(summary);
            }
            var changed = Root.Q<LocButton>(UiNames.SettingsChanged);
            changed.SetResolved(_page.ChangedCount > 0 ? strings.Format(StringKeys.SettingsPageChangedCount, new StringArgs().Add(UiPlaceholders.Count, _page.ChangedCount)) : _page.ChangedLabel);
            changed.EnableInClassList(FrontClasses.SettingsToolOn, _query.ChangedOnly);
            Root.Q<LocButton>(UiNames.SettingsReset).SetEnabled(_page.ChangedCount > 0);
            _rows.Clear();
            if (_page.DeveloperSwitch != null && !_page.Filtering) _rows.Add(Row(_page.DeveloperSwitch));
            if (_page.ShowAbout) AboutPanel.Fill(_rows, AboutView.Build(Ui.Data, Ui.BuildVersion), Ui.Data.Strings);
            else if (_page.ShowChangelog) SettingsPanels.Changelog(_rows, Ui.Data, _openChanges, id =>
            {
                if (!_openChanges.Add(id)) _openChanges.Remove(id);
                _focusKey = id;
                Render();
            });
            else if (_page.ShowRules) SettingsPanels.Rules(_rows, Ui.Data, _settings, Apply);
            else if (_page.Empty != null) _rows.Add(Text(_page.Empty, FrontClasses.SettingsEmpty));
            else foreach (var row in _page.Rows) _rows.Add(Row(row));
            RestoreFocus();
        }

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
            var head = new VisualElement();
            head.AddToClassList(FrontClasses.SettingsRowHead);
            if (!control.IsDefault) head.Add(Text(Ui.Data.Strings.Get(StringKeys.SettingsPageDot), FrontClasses.SettingsDot));
            head.Add(Text(control.Label, FrontClasses.SettingsRowLabel));
            if (!control.IsDefault) head.Add(RowReset(control));
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
                case SV.Button: host.Add(ActionButton(control)); break;
            }
            return row;
        }

        /// <summary>The Reset beside a changed setting's name (settings.js set-row-reset): this one setting returns to its default, with an Undo.</summary>
        private LocButton RowReset(SettingControlView control)
        {
            var button = new LocButton { userData = control.Key + StringKeys.SettingsPageRowReset };
            button.AddToClassList(FrontClasses.SettingsRowReset);
            button.SetResolved(Ui.Data.Strings.Get(StringKeys.SettingsPageRowReset));
            button.tooltip = Ui.Data.Strings.Get(StringKeys.SettingsPageRowResetTip);
            var key = control.Key;
            var name = control.Label;
            button.clicked += () => ResetWith(Ui.Data.Strings.Format(StringKeys.SettingsUndoRow, new StringArgs().Add(UiPlaceholders.Name, name)), () => _settings.ResetKey(key));
            return button;
        }

        private VisualElement Switch(SettingControlView control)
        {
            var on = control.Shown;
            var button = new LocButton { userData = control.Key };
            button.AddToClassList(FrontClasses.SettingsSwitch);
            button.EnableInClassList(FrontClasses.SettingsSwitchOn, on);
            var knob = new VisualElement { pickingMode = PickingMode.Ignore };
            knob.AddToClassList(FrontClasses.SettingsKnob);
            button.Add(knob);
            button.tooltip = Ui.Data.Strings.Get(on ? MetaStringKeys.SettingsOn : MetaStringKeys.SettingsOff);
            var key = control.Key;
            var positive = control.Positive;
            button.clicked += () => Apply(key, new JValue(positive ? !on : on), button);
            return button;
        }

        /// <summary>A button row (Reset all settings): it asks first.</summary>
        private VisualElement ActionButton(SettingControlView control)
        {
            var button = new LocButton { userData = control.Key };
            button.AddToClassList(FrontClasses.SettingsButton);
            button.AddToClassList(FrontClasses.SettingsTool);
            button.SetResolved(Ui.Data.Strings.Get(StringKeys.SettingsResetAllSettingsButton));
            if (control.Action == SV.ActionResetAll) button.clicked += AskResetAll;
            return button;
        }

        /// <summary>The group select: the open tab's groups (settings.js renders it as a select on the wide screen and the phone).</summary>
        private SelectField GroupSelect()
        {
            var select = new SelectField();
            select.AddToClassList(FrontClasses.SettingsGroup);
            select.Face.userData = UiNames.SettingsGroups;
            select.SetOptions(_page.Groups.Select(g => new SelectOption { Value = g.Id, Label = g.Label }).ToList(), _page.Groups.FirstOrDefault(g => g.Active)?.Id);
            select.Picked += id => { _focusKey = UiNames.SettingsGroups; _query.Group = id; _query.Topic = null; Render(); };
            return select;
        }

        /// <summary>The topic select beside the group select: the open group's topics (Interface, Title screen, Map, HUD).</summary>
        private SelectField TopicSelect()
        {
            var select = new SelectField();
            select.AddToClassList(FrontClasses.SettingsTopic);
            select.Face.userData = FrontClasses.SettingsTopic;
            select.SetOptions(_page.Topics.Select(t => new SelectOption { Value = t.Id, Label = t.Label }).ToList(), _page.Topics.FirstOrDefault(t => t.Active)?.Id);
            select.Picked += id => { _focusKey = FrontClasses.SettingsTopic; _query.Topic = id; Render(); };
            return select;
        }

        /// <summary>An enum setting as the web's select: the chosen words with a chevron and the list of choices.</summary>
        private VisualElement Choices(SettingControlView control)
        {
            if (!control.AsSelect) return Segmented(control);
            var select = new SelectField();
            select.AddToClassList(FrontClasses.SettingsSelect);
            select.Face.userData = control.Key;
            select.SetOptions(control.Options.Select(o => new SelectOption { Value = o.Key, Label = o.Value }).ToList(), Js.Str(control.Value));
            var key = control.Key;
            select.Picked += value => Apply(key, new JValue(value), select.Face);
            return select;
        }

        /// <summary>A choice of three or fewer options (web .as-seg.choice-group): its options side by side, the chosen one lit.</summary>
        private VisualElement Segmented(SettingControlView control)
        {
            var group = new VisualElement();
            group.AddToClassList(SettingsPanelClasses.Segmented);
            var chosen = Js.Str(control.Value);
            foreach (var option in control.Options)
            {
                var button = new LocButton { userData = control.Key + option.Key };
                button.AddToClassList(SettingsPanelClasses.SegmentOption);
                button.EnableInClassList(SettingsPanelClasses.SegmentOn, option.Key == chosen);
                button.SetResolved(option.Value);
                var key = control.Key;
                var value = option.Key;
                button.clicked += () => Apply(key, new JValue(value), button);
                group.Add(button);
            }
            return group;
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

        // ------------------------------------------------------------------ resets, with an Undo for eight seconds (settings.js resetKeys, offerUndo)

        /// <summary>Takes a snapshot, runs the reset, and offers the Undo bar with the label.</summary>
        private void ResetWith(string label, System.Action reset)
        {
            Flush();
            var before = _settings.Snapshot();
            reset();
            if (!JToken.DeepEquals(before, _settings.Snapshot())) OfferUndo(label, before);
            Render();
        }

        /// <summary>The header's Reset to defaults and the menu's Reset all settings: it asks first (the web's confirmResetAll).</summary>
        private void AskResetAll()
        {
            Flush();
            ConfirmRequest.Open(Nav, new ConfirmRequest
            {
                ConfirmId = ConfirmIds.ResetSettings,
                OnConfirm = () => ResetWith(Ui.Data.Strings.Get(StringKeys.SettingsUndoAll), () => _settings.ResetAll()),
            });
        }

        /// <summary>Reset this group: the settings of the topic in front, or the matches while a filter is on.</summary>
        private void ResetOpenTopic()
        {
            var keys = _page.Rows.Select(r => r.Key).ToList();
            ResetWith(Ui.Data.Strings.Get(_page.Filtering ? StringKeys.SettingsUndoResults : StringKeys.SettingsUndoGroup), () => _settings.ResetKeys(keys));
        }

        private void OfferUndo(string label, JObject before)
        {
            _undo = before;
            _undoLabel = label;
            var bar = Root.Q(UiNames.SettingsUndo);
            Root.Q<LocLabel>(UiNames.SettingsUndoText).SetResolved(label);
            UiDom.Show(bar, true);
            _undoTimer?.Pause();
            _undoTimer = Root.schedule.Execute(DropUndo).StartingIn(Ui.Data.Tokens.Duration(TokenKeys.SettingsUndo));
        }

        private void DropUndo()
        {
            _undo = null;
            _undoTimer?.Pause();
            _undoTimer = null;
            UiDom.Show(Root.Q(UiNames.SettingsUndo), false);
        }

        private void Undo()
        {
            if (_undo == null) return;
            _settings.Restore(_undo);
            DropUndo();
            Render();
        }

        // ------------------------------------------------------------------ the header's tools

        private void ToggleMenu()
        {
            var menu = Root.Q(UiNames.SettingsMenu);
            UiDom.Show(menu, menu.ClassListContains(UiClasses.Hidden));
        }

        private void CloseMenu() => UiDom.Show(Root.Q(UiNames.SettingsMenu), false);

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

        public override void Unbind()
        {
            Flush();
            _undoTimer?.Pause();
        }
    }
}
