using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.App.Nodes;
using Ashen.App.Ui;
using Ashen.Generated;
using SN = Ashen.Generated.StringKeys;
using SV = Ashen.Generated.EventScreenValues;

namespace Ashen.App.Run
{
    /// <summary>What a press of the event screen did: a refusal in the web's words (shown at the control; nothing changed), or whether the event has been left.</summary>
    public sealed class Core2EventStep
    {
        public string Refusal;

        /// <summary>The event is over: Continue was taken and the run stands where <see cref="Core2RunSession.Location"/> says (the map, or the fight the response started).</summary>
        public bool Left;

        public bool Done => Refusal == null;
    }

    /// <summary>
    /// The event screen's logic (VP-9, D-960; web ui/screens/event.js mountEvent, ui/screens/dialogue.js mountDialogue and ui/models/DialogueModel.js over ui/models/ChoiceBodyModel.js): the
    /// event (a one-off, drawn as the choice body: the narrative with its art beside the responses, the status, Continue) or a quest chain's step (spoken: the text divides into beats on blank
    /// lines, the player stands left and the speaker right, the responses are offered on the last beat only, Back, Skip speech and Continue move between beats and change nothing). A response
    /// that cannot be paid refuses at the control with the web's "Cannot afford"; a binding one is held to confirm (the screen asks); a response taken shows its result sentence and opens
    /// Continue, which is <see cref="Core2RunSession.FinishEvent"/> (it enters the fight the response started). Only a response and Continue change the run; the beat is never saved.
    /// Engine-free; the screen draws these fields and sends presses here.
    /// </summary>
    public sealed class Core2EventFlow
    {
        private readonly Core2RunSession _session;
        private readonly UiData _ui;
        private readonly StringTable _strings;
        private Core2EventViewState _view;
        private IReadOnlyList<string> _beats = Array.Empty<string>();
        private int _beat;
        private int _generation;

        public Core2EventFlow(Core2RunSession session, UiData ui)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _ui = ui;
            _strings = ui.Strings;
            Refresh();
        }

        /// <summary>The event the run stands at, as the web's screen words it; null when none is open.</summary>
        public Core2EventViewState View => _view;

        public bool Open => _view != null;

        public string EventId => _view?.EventId;

        /// <summary>A quest chain's step: spoken in the dialogue frame (a speaker stands opposite the player).</summary>
        public bool IsDialogue => _view != null && _view.HasSpeaker;

        public string Eyebrow => _view?.Eyebrow ?? string.Empty;

        public string Title => _view?.Title ?? string.Empty;

        /// <summary>The head's status ("Choose a response", "1 of 2 available", "Resolved").</summary>
        public string Status => _view?.Status ?? string.Empty;

        public bool Resolved => _view != null && _view.Resolved;

        /// <summary>The art well's painting (art.json 'event', the web's eventArtAsset); an event with no painting keeps the enemy art the node rules name for it (D-553), never a blank.</summary>
        public string ArtId => _view == null ? null : _ui.Art?.Event(_view.EventId) ?? _view.ArtId;

        /// <summary>The painting band above the choice body (scenePainting('event')).</summary>
        public string SceneArtId => _ui.Art?.Scene(EventArt.Scene);

        public string Glyph => _view?.Glyph;

        public string BackgroundId => _view?.BackgroundId;

        // ------------------------------------------------------------------ the narrative

        /// <summary>The event's text as authored, in its paragraphs (the choice body's prose).</summary>
        public IReadOnlyList<string> Paragraphs { get; private set; } = Array.Empty<string>();

        /// <summary>The dialogue's beats (the text divided on blank lines, whitespace folded); one empty beat when there is no text.</summary>
        public IReadOnlyList<string> Beats => _beats;

        public int Beat => _beat;

        public bool OnLastBeat => _beat >= _beats.Count - 1;

        public string SpeakerName => _view?.SpeakerName ?? string.Empty;

        public string PlayerName => _strings.Get(SN.Core2EventPlayer);

        /// <summary>The speaker's painting when art answers to the portrait key (ui/nodes.json dialogue.speakerArt), else null: the name plate stands in the slot.</summary>
        public string SpeakerArtId =>
            _view != null && _view.SpeakerPortraitAvailable ? NodeRules.Fill(_ui.Nodes.SpeakerArt, NodePlaceholders.Id, _view.SpeakerPortraitKey) : null;

        /// <summary>The caption under the figures: the current beat, or the response's own result once one is taken (on the player's side).</summary>
        public string Caption => Resolved ? _view.ResultText : _beats.Count > 0 ? _beats[_beat] : string.Empty;

        /// <summary>The caption is the player's (a result), not the speaker's.</summary>
        public bool CaptionIsPlayer => Resolved;

        /// <summary>The prose result of a one-off event once a response is taken.</summary>
        public string ResultText => _view?.ResultText ?? string.Empty;

        // ------------------------------------------------------------------ the responses

        /// <summary>The responses offered now: a one-off event shows them until one is taken, a dialogue on its last beat only.</summary>
        public bool ShowResponses => _view != null && !Resolved && (!IsDialogue || OnLastBeat);

        public IReadOnlyList<Core2EventResponseView> Responses => ShowResponses ? _view.Responses : Array.Empty<Core2EventResponseView>();

        public string HoldLabel => _strings.Get(SN.Core2EventHold);

        // ------------------------------------------------------------------ the foot

        public string BackLabel => _strings.Get(SN.Core2EventBack);

        public string SkipLabel => _strings.Get(SN.Core2EventSkip);

