using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using E = Ashen.Generated.CombatEvents;
using HK = Ashen.Generated.Core2HitKeys;
using HV = Ashen.Generated.Core2HitValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What a Core 2 fight does to an event on its way through the bus (web engine/triggers.js emitEvent): the action that caused it names
    /// itself in the payload, the ability riders count the Health an entity lost, and the enemy knowledge settles the telegraphed action when the
    /// move it announced starts, or never will (the enemy fell or was staggered).
    /// </summary>
    public static class CoreEvents
    {
        /// <summary>The payload with the executing action's card, source and move laid under it (<c>{ cardId, ..., ...payload }</c>).</summary>
        public static JObject Decorate(CombatState c, string type, JObject payload)
        {
            var action = c.CurrentAction;
            if (action == null) return payload;
            var result = new JObject();
            if (action.Card != null)
            {
                var card = action.Card;
                Put(result, K.CardId, card[K.CardId]);
                Put(result, DK.CardInstanceId, card[K.InstanceId]);
                Put(result, DK.CardType, card[K.Type]);
                result[DK.CardTags] = (card[DK.AuthoredTags] is JToken authored && Js.Truthy(authored) ? authored : card[K.Tags] is JArray ? card[K.Tags] : new JArray()).DeepClone();
                Put(result, DK.AbilityKind, card[DK.AbilityKind]);
                Put(result, K.SourceId, action.Source?[K.Id]);
                Put(result, DK.SourceKind, action.Source?[K.Kind]);
            }
            foreach (var p in (payload ?? new JObject()).Properties()) result[p.Name] = p.Value;
            if (action.Meta?.Preparing == true) { if (result[DK.Preparing] == null) result[DK.Preparing] = true; }
            else if (Js.Truthy(action.Meta?.MoveId) && result[K.MoveId] == null) result[K.MoveId] = action.Meta.MoveId;
            return result;
        }

        private static void Put(JObject o, string key, JToken value)
        {
            if (value != null) o[key] = value.DeepClone();
        }

        public static JObject RidersOf(JObject entity)
        {
            if (entity[CK.AbilityRiders] is JObject existing) return existing;
            return (JObject)(entity[CK.AbilityRiders] = new JObject
            {
                [DK.CardsPlayed] = 0, [DK.TagPlays] = new JObject(), [DK.DistinctTagPlays] = new JObject(), [DK.ManaSpent] = 0,
                [DK.Discarded] = 0, [DK.HpLostSinceTurnStart] = 0, [DK.HpLostThisRound] = 0, [DK.OfferingsPaid] = 0, [DK.PreviousSpell] = 0, [DK.TurnStartHpPct] = Ashen.Generated.CombatMath.Percent,
                [DK.Charges] = new JObject(), [DK.Once] = new JObject(),
            });
        }

        /// <summary>recordAbilityEvent(ctx, type, payload): Health lost counts toward the turn and the round; a chosen or random discard counts.</summary>
        public static void RecordAbility(CombatState c, string type, JObject ev)
        {
            if (type == E.HpLost)
            {
                var id = ev.Str(K.TargetId);
                var entity = id == c.Player.Str(K.Id) ? c.Player : c.Enemies.FirstOrDefault(e => e.Str(K.Id) == id);
                if (entity != null)
                {
                    var state = RidersOf(entity);
                    var amount = Math.Max(0, Js.Or0(ev[K.Amount]));
                    state.Put(DK.HpLostSinceTurnStart, state.Num(DK.HpLostSinceTurnStart) + amount);
                    state.Put(DK.HpLostThisRound, state.Num(DK.HpLostThisRound) + amount);
                }
            }
            if (type == DK.CardDiscarded && CoreSets.DiscardReasons.Contains(ev.Str(K.Reason)))
            {
                var riders = RidersOf(c.Player);
                riders.Put(DK.Discarded, riders.Num(DK.Discarded) + 1);
            }
        }

        /// <summary>
        /// creditKnowledgeResponse for an evade: a player who failed to read the telegraph is paid Perception when its Evade answers that action, and a
        /// bankable encounter keeps the counter, which is the run's (WP-11) and refused by name.
        /// </summary>
        private static void CreditResponse(CombatState c, JObject ev)
        {
            var state = c.Fields.Obj(CK.EnemyKnowledge);
            var ownerId = V.Player;
            var enemy = c.Enemies.FirstOrDefault(e => e.Str(K.Id) == ev.Str(K.SourceId));
            var owner = state?.Obj(DK.Owners)?.Obj(ownerId);
            var action = enemy?.Obj(CK.KnowledgeAction);
            var read = action?.Obj(CK.Reads)?.Obj(ownerId);
            if (read == null || owner == null || action.Is(CK.Cancelled) || !JToken.DeepEquals(ev[HK.EnemyActionSerial] ?? JValue.CreateNull(), action[CK.Serial] ?? JValue.CreateNull())) return;
            CoreKnowledge.PayPerception(c, enemy, ownerId, DV.ReasonResponse);
            if (state.Is(DK.Bankable) && state.Is(DK.Encounter) && !Js.Includes(owner[DK.CounterEnemyIds], enemy.Str(K.EnemyId))) throw new NotSupportedException(DM.KnowledgeCreditNotPorted);
        }

        /// <summary>recordKnowledgeEvent(combat, event): the announced move starts, or the enemy that announced it is gone.</summary>
        public static void RecordKnowledge(CombatState c, JObject ev)
        {
            if (c.Fields[CK.EnemyKnowledge] == null) return;
            var type = ev.Str(K.Type);
            if (type == E.EnemyMoveStarted && !ev.Is(DK.Reaction)) CoreKnowledge.Resolve(c, c.Enemies.FirstOrDefault(e => e.Str(K.Id) == ev.Str(K.SourceId)));
            if (type == HV.EventAttackMissed && ev.Str(HK.MissReason) == HV.MissAccuracy && (ev.Is(DK.EvadeActive) || ev.Num(DK.EvadeStacks) > 0) && ev.Str(K.TargetId) == c.Player.Str(K.Id))
                CreditResponse(c, ev);
            if (type == E.EnemyDied || type == E.EnemyStaggered)
            {
                var id = ev.Str(K.TargetId) ?? ev.Str(DK.EnemyInstanceId) ?? ev.Str(K.EnemyId);
                CoreKnowledge.Cancel(c, c.Enemies.FirstOrDefault(e => e.Str(K.Id) == id));
            }
        }
    }
}
