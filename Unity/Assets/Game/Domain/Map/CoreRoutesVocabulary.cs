using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;

namespace Ashen.Domain.Map
{
    /// <summary>
    /// The closed names the Core 2 route builder (web actmap.js buildGameProfileActMap) keeps in code, read once from
    /// rules/mapEngine.json routes: the route node types it treats specially (rest, boss, elite, shop, event), how each
    /// authored type is displayed on the map, the kind a pre-resolved node reports, the Unknown-node odds it substitutes,
    /// the boss node's id and the lane count it accepts. The route content itself (depths, rests, shares) is the profile's.
    /// </summary>
    public sealed class CoreRoutesVocabulary
    {
        public CoreRoutesVocabulary(JObject routes)
        {
            var roles = routes.Obj(MK.Roles);
            Rest = roles.Str(MK.Rest);
            Boss = roles.Str(MK.Boss);
            Elite = roles.Str(MK.Elite);
            Shop = roles.Str(MK.Shop);
            Event = roles.Str(MK.Event);
            DisplayTypes = routes.Obj(MK.DisplayTypes).Properties().ToDictionary(p => p.Name, p => Js.Str(p.Value));
            ResolvedKinds = routes.Obj(MK.ResolvedKinds).Properties().ToDictionary(p => p.Name, p => Js.Str(p.Value));
            UnknownWeights = (JObject)routes.Obj(MK.UnknownWeights).DeepClone();
            BossNodeId = routes.Str(MK.BossNodeId);
            var lanes = routes.Obj(MK.Lanes);
            LanesMin = (int)Js.D(lanes[K.Min]);
            LanesMax = (int)Js.D(lanes[K.Max]);
            LaneCentre = Js.D(routes[MK.LaneCentre]);
        }

        /// <summary>The route types that are not drawn as alternatives: a rest depth is one shared node and the boss is the terminal.</summary>
        public string Rest { get; }

        public string Boss { get; }

        /// <summary>The types the builder reads by role: elites wait for the minimum threat floor, shops take the shop share, and a hidden node is shown as an event.</summary>
        public string Elite { get; }

        public string Shop { get; }

        public string Event { get; }

        /// <summary>The map node type each authored route type is displayed as (the 405 map vocabulary).</summary>
        public IReadOnlyDictionary<string, string> DisplayTypes { get; }

        /// <summary>The kind a node reports when it is shown as an event but is not one (a shop is a merchant).</summary>
        public IReadOnlyDictionary<string, string> ResolvedKinds { get; }

        /// <summary>The Unknown-node odds the route builder rolls with: one weight, so every Unknown node is an event, but the draw is still taken.</summary>
        public JObject UnknownWeights { get; }

        public string BossNodeId { get; }

        public int LanesMin { get; }

        public int LanesMax { get; }

        /// <summary>Where a shared node (a rest depth, the boss) sits across the lanes, as a fraction of the span from the first lane to the last.</summary>
        public double LaneCentre { get; }

        /// <summary>The column of a node shared by every lane: the lane span (columns - 1) scaled by <see cref="LaneCentre"/>.</summary>
        public double Centre(int columns) => (columns - 1) * LaneCentre;

        public string DisplayType(string routeType) => routeType != null && DisplayTypes.TryGetValue(routeType, out var d) ? d : null;

        /// <summary>What a node of this route type reports when it is pre-resolved: its mapped kind, else the type itself.</summary>
        public string ResolvedKind(string routeType) => ResolvedKinds.TryGetValue(routeType, out var k) ? k : routeType;

        /// <summary>The route types an act may offer as alternatives, from a list of authored types (de-duplicated, rest and boss removed, in first-seen order).</summary>
        public List<string> Alternatives(IEnumerable<string> authored) =>
            authored.Distinct().Where(t => t != Rest && t != Boss).ToList();
    }
}
