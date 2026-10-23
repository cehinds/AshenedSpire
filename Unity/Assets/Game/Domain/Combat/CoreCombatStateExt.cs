using Newtonsoft.Json.Linq;

namespace Ashen.Domain.Combat
{
    /// <summary>The runtime fields the card being played keeps while a choice is pending (never saved; web <c>pendingAbilityPlay</c> holds them by reference).</summary>
    public sealed partial class CombatState
    {
        /// <summary>The instance the card play resolves (the hand's own object, which then goes to its pile).</summary>
        public JObject PendingPlayInstance;

        /// <summary>The carrier the card was played with (its tags, ability kind and the armament it used).</summary>
        public JObject PendingPlayRef;

        /// <summary>The snapshot taken before the play (<c>abilityBefore</c>, <c>abilityEntities</c>, <c>cardTargetsAllEnemies</c>), which the card's own events carry.</summary>
        public JObject PendingPlayBefore;
    }
}
