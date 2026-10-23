using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DV = Ashen.Generated.Core2DamageValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.Core2MatchupKeys;
using NK = Ashen.Generated.Core2RangeKeys;
using NM = Ashen.Generated.Core2RangeMessages;
using NV = Ashen.Generated.Core2RangeValues;
using Op = Ashen.Generated.CombatOps;
using Pr = Ashen.Generated.CombatPredicates;
using RV = Ashen.Generated.Core2ReactionValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>A trigger an actor owns, with the gate key its fires are counted under (web triggers.rangeHookSources row).</summary>
    public sealed class HookRow
    {
        public string Key;
        public JObject Trigger;
    }

    /// <summary>What an inherited trigger action carries: its carrier (the root's plan and lineage) and the sites it reaches, one batch per hit.</summary>
    public sealed class InheritedRangeAction
    {
        public JObject Carrier;
        public JArray Batches;
    }

    /// <summary>
    /// The range lineage (web engine/combatRangeLineage.js, model/combatRangeLineageState.js): a committed action reserves the finite causal envelope of the
    /// hooks its actor owns before it is paid for. Every hostile effect such a hook can put on a creature is a rider contact of the root's quote, so the
    /// Stamina to reach it is paid once, with the root, and the hook's action later inherits the root's plan, its target sites and its contact. Only the same
    /// typed actor inherits it; a turn hook or another actor's trigger is an independent action, quoted and paid for on its own.
    /// </summary>
    public static class CoreRangeLineage
    {
        private static readonly HashSet<string> SelfTargets = new HashSet<string>(StringComparer.Ordinal) { V.TargetSelf, V.RefOwner };

        // ------------------------------------------------------------------ the hooks an actor owns

        /// <summary>triggers.rangeHookSources(ctx, owner, additionalStatuses): the stance, mounted and status hooks of an actor, with their gate keys.</summary>
        public static List<HookRow> Hooks(CombatState c, JObject owner, IEnumerable<string> additional)
        {
            var rows = new List<HookRow>();
            var key = Triggers.OwnerKey(owner);
            if (owner?.Str(K.Kind) == V.Player)
            {
                if (Js.Truthy(owner[K.StanceId]))
                {
                    var stanceId = owner.Str(K.StanceId);
                    var hooks = c.Data.Stances.Get(stanceId).Arr(K.Hooks);
                    for (var i = 0; hooks != null && i < hooks.Count; i++) rows.Add(new HookRow { Key = Triggers.GateKey(V.StanceGate, key, stanceId, Triggers.Index(i)), Trigger = (JObject)hooks[i] });
                }
                if (c.PropertyMounts != null && c.PropertyMounts.TryGetValue(key, out var mounts))
                    foreach (var sourceKey in mounts.SortedKeys())
                    {
                        var index = 0;
                        foreach (var rule in mounts[sourceKey].Rules)
                            foreach (var trigger in Js.Items(rule[K.Triggers]).OfType<JObject>())
                                rows.Add(new HookRow { Key = Triggers.GateKey(V.PropertyGate, key, sourceKey, Triggers.Index(index++)), Trigger = trigger });
                    }
            }
            var ids = new List<string>();
            foreach (var p in (owner?.Obj(K.Statuses) ?? new JObject()).Properties()) if (!ids.Contains(p.Name)) ids.Add(p.Name);
            foreach (var id in additional ?? Enumerable.Empty<string>()) if (!ids.Contains(id)) ids.Add(id);
            foreach (var id in ids)
            {
                if (!c.Data.Statuses.Has(id)) continue;
                var hooks = c.Data.Statuses.Get(id).Arr(K.Hooks);
                for (var i = 0; hooks != null && i < hooks.Count; i++) rows.Add(new HookRow { Key = Triggers.GateKey(V.StatusGate, key, id, Triggers.Index(i)), Trigger = (JObject)hooks[i] });
            }
            return rows;
        }

        private static bool GivesSelf(JObject effect) => effect.Str(K.Op) == Op.ApplyStatus && SelfTargets.Contains(effect.Str(K.Target));

        /// <summary>The statuses the actor holds or the actions give it, widened by the statuses its own hooks apply to it (even when their condition is false now).</summary>
        private static List<string> SourceStatuses(CombatState c, JObject source, IEnumerable<CombatAction> actions)
        {
            var ids = new List<string>();
            foreach (var p in (source.Obj(K.Statuses) ?? new JObject()).Properties()) ids.Add(p.Name);
            foreach (var action in actions) if (GivesSelf(action.Effect) && !ids.Contains(action.Effect.Str(K.Status))) ids.Add(action.Effect.Str(K.Status));
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var row in Hooks(c, source, ids))
                    foreach (var effect in Js.Items(row.Trigger[K.Do]).OfType<JObject>())
                        if (GivesSelf(effect) && !ids.Contains(effect.Str(K.Status))) { ids.Add(effect.Str(K.Status)); changed = true; }
            }
            return ids;
        }

        /// <summary>Whether the trigger strikes another creature (a hostile op aimed at anything but its owner, its player or an ally).</summary>
        public static bool Strikes(JObject trigger) =>
            Js.Items(trigger[K.Do]).OfType<JObject>().Any(effect => CoreRange.IsHostileOp(effect.Str(K.Op)) && !CoreRange.IsFriendlyTarget(effect.Str(K.Target)));

        /// <summary>Whether the actor owns a hook that strikes another creature, counting the statuses the actions give it.</summary>
        public static bool HasHostileHooks(CombatState c, JObject owner, IEnumerable<CombatAction> actions) =>
            Hooks(c, owner, SourceStatuses(c, owner, actions)).Any(row => Strikes(row.Trigger));

        // ------------------------------------------------------------------ the events an effect can cause

        private static string KeyOf(CombatState c, JObject entity) => CoreRange.ActorKey(c, entity);

        private static JObject Find(CombatState c, string key) => c.Enemies.Concat(new[] { c.Player }).FirstOrDefault(entity => KeyOf(c, entity) == key);

        private sealed class Ev
        {
            public string Type;
            public string Status;
        }

        private static Ev Of(string type, string status = null) => new Ev { Type = type, Status = status };

        /// <summary>
        /// rawEventsFor(ctx, effect, target) filtered by the events a trigger can listen for. The attack outcomes and the Stamina an effect restores are
        /// not among them, so they cannot start a chain.
        /// </summary>
        private static List<Ev> EventsFor(JObject effect, JObject target)
        {
            var op = effect.Str(K.Op);
            var rows = new List<Ev>();
            if (op == Op.Damage)
            {
                rows.Add(Of(E.DamageDealt));
                rows.Add(Of(E.HpLost));
                rows.Add(Of(E.EnemyStaggered));
                rows.Add(Of(E.EnemyDied));
                rows.Add(Of(DK.BlockBroken));
                if (target?.Is(K.ArcaneExposure) == true) rows.Add(Of(E.ArcaneBreak));
                rows.Add(Of(E.StatusApplied, DV.StatusVulnerable));
            }
            else if (op == Op.ApplyStatus || op == CV.OpBuildup) rows.Add(Of(E.StatusApplied, effect.Str(K.Status)));
            else if (op == DV.OpArcaneBuildup)
            {
                if (target?.Is(K.ArcaneExposure) == true)
                {
                    rows.Add(Of(E.ArcaneBreak));
                    rows.Add(Of(E.StatusApplied, target.Obj(K.ArcaneExposure)?.Obj(Ashen.Generated.Core2ReactionKeys.OnBreak)?.Str(K.Status)));
                }
            }
            else if (op == Op.PoiseDamage || op == DV.OpStagger)
            {
                rows.Add(Of(DK.PoiseBroken));
                rows.Add(Of(E.EnemyStaggered));
                rows.Add(Of(E.StatusApplied, DV.StatusVulnerable));
            }
            else if (op == DV.OpWardDamage) rows.Add(Of(Ashen.Generated.Core2StatusValues.WardBroken));
            else if (op == Op.Heal) rows.Add(Of(E.Healed));
            else if (op == Op.RestoreMana) rows.Add(Of(E.ManaRestored));
            else if (op == Op.EnterStance)
            {
                rows.Add(Of(E.StanceEntered));
                rows.Add(Of(E.StanceExited));
            }
            else if (op == DV.OpLoseHp) rows.Add(Of(E.HpLost));
            return rows;
        }

        private static JArray EventRows(List<Ev> events) =>
            new JArray(events.Select(ev => ev.Status != null ? new JObject { [K.Type] = ev.Type, [K.Status] = ev.Status } : new JObject { [K.Type] = ev.Type }));

        /// <summary>possible(pred, event, ...): whether the hook's condition can hold for the event (state-dependent conditions stay possible).</summary>
        private static bool Possible(JToken pred, Ev ev, string sourceKey, string targetKey, JObject carrier)
        {
            if (!(pred is JObject p)) return true;
            switch (p.Str(K.P))
            {
                case Pr.All: return Js.Items(p[K.Preds]).All(row => Possible(row, ev, sourceKey, targetKey, carrier));
                case Pr.Any: return Js.Items(p[K.Preds]).Any(row => Possible(row, ev, sourceKey, targetKey, carrier));
                case Pr.EventStatusIs: return ev.Status == p.Str(K.Status);
                case Pr.EventTargetIsOwner: return targetKey == sourceKey;
                case Pr.EventSourceIsOwner: return true;
                case Pr.EventIsAttack: return ev.Type == E.DamageDealt;
                case Pr.CardTagIs: return Js.Includes(Js.Truthy(carrier?[DK.AuthoredTags]) ? carrier[DK.AuthoredTags] : carrier?[K.Tags] ?? new JArray(), p.Str(K.Tag));
                default: return true;
            }
        }

        private static bool GateSpent(CombatState c, HookRow row)
        {
            c.TriggerState.TryGetValue(row.Key, out var state);
            var trigger = row.Trigger;
            return (trigger.Is(K.Once) && state != null && state.Fires > 0)
                || (!Js.Nullish(trigger[K.LimitPerTurn]) && state != null && state.Turn == c.Turn && state.TurnFires >= trigger.Num(K.LimitPerTurn));
        }

        private static JObject Flags(CombatState c, JObject row) => new JObject
        {
            [DK.Coverage] = Js.N(CoreRange.RangeBand(c, row[DK.Coverage])), [DK.Weapon] = row.Is(DK.Weapon), [DK.ThrowCapable] = row.Is(DK.ThrowCapable), [DK.Bypass] = row.Is(DK.Bypass),
            [DK.SourceWeaponId] = row[DK.SourceWeaponId]?.DeepClone() ?? Js.Null(),
        };

        private static List<JObject> SampledTargets(CombatState c, JObject source, JObject target, JObject effect, JObject carrier) =>
            Actions.ResolveTargets(c, new CombatAction { Source = source, Owner = source, Target = target, Card = carrier, Effect = effect, Meta = ActionMeta.Empty() }, effect.Str(K.Target));

        private static double Floor0(double value) => Math.Max(0, Math.Floor(value));

        private static double CountOf(CombatState c, CombatAction probe, JToken value, JObject target) => Floor0(Actions.EvalNum(c, probe, value, 1, target));

        private static PlayRefusalException Refuse(string message, string reason) => new PlayRefusalException(message, reason);

        // ------------------------------------------------------------------ preparing

        /// <summary>assertPreparingRangeSupported(ctx, source, def): a hostile hook that fires while a card is prepared needs a separately quoted action first.</summary>
        public static void AssertPreparingSupported(CombatState c, JObject source, JObject def)
        {
            if (c.Fields.Obj(CK.RangeState)?.Obj(CK.Policy)?.Is(CK.Enabled) != true) return;
            var carrier = new JObject { [DK.AuthoredTags] = (def[CK.CardTags] ?? def[K.Tags] ?? new JArray()).DeepClone() };
            var hooks = Hooks(c, source, SourceStatuses(c, source, new List<CombatAction>()));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var limit = c.Data.Rule(K.Triggers, NK.MaxDepth);
            void Walk(Ev ev, string targetKey, int depth)
            {
                if (depth > limit) throw Refuse(NM.UnboundedPreparation, RV.ReasonRange);
                foreach (var row in hooks)
                {
                    if (row.Trigger.Str(K.On) != ev.Type || GateSpent(c, row) || !Possible(row.Trigger[K.If], ev, KeyOf(c, source), targetKey, carrier)) continue;
                    var stamp = new JArray(row.Key, ev.Type, ev.Type == E.StatusApplied ? (JToken)(ev.Status ?? (string)null) : Js.Null(), targetKey).ToString(Newtonsoft.Json.Formatting.None);
                    if (!visited.Add(stamp)) continue;
                    foreach (var effect in Js.Items(row.Trigger[K.Do]).OfType<JObject>())
                    {
                        if (CoreRange.IsHostileOp(effect.Str(K.Op)) && !CoreRange.IsFriendlyTarget(effect.Str(K.Target))) throw Refuse(NM.HostilePreparation, RV.ReasonRange);
                        foreach (var next in EventsFor(effect, source)) Walk(next, KeyOf(c, source), depth + 1);
                    }
                }
            }
            Walk(Of(DK.CardPreparing), KeyOf(c, source), 0);
        }

        // ------------------------------------------------------------------ the envelope

        /// <summary>
        /// extendRangeEnvelope(ctx, source, actions, contacts, { preview, carrier }): the finite graph of the hostile hooks the actions can set going. Every
        /// hostile effect a hook can put on a creature adds a rider contact to <paramref name="contacts"/>. Null when the actor owns no striking hook or
        /// none can fire.
        /// </summary>
        public static JObject Extend(CombatState c, JObject source, IReadOnlyList<CombatAction> actions, List<JObject> contacts, bool preview, JObject carrier)
        {
            if (c.Fields.Obj(CK.RangeState)?.Obj(CK.Policy)?.Is(CK.Enabled) != true) return null;
            carrier ??= new JObject();
            var sourceKey = KeyOf(c, source);
            var sites = new List<JObject>();
            var edges = new List<JObject>();
            var hooks = Hooks(c, source, SourceStatuses(c, source, actions));
            if (!hooks.Any(row => Strikes(row.Trigger))) return null;
            var maxEvents = c.Data.Rule(K.Triggers, NK.MaxEvents);
            var maxDepth = c.Data.Rule(K.Triggers, NK.MaxDepth);

            void Visit(JObject site, JObject card, List<string> path)
            {
                if (sites.Count > maxEvents || edges.Count > maxEvents || path.Count > maxDepth) throw Refuse(NM.UnboundedEnvelope, RV.ReasonRange);
                var siteTargetKey = site.Str(DK.TargetKey);
                var target = Find(c, siteTargetKey);
                foreach (var ev in Js.Items(site[NK.Events]).OfType<JObject>().Select(row => Of(row.Str(K.Type), row.Str(K.Status))).ToList())
                    foreach (var hook in hooks)
                    {
                        if (hook.Trigger.Str(K.On) != ev.Type || GateSpent(c, hook) || !Possible(hook.Trigger[K.If], ev, sourceKey, siteTargetKey, card)) continue;
                        var prior = path.Count(key => key == hook.Key);
                        double? limit = hook.Trigger.Is(K.Once) ? 1 : !Js.Nullish(hook.Trigger[K.LimitPerTurn]) ? hook.Trigger.Num(K.LimitPerTurn) : (double?)null;
                        if (limit != null && prior >= limit) continue;
                        var effects = Js.Items(hook.Trigger[K.Do]).OfType<JObject>().ToList();
                        for (var effectIndex = 0; effectIndex < effects.Count; effectIndex++)
                        {
                            var effect = effects[effectIndex];
                            var edge = new JObject
                            {
                                [NK.Id] = NV.EdgePrefix + edges.Count.ToString(CultureInfo.InvariantCulture), [NK.ParentId] = site.Str(NK.Id), [NK.Event] = ev.Type,
                            };
                            if (!string.IsNullOrEmpty(ev.Status)) edge[K.Status] = ev.Status;
                            edge[NK.TriggerKey] = hook.Key;
                            edge[NK.EffectIndex] = effectIndex;
                            edge[DK.Targets] = new JArray();
                            edges.Add(edge);
                            var probe = new CombatAction { Source = source, Owner = source, Target = target, Card = card, Effect = effect, Meta = ActionMeta.Empty() };
                            var repeats = CountOf(c, probe, effect[CK.Repeat], target);
                            var hits = effect.Str(K.Op) == Op.Damage ? CountOf(c, probe, effect[K.Hits], target) : 1;
                            for (var r = 0; r < repeats; r++)
                                for (var h = 0; h < hits; h++)
                                    foreach (var childTarget in SampledTargets(c, source, target, effect, card))
                                    {
                                        var targetKey = KeyOf(c, childTarget);
                                        var childId = NV.SitePrefix + sites.Count.ToString(CultureInfo.InvariantCulture);
                                        var delivery = effect.Is(K.Attack) || effect.Is(K.Tags) ? Flags(c, CoreRange.SourceContact(c, source, childTarget, new JObject(), effect, childId)) : (JObject)site[NK.Delivery].DeepClone();
                                        var contactId = CoreRange.IsHostileOp(effect.Str(K.Op)) && source.Str(K.Kind) != childTarget.Str(K.Kind) ? NV.RiderPrefix + childId : null;
                                        if (contactId != null)
                                        {
                                            var contact = new JObject { [DK.ContactId] = contactId, [DK.TargetKey] = targetKey };
                                            foreach (var p in delivery.Properties()) contact[p.Name] = p.Value.DeepClone();
                                            contacts.Add(contact);
                                        }
                                        var child = new JObject
                                        {
                                            [NK.Id] = childId, [DK.TargetKey] = targetKey, [NK.ParentId] = site.Str(NK.Id), [NK.Events] = EventRows(EventsFor(effect, childTarget)), [NK.Delivery] = delivery,
                                        };
                                        sites.Add(child);
                                        ((JArray)edge[DK.Targets]).Add(new JObject { [DK.TargetKey] = targetKey, [NK.SiteId] = childId, [DK.ContactId] = contactId != null ? (JToken)contactId : Js.Null(), [NK.Batch] = Js.N(r * hits + h) });
                                        Visit(child, new JObject(), path.Concat(new[] { hook.Key }).ToList());
                                    }
                        }
                    }
            }

            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index];
                var effect = action.Effect;
                var probe = new CombatAction { Source = action.Source, Owner = action.Owner, Target = action.Target, Card = action.Card, Effect = effect, Meta = action.Meta ?? ActionMeta.Empty() };
                if (EventsFor(effect, action.Target).Count == 0) continue;
                var repeats = CountOf(c, probe, effect[CK.Repeat], action.Target);
                var hits = effect.Str(K.Op) == Op.Damage ? CountOf(c, probe, effect[K.Hits], action.Target) : 1;
                var batches = new JArray();
                for (var r = 0; r < repeats; r++)
                    for (var h = 0; h < hits; h++)
                    {
                        var batch = (int)(r * hits + h);
                        var committed = action.Meta?.RangeTargetBatches;
                        var targets = committed != null && batch < committed.Count
                            ? committed[batch].Select(id => Triggers.FindEntity(c, id)).ToList()
                            : SampledTargets(c, source, action.Target, effect, action.Card);
                        var refs = new JArray();
                        foreach (var target in targets)
                        {
                            var targetKey = KeyOf(c, target);
                            var id = NV.SitePrefix + sites.Count.ToString(CultureInfo.InvariantCulture);
                            var site = new JObject
                            {
                                [NK.Id] = id, [DK.TargetKey] = targetKey, [NK.ParentId] = Js.Null(), [NK.Events] = EventRows(EventsFor(effect, target)),
                                [NK.Delivery] = Flags(c, CoreRange.SourceContact(c, source, target, action.Card, effect, id)),
                            };
                            sites.Add(site);
                            refs.Add(new JObject { [NK.Id] = target[K.Id].DeepClone(), [NK.SiteId] = id });
                            Visit(site, action.Card, new List<string>());
                        }
                        batches.Add(refs);
                    }
                if (!preview)
                {
                    action.Meta = (action.Meta ?? ActionMeta.Empty()).Clone();
                    action.Meta.RangeSiteBatches = batches;
                }
            }
            var lifecycle = carrier.Is(K.InstanceId) && !carrier.Is(DK.CounterReturnAttack) && !carrier.Is(K.MoveId) ? new[] { DK.CardPlayed, DK.CardResolved } : new string[0];
            foreach (var type in lifecycle)
            {
                var target = actions.Select(action => action.Target).FirstOrDefault(t => t != null) ?? source;
                var id = NV.LifecyclePrefix + type;
                var site = new JObject
                {
                    [NK.Id] = id, [DK.TargetKey] = KeyOf(c, target), [NK.ParentId] = Js.Null(), [NK.Events] = new JArray(new JObject { [K.Type] = type }),
                    [NK.Delivery] = Flags(c, CoreRange.SourceContact(c, source, target, carrier, new JObject(), id)),
                };
                sites.Add(site);
                Visit(site, carrier, new List<string>());
            }
            if (edges.Count == 0)
            {
                if (!preview) foreach (var action in actions) if (action.Meta != null) action.Meta.RangeSiteBatches = null;
                return null;
            }
            return new JObject { [CK.Version] = 1, [DK.RootId] = NV.RootPending, [DK.SourceKey] = sourceKey, [NK.Sites] = new JArray(sites), [NK.Edges] = new JArray(edges) };
        }

        // ------------------------------------------------------------------ the events a root causes

        private static JObject Site(JObject graph, string id) => Js.Items(graph[NK.Sites]).OfType<JObject>().FirstOrDefault(site => site.Str(NK.Id) == id);

        private static int BatchOf(CombatState c, CombatAction action)
        {
            var hits = action.Effect?.Str(K.Op) == Op.Damage ? Floor0(Actions.EvalNum(c, action, action.Effect[K.Hits], 1, action.Target)) : 1;
            return (int)((action.Meta?.ExpansionExecutionRepeat ?? 0) * hits + (action.Meta?.RangeHitIndex ?? 0));
        }

        /// <summary>eventRangeOrigin(ctx, type, payload, action): the site of the causal graph an event of the executing action belongs to, else null.</summary>
        public static JObject EventOrigin(CombatState c, string type, JObject payload, CombatAction action)
        {
            var carrier = action?.Card;
            var graph = carrier?.Obj(Ashen.Generated.Core2MatchupKeys.RangeLineagePlan);
            if (graph == null) return null;
            JObject Origin(JObject site) => new JObject { [CK.Version] = 1, [DK.RootId] = graph[DK.RootId].DeepClone(), [NK.SiteId] = site.Str(NK.Id), [DK.SourceKey] = graph[DK.SourceKey].DeepClone() };
            if (type == DK.CardPlayed || type == DK.CardResolved)
            {
                var life = Site(graph, NV.LifecyclePrefix + type);
                return life != null ? Origin(life) : null;
            }
            var targetId = payload?.Str(K.TargetId);
            var targetKey = string.IsNullOrEmpty(targetId) ? graph.Str(DK.SourceKey) : targetId == V.Player ? CV.ActorPlayerPrefix + (payload.Str(NK.TargetPlayerId) ?? payload.Str(NK.PlayerId) ?? V.Player) : CV.ActorEnemyPrefix + targetId;
            var batch = BatchOf(c, action);
            var refs = action.Meta?.RangeSiteBatches != null && batch < action.Meta.RangeSiteBatches.Count ? action.Meta.RangeSiteBatches[batch] as JArray : null;
            var status = payload?.Str(K.Status);
            var site = Js.Items(refs).OfType<JObject>().Select(row => Site(graph, row.Str(NK.SiteId))).FirstOrDefault(s => s != null && s.Str(DK.TargetKey) == targetKey
                && Js.Items(s[NK.Events]).OfType<JObject>().Any(ev => ev.Str(K.Type) == type && (string.IsNullOrEmpty(ev.Str(K.Status)) || ev.Str(K.Status) == status)));
            return site != null ? Origin(site) : null;
        }

        /// <summary>inheritedRangeAction(ctx, key, owner, event, effect, effectIndex, baseCarrier, parentCarrier): the carrier and target sites a trigger of the root's actor inherits.</summary>
        public static InheritedRangeAction Inherited(CombatState c, string key, JObject owner, JObject ev, JObject effect, int effectIndex, JObject baseCarrier, JObject parentCarrier)
        {
            var origin = ev?.Obj(NK.RangeOrigin);
            if (origin == null) return null;
            if (KeyOf(c, owner) != origin.Str(DK.SourceKey)) return null;
            var parent = parentCarrier ?? c.CurrentAction?.Card ?? c.PendingPlayRef;
            var graph = parent?.Obj(MK.RangeLineagePlan);
            var plan = parent?.Obj(DK.RangePlan);
            if (graph == null || plan == null || graph.Str(DK.RootId) != origin.Str(DK.RootId)) throw Refuse(NM.EnvelopeMissing, NV.ReasonLineage);
            var status = ev.Str(K.Type) == E.StatusApplied ? ev.Str(K.Status) : null;
            var edge = Js.Items(graph[NK.Edges]).OfType<JObject>().FirstOrDefault(row => row.Str(NK.ParentId) == origin.Str(NK.SiteId) && row.Str(NK.Event) == ev.Str(K.Type)
                && (row.Str(K.Status) ?? string.Empty) == (status ?? string.Empty) && row.Str(NK.TriggerKey) == key && row.Num(NK.EffectIndex) == effectIndex);
            if (edge == null) throw Refuse(NM.OutsideEnvelope, NV.ReasonLineage);
            var batches = new List<JArray>();
            foreach (var row in Js.Items(edge[DK.Targets]).OfType<JObject>())
            {
                var entity = Find(c, row.Str(DK.TargetKey));
                if (entity == null) continue;
                var at = (int)row.Num(NK.Batch);
                while (batches.Count <= at) batches.Add(null);
                (batches[at] ??= new JArray()).Add(new JObject { [NK.Id] = entity[K.Id].DeepClone(), [NK.SiteId] = row[NK.SiteId].DeepClone() });
            }
            var first = Site(graph, Js.Items(edge[DK.Targets]).OfType<JObject>().FirstOrDefault()?.Str(NK.SiteId));
            var delivery = first?.Obj(NK.Delivery);
            var carrier = Js.Spread(baseCarrier);
            carrier[DK.RangePlan] = plan.DeepClone();
            carrier[MK.RangeLineagePlan] = graph.DeepClone();
            carrier[MK.RangeLineage] = new JObject { [CK.Version] = 1, [DK.RootId] = graph[DK.RootId].DeepClone(), [NK.EdgeId] = edge[NK.Id].DeepClone() };
            if (delivery != null)
                carrier[DK.ResolvedRangeSource] = new JObject
                {
                    [DK.SourceType] = delivery.Is(DK.Weapon) ? CV.SourceWeapon : DV.SourceSpell, [DK.ItemId] = delivery[DK.SourceWeaponId]?.DeepClone() ?? Js.Null(),
                    [CK.Range] = CoreRange.RangeName(c, delivery.Num(DK.Coverage)), [DK.PropertyTags] = delivery.Is(DK.ThrowCapable) ? new JArray(DV.TagThrow) : new JArray(),
                };
            // A sparse batch list (an array with holes in the web) reads as no sites for the batch it skipped.
            return new InheritedRangeAction { Carrier = carrier, Batches = new JArray(batches.Select(batch => (JToken)batch ?? new JArray())) };
        }

        /// <summary>rangeTargetsFor(ctx, action): the creatures a committed quote reached for this hit of the action, else null.</summary>
        public static List<JObject> TargetsFor(CombatState c, CombatAction action)
        {
            var lineage = action.Card?.Is(MK.RangeLineage) == true;
            if (!lineage && action.Meta?.RangeTargetBatches == null) return null;
            var batch = BatchOf(c, action);
            List<string> ids;
            if (lineage)
            {
                var refs = action.Meta?.RangeSiteBatches != null && batch < action.Meta.RangeSiteBatches.Count ? action.Meta.RangeSiteBatches[batch] as JArray : null;
                ids = Js.Items(refs).OfType<JObject>().Select(row => row.Str(NK.Id)).ToList();
            }
            else ids = batch < action.Meta.RangeTargetBatches.Count ? action.Meta.RangeTargetBatches[batch] : new List<string>();
            return ids.Select(id => Triggers.FindEntity(c, id)).Where(entity => entity != null && entity.Is(K.Alive)).ToList();
        }

        /// <summary>inheritedContactId(ctx, action, target): the rider contact the inherited hit of the action is planned under.</summary>
        public static string InheritedContactId(CombatState c, CombatAction action, JObject target)
        {
            var carrier = action.Card;
            var graph = carrier?.Obj(MK.RangeLineagePlan);
            var edgeId = carrier?.Obj(MK.RangeLineage)?.Str(NK.EdgeId);
            var edge = Js.Items(graph?[NK.Edges]).OfType<JObject>().FirstOrDefault(row => row.Str(NK.Id) == edgeId);
            var batch = BatchOf(c, action);
            var row = Js.Items(edge?[DK.Targets]).OfType<JObject>().FirstOrDefault(r => (int)r.Num(NK.Batch) == batch && r.Str(DK.TargetKey) == KeyOf(c, target));
            return row?.Str(DK.ContactId);
        }

        // ------------------------------------------------------------------ independent triggers

        /// <summary>
        /// acceptIndependentTrigger(ctx, source, actions, key): a hostile trigger owned by another actor (or a turn hook) is an independent action. It may not
        /// borrow the causing card's approach or its payment; it is quoted on a detached RNG first, so an unaffordable automatic effect spends nothing and
        /// samples nothing, then accepted as a root of its own.
        /// </summary>
        public static List<CombatAction> AcceptIndependent(CombatState c, JObject source, List<CombatAction> actions, string key)
        {
            if (c.Fields.Obj(CK.RangeState)?.Obj(CK.Policy)?.Is(CK.Enabled) != true || !actions.Any(action => CoreRange.IsHostileOp(action.Effect.Str(K.Op)))) return actions;
            foreach (var action in actions)
                if (action.Card?.Is(MK.RangeLineage) == true) return actions;
            foreach (var action in actions)
            {
                var tag = Js.Items(action.Effect[K.Tags]).Select(Js.Str).FirstOrDefault(t => t != null && t.StartsWith(NV.TagRange, StringComparison.Ordinal));
                var range = tag != null ? tag.Substring(NV.TagRange.Length) : CV.RangeMelee;
                var card = Js.Spread(action.Card);
                card[DK.ResolvedRangeSource] = new JObject { [DK.SourceType] = DV.SourceSpell, [DK.ItemId] = Js.Null(), [CK.Range] = range, [DK.PropertyTags] = new JArray() };
                foreach (var drop in new[] { DK.RangePlan, MK.RangeLineagePlan, DK.RangeCounterReservation, DK.RangeReturnAuthorization }) card.Remove(drop);
                action.Card = card;
            }
            var quoted = CoreRange.Quote(c, source, actions, null, true, null);
            if (quoted.Arr(DK.Targets).Count == 0) return actions;
            var available = source.Str(K.Kind) == V.Enemy ? CoreRange.StaminaOf(source) : source.Or0(K.Stamina);
            var advance = quoted.Num(DK.AdvanceSp);
            if (!quoted.Is(DK.Available) || available < advance)
            {
                c.Emit(NV.EventActionUnavailable, new JObject
                {
                    [K.SourceId] = source[K.Id].DeepClone(), [DK.OwnerId] = V.Player, [DK.Required] = Js.N(advance), [DK.Available] = Js.N(available), [DK.AdvanceSp] = Js.N(advance),
                    [K.Reason] = RV.ResourceStamina, [NK.TriggerKey] = key,
                });
                return actions.Where(action => !CoreRange.IsHostileOp(action.Effect.Str(K.Op))).ToList();
            }
            foreach (var action in actions)
                if (action.Meta?.Event?.Obj(NK.RangeOrigin) != null)
                {
                    action.Meta = action.Meta.Clone();
                    action.Meta.Event = (JObject)action.Meta.Event.DeepClone();
                    action.Meta.Event.Remove(NK.RangeOrigin);
                }
            var carrier = actions[0].Card;
            var serial = Js.Or0(c.Fields[DK.ExpansionActionSerial]) + 1;
            c.Fields[DK.ExpansionActionSerial] = Js.N(serial);
            var rootId = (Js.Truthy(c.Fields[CK.CombatKey]) ? c.Fields.Str(CK.CombatKey) : DV.CombatKeyDefault) + DV.KeySeparator + NV.SegmentTrigger + DV.KeySeparator + serial.ToString(CultureInfo.InvariantCulture);
            var prior = c.RangeContext;
            c.RangeContext = null;
            try { CoreRange.Accept(c, source, actions, carrier, rootId); }
            finally { if (prior != null) c.RangeContext = prior; }
            return actions;
        }

        /// <summary>
        /// bindReservedRootSites(ctx, actions, graph): a Counter's reservation covers a conservative Health and Poise return; its original root sites are
        /// materialised on the eventual nonzero payload without resampling.
        /// </summary>
        public static void BindReservedRootSites(CombatState c, List<CombatAction> actions, JObject graph)
        {
            if (graph == null) return;
            var available = Js.Items(graph[NK.Sites]).OfType<JObject>().Where(site => Js.Nullish(site[NK.ParentId]) && !site.Str(NK.Id).StartsWith(NV.LifecyclePrefix, StringComparison.Ordinal)).ToList();
            var used = new HashSet<string>(StringComparer.Ordinal);
            var order = new[] { Op.Damage, Op.PoiseDamage, DV.OpWardDamage };
            foreach (var action in actions)
            {
                var signature = EventRows(EventsFor(action.Effect, action.Target)).ToString(Newtonsoft.Json.Formatting.None);
                var site = available.FirstOrDefault(s => !used.Contains(s.Str(NK.Id)) && s.Str(DK.TargetKey) == KeyOf(c, action.Target) && s[NK.Events].ToString(Newtonsoft.Json.Formatting.None) == signature);
                if (site == null) throw Refuse(NM.ReservedOutside, NV.ReasonLineage);
                used.Add(site.Str(NK.Id));
                action.Meta = (action.Meta ?? ActionMeta.Empty()).Clone();
                action.Meta.RangeSiteBatches = new JArray { new JArray { new JObject { [NK.Id] = action.Target[K.Id].DeepClone(), [NK.SiteId] = site.Str(NK.Id) } } };
                action.Meta.RangeTargetBatches = new List<List<string>> { new List<string> { action.Target.Str(K.Id) } };
                action.Meta.RangeEffectIndex = Array.IndexOf(order, action.Effect.Str(K.Op));
            }
        }
    }
}
