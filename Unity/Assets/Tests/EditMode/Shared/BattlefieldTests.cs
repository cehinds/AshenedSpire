using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Ui;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-4 (D-1540..): the combat board's formation is the web's perspective grid. Tools/oracle-core2-battlefield.mjs runs the web's own alternativeFormation, fitAlternativeSprites and
    /// alternativeCombatComposition on a matrix of fields, party and enemy lists as battlefieldStage.js feeds them, and measures the shipped board in a browser; the C# port
    /// (BattlefieldFormation) is compared with both. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class BattlefieldTests
    {
        private const double Tolerance = 1e-4;
        private const double MeasuredTolerance = 0.1;
        private static UiData _ui;

        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));

        private static BattlefieldData Data => Ui.Battlefield;

        private static JObject Read(string name) => OracleDocuments.Read(Path.Combine(TestContent.OracleRoot, "core2", "battlefield", name));

        private static IEnumerable<TestCaseData> Cases()
        {
            var i = 0;
            foreach (var c in (JArray)Read("model.json")["cases"])
            {
                var f = c["field"];
                yield return new TestCaseData(i++).SetName($"Model({f.Value<double>("width")}x{Math.Round(f.Value<double>("height"))} {c.Value<string>("list")} x{((JArray)c["enemies"]).Count})");
            }
        }

        private static IEnumerable<TestCaseData> Scenarios()
        {
            var i = 0;
            foreach (var s in (JArray)Read("board.json")["scenarios"]) yield return new TestCaseData(i++).SetName($"Board({s["viewport"].Value<int>("width")}x{s["viewport"].Value<int>("height")} {s.Value<int>("enemies")} enemies)");
        }

        private static (BattlefieldPlan Plan, List<BattlefieldActor> Actors) Build(double width, double height, bool narrow, string formFactor, IReadOnlyList<string> enemyIds)
        {
            var ids = Enumerable.Range(1, enemyIds.Count).Select(n => "e" + n).ToList();
            var plan = BattlefieldFormation.Plan(Data, width, height, narrow, formFactor, ids.Count, 1);
            BattlefieldFormation.Seat(Data, plan, new[] { "player" }, ids);
            var actors = new List<BattlefieldActor>();
            foreach (var slot in plan.Slots)
            {
                var enemy = slot.Id != "player";
                var defId = enemy ? enemyIds[int.Parse(slot.Id.Substring(1), System.Globalization.CultureInfo.InvariantCulture) - 1] : null;
                var figure = enemy ? Ui.EnemyStage.Figure(defId) : null;
                var stature = enemy ? Data.Stature(defId) : "normal";
                actors.Add(new BattlefieldActor
                {
                    Id = slot.Id, Enemy = enemy, EnemyId = defId, Stature = stature, Ratio = enemy ? Data.SpriteRatio(stature, defId) : 1,
                    Multiplier = BattlefieldFormation.Multiplier(Data, enemy, slot.Row),
                    BoxHeight = Ui.EnemyStage.Height, VisibleHeight = Ui.EnemyStage.Height,
                    VisibleWidth = enemy ? figure.IdleWidth * Ui.EnemyStage.Height / figure.IdleHeight : 179.08,
                    Leading = BattlefieldFormation.Leading(Data, height, 0, 0, false), ArtLeading = 0, Slot = slot,
                });
            }
            return (plan, actors);
        }

        [TestCaseSource(nameof(Cases))]
        public void TheFormationFitsAndPlacesEveryFigureAsTheWebDoes(int index)
        {
            var want = (JObject)((JArray)Read("model.json")["cases"])[index];
            var f = (JObject)want["field"];
            var width = f.Value<double>("width");
            var height = f.Value<double>("height");
            var narrow = f.Value<bool>("narrow");
            var built = Build(width, height, narrow, f.Value<string>("formFactor"), ((JArray)want["enemies"]).Select(t => t.Value<string>()).ToList());
            Assert.That(built.Plan.Rows, Is.EqualTo(want.Value<int>("rows")));
            // the lattice
            var cells = (JArray)want["cells"];
            Assert.That(built.Plan.VisualCells.Count, Is.EqualTo(cells.Count), "visual cells");
            foreach (var row in cells.OfType<JObject>())
            {
                var cell = built.Plan.VisualCells.First(c => c.Cell == row.Value<string>("cell") && c.Enemy == (row.Value<string>("side") == "enemy"));
                Assert.That(cell.X, Is.EqualTo(row.Value<double>("x")).Within(Tolerance), row["cell"] + " x");
                Assert.That(cell.Ground, Is.EqualTo(row.Value<double>("ground")).Within(Tolerance), row["cell"] + " ground");
                Assert.That(cell.Lane, Is.EqualTo(row.Value<int>("lane")));
                Assert.That(cell.Rank, Is.EqualTo(row.Value<int>("rank")));
                Assert.That(cell.VisualBand, Is.EqualTo(row.Value<string>("band")));
                Assert.That(cell.Width, Is.EqualTo(row.Value<double>("width")).Within(Tolerance));
                Assert.That(cell.ArtWidth, Is.EqualTo(row.Value<double>("artWidth")).Within(Tolerance));
                Assert.That(cell.MiniHudLeft, Is.EqualTo(row.Value<double>("miniHudLeft")).Within(Tolerance));
                Assert.That(cell.MiniHudWidth, Is.EqualTo(row.Value<double>("miniHudWidth")).Within(Tolerance));
                Assert.That(cell.VisualOnly, Is.EqualTo(row.Value<bool>("visualOnly")));
                var box = (JObject)row["box"];
                Assert.That(cell.Box.Left, Is.EqualTo(box.Value<double>("left")).Within(Tolerance), row["cell"] + " box left");
                Assert.That(cell.Box.Top, Is.EqualTo(box.Value<double>("top")).Within(Tolerance));
                Assert.That(cell.Box.Width, Is.EqualTo(box.Value<double>("width")).Within(Tolerance));
                Assert.That(cell.Box.Height, Is.EqualTo(box.Value<double>("height")).Within(Tolerance));
                Assert.That(cell.Box.Skew, Is.EqualTo(box.Value<double>("skew")).Within(Tolerance));
            }
            // who stands where
            foreach (var slot in ((JArray)want["slots"]).OfType<JObject>())
            {
                var mine = built.Plan.Slots.First(s => s.Id == slot.Value<string>("id"));
                Assert.That(mine.Cell, Is.EqualTo(slot.Value<string>("cell")), slot["id"] + " cell");
                Assert.That(mine.X, Is.EqualTo(slot.Value<double>("x")).Within(Tolerance));
                Assert.That(mine.Ground, Is.EqualTo(slot.Value<double>("ground")).Within(Tolerance));
            }
            // the inputs the oracle fed the fit
            foreach (var a in ((JArray)want["actors"]).OfType<JObject>())
            {
                var mine = built.Actors.First(x => x.Id == a.Value<string>("id"));
                Assert.That(mine.Ratio, Is.EqualTo(a.Value<double>("ratio")).Within(Tolerance), a["id"] + " ratio");
                Assert.That(mine.Multiplier, Is.EqualTo(a.Value<double>("multiplier")).Within(Tolerance), a["id"] + " multiplier");
                Assert.That(mine.VisibleWidth, Is.EqualTo(a.Value<double>("visibleWidth")).Within(Tolerance), a["id"] + " width");
                Assert.That(mine.Leading, Is.EqualTo(a.Value<double>("leading")).Within(Tolerance), a["id"] + " leading");
                if (a["stature"] != null) Assert.That(mine.Stature, Is.EqualTo(a.Value<string>("stature")), a["id"] + " stature");
            }
            // the fit and the placement
            var sizes = BattlefieldFormation.Fit(Data, width, height, narrow, built.Actors);
            foreach (var s in ((JArray)want["sizes"]).OfType<JObject>())
            {
                var mine = sizes.First(x => x.Id == s.Value<string>("id"));
                Assert.That(mine.VisibleHeight, Is.EqualTo(s.Value<double>("visibleHeight")).Within(Tolerance), s["id"] + " fit height");
                Assert.That(mine.Scale, Is.EqualTo(s.Value<double>("scale")).Within(Tolerance), s["id"] + " fit scale");
            }
            var composed = BattlefieldFormation.Compose(Data, width, built.Actors, sizes);
            foreach (var s in ((JArray)want["composed"]).OfType<JObject>())
            {
                var mine = composed.First(x => x.Id == s.Value<string>("id"));
                Assert.That(mine.X, Is.EqualTo(s.Value<double>("x")).Within(Tolerance), s["id"] + " x");
                Assert.That(mine.Ground, Is.EqualTo(s.Value<double>("ground")).Within(Tolerance), s["id"] + " ground");
                Assert.That(mine.VisibleHeight, Is.EqualTo(s.Value<double>("visibleHeight")).Within(Tolerance), s["id"] + " height");
                Assert.That(mine.Scale, Is.EqualTo(s.Value<double>("scale")).Within(Tolerance), s["id"] + " scale");
            }
        }

        [TestCaseSource(nameof(Scenarios))]
        public void TheShippedBoardIsMeasuredWhereThePortPutsIt(int index)
        {
            var want = (JObject)((JArray)Read("board.json")["scenarios"])[index];
            var field = (JObject)want["field"];
            var width = field.Value<double>("width");
            var height = field.Value<double>("height");
            var narrow = want.Value<string>("layout") == "narrow";
            var enemies = ((JArray)want["combatants"]).OfType<JObject>().Where(c => c.Value<string>("enemyId") != null).Select(c => c.Value<string>("enemyId")).ToList();
            var built = Build(width, height, narrow, want.Value<string>("formFactor"), enemies);
            var composed = BattlefieldFormation.Compose(Data, width, built.Actors, BattlefieldFormation.Fit(Data, width, height, narrow, built.Actors));
            foreach (var c in ((JArray)want["combatants"]).OfType<JObject>())
            {
                var mine = composed.First(x => x.Id == c.Value<string>("id"));
                Assert.That(mine.Slot.Cell, Is.EqualTo(c.Value<string>("cell")), c["id"] + " cell");
                Assert.That(mine.X, Is.EqualTo(c.Value<double>("x")).Within(MeasuredTolerance), c["id"] + " x");
                Assert.That(mine.Ground, Is.EqualTo(c.Value<double>("groundY") - field.Value<double>("top")).Within(MeasuredTolerance), c["id"] + " ground");
                Assert.That(mine.VisibleHeight, Is.EqualTo(c.Value<double>("visibleHeight")).Within(MeasuredTolerance), c["id"] + " height");
                if (c["miniHudWidth"] != null && !double.IsNaN(c.Value<double>("miniHudWidth")))
                {
                    Assert.That(mine.Slot.MiniHudLeft, Is.EqualTo(c.Value<double>("miniHudLeft")).Within(MeasuredTolerance), c["id"] + " mini hud left");
                    Assert.That(mine.Slot.MiniHudWidth, Is.EqualTo(c.Value<double>("miniHudWidth")).Within(MeasuredTolerance), c["id"] + " mini hud width");
                }
            }
            foreach (var cell in ((JArray)want["cells"]).OfType<JObject>())
            {
                var mine = built.Plan.VisualCells.First(c => c.Cell == cell.Value<string>("cell") && c.Enemy == (cell.Value<string>("side") == "enemy"));
                Assert.That(mine.X, Is.EqualTo(cell.Value<double>("x")).Within(MeasuredTolerance), cell["cell"] + " x");
                Assert.That(mine.Ground, Is.EqualTo(cell.Value<double>("ground")).Within(MeasuredTolerance), cell["cell"] + " ground");
            }
        }

        [Test]
        public void TheBoardHoldsFourEnemiesInTheFrontRankOfTheLastLane()
        {
            var plan = BattlefieldFormation.Plan(Data, 1920, 529, false, "desktop", 4, 1);
            BattlefieldFormation.Seat(Data, plan, new[] { "player" }, new[] { "e1", "e2", "e3", "e4" });
            Assert.That(plan.Slots.Select(s => s.Cell), Is.EqualTo(new[] { "C1", "C4", "A4", "B4", "C3" }));
            Assert.That(plan.Slots.Last().Rank, Is.EqualTo(1));
        }

        [Test]
        public void PastTheGridsCapacityAnEnemySharesACell()
        {
            var ids = Enumerable.Range(1, 8).Select(n => "e" + n).ToList();
            var plan = BattlefieldFormation.Plan(Data, 1920, 529, false, "desktop", ids.Count, 1);
            BattlefieldFormation.Seat(Data, plan, new[] { "player" }, ids);
            Assert.That(plan.Rows, Is.EqualTo(4));
            Assert.That(plan.Slots.Count, Is.EqualTo(9));
        }
    }
}
