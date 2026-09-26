using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    public sealed class RewardsArgs
    {
        public RunSession Session;
    }

    /// <summary>
    /// W-08 rewards (W1t; US-11.1 to US-11.3), bound to the RunSession's reward door: the head says "{claimed} of {total}
    /// claimed" (no close control), the reward list has one row per reward the offer carries (cinders granted on
    /// arrival, the Smithing Stone already paid, the card offer, skill and class drafts, flask, armament, relic, each
    /// with its icon from the art catalog or its glyph), a blocked row offers Skip, and the claim-status column lists
    /// every row's state and the choice still waiting. A card offer or a draft opens the pick sub-state: its cards (or
    /// class-tree nodes), select then Confirm, Back, and Skip on the card offer. Every claim is saved at once (PF-06),
    /// so a reload resumes the same state. Continue is always pressable: gold while rows are open, green with
    /// "✓ All claimed" once every row is resolved; while rows are open it asks first, stating what the rewardCollect
    /// setting will do (take the rest, or leave it). The act map is a later build (D-058), so after Continue the climb
    /// returns to the title. Refusals show at the control that asked. Escape leaves the pick, else opens W-20.
    /// </summary>
    public sealed class RewardsScreen : ScreenView
    {
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<LocButton> _skips = new List<LocButton>();
        private readonly List<VisualElement> _picks = new List<VisualElement>();
        private RunSession _session;
        private Shell _shell;
        private LocLabel _status;
        private ScrollView _scroll;
        private RewardsViewState _view;
        private RewardRowView _picking;
        private string _selected;

        public RunSession Session => _session;
        public RewardsViewState View => _view;
        public IReadOnlyList<Button> Rows => _rows;
        public IReadOnlyList<LocButton> Skips => _skips;
        public IReadOnlyList<VisualElement> Picks => _picks;
        public LocButton ContinueButton => _shell?.PrimaryButton;
        public LocButton ConfirmButton => Root.Q<LocButton>(UiNames.PickConfirm);

        /// <summary>The row whose pick sub-state is open (null on the list).</summary>
        public RewardRowView Picking => _picking;

        public string Selected => _selected;

        public override string FocusMode => _picking != null ? UiValues.FocusPick : null;

        protected override void OnBind(object args)
        {
            _session = (args as RewardsArgs)?.Session ?? Ui.Session;
            if (_session == null || !_session.HasPendingReward)
            {
                Debug.LogError(UiMessages.NoReward);
                return;
            }
            Ui.Session = _session;
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.SetFooter(null, StringKeys.RewardsContinue);
            _shell.Primary += OnContinue;
            _status = new LocLabel();
            _status.name = UiNames.RewardsStatus;
            _status.AddToClassList(UiClasses.RewardsStatus);
            _status.AddToClassList(UiClasses.ValueText);
            var header = _shell.Q(UiNames.ShellHeader);
            header?.Insert(Math.Max(0, header.IndexOf(_shell.ExitButton)), _status);
            _scroll = Root.Q<ScrollView>(UiNames.RewardScroll);
            Root.Q<LocButton>(UiNames.PickBack).clicked += LeavePick;
            Root.Q<LocButton>(UiNames.PickSkip).clicked += SkipPick;
            var confirm = ConfirmButton;
            confirm.clicked += () => ConfirmPick(confirm);
            ApplyBackground();
            try { Render(); }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
                ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => Render() });
                return;
            }
            Context.Instance.LastFocused = FirstOpenRow() ?? (VisualElement)ContinueButton;
        }

        private StringTable Strings => Ui.Data.Strings;

        /// <summary>The battlefield the spoils were won on, under the scrim (W1t is a door over the fight).</summary>
        private void ApplyBackground()
        {
            var components = Ui.Data.Components;
            var region = _session.Content.RegionOf(_session.EncounterId);
            var id = StringTable.Fill(components.CombatBackground, new StringArgs().Add(UiPlaceholders.Region, region));
            var element = Root.Q(UiNames.RewardsBackground);
            if (element != null) Ui.ApplyBackground(element, Ui.Art != null && Ui.Art.Get(id) != null ? id : components.CombatFallbackBackground);
        }

        // ------------------------------------------------------------------ render

        /// <summary>Rebuild the list from the door; focus stays on the same row (or its successor) across redraws.</summary>
        public void Render()
        {
            var keep = FocusedKey();
            _view = RewardsView.Build(_session, Ui.Data);
            if (_view == null) return;
            _shell.TitleLabel?.SetResolved(_view.Title);
            _status.SetResolved(_view.StatusText);
            RenderRows();
            RenderClaims();
            var cont = ContinueButton;
            cont.SetResolved(Strings.Get(_view.AllResolved ? StringKeys.RewardsContinueDone : StringKeys.RewardsContinue));
            cont.EnableInClassList(UiClasses.ButtonReady, _view.AllResolved);
            Root.Q<LocLabel>(UiNames.RewardsHint)?.SetResolved(_view.HintText);
            var picking = _picking != null;
            UiDom.Show(Root.Q(UiNames.RewardsBody), !picking);
            UiDom.Show(Root.Q(UiNames.RewardPick), picking);
            UiDom.Show(_shell.Footer, !picking);
            if (picking) RenderPick();
            if (keep != null) RestoreFocus(keep);
        }

        private void RenderRows()
        {
            var list = Root.Q(UiNames.RewardList);
            list.Clear();
            _rows.Clear();
            _skips.Clear();
            foreach (var row in _view.Rows)
            {
                var line = new VisualElement();
                line.AddToClassList(UiClasses.RewardLine);
                var button = new Button { name = row.Key };
                button.AddToClassList(UiClasses.Button);
                button.AddToClassList(UiClasses.Touch);
                button.AddToClassList(UiClasses.RewardRow);
                button.AddToClassList(UiClasses.RewardRowStatePrefix + row.State);
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList(UiClasses.RewardIcon);
                var texture = row.ArtId != null && Ui.Art != null ? Ui.Art.Get(row.ArtId) : null;
                if (texture != null) icon.style.backgroundImage = new StyleBackground(texture);
                else
                {
                    var glyph = new LocLabel(Strings.Has(row.GlyphKey) ? row.GlyphKey : StringKeys.GlyphRelic) { pickingMode = PickingMode.Ignore };
                    glyph.AddToClassList(UiClasses.RewardGlyph);
                    icon.Add(glyph);
                }
                button.Add(icon);
                var texts = new VisualElement { pickingMode = PickingMode.Ignore };
                texts.AddToClassList(UiClasses.RewardTexts);
                texts.Add(Text(row.Title, UiClasses.RewardTitle, UiClasses.ValueText));
                texts.Add(Text(row.Body, UiClasses.RewardBody, UiClasses.ValueText));
                button.Add(texts);
                button.Add(Text(row.StateText, UiClasses.RewardChip, UiClasses.HeaderText));
                button.SetEnabled(row.Pending);
                var captured = row;
                button.clicked += () => OnRow(captured, button);
                button.RegisterCallback<FocusInEvent>(_ => _scroll?.ScrollTo(line));
                line.Add(button);
                _rows.Add(button);
                if (row.Blocked)
                {
                    var skip = new LocButton(StringKeys.RewardsSkip) { name = string.Format(CultureInfo.InvariantCulture, UiFormats.RewardSkipName, row.Key) };
                    skip.AddToClassList(UiClasses.RewardSkip);
                    skip.clicked += () => Skip(captured, skip);
                    skip.RegisterCallback<FocusInEvent>(_ => _scroll?.ScrollTo(line));
                    line.Add(skip);
                    _skips.Add(skip);
                }
                list.Add(line);
            }
        }

        private static LocLabel Text(string value, params string[] classes)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) label.AddToClassList(c);
            label.SetResolved(value ?? string.Empty);
            return label;
        }

        private void RenderClaims()
        {
            var list = Root.Q(UiNames.ClaimList);
            list.Clear();
            foreach (var row in _view.Rows)
            {
                var line = new VisualElement();
                line.AddToClassList(UiClasses.ClaimRow);
                line.AddToClassList(UiClasses.RewardRowStatePrefix + row.State);
                line.Add(Text(row.Title, UiClasses.ClaimName, UiClasses.ValueText));
                line.Add(Text(row.StateText, UiClasses.ClaimState, UiClasses.ValueText));
                list.Add(line);
            }
            Root.Q<LocLabel>(UiNames.ClaimRequired)?.SetResolved(_view.RequiredText);
        }

        private void RenderPick()
        {
            var row = _view.Rows.FirstOrDefault(r => r.Key == _picking.Key) ?? _picking;
            _picking = row;
            Root.Q<LocLabel>(UiNames.PickTitle)?.SetResolved(row.PickTitle ?? row.Title);
            var cards = Root.Q(UiNames.PickCards);
            cards.Clear();
            _picks.Clear();
            foreach (var pick in row.Picks)
            {
                VisualElement element;
                if (pick.IsNode)
                {
                    var tile = new LocButton();
                    tile.AddToClassList(UiClasses.PickNode);
                    tile.SetResolved(Strings.Format(StringKeys.RewardsPickNode, new StringArgs().Add(UiPlaceholders.Name, pick.Name).Add(UiPlaceholders.Label, pick.Text)));
                    var id = pick.Id;
                    tile.clicked += () => Select(id);
                    element = tile;
                }
                else
                {
                    var card = new CardView();
                    card.AddToClassList(UiClasses.PickCard);
                    card.Bind(new CardViewData
                    {
                        Name = pick.Name,
                        Text = pick.Text,
                        TypeLine = pick.TypeLine,
                        Rarity = pick.Rarity,
                        Upgraded = pick.Upgraded,
                        Costs = pick.Costs.Select(c => new CardCostData { Kind = c.Kind, Text = c.Text }).ToList(),
                    });
                    var id = pick.Id;
                    card.RegisterCallback<PointerUpEvent>(_ => Select(id));
                    card.RegisterCallback<NavigationSubmitEvent>(e =>
                    {
                        if (e.target != card) return;
                        Select(id);
                        e.StopPropagation();
                    });
                    element = card;
                }
                element.EnableInClassList(UiClasses.CardChosen, pick.Id == _selected);
                element.EnableInClassList(UiClasses.PickSelected, pick.Id == _selected);
                cards.Add(element);
                _picks.Add(element);
            }
            UiDom.Show(Root.Q(UiNames.PickSkip), row.Kind == UiValues.RewardCardKind);
            var confirm = ConfirmButton;
            confirm.EnableInClassList(UiClasses.ButtonReady, _selected != null);
        }

        // ------------------------------------------------------------------ focus (kept across redraws)

        private string FocusedKey()
        {
            var focused = Context.Instance.Focus.Focused;
            if (focused == null) return null;
            if (_picking != null)
            {
                var i = _picks.IndexOf(focused);
                return i >= 0 ? _picking.Picks[i].Id : focused.name;
            }
            return focused.name;
        }

        private void RestoreFocus(string key)
        {
            VisualElement target = null;
            if (_picking != null)
            {
                var i = _picking.Picks.FindIndex(p => p.Id == key);
                target = i >= 0 && i < _picks.Count ? _picks[i] : Root.Q(key);
            }
            else target = Root.Q(key);
            if (target != null && target.focusable && target.enabledInHierarchy) target.Focus();
            else FocusSoon(FirstOpenRow() ?? (VisualElement)ContinueButton);
        }

        private VisualElement FirstOpenRow()
        {
            for (var i = 0; i < _view.Rows.Count && i < _rows.Count; i++)
                if (_view.Rows[i].Pending) return _rows[i];
            return _skips.FirstOrDefault();
        }

        private void FocusSoon(VisualElement element)
        {
            if (element == null) return;
            Context.Instance.LastFocused = element;
            Nav.FocusTop(element);
        }

        // ------------------------------------------------------------------ intents

        private void OnRow(RewardRowView row, VisualElement anchor)
        {
            if (!row.Pending)
            {
                Refuse(anchor, row.Blocked ? RunFlowValues.RefusalBlocked : RunFlowValues.RefusalClaimed);
                return;
            }
            if (row.Picks.Count > 0)
            {
                OpenPick(row);
                return;
            }
            Claim(row.Key, null, anchor);
        }

        /// <summary>Opens the pick sub-state for a card offer or a draft (select, then Confirm).</summary>
        public void OpenPick(RewardRowView row)
        {
            _picking = row;
            _selected = null;
            Render();
            FocusSoon(_picks.FirstOrDefault() ?? (VisualElement)Root.Q(UiNames.PickBack));
        }

        /// <summary>Selects a card or node (the first press selects; Confirm takes it).</summary>
        public void Select(string id)
        {
            if (_picking == null) return;
            _selected = id;
            Render();
        }

        private void ConfirmPick(VisualElement anchor)
        {
            if (_picking == null) return;
            if (_selected == null)
            {
                Refuse(anchor, RunFlowValues.RefusalNoPick);
                return;
            }
            var key = _picking.Key;
            if (!Claim(key, _selected, anchor)) return;
            LeavePick();
        }

        private void SkipPick()
        {
            if (_picking == null) return;
            var row = _picking;
            if (!SkipRow(row.Key, Root.Q(UiNames.PickSkip))) return;
            LeavePick();
        }

        /// <summary>Back to the list; focus returns to the row that opened the pick (or the next open one).</summary>
        public void LeavePick()
        {
            var key = _picking?.Key;
            _picking = null;
            _selected = null;
            Render();
            var i = _view.Rows.FindIndex(r => r.Key == key);
            FocusSoon(i >= 0 && _view.Rows[i].Pending ? _rows[i] : FirstOpenRow() ?? (VisualElement)ContinueButton);
        }

        private void Skip(RewardRowView row, VisualElement anchor)
        {
            if (!SkipRow(row.Key, anchor)) return;
            Render();
            FocusSoon(FirstOpenRow() ?? (VisualElement)ContinueButton);
        }

        /// <summary>A claim through the session (saved at once); a refusal shows at the control and keeps focus there.</summary>
        private bool Claim(string key, string pick, VisualElement anchor)
        {
            RewardClaimResult result;
            try { result = _session.ClaimReward(key, pick); }
            catch (Exception e)
            {
                SaveFailed(e, () => Claim(key, pick, anchor));
                return false;
            }
            if (!result.Landed)
            {
                Refuse(anchor, result.Refusal);
                return false;
            }
            if (_picking == null)
            {
                Render();
                FocusSoon(FirstOpenRow() ?? (VisualElement)ContinueButton);
            }
            return true;
        }

        private bool SkipRow(string key, VisualElement anchor)
        {
            RewardClaimResult result;
            try { result = _session.SkipReward(key); }
            catch (Exception e)
            {
                SaveFailed(e, () => SkipRow(key, anchor));
                return false;
            }
            if (!result.Landed)
            {
                Refuse(anchor, result.Refusal);
                return false;
            }
            return true;
        }

        /// <summary>Continue: straight on once every row is resolved; otherwise the door states what the setting will do first.</summary>
        private void OnContinue()
        {
            if (_view == null) return;
            if (_view.AllResolved)
            {
                Finish();
                return;
            }
            ConfirmRequest.Open(Nav, new ConfirmRequest
            {
                ConfirmId = _view.Mode == UiValues.RewardCollectAuto ? ConfirmIds.RewardLeaveAuto : ConfirmIds.RewardLeaveManual,
                Target = _view.StatusText,
                OnConfirm = Finish,
            });
        }

        /// <summary>Sweep per the setting, close the door, save; the act map is planned, so the climb returns to the title.</summary>
        public void Finish()
        {
            try { _session.FinishRewards(); }
            catch (Exception e)
            {
                SaveFailed(e, Finish);
                return;
            }
            Ui.Session = null;
            Nav.Go(Ui.Data.Screens.IsBuilt(ScreenIds.ActMap) ? ScreenIds.ActMap : ScreenIds.Title, null, true);
        }

        private void SaveFailed(Exception e, Action retry)
        {
            Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
            Render();
            ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => retry() });
        }

        private void Refuse(VisualElement anchor, string code)
        {
            if (anchor == null || code == null) return;
            var key = string.Format(CultureInfo.InvariantCulture, UiFormats.RewardRefusalKey, code);
            Kit.Refusal.Show(anchor, Context.Overlay, Strings.Get(Strings.Has(key) ? key : StringKeys.RewardsRefusalRefused), Ui.Data.Tokens.Duration(TokenKeys.Refusal));
        }

        // ------------------------------------------------------------------ pause and back

        private void OpenPause() => Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = _session });

        public override bool HandleBack()
        {
            if (_picking != null)
            {
                LeavePick();
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
            if (_session != null && _session.HasPendingReward) Render();
        }

        // ------------------------------------------------------------------ test hooks (PlayMode smoke, captures)

        /// <summary>Opens the pick sub-state of the first row of a kind (review captures) and optionally selects a pick.</summary>
        public void PickForReview(string kind, int select)
        {
            var row = _view?.Rows.FirstOrDefault(r => r.Picks.Count > 0 && r.Pending && (kind == null || r.Kind == kind));
            if (row == null) return;
            OpenPick(row);
            if (select >= 0 && select < row.Picks.Count) Select(row.Picks[select].Id);
        }
    }
}
