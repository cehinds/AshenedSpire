using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// VP-9 and VP-12 (D-960 to D-966): the event screen's and the run end screen's logic over the sessions the screens use, held without the editor. A one-off event is the web's choice body (the
    /// narrative with its art, the responses with their "Cannot afford" tag, the status, Continue that waits for a response and says "Steel yourself" before a fight); a quest chain's step is spoken
    /// (beats, Back, Skip speech, the responses on the last beat, the response's result in the caption); a response that cannot be paid or that the profile refuses is refused at the control; only a
    /// binding response is held to confirm; the event is not kept by a save. The run end reads the web's game over: heading, identity, the four numbers from the run's tally, the classes the
    /// run opened, the final deck, and Run again begins a new climb of the same class. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2EventEndFlowTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        private static UiData _ui;

        private static RunContent Content => Core2HostParityTests.Content;
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);
        private static string BossEncounter => Content.Core2.Loop.Map.Encounters.All.First(e => e.Value<string>("pool") == "boss").Value<string>("id");

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-eventend-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession AtTheEvent(string eventId, double cinders = 20, uint seed = 2024, Action<JObject> edit = null)
        {
            var session = Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);
            var node = session.ReachableNodes()[0];
            session.EditRunForReview(r =>
            {
                r["mapGraph"]["nodes"][node]["type"] = "event";
                r["mapGraph"]["nodes"][node]["resolved"] = new JObject { ["kind"] = "event", ["eventId"] = eventId };
                r["cinders"] = cinders;
                edit?.Invoke(r);
            });
            Assert.That(session.Travel(node).Travelled, Is.True);
            return session;
        }

        private static JObject Intent(string type, params object[] pairs)
        {
            var intent = new JObject { ["type"] = type };
            for (var i = 0; i < pairs.Length; i += 2) intent[(string)pairs[i]] = pairs[i + 1] == null ? JValue.CreateNull() : JToken.FromObject(pairs[i + 1]);
            return intent;
        }

        private static void PlayTurn(Core2RunSession session)
        {
            var state = session.Combat.State;
            for (var guard = 0; guard < 40 && session.IsInCombat; guard++)
            {
                if (session.Combat.PendingOffer() is JObject offer) { session.Execute(Intent("chooseReaction", "offerId", offer["id"], "optionId", null)); continue; }
                if (session.Combat.PendingChoice() != null) { session.Execute(session.Combat.LegalIntents().First()); continue; }
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

        private Core2RunSession ADeath(uint seed = 31337)
        {
            var session = Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r => { r["hp"] = 1; r["maxHp"] = 1; });
            session.StartEncounter(session.EncounterId);
            FightToTheEnd(session);
            Assert.That(session.RunOver, Is.True);
            return session;
        }

        // ------------------------------------------------------------------ the one-off event

        [Test]
        public void TheOneOffEventIsTheChoiceBodyWithItsNarrativeItsResponsesAndAStatus()
        {
            var session = AtTheEvent("weepingPilgrim");
            var flow = new Core2EventFlow(session, Ui);
            Assert.That(flow.Open, Is.True);
            Assert.That(flow.IsDialogue, Is.False);
            Assert.That(flow.Eyebrow, Is.EqualTo("Event"));
            Assert.That(flow.Title, Is.EqualTo("Wayward Pilgrim"));
            Assert.That(flow.Status, Is.EqualTo("1 of 2 available"), "a price closes the first response for now");
            Assert.That(flow.Paragraphs, Has.Count.EqualTo(1));
            Assert.That(flow.Paragraphs[0], Does.StartWith("A pilgrim sits at the roadside"));
            Assert.That(flow.ArtId, Is.EqualTo("event.weepingPilgrim"), "the event's own painting");
            Assert.That(flow.SceneArtId, Is.EqualTo("scene.grave-dialogue-master"), "the band above the door");
            Assert.That(flow.ShowResponses, Is.True);
            Assert.That(flow.Responses.Select(r => r.Label), Is.EqualTo(new[] { "Give 50 cinders (gain a random relic)", "Refuse" }));
            Assert.That(flow.Responses[0].Affordable, Is.False);
            Assert.That(flow.Responses[0].Tag, Is.EqualTo("Cannot afford"));
            Assert.That(flow.Responses[0].Priced, Is.True);
            Assert.That(flow.Responses[1].Tag, Is.Empty);
            Assert.That(flow.ContinueText, Is.EqualTo("Continue"));
            Assert.That(flow.ContinueEnabled, Is.False);
            Assert.That(flow.ContinueReason, Is.EqualTo("Choose a response first."));
            Assert.That(flow.Resolved, Is.False);
        }

        [Test]
        public void AResponseThePurseCannotPayRefusesAtTheControlAndChangesNothing()
        {
            var session = AtTheEvent("weepingPilgrim");
            var flow = new Core2EventFlow(session, Ui);
            var step = flow.Choose("giveCinders");
            Assert.That(step.Done, Is.False);
            Assert.That(step.Refusal, Is.EqualTo("Cannot afford"));
            Assert.That(session.EventDone, Is.False);
            Assert.That(session.Run.Value<double>("cinders"), Is.EqualTo(20));
            Assert.That(flow.Resolved, Is.False);
            Assert.That(flow.Choose("nothing").Refusal, Is.EqualTo("This event cannot be shown."), "a response the event lacks is refused too");
            Assert.That(flow.Continue().Refusal, Is.EqualTo("Choose a response first."), "Continue is not a way out");
            Assert.That(session.EventId, Is.EqualTo("weepingPilgrim"));
            // With the cinders in hand the same response is taken.
            var rich = new Core2EventFlow(AtTheEvent("weepingPilgrim", 50, 2025), Ui);
            Assert.That(rich.Status, Is.EqualTo("Choose a response"), "every response is open");
            Assert.That(rich.Responses[0].Tag, Is.Empty);
        }

        [Test]
        public void AResponseTakenShowsItsResultOpensContinueAndContinueReturnsToTheMapSaved()
        {
            var session = AtTheEvent("goldenMoth");
            var flow = new Core2EventFlow(session, Ui);
            Assert.That(flow.Status, Is.EqualTo("Choose a response"));
            Assert.That(flow.Choose("gatherCinders").Done, Is.True);
            Assert.That(flow.Resolved, Is.True);
            Assert.That(flow.Status, Is.EqualTo("Resolved"));
            Assert.That(flow.ShowResponses, Is.False, "the responses give way to the result");
            Assert.That(flow.Responses, Is.Empty);
            Assert.That(flow.ResultText, Is.EqualTo("The cinders come away easily. The moth watches you with something like approval."));
            Assert.That(flow.ContinueEnabled, Is.True);
            Assert.That(flow.ContinueReason, Is.Empty);
            Assert.That(flow.ContinueText, Is.EqualTo("Continue"));
            Assert.That(flow.Choose("leave").Done, Is.True, "a second response changes nothing");
            Assert.That(session.Run.Value<double>("cinders"), Is.EqualTo(60), "the 40 cinders were paid once");
            Assert.That(session.Location, Is.EqualTo("event"), "the run stands at the event until Continue");
            var step = flow.Continue();
            Assert.That(step.Left, Is.True);
            Assert.That(session.EventId, Is.Null);
            Assert.That(session.Location, Is.EqualTo("map"));
            Assert.That(session.IsInCombat, Is.False);
            var resumed = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(resumed.Status, Is.EqualTo(Core2RunLoadStatus.Resumed), string.Join("; ", resumed.Warnings));
            Assert.That(resumed.Session.Run.Value<double>("cinders"), Is.EqualTo(60), "Continue saved the response");
        }

        [Test]
        public void AResponseThatStartsAFightSaysSteelYourselfAndContinueEntersItOnTheEventsNode()
        {
            var session = AtTheEvent("wyrmTrial");
            var flow = new Core2EventFlow(session, Ui);
            Assert.That(flow.Choose("enterRing").Done, Is.True);
            Assert.That(flow.ContinueText, Is.EqualTo("Steel yourself"));
            Assert.That(session.IsInCombat, Is.False, "the fight waits for Continue");
            var step = flow.Continue();
            Assert.That(step.Left, Is.True);
            Assert.That(session.IsInCombat, Is.True);
            Assert.That(session.Location, Is.EqualTo("combat"));
        }

        [Test]
        public void OnlyABindingResponseIsHeldToConfirmAndAPricedOneIsAskedAboutNothing()
        {
            var binding = 0;
            var plain = 0;
            foreach (var id in Content.Core2.Events.Events.All.Select(e => e.Value<string>("id")))
            {
                var session = AtTheEvent(id, 500, 2024);
                var flow = new Core2EventFlow(session, Ui);
                foreach (var response in flow.View.Responses.Where(r => r.Affordable))
                {
                    Assert.That(flow.NeedsConfirm(response.ChoiceId), Is.EqualTo(response.Binding), id + "/" + response.ChoiceId);
                    if (response.Binding) binding++;
                    else plain++;
                }
            }
            Assert.That(binding, Is.GreaterThan(0), "some response of some event binds (a curse, a lasting loss)");
            Assert.That(plain, Is.GreaterThan(binding));
        }

        [Test]
        public void TheMirrorsClassSwapIsRefusedInTheWebsWordsAndTheEventStaysOpen()
        {
            var session = AtTheEvent("turncoatMirror");
            var flow = new Core2EventFlow(session, Ui);
            var step = flow.Choose("stepThrough");
            Assert.That(step.Done, Is.False);
            Assert.That(step.Refusal, Is.EqualTo(Core2LoopMessages.ConfiguredClassFixed));
            Assert.That(flow.Resolved, Is.False, "the web leaves the screen open");
            Assert.That(flow.Choose("turnAway").Done, Is.True, "the other response is still there");
        }

        [Test]
        public void TheEventIsNotAMapNoticeAnyMoreAndItsLocationRoutesToItsScreen()
        {
            var session = AtTheEvent("goldenMoth");
            Assert.That(session.PendingPort, Is.Null, "the event is a screen of its own now (VP-9), not a map notice");
            Assert.That(session.Location, Is.EqualTo("event"));
            Assert.That(Content.Flow.Core2ScreenFor(session.Location), Is.EqualTo(ScreenIds.Core2Event));
            Assert.That(Ui.Screens.IsBuilt(ScreenIds.Core2Event), Is.True);
            var travel = session.Travel("nowhere");
            Assert.That(travel.Travelled, Is.False, "no travel past an open event");
        }

        [Test]
        public void ASaveStandingAtTheEventResumesAtTheMapAsTheWebReloads()
        {
            var session = AtTheEvent("goldenMoth");
            session.Save();
            var resumed = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(resumed.Status, Is.EqualTo(Core2RunLoadStatus.Resumed), string.Join("; ", resumed.Warnings));
            Assert.That(resumed.Session.EventId, Is.Null);
            Assert.That(resumed.Session.Location, Is.EqualTo("map"));
            Assert.That(new Core2EventFlow(resumed.Session, Ui).Open, Is.False);
        }

        // ------------------------------------------------------------------ the spoken step

        [Test]
        public void AQuestStepIsSpokenInBeatsWithTheResponsesOnTheLastOneAndTheResultInTheCaption()
        {
            var session = AtTheEvent("graveOfTheNameless");
            var flow = new Core2EventFlow(session, Ui);
            Assert.That(flow.IsDialogue, Is.True);
            Assert.That(flow.Title, Is.EqualTo("Grave of the Nameless"));
            Assert.That(flow.SpeakerName, Is.EqualTo("The Nameless"));
            Assert.That(flow.PlayerName, Is.EqualTo("You"));
            Assert.That(flow.Beats, Has.Count.EqualTo(2), "the text divides on the blank line");
            Assert.That(flow.Beat, Is.EqualTo(0));
            Assert.That(flow.Caption, Does.StartWith("A cairn of broken swords"));
            Assert.That(flow.ShowResponses, Is.False, "the responses come at the end");
            Assert.That(flow.BackEnabled, Is.False);
            Assert.That(flow.SkipEnabled, Is.True);
            Assert.That(flow.ContinueEnabled, Is.True);
            Assert.That(flow.BackLabel, Is.EqualTo("Back"));
            Assert.That(flow.SkipLabel, Is.EqualTo("Skip speech"));
            Assert.That(flow.Back().Done, Is.True);
            Assert.That(flow.Beat, Is.EqualTo(0), "Back on the first beat does nothing");
            Assert.That(flow.Continue().Done, Is.True);
            Assert.That(flow.Beat, Is.EqualTo(1));
            Assert.That(flow.OnLastBeat, Is.True);
            Assert.That(flow.Caption, Does.StartWith("The mound is quiet"));
            Assert.That(flow.ShowResponses, Is.True);
            Assert.That(flow.Responses.Select(r => r.Label), Is.EqualTo(new[] { "Dig for cinders (gain 90 cinders; the grave may wake)", "Pay respects (heal 15% max HP)", "Leave" }));
            Assert.That(flow.BackEnabled, Is.True);
            Assert.That(flow.SkipEnabled, Is.False);
            Assert.That(flow.ContinueEnabled, Is.False, "the responses are the way on");
            Assert.That(flow.ContinueReason, Is.EqualTo("Choose a reply first."));
            Assert.That(flow.Continue().Refusal, Is.EqualTo("Choose a reply first."));
            Assert.That(flow.Back().Done, Is.True);
            Assert.That(flow.Beat, Is.EqualTo(0));
            Assert.That(flow.ShowResponses, Is.False, "reviewing an earlier beat never offers the responses");
            Assert.That(flow.Skip().Done, Is.True);
            Assert.That(flow.Beat, Is.EqualTo(1));
            Assert.That(session.EventDone, Is.False, "moving between beats changes nothing in the run");
            Assert.That(flow.Choose("payRespects").Done, Is.True);
            Assert.That(flow.Resolved, Is.True);
            Assert.That(flow.CaptionIsPlayer, Is.True);
            Assert.That(flow.Caption, Is.EqualTo("You right a fallen blade and stand a while. When you leave, you are lighter than you came."));
            Assert.That(flow.Responses, Is.Empty);
            Assert.That(flow.BackEnabled, Is.False);
            Assert.That(flow.SkipEnabled, Is.False);
            Assert.That(flow.ContinueEnabled, Is.True);
            Assert.That(flow.Continue().Left, Is.True);
            Assert.That(session.Location, Is.EqualTo("map"));
        }

        [Test]
        public void ASpokenResponseThatCannotBePaidRefusesAtThePressLikeTheOneOffs()
        {
            var session = AtTheEvent("lastLantern", 0);
            var flow = new Core2EventFlow(session, Ui);
            Assert.That(flow.IsDialogue, Is.True);
            Assert.That(flow.Choose("leave").Done, Is.True, "off the last beat no response is offered: a press changes nothing");
            Assert.That(session.EventDone, Is.False);
            flow.Skip();
            var poor = flow.Responses.First(r => !r.Affordable);
            Assert.That(poor.ChoiceId, Is.EqualTo("buyOil"));
            Assert.That(flow.Choose("buyOil").Refusal, Is.EqualTo("Cannot afford"));
            Assert.That(flow.Resolved, Is.False);
            Assert.That(session.Run.Value<double>("cinders"), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ the run's tally

        [Test]
        public void TheTallyCountsWhatThePlayerDealtAndTookAndNamesTheLastBlow()
        {
            var enemies = new List<JObject> { new JObject { ["id"] = "e1", ["name"] = "Ash Warden" } };
            var tally = new Core2RunTally();
            tally.BeginFight();
            tally.Observe(new[]
            {
                new JObject { ["type"] = "damageDealt", ["sourceId"] = "player", ["targetId"] = "e1", ["amount"] = 9 },
                new JObject { ["type"] = "enemyMoveStarted", ["sourceId"] = "e1", ["moveId"] = "cleave" },
                new JObject { ["type"] = "damageDealt", ["sourceId"] = "e1", ["targetId"] = "player", ["amount"] = 14 },
                new JObject { ["type"] = "hpLost", ["targetId"] = "player", ["amount"] = 14, ["cause"] = "attack" },
            }, enemies);
            Assert.That(tally.DamageDealt, Is.EqualTo(9));
            Assert.That(tally.DamageTaken, Is.EqualTo(14));
            Assert.That(tally.LastHit.SourceName, Is.EqualTo("Ash Warden"));
            Assert.That(tally.LastHit.Move, Is.EqualTo("Cleave"));
            Assert.That(tally.LastHit.Amount, Is.EqualTo(14));
            // A blow with another cause names the cause (a status tick), and a bare effect names nobody.
            tally.Observe(new[] { new JObject { ["type"] = "hpLost", ["targetId"] = "player", ["amount"] = 2, ["cause"] = "proc:bleedingWound" } }, enemies);
            Assert.That(tally.LastHit.SourceName, Is.EqualTo("Bleeding Wound"));
            Assert.That(tally.LastHit.Move, Is.Null);
            tally.Observe(new[] { new JObject { ["type"] = "hpLost", ["targetId"] = "player", ["amount"] = 1, ["cause"] = "effect" } }, enemies);
            Assert.That(tally.LastHit.SourceName, Is.Null);
            Assert.That(tally.DamageTaken, Is.EqualTo(17));
            var again = Core2RunTally.FromJson(tally.ToJson());
            Assert.That(again.DamageDealt, Is.EqualTo(9));
            Assert.That(again.DamageTaken, Is.EqualTo(17));
            Assert.That(again.LastHit.Amount, Is.EqualTo(1));
            tally.BeginFight();
            Assert.That(tally.LastHit, Is.Null, "a new fight forgets the last blow");
            Assert.That(tally.DamageTaken, Is.EqualTo(17), "the run's numbers go on");
            Assert.That(Core2RunTally.Words("ashStrike"), Is.EqualTo("Ash Strike"));
            Assert.That(Core2RunTally.Words("cinder_fall"), Is.EqualTo("Cinder fall"));
        }

        [Test]
        public void TheSessionTalliesAFightAndKeepsTheTallyThroughASave()
        {
            var session = ADeath();
            Assert.That(session.Tally.DamageTaken, Is.GreaterThan(0), "the blow that killed it cost hit points");
            Assert.That(session.Tally.LastHit, Is.Not.Null);
            Assert.That(session.Tally.LastHit.SourceName, Is.Not.Empty);
            var record = (JObject)((JArray)ProfileStore.Load(_saves).Doc["results"]).Last;
            Assert.That(record.Value<double>("damageDealt"), Is.EqualTo(session.Tally.DamageDealt), "the history row carries the session's tally (D-1162)");
            Assert.That(record.Value<double>("damageTaken"), Is.EqualTo(session.Tally.DamageTaken));
            var mid = Core2RunSession.New(Content, _saves, 2, 99u, "reaver", "Aldric", () => Epoch);
            mid.Travel(mid.ReachableNodes()[0]);
            mid.EditRunForReview(r => { r["hp"] = 100000; r["maxHp"] = 100000; });
            mid.StartEncounter(mid.EncounterId);
            for (var i = 0; i < 3 && mid.IsInCombat; i++) PlayTurn(mid);
            mid.Save();
            var resumed = Core2RunSession.Load(Content, _saves, 2, () => Epoch);
            Assert.That(resumed.Status, Is.Not.EqualTo(Core2RunLoadStatus.Damaged), string.Join("; ", resumed.Warnings));
            Assert.That(resumed.Session.Tally.DamageDealt, Is.EqualTo(mid.Tally.DamageDealt));
            Assert.That(resumed.Session.Tally.DamageTaken, Is.EqualTo(mid.Tally.DamageTaken));
        }

        [Test]
        public void TheTallyCountsTheIntentsAppendedAfterTheCheckpoint()
        {
            var mid = Core2RunSession.New(Content, _saves, 2, 99u, "reaver", "Aldric", () => Epoch);
            mid.Travel(mid.ReachableNodes()[0]);
            mid.EditRunForReview(r => { r["hp"] = 100000; r["maxHp"] = 100000; });
            mid.StartEncounter(mid.EncounterId);
            mid.Save();
            var before = mid.Tally.DamageDealt;
            var state = mid.Combat.State;
            foreach (var card in state.Piles.Hand.ToList())
            {
                var intent = Intent("playCard", "cardInstanceId", card.Value<string>("instanceId"));
                intent["targetId"] = state.Enemies.First(e => e.Value<bool>("alive")).Value<string>("id");
                if (mid.Execute(intent).Accepted && mid.Tally.DamageDealt > before) break;
            }
            Assume.That(mid.IsInCombat && mid.Tally.DamageDealt > before, "a card dealt damage without ending the fight");
            Assume.That(mid.Combat.LoggedCommands, Is.GreaterThan(0), "the shipped run flow appends intents to the checkpoint's log");
            var resumed = Core2RunSession.Load(Content, _saves, 2, () => Epoch);
            Assert.That(resumed.Status, Is.EqualTo(Core2RunLoadStatus.Resumed), string.Join("; ", resumed.Warnings));
            Assert.That(resumed.Session.Tally.ToJson().ToString(), Is.EqualTo(mid.Tally.ToJson().ToString()), "the replayed intents are tallied once");
        }

        // ------------------------------------------------------------------ the run end

        [Test]
        public void ADefeatIsTheWebsGameOverWithTheBlowTheNumbersTheDeckAndTheClassItOpened()
        {
            var session = ADeath();
            var flow = new Core2RunEndFlow(session, Ui);
            Assert.That(flow.Victory, Is.False);
            Assert.That(flow.Eyebrow, Is.EqualTo("The climb ends"));
            Assert.That(flow.Title, Is.EqualTo("Defeat"));
            Assert.That(flow.Heading, Is.EqualTo("You perished"));
            Assert.That(flow.IdentityEyebrow, Is.EqualTo("Forsaken"));
            Assert.That(flow.Name, Is.EqualTo("Aldric"));
            Assert.That(flow.Line, Does.StartWith("Floor 1 / "));
            Assert.That(flow.Line, Does.EndWith("0 fights won"));
            Assert.That(flow.Meta, Is.EqualTo("Seed " + session.SeedText));
            Assert.That(flow.Cause, Does.StartWith("Slain by "));
            Assert.That(flow.Cause, Does.Contain(session.Tally.LastHit.SourceName));
            Assert.That(flow.Chips.Select(c => c.Key), Is.EqualTo(new[] { "Damage dealt", "Damage taken", "Cinders", "Final HP" }));
            Assert.That(flow.Chips[1].Value, Is.EqualTo(((int)session.Tally.DamageTaken).ToString()));
            Assert.That(flow.Chips[2].Value, Is.EqualTo("20"), "a Core 2 run starts with 20 cinders");
            Assert.That(flow.Chips[3].Value, Is.EqualTo("0 / 1"), "a death ends at no hit points");
            Assert.That(flow.EarnedHeading, Is.EqualTo("Earned"));
            Assert.That(flow.Earned, Has.Count.EqualTo(session.ClassesUnlocked.Count));
            Assert.That(flow.Earned, Is.Not.Empty, "a finished run opens the next class");
            Assert.That(flow.Earned[0].Kind, Is.EqualTo("class"));
            Assert.That(flow.Earned[0].Name, Is.Not.Empty);
            Assert.That(flow.DeckEyebrow, Is.EqualTo("Final deck"));
            Assert.That(flow.DeckTitle, Is.EqualTo(flow.Deck.Count + " cards"));
            Assert.That(flow.Deck.Select(c => c.Name), Does.Contain("Strike"));
            Assert.That(flow.AgainLabel, Is.EqualTo("Run again"));
            Assert.That(flow.HistoryLabel, Is.EqualTo("Run history"));
            Assert.That(flow.TitleLabel, Is.EqualTo("Return to title"));
        }

        [Test]
        public void AVictoryIsEmberRestoredWithNoBlowAndTheHitPointsItEndedWith()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 5152u, "reaver", "Aldric", () => Epoch);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r => { r["actNumber"] = Content.Core2.Loop.Acts; r["maxHp"] = 5000; r["hp"] = 5000; });
            session.StartEncounter(BossEncounter);
            foreach (var enemy in session.Combat.State.Enemies) enemy["hp"] = 1;
            FightToTheEnd(session);
            Assert.That(session.RunOver, Is.True);
            var flow = new Core2RunEndFlow(session, Ui);
            Assert.That(flow.Victory, Is.True);
            Assert.That(flow.Eyebrow, Is.EqualTo("The climb"));
            Assert.That(flow.Title, Is.EqualTo("Victory"));
            Assert.That(flow.Heading, Is.EqualTo("Ember restored"));
            Assert.That(flow.Cause, Is.Null);
            Assert.That(flow.Line, Does.EndWith("1 fight won"));
            Assert.That(flow.Chips[3].Value, Does.EndWith(" / 5000"));
            Assert.That(flow.Chips[3].Value, Does.Not.StartWith("0 /"));
            Assert.That(flow.Chips[0].Value, Is.Not.EqualTo("0"), "the player struck the keeper");
        }

        [Test]
        public void RunAgainBeginsANewClimbOfTheSameClassAndNameWithANewSeedInTheSameSlot()
        {
            var session = ADeath();
            var flow = new Core2RunEndFlow(session, Ui);
            var again = flow.RunAgain(Content, _saves, 777u);
            Assert.That(again.Done, Is.True, again.Refusal);
            Assert.That(again.Session.ClassId, Is.EqualTo("reaver"));
            Assert.That(again.Session.Name, Is.EqualTo("Aldric"));
            Assert.That(again.Session.Seed, Is.EqualTo(777u));
            Assert.That(again.Session.SeedText, Is.Not.EqualTo(session.SeedText));
            Assert.That(again.Session.Location, Is.EqualTo("map"));
            Assert.That(again.Session.SlotIndex, Is.EqualTo(1));
            Assert.That(_saves.Exists(again.Session.Slot), Is.True, "the new climb is saved in the slot");
            Assert.That(again.Session.RunOver, Is.False);
        }
    }
}