        public bool BackEnabled => IsDialogue && !Resolved && _beat > 0;

        public bool SkipEnabled => IsDialogue && !Resolved && !OnLastBeat;

        public string ContinueText => _view?.ContinueText ?? string.Empty;

        /// <summary>A one-off event's Continue is live once a response is taken; a dialogue's moves to the next beat until the last, where the responses are the way on.</summary>
        public bool ContinueEnabled => _view != null && (Resolved || (IsDialogue && !OnLastBeat));

        /// <summary>The sentence under a Continue that waits: "Choose a response first." (an event) or "Choose a reply first." (a dialogue).</summary>
        public string ContinueReason => ContinueEnabled ? string.Empty : _strings.Get(IsDialogue ? SN.Core2EventReplyReason : SN.Core2EventContinueReason);

        // ------------------------------------------------------------------ the view

        /// <summary>Reads the event again from the run (after a response the responses give way to the result).</summary>
        public void Refresh()
        {
            _view = Core2EventView.Build(_session, _strings, _ui.Nodes, _session.Region);
            if (_view == null)
            {
                _beats = Array.Empty<string>();
                Paragraphs = Array.Empty<string>();
                return;
            }
            Paragraphs = Divide(_view.Text, false);
            _beats = Divide(_view.Text, true);
            if (_beats.Count == 0) _beats = new[] { string.Empty };
            _beat = Math.Min(Math.Max(_beat, 0), _beats.Count - 1);
        }

        /// <summary>The text divided on blank lines; a beat folds its whitespace as the web's does, a paragraph keeps its lines.</summary>
        private static IReadOnlyList<string> Divide(string text, bool fold)
        {
            var parts = Regex.Split(text ?? string.Empty, SV.BeatBreakPattern);
            return parts.Select(p => fold ? Regex.Replace(p, SV.WhitespacePattern, SV.Space).Trim() : p.Trim()).Where(p => p.Length > 0).ToList();
        }

        // ------------------------------------------------------------------ presses

        /// <summary>A response was pressed and its confirmation (when it binds) answered yes: the response is taken. One that cannot be paid refuses with "Cannot afford".</summary>
        public Core2EventStep Choose(string choiceId)
        {
            if (_view == null) return Refuse(SN.Core2EventUnknown);
            if (Resolved || (IsDialogue && !OnLastBeat)) return new Core2EventStep();
            var response = _view.Responses.FirstOrDefault(r => r.ChoiceId == choiceId);
            if (response == null) return Refuse(SN.Core2EventUnknown);
            if (!response.Affordable) return new Core2EventStep { Refusal = response.Tag };
            var result = _session.ChooseEvent(choiceId);
            _generation++;
            Refresh();
            if (result.Error != null) return new Core2EventStep { Refusal = result.Error };
            if (!result.Ok) return new Core2EventStep { Refusal = NodeText.Refusal(_strings, result.Result.Refusal) };
            return new Core2EventStep();
        }

        /// <summary>Whether pressing this response asks first: only a binding one is held to confirm (the web's 'eventChoice' beat: stakes run when binding, none otherwise).</summary>
        public bool NeedsConfirm(string choiceId)
        {
            var response = _view?.Responses.FirstOrDefault(r => r.ChoiceId == choiceId);
            return response != null && response.Affordable && response.Binding;
        }

        /// <summary>
        /// The beat's generation (web DialogueModel state.generation): it moves on every line read, skipped to or answered, so a voice that reports the end of a line it started under can be told from a stale
        /// one. The screen has no voice; <see cref="SpeechEnded"/> is the hook it would call.
        /// </summary>
        public int Generation => _generation;

        /// <summary>
        /// speechEnded: a line's speech ended under <paramref name="generation"/>. A stale generation (the player moved on or skipped since), a resolved event or the last line changes nothing; a current one
        /// reads the next line. It never picks a response.
        /// </summary>
        public Core2EventStep SpeechEnded(int generation)
        {
            if (generation != _generation || _view == null || !IsDialogue || Resolved || OnLastBeat) return new Core2EventStep();
            _beat++;
            _generation++;
            return new Core2EventStep();
        }

        /// <summary>Back reads the line before this one again (a dialogue only; nothing changes).</summary>
        public Core2EventStep Back()
        {
            if (!BackEnabled) return new Core2EventStep();
            _beat--;
            _generation++;
            return new Core2EventStep();
        }

        /// <summary>Skip speech jumps to the last line and the responses.</summary>
        public Core2EventStep Skip()
        {
            if (!SkipEnabled) return new Core2EventStep();
            _beat = _beats.Count - 1;
            _generation++;
            return new Core2EventStep();
        }

        /// <summary>
        /// Continue: before a response a dialogue reads the next line (on the last the responses are the way on, so it refuses with its reason); a one-off event refuses until a response is taken;
        /// after a response it leaves the event (a fight the response started is entered where the run stands). Saved by the session.
        /// </summary>
        public Core2EventStep Continue()
        {
            if (_view == null) return Refuse(SN.Core2EventUnknown);
            if (!Resolved)
            {
                if (IsDialogue && !OnLastBeat)
                {
                    _beat++;
                    _generation++;
                    return new Core2EventStep();
                }
                return new Core2EventStep { Refusal = ContinueReason };
            }
            _session.FinishEvent();
            _view = null;
            return new Core2EventStep { Left = true };
        }

        private Core2EventStep Refuse(string key) => new Core2EventStep { Refusal = _strings.Get(key) };
    }
}
