using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using CV = Ashen.Generated.Core2CombatValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The action queue of a Core 2 fight (web engine/combat.js drainQueue and endCheck, engine/actions.js executeAction and the opcodes
    /// the fight start and the end-turn loop run): applyStatus, removeStatus, restoreMana, restoreStamina, draw, block and gainBarrier
    /// (which a configured fight resolves as Block). The damage, Poise, buildup and card opcodes arrive with WP-5 to WP-9; an action
    /// carrying one is refused by name, so a half-ported fight cannot run a card as if it were whole.
    /// </summary>
    public static class CoreActions
    {
        /// <summary>drainQueue(combat): execute the queue to empty, checking for the end of the fight after every action.</summary>
        public static void Drain(CombatState c)
        {
            var guard = 0;
            var limit = c.Data.Rule(K.Limits, K.QueueGuard);
            while (c.Queue.Count > 0)
            {
                if (++guard > limit) throw new InvalidOperationException(M.QueueDidNotDrain);
                if (CoreReactions.Before(c, c.Queue.First.Value)) return;
                var action = c.Queue.First.Value;
                c.Queue.RemoveFirst();
                CoreReactions.StartQueuedEnemy(c, action);
                Execute(c, action);
                if (CoreCardZones.HasChoice(c)) return;
                EndCheck(c);
                if (c.Result != null)
                {
                    c.Queue.Clear();
                    return;
                }
            }
            EndCheck(c);
        }

        /// <summary>endCheck(combat): the fight is over when the player or every enemy is down.</summary>
        public static void EndCheck(CombatState c)
        {
            if (c.Result != null || CoreExpansion.Pending(c) > 0 || c.PendingAbilityPlay != null) return;
            if (!c.Player.Is(K.Alive) || c.Player.Num(K.Hp) <= 0) Finish(c, V.Defeat);
            else if (c.Enemies.Count > 0 && c.Enemies.All(e => !e.Is(K.Alive))) Finish(c, V.Victory);
        }

        private static void Finish(CombatState c, string result)
        {
            c.Result = result;
            c.Phase = V.PhaseEnded;
            foreach (var field in new[] { DK.ReactionCursor, DK.PendingReaction, DK.ReactionResume, CK.ReactionHandCleanup, DK.ReactionStack, DK.ReactionRootPlay }) c.Fields.Remove(field);
            c.Fields[DK.PendingExpansionActions] = 0;
            c.Queue.Clear();
            c.Emit(E.CombatEnd, Js.Obj(K.Victory, result == V.Victory));
            c.Queue.Clear();
        }

        /// <summary>
        /// executeAction(ctx, action): a committed action begins when its first effect runs; then the effect's gate, its repeat count, then the opcode.
        /// </summary>
        public static void Execute(CombatState c, CombatAction action)
        {
            var eff = action.Effect;
            var outer = c.CurrentAction;
            c.CurrentAction = action;
            try { ExecuteBody(c, action, eff); }
            finally { c.CurrentAction = outer; }
        }

        private static void ExecuteBody(CombatState c, CombatAction action, JObject eff)
        {
            if (eff.Str(K.Op) == DV.OpCompleteCombatAction) { CoreExpansion.Complete(c, action); return; }
            if (c.Result != null) return;
            var group = action.Meta?.Group;
            var sequence = group?.ContactSequence;
            if (sequence != null && action.Meta.ContactEffectFirst == false && !(sequence.Gates.TryGetValue(action.Meta.ContactEffectIndex, out var open) && open)) return;
            if (group != null && group.Cancelled) return;
            if (group != null) CoreExpansion.Begin(c, group, action.Source);
            if (action.Meta?.CombatCounterReaction == true) throw new NotSupportedException(DM.CounterNotPorted);
            if (sequence != null && action.Meta.ContactEffectFirst == false)
            {
                eff = Js.Spread(eff);
                eff.Remove(K.If);
                eff.Remove(CK.OncePerTurn);
            }
            if (Js.Truthy(eff[CK.Script])) throw new NotSupportedException(CM.ScriptNotPorted);
            if (Js.Truthy(eff[K.If]))
            {
                var pctx = new PredicateContext { Owner = action.Owner ?? action.Source, Source = action.Source, Target = action.Target, Card = action.Card, Meta = action.Meta };
                if (!Triggers.EvalPredicate(c, eff.Obj(K.If), pctx)) return;
            }
            if (Js.Truthy(eff[CK.OncePerTurn]) && !CoreRiders.AllowOnce(action.Owner ?? action.Source ?? c.Player, eff[CK.OncePerTurn], action.Card,
                    action.Meta?.AbilityEffectIndex != null ? Js.N(action.Meta.AbilityEffectIndex.Value) : eff[K.Op])) return;
            if (sequence != null && action.Meta.ContactEffectFirst == true) sequence.Gates[action.Meta.ContactEffectIndex] = true;
            var repeat = Actions.EvalNum(c, action, eff[CK.Repeat], 1);
            var previous = c.CurrentAction;
            c.CurrentAction = action;
            try
            {
                for (var r = 0; r < repeat; r++)
                {
                    if (action.Meta != null) action.Meta.ExpansionExecutionRepeat = r;
                    RunOpcode(c, action, eff);
                    SpendSilenced(c, action, eff);
                    if (c.Result != null || CoreCardZones.HasChoice(c)) return;
                }
            }
            finally { c.CurrentAction = previous; }
        }

        /// <summary>spendSilencedForEffect: any other effect of a Force card spends a Silenced stack of what it reaches; a target that holds one is the status package's (WP-9).</summary>
        private static void SpendSilenced(CombatState c, CombatAction action, JObject eff)
        {
            var op = eff.Str(K.Op);
            if (action.Card == null || action.Meta?.StanceEntry == true || action.Meta?.StanceRestore == true || op == Op.Damage || op == DV.OpCompleteCombatAction || eff.Str(K.Target) == V.TargetRandomEnemy) return;
            if (!CoreDamage.IsForceHit(c, action.Card.Obj(CK.CombatProfile) ?? CoreStances.ProfileFor(c, action.Card))) return;
            var targets = Js.Nullish(eff[K.Target]) && CoreSets.OwnerOps.Contains(op) ? new List<JObject> { action.Owner ?? action.Source } : Actions.ResolveTargets(c, action, eff.Str(K.Target));
            foreach (var t in targets)
            {
                if (action.Meta?.Group != null && action.Meta.Group.Targets.TryGetValue(t.Str(K.Id), out var row) && row.Receipt?.Is(DK.Avoided) == true) continue;
                SpendSilencedStack(c, t);
            }
        }

        /// <summary>spendSilencedStack(ctx, target): a Force contact spends one Silenced stack; the log shows it as consumed.</summary>
        public static void SpendSilencedStack(CombatState c, JObject target)
        {
            if (target.Obj(K.Statuses)?.Obj(CV.StatusSilenced)?.Num(K.Stacks) > 0) CoreStatuses.Remove(c, target, CV.StatusSilenced, CV.ReasonConsumed, 1);
        }

        private static void RunOpcode(CombatState c, CombatAction action, JObject eff)
        {
            switch (eff.Str(K.Op))
            {
                case Op.ApplyStatus:
                    foreach (var t in PlannedStatusTargets(c, action, eff))
                    {
                        var authored = Actions.EvalNum(c, action, eff[K.Stacks], 1, t);
                        var stacks = authored + SpellAuthoredStatusBonus(c, action, authored) + (action.Meta?.Charge?.Or0(CV.OpBuildup) ?? 0);
                        var tags = Js.Items(eff[K.Tags]).Select(Js.Str).Concat(action.Card != null ? CoreStances.TagIds(action.Card) ?? new List<string>() : new List<string>()).ToList();
                        CoreStatuses.Apply(c, t, eff.Str(K.Status), stacks, action.Source, tags);
                        CoreImpact.ProposalThreshold(c, t);
                        if (action.Meta?.Group != null && action.Meta.Group.Targets.TryGetValue(t.Str(K.Id), out var statusRow) && statusRow.Receipt != null)
                            statusRow.Receipt[DK.StatusAccepted] = Js.N(statusRow.Receipt.Or0(DK.StatusAccepted) + stacks);
                        if (eff.Str(K.Status) == CV.StatusGrounded) CoreStatusControl.Ground(c, t);
                    }
                    break;
                case Op.RemoveStatus:
                    foreach (var t in Actions.ResolveTargets(c, action, eff.Str(K.Target)))
                    {
                        CoreStatuses.Remove(c, t, eff.Str(K.Status), CV.ReasonConsumed, eff[K.Amount] != null ? Math.Max(0, Actions.EvalNum(c, action, eff[K.Amount], 0, t)) : (double?)null);
                        CoreOps.TrimStances(c, t);
                    }
                    break;
                case Op.Draw:
                    CoreHand.Draw(c, Math.Max(0, Actions.EvalNum(c, action, eff[K.Amount], 1)));
                    break;
                case Op.RestoreStamina:
                    foreach (var t in Actions.ResolveTargets(c, action, eff.Str(K.Target)))
                    {
                        var amount = Math.Max(0, Math.Min(t.Num(K.MaxStamina) - t.Num(K.Stamina), Math.Max(0, Actions.EvalNum(c, action, eff[K.Amount], 1))));
                        t.Put(K.Stamina, t.Num(K.Stamina) + amount);
                        c.Emit(E.StaminaRecovered, Js.Obj(K.TargetId, t[K.Id], K.Amount, amount, K.Reason, V.ReasonEffect));
                    }
                    break;
                case Op.RestoreMana:
                    RestoreMana(c, action, eff);
                    break;
                case Op.Damage:
                    RunDamage(c, action, eff);
                    break;
                case Op.PoiseDamage:
                    RunPoiseDamage(c, action, eff);
                    break;
                case Op.Block:
                case CV.OpGainBarrier:
                    CoreOps.Protection(c, action, eff);
                    break;
                default:
                    if (CoreOps.Run(c, action, eff)) break;
                    throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, CM.OpcodeNotPorted, eff.Str(K.Op)));
            }
        }

        // ------------------------------------------------------------------ committed hits

        /// <summary>spellAuthoredStatusBonus(ctx, action, authored): a spell stance adds its stacks to a status a committed card or move authors.</summary>
        private static double SpellAuthoredStatusBonus(CombatState c, CombatAction action, double authored)
        {
            if (action.Card == null || (action.Meta?.Group == null) || action.Meta.StanceEntry || !(authored > 0)) return 0;
            var stance = action.Source?.Obj(CK.CombatStance);
            var eligible = CoreSets.CastingManeuvers.Contains(stance?.Str(CK.Maneuver)) || stance?.Str(CK.Maneuver) == CV.StanceCounter && stance.Str(CK.Camp) == CV.CampSpell;
            return eligible ? CoreStances.Rules(c).Num(DK.SpellStatusStacks) : 0;
        }

        /// <summary>plannedStatusTargets(ctx, action, eff): the creatures the committed plan reached, less the ones that avoided the action.</summary>
        private static List<JObject> PlannedStatusTargets(CombatState c, CombatAction action, JObject eff)
        {
            var meta = action.Meta;
            if (meta?.ExpansionTargets == null || meta.StatusTargetCursor >= meta.ExpansionTargets.Count) return Actions.ResolveTargets(c, action, eff.Str(K.Target));
            var refs = meta.ExpansionTargets[meta.StatusTargetCursor++];
            return refs.Select(id => Triggers.FindEntity(c, id)).Where(e => e != null && e.Is(K.Alive)
                && !(meta.Group != null && meta.Group.Targets.TryGetValue(e.Str(K.Id), out var row) && row.Receipt?.Is(DK.Avoided) == true)).ToList();
        }

        /// <summary>The damage op of a committed action: one hit per planned contact, each through the attack chain.</summary>
        private static void RunDamage(CombatState c, CombatAction action, JObject eff)
        {
            action.Meta ??= new ActionMeta();
            var hits = Math.Max(0, Actions.EvalNum(c, action, eff[K.Hits], 1));
            for (var h = 0; h < hits; h++)
            {
                List<ContactRef> planned = null;
                if (action.Meta.ExpansionContacts != null && action.Meta.ExpansionCursor < action.Meta.ExpansionContacts.Count) planned = action.Meta.ExpansionContacts[action.Meta.ExpansionCursor++];
                var targets = planned != null ? planned.Select(contact => Triggers.FindEntity(c, contact.Id)).Where(e => e != null).ToList() : Actions.ResolveTargets(c, action, eff.Str(K.Target));
                if (eff.Str(K.Target) == DV.TargetAllEnemies && action.Card != null && action.Card[DK.TargetLimit] != null)
                    targets = targets.Take((int)Math.Max(0, Actions.EvalNum(c, action, action.Card[DK.TargetLimit], targets.Count))).ToList();
                foreach (var t in targets)
                {
                    if (!t.Is(K.Alive)) continue;
                    var baseAmount = Actions.EvalNum(c, action, eff[CK.Amount], 0, t);
                    var effectCarrier = CoreExpansion.EffectCarrier(c, action.Card, eff);
                    var carrier = Js.Spread(effectCarrier);
                    if (eff.Is(K.Attack)) carrier[K.Attack] = eff[K.Attack].DeepClone();
                    carrier[CK.DamageSchool] = (Js.Truthy(eff[CK.DamageSchool]) ? eff[CK.DamageSchool] : action.Card?[CK.DamageSchool])?.DeepClone();
                    carrier[K.Tags] = (action.Card?[K.Tags] is JArray ? action.Card[K.Tags] : Actions.AttackTagsFor(c, action, eff))?.DeepClone();
                    carrier[CK.CombatProfile] = effectCarrier[CK.CombatProfile].DeepClone();
                    carrier[CK.CombatRiderTargets] = new JArray();
                    carrier[DK.EnergySpent] = Js.N(action.Meta.EnergySpent ?? 0);
                    CoreImpact.Contact contact = null;
                    if (planned != null)
                    {
                        var planRef = planned.First(r => r.Id == t.Str(K.Id));
                        contact = new CoreImpact.Contact { Receipt = action.Meta.Group.Targets[planRef.Key].Receipt, Index = planRef.Index };
                        if (planRef.Crit != null) throw new NotSupportedException(DM.CritNotPorted);
                    }
                    if (c.RatingsRules != null && action.Source.Str(K.Kind) == V.Enemy)
                    {
                        var key = action.Source.Str(K.EnemyId) + DV.KeySeparator + (Js.Truthy(action.Meta.MoveId) ? action.Meta.MoveId : action.Source.Obj(K.Intent)?.Str(K.MoveId));
                        var attackType = c.RatingsRules.Obj(DK.EnemyAttackType)?.Str(key);
                        if (attackType != null && attackType != DV.AttackTypeAuto) carrier[CK.DamageSchool] = attackType;
                    }
                    if (action.Source.Obj(CK.CritRules)?.Count > 0 && planned == null) throw new NotSupportedException(DM.CritNotPorted);
                    CoreImpact.ApplyAttackDamage(c, action.Source, t, baseAmount, carrier, contact, CoreRange.HitContact(c, action.Source, t, carrier), null, action.Meta.ProposalTargetDecisions ??= new JObject());
                }
            }
        }

        /// <summary>The Poise damage op: the hit's Poise, scaled by the matchup and the stance pressure, drains the target's pool.</summary>
        private static void RunPoiseDamage(CombatState c, CombatAction action, JObject eff)
        {
            foreach (var t in Actions.ResolveTargets(c, action, eff.Str(K.Target)))
            {
                TargetRow row = null;
                action.Meta?.Group?.Targets.TryGetValue(t.Str(K.Id), out row);
                if (row?.Receipt?.Is(DK.Avoided) == true) continue;
                if (SplitSmashPressure(action, t, row)) throw new NotSupportedException(DM.SmashNotPorted);
                var multiplier = CoreTactical.MatchupPoiseMultiplier(c, action.Source, t, action.Card);
                var authored = Actions.EvalNum(c, action, eff[CK.Amount], 0, t);
                var amount = action.Card?.Is(DK.CounterReturnAttack) == true ? Math.Max(0, Math.Floor(authored))
                    : Math.Ceiling(authored * multiplier * CoreTactical.StancePressureMultiplier(c, action.Source, t, action.Card, row?.Receipt?.Obj(DK.CounterAttack)));
                CoreImpact.DealPoiseDamage(c, t, amount);
            }
        }

        private static bool SplitSmashPressure(CombatAction action, JObject target, TargetRow row) =>
            action.Card?.Obj(CK.CombatProfile)?.Str(CK.Maneuver) == CV.StanceSmash && CoreSets.CastingManeuvers.Contains(target.Obj(CK.CombatStance)?.Str(CK.Maneuver)) && row != null;

        private static void RestoreMana(CombatState c, CombatAction action, JObject eff)
        {
            var toFloor = eff[CK.ToFloorPct] != null;
            var funded = Js.IsFinite(eff[CK.Funded]) ? eff.Num(CK.Funded) : 0;
            var n = toFloor ? 0 : Math.Max(0, Actions.EvalNum(c, action, eff[K.Amount], 1) - funded);
            var pct = toFloor ? Math.Max(0, Actions.EvalNum(c, action, eff[CK.ToFloorPct], 0)) : 0;
            foreach (var t in Actions.ResolveTargets(c, action, eff.Str(K.Target)))
            {
                var before = t.Num(K.Mana);
                if (toFloor)
                {
                    var floor = Math.Min(t.Num(K.MaxMana), Math.Floor(t.Num(K.MaxMana) * pct / Ashen.Generated.CombatMath.Percent));
                    t.Put(K.Mana, before >= floor ? t.Num(K.MaxMana) : floor);
                }
                else t.Put(K.Mana, Math.Min(t.Num(K.MaxMana), t.Num(K.Mana) + n));
                c.Emit(E.ManaRestored, Js.Obj(K.TargetId, t[K.Id], K.Amount, t.Num(K.Mana) - before));
            }
        }

        // ------------------------------------------------------------------ Block

        /// <summary>stanceEffectiveness(ctx, source, carrier): the matching-stance multiplier of a card or move played in the stance already held.</summary>
        public static double StanceEffectiveness(CombatState c, JObject source, JObject carrier)
        {
            if (!CoreFixCore.Enabled(c)) return 1;
            var same = carrier != null && carrier[CK.FixCoreSameStance] != null ? carrier.Is(CK.FixCoreSameStance) : CoreStances.SameStance(c, source, carrier);
            return same ? CoreFixCore.Family(c, CK.Stance, source).Num(CK.EffectivenessMultiplier) : 1;
        }

        /// <summary>computeBlockGain (configured): the base plus Defense scaled by the profile's weight, times the matching-stance multiplier, floored.</summary>
        public static double ComputeBlockGain(CombatState c, JObject entity, double baseAmount, JObject card)
        {
            var defenses = c.CoreData.Configured(CK.DefensesModule);
            if (defenses == null || !Js.Includes(defenses[CK.Include], CK.Block)) return 0;
            var dr = CoreFixCore.Enabled(c) ? CorePools.RatingValue(c, entity, CK.RatingDr) : Js.Or0(entity.Obj(CK.Ratings)?[CK.RatingDr]);
            var weight = Js.Or0(c.CoreData.Configured(CK.EffectsModule)?[CK.DefenseRatingWeight]);
            return Math.Max(0, Math.Floor((baseAmount + dr * weight) * StanceEffectiveness(c, entity, card)));
        }

        /// <summary>gainBlock(ctx, entity, base, card): a configured fight's Block (Barrier included), capped by blockCap, refilling Poise.</summary>
        public static double GainBlock(CombatState c, JObject entity, double baseAmount, JObject card)
        {
            if (!entity.Is(K.Alive)) return 0;
            var fixCore = CoreFixCore.Enabled(c);
            if (!fixCore) throw new NotSupportedException(CM.LegacyBlockNotPorted);
            var synthetic = card != null && card.Is(CK.SyntheticDefense);
            // A guard no card raised (a status, a stance's turn-start protection) is the amount whole and lays down no Ward or Poise.
            if (synthetic || card == null || !(Js.Truthy(card[K.CardId]) || Js.Truthy(card[K.MoveId]))) return CoreOps.GainBlockAmount(c, entity, ComputeBlockGain(c, entity, baseAmount, card), card);
            var amt = ComputeBlockGain(c, entity, baseAmount, card);
            var cap = StatusCap(c, entity, K.BlockCap);
            if (cap != null && entity.Num(K.Block) + amt > cap.Value) amt = Math.Max(0, cap.Value - entity.Num(K.Block));
            entity.Put(K.Block, entity.Num(K.Block) + amt);
            if (amt > 0)
            {
                CorePools.SyncWard(c, entity);
                CorePools.SyncPoise(c, entity);
                if (!CoreFixCore.WardDisabled(c)) CoreControl.RestoreWard(c, entity, amt, Ashen.Generated.Core2DamageValues.SourceBlockCard, 0);
                CoreControl.RestorePoise(c, entity, amt, Ashen.Generated.Core2DamageValues.SourceBlockCard);
            }
            var payload = Js.Obj(K.TargetId, entity[K.Id], K.Amount, amt);
            if (card != null && Js.Truthy(card[Ashen.Generated.Core2DamageKeys.LogSource])) payload[CK.Source] = card[Ashen.Generated.Core2DamageKeys.LogSource].DeepClone();
            if (card != null && Js.Truthy(card[K.InstanceId]))
            {
                payload[Ashen.Generated.Core2DamageKeys.CardInstanceId] = card[K.InstanceId].DeepClone();
                if (card[K.SourceHand] != null) payload[K.SourceHand] = card[K.SourceHand].DeepClone();
                if (card[K.GrantedBy] != null) payload[K.GrantedBy] = card[K.GrantedBy].DeepClone();
                if (card.Has(Ashen.Generated.Core2DamageKeys.UsedArmamentId)) payload[Ashen.Generated.Core2DamageKeys.UsedArmamentId] = card[Ashen.Generated.Core2DamageKeys.UsedArmamentId].DeepClone();
            }
            c.Emit(E.BlockGained, payload);
            return amt;
        }

        public static double? StatusCap(CombatState c, JObject entity, string key)
        {
            double? cap = null;
            var statuses = entity.Obj(K.Statuses);
            if (statuses == null) return null;
            foreach (var id in statuses.Properties().Select(p => p.Name).ToList())
            {
                var mods = CoreStatuses.Definition(c, id).Obj(K.Modifiers);
                if (Js.IsNum(mods?[key])) cap = cap == null ? mods.Num(key) : Math.Max(cap.Value, mods.Num(key));
            }
            return cap;
        }
    }
}
