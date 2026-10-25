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
    /// WP-22 (D-1160..D-1169): the run screens' remaining gaps over a won fight's reward door: the spoils review's Edit deck and Equip now claim the option and hand over to the editor or the Armoury,
    /// the finished run's history row carries the session's damage tally, the Potions list between fights, the Armoury's equipment changes (Unequip, Replace, refusals) and the Combat Power card.
    /// Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Wp22RunScreensTests
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
            _dir = Path.Combine(Path.GetTempPath(), "ashen-wp22-" + Guid.NewGuid().ToString("N"));
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

        private Core2RunSession AtTheDoor(uint seed)
        {
            var session = Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r => { r["maxHp"] = 5000; r["hp"] = 5000; });
            session.StartEncounter(session.EncounterId);
            for (var guard = 0; guard < 400 && session.IsInCombat; guard++) PlayTurn(session);
            Assert.That(session.HasPendingReward, Is.True);
            return session;
        }

        // ------------------------------------------------------------------ the spoils review's Edit deck

        [Test]
        public void EditDeckClaimsTheCardThenProceedLeavesTheSpentOffer()
        {
            var session = AtTheDoor(777);
            var flow = new Core2SpoilsFlow(session, Ui);
            Assume.That(session.DeckEditorRefusal(), Is.Null, "the Core profile opens the editor from the Armoury");
            Assert.That(flow.Note, Does.Contain("edit your deck now"));
            var card = flow.Options.First(o => o.IsCard);
            flow.Select(card.Index);
            flow.Confirm();
            Assert.That(flow.EditDeckLabel, Is.EqualTo("Edit deck"));
            Assert.That(flow.KeepLabel, Is.Empty, "Edit deck takes Keep in collection's place");
            var result = flow.EditDeck();
            Assert.That(result.Done, Is.True);
            Assert.That(result.OpenEditor, Is.True);
            Assert.That(result.Left, Is.False, "the door stays until the editor closes");
            Assert.That(result.EditorFocusKey, Does.StartWith("card:"));
            Assert.That(session.Run["sideboard"].Count(c => ((string)c["instanceId"]).StartsWith("reward")), Is.EqualTo(1), "the card is in the collection");
            var opened = session.OpenDeckEditor(Ui.Strings);
            Assert.That(opened.Opened, Is.True);
            Assert.That(opened.Editor.Model().Collection.Any(t => t.Key == result.EditorFocusKey), Is.True, "the editor lists the card as a tile");
            opened.Editor.Cancel();
            Assert.That(flow.ProceedPending, Is.True);
            Assert.That(flow.Proceed().Left, Is.True, "a take count of one is spent");
            Assert.That(session.HasPendingReward, Is.False);
        }

        // ------------------------------------------------------------------ the record carries the tally (D-962)

        [Test]
        public void AFinishedRunsHistoryRowCarriesTheSessionsDamageTally()
        {
            var session = AtTheDoor(778);
            var dealt = session.Tally.DamageDealt;
            Assert.That(dealt, Is.GreaterThan(0));
            session.Abandon();
            var row = ProfileStore.Load(_saves).Doc.Arr("results").OfType<JObject>().Last();
            Assert.That((double)row["damageDealt"], Is.EqualTo(dealt));
            Assert.That((double)row["damageTaken"], Is.EqualTo(session.Tally.DamageTaken));
        }

        // ------------------------------------------------------------------ potions between fights

        [Test]
        public void ThePotionsListNamesTheChargeFlasksAndShutsDrinkingUntilTheSettingIsOn()
        {
            var session = AtTheDoor(779);
            var rows = Core2RunPotions.Rows(session, Ui.Strings);
            Assert.That(rows.Count(r => r.IsCharge), Is.EqualTo(2));
            var hp = rows.First(r => r.ChargeKind == "hp");
            Assert.That(hp.Name, Is.EqualTo("Crimson Flask"));
            Assert.That(hp.Description, Does.StartWith("Heal 25% of your max HP."));
            Assert.That(hp.Enabled, Is.False);
            Assert.That(hp.Reason, Does.Contain("Use flasks outside combat"));
            Assert.That(session.DrinkPotion("hp"), Is.False);
        }

        // ------------------------------------------------------------------ the Armoury

        [Test]
        public void UnequipAndEquipMoveTheBodyPieceThroughStorageAndRefuseWhatTheRulesRefuse()
        {
            var session = AtTheDoor(780);
            var body = ArmouryPanes.Equipment(session, Ui).First(p => p.SlotId == "armor" && p.Item != null);
            Assert.That(body.UnequipRefusal, Is.Empty);
            var off = session.Equip(Ui.Strings, body.SlotId, body.SetIndex, null, "Unequip");
            Assert.That(off.Ok, Is.True, off.Message);
            Assert.That(off.Message, Does.Contain("unequipped"));
            Assert.That(ArmouryPanes.Equipment(session, Ui).First(p => p.SlotId == "armor").Item, Is.Null);
            var back = session.Equip(Ui.Strings, body.SlotId, body.SetIndex, body.Item.Id, "Equip");
            Assert.That(back.Ok, Is.True, back.Message);
            Assert.That(back.Message, Does.Contain("equipped in"));
            var action = ArmouryInventory.Action(session, Ui, body.Item.Id, body.SlotId, body.SetIndex);
            Assert.That(action.Kind, Is.EqualTo(ArmouryActionKind.Unequip));
            Assert.That(session.EquipRefusal(Ui.Strings, "nowhere", 0, null), Is.Not.Null);
        }

        [Test]
        public void TheCharacterPaneShowsCombatPowerRatingsAndEquipmentCards()
        {
            var session = AtTheDoor(781);
            var view = ArmouryPanes.Character(session, Ui);
            Assert.That(view.Powers.Select(p => p.Label), Is.EqualTo(new[] { "Strike", "Magic", "Defense" }));
            Assert.That(view.PowerSummary, Does.StartWith("Strike "));
            Assert.That(view.Sheet.Select(c => c.Label), Is.EqualTo(new[] { "HP", "SP", "MP", "AR", "PR", "DR", "HandSize", "Draw" }));
            Assert.That(view.EquipmentCards, Is.Not.Empty);
            Assert.That(ArmouryInventory.Rows(session, Ui).Any(r => r.Equipable), Is.True);
        }
    }
}
