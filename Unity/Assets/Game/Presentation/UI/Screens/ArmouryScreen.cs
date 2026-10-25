using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using CardView = Ashen.Presentation.UI.Kit.CardView;
using Refusal = Ashen.Presentation.UI.Kit.Refusal;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>What the Armoury opens with: the run and the tab (<see cref="ArmouryValues"/>).</summary>
    public sealed class ArmouryArgs
    {
        public IClimb Session;
        public string Tab;

        /// <summary>With <see cref="ArmouryValues.TabEditor"/>: the collection tile key to start selected (the card a reward just handed over).</summary>
        public string FocusKey;

        /// <summary>The editor is the whole visit (the spoils' Edit deck, the web's editor without the hub): Done and Cancel close the Armoury.</summary>
        public bool CloseWithEditor;
    }

    /// <summary>The way into the Armoury from the map, the HUD and the pause menu.</summary>
    public static class ArmouryDoor
    {
        /// <summary>Opens the Armoury over the screen; false when it cannot (it reads a Core 2 run, and the screen must be built).</summary>
        public static bool Open(ScreenContext context, IClimb climb, string tab)
        {
            if (!(climb is Core2RunSession) || !context.Ui.Data.Screens.IsBuilt(ScreenIds.Armoury)) return false;
            context.Navigator.OpenModal(ScreenIds.Armoury, new ArmouryArgs { Session = climb, Tab = tab });
            return true;
        }
    }

    /// <summary>
    /// The Armoury modal (web equipment.js and deckEditor.js; docs/qa/web-screens/map-{deck,equipment,stats}; D-903, D-904): the combat modal's shell with the tabs Character | Armory | Edit Deck in its
    /// head. Character is the stats pane, Armory a rail of Equipment (the equipped positions and an inspector) and Cards (the run's deck as card faces, with the Edit deck action), and Edit Deck
    /// is the Core 2 deck editor over <see cref="Core2DeckEditor"/>: the deck's rows and the collection's tiles with their steps (a refusal is said at the control that was pressed), the counter, the
    /// cost curve, filter and sort chips, an inspector and Cancel, Undo and Done. The editor works on the run in place; Done keeps and saves it, Cancel (asked about when there are changes) and
    /// closing put it back exactly. The door is shut with the reason when <see cref="Core2RunSession.DeckEditorRefusal"/> names one.
    /// </summary>
    public sealed partial class ArmouryScreen : ScreenView
    {
        private Core2RunSession _run;
        private string _tab;
        private CombatModal _modal;
        private LocButton _tabCharacter;
        private LocButton _tabArmory;
        private LocButton _tabEditor;
        private Core2DeckEditor _editor;
        private DeckEditorView _view = new DeckEditorView();
        private string _selected;
        private string _position;
        private string _refusal = string.Empty;
        private string _focusKey;
        private bool _closeWithEditor;
        private string _pickSlot;
        private int _pickIndex;
        private string _inventoryKey;
        private string _notice = string.Empty;
        private readonly HashSet<string> _open = new HashSet<string>();

        public string Tab => _tab;
        public Core2DeckEditor Editor => _editor;
        public CombatModal Modal => _modal;
        public string RefusalText => _refusal;

        public override string FocusMode => null;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            var given = args as ArmouryArgs;
            _run = given?.Session as Core2RunSession ?? Ui.Core2Session;
            _tab = given?.Tab ?? ArmouryValues.TabStats;
            _modal = Root.Q<CombatModal>(UiNames.ArmouryModal);
            if (_run == null || _modal == null)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            _modal.size = UiValues.ModalSizeXl;
            _modal.CloseRequested += RequestClose;
            BindDialog();
            BuildTabs();
            var openEditor = _tab == ArmouryValues.TabEditor;
            if (openEditor) _tab = ArmouryValues.TabDeck;
            Render();
            if (!openEditor) return;
            _closeWithEditor = given?.CloseWithEditor == true;
            OpenEditor(null);
            if (_editor == null) return;
            if (!string.IsNullOrEmpty(given?.FocusKey)) _selected = given.FocusKey;
            Render();
        }

        public override void Unbind()
        {
            if (_editor != null && !_editor.Closed) _editor.Cancel();
            _editor = null;
        }

        // ------------------------------------------------------------------ head, back

        private void BuildTabs()
        {
            var tabs = new VisualElement { pickingMode = PickingMode.Position };
            tabs.AddToClassList(ArmouryClasses.Tabs);
            _tabCharacter = AddTab(tabs, StringKeys.ArmouryTabCharacter, () => SetTab(ArmouryValues.TabStats));
            _tabArmory = AddTab(tabs, StringKeys.ArmouryTabArmory, () => SetTab(_tab == ArmouryValues.TabDeck || _tab == ArmouryValues.TabInventory ? _tab : ArmouryValues.TabEquipment));
            _tabEditor = AddTab(tabs, StringKeys.ArmouryTabEditDeck, () => OpenEditor(_tabEditor));
            _modal.Head.Insert(0, tabs);
        }

        private static LocButton AddTab(VisualElement parent, string key, Action action)
        {
            var button = new LocButton(key);
            button.AddToClassList(ArmouryClasses.Tab);
            button.clicked += action;
            parent.Add(button);
            return button;
        }

        private void RequestClose()
        {
            if (_editor != null) CancelEditor();
            else Nav.Close(Context.Instance);
        }

        public override bool HandleBack()
        {
            if (DialogOpen) CloseDialog();
            else RequestClose();
            return true;
        }

        public override bool HandleMenu()
        {
            if (DialogOpen) CloseDialog();
            else RequestClose();
            return true;
        }

        private void SetTab(string tab)
        {
            if (_editor != null)
            {
                CancelEditor(() => SetTab(tab));
                return;
            }
            _tab = tab;
            _focusKey = null;
            _pickSlot = null;
            _inventoryKey = null;
            _notice = string.Empty;
            Render();
        }

        // ------------------------------------------------------------------ render

        public void Render()
        {
            _modal.Reset();
            var inArmory = _tab == ArmouryValues.TabEquipment || _tab == ArmouryValues.TabDeck || _tab == ArmouryValues.TabInventory;
            _tabCharacter.EnableInClassList(ArmouryClasses.TabOn, _tab == ArmouryValues.TabStats);
            _tabArmory.EnableInClassList(ArmouryClasses.TabOn, inArmory);
            _tabEditor.EnableInClassList(ArmouryClasses.TabOn, _tab == ArmouryValues.TabEditor);
            var body = _modal.Body;
            switch (_tab)
            {
                case ArmouryValues.TabEquipment:
                case ArmouryValues.TabInventory:
                case ArmouryValues.TabDeck:
                    var row = new VisualElement();
                    row.AddToClassList(ArmouryClasses.Body);
                    body.Add(row);
                    row.Add(Rail());
                    var pane = new VisualElement();
                    pane.AddToClassList(ArmouryClasses.Pane);
                    row.Add(pane);
                    if (_tab == ArmouryValues.TabEquipment) EquipmentPane(pane);
                    else if (_tab == ArmouryValues.TabInventory) InventoryPane(pane);
                    else CardsPane(pane);
                    break;
                case ArmouryValues.TabEditor:
                    EditorPane(body);
                    break;
                default:
                    CharacterPane(body);
                    break;
            }
            RenderFoot();
            RestoreFocus();
        }

        private void RenderFoot()
        {
            if (_tab == ArmouryValues.TabEditor)
            {
                _modal.Foot.AddToClassList(ArmouryClasses.Foot);
                var refusal = new LocLabel { pickingMode = PickingMode.Ignore };
                refusal.AddToClassList(ArmouryClasses.Refusal);
                refusal.SetResolved(_refusal);
                _modal.Foot.Add(refusal);
                Button(_modal.Foot, StringKeys.DeckEditorCancel, UiClasses.ButtonBack, CancelEditorPressed);
                var undo = Button(_modal.Foot, StringKeys.DeckEditorUndo, null, UndoEdit);
                undo.SetEnabled(_editor?.CanUndo == true);
                var done = Button(_modal.Foot, StringKeys.DeckEditorDone, UiClasses.ButtonPrimary, DonePressed);
                done.EnableInClassList(UiClasses.ButtonReady, _editor?.Model(_view).DoneDisabled == false);
                return;
            }
            _modal.Foot.RemoveFromClassList(ArmouryClasses.Foot);
            Button(_modal.Foot, StringKeys.ArmouryBack, UiClasses.ButtonBack, () => Nav.Close(Context.Instance));
            if (_footPlan != null)
            {
                var plan = _footPlan;
                var act = new LocButton();
                act.SetResolved(plan.Label);
                act.AddToClassList(UiClasses.Button);
                act.AddToClassList(plan.Danger ? UiClasses.ButtonBack : UiClasses.ButtonPrimary);
                act.EnableInClassList(ArmouryClasses.ActionOff, plan.Refusal.Length > 0);
                act.clicked += () => plan.Act();
                _modal.Foot.Add(act);
            }
        }

        private LocButton Button(VisualElement parent, string key, string kind, Action action, string focusKey = null)
        {
            var button = new LocButton(key);
            button.AddToClassList(UiClasses.Button);
            if (kind != null) button.AddToClassList(kind);
            button.userData = focusKey ?? key;
            button.clicked += action;
            parent.Add(button);
            return button;
        }

        private VisualElement Rail()
        {
            var rail = new VisualElement();
            rail.AddToClassList(ArmouryClasses.Rail);
            Item(rail, StringKeys.ArmouryRailCharacter, false, () => SetTab(ArmouryValues.TabStats));
            Item(rail, StringKeys.ArmouryRailEquipment, _tab == ArmouryValues.TabEquipment, () => SetTab(ArmouryValues.TabEquipment));
            Item(rail, StringKeys.ArmouryRailInventory, _tab == ArmouryValues.TabInventory, () => SetTab(ArmouryValues.TabInventory));
            Item(rail, StringKeys.ArmouryRailCards, _tab == ArmouryValues.TabDeck, () => SetTab(ArmouryValues.TabDeck));
            return rail;
        }

        private static void Item(VisualElement rail, string key, bool on, Action action)
        {
            var item = new LocButton(key);
            item.AddToClassList(ArmouryClasses.RailItem);
            item.EnableInClassList(ArmouryClasses.RailItemOn, on);
            item.userData = key;
            item.clicked += action;
            rail.Add(item);
        }

        private LocLabel Label(VisualElement parent, string text, params string[] classes)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) label.AddToClassList(c);
            label.SetResolved(text);
            parent.Add(label);
            return label;
        }

        private void RestoreFocus()
        {
            if (_focusKey == null) return;
            var key = _focusKey;
            var target = _modal.Query<VisualElement>().Where(e => e.focusable && key.Equals(e.userData)).First();
            if (target != null) Nav.FocusTop(target);
        }

        // ------------------------------------------------------------------ Character (map-stats)

        private bool _progression;

        private void CharacterPane(VisualElement body)
        {
            if (_progression)
            {
                ProgressionPane(body);
                return;
            }
            var view = ArmouryPanes.Character(_run, Ui.Data);
            Label(body, view.Eyebrow, ArmouryClasses.Eyebrow, UiClasses.Upper);
            Label(body, view.ClassName, ArmouryClasses.Name);
            Label(body, view.Description, ArmouryClasses.Line);
            var xp = new VisualElement { pickingMode = PickingMode.Ignore };
            xp.AddToClassList(ArmouryClasses.Xp);
            Label(xp, view.LevelText, ArmouryClasses.Eyebrow, UiClasses.Upper);
            Label(xp, view.XpText, ArmouryClasses.Line);
            body.Add(xp);
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.AddToClassList(ArmouryClasses.XpBar);
            var fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList(ArmouryClasses.XpFill);
            fill.style.width = Length.Percent((float)(view.XpFraction * UiMath.Percent));
            bar.Add(fill);
            body.Add(bar);
            var door = Button(body, StringKeys.ArmouryCharacterProgression, ArmouryClasses.ProgressionButton, () =>
            {
                _progression = true;
                _focusKey = null;
                Render();
            });
            door.userData = ArmouryValues.FoldLevels;
            var sheet = new VisualElement();
            sheet.AddToClassList(ArmouryClasses.Sheet);
            body.Add(sheet);
            var portrait = new VisualElement { pickingMode = PickingMode.Ignore };
            portrait.AddToClassList(ArmouryClasses.Portrait);
            Ui.ApplyBackground(portrait, view.Portrait);
            sheet.Add(portrait);
            var info = new VisualElement { pickingMode = PickingMode.Ignore };
            info.AddToClassList(ArmouryClasses.Info);
            sheet.Add(info);
            Fold(info, ArmouryValues.FoldPower, Strings.Get(StringKeys.ArmouryPowerGroup), view.PowerSummary, open =>
            {
                foreach (var power in view.Powers)
                {
                    var line = new VisualElement { pickingMode = PickingMode.Ignore };
                    line.AddToClassList(ArmouryClasses.Power);
                    Label(line, power.FullLabel, ArmouryClasses.PowerLabel);
                    Label(line, power.Gear, ArmouryClasses.Muted);
                    Label(line, CombatText.Number(power.Value), ArmouryClasses.PowerValue);
                    open.Add(line);
                    Label(open, power.Formula, ArmouryClasses.Muted);
                }
            });
            var box = Box(info, Strings.Get(StringKeys.ArmouryCharacterSheet));
            var chips = new VisualElement { pickingMode = PickingMode.Ignore };
            chips.AddToClassList(ArmouryClasses.Chips);
            foreach (var chip in view.Sheet)
            {
                var holder = new VisualElement { pickingMode = PickingMode.Ignore };
                holder.AddToClassList(ArmouryClasses.Chip);
                holder.style.flexDirection = FlexDirection.Row;
                Label(holder, chip.Label + ArmouryKeys.Space, ArmouryClasses.Muted);
                Label(holder, chip.Value, ArmouryClasses.ChipValue);
                chips.Add(holder);
            }
            box.Add(chips);
            Fold(info, ArmouryValues.FoldRelics, Strings.Get(StringKeys.ArmouryRelicsTitle), view.RelicsSummary, open =>
            {
                foreach (var name in view.RelicNames) Label(open, name, ArmouryClasses.Line);
            });
            Fold(info, ArmouryValues.FoldCards, Strings.Get(StringKeys.ArmouryCardsTitleEquipment), view.EquipmentCardsSummary, open => ReceiptPanel.Fill(open, ArmouryReceipts.For(_run, Ui.Data), Strings));
        }

        /// <summary>
        /// One of the web's information cards (equipment.js informationCard): a head with an arrow and the card's title in gold, its one-line summary under it, and the full content when the head is pressed
        /// (the open cards are kept across redraws).
        /// </summary>
        /// <summary>The Progression door (web characterSheet.js characterPane): the meter, the cadence, the attributes, the feats and the ladder, with a Back to the Character pane.</summary>
        private void ProgressionPane(VisualElement body)
        {
            ProgressionPanel.Fill(body, CharacterProgression.For(_run, Ui.Data), Strings, Fold, AttributesCard, SheetButton);
            Button(body, StringKeys.ConfirmBack, UiClasses.ButtonBack, () =>
            {
                _progression = false;
                _focusKey = null;
                Render();
            });
        }

        private void Fold(VisualElement parent, string key, string title, string summary, Action<VisualElement> content)
        {
            var open = _open.Contains(key);
            var box = new VisualElement();
            box.AddToClassList(ArmouryClasses.Fold);
            box.EnableInClassList(ArmouryClasses.FoldOpen, open);
            var head = new LocButton();
            head.AddToClassList(ArmouryClasses.FoldHead);
            head.SetResolved(Strings.Format(StringKeys.ArmouryFoldHead, new StringArgs().Add(UiPlaceholders.Mark, Strings.Get(open ? StringKeys.ArmouryFoldOpen : StringKeys.ArmouryFoldClosed)).Add(UiPlaceholders.Title, title)));
            head.userData = key;
            head.clicked += () =>
            {
                if (!_open.Add(key)) _open.Remove(key);
                _focusKey = key;
                Render();
            };
            box.Add(head);
            Label(box, summary, ArmouryClasses.FoldSummary);
            if (open)
            {
                var body = new VisualElement { pickingMode = PickingMode.Ignore };
                body.AddToClassList(ArmouryClasses.FoldBody);
                content(body);
                box.Add(body);
            }
            parent.Add(box);
        }

        private VisualElement Box(VisualElement parent, string title)
        {
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.AddToClassList(ArmouryClasses.CardBox);
            Label(box, title, ArmouryClasses.CardTitle);
            parent.Add(box);
            return box;
        }

        // ------------------------------------------------------------------ Cards (map-deck)

        private void CardsPane(VisualElement pane)
        {
            var deck = Js.Items(_run.Run[CombatKeys.Deck]).OfType<JObject>().ToList();
            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList(ArmouryClasses.CardsHead);
            pane.Add(head);
            Label(head, Strings.Format(StringKeys.ArmouryCardsTitle, new StringArgs().Add(UiPlaceholders.Count, deck.Count)), ArmouryClasses.Heading);
            Label(pane, Strings.Get(StringKeys.ArmouryCardsBlurb), ArmouryClasses.Line);
            var edit = Button(pane, StringKeys.ArmouryCardsEdit, UiClasses.ButtonPrimary, () => OpenEditor(_focusAnchor));
            edit.AddToClassList(ArmouryClasses.Edit);
            edit.AddToClassList(UiClasses.ButtonReady);
            _focusAnchor = edit;
            if (deck.Count == 0)
            {
                Label(pane, Strings.Get(StringKeys.ArmouryCardsEmpty), ArmouryClasses.Muted);
                return;
            }
            var grid = new VisualElement();
            grid.AddToClassList(ArmouryClasses.Grid);
            pane.Add(grid);
            foreach (var instance in deck)
            {
                var card = new CardView { focusable = false, pickingMode = PickingMode.Ignore };
                card.AddToClassList(ArmouryClasses.GridCard);
                card.Bind(CardData(Core2DeckCards.View(_run.CombatData, instance, Words)));
                grid.Add(card);
            }
        }

        private VisualElement _focusAnchor;

        private CardWords _words;

        /// <summary>The words of a deck card's face (P-1): the profile's card read with the run's rules.</summary>
        private CardWords Words => _words ?? (_words = CardWords.Outside(_run.Core.Combat, Ui.Data.CardText));

        /// <summary>A deck card's face data: the cost pips in the components' order (Stamina always, Mana when it costs), its type line, rank and traits; a deck card is never dimmed.</summary>
        private CardViewData CardData(Core2CardView card) => ItemFaces.DeckCard(Ui.Data, card);

        // ------------------------------------------------------------------ Edit Deck (D-404 seam)

        /// <summary>The door: opens the editor over the live run, or says why it stays shut at the control that asked.</summary>
        private void OpenEditor(VisualElement anchor)
        {
            if (_editor != null) return;
            var open = _run.OpenDeckEditor(Strings);
            if (!open.Opened)
            {
                if (anchor != null) RunFlow.Refuse(Context, anchor, open.Refusal);
                return;
            }
            _editor = open.Editor;
            _view = new DeckEditorView();
            _selected = null;
            _refusal = string.Empty;
            _tab = ArmouryValues.TabEditor;
            _focusKey = null;
            Render();
        }

        private void CancelEditorPressed() => CancelEditor();

        /// <summary>Cancel: the run is put back exactly; with changes made the player is asked first (deckEditor.closeUnsaved).</summary>
        private void CancelEditor(Action then = null)
        {
            if (_editor == null) return;
            void Discard()
            {
                _editor.Cancel();
                _editor = null;
                _tab = ArmouryValues.TabDeck;
                _refusal = string.Empty;
                _focusKey = null;
                if (_closeWithEditor)
                {
                    Nav.Close(Context.Instance);
                    return;
                }
                Render();
                then?.Invoke();
            }
            if (!_editor.CanUndo)
            {
                Discard();
                return;
            }
            ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.DeckDiscard, OnConfirm = Discard });
        }

        private void UndoEdit()
        {
            if (_editor == null) return;
            _editor.Undo();
            _refusal = string.Empty;
            Render();
        }

        private void DonePressed()
        {
            if (_editor == null) return;
            var anchor = _modal.Foot.Query<LocButton>().Where(b => StringKeys.DeckEditorDone.Equals(b.userData)).First();
            var result = _editor.Confirm();
            if (!result.Ok)
            {
                Say(anchor, result.Refusal.Text);
                return;
            }
            _editor = null;
            _tab = ArmouryValues.TabDeck;
            _refusal = string.Empty;
            _focusKey = null;
            if (_closeWithEditor)
            {
                Nav.Close(Context.Instance);
                return;
            }
            Render();
        }

        private void Say(VisualElement anchor, string text)
        {
            _refusal = text ?? string.Empty;
            if (anchor != null && _refusal.Length > 0) Refusal.Show(anchor, Context.Overlay, _refusal, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
        }

        private void Apply(DeckEditResult result, VisualElement anchor, string focusKey)
        {
            _focusKey = focusKey;
            if (result.Ok) _refusal = string.Empty;
            Render();
            if (!result.Ok)
            {
                var again = _modal.Query<VisualElement>().Where(e => focusKey.Equals(e.userData)).First() ?? anchor;
                Say(again, result.Refusal.Text);
                if (_modal.Foot.Q<LocLabel>() is LocLabel line) line.SetResolved(_refusal);
            }
        }

        private void EditorPane(VisualElement body)
        {
            var model = _editor.Model(_view);
            var editor = new VisualElement();
            editor.AddToClassList(ArmouryClasses.Editor);
            body.Add(editor);

            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList(ArmouryClasses.EditorHead);
            editor.Add(head);
            var counter = Label(head, model.Counter.Text, ArmouryClasses.Counter);
            counter.EnableInClassList(ArmouryClasses.CounterBad, model.Counter.OutOfBounds);
            var curve = new VisualElement { pickingMode = PickingMode.Ignore };
            curve.AddToClassList(ArmouryClasses.Curve);
            foreach (var bar in model.Curve)
            {
                var column = new VisualElement { pickingMode = PickingMode.Ignore };
                column.AddToClassList(ArmouryClasses.CurveBar);
                var fill = new VisualElement { pickingMode = PickingMode.Ignore };
                fill.AddToClassList(ArmouryClasses.CurveFill);
                fill.style.height = Length.Percent((float)(bar.Share * UiMath.Percent));
                column.Add(fill);
                Label(column, Strings.Format(StringKeys.DeckEditorCurveBar, new StringArgs().Add(UiPlaceholders.Bucket, bar.Bucket)), ArmouryClasses.CurveLabel);
                curve.Add(column);
            }
            head.Add(curve);

            ChipRow(editor, StringKeys.DeckEditorFilters, model.FilterType, ChipGroup.Type);
            ChipRow(editor, StringKeys.DeckEditorFilters, model.FilterCost, ChipGroup.Cost);
            ChipRow(editor, StringKeys.DeckEditorFilters, model.FilterSource.Concat(new[] { model.FilterUpgraded }).ToList(), ChipGroup.Source);
            if (!model.Ordered) ChipRow(editor, StringKeys.DeckEditorSort, model.Sorts, ChipGroup.Sort);

            var panes = new VisualElement();
            panes.AddToClassList(ArmouryClasses.Panes);
            editor.Add(panes);
            var inspect = Pane(panes, null, true);
            var library = Pane(panes, StringKeys.DeckEditorPaneCollection, false);
            var deck = Pane(panes, StringKeys.DeckEditorPaneDeck, false);
            Inspector(inspect, model);
            if (model.Collection.Count == 0) Label(library, Strings.Get(StringKeys.DeckEditorEmptyCollection), ArmouryClasses.Empty);
            foreach (var tile in model.Collection) TileRow(library, tile);
            if (model.Deck.Count == 0) Label(deck, Strings.Get(StringKeys.DeckEditorEmptyDeck), ArmouryClasses.Empty);
            foreach (var row in model.Deck) DeckRow(deck, row, model.Ordered);
        }

        private enum ChipGroup { Type, Cost, Source, Sort }

        private VisualElement Pane(VisualElement parent, string titleKey, bool inspect)
        {
            var pane = new VisualElement();
            pane.AddToClassList(ArmouryClasses.EditorPane);
            pane.EnableInClassList(ArmouryClasses.EditorPaneInspect, inspect);
            if (titleKey != null) Label(pane, Strings.Get(titleKey), ArmouryClasses.Heading);
            parent.Add(pane);
            return pane;
        }

        private void ChipRow(VisualElement parent, string labelKey, IReadOnlyList<DeckChip> chips, ChipGroup group)
        {
            if (chips == null || chips.Count == 0) return;
            var row = new VisualElement();
            row.AddToClassList(ArmouryClasses.Filters);
            Label(row, Strings.Get(labelKey), ArmouryClasses.FilterLabel, UiClasses.Upper);
            foreach (var chip in chips)
            {
                var button = new LocButton();
                button.SetResolved(chip.Label);
                button.AddToClassList(ArmouryClasses.FilterChip);
                button.EnableInClassList(ArmouryClasses.FilterChipOn, chip.On);
                var id = chip.Id;
                var key = group + UiFormats.TagSeparator + id;
                button.userData = key;
                button.clicked += () =>
                {
                    _view = Toggled(_view, group, id);
                    _focusKey = key;
                    Render();
                };
                row.Add(button);
            }
            parent.Add(row);
        }

        /// <summary>The view with a chip flipped: chips of one group OR, groups AND (web deckEditorView); the upgraded chip is a switch; a sort chip picks the sort.</summary>
        private static DeckEditorView Toggled(DeckEditorView view, ChipGroup group, string id)
        {
            IReadOnlyList<string> Flip(IReadOnlyList<string> list) => list.Contains(id) ? list.Where(i => i != id).ToList() : list.Concat(new[] { id }).ToList();
            var next = new DeckEditorView { Type = view.Type, Cost = view.Cost, Source = view.Source, Upgraded = view.Upgraded, Sort = view.Sort };
            switch (group)
            {
                case ChipGroup.Type: next.Type = Flip(view.Type); break;
                case ChipGroup.Cost: next.Cost = Flip(view.Cost); break;
                case ChipGroup.Sort: next.Sort = id; break;
                default:
                    if (id == Core2DeckText.FilterUpgraded) next.Upgraded = !view.Upgraded;
                    else next.Source = Flip(view.Source);
                    break;
            }
            return next;
        }

        private void Inspector(VisualElement pane, DeckEditorModelData model)
        {
            var row = model.Deck.FirstOrDefault(r => r.InstanceId == _selected) as DeckCardRow ?? model.Collection.FirstOrDefault(t => t.Key == _selected);
            if (row == null)
            {
                Label(pane, Strings.Get(StringKeys.ArmouryEditorPickCard), ArmouryClasses.Muted);
                return;
            }
            var card = new CardView { focusable = false, pickingMode = PickingMode.Ignore };
            card.AddToClassList(ArmouryClasses.GridCard);
            card.Bind(CardData(Core2DeckCards.View(_run.CombatData, row.Ref, Words)));
            pane.Add(card);
        }

        private void TileRow(VisualElement list, DeckTile tile)
        {
            var row = new VisualElement();
            row.AddToClassList(ArmouryClasses.Row);
            row.EnableInClassList(ArmouryClasses.RowSpent, !tile.Addable);
            row.EnableInClassList(ArmouryClasses.RowSelected, _selected == tile.Key);
            list.Add(row);
            Select(row, tile.Name + (tile.Upgraded ? ArmouryKeys.Space + Strings.Get(StringKeys.GlyphCheck) : string.Empty), tile.Key, tile.Key);
            Label(row, tile.CountText, ArmouryClasses.Meta);
            var remove = Step(row, StringKeys.DeckEditorRemove, tile.Removable, tile.Key + UiFormats.TagSeparator + ArmouryValues.StepRemove);
            remove.clicked += () => Apply(tile.RemoveInstanceId == null ? new DeckEditResult(false, null) : _editor.Remove(tile.RemoveInstanceId), remove, tile.Key + UiFormats.TagSeparator + ArmouryValues.StepRemove);
            var add = Step(row, StringKeys.DeckEditorAdd, tile.Addable, tile.Key + UiFormats.TagSeparator + ArmouryValues.StepAdd);
            add.clicked += () => Apply(_editor.Add(tile.Key), add, tile.Key + UiFormats.TagSeparator + ArmouryValues.StepAdd);
        }

        private void DeckRow(VisualElement list, DeckRow rowData, bool ordered)
        {
            var row = new VisualElement();
            row.AddToClassList(ArmouryClasses.Row);
            row.EnableInClassList(ArmouryClasses.RowSelected, _selected == rowData.InstanceId);
            list.Add(row);
            Select(row, rowData.Name, rowData.InstanceId, rowData.InstanceId);
            Label(row, rowData.CountText, ArmouryClasses.Meta);
            if (ordered)
            {
                var up = Step(row, StringKeys.DeckEditorUp, rowData.CanUp, rowData.InstanceId + UiFormats.TagSeparator + ArmouryValues.StepUp);
                up.clicked += () => Apply(_editor.Move(rowData.InstanceId, -1), up, rowData.InstanceId + UiFormats.TagSeparator + ArmouryValues.StepUp);
                var down = Step(row, StringKeys.DeckEditorDown, rowData.CanDown, rowData.InstanceId + UiFormats.TagSeparator + ArmouryValues.StepDown);
                down.clicked += () => Apply(_editor.Move(rowData.InstanceId, 1), down, rowData.InstanceId + UiFormats.TagSeparator + ArmouryValues.StepDown);
            }
            var remove = Step(row, StringKeys.DeckEditorRemove, rowData.Removable, rowData.InstanceId + UiFormats.TagSeparator + ArmouryValues.StepRemove);
            remove.clicked += () => Apply(_editor.Remove(rowData.InstanceId), remove, rowData.InstanceId + UiFormats.TagSeparator + ArmouryValues.StepRemove);
        }

        private void Select(VisualElement row, string name, string id, string focusKey)
        {
            var button = new LocButton();
            button.SetResolved(name);
            button.AddToClassList(ArmouryClasses.RowName);
            button.userData = focusKey;
            button.clicked += () =>
            {
                _selected = id;
                _focusKey = focusKey;
                Render();
            };
            row.Add(button);
        }

        /// <summary>A step control (＋ － ▲ ▼): it dims when the rules would refuse it but stays pressable, so the refusal is said at the control.</summary>
        private LocButton Step(VisualElement row, string glyphKey, bool allowed, string focusKey)
        {
            var button = new LocButton(glyphKey);
            button.AddToClassList(ArmouryClasses.Step);
            button.EnableInClassList(ArmouryClasses.StepOff, !allowed);
            button.userData = focusKey;
            row.Add(button);
            return button;
        }
    }
}
