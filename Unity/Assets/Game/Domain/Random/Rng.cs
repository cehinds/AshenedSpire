using System;
using System.Collections.Generic;
using Ashen.Generated;

namespace Ashen.Domain.Random
{
    /// <summary>
    /// Deterministic named-stream PRNG (US-0.5; docs/design/08 §5, PF-07), bit-identical to the shipped
    /// engine/rng.js: mulberry32 per stream, base = seed XOR fnv1a(streamName), value at draw i computed in O(1)
    /// so a restored counter is exact. Integer-only — draws are mapped to ranges with (u * n) >> 32, which equals
    /// the shipped Math.floor(float * n) for n below 2^21 (proven by the parity oracle).
    /// </summary>
    public sealed class Rng
    {
        private static readonly RngStream[] Streams = (RngStream[])Enum.GetValues(typeof(RngStream));
        private readonly uint[] _bases;
        private readonly uint[] _counters;

        public Rng(uint seed, IReadOnlyDictionary<RngStream, uint> counters = null)
        {
            Seed = seed;
            _bases = new uint[Streams.Length];
            _counters = new uint[Streams.Length];
            foreach (var s in Streams)
            {
                if (s == RngStream.None) continue;
                _bases[(int)s] = seed ^ Fnv1a(RngStreamNames.ToWire(s));
                if (counters != null && counters.TryGetValue(s, out var c)) _counters[(int)s] = c;
            }
        }

        public uint Seed { get; }

        /// <summary>Next raw 32-bit draw on a stream (the shipped float is this value / 2^32).</summary>
        public uint NextUInt(RngStream stream)
        {
            var i = Index(stream);
            _counters[i] = unchecked(_counters[i] + 1);
            return ValueAt(_bases[i], _counters[i]);
        }

        /// <summary>Uniform integer in [0, n) — equals Math.floor(rng.float() * n).</summary>
        public int Below(RngStream stream, int n)
        {
            if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
            return (int)(((ulong)NextUInt(stream) * (ulong)n) >> (int)RngAlgorithm.FractionBits);
        }

        /// <summary>Uniform integer in [min, max] inclusive — rng.int(stream, min, max).</summary>
        public int Int(RngStream stream, int min, int max)
        {
            if (max < min) throw new ArgumentOutOfRangeException(nameof(max));
            return min + Below(stream, max - min + 1);
        }

        public T Pick<T>(RngStream stream, IReadOnlyList<T> items)
        {
            if (items == null || items.Count == 0) throw new ArgumentException(nameof(items));
            return items[Below(stream, items.Count)];
        }

        /// <summary>New list, Fisher–Yates from the end, identical draw order to the shipped shuffle.</summary>
        public List<T> Shuffle<T>(RngStream stream, IReadOnlyList<T> items)
        {
            var list = new List<T>(items);
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = Below(stream, i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
            return list;
        }

        /// <summary>
        /// True with pct% probability — rng.chance(stream, pct) is float * 100 &lt; pct. Takes pct in hundredths
        /// (fixed point, docs/design/08 §5): u/2^32 * 100 &lt; p/100  ⇔  u * 100 * 100 &lt; p * 2^32.
        /// </summary>
        public bool Chance(RngStream stream, long pctHundredths)
        {
            var u = (ulong)NextUInt(stream);
            var scale = (ulong)RngAlgorithm.PercentScale;
            return pctHundredths > 0 && u * scale * scale < ((ulong)pctHundredths << (int)RngAlgorithm.FractionBits);
        }

        public uint Counter(RngStream stream) => _counters[Index(stream)];

        public Dictionary<RngStream, uint> Counters()
        {
            var d = new Dictionary<RngStream, uint>();
            foreach (var s in Streams) if (s != RngStream.None) d[s] = _counters[(int)s];
            return d;
        }

        /// <summary>A copy with identical seed and counters (used by previews so they never advance the real streams).</summary>
        public Rng Clone() => new Rng(Seed, Counters());

        private static int Index(RngStream stream)
        {
            if (stream == RngStream.None) throw new ArgumentException(nameof(stream));
            return (int)stream;
        }

        internal static uint ValueAt(uint streamBase, uint counter) => Scramble(unchecked(streamBase + counter * RngAlgorithm.Increment));

        internal static uint Scramble(uint a)
        {
            unchecked
            {
                var t = a;
                t = (t ^ (t >> (int)RngAlgorithm.ShiftA)) * (t | RngAlgorithm.OrA);
                t ^= t + (t ^ (t >> (int)RngAlgorithm.ShiftB)) * (t | RngAlgorithm.OrB);
                return t ^ (t >> (int)RngAlgorithm.ShiftC);
            }
        }

        /// <summary>FNV-1a over UTF-16 code units (the shipped hashString uses charCodeAt).</summary>
        internal static uint Fnv1a(string text)
        {
            unchecked
            {
                var h = RngAlgorithm.FnvOffset;
                foreach (var ch in text)
                {
                    h ^= ch;
                    h *= RngAlgorithm.FnvPrime;
                }
                return h;
            }
        }
    }
}
