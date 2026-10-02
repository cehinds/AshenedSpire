using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using SK = Ashen.Generated.ShopKeys;
using SV = Ashen.Generated.ShopValues;
using SM = Ashen.Generated.ShopMessages;
using SS = Ashen.Generated.ShopStringKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// One player action at the merchant. <see cref="Kind"/> is a ShopValues action name; the other fields address
    /// the offer (a shelf <see cref="Index"/>, a run relic/flask index for a sale, an armament <see cref="Id"/>, a deck
    /// <see cref="InstanceId"/>, a smith <see cref="ItemRef"/>/<see cref="MountKey"/>). An armament trade may carry the
    /// quote the player inspected (<see cref="PurchaseQuote"/>/<see cref="SaleQuote"/>); without one the current plan
    /// is the quote, as the shipped inspect-then-confirm does.
    /// </summary>
    public sealed class ShopAction
    {
        public string Kind;
        public int Index = -1;
        public string Id;
        public string InstanceId;
        public string ItemRef;
        public string MountKey;
        public PurchasePlan PurchaseQuote;
        public SalePlan SaleQuote;

        /// <summary>An action in the oracle's JSON shape ({ kind, index?, id?, instanceId?, itemRef?, mountKey? }).</summary>
        public static ShopAction FromJson(JObject o) => new ShopAction
        {
            Kind = o.Str(K.Kind),
            Index = Js.IsNum(o[RK.Index]) ? (int)o.Num(RK.Index) : -1,
            Id = o.Str(K.Id),
            InstanceId = o.Str(K.InstanceId),
            ItemRef = o.Str(K.ItemRef),
            MountKey = o.Str(SK.MountKey),
        };
    }

    /// <summary>What an action did: a receipt (the run changed) or a refusal (nothing changed).</summary>
    public sealed class ShopResult
    {
        public bool Ok;
        public JObject Receipt;
        public Refusal Refusal;

        public static ShopResult Accept(JObject receipt) => new ShopResult { Ok = true, Receipt = receipt };

        public static ShopResult Refuse(string key, params string[] args) => new ShopResult { Ok = false, Refusal = new Refusal(key, args) };

        public JObject ToJson() => Ok ? Js.Obj(SK.Ok, true, SK.Receipt, Receipt.DeepClone()) : Js.Obj(SK.Ok, false, SK.Refusal, Refusal.ToJson());
    }

    /// <summary>
    /// The merchant (US-9.1–9.3): the run-writing half of the shipped main.js enterNode 'merchant' case and of the shop
    /// screen's handlers (src/ui/screens/shop.js), UI-free. <see cref="Open"/> rolls the stock onto run.shopStock (it
    /// persists with the run, so a reload re-reads it rather than rolling again); <see cref="Execute"/> takes one action
    /// and returns a receipt or a refusal key; <see cref="View"/> is every offer with its price and availability;
    /// the Leave action clears the stock. Journeys' atlas shops are not ported (D-043).
    /// </summary>
    public static class Shop
    {
        /// <summary>
        /// enterNode(nodeId) for a merchant: build the stock on the 'shop' stream, apply the Custom Climb price
        /// multiplier (cards, relics, flasks and the removal, rounded up), roll whether a smith travels with him on the
        /// 'smith' stream, and store it all on run.shopStock. Returns the stock.
        /// </summary>
        public static JObject Open(ShopData d, JObject run, Rng rng)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (run.Is(RK.Journey)) throw new NotSupportedException(SM.JourneyShopDeferred);
            var stock = ShopStock.Build(d, rng, run);
            var pm = ShopStock.PriceMult(d, run);
            if (pm != 1)
            {
                foreach (var shelf in d.RuleList(SK.Stock, SK.PriceShelves))
                    foreach (var item in stock.Arr(shelf).OfType<JObject>()) item.Put(K.Cost, Math.Ceiling(item.Num(K.Cost) * pm));
                stock.Put(SK.RemoveCost, Math.Ceiling(stock.Num(SK.RemoveCost) * pm));
            }
            stock[SK.Smith] = SmithServices.At(d, d.RuleStr(SK.Stock, SK.MerchantNodeKind), rng);
            run[SK.ShopStock] = stock;
            return stock;
        }

        /// <summary>sellPriceFor(balance, kind, def): the buy-back price of a relic or flask (0 = not bought).</summary>
        public static double SellPrice(ShopData d, string kind, JObject def)
        {
            var shop = d.ShopBalance;
            var fraction = shop.Num(SK.SellFraction);
            if (!(fraction > 0)) return 0;
            if (kind == SV.KindRelic)
            {
                var range = shop.Obj(SK.RelicCost)?[RunJs.Key(def[RK.Rarity])] as JArray;
                return range != null ? Math.Floor(Js.D(range[0]) * fraction) : 0;
            }
            return Math.Floor(Js.D(shop.Arr(SK.FlaskCost)?[0]) * fraction);
        }

        private static JArray SmithHere(JObject stock)
        {
            var smith = stock.Obj(SK.Smith);
            return smith != null && smith.Is(SK.Offered) ? smith.Arr(SK.Services) ?? new JArray() : new JArray();
        }

        internal static double FlaskSlotCap(ShopData d)
        {
            var n = d.Balance[SK.FlaskSlots];
            if (!Js.IsInt(n) || Js.D(n) <= 0) throw new InvalidOperationException(SM.FlaskSlotsNotPositive);
            return Js.D(n);
        }

        /// <summary>offerAvailability: a plan's refusal first, then a full flask belt, then the price against the purse.</summary>
        internal static string Availability(double? price, double cinders, string reason = null, bool capacityFull = false)
        {
            if (reason != null) return reason;
            if (capacityFull) return SV.AvailFull;
            if (price != null && !(cinders >= price.Value)) return SV.AvailCinders;
            return null;
        }

        /// <summary>The quote an inspection of an armament or weapon-art offer holds (the shelf item by reference).</summary>
        public static PurchasePlan QuotePurchase(ShopData d, JObject run, string kind, int index)
        {
            var item = ShelfItem(run.Obj(SK.ShopStock), kind == SV.KindArmament ? K.Armaments : SK.WeaponArts, index);
            return item == null ? null : ArmamentTrading.PurchasePlanFor(d, run, item, kind);
        }

        /// <summary>The quote an inspection of a carried armament for sale holds.</summary>
        public static SalePlan QuoteSale(ShopData d, JObject run, string id) => ArmamentTrading.SalePlanFor(d, run, id);

        private static JObject ShelfItem(JObject stock, string shelf, int index)
        {
            var items = stock?.Arr(shelf);
            return items != null && index >= 0 && index < items.Count ? items[index] as JObject : null;
        }

        /// <summary>
        /// One action on the run: the screen's own gate first (no stock, an unaffordable price, a full belt, a burn the
        /// grid would not offer, a smith service not rolled, the sell shelf switched off), then the model call it makes.
        /// A refusal leaves the run untouched.
        /// </summary>
        public static ShopResult Execute(ShopData d, JObject run, ShopAction action, bool sellOn)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (action == null) throw new ArgumentNullException(nameof(action));
            try
            {
                return ExecuteCore(d, run, action, sellOn);
            }
            catch (RefusalException e)
            {
                return new ShopResult { Ok = false, Refusal = e.Refusal };
            }
        }

        private static ShopResult ExecuteCore(ShopData d, JObject run, ShopAction action, bool sellOn)
        {
            var stock = run[SK.ShopStock] as JObject;
            if (action.Kind == SV.ActionLeave)
            {
                if (stock == null) return ShopResult.Refuse(SV.AvailLocked);
                if (run.Is(RK.Journey)) throw new NotSupportedException(SM.JourneyShopDeferred);
                run[SK.ShopStock] = Js.Null();
                return ShopResult.Accept(Js.Obj(K.Kind, SV.ActionLeave));
            }
            if (stock == null) return ShopResult.Refuse(SV.AvailLocked);
            switch (action.Kind)
            {
                case SV.ActionBuyCard:
                {
                    var item = ShelfItem(stock, K.Cards, action.Index);
                    if (item == null) return ShopResult.Refuse(SS.ShopRefusalOfferGone);
                    if (!(run.Num(RK.Cinders) >= item.Num(K.Cost))) return ShopResult.Refuse(SV.AvailCinders);
                    run.Put(RK.Cinders, run.Num(RK.Cinders) - item.Num(K.Cost));
                    var deck = run.Arr(K.Deck);
                    var instance = Js.Obj(K.InstanceId, RunJs.Fmt(SV.CardInstanceFormat, RunJs.NumStr(deck.Count), item.Str(K.Id)), K.CardId, item[K.Id], K.Upgraded, false);
                    deck.Add(instance.DeepClone());
                    stock.Arr(K.Cards).RemoveAt(action.Index);
                    return ShopResult.Accept(Js.Obj(K.Kind, SV.KindCard, K.Id, item[K.Id], SK.Spent, item[K.Cost], SK.Instance, instance));
                }
                case SV.ActionBuyRelic:
                {
                    var item = ShelfItem(stock, K.Relics, action.Index);
                    if (item == null) return ShopResult.Refuse(SS.ShopRefusalOfferGone);
                    if (!(run.Num(RK.Cinders) >= item.Num(K.Cost))) return ShopResult.Refuse(SV.AvailCinders);
                    run.Put(RK.Cinders, run.Num(RK.Cinders) - item.Num(K.Cost));
                    run.Arr(K.Relics).Add(item[K.Id].DeepClone());
                    Creation.SyncFlaskGrowth(d.Run, run);
                    stock.Arr(K.Relics).RemoveAt(action.Index);
                    return ShopResult.Accept(Js.Obj(K.Kind, SV.KindRelic, K.Id, item[K.Id], SK.Spent, item[K.Cost]));
                }
                case SV.ActionBuyFlask:
                {
                    var item = ShelfItem(stock, K.Flasks, action.Index);
                    if (item == null) return ShopResult.Refuse(SS.ShopRefusalOfferGone);
                    if (!(run.Arr(K.Flasks).Count < FlaskSlotCap(d))) return ShopResult.Refuse(SV.AvailFull);
                    if (!(run.Num(RK.Cinders) >= item.Num(K.Cost))) return ShopResult.Refuse(SV.AvailCinders);
                    run.Put(RK.Cinders, run.Num(RK.Cinders) - item.Num(K.Cost));
                    run.Arr(K.Flasks).Add(Js.Obj(K.FlaskId, item[K.Id]));
                    stock.Arr(K.Flasks).RemoveAt(action.Index);
                    return ShopResult.Accept(Js.Obj(K.Kind, SV.KindFlask, K.Id, item[K.Id], SK.Spent, item[K.Cost]));
                }
                case SV.ActionBuyArmament:
                case SV.ActionBuyWeaponArt:
                {
                    var kind = action.Kind == SV.ActionBuyArmament ? SV.KindArmament : SV.KindWeaponArt;
                    var quote = action.PurchaseQuote ?? QuotePurchase(d, run, kind, action.Index);
                    if (quote == null) return ShopResult.Refuse(SS.ShopRefusalOfferGone);
                    return ShopResult.Accept(ArmamentTrading.CommitPurchase(d, run, quote));
                }
                case SV.ActionRemoveCard:
                {
                    var cost = stock.Num(SK.RemoveCost);
                    if (!(run.Num(RK.Cinders) >= cost)) return ShopResult.Refuse(SV.AvailCinders);
                    var deck = run.Arr(K.Deck);
                    if (!(deck.Count > 1)) return ShopResult.Refuse(SV.AvailLocked);
                    var card = deck.OfType<JObject>().FirstOrDefault(c => c.Str(K.InstanceId) == action.InstanceId);
                    if (!CardRemoval.RemoveDeckCard(d, run, action.InstanceId, true)) return ShopResult.Refuse(SV.AvailLocked);
                    run.Put(RK.Cinders, run.Num(RK.Cinders) - cost);
                    run.Put(SK.RemovesPurchased, run.Or0(SK.RemovesPurchased) + 1);
                    stock.Put(SK.RemoveCost, d.ShopBalance.Num(SK.RemoveBase) + d.ShopBalance.Num(SK.RemoveStep) * run.Num(SK.RemovesPurchased));
                    return ShopResult.Accept(Js.Obj(K.Kind, SV.KindRemove, K.InstanceId, action.InstanceId, K.CardId, card[K.CardId], SK.Spent, cost, SK.RemoveCost, stock[SK.RemoveCost]));
                }
                case SV.ActionSellRelic:
                case SV.ActionSellFlask:
                {
                    if (!sellOn) return ShopResult.Refuse(SV.AvailLocked);
                    var relic = action.Kind == SV.ActionSellRelic;
                    var list = run.Arr(relic ? K.Relics : K.Flasks);
                    if (list == null || action.Index < 0 || action.Index >= list.Count) return ShopResult.Refuse(SS.ShopRefusalOfferGone);
                    var held = list[action.Index];
                    var id = relic ? Js.Str(held) : Js.Get(held, K.FlaskId)?.Value<string>();
                    var price = SellPrice(d, relic ? SV.KindRelic : SV.KindFlask, relic ? d.Run.Relics.Get(id) : d.Combat.Flasks.Get(id));
                    if (!(price > 0)) return ShopResult.Refuse(SV.AvailLocked);
                    list.RemoveAt(action.Index);
                    if (relic) Creation.SyncFlaskGrowth(d.Run, run);
                    run.Put(RK.Cinders, run.Num(RK.Cinders) + price);
                    return ShopResult.Accept(Js.Obj(K.Kind, relic ? SV.KindRelic : SV.KindFlask, K.Id, id, SK.Received, price));
                }
                case SV.ActionSellArmament:
                {
                    if (!sellOn) return ShopResult.Refuse(SV.AvailLocked);
                    return ShopResult.Accept(ArmamentTrading.CommitSale(d, run, action.SaleQuote ?? QuoteSale(d, run, action.Id)));
                }
                case SV.ActionSmithUpgrade:
                {
                    if (!Js.Includes(SmithHere(stock), SV.ServiceUpgrade)) return ShopResult.Refuse(SV.AvailLocked);
                    if (ItemSmithing.Plan(d, run).Arr(SK.Candidates).Count == 0) return ShopResult.Refuse(SV.AvailLocked);
                    return ShopResult.Accept(ItemSmithing.Commit(d, run, action.ItemRef));
                }
                case SV.ActionSmithExtract:
                {
                    if (!Js.Includes(SmithHere(stock), SV.ServiceExtract)) return ShopResult.Refuse(SV.AvailLocked);
                    if (CardExtraction.ExtractionPlan(d, run).Arr(SK.Candidates).Count == 0) return ShopResult.Refuse(SV.AvailLocked);
                    return ShopResult.Accept(CardExtraction.CommitExtraction(d, run, action.ItemRef, action.MountKey));
                }
                case SV.ActionSmithInstall:
                {
                    if (!Js.Includes(SmithHere(stock), SV.ServiceInstall)) return ShopResult.Refuse(SV.AvailLocked);
                    if (CardExtraction.InstallPlan(d, run).Arr(SK.Candidates).Count == 0) return ShopResult.Refuse(SV.AvailLocked);
                    return ShopResult.Accept(CardExtraction.CommitInstall(d, run, action.ItemRef, action.MountKey, action.InstanceId));
                }
                default:
                    throw new ArgumentException(RunJs.Fmt(SM.UnknownAction, action.Kind));
            }
        }

        /// <summary>
        /// What the screen shows: every offer with its price and, when it cannot be taken now, the refusal key; the card
        /// removal with the cards the burn grid offers; the smith (when one travels with him) with each service's
        /// candidates; and, with the sell toggle on, the player's goods the merchant would buy. Null without a stock.
        /// </summary>
        public static JObject View(ShopData d, JObject run, bool sellOn)
        {
            if (!(run[SK.ShopStock] is JObject stock)) return null;
            var cinders = run.Num(RK.Cinders);
            var slotsFree = run.Arr(K.Flasks).Count < FlaskSlotCap(d);
            JToken Key(string key) => key == null ? (JToken)Js.Null() : key;
            JArray Priced(string shelf, bool capacity) => new JArray(Js.Items(stock[shelf]).OfType<JObject>().Select((item, index) =>
                Js.Obj(RK.Index, (double)index, K.Id, item[K.Id], K.Cost, item[K.Cost], SK.Refusal, Key(Availability(item.Num(K.Cost), cinders, null, capacity && !slotsFree)))));

            var view = new JObject { [K.Cards] = Priced(K.Cards, false) };
            var armaments = new JArray();
            var index = 0;
            foreach (var item in Js.Items(stock[K.Armaments]).OfType<JObject>())
            {
                var plan = ArmamentTrading.PurchasePlanFor(d, run, item, SV.KindArmament);
                if (plan.Def != null) armaments.Add(Js.Obj(RK.Index, (double)index, K.Id, item[K.Id], K.Cost, Js.N(plan.Cost), SK.Refusal, Key(plan.Ok ? null : plan.Reason)));
                index++;
            }
            view[K.Armaments] = armaments;
            view[SK.WeaponArts] = new JArray(Js.Items(stock[SK.WeaponArts]).OfType<JObject>().Select((item, i) =>
            {
                var quote = ArmamentTrading.PurchasePlanFor(d, run, item, SV.KindWeaponArt);
                return Js.Obj(RK.Index, (double)i, K.Id, item[K.Id], K.Cost, item[K.Cost], SK.Refusal, Key(quote.Ok ? null : quote.Reason));
            }));
            view[K.Relics] = Priced(K.Relics, false);
            view[K.Flasks] = Priced(K.Flasks, true);
            var removeAvail = Availability(stock.Num(SK.RemoveCost), cinders);
            var removeOpen = removeAvail == null && run.Arr(K.Deck).Count > 1;
            view[SK.Remove] = Js.Obj(K.Cost, stock[SK.RemoveCost], SK.Refusal, Key(removeOpen ? null : removeAvail ?? SV.AvailLocked),
                K.Cards, new JArray(Js.Items(run[K.Deck]).OfType<JObject>().Where(CardRemoval.CanRemove).Select(c => c[K.InstanceId])));
            var here = SmithHere(stock);
            var smith = Js.Obj(SK.Offered, stock.Obj(SK.Smith) != null && stock.Obj(SK.Smith).Is(SK.Offered), SK.Services, here.DeepClone());
            if (Js.Includes(here, SV.ServiceUpgrade))
            {
                var plan = ItemSmithing.Plan(d, run);
                var candidates = plan.Arr(SK.Candidates);
                smith[SV.ServiceUpgrade] = Js.Obj(SK.Refusal, Key(candidates.Count > 0 ? null : SV.AvailLocked), SK.Stones, plan[SK.Stones],
                    SK.Candidates, new JArray(candidates.OfType<JObject>().Select(c => c[K.ItemRef])));
            }
            foreach (var service in new[] { SV.ServiceExtract, SV.ServiceInstall })
            {
                if (!Js.Includes(here, service)) continue;
                var plan = service == SV.ServiceExtract ? CardExtraction.ExtractionPlan(d, run) : CardExtraction.InstallPlan(d, run);
                var candidates = plan.Arr(SK.Candidates);
                smith[service] = Js.Obj(SK.Refusal, Key(candidates.Count > 0 ? null : SV.AvailLocked), SK.Stones, plan[SK.Stones], K.Cost, plan[K.Cost],
                    SK.Candidates, new JArray(candidates.OfType<JObject>().Select(c => c[K.ItemRef])));
            }
            view[SK.Smith] = smith;
            if (sellOn)
            {
                var sellArmaments = new JArray();
                foreach (var id in RewardRolls.CarriedIds(run.Obj(K.Loadout)))
                {
                    var plan = ArmamentTrading.SalePlanFor(d, run, id);
                    if (plan.Def != null) sellArmaments.Add(Js.Obj(K.Id, id, SK.Price, plan.Price, SK.Refusal, Key(plan.Ok ? null : plan.Reason)));
                }
                var goods = new JArray();
                var relics = run.Arr(K.Relics) ?? new JArray();
                for (var at = 0; at < relics.Count; at++)
                {
                    var price = SellPrice(d, SV.KindRelic, d.Run.Relics.Get(Js.Str(relics[at])));
                    if (price > 0) goods.Add(Js.Obj(K.Kind, SV.KindRelic, RK.Index, (double)at, K.Id, relics[at], SK.Price, price));
                }
                var flasks = run.Arr(K.Flasks) ?? new JArray();
                for (var at = 0; at < flasks.Count; at++)
                {
                    var id = Js.Str(Js.Get(flasks[at], K.FlaskId));
                    var price = SellPrice(d, SV.KindFlask, d.Combat.Flasks.Get(id));
                    if (price > 0) goods.Add(Js.Obj(K.Kind, SV.KindFlask, RK.Index, (double)at, K.Id, id, SK.Price, price));
                }
                view[SK.Sell] = Js.Obj(K.Armaments, sellArmaments, SK.Goods, goods);
            }
            else view[SK.Sell] = Js.Null();
            return view;
        }
    }
}
