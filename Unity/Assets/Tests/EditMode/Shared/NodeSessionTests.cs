using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Nodes;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Domain.Events;
using Ashen.Domain.Loop;
using Ashen.Domain.Random;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// The F3 node screens' application layer on the climb's session (W-09 merchant US-9.1–9.3, W-11 event and dialogue
    /// US-10.1–10.3, W-13 legacy dungeon US-4.5; D-140n–D-146n): a seeded climb is travelled to its node, and after every
    /// step the run document, the RNG counters and the profile must equal what the ported domain produces when the same
    /// calls are made on a copy of the run as it was saved. Every landed step is saved, so a reload resumes on the same
    /// screen in the same state; refusals change and save nothing. Engine-free.
    /// </summary>
    [TestFixture]
    public class NodeSessionTests
    {
        private const uint Seed = 20260927u;

        private static RunContent _content;
        private static UiData _ui;
        private string _dir;
        private SaveService _saves;

        private static RunContent Content => _content ??= RunContent.Load(TestContent.Source);
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-nodes-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private RunSession NewSession() => RunSession.New(Content, _saves, 1, Seed, Content.DefaultClass(), "Aldric");

        private RunSession Reload() => RunSession.Load(Content, _saves, 1).Session;

        private static Dictionary<RngStream, uint> Counters(JObject run)
        {
            var counters = new Dictionary<RngStream, uint>();
            foreach (var p in (run["streamCounters"] as JObject ?? new JObject()).Properties()) counters[RngStreamNames.Parse(p.Name)] = (uint)p.Value.Value<double>();
            return counters;
        }

        /// <summary>A loop context over a copy of the run as the session holds it (its RNG from the saved counters) for the direct domain calls.</summary>
        private static LoopContext Copy(RunSession session)
        {
            var run = session.Run;
            return new LoopContext(Content.Loop, run, new Rng(session.Seed, Counters(run)), (JObject)session.Profile.Doc.DeepClone(), session.Settings());
        }

        /// <summary>
        /// The session's run, RNG counters (stamped on the run at every save outside a fight, engine/save.js saveRun) and
        /// profile equal the domain's. The runs are compared without the stamp.
        /// </summary>
        private static void Same(LoopContext want, RunSession session, string label)
        {
            JObject Bare(JObject run)
            {
                var copy = (JObject)run.DeepClone();
                copy.Remove("streamCounters");
                return copy;
            }
            var got = session.Run;
            Assert.That(JToken.DeepEquals(Bare(want.Run), Bare(got)), Is.True, label + ": run differs from the domain's");
            Assert.That(Counters(got), Is.EquivalentTo(want.Rng.Counters()), label + ": RNG counters differ");
            Assert.That(JToken.DeepEquals(want.Profile, session.Profile.Doc), Is.True, label + ": profile differs");
        }

        private static void Give(RunSession session, double cinders)
        {
            session.EditRunForReview(run => run["cinders"] = cinders);
            session.Save();
        }

        // ------------------------------------------------------------------ routing

        [Test]
        public void TheRouterReachesTheNodeScreensByLocation()
        {
            var flow = Content.Flow;
            Assert.That(flow.ScreenFor(RunFlowValues.LocationMerchant), Is.EqualTo(ScreenIds.Merchant));
            Assert.That(flow.ScreenFor(RunFlowValues.LocationEvent), Is.EqualTo(ScreenIds.Event));
            Assert.That(flow.ScreenFor(RunFlowValues.LocationDungeon), Is.EqualTo(ScreenIds.LegacyDungeon));
            foreach (var id in new[] { ScreenIds.Merchant, ScreenIds.Event, ScreenIds.Dialogue, ScreenIds.LegacyDungeon })
                Assert.That(Ui.Screens.IsBuilt(id), Is.True, id + " is built");
            var quest = NewSession();
            NodeReview.AtEvent(quest, "namelessKeeper");
            Assert.That(NodeScreens.Refine(ScreenIds.Event, quest), Is.EqualTo(ScreenIds.Dialogue), "a quest chain's step shows on dialogue (W4c)");
            var plain = NewSession();
            NodeReview.AtEvent(plain, "goldenMoth");
            Assert.That(NodeScreens.Refine(ScreenIds.Event, plain), Is.EqualTo(ScreenIds.Event));
        }

        // ------------------------------------------------------------------ W-09 merchant

        [Test]
        public void TheMerchantStockIsSavedOnArrivalAndAReloadReusesIt()
        {
            var session = NewSession();
            Assert.That(NodeReview.AtMerchant(session).Location, Is.EqualTo(RunFlowValues.LocationMerchant));
            var merchant = MerchantSession.Start(session);
            var stock = MerchantSession.Stock(merchant.Run);
            Assert.That(stock, Is.Not.Null, "travel rolled the stock");
            var resumed = Reload();
            Assert.That(resumed.Location, Is.EqualTo(RunFlowValues.LocationMerchant), "the slot resumes at the merchant (US-9.1)");
            Assert.That(JToken.DeepEquals(MerchantSession.Stock(MerchantSession.Start(resumed).Run), stock), Is.True, "the saved stock is reused");
        }

        [Test]
        public void PurchasesBurnsAndSalesMatchTheDomainAndAreSavedAtOnce()
        {
            var session = NewSession();
            NodeReview.AtMerchant(session);
            Give(session, 2000);
            var merchant = MerchantSession.Start(session);
            var want = Copy(session);
            var view = MerchantView.Build(merchant, Ui);
            var card = view.Shelf("cards").Offers.First(o => o.Available);
            var deck = session.Run["deck"].Count();
            Assert.That(merchant.Execute(card.Action).Ok, Is.True);
            Assert.That(Shop.Execute(want.Data.Shop, want.Run, card.Action, true).Ok, Is.True);
            Same(want, session, "buy a card");
            Assert.That(session.Run["deck"].Count(), Is.EqualTo(deck + 1));
            Assert.That(JToken.DeepEquals(Reload().Run, session.Run), Is.True, "the purchase is saved at once");

            view = MerchantView.Build(merchant, Ui);
            var remove = view.Shelf("services").Offers.First(o => o.Kind == NodeValues.OfferRemove);
            Assert.That(remove.Available && remove.Picks.Count > 0, Is.True, "the burn offers the removable deck cards");
            var burn = new ShopAction { Kind = remove.Action.Kind, InstanceId = remove.Picks[0].Id };
            Assert.That(merchant.Execute(burn).Ok, Is.True);
            Assert.That(Shop.Execute(want.Data.Shop, want.Run, burn, true).Ok, Is.True);
            Same(want, session, "remove a card");
            var next = MerchantView.Build(merchant, Ui).Shelf("services").Offers.First(o => o.Kind == NodeValues.OfferRemove);
            Assert.That(next.Price, Is.GreaterThan(remove.Price), "the removal price rises by the step (US-9.2)");

            foreach (var shelf in new[] { "relics", "flasks", "armaments", "weaponArts" })
            {
                var offer = MerchantView.Build(merchant, Ui).Shelf(shelf).Offers.FirstOrDefault(o => o.Available);
                if (offer == null) continue;
                Assert.That(merchant.Execute(offer.Action).Ok, Is.True, shelf);
                Assert.That(Shop.Execute(want.Data.Shop, want.Run, offer.Action, true).Ok, Is.True, shelf);
                Same(want, session, "buy from " + shelf);
            }

            var sale = MerchantView.Build(merchant, Ui).Shelf("sell").Offers.First(o => o.Available);
            Assert.That(merchant.Execute(sale.Action).Ok, Is.True);
            Assert.That(Shop.Execute(want.Data.Shop, want.Run, sale.Action, true).Ok, Is.True);
            Same(want, session, "sell");
            Assert.That(JToken.DeepEquals(Reload().Run, session.Run), Is.True, "every step is saved");
        }

        [Test]
        public void AnUnaffordableOfferIsRefusedWithWhatItNeedsAndNothingIsSaved()
        {
            var session = NewSession();
            NodeReview.AtMerchant(session);
            Give(session, 0);
            var merchant = MerchantSession.Start(session);
            var card = MerchantView.Build(merchant, Ui).Shelf("cards").Offers.First();
            Assert.That(card.Available, Is.False);
            Assert.That(card.ReasonText, Is.EqualTo(Ui.Strings.Format(NodeStringKeys.NodesMerchantNeed, new StringArgs().Add("need", NodeText.Number(card.Price)))));
            var before = session.Run;
            var gen = _saves.Load(session.Slot).Gen;
            var result = merchant.Execute(card.Action);
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Refusal.Key, Is.EqualTo("ui.shop.avail.cinders"));
            Assert.That(NodeText.Refusal(Ui.Strings, result.Refusal), Is.EqualTo("Not enough cinders"), "the shipped short text");
            Assert.That(JToken.DeepEquals(before, session.Run), Is.True, "a refusal changes nothing");
            Assert.That(_saves.Load(session.Slot).Gen, Is.EqualTo(gen), "and saves nothing");
        }

        [Test]
        public void TheSellShelfFollowsTheShopSellSetting()
        {
            var session = NewSession();
            NodeReview.AtMerchant(session);
            Assert.That(session.ShopSellOn, Is.True, "on by default (D-081)");
            var merchant = MerchantSession.Start(session);
            Assert.That(MerchantView.Build(merchant, Ui).Shelf("sell"), Is.Not.Null);
            session.Profile.Doc["settings"] = new JObject { ["shopSell"] = false };
            Assert.That(session.ShopSellOn, Is.False);
            Assert.That(MerchantView.Build(merchant, Ui).Shelf("sell"), Is.Null, "no Sell category while the setting is off (04 W-09)");
            Assert.That(merchant.Execute(new ShopAction { Kind = "sellRelic", Index = 0 }).Ok, Is.False);
        }

        [Test]
        public void EveryShelfAndSmithServiceReadsWithResolvedText()
        {
            var session = NewSession();
            NodeReview.AtMerchant(session);
            // A smith travels with him (the oracle's recorded stock patch), so the upgrade, extract and install rows read.
            session.EditRunForReview(run =>
            {
                run["shopStock"]["smith"] = new JObject { ["offered"] = true, ["services"] = new JArray("upgrade", "extract", "install") };
                run["smithingStones"] = 9.0;
            });
            var view = MerchantView.Build(MerchantSession.Start(session), Ui);
            Assert.That(view.Shelves.Select(s => s.Id), Is.EqualTo(new[] { "cards", "armaments", "weaponArts", "relics", "flasks", "services", "sell" }));
            foreach (var offer in view.Shelves.SelectMany(s => s.Offers))
            {
                Assert.That(offer.Name, Is.Not.Empty.And.Not.Contains("{"), offer.Key);
                Assert.That(offer.PriceText, Is.Not.Empty.And.Not.Contains("{"), offer.Key);
                Assert.That(offer.PrimaryText, Is.Not.Empty.And.Not.Contains("{"), offer.Key);
                if (!offer.Available) Assert.That(offer.ReasonText, Is.Not.Empty.And.Not.Contains("{"), offer.Key);
                Assert.That(Ui.Menus.Confirm(offer.ConfirmId), Is.Not.Null, offer.Key);
            }
            var services = view.Shelf("services").Offers.Select(o => o.Kind).Distinct().ToList();
            Assert.That(services, Does.Contain(NodeValues.OfferRemove));
            Assert.That(services, Does.Contain(NodeValues.OfferUpgrade), "the smith's upgrade is offered when he travels with the merchant");
        }

        [Test]
        public void LeavingClearsTheStockAndSavesAtTheMap()
        {
            var session = NewSession();
            NodeReview.AtMerchant(session);
            MerchantSession.Start(session).Leave();
            Assert.That(session.Location, Is.EqualTo(RunFlowValues.LocationMap));
            Assert.That(session.Run["shopStock"].Type, Is.EqualTo(JTokenType.Null), "cleared on leave (US-9.1)");
            Assert.That(Reload().Location, Is.EqualTo(RunFlowValues.LocationMap));
        }

        // ------------------------------------------------------------------ W-11 event and dialogue

        private (RunSession Session, EventSession Event) AtEvent(string eventId, double cinders = -1)
        {
            var session = NewSession();
            NodeReview.AtEvent(session, eventId);
            if (cinders >= 0) Give(session, cinders);
            return (session, EventSession.Start(session));
        }

        [Test]
        public void ContinueWaitsForAResponseAndAChoiceMatchesTheDomain()
        {
            var (session, ev) = AtEvent("goldenMoth");
            var view = EventView.Build(ev, Ui);
            Assert.That(view.ContinueEnabled, Is.False, "Continue is disabled until a response is chosen (US-10.1)");
            Assert.That(ev.Continue(), Is.Null);
            Assert.That(view.Responses.Count, Is.EqualTo(3));
            Assert.That(view.ArtId, Is.EqualTo("enemy.lanternMoth.base"));
            var want = Copy(session);
            var choice = view.Responses.First(r => r.Available).ChoiceId;
            Assert.That(ev.Choose(choice).Ok, Is.True);
            Assert.That(Events.Choose(want.Data.Events, want.Run, "goldenMoth", choice, want.Rng).Ok, Is.True);
            Same(want, session, "choose");
            view = EventView.Build(ev, Ui);
            Assert.That(view.Resolved && view.ContinueEnabled, Is.True);
            Assert.That(view.ContinueText, Is.EqualTo(Ui.Strings.Get(NodeStringKeys.NodesEventContinue)));
            Assert.That(view.StatusText, Is.EqualTo(Ui.Strings.Get(NodeStringKeys.NodesEventStatusResolved)));
            Assert.That(ev.Choose(choice).Ok, Is.False, "a second response is refused");

            var resumed = Reload();
            Assert.That(resumed.Location, Is.EqualTo(RunFlowValues.LocationEvent));
            Assert.That(resumed.EventDone, Is.True, "Save & quit resumes in the resolved state");
            var again = EventSession.Start(resumed);
            Assert.That(EventView.Build(again, Ui).ResultText, Is.EqualTo(view.ResultText), "the taken response is read back from the history");
            Assert.That(again.Continue(), Is.EqualTo(RunFlowValues.LocationMap));
            Assert.That(Reload().Location, Is.EqualTo(RunFlowValues.LocationMap));
        }

        [Test]
        public void AResponseThatStartsAFightReadsSteelYourselfAndHandsTheFightToCombat()
        {
            var (session, ev) = AtEvent("wyrmTrial");
            var enter = EventView.Build(ev, Ui).Responses.First(r => r.ChoiceId == "enterRing");
            Assert.That(enter.Preview, Does.Contain(Ui.Strings.Format(NodeStringKeys.NodesEffectStartCombat, new StringArgs().Add("name", string.Empty)).TrimEnd()));
            var want = Copy(session);
            Assert.That(ev.Choose("enterRing").Ok, Is.True);
            Events.Choose(want.Data.Events, want.Run, "wyrmTrial", "enterRing", want.Rng);
            Same(want, session, "choose the fight");
            var view = EventView.Build(ev, Ui);
            Assert.That(view.StartsFight, Is.True);
            Assert.That(view.ContinueText, Is.EqualTo("Steel yourself"), "US-10.1");
            var fight = RunLoop.EnterEventCombat(want);
            Assert.That(ev.Continue(), Is.EqualTo(RunFlowValues.LocationCombat));
            Assert.That(session.IsInCombat, Is.True);
            Assert.That(session.EncounterId, Is.EqualTo(fight.EncounterId), "the response's encounter");
            var resumed = Reload();
            Assert.That(resumed.IsInCombat, Is.True, "the handed-over fight is saved as it starts");
            Assert.That(resumed.StateHash(), Is.EqualTo(session.StateHash()));
        }

        [Test]
        public void IllegalResponsesShowTheirRequirementAndAreRefused()
        {
            var (session, ev) = AtEvent("weepingPilgrim", 0);
            var give = EventView.Build(ev, Ui).Responses.First(r => r.ChoiceId == "giveCinders");
            Assert.That(give.Available, Is.False);
            Assert.That(give.ReasonText, Is.EqualTo(Ui.Strings.Format(NodeStringKeys.NodesEventRequiresCinders, new StringArgs().Add("cost", "50").Add("cinders", "0"))));
            Assert.That(give.Label, Does.Contain(give.ReasonText), "the requirement shows on the response");
            var before = session.Run;
            var result = ev.Choose("giveCinders");
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Refusal.Key, Is.EqualTo("events.refusal.cinders"));
            Assert.That(JToken.DeepEquals(before, session.Run), Is.True);
            Assert.That(EventView.Build(ev, Ui).StatusText, Is.EqualTo(Ui.Strings.Format(NodeStringKeys.NodesEventStatusLimited, new StringArgs().Add("available", "1").Add("total", "2"))));
        }

        [Test]
        public void AQuestGatedStepShowsItsSpeakerAndItsHistoryRequirements()
        {
            var (session, ev) = AtEvent("namelessKeeper", 200);
            var view = EventView.Build(ev, Ui);
            Assert.That(view.HasSpeaker, Is.True);
            Assert.That(view.SpeakerName, Is.Not.Empty);
            Assert.That(view.SpeakerArtId, Is.EqualTo("enemy.emberStarvedPilgrim.base"));
            var locked = view.Responses.Where(r => r.Locked).ToList();
            Assert.That(locked.Count, Is.EqualTo(3), "without the Grave's history, three responses are locked (US-10.2)");
            Assert.That(locked.All(r => r.ReasonText.Contains("Grave of the Nameless")), Is.True, locked.First().ReasonText);
            Assert.That(ev.Choose("faceKeeper").Refusal.Key, Is.EqualTo("events.refusal.history"));
            // The earlier step taken: the history opens the matching responses.
            session.EditRunForReview(run => ((JArray)run["history"]).Add(new JObject
            {
                ["kind"] = "eventChoice", ["eventId"] = "graveOfTheNameless", ["choiceId"] = "digForCinders", ["actNumber"] = 1.0, ["floor"] = 0.0, ["mapNodeId"] = null,
            }));
            view = EventView.Build(ev, Ui);
            Assert.That(view.Responses.First(r => r.ChoiceId == "faceKeeper").Locked, Is.False);
            Assert.That(view.Responses.First(r => r.ChoiceId == "acceptThanks").Locked, Is.True, "payRespects was not chosen");
        }

        [Test]
        public void TheTurncoatMirrorIsTheOneWayToChangeClass()
        {
            var events = TestContent.ContentJson("catalog/events.json");
            var swappers = events.Properties().Where(p => p.Value["choices"].Any(c => c["effects"].Any(e => (string)e["op"] == "swapClass"))).Select(p => p.Name).ToList();
            Assert.That(swappers, Is.EqualTo(new[] { "turncoatMirror" }), "the class-swap op is used by that event only (US-10.3)");
            var (session, ev) = AtEvent("turncoatMirror");
            var before = session.ClassId;
            var want = Copy(session);
            Assert.That(ev.Choose("stepThrough").Ok, Is.True);
            Events.Choose(want.Data.Events, want.Run, "turncoatMirror", "stepThrough", want.Rng);
            Same(want, session, "swap");
            Assert.That(session.ClassId, Is.Not.EqualTo(before));
            Assert.That(EventView.Build(ev, Ui).SwapText, Does.Contain(Ui.Strings.Get("class." + session.ClassId + ".name")));
            Assert.That(Reload().Portrait, Does.Contain(session.ClassId), "the saved portrait follows the new class");
        }

        [Test]
        public void EveryEventReadsWithResolvedText()
        {
            foreach (var id in Content.Loop.Events.Events.All.Select(e => e.Value<string>("id")))
            {
                var (session, ev) = AtEvent(id, 500);
                var view = EventView.Build(ev, Ui);
                Assert.That(view.Title, Is.Not.Empty, id);
                Assert.That(view.Responses, Is.Not.Empty, id);
                foreach (var r in view.Responses)
                {
                    Assert.That(r.Label, Is.Not.Empty.And.Not.Contains("{"), id + " " + r.ChoiceId);
                    Assert.That(r.Preview, Is.Not.Empty.And.Not.Contains("{"), id + " " + r.ChoiceId);
                }
                Assert.That(view.Responses.Any(r => r.Available), Is.True, id + " has an always-legal response");
            }
        }

        // ------------------------------------------------------------------ W-13 legacy dungeon

        private (RunSession Session, DungeonSession Dungeon) AtDungeon()
        {
            var session = NewSession();
            NodeReview.AtDungeon(session, "BS");
            return (session, DungeonSession.Start(session));
        }

        [Test]
        public void EnterListenTravelAndResumeMatchTheDomain()
        {
            var (session, dungeon) = AtDungeon();
            Assert.That(session.Location, Is.EqualTo(RunFlowValues.LocationDungeon));
            var view = DungeonView.Build(dungeon, Ui);
            Assert.That(view.NodeName, Is.Not.Empty);
            Assert.That(view.BackgroundId, Is.EqualTo("env.legacy.BS-ENV-01-background"));
            Assert.That(view.MapId, Is.EqualTo("env.legacy.MAP-01-briar-sanctum"));
            Assert.That(view.Actions.Select(a => a.Kind), Is.EqualTo(new[] { NodeValues.ActionResponse }), "the entrance offers its response only");
            var want = Copy(session);
            Assert.That(dungeon.Choose("listen"), Is.EqualTo(RunFlowValues.LocationDungeon));
            LegacyDungeons.Choose(want.Data, want.Run, "listen", want.Rng);
            Same(want, session, "listen");
            Assert.That(DungeonView.Build(dungeon, Ui).Actions.Single().Kind, Is.EqualTo(NodeValues.ActionContinue));
            var resumed = Reload();
            Assert.That(resumed.Location, Is.EqualTo(RunFlowValues.LocationDungeon), "Save & quit resumes inside the dungeon");
            Assert.That(JToken.DeepEquals(resumed.Run["legacyDungeon"], session.Run["legacyDungeon"]), Is.True, "with the pending response");
            Assert.That(dungeon.Continue(), Is.EqualTo(RunFlowValues.LocationDungeon));
            LegacyDungeons.ContinueDialogue(want);
            Same(want, session, "continue");
            var exits = dungeon.Exits();
            Assert.That(exits, Is.Not.Empty);
            Assert.That(dungeon.Travel(exits[0]), Is.True);
            Assert.That(LegacyDungeons.TravelTo(want, exits[0]).Kind, Is.EqualTo("dialogue"));
            Same(want, session, "travel");
            Assert.That(dungeon.Travel(exits[0]), Is.False, "an unresolved node closes the road");
        }

        [Test]
        public void RoomsHandOffToCombatTheRestStayAndTheRewards()
        {
            foreach (var (from, to, location) in new[] { ("BS-04", "BS-05", RunFlowValues.LocationCombat), ("BS-05", "BS-06", RunFlowValues.LocationRest), ("BS-08", "BS-09", RunFlowValues.LocationRewards) })
            {
                var (session, dungeon) = AtDungeon();
                NodeReview.StandAt(session, from, true);
                dungeon.Refresh();
                var want = Copy(session);
                Assert.That(dungeon.Travel(to), Is.True, to);
                var room = DungeonView.Build(dungeon, Ui).Actions.Single(a => a.Kind == NodeValues.ActionRoom);
                Assert.That(room.Label, Is.Not.Empty);
                Assert.That(dungeon.EnterRoom(), Is.EqualTo(location), to);
                var direct = LegacyDungeons.TravelTo(want, to);
                if (location == RunFlowValues.LocationRewards)
                {
                    Same(want, session, "the cache, as travel-then-enter rolls it");
                    Assert.That(Reload().HasPendingReward, Is.True, "the cache is saved as a pending reward");
                }
                if (location == RunFlowValues.LocationCombat) Assert.That(session.EncounterId, Is.EqualTo(direct.EncounterId), "the room's encounter");
            }
        }

        [Test]
        public void FleeingRollsDexterityOnTheEventsStream()
        {
            var (session, dungeon) = AtDungeon();
            NodeReview.StandAt(session, "BS-02", true);
            dungeon.Refresh();
            dungeon.Travel("BS-03");
            var flee = DungeonView.Build(dungeon, Ui).Actions.First(a => a.Id == "flee");
            Assert.That(flee.Label, Does.Contain(NodeText.Number(LegacyDungeons.EscapeChance(Content.Loop, dungeon.Run))));
            var want = Copy(session);
            dungeon.Choose("flee");
            LegacyDungeons.Choose(want.Data, want.Run, "flee", want.Rng);
            Same(want, session, "flee");
            Assert.That(dungeon.Pending["roll"].Type, Is.EqualTo(JTokenType.Float).Or.EqualTo(JTokenType.Integer));
        }

        [Test]
        public void LeavingAClearedDungeonAdvancesTheActOrEndsTheClimb()
        {
            var (session, dungeon) = AtDungeon();
            Assert.That(dungeon.Leave(), Is.Null, "only a cleared dungeon can be left");
            NodeReview.StandAt(session, "BS-24", true, true);
            dungeon.Refresh();
            Assert.That(DungeonView.Build(dungeon, Ui).Actions.First().Kind, Is.EqualTo(NodeValues.ActionLeave), "Leave comes first once the dungeon is cleared");
            var want = Copy(session);
            Assert.That(dungeon.Leave(), Is.EqualTo(RunFlowValues.LocationMap));
            Assert.That(LegacyDungeons.Leave(want).Kind, Is.EqualTo("advanced"));
            Same(want, session, "leave");
            Assert.That(Reload().Act, Is.EqualTo(2), "the next act's map");

            var (final, finalDungeon) = AtDungeon();
            final.EditRunForReview(run => run["actNumber"] = Content.Loop.Rewards.RuleNum("summit", "finalAct"));
            NodeReview.StandAt(final, "BS-24", true, true);
            finalDungeon.Refresh();
            Assert.That(finalDungeon.Leave(), Is.EqualTo(RunFlowValues.LocationRunEnd));
            Assert.That(final.RunOver && final.End.Victory, Is.True);
            Assert.That(_saves.Exists(final.Slot), Is.False, "the climb is over and the slot cleared");
        }
    }
}
