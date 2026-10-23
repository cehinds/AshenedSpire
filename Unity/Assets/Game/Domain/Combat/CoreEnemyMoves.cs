using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// An enemy plays a move (web engine/combat.js enemyPhase, executeMovePayload, enemyMoveActions, enemyHalted, perceptionRead): it pays the
    /// move's Stamina (a move it cannot afford is told so and not played), plans the move's damage, Block and effects as one committed action
    /// against the player, and the queue resolves it hit by hit. A Counter move arms its reply and plays only its support (the reply is
    /// <see cref="CoreCounterReturn"/>). A move that charges for a later turn, a burden and a move that needs the enemy to close the distance are the later
    /// packages' (WP-9, WP-11, WP-12): each is refused by name.
    /// </summary>
    public static class CoreEnemyMoves
    {
        /// <summary>enemyHalted(combat, enemy): a staggered, skipping or locked enemy plays nothing more this turn.</summary>
        public static bool Halted(CombatState c, JObject enemy) => enemy.Is(K.SkipNextTurn) || CoreStatuses.Flag(c, enemy, K.SkipTurn) || CoreControl.Locked(c, enemy);

        /// <summary>perceptionRead(combat, enemy): whether the player reads the enemy's telegraph exactly.</summary>
        public static bool PerceptionRead(CombatState c, JObject enemy) =>
            c.Fields[CK.EnemyKnowledge] != null
                ? enemy.Obj(CK.KnowledgeAction)?.Obj(CK.Reads)?.Obj(DV.PlayerKey)?.Str(CK.Visibility) == DV.VisibilityExact
                : true;

        /// <summary>enemyMoveActions(combat, enemy, move, moveId, carrier): the move's damage, Block, Barrier and effects, in that order.</summary>
        public static List<CombatAction> Actions(CombatState c, JObject enemy, JObject move, string moveId, JObject carrier)
        {
            var actions = new List<CombatAction>();
            var counter = carrier.Obj(CK.CombatProfile)?.Str(CK.Maneuver) == CV.StanceCounter;
            var primed = enemy.Obj(K.Intent)?.Str(K.MoveId) == moveId && enemy.Obj(K.Intent).Is(CK.CounterDefensePrimed);
            var player = c.Player;
            if (!Js.Nullish(move[K.Damage]) && !counter)
            {
                var damage = Js.Obj(K.Op, Op.Damage, K.Target, V.RefPlayer, CK.Amount, CoreIntents.MoveDamage(enemy, move).DeepClone(), K.Hits, !Js.Nullish(move[K.Hits]) ? move[K.Hits].DeepClone() : Js.N(1));
                if (Js.Truthy(move[CK.DamageSchool])) damage[CK.DamageSchool] = move[CK.DamageSchool].DeepClone();
                actions.Add(new CombatAction { Effect = damage, Source = enemy, Owner = enemy, Target = player, Card = carrier, Meta = new ActionMeta { MoveId = moveId } });
            }
            if (!Js.Nullish(move[K.Block]) && !(counter && primed))
                actions.Add(new CombatAction { Effect = Js.Obj(K.Op, Op.Block, K.Target, V.TargetSelf, CK.Amount, move[K.Block].DeepClone()), Source = enemy, Owner = enemy, Target = player, Card = carrier, Meta = new ActionMeta { MoveId = moveId } });
            if (!Js.Nullish(move[CK.Barrier]) && !(counter && primed))
                actions.Add(new CombatAction { Effect = Js.Obj(K.Op, CV.OpGainBarrier, K.Target, V.TargetSelf, CK.Amount, move[CK.Barrier].DeepClone()), Source = enemy, Owner = enemy, Target = enemy, Card = carrier, Meta = new ActionMeta { MoveId = moveId } });
            foreach (var eff in Js.Items(move[K.Effects]).OfType<JObject>())
                if (!counter || !(eff.Str(K.Op) == Op.Damage || eff.Str(K.Op) == Op.PoiseDamage || eff.Str(K.Op) == DV.OpWardDamage))
                    actions.Add(new CombatAction { Effect = eff, Source = enemy, Owner = enemy, Target = player, Card = carrier, Meta = new ActionMeta { MoveId = moveId } });
            return actions;
        }

        /// <summary>
        /// startDelayedMove(combat, enemy, move, moveId): the commit turn of a delayed move. The telegraph stays and what the move does while it charges
        /// (Block, Barrier, effects) happens now; the real payload resolves on a later enemy turn.
        /// </summary>
        public static void StartDelayed(CombatState c, JObject enemy, JObject move, string moveId)
        {
            var charging = move.Obj(K.Delay)?.Obj(CK.WhileCharging) ?? new JObject();
            var carrier = CoreIntents.Carrier(c, enemy, move, moveId);
            if (!Js.Nullish(charging[K.Block]))
                c.Enqueue(new CombatAction { Effect = Js.Obj(K.Op, Op.Block, K.Target, V.TargetSelf, CK.Amount, charging[K.Block].DeepClone()), Source = enemy, Owner = enemy, Target = c.Player, Card = carrier, Meta = new ActionMeta { Preparing = true } });
            if (!Js.Nullish(charging[CK.Barrier]))
                c.Enqueue(new CombatAction { Effect = Js.Obj(K.Op, CV.OpGainBarrier, K.Target, V.TargetSelf, CK.Amount, charging[CK.Barrier].DeepClone()), Source = enemy, Owner = enemy, Target = enemy, Card = carrier, Meta = new ActionMeta { MoveId = moveId, Preparing = true } });
            var actions = Js.Items(charging[K.Effects]).OfType<JObject>()
                .Select(effect => new CombatAction { Effect = effect, Source = enemy, Owner = enemy, Target = c.Player, Card = carrier, Meta = new ActionMeta { MoveId = moveId, Preparing = true } }).ToList();
            // Keep charging damage, statuses and generated pressure on one hit receipt.
            if (actions.Count > 0) CoreExpansion.Enqueue(c, actions, enemy, c.Player, carrier);
            enemy[K.PendingMove] = Js.Obj(K.MoveId, moveId, K.ResolveOnTurn, c.Turn + (!Js.Nullish(move.Obj(K.Delay)[K.Turns]) ? move.Obj(K.Delay).Num(K.Turns) : 1));
            if (enemy.Obj(K.Intent)?.Str(K.MoveId) == moveId) { var intent = Js.Spread(enemy.Obj(K.Intent)); intent[K.Pending] = true; enemy[K.Intent] = intent; }
        }

        /// <summary>executeMovePayload(combat, enemy, move, moveId): the move is paid for and planned as one committed action. Returns "refused" when the enemy cannot afford it.</summary>
        public static string Execute(CombatState c, JObject enemy, JObject move, string moveId)
        {
            var spCost = CoreIntents.MoveSp(c, move, enemy, moveId);
            move = CoreIntents.Expanded(c, enemy, move, moveId);
            var carrier = CoreIntents.Carrier(c, enemy, move, moveId);
            var actions = Actions(c, enemy, move, moveId, carrier);
            var rangeQuote = CoreRange.Quote(c, enemy, actions, DV.RouteAdvance, true);
            var advance = rangeQuote?.Num(DK.AdvanceSp) ?? 0;
            var totalSp = spCost + advance;
            if (CoreFixCore.Enabled(c))
            {
                var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                var available = Js.IsNum(enemy[CK.FixCoreStaminaRemaining]) ? enemy.Num(CK.FixCoreStaminaRemaining) : Js.IsNum(enemy[K.MaxStamina]) ? enemy.Num(K.MaxStamina) : CoreIntents.Pool(c, def);
                if (totalSp > available && advance > 0) throw new NotSupportedException(DM.RangeAdvanceNotPorted);
                if (totalSp > available)
                {
                    c.Emit(DK.EnemyMoveUnavailable, Js.Obj(K.SourceId, enemy[K.Id], K.MoveId, moveId, DK.Required, totalSp, DK.Available, available, DK.Resource, DV.ResourceSp));
                    return DV.OutcomeRefused;
                }
                enemy.Put(CK.FixCoreStaminaRemaining, available - spCost);
                enemy.Put(K.Stamina, available - spCost);
            }
            if (!CoreBurdens.AllowMove(c, enemy, spCost, move)) return K.Blocked;
            var start = Js.Obj(K.SourceId, enemy[K.Id], K.EnemyId, enemy[K.EnemyId], K.MoveId, moveId, K.Kind, move[K.Intent],
                DK.ActionPreview, Js.Spread(CoreEnemyPreview.Preview(c, enemy, moveId), Js.Obj(DK.SpCost, spCost, DK.TotalSp, totalSp)));
            carrier[DK.RangeQuotedPlan] = rangeQuote;
            carrier[DK.RangeBaseCosts] = new JObject { [K.Energy] = Js.N(spCost), [K.Stamina] = Js.N(spCost), [K.Mana] = 0 };
            var group = CoreExpansion.Enqueue(c, actions, enemy, c.Player, carrier);
            if (group != null) group.EnemyMoveStart = start;
            return null;
        }
    }
}
