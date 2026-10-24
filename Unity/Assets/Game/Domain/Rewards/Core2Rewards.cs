using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using RngStream = Ashen.Generated.RngStream;
using DK = Ashen.Generated.Core2DeckKeys;
using K = Ashen.Generated.CombatKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using RM = Ashen.Generated.Core2RewardsMessages;
using RV = Ashen.Generated.Core2RewardsValues;
using RW = Ashen.Generated.Core2RewardsKeys;

namespace Ashen.Domain.Rewards
{
    /// <summary>"Add to deck" was refused: <see cref="Code"/> is the web's code (<c>notCard</c>, <c>full</c>, <c>limit</c>), the message its sentence.</summary>
    public sealed class DeckAddRefusalException : InvalidOperationException
    {
        public DeckAddRefusalException(string code, string message) : base(message) => Code = code;

        public string Code { get; }
    }

    /// <summary>
    /// Persisted mixed reward offers of a Core 2 run (web model/gameProfileRewards.js, WP-15): an offer is drawn once from the pools the character's
    /// eligibility leaves, stored on the run under its stable id, and claimed without ever editing the active deck; a won card may go straight into the
    /// deck, which then obeys the deck rules. Offers draw on the <c>cardRewards</c> stream. Claims run on a draft of the run, so a refusal leaves the
    /// run and the offer untouched. Engine-free. The shop's stock (WP-17, <see cref="Core2Shop"/>) shares <c>makeOffer</c> with it.
    /// </summary>
    public static class Core2Rewards
    {
        // ------------------------------------------------------------------ pools

        private sealed class Pools
        {
            public List<string> Weapons = new List<string>();
            public List<string> Relics = new List<string>();
            public List<string> CombatCards = new List<string>();
            public List<string> CombatCardsLow = new List<string>();
            public List<string> WeaponArts = new List<string>();
            public List<string> Potions = new List<string>();
            public List<string> Armor = new List<string>();

            public List<string> Of(string kind)
            {
                switch (kind)
                {
                    case RV.KindWeapons: return Weapons;
                    case RV.KindRelics: return Relics;
                    case RV.KindCombatCards: return CombatCards;
                    case RV.KindWeaponArts: return WeaponArts;
                    case RV.KindPotions: return Potions;
                    case RV.KindArmor: return Armor;
                    default: return null;
                }
            }

            public void Set(string kind, List<string> ids)
            {
                switch (kind)
                {
                    case RV.KindWeapons: Weapons = ids; break;
                    case RV.KindRelics: Relics = ids; break;
                    case RV.KindCombatCards: CombatCards = ids; break;
                    case RV.KindWeaponArts: WeaponArts = ids; break;
                    case RV.KindPotions: Potions = ids; break;
                    case RV.KindArmor: Armor = ids; break;
                }
            }
        }

