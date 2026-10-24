using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// VP-7 (D-850): the spoils screen's logic over a won fight's reward door, held without the editor. The level notices come first and Continue reads them; the offer lists its options with the
    /// web's words and chips; Confirm reviews the picked option; a card is added to the deck (or kept in the collection) and anything else is taken, each leaving the door; a deck at its limit
    /// refuses Add to deck in the web's words and changes nothing; Skip leaves the spoils; a saved door resumes on the same screen; the screen is in the registry and a fight leads to it.
    /// Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RewardsFlowTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        private static UiData _ui;

        private static RunContent Content => Core2HostParityTests.Content;
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-spoils-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

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

        /// <summary>A sturdy player wins the first fight of the climb (with the class track a point short of a level when <paramref name="levelUp"/>); the reward door is open.</summary>
        private Core2RunSession AtTheDoor(uint seed, bool levelUp = false)
        {
            var session = Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r =>
            {
                r["maxHp"] = 5000;
                r["hp"] = 5000;
                if (levelUp) r["skills"] = new JObject { ["class:reaver"] = new JObject { ["xp"] = 399, ["level"] = 0, ["pendingDrafts"] = 0 } };
            });
            session.StartEncounter(session.EncounterId);
            for (var guard = 0; guard < 400 && session.IsInCombat; guard++) PlayTurn(session);
            Assert.That(session.IsInCombat, Is.False, "the fight ended");
            Assert.That(session.Combat.State.Result, Is.EqualTo("victory"));
            Assert.That(session.HasPendingReward, Is.True);
            return session;
        }

        /// <summary>The first door within the first seeds whose offer holds an option matching <paramref name="wanted"/> (a card, or not a card).</summary>
        private Core2RunSession AtADoorWith(bool card, out int index)
        {
            for (var seed = 777u; seed < 830; seed++)
            {
                var session = AtTheDoor(seed);
                var options = session.RewardOffer.Arr("options").OfType<JObject>().ToList();
                index = options.FindIndex(o => (o.Value<string>("kind") == "combatCards" || o.Value<string>("kind") == "weaponArts") == card);
                if (index >= 0) return session;
                _saves.Delete(session.Slot);
            }
            index = -1;
            Assert.Fail("no offer within the first seeds holds the option wanted");
            return null;
        }

        private Core2SpoilsFlow Flow(Core2RunSession session) => new Core2SpoilsFlow(session, Ui);

        // ------------------------------------------------------------------ the offer

        [Test]
        public void TheOfferListsEveryOptionWithItsKindNameChipAndWords()
        {
            var session = AtTheDoor(777);
            var flow = Flow(session);
            Assert.That(flow.Stage, Is.EqualTo(Core2SpoilsStage.Offer), "no level was crossed, so no notice comes first");
            Assert.That(flow.Eyebrow, Is.EqualTo("Claim your spoils"));
            Assert.That(flow.Title, Is.EqualTo("Victory"));
            Assert.That(flow.Status, Is.EqualTo("0 of 1 claimed"));
            Assert.That(flow.Note, Is.EqualTo("Choose 1. Add the card to your deck or keep it in your collection."));
            Assert.That(flow.Options, Has.Count.EqualTo(3));
            foreach (var option in flow.Options)
            {
                Assert.That(option.Name, Is.Not.Empty);
                Assert.That(option.KindLabel, Is.Not.Empty.And.Not.Contain("spoils.kind"), option.Kind);
                Assert.That(option.Chip, Is.EqualTo(Core2SpoilsChip.New));
                Assert.That(option.ChipText, Is.EqualTo("NEW"));
                if (option.IsCard)
                {
                    Assert.That(option.Card, Is.Not.Null);
                    Assert.That(option.Card.Text, Is.Not.Empty, option.Name + " words");
                    Assert.That(option.Card.Face, Is.Not.Null);
                }
            }
            Assert.That(flow.CindersTitle, Is.EqualTo(session.LastEnd.Receipt.Cinders + " cinders"));
            Assert.That(flow.CindersBody, Does.StartWith("Granted"));
            Assert.That(flow.ConfirmLabel, Is.EqualTo("Confirm"));
            Assert.That(flow.BackLabel, Is.EqualTo("Skip"));
        }

        [Test]
        public void ConfirmNeedsAPickAndReviewShowsOnlyThePickedOptionWithBackToReturn()
        {
            var flow = Flow(AtTheDoor(777));
            var none = flow.Confirm();
            Assert.That(none.Done, Is.False);
            Assert.That(none.Refusal, Is.EqualTo("Select one first."), "refused at the control with the same words the other reward screens use");
            Assert.That(flow.Select(1).Done, Is.True);
            Assert.That(flow.Selected, Is.EqualTo(1));
            Assert.That(flow.Confirm().Done, Is.True);
            Assert.That(flow.Stage, Is.EqualTo(Core2SpoilsStage.Review));
            Assert.That(flow.Options.Select(o => o.Index), Is.EqualTo(new[] { 1 }));
            Assert.That(flow.BackLabel, Is.EqualTo("Back"));
            Assert.That(flow.Back().Done, Is.True);
            Assert.That(flow.Stage, Is.EqualTo(Core2SpoilsStage.Offer));
            Assert.That(flow.Options, Has.Count.EqualTo(3));
            Assert.That(flow.Selected, Is.EqualTo(1), "the pick is kept");
        }

        // ------------------------------------------------------------------ taking

        [Test]
        public void ACardIsAddedToTheDeckAndTheDoorIsLeft()
        {
            var session = AtADoorWith(true, out var index);
            var deck = session.Run.Arr("deck").Count;
            var flow = Flow(session);
            flow.Select(index);
            flow.Confirm();
            Assert.That(flow.ConfirmLabel, Is.EqualTo("Add to deck"));
            Assert.That(flow.KeepLabel, Is.EqualTo("Keep in collection"));
            Assert.That(flow.ConfirmRefusal, Is.Empty);
            var added = flow.Confirm();
            Assert.That(added.Done, Is.True, added.Refusal);
            Assert.That(added.Left, Is.True, "a spent offer is left by itself, as the web's proceed() does");
            Assert.That(session.HasPendingReward, Is.False);
            Assert.That(session.Location, Is.EqualTo("map"));
            Assert.That(session.Run.Arr("deck").Count, Is.EqualTo(deck + 1));
        }

        [Test]
        public void ACardKeptInTheCollectionStaysOutOfTheDeck()
        {
            var session = AtADoorWith(true, out var index);
            var deck = session.Run.Arr("deck").Count;
            var sideboard = session.Run.Arr("sideboard").Count;
            var flow = Flow(session);
            flow.Select(index);
            flow.Confirm();
            var kept = flow.Keep();
            Assert.That(kept.Done && kept.Left, Is.True, kept.Refusal);
            Assert.That(session.Run.Arr("deck").Count, Is.EqualTo(deck));
            Assert.That(session.Run.Arr("sideboard").Count, Is.EqualTo(sideboard + 1));
        }

        [Test]
        public void AnythingButACardIsTakenByContinue()
        {
            var session = AtADoorWith(false, out var index);
            var flow = Flow(session);
            var option = flow.Options[index];
            Assert.That(option.IsCard, Is.False);
            flow.Select(index);
            flow.Confirm();
            Assert.That(flow.ConfirmLabel, Is.EqualTo("Continue"));
            Assert.That(flow.KeepLabel, Is.Empty, "only a card has two ways in");
            var taken = flow.Confirm();
            Assert.That(taken.Done, Is.True, taken.Refusal);
            Assert.That(taken.Left, Is.True);
            Assert.That(session.HasPendingReward, Is.False);
        }

        [Test]
        public void ADeckAtItsLimitRefusesAddToDeckInTheWebsWordsAndTheCardCanStillBeKept()
        {
            var session = AtADoorWith(true, out var index);
            var deck = session.Run.Arr("deck").Count;
            session.Profile.Doc["settings"] = new JObject { ["deckMaxSize"] = deck, ["deckMaxUnlimited"] = false };
            var flow = Flow(session);
            flow.Select(index);
            flow.Confirm();
            var words = "Your deck is full (" + deck + " cards). Edit your deck to make room.";
            Assert.That(flow.ConfirmRefusal, Is.EqualTo(words), "shown at the control and in the status line");
            Assert.That(flow.StatusLine, Is.EqualTo(words));
            var before = session.StateHash();
            var refused = flow.Confirm();
            Assert.That(refused.Done, Is.False);
            Assert.That(refused.Refusal, Is.EqualTo(words));
            Assert.That(session.StateHash(), Is.EqualTo(before), "nothing moved");
            Assert.That(flow.Stage, Is.EqualTo(Core2SpoilsStage.Review));
            Assert.That(flow.Keep().Done, Is.True);
            Assert.That(session.Run.Arr("deck").Count, Is.EqualTo(deck));
        }

        [Test]
        public void SkipLeavesTheSpoilsWhereTheyLie()
        {
            var session = AtTheDoor(777);
            var flow = Flow(session);
            var before = session.Run.Arr("sideboard").Count;
            var left = flow.Skip();
            Assert.That(left.Done && left.Left, Is.True);
            Assert.That(session.Location, Is.EqualTo("map"));
            Assert.That(session.Run.Arr("sideboard").Count, Is.EqualTo(before));
        }

        [Test]
        public void WithMoreThanOneTakeAnOptionTakenWearsTakenAndRefusesBeingPickedAgain()
        {
            var session = AtTheDoor(777);
            var id = session.PendingReward.Value<string>("offerId");
            session.EditRunForReview(r => r["gameProfileRewardOffers"][id]["takeCount"] = 2);
            Assert.That(session.TakeReward(0).Done, Is.True);
            var flow = Flow(session);
            Assert.That(flow.Stage, Is.EqualTo(Core2SpoilsStage.Offer), "the door stands while a take is left");
            Assert.That(flow.Status, Is.EqualTo("1 of 2 claimed"));
            Assert.That(flow.Options[0].Chip, Is.EqualTo(Core2SpoilsChip.Taken));
            Assert.That(flow.Options[0].ChipText, Is.EqualTo("TAKEN"));
            Assert.That(flow.Options[1].Chip, Is.EqualTo(Core2SpoilsChip.New));
            var again = flow.Select(0);
            Assert.That(again.Done, Is.False);
            Assert.That(again.Refusal, Is.EqualTo("Already taken."));
            Assert.That(flow.Select(1).Done, Is.True);
        }

        // ------------------------------------------------------------------ the notices

        [Test]
        public void TheLevelNoticesComeFirstAndContinueReadsThemOnce()
        {
            var session = AtTheDoor(777, true);
            var flow = Flow(session);
            Assert.That(flow.Stage, Is.EqualTo(Core2SpoilsStage.Notices));
            Assert.That(flow.Eyebrow, Is.EqualTo("Level Up"));
            Assert.That(flow.Title, Is.EqualTo("Level up"));
            Assert.That(flow.ProgressionHeading, Is.EqualTo("Progression"));
            Assert.That(flow.Notices, Is.Not.Empty);
            Assert.That(flow.Notices.Any(n => n.Sentence.Contains(" · Level ") && n.Sentence.Contains(" → ")), Is.True, "the web's sentence: label · Level before → after");
            Assert.That(flow.Options, Is.Empty);
            Assert.That(flow.ConfirmLabel, Is.EqualTo("Continue"));
            var read = flow.Confirm();
            Assert.That(read.Done, Is.True);
            Assert.That(read.Left, Is.False, "the offer is still to come");
            Assert.That(flow.Stage, Is.EqualTo(Core2SpoilsStage.Offer));
            Assert.That(flow.Notices, Is.Empty);
            Assert.That(session.ProgressionNotices, Is.Empty);
        }

        [Test]
        public void TheLastBossesNoticesEndTheClimbWhenRead()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 5151, "reaver", "Aldric", () => Epoch);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r =>
            {
                r["maxHp"] = 5000;
                r["hp"] = 5000;
                r["actNumber"] = Content.Core2.Loop.Acts;
                r["skills"] = new JObject { ["class:reaver"] = new JObject { ["xp"] = 399, ["level"] = 0, ["pendingDrafts"] = 0 } };
            });
            session.StartEncounter(Content.Core2.Loop.Map.Encounters.All.First(e => e.Value<string>("pool") == "boss").Value<string>("id"));
            foreach (var enemy in session.Combat.State.Enemies) enemy["hp"] = 1;
            for (var guard = 0; guard < 400 && session.IsInCombat; guard++) PlayTurn(session);
            var flow = Flow(session);
            Assert.That(flow.Stage, Is.EqualTo(Core2SpoilsStage.Notices), "the last boss's door has no offer, only what the fight earned");
            Assert.That(flow.Options, Is.Empty);
            var read = flow.Confirm();
            Assert.That(read.Done && read.Left, Is.True);
            Assert.That(session.RunOver, Is.True);
        }

        // ------------------------------------------------------------------ the door resumes

        [Test]
        public void ASavedDoorResumesOnTheSpoilsScreenWithItsOfferIntact()
        {
            var session = AtTheDoor(777);
            Assert.That(Content.Flow.Core2ScreenFor(session.Location), Is.EqualTo(ScreenIds.Core2Rewards));
            var loaded = Core2RunSession.Load(Content, _saves, 1, () => Epoch).Session;
            Assert.That(loaded.Location, Is.EqualTo(RunFlowValues.LocationRewards));
            var flow = Flow(loaded);
            Assert.That(flow.Stage, Is.EqualTo(Core2SpoilsStage.Offer));
            Assert.That(flow.Options.Select(o => o.Id), Is.EqualTo(Flow(session).Options.Select(o => o.Id)));
        }

        [Test]
        public void TheSpoilsScreenIsBuiltWithItsFocusRegionAndWordsInTheRegistry()
        {
            Assert.That(Ui.Screens.IsBuilt(ScreenIds.Core2Rewards), Is.True);
            Assert.That(Ui.Screens.Get(ScreenIds.Core2Rewards).FocusOrder, Does.Contain(UiNames.SpoilsModal));
            foreach (var kind in new[] { "combatCards", "weaponArts", "weapons", "armor", "relics", "potions" })
                Assert.That(Ui.Strings.Has(string.Format(System.Globalization.CultureInfo.InvariantCulture, SpoilsFormats.KindKey, kind)), Is.True, kind);
        }
    }
}
