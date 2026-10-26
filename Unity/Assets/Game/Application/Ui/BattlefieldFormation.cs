using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;

namespace Ashen.App.Ui
{
    /// <summary>The grid of one fight: the cells of both sides, the cells the lattice draws and the cell each fighter stands on.</summary>
    public sealed class BattlefieldPlan
    {
        public int Rows;
        public List<BattlefieldCell> Cells = new List<BattlefieldCell>();
        public List<BattlefieldCell> VisualCells = new List<BattlefieldCell>();
        public List<BattlefieldCell> Slots = new List<BattlefieldCell>();
        public double Ground;
    }

    /// <summary>
    /// A fighter as the sprite fit sees it (ui/components/battlefieldStage.js reads these from the DOM): its slot, what its stage reports of its figure, the scale the presentation settings ask of it and the
    /// room the readouts above it need.
    /// </summary>
    public sealed class BattlefieldActor
    {
        public string Id;
        public bool Enemy;
        public string EnemyId;
        public string Stature;

        /// <summary>combatSpriteRatio of the stature (1 for a normal enemy and the party).</summary>
        public double Ratio;

        /// <summary>The presentation multiplier: the row's scale, the side's sprite scale and the display scale.</summary>
        public double Multiplier;

        /// <summary>The height and the width of the figure's idle envelope in the stage's own pixels (the stage's box).</summary>
        public double VisibleHeight;
        public double VisibleWidth;
        public double BoxHeight;

        /// <summary>The room reserved above the figure for its readouts (leading) and the header's keep-out above it (artLeading).</summary>
        public double Leading;
        public double ArtLeading;
        public BattlefieldCell Slot;
    }

    /// <summary>The fit and the placement of one fighter: the foot (x, ground) it stands on, the scale of its stage and the visible height that results, in the field's client pixels.</summary>
    public sealed class BattlefieldPlacement
    {
        public string Id;
        public double X;
        public double Ground;
        public double Scale;
        public double VisibleHeight;
        public BattlefieldCell Slot;
    }

    /// <summary>
    /// The default (alternative) appearance's formation of the combat board, ported from the web: model/AlternativeFormationModel.js alternativeFormation and fitAlternativeSprites,
    /// CombatCompositionModel.js perspectiveGridComposition and insetPerspectiveFootprint, and the dimensions and slot order combatFormation hands them. Every length is the web's client
    /// pixel at its own layout (the field's size); the board scales them to its own. Pure arithmetic, compared with the web's own functions by the oracle (Tools/oracle-core2-battlefield.mjs).
    /// </summary>
    public static class BattlefieldFormation
    {
        /// <summary>The rows of the grid for a fight: three lanes, more when a side needs them (formationDimensions with the perspective grid).</summary>
        public static int RowsFor(BattlefieldData d, int enemies, int party)
        {
            var perSide = Math.Max(party, enemies);
            return Math.Min(d.Rows.Length, Math.Max(d.Lanes, Math.Max((int)Math.Ceiling(perSide / BattlefieldMath.Pair), party)));
        }

        /// <summary>The form factor the field is laid out for when the caller names none (alternativeFormation: mobile when narrow, tablet up to the tablet width, else desktop).</summary>
        public static string FormFactorFor(double width, bool narrow) =>
            narrow ? BattlefieldNames.FormFactorMobile : width <= BattlefieldMath.TabletMaxWidth ? BattlefieldNames.FormFactorTablet : BattlefieldNames.FormFactorDesktop;

