using System;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using SK = Ashen.Generated.ShopKeys;
using SV = Ashen.Generated.ShopValues;
using SS = Ashen.Generated.ShopStringKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// Buy-back (US-9.3; D-159, not in the shipped game): what is sold at a merchant can be bought back at the price
    /// received, while the visit lasts. The list is the caller's (the session keeps it beside the run, so the shipped run
    /// document and the shop's parity are untouched); an entry is { kind, id, price }. An armament returns to storage
    /// (its tier and mounts were kept on the run at the sale) and the deck is restamped; a relic returns with its flask
    /// growth; a flask needs a free belt slot.
    /// </summary>
    public static class BuyBack
    {
        /// <summary>The entry a landed sale leaves (null for any other action or receipt).</summary>
        public static JObject EntryFor(ShopAction action, JObject receipt)
        {
            if (action == null || receipt == null) return null;
            string kind;
            switch (action.Kind)
            {
                case SV.ActionSellArmament: kind = SV.KindArmament; break;
                case SV.ActionSellRelic: kind = SV.KindRelic; break;
                case SV.ActionSellFlask: kind = SV.KindFlask; break;
                default: return null;
            }
            var id = Js.Str(receipt[K.Id]);
            return id == null ? null : Js.Obj(K.Kind, kind, K.Id, id, SK.Price, Js.Or0(receipt[SK.Received]));
        }

        /// <summary>Why the entry cannot be bought back now (the flask belt is full; the purse is short), or null.</summary>
        public static string Refusal(ShopData d, JObject run, JObject entry) =>
            Shop.Availability(entry.Num(SK.Price), run.Num(RK.Cinders), null,
                entry.Str(K.Kind) == SV.KindFlask && !(run.Arr(K.Flasks).Count < Shop.FlaskSlotCap(d)));

        /// <summary>Buys the entry back: the piece returns, the price is paid. Refused (nothing changes) as <see cref="Refusal"/> says.</summary>
        public static ShopResult Commit(ShopData d, JObject run, JObject entry)
        {
            if (entry == null) return ShopResult.Refuse(SS.ShopRefusalOfferGone);
            var refusal = Refusal(d, run, entry);
            if (refusal != null) return ShopResult.Refuse(refusal);
            var id = entry.Str(K.Id);
            switch (entry.Str(K.Kind))
            {
                case SV.KindArmament:
                {
                    var loadout = run.Obj(K.Loadout);
                    loadout[RK.Storage] = new JArray(Js.Items(loadout[RK.Storage]).Select(x => x.DeepClone())) { id };
                    StartingDeck.StampDeck(d.Run, run);
                    break;
                }
                case SV.KindRelic:
                    run.Arr(K.Relics).Add(id);
                    Creation.SyncFlaskGrowth(d.Run, run);
                    break;
                case SV.KindFlask:
                    run.Arr(K.Flasks).Add(Js.Obj(K.FlaskId, id));
                    break;
                default:
                    return ShopResult.Refuse(SS.ShopRefusalOfferGone);
            }
            run.Put(RK.Cinders, run.Num(RK.Cinders) - entry.Num(SK.Price));
            return ShopResult.Accept(Js.Obj(K.Kind, entry[K.Kind], K.Id, id, SK.Spent, entry[SK.Price]));
        }
    }
}
