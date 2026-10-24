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
    /// WP-16 (D-334 to D-340): the Core 2 rest site through the session the screens use. A rest node opens a visit that heals on arrival, the Smith upgrades one owned item one tier
    /// for nothing and Level Up spends an earned point (each once, in any order), every refusal says the web's sentence and changes nothing, Continue leaves and the site stays
    /// enterable until the player travels elsewhere, and a save made at the site resumes at the map with that re-entry. The numbers themselves are held to the web by
    /// Core2RestParityTests. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RestSessionTests
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
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-rest-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        /// <summary>A new climb whose first reachable node is a rest site, hurt first, then entered.</summary>
        private Core2RunSession AtTheRest(uint seed, string classId = "reaver", Action<JObject> edit = null)
        {
            var session = Core2RunSession.New(Content, _saves, 1, seed, classId, "Aldric", () => Epoch);
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
            var travel = session.Travel(node);
            Assert.That(travel.Travelled, Is.True);
            Assert.That(travel.Door.Kind, Is.EqualTo("rest"));
            return session;
        }

        [Test]
        public void ArrivalHealsOnceAndTheVisitRemembersWhatItGave()
        {
            var session = AtTheRest(2024);
            Assert.That(session.IsResting, Is.True);
            Assert.That(session.Location, Is.EqualTo("rest"));
            Assert.That(session.PendingPort, Is.EqualTo("rest"));
            var run = session.Run;
            Assert.That(run.Value<double>("hp"), Is.EqualTo(run.Value<double>("maxHp")));
            Assert.That(run["flaskCharges"].Value<double>("hpCurrent"), Is.EqualTo(run["flaskCharges"].Value<double>("hp")));
            var healed = session.RestVisit["healed"];
            Assert.That(healed.Value<double>("hp"), Is.EqualTo(run.Value<double>("maxHp") - 3));
            Assert.That(healed.Value<double>("flaskHp"), Is.GreaterThan(0));
            Assert.That(session.ReachableNodes(), Is.Empty, "the map waits while the site is open");
            var view = session.RestView(Strings);
            Assert.That(view.Healed, Does.StartWith("Healed +"));
            Assert.That(view.Services.Select(s => s.Id), Is.EqualTo(new[] { "smith" }), "Level Up is offered only to a character with points");
            Assert.That(view.Services[0].Available, Is.True);
            Assert.That(view.Status, Is.EqualTo("1 of 1 available"));
            Assert.That(view.Line, Is.EqualTo("Use any service here, all or none, then Continue."));
        }

        [Test]
        public void TheFreeSmithUpgradesOneItemOneTierAndOnlyOnce()
        {
            var session = AtTheRest(2024);
            var view = session.RestView(Strings);
            var item = view.SmithItems.Single();
            Assert.That(item.ItemRef, Is.EqualTo("armament/greatsword"));
            Assert.That(item.Plan.Value<double>("cost"), Is.EqualTo(0), "the Smith is free here");
            Assert.That(item.Available, Is.True);
            var rows = session.SmithCardRows(item.ItemRef);
            Assert.That(rows.Select(r => r.Value<string>("cardId")), Does.Contain("core.strike"), "the worn weapon's cards change");
            var done = session.UpgradeItem(item.ItemRef);
            Assert.That(done.Done, Is.True, done.Refusal);
            var run = session.Run;
            Assert.That(run["itemUpgradeLevels"].Value<double>("armament/greatsword"), Is.EqualTo(1));
            Assert.That(run["lastSmithingReceipt"].Value<bool>("free"), Is.True);
            Assert.That(run["lastSmithingReceipt"].Value<double>("spent"), Is.EqualTo(0));
            Assert.That(run["lastSmithingReceipt"]["affectedCards"], Is.Not.Empty);
            Assert.That(session.RestVisit["used"].Value<bool>("smith"), Is.True);
            var after = session.RestView(Strings);
            Assert.That(after.Services[0].Used, Is.True);
            Assert.That(after.Services[0].Description, Is.EqualTo("Done at this site."));
            Assert.That(after.Line, Is.EqualTo("Smith done. Rest complete; Continue when ready."));
            var before = session.StateHash();
            Assert.That(session.UpgradeItem(item.ItemRef).Refusal, Is.EqualTo("Rest visit is unavailable or spent"));
            Assert.That(session.StateHash(), Is.EqualTo(before), "a refusal changes nothing");
        }

        [Test]
        public void EveryRefusalSaysTheWebsSentenceAndChangesNothing()
        {
            var session = AtTheRest(2024);
            var before = session.StateHash();
            Assert.That(session.UpgradeItem("armament/nothing").Refusal, Is.EqualTo("Item 'armament/nothing' is not an eligible Smithing candidate"));
            Assert.That(session.TakeRestService("bogus", null).Refusal, Is.EqualTo("Unknown rest service 'bogus'"));
            Assert.That(session.TakeRestService("strategize", null).Refusal, Is.EqualTo("Unknown rest service 'strategize'"));
            Assert.That(session.SpendPoint("strength").Refusal, Is.EqualTo("Character leveling is disabled for this profile"));
            Assert.That(session.StateHash(), Is.EqualTo(before));
            session.EditRunForReview(r => r["level"]["unspentPoints"] = 1);
            Assert.That(session.SpendPoint("luck").Refusal, Does.StartWith("levelUp: 'luck' is not an attribute id (strength, dexterity"));
            Assert.That(session.StateHash(), Is.Not.EqualTo(before), "only the edit moved it");
        }

        [Test]
        public void LevelUpSpendsAnEarnedPointAndTheTwoServicesDoNotCloseEachOther()
        {
            var session = AtTheRest(2024, "reaver", r => r["level"]["unspentPoints"] = 1);
            var view = session.RestView(Strings);
            Assert.That(view.Services.Select(s => s.Id), Is.EqualTo(new[] { "smith", "levelUp" }));
            Assert.That(view.Line, Is.EqualTo("Smith and Level Up are both free here; use either, both or neither, then Continue."));
            var strength = session.Run["attributes"].Value<double>("strength");
            var spent = session.SpendPoint("strength");
            Assert.That(spent.Done, Is.True, spent.Refusal);
            var run = session.Run;
            Assert.That(run["attributes"].Value<double>("strength"), Is.EqualTo(strength + 1));
            Assert.That(run["level"].Value<double>("unspentPoints"), Is.EqualTo(0));
            Assert.That(run.Value<double>("levelUps"), Is.EqualTo(1));
            Assert.That(session.UpgradeItem("armament/greatsword").Done, Is.True, "the Smith is still open after Level Up");
            Assert.That(session.SpendPoint("strength").Refusal, Is.EqualTo("Rest visit is unavailable or spent"));
        }

        [Test]
        public void LeavingKeepsTheSiteEnterableUntilTheRunTravelsElsewhere()
        {
            var session = AtTheRest(2024);
            var node = session.Run.Value<string>("mapNodeId");
            Assert.That(session.UpgradeItem("armament/greatsword").Done, Is.True);
            var healed = session.RestVisit["healed"].ToString();
            Assert.That(session.LeaveRest().Done, Is.True);
            Assert.That(session.IsResting, Is.False);
            Assert.That(session.Location, Is.EqualTo("map"));
            Assert.That(session.Run["restRevisit"].Value<bool>("left"), Is.True);
            Assert.That(session.Run["restRevisit"].Value<bool>("spent"), Is.True);
            Assert.That(session.ReachableNodes(), Does.Contain(node), "the left site stays enterable");
            Assert.That(session.Travel(node).Travelled, Is.True);
            Assert.That(session.IsResting, Is.True);
            Assert.That(session.RestVisit["healed"].ToString(), Is.EqualTo(healed), "re-entering heals nothing again");
            Assert.That(session.RestVisit["used"].Value<bool>("smith"), Is.True, "what was spent stays spent");
            Assert.That(session.LeaveRest().Done, Is.True);
            var next = session.ReachableNodes().First(id => id != node);
            session.EditRunForReview(r => r["mapGraph"]["nodes"][next]["type"] = "merchant");
            Assert.That(session.Travel(next).Travelled, Is.True);
            session.LeaveShop();
            Assert.That(session.Run["restRevisit"], Is.Null, "travelling elsewhere ends the re-entry");
            Assert.That(session.LeaveRest().Refusal, Is.EqualTo("There is no rest site open"));
        }

        [Test]
        public void ASaveMadeAtTheSiteResumesAtTheMapWithTheReEntry()
        {
            var session = AtTheRest(31337);
            var node = session.Run.Value<string>("mapNodeId");
            Assert.That(session.UpgradeItem("armament/greatsword").Done, Is.True);
            var resumed = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(resumed.Status, Is.EqualTo(Core2RunLoadStatus.Resumed));
            var again = resumed.Session;
            Assert.That(again.Location, Is.EqualTo("map"), "the web reloads onto the map; the node is still enterable");
            Assert.That(again.IsResting, Is.False);
            Assert.That(again.ReachableNodes(), Does.Contain(node));
            Assert.That(again.Travel(node).Travelled, Is.True);
            Assert.That(again.RestVisit["used"].Value<bool>("smith"), Is.True);
            Assert.That(again.Run.Value<double>("hp"), Is.EqualTo(again.Run.Value<double>("maxHp")));
        }

        [Test]
        public void ThePlaceholderContinueLeavesTheSite()
        {
            var session = AtTheRest(2024);
            session.DismissPort();
            Assert.That(session.IsResting, Is.False);
            Assert.That(session.PendingPort, Is.Null);
            Assert.That(session.Location, Is.EqualTo("map"));
        }

        [Test]
        public void AProfileWithoutRestKeepsTheNoticeAndHealsNothing()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 77, "reaver", "Aldric", () => Epoch);
            var node = session.ReachableNodes()[0];
            session.EditRunForReview(r =>
            {
                r["mapGraph"]["nodes"][node]["type"] = "shrine";
                r["hp"] = 3;
                var profile = (JObject)r["gameProfile"].DeepClone();
                ((JObject)profile["modules"]).Remove("rest");
                r["gameProfile"] = profile;
            });
            var travel = session.Travel(node);
            Assert.That(travel.Travelled, Is.True);
            Assert.That(session.IsResting, Is.False);
            Assert.That(session.Run.Value<double>("hp"), Is.EqualTo(3));
            Assert.That(travel.NotYetPorted, Is.True, "the legacy rest is not part of a Core 2 run");
        }
    }
}
