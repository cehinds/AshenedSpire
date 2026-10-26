using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-15 (D-316 to D-324): a Core 2 fight leads to its rewards through the session the screens use. A won fight opens the reward door with a persisted offer
    /// (cinders paid, the skill and class tracks settled), the door resumes from a save, the player takes an option, adds a card to the deck or leaves; a boss's door
    /// is followed by the next act; the last boss and a death end the climb once their level notices are read; a refusal changes nothing. The numbers
    /// themselves are held to the web by Core2RewardsParityTests. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RewardsSessionTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        private static RunContent Content => Core2HostParityTests.Content;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-rewards-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession New(uint seed) => Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);

        private static JObject Intent(string type, params object[] pairs)
        {
            var o = new JObject { ["type"] = type };
            for (var i = 0; i + 1 < pairs.Length; i += 2) o[(string)pairs[i]] = JToken.FromObject(pairs[i + 1] ?? JValue.CreateNull());
            return o;
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

        /// <summary>A sturdy player wins the first fight of the climb; the reward door is open.</summary>
        private Core2RunSession AtTheDoor(uint seed)
        {
            var session = New(seed);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r => { r["maxHp"] = 5000; r["hp"] = 5000; });
            session.StartEncounter(session.EncounterId);
            FightToTheEnd(session);
            Assert.That(session.Combat.State.Result, Is.EqualTo("victory"));
            Assert.That(session.HasPendingReward, Is.True);
            return session;
        }

        private static string BossEncounter => Content.Core2.Loop.Map.Encounters.All.First(e => e.Value<string>("pool") == "boss").Value<string>("id");

        /// <summary>Starts the boss fight where the run stands, with the enemies nearly dead, and fights it to its end.</summary>
        private static void FightTheBoss(Core2RunSession session)
        {
            session.EditRunForReview(r => { r["maxHp"] = 5000; r["hp"] = 5000; });
            session.StartEncounter(BossEncounter);
            foreach (var enemy in session.Combat.State.Enemies) enemy["hp"] = 1;
            FightToTheEnd(session);
        }

        // ------------------------------------------------------------------ the door

        [Test]
        public void TheViewNamesTheOptionsAndWhatAddToDeckWouldMeet()
        {
            var session = AtTheDoor(777);
            var view = Core2RewardsView.Build(session);
            Assert.That(view.Source, Is.EqualTo("combat"));
            Assert.That(view.Options, Has.Count.EqualTo(3));
            Assert.That(view.TakeCount, Is.EqualTo(1));
            Assert.That(view.Cinders, Is.EqualTo(session.LastEnd.Receipt.Cinders));
            foreach (var option in view.Options)
            {
                Assert.That(option.Name, Is.Not.Empty);
                Assert.That(option.Claimable, Is.True);
                Assert.That(option.DeckRefusal, option.IsCard ? Is.Empty : Is.EqualTo("notCard"));
            }
            Assert.That(view.Notices.Count, Is.EqualTo(session.ProgressionNotices.Count));
        }

        [Test]
        public void ARefusedClaimChangesNothing()
        {
            var session = AtTheDoor(777);
            var before = session.StateHash();
            var refused = session.TakeReward(99);
            Assert.That(refused.Done, Is.False);
            Assert.That(refused.Refusal, Is.EqualTo("Invalid reward option"), "the web's own sentence");
            Assert.That(session.StateHash(), Is.EqualTo(before));
            var taken = session.TakeReward(0);
            Assert.That(taken.Done, Is.True, taken.Refusal);
            Assert.That(taken.Option.Value<string>("id"), Is.Not.Empty);
            var again = session.TakeReward(1);
            Assert.That(again.Refusal, Is.EqualTo("Reward offer is missing or already claimed"));
            var view = Core2RewardsView.Build(session);
            Assert.That(view.Options.Count(o => o.Taken), Is.EqualTo(1));
            Assert.That(view.Options.Where(o => !o.Taken).All(o => !o.Claimable), Is.True, "the offer is spent");
            var atDoor = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(atDoor.Session.StateHash(), Is.EqualTo(session.StateHash()), "the claim is saved");
        }

        [Test]
        public void ACardGoesStraightIntoTheDeckAndTheDoorResumesFromTheSave()
        {
            var seed = 777u;
            Core2RunSession session = null;
            JObject card = null;
            for (; seed < 800 && card == null; seed++)
            {
                session = AtTheDoor(seed);
                card = session.RewardOffer.Arr("options").OfType<JObject>().FirstOrDefault(o => o.Value<string>("kind") == "combatCards" || o.Value<string>("kind") == "weaponArts");
                if (card == null) _saves.Delete(session.Slot);
            }
            Assert.That(card, Is.Not.Null, "an offer within the first seeds holds a card");
            var offer = session.RewardOffer;
            var index = offer.Arr("options").IndexOf(offer.Arr("options").OfType<JObject>().First(o => o.Value<string>("id") == card.Value<string>("id") && o.Value<string>("kind") == card.Value<string>("kind")));
            var deckBefore = session.Run.Arr("deck").Count;
            Assert.That(session.RewardDeckRefusal(index), Is.Empty);
            var added = session.AddRewardToDeck(index);
            Assert.That(added.Done, Is.True, added.Refusal);
            var run = session.Run;
            Assert.That(run.Arr("deck").Count, Is.EqualTo(deckBefore + 1));
            var instance = "reward:" + offer.Value<string>("id") + ":" + index;
            Assert.That(run.Arr("deck").Any(c => c.Value<string>("instanceId") == instance), Is.True, "the card is in the deck under its claim id");
            Assert.That(run.Arr("sideboard").Any(c => c.Value<string>("instanceId") == instance), Is.False);
            Assert.That(session.AddRewardToDeck(index).Done, Is.True, "a repeat changes nothing");
            Assert.That(session.Run.Arr("deck").Count, Is.EqualTo(deckBefore + 1));
            var atDoor = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(atDoor.Session.StateHash(), Is.EqualTo(session.StateHash()));
            Assert.That(atDoor.Session.LeaveRewards().Done, Is.True);
            Assert.That(atDoor.Session.Location, Is.EqualTo("map"));
        }

        [Test]
        public void ADeckAtItsLimitRefusesAndTheCardStaysOffered()
        {
            Core2RunSession session = null;
            var index = -1;
            for (var seed = 777u; seed < 800 && index < 0; seed++)
            {
                session = AtTheDoor(seed);
                index = session.RewardOffer.Arr("options").OfType<JObject>().ToList().FindIndex(o => o.Value<string>("kind") == "combatCards" || o.Value<string>("kind") == "weaponArts");
                if (index < 0) _saves.Delete(session.Slot);
            }
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            var deck = session.Run.Arr("deck").Count;
            session.Profile.Doc["settings"] = new JObject { ["deckMaxSize"] = deck, ["deckMaxUnlimited"] = false };
            Assert.That(session.RewardDeckRefusal(index), Is.EqualTo("full"));
            var before = session.StateHash();
            var refused = session.AddRewardToDeck(index);
            Assert.That(refused.Done, Is.False);
            Assert.That(refused.Code, Is.EqualTo("full"));
            Assert.That(refused.Refusal, Is.EqualTo("Your deck is full (" + deck + " cards)"));
            Assert.That(session.StateHash(), Is.EqualTo(before), "neither the claim nor the deck moved");
            Assert.That(session.TakeReward(index).Done, Is.True, "the card can still be taken to the collection");
        }

        [Test]
        public void ADoorDismissedFromTheMapLeavesTheSpoils()
        {
            var session = AtTheDoor(777);
            Assert.That(session.PendingPort, Is.Null, "the door is a screen of its own now (VP-7), not a map notice");
            Assert.That(session.Location, Is.EqualTo("rewards"));
            session.DismissPort();
            Assert.That(session.HasPendingReward, Is.False);
            Assert.That(session.Location, Is.EqualTo("map"));
            Assert.That(session.ReachableNodes(), Is.Not.Empty);
        }

        // ------------------------------------------------------------------ the end of an act and of the climb

        [Test]
        public void ABossOpensTheDoorAndTheActAdvancesWhenItIsLeft()
        {
            var session = New(5150);
            session.Travel(session.ReachableNodes()[0]);
            FightTheBoss(session);
            Assert.That(session.Combat.State.Result, Is.EqualTo("victory"));
            Assert.That(session.HasPendingReward, Is.True);
            Assert.That(session.PendingReward.Value<string>("after"), Is.EqualTo("advanceAct"));
            Assert.That(session.Act, Is.EqualTo(1), "the act waits for the player to leave the spoils");
            Assert.That(session.Run["bossesBeaten"], Is.Not.Empty);
            Assert.That(session.LastEnd.Receipt.Cinders, Is.GreaterThanOrEqualTo(200), "a boss pays the most");
            Assert.That(session.LeaveRewards().Done, Is.True);
            Assert.That(session.Act, Is.EqualTo(2));
            Assert.That(session.LastEnd.ActAdvanced, Is.True);
            Assert.That(session.Location, Is.EqualTo("map"));
            Assert.That(session.Run.Value<double>("hp"), Is.EqualTo(session.Run.Value<double>("maxHp")), "the act advance heals");
            Assert.That(session.ReachableNodes(), Is.Not.Empty, "the next act's map");
        }

        [Test]
        public void ANoticeReadWhoseProfileSaveFailsLeavesTheRunOpenForTheRetry()
        {
            var session = New(5151);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r =>
            {
                r["actNumber"] = Content.Core2.Loop.Acts;
                r["skills"] = new JObject { ["class:reaver"] = new JObject { ["xp"] = 399, ["level"] = 0, ["pendingDrafts"] = 0 } };
            });
            FightTheBoss(session);
            Assert.That(session.NoticeOnlyDoor, Is.True);
            // The profile's temp file is a directory: the close-out's profile write fails, as a full disk would.
            var blocker = Path.Combine(_dir, _saves.Rules.ProfileSlot + ".json.tmp");
            Directory.CreateDirectory(blocker);
            Assert.That(() => session.AcknowledgeProgression(), Throws.Exception);
            Assert.That(session.RunOver, Is.False, "the rolled-back close-out is not over, so the run end does not open");
            Assert.That(session.End, Is.Null);
            Assert.That(session.ProgressionNotices, Has.Count.EqualTo(1), "the notice is still unread");
            Directory.Delete(blocker);
            Assert.That(session.AcknowledgeProgression().Done, Is.True);
            Assert.That(session.RunOver, Is.True);
            Assert.That(_saves.Exists(session.Slot), Is.False);
        }

        [Test]
        public void TheLastBossEndsTheClimbOnceItsLevelNoticesAreRead()
        {
            var session = New(5151);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r =>
            {
                r["actNumber"] = Content.Core2.Loop.Acts;
                // The class track a few points short of its next level: the boss's win crosses it.
                r["skills"] = new JObject { ["class:reaver"] = new JObject { ["xp"] = 399, ["level"] = 0, ["pendingDrafts"] = 0 } };
            });
            FightTheBoss(session);
            Assert.That(session.Combat.State.Result, Is.EqualTo("victory"));
            Assert.That(session.RunOver, Is.False, "the notice is read first");
            Assert.That(session.HasPendingReward, Is.True);
            Assert.That(session.NoticeOnlyDoor, Is.True, "the last boss's door has no offer");
            Assert.That(session.RewardOffer, Is.Null);
            var notices = session.ProgressionNotices;
            Assert.That(notices, Has.Count.EqualTo(1));
            Assert.That(notices[0].Value<string>("skillId"), Is.EqualTo("class:reaver"));
            Assert.That(notices[0].Value<string>("label"), Is.Not.Empty);
            Assert.That(notices[0].Value<double>("after"), Is.EqualTo(notices[0].Value<double>("before") + 1));
            var atDoor = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(atDoor.Session.ProgressionNotices, Has.Count.EqualTo(1), "an unread notice survives a reload");
            Assert.That(session.AcknowledgeProgression().Done, Is.True);
            Assert.That(session.RunOver, Is.True);
            Assert.That(session.End.Victory, Is.True);
            Assert.That(_saves.Exists(session.Slot), Is.False);
        }

        [Test]
        public void TheLastBossEndsTheClimbAtOnceWhenThereIsNothingToRead()
        {
            var session = New(5152);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r => r["actNumber"] = Content.Core2.Loop.Acts);
            FightTheBoss(session);
            Assert.That(session.Combat.State.Result, Is.EqualTo("victory"));
            Assert.That(session.RunOver, Is.True);
            Assert.That(session.End.Victory, Is.True);
        }

        [Test]
        public void ADeathPaysItsSkillXpAndEndsTheClimb()
        {
            var session = New(31337);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r => { r["hp"] = 1; r["maxHp"] = 1; });
            session.StartEncounter(session.EncounterId);
            FightToTheEnd(session);
            Assert.That(session.Combat.State.Result, Is.EqualTo("defeat"));
            Assert.That(session.LastEnd.Receipt.ClassAward, Is.Null, "a lost fight pays no class XP");
            Assert.That(session.RunOver, Is.True, "no notice to read, so the climb ends at once");
            Assert.That(session.LastEnd.Receipt.Cinders, Is.EqualTo(0));
        }
    }
}
