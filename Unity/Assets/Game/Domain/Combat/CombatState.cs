using System.Collections.Generic;
using Ashen.Domain.Random;
using Newtonsoft.Json.Linq;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>The four card piles (instances are JSON documents, as saved).</summary>
    public sealed class Piles
    {
        public List<JObject> Draw = new List<JObject>();
        public List<JObject> Hand = new List<JObject>();
        public List<JObject> Discard = new List<JObject>();
        public List<JObject> Exhaust = new List<JObject>();
    }

    /// <summary>A trigger gate's counters (shipped triggerState entry: once / limitPerTurn bookkeeping).</summary>
    public sealed class TriggerGate
    {
        public double Fires;
        public double Turn = -1;
        public double TurnFires;
    }

    /// <summary>A mounted property carrier (engine/properties.js mount record).</summary>
    public sealed class PropertyMount
    {
        public string Kind;
        public string Id;
        public string InstanceId;
        public List<JObject> Rules;
        public List<string> ScopeTags;
    }

    /// <summary>What caused a queued action (shipped action.meta).</summary>
    public sealed class ActionMeta
    {
        public double? EnergySpent;
        public double? ManaSpent;
        public double? StaminaSpent;
        public double? OrdinalThisTurn;
        public double? OrdinalThisCombat;
        public double? AttackOrdinal;
        public string MoveId;
        public double? AmountMult;
        public JObject Event;

        public static ActionMeta Empty() => new ActionMeta();
    }

    /// <summary>A queued action: one effect opcode plus its context (shipped { effect, source, owner, target, card, meta }).</summary>
    public sealed class CombatAction
    {
        public JObject Effect;
        public JObject Source;
        public JObject Owner;
        public JObject Target;
        public JObject Card;
        public ActionMeta Meta;
    }

    /// <summary>
    /// A live combat (shipped engine/combat.js combat object). Entities, card instances and rule snapshots are JSON
    /// documents in the shipped snapshot format (D-037); the queue, trigger gates and property mounts are runtime
    /// structures, re-derived on restore exactly as the shipped engine re-derives them.
    /// </summary>
    public sealed class CombatState
    {
        public CombatData Data;
        public Rng Rng;

        public JObject RatingsRules;
        public JObject HandRules;
        public double PendingDiscardDraw;

        public JObject EquipmentProfileRuleSnapshot;
        public JToken EquipmentAttackSlotCount;
        public JToken RemovedAttackSlotIds;
        public JObject ItemUpgradeLevels;
        public JToken ItemMounts;
        public JObject EquipmentPoolDeficits;
        public bool EquipmentChanged;

        public double Turn;
        public string Phase;
        public string Result;
        public double HandMax;
        public JToken DrawPerTurn;

        public JObject Player;
        public List<JObject> Enemies = new List<JObject>();
        public JObject Loadout;
        public JObject Attributes;
        public JToken DerivedStatRuleSnapshot;
        public JToken SwapCostRule;
        public double SwapsLeft;

        public Piles Piles = new Piles();
        public readonly LinkedList<CombatAction> Queue = new LinkedList<CombatAction>();
        public List<JObject> EventLog = new List<JObject>();

        /// <summary>The events emitted by the dispatch in progress; null between dispatches.</summary>
        public List<JObject> Buffer;

        public OrderedMap<TriggerGate> TriggerState = new OrderedMap<TriggerGate>();
        public double IdCounter;
        public double EmitDepth;

        public JObject Skills = new JObject();
        public JObject SkillXp = new JObject();
        public JArray CoreTags = new JArray();

        /// <summary>ownerKey → (sourceKey → mount), both insertion-ordered.</summary>
        public OrderedMap<OrderedMap<PropertyMount>> PropertyMounts;

        /// <summary>combat.emit: the bus (triggers) then the skill-XP listener, as the shipped attachSkillXp wraps it.</summary>
        public JObject Emit(string type, JObject payload = null)
        {
            var ev = Triggers.EmitEvent(this, type, payload);
            Ashen.Domain.Combat.SkillXp.Record(this, ev);
            return ev;
        }

        public void Enqueue(CombatAction action) => Queue.AddLast(action);

        public string NextInstanceId()
        {
            IdCounter += 1;
            return V.GeneratedIdPrefix + IdCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
