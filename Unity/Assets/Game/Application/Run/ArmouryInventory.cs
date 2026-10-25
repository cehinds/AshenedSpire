using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using RK = Ashen.Generated.RunKeys;
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

        /// <summary>The piece at its authored tier (armour and armaments), for its poker card.</summary>
        public JObject Piece;

        /// <summary>A class card (the web's class row): the class the run is, its glyph (an icon name), its description and its tags.</summary>
        public bool IsClass;
        public string Glyph;
        public string Description;
        public readonly List<string> ClassTags = new List<string>();
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
            foreach (var classId in Js.Items(new JArray(new[] { (JToken)session.ClassId }.Concat(run.Obj(ReceiptCardKeys.ClassCards)?.Properties().Select(p => (JToken)p.Name) ?? Enumerable.Empty<JToken>()))).Select(Js.Str).Distinct())
            {
                if (!data.Classes.Has(classId)) continue;
                var def = data.Classes.Get(classId);
                var equipped = !Js.Truthy(run[ReceiptCardKeys.ClassUnequipped]) && session.ClassId == classId;
                var classRow = new ArmouryInventoryRow
                {
                    Key = string.Format(CultureInfo.InvariantCulture, ReceiptCardKeys.ClassRowKey, classId),
                    Id = classId,
                    Name = def.Str(K.Name) ?? classId,
                    Category = strings.Get(StringKeys.ArmouryInventoryClass),
                    IsClass = true,
                    Glyph = def.Str(ReceiptCardKeys.ClassGlyph),
                    Description = def.Str(ReceiptCardKeys.ClassDescription),
                };
                if (equipped) classRow.EquippedLabels.Add(strings.Get(StringKeys.ArmouryInventoryCoreSlot));
                classRow.ClassTags.AddRange(Js.Items(def[ReceiptCardKeys.ClassTags]).Select(Js.Str).Where(t => t != null));
                rows.Add(classRow);
            }
            var owned = Armoury.Ownership(gear, loadout);
            var locations = Locations(gear, loadout);
            foreach (var piece in Js.Items(data.Equipment[K.Armour]).OfType<JObject>().Where(p => p.Str(K.ClassId) == session.ClassId && owned(p)))
                rows.Add(Piece(session, strings, piece, strings.Get(StringKeys.ArmouryInventoryArmour), locations));
            foreach (var piece in Js.Items(data.Equipment[K.Armaments]).OfType<JObject>().Where(owned))
                rows.Add(Piece(session, strings, piece, Named(strings, ArmouryKeys.KindStringKey, piece.Str(K.Kind)), locations));
            foreach (var group in Js.Items(run[K.Relics]).Select(Js.Str).Where(id => id != null && data.Relics.Has(id)).GroupBy(id => id))
            {
                var relic = new ArmouryInventoryRow { Key = StringKeys.ArmouryInventoryRelic + group.Key, Id = group.Key, Name = data.Relics.Get(group.Key).Str(K.Name) ?? group.Key, Category = strings.Get(StringKeys.ArmouryInventoryRelic), Count = group.Count() };
                relic.EquippedLabels.Add(strings.Get(StringKeys.ArmouryInventoryEquipped));
                rows.Add(relic);
            }
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
                Piece = piece,
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

        /// <summary>How an item stands towards a position (equipPickerTable.js): worn here, free, worn in another position, or not taken by this slot.</summary>
        public enum PickerStatus
        {
            Here,
            Free,
            Elsewhere,
            NoFit,
        }

        /// <summary>One row of the Armoury's equip picker table (Item, Status, Actions) for one position.</summary>
        public sealed class PickerRow
        {
            public ArmouryInventoryRow Row;
            public bool Fits;
            public PickerStatus Status;

            /// <summary>Why the slot does not take it: "kind" (its kind is not one of the slot's) or "hand" (it only fits the other hand); null when it fits.</summary>
            public string Reason;

            public readonly List<KeyValuePair<string, int>> Elsewhere = new List<KeyValuePair<string, int>>();
        }

        /// <summary>
        /// equipPickerTableRows then sortEquipPickerRows: the equipable rows for one position with their status, sorted by status (here, free, elsewhere, not fitting) then name, or by name then status; the
        /// direction flips the whole comparison. A stable sort, so ties keep the inventory's order.
        /// </summary>
        public static IReadOnlyList<PickerRow> PickerRows(Core2RunSession session, IReadOnlyList<ArmouryInventoryRow> rows, string slotId, int setIndex, bool byName, bool descending)
        {
            var gear = session.Core.Deck.Run.Run;
            var loadout = session.Run.Obj(K.Loadout);
            var slot = gear.EquipmentRows(K.Slots).First(s => s.Str(K.Id) == slotId);
            var list = new List<PickerRow>();
            foreach (var row in rows.Where(r => r.Equipable))
            {
                var piece = Piece(session, row.Id);
                var fits = piece != null && Armoury.FitsSlot(gear, slot, piece);
                var here = Js.Str(loadout?.Obj(K.Sets)?.Arr(slotId)?[setIndex]) == row.Id;
                var entry = new PickerRow { Row = row, Fits = fits };
                foreach (var held in (loadout?.Obj(K.Sets) ?? new JObject()).Properties())
                {
                    if (!(held.Value is JArray ids)) continue;
                    for (var i = 0; i < ids.Count; i++)
                        if (Js.Str(ids[i]) == row.Id && !(held.Name == slotId && i == setIndex)) entry.Elsewhere.Add(new KeyValuePair<string, int>(held.Name, i));
                }
                entry.Status = !fits ? PickerStatus.NoFit : here ? PickerStatus.Here : entry.Elsewhere.Count > 0 ? PickerStatus.Elsewhere : PickerStatus.Free;
                entry.Reason = fits ? null : !Js.Items(slot[K.Kinds]).Any(k => Js.Str(k) == piece?.Str(K.Kind)) ? PickerKeys.ReasonKind : PickerKeys.ReasonHand;
                list.Add(entry);
            }
            int ByStatus(PickerRow a, PickerRow b) => a.Status.CompareTo(b.Status);
            int ByName(PickerRow a, PickerRow b) => string.Compare(a.Row.Name, b.Row.Name, StringComparison.OrdinalIgnoreCase);
            Comparison<PickerRow> primary = byName ? (a, b) => { var c = ByName(a, b); return c != 0 ? c : ByStatus(a, b); } : (Comparison<PickerRow>)((a, b) => { var c = ByStatus(a, b); return c != 0 ? c : ByName(a, b); });
            var sign = descending ? -1 : 1;
            return list.Select((entry, index) => (entry, index)).OrderBy(x => x, Comparer<(PickerRow entry, int index)>.Create((x, y) => { var c = sign * primary(x.entry, y.entry); return c != 0 ? c : x.index.CompareTo(y.index); })).Select(x => x.entry).ToList();
        }

        /// <summary>The state of the eligibility line (inventoryEligibility): no position, the rules shut it, the attribute minima are not met, or it can be done.</summary>
        public enum EligibilityState
        {
            None,
            Blocked,
            Short,
            Ready,
        }

        /// <summary>What the Armoury says of a selected item (web equipment.js selectionFacts over ArmouryWorkspaceModel.js): the action its target gives, the comparison with what is equipped, whether it can go on.</summary>
        public sealed class SelectionView
        {
            public ArmouryActionView Action;
            public CandidateView Candidate;

            /// <summary>"Replaces Greatsword in Main Hand · 1"; empty without a comparison.</summary>
            public string Relation = string.Empty;

            /// <summary>"Strike 11 → 4" for each role whose number moves; with none, <see cref="Unchanged"/> is set.</summary>
            public readonly List<string> RoleLines = new List<string>();
            public bool Unchanged;
            public EligibilityState Eligibility;
            public string EligibilityText = string.Empty;
            public string Instruction = string.Empty;
        }

        /// <summary>
        /// The selection of an inventory row: with a position picked, the action it gives there (equip, move or unequip); without, the unequip of the position it already holds; none for a piece worn nowhere (it asks for a
        /// destination first). The comparison is the candidate receipt of that target.
        /// </summary>
        public static SelectionView Select(Core2RunSession session, UiData ui, ArmouryInventoryRow row, string pickSlot, int pickIndex)
        {
            var strings = ui.Strings;
            var view = new SelectionView();
            if (!row.Equipable) return view;
            var gear = session.Core.Deck.Run.Run;
            var loadout = session.Run.Obj(K.Loadout);
            var piece = Piece(session, row.Id);
            if (pickSlot != null) view.Action = Action(session, ui, row.Id, pickSlot, pickIndex);
            else if (piece != null && loadout != null)
            {
                foreach (var slot in gear.EquipmentRows(K.Slots).Where(s => Armoury.FitsSlot(gear, s, piece)))
                {
                    var at = Js.Items(loadout.Obj(K.Sets)?[slot.Str(K.Id)]).Select(Js.Str).ToList().IndexOf(row.Id);
                    if (at < 0) continue;
                    view.Action = Action(session, ui, row.Id, slot.Str(K.Id), at);
                    break;
                }
            }
            view.Instruction = strings.Get(view.Action != null ? StringKeys.ArmouryDestinationReview : StringKeys.ArmouryDestinationChoose);
            if (view.Action == null) return view;
            var target = gear.EquipmentRows(K.Slots).First(s => s.Str(K.Id) == view.Action.SlotId);
            view.Candidate = ArmouryReceipts.Candidate(session, ui, view.Action.SlotId, view.Action.SetIndex, view.Action.PieceId, session.SwapRuleId);
            var where = Where(strings, target, view.Action.SetIndex);
            if (view.Candidate != null)
            {
                var occupantId = Js.Str(loadout?.Obj(K.Sets)?.Arr(view.Action.SlotId)?[view.Action.SetIndex]);
                var occupantName = view.Action.Kind == ArmouryActionKind.Unequip ? row.Name
                    : !string.IsNullOrEmpty(occupantId) && occupantId != row.Id ? Piece(session, occupantId)?.Str(K.Name) ?? occupantId : null;
                view.Relation = view.Action.Kind == ArmouryActionKind.Unequip ? strings.Format(ArmouryWebKeys.CompareRemoves, new StringArgs().Add(UiPlaceholders.Name, occupantName).Add(UiPlaceholders.Slot, where))
                    : occupantName != null ? strings.Format(ArmouryWebKeys.CompareReplaces, new StringArgs().Add(UiPlaceholders.Name, occupantName).Add(UiPlaceholders.Slot, where))
                    : strings.Format(ArmouryWebKeys.CompareFills, new StringArgs().Add(UiPlaceholders.Slot, where));
                var labels = ArmouryPanes.RoleLabelsOf(strings);
                foreach (var role in view.Candidate.Roles.Where(r => r.Before != r.After))
                    view.RoleLines.Add(strings.Format(ArmouryWebKeys.CompareRole, new StringArgs().Add(UiPlaceholders.Label, labels.TryGetValue(role.Role, out var label) ? label : role.Role)
                        .Add(UiPlaceholders.Before, RunJs.NumStr(role.Before)).Add(UiPlaceholders.After, RunJs.NumStr(role.After))));
                view.Unchanged = view.RoleLines.Count == 0;
            }
            if (view.Action.Refusal.Length > 0)
            {
                view.Eligibility = EligibilityState.Blocked;
                view.EligibilityText = strings.Format(ArmouryWebKeys.EligibilityBlocked, new StringArgs().Add(ReceiptPlaceholders.Reason, view.Action.Refusal));
            }
            else
            {
                var failures = view.Candidate?.Requirement?.Rows.Where(r => !r.Met).ToList() ?? new List<ReceiptRequirementRow>();
                if (failures.Count > 0)
                {
                    view.Eligibility = EligibilityState.Short;
                    view.EligibilityText = strings.Format(ArmouryWebKeys.EligibilityShort, new StringArgs().Add(ReceiptPlaceholders.Requirements, string.Join(strings.Get(StringKeys.ArmouryCardRequirementJoin),
                        failures.Select(f => strings.Format(ArmouryWebKeys.EligibilityRequirement, new StringArgs().Add(ReceiptPlaceholders.Attribute, f.FullLabel)
                            .Add(ReceiptPlaceholders.Required, RunJs.NumStr(f.Required)).Add(ReceiptPlaceholders.Actual, f.Actual.HasValue ? RunJs.NumStr(f.Actual.Value) : ReceiptKeys.Dash))))));
                }
                else
                {
                    view.Eligibility = EligibilityState.Ready;
                    view.EligibilityText = strings.Format(ArmouryWebKeys.EligibilityReady, new StringArgs().Add(ReceiptPlaceholders.Action, view.Action.Label));
                }
            }
            return view;
        }

        private static JObject Piece(Core2RunSession session, string id)
        {
            var data = session.CombatData;
            return Js.Items(data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id)
                   ?? Js.Items(data.Equipment[K.Armour]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id && a.Str(K.ClassId) == session.ClassId);
        }
    }
}
