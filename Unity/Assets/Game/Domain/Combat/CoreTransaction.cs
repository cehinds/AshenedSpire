using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// A refused intent changes nothing (web engine/combat.js dispatch runs a Core fight's intent on a candidate copy inside foundationTransaction and adopts it only when it succeeded; WP-C0 D-294).
    /// <see cref="Take"/> holds the fight as it stands when an intent arrives; <see cref="Restore"/> puts every document, pile, gate, mount, plan and RNG counter back, and cuts the events the
    /// refused intent emitted from the log.
    /// </summary>
    public sealed class CoreTransaction
    {
        private CombatState _copy;
        private int _events;
        private List<JObject> _queue, _resume;
        private bool _reactionPlaying;
        private JObject _stashedInstance, _stashedRef, _stashedBefore;

        public static CoreTransaction Take(CombatState c) => new CoreTransaction
        {
            _copy = c.Detach(),
            _events = c.EventLog.Count,
            _queue = c.Queue.Select(a => CoreSnapshotActions.ActionToJson(a)).ToList(),
            _resume = c.ReactionResumeQueue?.Select(a => CoreSnapshotActions.ActionToJson(a)).ToList(),
            _reactionPlaying = c.ReactionPlaying,
            _stashedInstance = (JObject)c.StashedPlayInstance?.DeepClone(),
            _stashedRef = (JObject)c.StashedPlayRef?.DeepClone(),
            _stashedBefore = (JObject)c.StashedPlayBefore?.DeepClone(),
        };

        /// <summary>
        /// A detached copy of a fight that is paused (on an offer or a choice) keeps the work the pause suspended — the queued actions and the reaction resume queue, against
        /// the copy's own creatures — so an answer can be tried on it as on the fight itself (web <c>candidateState</c> carries the queue).
        /// </summary>
        public static CombatState DetachPaused(CombatState c)
        {
            var copy = c.Detach();
            var groups = new Dictionary<string, ExpansionGroup>(System.StringComparer.Ordinal);
            foreach (var action in c.Queue.Select(a => CoreSnapshotActions.ActionToJson(a)).ToList()) copy.Queue.AddLast(CoreSnapshotActions.ActionFromJson(copy, action, groups));
            copy.ReactionResumeQueue = c.ReactionResumeQueue?.Select(a => CoreSnapshotActions.ActionFromJson(copy, CoreSnapshotActions.ActionToJson(a), groups)).ToList();
            copy.ReactionPlaying = c.ReactionPlaying;
            copy.StashedPlayInstance = (JObject)c.StashedPlayInstance?.DeepClone();
            copy.StashedPlayRef = (JObject)c.StashedPlayRef?.DeepClone();
            copy.StashedPlayBefore = (JObject)c.StashedPlayBefore?.DeepClone();
            return copy;
        }

        public void Restore(CombatState c)
        {
            var s = _copy;
            c.Rng.RestoreCounters(s.Rng);
            c.RatingsRules = s.RatingsRules; c.HandRules = s.HandRules; c.PendingDiscardDraw = s.PendingDiscardDraw;
            c.EquipmentProfileRuleSnapshot = s.EquipmentProfileRuleSnapshot; c.EquipmentAttackSlotCount = s.EquipmentAttackSlotCount; c.RemovedAttackSlotIds = s.RemovedAttackSlotIds;
            c.ItemUpgradeLevels = s.ItemUpgradeLevels; c.ItemMounts = s.ItemMounts; c.EquipmentPoolDeficits = s.EquipmentPoolDeficits; c.EquipmentChanged = s.EquipmentChanged;
            c.Turn = s.Turn; c.Phase = s.Phase; c.Result = s.Result; c.HandMax = s.HandMax; c.DrawPerTurn = s.DrawPerTurn;
            c.Player = s.Player; c.Enemies = s.Enemies; c.Loadout = s.Loadout; c.Attributes = s.Attributes; c.DerivedStatRuleSnapshot = s.DerivedStatRuleSnapshot;
            c.SwapCostRule = s.SwapCostRule; c.SwapsLeft = s.SwapsLeft; c.IdCounter = s.IdCounter; c.EmitDepth = s.EmitDepth;
            c.Skills = s.Skills; c.SkillXp = s.SkillXp; c.CoreTags = s.CoreTags; c.Fields = s.Fields; c.RecordSkillXp = s.RecordSkillXp;
            c.PendingAbilityPlay = s.PendingAbilityPlay; c.PendingPlayInstance = s.PendingPlayInstance; c.PendingPlayRef = s.PendingPlayRef; c.PendingPlayBefore = s.PendingPlayBefore;
            c.HealMult = s.HealMult; c.RangeContext = s.RangeContext; c.RangeIncoming = s.RangeIncoming;
            c.Piles = s.Piles; c.TriggerState = s.TriggerState; c.PropertyMounts = s.PropertyMounts;
            c.EnemyPlans = s.EnemyPlans;
            // The work a pause suspended (an incoming attack, the rest of a card's effects) is put back against the restored creatures, joined to its groups by key as a save joins them.
            var groups = new Dictionary<string, ExpansionGroup>(System.StringComparer.Ordinal);
            c.Queue.Clear();
            foreach (var action in _queue) c.Queue.AddLast(CoreSnapshotActions.ActionFromJson(c, action, groups));
            if (c.EventLog.Count > _events) c.EventLog.RemoveRange(_events, c.EventLog.Count - _events);
            c.ReactionResumeQueue = _resume?.Select(a => CoreSnapshotActions.ActionFromJson(c, a, groups)).ToList();
            c.ReactionPlaying = _reactionPlaying;
            c.StashedPlayInstance = _stashedInstance; c.StashedPlayRef = _stashedRef; c.StashedPlayBefore = _stashedBefore;
            c.CurrentAction = null; c.EnqueueSink = null; c.GroupSink = null; c.RangeLifecycleCarrier = null; c.Buffer = null;
        }
    }
}
