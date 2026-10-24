using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-19 (D-345 to D-352): a Core 2 run is playable from the new run to its end through <see cref="Core2RunSession"/> alone. A bot plays whole climbs over several seeds and classes
    /// (the map, the fights, the reward door, the rests, the shops, the treasures, the events, the act advances) and never meets a node the session cannot answer; the climbs end into the
    /// profile (the record, the class unlock ledger), a save made at a door resumes on that door, and the views the screens read are built for the doors the bot meets. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2LoopSessionTests
    {
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        private string _dir;
        private SaveService _saves;

        private static RunContent Content => Core2HostParityTests.Content;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-loopsession-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>The bot's own generator: never the engine's, so that choosing cannot move a stream.</summary>
        private sealed class Lcg
        {
            private uint _s;
            public Lcg(uint seed) => _s = seed == 0 ? 1u : seed;
            public int Next(int n) { _s = unchecked(_s * 1664525u + 1013904223u); return n <= 1 ? 0 : (int)((_s >> 8) % (uint)n); }
        }

        private void OpenClasses()
        {
            var profile = ProfileStore.Load(_saves);
            profile.Doc["gameProfileUnlocks"] = new JObject { [Content.Core2.ProfileId] = new JObject { ["completedRunIds"] = new JArray("a", "b", "c") } };
            profile.Save(Content.ContentHash);
        }

        /// <summary>A cheap player: each card of the hand at the first living enemy, answers to offers and choices, then the end of the turn.</summary>
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

        private static void Fight(Core2RunSession session, bool weaken)
        {
            if (weaken) foreach (var enemy in session.Combat.State.Enemies) enemy["hp"] = Math.Min(enemy.Value<double>("hp"), 1.0);
            for (var guard = 0; guard < 400 && session.IsInCombat; guard++) PlayTurn(session);
            Assert.That(session.IsInCombat, Is.False, "the fight ended");
        }

        private sealed class Tally
        {
            public readonly HashSet<string> Doors = new HashSet<string>();
            public int Fights, Events, Responses, Rests, Shops, Buys, Treasures, Offers, Acts, Resumes, Views;
        }

        /// <summary>One whole climb by a bot: every door is worked through the session's own API.</summary>
        private Tally Climb(string classId, uint seed, bool weaken, Tally tally)
        {
            OpenClasses();
            var session = Core2RunSession.New(Content, _saves, 1, seed, classId, "Bot", () => Epoch);
            var pick = new Lcg(seed ^ 0x5eed);
            session.EditRunForReview(r => { r["maxHp"] = 100000; r["hp"] = 100000; });
            var strings = StringTableFor();
            for (var step = 0; step < 900 && !session.RunOver; step++)
            {
                var location = session.Location;
                Assert.That(new[] { "map", "combat", "rewards", "merchant", "rest", "event" }, Does.Contain(location), "the run stands at a door the session knows (" + location + ")");
                Assert.That(session.PendingPort == null || location != "map", Is.True, "no notice stands at the map");
                // A save made at a door resumes on it (an event is not kept: the web resumes at the map).
                if (location != "combat" && location != "event" && pick.Next(5) == 0)
                {
                    var loaded = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
                    Assert.That(loaded.Status, Is.EqualTo(Core2RunLoadStatus.Resumed), string.Join("; ", loaded.Warnings));
                    Assert.That(loaded.Session.Location, Is.EqualTo(location), "the save resumes on the door it was made at");
                    Assert.That(loaded.Session.StateHash(), Is.EqualTo(session.StateHash()), "the resumed run is the saved one at " + location);
                    session = loaded.Session;
                    tally.Resumes++;
                }
                switch (location)
                {
                    case "combat":
                        tally.Fights++;
                        Fight(session, weaken);
                        break;
                    case "rewards":
                        WorkRewards(session, pick, tally);
                        break;
                    case "rest":
                        WorkRest(session, pick, tally);
                        break;
                    case "merchant":
                        WorkShop(session, pick, tally, strings);
                        break;
                    case "event":
                        WorkEvent(session, pick, tally, strings);
                        break;
                    default:
                        {
                            var options = session.ReachableNodes();
                            Assert.That(options, Is.Not.Empty, "the map offers a way on");
                            var graph = session.Run.Obj("mapGraph").Obj("nodes");
                            var special = options.Where(id => { var type = graph.Obj(id).Value<string>("type"); return type != "monster" && type != "elite" && type != "boss" && type != "shrine"; }).ToList();
                            var node = special.Count > 0 && pick.Next(10) < 7 ? special[pick.Next(special.Count)] : options[pick.Next(options.Count)];
                            var actBefore = session.Act;
                            var result = session.Travel(node);
                            Assert.That(result.Travelled, Is.True, result.Refusal);
                            Assert.That(result.NotYetPorted, Is.False, "no node is answered 'not yet ported'");
                            tally.Doors.Add(result.Door.Kind);
                            Assert.That(session.Act, Is.EqualTo(actBefore));
                            break;
                        }
                }
            }
            Assert.That(session.RunOver, Is.True, "the climb finished within the bot's steps");
            Assert.That(session.Location, Is.EqualTo("runEnd"));
            Assert.That(_saves.Exists(session.Slot), Is.False, "the slot is cleared");
            var doc = ProfileStore.Load(_saves).Doc;
            var results = (JArray)doc["results"];
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results.Last.Value<bool>("victory"), Is.EqualTo(session.End.Victory));
            Assert.That(results.Last.Value<string>("class"), Is.EqualTo(classId));
            tally.Acts += session.Act - 1;
            // The next climb starts fresh in the same slot: the ledger holds this run and the result list restarts in the next test.
            Assert.That(doc["gameProfileUnlocks"][Content.Core2.ProfileId]["completedRunIds"], Has.Count.EqualTo(4), "the run joined the ledger");
            return tally;
        }

        private static UiData _ui;

        private static StringTable Strings => (_ui ??= UiData.Load(TestContent.Source)).Strings;

        private static StringTable StringTableFor() => Strings;

        private static void WorkRewards(Core2RunSession session, Lcg pick, Tally tally)
        {
            for (var guard = 0; guard < 12 && session.HasPendingReward; guard++)
            {
                if (session.ProgressionNotices.Count > 0) { Assert.That(session.AcknowledgeProgression().Done, Is.True); continue; }
                var offer = session.RewardOffer;
                if (offer == null) break;
                tally.Offers++;
                var n = offer.Arr("options").Count;
                var roll = pick.Next(10);
                if (roll < 4) session.TakeReward(pick.Next(n));
                else if (roll < 8) session.AddRewardToDeck(pick.Next(n));
                session.TakeReward(n + pick.Next(2));
                var left = session.LeaveRewards();
                Assert.That(left.Done, Is.True, left.Refusal);
            }
        }

        private static void WorkRest(Core2RunSession session, Lcg pick, Tally tally)
        {
            tally.Rests++;
            var view = session.RestView(Strings);
            Assert.That(view, Is.Not.Null, "the rest site's screen is built");
            for (var i = 0; i < pick.Next(3); i++) session.TakeRestService(pick.Next(2) == 0 ? "smith" : "levelUp", new JObject { ["itemRef"] = "armament/nothing", ["attributeId"] = "luck" });
            Assert.That(session.LeaveRest().Done, Is.True);
        }

        private static void WorkShop(Core2RunSession session, Lcg pick, Tally tally, StringTable strings)
        {
            tally.Shops++;
            var view = Core2ShopView.Build(session, strings);
            Assert.That(view, Is.Not.Null, "the shop's screen is built");
            var n = session.ShopStock.Arr("options").Count;
            for (var i = 0; i < pick.Next(3); i++) { session.BuyFromShop(pick.Next(n + 1)); tally.Buys++; }
            Assert.That(session.LeaveShop().Done, Is.True);
        }

        private static void WorkEvent(Core2RunSession session, Lcg pick, Tally tally, StringTable strings)
        {
            tally.Events++;
            var view = Core2EventView.Build(session, strings);
            Assert.That(view, Is.Not.Null, "the event's screen is built");
            Assert.That(view.Responses, Is.Not.Empty);
            Assert.That(view.ContinueEnabled, Is.False);
            tally.Views++;
            var taken = false;
            for (var attempt = 0; attempt < 6 && !taken; attempt++)
            {
                var all = session.EventDomainView();
                var choices = ((JArray)all["choices"]).OfType<JObject>().ToList();
                var id = pick.Next(8) == 7 ? "nonesuch" : choices[pick.Next(choices.Count)].Value<string>("choiceId");
                var r = session.ChooseEvent(id);
                tally.Responses++;
                taken = r.Ok;
            }
            if (!taken) Assert.That(session.ChooseEvent(session.EventStandInChoice()).Ok, Is.True);
            var done = Core2EventView.Build(session, strings);
            Assert.That(done.Resolved, Is.True);
            Assert.That(done.ContinueEnabled, Is.True);
            Assert.That(done.ResultText, Is.Not.Empty, "the result of the response is told");
            var end = session.FinishEvent();
            Assert.That(session.EventId, Is.Null);
            Assert.That(end.Location, Is.EqualTo(end.Fight != null ? "combat" : "map"));
        }

        [TestCase("reaver", 11u)]
        [TestCase("starseer", 12u)]
        [TestCase("rogue", 13u)]
        [TestCase("herald", 14u)]
        public void ABotPlaysAWholeClimbWithNoNodeLeftUnanswered(string classId, uint seed)
        {
            var tally = Climb(classId, seed, true, new Tally());
            Assert.That(tally.Fights, Is.GreaterThan(3));
            Assert.That(tally.Doors, Is.Not.Empty);
        }

        [Test]
        public void SeveralSeedsMeetEveryKindOfNodeAndEndTheirClimbs()
        {
            var seen = new HashSet<string>();
            var total = new Tally();
            var seeds = new uint[] { 101, 202, 303, 404, 505, 606 };
            var classes = Content.Core2.ClassIds;
            for (var i = 0; i < seeds.Length; i++)
            {
                // Each climb runs in a fresh store (the ledger it needs is written by the bot).
                TearDown();
                SetUp();
                var tally = Climb(classes[i % classes.Count], seeds[i], i % 3 != 2, new Tally());
                seen.UnionWith(tally.Doors);
                total.Fights += tally.Fights;
                total.Events += tally.Events;
                total.Rests += tally.Rests;
                total.Shops += tally.Shops;
                total.Offers += tally.Offers;
                total.Acts += tally.Acts;
                total.Resumes += tally.Resumes;
            }
            Assert.That(seen, Is.SupersetOf(new[] { "fight", "rest", "shop", "event" }));
            Assert.That(total.Events, Is.GreaterThan(0));
            Assert.That(total.Rests, Is.GreaterThan(0));
            Assert.That(total.Offers, Is.GreaterThan(0));
            Assert.That(total.Resumes, Is.GreaterThan(0), "a save at a door resumed on it");
        }
    }
}
