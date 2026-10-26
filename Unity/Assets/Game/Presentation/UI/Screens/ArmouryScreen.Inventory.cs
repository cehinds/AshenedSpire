using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;
using Refusal = Ashen.Presentation.UI.Kit.Refusal;
using UnityEngine;
using UnityEngine.UIElements;
using RV = Ashen.Generated.Core2RewardsValues;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Armoury's Inventory (web equipment.js inventoryBlock, inventoryFace, inventoryReveal, pickerTable, selectionFacts, destinationChoices): the class and everything the run carries as faces on one side (a piece of
    /// armour or an armament is its poker card with its count and where it is worn; the class is its icon, name and the Core slot) and the chosen item's detail on the other: the destinations it can go to, the card with
    /// its inspection, the comparison with what is equipped, whether it can go on, and its action. While a position is being filled the faces give way to the picker table of what fits it.
    /// </summary>
    public sealed partial class ArmouryScreen
    {
        private bool _pickerShowAll;
        private bool _pickerByName;
        private bool _pickerDescending;

        /// <summary>The chosen item's own action (also the footer's primary): its label, why it is shut, and what it does.</summary>
        private sealed class FootPlan
        {
            public string Label;
            public string Refusal;
            public bool Danger;
            public Action Act;
        }

        private FootPlan _footPlan;

        // ------------------------------------------------------------------ the pane

        private void InventoryPane(VisualElement pane)
        {
            var rows = ArmouryInventory.Rows(_run, Ui.Data);
            _footPlan = null;
            Label(pane, Strings.Get(StringKeys.ArmouryRailInventory), ArmouryClasses.Heading);
            NoticeLine(pane);
            if (_pickSlot != null)
            {
                Picker(pane, rows);
                return;
            }
            if (rows.Count == 0)
            {
                Label(pane, Strings.Get(StringKeys.ArmouryInventoryEmpty), ArmouryClasses.Muted);
                return;
            }
            var split = new VisualElement();
            split.AddToClassList(ArmouryPaneClasses.InvSplit);
            pane.Add(split);
            var collection = new VisualElement();
            collection.AddToClassList(ArmouryPaneClasses.InvCollection);
            split.Add(collection);
            var detail = new VisualElement();
            detail.AddToClassList(ArmouryPaneClasses.InvDetail);
            split.Add(detail);
            foreach (var row in rows) Face(collection, row);
            var selected = rows.FirstOrDefault(r => r.Key == _inventoryKey);
            if (selected == null) Label(detail, Strings.Get(ArmouryWebKeys.DetailPrompt), ArmouryClasses.Muted);
            else Detail(detail, selected, null);
        }

        // ------------------------------------------------------------------ faces

        private void Face(VisualElement collection, ArmouryInventoryRow row)
        {
            var face = new VisualElement { focusable = true, pickingMode = PickingMode.Position };
            face.AddToClassList(ArmouryPaneClasses.Face);
            face.EnableInClassList(ArmouryPaneClasses.FaceOn, _inventoryKey == row.Key);
            face.userData = row.Key;
            collection.Add(face);
            if (row.Equipable && row.Piece != null)
            {
                face.AddToClassList(ArmouryPaneClasses.FacePoker);
                face.Add(new PokerCard(Ui, EquipmentCards.For(_run, Ui.Data, row.Piece), row.Count));
            }
            else
            {
                var art = new VisualElement { pickingMode = PickingMode.Ignore };
                art.AddToClassList(ArmouryPaneClasses.FaceArt);
                Art(art, row);
                face.Add(art);
                Label(face, row.Name, ArmouryClasses.PositionName);
            }
            var trail = new VisualElement { pickingMode = PickingMode.Ignore };
            trail.AddToClassList(ArmouryPaneClasses.FaceTrail);
            face.Add(trail);
            Label(trail, row.Category, ArmouryClasses.Tag, UiClasses.Upper);
            Label(trail, Strings.Format(StringKeys.ArmouryInventoryCount, new StringArgs().Add(UiPlaceholders.Count, row.Count)), ArmouryClasses.Tag);
            if (row.EquippedLabels.Count > 0)
            {
                var single = row.EquippedLabels.Count == 1 && row.EquippedLabels[0] == Strings.Get(StringKeys.ArmouryInventoryEquipped);
                Label(trail, single ? row.EquippedLabels[0] : Strings.Format(StringKeys.ArmouryInventoryEquippedAt, new StringArgs().Add(UiPlaceholders.Names, string.Join(Strings.Get(StringKeys.ArmouryFacesJoin), row.EquippedLabels))),
                    ArmouryClasses.Tag, ArmouryPaneClasses.TagOn);
            }
            face.RegisterCallback<ClickEvent>(_ =>
            {
                _inventoryKey = _inventoryKey == row.Key ? null : row.Key;
                _focusKey = row.Key;
                Render();
            });
        }

        /// <summary>The picture of a row: the class's icon, a relic's or potion's art, a piece's picture.</summary>
        private void Art(VisualElement art, ArmouryInventoryRow row)
        {
            Texture2D texture = null;
            if (row.IsClass)
            {
                texture = Ui.Icon(row.Glyph);
                art.AddToClassList(ArmouryPaneClasses.FaceIcon);
            }
            else if (row.Equipable && row.Piece != null) texture = Ui.Texture(EquipmentCards.ArtId(_run, Ui.Data, row.Piece));
            else
            {
                var kind = row.Category == Strings.Get(StringKeys.ArmouryInventoryRelic) ? RV.KindRelics : RV.KindPotions;
                texture = Ui.Texture(Core2ItemFaces.Build(_run, Ui.Data, kind, row.Id, row.Name).ArtId);
            }
            if (texture != null) art.style.backgroundImage = new StyleBackground(texture);
        }

        // ------------------------------------------------------------------ the chosen item

        private void Detail(VisualElement detail, ArmouryInventoryRow row, ArmouryInventory.PickerRow picker)
        {
            if (!row.Equipable)
            {
                PlainDetail(detail, row);
                return;
            }
            var selection = ArmouryInventory.Select(_run, Ui.Data, row, _pickSlot, _pickIndex);
            if (_pickSlot == null) Destinations(detail, row);
            Inspection(detail, EquipmentCards.For(_run, Ui.Data, row.Piece), row.Count);
            Label(detail, selection.Instruction, ArmouryClasses.Muted, PokerClasses.Lore);
            if (selection.Candidate != null)
            {
                var compare = new VisualElement();
                compare.AddToClassList(ArmouryPaneClasses.CompareBox);
                detail.Add(compare);
                Label(compare, Strings.Format(StringKeys.ArmouryReceiptCompareTitle, new StringArgs().Add(UiPlaceholders.Name, row.Name)), ArmouryClasses.CardTitle);
                Label(compare, Strings.Get(StringKeys.ArmouryReceiptCompare), ArmouryClasses.Eyebrow, UiClasses.Upper);
                ReceiptPanel.Fill(compare, selection.Candidate, Strings);
            }
            if (selection.Action != null)
            {
                var facts = new VisualElement();
                facts.AddToClassList(ArmouryPaneClasses.CompareBox);
                detail.Add(facts);
                Label(facts, Strings.Get(ArmouryWebKeys.CompareTitle), ArmouryClasses.Eyebrow, UiClasses.Upper);
                if (selection.Relation.Length > 0) Label(facts, selection.Relation, ArmouryClasses.Line);
                if (selection.Candidate != null)
                {
                    if (selection.Unchanged) Label(facts, Strings.Get(ArmouryWebKeys.CompareUnchanged), ArmouryClasses.Muted);
                    foreach (var line in selection.RoleLines) Label(facts, line, ArmouryClasses.Line);
                }
                var eligibility = Label(facts, selection.EligibilityText, ArmouryClasses.Line);
                eligibility.EnableInClassList(ArmouryClasses.ReceiptUnmet, selection.Eligibility == ArmouryInventory.EligibilityState.Blocked);
                ActionButton(detail, row, selection.Action);
            }
        }

        /// <summary>The destination choices (destinationChoices): every position of every slot that fits the piece, each naming what is there; a locked one is shut.</summary>
        private void Destinations(VisualElement detail, ArmouryInventoryRow row)
        {
            var section = new VisualElement();
            section.AddToClassList(ArmouryPaneClasses.CompareBox);
            detail.Add(section);
            Label(section, Strings.Get(StringKeys.ArmouryDestinationTitle), ArmouryClasses.CardTitle);
            Label(section, Strings.Get(StringKeys.ArmouryDestinationHint), ArmouryClasses.Muted);
            var options = new VisualElement();
            options.AddToClassList(ArmouryClasses.InvDests);
            section.Add(options);
            foreach (var destination in ArmouryInventory.Destinations(_run, Ui.Data, row.Id))
            {
                var d = destination;
                var button = new LocButton();
                button.SetResolved(d.Label + ArmouryKeys.Space + d.Occupant);
                button.AddToClassList(ArmouryClasses.InvDest);
                button.EnableInClassList(ArmouryClasses.InvDestShut, d.Locked);
                button.userData = row.Key + UiFormats.TagSeparator + d.SlotId + d.SetIndex.ToString(CultureInfo.InvariantCulture);
                button.clicked += () =>
                {
                    if (d.Locked) return;
                    _pickSlot = d.SlotId;
                    _pickIndex = d.SetIndex;
                    _inventoryKey = row.Key;
                    _focusKey = null;
                    Render();
                };
                options.Add(button);
            }
        }

        /// <summary>The item's own action, as the detail and the footer carry it (the web's ep-equip button and footerAction): Equip to, Move to or Unequip from a position; one the rules shut says why where it is pressed.</summary>
        private void ActionButton(VisualElement detail, ArmouryInventoryRow row, ArmouryActionView action)
        {
            var key = row.Key;
            void Act()
            {
                if (action.Refusal.Length > 0)
                {
                    _notice = action.Refusal;
                    Refusal.Show(_modal, Context.Overlay, action.Refusal, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
                    return;
                }
                Say(_run.Equip(Strings, action.SlotId, action.SetIndex, action.PieceId, action.Label), key);
            }
            var button = new LocButton();
            button.SetResolved(action.Label);
            button.AddToClassList(UiClasses.Button);
            button.AddToClassList(action.Kind == ArmouryActionKind.Unequip ? UiClasses.ButtonBack : UiClasses.ButtonPrimary);
            button.EnableInClassList(ArmouryClasses.ActionOff, action.Refusal.Length > 0);
            button.userData = key + UiFormats.TagSeparator + action.Label;
            button.clicked += Act;
            detail.Add(button);
            _footPlan = new FootPlan { Label = action.Label, Refusal = action.Refusal, Danger = action.Kind == ArmouryActionKind.Unequip, Act = Act };
        }

        /// <summary>The class, a relic or a potion: a picture, its name, "Class · standard · 1 owned", what it says, its tags.</summary>
        private void PlainDetail(VisualElement detail, ArmouryInventoryRow row)
        {
            var box = new VisualElement();
            box.AddToClassList(ArmouryPaneClasses.PlainDetail);
            detail.Add(box);
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList(ArmouryPaneClasses.DetailArt);
            Art(art, row);
            box.Add(art);
            var info = new VisualElement();
            info.AddToClassList(ArmouryClasses.Info);
            box.Add(info);
            Label(info, row.Name, ArmouryClasses.Name);
            Label(info, Strings.Format(StringKeys.ArmouryDetailKind, new StringArgs().Add(ReceiptPlaceholders.Category, row.Category)
                .Add(UiPlaceholders.Rarity, Strings.Get(StringKeys.ArmouryDetailStandard)).Add(UiPlaceholders.Count, row.Count)), ArmouryClasses.Eyebrow, UiClasses.Upper);
            string description;
            if (row.IsClass) description = row.Description;
            else
            {
                var kind = row.Category == Strings.Get(StringKeys.ArmouryInventoryRelic) ? RV.KindRelics : RV.KindPotions;
                description = Core2ItemFaces.Build(_run, Ui.Data, kind, row.Id, row.Name).Effect;
            }
            Label(info, string.IsNullOrEmpty(description) ? Strings.Get(StringKeys.ArmouryDetailNoInfo) : description, ArmouryClasses.Line);
            if (row.ClassTags.Count > 0)
            {
                var tags = new VisualElement { pickingMode = PickingMode.Position };
                tags.AddToClassList(ArmouryPaneClasses.ChipRow);
                info.Add(tags);
                foreach (var tag in row.ClassTags) Chip(tags, tag, null);
            }
        }

        // ------------------------------------------------------------------ the picker table

        private void Picker(VisualElement pane, IReadOnlyList<ArmouryInventoryRow> rows)
        {
            var gear = _run.Core.Deck.Run.Run;
            var slot = gear.EquipmentRows(CombatKeys.Slots).First(s => s.Str(CombatKeys.Id) == _pickSlot);
            var positions = ArmouryPanes.Equipment(_run, Ui.Data);
            var here = positions.FirstOrDefault(p => p.SlotId == _pickSlot && p.SetIndex == _pickIndex);
            var slotLabel = slot.Str(CombatKeys.Label) ?? _pickSlot;
            var context = new VisualElement();
            context.AddToClassList(ArmouryClasses.Selecting);
            Label(context, Strings.Format(StringKeys.ArmouryInventorySelecting, new StringArgs()
                .Add(UiPlaceholders.Slot, ArmouryInventory.Where(Strings, slot, _pickIndex))
                .Add(UiPlaceholders.State, Strings.Get(here?.Active == true ? StringKeys.ArmouryChipActive : StringKeys.ArmouryInventoryReserve))
                .Add(UiPlaceholders.Name, here?.Name ?? string.Empty)), ArmouryClasses.Line);
            Button(context, StringKeys.ArmouryDestinationShowAll, null, () =>
            {
                _pickSlot = null;
                Render();
            });
            pane.Add(context);
            var table = ArmouryInventory.PickerRows(_run, rows, _pickSlot, _pickIndex, _pickerByName, _pickerDescending);
            var shown = _pickerShowAll ? table : table.Where(r => r.Fits).ToList();
            var filter = new LocButton();
            filter.SetResolved(Strings.Get(_pickerShowAll ? StringKeys.ArmouryPickerFitsOnly : StringKeys.ArmouryPickerShowAll));
            filter.AddToClassList(UiClasses.Button);
            filter.userData = ArmouryPaneValues.PickerFilter;
            filter.clicked += () =>
            {
                _pickerShowAll = !_pickerShowAll;
                _focusKey = ArmouryPaneValues.PickerFilter;
                Render();
            };
            pane.Add(filter);
            if (shown.Count == 0)
            {
                Label(pane, Strings.Format(StringKeys.ArmouryInventoryFitsNone, new StringArgs().Add(UiPlaceholders.Slot, slotLabel)), ArmouryClasses.Muted);
                return;
            }
            var grid = new VisualElement();
            grid.AddToClassList(ArmouryPaneClasses.PickerTable);
            pane.Add(grid);
            var head = new VisualElement();
            head.AddToClassList(ArmouryPaneClasses.PickerHead);
            grid.Add(head);
            SortHead(head, StringKeys.ArmouryPickerColItem, true);
            SortHead(head, StringKeys.ArmouryPickerColStatus, false);
            foreach (var entry in shown) PickerRow(grid, entry, slot, slotLabel);
        }

        private void SortHead(VisualElement head, string labelKey, bool byName)
        {
            var button = new LocButton();
            var active = _pickerByName == byName;
            button.SetResolved(Strings.Get(labelKey) + (active ? ArmouryKeys.Space + Strings.Get(_pickerDescending ? StringKeys.ArmourySortDescending : StringKeys.ArmourySortAscending) : string.Empty));
            button.AddToClassList(ArmouryPaneClasses.PickerSort);
            button.userData = ArmouryPaneValues.PickerSort + labelKey;
            button.clicked += () =>
            {
                if (_pickerByName == byName) _pickerDescending = !_pickerDescending;
                else
                {
                    _pickerByName = byName;
                    _pickerDescending = false;
                }
                _focusKey = ArmouryPaneValues.PickerSort + labelKey;
                Render();
            };
            head.Add(button);
        }

        private void PickerRow(VisualElement grid, ArmouryInventory.PickerRow entry, JObject slot, string slotLabel)
        {
            var row = entry.Row;
            var open = _inventoryKey == row.Key;
            var tr = new VisualElement();
            tr.AddToClassList(ArmouryPaneClasses.PickerRow);
            tr.EnableInClassList(ArmouryPaneClasses.PickerRowOpen, open);
            tr.EnableInClassList(ArmouryPaneClasses.PickerRowShut, !entry.Fits);
            grid.Add(tr);
            var line = new VisualElement();
            line.AddToClassList(ArmouryPaneClasses.PickerLine);
            tr.Add(line);
            var thumb = new VisualElement { pickingMode = PickingMode.Ignore };
            thumb.AddToClassList(ArmouryPaneClasses.PickerThumb);
            Art(thumb, row);
            line.Add(thumb);
            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList(ArmouryClasses.InvText);
            line.Add(text);
            Label(text, row.Name, ArmouryClasses.PositionName);
            var meta = new List<string> { row.Category };
            if (entry.Status == ArmouryInventory.PickerStatus.NoFit)
                meta.Add(entry.Reason == PickerKeys.ReasonKind ? Strings.Format(StringKeys.ArmouryPickerReasonKind, new StringArgs().Add(UiPlaceholders.Slot, slotLabel)) : Strings.Get(StringKeys.ArmouryPickerReasonHand));
            var first = entry.Elsewhere.FirstOrDefault();
            string SlotName(string id) => _run.Core.Deck.Run.Run.EquipmentRows(CombatKeys.Slots).First(s => s.Str(CombatKeys.Id) == id).Str(CombatKeys.Label) ?? id;
            if (entry.Status == ArmouryInventory.PickerStatus.Elsewhere && row.Count == 1)
                meta.Add(Strings.Format(StringKeys.ArmouryPickerOnlyCopy, new StringArgs().Add(UiPlaceholders.Slot, SlotName(first.Key)).Add(UiPlaceholders.N, first.Value + 1)));
            Label(text, string.Join(Strings.Get(StringKeys.ArmouryInventoryMetaJoin), meta), ArmouryClasses.Muted);
            var status = entry.Status == ArmouryInventory.PickerStatus.Elsewhere
                ? Strings.Format(StringKeys.ArmouryPickerStatusElsewhere, new StringArgs().Add(UiPlaceholders.Slot, SlotName(first.Key)).Add(UiPlaceholders.N, first.Value + 1))
                : Strings.Get(entry.Status == ArmouryInventory.PickerStatus.Here ? StringKeys.ArmouryPickerStatusHere : entry.Status == ArmouryInventory.PickerStatus.Free ? StringKeys.ArmouryPickerStatusFree : StringKeys.ArmouryPickerStatusNofit);
            Label(line, status, ArmouryClasses.Tag, ArmouryPaneClasses.TagOn);
            var actions = new VisualElement();
            actions.AddToClassList(ArmouryClasses.Actions);
            line.Add(actions);
            var inspect = new LocButton();
            inspect.SetResolved(Strings.Get(open ? StringKeys.ArmouryPickerHide : StringKeys.ArmouryRowInspect));
            inspect.AddToClassList(UiClasses.Button);
            inspect.userData = row.Key;
            inspect.clicked += () =>
            {
                _inventoryKey = open ? null : row.Key;
                _focusKey = row.Key;
                Render();
            };
            actions.Add(inspect);
            if (entry.Fits)
            {
                var action = ArmouryInventory.Action(_run, Ui.Data, row.Id, _pickSlot, _pickIndex);
                var act = new LocButton();
                act.SetResolved(Strings.Get(action.Kind == ArmouryActionKind.Unequip ? StringKeys.ArmouryPickerActUnequip : action.Kind == ArmouryActionKind.Move ? StringKeys.ArmouryPickerActMove : StringKeys.ArmouryPickerActEquip));
                act.AddToClassList(UiClasses.Button);
                act.AddToClassList(action.Kind == ArmouryActionKind.Unequip ? UiClasses.ButtonBack : UiClasses.ButtonPrimary);
                act.EnableInClassList(ArmouryClasses.ActionOff, action.Refusal.Length > 0);
                act.userData = row.Key + UiFormats.TagSeparator + action.Label;
                act.clicked += () =>
                {
                    if (action.Refusal.Length > 0)
                    {
                        _notice = action.Refusal;
                        Refusal.Show(act, Context.Overlay, action.Refusal, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
                        return;
                    }
                    Say(_run.Equip(Strings, action.SlotId, action.SetIndex, action.PieceId, action.Label), row.Key);
                };
                actions.Add(act);
            }
            if (open)
            {
                var reveal = new VisualElement();
                reveal.AddToClassList(ArmouryPaneClasses.PickerReveal);
                tr.Add(reveal);
                Detail(reveal, row, entry);
            }
        }
    }
}
