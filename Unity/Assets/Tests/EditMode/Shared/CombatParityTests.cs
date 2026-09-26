using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-5.1 combat parity (PF-04/05/10; D-036, D-037): every golden combat log recorded from the SHIPPED engine by
    /// Tools/oracle-combat.mjs is restored from its snapshot and replayed command by command through the C# engine;
    /// the projection after every step (HP, block, statuses, intents, energy/mana/stamina, hand, pile sizes and every
    /// RNG stream counter) must equal the shipped one. Tools/oracle-replay.mjs proves the same replay in JS.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class CombatParityTests
    {
        private static string CombatDir => Path.Combine(TestContent.OracleRoot, "combat");

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static CombatData _data;

        private static CombatData Data => _data ??= CombatData.FromRegistryDump(
            ReadJson(Path.Combine(CombatDir, "registries.json")),
            TestContent.ContentJson("rules/mechanics.json"),
            TestContent.ContentJson("rules/combatEngine.json"));

        public static IEnumerable<string> CombatFiles() =>
            Directory.Exists(CombatDir)
                ? Directory.GetFiles(CombatDir, "combat-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        [Test]
        public void TheGoldenLogsArePresent()
        {
            var index = ReadJson(Path.Combine(CombatDir, "index.json"));
            Assert.That(CombatFiles().Count(), Is.EqualTo(((JArray)index["combats"]).Count));
            Assert.That(CombatFiles().Count(), Is.GreaterThanOrEqualTo(48));
        }

        [TestCaseSource(nameof(CombatFiles))]
        public void ReplayMatchesTheShippedEngine(string file)
        {
            var log = ReadJson(Path.Combine(CombatDir, file));
            var counters = ((JObject)log["rngCounters"]).Properties().ToDictionary(p => RngStreamNames.Parse(p.Name), p => (uint)p.Value.Value<long>());
            var rng = new Rng((uint)log["seed"].Value<long>(), counters);
            var combat = CombatSnapshot.Restore(Data, rng, (JObject)log["snapshot"]);
            Check(file, "start", log["start"], Projection(combat));
            var steps = (JArray)log["steps"];
            for (var i = 0; i < steps.Count; i++)
            {
                var step = (JObject)steps[i];
                var command = CombatCommand.FromJson((JObject)step["command"]);
                string error = null;
                try
                {
                    CombatEngine.Dispatch(combat, command);
                }
                catch (Exception e) when (!(e is AssertionException))
                {
                    error = e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
                }
                if (error != null && step["error"] == null) Assert.Fail($"{file} step {i} ({step["command"].ToString(Formatting.None)}): unexpected {error}");
                Check(file, $"step {i} ({step["command"].ToString(Formatting.None)})", step["after"], Projection(combat));
            }
            Assert.That(combat.Result, Is.EqualTo(log["result"].Type == JTokenType.Null ? null : log.Value<string>("result")));
        }

        [TestCaseSource(nameof(CombatFiles))]
        public void SnapshotRoundTripsAndResumesIdentically(string file)
        {
            var log = ReadJson(Path.Combine(CombatDir, file));
            var counters = ((JObject)log["rngCounters"]).Properties().ToDictionary(p => RngStreamNames.Parse(p.Name), p => (uint)p.Value.Value<long>());
            var rng = new Rng((uint)log["seed"].Value<long>(), counters);
            var combat = CombatSnapshot.Restore(Data, rng, (JObject)log["snapshot"]);
            var steps = (JArray)log["steps"];
            var half = steps.Count / 2;
            for (var i = 0; i < half; i++) CombatEngine.Dispatch(combat, CombatCommand.FromJson((JObject)steps[i]["command"]));
            if (combat.Result != null) return;
            var saved = CombatSnapshot.Serialize(combat);
            var resumed = CombatSnapshot.Restore(Data, rng.Clone(), (JObject)JToken.Parse(saved.ToString(Formatting.None)));
            Check(file, "resumed", Projection(combat), Projection(resumed));
            for (var i = half; i < steps.Count; i++)
            {
                CombatEngine.Dispatch(resumed, CombatCommand.FromJson((JObject)steps[i]["command"]));
                Check(file, $"resumed step {i}", steps[i]["after"], Projection(resumed));
            }
        }

        // ------------------------------------------------------------------ projection (Tools/oracle-combat.mjs)

        private static JObject Entity(JObject e)
        {
            var statuses = new JObject();
            foreach (var p in ((JObject)e["statuses"] ?? new JObject()).Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var v = p.Value;
                if (v is JObject o) statuses[p.Name] = Coalesce(o["stacks"], Coalesce(o["value"], o)).DeepClone();
                else statuses[p.Name] = v.DeepClone();
            }
            var result = new JObject
            {
                ["id"] = Coalesce(e["id"], e["instanceId"])?.DeepClone(),
                ["hp"] = e["hp"]?.DeepClone(),
                ["maxHp"] = e["maxHp"]?.DeepClone(),
                ["block"] = e["block"]?.DeepClone(),
                ["alive"] = !(e["alive"] != null && e["alive"].Type == JTokenType.Boolean && !e["alive"].Value<bool>()),
                ["statuses"] = statuses,
            };
            if (Js.Truthy(e["intent"]))
            {
                var intent = (JObject)e["intent"];
                result["intent"] = new JObject
                {
                    ["moveId"] = Coalesce(intent["moveId"], JValue.CreateNull()).DeepClone(),
                    ["kind"] = Coalesce(intent["kind"], Coalesce(intent["type"], JValue.CreateNull())).DeepClone(),
                    ["amount"] = Coalesce(intent["amount"], Coalesce(intent["damage"], JValue.CreateNull())).DeepClone(),
                };
            }
            return result;
        }

        private static JToken Coalesce(JToken a, JToken b) => a == null || a.Type == JTokenType.Null ? b : a;

        private static JObject Projection(CombatState c)
        {
            var player = Entity(c.Player);
            player["energy"] = c.Player["energy"]?.DeepClone();
            player["mana"] = c.Player["mana"]?.DeepClone();
            player["stamina"] = c.Player["stamina"]?.DeepClone();
            var rng = new JObject();
            foreach (var kv in c.Rng.Counters().OrderBy(kv => (int)kv.Key)) rng[RngStreamNames.ToWire(kv.Key)] = kv.Value;
            return new JObject
            {
                ["turn"] = Js.N(c.Turn),
                ["phase"] = c.Phase,
                ["result"] = c.Result == null ? JValue.CreateNull() : new JValue(c.Result),
                ["player"] = player,
                ["enemies"] = new JArray(c.Enemies.Select(Entity)),
                ["hand"] = new JArray(c.Piles.Hand.Select(x => x["instanceId"].DeepClone())),
                ["draw"] = c.Piles.Draw.Count,
                ["discard"] = c.Piles.Discard.Count,
                ["exhaust"] = c.Piles.Exhaust.Count,
                ["rng"] = rng,
            };
        }

        // ------------------------------------------------------------------ comparison

        private static void Check(string file, string label, JToken want, JToken got)
        {
            var diff = FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{file} {label}: {diff}\n  want {want.ToString(Formatting.None)}\n  got  {got.ToString(Formatting.None)}");
        }

        private static string FirstDifference(JToken a, JToken b, string path)
        {
            var aNull = a == null || a.Type == JTokenType.Null || a.Type == JTokenType.Undefined;
            var bNull = b == null || b.Type == JTokenType.Null || b.Type == JTokenType.Undefined;
            if (aNull || bNull) return aNull == bNull ? null : $"{path}: {Show(a)} vs {Show(b)}";
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

        private static string Show(JToken t) => t == null ? "undefined" : t.ToString(Formatting.None);
    }
}
