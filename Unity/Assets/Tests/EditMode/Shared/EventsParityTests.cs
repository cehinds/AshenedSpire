using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Events;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-10.1–10.3 event parity (PF-04/05; D-036, D-037, D-040): every session Tools/oracle-events.mjs recorded from the
    /// SHIPPED event door — main.js showEvent/onDone, the event and dialogue screens' view facts and engine/quests.js
    /// commitEventChoice over the shipped models, on the shipped and reference presets — is replayed on CONTENT-built data
    /// through <see cref="Events.Open"/>, <see cref="Events.Choose"/>, <see cref="Events.Finish"/> and
    /// <see cref="RunEffects.Execute"/>: every view, outcome or refusal, event log, the run document after every step
    /// (strict: presence, values, key order) and the RNG counters must equal the shipped ones. The content-built event
    /// tables must equal the shipped registries row for row and key for key.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class EventsParityTests
    {
        private static string EventsDir => Path.Combine(TestContent.OracleRoot, "events");

        private static readonly Dictionary<string, EventsData> DataByPreset = new Dictionary<string, EventsData>(StringComparer.Ordinal);

        internal static EventsData Data(string preset)
        {
            if (DataByPreset.TryGetValue(preset, out var cached)) return cached;
            var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = preset });
            var content = snapshot.Content;
            var registries = RuntimeRegistries.Build(content, (JObject)content.Get(ContentFiles.StringsEn));
            return DataByPreset[preset] = registries.ToEventsData(
                (JObject)content.Get(ContentFiles.RulesMechanics), (JObject)content.Get(ContentFiles.RulesCombatEngine),
                (JObject)content.Get(ContentFiles.RulesHandRules), (JObject)content.Get(ContentFiles.RulesRunEngine),
                snapshot.ContentVersion, (JArray)content.Get(ContentFiles.BalanceCustomRun)["ASCENSION_ORDER"],
                (JObject)content.Get(ContentFiles.RulesRewardsEngine), (JObject)content.Get(ContentFiles.RulesShopEngine),
                (JObject)content.Get(ContentFiles.RulesEventsEngine));
        }

        private static JObject Index => ShopParityTests.ReadJson(Path.Combine(EventsDir, "index.json"));

        public static IEnumerable<string> SessionFiles() =>
            Directory.Exists(EventsDir)
                ? Directory.GetFiles(EventsDir, "*.json").Select(Path.GetFileName)
                    .Where(f => f.StartsWith("sweep-", StringComparison.Ordinal) || f.StartsWith("walk-", StringComparison.Ordinal))
                    .OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        public static IEnumerable<string> EffectFiles() =>
            Directory.Exists(EventsDir)
                ? Directory.GetFiles(EventsDir, "effects-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        private static void CheckCounters(string file, string label, JObject want, Rng rng)
        {
            foreach (var p in want.Properties())
                Assert.That(rng.Counter(RngStreamNames.Parse(p.Name)), Is.EqualTo((uint)p.Value.Value<long>()), $"{file} {label} rng {p.Name}");
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var sessions = (JArray)Index["sessions"];
            Assert.That(SessionFiles().Count() + EffectFiles().Count(), Is.EqualTo(sessions.Count));
            foreach (var family in new[] { "sweep", "walk", "effects" })
                Assert.That(sessions.Count(s => s.Value<string>("family") == family), Is.GreaterThan(20), family);
            var accepted = ((JObject)Index["tally"]["accepted"]).Properties().Select(p => p.Name).ToList();
            var ids = (JObject)Data("shipped").EventChoiceIds;
            Assert.That(ids.Count, Is.EqualTo(25), "25 events");
            foreach (var e in ids.Properties())
                foreach (var choice in (JArray)e.Value)
                    Assert.That(accepted, Does.Contain(e.Name + "/" + choice.Value<string>()), "no recorded commit of " + e.Name + "/" + choice);
            var refused = ((JObject)Index["tally"]["refused"]).Properties().Select(p => p.Name.Split(':')[1]).Distinct().ToList();
            foreach (var field in typeof(EventStringKeys).GetFields())
                Assert.That(refused, Does.Contain((string)field.GetValue(null)), field.Name);
            Assert.That(Index["tally"].Value<int>("fights"), Is.GreaterThan(0));
        }

        [Test]
        public void EveryRefusalKeyResolvesToText()
        {
            var strings = JObject.Parse(File.ReadAllText(Path.Combine(TestContent.Root, "strings", "events.en.json")));
            foreach (var field in typeof(EventStringKeys).GetFields())
                Assert.That(strings.Value<string>((string)field.GetValue(null)), Is.Not.Null.And.Not.Empty, field.Name);
        }

        public static IEnumerable<string> Presets() => new[] { "shipped", "reference" };

        [TestCaseSource(nameof(Presets))]
        public void ContentEventTablesEqualTheShippedOnes(string preset)
        {
            var want = (JObject)ShopParityTests.ReadJson(Path.Combine(EventsDir, "registries.json"))[preset];
            var d = Data(preset);
            Check(preset, "events", want["events"], new JArray(d.Events.All));
            Check(preset, "speakers", want["speakers"], new JArray(d.Speakers.All));
            Check(preset, "questChains", want["questChains"], d.QuestChains);
            Check(preset, "eventSpeakers", want["eventSpeakers"], d.EventSpeakers);
            Check(preset, "eventHistoryRequirements", want["eventHistoryRequirements"], d.EventHistoryRequirements);
            Check(preset, "eventChoiceIds", want["eventChoiceIds"], d.EventChoiceIds);
            Check(preset, "eventChoiceHistoryRequirements", want["eventChoiceHistoryRequirements"], d.ChoiceRequirements);
        }

        [TestCaseSource(nameof(SessionFiles))]
        public void EventSessionMatchesTheShippedDoor(string file)
        {
            var log = ShopParityTests.ReadJson(Path.Combine(EventsDir, file));
            var d = Data(log.Value<string>("preset"));
            var run = (JObject)log["run"].DeepClone();
            var expectedRun = (JObject)log["run"].DeepClone();
            var rng = new Rng((uint)log["seed"].Value<long>(), ShopParityTests.Counters((JObject)log["rngBefore"]));
            var visits = (JArray)log["visits"];
            for (var i = 0; i < visits.Count; i++)
            {
                var visit = (JObject)visits[i];
                var eventId = visit.Value<string>("eventId");
                var label = $"visit {i} {eventId}/{visit.Value<string>("choiceId")}";
                Check(file, label + " view", visit["view"], Events.Open(d, run, eventId));
                var result = Events.Choose(d, run, eventId, visit.Value<string>("choiceId"), rng);
                Check(file, label + " result", visit["result"], result.ToJson());
                ShopParityTests.ApplyDelta(expectedRun, (JObject)visit["runDelta"]);
                Check(file, label + " run", expectedRun, run);
                CheckCounters(file, label, (JObject)visit["rngAfter"], rng);
                if (!(visit["finish"] is JObject finish)) continue;
                var done = Events.Finish(run);
                Check(file, label + " finish", finish["fight"], done["fight"]);
                ShopParityTests.ApplyDelta(expectedRun, (JObject)finish["runDelta"]);
                Check(file, label + " run after finish", expectedRun, run);
            }
        }

        [TestCaseSource(nameof(EffectFiles))]
        public void RunEffectsMatchTheShippedDoor(string file)
        {
            var log = ShopParityTests.ReadJson(Path.Combine(EventsDir, file));
            var d = Data(log.Value<string>("preset"));
            var run = (JObject)log["run"].DeepClone();
            var rng = new Rng((uint)log["seed"].Value<long>(), ShopParityTests.Counters((JObject)log["rngBefore"]));
            var expected = (JObject)log["expected"];
            if (expected["error"] != null)
            {
                Assert.Throws<InvalidOperationException>(() => RunEffects.Execute(d, run, rng, (JArray)log["effects"].DeepClone()), file);
                return;
            }
            var events = RunEffects.Execute(d, run, rng, (JArray)log["effects"].DeepClone());
            Check(file, "events", expected["events"], events);
            var expectedRun = (JObject)log["run"].DeepClone();
            ShopParityTests.ApplyDelta(expectedRun, (JObject)expected["runDelta"]);
            Check(file, "run", expectedRun, run);
            CheckCounters(file, "effects", (JObject)expected["rngAfter"], rng);
        }

        [Test]
        public void DeferredOpsThrowByName()
        {
            var file = EffectFiles().First();
            var log = ShopParityTests.ReadJson(Path.Combine(EventsDir, file));
            var d = Data(log.Value<string>("preset"));
            var rng = new Rng((uint)log["seed"].Value<long>(), ShopParityTests.Counters((JObject)log["rngBefore"]));
            var e = Assert.Throws<NotSupportedException>(() => RunEffects.Execute(d, (JObject)log["run"].DeepClone(), rng, new JArray(new JObject { ["op"] = "refillFlasks" })));
            Assert.That(e.Message, Is.EqualTo(EventMessages.RefillFlasksDeferred));
            e = Assert.Throws<NotSupportedException>(() => RunEffects.Execute(d, (JObject)log["run"].DeepClone(), rng, new JArray(new JObject { ["script"] = "anything" })));
            Assert.That(e.Message, Is.EqualTo(EventMessages.ScriptsDeferred));
        }

        private static void Check(string file, string label, JToken want, JToken got)
        {
            var diff = RewardsParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{file} {label}: first difference at {diff}");
        }
    }
}
