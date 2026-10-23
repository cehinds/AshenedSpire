using Newtonsoft.Json.Linq;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The fields of an action's metadata that the Core 2 statuses, status control and effect ops add (web <c>action.meta</c>). Kept in its own part of
    /// the class so the legacy fields stay where they were.
    /// </summary>
    public sealed partial class ActionMeta
    {
        /// <summary>meta.killReceipt: what a kill by a damage-over-time effect is credited with (the statuses the target held before, the source).</summary>
        public JObject KillReceipt;

        /// <summary>meta.choice: the pick a played card was paid with (a chosen stance or maneuver).</summary>
        public JToken Choice;

        /// <summary>meta.linkedDepth: how many linked spells deep this effect is.</summary>
        public int LinkedDepth;

        /// <summary>meta.abilityChargeDamage and its siblings: the riders a consumed card charge attached to this effect.</summary>
        public JObject Charge;

        /// <summary>meta.cardTargetsAllEnemies, abilityBefore and abilityEntities: the snapshot taken before the play.</summary>
        public JObject Before;

        /// <summary>meta.targetId, set on the action of a kill receipt.</summary>
        public string TargetId;

        /// <summary>meta.expansionExecutionRepeat: which repeat of its effect the action is on (the plan's target batch it reads).</summary>
        public int? ExpansionExecutionRepeat;

        /// <summary>
        /// The contact of the hit a status rule's follow-up (a reflect, an automatic reaction) answers: the web copies the hit's carrier, its
        /// <c>expansionContact</c> included, so the follow-up is not rolled again and settles into the same receipt.
        /// </summary>
        public CoreImpact.Contact InheritedContact;

        /// <summary>meta.cardInstanceId, cardId and trigger: the card whose turn-end-in-hand effect this is.</summary>
        public string CardInstanceId;
        public string CardId;
        public string Trigger;

        /// <summary>meta.triggeredBy: the status, stance, property or relic whose trigger enqueued the effect (<c>{ kind, id, ownerId }</c>), for the combat log.</summary>
        public JObject TriggeredBy;

        /// <summary>meta.stanceRestore: the effect restores a stance's protection (a whole, unmodified amount).</summary>
        public bool StanceRestore;
    }

    /// <summary>The runtime state a committed action's group keeps for the Core 2 card traits (web group.ownPressure).</summary>
    public sealed partial class ExpansionGroup
    {
        /// <summary>The Poise and Ward the card's own effects dealt to each target, by target id (<c>{ id, poiseDealt, wardDealt }</c>), for Impact and Flux.</summary>
        public JObject OwnPressure;
    }
}
