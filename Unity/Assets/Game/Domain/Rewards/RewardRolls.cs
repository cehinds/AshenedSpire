using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using RngStream = Ashen.Generated.RngStream;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using RK = Ashen.Generated.RunKeys;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;
using CombatMath = Ashen.Generated.CombatMath;

namespace Ashen.Domain.Rewards
{
    /// <summary>
    /// The combat reward rolls (shipped engine/encounters.js, SPEC §6): cinders on 'misc', the card offer and both draft
    /// kinds on 'cardRewards', relics on 'relicRewards', the flask pity roll on 'flaskRewards' and armament drops on
    /// 'armaments' — the same streams, draw order and weight walks as shipped, so a seed replays exactly. The only run
    /// write here is the flask pity counter (run.flaskChancePct).
    /// </summary>
    public static class RewardRolls
    {
        private static JObject Rewards(RewardsData d) => d.Balance.Obj(WK.Rewards) ?? new JObject();

        private static double Guard(RewardsData d) => d.RuleNum(WK.Rolls, WK.Guard);

        /// <summary>passiveFlag(registries, relicIds, key): whether an owned relic's passives set the key to true.</summary>
        public static bool PassiveFlag(RewardsData d, JToken relicIds, string key)
        {
            foreach (var id in Js.Items(relicIds))
            {
                var p = d.Run.Relics.Get(Js.Str(id)).Obj(K.Passives);
                if (p != null && p[key]?.Type == JTokenType.Boolean && p.Value<bool>(key)) return true;
            }
            return false;
        }

        /// <summary>rollRuneReward(registries, rng, pool, relicIds): cinders for a pool, scaled by runeGainMult passives (floored).</summary>
        public static double RuneReward(RewardsData d, Rng rng, string pool, JToken relicIds)
        {
            var range = Rewards(d).Obj(RK.Cinders)?[pool] as JArray ?? throw new InvalidOperationException(RunJs.Fmt(M.UnknownRegistryId, RK.Cinders, pool));
            var value = rng.Int(RngStream.Misc, (int)Js.D(range[0]), (int)Js.D(range[1]));
            return Math.Floor(value * Cards.PassiveMult(d.Combat, relicIds as JArray, WK.RuneGainMult, null));
        }

        /// <summary>cardRewardRarityWeights(registries, { classId, pool, flatRarity }): authored reward odds; Chaos is flat.</summary>
        public static JObject RarityWeights(RewardsData d, string classId, string pool, bool flatRarity)
        {
            if (flatRarity) return d.RuleObj(WK.Rewards, WK.FlatRarityWeights);
            var rewards = Rewards(d);
            var byPool = rewards.Obj(WK.RarityWeights) ?? new JObject();
            var poolId = pool != null && byPool.ContainsKey(pool) ? pool : WV.Normal;
            var byClass = classId != null ? rewards.Obj(WK.RarityWeightsByClass)?.Obj(classId)?[poolId] : null;
            return Js.Truthy(byClass) ? (JObject)byClass : byPool.Obj(poolId);
        }

        /// <summary>The weight walk every rarity-first roll shares: draw once, subtract weights in order, last rarity on a miss.</summary>
        private static string WalkRarity(Rng rng, RngStream stream, List<string> rarities, JObject weights, double total)
        {
            var roll = rng.Float(stream) * total;
            var rarity = rarities[rarities.Count - 1];
            foreach (var r in rarities)
            {
                roll -= weights.Num(r);
                if (roll < 0)
                {
                    rarity = r;
                    break;
                }
            }
            return rarity;
        }

        private static OrderedMap<List<string>> ByRarity(RewardsData d, string classId, Func<JObject, bool> keep)
        {
            var byRarity = new OrderedMap<List<string>>();
            foreach (var idToken in Js.Items(d.Run.Classes.Get(classId)[WK.CardPool]))
            {
                var id = Js.Str(idToken);
                var def = d.Run.Cards.Get(id);
                if (!keep(def)) continue;
                var rarity = RunJs.Key(def[RK.Rarity]);
                if (!byRarity.TryGetValue(rarity, out var list)) byRarity[rarity] = list = new List<string>();
                list.Add(id);
            }
            return byRarity;
        }

        /// <summary>
        /// rollCardRewardIds(registries, rng, { classId, pool, relicIds, flatRarity }): distinct card ids, rarity first
        /// per the door's odds (elites offer one more with an eliteExtraCardReward relic).
        /// </summary>
        public static JArray CardRewardIds(RewardsData d, Rng rng, string classId, string pool, JToken relicIds, bool flatRarity)
        {
            var count = Rewards(d).Num(WK.CardChoices);
            if (pool == WV.Elite && PassiveFlag(d, relicIds, WK.EliteExtraCardReward)) count += 1;
            var weights = RarityWeights(d, classId, pool, flatRarity);
            var byRarity = ByRarity(d, classId, def => true);
            var rarities = weights.Properties().Select(p => p.Name).Where(r => byRarity.TryGetValue(r, out var l) && l.Count > 0 && weights.Num(r) > 0).ToList();
            var total = rarities.Aggregate(0.0, (a, r) => a + weights.Num(r));
            var picks = new List<string>();
            if (!Js.Truthy(Js.N(total))) return new JArray();
            double guard = 0;
            while (picks.Count < count && guard++ < Guard(d))
            {
                var rarity = WalkRarity(rng, RngStream.CardRewards, rarities, weights, total);
                var options = byRarity[rarity].Where(id => !picks.Contains(id)).ToList();
                if (options.Count == 0) continue;
                picks.Add(rng.Pick(RngStream.CardRewards, options));
            }
            return new JArray(picks);
        }

