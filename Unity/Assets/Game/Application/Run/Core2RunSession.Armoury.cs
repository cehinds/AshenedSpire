using System;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.App.Run
{
    /// <summary>What an Armoury change did: the sentence the pane shows (the web's notice), and whether the loadout changed.</summary>
    public sealed class Core2ArmouryChange
    {
        public bool Ok;
        public string Message = string.Empty;
    }

    public sealed partial class Core2RunSession
    {
        // ------------------------------------------------------------------ the Armoury's equipment doors (web equipment.js applyEquipmentChange, activatePosition; main.js showArmoury onChange)

        private Ashen.Domain.Run.RunData GearData => Core.Deck.Run.Run;

        /// <summary>The weapon swap price rule the player chose in Settings (settings.swapCostRule), or null for the profile's own (the web's resolveSwapCostRule reads it from the profile's settings).</summary>
        public string SwapRuleId => PlayerSettings.Text(ReceiptKeys.SwapRuleSetting);

        /// <summary>
        /// What the figure wears and holds (loadout.js figureSpec's armourId and gripOf's right and left): the armour set of the armour slot and the pieces of the two hands (null for an empty hand), which
        /// select the painted outfit and the equipment animation set of the player's portrait (<see cref="OutfitArtData"/>).
        /// </summary>
        public (string Armour, string Right, string Left) FigureGear()
        {
            var loadout = _run.Obj(K.Loadout);
            var data = GearData;
            string In(string slotId) => slotId == null || loadout == null ? null : Ashen.Domain.Combat.Equipment.EquippedIn(data.Combat, loadout, ClassId, slotId)?.Str(K.Id);
            string Hand(string hand) => In(data.EquipmentRows(K.Slots).FirstOrDefault(row => Loadout.SlotHand(data, row) == hand)?.Str(K.Id));
            return (In(Ashen.Generated.CombatValues.Armor), Hand(Ashen.Generated.CombatValues.Right), Hand(Ashen.Generated.CombatValues.Left));
        }

        private EquipContext EquipmentContext(string itemId, int setIndex) => new EquipContext
        {
            InCombat = false,
            Loadout = _run.Obj(K.Loadout),
            ClassId = ClassId,
            SetIndex = setIndex,
            NamesItem = true,
            ItemId = itemId,
            Attributes = _run.Obj(K.Attributes),
            ItemUpgradeLevels = _run.Obj(K.ItemUpgradeLevels) ?? new JObject(),
            ConfiguredDeck = true,
        };

        /// <summary>
        /// Why <paramref name="itemId"/> (or nothing, an unequip) cannot go into a position now, in the web's words, or null when it can: a fight is on, the canEquip rules (the grip the hands would be
        /// left in, the attribute minima), the storage a displaced piece would need.
        /// </summary>
        public string EquipRefusal(StringTable strings, string slotId, int setIndex, string itemId)
        {
            if (IsInCombat) return strings.Get(StringKeys.ArmouryChangeCombatUnavailable);
            var loadout = _run.Obj(K.Loadout);
            if (loadout == null) return strings.Get(StringKeys.ArmouryRefusalNoLocation);
            var verdict = Armoury.CanEquip(GearData, slotId, EquipmentContext(itemId, setIndex));
            if (!verdict.Ok) return verdict.Message;
            var transition = Armoury.TransitionPlan(GearData, loadout, slotId, setIndex, itemId);
            if (transition.Ok) return null;
            if (transition.NoLocation) return strings.Get(StringKeys.ArmouryRefusalNoLocation);
            return strings.Format(string.IsNullOrEmpty(itemId) ? StringKeys.ArmouryRefusalStorageFullUnequip : StringKeys.ArmouryRefusalStorageFullMove,
                new StringArgs().Add(UiPlaceholders.Count, transition.Stored).Add(UiPlaceholders.Total, transition.Capacity));
        }

        /// <summary>
        /// Puts a piece into a position, or clears it (an unequip), as the Armoury does outside a fight: the loadout changes under equipPiece's gates, the deck is restamped (the pools re-derived, the gear's
        /// lent cards reconciled) and the run is saved. A refusal changes nothing. <paramref name="actionLabel"/> words the generic refusal ("Equip to MH1 · 1").
        /// </summary>
        public Core2ArmouryChange Equip(StringTable strings, string slotId, int setIndex, string itemId, string actionLabel)
        {
            var refusal = EquipRefusal(strings, slotId, setIndex, itemId);
            if (refusal != null) return new Core2ArmouryChange { Message = refusal };
            var loadout = _run.Obj(K.Loadout);
            var slot = GearData.EquipmentRows(K.Slots).First(s => s.Str(K.Id) == slotId);
            var previousId = Js.Str((loadout.Obj(K.Sets)?.Arr(slotId))?[setIndex]);
            var snapshot = Snapshot();
            bool changed;
            try
            {
                changed = Armoury.EquipPiece(GearData, loadout, slotId, setIndex, itemId, Armoury.Ownership(GearData, loadout), EquipmentContext(itemId, setIndex), null);
                if (changed) DeckRules.Restamp(Core.Deck, _run);
            }
            catch (InvalidOperationException)
            {
                Restore(snapshot);
                changed = false;
            }
            if (!changed)
            {
                Restore(snapshot);
                return new Core2ArmouryChange { Message = strings.Format(StringKeys.ArmouryRefusalRefused, new StringArgs().Add(UiPlaceholders.Label, actionLabel ?? string.Empty)) };
            }
            SaveOrRestore(snapshot);
            var where = strings.Format(StringKeys.ArmouryChangePosition, new StringArgs().Add(UiPlaceholders.Label, slot.Str(K.Label) ?? slotId).Add(UiPlaceholders.N, setIndex + 1));
            string NameOf(string id) => string.IsNullOrEmpty(id) ? string.Empty : Armoury_Name(id);
            if (string.IsNullOrEmpty(itemId))
                return new Core2ArmouryChange { Ok = true, Message = strings.Format(StringKeys.ArmouryChangeUnequipped, new StringArgs().Add(UiPlaceholders.Name, NameOf(previousId)).Add(UiPlaceholders.Slot, where)) };
            var message = strings.Format(StringKeys.ArmouryChangeEquipped, new StringArgs().Add(UiPlaceholders.Name, NameOf(itemId)).Add(UiPlaceholders.Slot, where));
            if (!string.IsNullOrEmpty(previousId) && previousId != itemId)
                message += strings.Get(StringKeys.ArmouryChangeJoin) + strings.Format(StringKeys.ArmouryChangeStored, new StringArgs().Add(UiPlaceholders.Name, NameOf(previousId)));
            return new Core2ArmouryChange { Ok = true, Message = message };
        }

        private string Armoury_Name(string id)
        {
            var data = CombatData;
            var row = Js.Items(data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id)
                      ?? Js.Items(data.Equipment[K.Armour]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id && a.Str(K.ClassId) == ClassId);
            return row?.Str(K.Name) ?? id;
        }

        /// <summary>One derived-stat row (the run's own snapshot) at the run's attributes and level, or null when the run has no such row (the web's statRowValue for the Poise receipt).</summary>
        public double? DerivedStat(string statId, double? atLevel = null)
        {
            var run = _run;
            var d = Core.Combat.Run;
            var table = run.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules);
            if (table == null || CoreDerivedStats.ResolvedRow(d, table, statId, ClassId) == null) return null;
            var level = atLevel ?? (Js.Truthy(run.Obj(K.Level)?[K.Level]) ? run.Obj(K.Level).Num(K.Level) : 1);
            return CoreDerivedStats.Derive(d, table, statId, run.Obj(K.Attributes), d.Run.Classes.Get(ClassId), level).Value;
        }

        /// <summary>
        /// The loadout as it would stand with <paramref name="itemId"/> in a position (equipPiece on a copy: its gates, the storage transition), or null when the piece cannot go there. Nothing in the run changes;
        /// the Armoury's comparison reads it.
        /// </summary>
        public JObject TrialLoadout(string slotId, int setIndex, string itemId)
        {
            var loadout = _run.Obj(K.Loadout);
            if (loadout == null || string.IsNullOrEmpty(itemId)) return null;
            var trial = (JObject)loadout.DeepClone();
            var ok = false;
            try { ok = Armoury.EquipPiece(GearData, trial, slotId, setIndex, itemId, Armoury.Ownership(GearData, trial), EquipmentContext(itemId, setIndex), null); }
            catch (InvalidOperationException) { }
            return ok ? trial : null;
        }

        /// <summary>Why a set cannot be made the slot's active one (the swap rule, the grip the hands would be left in), or null (positionActivationSeal).</summary>
        public string ActivationRefusal(StringTable strings, string slotId, int setIndex)
        {
            var seal = Armoury.CanSwap(GearData, slotId, IsInCombat);
            if (!seal.Ok) return seal.Message;
            var loadout = _run.Obj(K.Loadout);
            if (loadout == null) return strings.Get(StringKeys.ArmouryRefusalNoLocation);
            var trial = (JObject)loadout.DeepClone();
            trial.Obj(K.Active).Put(slotId, setIndex);
            var itemId = Js.Str(trial.Obj(K.Sets)?.Arr(slotId)?[setIndex]);
            var grip = Armoury.GripRefusal(GearData, trial, ClassId, slotId, setIndex, itemId);
            return grip == null ? null : grip.Message;
        }

        /// <summary>Makes a set the slot's active one (cycleSet), restamps the deck and saves; a refusal changes nothing.</summary>
        public Core2ArmouryChange Activate(StringTable strings, string slotId, int setIndex)
        {
            var refusal = ActivationRefusal(strings, slotId, setIndex);
            if (refusal != null) return new Core2ArmouryChange { Message = refusal };
            var loadout = _run.Obj(K.Loadout);
            var snapshot = Snapshot();
            bool changed;
            try
            {
                changed = Armoury.CycleSet(GearData, loadout, slotId, setIndex, false, ClassId, null, true, ArmouryPanes.EarnedRungs(this, slotId));
                if (changed) DeckRules.Restamp(Core.Deck, _run);
            }
            catch (InvalidOperationException)
            {
                changed = false;
            }
            if (!changed)
            {
                Restore(snapshot);
                return new Core2ArmouryChange { Message = strings.Format(StringKeys.ArmouryRefusalRefused, new StringArgs().Add(UiPlaceholders.Label, strings.Get(StringKeys.ArmouryActionActivate))) };
            }
            SaveOrRestore(snapshot);
            return new Core2ArmouryChange { Ok = true };
        }
    }
}
