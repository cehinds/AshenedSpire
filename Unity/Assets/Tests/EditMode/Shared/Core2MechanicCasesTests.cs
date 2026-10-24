using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-7 (docs/design/11 section 4, "Rules DSL"): every <c>engine/mechanics/&#42;/cases.json</c> of the pinned web, exported into
    /// <c>rules/core2Mechanics.json</c>, runs on the C# kernel subset (<see cref="CoreMechanicsKernel"/>, <see cref="CoreMechanicsRuntime"/>)
    /// and must produce what the web's own runtime produced for it (Tools/oracle-core2-damage.mjs, <c>mechanics.json</c>): the state after,
    /// the events in order, the value or the error. The kernel is also run on inline mechanics that exercise what the shipped ones do not
    /// (stacking policies, priorities, dependencies, conditions, formulas and the refusals). Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2MechanicCasesTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "damage");

        private static JObject _mechanics;
        private static JObject _ops;
        private static JObject _oracle;
        private static JObject _units;

        private static JObject Mechanics
        {
            get
            {
                if (_mechanics != null) return _mechanics;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var registries = Core2Registries.Load(snapshot.Content);
                var data = registries.ToCoreCombatData(snapshot.Content, registries.ToCoreRunData(snapshot.Content));
                _ops = data.MechanicOps;
                return _mechanics = data.Mechanics;
            }
        }

        private static JObject Oracle => _oracle ??= (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, "mechanics.json")));

        private static JObject Units => _units ??= (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, "units.json")));

        private static CoreMechanicsRuntime Runtime
        {
            get
            {
                var mechanics = Mechanics;
                return new CoreMechanicsRuntime(mechanics, _ops);
            }
        }

        public static IEnumerable<TestCaseData> Cases()
        {
            if (!File.Exists(Path.Combine(Dir, "mechanics.json"))) yield break;
            foreach (var row in ((JArray)Oracle["cases"]).OfType<JObject>())
                yield return new TestCaseData(row.Value<string>("mechanic"), row.Value<int>("index")).SetName("Core2Case_" + row.Value<string>("mechanic") + "_" + row.Value<int>("index"));
        }

        public static IEnumerable<TestCaseData> KernelCases()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var count = ((JArray)Units["kernel"]).Count;
            for (var i = 0; i < count; i++) yield return new TestCaseData(i).SetName("Core2Kernel_" + i);
        }

        [Test]
        public void EveryMechanicWithCasesIsReplayed()
        {
            var exported = Mechanics.Properties().Where(p => ((JArray)((JObject)p.Value)["cases"]).Count > 0).ToDictionary(p => p.Name, p => ((JArray)((JObject)p.Value)["cases"]).Count);
            var replayed = ((JArray)Oracle["cases"]).OfType<JObject>().GroupBy(r => r.Value<string>("mechanic")).ToDictionary(g => g.Key, g => g.Count());
            Assert.That(exported, Is.Not.Empty);
            Assert.That(replayed.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(exported.Keys.OrderBy(k => k, StringComparer.Ordinal)), "the mechanics with cases");
            foreach (var pair in exported) Assert.That(replayed[pair.Key], Is.EqualTo(pair.Value), pair.Key);
        }

        [TestCaseSource(nameof(Cases))]
        public void ACaseRunsAsTheWebRunsIt(string slug, int index)
        {
            var row = ((JArray)Oracle["cases"]).OfType<JObject>().First(r => r.Value<string>("mechanic") == slug && r.Value<int>("index") == index);
            var doc = (JObject)Mechanics[slug];
            var testCase = (JObject)((JArray)doc["cases"])[index];
            Assert.That(testCase.Value<string>("name"), Is.EqualTo(row.Value<string>("name")));
            var result = Runtime.RunCase(doc, testCase);
            Assert.That(result.Ok, Is.True, slug + " / " + row.Value<string>("name") + ": " + result.Error);
            if (row["state"] != null)
            {
                foreach (var section in new[] { "entities", "piles", "mechanics" })
                {
                    var diff = RunParityTests.FirstDifference(row["state"][section] ?? new JObject(), result.State[section] ?? new JObject(), "$." + section);
                    if (diff != null) Assert.Fail(slug + " / " + row.Value<string>("name") + ": first difference at " + diff);
                }
                Assert.That(result.Events, Is.EqualTo(row["events"].Select(t => t.Value<string>()).ToList()), "events");
            }
            if (row["value"] != null) Assert.That(RunParityTests.FirstDifference(row["value"], result.Value, "$"), Is.Null, slug + " value");
        }

        // ------------------------------------------------------------------ the generic kernel

        private static JToken Run(Func<JToken> body)
        {
            try { return body(); }
            catch (MechanicsException e) { return new JObject { ["error"] = e.Message }; }
            catch (InvalidOperationException e) { return new JObject { ["error"] = e.Message }; }
        }

        private static Dictionary<string, JToken> Context(JObject context)
        {
            var scope = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var p in context.Properties()) scope[p.Name] = p.Value;
            return scope;
        }

        [TestCaseSource(nameof(KernelCases))]
        public void TheKernelRunsInlineMechanicsAsTheWebDoes(int index)
        {
            var unit = (JObject)((JArray)Units["kernel"])[index];
            var mechanics = new JObject();
            foreach (var p in ((JObject)unit["mechanics"]).Properties())
                mechanics[p.Name] = new JObject { ["mechanic"] = p.Value["mechanic"].DeepClone(), ["interface"] = p.Value["interface"].DeepClone(), ["service"] = p.Value["service"].DeepClone() };
            var attachments = ((JArray)unit["attachments"]).OfType<JObject>().Select(a => (JObject)a.DeepClone()).ToList();
            var moduleParameters = unit["moduleParameters"] as JObject;
            JObject Clone(JToken t) => (JObject)t.DeepClone();
            var label = unit.Value<string>("name");
            var want = unit["result"];
            if (unit["query"] is JObject query)
            {
                var got = Run(() =>
                {
                    var bindings = CoreMechanicsKernel.CompileBindings(mechanics, attachments, moduleParameters == null ? null : Clone(moduleParameters));
                    if (bindings.Count != 1) throw new InvalidOperationException("one active binding");
                    return CoreMechanicsKernel.QueryBinding(bindings[0], query.Value<string>("query"), query["params"] as JObject);
                });
                Assert.That(RunParityTests.FirstDifference(want, got, "$"), Is.Null, label);
            }
            else if (unit.Value<bool>("bindings"))
            {
                var got = Run(() => new JArray(CoreMechanicsKernel.CompileBindings(mechanics, attachments, moduleParameters == null ? null : Clone(moduleParameters))
                    .Select(b => new JObject { ["id"] = b.Id, ["tag"] = b.Tag, ["config"] = b.Config.DeepClone() })));
                Assert.That(RunParityTests.FirstDifference(want, got, "$"), Is.Null, label);
            }
            else
            {
                var calls = unit["variants"] is JArray variants ? variants.OfType<JObject>().ToList() : new List<JObject> { (JObject)unit["call"] };
                var results = (JArray)want;
                for (var i = 0; i < calls.Count; i++)
                {
                    var call = calls[i];
                    var got = Run(() =>
                    {
                        var bindings = CoreMechanicsKernel.CompileBindings(mechanics, attachments, moduleParameters == null ? null : Clone(moduleParameters));
                        return CoreMechanicsKernel.EvaluateModifiers(bindings, call.Value<string>("phase"), call.Value<string>("subjectId"), call.Value<string>("actorId"),
                            Context((JObject)(call["context"] ?? new JObject())), call.Value<double>("amount"), call.Value<bool>("quantity"));
                    });
                    Assert.That(RunParityTests.FirstDifference(results[i], got, "$"), Is.Null, label + " #" + i);
                }
            }
        }
    }
}
