using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Generated;

namespace Ashen.App.Run
{
    /// <summary>Which layer of the shop screen is open: the shelves, an item's inspection, or the question before spending.</summary>
    public enum Core2ShopStage
    {
        Shelf,
        Inspect,
        Confirm,
    }

    /// <summary>A shelf's tab: the kind, its label (ARMAMENTS, RELICS, CARDS...) and whether it is the one shown.</summary>
    public sealed class Core2ShopTab
    {
        public string Kind;
        public string Label;
        public bool Selected;
    }

    /// <summary>One offer on a shelf as the screen draws it: its face, the price and the Buy words, and whether Buy can be taken now (with the reason it cannot).</summary>
    public sealed class Core2ShopItem
    {
        public int Index;
        public Core2ItemFace Face;
        public string Name;
        public string PriceText;
        public string BuyText;
        public bool Taken;
        public bool Available;

        /// <summary>Why Buy cannot be taken (the web's reason under the offer); empty when it can.</summary>
        public string Reason = string.Empty;
    }

    /// <summary>What a press of the shop screen did: a refusal in the web's words (shown at the control; nothing changed), or whether the shop has been left.</summary>
    public sealed class Core2ShopStep
    {
        public string Refusal;
        public bool Left;
        public bool Done => Refusal == null;
    }

    /// <summary>
    /// The shop screen's logic (VP-9, D-952; web ui/screens/shop.js mountConfiguredShop, merchantOffer.js and the shop-inspect and shop-review doors): the purse, a tab per shelf in the order the
    /// stock lists them, the offers of the shelf shown (a card as the VP-3 face, anything else as a collectible with its name, one line and Inspect), an inspection of one item, and the
    /// question asked before spending (Buy this item? Spend 10 of your 25 cinders (15 left).). A Buy that cannot be taken refuses at the control with the web's reason; a purchase is
    /// <see cref="Core2RunSession.BuyFromShop"/> (saved at once) and Leave is <see cref="Core2RunSession.LeaveShop"/>. Engine-free; the screen only draws it and sends presses here.
    /// </summary>
    public sealed class Core2ShopFlow
    {
        private readonly Core2RunSession _session;
        private readonly UiData _ui;
        private readonly StringTable _strings;
        private readonly Dictionary<int, Core2ItemFace> _faces = new Dictionary<int, Core2ItemFace>();
        private string _active;
        private int? _focus;

        public Core2ShopFlow(Core2RunSession session, UiData ui)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _ui = ui;
            _strings = ui.Strings;
            Refresh();
        }

        public Core2ShopViewState View { get; private set; }

        public Core2ShopStage Stage { get; private set; }

        public string Title => View?.Title ?? string.Empty;

        public string LeaveText => View?.LeaveText ?? string.Empty;

        /// <summary>The purse as the HUD says it ("25 cinders").</summary>
        public string PurseText => View?.PurseText ?? string.Empty;

        public IReadOnlyList<Core2ShopTab> Tabs { get; private set; } = Array.Empty<Core2ShopTab>();

        public string ActiveKind => _active;

        /// <summary>The shelf's name over its offers (the active tab's label).</summary>
        public string Heading { get; private set; } = string.Empty;

        public IReadOnlyList<Core2ShopItem> Items { get; private set; } = Array.Empty<Core2ShopItem>();

        /// <summary>The item under inspection or confirmation, or null.</summary>
        public Core2ShopItem Focus => _focus.HasValue ? AllItems.FirstOrDefault(i => i.Index == _focus.Value) : null;

        public IReadOnlyList<Core2ShopItem> AllItems { get; private set; } = Array.Empty<Core2ShopItem>();

        public string InspectLabel => _strings.Get(StringKeys.Core2ShopInspect);

        /// <summary>The Buy button's word; its price is said under it (the web's Buy with 10 cinders below).</summary>
        public string BuyLabel => _strings.Get(StringKeys.Core2ShopBuyShort);

        public string EmptyText => _strings.Get(StringKeys.Core2ShopEmpty);

        public string BackLabel => _strings.Get(StringKeys.Core2ShopBack);

        public string ConfirmQuestion { get; private set; } = string.Empty;

        public string ConfirmMessage { get; private set; } = string.Empty;

        public string ConfirmButton { get; private set; } = string.Empty;

        /// <summary>The shop has nothing on its shelves (a stock whose every shelf the profile left empty).</summary>
        public bool Empty => AllItems.Count == 0;

        // ------------------------------------------------------------------ the view

