using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// F1 run session (US-2.1 default character, US-5.x first fight, PF-06/AF-10 save and resume; 08 §12 SaveRoundTrip):
    /// content-built run data creates the shipped run documents; the first-fight rule is data; new → fight → save →
    /// load gives the identical state hash; a fight resumed mid-way after N logged commands equals the live one and
    /// plays on identically; a diverged log or changed content resumes at the last verified state with a warning; a
    /// payload this build cannot resume is Damaged; the summary W-03 reads is live.
    /// </summary>
    [TestFixture]
    public class RunSessionTests
    {
        private static RunContent _shipped;
        private static RunContent _default;
        private string _dir;
        private SaveService _saves;

        private static RunContent Shipped => _shipped ??= RunContent.Load(TestContent.Source, "shipped");
        private static RunContent Default => _default ??= RunContent.Load(TestContent.Source);

        private static readonly DateTime Epoch = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        private DateTime _now;
        private DateTime Clock() => _now;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-run-session-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            _now = Epoch;
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static string RunDir => Path.Combine(TestContent.OracleRoot, "run");

        /// <summary>A deterministic player: the first playable card at its first legal target, else end the turn (choosing the fewest discards).</summary>
        private static CombatCommand NextCommand(CombatState state)
        {
            var view = CombatViewModel.Build(state);
            var card = view.Hand.FirstOrDefault(c => c.Playable);
            if (card != null) return CombatCommand.PlayCard(card.InstanceId, card.Targets.FirstOrDefault());
            var plan = HandRules.Plan(state);
            return CombatCommand.EndTurn(plan.Cards.Take((int)plan.Minimum).Select(c => c.Value<string>("instanceId")));
        }

        private static void Play(RunSession session, int commands)
        {
            for (var i = 0; i < commands && !session.Combat.IsOver; i++)
                Assert.That(session.Execute(NextCommand(session.Combat.State)).Accepted, Is.True, "command " + i);
        }

        private RunSession NewFight(RunContent content, uint seed = 12345, string classId = null)
        {
            var session = RunSession.New(content, _saves, 1, seed, classId ?? content.DefaultClass(), "Aldric", Clock);
            session.StartEncounter();
            return session;
        }

        // ------------------------------------------------------------------ content

        [Test]
        public void ContentBuiltRunDataCreatesTheShippedRuns()
        {
            var index = ReadJson(Path.Combine(RunDir, "index.json"));
            var checkedRuns = 0;
            foreach (var r in ((JArray)index["runs"]).OfType<JObject>().Where(r => r.Value<string>("variant") == "default").Take(8))
            {
                var log = ReadJson(Path.Combine(RunDir, r.Value<string>("file")));
                var run = RunState.Create(Shipped.Data, (uint)log["seed"].Value<long>(), log.Value<string>("classId"));
                var diff = RunParityTests.FirstDifference(log["run"], run, "$");
                Assert.That(diff, Is.Null, r.Value<string>("file"));
                foreach (var fight in ((JArray)log["fights"]).OfType<JObject>())
                    Assert.That(RunParityTests.FirstDifference(fight["args"], RunCombat.CreateArgs(run, fight.Value<string>("encounterId"), Shipped.Data), "$"), Is.Null,
                        r.Value<string>("file") + " args " + fight.Value<string>("encounterId"));
                checkedRuns++;
            }
            Assert.That(checkedRuns, Is.EqualTo(8));
        }

        [Test]
        public void TheCreationTablesMatchTheShippedRegistries()
        {
            var dump = ReadJson(Path.Combine(RunDir, "registries.json"));
            var d = Shipped.Data;
            Assert.That(RunParityTests.FirstDifference(dump["creationModes"], new JArray(d.CreationModes.All), "$"), Is.Null);
            Assert.That(RunParityTests.FirstDifference(dump["seats"], new JArray(d.Seats.All), "$"), Is.Null);
            Assert.That(d.Encounters.All.Select(e => e.Value<string>("id")), Is.EqualTo(((JArray)dump["encounters"]).Select(e => e.Value<string>("id"))));
            Assert.That(RunParityTests.FirstDifference(dump["attributeRules"], d.AttributeRules, "$"), Is.Null);
            Assert.That(RunParityTests.FirstDifference(dump["derivedStatRules"], d.DerivedStatRules, "$"), Is.Null);
            Assert.That(d.ContentVersion, Is.EqualTo(dump.Value<string>("contentVersion")));
        }

        [Test]
        public void TheFirstFightIsTheDataRulesEncounter()
        {
            var id = Default.FirstFightEncounter();
            var encounter = Default.Data.Encounters.Get(id);
            Assert.That(encounter.Value<string>("seat"), Is.EqualTo(Default.Flow.FirstFightSeat));
            Assert.That(encounter.Value<string>("pool"), Is.EqualTo(Default.Flow.FirstFightPool));
            Assert.That(Default.RegionOf(id), Is.Not.Null.And.Not.Empty);
            Assert.That(Default.FirstFightEncounter(), Is.EqualTo(id), "deterministic");
            Assert.That(Default.ClassIds, Does.Contain(Default.Flow.DefaultClass));
            Assert.That(Default.DefaultClass(), Is.Not.Null, "some class can begin under the default preset");
            Assert.That(Shipped.DefaultClass(), Is.EqualTo(Shipped.Flow.DefaultClass), "the rule's default begins under the shipped tuning");
            foreach (var cls in Shipped.ClassIds) Assert.That(Shipped.ClassProblem(cls), Is.Null, cls);
        }

        [Test]
        public void EveryClassStartsTheFirstFightUnderTheDefaultPreset()
        {
            foreach (var classId in Default.ClassIds.Where(id => Default.ClassProblem(id) == null))
            {
                var session = NewFight(Default, 777, classId);
                Assert.That(session.IsInCombat, Is.True, classId);
                Assert.That(session.Combat.State.Result, Is.Null, classId);
                Assert.That(session.Combat.State.Piles.Hand, Is.Not.Empty, classId);
                Play(session, 4);
            }
        }

        // ------------------------------------------------------------------ save and resume

        [Test]
        public void NewFightSaveLoadGivesTheIdenticalState()
        {
            var session = NewFight(Default);
            var hash = session.StateHash();
            var loaded = RunSession.Load(Default, _saves, 1, Clock);
            Assert.That(loaded.Status, Is.EqualTo(RunLoadStatus.Resumed), string.Join("; ", loaded.Warnings));
            Assert.That(loaded.Session.IsInCombat, Is.True);
            Assert.That(loaded.Session.StateHash(), Is.EqualTo(hash));
            Assert.That(loaded.Session.EncounterId, Is.EqualTo(session.EncounterId));
            Assert.That(loaded.Session.Name, Is.EqualTo("Aldric"));
        }

        [TestCase(1), TestCase(3), TestCase(7), TestCase(12)]
        public void AFightResumedAfterNCommandsEqualsTheLiveOneAndPlaysOnIdentically(int n)
        {
            var live = NewFight(Default, 4242);
            Play(live, n);
            var loaded = RunSession.Load(Default, _saves, 1, Clock);
            Assert.That(loaded.Status, Is.EqualTo(RunLoadStatus.Resumed), string.Join("; ", loaded.Warnings));
            Assert.That(loaded.Session.StateHash(), Is.EqualTo(live.StateHash()), "resume equivalence after " + n);

            var twin = loaded.Session;
            for (var i = 0; i < 10 && !live.Combat.IsOver; i++)
            {
                var command = NextCommand(live.Combat.State);
                Assert.That(live.Combat.Execute(command).Accepted, Is.True);
                Assert.That(twin.Combat.Execute(command).Accepted, Is.True);
                Assert.That(twin.StateHash(), Is.EqualTo(live.StateHash()), "step " + i);
            }
        }

        [Test]
        public void CommandsAppendToTheLogAndTurnEndCheckpoints()
        {
            Assert.That(Default.Flow.AfterCommand, Is.EqualTo(RunFlowValues.AutosaveAppend));
            Assert.That(Default.Flow.CheckpointOn, Does.Contain(CombatValues.CommandEndTurn));
            var session = NewFight(Default, 99);
            var card = CombatViewModel.Build(session.Combat.State).Hand.First(c => c.Playable);
            Assert.That(session.Execute(CombatCommand.PlayCard(card.InstanceId, card.Targets.FirstOrDefault())).Accepted, Is.True);
            Assert.That(_saves.Load(_saves.Rules.RunSlotName(1)).Commands, Has.Count.EqualTo(1), "the played card is in the log");
            var gen = _saves.Load(_saves.Rules.RunSlotName(1)).Gen;
            var plan = HandRules.Plan(session.Combat.State);
            Assert.That(session.Execute(CombatCommand.EndTurn(plan.Cards.Take((int)plan.Minimum).Select(c => c.Value<string>("instanceId")))).Accepted, Is.True);
            var after = _saves.Load(_saves.Rules.RunSlotName(1));
            Assert.That(after.Gen, Is.GreaterThan(gen), "turn end writes a new checkpoint (PF-06 commit point)");
            Assert.That(after.Commands, Is.Empty);
        }

        [Test]
        public void ARefusedCommandIsNeitherAppliedNorSaved()
        {
            var session = NewFight(Default, 5);
            var hash = session.StateHash();
            var outcome = session.Execute(CombatCommand.PlayCard("no-such-card"));
            Assert.That(outcome.Accepted, Is.False);
            Assert.That(outcome.Refusal.Key, Is.EqualTo(CombatStringKeys.CombatRefusalNotInHand));
            Assert.That(session.StateHash(), Is.EqualTo(hash));
            Assert.That(_saves.Load(_saves.Rules.RunSlotName(1)).Commands, Is.Empty);
        }

        [Test]
        public void ATamperedLogResumesAtTheLastVerifiedCommandWithAWarning()
        {
            var session = NewFight(Default, 31337);
            var hashes = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var card = CombatViewModel.Build(session.Combat.State).Hand.FirstOrDefault(c => c.Playable);
                if (card == null) break;
                session.Execute(CombatCommand.PlayCard(card.InstanceId, card.Targets.FirstOrDefault()));
                hashes.Add(session.StateHash());
            }
            Assume.That(hashes.Count, Is.GreaterThanOrEqualTo(2), "needs two plays in the opening hand");
            var log = Directory.GetFiles(_dir, "*.log").Single();
            var lines = File.ReadAllLines(log);
            var record = JObject.Parse(lines[1]);
            record["stateHashAfter"] = "tampered";
            lines[1] = record.ToString(Formatting.None);
            File.WriteAllLines(log, lines);
            var loaded = RunSession.Load(Default, _saves, 1, Clock);
            Assert.That(loaded.Status, Is.EqualTo(RunLoadStatus.ResumedWithWarning));
            Assert.That(loaded.Session.StateHash(), Is.EqualTo(hashes[0]));
            Assert.That(loaded.Warnings, Is.Not.Empty);
            var again = RunSession.Load(Default, _saves, 1, Clock);
            Assert.That(again.Status, Is.EqualTo(RunLoadStatus.Resumed), "the slot was re-committed cleanly");
            Assert.That(again.Session.StateHash(), Is.EqualTo(hashes[0]));
        }

        [Test]
        public void ChangedContentResumesFromTheCheckpointWithoutTheLog()
        {
            var session = NewFight(Shipped, 8080);
            var handAtCheckpoint = session.Combat.State.Piles.Hand.Select(c => c.Value<string>("instanceId")).ToList();
            var card = CombatViewModel.Build(session.Combat.State).Hand.First(c => c.Playable);
            Assert.That(session.Execute(CombatCommand.PlayCard(card.InstanceId, card.Targets.FirstOrDefault())).Accepted, Is.True);
            Assume.That(Default.ContentHash, Is.Not.EqualTo(Shipped.ContentHash));
            var loaded = RunSession.Load(Default, _saves, 1, Clock);
            Assert.That(loaded.Status, Is.EqualTo(RunLoadStatus.ResumedWithWarning), string.Join("; ", loaded.Warnings));
            Assert.That(loaded.Session.Combat.State.Piles.Hand.Select(c => c.Value<string>("instanceId")), Is.EqualTo(handAtCheckpoint), "the logged play was not replayed");
        }

        [Test]
        public void EmptyNewerAndForeignPayloadsAreNotResumed()
        {
            Assert.That(RunSession.Load(Default, _saves, 2).Status, Is.EqualTo(RunLoadStatus.Empty));
            _saves.Checkpoint(_saves.Rules.RunSlotName(2), new JObject { ["summary"] = new JObject { ["name"] = "Old" } }, string.Empty, string.Empty);
            var foreign = RunSession.Load(Default, _saves, 2);
            Assert.That(foreign.Status, Is.EqualTo(RunLoadStatus.Damaged));
            Assert.That(foreign.Resumed, Is.False);
            var newer = new SaveRules { RunSlots = 3, CurrentVersion = _saves.Rules.CurrentVersion + 1, ProfileSlot = "profile", ReplaceRetries = 1, ReplaceRetryDelayMs = 1 };
            new SaveService(_dir, newer).Checkpoint(_saves.Rules.RunSlotName(3), new JObject(), string.Empty, string.Empty);
            Assert.That(RunSession.Load(Default, _saves, 3).Status, Is.EqualTo(RunLoadStatus.Newer));
            File.WriteAllText(Path.Combine(_dir, _saves.Rules.RunSlotName(2) + ".json"), "{ broken");
            foreach (var f in Directory.GetFiles(_dir, _saves.Rules.RunSlotName(2) + "*")) File.WriteAllText(f, "{ broken");
            Assert.That(RunSession.Load(Default, _saves, 2).Status, Is.EqualTo(RunLoadStatus.Damaged));
        }

        [Test]
        public void TheSlotSummaryIsLiveAndReadByTheSlotRows()
        {
            var session = NewFight(Default, 2718);
            _now = Epoch.AddMinutes(5);
            Play(session, 3);
            session.Save();
            var summary = SlotSummaries.ReadOne(_saves, 1);
            Assert.That(summary.Status, Is.EqualTo(SlotStatus.Ready));
            Assert.That(summary.Name, Is.EqualTo("Aldric"));
            Assert.That(summary.ClassId, Is.EqualTo(Default.DefaultClass()));
            Assert.That(summary.Portrait, Is.EqualTo(Default.Portrait(Default.DefaultClass())));
            Assert.That(summary.Hp, Is.EqualTo((int)session.Combat.State.Player.Value<double>("hp")));
            Assert.That(summary.HpMax, Is.GreaterThanOrEqualTo(summary.Hp));
            Assert.That(summary.Act, Is.EqualTo(1));
            Assert.That(summary.Seed, Is.EqualTo(Default.Seeds.Format(2718)));
            Assert.That(summary.SavedAt, Is.EqualTo("2026-09-26T12:05:00Z"));
            Assert.That(summary.PlaytimeSeconds, Is.EqualTo(300));
            Assert.That(summary.Journey, Is.EqualTo(Default.Flow.Journey));
            Assert.That(SlotSummaries.ContinueTarget(SlotSummaries.Read(_saves)).Index, Is.EqualTo(1));
        }

        [Test]
        public void AFinishedFightIsCheckpointedAtItsRewardsOrClearedByADeath()
        {
            var session = NewFight(Default, 1);
            for (var i = 0; i < 400 && !session.Combat.IsOver; i++) session.Execute(NextCommand(session.Combat.State));
            Assert.That(session.Combat.IsOver, Is.True, "the bot finishes the fight");
            Assert.That(session.FightFinished, Is.True, "the post-combat door ran with the last command");
            var loaded = RunSession.Load(Default, _saves, 1, Clock);
            if (session.Combat.State.Result == CombatValues.Victory)
            {
                Assert.That(loaded.Session.IsInCombat, Is.False, "the save no longer holds the fight");
                Assert.That(loaded.Session.HasPendingReward, Is.True, "it holds the rolled rewards");
                Assert.That(loaded.Session.StateHash(), Is.EqualTo(session.StateHash()));
            }
            else
            {
                Assert.That(loaded.Status, Is.EqualTo(RunLoadStatus.Empty), "a death clears the slot");
            }
        }
    }
}
