using Newtonsoft.Json.Linq;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What the fight asks of the run's equipment model (us-5.11). The shipped engine/combat.js imports model/loadout.js
    /// for its two mid-fight equipment intents and for createCombat's profile-snapshot fallback; here that model lives
    /// in Ashen.Domain.Run (it restamps piles through the run's deck-composition port), so the combat engine reaches
    /// it through this seam. <c>RunData</c> attaches its implementation to its <see cref="CombatData"/>; a fight built
    /// on combat data alone has none, and the intents then throw by name.
    /// </summary>
    public interface IEquipmentPort
    {
        /// <summary>createEquipmentProfileRuleSnapshot(registries): the host rows for a player handed without the run's own.</summary>
        JObject CreateProfileSnapshot();

        /// <summary>registries.derivedStatRules: the live table the unrated Poise receipt falls back on when a fight carries no rule snapshot.</summary>
        JObject DerivedStatRules { get; }

        /// <summary>doSwapArmament: cycle a hand to another of its sets, charge the price and restamp every pile. Throws the shipped refusal.</summary>
        void SwapArmament(CombatState c, string slotId, int setIndex);

        /// <summary>doChangeEquipment: replace, move or empty one position, charge the price and restamp. Throws the shipped refusal.</summary>
        void ChangeEquipment(CombatState c, string slotId, int setIndex, string pieceId);

        /// <summary>The refusal <see cref="SwapArmament"/> would throw, asked without mutating (null when legal).</summary>
        Refusal CheckSwap(CombatState c, string slotId, int setIndex);

        /// <summary>The refusal <see cref="ChangeEquipment"/> would throw, asked without mutating (null when legal).</summary>
        Refusal CheckChange(CombatState c, string slotId, int setIndex, string pieceId);

        /// <summary>The swapCostFor receipt a swap would be charged, or null where the engine refuses before pricing.</summary>
        JObject SwapPrice(CombatState c, string slotId, int setIndex);

        /// <summary>The swapCostFor receipt an equipment change would be charged, or null where the engine refuses before pricing.</summary>
        JObject ChangePrice(CombatState c, string slotId, int setIndex, string pieceId);
    }
}
