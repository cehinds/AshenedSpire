using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using MM = Ashen.Generated.MapMessages;

namespace Ashen.Domain.Map
{
    /// <summary>
    /// The encounter and Unknown-node rolls the map reads (shipped engine/encounters.js rollEncounter and
    /// resolveUnknownNode). Stream 'enemyAI' for encounters, 'events' for Unknown nodes (SPEC §5.6).
    /// </summary>
    public static class MapEncounters
    {
        /// <summary>
        /// rollEncounter(registries, rng, { pool, seat, exclude }): a weighted pick from the seat's pool, avoiding the
        /// no-repeat window unless that empties it.
        /// </summary>
        public static string RollEncounter(MapData data, Rng rng, string pool, string seat, IReadOnlyCollection<string> exclude = null)
        {
            if (string.IsNullOrEmpty(seat)) throw new ArgumentException(MM.RollEncounterSeatRequired, nameof(seat));
            var skip = exclude ?? Array.Empty<string>();
            bool InSeatPool(JObject e) => e.Str(MK.Pool) == pool && pool != null && e.Str(MK.Seat) == seat;
            var candidates = data.Encounters.All.Where(e => InSeatPool(e) && !skip.Contains(e.Str(K.Id))).ToList();
            if (candidates.Count == 0) candidates = data.Encounters.All.Where(InSeatPool).ToList();
            if (candidates.Count == 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.NoEncountersInPool, pool, seat));
            var total = 0d;
            foreach (var e in candidates) total = total + e.Num(K.Weight);
            var r = rng.Float(RngStream.EnemyAI) * total;
            foreach (var e in candidates)
            {
                r -= e.Num(K.Weight);
                if (r < 0) return e.Str(K.Id);
            }
            return candidates[candidates.Count - 1].Str(K.Id);
        }

        /// <summary>
        /// resolveUnknownNode(registries, rng, { seenEvents, tier, history }): the tier's unknownWeights pick a kind; an
        /// event is drawn from the earned pool (quest gates met, gated steps not already answered), avoiding repeats
        /// while unseen events remain, and falls back to a fight when no event exists.
        /// </summary>
        public static ResolvedNode ResolveUnknownNode(MapData data, Rng rng, IReadOnlyCollection<string> seenEvents, int tier, JArray history)
        {
            var rules = data.Rules;
            var cfg = data.MapConfig(tier);
            var odds = cfg.Obj(MK.UnknownWeights);
            if (odds == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.UnknownNoWeights, tier));
            var total = 0d;
            foreach (var p in odds.Properties()) total = total + Js.D(p.Value);
            var r = rng.Float(RngStream.Events) * total;
            var kind = rules.UnknownEvent;
            foreach (var p in odds.Properties())
            {
                r -= Js.D(p.Value);
                if (r < 0)
                {
                    kind = p.Name;
                    break;
                }
            }
            if (kind != rules.UnknownEvent) return new ResolvedNode(kind);

            var rows = history ?? new JArray();
            var gates = data.EventHistoryRequirements;
            var completed = new HashSet<string>(rows.OfType<JObject>()
                .Where(row => row.Str(K.Kind) == rules.EventChoiceKind)
                .Select(row => row.Str(MK.EventId) ?? string.Empty), StringComparer.Ordinal);
            var earned = data.Events.Ids.Where(id =>
            {
                var gate = gates[id];
                if (!Js.Truthy(gate)) return true;
                return !completed.Contains(id) && EventHistory.RequirementMet(rules, gate, rows);
            }).ToList();
            var seen = seenEvents ?? Array.Empty<string>();
            var pool = earned.Where(id => !seen.Contains(id)).ToList();
            if (pool.Count == 0) pool = earned;
            if (pool.Count == 0) return new ResolvedNode(rules.UnknownFight);
            return new ResolvedNode(kind, rng.Pick(RngStream.Events, pool));
        }
    }
}
