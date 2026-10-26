using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using MM = Ashen.Generated.MapMessages;

namespace Ashen.Domain.Map
{
    /// <summary>
    /// The Core 2 act map (web engine/actmap.js buildActMap for a profile whose routes module is configured, i.e.
    /// buildGameProfileActMap): a fixed number of pre-boss depths in a fixed number of lanes, one shared node on each
    /// mandatory rest depth, an authored opening type, shops at an authored share of the randomly typed nodes, fog that
    /// shows hidden types as events, one boss picked from the seat's pool, and the profile hash stamped on the graph.
    /// Everything it decides is the profile's routes content; the names it keeps in code are rules/mapEngine.json routes
    /// (<see cref="CoreRoutesVocabulary"/>). Draw order is the web's, so every seed replays: the 'map' stream types the
    /// nodes (a pick, then for a non-rest, non-opening, non-elite node a shop chance and, unless that makes a shop, a
    /// second pick), each shown-as-event node rolls the 'events' stream, and the boss is the last 'map' pick.
    /// </summary>
    public static class CoreRoutes
    {
        /// <summary>buildActMap(registries, rng, seat, tier, null, { history }) under a configured routes module.</summary>
        public static ActMapGraph BuildActMap(MapData data, CoreRoutesConfig config, Rng rng, string seat, int tier, JArray history = null)
        {
            if (string.IsNullOrEmpty(seat)) throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, MM.BuildSeatRequired, seat), nameof(seat));
            if (tier < 1) throw new ArgumentOutOfRangeException(nameof(tier), string.Format(CultureInfo.InvariantCulture, MM.BuildTierInvalid, tier));
            data.Seats.Get(seat);
            return Build(data, config, rng, seat, tier, history ?? new JArray());
        }

        private static ActMapGraph Build(MapData data, CoreRoutesConfig config, Rng rng, string seat, int tier, JArray history)
        {
            var vocab = data.Rules.Routes ?? throw new InvalidOperationException(MM.RoutesNoVocabulary);
            var rules = config.Content;
            var actsToken = rules?[MK.Acts];
            if (rules == null || (Js.IsNum(actsToken) && tier > Js.D(actsToken))) throw new InvalidOperationException(MM.RoutesTierRange);

            var countToken = rules[MK.PrebossNodes];
            var restsToken = rules[MK.RestsPerAct];
            if (!Js.IsInt(countToken) || Js.D(countToken) < 1 || (Js.IsNum(restsToken) && Js.D(countToken) < Js.D(restsToken)))
                throw new InvalidOperationException(MM.RoutesLength);
            var count = (int)Js.D(countToken);

            var columnsToken = rules[MK.BranchColumns];
            if (!Js.IsInt(columnsToken) || Js.D(columnsToken) < vocab.LanesMin || Js.D(columnsToken) > vocab.LanesMax)
                throw new InvalidOperationException(MM.RoutesColumns);
            var columns = (int)Js.D(columnsToken);

            var threatToken = rules[MK.MinimumThreatFloor];
            if (!Js.IsInt(threatToken) || Js.D(threatToken) < 1 || count + 1 < Js.D(threatToken))
                throw new InvalidOperationException(MM.RoutesThreatFloor);
            var minimumThreatFloor = (int)Js.D(threatToken);

            if (!(rules[MK.RestFloors] is JArray restFloorTokens)) throw new InvalidOperationException(MM.RoutesRestFloorsMissing);
            if (!Js.IsNum(restsToken) || restFloorTokens.Count != Js.D(restsToken)
                || restFloorTokens.Select(Js.D).Distinct().Count() != restFloorTokens.Count
                || restFloorTokens.Any(t => !Js.IsInt(t) || Js.D(t) < 1 || Js.D(t) > count))
                throw new InvalidOperationException(MM.RoutesRestFloors);
            var restFloors = restFloorTokens.Select(t => (int)Js.D(t)).ToList();
            if (Js.Truthy(rules[MK.RequirePrebossRest]) && !restFloors.Contains(count)) throw new InvalidOperationException(MM.RoutesPrebossRest);

            var firstFloor = Js.Nullish(rules[MK.FirstFloor]) ? null : Js.Str(rules[MK.FirstFloor]);
            var hasShare = !Js.Nullish(rules[MK.ShopShare]);
            var shopShare = hasShare ? Js.D(rules[MK.ShopShare]) : 0d;
            if (hasShare && !(Js.IsNum(rules[MK.ShopShare]) && shopShare >= 0 && shopShare <= 1)) throw new InvalidOperationException(MM.RoutesShopShare);

            var hidden = new HashSet<string>(config.HiddenTypes);
            var alternatives = vocab.Alternatives(config.HiddenTypes.Concat(config.VisibleTypes));
            if (alternatives.Count == 0 || alternatives.Any(t => vocab.DisplayType(t) == null)) throw new InvalidOperationException(MM.RoutesNodeTypes);
            if (firstFloor != null && (!alternatives.Contains(firstFloor) || restFloors.Contains(1) || (firstFloor == vocab.Elite && minimumThreatFloor > 1)))
                throw new InvalidOperationException(MM.RoutesFirstFloor);

            var mapRules = data.Rules;
            var hideUnknown = config.HideUnknownNodes;
            var graph = new ActMapGraph();
            var layers = new List<List<string>>();
            var seenEvents = new List<string>();
            var fogType = mapRules.Event;
            for (var floor = 1; floor <= count; floor++)
            {
                var rest = restFloors.Contains(floor);
                var layer = new List<string>();
                var depth = floor;
                var choices = alternatives.Where(t => t != vocab.Elite || depth >= minimumThreatFloor).ToList();
                if (!rest && choices.Count == 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.RoutesNoChoices, floor));
                var lanes = rest ? 1 : columns;
                for (var lane = 0; lane < lanes; lane++)
                {
                    var id = mapRules.NodeId(floor, lane);
                    var forced = !rest && floor == 1 && firstFloor != null;
                    var actual = rest ? vocab.Rest : forced ? firstFloor : rng.Pick(RngStream.Map, choices);
                    if (!rest && !forced && hasShare && alternatives.Contains(vocab.Shop) && actual != vocab.Elite)
                    {
                        var plain = alternatives.Where(t => t != vocab.Elite && t != vocab.Shop).ToList();
                        var shop = rng.Float(RngStream.Map) * RngAlgorithm.PercentScale < shopShare * RngAlgorithm.PercentScale;
                        actual = shop || plain.Count == 0 ? vocab.Shop : rng.Pick(RngStream.Map, plain);
                    }
                    var type = hideUnknown && hidden.Contains(actual) && !forced ? fogType : vocab.DisplayType(actual);
                    var node = new MapNode(id, floor, rest ? vocab.Centre(columns) : lane) { Type = type, ActualType = actual };
                    if (type == mapRules.Event)
                    {
                        node.Resolved = actual == vocab.Event
                            ? MapEncounters.ResolveUnknownNode(data, rng, seenEvents, vocab.UnknownWeights, history)
                            : new ResolvedNode(vocab.ResolvedKind(actual));
                        if (node.Resolved.Kind == mapRules.UnknownEvent) seenEvents.Add(node.Resolved.EventId);
                    }
                    graph.Nodes[id] = node;
                    layer.Add(id);
                }
                layers.Add(layer);
            }

            var finalTier = actsToken == null ? 0 : (int)Js.D(actsToken);
            var pool = data.Encounters.All.Where(e => e.Str(MK.Pool) == mapRules.BossPool && MapSeats.EncounterFitsSeat(e, seat, tier, finalTier)).ToList();
            if (pool.Count == 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.RoutesNoBoss, seat, tier));
            var boss = rng.Pick(RngStream.Map, pool);
            var bossId = boss.Str(K.Id);
            graph.Nodes[vocab.BossNodeId] = new MapNode(vocab.BossNodeId, count + 1, vocab.Centre(columns))
            {
                Type = vocab.DisplayType(vocab.Boss),
                ActualType = vocab.Boss,
                EncounterId = bossId,
                Label = MapSeats.BossDestinationLabel(data, bossId),
            };
            layers.Add(new List<string> { vocab.BossNodeId });

            for (var depth = 0; depth < layers.Count - 1; depth++)
            {
                var current = layers[depth];
                var next = layers[depth + 1];
                foreach (var id in current)
                {
                    var from = graph.Nodes[id];
                    from.Next = next.Where(target => current.Count == 1 || next.Count == 1 || Math.Abs(from.Col - graph.Nodes[target].Col) <= 1).ToList();
                }
            }

            graph.StartIds = layers[0];
            graph.ShrineId = restFloors.Count > 0 ? mapRules.NodeId(restFloors.Max(), 0) : null;
            graph.BossId = vocab.BossNodeId;
            graph.BossIds = new List<string> { vocab.BossNodeId };
            graph.Floors = count + 1;
            graph.Columns = columns;
            graph.GameProfileHash = config.ProfileHash;
            return graph;
        }
    }
}
