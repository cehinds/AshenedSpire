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
            Check(file, "start", log["start"], WithPreviews(combat, log["start"]));
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
                Check(file, $"step {i} ({step["command"].ToString(Formatting.None)})", step["after"], WithPreviews(combat, step["after"]));
            }
            Assert.That(combat.Result, Is.EqualTo(log["result"].Type == JTokenType.Null ? null : log.Value<string>("result")));
        }

        /// <summary>
        /// createCombat from the recorded shipped inputs must produce the shipped initial snapshot exactly: enemy HP
        /// rolls, ratings and meters, the shuffled draw pile with Innate on top, the event log, the first intents,
        /// the opening hand and every RNG counter.
        /// </summary>
        [TestCaseSource(nameof(CombatFiles))]
        public void CreateCombatMatchesTheShippedStart(string file)
        {
            var log = ReadJson(Path.Combine(CombatDir, file));
            var rng = new Rng((uint)log["seed"].Value<long>());
            var combat = CombatStart.Create(Data, rng, (JObject)log["create"]);
            Check(file, "createCombat snapshot", log["snapshot"], CombatSnapshot.Serialize(combat));
            var start = (JObject)log["start"].DeepClone();
            start.Remove("previews");
            Check(file, "createCombat start", start, Projection(combat));
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
                Check(file, $"resumed step {i}", steps[i]["after"], WithPreviews(resumed, steps[i]["after"]));
            }
        }

        // ------------------------------------------------------------------ mid-fight equipment (us-5.11)

        private static readonly Dictionary<string, CombatData> _variants = new Dictionary<string, CombatData>(StringComparer.Ordinal);

        /// <summary>
        /// The combat data a swap case ran against: RunData built from registries.json (the swap port restamps through
        /// the run's composition model), with the configuration variant's overlay (its differing top-level tables).
        /// </summary>
        private static CombatData Variant(string name)
        {
            if (_variants.TryGetValue(name, out var cached)) return cached;
            var dump = ReadJson(Path.Combine(CombatDir, "registries.json"));
            if (name != "shipped")
                foreach (var p in ((JObject)ReadJson(Path.Combine(CombatDir, "registries-" + name + ".json"))["overlay"]).Properties())
                    dump[p.Name] = p.Value.DeepClone();
            var run = Ashen.Domain.Run.RunData.FromRegistryDump(dump,
                TestContent.ContentJson("rules/mechanics.json"),
                TestContent.ContentJson("rules/combatEngine.json"),
                TestContent.ContentJson("rules/handRules.json"),
                TestContent.ContentJson("rules/runEngine.json"));
            return _variants[name] = run.Combat;
        }

        public static IEnumerable<string> SwapFiles() =>
            Directory.Exists(CombatDir)
                ? Directory.GetFiles(CombatDir, "swap-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        private static Rng RngAt(JObject log)
        {
            var counters = ((JObject)log["rngCounters"]).Properties().ToDictionary(p => RngStreamNames.Parse(p.Name), p => (uint)p.Value.Value<long>());
            return new Rng((uint)log["seed"].Value<long>(), counters);
        }

        /// <summary>The oracle's equipment projection: the loadout, the full piles, the player entity, the pool deficits and the flags.</summary>
        private static JObject EquipProjection(CombatState c)
        {
            var s = CombatSnapshot.Serialize(c);
            return new JObject
            {
                ["loadout"] = s["loadout"], ["piles"] = s["piles"], ["player"] = s["player"],
                ["equipmentPoolDeficits"] = s["equipmentPoolDeficits"], ["equipmentChanged"] = s["equipmentChanged"], ["swapsLeft"] = s["swapsLeft"],
            };
        }

        /// <summary>
        /// Every recorded equipment option at this point: the legality service refuses exactly the options the shipped
        /// dispatch refused, a refused option dispatched on a restored clone throws the shipped message, and the price
        /// preview equals the shipped swapCostFor receipt (or is absent where the engine refuses before pricing).
        /// </summary>
        private static void CheckOptions(string file, string label, CombatState c, JToken options)
        {
            if (options == null) return;
            foreach (var option in ((JArray)options).OfType<JObject>())
            {
                var command = CombatCommand.FromJson((JObject)option["command"]);
                var where = $"{file} {label} {option["command"].ToString(Formatting.None)}";
                var refusal = CombatLegality.Check(c, command);
                var error = option["error"]?.Type == JTokenType.String ? option.Value<string>("error") : null;
                Assert.That(refusal == null, Is.EqualTo(error == null), where + " legality (" + (refusal?.Key ?? "legal") + " vs " + (error ?? "legal") + ")");
                var clone = CombatSnapshot.Restore(c.Data, c.Rng.Clone(), CombatSnapshot.Serialize(c));
                string thrown = null;
                try
                {
                    CombatEngine.Dispatch(clone, command);
                }
                catch (InvalidOperationException e)
                {
                    thrown = e.Message;
                }
                Assert.That(thrown, Is.EqualTo(error), where + " dispatch");
                var price = command.Type == CombatValues.CommandSwapArmament
                    ? CombatPreview.SwapPrice(c, command.SlotId, command.SetIndex)
                    : CombatPreview.ChangePrice(c, command.SlotId, command.SetIndex, command.PieceId);
                Check(file, label + " price " + option["command"].ToString(Formatting.None), option["price"], price);
            }
        }

        /// <summary>
        /// US-5.11 mid-fight equipment parity: every golden combat that swaps sets and re-arms positions (every class, its
        /// kits, carried armaments, the three swap-cost rules and the configuration variants) replays through the C#
        /// port. After every step the projection and previews must match; after every equipment step the events it
        /// emitted and the equipment projection; the recorded equipment options (legality, message and price) at the
        /// start, after each equipment step and after the first End Turn; and the final committed state.
        /// </summary>
        [TestCaseSource(nameof(SwapFiles))]
        public void SwapReplayMatchesTheShippedEngine(string file)
        {
            var log = ReadJson(Path.Combine(CombatDir, file));
            var data = Variant(log.Value<string>("variant"));
            var combat = CombatSnapshot.Restore(data, RngAt(log), (JObject)log["snapshot"]);
            Check(file, "start", log["start"].DeepClone() is JObject start && start.Remove("options") ? start : log["start"], WithPreviews(combat, log["start"]));
            CheckOptions(file, "start options", combat, log["start"]["options"]);
            var steps = (JArray)log["steps"];
            for (var i = 0; i < steps.Count; i++)
            {
                var step = (JObject)steps[i];
                var label = $"step {i} ({step["command"].ToString(Formatting.None)})";
                var command = CombatCommand.FromJson((JObject)step["command"]);
                List<JObject> events = null;
                try
                {
                    events = CombatEngine.Dispatch(combat, command);
                }
                catch (Exception e) when (!(e is AssertionException))
                {
                    if (step["error"] == null) Assert.Fail($"{file} {label}: unexpected {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                }
                Check(file, label, step["after"], WithPreviews(combat, step["after"]));
                if (step["equip"] != null)
                {
                    Check(file, label + " events", step["events"], new JArray(events.Select(e => e.DeepClone())));
                    Check(file, label + " equipment", step["equip"], EquipProjection(combat));
                }
                CheckOptions(file, label + " options", combat, step["options"]);
            }
            Assert.That(combat.Result, Is.EqualTo(log["result"].Type == JTokenType.Null ? null : log.Value<string>("result")), file);
            var final = CombatSnapshot.Serialize(combat);
            final.Remove("eventLog");
            Check(file, "final", log["final"], final);
        }

        [TestCaseSource(nameof(SwapFiles))]
        public void SwapCreateCombatMatchesTheShippedStart(string file)
        {
            var log = ReadJson(Path.Combine(CombatDir, file));
            var rng = new Rng((uint)log["seed"].Value<long>());
            var combat = CombatStart.Create(Variant(log.Value<string>("variant")), rng, (JObject)log["create"]);
            Check(file, "createCombat snapshot", log["snapshot"], CombatSnapshot.Serialize(combat));
        }

        [TestCaseSource(nameof(SwapFiles))]
        public void SwapSnapshotRoundTripsAndResumesIdentically(string file)
        {
            var log = ReadJson(Path.Combine(CombatDir, file));
            var data = Variant(log.Value<string>("variant"));
            var combat = CombatSnapshot.Restore(data, RngAt(log), (JObject)log["snapshot"]);
            var steps = (JArray)log["steps"];
            var half = steps.Count / 2;
            for (var i = 0; i < half; i++) CombatEngine.Dispatch(combat, CombatCommand.FromJson((JObject)steps[i]["command"]));
            if (combat.Result != null) return;
            var resumed = CombatSnapshot.Restore(data, combat.Rng.Clone(), (JObject)JToken.Parse(CombatSnapshot.Serialize(combat).ToString(Formatting.None)));
            for (var i = half; i < steps.Count; i++)
            {
                var command = CombatCommand.FromJson((JObject)steps[i]["command"]);
                Assert.That(CombatCommand.FromJson(command.ToJson()).ToJson().ToString(Formatting.None), Is.EqualTo(command.ToJson().ToString(Formatting.None)), file + " wire form");
                CombatEngine.Dispatch(resumed, command);
                Check(file, $"resumed step {i}", steps[i]["after"], WithPreviews(resumed, steps[i]["after"]));
            }
        }

        /// <summary>
        /// The mid-fight Armoury view (Application) reads the same legality and prices: every recorded start option on a
        /// hand slot appears with the shipped verdict and cost, and the session runs the first recorded equipment
        /// command through its legality gate to the same equipment projection.
        /// </summary>
        [TestCaseSource(nameof(SwapFiles))]
        public void ArmouryViewAndSessionAgreeWithTheShippedOptions(string file)
        {
            var log = ReadJson(Path.Combine(CombatDir, file));
            var data = Variant(log.Value<string>("variant"));
            var combat = CombatSnapshot.Restore(data, RngAt(log), (JObject)log["snapshot"]);
            var view = Ashen.App.Combat.CombatViewModel.Build(combat);
            Assert.That(view.Armoury, Is.Not.Null, file);
            foreach (var option in ((JArray)log["start"]["options"]).OfType<JObject>())
            {
                var command = CombatCommand.FromJson((JObject)option["command"]);
                var legal = option["error"]?.Type != JTokenType.String;
                var cost = option["price"]?["cost"];
                if (command.Type == CombatValues.CommandSwapArmament)
                {
                    var slot = view.Armoury.Slots.FirstOrDefault(s => s.SlotId == command.SlotId);
                    if (slot == null) continue;
                    var set = slot.Sets[command.SetIndex];
                    Assert.That(set.Swappable, Is.EqualTo(legal), file + " " + option["command"]);
                    Assert.That(set.Cost, Is.EqualTo(cost == null ? (double?)null : Js.D(cost)), file + " " + option["command"]);
                }
                else
                {
                    var change = Ashen.App.Combat.CombatViewModel.EquipmentChanges(combat, command.SlotId, command.SetIndex).FirstOrDefault(x => x.PieceId == command.PieceId);
                    if (change == null) continue;
                    Assert.That(change.Allowed, Is.EqualTo(legal), file + " " + option["command"]);
                    Assert.That(change.Cost, Is.EqualTo(cost == null ? (double?)null : Js.D(cost)), file + " " + option["command"]);
                }
            }
            var steps = ((JArray)log["steps"]).OfType<JObject>().ToList();
            var first = steps.FindIndex(s => s["equip"] != null);
            if (first < 0) return;
            var session = Ashen.App.Combat.CombatSession.Start(data, (uint)log["seed"].Value<long>(), (JObject)log["create"]);
            for (var i = 0; i <= first; i++)
            {
                var outcome = session.Execute(CombatCommand.FromJson((JObject)steps[i]["command"]));
                Assert.That(outcome.Accepted, Is.True, $"{file} step {i}");
            }
            Check(file, "session equipment", steps[first]["equip"], EquipProjection(session.State));
        }

        [Test]
        public void TheSwapLogsCoverEveryDoor()
        {
            var index = (JArray)ReadJson(Path.Combine(CombatDir, "index.json"))["swaps"];
            Assert.That(SwapFiles().Count(), Is.EqualTo(index.Count));
            var commands = new HashSet<string>();
            var errors = new HashSet<string>();
            var variants = new HashSet<string>();
            foreach (var file in SwapFiles())
            {
                var log = ReadJson(Path.Combine(CombatDir, file));
                variants.Add(log.Value<string>("variant"));
                foreach (var step in ((JArray)log["steps"]).OfType<JObject>().Prepend((JObject)log["start"]))
                {
                    if (step["equip"] != null) commands.Add(step["command"].Value<string>("type"));
                    foreach (var option in Js.Items(step["options"]).OfType<JObject>())
                        if (option["error"]?.Type == JTokenType.String) errors.Add(System.Text.RegularExpressions.Regex.Replace(option.Value<string>("error"), "[0-9]+", "#"));
                }
            }
            Assert.That(commands, Is.EquivalentTo(new[] { "swapArmament", "changeEquipment" }), "both intents are played");
            Assert.That(variants, Is.SupersetOf(new[] { "shipped", "unrated", "endsTurn", "locked", "disabled", "allowance" }));
            Assert.That(errors.Count, Is.GreaterThanOrEqualTo(8), "refusal kinds: " + string.Join(" | ", errors));
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
                ["skillXp"] = c.SkillXp["player"]?.DeepClone() ?? JValue.CreateNull(),
            };
        }

        /// <summary>The projection plus, when the oracle recorded them, the previews (card and intent) at that point.</summary>
        private static JObject WithPreviews(CombatState c, JToken want)
        {
            var projection = Projection(c);
            if (want?["previews"] == null) return projection;
            var cards = new JArray();
            foreach (var card in c.Piles.Hand)
            {
                var p = CombatPreview.PreviewCard(c, card.Value<string>("instanceId"));
                var values = new JArray();
                foreach (var v in (JArray)p["values"])
                {
                    var entry = new JObject { ["op"] = v["op"].DeepClone(), ["target"] = v["target"]?.DeepClone() ?? JValue.CreateNull(), ["value"] = v["value"]?.DeepClone() ?? JValue.CreateNull() };
                    if (v["hits"] != null && v["hits"].Type != JTokenType.Null) entry["hits"] = v["hits"].DeepClone();
                    if (Js.Truthy(v["perTarget"])) entry["perTarget"] = v["perTarget"].DeepClone();
                    if (Js.Truthy(v["status"])) entry["status"] = v["status"].DeepClone();
                    if (Js.Truthy(v["token"])) entry["token"] = v["token"].DeepClone();
                    if (Js.Truthy(v["boostTint"])) entry["boostTint"] = v["boostTint"].DeepClone();
                    values.Add(entry);
                }
                cards.Add(new JObject
                {
                    ["id"] = card["instanceId"].DeepClone(), ["cost"] = p["cost"].DeepClone(), ["costIsX"] = p["costIsX"].DeepClone(),
                    ["needsTarget"] = p["needsTarget"].DeepClone(), ["manaCost"] = p["manaCost"].DeepClone(), ["staminaCost"] = p["staminaCost"].DeepClone(),
                    ["values"] = values, ["tokens"] = p["tokens"].DeepClone(),
                });
            }
            var intents = new JArray();
            foreach (var e in c.Enemies.Where(e => Js.Truthy(e["alive"])))
            {
                var i = CombatPreview.PreviewIntent(c, e.Value<string>("id"));
                intents.Add(new JObject
                {
                    ["id"] = e["id"].DeepClone(), ["kind"] = Coalesce(i["kind"], JValue.CreateNull()).DeepClone(), ["moveId"] = Coalesce(i["moveId"], JValue.CreateNull()).DeepClone(),
                    ["damage"] = Coalesce(i["damage"], JValue.CreateNull()).DeepClone(), ["hits"] = Coalesce(i["hits"], JValue.CreateNull()).DeepClone(),
                    ["totalDamage"] = Coalesce(i["totalDamage"], JValue.CreateNull()).DeepClone(), ["block"] = Coalesce(i["block"], JValue.CreateNull()).DeepClone(),
                    ["pending"] = Js.Truthy(i["pending"]),
                });
            }
            projection["previews"] = new JObject { ["cards"] = cards, ["intents"] = intents };
            return projection;
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
