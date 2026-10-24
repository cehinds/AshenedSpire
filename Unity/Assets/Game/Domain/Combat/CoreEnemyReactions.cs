using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using EK = Ashen.Generated.Core2EnemyKeys;
using EV = Ashen.Generated.Core2EnemyValues;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.Core2ReactionKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>One action of an enemy reaction's committed plan, as the stack holds it: the effect, the carrier it shares with its group, the meta without the group and the creatures it names by id.</summary>
    public sealed class PlanAction
    {
        public JObject Effect;
        public JObject Card;
        public ActionMeta Meta;
        public string SourceRef;
        public string OwnerRef;
        public string TargetRef;
    }

    /// <summary>The plan an enemy commits when it reacts (web plan { group, actions }): its group with the contacts already planned, and the actions that play them.</summary>
    public sealed class EnemyPlan
    {
        public ExpansionGroup Group;
        public List<PlanAction> Actions = new List<PlanAction>();

        public EnemyPlan CloneDeep()
        {
            var group = Group.CloneDeep();
            return new EnemyPlan
            {
                Group = group,
                Actions = Actions.Select(a => new PlanAction
                {
                    Effect = (JObject)a.Effect.DeepClone(), Card = group.Carrier, Meta = DeepMeta(a.Meta), SourceRef = a.SourceRef, OwnerRef = a.OwnerRef, TargetRef = a.TargetRef,
                }).ToList(),
            };
        }

        private static ActionMeta DeepMeta(ActionMeta meta)
        {
            var copy = meta.Clone();
            copy.ExpansionContacts = meta.ExpansionContacts?.Select(batch => batch.Select(contact => contact.Copy()).ToList()).ToList();
            copy.ExpansionTargets = meta.ExpansionTargets?.Select(batch => batch.ToList()).ToList();
            return copy;
        }
    }

    public sealed partial class ExpansionGroup
    {
        /// <summary>group.kind: the intent of the move a reaction plan commits (the stack entry reports it when the plan starts).</summary>
        public string Kind;

        /// <summary>The group as a private copy: what a candidate (an offer being checked) runs, so the live plan is never begun by it.</summary>
        public ExpansionGroup CloneDeep()
        {
            var copy = new ExpansionGroup
            {
                Key = Key, SourceId = SourceId, Carrier = (JObject)Carrier.DeepClone(), Begun = Begun, Cancelled = Cancelled, PayloadStarted = PayloadStarted, ContactTelegraphed = ContactTelegraphed,
                EnemyMoveStart = (JObject)EnemyMoveStart?.DeepClone(), ReactionAnswered = ReactionAnswered?.ToList(), ReactionNoticed = ReactionNoticed?.ToList(),
                ManualCounterSerials = ManualCounterSerials != null ? new Dictionary<string, double>(ManualCounterSerials, StringComparer.Ordinal) : null, EnemyWindows = EnemyWindows,
                CounterReturn = (JObject)CounterReturn?.DeepClone(), SmashPressureApplied = SmashPressureApplied?.ToList(), Kind = Kind, OwnPressure = (JObject)OwnPressure?.DeepClone(),
            };
            foreach (var pair in Targets.Entries())
                copy.Targets[pair.Key] = new TargetRow
                {
                    Id = pair.Value.Id, Contacts = pair.Value.Contacts.Select(contact => (JObject)contact.DeepClone()).ToList(),
                    Receipt = (JObject)pair.Value.Receipt?.DeepClone(), Interactions = (JObject)pair.Value.Interactions?.DeepClone(),
                };
            return copy;
        }
    }

    /// <summary>
    /// An enemy's reaction (web engine/enemyReactions.js pushEnemyReaction, relinkEnemyPlan, enemyEntryHalt, runEnemyPlan): the reaction move is planned at
    /// push as a committed action into a sink (nothing is queued, no critical roll) and stacked as <c>{ side: enemy, ownerId, enemyId, moveId,
    /// targetPlayerId, plan }</c>; the plan holds the creatures by id and is bound to the live (or candidate) fight when the stack reaches it. The state keeps a
    /// compact view of the plan (the contacts' numbers, the actions' effects and references); the plan itself is a runtime object beside it.
    /// </summary>
    public static class CoreEnemyReactions
    {
        private static string PlanKey(string ownerId, string groupKey) => ownerId + Ashen.Generated.Core2DamageValues.KeySeparator + groupKey;

        /// <summary>The runtime plan an entry of the stack stands for.</summary>
        public static EnemyPlan PlanOf(CombatState c, JObject entry, string groupKey) => c.EnemyPlans[PlanKey(entry.Str(DK.OwnerId), groupKey)];

        /// <summary>The compact view of a plan the state keeps (and the oracle records).</summary>
        public static JObject View(EnemyPlan plan)
        {
            var targets = new JObject();
            foreach (var pair in plan.Group.Targets.Entries())
                targets[pair.Key] = new JArray(pair.Value.Contacts.Select(contact => new JObject
                {
                    [CK.Amount] = contact[CK.Amount]?.DeepClone() ?? Js.Null(), [DK.Pressure] = contact[DK.Pressure]?.DeepClone() ?? Js.Null(), [DK.Effect] = contact[DK.Effect]?.DeepClone() ?? Js.Null(),
                }));
            return new JObject
            {
                [EK.PlanKind] = plan.Group.Kind != null ? (JToken)plan.Group.Kind : Js.Null(), [EK.PlanKey] = plan.Group.Key, [EK.PlanSourceId] = plan.Group.SourceId != null ? (JToken)plan.Group.SourceId : Js.Null(), [EK.PlanTargets] = targets,
                [EK.PlanActions] = new JArray(plan.Actions.Select(a => new JObject
                {
                    [DK.Effect] = a.Effect.DeepClone(), [EK.SourceRef] = Ref(a.SourceRef), [EK.OwnerRef] = Ref(a.OwnerRef), [EK.TargetRef] = Ref(a.TargetRef),
                    [K.MoveId] = Js.Truthy(Js.S(a.Meta?.MoveId)) ? (JToken)a.Meta.MoveId : Js.Null(),
                })),
            };
        }

        private static JToken Ref(string id) => id == null ? Js.Null() : new JObject { [K.Id] = id };

        private static JObject EntityOf(CombatState c, string id) => id == null ? null : id == c.Player.Str(K.Id) ? c.Player : c.Enemies.FirstOrDefault(enemy => enemy.Str(K.Id) == id);

        /// <summary>
        /// pushEnemyReaction(ctx, group, enemy, moveId, targetPlayerId): commit the reaction at push. Its actions are planned as a reaction carrier into a sink
        /// and stacked; the enemy has reacted this turn.
        /// </summary>
        public static JObject Push(CombatState c, ExpansionGroup group, JObject enemy, string moveId, string targetPlayerId)
        {
            var stack = c.Fields.Obj(DK.ReactionStack);
            if (stack == null) c.Fields[DK.ReactionStack] = stack = new JObject { [RK.GroupKey] = group.Key, [CK.Entries] = new JArray(), [RK.Resolving] = false };
            var entries = stack.Arr(CK.Entries);
            var sink = new List<CombatAction>();
            var pending = CoreExpansion.Pending(c);
            ExpansionGroup planned;
            var authored = c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Obj(K.Moves).Obj(moveId);
            var move = CoreIntents.Expanded(c, enemy, authored, moveId);
            var built = CoreIntents.Carrier(c, enemy, move, moveId);
            var actions = CoreEnemyMoves.Actions(c, enemy, move, moveId, built);
            var carrier = Js.Spread(built);
            carrier[DK.CombatReaction] = true;
            carrier[DK.ReactionDepth] = Js.N(entries.Count + 1);
            foreach (var action in actions) action.Card = carrier;
            c.EnqueueSink = sink;
            try { planned = CoreExpansion.Enqueue(c, actions, enemy, c.Player, carrier); }
            finally
            {
                c.EnqueueSink = null;
                c.Fields[DK.PendingExpansionActions] = Js.N(pending);
            }
            planned.Kind = move.Str(K.Intent);
            var plan = new EnemyPlan { Group = planned };
            foreach (var queued in sink)
            {
                var meta = queued.Meta.Clone();
                meta.Group = null;
                plan.Actions.Add(new PlanAction { Effect = queued.Effect, Card = queued.Card, Meta = meta, SourceRef = queued.Source?.Str(K.Id), OwnerRef = queued.Owner?.Str(K.Id), TargetRef = queued.Target?.Str(K.Id) });
            }
            c.EnemyPlans[PlanKey(enemy.Str(K.Id), group.Key)] = plan;
            var entry = new JObject
            {
                [RK.Side] = EV.SideEnemy, [DK.OwnerId] = enemy[K.Id].DeepClone(), [K.EnemyId] = enemy[K.EnemyId].DeepClone(), [K.MoveId] = moveId,
                [EK.TargetPlayerId] = targetPlayerId, [EK.Plan] = View(plan),
            };
            entries.Add(entry);
            enemy[RK.CombatReactionTurn] = Js.N(c.Turn);
            c.Emit(EV.EventCombatEnemyReactionStacked, Js.Obj(K.SourceId, enemy[K.Id], K.EnemyId, enemy[K.EnemyId], K.MoveId, moveId, EK.TargetPlayerId, targetPlayerId, RK.GroupKey, group.Key, RK.Depth, entries.Count));
            return entry;
        }

        /// <summary>relinkEnemyPlan(ctx, plan): the committed plan's actions bound to the creatures of this fight, all sharing one group.</summary>
        public static List<CombatAction> Relink(CombatState c, EnemyPlan plan) =>
            plan.Actions.Select(a =>
            {
                var meta = a.Meta.Clone();
                meta.Group = plan.Group;
                return new CombatAction { Effect = a.Effect, Source = EntityOf(c, a.SourceRef), Owner = EntityOf(c, a.OwnerRef), Target = EntityOf(c, a.TargetRef), Card = a.Card, Meta = meta };
            }).ToList();

        /// <summary>enemyEntryHalt(ctx, entry): why an entry fizzles when reached (its enemy is dead, staggered or locked, or its target is gone); null when it plays.</summary>
        public static string Halt(CombatState c, JObject entry)
        {
            var enemy = c.Enemies.FirstOrDefault(row => row.Str(K.Id) == entry.Str(DK.OwnerId));
            if (enemy == null || !enemy.Is(K.Alive) || enemy.Num(K.Hp) <= 0 || enemy.Is(K.SkipNextTurn) || CoreControl.Locked(c, enemy)) return EV.ReasonEnemyHalted;
            return CoreReactions.SeatGone(c) ? EV.ReasonTargetGone : null;
        }

        /// <summary>
        /// runEnemyPlan(ctx, entry): runs one enemy entry's committed plan in this fight (the live resolution or an offer check's candidate) on a queue of its
        /// own, as a reaction, so no window, rider roll or automatic Counter return chains from it. Returns the halt reason when it fizzled instead.
        /// </summary>
        public static string Run(CombatState c, JObject entry, EnemyPlan plan)
        {
            var halted = Halt(c, entry);
            if (halted != null) return halted;
            var saved = c.Queue.ToList();
            c.Queue.Clear();
            var wasPlaying = c.ReactionPlaying;
            c.ReactionPlaying = true;
            try
            {
                var enemy = c.Enemies.FirstOrDefault(row => row.Str(K.Id) == entry.Str(DK.OwnerId));
                if (enemy != null)
                {
                    var performed = enemy.Arr(DK.PerformedMoves);
                    if (performed == null) enemy[DK.PerformedMoves] = performed = new JArray();
                    performed.Add(entry[K.MoveId].DeepClone());
                }
                c.Emit(Ashen.Generated.CombatEvents.EnemyMoveStarted, Js.Obj(K.SourceId, entry[DK.OwnerId], K.EnemyId, entry[K.EnemyId], K.MoveId, entry[K.MoveId], K.Kind, plan.Group.Kind != null ? (JToken)plan.Group.Kind : Js.Null(), DK.Reaction, true));
                CoreExpansion.AddPending(c, 1);
                foreach (var action in Relink(c, plan)) c.Queue.AddLast(action);
                CoreActions.Drain(c);
            }
            finally
            {
                c.ReactionPlaying = wasPlaying;
                var left = c.Queue.ToList();
                c.Queue.Clear();
                if (c.Result == null)
                {
                    foreach (var action in left) c.Queue.AddLast(action);
                    foreach (var action in saved) c.Queue.AddLast(action);
                }
            }
            return null;
        }
    }
}
