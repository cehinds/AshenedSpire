using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CCM = Ashen.Generated.Core2CostMessages;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Whether a card in hand wants an enemy aimed at it right now (web engine/combat.js cardNeedsEnemyTargetNow, WP-C0 D-293): the printed face's effects, with the buildup a
    /// charge another card left for this one adds (a charge turns a self card into one that applies a status to an enemy). A play whose chosen face is friendly still takes an enemy
    /// when the printed face wants one.
    /// </summary>
    public static class CoreTargets
    {
        /// <summary>needsEnemyTarget(def): an immediate effect aims at `enemy` (a Counter's damage and Poise wait for the reply).</summary>
        public static bool NeedsEnemyTarget(CombatState c, JObject def)
        {
            var effects = Js.Items(def[K.Effects]).OfType<JObject>();
            if (CoreStances.ProfileFor(c, def).Str(CK.Maneuver) == CV.StanceCounter) effects = effects.Where(e => e.Str(K.Op) != Ashen.Generated.CombatOps.Damage && e.Str(K.Op) != Ashen.Generated.CombatOps.PoiseDamage);
            return effects.Any(e => e.Str(K.Target) == DV.TargetEnemy);
        }

        /// <summary>cardNeedsEnemyTargetNow(combat, cardInstanceId).</summary>
        public static bool CardNeedsEnemyTargetNow(CombatState c, string instanceId)
        {
            var inst = c.Piles.Hand.FirstOrDefault(card => card.Str(K.InstanceId) == instanceId)
                       ?? throw new System.InvalidOperationException(string.Format(CultureInfo.InvariantCulture, CCM.NotInHand, instanceId));
            var def = CoreCosts.Resolve(c, inst);
            var tags = Js.Items(def[CK.CardTags] ?? def[K.Tags]).Select(t => Js.IsStr(t) ? Js.Str(t) : Js.Str(Js.Get(t, K.Id))).ToList();
            var source = c;
            // With a listener, or work still queued, the web asks the exact preview: the card prepared on a detached copy.
            if (c.Queue.Count > 0 || CoreCardZones.HasChoice(c) || CoreCosts.ListensForPreparing(c, tags)) source = CorePreparing.Preview(c, inst, def, null);
            var enemyAvailable = source.Enemies.Any(e => e.Is(K.Alive));
            var charges = CoreRiders.Matching(source.Player, CoreCardPlay.Carrier(source, inst, def), enemyAvailable);
            var effects = CoreRiders.ChargedEffects(source, Js.Items(def[K.Effects]).OfType<JObject>().ToList(), charges, enemyAvailable);
            var charged = Js.Spread(def);
            charged[K.Effects] = new JArray(effects);
            return NeedsEnemyTarget(c, charged);
        }
    }
}
