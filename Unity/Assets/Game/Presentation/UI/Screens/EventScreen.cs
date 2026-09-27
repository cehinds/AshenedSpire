using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Nodes;
using Ashen.App.Ui;
using Ashen.Domain.Events;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-11 event (W1u) and W4c dialogue (a quest chain's step, which has a speaker; US-10.1–10.3, AF-07), bound to
    /// <see cref="EventSession"/>. W1u: the narrative (reused art and the event's text) beside the responses; the head says
    /// "Choose a response", "n of m available" or "Resolved"; no close control and no Back. W4c: the player's figure and the
    /// speaker's (the listener dimmed) above an opaque context band with the speaker, the line and the responses. A
    /// response that cannot be taken reads its requirement (a price, or the earlier choices it needs) and is refused at
    /// the press; any other opens its review door with the exact effect preview (hold-to-confirm for a binding one). Once
    /// a response is taken its result sentence replaces the list and Continue is enabled: "Steel yourself" when it
    /// started a fight (handed to W-07), else back to the map. Escape and pad Start open W-20.
    /// </summary>
    public sealed class EventScreen : NodeScreen
    {
        private readonly List<Button> _responses = new List<Button>();
        private EventSession _event;
        private EventViewState _view;
        private Shell _shell;
        private LocLabel _status;
        private bool _dialogue;

        public EventSession Event => _event;
        public EventViewState View => _view;
        public IReadOnlyList<Button> Responses => _responses;
        public bool IsDialogue => _dialogue;
        public Button ContinueButton => _dialogue ? Root.Q<LocButton>(UiNames.DialogueContinue) : _shell?.PrimaryButton;

        protected override void Begin()
        {
            _dialogue = Context.Def.Id == ScreenIds.Dialogue;
            _event = EventSession.Start(Session);
            if (_dialogue)
            {
                Root.Q<LocButton>(UiNames.DialogueContinue).clicked += Continue;
                _status = Root.Q<LocLabel>(UiNames.EventStatus);
                return;
            }
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.SetFooter(null, NodeStringKeys.NodesEventContinue);
            _shell.Primary += Continue;
            _status = new LocLabel();
            _status.name = UiNames.EventStatus;
            _status.AddToClassList(NodeClasses.EventStatus);
            _status.AddToClassList(UiClasses.ValueText);
            var header = _shell.Q(UiNames.ShellHeader);
            header?.Insert(Math.Max(0, header.IndexOf(_shell.ExitButton)), _status);
        }

        public override void Render()
        {
            if (_event == null || Session.EventId == null) return;
            var keep = Context.Instance.Focus.Focused?.name;
            _view = EventView.Build(_event, Ui.Data, Session.Region);
            RenderHud();
            var background = Root.Q(_dialogue ? UiNames.DialogueBackground : UiNames.EventBackground);
            if (!Art(background, _view.BackgroundId)) Art(background, Ui.Data.Nodes.EventFallbackBackground);
            _status?.SetResolved(_view.StatusText);
            if (_dialogue)
            {
                Root.Q<LocLabel>(UiNames.DialogueTitle)?.SetResolved(_view.DialogueTitle ?? _view.Title);
                Root.Q<LocLabel>(UiNames.DialogueLine)?.SetResolved(_view.Text);
                Root.Q<LocLabel>(UiNames.SpeakerName)?.SetResolved(_view.SpeakerName ?? _view.Title);
                var player = Root.Q(UiNames.PlayerFigure);
                Art(player, Session.Portrait);
                player?.EnableInClassList(NodeClasses.DialogueFigureListening, !_view.Resolved);
                var npc = Root.Q(UiNames.NpcFigure);
                if (!Art(npc, _view.SpeakerArtId)) Art(npc, _view.ArtId);
            }
            else
            {
                _shell.TitleLabel?.SetResolved(_view.Title);
                Art(Root.Q(UiNames.EventArt), _view.ArtId);
                Root.Q<LocLabel>(UiNames.EventText)?.SetResolved(_view.Text);
            }
            RenderResponses();
            UiDom.Show(Root.Q(UiNames.ResponseList), !_view.Resolved);
            UiDom.Show(Root.Q(UiNames.EventResult), _view.Resolved);
            Root.Q<LocLabel>(UiNames.ResultText)?.SetResolved(_view.ResultText ?? string.Empty);
            var note = string.Join(Strings.Get(NodeStringKeys.NodesEffectJoin), new[] { _view.SwapText, _view.FightNote }.Where(s => !string.IsNullOrEmpty(s)));
            var noteLabel = Root.Q<LocLabel>(UiNames.ResultNote);
            noteLabel?.SetResolved(note);
            UiDom.Show(noteLabel, note.Length > 0);
            var cont = ContinueButton;
            if (cont is LocButton loc) loc.SetResolved(_view.ContinueText);
            cont?.SetEnabled(_view.ContinueEnabled);
            cont?.EnableInClassList(UiClasses.ButtonReady, _view.ContinueEnabled);
            if (keep != null)
            {
                var again = Root.Q(keep);
                if (again != null && again.focusable && again.enabledInHierarchy && UiDom.IsShown(again)) again.Focus();
                else FocusSoon(_view.Resolved ? cont : _responses.FirstOrDefault());
            }
        }

        private void RenderResponses()
        {
            var list = Root.Q(UiNames.ResponseList);
            list.Clear();
            _responses.Clear();
            if (_view.Resolved) return;
            foreach (var response in _view.Responses)
            {
                var row = new Button { name = response.ChoiceId };
                row.AddToClassList(UiClasses.Button);
                row.AddToClassList(UiClasses.Touch);
                row.AddToClassList(NodeClasses.ResponseRow);
                row.EnableInClassList(NodeClasses.ResponseRowLocked, !response.Available);
                var label = new LocLabel { pickingMode = PickingMode.Ignore };
                label.AddToClassList(NodeClasses.ResponseLabel);
                label.AddToClassList(UiClasses.ValueText);
                label.SetResolved(response.Label);
                row.Add(label);
                var preview = new LocLabel { pickingMode = PickingMode.Ignore };
                preview.AddToClassList(NodeClasses.ResponsePreview);
                preview.AddToClassList(UiClasses.ValueText);
                preview.SetResolved(response.Preview);
                row.Add(preview);
                var captured = response;
                row.clicked += () => Press(captured, row);
                list.Add(row);
                _responses.Add(row);
            }
        }

        /// <summary>A response press: refused with its requirement when it cannot be taken; else its review door (hold when binding).</summary>
        public void Press(EventResponseView response, VisualElement anchor)
        {
            if (_view.Resolved || response == null) return;
            if (!response.Available)
            {
                Refuse(anchor, response.ReasonText);
                return;
            }
            ConfirmRequest.Open(Nav, new ConfirmRequest
            {
                ConfirmId = response.Binding ? ConfirmIds.EventChooseBinding : ConfirmIds.EventChoose,
                Target = response.Label,
                Args = new StringArgs().Add(NodePlaceholders.Preview, response.Preview),
                OnConfirm = () => Take(response.ChoiceId, anchor),
            });
        }

        /// <summary>Takes a response through the session (saved at once); a refusal shows at the control.</summary>
        public void Take(string choiceId, VisualElement anchor = null)
        {
            EventResult result = null;
            if (!Guard(() => result = _event.Choose(choiceId), () => Take(choiceId, anchor))) return;
            if (!result.Ok)
            {
                Render();
                Refuse(anchor ?? ContinueButton, NodeText.Refusal(Strings, result.Refusal));
                return;
            }
            Render();
            FocusSoon(ContinueButton);
        }

        /// <summary>Continue (disabled until a response is taken): the fight it started, else the map.</summary>
        public void Continue()
        {
            if (_view == null || !_view.ContinueEnabled)
            {
                Refuse(ContinueButton, _view?.StatusText);
                return;
            }
            if (!Guard(() => _event.Continue(), Continue)) return;
            Follow();
        }

        /// <summary>Opens the review door of a response by id (smoke tests and captures).</summary>
        public void PressForReview(string choiceId)
        {
            var i = _view?.Responses.FindIndex(r => r.ChoiceId == choiceId) ?? -1;
            if (i >= 0) Press(_view.Responses[i], i < _responses.Count ? _responses[i] : null);
        }
    }
}
