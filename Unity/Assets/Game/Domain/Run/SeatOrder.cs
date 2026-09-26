using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Random;
using Ashen.Generated;

namespace Ashen.Domain.Run
{
    /// <summary>A seat as the order draw needs it: id and base tier (catalog/seats.json).</summary>
    public readonly struct SeatInfo
    {
        public SeatInfo(string id, int baseTier)
        {
            Id = id;
            BaseTier = baseTier;
        }

        public string Id { get; }
        public int BaseTier { get; }
    }

    /// <summary>
    /// Seeded seat order (US-4.1; shipped drawSeatOrder): default order is by base tier then id (ordinal), one
    /// shuffle on the seats stream; a pinned first seat ROTATES the drawn order so every other stream is untouched.
    /// </summary>
    public static class SeatOrder
    {
        public static IReadOnlyList<string> DefaultOrder(IEnumerable<SeatInfo> seats) =>
            seats.OrderBy(s => s.BaseTier).ThenBy(s => s.Id, StringComparer.Ordinal).Select(s => s.Id).ToList();

        public static IReadOnlyList<string> Draw(Rng rng, IEnumerable<SeatInfo> seats, string firstSeat = null)
        {
            var drawn = rng.Shuffle(RngStream.Seats, DefaultOrder(seats));
            if (firstSeat == null) return drawn;
            var at = drawn.IndexOf(firstSeat);
            if (at < 0) throw new ArgumentException(firstSeat, nameof(firstSeat));
            return drawn.Skip(at).Concat(drawn.Take(at)).ToList();
        }
    }
}
