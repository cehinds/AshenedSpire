using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using AF = Ashen.Generated.ArmouryFormats;
using K = Ashen.Generated.CombatKeys;
using P = Ashen.Generated.MetaPlaceholders;
using RK = Ashen.Generated.RunKeys;
using S = Ashen.Generated.MetaStringKeys;

namespace Ashen.App.Run
{
    /// <summary>One cell of a W-12 slot: the set index, the piece in it (null: empty) and whether it is the active set.</summary>
    public sealed class ArmouryCellView
    {
        public int SetIndex;
        public string ItemId;
        public string Label;
        public bool Active;
    }

    /// <summary>One equipment slot of W-12 (hands with their sets, body, head, hands, feet, talismans).</summary>
    public sealed class ArmourySlotView
    {
        public string SlotId;
        public string Label;
        public readonly List<ArmouryCellView> Cells = new List<ArmouryCellView>();
    }

    /// <summary>W-12 <c>screen:armoury</c> (US-7.6): the slots, storage and the weight class.</summary>
    public sealed class ArmouryViewState
    {
        public string Title;
        public string Weight;
        public readonly List<ArmourySlotView> Slots = new List<ArmourySlotView>();
        public string StorageTitle;
        public readonly List<KeyValuePair<string, string>> Storage = new List<KeyValuePair<string, string>>();
        public string StorageEmpty;
    }

    /// <summary>What a change would do (W-12's inspect pane): refused with the reason, or the before/after receipts.</summary>
    public sealed class ArmouryChange
    {
        public string Refusal;
        public readonly List<string> Receipts = new List<string>();
        public bool Done => Refusal == null;
    }

    /// <summary>
    /// W-12 the Armoury out of combat (US-7.4, US-7.6) over the run's loadout: put a carried piece into a set of a slot
    /// (or clear it), or make another hand set active. Each change goes through the shipped doors (canEquip, equipPiece or
    /// cycleSet), then stampDeck re-derives the pools and lends or takes back the item-owned cards (US-7.3), and the run
    /// is saved. A preview runs the same steps on a copy and reports the receipts. Closed during a fight.
    /// </summary>
    public sealed partial class RunSession
    {
        private bool ArmouryClosed => IsInCombat || RunOver || _run == null;

        public ArmouryViewState ArmouryView(UiData ui)
        {
            var strings = ui.Strings;
            var d = Content.Data;
            var loadout = _run?.Obj(K.Loadout) ?? new JObject();
            var view = new ArmouryViewState { Title = strings.Get(S.ArmouryTitle), Weight = WeightText(strings, _run) };
            foreach (var slot in d.EquipmentRows(K.Slots).OrderBy(s => Js.Or0(s[MetaKeys.Order])))
            {
                var id = slot.Str(K.Id);
                var cells = loadout.Obj(K.Sets)?[id] as JArray;
                if (cells == null) continue;
                var active = (int)Js.Or0(loadout.Obj(K.Active)?[id]);
                var slotView = new ArmourySlotView { SlotId = id, Label = slot.Str(RunFlowKeys.Label) ?? id };
                for (var i = 0; i < cells.Count; i++)
                {
                    var item = Js.Str(cells[i]);
                    slotView.Cells.Add(new ArmouryCellView { SetIndex = i, ItemId = item, Label = ItemName(strings, item), Active = i == active });
                }
                view.Slots.Add(slotView);
            }
            var storage = Js.Items(loadout[RK.Storage]).Select(Js.Str).Where(s => s != null).ToList();
            view.StorageTitle = strings.Format(S.ArmouryStorage, new StringArgs().Add(P.Count, storage.Count));
            foreach (var item in storage) view.Storage.Add(new KeyValuePair<string, string>(item, ItemName(strings, item)));
            if (storage.Count == 0) view.StorageEmpty = strings.Get(S.ArmouryStorageEmpty);
            return view;
        }

        /// <summary>The pieces that may go into a slot now: carried (storage or another set), fitting the slot's kinds and hand.</summary>
        public List<string> ArmouryCandidates(string slotId)
        {
            var d = Content.Data;
            var loadout = _run?.Obj(K.Loadout);
            var slot = d.EquipmentRows(K.Slots).FirstOrDefault(s => s.Str(K.Id) == slotId);
            if (loadout == null || slot == null) return new List<string>();
            return Armoury.CarriedIds(loadout).Where(id => Armoury.FitsSlot(d, slot, Piece(id))).ToList();
        }

        /// <summary>What putting <paramref name="itemId"/> (null clears) into the set would do, without changing the run.</summary>
        public ArmouryChange PreviewEquip(UiData ui, string slotId, int setIndex, string itemId) => Change(ui, false, run => Equip(run, slotId, setIndex, itemId));

        public ArmouryChange Equip(UiData ui, string slotId, int setIndex, string itemId) => Change(ui, true, run => Equip(run, slotId, setIndex, itemId));

        /// <summary>Make another set of a hand slot active (free out of combat).</summary>
        public ArmouryChange CycleSet(UiData ui, string slotId, int setIndex) => Change(ui, true, run =>
            Armoury.CycleSet(Content.Data, run.Obj(K.Loadout), slotId, setIndex, false, run.Str(RK.Class), null) ? null : ui.Strings.Get(S.ArmouryRefusalSame));

