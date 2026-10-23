using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using NK = Ashen.Generated.Core2RangeKeys;
using NM = Ashen.Generated.Core2RangeMessages;
using NN = Ashen.Generated.Core2RangeNumbers;
using NV = Ashen.Generated.Core2RangeValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The player's move across their own side of the field (web model/formationMovement.js formationMovePlan, model/formationLayout.js, the
    /// <c>moveCharacter</c> intent): off unless the presentation settings turn it on, it costs one action unless they say it is free, and it never
    /// touches the engagement.
    /// </summary>
    public static class CoreFormation
    {
        private sealed class Dimensions
        {
            public int Columns;
            public int Rows;
            public bool Perspective;
        }

        private static bool Flag(JObject settings, string key, bool fallback) => settings != null && settings[key] != null ? settings[key].Type == JTokenType.Boolean && settings[key].Value<bool>() : fallback;

        private static double Whole(JObject settings, string key, double fallback, double low, double high)
        {
            if (settings == null || settings[key] == null || !Js.IsNum(settings[key])) return fallback;
            return Math.Min(high, Math.Max(low, Math.Round(Js.D(settings[key]), MidpointRounding.AwayFromZero)));
        }

        private static Dimensions DimensionsOf(CombatState c, JObject settings)
        {
            var perspective = !Flag(settings, NV.SettingClassic, false);
            var actors = Math.Max(1, c.Enemies.Count);
            if (perspective)
            {
                var f = c.CoreData.Section(CK.Formation);
                var extra = c.Fields.Obj(CK.FormationPolicy)?.Obj(CK.ExtraRow);
                var enabled = extra != null && extra.Is(CK.Enabled) && Js.Includes(extra[CK.Sides], CV.SideEnemy);
                var rows = (int)Math.Min(f.Num(CK.MaxRows), Math.Max(f.Num(CK.Lanes), Math.Ceiling(actors / (enabled ? f.Num(CK.EnemiesPerLaneExtra) : f.Num(CK.EnemiesPerLane)))));
                return new Dimensions { Columns = 1, Rows = rows, Perspective = true };
            }
            var columns = (int)Whole(settings, NV.SettingColumns, NN.FormationColumnsDefault, NN.FormationColumnsMin, NN.FormationColumnsMax);
            var rowCount = (int)Whole(settings, NV.SettingRows, NN.FormationRowsDefault, NN.FormationRowsMin, NN.FormationRowsMax);
            rowCount = (int)Math.Min(NN.FormationRowsMax, Math.Max(rowCount, Math.Ceiling((double)actors / columns)));
            columns = (int)Math.Min(NN.FormationColumnsMax, Math.Max(columns, Math.Ceiling((double)actors / rowCount)));
            return new Dimensions { Columns = columns, Rows = rowCount };
        }

        private static bool Parse(string cell, out int row, out int column)
        {
            row = -1;
            column = 0;
            if (cell == null || cell.Length != NN.CellLength) return false;
            row = NV.FormationRows.IndexOf(cell.Substring(0, 1), StringComparison.Ordinal);
            return row >= 0 && int.TryParse(cell.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out column) && column >= 1 && column <= NN.FormationRowsMax;
        }

        private static bool IsCell(string cell, int rows, int columns) => Parse(cell, out var row, out var column) && row < rows && column >= 1 && column <= columns;

        private static string Current(string cell, Dimensions d)
        {
            if (IsCell(cell, d.Rows, d.Columns)) return cell;
            if (!Parse(cell, out var row, out var column)) return null;
            if (d.Perspective && row < d.Rows && column <= NN.FormationColumnsMax) return cell.Substring(0, 1) + NV.FirstColumn;
            if (!d.Perspective && column <= d.Columns) return NV.FormationRows.Substring(Math.Min(row, d.Rows - 1), 1) + cell.Substring(1);
            return null;
        }

        private static string Spawn(Dimensions d)
        {
            var row = Math.Min(d.Rows - 1, Math.Max(0, NV.FormationRows.IndexOf(NV.SpawnRow, StringComparison.Ordinal)));
            return NV.FormationRows.Substring(row, 1) + NV.FirstColumn;
        }

        /// <summary>formationMovePlan(combat, cell, settings): whether the player may move to the cell, why not, and what it costs.</summary>
        public static JObject Plan(CombatState c, string cell, JObject settings)
        {
            var d = DimensionsOf(c, settings);
            var current = Current(c.Player.Str(CK.FormationCell), d) ?? Spawn(d);
            var cost = Flag(settings, NV.SettingCostsAction, true) ? 1 : 0;
            string reason = string.Empty;
            if (!Flag(settings, NV.SettingEnabled, false)) reason = NM.MovementDisabled;
            else if (c.Result != null || c.Phase != V.PhasePlayer || !c.Player.Is(K.Alive)) reason = NM.MovementOnTurn;
            else if (!IsCell(cell, d.Rows, d.Columns)) reason = NM.MovementChooseTile;
            else if (cell == current) reason = NM.MovementAlready;
            else if (c.Player.Or0(K.Stamina) < cost) reason = NM.MovementCost;
            return new JObject { [NK.Ok] = reason.Length == 0, [K.Reason] = reason, [NK.Current] = current, [NK.Cell] = cell, [K.Cost] = cost };
        }

        /// <summary>The <c>moveCharacter</c> intent: the player steps to the cell, pays the action the setting asks for and says so.</summary>
        public static void Move(CombatState c, string cell, JObject settings)
        {
            var move = Plan(c, cell, settings);
            if (!move.Is(NK.Ok)) throw new InvalidOperationException(move.Str(K.Reason));
            c.Player.Put(K.Stamina, c.Player.Num(K.Stamina) - move.Num(K.Cost));
            c.Player[CK.FormationCell] = move[NK.Cell].DeepClone();
            if (move.Num(K.Cost) > 0) c.Emit(E.EnergySpent, Js.Obj(K.Amount, move[K.Cost]));
            c.Emit(NV.EventCharacterMoved, Js.Obj(K.SourceId, c.Player[K.Id], NK.From, move[NK.Current], NK.To, move[NK.Cell]));
            CoreActions.Drain(c);
        }
    }
}
