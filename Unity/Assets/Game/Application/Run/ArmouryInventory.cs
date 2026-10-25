using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Run
{
    /// <summary>What a press on an inventory row does at a chosen position (web model/armouryLayout.js inventorySelectionAction).</summary>
    public enum ArmouryActionKind
    {
        Equip,
        Move,
        Unequip,
    }

    /// <summary>One position an item could go to: the slot's name and the set number, whether it is open, and what is there now.</summary>
    public sealed class ArmouryDestinationView
    {
        public string SlotId;
        public int SetIndex;

        /// <summary>"Main hand · 1" (the slot's label and the set's number).</summary>
        public string Label;

        public bool Active;
        public bool Locked;

        /// <summary>"Active · Greatsword" or the way a locked position opens.</summary>
        public string Occupant;
    }

    /// <summary>The one action an inventory row offers at a chosen position: its kind, its words ("Equip to Main hand · 1"), and the rules' refusal (empty when it can be done).</summary>
    public sealed class ArmouryActionView
    {
        public ArmouryActionKind Kind;
        public string Label;
        public string SlotId;
        public int SetIndex;

        /// <summary>The piece to put there, or null for an unequip.</summary>
        public string PieceId;

        public string Refusal = string.Empty;
    }

    /// <summary>One row of the Armoury's Inventory (web model/inventoryPresentation.js inventoryRows): an owned piece of armour or armament, a relic or a carried potion.</summary>
    public sealed class ArmouryInventoryRow
    {
        public string Key;
        public string Id;
        public string Name;

        /// <summary>"Armour", "Weapon", "Shield", "Staff", "Relic" or "Potion".</summary>
        public string Category;

        public int Count = 1;

        /// <summary>The slots that hold the piece now ("Main hand").</summary>
        public readonly List<string> EquippedLabels = new List<string>();

        /// <summary>The piece's facts (armour and armaments), or null.</summary>
        public ArmouryItemView Item;

        /// <summary>The piece can be put into a position (armour and armaments).</summary>
        public bool Equipable;
    }

    /// <summary>The Armoury's Inventory over a Core 2 run (D-1160): the rows, the positions each can go to, and the action a chosen position gives. Engine-free.</summary>
    public static class ArmouryInventory
    {
        public static IReadOnlyList<ArmouryInventoryRow> Rows(Core2RunSession session, UiData ui)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var data = session.CombatData;
            var gear = session.Core.Deck.Run.Run;
            var loadout = run.Obj(K.Loadout);
            var rows = new List<ArmouryInventoryRow>();
            var owned = Armoury.Ownership(gear, loadout);
            var locations = Locations(gear, loadout);
            foreach (var piece in Js.Items(data.Equipment[K.Armour]).OfType<JObject>().Where(p => p.Str(K.ClassId) == session.ClassId && owned(p)))
                rows.Add(Piece(session, strings, piece, strings.Get(StringKeys.ArmouryInventoryArmour), locations));
            foreach (var piece in Js.Items(data.Equipment[K.Armaments]).OfType<JObject>().Where(owned))
                rows.Add(Piece(session, strings, piece, Named(strings, ArmouryKeys.KindStringKey, piece.Str(K.Kind)), locations));
            foreach (var group in Js.Items(run[K.Relics]).Select(Js.Str).Where(id => id != null && data.Relics.Has(id)).GroupBy(id => id))
                rows.Add(new ArmouryInventoryRow { Key = StringKeys.ArmouryInventoryRelic + group.Key, Id = group.Key, Name = data.Relics.Get(group.Key).Str(K.Name) ?? group.Key, Category = strings.Get(StringKeys.ArmouryInventoryRelic), Count = group.Count() });
            foreach (var group in Js.Items(run[K.Flasks]).OfType<JObject>().Select(f => f.Str(K.FlaskId)).Where(id => id != null && data.Flasks.Has(id)).GroupBy(id => id))
                rows.Add(new ArmouryInventoryRow { Key = StringKeys.ArmouryInventoryPotion + group.Key, Id = group.Key, Name = data.Flasks.Get(group.Key).Str(K.Name) ?? group.Key, Category = strings.Get(StringKeys.ArmouryInventoryPotion), Count = group.Count() });
            return rows;
        }

        private static ArmouryInventoryRow Piece(Core2RunSession session, StringTable strings, JObject piece, string category, Dictionary<string, List<string>> locations)
        {
            var id = piece.Str(K.Id);
            var row = new ArmouryInventoryRow
            {
                Key = category + ArmouryKeys.Space + id,
                Id = id,
                Name = piece.Str(K.Name) ?? id,
                Category = category,
                Item = ArmouryPanes.Item(session, strings, null, id),
                Equipable = true,
            };
            if (locations.TryGetValue(id, out var labels)) row.EquippedLabels.AddRange(labels);
            return row;
        }

        private static string Named(StringTable strings, string format, string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            var key = string.Format(CultureInfo.InvariantCulture, format, id);
            return strings.Has(key) ? strings.Get(key) : id;
        }

        private static Dictionary<string, List<string>> Locations(Ashen.Domain.Run.RunData gear, JObject loadout)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (loadout == null) return map;
            foreach (var slot in gear.EquipmentRows(K.Slots))
            {
                var label = slot.Str(K.Label) ?? slot.Str(K.Id);
                foreach (var id in Js.Items(loadout.Obj(K.Sets)?[slot.Str(K.Id)]).Select(Js.Str).Where(id => !string.IsNullOrEmpty(id)))
                {
                    if (!map.TryGetValue(id, out var labels)) map[id] = labels = new List<string>();
                    if (!labels.Contains(label)) labels.Add(label);
                }
            }
            return map;
        }

        /// <summary>The slots a piece fits (fitsSlot), with their positions: every opened set and the locked ones too (shut, with their hint).</summary>
        public static IReadOnlyList<ArmouryDestinationView> Destinations(Core2RunSession session, UiData ui, string pieceId)
        {
            var strings = ui.Strings;
            var gear = session.Core.Deck.Run.Run;
            var piece = Piece(session, pieceId);
            var list = new List<ArmouryDestinationView>();
            if (piece == null) return list;
            var fitting = gear.EquipmentRows(K.Slots).Where(slot => Armoury.FitsSlot(gear, slot, piece)).Select(slot => slot.Str(K.Id)).ToHashSet(StringComparer.Ordinal);
            foreach (var position in ArmouryPanes.Equipment(session, ui).Where(p => fitting.Contains(p.SlotId)))
            {
                var slot = gear.EquipmentRows(K.Slots).First(s => s.Str(K.Id) == position.SlotId);
                list.Add(new ArmouryDestinationView
                {
                    SlotId = position.SlotId,
                    SetIndex = position.SetIndex,
                    Label = Where(strings, slot, position.SetIndex),
                    Active = position.Active,
                    Locked = position.Locked,
                    Occupant = position.Locked ? position.Hint : strings.Format(StringKeys.ArmouryDestinationOccupant, new StringArgs()
                        .Add(UiPlaceholders.State, strings.Get(position.Active ? StringKeys.ArmouryChipActive : StringKeys.ArmouryInventoryReserve)).Add(UiPlaceholders.Name, position.Name)),
                });
            }
            return list;
        }

        /// <summary>"Main hand · 1": the slot's label and the set's number.</summary>
        public static string Where(StringTable strings, JObject slot, int setIndex) =>
            strings.Format(StringKeys.ArmouryChangePosition, new StringArgs().Add(UiPlaceholders.Label, slot.Str(K.Label) ?? slot.Str(K.Id)).Add(UiPlaceholders.N, setIndex + 1));

        /// <summary>
        /// The action an item gives at a chosen position (inventorySelectionAction and the row's seal): Unequip when the position already holds it, Move when it is worn elsewhere, else Equip;
        /// the refusal is the equip rules' (canEquip, the storage a displaced piece needs).
        /// </summary>
        public static ArmouryActionView Action(Core2RunSession session, UiData ui, string pieceId, string slotId, int setIndex)
        {
            var strings = ui.Strings;
            var gear = session.Core.Deck.Run.Run;
            var loadout = session.Run.Obj(K.Loadout);
            var slot = gear.EquipmentRows(K.Slots).First(s => s.Str(K.Id) == slotId);
            var here = Js.Str(loadout?.Obj(K.Sets)?.Arr(slotId)?[setIndex]);
            var elsewhere = loadout != null && (loadout.Obj(K.Sets) ?? new JObject()).Properties()
                .Any(p => Js.Items(p.Value).Select((value, index) => (value, index)).Any(x => Js.Str(x.value) == pieceId && (p.Name != slotId || x.index != setIndex)));
            var kind = here == pieceId ? ArmouryActionKind.Unequip : elsewhere ? ArmouryActionKind.Move : ArmouryActionKind.Equip;
            var place = Where(strings, slot, setIndex);
            var key = kind == ArmouryActionKind.Unequip ? StringKeys.ArmouryActionUnequipFrom : kind == ArmouryActionKind.Move ? StringKeys.ArmouryActionMoveTo : StringKeys.ArmouryActionEquipTo;
            var action = new ArmouryActionView
            {
                Kind = kind,
                Label = strings.Format(key, new StringArgs().Add(UiPlaceholders.Slot, place)),
                SlotId = slotId,
                SetIndex = setIndex,
                PieceId = kind == ArmouryActionKind.Unequip ? null : pieceId,
            };
            action.Refusal = session.EquipRefusal(strings, slotId, setIndex, action.PieceId) ?? string.Empty;
            return action;
        }

        private static JObject Piece(Core2RunSession session, string id)
        {
            var data = session.CombatData;
            return Js.Items(data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id)
                   ?? Js.Items(data.Equipment[K.Armour]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id && a.Str(K.ClassId) == session.ClassId);
        }
    }
}
