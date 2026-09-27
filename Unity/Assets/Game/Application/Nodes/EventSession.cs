using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.App.Run;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Domain.Loop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using EK = Ashen.Generated.EventKeys;
using EV = Ashen.Generated.EventValues;
using ES = Ashen.Generated.EventStringKeys;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.App.Nodes
{
    /// <summary>
    /// An Unknown-node event (W-11 event and W4c dialogue; US-10.1–10.3, AF-07) on the climb's session: travel entered it
    /// (the event id and whether a response was taken are saved with the run); <see cref="Choose"/> is
    /// <see cref="RunSession.ChooseEvent"/> (the effects, the history row and the quests; a refusal changes nothing; saved
    /// with the event still open, so a reload resumes resolved); <see cref="Continue"/> is <see cref="RunSession.FinishEvent"/>,
    /// allowed once a response is taken: a response that started a fight begins it where the run stands (W-07), any other
    /// returns to the act map. The Turncoat Mirror's class swap is an effect of its response (the swapClass op). Engine-free.
    /// </summary>
    public sealed class EventSession
    {
        private EventSession(RunSession owner) => Owner = owner;

        public RunSession Owner { get; }
        public EventsData Data => Owner.Content.Loop.Events;
        public string EventId => Owner.EventId;

        /// <summary>A response has been taken (Continue is enabled).</summary>
        public bool Resolved => Owner.EventDone;

        /// <summary>The outcome of the response taken in this session (null after a resume: the result is re-read from the choice).</summary>
        public EventResult LastResult { get; private set; }

        public static EventSession Start(RunSession owner)
        {
            if (owner?.EventId == null) throw new ArgumentException(NodeMessages.EventNeedsId);
            return new EventSession(owner);
        }

        /// <summary>The domain's view (Events.Open) of the event before a response is taken.</summary>
        public JObject DomainView() => Owner.EventView();

        /// <summary>Every authored choice with its id and history requirement (content/events.js eventChoicesWithHistory).</summary>
        public List<JObject> AllChoices() => EventChoices.WithHistory(Data, Data.Events.Get(EventId));

        /// <summary>The response taken at this event (its history row; the last one for this event).</summary>
        public string TakenChoice(JObject run)
        {
            if (!Resolved) return null;
            return Js.Items(run?[RK.History]).OfType<JObject>().LastOrDefault(r => r.Str(K.Kind) == EV.ChoiceKind && r.Str(MK.EventId) == EventId)?.Str(MK.ChoiceId);
        }

        /// <summary>The taken response started a fight (Continue reads "Steel yourself", US-10.1).</summary>
        public static bool StartsFight(JObject run, bool resolved) => resolved && Js.Truthy(run?[RK.CombatEntered]);

        public EventResult Choose(string choiceId)
        {
            var result = Owner.ChooseEvent(choiceId);
            if (!result.Ok) return result;
            LastResult = result;
            Owner.FollowClass();
            return result;
        }

        /// <summary>Continue: null before a response is taken; else the location after it (combat for the response's fight, else the map).</summary>
        public string Continue() => Resolved ? Owner.FinishEvent() : null;
    }

    /// <summary>One response as W-11 draws it.</summary>
    public sealed class EventResponseView
    {
        public string ChoiceId;
        public string Label;
        public string Preview;
        public bool Available;
        public bool Locked;
        public bool Binding;
        public string ReasonText;
        public bool Chosen;
    }

    /// <summary>The event (or dialogue) as the screen draws it.</summary>
    public sealed class EventViewState
    {
        public string EventId;
        public string Title;
        public string Text;
        public string ArtId;
        public string BackgroundId;
        public string StatusText;
        public bool HasSpeaker;
        public string SpeakerName;
        public string SpeakerArtId;
        public string DialogueTitle;
        public List<EventResponseView> Responses = new List<EventResponseView>();
        public bool Resolved;
        public string ResultText;
        public string SwapText;
        public bool StartsFight;
        public string FightNote;
        public string ContinueText;
        public bool ContinueEnabled;
    }

    /// <summary>
    /// W-11 as display data: the narrative (title, text, reused art), the status ("Choose a response", "n of m available",
    /// "Resolved"), each response with its effect preview, a binding mark (⟲, hold to commit) and — when it cannot be taken
    /// — its requirement (a price, or the earlier choices the history needs; the choices the shipped door hides are shown
    /// locked with their requirement, US-10.1), the speaker of a quest chain's step (W4c), the resolved result and the
    /// Continue label ("Steel yourself" when the response started a fight). Engine-free.
    /// </summary>
    public static class EventView
    {
        public static EventViewState Build(EventSession session, UiData ui, string regionId = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var strings = ui.Strings;
            var d = session.Data;
            var def = d.Events.Get(session.EventId) ?? new JObject();
            var run = session.Owner.Run;
            var open = session.DomainView() ?? new JObject();
            var all = session.AllChoices();
            var taken = session.TakenChoice(run);
            var state = new EventViewState
            {
                EventId = session.EventId,
                Title = def.Str(K.Name) ?? session.EventId,
                Text = def.Str(EK.Text) ?? string.Empty,
                ArtId = EventArt(d, ui, def, all),
                BackgroundId = NodeRules.Fill(ui.Nodes.EventBackground, NodePlaceholders.Region, regionId) ?? ui.Nodes.EventFallbackBackground,
                Resolved = session.Resolved,
            };
            if (open[EK.Speaker] is JObject speaker)
            {
                state.HasSpeaker = true;
                state.SpeakerName = speaker.Str(K.Name) ?? speaker.Str(K.Id);
                state.SpeakerArtId = speaker.Is(EK.PortraitAvailable) ? NodeRules.Fill(ui.Nodes.SpeakerArt, NodePlaceholders.Id, speaker.Str(EK.PortraitKey)) : null;
                state.DialogueTitle = state.SpeakerName == state.Title ? state.Title
                    : strings.Format(NodeStringKeys.NodesDialogueTitle, new StringArgs().Add(NodePlaceholders.Speaker, state.SpeakerName).Add(NodePlaceholders.Title, state.Title));
            }
            var cinders = run.Num(RK.Cinders);
            var visible = Js.Items(open[RK.Choices]).OfType<JObject>().ToList();
            for (var index = 0; index < all.Count; index++)
            {
                var choice = all[index];
                var id = choice.Str(K.Id);
                var row = visible.FirstOrDefault(v => v.Str(MK.ChoiceId) == id);
                var response = new EventResponseView { ChoiceId = id, Preview = Preview(d, strings, choice), Chosen = taken == id };
                var label = choice.Str(K.Label) ?? id;
                if (row == null)
                {
                    response.Locked = true;
                    response.ReasonText = History(d, strings, choice[EK.RequiresHistory]);
                    response.Label = strings.Format(NodeStringKeys.NodesEventLocked, new StringArgs().Add(NodePlaceholders.Label, label).Add(NodePlaceholders.Requirement, response.ReasonText));
                }
                else
                {
                    response.Available = row.Is(ShopKeys.Affordable);
                    response.Binding = row.Is(EK.Binding);
                    response.Label = response.Binding ? strings.Format(NodeStringKeys.NodesEventBinding, new StringArgs().Add(NodePlaceholders.Label, label)) : label;
                    if (!response.Available)
                    {
                        var cost = Js.Get(choice[K.Requires], RK.Cinders);
                        response.ReasonText = Js.IsNum(cost)
                            ? strings.Format(NodeStringKeys.NodesEventRequiresCinders, new StringArgs().Add(NodePlaceholders.Cost, NodeText.Number(Js.D(cost))).Add(NodePlaceholders.Cinders, NodeText.Number(cinders)))
                            : NodeText.Refusal(strings, row.Str(ShopKeys.Refusal) ?? ES.EventsRefusalCinders);
                        response.Label = strings.Format(NodeStringKeys.NodesEventLocked, new StringArgs().Add(NodePlaceholders.Label, label).Add(NodePlaceholders.Requirement, response.ReasonText));
                    }
                }
                state.Responses.Add(response);
            }
            if (session.Resolved)
            {
                var chosen = all.FirstOrDefault(c => c.Str(K.Id) == taken);
                state.ResultText = session.LastResult?.Outcome?.Str(EK.ResultText) ?? chosen?.Str(EK.ResultText) ?? string.Empty;
                if (Js.Items(chosen?[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Op) == EV.SwapClass))
                    state.SwapText = strings.Format(NodeStringKeys.NodesEventSwapped, new StringArgs().Add(NodePlaceholders.Class,
                        strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.ClassNameKey, run.Str(RK.Class)))));
                state.StartsFight = EventSession.StartsFight(run, session.Resolved);
                state.FightNote = state.StartsFight ? strings.Get(NodeStringKeys.NodesEventFightNote) : null;
                state.StatusText = strings.Get(NodeStringKeys.NodesEventStatusResolved);
            }
            else
            {
                var total = state.Responses.Count;
                var available = state.Responses.Count(r => r.Available);
                state.StatusText = available < total
                    ? strings.Format(NodeStringKeys.NodesEventStatusLimited, new StringArgs().Add(NodePlaceholders.Available, NodeText.Number(available)).Add(NodePlaceholders.Total, NodeText.Number(total)))
                    : strings.Get(NodeStringKeys.NodesEventStatusChoose);
            }
            state.ContinueEnabled = session.Resolved;
            state.ContinueText = strings.Get(state.StartsFight ? NodeStringKeys.NodesEventSteel : NodeStringKeys.NodesEventContinue);
            return state;
        }

        /// <summary>The narrative art: the data's entry for the event, else the first enemy of a fight it can start.</summary>
        private static string EventArt(EventsData d, UiData ui, JObject def, List<JObject> choices)
        {
            var id = def.Str(K.Id);
            if (id != null && ui.Nodes.EventArt[id] != null) return (string)ui.Nodes.EventArt[id];
            foreach (var effect in choices.SelectMany(c => Js.Items(c[K.Effects]).OfType<JObject>()))
            {
                if (effect.Str(K.Op) != EV.StartCombat) continue;
                var enc = d.Run.Encounters.Has(effect.Str(MK.EncounterId)) ? d.Run.Encounters.Get(effect.Str(MK.EncounterId)) : null;
                var enemy = Js.Str(Js.Items(enc?[K.Enemies]).FirstOrDefault());
                if (enemy != null) return NodeRules.Fill(ui.Nodes.EventEnemyArt, NodePlaceholders.Id, enemy);
            }
            return null;
        }

        // ------------------------------------------------------------------ requirements (US-10.1: illegal choices show their requirement)

        private static string History(EventsData d, StringTable strings, JToken requirement)
        {
            if (!(requirement is JObject groups)) return NodeText.Refusal(strings, ES.EventsRefusalHistory);
            var parts = new List<string>();
            foreach (var (group, key) in new[] { (MK.All, NodeStringKeys.NodesEventRequiresAll), (MK.Any, NodeStringKeys.NodesEventRequiresAny), (MK.None, NodeStringKeys.NodesEventRequiresNone) })
            {
                var refs = Js.Items(groups[group]).OfType<JObject>().Select(r => ChoiceName(d, strings, r.Str(MK.EventId), r.Str(MK.ChoiceId))).ToList();
                if (refs.Count == 0) continue;
                parts.Add(strings.Format(key, new StringArgs().Add(NodePlaceholders.List, NodeText.Join(strings, NodeStringKeys.NodesEventRequiresOr, refs))));
            }
            return parts.Count == 0 ? NodeText.Refusal(strings, ES.EventsRefusalHistory) : NodeText.Join(strings, NodeStringKeys.NodesListJoin, parts);
        }

        private static string ChoiceName(EventsData d, StringTable strings, string eventId, string choiceId)
        {
            var def = d.Events.Has(eventId) ? d.Events.Get(eventId) : null;
            var choice = def == null ? null : EventChoices.WithHistory(d, def).FirstOrDefault(c => c.Str(K.Id) == choiceId);
            return strings.Format(NodeStringKeys.NodesEventRequiresItem, new StringArgs()
                .Add(NodePlaceholders.Choice, choice?.Str(K.Label) ?? choiceId).Add(NodePlaceholders.Event, def?.Str(K.Name) ?? eventId));
        }

        // ------------------------------------------------------------------ the effect preview (AF-07 review: exact effect preview)

        /// <summary>A response's effects in words, joined; "Nothing happens." for none.</summary>
        public static string Preview(EventsData d, StringTable strings, JObject choice)
        {
            var effects = Js.Items(choice?[K.Effects]).OfType<JObject>().ToList();
            if (effects.Count == 0) return strings.Get(NodeStringKeys.NodesEffectNothing);
            return NodeText.Join(strings, NodeStringKeys.NodesEffectJoin, effects.Select(e => Effect(d, strings, e)));
        }

        private static string Effect(EventsData d, StringTable strings, JObject e)
        {
            var args = new StringArgs();
            string key;
            var amount = e[K.Amount];
            switch (e.Str(K.Op))
            {
                case EV.AddCinders:
                    var n = Js.IsNum(amount) ? Js.D(amount) : 0;
                    key = n < 0 ? NodeStringKeys.NodesEffectPayCinders : NodeStringKeys.NodesEffectAddCinders;
                    args.Add(NodePlaceholders.Amount, NodeText.Number(Math.Abs(n)));
                    break;
                case EV.StartCombat:
                {
                    key = NodeStringKeys.NodesEffectStartCombat;
                    var enc = d.Run.Encounters.Has(e.Str(MK.EncounterId)) ? d.Run.Encounters.Get(e.Str(MK.EncounterId)) : null;
                    var names = Js.Items(enc?[K.Enemies]).Select(Js.Str).Select(id => d.Combat.Enemies.Has(id) ? d.Combat.Enemies.Get(id).Str(K.Name) : id);
                    args.Add(NodePlaceholders.Name, NodeText.Join(strings, NodeStringKeys.NodesListJoin, names));
                    break;
                }
                case EV.AddRelic:
                {
                    var id = e.Str(K.Id);
                    key = id != null ? NodeStringKeys.NodesEffectAddRelicNamed : NodeStringKeys.NodesEffectAddRelicRandom;
                    if (id != null) args.Add(NodePlaceholders.Name, d.Combat.Relics.Has(id) ? d.Combat.Relics.Get(id).Str(K.Name) : id);
                    break;
                }
                case EV.AddCardToDeck:
                {
                    var id = e.Str(K.Card);
                    key = NodeStringKeys.NodesEffectAddCardToDeck;
                    args.Add(NodePlaceholders.Name, id != null && d.Combat.Cards.Has(id) ? d.Combat.Cards.Get(id).Str(K.Name) : id);
                    break;
                }
                case EV.RemoveCardFromDeck: key = NodeStringKeys.NodesEffectRemoveCardFromDeckRandom; break;
                case EV.UpgradeCard: key = NodeStringKeys.NodesEffectUpgradeCardRandom; break;
                case EV.SwapClass: key = NodeStringKeys.NodesEffectSwapClass; break;
                case EV.LoseMaxHpPct:
                    key = NodeStringKeys.NodesEffectLoseMaxHpPct;
                    args.Add(NodePlaceholders.Pct, NodeText.Number(e.Num(K.Pct)));
                    break;
                default:
                    if (e.Str(K.Op) == NodeValues.OpDamage && Js.IsNum(amount))
                    {
                        key = NodeStringKeys.NodesEffectDamage;
                        args.Add(NodePlaceholders.Amount, NodeText.Number(Js.D(amount)));
                    }
                    else if (e.Str(K.Op) == NodeValues.OpHeal)
                    {
                        var pct = Js.Get(amount, K.Pct);
                        key = Js.IsNum(pct) ? NodeStringKeys.NodesEffectHealPct : NodeStringKeys.NodesEffectHealFlat;
                        args.Add(NodePlaceholders.Pct, Js.IsNum(pct) ? NodeText.Number(Js.D(pct)) : string.Empty);
                        args.Add(NodePlaceholders.Amount, Js.IsNum(amount) ? NodeText.Number(Js.D(amount)) : string.Empty);
                    }
                    else key = NodeStringKeys.NodesEffectOther;
                    break;
            }
            var text = strings.Format(key, args);
            var gate = e.Obj(K.If);
            var chance = gate?[K.Pct];
            if (gate != null && Js.IsNum(chance))
                text = strings.Format(NodeStringKeys.NodesEffectChance, new StringArgs().Add(NodePlaceholders.Pct, NodeText.Number(Js.D(chance))).Add(NodePlaceholders.Effect, text));
            return text;
        }
    }
}
