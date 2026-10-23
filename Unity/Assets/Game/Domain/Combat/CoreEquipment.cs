using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RR = Ashen.Generated.Core2RemainingKeys;
using RV = Ashen.Generated.RunValues;
using S = Ashen.Generated.CombatStringKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The mid-fight equipment doors of a Core 2 fight (web engine/combat.js doSwapArmament and doChangeEquipment, WP-C0 D-292): cycle a hand to another of its sets,
    /// or put a carried piece into a position. Both follow the shipped order: the guards (phase, equipment on, a loadout, canSwap / canEquip, the preview equip), the
    /// price (swapCostFor under the fight's rule) and its currency (Stamina, or the per-turn allowance), the mutation, the property remount, the Stamina charge, the pool
    /// moves, the granted-card reconcile and the four pile restamps, the Poise vessel, the armour re-projected onto the player, the Draw, the event and, when the rule says
    /// so, the end of the turn. A Core fight differs from the 405 in three places: the Actions pool is Stamina, the pool delta carries what the gear's attributes are worth
    /// (combatEquipmentPoolProjection), and the Draw follows the formula.
    /// </summary>
    public static class CoreEquipment
    {
        private sealed class Guard
        {
            public ArmouryVerdict Refusal;
            public JObject Price;
            public Func<JObject, bool> Owned;
            public EquipContext Context;
        }

        private static Guard Refuse(string stringKey, string template, params object[] args) =>
            new Guard { Refusal = ArmouryVerdict.No(null, stringKey, template, args) };

        private static Guard Refuse(ArmouryVerdict verdict) => new Guard { Refusal = verdict };

        private static RunData Data(CombatState c) => c.CoreData.Run.Run;

        private static JObject Cfg(CombatState c) => Data(c).EquipmentBalance;

        private static bool Allowance(CombatState c) => Cfg(c).Str(K.SwapCostKind) == V.SwapAllowance;

        private static double RelicDelta(CombatState c) =>
            Cards.PassiveSum(c.Data, c.Player.Arr(K.RelicIds), RK.SwapCostDelta, new JObject(), Properties.MountsOf(c, c.Player));

        private static JObject PriceOn(CombatState c, JObject loadout, string slotId, int setIndex) =>
            Armoury.SwapCostFor(Data(c), c.SwapCostRule, loadout, c.Player.Str(K.ClassId), slotId, setIndex, RelicDelta(c));

        private static Guard Head(CombatState c, string turnTemplate)
        {
            if (c.Phase != V.PhasePlayer) return Refuse(S.CombatRefusalNotPlayerTurn, turnTemplate);
            if (!Cfg(c).Is(K.Enabled)) return Refuse(S.CombatRefusalEquipmentDisabled, RM.EquipmentDisabled);
            if (c.Loadout == null) return Refuse(S.CombatRefusalNoLoadout, RM.CombatHasNoLoadout);
            return null;
        }

        /// <summary>The currency check once the price is known: the Actions pool of a Core fight is its Stamina.</summary>
        private static Guard Afford(CombatState c, JObject price, string emptyKey, string emptyTemplate, string energyKey, string energyTemplate)
        {
            if (Allowance(c))
            {
                if (c.SwapsLeft < 1) return Refuse(emptyKey, emptyTemplate);
            }
            else if (c.Player.Num(K.Stamina) < price.Num(K.Cost))
                return Refuse(energyKey, energyTemplate, RunJs.NumStr(price.Num(K.Cost)), RunJs.NumStr(c.Player.Num(K.Stamina)));
            return null;
        }

        private static EquipContext ContextOf(CombatState c) =>
            new EquipContext { InCombat = true, Attributes = Allocated(c), ItemUpgradeLevels = c.ItemUpgradeLevels, ClassId = c.Player.Str(K.ClassId), ConfiguredDeck = true };

        private static Guard SwapGuards(CombatState c, string slotId, int setIndex)
        {
            var head = Head(c, RM.SwapNotYourTurn);
            if (head != null) return head;
            var allowed = Armoury.CanSwap(Data(c), slotId, true);
            if (!allowed.Ok) return Refuse(allowed);
            var price = PriceOn(c, c.Loadout, slotId, setIndex);
            return Afford(c, price, S.CombatRefusalNoSwapsLeft, RM.NoSwapsLeft, S.CombatRefusalSwapEnergy, RM.SwapCostsEnergy) ?? new Guard { Price = price };
        }

        private static Guard ChangeGuards(CombatState c, string slotId, int setIndex, string pieceId)
        {
            var head = Head(c, RM.ChangeNotYourTurn);
            if (head != null) return head;
            var d = Data(c);
            var allowed = Armoury.CanEquip(d, slotId, new EquipContext { InCombat = true });
            if (!allowed.Ok) return Refuse(allowed);
            var owned = Armoury.Ownership(d, c.Loadout);
            var ctx = ContextOf(c);
            var preview = RunJs.Clone(c.Loadout);
            if (!Armoury.EquipPiece(d, preview, slotId, setIndex, pieceId, owned, ctx, null))
                return Refuse(S.CombatRefusalChangeUnavailable, RM.ChangeNotAvailable);
            var price = PriceOn(c, preview, slotId, setIndex);
            return Afford(c, price, S.CombatRefusalNoChangesLeft, RM.NoChangesLeft, S.CombatRefusalChangeEnergy, RM.ChangeCostsEnergy)
                   ?? new Guard { Price = price, Owned = owned, Context = ctx };
        }

        // ------------------------------------------------------------------ the intents

        /// <summary>doSwapArmament(combat, { slotId, setIndex }).</summary>
        public static void Swap(CombatState c, string slotId, int setIndex)
        {
            var guard = SwapGuards(c, slotId, setIndex);
            if (guard.Refusal != null) throw new InvalidOperationException(guard.Refusal.Message);
            var d = Data(c);
            var p = c.Player;
            var classId = p.Str(K.ClassId);
            var activeBefore = c.Loadout.Obj(K.Active)?[slotId]?.DeepClone();
            var poolBefore = PoolProjection(c);
            if (!Armoury.CycleSet(d, c.Loadout, slotId, setIndex, true, classId, payload => c.Emit(E.EquipmentChanged, payload), true))
                throw new InvalidOperationException(RunJs.Fmt(RM.NoSetOnSlot, setIndex, slotId));
            var poolAfter = PoolProjection(c);
            Charge(c, guard.Price);
            Properties.SyncLoadout(c);
            if (!Equipment.StrictEquals(activeBefore, c.Loadout.Obj(K.Active)?[slotId]))
            {
                c.EquipmentChanged = true;
                MovePools(c, poolBefore, poolAfter);
            }
            Restamp(c);
            Expanded(c, poolBefore, poolAfter);
            c.Emit(E.ArmamentSwapped, Js.Obj(K.SlotId, slotId, K.SetIndex, setIndex, K.Cost, guard.Price.Num(K.Cost), RK.Rule, guard.Price[RK.RuleId]));
            if (Cfg(c).Is(RK.SwapEndsTurn)) CoreTurns.EndTurn(c, new string[0]);
        }

        /// <summary>doChangeEquipment(combat, { slotId, setIndex, pieceId }).</summary>
        public static void Change(CombatState c, string slotId, int setIndex, string pieceId)
        {
            var guard = ChangeGuards(c, slotId, setIndex, pieceId);
            if (guard.Refusal != null) throw new InvalidOperationException(guard.Refusal.Message);
            var d = Data(c);
            var p = c.Player;
            var classId = p.Str(K.ClassId);
            var poolBefore = PoolProjection(c);
            JObject changeEvent = null;
            if (!Armoury.EquipPiece(d, c.Loadout, slotId, setIndex, pieceId, guard.Owned, guard.Context, payload => changeEvent = payload))
                throw new InvalidOperationException(RM.ChangeNoLongerAvailable);
            Properties.SyncLoadout(c);
            var poolAfter = PoolProjection(c);
            Charge(c, guard.Price);
            c.EquipmentChanged = true;
            MovePools(c, poolBefore, poolAfter);
            Restamp(c);
            Expanded(c, poolBefore, poolAfter);
            if (changeEvent != null) c.Emit(E.EquipmentChanged, changeEvent);
            c.Emit(E.EquipmentRearmed, Js.Obj(K.SlotId, slotId, K.SetIndex, setIndex, K.PieceId, Js.S(pieceId), K.Cost, guard.Price.Num(K.Cost), RK.Rule, guard.Price[RK.RuleId]));
            if (Cfg(c).Is(RK.SwapEndsTurn)) CoreTurns.EndTurn(c, new string[0]);
        }

        private static void Charge(CombatState c, JObject price)
        {
            if (Allowance(c)) c.SwapsLeft -= 1;
            else c.Player.Put(K.Stamina, c.Player.Num(K.Stamina) - price.Num(K.Cost));
        }

        /// <summary>Each pool maximum moves by what the equipment change moved it (HP never below one), carrying its deficit.</summary>
        private static void MovePools(CombatState c, RunModsResult before, RunModsResult after)
        {
            var d = Data(c);
            var p = c.Player;
            var fields = Loadout.PoolFields(d);
            var current = d.RuleObj(K.Loadout, RK.PoolCurrent);
            foreach (var maxField in fields)
            {
                var currentField = current.Str(maxField);
                double floor = maxField == fields[0] ? 1 : 0;
                var nextMax = Math.Max(floor, p.Num(maxField) + after.Pool(maxField) - before.Pool(maxField));
                c.EquipmentPoolDeficits.Put(currentField, MoveEquipmentPool(d, p, maxField, nextMax, c.EquipmentPoolDeficits[currentField]));
            }
        }

        /// <summary>moveEquipmentPool(holder, maxField, nextMax, carriedDeficit): the vessel is resized and its deficit kept; Stamina above the old maximum stays above the new one.</summary>
        public static double MoveEquipmentPool(RunData d, JObject holder, string maxField, double nextMax, JToken carriedDeficit)
        {
            var currentField = d.RuleObj(K.Loadout, RK.PoolCurrent).Str(maxField);
            if (currentField == null || !Js.IsFinite(holder[maxField]) || !Js.IsFinite(holder[currentField]))
                throw new InvalidOperationException(RunJs.Fmt(RM.MovePoolNeedsFinite, maxField, currentField ?? V.Undefined));
            var oldMax = holder.Num(maxField);
            var surplus = currentField == K.Stamina ? Math.Max(0, holder.Num(currentField) - oldMax) : 0;
            var observed = Math.Max(0, oldMax - holder.Num(currentField));
            var prior = Js.IsInt(carriedDeficit) && Js.D(carriedDeficit) >= 0 ? Js.D(carriedDeficit) : observed;
            var represented = Math.Min(prior, oldMax);
            var deficit = Math.Max(0, prior + observed - represented);
            holder.Put(maxField, nextMax);
            holder.Put(currentField, Math.Max(0, nextMax - deficit) + surplus);
            return deficit;
        }

        // ------------------------------------------------------------------ pools

        /// <summary>The attributes the player allocated: the fight's saved allocation, else its attributes.</summary>
        public static JObject Allocated(CombatState c) => c.Fields.Obj(CK.AllocatedAttributes) ?? c.Attributes;

        /// <summary>combatEquipmentPoolProjection(ctx) for a configured fight: the worn gear's run mods, plus what the attributes it adds are worth in each pool and in the Draw.</summary>
        public static RunModsResult PoolProjection(CombatState c)
        {
            var d = c.CoreData.Run;
            var classId = c.Player.Str(K.ClassId);
            var allocated = Allocated(c);
            var subject = new JObject { [RK.Class] = classId, [K.Loadout] = c.Loadout, [K.ItemUpgradeLevels] = c.ItemUpgradeLevels, [K.Attributes] = allocated };
            var attributes = CoreGear.EffectiveAttributes(d, subject, allocated);
            var table = (c.DerivedStatRuleSnapshot as JObject)?.Obj(K.Rules);
            var classDef = d.Run.Classes.Get(classId);
            var level = Js.Truthy(c.Fields[CK.CharacterLevel]) ? Js.D(c.Fields[CK.CharacterLevel]) : 1;
            double Value(string stat, JObject attrs) => CoreDerivedStats.Derive(d, table, stat, attrs, classDef, level).Value;
            var mods = CoreRunRules.RunMods(d, c.Loadout, classId, subject);
            var stats = c.CoreData.Section(RR.Equipment).Obj(RR.PoolStats) ?? new JObject();
            var result = new RunModsResult { SwapCostDelta = mods.SwapCostDelta, StartStatuses = mods.StartStatuses };
            foreach (var pair in mods.Pools)
            {
                var stat = stats.Str(pair.Key);
                result.Pools.Add(new KeyValuePair<string, double>(pair.Key, pair.Value + (stat != null ? Value(stat, attributes) - Value(stat, allocated) : 0)));
            }
            result.DrawPerTurn = Value(c.CoreData.Section(RR.Equipment).Str(RR.DrawStat), attributes);
            return result;
        }

        // ------------------------------------------------------------------ the restamp

        /// <summary>The granted-card reconcile and the four pile restamps against a synthetic run, then the unrated Poise vessel.</summary>
        private static void Restamp(CombatState c)
        {
            var d = Data(c);
            var p = c.Player;
            var run = new JObject { [K.Deck] = new JArray(), [K.Loadout] = RunJs.Clone(c.Loadout), [RK.Class] = Js.S(p.Str(K.ClassId)) };
            void Put(string key, JToken value)
            {
                if (value != null) run[key] = value.DeepClone();
            }
            Put(K.Attributes, Allocated(c));
            Put(CK.CombatExpansionVersion, c.Fields[CK.CombatExpansionVersion]);
            Put(Ashen.Generated.Core2RunSnapshotKeys.AshenBlight, p[CK.AshenBlight]);
            Put(K.ItemUpgradeLevels, c.ItemUpgradeLevels);
            Put(K.EquipmentProfileRuleSnapshot, c.EquipmentProfileRuleSnapshot);
            Put(K.EquipmentAttackSlotCount, c.EquipmentAttackSlotCount);
            Put(K.RemovedAttackSlotIds, c.RemovedAttackSlotIds);
            Put(CK.SideboardedEquipmentCardIds, c.Fields[CK.SideboardedEquipmentCardIds]);
            Put(K.ItemMounts, c.ItemMounts);
            Put(K.DerivedStatRuleSnapshot, c.DerivedStatRuleSnapshot);
            if (Js.IsInt(c.Fields[CK.CharacterLevel])) run[K.Level] = new JObject { [K.Level] = c.Fields[CK.CharacterLevel].DeepClone() };
            ReconcileGranted(c, run);
            foreach (var pile in new[] { c.Piles.Hand, c.Piles.Draw, c.Piles.Discard, c.Piles.Exhaust }) StartingDeck.StampPile(d, run, pile, true);
            if (c.RatingsRules == null)
            {
                // stampPlayerPoiseMax(p, playerPoiseThresholdReceipt(...).value) on the fight's loadout, relics, upgrade levels, attributes, rule snapshot and level.
                var vessel = new JObject
                {
                    [K.Loadout] = c.Loadout, [K.RelicIds] = p[K.RelicIds], [K.ClassId] = p[K.ClassId], [K.ItemUpgradeLevels] = c.ItemUpgradeLevels,
                    [K.Attributes] = c.Attributes, [K.DerivedStatRuleSnapshot] = c.DerivedStatRuleSnapshot,
                };
                if (Js.IsInt(c.Fields[CK.CharacterLevel])) vessel[K.Level] = c.Fields[CK.CharacterLevel].DeepClone();
                CombatStart.StampPoiseMax(c.Data, p, CoreCombatStart.PoiseVessel(c, vessel));
            }
        }

        /// <summary>reconcileGrantedCardsInCombat(registries, run, piles) for a Core fight: what the worn gear no longer lends leaves every pile, what it keeps adopts its ownership, what it newly lends lands in the discard pile.</summary>
        private static void ReconcileGranted(CombatState c, JObject run)
        {
            var d = c.CoreData.Run;
            var desired = CoreRunRules.GearGrantInstances(d, run);
            var wanted = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var w in desired) wanted[w.Str(K.InstanceId) ?? V.Undefined] = w;
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pile in new[] { c.Piles.Hand, c.Piles.Draw, c.Piles.Discard, c.Piles.Exhaust })
            {
                var kept = new List<JObject>();
                foreach (var inst in pile)
                {
                    if (!StartingDeck.IsItemOwned(d.Run, inst)) { kept.Add(inst); continue; }
                    if (!wanted.TryGetValue(inst.Str(K.InstanceId) ?? V.Undefined, out var want)) continue;
                    present.Add(inst.Str(K.InstanceId));
                    kept.Add(StartingDeck.AdoptWanted(inst, want));
                }
                pile.Clear();
                pile.AddRange(kept);
            }
            foreach (var w in desired) if (!present.Contains(w.Str(K.InstanceId) ?? V.Undefined)) c.Piles.Discard.Add(w);
        }

        // ------------------------------------------------------------------ the armour on the player

        /// <summary>The v2 block of both doors: the worn armour re-projected onto the player, the pool maxima's base moved by the delta, the Draw as the formula gives it.</summary>
        private static void Expanded(CombatState c, RunModsResult before, RunModsResult after)
        {
            if (Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion) return;
            var p = c.Player;
            var projection = ExpandedProjection(c, p.Str(K.ClassId));
            foreach (var pair in projection.Properties()) p[pair.Name] = pair.Value.DeepClone();
            var bases = p.Obj(CK.BaseResourceMaxima);
            foreach (var field in Loadout.PoolFields(Data(c))) bases.Put(field, bases.Num(field) + after.Pool(field) - before.Pool(field));
            if (after.DrawPerTurn != null) p.Put(K.DrawPerTurn, after.DrawPerTurn.Value);
        }

        /// <summary>expandedEquipmentProjection(registries, loadout, classId, rules): armour class, flat resistances, weaknesses, resistances and traits of the worn pieces.</summary>
        public static JObject ExpandedProjection(CombatState c, string classId)
        {
            var rules = c.Fields.Obj(CK.CombatExpansionRules)?.Obj(RR.Equipment);
            var rows = new List<JObject>();
            if (c.Loadout != null)
                foreach (var slot in Js.Items(c.Data.Equipment[K.Slots]).OfType<JObject>())
                {
                    var piece = Equipment.EquippedIn(c.Data, c.Loadout, classId, slot.Str(K.Id));
                    if (piece == null) continue;
                    var armour = Js.Includes(slot[K.Kinds], V.Armor);
                    rows.Add(armour
                        ? rules?.Obj(RR.ArmourRows)?.Obj(classId + V.KeySeparator + piece.Str(K.Id)) ?? rules?.Obj(RR.SharedArmourRows)?.Obj(piece.Str(K.Id))
                        : rules?.Obj(RR.ArmamentRows)?.Obj(piece.Str(K.Id)));
                    if (Js.Truthy(piece[RR.CombatExpansionRow])) rows.Add(piece.Obj(RR.CombatExpansionRow));
                }
            return Projection(c, rows);
        }

        private static double Positive(JToken value) => Js.IsFinite(value) ? Math.Max(0, Js.D(value)) : 0;

        private static JObject Projection(CombatState c, List<JObject> rows)
        {
            var cap = Math.Round(c.CoreData.CombatRules.Num(RR.ResistanceCap) * 100, MidpointRounding.AwayFromZero);
            var result = new JObject
            {
                [RR.ArmorClass] = RR.ArmorClassLight, [RR.DamageResistanceFlat] = new JObject(), [RR.DamageWeaknessPercent] = new JObject(),
                [RR.DamageResistPercent] = new JObject(), [RR.CombatTraits] = new JObject(),
            };
            foreach (var row in rows)
            {
                if (row == null) continue;
                if (Js.Truthy(row[RR.ArmorClass])) result[RR.ArmorClass] = row[RR.ArmorClass].DeepClone();
                foreach (var pair in (row.Obj(RR.RowFlat) ?? new JObject()).Properties()) result.Obj(RR.DamageResistanceFlat).Put(pair.Name, result.Obj(RR.DamageResistanceFlat).Or0(pair.Name) + Positive(pair.Value));
                foreach (var pair in (row.Obj(RR.RowWeak) ?? new JObject()).Properties()) result.Obj(RR.DamageWeaknessPercent).Put(pair.Name, Math.Max(result.Obj(RR.DamageWeaknessPercent).Or0(pair.Name), Positive(pair.Value)));
                foreach (var pair in (row.Obj(RR.RowResist) ?? new JObject()).Properties()) result.Obj(RR.DamageResistPercent).Put(pair.Name, Math.Min(cap, Math.Max(result.Obj(RR.DamageResistPercent).Or0(pair.Name), Positive(pair.Value))));
                foreach (var pair in (row.Obj(RR.RowTraits) ?? new JObject()).Properties())
                {
                    var traits = result.Obj(RR.CombatTraits);
                    if (pair.Value is JObject map)
                    {
                        if (!(traits[pair.Name] is JObject into)) traits[pair.Name] = into = new JObject();
                        foreach (var entry in map.Properties()) into.Put(entry.Name, into.Or0(entry.Name) + Positive(entry.Value));
                    }
                    else if (pair.Value is JArray list)
                    {
                        var union = new JArray();
                        foreach (var item in Js.Items(traits[pair.Name]).Concat(list)) if (!union.Any(x => JToken.DeepEquals(x, item))) union.Add(item.DeepClone());
                        traits[pair.Name] = union;
                    }
                    else if (pair.Value.Type == JTokenType.Boolean) traits[pair.Name] = Js.Truthy(traits[pair.Name]) || (bool)pair.Value;
                }
            }
            return result;
        }
    }
}