        /// <summary>The cells, the lattice and the slots of a fight with <paramref name="enemies"/> enemies and a party of <paramref name="party"/> (alternativeFormation).</summary>
        public static BattlefieldPlan Plan(BattlefieldData d, double width, double height, bool narrow, string formFactor, int enemies, int party)
        {
            var rows = RowsFor(d, enemies, party);
            var profile = height < d.ShortBelowPx ? d.Short : d.Profiles.TryGetValue(formFactor ?? FormFactorFor(width, narrow), out var named) ? named : narrow ? d.Narrow : d.Wide;
            var lanes = rows;
            var rowHeight = height * profile.RowHeight;
            var baseline = height * (profile.Top + d.Bands.Count * profile.RowHeight);
            double Shear(double y) => profile.Skew * (baseline - y);
            var laneWidth = Math.Max(1, (width * (profile.Right - profile.Left) - Shear(height * profile.Top)) / lanes);
            BattlefieldCell Place(bool enemy, int row, int column, bool visualOnly)
            {
                if (!enemy && column > 0) return null;
                var rank = enemy ? Math.Min(BattlefieldCount.ExtraRank, column) : 0;
                var band = d.Bands[enemy ? rank : d.Bands.Count - 1];
                var lane = enemy ? row : lanes - 1 - row;
                var top = height * profile.Top + band.Index * rowHeight;
                var ground = top + band.AnchorY * rowHeight;
                var laneLeft = width * profile.Left + lane * laneWidth;
                var left = laneLeft + Shear(top + rowHeight * BattlefieldMath.Half);
                return new BattlefieldCell
                {
                    Cell = d.Rows[row].ToString() + (enemy ? d.Columns * BattlefieldCount.Pair - column : column + 1),
                    Enemy = enemy, Row = row, Column = column, Lane = lane, Rank = rank, VisualBand = d.VisualBands[band.Index].ToString(),
                    X = laneLeft + band.AnchorX * laneWidth + Shear(ground), Ground = ground, FitGround = ground,
                    Box = new BattlefieldBox { Left = left, Top = top, Width = laneWidth, Height = rowHeight, Skew = profile.Skew },
                    Width = Math.Min(laneWidth * d.BodyWidthFraction, d.BodyWidthMaxPx), ArtWidth = laneWidth,
                    MiniHudLeft = left + profile.Skew * rowHeight * BattlefieldMath.Half + d.MiniHudInsetPx, MiniHudWidth = laneWidth - BattlefieldMath.Pair * d.MiniHudInsetPx, MiniHudTop = top,
                    Depth = band.Depth, Layer = band.Layer, VisualOnly = visualOnly,
                };
            }
            var plan = new BattlefieldPlan { Rows = rows };
            for (var row = 0; row < rows; row++)
                foreach (var enemy in new[] { false, true })
                    for (var column = 0; column < d.Columns; column++)
                    {
                        var cell = Place(enemy, row, column, false);
                        if (cell != null) plan.Cells.Add(cell);
                    }
            plan.VisualCells.AddRange(plan.Cells);
            // The full lattice is shown with its extra band even when Core forbids extra occupancy: visual-only cells never enter the gameplay cell map.
            for (var row = 0; row < rows; row++)
            {
                var address = d.Rows[row].ToString() + (d.Columns * BattlefieldCount.Pair + 1);
                if (plan.Cells.Any(c => c.Cell == address)) continue;
                plan.VisualCells.Add(Place(true, row, d.Columns, true).WithCell(address));
            }
            plan.Ground = plan.Cells.Max(c => c.Ground);
            return plan;
        }

        private static BattlefieldCell WithCell(this BattlefieldCell cell, string address)
        {
            cell.Cell = address;
            return cell;
        }

        /// <summary>
        /// The slot of every fighter: the party takes its lane of the party's rank (the lead keeps the lane of its gameplay cell), the enemies fill the back rank (the last lane, the first, then the lanes between)
        /// and then the front rank, past the capacity of the grid they share a cell (alternativeFormation, perspectiveEnemyCellOrder).
        /// </summary>
        public static void Seat(BattlefieldData d, BattlefieldPlan plan, IReadOnlyList<string> party, IReadOnlyList<string> enemies)
        {
            var lanes = plan.Rows;
            var spawn = Math.Min(lanes - 1, Math.Max(0, d.Rows.IndexOf(d.PlayerSpawnRow)));
            var lead = party.Count > 0 ? lanes - 1 - spawn : 0;
            var friendLanes = new List<int> { lead };
            friendLanes.AddRange(Enumerable.Range(0, lanes).Where(i => i != lead));
            plan.Slots.Clear();
            for (var i = 0; i < party.Count; i++)
                plan.Slots.Add(plan.Cells.First(c => !c.Enemy && c.Rank == 0 && c.Lane == friendLanes[i % lanes]).Copy(party[i]));
            var back = new List<int> { lanes - 1, 0 };
            back.AddRange(Enumerable.Range(1, Math.Max(0, lanes - BattlefieldCount.Pair)));
            var front = Enumerable.Range(0, lanes).Reverse().ToList();
            var order = back.Select(lane => d.Rows[lane].ToString() + d.Columns * BattlefieldCount.Pair).Concat(front.Select(lane => d.Rows[lane].ToString() + (d.Columns * BattlefieldCount.Pair - 1))).ToList();
            var targets = order.Select(address => plan.Cells.FirstOrDefault(c => c.Enemy && c.Cell == address)).Where(c => c != null).ToList();
            for (var i = 0; i < enemies.Count; i++) plan.Slots.Add(targets[i < targets.Count ? i : i % targets.Count].Copy(enemies[i]));
        }

