using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DeckKeys;
using RV = Ashen.Generated.Core2RewardsValues;
using RW = Ashen.Generated.Core2RewardsKeys;

namespace Ashen.App.Run
{
    /// <summary>One option of a Core 2 reward offer as a screen draws it.</summary>
    public sealed class Core2RewardOptionView
    {
        public int Index;

        /// <summary>The option's kind (<see cref="RV"/>: combatCards, weaponArts, weapons, armor, relics, potions) and registry id.</summary>
        public string Kind;

        public string Id;

        /// <summary>The registry name of the thing offered (its id when the registry has none).</summary>
        public string Name;

        /// <summary>The option was taken (claimed).</summary>
        public bool Taken;

        /// <summary>Take can still be pressed: the offer is not yet claimed.</summary>
        public bool Claimable;

        /// <summary>A card can also go straight into the deck.</summary>
        public bool IsCard;

        /// <summary>The "Add to deck" refusal while the option can still be taken: empty when legal, else <c>notCard</c>, <c>full</c> or <c>limit</c>.</summary>
        public string DeckRefusal = string.Empty;
    }

    /// <summary>One level the fight crossed: the track's label, the levels before and after, the weapons used.</summary>
    public sealed class Core2RewardNoticeView
    {
        public string SkillId;
        public string Label;
        public double Before;
        public double After;
        public IReadOnlyList<string> Weapons = Array.Empty<string>();
    }

    /// <summary>The whole reward door of a Core 2 run as the (future) screen draws it.</summary>
    public sealed class Core2RewardsViewState
    {
        public string OfferId;

        /// <summary>combat or level.</summary>
        public string Source;

        /// <summary>The door has notices only (a death, or the last act's boss): reading them ends the climb.</summary>
        public bool NoticeOnly;

        /// <summary>What is shown first while notices wait: the level notices, then the offer.</summary>
        public bool ShowNotices => Notices.Count > 0;

        public IReadOnlyList<Core2RewardNoticeView> Notices = Array.Empty<Core2RewardNoticeView>();
        public IReadOnlyList<Core2RewardOptionView> Options = Array.Empty<Core2RewardOptionView>();

        /// <summary>How many options may be taken, and how many have been.</summary>
        public int TakeCount;

        public int TakenCount;

        /// <summary>The last fight's cinders (zero when the door is not a fight's).</summary>
        public double Cinders;
    }

    /// <summary>
    /// The reward door of a Core 2 run as display data (WP-15; the seam the spoils screen reads): the level notices still to be read, then the offer's options
    /// with their registry names, what was taken and the refusal "Add to deck" would meet. Engine-free. The screen binds Take, Add to deck and Continue to
    /// <see cref="Core2RunSession.TakeReward"/>, <see cref="Core2RunSession.AddRewardToDeck"/> and <see cref="Core2RunSession.LeaveRewards"/>.
    /// </summary>
    public static class Core2RewardsView
    {
        public static Core2RewardsViewState Build(Core2RunSession session)
        {
            var pending = session.PendingReward;
            if (pending == null) return null;
            var d = session.Core.Rewards;
            var offer = session.RewardOffer;
            var state = new Core2RewardsViewState
            {
                OfferId = pending.Str(RW.OfferId),
                Source = pending.Str(RW.Source),
                NoticeOnly = session.NoticeOnlyDoor,
                Notices = session.ProgressionNotices.Select(n => new Core2RewardNoticeView
                {
                    SkillId = n.Str(RW.SkillId), Label = n.Str(Ashen.Generated.CombatKeys.Label), Before = n.Num(RW.Before), After = n.Num(RW.After),
                    Weapons = Js.Items(n[RW.Weapons]).Select(Js.Str).ToList(),
                }).ToList(),
                Cinders = session.LastEnd?.Receipt?.Cinders ?? 0,
            };
            if (offer == null) return state;
            var claimed = !Js.Nullish(offer[RW.Claimed]);
            state.TakeCount = (int)offer.Num(RW.TakeCount);
            state.TakenCount = offer.Arr(RW.Taken)?.Count ?? 0;
            var options = new List<Core2RewardOptionView>();
            var rows = offer.Arr(RW.Options);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = (JObject)rows[i];
                var kind = row.Str(RW.Kind);
                var taken = Js.Items(offer[RW.Taken]).Any(t => Js.IsNum(t) && (int)Js.D(t) == i);
                var card = kind == RV.KindCombatCards || kind == RV.KindWeaponArts;
                options.Add(new Core2RewardOptionView
                {
                    Index = i, Kind = kind, Id = row.Str(RW.Id), Name = NameOf(d, kind, row.Str(RW.Id)), Taken = taken, Claimable = !claimed && !taken, IsCard = card,
                    DeckRefusal = !claimed && !taken ? session.RewardDeckRefusal(i) : string.Empty,
                });
            }
            state.Options = options;
            return state;
        }

        private static string NameOf(Core2RewardsData d, string kind, string id)
        {
            JObject row = null;
            switch (kind)
            {
                case RV.KindCombatCards:
                case RV.KindWeaponArts:
                    row = d.RunData.Cards.Has(id) ? d.RunData.Cards.Get(id) : null;
                    break;
                case RV.KindRelics:
                    row = d.RunData.Relics.Has(id) ? d.RunData.Relics.Get(id) : null;
                    break;
                case RV.KindPotions:
                    row = d.RunData.Combat.Flasks.Has(id) ? d.RunData.Combat.Flasks.Get(id) : null;
                    break;
                case RV.KindWeapons:
                    row = d.RunData.EquipmentRows(DK.Armaments).FirstOrDefault(r => r.Str(DK.Id) == id);
                    break;
                case RV.KindArmor:
                    row = d.RunData.EquipmentRows(DK.Armour).FirstOrDefault(r => r.Str(DK.Id) == id);
                    break;
            }
            return Js.Truthy(row?[Ashen.Generated.CombatKeys.Name]) ? row.Str(Ashen.Generated.CombatKeys.Name) : id;
        }
    }
}