        /// <summary>
        /// rollSkillDraftIds(registries, rng, { classId, loadout, skillId, level, pool, flatRarity }): distinct card ids for
        /// one skill draft — the class pool filtered to the track's schools and the rarities its level unlocks, at the
        /// door's odds, balance.skill.draftSize picks. An empty pool rolls nothing and draws nothing.
        /// </summary>
        public static JArray SkillDraftIds(RewardsData d, Rng rng, string classId, JObject loadout, string skillId, double level, string pool, bool flatRarity)
        {
            var count = Js.D(d.Balance.Obj(K.Skill)?[WK.DraftSize]);
            var schools = new HashSet<string>(Skills.Schools(d, loadout, skillId), StringComparer.Ordinal);
            var unlocked = Skills.RarityUnlockedAt(d, level);
            if (schools.Count == 0 || unlocked.Count == 0 || !(count > 0)) return new JArray();
            var weights = RarityWeights(d, classId, pool, flatRarity);
            var byRarity = ByRarity(d, classId, def => unlocked.Contains(RunJs.Key(def[RK.Rarity])) && Js.Items(def[K.Tags]).Any(t => Js.IsStr(t) && schools.Contains(Js.Str(t))));
            var rarities = unlocked.Where(r => byRarity.TryGetValue(r, out var l) && l.Count > 0 && weights.Num(r) > 0).ToList();
            var total = rarities.Aggregate(0.0, (a, r) => a + weights.Num(r));
            var picks = new List<string>();
            if (!Js.Truthy(Js.N(total))) return new JArray();
            double guard = 0;
            while (picks.Count < count && guard++ < Guard(d))
            {
                var rarity = WalkRarity(rng, RngStream.CardRewards, rarities, weights, total);
                var options = byRarity[rarity].Where(id => !picks.Contains(id)).ToList();
                if (options.Count == 0)
                {
                    if (rarities.All(r => byRarity[r].All(picks.Contains))) break;
                    continue;
                }
                picks.Add(rng.Pick(RngStream.CardRewards, options));
            }
            return new JArray(picks);
        }

        /// <summary>
        /// rollClassDraftIds(registries, rng, { classId, coreTags, level }): distinct tree node ids for one class draft,
        /// balance.skill.draftSize picks from the draftable pool on 'cardRewards'.
        /// </summary>
        public static JArray ClassDraftIds(RewardsData d, Rng rng, string classId, JToken coreTags, double level)
        {
            var count = Js.D(d.Balance.Obj(K.Skill)?[WK.DraftSize]);
            var pool = ClassTree.DraftPool(d, classId, coreTags, level);
            if (pool.Count == 0 || !(count > 0)) return new JArray();
            var picks = new List<string>();
            while (picks.Count < Math.Min(count, pool.Count)) picks.Add(rng.Pick(RngStream.CardRewards, pool.Where(id => !picks.Contains(id)).ToList()));
            return new JArray(picks);
        }

        /// <summary>flaskKindOf(def) for the charge kinds: the explicit kind, else the first kindByOp rule an effect matches.</summary>
        private static string FlaskKindOf(RewardsData d, JObject def)
        {
            if (Js.IsStr(def[K.Kind])) return def.Str(K.Kind);
            foreach (var rule in Js.Items(d.Combat.Engine.Obj(K.Flasks)?[K.KindByOp]).OfType<JObject>())
                if (Js.Items(def[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Op) == rule.Str(K.Op))) return rule.Str(K.Kind);
            return null;
        }

