using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DeckKeys;
using RV = Ashen.Generated.Core2RewardsValues;
using RW = Ashen.Generated.Core2RewardsKeys;
using SV = Ashen.Generated.Core2ShopValues;
using SW = Ashen.Generated.Core2ShopKeys;

namespace Ashen.App.Run
{
    /// <summary>One item on a shelf of a Core 2 shop as a screen draws it (web ui/screens/shop.js mountConfiguredShop).</summary>
    public sealed class Core2ShopOfferView
    {
        /// <summary>The item's place in the stock (what <see cref="Core2RunSession.BuyFromShop"/> takes).</summary>
        public int Index;

        /// <summary>The item's kind (<see cref="RV"/>: combatCards, weaponArts, weapons, armor, relics, potions) and registry id.</summary>
        public string Kind;

        public string Id;

        /// <summary>The registry name of the item (its id when the registry has none).</summary>
        public string Name;

        public double Price;

        /// <summary>The price as the screen says it ("10 cinders") and the Buy button's text ("Buy · 10 cinders").</summary>
        public string PriceText;

        public string BuyText;

        public bool Taken;

        /// <summary>Buy can be pressed: not taken, the stock not claimed whole, the purse covers the price and a potion has room (the screen's <c>available</c>).</summary>
        public bool Available;

        /// <summary>When not available, the string key of the reason the screen shows (<c>shop.avail.locked</c> for a taken item, else <c>shop.avail.cinders</c>) and its text; null otherwise.</summary>
        public string ReasonKey;

        public string Reason;

        /// <summary>The confirmation the web asks before spending: the question, the message with the purse before and after, and the button.</summary>
        public string ConfirmQuestion;

        public string ConfirmMessage;
        public string ConfirmButton;
    }

    /// <summary>One shelf (a category tab) of the shop: the items of one kind, in the order the stock lists them.</summary>
    public sealed class Core2ShopShelfView
    {
        public string Kind;
        public string Label;
        public IReadOnlyList<Core2ShopOfferView> Offers = Array.Empty<Core2ShopOfferView>();
    }

    /// <summary>The whole shop of a Core 2 run as the (future) screen draws it.</summary>
    public sealed class Core2ShopViewState
    {
        public string ShopId;
        public string Title;
        public string LeaveText;

        /// <summary>The purse: the cinders carried and how the screen says it.</summary>
        public double Cinders;

        public string PurseText;

        /// <summary>The price every item costs.</summary>
        public double Price;

        /// <summary>The shelves in the order their kinds first appear in the stock (a tab each); an empty stock has none.</summary>
        public IReadOnlyList<Core2ShopShelfView> Shelves = Array.Empty<Core2ShopShelfView>();
    }

    /// <summary>
    /// The shop of a Core 2 run as display data (WP-17; the seam the shop screen reads): the purse, the shelves with their items, what each costs and whether it can be
    /// bought now and, if not, why. Engine-free. The screen binds Buy to <see cref="Core2RunSession.BuyFromShop"/> (after the confirmation each offer carries) and Leave to
    /// <see cref="Core2RunSession.LeaveShop"/>.
    /// </summary>
    public static class Core2ShopView
    {
        public static Core2ShopViewState Build(Core2RunSession session, StringTable strings)
        {
            var stock = session.ShopStock;
            if (stock == null) return null;
            var d = session.Core.Rewards;
            var price = stock.Num(SW.Price);
            var cinders = session.Cinders;
            string Say(string key, params (string name, double value)[] args)
            {
                var named = new StringArgs();
                foreach (var a in args) named.Add(a.name, Ashen.Domain.Run.RunJs.NumStr(a.value));
                return strings.Format(key, named);
            }
            var offers = new List<Core2ShopOfferView>();
            var rows = stock.Arr(RW.Options) ?? new JArray();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = (JObject)rows[i];
                var kind = row.Str(RW.Kind);
                var taken = Js.Items(stock[RW.Taken]).Any(t => Js.IsNum(t) && (int)Js.D(t) == i);
                var available = session.CanBuy(stock, i);
                var reasonKey = available ? null : taken ? Ashen.Generated.StringKeys.Core2ShopAvailLocked : Ashen.Generated.StringKeys.Core2ShopAvailCinders;
                offers.Add(new Core2ShopOfferView
                {
                    Index = i, Kind = kind, Id = row.Str(RW.Id), Name = NameOf(d, kind, row.Str(RW.Id)), Price = price,
                    PriceText = Say(Ashen.Generated.StringKeys.Core2ShopPrice, (SV.PlaceholderCost, price)),
                    BuyText = Say(Ashen.Generated.StringKeys.Core2ShopBuy, (SV.PlaceholderCost, price)),
                    Taken = taken, Available = available, ReasonKey = reasonKey, Reason = reasonKey == null ? null : strings.Get(reasonKey),
                    ConfirmQuestion = strings.Get(Ashen.Generated.StringKeys.Core2ShopConfirmQuestion),
                    ConfirmMessage = Say(Ashen.Generated.StringKeys.Core2ShopConfirmMessage, (SV.PlaceholderCost, price), (SV.PlaceholderCinders, cinders), (SV.PlaceholderLeft, cinders - price)),
                    ConfirmButton = strings.Get(Ashen.Generated.StringKeys.Core2ShopConfirmButton),
                });
            }
            var shelves = offers.GroupBy(o => o.Kind).Select(g => new Core2ShopShelfView
            {
                Kind = g.Key, Label = strings.Get(string.Format(CultureInfo.InvariantCulture, SV.BarKeyFormat, g.Key)), Offers = g.ToList(),
            }).ToList();
            return new Core2ShopViewState
            {
                ShopId = session.OpenShopId, Title = strings.Get(Ashen.Generated.StringKeys.Core2ShopTitle), LeaveText = strings.Get(Ashen.Generated.StringKeys.Core2ShopLeave),
                Cinders = cinders, PurseText = Say(Ashen.Generated.StringKeys.Core2ShopPurse, (SV.PlaceholderCinders, cinders)), Price = price, Shelves = shelves,
            };
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
