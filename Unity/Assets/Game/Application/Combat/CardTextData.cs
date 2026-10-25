using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using T = Ashen.Generated.CardTextKeys;

namespace Ashen.App.Combat
{
    /// <summary>
    /// The words and lists the card face generator reads (P-1; ui/cardText.json for the lists, strings/app.en.json cardtext.* for the sentences). The web writes a card's
    /// rules in model/cardFaceText.js out of English fragments; here each fragment is a string-table row filled with {0}, {1} and the lists are data, so the generator
    /// itself holds no wording (the enforcement rule).
    /// </summary>
    public sealed class CardTextData
    {
        public readonly StringTable Strings;
        public readonly JObject Json;

        private readonly List<KeyValuePair<Regex, string>> _compact = new List<KeyValuePair<Regex, string>>();
        private readonly List<KeyValuePair<string, string>> _escapes = new List<KeyValuePair<string, string>>();

        public CardTextData(StringTable strings, JObject json)
        {
            Strings = strings ?? throw new ArgumentNullException(nameof(strings));
            Json = json ?? throw new ArgumentNullException(nameof(json));
            foreach (var pair in Js.Items(json[T.Compact]).OfType<JArray>())
                _compact.Add(new KeyValuePair<Regex, string>(new Regex(pair[0].Value<string>(), RegexOptions.CultureInvariant), pair[1].Value<string>()));
            foreach (var pair in Js.Items(json[T.Escapes]).OfType<JArray>()) _escapes.Add(new KeyValuePair<string, string>(pair[0].Value<string>(), pair[1].Value<string>()));
        }

        // ------------------------------------------------------------------ lists

        public List<string> List(string key) => Js.Items(Json[key]).Select(Js.Str).ToList();

        public bool In(string key, string value) => value != null && List(key).Contains(value);

        public string Map(string key, string id) => id != null && Json[key] is JObject map && Js.IsStr(map[id]) ? map.Str(id) : null;

        public JObject Table(string key) => Json[key] as JObject ?? new JObject();

        // ------------------------------------------------------------------ words

        /// <summary>A sentence of the table with {0}, {1} ... filled.</summary>
        public string Say(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, Strings.Get(key), args);

        public string Word(string key) => Strings.Get(key);

        /// <summary>esc(value): the characters the html cannot carry, as the web escapes them (&amp; first).</summary>
        public string Esc(string value)
        {
            var text = value ?? string.Empty;
            foreach (var pair in _escapes) text = text.Replace(pair.Key, pair.Value);
            return text;
        }

        /// <summary>card.js's esc (ui/components/tooltip.js): the same without the apostrophe, which the card's own template text keeps.</summary>
        public string EscBasic(string value)
        {
            var text = value ?? string.Empty;
            foreach (var pair in _escapes.Take(_escapes.Count - 1)) text = text.Replace(pair.Key, pair.Value);
            return text;
        }

        /// <summary>The reverse of <see cref="Esc"/>: &amp; is decoded last, so an escaped entity stays literal text.</summary>
        public string Unesc(string value)
        {
            var text = value ?? string.Empty;
            for (var i = _escapes.Count - 1; i >= 0; i--) text = text.Replace(_escapes[i].Value, _escapes[i].Key);
            return text;
        }

        /// <summary>compactCardRules(html): the shortenings the face applies to the joined lines (in the web's order).</summary>
        public string Compact(string html)
        {
            var text = Regex.Replace(html ?? string.Empty, Json.Str(T.Whitespace), Json.Str(T.Space));
            foreach (var pair in _compact) text = pair.Key.Replace(text, pair.Value);
            return text.Trim();
        }

        /// <summary>JS String(number): whole numbers without a fraction, others in the shortest form.</summary>
        public static string Num(double value) =>
            value == Math.Floor(value) && Math.Abs(value) < long.MaxValue ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

        /// <summary>JS String(value) of a JSON number or string.</summary>
        public static string Show(JToken value) => Js.IsNum(value) ? Num(Js.D(value)) : Js.IsStr(value) ? Js.Str(value) : value?.ToString() ?? string.Empty;
    }
}
