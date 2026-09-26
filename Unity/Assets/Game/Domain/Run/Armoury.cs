using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;
using S = Ashen.Generated.CombatStringKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// A shipped yes/no answer with its sentence: <see cref="Message"/> is the shipped (developer-facing) text the
    /// engine throws, <see cref="Refusal"/> the string-table key and arguments the UI words it with.
    /// </summary>
    public sealed class ArmouryVerdict
    {
        public static readonly ArmouryVerdict Yes = new ArmouryVerdict { Ok = true, Word = string.Empty, Message = string.Empty };

        public bool Ok;

        /// <summary>canSwap's badge word ('fastened', 'unknown', or empty when the slot may swap).</summary>
        public string Word;

        public string Message;
        public Refusal Refusal;

        public static ArmouryVerdict No(string word, string stringKey, string template, params object[] args) => new ArmouryVerdict
        {
            Ok = false,
            Word = word ?? string.Empty,
            Message = RunJs.Fmt(template, args),
            Refusal = new Refusal(stringKey, args),
        };
    }

    /// <summary>The context equipPiece and canEquip are handed (shipped ctx: inCombat, loadout, classId, setIndex, itemId, attributes, levels).</summary>
    public sealed class EquipContext
    {
        public bool InCombat;
        public JObject Loadout;
        public string ClassId;
        public int? SetIndex;

        /// <summary>True when the caller names the candidate (ctx.itemId !== undefined); <see cref="ItemId"/> may still be null (an empty hand).</summary>
        public bool NamesItem;

        public string ItemId;
        public JObject Attributes;
        public JObject ItemUpgradeLevels;
    }

    /// <summary>
    /// The Armoury's mutation model (shipped model/loadout.js, us-5.11): canSwap and canEquip, the swap price chain
    /// (swapCostFor), cycleSet with the openedSets ladder, equipPiece with its grip and attribute gates, the storage
    /// transition, ownership, fitsSlot and the equipmentChanged payload. Combat asks it with an empty profile meta, so no
    /// ladder rung is ever earned there: a fight cycles between the sets the loadout already holds.
    /// </summary>
    public static class Armoury
    {
        private static JObject SlotById(RunData d, string slotId) => d.EquipmentRows(K.Slots).FirstOrDefault(s => s.Str(K.Id) == slotId);

        private static JArray Cells(JObject loadout, string slotId) => loadout?.Obj(K.Sets)?[slotId ?? V.Undefined] as JArray;

        private static string Cell(JArray ids, int index) => ids != null && index >= 0 && index < ids.Count ? Js.Str(ids[index]) : null;

        /// <summary>canSwap(registries, slotId, { inCombat }): may this slot change its ACTIVE set now.</summary>
        public static ArmouryVerdict CanSwap(RunData d, string slotId, bool inCombat)
        {
            var slot = SlotById(d, slotId);
            if (slot == null) return ArmouryVerdict.No(RV.WordUnknown, S.CombatRefusalNoSlot, RM.NoSlot, slotId ?? V.Undefined);
            if (inCombat && slot.Str(RK.Swap) != RV.SwapInCombat)
                return ArmouryVerdict.No(RV.WordFastened, S.CombatRefusalSlotFastened, RM.SlotFastened, slot.Str(K.Label) ?? V.Undefined);
            return ArmouryVerdict.Yes;
        }

        /// <summary>
        /// swapCostFor(registries, { rule, loadout, classId, slotId, setIndex, relicDelta }) → the whole derivation:
        /// { cost, ruleId, base, baseCost, categoryTag, gearOn, gearDelta, gearIgnored, floored }. A category rule prices
        /// the piece being drawn (first matching category row); a gear rule adds the relic and worn-mod deltas; the
        /// total floors at zero.
        /// </summary>
        public static JObject SwapCostFor(RunData d, JToken rule, JObject loadout, string classId, string slotId, int setIndex, double relicDelta)
        {
            var cfg = d.EquipmentBalance;
            var fallback = Js.IsFinite(cfg[RK.SwapCost]) ? cfg.Num(RK.SwapCost) : 0;
            var row = Js.Truthy(rule) ? rule as JObject : null;
            var baseKind = row != null && d.RuleList(K.Loadout, RK.SwapCostBases).Contains(row.Str(K.Base)) ? row.Str(K.Base) : RV.SwapBaseDefault;
            string categoryTag = null;
            var baseCost = fallback;
            if (baseKind == RV.SwapBaseCategory)
            {
                var id = Cell(Cells(loadout, slotId), setIndex);
                var piece = !string.IsNullOrEmpty(id) ? d.EquipmentRows(K.Armaments).FirstOrDefault(a => a.Str(K.Id) == id) : null;
                var tags = piece?[K.Tags];
                var hit = Js.Items(cfg[RK.SwapCostByCategory]).FirstOrDefault(r => Js.Truthy(r) && Js.Includes(tags, Js.Str(Js.Get(r, K.Tag)))) as JObject;
                if (hit != null)
                {
                    categoryTag = hit.Str(K.Tag);
                    baseCost = Js.IsFinite(hit[K.Cost]) ? hit.Num(K.Cost) : fallback;
                }
            }
            var worn = loadout != null ? Loadout.RunMods(d, loadout, classId).SwapCostDelta : 0;
            var delta = relicDelta + worn;
            var gearOn = row != null && Js.Truthy(row[RK.Gear]);
            var raw = baseCost + (gearOn ? delta : 0);
            var receipt = Js.Obj(K.Cost, Math.Max(0, raw));
            if (row == null) receipt[RK.RuleId] = Js.Null();
            else if (row[K.Id] != null) receipt[RK.RuleId] = row[K.Id].DeepClone();
            receipt[K.Base] = baseKind;
            receipt.Put(RK.BaseCost, baseCost);
            receipt[RK.CategoryTag] = Js.S(categoryTag);
            receipt.Put(RK.GearOn, gearOn);
            receipt.Put(RK.GearDelta, gearOn ? delta : 0);
            receipt.Put(RK.GearIgnored, gearOn ? 0 : delta);
            receipt.Put(RK.Floored, raw < 0);
            return receipt;
        }

        /// <summary>
        /// canEquip(registries, slotId, ctx): may what is in a set of this slot change now — the combat-wide
        /// allowChangesInCombat rule, then (when the caller names what it is about to put where) the grip the hands
        /// would be left in and the candidate's attribute minima.
        /// </summary>
        public static ArmouryVerdict CanEquip(RunData d, string slotId, EquipContext ctx)
        {
            var slot = SlotById(d, slotId);
            if (slot == null) return ArmouryVerdict.No(RV.WordUnknown, S.CombatRefusalNoSlot, RM.NoSlot, slotId ?? V.Undefined);
            var cfg = d.EquipmentBalance;
            if (ctx.InCombat && !(cfg[RK.AllowChangesInCombat]?.Type == JTokenType.Boolean && cfg.Is(RK.AllowChangesInCombat)))
                return ArmouryVerdict.No(string.Empty, S.CombatRefusalChangesDisabled, RM.CombatChangesDisabled);
            if (ctx.Loadout != null && ctx.NamesItem)
            {
                var grip = GripRefusal(d, ctx.Loadout, ctx.ClassId, slotId, ctx.SetIndex, ctx.ItemId);
                if (grip != null) return grip;
            }
            if (!string.IsNullOrEmpty(ctx.ItemId) && ctx.Attributes != null)
            {
                var piece = d.EquipmentRows(K.Armaments).FirstOrDefault(r => r.Str(K.Id) == ctx.ItemId)
                            ?? d.EquipmentRows(K.Armour).FirstOrDefault(r => r.Str(K.Id) == ctx.ItemId && (string.IsNullOrEmpty(ctx.ClassId) || r.Str(K.ClassId) == ctx.ClassId))
                            ?? d.EquipmentRows(K.Armour).FirstOrDefault(r => r.Str(K.Id) == ctx.ItemId);
                if (piece != null)
                {
                    var failures = Loadout.RequirementFailures(d, piece, ctx.Attributes, ctx.ItemUpgradeLevels ?? new JObject());
                    if (failures.Count > 0)
                    {
                        var shortOf = failures[0];
                        var attributeId = shortOf.Str(RK.AttributeId);
                        var label = Js.Truthy(d.Attributes.Get(attributeId)[RK.ShortLabel]) ? d.Attributes.Get(attributeId).Str(RK.ShortLabel) : attributeId;
                        var have = shortOf[RK.Actual]?.Type == JTokenType.Null ? RV.UnknownAmount : RunJs.NumStr(shortOf.Num(RK.Actual));
                        var name = Js.Truthy(piece[K.Name]) ? piece.Str(K.Name) : piece.Str(K.Id);
                        return ArmouryVerdict.No(string.Empty, S.CombatRefusalRequirement, RM.RequirementShort, name, label, RunJs.NumStr(shortOf.Num(RK.Required)), have);
                    }
                }
            }
            return ArmouryVerdict.Yes;
        }

        private static (JObject Piece, bool TwoHanded) HandHeld(RunData d, JObject loadout, string classId, string hand)
        {
            var slot = d.EquipmentRows(K.Slots).FirstOrDefault(row => Loadout.SlotHand(d, row) == hand);
            if (slot == null || loadout == null) return (null, false);
            var piece = Combat.Equipment.EquippedIn(d.Combat, loadout, classId, slot.Str(K.Id));
            var package = piece != null ? WeaponCards.FromPiece(d, piece) : null;
            return (piece, package != null && package.HandsRequired == d.RuleNum(K.Loadout, RK.TwoHandedHands));
        }

        /// <summary>
        /// gripRefusal(registries, loadout, classId, slotId, setIndex, itemId): null, or the refusal of the grip the hands
        /// would be left in — a two-handed piece beside an occupied other hand — judged on the loadout as the edit
        /// leaves it (a moved piece first leaves every other hand cell).
        /// </summary>
        public static ArmouryVerdict GripRefusal(RunData d, JObject loadout, string classId, string slotId, int? setIndex, string itemId)
        {
            var slot = SlotById(d, slotId);
            if (slot == null || Loadout.SlotHand(d, slot) == null || loadout == null) return null;
            var trial = RunJs.Clone(loadout);
            if (!(trial[K.Sets] is JObject sets)) trial[K.Sets] = sets = new JObject();
            if (!(trial[K.Active] is JObject)) trial[K.Active] = new JObject();
            foreach (var hand in d.EquipmentRows(K.Slots).Where(row => Loadout.SlotHand(d, row) != null))
            {
                var cells = new JArray(Js.Items(sets[hand.Str(K.Id)]).Select(held => !string.IsNullOrEmpty(itemId) && Js.Str(held) == itemId ? Js.Null() : held.DeepClone()));
                sets[hand.Str(K.Id)] = cells;
            }
            if (setIndex.HasValue && setIndex.Value >= 0)
            {
                var target = (JArray)sets[slotId];
                while (target.Count <= setIndex.Value) target.Add(Js.Null());
                target[setIndex.Value] = string.IsNullOrEmpty(itemId) ? Js.Null() : new JValue(itemId);
            }
            var right = HandHeld(d, trial, classId, V.Right);
            var left = HandHeld(d, trial, classId, V.Left);
            if ((right.TwoHanded && left.Piece != null) || (left.TwoHanded && right.Piece != null))
            {
                var two = right.TwoHanded ? right.Piece : left.Piece;
                var other = right.TwoHanded ? left.Piece : right.Piece;
                string Name(JObject p) => Js.Truthy(p[K.Name]) ? p.Str(K.Name) : p.Str(K.Id);
                return ArmouryVerdict.No(string.Empty, S.CombatRefusalGrip, RM.GripConflict, Name(two), Name(other));
            }
            return null;
        }

        /// <summary>
        /// openedSets(registries, slot, { meta, loadout }): how many sets are usable — one, plus the ladder rungs the
        /// profile has earned (none: combat passes an empty meta), raised to whatever the loadout already holds, capped
        /// at the slot's sets.
        /// </summary>
        public static int OpenedSets(RunData d, JObject slot, JObject loadout)
        {
            var sets = RunJs.Number(slot?[K.Sets]);
            var cap = Math.Max(1, double.IsNaN(sets) || sets == 0 ? 1 : sets);
            double opened = 1;
            var ids = Cells(loadout, slot.Str(K.Id));
            if (ids != null) for (var i = 0; i < ids.Count; i += 1) if (Js.Truthy(ids[i])) opened = Math.Max(opened, i + 1);
            return (int)Math.Min(cap, opened);
        }

        /// <summary>
        /// cycleSet(registries, loadout, slotId, index, { meta: {}, inCombat, classId, onEquipmentChanged }): make set
        /// <paramref name="index"/> active. False when the index, the slot, the swap rule, the ladder or the deck plan
        /// says no (the active index is then unchanged).
        /// </summary>
        public static bool CycleSet(RunData d, JObject loadout, string slotId, int index, bool inCombat, string classId, Action<JObject> onEquipmentChanged)
        {
            var ids = Cells(loadout, slotId);
            if (ids == null || index < 0 || index >= ids.Count) return false;
            var slot = SlotById(d, slotId);
            if (slot == null) return false;
            if (!CanSwap(d, slotId, inCombat).Ok) return false;
            if (index >= OpenedSets(d, slot, loadout)) return false;
            var before = RunJs.Clone(loadout);
            var active = loadout.Obj(K.Active);
            active.Put(slotId, index);
            try
            {
                WeaponCards.BuildPlan(d, loadout, string.IsNullOrEmpty(classId) ? null : classId, null, null);
            }
            catch (Exception)
            {
                active[slotId] = before.Obj(K.Active)[slotId]?.DeepClone();
                return false;
            }
            onEquipmentChanged?.Invoke(ChangedPayload(d, RV.ReasonSwapSet, before, loadout));
            return true;
        }

        /// <summary>ownership(registries, { meta: {}, loadout }).has: is this piece yours — unlocked, found (carried, per run) or the creation grant.</summary>
        public static Func<JObject, bool> Ownership(RunData d, JObject loadout)
        {
            var cfg = d.EquipmentBalance;
            var drops = cfg.Obj(RK.Drops) ?? new JObject();
            var persistence = cfg.Str(RK.Persistence);
            var found = new HashSet<string>(StringComparer.Ordinal);
            if (persistence != RV.PersistenceUnlocked) foreach (var id in CarriedIds(loadout)) found.Add(id);
            var grant = loadout?.Obj(RK.CreationArmourGrant);
            var basicTag = cfg.Str(RK.BasicTag);
            bool IsBasic(JObject piece) => !string.IsNullOrEmpty(basicTag) && Js.Includes(piece[K.Tags], basicTag);
            string Why(JObject piece)
            {
                if (piece == null) return RV.GateUnearned;
                if (grant != null && Equipment.StrictEquals(grant[K.ClassId], piece[K.ClassId]) && Equipment.StrictEquals(grant[K.Id], piece[K.Id])) return null;
                if (!Js.Nullish(piece[RK.Unlock]) && Js.Str(piece[RK.Unlock]) != string.Empty) return RV.GateUnearned;
                if (piece.Str(K.Kind) != V.Armor && drops.Is(RK.RequireFound))
                    return IsBasic(piece) || found.Contains(piece.Str(K.Id) ?? V.Undefined) ? null : RV.GateUnfound;
                return null;
            }
            return piece => piece != null && Why(piece) == null;
        }

        /// <summary>carriedIds(loadout): storage, then every piece slotted in any set (once each).</summary>
        public static List<string> CarriedIds(JObject loadout)
        {
            var out_ = new List<string>();
            if (loadout == null) return out_;
            foreach (var id in Js.Items(loadout[RK.Storage])) out_.Add(Js.Str(id));
            foreach (var p in (loadout.Obj(K.Sets) ?? new JObject()).Properties())
                foreach (var id in Js.Items(p.Value))
                    if (Js.Truthy(id) && !out_.Contains(Js.Str(id))) out_.Add(Js.Str(id));
            return out_;
        }

        /// <summary>fitsSlot(slot, piece): the slot takes the piece's kind, and the piece's hand (if it has one) is the slot's.</summary>
        public static bool FitsSlot(RunData d, JObject slot, JObject piece)
        {
            if (slot == null || piece == null) return false;
            if (!Js.Includes(slot[K.Kinds], piece.Str(K.Kind))) return false;
            var may = piece.Str(K.Hand);
            if (may == null || !d.RuleList(K.Loadout, RK.Hands).Contains(may)) return true;
            return may == Loadout.SlotHand(d, slot);
        }

        /// <summary>
        /// equipTransitionPlan + applyEquipTransition: put <paramref name="itemId"/> (or nothing) into one position. A hand
        /// position also clears the piece from every other hand cell and sends the piece it displaces to storage, which
        /// must have room. False when the location or the storage refuses.
        /// </summary>
        public static bool ApplyEquipTransition(RunData d, JObject loadout, string slotId, int setIndex, string itemId)
        {
            var ids = Cells(loadout, slotId);
            var slot = SlotById(d, slotId);
            if (slot == null || ids == null || setIndex < 0 || setIndex >= ids.Count) return false;
            var previousId = Js.Truthy(ids[setIndex]) ? Js.Str(ids[setIndex]) : null;
            JToken Held() => string.IsNullOrEmpty(itemId) ? Js.Null() : new JValue(itemId);
            if (Loadout.SlotHand(d, slot) == null)
            {
                ids[setIndex] = Held();
                return true;
            }
            var cap = Js.IsInt(d.EquipmentBalance[RK.StorageSlots]) ? d.EquipmentBalance.Num(RK.StorageSlots) : d.RuleNum(K.Loadout, RK.StorageSlotsDefault);
            var nextStorage = new List<JToken>();
            foreach (var id in Js.Items(loadout[RK.Storage]))
                if (!nextStorage.Any(x => Equipment.StrictEquals(x, id))) nextStorage.Add(id);
            nextStorage = nextStorage.Where(id => !Equipment.StrictEquals(id, itemId == null ? Js.Null() : new JValue(itemId))).ToList();
            var storesPrevious = previousId != null && previousId != itemId && !nextStorage.Any(id => Js.Str(id) == previousId);
            if (storesPrevious && nextStorage.Count >= cap) return false;
            var handSlotIds = new HashSet<string>(d.EquipmentRows(K.Slots).Where(s => Loadout.SlotHand(d, s) != null).Select(s => s.Str(K.Id)), StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(itemId))
                foreach (var p in (loadout.Obj(K.Sets) ?? new JObject()).Properties())
                {
                    if (!handSlotIds.Contains(p.Name) || !(p.Value is JArray others)) continue;
                    for (var i = 0; i < others.Count; i++)
                        if (Js.Str(others[i]) == itemId && (p.Name != slotId || i != setIndex)) others[i] = Js.Null();
                }
            if (storesPrevious) nextStorage.Add(new JValue(previousId));
            loadout[RK.Storage] = new JArray(nextStorage.Select(x => x.DeepClone()));
            ids[setIndex] = Held();
            return true;
        }

        /// <summary>
        /// equipPiece(registries, loadout, slotId, setIndex, itemId, owned, ctx): put a piece into one set of a slot, or
        /// clear it with a null piece. The gates are on the mutation: canEquip (the combat rule, the grip, the attribute
        /// minima), the slot's kinds and hand, ownership, the requirement receipt, the storage transition and the deck
        /// plan. True when it changed the loadout.
        /// </summary>
        public static bool EquipPiece(RunData d, JObject loadout, string slotId, int setIndex, string itemId, Func<JObject, bool> owned, EquipContext ctx, Action<JObject> onEquipmentChanged)
        {
            var ids = Cells(loadout, slotId);
            if (ids == null || setIndex < 0 || setIndex >= ids.Count) return false;
            var permission = CanEquip(d, slotId, new EquipContext
            {
                InCombat = ctx.InCombat, Loadout = loadout, ClassId = ctx.ClassId, SetIndex = setIndex, NamesItem = true, ItemId = itemId,
                Attributes = ctx.Attributes, ItemUpgradeLevels = ctx.Attributes != null ? ctx.ItemUpgradeLevels : null,
            });
            if (!permission.Ok) return false;
            var slot = SlotById(d, slotId);
            if (slot == null) return false;
            var before = RunJs.Clone(loadout);
            var appearedElsewhere = !string.IsNullOrEmpty(itemId) && (loadout.Obj(K.Sets) ?? new JObject()).Properties().Any(p =>
                Js.Items(p.Value).Select((value, index) => (value, index)).Any(x => Js.Str(x.value) == itemId && (p.Name != slotId || x.index != setIndex)));
            var reason = string.IsNullOrEmpty(itemId) ? RV.ReasonUnequip : appearedElsewhere ? RV.ReasonMove : RV.ReasonEquip;
            if (string.IsNullOrEmpty(itemId))
            {
                var changed = ApplyEquipTransition(d, loadout, slotId, setIndex, null);
                if (changed) onEquipmentChanged?.Invoke(ChangedPayload(d, reason, before, loadout));
                return changed;
            }
            JObject piece;
            if (Js.Includes(slot[K.Kinds], V.Armor))
                piece = d.EquipmentRows(K.Armour).FirstOrDefault(o => o.Str(K.Id) == itemId && !string.IsNullOrEmpty(ctx.ClassId) && o.Str(K.ClassId) == ctx.ClassId)
                        ?? d.EquipmentRows(K.Armour).FirstOrDefault(o => o.Str(K.Id) == itemId);
            else piece = d.EquipmentRows(K.Armaments).FirstOrDefault(a => a.Str(K.Id) == itemId);
            if (piece == null || !FitsSlot(d, slot, piece)) return false;
            if (owned == null || !owned(piece)) return false;
            if (Loadout.RequirementFailures(d, piece, ctx.Attributes, ctx.ItemUpgradeLevels ?? new JObject()).Count > 0) return false;
            var next = RunJs.Clone(loadout);
            if (!ApplyEquipTransition(d, next, slotId, setIndex, itemId)) return false;
            try
            {
                WeaponCards.BuildPlan(d, next, string.IsNullOrEmpty(ctx.ClassId) ? null : ctx.ClassId, null, null);
            }
            catch (Exception)
            {
                return false;
            }
            foreach (var key in new[] { K.Sets, K.Active, RK.Storage })
                if (next[key] != null) loadout[key] = next[key].DeepClone();
                else loadout.Remove(key);
            onEquipmentChanged?.Invoke(ChangedPayload(d, reason, before, loadout));
            return true;
        }

        /// <summary>loadoutSignature(loadout): 'slot:activeItem|…' over the sorted slot ids ('-' for an empty cell).</summary>
        public static string LoadoutSignature(JObject loadout)
        {
            if (loadout == null) return string.Empty;
            var sets = loadout.Obj(K.Sets) ?? new JObject();
            var active = loadout.Obj(K.Active) ?? new JObject();
            return string.Join(RV.SignatureJoiner, sets.Properties().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).Select(k =>
            {
                var index = (int)Js.Or0(active[k]);
                var id = Cell(sets[k] as JArray, index);
                return k + RV.SignaturePairSeparator + (string.IsNullOrEmpty(id) ? RV.SignatureEmptyCell : id);
            }));
        }

        /// <summary>equipmentChangedPayload(reason, before, after): the reason, both signatures and every position whose item or active flag moved.</summary>
        public static JObject ChangedPayload(RunData d, string reason, JObject before, JObject after)
        {
            var changed = new JArray();
            var beforeSets = before?.Obj(K.Sets) ?? new JObject();
            var afterSets = after?.Obj(K.Sets) ?? new JObject();
            var slotIds = beforeSets.Properties().Select(p => p.Name).Union(afterSets.Properties().Select(p => p.Name)).OrderBy(k => k, StringComparer.Ordinal);
            foreach (var slotId in slotIds)
            {
                var beforeIds = beforeSets[slotId] as JArray ?? new JArray();
                var afterIds = afterSets[slotId] as JArray ?? new JArray();
                var count = Math.Max(beforeIds.Count, afterIds.Count);
                for (var setIndex = 0; setIndex < count; setIndex++)
                {
                    var beforeItemId = Cell(beforeIds, setIndex);
                    var afterItemId = Cell(afterIds, setIndex);
                    if (string.IsNullOrEmpty(beforeItemId)) beforeItemId = null;
                    if (string.IsNullOrEmpty(afterItemId)) afterItemId = null;
                    var beforeActive = Js.Or0(before?.Obj(K.Active)?[slotId]) == setIndex;
                    var afterActive = Js.Or0(after?.Obj(K.Active)?[slotId]) == setIndex;
                    if (beforeItemId != afterItemId || beforeActive != afterActive)
                        changed.Add(Js.Obj(K.SlotId, slotId, K.SetIndex, setIndex, RK.BeforeItemId, Js.S(beforeItemId), RK.AfterItemId, Js.S(afterItemId),
                            RK.BeforeActive, beforeActive, RK.AfterActive, afterActive));
                }
            }
            return Js.Obj(K.Reason, reason, RK.BeforeLoadoutSignature, LoadoutSignature(before), RK.AfterLoadoutSignature, LoadoutSignature(after),
                RK.ChangedPositions, changed);
        }
    }
}
