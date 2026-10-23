using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-6 (D-210): Core 2 d20 hit resolution. Every case recorded from the web's stanceHit.js and combatAvoidance.js on a real Core 2
    /// fight (Tools/oracle-core2-hit.mjs) is rebuilt through <see cref="CoreRunCombat.Create"/>, given the same overlay on its fighters and
    /// rules, and must give the web's preview field for field; the same attack resolved with the scripted dice must give the web's result,
    /// ask for the same number of dice (with the same bounds), leave the same Evade behind and announce the same event; sequences on the
    /// real <c>combatAvoidance</c> stream must give the same results and end on the same RNG counters, die for die. The units cover
    /// effectiveEvasion, evadeStackGain, precisionStatMix, previewCardAttackHit, rollChanceDie, effectiveChance, resolveAvoidance and the
    /// proposal rules a hit reads. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2HitParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "hit");

        private static CoreCombatData _data;
        private static readonly Dictionary<string, JObject> Files = new Dictionary<string, JObject>();

        private static CoreCombatData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var registries = Core2Registries.Load(snapshot.Content);
                return _data = registries.ToCoreCombatData(snapshot.Content, registries.ToCoreRunData(snapshot.Content));
            }
        }

        private static JObject Read(string name)
        {
            lock (Files)
            {
                if (!Files.TryGetValue(name, out var doc)) Files[name] = doc = (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, name)));
                return doc;
            }
        }

        private static JObject Unlocked => new JObject
        {
            ["gameProfileUnlocks"] = new JObject { [Data.Run.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) } },
        };

        // ------------------------------------------------------------------ the fixtures

        public static IEnumerable<string> CaseFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "hit-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Cases(string prefix)
        {
            foreach (var file in CaseFiles())
            {
                var count = ((JArray)Read(file)["cases"]).Count;
                for (var i = 0; i < count; i++) yield return new TestCaseData(file, i).SetName(prefix + Path.GetFileNameWithoutExtension(file) + "_" + i);
            }
        }

        public static IEnumerable<TestCaseData> PreviewCases() => Cases("Core2HitPreview_");

        public static IEnumerable<TestCaseData> RollCases() => Cases("Core2HitRoll_");

        public static IEnumerable<TestCaseData> SequenceCases()
        {
            if (!File.Exists(Path.Combine(Dir, "seq.json"))) yield break;
            var count = ((JArray)Read("seq.json")["sequences"]).Count;
            for (var i = 0; i < count; i++) yield return new TestCaseData(i).SetName("Core2HitSeq_" + i);
        }

        public static IEnumerable<TestCaseData> Units(string section, string prefix)
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var rows = (JArray)Read("units.json")[section];
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName(prefix + i);
        }

        public static IEnumerable<TestCaseData> EvasionUnits() => Units("evasion", "Core2HitEvasion_");

        public static IEnumerable<TestCaseData> GainUnits() => Units("gain", "Core2HitGain_");

        public static IEnumerable<TestCaseData> MixUnits() => Units("mix", "Core2HitMix_");

        public static IEnumerable<TestCaseData> CardUnits() => Units("cardPreview", "Core2HitCard_");

        public static IEnumerable<TestCaseData> AvoidanceUnits() => Units("avoidance", "Core2HitAvoidance_");

        public static IEnumerable<TestCaseData> ProposalUnits() => Units("proposal", "Core2HitProposal_");

        // ------------------------------------------------------------------ building a case

        /// <summary>RFC 7386 merge, as the oracle applies its overlay: an object merges key by key, null deletes, anything else replaces.</summary>
        private static void Merge(JObject target, JObject patch)
        {
            foreach (var p in patch.Properties())
            {
                if (p.Value.Type == JTokenType.Null) target.Remove(p.Name);
                else if (p.Value is JObject sub && target[p.Name] is JObject existing) Merge(existing, sub);
                else target[p.Name] = p.Value.DeepClone();
            }
        }

        private sealed class Built
        {
            public CombatState C;
            public JObject Source;
            public JObject Target;
            public JObject Carrier;
            public HitOptions Options;
        }

        /// <summary>The fight of a recorded case with its overlay: the fields, the rules snapshot, the capabilities, the two fighters, the range state.</summary>
        private static Built Build(JObject spec, JObject inputs = null)
        {
            var seed = (uint)spec["seed"].Value<long>();
            var run = CoreRunRules.Create(Data.Run, seed, spec.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
            var c = CoreRunCombat.Create(Data, new Rng(seed), run, spec.Value<string>("encounterId"));
            if (spec["fields"] is JObject fields) Merge(c.Fields, fields);
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
            var options = new HitOptions { OpenProposalCapabilities = spec.Value<bool?>("openCapabilities") == true };
            if (spec["options"] is JObject o && o.Value<bool?>("evadeDisabled") == true) options.EvadeDisabled = true;
            if (inputs != null)
            {
                options.RangeContact = inputs["rangeContact"] as JObject;
                options.DamageType = inputs["damageType"]?.Type == JTokenType.String ? inputs.Value<string>("damageType") : null;
            }
            return new Built { C = c, Source = source, Target = target, Carrier = spec["carrier"] as JObject, Options = options };
        }

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{label}: first difference at {diff}");
        }

        private static JObject After(JObject entity) => new JObject
        {
            ["statuses"] = entity["statuses"]?.DeepClone() ?? new JObject(),
            ["nextEvadeMode"] = entity["nextEvadeMode"]?.DeepClone() ?? JValue.CreateNull(),
            ["combatEvade"] = entity["combatEvade"]?.DeepClone() ?? JValue.CreateNull(),
        };

        // The events a hit owns (the oracle's OWN_EVENTS): a miss against Evade also credits Perception (WP-7 pays it on the bus), a receipt the oracle does not record.
        private static readonly HashSet<string> OwnEvents = new HashSet<string> { "attackHit", "attackMissed", "statusRemoved", "statusExpired", "combatAvoidanceResolved", "perceptionGained" };

        private static JArray Events(CombatState c, int mark) => new JArray(c.EventLog.Skip(mark).Where(e => OwnEvents.Contains(e.Value<string>("type"))).Select(e => e.DeepClone()));

        private static void Restore(JObject entity, JObject snapshot)
        {
            entity.RemoveAll();
            foreach (var p in snapshot.Properties()) entity[p.Name] = p.Value.DeepClone();
        }

        /// <summary>The counters the oracle records (the web's rng.getCounters()) as the C# streams hold them.</summary>
        private static void SameCounters(string label, JToken want, CombatState c)
        {
            var got = c.Rng.AllCounters();
            foreach (var p in ((JObject)want).Properties())
                Assert.That(got.TryGetValue(p.Name, out var value) ? (long)value : 0L, Is.EqualTo(p.Value.Value<long>()), label + " rng " + p.Name);
        }

        // ------------------------------------------------------------------ the oracle

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(CaseFiles().Count(), Is.EqualTo(((JArray)index["files"]).Count));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(index["scripts"].Value<int>(), Is.GreaterThan(1000));
        }

        // ------------------------------------------------------------------ the preview

        [TestCaseSource(nameof(PreviewCases))]
        public void ThePreviewMatchesTheWeb(string file, int index)
        {
            var spec = (JObject)((JArray)Read(file)["cases"])[index];
            var b = Build(spec, (JObject)spec["inputs"]);
            var label = $"{file}[{index}] {spec.Value<string>("direction")} {spec.Value<string>("mode")}";
            var plan = CoreProposal.TargetPlan(b.C, b.Source, b.Target, b.Carrier, b.Options.DamageType, b.Options.OpenProposalCapabilities);
            Same(label + " proposal", spec["inputs"]["proposal"], plan);
            var preview = CoreHitRoll.Preview(b.C, b.Source, b.Target, b.Carrier, b.Options);
            Same(label + " preview", spec["preview"], preview);
            SameCounters(label + " preview", spec["rng"], b.C);
        }

        // ------------------------------------------------------------------ scripted dice

        [TestCaseSource(nameof(RollCases))]
        public void TheScriptedRollsMatchTheWeb(string file, int index)
        {
            var spec = (JObject)((JArray)Read(file)["cases"])[index];
            var b = Build(spec, (JObject)spec["inputs"]);
            var label = $"{file}[{index}] {spec.Value<string>("direction")} {spec.Value<string>("mode")}";
            var sourceSnapshot = (JObject)b.Source.DeepClone();
            var targetSnapshot = (JObject)b.Target.DeepClone();
            var n = 0;
            foreach (var script in ((JArray)spec["scripts"]).OfType<JObject>())
            {
                var step = $"{label} script {n++}";
                var dice = script["dice"].Select(d => d.Value<int>()).ToList();
                var calls = new List<int[]>();
                Func<int, int, int> draw = (low, high) =>
                {
                    calls.Add(new[] { low, high });
                    return dice[calls.Count - 1];
                };
                var mark = b.C.EventLog.Count;
                var result = CoreHitRoll.Resolve(b.C, b.Source, b.Target, b.Carrier, b.Options, draw);
                Same(step + " result", script["result"], result);
                Assert.That(calls.Count, Is.EqualTo(script["used"].Value<int>()), step + " dice drawn");
                Assert.That(calls.Count, Is.EqualTo(((JArray)script["calls"]).Count), step + " draw calls");
                for (var i = 0; i < calls.Count; i++)
                {
                    Assert.That(calls[i][0], Is.EqualTo(script["calls"][i][1].Value<int>()), step + " die low " + i);
                    Assert.That(calls[i][1], Is.EqualTo(script["calls"][i][2].Value<int>()), step + " die high " + i);
                }
                Same(step + " target", script["targetAfter"], After(b.Target));
                Same(step + " source", script["sourceAfter"], After(b.Source));
                Same(step + " events", script["events"], Events(b.C, mark));
                b.C.EventLog.RemoveRange(mark, b.C.EventLog.Count - mark);
                Restore(b.Target, targetSnapshot);
                Restore(b.Source, sourceSnapshot);
            }
            SameCounters(label + " rolls", spec["rng"], b.C);
        }

        // ------------------------------------------------------------------ the real stream

        [TestCaseSource(nameof(SequenceCases))]
        public void TheRealStreamMatchesTheWeb(int index)
        {
            var spec = (JObject)((JArray)Read("seq.json")["sequences"])[index];
            var b = Build(spec);
            var n = 0;
            foreach (var step in ((JArray)spec["steps"]).OfType<JObject>())
            {
                var label = $"seq[{index}] step {n++}";
                // The contact the web read for this step (the engagement planner is WP-12) and the damage type of the proposal rules.
                b.Options.RangeContact = step["inputs"]["rangeContact"] as JObject;
                b.Options.DamageType = step["inputs"]["damageType"]?.Type == JTokenType.String ? step["inputs"].Value<string>("damageType") : null;
                var preview = CoreHitRoll.Preview(b.C, b.Source, b.Target, b.Carrier, b.Options);
                Same(label + " preview", step["preview"], preview);
                SameCounters(label + " preview", step["previewCounters"], b.C);
                var before = (long)b.C.Rng.Counter(Core2RngStream.CombatAvoidance);
                var mark = b.C.EventLog.Count;
                var result = CoreHitRoll.Resolve(b.C, b.Source, b.Target, b.Carrier, b.Options);
                Same(label + " result", step["result"], result);
                Assert.That((long)b.C.Rng.Counter(Core2RngStream.CombatAvoidance) - before, Is.EqualTo(step["drawn"].Value<long>()), label + " dice drawn");
                SameCounters(label, step["counters"], b.C);
                Same(label + " target", step["targetAfter"], After(b.Target));
                Same(label + " events", step["events"], Events(b.C, mark));
            }
        }

        // ------------------------------------------------------------------ units

        [TestCaseSource(nameof(EvasionUnits))]
        public void EffectiveEvasionMatchesTheWeb(int index)
        {
            var spec = (JObject)((JArray)Read("units.json")["evasion"])[index];
            var b = Build(spec);
            var disabled = spec["options"].Value<bool>("evadeDisabled");
            var baseEntity = spec["options"]["baseEntity"]?.Value<string>() == "source" ? b.Source : null;
            Assert.That(CoreHitRoll.EffectiveEvasion(b.C, b.Target, disabled, baseEntity), Is.EqualTo(spec["value"].Value<double>()), "evasion " + index);
        }

        [TestCaseSource(nameof(GainUnits))]
        public void EvadeStackGainMatchesTheWeb(int index)
        {
            var spec = (JObject)((JArray)Read("units.json")["gain"])[index];
            var b = Build(spec);
            var entity = spec.Value<string>("entity") == "target" ? b.Target : b.Source;
            Assert.That(CoreHitRoll.EvadeStackGain(b.C, entity), Is.EqualTo(spec["value"].Value<double>()), "gain " + index);
            Assert.That(CoreHitRoll.EvadeStackGain(b.C, null), Is.EqualTo(spec["noEntity"].Value<double>()), "gain without entity " + index);
        }

        [TestCaseSource(nameof(MixUnits))]
        public void PrecisionStatMixMatchesTheWeb(int index)
        {
            var spec = (JObject)((JArray)Read("units.json")["mix"])[index];
            var b = Build(spec);
            Assert.That(CoreHitRoll.PrecisionStatMix(b.C, b.Source), Is.EqualTo(spec["mix"].Value<double>()), "mix " + index);
            Assert.That(CoreHitRoll.PrecisionEntryGrant(b.C, b.Source, spec["base"].Value<double>()), Is.EqualTo(spec["grant"].Value<double>()), "grant " + index);
            Assert.That(CoreHitRoll.PrecisionStatMix(b.C, b.Target), Is.EqualTo(spec["targetMix"].Value<double>()), "target mix " + index);
        }

        [TestCaseSource(nameof(CardUnits))]
        public void TheSelectedCardPreviewMatchesTheWeb(int index)
        {
            var spec = (JObject)((JArray)Read("units.json")["cardPreview"])[index];
            var b = Build(spec, (JObject)spec["inputs"]);
            Same("card preview " + index, spec["value"], CoreHitRoll.PreviewCard(b.C, b.Source, b.Target, b.Carrier, b.Options));
        }

        [TestCaseSource(nameof(AvoidanceUnits))]
        public void TheAvoidanceReceiptMatchesTheWeb(int index)
        {
            var spec = (JObject)((JArray)Read("units.json")["avoidance"])[index];
            var b = Build(spec);
            if (spec["knowledgeAction"] is JObject knowledge) b.Source["knowledgeAction"] = knowledge.DeepClone();
            var options = (JObject)spec["options"];
            var mark = b.C.EventLog.Count;
            var value = CoreAvoidance.Resolve(b.C, b.Source, b.Target, (JObject)spec["profile"], spec["damage"].Value<double>(), options.Value<bool?>("hitCheck") == true, spec["saved"] as JObject);
            Same("avoidance " + index, spec["value"], value);
            Same("avoidance target " + index, spec["targetAfter"], After(b.Target));
            Same("avoidance events " + index, spec["events"], Events(b.C, mark));
            SameCounters("avoidance " + index, spec["counters"], b.C);
        }

        [TestCaseSource(nameof(ProposalUnits))]
        public void TheProposalPlanMatchesTheWeb(int index)
        {
            var spec = (JObject)((JArray)Read("units.json")["proposal"])[index];
            var b = Build(spec);
            var damageType = spec["damageType"]?.Type == JTokenType.String ? spec.Value<string>("damageType") : null;
            Same("proposal " + index, spec["plan"], CoreProposal.TargetPlan(b.C, b.Source, b.Target, b.Carrier, damageType, spec.Value<bool?>("openCapabilities") == true));
        }

        // ------------------------------------------------------------------ chance dice

        [Test]
        public void ChanceModesAndPercentagesMatchTheWeb()
        {
            var chance = (JObject)Read("units.json")["chance"];
            var spec = (JObject)((JArray)Read("hit-0.json")["cases"])[0];
            var c = Build(spec).C;
            foreach (var row in ((JArray)chance["modes"]).OfType<JObject>())
                Assert.That(CoreAvoidance.ChanceMode((JObject)row["flags"]), Is.EqualTo(row.Value<string>("mode")), row["flags"].ToString());
            foreach (var row in ((JArray)chance["chances"]).OfType<JObject>())
                Assert.That(CoreAvoidance.EffectiveChance(c, row.Value<double>("base"), (JObject)row["flags"]), Is.EqualTo(row.Value<double>("value")), row.ToString());
        }

        [Test]
        public void ChanceDiceOnScriptedDiceMatchTheWeb()
        {
            var chance = (JObject)Read("units.json")["chance"];
            var spec = (JObject)((JArray)Read("hit-0.json")["cases"])[0];
            var c = Build(spec).C;
            var n = 0;
            foreach (var row in ((JArray)chance["dice"]).OfType<JObject>())
            {
                var script = row["script"].Select(d => d.Value<int>()).ToList();
                var calls = new List<int[]>();
                var result = CoreAvoidance.RollChanceDie(c, (low, high) => { calls.Add(new[] { low, high }); return script[calls.Count - 1]; }, row.Value<int>("die"), (JObject)row["flags"]);
                Same("chance die " + n, row["result"], result);
                Assert.That(calls.Count, Is.EqualTo(((JArray)row["calls"]).Count), "chance die calls " + n);
                foreach (var call in calls) Assert.That(call, Is.EqualTo(new[] { 1, row.Value<int>("die") }));
                n++;
            }
        }

        [Test]
        public void ChanceDiceOnTheRealStreamMatchTheWeb()
        {
            var chance = (JObject)Read("units.json")["chance"];
            var spec = (JObject)((JArray)Read("hit-0.json")["cases"])[0];
            var c = Build(spec).C;
            var n = 0;
            foreach (var row in ((JArray)chance["real"]).OfType<JObject>())
            {
                var counters = new Dictionary<Core2RngStream, uint> { [Core2RngStream.CombatAvoidance] = row["counters"].Value<uint>("combatAvoidance") };
                var rng = new Rng(row.Value<uint>("seed"), null, counters);
                var result = CoreAvoidance.RollChanceDie(c, (low, high) => rng.Int(Core2RngStream.CombatAvoidance, low, high), row.Value<int>("die"), (JObject)row["flags"]);
                Same("real chance die " + n, row["result"], result);
                Assert.That((long)rng.Counter(Core2RngStream.CombatAvoidance), Is.EqualTo(row["after"].Value<long>("combatAvoidance")), "real chance counter " + n);
                n++;
            }
        }
    }
}
