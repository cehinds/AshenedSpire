using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using V = Ashen.Generated.CombatValues;
using SK = Ashen.Generated.Core2StatusKeys;
using SV = Ashen.Generated.Core2StatusValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The predicates a Core 2 fight evaluates differently from the 405 rules or in addition to them (web engine/triggers.js evalPredicate): the
    /// ability-rider predicates (turn metrics, charges, explicit discards, a kill with a status, a new negative status, a card's kind and reach, the
    /// resources paid), stances held beside the main one, and the status, Block and Health reads that may look at the combatants as they were before
    /// the card being played (<c>snapshot</c>). Everything else is the shared evaluator's.
    /// </summary>
    public static class CoreTriggers
    {
        private static JObject Resolve(CombatState c, PredicateContext pctx, string of)
        {
            switch (of)
            {
                case V.RefPlayer: return c.Player;
                case V.RefSelf: return pctx.Source ?? pctx.Owner ?? c.Player;
                case V.RefOwner: return pctx.Owner ?? pctx.Source;
                case V.RefEnemy:
                case V.RefTarget: return pctx.Target;
                default: throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnresolvableRef, of));
            }
        }

        /// <summary>The snapshot the rider predicates read: the action's, else the card being played's (the events of its own play).</summary>
        private static JObject Before(CombatState c, PredicateContext pctx)
        {
            if (pctx.Meta?.Before != null) return pctx.Meta.Before;
            var ev = pctx.Event;
            return ev != null && Present(ev[SK.AbilityBefore]) ? Js.Obj(SK.AbilityBefore, ev[SK.AbilityBefore], SK.AbilityEntities, ev[SK.AbilityEntities]) : null;
        }

        private static JObject Prior(CombatState c, JObject entity, PredicateContext pctx)
        {
            var ev = pctx.Meta == null ? pctx.Event : null;
            return CoreRiders.PriorEntity(c, entity, Before(c, pctx), pctx.Meta?.TargetId ?? ev?.Str(K.TargetId), ev?.Obj(SK.TargetStatusesBefore));
        }

        private static bool Present(JToken t) => t != null && !Js.Nullish(t);

        /// <summary>Evaluates a predicate this class owns; false when the shared evaluator should answer.</summary>
        public static bool Eval(CombatState c, JObject pred, PredicateContext pctx, out bool value)
        {
            value = false;
            var ev = pctx.Event;
            switch (pred.Str(K.P))
            {
                case SV.TurnMetric:
                {
                    var owner = pctx.Owner ?? pctx.Source ?? c.Player;
                    var metric = (JObject)pred.DeepClone();
                    if (pred.Str(K.CardId) == DK.Event) metric[K.CardId] = Js.Truthy(ev?[K.CardId]) ? ev[K.CardId].DeepClone() : pctx.Card?[K.CardId]?.DeepClone();
                    var snapshot = pred.Str(Ashen.Generated.SessionKeys.Snapshot) == Ashen.Generated.RewardsKeys.Current ? null : Before(c, pctx)?.Obj(SK.AbilityBefore);
                    var amount = CoreRiders.Metric(owner, metric, snapshot);
                    value = (!Present(pred[K.AtLeast]) || amount >= pred.Num(K.AtLeast)) && (!Present(pred[SK.AtMost]) || amount <= pred.Num(SK.AtMost));
                    return true;
                }
                case SV.ChargeAvailable:
                    value = Js.Truthy((pctx.Owner ?? pctx.Source ?? c.Player)?.Obj(CK.AbilityRiders)?.Obj(DK.Charges)?[pred.Str(Ashen.Generated.Core2DeckKeys.Key) ?? string.Empty]);
                    return true;
                case SV.EventChargeConsumed:
                    value = ev?.Str(K.Type) == SV.CardChargeConsumed && Js.Includes(ev[SK.Keys], pred.Str(Ashen.Generated.Core2DeckKeys.Key));
                    return true;
                case SV.EventDiscardExplicit:
                    value = ev?.Str(K.Type) == E.CardDiscarded && ev.Is(SK.Explicit) && CoreSets.DiscardReasons.Contains(ev.Str(K.Reason));
                    return true;
                case SV.EnemyKilledWithStatus:
                {
                    var status = ev?.Obj(SK.TargetStatusesBefore)?.Obj(pred.Str(K.Status) ?? string.Empty);
                    var held = Present(status?.Obj(K.Meter)?[K.Value]) ? status.Obj(K.Meter).Num(K.Value) : Present(status?[K.Stacks]) ? status.Num(K.Stacks) : 0;
                    value = ev?.Str(K.Type) == E.EnemyDied && held > 0;
                    return true;
                }
                case SV.EnemyNegativeStatusNew:
                    value = ev?.Str(K.Type) == E.StatusApplied && ev.Str(CK.SourceKind) == V.Enemy && ev[CK.WasAbsent]?.Type == JTokenType.Boolean && ev.Is(CK.WasAbsent)
                        && Js.Includes(pred[K.Statuses], ev.Str(K.Status)) && ev.Str(K.TargetId) == pctx.Owner?.Str(K.Id);
                    return true;
                case SV.CardAbilityKindIs:
                    value = (Js.Truthy(pctx.Card?[DK.AbilityKind]) ? pctx.Card.Str(DK.AbilityKind) : ev?.Str(DK.AbilityKind)) == pred.Str(K.Kind);
                    return true;
                case SV.CardTargetsAllEnemies:
                {
                    if (Present(pctx.Card?[SK.TargetsAllEnemies])) { value = pctx.Card.Is(SK.TargetsAllEnemies); return true; }
                    var carried = ev?[SV.CardTargetsAllEnemies];
                    if (Present(carried)) { value = Js.Truthy(carried); return true; }
                    var id = Js.Truthy(pctx.Card?[K.CardId]) ? pctx.Card.Str(K.CardId) : ev?.Str(K.CardId);
                    value = id != null && CoreRiders.TargetsAllEnemies(c.Data.Cards.Get(id));
                    return true;
                }
                case SV.ResourcePaid:
                {
                    var paid = pred.Str(DK.Resource) == K.Mana ? pctx.Meta?.ManaSpent : pctx.Meta?.StaminaSpent;
                    value = (paid ?? 0) >= (Present(pred[K.Amount]) ? pred.Num(K.Amount) : 1);
                    return true;
                }
                case Ashen.Generated.CombatPredicates.InStance:
                {
                    var stance = pred.Str(K.Stance);
                    value = (c.Player.Str(K.StanceId) != null && c.Player.Str(K.StanceId) == stance)
                        || CoreDamage.ActiveCombatStances(c, c.Player).Any(row => row.Str(CK.Maneuver) == stance || (row.Str(CK.Maneuver) == Ashen.Generated.Core2CombatValues.StancePrepared && stance == Ashen.Generated.Core2CombatValues.ManeuverCasting));
                    return true;
                }
                case Ashen.Generated.Core2DamageValues.CondHasStatus:
                {
                    var atLeast = Present(pred[K.AtLeast]) ? pred.Num(K.AtLeast) : 1;
                    if (pred.Str(K.Of) == K.AllEnemies)
                    {
                        value = c.Enemies.Select(e => pred.Is(Ashen.Generated.SessionKeys.Snapshot) ? Prior(c, e, pctx) : e).Any(e => e.Is(K.Alive) && CoreStatuses.StacksWithStance(c, e, pred.Str(K.Status)) >= atLeast);
                        return true;
                    }
                    var current = Resolve(c, pctx, pred.Str(K.Of));
                    var entity = pred.Is(Ashen.Generated.SessionKeys.Snapshot) ? Prior(c, current, pctx) : current;
                    value = entity != null && CoreStatuses.StacksWithStance(c, entity, pred.Str(K.Status)) >= atLeast;
                    return true;
                }
                case Ashen.Generated.CombatPredicates.HasBlock:
                {
                    var current = Resolve(c, pctx, pred.Str(K.Of));
                    var entity = pred.Is(Ashen.Generated.SessionKeys.Snapshot) ? Prior(c, current, pctx) : current;
                    value = entity != null && entity.Num(K.Block) > 0;
                    return true;
                }
                case E.HpBelowPct:
                {
                    var current = Resolve(c, pctx, pred.Str(K.Of));
                    var entity = pred.Is(Ashen.Generated.SessionKeys.Snapshot) ? Prior(c, current, pctx) : current;
                    value = entity != null && entity.Num(K.Hp) <= entity.Num(K.MaxHp) * pred.Num(K.Pct) / Ashen.Generated.CombatMath.Percent;
                    return true;
                }
                default:
                    return false;
            }
        }
    }
}
