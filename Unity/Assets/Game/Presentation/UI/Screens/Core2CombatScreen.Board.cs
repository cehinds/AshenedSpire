using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The board's formation (P-4, D-1540..), laid out as src/ui/components/battlefieldStage.js lays out the web's: one perspective grid over the field, every fighter on the foot anchor of its cell,
    /// fitted by <see cref="BattlefieldFormation"/> to its cell's width, the headroom over its foot and the screen's edges, with its readout hung from the cell's width above the group's tallest
    /// figure and its floor shadow as wide as the frame on show. The formation is computed in the web's own client pixels (the field's size times the layout's scale) and drawn in the board's.
    /// </summary>
    public sealed partial class Core2CombatScreen
    {
        private BattlefieldGrid _grid;
        private Texture2D _floorShadow;
        private bool _boardWired;

        private BattlefieldData Board => Ui.Data.Battlefield;

        private void WireBoard()
        {
            if (_boardWired) return;
            _boardWired = true;
            var host = Root.Q(UiNames.StageGrid);
            if (host == null) return;
            _grid = new BattlefieldGrid();
            host.Add(_grid);
            Root.Q(UiNames.EnemyRow).RegisterCallback<GeometryChangedEvent>(_ => LayoutBoard());
        }

        /// <summary>The web's client pixels per pixel of the board at this layout.</summary>
        private double BoardScale(bool narrow) => Ui.Data.Tokens.Ratio(narrow ? RatioKeys.BoardScaleNarrow : RatioKeys.BoardScale);

        /// <summary>Lays the fighters and the lattice out for the fight on the field's present size.</summary>
        private void LayoutBoard()
        {
            WireBoard();
            var data = Board;
            var field = Root.Q(UiNames.EnemyRow);
            if (data == null || field == null || _player?.Data == null || View == null) return;
            var w = field.resolvedStyle.width;
            var h = field.resolvedStyle.height;
            if (float.IsNaN(w) || float.IsNaN(h) || w <= 0 || h <= 0) return;
            var narrow = Nav.Layout?.Narrow ?? false;
            var k = BoardScale(narrow);
            double width = w * k, height = h * k;
            var enemies = View.Enemies.ToList();
            var enemyIds = enemies.Select(e => e.Id).ToList();
            var plan = BattlefieldFormation.Plan(data, width, height, narrow, BattlefieldFormation.FormFactorFor(width, narrow), enemyIds.Count, 1);
            BattlefieldFormation.Seat(data, plan, new[] { View.Player.Id }, enemyIds);
            var actors = plan.Slots.Select(slot => ActorOf(data, slot, enemies, height)).ToList();
            var placed = BattlefieldFormation.Compose(data, width, actors, BattlefieldFormation.Fit(data, width, height, narrow, actors));
            DrawGrid(data, plan, k);
            // A band's readouts hang from one height: the tallest figure of the band, the group's own height and a gap above it (battlefieldStage.js miniHudTop).
            var groupTop = placed.GroupBy(p => p.Slot.VisualBand).ToDictionary(g => g.Key, g => g.Min(p => p.Ground - p.VisibleHeight) - data.MiniHudGroupHeightPx - data.MiniHudSpriteGapPx);
            var shadow = FloorShadow();
            foreach (var p in placed.OrderBy(p => p.Slot.Layer))
            {
                var fighter = AnchorOf(p.Id);
                if (fighter == null) continue;
                var actor = actors.First(a => a.Id == p.Id);
                var top = groupTop[p.Slot.VisualBand];
                var shadowHeight = Ui.Data.Tokens.Ratio(RatioKeys.BoardFloorShadowH) * p.Scale;
                var shadowWidth = ShadowWidth(data, fighter, p, actor);
                var shadowBottom = p.Ground + Ui.Data.Tokens.Ratio(RatioKeys.BoardFloorShadowDrop) * p.Scale;
                var artWidth = actor.VisibleWidth * p.Scale;
                fighter.PlaceOnBoard(new FighterPlace
                {
                    Left = (float)(p.Slot.MiniHudLeft / k), Top = (float)(top / k), Width = (float)(p.Slot.MiniHudWidth / k), Height = (float)((p.Ground - top) / k),
                    ArtLeft = (float)((p.X - p.Slot.MiniHudLeft - artWidth * UiMath.Half) / k), ArtTop = (float)((p.Ground - p.VisibleHeight - top) / k), ArtWidth = (float)(artWidth / k), ArtHeight = (float)(p.VisibleHeight / k),
                    ShadowLeft = (float)((p.X - shadowWidth * UiMath.Half - p.Slot.MiniHudLeft) / k), ShadowTop = (float)((shadowBottom - shadowHeight - top) / k), ShadowWidth = (float)(shadowWidth / k), ShadowHeight = (float)(shadowHeight / k),
                }, shadow);
                // Depth: the party paints over the enemies, a rank over the one behind it (the web's --combat-depth).
                fighter.BringToFront();
            }
        }

        private BattlefieldActor ActorOf(BattlefieldData data, BattlefieldCell slot, IReadOnlyList<Core2FighterView> enemies, double height)
        {
            var enemy = slot.Id != View.Player.Id;
            var view = enemy ? enemies.First(e => e.Id == slot.Id) : null;
            var stage = Ui.Data.EnemyStage;
            var figure = enemy ? stage.Figure(view.DefId) : null;
            var player = enemy ? null : Ui.Data.CardStage.Class(_run.ClassId);
            var boxHeight = enemy ? stage.Height : player?.StageHeight ?? stage.Height;
            var stature = enemy ? data.Stature(view.DefId) : BattlefieldNames.StatureNormal;
            return new BattlefieldActor
            {
                Id = slot.Id, Enemy = enemy, EnemyId = view?.DefId, Stature = stature, Ratio = enemy ? data.SpriteRatio(stature, view.DefId) : 1,
                Multiplier = BattlefieldFormation.Multiplier(data, enemy, slot.Row),
                BoxHeight = boxHeight, VisibleHeight = boxHeight,
                VisibleWidth = enemy ? (figure != null ? figure.IdleWidth * boxHeight / figure.IdleHeight : boxHeight) : player?.IdleWidth ?? boxHeight,
                Leading = BattlefieldFormation.Leading(data, height, 0, 0, false), ArtLeading = 0, Slot = slot,
            };
        }

        /// <summary>
        /// The width of a fighter's floor shadow in client pixels (syncFloorShadows): the opaque width of the frame on show, scaled with the figure; a figure with no measured frame takes the web's default share
        /// of its box.
        /// </summary>
        private double ShadowWidth(BattlefieldData data, Fighter fighter, BattlefieldPlacement placement, BattlefieldActor actor)
        {
            var stage = Ui.Data.EnemyStage;
            if (actor.Enemy && fighter.HasEnemyStage)
            {
                var units = fighter.EnemyInkUnits();
                if (units.HasValue) return units.Value * placement.VisibleHeight / stage.Height;
            }
            else if (!actor.Enemy)
            {
                var cls = Ui.Data.CardStage.Class(_run.ClassId);
                var ready = cls?.Frame(StageNames.Ready);
                if (ready != null && Ui.Data.FigureInk.TryGet(ready.Art, out var ink)) return ink.Share * ready.W * placement.VisibleHeight / cls.Unit;
            }
            return actor.VisibleWidth * placement.Scale * Ui.Data.Tokens.Ratio(RatioKeys.BoardShadowDefaultShare);
        }

        // ------------------------------------------------------------------ the lattice

        /// <summary>
        /// The outline of every cell of the lattice, one uniform stroke (perspectiveCellOutlinePath): its left edge, its top edge, its right edge for the last lane and its bottom edge for the party's band; the
        /// cell's box slides its rows by the skew, so its top edge sits right of its bottom edge.
        /// </summary>
        private void DrawGrid(BattlefieldData data, BattlefieldPlan plan, double k)
        {
            if (_grid == null) return;
            var lines = new List<GridLine>();
            var last = data.VisualBands[data.VisualBands.Length - 1].ToString();
            Vector2 P(double x, double y) => new Vector2((float)(x / k), (float)(y / k));
            foreach (var cell in plan.VisualCells)
            {
                var b = cell.Box;
                var slide = b.Skew * b.Height * UiMath.Half;
                var topLeft = P(b.Left + slide, b.Top);
                var topRight = P(b.Left + b.Width + slide, b.Top);
                var bottomRight = P(b.Left + b.Width - slide, b.Top + b.Height);
                var bottomLeft = P(b.Left - slide, b.Top + b.Height);
                lines.Add(new GridLine { From = bottomLeft, To = topLeft });
                lines.Add(new GridLine { From = topLeft, To = topRight });
                if (cell.Lane == plan.Rows - 1) lines.Add(new GridLine { From = topRight, To = bottomRight });
                if (cell.VisualBand == last) lines.Add(new GridLine { From = bottomRight, To = bottomLeft });
            }
            _grid.SetLines(lines, Ui.TokenColor(TokenKeys.BoardGridLine), (float)(Ui.Data.Tokens.Ratio(RatioKeys.BoardGridLinePx) / k));
        }

        // ------------------------------------------------------------------ the floor shadow (styles/combat.css .sprite::before)

        /// <summary>
        /// The dark ellipse under a figure: radial-gradient(ellipse, rgba(10, 8, 6, .7), rgba(10, 8, 6, .28) 45%, transparent 72%) over a box with border-radius 50%. UI Toolkit has no gradients, so the
        /// gradient is drawn once into a small texture and stretched to the box.
        /// </summary>
        private Texture2D FloorShadow()
        {
            if (_floorShadow != null) return _floorShadow;
            var tokens = Ui.Data.Tokens;
            var w = (int)tokens.Ratio(RatioKeys.BoardShadowTexW);
            var h = (int)tokens.Ratio(RatioKeys.BoardShadowTexH);
            var colour = Ui.TokenColor(TokenKeys.BoardShadow);
            double core = tokens.Ratio(RatioKeys.BoardShadowCore), mid = tokens.Ratio(RatioKeys.BoardShadowMid), midStop = tokens.Ratio(RatioKeys.BoardShadowMidStop), edge = tokens.Ratio(RatioKeys.BoardShadowEdge), reach = tokens.Ratio(RatioKeys.BoardShadowReach);
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var dx = (x + UiMath.Half) / w * BattlefieldMath.Pair - 1;
                    var dy = (y + UiMath.Half) / h * (double)GridMesh.Second - 1;
                    // The gradient's ending ellipse passes through the box's corners (farthest-corner): its radii are the box's, scaled by the corner's reach.
                    var r = Math.Sqrt(dx * dx + dy * dy) / reach;
                    var inside = dx * dx + dy * dy <= 1;
                    var alpha = !inside ? 0d : r >= edge ? 0d : r <= midStop ? core + (mid - core) * (r / midStop) : mid * (1 - (r - midStop) / (edge - midStop));
                    pixels[y * w + x] = new Color(colour.r, colour.g, colour.b, (float)alpha);
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return _floorShadow = texture;
        }
    }
}
