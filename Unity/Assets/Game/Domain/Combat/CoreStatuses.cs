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
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;
using SM = Ashen.Generated.Core2StatusMessages;
using SK = Ashen.Generated.Core2StatusKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// A Core 2 fight's statuses (web engine/statuses.js and the status-control entry points of engine/combatStatusControl.js): the definition
    /// with its FixCore override and the facts laid over it, applying (stack modes, durations, the threshold procs and build-up meters, the active
    /// statuses of the control rows), removing, the two decay clocks (start and end of the owner's turn) and the modifier reads (multipliers, adders,
    /// flags and caps) that the damage, Block and turn math consult. It never names a status id: statuses are content.
    /// </summary>
    public static class CoreStatuses
    {
        // ------------------------------------------------------------------ the definition

        /// <summary>statusDefinition(ctx, id, holder): the registry row with the FixCore override over it and the facts of the rules in its text.</summary>
        public static JObject Definition(CombatState c, string id, JObject holder = null)
        {
            var baseDefinition = c.Data.Statuses.Get(id);
            if (!CoreFixCore.Enabled(c)) return baseDefinition;
            var rules = CoreFixCore.Rules(c, holder);
            var statuses = rules.Obj(K.Statuses);
            var overrides = statuses?.Obj(CK.Overrides)?.Obj(id);
            var row = overrides != null ? Js.Spread(baseDefinition, overrides) : Js.Spread(baseDefinition);
            var staggered = CoreFixCore.StaggeredRules(c);
            string Number(JToken t) => Js.D(t).ToString(CultureInfo.InvariantCulture);
            string Fact()
            {
                if (id == CV.StatusWeak) return string.Format(CultureInfo.InvariantCulture, SM.StatusFactWeak, Number(statuses?[CK.WeakArMultiplier]));
                if (id == Ashen.Generated.Core2DamageValues.SourceVulnerable) return string.Format(CultureInfo.InvariantCulture, SM.StatusFactVulnerable, Number(statuses?[Ashen.Generated.Core2DamageKeys.VulnerabilityMultiplier]));
                if (id == V.IntentStaggered) return string.Format(CultureInfo.InvariantCulture, SM.StatusFactStaggered, Number(staggered[Ashen.Generated.Core2DamageKeys.StaminaLoss]), Number(staggered[Ashen.Generated.Core2DamageKeys.VulnerableStacks]));
                if (id == CK.Evade) return string.Format(CultureInfo.InvariantCulture, SM.StatusFactEvade, Number(rules.Obj(CK.Accuracy)?[Ashen.Generated.Core2FixCoreKeys.BaseAC]));
                if (id == CK.Precision) return SM.StatusFactPrecision;
                return null;
            }
            var fact = Fact();
            if (fact == null) return row;
            string text;
            if (id == CK.Precision) text = string.Format(CultureInfo.InvariantCulture, SM.StatusTailPrecision, fact);
            else if (id == V.IntentStaggered) text = fact;
            else text = string.Format(CultureInfo.InvariantCulture, SM.StatusTailDecay, fact, Number(statuses?[CK.DecayAmount]));
            row[Ashen.Generated.UiKeys.Tooltip] = text;
            row[Ashen.Generated.Core2DamageKeys.Description] = text;
            return row;
        }

        private static double Whole(JToken value) => Math.Max(0, Math.Floor(Js.IsFinite(value) ? Js.D(value) : 0));

        private static bool IsDurationDecay(JToken decay) => decay is JObject o && Js.IsNum(o[K.Duration]);

        /// <summary>The status-control rule rows the fight carries (<c>combatExpansionRules.statuses.statuses</c>).</summary>
        public static IEnumerable<JObject> ControlRows(CombatState c) =>
            (c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Statuses)?.Obj(CK.Statuses) ?? new JObject()).Properties().Select(p => p.Value).OfType<JObject>();

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
            if (!overridden && CoreStatusControl.ApplyActive(c, target, statusId, stacks, source)) return;
            var def = Definition(c, statusId);
            if (target == null || !target.Is(K.Alive)) return;
            var wasAbsent = Statuses.Stacks(target, statusId) <= 0;
            var amount = Math.Floor(stacks);
            if (amount <= 0 && def.Str(K.StackMode) != V.StackUnique) return;
            var statuses = target.Obj(K.Statuses);
            var proc = def.Obj(K.Proc);

            // Threshold-proc resistance (#61): a carried resist status blocks part of every incoming application of the status it names.
            if (proc != null)
            {
                double blocked = 0;
                foreach (var p in statuses.Properties().ToList())
                {
                    if (!(p.Value is JObject otherInst) || Statuses.LiveValue(otherInst) <= 0) continue;
                    var otherDef = c.Data.Statuses.Has(p.Name) ? c.Data.Statuses.Get(p.Name) : null;
                    var resists = otherDef?.Obj(K.Resists);
                    if (resists != null && resists.Str(K.Status) == statusId)
                        blocked += Math.Ceiling(amount * resists.Num(K.Percent) / Ashen.Generated.CombatMath.Percent);
                }
                if (blocked > 0)
                {
                    blocked = Math.Min(blocked, amount);
                    amount -= blocked;
                    c.Emit(E.ProcResisted, Js.Obj(K.TargetId, target[K.Id], K.Status, statusId, K.Blocked, blocked, K.Applied, amount));
                    if (amount <= 0) return;
                }
            }

            var inst = statuses.Obj(statusId);
            if (inst == null)
            {
                statuses[statusId] = Js.Obj(K.Stacks, 0);
                inst = statuses.Obj(statusId);
                var meterDef = def.Obj(K.Meter);
                if (meterDef != null) inst[K.Meter] = Js.Obj(K.Value, 0, K.Max, meterDef[K.Max]);
                else if (proc != null) inst[K.Meter] = Js.Obj(K.Value, 0, K.Max, proc[K.Threshold]);
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
            // Re-applying a duration status adds stacks (above) AND refreshes the duration.
            if (IsDurationDecay(def[K.Decay])) inst[K.Duration] = def.Obj(K.Decay)[K.Duration].DeepClone();

            c.Emit(E.StatusApplied, Js.Obj(CK.SourceKind, source?[K.Kind], CK.TargetKind, target[K.Kind], CK.WasAbsent, wasAbsent, K.TargetId, target[K.Id],
                K.SourceId, source != null ? source[K.Id] : Js.Null(), K.Status, statusId, K.Stacks, amount, K.Total, Statuses.Stacks(target, statusId)));

            if (proc != null) CheckProcFill(c, target, statusId, def, inst, source);
            else if (meter != null) CheckMeterFill(c, target, statusId, def, inst);
            CoreStatusControl.AfterStatusGrant(c, target, statusId);
        }

        /// <summary>
        /// checkProcFill: the threshold reached, the build-up resets to zero (overflow dropped, the threshold constant) and the burst is its own damage
        /// record: a loss of Health, Poise damage and a stagger for an enemy, the proc's own effects, and the post-proc resistance of a tagged creature.
        /// </summary>
        private static void CheckProcFill(CombatState c, JObject entity, string statusId, JObject def, JObject inst, JObject source)
        {
            var meter = inst.Obj(K.Meter);
            if (meter.Num(K.Value) < meter.Num(K.Max)) return;
            var killReceipt = Js.Obj(SK.TargetStatusesBefore, entity.Obj(K.Statuses).DeepClone(), K.SourceId, source?[K.Id]);
            var p = def.Obj(K.Proc);
            meter.Put(K.Value, 0);
            var pct = Math.Floor(entity.Num(K.MaxHp) * p.Num(K.BurstPercent) / Ashen.Generated.CombatMath.Percent);
            var burst = Math.Max(p.Num(K.BurstMin), Math.Min(p.Num(K.BurstMax), pct));
            c.Emit(E.ProcBurst, Js.Obj(K.TargetId, entity[K.Id], K.Status, statusId, K.Amount, burst, K.Threshold, meter[K.Max], K.PoiseDamage, p.Or0(K.PoiseDamage), K.Stagger, p.Is(K.Stagger)));
            void Enq(JObject effect) => c.Enqueue(new CombatAction { Effect = effect, Source = entity, Owner = entity, Target = entity, Meta = new ActionMeta { KillReceipt = killReceipt } });
            Enq(Js.Obj(K.Op, Op.LoseHp, K.Target, V.TargetSelf, K.Amount, burst, K.Cause, V.ProcCausePrefix + statusId));
            var isEnemy = entity.Str(K.Kind) == V.Enemy;
            if (p.Num(K.PoiseDamage) > 0 && isEnemy) Enq(Js.Obj(K.Op, Op.PoiseDamage, K.Amount, p[K.PoiseDamage]));
            if (p.Is(K.Stagger) && isEnemy) Enq(Js.Obj(K.Op, Op.Stagger));
            foreach (var eff in Js.Items(p[K.Effects]).OfType<JObject>()) Enq(eff);
            var resistance = p.Obj(K.Resistance);
            if (resistance != null)
            {
                var enemyDef = isEnemy ? c.Data.Enemies.Get(entity.Str(K.EnemyId)) : null;
                var tags = enemyDef?[K.Tags];
                if (Js.Items(Js.Truthy(tags) ? tags : null).Any(t => Js.Includes(resistance[K.Tags], Js.Str(t))))
                    Enq(Js.Obj(K.Op, Op.ApplyStatus, K.Target, V.TargetSelf, K.Status, resistance[K.Status], K.Stacks, 1));
            }
        }

        /// <summary>checkMeterFill: the meter filled, its overflow carries, its threshold grows by the definition's growth unless a combatant disables the growth.</summary>
        private static void CheckMeterFill(CombatState c, JObject entity, string statusId, JObject def, JObject inst)
        {
            var meter = inst.Obj(K.Meter);
            var meterDef = def.Obj(K.Meter);
            var guard = 0;
            while (meter.Num(K.Value) >= meter.Num(K.Max))
            {
                if (++guard > c.Data.Rule(K.Limits, K.FillLoopGuard)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Ashen.Generated.CombatMessages.MeterLoop, statusId));
                meter.Put(K.Value, meter.Num(K.Value) - meter.Num(K.Max));
                c.Emit(E.MeterFilled, Js.Obj(K.TargetId, entity[K.Id], K.Status, statusId, K.Threshold, meter[K.Max]));
                var growth = Js.Coalesce(meterDef[K.GrowthMult], 1);
                if (growth != 1 && !AnyCombatantFlag(c, K.MeterMaxGrowthDisabled)) meter.Put(K.Max, Math.Ceiling(meter.Num(K.Max) * growth));
                foreach (var eff in Js.Items(meterDef[K.OnFill]).OfType<JObject>())
                    c.Enqueue(new CombatAction { Effect = eff, Source = entity, Owner = entity, Target = entity, Meta = ActionMeta.Empty() });
            }
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

        /// <summary>modifierSources(ctx, entity): the modifiers of each status the entity holds (with its stacks) and, for the player, of its stance.</summary>
        private static IEnumerable<KeyValuePair<JObject, double>> ModifierSources(CombatState c, JObject entity)
        {
            var statuses = entity.Obj(K.Statuses);
            if (statuses != null)
                foreach (var statusId in statuses.Properties().Select(p => p.Name).ToList())
                {
                    var mods = Definition(c, statusId).Obj(K.Modifiers);
                    if (mods != null) yield return new KeyValuePair<JObject, double>(mods, Statuses.Stacks(entity, statusId));
                }
            if (entity.Str(K.Kind) == V.Player && Js.Truthy(entity[K.StanceId]))
            {
                var mods = c.Data.Stances.Get(entity.Str(K.StanceId)).Obj(K.Modifiers);
                if (mods != null) yield return new KeyValuePair<JObject, double>(mods, 1);
            }
        }

        /// <summary>getMult(ctx, entity, key): the product of the flat multipliers (not scaled by stacks).</summary>
        public static double Mult(CombatState c, JObject entity, string key)
        {
            double m = 1;
            foreach (var src in ModifierSources(c, entity)) if (Js.IsNum(src.Key[key])) m *= src.Key.Num(key);
            return m;
        }

        /// <summary>getAdd(ctx, entity, key): the sum of the per-stack adders.</summary>
        public static double Add(CombatState c, JObject entity, string key)
        {
            double a = 0;
            foreach (var src in ModifierSources(c, entity)) if (Js.IsNum(src.Key[key])) a += src.Key.Num(key) * src.Value;
            return a;
        }

        /// <summary>getFlag(ctx, entity, key): any status on the entity (or the player's stance) sets the boolean modifier, FixCore overrides included.</summary>
        public static bool Flag(CombatState c, JObject entity, string key)
        {
            foreach (var src in ModifierSources(c, entity))
                if (src.Key[key] != null && src.Key[key].Type == JTokenType.Boolean && src.Key[key].Value<bool>()) return true;
            return false;
        }

        /// <summary>getCap(ctx, entity, key): the largest declared value (the most generous cap wins), or null when none is declared.</summary>
        public static double? Cap(CombatState c, JObject entity, string key)
        {
            double? cap = null;
            foreach (var src in ModifierSources(c, entity))
                if (Js.IsNum(src.Key[key])) cap = cap == null ? src.Key.Num(key) : Math.Max(cap.Value, src.Key.Num(key));
            return cap;
        }

        /// <summary>anyCombatantFlag(ctx, key): any living combatant carries the boolean modifier.</summary>
        public static bool AnyCombatantFlag(CombatState c, string key)
        {
            if (c.Player != null && c.Player.Is(K.Alive) && Flag(c, c.Player, key)) return true;
            foreach (var e in c.Enemies) if (e.Is(K.Alive) && Flag(c, e, key)) return true;
            return false;
        }

        // ------------------------------------------------------------------ the Staggered stance as a status

        /// <summary>isStaggered(ctx, entity): FixCore is on and a stance the entity holds is the Staggered one.</summary>
        public static bool IsStaggered(CombatState c, JObject entity) =>
            entity != null && CoreFixCore.Enabled(c) && CoreDamage.ActiveCombatStances(c, entity).Any(row => row?.Str(CK.Maneuver) == V.IntentStaggered);

        /// <summary>statusStacksWithStance(ctx, entity, id): status stacks, counting the Staggered stance as one <c>staggered</c> stack.</summary>
        public static double StacksWithStance(CombatState c, JObject entity, string statusId)
        {
            var stacks = Statuses.Stacks(entity, statusId);
            return stacks > 0 || statusId != V.IntentStaggered || !IsStaggered(c, entity) ? stacks : 1;
        }
    }
}
