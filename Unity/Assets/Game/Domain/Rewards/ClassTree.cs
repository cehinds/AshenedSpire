using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.Domain.Rewards
{
    /// <summary>
    /// The class tree (shipped model/classTree.js, plan phase 5b): tree rows per class, the tier each class level
    /// opens (balance.skill.class.tierAt), the draftable pool (tier open, not picked, every REQUIRES picked, no
    /// CONFLICTS_WITH picked either way) and the class track's pay for a fight.
    /// </summary>
    public static class ClassTree
    {
        public sealed class TreeRow
        {
            public string ClassId;
            public string NodeId;
            public double Tier;
        }

        /// <summary>classTreeRows(registries, classId): the tree rows of one class, in table order.</summary>
        public static List<TreeRow> Rows(RewardsData d, string classId) =>
            Js.Items(d.Combat.ClassTree).OfType<JObject>().Where(row => row.Str(K.ClassId) == classId)
                .Select(row => new TreeRow { ClassId = row.Str(K.ClassId), NodeId = row.Str(K.NodeId), Tier = RunJs.Number(row[RK.Tier]) }).ToList();

        /// <summary>tierOpensAt(registries, tier): the class level tier (1-based) opens at; +∞ when unauthored.</summary>
        public static double TierOpensAt(RewardsData d, double tier)
        {
            var at = d.Balance.Obj(K.Skill)?.Obj(V.ClassKind)?[WK.TierAt] as JArray;
            var index = tier - 1;
            if (at == null || double.IsNaN(index) || Math.Floor(index) != index || index < 0 || index >= at.Count) return double.PositiveInfinity;
            var value = at[(int)index];
            return Js.IsInt(value) ? Js.D(value) : double.PositiveInfinity;
        }

        private static JObject Rule(RewardsData d, string nodeId) => d.Combat.PropertyRules.Has(nodeId) ? d.Combat.PropertyRules.Get(nodeId) : null;

        /// <summary>classDraftPool(registries, classId, coreTags, level): the node ids a class at the level may draft, in table order.</summary>
        public static List<string> DraftPool(RewardsData d, string classId, JToken coreTags, double level)
        {
            var picked = new HashSet<string>(Js.Items(coreTags).Select(RunJs.Key), StringComparer.Ordinal);
            var excludedByPicked = new HashSet<string>(picked.SelectMany(id => Js.Items(Rule(d, id)?[K.Excludes]).Select(RunJs.Key)), StringComparer.Ordinal);
            return Rows(d, classId)
                .Where(row => level >= TierOpensAt(d, row.Tier) && !picked.Contains(row.NodeId))
                .Where(row =>
                {
                    var r = Rule(d, row.NodeId);
                    if (r == null) return false;
                    if (Js.Items(r[K.Requires]).Any(t => !picked.Contains(RunJs.Key(t)))) return false;
                    if (Js.Items(r[K.Excludes]).Any(t => picked.Contains(RunJs.Key(t)))) return false;
                    return !excludedByPicked.Contains(row.NodeId);
                })
                .Select(row => row.NodeId).ToList();
        }

        /// <summary>
        /// awardClassXp(registries, run, { victory, pool }) → the award, or null: a lost fight pays nothing; a won one
        /// pays balance.skill.class.xp.perWin, plus bossKill at a boss door.
        /// </summary>
        public static JObject AwardClassXp(RewardsData d, JObject run, bool victory, string pool)
        {
            if (!victory || run == null || !run.Is(RK.Class)) return null;
            var xp = d.Balance.Obj(K.Skill)?.Obj(V.ClassKind)?.Obj(K.Xp) ?? new JObject();
            var amount = xp.Or0(WK.PerWin) + (pool == WV.Boss ? xp.Or0(WK.BossKill) : 0);
            if (!(amount > 0)) return null;
            return Skills.Award(d, run, Skills.ClassSkillId(RunJs.Key(run[RK.Class])), amount);
        }
    }
}
