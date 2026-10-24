using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;
using SK = Ashen.Generated.Core2StatusKeys;
using SV = Ashen.Generated.Core2StatusValues;
using SM = Ashen.Generated.Core2StatusMessages;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What a Core 2 fight adds to the formula evaluator (web engine/actions.js formulaCtxFor, model/formulas.js <c>metric</c>, engine/cardBehavior.js
    /// weaponDamageBasis): the weapon damage a card scales with, the card being played, the books before the play, the resources paid and the Evade a
    /// play grants, and the combat metrics a formula may read.
    /// </summary>
    public static class CoreFormulas
    {
        /// <summary>weaponDamageBasis(ctx, actor): the damage of the profile's basic strike plus the actor's Attack rating.</summary>
        public static double WeaponDamage(CombatState c, JObject actor)
        {
            var traits = c.CoreData.Section(CK.Traits);
            var strike = Js.Items(c.CoreData.Content(Ashen.Generated.Core2ProfileContentKeys.Cards)).OfType<JObject>().FirstOrDefault(card => card.Str(K.Id) == traits.Str(SK.StrikeCardId));
            var authored = Js.Items(strike?[K.Effects]).OfType<JObject>().FirstOrDefault(e => e.Str(K.Op) == Op.Damage)?[K.Amount];
            var amount = Js.Truthy(authored) ? Js.D(authored) : traits.Num(SK.FallbackStrikeDamage);
            return Math.Max(0, amount + Js.Or0(actor?.Obj(CK.Ratings)?[CK.RatingAr]));
        }

        /// <summary>The Core part of formulaCtxFor: the fields the web adds to a formula context.</summary>
        public static void Extend(CombatState c, CombatAction action, FormulaContext ctx)
        {
            var meta = action.Meta;
            ctx.WeaponDamage = WeaponDamage(c, action.Source);
            ctx.CardId = action.Card?.Str(K.CardId);
            ctx.MetricsBefore = meta?.Before?.Obj(SK.AbilityBefore);
            ctx.ManaSpent = meta?.ManaSpent ?? 0;
            ctx.StaminaSpent = meta?.StaminaSpent ?? 0;
            ctx.EvadeStackGain = CoreHitRoll.EvadeStackGain(c, action.Source, action.Card);
        }

        /// <summary>The <c>metric</c> formula: one of the combat metrics, times <c>per</c> (default 1).</summary>
        public static double Metric(FormulaContext ctx, JObject node, JObject entity)
        {
            var of = node.Str(K.Of);
            var state = (of == null || of == V.TargetSelf || of == V.RefOwner) && ctx.MetricsBefore != null ? ctx.MetricsBefore : entity.Obj(CK.AbilityRiders) ?? new JObject();
            var listed = Js.Items(node[K.Statuses]).Select(Js.Str).ToList();
            var active = (entity.Obj(K.Statuses) ?? new JObject()).Properties().Where(p => p.Value is JObject row
                && ((Js.Truthy(row[K.Stacks]) ? Js.D(row[K.Stacks]) : Js.Or0(row.Obj(K.Meter)?[K.Value])) > 0) && listed.Contains(p.Name)).Select(p => (JObject)p.Value).ToList();
            double Held(JObject row) => Js.Truthy(row[K.Stacks]) ? Js.D(row[K.Stacks]) : Js.Or0(row.Obj(K.Meter)?[K.Value]);
            double value;
            switch (node.Str(SK.Metric))
            {
                case DK.WeaponDamage: value = ctx.WeaponDamage; break;
                case SV.NegativeStatusTypes: value = active.Count; break;
                case SV.NegativeStatusStacks: value = active.Sum(Held); break;
                case SV.DamageTakenThisTurn: value = state.Or0(DK.HpLostSinceTurnStart); break;
                case CK.SameCardPlays: value = state.Obj(CK.CardUsesCombat)?.Or0(ctx.CardId ?? string.Empty) ?? 0; break;
                case CK.CardsPlayed: value = state.Or0(DK.CardsPlayed); break;
                case SV.DistinctTypes: value = state.Obj(DK.TypePlays)?.Properties().Count() ?? 0; break;
                case K.ManaSpent: value = ctx.ManaSpent; break;
                case K.StaminaSpent: value = ctx.StaminaSpent; break;
                case SV.EvadeStackGain: value = double.IsNaN(ctx.EvadeStackGain) ? 0 : ctx.EvadeStackGain; break;
                default: throw new InvalidOperationException(SM.UnknownMetric);
            }
            return value * (Js.Nullish(node[K.Per]) ? 1 : node.Num(K.Per));
        }
    }
}
