using System.Collections.Generic;
using System.Linq;
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

        /// <summary>web <c>ctx._reactionPlaying</c>: a reaction card is being played (an offer's candidate or the committed card); it plays off the player's turn and opens no window.</summary>
        public bool ReactionPlaying;

        /// <summary>The queue a reaction card interrupted (web <c>reactionResume.queue</c>); the state holds only its length, the actions are runtime objects.</summary>
        public List<CombatAction> ReactionResumeQueue;

        /// <summary>web <c>ctx._groupSink</c>: the committed actions a reaction candidate planned, for the incoming damage it would do to each enemy.</summary>
        public List<ExpansionGroup> GroupSink;

        /// <summary>The play a player-card root deferred while its reaction window waits (web <c>reactionRootPlay</c> keeps the pending play; the runtime refs of the card ride here).</summary>
        public JObject StashedPlayInstance;
        public JObject StashedPlayRef;
        public JObject StashedPlayBefore;

        /// <summary>web <c>ctx.enqueue</c> swapped for a sink while an enemy's reaction is planned: what the planner queues is kept as the plan instead of being run.</summary>
        public List<CombatAction> EnqueueSink;

        /// <summary>The committed plans of the enemy entries on the reaction stack, by <c>ownerId:groupKey</c> (the state holds a compact view of each).</summary>
        public Dictionary<string, EnemyPlan> EnemyPlans = new Dictionary<string, EnemyPlan>();

        /// <summary>web <c>ctx._rangeContext</c>: the incoming attack's quote a reply nested in it prices its reach against.</summary>
        public JObject RangeContext;

        /// <summary>web <c>ctx._rangeIncoming</c>: which contact of which incoming root the reaction being played answers.</summary>
        public JObject RangeIncoming;

        /// <summary>web <c>payload.rangeLifecycleCarrier</c>: the carrier of a card whose <c>cardResolved</c> is being told after its play left the pending slot.</summary>
        public JObject RangeLifecycleCarrier;

        private static JObject Copy(JObject o) => (JObject)o?.DeepClone();

        private static JToken Copy(JToken t) => t?.DeepClone();

        /// <summary>
        /// web <c>candidateState(ctx, { detached: true })</c>: a private copy of the fight to play a reaction card on and throw away. Every document, pile,
        /// gate and mount is copied, the RNG continues from the same counters, the event log starts empty and the queue holds nothing, so what the copy does
        /// can neither move the fight nor draw from it.
        /// </summary>
        public CombatState Detach()
        {
            var copy = new CombatState
            {
                Data = Data,
                Rng = Rng.Clone(),
                RatingsRules = Copy(RatingsRules),
                HandRules = Copy(HandRules),
                PendingDiscardDraw = PendingDiscardDraw,
                EquipmentProfileRuleSnapshot = Copy(EquipmentProfileRuleSnapshot),
                EquipmentAttackSlotCount = Copy(EquipmentAttackSlotCount),
                RemovedAttackSlotIds = Copy(RemovedAttackSlotIds),
                ItemUpgradeLevels = Copy(ItemUpgradeLevels),
                ItemMounts = Copy(ItemMounts),
                EquipmentPoolDeficits = Copy(EquipmentPoolDeficits),
                EquipmentChanged = EquipmentChanged,
                Turn = Turn,
                Phase = Phase,
                Result = Result,
                HandMax = HandMax,
                DrawPerTurn = Copy(DrawPerTurn),
                Player = Copy(Player),
                Enemies = Enemies.Select(enemy => Copy(enemy)).ToList(),
                Loadout = Copy(Loadout),
                Attributes = Copy(Attributes),
                DerivedStatRuleSnapshot = Copy(DerivedStatRuleSnapshot),
                SwapCostRule = Copy(SwapCostRule),
                SwapsLeft = SwapsLeft,
                IdCounter = IdCounter,
                EmitDepth = EmitDepth,
                Skills = Copy(Skills),
                SkillXp = Copy(SkillXp),
                CoreTags = (JArray)CoreTags.DeepClone(),
                CoreData = CoreData,
                Fields = Copy(Fields),
                RecordSkillXp = RecordSkillXp,
                PendingAbilityPlay = Copy(PendingAbilityPlay),
                PendingPlayInstance = Copy(PendingPlayInstance),
                PendingPlayRef = Copy(PendingPlayRef),
                PendingPlayBefore = Copy(PendingPlayBefore),
                HealMult = HealMult,
                RunOpcodes = RunOpcodes,
                RangeContext = Copy(RangeContext),
                RangeIncoming = Copy(RangeIncoming),
            };
            foreach (var plan in EnemyPlans) copy.EnemyPlans[plan.Key] = plan.Value.CloneDeep();
            copy.Piles.Draw = Piles.Draw.Select(card => Copy(card)).ToList();
            copy.Piles.Hand = Piles.Hand.Select(card => Copy(card)).ToList();
            copy.Piles.Discard = Piles.Discard.Select(card => Copy(card)).ToList();
            copy.Piles.Exhaust = Piles.Exhaust.Select(card => Copy(card)).ToList();
            foreach (var gate in TriggerState.Entries()) copy.TriggerState[gate.Key] = new TriggerGate { Fires = gate.Value.Fires, Turn = gate.Value.Turn, TurnFires = gate.Value.TurnFires };
            if (PropertyMounts != null)
            {
                copy.PropertyMounts = new OrderedMap<OrderedMap<PropertyMount>>();
                foreach (var owner in PropertyMounts.Entries())
                {
                    var sources = new OrderedMap<PropertyMount>();
                    foreach (var source in owner.Value.Entries())
                        sources[source.Key] = new PropertyMount { Kind = source.Value.Kind, Id = source.Value.Id, InstanceId = source.Value.InstanceId, Rules = source.Value.Rules.Select(rule => Copy(rule)).ToList(), ScopeTags = source.Value.ScopeTags?.ToList(),
                            MechanicTags = (JArray)source.Value.MechanicTags.DeepClone(), MechanicBindings = (JArray)source.Value.MechanicBindings.DeepClone(), ConfiguredEnemy = source.Value.ConfiguredEnemy };
                    copy.PropertyMounts[owner.Key] = sources;
                }
            }
            return copy;
        }
    }
}
