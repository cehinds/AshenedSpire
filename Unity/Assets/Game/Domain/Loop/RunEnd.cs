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
        /// <summary>runResult(victory) over a run and the name of its class identity (read from the run's own registries).</summary>
        public static JObject Result(JObject run, bool victory, string className)
        {
            var custom = run.Obj(RK.Custom);
            var stats = run.Obj(WK.Stats) ?? new JObject();
            var customization = run[LK.Customization];
            return Js.Obj(
                V.Victory, victory,
                RK.Seed, run[RK.SeedString]?.DeepClone(),
                RK.Class, run[RK.Class]?.DeepClone(),
                LK.ClassName, className,
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


        /// <summary>runClassIdentity(registries, run).name over the post-combat data of any registries.</summary>
        public static string ClassName(RewardsData rewards, JObject run)
        {
            var classId = run.Str(RK.Class);
            if (!rewards.Combat.Classes.Has(classId)) return Js.Truthy(run[RK.Class]) ? RunJs.Key(run[RK.Class]) : string.Empty;
            var def = rewards.Combat.Classes.Get(classId);
            var picked = Js.Items(run[K.CoreTags]).Where(t => Js.IsStr(t) && Js.Str(t).Length > 0).Select(Js.Str).ToList();
            var tree = ClassTree.Rows(rewards, classId);
            var ladder = rewards.Balance.Obj(K.Skill)?.Obj(RK.Class)?[WK.TierAt] as JArray;
            var topTier = ladder != null && ladder.Count > 0 ? ladder.Count : Math.Max(0, tree.Select(r => r.Tier).DefaultIfEmpty(0).Max());
            var subclassId = picked.FirstOrDefault(id => tree.Any(r => r.NodeId == id && r.Tier == topTier && topTier > 0));
            var subclass = subclassId != null ? rewards.Nodes.OfType<JObject>().FirstOrDefault(n => n.Str(K.Id) == subclassId) : null;
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

        // ------------------------------------------------------------------ progress (model/unlocks.js)

        /// <summary>emptyProgress().</summary>
        public static JObject EmptyProgress() => Js.Obj(LK.Runs, 0.0, LK.Wins, 0.0, LK.MaxAct, 1.0, LK.Bosses, new JArray(), LK.WonClasses, new JArray(),
            LK.MaxClassLevel, 0.0, WK.BossGroups, new JObject());
    }
}
