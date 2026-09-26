using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-0.3 end to end (D-040, D-041): the combat data built from CONTENT (config layers → runtime registries →
    /// CombatData) equals the registry tables the shipped engine ran the golden combats against — the same rows,
    /// the same row order and equal rows with the SAME key order (display text re-attached at its authored anchor,
    /// D-069; tags stamped in the shipped spread order) — and the golden combats replay identically on it.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ContentCombatDataTests
    {
        private static CombatData _content;

        private static string CombatDir => Path.Combine(TestContent.OracleRoot, "combat");

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static CombatData ContentData
        {
            get
            {
                if (_content != null) return _content;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var strings = (JObject)snapshot.Content.Get(ContentFiles.StringsEn);
                var registries = RuntimeRegistries.Build(snapshot.Content, strings);
                return _content = registries.ToCombatData((JObject)snapshot.Content.Get(ContentFiles.RulesMechanics), (JObject)snapshot.Content.Get(ContentFiles.RulesCombatEngine));
            }
        }

        private static IEnumerable<string> Tables() => CombatData.TableNames;

        [TestCaseSource(nameof(Tables))]
        public void ContentTableEqualsTheShippedTableInOrder(string table)
        {
            var dump = ReadJson(Path.Combine(CombatDir, "registries.json"));
            var want = ((JArray)dump[table]).OfType<JObject>().ToList();
            var got = ContentData.Tables[table].All;
            var key = table == "propertyRules" ? "tag" : "id";
            Assert.That(got.Select(r => r.Value<string>(key)), Is.EqualTo(want.Select(r => r.Value<string>(key))), table + ": row order");
            for (var i = 0; i < want.Count; i++)
            {
                Assert.That(Canonical(got[i]), Is.EqualTo(Canonical(want[i])), $"{table}[{want[i].Value<string>(key)}] (numbers as doubles)");
                if (want[i]["moves"] is JObject moves)
                    Assert.That(((JObject)got[i]["moves"]).Properties().Select(p => p.Name), Is.EqualTo(moves.Properties().Select(p => p.Name)), $"{table}[{want[i].Value<string>(key)}].moves order");
            }
        }

        [Test]
        public void ContentEquipmentBalanceAndClassTreeEqualTheShippedOnes()
        {
            var dump = ReadJson(Path.Combine(CombatDir, "registries.json"));
            var equipment = (JObject)dump["equipment"];
            Assert.That(ContentData.Equipment.Properties().Select(p => p.Name), Is.EquivalentTo(equipment.Properties().Select(p => p.Name)), "equipment tables (read by name, never iterated)");
            foreach (var p in equipment.Properties())
            {
                if (p.Value is JArray rows && ContentData.Equipment[p.Name] is JArray got && got.Count == rows.Count)
                    for (var i = 0; i < rows.Count; i++) Assert.That(Canonical(got[i]), Is.EqualTo(Canonical(rows[i])), $"equipment.{p.Name}[{i}]");
                Assert.That(Canonical(ContentData.Equipment[p.Name]), Is.EqualTo(Canonical(p.Value)), "equipment." + p.Name);
            }
            Assert.That(Canonical(ContentData.Balance), Is.EqualTo(Canonical(dump["balance"])), "balance");
            Assert.That(Canonical(ContentData.ClassTree), Is.EqualTo(Canonical(dump["classTree"])), "classTree");
        }

        public static IEnumerable<string> CombatFiles() =>
            Directory.Exists(CombatDir)
                ? Directory.GetFiles(CombatDir, "combat-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        /// <summary>Start from the recorded createCombat inputs on content-built data and replay every command.</summary>
        [TestCaseSource(nameof(CombatFiles))]
        public void GoldenCombatReplaysOnContentData(string file)
        {
            var log = ReadJson(Path.Combine(CombatDir, file));
            var combat = CombatStart.Create(ContentData, new Rng((uint)log["seed"].Value<long>()), (JObject)log["create"]);
            Assert.That(Canonical(CombatSnapshot.Serialize(combat)), Is.EqualTo(Canonical(log["snapshot"])), file + " start snapshot");
            foreach (var step in ((JArray)log["steps"]).OfType<JObject>())
                CombatEngine.Dispatch(combat, CombatCommand.FromJson((JObject)step["command"]));
            var last = (JObject)((JArray)log["steps"]).Last["after"];
            Assert.That(combat.Result, Is.EqualTo(log["result"].Type == JTokenType.Null ? null : log.Value<string>("result")), file);
            Assert.That(combat.Player.Value<double>("hp"), Is.EqualTo(last["player"].Value<double>("hp")), file + " final hp");
            var counters = new JObject();
            foreach (var kv in combat.Rng.Counters().OrderBy(kv => (int)kv.Key)) counters[RngStreamNames.ToWire(kv.Key)] = kv.Value;
            Assert.That(Canonical(counters), Is.EqualTo(Canonical(last["rng"])), file + " rng");
        }

        /// <summary>Every number printed as a round-trip double; keys in their order, or ordinally sorted.</summary>
        private static string Canonical(JToken t, bool sortKeys = false)
        {
            var sb = new StringBuilder();
            Write(sb, t, sortKeys);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, JToken t, bool sortKeys)
        {
            switch (t)
            {
                case null:
                    sb.Append("undefined");
                    return;
                case JObject o:
                    sb.Append('{');
                    foreach (var p in sortKeys ? o.Properties().OrderBy(x => x.Name, StringComparer.Ordinal) : o.Properties()) { sb.Append(JsonConvert.ToString(p.Name)).Append(':'); Write(sb, p.Value, sortKeys); sb.Append(','); }
                    sb.Append('}');
                    return;
                case JArray a:
                    sb.Append('[');
                    foreach (var x in a) { Write(sb, x, sortKeys); sb.Append(','); }
                    sb.Append(']');
                    return;
                default:
                    sb.Append(Js.IsNum(t) ? Js.D(t).ToString("R", CultureInfo.InvariantCulture) : t.ToString(Formatting.None));
                    return;
            }
        }
    }
}
