using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-7 (D-220): the Core 2 damage receipt and its protections. Every case recorded from the web's profileDamageReceipt on a real Core 2
    /// fight with a declared overlay (Tools/oracle-core2-damage.mjs) is rebuilt through <see cref="CoreRunCombat.Create"/> and must give the
    /// web's receipt field for field, the <c>sources[]</c> lines of the damage receipt included; the same attack applied on scripted dice (a
    /// hit and a miss) must leave both fighters, the events and the queued actions as the web leaves them. The units cover the protection
    /// receipt (Poise and Ward, the mechanics kernel), the rules as an entity reads them, the stance mitigation, the Poise drain, the
    /// Staggered stance, Poise damage and Block / Barrier gain. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2DamageParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "damage");

        private static CoreCombatData _data;

        internal static CoreCombatData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var registries = Core2Registries.Load(snapshot.Content);
                return _data = registries.ToCoreCombatData(snapshot.Content, registries.ToCoreRunData(snapshot.Content));
            }
        }

        internal static JObject Read(string name) => OracleDocuments.Read(Path.Combine(Dir, name));

        internal static JObject Unlocked => new JObject
        {
            ["gameProfileUnlocks"] = new JObject { [Data.Run.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) } },
        };

        // ------------------------------------------------------------------ the fixtures

        public static IEnumerable<string> ReceiptFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "receipts-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Cases(string prefix)
        {
            foreach (var file in ReceiptFiles())
            {
                var count = ((JArray)Read(file)["cases"]).Count;
                for (var i = 0; i < count; i++) yield return new TestCaseData(file, i).SetName(prefix + Path.GetFileNameWithoutExtension(file) + "_" + i);
            }
        }

        public static IEnumerable<TestCaseData> ReceiptCases() => Cases("Core2Receipt_");

        public static IEnumerable<TestCaseData> AppliedCases() => Cases("Core2Applied_");

        public static IEnumerable<TestCaseData> UnitCases()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var rows = (JArray)Read("units.json")["units"];
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2Unit_" + rows[i].Value<string>("kind") + "_" + i);
        }

        // ------------------------------------------------------------------ building a case

        /// <summary>RFC 7386 merge, as the oracle applies its overlay: an object merges key by key, null deletes, anything else replaces.</summary>
        internal static void Merge(JObject target, JObject patch)
        {
            foreach (var p in patch.Properties())
            {
                if (p.Value.Type == JTokenType.Null) target.Remove(p.Name);
                else if (p.Value is JObject sub && target[p.Name] is JObject existing) Merge(existing, sub);
                else target[p.Name] = p.Value.DeepClone();
            }
        }

        internal sealed class Built
        {
            public CombatState C;
            public JObject Source;
            public JObject Target;
            public JObject Carrier;
        }

        internal static Built Build(JObject spec)
        {
            var seed = (uint)spec["seed"].Value<long>();
            var run = CoreRunRules.Create(Data.Run, seed, spec.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
            var c = CoreRunCombat.Create(Data, new Rng(seed), run, spec.Value<string>("encounterId"));
            if (spec["fields"] is JObject fields)
            {
                var copy = (JObject)fields.DeepClone();
                if (copy["turn"] != null) { c.Turn = copy.Value<double>("turn"); copy.Remove("turn"); }
                Merge(c.Fields, copy);
            }
            if (spec["rules"] is JObject rules) Merge(c.Fields.Obj("fixCoreRules"), rules);
            var playerToEnemy = spec.Value<string>("direction") == "playerToEnemy";
            var enemy = c.Enemies[spec["enemyIndex"] != null ? spec.Value<int>("enemyIndex") : 0];
            var source = playerToEnemy ? c.Player : enemy;
            var target = playerToEnemy ? enemy : c.Player;
            if (spec["patch"]?["source"] is JObject s) Merge(source, s);
            if (spec["patch"]?["target"] is JObject t) Merge(target, t);
            var range = spec["range"] as JObject;
            if (range?.Value<string>("mode") == "delete") c.Fields.Remove("rangeState");
            else if (range?.Value<string>("mode") == "band")
                foreach (var engagement in c.Fields.Obj("rangeState").Obj("engagements").Properties().ToList()) engagement.Value = range["band"].DeepClone();
            return new Built { C = c, Source = source, Target = target, Carrier = spec["carrier"] is JObject carrier ? (JObject)carrier.DeepClone() : null };
        }

        internal static void Same(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{label}: first difference at {diff}");
        }

        private static readonly string[] Quiet = { "statusControl", "abilityRiders", "ratingSources", "knowledgeAction", "intent", "plannedMoves", "movesHistory", "arcaneExposure" };

        /// <summary>The oracle's looks(): the entity without the books it keeps for other work packages.</summary>
        internal static JObject Looks(JObject entity)
        {
            var copy = (JObject)entity.DeepClone();
            foreach (var key in Quiet) copy.Remove(key);
            // Actions is Stamina (bindTurnStamina): the web's entity reads energy and energyMax through the pool.
            if (copy.Value<string>("kind") == "player")
            {
                copy["energy"] = copy["stamina"];
                copy["energyMax"] = copy["maxStamina"];
            }
            return copy;
        }

        private static JArray Events(CombatState c, int mark) => new JArray(c.EventLog.Skip(mark).Select(e => e.DeepClone()));

        private static JArray Queue(CombatState c) => new JArray(c.Queue.Select(a => new JObject
        {
            ["effect"] = a.Effect.DeepClone(), ["source"] = a.Source?["id"]?.DeepClone() ?? JValue.CreateNull(), ["target"] = a.Target?["id"]?.DeepClone() ?? JValue.CreateNull(),
        }));

        // ------------------------------------------------------------------ the oracle

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(ReceiptFiles().Count(), Is.GreaterThan(0));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(index["units"].Value<int>("units"), Is.GreaterThan(0));
        }

        // ------------------------------------------------------------------ the receipt

        [TestCaseSource(nameof(ReceiptCases))]
        public void TheReceiptMatchesTheWeb(string file, int index)
        {
            var spec = (JObject)((JArray)Read(file)["cases"])[index];
            var web = (JObject)spec["spec"];
            var b = Build(web);
            var label = $"{file}[{index}] {web.Value<string>("direction")} base {web.Value<string>("base")}";
            var inputs = (JObject)spec["inputs"];
            var damageType = CoreDamage.ProposalDamageType(b.C, b.Carrier);
            Assert.That(damageType, Is.EqualTo(inputs["damageType"].Type == JTokenType.String ? inputs.Value<string>("damageType") : null), label + " damage type");
            var contact = Contact(b, inputs, file + "[" + index + "]");
            var receipt = CoreDamage.ProfileDamageReceipt(b.C, b.Source, b.Target, web.Value<double>("base"), b.Carrier, contact, web.Value<bool>("proposal"));
            Same(label + " receipt", spec["receipt"], receipt);
        }

        // ------------------------------------------------------------------ the receipt applied

        [TestCaseSource(nameof(AppliedCases))]
        public void TheAttackAppliedMatchesTheWeb(string file, int index)
        {
            var spec = (JObject)((JArray)Read(file)["cases"])[index];
            var web = (JObject)spec["spec"];
            foreach (var label in new[] { "hit", "miss" })
            {
                var want = (JObject)spec["applied"][label];
                if (want["error"] != null) continue;
                var name = $"{file}[{index}] {label}";
                var b = Build(web);
                var dice = want["dice"].Select(d => d.Value<int>()).ToList();
                var drawn = 0;
                Func<int, int, int> draw = (low, high) => dice[drawn++];
                var mark = b.C.EventLog.Count;
                var returned = CoreImpact.ApplyAttackDamage(b.C, b.Source, b.Target, web.Value<double>("base"), b.Carrier, null, Contact(b, (JObject)spec["inputs"], file + "[" + index + "]"), draw);
                Assert.That(returned, Is.EqualTo(want.Value<double>("returned")), name + " returned");
                Assert.That(drawn, Is.EqualTo(want.Value<int>("draws")), name + " draws");
                Same(name + " source", want["source"], Looks(b.Source));
                Same(name + " target", want["target"], Looks(b.Target));
                Same(name + " events", want["events"], Events(b.C, mark));
                Same(name + " queue", want["queue"], Queue(b.C));
                Same(name + " interrupts", want["interrupts"], b.C.Fields["gameProfileInterrupts"] ?? JValue.CreateNull());
            }
        }

        /// <summary>rangeHitContact: the engagement planner reads the contact of this hit itself, and it is the one the web read.</summary>
        private static JObject Contact(Built b, JObject inputs, string name)
        {
            var contact = CoreRange.HitContact(b.C, b.Source, b.Target, b.Carrier);
            Same(name + " range contact", inputs["rangeContact"] ?? JValue.CreateNull(), (JToken)contact ?? JValue.CreateNull());
            return contact;
        }

        // ------------------------------------------------------------------ units

        [TestCaseSource(nameof(UnitCases))]
        public void TheUnitMatchesTheWeb(int index)
        {
            var unit = (JObject)((JArray)Read("units.json")["units"])[index];
            var kind = unit.Value<string>("kind");
            var spec = (JObject)unit["spec"];
            var b = Build(spec);
            var label = kind + "[" + index + "]";
            switch (kind)
            {
                case "protectionReceipt":
                {
                    JToken got;
                    try { got = CoreFixCore.ProtectionReceipt(b.C, b.Target, unit.Value<string>("damageKind"), unit.Value<double>("amount")); }
                    catch (MechanicsException e) { got = new JObject { ["error"] = e.Message }; }
                    Same(label, unit["result"], got);
                    break;
                }
                case "fixCoreRules":
                {
                    JToken got, bindings;
                    try { got = CoreFixCore.Rules(b.C, b.Source, null); }
                    catch (MechanicsException e) { got = new JObject { ["error"] = e.Message }; }
                    Same(label + " rules", unit["result"], got);
                    try
                    {
                        bindings = new JArray(CoreFixCore.ProtectionBindings(b.C, b.Source).Select(x => new JObject
                        {
                            ["id"] = x.Id, ["mechanicId"] = x.MechanicId, ["tag"] = x.Tag, ["ownerId"] = x.OwnerId, ["config"] = x.Config.DeepClone(),
                        }));
                    }
                    catch (MechanicsException e) { bindings = new JObject { ["error"] = e.Message }; }
                    Same(label + " bindings", unit["bindings"], bindings);
                    break;
                }
                case "stanceMitigation":
                {
                    var incoming = unit["incoming"].Type == JTokenType.String ? unit.Value<string>("incoming") : null;
                    var defending = unit["defending"].Type == JTokenType.String ? unit.Value<string>("defending") : null;
                    Same(label, unit["result"], CoreDamage.StanceMitigation(b.C, incoming, defending));
                    break;
                }
                default:
                {
                    var mark = b.C.EventLog.Count;
                    var amount = unit.Value<double>("amount");
                    double? returned = null;
                    string error = null;
                    try
                    {
                        switch (kind)
                        {
                            case "drain": returned = CoreImpact.DrainPoise(b.C, b.Source, b.Target, amount); break;
                            case "stagger": CoreImpact.FixCoreStagger(b.C, b.Target, unit["cause"]?.Type == JTokenType.String ? unit.Value<string>("cause") : null); break;
                            case "poiseDamage": CoreImpact.DealPoiseDamage(b.C, b.Target, amount, b.Source); break;
                            case "block":
                            case "barrier":
                                returned = CoreActions.GainBlock(b.C, b.Target, amount, unit["card"] as JObject);
                                break;
                        }
                    }
                    catch (NotSupportedException e) { error = e.Message; }
                    if (unit["error"] != null) { Assert.That(error, Is.Not.Null, label + " the web refused"); return; }
                    Assert.That(error, Is.Null, label + " " + error);
                    if (unit["returned"].Type == JTokenType.Null) Assert.That(returned, Is.Null.Or.EqualTo(0).Within(0), label + " returned");
                    else Assert.That(returned, Is.EqualTo(unit.Value<double>("returned")), label + " returned");
                    Same(label + " target", unit["target"], Looks(b.Target));
                    Same(label + " events", unit["events"], Events(b.C, mark));
                    Assert.That(b.C.Queue.Count, Is.EqualTo(unit.Value<int>("queue")), label + " queue");
                    Same(label + " interrupts", unit["interrupts"], b.C.Fields["gameProfileInterrupts"] ?? JValue.CreateNull());
                    break;
                }
            }
        }
    }
}
