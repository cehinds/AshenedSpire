using System;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
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
    /// WP-17 (D-330 to D-335): the Core 2 shop through the session the screens use. A merchant node opens a persisted stock (three of each kind at ten cinders), a visit can
    /// be resumed from a save with the same stock, buying spends the price and claims the item as a reward is, every refusal says the web's sentence and changes nothing, and leaving
    /// returns to the map. The numbers themselves are held to the web by Core2ShopParityTests. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2ShopSessionTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        private static RunContent Content => Core2HostParityTests.Content;

        private static UiData _ui;

        private static StringTable Strings => (_ui ??= UiData.Load(TestContent.Source)).Strings;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-shop-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        /// <summary>A new climb whose first reachable node is a merchant, entered.</summary>
        private Core2RunSession AtTheShop(uint seed, double cinders = 40)
        {
            var session = Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);
            var node = session.ReachableNodes()[0];
            session.EditRunForReview(r => { r["mapGraph"]["nodes"][node]["type"] = "merchant"; r["cinders"] = cinders; });
            var travel = session.Travel(node);
            Assert.That(travel.Travelled, Is.True);
            Assert.That(travel.Door.Kind, Is.EqualTo("shop"));
            return session;
        }

        [Test]
        public void AMerchantNodeOpensAStockOfThreeOfEachKindAtTenCinders()
        {
            var session = AtTheShop(2024);
            Assert.That(session.HasOpenShop, Is.True);
            Assert.That(session.Location, Is.EqualTo("merchant"));
            Assert.That(session.PendingPort, Is.Null, "the shop is a screen of its own now (VP-9), not a map notice");
            var stock = session.ShopStock;
            Assert.That(stock.Value<double>("price"), Is.EqualTo(10));
            Assert.That(stock.Value<string>("source"), Is.EqualTo("shop"));
            var kinds = stock["options"].Select(o => o.Value<string>("kind")).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
            Assert.That(kinds.Keys, Is.EquivalentTo(new[] { "combatCards", "weaponArts", "weapons", "armor", "relics", "potions" }));
            Assert.That(kinds.Where(k => k.Key != "weaponArts").All(k => k.Value == 3), Is.True, "three of each kind");
            Assert.That(kinds["weaponArts"], Is.InRange(1, 3), "the authored weapon arts a class can reach may be fewer than a shelf");
            Assert.That(session.Run["streamCounters"]?["shop"]?.Value<int>() ?? 0, Is.GreaterThan(0), "the shelves are filled on the shop stream");
            Assert.That(session.ReachableNodes(), Is.Empty, "the map waits while the shop is open");
        }

        [Test]
        public void TheViewHasAShelfPerKindWithPricesReasonsAndTheConfirmation()
        {
            var session = AtTheShop(2024, 25);
            var view = Core2ShopView.Build(session, Strings);
            Assert.That(view.Title, Is.EqualTo("Merchant"));
            Assert.That(view.PurseText, Is.EqualTo("25 cinders"));
            Assert.That(view.Shelves.Select(s => s.Kind), Is.EqualTo(session.ShopStock["options"].Select(o => o.Value<string>("kind")).Distinct()), "the tabs in the order the kinds first appear");
            Assert.That(view.Shelves.Select(s => s.Label), Is.All.Not.Empty);
            Assert.That(view.Shelves.SelectMany(s => s.Offers).Count(), Is.EqualTo(session.ShopStock["options"].Count()));
            foreach (var offer in view.Shelves.SelectMany(s => s.Offers))
            {
                Assert.That(offer.Name, Is.Not.Empty);
                Assert.That(offer.PriceText, Is.EqualTo("10 cinders"));
                Assert.That(offer.BuyText, Is.EqualTo("Buy · 10 cinders"));
                Assert.That(offer.ConfirmMessage, Is.EqualTo("Spend 10 of your 25 cinders (15 left). The item joins your collection."));
                Assert.That(offer.Available, Is.True);
                Assert.That(offer.Reason, Is.Null);
            }
            var poor = AtTheShop(2024, 4);
            var offers = Core2ShopView.Build(poor, Strings).Shelves.SelectMany(s => s.Offers).ToList();
            Assert.That(offers.All(o => !o.Available && o.Reason == "Not enough cinders"), Is.True);
        }

        [Test]
        public void BuyingSpendsThePriceAndClaimsTheItemLikeAReward()
        {
            var session = AtTheShop(2024);
            var stock = session.ShopStock;
            var index = stock["options"].Select((o, i) => (o, i)).First(x => x.o.Value<string>("kind") == "combatCards").i;
            var bought = session.BuyFromShop(index);
            Assert.That(bought.Done, Is.True, bought.Refusal);
            Assert.That(bought.Item.Value<double>("price"), Is.EqualTo(10));
            Assert.That(session.Cinders, Is.EqualTo(30));
            var run = session.Run;
            Assert.That(run["sideboard"].Select(c => c.Value<string>("instanceId")), Has.Member("reward:shop:" + session.OpenShopId + ":" + index));
            var view = Core2ShopView.Build(session, Strings);
            var offer = view.Shelves.SelectMany(s => s.Offers).First(o => o.Index == index);
            Assert.That(offer.Taken, Is.True);
            Assert.That(offer.Available, Is.False);
            Assert.That(offer.Reason, Is.EqualTo("Unavailable"));
            Assert.That(session.BuyFromShop(index).Refusal, Is.EqualTo("Reward offer is missing or already claimed"));
            Assert.That(session.Cinders, Is.EqualTo(30), "a refusal spends nothing");
        }

        [Test]
        public void EveryRefusalSaysTheWebsSentenceAndChangesNothing()
        {
            var session = AtTheShop(2024, 9);
            var before = session.StateHash();
            Assert.That(session.BuyFromShop(0).Refusal, Is.EqualTo("Insufficient cinders"));
            Assert.That(session.BuyFromShop(99).Refusal, Is.EqualTo("Insufficient cinders"), "the purse is checked first");
            session.EditRunForReview(r => r["cinders"] = 100);
            before = session.StateHash();
            Assert.That(session.BuyFromShop(99).Refusal, Is.EqualTo("Invalid reward option"));
            Assert.That(session.BuyFromShop(-1).Refusal, Is.EqualTo("Invalid reward option"));
            Assert.That(session.StateHash(), Is.EqualTo(before));
            session.EditRunForReview(r => r["gameProfileRewardOffers"][("shop:" + r["gameProfileOpenShop"])]["price"] = "free");
            Assert.That(session.BuyFromShop(0).Refusal, Is.EqualTo("Invalid configured shop price"));
            var closed = AtTheShop(5, 100);
            closed.LeaveShop();
            Assert.That(closed.BuyFromShop(0).Refusal, Is.EqualTo("There is no shop open"));
        }

        [Test]
        public void APotionIsRefusedWhenTheSlotsAreFull()
        {
            var session = AtTheShop(2024, 500);
            var index = session.ShopStock["options"].Select((o, i) => (o, i)).First(x => x.o.Value<string>("kind") == "potions").i;
            session.EditRunForReview(r => r["flasks"] = new JArray(Enumerable.Range(0, 20).Select(_ => (JToken)new JObject { ["flaskId"] = "x" })));
            var view = Core2ShopView.Build(session, Strings);
            Assert.That(view.Shelves.SelectMany(s => s.Offers).First(o => o.Index == index).Available, Is.False);
            var before = session.StateHash();
            Assert.That(session.BuyFromShop(index).Refusal, Is.EqualTo("Potion slots are full"));
            Assert.That(session.StateHash(), Is.EqualTo(before));
        }

        [Test]
        public void AVisitResumesFromASaveWithTheSameStockAndLeavingGoesBackToTheMap()
        {
            var session = AtTheShop(31337);
            var stock = session.ShopStock;
            Assert.That(session.BuyFromShop(0).Done, Is.True);
            var resumed = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(resumed.Status, Is.EqualTo(Core2RunLoadStatus.Resumed));
            var again = resumed.Session;
            Assert.That(again.StateHash(), Is.EqualTo(session.StateHash()));
            Assert.That(again.Location, Is.EqualTo("merchant"));
            Assert.That(again.ShopStock["options"].ToString(), Is.EqualTo(stock["options"].ToString()), "the same stock");
            Assert.That(again.ShopStock["taken"][0].Value<int>(), Is.EqualTo(0));
            Assert.That(again.BuyFromShop(1).Done, Is.True);
            Assert.That(again.LeaveShop().Done, Is.True);
            Assert.That(again.HasOpenShop, Is.False);
            Assert.That(again.Location, Is.EqualTo("map"));
            Assert.That(again.ReachableNodes(), Is.Not.Empty);
            Assert.That(again.Run["gameProfileRewardOffers"]["shop:" + stock.Value<string>("id").Substring("shop:".Length)]["taken"].Count(), Is.EqualTo(2), "what was taken stays taken");
            var map = Core2RunSession.Load(Content, _saves, 1, () => Epoch).Session;
            Assert.That(map.Location, Is.EqualTo("map"));
        }

        [Test]
        public void ThePlaceholderContinueLeavesTheShop()
        {
            var session = AtTheShop(2024);
            session.DismissPort();
            Assert.That(session.HasOpenShop, Is.False);
            Assert.That(session.PendingPort, Is.Null);
            Assert.That(session.Location, Is.EqualTo("map"));
        }

        [Test]
        public void AProfileWithoutShopsGoesStraightBackToTheMap()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 77, "reaver", "Aldric", () => Epoch);
            var node = session.ReachableNodes()[0];
            session.EditRunForReview(r =>
            {
                r["mapGraph"]["nodes"][node]["type"] = "merchant";
                var profile = (JObject)r["gameProfile"].DeepClone();
                ((JObject)profile["modules"]).Remove("shops");
                r["gameProfile"] = profile;
            });
            var travel = session.Travel(node);
            Assert.That(travel.Travelled, Is.True);
            Assert.That(session.HasOpenShop, Is.False);
            Assert.That(travel.NotYetPorted, Is.False);
            Assert.That(session.Location, Is.EqualTo("map"));
        }
    }
}
