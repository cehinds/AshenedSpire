using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Nodes;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Core 2 event screen (VP-9, D-960; web ui/screens/event.js mountEvent, ui/screens/dialogue.js mountDialogue, docs/qa/web-screens/event-*): a one-off event is the choice body under the
    /// painting band, with EVENT, the gold name and the status in the head, the narrative with its art well beside the responses (each a card with its label, "Cannot afford" or the HOLD mark of a
    /// binding one) and a full-width Continue that waits for a response and says "Steel yourself" before a fight; a quest step is spoken, with the player and the speaker over a context band (the
    /// line, the responses on the last beat) and the footer's Back, Skip speech and Continue. A response that cannot be paid refuses at the control; a binding one asks first (held); the result
    /// replaces the responses; Continue leaves (it enters the fight the response started). Every press goes through <see cref="Core2EventFlow"/>; Escape opens the pause screen.
    /// </summary>
    public sealed class Core2EventScreen : ScreenView
    {
        private readonly Dictionary<string, LocButton> _responses = new Dictionary<string, LocButton>();
        private Core2RunSession _run;
        private Core2EventFlow _flow;
        private CombatModal _door;
        private LocButton _continue;
        private LocButton _back;
        private LocButton _skip;
        private LocLabel _reason;
        private bool _bound;

        public Core2RunSession Run => _run;

        public Core2EventFlow Flow => _flow;

        public IReadOnlyDictionary<string, LocButton> ResponseButtons => _responses;

        public LocButton ContinueButton => _continue;

        public LocButton BackButton => _back;

        public LocButton SkipButton => _skip;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            _run = (args as Core2RunArgs)?.Session ?? Ui.Core2Session;
            if (_run == null || _run.EventId == null)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            Ui.Core2Session = _run;
            _flow = new Core2EventFlow(_run, Ui.Data);
            _door = Root.Q<CombatModal>(UiNames.CeventDoor);
            _back = Root.Q<LocButton>(UiNames.CeventBack);
            _skip = Root.Q<LocButton>(UiNames.CeventSkip);
            var footContinue = Root.Q<LocButton>(UiNames.CeventContinue);
            _reason = Root.Q<LocLabel>(UiNames.CeventReason);
            _back.SetResolved(_flow.BackLabel);
            _skip.SetResolved(_flow.SkipLabel);
            _back.clicked += () => Press(_flow.Back, _back);
            _skip.clicked += () => Press(_flow.Skip, _skip);
            footContinue.clicked += () => Press(_flow.Continue, footContinue);
            BoardHud.Wire(Context, _run, OpenPause);
            Root.Q(UiNames.EventPage).EnableInClassList(EventClasses.PageDialogue, _flow.IsDialogue);
            BoardHud.SetImage(Root.Q(UiNames.CeventScene), Ui.Texture(_flow.SceneArtId));
            Render();
            _bound = true;
        }

        // ------------------------------------------------------------------ render

        /// <summary>Draws the event as it stands: the choice body or the spoken step; focus goes to the first open response, else Continue.</summary>
        public void Render()
        {
            if (_flow == null) return;
            BoardHud.Render(Context, _run);
            _responses.Clear();
            if (_flow.IsDialogue) RenderDialogue();
            else RenderChoiceBody();
        }

        private void RenderChoiceBody()
        {
            _door.Reset();
            _door.SetHead(_flow.Eyebrow, _flow.Title, _flow.Status);
            var columns = new VisualElement();
            columns.AddToClassList(EventClasses.Columns);
            var narrative = new VisualElement();
            narrative.AddToClassList(EventClasses.Narrative);
            var well = new VisualElement { pickingMode = PickingMode.Ignore };
            well.AddToClassList(EventClasses.ArtWell);
            BoardHud.SetImage(well, Ui.Texture(_flow.ArtId));
            narrative.Add(well);
            foreach (var paragraph in _flow.Paragraphs) narrative.Add(Text(paragraph, EventClasses.Prose));
            columns.Add(narrative);
            var responses = new VisualElement();
            responses.AddToClassList(EventClasses.Responses);
            if (_flow.Resolved) responses.Add(Text(_flow.ResultText, EventClasses.Prose, EventClasses.Result));
            else foreach (var response in _flow.Responses) responses.Add(Response(response));
            columns.Add(responses);
            _door.Body.Add(columns);
            _continue = new LocButton();
            _continue.AddToClassList(EventClasses.Continue);
            _continue.SetResolved(_flow.ContinueText);
            _continue.EnableInClassList(EventClasses.Off, !_flow.ContinueEnabled);
            _continue.EnableInClassList(UiClasses.ButtonReady, _flow.ContinueEnabled);
            _continue.clicked += () => Press(_flow.Continue, _continue);
            _door.Foot.Add(_continue);
            var reason = Text(_flow.ContinueReason, EventClasses.ContinueReason);
            UiDom.Show(reason, !string.IsNullOrEmpty(_flow.ContinueReason));
            _door.Foot.Add(reason);
            // The one-off keeps Continue at the foot of the door; the footer of the spoken step is hidden by the page.
            Focus(_flow.Resolved ? _continue : (VisualElement)_responses.Values.FirstOrDefault(b => !b.ClassListContains(EventClasses.ResponseOff)) ?? _continue);
        }

        private void RenderDialogue()
        {
            var background = Root.Q(UiNames.CeventBackground);
            if (!SetArt(background, _flow.BackgroundId)) SetArt(background, Ui.Data.Nodes.EventFallbackBackground);
            SetArt(Root.Q(UiNames.CeventPlayer), _run.Portrait);
            var npc = Root.Q(UiNames.CeventNpc);
            SetArt(npc, _flow.SpeakerArtId);
            Root.Q<LocLabel>(UiNames.CeventPlayerName).SetResolved(_flow.PlayerName);
            Root.Q<LocLabel>(UiNames.CeventNpcName).SetResolved(_flow.SpeakerName);
            Root.Q(UiNames.CeventPlayer).parent.EnableInClassList(EventClasses.StageSideListening, !_flow.Resolved);
            npc.parent.EnableInClassList(EventClasses.StageSideListening, _flow.Resolved);
            Root.Q<LocLabel>(UiNames.CeventTitle).SetResolved(_flow.Title);
            var caption = Root.Q<LocLabel>(UiNames.CeventCaption);
            caption.SetResolved(_flow.Caption);
            caption.EnableInClassList(EventClasses.CaptionPlayer, _flow.CaptionIsPlayer);
            var list = Root.Q(UiNames.CeventResponses);
            list.Clear();
            foreach (var response in _flow.Responses) list.Add(Response(response));
            _back.SetEnabled(_flow.BackEnabled);
            _skip.SetEnabled(_flow.SkipEnabled);
            var cont = Root.Q<LocButton>(UiNames.CeventContinue);
            cont.SetResolved(_flow.ContinueText);
            cont.EnableInClassList(EventClasses.Off, !_flow.ContinueEnabled);
            cont.EnableInClassList(UiClasses.ButtonReady, _flow.ContinueEnabled);
            _continue = cont;
            _reason.SetResolved(_flow.ContinueReason);
            UiDom.Show(_reason, !string.IsNullOrEmpty(_flow.ContinueReason));
            Focus(_responses.Values.FirstOrDefault(b => !b.ClassListContains(EventClasses.ResponseOff)) ?? (_flow.ContinueEnabled ? cont : _back.enabledSelf ? _back : (VisualElement)cont));
        }

        private VisualElement Response(Core2EventResponseView response)
        {
            var row = new LocButton { userData = response.ChoiceId };
            row.AddToClassList(EventClasses.Response);
            row.EnableInClassList(EventClasses.ResponseOff, !response.Affordable);
            var texts = new VisualElement { pickingMode = PickingMode.Ignore };
            texts.AddToClassList(EventClasses.ResponseTexts);
            texts.Add(Text(response.Label, EventClasses.ResponseLabel));
            if (!string.IsNullOrEmpty(response.Tag)) texts.Add(Text(response.Tag, EventClasses.ResponseTag));
            row.Add(texts);
            if (response.Binding && response.Affordable) row.Add(Text(_flow.HoldLabel, EventClasses.ResponseHold, UiClasses.Upper));
            row.clicked += () => PressResponse(response, row);
            _responses[response.ChoiceId] = row;
            return row;
        }

        // ------------------------------------------------------------------ intents

        /// <summary>A response: refused at the control when it cannot be paid; a binding one opens its question (held) first; any other is taken.</summary>
        public void PressResponse(Core2EventResponseView response, VisualElement anchor)
        {
            if (_flow == null || response == null) return;
            if (_flow.NeedsConfirm(response.ChoiceId))
            {
                ConfirmRequest.Open(Nav, new ConfirmRequest
                {
                    ConfirmId = ConfirmIds.EventChooseBinding,
                    Target = response.Label,
                    Args = new StringArgs().Add(NodePlaceholders.Preview, response.ResultText),
                    OnConfirm = () => Press(() => _flow.Choose(response.ChoiceId), anchor),
                });
                return;
            }
            Press(() => _flow.Choose(response.ChoiceId), anchor);
        }

        /// <summary>One press: the flow's step runs (Continue is saved by the session), a refusal shows at the control that asked, a step that left the event goes on to where the run stands, any other redraws.</summary>
        private void Press(Func<Core2EventStep> step, VisualElement anchor)
        {
            Core2EventStep result;
            try { result = step(); }
            catch (Exception e)
            {
                SaveFailed(e, () => Press(step, anchor));
                return;
            }
            if (!result.Done)
            {
                Refuse(anchor, result.Refusal);
                return;
            }
            if (result.Left)
            {
                RunFlow.Show(Context, _run);
                return;
            }
            Render();
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

        // ------------------------------------------------------------------ helpers

        private bool SetArt(VisualElement element, string assetId)
        {
            var texture = assetId != null ? Ui.Texture(assetId) : null;
            BoardHud.SetImage(element, texture);
            return texture != null;
        }

        private static LocLabel Text(string value, params string[] classes)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) label.AddToClassList(c);
            label.SetResolved(value ?? string.Empty);
            return label;
        }

        private void Focus(VisualElement element)
        {
            if (element == null) return;
            Context.Instance.LastFocused = element;
            if (_bound) Nav.FocusTop(element);
        }

        // ------------------------------------------------------------------ pause and back

        private void OpenPause() => Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = _run });

        public override bool HandleBack()
        {
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
            if (_run.EventId == null || _run.RunOver)
            {
                RunFlow.Show(Context, _run);
                return;
            }
            _flow.Refresh();
            Render();
        }
    }
}