        // ------------------------------------------------------------------ fitAlternativeSprites

        /// <summary>
        /// The size every figure is fitted to (fitAlternativeSprites): the share of the field's height it asks for, held to its cell's width, the headroom above its foot and the screen's edges; an enemy of one
        /// stature takes the smallest height any of its stature came to, a low creature stays at half of it.
        /// </summary>
        public static List<BattlefieldPlacement> Fit(BattlefieldData d, double width, double height, bool narrow, IReadOnlyList<BattlefieldActor> actors)
        {
            var fits = new List<(BattlefieldActor A, bool Enemy, bool Low, double Height)>();
            foreach (var a in actors.Where(a => a.VisibleHeight > 0 && a.VisibleWidth > 0))
            {
                var enemy = a.Enemy;
                var sideCount = actors.Count(b => b.Enemy == a.Enemy);
                var low = enemy && a.EnemyId != null && d.Low.Contains(a.EnemyId);
                var ratio = !enemy ? 1 : low ? d.LowRatio : Math.Min(d.RatioCap, a.Ratio);
                var share = enemy ? (narrow ? d.RequestedEnemyNarrow : d.RequestedEnemyWide) : (narrow ? d.RequestedPlayerNarrow : d.RequestedPlayerWide);
                var requested = height * share * ratio * (enemy ? 1 : a.Slot.Depth * d.PlayerArtworkScale);
                var slot = a.Slot;
                var box = slot.Box;
                var boxWidth = box != null ? box.Width - Math.Abs(box.Skew) * box.Height - BattlefieldMath.Pair * Math.Abs(box.Skew * (box.Top + box.Height * BattlefieldMath.Half - slot.Ground)) : slot.ArtWidth;
                var widthLimit = Math.Min(width - d.WidthMargin, boxWidth != 0 ? Math.Max(1, boxWidth - BattlefieldMath.Pair * (d.FootprintInsetPx + BattlefieldMath.Half)) : double.PositiveInfinity);
                var enemyBox = enemy ? box : null;
                var cellCentre = enemyBox != null ? enemyBox.Left + enemyBox.Width * BattlefieldMath.Half + enemyBox.Skew * (enemyBox.Top + enemyBox.Height * BattlefieldMath.Half - slot.Ground) : slot.X;
                var edgeRoom = Math.Max(Math.Min(slot.X - d.EdgeMargin, width - slot.X - d.EdgeMargin), Math.Min(cellCentre - d.EdgeMargin, width - cellCentre - d.EdgeMargin));
                var multiplier = a.Multiplier != 0 ? a.Multiplier : 1;
                var shape = a.VisibleHeight / a.VisibleWidth;
                var shortFloor = height < d.ShortBelowPx ? Math.Min(box != null ? box.Height : 1, requested) : 1;
                var spaciousFloor = enemy && !narrow && height >= d.ShortBelowPx && sideCount <= d.SpaciousMaxEnemies
                    ? Min(height * d.SpaciousEnemyClassHeightFraction * ratio * Math.Min(1, multiplier), slot.Ground - a.ArtLeading - d.Headroom, widthLimit * shape, BattlefieldMath.Pair * edgeRoom * shape)
                    : 1;
                var visible = Math.Max(shortFloor, Math.Max(spaciousFloor, Min(requested * Math.Min(d.MultiplierCap, multiplier), slot.Ground - a.Leading - d.Headroom, widthLimit * shape, BattlefieldMath.Pair * edgeRoom * shape)));
                fits.Add((a, enemy, low, Math.Min(visible, widthLimit * shape)));
            }
            var classUnit = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var fit in fits.Where(f => f.Enemy))
            {
                var unit = fit.Height / (fit.Low ? d.LowRatio : 1);
                classUnit[fit.A.Stature] = classUnit.TryGetValue(fit.A.Stature, out var known) ? Math.Min(known, unit) : unit;
            }
            return fits.Select(fit =>
            {
                var visible = fit.Enemy ? classUnit[fit.A.Stature] * (fit.Low ? d.LowRatio : 1) : fit.Height;
                return new BattlefieldPlacement { Id = fit.A.Slot.Id, Scale = visible / fit.A.VisibleHeight, VisibleHeight = visible, X = fit.A.Slot.X, Slot = fit.A.Slot };
            }).ToList();
        }

