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
    /// The Core 2 rest screen (VP-8, D-950; web ui/components/gameProfileRest.js, restFrame.js, smithUpgradeModal.js, statAllocationCard.js; docs/qa/web-screens/rest-*, smith-*): the painted chapel
    /// with the traveler at the fire fills the room under the board HUD and the door stands on the right: the eyebrow REST, the gold title, how many services are available, what the arrival healed,
    /// the line under it, the recovery preview (HP, MP, SP and the two flask pools, before and after, with the gain), one card per service (Smith, Level Up) and Continue. A service card opens its
    /// modal, or refuses at the card with its reason when it cannot be taken. The Smith modal lists the items it can upgrade; the item chosen shows its stat rows, its requirements, the deck
    /// cards it changes (before and after) and the recovery, and Upgrade asks once more before it takes the tier. Level Up lists the attributes (a point goes to one) and confirms. Every
    /// press goes through <see cref="Core2RestFlow"/>, which saves at once through the session; Continue leaves for the map. Escape closes the modal, else opens the pause screen.
    /// </summary>
    public sealed class Core2RestScreen : ScreenView
    {
        private readonly List<LocButton> _services = new List<LocButton>();
        private readonly List<LocButton> _items = new List<LocButton>();
        private readonly List<LocButton> _attributes = new List<LocButton>();
        private Core2RunSession _run;
        private Core2RestFlow _flow;
        private CombatModal _door;
        private CombatModal _modal;
        private VisualElement _scrim;
        private LocButton _continue;
        private LocButton _modalBack;
        private LocButton _modalConfirm;
        private string _opener;
        private bool _bound;

        public Core2RunSession Run => _run;

        public Core2RestFlow Flow => _flow;

        public IReadOnlyList<LocButton> ServiceButtons => _services;

        public IReadOnlyList<LocButton> ItemButtons => _items;

        public IReadOnlyList<LocButton> AttributeButtons => _attributes;

        public LocButton ContinueButton => _continue;

        public LocButton ModalBackButton => _modalBack;

        public LocButton ModalConfirmButton => _modalConfirm;

        public override string FocusMode => _flow != null && _flow.Modal != Core2RestModal.None ? RestShopValues.FocusModal : null;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            _run = (args as Core2RunArgs)?.Session ?? Ui.Core2Session;
            if (_run == null || !_run.IsResting)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            Ui.Core2Session = _run;
            _flow = new Core2RestFlow(_run, Ui.Data);
            _door = Root.Q<CombatModal>(UiNames.CrestDoor);
            _modal = Root.Q<CombatModal>(UiNames.CrestModal);
            _scrim = Root.Q(UiNames.CrestScrim);
            UiDom.Show(_door.CloseButton, false);
            _modal.AddToClassList(RestClasses.Modal);
            _modal.CloseRequested += () => Press(_flow.CloseModal, _modal.CloseButton);
            _scrim.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == _scrim) Press(_flow.CloseModal, null);
            });
            BoardHud.Wire(Context, _run, OpenPause);
            ApplyScene();
            Render();
            _bound = true;
        }

        // ------------------------------------------------------------------ the room

        private void ApplyScene()
        {
            var scene = Root.Q(UiNames.CrestScene);
            var stage = Root.Q(UiNames.CrestStage);
            BoardHud.SetImage(stage, BoardHud.Art(Ui, ArtKinds.Scene, RestShopArt.RestScene));
            BoardHud.SetImage(Root.Q(UiNames.CrestTraveler), BoardHud.Art(Ui, ArtKinds.Illustration, RestShopArt.RestTraveler));
            scene.RegisterCallback<GeometryChangedEvent>(_ => LayoutScene());
            _door.RegisterCallback<GeometryChangedEvent>(_ => LayoutScene());
        }

        /// <summary>
        /// The painting's square stage (player-polish.css:547-563): as tall as the room or as wide as the room left of the door, whichever is more, centred on the room's height, with the
        /// traveler standing on its floor at the web's share of it.
        /// </summary>
        private void LayoutScene()
        {
            var scene = Root.Q(UiNames.CrestScene);
            var stage = Root.Q(UiNames.CrestStage);
            var traveler = Root.Q(UiNames.CrestTraveler);
            var width = scene.layout.width;
            var height = scene.layout.height;
            if (float.IsNaN(width) || float.IsNaN(height) || height <= 0f || float.IsNaN(_door.layout.xMin)) return;
            var gap = width - _door.layout.xMax;
            var side = Mathf.Max(height, _door.layout.xMin - gap);
            var tokens = Ui.Data.Tokens;
            stage.style.width = side;
            stage.style.height = side;
            stage.style.left = 0f;
            stage.style.top = (height - side) * (float)UiMath.Half;
            var travelerW = side * (float)tokens.Ratio(RatioKeys.RestTravelerW);
            traveler.style.left = side * (float)tokens.Ratio(RatioKeys.RestTravelerLeft);
            traveler.style.width = travelerW;
            traveler.style.height = travelerW * (float)tokens.Ratio(RatioKeys.RestTravelerAspect);
            traveler.style.bottom = side * (float)tokens.Ratio(RatioKeys.RestTravelerBottom);
        }

        // ------------------------------------------------------------------ render

        /// <summary>Draws the door and, when one is open, the modal; focus goes to the control a player most likely wants next.</summary>
        public void Render()
        {
            if (_flow == null) return;
            BoardHud.Render(Context, _run);
            _services.Clear();
            _items.Clear();
            _attributes.Clear();
            _continue = _modalBack = _modalConfirm = null;
            RenderDoor();
            RenderModal();
        }

        private void RenderDoor()
        {
            _door.Reset();
            _door.SetHead(_flow.Eyebrow, _flow.Title, _flow.Status);
            var body = _door.Body;
            if (!string.IsNullOrEmpty(_flow.Healed)) body.Add(Text(_flow.Healed, RestClasses.Line));
            if (!string.IsNullOrEmpty(_flow.Line)) body.Add(Text(_flow.Line, RestClasses.Line));
            body.Add(Text(_flow.RecoveryHeading, RestClasses.Heading, UiClasses.Upper));
            body.Add(Table(_flow.Recovery));
            foreach (var service in _flow.Services) body.Add(ServiceCard(service));
            _continue = new LocButton();
            _continue.AddToClassList(UiClasses.ButtonPrimary);
            _continue.AddToClassList(UiClasses.ButtonReady);
            _continue.SetResolved(_flow.ContinueText);
            _continue.clicked += () => Press(_flow.Leave, _continue);
            _door.Foot.Add(_continue);
            if (_flow.Modal == Core2RestModal.None) Focus(_opener != null ? _services.FirstOrDefault(b => (string)b.userData == _opener) ?? _continue : _services.FirstOrDefault() ?? _continue);
        }

        private LocButton ServiceCard(Core2RestServiceView service)
        {
            var button = new LocButton { userData = service.Id };
            button.AddToClassList(RestClasses.Service);
            button.EnableInClassList(RestClasses.ServiceLocked, !service.Available);
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList(RestClasses.ServiceIcon);
            BoardHud.SetImage(icon, Ui.Icon(service.Id == Core2RestValues.ServiceSmith ? RestShopArt.IconSmith : RestShopArt.IconLevelUp));
            var texts = new VisualElement { pickingMode = PickingMode.Ignore };
            texts.AddToClassList(RestClasses.ServiceBody);
            texts.Add(Text(service.Title, RestClasses.ServiceTitle));
            texts.Add(Text(service.Available || service.Used ? service.Description : service.Reason, RestClasses.ServiceText));
            button.Add(icon);
            button.Add(texts);
            button.clicked += () => OnService(service.Id, button);
            _services.Add(button);
            return button;
        }

        private VisualElement Table(IReadOnlyList<Core2RestLine> lines)
        {
            var table = new VisualElement { pickingMode = PickingMode.Ignore };
            table.AddToClassList(RestClasses.Rows);
            foreach (var line in lines)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList(RestClasses.Row);
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList(RestClasses.RowIcon);
                BoardHud.SetImage(icon, Ui.Icon(line.Id));
                row.Add(icon);
                row.Add(Text(line.Label, RestClasses.RowLabel));
                row.Add(Change(line.Before, line.After));
                row.Add(Text(line.Gain, RestClasses.RowGain));
                table.Add(row);
            }
            return table;
        }

        private VisualElement Change(string before, string after)
        {
            var change = new VisualElement { pickingMode = PickingMode.Ignore };
            change.AddToClassList(RestClasses.RowChange);
            change.Add(Text(before, RestClasses.RowBefore));
            change.Add(Text(Strings.Get(StringKeys.Core2RestRecoveryArrow), RestClasses.RowArrow));
            change.Add(Text(after, RestClasses.RowAfter));
            return change;
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

        // ------------------------------------------------------------------ the modals

        private void RenderModal()
        {
            var open = _flow.Modal != Core2RestModal.None;
            UiDom.Show(_scrim, open);
            _modal.Reset();
            if (!open) return;
            switch (_flow.Modal)
            {
                case Core2RestModal.Smith:
                    RenderSmith();
                    break;
                case Core2RestModal.SmithReview:
                    RenderReview();
                    break;
                default:
                    RenderLevel();
                    break;
            }
        }

        private static ScrollView Scroll()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical) { focusable = false };
            scroll.contentViewport.focusable = false;
            scroll.horizontalScroller.slider.focusable = false;
            scroll.verticalScroller.slider.focusable = false;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            return scroll;
        }

        private void RenderSmith()
        {
            _modal.size = RestShopValues.SizeSmith;
            _modal.RemoveFromClassList(RestClasses.ModalFit);
            _modal.SetHead(null, _flow.ModalTitle, _flow.ModalStatus);
            var split = new VisualElement();
            split.AddToClassList(RestClasses.Split);
            var list = Scroll();
            list.AddToClassList(RestClasses.List);
            foreach (var row in _flow.SmithRows) list.Add(ItemRow(row));
            var pane = Scroll();
            pane.AddToClassList(RestClasses.Pane);
            FillPane(pane.contentContainer);
            split.Add(list);
            split.Add(pane);
            _modal.Body.Add(split);
            _modalBack = Foot(_flow.BackLabel, UiClasses.ButtonBack, () => Press(_flow.CloseModal, _modalBack));
            var chosen = _items.FirstOrDefault(b => (string)b.userData == _flow.SmithSelected);
            Focus(chosen ?? _items.FirstOrDefault() ?? _modalBack);
        }

        private LocButton ItemRow(Core2SmithRow row)
        {
            var button = new LocButton { userData = row.ItemRef };
            button.AddToClassList(RestClasses.Item);
            button.EnableInClassList(RestClasses.ItemSelected, row.Selected);
            button.Add(Art(row.ArtId, row.FaceKind, RestClasses.ItemArt));
            var texts = new VisualElement { pickingMode = PickingMode.Ignore };
            texts.AddToClassList(RestClasses.ItemText);
            texts.Add(Text(row.Name, RestClasses.ItemName));
            texts.Add(Text(row.Tier, RestClasses.ItemTier));
            button.Add(texts);
            button.Add(Text(row.Count, UiClasses.Chip, RestClasses.ItemCount));
            button.clicked += () => Press(() => _flow.SelectSmith(row.ItemRef), button);
            _items.Add(button);
            return button;
        }

        private VisualElement Art(string artId, string faceKind, string cls)
        {
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList(cls);
            var texture = (artId != null ? Ui.Texture(artId) : null) ?? Ui.Icon(SpoilsOption.KindIcon(faceKind));
            BoardHud.SetImage(art, texture);
            return art;
        }

        private void FillPane(VisualElement pane)
        {
            var detail = _flow.SmithDetail;
            if (detail == null)
            {
                var empty = new VisualElement { pickingMode = PickingMode.Ignore };
                empty.AddToClassList(RestClasses.Empty);
                var art = new VisualElement { pickingMode = PickingMode.Ignore };
                art.AddToClassList(RestClasses.EmptyArt);
                BoardHud.SetImage(art, Ui.Icon(RestShopArt.IconSmith));
                empty.Add(art);
                empty.Add(Text(_flow.Instruction, RestClasses.Instruction));
                pane.Add(empty);
                pane.Add(Text(_flow.ModalNote, RestClasses.Note));
                return;
            }
            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList(RestClasses.Selected);
            head.Add(Art(detail.ArtId, detail.FaceKind, RestClasses.SelectedArt));
            var texts = new VisualElement { pickingMode = PickingMode.Ignore };
            texts.AddToClassList(RestClasses.SelectedTexts);
            texts.Add(Text(_flow.SelectedEyebrow, RestClasses.Heading, UiClasses.Upper));
            texts.Add(Text(detail.Name, RestClasses.SelectedName));
            texts.Add(Text(detail.Meta, RestClasses.SelectedMeta));
            head.Add(texts);
            head.Add(Text(detail.KindLabel, UiClasses.Chip));
            pane.Add(head);
            AddFacts(pane, detail);
            pane.Add(Text(detail.RecoveryHeading, RestClasses.Heading, UiClasses.Upper));
            pane.Add(Table(detail.Recovery));
            pane.Add(Text(detail.CostLine, RestClasses.Cost));
            _modalConfirm = new LocButton();
            _modalConfirm.AddToClassList(UiClasses.ButtonPrimary);
            _modalConfirm.AddToClassList(UiClasses.ButtonReady);
            _modalConfirm.AddToClassList(RestClasses.Confirm);
            _modalConfirm.SetResolved(_flow.ConfirmLabel);
            _modalConfirm.clicked += () => Press(_flow.RequestUpgrade, _modalConfirm);
            pane.Add(_modalConfirm);
            pane.Add(Text(_flow.ModalNote, RestClasses.Note));
        }

        /// <summary>The item's stat rows, its requirements and the deck cards it changes, each before and after.</summary>
        private void AddFacts(VisualElement into, Core2SmithDetail detail)
        {
            if (detail.Changes.Count > 0)
            {
                into.Add(Text(detail.ChangesHeading, RestClasses.Heading, UiClasses.Upper));
                foreach (var change in detail.Changes) into.Add(ChangeRow(change.Label, change.Before, change.After, null, true));
            }
            into.Add(Text(detail.RequirementsHeading, RestClasses.Heading, UiClasses.Upper));
            if (detail.Requirements.Count == 0) into.Add(Text(detail.NoRequirement, RestClasses.ServiceText));
            foreach (var requirement in detail.Requirements) into.Add(ChangeRow(requirement.Label, requirement.Before, requirement.After, requirement.YouHave, requirement.Met));
            if (detail.Cards.Count == 0) return;
            into.Add(Text(detail.CardsHeading, RestClasses.Heading, UiClasses.Upper));
            foreach (var card in detail.Cards) into.Add(ChangeRow(card.Label, card.Before, card.After, null, true));
        }

        private VisualElement ChangeRow(string label, string before, string after, string you, bool met)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList(RestClasses.Change);
            row.EnableInClassList(RestClasses.ChangeUnmet, !met);
            row.Add(Text(label, RestClasses.ChangeLabel));
            if (!string.IsNullOrEmpty(you)) row.Add(Text(you, RestClasses.ChangeYou));
            row.Add(Change(before, after));
            return row;
        }

        /// <summary>The Smith's question: the permanent badge over "Upgrade X?", the sentence, what changes, Back and the upgrade.</summary>
        private void RenderReview()
        {
            _modal.size = UiValues.ModalSizeMd;
            _modal.AddToClassList(RestClasses.ModalFit);
            _modal.SetHead(_flow.ReviewBadge, _flow.ReviewQuestion);
            var body = _modal.Body;
            body.Add(Text(_flow.ReviewMessage, RestClasses.Instruction));
            if (_flow.SmithDetail != null)
            {
                AddFacts(body, _flow.SmithDetail);
                body.Add(Text(_flow.SmithDetail.CostLine, RestClasses.Cost));
            }
            _modalBack = Foot(_flow.ReviewBack, UiClasses.ButtonBack, () => Press(_flow.BackToSmith, _modalBack));
            _modalConfirm = Foot(_flow.ConfirmLabel, UiClasses.ButtonPrimary, () => Press(_flow.CommitUpgrade, _modalConfirm));
            _modalConfirm.AddToClassList(UiClasses.ButtonReady);
            Focus(_modalBack);
        }

        private void RenderLevel()
        {
            _modal.size = UiValues.ModalSizeMd;
            _modal.AddToClassList(RestClasses.ModalFit);
            _modal.SetHead(null, _flow.ModalTitle);
            var body = _modal.Body;
            body.Add(Text(_flow.ModalNote, RestClasses.Instruction));
            foreach (var row in _flow.LevelRows) body.Add(AttributeRow(row));
            if (_flow.ModalRecovery.Count > 0)
            {
                body.Add(Text(_flow.RecoveryHeading, RestClasses.Heading, UiClasses.Upper));
                body.Add(Table(_flow.ModalRecovery));
            }
            _modalBack = Foot(_flow.BackLabel, UiClasses.ButtonBack, () => Press(_flow.CloseModal, _modalBack));
            _modalConfirm = Foot(_flow.ConfirmLabel, UiClasses.ButtonPrimary, () => Press(_flow.ConfirmLevel, _modalConfirm));
            _modalConfirm.AddToClassList(UiClasses.ButtonReady);
            _modalConfirm.EnableInClassList(RestClasses.ConfirmOff, _flow.LevelSelected == null);
            Focus(_attributes.FirstOrDefault(b => (string)b.userData == _flow.LevelSelected) ?? _attributes.FirstOrDefault() ?? _modalBack);
        }

        private LocButton AttributeRow(Core2LevelRow row)
        {
            var button = new LocButton { userData = row.Id };
            button.AddToClassList(RestClasses.Attribute);
            button.EnableInClassList(RestClasses.AttributeSelected, row.Selected);
            button.EnableInClassList(RestClasses.AttributeLocked, !row.Available);
            var texts = new VisualElement { pickingMode = PickingMode.Ignore };
            texts.Add(Text(row.Label, RestClasses.AttributeName));
            if (!row.Available && !string.IsNullOrEmpty(row.Reason)) texts.Add(Text(row.Reason, RestClasses.ItemTier));
            button.Add(texts);
            button.Add(Change(row.Value, row.After));
            button.clicked += () => Press(() => _flow.SelectAttribute(row.Id), button);
            _attributes.Add(button);
            return button;
        }

        // ------------------------------------------------------------------ intents

        private void OnService(string id, LocButton anchor)
        {
            _opener = id;
            Press(() => _flow.OpenService(id), anchor);
        }

        /// <summary>
        /// One press: the flow's step runs (a service taken is saved by the session), a refusal shows at the control that was pressed, a step that left the site goes on to the screen the run stands
        /// at, any other step redraws. A save that failed asks to retry.
        /// </summary>
        private void Press(Func<Core2RestStep> step, VisualElement anchor)
        {
            Core2RestStep result;
            try { result = step(); }
            catch (Exception e)
            {
                SaveFailed(e, () => Press(step, anchor));
                return;
            }
            if (!result.Done)
            {
                Refuse(anchor, result.Refusal);
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

        /// <summary>Escape: an open modal closes (a question goes back to the Smith's pane), anything else opens the pause screen.</summary>
        public override bool HandleBack()
        {
            if (_flow != null && _flow.Modal != Core2RestModal.None)
            {
                Press(_flow.CloseModal, null);
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
            if (!_run.IsResting || _run.RunOver)
            {
                RunFlow.Show(Context, _run);
                return;
            }
            _flow.Refresh();
            Render();
        }
    }
}
