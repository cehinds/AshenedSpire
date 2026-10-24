using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;

namespace Ashen.App.Ui
{
    /// <summary>One word waiting to show over a fighter: who arrived when, and how it ranks.</summary>
    public struct CalloutWord
    {
        public int Arrival;
        public double Rank;
    }

    /// <summary>Where and when a word shows: its arrival, the ms from now it starts and the lane (0 is the top) it takes.</summary>
    public struct CalloutSlot
    {
        public int Arrival;
        public double Start;
        public int Lane;
    }

    /// <summary>
    /// The schedule of the words over one fighter (src/ui/fx.js planWordQueue, owner ruling 2026-10-12): they start in priority order one step apart, at most
    /// <c>lanes</c> show at once, and each later word waits for the earliest lane to finish; nothing is dropped. Engine-free so the tests pin it.
    /// </summary>
    public static class CalloutPlan
    {
        /// <summary>Priority of a word, highest first (fx.js stackPriority): a status word, then a miss, immunity, the breaks, a block, a resist.</summary>
        public static double Rank(CalloutDefs defs, string kind, bool status)
        {
            var p = defs.Priority;
            if (status) return p[FxPriority.Status];
            return p.TryGetValue(kind ?? string.Empty, out var rank) ? rank : p[FxPriority.Other];
        }

        /// <param name="items">The words to start, in arrival order.</param>
        /// <param name="lanes">How long each lane is still busy, from now.</param>
        /// <param name="after">When the last word before this batch started (ms from now), or null.</param>
        public static List<CalloutSlot> Plan(IReadOnlyList<CalloutWord> items, double[] lanes, double stepMs, double lifeMs, double? after)
        {
            var sorted = items.OrderByDescending(i => i.Rank).ThenBy(i => i.Arrival).ToList();
            var free = (double[])lanes.Clone();
            var result = new List<CalloutSlot>();
            var previous = after;
            foreach (var item in sorted)
            {
                var lane = 0;
                for (var l = 1; l < free.Length; l++) if (free[l] < free[lane]) lane = l;
                var start = Math.Max(0, free[lane]);
                if (previous.HasValue) start = Math.Max(start, previous.Value + stepMs);
                free[lane] = start + lifeMs;
                previous = start;
                result.Add(new CalloutSlot { Arrival = item.Arrival, Start = start, Lane = lane });
            }
            return result;
        }
    }
}
