using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using NK = Ashen.Generated.Core2SnapshotKeys;
using NV = Ashen.Generated.Core2SnapshotValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The queued work of a paused Core 2 fight in the web's snapshot shape (engine/combatSnapshot.js <c>abilityQueue</c>, <c>reactionResume.queue</c> and an
    /// enemy entry's <c>plan</c>): an action is <c>{ effect, card, meta, sourceId, ownerId, targetId }</c>, its meta names the committed action it belongs to
    /// (<c>expansionGroup</c>) and the contacts that group planned, and the groups are plain data (targets, contacts, receipts, the per-hit sequence). The
    /// runtime holds the same things as objects (<see cref="CombatAction"/>, <see cref="ActionMeta"/>, <see cref="ExpansionGroup"/>); this class is the one
    /// place that turns one into the other. A group that several actions share is written once per action and joined again by its key, as the web joins it.
    /// </summary>
    public static class CoreSnapshotActions
    {
        private static JToken Clone(JToken token) => token?.DeepClone();

        private static void Put(JObject o, string key, JToken value)
        {
            if (value != null) o[key] = value.DeepClone();
        }

        private static void Put(JObject o, string key, double? value)
        {
            if (value.HasValue) o[key] = Js.N(value.Value);
        }

        // ------------------------------------------------------------------ an action

        /// <summary>The action as the web saves it: the creatures by id, the group inside the meta.</summary>
        public static JObject ActionToJson(CombatAction action, bool withGroup = true)
        {
            var o = new JObject { [NK.Effect] = Clone(action.Effect) };
            if (action.Card != null) o[K.Card] = Clone(action.Card);
            o[NK.Meta] = MetaToJson(action.Meta, withGroup);
            // The web writes the creatures' ids as they are: an absent creature is no key.
            if (action.Source != null) o[NK.SourceId] = action.Source.Str(K.Id);
            if (action.Owner != null) o[NK.OwnerId] = action.Owner.Str(K.Id);
            if (action.Target != null) o[NK.TargetId] = action.Target.Str(K.Id);
            return o;
        }

        /// <summary>The action from its saved shape; <paramref name="groups"/> joins the actions of one committed action to one group.</summary>
        public static CombatAction ActionFromJson(CombatState c, JObject saved, Dictionary<string, ExpansionGroup> groups)
        {
            JObject Entity(string key)
            {
                var id = saved.Str(key);
                if (id == null) return null;
                return id == c.Player.Str(K.Id) ? c.Player : c.Enemies.FirstOrDefault(e => e.Str(K.Id) == id);
            }
            var action = new CombatAction
            {
                Effect = (JObject)saved[NK.Effect].DeepClone(),
                Card = saved[K.Card] is JObject card ? (JObject)card.DeepClone() : null,
                Source = Entity(NK.SourceId), Owner = Entity(NK.OwnerId), Target = Entity(NK.TargetId),
                Meta = MetaFromJson(saved.Obj(NK.Meta), groups),
            };
            // The web hands the group's carrier to every action of the committed action; a copy that still equals it is that carrier again.
            var group = action.Meta.Group;
            if (group != null && action.Card != null && JToken.DeepEquals(action.Card, group.Carrier)) action.Card = group.Carrier;
            return action;
        }

        // ------------------------------------------------------------------ the meta

        private static JObject MetaToJson(ActionMeta m, bool withGroup)
        {
            var o = new JObject();
            if (m == null) return o;
            Put(o, NK.EnergySpent, m.EnergySpent);
            Put(o, NK.ManaSpent, m.ManaSpent);
            Put(o, NK.StaminaSpent, m.StaminaSpent);
            Put(o, NK.OrdinalThisTurn, m.OrdinalThisTurn);
            Put(o, NK.OrdinalThisCombat, m.OrdinalThisCombat);
            Put(o, NK.AttackOrdinal, m.AttackOrdinal);
            if (m.MoveId != null) o[NK.MoveId] = m.MoveId;
            if (m.Preparing) o[NK.Preparing] = true;
            if (m.StanceEntry) o[NK.StanceEntry] = true;
            if (m.CombatCounterReaction) o[NK.CombatCounterReaction] = true;
            if (m.CombatCounterInterrupted) o[NK.CombatCounterInterrupted] = true;
            Put(o, NK.AmountMult, m.AmountMult);
            Put(o, NK.Event, m.Event);
            if (m.ProposalReactive) o[NK.ProposalReactive] = true;
            if (m.Direct.HasValue) o[NK.Direct] = m.Direct.Value;
            if (m.ExpansionContacts != null) o[NK.ExpansionContacts] = new JArray(m.ExpansionContacts.Select(batch => (JToken)new JArray(batch.Select(ContactRefToJson))));
            if (m.ExpansionTargets != null) o[NK.ExpansionTargets] = new JArray(m.ExpansionTargets.Select(batch => (JToken)new JArray(batch.Select(id => (JToken)new JObject { [K.Id] = id }))));
            if (m.ExpansionCursor != 0) o[NK.ExpansionCursor] = m.ExpansionCursor;
            if (m.StatusTargetCursor != 0) o[NK.StatusTargetCursor] = m.StatusTargetCursor;
            if (m.ContactIndex.HasValue) o[NK.ContactIndex] = m.ContactIndex.Value;
            if (m.ContactIndex.HasValue) o[NK.ContactEffectIndex] = m.ContactEffectIndex;
            if (m.ContactEffectFirst.HasValue) o[NK.ContactEffectFirst] = m.ContactEffectFirst.Value;
            if (m.ContactCompletion) o[NK.ContactCompletion] = true;
            if (m.RangeTargetBatches != null) o[NK.RangeTargetBatches] = new JArray(m.RangeTargetBatches.Select(batch => (JToken)new JArray(batch.Select(id => (JToken)new JObject { [K.Id] = id }))));
            if (m.AbilityEffectIndex.HasValue) o[NK.AbilityEffectIndex] = m.AbilityEffectIndex.Value;
            Put(o, NK.ProposalTargetDecisions, m.ProposalTargetDecisions);
            Put(o, NK.KillReceipt, m.KillReceipt);
            Put(o, NK.EnemyKnowledgeActionSerial, m.EnemyKnowledgeActionSerial);
            Put(o, NK.Choice, m.Choice);
            if (m.LinkedDepth != 0) o[NK.LinkedDepth] = m.LinkedDepth;
            if (m.Charge != null)
                foreach (var p in m.Charge.Properties()) o[NV.ChargePrefix + char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1)] = p.Value.DeepClone();
            if (m.Before != null) foreach (var p in BeforeToJson(m.Before).Properties()) o[p.Name] = p.Value;
            if (m.TargetId != null) o[NK.TargetId] = m.TargetId;
            if (m.ExpansionExecutionRepeat.HasValue) o[NK.ExpansionExecutionRepeat] = m.ExpansionExecutionRepeat.Value;
            if (m.InheritedContact != null) o[NK.ExpansionContact] = new JObject { [NK.Receipt] = Clone(m.InheritedContact.Receipt), [NK.Index] = m.InheritedContact.Index };
            if (m.CardInstanceId != null) o[NK.CardInstanceId] = m.CardInstanceId;
            if (m.CardId != null) o[NK.CardId] = m.CardId;
            if (m.Trigger != null) o[NK.Trigger] = m.Trigger;
            Put(o, NK.TriggeredBy, m.TriggeredBy);
            Put(o, NK.RangeSiteBatches, m.RangeSiteBatches);
            if (m.RangeHitIndex.HasValue) o[NK.RangeHitIndex] = m.RangeHitIndex.Value;
            if (m.RangeEffectIndex.HasValue) o[NK.RangeEffectIndex] = m.RangeEffectIndex.Value;
            if (m.StanceRestore) o[NK.StanceRestore] = true;
            if (withGroup && m.Group != null) o[NK.ExpansionGroup] = GroupToJson(m.Group);
            return o;
        }

        private static JObject ContactRefToJson(ContactRef r)
        {
            var o = new JObject { [NK.Key] = r.Key, [NK.Index] = r.Index, [K.Id] = r.Id };
            if (r.Crit.HasValue) o[NK.Crit] = Js.N(r.Crit.Value);
            return o;
        }

        private static ActionMeta MetaFromJson(JObject o, Dictionary<string, ExpansionGroup> groups)
        {
            var m = new ActionMeta();
            if (o == null) return m;
            double? D(string key) => Js.IsNum(o[key]) ? Js.D(o[key]) : (double?)null;
            int? I(string key) => Js.IsNum(o[key]) ? (int)Js.D(o[key]) : (int?)null;
            JObject Obj(string key) => o[key] is JObject x ? (JObject)x.DeepClone() : null;
            m.EnergySpent = D(NK.EnergySpent);
            m.ManaSpent = D(NK.ManaSpent);
            m.StaminaSpent = D(NK.StaminaSpent);
            m.OrdinalThisTurn = D(NK.OrdinalThisTurn);
            m.OrdinalThisCombat = D(NK.OrdinalThisCombat);
            m.AttackOrdinal = D(NK.AttackOrdinal);
            m.MoveId = o.Str(NK.MoveId);
            m.Preparing = o.Is(NK.Preparing);
            m.StanceEntry = o.Is(NK.StanceEntry);
            m.CombatCounterReaction = o.Is(NK.CombatCounterReaction);
            m.CombatCounterInterrupted = o.Is(NK.CombatCounterInterrupted);
            m.AmountMult = D(NK.AmountMult);
            m.Event = Obj(NK.Event);
            m.ProposalReactive = o.Is(NK.ProposalReactive);
            if (o[NK.Direct] != null) m.Direct = o.Is(NK.Direct);
            if (o[NK.ExpansionContacts] is JArray contacts)
                m.ExpansionContacts = contacts.Select(batch => ((JArray)batch).OfType<JObject>().Select(ContactRefFromJson).ToList()).ToList();
            if (o[NK.ExpansionTargets] is JArray targets)
                m.ExpansionTargets = targets.Select(batch => ((JArray)batch).OfType<JObject>().Select(r => r.Str(K.Id)).ToList()).ToList();
            m.ExpansionCursor = I(NK.ExpansionCursor) ?? 0;
            m.StatusTargetCursor = I(NK.StatusTargetCursor) ?? 0;
            m.ContactIndex = I(NK.ContactIndex);
            m.ContactEffectIndex = I(NK.ContactEffectIndex) ?? 0;
            if (o[NK.ContactEffectFirst] != null) m.ContactEffectFirst = o.Is(NK.ContactEffectFirst);
            m.ContactCompletion = o.Is(NK.ContactCompletion);
            if (o[NK.RangeTargetBatches] is JArray batches) m.RangeTargetBatches = batches.Select(batch => ((JArray)batch).Select(id => id is JObject r ? r.Str(K.Id) : Js.Str(id)).ToList()).ToList();
            m.AbilityEffectIndex = I(NK.AbilityEffectIndex);
            m.ProposalTargetDecisions = Obj(NK.ProposalTargetDecisions);
            m.KillReceipt = Obj(NK.KillReceipt);
            m.EnemyKnowledgeActionSerial = Clone(o[NK.EnemyKnowledgeActionSerial]);
            m.Choice = Clone(o[NK.Choice]);
            m.LinkedDepth = I(NK.LinkedDepth) ?? 0;
            foreach (var p in o.Properties())
            {
                if (p.Name.StartsWith(NV.ChargePrefix, StringComparison.Ordinal) && p.Name.Length > NV.ChargePrefix.Length)
                    (m.Charge ??= new JObject())[char.ToLowerInvariant(p.Name[NV.ChargePrefix.Length]) + p.Name.Substring(NV.ChargePrefix.Length + 1)] = p.Value.DeepClone();
                else if (BeforeKeys.Contains(p.Name)) (m.Before ??= new JObject())[p.Name] = p.Value.DeepClone();
            }
            if (m.Before != null) m.Before = BeforeFromJson(m.Before);
            m.TargetId = o.Str(NK.TargetId);
            m.ExpansionExecutionRepeat = I(NK.ExpansionExecutionRepeat);
            if (o[NK.ExpansionContact] is JObject inherited) m.InheritedContact = new CoreImpact.Contact { Receipt = inherited[NK.Receipt] as JObject, Index = (int)inherited.Num(NK.Index) };
            m.CardInstanceId = o.Str(NK.CardInstanceId);
            m.CardId = o.Str(NK.CardId);
            m.Trigger = o.Str(NK.Trigger);
            m.TriggeredBy = Obj(NK.TriggeredBy);
            m.RangeSiteBatches = o[NK.RangeSiteBatches] is JArray sites ? (JArray)sites.DeepClone() : null;
            m.RangeHitIndex = I(NK.RangeHitIndex);
            m.RangeEffectIndex = I(NK.RangeEffectIndex);
            m.StanceRestore = o.Is(NK.StanceRestore);
            if (o[NK.ExpansionGroup] is JObject group)
            {
                var key = group.Str(NK.Key);
                if (!groups.TryGetValue(key, out var joined)) groups[key] = joined = GroupFromJson(group);
                m.Group = joined;
            }
            return m;
        }

        private static readonly HashSet<string> BeforeKeys = new HashSet<string>(StringComparer.Ordinal) { NK.AbilityBefore, NK.AbilityEntities, NK.CardTargetsAllEnemies };

        /// <summary>The snapshot taken before a play, as the web holds it: its player carries the Stamina under the energy names too, wherever the entity is copied (bindTurnStamina).</summary>
        public static JObject BeforeToJson(JObject before)
        {
            var copy = (JObject)before.DeepClone();
            foreach (var entity in PlayersOf(copy))
            {
                entity[K.Energy] = entity[K.Stamina]?.DeepClone();
                entity[K.EnergyMax] = entity[K.MaxStamina]?.DeepClone();
            }
            return copy;
        }

        public static JObject BeforeFromJson(JObject before)
        {
            foreach (var entity in PlayersOf(before))
            {
                entity.Remove(K.Energy);
                entity.Remove(K.EnergyMax);
            }
            return before;
        }

        private static IEnumerable<JObject> PlayersOf(JObject before) =>
            (before[NK.AbilityEntities] as JObject)?.Properties().Select(x => x.Value).OfType<JObject>().Where(x => x.Str(K.Kind) == Ashen.Generated.CombatValues.Player) ?? Enumerable.Empty<JObject>();

        private static ContactRef ContactRefFromJson(JObject o) => new ContactRef
        {
            Key = o.Str(NK.Key), Index = (int)o.Num(NK.Index), Id = o.Str(K.Id), Crit = Js.IsNum(o[NK.Crit]) ? o.Num(NK.Crit) : (double?)null,
        };

        // ------------------------------------------------------------------ a group

        private static JObject RowsToJson(OrderedMap<TargetRow> rows)
        {
            var o = new JObject();
            foreach (var pair in rows.Entries())
            {
                var row = new JObject
                {
                    [K.Id] = pair.Value.Id,
                    [NK.Contacts] = new JArray(pair.Value.Contacts.Select(contact => contact.DeepClone())),
                    [NK.Receipt] = Clone(pair.Value.Receipt) ?? Js.Null(),
                };
                if (pair.Value.Interactions != null) row[NK.Interactions] = pair.Value.Interactions.DeepClone();
                o[pair.Key] = row;
            }
            return o;
        }

        private static OrderedMap<TargetRow> RowsFromJson(JObject o)
        {
            var rows = new OrderedMap<TargetRow>();
            if (o == null) return rows;
            foreach (var p in o.Properties())
            {
                var row = (JObject)p.Value;
                rows[p.Name] = new TargetRow
                {
                    Id = row.Str(K.Id),
                    Contacts = Js.Items(row[NK.Contacts]).OfType<JObject>().Select(contact => (JObject)contact.DeepClone()).ToList(),
                    Receipt = row[NK.Receipt] is JObject receipt ? (JObject)receipt.DeepClone() : null,
                    Interactions = row[NK.Interactions] is JObject interactions ? (JObject)interactions.DeepClone() : null,
                };
            }
            return rows;
        }

        /// <summary>The committed action as plain data (web group).</summary>
        public static JObject GroupToJson(ExpansionGroup g)
        {
            var o = new JObject { [NK.Key] = g.Key, [NK.SourceId] = g.SourceId, [NK.Carrier] = Clone(g.Carrier), [NK.Targets] = RowsToJson(g.Targets), [NK.Begun] = g.Begun };
            // The web assigns cancelled when a hit's payload starts and keeps it through the hits that follow.
            if (g.Cancelled || g.PayloadStarted || (g.ContactSequence != null && g.ContactSequence.Index > 0)) o[NK.Cancelled] = g.Cancelled;
            if (g.PayloadStarted || (g.ContactSequence != null && g.ContactSequence.Index >= 0)) o[NK.PayloadStarted] = g.PayloadStarted;
            if (g.ContactTelegraphed) o[NK.ContactTelegraphed] = true;
            Put(o, NK.EnemyMoveStart, g.EnemyMoveStart);
            if (g.ContactSequence != null) o[NK.ContactSequence] = SequenceToJson(g.ContactSequence);
            if (g.ReactionAnswered != null) o[NK.ReactionAnswered] = new JArray(g.ReactionAnswered);
            if (g.ReactionNoticed != null) o[NK.ReactionNoticed] = new JArray(g.ReactionNoticed);
            if (g.ManualCounterSerials != null) o[NK.ManualCounterSerials] = new JObject(g.ManualCounterSerials.Select(p => new JProperty(p.Key, Js.N(p.Value))));
            if (g.EnemyWindows.HasValue) o[NK.EnemyWindows] = g.EnemyWindows.Value;
            Put(o, NK.CounterReturn, g.CounterReturn);
            if (g.SmashPressureApplied != null) o[NK.SmashPressureApplied] = new JArray(g.SmashPressureApplied);
            if (g.Kind != null) o[NK.Kind] = g.Kind;
            Put(o, NK.OwnPressure, g.OwnPressure);
            return o;
        }

        private static JObject SequenceToJson(ContactSequence s)
        {
            var o = new JObject
            {
                [NK.RootKey] = s.RootKey, [NK.Index] = s.Index,
                [NK.Contacts] = new JArray(s.Contacts.Select(rows => (JToken)new JObject { [NK.Targets] = RowsToJson(rows) })),
                [NK.RootTargets] = RowsToJson(s.RootTargets),
                [NK.Gates] = new JObject(s.Gates.OrderBy(p => p.Key).Select(p => new JProperty(p.Key.ToString(CultureInfo.InvariantCulture), p.Value))),
            };
            Put(o, NK.ProposalTargetDecisions, s.ProposalTargetDecisions);
            return o;
        }

        public static ExpansionGroup GroupFromJson(JObject o)
        {
            var g = new ExpansionGroup
            {
                Key = o.Str(NK.Key), SourceId = o.Str(NK.SourceId), Carrier = (JObject)o[NK.Carrier].DeepClone(), Targets = RowsFromJson(o.Obj(NK.Targets)),
                Begun = o.Is(NK.Begun), Cancelled = o.Is(NK.Cancelled), PayloadStarted = o.Is(NK.PayloadStarted), ContactTelegraphed = o.Is(NK.ContactTelegraphed),
                EnemyMoveStart = o[NK.EnemyMoveStart] is JObject start ? (JObject)start.DeepClone() : null,
                ReactionAnswered = o[NK.ReactionAnswered] is JArray answered ? answered.Select(Js.Str).ToList() : null,
                ReactionNoticed = o[NK.ReactionNoticed] is JArray noticed ? noticed.Select(Js.Str).ToList() : null,
                ManualCounterSerials = o[NK.ManualCounterSerials] is JObject serials ? serials.Properties().ToDictionary(p => p.Name, p => Js.D(p.Value), StringComparer.Ordinal) : null,
                EnemyWindows = Js.IsNum(o[NK.EnemyWindows]) ? (int)o.Num(NK.EnemyWindows) : (int?)null,
                CounterReturn = o[NK.CounterReturn] is JObject counter ? (JObject)counter.DeepClone() : null,
                SmashPressureApplied = o[NK.SmashPressureApplied] is JArray smash ? smash.Select(Js.Str).ToList() : null,
                Kind = o.Str(NK.Kind),
                OwnPressure = o[NK.OwnPressure] is JObject pressure ? (JObject)pressure.DeepClone() : null,
            };
            if (o[NK.ContactSequence] is JObject sequence)
            {
                var s = new ContactSequence
                {
                    RootKey = sequence.Str(NK.RootKey), Index = (int)sequence.Num(NK.Index),
                    Contacts = Js.Items(sequence[NK.Contacts]).OfType<JObject>().Select(c => RowsFromJson(c.Obj(NK.Targets))).ToList(),
                    RootTargets = RowsFromJson(sequence.Obj(NK.RootTargets)),
                    ProposalTargetDecisions = sequence[NK.ProposalTargetDecisions] is JObject decisions ? (JObject)decisions.DeepClone() : null,
                };
                foreach (var p in sequence.Obj(NK.Gates)?.Properties() ?? Enumerable.Empty<JProperty>()) s.Gates[int.Parse(p.Name, CultureInfo.InvariantCulture)] = Js.Truthy(p.Value);
                // JSON has no aliases: the live hit's targets are the group's, as activateCombatContact reconnects them.
                if (s.Index >= 0 && s.Index < s.Contacts.Count) s.Contacts[s.Index] = g.Targets;
                g.ContactSequence = s;
            }
            return g;
        }

        // ------------------------------------------------------------------ an enemy's committed plan

        /// <summary>A plan as the stack entry saves it: <c>{ group, actions: [{ effect, card, meta, sourceRef, ownerRef, targetRef }] }</c>.</summary>
        public static JObject PlanToJson(EnemyPlan plan)
        {
            JToken Ref(string id) => id == null ? Js.Null() : new JObject { [K.Id] = id };
            return new JObject
            {
                [NK.Group] = GroupToJson(plan.Group),
                [NK.Actions] = new JArray(plan.Actions.Select(a =>
                {
                    var o = new JObject { [NK.Effect] = Clone(a.Effect) };
                    if (a.Card != null) o[K.Card] = Clone(a.Card);
                    o[NK.Meta] = MetaToJson(a.Meta, false);
                    o[NK.SourceRef] = Ref(a.SourceRef);
                    o[NK.OwnerRef] = Ref(a.OwnerRef);
                    o[NK.TargetRef] = Ref(a.TargetRef);
                    return (JToken)o;
                })),
            };
        }

        public static EnemyPlan PlanFromJson(JObject saved)
        {
            var group = GroupFromJson(saved.Obj(NK.Group));
            var plan = new EnemyPlan { Group = group };
            foreach (var a in Js.Items(saved[NK.Actions]).OfType<JObject>())
            {
                string Id(string key) => a[key] is JObject r ? r.Str(K.Id) : null;
                var card = a[K.Card] is JObject carrier ? (JObject)carrier.DeepClone() : null;
                if (card != null && JToken.DeepEquals(card, group.Carrier)) card = group.Carrier;
                plan.Actions.Add(new PlanAction
                {
                    Effect = (JObject)a[NK.Effect].DeepClone(), Card = card, Meta = MetaFromJson(a.Obj(NK.Meta), new Dictionary<string, ExpansionGroup>()),
                    SourceRef = Id(NK.SourceRef), OwnerRef = Id(NK.OwnerRef), TargetRef = Id(NK.TargetRef),
                });
            }
            return plan;
        }
    }
}
