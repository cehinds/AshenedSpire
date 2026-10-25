using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

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

        private static bool Truthy(JToken token) => token != null && token.Type == JTokenType.Object;
    }
}
