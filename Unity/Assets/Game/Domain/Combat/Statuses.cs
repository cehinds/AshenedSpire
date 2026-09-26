using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The generic status-model interpreter (shipped engine/statuses.js, SPEC §3.7): stack modes, build-up meters,
    /// threshold procs, resists, decay clocks, and the modifiers the damage/block math consults. It never names a
    /// status id; statuses are content.
    /// </summary>
    public static class Statuses
    {
        public static JObject Instance(JObject entity, string statusId) => entity?.Obj(K.Statuses)?.Obj(statusId);

        public static double Stacks(JObject entity, string statusId)
        {
            var inst = Instance(entity, statusId);
            if (inst == null) return 0;
            var meter = inst.Obj(K.Meter);
            return meter != null ? meter.Num(K.Value) : inst.Or0(K.Stacks);
        }

        public static bool Has(JObject entity, string statusId) => Stacks(entity, statusId) > 0;

        /// <summary>The live value an instance contributes (meter value, else stacks) — the shipped (meter ? value : stacks).</summary>
        internal static double LiveValue(JObject inst)
        {
            var meter = inst.Obj(K.Meter);
            return meter != null ? meter.Num(K.Value) : inst.Num(K.Stacks);
        }

        /// <summary>applyStatus(ctx, target, statusId, stacks, source) — the applyStatus opcode body.</summary>
        public static void Apply(CombatState c, JObject target, string statusId, double stacks, JObject source)
        {
            var def = c.Data.Statuses.Get(statusId);
            if (target == null || !target.Is(K.Alive)) return;
            var amount = Math.Floor(stacks);
            var weights = c.RatingsRules?.Obj(K.Statuses)?.Obj(statusId);
            if (weights != null && target[K.Ratings] != null && Js.Truthy(target[K.Ratings]) && source != null && source.Str(K.Id) != target.Str(K.Id) && amount > 0)
            {
                var resistance = Ratings.Value(c, target, K.Poise) * weights.Num(K.Poise) + Ratings.Value(c, target, K.Ward) * weights.Num(K.Ward);
                var cfg = c.RatingsRules.Obj(K.Resistance);
                var reduction = Math.Min(cfg.Num(K.Maximum), resistance / (cfg.Num(K.StatusK) + resistance));
                if (!Js.Truthy(target[K.RatingBuildupRemainders])) target[K.RatingBuildupRemainders] = new JObject();
                var remainders = target.Obj(K.RatingBuildupRemainders);
                var exact = amount * (1 - reduction) + remainders.Or0(statusId);
                var applied = Math.Floor(exact + Ashen.Generated.CombatMath.Epsilon);
                remainders.Put(statusId, Math.Max(0, exact - applied));
                c.Emit(E.ProcResisted, Js.Obj(K.TargetId, target[K.Id], K.Status, statusId, K.Blocked, amount - applied, K.Applied, applied));
                amount = applied;
                if (amount == 0) return;
            }
            if (amount <= 0 && def.Str(K.StackMode) != V.StackUnique) return;

            var statuses = target.Obj(K.Statuses);
            var proc = def.Obj(K.Proc);
            if (proc != null)
            {
                double blocked = 0;
                foreach (var p in statuses.Properties().ToList())
                {
                    if (!(p.Value is JObject otherInst) || LiveValue(otherInst) <= 0) continue;
                    var otherDef = c.Data.Statuses.Get(p.Name);
                    var resists = otherDef.Obj(K.Resists);
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
                inst = Js.Obj(K.Stacks, 0);
                var meterDef = def.Obj(K.Meter);
                if (meterDef != null) inst[K.Meter] = Js.Obj(K.Value, 0, K.Max, meterDef[K.Max]);
                else if (proc != null) inst[K.Meter] = Js.Obj(K.Value, 0, K.Max, proc[K.Threshold]);
                statuses[statusId] = inst;
                inst = statuses.Obj(statusId);
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
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownStackMode, def.Str(K.StackMode), statusId));
            }

            if (IsDurationDecay(def[K.Decay])) inst[K.Duration] = def.Obj(K.Decay)[K.Duration].DeepClone();

            c.Emit(E.StatusApplied, Js.Obj(K.TargetId, target[K.Id], K.SourceId, source != null ? source[K.Id] : Js.Null(),
                K.Status, statusId, K.Stacks, amount, K.Total, Stacks(target, statusId)));

            if (proc != null) CheckProcFill(c, target, statusId, def, inst);
            else if (meter != null) CheckMeterFill(c, target, statusId, def, inst);
        }

        private static void CheckProcFill(CombatState c, JObject entity, string statusId, JObject def, JObject inst)
        {
            var meter = inst.Obj(K.Meter);
            if (meter.Num(K.Value) < meter.Num(K.Max)) return;
            var p = def.Obj(K.Proc);
            meter.Put(K.Value, 0);
            var pct = Math.Floor(entity.Num(K.MaxHp) * p.Num(K.BurstPercent) / Ashen.Generated.CombatMath.Percent);
            var burst = Math.Max(p.Num(K.BurstMin), Math.Min(p.Num(K.BurstMax), pct));
            c.Emit(E.ProcBurst, Js.Obj(K.TargetId, entity[K.Id], K.Status, statusId, K.Amount, burst, K.Threshold, meter[K.Max],
                K.PoiseDamage, p.Or0(K.PoiseDamage), K.Stagger, p.Is(K.Stagger)));
            void Enq(JObject effect) => c.Enqueue(new CombatAction { Effect = effect, Source = entity, Owner = entity, Target = entity, Meta = ActionMeta.Empty() });
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

        private static void CheckMeterFill(CombatState c, JObject entity, string statusId, JObject def, JObject inst)
        {
            var meter = inst.Obj(K.Meter);
            var meterDef = def.Obj(K.Meter);
            var guard = 0;
            while (meter.Num(K.Value) >= meter.Num(K.Max))
            {
                if (++guard > c.Data.Rule(K.Limits, K.FillLoopGuard)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.MeterLoop, statusId));
                meter.Put(K.Value, meter.Num(K.Value) - meter.Num(K.Max));
                c.Emit(E.MeterFilled, Js.Obj(K.TargetId, entity[K.Id], K.Status, statusId, K.Threshold, meter[K.Max]));
                var growth = Js.Coalesce(meterDef[K.GrowthMult], 1);
                if (growth != 1 && !AnyCombatantFlag(c, K.MeterMaxGrowthDisabled)) meter.Put(K.Max, Math.Ceiling(meter.Num(K.Max) * growth));
                foreach (var eff in Js.Items(meterDef[K.OnFill]).OfType<JObject>())
                    c.Enqueue(new CombatAction { Effect = eff, Source = entity, Owner = entity, Target = entity, Meta = ActionMeta.Empty() });
            }
        }

        public static void Remove(CombatState c, JObject target, string statusId, string reason, bool silent = false)
        {
            var statuses = target.Obj(K.Statuses);
            if (statuses?[statusId] == null) return;
            statuses.Remove(statusId);
            if (!silent) c.Emit(E.StatusExpired, Js.Obj(K.TargetId, target[K.Id], K.Status, statusId, K.Reason, reason ?? V.ReasonRemoved));
        }

        /// <summary>decayAtTurnEnd: perTurnEnd −1 stack; {duration} −1 turn; none/onConsume untouched.</summary>
        public static void DecayAtTurnEnd(CombatState c, JObject entity)
        {
            var statuses = entity.Obj(K.Statuses);
            foreach (var statusId in statuses.Properties().Select(p => p.Name).ToList())
            {
                var inst = statuses.Obj(statusId);
                if (inst == null) continue;
                var def = c.Data.Statuses.Get(statusId);
                if (def.Str(K.Decay) == V.DecayPerTurnEnd)
                {
                    var meter = inst.Obj(K.Meter);
                    if (meter != null) meter.Put(K.Value, meter.Num(K.Value) - 1);
                    else inst.Put(K.Stacks, inst.Num(K.Stacks) - 1);
                    if (Stacks(entity, statusId) <= 0) Remove(c, entity, statusId, V.ReasonDecayed);
                }
                else if (IsDurationDecay(def[K.Decay]))
                {
                    inst.Put(K.Duration, inst.Num(K.Duration) - 1);
                    if (inst.Num(K.Duration) <= 0) Remove(c, entity, statusId, V.ReasonExpired);
                }
            }
        }

        private static bool IsDurationDecay(JToken decay) => decay is JObject o && Js.IsNum(o[K.Duration]);

        // ------------------------------------------------------------------ modifiers

        private static IEnumerable<KeyValuePair<JObject, double>> ModifierSources(CombatState c, JObject entity)
        {
            var statuses = entity.Obj(K.Statuses);
            if (statuses != null)
                foreach (var statusId in statuses.Properties().Select(p => p.Name).ToList())
                {
                    var def = c.Data.Statuses.Get(statusId);
                    var mods = def.Obj(K.Modifiers);
                    if (mods != null) yield return new KeyValuePair<JObject, double>(mods, Stacks(entity, statusId));
                }
            if (entity.Str(K.Kind) == V.Player && Js.Truthy(entity[K.StanceId]))
            {
                var stance = c.Data.Stances.Get(entity.Str(K.StanceId));
                var mods = stance.Obj(K.Modifiers);
                if (mods != null) yield return new KeyValuePair<JObject, double>(mods, 1);
            }
        }

        public static double GetMult(CombatState c, JObject entity, string key)
        {
            double m = 1;
            foreach (var src in ModifierSources(c, entity)) if (Js.IsNum(src.Key[key])) m *= src.Key.Num(key);
            return m;
        }

        public static double GetAdd(CombatState c, JObject entity, string key)
        {
            double a = 0;
            foreach (var src in ModifierSources(c, entity)) if (Js.IsNum(src.Key[key])) a += src.Key.Num(key) * src.Value;
            return a;
        }

        public static bool GetFlag(CombatState c, JObject entity, string key)
        {
            foreach (var src in ModifierSources(c, entity))
                if (src.Key[key] != null && src.Key[key].Type == JTokenType.Boolean && src.Key[key].Value<bool>()) return true;
            return false;
        }

        public static double? GetCap(CombatState c, JObject entity, string key)
        {
            double? cap = null;
            foreach (var src in ModifierSources(c, entity))
                if (Js.IsNum(src.Key[key])) cap = cap == null ? src.Key.Num(key) : Math.Max(cap.Value, src.Key.Num(key));
            return cap;
        }

        public static bool AnyCombatantFlag(CombatState c, string key)
        {
            if (c.Player != null && c.Player.Is(K.Alive) && GetFlag(c, c.Player, key)) return true;
            foreach (var e in c.Enemies) if (e.Is(K.Alive) && GetFlag(c, e, key)) return true;
            return false;
        }
    }
}
