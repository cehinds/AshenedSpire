using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-C1a (D-302): a Core 2 run hosted through the application layer. It is created through the Core 2 profile and saved at the act map, travels to a fight node and starts a
    /// Core 2 fight on the run's own streams, writes the fight back to the run and returns to the map, ends on a death (or the last act's boss) into the profile, answers the nodes
    /// whose rules are later work packages as "not yet ported", and saves and resumes at the map and in the middle of a fight. The title door (<see cref="RunHost"/>) picks the
    /// ruleset from rules/runFlow.json and reads a slot by its save format. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RunSessionTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        private static RunContent Content => Core2HostParityTests.Content;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-run-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession New(uint seed = 12345) => Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);

        private static JObject Intent(string type, params object[] pairs)
        {
            var o = new JObject { ["type"] = type };
            for (var i = 0; i + 1 < pairs.Length; i += 2) o[(string)pairs[i]] = JToken.FromObject(pairs[i + 1] ?? JValue.CreateNull());
            return o;
        }

        /// <summary>A cheap player: each card of the hand at the first enemy (the rules refuse what they refuse), answer an offer with Back, then end the turn.</summary>
        private static void PlayTurn(Core2RunSession session)
        {
            var state = session.Combat.State;
            for (var guard = 0; guard < 40 && session.IsInCombat; guard++)
            {
                if (session.Combat.PendingOffer() is JObject offer) { session.Execute(Intent("chooseReaction", "offerId", offer["id"], "optionId", null)); continue; }
                if (session.Combat.PendingChoice() != null) { var answer = session.Combat.LegalIntents().First(); session.Execute(answer); continue; }
                var played = false;
                foreach (var card in state.Piles.Hand.ToList())
                {
                    var target = state.Enemies.FirstOrDefault(e => e.Value<bool>("alive"))?.Value<string>("id");
                    var intent = Intent("playCard", "cardInstanceId", card.Value<string>("instanceId"));
                    if (target != null) intent["targetId"] = target;
                    if (!session.IsInCombat) return;
                    if (session.Execute(intent).Accepted) { played = true; break; }
                }
                if (!played) { session.Execute(Intent("endTurn", "discardIds", new string[0])); return; }
            }
        }

        private static void FightToTheEnd(Core2RunSession session)
        {
            for (var guard = 0; guard < 400 && session.IsInCombat; guard++) PlayTurn(session);
            Assert.That(session.IsInCombat, Is.False, "the fight ended");
        }

        /// <summary>The reachable node of the given type from where the run stands, else the first one.</summary>
        private static string Pick(Core2RunSession session, Func<JObject, bool> want = null)
        {
            var graph = session.Run.Obj("mapGraph").Obj("nodes");
            var reachable = session.ReachableNodes();
            return reachable.FirstOrDefault(id => want != null && want((JObject)graph[id])) ?? reachable[0];
        }

        // ------------------------------------------------------------------ create and travel

        [Test]
        public void ANewRunIsMadeThroughTheCore2ProfileAndSavedAtTheMap()
        {
            var session = New();
            Assert.That(session.Location, Is.EqualTo("map"));
            var run = session.Run;
            Assert.That(run["gameProfile"]["hash"].Value<string>(), Is.EqualTo(Content.Core2.ProfileHash), "the run pins the profile");
            Assert.That(run["seatOrder"], Is.Not.Null);
            Assert.That(run["mapGraph"]["gameProfileHash"].Value<string>(), Is.EqualTo(Content.Core2.ProfileHash));
            Assert.That(run["stats"]["fightsWon"].Value<int>(), Is.EqualTo(0));
            Assert.That(session.ReachableNodes(), Is.Not.Empty);
            Assert.That(Core2RunSession.FormatOf(_saves, 1), Is.EqualTo(3), "a Core 2 save is format 3");
            Assert.That(session.SeedText, Is.Not.Empty);
            // The slot summary W-02 reads is the live one.
            var summary = session.Summary();
            Assert.That(summary["classId"].Value<string>(), Is.EqualTo("reaver"));
            Assert.That(summary["act"].Value<int>(), Is.EqualTo(1));
            // A class still locked in the profile's ledger is refused by the rules.
            Assert.Throws<InvalidOperationException>(() => Core2RunSession.New(Content, _saves, 2, 5u, "rogue", "X", () => Epoch));
        }

        [Test]
        public void TravellingToAFightNodeStartsACore2FightOnTheRunsStreams()
        {
            var session = New();
            Assert.That(session.Travel("not-a-node").Refusal, Is.EqualTo(RunFlowValues.RefusalNotReachable));
            var node = session.ReachableNodes()[0];
            var result = session.Travel(node);
            Assert.That(result.Travelled, Is.True);
            Assert.That(result.Door.IsFight, Is.True, "the opening depth is a fight");
            Assert.That(session.IsInCombat, Is.True);
            Assert.That(session.Location, Is.EqualTo("combat"));
            Assert.That(session.Combat.State.Player.Value<string>("classId"), Is.EqualTo("reaver"));
            Assert.That(session.EncounterId, Is.EqualTo(result.Door.EncounterId));
            Assert.That(session.Run["combatEntered"]["encounterId"].Value<string>(), Is.EqualTo(result.Door.EncounterId));
            Assert.That(session.Run.Value<string>("mapNodeId"), Is.EqualTo(node));
            Assert.That(session.Travel(node).Refusal, Is.EqualTo(RunFlowValues.RefusalNotOnMap), "no travel mid-fight");
            Assert.That(session.Combat.State.Rng.AllCounters()["enemyAI"], Is.GreaterThan(0u), "the fight draws on the run's streams");
        }

        // ------------------------------------------------------------------ the fight's end

        [Test]
        public void AWonFightIsWrittenBackToTheRunAndTheRunReturnsToTheMap()
        {
            var session = New(777);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r => { r["maxHp"] = 5000; r["hp"] = 5000; });
            // The test player is the engine's own: restart the fight with the strong run.
            session.StartEncounter(session.EncounterId);
            FightToTheEnd(session);
            Assert.That(session.Combat.State.Result, Is.EqualTo("victory"));
            Assert.That(session.FightFinished, Is.True);
            Assert.That(session.LastEnd.Outcome, Is.EqualTo("victory"));
            Assert.That(session.LastEnd.NotYetPorted, Does.Contain("rewards"), "the reward door is a later work package");
            Assert.That(session.Location, Is.EqualTo("map"));
            Assert.That(session.RunOver, Is.False);
            var run = session.Run;
            Assert.That(run["stats"]["fightsWon"].Value<int>(), Is.EqualTo(1));
            Assert.That(run["combatEntered"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(run["hp"].Value<double>(), Is.EqualTo(session.Combat.State.Player.Value<double>("hp")), "the pools the fight left are the run's");
            Assert.That(run["streamCounters"]["enemyAI"].Value<double>(), Is.GreaterThan(0), "the run's counters moved with the fight");
            Assert.That(session.ReachableNodes(), Is.Not.Empty, "the climb goes on");
            var resumed = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(resumed.Status, Is.EqualTo(Core2RunLoadStatus.Resumed), string.Join("; ", resumed.Warnings));
            Assert.That(resumed.Session.StateHash(), Is.EqualTo(session.StateHash()), "the map after a fight is saved");
        }

        [Test]
        public void ADeathEndsTheRunIntoTheProfileAndClearsTheSlot()
        {
            var session = New(31337);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r => { r["hp"] = 1; r["maxHp"] = 1; });
            session.StartEncounter(session.EncounterId);
            FightToTheEnd(session);
            Assert.That(session.Combat.State.Result, Is.EqualTo("defeat"));
            Assert.That(session.RunOver, Is.True);
            Assert.That(session.Location, Is.EqualTo("runEnd"));
            Assert.That(session.LastEnd.Outcome, Is.EqualTo("defeat"));
            Assert.That(session.End.Victory, Is.False);
            Assert.That(_saves.Exists(session.Slot), Is.False, "permadeath clears the slot");
            var profile = ProfileStore.Load(_saves).Doc;
            Assert.That(((JArray)profile["results"]).Count, Is.EqualTo(1));
            var record = (JObject)((JArray)profile["results"]).Last;
            Assert.That(record["victory"].Value<bool>(), Is.False);
            Assert.That(record["profileHash"].Value<string>(), Is.EqualTo(Content.Core2.ProfileHash), "the record states the profile it was played under");
            Assert.That(record["class"].Value<string>(), Is.EqualTo("reaver"));
            // A finished run counts toward the class unlock ledger: the next class opens.
            var ledger = profile["gameProfileUnlocks"][Content.Core2.ProfileId]["completedRunIds"] as JArray;
            Assert.That(ledger, Has.Count.EqualTo(1));
            Assert.That(session.ClassesUnlocked, Is.Not.Empty);
            Assert.That(() => Core2RunSession.New(Content, _saves, 2, 5u, session.ClassesUnlocked[0], "Next", () => Epoch), Throws.Nothing);
            Assert.Throws<InvalidOperationException>(() => session.Execute(Intent("endTurn", "discardIds", new string[0])));
        }

        [Test]
        public void TheLastBossesEndTheClimbWithAVictoryAndEarlierOnesAdvanceTheAct()
        {
            var session = New(2026);
            session.EditRunForReview(r => { r["maxHp"] = 100000; r["hp"] = 100000; });
            var acts = new List<int>();
            for (var guard = 0; guard < 400 && !session.RunOver; guard++)
            {
                if (session.IsInCombat) { FightToTheEnd(session); if (session.LastEnd.ActAdvanced) acts.Add(session.Act); continue; }
                var result = session.Travel(Pick(session, n => n.Value<string>("type") == "monster" || n.Value<string>("type") == "boss"));
                Assert.That(result.Travelled, Is.True);
            }
            Assert.That(session.RunOver, Is.True, "the climb finished within the guard");
            Assert.That(session.End.Victory, Is.True);
            Assert.That(acts, Is.EqualTo(Enumerable.Range(2, Content.Core2.Loop.Acts - 1).ToList()), "each act but the last advanced");
            Assert.That(session.Run["bossesBeaten"], Has.Count.EqualTo(Content.Core2.Loop.Acts));
            Assert.That(ProfileStore.Load(_saves).Doc["progress"]["wins"].Value<int>(), Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ the nodes later work packages own

        [Test]
        public void ANodeWhoseRulesAreALaterPackageIsAnsweredNotYetPorted()
        {
            var session = New(99);
            var seen = new HashSet<string>();
            session.EditRunForReview(r => { r["maxHp"] = 100000; r["hp"] = 100000; });
            for (var guard = 0; guard < 200 && !session.RunOver && seen.Count < 2; guard++)
            {
                if (session.IsInCombat) { FightToTheEnd(session); continue; }
                var graph = session.Run.Obj("mapGraph").Obj("nodes");
                var node = session.ReachableNodes().FirstOrDefault(id => graph.Obj(id).Value<string>("type") != "monster" && graph.Obj(id).Value<string>("type") != "boss" && graph.Obj(id).Value<string>("type") != "elite") ?? session.ReachableNodes()[0];
                var result = session.Travel(node);
                if (!result.NotYetPorted) continue;
                seen.Add(result.Door.Kind);
                Assert.That(session.Location, Is.EqualTo("map"), "the run stands at the node and goes on");
                Assert.That(session.PendingPort, Is.EqualTo(result.Door.Kind));
                Assert.That(session.Run.Value<string>("mapNodeId"), Is.EqualTo(node));
                session.DismissPort();
                Assert.That(session.PendingPort, Is.Null);
            }
            Assert.That(seen, Is.Not.Empty, "a climb meets a rest, shop, treasure or event");
        }

        // ------------------------------------------------------------------ save and resume

        [Test]
        public void ARunResumesAtTheMapAndInTheMiddleOfAFightOnTheSameState()
        {
            var session = New(4242);
            var atMap = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(atMap.Status, Is.EqualTo(Core2RunLoadStatus.Resumed));
            Assert.That(atMap.Session.StateHash(), Is.EqualTo(session.StateHash()));
            Assert.That(atMap.Session.ReachableNodes(), Is.EqualTo(session.ReachableNodes()));

            session.Travel(session.ReachableNodes()[0]);
            // One card: the fight is part way through, with a logged command after its checkpoint.
            var state = session.Combat.State;
            foreach (var card in state.Piles.Hand.ToList())
            {
                var intent = Intent("playCard", "cardInstanceId", card.Value<string>("instanceId"), "targetId", state.Enemies[0].Value<string>("id"));
                if (session.Execute(intent).Accepted) break;
                if (session.Execute(Intent("playCard", "cardInstanceId", card.Value<string>("instanceId"))).Accepted) break;
            }
            Assert.That(session.IsInCombat, Is.True);
            Assert.That(session.Combat.LoggedCommands, Is.EqualTo(1), "one command after the checkpoint at the fight's entry");
            var loaded = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(loaded.Status, Is.EqualTo(Core2RunLoadStatus.Resumed), string.Join("; ", loaded.Warnings));
            var resumed = loaded.Session;
            Assert.That(resumed.IsInCombat, Is.True);
            Assert.That(resumed.StateHash(), Is.EqualTo(session.StateHash()), "the reload is the saved state, command log replayed");
            Assert.That(resumed.EncounterId, Is.EqualTo(session.EncounterId));
            // Both play on to the same end.
            for (var guard = 0; guard < 200 && session.IsInCombat; guard++)
            {
                PlayTurn(session);
                PlayTurn(resumed);
                Assert.That(resumed.IsInCombat, Is.EqualTo(session.IsInCombat), "turn " + guard);
                if (session.IsInCombat) Assert.That(resumed.Combat.StateHash(), Is.EqualTo(session.Combat.StateHash()), "turn " + guard);
            }
            Assert.That(resumed.Run.ToString(), Is.EqualTo(session.Run.ToString()), "the run after the fight");
        }

        [Test]
        public void ASaveOfAnotherProfileOrAnotherRulesetIsRefusedAndKept()
        {
            var session = New(8);
            var envelope = _saves.Load(session.Slot);
            var payload = (JObject)envelope.Payload.DeepClone();
            payload["gameProfile"]["hash"] = "sha256:other";
            _saves.Checkpoint(session.Slot, payload, "c", session.StateHash());
            var mismatch = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(mismatch.Status, Is.EqualTo(Core2RunLoadStatus.ProfileMismatch));
            Assert.That(_saves.Exists(session.Slot), Is.True, "the slot is kept as it is");

            var legacy = (JObject)envelope.Payload.DeepClone();
            legacy["formatVersion"] = Content.Flow.SaveFormat;
            _saves.Checkpoint(session.Slot, legacy, "c", session.StateHash());
            Assert.That(Core2RunSession.Load(Content, _saves, 1, () => Epoch).Status, Is.EqualTo(Core2RunLoadStatus.OtherRuleset), "the 405 format is the other session's");
            Assert.That(Core2RunSession.Load(Content, _saves, 5, () => Epoch).Status, Is.EqualTo(Core2RunLoadStatus.Empty));
        }

        [Test]
        public void TheGamesDefaultPresetHostsACore2RunToo()
        {
            var content = RunContent.Load(TestContent.Source);
            Assert.That(content.PresetId, Is.Not.EqualTo(Content.PresetId), "this is the tuning the game ships, not the neutral one the other tests use");
            var session = Core2RunSession.New(content, _saves, 1, 5u, "reaver", "Preset", () => Epoch);
            Assert.That(session.Travel(session.ReachableNodes()[0]).Door.IsFight, Is.True);
            Assert.That(session.Combat.State.Player.Value<string>("classId"), Is.EqualTo("reaver"));
            Assert.That(Core2RunSession.Load(content, _saves, 1, () => Epoch).Status, Is.EqualTo(Core2RunLoadStatus.Resumed));
        }

        // ------------------------------------------------------------------ the title door

        [Test]
        public void TheTitleDoorStartsTheRulesetTheDataNamesAndReadsASlotByItsFormat()
        {
            Assert.That(RunHost.Ruleset(Content), Is.EqualTo(RunFlowValues.RulesetCore2), "the data's New Game is the Core 2 run (owner ruling D-164; WP-C1b flipped the switch)");
            Assert.That(RunHost.Start(Content, _saves, 5, 7u, "reaver", "Data", () => Epoch).IsCore2, Is.True, "a New Game starts the ruleset the data names");
            var legacy = RunHost.Start(Content, _saves, 1, 7u, Content.DefaultClass(), "Old", () => Epoch, ruleset: RunFlowValues.RulesetLegacy);
            Assert.That(legacy.Legacy, Is.Not.Null);
            Assert.That(legacy.IsCore2, Is.False);

            var core2 = RunHost.Start(Content, _saves, 2, 7u, "reaver", "New", () => Epoch, ruleset: RunFlowValues.RulesetCore2);
            Assert.That(core2.IsCore2, Is.True);
            Assert.That(core2.Location, Is.EqualTo("map"));

            var flow = Content.Flow;
            var before = flow.NewRunRuleset;
            try
            {
                flow.NewRunRuleset = RunFlowValues.RulesetLegacy;
                Assert.That(RunHost.Start(Content, _saves, 3, 9u, Content.DefaultClass(), "Flip", () => Epoch).IsCore2, Is.False, "flipping the data back starts a 405 run");
            }
            finally { flow.NewRunRuleset = before; }
            Assert.Throws<ArgumentException>(() => RunHost.Start(Content, _saves, 4, 1u, "reaver", "X", () => Epoch, ruleset: "nonsense"));

            var back1 = RunHost.Load(Content, _saves, 1, () => Epoch);
            Assert.That(back1.Run.Legacy, Is.Not.Null, "a format-2 slot is the 405 run");
            var back2 = RunHost.Load(Content, _saves, 2, () => Epoch);
            Assert.That(back2.Run.Core2, Is.Not.Null, "a format-3 slot is a Core 2 run");
            Assert.That(RunHost.Load(Content, _saves, 6, () => Epoch).Resumed, Is.False);
        }
    }
}
