using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Refusal = Ashen.Presentation.UI.Kit.Refusal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Armoury's Equipment pane (web equipment.js buildArmoury, positionCard, positionGridCard, armamentListDetail, armamentGridDetail): "Equipped positions" with the number of items held and a toggle between
    /// the list view (a card per position with the piece's poker card as its thumbnail and the actions Inspect, Replace, Unequip and Make active, beside the inspection of the chosen piece) and the grid view
    /// (the slots' tiles beside a Details card). The view is kept in the profile's settings.
    /// </summary>
    public sealed partial class ArmouryScreen
    {
        private string _armamentView;
        private string _listSelection;
        private string _gridSelection;

        private string ArmamentView => _armamentView ??= _run.PlayerSettings.ArmouryArmamentView ?? Ui.Data.EquipmentCard.DefaultView;

        private void ToggleArmamentView()
        {
            _armamentView = _armamentView == ArmouryViewKeys.List ? ArmouryViewKeys.Grid : ArmouryViewKeys.List;
            _run.PlayerSettings.SetArmouryArmamentView(_armamentView);
            _focusKey = ArmouryPaneValues.ViewToggle;
            Render();
        }

        // ------------------------------------------------------------------ the pane

        private void EquipmentPane(VisualElement pane)
        {
            var positions = ArmouryPanes.Equipment(_run, Ui.Data);
            NoticeLine(pane);
            var card = new VisualElement();
            card.AddToClassList(ArmouryPaneClasses.EquipCard);
            pane.Add(card);
            var head = new VisualElement { pickingMode = PickingMode.Position };
            head.AddToClassList(ArmouryPaneClasses.EquipHead);
            card.Add(head);
            Label(head, Strings.Get(StringKeys.ArmouryEquipmentTitle), ArmouryClasses.Heading);
            Label(head, positions.Count(p => p.Item != null).ToString(CultureInfo.InvariantCulture), ArmouryPaneClasses.Count);
            var toggle = new LocButton();
            toggle.SetResolved(Strings.Get(ArmamentView == ArmouryViewKeys.List ? StringKeys.ArmouryViewGrid : StringKeys.ArmouryViewList));
            toggle.AddToClassList(ArmouryPaneClasses.ViewToggle);
            toggle.userData = ArmouryPaneValues.ViewToggle;
            toggle.clicked += ToggleArmamentView;
            head.Add(toggle);
            if (ArmamentView == ArmouryViewKeys.List) ListWorkspace(card, positions);
            else GridWorkspace(card, positions);
        }

        private static string PositionKey(ArmouryPositionView p) => p.SlotId + UiFormats.TagSeparator + p.SetIndex.ToString(CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ list view

        private void ListWorkspace(VisualElement card, IReadOnlyList<ArmouryPositionView> positions)
        {
            var work = new VisualElement();
            work.AddToClassList(ArmouryPaneClasses.ListWorkspace);
            card.Add(work);
            var list = new VisualElement();
            list.AddToClassList(ArmouryPaneClasses.PosList);
            work.Add(list);
            foreach (var position in positions) PositionCard(list, position);
            var aside = new VisualElement();
            aside.AddToClassList(ArmouryPaneClasses.ListInspect);
            work.Add(aside);
            var occupied = positions.Where(p => p.Card != null).ToList();
            var selected = occupied.FirstOrDefault(p => PositionKey(p) == _listSelection) ?? occupied.FirstOrDefault(p => p.Active) ?? occupied.FirstOrDefault();
            Label(aside, selected == null ? Strings.Get(StringKeys.ArmouryRowInspect) : Strings.Format(StringKeys.ArmouryEquipmentInspect, new StringArgs().Add(UiPlaceholders.Code, selected.Code)), ArmouryClasses.Heading);
            if (selected != null) Inspection(aside, selected.Card);
        }

        /// <summary>A position as the web's compact list draws it: locked is one line with how to open it; empty a heading and its actions; occupied the piece's card as its thumbnail beside the heading and the actions.</summary>
        private void PositionCard(VisualElement list, ArmouryPositionView position)
        {
            var key = PositionKey(position);
            var row = new VisualElement();
            row.AddToClassList(ArmouryPaneClasses.Pos);
            list.Add(row);
            if (position.Locked)
            {
                row.AddToClassList(ArmouryPaneClasses.PosLocked);
                Label(row, position.Code + ArmouryKeys.Space + ReceiptKeys.Dash, ArmouryClasses.Code);
                Label(row, Strings.Get(StringKeys.ArmouryRowLocked), ArmouryClasses.PositionName);
                Label(row, position.Hint.Length > 0 ? position.Hint : position.Label, ArmouryClasses.Muted);
                return;
            }
            var occupied = position.Card != null;
            row.EnableInClassList(ArmouryPaneClasses.PosOccupied, occupied);
            row.EnableInClassList(ArmouryPaneClasses.PosActive, position.Active);
            row.EnableInClassList(ArmouryPaneClasses.PosSelected, _pickSlot == position.SlotId && _pickIndex == position.SetIndex);
            if (occupied)
            {
                var thumb = new VisualElement { pickingMode = PickingMode.Ignore };
                thumb.AddToClassList(ArmouryPaneClasses.PosThumb);
                thumb.Add(new PokerCard(Ui, position.Card));
                row.Add(thumb);
            }
            var body = new VisualElement();
            body.AddToClassList(ArmouryPaneClasses.PosBody);
            row.Add(body);
            var heading = new VisualElement { pickingMode = PickingMode.Ignore };
            heading.AddToClassList(ArmouryPaneClasses.PosHeading);
            body.Add(heading);
            Label(heading, position.Code + ArmouryKeys.Space + ReceiptKeys.Dash, ArmouryClasses.Code);
            Label(heading, occupied ? position.Name : Strings.Get(StringKeys.ArmouryRowEmpty), ArmouryClasses.PositionName);
            var stateKey = occupied ? (position.Active ? StringKeys.ArmouryChipEquipped : StringKeys.ArmouryInventoryReserve) : (position.Active ? StringKeys.ArmouryChipActive : StringKeys.ArmouryInventoryReserve);
            Label(heading, Strings.Get(stateKey), ArmouryClasses.Tag, UiClasses.Upper);
            PositionActions(body, position, true);
        }

        /// <summary>
        /// positionActions: Inspect (an occupied position, in the list view), Replace or Choose item, Unequip, then Make active when the position is not the slot's active one. One the rules shut is disabled
        /// and says why beside it (the web's armament-action-reason).
        /// </summary>
        private void PositionActions(VisualElement parent, ArmouryPositionView position, bool inspect)
        {
            var key = PositionKey(position);
            var actions = new VisualElement();
            actions.AddToClassList(ArmouryClasses.Actions);
            parent.Add(actions);
            if (inspect && position.Item != null)
                Button(actions, StringKeys.ArmouryRowInspect, null, () =>
                {
                    _listSelection = key;
                    _focusKey = key;
                    Render();
                }, key);
            var replace = new LocButton();
            replace.SetResolved(position.ReplaceLabel);
            replace.AddToClassList(UiClasses.Button);
            replace.userData = key + UiFormats.TagSeparator + StringKeys.ArmouryActionReplace;
            replace.clicked += () => Pick(position);
            actions.Add(replace);
            if (position.Item != null) ShutAction(actions, StringKeys.ArmouryActionUnequip, key, position.UnequipRefusal, () => Say(_run.Equip(Strings, position.SlotId, position.SetIndex, null, Strings.Get(StringKeys.ArmouryActionUnequip)), key));
            if (!position.Active) ShutAction(actions, StringKeys.ArmouryActionActivate, key, position.ActivateRefusal, () => Say(_run.Activate(Strings, position.SlotId, position.SetIndex), key));
        }

        private void ShutAction(VisualElement actions, string labelKey, string key, string refusal, Action act)
        {
            var button = new LocButton(labelKey);
            button.AddToClassList(UiClasses.Button);
            button.userData = key + UiFormats.TagSeparator + labelKey;
            if (string.IsNullOrEmpty(refusal)) button.clicked += act;
            else button.SetEnabled(false);
            actions.Add(button);
            if (!string.IsNullOrEmpty(refusal)) Label(actions, refusal, ArmouryClasses.Reason);
        }

        /// <summary>Shows what an equipment change said (the web's notice) and redraws from the run; the control that was pressed keeps focus when it is still there.</summary>
        private void Say(Core2ArmouryChange change, string focusKey)
        {
            _notice = change.Message ?? string.Empty;
            if (change.Ok)
            {
                _pickSlot = null;
                _inventoryKey = null;
                _focusKey = focusKey;
            }
            Render();
            if (!change.Ok && _notice.Length > 0) Refusal.Show(_modal, Context.Overlay, _notice, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
        }

        private void NoticeLine(VisualElement pane)
        {
            var line = Label(pane, _notice, ArmouryClasses.Notice);
            UiDom.Show(line, _notice.Length > 0);
        }

        /// <summary>Replace / Choose item: the Inventory opens filtered to what fits the position (the web's openInventoryForSelection).</summary>
        private void Pick(ArmouryPositionView position)
        {
            _pickSlot = position.SlotId;
            _pickIndex = position.SetIndex;
            _inventoryKey = null;
            _notice = string.Empty;
            _tab = ArmouryValues.TabInventory;
            _focusKey = null;
            Render();
        }

        // ------------------------------------------------------------------ grid view

        private void GridWorkspace(VisualElement card, IReadOnlyList<ArmouryPositionView> positions)
        {
            var visible = positions;
            var selected = visible.FirstOrDefault(p => PositionKey(p) == _gridSelection)
                ?? visible.FirstOrDefault(p => p.Active && p.Card != null) ?? visible.FirstOrDefault(p => !p.Locked) ?? visible.FirstOrDefault();
            if (selected != null) _gridSelection = PositionKey(selected);
            var work = new VisualElement();
            work.AddToClassList(ArmouryPaneClasses.GridWorkspace);
            card.Add(work);
            var collection = new VisualElement();
            collection.AddToClassList(ArmouryPaneClasses.GridCollection);
            work.Add(collection);
            foreach (var group in visible.GroupBy(p => p.SlotId))
            {
                var section = new VisualElement();
                section.AddToClassList(ArmouryPaneClasses.GridGroup);
                collection.Add(section);
                Label(section, group.First().SlotLabel, ArmouryClasses.Eyebrow, UiClasses.Upper);
                var tiles = new VisualElement();
                tiles.AddToClassList(ArmouryPaneClasses.GridTiles);
                section.Add(tiles);
                foreach (var position in group) Tile(tiles, position, selected);
            }
            var detail = new VisualElement();
            detail.AddToClassList(ArmouryPaneClasses.GridDetail);
            work.Add(detail);
            Label(detail, Strings.Get(StringKeys.ArmouryDetailHeading), ArmouryClasses.Eyebrow, UiClasses.Upper);
            if (selected == null)
            {
                Label(detail, Strings.Get(StringKeys.ArmouryDetailNoPositions), ArmouryClasses.Muted);
                return;
            }
            if (selected.Card != null) ArmamentDetail(detail, selected);
            else Label(detail, Strings.Format(StringKeys.ArmouryDetailPositionState, new StringArgs().Add(UiPlaceholders.Label, selected.Label)
                .Add(UiPlaceholders.State, Strings.Get(selected.Locked ? StringKeys.ArmouryDetailStateLocked : StringKeys.ArmouryDetailStateEmpty))), ArmouryClasses.Muted);
            if (!selected.Locked) PositionActions(detail, selected, false);
        }

        /// <summary>A grid tile is the position stood on end: its art over its name, the code as its badge.</summary>
        private void Tile(VisualElement tiles, ArmouryPositionView position, ArmouryPositionView selected)
        {
            var key = PositionKey(position);
            var tile = new VisualElement { focusable = true };
            tile.AddToClassList(ArmouryPaneClasses.Tile);
            tile.EnableInClassList(ArmouryPaneClasses.TileSelected, selected != null && PositionKey(selected) == key);
            tile.EnableInClassList(ArmouryPaneClasses.TileLocked, position.Locked);
            tile.userData = key;
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList(ArmouryPaneClasses.TileArt);
            if (position.Locked)
            {
                var lockIcon = Ui.Icon(ArmouryPaneValues.IconLock);
                if (lockIcon != null) art.style.backgroundImage = new StyleBackground(lockIcon);
            }
            else if (position.Card?.ArtId != null)
            {
                var texture = Ui.Texture(position.Card.ArtId);
                if (texture != null) art.style.backgroundImage = new StyleBackground(texture);
            }
            tile.Add(art);
            Label(tile, position.Locked ? Strings.Get(StringKeys.ArmouryRowLocked) : position.Name, ArmouryClasses.PositionName);
            Label(tile, position.Code, ArmouryClasses.Tag, UiClasses.Upper);
            tile.RegisterCallback<ClickEvent>(_ =>
            {
                if (position.Locked)
                {
                    Refusal.Show(tile, Context.Overlay, position.Hint.Length > 0 ? position.Hint : Strings.Get(StringKeys.ArmouryDetailLockedTip), Ui.Data.Tokens.Duration(TokenKeys.Refusal));
                    return;
                }
                _gridSelection = key;
                _focusKey = key;
                Render();
            });
            tiles.Add(tile);
        }

        /// <summary>armamentDetail: the piece's inspection with its lore, its facts (type, effects, combat bonuses, smithing tier, the armament's intrinsic numbers) and its tags.</summary>
        private void ArmamentDetail(VisualElement detail, ArmouryPositionView position)
        {
            var box = new VisualElement();
            box.AddToClassList(ArmouryClasses.CardBox);
            detail.Add(box);
            Label(box, position.SlotLabel, ArmouryClasses.Eyebrow, UiClasses.Upper);
            Label(box, Strings.Format(StringKeys.ArmouryDetailTitle, new StringArgs().Add(UiPlaceholders.Name, position.Name)), ArmouryClasses.CardTitle);
            Inspection(box, position.Card);
            Label(box, position.Lore, ArmouryClasses.Line, PokerClasses.Lore);
            foreach (var fact in position.DetailFacts)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList(ArmouryPaneClasses.FactRow);
                Label(row, fact.Key, ArmouryClasses.Muted);
                Label(row, fact.Value, ArmouryClasses.Line);
                box.Add(row);
            }
            var tags = new VisualElement { pickingMode = PickingMode.Position };
            tags.AddToClassList(ArmouryPaneClasses.ChipRow);
            box.Add(tags);
            if (position.TagRows.Count == 0) Label(tags, Strings.Get(StringKeys.ArmouryDetailNoTags), ArmouryClasses.Muted);
            foreach (var tag in position.TagRows) Chip(tags, tag.Label, tag.Explanation);
        }

        // ------------------------------------------------------------------ the poker card's inspection

        /// <summary>
        /// renderEquipmentInspection: the piece's card, the chips of what it is (its type, its requirement when it is not a minimum, its tags and its rarity, each said once and each with its explanation on hover),
        /// its facts as chips, its effects and its minimum as lines, and its lore.
        /// </summary>
        private void Inspection(VisualElement parent, EquipmentCardModel model, int owned = -1)
        {
            var wrap = new VisualElement();
            wrap.AddToClassList(ArmouryPaneClasses.InspectionBox);
            parent.Add(wrap);
            wrap.Add(new PokerCard(Ui, model, owned));
            var chips = new VisualElement { pickingMode = PickingMode.Position };
            chips.AddToClassList(ArmouryPaneClasses.ChipRow);
            wrap.Add(chips);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Once(string label, string explanation)
            {
                if (string.IsNullOrEmpty(label) || !seen.Add(label)) return;
                Chip(chips, char.ToUpperInvariant(label[0]) + label.Substring(1), explanation);
            }
            Once(model.Armor ? model.Type : model.Type.Split(new[] { Strings.Get(StringKeys.ArmouryCardTypeSplit) }, StringSplitOptions.None)[0], model.TypeExplanation);
            if (!model.HasMinimum) Once(model.Requirement, model.RequirementExplanation);
            foreach (var tag in model.Tags) Once(tag.Label, tag.Explanation);
            Once(model.Rarity, Strings.Format(StringKeys.ArmouryCardRarityTip, new StringArgs().Add(UiPlaceholders.Rarity, model.Rarity)));
            if (model.Facts.Count > 0)
            {
                var facts = new VisualElement { pickingMode = PickingMode.Position };
                facts.AddToClassList(ArmouryPaneClasses.ChipRow);
                wrap.Add(facts);
                foreach (var fact in model.Facts)
                    Chip(facts, Strings.Format(StringKeys.ArmouryCardFactChip, new StringArgs().Add(UiPlaceholders.Label, fact.Label).Add(UiPlaceholders.Value, fact.Value)), fact.Explanation);
            }
            foreach (var effect in model.Effects) Label(wrap, effect.Label, ArmouryClasses.Line);
            if (model.HasMinimum) Label(wrap, model.Requirement, ArmouryClasses.Line);
            Label(wrap, model.Flavor, ArmouryClasses.Muted, PokerClasses.Lore);
        }

        /// <summary>An inspection chip (the web's inspectionTag): a word with its explanation on hover.</summary>
        private LocLabel Chip(VisualElement row, string label, string explanation)
        {
            var chip = new LocLabel { pickingMode = PickingMode.Position, focusable = true };
            chip.AddToClassList(ArmouryPaneClasses.ChipTag);
            chip.SetResolved(label);
            row.Add(chip);
            if (!string.IsNullOrEmpty(explanation))
                Tooltip.AttachResolved(chip, Context.Overlay, Ui.Data.Components.TooltipPlain, label, explanation, Ui.Data.Tokens.Duration(TokenKeys.TooltipDelay));
            return chip;
        }
    }
}
