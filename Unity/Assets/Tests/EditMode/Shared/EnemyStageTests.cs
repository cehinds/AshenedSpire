using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
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
