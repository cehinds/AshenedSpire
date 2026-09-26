using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using RngStream = Ashen.Generated.RngStream;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using WK = Ashen.Generated.RewardsKeys;
using SK = Ashen.Generated.ShopKeys;
using SM = Ashen.Generated.ShopMessages;
using CombatMath = Ashen.Generated.CombatMath;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// The smith's service table and who offers it (shipped model/smithingRules.js normalizeSmithingRules and
    /// normalizeServices, model/cardExtraction.js smithServiceRules and smithServicesAt): which node kinds offer which
    /// services on a visit (a chance of 100 is a promise and draws nothing; anything between 0 and 100 is one draw on the
    /// 'smith' stream) and what the priced services cost in Smithing Stones. Malformed authoring is refused by name.
    /// </summary>
    public static class SmithServices
    {
        private static JObject OwnObject(JToken value, string label) =>
            value as JObject ?? throw new InvalidOperationException(RunJs.Fmt(SM.MustBeObject, label));

        internal static double Integer(JToken value, string label, double minimum = 0)
        {
            if (!Js.IsInt(value) || Js.D(value) < minimum) throw new InvalidOperationException(RunJs.Fmt(SM.MustBeIntegerAtLeast, label, RunJs.NumStr(minimum)));
            return Js.D(value);
        }

        private static string Label(params string[] parts) => string.Join(RV.PathDot, parts);

        /// <summary>normalizeServices(raw): the validated service table; the inert table when none is authored.</summary>
        private static JObject NormalizeServices(ShopData d, JToken raw)
        {
            if (Js.Nullish(raw)) return (JObject)d.RuleObj(SK.Smith, SK.InertServices).DeepClone();
            var root = Label(WK.Smithing, SK.Services);
            var source = OwnObject(raw, root);
            var offeredRaw = OwnObject(source[SK.OfferedAt], Label(root, SK.OfferedAt));
            var known = d.RuleList(SK.Smith, SK.Services);
            var offeredAt = new JObject();
            foreach (var p in offeredRaw.Properties())
            {
                var label = Label(root, SK.OfferedAt, p.Name);
                if (p.Name.Length == 0) throw new InvalidOperationException(SM.EmptyNodeKind);
                var spec = OwnObject(p.Value, label);
                var chance = Integer(spec[WK.Chance], Label(label, WK.Chance));
                if (chance > CombatMath.Percent) throw new InvalidOperationException(RunJs.Fmt(SM.ChanceOutOfRange, label));
                if (!(spec[SK.Services] is JArray services) || services.Count == 0) throw new InvalidOperationException(RunJs.Fmt(SM.ServicesNotListed, label));
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var service in services)
                {
                    var name = Js.Str(service);
                    if (name == null || !known.Contains(name)) throw new InvalidOperationException(RunJs.Fmt(SM.UnknownService, label, RunJs.Key(service), string.Join(RV.ListJoiner, known)));
                    if (!seen.Add(name)) throw new InvalidOperationException(RunJs.Fmt(SM.ServiceTwice, label, name));
                }
                offeredAt[p.Name] = Js.Obj(WK.Chance, chance, SK.Services, services.DeepClone());
            }
            var result = Js.Obj(SK.OfferedAt, offeredAt);
            foreach (var service in d.RuleList(SK.Smith, SK.PricedServices))
            {
                var row = OwnObject(source[service], Label(root, service));
                result[service] = Js.Obj(K.Cost, Integer(row[K.Cost], Label(root, service, K.Cost)));
            }
            var keys = d.RuleList(SK.Smith, SK.ServiceKeys);
            foreach (var p in source.Properties())
                if (!keys.Contains(p.Name)) throw new InvalidOperationException(RunJs.Fmt(SM.UnknownServicesKey, Label(root, p.Name)));
            return result;
        }

        /// <summary>
        /// normalizeSmithingRules(balance.smithing).services (smithServiceRules): the reward faucet is validated first,
        /// as the shipped normalizer does, then the service table.
        /// </summary>
        public static JObject Rules(ShopData d)
        {
            Rewards.Smithing.RewardByPool(d.Rewards);
            return NormalizeServices(d, d.Balance.Obj(WK.Smithing)[SK.Services]);
        }

        /// <summary>
        /// smithServicesAt(registries, nodeKind, rng) → { nodeKind, offered, rolled, chance, services }: the services a
        /// node of this kind offers on this visit. A node kind the table does not name offers nothing.
        /// </summary>
        public static JObject At(ShopData d, string nodeKind, Rng rng)
        {
            var rules = Rules(d);
            var row = nodeKind != null ? rules.Obj(SK.OfferedAt).Obj(nodeKind) : null;
            if (row == null) return Js.Obj(SK.NodeKind, nodeKind, SK.Offered, false, SK.Rolled, false, WK.Chance, 0.0, SK.Services, new JArray());
            var chance = row.Num(WK.Chance);
            var rolled = chance > 0 && chance < CombatMath.Percent;
            var offered = chance >= CombatMath.Percent || (!(chance <= 0) && rng.Float(RngStream.Smith) * CombatMath.Percent < chance);
            return Js.Obj(SK.NodeKind, nodeKind, SK.Offered, offered, SK.Rolled, rolled, WK.Chance, chance, SK.Services, offered ? row[SK.Services].DeepClone() : new JArray());
        }
    }
}
