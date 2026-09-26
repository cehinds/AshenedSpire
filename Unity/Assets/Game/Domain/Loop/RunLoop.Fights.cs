using System;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Newtonsoft.Json.Linq;
using LM = Ashen.Generated.LoopMessages;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.Domain.Loop
{
    /// <summary>
    /// How a fight ended for the run: the post-combat receipt (<see cref="CombatEnd.Apply"/>), its outcome — 'defeat' and
    /// 'victory' (the summit) close the run out (<see cref="End"/>), 'reward' leaves the pending reward on the run for
    /// <see cref="RewardDoor.OpenPending"/>.
    /// </summary>
    public sealed class CombatOutcome
    {
        public CombatEndReceipt Receipt;
        public string Outcome;
        public RunEndReceipt End;

        /// <summary>{ outcome, [result, earned] }.</summary>
        public JObject ToJson()
        {
            var o = Js.Obj(WK.Outcome, Outcome);
            if (End != null) foreach (var p in End.ToJson().Properties()) o[p.Name] = p.Value.DeepClone();
            return o;
        }
    }

    public static partial class RunLoop
    {
        /// <summary>
        /// onCombatEnd(result, combat, enc) for the fight <see cref="LoopContext.Fight"/> entered: the post-combat pipeline
        /// (the write-back, the ledgers, the Smithing Stone, the reward rolls — a legacy dungeon's node resolved), then a
        /// death or the summit closes the run out through <see cref="RunEnd.Finish"/>. The screens, audio, the victory beat
        /// and the slot are the caller's.
        /// </summary>
        public static CombatOutcome EndCombat(LoopContext ctx, CombatState combat, string result)
        {
            var fight = ctx.Fight ?? throw new InvalidOperationException(LM.NoFightEntered);
            var options = new CombatEndOptions
            {
                Found = ctx.Found(),
                PointsPerLevel = ctx.Settings.PointsPerLevel?.DeepClone(),
                DungeonResolver = run => LegacyDungeons.ResolveNode(ctx.Data, run),
            };
            var receipt = CombatEnd.Apply(ctx.Data.Rewards, ctx.Run, combat, fight.Encounter, result, ctx.Rng, options);
            ctx.Fight = null;
            var outcome = new CombatOutcome { Receipt = receipt, Outcome = receipt.Outcome };
            if (receipt.Outcome == V.Defeat) outcome.End = RunEnd.Finish(ctx, false);
            else if (receipt.Outcome == V.Victory) outcome.End = RunEnd.Finish(ctx, true);
            return outcome;
        }
    }
}
