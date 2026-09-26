using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;

namespace Ashen.Domain.Map
{
    /// <summary>
    /// The seat readers the map needs (shipped model/seats.js and model/bossDestinationLabels.js): the tier a content
    /// act is, the final tier, whether a boss row may be a destination for a seat at a tier, and a destination's label.
    /// </summary>
    public static class MapSeats
    {
        /// <summary>tierOf(contentAct, cycle): 1..cycle, looping for Endless (<c>Math.trunc(Number(x) || 1)</c>, floored at 1).</summary>
        public static int TierOf(double contentAct, double cycle)
        {
            var n = Math.Max(1, Math.Truncate(Truthy(contentAct) ? contentAct : 1));
            var len = Math.Max(1, Math.Truncate(Truthy(cycle) ? cycle : 1));
            return (int)(((n - 1) % len) + 1);
        }

        /// <summary>finalTier(registries) = balance.endless.actsPerCycle.</summary>
        public static int FinalTier(MapData data) => (int)data.Endless.Num(MK.ActsPerCycle);

        /// <summary>
        /// encounterFitsSeat(encounter, { seat, tier, finalTier }): the seat's own rows, or the one null-seat row at the
        /// final tier (SPEC §13.5).
        /// </summary>
        public static bool EncounterFitsSeat(JObject encounter, string seat, int tier, int finalTier)
        {
            if (encounter == null) return false;
            var own = encounter[MK.Seat];
            if (Js.IsStr(own) && Js.Str(own) == seat) return true;
            return own != null && own.Type == JTokenType.Null && TierOf(tier, finalTier) == finalTier;
        }

        /// <summary>bossDestinationLabel: "[location · ]enemy names joined by ' &amp; '".</summary>
        public static string BossDestinationLabel(MapData data, string encounterId)
        {
            var encounter = data.Encounters.Get(encounterId);
            var enemies = string.Join(data.Rules.EnemySeparator, Js.Items(encounter[K.Enemies]).Select(id => JoinText(data.Enemies.Get(Js.Str(id))[K.Name])));
            var parts = new List<string>();
            var location = data.BossLocations[encounterId];
            if (Js.Truthy(location)) parts.Add(JoinText(location));
            if (enemies.Length > 0) parts.Add(enemies);
            return string.Join(data.Rules.PartSeparator, parts);
        }

        /// <summary>drawSeatOrder(registries, rng, { firstSeat }): the seated order draw (one shuffle on the seats stream).</summary>
        public static IReadOnlyList<string> DrawSeatOrder(MapData data, Rng rng, string firstSeat = null) =>
            SeatOrder.Draw(rng, data.Seats.All.Select(s => new SeatInfo(s.Str(K.Id), (int)s.Num(MK.BaseTier))), firstSeat);

        /// <summary>How Array.prototype.join prints a value: null/undefined as "", strings as-is, anything else as JSON.</summary>
        private static string JoinText(JToken t)
        {
            if (Js.Nullish(t)) return string.Empty;
            return Js.IsStr(t) ? Js.Str(t) : t.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool Truthy(double d) => d != 0 && !double.IsNaN(d);
    }
}
