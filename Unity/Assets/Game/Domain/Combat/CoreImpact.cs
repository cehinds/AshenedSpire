using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What a Core 2 hit does to the fighters (web engine/actions.js applyAttackDamage, applyProfileDamage, fixCorePoiseBreak, drainFixCorePoise,
    /// fixCoreStagger, staggerEnemy, dealPoiseDamage, afterHpChange): the receipt's Block, Barrier, Ward and Poise taken off, the HP lost, the
    /// events, a Poise break putting its owner in the Staggered stance (one Stamina lost next turn, Vulnerable gained), an advantage interrupt
    /// staggering an enemy, death. Extra Poise damage drains the pool by whole numbers and breaks it at zero.
    /// </summary>
    public static class CoreImpact
    {
        // ------------------------------------------------------------------ Poise break and the Staggered stance

        /// <summary>claimStagger(ctx, target): one stagger per target per round (the stances module's limit); true while the round's limit is not used up.</summary>
        public static bool ClaimStagger(CombatState c, JObject target)
        {
            var limit = Js.Coalesce(c.CoreData.Configured(CK.StancesModule)?[DK.StaggerLimitPerTargetPerRound], c.CoreData.Section(DK.SectionDamage).Num(DK.StaggerLimit));
            var ledger = c.Fields.Obj(DK.GameProfileInterrupts) ?? (JObject)(c.Fields[DK.GameProfileInterrupts] = new JObject());
            var key = c.Turn.ToString(CultureInfo.InvariantCulture) + DV.TurnKeySeparator + target.Str(K.Id);
            if (Js.Or0(ledger[key]) >= limit) return false;
            ledger.Put(key, Js.Or0(ledger[key]) + 1);
            return true;
        }

        /// <summary>fixCoreStaggeredStance(entity, rules): the stance a Staggered owner stands in until its next turn begins.</summary>
        public static JObject StaggeredStance(JObject entity, JObject rules) => new JObject
        {
            [CK.Maneuver] = DV.StanceStaggered, [CK.Camp] = CV.CampPhysical, [CK.Range] = CV.RangeMelee, [DK.Staggered] = true,
            [DK.StaminaLoss] = rules[DK.StaminaLoss]?.DeepClone(), [DK.VulnerableStacks] = rules[DK.VulnerableStacks]?.DeepClone(),
            [CK.ExpiresOnOwnerCycle] = Js.N(entity.Or0(CK.CombatOwnerCycle) + 1),
        };

        /// <summary>fixCoreStagger(ctx, entity, { cause }): the Staggered stance, the Stamina lost next turn and the Vulnerable gained, for a player or an enemy.</summary>
        public static void FixCoreStagger(CombatState c, JObject entity, string cause = null)
        {
            var rules = CoreFixCore.StaggeredRules(c);
            if (entity.Obj(CK.CombatCounter)?.Num(CK.Charges) == 0) entity[DK.CombatCounterStaggered] = entity.Obj(CK.CombatCounter)[CK.Serial]?.DeepClone();
            CoreCounters.Clear(entity, c);
            if (rules.Is(CK.Stance))
            {
                entity[CK.CombatStance] = StaggeredStance(entity, rules);
                c.Emit(CK.CombatStanceChanged, Js.Obj(K.TargetId, entity[K.Id], CK.Maneuver, DV.StanceStaggered));
            }
            if (rules.Num(DK.StaminaLoss) > 0)
            {
                if (entity.Str(K.Kind) == V.Player) entity.Put(K.PendingActionLoss, entity.Or0(K.PendingActionLoss) + rules.Num(DK.StaminaLoss));
                else entity.Put(CK.PendingStaminaLoss, entity.Or0(CK.PendingStaminaLoss) + rules.Num(DK.StaminaLoss));
            }
            if (rules.Num(DK.VulnerableStacks) > 0) CoreStatuses.Apply(c, entity, DV.StatusVulnerable, rules.Num(DK.VulnerableStacks), entity);
            if (entity.Str(K.Kind) == V.Player)
                c.Emit(E.PlayerStaggered, Js.Obj(K.TargetId, entity[K.Id], K.ActionLoss, rules[DK.StaminaLoss],
                    K.Statuses, rules.Num(DK.VulnerableStacks) > 0 ? new JObject { [DV.StatusVulnerable] = rules[DK.VulnerableStacks] } : new JObject(), K.Cause, cause));
        }

        /// <summary>staggerRefused(ctx, enemy): a dead enemy, or one that is no enemy, cannot be staggered.</summary>
        public static bool StaggerRefused(CombatState c, JObject enemy) => enemy == null || enemy.Str(K.Kind) != V.Enemy || !enemy.Is(K.Alive);

        /// <summary>staggerEnemy(ctx, enemy, { cause }): the Core stagger of an enemy (the Staggered stance, not a skipped turn); false when refused.</summary>
        public static bool StaggerEnemy(CombatState c, JObject enemy, string cause = null)
        {
            if (StaggerRefused(c, enemy)) return false;
            FixCoreStagger(c, enemy, cause);
            c.Emit(E.EnemyStaggered, Js.Obj(K.TargetId, enemy[K.Id], K.EnemyId, enemy[K.EnemyId], DK.CancelledMove, Js.Null(), K.Cause, cause));
            return true;
        }

        /// <summary>fixCorePoiseBreak(ctx, source, target): the pool broke; the owner is staggered (an enemy's break counts against the round's stagger limit).</summary>
        public static void PoiseBreak(CombatState c, JObject source, JObject target)
        {
            var staggers = !(target.Str(K.Kind) == V.Enemy && StaggerRefused(c, target));
            c.Emit(DK.PoiseBroken, Js.Obj(K.TargetId, target[K.Id], DK.TargetKind, target[K.Kind], K.SourceId, source?[K.Id], DK.PoiseBreak, staggers));
            if (target.Str(K.Kind) == V.Enemy)
            {
                ClaimStagger(c, target);
                StaggerEnemy(c, target, DV.CausePoiseBreak);
            }
            else FixCoreStagger(c, target, DV.CausePoiseBreak);
        }

        /// <summary>drainFixCorePoise(ctx, source, target, amount): extra Poise damage, whole numbers, never more than the pool holds; emptying it breaks Poise. Returns what it took.</summary>
        public static double DrainPoise(CombatState c, JObject source, JObject target, double amount)
        {
            var poise = target?.Obj(CK.PersistentPoise);
            var n = Math.Max(0, Math.Floor(amount));
            if (!(target?.Is(K.Alive) == true) || !(n > 0) || !(poise?.Num(K.Value) > 0)) return 0;
            var spent = Math.Min(poise.Num(K.Value), n);
            poise.Put(K.Value, poise.Num(K.Value) - spent);
            c.Emit(CK.PersistentPoiseChanged, Js.Obj(K.TargetId, target[K.Id], DK.Spent, spent, K.Value, poise[K.Value], CK.Max, poise[CK.Max], CK.Source, DV.SourcePoiseDamage));
            if (poise.Num(K.Value) == 0 && target.Num(K.Hp) > 0) PoiseBreak(c, source, target);
            return spent;
        }

        /// <summary>dealPoiseDamage(ctx, entity, amount, { source, cause }): a configured fight's Poise damage drains the pool (it never fills a break meter).</summary>
        public static void DealPoiseDamage(CombatState c, JObject entity, double amount, JObject source = null)
        {
            if (CoreFixCore.Enabled(c)) DrainPoise(c, source, entity, amount);
        }

        // ------------------------------------------------------------------ hit points

        /// <summary>proposalThreshold(ctx, target): the status rules that act when a creature falls below a threshold fire once its Health changed.</summary>
        public static void ProposalThreshold(CombatState c, JObject target)
        {
            var threshold = CoreProposal.ThresholdPlan(c, target);
            if (Js.Items(threshold[CK.Effects]).Any()) CoreProposal.ApplyPlan(c, threshold, target, target, null);
        }

        /// <summary>afterHpChange(ctx, target, death): the lethal-threshold status rules, then a fall to zero kills (an enemy reports it); a player's revive token is not ported.</summary>
        public static void AfterHpChange(CombatState c, JObject target, JObject death = null)
        {
            if (target.Num(K.Hp) > 0)
            {
                var threshold = CoreProposal.ThresholdPlan(c, target);
                if (Js.Items(threshold[CK.Effects]).Any()) CoreProposal.ApplyPlan(c, threshold, target, target, null);
            }
            if (target.Num(K.Hp) <= 0 && target.Is(K.Alive) && target.Str(K.Kind) == V.Player && target == c.Player && c.Fields[CK.Consumables] is JObject counts && counts.Properties().Any(p => Js.Or0(p.Value) >= 1))
                throw new NotSupportedException(DM.ReviveNotPorted);
            if (target.Num(K.Hp) <= 0 && target.Is(K.Alive))
            {
                target.Put(K.Hp, 0);
                target.Put(K.Alive, false);
                if (target.Str(K.Kind) == V.Enemy)
                {
                    var payload = Js.Obj(K.TargetId, target[K.Id], K.EnemyId, target[K.EnemyId]);
                    if (death != null) foreach (var p in death.Properties()) payload[p.Name] = p.Value.DeepClone();
                    c.Emit(E.EnemyDied, payload);
                }
            }
            Triggers.CheckPhases(c);
        }

        /// <summary>applyHeal(ctx, target, amount): HP up to the maximum; the receipt names what was asked and what was gained.</summary>
        public static double Heal(CombatState c, JObject target, double amount)
        {
            if (target == null || !target.Is(K.Alive)) return 0;
            var n = Math.Max(0, Math.Floor(amount));
            var gained = Math.Min(n, target.Num(K.MaxHp) - target.Num(K.Hp));
            target.Put(K.Hp, target.Num(K.Hp) + gained);
            c.Emit(E.Healed, Js.Obj(K.TargetId, target[K.Id], K.Amount, gained, K.Requested, n));
            AfterHpChange(c, target);
            return gained;
        }

        // ------------------------------------------------------------------ the hit

        /// <summary>The tactical receipt of the action a contact belongs to and the contact's index in it (web carrier.expansionContact).</summary>
        public sealed class Contact
        {
            public JObject Receipt;
            public int Index;
        }

        private static string Str(JToken t) => Js.IsStr(t) ? Js.Str(t) : null;

        /// <summary>
        /// applyProfileDamage(ctx, source, target, base, carrier): one hit, fully resolved. Without a planned contact the d20 is rolled here; with one
        /// the action's begin step has already rolled it. <paramref name="rangeContact"/> is the engagement contact the receipt's hit preview reads.
        /// </summary>
        public static double ApplyProfileDamage(CombatState c, JObject source, JObject target, double baseAmount, JObject carrier, Contact contact = null, JObject rangeContact = null, Func<int, int, int> draw = null)
        {
            if (contact == null)
            {
                var hit = CoreHitRoll.Resolve(c, source, target, carrier, new HitOptions { RangeContact = rangeContact, DamageType = CoreDamage.ProposalDamageType(c, carrier) }, draw);
                if (hit != null && hit[DK.Hit] != null && hit[DK.Hit].Type == JTokenType.Boolean && !hit.Is(DK.Hit)) return 0;
            }
            else if (target.Obj(CK.CombatCounter) != null) throw new NotSupportedException(DM.CounterNotPorted);
            var receipt = CoreDamage.ProfileDamageReceipt(c, source, target, baseAmount, carrier, rangeContact);
            if (contact != null)
            {
                var tactical = contact.Receipt;
                var amounts = tactical.Arr(DK.Amounts);
                tactical.Put(K.Amount, tactical.Num(K.Amount) + receipt.Num(K.Amount) - Js.Or0(amounts[contact.Index]));
                amounts[contact.Index] = Js.N(receipt.Num(K.Amount));
                var incomings = tactical.Arr(DK.Incomings) ?? (JArray)(tactical[DK.Incomings] = new JArray());
                while (incomings.Count <= contact.Index) incomings.Add(Js.Null());
                incomings[contact.Index] = receipt.Obj(DK.DamageReceipt)?.Arr(DK.Hits)?[0]?[DK.Incoming]?.DeepClone() ?? Js.N(receipt.Num(K.Amount));
                tactical.Put(DK.HpLoss, tactical.Or0(DK.HpLoss) + receipt.Num(DK.HpLoss));
                tactical.Put(K.Blocked, tactical.Or0(K.Blocked) + receipt.Num(K.Blocked));
                if (receipt.Is(DK.Evaded)) tactical[DK.Avoided] = true;
            }
            var before = target.Or0(K.Block);
            double poiseBefore = 0;
            target.Put(K.Block, receipt.Num(K.BlockRemaining));
            target.Put(CK.Barrier, Math.Max(0, target.Or0(CK.Barrier) - receipt.Num(DK.BarrierMitigated)));
            var ward = target.Obj(CK.PersistentWard);
            if (ward != null) ward.Put(K.Value, Math.Max(0, ward.Num(K.Value) - receipt.Num(DK.WardMitigated)));
            var poise = target.Obj(CK.PersistentPoise);
            if (poise != null)
            {
                poiseBefore = poise.Num(K.Value);
                poise.Put(K.Value, Math.Max(0, poise.Num(K.Value) - receipt.Num(DK.PoiseMitigated) - receipt.Or0(DK.PoiseDrain)));
            }
            target.Put(K.Hp, target.Num(K.Hp) - receipt.Num(DK.HpLoss));
            var payload = Js.Obj(K.SourceId, source?[K.Id] ?? Js.Null(), K.TargetId, target[K.Id]);
            if (carrier.Is(K.InstanceId))
            {
                payload[DK.CardInstanceId] = carrier[K.InstanceId].DeepClone();
                if (carrier[K.SourceHand] != null) payload[K.SourceHand] = carrier[K.SourceHand].DeepClone();
                if (carrier[K.GrantedBy] != null) payload[K.GrantedBy] = carrier[K.GrantedBy].DeepClone();
                if (carrier.Has(DK.UsedArmamentId)) payload[DK.UsedArmamentId] = carrier[DK.UsedArmamentId].DeepClone();
            }
            payload[K.Amount] = receipt[K.Amount].DeepClone();
            payload[K.Blocked] = receipt[K.Blocked].DeepClone();
            payload[DK.HpLoss] = receipt[DK.HpLoss].DeepClone();
            payload[DK.PoiseMitigated] = receipt[DK.PoiseMitigated].DeepClone();
            payload[DK.PoiseAbsorbed] = receipt[DK.PoiseMitigated].DeepClone();
            if (receipt.Is(DK.PoiseDrain)) payload[DK.PoiseDamage] = receipt[DK.PoiseDrain].DeepClone();
            payload[DK.WardMitigated] = receipt[DK.WardMitigated].DeepClone();
            payload[DK.DamageReceipt] = receipt[DK.DamageReceipt].DeepClone();
            payload[DK.Evaded] = receipt[DK.Evaded].DeepClone();
            payload[DK.Advantage] = receipt[DK.Advantage].DeepClone();
            payload[K.BlockRemaining] = target[K.Block].DeepClone();
            payload[DK.BarrierRemaining] = target[CK.Barrier]?.DeepClone();
            payload[DK.WardRemaining] = ward?[K.Value]?.DeepClone();
            payload[DK.PoiseRemaining] = poise?[K.Value]?.DeepClone();
            payload[K.IsAttack] = true;
            c.Emit(E.DamageDealt, payload);
            if (before > 0 && target.Num(K.Block) == 0) c.Emit(DK.BlockBroken, Js.Obj(K.SourceId, source?[K.Id], K.TargetId, target[K.Id]));
            if (receipt.Num(DK.HpLoss) > 0) c.Emit(E.HpLost, Js.Obj(K.TargetId, target[K.Id], K.Amount, receipt[DK.HpLoss], K.Cause, DV.CauseAttack));
            var poiseBroke = poiseBefore > 0 && poise?.Num(K.Value) == 0 && target.Is(K.Alive) && target.Num(K.Hp) > 0;
            if (poiseBroke) PoiseBreak(c, source, target);
            if (receipt.Is(DK.Interrupted) && !poiseBroke && target.Str(K.Kind) == V.Enemy && target.Is(K.Alive) && ClaimStagger(c, target)) StaggerEnemy(c, target, null);
            AfterHpChange(c, target, source != null ? Js.Obj(K.SourceId, source[K.Id]) : new JObject());
            return receipt.Num(K.Amount);
        }

        /// <summary>
        /// applyAttackDamage(ctx, source, target, base, tags, carrier): the proposal rules around one hit (a target that cannot be hit, the plan the
        /// outgoing and incoming status rules ask for), the hit itself, and what it leaves behind (reflect, an automatic reaction, spent protection).
        /// </summary>
        public static double ApplyAttackDamage(CombatState c, JObject source, JObject target, double baseAmount, JObject carrier, Contact contact = null, JObject rangeContact = null, Func<int, int, int> draw = null, JObject sharedDecisions = null)
        {
            if (!target.Is(K.Alive)) return 0;
            carrier = carrier ?? new JObject();
            if (contact?.Receipt?.Is(DK.Avoided) == true) return 0;
            var decisions = sharedDecisions ?? carrier.Obj(DK.ProposalTargetDecisions);
            var key = target.Str(K.Id);
            var cached = decisions?.Obj(key);
            var targetPlan = cached ?? CoreProposal.ResolveTargetPlan(c, source, target, carrier);
            if (cached == null)
            {
                if (decisions != null) decisions[key] = targetPlan;
                CoreProposal.ApplyPlan(c, new JObject { [DK.StatusChanges] = targetPlan[DK.StatusChanges]?.DeepClone() ?? new JArray() }, source, target, carrier);
            }
            if (!targetPlan.Is(DK.Allowed) || targetPlan.Is(DK.Avoided))
            {
                c.Emit(DK.ProposalHitAvoided, Js.Obj(K.SourceId, source?[K.Id], K.TargetId, target[K.Id], DK.Perception, !targetPlan.Is(DK.Allowed)));
                return 0;
            }
            var raw = CoreDamage.ProfileDamageReceipt(c, source, target, baseAmount, carrier, rangeContact, false).Num(DK.Raw);
            var plan = CoreProposal.HitPlan(c, source, target, raw, carrier);
            var hp = target.Num(K.Hp);
            var eventStart = c.EventLog.Count;
            var amount = plan.Num(DK.Healing) > 0 ? 0 : ApplyProfileDamage(c, source, target, baseAmount, carrier, contact, rangeContact, draw);
            if (plan.Num(DK.Healing) > 0) Heal(c, target, plan.Num(DK.Healing));
            var damaged = c.EventLog.Skip(eventStart).FirstOrDefault(e => e.Str(K.Type) == E.DamageDealt && e.Str(K.TargetId) == target.Str(K.Id));
            if (plan.Num(DK.Healing) > 0 || (raw > 0 && damaged != null && !damaged.Is(DK.Evaded) && !damaged.Is(DK.Immune)))
            {
                CoreProposal.ApplyPlan(c, plan, source, target, carrier, contact);
                CoreProposal.ApplyPlan(c, CoreProposal.AfterHitPlan(c, source, target, raw, Math.Max(0, hp - target.Num(K.Hp)), carrier), source, target, carrier, contact);
            }
            return amount;
        }
    }
}
