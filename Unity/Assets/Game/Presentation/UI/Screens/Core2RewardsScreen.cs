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
    /// <summary>
    /// The Core 2 spoils screen (VP-7, D-850; web main.js showGameProfileReward, components/gameProfileOffers.js, gameProfileLevelUp.js, rewardDoor.js): the reward door modal over the act map, drawn by
    /// <see cref="Core2SpoilsFlow"/>. First the level notices the fight earned (one sentence per level crossed; Continue reads them), then the offer: the cinders it paid, then one column per option (a
    /// card as the VP-3 face, anything else as a collectible face) with its NEW or TAKEN chip and its name as the radio. Confirm opens the review of the picked option: a card offers Add to deck (the
    /// deck rules may refuse, and the refusal shows at the control and in the status line) and Keep in collection, anything else Continue; Back returns to the pick; Skip leaves the spoils where
    /// they lie. A claim is saved at once by the session, so Continue from the title resumes at this screen. Leaving goes where the run stands: the map, the next act after a boss, or the run end.
    /// Escape opens the pause screen (or Back, under review).
    /// </summary>
    public sealed class Core2RewardsScreen : ScreenView
    {
        private readonly List<SpoilsOption> _options = new List<SpoilsOption>();
        private Core2RunSession _run;
        private Core2SpoilsFlow _flow;
        private CombatModal _modal;
        private RunHud _hud;
        private LocButton _confirm;
        private LocButton _back;
        private LocButton _keep;
        private LocLabel _status;
        private bool _bound;

        public Core2RunSession Run => _run;

        public Core2SpoilsFlow Flow => _flow;

        public IReadOnlyList<SpoilsOption> Options => _options;

        public LocButton ConfirmButton => _confirm;

        public LocButton BackButton => _back;

        public LocButton KeepButton => _keep;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            _run = (args as Core2RunArgs)?.Session ?? Ui.Core2Session;
            if (_run == null || !_run.HasPendingReward)
            {
                Debug.LogError(UiMessages.NoReward);
                return;
            }
            Ui.Core2Session = _run;
            Ui.Session = null;
            _flow = new Core2SpoilsFlow(_run, Ui.Data);
            _hud = Root.Q<RunHud>(UiNames.RunHud);
            if (_hud != null) _hud.Menu += OpenPause;
            _modal = Root.Q<CombatModal>(UiNames.SpoilsModal);
            UiDom.Show(_modal.CloseButton, false);
            ApplyBackground();
            Render();
            _bound = true;
        }

        private void ApplyBackground()
        {
            var element = Root.Q(UiNames.MapBackground);
            if (element == null) return;
            var id = StringTable.Fill(Ui.Data.Components.Climb.MapBackground, new StringArgs().Add(UiPlaceholders.Region, _run.Region));
            Ui.ApplyBackground(element, Ui.Art != null && Ui.Art.Get(id) != null ? id : Ui.Data.Components.CombatFallbackBackground);
        }

        // ------------------------------------------------------------------ render

        /// <summary>Draws the stage the flow stands at; focus goes to the control a player most likely wants next.</summary>
        public void Render()
        {
            if (_flow == null) return;
            _hud?.Bind(RunHudView.Build(_run, Ui.Data));
            if (_hud?.Portrait != null) Ui.ApplyBackground(_hud.Portrait, _run.Portrait);
            _options.Clear();
            _confirm = _back = _keep = null;
            _status = null;
            _modal.Reset();
            _modal.SetHead(_flow.Eyebrow, _flow.Title, _flow.Status);
            var body = _modal.Body;
            VisualElement focus;
            if (_flow.Stage == Core2SpoilsStage.Notices)
            {
                RenderNotices(body);
                _confirm = Foot(_flow.ConfirmLabel, UiClasses.ButtonPrimary, OnConfirm);
                focus = _confirm;
            }
            else
            {
                RenderOffer(body);
                if (_flow.Stage == Core2SpoilsStage.Review)
                {
                    _back = Foot(_flow.BackLabel, UiClasses.ButtonBack, OnBack);
                    if (_flow.KeepLabel.Length > 0) _keep = Foot(_flow.KeepLabel, null, OnKeep);
                    _confirm = Foot(_flow.ConfirmLabel, UiClasses.ButtonPrimary, OnConfirm);
                    focus = _confirm;
                }
                else
                {
                    _back = Foot(_flow.BackLabel, UiClasses.ButtonBack, OnSkip);
                    _confirm = Foot(_flow.ConfirmLabel, UiClasses.ButtonPrimary, OnConfirm);
                    var picked = _options.FirstOrDefault(o => o.Index == _flow.Selected);
                    focus = picked != null ? picked.PickButton : _options.FirstOrDefault(o => !_flow.Options.First(x => x.Index == o.Index).Taken)?.PickButton ?? (VisualElement)_confirm;
                }
            }
            _confirm.EnableInClassList(SpoilsClasses.ConfirmOff, _flow.ConfirmRefusal.Length > 0);
            // While the screen is being bound the navigator focuses the remembered control itself; later draws move focus to where the player goes next.
            if (_bound) FocusSoon(focus);
            else Context.Instance.LastFocused = focus;
        }

        private void RenderNotices(VisualElement body)
        {
            var heading = new LocLabel { pickingMode = PickingMode.Ignore };
            heading.AddToClassList(SpoilsClasses.Heading);
            heading.AddToClassList(UiClasses.Upper);
            heading.SetResolved(_flow.ProgressionHeading);
            body.Add(heading);
            foreach (var notice in _flow.Notices)
            {
                var row = new VisualElement();
                row.AddToClassList(SpoilsClasses.Notice);
                var label = new LocLabel { pickingMode = PickingMode.Ignore };
                label.AddToClassList(SpoilsClasses.NoticeLabel);
                label.AddToClassList(UiClasses.ValueText);
                label.SetResolved(notice.Sentence);
                row.Add(label);
                body.Add(row);
            }
        }

        private void RenderOffer(VisualElement body)
        {
            if (_flow.CindersTitle != null)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList(SpoilsClasses.Cinders);
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList(SpoilsClasses.CindersIcon);
                var texture = Ui.Icon(SpoilsIcons.Cinders);
                if (texture != null) icon.style.backgroundImage = new StyleBackground(texture);
                var texts = new VisualElement { pickingMode = PickingMode.Ignore };
                texts.AddToClassList(SpoilsClasses.CindersTexts);
                texts.Add(Text(_flow.CindersTitle, SpoilsClasses.CindersTitle, UiClasses.ValueText));
                texts.Add(Text(_flow.CindersBody, SpoilsClasses.CindersBody, UiClasses.ValueText));
                var taken = new LocLabel { pickingMode = PickingMode.Ignore };
                taken.AddToClassList(UiClasses.Chip);
                taken.SetResolved(Strings.Get(StringKeys.SpoilsChipTaken));
                row.Add(icon);
                row.Add(texts);
                row.Add(taken);
                body.Add(row);
            }
            body.Add(Text(_flow.Note, SpoilsClasses.Note, UiClasses.ValueText));
            var strip = new VisualElement();
            strip.AddToClassList(SpoilsClasses.Strip);
            strip.EnableInClassList(SpoilsClasses.StripReview, _flow.Stage == Core2SpoilsStage.Review);
            foreach (var option in _flow.Options)
            {
                var column = new SpoilsOption(option, Ui, option.Index == _flow.Selected);
                column.Chosen += OnOption;
                _options.Add(column);
                strip.Add(column);
            }
            body.Add(strip);
            _status = Text(_flow.StatusLine, SpoilsClasses.Status, UiClasses.ValueText);
            body.Add(_status);
        }

        private static LocLabel Text(string value, params string[] classes)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) label.AddToClassList(c);
            label.SetResolved(value ?? string.Empty);
            return label;
        }

        private LocButton Foot(string text, string kind, Action press)
        {
            var button = new LocButton();
            if (kind != null) button.AddToClassList(kind);
            button.SetResolved(text);
            button.clicked += press;
            _modal.Foot.Add(button);
            return button;
        }

        // ------------------------------------------------------------------ intents

        private void OnOption(int index, VisualElement anchor) => Press(() => _flow.Select(index), anchor, index);

        private void OnConfirm()
        {
            var anchor = _confirm;
            Press(_flow.Confirm, anchor);
        }

        private void OnKeep() => Press(_flow.Keep, _keep);

        private void OnBack() => Press(_flow.Back, _back);

        private void OnSkip() => Press(_flow.Skip, _back);

        /// <summary>
        /// One press: the flow's step runs (a claim is saved by the session), a refusal shows at the control that was pressed with the status line repeating it, a step that left the door goes on to
        /// the screen the run stands at, any other step redraws. A save that failed asks to retry.
        /// </summary>
        private void Press(Func<Core2SpoilsResult> step, VisualElement anchor, int focusOption = -1)
        {
            Core2SpoilsResult result;
            try { result = step(); }
            catch (Exception e)
            {
                SaveFailed(e, () => Press(step, anchor, focusOption));
                return;
            }
            if (!result.Done)
            {
                Refuse(anchor, result.Refusal);
                if (_status != null) _status.SetResolved(result.Refusal);
                return;
            }
            if (result.Left)
            {
                RunFlow.Show(Context, _run);
                return;
            }
            Render();
            if (focusOption >= 0) FocusSoon(_options.FirstOrDefault(o => o.Index == focusOption)?.PickButton);
        }

        private void SaveFailed(Exception e, Action retry)
        {
            Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
            _flow.Refresh();
            Render();
            ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => retry() });
        }

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

        // ------------------------------------------------------------------ pause and back

        private void OpenPause() => Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = _run });

        /// <summary>Escape: the review goes back to the pick, anything else opens the pause screen (the door has no close).</summary>
        public override bool HandleBack()
        {
            if (_flow != null && _flow.Stage == Core2SpoilsStage.Review)
            {
                OnBack();
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
            if (_flow == null || _run == null) return;
            if (!_run.HasPendingReward || _run.RunOver)
            {
                RunFlow.Show(Context, _run);
                return;
            }
            _flow.Refresh();
            Render();
        }
    }
}
