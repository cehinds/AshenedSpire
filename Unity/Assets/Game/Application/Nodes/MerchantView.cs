using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using SK = Ashen.Generated.ShopKeys;
using SS = Ashen.Generated.ShopStringKeys;
using SV = Ashen.Generated.ShopValues;

namespace Ashen.App.Nodes
{
    /// <summary>One offer on a W-09 shelf as the screen draws it: what it is, its price, whether it can be taken and why not, and the action it takes.</summary>
    public sealed class MerchantOffer
    {
        /// <summary>Unique within the view (shelf and position), kept across redraws for focus.</summary>
        public string Key;

        public string Shelf;

        /// <summary>A NodeValues offer kind (card, relic, flask, armament, weaponArt, remove, upgrade, extract, install, sale).</summary>
        public string Kind;

        public ShopAction Action;
        public string Name;
        public string KindText;
        public string Text;

        /// <summary>The card face (cards and weapon arts) for the detail pane.</summary>
        public RewardPickView Card;

        public string ArtId;
        public string GlyphKey;
        public double Price;
        public bool PaidInStones;
        public bool Sale;
        public string PriceText;
        public bool Available;
        public string ReasonText;

        /// <summary>A card to pick first (the burn: the removable deck cards; an install: the cards the mount takes), by instance id.</summary>
        public List<RewardPickView> Picks = new List<RewardPickView>();

        public string PickTitle;
        public string PrimaryText;

        /// <summary>The W2a door (ConfirmIds) and its arguments (item, cost, cinders, left, price, stones).</summary>
        public string ConfirmId;

        public StringArgs ConfirmArgs = new StringArgs();
        public bool NeedsPick => Picks.Count > 0 || Kind == NodeValues.OfferRemove || Kind == NodeValues.OfferInstall;
    }

    /// <summary>A shelf (a rail category): its offers, the count beside its label and the pane's status line.</summary>
    public sealed class MerchantShelfView
    {
        public string Id;
        public string LabelKey;
        public string Count;
        public string Status;
        public bool Empty;
        public List<MerchantOffer> Offers = new List<MerchantOffer>();

        /// <summary>The sell shelf's Buy back section (D-081: there is no separate buy-back; the note says so).</summary>
        public string NoteTitle;

        public string Note;
    }

    /// <summary>The whole W-09 merchant as the screen draws it.</summary>
    public sealed class MerchantViewState
    {
        public string Title;
        public double Cinders;
        public double Stones;
        public bool SellOn;
        public List<MerchantShelfView> Shelves = new List<MerchantShelfView>();

        public MerchantShelfView Shelf(string id) => Shelves.FirstOrDefault(s => s.Id == id);

        public MerchantOffer Offer(string key) => Shelves.SelectMany(s => s.Offers).FirstOrDefault(o => o.Key == key);
    }

    /// <summary>
    /// W-09 as display data (US-9.2, US-9.3): the shelves in ui/nodes.json order — cards, armaments, weapon arts, relics,
    /// flasks, services (the card burn at its rising price, and the smith's upgrade, extract and install when a smith
    /// travels with him), and the sell shelf while the shopSell setting is on — each offer with its name, text, art, price,
    /// availability and the reason it cannot be taken (an unaffordable price reads "Need n more cinders"), and the W2a
    /// door its Buy opens. Built from the domain's own view (<see cref="Shop.View"/>) and plans. Engine-free.
    /// </summary>
    public static class MerchantView
    {
        public static MerchantViewState Build(MerchantSession session, UiData ui)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var strings = ui.Strings;
            var run = session.Run;
            var view = session.DomainView(run);
            var state = new MerchantViewState
            {
                Title = strings.Get(NodeStringKeys.NodesMerchantTitle),
                Cinders = run.Num(RK.Cinders),
                Stones = Js.Or0(run[RK.SmithingStones]),
                SellOn = session.SellOn,
            };
            if (view == null) return state;
            foreach (var id in ui.Nodes.MerchantShelves)
            {
                if (id == NodeValues.ShelfSell && !session.SellOn) continue;
                var shelf = new MerchantShelfView { Id = id, LabelKey = string.Format(CultureInfo.InvariantCulture, NodeFormats.ShelfLabelKey, id) };
                switch (id)
                {
                    case NodeValues.ShelfCards: Priced(session, ui, run, view, shelf, K.Cards, NodeValues.OfferCard, SV.ActionBuyCard); break;
                    case NodeValues.ShelfArmaments: Priced(session, ui, run, view, shelf, K.Armaments, NodeValues.OfferArmament, SV.ActionBuyArmament); break;
                    case NodeValues.ShelfWeaponArts: Priced(session, ui, run, view, shelf, SK.WeaponArts, NodeValues.OfferWeaponArt, SV.ActionBuyWeaponArt); break;
                    case NodeValues.ShelfRelics: Priced(session, ui, run, view, shelf, K.Relics, NodeValues.OfferRelic, SV.ActionBuyRelic); break;
                    case NodeValues.ShelfFlasks: Priced(session, ui, run, view, shelf, K.Flasks, NodeValues.OfferFlask, SV.ActionBuyFlask); break;
                    case NodeValues.ShelfServices: Services(session, ui, run, view, shelf); break;
                    case NodeValues.ShelfSell: Sell(session, ui, run, view, shelf); break;
                    default: continue;
                }
                state.Shelves.Add(shelf);
            }
            return state;
        }

