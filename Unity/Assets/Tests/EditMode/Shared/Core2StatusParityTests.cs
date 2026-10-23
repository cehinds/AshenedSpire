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
    /// WP-9 (D-240): the Core 2 statuses. Every status of the registry (101) is read the way the web reads it (the FixCore definition and the
    /// modifier reads), and every scripted scenario recorded by Tools/oracle-core2-statuses.mjs (apply, remove, decay, owner hooks, the event bus,
    /// build-up pressure, recovery rolls, Sleep, the control locks, Ward spending ...) is played again on the C# port from the same seed and must
    /// equal the web after every step: the call's return value, the events (type and payload), the actions it queued and the whole combat as a
    /// delta. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2StatusParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "statuses");

        private static readonly Dictionary<string, JObject> Files = new Dictionary<string, JObject>();

        internal static JObject Read(string name)
        {
            lock (Files)
            {
                if (!Files.TryGetValue(name, out var doc)) Files[name] = doc = (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, name)));
                return doc;
            }
        }

        public static IEnumerable<string> ScenarioFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "scenarios-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Scenarios()
        {
            foreach (var file in ScenarioFiles())
            {
                var rows = (JArray)Read(file)["scenarios"];
                for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(file, i).SetName("Core2Status_" + Path.GetFileNameWithoutExtension(file).Replace("scenarios-", string.Empty) + "_" + i);
            }
        }

        public static IEnumerable<TestCaseData> DefinitionCases()
        {
            if (!File.Exists(Path.Combine(Dir, "defs.json"))) yield break;
            foreach (var p in ((JObject)Read("defs.json")["statuses"]).Properties()) yield return new TestCaseData(p.Name).SetName("Core2StatusDefinition_" + p.Name);
        }

        // ------------------------------------------------------------------ building

        internal static CombatState Build(JObject spec)
        {
            var data = Core2DamageParityTests.Data;
            var seed = (uint)spec["seed"].Value<long>();
            var run = CoreRunRules.Create(data.Run, seed, spec.Value<string>("classId"), CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            var c = CoreRunCombat.Create(data, new Rng(seed), run, spec.Value<string>("encounterId"));
            c.Fields.Remove("rangeState"); // range lineage (WP-12) is not part of the statuses oracle
            if (spec["fields"] is JObject fields)
            {
                var copy = (JObject)fields.DeepClone();
                if (copy["turn"] != null) { c.Turn = copy.Value<double>("turn"); copy.Remove("turn"); }
                Core2DamageParityTests.Merge(c.Fields, copy);
            }
            if (spec["rules"] is JObject rules) Core2DamageParityTests.Merge(c.Fields.Obj("fixCoreRules"), rules);
            if (spec["statusRules"] is JObject statusRules) Core2DamageParityTests.Merge(c.Fields.Obj("combatExpansionRules").Obj("statuses"), statusRules);
            if (spec["patch"] is JObject patch)
                foreach (var p in patch.Properties()) Core2DamageParityTests.Merge(Entity(c, p.Name), (JObject)p.Value);
            return c;
        }

        internal static JObject Entity(CombatState c, string who) => who == null ? null : who == "p" ? c.Player : c.Enemies[int.Parse(who.Substring(1))];

        // ------------------------------------------------------------------ the definitions

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2DamageParityTests.Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(ScenarioFiles().Count(), Is.EqualTo(((JArray)index["files"]).Count));
            Assert.That(((JObject)Read("defs.json")["statuses"]).Count, Is.EqualTo(101), "every status of the registry");
        }


        [TestCaseSource(nameof(DefinitionCases))]
        public void TheDefinitionAndTheModifiersReadAsTheWebReadsThem(string id)
        {
            var row = (JObject)Read("defs.json")["statuses"][id];
            var spec = (JObject)Read("defs.json")["spec"];
            var c = Build(spec);
            Core2DamageParityTests.Same(id + " base", row["base"], c.Data.Statuses.Get(id));
            Core2DamageParityTests.Same(id + " definition", row["definition"], CoreStatuses.Definition(c, id));
            foreach (var stacks in new[] { 1, 2, 3 })
            {
                var fight = Build(spec);
                fight.Player["statuses"] = new JObject { [id] = new JObject { ["stacks"] = stacks } };
                fight.Enemies[0]["statuses"] = new JObject { [id] = new JObject { ["stacks"] = stacks } };
                var got = new JObject { ["player"] = Mods(fight, fight.Player), ["enemy"] = Mods(fight, fight.Enemies[0]) };
                Core2DamageParityTests.Same(id + " mods x" + stacks, row["mods"][stacks.ToString()], got);
            }
        }

        private static JObject Mods(CombatState c, JObject entity)
        {
            var o = new JObject();
            foreach (var key in ((JArray)Read("defs.json")["modifierKeys"]).Select(t => t.Value<string>()))
                o[key] = new JObject
                {
                    ["mult"] = Js.N(CoreStatuses.Mult(c, entity, key)), ["add"] = Js.N(CoreStatuses.Add(c, entity, key)), ["flag"] = CoreStatuses.Flag(c, entity, key),
                    ["cap"] = CoreStatuses.Cap(c, entity, key) is double cap ? Js.N(cap) : Js.Null(), ["any"] = CoreStatuses.AnyCombatantFlag(c, key),
                };
            return o;
        }

        // ------------------------------------------------------------------ the scenarios

        private static JToken ResolveIds(CombatState c, JToken value)
        {
            if (value is JValue v && v.Type == JTokenType.String)
            {
                var text = v.Value<string>();
                return text.Length > 2 && text[0] == '$' && text[1] == 'e' ? c.Enemies[int.Parse(text.Substring(2))]["id"].DeepClone() : value.DeepClone();
            }
            if (value is JArray a) return new JArray(a.Select(x => ResolveIds(c, x)));
            if (value is JObject o) return new JObject(o.Properties().Select(p => new JProperty(p.Name, ResolveIds(c, p.Value))));
            return value.DeepClone();
        }

        private static JObject Obj(JToken t) => t as JObject ?? new JObject();

        private static JToken Run(CombatState c, JObject step)
        {
            var e = Entity(c, step.Value<string>("who"));
            var from = Entity(c, step.Value<string>("from"));
            var options = Obj(step["options"]);
            switch (step.Value<string>("op"))
            {
                case "apply":
                    CoreStatuses.Apply(c, e, step.Value<string>("status"), step.Value<double>("stacks"), from, step["tags"]?.Select(t => t.Value<string>()).ToList());
                    return Js.Null();
                case "remove":
                    CoreStatuses.Remove(c, e, step.Value<string>("status"), step.Value<string>("reason"), step["amount"] != null ? step.Value<double>("amount") : (double?)null, step["silent"] != null && step.Value<bool>("silent"));
                    return Js.Null();
                case "decayStart": CoreStatuses.DecayAtTurnStart(c, e); return Js.Null();
                case "decayEnd": CoreStatuses.DecayAtTurnEnd(c, e); return Js.Null();
                case "turn": c.Turn += step["by"] != null ? step.Value<double>("by") : 1; return Js.N(c.Turn);
                case "hooks": CoreStatusHooks.FireOwner(c, e, step.Value<string>("hook")); return Js.Null();
                case "emit": c.Emit(step.Value<string>("type"), (JObject)ResolveIds(c, step["payload"] ?? new JObject())); return Js.Null();
                case "drain":
                {
                    var ran = new JArray();
                    var guard = 0;
                    while (c.Queue.Count > 0)
                    {
                        if (++guard > 200) throw new InvalidOperationException("queue did not drain");
                        var action = c.Queue.First.Value;
                        c.Queue.RemoveFirst();
                        ran.Add(new JObject { ["op"] = action.Effect["op"].DeepClone(), ["source"] = action.Source?["id"]?.DeepClone() ?? Js.Null(), ["target"] = action.Target?["id"]?.DeepClone() ?? Js.Null() });
                        CoreActions.Execute(c, action);
                        if (c.Result != null) { c.Queue.Clear(); break; }
                    }
                    return ran;
                }
                case "set": Core2DamageParityTests.Merge(e, (JObject)step["patch"]); return Js.Null();
                case "pressure": return CoreStatusControl.ApplyPressure(c, e, step.Value<string>("status"), step.Value<double>("units"), from, options);
                case "recover": return CoreStatusControl.RecoverAtOwnerStart(c, e, options["cycle"] != null ? options.Value<double>("cycle") : c.Turn, options);
                case "sleep": return CoreStatusControl.SleepRestoration(c, e, options);
                case "endControl": return CoreStatusControl.EndTurn(c, e, step.Value<string>("selected"));
                case "actionLoss": return Js.N(CoreStatusControl.ConsumeActionLoss(c, e));
                case "before": CoreStatusControl.BeforeAction(c, e, Obj(step["profile"]), step["connected"] == null || step.Value<bool>("connected")); return Js.Null();
                case "complete":
                    CoreStatusControl.CompleteAction(c, e, Obj(step["profile"]), step["connected"] == null || step.Value<bool>("connected"), step.Value<string>("actionKey"), step["damaging"] == null || step.Value<bool>("damaging"));
                    return Js.Null();
                case "ground": CoreStatusControl.Ground(c, e, step["clear"] != null && step.Value<bool>("clear")); return Js.Null();
                case "manual": return CoreStatusControl.ManualRecovery(c, e, (JArray)step["selections"], options);
                case "spendWard": return CoreStatusControl.SpendWard(c, e, step.Value<double>("amount"), Obj(step["meta"]));
                case "restoreWard":
                    return CoreControl.RestoreWard(c, e, step.Value<double>("nominal"), options.Value<string>("source"), options["modifierPercent"] != null ? options.Value<double>("modifierPercent") : (double?)null,
                        options["cap"] != null ? options.Value<double>("cap") : (double?)null, options.Value<string>("onceKey"));
                case "restorePoise":
                    return CoreControl.RestorePoise(c, e, step.Value<double>("nominal"), options.Value<string>("source"), options["modifierPercent"] != null ? options.Value<double>("modifierPercent") : (double?)null,
                        options["cap"] != null ? options.Value<double>("cap") : (double?)null, options.Value<string>("onceKey"));
                case "breakWard": return CoreStatusControl.BreakWard(c, e, Obj(step["meta"]));
                case "read": return Query(c, e, step);
                case "tacticalComplete":
                {
                    var receipt = new JObject { ["completed"] = false, ["profiles"] = new JArray(), ["covered"] = new JArray() };
                    foreach (var prop in ((JObject)step["receipt"]).Properties()) receipt[prop.Name] = prop.Value.DeepClone();
                    CoreTactical.Complete(c, e, Entity(c, step.Value<string>("targetWho")), Obj(step["carrier"]), receipt);
                    return Js.Null();
                }
                case "effect":
                {
                    var action = new CombatAction
                    {
                        Effect = (JObject)step["effect"].DeepClone(), Source = e, Owner = e, Target = step["targetWho"] != null ? Entity(c, step.Value<string>("targetWho")) : null,
                        Card = step["card"] != null ? (JObject)step["card"].DeepClone() : null, Meta = EffectMeta(Obj(step["meta"])),
                    };
                    CoreActions.Execute(c, action);
                    return Js.Null();
                }
                default: throw new InvalidOperationException("unknown step " + step.Value<string>("op"));
            }
        }

        private static ActionMeta EffectMeta(JObject meta)
        {
            var result = new ActionMeta();
            if (meta["linkedDepth"] != null) result.LinkedDepth = meta.Value<int>("linkedDepth");
            if (meta["choice"] != null) result.Choice = meta["choice"].DeepClone();
            var charge = new JObject();
            foreach (var p in new[] { ("abilityChargeBlock", "block"), ("abilityChargeHeal", "heal"), ("abilityChargeBuildup", "buildup"), ("abilityChargeDamage", "damage"), ("abilityChargeDamageEffect", "damageEffect"), ("abilityChargeBreak", "break") })
                if (meta[p.Item1] != null) charge[p.Item2] = meta[p.Item1].DeepClone();
            if (charge.Count > 0) result.Charge = charge;
            return result;
        }

        private static JToken Query(CombatState c, JObject e, JObject step)
        {
            switch (step.Value<string>("read"))
            {
                case "mods": return Mods(c, e);
                case "stacks": return Js.N(Statuses.Stacks(e, step.Value<string>("status")));
                case "definition": return CoreStatuses.Definition(c, step.Value<string>("status"));
                case "restrictions": return CoreStatusControl.Restrictions(c, e);
                case "gate": return CoreStatusControl.Gate(c, e, Obj(step["card"]));
                case "controls": return CoreStatusControl.RecoveryControls(c, e);
                case "chance": return Js.N(CoreStatusControl.RecoveryChance(c, e, step.Value<string>("status"), Obj(step["options"])));
                case "effectiveChance":
                    return Js.N(CoreStatusControl.EffectiveChance(step.Value<double>("chance"), Obj(step["options"]).Is("advantage"), Obj(step["options"]).Is("disadvantage")));
                case "incoming": return CoreStatusControl.IncomingModifier(c, e, Obj(step["profile"]));
                case "electrical": return CoreStatusControl.ElectricalTraits(c, e);
                case "rules": return CoreFixCore.Rules(c, e);
                default: throw new InvalidOperationException("unknown read " + step.Value<string>("read"));
            }
        }

        private static JArray QueueRows(CombatState c) => new JArray(c.Queue.Select(a => new JObject
        {
            ["effect"] = a.Effect.DeepClone(), ["source"] = a.Source?["id"]?.DeepClone() ?? Js.Null(), ["target"] = a.Target?["id"]?.DeepClone() ?? Js.Null(),
            ["killReceipt"] = a.Meta?.KillReceipt?.DeepClone() ?? Js.Null(),
        }));

        // ------------------------------------------------------------------ the card traits

        public static IEnumerable<TestCaseData> TraitProjections()
        {
            if (!File.Exists(Path.Combine(Dir, "traits.json"))) yield break;
            var rows = (JArray)Read("traits.json")["projection"];
            for (var i = 0; i < rows.Count; i += 20)
                yield return new TestCaseData(i, Math.Min(rows.Count, i + 20)).SetName("Core2TraitProjection_" + i);
        }

        [TestCaseSource(nameof(TraitProjections))]
        public void TheProjectionAddsTheTraitsTheWebAdds(int from, int to)
        {
            var file = Read("traits.json");
            var c = Build((JObject)Read("defs.json")["spec"]);
            for (var i = from; i < to; i++)
            {
                var row = (JObject)file["projection"][i];
                var def = (JObject)file["defs"][row.Value<string>("id")];
                var baseTags = ((JArray)file["baseTags"][row.Value<string>("id")]).Select(t => t.Value<string>()).ToList();
                var rules = (JObject)file["rules"][row.Value<string>("rules")];
                var at = row.Value<string>("id") + " " + row.Value<string>("rules");
                Assert.That(CoreCardTraits.Tier(def), Is.EqualTo(row.Value<double>("tier")), at + " tier");
                var input = (JObject)def.DeepClone();
                var got = CoreCardTraits.Apply(c, input, rules, baseTags);
                Core2DamageParityTests.Same(at + " input untouched", def, input);
                Core2DamageParityTests.Same(at + " traitValues", row["traitValues"], got["traitValues"] ?? Js.Null());
                Core2DamageParityTests.Same(at + " tags", row["tags"], ReferenceEquals(got, input) ? Js.Null() : got["tags"] ?? Js.Null());
                Core2DamageParityTests.Same(at + " cardTags", row["cardTags"], got["cardTags"] ?? Js.Null());
            }
        }

        [Test]
        public void TheTraitMathIsTheWebs()
        {
            var file = Read("traits.json");
            var c = Build((JObject)Read("defs.json")["spec"]);
            foreach (JObject row in (JArray)file["math"])
            {
                var kind = row.Value<string>("kind");
                if (row["rules"] != null) c.Fields["reactionRules"] = file["rules"][row.Value<string>("rules")].DeepClone();
                var at = row.ToString(Newtonsoft.Json.Formatting.None);
                switch (kind)
                {
                    case "advantage": Assert.That(CoreCardTraits.HasAdvantage(c, row.Value<string>("player"), row.Value<string>("target")), Is.EqualTo(row.Value<bool>("value")), at); break;
                    case "pct": Assert.That(CoreCardTraits.CounterstrikePct(c, row.Value<string>("player"), row.Value<string>("target")), Is.EqualTo(row.Value<double>("value")).Within(1e-12), at); break;
                    case "amount": Core2DamageParityTests.Same(at, row["value"], CoreCardTraits.CounterstrikeAmount(row["amount"], row.Value<double>("pct"))); break;
                    case "hit": Assert.That(CoreCardTraits.HitAmount(row.Value<double>("own"), row.Value<double>("n")), Is.EqualTo(row.Value<double>("value")), at); break;
                    case "maneuver": Assert.That(CoreCardTraits.ManeuverOf(row["entity"] as JObject, row.Value<string>("resolving")), Is.EqualTo(row.Value<string>("value")), at); break;
                    default: Assert.Fail("unknown math row " + kind); break;
                }
            }
        }

        [TestCaseSource(nameof(Scenarios))]
        public void TheScenarioPlaysAsTheWebPlaysIt(string file, int index)
        {
            var spec = (JObject)((JArray)Read(file)["scenarios"])[index];
            var c = Build(spec);
            var label = file + "[" + index + "] " + spec.Value<string>("name");
            JToken want = Core2CombatStartParityTests.Project(c);
            var n = 0;
            foreach (var row in ((JArray)spec["steps"]).OfType<JObject>())
            {
                var step = (JObject)row["step"];
                var at = label + " step " + n++ + " " + step.Value<string>("op");
                var mark = c.EventLog.Count;
                JToken result = null;
                string error = null;
                try { result = Run(c, step); } catch (Exception ex) { error = ex.Message; }
                if (row["error"] != null) Assert.That(error, Is.EqualTo(row.Value<string>("error")), at + " error");
                else if (error != null) Assert.Fail(at + " the port threw: " + error);
                else Core2DamageParityTests.Same(at + " result", row["result"] ?? Js.Null(), result ?? Js.Null());
                want = Core2CombatStartParityTests.Apply(want, row["delta"]);
                Core2DamageParityTests.Same(at + " state", want, Core2CombatStartParityTests.Project(c));
                Core2DamageParityTests.Same(at + " events", row["events"], new JArray(c.EventLog.Skip(mark).Select(ev => ev.DeepClone())));
                Core2DamageParityTests.Same(at + " queue", row["queue"], QueueRows(c));
            }
        }
    }
}
