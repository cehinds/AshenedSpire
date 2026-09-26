using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using MK = Ashen.Generated.MapKeys;
using EK = Ashen.Generated.EventKeys;
using EV = Ashen.Generated.EventValues;
using EM = Ashen.Generated.EventMessages;

namespace Ashen.Domain.Events
{
    /// <summary>
    /// Deterministic run-history facts for quest and event chains (shipped model/quests.js, and engine/quests.js
    /// completeQuest): event-choice rows and quest-completion rows in run.history, validated so a malformed row fails
    /// closed, the requirement groups (all / any / none) that open later choices, the choices a run can see, and the
    /// quest chains' readers. No wall clock is recorded, so a row replays byte for byte.
    /// </summary>
    public static class Quests
    {
        private static readonly Regex IdPattern = new Regex(EV.IdPattern, RegexOptions.CultureInvariant);
        private static readonly Regex QuestIdPattern = new Regex(EV.QuestIdPattern, RegexOptions.CultureInvariant);

        /// <summary>validId(value): a stable id (a letter, then letters, digits, _ or -, at most 80).</summary>
        public static bool ValidId(JToken value) => Js.IsStr(value) && IdPattern.IsMatch(Js.Str(value));

        /// <summary>validQuestId(value): the event-id grammar plus ':'.</summary>
        public static bool ValidQuestId(JToken value) => Js.IsStr(value) && QuestIdPattern.IsMatch(Js.Str(value));

        /// <summary>historyArray(subject): the subject itself when it is an array, else its history array, else null.</summary>
        private static JArray HistoryArray(JToken subject) => subject as JArray ?? (subject as JObject)?[RK.History] as JArray;

        private static List<string> ChoiceRefProblems(JToken reference, string at)
        {
            var out_ = new List<string>();
            if (!(reference is JObject o)) return new List<string> { RunJs.Fmt(EM.ProblemMustBeObject, at) };
            if (!ValidId(o[MK.EventId])) out_.Add(RunJs.Fmt(EM.ProblemEventId, at));
            if (!ValidId(o[MK.ChoiceId])) out_.Add(RunJs.Fmt(EM.ProblemChoiceId, at));
            return out_;
        }

        private static bool PositiveInteger(JToken v) => Js.IsInt(v) && Js.D(v) >= 1;

        private static bool NonNegativeInteger(JToken v) => Js.IsInt(v) && Js.D(v) >= 0;

        /// <summary>eventChoiceHistoryProblems(subject): only event-choice rows are validated; other row kinds stay valid.</summary>
        public static List<string> HistoryProblems(JToken subject)
        {
            var history = HistoryArray(subject);
            if (history == null) return new List<string> { EM.HistoryRequired };
            var out_ = new List<string>();
            for (var index = 0; index < history.Count; index++)
            {
                var row = history[index] as JObject;
                if (row == null || row.Str(K.Kind) != EV.ChoiceKind) continue;
                var at = RunJs.Fmt(EV.HistoryAt, index);
                out_.AddRange(ChoiceRefProblems(row, at));
                if (!PositiveInteger(row[RK.ActNumber])) out_.Add(RunJs.Fmt(EM.ProblemActNumber, at));
                if (!NonNegativeInteger(row[RK.Floor])) out_.Add(RunJs.Fmt(EM.ProblemFloor, at));
                if (!Js.Nullish(row[RK.MapNodeId]) && !ValidId(row[RK.MapNodeId])) out_.Add(RunJs.Fmt(EM.ProblemMapNodeId, at));
            }
            return out_;
        }

        /// <summary>
        /// recordEventChoice(run, { eventId, choiceId }) → the row appended to run.history: kind, eventId, choiceId,
        /// actNumber, floor, mapNodeId (null when the run has none). Throws by name on a run it cannot describe.
        /// </summary>
        public static JObject RecordEventChoice(JObject run, string eventId, string choiceId)
        {
            if (run == null || !(run[RK.History] is JArray history)) throw new InvalidOperationException(EM.RecordNeedsHistory);
            var problems = ChoiceRefProblems(Js.Obj(MK.EventId, eventId, MK.ChoiceId, choiceId), EV.ChoiceAt);
            if (problems.Count > 0) throw new InvalidOperationException(string.Join(RV.ProblemJoiner, problems));
            if (!PositiveInteger(run[RK.ActNumber])) throw new InvalidOperationException(EM.RecordNeedsAct);
            if (!NonNegativeInteger(run[RK.Floor])) throw new InvalidOperationException(EM.RecordNeedsFloor);
            if (!Js.Nullish(run[RK.MapNodeId]) && !ValidId(run[RK.MapNodeId])) throw new InvalidOperationException(EM.RecordNeedsNode);
            var record = Js.Obj(K.Kind, EV.ChoiceKind, MK.EventId, eventId, MK.ChoiceId, choiceId, RK.ActNumber, run[RK.ActNumber].DeepClone(),
                RK.Floor, run[RK.Floor].DeepClone(), RK.MapNodeId, Js.Nullish(run[RK.MapNodeId]) ? Js.Null() : run[RK.MapNodeId].DeepClone());
            history.Add(record.DeepClone());
            return record;
        }

        /// <summary>hasEventChoice(subject, ref): exact fact lookup; malformed event-choice history fails closed.</summary>
        public static bool HasEventChoice(JToken subject, JToken reference)
        {
            var history = HistoryArray(subject);
            if (history == null || ChoiceRefProblems(reference, EV.ChoiceAt).Count > 0 || HistoryProblems(history).Count > 0) return false;
            return history.OfType<JObject>().Any(row => row.Str(K.Kind) == EV.ChoiceKind
                && row.Str(MK.EventId) == Js.Str(reference[MK.EventId]) && row.Str(MK.ChoiceId) == Js.Str(reference[MK.ChoiceId]));
        }

