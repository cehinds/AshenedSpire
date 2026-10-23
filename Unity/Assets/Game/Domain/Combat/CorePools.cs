using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// A Core 2 combatant's rating-driven pools (web engine/combatPools.js, model/combatRatings.js persistentWardMaximum,
    /// persistentPoiseMaximum, ratingValue; engine/combatStatusControl.js initializePersistentWard and initializePersistentPoise):
    /// Poise is the physical pool a fight opens full; Ward is off in the default profile (<c>ward.enabled: false</c>). Either follows
    /// its rating while a status or stance moves it: a rise adds the same to the current pool, a fall clamps it and remembers the
    /// amount that was missing.
    /// </summary>
    public static class CorePools
    {
        private static double Whole(double value) => Math.Max(0, Math.Floor(double.IsNaN(value) || double.IsInfinity(value) ? 0 : value));

        // ------------------------------------------------------------------ ratings

        /// <summary>stanceRatingBonus(ctx, entity, id): the offensive and defensive rating a stance in force adds.</summary>
        public static double StanceRatingBonus(CombatState c, JObject entity, string id)
        {
            if (Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion) return 0;
            var stance = CoreStances.StanceOf(c, entity?.Obj(CK.CombatStance));
            if (stance == null) return 0;
            var cfg = CoreStances.Rules(c);
            var maneuver = stance.Str(CK.Maneuver);
            var vocab = c.CoreData.Section(CK.Vocabulary);
            var counterStance = stance[CK.CounterStance] != null && Js.Truthy(stance[CK.CounterStance]);
            var offensive = counterStance || Js.Includes(vocab[CK.OffensiveManeuvers], maneuver);
            var defensive = Js.Includes(vocab[CK.DefensiveManeuvers], maneuver);
            var spell = stance.Str(CK.Camp) == CV.CampSpell;
            var offensiveId = maneuver == CV.StanceCasting || Js.Truthy(stance[CK.Mirrored]) || ((maneuver == CV.StanceCounter || counterStance) && spell) ? CK.RatingPr : CK.RatingAr;
            return (offensive && id == offensiveId ? cfg.Num(CK.OffensiveRating) : 0) + (defensive && id == (spell ? CK.RatingWard : CK.RatingDr) ? cfg.Num(CK.DefensiveRating) : 0);
        }

        /// <summary>ratingValue(ctx, entity, id): the rating plus its stance bonus, the Weak adjustment on Attack, floored at 0.</summary>
        public static double RatingValue(CombatState c, JObject entity, string id)
        {
            var value = Js.Or0(entity?.Obj(CK.Ratings)?[id]) + StanceRatingBonus(c, entity, id);
            if (CoreFixCore.Enabled(c) && id == CK.RatingAr && entity?.Obj(K.Statuses)?.Obj(CV.StatusWeak)?.Num(K.Stacks) > 0)
                value = Math.Floor(value * CoreFixCore.Family(c, CK.Statuses, entity).Num(CK.WeakArMultiplier));
            return Math.Max(0, value);
        }

        private static double Share(CombatState c, string key)
        {
            var share = c.CoreData.Section(CK.Resistance)[key];
            return Js.IsFinite(share) ? Js.D(share) : 1;
        }

        /// <summary>persistentWardMaximum(ctx, entity): the Ward pool a combatant starts a fight with (0 when the profile switches Ward off).</summary>
        public static double WardMaximum(CombatState c, JObject entity) =>
            CoreFixCore.WardDisabled(c) ? 0 : Math.Max(0, Math.Floor(RatingValue(c, entity, CK.RatingWard) * Share(c, CK.WardPool)));

        /// <summary>persistentPoiseMaximum(ctx, entity): the Poise pool a combatant starts a fight with (physical only).</summary>
        public static double PoiseMaximum(CombatState c, JObject entity) => Math.Max(0, Math.Floor(RatingValue(c, entity, CK.RatingPoise) * Share(c, CK.PoisePool)));

        // ------------------------------------------------------------------ pools

        /// <summary>initializePersistentWard: full at every fight start; also lays down the entity's status-control state.</summary>
        public static void InitializeWard(JObject entity, double maximum)
        {
            var max = Whole(maximum);
            entity[CK.PersistentWard] = Js.Obj(CK.Max, max, K.Value, max);
            CoreControl.State(entity);
        }

        /// <summary>initializePersistentPoise: the physical twin of Ward, full at every fight start.</summary>
        public static void InitializePoise(JObject entity, double maximum)
        {
            var max = Whole(maximum);
            entity[CK.PersistentPoise] = Js.Obj(CK.Max, max, K.Value, max);
        }

        /// <summary>resizePool: a rating swing keeps the missing amount fixed.</summary>
        private static void Resize(JObject pool, double max)
        {
            var missing = Math.Max(0, pool[CK.Missing] != null && pool.Num(K.Value) == 0 ? pool.Num(CK.Missing) : pool.Num(CK.Max) - pool.Num(K.Value));
            pool.Put(CK.Max, max);
            pool.Put(K.Value, Math.Max(0, max - missing));
            if (missing > max) pool.Put(CK.Missing, missing);
            else pool.Remove(CK.Missing);
        }

        /// <summary>syncWardMaximum(ctx, entity).</summary>
        public static void SyncWard(CombatState c, JObject entity)
        {
            var ward = entity?.Obj(CK.PersistentWard);
            if (ward == null || !Js.Truthy(entity[CK.Ratings]) || Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion) return;
            var max = WardMaximum(c, entity);
            if (max == ward.Num(CK.Max)) return;
            Resize(ward, max);
            c.Emit(CK.PersistentWardChanged, Js.Obj(K.TargetId, entity[K.Id], K.Value, ward[K.Value], CK.Max, ward[CK.Max], CK.Source, CV.SourceRating));
        }

        /// <summary>syncPoiseMaximum(ctx, entity).</summary>
        public static void SyncPoise(CombatState c, JObject entity)
        {
            var poise = entity?.Obj(CK.PersistentPoise);
            if (poise == null || !Js.Truthy(entity[CK.Ratings]) || Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion) return;
            var max = PoiseMaximum(c, entity);
            if (max == poise.Num(CK.Max)) return;
            Resize(poise, max);
            c.Emit(CK.PersistentPoiseChanged, Js.Obj(K.TargetId, entity[K.Id], K.Value, poise[K.Value], CK.Max, poise[CK.Max], CK.Source, CV.SourceRating));
        }

        /// <summary>The pools follow a status that moves a rating: Ward then Poise, as applyStatus and removeStatus sync them.</summary>
        public static void SyncBoth(CombatState c, JObject entity)
        {
            if (entity == null || Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion) return;
            SyncWard(c, entity);
            SyncPoise(c, entity);
        }
    }

    /// <summary>
    /// The status-control state every expanded combatant carries and the turn entry points that read it (web
    /// engine/combatStatusControl.js state, recoverStatusesAtOwnerStart, sleepRestoration, consumeControlActionLoss, endControlTurn).
    /// A control status (Sleep, Frozen, Prone and the like, the rows of combatStatusRules) arrives with WP-9; a combatant holding one
    /// before that is refused by name rather than read wrongly.
    /// </summary>
    public static class CoreControl
    {
        /// <summary>state(entity): the entity's status-control record, created full on first use.</summary>
        public static JObject State(JObject entity)
        {
            if (entity[CK.StatusControl] is JObject existing) return existing;
            entity[CK.StatusControl] = new JObject
            {
                [CK.Version] = 1,
                [CK.Gauges] = new JObject(),
                [CK.SleepHpRestored] = 0,
                [CK.SleepWardGranted] = false,
                [CK.SleepWardContribution] = 0,
                [CK.WardRestoreCarry] = 0,
                [CK.WardDamageCarry] = 0,
                [CK.WardAllowances] = new JObject(),
                [CK.ResolveNextTurn] = false,
                [CK.ResolveActive] = false,
                [CK.OwnerCycle] = 0,
                [CK.LastRecoveryCycle] = -1,
                [CK.CompletedActions] = new JObject(),
            };
            return entity.Obj(CK.StatusControl);
        }

        private static bool Expanded(CombatState c) => Js.D(c.Fields[CK.CombatExpansionVersion]) == c.CoreData.ExpandedVersion;

        private static double Stacks(JObject entity, string id) => Math.Max(0, Math.Floor(Js.IsFinite(entity?.Obj(K.Statuses)?.Obj(id)?[K.Stacks]) ? entity.Obj(K.Statuses).Obj(id).Num(K.Stacks) : 0));

        /// <summary>The control rows whose active status the entity holds (a lock, a sleep, a freeze).</summary>
        private static bool HoldsControl(CombatState c, JObject entity) =>
            CoreStatuses.ControlRows(c).Any(row => Stacks(entity, row.Str(CK.ActiveStatus)) > 0);

        /// <summary>controlRestrictions(ctx, entity).locked: a lock status holds the entity and no resolve turn is active.</summary>
        public static bool Locked(CombatState c, JObject entity)
        {
            if (!Expanded(c)) return false;
            if (entity.Obj(CK.StatusControl)?.Is(CK.ResolveActive) == true) return false;
            return CoreStatuses.ControlRows(c).Any(row => Js.Truthy(row[CK.Lock]) && Stacks(entity, row.Str(CK.ActiveStatus)) > 0);
        }

        /// <summary>recoverStatusesAtOwnerStart(ctx, entity, {cycle}): the owner's cycle opens; a chance recovery rolls for each held status.</summary>
        public static void RecoverAtOwnerStart(CombatState c, JObject entity, double cycle)
        {
            if (!Expanded(c) || !entity.Is(K.Alive)) return;
            var s = State(entity);
            if (s.Num(CK.LastRecoveryCycle) == cycle) return;
            s.Put(CK.OwnerCycle, cycle);
            s.Put(CK.LastRecoveryCycle, cycle);
            s.Put(CK.ResolveActive, s.Is(CK.ResolveNextTurn));
            s.Put(CK.ResolveNextTurn, false);
            s[CK.CompletedActions] = new JObject();
            foreach (var row in CoreStatuses.ControlRows(c))
            {
                if (CoreFixCore.Enabled(c) && CoreFixCore.Rules(c).Obj(CK.Statuses)?.Obj(CK.Overrides)?[row.Str(CK.ActiveStatus) ?? string.Empty] != null) continue;
                var gauge = s.Obj(CK.Gauges)?.Obj(row.Str(K.Id) ?? string.Empty);
                if (!Js.Truthy(row[CK.ChanceRecovery]) || (Stacks(entity, row.Str(CK.ActiveStatus)) == 0 && !(gauge != null && gauge.Num(K.Value) > 0))) continue;
                throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.StatusControlNotPorted);
            }
        }

        /// <summary>sleepRestoration(ctx, entity): Sleep heals and refills Ward at the owner's start; nothing to do without the status.</summary>
        public static void SleepRestoration(CombatState c, JObject entity)
        {
            if (!Expanded(c) || !entity.Is(K.Alive) || entity.Obj(CK.StatusControl)?.Is(CK.ResolveActive) == true) return;
            var sleep = c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Statuses)?.Obj(CK.Sleep);
            if (sleep != null && Stacks(entity, sleep.Str(CK.Status)) > 0) throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.StatusControlNotPorted);
        }

        /// <summary>consumeControlActionLoss(ctx, entity): a Frozen owner loses actions; none without the status.</summary>
        public static double ConsumeActionLoss(CombatState c, JObject entity)
        {
            if (!Expanded(c) || entity.Obj(CK.StatusControl)?.Is(CK.ResolveActive) == true) return 0;
            var frozen = c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Statuses)?.Obj(CK.Frozen);
            if (frozen != null && Stacks(entity, frozen.Str(CK.Status)) > 0) throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.StatusControlNotPorted);
            return 0;
        }

        /// <summary>endControlTurn(ctx, entity): a locked owner spends a stack waiting; a free one only ends its resolve turn.</summary>
        public static void EndTurn(CombatState c, JObject entity)
        {
            if (!Expanded(c)) return;
            var s = State(entity);
            if (Locked(c, entity)) throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.StatusControlNotPorted);
            s.Put(CK.ResolveActive, false);
        }

        private static JObject StatusRules(CombatState c) => c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Statuses);

        /// <summary>
        /// beforeStatusAction(ctx, target, profile, { connected }): a fire hit thaws a frozen, chilled or frosting target. Thawing is the status
        /// control package's (WP-9): a target that holds one is refused by name.
        /// </summary>
        public static void BeforeStatusAction(CombatState c, JObject target, JObject profile, bool connected)
        {
            if (!Expanded(c) || !connected) return;
            var cfg = StatusRules(c)?.Obj(Ashen.Generated.Core2DamageKeys.Interactions);
            if (cfg == null || !(profile.Is(Ashen.Generated.Core2DamageKeys.ContainsFire) || profile.Str(CK.School) == Ashen.Generated.Core2DamageValues.SchoolFire || profile.Str(CK.DamageType) == Ashen.Generated.Core2DamageValues.SchoolFire)) return;
            var gauge = target.Obj(CK.StatusControl)?.Obj(CK.Gauges)?.Obj(cfg.Str(Ashen.Generated.Core2DamageKeys.FrostGauge) ?? string.Empty);
            if (gauge != null || Stacks(target, cfg.Str(Ashen.Generated.Core2DamageKeys.Frozen)) > 0 || Stacks(target, cfg.Str(Ashen.Generated.Core2DamageKeys.Chilled)) > 0)
                throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.StatusControlNotPorted);
        }

        /// <summary>
        /// completeStatusAction(ctx, target, profile, { connected, actionKey, damaging }): the action's finalization is recorded once per key; it
        /// wakes a sleeper, shatters a frozen or chilled target and reveals a decoy, which the status control package owns (WP-9).
        /// </summary>
        public static void CompleteStatusAction(CombatState c, JObject target, JObject profile, bool connected, string actionKey, bool damaging)
        {
            if (!Expanded(c) || !connected || !damaging) return;
            if (actionKey == null) throw new InvalidOperationException(Ashen.Generated.Core2DamageMessages.ActionKeyRequired);
            var s = State(target);
            var completed = s.Obj(CK.CompletedActions);
            if (completed.Is(actionKey)) return;
            completed[actionKey] = true;
            var rules = StatusRules(c);
            var interactions = rules?.Obj(Ashen.Generated.Core2DamageKeys.Interactions);
            if (rules == null || interactions == null) return;
            var woken = !s.Is(CK.ResolveActive) && Stacks(target, rules.Obj(CK.Sleep)?.Str(CK.Status)) > 0;
            var blunt = profile.Is(Ashen.Generated.Core2DamageKeys.ContainsBlunt) || profile.Str(CK.DamageType) == Ashen.Generated.Core2DamageValues.DamageBlunt;
            var shattered = blunt && (Stacks(target, interactions.Str(Ashen.Generated.Core2DamageKeys.Frozen)) > 0 || Stacks(target, interactions.Str(Ashen.Generated.Core2DamageKeys.Chilled)) > 0);
            var reveals = profile.Is(Ashen.Generated.Core2DamageKeys.ContainsReveal) || profile.Str(CK.Maneuver) == CV.StanceSweep || CoreSets.RevealTypes.Contains(profile.Str(CK.DamageType));
            var revealed = reveals && (Stacks(target, interactions.Str(Ashen.Generated.Core2DamageKeys.Decoy)) > 0 || Stacks(target, interactions.Str(Ashen.Generated.Core2DamageKeys.Concealment)) > 0);
            if (woken || shattered || revealed) throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.StatusControlNotPorted);
        }

        private static JObject Emit(CombatState c, string type, JObject entity, JObject payload)
        {
            var body = new JObject { [K.TargetId] = entity[K.Id]?.DeepClone() };
            foreach (var p in payload.Properties()) body[p.Name] = p.Value;
            return c.Emit(type, body);
        }

        private static double Finite(JToken t) => Js.IsFinite(t) ? Js.D(t) : 0;

        /// <summary>
        /// restorePersistentWard(ctx, entity, nominal, { source, modifierPercent, cap, onceKey }): Ward refills against its missing amount; a card's
        /// surplus becomes Barrier. Returns what the pool gained, the missing debt it paid down, the Barrier and the credit.
        /// </summary>
        public static JObject RestoreWard(CombatState c, JObject entity, double nominal, string source = null, double? modifierPercent = null, double? cap = null, string onceKey = null)
        {
            var none = Js.Obj(Ashen.Generated.Core2DamageKeys.Restored, 0, Ashen.Generated.Core2DamageKeys.Credited, 0);
            if (!Expanded(c) || !entity.Is(K.Alive) || entity.Obj(CK.PersistentWard) == null) return none;
            var s = State(entity);
            var ward = entity.Obj(CK.PersistentWard);
            source = source ?? Ashen.Generated.Core2DamageValues.SourceEffect;
            var sleep = source == Ashen.Generated.Core2DamageValues.SourceSleep;
            if (sleep) throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.StatusControlNotPorted);
            if (onceKey != null && s.Obj(CK.WardAllowances)?[onceKey] != null && Js.Truthy(s.Obj(CK.WardAllowances)[onceKey])) return none;
            if (!(nominal > 0)) return none;
            var percent = modifierPercent ?? 0;
            var overflow = source == Ashen.Generated.Core2DamageValues.SourceCard;
            if (ward.Num(K.Value) >= ward.Num(CK.Max) && !overflow)
            {
                s.Put(CK.WardRestoreCarry, 0);
                return none;
            }
            if (onceKey != null) s.Obj(CK.WardAllowances)[onceKey] = true;
            var credit = Math.Max(0, nominal * (1 + percent / Ashen.Generated.CombatMath.Percent));
            if (cap != null) credit = Math.Min(credit, Math.Max(0, cap.Value));
            var missingMode = ward[CK.Missing] != null;
            var valueBefore = ward.Num(K.Value);
            var room = missingMode ? Math.Max(0, ward.Num(CK.Missing)) : ward.Num(CK.Max) - ward.Num(K.Value);
            var exact = overflow ? credit + s.Num(CK.WardRestoreCarry) : Math.Min(room, credit + s.Num(CK.WardRestoreCarry));
            var whole = Math.Floor(exact + Ashen.Generated.CombatMath.Epsilon);
            var restored = Math.Min(room, whole);
            var barrier = overflow ? whole - restored : 0;
            if (missingMode)
            {
                var missingAfter = room - restored;
                ward.Put(K.Value, Math.Max(0, Math.Min(ward.Num(CK.Max), ward.Num(CK.Max) - missingAfter)));
                if (missingAfter <= ward.Num(CK.Max)) ward.Remove(CK.Missing); else ward.Put(CK.Missing, missingAfter);
            }
            else
            {
                ward.Put(K.Value, ward.Num(K.Value) + restored);
                if (ward.Num(K.Value) > 0) ward.Remove(CK.Missing);
            }
            s.Put(CK.WardRestoreCarry, overflow || ward.Num(K.Value) < ward.Num(CK.Max) ? Math.Max(0, exact - whole) : 0);
            var increase = ward.Num(K.Value) - valueBefore;
            var missingRecovered = restored - increase;
            Emit(c, CK.PersistentWardChanged, entity, Js.Obj(Ashen.Generated.Core2DamageKeys.Restored, increase, Ashen.Generated.Core2DamageKeys.MissingRecovered, missingRecovered, Ashen.Generated.Core2DamageKeys.Credited, credit,
                K.Value, ward[K.Value], CK.Max, ward[CK.Max], CK.Source, source));
            if (barrier > 0)
            {
                entity.Put(CK.Barrier, entity.Or0(CK.Barrier) + barrier);
                Emit(c, Ashen.Generated.Core2DamageKeys.BarrierGained, entity, Js.Obj(K.Amount, barrier, Ashen.Generated.Core2DamageKeys.Total, entity[CK.Barrier]));
            }
            return Js.Obj(Ashen.Generated.Core2DamageKeys.Restored, increase, Ashen.Generated.Core2DamageKeys.MissingRecovered, missingRecovered, Ashen.Generated.Core2DamageKeys.Barrier, barrier,
                Ashen.Generated.Core2DamageKeys.Credited, credit, Ashen.Generated.Core2DamageKeys.Carry, s[CK.WardRestoreCarry]);
        }

        /// <summary>
        /// restorePersistentPoise(ctx, entity, nominal, { source, onceKey, cap, modifierPercent }): the physical twin of Ward. A card's surplus becomes
        /// Block (under the entity's block cap); any other source stops at the pool.
        /// </summary>
        public static JObject RestorePoise(CombatState c, JObject entity, double nominal, string source = null, double? modifierPercent = null, double? cap = null, string onceKey = null)
        {
            var none = Js.Obj(Ashen.Generated.Core2DamageKeys.Restored, 0, Ashen.Generated.Core2DamageKeys.Credited, 0);
            if (!Expanded(c) || !entity.Is(K.Alive) || entity.Obj(CK.PersistentPoise) == null) return none;
            var s = State(entity);
            var poise = entity.Obj(CK.PersistentPoise);
            var key = onceKey != null ? Ashen.Generated.Core2DamageValues.OncePoisePrefix + onceKey : null;
            if (key != null && Js.Truthy(s.Obj(CK.WardAllowances)?[key])) return none;
            if (!(nominal > 0)) return none;
            var missingMode = poise[CK.Missing] != null;
            var overflow = source == Ashen.Generated.Core2DamageValues.SourceCard;
            if (!missingMode && poise.Num(K.Value) >= poise.Num(CK.Max) && !overflow) return none;
            if (key != null) s.Obj(CK.WardAllowances)[key] = true;
            var credit = Math.Max(0, nominal * (1 + (modifierPercent ?? 0) / Ashen.Generated.CombatMath.Percent));
            if (cap != null) credit = Math.Min(credit, Math.Max(0, cap.Value));
            var valueBefore = poise.Num(K.Value);
            var room = missingMode ? Math.Max(0, poise.Num(CK.Missing)) : poise.Num(CK.Max) - poise.Num(K.Value);
            var whole = Math.Floor(credit + Ashen.Generated.CombatMath.Epsilon);
            var restored = Math.Min(room, whole);
            var block = overflow ? whole - restored : 0;
            if (block > 0)
            {
                var blockCap = CoreActions.StatusCap(c, entity, K.BlockCap);
                if (blockCap != null) block = Math.Min(block, Math.Max(0, blockCap.Value - entity.Or0(K.Block)));
            }
            if (missingMode)
            {
                var missingAfter = room - restored;
                poise.Put(K.Value, Math.Max(0, Math.Min(poise.Num(CK.Max), poise.Num(CK.Max) - missingAfter)));
                if (missingAfter <= poise.Num(CK.Max)) poise.Remove(CK.Missing); else poise.Put(CK.Missing, missingAfter);
            }
            else poise.Put(K.Value, poise.Num(K.Value) + restored);
            var increase = poise.Num(K.Value) - valueBefore;
            var missingRecovered = restored - increase;
            Emit(c, CK.PersistentPoiseChanged, entity, Js.Obj(Ashen.Generated.Core2DamageKeys.Restored, increase, Ashen.Generated.Core2DamageKeys.MissingRecovered, missingRecovered, Ashen.Generated.Core2DamageKeys.Credited, credit,
                K.Value, poise[K.Value], CK.Max, poise[CK.Max], CK.Source, source ?? Ashen.Generated.Core2DamageValues.SourceCard));
            if (block > 0)
            {
                entity.Put(K.Block, entity.Or0(K.Block) + block);
                Emit(c, E.BlockGained, entity, Js.Obj(K.Amount, block, Ashen.Generated.Core2DamageKeys.Total, entity[K.Block], CK.Source, Ashen.Generated.Core2DamageValues.SourcePoise));
            }
            return Js.Obj(Ashen.Generated.Core2DamageKeys.Restored, increase, Ashen.Generated.Core2DamageKeys.MissingRecovered, missingRecovered, K.Block, block, Ashen.Generated.Core2DamageKeys.Credited, credit);
        }
    }
}
