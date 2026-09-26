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
    /// The F3 node screens' application layer (W-09 merchant US-9.1–9.3, W-11 event and dialogue US-10.1–10.3, W-13 legacy
    /// dungeon US-4.5; D-140n–D-146n): each session is driven on a seeded run made by the loop's newRun and travelled to its
    /// node, and after every step the run document, the RNG counters and the profile must equal what the ported domain
    /// produces when the same calls are made on a copy of the same context directly. Every landed step is saved, so a
    /// reload resumes on the same screen in the same state; refusals change nothing. Engine-free.
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

        private RunSession NewSession()
        {
            var session = RunSession.New(Content, _saves, 1, Seed, Content.DefaultClass(), "Aldric");
            NodeReview.UseLoopRun(session, Seed);
            return session;
        }

        private RunSession Reload() => RunSession.Load(Content, _saves, 1).Session;

        /// <summary>A copy of a context (run, RNG counters, profile) for the direct domain calls.</summary>
        private static LoopContext Copy(LoopContext ctx) =>
            new LoopContext(ctx.Data, (JObject)ctx.Run.DeepClone(), new Rng(ctx.Rng.Seed, ctx.Rng.Counters()), (JObject)ctx.Profile.DeepClone(), ctx.Settings);

        /// <summary>
        /// The run, RNG counters and profile equal the domain's. The run is compared without streamCounters, which the save
        /// stamps on every save outside a fight (engine/save.js saveRun; D-109); the stamp must equal the live counters.
        /// </summary>
        private static void Same(LoopContext want, LoopContext got, string label)
        {
            JObject Bare(JObject run)
            {
                var copy = (JObject)run.DeepClone();
                copy.Remove("streamCounters");
                return copy;
            }
            Assert.That(JToken.DeepEquals(Bare(want.Run), Bare(got.Run)), Is.True, label + ": run differs from the domain's");
            Assert.That(want.Rng.Counters(), Is.EquivalentTo(got.Rng.Counters()), label + ": RNG counters differ");
            Assert.That(JToken.DeepEquals(want.Profile, got.Profile), Is.True, label + ": profile differs");
            if (got.Run["streamCounters"] is JObject stamped)
                foreach (var kv in got.Rng.Counters()) Assert.That((uint)stamped.Value<double>(RngStreamNames.ToWire(kv.Key)), Is.EqualTo(kv.Value), label + ": stamped " + kv.Key);
        }

        private static void Give(RunSession session, double cinders) => session.EditRunForReview(run => run["cinders"] = cinders);

        // ------------------------------------------------------------------ routing

        [Test]
        public void TheRouterEntryIsKeyedByNodeOutcomeKind()
        {
            var d = Content.Loop;
            Assert.That(NodeScreens.For(d, new NodeOutcome { Kind = "merchant" }).Screen, Is.EqualTo(ScreenIds.Merchant));
            Assert.That(NodeScreens.For(d, new NodeOutcome { Kind = "event", EventId = "goldenMoth" }).Screen, Is.EqualTo(ScreenIds.Event));
            Assert.That(NodeScreens.For(d, new NodeOutcome { Kind = "event", EventId = "namelessKeeper" }).Screen, Is.EqualTo(ScreenIds.Dialogue), "a quest chain's step has a speaker (W4c)");
            var dungeon = NodeScreens.For(d, new NodeOutcome { Kind = "dungeon", DungeonId = "BS", EncounterId = "bossThornMatriarch" });
            Assert.That(dungeon.Screen, Is.EqualTo(ScreenIds.LegacyDungeon));
            Assert.That(dungeon.DungeonId, Is.EqualTo("BS"));
            foreach (var kind in new[] { "fight", "rest", "treasure" }) Assert.That(NodeScreens.For(d, new NodeOutcome { Kind = kind }), Is.Null, kind);
            Assert.That(Ui.Screens.IsBuilt(ScreenIds.Merchant) && Ui.Screens.IsBuilt(ScreenIds.Event) && Ui.Screens.IsBuilt(ScreenIds.Dialogue) && Ui.Screens.IsBuilt(ScreenIds.LegacyDungeon), Is.True);
        }

        // ------------------------------------------------------------------ W-09 merchant

        [Test]
        public void TheMerchantRollsItsStockOnceAndAReloadReusesIt()
        {
            var session = NewSession();
            var entry = NodeReview.AtMerchant(session);
            var ctx = session.NodeContext();
            var want = Copy(ctx);
            Shop.Open(want.Data.Shop, want.Run, want.Rng);
            var merchant = MerchantSession.Start(ctx, session, entry);
            Assert.That(merchant.Rolled, Is.True);
            Same(want, ctx, "open");
            var resumed = Reload();
            Assert.That(resumed.ResumeNode?.Screen, Is.EqualTo(ScreenIds.Merchant), "the slot resumes at the merchant (US-9.1)");
            Assert.That(resumed.Location, Is.EqualTo(ScreenIds.Merchant));
            var again = MerchantSession.Start(resumed.NodeContext(), resumed, resumed.ResumeNode);
            Assert.That(again.Rolled, Is.False, "a resumed visit re-reads the saved stock");
            Assert.That(JToken.DeepEquals(again.Stock, merchant.Stock), Is.True);
        }

        [Test]
        public void PurchasesBurnsAndSalesMatchTheDomainAndAreSavedAtOnce()
        {
            var session = NewSession();
            var entry = NodeReview.AtMerchant(session);
            Give(session, 2000);
            var ctx = session.NodeContext();
            var merchant = MerchantSession.Start(ctx, session, entry);
            var want = Copy(ctx);
            var view = MerchantView.Build(merchant, Ui);
            var card = view.Shelf("cards").Offers.First(o => o.Available);
            var deck = ctx.Run["deck"].Count();
            Assert.That(merchant.Execute(card.Action).Ok, Is.True);
            Assert.That(Shop.Execute(want.Data.Shop, want.Run, card.Action, true).Ok, Is.True);
            Same(want, ctx, "buy a card");
            Assert.That(ctx.Run["deck"].Count(), Is.EqualTo(deck + 1));
            Assert.That(JToken.DeepEquals(Reload().Run, ctx.Run), Is.True, "the purchase is saved at once");

            view = MerchantView.Build(merchant, Ui);
            var remove = view.Shelf("services").Offers.First(o => o.Kind == NodeValues.OfferRemove);
            Assert.That(remove.Available && remove.Picks.Count > 0, Is.True, "the burn offers the removable deck cards");
            var burn = new ShopAction { Kind = remove.Action.Kind, InstanceId = remove.Picks[0].Id };
            var price = remove.Price;
            Assert.That(merchant.Execute(burn).Ok, Is.True);
            Assert.That(Shop.Execute(want.Data.Shop, want.Run, burn, true).Ok, Is.True);
            Same(want, ctx, "remove a card");
            var next = MerchantView.Build(merchant, Ui).Shelf("services").Offers.First(o => o.Kind == NodeValues.OfferRemove);
            Assert.That(next.Price, Is.GreaterThan(price), "the removal price rises by the step (US-9.2)");

            foreach (var shelf in new[] { "relics", "flasks", "armaments", "weaponArts" })
            {
                var offer = MerchantView.Build(merchant, Ui).Shelf(shelf).Offers.FirstOrDefault(o => o.Available);
                if (offer == null) continue;
                Assert.That(merchant.Execute(offer.Action).Ok, Is.True, shelf);
                Assert.That(Shop.Execute(want.Data.Shop, want.Run, offer.Action, true).Ok, Is.True, shelf);
                Same(want, ctx, "buy from " + shelf);
            }

            var sale = MerchantView.Build(merchant, Ui).Shelf("sell").Offers.First(o => o.Available);
            Assert.That(merchant.Execute(sale.Action).Ok, Is.True);
            Assert.That(Shop.Execute(want.Data.Shop, want.Run, sale.Action, true).Ok, Is.True);
            Same(want, ctx, "sell");
            Assert.That(JToken.DeepEquals(Reload().Run, ctx.Run), Is.True, "every step is saved");
        }

        [Test]
        public void AnUnaffordableOfferIsRefusedWithWhatItNeedsAndNothingIsSaved()
        {
            var session = NewSession();
            var entry = NodeReview.AtMerchant(session);
            Give(session, 0);
            var ctx = session.NodeContext();
            var merchant = MerchantSession.Start(ctx, session, entry);
            var view = MerchantView.Build(merchant, Ui);
            var card = view.Shelf("cards").Offers.First();
            Assert.That(card.Available, Is.False);
            Assert.That(card.ReasonText, Is.EqualTo(Ui.Strings.Format(NodeStringKeys.NodesMerchantNeed, new StringArgs().Add("need", NodeText.Number(card.Price)))));
            var before = (JObject)ctx.Run.DeepClone();
            var gen = _saves.Load(session.Slot).Gen;
            var result = merchant.Execute(card.Action);
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Refusal.Key, Is.EqualTo("ui.shop.avail.cinders"));
            Assert.That(NodeText.Refusal(Ui.Strings, result.Refusal), Is.EqualTo("Not enough cinders"), "the shipped short text");
            Assert.That(JToken.DeepEquals(before, ctx.Run), Is.True, "a refusal changes nothing");
            Assert.That(_saves.Load(session.Slot).Gen, Is.EqualTo(gen), "and saves nothing");
        }

        [Test]
        public void TheSellShelfFollowsTheShopSellSetting()
        {
            var session = NewSession();
            var entry = NodeReview.AtMerchant(session);
            Assert.That(session.SellOn, Is.True, "on by default (D-081)");
            var merchant = MerchantSession.Start(session.NodeContext(), session, entry);
            Assert.That(MerchantView.Build(merchant, Ui).Shelf("sell"), Is.Not.Null);
            session.Profile.Doc["settings"] = new JObject { ["shopSell"] = false };
            Assert.That(session.SellOn, Is.False);
            var view = MerchantView.Build(merchant, Ui);
            Assert.That(view.Shelf("sell"), Is.Null, "no Sell category while the setting is off (04 W-09)");
            var result = merchant.Execute(new ShopAction { Kind = "sellRelic", Index = 0 });
            Assert.That(result.Ok, Is.False);
        }

        [Test]
        public void EveryShelfAndSmithServiceReadsWithResolvedText()
        {
            var session = NewSession();
            var entry = NodeReview.AtMerchant(session);
            var ctx = session.NodeContext();
            var merchant = MerchantSession.Start(ctx, session, entry);
            // A smith travels with him (the oracle's recorded stock patch), so the upgrade, extract and install rows read.
            ctx.Run["shopStock"]["smith"] = new JObject { ["offered"] = true, ["services"] = new JArray("upgrade", "extract", "install") };
            ctx.Run["smithingStones"] = 9.0;
            var view = MerchantView.Build(merchant, Ui);
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
            var entry = NodeReview.AtMerchant(session);
            var ctx = session.NodeContext();
            var merchant = MerchantSession.Start(ctx, session, entry);
            var exit = merchant.Leave();
            Assert.That(exit.IsMap, Is.True);
            Assert.That(ctx.Run["shopStock"].Type, Is.EqualTo(JTokenType.Null), "cleared on leave (US-9.1)");
            var resumed = Reload();
            Assert.That(resumed.Location, Is.EqualTo(RunFlowValues.LocationMap));
            Assert.That(resumed.ResumeNode, Is.Null);
        }

        // ------------------------------------------------------------------ W-11 event and dialogue

        private (RunSession Session, EventSession Event, LoopContext Ctx) AtEvent(string eventId, double cinders = -1)
        {
            var session = NewSession();
            var entry = NodeReview.AtEvent(session, eventId);
            if (cinders >= 0) Give(session, cinders);
            var ctx = session.NodeContext();
            return (session, EventSession.Start(ctx, session, entry), ctx);
        }

        [Test]
        public void ContinueWaitsForAResponseAndAChoiceMatchesTheDomain()
        {
            var (session, ev, ctx) = AtEvent("goldenMoth");
            var view = EventView.Build(ev, Ui);
            Assert.That(view.ContinueEnabled, Is.False, "Continue is disabled until a response is chosen (US-10.1)");
            Assert.That(ev.Continue(), Is.Null);
            Assert.That(view.Responses.Count, Is.EqualTo(3));
            Assert.That(view.ArtId, Is.EqualTo("enemy.lanternMoth.base"));
            var want = Copy(ctx);
            var choice = view.Responses.First(r => r.Available).ChoiceId;
            Assert.That(ev.Choose(choice).Ok, Is.True);
            Assert.That(Events.Choose(want.Data.Events, want.Run, "goldenMoth", choice, want.Rng).Ok, Is.True);
            Same(want, ctx, "choose");
            view = EventView.Build(ev, Ui);
            Assert.That(view.Resolved && view.ContinueEnabled, Is.True);
            Assert.That(view.ContinueText, Is.EqualTo(Ui.Strings.Get(NodeStringKeys.NodesEventContinue)));
            Assert.That(view.StatusText, Is.EqualTo(Ui.Strings.Get(NodeStringKeys.NodesEventStatusResolved)));
            Assert.That(ev.Choose(choice).Ok, Is.False, "a second response is refused");

            var resumed = Reload();
            Assert.That(resumed.ResumeNode.ChoiceId, Is.EqualTo(choice), "Save & quit resumes in the resolved state");
            var again = EventSession.Start(resumed.NodeContext(), resumed, resumed.ResumeNode);
            Assert.That(EventView.Build(again, Ui).ResultText, Is.EqualTo(view.ResultText));
            var exit = again.Continue();
            Assert.That(exit.IsMap, Is.True);
            Assert.That(Reload().Location, Is.EqualTo(RunFlowValues.LocationMap));
        }

        [Test]
        public void AResponseThatStartsAFightReadsSteelYourselfAndHandsTheFightToCombat()
        {
            var (session, ev, ctx) = AtEvent("wyrmTrial");
            var enter = EventView.Build(ev, Ui).Responses.First(r => r.ChoiceId == "enterRing");
            Assert.That(enter.Preview, Does.Contain(Ui.Strings.Format(NodeStringKeys.NodesEffectStartCombat, new StringArgs().Add("name", string.Empty)).TrimEnd()));
            var want = Copy(ctx);
            Assert.That(ev.Choose("enterRing").Ok, Is.True);
            Events.Choose(want.Data.Events, want.Run, "wyrmTrial", "enterRing", want.Rng);
            Same(want, ctx, "choose the fight");
            var view = EventView.Build(ev, Ui);
            Assert.That(view.StartsFight, Is.True);
            Assert.That(view.ContinueText, Is.EqualTo("Steel yourself"), "US-10.1");
            var exit = ev.Continue();
            var fight = RunLoop.EnterEventCombat(want);
            Assert.That(exit.IsFight, Is.True);
            Assert.That(exit.Fight.EncounterId, Is.EqualTo(fight.EncounterId));
            Assert.That(JToken.DeepEquals(exit.Fight.Args, fight.Args), Is.True, "the same createCombat arguments");
            Same(want, ctx, "enter the fight");
            session.StartFight(exit.Fight);
            Assert.That(session.IsInCombat && session.Location == RunFlowValues.LocationCombat, Is.True);
            var resumed = Reload();
            Assert.That(resumed.IsInCombat, Is.True, "the handed-over fight is saved as it starts");
            Assert.That(resumed.StateHash(), Is.EqualTo(session.StateHash()));
        }

        [Test]
        public void IllegalResponsesShowTheirRequirementAndAreRefused()
        {
            var (session, ev, ctx) = AtEvent("weepingPilgrim", 0);
            var give = EventView.Build(ev, Ui).Responses.First(r => r.ChoiceId == "giveCinders");
            Assert.That(give.Available, Is.False);
            Assert.That(give.ReasonText, Is.EqualTo(Ui.Strings.Format(NodeStringKeys.NodesEventRequiresCinders, new StringArgs().Add("cost", "50").Add("cinders", "0"))));
            Assert.That(give.Label, Does.Contain(give.ReasonText), "the requirement shows on the response");
            var before = (JObject)ctx.Run.DeepClone();
            var result = ev.Choose("giveCinders");
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Refusal.Key, Is.EqualTo("events.refusal.cinders"));
            Assert.That(JToken.DeepEquals(before, ctx.Run), Is.True);
            Assert.That(EventView.Build(ev, Ui).StatusText, Is.EqualTo(Ui.Strings.Format(NodeStringKeys.NodesEventStatusLimited, new StringArgs().Add("available", "1").Add("total", "2"))));
        }

        [Test]
        public void AQuestGatedStepOpensAsDialogueWithItsSpeakerAndItsHistoryRequirements()
        {
            var (session, ev, ctx) = AtEvent("namelessKeeper", 200);
            Assert.That(ev.Entry.Screen, Is.EqualTo(ScreenIds.Dialogue));
            var view = EventView.Build(ev, Ui);
            Assert.That(view.HasSpeaker, Is.True);
            Assert.That(view.SpeakerName, Is.Not.Empty);
            Assert.That(view.SpeakerArtId, Is.EqualTo("enemy.emberStarvedPilgrim.base"));
            var locked = view.Responses.Where(r => r.Locked).ToList();
            Assert.That(locked.Count, Is.EqualTo(3), "without the Grave's history, three responses are locked (US-10.2)");
            Assert.That(locked.All(r => r.ReasonText.Contains("Grave of the Nameless")), Is.True, locked.First().ReasonText);
            Assert.That(ev.Choose("faceKeeper").Refusal.Key, Is.EqualTo("events.refusal.history"));
            // The earlier step taken: the history opens the matching responses.
            var grave = Events.Choose(ctx.Data.Events, ctx.Run, "graveOfTheNameless", "digForCinders", ctx.Rng);
            Assert.That(grave.Ok, Is.True);
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
            var (session, ev, ctx) = AtEvent("turncoatMirror");
            var before = ctx.Run.Value<string>("class");
            var want = Copy(ctx);
            Assert.That(ev.Choose("stepThrough").Ok, Is.True);
            Events.Choose(want.Data.Events, want.Run, "turncoatMirror", "stepThrough", want.Rng);
            Same(want, ctx, "swap");
            Assert.That(ctx.Run.Value<string>("class"), Is.Not.EqualTo(before));
            var view = EventView.Build(ev, Ui);
            Assert.That(view.SwapText, Does.Contain(Ui.Strings.Get("class." + ctx.Run.Value<string>("class") + ".name")));
            Assert.That(Reload().Portrait, Does.Contain(ctx.Run.Value<string>("class")), "the saved portrait follows the new class");
        }

        [Test]
        public void EveryEventReadsWithResolvedText()
        {
            foreach (var id in Content.Loop.Events.Events.All.Select(e => e.Value<string>("id")))
            {
                var (session, ev, ctx) = AtEvent(id, 500);
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

        private (RunSession Session, DungeonSession Dungeon, LoopContext Ctx) AtDungeon()
        {
            var session = NewSession();
            var entry = NodeReview.AtDungeon(session, "BS");
            var ctx = session.NodeContext();
            return (session, DungeonSession.Start(ctx, session, entry), ctx);
        }

        [Test]
        public void EnterListenTravelAndResumeMatchTheDomain()
        {
            var (session, dungeon, ctx) = AtDungeon();
            var view = DungeonView.Build(dungeon, Ui);
            Assert.That(view.NodeName, Is.Not.Empty);
            Assert.That(view.BackgroundId, Is.EqualTo("env.legacy.BS-ENV-01-background"));
            Assert.That(view.MapId, Is.EqualTo("env.legacy.MAP-01-briar-sanctum"));
            Assert.That(view.Actions.Select(a => a.Kind), Is.EqualTo(new[] { NodeValues.ActionResponse }), "the entrance offers its response only");
            var want = Copy(ctx);
            dungeon.Choose("listen");
            LegacyDungeons.Choose(want.Data, want.Run, "listen", want.Rng);
            Same(want, ctx, "listen");
            Assert.That(DungeonView.Build(dungeon, Ui).Actions.Single().Kind, Is.EqualTo(NodeValues.ActionContinue));
            var resumed = Reload();
            Assert.That(resumed.ResumeNode.Screen, Is.EqualTo(ScreenIds.LegacyDungeon), "Save & quit resumes inside the dungeon");
            Assert.That(JToken.DeepEquals(resumed.Run["legacyDungeon"], ctx.Run["legacyDungeon"]), Is.True, "with the pending response");
            Assert.That(dungeon.Continue().Kind, Is.EqualTo(NodeValues.ExitStay));
            LegacyDungeons.ContinueDialogue(want);
            Same(want, ctx, "continue");
            var exits = dungeon.Exits();
            Assert.That(exits, Is.Not.Empty);
            Assert.That(dungeon.Travel(exits[0]), Is.True);
            Assert.That(LegacyDungeons.TravelTo(want, exits[0]).Kind, Is.EqualTo("dialogue"));
            Same(want, ctx, "travel");
            Assert.That(dungeon.Travel(exits[0]), Is.False, "an unresolved node closes the road");
        }

        [Test]
        public void RoomsHandOffAsTheShippedTravelThenEnterDoes()
        {
            foreach (var (from, to, exitKind) in new[] { ("BS-04", "BS-05", NodeValues.ExitFight), ("BS-05", "BS-06", NodeValues.ExitRest), ("BS-08", "BS-09", NodeValues.ExitRewards) })
            {
                var (session, dungeon, ctx) = AtDungeon();
                NodeReview.StandAt(session, from, true);
                var want = Copy(ctx);
                Assert.That(dungeon.Travel(to), Is.True, to);
                var room = DungeonView.Build(dungeon, Ui).Actions.Single(a => a.Kind == NodeValues.ActionRoom);
                Assert.That(room.Label, Is.Not.Empty);
                var exit = dungeon.EnterRoom();
                var direct = LegacyDungeons.TravelTo(want, to);
                Assert.That(exit.Kind, Is.EqualTo(exitKind), to);
                Same(want, ctx, "travel into " + to);
                if (exitKind == NodeValues.ExitFight) Assert.That(JToken.DeepEquals(exit.Fight.Args, direct.Args), Is.True);
                if (exitKind == NodeValues.ExitRewards) Assert.That(Reload().HasPendingReward, Is.True, "the cache is saved as a pending reward");
            }
        }

        [Test]
        public void FleeingRollsDexterityOnTheEventsStream()
        {
            var (session, dungeon, ctx) = AtDungeon();
            NodeReview.StandAt(session, "BS-02", true);
            dungeon.Travel("BS-03");
            var flee = DungeonView.Build(dungeon, Ui).Actions.First(a => a.Id == "flee");
            Assert.That(flee.Label, Does.Contain(NodeText.Number(LegacyDungeons.EscapeChance(ctx.Data, ctx.Run))));
            var want = Copy(ctx);
            var pending = dungeon.Choose("flee");
            LegacyDungeons.Choose(want.Data, want.Run, "flee", want.Rng);
            Same(want, ctx, "flee");
            Assert.That(pending["roll"].Type, Is.EqualTo(JTokenType.Float).Or.EqualTo(JTokenType.Integer));
        }

        [Test]
        public void LeavingAClearedDungeonAdvancesTheActOrEndsTheClimb()
        {
            var (session, dungeon, ctx) = AtDungeon();
            Assert.That(dungeon.Leave(), Is.Null, "only a cleared dungeon can be left");
            NodeReview.StandAt(session, "BS-24", true, true);
            Assert.That(DungeonView.Build(dungeon, Ui).Actions.Last().Kind, Is.EqualTo(NodeValues.ActionLeave));
            var want = Copy(ctx);
            var exit = dungeon.Leave();
            var direct = LegacyDungeons.Leave(want);
            Assert.That(exit.IsMap && direct.Kind == "advanced", Is.True);
            Same(want, ctx, "leave");
            Assert.That(Reload().ResumeNode, Is.Null, "the next act's map");

            var (final, finalDungeon, finalCtx) = AtDungeon();
            final.EditRunForReview(run => run["actNumber"] = Content.Loop.Rewards.RuleNum("summit", "finalAct"));
            NodeReview.StandAt(final, "BS-24", true, true);
            var end = finalDungeon.Leave();
            Assert.That(end.Kind, Is.EqualTo(NodeValues.ExitVictory));
            Assert.That(end.End.Victory, Is.True);
            Assert.That(final.RunOver && !_saves.Exists(final.Slot), Is.True, "the climb is over and the slot cleared");
        }
    }
}
