using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Nodes;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using EK = Ashen.Generated.EventKeys;
using EV = Ashen.Generated.EventValues;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;
using SK = Ashen.Generated.ShopKeys;
using SN = Ashen.Generated.StringKeys;

namespace Ashen.App.Run
{
    /// <summary>One response of an event as the web's event screen draws it (ui/screens/event.js mountEvent): its label and, when the purse cannot pay it, the "Cannot afford" tag.</summary>
    public sealed class Core2EventResponseView
    {
        /// <summary>The response's id (what <see cref="Core2RunSession.ChooseEvent"/> takes) and its place among the event's authored responses.</summary>
        public string ChoiceId;

        public int Index;
        public string Label;

        /// <summary>The purse covers the response (the bar is enabled).</summary>
        public bool Affordable;

        /// <summary>The words beside a response that cannot be taken ("Cannot afford"); empty otherwise.</summary>
        public string Tag;

        /// <summary>The response carries a price (<c>requires</c>) and one that binds (a curse, a lasting loss): the screen holds the press to confirm.</summary>
        public bool Priced;

        public bool Binding;

        /// <summary>The response's own result sentence (the web's review door shows it under the question; the result step shows it once the response is taken).</summary>
        public string ResultText = string.Empty;
    }

    /// <summary>The event (or a quest chain's dialogue step) as the screen draws it before and after a response.</summary>
    public sealed class Core2EventViewState
    {
        public string EventId;

        /// <summary>The head's eyebrow ("Event") and the event's name.</summary>
        public string Eyebrow;

        public string Title;

        /// <summary>The narrative, as authored (paragraphs separated by blank lines).</summary>
        public string Text;

        /// <summary>The art well: an asset id when the data names one for the event (or a fight it can start), else the event's glyph.</summary>
        public string ArtId;

        public string Glyph;
        public string BackgroundId;

        /// <summary>The head's status: "Choose a response", "n of m available" or "Resolved".</summary>
        public string Status;

        /// <summary>A quest chain's step is spoken: the speaker, and the portrait key when art answers to it.</summary>
        public bool HasSpeaker;

        public string SpeakerName;
        public string SpeakerPortraitKey;
        public bool SpeakerPortraitAvailable;

        public IReadOnlyList<Core2EventResponseView> Responses = Array.Empty<Core2EventResponseView>();

        /// <summary>A response was taken: the responses give way to <see cref="ResultText"/> and Continue is live.</summary>
        public bool Resolved;

        public string ResultText;

        /// <summary>The foot's primary: "Continue", or "Steel yourself" when the response started a fight.</summary>
        public string ContinueText;

        public bool ContinueEnabled;

        /// <summary>The reason Continue is disabled ("Choose a response first."), empty once it is live.</summary>
        public string ContinueReason;

        public bool StartsFight;
    }

    /// <summary>
    /// The event of a Core 2 run as display data (WP-19; the seam the event screen reads): the web's event screen words — the eyebrow, the name, the text, the responses with
    /// their "Cannot afford" tag, the status, the result and the Continue that reads "Steel yourself" before a fight. The screen binds a response to
    /// <see cref="Core2RunSession.ChooseEvent"/> and Continue to <see cref="Core2RunSession.FinishEvent"/>. Engine-free.
    /// </summary>
    public static class Core2EventView
    {
        public static Core2EventViewState Build(Core2RunSession session, StringTable strings, NodeRules nodes = null, string regionId = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var eventId = session.EventId;
            if (eventId == null) return null;
            var d = session.Core.Events;
            var def = d.Events.Get(eventId);
            var open = session.EventDomainView();
            var all = EventChoices.WithHistory(d, def);
            var state = new Core2EventViewState
            {
                EventId = eventId, Eyebrow = strings.Get(SN.Core2EventEyebrow), Title = def.Str(K.Name), Text = def.Str(EK.Text) ?? string.Empty, Glyph = def.Str(EK.Art),
                Resolved = session.EventDone,
            };
            if (nodes != null)
            {
                state.ArtId = EventArt(d, nodes, def, all);
                state.BackgroundId = NodeRules.Fill(nodes.EventBackground, NodePlaceholders.Region, regionId) ?? nodes.EventFallbackBackground;
            }
            if (open[EK.Speaker] is JObject speaker)
            {
                state.HasSpeaker = true;
                state.SpeakerName = speaker.Str(K.Name);
                state.SpeakerPortraitKey = speaker.Str(EK.PortraitKey);
                state.SpeakerPortraitAvailable = speaker.Is(EK.PortraitAvailable);
            }
            var responses = new List<Core2EventResponseView>();
            foreach (var row in Js.Items(open[RK.Choices]).OfType<JObject>())
            {
                var affordable = row.Is(SK.Affordable);
                responses.Add(new Core2EventResponseView
                {
                    ChoiceId = row.Str(MK.ChoiceId), Index = (int)row.Num(RK.Index), Label = row.Str(K.Label), Affordable = affordable,
                    Tag = affordable ? string.Empty : strings.Get(SN.Core2EventCannotAfford), Priced = row.Is(EK.Priced), Binding = row.Is(EK.Binding),
                    ResultText = row.Str(EK.ResultText) ?? string.Empty,
                });
            }
            state.Responses = responses;
            var status = open.Obj(K.Status);
            if (state.Resolved) state.Status = strings.Get(Ashen.Generated.NodeStringKeys.NodesEventStatusResolved);
            else if (status.Str(K.Phase) == EV.PhaseLimited)
                state.Status = strings.Format(Ashen.Generated.NodeStringKeys.NodesEventStatusLimited,
                    new StringArgs().Add(NodePlaceholders.Available, NodeText.Number(status.Num(EK.Available))).Add(NodePlaceholders.Total, NodeText.Number(status.Num(K.Total))));
            else state.Status = strings.Get(Ashen.Generated.NodeStringKeys.NodesEventStatusChoose);
            if (state.Resolved)
            {
                var result = session.EventResult?.Result?.Outcome;
                state.ResultText = result?.Str(EK.ResultText) ?? string.Empty;
                state.StartsFight = session.EventResult?.Fight != null;
            }
            state.ContinueEnabled = state.Resolved;
            state.ContinueText = strings.Get(state.StartsFight ? Ashen.Generated.NodeStringKeys.NodesEventSteel : Ashen.Generated.NodeStringKeys.NodesEventContinue);
            state.ContinueReason = state.Resolved ? string.Empty : strings.Get(SN.Core2EventContinueReason);
            return state;
        }

        /// <summary>The narrative art: the data's entry for the event, else the first enemy of a fight it can start.</summary>
        private static string EventArt(EventsData d, NodeRules nodes, JObject def, List<JObject> choices)
        {
            var id = def.Str(K.Id);
            if (id != null && nodes.EventArt[id] != null) return (string)nodes.EventArt[id];
            foreach (var effect in choices.SelectMany(c => Js.Items(c[K.Effects]).OfType<JObject>()))
            {
                if (effect.Str(K.Op) != EV.StartCombat) continue;
                var enc = d.Run.Encounters.Has(effect.Str(MK.EncounterId)) ? d.Run.Encounters.Get(effect.Str(MK.EncounterId)) : null;
                var enemy = Js.Str(Js.Items(enc?[K.Enemies]).FirstOrDefault());
                if (enemy != null) return NodeRules.Fill(nodes.EventEnemyArt, NodePlaceholders.Id, enemy);
            }
            return null;
        }
    }
}
