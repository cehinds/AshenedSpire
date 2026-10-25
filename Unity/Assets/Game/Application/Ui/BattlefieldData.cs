using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One profile of the perspective grid: where the grid sits in the field and how far its rows slide (content/config/ui/presentation/combatFormationModel.json perspectiveGrid).</summary>
    public sealed class BattlefieldProfile
    {
        public double Left;
        public double Right;
        public double Top;
        public double RowHeight;
        public double Skew;
    }

    /// <summary>One band of the grid (enemy back, enemy front, extra combatants, the party): its row, where a foot stands in its cell, its depth and its layer.</summary>
    public sealed class BattlefieldBand
    {
        public string Id;
        public int Index;
        public double AnchorX;
        public double AnchorY;
        public double Depth;
        public double Layer;
    }

    /// <summary>The parallelogram of a cell: the box and how far its rows slide to the right per pixel above the party's baseline.</summary>
    public sealed class BattlefieldBox
    {
        public double Left;
        public double Top;
        public double Width;
        public double Height;
        public double Skew;
    }

    /// <summary>
    /// A cell of the perspective grid (model/AlternativeFormationModel.js alternativeFormation): its gameplay address, lane and rank, the band it belongs to, the foot anchor (x, ground) its figure stands on,
    /// its parallelogram and the box of its target readout.
    /// </summary>
    public sealed class BattlefieldCell
    {
        public string Id;
        public string Cell;
        public bool Enemy;
        public int Row;
        public int Column;
        public int Lane;
        public int Rank;
        public string VisualBand;
        public double X;
        public double Ground;
        public double FitGround;
        public BattlefieldBox Box;
        public double Width;
        public double ArtWidth;
        public double MiniHudLeft;
        public double MiniHudWidth;
        public double MiniHudTop;
        public double Depth;
        public double Layer;
        public bool VisualOnly;

        /// <summary>The cell as a slot of a fighter: the same anchor and box under the fighter's id.</summary>
        public BattlefieldCell Copy(string id)
        {
            var copy = (BattlefieldCell)MemberwiseClone();
            copy.Id = id;
            return copy;
        }
    }

    /// <summary>
    /// ui/battlefield.json (P-4, D-1540..): the configuration of the web's combat board layout in its own client pixels: the perspective grid's profiles and bands, the party's and the enemies' scale
    /// settings at the web's defaults, the stature of every enemy and the literals the sprite fit keeps inline. Engine-free; <see cref="BattlefieldFormation"/> does the arithmetic.
    /// </summary>
    public sealed class BattlefieldData
    {
        public readonly string Rows;
        public readonly string VisualBands;
        public readonly BattlefieldProfile Wide;
        public readonly BattlefieldProfile Narrow;
        public readonly BattlefieldProfile Short;
        public readonly IReadOnlyDictionary<string, BattlefieldProfile> Profiles;
        public readonly IReadOnlyList<BattlefieldBand> Bands;
        public readonly double ShortBelowPx;
        public readonly double BodyWidthFraction;
        public readonly double BodyWidthMaxPx;
        public readonly double FootprintInsetPx;
        public readonly double PlayerArtworkScale;
        public readonly double SpaciousEnemyClassHeightFraction;
        public readonly double MiniHudInsetPx;
        public readonly double MiniHudGroupHeightPx;
        public readonly double MiniHudSpriteGapPx;
        public readonly double PlayerSpriteScale;
        public readonly double EnemySpriteScale;
        public readonly double DisplayScale;
        public readonly IReadOnlyDictionary<string, double> RowScale;
        public readonly int Columns;
        public readonly int Lanes;
        public readonly string PlayerSpawnRow;
        public readonly double RequestedEnemyWide;
        public readonly double RequestedEnemyNarrow;
        public readonly double RequestedPlayerWide;
        public readonly double RequestedPlayerNarrow;
        public readonly double LowRatio;
        public readonly double RatioCap;
        public readonly double MultiplierCap;
        public readonly double WidthMargin;
        public readonly double EdgeMargin;
        public readonly double Headroom;
        public readonly int SpaciousMaxEnemies;
        public readonly double LeadingCap;
        public readonly double LeadingHeightShare;
        public readonly double OverheadGap;
        public readonly double OverheadGapCompact;
        public readonly double ScreenMargin;
        public readonly double ShadowHeightShare;
        public readonly double DefaultBoxHeight;
        public readonly double LargeRatio;
        public readonly double HugeRatio;
        public readonly IReadOnlyDictionary<string, double> BossScale;
        public readonly IReadOnlyDictionary<string, string> StatureOf;
        public readonly IReadOnlyCollection<string> Low;

        public BattlefieldData(JObject json)
        {
            Rows = json.Value<string>(BattlefieldKeys.Rows);
            VisualBands = json.Value<string>(BattlefieldKeys.VisualBands);
            var grid = (JObject)json[BattlefieldKeys.Grid];
            Wide = Profile((JObject)grid[BattlefieldKeys.Wide]);
            Narrow = Profile((JObject)grid[BattlefieldKeys.Narrow]);
            Short = Profile((JObject)grid[BattlefieldKeys.Short]);
            Profiles = ((JObject)grid[BattlefieldKeys.Profiles]).Properties().ToDictionary(p => p.Name, p => Profile((JObject)p.Value), StringComparer.Ordinal);
            Bands = ((JArray)grid[BattlefieldKeys.Bands]).Select(b => new BattlefieldBand
            {
                Id = b.Value<string>(BattlefieldKeys.Id),
                Index = b.Value<int>(BattlefieldKeys.Index),
                AnchorX = b[BattlefieldKeys.Anchor].Value<double>(BattlefieldKeys.X),
                AnchorY = b[BattlefieldKeys.Anchor].Value<double>(BattlefieldKeys.Y),
                Depth = b.Value<double>(BattlefieldKeys.Depth),
                Layer = b.Value<double>(BattlefieldKeys.Layer),
            }).ToList();
            ShortBelowPx = grid.Value<double>(BattlefieldKeys.ShortBelowPx);
            BodyWidthFraction = grid.Value<double>(BattlefieldKeys.BodyWidthFraction);
            BodyWidthMaxPx = grid.Value<double>(BattlefieldKeys.BodyWidthMaxPx);
            FootprintInsetPx = grid.Value<double>(BattlefieldKeys.FootprintInsetPx);
            PlayerArtworkScale = grid.Value<double>(BattlefieldKeys.PlayerArtworkScale);
            SpaciousEnemyClassHeightFraction = grid.Value<double>(BattlefieldKeys.SpaciousEnemyClassHeightFraction);
            MiniHudInsetPx = grid.Value<double>(BattlefieldKeys.MiniHudInsetPx);
            MiniHudGroupHeightPx = grid.Value<double>(BattlefieldKeys.MiniHudGroupHeightPx);
            MiniHudSpriteGapPx = grid.Value<double>(BattlefieldKeys.MiniHudSpriteGapPx);
            var presentation = (JObject)json[BattlefieldKeys.Presentation];
            PlayerSpriteScale = presentation.Value<double>(BattlefieldKeys.PlayerSpriteScale);
            EnemySpriteScale = presentation.Value<double>(BattlefieldKeys.EnemySpriteScale);
            DisplayScale = presentation.Value<double>(BattlefieldKeys.DisplayScale);
            RowScale = ((JObject)presentation[BattlefieldKeys.RowScale]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<double>(), StringComparer.Ordinal);
            Columns = presentation.Value<int>(BattlefieldKeys.Columns);
            Lanes = presentation.Value<int>(BattlefieldKeys.Lanes);
            PlayerSpawnRow = presentation.Value<string>(BattlefieldKeys.PlayerSpawnRow);
            var fit = (JObject)json[BattlefieldKeys.Fit];
            var requested = (JObject)fit[BattlefieldKeys.Requested];
            RequestedEnemyWide = requested[BattlefieldKeys.Enemy].Value<double>(BattlefieldKeys.Wide);
            RequestedEnemyNarrow = requested[BattlefieldKeys.Enemy].Value<double>(BattlefieldKeys.Narrow);
            RequestedPlayerWide = requested[BattlefieldKeys.Player].Value<double>(BattlefieldKeys.Wide);
            RequestedPlayerNarrow = requested[BattlefieldKeys.Player].Value<double>(BattlefieldKeys.Narrow);
            LowRatio = fit.Value<double>(BattlefieldKeys.LowRatio);
            RatioCap = fit.Value<double>(BattlefieldKeys.RatioCap);
            MultiplierCap = fit.Value<double>(BattlefieldKeys.MultiplierCap);
            WidthMargin = fit.Value<double>(BattlefieldKeys.WidthMargin);
            EdgeMargin = fit.Value<double>(BattlefieldKeys.EdgeMargin);
            Headroom = fit.Value<double>(BattlefieldKeys.Headroom);
            SpaciousMaxEnemies = fit.Value<int>(BattlefieldKeys.SpaciousMaxEnemies);
            var leading = (JObject)fit[BattlefieldKeys.Leading];
            LeadingCap = leading.Value<double>(BattlefieldKeys.Cap);
            LeadingHeightShare = leading.Value<double>(BattlefieldKeys.HeightShare);
            OverheadGap = leading.Value<double>(BattlefieldKeys.Gap);
            OverheadGapCompact = leading.Value<double>(BattlefieldKeys.GapCompact);
            ScreenMargin = fit.Value<double>(BattlefieldKeys.ScreenMargin);
            ShadowHeightShare = fit.Value<double>(BattlefieldKeys.ShadowHeightShare);
            DefaultBoxHeight = fit.Value<double>(BattlefieldKeys.DefaultBoxHeight);
            var stature = (JObject)json[BattlefieldKeys.Stature];
            LargeRatio = stature[BattlefieldKeys.Ratio].Value<double>(BattlefieldKeys.Large);
            HugeRatio = stature[BattlefieldKeys.Ratio].Value<double>(BattlefieldKeys.Huge);
            BossScale = ((JObject)stature[BattlefieldKeys.BossScale]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<double>(), StringComparer.Ordinal);
            StatureOf = ((JObject)stature[BattlefieldKeys.Of]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>(), StringComparer.Ordinal);
            Low = new HashSet<string>(((JArray)json[BattlefieldKeys.Low]).Select(t => t.Value<string>()), StringComparer.Ordinal);
        }

        private static BattlefieldProfile Profile(JObject row) => new BattlefieldProfile
        {
            Left = row.Value<double>(BattlefieldKeys.Left),
            Right = row.Value<double>(BattlefieldKeys.Right),
            Top = row.Value<double>(BattlefieldKeys.Top),
            RowHeight = row.Value<double>(BattlefieldKeys.RowHeight),
            Skew = row.Value<double>(BattlefieldKeys.Skew),
        };

        /// <summary>The stature of an enemy (its encounter pool: normal, elite is large, boss is huge); normal for an enemy the data does not name.</summary>
        public string Stature(string enemyId) => enemyId != null && StatureOf.TryGetValue(enemyId, out var s) ? s : BattlefieldNames.StatureNormal;

        /// <summary>combatSpriteRatio: how much taller than a normal figure a stature stands.</summary>
        public double SpriteRatio(string stature, string enemyId) =>
            stature == BattlefieldNames.StatureLarge ? LargeRatio : stature == BattlefieldNames.StatureHuge ? (enemyId != null && BossScale.TryGetValue(enemyId, out var scale) ? scale : HugeRatio) : 1;
    }
}