        /// <summary>Reads the shop again from the run (after a purchase the item reads Unavailable and the purse is lower).</summary>
        public void Refresh()
        {
            View = Core2ShopView.Build(_session, _strings);
            if (View == null) return;
            var all = new List<Core2ShopItem>();
            foreach (var shelf in View.Shelves)
                foreach (var offer in shelf.Offers)
                {
                    if (!_faces.TryGetValue(offer.Index, out var face))
                    {
                        face = Core2ItemFaces.Build(_session, _ui, offer.Kind, offer.Id, offer.Name);
                        _faces[offer.Index] = face;
                    }
                    all.Add(new Core2ShopItem
                    {
                        Index = offer.Index, Face = face, Name = offer.Name, PriceText = offer.PriceText, BuyText = offer.BuyText, Taken = offer.Taken, Available = offer.Available,
                        Reason = offer.Available ? string.Empty : Reason(offer, shelf.Kind),
                    });
                }
            AllItems = all;
            if (_active == null || View.Shelves.All(s => s.Kind != _active)) _active = View.Shelves.FirstOrDefault()?.Kind;
            Tabs = View.Shelves.Select(s => new Core2ShopTab { Kind = s.Kind, Label = s.Label, Selected = s.Kind == _active }).ToList();
            Heading = View.Shelves.FirstOrDefault(s => s.Kind == _active)?.Label ?? string.Empty;
            Items = View.Shelves.FirstOrDefault(s => s.Kind == _active)?.Offers.Select(o => all.First(i => i.Index == o.Index)).ToList() ?? new List<Core2ShopItem>();
            var focus = Focus;
            if (focus == null) { _focus = null; Stage = Core2ShopStage.Shelf; }
            else FillConfirm(focus);
        }

        /// <summary>
        /// The reason a Buy cannot be taken, in the web's words: a taken item is "Unavailable", a purse that falls short "Not enough cinders", and a flask with no room for it the purchase's own
        /// sentence ("Potion slots are full") — the web's button says "Not enough cinders" for that too, which tells the player nothing.
        /// </summary>
        private string Reason(Core2ShopOfferView offer, string kind)
        {
            if (offer.Taken) return _strings.Get(StringKeys.Core2ShopAvailLocked);
            if (View.Cinders < offer.Price) return _strings.Get(StringKeys.Core2ShopAvailCinders);
            return Core2RewardsMessages.PotionSlotsFull;
        }

        private void FillConfirm(Core2ShopItem item)
        {
            var offer = View.Shelves.SelectMany(s => s.Offers).First(o => o.Index == item.Index);
            ConfirmQuestion = offer.ConfirmQuestion;
            ConfirmMessage = offer.ConfirmMessage;
            ConfirmButton = offer.ConfirmButton;
        }

        // ------------------------------------------------------------------ presses

        /// <summary>A tab was chosen: its shelf shows.</summary>
        public Core2ShopStep SelectShelf(string kind)
        {
            if (Stage != Core2ShopStage.Shelf || View == null || View.Shelves.All(s => s.Kind != kind)) return new Core2ShopStep();
            _active = kind;
            Refresh();
            return new Core2ShopStep();
        }

        /// <summary>An item's Inspect: the web's inspection door over the shelf.</summary>
        public Core2ShopStep Inspect(int index)
        {
            if (Stage != Core2ShopStage.Shelf) return new Core2ShopStep();
            var item = AllItems.FirstOrDefault(i => i.Index == index);
            if (item == null) return new Core2ShopStep { Refusal = _strings.Get(StringKeys.Core2ShopAvailLocked) };
            _focus = index;
            Stage = Core2ShopStage.Inspect;
            Refresh();
            return new Core2ShopStep();
        }

        /// <summary>Buy was pressed: an offer that cannot be taken refuses with its reason, anything else asks before spending.</summary>
        public Core2ShopStep RequestBuy(int index)
        {
            if (Stage == Core2ShopStage.Confirm) return new Core2ShopStep();
            var item = AllItems.FirstOrDefault(i => i.Index == index);
            if (item == null) return new Core2ShopStep { Refusal = _strings.Get(StringKeys.Core2ShopAvailLocked) };
            if (!item.Available) return new Core2ShopStep { Refusal = item.Reason };
            _focus = index;
            Stage = Core2ShopStage.Confirm;
            Refresh();
            return new Core2ShopStep();
        }

        /// <summary>The question's BUY IT: the price comes off and the item joins the run (saved at once); a refusal is the purchase's own sentence and nothing changes.</summary>
        public Core2ShopStep ConfirmBuy()
        {
            if (Stage != Core2ShopStage.Confirm || !_focus.HasValue) return new Core2ShopStep();
            var result = _session.BuyFromShop(_focus.Value);
            if (!result.Done)
            {
                Stage = Core2ShopStage.Shelf;
                _focus = null;
                Refresh();
                return new Core2ShopStep { Refusal = result.Refusal };
            }
            Stage = Core2ShopStage.Shelf;
            _focus = null;
            Refresh();
            return new Core2ShopStep();
        }

        /// <summary>Back, the close glyph or Escape: the question or the inspection closes (nothing is spent).</summary>
        public Core2ShopStep CloseOverlay()
        {
            if (Stage == Core2ShopStage.Shelf) return new Core2ShopStep();
            Stage = Core2ShopStage.Shelf;
            _focus = null;
            return new Core2ShopStep();
        }

        /// <summary>Leave: the open marker goes (the stock keeps what was taken) and the run is back at the map.</summary>
        public Core2ShopStep Leave()
        {
            if (Stage != Core2ShopStage.Shelf) return new Core2ShopStep();
            var result = _session.LeaveShop();
            return result.Done ? new Core2ShopStep { Left = true } : new Core2ShopStep { Refusal = result.Refusal };
        }
    }
}