        private static double Min(double a, double b, double c, double e) => Math.Min(Math.Min(a, b), Math.Min(c, e));

        // ------------------------------------------------------------------ perspectiveGridComposition

        /// <summary>
        /// Puts every fitted figure on its cell's foot anchor (perspectiveGridComposition): the screen's edges hold it, and an enemy's complete shadow envelope is held inside its skewed cell
        /// (insetPerspectiveFootprint). The fit never changes.
        /// </summary>
        public static List<BattlefieldPlacement> Compose(BattlefieldData d, double width, IReadOnlyList<BattlefieldActor> actors, IReadOnlyList<BattlefieldPlacement> sizes)
        {
            var result = new List<BattlefieldPlacement>();
            foreach (var size in sizes)
            {
                var actor = actors.First(a => a.Slot.Id == size.Id);
                var half = actor.VisibleWidth * size.Scale * BattlefieldMath.Half;
                var x = Math.Max(half + d.ScreenMargin, Math.Min(width - half - d.ScreenMargin, actor.Slot.X));
                double footX = x, ground = actor.Slot.Ground;
                if (actor.Enemy && actor.Slot.Box != null)
                {
                    var shadowHeight = (actor.BoxHeight > 0 ? actor.BoxHeight : actor.VisibleHeight > 0 ? actor.VisibleHeight : d.DefaultBoxHeight) * size.Scale * d.ShadowHeightShare;
                    (footX, ground) = InsetFootprint(d, actor.Slot, half * BattlefieldMath.Pair, shadowHeight);
                }
                result.Add(new BattlefieldPlacement { Id = size.Id, X = footX, Ground = ground, Scale = size.Scale, VisibleHeight = size.VisibleHeight, Slot = actor.Slot });
            }
            return result;
        }

        /// <summary>insetPerspectiveFootprint: the shadow's bounding envelope kept inside its skewed cell (the active kit draws an ellipse as tall as the share of the figure and as wide as its body).</summary>
        public static (double X, double Ground) InsetFootprint(BattlefieldData d, BattlefieldCell slot, double width, double height)
        {
            var box = slot.Box;
            var above = height * BattlefieldMath.Half;
            var below = height * BattlefieldMath.Half;
            var inset = d.FootprintInsetPx;
            var ground = Math.Max(box.Top + above + inset, Math.Min(box.Top + box.Height - below - inset, slot.Ground));
            double CentreAt(double y) => box.Left + box.Width * BattlefieldMath.Half + box.Skew * (box.Top + box.Height * BattlefieldMath.Half - y);
            var half = width * BattlefieldMath.Half + inset;
            var top = CentreAt(ground - above);
            var bottom = CentreAt(ground + below);
            var low = Math.Max(top, bottom) - box.Width * BattlefieldMath.Half + half;
            var high = Math.Min(top, bottom) + box.Width * BattlefieldMath.Half - half;
            return (low <= high ? Math.Max(low, Math.Min(high, slot.X)) : (low + high) * BattlefieldMath.Half, ground);
        }

        /// <summary>The presentation multiplier of a fighter in a row: the row's scale, the side's sprite scale and the display scale (battlefieldStage.js refresh).</summary>
        public static double Multiplier(BattlefieldData d, bool enemy, int row) =>
            (d.RowScale.TryGetValue(d.Rows[row].ToString(), out var rowScale) ? rowScale : 1) * (enemy ? d.EnemySpriteScale : d.PlayerSpriteScale) * d.DisplayScale;

        /// <summary>The room above a figure the fit reserves for its readouts (battlefieldStage.js: the cap or the stack's height, and the gap, whichever is more).</summary>
        public static double Leading(BattlefieldData d, double height, double stackHeight, double ceiling, bool compact) =>
            Math.Max(Math.Min(d.LeadingCap, height * d.LeadingHeightShare), stackHeight + ceiling + (compact ? d.OverheadGapCompact : d.OverheadGap));
    }
}
