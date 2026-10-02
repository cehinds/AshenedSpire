using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using SK = Ashen.Generated.ShopKeys;
using SV = Ashen.Generated.ShopValues;
using SS = Ashen.Generated.ShopStringKeys;
using CombatMath = Ashen.Generated.CombatMath;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// An inert purchase quote (shipped armamentPurchasePlan's result): whether the offer can be bought now and why not,
    /// its price and the shop's trade revision. A commit revalidates it; a quote whose cost or revision no longer
    /// matches is refused as changed.
    /// </summary>
    public sealed class PurchasePlan
    {
        public bool Ok;
        public string Reason;
        public JObject Item;
        public JObject Def;
        public string Kind;
        public string Shelf;
        public double Cost;
        public double Revision;
    }

    /// <summary>An inert sale quote (shipped armamentSalePlan's result): the price, the smithing tier and mounts kept with the item.</summary>
    public sealed class SalePlan
    {
        public bool Ok;
        public string Reason;
        public string Id;
        public JObject Def;
        public string ItemRef;
        public JToken Tier;
        public List<JObject> Mounts;
        public double Price;
        public bool Equipped;
        public string Signature;
        public double Revision;
    }

    /// <summary>
    /// Merchant armament and weapon-art trades (shipped model/armamentTrading.js): plans are inert; commits revalidate
    /// through the same plan before they mutate the run, and every trade bumps the shop's trade revision.
    /// </summary>
    public static class ArmamentTrading
    {
        private static readonly double MaxSafeInteger = Math.Pow(CombatMath.Two, CombatMath.SafeIntegerBits) - 1;

        /// <summary><c>Number.isSafeInteger(v)</c>.</summary>
        internal static bool SafeInteger(JToken v) => Js.IsInt(v) && Math.Abs(Js.D(v)) <= MaxSafeInteger;

        private static bool SafeInteger(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Floor(v) == v && Math.Abs(v) <= MaxSafeInteger;

        private static bool Priced(JToken cost) => SafeInteger(cost) && Js.D(cost) > 0;

        private static double Revision(JObject run) => run.Obj(SK.ShopStock)?.Or0(SK.TradeRevision) ?? 0;

        /// <summary>eligibleWeaponArts(registries): every package's default weapon art whose card carries the extractable tag.</summary>
        public static List<string> EligibleWeaponArts(ShopData d)
        {
            var tag = d.Run.EquipmentBalance.Obj(RK.CardMounts)?.Str(RK.ExtractableTag);
            if (string.IsNullOrEmpty(tag)) tag = d.RuleStr(SK.Stock, SK.DefaultExtractableTag);
            var ids = new List<string>();
            foreach (var piece in d.Run.EquipmentRows(K.Armaments))
                foreach (var id in RunJs.Strs(piece.Obj(K.WeaponCardPackage)?[RK.WeaponArtDefaults]))
                    if (!ids.Contains(id)) ids.Add(id);
            return ids.Where(id => Js.Includes(d.Run.Cards.Get(id)[K.Tags], tag)).ToList();
        }

        private static string ShelfOf(string kind) => kind == SV.KindArmament ? K.Armaments : SK.WeaponArts;

        /// <summary>
        /// armamentPurchasePlan(registries, run, item, kind): the offer (an item on the shelf, by reference) checked
        /// against the shelf, its price, the weapon-art rule, what the run carries, the inventory cap and the purse.
        /// </summary>
        public static PurchasePlan PurchasePlanFor(ShopData d, JObject run, JObject item, string kind)
        {
            var shelf = ShelfOf(kind);
            var def = kind == SV.KindArmament ? d.Armament(item?.Str(K.Id)) : d.Run.Cards.Get(item?.Str(K.Id));
            var shelfItems = run.Obj(SK.ShopStock)?.Arr(shelf);
            string reason = null;
            if (item == null || shelfItems == null || !shelfItems.Any(x => ReferenceEquals(x, item))) reason = SS.ShopRefusalOfferGone;
            else if (def == null || !Priced(item[K.Cost])) reason = SS.ShopRefusalNoPrice;
            else if (kind != SV.KindArmament && !EligibleWeaponArts(d).Contains(item.Str(K.Id))) reason = SS.ShopRefusalNotWeaponArt;
            else if (kind == SV.KindArmament && RewardRolls.CarriedIds(run.Obj(K.Loadout)).Contains(item.Str(K.Id))) reason = SS.ShopRefusalAlreadyCarried;
            else if (kind == SV.KindArmament && !Js.Truthy(run[K.Loadout])) reason = SS.ShopRefusalInventoryUnavailable;
            else if (kind == SV.KindArmament && Js.Items(run.Obj(K.Loadout)[RK.Storage]).Count() >= StorageSlots(d)) reason = SS.ShopRefusalInventoryFull;
            else if (!SafeInteger(run[RK.Cinders]) || run.Num(RK.Cinders) < item.Num(K.Cost)) reason = SS.ShopRefusalCinders;
            return new PurchasePlan { Ok = reason == null, Reason = reason, Item = item, Def = def, Kind = kind, Shelf = shelf, Cost = item != null ? item.Num(K.Cost) : double.NaN, Revision = Revision(run) };
        }

        private static double StorageSlots(ShopData d)
        {
            var slots = d.Run.EquipmentBalance[RK.StorageSlots];
            return Js.Nullish(slots) ? d.RuleNum(SK.Stock, SK.DefaultStorageSlots) : Js.D(slots);
        }

        /// <summary>
        /// commitArmamentPurchase(registries, run, quote) → { kind, id, spent, instance }: revalidates the quote, then
        /// stores the armament (or adds the weapon art as a loose deck card), pays, takes it off the shelf and bumps
        /// the revision.
        /// </summary>
        public static JObject CommitPurchase(ShopData d, JObject run, PurchasePlan quote)
        {
            var plan = PurchasePlanFor(d, run, quote.Item, quote.Kind);
            if (!plan.Ok) throw new RefusalException(plan.Reason);
            if (quote.Cost != plan.Cost || quote.Revision != plan.Revision) throw new RefusalException(SS.ShopRefusalOfferChanged);
            JObject instance = null;
            var id = plan.Item.Str(K.Id);
            if (plan.Kind != SV.KindArmament)
            {
                var n = 1;
                while (Js.Items(run[K.Deck]).OfType<JObject>().Any(c => c.Str(K.InstanceId) == RunJs.Fmt(SV.ArtInstanceFormat, RunJs.NumStr(n), id))) n++;
                instance = Js.Obj(K.InstanceId, RunJs.Fmt(SV.ArtInstanceFormat, RunJs.NumStr(n), id), K.CardId, id, K.Upgraded, false);
            }
            if (plan.Kind == SV.KindArmament)
            {
                var loadout = run.Obj(K.Loadout);
                var storage = new JArray(Js.Items(loadout[RK.Storage]).Select(x => x.DeepClone())) { id };
                loadout[RK.Storage] = storage;
            }
            else run.Arr(K.Deck).Add(instance.DeepClone());
            run.Put(RK.Cinders, run.Num(RK.Cinders) - plan.Cost);
            var shelf = run.Obj(SK.ShopStock).Arr(plan.Shelf);
            shelf.RemoveAt(shelf.ToList().FindIndex(x => ReferenceEquals(x, plan.Item)));
            run.Obj(SK.ShopStock).Put(SK.TradeRevision, plan.Revision + 1);
            return Js.Obj(K.Kind, plan.Kind, K.Id, id, SK.Spent, plan.Cost, SK.Instance, (JToken)instance ?? Js.Null());
        }

        /// <summary>
        /// armamentSalePlan(registries, run, id): the buy-back price (a fraction of the low end of the piece's cost band),
        /// what stays with the item (tier and mounts), and why the trader would not buy it now.
        /// </summary>
        public static SalePlan SalePlanFor(ShopData d, JObject run, string id)
        {
            var def = d.Armament(id);
            var itemRef = def != null ? Combat.Equipment.PieceItemRef(def) : string.Join(V.ItemRefSeparator, V.ArmamentRefPrefix, id ?? V.Undefined);
            var bal = d.ShopBalance;
            var range = def != null ? bal.Obj(SK.ArmamentCost)?[RunJs.Key(def[RK.Rarity])] : null;
            var fraction = bal.Num(SK.SellFraction);
            var price = Js.Truthy(range) && fraction > 0 && fraction < 1 ? Math.Floor(Js.D((range as JArray)?[0]) * fraction) : 0;
            var equipped = (run.Obj(K.Loadout)?.Obj(K.Sets) ?? new JObject()).Properties().Any(p => Js.Includes(p.Value, id));
            var tier = RunJs.Coalesce(run.Obj(K.ItemUpgradeLevels)?[itemRef], run.Obj(RK.ArmamentLevels)?[id ?? V.Undefined]) ?? Js.N(0);
            var mounts = def != null ? CardExtraction.MountRows(d, run, itemRef, def).Where(r => Js.Truthy(r[K.CardId])).ToList() : new List<JObject>();
            var signature = new JArray(tier.DeepClone(), Js.Truthy(run.Obj(K.ItemMounts)?[itemRef]) ? run.Obj(K.ItemMounts)[itemRef].DeepClone() : Js.Null()).ToString(Formatting.None);
            string reason = null;
            if (!Js.Truthy(run[SK.ShopStock])) reason = SS.ShopRefusalNoTrader;
            else if (def == null) reason = SS.ShopRefusalUnknownArmament;
            else if (equipped) reason = SS.ShopRefusalEquipped;
            else if (!Js.Includes(run.Obj(K.Loadout)?[RK.Storage], id)) reason = SS.ShopRefusalNotInInventory;
            else if (!(SafeInteger(price) && price > 0)) reason = SS.ShopRefusalNotBuying;
            else if (!SafeInteger(run[RK.Cinders]) || !SafeInteger(run.Num(RK.Cinders) + price)) reason = SS.ShopRefusalCinderBalance;
            return new SalePlan { Ok = reason == null, Reason = reason, Id = id, Def = def, ItemRef = itemRef, Tier = tier, Mounts = mounts, Price = price, Equipped = equipped, Signature = signature, Revision = Revision(run) };
        }

        /// <summary>
        /// commitArmamentSale(registries, run, quote) → { id, received, tier }: revalidates the quote, then takes the piece
        /// out of Inventory with every card it lent the deck, and pays; its tier and mounts stay recorded for reacquisition.
        /// </summary>
        public static JObject CommitSale(ShopData d, JObject run, SalePlan quote)
        {
            var plan = SalePlanFor(d, run, quote.Id);
            if (!plan.Ok) throw new RefusalException(plan.Reason);
            if (plan.Price != quote.Price || plan.Signature != quote.Signature || plan.Revision != quote.Revision) throw new RefusalException(SS.ShopRefusalSaleChanged);
            var loadout = run.Obj(K.Loadout);
            var storage = new JArray(Js.Items(loadout[RK.Storage]).Where(x => Js.Str(x) != plan.Id || !Js.IsStr(x)).Select(x => x.DeepClone()));
            var deck = new JArray(Js.Items(run[K.Deck]).Where(c => !(c is JObject o) || CardMounts.OwnerItemRef(o) != plan.ItemRef).Select(c => c.DeepClone()));
            loadout[RK.Storage] = storage;
            run[K.Deck] = deck;
            run.Put(RK.Cinders, run.Num(RK.Cinders) + plan.Price);
            run.Obj(SK.ShopStock).Put(SK.TradeRevision, plan.Revision + 1);
            return Js.Obj(K.Id, plan.Id, SK.Received, plan.Price, RK.Tier, plan.Tier.DeepClone());
        }
    }
}
