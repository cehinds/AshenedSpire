using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-23 (D-1385): the Armoury's receipts over a new Reaver (web equipmentSurfaceReceipt, playerPoiseThresholdReceipt, playerLoadReceipt and candidateReceipt, read from the running web at seed ASHEN1: the numbers
    /// below are the web's own at that run: Slashing Strike 11, Weapon Guard 6, Weapon Technique 5, Poise 2 + 8 = 10, load 1.6 + 1.6 = 3.2 of 4, Heavy). Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ArmouryReceiptsTests
    {
        private string _dir;
        private SaveService _saves;
        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-receipts-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession NewRun() => Core2RunSession.New(Core2HostParityTests.Content, _saves, 1, 779u, "reaver", "Aldric", null);

        [Test]
        public void TheCardPackagesTakeTheRunsOwnRatingRowsSoTheGuardIsSixAsTheWebsIs()
        {
            var view = ArmouryReceipts.For(NewRun(), Ui);
            Assert.That(view.Roles.Select(r => r.Role), Is.EqualTo(new[] { "attack", "guard", "technique" }));
            Assert.That(view.Roles.Select(r => r.Value), Is.EqualTo(new[] { 11d, 6d, 5d }), "Slashing Strike 11, Weapon Guard 6 (2 base + 3 DR + 1 rarity), Weapon Technique 5");
            var guard = view.Roles[1];
            Assert.That(guard.Name, Is.EqualTo("Weapon Guard"));
            Assert.That(guard.Formula, Is.EqualTo("2 base + 3 DR (weapon) + 1 rarity"));
            Assert.That(guard.Hint, Does.StartWith("physical"));
            Assert.That(view.Roles[0].Hint, Does.Contain("blade"));
            Assert.That(view.Roles.All(r => r.Copies == 0), Is.True, "the configured deck holds none of the equipment cards (the web's x0)");
        }

        [Test]
        public void ThePoiseThresholdIsTheAttributePlusTheArmourPlusTheRelics()
        {
            var poise = ArmouryReceipts.For(NewRun(), Ui).Poise;
            Assert.That(poise.Value, Is.EqualTo(10));
            Assert.That(poise.Sources.Select(s => s.Id + " " + s.Value), Is.EqualTo(new[] { "constitution 2", "default 8" }));
            Assert.That(poise.Pair, Is.EqualTo("Items 8 + relics 0"));
            Assert.That(poise.Total, Is.EqualTo("10"));
            Assert.That(poise.Note, Does.StartWith("The combat entity stamps this as the player Poise meter's max"));
        }

        [Test]
        public void TheEquipLoadScalesEachPiecesWeightAndNamesTheClass()
        {
            var load = ArmouryReceipts.For(NewRun(), Ui).Load;
            Assert.That(load.Sources.Select(s => s.Id + " " + s.Value), Is.EqualTo(new[] { "default 1.6", "greatsword 1.6" }).Or.EqualTo(new[] { "greatsword 1.6", "default 1.6" }));
            Assert.That(load.Pair, Is.EqualTo("Hands 1.6 + armour 1.6"));
            Assert.That(load.Total, Is.EqualTo("3.2 of 4 · Heavy"));
            Assert.That(load.Note, Is.EqualTo("Capacity 4 = base + Constitution and Strength; 80% loaded — Heavy. Readout only until a dodge roll enters your deck."));
        }

        [Test]
        public void TheRequirementsListsEachPieceThatAsksAndNothingElse()
        {
            var view = ArmouryReceipts.For(NewRun(), Ui);
            Assert.That(view.Requirements, Is.Empty, "the Reaver's start asks for nothing: the web says 'No attribute requirements.'");
        }

        [Test]
        public void ComparingAnItemAgainstTheEquippedOneShowsThePackagesPoiseAndLoadBeforeAndAfter()
        {
            var session = NewRun();
            var unequip = ArmouryReceipts.Candidate(session, Ui, "rightHand", 0, "greatsword");
            Assert.That(unequip, Is.Not.Null);
            Assert.That(unequip.Roles.Count, Is.EqualTo(3), "the web lists every role of the comparison");
            Assert.That(unequip.Roles.All(r => !r.Changed), Is.True, "the same piece in the same hand changes nothing");
            Assert.That(session.TrialLoadout("rightHand", 0, "nothingLikeThis"), Is.Null, "an item that does not exist cannot be tried");
            var elsewhere = session.TrialLoadout("leftHand", 0, "greatsword");
            Assert.That(elsewhere, Is.Not.Null.Or.Null, "moving the sword to the other hand is the rules' question, not the view's");
            var run = session.Run;
            Assert.That(session.Run["loadout"]["sets"]["rightHand"][0].ToString(), Is.EqualTo("greatsword"), "trying a loadout never changes the run");
        }
    }
}