        /// <summary>chargeKindForFlask(registries, id): the charge kind a flask id backs (first authored of its kind), or the legacy seam's.</summary>
        public static string ChargeKindForFlask(RewardsData d, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var kind in RunJs.Strs(d.Combat.Engine.Obj(K.Flasks)?[K.ChargeKinds]))
            {
                var def = d.Combat.Flasks.All.FirstOrDefault(f => FlaskKindOf(d, f) == kind)
                          ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.MissingChargeFlask, kind));
                if (def.Str(K.Id) == id) return kind;
            }
            return d.RuleObj(K.Flasks, WK.LegacyChargeKindById).Str(id);
        }

        /// <summary>utilityFlaskIds(registries): every flask id that backs no charge pool, in registry order.</summary>
        public static List<string> UtilityFlaskIds(RewardsData d) =>
            d.Combat.Flasks.Ids.Where(id => ChargeKindForFlask(d, id) == null).ToList();

        /// <summary>
        /// rollFlaskDrop(registries, rng, run) → flask id | null: a decaying chance persisted on run.flaskChancePct —
        /// −step on a drop, +step on a miss, clamped to 0..100.
        /// </summary>
        public static string FlaskDrop(RewardsData d, Rng rng, JObject run)
        {
            var bal = Rewards(d);
            if (Js.Nullish(run[WK.FlaskChancePct])) run[WK.FlaskChancePct] = bal[WK.FlaskDropBasePct]?.DeepClone();
            var hit = rng.Float(RngStream.FlaskRewards) * CombatMath.Percent < run.Num(WK.FlaskChancePct);
            if (hit)
            {
                run.Put(WK.FlaskChancePct, Math.Max(0, run.Num(WK.FlaskChancePct) - bal.Num(WK.FlaskDropStepPct)));
                var pool = UtilityFlaskIds(d);
                return pool.Count > 0 ? rng.Pick(RngStream.FlaskRewards, pool) : null;
            }
            run.Put(WK.FlaskChancePct, Math.Min(CombatMath.Percent, run.Num(WK.FlaskChancePct) + bal.Num(WK.FlaskDropStepPct)));
            return null;
        }

        /// <summary>
        /// rollRelicReward(registries, rng, ownedIds, { rarities }) → relic id | null: the reward-pool relics of those
        /// rarities the run does not own.
        /// </summary>
        public static string RelicReward(RewardsData d, Rng rng, JToken ownedIds, List<string> rarities)
        {
            var owned = new HashSet<string>(Js.Items(ownedIds).Select(Js.Str).Where(s => s != null), StringComparer.Ordinal);
            var pool = d.Run.Relics.All
                .Where(r => (Js.Truthy(r[WK.Pool]) ? RunJs.Key(r[WK.Pool]) : WV.RewardPool) == WV.RewardPool && rarities.Contains(RunJs.Key(r[RK.Rarity])) && !owned.Contains(r.Str(K.Id)))
                .Select(r => r.Str(K.Id)).ToList();
            return pool.Count > 0 ? rng.Pick(RngStream.RelicRewards, pool) : null;
        }

        /// <summary>carriedIds(loadout): every armament this run has access to — storage, then whatever is slotted.</summary>
        public static List<string> CarriedIds(JObject loadout)
        {
            var out_ = new List<string>();
            if (loadout == null) return out_;
            foreach (var id in Js.Items(loadout[RK.Storage])) out_.Add(Js.Str(id));
            foreach (var p in (loadout.Obj(K.Sets) ?? new JObject()).Properties())
                foreach (var id in Js.Items(p.Value).Select(Js.Str))
                    if (!string.IsNullOrEmpty(id) && !out_.Contains(id)) out_.Add(id);
            return out_;
        }

        /// <summary>
        /// rollArmamentDrop(registries, rng, { source, found, carried }) → id | null: the source's chance on 'armaments',
        /// then rarity first and a piece by dropWeight, preferring pieces neither found nor carried.
        /// </summary>
        public static string ArmamentDrop(RewardsData d, Rng rng, string source, IEnumerable<string> found, IEnumerable<string> carried)
        {
            var cfg = d.Run.EquipmentBalance.Obj(WK.Drops) ?? new JObject();
            if (!cfg.Is(K.Enabled)) return null;
            var chance = source != null ? cfg.Obj(WK.Chance)?[source] : null;
            if (!Js.Truthy(chance)) return null;
            if (rng.Int(RngStream.Armaments, 1, (int)CombatMath.Percent) > Js.D(chance)) return null;
            var weights = (source != null ? cfg.Obj(WK.RarityWeights)?.Obj(source) : null) ?? new JObject();
            var seen = new HashSet<string>(found.Concat(carried).Where(s => s != null), StringComparer.Ordinal);
            var pool = d.Run.EquipmentRows(K.Armaments).Where(a => a.Str(RK.Unlock) == string.Empty).ToList();
            if (cfg.Is(WK.PreferUnfound))
            {
                var fresh = pool.Where(a => !seen.Contains(a.Str(K.Id))).ToList();
                if (fresh.Count == 0) return null;
                pool = fresh;
            }
            var rarities = weights.Properties().Select(p => p.Name).Where(r => pool.Any(a => a.Str(RK.Rarity) == r)).ToList();
            if (rarities.Count == 0) return null;
            var total = rarities.Aggregate(0.0, (a, r) => a + weights.Num(r));
            var rarity = WalkRarity(rng, RngStream.Armaments, rarities, weights, total);
            var candidates = pool.Where(a => a.Str(RK.Rarity) == rarity && RunJs.Number(a[WK.DropWeight]) > 0).ToList();
            if (candidates.Count == 0) return null;
            var pieceTotal = candidates.Aggregate(0.0, (sum, piece) => sum + piece.Num(WK.DropWeight));
            var pieceRoll = rng.Float(RngStream.Armaments) * pieceTotal;
            foreach (var piece in candidates)
            {
                pieceRoll -= piece.Num(WK.DropWeight);
                if (pieceRoll < 0) return piece.Str(K.Id);
            }
            return candidates[candidates.Count - 1].Str(K.Id);
        }
    }
}
