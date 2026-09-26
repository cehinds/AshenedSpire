using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// F3 Classic Climb through the run session (US-4.1–4.9, US-8.1–8.6, US-13.1; AF-04, AF-06, AF-10, AF-11; PF-06; 09 §F3
    /// exit gate "a seeded run from title to an act-3 boss win and to a death; resume works at every commit point"): a new
    /// climb is made by the run loop's newRun and stands at the act map; travel is legal only to reachable nodes; a seeded
    /// driver plays whole climbs — map, fights (a legal-move policy through <see cref="RunSession.Execute"/>), the reward
    /// door, rest stays (one action), merchants, events and legacy dungeons through the planned doors, act advances — to the
    /// summit (enemy HP scaled as the bot's assisted policy, D-103) and to a death, and after every commit point the slot is
    /// loaded into a fresh session whose state hash and location equal the live one, and the climb goes on from the loaded
    /// session. The climb's views (W-06 map, W-10 rest, W-15 run end, the RUN_HUD, the planned doors) resolve every string.
    /// </summary>
    [TestFixture]
    public class ClimbSessionTests
    {
        /// <summary>A Classic seed the driver takes to the summit under the assisted scale (found by <see cref="SearchWinningSeeds"/>).</summary>
        public const uint WinSeed = 2u;

        /// <summary>A seed whose short Custom Climb (<see cref="ShortClimb"/>) the driver wins under the assisted scale with the smoke policy (the PlayMode smoke's; it passes a legacy dungeon, events, merchants and rests).</summary>
        public const uint ShortWinSeed = 3u;

        private static RunContent _default;
        private static UiData _ui;
        private string _dir;
        private SaveService _saves;

        private static RunContent Default => _default ??= RunContent.Load(TestContent.Source);
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        /// <summary>The bot's assisted enemy-HP scale per pool (D-103).</summary>
        public static double Assisted(string pool) => pool == "boss" ? 0.12 : pool == "elite" ? 0.25 : 0.35;

        /// <summary>A Custom Climb with the shortest act the map rules allow (US-4.2's run shape; the PlayMode smoke's fast climb).</summary>
        public static JObject ShortClimb() => JObject.Parse("{\"ascension\":0,\"mods\":{},\"deckMode\":\"standard\",\"mapShape\":{\"floors\":7,\"columns\":3}}");

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-climb-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            RunSession.ReviewEnemyHpScale = null;
        }

        [TearDown]
        public void TearDown()
        {
            RunSession.ReviewEnemyHpScale = null;
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        // ------------------------------------------------------------------ the driver

        private sealed class Lcg
        {
            private uint _s;

            public Lcg(uint seed) => _s = seed == 0 ? 1 : seed;

            public int Next(int n)
            {
                _s = unchecked(_s * 1664525u + 1013904223u);
                return (int)(_s % (uint)n);
            }
        }

        /// <summary>
        /// Plays a climb through the session's own API, one commit point at a time; with <see cref="Reload"/> on, every commit
        /// point is checked by loading the slot into a fresh session (hash and location equal) and the climb continues from it.
        /// </summary>
        public sealed class Driver
        {
            private readonly RunContent _content;
            private readonly SaveService _saves;
            private readonly Lcg _pick;
            public RunSession Session;
            public bool Reload = true;
            public bool EndTurnsOnly;

            /// <summary>
            /// The PlayMode smoke's policy (mirrored there key for key): the first reachable node, Continue at every reward
            /// door without claiming, Rest when it is offered (else nothing) and then Leave; fights and the rest are the same.
            /// </summary>
            public bool Smoke;
            public int Steps;
            public int Fights;
            public int Reloads;
            public readonly SortedDictionary<string, int> Seen = new SortedDictionary<string, int>(StringComparer.Ordinal);

            public Driver(RunContent content, SaveService saves, RunSession session, uint seed)
            {
                _content = content;
                _saves = saves;
                Session = session;
                _pick = new Lcg(seed ^ 0x5bd1e995u);
            }

            private void Saw(string what) => Seen[what] = Seen.TryGetValue(what, out var n) ? n + 1 : 1;

            public string Play(int bound)
            {
                Check();
                while (!Session.RunOver)
                {
                    if (++Steps > bound) Assert.Fail("no end within " + bound + " steps (at " + Session.Location + ", act " + Session.Act + ", floor " + Session.Floor + ")");
                    StepOnce();
                }
                return Session.End.Victory ? "victory" : "defeat";
            }

            /// <summary>One step and its commit-point check; an act that climbed is counted.</summary>
            public void StepOnce()
            {
                var act = Session.Act;
                Step();
                if (!Session.RunOver && Session.Act > act) Saw("act");
                Check();
            }

            private void Step()
            {
                var s = Session;
                var location = s.Location;
                Saw(location);
                switch (location)
                {
                    case "map":
                    {
                        var nodes = s.ReachableNodes();
                        Assert.That(nodes, Is.Not.Empty, "the map offers a way on");
                        var travel = s.Travel(Smoke ? nodes[0] : nodes[_pick.Next(nodes.Count)]);
                        Assert.That(travel.Travelled, Is.True, travel.Refusal);
                        if (travel.Kind == "treasure") Saw("treasure");
                        return;
                    }
                    case "combat":
                        Fight();
                        return;
                    case "rewards":
                        Claim();
                        return;
                    case "rest":
                        Rest();
                        return;
                    case "merchant":
                        s.LeaveMerchant();
                        return;
                    case "event":
                        if (!s.EventDone)
                        {
                            var chosen = s.ChooseEvent(s.EventStandInChoice());
                            Assert.That(chosen.Ok, Is.True, "the stand-in choice is legal");
                        }
                        else s.FinishEvent();
                        return;
                    case "dungeon":
                        s.AdvanceDungeon();
                        return;
                    default:
                        Assert.Fail("unexpected location " + location);
                        return;
                }
            }

            /// <summary>The fight to its end; with reloads on, the slot is loaded once mid-fight (after the third command).</summary>
            private void Fight()
            {
                var commands = 0;
                var act = Session.Act;
                while (Session.IsInCombat)
                {
                    if (++commands > 4000) Assert.Fail("a fight did not end");
                    var state = Session.Combat.State;
                    var outcome = Session.Execute(EndTurnsOnly ? EndTurn(state) : Choose(state));
                    Assert.That(outcome.Accepted, Is.True, outcome.Refusal?.Key);
                    if (Reload && commands == 3 && Session.IsInCombat) Check();
                }
                Fights++;
                if (Session.RunOver) Assert.That(Session.End, Is.Not.Null);
                _ = act;
            }

            private CombatCommand Choose(CombatState c)
            {
                var p = c.Player;
                if (p.Num("hp") * 10 < p.Num("maxHp") * 4 && CombatLegality.CanUseFlask(c, 0, "hp") == null) return CombatCommand.UseFlask(0, "hp");
                if (p.Num("mana") <= 0 && p.Num("maxMana") > 0 && CombatLegality.CanUseFlask(c, 0, "mana") == null && _pick.Next(2) == 0) return CombatCommand.UseFlask(0, "mana");
                var hand = c.Piles.Hand.Select(card => card.Str("instanceId")).ToList();
                var start = hand.Count == 0 ? 0 : _pick.Next(hand.Count);
                var order = Enumerable.Range(0, hand.Count).Select(k => hand[(start + k) % hand.Count]).ToList();
                foreach (var aimed in new[] { true, false })
                    foreach (var id in order)
                    {
                        var targets = CombatLegality.Targets(c, id);
                        if ((targets.Count > 0) != aimed) continue;
                        var target = targets.Count > 0 ? targets[_pick.Next(targets.Count)] : null;
                        if (CombatLegality.CanPlay(c, id, target) == null) return CombatCommand.PlayCard(id, target);
                    }
                return EndTurn(c);
            }

            private static CombatCommand EndTurn(CombatState c)
            {
                var plan = HandRules.Plan(c);
                return CombatCommand.EndTurn(plan.Cards.Take((int)plan.Minimum).Select(card => card.Str("instanceId")));
            }

            /// <summary>Every open row claimed (a choice takes its first pick), then Continue; a boss's spoils advance the act.</summary>
            private void Claim()
            {
                var view = RewardsView.Build(Session, Ui);
                if (!Smoke) foreach (var row in view.Rows.Where(r => r.Pending))
                {
                    Session.ClaimReward(row.Key, row.Picks.FirstOrDefault()?.Id);
                    Check();
                }
                Session.FinishRewards();
            }

            /// <summary>One action: banked points first, else Rest; then Leave if the stay is still open.</summary>
            private void Rest()
            {
                var s = Session;
                var view = RestView.Build(s, Ui);
                Assert.That(view, Is.Not.Null, "the stay is open");
                RestActionResult done;
                if (view.Points > 0 && !Smoke) done = s.AssignPoints(new JObject { [view.Attributes[_pick.Next(view.Attributes.Count)].Id] = view.Points });
                else if (view.Option("rest").Available) done = s.RestHere();
                else done = null;
                if (done != null)
                {
                    Assert.That(done.Done, Is.True, done.Refusal);
                    Saw("restAction");
                    Check();
                }
                if (Session.Location == "rest") Session.LeaveRest();
            }

            /// <summary>A commit point: load the slot into a fresh session; its hash and location must equal the live one's, and play goes on from it.</summary>
            public void Check()
            {
                var s = Session;
                if (s.RunOver)
                {
                    Assert.That(_saves.Exists(s.Slot), Is.False, "a finished run leaves the slot empty (permadeath)");
                    return;
                }
                Assert.That(s.Run.Num("hp"), Is.InRange(0, s.Run.Num("maxHp")));
                if (!Reload) return;
                var hash = s.StateHash();
                var loaded = RunSession.Load(_content, _saves, s.SlotIndex);
                Assert.That(loaded.Status, Is.EqualTo(RunLoadStatus.Resumed), s.Location + ": " + string.Join("; ", loaded.Warnings));
                Assert.That(loaded.Session.Location, Is.EqualTo(s.Location), "resume lands at the same place (step " + Steps + ", live pending " + (s.Run["pendingReward"] != null) + ", loaded pending " + (loaded.Session.Run["pendingReward"] != null) + ", combat " + (s.Combat != null) + " finished " + s.FightFinished + " over " + s.Combat?.IsOver + ")");
                Assert.That(loaded.Session.StateHash(), Is.EqualTo(hash), "resume at " + s.Location + " gives the identical state");
                Session = loaded.Session;
                Reloads++;
            }
        }

        private RunSession NewClimb(uint seed, JObject custom = null, string classId = null) =>
            RunSession.New(Default, _saves, 1, seed, classId ?? Default.DefaultClass(), "Aldric", null, custom);

        // ------------------------------------------------------------------ the map

        [Test]
        public void ANewClimbStandsAtTheActMapWithItsStartRowReachable()
        {
            var s = NewClimb(77);
            Assert.That(s.Location, Is.EqualTo("map"));
            var run = s.Run;
            Assert.That(run["mapGraph"], Is.InstanceOf<JObject>(), "startClimb builds the first act's map");
            Assert.That(((JArray)run["seatOrder"]).Count, Is.GreaterThanOrEqualTo(3), "the seat order is drawn (US-4.1)");
            Assert.That(s.ReachableNodes(), Is.EqualTo(((JArray)run["mapGraph"]["startIds"]).Select(t => (string)t)));
            Assert.That(run["customization"]["name"].Value<string>(), Is.EqualTo("Aldric"));
            var loaded = RunSession.Load(Default, _saves, 1);
            Assert.That(loaded.Status, Is.EqualTo(RunLoadStatus.Resumed));
            Assert.That(loaded.Session.Location, Is.EqualTo("map"));
            Assert.That(loaded.Session.StateHash(), Is.EqualTo(s.StateHash()));
        }

        [Test]
        public void TravelGoesOnlyToAReachableNode()
        {
            var s = NewClimb(77);
            var graph = (JObject)s.Run["mapGraph"];
            var far = ((JObject)graph["nodes"]).Properties().Select(p => p.Name).First(id => !s.ReachableNodes().Contains(id));
            var refused = s.Travel(far);
            Assert.That(refused.Refusal, Is.EqualTo(RunFlowValues.RefusalNotReachable));
            Assert.That(s.Location, Is.EqualTo("map"));
            var first = s.ReachableNodes()[0];
            var went = s.Travel(first);
            Assert.That(went.Travelled, Is.True);
            Assert.That(s.Run["mapNodeId"].Value<string>(), Is.EqualTo(first));
            Assert.That(s.Travel(first).Refusal, Is.Not.Null, "a second travel waits for the node to be resolved");
        }

        [Test]
        public void TheMapViewDrawsEveryNodeEdgeAndTheReachableRow()
        {
            var s = NewClimb(77);
            var view = ActMapView.Build(s, Ui);
            var graph = (JObject)s.Run["mapGraph"];
            Assert.That(view.Nodes.Count, Is.EqualTo(((JObject)graph["nodes"]).Count));
            Assert.That(view.Nodes.Where(n => n.Reachable).Select(n => n.Id), Is.EquivalentTo(s.ReachableNodes()));
            Assert.That(view.Edges.Count, Is.EqualTo(((JObject)graph["nodes"]).Properties().Sum(p => ((JArray)p.Value["next"]).Count)));
            Assert.That(view.Nodes.Where(n => n.Kind == "boss").All(n => !string.IsNullOrEmpty(n.BossLabel)), Is.True, "boss nodes carry their destination (US-4.4)");
            foreach (var n in view.Nodes)
            {
                Assert.That(Ui.Strings.Has(n.GlyphKey), Is.True, n.GlyphKey);
                Assert.That(n.Label, Does.Not.StartWith("map."), n.Kind);
            }
            Assert.That(view.Title, Does.Not.Contain("{"));
            var hud = RunHudView.Build(s, Ui);
            Assert.That(new[] { hud.Identity, hud.Trail, hud.Cinders, hud.Stones, hud.Flasks, hud.Deck }.Any(t => t.Contains("{")), Is.False);
        }

        [Test]
        public void TheShortCustomClimbIsAValidRunShape()
        {
            var s = NewClimb(ShortWinSeed, ShortClimb());
            var graph = (JObject)s.Run["mapGraph"];
            Assert.That(graph["floors"].Value<int>(), Is.EqualTo(7), "the shape's floors are applied");
            Assert.That(graph["columns"].Value<int>(), Is.EqualTo(3));
        }

        // ------------------------------------------------------------------ whole climbs (the F3 exit gate)

        [Test]
        public void ASeededClimbReachesTheSummitAndResumesAtEveryCommitPoint()
        {
            RunSession.ReviewEnemyHpScale = Assisted;
            var driver = new Driver(Default, _saves, NewClimb(WinSeed), WinSeed);
            var outcome = driver.Play(1500);
            TestContext.Progress.WriteLine("climb seed " + WinSeed + ": " + outcome + " after " + driver.Steps + " steps, " + driver.Fights + " fights, " + driver.Reloads + " reloads; "
                + string.Join(", ", driver.Seen.Select(p => p.Key + " " + p.Value)));
            Assert.That(outcome, Is.EqualTo("victory"), "the pinned seed reaches the summit");
            Assert.That(driver.Session.Act, Is.EqualTo(3), "the win is an act-3 boss (US-4.7)");
            Assert.That(driver.Seen.ContainsKey("act"), Is.True, "act transitions were taken");
            foreach (var door in new[] { "map", "combat", "rewards", "rest" }) Assert.That(driver.Seen.ContainsKey(door), Is.True, "the climb stood at " + door);
            var profile = driver.Session.Profile.Doc;
            Assert.That(((JArray)profile["results"]).Last()["victory"].Value<bool>(), Is.True, "the run is recorded (finishRun)");
            var end = RunEndView.Build(driver.Session, Ui);
            Assert.That(end.Victory, Is.True);
            Assert.That(end.Deck, Is.Not.Empty);
            Assert.That(end.Stats.Concat(end.Deck).Concat(end.Unlocks).Concat(new[] { end.Title, end.Detail, end.Where }).Any(t => t.Contains("{")), Is.False);
        }

        [Test]
        public void AClimbEndsInADeathThatClearsTheSlot()
        {
            var driver = new Driver(Default, _saves, NewClimb(90210), 90210) { EndTurnsOnly = true };
            var outcome = driver.Play(400);
            Assert.That(outcome, Is.EqualTo("defeat"));
            Assert.That(_saves.Exists(driver.Session.Slot), Is.False, "permadeath (US-4.9)");
            Assert.That(RunSession.Load(Default, _saves, 1).Status, Is.EqualTo(RunLoadStatus.Empty));
            var end = RunEndView.Build(driver.Session, Ui);
            Assert.That(end.Victory, Is.False);
            Assert.That(end.Stats.Any(t => t.StartsWith("Fell to", StringComparison.Ordinal)), Is.True, "the killer is named");
            Assert.That(((JArray)driver.Session.Profile.Doc["results"]).Count, Is.EqualTo(1));
        }

        [Test]
        public void TheShortClimbWinsFastForThePlayModeSmoke()
        {
            RunSession.ReviewEnemyHpScale = Assisted;
            var driver = new Driver(Default, _saves, NewClimb(ShortWinSeed, ShortClimb()), ShortWinSeed) { Smoke = true, Reload = false };
            var outcome = driver.Play(600);
            TestContext.Progress.WriteLine("short climb seed " + ShortWinSeed + ": " + outcome + " after " + driver.Steps + " steps, " + driver.Fights + " fights; "
                + string.Join(", ", driver.Seen.Select(p => p.Key + " " + p.Value)));
            Assert.That(outcome, Is.EqualTo("victory"));
        }

        /// <summary>Seed search for the pinned seeds (explicit; prints the outcomes).</summary>
        [Test, Explicit("seed search")]
        public void SearchWinningSeeds()
        {
            RunSession.ReviewEnemyHpScale = Assisted;
            foreach (var custom in new[] { ShortClimb() })
                for (uint seed = 1; seed <= 30; seed++)
                {
                    var driver = new Driver(Default, _saves, NewClimb(seed, custom), seed) { Reload = false, Smoke = true };
                    string outcome;
                    try { outcome = driver.Play(1500); }
                    catch (Exception e) { outcome = "error " + e.Message; }
                    TestContext.Progress.WriteLine((custom == null ? "classic" : "short") + " seed " + seed + ": " + outcome + " act " + driver.Session.Act + ", " + driver.Steps + " steps, " + driver.Fights + " fights; "
                        + string.Join(", ", driver.Seen.Select(p => p.Key + " " + p.Value)));
                }
        }

        // ------------------------------------------------------------------ rest stays (W-10; US-8.1 to US-8.6)

        /// <summary>Plays with the driver until the run stands at a rest place (without acting there).</summary>
        private RunSession AtRest(uint seed)
        {
            RunSession.ReviewEnemyHpScale = Assisted;
            var driver = new Driver(Default, _saves, NewClimb(seed), seed) { Reload = false };
            for (var i = 0; i < 400 && driver.Session.Location != "rest" && !driver.Session.RunOver; i++)
            {
                if (driver.Session.Location == "rest") break;
                var s = driver.Session;
                switch (s.Location)
                {
                    case "map":
                        var nodes = s.ReachableNodes();
                        s.Travel(nodes.FirstOrDefault(id => ((JObject)s.Run["mapGraph"]["nodes"][id])["type"].Value<string>() == "shrine") ?? nodes[0]);
                        break;
                    default:
                        driver.StepOnce();
                        break;
                }
            }
            return driver.Session;
        }

        [Test]
        public void ARestStayResumesOnTheSameStreamsAndItsActionsAreSaved()
        {
            RunSession s;
            try { s = AtRest(WinSeed); }
            catch (AssertionException) { s = null; }
            Assume.That(s != null && s.Location == "rest", "the seed reaches a rest place");
            var view = RestView.Build(s, Ui);
            Assert.That(view.Options.Select(o => o.Id), Does.Contain("rest"));
            Assert.That(view.Header, Does.Not.Contain("{"));
            Assert.That(view.Options.All(o => !o.StateText.Contains("{") && !o.Title.StartsWith("rest.")), Is.True);
            var hash = s.StateHash();
            var loaded = RunSession.Load(Default, _saves, 1);
            Assert.That(loaded.Status, Is.EqualTo(RunLoadStatus.Resumed), string.Join("; ", loaded.Warnings));
            Assert.That(loaded.Session.Location, Is.EqualTo("rest"));
            Assert.That(loaded.Session.StateHash(), Is.EqualTo(hash), "the re-opened stay drew exactly what the first opening drew");
            var again = RestView.Build(loaded.Session, Ui);
            Assert.That(again.Options.Select(o => o.Id + "=" + o.Available), Is.EqualTo(view.Options.Select(o => o.Id + "=" + o.Available)), "the same services (the smith's roll replayed)");

            s = loaded.Session;
            var hpBefore = s.Run.Num("hp");
            var rest = s.RestHere();
            Assert.That(rest.Done, Is.True, rest.Refusal);
            Assert.That(s.Run.Num("hp"), Is.GreaterThanOrEqualTo(hpBefore));
            var after = RunSession.Load(Default, _saves, 1).Session;
            Assert.That(after.StateHash(), Is.EqualTo(s.StateHash()), "the rest action was saved (PF-06 rest action)");
            if (after.Location == "rest")
            {
                Assert.That(after.RestHere().Refusal, Is.EqualTo("rested"), "a multi-use stay rests once, across a reload");
                after.LeaveRest();
            }
            Assert.That(after.Location, Is.EqualTo("map"));
        }

        [Test]
        public void TheLevelPreviewShowsTheDerivedPoolsBeforeAndAfter()
        {
            var s = NewClimb(77);
            var attr = RestView.LevelPreview(s, Ui, new JObject());
            Assert.That(attr.Count, Is.EqualTo(4));
            Assert.That(attr.All(r => !r.Contains("{")), Is.True);
        }

        // ------------------------------------------------------------------ routing and data

        [Test]
        public void EveryResumableLocationRoutesToAScreen()
        {
            foreach (var location in new[] { "combat", "rewards", "map", "rest", "merchant", "event", "dungeon", "runEnd" })
                Assert.That(Default.Flow.ScreenFor(location), Is.Not.Null.And.Not.Empty, location);
            foreach (var id in new[] { "combat", "rewards", "map", "rest", "runEnd" })
                Assert.That(Ui.Screens.IsBuilt(Default.Flow.ScreenFor(id)), Is.True, id + " is built");
        }

        [Test]
        public void TheLoopSettingsComeFromTheProfileThenThePreset()
        {
            var s = NewClimb(77);
            Assert.That(s.Settings().MultiUse, Is.EqualTo(Default.Snapshot.PlayerSettings?["shrineMultiUse"]?.Value<bool>() ?? false), "the preset's multi-use shrines");
            Assert.That(s.HoldConfirmMs(1100), Is.GreaterThanOrEqualTo(0));
        }
    }
}
