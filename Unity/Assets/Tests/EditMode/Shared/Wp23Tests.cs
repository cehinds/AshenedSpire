using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-23 (D-1370..): the web's tooltip placement, the shrine lane, the player's View mode and UI size, and the hover-explanation delays, all engine-free. Suite: Presentation.
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class Wp23Tests
    {
        private static UiData _ui;
        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));
        private static TooltipPlacementRules Rules => Ui.Components.TooltipPlacement;

        [Test]
        public void TheTooltipRulesAreTheWebs()
        {
            Assert.That(Rules.Gap, Is.EqualTo(14), "kit.css --place-gap");
            Assert.That(Rules.Pad, Is.EqualTo(4), "fx.js placeAnchored pad");
            Assert.That(Rules.EdgeEpsilon, Is.EqualTo(0.5), "tooltipPlacement.json edgeEpsilonPx");
            Assert.That(Rules.FocusMs, Is.EqualTo(500), "tooltipHelp.focusMs");
        }

        [Test]
        public void ATooltipSitsAboveItsTriggerWhenThereIsRoom()
        {
            var anchor = new PlacementBox(600, 300, 40, 20);
            var box = TooltipPlacement.Place(anchor, 200, 60, 1280, 720, TooltipPlacementValues.Above, TooltipPlacementValues.AlignStart, Rules);
            Assert.That(box.Left, Is.EqualTo(600));
            Assert.That(box.Top, Is.EqualTo(300 - 60 - 14), "the gap above the trigger");
            var arrow = TooltipPlacement.ArrowFor(box, anchor, 7.5, 8, Rules);
            Assert.That(arrow.Edge, Is.EqualTo(TooltipPlacementValues.EdgeBottom), "the arrow is on the edge that faces the trigger");
            Assert.That(arrow.Along, Is.EqualTo(20), "and points at its centre");
        }

        [Test]
        public void ATooltipFlipsUnderATriggerAtTheTopOfTheScreen()
        {
            var anchor = new PlacementBox(600, 10, 40, 20);
            var box = TooltipPlacement.Place(anchor, 200, 60, 1280, 720, TooltipPlacementValues.Above, TooltipPlacementValues.AlignStart, Rules);
            Assert.That(box.Top, Is.EqualTo(10 + 20 + 14), "below the trigger when above does not fit");
            Assert.That(TooltipPlacement.ArrowFor(box, anchor, 7.5, 8, Rules).Edge, Is.EqualTo(TooltipPlacementValues.EdgeTop));
        }

        [Test]
        public void ATooltipAtTheSideEdgeKeepsInsideTheScreen()
        {
            var right = new PlacementBox(1230, 300, 40, 20);
            var box = TooltipPlacement.Place(right, 200, 60, 1280, 720, TooltipPlacementValues.Above, TooltipPlacementValues.AlignStart, Rules);
            Assert.That(box.Right, Is.LessThanOrEqualTo(1280 - 4 - 4), "the panel slides left of the edge");
            Assert.That(box.Top, Is.EqualTo(300 - 60 - 14), "still above");
            var left = new PlacementBox(1, 300, 10, 10);
            Assert.That(TooltipPlacement.Place(left, 200, 60, 1280, 720, TooltipPlacementValues.Above, TooltipPlacementValues.AlignStart, Rules).Left, Is.EqualTo(4), "and right of the left edge by the pad");
        }

        [Test]
        public void ATooltipThatFitsNowhereIsBoundedNotLost()
        {
            var anchor = new PlacementBox(10, 10, 20, 20);
            var box = TooltipPlacement.Place(anchor, 400, 300, 300, 200, TooltipPlacementValues.Above, TooltipPlacementValues.AlignStart, Rules);
            Assert.That(box.Left, Is.EqualTo(4), "a misplaced panel, never an absent one");
            Assert.That(box.Top, Is.EqualTo(4));
        }

        [Test]
        public void ATooltipBesideAnAnchorPointsSideways()
        {
            var anchor = new PlacementBox(300, 300, 40, 40);
            var box = TooltipPlacement.Place(anchor, 200, 60, 1280, 720, TooltipPlacementValues.Right, TooltipPlacementValues.AlignStart, Rules);
            Assert.That(box.Left, Is.EqualTo(300 + 40 + 14));
            Assert.That(TooltipPlacement.ArrowFor(box, anchor, 7.5, 8, Rules).Edge, Is.EqualTo(TooltipPlacementValues.EdgeLeft));
        }

        [Test]
        public void AHoverDelayIsASecondsChoiceAsMilliseconds()
        {
            Assert.That(TooltipPolicy.Millis("0.25s", 7), Is.EqualTo(250));
            Assert.That(TooltipPolicy.Millis("1.5s", 7), Is.EqualTo(1500));
            Assert.That(TooltipPolicy.Millis("2s", 7), Is.EqualTo(2000), "the Lit city pause uses the same words");
            Assert.That(TooltipPolicy.Millis("soon", 7), Is.EqualTo(7), "a value that is not seconds is the fallback");
            Assert.That(TooltipPolicy.Millis(null, 7), Is.EqualTo(7));
        }

        // ------------------------------------------------------------------ the spoken step's figure close-up (web PortraitCropModel.closeUpPlacement, measured on the web's own event-dialogue shot)

        [Test]
        public void TheDialogueRulesAreTheW4cScenesAndEveryFigureHasAVisibleBox()
        {
            var rules = Ui.Components.Dialogue;
            Assert.That(rules.InsetVw, Is.EqualTo(2.5));
            Assert.That(rules.SlotWidthVw, Is.EqualTo(20));
            Assert.That(rules.VisibleFraction, Is.EqualTo(1d / 3d).Within(0.001), "the upper third of the figure");
            Assert.That(rules.VisibleFractionCompact, Is.EqualTo(1), "a phone shows the whole figure");
            Assert.That(rules.MaxOuterOverflowVw, Is.EqualTo(8));
            Assert.That(rules.MinVisibleHeightVh, Is.EqualTo(22));
            foreach (var id in new[] { "class.reaver.sprite.ember", "class.starseer.sprite.ember", "enemy.graveWisp.base", "enemy.emberStarvedPilgrim.base" })
            {
                var box = rules.BoxOf(id);
                Assert.That(rules.Figures.ContainsKey(id), Is.True, id);
                Assert.That(box.Height, Is.InRange(0.1, 1.0), id);
                Assert.That(box.Width, Is.InRange(0.1, 1.0), id);
            }
        }

        [Test]
        public void AFigureOfTheWholeBoxFillsTheSlotToTheRevealLineAsTheWebsNpcDoes()
        {
            // The web's graveWisp at 1920x1080 in its own local px (zoom 1.48: a frame 1297 x 729.7): its art 151.236 x 190, the slot top at 84.76 and the context band's top at 364.75.
            var rules = Ui.Components.Dialogue;
            var lane = FigureCrop.LaneFor(true, 1297.0, rules);
            Assert.That(lane.Left, Is.EqualTo(660.7).Within(0.5));
            Assert.That(lane.Width, Is.EqualTo(604.1).Within(0.5), "half the frame less the insets and the gap (24 px at least)");
            var left = FigureCrop.LaneFor(false, 1297.0, rules);
            Assert.That(left.Left, Is.EqualTo(32.4).Within(0.1));
            var box = new FigureBox { Top = 0, Height = 1, CenterX = UiMath.Half, Width = 1 };
            var placed = FigureCrop.Place(box, 151.236, 190, 1135.05, 84.76, 364.75, lane, false, 1297.0, 729.7, rules);
            Assert.That(placed.Scale, Is.EqualTo(4.4198).Within(0.01), "a third of the figure's height spans the slot's top to the reveal line");
            Assert.That(placed.Y, Is.EqualTo(84.75).Within(0.5));
            Assert.That(placed.X, Is.EqualTo(660.5).Within(0.7), "leaning out of its lane to the inner edge: the pinned centre");
            Assert.That(placed.VisibleWidth, Is.EqualTo(667.4).Within(2), "the lane (604) plus the lean the figure needs");
        }

        [Test]
        public void AFigureTooWideForItsLaneLeansOutwardThenShrinksButNeverBelowTheFloor()
        {
            var rules = Ui.Components.Dialogue;
            var lane = FigureCrop.LaneFor(false, 1297.0, rules);
            var allowed = lane.Width + 1297.0 * 0.08;
            // A very wide figure, the whole of it shown (a phone): it shrinks to the lane plus the lean the config allows, and its centre stays in the lane.
            var wide = new FigureBox { Top = 0, Height = 1, CenterX = UiMath.Half, Width = 1 };
            var placed = FigureCrop.Place(wide, 3000, 800, 160, 84.76, 364.75, lane, true, 1297.0, 729.7, rules);
            Assert.That(placed.VisibleWidth, Is.EqualTo(allowed).Within(0.01), "never past the lane plus the allowed lean");
            Assert.That(placed.X + 3000 * UiMath.Half * placed.Scale, Is.InRange(lane.Left, lane.Left + lane.Width), "its centre stays inside its lane");
            // The upper third of the same figure would be shown too small (22 % of the frame is the least): the floor wins over the lane, as the web's does.
            var third = FigureCrop.Place(wide, 3000, 800, 160, 84.76, 364.75, lane, false, 1297.0, 729.7, rules);
            Assert.That(800 * (1d / 3d) * third.Scale, Is.GreaterThanOrEqualTo(729.7 * 0.22 - 0.5), "the shown part is never below the floor");
        }

        private static JObject Graph() => JObject.Parse(@"{
            ""startIds"": [""a1"", ""a2""],
            ""nodes"": {
                ""a1"": { ""type"": ""monster"", ""floor"": 1, ""next"": [""b1""] },
                ""a2"": { ""type"": ""monster"", ""floor"": 1, ""next"": [""b2""] },
                ""b1"": { ""type"": ""event"", ""floor"": 2, ""next"": [""c1""] },
                ""b2"": { ""type"": ""shrine"", ""floor"": 2, ""next"": [""c1""] },
                ""c1"": { ""type"": ""shrine"", ""floor"": 3, ""next"": [""d1""] },
                ""d1"": { ""type"": ""boss"", ""floor"": 4, ""next"": [] }
            } }");

        [Test]
        public void TheShrineLaneIsTheWalkToTheNearestShrineAhead()
        {
            var graph = Graph();
            Assert.That(MapFog.ShrineLane(graph, null), Is.EqualTo(new[] { "a2", "b2" }), "from the entrances: the nearest shrine is two floors up, and the walk starts at its door");
            Assert.That(MapFog.ShrineLane(graph, "a1"), Is.EqualTo(new[] { "a1", "b1", "c1" }), "from a1 the only shrine ahead is c1");
            Assert.That(MapFog.ShrineLane(graph, "b2"), Is.EqualTo(new[] { "b2", "c1" }), "standing on a shrine the nearest is the next one, never itself");
            Assert.That(MapFog.ShrineLane(graph, "c1"), Is.Empty, "none ahead");
        }

        [Test]
        public void ViewModeAndUiSizeShapeTheLayout()
        {
            try
            {
                var rules = Ui.Layout;
                LayoutClassifier.Preset = SettingsValues.ViewAuto;
                LayoutClassifier.Size = SettingsValues.SizeAuto;
                var auto = LayoutClassifier.Classify(1920, 1080, rules);
                Assert.That(auto.Narrow, Is.False);
                LayoutClassifier.Preset = SettingsValues.ViewMobile;
                Assert.That(LayoutClassifier.Classify(1920, 1080, rules).Narrow, Is.True, "Mobile is the narrow layout on any window");
                LayoutClassifier.Preset = SettingsValues.ViewDesktop;
                Assert.That(LayoutClassifier.Classify(390, 844, rules).Narrow, Is.False, "Desktop is the wide layout on any window");
                LayoutClassifier.Preset = SettingsValues.ViewAuto;
                LayoutClassifier.Size = "S";
                Assert.That(LayoutClassifier.Classify(1920, 1080, rules).Scale, Is.LessThan(auto.Scale), "a fixed S is smaller than the fit of a big window");
                LayoutClassifier.Size = "XL";
                Assert.That(LayoutClassifier.Classify(1920, 1080, rules).Scale, Is.LessThanOrEqualTo(auto.Scale), "XL (zoom 1.45) is a little under the 1.48 a 1080p window fits");
                Assert.That(LayoutClassifier.Classify(1280, 720, rules).Scale, Is.EqualTo(LayoutClassifier.Scale(1280, 720, rules.Wide)), "XL gets as much as fits: on a small window that is the fit");
                Assert.That(LayoutClassifier.Classify(390, 844, rules).Scale, Is.EqualTo(LayoutClassifier.Scale(390, 844, rules.Narrow)), "the phone layout ignores UI size");
            }
            finally
            {
                LayoutClassifier.Preset = SettingsValues.ViewAuto;
                LayoutClassifier.Size = SettingsValues.SizeAuto;
            }
        }
    }
}
