using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Ashen.Domain.Combat
{
    /// <summary>A planned contact: the target row it belongs to, its place in that row and, for a critical hit, its multiplier (web expansionContacts entries).</summary>
    public sealed class ContactRef
    {
        public string Key;
        public int Index;
        public string Id;
        public double? Crit;

        public ContactRef Copy() => (ContactRef)MemberwiseClone();
    }

    /// <summary>One defender of a committed action: its planned contacts, the receipt the action's begin step wrote and the interactions the status rules read.</summary>
    public sealed class TargetRow
    {
        public string Id;
        public List<JObject> Contacts = new List<JObject>();
        public JObject Receipt;
        public JObject Interactions;
    }

    /// <summary>The per-hit contacts of a committed action (web group.contactSequence): each hit has its own targets and receipt.</summary>
    public sealed class ContactSequence
    {
        public string RootKey;
        public int Index = -1;
        public List<OrderedMap<TargetRow>> Contacts = new List<OrderedMap<TargetRow>>();
        public OrderedMap<TargetRow> RootTargets;
        public Dictionary<int, bool> Gates = new Dictionary<int, bool>();
        public JObject ProposalTargetDecisions;
    }

    /// <summary>
    /// A committed card or move (web engine/combatExpansionActions.js group): one receipt per defender across all its contacts. It is runtime
    /// state, never saved; a fight in progress holds it on the queued actions that name it.
    /// </summary>
    public sealed partial class ExpansionGroup
    {
        public string Key;
        public string SourceId;
        public JObject Carrier;
        public OrderedMap<TargetRow> Targets = new OrderedMap<TargetRow>();
        public bool Begun;
        public bool Cancelled;
        public bool PayloadStarted;
        public bool ContactTelegraphed;
        public JObject EnemyMoveStart;
        public ContactSequence ContactSequence;
        public List<string> ReactionAnswered;
        public List<string> ReactionNoticed;
    }
}
