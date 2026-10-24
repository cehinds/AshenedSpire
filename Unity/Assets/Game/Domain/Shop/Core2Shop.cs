using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2ProfileContentKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using RK = Ashen.Generated.RunKeys;
using RW = Ashen.Generated.Core2RewardsKeys;
using SM = Ashen.Generated.Core2ShopMessages;
using SV = Ashen.Generated.Core2ShopValues;
using SW = Ashen.Generated.Core2ShopKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// The shop of a Core 2 run (web model/gameProfileRewards.js gameProfileShopStock and purchaseGameProfileShopOffer, main.js enterNode and showShop, WP-17): a merchant node
    /// opens a stock kept among the run's reward offers under <c>shop:&lt;act&gt;:&lt;node&gt;</c>. It holds <c>itemsPerCategory</c> items on each shelf (cards, weapon arts,
    /// weapons, armour, relics, potions, in the order the rewards content lists them), each priced alike; the shelves are filled once, on the <c>shop</c> stream, from the same
    /// pools a fight's spoils draw on, and a visit later sees the same stock. A purchase spends the price in cinders and claims the item as a reward does (a card lands in the
    /// sideboard); a refusal changes nothing. The legacy merchant (<see cref="Shop"/>) is a different set of rules and stays. Engine-free.
    /// </summary>
    public static class Core2Shop
    {
        /// <summary>The id of a shop's stock: <c>shop:&lt;act&gt;:&lt;node&gt;</c>.</summary>
        public static string OfferId(string shopId) => SV.OfferPrefix + shopId;

        /// <summary>The shop of the node the run stands at: <c>&lt;act&gt;:&lt;node&gt;</c>.</summary>
        public static string ShopIdAt(JObject run) => string.Join(SV.IdSeparator, RunJs.Key(run[RK.ActNumber]), RunJs.Key(run[RK.MapNodeId]));

        /// <summary>The shop the run has open (<c>gameProfileOpenShop</c>), or null.</summary>
        public static string Open(JObject run) => Js.Truthy(run?[SW.GameProfileOpenShop]) ? RunJs.Key(run[SW.GameProfileOpenShop]) : null;

        /// <summary>The profile turns shops on.</summary>
        public static bool Enabled(Core2RewardsData d, JObject run) => d.Module(run, MI.Shops) != null;

        /// <summary>
        /// gameProfileShopStock(registries, run, rng, { shopId }): the stock under <c>shop:&lt;shopId&gt;</c>, drawn now on the first visit and as it stands after that. The price is
        /// written onto the stock when it is made (the module's, else the content's).
        /// </summary>
        public static JObject Stock(Core2RewardsData d, JObject run, Rng rng, string shopId)
        {
            var module = d.Module(run, MI.Shops);
            if (module?.Str(RW.Mode) != Ashen.Generated.Core2RunValues.Configured) throw new InvalidOperationException(SM.ShopUnavailable);
            var rules = d.Content(run, CK.Shops) as JObject;
            var categories = Js.Items((d.Content(run, RW.Rewards) as JObject)?[RW.Categories]).Select(Js.Str).ToList();
            var perShelf = Js.Nullish(module[SW.ItemsPerCategory]) ? rules?[SW.ItemsPerCategory] : module[SW.ItemsPerCategory];
            int? itemsPerCategory = Js.Nullish(perShelf) ? (int?)null : (int)Js.D(perShelf);
            var count = itemsPerCategory == null
                ? Js.D(Js.Nullish(module[SW.Options]) ? rules[SW.OfferCount] : module[SW.Options])
                : itemsPerCategory.Value * categories.Count;
            var stock = Core2Rewards.MakeOffer(d, run, rng, OfferId(shopId), Ashen.Generated.Core2RewardsValues.SourceShop, count, count, categories, null, itemsPerCategory);
            if (stock[SW.Price] == null && (Js.Nullish(module[SW.Price]) ? rules?[SW.Price] : module[SW.Price]) is JToken price && !Js.Nullish(price)) stock[SW.Price] = price.DeepClone();
            return stock;
        }

        /// <summary>
        /// The merchant node's door (main.js enterNode, then showShop): a profile without shops goes straight back to the map (null); otherwise the shop is marked open on the run
        /// and its stock made or found. Returns the shop's id.
        /// </summary>
        public static string Enter(Core2RewardsData d, JObject run, Rng rng, string nodeId)
        {
            if (!Enabled(d, run)) return null;
            var shopId = string.Join(SV.IdSeparator, RunJs.Key(run[RK.ActNumber]), nodeId);
            run[SW.GameProfileOpenShop] = shopId;
            Stock(d, run, rng, shopId);
            return shopId;
        }

        /// <summary>The shop screen's mount (showShop): the open shop's stock, made now if the run was saved before it had one.</summary>
        public static JObject Show(Core2RewardsData d, JObject run, Rng rng)
        {
            if (!Enabled(d, run)) return null;
            var shopId = Open(run) ?? ShopIdAt(run);
            run[SW.GameProfileOpenShop] = shopId;
            return Stock(d, run, rng, shopId);
        }

        /// <summary>
        /// purchaseGameProfileShopOffer(registries, run, shopId, index): spends the stock's price and claims the item at <paramref name="index"/> as a fight's spoils would. Throws the web's
        /// sentences (<c>Missing configured shop</c>, <c>Invalid configured shop price</c>, <c>Insufficient cinders</c>, then the claim's own) and leaves the run as it was. Returns the item
        /// bought, with the price paid.
        /// </summary>
        public static JObject Purchase(Core2RewardsData d, JObject run, string shopId, int index)
        {
            var offerId = OfferId(shopId);
            var stock = (run[RW.GameProfileRewardOffers] as JObject)?[offerId] as JObject;
            if (stock == null || stock.Str(RW.Source) != Ashen.Generated.Core2RewardsValues.SourceShop) throw new InvalidOperationException(SM.MissingShop);
            if (!Js.IsSafeInteger(stock[SW.Price]) || stock.Num(SW.Price) < 0) throw new InvalidOperationException(SM.InvalidPrice);
            var price = stock.Num(SW.Price);
            if (run.Num(SW.Cinders) < price) throw new InvalidOperationException(SM.InsufficientCinders);
            // The claim restores the run when it refuses; the price comes off only once it has been taken.
            var receipt = Core2Rewards.Claim(d, run, offerId, index);
            run.Put(SW.Cinders, run.Num(SW.Cinders) - price);
            receipt.Put(SW.Price, price);
            return receipt;
        }

        /// <summary>The shop screen's Leave: the open marker goes (the stock stays with its taken items).</summary>
        public static void Leave(JObject run) => run.Remove(SW.GameProfileOpenShop);

        /// <summary>
        /// Whether an item can be bought as the screen shows it (shop.js mountConfiguredShop): not taken, the stock not claimed whole, the purse covers the price and, for a
        /// potion, the flask can be picked up.
        /// </summary>
        public static bool Available(Core2RewardsData d, JObject run, JObject stock, int index)
        {
            var option = stock.Arr(RW.Options)?[index] as JObject;
            if (option == null) return false;
            var taken = Js.Items(stock[RW.Taken]).Any(t => Js.IsNum(t) && (int)Js.D(t) == index);
            var claimed = !Js.Nullish(stock[RW.Claimed]);
            return !taken && !claimed && run.Num(SW.Cinders) >= stock.Num(SW.Price) && (option.Str(RW.Kind) != Ashen.Generated.Core2RewardsValues.KindPotions || Core2Rewards.CanAcquireFlask(d, run, option.Str(RW.Id)));
        }
    }
}
