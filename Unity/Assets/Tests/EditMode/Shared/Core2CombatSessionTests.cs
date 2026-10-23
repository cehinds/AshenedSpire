using System;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Saves;
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
    /// WP-13 (D-280, D-281): the engine-free Core 2 fight session. It starts a fight from a run on the run's own streams, plays intents through the one
    /// door the web has, refuses without cost, answers the previews and legal intents a screen asks for, and saves and resumes through <see cref="SaveService"/>:
    /// a fight paused on a reaction offer is a checkpoint like any other and resumes at the offer, on exactly the state it was saved in (the recorded web
    /// fights play on from the loaded save to the same end). Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2CombatSessionTests
    {
        private string _dir;
        private SaveService _saves;

        private static CoreCombatData Data => Core2DamageParityTests.Data;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-saves-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static JObject Run(string classId, uint seed, JObject spec = null)
        {
            var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            if (spec?["deckRemove"] is JArray remove)
            {
                var ids = remove.Select(t => t.Value<string>()).ToList();
                foreach (var card in ((JArray)run["deck"]).OfType<JObject>().Where(card => ids.Contains(card.Value<string>("cardId"))).ToList()) card.Remove();
            }
            foreach (var add in (spec?["deckAdd"] as JArray ?? new JArray()).OfType<JObject>())
                ((JArray)run["deck"]).Add(new JObject { ["instanceId"] = add["instanceId"].DeepClone(), ["cardId"] = add["cardId"].DeepClone(), ["upgraded"] = false });
            return run;
        }

        private static JObject Intent(string type, params object[] pairs)
        {
            var o = new JObject { ["type"] = type };
            for (var i = 0; i + 1 < pairs.Length; i += 2) o[(string)pairs[i]] = JToken.FromObject(pairs[i + 1] ?? JValue.CreateNull());
            return o;
        }

        // ------------------------------------------------------------------ the recorded fight with a pause on an offer

        /// <summary>A recorded reaction fight and the step it first pauses on an offer at.</summary>
        private static (JObject Record, int Pause) PausedFight()
        {
            foreach (var file in Core2ReactionParityTests.FightFiles())
                foreach (var fight in ((JArray)Core2ReactionParityTests.Read(file)["fights"]).OfType<JObject>())
                {
                    var steps = ((JArray)fight["steps"]).OfType<JObject>().ToList();
                    var pause = steps.FindIndex(step => step["error"] == null && step["offer"] != null && step["offer"].Type != JTokenType.Null);
                    if (pause >= 0 && pause < steps.Count - 2) return (fight, pause);
                }
            throw new InvalidOperationException("no recorded fight pauses on an offer");
        }

        private static Core2CombatSession StartRecorded(JObject record, out JObject run)
        {
            var seed = (uint)record["seed"].Value<long>();
            run = Run(record.Value<string>("classId"), seed, record);
            return Core2CombatSession.Start(Data, run, record.Value<string>("encounterId"));
        }

        private static Core2Outcome Play(Core2CombatSession session, JObject step)
        {
            foreach (var id in (step["staggered"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                session.State.Enemies.First(e => e.Value<string>("id") == id)["skipNextTurn"] = true;
            return session.Execute((JObject)step["intent"]);
        }

        [Test]
        public void ASessionStartsFromARunOnTheRunsOwnStreams()
        {
            var (record, _) = PausedFight();
            var session = StartRecorded(record, out var run);
            var seed = (uint)record["seed"].Value<long>();
            var alone = CoreRunCombat.Create(Data, new Rng(seed), Run(record.Value<string>("classId"), seed, record), record.Value<string>("encounterId"));
            Assert.That(session.Seed, Is.EqualTo(seed));
            Assert.That(session.State.Phase, Is.EqualTo("player"));
            Assert.That(session.IsOver, Is.False);
            Core2CombatStartParityTests.Same("start", Core2CombatStartParityTests.Project(alone), Core2CombatStartParityTests.Project(session.State));
            // The fight's draws continue the run's streams: starting from counters the run holds, the streams go on from them.
            var held = Run(record.Value<string>("classId"), seed, record);
            held["streamCounters"] = new JObject { ["shuffle"] = 7, ["enemyAI"] = 3 };
            var continued = Core2CombatSession.Start(Data, held, record.Value<string>("encounterId"));
            Assert.That(continued.State.Rng.AllCounters()["shuffle"], Is.GreaterThan(7u), "the shuffle went on from the run's counter");
            Assert.That(continued.State.Rng.AllCounters()["enemyAI"], Is.GreaterThanOrEqualTo(3u));
            Assert.That(session.Checkpoint()["rng"], Is.Not.Null);
        }

        [Test]
        public void TheRecordedFightPlaysThroughTheSessionAndAnIntentTheRulesRefuseCostsNothing()
        {
            var (record, _) = PausedFight();
            var session = StartRecorded(record, out _);
            JToken want = record["start"]["state"].DeepClone();
            var n = 0;
            foreach (var step in ((JArray)record["steps"]).OfType<JObject>())
            {
                var before = session.StateHash();
                var outcome = Play(session, step);
                var at = "step " + n++;
                if (step["error"] != null)
                {
                    Assert.That(outcome.Accepted, Is.False, at + " the web refused this intent");
                    Assert.That(outcome.Refusal.Message, Is.Not.Null.And.Not.Empty);
                    continue;
                }
                Assert.That(outcome.Accepted, Is.True, at);
                want = Core2CombatStartParityTests.Apply(want, step["delta"]);
                Core2CombatStartParityTests.Same(at, want, Core2CombatStartParityTests.Project(session.State));
                Assert.That(string.Join(" ", outcome.Events.Select(e => e.Value<string>("type"))), Is.EqualTo(string.Join(" ", step["events"].Select(t => t.Value<string>()))), at + " events");
                Assert.That(outcome.Offer == null, Is.EqualTo(step["offer"] == null || step["offer"].Type == JTokenType.Null), at + " offer");
            }
            // An intent the rules refuse changes nothing and says why; the fight (over or not) answers the same way.
            var hash = session.StateHash();
            var refused = session.Execute(Intent("playCard", "cardInstanceId", "no-such-card"));
            Assert.That(refused.Accepted, Is.False);
            Assert.That(refused.Refusal.Message, Is.Not.Empty);
            Assert.That(session.StateHash(), Is.EqualTo(hash));
        }

        [Test]
        public void AFightPausedOnAnOfferIsSavedReloadedAndFinishedOnTheSameState()
        {
            var (record, pause) = PausedFight();
            var steps = ((JArray)record["steps"]).OfType<JObject>().ToList();
            var live = StartRecorded(record, out var run);
            for (var i = 0; i <= pause; i++) if (steps[i]["error"] == null) Assert.That(Play(live, steps[i]).Accepted, Is.True, "step " + i);
            Assert.That(live.PendingOffer(), Is.Not.Null, "the fight is paused on an offer");

            const string slot = "slot1";
            live.Save(_saves, slot, "content-hash", run);
            var loaded = Core2CombatSession.Load(Data, _saves, slot);
            Assert.That(loaded.Status, Is.EqualTo(Core2LoadStatus.Loaded), string.Join("; ", loaded.Warnings));
            var resumed = loaded.Session;
            Assert.That(resumed.StateHash(), Is.EqualTo(live.StateHash()), "the reload is the saved state");
            Assert.That(JToken.DeepEquals(resumed.PendingOffer(), live.PendingOffer()), Is.True, "it resumes at the offer");
            Assert.That(loaded.Run.Value<string>("classId"), Is.EqualTo(run.Value<string>("classId")));
            Assert.That(resumed.LegalIntents().Count, Is.EqualTo(live.LegalIntents().Count));

            // The rest of the recorded fight plays on from both and ends the same.
            for (var i = pause + 1; i < steps.Count; i++)
            {
                if (steps[i]["error"] != null) continue;
                Assert.That(Play(live, steps[i]).Accepted, Is.True, "live step " + i);
                Assert.That(Play(resumed, steps[i]).Accepted, Is.True, "resumed step " + i);
                Assert.That(resumed.StateHash(), Is.EqualTo(live.StateHash()), "step " + i);
            }
            Assert.That(resumed.IsOver, Is.EqualTo(live.IsOver));
        }

        [Test]
        public void TheLogOfASaveReplaysOnTopOfItsCheckpointAndAMismatchStopsAtTheLastVerifiedState()
        {
            var (record, pause) = PausedFight();
            var steps = ((JArray)record["steps"]).OfType<JObject>().ToList();
            var live = StartRecorded(record, out var run);
            const string slot = "slot2";
            live.Save(_saves, slot, "content-hash", run);
            var logged = 0;
            for (var i = 0; i <= pause + 1 && i < steps.Count; i++)
            {
                if (steps[i]["error"] != null) continue;
                foreach (var id in (steps[i]["staggered"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                    live.State.Enemies.First(e => e.Value<string>("id") == id)["skipNextTurn"] = true;
                // The staggered flag is part of the state the step ran on, so a logged step carries it as the state it is applied to.
                if (steps[i]["staggered"] != null) { live.Save(_saves, slot, "content-hash", run); logged = 0; }
                Assert.That(live.ExecuteAndLog(_saves, slot, (JObject)steps[i]["intent"]).Accepted, Is.True);
                logged++;
            }
            Assert.That(live.LoggedCommands, Is.EqualTo(logged));
            var loaded = Core2CombatSession.Load(Data, _saves, slot);
            Assert.That(loaded.Status, Is.EqualTo(Core2LoadStatus.Loaded));
            Assert.That(loaded.LogDiverged, Is.False);
            Assert.That(loaded.Session.StateHash(), Is.EqualTo(live.StateHash()), "checkpoint plus log is the live state");
            Assert.That(loaded.Session.LoggedCommands, Is.EqualTo(logged));
        }

        [Test]
        public void ASaveTheBuildCannotReadIsRefusedByNameAndKept()
        {
            var (record, pause) = PausedFight();
            var live = StartRecorded(record, out var run);
            var payload = live.Payload(run);

            const string slot = "slot3";
            _saves.Checkpoint(slot, WithFormat(payload, 2), "c", live.StateHash());
            Assert.That(Core2CombatSession.Load(Data, _saves, slot).Status, Is.EqualTo(Core2LoadStatus.FormatMismatch), "the 405 save format is not a Core 2 save");

            var pinned = (JObject)payload.DeepClone();
            pinned["gameProfile"]["hash"] = "sha256:other";
            _saves.Checkpoint(slot, pinned, "c", live.StateHash());
            var mismatch = Core2CombatSession.Load(Data, _saves, slot);
            Assert.That(mismatch.Status, Is.EqualTo(Core2LoadStatus.ProfileMismatch), "a fight resumes under the profile it pinned (D-171)");
            Assert.That(_saves.Exists(slot), Is.True, "the slot is kept as it is");

            var damaged = (JObject)payload.DeepClone();
            damaged["combat"]["snapshot"]["turn"] = 0;
            _saves.Checkpoint(slot, damaged, "c", live.StateHash());
            var refused = Core2CombatSession.Load(Data, _saves, slot);
            Assert.That(refused.Status, Is.EqualTo(Core2LoadStatus.Damaged));
            Assert.That(string.Join(" ", refused.Warnings), Does.Contain("turn"));

            Assert.That(Core2CombatSession.Load(Data, _saves, "no-such-slot").Status, Is.EqualTo(Core2LoadStatus.Empty));
            Assert.That(pause, Is.GreaterThanOrEqualTo(0));
        }

        private static JObject WithFormat(JObject payload, int format)
        {
            var copy = (JObject)payload.DeepClone();
            copy["formatVersion"] = format;
            return copy;
        }

        [Test]
        public void ThePreviewsAndTheLegalIntentsAreWhatAScreenAsks()
        {
            var (record, pause) = PausedFight();
            var session = StartRecorded(record, out _);
            var legal = session.LegalIntents();
            Assert.That(legal.Any(i => i.Value<string>("type") == "endTurn"), Is.True);
            var card = legal.First(i => i.Value<string>("type") == "playCard");
            var instanceId = card.Value<string>("cardInstanceId");
            Assert.That(session.PreviewCard(instanceId, card.Value<string>("targetId")), Is.Not.Null);
            Assert.That(session.CardCosts(instanceId), Is.Not.Null);
            Assert.That(session.PlayRefusal(instanceId), Is.Null, "a legal play has no refusal");
            var enemy = session.State.Enemies.First();
            Assert.That(session.PreviewEnemy(enemy.Value<string>("id")), Is.Not.Null);
            foreach (var predict in legal.Where(i => i.Value<string>("type") == "predictIntent")) Assert.That(session.Execute(predict).Accepted, Is.True, "a predicted telegraph is accepted");
            // Probing the legal intents drew nothing from the live streams.
            var hash = session.StateHash();
            session.LegalIntents();
            Assert.That(session.StateHash(), Is.EqualTo(hash));

            // Paused on an offer, the legal intents are its options and Back.
            var steps = ((JArray)record["steps"]).OfType<JObject>().ToList();
            for (var i = 0; i <= pause; i++) if (steps[i]["error"] == null) Play(session, steps[i]);
            var offer = session.PendingOffer();
            var answers = session.LegalIntents();
            Assert.That(answers.All(a => a.Value<string>("type") == "chooseReaction"), Is.True);
            Assert.That(answers.Count, Is.EqualTo(((JArray)offer["options"]).Count + 1), "every option, and Back");
            Assert.That(answers.Last()["optionId"].Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void AFightPlayedToItsEndRefusesWhatComesAfter()
        {
            var (record, _) = PausedFight();
            var session = StartRecorded(record, out _);
            var guard = 0;
            while (!session.IsOver && guard++ < 400)
            {
                var legal = session.LegalIntents();
                var intent = legal.FirstOrDefault(i => i.Value<string>("type") == "chooseReaction") ?? legal.FirstOrDefault(i => i.Value<string>("type") == "playCard" && i["targetId"] != null)
                    ?? legal.FirstOrDefault(i => i.Value<string>("type") == "endTurn") ?? legal.First();
                Assert.That(session.Execute(intent).Accepted, Is.True, "a legal intent is accepted");
            }
            Assert.That(session.IsOver, Is.True, "the fight ended within the guard");
            Assert.That(session.State.Result, Is.EqualTo("victory").Or.EqualTo("defeat"));
            Assert.That(session.Execute(Intent("endTurn", "discardIds", new string[0])).Accepted, Is.False);
            Assert.That(session.LegalIntents(), Is.Empty);
            // A finished fight is not a resume point.
            Assert.Throws<InvalidOperationException>(() => session.Save(_saves, "slot4", "content-hash"));
            Assert.That(_saves.Exists("slot4"), Is.False);
        }

        // ------------------------------------------------------------------ legal intents: routes and recoveries

        private static Core2CombatSession ReaverFight() => Core2CombatSession.Start(Data, Run("reaver", 1121046183), "loneSoldier");

        /// <summary>The first control status the rules can recover by breaking it (a lock), by its id.</summary>
        private static string RecoverableStatus(Core2CombatSession session) =>
            CoreStatusControl.RecoveryControls(session.State, session.State.Player).OfType<JObject>().Select(row => row.Value<string>("status")).FirstOrDefault();

        [Test]
        public void ARecoverableControlStatusOffersItsRecoveryWhenTheStaminaIsThere()
        {
            var session = ReaverFight();
            Assert.That(session.LegalIntents().Any(i => i.Value<string>("type") == "recoverControl"), Is.False, "nothing to recover at the start");
            var lockRow = CoreStatuses.ControlRows(session.State).First(row => row.Value<bool?>("lock") == true);
            var status = lockRow.Value<string>("activeStatus");
            session.State.Player.Obj("statuses")[status] = new JObject { ["stacks"] = 2 };
            var cost = lockRow.Value<double>("recoveryCost");
            session.State.Player["stamina"] = cost;
            var recoveries = session.LegalIntents().Where(i => i.Value<string>("type") == "recoverControl").ToList();
            Assert.That(recoveries.Count, Is.EqualTo(1));
            Assert.That(recoveries[0]["selections"].ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo(new JArray(new JObject { ["status"] = status, ["stacks"] = 1 }).ToString(Newtonsoft.Json.Formatting.None)));
            var hash = session.StateHash();
            session.LegalIntents();
            Assert.That(session.StateHash(), Is.EqualTo(hash), "probing recoveries changed nothing");
            Assert.That(session.Execute(recoveries[0]).Accepted, Is.True);
            Assert.That(session.State.Player.Obj("statuses").Obj(status)?.Value<double?>("stacks") ?? 0, Is.EqualTo(1));
            session.State.Player["stamina"] = cost - 1;
            Assert.That(session.LegalIntents().Any(i => i.Value<string>("type") == "recoverControl"), Is.False, "too little Stamina to pay for it");
        }

        /// <summary>A fight whose hand holds an aimed card that can be thrown for less than advancing costs, by scanning openings; the card, its target and the two prices.</summary>
        private static (Core2CombatSession Session, string Card, string Target, double Advance, double Throw) CheaperThrow()
        {
            foreach (var classId in new[] { "reaver", "rogue", "herald", "starseer" })
                for (uint seed = 1; seed <= 60; seed++)
                    foreach (var encounter in new[] { "loneSoldier" })
                    {
                        var session = Core2CombatSession.Start(Data, Run(classId, seed), encounter);
                        // The enemy one band beyond the card's reach: advancing costs Stamina for the approach, a throw reaches without it.
                        var engagements = session.State.Fields.Obj("rangeState")?.Obj("engagements");
                        if (engagements == null) continue;
                        foreach (var pair in engagements.Properties().ToList()) engagements[pair.Name] = 1;
                        foreach (var enemy in session.State.Enemies)
                            foreach (var card in session.State.Piles.Hand)
                            {
                                JObject range;
                                try { range = session.PreviewCard(card.Value<string>("instanceId"), enemy.Value<string>("id")); } catch (InvalidOperationException) { continue; }
                                var rows = (range?["routes"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                                var advance = rows.FirstOrDefault(r => r.Value<string>("id") == "advance");
                                var thrown = rows.FirstOrDefault(r => r.Value<string>("id") == "throw");
                                if (advance == null || thrown == null || thrown.Value<bool>("available") != true || advance.Value<bool>("available") != true) continue;
                                var advanceCost = advance["costs"].Value<double>("energy");
                                var throwCost = thrown["costs"].Value<double>("energy");
                                if (throwCost >= advanceCost) continue;
                                // The card must take this enemy as its target when it can be paid for.
                                session.State.Player["stamina"] = advanceCost;
                                session.State.Player["energy"] = advanceCost;
                                if (session.LegalIntents().Any(i => i.Value<string>("cardInstanceId") == card.Value<string>("instanceId") && i.Value<string>("targetId") == enemy.Value<string>("id") && i.Value<string>("rangeRoute") == "throw"))
                                    return (session, card.Value<string>("instanceId"), enemy.Value<string>("id"), advanceCost, throwCost);
                            }
                    }
            return (null, null, null, 0, 0);
        }

        [Test]
        public void ACardWithAThrowRouteOffersBothRoutesAndOnlyThrowWhenTheAdvanceIsUnaffordable()
        {
            var (session, cardId, targetId, advance, thrown) = CheaperThrow();
            Assert.That(session, Is.Not.Null, "some opening has a card that is cheaper to throw");
            // With Stamina for either, both routes play (the default advance carries no route).
            session.State.Player["stamina"] = advance;
            session.State.Player["energy"] = advance;
            var plays = session.LegalIntents().Where(i => i.Value<string>("cardInstanceId") == cardId && i.Value<string>("targetId") == targetId).ToList();
            Assert.That(plays.Any(i => i["rangeRoute"] == null), Is.True, "the default advance");
            Assert.That(plays.Any(i => i.Value<string>("rangeRoute") == "throw"), Is.True, "the throw");
            Assert.That(plays.Any(i => i.Value<string>("rangeRoute") == "advance"), Is.False, "advance is the default, not a second candidate");
            // Enough for the throw and not for the advance: only the throw plays.
            session.State.Player["stamina"] = thrown;
            session.State.Player["energy"] = thrown;
            Assert.That(advance, Is.GreaterThan(thrown));
            plays = session.LegalIntents().Where(i => i.Value<string>("cardInstanceId") == cardId && i.Value<string>("targetId") == targetId).ToList();
            Assert.That(plays.Select(i => i.Value<string>("rangeRoute")), Is.EqualTo(new[] { "throw" }));
            Assert.That(session.Execute(plays[0]).Accepted, Is.True, "the throw is accepted");
        }

        /// <summary>The recorded card-bot fights of the web's reference enumerator (<c>legalIntents</c> in Tools/oracle-core2.mjs), one case each.</summary>
        public static System.Collections.Generic.IEnumerable<TestCaseData> RecordedCardFights() =>
            Directory.GetFiles(Path.Combine(TestContent.OracleRoot, "core2", "combat"), "cards-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => new TestCaseData(f).SetName("Core2LegalIntents_" + Path.GetFileNameWithoutExtension(f)));

        /// <summary>
        /// The fixtures record how many legal intents the web enumerated before each step. This build's dispatch has no flask yet, so the web's useFlask
        /// candidates (at most the two charge kinds) are not enumerated: until a recorded flask is used, the lists differ by no more than those, and every other
        /// kind (cards with their targets, choices and routes, end of turn, reactions, predictions, recoveries) counts the same at every step.
        /// </summary>
        [TestCaseSource(nameof(RecordedCardFights))]
        public void TheLegalIntentsMatchTheWebsAtEveryRecordedStep(string file)
        {
            var record = (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(TestContent.OracleRoot, "core2", "combat", file)));
            var session = Core2CombatSession.Start(Data, Run(record.Value<string>("classId"), (uint)record["seed"].Value<long>()), record.Value<string>("encounterId"));
            const int flaskKinds = 2;
            var checkedSteps = 0;
            foreach (var step in ((JArray)record["steps"]).OfType<JObject>())
            {
                var intent = (JObject)step["intent"];
                if (intent.Value<string>("type") == "useFlask") break;
                Assert.That(step.Value<int>("legalCount") - session.LegalIntents().Count, Is.InRange(0, flaskKinds), file + " step " + checkedSteps);
                Assert.That(session.Execute(intent).Accepted, Is.EqualTo(step["error"] == null), file + " step " + checkedSteps);
                checkedSteps++;
            }
        }
    }
}
