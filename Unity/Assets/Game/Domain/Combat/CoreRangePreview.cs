using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using SK = Ashen.Generated.Core2StatusKeys;
using SV = Ashen.Generated.Core2StatusValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The range quote the card face shows (web engine/combat.js previewCard and previewCardHit, the <c>range</c> they embed): every route a card can take to
    /// its targets, what the approach costs and the odds of each contact before and after it. A preview plans on a copy of the fight and changes nothing.
    /// </summary>
    public static class CoreRangePreview
    {
        private static CoreRange.HitPreview CardHit => (c, source, target, carrier) => CoreHitRoll.PreviewCard(c, source, target, carrier);

        /// <summary>previewCardHit(combat, id, targetId, { rangeRoute }).range: the routes of a card's attack to one enemy (null without a range state, a card outside the hand or no such enemy).</summary>
        public static JObject Hit(CombatState c, string instanceId, string targetId, string route = null)
        {
            var inst = c.Piles.Hand.FirstOrDefault(card => card.Str(K.InstanceId) == instanceId);
            var target = c.Enemies.FirstOrDefault(enemy => enemy.Str(K.Id) == targetId);
            if (inst == null || target == null) return null;
            var def = CoreCosts.Resolve(c, inst);
            var carrier = CoreCardPlay.Carrier(c, inst, def);
            var probe = new CombatAction
            {
                Source = c.Player, Target = target, Card = carrier, Effect = new JObject { [K.Op] = Op.Damage, [K.Target] = DV.TargetEnemy, [CK.Amount] = 0 }, Meta = ActionMeta.Empty(),
            };
            return CoreRange.PreviewRoutes(c, c.Player, new List<CombatAction> { probe }, CoreCosts.PlayCosts(c, def), route ?? DV.RouteAdvance, CardHit);
        }

        /// <summary>
        /// previewCard(combat, id, targetId, { rangeRoute }).range: the routes of the card's whole play, planned the way the play plans it (its charged effects,
        /// the stance it enters, the piece each attack is dealt with) on a detached copy of the fight. Null without a range state.
        /// </summary>
        public static JObject Card(CombatState live, string instanceId, string targetId, string route = null)
        {
            if (live.Fields.Obj(CK.RangeState) == null) return null;
            // previewCard plans on the fight as the player sees it: an enemy not read shows no stance, Counter or pending move.
            var c = CoreVisibility.VisibleCopy(live);
            var player = c.Player;
            var inst = c.Piles.Hand.First(card => card.Str(K.InstanceId) == instanceId);
            var printed = CoreCosts.Resolve(c, inst);
            var paid = CoreCosts.CardPlayCosts(c, instanceId);
            var variantTarget = targetId != null ? Triggers.FindEntity(c, targetId) : null;
            var def = CoreVariants.Select(c, printed, paid, variantTarget);
            var effects = Js.Items(def[K.Effects]).OfType<JObject>().ToList();
            var maneuver = CoreStances.ProfileFor(c, def).Str(CK.Maneuver);
            var immediate = maneuver == CV.StanceCounter ? effects.Where(e => e.Str(K.Op) != Op.Damage && e.Str(K.Op) != Op.PoiseDamage).ToList() : effects;
            var living = c.Enemies.Where(e => e.Is(K.Alive)).ToList();
            var target = targetId != null ? variantTarget : immediate.Any(e => e.Str(K.Target) == DV.TargetEnemy) ? living.FirstOrDefault() : null;
            var cardRef = CoreCardPlay.Carrier(c, inst, def);
            CoreStances.SetProfileStance(c, player, cardRef);
            var enemyAvailable = living.Count > 0;
            var charges = CoreRiders.Matching(player, cardRef, enemyAvailable);
            var charged = CoreRiders.ChargedEffects(c, effects, charges, enemyAvailable);
            if (!ReferenceEquals(charged, effects)) target ??= living.FirstOrDefault();
            var prepared = maneuver == CV.StanceCounter ? charged : CoreCardTraits.CounterstrikeEffects(c, player, target, cardRef, charged, false);
            var meta = new ActionMeta { EnergySpent = paid.Num(K.Stamina), ManaSpent = paid.Num(K.Mana), StaminaSpent = paid.Num(K.Stamina) };
            var actions = prepared.Select(effect => new CombatAction { Effect = effect, Source = player, Owner = player, Target = target, Card = cardRef, Meta = meta.Clone() }).ToList();
            return CoreRange.PreviewRoutes(c, player, actions, paid, route ?? DV.RouteAdvance, CardHit);
        }
    }
}