        /// <summary>
        /// Group members are authored references because the legacy catalog's item tags are broader than the core's weapon and armor families: a member is
        /// valid only when its row carries every tag its group requires. Never infer type from art.
        /// </summary>
        private static HashSet<string> GroupMembers(Core2RewardsData d, JObject rules, string category, string family)
        {
            var armament = family == RV.FamilyArmament;
            var rows = d.RunData.EquipmentRows(armament ? DK.Armaments : DK.Armour);
            string KeyOf(JObject row) => armament ? row.Str(DK.Id) : row.Str(RW.ClassId) + RV.ArmourKeySeparator + row.Str(DK.Id);
            var byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var row in rows) byId[KeyOf(row)] = row;
            var references = rules.Obj(RW.Eligibility)?.Obj(category)?[RW.Groups] as JArray
                ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RM.MissingGroups, category));
            var eligible = new HashSet<string>(StringComparer.Ordinal);
            foreach (var groupId in references.Select(Js.Str))
            {
                var group = rules.Obj(RW.EquipmentGroups)?[groupId ?? string.Empty] as JObject;
                if (group == null || group.Str(RW.Family) != family || !(group[RW.Members] is JArray members) || !(group[RW.AllTags] is JArray allTags))
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RM.InvalidGroup, category, groupId));
                foreach (var member in members.Select(Js.Str))
                {
                    if (member == null || !byId.TryGetValue(member, out var row)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RM.UnknownMember, family, member));
                    var tags = new HashSet<string>(Js.Items(row[RW.EntityTags]).Concat(Js.Items(row[RW.ItemTypeTags])).Concat(Js.Items(row[DK.Tags])).Select(Js.Str), StringComparer.Ordinal);
                    if (!allTags.Select(Js.Str).All(tags.Contains)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RM.MissingTags, groupId, member));
                    eligible.Add(member);
                }
            }
            return eligible;
        }

        /// <summary>A card's rank: its own when it has one, else its rarity's (rewards eligibility.combatCards.rankOfRarity), else the first.</summary>
        private static double CardRank(JObject card, JObject rules)
        {
            if (Js.IsSafeInteger(card[DK.Rank])) return card.Num(DK.Rank);
            var of = rules?.Obj(RW.Eligibility)?.Obj(RW.CombatCards)?.Obj(RW.RankOfRarity)?[card.Str(DK.Rarity) ?? string.Empty];
            return Js.Nullish(of) ? 1 : Js.D(of);
        }

        private static IEnumerable<string> LoadoutIds(JObject run) =>
            Js.Items(run.Obj(DK.Loadout)?[RW.Storage]).Select(Js.Str)
                .Concat((run.Obj(DK.Loadout)?.Obj(RW.Sets) ?? new JObject()).Properties().SelectMany(p => Js.Items(p.Value)).Select(Js.Str));

        private static Pools PoolsOf(Core2RewardsData d, JObject run)
        {
            var definitions = d.Content(run, DK.Cards) as JArray ?? new JArray();
            var rules = d.Content(run, RW.Rewards) as JObject;
            // A basic (below minRank) card the character already owns is never offered again.
            var cardRule = rules?.Obj(RW.Eligibility)?.Obj(RW.CombatCards);
            var ownedIds = new HashSet<string>(Js.Items(run[DK.Deck]).Concat(Js.Items(run[DK.Sideboard])).Select(card => Js.IsStr(card) ? Js.Str(card) : Js.Str(Js.Get(card, DK.CardId)) ?? Js.Str(Js.Get(card, DK.Id))), StringComparer.Ordinal);
            var cards = definitions.OfType<JObject>().Where(card =>
                DeckRules.RangeCardAvailable(d.Deck, run, card)
                && (card.Str(DK.Class) == d.Deck.Rules.Str(DK.ColorlessClass) || card.Str(DK.Class) == run.Str(DK.Class))
                && !(card.Str(DK.Rarity) is string rarity && Js.Includes(d.Rules[RW.ExcludedRarities], rarity)) && card.Str(DK.Type) != RV.StatusType
                && !(Js.Truthy(cardRule?[RW.SkipOwnedBasics]) && CardRank(card, rules) < Js.Coalesce(cardRule[RW.MinRank], 0) && ownedIds.Contains(card.Str(DK.Id)))).ToList();
            var equipment = d.Module(run, MI.Equipment) != null;
            var hasCards = d.Module(run, MI.Cards) != null;
            var weapons = equipment ? GroupMembers(d, rules, RW.Weapons, RV.FamilyArmament) : new HashSet<string>();
            var armor = equipment ? GroupMembers(d, rules, RW.Armor, RV.FamilyArmour) : new HashSet<string>();
            var authoredArts = rules?.Obj(RW.Eligibility)?.Obj(RW.WeaponArts)?[RW.CardIds] as JArray ?? throw new InvalidOperationException(RM.MissingWeaponArts);
            if (hasCards && equipment)
                foreach (var id in authoredArts.Select(Js.Str))
                    if (!definitions.Any(card => card is JObject o && o.Str(DK.Id) == id) || !d.RunData.Cards.Has(id))
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RM.UnknownWeaponArt, id));
            var carried = new HashSet<string>(LoadoutIds(run), StringComparer.Ordinal);
            var owned = (run.Obj(DK.Loadout)?.Obj(RW.Sets) ?? new JObject()).Properties().SelectMany(p => Js.Items(p.Value)).Select(Js.Str).ToList();
            var minRank = Js.Coalesce(rules?.Obj(RW.Eligibility)?.Obj(RW.CombatCards)?[RW.MinRank], 0);
            var include = d.Module(run, MI.Resources)?[RW.Include];
            return new Pools
            {
                Weapons = equipment ? d.RunData.EquipmentRows(DK.Armaments).Where(row => weapons.Contains(row.Str(DK.Id)) && !carried.Contains(row.Str(DK.Id))).Select(row => row.Str(DK.Id)).ToList() : new List<string>(),
                Relics = d.RunData.Relics.All.Where(row => !Js.Includes(run[K.Relics], row.Str(DK.Id))).Select(row => row.Str(DK.Id)).ToList(),
                CombatCards = hasCards ? cards.Select(row => row.Str(DK.Id)).ToList() : new List<string>(),
                // Rank split for combat-card offers: a card's own rank, else its rarity's.
                CombatCardsLow = hasCards ? cards.Where(row => CardRank(row, rules) < minRank).Select(row => row.Str(DK.Id)).ToList() : new List<string>(),
                WeaponArts = hasCards && equipment
                    ? authoredArts.Select(Js.Str).Distinct().Where(id => DeckRules.RangeCardAvailable(d.Deck, run, d.RunData.Cards.Get(id))).ToList()
                    : new List<string>(),
                Potions = d.RunData.Combat.Flasks.All.Where(row => Js.Includes(include, RV.ManaPool) || !Js.Items(row[DK.Effects]).Any(e => Js.Str(Js.Get(e, DK.Op)) == RV.OpRestoreMana))
                    .Select(row => row.Str(DK.Id)).ToList(),
                Armor = equipment
                    ? d.RunData.EquipmentRows(DK.Armour).Where(row => armor.Contains(row.Str(RW.ClassId) + RV.ArmourKeySeparator + row.Str(DK.Id)) && row.Str(RW.ClassId) == run.Str(DK.Class)
                        && !Js.Items(run.Obj(DK.Loadout)?[RW.BoughtArmour]).Any(o => Js.Str(Js.Get(o, DK.Id)) == row.Str(DK.Id)) && !owned.Contains(row.Str(DK.Id)))
                        .Select(row => row.Str(DK.Id)).ToList()
                    : new List<string>(),
            };
        }

        // ------------------------------------------------------------------ offers

        private static JToken Offers(JObject run) => run[RW.GameProfileRewardOffers];

        private static JObject OfferOf(JObject run, string offerId) => (Offers(run) as JObject)?[offerId ?? string.Empty] as JObject;

        /// <summary>
        /// makeOffer: the stored offer under <paramref name="offerId"/>, or a new one drawn now. A shop (<paramref name="itemsPerCategory"/> set, WP-17) fills each shelf
        /// on its own <c>shop</c> stream, the categories in their authored order, no stance reserve and no weighted group; every other offer draws on <c>cardRewards</c>.
        /// </summary>
        internal static JObject MakeOffer(Core2RewardsData d, JObject run, Rng rng, string offerId, string source, double count, double takeCount, IReadOnlyList<string> categories, JToken groups, int? itemsPerCategory = null)
        {
            var stream = source == RV.SourceShop && itemsPerCategory != null ? RngStream.Shop : RngStream.CardRewards;
            var shelfCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(offerId)) throw new InvalidOperationException(RM.OfferIdRequired);
            if (OfferOf(run, offerId) is JObject stored) return stored;
            var available = PoolsOf(d, run);
            var options = new JArray();
            var list = groups as JArray ?? new JArray();
            if (list.Count > 0)
            {
                var problems = Core2RewardPolicy.GroupProblems(groups, categories);
                if (problems.Count > 0) throw new InvalidOperationException(string.Join(RM.ProblemSeparator, problems));
            }
            // Owner 2026-10-19: reserve slot(s) for a basic or cantrip card of a stance the character lacks.
            var combatCardsOffered = list.Count == 0 || list.OfType<JObject>().Any(g => Js.D(g[RW.Weight]) > 0 && Js.Includes(g[RW.Categories], RV.KindCombatCards));
            if (source != RV.SourceShop && categories.Contains(RV.KindCombatCards) && combatCardsOffered)
                foreach (var id in Core2StanceCoverage.Reserve(d, run, rng, available.CombatCards, count - options.Count))
                {
                    options.Add(Option(RV.KindCombatCards, id));
                    available.CombatCards = available.CombatCards.Where(value => value != id).ToList();
                }
            // Ordinary combat draws weighted groups.
            var rule = (d.Content(run, RW.Rewards) as JObject)?.Obj(RW.Eligibility)?.Obj(RW.CombatCards);
            while (options.Count < count)
            {
                var kinds = categories.Where(kind => (available.Of(kind)?.Count ?? 0) > 0 && (itemsPerCategory == null || (shelfCounts.TryGetValue(kind, out var shelf) ? shelf : 0) < itemsPerCategory)).ToList();
                if (kinds.Count == 0) break;
                var kind = itemsPerCategory == null ? Core2RewardPolicy.PickCategory(rng, kinds, groups) : kinds[0];
                if (kind == null) break;
                var ids = available.Of(kind);
                // Most card rewards are rank 1+; a small authored chance lets a lower-rank card through.
                if (kind == RV.KindCombatCards && rule != null)
                {
                    var low = new HashSet<string>(available.CombatCardsLow, StringComparer.Ordinal);
                    var high = ids.Where(id => !low.Contains(id)).ToList();
                    var lowIds = ids.Where(low.Contains).ToList();
                    // The chance is honored even when no rank 1+ card is left: a failed (or zero) roll drops the category.
                    var scale = (int)d.Rules.Num(RW.LowerRankScale);
                    var allowLow = lowIds.Count > 0 && rng.Pick(stream, Enumerable.Range(0, scale).ToList()) < RunJs.Round(Js.Coalesce(rule[RW.LowerRankChance], 0) * scale);
                    if (!allowLow && high.Count == 0)
                    {
                        available.Set(kind, new List<string>());
                        continue;
                    }
                    ids = allowLow ? lowIds : high;
                }
                var chosen = rng.Pick(stream, ids);
                available.Set(kind, available.Of(kind).Where(value => value != chosen).ToList());
                options.Add(Option(kind, chosen));
                shelfCounts[kind] = (shelfCounts.TryGetValue(kind, out var had) ? had : 0) + 1;
            }
            var offer = new JObject
            {
                [RW.Id] = offerId, [RW.Source] = source, [RW.Options] = options, [RW.TakeCount] = Js.N(takeCount), [RW.Taken] = new JArray(), [RW.Claimed] = Js.Null(),
            };
            if (!(Offers(run) is JObject all)) run[RW.GameProfileRewardOffers] = all = new JObject();
            all[offerId] = offer;
            return offer;
        }

        private static bool TakenHas(JObject offer, int index) => Js.Items(offer[RW.Taken]).Any(t => Js.IsNum(t) && Js.D(t) == index);

        private static JObject Option(string kind, string id) => new JObject { [RW.Kind] = kind, [RW.Id] = id };

        /// <summary>
        /// gameProfileRewardOffers(registries, run, rng, { offerId, source }): the stored offer under <paramref name="offerId"/>, or a new one drawn now.
        /// A combat offer draws the profile's reward groups over every category; a level offer is cards only. The returned object is the one on the run.
        /// </summary>
        public static JObject Offer(Core2RewardsData d, JObject run, Rng rng, string offerId, string source)
        {
            var module = d.Module(run, MI.Rewards);
            if (module?.Str(RW.Mode) != Ashen.Generated.Core2RunValues.Configured) throw new InvalidOperationException(RM.RewardsUnavailable);
            var rules = d.Content(run, RW.Rewards) as JObject;
            var categories = source == RV.SourceLevel ? new List<string> { RV.KindCombatCards } : Js.Items(rules?[RW.Categories]).Select(Js.Str).ToList();
            var groups = source == RV.SourceCombat ? (module[RW.RewardGroups] ?? rules?[RW.RewardGroups]) : null;
            return MakeOffer(d, run, rng, offerId, source, Js.D(Js.Nullish(module[RW.OfferCount]) ? rules[RW.OfferCount] : module[RW.OfferCount]),
                Js.D(Js.Nullish(module[RW.TakeCount]) ? rules[RW.TakeCount] : module[RW.TakeCount]), categories, groups);
        }

        // ------------------------------------------------------------------ claims

        /// <summary>The instance id a claimed card gets in the sideboard.</summary>
        public static string InstanceId(string offerId, int index) => RV.RewardInstancePrefix + offerId + RV.IdSeparator + index.ToString(CultureInfo.InvariantCulture);

        // The web claims on a draft (a structuredClone of the run) and assigns it back on success. The run pins its whole profile (megabytes, never edited), so a
        // claim here works on the run itself and keeps a copy of every other top-level field, which it puts back when the claim is refused: the same all-or-nothing.
        private static Dictionary<string, JToken> Save(JObject run) =>
            run.Properties().Where(p => p.Name != Ashen.Generated.Core2RunSnapshotKeys.GameProfile).ToDictionary(p => p.Name, p => p.Value.DeepClone());

        private static void Restore(JObject run, Dictionary<string, JToken> saved)
        {
            foreach (var name in run.Properties().Select(p => p.Name).Where(n => n != Ashen.Generated.Core2RunSnapshotKeys.GameProfile && !saved.ContainsKey(n)).ToList()) run.Remove(name);
            foreach (var field in saved) run[field.Key] = field.Value;
        }

        /// <summary>flaskAcquisitionPlan: whether a flask can be picked up, and how it lands (a charge-pool capacity point, or a slot).</summary>
        private sealed class FlaskPlan
        {
            public bool Ok;
            public bool Capacity;
            public string ChargeKind;
            public double Amount;
            public string Reason;
        }

        private static FlaskPlan PlanFlask(Core2RewardsData d, JObject run, string flaskId)
        {
            d.RunData.Combat.Flasks.Get(flaskId);
            var amount = Js.Or0(d.Module(run, MI.Resources)?[RW.ChargeFlaskCapacityPerPickup]);
            var chargeKind = amount > 0 ? RewardRolls.ChargeKindForFlask(d.Rewards, flaskId) : null;
            if (chargeKind != null)
            {
                var charges = run.Obj(K.FlaskCharges);
                var keys = Js.Items(d.Rules[RW.ChargeLedgerKeys]).Select(Js.Str).ToList();
                var kinds = Js.Items(d.RunData.Combat.Engine.Obj(K.Flasks)?[K.ChargeKinds]).Select(Js.Str).ToList();
                var valid = charges != null && Js.IsSafeInteger(Js.N(amount)) && amount > 0 && charges.Num(RW.Base) > 0 && charges.Num(RW.Capacity) > 0
                    && keys.All(key => Js.IsSafeInteger(charges[key]) && charges.Num(key) >= 0)
                    && kinds.All(kind => Js.IsSafeInteger(charges.Obj(RW.Grown)?[kind]) && charges.Obj(RW.Grown).Num(kind) >= 0 && charges.Num(kind + RV.CurrentSuffix) <= charges.Num(kind))
                    && charges.Num(RW.Hp) + charges.Num(RW.Mana) == charges.Num(RW.Capacity)
                    && charges.Num(RW.Capacity) == charges.Num(RW.Base) + charges.Obj(RW.Grown).Num(RW.Hp) + charges.Obj(RW.Grown).Num(RW.Mana) + charges.Num(RW.Granted)
                    && Js.IsSafeInteger(Js.N(charges.Num(RW.Capacity) + amount));
                return new FlaskPlan { Ok = valid, Capacity = true, ChargeKind = chargeKind, Amount = amount, Reason = valid ? null : RM.InvalidCapacityLedger };
            }
            var cap = d.RunData.Balance[RW.FlaskSlots];
            if (!Js.IsInt(cap) || Js.D(cap) <= 0) throw new InvalidOperationException(RM.FlaskSlotsNotPositive);
            var ok = run[K.Flasks] is JArray flasks && flasks.Count < Js.D(cap);
            return new FlaskPlan { Ok = ok, Amount = 1, Reason = ok ? null : RM.PotionSlotsFull };
        }

        /// <summary>flaskAcquisitionPlan(...).ok: a flask can be picked up (a free slot, or a valid charge ledger for a capacity flask).</summary>
        public static bool CanAcquireFlask(Core2RewardsData d, JObject run, string flaskId) => PlanFlask(d, run, flaskId).Ok;

        private static FlaskPlan AcquireFlask(Core2RewardsData d, JObject run, string flaskId)
        {
            var plan = PlanFlask(d, run, flaskId);
            if (!plan.Ok) return plan;
            if (plan.Capacity)
            {
                var charges = run.Obj(K.FlaskCharges);
                charges.Put(RW.Capacity, charges.Num(RW.Capacity) + plan.Amount);
                charges.Put(plan.ChargeKind, charges.Num(plan.ChargeKind) + plan.Amount);
                charges.Put(plan.ChargeKind + RV.CurrentSuffix, charges.Num(plan.ChargeKind + RV.CurrentSuffix) + plan.Amount);
                charges.Put(RW.Granted, charges.Num(RW.Granted) + plan.Amount);
            }
            else run.Arr(K.Flasks).Add(new JObject { [RW.FlaskId] = flaskId });
            return plan;
        }

        /// <summary>
        /// claimGameProfileReward(registries, run, offerId, index): takes the option at <paramref name="index"/> (a card lands in the sideboard as
        /// <c>reward:&lt;offer&gt;:&lt;index&gt;</c>; a weapon in storage; a relic; a potion; a set of armour) and returns it. Throws the web's refusals, leaving the run untouched.
        /// </summary>
        public static JObject Claim(Core2RewardsData d, JObject run, string offerId, int index)
        {
            var offer = OfferOf(run, offerId);
            var claimed = offer?[RW.Claimed];
            if (offer == null || !(claimed != null && claimed.Type == JTokenType.Null) || TakenHas(offer, index) || Js.D(offer[RW.TakeCount]) < 1)
                throw new InvalidOperationException(RM.OfferClaimed);
            var options = offer.Arr(RW.Options);
            var option = index >= 0 && index < options.Count ? options[index] as JObject : null;
            if (option == null) throw new InvalidOperationException(RM.InvalidOption);
            var kind = option.Str(RW.Kind);
            if (!(PoolsOf(d, run).Of(kind)?.Contains(option.Str(RW.Id)) ?? false)) throw new InvalidOperationException(RM.OptionUnavailable);
            var id = option.Str(RW.Id);
            var saved = Save(run);
            try { Apply(d, run, offerId, index, option, kind, id); }
            catch
            {
                Restore(run, saved);
                throw;
            }
            return (JObject)option.DeepClone();
        }

        private static void Apply(Core2RewardsData d, JObject draft, string offerId, int index, JObject option, string kind, string id)
        {
            var loadout = draft.Obj(DK.Loadout);
            switch (kind)
            {
                case RV.KindCombatCards:
                case RV.KindWeaponArts:
                    d.RunData.Cards.Get(id);
                    draft.Arr(DK.Sideboard).Add(new JObject { [DK.InstanceId] = InstanceId(offerId, index), [DK.CardId] = id, [DK.Upgraded] = false });
                    break;
                case RV.KindWeapons:
                    if (!d.RunData.EquipmentRows(DK.Armaments).Any(row => row.Str(DK.Id) == id)) throw new InvalidOperationException(RM.UnknownWeapon);
                    if (Js.Includes(loadout[RW.Storage], id)) throw new InvalidOperationException(RM.WeaponOwned);
                    loadout.Arr(RW.Storage).Add(id);
                    break;
                case RV.KindRelics:
                    d.RunData.Relics.Get(id);
                    if (Js.Includes(draft[K.Relics], id)) throw new InvalidOperationException(RM.RelicOwned);
                    draft.Arr(K.Relics).Add(id);
                    break;
                case RV.KindPotions:
                    var acquisition = AcquireFlask(d, draft, id);
                    if (!acquisition.Ok) throw new InvalidOperationException(acquisition.Reason);
                    break;
                case RV.KindArmor:
                    if (!d.RunData.EquipmentRows(DK.Armour).Any(row => row.Str(DK.Id) == id && row.Str(RW.ClassId) == draft.Str(DK.Class))) throw new InvalidOperationException(RM.UnknownArmor);
                    if (!(loadout[RW.BoughtArmour] is JArray)) loadout[RW.BoughtArmour] = new JArray();
                    loadout.Arr(RW.BoughtArmour).Add(new JObject { [RW.ClassId] = draft.Str(DK.Class), [DK.Id] = id });
                    break;
                default:
                    throw new InvalidOperationException(RM.UnknownCategory);
            }
            var pending = OfferOf(draft, offerId);
            pending.Arr(RW.Taken).Add(index);
            if (pending.Arr(RW.Taken).Count >= Math.Min(Js.D(pending[RW.TakeCount]), pending.Arr(RW.Options).Count)) pending[RW.Claimed] = index;
            RunState.SyncZones(d.RunData, draft);
        }

        // ------------------------------------------------------------------ add to deck

        private static JObject AddToDeckDraft(Core2RewardsData d, JObject draft, string offerId, int index, JObject settings, out bool changed)
        {
            var offer = OfferOf(draft, offerId);
            var options = offer?.Arr(RW.Options);
            var option = options != null && index >= 0 && index < options.Count ? options[index] as JObject : null;
            if (option == null || (option.Str(RW.Kind) != RV.KindCombatCards && option.Str(RW.Kind) != RV.KindWeaponArts))
                throw new DeckAddRefusalException(RV.CodeNotCard, RM.OnlyCards);
            var instanceId = InstanceId(offerId, index);
            changed = false;
            if (Js.Items(draft[DK.Deck]).Any(card => Js.Str(Js.Get(card, DK.InstanceId)) == instanceId)) return option;
            if (!TakenHas(offer, index)) Claim(d, draft, offerId, index);
            var max = DeckRules.EditBounds(d.Deck, settings).Max;
            if (draft.Arr(DK.Deck).Count + 1 > max) throw new DeckAddRefusalException(RV.CodeFull, string.Format(CultureInfo.InvariantCulture, RM.DeckFull, RunJs.NumStr(max)));
            if (!DeckRules.MoveFromSideboard(d.Deck, draft, instanceId, settings)) throw new DeckAddRefusalException(RV.CodeLimit, RM.CannotJoin);
            RunState.SyncZones(d.RunData, draft);
            changed = true;
            return option;
        }

        /// <summary>
        /// rewardDeckAddRefusal: empty when "Add to deck" is legal for the offered card, else the web's code (<c>notCard</c>, <c>full</c>, <c>limit</c>).
        /// Any other refusal (an offer already claimed by another option, a missing offer) is thrown as it is.
        /// </summary>
        public static string DeckAddRefusal(Core2RewardsData d, JObject run, string offerId, int index, JObject settings = null)
        {
            var saved = Save(run);
            try
            {
                AddToDeckDraft(d, run, offerId, index, settings, out _);
                return string.Empty;
            }
            catch (DeckAddRefusalException e) { return e.Code; }
            finally { Restore(run, saved); }
        }

        /// <summary>
        /// claimGameProfileRewardToDeck: claims the card (when not yet claimed) and adds it to the deck; a repeat call changes nothing. Both steps run on a
        /// draft, so a refusal leaves the run and the offer untouched. Returns the option.
        /// </summary>
        public static JObject ClaimToDeck(Core2RewardsData d, JObject run, string offerId, int index, JObject settings = null)
        {
            var saved = Save(run);
            try { return (JObject)AddToDeckDraft(d, run, offerId, index, settings, out _).DeepClone(); }
            catch
            {
                Restore(run, saved);
                throw;
            }
        }
    }
}
