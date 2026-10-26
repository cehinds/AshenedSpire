using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-4 (D-1540..): the enemies' combat figure is the web's expansion stage (src/ui/enemyExpansionStage.js). Tools/oracle-core2-enemystage.mjs records, from the web's own modules and from the
    /// stage built in a browser, where every frame of every enemy is drawn (as a rest and as a clip frame), what enemyPresentation, enemyStanceCategory and enemyActionPose answer and which frame
    /// each clip shows at a moment; the C# port (EnemyStageData, EnemyStageState, EnemyStancePlans) is compared with them. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class EnemyStageTests
    {
        private const double Tolerance = 0.05;
        private static UiData _ui;

        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));

        private static EnemyStageData Stage => Ui.EnemyStage;

        private static JObject Read(string name) => OracleDocuments.Read(Path.Combine(TestContent.OracleRoot, "core2", "enemystage", name));

        private static string[] Enemies => ((JObject)Read("placement.json")["enemies"]).Properties().Select(p => p.Name).ToArray();

        private static CoreCombatData _data;

        private static CoreCombatData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var registries = Core2Registries.Load(snapshot.Content);
                return _data = registries.ToCoreCombatData(snapshot.Content, registries.ToCoreRunData(snapshot.Content));
            }
        }

        private static JObject Unlocked => new JObject
        {
            ["gameProfileUnlocks"] = new JObject { [Data.Run.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) } },
        };

        private static CombatState Open(long seedValue, string classId, string encounterId)
        {
            var seed = (uint)seedValue;
            var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Unlocked));
            return CoreRunCombat.Create(Data, new Rng(seed), run, encounterId);
        }

        private static string[] Encounters => ((JArray)Read("fights.json")["fights"]).Select(f => f.Value<string>("encounterId")).ToArray();

        private static string Art(string frame) => "exp." + frame;

        private static void SameRect(string what, JObject want, StageRect got)
        {
            Assert.That(got.Left, Is.EqualTo(want.Value<double>("left")).Within(Tolerance), what + " left");
            Assert.That(got.Top, Is.EqualTo(want.Value<double>("top")).Within(Tolerance), what + " top");
            Assert.That(got.Width, Is.EqualTo(want.Value<double>("width")).Within(Tolerance), what + " width");
            Assert.That(got.Height, Is.EqualTo(want.Value<double>("height")).Within(Tolerance), what + " height");
        }

        private static EnemyDraw Draw(EnemyStageState state, string enemy) => state.Draw(frame => Art(enemy + "." + frame), art => true);

        [Test]
        public void EveryRosterEnemyHasAStage()
        {
            Assert.That(Enemies.Length, Is.EqualTo(33));
            foreach (var id in Enemies) Assert.That(Stage.Has(id), id);
            Assert.That(Stage.Height, Is.EqualTo(190));
        }

        [TestCaseSource(nameof(Enemies))]
        public void ARestIsDrawnWhereTheWebDrawsIt(string id)
        {
            var want = (JObject)Read("placement.json")["enemies"][id];
            foreach (var rest in new[] { EnemyRests.Idle, EnemyRests.Guard, EnemyRests.Wounded, EnemyRests.Afflicted, EnemyRests.Defeated })
            {
                var state = new EnemyStageState(Stage, id);
                state.SetState(rest, null, null);
                var draw = Draw(state, id);
                var row = (JObject)want["rests"][rest];
                Assert.That(draw, Is.Not.Null, id + " " + rest);
                Assert.That(draw.Frame, Is.EqualTo(row.Value<string>("frame")), id + " " + rest + " frame");
                SameRect(id + " " + rest, row, draw.Rect);
            }
        }

        [TestCaseSource(nameof(Enemies))]
        public void AStanceFrameIsDrawnWhereTheWebDrawsIt(string id)
        {
            var want = (JObject)Read("placement.json")["enemies"][id];
            foreach (var row in ((JObject)want["stances"]).Properties())
            {
                var state = new EnemyStageState(Stage, id);
                state.SetState(EnemyRests.Idle, null, row.Name);
                var draw = Draw(state, id);
                Assert.That(draw.Frame, Is.EqualTo(row.Value.Value<string>("frame")), id + " " + row.Name);
                SameRect(id + " " + row.Name, (JObject)row.Value, draw.Rect);
                Assert.That(state.Stance, Is.EqualTo(row.Name));
            }
        }

        [TestCaseSource(nameof(Enemies))]
        public void AClipFrameIsDrawnWhereTheWebDrawsIt(string id)
        {
            var want = (JObject)Read("placement.json")["enemies"][id];
            foreach (var row in ((JObject)want["frames"]).Properties())
            {
                var state = new EnemyStageState(Stage, id);
                Assert.That(state.Play(row.Name, 100000, 0, false), Is.True, id + " " + row.Name);
                var draw = Draw(state, id);
                Assert.That(draw.Frame, Is.EqualTo(row.Value.Value<string>("frame")), id + " " + row.Name + " frame");
                SameRect(id + " " + row.Name, (JObject)row.Value, draw.Rect);
            }
        }

        [Test]
        public void TheWebPlaysEveryPoseTheOracleMeasured()
        {
            // play() refuses a pose with no clip and no frame of its own (stance frames are only ever rests): the oracle's frames are exactly the stage's poses.
            var want = (JObject)Read("placement.json")["enemies"]["ashRevenant"];
            var played = ((JObject)want["frames"]).Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var mine = Stage.Poses.Where(p => new EnemyStageState(Stage, "ashRevenant").Play(p, 100000, 0, false)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.That(mine, Is.EqualTo(played));
        }

        [Test]
        public void EnemyPresentationMatchesTheWeb()
        {
            var problems = new List<string>();
            var cases = (JArray)Read("presentation.json")["cases"];
            foreach (JObject c in cases)
            {
                var entity = (JObject)c["entity"];
                var statuses = ((JArray)c["statusOrder"]).Select(t => t.Value<string>()).Select(id => (id, entity["statuses"][id].Value<double>("stacks"), entity["statuses"][id]["meter"] != null && entity["statuses"][id].Value<bool>("meter"))).ToList();
                var alive = entity["alive"] == null || entity.Value<bool>("alive");
                var got = Stage.Presentation(alive, entity.Value<double?>("hp") ?? 0, entity.Value<double?>("maxHp") ?? 0, entity.Value<double?>("block") ?? 0, statuses);
                var result = (JObject)c["result"];
                var themes = ((JArray)result["auraThemes"]).Select(t => t.Value<string>()).ToList();
                if (got.Rest != result.Value<string>("rest") || !got.Themes.SequenceEqual(themes)) problems.Add(entity.ToString(Newtonsoft.Json.Formatting.None) + " -> " + got.Rest + "/" + string.Join(",", got.Themes) + " (web " + result.Value<string>("rest") + "/" + string.Join(",", themes) + ")");
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(10)));
            Assert.That(cases.Count, Is.GreaterThan(100));
        }

        [Test]
        public void TheCategoryOfAnIntentMatchesTheWeb()
        {
            var problems = new List<string>();
            var cases = (JArray)Read("stance.json")["cases"];
            foreach (JObject c in cases)
            {
                var intent = c["intent"] as JObject;
                var want = c["category"].Type == JTokenType.Null ? null : c.Value<string>("category");
                var got = EnemyStancePlans.Category(Stage, intent);
                if (got != want) problems.Add((intent?.ToString(Newtonsoft.Json.Formatting.None) ?? "null") + " -> " + (got ?? "none") + " (web " + (want ?? "none") + ")");
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(10)));
        }

        [Test]
        public void TheAttackPoseOfEveryMoveMatchesTheWeb()
        {
            var problems = new List<string>();
            var moves = (JObject)Read("actions.json")["moves"];
            foreach (var row in moves.Properties())
            {
                var parts = row.Name.Split(':');
                var action = Stage.Move(parts[0], parts[1]);
                var o = (JObject)row.Value;
                if (action.Family != o.Value<string>("family") || action.Casting != o.Value<bool>("casting")) problems.Add(row.Name + " family " + action.Family + "/" + action.Casting + " (web " + o.Value<string>("family") + "/" + o.Value<bool>("casting") + ")");
                foreach (var kind in ((JObject)o["byKind"]).Properties())
                    if (Stage.ActionPose(kind.Name, action) != kind.Value.Value<string>()) problems.Add(row.Name + " as " + kind.Name + " -> " + Stage.ActionPose(kind.Name, action) + " (web " + kind.Value.Value<string>() + ")");
                var actor = Stage.Actor(parts[0]);
                if (Math.Abs(actor.Tempo - o.Value<double>("tempo")) > 1e-9 || Math.Abs(actor.Reach - o.Value<double>("reach")) > 1e-9) problems.Add(row.Name + " tempo/reach " + actor.Tempo + "/" + actor.Reach);
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(10)));
            Assert.That(moves.Count, Is.EqualTo(138));
        }

        [Test]
        public void EveryClipShowsTheFrameTheWebShowsAtEachMoment()
        {
            var problems = new List<string>();
            var checkedRows = 0;
            foreach (var clip in ((JObject)Read("clips.json")["clips"]).Properties())
                foreach (JObject row in (JArray)clip.Value)
                {
                    var state = new EnemyStageState(Stage, "ashRevenant");
                    Assert.That(state.Play(clip.Name, row.Value<double>("ms"), 1000, false), Is.True);
                    state.Tick(1000 + row.Value<double>("elapsed"));
                    var got = state.Frame;
                    checkedRows++;
                    if (got != row.Value<string>("frame")) problems.Add(clip.Name + " " + row.Value<double>("ms") + "ms at " + row.Value<double>("elapsed") + " -> " + got + " (web " + row.Value<string>("frame") + ")");
                }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(10)));
            Assert.That(checkedRows, Is.GreaterThan(100));
        }

        [TestCaseSource(nameof(Encounters))]
        public void EveryEnemyOfAFightHoldsTheCategoryTheWebHoldsAndEveryMoveAnnouncesTheirs(string encounterId)
        {
            var want = ((JArray)Read("fights.json")["fights"]).OfType<JObject>().First(f => f.Value<string>("encounterId") == encounterId);
            var c = Open(want.Value<long>("seed"), want.Value<string>("classId"), encounterId);
            var problems = new List<string>();
            foreach (var row in ((JObject)want["enemies"]).Properties())
            {
                var enemy = c.Enemies.First(e => e.Str(CombatKeys.Id) == row.Name);
                var o = (JObject)row.Value;
                Assert.That(enemy.Str(CombatKeys.EnemyId), Is.EqualTo(o.Value<string>("enemyId")));
                var start = EnemyStancePlans.Category(Stage, enemy.Obj(CombatKeys.Intent));
                var wantStart = o["start"].Type == JTokenType.Null ? null : o.Value<string>("start");
                if (start != wantStart) problems.Add(encounterId + " " + row.Name + " starts " + (start ?? "none") + " (web " + (wantStart ?? "none") + ")");
                foreach (var move in ((JObject)o["moves"]).Properties())
                {
                    var wantMove = move.Value.Type == JTokenType.Null ? null : move.Value.Value<string>();
                    var got = EnemyStancePlans.ForMove(Stage, c, enemy, move.Name, null);
                    if (got != wantMove) problems.Add(encounterId + " " + row.Name + " " + move.Name + " announces " + (got ?? "none") + " (web " + (wantMove ?? "none") + ")");
                }
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(10)));
        }

        [Test]
        public void ABeatThatStartsAMoveIsPlayedAsTheWebPlaysIt()
        {
            JObject Event(string type, string move, string kind = null) => new JObject { ["type"] = type, ["moveId"] = move, ["kind"] = kind };
            FxBeat Beat(params Core2LogLine[] lines) => new FxBeat { Events = lines.ToList() };
            Core2LogLine Line(string type, string move, string kind = null) => new Core2LogLine { Type = type, Event = Event(type, move, kind) };
            var spell = EnemyStagePlans.ActionOf(Stage, Beat(Line(FxEvents.EnemyMoveStarted, "warcry", "buff")), "wanderingSoldier");
            Assert.That(spell.Pose, Is.EqualTo(EnemyActions.MagicAttack).Or.EqualTo(EnemyActions.Buff));
            Assert.That(EnemyStagePlans.ActionOf(Stage, Beat(Line(FxEvents.EnemyMoveStarted, "slash", "attack")), "wanderingSoldier").Pose, Is.EqualTo(EnemyActions.Attack));
            Assert.That(EnemyStagePlans.ActionOf(Stage, Beat(Line(FxEvents.EnemyMoveStarted, "guard", "block")), "wanderingSoldier").Pose, Is.EqualTo(EnemyActions.Guard));
            Assert.That(EnemyStagePlans.ActionOf(Stage, Beat(Line(FxEvents.CombatHitStarted, "slash", "attack")), "wanderingSoldier").Motion, Is.EqualTo("sweep"));
            Assert.That(EnemyStagePlans.ActionOf(Stage, Beat(Line(FxEvents.DamageDealt, null)), "wanderingSoldier"), Is.Null);
            var counter = new Core2LogLine { Type = FxEvents.CombatCounterTriggered, Event = new JObject { ["sourceKind"] = "enemy", ["moveId"] = "slash" } };
            Assert.That(EnemyStagePlans.ActionOf(Stage, Beat(counter), "wanderingSoldier").Pose, Is.EqualTo(EnemyActions.Attack));
        }

        [Test]
        public void AnEnemyActionTakesThePacesLungeScaledByItsTempoAndLandsAtTheWindup()
        {
            var action = new EnemyAction { Tempo = 0.72, Reach = 1.25 };
            var timing = EnemyStagePlans.Timing(action, 280, 0.55);
            Assert.That(timing.TotalMs, Is.EqualTo(202));
            Assert.That(timing.ImpactMs, Is.EqualTo(111));
            Assert.That(EnemyStagePlans.Timing(null, 280, 0.55).TotalMs, Is.EqualTo(280));
        }

        [Test]
        public void TheTravelOfAnActionFollowsTheFamilyKeyframesAtTheActorsReach()
        {
            var track = Stage.TravelTrack("sweep", 1.25, 300);
            Assert.That(track, Is.Not.Null);
            Assert.That(track.Ms, Is.EqualTo(300));
            Assert.That(track.Stops.Select(s => s.T), Is.EqualTo(new[] { 0d, 0.22, 0.55, 1d }));
            Assert.That(track.Stops.Select(s => s.X.Value), Is.EqualTo(new[] { 0d, 15d, -32.5, 0d }).Within(1e-9));
            Assert.That(Stage.TravelTrack("release", 1, 300), Is.Null, "a projectile releases without travelling");
            Assert.That(Stage.TravelTrack("lunge", 1, 300).Stops.Select(s => s.T), Is.EqualTo(new[] { 0d, 0.3, 0.55, 0.65, 1d }));
        }

        private static readonly Dictionary<string, double[]> Easings = new Dictionary<string, double[]> { ["easeOut"] = new[] { 0d, 0d, 0.58, 1d } };

        [Test]
        public void TheFilterOfAnActionIsTheFamilyKeyframesOfTheCss()
        {
            // styles/combat.css: family-slash 55% brightness(1.25), family-strike 1.4, family-spell 1.4 with drop-shadow(0 0 10px gold), family-guard 35-70% 1.2 with 5px parchment, family-dodge opacity .65 at 40-60%.
            double? Bright(string motion, double share) => FxCurve.Sample(Stage.ActionTrack(motion, 1, 1000), Easings, share * 1000).Brightness;
            Assert.That(Bright("sweep", 0), Is.EqualTo(1).Within(1e-9));
            Assert.That(Bright("sweep", 0.55), Is.EqualTo(1.25).Within(1e-9));
            Assert.That(Bright("sweep", 1), Is.EqualTo(1).Within(1e-9));
            Assert.That(Bright("impact", 0.55), Is.EqualTo(1.4).Within(1e-9));
            Assert.That(Bright("lunge", 0.6), Is.EqualTo(1.25).Within(1e-9), "held from 55% to 65%");
            Assert.That(Bright("release", 0.5), Is.EqualTo(1.25).Within(1e-9));
            Assert.That(Bright("cast", 0.55), Is.EqualTo(1.4).Within(1e-9));
            Assert.That(Bright("brace", 0.5), Is.EqualTo(1.2).Within(1e-9));
            Assert.That(Bright("sweep", 0.25), Is.GreaterThan(1).And.LessThan(1.25), "eased between the stops that name the filter");
            var cast = Stage.ActionTrack("cast", 1, 1000);
            Assert.That(cast.GlowBlur, Is.EqualTo(10));
            Assert.That(cast.GlowColour, Is.EqualTo("#c9a227"));
            Assert.That(FxCurve.Sample(cast, Easings, 550).Glow, Is.EqualTo(1).Within(1e-9));
            Assert.That(FxCurve.Sample(cast, Easings, 0).Glow, Is.EqualTo(0).Within(1e-9));
            Assert.That(FxCurve.Sample(cast, Easings, 500).X, Is.Null, "a cast does not travel");
            var guard = Stage.ActionTrack("brace", 1, 1000);
            Assert.That(guard.GlowBlur, Is.EqualTo(5));
            Assert.That(guard.GlowColour, Is.EqualTo("#e8dcc0"));
            Assert.That(FxCurve.Sample(Stage.ActionTrack("sidestep", 1, 1000), Easings, 500).Opacity, Is.EqualTo(0.65).Within(1e-9));
            Assert.That(Stage.ActionTrack("settle", 1, 1000), Is.Null, "a neutral action plays nothing");
        }

        [Test]
        public void ATravellingActionKeepsItsTranslateBesideTheFilter()
        {
            var track = Stage.ActionTrack("sweep", 1.25, 300);
            var travel = Stage.TravelTrack("sweep", 1.25, 300);
            Assert.That(track.Stops.Where(s => s.X.HasValue).Select(s => (s.T, s.X.Value)), Is.EqualTo(travel.Stops.Select(s => (s.T, s.X.Value))));
            Assert.That(track.Stops.Where(s => s.Brightness.HasValue).Select(s => s.T), Is.EqualTo(new[] { 0d, 0.55, 1d }));
            Assert.That(track.Stops.Select(s => s.T), Is.Ordered);
        }

        // ------------------------------------------------------------------ the clock

        [Test]
        public void AClipSettlesToTheRestWhenItsTimeIsUp()
        {
            var state = new EnemyStageState(Stage, "ashRevenant");
            state.SetState(EnemyRests.Guard, null, null);
            Assert.That(state.Frame, Is.EqualTo("block-01"));
            Assert.That(state.Play(EnemyActions.Attack, 400, 0, false), Is.True);
            Assert.That(state.Busy, Is.True);
            Assert.That(state.Frame, Is.EqualTo("attack-01"));
            Assert.That(state.Tick(399), Is.True);
            Assert.That(state.Tick(400), Is.False);
            Assert.That(state.Busy, Is.False);
            Assert.That(state.Frame, Is.EqualTo("block-01"));
        }

        [Test]
        public void TheHitStopHoldsTheFrameOnShowAndMovesTheEndBack()
        {
            var state = new EnemyStageState(Stage, "ashRevenant");
            Assert.That(state.Play(EnemyActions.Attack, 400, 0, false), Is.True);
            state.Tick(250);
            var frame = state.Frame;
            Assert.That(state.Hold(300, 250), Is.True);
            Assert.That(state.Tick(500), Is.True);
            Assert.That(state.Frame, Is.EqualTo(frame));
            Assert.That(state.Tick(699), Is.True);
            Assert.That(state.Tick(700), Is.False);
            Assert.That(state.Frame, Is.EqualTo(EnemyRests.Idle));
            Assert.That(state.Hold(100, 800), Is.False, "nothing plays, nothing to hold");
        }

        [Test]
        public void ADefeatedEnemyPlaysNothingButTheFall()
        {
            var state = new EnemyStageState(Stage, "ashRevenant");
            Assert.That(state.Play(EnemyActions.Attack, 400, 0, false), Is.True);
            state.SetState(EnemyRests.Defeated, null, "attack");
            Assert.That(state.Busy, Is.False, "a defeated enemy settles at once");
            Assert.That(state.Frame, Is.EqualTo(EnemyRests.Defeated));
            Assert.That(state.Play(EnemyActions.Attack, 400, 0, false), Is.False);
            Assert.That(state.Play(EnemyActions.Defeated, 400, 0, false), Is.True);
            Assert.That(state.Frame, Is.EqualTo(EnemyRests.Defeated));
        }

        [Test]
        public void AFallTakesTheStageDownFromAnyRestAndDropsItsGlow()
        {
            var state = new EnemyStageState(Stage, "ashRevenant");
            state.SetState(EnemyRests.Idle, new[] { "strength" }, "attack");
            Assert.That(Draw(state, "ashRevenant").Themes, Is.EqualTo(new[] { "strength" }));
            Assert.That(state.Play(EnemyActions.Defeated, 300, 0, false), Is.True);
            Assert.That(state.Frame, Is.EqualTo(EnemyRests.Defeated));
            Assert.That(Draw(state, "ashRevenant").Themes, Is.Empty);
            Assert.That(state.Stance, Is.Null);
        }

        [Test]
        public void ReducedMotionAndAZeroLengthSettleAtOnce()
        {
            var state = new EnemyStageState(Stage, "ashRevenant");
            Assert.That(state.Play(EnemyActions.Attack, 400, 0, true), Is.True);
            Assert.That(state.Busy, Is.False);
            Assert.That(state.Play(EnemyActions.Attack, 0, 0, false), Is.True);
            Assert.That(state.Busy, Is.False);
            Assert.That(state.Play("nonsense", 400, 0, false), Is.False);
        }

        [Test]
        public void AStanceFrameReplacesEveryRestButTheFall()
        {
            var state = new EnemyStageState(Stage, "ashRevenant");
            state.SetState(EnemyRests.Guard, null, "attack");
            Assert.That(state.Frame, Is.EqualTo(Stage.StanceFrame("ashRevenant", "attack")));
            state.SetState(EnemyRests.Defeated, null, "attack");
            Assert.That(state.Frame, Is.EqualTo(EnemyRests.Defeated));
            state.SetState(EnemyRests.Idle, null, null);
            Assert.That(state.Frame, Is.EqualTo(EnemyRests.Idle));
            Assert.That(state.Stance, Is.Null);
        }
    }
}