        // ------------------------------------------------------------------ priced shelves

        private static void Priced(MerchantSession session, UiData ui, JObject run, JObject view, MerchantShelfView shelf, string key, string kind, string action)
        {
            var strings = ui.Strings;
            var data = session.Data;
            var combat = data.Combat;
            var cinders = run.Num(RK.Cinders);
            foreach (var row in Js.Items(view[key]).OfType<JObject>())
            {
                var id = row.Str(K.Id);
                var cost = row.Num(K.Cost);
                var offer = new MerchantOffer
                {
                    Key = shelf.Id + NodeFormats.KeySeparator + NodeText.Number(row.Num(RK.Index)),
                    Shelf = shelf.Id,
                    Kind = kind,
                    Action = new ShopAction { Kind = action, Index = (int)row.Num(RK.Index) },
                    Price = cost,
                    PriceText = strings.Format(NodeStringKeys.NodesMerchantPrice, new StringArgs().Add(NodePlaceholders.Cost, NodeText.Number(cost))),
                    KindText = strings.Get(string.Format(CultureInfo.InvariantCulture, NodeFormats.KindLabelKey, kind)),
                    GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.GlyphKey, kind == NodeValues.OfferWeaponArt ? K.Card : kind),
                    ConfirmId = ConfirmIds.MerchantBuy,
                };
                switch (kind)
                {
                    case NodeValues.OfferCard:
                    case NodeValues.OfferWeaponArt:
                        offer.Card = RewardsView.CardPick(combat, strings, ui.Components.CostKinds, id, false);
                        offer.Name = offer.Card.Name;
                        offer.Text = offer.Card.Text;
                        break;
                    case NodeValues.OfferArmament:
                    {
                        var def = data.Armament(id) ?? new JObject();
                        offer.Name = def.Str(K.Name) ?? id;
                        offer.Text = def.Str(RunFlowKeys.Blurb) ?? string.Empty;
                        offer.ArtId = RewardsView.ArtFromKey(ui.Components.RewardArmamentArt, def.Str(RunFlowKeys.ArtKey) ?? id, null);
                        break;
                    }
                    case NodeValues.OfferRelic:
                    {
                        var def = combat.Relics.Has(id) ? combat.Relics.Get(id) : new JObject();
                        offer.Name = def.Str(K.Name) ?? id;
                        offer.Text = RewardsView.Sentence(combat, def.Str(K.TextTemplate), RewardsView.RelicTokens(combat, def, ui.Components));
                        offer.ArtId = StringTable.Fill(ui.Components.RewardRelicArt, new StringArgs().Add(UiPlaceholders.Id, id));
                        break;
                    }
                    case NodeValues.OfferFlask:
                    {
                        var def = combat.Flasks.Has(id) ? combat.Flasks.Get(id) : new JObject();
                        offer.Name = def.Str(K.Name) ?? id;
                        offer.Text = RewardsView.Sentence(combat, def.Str(K.TextTemplate), RewardsView.EffectTokens(combat, def[K.Effects]));
                        offer.ArtId = RewardsView.ArtFromKey(ui.Components.RewardFlaskArt, def.Str(RunFlowKeys.ArtKey), ui.Components.RewardFlaskArtPrefix);
                        break;
                    }
                }
                Availability(strings, offer, row.Str(SK.Refusal), cinders);
                offer.PrimaryText = strings.Format(NodeStringKeys.NodesMerchantPrimaryBuy, new StringArgs().Add(NodePlaceholders.Cost, NodeText.Number(cost)));
                Door(offer, cinders);
                shelf.Offers.Add(offer);
            }
            shelf.Count = NodeText.Number(shelf.Offers.Count);
            shelf.Empty = shelf.Offers.Count == 0;
            shelf.Status = shelf.Empty
                ? strings.Get(NodeStringKeys.NodesMerchantSoldOut)
                : strings.Format(NodeStringKeys.NodesMerchantForSale, new StringArgs().Add(NodePlaceholders.N, NodeText.Number(shelf.Offers.Count)));
        }

        /// <summary>An unaffordable price reads "Need n more"; any other refusal key is resolved from the string tables.</summary>
        private static void Availability(StringTable strings, MerchantOffer offer, string refusal, double purse, double shortfall = -1)
        {
            offer.Available = refusal == null;
            if (offer.Available) return;
            var cinders = refusal == SV.AvailCinders || refusal == SS.ShopRefusalCinders;
            if (cinders || (offer.PaidInStones && shortfall > 0))
            {
                var need = offer.PaidInStones ? shortfall : offer.Price - purse;
                offer.ReasonText = strings.Format(offer.PaidInStones ? NodeStringKeys.NodesMerchantNeedStones : NodeStringKeys.NodesMerchantNeed,
                    new StringArgs().Add(NodePlaceholders.Need, NodeText.Number(Math.Max(0, need))));
                return;
            }
            offer.ReasonText = NodeText.Refusal(strings, refusal);
        }

        private static void Door(MerchantOffer offer, double cinders)
        {
            offer.ConfirmArgs = new StringArgs()
                .Add(NodePlaceholders.Item, offer.Name)
                .Add(NodePlaceholders.Cost, NodeText.Number(offer.Price))
                .Add(NodePlaceholders.Cinders, NodeText.Number(cinders))
                .Add(NodePlaceholders.Left, NodeText.Number(cinders - offer.Price))
                .Add(NodePlaceholders.Price, NodeText.Number(offer.Price));
        }

        // ------------------------------------------------------------------ services

        private static void Services(MerchantSession session, UiData ui, JObject run, JObject view, MerchantShelfView shelf)
        {
            var strings = ui.Strings;
            var data = session.Data;
            var cinders = run.Num(RK.Cinders);
            var remove = view.Obj(SK.Remove);
            if (remove != null)
            {
                var cost = remove.Num(K.Cost);
                var step = data.ShopBalance.Num(SK.RemoveStep);
                var deck = Js.Items(run[K.Deck]).OfType<JObject>().ToList();
                var offer = new MerchantOffer
                {
                    Key = shelf.Id + NodeFormats.KeySeparator + NodeValues.OfferRemove,
                    Shelf = shelf.Id,
                    Kind = NodeValues.OfferRemove,
                    Action = new ShopAction { Kind = SV.ActionRemoveCard },
                    Name = strings.Get(NodeStringKeys.NodesMerchantServiceRemove),
                    Text = strings.Format(NodeStringKeys.NodesMerchantServiceRemoveBody, new StringArgs().Add(NodePlaceholders.Cost, NodeText.Number(cost)).Add(NodePlaceholders.Step, NodeText.Number(step))),
                    KindText = strings.Get(NodeStringKeys.NodesMerchantKindService),
                    GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.GlyphKey, K.Card),
                    Price = cost,
                    PriceText = strings.Format(NodeStringKeys.NodesMerchantPrice, new StringArgs().Add(NodePlaceholders.Cost, NodeText.Number(cost))),
                    PickTitle = strings.Get(NodeStringKeys.NodesMerchantPickRemove),
                    PrimaryText = strings.Format(NodeStringKeys.NodesMerchantPrimaryChoose, new StringArgs().Add(NodePlaceholders.Cost, NodeText.Number(cost))),
                    ConfirmId = ConfirmIds.MerchantRemove,
                };
                foreach (var instanceId in Js.Items(remove[K.Cards]).Select(Js.Str))
                {
                    var card = deck.FirstOrDefault(c => c.Str(K.InstanceId) == instanceId);
                    if (card != null) offer.Picks.Add(DeckPick(data.Combat, ui, card));
                }
                Availability(strings, offer, remove.Str(SK.Refusal), cinders);
                Door(offer, cinders);
                shelf.Offers.Add(offer);
            }
            var smith = view.Obj(SK.Smith);
            if (smith != null && smith.Is(SK.Offered))
            {
                var services = Js.Items(smith[SK.Services]).Select(Js.Str).ToList();
                var stones = Js.Or0(run[RK.SmithingStones]);
                if (services.Contains(SV.ServiceUpgrade))
                    foreach (var c in Js.Items(ItemSmithing.Plan(data, run)[SK.Candidates]).OfType<JObject>())
                    {
                        var offer = SmithOffer(strings, shelf, NodeValues.OfferUpgrade, c, stones,
                            strings.Format(NodeStringKeys.NodesMerchantServiceUpgrade, new StringArgs().Add(NodePlaceholders.Item, c.Str(SK.ItemName)).Add(NodePlaceholders.Level, NodeText.Number(c.Num(SK.NextLevel)))),
                            strings.Get(NodeStringKeys.NodesMerchantServiceUpgradeBody), new ShopAction { Kind = SV.ActionSmithUpgrade, ItemRef = c.Str(K.ItemRef) }, c.Str(K.ItemRef));
                        shelf.Offers.Add(offer);
                    }
                if (services.Contains(SV.ServiceExtract))
                    foreach (var c in Js.Items(CardExtraction.ExtractionPlan(data, run)[SK.Candidates]).OfType<JObject>())
                        foreach (var mount in Js.Items(c[RK.Mounts]).OfType<JObject>())
                        {
                            var offer = SmithOffer(strings, shelf, NodeValues.OfferExtract, c, stones,
                                strings.Format(NodeStringKeys.NodesMerchantServiceExtract, new StringArgs().Add(NodePlaceholders.Card, mount.Str(SK.CardName)).Add(NodePlaceholders.Item, c.Str(SK.ItemName))),
                                strings.Get(NodeStringKeys.NodesMerchantServiceExtractBody),
                                new ShopAction { Kind = SV.ActionSmithExtract, ItemRef = c.Str(K.ItemRef), MountKey = mount.Str(SK.MountKey) }, c.Str(K.ItemRef) + NodeFormats.KeySeparator + mount.Str(SK.MountKey));
                            shelf.Offers.Add(offer);
                        }
                if (services.Contains(SV.ServiceInstall))
                    foreach (var c in Js.Items(CardExtraction.InstallPlan(data, run)[SK.Candidates]).OfType<JObject>())
                        foreach (var mount in Js.Items(c[RK.Mounts]).OfType<JObject>())
                        {
                            var offer = SmithOffer(strings, shelf, NodeValues.OfferInstall, c, stones,
                                strings.Format(NodeStringKeys.NodesMerchantServiceInstall, new StringArgs().Add(NodePlaceholders.Item, c.Str(SK.ItemName))),
                                strings.Format(NodeStringKeys.NodesMerchantServiceInstallBody, new StringArgs().Add(NodePlaceholders.Mount, mount.Str(K.Kind))),
                                new ShopAction { Kind = SV.ActionSmithInstall, ItemRef = c.Str(K.ItemRef), MountKey = mount.Str(SK.MountKey) }, c.Str(K.ItemRef) + NodeFormats.KeySeparator + mount.Str(SK.MountKey));
                            offer.PickTitle = strings.Get(NodeStringKeys.NodesMerchantPickInstall);
                            offer.PrimaryText = strings.Format(NodeStringKeys.NodesMerchantPrimaryInstall, new StringArgs().Add(NodePlaceholders.Cost, NodeText.Number(offer.Price)));
                            foreach (var card in Js.Items(mount[K.Cards]).OfType<JObject>()) offer.Picks.Add(DeckPick(data.Combat, ui, card));
                            shelf.Offers.Add(offer);
                        }
            }
            var open = shelf.Offers.Count(o => o.Available);
            shelf.Count = NodeText.Number(open);
            shelf.Empty = shelf.Offers.Count == 0;
            shelf.Status = shelf.Empty ? strings.Get(NodeStringKeys.NodesMerchantNoServices)
                : strings.Format(NodeStringKeys.NodesMerchantServicesOpen, new StringArgs().Add(NodePlaceholders.N, NodeText.Number(open)).Add(NodePlaceholders.Total, NodeText.Number(shelf.Offers.Count)));
        }

        private static MerchantOffer SmithOffer(StringTable strings, MerchantShelfView shelf, string kind, JObject candidate, double stones, string name, string text, ShopAction action, string key)
        {
            var cost = candidate.Num(K.Cost);
            var offer = new MerchantOffer
            {
                Key = shelf.Id + NodeFormats.KeySeparator + kind + NodeFormats.KeySeparator + key,
                Shelf = shelf.Id,
                Kind = kind,
                Action = action,
                Name = name,
                Text = text,
                KindText = strings.Get(NodeStringKeys.NodesMerchantKindService),
                GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.GlyphKey, NodeValues.OfferArmament),
                Price = cost,
                PaidInStones = true,
                PriceText = strings.Format(NodeStringKeys.NodesMerchantPriceStones, new StringArgs().Add(NodePlaceholders.Cost, NodeText.Number(cost))),
                PrimaryText = strings.Format(NodeStringKeys.NodesMerchantPrimarySmith, new StringArgs().Add(NodePlaceholders.Cost, NodeText.Number(cost))),
                ConfirmId = ConfirmIds.MerchantSmith,
            };
            var shortfall = candidate.Num(SK.Shortfall);
            Availability(strings, offer, candidate.Is(SK.Affordable) ? null : SS.SmithRefusalInsufficientStones, stones, shortfall);
            offer.ConfirmArgs = new StringArgs().Add(NodePlaceholders.Item, candidate.Str(SK.ItemName)).Add(NodePlaceholders.Cost, NodeText.Number(cost))
                .Add(NodePlaceholders.Stones, NodeText.Number(stones)).Add(NodePlaceholders.Left, NodeText.Number(stones - cost));
            return offer;
        }

        /// <summary>A deck card as a pick (its instance id is the pick id).</summary>
        private static RewardPickView DeckPick(CombatData combat, UiData ui, JObject instance)
        {
            var pick = RewardsView.CardPick(combat, ui.Strings, ui.Components.CostKinds, instance.Str(K.CardId), Js.Truthy(instance[K.Upgraded]));
            pick.Id = instance.Str(K.InstanceId);
            return pick;
        }

        // ------------------------------------------------------------------ the sell shelf (US-9.3; D-081)

        private static void Sell(MerchantSession session, UiData ui, JObject run, JObject view, MerchantShelfView shelf)
        {
            var strings = ui.Strings;
            var data = session.Data;
            var combat = data.Combat;
            var cinders = run.Num(RK.Cinders);
            var sell = view.Obj(SK.Sell) ?? new JObject();
            var n = 0;
            foreach (var row in Js.Items(sell[K.Armaments]).OfType<JObject>())
            {
                var id = row.Str(K.Id);
                var def = data.Armament(id) ?? new JObject();
                var offer = SaleOffer(strings, shelf, n++, row.Num(SK.Price), cinders, def.Str(K.Name) ?? id, def.Str(RunFlowKeys.Blurb),
                    strings.Get(NodeStringKeys.NodesMerchantKindArmament), new ShopAction { Kind = SV.ActionSellArmament, Id = id });
                offer.ArtId = RewardsView.ArtFromKey(ui.Components.RewardArmamentArt, def.Str(RunFlowKeys.ArtKey) ?? id, null);
                offer.GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.GlyphKey, NodeValues.OfferArmament);
                Availability(strings, offer, row.Str(SK.Refusal), cinders);
                shelf.Offers.Add(offer);
            }
            foreach (var row in Js.Items(sell[SK.Goods]).OfType<JObject>())
            {
                var id = row.Str(K.Id);
                var relic = row.Str(K.Kind) == SV.KindRelic;
                var def = relic ? (combat.Relics.Has(id) ? combat.Relics.Get(id) : new JObject()) : (combat.Flasks.Has(id) ? combat.Flasks.Get(id) : new JObject());
                var text = relic
                    ? RewardsView.Sentence(combat, def.Str(K.TextTemplate), RewardsView.RelicTokens(combat, def, ui.Components))
                    : RewardsView.Sentence(combat, def.Str(K.TextTemplate), RewardsView.EffectTokens(combat, def[K.Effects]));
                var offer = SaleOffer(strings, shelf, n++, row.Num(SK.Price), cinders, def.Str(K.Name) ?? id, text,
                    strings.Get(relic ? NodeStringKeys.NodesMerchantKindRelic : NodeStringKeys.NodesMerchantKindFlask),
                    new ShopAction { Kind = relic ? SV.ActionSellRelic : SV.ActionSellFlask, Index = (int)row.Num(RK.Index) });
                offer.ArtId = relic
                    ? StringTable.Fill(ui.Components.RewardRelicArt, new StringArgs().Add(UiPlaceholders.Id, id))
                    : RewardsView.ArtFromKey(ui.Components.RewardFlaskArt, def.Str(RunFlowKeys.ArtKey), ui.Components.RewardFlaskArtPrefix);
                offer.GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.GlyphKey, relic ? NodeValues.OfferRelic : NodeValues.OfferFlask);
                shelf.Offers.Add(offer);
            }
            // Buy back (D-159): what was sold this visit, at the price received.
            var back = session.Owner.BuyBackList;
            for (var i = 0; i < back.Count; i++)
            {
                var entry = back[i];
                var id = entry.Str(K.Id);
                var kind = entry.Str(K.Kind);
                var name = kind == SV.KindArmament ? (data.Armament(id)?.Str(K.Name) ?? id)
                    : kind == SV.KindRelic ? (combat.Relics.Has(id) ? combat.Relics.Get(id).Str(K.Name) ?? id : id)
                    : (combat.Flasks.Has(id) ? combat.Flasks.Get(id).Str(K.Name) ?? id : id);
                var price = entry.Num(SK.Price);
                var refusal = Ashen.Domain.Shop.BuyBack.Refusal(data, run, entry);
                var offer = new MerchantOffer
                {
                    Key = shelf.Id + NodeFormats.KeySeparator + SV.ActionBuyBack + NodeFormats.KeySeparator + NodeText.Number(i),
                    Shelf = shelf.Id,
                    Kind = NodeValues.OfferSale,
                    Action = new ShopAction { Kind = SV.ActionBuyBack, Index = i },
                    Name = name,
                    Text = string.Empty,
                    KindText = strings.Get(MetaStringKeys.MerchantBuyBackKind),
                    Price = price,
                    Available = true,
                    PriceText = strings.Format(MetaStringKeys.MerchantBuyBackPrice, new StringArgs().Add(NodePlaceholders.Price, NodeText.Number(price))),
                    PrimaryText = strings.Format(MetaStringKeys.MerchantBuyBackPrimary, new StringArgs().Add(NodePlaceholders.Price, NodeText.Number(price))),
                    ConfirmId = ConfirmIds.MerchantBuy,
                };
                offer.ConfirmArgs = new StringArgs().Add(NodePlaceholders.Item, name).Add(NodePlaceholders.Price, NodeText.Number(price)).Add(NodePlaceholders.Cinders, NodeText.Number(cinders));
                offer.GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.GlyphKey, kind == SV.KindArmament ? NodeValues.OfferArmament : kind == SV.KindRelic ? NodeValues.OfferRelic : NodeValues.OfferFlask);
                Availability(strings, offer, refusal, cinders);
                shelf.Offers.Add(offer);
            }
            var takes = shelf.Offers.Count(o => o.Available);
            shelf.Count = NodeText.Number(takes);
            shelf.Empty = shelf.Offers.Count == 0;
            shelf.Status = shelf.Empty ? strings.Get(NodeStringKeys.NodesMerchantNothingWanted)
                : strings.Format(NodeStringKeys.NodesMerchantWillTake, new StringArgs().Add(NodePlaceholders.N, NodeText.Number(takes)));
            shelf.NoteTitle = strings.Get(NodeStringKeys.NodesMerchantBuyBack);
            shelf.Note = strings.Get(MetaStringKeys.MerchantBuyBackNote);
        }

        private static MerchantOffer SaleOffer(StringTable strings, MerchantShelfView shelf, int n, double price, double cinders, string name, string text, string kindText, ShopAction action)
        {
            var offer = new MerchantOffer
            {
                Key = shelf.Id + NodeFormats.KeySeparator + NodeText.Number(n),
                Shelf = shelf.Id,
                Kind = NodeValues.OfferSale,
                Action = action,
                Name = name,
                Text = text ?? string.Empty,
                KindText = kindText,
                Price = price,
                Sale = true,
                Available = true,
                PriceText = strings.Format(NodeStringKeys.NodesMerchantPriceBack, new StringArgs().Add(NodePlaceholders.Price, NodeText.Number(price))),
                PrimaryText = strings.Format(NodeStringKeys.NodesMerchantPrimarySell, new StringArgs().Add(NodePlaceholders.Price, NodeText.Number(price))),
                ConfirmId = ConfirmIds.MerchantSell,
            };
            offer.ConfirmArgs = new StringArgs().Add(NodePlaceholders.Item, name).Add(NodePlaceholders.Price, NodeText.Number(price)).Add(NodePlaceholders.Cinders, NodeText.Number(cinders));
            return offer;
        }
    }
}
