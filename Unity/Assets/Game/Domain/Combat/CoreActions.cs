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
                var action = c.Queue.First.Value;
                c.Queue.RemoveFirst();
                Execute(c, action);
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
            if (c.Result != null) return;
            if (!c.Player.Is(K.Alive) || c.Player.Num(K.Hp) <= 0) Finish(c, V.Defeat);
            else if (c.Enemies.Count > 0 && c.Enemies.All(e => !e.Is(K.Alive))) Finish(c, V.Victory);
        }

        private static void Finish(CombatState c, string result)
        {
            c.Result = result;
            c.Phase = V.PhaseEnded;
            c.Queue.Clear();
            c.Emit(E.CombatEnd, Js.Obj(K.Victory, result == V.Victory));
            c.Queue.Clear();
        }

        /// <summary>executeAction(ctx, action): the effect's gate, its repeat count, then the opcode.</summary>
        public static void Execute(CombatState c, CombatAction action)
        {
            if (c.Result != null) return;
            var eff = action.Effect;
            if (Js.Truthy(eff[CK.Script])) throw new NotSupportedException(CM.ScriptNotPorted);
            if (Js.Truthy(eff[K.If]))
            {
                var pctx = new PredicateContext { Owner = action.Owner ?? action.Source, Source = action.Source, Target = action.Target, Card = action.Card, Meta = action.Meta };
                if (!Triggers.EvalPredicate(c, eff.Obj(K.If), pctx)) return;
            }
            if (Js.Truthy(eff[CK.OncePerTurn])) throw new NotSupportedException(CM.OncePerTurnNotPorted);
            var repeat = Actions.EvalNum(c, action, eff[CK.Repeat], 1);
            for (var r = 0; r < repeat; r++)
            {
                RunOpcode(c, action, eff);
                if (c.Result != null) return;
            }
        }

        private static void RunOpcode(CombatState c, CombatAction action, JObject eff)
        {
            switch (eff.Str(K.Op))
            {
                case Op.ApplyStatus:
                    foreach (var t in Actions.ResolveTargets(c, action, eff.Str(K.Target)))
                    {
                        var authored = Actions.EvalNum(c, action, eff[K.Stacks], 1, t);
                        var tags = Js.Items(eff[K.Tags]).Select(Js.Str).Concat(action.Card != null ? CoreStances.TagIds(action.Card) ?? new List<string>() : new List<string>()).ToList();
                        CoreStatuses.Apply(c, t, eff.Str(K.Status), authored, action.Source, tags);
                    }
                    break;
                case Op.RemoveStatus:
                    foreach (var t in Actions.ResolveTargets(c, action, eff.Str(K.Target)))
                        CoreStatuses.Remove(c, t, eff.Str(K.Status), CV.ReasonConsumed, eff[K.Amount] != null ? Math.Max(0, Actions.EvalNum(c, action, eff[K.Amount], 0, t)) : (double?)null);
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
                case Op.Block:
                case CV.OpGainBarrier:
                    foreach (var t in Actions.ResolveTargets(c, action, eff.Str(K.Target)))
                        GainBlock(c, t, Actions.EvalNum(c, action, eff[K.Amount], 0, t), action.Card);
                    break;
                default:
                    throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, CM.OpcodeNotPorted, eff.Str(K.Op)));
            }
        }

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
            if (synthetic || card == null || !(Js.Truthy(card[K.CardId]) || Js.Truthy(card[K.MoveId]))) throw new NotSupportedException(CM.SyntheticBlockNotPorted);
            var amt = ComputeBlockGain(c, entity, baseAmount, card);
            var cap = CoreStatusCap(c, entity, K.BlockCap);
            if (cap != null && entity.Num(K.Block) + amt > cap.Value) amt = Math.Max(0, cap.Value - entity.Num(K.Block));
            entity.Put(K.Block, entity.Num(K.Block) + amt);
            if (amt > 0)
            {
                CorePools.SyncWard(c, entity);
                CorePools.SyncPoise(c, entity);
                if (!CoreFixCore.WardDisabled(c)) throw new NotSupportedException(CM.WardRestoreNotPorted);
                CoreControl.RestorePoise(c, entity, amt, CV.SourceBlockCard, false);
            }
            c.Emit(E.BlockGained, Js.Obj(K.TargetId, entity[K.Id], K.Amount, amt));
            return amt;
        }

        private static double? CoreStatusCap(CombatState c, JObject entity, string key)
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
