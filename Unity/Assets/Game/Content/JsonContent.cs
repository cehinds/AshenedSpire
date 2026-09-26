using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// Deterministic JSON parsing for content (docs/design/08 §5): decimals never pass through double,
    /// dates are not interpreted, culture is invariant.
    /// </summary>
    public static class JsonContent
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            FloatParseHandling = FloatParseHandling.Decimal,
            DateParseHandling = DateParseHandling.None,
            Culture = CultureInfo.InvariantCulture,
            MaxDepth = 128,
        };

        public static JToken Parse(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)))
            {
                reader.FloatParseHandling = Settings.FloatParseHandling;
                reader.DateParseHandling = Settings.DateParseHandling;
                reader.Culture = Settings.Culture;
                reader.MaxDepth = Settings.MaxDepth;
                var token = JToken.ReadFrom(reader);
                while (reader.Read()) { }
                return token;
            }
        }

        public static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

        public static decimal ToDecimal(JToken t) => t.Value<decimal>();

        public static string Describe(JToken t)
        {
            if (t == null) return JTokenType.Null.ToString();
            return t.Type == JTokenType.String ? t.Value<string>() : t.ToString(Formatting.None);
        }
    }
}
