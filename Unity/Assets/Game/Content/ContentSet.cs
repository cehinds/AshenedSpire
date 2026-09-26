using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// An immutable set of content documents (manifest path → JSON), e.g. the effective configuration of a run.
    /// Reads return deep clones so callers can never mutate the frozen set. The hash is canonical: object keys
    /// are sorted ordinally before hashing, so it is independent of property order and platform.
    /// </summary>
    public sealed class ContentSet
    {
        private readonly Dictionary<string, JToken> _files;
        private string _hash;

        public ContentSet(IDictionary<string, JToken> files)
        {
            _files = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var kv in files) _files[kv.Key] = kv.Value?.DeepClone();
        }

        public IEnumerable<string> Paths => _files.Keys.OrderBy(k => k, StringComparer.Ordinal);

        public bool Contains(string path) => _files.ContainsKey(path);

        public JToken Get(string path) => _files.TryGetValue(path, out var t) ? t?.DeepClone() : null;

        /// <summary>Value at a dotted path inside a file ("balance.startingCinders"), or null.</summary>
        public JToken Select(string path, string jsonPath) => (_files.TryGetValue(path, out var t) ? t : null)?.SelectToken(jsonPath)?.DeepClone();

        public string Hash => _hash ?? (_hash = ComputeHash());

        private string ComputeHash()
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var path in Paths)
                {
                    sb.Append(path).Append(Ashen.Generated.ContentLayout.Lf);
                    sb.Append(Canonical(_files[path])).Append(Ashen.Generated.ContentLayout.Lf);
                }
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) hex.Append(b.ToString(Ashen.Generated.ContentLayout.HexByteFormat, CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        /// <summary>Compact JSON with ordinally sorted object keys and invariant number formatting.</summary>
        public static string Canonical(JToken token)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(sw) { Formatting = Formatting.None, Culture = CultureInfo.InvariantCulture })
                Write(w, token);
            return sw.ToString();
        }

        private static void Write(JsonWriter w, JToken t)
        {
            switch (t)
            {
                case null:
                    w.WriteNull();
                    break;
                case JObject o:
                    w.WriteStartObject();
                    foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        w.WritePropertyName(p.Name);
                        Write(w, p.Value);
                    }
                    w.WriteEndObject();
                    break;
                case JArray a:
                    w.WriteStartArray();
                    foreach (var item in a) Write(w, item);
                    w.WriteEndArray();
                    break;
                default:
                    t.WriteTo(w);
                    break;
            }
        }
    }
}
