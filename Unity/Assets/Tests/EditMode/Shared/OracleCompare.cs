using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ashen.Tests
{
    /// <summary>The oracle documents' comparisons and readers, shared by the parity tests (kept from the 405 run's parity tests, WP-21).</summary>
    internal static class OracleCompare
    {
        /// <summary>
        /// Order-insensitive deep equality with numbers compared as doubles. Unlike the combat projection compare, an
        /// absent key and an explicit null differ: the shipped document drops undefined and keeps null.
        /// </summary>
        internal static string FirstDifference(JToken a, JToken b, string path)
        {
            if (a == null || b == null) return a == null && b == null ? null : $"{path}: {Show(a)} vs {Show(b)}";
            if (Js.IsNum(a) && Js.IsNum(b)) return Js.D(a) == Js.D(b) ? null : $"{path}: {Show(a)} vs {Show(b)}";
            if (a.Type != b.Type) return $"{path}: {Show(a)} vs {Show(b)}";
            if (a is JObject oa)
            {
                var ob = (JObject)b;
                foreach (var name in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)))
                {
                    var d = FirstDifference(oa[name], ob[name], path + "." + name);
                    if (d != null) return d;
                }
                return null;
            }
            if (a is JArray aa)
            {
                var ab = (JArray)b;
                if (aa.Count != ab.Count) return $"{path}: length {aa.Count} vs {ab.Count}";
                for (var i = 0; i < aa.Count; i++)
                {
                    var d = FirstDifference(aa[i], ab[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    if (d != null) return d;
                }
                return null;
            }
            return JToken.DeepEquals(a, b) ? null : $"{path}: {Show(a)} vs {Show(b)}";
        }

        /// <summary>
        /// Strict deep equality: an absent key and an explicit null differ (D-045), numbers compare as doubles, and
        /// object keys must appear in the same order (D-040: the shipped insertion order is part of the document).
        /// </summary>
        internal static string FirstDifferenceOrdered(JToken a, JToken b, string path)
        {
            if (a == null || b == null) return a == null && b == null ? null : $"{path}: {Show(a)} vs {Show(b)}";
            if (Js.IsNum(a) && Js.IsNum(b)) return Js.D(a) == Js.D(b) ? null : $"{path}: {Show(a)} vs {Show(b)}";
            if (a.Type != b.Type) return $"{path}: {Show(a)} vs {Show(b)}";
            if (a is JObject oa)
            {
                var ob = (JObject)b;
                foreach (var name in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)))
                {
                    var d = FirstDifferenceOrdered(oa[name], ob[name], path + "." + name);
                    if (d != null) return d;
                }
                var ka = oa.Properties().Select(p => p.Name).ToList();
                var kb = ob.Properties().Select(p => p.Name).ToList();
                for (var i = 0; i < ka.Count; i++)
                    if (ka[i] != kb[i]) return $"{path}: key order [{string.Join(",", ka)}] vs [{string.Join(",", kb)}]";
                return null;
            }
            if (a is JArray aa)
            {
                var ab = (JArray)b;
                if (aa.Count != ab.Count) return $"{path}: length {aa.Count} vs {ab.Count}";
                for (var i = 0; i < aa.Count; i++)
                {
                    var d = FirstDifferenceOrdered(aa[i], ab[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    if (d != null) return d;
                }
                return null;
            }
            return JToken.DeepEquals(a, b) ? null : $"{path}: {Show(a)} vs {Show(b)}";
        }

        /// <summary>A recorded run delta applied to the expected document: set in place (new keys appended), removals, then the recorded key order.</summary>
        internal static void ApplyDelta(JObject doc, JObject delta)
        {
            foreach (var p in ((JObject)delta["set"]).Properties()) doc[p.Name] = p.Value.DeepClone();
            foreach (var k in (JArray)delta["removed"]) doc.Remove(k.Value<string>());
            if (!(delta["keys"] is JArray keys)) return;
            var props = keys.Select(k => doc.Property(k.Value<string>())).ToList();
            doc.RemoveAll();
            foreach (var p in props) doc.Add(p);
        }

        internal static Dictionary<RngStream, uint> Counters(JObject o) =>
            o.Properties().ToDictionary(p => RngStreamNames.Parse(p.Name), p => (uint)p.Value.Value<long>());

        internal static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        internal static string Show(JToken t)
        {
            if (t == null) return "(absent)";
            var s = t.ToString(Formatting.None);
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }
}
