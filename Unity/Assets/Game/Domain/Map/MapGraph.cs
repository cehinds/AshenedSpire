using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;

namespace Ashen.Domain.Map
{
    /// <summary>What an Unknown (?) node resolved to at map birth: a kind, and the event id when the kind is an event.</summary>
    public sealed class ResolvedNode
    {
        public ResolvedNode(string kind, string eventId = null)
        {
            Kind = kind;
            EventId = eventId;
        }

        public string Kind { get; }

        public string EventId { get; }

        /// <summary><c>{ kind }</c> or <c>{ kind, eventId }</c>, as the shipped resolver returns it.</summary>
        public JObject ToJson()
        {
            var o = new JObject { [K.Kind] = Kind };
            if (EventId != null) o[MK.EventId] = EventId;
            return o;
        }

        public static ResolvedNode FromJson(JObject o) => o == null ? null : new ResolvedNode(o.Str(K.Kind), o.Str(MK.EventId));
    }

    /// <summary>One map node (the shipped <c>{ id, floor, col, type, next }</c>, plus resolved / boss destination fields).</summary>
    public sealed class MapNode
    {
        public MapNode(string id, int floor, int col)
        {
            Id = id;
            Floor = floor;
            Col = col;
        }

        public string Id { get; set; }
        public int Floor { get; }
        public int Col { get; set; }

        /// <summary>The node type, or null while untyped (typing resets rollable nodes to null each attempt).</summary>
        public string Type { get; set; }

        /// <summary>Edge targets in insertion order.</summary>
        public List<string> Next { get; set; } = new List<string>();

        /// <summary>An Unknown node's pre-rolled outcome (event nodes only).</summary>
        public ResolvedNode Resolved { get; set; }

        /// <summary>A boss terminal's encounter (assignBossDestinations).</summary>
        public string EncounterId { get; set; }

        public string DestinationLabel { get; set; }

        /// <summary>The node JSON in the shipped key order: id, floor, col, type, next, then resolved or encounterId/destinationLabel.</summary>
        public JObject ToJson()
        {
            var o = new JObject
            {
                [K.Id] = Id,
                [MK.Floor] = Floor,
                [MK.Col] = Col,
                [K.Type] = Type == null ? JValue.CreateNull() : new JValue(Type),
                [MK.Next] = new JArray(Next.Select(n => (object)n).ToArray()),
            };
            if (Resolved != null) o[MK.Resolved] = Resolved.ToJson();
            if (EncounterId != null) o[MK.EncounterId] = EncounterId;
            if (DestinationLabel != null) o[MK.DestinationLabel] = DestinationLabel;
            return o;
        }

        public static MapNode FromJson(JObject o)
        {
            var node = new MapNode(o.Str(K.Id), (int)o.Num(MK.Floor), (int)o.Num(MK.Col))
            {
                Type = o.Str(K.Type),
                Next = Js.Items(o[MK.Next]).Select(Js.Str).ToList(),
                Resolved = ResolvedNode.FromJson(o.Obj(MK.Resolved)),
                EncounterId = o.Str(MK.EncounterId),
                DestinationLabel = o.Str(MK.DestinationLabel),
            };
            return node;
        }
    }

    /// <summary>
    /// An act map (the shipped mapGraph): nodes in insertion order (<c>Object.values(nodes)</c> order is semantic —
    /// the Unknown rolls walk it), the start ids, the pre-boss shrine, the boss terminal(s), and the geometry that
    /// travels with the graph.
    /// </summary>
    public sealed class ActMapGraph
    {
        public OrderedMap<MapNode> Nodes { get; } = new OrderedMap<MapNode>();

        public List<string> StartIds { get; set; } = new List<string>();

        public string ShrineId { get; set; }

        /// <summary>The first boss terminal (a compatibility alias once destinations are assigned).</summary>
        public string BossId { get; set; }

        public int Floors { get; set; }

        public int Columns { get; set; }

        /// <summary>Every boss terminal, or null on a legacy singular graph.</summary>
        public List<string> BossIds { get; set; }

        /// <summary><c>Object.values(nodes)</c>: a snapshot in insertion order.</summary>
        public List<MapNode> AllNodes() => Nodes.Entries().Select(e => e.Value).ToList();

        public MapNode Node(string id) => id != null && Nodes.TryGetValue(id, out var n) ? n : null;

        /// <summary>The graph JSON in the shipped key order: nodes, startIds, shrineId, bossId, floors, columns[, bossIds].</summary>
        public JObject ToJson()
        {
            var nodes = new JObject();
            foreach (var e in Nodes.Entries()) nodes[e.Key] = e.Value.ToJson();
            var o = new JObject
            {
                [MK.Nodes] = nodes,
                [MK.StartIds] = new JArray(StartIds.Select(s => (object)s).ToArray()),
                [MK.ShrineId] = ShrineId,
                [MK.BossId] = BossId,
                [MK.Floors] = Floors,
                [MK.Columns] = Columns,
            };
            if (BossIds != null) o[MK.BossIds] = new JArray(BossIds.Select(s => (object)s).ToArray());
            return o;
        }

        /// <summary>Reads a saved graph (current or legacy singular) back.</summary>
        public static ActMapGraph FromJson(JObject o)
        {
            var g = new ActMapGraph
            {
                StartIds = Js.Items(o[MK.StartIds]).Select(Js.Str).ToList(),
                ShrineId = o.Str(MK.ShrineId),
                BossId = o.Str(MK.BossId),
                Floors = (int)Js.Coalesce(o[MK.Floors], 0),
                Columns = (int)Js.Coalesce(o[MK.Columns], 0),
                BossIds = o[MK.BossIds] is JArray ids ? ids.Select(Js.Str).ToList() : null,
            };
            if (o.Obj(MK.Nodes) is JObject nodes)
                foreach (var p in nodes.Properties())
                    if (p.Value is JObject node) g.Nodes[p.Name] = MapNode.FromJson(node);
            return g;
        }
    }
}
