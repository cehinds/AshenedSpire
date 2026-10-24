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
    /// WP-C1b (D-310 to D-313): what the Unity screens read of a Core 2 run, held without the editor. The act map over the Core 2 map (the display types the fog shows, a rest depth between lanes),
    /// the run HUD, the notice for a place whose rules are a later work package, the run end of a Core 2 death (its deck named from the profile's cards, the class it opened), the classes W-04
    /// offers under each ruleset, and the data that routes a Core 2 run's places to screens. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2ScreensTests
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
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-screens-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession New(uint seed = 4321) => Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);

        // ------------------------------------------------------------------ the act map

        [Test]
        public void TheActMapOfACore2RunIsDrawnFromItsMapWithTheDisplayTypesTheGraphCarries()
        {
            var session = New();
            var view = ActMapView.Build(session, Ui);
            var graph = session.Run.Obj("mapGraph").Obj("nodes");
            Assert.That(view.Nodes.Count, Is.EqualTo(graph.Count), "a node per map node");
            Assert.That(view.Nodes.Select(n => n.Id), Is.EquivalentTo(graph.Properties().Select(p => p.Name)));
            foreach (var node in view.Nodes)
            {
                var json = (JObject)graph[node.Id];
                Assert.That(node.Kind, Is.EqualTo(json.Value<string>("type")), node.Id + " shows the display type (a fogged node reads as an event)");
                Assert.That(node.Label, Is.Not.Empty, node.Id);
                Assert.That(node.Col, Is.EqualTo(json.Value<double>("col")), node.Id + " lane");
            }
            Assert.That(view.Nodes.Where(n => n.Reachable).Select(n => n.Id), Is.EqualTo(session.ReachableNodes()), "the opening row is reachable");
            Assert.That(view.Title, Does.Contain("1"));
            Assert.That(view.Floors, Is.GreaterThan(1));
            Assert.That(view.Legend, Is.Not.Empty);
        }

        [Test]
        public void ARestDepthBetweenLanesKeepsItsHalfColumn()
        {
            var session = New();
            // An even profile width puts a shared node half a lane in (CoreRoutesVocabulary.Centre); the view keeps the fraction.
            session.EditRunForReview(r =>
            {
                var graph = (JObject)r["mapGraph"];
                graph["columns"] = 4;
                foreach (var p in ((JObject)graph["nodes"]).Properties().Where(p => p.Value.Value<string>("type") == "shrine")) p.Value["col"] = 1.5;
            });
            var view = ActMapView.Build(session, Ui);
            var rest = view.Nodes.First(n => n.Kind == "shrine");
            Assert.That(rest.Col, Is.EqualTo(1.5), "the rest node sits between lanes 1 and 2");
            Assert.That(view.Columns, Is.EqualTo(4));
        }

        [Test]
        public void TheRunHudOfACore2RunNamesTheCharacterAndTheirPools()
        {
            var session = New();
            var hud = RunHudView.Build(session, Ui);
            Assert.That(hud.Identity, Does.Contain("Aldric"));
            Assert.That(hud.MaxHp, Is.EqualTo((int)session.Run.Value<double>("maxHp")));
            Assert.That(hud.Cinders, Is.Not.Empty);
            Assert.That(hud.Deck, Is.Not.Empty);
            Assert.That(hud.Trail, Is.Not.Empty);
        }

        // ------------------------------------------------------------------ places whose rules are a later work package

        [Test]
        public void APlaceNotYetPortedShowsAClearNoticeWithContinueAndTheRunGoesOn()
        {
            var session = New();
            // Stand at a second-depth node: the rest depth is next.
            session.EditRunForReview(r => r["mapNodeId"] = "n2_1");
            var next = session.ReachableNodes();
            var graph = session.Run.Obj("mapGraph").Obj("nodes");
            var rest = next.First(id => graph.Obj(id).Value<string>("type") == "shrine");
            var travel = session.Travel(rest);
            Assert.That(travel.Travelled, Is.True);
            Assert.That(travel.NotYetPorted, Is.True);
            Assert.That(session.PendingPort, Is.EqualTo("rest"));
            var notice = Core2PortView.Build(session, Ui);
            Assert.That(notice.Title, Is.Not.Empty);
            Assert.That(notice.Body, Does.Contain("not yet available"), "a clear notice");
            Assert.That(notice.ActionText, Is.Not.Empty);
            Assert.That(notice.Title, Is.Not.EqualTo(notice.Body));
            session.DismissPort();
            session.Save();
            Assert.That(session.PendingPort, Is.Null);
            Assert.That(session.ReachableNodes(), Is.Not.Empty, "the climb goes on from the place");
            foreach (var kind in new[] { Core2HostValues.DoorRest, Core2HostValues.DoorShop, Core2HostValues.DoorTreasure, Core2HostValues.DoorEvent, Core2HostValues.PortRewards })
            {
                Assert.That(Ui.Strings.Has(string.Format(System.Globalization.CultureInfo.InvariantCulture, Core2UiFormats.PortTitleKey, kind)), Is.True, kind + " title");
                Assert.That(Ui.Strings.Get(string.Format(System.Globalization.CultureInfo.InvariantCulture, Core2UiFormats.PortBodyKey, kind)), Does.Contain("not yet available"), kind + " body");
            }
        }

        // ------------------------------------------------------------------ the run end

        [Test]
        public void ACore2DeathReachesTheRunEndWithTheDeckNamedTheKillerAndTheClassItOpened()
        {
            var session = New(31337);
            session.Travel(session.ReachableNodes()[0]);
            session.EditRunForReview(r => { r["hp"] = 1; r["maxHp"] = 1; });
            session.StartEncounter(session.EncounterId);
            var flow = new Core2CombatFlow(session, Ui.Strings);
            for (var guard = 0; guard < 60 && !flow.Ended; guard++)
            {
                if (flow.Prompt?.Kind == Core2UiValues.PromptOffer) flow.DeclineOffer();
                else if (flow.Prompt != null) { var o = flow.Prompt.Options.FirstOrDefault(x => x.Enabled); if (o != null) flow.Choose(o); else flow.Back(); }
                else flow.EndTurn();
            }
            Assert.That(session.RunOver, Is.True);
            var end = RunEndView.Build(session, Ui);
            Assert.That(end.Victory, Is.False);
            Assert.That(end.Title, Is.Not.Empty);
            Assert.That(end.Deck, Is.Not.Empty);
            Assert.That(end.Deck.All(line => !line.Contains("core.")), Is.True, "cards are named from the profile's rows, not their ids");
            Assert.That(end.Stats.Any(s => s.Contains(session.LastFoes()[0])), Is.True, "the killer is named");
            Assert.That(end.Unlocks.Count, Is.EqualTo(session.ClassesUnlocked.Count), "a class the run opened is listed");
            Assert.That(end.Unlocks, Is.Not.Empty);
            Assert.That(session.Region, Is.Not.Null);
        }

        // ------------------------------------------------------------------ W-04 under each ruleset

        [Test]
        public void TheClassesOfNewGameAreEachRulesetsOwnAndACore2ClassStaysLockedUntilTheLedgerOpensIt()
        {
            var profile = ProfileStore.Load(_saves).Doc;
            var legacy = CreationClasses.For(Content, RunFlowValues.RulesetLegacy, profile);
            Assert.That(legacy.IsCore2, Is.False);
            Assert.That(legacy.Ids, Is.EqualTo(Content.ClassIds));
            Assert.That(legacy.Locked("reaver"), Is.False);

            var core2 = CreationClasses.For(Content, RunFlowValues.RulesetCore2, profile);
            Assert.That(core2.Ids, Is.EqualTo(Content.Core2.ClassIds));
            Assert.That(core2.Locked(core2.Ids[0]), Is.False, "the first class is open");
            Assert.That(core2.Ids.Skip(1).All(core2.Locked), Is.True, "the others wait for finished runs");
            Assert.That(core2.Problem(core2.Ids[1]), Is.Not.Null);
            Assert.That(core2.Problem(core2.Ids[0]), Is.Null);
            Assert.That(core2.Default(), Is.EqualTo(core2.Ids[0]));
            foreach (var id in core2.Ids)
            {
                var preview = core2.Preview(id);
                Assert.That(preview, Is.Not.Null, id + " previews what it would start with, locked or not");
                Assert.That(preview.MaxHp, Is.GreaterThan(0));
                Assert.That(preview.Attributes, Is.Not.Empty);
            }
            Assert.That(core2.Problem("nonsense"), Is.Not.Null);
        }

        // ------------------------------------------------------------------ data: the places of a Core 2 run and their screens

        [Test]
        public void EveryPlaceOfACore2RunRoutesToABuiltScreen()
        {
            var flow = Content.Flow;
            foreach (var location in new[] { RunFlowValues.LocationCombat, RunFlowValues.LocationMap, RunFlowValues.LocationRunEnd })
            {
                var id = flow.Core2ScreenFor(location);
                Assert.That(id, Is.Not.Null, location);
                Assert.That(Ui.Screens.Get(id), Is.Not.Null, id);
                Assert.That(Ui.Screens.IsBuilt(id), Is.True, id + " is built");
            }
            Assert.That(flow.Core2ScreenFor(RunFlowValues.LocationCombat), Is.EqualTo(ScreenIds.Core2Combat));
            Assert.That(flow.Core2ScreenFor("nowhere"), Is.Null);
            Assert.That(flow.NewRunRuleset, Is.EqualTo(RunFlowValues.RulesetCore2));
        }

        [Test]
        public void TheCombatFlowReadsThePlayersCombatPacing()
        {
            var session = New();
            session.Travel(session.ReachableNodes()[0]);
            var flow = new Core2CombatFlow(session, Ui.Strings);
            Assert.That(flow.Pacing, Is.Not.Null);
            Assert.That(flow.Pacing.StageGapScaledMs, Is.GreaterThan(0));
            Assert.That(flow.Pacing.AnimationStepScaledMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(Ui.Components.FighterRatings, Is.EqualTo(new[] { Core2UiValues.RatingEvade, Core2UiValues.RatingPoise, Core2UiValues.RatingWard, Core2UiValues.RatingBlock, Core2UiValues.RatingHp }),
                "the fighter HUD order (Evade, Poise, Ward, Block, HP)");
            Assert.That(Ui.Components.LogLines, Is.GreaterThan(0));
        }
    }
}
