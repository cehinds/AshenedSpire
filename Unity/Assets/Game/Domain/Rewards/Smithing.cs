using System;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using WK = Ashen.Generated.RewardsKeys;
using WM = Ashen.Generated.RewardsMessages;

namespace Ashen.Domain.Rewards
{
    /// <summary>
    /// The Smithing Stone faucet (shipped model/smithing.js grantSmithingReward over smithingRules.js
    /// normalizeSmithingRules): a resolved reward pays balance.smithing.rewardByPool[pool] exactly once per reward id.
    /// Only the reward table is normalized here; the services table, the plans and the commits are the smith's one
    /// port in Ashen.Domain.Shop (SmithServices, ItemSmithing, CardExtraction), which the merchant, the events and the
    /// rest stop all call (D-065, D-112). The stone balance both shipped files read is this one.
    /// </summary>
    public static class Smithing
    {
        /// <summary>normalizeSmithingRules(raw).rewardByPool: every pool of the rules' list an integer ≥ 0, no unknown pool.</summary>
        public static JObject RewardByPool(RewardsData d)
        {
            if (!(d.Balance[WK.Smithing] is JObject source)) throw new InvalidOperationException(WM.SmithingRulesNotObject);
            if (!(source[WK.RewardByPool] is JObject rewards)) throw new InvalidOperationException(WM.RewardByPoolNotObject);
            var pools = d.RuleList(WK.Smithing, WK.RewardPools);
            var out_ = new JObject();
            foreach (var pool in pools)
            {
                var value = rewards[pool];
                if (!Js.IsInt(value) || Js.D(value) < 0) throw new InvalidOperationException(RunJs.Fmt(WM.RewardByPoolNotInteger, pool));
                out_[pool] = value.DeepClone();
            }
            foreach (var p in rewards.Properties())
                if (!pools.Contains(p.Name)) throw new InvalidOperationException(RunJs.Fmt(WM.RewardByPoolUnknownPool, p.Name));
            return out_;
        }

        /// <summary>stoneBalance(run) (smithing.js and cardExtraction.js alike): run.smithingStones as an integer ≥ 0; absent reads 0.</summary>
        public static double StoneBalance(JObject run)
        {
            var value = run[RK.SmithingStones];
            if (Js.Nullish(value)) return 0;
            if (!Js.IsInt(value) || Js.D(value) < 0) throw new InvalidOperationException(WM.SmithingStonesNotInteger);
            return Js.D(value);
        }

        /// <summary>
        /// grantSmithingReward(registries, run, pool, rewardId) → { pool, rewardId, amount, duplicate, stoneBalanceAfter }:
        /// pays the pool's stones and records the claim; a claimed id pays nothing.
        /// </summary>
        public static JObject GrantReward(RewardsData d, JObject run, string pool, string rewardId)
        {
            var rewardByPool = RewardByPool(d);
            if (pool == null || rewardByPool[pool] == null) throw new InvalidOperationException(RunJs.Fmt(WM.UnknownSmithingPool, pool));
            if (string.IsNullOrEmpty(rewardId)) throw new InvalidOperationException(WM.SmithingRewardIdBlank);
            var claimed = run[RK.SmithingRewardClaims] as JArray ?? new JArray();
            if (claimed.Any(c => Js.Str(c) == rewardId))
                return Js.Obj(WK.Pool, pool, WK.RewardId, rewardId, K.Amount, 0.0, WK.Duplicate, true, WK.StoneBalanceAfter, StoneBalance(run));
            var amount = Js.D(rewardByPool[pool]);
            run.Put(RK.SmithingStones, StoneBalance(run) + amount);
            var next = new JArray(claimed.Select(c => c.DeepClone())) { rewardId };
            run[RK.SmithingRewardClaims] = next;
            return Js.Obj(WK.Pool, pool, WK.RewardId, rewardId, K.Amount, amount, WK.Duplicate, false, WK.StoneBalanceAfter, run.Num(RK.SmithingStones));
        }
    }
}
