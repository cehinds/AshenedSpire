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
    /// The ONE act-boot path (shipped engine/actmap.js): apply the run shape, generate the map on the 'map' stream,
    /// pre-roll every Unknown node on the 'events' stream in node order (each resolved event joining the no-repeat
    /// list), then seat the seat's boss destinations. Draw order is the shipped one, so every seed replays.
    /// </summary>
    public static class ActMap
    {
        /// <summary>buildActMap(registries, rng, seat, tier, mapShape, { history }).</summary>
        public static ActMapGraph BuildActMap(MapData data, Rng rng, string seat, int tier, JToken mapShape = null, JArray history = null)
        {
            if (string.IsNullOrEmpty(seat)) throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, MM.BuildSeatRequired, seat), nameof(seat));
            if (tier < 1) throw new ArgumentOutOfRangeException(nameof(tier), string.Format(CultureInfo.InvariantCulture, MM.BuildTierInvalid, tier));
            data.Seats.Get(seat);
            var rules = data.Rules;
            var authored = data.MapConfig(tier);
            var shaped = FloorPlans.ApplyRunShape(rules, authored, mapShape, data.MapShapeLimits);
            if (shaped.Errors.Count > 0)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.BuildShapeRefused, MapError.Join(shaped.Errors)));
            var map = MapGen.GenerateActMap(rules, shaped.Config, rng);
            var assigned = new List<string>();
            foreach (var node in map.AllNodes())
            {
                if (node.Type != rules.Event) continue;
                node.Resolved = MapEncounters.ResolveUnknownNode(data, rng, assigned, tier, history);
                if (node.Resolved.Kind == rules.UnknownEvent) assigned.Add(node.Resolved.EventId);
            }
            var finalTier = MapSeats.FinalTier(data);
            var pool = data.Encounters.All.Where(e => e.Str(MK.Pool) == rules.BossPool && MapSeats.EncounterFitsSeat(e, seat, tier, finalTier)).ToList();
            if (pool.Count == 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.BuildNoBoss, seat));
            var selected = pool.Count > map.Columns ? rng.Shuffle(RngStream.Map, pool).Take(map.Columns).ToList() : pool;
            return MapGen.AssignBossDestinations(rules, map, selected
                .Select(e => new BossDestination(e.Str(K.Id), MapSeats.BossDestinationLabel(data, e.Str(K.Id))))
                .ToList());
        }

        /// <summary>
        /// bossEncounterForNode(registries, graph, nodeId, { seat, tier }): the authoritative encounter behind a boss
        /// terminal. A legacy singular graph (no bossIds) maps its lone boss by TIER through LEGACY_ACT_BOSSES. Read-only.
        /// </summary>
        public static string BossEncounterForNode(MapData data, ActMapGraph graph, string nodeId, string seat, int tier)
        {
            if (string.IsNullOrEmpty(seat)) throw new ArgumentException(MM.BossNeedsSeatTier, nameof(seat));
            var node = graph?.Node(nodeId);
            if (node == null || node.Type != data.Rules.Boss) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.BossNotBossNode, nodeId));
            var encounterId = !string.IsNullOrEmpty(node.EncounterId) ? node.EncounterId
                : graph.BossIds == null && graph.BossId == nodeId ? Js.Str(data.LegacyActBosses[tier.ToString(CultureInfo.InvariantCulture)]) : null;
            var encounter = !string.IsNullOrEmpty(encounterId) && data.Encounters.Has(encounterId) ? data.Encounters.Get(encounterId) : null;
            if (encounter == null || encounter.Str(MK.Pool) != data.Rules.BossPool || !MapSeats.EncounterFitsSeat(encounter, seat, tier, MapSeats.FinalTier(data)))
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.BossNoEncounter, nodeId, seat, tier));
            return encounterId;
        }

        /// <summary>drawSeatOrder(registries, rng, { firstSeat }): one shuffle on the seats stream (see <see cref="Ashen.Domain.Run.SeatOrder"/>).</summary>
        public static IReadOnlyList<string> DrawSeatOrder(MapData data, Rng rng, string firstSeat = null) => MapSeats.DrawSeatOrder(data, rng, firstSeat);
    }
}
