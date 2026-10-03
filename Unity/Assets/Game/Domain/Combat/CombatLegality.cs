using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using S = Ashen.Generated.CombatStringKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>Why an intent is refused: a string key and its format arguments (US-5.1: a refusal names what is missing).</summary>
    public sealed class Refusal
    {
        public Refusal(string key, params object[] args)
        {
            Key = key;
            Args = args ?? new object[0];
        }

        public string Key { get; }
        public object[] Args { get; }
    }

    /// <summary>
    /// The one legality service (US-5.1, US-5.9): the checks the engine makes before it pays anything, asked without
    /// mutating, so the UI can grey a card, list legal targets and say exactly why an action is refused. It mirrors
    /// the shipped doPlayCard / doEndTurn / doUseFlask / doSwapArmament / doChangeEquipment guards in the same order.
    /// </summary>
    public static class CombatLegality
    {
        public static bool NeedsEnemyTarget(JObject def) => Js.Items(def[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Target) == V.RefEnemy);

        /// <summary>Null when the card can be played at that target (or untargeted); otherwise the refusal.</summary>
        public static Refusal CanPlay(CombatState c, string cardInstanceId, string targetId = null)
        {
            if (c.Result != null) return new Refusal(S.CombatRefusalCombatOver);
            if (c.Phase != V.PhasePlayer) return new Refusal(S.CombatRefusalNotPlayerTurn);
            var inst = c.Piles.Hand.FirstOrDefault(card => card.Str(K.InstanceId) == cardInstanceId);
            if (inst == null) return new Refusal(S.CombatRefusalNotInHand);
            var def = Cards.Resolve(c.Data, inst);
            if (Framework.IsUnplayable(c.Data, def)) return new Refusal(S.CombatRefusalUnplayable, def.Str(K.Name));
            var p = c.Player;
            var isX = def.Str(K.Cost) == V.XCost;
            var cost = isX ? p.Num(K.Energy) : CombatEngine.EffectiveCost(c, def);
            var pools = Framework.Costs(c.Data, def, 0, Equipment.PlayerWeightClass(c).WeightClass);
            if (p.Num(K.Energy) < cost) return new Refusal(S.CombatRefusalEnergy, cost, p.Num(K.Energy));
            if (p.Num(K.Mana) < pools.Mana) return new Refusal(S.CombatRefusalMana, pools.Mana, p.Num(K.Mana));
            if (p.Num(K.Stamina) < pools.Stamina) return new Refusal(S.CombatRefusalStamina, pools.Stamina, p.Num(K.Stamina));
            if (targetId != null)
            {
                var target = Triggers.FindEntity(c, targetId);
                if (target == null || !target.Is(K.Alive)) return new Refusal(S.CombatRefusalInvalidTarget);
            }
            else if (NeedsEnemyTarget(def) && !c.Enemies.Any(e => e.Is(K.Alive))) return new Refusal(S.CombatRefusalNoTarget);
            return null;
        }

        /// <summary>The targets a card may be aimed at: living enemies when it targets an enemy, else none (it resolves itself).</summary>
        public static IReadOnlyList<string> Targets(CombatState c, string cardInstanceId)
        {
            var inst = c.Piles.Hand.FirstOrDefault(card => card.Str(K.InstanceId) == cardInstanceId);
            if (inst == null || !NeedsEnemyTarget(Cards.Resolve(c.Data, inst))) return new string[0];
            return c.Enemies.Where(e => e.Is(K.Alive)).Select(e => e.Str(K.Id)).ToList();
        }

        public static Refusal CanEndTurn(CombatState c, IReadOnlyList<string> discardIds)
        {
            if (c.Result != null) return new Refusal(S.CombatRefusalCombatOver);
            if (c.Phase != V.PhasePlayer) return new Refusal(S.CombatRefusalNotPlayerTurn);
            var plan = HandRules.Plan(c);
            var ids = discardIds ?? new string[0];
            var eligible = new HashSet<string>(plan.Cards.Select(card => card.Str(K.InstanceId)));
            if (ids.Distinct().Count() != ids.Count || ids.Any(id => !eligible.Contains(id)) || ids.Count > plan.Maximum || ids.Count < plan.Minimum)
                return new Refusal(S.CombatRefusalDiscardSelection, plan.Minimum, plan.Maximum);
            return null;
        }

        public static Refusal CanUseFlask(CombatState c, int slot, string chargeKind = null)
        {
            if (c.Result != null) return new Refusal(S.CombatRefusalCombatOver);
            if (c.Phase != V.PhasePlayer) return new Refusal(S.CombatRefusalNotPlayerTurn);
            var p = c.Player;
            if (chargeKind != null && Js.Includes(c.Data.Engine.Obj(K.Flasks)?[K.ChargeKinds], chargeKind))
            {
                var charges = p.Obj(K.FlaskCharges);
                if (charges == null || charges.Num(chargeKind + V.CurrentSuffix) <= 0) return new Refusal(S.CombatRefusalNoFlaskCharges);
                return null;
            }
            var flasks = p.Arr(K.Flasks);
            if (flasks == null || slot < 0 || slot >= flasks.Count || !(flasks[slot] is JObject)) return new Refusal(S.CombatRefusalNoFlask);
            return null;
        }

        /// <summary>
        /// Null when the hand can cycle to that set now (doSwapArmament's guards, the price and the ladder included);
        /// otherwise the refusal. Asked through the run's equipment port; combat data alone refuses.
        /// </summary>
        public static Refusal CanSwap(CombatState c, string slotId, int setIndex)
        {
            if (c.Result != null) return new Refusal(S.CombatRefusalCombatOver);
            var port = c.Data.EquipmentPort;
            return port == null ? new Refusal(S.CombatRefusalEquipmentDisabled) : port.CheckSwap(c, slotId, setIndex);
        }

        /// <summary>Null when that piece (or an empty hand, for a null piece) can go into that position now (doChangeEquipment's guards).</summary>
        public static Refusal CanChangeEquipment(CombatState c, string slotId, int setIndex, string pieceId)
        {
            if (c.Result != null) return new Refusal(S.CombatRefusalCombatOver);
            var port = c.Data.EquipmentPort;
            return port == null ? new Refusal(S.CombatRefusalEquipmentDisabled) : port.CheckChange(c, slotId, setIndex, pieceId);
        }

        /// <summary>The refusal for a command, whatever its type (null when legal).</summary>
        public static Refusal Check(CombatState c, CombatCommand command)
        {
            switch (command.Type)
            {
                case V.CommandPlayCard:
                    return CanPlay(c, command.CardInstanceId, command.TargetId);
                case V.CommandEndTurn:
                    return CanEndTurn(c, command.DiscardIds);
                case V.CommandUseFlask:
                    return CanUseFlask(c, command.Slot, command.ChargeKind);
                case V.CommandSwapArmament:
                    return CanSwap(c, command.SlotId, command.SetIndex);
                case V.CommandChangeEquipment:
                    return CanChangeEquipment(c, command.SlotId, command.SetIndex, command.PieceId);
                default:
                    return new Refusal(S.CombatRefusalInvalidTarget);
            }
        }
    }
}
