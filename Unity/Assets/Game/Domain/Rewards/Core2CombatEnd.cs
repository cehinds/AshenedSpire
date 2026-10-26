using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.Core2RewardsValues;
using RW = Ashen.Generated.Core2RewardsKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.Domain.Rewards
{
    /// <summary>What a fight's end did to a Core 2 run and where the climb goes next (<see cref="Core2CombatEnd.Settle"/>).</summary>
    public sealed class Core2CombatEndReceipt
    {
        /// <summary>The fight's result ('victory' or 'defeat') and the pool of its encounter.</summary>
        public string Result;

        public string Pool;

        /// <summary>skillXpReceipt after the weapon rate: whole XP per track the fight paid, in receipt order.</summary>
        public JObject TrackReceipt = new JObject();

        public JArray TrackAwards = new JArray();
        public JObject ClassAward;

        /// <summary>Only the actual level crossings (skillLevelNotices): what the progression modal shows.</summary>
        public JArray Notices = new JArray();

        /// <summary>The cinders a won fight paid.</summary>
        public double Cinders;

        /// <summary>The next step: <see cref="Core2RewardDoor.NextDoor"/> (a reward door is pending on the run), <c>map</c>, <c>advanceAct</c>, <c>victory</c> or <c>defeat</c> (the run is over).</summary>
        public string Next;

        /// <summary>The id of the offer the door shows, when a door opens.</summary>
        public string OfferId;
    }

    /// <summary>
    /// The combat end of a configured Core 2 run (web main.js onCombatEnd 3241-3340 and beginGameProfileReward 3900-3910, WP-15): the fight is written back to
    /// the run (runCombatEnd), the skill tracks and the class track are paid once (a death pays too), then a death ends the run, and a win pays its cinders,
    /// records a boss, ends the run at the last act's boss, or opens the reward door with a persisted offer. Character XP is zero under Core (it has
    /// skill-only progression), so no level is earned and no level offer follows. What stays with the owner of the run: the screens, the audio, the victory beat,
    /// the profile's bank of enemy knowledge (a Core 2 run carries none) and the after-combat recovery settings (their defaults restore nothing).
    /// </summary>
    public static class Core2CombatEnd
    {
        /// <summary>
        /// onCombatEnd(result, combat, enc) for a configured run. <paramref name="rng"/> is the run's generator, the one the fight drew from. The run is
        /// changed in place; the receipt says what was paid and where the climb goes.
        /// </summary>
        public static Core2CombatEndReceipt Settle(Core2RewardsData d, JObject run, Rng rng, CombatState combat, JObject encounter)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            var victory = combat.Result == V.Victory;
            var pool = encounter.Str(WK.Pool);
            var receipt = new Core2CombatEndReceipt { Result = combat.Result, Pool = pool };
            run.Remove(RW.CombatPendingOutcome);
            CoreRunCombat.End(d.Combat, run, combat);
            // Skill tracks are paid here, once: the fight kept a receipt of every hit, Block and card by track; the run's ledger takes it now, win or loss.
            receipt.TrackReceipt = SkillXp.Receipt(combat);
            if (Core2Skills.Configured(d, run))
            {
                receipt.TrackAwards = Core2Skills.SettleTracks(d, run, receipt.TrackReceipt);
                receipt.ClassAward = Core2Skills.ClassAward(d, run, victory, pool, Core2Skills.XpMultiplier(d, run, true));
                var used = combat.SkillXp.Obj(V.Player)?.Arr(Ashen.Generated.Core2DamageKeys.UsedArmaments) ?? new JArray();
                receipt.Notices = Core2Skills.LevelNotices(d, new[] { receipt.ClassAward }.Concat(receipt.TrackAwards.OfType<JObject>()), used);
            }
            if (!victory)
            {
                run.Put(K.Hp, 0);
                run[RK.CombatEntered] = Js.Null();
                Finish(receipt, Core2RewardDoor.Begin(d, run, rng, RV.SourceCombat, RV.AfterDefeat, 0, receipt.Notices, true));
                return receipt;
            }
            // Recovery after combat (Settings, Advanced) restores nothing at its defaults; the vital-renewal feat is not part of a Core 2 run.
            var stats = run.Obj(WK.Stats) ?? throw new InvalidOperationException(Ashen.Generated.RewardsMessages.RunHasNoStats);
            stats.Put(WK.FightsWon, stats.Num(WK.FightsWon) + 1);
            run[RK.CombatEntered] = Js.Null();
            receipt.Cinders = Math.Floor(RewardRolls.RuneReward(d.Rewards, rng, pool, run[K.Relics]));
            run.Put(RK.Cinders, run.Num(RK.Cinders) + receipt.Cinders);
            var boss = pool == WV.Boss;
            if (boss)
            {
                var beaten = new List<string>(Js.Items(run[WK.BossesBeaten]).Select(Js.Str));
                foreach (var enemy in Js.Items(encounter[K.Enemies]).Select(Js.Str)) if (!beaten.Contains(enemy)) beaten.Add(enemy);
                run[WK.BossesBeaten] = new JArray(beaten);
            }
            var acts = d.Module(run, MI.Routes)?[RW.Acts];
            if (boss && run.Num(RK.ActNumber) >= (Js.Truthy(acts) ? Js.D(acts) : d.Rules.Num(RW.DefaultActs)))
            {
                Finish(receipt, Core2RewardDoor.Begin(d, run, rng, RV.SourceCombat, RV.AfterVictory, 0, receipt.Notices, true));
                return receipt;
            }
            Finish(receipt, Core2RewardDoor.Begin(d, run, rng, RV.SourceCombat, boss ? RV.AfterAdvanceAct : RV.AfterMap, 0, receipt.Notices, false));
            return receipt;
        }

        private static void Finish(Core2CombatEndReceipt receipt, Core2RewardStep step)
        {
            receipt.Next = step.Next;
            receipt.OfferId = step.OfferId;
        }
    }
}
