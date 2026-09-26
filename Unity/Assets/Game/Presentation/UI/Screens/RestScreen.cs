using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-10 rest (W1s; AF-06, US-8.1 to US-8.6), bound to the run session's rest stay: the RUN_HUD band; the head "{place} ·
    /// {available} of {total} available" with the place's tags and the arrival refill; the option cards the place offers
    /// beside their availability column — Rest ⟲ (hold-to-confirm, US-8.2: a short press shows the review with the heal
    /// before and after, a hold of the player's hold dial commits; releasing early cancels), Smith (the item picker with tier
    /// and change previews, W1i), Extract and Install (mount rows with their receipts, W1j/W1k), the flask split within the
    /// shared pool, and Level up (attribute rows with + and −, a live receipt of the derived pools; W-22). Each commit is
    /// saved at once (PF-06 "rest action"); a committed service at a single-use place ends the stay and returns to the map.
    /// The footer's Continue shows at a multi-use place or when a relic forbids Rest (04 W-10). Escape leaves a pane, else
    /// opens W-20.
    /// </summary>
    public sealed class RestScreen : ScreenView
    {
        private readonly List<VisualElement> _options = new List<VisualElement>();
        private readonly List<VisualElement> _paneItems = new List<VisualElement>();
        private RunSession _session;
        private RestViewState _view;
        private Shell _shell;
        private RunHud _hud;
        private string _pane;
        private string _paneSelected;
        private JObject _assign = new JObject();
        private float _restCommittedAt = float.NegativeInfinity;

        public RunSession Session => _session;
        public RestViewState View => _view;
        public IReadOnlyList<VisualElement> Options => _options;
        public IReadOnlyList<VisualElement> PaneItems => _paneItems;
        public string Pane => _pane;
        public HoldButton RestHold => Root.Q<HoldButton>(UiNames.RestHold);
        public LocButton ContinueButton => _shell?.PrimaryButton;
        public LocButton CommitButton => Root.Q<LocButton>(UiNames.PaneCommit);

        public override string FocusMode => _pane != null ? UiValues.FocusPane : null;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            _session = (args as RunScreenArgs)?.Session ?? Ui.Session;
            if (_session?.Visit == null)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            Ui.Session = _session;
            _hud = Root.Q<RunHud>(UiNames.RunHud);
            if (_hud != null) _hud.Menu += OpenPause;
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.Primary += Leave;
            Root.Q<LocButton>(UiNames.PaneBack).clicked += ClosePane;
            var commit = CommitButton;
            commit.clicked += () => CommitPane(commit);
            ApplyBackground();
            Render();
            Context.Instance.LastFocused = _options.FirstOrDefault(o => o.enabledSelf) ?? (VisualElement)ContinueButton;
        }

        private void ApplyBackground()
        {
            var element = Root.Q(UiNames.RestBackground);
            if (element == null) return;
            var id = StringTable.Fill(Ui.Data.Components.Climb.RestBackground, new StringArgs().Add(UiPlaceholders.Region, _session.Region));
            Ui.ApplyBackground(element, Ui.Art != null && Ui.Art.Get(id) != null ? id : Ui.Data.Components.CombatFallbackBackground);
        }

        // ------------------------------------------------------------------ render

        public void Render()
        {
            _view = RestView.Build(_session, Ui.Data);
            if (_view == null)
            {
                RunFlow.Show(Context, _session);
                return;
            }
            _hud?.Bind(RunHudView.Build(_session, Ui.Data));
            if (_hud?.Portrait != null) Ui.ApplyBackground(_hud.Portrait, _session.Portrait);
            _shell.TitleLabel?.SetResolved(_view.Header);
            Line(UiNames.RestTags, _view.Tags);
            Line(UiNames.RestRefill, _view.Refill);
            _shell.SetFooter(null, _view.ContinueShown ? StringKeys.RestContinue : null);
            _shell.PrimaryButton?.EnableInClassList(UiClasses.ButtonReady, _view.ContinueShown);
            RenderOptions();
            UiDom.Show(Root.Q(UiNames.RestMain), _pane == null);
            UiDom.Show(Root.Q(UiNames.RestPane), _pane != null);
            UiDom.Show(_shell.Footer, _pane == null && _view.ContinueShown);
            if (_pane != null) RenderPane();
        }

        private void Line(string name, string text)
        {
            var label = Root.Q<LocLabel>(name);
            label?.SetResolved(text ?? string.Empty);
            UiDom.Show(label, !string.IsNullOrEmpty(text));
        }

        private void RenderOptions()
        {
            var keep = Context.Instance.Focus.Focused?.name;
            var list = Root.Q(UiNames.RestOptions);
            var availability = Root.Q(UiNames.AvailabilityList);
            list.Clear();
            availability.Clear();
            _options.Clear();
            foreach (var option in _view.Options)
            {
                var line = new VisualElement();
                line.AddToClassList(UiClasses.RestOption);
                line.EnableInClassList(UiClasses.RestOptionUnavailable, !option.Available);
                VisualElement control;
                if (option.Id == RunFlowValues.OptionRest)
                {
                    var hold = new HoldButton { name = UiNames.RestHold, labelKey = StringKeys.RestOptionRest };
                    hold.HoldMs = _session.HoldConfirmMs(Ui.Data.Tokens.Duration(TokenKeys.HoldConfirm));
                    hold.HoldEnabled = hold.HoldMs > 0 && option.Available;
                    hold.AddToClassList(UiClasses.RestOptionButton);
                    var captured = option;
                    hold.Committed += () => DoRest(captured, hold);
                    hold.clicked += () => { if (hold.HoldEnabled && (Time.realtimeSinceStartup - _restCommittedAt) * UiMath.MillisPerSecond > Ui.Data.Tokens.Duration(TokenKeys.FocusSettle)) Review(captured, hold); };
                    control = hold;
                }
                else
                {
                    var button = new LocButton { name = option.Id };
                    button.SetResolved(option.Title);
                    button.AddToClassList(UiClasses.RestOptionButton);
                    var captured = option;
                    button.clicked += () => OnOption(captured, button);
                    control = button;
                }
                line.Add(control);
                var detail = new LocLabel { pickingMode = PickingMode.Ignore };
                detail.SetResolved(option.Detail ?? string.Empty);
                detail.AddToClassList(UiClasses.RestOptionDetail);
                detail.AddToClassList(UiClasses.ValueText);
                line.Add(detail);
                list.Add(line);
                _options.Add(control);

                var row = new VisualElement();
                row.AddToClassList(UiClasses.AvailabilityRow);
                row.EnableInClassList(UiClasses.RestOptionUnavailable, !option.Available);
                var name = new LocLabel();
                name.SetResolved(option.Title);
                name.AddToClassList(UiClasses.AvailabilityName);
                name.AddToClassList(UiClasses.ValueText);
                var state = new LocLabel();
                state.SetResolved(option.StateText);
                state.AddToClassList(UiClasses.AvailabilityState);
                state.AddToClassList(UiClasses.ValueText);
                row.Add(name);
                row.Add(state);
                availability.Add(row);
            }
            if (keep != null && _pane == null)
            {
                var again = _options.FirstOrDefault(o => o.name == keep);
                if (again != null) FocusSoon(again);
            }
        }

        // ------------------------------------------------------------------ the option cards

        private void OnOption(RestOptionView option, VisualElement anchor)
        {
            if (!option.Available)
            {
                Refuse(anchor, option.StateText);
                return;
            }
            OpenPane(option.Id);
        }

        /// <summary>A short press on Rest: the review (what the Rest restores, and that it takes a hold), or why it cannot be taken.</summary>
        private void Review(RestOptionView option, VisualElement anchor)
        {
            if (!option.Available) { Refuse(anchor, option.StateText); return; }
            Refuse(anchor, (_view.RestReview ?? string.Empty) + Strings.Get(StringKeys.RestTagJoin) + Strings.Get(StringKeys.RestReviewHold));
        }

        private void DoRest(RestOptionView option, VisualElement anchor)
        {
            if (!option.Available)
            {
                Refuse(anchor, option.StateText);
                return;
            }
            _restCommittedAt = Time.realtimeSinceStartup;
            Commit(() => _session.RestHere(), anchor);
        }

        /// <summary>A rest action through the session (saved at once): a refusal shows at the control; a stay that ended goes back to the map.</summary>
        private void Commit(Func<RestActionResult> action, VisualElement anchor)
        {
            RestActionResult result;
            try { result = action(); }
            catch (Exception e)
            {
                SaveFailed(e, () => Commit(action, anchor));
                return;
            }
            if (!result.Done)
            {
                var key = string.Format(CultureInfo.InvariantCulture, UiFormats.RestRefusalKey, result.Refusal);
                Refuse(anchor, Strings.Has(key) ? Strings.Get(key) : Strings.Get(StringKeys.RestStateUnavailable));
                return;
            }
            if (result.Left)
            {
                RunFlow.Show(Context, _session);
                return;
            }
            _pane = _pane == RunFlowValues.OptionFlasks ? _pane : null;
            _paneSelected = null;
            _assign = new JObject();
            Render();
            if (_pane == null) FocusSoon(_options.FirstOrDefault(o => o.enabledSelf && o.name == anchor?.name) ?? _options.FirstOrDefault());
        }

        private void Leave()
        {
            try { _session.LeaveRest(); }
            catch (Exception e)
            {
                SaveFailed(e, Leave);
                return;
            }
            RunFlow.Show(Context, _session);
        }

        // ------------------------------------------------------------------ panes (W1i smith, W1j/W1k extract and install, the flask split, level up)

        public void OpenPane(string id)
        {
            _pane = id;
            _paneSelected = null;
            _assign = new JObject();
            Render();
            FocusSoon(_paneItems.FirstOrDefault(i => i.focusable) ?? Root.Q(UiNames.PaneBack));
        }

        private void ClosePane()
        {
            var was = _pane;
            _pane = null;
            _paneSelected = null;
            _assign = new JObject();
            Render();
            FocusSoon(_options.FirstOrDefault(o => o.name == was) ?? _options.FirstOrDefault());
        }

        private void RenderPane()
        {
            var keep = Context.Instance.Focus.Focused?.name;
            var items = Root.Q(UiNames.PaneItems);
            var receipt = Root.Q(UiNames.PaneReceipt);
            items.Clear();
            receipt.Clear();
            _paneItems.Clear();
            Root.Q<LocLabel>(UiNames.PaneTitle)?.SetResolved(Strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.RestOptionKey, _pane)));
            var status = Root.Q<LocLabel>(UiNames.PaneStatus);
            var commit = CommitButton;
            var back = Root.Q<LocButton>(UiNames.PaneBack);
            back.stringKey = _pane == RunFlowValues.OptionFlasks ? StringKeys.RestFlasksDone : StringKeys.RestBack;
            UiDom.Show(commit, _pane != RunFlowValues.OptionFlasks);
            switch (_pane)
            {
                case RunFlowValues.OptionSmith:
                {
                    status?.SetResolved(_view.Stones);
                    foreach (var item in _view.Smith)
                        AddItem(items, item.ItemRef, item.Name + Strings.Get(StringKeys.RestTagJoin) + item.Tier + Strings.Get(StringKeys.RestTagJoin) + item.Cost, item.Affordable);
                    var chosen = _view.Smith.FirstOrDefault(i => i.ItemRef == _paneSelected);
                    if (_view.Smith.Count == 0) Receipt(receipt, Strings.Get(StringKeys.RestSmithEmpty));
                    else if (chosen == null) Receipt(receipt, Strings.Get(StringKeys.RestSmithSelect));
                    else
                    {
                        Receipt(receipt, chosen.Name + Strings.Get(StringKeys.RestTagJoin) + chosen.Tier);
                        foreach (var change in chosen.Changes) Receipt(receipt, change);
                    }
                    commit.SetResolved(chosen?.Commit ?? Strings.Get(StringKeys.RestOptionSmith));
                    commit.EnableInClassList(UiClasses.ButtonReady, chosen != null && chosen.Affordable);
                    break;
                }
                case RunFlowValues.OptionExtract:
                case RunFlowValues.OptionInstall:
                {
                    var extract = _pane == RunFlowValues.OptionExtract;
                    var rows = extract ? _view.Extract : _view.Install;
                    status?.SetResolved(_view.Stones);
                    for (var i = 0; i < rows.Count; i++) AddItem(items, RowKey(rows[i], i), rows[i].Title + Strings.Get(StringKeys.RestTagJoin) + rows[i].Cost, rows[i].Affordable);
                    var chosen = rows.Where((r, i) => RowKey(r, i) == _paneSelected).FirstOrDefault();
                    Receipt(receipt, rows.Count == 0 ? Strings.Get(StringKeys.RestCardsEmpty) : chosen?.Receipt ?? Strings.Get(StringKeys.RestSmithSelect));
                    commit.SetResolved(Strings.Get(extract ? StringKeys.RestCardsExtract : StringKeys.RestCardsInstall));
                    commit.EnableInClassList(UiClasses.ButtonReady, chosen != null && chosen.Affordable);
                    break;
                }
                case RunFlowValues.OptionFlasks:
                {
                    status?.SetResolved(_view.Pool);
                    foreach (var row in _view.Flasks) AddStepper(items, row.Kind, Strings.Format(StringKeys.RestFlasksRow, new StringArgs().Add(UiPlaceholders.Name, row.Name).Add(UiPlaceholders.Count, row.Count)),
                        row.CanSub, row.CanAdd, StringKeys.RestFlasksLess, StringKeys.RestFlasksMore, step => MoveFlask(row.Kind, step));
                    Receipt(receipt, RestView.FlaskLine(Strings, _view.Flasks));
                    break;
                }
                case RunFlowValues.OptionLevel:
                {
                    var spent = _assign.Properties().Sum(p => (int)Js(p.Value));
                    var left = _view.Points - spent;
                    status?.SetResolved(Strings.Format(StringKeys.RestLevelPoints, new StringArgs().Add(UiPlaceholders.Count, left)));
                    foreach (var attr in _view.Attributes)
                    {
                        var added = (int)Js(_assign[attr.Id]);
                        AddStepper(items, attr.Id, Strings.Format(StringKeys.RestLevelRow, new StringArgs().Add(UiPlaceholders.Name, attr.Name)
                                .Add(UiPlaceholders.Value, attr.Value + added)), added > 0, left > 0, StringKeys.RestLevelMinus, StringKeys.RestLevelPlus,
                            step => AssignStep(attr.Id, step));
                    }
                    foreach (var line in RestView.LevelPreview(_session, Ui.Data, _assign)) Receipt(receipt, line);
                    commit.SetResolved(Strings.Get(StringKeys.RestLevelConfirm));
                    commit.EnableInClassList(UiClasses.ButtonReady, spent > 0);
                    break;
                }
            }
            if (keep != null)
            {
                var again = Root.Q(UiNames.RestPane).Query<VisualElement>().Where(e => e.name == keep && e.focusable).First();
                if (again != null) FocusSoon(again);
            }
        }

        private static double Js(JToken token) => token == null || token.Type == JTokenType.Null ? 0d : (double)token;

        private static string RowKey(RestMountRow row, int index) => index.ToString(CultureInfo.InvariantCulture);

        private void AddItem(VisualElement items, string key, string text, bool affordable)
        {
            var button = new LocButton { name = key };
            button.SetResolved(text);
            button.AddToClassList(UiClasses.PaneItem);
            button.EnableInClassList(UiClasses.Selected, key == _paneSelected);
            button.EnableInClassList(UiClasses.RestOptionUnavailable, !affordable);
            button.clicked += () =>
            {
                _paneSelected = key;
                Render();
            };
            items.Add(button);
            _paneItems.Add(button);
        }

        private void AddStepper(VisualElement items, string key, string text, bool canSub, bool canAdd, string lessKey, string moreKey, Action<int> step)
        {
            var row = new VisualElement();
            row.AddToClassList(UiClasses.PaneRow);
            var less = new LocButton(lessKey) { name = key + UiFormats.StepLessSuffix };
            less.AddToClassList(UiClasses.PaneStep);
            less.SetEnabled(canSub);
            less.clicked += () => step(-1);
            var label = new LocLabel();
            label.SetResolved(text);
            label.AddToClassList(UiClasses.PaneValue);
            label.AddToClassList(UiClasses.ValueText);
            var more = new LocButton(moreKey) { name = key + UiFormats.StepMoreSuffix };
            more.AddToClassList(UiClasses.PaneStep);
            more.SetEnabled(canAdd);
            more.clicked += () => step(1);
            row.Add(less);
            row.Add(label);
            row.Add(more);
            items.Add(row);
            _paneItems.Add(less);
            _paneItems.Add(more);
        }

        private static void Receipt(VisualElement receipt, string text)
        {
            var line = new LocLabel();
            line.SetResolved(text ?? string.Empty);
            line.AddToClassList(UiClasses.ReceiptLine);
            line.AddToClassList(UiClasses.ValueText);
            receipt.Add(line);
        }

        private void MoveFlask(string kind, int step) => Commit(() => _session.MoveFlask(kind, step), Context.Instance.Focus.Focused);

        private void AssignStep(string attributeId, int step)
        {
            var next = Math.Max(0, (int)Js(_assign[attributeId]) + step);
            if (next == 0) _assign.Remove(attributeId);
            else _assign[attributeId] = next;
            RenderPane();
        }

        private void CommitPane(VisualElement anchor)
        {
            switch (_pane)
            {
                case RunFlowValues.OptionSmith:
                    if (_paneSelected == null) { Refuse(anchor, Strings.Get(StringKeys.RestSmithSelect)); return; }
                    Commit(() => _session.Smith(_paneSelected), anchor);
                    return;
                case RunFlowValues.OptionExtract:
                case RunFlowValues.OptionInstall:
                {
                    var extract = _pane == RunFlowValues.OptionExtract;
                    var rows = extract ? _view.Extract : _view.Install;
                    var chosen = rows.Where((r, i) => RowKey(r, i) == _paneSelected).FirstOrDefault();
                    if (chosen == null) { Refuse(anchor, Strings.Get(StringKeys.RestSmithSelect)); return; }
                    Commit(() => extract ? _session.Extract(chosen.ItemRef, chosen.MountKey) : _session.Install(chosen.ItemRef, chosen.MountKey, chosen.InstanceId), anchor);
                    return;
                }
                case RunFlowValues.OptionLevel:
                    if (!_assign.Properties().Any()) { Refuse(anchor, Strings.Format(StringKeys.RestLevelPoints, new StringArgs().Add(UiPlaceholders.Count, _view.Points))); return; }
                    var assigned = (JObject)_assign.DeepClone();
                    Commit(() => _session.AssignPoints(assigned), anchor);
                    return;
            }
        }

        // ------------------------------------------------------------------ helpers

        private void Refuse(VisualElement anchor, string text)
        {
            if (anchor == null || string.IsNullOrEmpty(text)) return;
            Kit.Refusal.Show(anchor, Context.Overlay, text, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
        }

        private void FocusSoon(VisualElement element)
        {
            if (element == null) return;
            Context.Instance.LastFocused = element;
            Nav.FocusTop(element);
        }

        private void SaveFailed(Exception e, Action retry)
        {
            Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
            ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => retry() });
        }

        private void OpenPause() => Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = _session });

        public override bool HandleBack()
        {
            if (_pane != null)
            {
                ClosePane();
                return true;
            }
            OpenPause();
            return true;
        }

        public override bool HandleMenu()
        {
            OpenPause();
            return true;
        }

        public override void OnReturned()
        {
            if (_session?.Visit != null) Render();
        }

        // ------------------------------------------------------------------ review hooks (captures)

        /// <summary>Opens a pane and selects its first item (review captures).</summary>
        public void PaneForReview(string pane, bool selectFirst, int assignFirst)
        {
            OpenPane(pane);
            if (pane == RunFlowValues.OptionLevel && assignFirst > 0 && _view.Attributes.Count > 0)
            {
                _assign[_view.Attributes[0].Id] = Math.Min(assignFirst, _view.Points);
                RenderPane();
                return;
            }
            if (!selectFirst) return;
            _paneSelected = pane == RunFlowValues.OptionSmith ? _view.Smith.FirstOrDefault()?.ItemRef : (pane == RunFlowValues.OptionExtract ? _view.Extract : _view.Install).Count > 0 ? RowKey(null, 0) : null;
            Render();
        }
    }
}
