using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Nodes;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-09 merchant (W1d/W1v workspace with rail; US-9.1–9.3, AF-08), bound to <see cref="MerchantSession"/>: the rail (a
    /// [Cards ▾] selector on narrow or short screens, CategoryNav model) holds the shelves — cards, armaments, weapon arts,
    /// relics, flasks, services, and Sell while the shopSell setting is on; the pane lists the shelf's offers with their
    /// price (struck through with the reason when it cannot be taken: "Need n more cinders"); the detail shows the selected
    /// offer (its card face, or its art and text) with its price. The first press on an offer selects it, a second (or the
    /// footer Primary, which reads "Buy · n cinders" / "Sell · n cinders" / "Smith · n ⬡") opens its W2a door; a burn or an
    /// install first opens a card pick. Every landed step is saved; a refusal shows at the control that asked. [Leave] and
    /// [×] leave (the stock is cleared). The RUN_HUD shows the purse. Escape backs out of the pick, else opens W-20.
    /// </summary>
    public sealed class MerchantScreen : NodeScreen
    {
        private readonly List<Button> _tiles = new List<Button>();
        private readonly List<VisualElement> _pickCards = new List<VisualElement>();
        private MerchantSession _merchant;
        private MerchantViewState _view;
        private Shell _shell;
        private Workspace _workspace;
        private string _shelf;
        private string _selected;
        private MerchantOffer _picking;
        private string _pick;
        private string _categories;

        public MerchantSession Merchant => _merchant;
        public MerchantViewState View => _view;
        public IReadOnlyList<Button> Tiles => _tiles;
        public IReadOnlyList<VisualElement> PickCards => _pickCards;
        public string ShelfId => _shelf;
        public string Selected => _selected;
        public MerchantOffer Picking => _picking;
        public string Pick => _pick;
        public LocButton PrimaryButton => _shell?.PrimaryButton;
        public LocButton LeaveButton => _shell?.BackButton;
        public LocButton ConfirmPickButton => Root.Q<LocButton>(UiNames.PickConfirm);
        public MerchantShelfView Shelf => _view?.Shelf(_shelf);

        public override string FocusMode => _picking != null ? UiValues.FocusPick : null;

        protected override void Begin()
        {
            _merchant = MerchantSession.Start(Session);
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.SetFooter(NodeStringKeys.NodesMerchantLeave, NodeStringKeys.NodesMerchantPrimaryNone);
            _shell.Back += LeaveMerchant;
            _shell.Exit += LeaveMerchant;
            _shell.Primary += Primary;
            _workspace = Root.Q<Workspace>(UiNames.Workspace);
            if (_workspace != null)
            {
                _workspace.Rules = Ui.Data.Layout.CategoryNav;
                _workspace.Picked += ShowShelf;
            }
            Root.Q<LocButton>(UiNames.PickBack).clicked += LeavePick;
            var confirm = ConfirmPickButton;
            confirm.clicked += () => ConfirmPick(confirm);
        }

        // ------------------------------------------------------------------ render

        public override void Render()
        {
            if (_merchant == null || Session.Location != RunFlowValues.LocationMerchant) return;
            var keep = Context.Instance.Focus.Focused?.name;
            _view = MerchantView.Build(_merchant, Ui.Data);
            ScaleRailRules();
            if (_shelf == null || _view.Shelf(_shelf) == null) _shelf = _view.Shelves.FirstOrDefault()?.Id;
            if (_selected != null && _view.Offer(_selected) == null) _selected = null;
            RenderHud();
            RenderCategories();
            RenderShelf();
            RenderDetail();
            RenderPrimary();
            var picking = _picking != null;
            UiDom.Show(_workspace, !picking);
            UiDom.Show(Root.Q(UiNames.MerchantPick), picking);
            UiDom.Show(_shell.Footer, !picking);
            if (picking) RenderPick();
            if (keep != null)
            {
                var again = Root.Q(keep);
                if (again != null && again.focusable && UiDom.IsShown(again)) again.Focus();
            }
        }

        /// <summary>
        /// The rail's tap floor in reference px is the touch minimum after scaling (ui/layout.json minPhysical), so a short
        /// screen (844×390) folds the seven shelves into the [Cards ▾] selector, as 04 W-09 draws.
        /// </summary>
        private void ScaleRailRules()
        {
            var layout = Nav.Layout;
            var rules = Ui.Data.Layout.CategoryNav;
            if (_workspace == null || layout == null || rules == null) return;
            _workspace.Rules = new CategoryNavRules
            {
                RailWidth = rules.RailWidth,
                MinPaneWidth = rules.MinPaneWidth,
                TapFloor = Math.Max(rules.TapFloor, layout.ReferenceMinimum(Ui.Data.Layout.MinPhysicalOf(UiKeys.Touch))),
                Chrome = rules.Chrome,
                Hysteresis = rules.Hysteresis,
            };
        }

        private void RenderCategories()
        {
            var items = _view.Shelves.Select(s => new CategoryItem { Id = s.Id, LabelKey = s.LabelKey, Count = s.Count }).ToList();
            var signature = string.Join(NodeFormats.KeySeparator, items.Select(i => i.Id + NodeFormats.KeySeparator + i.Count)) + NodeFormats.KeySeparator + _shelf;
            if (signature == _categories) return;
            _categories = signature;
            _workspace?.SetCategories(items, _shelf);
        }

        private void RenderShelf()
        {
            var shelf = Shelf;
            var list = Root.Q(UiNames.ShelfList);
            list.Clear();
            _tiles.Clear();
            Root.Q<LocLabel>(UiNames.ShelfStatus)?.SetResolved(shelf?.Status ?? string.Empty);
            if (shelf == null) return;
            var scroll = Root.Q<ScrollView>(UiNames.ShelfScroll);
            foreach (var offer in shelf.Offers)
            {
                var tile = new Button { name = offer.Key };
                tile.AddToClassList(UiClasses.Button);
                tile.AddToClassList(UiClasses.Touch);
                tile.AddToClassList(NodeClasses.ShelfTile);
                tile.EnableInClassList(NodeClasses.ShelfTileUnavailable, !offer.Available);
                tile.EnableInClassList(NodeClasses.ShelfTileSelected, offer.Key == _selected);
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList(NodeClasses.TileIcon);
                if (!Art(icon, offer.ArtId))
                {
                    var glyph = new LocLabel(Strings.Has(offer.GlyphKey) ? offer.GlyphKey : StringKeys.GlyphCinders) { pickingMode = PickingMode.Ignore };
                    glyph.AddToClassList(NodeClasses.TileGlyph);
                    icon.Add(glyph);
                }
                tile.Add(icon);
                var texts = new VisualElement { pickingMode = PickingMode.Ignore };
                texts.AddToClassList(NodeClasses.TileTexts);
                texts.Add(Text(offer.Name, NodeClasses.TileName, UiClasses.ValueText));
                texts.Add(Text(offer.KindText, NodeClasses.TileKind, UiClasses.HeaderText));
                if (!offer.Available) texts.Add(Text(offer.ReasonText, NodeClasses.TileReason, UiClasses.ValueText));
                tile.Add(texts);
                var price = offer.Available ? offer.PriceText : string.Format(CultureInfo.InvariantCulture, NodeFormats.Strike, offer.PriceText);
                tile.Add(Text(price, NodeClasses.TilePrice, UiClasses.HeaderText));
                var key = offer.Key;
                tile.clicked += () => Press(key);
                tile.RegisterCallback<FocusInEvent>(_ => scroll?.ScrollTo(tile));
                list.Add(tile);
                _tiles.Add(tile);
            }
            var note = Root.Q(UiNames.ShelfNote);
            UiDom.Show(note, shelf.Note != null);
            Root.Q<LocLabel>(UiNames.NoteTitle)?.SetResolved(shelf.NoteTitle ?? string.Empty);
            Root.Q<LocLabel>(UiNames.NoteText)?.SetResolved(shelf.Note ?? string.Empty);
        }

        private static LocLabel Text(string value, params string[] classes)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) label.AddToClassList(c);
            label.SetResolved(value ?? string.Empty);
            return label;
        }

        private void RenderDetail()
        {
            var offer = _selected == null ? null : _view.Offer(_selected);
            var figure = Root.Q(UiNames.DetailFigure);
            var host = Root.Q(UiNames.DetailCardHost);
            host.Clear();
            if (offer?.Card != null)
            {
                var card = new CardView();
                card.focusable = false;
                card.AddToClassList(NodeClasses.DetailCard);
                card.Bind(CardData(offer.Card, offer.Available, offer.ReasonText));
                host.Add(card);
            }
            UiDom.Show(host, offer?.Card != null);
            var hasArt = offer != null && offer.Card == null && Art(figure, offer.ArtId);
            if (offer == null) hasArt = Art(figure, Ui.Data.Nodes.MerchantFigure);
            UiDom.Show(figure, hasArt);
            Root.Q<LocLabel>(UiNames.DetailKind)?.SetResolved(offer?.KindText ?? string.Empty);
            Root.Q<LocLabel>(UiNames.DetailTitle)?.SetResolved(offer?.Name ?? _view.Title);
            var text = Root.Q<LocLabel>(UiNames.DetailText);
            text?.SetResolved(offer == null ? Strings.Get(NodeStringKeys.NodesMerchantDetailNone) : offer.Text ?? string.Empty);
            UiDom.Show(text, offer?.Card == null);
            Root.Q<LocLabel>(UiNames.DetailPrice)?.SetResolved(offer?.PriceText ?? string.Empty);
            var reason = Root.Q<LocLabel>(UiNames.DetailReason);
            reason?.SetResolved(offer?.ReasonText ?? string.Empty);
            UiDom.Show(reason, offer != null && !offer.Available);
        }

        private static CardViewData CardData(RewardPickView pick, bool affordable, string refusal) => new CardViewData
        {
            Name = pick.Name,
            Text = pick.Text,
            TypeLine = pick.TypeLine,
            Rarity = pick.Rarity,
            Upgraded = pick.Upgraded,
            Affordable = affordable,
            RefusalText = refusal,
            Costs = pick.Costs.Select(c => new CardCostData { Kind = c.Kind, Text = c.Text }).ToList(),
        };

        private void RenderPrimary()
        {
            var offer = _selected == null ? null : _view.Offer(_selected);
            var primary = PrimaryButton;
            primary.SetResolved(offer?.PrimaryText ?? Strings.Get(NodeStringKeys.NodesMerchantPrimaryNone));
            primary.EnableInClassList(UiClasses.ButtonReady, offer != null && offer.Available);
        }

        private void RenderPick()
        {
            var offer = _view.Offer(_picking.Key) ?? _picking;
            _picking = offer;
            Root.Q<LocLabel>(UiNames.PickTitle)?.SetResolved(offer.PickTitle ?? offer.Name);
            var cards = Root.Q(UiNames.PickCards);
            cards.Clear();
            _pickCards.Clear();
            foreach (var pick in offer.Picks)
            {
                var id = pick.Id;
                var card = new CardView { name = id };
                card.AddToClassList(UiClasses.PickCard);
                card.Bind(CardData(pick, true, null));
                card.RegisterCallback<PointerUpEvent>(_ => Choose(id));
                card.RegisterCallback<NavigationSubmitEvent>(e =>
                {
                    if (e.target != card) return;
                    Choose(id);
                    e.StopPropagation();
                });
                card.EnableInClassList(UiClasses.CardChosen, id == _pick);
                card.EnableInClassList(UiClasses.PickSelected, id == _pick);
                cards.Add(card);
                _pickCards.Add(card);
            }
            ConfirmPickButton.EnableInClassList(UiClasses.ButtonReady, _pick != null);
        }

        // ------------------------------------------------------------------ intents

        /// <summary>A rail pick: the shelf's offers, focus on the first one.</summary>
        public void ShowShelf(string id)
        {
            if (_view?.Shelf(id) == null) return;
            _shelf = id;
            _selected = null;
            Render();
            FocusSoon(_tiles.FirstOrDefault() ?? (VisualElement)PrimaryButton);
        }

        /// <summary>The first press selects an offer; a second press on the selected one acts like the footer Primary.</summary>
        public void Press(string key)
        {
            if (_selected == key)
            {
                Primary();
                return;
            }
            Select(key);
        }

        public void Select(string key)
        {
            if (_view?.Offer(key) == null) return;
            _selected = key;
            Render();
            var tile = _tiles.FirstOrDefault(t => t.name == key);
            if (tile != null) tile.Focus();
        }

        /// <summary>Buy / Sell / Smith / Choose a card on the selected offer; an offer that cannot be taken is refused at the control.</summary>
        public void Primary()
        {
            var anchor = (VisualElement)_tiles.FirstOrDefault(t => t.name == _selected) ?? PrimaryButton;
            if (Context.Instance.Focus.Focused == PrimaryButton) anchor = PrimaryButton;
            var offer = _selected == null ? null : _view.Offer(_selected);
            if (offer == null)
            {
                Refuse(PrimaryButton, Strings.Get(NodeStringKeys.NodesMerchantPrimaryNone));
                return;
            }
            if (!offer.Available)
            {
                Refuse(anchor, offer.ReasonText);
                return;
            }
            if (offer.NeedsPick)
            {
                OpenPick(offer);
                return;
            }
            Door(offer, null, anchor);
        }

        private void OpenPick(MerchantOffer offer)
        {
            _picking = offer;
            _pick = null;
            Render();
            FocusSoon(_pickCards.FirstOrDefault() ?? (VisualElement)Root.Q(UiNames.PickBack));
        }

        /// <summary>Selects a card in the pick (first press selects; Confirm opens the door).</summary>
        public void Choose(string id)
        {
            if (_picking == null) return;
            _pick = id;
            Render();
        }

        private void ConfirmPick(VisualElement anchor)
        {
            if (_picking == null) return;
            if (_pick == null)
            {
                Refuse(anchor, Strings.Get(NodeStringKeys.NodesMerchantPickPrompt));
                return;
            }
            Door(_picking, _pick, anchor);
        }

        public void LeavePick()
        {
            var key = _picking?.Key;
            _picking = null;
            _pick = null;
            Render();
            FocusSoon((VisualElement)_tiles.FirstOrDefault(t => t.name == key) ?? PrimaryButton);
        }

        /// <summary>The offer's W2a door (focus starts on Back); its primary takes the step.</summary>
        private void Door(MerchantOffer offer, string pick, VisualElement anchor)
        {
            var name = pick != null ? offer.Picks.FirstOrDefault(p => p.Id == pick)?.Name ?? offer.Name : offer.Name;
            var args = offer.ConfirmArgs;
            if (pick != null) args.Add(NodePlaceholders.Item, name);
            ConfirmRequest.Open(Nav, new ConfirmRequest
            {
                ConfirmId = offer.ConfirmId,
                Args = args,
                Target = name,
                OnConfirm = () => Take(offer, pick, anchor),
            });
        }

        /// <summary>Takes the step through the session (saved at once); a refusal shows at the control.</summary>
        public void Take(MerchantOffer offer, string pick, VisualElement anchor = null)
        {
            var action = new ShopAction
            {
                Kind = offer.Action.Kind,
                Index = offer.Action.Index,
                Id = offer.Action.Id,
                ItemRef = offer.Action.ItemRef,
                MountKey = offer.Action.MountKey,
                InstanceId = pick ?? offer.Action.InstanceId,
            };
            ShopResult result = null;
            if (!Guard(() => result = _merchant.Execute(action), () => Take(offer, pick, anchor))) return;
            var index = _tiles.FindIndex(t => t.name == offer.Key);
            if (!result.Ok)
            {
                Render();
                Refuse(anchor ?? PrimaryButton, NodeText.Refusal(Strings, result.Refusal));
                return;
            }
            _picking = null;
            _pick = null;
            _selected = null;
            Render();
            FocusSoon(_tiles.Count > 0 ? _tiles[Math.Min(Math.Max(index, 0), _tiles.Count - 1)] : (VisualElement)PrimaryButton);
        }

        private void LeaveMerchant()
        {
            if (!Guard(_merchant.Leave, LeaveMerchant)) return;
            Follow();
        }

        public override bool HandleBack()
        {
            if (_picking != null)
            {
                LeavePick();
                return true;
            }
            return base.HandleBack();
        }

        // ------------------------------------------------------------------ review hooks (captures)

        /// <summary>Shows a shelf and selects its first offer (or the first unavailable one) for a capture.</summary>
        public void ReviewShelf(string shelf, bool unavailable)
        {
            ShowShelf(shelf);
            var offer = Shelf?.Offers.FirstOrDefault(o => unavailable ? !o.Available : o.Available) ?? Shelf?.Offers.FirstOrDefault();
            if (offer != null) Select(offer.Key);
        }
    }
}
