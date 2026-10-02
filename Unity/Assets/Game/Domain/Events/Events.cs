using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using Refusal = Ashen.Domain.Shop.Refusal;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using MK = Ashen.Generated.MapKeys;
using SK = Ashen.Generated.ShopKeys;
using EK = Ashen.Generated.EventKeys;
using EV = Ashen.Generated.EventValues;
using ES = Ashen.Generated.EventStringKeys;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.Domain.Events
{
    /// <summary>What a choice did: the outcome (the run changed) or a refusal (nothing changed).</summary>
    public sealed class EventResult
    {
        public bool Ok;

        /// <summary>{ choiceId, resultText, receipt (the history row), completions, events, fight (encounter id or null) }.</summary>
        public JObject Outcome;

        public Refusal Refusal;

        /// <summary>The encounter a choice started (run.combatEntered), or null — the caller enters it after the result text.</summary>
        public string Fight => Ok ? Js.Str(Outcome[MK.Fight]) : null;

        public JObject ToJson() => Ok ? Js.Obj(SK.Ok, true, WK.Outcome, Outcome.DeepClone()) : Js.Obj(SK.Ok, false, SK.Refusal, Refusal.ToJson());
    }

    /// <summary>
    /// Unknown-node events (US-10.1–10.3): the run-writing half of the shipped main.js showEvent and of the event and
    /// dialogue screens, UI-free. <see cref="Open"/> is what the screen shows (the visible choices with their price,
    /// binding and refusal facts, the choices history hides, and for a quest chain's step its speaker);
    /// <see cref="Choose"/> commits a choice through the shipped quest door (effects, then the history row, then quest
    /// completion) and reports a fight as an encounter id rather than running it; <see cref="Finish"/> is onDone (the
    /// fight leaves the run for the caller's enterCombat).
    /// </summary>
    public static class Events
    {
        private static JToken Key(string key) => key == null ? (JToken)Js.Null() : key;

        /// <summary>showEvent(eventId): the view the event (or dialogue) screen draws before a response is taken.</summary>
        public static JObject Open(EventsData d, JObject run, string eventId)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (run == null) throw new ArgumentNullException(nameof(run));
            var def = d.Events.Get(eventId);
            var questId = Quests.QuestChainForEvent(d, eventId);
            var all = EventChoices.WithHistory(d, def);
            var visible = Quests.AvailableChoices(d, all, run);
            var choices = new JArray();
            foreach (var v in visible)
            {
                var reasons = EventChoices.BindingReasons(d, v.Choice);
                var affordable = EventChoices.Affordable(v.Choice, run);
                choices.Add(Js.Obj(RK.Index, (double)v.Index, MK.ChoiceId, v.Choice[K.Id], K.Label, v.Choice[K.Label], EK.ResultText, v.Choice[EK.ResultText],
                    SK.Affordable, affordable, EK.Priced, Js.Truthy(v.Choice[K.Requires]), EK.Binding, reasons.Count > 0, EK.BindingReasons, new JArray(reasons),
                    SK.Refusal, Key(affordable ? null : ES.EventsRefusalCinders)));
            }
            var hidden = new JArray();
            for (var index = 0; index < all.Count; index++)
                if (!visible.Any(v => v.Index == index))
                    hidden.Add(Js.Obj(RK.Index, (double)index, MK.ChoiceId, all[index][K.Id], SK.Refusal, ES.EventsRefusalHistory));
            JToken speaker = Js.Null();
            if (questId != null)
            {
                var s = d.Speakers.Get(d.EventSpeakers.Str(eventId));
                var portrait = s.Str(EK.PortraitKey);
                speaker = Js.Obj(K.Id, s[K.Id], K.Name, s[K.Name], EK.PortraitKey, s[EK.PortraitKey], EK.PortraitAvailable, !string.IsNullOrEmpty(portrait) && d.Combat.Enemies.Has(portrait));
            }
            var total = choices.Count;
            var available = choices.OfType<JObject>().Count(c => c.Is(SK.Affordable));
            var status = Js.Obj(K.Total, (double)total, EK.Available, (double)available, K.Blocked, (double)(total - available),
                EK.Binding, (double)choices.OfType<JObject>().Count(c => c.Is(EK.Binding)), K.Phase, available < total ? EV.PhaseLimited : EV.PhaseChoose);
            var view = Js.Obj(MK.EventId, eventId, EK.QuestId, Key(questId), WK.Title, def[K.Name], EK.Text, def[EK.Text], EK.Art, def[EK.Art]);
            view[EK.Speaker] = speaker;
            view[RK.Choices] = choices;
            view[EK.Hidden] = hidden;
            view[K.Status] = status;
            return view;
        }

        /// <summary>
        /// commitEventChoice(ctx, { eventId, choiceId }): a choice the event lacks, one the history has not opened or one
        /// the purse cannot pay is refused; otherwise the choice's effects run (engine/actions.js executeRunEffects), the
        /// history row is written and any quest the choice completes is completed once.
        /// </summary>
        public static EventResult Choose(EventsData d, JObject run, string eventId, string choiceId, Rng rng)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            var def = d.Events.Get(eventId);
            var choice = EventChoices.WithHistory(d, def).FirstOrDefault(entry => entry.Str(K.Id) == choiceId);
            if (choice == null) return new EventResult { Refusal = new Refusal(ES.EventsRefusalUnknownChoice) };
            if (Quests.AvailableChoices(d, new List<JObject> { choice }, run).Count == 0) return new EventResult { Refusal = new Refusal(ES.EventsRefusalHistory) };
            if (!EventChoices.Affordable(choice, run)) return new EventResult { Refusal = new Refusal(ES.EventsRefusalCinders) };
            var events = RunEffects.Execute(d, run, rng, choice[K.Effects] as JArray ?? new JArray());
            var receipt = Quests.RecordEventChoice(run, eventId, choiceId);
            var completions = new JArray();
            foreach (var questId in Quests.QuestsCompletedBy(d, eventId, choiceId))
            {
                var (record, ev) = Quests.CompleteQuest(d, run, questId, EV.SourceEvent);
                if (record == null) continue;
                completions.Add(record);
                events.Add(ev);
            }
            var entered = run[RK.CombatEntered];
            JToken fight = Js.Truthy(entered) ? (Js.IsStr(entered) ? entered.DeepClone() : Js.Get(entered, MK.EncounterId)?.DeepClone() ?? Js.Null()) : Js.Null();
            var outcome = Js.Obj(MK.ChoiceId, choice[K.Id], EK.ResultText, choice[EK.ResultText], SK.Receipt, receipt, EK.Completions, completions, MK.Events, events);
            outcome[MK.Fight] = fight;
            return new EventResult { Ok = true, Outcome = outcome };
        }

        /// <summary>
        /// showEvent's onDone: a fight the choice started (run.combatEntered, as the id or { encounterId }) leaves the run;
        /// returns { fight: encounterId | null } for the caller's enterCombat.
        /// </summary>
        public static JObject Finish(JObject run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            var entered = run[RK.CombatEntered];
            if (!Js.Truthy(entered)) return Js.Obj(MK.Fight, Js.Null());
            var encounterId = Js.IsStr(entered) ? entered.DeepClone() : Js.Get(entered, MK.EncounterId)?.DeepClone() ?? Js.Null();
            run[RK.CombatEntered] = Js.Null();
            return Js.Obj(MK.Fight, encounterId);
        }
    }
}
