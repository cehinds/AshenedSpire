using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;

namespace Ashen.App.Run
{
    /// <summary>
    /// What the fog of war lights on an act map (web model/map/mapknowledge.js litNodes, nearestShrine; D-901). The set is derived from the run's path alone, so it only grows:
    /// the trail (every node walked and the split in front of it, which keeps the cone behind the frontier known), where the run stands and its split, the doors (every entrance),
    /// the end (the boss) and, for every shrine stood on, the nearest shrine ahead of it. Engine-free.
    /// </summary>
    public static class MapFog
    {
        /// <summary>litNodes({ graph, run }): the ids the fog does not cover. <paramref name="graph"/> is the run's map graph; <paramref name="path"/> its walked nodes; <paramref name="at"/> where it stands (or null).</summary>
        public static HashSet<string> Lit(JObject graph, IEnumerable<string> path, string at)
        {
            var lit = new HashSet<string>(StringComparer.Ordinal);
            var nodes = graph?.Obj(MK.Nodes);
            if (nodes == null) return lit;
            void Add(string id)
            {
                if (id != null && nodes[id] != null) lit.Add(id);
            }
            void Shine(string id)
            {
                var node = id == null ? null : nodes.Obj(id);
                if (node == null) return;
                lit.Add(id);
                foreach (var next in Js.Items(node[MK.Next]).Select(Js.Str)) Add(next);
            }
            void ShrineAhead(string id)
            {
                var node = id == null ? null : nodes.Obj(id);
                if (node == null || node.Str(K.Type) != MK.Shrine) return;
                var found = NearestShrine(nodes, new[] { id });
                if (found != null) Add(found);
            }
            foreach (var id in Js.Items(graph[MK.StartIds]).Select(Js.Str)) Add(id);
            var bosses = graph[MK.BossIds] is JArray ids ? Js.Items(ids).Select(Js.Str) : nodes.Properties().Where(p => Js.Str(p.Value[K.Type]) == MK.Boss).Select(p => p.Name);
            foreach (var id in bosses) Add(id);
            Add(Js.Str(graph[MK.BossId]));
            foreach (var id in path ?? Enumerable.Empty<string>())
            {
                Shine(id);
                ShrineAhead(id);
            }
            if (at != null)
            {
                Shine(at);
                ShrineAhead(at);
            }
            return lit;
        }

        /// <summary>
        /// nearestShrine({ graph, from }): the shrine nearest ahead of the sources, walking the one-way edges breadth first (a source is never its own answer); ties go to the smaller id and
        /// every frontier is walked in sorted order, so the answer does not depend on key order. Null when no shrine lies ahead.
        /// </summary>
        public static string NearestShrine(JObject nodes, IEnumerable<string> from)
        {
            var seen = new HashSet<string>(from.Where(id => id != null && nodes[id] != null), StringComparer.Ordinal);
            var frontier = seen.OrderBy(id => id, StringComparer.Ordinal).ToList();
            while (frontier.Count > 0)
            {
                var step = new List<string>();
                foreach (var id in frontier)
                    foreach (var to in Js.Items(nodes.Obj(id)[MK.Next]).Select(Js.Str))
                    {
                        if (to == null || nodes[to] == null || !seen.Add(to)) continue;
                        step.Add(to);
                    }
                step.Sort(StringComparer.Ordinal);
                var hit = step.FirstOrDefault(id => nodes.Obj(id).Str(K.Type) == MK.Shrine);
                if (hit != null) return hit;
                frontier = step;
            }
            return null;
        }
    }
}
