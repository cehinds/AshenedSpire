using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DV = Ashen.Generated.Core2DamageValues;
using EK = Ashen.Generated.Core2EnemyKeys;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using Pr = Ashen.Generated.CombatPredicates;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The read-only action payload of an enemy's move (web engine/enemyActionPreview.js previewEnemyAction): the expanded move's opcodes with their formulas
    /// evaluated, the move's profile and tags, what it does while it charges and how long a delayed move takes. Nothing here moves the fight or draws on a
    /// stream: a random predicate belongs to execution, so a condition that depends on one is uncertain.
    /// </summary>
    public static class CoreEnemyPreview
    {
        /// <summary>previewPredicate(combat, predicate, action): true, false, or null when the condition depends on a roll.</summary>
        private static bool? Predicate(CombatState c, JObject predicate, CombatAction action)
        {
            var p = predicate.Str(K.P);
            if (p == Pr.Random) return null;
            if (p == Pr.Not)
            {
                var inner = Predicate(c, predicate.Obj(K.Pred), action);
                return inner == null ? (bool?)null : !inner.Value;
            }
            if (p == Pr.All || p == Pr.Any)
            {
                var decisive = p == Pr.Any;
                var uncertain = false;
                foreach (var child in Js.Items(predicate[K.Preds]).OfType<JObject>())
                {
                    var result = Predicate(c, child, action);
                    if (result == decisive) return decisive;
                    if (result == null) uncertain = true;
                }
                return uncertain ? (bool?)null : !decisive;
            }
            return Triggers.EvalPredicate(c, predicate, new PredicateContext { Owner = action.Owner, Source = action.Source, Target = action.Target, Card = action.Card, Meta = action.Meta });
        }

        private static JToken Resolve(JToken value, FormulaContext formulas)
        {
            if (value == null) return null;
            if (Formulas.IsFormula(value)) return Js.N(Formulas.Evaluate(value, formulas));
            if (value is JArray array) return new JArray(array.Select(item => Resolve(item, formulas)));
            if (value is JObject o)
            {
                var copy = new JObject();
                foreach (var p in o.Properties()) copy[p.Name] = Resolve(p.Value, formulas);
                return copy;
            }
            return value.DeepClone();
        }

        private static JObject Project(CombatState c, JObject enemy, JObject carrier, string moveId, JObject effect)
        {
            var target = effect.Str(K.Target) == V.TargetSelf || effect.Str(K.Target) == V.RefOwner ? enemy : c.Player;
            var action = new CombatAction { Source = enemy, Owner = enemy, Target = target, Card = carrier, Meta = new ActionMeta { MoveId = moveId } };
            var formulas = Actions.FormulaCtxFor(c, action, target);
            var result = (JObject)Resolve(effect, formulas);
            if (Js.Truthy(effect[K.If]))
            {
                var met = Predicate(c, effect.Obj(K.If), action);
                result[EK.ConditionMet] = met == null ? Js.Null() : (JToken)met.Value;
                if (met == null) result[EK.ConditionUncertain] = true;
            }
            return result;
        }

        /// <summary>The effects of one row of the move (the move itself, or what it does while it charges), in the order a move plays them.</summary>
        private static JArray Payload(CombatState c, JObject enemy, JObject carrier, string moveId, JObject row, bool primary)
        {
            var counter = carrier.Obj(CK.CombatProfile)?.Str(CK.Maneuver) == CV.StanceCounter;
            var primed = primary && counter && enemy.Obj(K.Intent)?.Str(K.MoveId) == moveId && enemy.Obj(K.Intent).Is(CK.CounterDefensePrimed);
            var rows = new List<JObject>();
            if (!Js.Nullish(row[K.Damage]) && !counter)
                rows.Add(Js.Obj(K.Op, Op.Damage, K.Target, V.RefPlayer, CK.Amount, CoreIntents.MoveDamage(enemy, row), K.Hits, !Js.Nullish(row[K.Hits]) ? row[K.Hits].DeepClone() : Js.N(1)));
            if (!Js.Nullish(row[K.Block]) && !primed) rows.Add(Js.Obj(K.Op, Op.Block, K.Target, V.TargetSelf, CK.Amount, row[K.Block].DeepClone()));
            if (!Js.Nullish(row[CK.Barrier]) && !primed) rows.Add(Js.Obj(K.Op, CV.OpGainBarrier, K.Target, V.TargetSelf, CK.Amount, row[CK.Barrier].DeepClone()));
            foreach (var effect in Js.Items(row[K.Effects]).OfType<JObject>())
                if (!counter || !(effect.Str(K.Op) == Op.Damage || effect.Str(K.Op) == Op.PoiseDamage || effect.Str(K.Op) == DV.OpWardDamage)) rows.Add(effect);
            return new JArray(rows.Select(effect => Project(c, enemy, carrier, moveId, effect)));
        }

        /// <summary>previewEnemyAction(combat, enemy, moveId): null for a move the enemy does not have.</summary>
        public static JObject Preview(CombatState c, JObject enemy, string moveId)
        {
            var authored = c.Data.Enemies.Get(enemy.Str(K.EnemyId))?.Obj(K.Moves)?.Obj(moveId);
            if (authored == null) return null;
            var move = CoreIntents.Expanded(c, enemy, authored, moveId);
            var carrier = CoreIntents.Carrier(c, enemy, authored, moveId);
            var charging = move.Obj(K.Delay)?.Obj(CK.WhileCharging);
            var result = new JObject
            {
                [K.MoveId] = moveId,
                [DK.Profile] = carrier.Obj(CK.CombatProfile).DeepClone(),
                [K.Tags] = new JArray(Js.Items(carrier[K.Tags]).Select(t => t.DeepClone())),
                [EK.ActionEffects] = Payload(c, enemy, carrier, moveId, move, true),
                [EK.ChargingEffects] = charging != null ? Payload(c, enemy, carrier, moveId, charging, false) : new JArray(),
            };
            if (Js.Truthy(move[K.Delay])) result[EK.DelayTurns] = !Js.Nullish(move.Obj(K.Delay)[K.Turns]) ? move.Obj(K.Delay)[K.Turns].DeepClone() : Js.N(1);
            return result;
        }
    }
}
