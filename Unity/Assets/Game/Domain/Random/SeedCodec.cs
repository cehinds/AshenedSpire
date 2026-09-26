using System;
using System.Collections.Generic;
using System.Text;

namespace Ashen.Domain.Random
{
    /// <summary>
    /// Seed display codec (rules/rng.json "seed"; ported from the shipped seedToString/seedFromString):
    /// base-N over the configured alphabet, homoglyphs folded (O→0), trimmed and upper-cased; longer strings wrap mod 2^32.
    /// </summary>
    public sealed class SeedCodec
    {
        private readonly string _alphabet;
        private readonly IReadOnlyList<KeyValuePair<string, string>> _homoglyphs;

        public SeedCodec(string alphabet, IReadOnlyList<KeyValuePair<string, string>> homoglyphs, int maxLength)
        {
            if (string.IsNullOrEmpty(alphabet)) throw new ArgumentException(nameof(alphabet));
            _alphabet = alphabet;
            _homoglyphs = homoglyphs ?? Array.Empty<KeyValuePair<string, string>>();
            MaxLength = maxLength;
        }

        public int MaxLength { get; }

        public string Format(uint seed)
        {
            var radix = (uint)_alphabet.Length;
            if (seed == 0) return _alphabet[0].ToString();
            var sb = new StringBuilder();
            var n = seed;
            while (n > 0)
            {
                sb.Insert(0, _alphabet[(int)(n % radix)]);
                n /= radix;
            }
            return sb.ToString();
        }

        /// <summary>Returns the first character that is not part of the vocabulary, or null when the text is a seed.</summary>
        public char? Problem(string text) => Scan(text, out _);

        public uint Parse(string text)
        {
            var bad = Scan(text, out var cleaned);
            if (bad.HasValue) throw new FormatException(bad.Value.ToString());
            var radix = (uint)_alphabet.Length;
            uint n = 0;
            unchecked { foreach (var ch in cleaned) n = n * radix + (uint)_alphabet.IndexOf(ch); }
            return n;
        }

        private char? Scan(string text, out string cleaned)
        {
            cleaned = (text ?? string.Empty).Trim().ToUpperInvariant();
            foreach (var pair in _homoglyphs) cleaned = cleaned.Replace(pair.Key, pair.Value);
            foreach (var ch in cleaned) if (_alphabet.IndexOf(ch) < 0) return ch;
            return null;
        }
    }
}
