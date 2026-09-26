using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WM = Ashen.Generated.RewardsMessages;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.Domain.Rewards
{
    /// <summary>What the run's owner knows beside the run: the profile's found armaments and the level-up dial.</summary>
    public sealed class CombatEndOptions
    {
        /// <summary>saves.loadMeta().found — armaments the profile has held (read-only here; rollDrop prefers others).</summary>
        public IList<string> Found = new List<string>();

        /// <summary>resolveLevelUpValue(settings) — points per character level; null or non-positive reads balance.levelUp.</summary>
        public JToken PointsPerLevel;

        /// <summary>
        /// legacyDungeon.js resolveDungeonNode(run), for a fight won inside a legacy dungeon (the run loop's). Null: a
        /// dungeon fight is deferred and throws by name, as before.
        /// </summary>
        public Action<JObject> DungeonResolver;
    }

    /// <summary>
    /// What a fight's end did (the post-combat receipt): the outcome ('defeat', 'victory' — the summit, the run is
    /// over — or 'reward', the pending-reward checkpoint is on the run), the ledgers' awards and, past a win, the
    /// Smithing Stone receipt and the rewards offered.
    /// </summary>
    public sealed class CombatEndReceipt
    {
        public string Outcome;
        public JObject TrackReceipt;
        public JArray SkillAwards;
        public JObject ClassAward;
        public double LevelXp;
        public JObject LevelAward;
        public JObject XpGains;
        public JObject SmithingStoneReceipt;
        public JObject Rewards;

        /// <summary>The receipt as a JSON document (the oracle's shape and key order).</summary>
        public JObject ToJson()
        {
            var o = Js.Obj(WK.Outcome, Outcome, WK.TrackReceipt, TrackReceipt, WK.SkillAwards, SkillAwards, WK.ClassAward, (JToken)ClassAward ?? Js.Null(),
                WK.LevelXp, LevelXp, WK.LevelAward, LevelAward, WK.XpGains, XpGains);
            if (SmithingStoneReceipt != null) o[WK.SmithingStoneReceipt] = SmithingStoneReceipt.DeepClone();
            if (Rewards != null) o[WK.Rewards] = Rewards.DeepClone();
            return (JObject)o.DeepClone();
        }
    }

    /// <summary>
    /// The post-combat pipeline (shipped main.js onCombatEnd, rollSkillDrafts, rollClassDrafts, beginPendingReward,
    /// rollDrop and victoryTitle): the pure, run-writing half of a fight's end, in the shipped order and on the shipped
    /// RNG streams. Screens, audio, the victory beat, saves and the profile's run record are the caller's (D-067);
    /// journeys are deferred (D-066); a legacy dungeon's fight resolves its node through the run loop's DungeonResolver (D-078l).
    /// </summary>
    public static class CombatEnd
    {
        /// <summary>
        /// onCombatEnd(result, combat, enc) on <paramref name="run"/>: write the fight back, pay the skill, class and
        /// character ledgers, restamp the deck, then — on a win — count it, pay the Smithing Stone and roll the door's
        /// rewards into run.pendingReward (a boss past the final act ends the run instead). <paramref name="rng"/> is the
        /// run's RNG, the one the fight drew from.
        /// </summary>
        public static CombatEndReceipt Apply(RewardsData d, JObject run, CombatState combat, JObject enc, string result, Rng rng, CombatEndOptions options = null)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (combat == null) throw new ArgumentNullException(nameof(combat));
            options ??= new CombatEndOptions();
            rng ??= combat.Rng;
            var inDungeon = run.Is(RK.LegacyDungeon);
            if (run.Is(RK.Journey) || (inDungeon && options.DungeonResolver == null)) throw new NotSupportedException(WM.JourneyCombatEndDeferred);
            var pool = enc?.Str(WK.Pool);
            var victory = result == V.Victory;

            // The fight and the run share one loadout (main.js enterCombat passes run.loadout and rejoins a restored
            // fight's copy), so equipment changed mid-fight is still changed now.
            if (combat.Loadout != null && !ReferenceEquals(run[K.Loadout], combat.Loadout)) run[K.Loadout] = combat.Loadout.DeepClone();
            var p = combat.Player;
            SetOrRemove(run, K.Flasks, p[K.Flasks]);
            if (p.Is(K.FlaskCharges)) run[K.FlaskCharges] = Js.Spread(p.Obj(K.FlaskCharges));
            var current = d.Run.RuleObj(K.Loadout, RK.PoolCurrent);
            foreach (var maxField in Loadout.PoolFields(d.Run))
            {
                var field = current.Str(maxField);
                SetOrRemove(run, field, p[field]);
                SetOrRemove(run, maxField, p[maxField]);
            }
            run[K.EquipmentPoolDeficits] = Js.Spread(combat.EquipmentPoolDeficits);

            var receipt = new CombatEndReceipt { TrackReceipt = SkillXp.Receipt(combat) };
            receipt.SkillAwards = Skills.ApplyXp(d, run, receipt.TrackReceipt);
            receipt.ClassAward = ClassTree.AwardClassXp(d, run, victory, pool);
            receipt.LevelXp = LevelUp.CombatLevelXp(d, victory, pool, combat.EventLog.Count(e => e.Str(K.Type) == E.EnemyDied));
            receipt.LevelAward = LevelUp.AwardLevelXp(d, run, receipt.LevelXp, options.PointsPerLevel);
            receipt.XpGains = CombatXpGains(receipt.TrackReceipt, new[] { receipt.ClassAward }, receipt.LevelAward.Num(WK.Gained));
            StartingDeck.StampDeck(d.Run, run, combat.EquipmentChanged);

            if (!victory)
            {
                run.Put(K.Hp, 0);
                receipt.Outcome = V.Defeat;
                return receipt;
            }

            var stats = run.Obj(WK.Stats) ?? throw new InvalidOperationException(WM.RunHasNoStats);
            stats.Put(WK.FightsWon, stats.Num(WK.FightsWon) + 1);
            if (inDungeon) options.DungeonResolver(run);
            run[RK.CombatEntered] = Js.Null();
            var rewardParts = new List<string> { WV.CombatRewardPrefix, RunJs.Key(run[RK.ActNumber]), RunJs.Key(run[RK.Floor]), run.Is(RK.MapNodeId) ? RunJs.Key(run[RK.MapNodeId]) : WV.UnknownNode };
            if (inDungeon) rewardParts.Add(RunJs.Key(run.Obj(RK.LegacyDungeon)[WK.Current]));
            rewardParts.Add(pool ?? V.Undefined);
            var rewardId = string.Join(V.KeySeparator, rewardParts);
            receipt.SmithingStoneReceipt = Smithing.GrantReward(d, run, pool, rewardId);

            if (pool == WV.Boss)
            {
                if (!run.Is(WK.BossesBeaten)) run[WK.BossesBeaten] = new JArray();
                var beaten = run.Arr(WK.BossesBeaten);
                var enemies = Js.Items(enc[K.Enemies]).Select(RunJs.Key).ToList();
                foreach (var id in enemies) if (!beaten.Any(b => Js.Str(b) == id)) beaten.Add(id);
                if (!run.Is(WK.BossGroups)) run[WK.BossGroups] = new JObject();
                var groups = run.Obj(WK.BossGroups);
                var held = Combat.Equipment.EquippedPieces(d.Combat, run.Obj(K.Loadout), run.Str(RK.Class), new JObject())
                    .SelectMany(piece => Js.Items(piece[K.ItemTypeTags]).Select(RunJs.Key)).Distinct().ToList();
                foreach (var id in enemies)
                    groups[id] = new JArray(Js.Items(groups[id]).Select(RunJs.Key).Concat(held).Distinct());
                if (!inDungeon && run.Num(RK.ActNumber) >= d.RuleNum(WK.Summit, WK.FinalAct) && !ModOn(d, run, RK.Endless))
                {
                    receipt.Outcome = V.Victory;
                    return receipt;
                }
                var bossArmament = RollDrop(d, run, rng, options, WV.Boss);
                var drops = d.Run.EquipmentBalance.Obj(WK.Drops) ?? new JObject();
                var bossDrafts = RollSkillDrafts(d, run, rng, WV.Boss);
                var bossClassDrafts = RollClassDrafts(d, run, rng);
                var cinders = RewardRolls.RuneReward(d, rng, WV.Boss, run[K.Relics]) + (bossArmament != null ? 0 : drops.Or0(WK.ConsolationCinders));
                var cardIds = bossDrafts.Count > 0 || bossClassDrafts.Count > 0
                    ? new JArray()
                    : RewardRolls.CardRewardIds(d, rng, run.Str(RK.Class), WV.Boss, run[K.Relics], ModOn(d, run, WK.ChaosRewards));
                var relicId = RewardRolls.RelicReward(d, rng, run[K.Relics], d.RuleList(WK.Rewards, WK.BossRelicRarities));
                receipt.Rewards = Js.Obj(WK.Title, VictoryTitle(d, enc, pool), RK.Cinders, cinders, WK.ClassDrafts, bossClassDrafts, WK.SkillDrafts, bossDrafts,
                    WK.CardIds, cardIds, K.RelicId, Js.S(relicId), WK.ArmamentId, Js.S(bossArmament), WK.SmithingStoneReceipt, receipt.SmithingStoneReceipt.DeepClone(),
                    WK.XpGains, receipt.XpGains.DeepClone());
                BeginPendingReward(run, receipt.Rewards, WV.Boss, inDungeon ? WV.MapDoor : WV.AdvanceAct);
                receipt.Outcome = WV.RewardOutcome;
                return receipt;
            }

            var drafts = RollSkillDrafts(d, run, rng, pool);
            var classDrafts = RollClassDrafts(d, run, rng);
            var runeReward = RewardRolls.RuneReward(d, rng, pool, run[K.Relics]);
            var offer = drafts.Count > 0 || classDrafts.Count > 0
                ? new JArray()
                : RewardRolls.CardRewardIds(d, rng, run.Str(RK.Class), pool, run[K.Relics], ModOn(d, run, WK.ChaosRewards));
            var flaskId = RewardRolls.FlaskDrop(d, rng, run);
            var relic = pool == WV.Elite ? RewardRolls.RelicReward(d, rng, run[K.Relics], d.RuleList(WK.Rewards, WK.RelicRarities)) : null;
            var armamentId = RollDrop(d, run, rng, options, pool);
            receipt.Rewards = Js.Obj(WK.Title, VictoryTitle(d, enc, pool), RK.Cinders, runeReward, WK.ClassDrafts, classDrafts, WK.SkillDrafts, drafts,
                WK.CardIds, offer, K.FlaskId, Js.S(flaskId), K.RelicId, Js.S(relic), WK.ArmamentId, Js.S(armamentId),
                WK.SmithingStoneReceipt, receipt.SmithingStoneReceipt.DeepClone(), WK.XpGains, receipt.XpGains.DeepClone());
            BeginPendingReward(run, receipt.Rewards, pool, WV.MapDoor);
            receipt.Outcome = WV.RewardOutcome;
            return receipt;
        }

        /// <summary><c>run.x = value</c>: undefined drops the key (as JSON.stringify does), anything else is copied in.</summary>
        private static void SetOrRemove(JObject run, string key, JToken value)
        {
            if (value == null) run.Remove(key);
            else run[key] = value.DeepClone();
        }

        /// <summary>activeMods(run.custom)[mod]: the run's own mods plus the first `ascension` of the ascension order.</summary>
        public static bool ModOn(RewardsData d, JObject run, string mod)
        {
            var custom = run.Obj(RK.Custom);
            if (!run.Is(RK.Custom)) return false;
            var active = Js.Spread(custom?.Obj(K.Mods));
            var ascension = Js.Or0(custom?[WK.Ascension]);
            for (var i = 0; i < ascension && i < d.AscensionOrder.Count; i++) active[RunJs.Key(d.AscensionOrder[i])] = true;
            return active.Is(mod);
        }

        /// <summary>victoryTitle(enc): a boss falls by name; an elite is vanquished; anything else is a victory.</summary>
        public static string VictoryTitle(RewardsData d, JObject enc, string pool)
        {
            if (pool == WV.Boss)
                return RunJs.Fmt(d.RuleStr(WK.Titles, WV.Boss), d.Combat.Enemies.Get(Js.Str(enc.Arr(K.Enemies)?.FirstOrDefault())).Str(K.Name).ToUpperInvariant());
            return d.RuleStr(WK.Titles, pool == WV.Elite ? WV.Elite : WV.Normal);
        }

        /// <summary>rollDrop(source): a pure armament roll against the profile's found set and what the run carries.</summary>
        private static string RollDrop(RewardsData d, JObject run, Rng rng, CombatEndOptions options, string source) =>
            RewardRolls.ArmamentDrop(d, rng, source, options.Found ?? new List<string>(), RewardRolls.CarriedIds(run.Obj(K.Loadout)));

        /// <summary>
        /// rollSkillDrafts(pool): per track with a draft queued, up to balance.skill.draftsPerCombat rows rolled on
        /// 'cardRewards' at the door's odds; a track whose schools offer nothing rolls no row and keeps its draft.
        /// </summary>
        private static JArray RollSkillDrafts(RewardsData d, JObject run, Rng rng, string pool)
        {
            var perDoor = d.Balance.Obj(K.Skill).Num(WK.DraftsPerCombat);
            var out_ = new JArray();
            foreach (var track in Skills.Tracks(d))
            {
                var row = run.Obj(K.Skills)?.Obj(track.Id);
                if (row == null || !(row.Num(WK.PendingDrafts) > 0)) continue;
                for (var i = 0; i < Math.Min(perDoor, row.Num(WK.PendingDrafts)); i++)
                {
                    var cardIds = RewardRolls.SkillDraftIds(d, rng, run.Str(RK.Class), run.Obj(K.Loadout), track.Id, row.Num(K.Level), pool, ModOn(d, run, WK.ChaosRewards));
                    if (cardIds.Count > 0) out_.Add(Js.Obj(WK.SkillId, track.Id, K.Level, row[K.Level]?.DeepClone(), WK.CardIds, cardIds));
                }
            }
            return out_;
        }

        /// <summary>rollClassDrafts(): one class draft per door while the class track has one queued.</summary>
        private static JArray RollClassDrafts(RewardsData d, JObject run, Rng rng)
        {
            var row = run.Obj(K.Skills)?.Obj(Skills.ClassSkillId(RunJs.Key(run[RK.Class])));
            if (row == null || !(row.Num(WK.PendingDrafts) > 0)) return new JArray();
            var nodeIds = RewardRolls.ClassDraftIds(d, rng, run.Str(RK.Class), run[K.CoreTags], row.Num(K.Level));
            return nodeIds.Count > 0
                ? new JArray(Js.Obj(K.ClassId, run[RK.Class]?.DeepClone(), K.Level, row[K.Level]?.DeepClone(), WK.NodeIds, nodeIds))
                : new JArray();
        }

        /// <summary>beginPendingReward(rewards, { source, after }): the resumable reward checkpoint on the run.</summary>
        public static void BeginPendingReward(JObject run, JObject rewards, string source, string after)
        {
            var taken = rewards.Obj(WK.SmithingStoneReceipt)?.Num(K.Amount) > 0;
            run[WK.PendingReward] = Js.Obj(RK.SchemaVersion, 1.0, RK.Source, source, WK.After, after, WK.Rewards, rewards.DeepClone(),
                WK.States, taken ? Js.Obj(WK.SmithingStone, WV.Taken) : new JObject(), WK.ChosenCardId, Js.Null(),
                WK.ChosenDraftCardIds, new JObject(), WK.ChosenDraftNodeIds, new JObject());
        }

        /// <summary>
        /// combatXpGains({ receipt, awards, levelGained }) → { level, tracks }: the fight's per-track XP plus every award
        /// the run's owner made on top (summed, floored), and what the character level was paid.
        /// </summary>
        public static JObject CombatXpGains(JObject receipt, IEnumerable<JObject> awards, double levelGained)
        {
            var tracks = new JObject();
            void Add(string id, JToken xp)
            {
                if (string.IsNullOrEmpty(id) || !(Js.IsFinite(xp) && Js.D(xp) > 0)) return;
                tracks.Put(id, tracks.Or0(id) + Math.Floor(Js.D(xp)));
            }
            foreach (var p in (receipt ?? new JObject()).Properties()) Add(p.Name, p.Value);
            foreach (var award in awards ?? Enumerable.Empty<JObject>())
                if (award != null) Add(award.Str(WK.SkillId), award[WK.Gained]);
            var level = !double.IsNaN(levelGained) && !double.IsInfinity(levelGained) && levelGained > 0 ? Math.Floor(levelGained) : 0;
            return Js.Obj(K.Level, level, WK.Tracks, tracks);
        }
    }
}
