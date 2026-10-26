using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using AK = Ashen.Generated.Core2AutoReactionKeys;
using AV = Ashen.Generated.Core2AutoReactionValues;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.Core2ReactionKeys;
using RN = Ashen.Generated.Core2ReactionNumbers;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Auto-combat's reaction chooser (web model/autoCombatPolicy.js; owner ruling D12, 2026-10-18): auto-combat automates the player's reaction choices
    /// ONLY. It never plays a card and never ends a turn. The chooser is pure and deterministic and reads only what the seat itself can see: the intent as the
    /// seat perceives it (a hidden or unrevealed intent is treated as unknown), the maneuver of its last stance and its own manual reaction picks. The
    /// order is: the reaction that counters the perceived action, then (only while the action is unknown) the one that matches the last stance, then the
    /// most often manually picked kind, then source order. It answers with an option or with null, a deliberate Back.
    /// </summary>
    public static class AutoReactionPolicy
    {
        private static readonly Dictionary<string, string> Normal = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AV.Casting] = AV.Prepared, [AV.Preparing] = AV.Prepared, [AV.Cast] = AV.Prepared, [AV.Ranged] = AV.Attack, [AV.Attacking] = AV.Attack,
            [AV.Smashing] = AV.Smash, [AV.Sweeping] = AV.Sweep, [AV.Defending] = AV.Defend, [AV.Countering] = AV.Counter, [AV.Evading] = AV.Evade, [AV.Block] = AV.Defend,
        };

        private static string Normalize(string value) => value != null && Normal.TryGetValue(value, out var mapped) ? mapped : string.IsNullOrEmpty(value) ? null : value;

        /// <summary>perceivedManeuver(intent): the incoming maneuver as the seat perceives it, or null when it is not known.</summary>
        public static string PerceivedManeuver(JObject intent)
        {
            if (intent == null || intent.Is(AK.Hidden) || (intent[AK.Revealed]?.Type == JTokenType.Boolean && !intent.Is(AK.Revealed))) return null;
            var maneuver = intent.Obj(AK.Profile)?.Str(AK.Maneuver);
            return Normalize(!string.IsNullOrEmpty(maneuver) ? maneuver : intent.Str(AK.Stance));
        }

        /// <summary>matchupRank: 2 = the reaction counters the perceived action, 1 = neutral, 0 = the action counters it.</summary>
        private static int MatchupRank(string kind, string incoming, JObject table)
        {
            if (kind == null || incoming == null) return 1;
            if (Js.Includes(table?[kind], incoming)) return RN.CounterRank;
            if (Js.Includes(table?[incoming], kind)) return 0;
            return 1;
        }

        private static bool Beats(int[] a, int[] b)
        {
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return a[i] > b[i];
            return false;
        }

        /// <summary>
        /// chooseReaction(state, seat, options): one option, or null to decline. <paramref name="state"/> holds <c>matchups</c> (the stance matchup table) and
        /// <c>seats[seat]</c>: <c>intent</c> (the seat's own previewed intent), <c>lastStance</c> and <c>picks</c> (a tally of its manual picks by maneuver).
        /// Each option carries an <c>id</c> and the <c>kind</c> of the reaction card.
        /// </summary>
        public static JObject Choose(JObject state, string seat, IReadOnlyList<JObject> options)
        {
            var own = state?.Obj(AK.Seats)?.Obj(seat) ?? new JObject();
            var incoming = PerceivedManeuver(own.Obj(AK.Intent));
            var last = Normalize(own.Str(AK.LastStance));
            JObject best = null;
            int[] bestKey = null;
            var bestSignal = false;
            foreach (var option in options)
            {
                var kind = Normalize(option?.Str(RK.Kind));
                var rank = MatchupRank(kind, incoming, state?.Obj(AK.Matchups));
                var stance = incoming == null && last != null && kind == last ? 1 : 0;
                var tally = kind == null ? null : own.Obj(AK.Picks)?[kind];
                var picks = (int)Math.Max(0, Js.IsFinite(tally) ? Js.D(tally) : 0);
                var signal = rank > 0 && (rank == RN.CounterRank || stance == 1 || picks > 0);
                var key = new[] { rank, stance, picks };
                if (best == null || Beats(key, bestKey)) { best = option; bestKey = key; bestSignal = signal; }
            }
            return bestSignal ? best : null;
        }

        /// <summary>reactionAnswer(pending, picked): the intent that answers an offer; a null choice is a deliberate decline, never "do nothing".</summary>
        public static JObject Answer(JObject pending, JObject picked) =>
            new JObject { [RK.OfferId] = pending[K.Id]?.DeepClone(), [RK.OptionId] = picked?[K.Id]?.DeepClone() ?? Js.Null() };
    }
}
