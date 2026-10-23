using Newtonsoft.Json.Linq;

namespace Ashen.Domain.Combat
{
    /// <summary>The runtime fields the Core 2 statuses and effect ops add to a fight (never saved; the fight's saved fields live in <see cref="CombatState.Fields"/>).</summary>
    public sealed partial class CombatState
    {
        /// <summary>
        /// The snapshot (<c>abilityBefore</c>, <c>abilityEntities</c>, <c>cardTargetsAllEnemies</c>) taken before the card being played: the web carries
        /// it on the payload of the card's own events and on the actions' metadata; the rider predicates read it from here while the play runs.
        /// </summary>
        public JObject EventBefore;
    }
}
