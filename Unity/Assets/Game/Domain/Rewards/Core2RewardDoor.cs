using System;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using MI = Ashen.Generated.Core2ModuleIds;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.Core2RewardsValues;
using RW = Ashen.Generated.Core2RewardsKeys;

namespace Ashen.Domain.Rewards
{
    /// <summary>Where the climb goes after a step of the reward door: <see cref="Core2RewardDoor.NextDoor"/> (the door stays open on <see cref="OfferId"/>), <c>map</c>, <c>advanceAct</c>, <c>victory</c> or <c>defeat</c>.</summary>
    public sealed class Core2RewardStep
    {
        public string Next;
        public string OfferId;

        public bool DoorOpen => Next == Core2RewardDoor.NextDoor;
    }

    /// <summary>
    /// The reward door of a Core 2 climb (web main.js beginGameProfileReward and showGameProfileReward, 3900-3940, WP-15): after a fight, the pending marker
    /// on the run (<c>gameProfilePendingReward</c>: the offer, where it came from, what comes after it, the level-up notices still to be read) beside the
    /// persisted offer, so that a reload resumes the same door. The player reads the notices, takes one option or adds a card to the deck
    /// (<see cref="Core2Rewards"/>), and leaves; leaving goes on to the next level offer, the act advance after a boss, or the map. A notice-only door
    /// (a death, the last act's boss) has no offer: reading the notices ends the climb.
    /// </summary>
    public static class Core2RewardDoor
    {
        /// <summary>The door is still open.</summary>
        public const string NextDoor = RV.NextDoor;

        public static string NextAfter(string after)
        {
            switch (after)
            {
                case RV.AfterVictory: return RV.AfterVictory;
                case RV.AfterDefeat: return RV.AfterDefeat;
                case RV.AfterAdvanceAct: return RV.AfterAdvanceAct;
                default: return RV.AfterMap;
            }
        }

        /// <summary>The pending reward on the run, or null.</summary>
        public static JObject Pending(JObject run) => run?.Obj(RW.GameProfilePendingReward);

        /// <summary>
        /// beginGameProfileReward({ source, after, levelOffers, progression, noticeOnly }): opens the door — the offer is drawn and stored, the pending marker
        /// written. A profile without the rewards module, or a notice-only door with no notice, has no door: the climb goes straight on.
        /// </summary>
        public static Core2RewardStep Begin(Core2RewardsData d, JObject run, Rng rng, string source, string after, int levelOffers, JArray notices, bool noticeOnly)
        {
            noticeOnly |= d.Module(run, MI.Rewards) == null;
            if (noticeOnly && (notices == null || notices.Count == 0)) return new Core2RewardStep { Next = NextAfter(after) };
            var offerId = string.Join(RV.IdSeparator, RunJs.Key(run[RK.ActNumber]), RunJs.Key(run[RK.MapNodeId]), source);
            if (!noticeOnly) Core2Rewards.Offer(d, run, rng, offerId, source);
            var pending = new JObject
            {
                [RW.OfferId] = offerId, [RW.Source] = source, [RW.After] = after, [RW.LevelOffers] = levelOffers,
            };
            if (notices != null && notices.Count > 0) pending[RW.Progression] = notices.DeepClone();
            if (noticeOnly) pending[RW.NoticeOnly] = true;
            run[RW.GameProfilePendingReward] = pending;
            return new Core2RewardStep { Next = NextDoor, OfferId = offerId };
        }

        /// <summary>
        /// The progression modal's Continue (acknowledgeSkillLevelNotices, then showGameProfileReward): the notices are read once; a notice-only door then ends
        /// (the pending marker goes and the climb goes on to its <c>after</c>), an offer door shows its offer.
        /// </summary>
        public static Core2RewardStep Acknowledge(JObject run)
        {
            var pending = Pending(run);
            if (pending == null || !(pending[RW.Progression] is JArray notices) || notices.Count == 0) throw new InvalidOperationException(Ashen.Generated.Core2RewardsMessages.NoNotices);
            pending.Remove(RW.Progression);
            if (!Js.Truthy(pending[RW.NoticeOnly])) return new Core2RewardStep { Next = NextDoor, OfferId = pending.Str(RW.OfferId) };
            run.Remove(RW.GameProfilePendingReward);
            return new Core2RewardStep { Next = NextAfter(pending.Str(RW.After)) };
        }

        /// <summary>
        /// The offer screen's Continue: the pending marker goes; a level offer still owed is drawn and opens the door again; otherwise the climb goes on to the
        /// act advance (after a boss) or the map. Whatever was not taken is left.
        /// </summary>
        public static Core2RewardStep Leave(Core2RewardsData d, JObject run, Rng rng)
        {
            var pending = Pending(run) ?? throw new InvalidOperationException(Ashen.Generated.Core2RewardsMessages.NoDoor);
            run.Remove(RW.GameProfilePendingReward);
            var owed = (int)pending.Num(RW.LevelOffers);
            if (owed > 0)
            {
                var offerId = string.Join(RV.IdSeparator, pending.Str(RW.OfferId), RV.LevelSegment, owed.ToString(CultureInfo.InvariantCulture));
                Core2Rewards.Offer(d, run, rng, offerId, RV.SourceLevel);
                var next = (JObject)pending.DeepClone();
                next[RW.OfferId] = offerId;
                next[RW.Source] = RV.SourceLevel;
                next[RW.LevelOffers] = owed - 1;
                run[RW.GameProfilePendingReward] = next;
                return new Core2RewardStep { Next = NextDoor, OfferId = offerId };
            }
            return new Core2RewardStep { Next = pending.Str(RW.After) == RV.AfterAdvanceAct ? RV.AfterAdvanceAct : RV.AfterMap };
        }
    }
}
