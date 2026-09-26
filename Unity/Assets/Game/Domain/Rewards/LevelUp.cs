using System;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using WK = Ashen.Generated.RewardsKeys;
using WM = Ashen.Generated.RewardsMessages;
using WV = Ashen.Generated.RewardsValues;
using CombatMath = Ashen.Generated.CombatMath;

namespace Ashen.Domain.Rewards
{
    /// <summary>
    /// The character level (shipped model/levelup.js, plan phase 6): one ledger <c>run.level = { xp, level,
    /// unspentPoints }</c> paid at the end of a fight, the one curve every track shares, the points each step grants
    /// and the pool re-derivation a climb triggers. The heal-ledger note is not ported (D-044).
    /// </summary>
    public static class LevelUp
    {
        private static JObject Balance(RewardsData d, string key) => d.Balance.Obj(key) ?? new JObject();

        /// <summary>emptyLevel(): level 1, nothing earned, nothing waiting.</summary>
        public static JObject EmptyLevel() => Js.Obj(K.Xp, 0.0, K.Level, 1.0, RK.UnspentPoints, 0.0);

        /// <summary>
        /// xpToNext(registries, level): round(base × growth^(level − 1) / roundTo + ε) × roundTo, at least one unit;
        /// an unauthored base or growth reads the rules' defaults.
        /// </summary>
        public static double XpToNext(RewardsData d, JToken level)
        {
            var curve = Balance(d, K.Level).Obj(K.Xp) ?? new JObject();
            var b = Js.IsFinite(curve[K.Base]) && curve.Num(K.Base) > 0 ? curve.Num(K.Base) : d.RuleNum(K.Level, WK.DefaultBase);
            var g = Js.IsFinite(curve[K.Growth]) && curve.Num(K.Growth) > 0 ? curve.Num(K.Growth) : d.RuleNum(K.Level, WK.DefaultGrowth);
            var step = Js.IsInt(level) && Js.D(level) > 1 ? Js.D(level) - 1 : 0;
            var unit = Js.IsInt(curve[WK.RoundTo]) && curve.Num(WK.RoundTo) > 0 ? curve.Num(WK.RoundTo) : 1;
            return Math.Max(unit, RunJs.Round(b * Math.Pow(g, step) / unit + CombatMath.Epsilon) * unit);
        }

        /// <summary>
        /// combatLevelXp(registries, { victory, pool, kills }): xp.combatWin for a won fight plus xp.kill.&lt;pool&gt; per
        /// enemy felled (an unknown pool pays the normal rate).
        /// </summary>
        public static double CombatLevelXp(RewardsData d, bool victory, string pool, double kills)
        {
            var t = Balance(d, K.Xp);
            var kill = t.Obj(WK.Kill) ?? new JObject();
            var perKill = pool != null && Js.IsFinite(kill[pool]) ? kill.Num(pool) : Js.IsFinite(kill[WV.Normal]) ? kill.Num(WV.Normal) : 0;
            var won = victory && Js.IsFinite(t[WK.CombatWin]) ? t.Num(WK.CombatWin) : 0;
            var n = Math.Floor(kills) == kills && kills > 0 ? kills : 0;
            return won + n * perKill;
        }

        /// <summary>
        /// awardLevelXp(registries, run, amount, { pointsPerLevel }) → { before, after, levelUps, points, thresholds,
        /// gained }: writes the ledger and climbs as many steps as the XP buys (capped by balance.levelUp.maxLevels),
        /// each step granting pointsPerLevel (the caller's dial, else the authored value) to unspentPoints; a climb
        /// re-derives the pools from the run's own snapshot. A non-positive or non-finite amount writes nothing.
        /// </summary>
        public static JObject AwardLevelXp(RewardsData d, JObject run, double amount, JToken pointsPerLevel)
        {
            if (run == null) throw new InvalidOperationException(WM.AwardLevelNoRun);
            if (!(run[K.Level] is JContainer)) run[K.Level] = EmptyLevel();
            var row = run.Obj(K.Level);
            var before = row[K.Level]?.DeepClone();
            var gain = double.IsNaN(amount) || double.IsInfinity(amount) ? 0 : Math.Floor(amount);
            if (gain <= 0)
                return Js.Obj(WK.Before, before, WK.After, before?.DeepClone(), RK.LevelUps, 0.0, WK.Points, 0.0, WK.Thresholds, 0.0, WK.Gained, 0.0);
            var t = Balance(d, WK.LevelUp);
            var authored = Js.IsInt(t[WK.PointsPerLevel]) && t.Num(WK.PointsPerLevel) > 0 ? t.Num(WK.PointsPerLevel) : 1;
            var perLevel = Js.IsInt(pointsPerLevel) && Js.D(pointsPerLevel) > 0 ? Js.D(pointsPerLevel) : authored;
            double? cap = Js.IsInt(t[WK.MaxLevels]) ? t.Num(WK.MaxLevels) : (double?)null;
            row.Put(K.Xp, row.Num(K.Xp) + gain);
            double points = 0;
            var cost = XpToNext(d, row[K.Level]);
            while (row.Num(K.Xp) >= cost && (cap == null || row.Num(K.Level) < cap.Value))
            {
                row.Put(K.Xp, row.Num(K.Xp) - cost);
                row.Put(K.Level, row.Num(K.Level) + 1);
                row.Put(RK.UnspentPoints, row.Num(RK.UnspentPoints) + perLevel);
                points += perLevel;
                cost = XpToNext(d, row[K.Level]);
            }
            var levelUps = row.Num(K.Level) - Js.D(before);
            double thresholds = 0;
            if (levelUps > 0) thresholds = RederivePools(d, run);
            return Js.Obj(WK.Before, before, WK.After, row[K.Level]?.DeepClone(), RK.LevelUps, levelUps, WK.Points, points, WK.Thresholds, thresholds, WK.Gained, gain);
        }

        /// <summary>
        /// rederivePools(registries, run) → how many maxima moved: Actions and Hand from the run's own snapshot at its
        /// attributes and level, then every pool through reconcileRunLoadoutHp (deficits carried).
        /// </summary>
        public static double RederivePools(RewardsData d, JObject run)
        {
            if (!run.Is(K.DerivedStatRuleSnapshot) || !run.Obj(K.DerivedStatRuleSnapshot).Is(RK.Rules)) return 0;
            var rules = run.Obj(K.DerivedStatRuleSnapshot).Obj(RK.Rules);
            var classDef = d.Run.Classes.Get(run.Str(RK.Class));
            var level = RunState.CharacterLevelOf(run);
            var before = new JObject();
            foreach (var field in Loadout.PoolFields(d.Run)) before[field] = run[field]?.DeepClone();
            double moved = 0;
            foreach (var (key, statId) in new[] { (K.EnergyMax, RV.StatEnergy), (K.DrawPerTurn, RV.StatDraw) })
            {
                if (run[key] == null) continue;
                var next = DerivedStats.Derive(rules, statId, run.Obj(K.Attributes), classDef, level).Value;
                if (!(Js.IsNum(run[key]) && Js.D(run[key]) == next)) moved += 1;
                run.Put(key, next);
            }
            Loadout.ReconcileRunLoadoutHp(d.Run, run, false);
            foreach (var p in before.Properties())
                if (!SameValue(run[p.Name], p.Value)) moved += 1;
            return moved;
        }

        /// <summary>JS <c>===</c> on two optional scalars.</summary>
        private static bool SameValue(JToken a, JToken b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (Js.IsNum(a) && Js.IsNum(b)) return Js.D(a) == Js.D(b);
            return JToken.DeepEquals(a, b);
        }
    }
}
