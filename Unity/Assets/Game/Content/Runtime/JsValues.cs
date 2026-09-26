using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// The JavaScript value semantics the shipped registry transforms rely on, over Newtonsoft tokens: truthiness,
    /// IEEE-double numbers (content parses as decimal; the shipped values are doubles), shallow object spread.
    /// A missing property and a JSON null both read as JS <c>undefined</c>/<c>null</c>.
    /// </summary>
    internal static class JsValues
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
                    var d = ToDouble(t);
                    return d != 0 && !double.IsNaN(d);
                case JTokenType.String:
                    return t.Value<string>().Length > 0;
                default:
                    return true;
            }
        }

        /// <summary>null or undefined (the operands <c>??</c> and <c>?.</c> skip).</summary>
        public static bool IsNullish(JToken t) => t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined;

        /// <summary><c>typeof t === 'number'</c>.</summary>
        public static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

        public static bool IsString(JToken t) => t != null && t.Type == JTokenType.String;

        /// <summary>The string a token holds, or null when it is not a string (so <c>x === 'lit'</c> is <c>Str(x) == lit</c>).</summary>
        public static string Str(JToken t) => IsString(t) ? t.Value<string>() : null;

        /// <summary><c>Number.isFinite(t)</c>.</summary>
        public static bool IsFinite(JToken t) => IsNumber(t) && !double.IsNaN(ToDouble(t)) && !double.IsInfinity(ToDouble(t));

        /// <summary>The IEEE double JS would hold for a JSON number (parsed from its shortest decimal text).</summary>
        public static double ToDouble(JToken t)
        {
            var v = (JValue)t;
            switch (v.Value)
            {
                case double d:
                    return d;
                case float f:
                    return f;
                case long l:
                    return l;
                case int i:
                    return i;
                case decimal m:
                    return double.Parse(m.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
                default:
                    return double.Parse(Convert.ToString(v.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>A number as a JS value, or NaN when it is not one (a missing multiplier poisons the formula, as in JS).</summary>
        public static double NumberOrNaN(JToken t) => IsNumber(t) ? ToDouble(t) : double.NaN;

        /// <summary>A computed JS number as JSON: integral values as integers, NaN/Infinity as null (JSON.stringify).</summary>
        public static JToken Number(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return JValue.CreateNull();
            if (d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue) return new JValue((long)d);
            return new JValue(d);
        }

        /// <summary>The text of an id-like value used in a key (ids are strings; anything else keys by its JSON text).</summary>
        public static string Text(JToken t)
        {
            if (IsNullish(t)) return string.Empty;
            if (t.Type == JTokenType.String) return t.Value<string>();
            if (IsNumber(t)) return ToDouble(t).ToString(CultureInfo.InvariantCulture);
            return t.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary><c>obj?.[key]</c>: the property of an object, else null (undefined).</summary>
        public static JToken Get(JToken obj, string key) => obj is JObject o ? o[key] : null;

        /// <summary><c>{ ...a, ...b, ... }</c>: a new object; later sources overwrite earlier keys in place.</summary>
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

        /// <summary>The object rows of an array (a non-array reads as empty, like <c>(x || [])</c> over a malformed table).</summary>
        public static IEnumerable<JToken> Items(JToken t)
        {
            if (t is JArray a) foreach (var item in a) yield return item;
        }
    }
}
