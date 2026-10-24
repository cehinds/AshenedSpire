using System;
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
    /// VP-8 and VP-9 (D-950 to D-953): the rest screen's and the shop screen's logic over the sessions the screens use, held without the editor. The rest door says its status, the arrival line and
    /// the recovery preview; a service opens its modal or refuses with its reason; the Smith lists the items it can upgrade, shows the one chosen (stat rows, requirements, the deck cards it changes,
    /// the recovery), asks once more and takes the tier; Level Up spends a point; Continue leaves. The shop lists a tab per shelf with its offers and prices, inspects an item, asks before
    /// spending with the purse before and after, refuses what cannot be bought at the control and leaves. Both are routed from the run's location and resume from a save. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RestShopFlowTests
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
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-restshop-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession AtTheRest(uint seed = 2024, Action<JObject> edit = null)
        {
            var session = Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);
            var node = session.ReachableNodes()[0];
            session.EditRunForReview(r =>
            {
                r["mapGraph"]["nodes"][node]["type"] = "shrine";
                r["hp"] = 3;
                r["mana"] = 0;
                r["stamina"] = 1;
                r["flaskCharges"]["hpCurrent"] = 0;
                edit?.Invoke(r);
            });
            Assert.That(session.Travel(node).Travelled, Is.True);
            return session;
        }

        private Core2RunSession AtTheShop(uint seed = 2024, double cinders = 40)
        {
            var session = Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);
            var node = session.ReachableNodes()[0];
            session.EditRunForReview(r => { r["mapGraph"]["nodes"][node]["type"] = "merchant"; r["cinders"] = cinders; });
            Assert.That(session.Travel(node).Travelled, Is.True);
            return session;
        }

        // ------------------------------------------------------------------ the rest door

        [Test]
        public void TheRestDoorSaysItsStatusTheArrivalHealAndAnUnmovingRecoveryPreview()
        {
            var session = AtTheRest();
            var flow = new Core2RestFlow(session, Ui);
            Assert.That(flow.Eyebrow, Is.EqualTo("Rest"));
            Assert.That(flow.Title, Is.EqualTo("Shrine of Ember"));
            Assert.That(flow.Status, Is.EqualTo("1 of 1 available"));
            Assert.That(flow.Healed, Does.StartWith("Healed +"));
            Assert.That(flow.Healed, Does.EndWith("Flasks refilled."));
            Assert.That(flow.Line, Is.EqualTo("Use any service here, all or none, then Continue."));
            Assert.That(flow.RecoveryHeading, Is.EqualTo("Recovery preview"));
            Assert.That(flow.Recovery.Select(r => r.Label), Is.EqualTo(new[] { "HP", "MP", "SP", "Healing flasks", "MP flasks" }));
            Assert.That(flow.Recovery.All(r => !r.Gained && r.Gain == "—" && r.Before == r.After), Is.True, "the arrival heal has already filled every pool");
            Assert.That(flow.Services.Select(s => s.Title), Is.EqualTo(new[] { "Smith" }));
            Assert.That(flow.Services[0].Description, Is.EqualTo("Upgrade one piece of equipment for free. Does not end the visit."));
            Assert.That(flow.ContinueText, Is.EqualTo("Continue"));
            Assert.That(flow.Modal, Is.EqualTo(Core2RestModal.None));
        }

        [Test]
        public void TheSmithListsItsItemsShowsTheOneChosenAndAsksBeforeTakingTheTier()
        {
            var session = AtTheRest();
            var flow = new Core2RestFlow(session, Ui);
            Assert.That(flow.OpenService("smith").Done, Is.True);
            Assert.That(flow.Modal, Is.EqualTo(Core2RestModal.Smith));
            Assert.That(flow.ModalTitle, Is.EqualTo("Upgrade an Item"));
            Assert.That(flow.ModalStatus, Does.Contain("Smithing Stone"));
            Assert.That(flow.Instruction, Is.EqualTo("Choose one owned item. Review its exact changes, requirements, and Stone cost."));
            Assert.That(flow.BackLabel, Is.EqualTo("Back to Shrine"));
            var row = flow.SmithRows.Single();
            Assert.That(row.Name, Is.EqualTo("Greatsword"));
            Assert.That(row.Tier, Is.EqualTo("Tier 0 → 1"));
            Assert.That(row.Count, Is.EqualTo("×1 owned"));
            Assert.That(flow.SmithDetail, Is.Null, "nothing is chosen yet");
            Assert.That(flow.ConfirmLabel, Is.EqualTo("Select an item"));
            Assert.That(flow.RequestUpgrade().Refusal, Is.EqualTo("Select an item"), "Upgrade with nothing chosen refuses");
            Assert.That(flow.SelectSmith("armament/nothing").Done, Is.False);
            Assert.That(flow.SelectSmith(row.ItemRef).Done, Is.True);
            var detail = flow.SmithDetail;
            Assert.That(detail.Name, Is.EqualTo("Greatsword"));
            Assert.That(detail.Meta, Is.EqualTo("Tier 0 → 1 · ×1 owned"));
            Assert.That(detail.Changes, Is.Not.Empty, "the item's own stat rows");
            Assert.That(detail.Changes.All(c => c.Before != c.After), Is.True);
            Assert.That(detail.Cards.Any(c => c.Label.StartsWith("Strike")), Is.True, "the worn weapon's deck cards change");
            Assert.That(detail.Cards.All(c => c.Before != c.After), Is.True);
            Assert.That(detail.CostLine, Does.StartWith("Smithing Stone Cost: 0 / "));
            Assert.That(flow.ConfirmLabel, Is.EqualTo("Upgrade (0)"));
            Assert.That(flow.ModalNote, Is.EqualTo("Review the upgrade before confirming. Your other choices and Continue stay available."));
            Assert.That(flow.RequestUpgrade().Done, Is.True);
            Assert.That(flow.Modal, Is.EqualTo(Core2RestModal.SmithReview));
            Assert.That(flow.ReviewQuestion, Is.EqualTo("Upgrade Greatsword?"));
            Assert.That(flow.ReviewMessage, Is.EqualTo("Tier 0 becomes tier 1. This spends the shown Stone cost and stays at this site."));
            Assert.That(flow.ReviewBadge, Is.EqualTo("PERMANENT FOR THIS RUN"));
            Assert.That(session.Run["itemUpgradeLevels"]?.Value<double?>("armament/greatsword") ?? 0, Is.EqualTo(0), "nothing is taken until the question is answered");
            Assert.That(flow.BackToSmith().Done, Is.True);
            Assert.That(flow.Modal, Is.EqualTo(Core2RestModal.Smith));
            Assert.That(flow.RequestUpgrade().Done, Is.True);
            var done = flow.CommitUpgrade();
            Assert.That(done.Done, Is.True, done.Refusal);
            Assert.That(flow.Modal, Is.EqualTo(Core2RestModal.None));
            Assert.That(session.Run["itemUpgradeLevels"].Value<double>("armament/greatsword"), Is.EqualTo(1));
            Assert.That(flow.Services[0].Used, Is.True);
            Assert.That(flow.Services[0].Description, Is.EqualTo("Done at this site."));
            Assert.That(flow.Line, Is.EqualTo("Smith done. Rest complete; Continue when ready."));
        }

        [Test]
        public void ASpentServiceRefusesAtTheCardWithItsReasonAndChangesNothing()
        {
            var session = AtTheRest();
            var flow = new Core2RestFlow(session, Ui);
            flow.OpenService("smith");
            flow.SelectSmith(flow.SmithRows[0].ItemRef);
            flow.RequestUpgrade();
            flow.CommitUpgrade();
            var before = session.StateHash();
            var again = flow.OpenService("smith");
            Assert.That(again.Done, Is.False);
            Assert.That(again.Refusal, Is.EqualTo("Done at this site."));
            Assert.That(flow.Modal, Is.EqualTo(Core2RestModal.None));
            Assert.That(flow.OpenService("bogus").Done, Is.False);
            Assert.That(session.StateHash(), Is.EqualTo(before));
        }

        [Test]
        public void LevelUpListsTheAttributesAndSpendsOnePointOnTheOneChosen()
        {
            var session = AtTheRest(2024, r => r["level"]["unspentPoints"] = 1);
            var flow = new Core2RestFlow(session, Ui);
            Assert.That(flow.Services.Select(s => s.Title), Is.EqualTo(new[] { "Smith", "Level Up" }));
            Assert.That(flow.Line, Is.EqualTo("Smith and Level Up are both free here; use either, both or neither, then Continue."));
            Assert.That(flow.OpenService("levelUp").Done, Is.True);
            Assert.That(flow.Modal, Is.EqualTo(Core2RestModal.LevelUp));
            Assert.That(flow.ModalTitle, Is.EqualTo("Level Up"));
            Assert.That(flow.ModalNote, Is.EqualTo("Choose one attribute for one earned point. Confirming keeps you at this site."));
            Assert.That(flow.ConfirmLabel, Is.EqualTo("Assign point"));
            Assert.That(flow.LevelRows, Is.Not.Empty);
            Assert.That(flow.ConfirmLevel().Refusal, Is.EqualTo("Select one first."), "nothing chosen refuses at the control");
            var strength = flow.LevelRows.First(r => r.Id == "strength");
            Assert.That(flow.SelectAttribute("strength").Done, Is.True);
            Assert.That(flow.LevelRows.First(r => r.Id == "strength").After, Is.Not.EqualTo(strength.Value), "the chosen row shows its value after the point");
            Assert.That(flow.SelectAttribute("strength").Done, Is.True);
            Assert.That(flow.LevelSelected, Is.Null, "pressing the chosen row takes the point back");
            flow.SelectAttribute("strength");
            var before = session.Run["attributes"].Value<double>("strength");
            var done = flow.ConfirmLevel();
            Assert.That(done.Done, Is.True, done.Refusal);
            Assert.That(session.Run["attributes"].Value<double>("strength"), Is.EqualTo(before + 1));
            Assert.That(flow.Modal, Is.EqualTo(Core2RestModal.None));
            Assert.That(flow.Services.Where(s => s.Id == "levelUp").All(s => s.Used || !s.Available), Is.True, "the point is spent (the service is no longer offered, or reads used)");
            Assert.That(flow.Services.First(s => s.Id == "smith").Available, Is.True, "the Smith stays open after Level Up");
        }

        [Test]
        public void AModalClosesWithoutTakingAnythingAndContinueLeavesForTheMap()
        {
            var session = AtTheRest();
            var flow = new Core2RestFlow(session, Ui);
            flow.OpenService("smith");
            var before = session.StateHash();
            Assert.That(flow.Leave().Done, Is.True, "Continue is inert while a modal is open");
            Assert.That(session.IsResting, Is.True);
            Assert.That(flow.CloseModal().Done, Is.True);
            Assert.That(flow.Modal, Is.EqualTo(Core2RestModal.None));
            Assert.That(session.StateHash(), Is.EqualTo(before));
            var left = flow.Leave();
            Assert.That(left.Left, Is.True);
            Assert.That(session.IsResting, Is.False);
            Assert.That(session.Location, Is.EqualTo("map"));
            Assert.That(session.PendingPort, Is.Null);
        }

        [Test]
        public void AnOpenSiteIsRoutedToTheRestScreenAndAShopToTheShopScreen()
        {
            var rest = AtTheRest();
            Assert.That(rest.Location, Is.EqualTo(RunFlowValues.LocationRest));
            Assert.That(rest.Content.Flow.Core2ScreenFor(rest.Location), Is.EqualTo(ScreenIds.Core2Rest));
            Assert.That(Ui.Screens.IsBuilt(ScreenIds.Core2Rest), Is.True);
            var shop = AtTheShop();
            Assert.That(shop.Location, Is.EqualTo(RunFlowValues.LocationMerchant));
            Assert.That(shop.Content.Flow.Core2ScreenFor(shop.Location), Is.EqualTo(ScreenIds.Core2Shop));
            Assert.That(Ui.Screens.IsBuilt(ScreenIds.Core2Shop), Is.True);
            Assert.That(shop.PendingPort, Is.Null, "the placeholder notice is gone");
        }

        // ------------------------------------------------------------------ the shop

        [Test]
        public void TheShopListsATabPerShelfInStockOrderWithPricesAndThePurse()
        {
            var session = AtTheShop();
            var flow = new Core2ShopFlow(session, Ui);
            Assert.That(flow.Title, Is.EqualTo("Merchant"));
            Assert.That(flow.PurseText, Is.EqualTo("40 cinders"));
            Assert.That(flow.LeaveText, Is.EqualTo("Leave"));
            Assert.That(flow.Tabs.Count, Is.EqualTo(6));
            Assert.That(flow.Tabs.Select(t => t.Label), Is.EquivalentTo(new[] { "CARDS", "WEAPON ARTS", "ARMAMENTS", "ARMOUR", "RELICS", "FLASKS" }));
            Assert.That(flow.Tabs.Count(t => t.Selected), Is.EqualTo(1));
            Assert.That(flow.Items, Is.Not.Empty);
            Assert.That(flow.Heading, Is.EqualTo(flow.Tabs.First(t => t.Selected).Label));
            Assert.That(flow.Items.All(i => i.PriceText == "10 cinders" && i.Available && i.Reason == string.Empty), Is.True);
            Assert.That(flow.BuyLabel, Is.EqualTo("Buy"));
            foreach (var tab in flow.Tabs.ToList())
            {
                Assert.That(flow.SelectShelf(tab.Kind).Done, Is.True);
                Assert.That(flow.ActiveKind, Is.EqualTo(tab.Kind));
                Assert.That(flow.Items.All(i => i.Face.Kind == tab.Kind), Is.True, tab.Label);
            }
        }

        [Test]
        public void EveryShelfOffersFacesAndTheCardsAreCardFaces()
        {
            var session = AtTheShop();
            var flow = new Core2ShopFlow(session, Ui);
            foreach (var item in flow.AllItems)
            {
                Assert.That(item.Name, Is.Not.Empty);
                Assert.That(item.Face.KindLabel, Is.Not.Empty, item.Face.Kind);
                if (item.Face.IsCard) Assert.That(item.Face.Card.Name, Is.EqualTo(item.Name));
            }
            Assert.That(flow.AllItems.Where(i => i.Face.Kind == "relics").All(i => i.Face.ArtId != null && i.Face.Effect.Length > 0), Is.True, "a relic has its picture and its sentence");
            var weapon = flow.AllItems.First(i => i.Face.Kind == "weapons");
            Assert.That(weapon.Face.Stats, Is.Not.Empty, "an armament says its numbers");
        }

        [Test]
        public void InspectOpensTheItemAndBackClosesItWithoutSpending()
        {
            var session = AtTheShop();
            var flow = new Core2ShopFlow(session, Ui);
            var item = flow.Items[0];
            var before = session.StateHash();
            Assert.That(flow.Inspect(item.Index).Done, Is.True);
            Assert.That(flow.Stage, Is.EqualTo(Core2ShopStage.Inspect));
            Assert.That(flow.Focus.Index, Is.EqualTo(item.Index));
            Assert.That(flow.InspectLabel, Is.EqualTo("Inspect"));
            Assert.That(flow.BackLabel, Is.EqualTo("Back"));
            Assert.That(flow.SelectShelf(flow.Tabs.Last().Kind).Done, Is.True);
            Assert.That(flow.ActiveKind, Is.EqualTo(flow.Tabs.First().Kind), "the shelves are behind the inspection");
            Assert.That(flow.CloseOverlay().Done, Is.True);
            Assert.That(flow.Stage, Is.EqualTo(Core2ShopStage.Shelf));
            Assert.That(session.StateHash(), Is.EqualTo(before));
        }

        [Test]
        public void BuyAsksWithThePurseBeforeAndAfterThenSpendsAndTheItemReadsUnavailable()
        {
            var session = AtTheShop(2024, 40);
            var flow = new Core2ShopFlow(session, Ui);
            var item = flow.Items[0];
            Assert.That(flow.RequestBuy(item.Index).Done, Is.True);
            Assert.That(flow.Stage, Is.EqualTo(Core2ShopStage.Confirm));
            Assert.That(flow.ConfirmQuestion, Is.EqualTo("Buy this item?"));
            Assert.That(flow.ConfirmMessage, Is.EqualTo("Spend 10 of your 40 cinders (30 left). The item joins your collection."));
            Assert.That(flow.ConfirmButton, Is.EqualTo("BUY IT"));
            Assert.That(session.Cinders, Is.EqualTo(40), "nothing is spent until the question is answered");
            Assert.That(flow.CloseOverlay().Done, Is.True);
            Assert.That(session.Cinders, Is.EqualTo(40));
            flow.RequestBuy(item.Index);
            var bought = flow.ConfirmBuy();
            Assert.That(bought.Done, Is.True, bought.Refusal);
            Assert.That(session.Cinders, Is.EqualTo(30));
            Assert.That(flow.PurseText, Is.EqualTo("30 cinders"));
            Assert.That(flow.Stage, Is.EqualTo(Core2ShopStage.Shelf));
            var after = flow.Items.First(i => i.Index == item.Index);
            Assert.That(after.Taken, Is.True);
            Assert.That(after.Available, Is.False);
            Assert.That(after.Reason, Is.EqualTo("Unavailable"));
            var hash = session.StateHash();
            Assert.That(flow.RequestBuy(item.Index).Refusal, Is.EqualTo("Unavailable"), "a taken offer refuses at the control");
            Assert.That(session.StateHash(), Is.EqualTo(hash));
        }

        [Test]
        public void ABuyThePurseCannotCoverRefusesAtTheControlAndChangesNothing()
        {
            var session = AtTheShop(2024, 5);
            var flow = new Core2ShopFlow(session, Ui);
            var item = flow.Items[0];
            Assert.That(item.Available, Is.False);
            Assert.That(item.Reason, Is.EqualTo("Not enough cinders"));
            var before = session.StateHash();
            var step = flow.RequestBuy(item.Index);
            Assert.That(step.Done, Is.False);
            Assert.That(step.Refusal, Is.EqualTo("Not enough cinders"));
            Assert.That(flow.Stage, Is.EqualTo(Core2ShopStage.Shelf));
            Assert.That(session.StateHash(), Is.EqualTo(before));
        }

        [Test]
        public void LeaveReturnsToTheMapAndASavedShopResumesWithTheSameStock()
        {
            var session = AtTheShop(2024, 40);
            var flow = new Core2ShopFlow(session, Ui);
            var item = flow.Items[0];
            flow.RequestBuy(item.Index);
            flow.ConfirmBuy();
            session.Save();
            var again = Core2RunSession.Load(Content, _saves, 1, () => Epoch).Session;
            Assert.That(again.Location, Is.EqualTo(RunFlowValues.LocationMerchant), "Continue resumes at the shop");
            var resumed = new Core2ShopFlow(again, Ui);
            Assert.That(resumed.AllItems.Select(i => i.Name), Is.EqualTo(flow.AllItems.Select(i => i.Name)));
            Assert.That(resumed.AllItems.First(i => i.Index == item.Index).Taken, Is.True, "what was bought stays bought");
            Assert.That(resumed.PurseText, Is.EqualTo("30 cinders"));
            resumed.RequestBuy(resumed.Items.First(i => i.Available).Index);
            Assert.That(resumed.Leave().Done, Is.True, "Leave is inert while the question is open");
            Assert.That(again.HasOpenShop, Is.True);
            resumed.CloseOverlay();
            var left = resumed.Leave();
            Assert.That(left.Left, Is.True);
            Assert.That(again.HasOpenShop, Is.False);
            Assert.That(again.Location, Is.EqualTo(RunFlowValues.LocationMap));
            Assert.That(again.PendingPort, Is.Null);
        }
    }
}