        /// <summary>eventChoiceRequirementProblems(requirement): { all?, any?, none? } of choice refs; an explicitly empty `any` is invalid.</summary>
        public static List<string> RequirementProblems(EventsData d, JToken requirement)
        {
            if (Js.Nullish(requirement)) return new List<string>();
            if (!(requirement is JObject o)) return new List<string> { EM.RequirementObject };
            var groups = d.RuleList(RK.History, MK.RequirementGroups);
            var out_ = new List<string>();
            foreach (var p in o.Properties())
                if (!groups.Contains(p.Name)) out_.Add(RunJs.Fmt(EM.RequirementUnknownGroup, p.Name));
            foreach (var group in groups)
            {
                if (o.Property(group) == null) continue;
                if (!(o[group] is JArray refs))
                {
                    out_.Add(RunJs.Fmt(EM.RequirementGroupArray, group));
                    continue;
                }
                if (group == MK.Any && refs.Count == 0) out_.Add(EM.RequirementAnyEmpty);
                for (var i = 0; i < refs.Count; i++) out_.AddRange(ChoiceRefProblems(refs[i], RunJs.Fmt(EV.RequirementAt, group, i)));
            }
            return out_;
        }

        /// <summary>eventChoiceRequirementMet(requirement, subject): later-step availability from exact earlier-choice facts.</summary>
        public static bool RequirementMet(EventsData d, JToken requirement, JToken subject)
        {
            if (RequirementProblems(d, requirement).Count > 0) return false;
            var history = HistoryArray(subject);
            if (history == null || HistoryProblems(history).Count > 0) return false;
            var groups = requirement as JObject ?? new JObject();
            bool Matches(JToken reference) => HasEventChoice(history, reference);
            if (Js.Items(groups[MK.All]).Any(r => !Matches(r))) return false;
            if (Js.Truthy(groups[MK.Any]) && !Js.Items(groups[MK.Any]).Any(Matches)) return false;
            if (Js.Items(groups[MK.None]).Any(Matches)) return false;
            return true;
        }

        /// <summary>One visible choice with its authored index (so bindings stay stable when a requirement hides an earlier one).</summary>
        public sealed class IndexedChoice
        {
            public JObject Choice;
            public int Index;
        }

        /// <summary>availableEventChoices(choices, subject): the choices with a stable id whose history requirement the run meets.</summary>
        public static List<IndexedChoice> AvailableChoices(EventsData d, IReadOnlyList<JObject> choices, JToken subject)
        {
            var out_ = new List<IndexedChoice>();
            for (var index = 0; index < choices.Count; index++)
            {
                var choice = choices[index];
                if (choice != null && ValidId(choice[K.Id]) && RequirementMet(d, choice[EK.RequiresHistory], subject)) out_.Add(new IndexedChoice { Choice = choice, Index = index });
            }
            return out_;
        }

        /// <summary>questChainForEvent(chains, eventId): the quest whose steps include the event, or null.</summary>
        public static string QuestChainForEvent(EventsData d, string eventId)
        {
            foreach (var p in d.QuestChains.Properties())
                if (Js.Get(p.Value, EK.Steps) is JArray steps && Js.Includes(steps, eventId)) return p.Name;
            return null;
        }

        /// <summary>questsCompletedBy(chains, { eventId, choiceId }): every quest a choice completes.</summary>
        public static List<string> QuestsCompletedBy(EventsData d, string eventId, string choiceId) =>
            d.QuestChains.Properties()
                .Where(p => Js.Get(p.Value, EK.Completes) is JArray completes && completes.OfType<JObject>().Any(r => r.Str(MK.EventId) == eventId && r.Str(MK.ChoiceId) == choiceId))
                .Select(p => p.Name).ToList();

        /// <summary>hasQuestCompletion(subject, questId): presence only (a malformed neighbour row never reopens a quest).</summary>
        public static bool HasQuestCompletion(JToken subject, string questId)
        {
            var history = HistoryArray(subject);
            if (history == null || !ValidQuestId(Js.S(questId))) return false;
            return history.OfType<JObject>().Any(row => row.Str(K.Kind) == EV.QuestKind && row.Str(EK.QuestId) == questId);
        }

        /// <summary>recordQuestCompletion(run, { questId, source }) → the appended row, or null when the quest was already complete.</summary>
        public static JObject RecordQuestCompletion(EventsData d, JObject run, string questId, string source)
        {
            if (run == null || !(run[RK.History] is JArray history)) throw new InvalidOperationException(EM.CompletionNeedsHistory);
            if (!ValidQuestId(Js.S(questId))) throw new InvalidOperationException(RunJs.Fmt(EM.CompletionQuestId, questId));
            var sources = d.RuleList(RK.History, EK.CompletionSources);
            if (!sources.Contains(source)) throw new InvalidOperationException(RunJs.Fmt(EM.CompletionSource, string.Join(RV.ListJoiner, sources), source));
            if (HasQuestCompletion(run, questId)) return null;
            var record = Js.Obj(K.Kind, EV.QuestKind, EK.QuestId, questId, RK.Source, source);
            history.Add(record.DeepClone());
            return record;
        }

        /// <summary>completeQuest(ctx, { questId, source }) → { record, event } or null: one questCompleted row and event, at most once per run.</summary>
        public static (JObject Record, JObject Event) CompleteQuest(EventsData d, JObject run, string questId, string source)
        {
            var record = RecordQuestCompletion(d, run, questId, source);
            if (record == null) return (null, null);
            return (record, Js.Obj(K.Type, EV.QuestKind, EK.QuestId, questId, RK.Source, source));
        }
    }
}
