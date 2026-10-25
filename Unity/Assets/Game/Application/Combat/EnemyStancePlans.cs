using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.App.Combat
{
    /// <summary>
    /// The public action category of an enemy's intent, the frame its figure holds (src/model/enemyStancePose.js, model/combatIntentVisibility.js combatIntentStance). The category is what the intent
    /// announces even while the move itself is concealed: visibility, the move's identity and its numbers never select artwork.
    /// </summary>
    public static class EnemyStancePlans
    {
        /// <summary>combatIntentStance: the stance an intent announces.</summary>
        public static string Stance(JObject intent, JObject profile)
        {
            var kind = intent.Str(EnemyIntentNames.Kind);
            var maneuver = profile.Str(EnemyIntentNames.Maneuver);
            var camp = profile.Str(EnemyIntentNames.Camp);
            var stance = intent.Str(EnemyIntentNames.Stance);
            if (!string.IsNullOrEmpty(stance)) return stance;
            if (kind == EnemyIntentNames.KindStaggered) return EnemyIntentNames.StanceStaggered;
            if (maneuver == EnemyIntentNames.ManeuverCounter) return EnemyIntentNames.StanceCountering;
            if (maneuver == EnemyIntentNames.ManeuverDefend) return EnemyIntentNames.StanceDefending;
            if (maneuver == EnemyIntentNames.ManeuverSweep) return EnemyIntentNames.StanceSweeping;
            if (camp == EnemyIntentNames.CampSpell || camp == EnemyIntentNames.CampSpells || camp == EnemyIntentNames.CampMagic) return EnemyIntentNames.StanceCasting;
            if (maneuver == EnemyIntentNames.ManeuverRanged) return EnemyIntentNames.StanceRanged;
            if (maneuver == EnemyIntentNames.ManeuverSmash) return EnemyIntentNames.StanceSmashing;
            if (kind == EnemyIntentNames.KindBlock || kind == EnemyIntentNames.KindDefend) return EnemyIntentNames.StanceDefending;
            if (maneuver == EnemyIntentNames.ManeuverAttack || kind == EnemyIntentNames.KindAttack || kind == EnemyIntentNames.KindAttackDebuff) return EnemyIntentNames.StanceAttacking;
            return EnemyIntentNames.StancePreparing;
        }

        /// <summary>enemyStanceCategory: the action category (smash, attack, counter, sweep, defend, preparing) of an intent, or null when it announces none.</summary>
        public static string Category(EnemyStageData data, JObject intent)
        {
            if (intent == null) return null;
            var profile = Truthy(intent[EnemyIntentNames.CombatProfile]) ? intent.Obj(EnemyIntentNames.CombatProfile) : Truthy(intent[EnemyIntentNames.Profile]) ? intent.Obj(EnemyIntentNames.Profile) : new JObject();
            var kind = intent.Str(EnemyIntentNames.Kind);
            if (string.IsNullOrEmpty(intent.Str(EnemyIntentNames.Stance)) && (string.IsNullOrEmpty(kind) || kind == EnemyIntentNames.KindUnknown)
                && string.IsNullOrEmpty(profile.Str(EnemyIntentNames.Camp)) && string.IsNullOrEmpty(profile.Str(EnemyIntentNames.Maneuver))) return null;
            return data.Category(Stance(intent, profile));
        }

        /// <summary>
        /// The category a move announces once the enemy starts it (ui/screens/combat.js applyBeatToDisp, enemyMoveStarted): enemyStanceCategory of the move's kind and the combat profile of its carrier
        /// (enemyMoveCarrier). Null for a move the enemy does not have.
        /// </summary>
        public static string ForMove(EnemyStageData data, CombatState c, JObject enemy, string moveId, string kind)
        {
            if (data == null || c == null || enemy == null || moveId == null) return null;
            var def = c.Data.Enemies.Has(enemy.Str(K.EnemyId)) ? c.Data.Enemies.Get(enemy.Str(K.EnemyId)) : null;
            var move = def?.Obj(K.Moves)?.Obj(moveId);
            if (move == null) return null;
            var carrier = CoreIntents.Carrier(c, enemy, move, moveId);
            var intent = new JObject { [EnemyIntentNames.Kind] = !string.IsNullOrEmpty(kind) ? kind : move.Str(K.Intent), [EnemyIntentNames.CombatProfile] = carrier.Obj(CK.CombatProfile) };
            return Category(data, intent);
        }

        private static bool Truthy(JToken token) => token != null && token.Type == JTokenType.Object;
    }
}