        private string Equip(JObject run, string slotId, int setIndex, string itemId)
        {
            var d = Content.Data;
            var loadout = run.Obj(K.Loadout);
            var cells = loadout?.Obj(K.Sets)?[slotId] as JArray;
            if (cells != null && setIndex >= 0 && setIndex < cells.Count && Js.Str(cells[setIndex]) == itemId) return S.ArmouryRefusalSame;
            var ctx = new EquipContext
            {
                InCombat = false,
                Loadout = loadout,
                ClassId = run.Str(RK.Class),
                SetIndex = setIndex,
                NamesItem = true,
                ItemId = itemId,
                Attributes = run.Obj(K.Attributes),
                ItemUpgradeLevels = run.Obj(K.ItemUpgradeLevels),
            };
            var verdict = Armoury.CanEquip(d, slotId, ctx);
            if (!verdict.Ok) return verdict.Message;
            return Armoury.EquipPiece(d, loadout, slotId, setIndex, itemId, Armoury.Ownership(d, loadout), ctx, null) ? null : S.ArmouryRefusalSame;
        }

        /// <summary>One change on a copy (preview) or on the run (commit, saved): the door, then stampDeck, then the receipts.</summary>
        private ArmouryChange Change(UiData ui, bool commit, Func<JObject, string> door)
        {
            var strings = ui.Strings;
            var result = new ArmouryChange();
            if (ArmouryClosed)
            {
                result.Refusal = strings.Get(S.ArmouryRefusalInCombat);
                return result;
            }
            var before = (JObject)_run.DeepClone();
            var after = (JObject)_run.DeepClone();
            var refusal = door(after);
            if (refusal != null)
            {
                result.Refusal = refusal == S.ArmouryRefusalSame ? strings.Get(S.ArmouryRefusalSame)
                    : strings.Format(S.ArmouryRefusalCannot, new StringArgs().Add(P.Reason, refusal));
                return result;
            }
            StartingDeck.StampDeck(Content.Data, after);
            foreach (var field in new[] { K.MaxHp, K.MaxMana, K.MaxStamina, K.EnergyMax })
                if (before.Num(field) != after.Num(field))
                    result.Receipts.Add(strings.Format(S.ArmouryReceiptPool, new StringArgs()
                        .Add(P.Name, strings.Get(string.Format(CultureInfo.InvariantCulture, AF.PoolName, field)))
                        .Add(P.Before, (int)before.Num(field)).Add(P.After, (int)after.Num(field))));
            var deckBefore = Js.Items(before[K.Deck]).Count();
            var deckAfter = Js.Items(after[K.Deck]).Count();
            if (deckBefore != deckAfter)
                result.Receipts.Add(strings.Format(S.ArmouryReceiptDeck, new StringArgs().Add(P.Before, deckBefore).Add(P.After, deckAfter)));
            var wBefore = WeightName(strings, before);
            var wAfter = WeightName(strings, after);
            if (wBefore != wAfter) result.Receipts.Add(strings.Format(S.ArmouryReceiptWeight, new StringArgs().Add(P.Before, wBefore).Add(P.After, wAfter)));
            if (result.Receipts.Count == 0) result.Receipts.Add(strings.Get(S.ArmouryReceiptNone));
            if (!commit) return result;
            _run = after;
            try { Save(); }
            catch
            {
                _run = before;
                throw;
            }
            return result;
        }

        private WeightClassReceipt Weight(JObject run)
        {
            var data = Content.Combat;
            var attributes = run?.Obj(K.Attributes) ?? new JObject();
            double load = 0;
            if (run?.Obj(K.Loadout) != null)
                foreach (var piece in Equipment.EquippedPieces(data, run.Obj(K.Loadout), run.Str(RK.Class), run.Obj(K.ItemUpgradeLevels) ?? new JObject()))
                    load += Equipment.PieceWeight(piece);
            return Equipment.ComputeWeightClass(data, attributes.Num(K.Constitution), attributes.Num(K.Strength), 0, load);
        }

        private string WeightName(StringTable strings, JObject run)
        {
            var id = Weight(run).WeightClass?.Str(K.Id);
            var key = string.Format(CultureInfo.InvariantCulture, AF.WeightName, id);
            return strings.Has(key) ? strings.Get(key) : id;
        }

        private string WeightText(StringTable strings, JObject run)
        {
            var w = Weight(run);
            return strings.Format(S.ArmouryWeight, new StringArgs().Add(P.Name, WeightName(strings, run)).Add(P.N, (int)w.Percent).Add(P.Count, (int)w.Capacity));
        }

        private JObject Piece(string id) =>
            Content.Data.EquipmentRows(K.Armaments).Concat(Content.Data.EquipmentRows(K.Armour)).FirstOrDefault(r => r.Str(K.Id) == id);

        private string ItemName(StringTable strings, string id)
        {
            if (id == null) return strings.Get(S.ArmouryEmpty);
            var key = string.Format(CultureInfo.InvariantCulture, MetaFormats.ArmamentName, id);
            if (strings.Has(key)) return strings.Get(key);
            key = string.Format(CultureInfo.InvariantCulture, CreationValues.ArmourNameKey, ClassId, id);
            return strings.Has(key) ? strings.Get(key) : id;
        }
    }
}
