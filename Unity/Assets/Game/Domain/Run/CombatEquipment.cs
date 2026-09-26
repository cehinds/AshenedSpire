using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;
using S = Ashen.Generated.CombatStringKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// The mid-fight equipment intents (shipped engine/combat.js doSwapArmament and doChangeEquipment, us-5.11) and
    /// createCombat's profile-snapshot fallback, as the run port the combat engine asks (<see cref="IEquipmentPort"/>).
    /// Both intents follow the shipped order exactly: the guards (phase, equipment on, a loadout, canSwap / canEquip and
    /// the preview equip), the price (swapCostFor under the fight's rule, the relics' swapCostDelta summed here) and its
    /// currency (energy, or the per-turn allowance), then the mutation (cycleSet / equipPiece), the property remount,
    /// the pool moves (a changed maximum carries the deficit), the charge, the granted-card reconcile and the four pile
    /// restamps through a synthetic run, the unrated Poise restamp, the ratings refresh, the events and — when the rule
    /// says so — the end of the turn. The guards are also asked without mutating, for legality and prices.
    /// </summary>
    public sealed class CombatEquipment : IEquipmentPort
    {
        private readonly RunData _d;

        public CombatEquipment(RunData d) => _d = d ?? throw new ArgumentNullException(nameof(d));

        public JObject DerivedStatRules => _d.DerivedStatRules;

        public JObject CreateProfileSnapshot() => Loadout.CreateProfileSnapshot(_d, new DerivedOptions());

        private sealed class Guard
        {
            public ArmouryVerdict Refusal;
            public JObject Price;
            public JObject PreviewLoadout;
            public Func<JObject, bool> Owned;
            public EquipContext Context;
        }

        private static Guard Refuse(string stringKey, string template, params object[] args) =>
            new Guard { Refusal = ArmouryVerdict.No(string.Empty, stringKey, template, args) };

        private static Guard Refuse(ArmouryVerdict verdict) => new Guard { Refusal = verdict };

        private JObject Cfg => _d.EquipmentBalance;

        private bool Allowance => Cfg.Str(K.SwapCostKind) == V.SwapAllowance;

        private static double RelicDelta(CombatState c) =>
            Cards.PassiveSum(c.Data, c.Player.Arr(K.RelicIds), RK.SwapCostDelta, new JObject(), Properties.MountsOf(c, c.Player));

        private JObject PriceOn(CombatState c, JObject loadout, string slotId, int setIndex) =>
            Armoury.SwapCostFor(_d, c.SwapCostRule, loadout, c.Player.Str(K.ClassId), slotId, setIndex, RelicDelta(c));

        /// <summary>The shared head of both intents: the phase, equipment on, a loadout (null when they pass).</summary>
        private Guard Head(CombatState c, string turnTemplate)
        {
            if (c.Phase != V.PhasePlayer) return Refuse(S.CombatRefusalNotPlayerTurn, turnTemplate);
            if (!Cfg.Is(K.Enabled)) return Refuse(S.CombatRefusalEquipmentDisabled, RM.EquipmentDisabled);
            if (c.Loadout == null) return Refuse(S.CombatRefusalNoLoadout, RM.CombatHasNoLoadout);
            return null;
        }

        /// <summary>The currency check once the price is known.</summary>
        private Guard Afford(CombatState c, JObject price, string emptyKey, string emptyTemplate, string energyKey, string energyTemplate)
        {
            if (Allowance)
            {
                if (c.SwapsLeft < 1) return Refuse(emptyKey, emptyTemplate);
            }
            else if (c.Player.Num(K.Energy) < price.Num(K.Cost))
                return Refuse(energyKey, energyTemplate, RunJs.NumStr(price.Num(K.Cost)), RunJs.NumStr(c.Player.Num(K.Energy)));
            return null;
        }

        private Guard SwapGuards(CombatState c, string slotId, int setIndex)
        {
            var head = Head(c, RM.SwapNotYourTurn);
            if (head != null) return head;
            var allowed = Armoury.CanSwap(_d, slotId, true);
            if (!allowed.Ok) return Refuse(allowed);
            var price = PriceOn(c, c.Loadout, slotId, setIndex);
            return Afford(c, price, S.CombatRefusalNoSwapsLeft, RM.NoSwapsLeft, S.CombatRefusalSwapEnergy, RM.SwapCostsEnergy) ?? new Guard { Price = price };
        }

        private Guard ChangeGuards(CombatState c, string slotId, int setIndex, string pieceId)
        {
            var head = Head(c, RM.ChangeNotYourTurn);
            if (head != null) return head;
            var allowed = Armoury.CanEquip(_d, slotId, new EquipContext { InCombat = true });
            if (!allowed.Ok) return Refuse(allowed);
            var owned = Armoury.Ownership(_d, c.Loadout);
            var ctx = new EquipContext { InCombat = true, Attributes = c.Attributes, ItemUpgradeLevels = c.ItemUpgradeLevels, ClassId = c.Player.Str(K.ClassId) };
            var preview = RunJs.Clone(c.Loadout);
            if (!Armoury.EquipPiece(_d, preview, slotId, setIndex, pieceId, owned, ctx, null))
                return Refuse(S.CombatRefusalChangeUnavailable, RM.ChangeNotAvailable);
            var price = PriceOn(c, preview, slotId, setIndex);
            return Afford(c, price, S.CombatRefusalNoChangesLeft, RM.NoChangesLeft, S.CombatRefusalChangeEnergy, RM.ChangeCostsEnergy)
                   ?? new Guard { Price = price, PreviewLoadout = preview, Owned = owned, Context = ctx };
        }

        // ------------------------------------------------------------------ legality and prices (no mutation)

        public Refusal CheckSwap(CombatState c, string slotId, int setIndex)
        {
            var guard = SwapGuards(c, slotId, setIndex);
            if (guard.Refusal != null) return guard.Refusal.Refusal;
            // The ladder and the deck plan answer on a copy: cycleSet is the mutation's own gate.
            if (!Armoury.CycleSet(_d, RunJs.Clone(c.Loadout), slotId, setIndex, true, c.Player.Str(K.ClassId), null))
                return new Refusal(S.CombatRefusalNoSet, setIndex, slotId);
            return null;
        }

        public Refusal CheckChange(CombatState c, string slotId, int setIndex, string pieceId) =>
            ChangeGuards(c, slotId, setIndex, pieceId).Refusal?.Refusal;

        public JObject SwapPrice(CombatState c, string slotId, int setIndex)
        {
            if (Head(c, RM.SwapNotYourTurn) != null || !Armoury.CanSwap(_d, slotId, true).Ok) return null;
            return PriceOn(c, c.Loadout, slotId, setIndex);
        }

        public JObject ChangePrice(CombatState c, string slotId, int setIndex, string pieceId)
        {
            if (Head(c, RM.ChangeNotYourTurn) != null || !Armoury.CanEquip(_d, slotId, new EquipContext { InCombat = true }).Ok) return null;
            var preview = RunJs.Clone(c.Loadout);
            var ctx = new EquipContext { InCombat = true, Attributes = c.Attributes, ItemUpgradeLevels = c.ItemUpgradeLevels, ClassId = c.Player.Str(K.ClassId) };
            if (!Armoury.EquipPiece(_d, preview, slotId, setIndex, pieceId, Armoury.Ownership(_d, c.Loadout), ctx, null)) return null;
            return PriceOn(c, preview, slotId, setIndex);
        }

        // ------------------------------------------------------------------ the intents

        public void SwapArmament(CombatState c, string slotId, int setIndex)
        {
            var guard = SwapGuards(c, slotId, setIndex);
            if (guard.Refusal != null) throw new InvalidOperationException(guard.Refusal.Message);
            var p = c.Player;
            var classId = p.Str(K.ClassId);
            var activeBefore = c.Loadout.Obj(K.Active)?[slotId]?.DeepClone();
            var poolBefore = Loadout.RunMods(_d, c.Loadout, classId);
            if (!Armoury.CycleSet(_d, c.Loadout, slotId, setIndex, true, classId, payload => c.Emit(E.EquipmentChanged, payload)))
                throw new InvalidOperationException(RunJs.Fmt(RM.NoSetOnSlot, setIndex, slotId));
            var poolAfter = Loadout.RunMods(_d, c.Loadout, classId);
            Properties.SyncLoadout(c);
            if (!Equipment.StrictEquals(activeBefore, c.Loadout.Obj(K.Active)?[slotId]))
            {
                c.EquipmentChanged = true;
                MovePools(c, poolBefore, poolAfter);
            }
            Charge(c, guard.Price);
            Restamp(c);
            c.Emit(E.ArmamentSwapped, Js.Obj(K.SlotId, slotId, K.SetIndex, setIndex, K.Cost, guard.Price.Num(K.Cost), RK.Rule, guard.Price[RK.RuleId]));
            if (Cfg.Is(RK.SwapEndsTurn)) CombatEngine.EndTurn(c, new string[0]);
        }

        public void ChangeEquipment(CombatState c, string slotId, int setIndex, string pieceId)
        {
            var guard = ChangeGuards(c, slotId, setIndex, pieceId);
            if (guard.Refusal != null) throw new InvalidOperationException(guard.Refusal.Message);
            var p = c.Player;
            var classId = p.Str(K.ClassId);
            var poolBefore = Loadout.RunMods(_d, c.Loadout, classId);
            JObject changeEvent = null;
            if (!Armoury.EquipPiece(_d, c.Loadout, slotId, setIndex, pieceId, guard.Owned, guard.Context, payload => changeEvent = payload))
                throw new InvalidOperationException(RM.ChangeNoLongerAvailable);
            Properties.SyncLoadout(c);
            var poolAfter = Loadout.RunMods(_d, c.Loadout, classId);
            c.EquipmentChanged = true;
            MovePools(c, poolBefore, poolAfter);
            Charge(c, guard.Price);
            Restamp(c);
            if (changeEvent != null) c.Emit(E.EquipmentChanged, changeEvent);
            c.Emit(E.EquipmentRearmed, Js.Obj(K.SlotId, slotId, K.SetIndex, setIndex, K.PieceId, Js.S(pieceId), K.Cost, guard.Price.Num(K.Cost),
                RK.Rule, guard.Price[RK.RuleId]));
            if (Cfg.Is(RK.SwapEndsTurn)) CombatEngine.EndTurn(c, new string[0]);
        }

        /// <summary>Each pool maximum moves by what the equipment change moved it (HP never below one), carrying its deficit.</summary>
        private void MovePools(CombatState c, RunModsResult before, RunModsResult after)
        {
            var p = c.Player;
            var fields = Loadout.PoolFields(_d);
            var current = _d.RuleObj(K.Loadout, RK.PoolCurrent);
            foreach (var maxField in fields)
            {
                var currentField = current.Str(maxField);
                double floor = maxField == fields[0] ? 1 : 0;
                var nextMax = Math.Max(floor, p.Num(maxField) + after.Pool(maxField) - before.Pool(maxField));
                c.EquipmentPoolDeficits.Put(currentField, Loadout.MoveEquipmentPool(_d, p, maxField, nextMax, c.EquipmentPoolDeficits[currentField]));
            }
        }

        private void Charge(CombatState c, JObject price)
        {
            if (Allowance) c.SwapsLeft -= 1;
            else c.Player.Put(K.Energy, c.Player.Num(K.Energy) - price.Num(K.Cost));
        }

        /// <summary>
        /// The granted-card reconcile and the four pile restamps against a synthetic run (deck: [], the fight's loadout,
        /// attributes, levels, profile snapshot, birth quota, retired slots and item mounts), then the unrated Poise
        /// vessel and the ratings.
        /// </summary>
        private void Restamp(CombatState c)
        {
            var p = c.Player;
            var run = new JObject
            {
                [K.Deck] = new JArray(),
                [K.Loadout] = RunJs.Clone(c.Loadout),
                [RK.Class] = Js.S(p.Str(K.ClassId)),
            };
            void Put(string key, JToken value)
            {
                if (value != null) run[key] = value.DeepClone();
            }
            Put(K.Attributes, c.Attributes);
            Put(K.ItemUpgradeLevels, c.ItemUpgradeLevels);
            Put(K.EquipmentProfileRuleSnapshot, c.EquipmentProfileRuleSnapshot);
            Put(K.EquipmentAttackSlotCount, c.EquipmentAttackSlotCount);
            Put(K.RemovedAttackSlotIds, c.RemovedAttackSlotIds);
            Put(K.ItemMounts, c.ItemMounts);
            StartingDeck.ReconcileGrantedCardsInCombat(_d, run, c.Piles);
            foreach (var pile in new[] { c.Piles.Hand, c.Piles.Draw, c.Piles.Discard, c.Piles.Exhaust }) StartingDeck.StampPile(_d, run, pile);
            if (c.RatingsRules == null)
                CombatStart.StampPoiseMax(c.Data, p, Equipment.PoiseThreshold(c.Data, c.Loadout, p[K.RelicIds] ?? new JArray(), p.Str(K.ClassId),
                    c.ItemUpgradeLevels, c.Attributes, c.DerivedStatRuleSnapshot));
            Ratings.Refresh(c);
        }
    }
}
