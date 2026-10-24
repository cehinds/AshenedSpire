using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Newtonsoft.Json.Linq;
using RngStream = Ashen.Generated.RngStream;
using CK = Ashen.Generated.Core2CombatKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using RV = Ashen.Generated.Core2RewardsValues;
using RW = Ashen.Generated.Core2RewardsKeys;

namespace Ashen.Domain.Rewards
{
    /// <summary>
    /// Card rewards cover the basic stances (web model/stanceCoverage.js, owner 2026-10-19): while the character owns no card of some stance (deck and
    /// collection), an offer reserves a slot for a basic or cantrip card of one. The rules live in the reward-ranks module's <c>stanceCoverage</c>
    /// setting, which the profile lays into the rewards content; nothing here is a constant.
    /// </summary>
    public static class Core2StanceCoverage
    {
        /// <summary>The enabled stanceCoverage rule of a run's profile, or null.</summary>
        public static JObject Rule(Core2RewardsData d, JObject run)
        {
            var rule = (d.Content(run, RW.Rewards) as JObject)?.Obj(RW.Eligibility)?.Obj(RW.CombatCards)?.Obj(RW.StanceCoverage);
            return rule != null && Js.Truthy(rule[RW.Enabled]) && d.Module(run, MI.Stances) != null && d.Module(run, MI.Cards) != null && Js.D(rule[RW.ReservedSlots]) > 0 ? rule : null;
        }

        private static List<string> StanceIds(Core2RewardsData d, JObject run)
        {
            var alias = d.Section(RW.StanceAlias);
            var ids = new List<string>();
            foreach (var id in Js.Items(d.Module(run, MI.Stances)?[RW.Include]).Select(Js.Str))
            {
                var mapped = Js.Truthy(alias[id ?? string.Empty]) ? alias.Str(id) : id;
                if (!ids.Contains(mapped)) ids.Add(mapped);
            }
            return ids;
        }

        /// <summary>The stance of a card id (its maneuver), or null when the card has none or is unknown.</summary>
        private static string StanceOfId(Core2RewardsData d, CombatState reader, string id)
        {
            try { return CoreStances.StanceOf(reader, d.RunData.Cards.Get(id))?.Str(CK.Maneuver); }
            catch (InvalidOperationException) { return null; }
        }

        internal static CombatState Reader(Core2RewardsData d) => new CombatState { CoreData = d.Combat, Data = d.Combat.Combat };

        private static string OwnedId(JToken card) => Js.IsStr(card) ? Js.Str(card) : Js.Str(Js.Get(card, RW.CardId)) ?? Js.Str(Js.Get(card, RW.Id));

        /// <summary>Stances of the profile with no owned card (deck and sideboard), in the module's authored order.</summary>
        public static List<string> MissingBasicStances(Core2RewardsData d, JObject run)
        {
            var reader = Reader(d);
            var owned = new HashSet<string>(Js.Items(run[RW.Deck]).Concat(Js.Items(run[RW.Sideboard])).Select(card => StanceOfId(d, reader, OwnedId(card))).Where(s => s != null), StringComparer.Ordinal);
            return StanceIds(d, run).Where(stance => !owned.Contains(stance)).ToList();
        }

        private static Func<string, bool> TierMembers(Core2RewardsData d, JObject run, JObject rule)
        {
            var basic = new HashSet<string>(StringComparer.Ordinal);
            if (d.Content(run, RW.CardPools) is JObject pools)
                foreach (var pool in pools.Properties())
                {
                    if (Js.Includes(rule[RW.ExcludePools], pool.Name)) continue;
                    var row = pool.Value as JObject;
                    foreach (var id in (row?.Obj(RW.ByTag)?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => Js.Str(p.Value))) if (id != null) basic.Add(id);
                    foreach (var byTag in (row?.Obj(RW.ByClass)?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => p.Value as JObject))
                        foreach (var id in (byTag?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => Js.Str(p.Value))) if (id != null) basic.Add(id);
                }
            var preferBasic = Js.Includes(rule[RW.PreferTiers], RV.TierBasic);
            var preferCantrip = Js.Includes(rule[RW.PreferTiers], RV.TierCantrip);
            var cantripTag = d.Rules.Str(RW.CantripTag);
            bool IsCantrip(string id)
            {
                var card = d.RunData.Cards.Get(id);
                var tags = Js.Truthy(card[RW.CardTags]) ? card[RW.CardTags] : card[RW.Tags];
                return Js.Items(tags).Any(tag => (Js.IsStr(tag) ? Js.Str(tag) : Js.Str(Js.Get(tag, RW.Id))) == cantripTag);
            }
            return id => (preferBasic && basic.Contains(id)) || (preferCantrip && IsCantrip(id));
        }

        /// <summary>
        /// reserveStanceCards(registries, run, rng, stream, eligibleIds, limit): up to min(reservedSlots, limit) card ids for the offer, each a basic or
        /// cantrip card (from <paramref name="eligibleIds"/>) of a stance the character lacks, distinct stances, seeded draws on <c>cardRewards</c>.
        /// Draws nothing when no candidate exists.
        /// </summary>
        public static List<string> Reserve(Core2RewardsData d, JObject run, Rng rng, IReadOnlyList<string> eligibleIds, double limit)
        {
            var rule = Rule(d, run);
            var slots = Math.Min(rule != null ? Js.D(rule[RW.ReservedSlots]) : 0, limit);
            var picked = new List<string>();
            if (rule == null || !(slots > 0)) return picked;
            var inTier = TierMembers(d, run, rule);
            var reader = Reader(d);
            var stances = new List<string>();
            var byStance = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var stance in MissingBasicStances(d, run))
            {
                var ids = eligibleIds.Where(id => inTier(id) && StanceOfId(d, reader, id) == stance).ToList();
                if (ids.Count == 0) continue;
                if (!byStance.ContainsKey(stance)) stances.Add(stance);
                byStance[stance] = ids;
            }
            while (picked.Count < slots && stances.Count > 0)
            {
                var stance = rng.Pick(RngStream.CardRewards, stances);
                picked.Add(rng.Pick(RngStream.CardRewards, byStance[stance]));
                stances.Remove(stance);
                byStance.Remove(stance);
            }
            return picked;
        }
    }
}
