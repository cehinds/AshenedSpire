using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// JavaScript value semantics over Newtonsoft tokens, for the combat port (D-036, D-037): the combat document
    /// is a JSON object graph exactly as the shipped engine holds it, and its arithmetic is IEEE double. A missing
    /// property and a JSON null both read as JS <c>undefined</c>/<c>null</c>.
    /// </summary>
    public static class Js
    {
        /// <summary>JS truthiness: null/undefined, false, 0, NaN and "" are falsy; objects and arrays are truthy.</summary>
        public static bool Truthy(JToken t)
        {
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return false;
                case JTokenType.Boolean:
                    return t.Value<bool>();
                case JTokenType.Integer:
                case JTokenType.Float:
                    var d = D(t);
                    return d != 0 && !double.IsNaN(d);
                case JTokenType.String:
                    return t.Value<string>().Length > 0;
                default:
                    return true;
            }
        }

        /// <summary><c>t == null</c> in JS (null or undefined).</summary>
        public static bool Nullish(JToken t) => t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined;

        /// <summary><c>typeof t === 'number'</c>.</summary>
        public static bool IsNum(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

        /// <summary><c>typeof t === 'string'</c>.</summary>
        public static bool IsStr(JToken t) => t != null && t.Type == JTokenType.String;

        /// <summary><c>Number.isFinite(t)</c>.</summary>
        public static bool IsFinite(JToken t) => IsNum(t) && !double.IsNaN(D(t)) && !double.IsInfinity(D(t));

        /// <summary><c>Number.isInteger(t)</c>.</summary>
        public static bool IsInt(JToken t) => IsFinite(t) && Math.Floor(D(t)) == D(t);

        /// <summary>The string a token holds, or null when it is not one (so <c>x === 'lit'</c> is <c>Str(x) == lit</c>).</summary>
        public static string Str(JToken t) => IsStr(t) ? t.Value<string>() : null;

        /// <summary>The IEEE double JS holds for a number token; NaN for anything else (JS arithmetic on undefined).</summary>
        public static double D(JToken t)
        {
            if (!IsNum(t)) return double.NaN;
            var v = ((JValue)t).Value;
            switch (v)
            {
                case double d:
                    return d;
                case long l:
                    return l;
                case int i:
                    return i;
                case float f:
                    return f;
                case decimal m:
                    return double.Parse(m.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
                default:
                    return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary><c>t || 0</c> for a numeric slot: the number when truthy, else 0.</summary>
        public static double Or0(JToken t) => Truthy(t) && IsNum(t) ? D(t) : 0;

        /// <summary><c>typeof t === 'number' ? t : fallback</c>.</summary>
        public static double NumOr(JToken t, double fallback) => IsNum(t) ? D(t) : fallback;

        /// <summary><c>t != null ? t : fallback</c> for a numeric slot.</summary>
        public static double Coalesce(JToken t, double fallback) => Nullish(t) ? fallback : D(t);

        /// <summary>A computed JS number as JSON: integral values as integers (as JSON.stringify prints them).</summary>
        public static JValue N(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return JValue.CreateNull();
            if (d == Math.Floor(d) && Math.Abs(d) < Limits.SafeInteger) return new JValue((long)d);
            return new JValue(d);
        }

        public static JValue S(string s) => s == null ? JValue.CreateNull() : new JValue(s);

        public static JValue B(bool b) => new JValue(b);

        public static JValue Null() => JValue.CreateNull();

        /// <summary><c>obj?.[key]</c>.</summary>
        public static JToken Get(JToken obj, string key) => obj is JObject o ? o[key] : null;

        /// <summary>The array items of a token (a non-array reads as empty, like <c>(x || [])</c>).</summary>
        public static IEnumerable<JToken> Items(JToken t)
        {
            if (t is JArray a) foreach (var item in a) yield return item;
        }

        /// <summary><c>Array.isArray(t) &amp;&amp; t.includes(s)</c> for a string.</summary>
        public static bool Includes(JToken array, string s)
        {
            if (!(array is JArray a) || s == null) return false;
            foreach (var item in a) if (Str(item) == s) return true;
            return false;
        }

        /// <summary><c>{ ...a, ...b }</c>: a new object; later sources overwrite earlier keys in place (key order kept).</summary>
        public static JObject Spread(params JObject[] sources)
        {
            var result = new JObject();
            foreach (var source in sources)
            {
                if (source == null) continue;
                foreach (var p in source.Properties()) result[p.Name] = p.Value.DeepClone();
            }
            return result;
        }

        /// <summary>Ordinal comparison, the JS default for string <c>&lt;</c> and <c>Array.prototype.sort</c>.</summary>
        public static int Cmp(string a, string b) => string.CompareOrdinal(a, b);

        /// <summary>A JSON object built from alternating key/value pairs, skipping undefined (null reference) values.</summary>
        public static JObject Obj(params object[] pairs)
        {
            var o = new JObject();
            for (var i = 0; i + 1 < pairs.Length; i++)
            {
                var key = (string)pairs[i];
                var value = pairs[++i];
                if (value == null) continue;
                o[key] = Token(value);
            }
            return o;
        }

        /// <summary>A C# value as a token (numbers as JS numbers).</summary>
        public static JToken Token(object value)
        {
            switch (value)
            {
                case null:
                    return JValue.CreateNull();
                case JToken t:
                    return t;
                case string s:
                    return new JValue(s);
                case bool b:
                    return new JValue(b);
                case double d:
                    return N(d);
                case int i:
                    return N(i);
                case long l:
                    return N(l);
                default:
                    return JToken.FromObject(value);
            }
        }

        /// <summary>Number limits of the JS runtime (Number.MAX_SAFE_INTEGER bounds integral printing).</summary>
        private static class Limits
        {
            public static readonly double SafeInteger = Math.Pow(Ashen.Generated.CombatMath.Two, Ashen.Generated.CombatMath.SafeIntegerBits);
        }
    }

    /// <summary>Field access on combat documents with JS semantics (see <see cref="Js"/>).</summary>
    public static class JsExt
    {
        public static double Num(this JObject o, string key) => Js.D(o?[key]);

        public static double Or0(this JObject o, string key) => Js.Or0(o?[key]);

        public static bool Is(this JObject o, string key) => Js.Truthy(o?[key]);

        public static string Str(this JObject o, string key) => Js.Str(o?[key]);

        public static JObject Obj(this JObject o, string key) => o?[key] as JObject;

        public static JArray Arr(this JObject o, string key) => o?[key] as JArray;

        public static void Put(this JObject o, string key, double value) => o[key] = Js.N(value);

        public static void Put(this JObject o, string key, string value) => o[key] = Js.S(value);

        public static void Put(this JObject o, string key, bool value) => o[key] = new JValue(value);

        public static bool Has(this JObject o, string key) => o != null && o[key] != null;
    }

    /// <summary>An insertion-ordered string map (the shipped engine's Map and plain-object key order).</summary>
    public sealed class OrderedMap<T>
    {
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, T> _values = new Dictionary<string, T>(StringComparer.Ordinal);

        public int Count => _keys.Count;

        public IReadOnlyList<string> Keys => _keys;

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        public bool TryGetValue(string key, out T value) => _values.TryGetValue(key, out value);

        public T this[string key]
        {
            get => _values[key];
            set
            {
                if (!_values.ContainsKey(key)) _keys.Add(key);
                _values[key] = value;
            }
        }

        public bool Remove(string key)
        {
            if (!_values.Remove(key)) return false;
            _keys.Remove(key);
            return true;
        }

        public IEnumerable<KeyValuePair<string, T>> Entries()
        {
            foreach (var k in _keys.ToArray()) yield return new KeyValuePair<string, T>(k, _values[k]);
        }

        /// <summary>Keys in ordinal order (<c>Object.keys(m).sort()</c>).</summary>
        public List<string> SortedKeys()
        {
            var keys = new List<string>(_keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }
}
