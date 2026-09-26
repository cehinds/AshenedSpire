using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-0.3: the C# runtime registries equal the shipped createRegistries() output (Tools/oracle-registries.mjs →
    /// Unity/Assets/Tests/Oracle/registries/&lt;preset&gt;/&lt;table&gt;.json) for both presets, row for row (suite: Parity).
    /// Both sides go through one canonical form (ordinally sorted keys, every number as an IEEE double) before
    /// <see cref="JToken.DeepEquals(JToken, JToken)"/>; a failure names the first differing row and path.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class RegistryParityTests
    {
        private static readonly string[] Presets = { "shipped", "reference" };
        private static readonly Dictionary<string, RuntimeRegistries> Built = new Dictionary<string, RuntimeRegistries>(StringComparer.Ordinal);

        private static RuntimeRegistries Registries(string preset)
        {
            if (Built.TryGetValue(preset, out var r)) return r;
            var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = preset });
            var strings = (JObject)snapshot.Content.Get(ContentFiles.StringsEn);
            return Built[preset] = RuntimeRegistries.Build(snapshot.Content, strings);
        }

        private static JObject Oracle(string preset, string table) =>
            TestContent.Oracle(Path.Combine("registries", preset, table + ".json"));

        private static IEnumerable<TestCaseData> TableCases() =>
            from preset in Presets
            from table in RuntimeRegistries.TableNames
            select new TestCaseData(preset, table).SetName("Registry_" + preset + "_" + table);

        [TestCaseSource(nameof(TableCases))]
        public void TableMatchesTheShippedRegistry(string preset, string table)
        {
            var actual = Registries(preset).Table(table);
            Assert.That(actual, Is.Not.Null, table);
            AssertRowsEqual(preset + "/" + table, actual, Oracle(preset, table));
        }

        [TestCase("shipped"), TestCase("reference")]
        public void EquipmentMatchesTheShippedRegistry(string preset) =>
            AssertRowsEqual(preset + "/equipment", Registries(preset).Equipment, Oracle(preset, "equipment"));

        [TestCase("shipped"), TestCase("reference")]
        public void BalanceMatchesTheShippedRegistry(string preset) =>
            AssertRowsEqual(preset + "/balance", Registries(preset).Balance, Oracle(preset, "balance"));

        [Test]
        public void EveryOracleTableIsCovered()
        {
            foreach (var preset in Presets)
            {
                var files = Directory.GetFiles(Path.Combine(TestContent.OracleRoot, "registries", preset), "*.json")
                    .Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal);
                var covered = RuntimeRegistries.TableNames.Concat(new[] { "equipment", "balance" }).OrderBy(n => n, StringComparer.Ordinal);
                Assert.That(covered, Is.EqualTo(files), preset);
            }
        }

        [Test]
        public void ProjectionActuallyChangesCardsAndPresetsDiffer()
        {
            // Guards against a vacuous pass: the reference preset retunes defense-card bonuses, so its Block values differ.
            var shipped = Registries("shipped").Table(RegistryKeys.Cards);
            var reference = Registries("reference").Table(RegistryKeys.Cards);
            Assert.That(JToken.DeepEquals(Canonical(shipped), Canonical(reference)), Is.False);
            Assert.That(shipped.Properties().Count(), Is.EqualTo(TestContent.ContentJson(ContentFiles.CatalogCards).Properties().Count()));
        }

        // ------------------------------------------------------------------ comparison

        private static void AssertRowsEqual(string what, JObject actual, JObject expected)
        {
            var a = Canonical(actual);
            var e = Canonical(expected);
            if (JToken.DeepEquals(a, e)) return;
            var missing = ((JObject)e).Properties().Select(p => p.Name).Where(k => ((JObject)a)[k] == null).ToList();
            var extra = ((JObject)a).Properties().Select(p => p.Name).Where(k => ((JObject)e)[k] == null).ToList();
            var differing = ((JObject)e).Properties().Where(p => ((JObject)a)[p.Name] != null && !JToken.DeepEquals(((JObject)a)[p.Name], p.Value)).ToList();
            var message = what + ": " + differing.Count + " differing row(s), missing [" + string.Join(", ", missing.Take(10)) + "], extra [" + string.Join(", ", extra.Take(10)) + "]";
            if (differing.Count > 0)
            {
                var row = differing[0];
                message += "\n  first: " + row.Name + " — " + FirstDifference(((JObject)a)[row.Name], row.Value, row.Name);
            }
            Assert.Fail(message);
        }

        /// <summary>Sorted keys (ordinal) and every number as a double, so decimal-parsed and computed values compare as JS numbers.</summary>
        private static JToken Canonical(JToken t)
        {
            switch (t)
            {
                case null:
                    return JValue.CreateNull();
                case JObject o:
                    var sorted = new JObject();
                    foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)) sorted[p.Name] = Canonical(p.Value);
                    return sorted;
                case JArray arr:
                    return new JArray(arr.Select(Canonical));
                case JValue v when v.Type == JTokenType.Integer || v.Type == JTokenType.Float:
                    return new JValue(double.Parse(Convert.ToString(v.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture));
                default:
                    return t.DeepClone();
            }
        }

        private static string FirstDifference(JToken actual, JToken expected, string path)
        {
            if (actual is JObject ao && expected is JObject eo)
            {
                foreach (var key in eo.Properties().Select(p => p.Name).Union(ao.Properties().Select(p => p.Name)).OrderBy(k => k, StringComparer.Ordinal))
                    if (!JToken.DeepEquals(ao[key], eo[key])) return FirstDifference(ao[key], eo[key], path + "." + key);
            }
            else if (actual is JArray aa && expected is JArray ea)
            {
                for (var i = 0; i < Math.Min(aa.Count, ea.Count); i++)
                    if (!JToken.DeepEquals(aa[i], ea[i])) return FirstDifference(aa[i], ea[i], path + "[" + i + "]");
                if (aa.Count != ea.Count) return path + ": length " + aa.Count + " vs expected " + ea.Count;
            }
            return path + ": got " + Show(actual) + ", expected " + Show(expected);
        }

        private static string Show(JToken t)
        {
            var s = t == null ? "(missing)" : t.ToString(Newtonsoft.Json.Formatting.None);
            return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
        }
    }
}
