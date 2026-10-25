using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Core 2 shop screen (VP-9, D-952; web ui/screens/shop.js mountConfiguredShop, components/merchantOffer.js and the shop inspection and review doors; docs/qa/web-screens/shop-*,
    /// master-merchant*): the merchant's tent under the board HUD with the merchant standing left, and the frame on the right: Merchant with a close glyph that leaves, the shelf's name, one row per
    /// offer (its picture or card face with Inspect, the name in gold, one line, Buy and its price), the dock of shelf tabs and Leave. Inspect opens the item over the room (a card as its large
    /// face; anything else with its picture, its words, an armament's numbers and the weapon art it grants). Buy asks "Buy this item?" with the purse before and after and BUY IT; a Buy that cannot
    /// be taken refuses at the button with the web's reason (Unavailable, Not enough cinders). Every press goes through <see cref="Core2ShopFlow"/>; a purchase is saved at once by the session and
    /// Leave returns to the map. Escape closes the modal, else opens the pause screen.
    /// </summary>
    public sealed class Core2ShopScreen : ScreenView
    {
        private readonly Dictionary<int, LocButton> _media = new Dictionary<int, LocButton>();
        private readonly Dictionary<int, HoldButton> _buy = new Dictionary<int, HoldButton>();
        private readonly List<LocButton> _tabs = new List<LocButton>();
        private Core2RunSession _run;
        private Core2ShopFlow _flow;
        private CombatModal _frame;
        private CombatModal _modal;
        private VisualElement _scrim;
        private LocButton _leave;
        private LocButton _modalBack;
        private LocButton _modalConfirm;
        private int _opener = -1;
        private bool _openerBuy;
        private string _focusTab;
        private bool _bound;

        public Core2RunSession Run => _run;

        public Core2ShopFlow Flow => _flow;

        public IReadOnlyDictionary<int, HoldButton> BuyButtons => _buy;

        public IReadOnlyDictionary<int, LocButton> InspectButtons => _media;

        public IReadOnlyList<LocButton> TabButtons => _tabs;

        public LocButton LeaveButton => _leave;

        public LocButton ModalBackButton => _modalBack;

        public LocButton ModalConfirmButton => _modalConfirm;

        public override string FocusMode => _flow != null && _flow.Stage != Core2ShopStage.Shelf ? RestShopValues.FocusModal : null;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            _run = (args as Core2RunArgs)?.Session ?? Ui.Core2Session;
            if (_run == null || !_run.HasOpenShop)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            Ui.Core2Session = _run;
            Ui.Session = null;
            _flow = new Core2ShopFlow(_run, Ui.Data);
            _frame = Root.Q<CombatModal>(UiNames.CshopFrame);
            _modal = Root.Q<CombatModal>(UiNames.CshopModal);
            _scrim = Root.Q(UiNames.CshopScrim);
            _frame.CloseRequested += () => Press(_flow.Leave, _frame.CloseButton);
            _modal.CloseRequested += () => Press(_flow.CloseOverlay, _modal.CloseButton);
            _scrim.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == _scrim) Press(_flow.CloseOverlay, null);
            });
            BoardHud.Wire(Context, _run, OpenPause);
            ApplyScene();
            Render();
            _bound = true;
        }

        // ------------------------------------------------------------------ the room

        private void ApplyScene()
        {
            var scene = Root.Q(UiNames.CshopScene);
            BoardHud.SetImage(scene, BoardHud.Art(Ui, ArtKinds.Scene, RestShopArt.ShopScene));
            var portrait = Root.Q(UiNames.CshopPortrait);
            var texture = BoardHud.Art(Ui, ArtKinds.Illustration, RestShopArt.ShopPortrait);
            BoardHud.SetImage(portrait, texture);
            scene.RegisterCallback<GeometryChangedEvent>(_ => LayoutPortrait(scene, portrait, texture));
        }

        /// <summary>The merchant stands bottom-left at the web's share of the room, fitted to his picture's own proportions (servicePortrait: 43% wide, 92% high, contained at left and bottom).</summary>
        private void LayoutPortrait(VisualElement scene, VisualElement portrait, Texture2D texture)
        {
            var width = scene.layout.width;
            var height = scene.layout.height;
            if (texture == null || float.IsNaN(width) || float.IsNaN(height) || height <= 0f) return;
            var tokens = Ui.Data.Tokens;
            var boxW = width * (float)tokens.Ratio(RatioKeys.ShopPortraitW);
            var boxH = height * (float)tokens.Ratio(RatioKeys.ShopPortraitH);
            var scale = Mathf.Min(boxW / texture.width, boxH / texture.height);
            portrait.style.width = texture.width * scale;
            portrait.style.height = texture.height * scale;
        }

        // ------------------------------------------------------------------ render

        /// <summary>Draws the frame and, when one is open, the modal; focus goes to the control a player most likely wants next.</summary>
        public void Render()
        {
            if (_flow == null) return;
            BoardHud.Render(Context, _run);
            _media.Clear();
            _buy.Clear();
            _tabs.Clear();
            _leave = _modalBack = _modalConfirm = null;
            RenderFrame();
            RenderModal();
        }

        private void RenderFrame()
        {
            _frame.Reset();
            _frame.SetHead(null, _flow.Title);
            var body = _frame.Body;
            if (_flow.Empty) body.Add(Text(_flow.EmptyText, ShopClasses.Empty));
            else
            {
                body.Add(Text(_flow.Heading, ShopClasses.Heading, UiClasses.Upper));
                foreach (var item in _flow.Items) body.Add(Offer(item));
            }
            var dock = new VisualElement();
            dock.AddToClassList(ShopClasses.Dock);
            foreach (var tab in _flow.Tabs) dock.Add(Tab(tab));
            _frame.Foot.Add(dock);
            _leave = new LocButton();
            _leave.AddToClassList(ShopClasses.Leave);
            _leave.SetResolved(_flow.LeaveText);
            _leave.clicked += () => Press(_flow.Leave, _leave);
            _frame.Foot.Add(_leave);
            if (_flow.Stage == Core2ShopStage.Shelf) Focus(Resume());
        }

        /// <summary>Where focus lands after a draw: the control the last press came from (the tab, the item's Inspect or Buy), else the first Buy that can be pressed, else Leave.</summary>
        private VisualElement Resume()
        {
            if (_focusTab != null) return _tabs.FirstOrDefault(t => (string)t.userData == _focusTab) ?? (VisualElement)_leave;
            if (_opener >= 0)
            {
                var dict = _openerBuy ? _buy : _media;
                if (dict.TryGetValue(_opener, out var button)) return button;
            }
            return _flow.Items.Where(i => i.Available).Select(i => (VisualElement)_buy[i.Index]).FirstOrDefault() ?? (_tabs.FirstOrDefault(t => t.ClassListContains(ShopClasses.TabOn)) ?? _leave);
        }

        private VisualElement Offer(Core2ShopItem item)
        {
            var row = new VisualElement();
            row.AddToClassList(ShopClasses.Offer);
            row.EnableInClassList(ShopClasses.OfferTaken, item.Taken);
            var media = new LocButton { userData = item.Index };
            media.AddToClassList(ShopClasses.Media);
            media.Add(item.Face.IsCard ? CardFace(item) : Art(item.Face, ShopClasses.MediaArt));
            media.Add(Text(_flow.InspectLabel, ShopClasses.MediaCaption));
            media.clicked += () =>
            {
                _opener = item.Index;
                _openerBuy = false;
                _focusTab = null;
                Press(() => _flow.Inspect(item.Index), media);
            };
            _media[item.Index] = media;
            row.Add(media);
            var details = new VisualElement { pickingMode = PickingMode.Ignore };
            details.AddToClassList(ShopClasses.Details);
            details.Add(Text(item.Name, ShopClasses.Name));
            if (!string.IsNullOrEmpty(item.Face.Effect)) details.Add(Text(item.Face.Effect, ShopClasses.Text));
            row.Add(details);
            var actions = new VisualElement { pickingMode = PickingMode.Ignore };
            actions.AddToClassList(ShopClasses.Actions);
            // The web's Buy is a hold button: a tap asks "Buy this item?", holding it buys at once (holdConfirm; with holds off, or an offer that cannot be bought, a press only asks or refuses).
            var buy = new HoldButton { userData = item.Index };
            buy.AddToClassList(UiClasses.ButtonReady);
            buy.AddToClassList(ShopClasses.Buy);
            buy.EnableInClassList(ShopClasses.BuyOff, !item.Available);
            buy.labelKey = null;
            buy.Q<LocLabel>(UiNames.HoldLabel)?.SetResolved(_flow.BuyLabel);
            buy.HoldMs = Ui.Data.Tokens.Duration(TokenKeys.HoldConfirm);
            buy.HoldEnabled = item.Available && _run.PlayerSettings.Text(SettingIds.HoldConfirm) != SettingIds.HoldOff;
            var held = false;
            buy.clicked += () =>
            {
                // The release that ends a completed hold also clicks: the purchase has been made, so it asks nothing.
                if (held)
                {
                    held = false;
                    return;
                }
                _opener = item.Index;
                _openerBuy = true;
                _focusTab = null;
                Press(() => _flow.RequestBuy(item.Index), buy);
            };
            if (buy.HoldEnabled)
                buy.Committed += () =>
                {
                    held = true;
                    _opener = item.Index;
                    _openerBuy = true;
                    _focusTab = null;
                    Press(() => _flow.BuyNow(item.Index), buy);
                };
            _buy[item.Index] = buy;
            actions.Add(buy);
            actions.Add(Text(item.PriceText, ShopClasses.Price));
            if (!item.Available && !string.IsNullOrEmpty(item.Reason)) actions.Add(Text(item.Reason, ShopClasses.Reason));
            row.Add(actions);
            return row;
        }

        private VisualElement CardFace(Core2ShopItem item)
        {
            var card = ItemFaces.Card(item.Face.Card);
            card.AddToClassList(ShopClasses.MediaCard);
            card.pickingMode = PickingMode.Ignore;
            return card;
        }

        private VisualElement Art(Core2ItemFace face, string cls)
        {
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList(cls);
            BoardHud.SetImage(art, (face.ArtId != null ? Ui.Texture(face.ArtId) : null) ?? Ui.Icon(SpoilsOption.KindIcon(face.Kind)));
            return art;
        }

        private LocButton Tab(Core2ShopTab tab)
        {
            var button = new LocButton { userData = tab.Kind };
            button.AddToClassList(ShopClasses.Tab);
            button.AddToClassList(UiClasses.Upper);
            button.EnableInClassList(ShopClasses.TabOn, tab.Selected);
            button.SetResolved(tab.Label);
            button.clicked += () =>
            {
                _focusTab = tab.Kind;
                _opener = -1;
                Press(() => _flow.SelectShelf(tab.Kind), button);
            };
            _tabs.Add(button);
            return button;
        }

        private static LocLabel Text(string value, params string[] classes)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) label.AddToClassList(c);
            label.SetResolved(value ?? string.Empty);
            return label;
        }

        private LocButton Foot(string text, string kind, Action press)
        {
            var button = new LocButton();
            if (kind != null) button.AddToClassList(kind);
            button.SetResolved(text);
            button.clicked += press;
            _modal.Foot.Add(button);
            return button;
        }

        // ------------------------------------------------------------------ the modal

        private void RenderModal()
        {
            var open = _flow.Stage != Core2ShopStage.Shelf && _flow.Focus != null;
            UiDom.Show(_scrim, open);
            _modal.Reset();
            if (!open) return;
            if (_flow.Stage == Core2ShopStage.Inspect) RenderInspect(_flow.Focus);
            else RenderConfirm(_flow.Focus);
        }

        private void RenderInspect(Core2ShopItem item)
        {
            var face = item.Face;
            _modal.SetHead(null, item.Name);
            var column = new VisualElement();
            column.AddToClassList(ShopClasses.Inspect);
            if (face.IsCard)
            {
                var card = ItemFaces.Card(face.Card);
                card.AddToClassList(ShopClasses.InspectCard);
                column.Add(card);
            }
            else
            {
                column.Add(Text(face.KindLabel, UiClasses.Chip));
                column.Add(Art(face, ShopClasses.InspectArt));
                if (!string.IsNullOrEmpty(face.Effect)) column.Add(Text(face.Effect, ShopClasses.InspectText));
                if (face.Stats.Count > 0) column.Add(Stats(face));
                if (!string.IsNullOrEmpty(face.PackageLine))
                {
                    var package = new VisualElement { pickingMode = PickingMode.Ignore };
                    package.AddToClassList(ShopClasses.Package);
                    package.Add(Text(face.PackageHeading, ShopClasses.PackageHeading, UiClasses.Upper));
                    package.Add(Text(face.PackageLine, ShopClasses.PackageLine));
                    column.Add(package);
                }
            }
            _modal.Body.Add(column);
            _modalBack = Foot(_flow.BackLabel, UiClasses.ButtonBack, () => Press(_flow.CloseOverlay, _modalBack));
            Focus(_modalBack);
        }

        private VisualElement Stats(Core2ItemFace face)
        {
            var stats = new VisualElement { pickingMode = PickingMode.Ignore };
            stats.AddToClassList(ShopClasses.Stats);
            foreach (var stat in face.Stats)
            {
                var cell = new VisualElement { pickingMode = PickingMode.Ignore };
                cell.AddToClassList(ShopClasses.Stat);
                cell.Add(Text(stat.Value, ShopClasses.StatValue));
                cell.Add(Text(stat.Key, ShopClasses.StatLabel, UiClasses.Upper));
                stats.Add(cell);
            }
            return stats;
        }

        private void RenderConfirm(Core2ShopItem item)
        {
            _modal.SetHead(null, _flow.ConfirmQuestion);
            var body = _modal.Body;
            body.Add(Text(item.Name, ShopClasses.Name));
            body.Add(Text(_flow.ConfirmMessage, ShopClasses.Question));
            _modalBack = Foot(_flow.BackLabel, UiClasses.ButtonBack, () => Press(_flow.CloseOverlay, _modalBack));
            _modalConfirm = Foot(_flow.ConfirmButton, UiClasses.ButtonPrimary, () => Press(_flow.ConfirmBuy, _modalConfirm));
            _modalConfirm.AddToClassList(UiClasses.ButtonReady);
            Focus(_modalBack);
        }

        // ------------------------------------------------------------------ intents

        /// <summary>
        /// One press: the flow's step runs (a purchase is saved by the session), a refusal shows at the control that was pressed, a step that left the shop goes on to the screen the run stands at,
        /// any other step redraws. A save that failed asks to retry.
        /// </summary>
        private void Press(Func<Core2ShopStep> step, VisualElement anchor)
        {
            Core2ShopStep result;
            try { result = step(); }
            catch (Exception e)
            {
                SaveFailed(e, () => Press(step, anchor));
                return;
            }
            if (!result.Done)
            {
                // A purchase that was refused after the question closed it: the shelf stands again and the refusal shows at the Buy that asked.
                Render();
                Refuse(_opener >= 0 && _buy.TryGetValue(_opener, out var buy) ? buy : anchor, result.Refusal);
                return;
            }
            if (result.Left)
            {
                RunFlow.Show(Context, _run);
                return;
            }
            Render();
        }

        private void SaveFailed(Exception e, Action retry)
        {
            Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
            _flow.Refresh();
            Render();
            ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => retry() });
        }

        private void Refuse(VisualElement anchor, string text)
        {
            if (anchor == null || string.IsNullOrEmpty(text)) return;
            Kit.Refusal.Show(anchor, Context.Overlay, text, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
        }

        private void Focus(VisualElement element)
        {
            if (element == null) return;
            // While the screen is being bound the navigator focuses the remembered control itself; later draws move focus to where the player goes next.
            Context.Instance.LastFocused = element;
            if (_bound) Nav.FocusTop(element);
        }

        // ------------------------------------------------------------------ pause and back

        private void OpenPause() => Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = _run });

        /// <summary>Escape: an open modal closes, anything else opens the pause screen.</summary>
        public override bool HandleBack()
        {
            if (_flow != null && _flow.Stage != Core2ShopStage.Shelf)
            {
                Press(_flow.CloseOverlay, null);
                return true;
            }
            OpenPause();
            return true;
        }

        public override bool HandleMenu()
        {
            OpenPause();
            return true;
        }

        public override void OnReturned()
        {
            if (_flow == null || _run == null) return;
            if (!_run.HasOpenShop || _run.RunOver)
            {
                RunFlow.Show(Context, _run);
                return;
            }
            _flow.Refresh();
            Render();
        }
    }
}
