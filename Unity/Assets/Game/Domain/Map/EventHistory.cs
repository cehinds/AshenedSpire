using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;

namespace Ashen.Domain.Map
{
    /// <summary>
    /// The quest-history gate the Unknown-node roll reads (shipped model/quests.js): whether a run's event-choice
    /// history meets an event's requirement. Malformed history or a malformed requirement fails closed. Only the
    /// pass/fail answer is ported here; the problem strings stay with the save validator.
    /// </summary>
    public static class EventHistory
    {
        /// <summary>eventChoiceRequirementMet(requirement, { history }).</summary>
        public static bool RequirementMet(MapRules rules, JToken requirement, JArray history)
        {
            if (RequirementHasProblems(rules, requirement)) return false;
            if (history == null || HistoryHasProblems(rules, history)) return false;
            var groups = requirement as JObject ?? new JObject();
            bool Matches(JToken r) => HasChoice(rules, history, r);
            if (Js.Items(groups[MK.All]).Any(r => !Matches(r))) return false;
            if (Js.Truthy(groups[MK.Any]) && !Js.Items(groups[MK.Any]).Any(Matches)) return false;
            if (Js.Items(groups[MK.None]).Any(Matches)) return false;
            return true;
        }

        /// <summary>hasEventChoice(history, ref): an exact choice fact; malformed history fails closed.</summary>
        public static bool HasChoice(MapRules rules, JArray history, JToken reference)
        {
            if (history == null || RefHasProblems(rules, reference) || HistoryHasProblems(rules, history)) return false;
            var r = (JObject)reference;
            return history.Any(row => row is JObject o && o.Str(K.Kind) == rules.EventChoiceKind
                && Js.Str(o[MK.EventId]) == r.Str(MK.EventId) && Js.Str(o[MK.ChoiceId]) == r.Str(MK.ChoiceId)
                && Js.IsStr(o[MK.EventId]) && Js.IsStr(o[MK.ChoiceId]));
        }

        /// <summary>eventChoiceHistoryProblems(history).length &gt; 0 — only event-choice rows are checked.</summary>
        public static bool HistoryHasProblems(MapRules rules, JArray history)
        {
            foreach (var token in history)
            {
                if (!(token is JObject row) || row.Str(K.Kind) != rules.EventChoiceKind) continue;
                if (RefHasProblems(rules, row)) return true;
                var act = row[MK.ActNumber];
                if (!Js.IsInt(act) || Js.D(act) < 1) return true;
                var floor = row[MK.Floor];
                if (!Js.IsInt(floor) || Js.D(floor) < 0) return true;
                var node = row[MK.MapNodeId];
                if (!Js.Nullish(node) && !rules.ValidId(node)) return true;
            }
            return false;
        }

        /// <summary>eventChoiceRequirementProblems(requirement).length &gt; 0.</summary>
        public static bool RequirementHasProblems(MapRules rules, JToken requirement)
        {
            if (Js.Nullish(requirement)) return false;
            if (!(requirement is JObject req)) return true;
            foreach (var p in req.Properties())
                if (!rules.RequirementGroups.Contains(p.Name)) return true;
            foreach (var group in rules.RequirementGroups)
            {
                if (!req.ContainsKey(group)) continue;
                if (!(req[group] is JArray refs)) return true;
                if (group == MK.Any && refs.Count == 0) return true;
                if (refs.Any(r => RefHasProblems(rules, r))) return true;
            }
            return false;
        }

        private static bool RefHasProblems(MapRules rules, JToken reference) =>
            !(reference is JObject r) || !rules.ValidId(r[MK.EventId]) || !rules.ValidId(r[MK.ChoiceId]);
    }
}
