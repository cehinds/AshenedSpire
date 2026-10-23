using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// A Core 2 fight's statuses (web engine/statuses.js and the status-control entry points of engine/combatStatusControl.js): the
    /// definition with its FixCore override, applying, removing and the two decay clocks (start and end of the owner's turn). The
    /// build-up meters and thresholds of the expanded statuses (a meter filling, a proc bursting) arrive with WP-9 and are refused
    /// by name here rather than half run.
    /// </summary>
    public static class CoreStatuses
    {
        /// <summary>statusDefinition(ctx, id): the registry row with the FixCore override over it.</summary>
        public static JObject Definition(CombatState c, string id) => CoreFixCore.StatusDefinition(c, id, c.Data.Statuses.Get(id));

        private static double Whole(JToken value) => Math.Max(0, Math.Floor(Js.IsFinite(value) ? Js.D(value) : 0));

        private static double ActiveStacks(JObject entity, string id) => Whole(entity?.Obj(K.Statuses)?.Obj(id)?[K.Stacks]);

        private static bool IsDurationDecay(JToken decay) => decay is JObject o && Js.IsNum(o[K.Duration]);

        // ------------------------------------------------------------------ active statuses (the rule rows of combatStatusRules)

        /// <summary>The status-control rule rows the fight carries (<c>combatExpansionRules.statuses.statuses</c>).</summary>
        public static IEnumerable<JObject> ControlRows(CombatState c) =>
            (c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Statuses)?.Obj(CK.Statuses) ?? new JObject()).Properties().Select(p => p.Value).OfType<JObject>();

        private static JObject ActiveRow(CombatState c, string id) => ControlRows(c).FirstOrDefault(row => row.Str(CK.ActiveStatus) == id);

        /// <summary>applyExpansionActiveStatus: a control status (an "active" status of the rule rows) is capped and never a meter.</summary>
        private static bool ApplyActive(CombatState c, JObject target, string id, double amount, JObject source)
        {
            var row = ActiveRow(c, id);
            if (row == null) return false;
            if (target == null || !target.Is(K.Alive) || !(amount > 0)) return true;
            var before = ActiveStacks(target, id);
            var add = Math.Min(Math.Max(0, Math.Floor(amount)), Math.Max(0, row.Num(CK.Cap) - before));
            if (add == 0) return true;
            var statuses = target.Obj(K.Statuses);
            var inst = statuses.Obj(id);
            if (inst == null) { statuses[id] = Js.Obj(K.Stacks, 0); inst = statuses.Obj(id); }
            inst.Put(K.Stacks, before + add);
            inst.Remove(K.Meter);
            CorePools.SyncBoth(c, target);
            var def = c.Data.Statuses.Has(id) ? c.Data.Statuses.Get(id) : null;
            if (Js.Truthy(def?.Obj(K.Decay)?[K.Duration])) inst[K.Duration] = def.Obj(K.Decay)[K.Duration].DeepClone();
            c.Emit(E.StatusApplied, EventFor(target, Js.Obj(K.SourceId, source != null ? source[K.Id] : Js.Null(), CK.SourceKind, source?[K.Kind], CK.TargetKind, target[K.Kind],
                K.Status, id, K.Stacks, add, K.Total, inst[K.Stacks], CK.WasAbsent, before == 0)));
            return true;
        }

        private static JObject EventFor(JObject entity, JObject payload)
        {
            payload[K.TargetId] = entity[K.Id].DeepClone();
            return payload;
        }

        // ------------------------------------------------------------------ apply

        /// <summary>applyStatus(ctx, target, id, stacks, source, options): the applyStatus opcode body; <paramref name="tags"/> are the granting effect's tags (Persistent).</summary>
        public static void Apply(CombatState c, JObject target, string statusId, double stacks, JObject source, IEnumerable<string> tags = null)
        {
            var before = Statuses.Stacks(target, statusId);
            ApplyRaw(c, target, statusId, stacks, source);
            if (CoreFixCore.Enabled(c) && statusId == CV.StatusPrecision)
            {
                var inst = target?.Obj(K.Statuses)?.Obj(statusId);
                if (inst != null && tags != null && tags.Contains(CV.TagPersistent))
                    inst.Put(CK.PersistentStacks, Math.Min(inst.Num(K.Stacks), inst.Or0(CK.PersistentStacks) + Math.Max(0, inst.Num(K.Stacks) - before)));
            }
            CorePools.SyncBoth(c, target);
        }

        private static void ApplyRaw(CombatState c, JObject target, string statusId, double stacks, JObject source)
        {
            var overridden = CoreFixCore.Enabled(c) && CoreFixCore.Rules(c, target).Obj(CK.Statuses)?.Obj(CK.Overrides)?[statusId] != null;
            if (!overridden && ApplyActive(c, target, statusId, stacks, source)) return;
            var def = Definition(c, statusId);
            if (target == null || !target.Is(K.Alive)) return;
            var wasAbsent = Statuses.Stacks(target, statusId) <= 0;
            var amount = Math.Floor(stacks);
            if (amount <= 0 && def.Str(K.StackMode) != V.StackUnique) return;
            if (def.Obj(K.Proc) != null) throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, CM.StatusProcNotPorted, statusId));

            var statuses = target.Obj(K.Statuses);
            var inst = statuses.Obj(statusId);
            if (inst == null)
            {
                statuses[statusId] = Js.Obj(K.Stacks, 0);
                inst = statuses.Obj(statusId);
                var meterDef = def.Obj(K.Meter);
                if (meterDef != null) inst[K.Meter] = Js.Obj(K.Value, 0, K.Max, meterDef[K.Max]);
            }
            var meter = inst.Obj(K.Meter);
            switch (def.Str(K.StackMode))
            {
                case V.StackAdd:
                    if (meter != null) meter.Put(K.Value, meter.Num(K.Value) + amount);
                    else inst.Put(K.Stacks, inst.Num(K.Stacks) + amount);
                    break;
                case V.StackRefresh:
                    if (meter != null) meter.Put(K.Value, Math.Max(meter.Num(K.Value), amount));
                    else inst.Put(K.Stacks, Math.Max(inst.Num(K.Stacks), amount));
                    break;
                case V.StackUnique:
                    if (meter != null) meter.Put(K.Value, Math.Max(meter.Num(K.Value), 1));
                    else inst.Put(K.Stacks, 1);
                    break;
                default:
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Ashen.Generated.CombatMessages.UnknownStackMode, def.Str(K.StackMode), statusId));
            }
            if (IsDurationDecay(def[K.Decay])) inst[K.Duration] = def.Obj(K.Decay)[K.Duration].DeepClone();

            c.Emit(E.StatusApplied, Js.Obj(CK.SourceKind, source?[K.Kind], CK.TargetKind, target[K.Kind], CK.WasAbsent, wasAbsent, K.TargetId, target[K.Id],
                K.SourceId, source != null ? source[K.Id] : Js.Null(), K.Status, statusId, K.Stacks, amount, K.Total, Statuses.Stacks(target, statusId)));
            if (meter != null && meter.Num(K.Value) >= meter.Num(K.Max)) throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, CM.StatusMeterNotPorted, statusId));
        }

        // ------------------------------------------------------------------ remove

        /// <summary>removeStatus(ctx, target, id, opts): drop <paramref name="amount"/> stacks (all when null), emitting the receipts for the reason given.</summary>
        public static void Remove(CombatState c, JObject target, string statusId, string reason, double? amount = null, bool silent = false, bool syncPools = true)
        {
            RemoveRaw(c, target, statusId, reason, amount, silent);
            if (syncPools) CorePools.SyncBoth(c, target);
        }

        private static void RemoveRaw(CombatState c, JObject target, string statusId, string reason, double? amount, bool silent)
        {
            var statuses = target.Obj(K.Statuses);
            if (statuses?[statusId] == null) return;
            var before = Statuses.Stacks(target, statusId);
            if (amount.HasValue)
            {
                var instance = statuses.Obj(statusId);
                var holder = instance.Obj(K.Meter) ?? instance;
                var field = instance.Obj(K.Meter) != null ? K.Value : K.Stacks;
                holder.Put(field, Math.Max(0, holder.Num(field) - amount.Value));
                if (instance[CK.PersistentStacks] != null) instance.Put(CK.PersistentStacks, Math.Min(instance.Num(CK.PersistentStacks), holder.Num(field)));
                if (holder.Num(field) > 0)
                {
                    if (reason == CV.ReasonConsumed || reason == CV.ReasonOwnerTurnStart || reason == CV.ReasonOwnerTurnEnd)
                        c.Emit(CV.EventStatusRemoved, Js.Obj(K.TargetId, target[K.Id], K.Status, statusId, K.Amount, before - Statuses.Stacks(target, statusId), K.Reason, reason));
                    return;
                }
            }
            statuses.Remove(statusId);
            if (reason == CV.ReasonConsumed || reason == CV.ReasonOwnerTurnStart)
                c.Emit(CV.EventStatusRemoved, Js.Obj(K.TargetId, target[K.Id], K.Status, statusId, K.Amount, before, K.Reason, CV.ReasonConsumed));
            if (!silent) c.Emit(E.StatusExpired, Js.Obj(K.TargetId, target[K.Id], K.Status, statusId, K.Reason, reason ?? V.ReasonRemoved));
        }

        // ------------------------------------------------------------------ clocks

        /// <summary>decayAtTurnStart(ctx, entity): FixCore decays a stack of each per-turn status at the owner's turn start, once a round.</summary>
        public static void DecayAtTurnStart(CombatState c, JObject entity)
        {
            if (!CoreFixCore.Enabled(c) || (entity[CK.FixCoreDecayRound] != null && entity.Num(CK.FixCoreDecayRound) == c.Turn)) return;
            entity.Put(CK.FixCoreDecayRound, c.Turn);
            var amount = CoreFixCore.Family(c, CK.Statuses, entity).Num(CK.DecayAmount);
            foreach (var id in entity.Obj(K.Statuses).Properties().Select(p => p.Name).ToList())
            {
                var inst = entity.Obj(K.Statuses).Obj(id);
                var def = Definition(c, id);
                if (id != CV.StatusPrecision && (def.Str(K.Decay) == V.DecayPerTurnEnd || id == CV.StatusEvade))
                    Remove(c, entity, id, CV.ReasonOwnerTurnStart, amount);
                else if (IsDurationDecay(def[K.Decay]))
                {
                    inst.Put(K.Duration, inst.Num(K.Duration) - 1);
                    if (inst.Num(K.Duration) <= 0) Remove(c, entity, id, V.ReasonExpired);
                }
            }
            CorePools.SyncBoth(c, entity);
        }

        /// <summary>decayAtTurnEnd(ctx, entity): under FixCore only the temporary Precision ends with the owner's turn.</summary>
        public static void DecayAtTurnEnd(CombatState c, JObject entity)
        {
            if (!CoreFixCore.Enabled(c)) throw new NotSupportedException(CM.LegacyDecayNotPorted);
            var precision = entity.Obj(K.Statuses)?.Obj(CV.StatusPrecision);
            var temporary = precision != null ? Math.Max(0, precision.Num(K.Stacks) - precision.Or0(CK.PersistentStacks)) : 0;
            if (temporary > 0) RemoveRaw(c, entity, CV.StatusPrecision, CV.ReasonOwnerTurnEnd, temporary, false);
        }

        // ------------------------------------------------------------------ modifiers

        /// <summary>getFlag(ctx, entity, key): any status on the entity sets the boolean modifier (FixCore overrides included).</summary>
        public static bool Flag(CombatState c, JObject entity, string key)
        {
            var statuses = entity.Obj(K.Statuses);
            if (statuses != null)
                foreach (var id in statuses.Properties().Select(p => p.Name).ToList())
                {
                    var mods = Definition(c, id).Obj(K.Modifiers);
                    if (mods?[key] != null && mods[key].Type == JTokenType.Boolean && mods[key].Value<bool>()) return true;
                }
            if (entity.Str(K.Kind) == V.Player && Js.Truthy(entity[K.StanceId]))
            {
                var mods = c.Data.Stances.Get(entity.Str(K.StanceId)).Obj(K.Modifiers);
                if (mods?[key] != null && mods[key].Type == JTokenType.Boolean && mods[key].Value<bool>()) return true;
            }
            return false;
        }
    }
}
