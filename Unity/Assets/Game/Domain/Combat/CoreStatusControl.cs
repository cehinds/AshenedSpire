using System;
using Ashen.Generated;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;
using SV = Ashen.Generated.Core2StatusValues;
using SK = Ashen.Generated.Core2StatusKeys;
using SM = Ashen.Generated.Core2StatusMessages;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Status control in a Core 2 fight (web engine/combatStatusControl.js): the active statuses of the control rows (capped, never a meter), the
    /// build-up gauges that fill them and their admission through Ward, the recovery rolls at the owner's turn start, Sleep's rest, the control locks
    /// (Sleep, Paralysis, Dazed) and their manual recovery, Frozen's action loss, the thaw, shatter, wake and reveal a connected action causes, and
    /// the Ward pool's spending. The state it keeps is <c>entity.statusControl</c> (<see cref="CoreControl.State"/>).
    /// </summary>
    public static class CoreStatusControl
    {
        private static bool Enabled(CombatState c) => Js.D(c.Fields[CK.CombatExpansionVersion]) == c.CoreData.ExpandedVersion;

        private static double Finite(JToken value) => Js.IsFinite(value) ? Js.D(value) : 0;

        private static double Whole(JToken value) => Math.Max(0, Math.Floor(Finite(value)));

        private static double Stacks(JObject entity, string id) => Whole(entity?.Obj(K.Statuses)?.Obj(id)?[K.Stacks]);

        private static JObject Rules(CombatState c) => c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Statuses);

        private static JObject RuleForActive(CombatState c, string id) => CoreStatuses.ControlRows(c).FirstOrDefault(row => row.Str(CK.ActiveStatus) == id);

        private static void SyncPools(CombatState c, JObject entity)
        {
            if (entity != null) CorePools.SyncBoth(c, entity);
        }

        /// <summary>emit(ctx, type, entity, payload): the event names its entity as the target.</summary>
        private static JObject Emit(CombatState c, string type, JObject entity, JObject payload = null)
        {
            var body = new JObject { [K.TargetId] = entity[K.Id]?.DeepClone() };
            if (payload != null) foreach (var p in payload.Properties()) body[p.Name] = p.Value;
            return c.Emit(type, body);
        }

        /// <summary>clearActive(ctx, entity, id, count, reason): remove stacks of an active status, telling the log why.</summary>
        private static double ClearActive(CombatState c, JObject entity, string id, double count = double.PositiveInfinity, string reason = null)
        {
            var inst = entity.Obj(K.Statuses)?.Obj(id);
            if (inst == null) return 0;
            var removed = Math.Min(Stacks(entity, id), count);
            if (!(removed > 0)) return 0;
            inst.Put(K.Stacks, inst.Num(K.Stacks) - removed);
            if (inst.Num(K.Stacks) == 0) entity.Obj(K.Statuses).Remove(id);
            SyncPools(c, entity);
            Emit(c, CV.EventStatusRemoved, entity, Js.Obj(K.Status, id, K.Amount, removed, K.Reason, reason ?? SV.Recovered));
            return removed;
        }

        // ------------------------------------------------------------------ Ward and the breaks

        /// <summary>
        /// breakPersistentWard(ctx, entity, meta): THE ONE WARD BREAK. A player pays the break's Stamina next turn; an enemy has none to pay. Every path
        /// that takes Ward to 0 comes here; <c>deferBreak</c> returns the payload without emitting, so a caller can emit after its damageDealt.
        /// </summary>
        public static JObject BreakWard(CombatState c, JObject entity, JObject meta = null)
        {
            meta ??= new JObject();
            var staminaLoss = entity.Str(K.Kind) == V.Player ? 1 : 0;
            if (staminaLoss > 0) entity.Put(K.PendingActionLoss, entity.Or0(K.PendingActionLoss) + staminaLoss);
            var payload = Js.Obj(K.TargetId, entity[K.Id], K.SourceId, meta[K.SourceId], Ashen.Generated.Core2DamageKeys.StaminaLoss, staminaLoss);
            if (Js.Truthy(meta[SK.StatusId])) payload[SK.StatusId] = meta[SK.StatusId].DeepClone();
            if (entity.Str(K.Kind) == V.Enemy) payload[Ashen.Generated.Core2DamageKeys.Interrupted] = true;
            if (!meta.Is(SK.DeferBreak)) EmitBreak(c, entity, SV.WardBroken, payload, SV.WardBreak);
            return payload;
        }

        /// <summary>prepareBreakInterrupt(ctx, entity, cause): the stagger a break causes is tried here; true when the enemy was staggered.</summary>
        public static bool PrepareBreakInterrupt(CombatState c, JObject entity, string cause)
        {
            // A killing hit breaks nothing that can act again.
            if (entity?.Str(K.Kind) != V.Enemy || !entity.Is(K.Alive) || !(entity.Num(K.Hp) > 0)) return false;
            if (!CoreImpact.StaggerEnemy(c, entity, cause)) return false;
            // Under FixCore a stagger is a stance: it cancels nothing and is not an interrupt, so there is no enemyInterrupted event.
            if (CoreFixCore.Enabled(c)) return false;
            throw new NotSupportedException(SM.LegacyBreakNotPorted);
        }

        /// <summary>emitBreak(ctx, entity, type, payload, cause): a break on an enemy keeps the interrupted flag only if the stagger really interrupts.</summary>
        public static void EmitBreak(CombatState c, JObject entity, string type, JObject payload, string cause)
        {
            if (payload.Is(Ashen.Generated.Core2DamageKeys.Interrupted))
            {
                var interrupted = PrepareBreakInterrupt(c, entity, cause);
                if (interrupted) payload[Ashen.Generated.Core2DamageKeys.Interrupted] = true; else payload.Remove(Ashen.Generated.Core2DamageKeys.Interrupted);
                c.Emit(type, payload);
                return;
            }
            c.Emit(type, payload);
        }

        /// <summary>
        /// spendPersistentWard(ctx, entity, amount, meta): fractional pressure shares one carried budget, not one per hit. Returns
        /// <c>{ spent, carry, broke, breakEvent }</c>; <c>breakEvent</c> is the wardBroken payload for a Ward taken from above zero to zero.
        /// </summary>
        public static JObject SpendWard(CombatState c, JObject entity, double amount, JObject meta = null)
        {
            meta ??= new JObject();
            JObject Result(double spent, double carry, bool broke, JObject breakEvent) =>
                Js.Obj(Ashen.Generated.Core2DamageKeys.Spent, spent, Ashen.Generated.Core2DamageKeys.Carry, carry, SK.Broke, broke, SK.BreakEvent, breakEvent != null ? (JToken)breakEvent : Js.Null());
            var ward = entity?.Obj(CK.PersistentWard);
            if (!Enabled(c) || ward == null || amount <= 0) return Result(0, 0, false, null);
            var s = CoreControl.State(entity);
            if (ward.Num(K.Value) <= 0)
            {
                s.Put(CK.WardDamageCarry, 0);
                return Result(0, 0, false, null);
            }
            var exact = Math.Max(0, Finite(Js.N(amount))) + s.Num(CK.WardDamageCarry);
            var whole = Math.Floor(exact + Ashen.Generated.CombatMath.Epsilon);
            var spent = Math.Min(ward.Num(K.Value), whole);
            ward.Put(K.Value, ward.Num(K.Value) - spent);
            s.Put(CK.WardDamageCarry, ward.Num(K.Value) > 0 ? exact - whole : 0);
            var changed = Js.Obj(Ashen.Generated.Core2DamageKeys.Spent, spent, K.Value, ward[K.Value], CK.Max, ward[CK.Max]);
            foreach (var p in meta.Properties()) changed[p.Name] = p.Value.DeepClone();
            Emit(c, CK.PersistentWardChanged, entity, changed);
            var breakEvent = ward.Num(K.Value) == 0 && spent > 0 ? BreakWard(c, entity, meta) : null;
            return Result(spent, s.Num(CK.WardDamageCarry), breakEvent != null, breakEvent);
        }

        // ------------------------------------------------------------------ active statuses

        /// <summary>applyExpansionActiveStatus: a control status (an "active" status of the rule rows) is capped and never a meter. True when the id is one.</summary>
        public static bool ApplyActive(CombatState c, JObject target, string id, double amount, JObject source)
        {
            if (!Enabled(c)) return false;
            var row = RuleForActive(c, id);
            if (row == null) return false;
            if (target == null || !target.Is(K.Alive) || !(amount > 0)) return true;
            var before = Stacks(target, id);
            var add = Math.Min(Whole(Js.N(amount)), Math.Max(0, row.Num(CK.Cap) - before));
            if (add == 0) return true;
            var statuses = target.Obj(K.Statuses);
            var inst = statuses.Obj(id);
            if (inst == null) { statuses[id] = Js.Obj(K.Stacks, 0); inst = statuses.Obj(id); }
            inst.Put(K.Stacks, before + add);
            inst.Remove(K.Meter);
            SyncPools(c, target);
            var def = c.Data.Statuses.Has(id) ? c.Data.Statuses.Get(id) : null;
            if (Js.Truthy(def?.Obj(K.Decay)?[K.Duration])) inst[K.Duration] = def.Obj(K.Decay)[K.Duration].DeepClone();
            Emit(c, E.StatusApplied, target, Js.Obj(K.SourceId, source != null ? source[K.Id] : Js.Null(), CK.SourceKind, source?[K.Kind], CK.TargetKind, target[K.Kind],
                K.Status, id, K.Stacks, add, K.Total, inst[K.Stacks], CK.WasAbsent, before == 0));
            return true;
        }

        /// <summary>grant(ctx, target, id, amount, source): a row's extra grant (Chilled with Frozen): an active status, else a plain application without the meter or proc machinery.</summary>
        private static void Grant(CombatState c, JObject target, string id, double amount, JObject source)
        {
            if (ApplyActive(c, target, id, amount, source)) return;
            var def = c.Data.Statuses.Has(id) ? c.Data.Statuses.Get(id) : null;
            var prior = Stacks(target, id);
            var statuses = target.Obj(K.Statuses);
            var inst = statuses.Obj(id);
            if (inst == null) { statuses[id] = Js.Obj(K.Stacks, 0); inst = statuses.Obj(id); }
            var mode = def?.Str(K.StackMode);
            inst.Put(K.Stacks, mode == V.StackUnique ? 1 : mode == V.StackRefresh ? Math.Max(prior, amount) : prior + amount);
            SyncPools(c, target);
            if (Js.Truthy(def?.Obj(K.Decay)?[K.Duration])) inst[K.Duration] = def.Obj(K.Decay)[K.Duration].DeepClone();
            Emit(c, E.StatusApplied, target, Js.Obj(K.SourceId, source != null ? source[K.Id] : Js.Null(), K.Status, id, K.Stacks, amount, K.Total, inst[K.Stacks], CK.WasAbsent, !(prior > 0)));
        }

        /// <summary>
        /// applyStatusPressure(ctx, target, id, units, source, options): the buildup opcode. The units fill the id's gauge; each full gauge becomes one
        /// stack of the row's active status (or is spent against Ward first, for a spell), up to the row's cap. What is left in the gauge counts as
        /// accepted, so a status-only or mixed action never earns a Counter.
        /// </summary>
        public static JObject ApplyPressure(CombatState c, JObject target, string id, double units, JObject source = null, JObject options = null)
        {
            options ??= new JObject();
            var receipt = Js.Obj(SK.AcceptedBuildup, 0, SK.ActiveAdded, 0, SK.Resisted, 0, SK.WardSpent, 0, SK.PoiseStrain, 0, SK.FullyResisted, true);
            if (!Enabled(c) || target == null || !target.Is(K.Alive) || !(units > 0)) return receipt;
            var cfg = Rules(c);
            var row = cfg.Obj(CK.Statuses)?.Obj(id);
            if (row == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, SM.UnknownBuildup, id));
            var s = CoreControl.State(target);
            var camp = Js.Truthy(options[CK.Camp]) ? options.Str(CK.Camp) : row.Str(CK.Camp);
            if (camp != CV.CampPhysical && camp != CV.CampSpell) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, SM.UnknownCamp, camp));
            var traits = options.Obj(CK.Traits) ?? target.Obj(CK.CombatTraits) ?? new JObject();
            if (Js.Includes(traits[SK.ImmuneStatuses], id))
            {
                receipt[SK.Resisted] = Js.N(units);
                Emit(c, SV.StatusImmune, target, Js.Obj(K.Status, id, SK.Units, units));
                return receipt;
            }
            var percent = Math.Min(CombatMath.Percent, Math.Max(0, Finite(!Js.Nullish(options[SK.ResistancePercent]) ? options[SK.ResistancePercent] : traits.Obj(SK.BuildupResistance)?[id])));
            var accepted = Math.Max(0, units * (1 - percent / Ashen.Generated.CombatMath.Percent));
            var resisted = units - accepted;
            var gauges = s.Obj(CK.Gauges);
            var gauge = gauges.Obj(id);
            var recoveryProfile = Js.Truthy(options[SK.RecoveryProfile]) ? options[SK.RecoveryProfile] : row[Ashen.Generated.Core2DamageKeys.Profile];
            if (gauge == null) { gauges[id] = Js.Obj(K.Value, 0, CK.Camp, camp, SK.RecoveryProfile, recoveryProfile?.DeepClone()); gauge = gauges.Obj(id); }
            // A gauge may receive both camps: only the current application chooses the defense for fills.
            gauge[CK.Camp] = camp;
            gauge[SK.RecoveryProfile] = recoveryProfile?.DeepClone();
            gauge.Put(K.Value, gauge.Num(K.Value) + accepted);
            var iterations = 0;
            double activeAdded = 0, wardSpent = 0, poiseStrain = 0;
            var active = row.Str(CK.ActiveStatus);
            while (gauge.Num(K.Value) >= (Stacks(target, active) > 0 ? row.Num(SK.ActiveThreshold) : row.Num(SK.InitialThreshold)))
            {
                if (++iterations > c.Data.Rule(K.Limits, K.FillLoopGuard) * CombatMath.Percent) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, SM.UnboundedBuildup, id));
                if (Stacks(target, active) >= row.Num(CK.Cap)) break;
                var threshold = Stacks(target, active) > 0 ? row.Num(SK.ActiveThreshold) : row.Num(SK.InitialThreshold);
                gauge.Put(K.Value, gauge.Num(K.Value) - threshold);
                var admitted = true;
                var ward = target.Obj(CK.PersistentWard);
                if (camp == CV.CampSpell && ward != null && ward.Num(K.Value) > 0)
                {
                    var spent = SpendWard(c, target, 1, Js.Obj(K.Status, id, SK.ActionKey, options[SK.ActionKey], K.SourceId, source?[K.Id]));
                    wardSpent += spent.Num(Ashen.Generated.Core2DamageKeys.Spent);
                    admitted = ward.Num(K.Value) == 0;
                }
                else if (camp == CV.CampPhysical && target.Obj(K.PoiseMeter)?.Num(K.Max) > 0)
                {
                    // A physical fill strains the Poise meter: one point each, and a full meter is what lets the status through (and Staggers).
                    var meter = target.Obj(K.PoiseMeter);
                    meter.Put(K.Value, meter.Num(K.Value) + 1);
                    poiseStrain += 1;
                    admitted = meter.Num(K.Value) >= meter.Num(K.Max);
                    if (admitted)
                    {
                        meter.Put(K.Value, meter.Num(K.Value) % meter.Num(K.Max));
                        Emit(c, SV.StatusPoiseBreak, target, Js.Obj(K.Status, id, SK.ActionKey, options[SK.ActionKey]));
                        if (target.Str(K.Kind) == V.Enemy)
                        {
                            c.Emit(E.MeterFilled, Js.Obj(K.TargetId, target[K.Id], K.Meter, K.Poise, K.Threshold, meter[K.Max]));
                            CoreImpact.StaggerEnemy(c, target);
                        }
                        else
                        {
                            StaggerPlayer(c, target);
                        }
                        Actions.GrowPoiseMeter(c, target);
                    }
                }
                if (admitted)
                {
                    var before = Stacks(target, active);
                    Grant(c, target, active, 1, source);
                    activeAdded += Stacks(target, active) - before;
                    foreach (var extra in (row.Obj(Ashen.Generated.RunKeys.Grants) ?? new JObject()).Properties()) Grant(c, target, extra.Name, Js.D(extra.Value), source);
                }
                else resisted += threshold;
                Emit(c, SV.StatusGaugeFilled, target, Js.Obj(K.Status, id, K.Threshold, threshold, SK.Admitted, admitted, CK.Camp, camp, SK.ActionKey, options[SK.ActionKey]));
            }
            // The remaining gauge pressure is admitted even without a full stack.
            var acceptedBuildup = Math.Min(accepted, gauge.Num(K.Value));
            resisted = Math.Min(units, resisted);
            receipt[SK.AcceptedBuildup] = Js.N(acceptedBuildup);
            receipt[SK.ActiveAdded] = Js.N(activeAdded);
            receipt[SK.Resisted] = Js.N(resisted);
            receipt[SK.WardSpent] = Js.N(wardSpent);
            receipt[SK.PoiseStrain] = Js.N(poiseStrain);
            receipt[SK.FullyResisted] = activeAdded == 0 && acceptedBuildup == 0;
            var payload = Js.Obj(K.Status, id, K.SourceId, source != null ? source[K.Id] : Js.Null(), CK.Camp, camp, SK.ActionKey, options[SK.ActionKey], K.Value, gauge[K.Value]);
            foreach (var p in receipt.Properties()) payload[p.Name] = p.Value.DeepClone();
            Emit(c, SV.StatusPressure, target, payload);
            return receipt;
        }

        /// <summary>staggerPlayer(ctx, player): the player's Poise meter filled; the next turn opens with fewer actions, and each status the balance row names is applied.</summary>
        public static void StaggerPlayer(CombatState c, JObject player)
        {
            CoreCounters.Clear(player, c);
            var cfg = c.Data.Balance.Obj(K.Stagger)?.Obj(K.Player) ?? new JObject();
            var actionLoss = Js.IsInt(cfg[K.ActionLoss]) ? cfg.Num(K.ActionLoss) : 0;
            var applied = new JObject();
            foreach (var p in (cfg.Obj(K.Statuses) ?? new JObject()).Properties())
            {
                if (!(Js.D(p.Value) > 0)) continue;
                CoreStatuses.Apply(c, player, p.Name, Js.D(p.Value), null);
                applied[p.Name] = p.Value.DeepClone();
            }
            player.Put(K.PendingActionLoss, player.Or0(K.PendingActionLoss) + actionLoss);
            c.Emit(E.PlayerStaggered, Js.Obj(K.TargetId, player[K.Id], K.ActionLoss, actionLoss, K.Statuses, applied));
        }

        // ------------------------------------------------------------------ the locks

        /// <summary>controlRestrictions(ctx, entity): the control statuses holding the entity and what they take from it.</summary>
        public static JObject Restrictions(CombatState c, JObject entity)
        {
            if (!Enabled(c)) return Js.Obj(K.Locked, false, SK.Locks, new JArray(), SK.CounterDisabled, false, SK.EvadeDisabled, false, CK.ResolveActive, false);
            var resolveActive = entity.Obj(CK.StatusControl)?.Is(CK.ResolveActive) == true;
            var locks = CoreStatuses.ControlRows(c).Where(row => row.Is(CK.Lock) && Stacks(entity, row.Str(CK.ActiveStatus)) > 0).Select(row => row.Str(CK.ActiveStatus)).ToList();
            var locked = !resolveActive && locks.Count > 0;
            return Js.Obj(K.Locked, locked, SK.Locks, new JArray(locks), SK.CounterDisabled, locked, SK.EvadeDisabled, locked, CK.ResolveActive, resolveActive);
        }

        /// <summary>controlGate(ctx, entity, card): a locked hand plays only a card (or the built-in recovery) that names every lock as usable while it holds.</summary>
        public static JObject Gate(CombatState c, JObject entity, JObject card)
        {
            var gate = Restrictions(c, entity);
            card ??= new JObject();
            bool allowed;
            if (!gate.Is(K.Locked) || card.Is(SK.BuiltinRecovery)) allowed = true;
            else
            {
                var covers = !Js.Nullish(card[SK.UsableWhile]) && Js.Truthy(card[SK.UsableWhile]) ? card[SK.UsableWhile] : card.Obj(Ashen.Generated.ConfirmIds.Recovery)?[SK.UsableWhile];
                allowed = Js.Items(gate[SK.Locks]).All(id => Js.Includes(covers, Js.Str(id)));
            }
            var result = new JObject { [Ashen.Generated.Core2DamageKeys.Allowed] = allowed };
            foreach (var p in gate.Properties()) result[p.Name] = p.Value.DeepClone();
            return result;
        }

        /// <summary>recoveryControls(ctx, entity): the built-in recoveries: break each lock, and stand up from Prone.</summary>
        public static JArray RecoveryControls(CombatState c, JObject entity)
        {
            var rows = new JArray();
            if (!Enabled(c)) return rows;
            foreach (var row in CoreStatuses.ControlRows(c).Where(r => r.Is(CK.Lock) && Stacks(entity, r.Str(CK.ActiveStatus)) > 0))
                rows.Add(Js.Obj(K.Id, SV.RecoverPrefix + row.Str(CK.ActiveStatus), K.Status, row[CK.ActiveStatus], K.Stacks, Stacks(entity, row.Str(CK.ActiveStatus)), SK.CostPerStack, row[SK.RecoveryCost],
                    SK.BuiltinRecovery, true, SK.Upcastable, false));
            var prone = Rules(c).Obj(SK.Prone);
            if (Stacks(entity, prone.Str(K.Status)) > 0)
                rows.Add(Js.Obj(K.Id, SV.RecoverStand, K.Status, prone[K.Status], K.Stacks, 1, SK.CostPerStack, prone[SK.StandCost], SK.BuiltinRecovery, true, SK.Upcastable, false,
                    SK.Disabled, Restrictions(c, entity).Is(K.Locked)));
            return rows;
        }

        /// <summary>manualRecovery(ctx, entity, selections, options): pay Stamina to clear active control stacks. Returns <c>{ ok, ... }</c> with the reason when refused.</summary>
        public static JObject ManualRecovery(CombatState c, JObject entity, JArray selections, JObject options = null)
        {
            options ??= new JObject();
            JObject Refused(string reason) => Js.Obj(Ashen.Generated.ShopKeys.Ok, false, K.Reason, reason);
            if (!Enabled(c) || entity == null || !entity.Is(K.Alive) || selections == null || selections.Count == 0) return Refused(SM.NoRecoverySelected);
            var available = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var row in RecoveryControls(c, entity).OfType<JObject>()) available[row.Str(K.Status)] = row;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            double cost = 0;
            foreach (var item in selections.OfType<JObject>())
            {
                var status = item.Str(K.Status);
                available.TryGetValue(status ?? string.Empty, out var row);
                if (row == null || row.Is(SK.Disabled) || seen.Contains(status) || !Js.IsInt(item[K.Stacks]) || item.Num(K.Stacks) < 1 || item.Num(K.Stacks) > row.Num(K.Stacks)) return Refused(SM.InvalidRecoverySelection);
                seen.Add(status);
                cost += item.Num(K.Stacks) * row.Num(SK.CostPerStack);
            }
            var pool = Js.Truthy(options[Ashen.Generated.Core2DeckKeys.Pool]) ? options.Str(Ashen.Generated.Core2DeckKeys.Pool) : entity.Str(K.Kind) == V.Player && Js.IsFinite(entity[K.Stamina]) ? K.Energy : K.Stamina;
            if (!Js.IsFinite(entity[K.Stamina]) || entity.Num(K.Stamina) < cost)
                return Js.Obj(Ashen.Generated.ShopKeys.Ok, false, K.Reason, SM.InsufficientStamina, K.Cost, cost, Ashen.Generated.Core2DeckKeys.Pool, pool);
            entity.Put(K.Stamina, entity.Num(K.Stamina) - cost);
            Emit(c, E.StaminaSpent, entity, Js.Obj(K.SourceId, entity[K.Id], K.Amount, cost, Ashen.Generated.ConfirmIds.Recovery, true));
            foreach (var item in selections.OfType<JObject>()) ClearActive(c, entity, item.Str(K.Status), item.Num(K.Stacks));
            Emit(c, SV.ControlRecovered, entity, Js.Obj(SK.Selections, selections.DeepClone(), K.Cost, cost, Ashen.Generated.Core2DeckKeys.Pool, pool, SK.Manual, true));
            return Js.Obj(Ashen.Generated.ShopKeys.Ok, true, K.Cost, cost, Ashen.Generated.Core2DeckKeys.Pool, pool, SK.Restrictions, Restrictions(c, entity));
        }

        // ------------------------------------------------------------------ recovery rolls

        /// <summary>recoveryChance(ctx, entity, id, options): the percent chance a held status (or its gauge) recovers at the owner's turn start.</summary>
        public static double RecoveryChance(CombatState c, JObject entity, string id, JObject options = null)
        {
            options ??= new JObject();
            var cfg = Rules(c);
            var row = cfg.Obj(CK.Statuses)?.Obj(id) ?? RuleForActive(c, id);
            if (row == null) return 0;
            var profile = Js.Truthy(options[Ashen.Generated.Core2DamageKeys.Profile]) ? options.Str(Ashen.Generated.Core2DamageKeys.Profile) : entity.Obj(CK.StatusControl)?.Obj(CK.Gauges)?.Obj(id)?.Str(SK.RecoveryProfile) ?? row.Str(Ashen.Generated.Core2DamageKeys.Profile);
            var weights = options.Obj(CK.Weights) ?? cfg.Obj(Ashen.Generated.Core2DamageKeys.Profiles)?.Obj(profile);
            var attrs = options.Obj(K.Attributes) ?? entity.Obj(K.Attributes) ?? (entity == c.Player ? c.Attributes : null) ?? new JObject();
            double weighted = 0;
            foreach (var w in (weights ?? new JObject()).Properties()) weighted += Finite(attrs[w.Name]) * Js.D(w.Value);
            var count = Stacks(entity, row.Str(CK.ActiveStatus));
            var recovery = cfg.Obj(Ashen.Generated.ConfirmIds.Recovery);
            var bonus = Finite(!Js.Nullish(options[CK.Bonus]) ? options[CK.Bonus] : entity.Obj(CK.CombatTraits)?.Obj(SK.RecoveryBonus)?[profile]);
            return Math.Min(recovery.Num(K.Maximum), Math.Max(recovery.Num(K.Minimum), recovery.Num(K.Base) + Math.Floor(weighted) + bonus - recovery.Num(SK.StackPenalty) * Math.Max(0, count - 1)));
        }

        /// <summary>effectiveChance(chance, { advantage, disadvantage }): the chance of one roll with advantage (better of two) or disadvantage (worse of two).</summary>
        public static double EffectiveChance(double chance, bool advantage = false, bool disadvantage = false)
        {
            var p = chance / Ashen.Generated.CombatMath.Percent;
            return advantage == disadvantage ? chance : Ashen.Generated.CombatMath.Percent * (advantage ? 1 - (1 - p) * (1 - p) : p * p);
        }

        /// <summary>rollStatusChance(ctx, chance, options): a d100 on the named stream (two with advantage or disadvantage); the roll succeeds at or under the chance.</summary>
        public static JObject RollChance(CombatState c, double chance, bool advantage, bool disadvantage, Ashen.Generated.Core2RngStream stream)
        {
            var first = c.Rng.Int(stream, 1, (int)CombatMath.Percent);
            var rolls = new List<int> { first };
            if (advantage != disadvantage) rolls.Add(c.Rng.Int(stream, 1, (int)CombatMath.Percent));
            var roll = advantage == disadvantage ? first : advantage ? rolls.Min() : rolls.Max();
            return Js.Obj(Ashen.Generated.RewardsKeys.Rolls, new JArray(rolls.Select(r => (JToken)r)), K.Roll, roll, Ashen.Generated.Core2DamageKeys.Chance, chance, SK.EffectiveChance, EffectiveChance(chance, advantage, disadvantage), K.Success, roll <= chance);
        }

        /// <summary>recoverStatusesAtOwnerStart(ctx, entity, { cycle }): the owner's cycle opens and each recoverable status or gauge rolls to lose a stack.</summary>
        public static JArray RecoverAtOwnerStart(CombatState c, JObject entity, double cycle, JObject options = null)
        {
            var result = new JArray();
            options ??= new JObject();
            if (!Enabled(c) || entity == null || !entity.Is(K.Alive)) return result;
            var s = CoreControl.State(entity);
            if (s.Num(CK.LastRecoveryCycle) == cycle) return result;
            s.Put(CK.OwnerCycle, cycle);
            s.Put(CK.LastRecoveryCycle, cycle);
            s.Put(CK.ResolveActive, s.Is(CK.ResolveNextTurn));
            s.Put(CK.ResolveNextTurn, false);
            s[CK.CompletedActions] = new JObject();
            var cfg = Rules(c);
            var interactions = cfg.Obj(Ashen.Generated.Core2DamageKeys.Interactions);
            foreach (var entry in (cfg.Obj(CK.Statuses) ?? new JObject()).Properties())
            {
                var id = entry.Name;
                var row = (JObject)entry.Value;
                if (CoreFixCore.Enabled(c) && CoreFixCore.Rules(c).Obj(CK.Statuses)?.Obj(CK.Overrides)?[row.Str(CK.ActiveStatus) ?? string.Empty] != null) continue;
                var gauge = s.Obj(CK.Gauges)?.Obj(id);
                if (!row.Is(CK.ChanceRecovery) || (Stacks(entity, row.Str(CK.ActiveStatus)) == 0 && !(gauge != null && gauge.Num(K.Value) > 0))) continue;
                var traits = entity.Obj(CK.CombatTraits) ?? new JObject();
                var profile = gauge?.Str(SK.RecoveryProfile) ?? row.Str(Ashen.Generated.Core2DamageKeys.Profile);
                var advantage = options.Is(Ashen.Generated.Core2DamageKeys.Advantage) || Js.Includes(traits[SK.RecoveryAdvantage], profile) || (id == interactions.Str(SK.Paralysis) && Stacks(entity, interactions.Str(CK.Grounded)) > 0);
                var disadvantage = options.Is(Ashen.Generated.Core2HitKeys.Disadvantage) || Js.Includes(traits[SK.RecoveryDisadvantage], profile) || (profile == SV.Bodily && Stacks(entity, interactions.Str(SK.OffBalance)) > 0);
                var chanceOptions = Js.Spread(options);
                chanceOptions[Ashen.Generated.Core2DamageKeys.Profile] = profile;
                var receipt = RollChance(c, RecoveryChance(c, entity, id, chanceOptions), advantage, disadvantage, Ashen.Generated.Core2RngStream.StatusRecovery);
                double removed = 0;
                if (receipt.Is(K.Success))
                {
                    if (Stacks(entity, row.Str(CK.ActiveStatus)) > 0) removed = ClearActive(c, entity, row.Str(CK.ActiveStatus), 1);
                    else
                    {
                        removed = gauge.Num(K.Value) * cfg.Obj(Ashen.Generated.ConfirmIds.Recovery).Num(SK.PendingRemoval);
                        gauge.Put(K.Value, gauge.Num(K.Value) - removed);
                    }
                }
                var recovered = new JObject { [K.Status] = id };
                foreach (var p in receipt.Properties()) recovered[p.Name] = p.Value.DeepClone();
                recovered[V.ReasonRemoved] = Js.N(removed);
                recovered[SK.Cycle] = Js.N(cycle);
                Emit(c, SV.StatusRecoveryRolled, entity, recovered);
                result.Add(recovered);
            }
            return result;
        }

        /// <summary>
        /// restorationModifierPercent(entity, kind): the Ashen Blight bonus to a restoration. A creature with no Blight milestone earns none; one with
        /// a milestone is refused by name (Ashen Blight is not part of a Core 2 run).
        /// </summary>
        public static double RestorationPercent(CombatState c, JObject entity)
        {
            if (Js.Items(entity?.Obj(CK.AshenBlight)?[CK.Milestones]).Any()) throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.AshenBlightNotPorted);
            return 0;
        }

        /// <summary>The Ward Sleep may restore in a whole fight (<c>sleep.wardCombatCap</c>).</summary>
        public static double SleepWardCap(CombatState c) => Rules(c).Obj(CK.Sleep).Num(SK.WardCombatCap);

        /// <summary>sleepRestoration(ctx, entity, options): Sleep heals and refills Ward once an owner cycle, under a per-fight cap.</summary>
        public static JObject SleepRestoration(CombatState c, JObject entity, JObject options = null)
        {
            options ??= new JObject();
            var none = Js.Obj(K.Hp, 0, K.Ward, 0);
            if (!Enabled(c) || entity == null || !entity.Is(K.Alive) || entity.Obj(CK.StatusControl)?.Is(CK.ResolveActive) == true) return none;
            var cfg = Rules(c).Obj(CK.Sleep);
            var count = Stacks(entity, cfg.Str(K.Status));
            if (!(count > 0)) return none;
            var s = CoreControl.State(entity);
            // A second caller in the same owner cycle cannot spend the budget again.
            if (s[SK.LastSleepCycle] != null && s.Num(SK.LastSleepCycle) == s.Num(CK.OwnerCycle)) return none;
            s.Put(SK.LastSleepCycle, s.Num(CK.OwnerCycle));
            var percent = Finite(!Js.Nullish(options[SK.ModifierPercent]) ? options[SK.ModifierPercent] : Js.N(RestorationPercent(c, entity)));
            var hp = Math.Min(Math.Max(0, entity.Num(K.MaxHp) - entity.Num(K.Hp)), Math.Min(Math.Max(0, cfg.Num(SK.HpCombatCap) - s.Num(CK.SleepHpRestored)),
                Math.Max(0, Math.Floor(cfg.Num(SK.HpPerStack) * count * (1 + percent / Ashen.Generated.CombatMath.Percent)))));
            entity.Put(K.Hp, entity.Num(K.Hp) + hp);
            s.Put(CK.SleepHpRestored, s.Num(CK.SleepHpRestored) + hp);
            if (hp != 0) Emit(c, E.Healed, entity, Js.Obj(K.SourceId, entity[K.Id], K.Amount, hp, CK.Source, CK.Sleep));
            double? wardPercent = !Js.Nullish(options[SK.WardModifierPercent]) ? Js.D(options[SK.WardModifierPercent]) : (double?)null;
            var refill = CoreControl.RestoreWard(c, entity, 1, CK.Sleep, wardPercent);
            var ward = refill.Or0(Ashen.Generated.Core2DamageKeys.Restored) + refill.Or0(Ashen.Generated.Core2DamageKeys.MissingRecovered);
            return Js.Obj(K.Hp, hp, K.Ward, ward);
        }

        /// <summary>endControlTurn(ctx, entity, selectedId): a locked owner spends a stack waiting; a free one only ends its resolve turn.</summary>
        public static JObject EndTurn(CombatState c, JObject entity, string selectedId = null)
        {
            if (!Enabled(c)) return Js.Obj(V.ReasonRemoved, 0, CK.ResolveNextTurn, false);
            var s = CoreControl.State(entity);
            var gate = Restrictions(c, entity);
            double removed = 0;
            if (gate.Is(K.Locked))
            {
                var locks = Js.Items(gate[SK.Locks]).Select(Js.Str).ToList();
                var id = selectedId != null && locks.Contains(selectedId) ? selectedId : locks[0];
                removed = ClearActive(c, entity, id, 1, SV.Wait);
                s.Put(CK.ResolveNextTurn, true);
                Emit(c, SV.ControlResolveScheduled, entity, Js.Obj(K.Status, id));
            }
            s.Put(CK.ResolveActive, false);
            return Js.Obj(V.ReasonRemoved, removed, CK.ResolveNextTurn, s[CK.ResolveNextTurn]);
        }

        /// <summary>consumeControlActionLoss(ctx, entity): a Frozen owner loses an action per stack (one a turn) unless a resolve turn is active.</summary>
        public static double ConsumeActionLoss(CombatState c, JObject entity)
        {
            if (!Enabled(c) || entity.Obj(CK.StatusControl)?.Is(CK.ResolveActive) == true) return 0;
            var row = Rules(c).Obj(CK.Frozen);
            return ClearActive(c, entity, row.Str(K.Status), 1, SV.ActionDenied) > 0 ? row.Num(K.ActionLoss) : 0;
        }

        /// <summary>statusIncomingModifier(ctx, entity, profile): the percent of extra damage and the Evade a held status adds against an incoming hit.</summary>
        public static JObject IncomingModifier(CombatState c, JObject entity, JObject profile)
        {
            profile ??= new JObject();
            if (!Enabled(c)) return Js.Obj(K.Percent, 0, SK.EvadeBonus, 0);
            var cfg = Rules(c);
            double percent = 0, evadeBonus = 0;
            var sleep = cfg.Obj(CK.Sleep);
            var prone = cfg.Obj(SK.Prone);
            var interactions = cfg.Obj(Ashen.Generated.Core2DamageKeys.Interactions);
            if (entity.Obj(CK.StatusControl)?.Is(CK.ResolveActive) != true) percent += Stacks(entity, sleep.Str(K.Status)) * sleep.Num(SK.DamagePercentPerStack);
            if (Stacks(entity, prone.Str(K.Status)) > 0)
            {
                if (profile.Str(CK.Range) == CV.RangeMelee) percent += prone.Num(SK.MeleePercent);
                else if (profile.Str(CK.Targeting) != CV.TargetingArea || profile.Is(CK.Projectile))
                {
                    percent += prone.Num(SK.RangedSinglePercent);
                    if (profile.Str(CK.Camp) == CV.CampPhysical && (profile.Str(CK.Range) == CK.Reach || profile.Str(CK.Range) == CV.ManeuverRanged))
                        evadeBonus += profile.Str(CK.Range) == CV.ManeuverRanged ? prone.Num(SK.RangedEvade) : prone.Num(SK.ReachEvade);
                }
            }
            if (profile.Str(CK.DamageType) == Ashen.Generated.Core2DamageValues.DamageBlunt && Stacks(entity, interactions.Str(Ashen.Generated.Core2DamageKeys.Chilled)) > 0) percent += interactions.Num(SK.ChilledBluntPercent);
            return Js.Obj(K.Percent, percent, SK.EvadeBonus, evadeBonus);
        }

        // ------------------------------------------------------------------ what a connected action does

        /// <summary>beforeStatusAction(ctx, target, profile, { connected }): a fire hit thaws a frozen, chilled or frosting target; it must follow the avoidance decision.</summary>
        public static void BeforeAction(CombatState c, JObject target, JObject profile, bool connected = true)
        {
            if (!Enabled(c) || !connected) return;
            var cfg = Rules(c).Obj(Ashen.Generated.Core2DamageKeys.Interactions);
            if (!(profile.Is(Ashen.Generated.Core2DamageKeys.ContainsFire) || profile.Str(CK.School) == Ashen.Generated.Core2DamageValues.SchoolFire || profile.Str(CK.DamageType) == Ashen.Generated.Core2DamageValues.SchoolFire)) return;
            var gauge = target.Obj(CK.StatusControl)?.Obj(CK.Gauges)?.Obj(cfg.Str(Ashen.Generated.Core2DamageKeys.FrostGauge));
            if (gauge != null) gauge.Put(K.Value, 0);
            foreach (var id in new[] { cfg.Str(CK.Frozen), cfg.Str(Ashen.Generated.Core2DamageKeys.Chilled), cfg.Str(Ashen.Generated.Core2DamageKeys.FrostGauge) }) ClearActive(c, target, id, double.PositiveInfinity, SV.Thawed);
        }

        /// <summary>completeStatusAction(ctx, target, profile, { connected, actionKey, damaging }): once per action key, a damaging connected action wakes, shatters and reveals.</summary>
        public static void CompleteAction(CombatState c, JObject target, JObject profile, bool connected, string actionKey, bool damaging)
        {
            if (!Enabled(c) || !connected || !damaging) return;
            if (actionKey == null) throw new InvalidOperationException(Ashen.Generated.Core2DamageMessages.ActionKeyRequired);
            var s = CoreControl.State(target);
            var completed = s.Obj(CK.CompletedActions);
            if (completed.Is(actionKey)) return;
            completed[actionKey] = true;
            var cfg = Rules(c);
            var interactions = cfg.Obj(Ashen.Generated.Core2DamageKeys.Interactions);
            if (!s.Is(CK.ResolveActive)) ClearActive(c, target, cfg.Obj(CK.Sleep).Str(K.Status), 1, SV.Woken);
            if (profile.Is(Ashen.Generated.Core2DamageKeys.ContainsBlunt) || profile.Str(CK.DamageType) == Ashen.Generated.Core2DamageValues.DamageBlunt)
            {
                ClearActive(c, target, interactions.Str(CK.Frozen), 1, SV.Shattered);
                ClearActive(c, target, interactions.Str(Ashen.Generated.Core2DamageKeys.Chilled), 1, SV.Shattered);
            }
            if (profile.Is(Ashen.Generated.Core2DamageKeys.ContainsReveal) || profile.Str(CK.Maneuver) == CV.StanceSweep || CoreSets.RevealTypes.Contains(profile.Str(CK.DamageType)))
                foreach (var id in new[] { interactions.Str(Ashen.Generated.Core2DamageKeys.Decoy), interactions.Str(Ashen.Generated.Core2DamageKeys.Concealment) }) ClearActive(c, target, id, double.PositiveInfinity, SV.Revealed);
        }

        /// <summary>groundStatusPressure(ctx, entity, { clearActiveStacks }): Grounding empties the Paralysis gauge (and clears its active stacks when asked).</summary>
        public static void Ground(CombatState c, JObject entity, bool clearActiveStacks = false)
        {
            if (!Enabled(c)) return;
            var id = Rules(c).Obj(Ashen.Generated.Core2DamageKeys.Interactions).Str(SK.Paralysis);
            var gauge = entity.Obj(CK.StatusControl)?.Obj(CK.Gauges)?.Obj(id);
            if (gauge != null) gauge.Put(K.Value, 0);
            if (clearActiveStacks) ClearActive(c, entity, id, double.PositiveInfinity, CK.Grounded);
        }

        /// <summary>electricalTraits(ctx, entity): grounded (by the status or the trait), conductive (unless grounded) and insulated.</summary>
        public static JObject ElectricalTraits(CombatState c, JObject entity)
        {
            var traits = entity.Obj(CK.CombatTraits);
            var grounded = Enabled(c) && (Stacks(entity, Rules(c).Obj(Ashen.Generated.Core2DamageKeys.Interactions).Str(CK.Grounded)) > 0 || traits?[CK.Grounded]?.Type == JTokenType.Boolean && traits.Is(CK.Grounded));
            var conductive = !grounded && traits?[CV.TraitConductive]?.Type == JTokenType.Boolean && traits.Is(CV.TraitConductive);
            var insulated = traits?[SK.Insulated]?.Type == JTokenType.Boolean && traits.Is(SK.Insulated);
            return Js.Obj(CK.Grounded, grounded, CV.TraitConductive, conductive, SK.Insulated, insulated);
        }

        /// <summary>afterStatusGrant(ctx, entity, id): taking the Grounded status empties the Paralysis gauge.</summary>
        public static void AfterStatusGrant(CombatState c, JObject entity, string id)
        {
            if (Enabled(c) && id == Rules(c)?.Obj(Ashen.Generated.Core2DamageKeys.Interactions)?.Str(CK.Grounded)) Ground(c, entity);
        }
    }
}
