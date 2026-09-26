using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Saves;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-5.1 / US-5.9 / PF-06: the combat session. Refusals cost nothing and name what is missing; the legality
    /// service agrees with the engine on every card at every step of the golden combats; a fight saved mid-way with a
    /// command log resumes to the identical state; a tampered log stops at the last verified state (D-017).
    /// </summary>
    [TestFixture]
    public class CombatSessionTests
    {
        private static string CombatDir => Path.Combine(TestContent.OracleRoot, "combat");
        private static CombatData _data;
        private string _dir;

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static CombatData Data => _data ??= CombatData.FromRegistryDump(
            ReadJson(Path.Combine(CombatDir, "registries.json")),
            TestContent.ContentJson(ContentFiles.RulesMechanics),
            TestContent.ContentJson(ContentFiles.RulesCombatEngine));

        private static JObject Log(int index) => ReadJson(Path.Combine(CombatDir, $"combat-{index:000}.json"));

        private static CombatSession Start(JObject log) => CombatSession.Start(Data, (uint)log["seed"].Value<long>(), (JObject)log["create"]);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-combat-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        [Test]
        public void ARefusalNamesTheMissingEnergyAndCostsNothing()
        {
            var session = Start(Log(0));
            var energy = session.State.Player.Value<double>("energy");
            session.State.Player["energy"] = 0;
            var before = session.StateHash();
            var card = session.State.Piles.Hand.First(c => CombatEngine.EffectiveCost(session.State, Cards.Resolve(Data, c)) > 0);
            var outcome = session.Execute(CombatCommand.PlayCard(card.Value<string>("instanceId"), CombatLegality.Targets(session.State, card.Value<string>("instanceId")).FirstOrDefault()));
            Assert.That(outcome.Accepted, Is.False);
            Assert.That(outcome.Refusal.Key, Is.EqualTo(CombatStringKeys.CombatRefusalEnergy));
            Assert.That(outcome.Refusal.Args[1], Is.EqualTo(0d));
            Assert.That(session.StateHash(), Is.EqualTo(before), "a refusal must not change the fight");
            Assert.That(energy, Is.GreaterThan(0));
        }

        [Test]
        public void AcceptedCommandsRaiseResolvedWithTheirEvents()
        {
            var log = Log(1);
            var session = Start(log);
            var seen = new List<CombatOutcome>();
            session.Resolved += seen.Add;
            var first = CombatCommand.FromJson((JObject)log["steps"][0]["command"]);
            var outcome = session.Execute(first);
            Assert.That(outcome.Accepted, Is.True);
            Assert.That(seen, Has.Count.EqualTo(1));
            Assert.That(outcome.Events, Is.Not.Empty);
        }

        /// <summary>Property: at every step of the first 10 golden combats, CanPlay is null exactly when the engine accepts the play.</summary>
        [Test]
        public void LegalityAgreesWithTheEngineOnEveryCardAtEveryStep()
        {
            var checkedPlays = 0;
            for (var i = 0; i < 10; i++)
            {
                var log = Log(i);
                var session = Start(log);
                foreach (var step in ((JArray)log["steps"]).OfType<JObject>())
                {
                    foreach (var card in session.State.Piles.Hand.ToList())
                    {
                        var id = card.Value<string>("instanceId");
                        foreach (var target in new string[] { null }.Concat(session.State.Enemies.Select(e => e.Value<string>("id"))))
                        {
                            var legal = CombatLegality.CanPlay(session.State, id, target) == null;
                            var probe = CombatSession.Resume(Data, session.Checkpoint());
                            bool accepted;
                            try { CombatEngine.Dispatch(probe.State, CombatCommand.PlayCard(id, target)); accepted = true; }
                            catch (InvalidOperationException) { accepted = false; }
                            Assert.That(legal, Is.EqualTo(accepted), $"combat {i}: card {id} target {target ?? "none"}");
                            checkedPlays++;
                        }
                    }
                    session.Execute(CombatCommand.FromJson((JObject)step["command"]));
                }
            }
            Assert.That(checkedPlays, Is.GreaterThan(500));
        }

        [Test]
        public void SaveWithCommandLogResumesToTheIdenticalState()
        {
            var log = Log(2);
            var saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            var session = Start(log);
            session.Save(saves, "run0", "content");
            var steps = ((JArray)log["steps"]).OfType<JObject>().ToList();
            foreach (var step in steps.Take(steps.Count / 2)) session.ExecuteAndLog(saves, "run0", CombatCommand.FromJson((JObject)step["command"]));
            var loaded = saves.Load("run0");
            Assert.That(loaded.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(loaded.Commands.Count, Is.EqualTo(steps.Count / 2));
            var resumed = CombatSession.Load(Data, loaded, out var diverged);
            Assert.That(diverged, Is.False);
            Assert.That(resumed.StateHash(), Is.EqualTo(session.StateHash()));
            foreach (var step in steps.Skip(steps.Count / 2)) resumed.Execute(CombatCommand.FromJson((JObject)step["command"]));
            Assert.That(resumed.State.Result, Is.EqualTo(log["result"].Type == JTokenType.Null ? null : log.Value<string>("result")));
        }

        [Test]
        public void ATamperedLogStopsAtTheLastVerifiedState()
        {
            var log = Log(3);
            var saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            var session = Start(log);
            session.Save(saves, "run1", "content");
            var steps = ((JArray)log["steps"]).OfType<JObject>().ToList();
            var hashes = new List<string>();
            foreach (var step in steps.Take(4))
            {
                session.ExecuteAndLog(saves, "run1", CombatCommand.FromJson((JObject)step["command"]));
                hashes.Add(session.StateHash());
            }
            var loaded = saves.Load("run1");
            loaded.Commands[2]["stateHashAfter"] = "tampered";
            var resumed = CombatSession.Load(Data, loaded, out var diverged);
            Assert.That(diverged, Is.True);
            Assert.That(resumed.StateHash(), Is.EqualTo(hashes[1]), "resumes at the last verified command");
        }
    }
}
