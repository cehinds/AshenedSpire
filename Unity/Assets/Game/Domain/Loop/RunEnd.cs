using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using LK = Ashen.Generated.LoopKeys;
using LV = Ashen.Generated.LoopValues;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.Domain.Loop
{
    /// <summary>
    /// A run closed out (the shipped finishRun): the run record written to the profile's history, and the unlocks it
    /// newly earned (their rows, in the unlock table's order). The slot is the caller's to clear (saves.clearRun).
    /// </summary>
    public sealed class RunEndReceipt
    {
        public bool Victory;

        /// <summary>runResult(victory): the record appended to profile.results (the run summary, US-4.9/13.1).</summary>
        public JObject Result;

        /// <summary>The unlock rows newly earned (US-13.2), in the unlock table's order.</summary>
        public List<JObject> Earned = new List<JObject>();

        /// <summary>{ result, earned: [unlock id] }.</summary>
        public JObject ToJson() => Js.Obj(K.Result, Result.DeepClone(), LK.Earned, new JArray(Earned.Select(u => u[K.Id]?.DeepClone())));
    }

    /// <summary>
    /// The end of a run (shipped main.js runResult, finishRun and the profile writes of engine/save.js recordResult and
    /// saveMeta, over model/unlocks.js recordProgress/evaluateUnlocks, classCard.js runClassIdentity, classSwap.js
    /// peakClassLevel and customMods.js isCustomRun). A death and a victory close out through the same door, so they
    /// can never disagree about what counts. Profiles are their stored JSON, rewritten in place (D-092).
    /// </summary>
    public static class RunEnd
    {
        /// <summary>finishRun(victory): record the run, advance the durable progress tally, and hand back what was newly earned.</summary>
        public static RunEndReceipt Finish(LoopContext ctx, bool victory)
        {
            var d = ctx.Data;
            var result = Result(ctx, victory);
            var meta = ctx.Profile;
            // saves.recordResult: append, keep the newest historyLimit.
            if (!(meta[LK.Results] is JArray results) || !Js.Truthy(meta[LK.Results])) meta[LK.Results] = results = new JArray();
            results.Add(result.DeepClone());
            var limit = (int)d.RuleNum(LK.Profile, LK.HistoryLimit);
            while (results.Count > limit) results.RemoveAt(0);
            meta[LK.Progress] = RecordProgress(meta[LK.Progress] as JObject, result);
            var fresh = EvaluateUnlocks(d, meta);
            if (fresh.Count > 0)
            {
                var unlocked = new JArray(Js.Items(meta[RK.Unlocked]).Select(t => t.DeepClone()));
                foreach (var id in fresh) unlocked.Add(id);
                meta[RK.Unlocked] = unlocked;
            }
            SaveMeta(d, meta);
            return new RunEndReceipt
            {
                Victory = victory,
                Result = result,
                Earned = fresh.Select(id => d.Unlocks.OfType<JObject>().FirstOrDefault(u => u.Str(K.Id) == id)).Where(u => u != null).ToList(),
            };
        }

        /// <summary>
        /// saveMeta(meta): the stored profile is <c>{ ...meta, schemaVersion }</c> at the current profile schema. Only the last
        /// write of a door is kept, so the loop applies it once, after the door's changes.
        /// </summary>
        internal static void SaveMeta(LoopData d, JObject meta) => meta[RK.SchemaVersion] = Js.N(d.RuleNum(LK.Profile, RK.SchemaVersion));

        /// <summary>runResult(victory): the run record — seed, class, act, floor, the fight tallies, the bosses felled and the class peak.</summary>
        public static JObject Result(LoopContext ctx, bool victory)
        {
            var run = ctx.Run;
            var custom = run.Obj(RK.Custom);
            var stats = run.Obj(WK.Stats) ?? new JObject();
            var customization = run[LK.Customization];
            return Js.Obj(
                V.Victory, victory,
                RK.Seed, run[RK.SeedString]?.DeepClone(),
                RK.Class, run[RK.Class]?.DeepClone(),
                LK.ClassName, ClassName(ctx.Data, run),
                LK.Act, run[RK.ActNumber]?.DeepClone(),
                MK.Floor, run[RK.Floor]?.DeepClone(),
                WK.FightsWon, stats[WK.FightsWon]?.DeepClone(),
                LK.DamageDealt, stats[LK.DamageDealt]?.DeepClone(),
                LK.DamageTaken, stats[LK.DamageTaken]?.DeepClone(),
                K.Name, Js.Truthy(customization) ? Js.Get(customization, K.Name)?.DeepClone() : customization?.DeepClone(),
                RK.Custom, IsCustomRun(custom),
                WK.Ascension, Js.Or0(custom?[WK.Ascension]),
                LK.Bosses, new JArray(Js.Items(run[WK.BossesBeaten]).Select(t => t.DeepClone())),
                LK.MaxClassLevel, PeakClassLevel(run),
                WK.BossGroups, run[WK.BossGroups] is JObject groups ? groups.DeepClone() : new JObject());
        }

        /// <summary>runClassIdentity(registries, run).name: the subclass once its top-tier node is picked, the class until then.</summary>
        public static string ClassName(LoopData d, JObject run)
        {
            var classId = run.Str(RK.Class);
            if (!d.Combat.Classes.Has(classId)) return Js.Truthy(run[RK.Class]) ? RunJs.Key(run[RK.Class]) : string.Empty;
            var def = d.Combat.Classes.Get(classId);
            var picked = Js.Items(run[K.CoreTags]).Where(t => Js.IsStr(t) && Js.Str(t).Length > 0).Select(Js.Str).ToList();
            var tree = ClassTree.Rows(d.Rewards, classId);
            var ladder = d.Balance.Obj(K.Skill)?.Obj(RK.Class)?[WK.TierAt] as JArray;
            var topTier = ladder != null && ladder.Count > 0 ? ladder.Count : Math.Max(0, tree.Select(r => r.Tier).DefaultIfEmpty(0).Max());
            var subclassId = picked.FirstOrDefault(id => tree.Any(r => r.NodeId == id && r.Tier == topTier && topTier > 0));
            var subclass = subclassId != null ? d.Rewards.Nodes.OfType<JObject>().FirstOrDefault(n => n.Str(K.Id) == subclassId) : null;
            return subclass != null ? subclass.Str(K.Label) : def.Str(K.Name);
        }

        /// <summary>isCustomRun(custom): any non-default rule (ascension, deck mode, map shape, pinned seat, a mod on).</summary>
        public static bool IsCustomRun(JObject custom)
        {
            if (custom == null) return false;
            if (custom.Num(WK.Ascension) > 0) return true;
            if (custom.Is(LK.DeckMode) && custom.Str(LK.DeckMode) != LV.StandardDeck) return true;
            if (custom[Ashen.Generated.MapKeys.MapShape] is JObject shape && shape.Count > 0) return true;
            if (custom.Is(LK.FirstSeat)) return true;
            return custom.Obj(K.Mods)?.Properties().Any(p => Js.Truthy(p.Value)) == true;
        }

        /// <summary>peakClassLevel(run): the highest class level reached, the live class tracks and every swap's fromLevel alike.</summary>
        public static double PeakClassLevel(JObject run)
        {
            var prefix = V.ClassSkillPrefix + V.KeySeparator;
            var live = (run.Obj(K.Skills)?.Properties() ?? Enumerable.Empty<JProperty>())
                .Where(p => p.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(p => Js.Or0(Js.Get(p.Value, K.Level)));
            var swapped = Js.Items(run[Ashen.Generated.MapKeys.History]).OfType<JObject>()
                .Where(h => h.Str(K.Kind) == LV.ClassSwapped)
                .Select(h => { var n = RunJs.Number(h[LK.FromLevel]); return double.IsNaN(n) ? 0 : n; });
            return live.Concat(swapped).Aggregate(0.0, Math.Max);
        }

        // ------------------------------------------------------------------ progress and unlocks (model/unlocks.js)

        /// <summary>emptyProgress().</summary>
        public static JObject EmptyProgress() => Js.Obj(LK.Runs, 0.0, LK.Wins, 0.0, LK.MaxAct, 1.0, LK.Bosses, new JArray(), LK.WonClasses, new JArray(),
            LK.MaxClassLevel, 0.0, WK.BossGroups, new JObject());

        private static void AddOnce(JArray list, string value)
        {
            if (!string.IsNullOrEmpty(value) && !Js.Includes(list, value)) list.Add(value);
        }

        /// <summary>recordProgress(progress, result): the durable tally, advanced in place by one run's record.</summary>
        public static JObject RecordProgress(JObject progress, JObject result)
        {
            var p = progress ?? EmptyProgress();
            foreach (var e in EmptyProgress().Properties()) if (p[e.Name] == null) p[e.Name] = e.Value.DeepClone();
            p.Put(LK.Runs, p.Num(LK.Runs) + 1);
            var act = RunJs.Number(result[LK.Act]);
            p.Put(LK.MaxAct, Math.Max(p.Num(LK.MaxAct), double.IsNaN(act) || act == 0 ? 1 : act));
            foreach (var id in Js.Items(result[LK.Bosses])) AddOnce(p.Arr(LK.Bosses), RunJs.Key(id));
            var level = RunJs.Number(result[LK.MaxClassLevel]);
            p.Put(LK.MaxClassLevel, Math.Max(p.Or0(LK.MaxClassLevel), double.IsNaN(level) ? 0 : level));
            foreach (var entry in (result[WK.BossGroups] as JObject ?? new JObject()).Properties())
            {
                var groups = p.Obj(WK.BossGroups);
                if (!Js.Truthy(groups[entry.Name])) groups[entry.Name] = new JArray();
                foreach (var g in Js.Items(entry.Value)) AddOnce(groups.Arr(entry.Name), RunJs.Key(g));
            }
            if (Js.Truthy(result[V.Victory]))
            {
                p.Put(LK.Wins, p.Num(LK.Wins) + 1);
                AddOnce(p.Arr(LK.WonClasses), RunJs.Key(result[RK.Class]));
            }
            return p;
        }

        /// <summary>evaluateUnlocks(unlocks, meta): the unlock ids newly met by the tally, in table order.</summary>
        public static List<string> EvaluateUnlocks(LoopData d, JObject meta)
        {
            var progress = meta[LK.Progress] as JObject ?? EmptyProgress();
            var earned = new HashSet<string>(Js.Items(meta[RK.Unlocked]).Select(Js.Str).Where(s => s != null), StringComparer.Ordinal);
            var fresh = new List<string>();
            foreach (var u in d.Unlocks.OfType<JObject>())
            {
                var id = u.Str(K.Id);
                if (id != null && earned.Contains(id)) continue;
                if (Met(u, progress)) fresh.Add(id);
            }
            return fresh;
        }

        /// <summary>UNLOCK_CONDITIONS[u.condition](u, progress); an unknown condition is never met.</summary>
        private static bool Met(JObject u, JObject p)
        {
            var param = u[LK.Param];
            var text = RunJs.Key(param);
            var number = RunJs.Number(param);
            switch (u.Str(LK.Condition))
            {
                case LV.WinAsClass: return Js.Includes(p[LK.WonClasses], text);
                case LV.BeatBoss: return Js.Includes(p[LK.Bosses], text);
                case LV.ReachAct: return Js.Or0(p[LK.MaxAct]) >= number;
                case LV.ClassLevel: return Js.Or0(p[LK.MaxClassLevel]) >= number;
                case LV.WinRuns: return Js.Or0(p[LK.Wins]) >= number;
                case LV.BossWithGroup:
                {
                    var at = text.IndexOf(V.KeySeparator, StringComparison.Ordinal);
                    var enemyId = at < 0 ? text : text.Substring(0, at);
                    var group = at < 0 ? string.Empty : text.Substring(at + V.KeySeparator.Length);
                    return Js.Includes(p[LK.Bosses], enemyId) && Js.Includes(Js.Get(p[WK.BossGroups], enemyId), group);
                }
                default: return false;
            }
        }
    }
}
