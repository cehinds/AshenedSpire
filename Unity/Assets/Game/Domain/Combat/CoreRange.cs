using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Where the fighters stand and how far apart (web model/formationOccupancy.js initializeFormationOccupancy, model/formationLayout.js
    /// perspective grid, engine/combatEngagement.js initializeCombatRange, model/combatEngagement.js createRangeState): one rank of the
    /// player's lanes against two ranks of enemies, and the engagement band of every player/enemy pair, set by the weapon each side
    /// starts with.
    /// </summary>
    public static class CoreRange
    {
        private static string Cell(JObject formation, int lane, double column) =>
            formation.Str(CK.Rows)[lane].ToString() + column.ToString(CultureInfo.InvariantCulture);

        /// <summary>initializeFormationOccupancy(combat): the profile's formation policy, then a cell for the player and every enemy.</summary>
        public static void InitializeFormation(CombatState c)
        {
            var policy = c.CoreData.Module(CK.FormationModule);
            if (policy == null) return;
            c.Fields[CK.FormationPolicy] = policy.DeepClone();
            var f = c.CoreData.Section(CK.Formation);
            var extra = policy.Obj(CK.ExtraRow);
            var enabled = extra != null && extra.Is(CK.Enabled) && Js.Includes(extra[CK.Sides], CV.SideEnemy);
            var lanes = f.Num(CK.Lanes);
            var rows = (int)Math.Min(f.Num(CK.MaxRows), Math.Max(lanes, Math.Max(Math.Ceiling(c.Enemies.Count / (enabled ? f.Num(CK.EnemiesPerLaneExtra) : f.Num(CK.EnemiesPerLane))), 1)));
            var order = new List<string>();
            var back = new List<int> { rows - 1, 0 };
            for (var lane = 1; lane < rows - 1; lane++) back.Add(lane);
            order.AddRange(back.Select(lane => Cell(f, lane, f.Num(CK.EnemyBackColumn))));
            for (var lane = rows - 1; lane >= 0; lane--) order.Add(Cell(f, lane, f.Num(CK.EnemyFrontColumn)));
            if (enabled) for (var lane = 0; lane < rows; lane++) order.Add(Cell(f, lane, f.Num(CK.ExtraColumn)));
            var taken = new HashSet<string>(StringComparer.Ordinal);
            c.Player[CK.FormationCell] = Cell(f, rows - 1, f.Num(CK.PlayerColumn));
            foreach (var enemy in c.Enemies)
            {
                var cell = order.FirstOrDefault(candidate => !taken.Contains(candidate)) ?? throw new InvalidOperationException(CM.FormationFull);
                enemy[CK.FormationCell] = cell;
                taken.Add(cell);
            }
        }

        private static string PairKey(string a, string b)
        {
            var pair = new[] { a, b };
            Array.Sort(pair, StringComparer.Ordinal);
            return new JArray(pair).ToString(Formatting.None);
        }

        private static double Band(CombatState c, string range)
        {
            var ranges = Js.Items(c.CoreData.Section(CK.Vocabulary)[CK.Ranges]).Select(Js.Str).ToList();
            var index = ranges.IndexOf(range);
            return index;
        }

        private static string AttackSource(JObject piece) =>
            Js.Items(piece[K.Tags]).Select(Js.Str).FirstOrDefault(t => t != null && t.StartsWith(CV.TagSource, StringComparison.Ordinal))?.Substring(CV.TagSource.Length)
            ?? piece.Obj(CK.Attack)?.Str(CK.Source);

        /// <summary>initializeCombatRange(ctx, policy): every player/enemy pair starts at the wider of the two sides' starting bands.</summary>
        public static void InitializeRange(CombatState c, JObject policy)
        {
            if (!CoreFixCore.Enabled(c)) return;
            var start = new List<(string key, double band)>();
            var playerKey = CV.ActorPlayerKey;
            c.Player[CK.RangeActorKey] = playerKey;
            string primaryRange = null;
            var loadout = c.Loadout;
            foreach (var hand in Js.Items(c.CoreData.Section(CK.Range)[CK.Hands]).Select(Js.Str))
            {
                foreach (var slot in Js.Items(c.Data.Equipment[K.Slots]).OfType<JObject>().Where(s => s.Str(K.Hand) == hand))
                {
                    var piece = loadout != null ? Equipment.EquippedIn(c.Data, loadout, c.Player.Str(K.ClassId), slot.Str(K.Id)) : null;
                    if (piece != null && AttackSource(piece) == CV.SourceWeapon) { primaryRange = Js.Truthy(piece[CK.Range]) ? piece.Str(CK.Range) : CV.RangeMelee; break; }
                }
                if (primaryRange != null) break;
            }
            start.Add((playerKey, Band(c, primaryRange ?? CV.RangeMelee)));
            foreach (var enemy in c.Enemies)
            {
                var key = CV.ActorEnemyPrefix + enemy.Str(K.Id);
                enemy[CK.RangeActorKey] = key;
                var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                start.Add((key, Band(c, Js.Truthy(def[CK.StartingRange]) ? def.Str(CK.StartingRange) : CV.RangeMelee)));
            }
            var engagements = new JObject();
            foreach (var a in start)
                foreach (var b in start)
                    if (a.key.StartsWith(CV.ActorPlayerPrefix, StringComparison.Ordinal) && b.key.StartsWith(CV.ActorEnemyPrefix, StringComparison.Ordinal))
                        engagements.Put(PairKey(a.key, b.key), Math.Max(a.band, b.band));
            var rules = policy != null ? (JObject)policy.DeepClone() : Js.Spread(CoreFixCore.Raw(c).Obj(CK.Range));
            c.Fields[CK.RangeState] = new JObject { [CK.Version] = 1, [CK.Policy] = rules, [CK.Engagements] = engagements };
        }
    }
}
